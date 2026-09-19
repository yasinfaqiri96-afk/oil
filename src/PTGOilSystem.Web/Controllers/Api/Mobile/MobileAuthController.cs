using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using PTGOilSystem.Web.Infrastructure.Api;
using PTGOilSystem.Web.Infrastructure.RateLimiting;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.Mobile;
using PTGOilSystem.Web.Security;
using PTGOilSystem.Web.Security.Mobile;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Mobile;

namespace PTGOilSystem.Web.Controllers.Api.Mobile;

/// <summary>
/// ورود، تازه‌سازی و خروج موبایل. اعتبارسنجی رمز همان <see cref="IUserService.VerifyPasswordAsync"/>
/// وب است (فقط کاربر فعال)، قفل موقت همان <see cref="ILoginAttemptGuard"/> و رویدادهای ورود با همان
/// Actionهای <see cref="LoginAuditActions"/> ثبت می‌شوند؛ پس تلاش ناموفق وب و موبایل یک شمارش دارند.
/// </summary>
[Route("api/mobile/v1/auth")]
public sealed class MobileAuthController(
    IUserService users,
    ILoginAttemptGuard loginAttemptGuard,
    IMobileTokenService tokens,
    IMobileUserProfileService profiles,
    IAuditService audit,
    MobileJwtKeyProvider keys,
    ILogger<MobileAuthController> logger) : MobileApiControllerBase
{
    internal const string ModuleName = "MobileAuth";
    internal const string RefreshTokenReuseAction = "MobileRefreshTokenReuse";
    internal const string LogoutAction = "MobileLogout";

    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.Login)]
    public async Task<IActionResult> Login([FromBody] MobileLoginRequest request, CancellationToken cancellationToken)
    {
        ApplyNoStore();
        if (!keys.CanIssueTokens)
        {
            logger.LogError("Mobile login rejected: the MobileAuth signing key is not configured.");
            return MobileAuthUnavailable();
        }

        if (keys.Status == MobileSigningKeyStatus.DevelopmentEphemeral)
        {
            logger.LogWarning("Mobile tokens are signed with a temporary development key and become invalid on restart.");
        }

        var username = request.Username.Trim();
        var ipAddress = ClientIpAddress();

        var attemptStatus = await loginAttemptGuard.GetStatusAsync(username, ipAddress, cancellationToken);
        if (attemptStatus.IsLocked)
        {
            await AuditAsync(LoginAuditActions.Locked, username, "درخواست ورود موبایل در زمان قفل موقت رد شد.",
                StatusCodes.Status429TooManyRequests, cancellationToken: cancellationToken);
            return LockedResponse();
        }

        var user = await users.VerifyPasswordAsync(username, request.Password, cancellationToken);
        if (user is null)
        {
            await AuditAsync(LoginAuditActions.Failed, username, "تلاش ناموفق ورود موبایل.",
                StatusCodes.Status401Unauthorized, cancellationToken: cancellationToken);

            attemptStatus = await loginAttemptGuard.GetStatusAsync(username, ipAddress, cancellationToken);
            if (attemptStatus.IsLocked)
            {
                await AuditAsync(LoginAuditActions.Locked, username, "حفاظت ورود موبایل پس از چند تلاش ناموفق فعال شد.",
                    StatusCodes.Status429TooManyRequests, cancellationToken: cancellationToken);
                return LockedResponse();
            }

            // کاربر غیرفعال و رمز نادرست یک پاسخ دارند تا وجود حساب فاش نشود.
            return ApiError(StatusCodes.Status401Unauthorized, ApiErrorCodes.InvalidCredentials,
                "نام کاربری یا رمز عبور نادرست است.");
        }

        var pair = await tokens.IssueForLoginAsync(
            user,
            new MobileClientInfo(request.DeviceId, request.DeviceName, ipAddress),
            cancellationToken);

        await AuditAsync(LoginAuditActions.Succeeded, user.Username,
            $"ورود موفق موبایل با نقش {UserClaimsFactory.ResolveRoleName(user.Role?.Name)}.",
            StatusCodes.Status200OK, isSuccess: true, actorUserId: user.Id, entityId: user.Id,
            cancellationToken: cancellationToken);

        logger.LogInformation("User '{Username}' signed in from the mobile app.", user.Username);
        return Ok(await BuildAuthResponseAsync(user, pair, cancellationToken));
    }

    [HttpPost("refresh")]
    [AllowAnonymous]
    public async Task<IActionResult> Refresh([FromBody] MobileRefreshRequest request, CancellationToken cancellationToken)
    {
        ApplyNoStore();
        if (!keys.CanIssueTokens)
        {
            return MobileAuthUnavailable();
        }

        var result = await tokens.RefreshAsync(
            request.RefreshToken,
            new MobileClientInfo(request.DeviceId, null, ClientIpAddress()),
            cancellationToken);

        switch (result.Status)
        {
            case MobileRefreshStatus.Success:
                return Ok(await BuildAuthResponseAsync(result.User!, result.Tokens!, cancellationToken));

            case MobileRefreshStatus.ReuseDetected:
                logger.LogWarning(
                    "Mobile refresh token reuse detected for user {UserId}; session {SessionId} revoked.",
                    result.User?.Id,
                    result.SessionId);
                await AuditAsync(RefreshTokenReuseAction, result.User?.Username,
                    "استفادهٔ دوباره از توکن تازه‌سازیِ چرخیده؛ کل نشست موبایل باطل شد.",
                    StatusCodes.Status401Unauthorized, actorUserId: result.User?.Id, entityId: result.User?.Id ?? 0,
                    category: AuditLogCategories.Security, cancellationToken: cancellationToken);
                return ApiError(StatusCodes.Status401Unauthorized, ApiErrorCodes.RefreshTokenReused,
                    "این نشست به دلیل امنیتی باطل شد. دوباره وارد شوید.");

            default:
                return ApiError(StatusCodes.Status401Unauthorized, ApiErrorCodes.RefreshTokenInvalid,
                    "نشست موبایل معتبر نیست. دوباره وارد شوید.");
        }
    }

    /// <summary>
    /// نشستِ همین گوشی را باطل می‌کند — با توکن دسترسیِ معتبر، یا با توکن تازه‌سازی وقتی توکن دسترسی
    /// منقضی شده است. پاسخ همیشه 204 است تا وجود یا نبودِ نشست فاش نشود.
    /// </summary>
    [HttpPost("logout")]
    [AllowAnonymous]
    public async Task<IActionResult> Logout([FromBody] MobileLogoutRequest? request, CancellationToken cancellationToken)
    {
        ApplyNoStore();
        var ipAddress = ClientIpAddress();
        int? userId = null;
        string? username = null;

        var bearer = await HttpContext.AuthenticateAsync(JwtBearerDefaults.AuthenticationScheme);
        if (bearer.Succeeded
            && int.TryParse(bearer.Principal!.FindFirstValue(ClaimTypes.NameIdentifier), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var bearerUserId)
            && Guid.TryParse(bearer.Principal.FindFirstValue(MobileClaimTypes.SessionId), out var sessionId))
        {
            await tokens.RevokeSessionAsync(bearerUserId, sessionId, MobileRefreshTokenRevocationReasons.Logout,
                ipAddress, cancellationToken);
            userId = bearerUserId;
            username = bearer.Principal.FindFirstValue(AppClaimTypes.Username);
        }

        if (!string.IsNullOrWhiteSpace(request?.RefreshToken))
        {
            var revoked = await tokens.RevokeByRefreshTokenAsync(request.RefreshToken,
                MobileRefreshTokenRevocationReasons.Logout, ipAddress, cancellationToken);
            userId ??= revoked?.UserId;
        }

        if (userId is not null)
        {
            await AuditAsync(LogoutAction, username, "کاربر از نشست موبایل خارج شد.",
                StatusCodes.Status204NoContent, isSuccess: true, actorUserId: userId, entityId: userId.Value,
                cancellationToken: cancellationToken);
        }

        return NoContent();
    }

    private async Task<MobileAuthResponse> BuildAuthResponseAsync(User user, MobileTokenPair pair, CancellationToken cancellationToken)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            UserClaimsFactory.Build(user),
            JwtBearerDefaults.AuthenticationScheme,
            ClaimTypes.Name,
            ClaimTypes.Role));

        return new MobileAuthResponse
        {
            AccessToken = pair.AccessToken,
            AccessTokenExpiresAtUtc = pair.AccessTokenExpiresAtUtc,
            RefreshToken = pair.RefreshToken,
            RefreshTokenExpiresAtUtc = pair.RefreshTokenExpiresAtUtc,
            User = await profiles.BuildAsync(principal, cancellationToken)
        };
    }

    private ObjectResult LockedResponse()
        => ApiError(StatusCodes.Status429TooManyRequests, ApiErrorCodes.AccountLocked,
            "ورود موقتاً محدود شده است. لطفاً ۱۵ دقیقه بعد دوباره تلاش کنید.");

    private ObjectResult MobileAuthUnavailable()
        => ApiError(StatusCodes.Status503ServiceUnavailable, ApiErrorCodes.MobileAuthUnavailable,
            "ورود موبایل روی این سرور فعال نیست.");

    private Task AuditAsync(
        string action,
        string? username,
        string description,
        int statusCode,
        bool isSuccess = false,
        int? actorUserId = null,
        int entityId = 0,
        string category = AuditLogCategories.Authentication,
        CancellationToken cancellationToken = default)
        => audit.LogActivityAndSaveAsync(new AuditLogEntryInput
        {
            Category = category,
            EntityName = nameof(User),
            EntityId = entityId,
            Action = action,
            ActorUserId = actorUserId,
            ActorUsername = string.IsNullOrWhiteSpace(username) ? null : username.Trim(),
            Module = ModuleName,
            Description = description,
            HttpMethod = Request.Method,
            RequestPath = Request.Path.Value,
            ControllerName = ModuleName,
            ActionName = ControllerContext.ActionDescriptor?.ActionName,
            StatusCode = statusCode,
            IsSuccess = isSuccess,
            CorrelationId = HttpContext.TraceIdentifier,
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString(),
            UserAgent = Request.Headers.UserAgent.ToString()
        }, cancellationToken);
}
