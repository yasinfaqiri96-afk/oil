using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PTGOilSystem.Web.Controllers;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Auth;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Security;
using PTGOilSystem.Web.Services;
using Xunit;

namespace PTGOilSystem.Web.Tests;

public class AuthControllerTests
{
    [Fact]
    public async Task Login_With_Valid_Credentials_Signs_In_And_Redirects()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        db.Roles.Add(new Role { Id = 1, Name = AuthRoles.Admin, Description = "Admin" });
        await db.SaveChangesAsync();

        var userService = new UserService(db);
        await userService.CreateUserAsync("admin", "System Admin", "StrongPass123!", 1);

        var authService = new RecordingAuthenticationService();
        var services = new ServiceCollection()
            .AddSingleton<IAuthenticationService>(authService)
            .BuildServiceProvider();

        var httpContext = new DefaultHttpContext { RequestServices = services };
        var controller = new AuthController(userService, NullLogger<AuthController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
            TempData = new TempDataDictionary(httpContext, new InMemoryTempDataProvider())
        };
        controller.Url = new StubUrlHelper();

        var result = await controller.Login(new LoginViewModel
        {
            Username = "admin",
            Password = "StrongPass123!"
        });

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Index", redirect.ActionName);
        Assert.Equal("Home", redirect.ControllerName);
        Assert.NotNull(authService.LastPrincipal);
        Assert.Equal("1", authService.LastPrincipal!.FindFirstValue(ClaimTypes.NameIdentifier));
        Assert.Equal(AuthRoles.Admin, authService.LastPrincipal.FindFirstValue(ClaimTypes.Role));
    }

    [Fact]
    public async Task Login_With_Invalid_Credentials_Returns_View_With_Error()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        db.Roles.Add(new Role { Id = 1, Name = AuthRoles.Admin, Description = "Admin" });
        await db.SaveChangesAsync();

        var userService = new UserService(db);
        await userService.CreateUserAsync("admin", "System Admin", "StrongPass123!", 1);

        var httpContext = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection()
                .AddSingleton<IAuthenticationService>(new RecordingAuthenticationService())
                .BuildServiceProvider()
        };

        var controller = new AuthController(userService, NullLogger<AuthController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
            TempData = new TempDataDictionary(httpContext, new InMemoryTempDataProvider())
        };
        controller.Url = new StubUrlHelper();

        var result = await controller.Login(new LoginViewModel
        {
            Username = "admin",
            Password = "wrong-password"
        });

        var view = Assert.IsType<ViewResult>(result);
        Assert.IsType<LoginViewModel>(view.Model);
        Assert.False(controller.ModelState.IsValid);
    }

    [Fact]
    public async Task Login_With_Custom_Role_Keeps_Custom_Role_Claim()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        db.Roles.Add(new Role
        {
            Id = 7,
            Name = "Finance",
            Description = "Finance team",
            CanManageData = true,
            AllowedNavigationItems = RoleAccessRules.SerializeNavigation(
            [
                RoleNavigationKeys.Dashboard,
                RoleNavigationKeys.CashAccounts,
                RoleNavigationKeys.Payments
            ])
        });
        await db.SaveChangesAsync();

        var userService = new UserService(db);
        await userService.CreateUserAsync("finance", "Finance User", "StrongPass123!", 7);

        var authService = new RecordingAuthenticationService();
        var services = new ServiceCollection()
            .AddSingleton<IAuthenticationService>(authService)
            .BuildServiceProvider();

        var httpContext = new DefaultHttpContext { RequestServices = services };
        var controller = new AuthController(userService, NullLogger<AuthController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
            TempData = new TempDataDictionary(httpContext, new InMemoryTempDataProvider())
        };
        controller.Url = new StubUrlHelper();

        var result = await controller.Login(new LoginViewModel
        {
            Username = "finance",
            Password = "StrongPass123!"
        });

        Assert.IsType<RedirectToActionResult>(result);
        Assert.NotNull(authService.LastPrincipal);
        Assert.Equal("Finance", authService.LastPrincipal!.FindFirstValue(ClaimTypes.Role));
        Assert.Contains(authService.LastPrincipal.FindAll(AppClaimTypes.AllowedNavigation), c => c.Value == RoleNavigationKeys.CashAccounts);
        Assert.Contains(authService.LastPrincipal.FindAll(AppClaimTypes.Permission), c => c.Value == AppPermissions.ManageData);
        Assert.DoesNotContain(authService.LastPrincipal.FindAll(AppClaimTypes.Permission), c => c.Value == AppPermissions.ManageUsers);
    }

    [Fact]
    public async Task Logout_Signs_Out_And_Redirects_To_Login()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        var authService = new RecordingAuthenticationService();
        var services = new ServiceCollection()
            .AddSingleton<IAuthenticationService>(authService)
            .BuildServiceProvider();
        var httpContext = new DefaultHttpContext { RequestServices = services };
        var controller = new AuthController(new UserService(db), NullLogger<AuthController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
            TempData = new TempDataDictionary(httpContext, new InMemoryTempDataProvider())
        };
        controller.Url = new StubUrlHelper();

        var result = await controller.Logout();

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Login", redirect.ActionName);
        Assert.True(authService.SignedOut);
    }

    private sealed class RecordingAuthenticationService : IAuthenticationService
    {
        public ClaimsPrincipal? LastPrincipal { get; private set; }
        public bool SignedOut { get; private set; }

        public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme)
            => Task.FromResult(AuthenticateResult.NoResult());

        public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties)
            => Task.CompletedTask;

        public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties)
            => Task.CompletedTask;

        public Task SignInAsync(HttpContext context, string? scheme, ClaimsPrincipal principal, AuthenticationProperties? properties)
        {
            LastPrincipal = principal;
            return Task.CompletedTask;
        }

        public Task SignOutAsync(HttpContext context, string? scheme, AuthenticationProperties? properties)
        {
            SignedOut = true;
            return Task.CompletedTask;
        }
    }

    private sealed class InMemoryTempDataProvider : ITempDataProvider
    {
        private IDictionary<string, object> _data = new Dictionary<string, object>();

        public IDictionary<string, object> LoadTempData(HttpContext context) => _data;

        public void SaveTempData(HttpContext context, IDictionary<string, object> values)
            => _data = new Dictionary<string, object>(values);
    }

    private sealed class StubUrlHelper : IUrlHelper
    {
        public ActionContext ActionContext { get; } = new();

        public string? Action(UrlActionContext actionContext) => "/";

        public string? Content(string? contentPath) => contentPath;

        public bool IsLocalUrl(string? url) => true;

        public string? Link(string? routeName, object? values) => "/";

        public string? RouteUrl(UrlRouteContext routeContext) => "/";
    }
}
