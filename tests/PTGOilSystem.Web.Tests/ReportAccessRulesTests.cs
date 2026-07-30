using System.Security.Claims;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Security;
using Xunit;

namespace PTGOilSystem.Web.Tests;

public class ReportAccessRulesTests
{
    [Theory]
    [InlineData("Reports", RoleNavigationKeys.Reports)]
    [InlineData("Reconciliation", RoleNavigationKeys.Reports)]
    [InlineData("InventoryReports", RoleNavigationKeys.Inventory)]
    [InlineData("PartyStatements", RoleNavigationKeys.Payments)]
    public void Report_Controller_Is_Explicitly_Mapped(string controller, string expectedKey)
        => Assert.Equal(expectedKey, RoleAccessRules.NavigationKeyForController(controller));

    [Fact]
    public void Direct_Url_Requires_The_Mapped_Navigation_Claim()
    {
        var reportsOnly = UserWithNavigation(RoleNavigationKeys.Reports);

        Assert.True(RoleAccessRules.CanAccessController(reportsOnly, "Reports"));
        Assert.True(RoleAccessRules.CanAccessController(reportsOnly, "Reconciliation"));
        Assert.False(RoleAccessRules.CanAccessController(reportsOnly, "InventoryReports"));
        Assert.False(RoleAccessRules.CanAccessController(reportsOnly, "PartyStatements"));
    }

    [Fact]
    public void Unknown_Controller_Is_Denied_By_Default()
        => Assert.False(RoleAccessRules.CanAccessController(
            UserWithNavigation(RoleNavigationKeys.Reports),
            "UnmappedSensitiveReport"));

    private static ClaimsPrincipal UserWithNavigation(params string[] keys)
        => new(new ClaimsIdentity(
            keys.Select(key => new Claim(AppClaimTypes.AllowedNavigation, key))
                .Append(new Claim(ClaimTypes.Role, AuthRoles.Viewer)),
            "test"));
}
