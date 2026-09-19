using Microsoft.AspNetCore.Mvc;
using PTGOilSystem.Web.Models.Mobile;
using PTGOilSystem.Web.Services.Mobile;

namespace PTGOilSystem.Web.Controllers.Api.Mobile;

[Route("api/mobile/v1/me")]
public sealed class MobileMeController(IMobileUserProfileService profiles) : MobileApiControllerBase
{
    [HttpGet]
    public async Task<ActionResult<MobileMeResponse>> Get(CancellationToken cancellationToken)
        => Ok(await profiles.BuildAsync(User, cancellationToken));
}
