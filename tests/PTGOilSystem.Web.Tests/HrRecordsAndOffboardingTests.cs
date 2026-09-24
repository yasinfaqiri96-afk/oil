using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Employees;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Exceptions;
using PTGOilSystem.Web.Services.HumanResources;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>مدیریت بشری، فاز ۳، ۱۰ و ۱۱ — پروفایل، اسناد و پایانِ همکاری.</summary>
public class HrRecordsAndOffboardingTests
{
    [Fact]
    public async Task Document_Is_Stored_Outside_Wwwroot_And_Replacement_Keeps_History()
    {
        await using var db = await SeedAsync();
        var environment = new HrContractsAndCompensationTests.TestEnvironment();
        var records = Records(db, environment);

        var first = await records.UploadDocumentAsync(new(1, EmployeeDocumentType.Tazkira, "Tazkira", null, null, null, File("a.pdf")), null);
        var attachment = await db.HrAttachments.SingleAsync(a => a.Id == first.AttachmentId);
        var path = new HrFileStorage(db, environment).ResolvePath(attachment);
        Assert.NotNull(path);
        Assert.StartsWith(Path.Combine(environment.ContentRootPath, "App_Data"), path);
        Assert.DoesNotContain("wwwroot", path);

        var second = await records.UploadDocumentAsync(new(1, EmployeeDocumentType.Tazkira, "Tazkira renewed", null, null, null, File("b.pdf")), first.Id);

        var old = await db.EmployeeDocuments.SingleAsync(d => d.Id == first.Id);
        Assert.True(old.IsDeleted);
        Assert.Equal(second.Id, old.ReplacedByDocumentId);
        Assert.Equal(2, await db.EmployeeDocuments.CountAsync());
        Assert.NotNull(new HrFileStorage(db, environment).ResolvePath(attachment)); // فایلِ قبلی پاک نشده
    }

    [Fact]
    public async Task Document_Delete_Is_Soft_And_Requires_Reason()
    {
        await using var db = await SeedAsync();
        var records = Records(db, new HrContractsAndCompensationTests.TestEnvironment());
        var document = await records.UploadDocumentAsync(new(1, EmployeeDocumentType.Cv, null, null, null, null, File("cv.docx")), null);

        Assert.Equal("HR_DOCUMENT_DELETE_REASON", (await Assert.ThrowsAsync<BusinessRuleException>(
            () => records.DeleteDocumentAsync(document.Id, " "))).Code);
        await records.DeleteDocumentAsync(document.Id, "Wrong person");

        var saved = await db.EmployeeDocuments.SingleAsync();
        Assert.True(saved.IsDeleted);
        Assert.Equal("Wrong person", saved.DeletedReason);
        Assert.Equal("cv", saved.Title);
    }

    [Fact]
    public async Task Termination_Requires_Acknowledgement_When_Money_Is_Open()
    {
        await using var db = await SeedAsync();
        await HrSalaryIntegrityTests.BuildService(db, pilot: false).CreateAsync(new(1, new DateTime(2026, 6, 5), EmployeeSalaryTransactionType.SalaryAdvance, 100m, "USD", null, 1, null, null, null, null));
        var records = Records(db, new HrContractsAndCompensationTests.TestEnvironment());

        var checklist = await records.GetOffboardingChecklistAsync(1);
        Assert.Contains(checklist.OutstandingAdvances, a => a.Currency == "USD" && a.Amount == 100m);

        Assert.Equal("HR_TERMINATION_OPEN_ITEMS", (await Assert.ThrowsAsync<BusinessRuleException>(
            () => records.TerminateAsync(1, new DateTime(2026, 6, 20), "Resigned", null, acknowledgeOpenItems: false))).Code);
        Assert.True((await db.Employees.SingleAsync()).IsActive);

        await records.TerminateAsync(1, new DateTime(2026, 6, 20), "Resigned", "Laptop returned", acknowledgeOpenItems: true);
        var employee = await db.Employees.SingleAsync();
        Assert.False(employee.IsActive);
        Assert.Equal(new DateTime(2026, 6, 20), employee.EndDate);
        Assert.Equal("Resigned", employee.TerminationReason);
        Assert.Single(await db.EmployeeSalaryTransactions.ToListAsync()); // هیچ سابقه‌ای پاک نشد

        Assert.Equal("HR_ALREADY_TERMINATED", (await Assert.ThrowsAsync<BusinessRuleException>(
            () => records.TerminateAsync(1, new DateTime(2026, 6, 21), "Again", null, true))).Code);
    }

