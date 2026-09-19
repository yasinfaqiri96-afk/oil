using System.Globalization;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Mobile;
using PTGOilSystem.Web.Security;
using PTGOilSystem.Web.Services.Accounting;

namespace PTGOilSystem.Web.Services.Mobile;

public interface IMobileUserProfileService
{
    Task<MobileMeResponse> BuildAsync(ClaimsPrincipal user, CancellationToken ct = default);
}

/// <summary>
/// پروفایل موبایل از ادعاهای کاربرِ احرازشده (همان ادعاهای ورود وب) و شرکت مالک سیستم.
/// کاربر در این سیستم به شرکت یا شعبه وصل نیست؛ فقط یک شرکت مالک وجود دارد.
/// </summary>
public sealed class MobileUserProfileService(
    ApplicationDbContext db,
    ISystemCompanyProvider systemCompany,
    ILogger<MobileUserProfileService> logger) : IMobileUserProfileService
{
    public async Task<MobileMeResponse> BuildAsync(ClaimsPrincipal user, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(user);

        int.TryParse(
            user.FindFirstValue(ClaimTypes.NameIdentifier),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var userId);
        var navigation = RoleAccessRules.AllowedNavigationForUser(user);

        return new MobileMeResponse
        {
            UserId = userId,
            Username = user.FindFirstValue(AppClaimTypes.Username) ?? "",
            DisplayName = user.Identity?.Name ?? "",
            Role = user.FindFirstValue(ClaimTypes.Role) ?? "",
            Permissions = user.FindAll(AppClaimTypes.Permission)
                .Select(claim => claim.Value)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray(),
            Navigation = RoleAccessRules.NormalizeNavigation(navigation),
            Capabilities = new MobileCapabilities
            {
                Dashboard = navigation.Contains(RoleNavigationKeys.Dashboard),
                Operations = navigation.Contains(RoleNavigationKeys.Operations),
                Inventory = navigation.Contains(RoleNavigationKeys.Inventory),
                Sales = navigation.Contains(RoleNavigationKeys.Sales),
                Finance = navigation.Contains(RoleNavigationKeys.CashAccounts)
                    || navigation.Contains(RoleNavigationKeys.Payments),
                Reports = navigation.Contains(RoleNavigationKeys.Reports),
                ManageData = RoleAccessRules.CanManageData(user)
            },
            Company = await LoadOwnerCompanyAsync(ct)
        };
    }

    private async Task<MobileCompanyInfo?> LoadOwnerCompanyAsync(CancellationToken ct)
    {
        int? companyId;
        try
        {
            companyId = await systemCompany.FindOwnerCompanyIdAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // پیکربندی نادرستِ شرکت مالک نباید ورود موبایل را از کار بیندازد؛ فقط لاگ می‌شود.
            logger.LogWarning(ex, "Owner company could not be resolved for the mobile profile.");
            return null;
        }

        if (companyId is null)
        {
            return null;
        }

        return await db.Companies
            .AsNoTracking()
            .Where(company => company.Id == companyId.Value)
            .Select(company => new MobileCompanyInfo
            {
                Id = company.Id,
                Name = company.Name,
                NamePersian = company.NamePersian
            })
            .FirstOrDefaultAsync(ct);
    }
}
