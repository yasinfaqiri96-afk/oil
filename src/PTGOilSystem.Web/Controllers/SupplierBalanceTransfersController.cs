using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Helpers;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.Suppliers;
using PTGOilSystem.Web.Security;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Audit;
using PTGOilSystem.Web.Services.Exceptions;
using PTGOilSystem.Web.Services.Time;

namespace PTGOilSystem.Web.Controllers;

/// <summary>
/// «مانده قابل انتقال» تأمین‌کننده — انتقال طلب شرکت به یک یا چند قرارداد خرید همان تأمین‌کننده.
///
/// این کنترلر هیچ پرداخت یا دریافتی نمی‌سازد و صندوق/بانک را تغییر نمی‌دهد. تمام منطق مالی در
/// SupplierBalanceTransferService است و مانده از SupplierTransferableBalanceService خوانده می‌شود.
/// </summary>
[Authorize]
public sealed class SupplierBalanceTransfersController : Controller
{
    private const int LookupLimit = 200;

    private readonly ApplicationDbContext _db;
    private readonly ISupplierTransferableBalanceService _balances;
    private readonly ISupplierBalanceTransferService _transfers;
    private readonly IAuditService _audit;
    private readonly IAfghanistanBusinessClock _clock;
    private readonly IPricingService _pricing;

