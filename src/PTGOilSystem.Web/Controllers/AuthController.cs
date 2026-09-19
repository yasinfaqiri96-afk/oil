using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using PTGOilSystem.Web.Infrastructure.RateLimiting;
using PTGOilSystem.Web.Models.Auth;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Security;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Exceptions;

namespace PTGOilSystem.Web.Controllers;

public class AuthController : Controller
{
    private readonly IUserService _users;
    private readonly IAuditService? _audit;
    private readonly ILoginAttemptGuard _loginAttemptGuard;
    private readonly ILogger<AuthController> _logger;

    public AuthController(IUserService users, ILogger<AuthController> logger)
        : this(users, null, AllowAllLoginAttemptGuard.Instance, logger)
    {
    }

    public AuthController(IUserService users, IAuditService? audit, ILogger<AuthController> logger)
        : this(users, audit, AllowAllLoginAttemptGuard.Instance, logger)
    {
    }

    [ActivatorUtilitiesConstructor]
    public AuthController(
        IUserService users,
        IAuditService? audit,
        ILoginAttemptGuard loginAttemptGuard,
        ILogger<AuthController> logger)
    {
        _users = users;
        _audit = audit;
        _loginAttemptGuard = loginAttemptGuard;
        _logger = logger;
    }

    [AllowAnonymous]
    public IActionResult Login(string? returnUrl = null)
    {
        ApplyLoginResponseHeaders();

        if (User.Identity?.IsAuthenticated == true)
            return RedirectToLocal(returnUrl);

        return View(new LoginViewModel { ReturnUrl = returnUrl });
    }

