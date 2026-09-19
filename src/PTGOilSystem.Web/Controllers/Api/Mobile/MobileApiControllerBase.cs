using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PTGOilSystem.Web.Infrastructure.Api;
using PTGOilSystem.Web.Security;

namespace PTGOilSystem.Web.Controllers.Api.Mobile;

/// <summary>
/// پایهٔ کنترلرهای <c>/api/mobile/v1</c>: فقط Bearer، کلید ناوبری صریح و خطای ProblemDetails.
/// کنترلرها نازک‌اند؛ محاسبه در سرویس‌های مرجع است.
/// </summary>
[ApiController]
[Authorize(Policy = AuthPolicies.MobileApi)]
[ApiNavigation(RoleNavigationKeys.Dashboard)]
[TypeFilter(typeof(ApiExceptionFilter))]
public abstract class MobileApiControllerBase : ControllerBase
{
    protected ObjectResult ApiError(int status, string code, string detail)
        => ApiProblem.Result(HttpContext, status, code, detail);

    protected string ClientIpAddress()
        => HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    protected void ApplyNoStore()
    {
        Response.Headers.CacheControl = "no-store";
        Response.Headers.Pragma = "no-cache";
    }
}
