using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Security;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.OperationalPeriod;
using PTGOilSystem.Web.Services.Time;

namespace PTGOilSystem.Web.Controllers;

/// <summary>
/// PTG-P1-01 — تنها مسیر بستن و بازکردنِ دورهٔ عملیاتی.
///
/// عمداً کوچک است: یک تاریخ و یک دلیل. تقویمِ دوره‌ای/سال مالی متعلق به ماژول حسابداری
/// است که خاموش می‌ماند؛ این‌جا فقط واترمارکِ «تا این تاریخ بسته است» نگه داشته می‌شود
/// تا گزارشی که به شریک داده شده، ماه بعد عدد دیگری ندهد.
/// </summary>
[Authorize(Policy = AuthPolicies.OperationalPeriodAdmin)]
public sealed class OperationalPeriodLocksController(
    ApplicationDbContext db,
    IOperationalPeriodGuard guard,
    IAuditService audit,
    ICurrentUserContext currentUser,
    IAfghanistanBusinessClock clock) : Controller
{
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        ViewBag.Status = await guard.GetStatusAsync(cancellationToken);
        ViewBag.Today = clock.Today;

        var history = await db.OperationalPeriodLocks
            .AsNoTracking()
            .OrderByDescending(x => x.Id)
            .Take(50)
            .ToListAsync(cancellationToken);

        return View(history);
    }

    /// <summary>بستنِ دوره تا یک تاریخ. دلیل اجباری است؛ قفلِ بی‌دلیل قابل بازرسی نیست.</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Close(DateTime lockedThroughDate, string? reason, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            TempData["err"] = "برای بستن دوره باید دلیل نوشته شود.";
            return RedirectToAction(nameof(Index));
        }

        // بستنِ دوره‌ای که هنوز نیامده یعنی قفل‌کردنِ کارِ امروز؛ اجازه داده نمی‌شود.
        if (lockedThroughDate.Date > clock.Today)
        {
            TempData["err"] = "تاریخ بستن دوره نمی‌تواند بعد از امروز باشد.";
            return RedirectToAction(nameof(Index));
        }

        var current = await guard.GetStatusAsync(cancellationToken);
        if (current.IsLocked && lockedThroughDate.Date <= current.LockedThroughDate!.Value)
        {
            TempData["err"] = $"دوره از قبل تا {current.LockedThroughDate:yyyy-MM-dd} بسته است.";
            return RedirectToAction(nameof(Index));
        }

        db.OperationalPeriodLocks.Add(new OperationalPeriodLock
        {
            LockedThroughDate = DateTime.SpecifyKind(lockedThroughDate.Date, DateTimeKind.Utc),
            IsActive = true,
            Reason = reason.Trim()
        });

        await audit.LogAsync(
            nameof(OperationalPeriodLock),
            0,
            AuditAction.Insert,
            currentUser.UserId,
            $"CloseOperationalPeriodThrough={lockedThroughDate:yyyy-MM-dd}",
            cancellationToken);

        await db.SaveChangesAsync(cancellationToken);

        TempData["ok"] = $"دوره مالی تا {lockedThroughDate:yyyy-MM-dd} بسته شد.";
        return RedirectToAction(nameof(Index));
    }

    /// <summary>
    /// بازکردنِ دوره. سطرِ قبلی حذف نمی‌شود؛ فقط غیرفعال می‌گردد تا معلوم بماند چه‌کسی و
    /// با چه دلیلی باز کرده است.
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Reopen(string? reason, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            TempData["err"] = "برای بازکردن دوره باید دلیل نوشته شود.";
            return RedirectToAction(nameof(Index));
        }

        var active = await db.OperationalPeriodLocks
            .Where(x => x.IsActive)
            .ToListAsync(cancellationToken);

        if (active.Count == 0)
        {
            TempData["err"] = "هیچ دورهٔ بسته‌ای وجود ندارد.";
            return RedirectToAction(nameof(Index));
        }

        foreach (var row in active)
        {
            row.IsActive = false;
            row.Reason = $"{row.Reason} | باز شد: {reason.Trim()}";
        }

        await audit.LogAsync(
            nameof(OperationalPeriodLock),
            0,
            AuditAction.Approve,
            currentUser.UserId,
            $"ReopenOperationalPeriod; Reason={reason.Trim()}",
            cancellationToken);

        await db.SaveChangesAsync(cancellationToken);

        TempData["ok"] = "قفل دوره مالی برداشته شد.";
        return RedirectToAction(nameof(Index));
    }
}
