using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PTGOilSystem.Web.Configuration;
using PTGOilSystem.Web.Controllers;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Security;
using PTGOilSystem.Web.Services;
using Xunit;

namespace PTGOilSystem.Web.Tests;

public class OnboardingTests
{
    private static ApplicationDbContext CreateDb()
        => new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    [Fact]
    public async Task Progress_On_Empty_Database_Has_No_Completed_Step()
    {
        await using var db = CreateDb();

        var progress = await new OnboardingService(db).GetProgressAsync();

        Assert.Equal(new OnboardingProgress(false, false, false, false, false, false), progress);
        Assert.False(progress.IsComplete);
    }

    [Fact]
    public async Task Company_Without_System_Owner_Does_Not_Complete_The_First_Step()
    {
        await using var db = CreateDb();
        db.Companies.Add(new Company { Code = "C1", Name = "Demo Co" });
        await db.SaveChangesAsync();

        var progress = await new OnboardingService(db).GetProgressAsync();

        Assert.True(progress.HasCompany);
        Assert.False(progress.HasOwnerCompany);
    }

    [Fact]
    public async Task Customer_Alone_Counts_As_First_Party()
    {
        await using var db = CreateDb();
        db.Customers.Add(new Customer { Name = "Buyer" });
        await db.SaveChangesAsync();

        var progress = await new OnboardingService(db).GetProgressAsync();

        Assert.True(progress.HasParty);
    }

    [Fact]
    public async Task Progress_Is_Complete_Only_When_Every_Step_Has_Real_Data()
    {
        await using var db = CreateDb();
        db.Companies.Add(new Company { Id = 1, Code = "C1", Name = "Demo Co", IsSystemOwner = true });
        db.Products.Add(new Product { Id = 1, Code = "GO", Name = "Gas Oil" });
        db.Suppliers.Add(new Supplier { Id = 1, Name = "Seller" });
        db.Contracts.Add(new Contract { Id = 1, ContractName = "P-1", ContractNumber = "P-1", CompanyId = 1, ProductId = 1, SupplierId = 1 });
        await db.SaveChangesAsync();

        var beforeLoading = await new OnboardingService(db).GetProgressAsync();
        Assert.False(beforeLoading.IsComplete);

        db.LoadingRegisters.Add(new LoadingRegister { ContractId = 1, ProductId = 1, LoadedQuantityMt = 10m });
        await db.SaveChangesAsync();

        var afterLoading = await new OnboardingService(db).GetProgressAsync();
        Assert.True(afterLoading.IsComplete);
    }

    [Fact]
    public async Task Dashboard_Has_No_Onboarding_When_Disabled()
    {
        await using var db = CreateDb();
        var controller = CreateController(db, enabled: false, AuthRoles.Admin);

        var result = Assert.IsType<ViewResult>(await controller.Index());

        Assert.Null(result.ViewData["Onboarding"]);
    }

    [Fact]
    public async Task Dashboard_Has_No_Onboarding_For_Read_Only_User()
    {
        await using var db = CreateDb();
        var controller = CreateController(db, enabled: true, AuthRoles.Viewer);

        var result = Assert.IsType<ViewResult>(await controller.Index());

        Assert.Null(result.ViewData["Onboarding"]);
    }

    [Fact]
    public async Task Dashboard_Reads_Dismissed_Panels_From_The_User_Cookie()
    {
        await using var db = CreateDb();
        var controller = CreateController(db, enabled: true, AuthRoles.Admin, cookie: "ptg-onb-demo_user=setup");

        var result = Assert.IsType<ViewResult>(await controller.Index());

        var panel = Assert.IsType<OnboardingPanelViewModel>(result.ViewData["Onboarding"]);
        Assert.Equal("ptg-onb-demo_user", panel.StorageKey);
        Assert.True(panel.IsDismissed("setup"));
        Assert.False(panel.IsDismissed("tasks"));
    }

    [Fact]
    public async Task Guide_Link_Reopens_A_Dismissed_Panel()
    {
        await using var db = CreateDb();
        var controller = CreateController(db, enabled: true, AuthRoles.Admin, cookie: "ptg-onb-demo_user=setup.tasks");

        var result = Assert.IsType<ViewResult>(await controller.Index(guide: "1"));

        var panel = Assert.IsType<OnboardingPanelViewModel>(result.ViewData["Onboarding"]);
        Assert.False(panel.IsDismissed("setup"));
        Assert.False(panel.IsDismissed("tasks"));
    }

    [Theory]
    [InlineData("demo.user", "ptg-onb-demo_user")]
    [InlineData("ali-01", "ptg-onb-ali-01")]
    [InlineData(null, "ptg-onb-user")]
    public void Storage_Key_Is_A_Valid_Cookie_Name(string? userName, string expected)
        => Assert.Equal(expected, OnboardingPanelViewModel.BuildStorageKey(userName));

    private static HomeController CreateController(ApplicationDbContext db, bool enabled, string role, string? cookie = null)
    {
        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "Demo User"), new Claim(AppClaimTypes.Username, "demo.user"), new Claim(ClaimTypes.Role, role)],
            authenticationType: "Test");
        var httpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) };
        if (cookie is not null)
        {
            httpContext.Request.Headers.Cookie = cookie;
        }

        return new HomeController(
            new DashboardService(db, new HttpContextAccessor()),
            NullLogger<HomeController>.Instance,
            new OnboardingService(db),
            Options.Create(new OnboardingOptions { Enabled = enabled }))
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext }
        };
    }
}