    public SupplierBalanceTransfersController(
        ApplicationDbContext db,
        ISupplierTransferableBalanceService balances,
        ISupplierBalanceTransferService transfers,
        IAuditService audit,
        IPricingService pricing,
        IAfghanistanBusinessClock? clock = null)
    {
        _db = db;
        _balances = balances;
        _transfers = transfers;
        _audit = audit;
        _pricing = pricing;
        _clock = clock ?? new AfghanistanBusinessClock(TimeProvider.System);
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    public async Task<IActionResult> Create(
        int supplierId,
        int companyId,
        string? currency = null,
        string? returnUrl = null)
    {
        var supplier = await _db.Suppliers.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == supplierId);
        if (supplier is null)
        {
            return NotFound();
        }

        var balance = await _balances.GetAsync(supplierId);
        // شرکت از Context صفحه می‌آید. صفر یعنی سطل «سطح گروه»؛ اگر اصلاً داده نشده و فقط یک
        // سطل مانده دارد، همان انتخاب می‌شود تا کاربر مجبور به انتخاب بی‌معنی نشود.
        var company = balance.Company(companyId);
        if (company is null && companyId == SupplierTransferableBalance.GroupLevelCompanyId)
        {
            var pools = balance.AllPools.Where(c => c.HasTransferable).ToList();
            company = pools.Count == 1 ? pools[0] : null;
        }

        if (company is null || !company.HasTransferable)
        {
            TempData["error"] = companyId > 0
                ? "برای این شرکت مانده قابل انتقال وجود ندارد."
                : "مانده قابل انتقال وجود ندارد یا باید سطل مانده انتخاب شود.";
            return RedirectToSupplier(supplierId, returnUrl);
        }

        var buckets = MapBuckets(company);
        var selected = SystemCurrency.Normalize(
            string.IsNullOrWhiteSpace(currency) ? buckets[0].CurrencyCode : currency);

        var model = new SupplierBalanceTransferCreateViewModel
        {
            SupplierId = supplierId,
            SupplierName = supplier.Name,
            CompanyId = company.CompanyId,
            CompanyName = company.CompanyName,
            CurrencyCode = buckets.Any(b => b.CurrencyCode == selected) ? selected : buckets[0].CurrencyCode,
            TransferDate = _clock.Today,
            Buckets = buckets,
            ReturnUrl = TryGetLocalReturnUrl(returnUrl, out var local) ? local : null,
            Lines = [new SupplierBalanceTransferLineViewModel()]
        };
        await ApplyDayRateAsync(model);

        await PopulateContractsAsync(model);
        return View(model);
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(SupplierBalanceTransferCreateViewModel model)
    {
        model.ReferenceNumber = string.IsNullOrWhiteSpace(model.ReferenceNumber) ? null : model.ReferenceNumber.Trim();
        model.Notes = string.IsNullOrWhiteSpace(model.Notes) ? null : model.Notes.Trim();
        model.CurrencyCode = SystemCurrency.Normalize(model.CurrencyCode);

        var lines = (model.Lines ?? [])
            .Where(l => l.ContractId > 0 && l.TransferOriginalAmount > 0m)
            .ToList();
        if (lines.Count == 0)
        {
            ModelState.AddModelError(string.Empty, "حد اقل یک قرارداد مقصد با مبلغ باید داده شود.");
        }

        if (!ModelState.IsValid)
        {
            await RefreshFormAsync(model);
            return View(model);
        }

        try
        {
            var created = await _transfers.CreateAsync(new SupplierBalanceTransferCreateRequest(
                model.SupplierId,
                model.CompanyId,
                model.TransferDate,
                model.CurrencyCode,
                model.TransferPerUsdRate,
                lines.Select(l => new SupplierBalanceTransferLineRequest(
                    l.ContractId,
                    l.TransferOriginalAmount,
                    l.ContractCurrencyPerUsdRate)).ToList(),
                model.ReferenceNumber,
                model.Notes,
                CurrentUserName()));

            foreach (var transfer in created)
            {
                await _audit.LogAsync(
                    nameof(SupplierBalanceTransfer),
                    transfer.Id,
                    AuditAction.Insert,
                    diff: AuditDiffFormatter.ForCreate(
                        ("SupplierId", transfer.SupplierId),
                        ("CompanyId", transfer.CompanyId),
                        ("ContractId", transfer.ContractId),
                        ("TransferDate", transfer.TransferDate),
                        ("TransferOriginalAmount", transfer.TransferOriginalAmount),
                        ("OriginalCurrencyCode", transfer.OriginalCurrencyCode),
                        ("HistoricalFxRateToUsd", transfer.HistoricalFxRateToUsd),
                        ("HistoricalAmountUsd", transfer.HistoricalAmountUsd),
                        ("TransferPerUsdRate", transfer.TransferPerUsdRate),
                        ("TransferValueUsd", transfer.TransferValueUsd),
                        ("ExchangeDifferenceUsd", transfer.ExchangeDifferenceUsd),
                        ("ExchangeDifferenceType", transfer.ExchangeDifferenceType),
                        ("ContractCurrencyCode", transfer.ContractCurrencyCode),
                        ("TransferContractCurrencyAmount", transfer.TransferContractCurrencyAmount),
                        ("BatchId", transfer.BatchId)));
            }

            await _db.SaveChangesAsync();

            var totalUsd = created.Sum(t => t.TransferValueUsd);
            TempData["ok"] = created.Count == 1
                ? $"انتقال ثبت شد: {created[0].TransferOriginalAmount:N2} {created[0].OriginalCurrencyCode} برابر {totalUsd:N2} USD."
                : $"{created.Count} انتقال ثبت شد؛ مجموع {totalUsd:N2} USD.";

            return RedirectToSupplier(model.SupplierId, model.ReturnUrl);
        }
        catch (BusinessRuleException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            await RefreshFormAsync(model);
            return View(model);
        }
    }

    /// <summary>
    /// سوابق انتقال یک تأمین‌کننده. <paramref name="q"/> و <paramref name="status"/> فقط فیلتر
    /// نمایشی‌اند؛ هیچ محاسبهٔ مالی به آن‌ها وابسته نیست و KPIها همیشه از کل سوابق ساخته می‌شوند.
    /// </summary>
    public async Task<IActionResult> History(int supplierId, string? q = null, string? status = null)
    {
        var supplier = await _db.Suppliers.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == supplierId);
        if (supplier is null)
        {
            return NotFound();
        }

        var rows = await _db.SupplierBalanceTransfers
            .AsNoTracking()
            .Include(t => t.Contract)
            .Include(t => t.Sources)
            .Where(t => t.SupplierId == supplierId)
            .OrderByDescending(t => t.TransferDate)
            .ThenByDescending(t => t.Id)
            .Select(t => new SupplierBalanceTransferHistoryRowViewModel
            {
                Id = t.Id,
                TransferDate = t.TransferDate,
                ContractId = t.ContractId,
                ContractName = t.Contract != null ? t.Contract.ContractName : "",
                ContractNumber = t.Contract != null ? t.Contract.ContractNumber : "",
                TransferOriginalAmount = t.TransferOriginalAmount,
                OriginalCurrencyCode = t.OriginalCurrencyCode,
                HistoricalAmountUsd = t.HistoricalAmountUsd,
                TransferPerUsdRate = t.TransferPerUsdRate,
                TransferValueUsd = t.TransferValueUsd,
                ExchangeDifferenceUsd = t.ExchangeDifferenceUsd,
                ExchangeDifferenceType = t.ExchangeDifferenceType,
                ContractCurrencyCode = t.ContractCurrencyCode,
                TransferContractCurrencyAmount = t.TransferContractCurrencyAmount,
                ReferenceNumber = t.ReferenceNumber,
                Status = t.Status,
                ReversedAtUtc = t.ReversedAtUtc,
                ReversalReason = t.ReversalReason,
                CreatedByUserName = t.CreatedByUserName,
                Sources = t.Sources
                    .OrderBy(s => s.SourceDate)
                    .Select(s => new SupplierTransferableSourceViewModel
                    {
                        SourceType = s.SourceType,
                        SourceDate = s.SourceDate,
                        CurrencyCode = s.OriginalCurrencyCode,
                        RemainingOriginalAmount = s.ConsumedOriginalAmount,
                        RemainingBookAmountUsd = s.ConsumedBookAmountUsd
                    })
                    .ToList()
            })
            .ToListAsync();

        var normalizedStatus = status?.Trim().ToLowerInvariant() switch
        {
            "active" => "active",
            "reversed" => "reversed",
            _ => null
        };
        var term = string.IsNullOrWhiteSpace(q) ? null : q.Trim();

        var visibleRows = rows
            .Where(r => normalizedStatus switch
            {
                "active" => r.IsActive,
                "reversed" => !r.IsActive,
                _ => true
            })
            .Where(r => term is null
                || r.ContractDisplayLabel.Contains(term, StringComparison.OrdinalIgnoreCase)
                || (r.ReferenceNumber ?? string.Empty).Contains(term, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var activeRows = rows.Where(r => r.IsActive).ToList();

        return View(new SupplierBalanceTransferHistoryViewModel
        {
            SupplierId = supplierId,
            SupplierName = supplier.Name,
            Rows = visibleRows,
            ActiveTotalUsd = activeRows.Sum(r => r.TransferValueUsd),
            ActiveExchangeDifferenceUsd = activeRows.Sum(r => r.ExchangeDifferenceUsd),
            TotalCount = rows.Count,
            ActiveCount = activeRows.Count,
            ReversedCount = rows.Count - activeRows.Count,
            Query = term,
            Status = normalizedStatus
        });
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Reverse(int id, string? reason, string? returnUrl = null)
    {
        var supplierId = await _db.SupplierBalanceTransfers
            .AsNoTracking()
            .Where(t => t.Id == id)
            .Select(t => (int?)t.SupplierId)
            .FirstOrDefaultAsync();

        if (supplierId is null)
        {
            return NotFound();
        }

        try
        {
            var transfer = await _transfers.ReverseAsync(new SupplierBalanceTransferReverseRequest(
                id,
                reason ?? string.Empty,
                CurrentUserName()));

            await _audit.LogAndSaveAsync(
                nameof(SupplierBalanceTransfer),
                transfer.Id,
                AuditAction.Reverse,
                diff: AuditDiffFormatter.ForUpdate(
                    ("Status", SupplierBalanceTransferStatus.Active, transfer.Status),
                    ("ReversalReason", null, transfer.ReversalReason)));

            TempData["ok"] = "انتقال برگشت داده شد.";
        }
        catch (BusinessRuleException ex)
        {
            TempData["error"] = ex.Message;
        }

        if (TryGetLocalReturnUrl(returnUrl, out var local))
        {
            return Redirect(local);
        }

        return RedirectToAction(nameof(History), new { supplierId = supplierId.Value });
    }

    /// <summary>پیش‌نمایش زندهٔ فورم: مانده و ارزش دالری با نرخ واردشده.</summary>
    [Authorize(Policy = AuthPolicies.ManageData)]
    public async Task<IActionResult> BalanceJson(int supplierId)
    {
        var balance = await _balances.GetAsync(supplierId);
        return Json(new
        {
            transferableTotalUsd = balance.TransferableTotalUsd,
            companies = balance.Companies.Select(c => new
            {
                companyId = c.CompanyId,
                companyName = c.CompanyName,
                claimUsd = c.ClaimUsd,
                transferableTotalUsd = c.TransferableTotalUsd,
                buckets = c.Buckets.Select(b => new
                {
                    currency = b.CurrencyCode,
                    remaining = b.RemainingOriginalAmount,
                    bookUsd = b.RemainingBookAmountUsd,
                    historicalPerUsd = b.WeightedHistoricalPerUsdRate,
                    historicalRateIsEstimated = b.RateIsEstimated
                })
            }),
            unknownCompanyUsd = balance.UnknownCompany.OutflowUsd
        });
    }

    /// <summary>
    /// مقدار پیش‌فرض «نرخ روز انتقال».
    ///
    /// نرخ تاریخی عمداً اینجا استفاده نمی‌شود: نرخ تاریخی می‌گوید این پول با چه نرخی وارد
    /// حساب شده، نه اینکه امروز چقدر می‌ارزد. اگر نرخ تاریخی به‌عنوان نرخ امروز جا بیفتد،
    /// سود/زیان تسعیر همیشه صفر می‌شود و اختلاف واقعی نرخ پنهان می‌ماند.
    ///
    /// پس فقط نرخ روزِ ثبت‌شدهٔ همان تاریخ گذاشته می‌شود؛ اگر نرخی ثبت نشده باشد، فیلد
    /// خالی می‌ماند تا کاربر خودش وارد کند.
    /// </summary>
    private async Task ApplyDayRateAsync(SupplierBalanceTransferCreateViewModel model)
    {
        if (model.IsUsd)
        {
            model.TransferPerUsdRate = 1m;
            model.DayRateSource = null;
            return;
        }

        try
        {
            var fx = await _pricing.GetFxRateAsync(
                model.CurrencyCode,
                SystemCurrency.BaseCurrencyCode,
                model.TransferDate.Date);

            // DailyFxRate در جهت «ارز → دالر» است؛ فورم «۱ دالر = چند واحد» می‌خواهد.
            // این تنها معکوس‌سازیِ باقی‌مانده در مسیر است و چاره‌ای ندارد، چون خودِ جدول نرخ
            // فقط همان جهت را نگه می‌دارد. با دقت ۱۲ رقمِ ستون، خطای برگشت حدود 1e-11 است؛
            // گردکردن به ۶ رقم آن را حذف می‌کند تا کاربر «۸۵.۰۰۰۰۰۰۰۰۲۵۵» نبیند.
            // این فقط مقدار پیشنهادیِ فورم است و کاربر می‌تواند عوضش کند؛ نرخ تاریخی
            // هیچ‌جای این مسیر دخالت ندارد.
            model.TransferPerUsdRate = decimal.Round(
                FxRateMath.PerUsdFromToUsd(fx.Value), 6, MidpointRounding.AwayFromZero);
            model.DayRateSource = fx.FallbackApplied
                ? $"نرخ روز {fx.EffectiveDate:yyyy-MM-dd} (نزدیک‌ترین نرخ ثبت‌شده)"
                : $"نرخ روز {fx.EffectiveDate:yyyy-MM-dd}";
        }
        catch (BusinessRuleException)
        {
            // نرخ روزی برای این تاریخ ثبت نشده — عمداً هیچ نرخی حدس زده نمی‌شود.
            model.TransferPerUsdRate = 0m;
            model.DayRateSource = null;
            model.DayRateMissing = true;
        }
    }

    internal static IReadOnlyList<SupplierTransferableBucketViewModel> MapBuckets(
        SupplierCompanyTransferableBalance balance)
        => balance.Buckets
            .Select(b => new SupplierTransferableBucketViewModel
            {
                CurrencyCode = b.CurrencyCode,
                RemainingOriginalAmount = b.RemainingOriginalAmount,
                RemainingBookAmountUsd = b.RemainingBookAmountUsd,
                HistoricalPerUsdRate = b.WeightedHistoricalPerUsdRate,
                HistoricalRateIsEstimated = b.RateIsEstimated,
                Sources = b.Slices
                    .Select(s => new SupplierTransferableSourceViewModel
                    {
                        SourceType = s.SourceType,
                        SourceDate = s.SourceDate,
                        CurrencyCode = s.CurrencyCode,
                        RemainingOriginalAmount = s.RemainingOriginalAmount,
                        RemainingBookAmountUsd = s.RemainingBookAmountUsd,
                        Description = s.Description
                    })
                    .ToList()
            })
            .ToList();

    private async Task RefreshFormAsync(SupplierBalanceTransferCreateViewModel model)
    {
        var supplier = await _db.Suppliers.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == model.SupplierId);
        model.SupplierName = supplier?.Name ?? string.Empty;

        var balance = await _balances.GetAsync(model.SupplierId);
        var company = balance.Company(model.CompanyId);
        model.CompanyName = company?.CompanyName ?? string.Empty;
        model.Buckets = company is null ? [] : MapBuckets(company);
        if (model.Lines is null || model.Lines.Count == 0)
        {
            model.Lines = [new SupplierBalanceTransferLineViewModel()];
        }

        await PopulateContractsAsync(model);
    }

    /// <summary>
    /// قراردادهای خرید همان تأمین‌کننده.
    /// شرکت اثبات‌شده → فقط قراردادهای همان شرکت. سطح گروه → همهٔ شرکت‌ها، چون شرکتِ هر ردیف
    /// از قرارداد مقصدش گرفته می‌شود و محدودکردنش کاربر را بی‌دلیل به مسیر دستی می‌راند.
    /// </summary>
    private async Task PopulateContractsAsync(SupplierBalanceTransferCreateViewModel model)
    {
        var query = _db.Contracts
            .AsNoTracking()
            .Where(c => c.ContractType == ContractType.Purchase && c.SupplierId == model.SupplierId);

        if (!model.IsGroupLevel)
        {
            query = query.Where(c => c.CompanyId == model.CompanyId);
        }

        var contracts = await query
            .OrderByDescending(c => c.ContractDate)
            .ThenBy(c => c.ContractNumber)
            .Take(LookupLimit)
            .Select(c => new { c.Id, c.ContractName, c.ContractNumber, c.Currency, CompanyName = c.Company!.Name })
            .ToListAsync();

        ViewBag.TransferContracts = new SelectList(
            contracts.Select(c => new ContractLookupOption(
                c.Id,
                // در حالت سطح گروه، نام شرکت کنار قرارداد می‌آید تا کاربر بداند هر ردیف به کدام
                // شرکت می‌نشیند — بدون اینکه مجبور به انتخاب دستی شرکت شود.
                model.IsGroupLevel
                    ? $"{ContractUiText.FormatDisplayLabel(c.ContractName, c.ContractNumber)} — {c.CompanyName}"
                    : ContractUiText.FormatDisplayLabel(c.ContractName, c.ContractNumber))),
            "Id",
            "Display");
        ViewBag.TransferContractCurrencies = contracts
            .ToDictionary(c => c.Id, c => SystemCurrency.Normalize(c.Currency));
    }

    private IActionResult RedirectToSupplier(int supplierId, string? returnUrl)
        => TryGetLocalReturnUrl(returnUrl, out var local)
            ? Redirect(local)
            : RedirectToAction("Details", "Suppliers", new { id = supplierId });

    private string? CurrentUserName()
        => User?.FindFirstValue(AppClaimTypes.Username) ?? User?.Identity?.Name;

    private bool TryGetLocalReturnUrl(string? returnUrl, out string localReturnUrl)
    {
        if (!string.IsNullOrWhiteSpace(returnUrl) && Url?.IsLocalUrl(returnUrl) == true)
        {
            localReturnUrl = returnUrl;
            return true;
        }

        localReturnUrl = string.Empty;
        return false;
    }
}
