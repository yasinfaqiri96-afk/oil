using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Helpers;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.Partners;
using PTGOilSystem.Web.Security;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Audit;
using PTGOilSystem.Web.Services.PartyStatements;

namespace PTGOilSystem.Web.Controllers;

/// <summary>
/// صورت‌حساب شراکت بین دو شریک: چه کسی پرداخت کرده، عاید فروش نزد چه کسی است،
/// سهم مفاد هر کدام چقدر است، چه تسویه‌ای بینشان انجام شده و در نهایت چه کسی به چه کسی بدهکار است.
///
/// این کنترلر هیچ فروش، مصرف یا رسیدِ جدیدی نمی‌سازد؛ فقط دادهٔ موجود را می‌خواند و
/// دو چیز را ثبت می‌کند: «عاید نزد کدام شریک» و «تسویهٔ بین شرکا».
/// </summary>
[Authorize]
public partial class PartnershipStatementController : Controller
{
    private readonly ApplicationDbContext _db;
    private readonly IPartnershipStatementService _statements;
    private readonly IAuditService _audit;

    // PTG-P0-01 — نگهبان ثبت دوباره (Refresh/تب دوم/تلاش پس از Timeout).
    private readonly IFormTokenGuard _formTokens;

    public PartnershipStatementController(
        ApplicationDbContext db,
        IPartnershipStatementService statements,
        IAuditService audit,
        IFormTokenGuard? formTokens = null)
    {
        _db = db;
        _statements = statements;
        _audit = audit;
        _formTokens = formTokens ?? new FormTokenGuard(db);
    }

    public async Task<IActionResult> Index(
        int? partnerAId = null,
        int? partnerBId = null,
        int[]? contractIds = null)
    {
        var pairs = await _statements.ListPairsAsync(HttpContext.RequestAborted);
        if (pairs.Count == 0)
        {
            return View(new PartnershipStatementPageViewModel { Pairs = pairs });
        }

        var pair = pairs.FirstOrDefault(p =>
            (p.PartnerAId == partnerAId && p.PartnerBId == partnerBId)
            || (p.PartnerAId == partnerBId && p.PartnerBId == partnerAId))
            ?? pairs[0];

        var statement = await _statements.BuildAsync(
            pair.PartnerAId,
            pair.PartnerBId,
            contractIds,
            HttpContext.RequestAborted);

        return View(new PartnershipStatementPageViewModel
        {
            Pairs = pairs,
            Statement = statement
        });
    }

    /// <summary>
    /// ثبت «عاید فروشِ این قرارداد نزد کدام شریک مانده است».
    /// هیچ Sale و هیچ CustomerReceipt تازه‌ای ساخته نمی‌شود؛ فقط همان فروشِ ثبت‌شده صاحبِ پول پیدا می‌کند.
    /// </summary>
    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SetProceedsHolder(
        int contractId,
        int? partnerId,
        string? returnUrl = null)
    {
        var contract = await _db.Contracts.FirstOrDefaultAsync(c => c.Id == contractId);
        if (contract is null)
        {
            return NotFound();
        }

        if (contract.OwnershipType != ContractOwnershipType.Partnership)
        {
            TempData["err"] = "این قرارداد شراکتی نیست.";
            return RedirectBack(returnUrl);
        }

        if (partnerId.HasValue)
        {
            var isPartnerOfContract = await _db.ContractPartners
                .AnyAsync(cp => cp.ContractId == contractId && cp.PartnerId == partnerId.Value);
            if (!isPartnerOfContract)
            {
                TempData["err"] = "این شریک در قرارداد انتخاب‌شده سهم ندارد.";
                return RedirectBack(returnUrl);
            }
        }

        var before = contract.SaleProceedsHolderPartnerId;
        if (before == partnerId)
        {
            return RedirectBack(returnUrl);
        }

        contract.SaleProceedsHolderPartnerId = partnerId;
        contract.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        await _audit.LogAndSaveAsync(
            nameof(Contract),
            contract.Id,
            AuditAction.Update,
            diff: AuditDiffFormatter.ForUpdate(
                ("SaleProceedsHolderPartnerId", before, partnerId)));

        TempData["ok"] = "نگهدارندهٔ عاید فروش ثبت شد.";
        return RedirectBack(returnUrl);
    }

