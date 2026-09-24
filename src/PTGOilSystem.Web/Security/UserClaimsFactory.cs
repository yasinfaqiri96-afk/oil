using System.Globalization;
using System.Security.Claims;
using PTGOilSystem.Web.Models.Entities;

namespace PTGOilSystem.Web.Security;

/// <summary>
/// ادعاهای کاربرِ واردشده — یک منبع برای ورود وب (کوکی) و API موبایل (JWT)، تا سامانهٔ
/// دسترسیِ موازی ساخته نشود. هر دو دقیقاً همین نقش، کلیدهای ناوبری و Permission را می‌بینند.
/// </summary>
public static class UserClaimsFactory
{
    public static List<Claim> Build(User user)
    {
        ArgumentNullException.ThrowIfNull(user);

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString(CultureInfo.InvariantCulture)),
            new(ClaimTypes.Name, user.FullName),
            new(AppClaimTypes.Username, user.Username),
            new(ClaimTypes.Role, ResolveRoleName(user.Role?.Name)),
        };

        if (!string.IsNullOrWhiteSpace(user.Email))
            claims.Add(new Claim(ClaimTypes.Email, user.Email));

        foreach (var navigationKey in RoleAccessRules.ResolveNavigationForRole(user.Role))
        {
            claims.Add(new Claim(AppClaimTypes.AllowedNavigation, navigationKey));
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

        return claims;
    }

    public static string ResolveRoleName(string? roleName)
        => string.IsNullOrWhiteSpace(roleName)
            ? AuthRoles.Viewer
            : roleName.Trim();
}