    [HttpPost, ValidateAntiForgeryToken]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.Login)]
    public async Task<IActionResult> Login(
        LoginViewModel model,
        CancellationToken cancellationToken = default)
    {
        ApplyLoginResponseHeaders();

        if (!ModelState.IsValid)
            return View(model);

        var attemptedUsername = model.Username.Trim();
        var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var attemptStatus = await _loginAttemptGuard.GetStatusAsync(
            attemptedUsername,
            ipAddress,
            cancellationToken);

        if (attemptStatus.IsLocked)
        {
            await LogAuthenticationEventAsync(
                LoginAuditActions.Locked,
                attemptedUsername,
                "درخواست ورود در زمان قفل موقت رد شد.",
                StatusCodes.Status429TooManyRequests,
                cancellationToken: cancellationToken);

            return LockedLoginView(model);
        }

        var user = await _users.VerifyPasswordAsync(
            attemptedUsername,
            model.Password,
            cancellationToken);
        if (user is null)
        {
            await LogAuthenticationEventAsync(
                LoginAuditActions.Failed,
                attemptedUsername,
                "تلاش ناموفق ورود به سیستم.",
                StatusCodes.Status401Unauthorized,
                cancellationToken: cancellationToken);

            attemptStatus = await _loginAttemptGuard.GetStatusAsync(
                attemptedUsername,
                ipAddress,
                cancellationToken);

            if (attemptStatus.IsLocked)
            {
                await LogAuthenticationEventAsync(
                    LoginAuditActions.Locked,
                    attemptedUsername,
                    "حفاظت ورود پس از چند تلاش ناموفق فعال شد.",
                    StatusCodes.Status429TooManyRequests,
                    cancellationToken: cancellationToken);

                return LockedLoginView(model);
            }

            ModelState.AddModelError(string.Empty, "نام کاربری یا رمز عبور نادرست است.");
            model.Password = string.Empty;
            return View(model);
        }

        var roleName = ResolveRoleName(user.Role?.Name);
        // همان ادعاهایی که API موبایل برای Bearer می‌سازد — یک منبع، بدون سامانهٔ دسترسی موازی.
        var claims = UserClaimsFactory.Build(user);

        var principal = new ClaimsPrincipal(
            new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));

        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            principal,
            new AuthenticationProperties
            {
                IsPersistent = false,
                AllowRefresh = true,
                ExpiresUtc = DateTimeOffset.UtcNow.AddHours(12)
            });

        await LogAuthenticationEventAsync(
            LoginAuditActions.Succeeded,
            user.Username,
            $"ورود موفق کاربر با نقش {roleName}.",
            StatusCodes.Status200OK,
            isSuccess: true,
            actorUserId: user.Id,
            entityId: user.Id,
            cancellationToken: cancellationToken);

        _logger.LogInformation("User '{Username}' logged in with role '{RoleName}'.", user.Username, roleName);
        TempData["ok"] = "ورود با موفقیت انجام شد.";
        return RedirectToLocal(model.ReturnUrl);
    }

    [Authorize]
    public IActionResult ChangePassword() => View(new ChangePasswordViewModel());

    [HttpPost, ValidateAntiForgeryToken]
    [Authorize]
    public async Task<IActionResult> ChangePassword(ChangePasswordViewModel model)
    {
        if (!ModelState.IsValid)
            return View(model);

        var rawUserId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!int.TryParse(rawUserId, out var userId))
            return Forbid();

        try
        {
            await _users.ChangePasswordAsync(userId, model.CurrentPassword, model.NewPassword);

            if (_audit is not null)
            {
                var username = User.FindFirstValue(AppClaimTypes.Username) ?? User.Identity?.Name ?? "";
                await _audit.LogAsync(
                    nameof(User),
                    userId,
                    AuditAction.Update,
                    diff: $"PasswordChangedByCurrentUser: Username={username}");

                await _audit.LogActivityAndSaveAsync(BuildAuthAuditEntry(
                    action: "ChangePassword",
                    actorUserId: userId,
                    username: username,
                    entityId: userId,
                    description: "کاربر رمز عبور خود را تغییر داد.",
                    isSuccess: true,
                    statusCode: 200));
            }

            TempData["ok"] = "رمز عبور با موفقیت تغییر کرد.";
            return RedirectToAction(nameof(ChangePassword));
        }
        catch (BusinessRuleException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            return View(model);
        }
    }

    [HttpPost, ValidateAntiForgeryToken]
    [Authorize]
    public async Task<IActionResult> Logout()
    {
        if (_audit is not null)
        {
            var rawUserId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            int.TryParse(rawUserId, out var userId);
            var username = User.FindFirstValue(AppClaimTypes.Username) ?? User.Identity?.Name ?? "";

            await _audit.LogActivityAndSaveAsync(BuildAuthAuditEntry(
                action: "Logout",
                actorUserId: userId > 0 ? userId : null,
                username: username,
                entityId: userId,
                description: "کاربر از سیستم خارج شد.",
                isSuccess: true,
                statusCode: 200));
        }

        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        TempData["ok"] = "خروج با موفقیت انجام شد.";
        return RedirectToAction(nameof(Login));
    }

    [AllowAnonymous]
    public async Task<IActionResult> AccessDenied()
    {
        if (_audit is not null && User.Identity?.IsAuthenticated == true)
        {
            var rawUserId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            int.TryParse(rawUserId, out var userId);
            var username = User.FindFirstValue(AppClaimTypes.Username) ?? User.Identity?.Name ?? "";

            await _audit.LogActivityAndSaveAsync(BuildAuthAuditEntry(
                action: "AccessDenied",
                category: AuditLogCategories.Security,
                actorUserId: userId > 0 ? userId : null,
                username: username,
                entityId: userId,
                description: "کاربر به عملیات یا صفحه غیرمجاز دسترسی خواست.",
                isSuccess: false,
                statusCode: 403));
        }

        return View();
    }

    private IActionResult RedirectToLocal(string? returnUrl)
    {
        if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
            return LocalRedirect(returnUrl);

        return RedirectToAction("Index", "Home");
    }

    private IActionResult LockedLoginView(LoginViewModel model)
    {
        Response.StatusCode = StatusCodes.Status429TooManyRequests;
        ModelState.AddModelError(
            string.Empty,
            "ورود موقتاً محدود شده است. لطفاً ۱۵ دقیقه بعد دوباره تلاش کنید.");
        model.Password = string.Empty;
        return View("Login", model);
    }

    private async Task LogAuthenticationEventAsync(
        string action,
        string? username,
        string description,
        int statusCode,
        bool isSuccess = false,
        int? actorUserId = null,
        int entityId = 0,
        CancellationToken cancellationToken = default)
    {
        if (_audit is null)
            return;

        await _audit.LogActivityAndSaveAsync(BuildAuthAuditEntry(
            action: action,
            actorUserId: actorUserId,
            username: username,
            entityId: entityId,
            description: description,
            isSuccess: isSuccess,
            statusCode: statusCode), cancellationToken);
    }

    private void ApplyLoginResponseHeaders()
    {
        Response.Headers.CacheControl = "no-store, no-cache";
        Response.Headers.Pragma = "no-cache";
        Response.Headers.Expires = "0";
        Response.Headers.XFrameOptions = "DENY";
    }

    private static string ResolveRoleName(string? roleName)
        => string.IsNullOrWhiteSpace(roleName)
            ? AuthRoles.Viewer
            : roleName.Trim();

    private AuditLogEntryInput BuildAuthAuditEntry(
        string action,
        string? username,
        string description,
        bool isSuccess,
        int statusCode,
        string category = AuditLogCategories.Authentication,
        int? actorUserId = null,
        int entityId = 0)
    {
        var httpContext = HttpContext;

        return new AuditLogEntryInput
        {
            Category = category,
            EntityName = nameof(User),
            EntityId = entityId,
            Action = action,
            ActorUserId = actorUserId,
            ActorUsername = string.IsNullOrWhiteSpace(username) ? null : username.Trim(),
            Module = nameof(AuthController).Replace("Controller", string.Empty, StringComparison.Ordinal),
            Description = description,
            HttpMethod = httpContext?.Request.Method,
            RequestPath = httpContext?.Request.Path.Value,
            ControllerName = nameof(AuthController).Replace("Controller", string.Empty, StringComparison.Ordinal),
            ActionName = ControllerContext?.ActionDescriptor?.ActionName,
            StatusCode = statusCode,
            IsSuccess = isSuccess,
            CorrelationId = httpContext?.TraceIdentifier,
            IpAddress = httpContext?.Connection.RemoteIpAddress?.ToString(),
            UserAgent = httpContext?.Request.Headers.UserAgent.ToString(),
        };
    }
}
