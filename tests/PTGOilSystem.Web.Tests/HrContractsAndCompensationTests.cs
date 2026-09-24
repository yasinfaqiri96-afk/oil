using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Employees;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Employees;
using PTGOilSystem.Web.Services.Exceptions;
using PTGOilSystem.Web.Services.HumanResources;
using PTGOilSystem.Web.Services.Time;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>مدیریت بشری، فاز ۲ و ۶ — قرارداد کاری و تاریخچهٔ معاش.</summary>
public class HrContractsAndCompensationTests
{
    private static readonly DateTime Jan1 = new(2026, 1, 1);

    // ---------- تاریخچهٔ معاش ----------

    [Fact]
    public async Task Salary_Change_Closes_Previous_Period_And_Never_Overwrites_It()
    {
        await using var db = await SeedAsync();
        var service = Compensation(db);
        await service.ChangeAsync(Change(Jan1, 1000m));

        await service.ChangeAsync(Change(new DateTime(2026, 4, 1), 1200m));

        var history = await db.EmployeeCompensations.OrderBy(c => c.EffectiveFrom).ToListAsync();
        Assert.Equal(2, history.Count);
        Assert.Equal(1000m, history[0].BaseSalary);
        Assert.Equal(new DateTime(2026, 3, 31), history[0].EffectiveTo);
        Assert.Equal(1200m, history[1].BaseSalary);
        Assert.Null(history[1].EffectiveTo);

        Assert.Equal(1000m, (await service.GetEffectiveAsync(1, new DateTime(2026, 3, 15)))!.BaseSalary);
        Assert.Equal(1200m, (await service.GetEffectiveAsync(1, new DateTime(2026, 4, 1)))!.BaseSalary);
        Assert.Null(await service.GetEffectiveAsync(1, new DateTime(2025, 12, 31)));

        // آینه فقط از معاشِ جاری پر می‌شود.
        Assert.Equal(1200m, (await db.Employees.SingleAsync()).BaseSalaryAmount);
    }

