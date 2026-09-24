using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Security;

namespace PTGOilSystem.Web.Middleware;

public sealed class DevAutoSignInMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IWebHostEnvironment _env;
    // Do not inject scoped ApplicationDbContext here. Resolve per-request from HttpContext.RequestServices.
    private readonly IConfiguration _config;
    private readonly ILogger<DevAutoSignInMiddleware> _logger;

    public DevAutoSignInMiddleware(
        RequestDelegate next,
        IWebHostEnvironment env,
        IConfiguration config,
        ILogger<DevAutoSignInMiddleware> logger)
    {
        _next = next;
        _env = env;
        _config = config;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // API هرگز به‌طور ضمنی وارد نمی‌شود؛ فقط مسیر صریح احراز هویت API (Bearer) معتبر است.
        if (!_env.IsDevelopment() || PTGOilSystem.Web.Infrastructure.Api.ApiRequest.IsApi(context))
        {
            await _next(context);
            return;
        }

        try
        {
            var autoSignin = _config["PTG_DEV_AUTO_SIGNIN"];
            if (string.Equals(autoSignin, "false", StringComparison.OrdinalIgnoreCase)
                || string.Equals(autoSignin, "0", StringComparison.OrdinalIgnoreCase))
            {
                await _next(context);
                return;
            }

            if (context.User?.Identity?.IsAuthenticated == true)
            {
                await _next(context);
                return;
            }

            // Only attempt auto sign-in for browser navigations to avoid signing
            // in for static assets or API requests.
            if (!string.Equals(context.Request.Method, "GET", StringComparison.OrdinalIgnoreCase)
                || !context.Request.Headers.TryGetValue("Accept", out var accept)
                || !accept.ToString().Contains("text/html"))
            {
                await _next(context);
                return;
            }


            var username = _config["PTG_BOOTSTRAP_ADMIN_USERNAME"] ?? "admin";
            var db = context.RequestServices.GetService(typeof(PTGOilSystem.Web.Data.ApplicationDbContext)) as PTGOilSystem.Web.Data.ApplicationDbContext;
            if (db is null)
            {
                _logger.LogDebug("DevAutoSignIn: ApplicationDbContext not available in RequestServices.");
                await _next(context);
                return;
            }

            var user = await db.Users.Include(u => u.Role)
                .FirstOrDefaultAsync(u => u.Username == username && u.IsActive);

            if (user is null)
            {
                _logger.LogDebug("DevAutoSignIn: admin user '{Username}' not found.", username);
                await _next(context);
                return;
            }

            var roleName = string.IsNullOrWhiteSpace(user.Role?.Name) ? AuthRoles.Viewer : user.Role.Name.Trim();

            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, user.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                new(ClaimTypes.Name, user.FullName),
                new(AppClaimTypes.Username, user.Username),
                new(ClaimTypes.Role, roleName),
            };

            if (!string.IsNullOrWhiteSpace(user.Email))
                claims.Add(new Claim(ClaimTypes.Email, user.Email));

            foreach (var nav in RoleAccessRules.ResolveNavigationForRole(user.Role))
            {
                claims.Add(new Claim(AppClaimTypes.AllowedNavigation, nav));
            }

            if (RoleAccessRules.RoleCanManageData(user.Role))
            {
                claims.Add(new Claim(AppClaimTypes.Permission, AppPermissions.ManageData));
            }

            if (RoleAccessRules.RoleCanManageUsers(user.Role))
            {
                claims.Add(new Claim(AppClaimTypes.Permission, AppPermissions.ManageUsers));
            }

            foreach (var permission in RoleAccessRules.ResolveGrantedPermissions(user.Role))
            {
                claims.Add(new Claim(AppClaimTypes.Permission, permission));
            }

            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));

            await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal,
                new AuthenticationProperties { IsPersistent = false });

            _logger.LogDebug("DevAutoSignIn: signed in '{Username}' as '{Role}'.", user.Username, roleName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DevAutoSignIn: error during automatic sign-in.");
        }

        await _next(context);
    }
}
