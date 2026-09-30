using System.Diagnostics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using PTGOilSystem.Web.Configuration;
using PTGOilSystem.Web.Models;
using PTGOilSystem.Web.Security;
using PTGOilSystem.Web.Services;

namespace PTGOilSystem.Web.Controllers;

[Authorize]
public class HomeController : Controller
{
    private readonly IDashboardService _dashboard;
    private readonly IOnboardingService? _onboarding;
    private readonly OnboardingOptions _onboardingOptions;
    private readonly ILogger<HomeController> _logger;

    public HomeController(
        IDashboardService dashboard,
        ILogger<HomeController> logger,
        IOnboardingService? onboarding = null,
        IOptions<OnboardingOptions>? onboardingOptions = null)
    {
        _dashboard = dashboard;
        _logger = logger;
        _onboarding = onboarding;
        _onboardingOptions = onboardingOptions?.Value ?? new OnboardingOptions();
    }

    public async Task<IActionResult> Index(string? guide = null, CancellationToken ct = default)
    {
        var vm = new DashboardViewModel();

        try
        {
            vm = await _dashboard.BuildDashboardAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Dashboard aggregates unavailable; database may not be migrated yet.");
            ViewData["DbWarning"] = "Database connection is unavailable or migrations have not been applied.";
        }

        // راهنمای شروع فقط برای کاربری که اجازهٔ ثبت دارد؛ کاربر فقط‌خواندنی کاری برای شروع ندارد.
        if (_onboarding is not null && _onboardingOptions.Enabled && RoleAccessRules.CanManageData(User))
        {
            try
            {
                // ClaimTypes.Name نام کامل است؛ نام کاربری یکتا در Claim جداگانه می‌آید.
                var storageKey = OnboardingPanelViewModel.BuildStorageKey(
                    User.FindFirst(AppClaimTypes.Username)?.Value ?? User.Identity?.Name);
                ViewData["Onboarding"] = new OnboardingPanelViewModel
                {
                    Progress = await _onboarding.GetProgressAsync(ct),
                    ForceOpen = string.Equals(guide, "1", StringComparison.Ordinal),
                    StorageKey = storageKey,
                    Dismissed = Request.Cookies[storageKey] ?? ""
                };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Onboarding progress unavailable.");
            }
        }

        return View(vm);
    }

    public IActionResult Privacy() => View();

    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    [AllowAnonymous]
    public IActionResult Error()
        => View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
}