    /// <summary>
    /// ثبت یک تسویهٔ واقعی بین دو شریک. نه مصرف است، نه فروش، نه پرداخت تأمین‌کننده —
    /// پس هیچ LedgerEntry و هیچ اثری روی P&amp;L قرارداد ندارد.
    /// </summary>
    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateSettlement(
        PartnerSettlementFormViewModel model,
        string? returnUrl = null,
        [FromForm(Name = FormTokenHtmlHelper.FieldName)] string? formToken = null)
    {
        if (model.FromPartnerId == model.ToPartnerId)
        {
            TempData["err"] = "شریک پرداخت‌کننده و دریافت‌کننده نمی‌توانند یکی باشند.";
            return RedirectBack(returnUrl);
        }

        if (model.Amount <= 0m)
        {
            TempData["err"] = "مبلغ تسویه باید بزرگ‌تر از صفر باشد.";
            return RedirectBack(returnUrl);
        }

        var partnerCount = await _db.Partners
            .CountAsync(p => p.Id == model.FromPartnerId || p.Id == model.ToPartnerId);
        if (partnerCount != 2)
        {
            TempData["err"] = "شریک انتخاب‌شده معتبر نیست.";
            return RedirectBack(returnUrl);
        }

        var currency = string.IsNullOrWhiteSpace(model.Currency) ? "USD" : model.Currency.Trim().ToUpperInvariant();
        var fxRate = model.AppliedFxRateToUsd > 0m ? model.AppliedFxRateToUsd : 1m;

        var settlement = new PartnerSettlement
        {
            SettlementDate = model.SettlementDate.Date,
            FromPartnerId = model.FromPartnerId,
            ToPartnerId = model.ToPartnerId,
            ContractId = model.ContractId,
            Amount = model.Amount,
            Currency = currency,
            AppliedFxRateToUsd = fxRate,
            AmountUsd = decimal.Round(model.Amount * fxRate, 4, MidpointRounding.AwayFromZero),
            Reference = model.Reference,
            Description = model.Description
        };

        // PTG-P0-01 — توکن با همان SaveChanges تسویه مصرف می‌شود؛ ارسال دوم چیزی نمی‌سازد.
        _formTokens.Stamp(formToken, "PartnerSettlement.Create", nameof(PartnerSettlement));

        _db.PartnerSettlements.Add(settlement);
        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException dup) when (_formTokens.IsDuplicate(dup))
        {
            TempData["err"] = "این تسویه قبلاً ثبت شده است و دوباره ثبت نشد.";
            return RedirectBack(returnUrl);
        }

        await _audit.LogAndSaveAsync(
            nameof(PartnerSettlement),
            settlement.Id,
            AuditAction.Insert,
            diff: AuditDiffFormatter.ForCreate(
                ("SettlementDate", settlement.SettlementDate),
                ("FromPartnerId", settlement.FromPartnerId),
                ("ToPartnerId", settlement.ToPartnerId),
                ("ContractId", settlement.ContractId),
                ("Amount", settlement.Amount),
                ("Currency", settlement.Currency),
                ("AmountUsd", settlement.AmountUsd),
                ("Reference", settlement.Reference),
                ("Description", settlement.Description)));

        TempData["ok"] = "تسویه بین شرکا ثبت شد.";
        return RedirectBack(returnUrl);
    }

    /// <summary>
    /// برگرداندن یک تسویهٔ اشتباه. تاریخچه پاک نمی‌شود؛ رکورد برگشتی می‌ماند و
    /// رکورد درست دوباره ثبت می‌شود (reverse-and-repost).
    /// </summary>
    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ReverseSettlement(int id, string? reason = null, string? returnUrl = null)
    {
        var settlement = await _db.PartnerSettlements.FirstOrDefaultAsync(s => s.Id == id);
        if (settlement is null)
        {
            return NotFound();
        }

        if (settlement.IsReversed)
        {
            TempData["err"] = "این تسویه قبلاً برگشت خورده است.";
            return RedirectBack(returnUrl);
        }

        settlement.IsReversed = true;
        settlement.ReversedAtUtc = DateTime.UtcNow;
        settlement.ReversalReason = reason;
        settlement.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        await _audit.LogAndSaveAsync(
            nameof(PartnerSettlement),
            settlement.Id,
            AuditAction.Reverse,
            diff: AuditDiffFormatter.ForUpdate(
                ("IsReversed", false, true),
                ("ReversalReason", null, reason)));

        TempData["ok"] = "تسویه برگشت خورد.";
        return RedirectBack(returnUrl);
    }

    private IActionResult RedirectBack(string? returnUrl)
        => !string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl)
            ? LocalRedirect(returnUrl)
            : RedirectToAction(nameof(Index));
}
