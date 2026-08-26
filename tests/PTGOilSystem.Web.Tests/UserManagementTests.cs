using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PTGOilSystem.Web.Controllers;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Auth;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Security;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.DeleteSafety;
using Xunit;

namespace PTGOilSystem.Web.Tests;

public class UserManagementTests
{
    [Fact]
    public async Task ChangePassword_With_Valid_Current_Password_Updates_User_Credentials()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        db.Roles.Add(new Role { Id = 1, Name = AuthRoles.Admin, Description = "Admin" });
        await db.SaveChangesAsync();

        var users = new UserService(db);
        var user = await users.CreateUserAsync("admin", "System Admin", "StrongPass123!", 1);

        var controller = new AuthController(users, new AuditService(db), NullLogger<AuthController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = BuildHttpContext(user.Id, user.Username, user.FullName, AuthRoles.Admin)
            },
            TempData = BuildTempData()
        };

        var result = await controller.ChangePassword(new ChangePasswordViewModel
        {
            CurrentPassword = "StrongPass123!",
            NewPassword = "NewStrong456!",
            ConfirmPassword = "NewStrong456!"
        });

        Assert.IsType<RedirectToActionResult>(result);
        Assert.Null(await users.VerifyPasswordAsync("admin", "StrongPass123!"));
        Assert.NotNull(await users.VerifyPasswordAsync("admin", "NewStrong456!"));
    }

    [Fact]
    public async Task ResetPasswordAsync_Updates_Password_For_Target_User()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        db.Roles.Add(new Role { Id = 1, Name = AuthRoles.Admin, Description = "Admin" });
        await db.SaveChangesAsync();

        var users = new UserService(db);
        var user = await users.CreateUserAsync("operator", "System Operator", "StrongPass123!", 1);

        await users.ResetPasswordAsync(user.Id, "ResetPass789!");

        Assert.Null(await users.VerifyPasswordAsync("operator", "StrongPass123!"));
        Assert.NotNull(await users.VerifyPasswordAsync("operator", "ResetPass789!"));
    }

    [Fact]
    public async Task Edit_Blocks_Removing_Only_Active_Admin()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        db.Roles.AddRange(
            new Role { Id = 1, Name = AuthRoles.Admin, Description = "Admin" },
            new Role { Id = 2, Name = AuthRoles.Viewer, Description = "Viewer" });
        await db.SaveChangesAsync();

        var users = new UserService(db);
        var admin = await users.CreateUserAsync("admin", "System Admin", "StrongPass123!", 1);

        var httpContext = BuildHttpContext(admin.Id, admin.Username, admin.FullName, AuthRoles.Admin);
        var controller = new UsersController(
            db,
            users,
            new AuditService(db),
            new MasterDataDeleteSafetyService(db),
            new CurrentUserContext(new HttpContextAccessor { HttpContext = httpContext }))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = httpContext
            },
            TempData = BuildTempData()
        };

        var result = await controller.Edit(admin.Id, new UserEditViewModel
        {
            Id = admin.Id,
            Username = admin.Username,
            FullName = admin.FullName,
            RoleId = 2,
            IsActive = false
        });

        var view = Assert.IsType<ViewResult>(result);
        Assert.IsType<UserEditViewModel>(view.Model);
        Assert.False(controller.ModelState.IsValid);
        Assert.Contains(controller.ModelState[string.Empty]!.Errors, e => e.ErrorMessage.Contains("آخرین مدیر"));
    }

    [Fact]
    public async Task Roles_Create_Adds_Custom_Role_And_User_Index_Lists_It()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        db.Roles.Add(new Role { Id = 1, Name = AuthRoles.Admin, Description = "Admin" });
        await db.SaveChangesAsync();

        var rolesController = new RolesController(db, new AuditService(db))
        {
            TempData = BuildTempData()
        };

        var createResult = await rolesController.Create(new RoleCreateViewModel
        {
            Name = "Finance",
            Description = "Finance team",
            CanManageData = true,
            AllowedNavigationItems =
            [
                RoleNavigationKeys.Dashboard,
                RoleNavigationKeys.CashAccounts,
                RoleNavigationKeys.Payments
            ]
        });

        var redirect = Assert.IsType<RedirectToActionResult>(createResult);
        Assert.Equal("Index", redirect.ActionName);
        var savedRole = await db.Roles.SingleAsync(r => r.Name == "Finance");
        Assert.True(savedRole.CanManageData);
        Assert.False(savedRole.CanManageUsers);
        Assert.Contains(RoleNavigationKeys.CashAccounts, RoleAccessRules.ResolveNavigationForRole(savedRole));

        var usersController = new UsersController(
            db,
            new UserService(db),
            new AuditService(db),
            new MasterDataDeleteSafetyService(db),
            new CurrentUserContext(new HttpContextAccessor()));

        var indexResult = await usersController.Index(null, null, null);

        Assert.IsType<ViewResult>(indexResult);
        var roles = Assert.IsAssignableFrom<IEnumerable<SelectListItem>>(usersController.ViewData["Roles"]);
        Assert.Contains(roles, role => role.Text == "Finance");
    }

    [Fact]
    public void Roles_Create_Get_Returns_Page_Form_With_Access_Options()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        using var db = new ApplicationDbContext(options);
        var controller = new RolesController(db, new AuditService(db));

        var result = controller.Create();

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<RoleCreateViewModel>(view.Model);
        Assert.Contains(RoleNavigationKeys.Dashboard, model.AllowedNavigationItems);
        Assert.IsAssignableFrom<IEnumerable<RoleNavigationItem>>(controller.ViewData["RoleNavigationItems"]);
        Assert.True((bool?)controller.ViewData["ModalDesignSystemAssets"] == true);
    }

    [Fact]
    public async Task Roles_Index_Search_Filters_By_Name_Or_Description()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        db.Roles.AddRange(
            new Role { Id = 1, Name = AuthRoles.Admin, Description = "System admin" },
            new Role { Id = 2, Name = "Finance", Description = "Payments team" },
            new Role { Id = 3, Name = "Warehouse", Description = "Stock team" });
        await db.SaveChangesAsync();

        var controller = new RolesController(db, new AuditService(db));

        var result = await controller.Index("Pay");

        var view = Assert.IsType<ViewResult>(result);
        var roles = Assert.IsAssignableFrom<IEnumerable<Role>>(view.Model).ToList();
        var role = Assert.Single(roles);
        Assert.Equal("Finance", role.Name);
        Assert.Equal("Pay", controller.ViewData["q"]);
    }

    [Fact]
    public async Task Roles_Edit_Updates_Custom_Role_Access_Settings()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        db.Roles.Add(new Role
        {
            Id = 5,
            Name = "Finance",
            Description = "Finance team",
            AllowedNavigationItems = RoleAccessRules.SerializeNavigation([RoleNavigationKeys.Dashboard])
        });
        await db.SaveChangesAsync();

        var controller = new RolesController(db, new AuditService(db))
        {
            TempData = BuildTempData()
        };

        var result = await controller.Edit(5, new RoleEditViewModel
        {
            Id = 5,
            Name = "Finance",
            Description = "Finance managers",
            CanManageData = true,
            CanManageUsers = true,
            AllowedNavigationItems =
            [
                RoleNavigationKeys.Dashboard,
                RoleNavigationKeys.Reports
            ]
        });

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Details", redirect.ActionName);

        var role = await db.Roles.SingleAsync(r => r.Id == 5);
        Assert.Equal("Finance managers", role.Description);
        Assert.True(role.CanManageData);
        Assert.True(role.CanManageUsers);
        var allowed = RoleAccessRules.ResolveNavigationForRole(role);
        Assert.Contains(RoleNavigationKeys.Reports, allowed);
        Assert.Contains(RoleNavigationKeys.Management, allowed);
    }

    private static DefaultHttpContext BuildHttpContext(int userId, string username, string fullName, string roleName)
    {
        var context = new DefaultHttpContext();
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                new Claim(ClaimTypes.Name, fullName),
                new Claim(AppClaimTypes.Username, username),
                new Claim(ClaimTypes.Role, roleName),
            ],
            "TestAuth"));
        return context;
    }

    private static TempDataDictionary BuildTempData()
        => new(new DefaultHttpContext(), new InMemoryTempDataProvider());

    private sealed class InMemoryTempDataProvider : ITempDataProvider
    {
        private IDictionary<string, object> _data = new Dictionary<string, object>();

        public IDictionary<string, object> LoadTempData(HttpContext context) => _data;

        public void SaveTempData(HttpContext context, IDictionary<string, object> values)
            => _data = new Dictionary<string, object>(values);
    }
}
