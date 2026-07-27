using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Helpers;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.Payments;
using PTGOilSystem.Web.Security;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Exceptions;

namespace PTGOilSystem.Web.Controllers;

/// <summary>
/// اتصال قرارداد به «پرداخت از طریق صراف» — پرداختی که فقط دو LedgerEntry دارد و PaymentTransaction ندارد.
/// فقط طبقه‌بندیِ قرارداد را روی هر دو ردیفِ گروه تنظیم می‌کند؛ هیچ Ledger/Journal جدیدی نمی‌سازد و
/// هیچ Reverse/Repost انجام نمی‌دهد. برای رکوردهای Legacyِ بدون GroupId، ابتدا جفت‌سازی لازم است.
/// </summary>
[Authorize(Policy = AuthPolicies.ManageData)]
public sealed class ViaSarrafPaymentsController : Controller
{
    private readonly ApplicationDbContext _db;
    private readonly IViaSarrafContractAssignmentService _assignment;
    private readonly IViaSarrafLegacyGroupingService _legacy;

    public ViaSarrafPaymentsController(
        ApplicationDbContext db,
        IViaSarrafContractAssignmentService assignment,
        IViaSarrafLegacyGroupingService legacy)
    {
        _db = db;
        _assignment = assignment;
        _legacy = legacy;
    }

    // ---- ثبت / تغییر قرارداد (گروهِ آماده) ----

