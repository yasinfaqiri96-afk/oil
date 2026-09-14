using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Models.Contracts;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Security;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.ContractClosure;

namespace PTGOilSystem.Web.Controllers;

/// <summary>
/// بستن قرارداد (با کنترل موارد باز) و بازگشایی آن (فقط مدیر، با دلیل). هر دو در Audit ثبت می‌شوند.
/// قفلِ ثبت‌های بعدی در <c>ApplicationDbContext</c> اعمال می‌شود، نه اینجا.
/// </summary>
public partial class ContractsController
{
    private IContractClosureService? _contractClosure;
    private IContractClosureService ContractClosure
        => _contractClosure ??= new ContractClosureService(_db, _audit, new StockService(_db));

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpGet]
    public async Task<IActionResult> Close(int id, string? returnUrl = null)
    {
        var check = await ContractClosure.EvaluateAsync(id);
        if (check is null) return NotFound();

        return View("Closure", await BuildClosureModelAsync(check, isReopen: false, returnUrl));
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Close(int id, string? reason, string? returnUrl = null)
    {
        var result = await ContractClosure.CloseAsync(id, reason, ClosureActorUserId());
        if (result.NotFound) return NotFound();

        if (!result.Succeeded)
        {
            TempData["err"] = result.Error;
            return RedirectToAction(nameof(Close), new { id, returnUrl });
        }

        TempData["ok"] = "قرارداد بسته شد. از این پس فقط قابل مشاهده است.";
        return ClosureRedirect(id, returnUrl);
    }

    [Authorize(Policy = AuthPolicies.AdminOnly)]
    [HttpGet]
    public async Task<IActionResult> Reopen(int id, string? returnUrl = null)
    {
        var check = await ContractClosure.EvaluateAsync(id);
        if (check is null) return NotFound();

        if (check.Status != ContractStatus.Closed)
        {
            TempData["err"] = "فقط قرارداد بسته را می‌توان بازگشایی کرد.";
            return ClosureRedirect(id, returnUrl);
        }

        return View("Closure", await BuildClosureModelAsync(check, isReopen: true, returnUrl));
    }

    [Authorize(Policy = AuthPolicies.AdminOnly)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Reopen(int id, string? reason, string? returnUrl = null)
    {
        var result = await ContractClosure.ReopenAsync(id, reason, ClosureActorUserId());
        if (result.NotFound) return NotFound();

        if (!result.Succeeded)
        {
            TempData["err"] = result.Error;
            return RedirectToAction(nameof(Reopen), new { id, returnUrl });
        }

        TempData["ok"] = "قرارداد بازگشایی شد و ثبت عملیات دوباره ممکن است.";
        return ClosureRedirect(id, returnUrl);
    }

    private async Task<ContractClosureViewModel> BuildClosureModelAsync(ContractClosureCheck check, bool isReopen, string? returnUrl)
    {
        var history = await _db.AuditLogs
            .AsNoTracking()
            .Where(a => a.EntityName == nameof(Contract)
                && a.EntityId == check.ContractId
                && (a.Action == ContractClosureService.CloseAuditAction || a.Action == ContractClosureService.ReopenAuditAction))
            .OrderByDescending(a => a.ActionAtUtc)
            .Take(20)
            .Select(a => new ContractClosureHistoryRow(a.ActionAtUtc, a.Action, a.ActorUsername, a.Description))
            .ToListAsync();

        return new ContractClosureViewModel
        {
            ContractId = check.ContractId,
            ContractLabel = check.ContractLabel,
            StatusName = ClosureStatusName(check.Status),
            IsReopen = isReopen,
            CanSubmit = isReopen ? check.Status == ContractStatus.Closed : check.CanClose,
            // در بازگشایی، «موارد باز» معنایی ندارد؛ فقط وضعیت و تاریخچه نمایش داده می‌شود.
            Blockers = isReopen ? [] : check.Blockers,
            History = history,
            ReturnUrl = SafeClosureReturnUrl(returnUrl)
        };
    }

    private IActionResult ClosureRedirect(int id, string? returnUrl)
    {
        var safeReturnUrl = SafeClosureReturnUrl(returnUrl);
        return safeReturnUrl is null
            ? RedirectToAction(nameof(Details), new { id })
            : Redirect(safeReturnUrl);
    }

    private string? SafeClosureReturnUrl(string? returnUrl)
        => !string.IsNullOrWhiteSpace(returnUrl) && Url?.IsLocalUrl(returnUrl) == true ? returnUrl : null;

    private int? ClosureActorUserId()
    {
        var raw = User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        return int.TryParse(raw, out var userId) ? userId : null;
    }

    private static string ClosureStatusName(ContractStatus status) => status switch
    {
        ContractStatus.Draft => "پیش‌نویس",
        ContractStatus.Active => "فعال",
        ContractStatus.Closed => "بسته‌شده",
        ContractStatus.Cancelled => "لغوشده",
        _ => status.ToString()
    };

    /// <summary>قرارداد بسته فقط قابل مشاهده است؛ فرم‌های ویرایش آن باز نمی‌شوند.</summary>
    private IActionResult? RedirectIfContractClosed(int id, ContractStatus status)
    {
        if (status != ContractStatus.Closed)
        {
            return null;
        }

        TempData["err"] = "این قرارداد بسته است و فقط قابل مشاهده است. برای ویرایش، مدیر سیستم باید آن را بازگشایی کند.";
        return RedirectToAction(nameof(Details), new { id });
    }
}