    [Fact]
    public async Task Termination_Ends_Active_Contract_And_Disables_Only_Linked_User()
    {
        await using var db = await SeedAsync();
        db.Users.Add(new User { Id = 50, Username = "ahmad", FullName = "Ahmad", PasswordHash = "x", IsActive = true });
        db.Users.Add(new User { Id = 51, Username = "other", FullName = "Other", PasswordHash = "x", IsActive = true });
        await db.SaveChangesAsync();
        var employee = await db.Employees.SingleAsync();
        employee.UserId = 50;
        await db.SaveChangesAsync();
        var environment = new HrContractsAndCompensationTests.TestEnvironment();
        var contract = await new EmploymentContractService(db, new EmployeeCompensationService(db, new AuditService(db)), new HrFileStorage(db, environment), new AuditService(db))
            .CreateAsync(1, new(null, EmploymentContractType.Permanent, new DateTime(2026, 1, 1), null, null, null, 1000m, "USD", null, null, null, null), canSetSalary: true);

        await Records(db, environment).TerminateAsync(1, new DateTime(2026, 6, 20), "Contract ended", null, acknowledgeOpenItems: false);

        var savedContract = await db.EmploymentContracts.SingleAsync(c => c.Id == contract.Id);
        Assert.Equal(EmploymentContractStatus.Terminated, savedContract.Status);
        Assert.Equal(new DateTime(2026, 6, 20), savedContract.TerminatedOn);
        Assert.False((await db.Users.SingleAsync(u => u.Id == 50)).IsActive);
        Assert.True((await db.Users.SingleAsync(u => u.Id == 51)).IsActive);
    }

    [Fact]
    public async Task Terminated_Employee_Gets_Prorated_Final_Month_In_Payroll()
    {
        await using var db = await HrSalaryIntegrityTests.NewAccountingDbAsync();
        var employee = await db.Employees.SingleAsync();
        employee.HireDate = new DateTime(2026, 1, 1);
        await db.SaveChangesAsync();
        await new EmployeeCompensationService(db, new AuditService(db)).ChangeAsync(new(1, new DateTime(2026, 1, 1), 3000m, "USD", EmployeeSalaryType.Monthly, CompensationSource.Manual, null, null));
        await Records(db, new HrContractsAndCompensationTests.TestEnvironment()).TerminateAsync(1, new DateTime(2026, 6, 10), "Resigned", null, false);

        var line = (await new PayrollService(db, HrSalaryIntegrityTests.BuildService(db), new HrCalendarService(db), new AuditService(db))
            .GenerateAsync(2026, 6)).Run.Lines.Single();

        Assert.Equal(10m, line.EmployedDays);
        Assert.Equal(1000m, line.BaseSalary);
        Assert.Empty((await new PayrollService(db, HrSalaryIntegrityTests.BuildService(db), new HrCalendarService(db), new AuditService(db))
            .GenerateAsync(2026, 7)).Run.Lines);
    }

    [Fact]
    public async Task Profile_Loads_All_Sections_And_Hides_Money_Without_Permission()
    {
        await using var db = await SeedAsync();
        await new EmployeeCompensationService(db, new AuditService(db)).ChangeAsync(new(1, new DateTime(2026, 1, 1), 1000m, "USD", EmployeeSalaryType.Monthly, CompensationSource.Manual, null, null));
        db.LeaveTypes.Add(new LeaveType { Id = 1, Name = "Annual", AnnualAllowanceDays = 20m, IsPaid = true, IsActive = true });
        await db.SaveChangesAsync();

        var admin = Assert.IsType<EmployeeDetailsViewModel>(Assert.IsType<ViewResult>(
            await EmployeeModuleTests.BuildEmployeesController(db).Details(1)).Model);
        Assert.NotNull(admin.CurrentCompensation);
        Assert.Single(admin.CompensationHistory);
        Assert.Contains(admin.LeaveBalances, b => b.AllowanceDays == 20m);

        var clerk = Assert.IsType<EmployeeDetailsViewModel>(Assert.IsType<ViewResult>(
            await EmployeeModuleTests.BuildEmployeesController(db, new System.Security.Claims.ClaimsPrincipal(
                new System.Security.Claims.ClaimsIdentity([new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, "Operator")], "Test"))).Details(1)).Model);
        Assert.Null(clerk.CurrentCompensation);
        Assert.Empty(clerk.CompensationHistory);
        Assert.Empty(clerk.PayrollLines);
        Assert.Contains(clerk.LeaveBalances, b => b.AllowanceDays == 20m);
    }

    // ---------- helpers ----------

    private static IFormFile File(string name)
    {
        var stream = new MemoryStream([1, 2, 3]);
        return new FormFile(stream, 0, stream.Length, "file", name) { Headers = new HeaderDictionary(), ContentType = "application/pdf" };
    }

    private static async Task<ApplicationDbContext> SeedAsync()
    {
        var db = new ApplicationDbContext(EmployeeModuleTests.NewDbOptions());
        EmployeeModuleTests.SeedReferenceData(db);
        await db.SaveChangesAsync();
        var employee = await db.Employees.SingleAsync();
        employee.HireDate = new DateTime(2026, 1, 1);
        await db.SaveChangesAsync();
        return db;
    }

    private static EmployeeRecordsService Records(ApplicationDbContext db, HrContractsAndCompensationTests.TestEnvironment environment)
    {
        var audit = new AuditService(db);
        var files = new HrFileStorage(db, environment);
        var compensation = new EmployeeCompensationService(db, audit);
        return new EmployeeRecordsService(db, files, new LeaveService(db, new HrCalendarService(db), files, audit),
            new EmploymentContractService(db, compensation, files, audit), audit);
    }
}