    [HttpGet]
    public async Task<IActionResult> AssignContract(int ledgerEntryId, string? returnUrl = null)
    {
        ViaSarrafGroup group;
        try
        {
            var loaded = await _assignment.TryLoadGroupByLedgerEntryAsync(ledgerEntryId);
            if (loaded is null)
            {
                TempData["error"] = "این پرداخت صرافی هنوز جفت نشده است. ابتدا «تأیید جفت تراکنش» را انجام دهید.";
                return RedirectToAction("Details", "Ledger", new { id = ledgerEntryId });
            }

            group = loaded;
        }
        catch (BusinessRuleException ex)
        {
            TempData["error"] = ex.Message;
            return RedirectToAction("Details", "Ledger", new { id = ledgerEntryId });
        }

        var model = await BuildAssignViewModelAsync(group, returnUrl);
        await PopulateContractsAsync(group.SupplierId, model.NewContractId);
        return View(model);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> AssignContract(ViaSarrafAssignContractViewModel model)
    {
        if (!ModelState.IsValid)
        {
            await RehydrateAssignAsync(model);
            return View(model);
        }

        try
        {
            var result = await _assignment.AssignContractAsync(new ViaSarrafContractAssignmentRequest(
                model.GroupId,
                model.NewContractId,
                model.Reason,
                CurrentUserId(),
                CurrentUserName()));

            TempData["ok"] = result.PreviousContractId is null
                ? "قرارداد پرداخت صرافی با موفقیت ثبت شد."
                : $"قرارداد قبلی از {result.PreviousContractNumber ?? result.PreviousContractId.ToString()} به {result.NewContractNumber ?? "(بدون قرارداد)"} تغییر کرد.";

            if (!string.IsNullOrWhiteSpace(model.ReturnUrl) && Url.IsLocalUrl(model.ReturnUrl))
            {
                return Redirect(model.ReturnUrl);
            }

            return RedirectToAction("Details", "Ledger", new { id = model.PaymentLedgerEntryId });
        }
        catch (BusinessRuleException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            await RehydrateAssignAsync(model);
            return View(model);
        }
    }

    // ---- تأیید دستیِ جفت (رکورد Legacy بدون GroupId) ----

    [HttpGet]
    public async Task<IActionResult> ConfirmPair(int ledgerEntryId, string? returnUrl = null)
    {
        var payment = await _db.LedgerEntries.AsNoTracking().FirstOrDefaultAsync(l => l.Id == ledgerEntryId);
        if (payment is null || payment.SourceType != PaymentsController.ViaSarrafSupplierLedgerSourceType)
        {
            return NotFound();
        }

        if (payment.ViaSarrafGroupId.HasValue)
        {
            return RedirectToAction(nameof(AssignContract), new { ledgerEntryId, returnUrl });
        }

        var model = new ViaSarrafConfirmPairViewModel
        {
            PaymentLedgerEntryId = payment.Id,
            SupplierName = await SupplierNameAsync(payment.SupplierId),
            SarrafName = await SarrafNameAsync(payment.SourceId),
            EntryDate = payment.EntryDate,
            SourceAmount = payment.SourceAmount ?? 0m,
            SourceCurrencyCode = payment.SourceCurrencyCode ?? SystemCurrency.BaseCurrencyCode,
            AmountUsd = payment.AmountUsd,
            ReturnUrl = !string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl) ? returnUrl : null,
            Candidates = await LoadPayableCandidatesAsync(payment.SourceId)
        };

        return View(model);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ConfirmPair(ViaSarrafConfirmPairViewModel model)
    {
        if (!model.PayableLedgerEntryId.HasValue)
        {
            ModelState.AddModelError(nameof(model.PayableLedgerEntryId), "انتخاب ردیف بدهی صراف الزامی است.");
        }

        if (!ModelState.IsValid)
        {
            var payment = await _db.LedgerEntries.AsNoTracking().FirstOrDefaultAsync(l => l.Id == model.PaymentLedgerEntryId);
            model.Candidates = payment is null ? [] : await LoadPayableCandidatesAsync(payment.SourceId);
            return View(model);
        }

        try
        {
            await _legacy.ManualPairAsync(
                model.PaymentLedgerEntryId,
                model.PayableLedgerEntryId!.Value,
                model.Reason,
                CurrentUserId(),
                CurrentUserName());

            TempData["ok"] = "جفت تراکنش تأیید شد. اکنون می‌توانید قرارداد را انتخاب کنید.";
            return RedirectToAction(nameof(AssignContract), new { ledgerEntryId = model.PaymentLedgerEntryId, returnUrl = model.ReturnUrl });
        }
        catch (BusinessRuleException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            var payment = await _db.LedgerEntries.AsNoTracking().FirstOrDefaultAsync(l => l.Id == model.PaymentLedgerEntryId);
            model.Candidates = payment is null ? [] : await LoadPayableCandidatesAsync(payment.SourceId);
            return View(model);
        }
    }

    // ---- گروه‌بندی Legacy: Dry Run + اعمالِ جفت‌های قطعی ----

    [HttpGet]
    public async Task<IActionResult> LegacyReview()
    {
        var report = await _legacy.AnalyzeAsync();
        return View(new ViaSarrafLegacyReviewViewModel
        {
            TotalUngroupedRows = report.TotalUngroupedRows,
            UngroupedPaymentRows = report.UngroupedPaymentRows,
            UngroupedPayableRows = report.UngroupedPayableRows,
            DeterministicPairCount = report.DeterministicPairs.Count,
            AmbiguousCount = report.Ambiguous.Count,
            UnpairedCount = report.Unpaired.Count,
            Ambiguous = report.Ambiguous
                .Select(a => new ViaSarrafLegacyReviewRow(a.LedgerEntryId, a.SourceType, a.SarrafId, a.EntryDate, a.SourceAmount, a.SourceCurrencyCode, a.Reason))
                .ToList(),
            Unpaired = report.Unpaired
                .Select(a => new ViaSarrafLegacyReviewRow(a.LedgerEntryId, a.SourceType, a.SarrafId, a.EntryDate, a.SourceAmount, a.SourceCurrencyCode, a.Reason))
                .ToList()
        });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ApplyConfirmed()
    {
        var result = await _legacy.ApplyConfirmedMatchesAsync(CurrentUserId());
        TempData["ok"] = $"{result.GroupsCreated} گروهِ قطعی ساخته شد ({result.RowsUpdated} ردیف). موارد مبهم و بی‌جفت دست‌نخورده ماندند.";
        return RedirectToAction(nameof(LegacyReview));
    }

    // ---- کمکی ----

    private async Task<ViaSarrafAssignContractViewModel> BuildAssignViewModelAsync(ViaSarrafGroup group, string? returnUrl)
        => new()
        {
            GroupId = group.GroupId,
            PaymentLedgerEntryId = group.PaymentLedgerEntryId,
            SupplierName = await SupplierNameAsync(group.SupplierId),
            SarrafName = await SarrafNameAsync(group.SarrafId),
            EntryDate = group.EntryDate,
            SourceAmount = group.SourceAmount,
            SourceCurrencyCode = group.SourceCurrencyCode,
            AmountUsd = group.AmountUsd,
            CurrentContractId = group.ContractId,
            CurrentContractNumber = group.ContractId.HasValue
                ? await _db.Contracts.AsNoTracking().Where(c => c.Id == group.ContractId.Value).Select(c => c.ContractNumber).FirstOrDefaultAsync()
                : null,
            NewContractId = group.ContractId,
            ReturnUrl = !string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl) ? returnUrl : null
        };

    private async Task RehydrateAssignAsync(ViaSarrafAssignContractViewModel model)
    {
        try
        {
            var group = await _assignment.LoadGroupAsync(model.GroupId);
            model.PaymentLedgerEntryId = group.PaymentLedgerEntryId;
            model.SupplierName = await SupplierNameAsync(group.SupplierId);
            model.SarrafName = await SarrafNameAsync(group.SarrafId);
            model.EntryDate = group.EntryDate;
            model.SourceAmount = group.SourceAmount;
            model.SourceCurrencyCode = group.SourceCurrencyCode;
            model.AmountUsd = group.AmountUsd;
            model.CurrentContractId = group.ContractId;
            model.CurrentContractNumber = group.ContractId.HasValue
                ? await _db.Contracts.AsNoTracking().Where(c => c.Id == group.ContractId.Value).Select(c => c.ContractNumber).FirstOrDefaultAsync()
                : null;
            await PopulateContractsAsync(group.SupplierId, model.NewContractId);
        }
        catch (BusinessRuleException)
        {
            await PopulateContractsAsync(0, model.NewContractId);
        }
    }

    private async Task PopulateContractsAsync(int supplierId, int? selectedId)
    {
        var contracts = await _db.Contracts
            .AsNoTracking()
            .Include(c => c.Product)
            .Include(c => c.Unit)
            .Where(c => c.ContractType == ContractType.Purchase && c.SupplierId == supplierId)
            .OrderByDescending(c => c.ContractDate)
            .ThenBy(c => c.ContractNumber)
            .ToListAsync();

        ViewBag.Contracts = new SelectList(
            ContractUiText.ToLookupOptions(contracts),
            nameof(ContractLookupOption.Id),
            nameof(ContractLookupOption.Display),
            selectedId);
    }

    private async Task<List<ViaSarrafPayableCandidateRow>> LoadPayableCandidatesAsync(int sarrafId)
        => await _db.LedgerEntries
            .AsNoTracking()
            .Where(l => l.SourceType == PaymentsController.ViaSarrafPayableLedgerSourceType
                && l.SourceId == sarrafId
                && l.ViaSarrafGroupId == null)
            .OrderByDescending(l => l.EntryDate)
            .ThenByDescending(l => l.Id)
            .Select(l => new ViaSarrafPayableCandidateRow(
                l.Id,
                l.EntryDate,
                l.SourceAmount ?? 0m,
                l.SourceCurrencyCode ?? "USD",
                l.AmountUsd,
                l.Reference,
                l.ContractId,
                l.Contract != null ? l.Contract.ContractNumber : null))
            .ToListAsync();

    private async Task<string> SupplierNameAsync(int? supplierId)
        => supplierId.HasValue
            ? await _db.Suppliers.AsNoTracking().Where(s => s.Id == supplierId.Value).Select(s => s.Name).FirstOrDefaultAsync() ?? "-"
            : "-";

    private async Task<string> SarrafNameAsync(int sarrafId)
        => await _db.Sarrafs.AsNoTracking().Where(s => s.Id == sarrafId).Select(s => s.Name).FirstOrDefaultAsync() ?? "-";

    private int? CurrentUserId()
        => int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

    private string? CurrentUserName()
        => User.FindFirstValue(AppClaimTypes.Username) ?? User.Identity?.Name;
}
