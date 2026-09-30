using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using PTGOilSystem.Web.Controllers;
using PTGOilSystem.Web.Models.HumanResources;
using PTGOilSystem.Web.Security;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// گزارشِ «پرداخت‌های معاش» روی ستونِ timestamptz فیلتر می‌کند؛ بازهٔ پیش‌فرضِ ماه Kind=Unspecified
/// بود و Npgsql آن را رد می‌کرد. فقط PostgreSQL واقعی این خطا را نشان می‌دهد.
/// </summary>
[Trait("Category", "PostgreSql")]
[Trait("Category", "Integration")]
[Collection(CanonicalSearchPostgresCollection.CollectionName)]
public sealed class HrReportsPaymentsPostgresTests(CanonicalSearchPostgresFixture fixture)
{
    [Fact]
    public async Task PaymentsReport_DefaultMonthRange_RendersWithoutNpgsqlKindError()
    {
        Assert.True(fixture.Available, $"این آزمون به PostgreSQL واقعی نیاز دارد: {fixture.UnavailableReason}");

        await using var db = fixture.CreateDbContext();
        var controller = new HrReportsController(db)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        [new Claim(AppClaimTypes.Permission, AppPermissions.ViewEmployeeSalary)], "Test"))
                }
            }
        };

        var result = await controller.Index(tab: "payments");

        var model = Assert.IsType<HrReportViewModel>(Assert.IsType<ViewResult>(result).Model);
        Assert.Equal("payments", model.Report);
    }
}
