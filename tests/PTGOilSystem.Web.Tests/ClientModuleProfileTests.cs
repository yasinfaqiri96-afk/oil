using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using PTGOilSystem.Web.Configuration;
using PTGOilSystem.Web.Security;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// Client Profile فقط ماژول‌ها را برای یک Instance مخفی می‌کند؛ بدون Profile رفتار سیستم کامل
/// دقیقاً همان قبلی است و Profile جای Role/Permission را نمی‌گیرد.
/// </summary>
public class ClientModuleProfileTests
{
    private static string WebProjectFile(string fileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "PTGOilSystem.Web", fileName);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }

        throw new FileNotFoundException(fileName);
    }

    private static ClientProfileOptions LoadOptions(params string[] files)
    {
        var builder = new ConfigurationBuilder();
        foreach (var file in files) builder.AddJsonFile(WebProjectFile(file), optional: false);
        var options = new ClientProfileOptions();
        builder.Build().GetSection(ClientProfileOptions.SectionName).Bind(options);
        return options;
    }

    private static ClientModuleProfile FarjadProfile()
        => new(LoadOptions("appsettings.json", "appsettings.Farjad.json"));

    private static ClaimsPrincipal SignedInUser(string role, params string[] navigationKeys)
    {
        var claims = new List<Claim> { new(ClaimTypes.Name, "tester"), new(ClaimTypes.Role, role) };
        claims.AddRange(navigationKeys.Select(key => new Claim(AppClaimTypes.AllowedNavigation, key)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "TestAuth"));
    }

    private static async Task<ActionExecutingContext> RunFilterAsync(
        ClientModuleProfile? profile, string controller, ClaimsPrincipal user)
    {
        var routeData = new RouteData();
        routeData.Values["controller"] = controller;
        var context = new ActionExecutingContext(
            new ActionContext(new DefaultHttpContext { User = user }, routeData, new ActionDescriptor()),
            new List<IFilterMetadata>(),
            new Dictionary<string, object?>(),
            controller: new object());
        await new RoleNavigationAuthorizationFilter(profile).OnActionExecutionAsync(context, () =>
            Task.FromResult(new ActionExecutedContext(context, new List<IFilterMetadata>(), new object())));
        return context;
    }

    [Fact]
    public void Base_Appsettings_Keep_The_Full_System_Unchanged()
    {
        var options = LoadOptions("appsettings.json");
        var profile = new ClientModuleProfile(options);

        Assert.False(options.SimplePurchaseEnabled);
        Assert.Null(profile.Name);
        Assert.False(profile.HasRestrictions);
        foreach (var controller in RoleAccessRules.NavigationItems.SelectMany(item => item.Controllers))
        {
            Assert.True(profile.IsControllerEnabled(controller), controller);
        }
    }

    [Fact]
    public void Farjad_Profile_Enables_Simple_Purchase_And_Hides_Advanced_Modules()
    {
        var profile = FarjadProfile();

        Assert.Equal("Farjad", profile.Name);
        Assert.True(profile.SimplePurchaseEnabled);
        foreach (var hidden in new[]
                 {
                     "Shipments",
                     "ShipmentPnl", "Dispatch", "CustomsDeclarations", "QualityInspections", "Wagons", "Vessels",
                     "Partners", "Sarrafs", "SarrafSettlements", "Employees", "Payroll", "OperationalAssets", "PlattsRates"
                 })
        {
            Assert.False(profile.IsControllerEnabled(hidden), hidden);
        }

        foreach (var visible in new[]
                 {
                     "Loading", "LoadingExcelImport", "LoadingReceipts", "InventoryTransportLegs",
                     "InventoryTransportReceipts", "InventoryLineage", "Transports",
                     "Home", "Contracts", "Inventory", "InventoryReports", "StorageTanks", "Sales", "Payments",
                     "PartySettlements", "PartyStatements", "Customers", "Suppliers", "ServiceProviders", "Drivers",
                     "Trucks", "Expenses", "LossEvents", "Reports", "Products", "Units", "Currencies", "DailyFxRates",
                     "Locations", "ExpenseTypes", "Users", "Roles", "CashAccounts", "Ledger", "FiscalYears"
                 })
        {
            Assert.True(profile.IsControllerEnabled(visible), visible);
        }
    }

    [Fact]
    public async Task Hidden_Module_Direct_Url_Is_Blocked_Even_For_Admin()
    {
        var context = await RunFilterAsync(FarjadProfile(), "Shipments", SignedInUser(AuthRoles.Admin));

        var redirect = Assert.IsType<RedirectToActionResult>(context.Result);
        Assert.Equal("AccessDenied", redirect.ActionName);
    }

    [Theory]
    [InlineData("Loading")]
    [InlineData("LoadingReceipts")]
    [InlineData("InventoryTransportLegs")]
    [InlineData("InventoryTransportReceipts")]
    [InlineData("Transports")]
    public async Task Farjad_Operational_Controllers_Are_Open_For_Admin(string controller)
    {
        var context = await RunFilterAsync(FarjadProfile(), controller, SignedInUser(AuthRoles.Admin));
        Assert.Null(context.Result);
    }

    [Fact]
    public async Task Same_Hidden_Module_Stays_Open_Without_A_Profile()
    {
        var context = await RunFilterAsync(null, "Loading", SignedInUser(AuthRoles.Admin));

        Assert.Null(context.Result);
    }

    [Fact]
    public async Task Enabled_Module_Still_Requires_Role_Navigation()
    {
        var profile = FarjadProfile();

        var denied = await RunFilterAsync(profile, "Sales", SignedInUser("Clerk", RoleNavigationKeys.Payments));
        var allowed = await RunFilterAsync(profile, "Sales", SignedInUser("Clerk", RoleNavigationKeys.Sales));

        Assert.IsType<RedirectToActionResult>(denied.Result);
        Assert.Null(allowed.Result);
    }

    [Theory]
    [InlineData("Farjad", true)]
    [InlineData("farjad-simple_1", true)]
    [InlineData("../secrets", false)]
    [InlineData("a/b", false)]
    [InlineData("", false)]
    public void Profile_Name_Is_Restricted_To_Safe_File_Names(string name, bool expected)
        => Assert.Equal(expected, ClientModuleProfile.IsValidProfileName(name));
}
