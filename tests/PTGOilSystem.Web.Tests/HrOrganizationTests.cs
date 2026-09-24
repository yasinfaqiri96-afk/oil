using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Controllers;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Employees;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.HumanResources;
using PTGOilSystem.Web.Security;
using PTGOilSystem.Web.Services;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>مدیریت بشری، فاز ۱ — بخش‌ها و بست‌ها و مهاجرتِ امنِ متنِ آزادِ قدیمی.</summary>
public class HrOrganizationTests
{
    [Fact]
    public async Task Department_Create_Rejects_Duplicate_Name_Case_Insensitively()
    {
        await using var db = new ApplicationDbContext(EmployeeModuleTests.NewDbOptions());
        var controller = BuildController(db);

        Assert.IsType<RedirectToActionResult>(await controller.EditDepartment(new DepartmentFormViewModel { Name = " مالی " }));
        var second = await controller.EditDepartment(new DepartmentFormViewModel { Name = "مالی" });

        Assert.IsType<ViewResult>(second);
        Assert.False(controller.ModelState.IsValid);
        var department = await db.Departments.SingleAsync();
        Assert.Equal("مالی", department.Name);
        Assert.Contains(await db.AuditLogs.ToListAsync(), a => a.EntityName == nameof(Department) && a.Action == "Insert");
    }

    [Fact]
    public async Task Renaming_Department_And_Position_Keeps_Legacy_Text_In_Sync()
    {
        await using var db = new ApplicationDbContext(EmployeeModuleTests.NewDbOptions());
        EmployeeModuleTests.SeedReferenceData(db);
        db.Departments.Add(new Department { Id = 5, Name = "Finance" });
        db.Positions.Add(new Position { Id = 7, Name = "Accountant", DepartmentId = 5 });
        await db.SaveChangesAsync();
        var employee = await db.Employees.SingleAsync();
        employee.DepartmentId = 5;
        employee.Department = "Finance";
        employee.PositionId = 7;
        employee.JobTitle = "Accountant";
        await db.SaveChangesAsync();

        var controller = BuildController(db);
        await controller.EditDepartment(new DepartmentFormViewModel { Id = 5, Name = "مالی", IsActive = true });
        await controller.EditPosition(new PositionFormViewModel { Id = 7, Name = "حسابدار", DepartmentId = 5, IsActive = true });

        var saved = await db.Employees.AsNoTracking().SingleAsync();
        Assert.Equal("مالی", saved.Department);
        Assert.Equal("حسابدار", saved.JobTitle);
    }

    [Fact]
    public async Task Link_Legacy_Text_Only_Links_Exact_Matches_And_Keeps_Text()
    {
        await using var db = new ApplicationDbContext(EmployeeModuleTests.NewDbOptions());
        EmployeeModuleTests.SeedReferenceData(db);
        db.Employees.Add(new Employee
        {
            Id = 2, EmployeeCode = "EMP-2", FullName = "Second", Department = "Opperations", JobTitle = "driver",
            SalaryCurrency = "USD", IsActive = true
        });
        db.Departments.Add(new Department { Id = 5, Name = "Finance" });
        db.Departments.Add(new Department { Id = 6, Name = "Operations" });
        db.Positions.Add(new Position { Id = 7, Name = "Driver" });
        await db.SaveChangesAsync();
        var first = await db.Employees.SingleAsync(e => e.Id == 1);
        first.Department = " finance ";
        await db.SaveChangesAsync();

        await BuildController(db).LinkLegacyText();

        var employees = await db.Employees.AsNoTracking().OrderBy(e => e.Id).ToListAsync();
        Assert.Equal(5, employees[0].DepartmentId);
        Assert.Equal(" finance ", employees[0].Department);
        Assert.Null(employees[1].DepartmentId); // «Opperations» املای دیگری است؛ حدس زده نمی‌شود.
        Assert.Equal(7, employees[1].PositionId);
        Assert.Equal("driver", employees[1].JobTitle);
    }

    [Fact]
    public async Task Employee_Form_Uses_Department_And_Position_And_Preserves_Legacy_Text()
    {
        await using var db = new ApplicationDbContext(EmployeeModuleTests.NewDbOptions());
        EmployeeModuleTests.SeedReferenceData(db);
        db.Departments.Add(new Department { Id = 5, Name = "مالی" });
        db.Positions.Add(new Position { Id = 7, Name = "حسابدار", DepartmentId = 5 });
        await db.SaveChangesAsync();
        var legacy = await db.Employees.SingleAsync();
        legacy.Department = "Old text";
        legacy.JobTitle = "Old title";
        await db.SaveChangesAsync();

        var controller = EmployeeModuleTests.BuildEmployeesController(db);

        // ویرایش بدونِ انتخاب بخش/بست: متن قدیمی پاک نمی‌شود.
        await controller.Edit(1, new EmployeeFormViewModel
        {
            Id = 1, EmployeeCode = "EMP-BASE", FullName = "Base Employee", BaseSalaryAmount = 1000m,
            SalaryCurrency = "USD", HireDate = new DateTime(2026, 5, 1), IsActive = true
        });
        var kept = await db.Employees.AsNoTracking().SingleAsync();
        Assert.Equal("Old text", kept.Department);
        Assert.Equal("Old title", kept.JobTitle);

        // انتخاب بخش/بست: پیوند ثبت و متن همگام می‌شود.
        await controller.Edit(1, new EmployeeFormViewModel
        {
            Id = 1, EmployeeCode = "EMP-BASE", FullName = "Base Employee", BaseSalaryAmount = 1000m,
            SalaryCurrency = "USD", HireDate = new DateTime(2026, 5, 1), IsActive = true,
            DepartmentId = 5, PositionId = 7
        });
        var linked = await db.Employees.AsNoTracking().SingleAsync();
        Assert.Equal(5, linked.DepartmentId);
        Assert.Equal(7, linked.PositionId);
        Assert.Equal("مالی", linked.Department);
        Assert.Equal("حسابدار", linked.JobTitle);
    }

    [Fact]
    public async Task Employee_Form_Rejects_Inactive_Department_For_New_Link()
    {
        await using var db = new ApplicationDbContext(EmployeeModuleTests.NewDbOptions());
        EmployeeModuleTests.SeedReferenceData(db);
        db.Departments.Add(new Department { Id = 5, Name = "Closed", IsActive = false });
        await db.SaveChangesAsync();

        var result = await EmployeeModuleTests.BuildEmployeesController(db).Edit(1, new EmployeeFormViewModel
        {
            Id = 1, EmployeeCode = "EMP-BASE", FullName = "Base Employee", BaseSalaryAmount = 1000m,
            SalaryCurrency = "USD", HireDate = new DateTime(2026, 5, 1), IsActive = true, DepartmentId = 5
        });

        Assert.IsType<ViewResult>(result);
        Assert.Null((await db.Employees.AsNoTracking().SingleAsync()).DepartmentId);
    }

    [Fact]
    public void Hr_Organization_Is_In_Human_Resources_Navigation()
        => Assert.Equal(RoleNavigationKeys.HumanResources, RoleAccessRules.NavigationKeyForController("HrOrganization"));

    private static HrOrganizationController BuildController(ApplicationDbContext db)
    {
        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, AuthRoles.Admin)], "Test"))
        };
        return new HrOrganizationController(db, new AuditService(db))
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
            TempData = new TempDataDictionary(httpContext, new NullTempDataProvider())
        };
    }

    private sealed class NullTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }
}
