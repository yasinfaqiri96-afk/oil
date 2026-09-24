using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.HumanResources;
using PTGOilSystem.Web.Security;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Audit;

namespace PTGOilSystem.Web.Controllers;

/// <summary>تنظیماتِ مدیریت بشری: ساعتِ کاری و قواعدِ کسر، رخصتی‌های رسمی و انواعِ رخصتی — یک صفحه.</summary>
[Authorize]
public class HrSettingsController : Controller
{
    private readonly ApplicationDbContext _db;
    private readonly IAuditService _audit;

    public HrSettingsController(ApplicationDbContext db, IAuditService audit)
    {
        _db = db;
        _audit = audit;
    }

    public async Task<IActionResult> Index()
    {
        var settings = await _db.HrSettings.AsNoTracking().OrderBy(s => s.Id).FirstOrDefaultAsync() ?? new HrSettings();
        return View(new HrSettingsPageViewModel
        {
            Settings = new HrSettingsFormViewModel
            {
                WorkdayStart = settings.WorkdayStart,
                WorkdayEnd = settings.WorkdayEnd,
                LateGraceMinutes = settings.LateGraceMinutes,
                WeeklyOffDays = settings.OffDays().Select(d => (int)d).ToArray(),
                PayrollDaysPerMonth = settings.PayrollDaysPerMonth,
                WorkingHoursPerDay = settings.WorkingHoursPerDay,
                DeductAbsences = settings.DeductAbsences,
                DeductLateMinutes = settings.DeductLateMinutes
            },
            Holidays = await _db.HrHolidays.AsNoTracking().OrderByDescending(h => h.Date).Take(100).ToListAsync(),
            LeaveTypes = await _db.LeaveTypes.AsNoTracking().OrderByDescending(t => t.IsActive).ThenBy(t => t.Name).ToListAsync(),
            CanEdit = RoleAccessRules.CanManageData(User)
        });
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveSettings([Bind(Prefix = "Settings")] HrSettingsFormViewModel model)
    {
        if (!ModelState.IsValid || model.WorkdayEnd <= model.WorkdayStart)
        {
            TempData["err"] = "تنظیمات معتبر نیست؛ ساعتِ پایانِ کار باید بعد از شروع باشد.";
            return RedirectToAction(nameof(Index));
        }

        var settings = await _db.HrSettings.OrderBy(s => s.Id).FirstOrDefaultAsync();
        var isNew = settings is null;
        settings ??= new HrSettings();
        var before = isNew ? null : new
        {
            settings.WorkdayStart, settings.WorkdayEnd, settings.LateGraceMinutes, settings.WeeklyOffDays,
            settings.PayrollDaysPerMonth, settings.WorkingHoursPerDay, settings.DeductAbsences, settings.DeductLateMinutes
        };

        settings.WorkdayStart = model.WorkdayStart;
        settings.WorkdayEnd = model.WorkdayEnd;
        settings.LateGraceMinutes = model.LateGraceMinutes;
        settings.WeeklyOffDays = string.Join(",", (model.WeeklyOffDays ?? []).Where(d => d is >= 0 and <= 6).Distinct().Order());
        settings.PayrollDaysPerMonth = model.PayrollDaysPerMonth;
        settings.WorkingHoursPerDay = model.WorkingHoursPerDay;
        settings.DeductAbsences = model.DeductAbsences;
        settings.DeductLateMinutes = model.DeductLateMinutes;
        if (isNew) _db.HrSettings.Add(settings);
        await _db.SaveChangesAsync();

        await _audit.LogAndSaveAsync(nameof(HrSettings), settings.Id, isNew ? AuditAction.Insert : AuditAction.Update,
            diff: AuditDiffFormatter.ForUpdate(
                ("WorkdayStart", before?.WorkdayStart, settings.WorkdayStart),
                ("WorkdayEnd", before?.WorkdayEnd, settings.WorkdayEnd),
                ("LateGraceMinutes", before?.LateGraceMinutes, settings.LateGraceMinutes),
                ("WeeklyOffDays", before?.WeeklyOffDays, settings.WeeklyOffDays),
                ("PayrollDaysPerMonth", before?.PayrollDaysPerMonth, settings.PayrollDaysPerMonth),
                ("WorkingHoursPerDay", before?.WorkingHoursPerDay, settings.WorkingHoursPerDay),
                ("DeductAbsences", before?.DeductAbsences, settings.DeductAbsences),
                ("DeductLateMinutes", before?.DeductLateMinutes, settings.DeductLateMinutes)));

        TempData["ok"] = "تنظیمات مدیریت بشری ذخیره شد.";
        return RedirectToAction(nameof(Index));
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> AddHoliday(DateTime date, string? name)
    {
        var label = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        if (label is null)
        {
            TempData["err"] = "نامِ رخصتیِ رسمی الزامی است.";
            return RedirectToAction(nameof(Index));
        }

        if (await _db.HrHolidays.AnyAsync(h => h.Date == date.Date))
        {
            TempData["err"] = "برای این تاریخ رخصتیِ رسمی ثبت شده است.";
            return RedirectToAction(nameof(Index));
        }

        var holiday = new HrHoliday { Date = date.Date, Name = label.Length > 150 ? label[..150] : label };
        _db.HrHolidays.Add(holiday);
        await _db.SaveChangesAsync();
        await _audit.LogAndSaveAsync(nameof(HrHoliday), holiday.Id, AuditAction.Insert,
            diff: AuditDiffFormatter.ForCreate(("Date", holiday.Date), ("Name", holiday.Name)));
        TempData["ok"] = "رخصتیِ رسمی ثبت شد.";
        return RedirectToAction(nameof(Index));
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> RemoveHoliday(int id)
    {
        var holiday = await _db.HrHolidays.FirstOrDefaultAsync(h => h.Id == id);
        if (holiday is null) return NotFound();

        await _audit.LogAsync(nameof(HrHoliday), holiday.Id, AuditAction.Delete,
            diff: AuditDiffFormatter.ForDelete(("Date", holiday.Date), ("Name", holiday.Name)));
        _db.HrHolidays.Remove(holiday);
        await _db.SaveChangesAsync();
        TempData["ok"] = "رخصتیِ رسمی حذف شد.";
        return RedirectToAction(nameof(Index));
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveLeaveType(int id, string? name, decimal? annualAllowanceDays, bool isPaid, bool isActive)
    {
        var label = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        if (label is null || annualAllowanceDays is < 0 or > 366)
        {
            TempData["err"] = "نامِ نوعِ رخصتی الزامی است و سهمیه باید بین ۰ و ۳۶۶ روز باشد.";
            return RedirectToAction(nameof(Index));
        }

        if (await _db.LeaveTypes.AnyAsync(t => t.Id != id && t.Name.ToUpper() == label.ToUpper()))
        {
            TempData["err"] = "نوعِ رخصتیِ هم‌نام وجود دارد.";
            return RedirectToAction(nameof(Index));
        }

        var type = id == 0 ? new LeaveType() : await _db.LeaveTypes.FirstOrDefaultAsync(t => t.Id == id);
        if (type is null) return NotFound();

        var before = id == 0 ? null : new { type.Name, type.AnnualAllowanceDays, type.IsPaid, type.IsActive };
        type.Name = label.Length > 100 ? label[..100] : label;
        type.AnnualAllowanceDays = annualAllowanceDays;
        type.IsPaid = isPaid;
        type.IsActive = isActive;
        if (id == 0) _db.LeaveTypes.Add(type);
        await _db.SaveChangesAsync();

        await _audit.LogAndSaveAsync(nameof(LeaveType), type.Id, id == 0 ? AuditAction.Insert : AuditAction.Update,
            diff: AuditDiffFormatter.ForUpdate(
                ("Name", before?.Name, type.Name),
                ("AnnualAllowanceDays", before?.AnnualAllowanceDays, type.AnnualAllowanceDays),
                ("IsPaid", before?.IsPaid, type.IsPaid),
                ("IsActive", before?.IsActive, type.IsActive)));
        TempData["ok"] = "نوعِ رخصتی ذخیره شد.";
        return RedirectToAction(nameof(Index));
    }
}
