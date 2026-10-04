using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PTGOilSystem.Web.Models;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Security;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Audit;
using PTGOilSystem.Web.Services.Calendars;
using PTGOilSystem.Web.Services.Time;

namespace PTGOilSystem.Web.Controllers;

/// <summary>
/// تنظیماتِ عمومیِ سامانه (فعلاً «نوع تقویم»). فقط مدیر سیستم. تغییرِ تقویم فقط نمایش و ورودِ تاریخ را
/// عوض می‌کند؛ هیچ رکوردِ تاریخ‌داری بازنویسی نمی‌شود.
/// </summary>
[Authorize(Policy = AuthPolicies.AdminOnly)]
public class SystemSettingsController : Controller
{
    private readonly ICalendarSettingsService _calendarSettings;
    private readonly IAfghanistanBusinessClock _clock;
    private readonly IAuditService _audit;

    public SystemSettingsController(
        ICalendarSettingsService calendarSettings,
        IAfghanistanBusinessClock clock,
        IAuditService audit)
    {
        _calendarSettings = calendarSettings;
        _clock = clock;
        _audit = audit;
    }

    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var today = _clock.Today;
        return View(new SystemSettingsPageViewModel
        {
            Settings = new SystemSettingsFormViewModel
            {
                CalendarType = await _calendarSettings.GetAsync(cancellationToken)
            },
            TodayGregorian = today.ToString("yyyy/MM/dd", CultureInfo.InvariantCulture),
            TodaySolar = AfghanSolarCalendar.Format(today, "yyyy/MM/dd") + " — " + AfghanSolarCalendar.Format(today, "d MMMM yyyy")
        });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Save([Bind(Prefix = "Settings")] SystemSettingsFormViewModel model, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid || !Enum.IsDefined(model.CalendarType))
        {
            TempData["err"] = "نوع تقویم معتبر نیست.";
            return RedirectToAction(nameof(Index));
        }

        var (settingId, previous) = await _calendarSettings.SaveAsync(model.CalendarType, cancellationToken);

        if (previous != model.CalendarType)
        {
            await _audit.LogAndSaveAsync(nameof(SystemSetting), settingId, AuditAction.Update,
                diff: AuditDiffFormatter.ForUpdate(
                    ("CalendarType", previous, model.CalendarType)));
        }

        TempData["ok"] = "تنظیمات ذخیره شد. تاریخ‌های ثبت‌شده تغییر نکرده‌اند؛ فقط نمایش و ورودِ تاریخ عوض شد.";
        return RedirectToAction(nameof(Index));
    }
}
