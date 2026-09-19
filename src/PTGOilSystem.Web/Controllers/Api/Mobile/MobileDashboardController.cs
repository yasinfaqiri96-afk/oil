using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using PTGOilSystem.Web.Infrastructure.RateLimiting;
using PTGOilSystem.Web.Models.Mobile;
using PTGOilSystem.Web.Services.Mobile;

namespace PTGOilSystem.Web.Controllers.Api.Mobile;

[Route("api/mobile/v1/dashboard")]
public sealed class MobileDashboardController(IMobileDashboardService dashboard) : MobileApiControllerBase
{
    [HttpGet]
    [EnableRateLimiting(RateLimitPolicies.HeavyReport)]
    public async Task<ActionResult<MobileDashboardResponse>> Get(CancellationToken cancellationToken)
        => Ok(await dashboard.BuildAsync(User, cancellationToken));
}