    [Fact]
    public async Task Backdated_Salary_Change_Is_Rejected()
    {
        await using var db = await SeedAsync();
        var service = Compensation(db);
        await service.ChangeAsync(Change(new DateTime(2026, 4, 1), 1000m));

        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => service.ChangeAsync(Change(new DateTime(2026, 3, 1), 900m)));
        Assert.Equal("HR_SALARY_BACKDATED", ex.Code);
        Assert.Single(await db.EmployeeCompensations.ToListAsync());
    }

    [Fact]
    public async Task Future_Salary_Change_Does_Not_Touch_Current_Mirror()
    {
        await using var db = await SeedAsync();
        var service = Compensation(db);
        await service.ChangeAsync(Change(Jan1, 1000m));

        await service.ChangeAsync(Change(AfghanistanBusinessClock.SystemToday.AddDays(40), 1500m));

        Assert.Equal(1000m, (await db.Employees.SingleAsync()).BaseSalaryAmount);
    }

    [Fact]
    public async Task Salary_Change_Inside_Accrued_Month_Is_Rejected()
    {
        await using var db = await SeedAsync();
        var service = Compensation(db);
        await service.ChangeAsync(Change(Jan1, 1000m));
        await HrSalaryIntegrityTests.BuildService(db, pilot: false).CreateAsync(new EmployeeSalaryTransactionCommand(
            1, new DateTime(2026, 5, 31), EmployeeSalaryTransactionType.SalaryAccrual, 1000m, "USD", null, null, null, null, 2026, 5));

        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => service.ChangeAsync(Change(new DateTime(2026, 5, 15), 1100m)));
        Assert.Equal("HR_SALARY_PERIOD_FINALIZED", ex.Code);

        await service.ChangeAsync(Change(new DateTime(2026, 6, 1), 1100m));
    }

    [Fact]
    public async Task Creating_Employee_With_Salary_Starts_Salary_History()
    {
        await using var db = new ApplicationDbContext(EmployeeModuleTests.NewDbOptions());
        EmployeeModuleTests.SeedReferenceData(db);
        await db.SaveChangesAsync();

        await EmployeeModuleTests.BuildEmployeesController(db).Create(new EmployeeFormViewModel
        {
            EmployeeCode = "EMP-NEW",
            FullName = "New Employee",
            BaseSalaryAmount = 800m,
            SalaryCurrency = "USD",
            HireDate = new DateTime(2026, 2, 1),
            IsActive = true
        });

        var employee = await db.Employees.SingleAsync(e => e.EmployeeCode == "EMP-NEW");
        var compensation = await db.EmployeeCompensations.SingleAsync(c => c.EmployeeId == employee.Id);
        Assert.Equal(800m, compensation.BaseSalary);
        Assert.Equal(new DateTime(2026, 2, 1), compensation.EffectiveFrom);
    }

    // ---------- قرارداد ----------

    [Fact]
    public async Task Contract_Records_Salary_History_And_Current_Department_Position()
    {
        await using var db = await SeedAsync();
        var contracts = Contracts(db);

        var contract = await contracts.CreateAsync(1, Terms(Jan1, 1000m, departmentId: 5, positionId: 7), canSetSalary: true);

        Assert.Equal(EmploymentContractStatus.Active, contract.Status);
        Assert.StartsWith("HR-EMP-BASE-20260101", contract.ContractNumber);
        var compensation = await db.EmployeeCompensations.SingleAsync();
        Assert.Equal(CompensationSource.Contract, compensation.Source);
        Assert.Equal(contract.Id, compensation.EmploymentContractId);
        var employee = await db.Employees.SingleAsync();
        Assert.Equal(5, employee.DepartmentId);
        Assert.Equal("مالی", employee.Department);
        Assert.Equal(7, employee.PositionId);
        Assert.Contains(await db.AuditLogs.ToListAsync(), a => a.EntityName == nameof(EmploymentContract) && a.Action == "Insert");
    }

    [Fact]
    public async Task Second_Active_Contract_Is_Rejected()
    {
        await using var db = await SeedAsync();
        var contracts = Contracts(db);
        await contracts.CreateAsync(1, Terms(Jan1, 1000m), canSetSalary: true);

        var ex = await Assert.ThrowsAsync<BusinessRuleException>(
            () => contracts.CreateAsync(1, Terms(new DateTime(2026, 6, 1), 1000m), canSetSalary: true));
        Assert.Equal("HR_CONTRACT_ACTIVE_EXISTS", ex.Code);
    }

    [Fact]
    public async Task Renewal_Creates_New_Contract_And_Keeps_History()
    {
        await using var db = await SeedAsync();
        var contracts = Contracts(db);
        var first = await contracts.CreateAsync(1, Terms(Jan1, 1000m, end: new DateTime(2026, 12, 31)), canSetSalary: true);

        var renewed = await contracts.RenewAsync(first.Id, Terms(new DateTime(2026, 7, 1), 1300m), canSetSalary: true);

        var old = await db.EmploymentContracts.SingleAsync(c => c.Id == first.Id);
        Assert.Equal(EmploymentContractStatus.Expired, old.Status);
        Assert.Equal(new DateTime(2026, 6, 30), old.EndDate);
        Assert.Equal(1000m, old.BaseSalary);
        Assert.Equal(first.Id, renewed.RenewedFromContractId);
        Assert.Equal(EmploymentContractStatus.Active, renewed.Status);

        var history = await db.EmployeeCompensations.OrderBy(c => c.EffectiveFrom).ToListAsync();
        Assert.Equal(new[] { 1000m, 1300m }, history.Select(h => h.BaseSalary));
        Assert.Equal(new DateTime(2026, 6, 30), history[0].EffectiveTo);
    }

    [Fact]
    public async Task Renewal_With_Same_Salary_Adds_No_Salary_Row()
    {
        await using var db = await SeedAsync();
        var contracts = Contracts(db);
        var first = await contracts.CreateAsync(1, Terms(Jan1, 1000m), canSetSalary: true);

        await contracts.RenewAsync(first.Id, Terms(new DateTime(2026, 7, 1), 1000m), canSetSalary: true);

        Assert.Single(await db.EmployeeCompensations.ToListAsync());
    }

    [Fact]
    public async Task Without_Salary_Permission_Contract_Takes_Current_Salary()
    {
        await using var db = await SeedAsync();
        await Compensation(db).ChangeAsync(Change(Jan1, 950m));

        var contract = await Contracts(db).CreateAsync(1, Terms(new DateTime(2026, 2, 1), 5000m), canSetSalary: false);

        Assert.Equal(950m, contract.BaseSalary);
        Assert.Single(await db.EmployeeCompensations.ToListAsync());
    }

    [Fact]
    public async Task Terminate_Requires_Reason_And_Keeps_Contract()
    {
        await using var db = await SeedAsync();
        var contracts = Contracts(db);
        var contract = await contracts.CreateAsync(1, Terms(Jan1, 1000m), canSetSalary: true);

        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => contracts.TerminateAsync(contract.Id, new DateTime(2026, 8, 1), " "));
        Assert.Equal("HR_CONTRACT_TERMINATE_REASON", ex.Code);

        await contracts.TerminateAsync(contract.Id, new DateTime(2026, 8, 1), "End of project");
        var saved = await db.EmploymentContracts.SingleAsync();
        Assert.Equal(EmploymentContractStatus.Terminated, saved.Status);
        Assert.Equal("End of project", saved.TerminationReason);
        Assert.Equal(1000m, saved.BaseSalary);
    }

    [Fact]
    public async Task Notes_Update_Does_Not_Change_Terms()
    {
        await using var db = await SeedAsync();
        var contracts = Contracts(db);
        var contract = await contracts.CreateAsync(1, Terms(Jan1, 1000m, end: new DateTime(2026, 12, 31)), canSetSalary: true);

        await contracts.UpdateNotesAsync(contract.Id, "Signed copy received", null);

        var saved = await db.EmploymentContracts.SingleAsync();
        Assert.Equal("Signed copy received", saved.Notes);
        Assert.Equal(new DateTime(2026, 12, 31), saved.EndDate);
        Assert.Equal(1000m, saved.BaseSalary);
    }

    [Fact]
    public void Active_Contract_Past_End_Date_Shows_As_Expired()
    {
        var contract = new EmploymentContract { Status = EmploymentContractStatus.Active, EndDate = new DateTime(2026, 1, 31) };
        Assert.Equal(EmploymentContractStatus.Expired, contract.EffectiveStatus(new DateTime(2026, 2, 1)));
        Assert.Equal(EmploymentContractStatus.Active, contract.EffectiveStatus(new DateTime(2026, 1, 31)));
    }

    // ---------- helpers ----------

    private static CompensationChange Change(DateTime from, decimal salary)
        => new(1, from, salary, "USD", EmployeeSalaryType.Monthly, CompensationSource.Manual, null, null);

    private static ContractTerms Terms(DateTime start, decimal salary, DateTime? end = null, int? departmentId = null, int? positionId = null)
        => new(null, EmploymentContractType.FixedTerm, start, end, departmentId, positionId, salary, "USD", 6m, 8m, null, null);

    private static async Task<ApplicationDbContext> SeedAsync()
    {
        var db = new ApplicationDbContext(EmployeeModuleTests.NewDbOptions());
        EmployeeModuleTests.SeedReferenceData(db);
        db.Departments.Add(new Department { Id = 5, Name = "مالی" });
        db.Positions.Add(new Position { Id = 7, Name = "حسابدار", DepartmentId = 5 });
        await db.SaveChangesAsync();
        return db;
    }

    private static EmployeeCompensationService Compensation(ApplicationDbContext db) => new(db, new AuditService(db));

    private static EmploymentContractService Contracts(ApplicationDbContext db)
        => new(db, Compensation(db), new HrFileStorage(db, new TestEnvironment()), new AuditService(db));

    internal sealed class TestEnvironment : IWebHostEnvironment
    {
        public string WebRootPath { get; set; } = Path.Combine(Path.GetTempPath(), "ptg-hr-tests", "wwwroot");
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ApplicationName { get; set; } = "PTGOilSystem.Web.Tests";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = Path.Combine(Path.GetTempPath(), "ptg-hr-tests", Guid.NewGuid().ToString("N"));
        public string EnvironmentName { get; set; } = "Testing";
    }
}
