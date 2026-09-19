using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Infrastructure.Api;

namespace PTGOilSystem.Web.Security.Mobile;

/// <summary>
/// رویدادهای Bearer موبایل. پس از اعتبارسنجیِ امضا و انقضا، کاربر و نشست از دیتابیس خوانده
/// می‌شوند: کاربرِ غیرفعال یا نشستِ باطل‌شده فوراً رد می‌شود، و ادعاها با همان
/// <see cref="UserClaimsFactory"/> ورود وب ساخته می‌شوند. پاسخ 401/403 همیشه JSON است.
/// </summary>
public static class MobileJwtBearerEvents
{
    internal const string FailureCodeItemKey = "ptg:mobile-auth-failure";

    public static JwtBearerEvents Create() => new()
    {
        OnTokenValidated = OnTokenValidatedAsync,
        OnChallenge = OnChallengeAsync,
        OnForbidden = context => ApiProblem.WriteAsync(
            context.HttpContext,
            StatusCodes.Status403Forbidden,
            ApiErrorCodes.Forbidden,
            ApiProblem.ForbiddenMessage)
    };

    private static async Task OnTokenValidatedAsync(TokenValidatedContext context)
    {
        var principal = context.Principal;
        if (!int.TryParse(
                principal?.FindFirst(MobileClaimTypes.Subject)?.Value,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var userId)
            || !Guid.TryParse(principal?.FindFirst(MobileClaimTypes.SessionId)?.Value, out var sessionId))
        {
            Fail(context, ApiErrorCodes.Unauthorized, "Token is missing required identifiers.");
            return;
        }

        var services = context.HttpContext.RequestServices;
        var cancellationToken = context.HttpContext.RequestAborted;
        var db = services.GetRequiredService<ApplicationDbContext>();

        var user = await db.Users
            .AsNoTracking()
            .Include(u => u.Role)
            .FirstOrDefaultAsync(u => u.Id == userId && u.IsActive, cancellationToken);
        if (user is null)
        {
            Fail(context, ApiErrorCodes.Unauthorized, "User is missing or inactive.");
            return;
        }

        var tokens = services.GetRequiredService<IMobileTokenService>();
        if (!await tokens.IsSessionActiveAsync(userId, sessionId, cancellationToken))
        {
            Fail(context, ApiErrorCodes.SessionRevoked, "Mobile session is revoked or expired.");
            return;
        }

        var identity = new ClaimsIdentity(
            UserClaimsFactory.Build(user),
            JwtBearerDefaults.AuthenticationScheme,
            ClaimTypes.Name,
            ClaimTypes.Role);
        identity.AddClaim(new Claim(MobileClaimTypes.SessionId, sessionId.ToString("D")));
        context.Principal = new ClaimsPrincipal(identity);
    }

    private static void Fail(TokenValidatedContext context, string code, string reason)
    {
        context.HttpContext.Items[FailureCodeItemKey] = code;
        context.Fail(reason);
    }

    private static Task OnChallengeAsync(JwtBearerChallengeContext context)
    {
        context.HandleResponse();

        var code = context.AuthenticateFailure is SecurityTokenExpiredException
            ? ApiErrorCodes.TokenExpired
            : context.HttpContext.Items[FailureCodeItemKey] as string ?? ApiErrorCodes.Unauthorized;

        var detail = code switch
        {
            ApiErrorCodes.TokenExpired => ApiProblem.TokenExpiredMessage,
            ApiErrorCodes.SessionRevoked => ApiProblem.SessionRevokedMessage,
            _ => ApiProblem.UnauthorizedMessage
        };

        context.Response.Headers.WWWAuthenticate = "Bearer";
        return ApiProblem.WriteAsync(context.HttpContext, StatusCodes.Status401Unauthorized, code, detail);
    }
}
