using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Helpers;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.Payments;
using PTGOilSystem.Web.Models.Reports;
using PTGOilSystem.Web.Models.Suppliers;
using PTGOilSystem.Web.Security;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Audit;
using PTGOilSystem.Web.Services.CompanyFlow;
using PTGOilSystem.Web.Services.DeleteSafety;
using PTGOilSystem.Web.Services.PartyStatements;
using PTGOilSystem.Web.Models.PartyStatements;

namespace PTGOilSystem.Web.Controllers;

[Authorize]
public partial class SuppliersController : Controller
{
    private readonly ApplicationDbContext _db;
    private readonly IAuditService _audit;
    private readonly MasterDataDeleteSafetyService _deleteSafety;
    private readonly IPurchaseAggregationService _purchaseAggregation;
    private readonly IPartyStatementReadService? _partyStatements;
    private readonly IPartyBalanceReadService _partyBalances;
    // موتور واحد «مانده قابل انتقال». nullable است تا سازنده‌های موجود و تست‌ها دست‌نخورده بمانند.
    private readonly ISupplierTransferableBalanceService? _transferableBalances;

    public SuppliersController(
        ApplicationDbContext db,
        IAuditService audit,
        MasterDataDeleteSafetyService deleteSafety,
        IPurchaseAggregationService? purchaseAggregation = null,
        IPartyStatementReadService? partyStatements = null,
        ISupplierTransferableBalanceService? transferableBalances = null,
        IPartyBalanceReadService? partyBalances = null)
    {
        _db = db;
        _audit = audit;
        _deleteSafety = deleteSafety;
        _purchaseAggregation = purchaseAggregation ?? new PurchaseAggregationService(db);
        _partyStatements = partyStatements;
        _transferableBalances = transferableBalances;
        _partyBalances = partyBalances ?? new PartyBalanceReadService(
            db,
            new PartyStatementPolicyResolver(),
            new CompanyFlowDirectionResolver(),
            new CompanyFlowBalanceService());
    }

    public async Task<IActionResult> Index(string? q, int page = 1, [FromQuery(Name = "pageSize")] int? perPage = null)
    {
        var pageSize = ListPageSize.Resolve(perPage, 20);
        ViewData["PageSize"] = pageSize;
        ViewData["DefaultPageSize"] = 20;

        var query = _db.Suppliers.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(q))
        {
            query = query.Where(p =>
                (p.Code != null && p.Code.Contains(q))
                || p.Name.Contains(q)
                || (p.NamePersian != null && p.NamePersian.Contains(q))
                || (p.ContactPerson != null && p.ContactPerson.Contains(q)));
        }

        var totalCount = await query.CountAsync();
        var pageCount = Math.Max(1, (int)Math.Ceiling(totalCount / (double)pageSize));
        page = Math.Clamp(page, 1, pageCount);

        ViewData["q"] = q;

        var items = await query
            .OrderBy(p => p.Name)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(p => new SupplierIndexItemViewModel
            {
                SupplierId = p.Id,
                Code = p.Code,
                Name = p.Name,
                IsActive = p.IsActive
            })
            .ToListAsync();

        await HydrateSupplierIndexMetricsAsync(items);

        return View(new SupplierIndexViewModel
        {
            Search = q,
            Items = items,
            CurrentPage = page,
            PageCount = pageCount,
            TotalCount = totalCount
        });
    }

    public async Task<IActionResult> Details(int id, int? contractId = null, string? tab = null, string? currency = null)
    {
        var displayCurrency = NormalizeDisplayCurrency(currency);
        var item = await BuildSupplierProfileAsync(id, contractId, tab, displayCurrency);
        if (item == null) return NotFound();
        if (_partyStatements is not null)
        {
            var statement = await _partyStatements.GetStatementAsync(
                new PartyRef(PartyStatementPartyType.Supplier, id),
                new PartyStatementFilter
                {
                    ContractId = contractId,
                    // ارز فقط در نمای روبل پاس می‌شود. در سرویس، CurrencyCode = RUB یعنی
                    // «نمایش روبلی»، ولی هر ارز دیگری فیلترِ اسناد است؛ پس در نمای USD چیزی
                    // پاس نمی‌شود تا خلاصهٔ دالریِ فعلی مو‌به‌مو مثل قبل بماند.
                    CurrencyCode = IsRubCurrency(displayCurrency) ? "RUB" : null,
                    IncludeOperationalColumns = false
                },
                HttpContext.RequestAborted);
            ViewData["PartyStatementSummary"] = statement.Summary;
            ViewData["PartyStatementRecentRows"] = statement.Rows.Where(r => !r.IsOpeningBalance).Reverse().Take(5).ToList();
        }

        // کارت «مانده قابل انتقال» — از موتور واحد خوانده می‌شود، نه از پرداخت‌های تکی.
        // از ViewData می‌آید تا SupplierProfileViewModel و مسیر ساخت آن دست‌نخورده بماند.
        var transferable = await BuildTransferableCardAsync(id, HttpContext?.RequestAborted ?? CancellationToken.None);
        if (transferable is not null)
        {
            ViewData["SupplierTransferableBalance"] = transferable;
        }

        return View(item);
    }

    /// <summary>
    /// کارت مانده قابل انتقال. اگر سرویس تزریق نشده باشد (تست‌های قدیمی) null برمی‌گرداند و
    /// صفحه دقیقاً مثل قبل رندر می‌شود.
    /// </summary>
    private async Task<SupplierTransferableBalanceCardViewModel?> BuildTransferableCardAsync(
        int supplierId,
        CancellationToken ct)
    {
        if (_transferableBalances is null)
        {
            return null;
        }

        var balance = await _transferableBalances.GetAsync(supplierId, ct);
        var activeTransferCount = await _db.SupplierBalanceTransfers
            .AsNoTracking()
            .CountAsync(t => t.SupplierId == supplierId && t.Status == SupplierBalanceTransferStatus.Active, ct);

        return new SupplierTransferableBalanceCardViewModel
        {
            SupplierId = supplierId,
            ActiveTransferCount = activeTransferCount,
            UnknownCompanyOutflowUsd = balance.UnknownCompany.OutflowUsd,
            UnknownCompanyRowCount = balance.UnknownCompany.RowCount,
            // AllPools = شرکت‌های اثبات‌شده + سطل سطح گروه. سطل سطح گروه هم مثل بقیه رندر و
            // قابل انتقال است؛ نبودِ شرکتِ منبع دیگر مانده را از UI حذف نمی‌کند.
            Companies = balance.AllPools
                .Select(c => new SupplierCompanyBalanceViewModel
                {
                    CompanyId = c.CompanyId,
                    CompanyName = c.CompanyName,
                    NetAccountBalanceUsd = c.NetAccountBalanceUsd,
                    ClaimUsd = c.ClaimUsd,
                    TransferableTotalUsd = c.TransferableTotalUsd,
                    ConsumedUsd = c.ConsumedByLegacyAllocationsUsd + c.ConsumedByTransfersUsd,
                    IsGroupLevel = c.CompanyId == SupplierTransferableBalance.GroupLevelCompanyId,
                    Buckets = SupplierBalanceTransfersController.MapBuckets(c)
                })
                .ToList()
        };
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    public IActionResult Create() => View(new Supplier());

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create([Bind("Id,Code,Name,NamePersian,Country,ContactPerson,Phone,Address,IsActive,Notes")] Supplier model, string? returnUrl = null)
    {
        Normalize(model);
        if (!ModelState.IsValid) return View(model);
        model.CreatedAtUtc = DateTime.UtcNow;
        _db.Suppliers.Add(model);
        await _db.SaveChangesAsync();
        TempData["ok"] = "تأمین‌کننده با موفقیت ثبت شد.";
        if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl)) return LocalRedirect(returnUrl);
        return RedirectToAction(nameof(Index));
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    public async Task<IActionResult> Edit(int id)
    {
        var item = await _db.Suppliers.FirstOrDefaultAsync(x => x.Id == id);
        if (item == null) return NotFound();
        return View(item);
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, [Bind("Id,Code,Name,NamePersian,Country,ContactPerson,Phone,Address,IsActive,Notes")] Supplier model)
    {
        if (id != model.Id) return BadRequest();
        Normalize(model);
        if (!ModelState.IsValid) return View(model);
        var existing = await _db.Suppliers.FirstOrDefaultAsync(x => x.Id == id);
        if (existing == null) return NotFound();
        existing.Code = model.Code;
        existing.Name = model.Name;
        existing.NamePersian = model.NamePersian;
        existing.Country = model.Country;
        existing.ContactPerson = model.ContactPerson;
        existing.Phone = model.Phone;
        existing.Address = model.Address;
        existing.IsActive = model.IsActive;
        existing.Notes = model.Notes;
        existing.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        TempData["ok"] = "ویرایش با موفقیت انجام شد.";
        return RedirectToAction(nameof(Index));
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        var item = await _db.Suppliers.FirstOrDefaultAsync(x => x.Id == id);
        if (item == null) return NotFound();

        var safety = await _deleteSafety.EvaluateSupplierAsync(id);
        if (!safety.CanDelete)
        {
            if (safety.ArchiveInsteadOfDelete && item.IsActive)
            {
                var archiveDiff = $"ArchiveInsteadOfDelete: {safety.DependencySummary} | "
                    + AuditDiffFormatter.ForUpdate(("IsActive", item.IsActive, false));
                item.IsActive = false;
                await _db.SaveChangesAsync();
                await _audit.LogAndSaveAsync(nameof(Supplier), item.Id, AuditAction.Update, diff: archiveDiff);
                TempData["ok"] = safety.BuildArchivedMessage("تأمین‌کننده");
                return RedirectToAction(nameof(Index));
            }

            TempData["err"] = safety.ArchiveInsteadOfDelete
                ? $"{safety.BuildBlockedMessage("تأمین‌کننده")} این رکورد قبلاً غیرفعال شده است."
                : safety.BuildBlockedMessage("تأمین‌کننده");
            return RedirectToAction(nameof(Index));
        }

        var deleteDiff = AuditDiffFormatter.ForDelete(
            ("Name", item.Name),
            ("Country", item.Country),
            ("ContactPerson", item.ContactPerson));
        _db.Suppliers.Remove(item);
        await _db.SaveChangesAsync();
        await _audit.LogAndSaveAsync(nameof(Supplier), item.Id, AuditAction.Delete, diff: deleteDiff);
        TempData["ok"] = "تأمین‌کننده حذف شد.";
        return RedirectToAction(nameof(Index));
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ToggleActive(int id, string? returnUrl = null)
    {
        var item = await _db.Suppliers.FirstOrDefaultAsync(x => x.Id == id);
        if (item is null) return NotFound();

        var wasActive = item.IsActive;
        item.IsActive = !item.IsActive;
        await _db.SaveChangesAsync();
        await _audit.LogAndSaveAsync(nameof(Supplier), item.Id, AuditAction.Update,
            diff: AuditDiffFormatter.ForUpdate(("IsActive", wasActive, item.IsActive)));

        TempData["ok"] = item.IsActive ? "رکورد فعال شد." : "رکورد غیرفعال شد.";
        if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl)) return Redirect(returnUrl);
        return RedirectToAction(nameof(Index));
    }

    private async Task HydrateSupplierIndexMetricsAsync(IReadOnlyList<SupplierIndexItemViewModel> items)
    {
        if (items.Count == 0)
        {
            return;
        }

        var supplierIds = items.Select(i => i.SupplierId).ToArray();
        var bySupplierId = items.ToDictionary(i => i.SupplierId);

        var contracts = await _db.Contracts
            .AsNoTracking()
            .Where(c => c.ContractType == ContractType.Purchase
                && c.SupplierId.HasValue
                && supplierIds.Contains(c.SupplierId.Value))
            .ToListAsync();
        var finalPriceByContract = contracts.ToDictionary(
            c => c.Id,
            c => ContractPricingAdapter.GetCanonicalFinalPrice(c));
        var purchaseAggregates = await _purchaseAggregation.AggregateForContractsAsync(
            contracts.Select(c => c.Id).ToArray(),
            finalPriceByContract);

        foreach (var group in contracts.GroupBy(c => c.SupplierId))
        {
            if (!group.Key.HasValue || !bySupplierId.TryGetValue(group.Key.Value, out var item))
            {
                continue;
            }

            item.PurchaseContractsCount = group.Count();
            item.ActivePurchaseContractsCount = group.Count(c => c.Status == ContractStatus.Active);
            item.TotalPurchaseQuantityMt = group.Sum(c => c.QuantityMt);
            item.LoadedPurchaseValueUsd = group.Sum(c =>
                purchaseAggregates.TryGetValue(c.Id, out var aggregate)
                    ? aggregate.TraceablePurchaseCostUsd
                    : 0m);
        }

        // AUD-05: ماندهٔ List از همان bulk read-model رسمی می‌آید که Management استفاده می‌کند.
        // این مسیر با PartyStatement در policy/direction/closing formula مشترک است؛ List دیگر
        // Debit/Credit یا LoadedValue-Paid را به‌عنوان «Balance» دوباره محاسبه نمی‌کند.
        var officialBalances = (await _partyBalances.GetBalancesAsync(
                new ManagementReportFilterViewModel(),
                HttpContext?.RequestAborted ?? CancellationToken.None))
            .Where(row => row.PartyType == PartyStatementPartyType.Supplier
                && bySupplierId.ContainsKey(row.PartyId))
            .ToDictionary(row => row.PartyId);

        foreach (var item in items)
        {
            if (!officialBalances.TryGetValue(item.SupplierId, out var balance))
            {
                continue;
            }

            item.LedgerOutflowUsd = balance.TotalOutflowUsd;
            item.LedgerReceiptUsd = balance.TotalReceiptUsd;
        }

        var paymentRows = await _db.PaymentTransactions
            .AsNoTracking()
            .Include(p => p.Contract)
            .Where(p =>
                (p.SupplierId.HasValue && supplierIds.Contains(p.SupplierId.Value))
                || (p.Contract != null
                    && p.Contract.ContractType == ContractType.Purchase
                    && p.Contract.SupplierId.HasValue
                    && supplierIds.Contains(p.Contract.SupplierId.Value)))
            .Select(p => new SupplierPaymentMetricProjection
            {
                PaymentId = p.Id,
                DirectSupplierId = p.SupplierId,
                ContractSupplierId = p.Contract != null && p.Contract.ContractType == ContractType.Purchase
                    ? p.Contract.SupplierId
                    : null,
                PaymentDate = p.PaymentDate,
                PaymentKind = p.PaymentKind,
                AmountUsd = p.AmountUsd
            })
            .ToListAsync();

        foreach (var group in ExpandSupplierPaymentLinks(paymentRows)
            .GroupBy(x => new { x.SupplierId, x.Payment.PaymentId })
            .Select(g => g.First())
            .GroupBy(x => x.SupplierId))
        {
            if (!bySupplierId.TryGetValue(group.Key, out var item))
            {
                continue;
            }

            item.TotalPaidUsd = group
                .Where(x => x.Payment.PaymentKind == PaymentKind.SupplierPayment)
                .Sum(x => x.Payment.AmountUsd);
            item.LastPaymentDate = group.Max(x => (DateTime?)x.Payment.PaymentDate);
        }
    }

    // نمای ارز فقط نمایشی است: هیچ مبلغی بر اساس آن دوباره محاسبه یا تبدیل نمی‌شود.
    private static string NormalizeDisplayCurrency(string? currency)
        => IsRubCurrency(currency) ? "RUB" : "USD";

    private async Task<SupplierProfileViewModel?> BuildSupplierProfileAsync(
        int id,
        int? contractId,
        string? tab,
        string displayCurrency = "USD")
    {
        var supplier = await _db.Suppliers
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id);
        if (supplier == null)
        {
            return null;
        }

        var contracts = await _db.Contracts
            .AsNoTracking()
            .Include(c => c.Product)
            .Where(c => c.ContractType == ContractType.Purchase && c.SupplierId == supplier.Id)
            .OrderByDescending(c => c.ContractDate)
            .ThenBy(c => c.ContractNumber)
            .ToListAsync();
        var contractIds = contracts.Select(c => c.Id).ToHashSet();

        if (contractId.HasValue && !contractIds.Contains(contractId.Value))
        {
            return null;
        }

        // Projected to a slim read-model (only fields the totals/statement/links read)
        // instead of full LedgerEntry + full Contract per row. Same filter/order;
        // Contract scalar parts carried so RUB/USD helpers compute identical values.
        var ledgers = (await _db.LedgerEntries
                .AsNoTracking()
                // انتساب مرکزی (همان قانونِ صورت‌حساب) تا کرایهٔ حمل و اسنادِ طرف دیگر
                // در پروفایل/مانده تأمین‌کننده هم شمرده نشوند.
                .Where(LedgerEntryOwnership.SupplierOwned(supplier.Id))
                .OrderBy(l => l.EntryDate)
                .ThenBy(l => l.Id)
                .Select(l => new SupplierLedgerProjection
                {
                    Id = l.Id,
                    EntryDate = l.EntryDate,
                    Side = l.Side,
                    AmountUsd = l.AmountUsd,
                    SourceType = l.SourceType,
                    SourceId = l.SourceId,
                    SupplierId = l.SupplierId,
                    ContractId = l.ContractId,
                    Currency = l.Currency,
                    SourceCurrencyCode = l.SourceCurrencyCode,
                    SourceAmount = l.SourceAmount,
                    AppliedFxRateToUsd = l.AppliedFxRateToUsd,
                    Reference = l.Reference,
                    Description = l.Description,
                    ContractNumber = l.Contract != null ? l.Contract.ContractNumber : null,
                    ContractRubPerUsdRate = l.Contract != null ? l.Contract.ContractRubPerUsdRate : null,
                    ContractCurrency = l.Contract != null ? l.Contract.Currency : null,
                    ContractAppliedFxRateToUsd = l.Contract != null ? l.Contract.AppliedFxRateToUsd : null
                })
                .ToListAsync())
            .DistinctBy(l => l.Id)
            .ToList();

        // اثرِ جاریِ تسویهٔ صراف: ثبتِ اصلیِ منسوخ‌شده (که با repost جایگزین شده) و سطرِ برگشت
        // نباید در آمار/مانده/صورت‌حساب تأمین‌کننده دوبار شمرده یا جدا نمایش داده شوند. فقط سطری
        // که تسویهٔ Posted به آن اشاره می‌کند می‌ماند. رجوع: SarrafSettlementLedgerEffectiveness.
        var effectiveSarrafLedgerIds = await SarrafSettlementLedgerEffectiveness
            .LoadEffectiveLedgerEntryIdsAsync(_db);
        ledgers = ledgers
            .Where(l => !SarrafSettlementLedgerEffectiveness.IsSuperseded(l.SourceType, l.Id, effectiveSarrafLedgerIds))
            .ToList();

        // Projected to a slim read-model instead of full PaymentTransaction +
        // CashAccount + Contract per row. Same filter/order; CashAccount + Contract
        // scalars carried so display text and RUB/USD values stay identical.
        var payments = (await _db.PaymentTransactions
                .AsNoTracking()
                .Where(p => p.SupplierId == supplier.Id
                    || (p.Contract != null
                        && p.Contract.ContractType == ContractType.Purchase
                        && p.Contract.SupplierId == supplier.Id))
                .OrderByDescending(p => p.PaymentDate)
                .ThenByDescending(p => p.Id)
                .Select(p => new SupplierPaymentProjection
                {
                    Id = p.Id,
                    PaymentDate = p.PaymentDate,
                    Direction = p.Direction,
                    PaymentKind = p.PaymentKind,
                    SarrafId = p.SarrafId,
                    ContractId = p.ContractId,
                    Amount = p.Amount,
                    Currency = p.Currency,
                    AmountUsd = p.AmountUsd,
                    AppliedFxRateToUsd = p.AppliedFxRateToUsd,
                    Reference = p.Reference,
                    Description = p.Description,
                    IsAdvancePayment = p.IsAdvancePayment,
                    LedgerEntryId = p.LedgerEntryId,
                    CreatedAtUtc = p.CreatedAtUtc,
                    CreatedByUserId = p.CreatedByUserId,
                    CashAccountName = p.CashAccount != null ? p.CashAccount.Name : null,
                    CashAccountType = p.CashAccount != null ? p.CashAccount.AccountType : (CashAccountType?)null,
                    ContractNumber = p.Contract != null ? p.Contract.ContractNumber : null,
                    ContractRubPerUsdRate = p.Contract != null ? p.Contract.ContractRubPerUsdRate : null,
                    ContractCurrency = p.Contract != null ? p.Contract.Currency : null,
                    ContractAppliedFxRateToUsd = p.Contract != null ? p.Contract.AppliedFxRateToUsd : null
                })
                .ToListAsync())
            .DistinctBy(p => p.Id)
            .ToList();

        var supplierPaymentIds = payments
            .Where(p => p.PaymentKind == PaymentKind.SupplierPayment)
            .Select(p => p.Id)
            .ToList();

        // انتقال مانده (پیش‌پرداخت آزاد → قرارداد) پرداخت جدید نیست، ولی برای قراردادِ مقصد
        // یک تخصیصِ واقعی است. مبلغ اصلی و ارز از خودِ سند خوانده می‌شود، نه از سطر دفتر:
        // پای مقصد با ارز قرارداد ثبت شده و مبلغ روبلی از آن بازیابی نمی‌شود.
        // این مبلغ فقط به PaidUsd/PaidRub همان قرارداد اضافه می‌شود؛ جمع کل پرداختِ
        // تأمین‌کننده از مسیر مستقل خودش می‌آید و انتقال داخلی آن را تغییر نمی‌دهد.
        var balanceTransfers = await _db.SupplierBalanceTransfers
            .AsNoTracking()
            .Where(t => t.SupplierId == supplier.Id
                && t.Status == SupplierBalanceTransferStatus.Active)
            .Select(t => new SupplierBalanceTransferContractProjection
            {
                Id = t.Id,
                ContractId = t.ContractId,
                ContractNumber = t.Contract != null ? t.Contract.ContractNumber : null,
                TransferDate = t.TransferDate,
                TransferOriginalAmount = t.TransferOriginalAmount,
                OriginalCurrencyCode = t.OriginalCurrencyCode,
                TransferValueUsd = t.TransferValueUsd,
                ReferenceNumber = t.ReferenceNumber
            })
            .ToListAsync();

        var transferPaidByContract = balanceTransfers
            .GroupBy(t => t.ContractId)
            .ToDictionary(g => g.Key, g => g.Sum(t => t.TransferValueUsd));
        var transferPaidRubByContract = balanceTransfers
            .Where(t => IsRubCurrency(t.OriginalCurrencyCode))
            .GroupBy(t => t.ContractId)
            .ToDictionary(g => g.Key, g => (decimal?)g.Sum(t => t.TransferOriginalAmount));
        // Projected to a slim DTO (only the fields the advance UI/totals read) instead
        // of loading full SupplierPaymentAllocation + full Contract per row. Numbers
        // identical: same filter, same order, same scalar values.
        var advanceAllocations = supplierPaymentIds.Count == 0
            ? new List<SupplierAdvanceAllocationProjection>()
            : await _db.SupplierPaymentAllocations
                .AsNoTracking()
                .Where(a => supplierPaymentIds.Contains(a.PaymentTransactionId))
                .OrderByDescending(a => a.AllocationDate)
                .ThenByDescending(a => a.Id)
                .Select(a => new SupplierAdvanceAllocationProjection
                {
                    Id = a.Id,
                    AllocationDate = a.AllocationDate,
                    PaymentTransactionId = a.PaymentTransactionId,
                    Status = a.Status,
                    AllocatedBookAmountUsd = a.AllocatedBookAmountUsd,
                    ContractId = a.ContractId,
                    ContractNumber = a.Contract != null ? a.Contract.ContractNumber : null,
                    ContractCurrencyCode = a.ContractCurrencyCode,
                    AllocatedContractCurrencyAmount = a.AllocatedContractCurrencyAmount
                })
                .ToListAsync();

        // معادل روبلیِ یک پرداخت فقط از منبع قفل‌شدهٔ خودِ همان سند می‌آید:
        //   • پرداخت ذاتاً روبلی → مبلغ واقعی سند.
        //   • پرداخت دالری → فقط اگر تخصیصِ همان پرداخت به ارز روبل قفل شده باشد
        //     (AllocatedContractCurrencyAmount با ContractCurrencyCode = RUB).
        // نرخ عمومی، نرخ امروز یا نسبت پرداخت‌ها هرگز برای ساختن روبل استفاده نمی‌شود.
        var lockedRubByPaymentId = advanceAllocations
            .Where(a => a.Status == SupplierPaymentAllocationStatus.Active
                && IsRubCurrency(a.ContractCurrencyCode)
                && a.AllocatedContractCurrencyAmount > 0m)
            .GroupBy(a => a.PaymentTransactionId)
            .ToDictionary(g => g.Key, g => g.Sum(a => a.AllocatedContractCurrencyAmount));

        decimal? PaymentRub(SupplierPaymentProjection payment)
            => GetPaymentRubEquivalent(payment)
                ?? (lockedRubByPaymentId.TryGetValue(payment.Id, out var lockedRub)
                    ? decimal.Round(lockedRub, 4, MidpointRounding.AwayFromZero)
                    : null);

        // Projected to a slim read-model instead of full SarrafSettlement + Sarraf +
        // Contract per row. Same filter/order; Sarraf name + Contract scalars carried
        // so settlement amounts, RUB/USD and reduction totals stay identical.
        var sarrafSettlements = (await _db.SarrafSettlements
                .AsNoTracking()
                .Where(s => s.SupplierId == supplier.Id
                    || (s.Contract != null
                        && s.Contract.ContractType == ContractType.Purchase
                        && s.Contract.SupplierId == supplier.Id))
                .OrderByDescending(s => s.SettlementDate)
                .ThenByDescending(s => s.Id)
                .Select(s => new SupplierSarrafSettlementProjection
                {
                    Id = s.Id,
                    SettlementDate = s.SettlementDate,
                    SarrafName = s.Sarraf != null ? s.Sarraf.Name : null,
                    ContractId = s.ContractId,
                    ContractNumber = s.Contract != null ? s.Contract.ContractNumber : null,
                    ReferenceNumber = s.ReferenceNumber,
                    Description = s.Description,
                    RequestedAmount = s.RequestedAmount,
                    RequestedCurrency = s.RequestedCurrency,
                    RequestedAmountUsd = s.RequestedAmountUsd,
                    SupplierAcceptedAmount = s.SupplierAcceptedAmount,
                    SupplierAcceptedCurrency = s.SupplierAcceptedCurrency,
                    SupplierAcceptedAmountUsd = s.SupplierAcceptedAmountUsd,
                    SarrafChargedAmount = s.SarrafChargedAmount,
                    SarrafCurrency = s.SarrafCurrency,
                    SarrafChargedAmountUsd = s.SarrafChargedAmountUsd,
                    DifferenceAmountUsd = s.DifferenceAmountUsd,
                    DifferenceType = s.DifferenceType,
                    DifferenceReason = s.DifferenceReason,
                    DifferenceTreatment = s.DifferenceTreatment,
                    Status = s.Status,
                    LedgerEntryId = s.LedgerEntryId,
                    ContractRubPerUsdRate = s.Contract != null ? s.Contract.ContractRubPerUsdRate : null,
                    ContractCurrency = s.Contract != null ? s.Contract.Currency : null,
                    ContractAppliedFxRateToUsd = s.Contract != null ? s.Contract.AppliedFxRateToUsd : null
                })
                .ToListAsync())
            .DistinctBy(s => s.Id)
            .ToList();

        var userIds = payments
            .Select(p => p.CreatedByUserId)
            .Where(userId => userId.HasValue)
            .Select(userId => userId!.Value)
            .Distinct()
            .ToList();
        var users = userIds.Count == 0
            ? new Dictionary<int, string>()
            : await _db.Users
                .AsNoTracking()
                .Where(u => userIds.Contains(u.Id))
                .ToDictionaryAsync(u => u.Id, u => string.IsNullOrWhiteSpace(u.FullName) ? u.Username : u.FullName);

        var supplierAccountLedgers = ledgers
            .Where(l => IsSupplierAccountLedger(l, supplier.Id))
            .ToList();
        var viaSarrafSupplierLedgers = supplierAccountLedgers
            .Where(l => l.SourceType == PaymentsController.ViaSarrafSupplierLedgerSourceType
                && l.Side == LedgerSide.Debit)
            .ToList();
        var sarrafSettlementIds = sarrafSettlements.Select(s => s.Id).ToHashSet();
        var sarrafLedgerRubRows = supplierAccountLedgers
            .Where(IsSarrafSupplierReductionLedger)
            .Select(l => new
            {
                l.SourceId,
                l.ContractId,
                HasExactRub = HasExactRubSource(l),
                AmountRub = GetSarrafLedgerRubPaymentImpact(l)
            })
            .ToList();
        var sarrafLedgerExactRubSourceIds = sarrafLedgerRubRows
            .Where(l => l.HasExactRub && l.SourceId > 0)
            .Select(l => l.SourceId)
            .ToHashSet();
        var sarrafLedgerExactRubBySettlementId = sarrafLedgerRubRows
            .Where(l => l.HasExactRub && l.SourceId > 0)
            .GroupBy(l => l.SourceId)
            .ToDictionary(g => g.Key, g => SumKnown(g.Select(l => l.AmountRub)));
        var sarrafLedgerRubRowsForTotals = sarrafLedgerRubRows
            .Where(l => l.HasExactRub || !sarrafSettlementIds.Contains(l.SourceId))
            .ToList();

        decimal? GetSarrafReductionRubForDisplay(SupplierSarrafSettlementProjection settlement)
            => sarrafLedgerExactRubBySettlementId.TryGetValue(settlement.Id, out var ledgerRub) && ledgerRub.HasValue
                ? ledgerRub.Value
                : GetSarrafSupplierReductionRub(settlement) ?? GetSettlementRubFallbackFromContractLoadings(settlement);

        var ledgerByContract = ledgers
            .Where(l => l.ContractId.HasValue)
            .GroupBy(l => l.ContractId!.Value)
            .ToDictionary(
                g => g.Key,
                g => new LedgerTotals(
                    OutflowUsd: g.Where(l => l.Side == LedgerSide.Debit).Sum(l => l.AmountUsd),
                    ReceiptUsd: g.Where(l => l.Side == LedgerSide.Credit).Sum(l => l.AmountUsd)));

        var directPaidByContract = payments
            .Where(p => p.ContractId.HasValue && p.PaymentKind == PaymentKind.SupplierPayment)
            .GroupBy(p => p.ContractId!.Value)
            .ToDictionary(g => g.Key, g => g.Sum(p => p.AmountUsd));

        var directPaidRubByContract = payments
            .Where(p => p.ContractId.HasValue && p.PaymentKind == PaymentKind.SupplierPayment)
            .GroupBy(p => p.ContractId!.Value)
            .ToDictionary(g => g.Key, g => SumKnown(g.Select(PaymentRub)));

        var sarrafPaidByContract = sarrafSettlements
            .Where(s => s.Status == SarrafSettlementStatus.Posted && s.ContractId.HasValue)
            .GroupBy(s => s.ContractId!.Value)
            .ToDictionary(g => g.Key, g => g.Sum(SupplierReductionAmountUsd));

        var viaSarrafPaidByContract = viaSarrafSupplierLedgers
            .Where(l => l.ContractId.HasValue)
            .GroupBy(l => l.ContractId!.Value)
            .ToDictionary(g => g.Key, g => g.Sum(l => l.AmountUsd));

        var viaSarrafPaidRubByContract = viaSarrafSupplierLedgers
            .Where(l => l.ContractId.HasValue)
            .GroupBy(l => l.ContractId!.Value)
            .ToDictionary(g => g.Key, g => SumKnown(g.Select(GetLedgerRubEquivalent)));

        var finalPriceByContract = contracts.ToDictionary(
            c => c.Id,
            c => ContractPricingAdapter.GetCanonicalFinalPrice(c));
        var purchaseAggregates = await _purchaseAggregation.AggregateForContractsAsync(
            contractIds.ToList(),
            finalPriceByContract);
        var loadingRubRows = await _db.LoadingRegisters
            .AsNoTracking()
            .Where(l => contractIds.Contains(l.ContractId))
            .Select(l => new SupplierLoadingRubProjection
            {
                ContractId = l.ContractId,
                LoadedQuantityMt = l.LoadedQuantityMt,
                LoadingPriceUsd = l.LoadingPriceUsd,
                SettlementCurrencyCode = l.SettlementCurrencyCode,
                RubRateStatus = l.RubRateStatus,
                RubPerUsdRate = l.RubPerUsdRate,
                AmountRubAtRubLock = l.AmountRubAtRubLock,
                SettlementUnitPriceRub = l.SettlementUnitPriceRub,
                SettlementValueRub = l.SettlementValueRub
            })
            .ToListAsync();
        var loadingRubFactsByContract = loadingRubRows
            .GroupBy(l => l.ContractId)
            .ToDictionary(g => g.Key, g => g.Select(ToLoadingRubFacts).ToList());
        // ارزش روبلی هر قرارداد از منبع مشترکِ LoadingRubValuation خوانده می‌شود — همان منبعی
        // که صفحهٔ جریان قرارداد استفاده می‌کند — تا دو صفحه برای یک قرارداد دو عدد ندهند.
        var loadedRubByContract = contracts.ToDictionary(
            c => c.Id,
            c =>
            {
                purchaseAggregates.TryGetValue(c.Id, out var purchaseAgg);
                loadingRubFactsByContract.TryGetValue(c.Id, out var facts);
                finalPriceByContract.TryGetValue(c.Id, out var finalPriceUsd);
                return LoadingRubValuation.AggregateForContract(
                    facts ?? [],
                    finalPriceUsd,
                    IsRubCurrency(c.Currency) ? c.UnitPriceInCurrency : null,
                    purchaseAgg?.TotalLoadedQuantityMt ?? 0m,
                    purchaseAgg?.TraceablePurchaseCostUsd ?? 0m,
                    GetRubPerUsdRate(c));
            });

        // نرخ مؤثر روبل از خودِ بارگیری‌های روبلیِ همین قرارداد: مجموع روبلِ واقعیِ بارگیری‌ها
        // ÷ بهای دالریِ همان بارگیری‌ها. این یک منبع روبلیِ واقعی و قابل‌ردیابی است (نرخ ثبت‌شدهٔ
        // بارگیری، مبلغ روبلی فایل، یا snapshot قفل‌شده) — برخلاف نسبت پرداخت‌ها که منبع نیست.
        // جمعِ تخمینی (تبدیل‌شده با نرخ قرارداد) منبع نیست و نرخ از آن ساخته نمی‌شود.
        decimal? GetLoadingDerivedRubPerUsdRate(int rateContractId)
        {
            if (!loadedRubByContract.TryGetValue(rateContractId, out var loadedRub)
                || loadedRub.IsEstimated
                || !loadedRub.AmountRub.HasValue
                || loadedRub.AmountRub.Value <= 0m
                || !purchaseAggregates.TryGetValue(rateContractId, out var purchaseAgg)
                || purchaseAgg.TraceablePurchaseCostUsd <= 0m)
            {
                return null;
            }

            return decimal.Round(
                loadedRub.AmountRub.Value / purchaseAgg.TraceablePurchaseCostUsd,
                6,
                MidpointRounding.AwayFromZero);
        }

        decimal? GetSettlementRubFallbackFromContractLoadings(SupplierSarrafSettlementProjection settlement)
            => settlement.ContractId.HasValue
                ? ToRubFromUsd(
                    SupplierReductionAmountUsd(settlement),
                    GetLoadingDerivedRubPerUsdRate(settlement.ContractId.Value))
                : null;

        decimal? GetSettlementRubForDisplay(SupplierSarrafSettlementProjection settlement)
        {
            if (sarrafLedgerExactRubBySettlementId.TryGetValue(settlement.Id, out var exactRub) && exactRub.HasValue)
            {
                return exactRub.Value;
            }

            return GetSarrafSupplierReductionRub(settlement)
                ?? GetSettlementRubFallbackFromContractLoadings(settlement);
        }

        var sarrafPaidRubByContract = sarrafLedgerRubRowsForTotals
            .Where(l => l.ContractId.HasValue)
            .GroupBy(l => l.ContractId!.Value)
            .ToDictionary(g => g.Key, g => SumKnown(g.Select(l => l.AmountRub)));
        var sarrafPaidRubFallbackByContract = sarrafSettlements
            .Where(s => s.Status == SarrafSettlementStatus.Posted && s.ContractId.HasValue)
            .Where(s => !sarrafLedgerExactRubSourceIds.Contains(s.Id))
            .GroupBy(s => s.ContractId!.Value)
            .ToDictionary(g => g.Key, g => SumKnown(g.Select(GetSettlementRubForDisplay)));
        foreach (var fallback in sarrafPaidRubFallbackByContract)
        {
            sarrafPaidRubByContract[fallback.Key] = sarrafPaidRubByContract.TryGetValue(fallback.Key, out var existing)
                ? SumKnown([existing, fallback.Value])
                : fallback.Value;
        }

        var contractSummaries = contracts
            .Select(c =>
            {
                var finalPriceUsd = finalPriceByContract[c.Id];
                purchaseAggregates.TryGetValue(c.Id, out var purchaseAgg);
                ledgerByContract.TryGetValue(c.Id, out var ledger);
                directPaidByContract.TryGetValue(c.Id, out var directPaidUsd);
                sarrafPaidByContract.TryGetValue(c.Id, out var sarrafPaidUsd);
                viaSarrafPaidByContract.TryGetValue(c.Id, out var viaSarrafPaidUsd);
                directPaidRubByContract.TryGetValue(c.Id, out var directPaidRub);
                sarrafPaidRubByContract.TryGetValue(c.Id, out var sarrafPaidRub);
                viaSarrafPaidRubByContract.TryGetValue(c.Id, out var viaSarrafPaidRub);
                transferPaidByContract.TryGetValue(c.Id, out var transferPaidUsd);
                transferPaidRubByContract.TryGetValue(c.Id, out var transferPaidRub);
                // نرخ روبلِ نمایش فقط از منبع روبلیِ واقعی می‌آید: نرخ ثابت قرارداد، یا نرخ مؤثرِ
                // بارگیری‌های روبلیِ همین قرارداد. نرخ از نسبت پرداخت‌ها ساخته نمی‌شود؛ پرداخت روبلی
                // «قیمتِ خرید» را روبلی نمی‌کند و در پرداخت ترکیبی USD/RUB آن نسبت رقیق و نادرست است.
                // قرارداد و بارگیریِ کاملاً دالری بدون نرخ، معادل روبلی نمی‌گیرد (null می‌ماند).
                var rubPerUsdRate = GetRubPerUsdRate(c) ?? GetLoadingDerivedRubPerUsdRate(c.Id);
                var estimatedTotalRub = GetContractTotalRub(c, finalPriceUsd, rubPerUsdRate);
                var loadedQuantityMt = purchaseAgg?.TotalLoadedQuantityMt ?? 0m;
                loadedRubByContract.TryGetValue(c.Id, out var loadingRub);
                var loadedValueRub = loadingRub.AmountRub;
                return new SupplierContractSummaryViewModel
                {
                    ContractId = c.Id,
                    ContractNumber = c.ContractNumber,
                    Product = c.Product?.Name ?? "-",
                    ContractDate = c.ContractDate,
                    QuantityMt = c.QuantityMt,
                    Currency = c.Currency,
                    UnitPriceOriginal = c.UnitPriceInCurrency,
                    FinalPriceUsd = finalPriceUsd,
                    RubPerUsdRate = rubPerUsdRate,
                    EstimatedTotalOriginal = c.UnitPriceInCurrency.HasValue && c.UnitPriceInCurrency.Value > 0m
                        ? decimal.Round(c.QuantityMt * c.UnitPriceInCurrency.Value, 4, MidpointRounding.AwayFromZero)
                        : null,
                    EstimatedTotalUsd = finalPriceUsd.HasValue && finalPriceUsd.Value > 0m
                        ? decimal.Round(c.QuantityMt * finalPriceUsd.Value, 4, MidpointRounding.AwayFromZero)
                        : null,
                    EstimatedTotalRub = estimatedTotalRub,
                    LoadedQuantityMt = loadedQuantityMt,
                    LoadedValueUsd = purchaseAgg?.TraceablePurchaseCostUsd ?? 0m,
                    LoadedValueRub = loadedValueRub,
                    LoadedValueRubIsEstimated = loadingRub.IsEstimated,
                    LedgerOutflowUsd = ledger?.OutflowUsd ?? 0m,
                    LedgerReceiptUsd = ledger?.ReceiptUsd ?? 0m,
                    PaidUsd = directPaidUsd + sarrafPaidUsd + viaSarrafPaidUsd + transferPaidUsd,
                    PaidRub = SumKnown([directPaidRub, sarrafPaidRub, viaSarrafPaidRub, transferPaidRub]),
                    Status = c.Status,
                    StatusName = GetContractStatusName(c.Status),
                    StatusBadgeClass = GetContractStatusBadgeClass(c.Status)
                };
            })
            .ToList();

        var paymentSummaries = payments
            .Select(p => new SupplierPaymentSummaryViewModel
            {
                PaymentId = p.Id,
                PaymentDate = p.PaymentDate,
                Direction = p.Direction,
                DirectionName = PaymentDirectionLabels.ToPersian(p.Direction),
                PaymentKind = p.PaymentKind,
                PaymentKindName = PaymentKindLabels.ToPersian(p.PaymentKind),
                CashAccount = p.CashAccountName ?? string.Empty,
                ContractId = p.ContractId,
                ContractNumber = p.ContractNumber,
                Amount = p.Amount,
                Currency = p.Currency,
                AppliedFxRateToUsd = p.AppliedFxRateToUsd,
                AmountUsd = p.AmountUsd,
                RubPerUsdRate = GetRubPerUsdRate(p),
                AmountRubEquivalent = PaymentRub(p),
                Reference = p.Reference,
                Description = p.Description,
                IsAdvancePayment = p.IsAdvancePayment,
                LedgerEntryId = p.LedgerEntryId,
                CreatedAtUtc = p.CreatedAtUtc,
                CreatedByUserId = p.CreatedByUserId,
                CreatedByDisplay = p.CreatedByUserId.HasValue && users.TryGetValue(p.CreatedByUserId.Value, out var userName)
                    ? userName
                    : null
            })
            .ToList();

        var sarrafSettlementSummaries = sarrafSettlements
            .Select(s => new SupplierSarrafSettlementViewModel
            {
                Id = s.Id,
                SettlementDate = s.SettlementDate,
                SarrafName = s.SarrafName ?? string.Empty,
                ContractId = s.ContractId,
                ContractNumber = s.ContractNumber,
                ReferenceNumber = s.ReferenceNumber,
                RequestedAmountUsd = s.RequestedAmountUsd,
                RequestedAmountRub = GetSarrafAmountRub(s.RequestedAmount, s.RequestedCurrency, s.RequestedAmountUsd, s),
                SupplierAcceptedAmountUsd = s.SupplierAcceptedAmountUsd,
                SupplierAcceptedAmountRub = GetSarrafAmountRub(s.SupplierAcceptedAmount, s.SupplierAcceptedCurrency, s.SupplierAcceptedAmountUsd, s),
                SupplierReductionAmountUsd = SupplierReductionAmountUsd(s),
                SupplierReductionAmountRub = GetSarrafReductionRubForDisplay(s),
                SarrafChargedAmountUsd = s.SarrafChargedAmountUsd,
                SarrafChargedAmountRub = ToRubFromUsd(s.SarrafChargedAmountUsd, GetRubPerUsdRate(s)),
                DifferenceAmountUsd = s.DifferenceAmountUsd,
                DifferenceAmountRub = ToRubFromUsd(Math.Abs(s.DifferenceAmountUsd), GetRubPerUsdRate(s)),
                DifferenceType = s.DifferenceType,
                DifferenceReason = s.DifferenceReason,
                DifferenceTreatment = s.DifferenceTreatment,
                Status = s.Status,
                LedgerEntryId = s.LedgerEntryId
            })
            .ToList();

        // لیست نمایشیِ یکپارچهٔ پرداخت‌ها: PaymentTransaction (نقدی/بانکی) + SarrafSettlement (صراف).
        // فقط برای نمایش در تب «پرداخت‌ها»؛ هیچ مبلغی از اینجا در جمع‌های مالی دوباره حساب نمی‌شود
        // (TotalPaidUsd از مسیر مستقل خودش محاسبه شده و دست‌نخورده است).
        var paymentLines = new List<SupplierPaymentLineViewModel>();
        foreach (var p in payments)
        {
            var method = p.SarrafId.HasValue
                ? "صراف"
                : p.CashAccountType switch
                {
                    CashAccountType.Cash => "نقدی",
                    CashAccountType.Bank => "بانکی",
                    CashAccountType.Mixed => "نقدی/بانکی",
                    _ => "—"
                };
            paymentLines.Add(new SupplierPaymentLineViewModel
            {
                Date = p.PaymentDate,
                Method = method,
                Amount = p.Amount,
                Currency = p.Currency,
                AmountUsd = p.AmountUsd,
                ContractNumber = p.ContractNumber,
                Reference = string.IsNullOrWhiteSpace(p.Reference) ? $"#{p.Id}" : p.Reference!,
                Description = p.Description,
                IsSarraf = false,
                PaymentId = p.Id
            });
        }
        foreach (var s in sarrafSettlements.Where(s => s.Status == SarrafSettlementStatus.Posted))
        {
            var settlementRub = GetSettlementRubForDisplay(s);
            paymentLines.Add(new SupplierPaymentLineViewModel
            {
                Date = s.SettlementDate,
                Method = "صراف",
                Amount = settlementRub ?? s.SupplierAcceptedAmount,
                Currency = settlementRub.HasValue ? "RUB" : s.SupplierAcceptedCurrency,
                AmountUsd = SupplierReductionAmountUsd(s),
                ContractNumber = s.ContractNumber,
                Reference = string.IsNullOrWhiteSpace(s.ReferenceNumber) ? $"#{s.Id}" : s.ReferenceNumber!,
                Description = s.Description,
                IsSarraf = true,
                SarrafSettlementId = s.Id
            });
        }
        foreach (var ledger in viaSarrafSupplierLedgers)
        {
            paymentLines.Add(new SupplierPaymentLineViewModel
            {
                Date = ledger.EntryDate,
                Method = "صراف",
                Amount = ledger.SourceAmount ?? ledger.AmountUsd,
                Currency = ledger.SourceCurrencyCode ?? ledger.Currency,
                AmountUsd = ledger.AmountUsd,
                ContractNumber = ledger.ContractNumber,
                Reference = string.IsNullOrWhiteSpace(ledger.Reference) ? $"#{ledger.Id}" : ledger.Reference!,
                Description = ledger.Description,
                IsSarraf = true,
                IsLedgerOnlyViaSarraf = true,
                LedgerEntryId = ledger.Id
            });
        }
        foreach (var transfer in balanceTransfers)
        {
            // پول تازه‌ای جابه‌جا نشده؛ همان پیش‌پرداختِ آزادِ قبلی حالا به این قرارداد نشسته است.
            paymentLines.Add(new SupplierPaymentLineViewModel
            {
                Date = transfer.TransferDate,
                Method = "انتقال مانده",
                Amount = transfer.TransferOriginalAmount,
                Currency = transfer.OriginalCurrencyCode,
                AmountUsd = transfer.TransferValueUsd,
                ContractNumber = transfer.ContractNumber,
                Reference = string.IsNullOrWhiteSpace(transfer.ReferenceNumber)
                    ? $"#{transfer.Id}"
                    : transfer.ReferenceNumber!,
                Description = "تخصیص پیش‌پرداخت آزاد به قرارداد",
                IsBalanceTransfer = true,
                BalanceTransferId = transfer.Id
            });
        }
        var paymentLineRows = paymentLines
            .OrderByDescending(l => l.Date)
            .ThenByDescending(l => l.LedgerEntryId
                ?? (l.IsSarraf ? l.SarrafSettlementId ?? 0 : l.PaymentId ?? 0))
            .ToList();

        var selectedContract = contractId.HasValue
            ? contractSummaries.First(c => c.ContractId == contractId.Value)
            : null;
        var statementLedgers = contractId.HasValue
            ? supplierAccountLedgers.Where(l => l.ContractId == contractId.Value).ToList()
            : supplierAccountLedgers;

        var ledgerDebitUsd = supplierAccountLedgers.Where(l => l.Side == LedgerSide.Debit).Sum(l => l.AmountUsd);
        var ledgerCreditUsd = supplierAccountLedgers.Where(l => l.Side == LedgerSide.Credit).Sum(l => l.AmountUsd);
        var directSupplierPaidUsd = paymentSummaries
            .Where(p => p.PaymentKind == PaymentKind.SupplierPayment)
            .Sum(p => p.AmountUsd);
        var directSupplierPaidRub = SumKnown(paymentSummaries
            .Where(p => p.PaymentKind == PaymentKind.SupplierPayment)
            .Select(p => p.AmountRubEquivalent));
        var sarrafReductionUsd = sarrafSettlements
            .Where(s => s.Status == SarrafSettlementStatus.Posted)
            .Sum(SupplierReductionAmountUsd);
        var viaSarrafReductionUsd = viaSarrafSupplierLedgers.Sum(l => l.AmountUsd);
        var viaSarrafReductionRub = SumKnown(viaSarrafSupplierLedgers.Select(GetLedgerRubEquivalent));
        var sarrafPaidActualUsd = sarrafSettlements
            .Where(s => s.Status == SarrafSettlementStatus.Posted)
            .Sum(s => s.SarrafChargedAmountUsd);
        var sarrafPaidActualRub = SumKnown(sarrafSettlements
            .Where(s => s.Status == SarrafSettlementStatus.Posted)
            .Select(GetSarrafActualPaidRub));
        var sarrafSupplierShortfallUsd = sarrafSettlements
            .Where(s => s.Status == SarrafSettlementStatus.Posted
                && s.DifferenceType == SarrafSettlementDifferenceType.SupplierShortfall)
            .Sum(s => Math.Abs(s.DifferenceAmountUsd));

        // D4 — تفاوت نرخ ارز شناسایی‌شده برای نمایش در صورت‌حساب (read-only).
        // فقط تسویه‌های صراف Posted که با روش «شناسایی سود/زیان نرخ ارز» ثبت شده‌اند.
        // وقتی صورت‌حساب روی یک قرارداد فیلتر شده، تفاوت نرخ هم به همان قرارداد محدود می‌شود.
        var fxRecognizedSettlements = sarrafSettlements
            .Where(s => s.Status == SarrafSettlementStatus.Posted
                && s.DifferenceTreatment == SarrafSettlementDifferenceTreatment.RecognizeExchangeGainLoss
                && (s.DifferenceType == SarrafSettlementDifferenceType.Gain
                    || s.DifferenceType == SarrafSettlementDifferenceType.Loss)
                && (!contractId.HasValue || s.ContractId == contractId.Value))
            .ToList();
        var recognizedFxGainUsd = fxRecognizedSettlements
            .Where(s => s.DifferenceType == SarrafSettlementDifferenceType.Gain)
            .Sum(s => Math.Abs(s.DifferenceAmountUsd));
        var recognizedFxLossUsd = fxRecognizedSettlements
            .Where(s => s.DifferenceType == SarrafSettlementDifferenceType.Loss)
            .Sum(s => Math.Abs(s.DifferenceAmountUsd));

        // پیش‌پرداخت: پرداخت‌های تأمین‌کننده‌ای که «پیش‌پرداخت» علامت خورده‌اند یا تخصیص دارند.
        var activeAllocatedByPayment = advanceAllocations
            .Where(a => a.Status == SupplierPaymentAllocationStatus.Active)
            .GroupBy(a => a.PaymentTransactionId)
            .ToDictionary(g => g.Key, g => g.Sum(a => a.AllocatedBookAmountUsd));
        var advancePayments = payments
            .Where(p => p.PaymentKind == PaymentKind.SupplierPayment
                && (p.IsAdvancePayment == true || activeAllocatedByPayment.ContainsKey(p.Id)))
            .ToList();
        var advanceTotalUsd = advancePayments.Sum(p => p.AmountUsd);
        var advanceAllocatedUsd = advanceAllocations
            .Where(a => a.Status == SupplierPaymentAllocationStatus.Active)
            .Sum(a => a.AllocatedBookAmountUsd);
        var advanceByCurrency = advancePayments
            .GroupBy(p => SystemCurrency.Normalize(p.Currency))
            .Select(g => new SupplierAdvanceCurrencyRowViewModel
            {
                Currency = g.Key,
                AdvanceTotalUsd = g.Sum(p => p.AmountUsd),
                AllocatedUsd = g.Sum(p => activeAllocatedByPayment.TryGetValue(p.Id, out var v) ? v : 0m)
            })
            .OrderByDescending(r => r.AdvanceTotalUsd)
            .ToList();
        var paymentReferenceById = payments.ToDictionary(p => p.Id, p => p.Reference);
        var advanceAllocationRows = advanceAllocations
            .Select(a => new SupplierAdvanceAllocationRowViewModel
            {
                Id = a.Id,
                AllocationDate = a.AllocationDate,
                PaymentTransactionId = a.PaymentTransactionId,
                PaymentReference = paymentReferenceById.TryGetValue(a.PaymentTransactionId, out var reference) && !string.IsNullOrWhiteSpace(reference)
                    ? reference!
                    : $"#{a.PaymentTransactionId}",
                ContractId = a.ContractId,
                ContractNumber = a.ContractNumber ?? string.Empty,
                AllocatedBookAmountUsd = a.AllocatedBookAmountUsd,
                ContractCurrencyCode = a.ContractCurrencyCode,
                AllocatedContractCurrencyAmount = a.AllocatedContractCurrencyAmount,
                IsActive = a.Status == SupplierPaymentAllocationStatus.Active
            })
            .ToList();

        // RUB payment totals must come only from actual RUB source amounts.
        // Do not fabricate RUB by dividing loaded RUB by loaded USD; that creates
        // misleading supplier balances when the payment itself was posted in USD.
        var sarrafPaidRubFromLedgers = SumKnown(sarrafLedgerRubRowsForTotals.Select(l => l.AmountRub));
        var sarrafPaidRubFromLegacySettlements = SumKnown(sarrafSettlements
            .Where(s => s.Status == SarrafSettlementStatus.Posted)
            .Where(s => !sarrafLedgerExactRubSourceIds.Contains(s.Id))
            .Select(GetSettlementRubForDisplay));

        // «هیچ پرداختی ثبت نشده» با «پرداخت هست ولی منبع روبلی ندارد» یکی نیست. وقتی اصلاً
        // پرداختی وجود ندارد، پرداخت روبلی واقعاً صفر است و مانده روبلی باید محاسبه شود؛
        // در غیر این صورت null می‌ماند تا با فرضِ «صفر روبل پرداخت شده» مانده جعلی نسازیم.
        var hasAnyPaymentSource = paymentSummaries.Any(p => p.PaymentKind == PaymentKind.SupplierPayment)
            || sarrafSettlements.Any(s => s.Status == SarrafSettlementStatus.Posted)
            || viaSarrafSupplierLedgers.Count > 0
            || sarrafLedgerRubRowsForTotals.Count > 0;

        var totalPaidRub = SumKnown(
            paymentSummaries
                .Where(p => p.PaymentKind == PaymentKind.SupplierPayment)
                .Select(p => p.AmountRubEquivalent)
                .Concat([sarrafPaidRubFromLedgers, sarrafPaidRubFromLegacySettlements, viaSarrafReductionRub]))
            ?? (hasAnyPaymentSource ? null : 0m);

        var paymentDates = paymentSummaries.Select(p => p.PaymentDate)
            .Concat(sarrafSettlements
                .Where(s => s.Status == SarrafSettlementStatus.Posted)
                .Select(s => s.SettlementDate))
            .Concat(viaSarrafSupplierLedgers.Select(l => l.EntryDate))
            .ToList();

        return new SupplierProfileViewModel
        {
            DisplayCurrency = displayCurrency,
            HasAdvances = advancePayments.Count > 0 || advanceAllocations.Count > 0,
            AdvanceTotalUsd = advanceTotalUsd,
            AdvanceAllocatedUsd = advanceAllocatedUsd,
            AdvanceFreeUsd = advanceTotalUsd - advanceAllocatedUsd,
            AdvanceByCurrency = advanceByCurrency,
            AdvanceAllocations = advanceAllocationRows,
            SupplierId = supplier.Id,
            Code = supplier.Code,
            Name = supplier.Name,
            NamePersian = supplier.NamePersian,
            Country = supplier.Country,
            ContactPerson = supplier.ContactPerson,
            Phone = supplier.Phone,
            Address = supplier.Address,
            Notes = supplier.Notes,
            IsActive = supplier.IsActive,
            CreatedAtUtc = supplier.CreatedAtUtc,
            UpdatedAtUtc = supplier.UpdatedAtUtc,
            PurchaseContractsCount = contractSummaries.Count,
            ActivePurchaseContractsCount = contractSummaries.Count(c => c.Status == ContractStatus.Active),
            TotalPurchaseQuantityMt = contractSummaries.Sum(c => c.QuantityMt),
            EstimatedContractValueUsd = contractSummaries.Sum(c => c.EstimatedTotalUsd ?? 0m),
            EstimatedContractValueRub = SumKnown(contractSummaries.Select(c => c.EstimatedTotalRub)),
            LoadedPurchaseQuantityMt = contractSummaries.Sum(c => c.LoadedQuantityMt),
            LoadedPurchaseValueUsd = contractSummaries.Sum(c => c.LoadedValueUsd),
            LoadedPurchaseValueRub = SumKnown(contractSummaries.Select(c => c.LoadedValueRub)),
            LoadedPurchaseValueRubIsEstimated = contractSummaries.Any(c => c.LoadedValueRubIsEstimated),
            RemainingPurchaseQuantityMt = contractSummaries.Sum(c => c.RemainingQuantityMt),
            EstimatedRemainingContractValueUsd = contractSummaries.Sum(c => c.EstimatedUnloadedValueUsd ?? 0m),
            EstimatedRemainingContractValueRub = SumKnown(contractSummaries.Select(c => c.EstimatedUnloadedValueRub)),
            LedgerOutflowUsd = ledgerDebitUsd,
            LedgerReceiptUsd = ledgerCreditUsd,
            TotalPaidUsd = directSupplierPaidUsd + sarrafReductionUsd + viaSarrafReductionUsd,
            TotalPaidRub = totalPaidRub,
            TotalPaidActualUsd = directSupplierPaidUsd + sarrafPaidActualUsd + viaSarrafReductionUsd,
            TotalPaidActualRub = SumKnown([directSupplierPaidRub, sarrafPaidActualRub, viaSarrafReductionRub])
                ?? (hasAnyPaymentSource ? null : 0m),
            LastPaymentDate = paymentDates.Count == 0 ? null : paymentDates.Max(),
            SarrafAcceptedUsd = sarrafReductionUsd,
            SarrafSupplierShortfallUsd = sarrafSupplierShortfallUsd,
            HasRecognizedFx = fxRecognizedSettlements.Count > 0,
            RecognizedFxGainUsd = recognizedFxGainUsd,
            RecognizedFxLossUsd = recognizedFxLossUsd,
            SelectedContractId = contractId,
            ActiveTab = ResolveActiveTab(tab, contractId),
            SelectedContract = selectedContract,
            StatementContractOptions = contractSummaries
                .OrderBy(c => c.ContractNumber)
                .Select(c => new SupplierStatementContractFilterOptionViewModel
                {
                    ContractId = c.ContractId,
                    ContractNumber = c.ContractNumber
                })
                .ToList(),
            Contracts = contractSummaries,
            Payments = paymentSummaries,
            SarrafSettlements = sarrafSettlementSummaries,
            StatementRows = BuildSupplierStatementRows(
                statementLedgers,
                sarrafSettlements
                    .Where(s => s.Status == SarrafSettlementStatus.Posted)
                    .ToDictionary(s => s.Id, GetSettlementRubForDisplay)),
            PaymentLines = paymentLineRows,
            UnallocatedPaymentTotalUsd = paymentLineRows
                .Where(p => string.IsNullOrWhiteSpace(p.ContractNumber))
                .Sum(p => p.AmountUsd)
        };
    }

    internal static decimal SupplierReductionAmountUsd(SarrafSettlement settlement)
        => settlement.DifferenceTreatment == SarrafSettlementDifferenceTreatment.RecognizeExchangeGainLoss
            ? settlement.RequestedAmountUsd
            : settlement.SupplierAcceptedAmountUsd;

    // نرخ روبل قرارداد از همان منبع مشترکِ صفحهٔ جریان قرارداد خوانده می‌شود.
    private static decimal? GetRubPerUsdRate(Contract? contract)
        => contract is null
            ? null
            : LoadingRubValuation.ContractRubPerUsdRate(
                contract.Currency,
                contract.ContractRubPerUsdRate,
                contract.AppliedFxRateToUsd);

    private static decimal? GetRubPerUsdRateFromFx(string? currency, decimal? fxRateToUsd)
        => IsRubCurrency(currency) && fxRateToUsd.HasValue && fxRateToUsd.Value > 0m
            ? decimal.Round(1m / fxRateToUsd.Value, 6, MidpointRounding.AwayFromZero)
            : null;

    private static decimal? GetContractTotalRub(Contract contract, decimal? finalPriceUsd, decimal? rubPerUsdRate)
    {
        if (IsRubCurrency(contract.Currency) && contract.UnitPriceInCurrency.HasValue)
        {
            return decimal.Round(contract.QuantityMt * contract.UnitPriceInCurrency.Value, 4, MidpointRounding.AwayFromZero);
        }

        return finalPriceUsd.HasValue
            ? ToRubFromUsd(decimal.Round(contract.QuantityMt * finalPriceUsd.Value, 4, MidpointRounding.AwayFromZero), rubPerUsdRate)
            : null;
    }

    private static decimal? GetPaymentRubEquivalent(PaymentTransaction payment)
        => IsRubCurrency(payment.Currency)
            ? decimal.Round(payment.Amount, 4, MidpointRounding.AwayFromZero)
            : null;

    private static decimal? GetSarrafSupplierReductionRub(SarrafSettlement settlement)
    {
        var amountUsd = SupplierReductionAmountUsd(settlement);
        if (settlement.DifferenceTreatment == SarrafSettlementDifferenceTreatment.RecognizeExchangeGainLoss)
        {
            return IsRubCurrency(settlement.RequestedCurrency)
                ? GetSarrafAmountRub(
                    settlement.RequestedAmount,
                    settlement.RequestedCurrency,
                    amountUsd,
                    settlement.Contract)
                : null;
        }

        return IsRubCurrency(settlement.SupplierAcceptedCurrency)
            ? GetSarrafAmountRub(
                settlement.SupplierAcceptedAmount,
                settlement.SupplierAcceptedCurrency,
                amountUsd,
                settlement.Contract)
            : null;
    }

    private static decimal? GetSarrafLedgerRubPaymentImpact(LedgerEntry ledger)
    {
        var amount = GetLedgerRubEquivalent(ledger);
        if (!amount.HasValue)
        {
            return null;
        }

        return ledger.Side == LedgerSide.Debit ? amount.Value : -amount.Value;
    }

    private static decimal? GetLedgerRubEquivalent(LedgerEntry ledger)
    {
        var currency = ledger.SourceCurrencyCode ?? ledger.Currency;
        if (IsRubCurrency(currency) && ledger.SourceAmount.HasValue)
        {
            return decimal.Round(ledger.SourceAmount.Value, 4, MidpointRounding.AwayFromZero);
        }

        var rubPerUsdRate = GetRubPerUsdRate(ledger.Contract)
            ?? GetRubPerUsdRateFromFx(currency, ledger.AppliedFxRateToUsd);
        return ToRubFromUsd(ledger.AmountUsd, rubPerUsdRate);
    }

    private static decimal? GetSarrafAmountRub(decimal sourceAmount, string sourceCurrency, decimal amountUsd, Contract? contract)
        => GetRubEquivalent(sourceAmount, sourceCurrency, amountUsd, GetRubPerUsdRate(contract));

    // SettlementCurrencyCode شرط ورود نیست: بارگیری‌ای که رقم روبلیِ فایل یا نرخ قفل‌شده دارد
    // روبلی شناخته می‌شود. ترتیب و محاسبه در LoadingRubValuation متمرکز است تا صفحهٔ قرارداد
    // و صفحهٔ تأمین‌کننده یک عدد بدهند.
    private static LoadingRubFacts ToLoadingRubFacts(SupplierLoadingRubProjection loading)
        => new(
            loading.LoadedQuantityMt,
            loading.LoadingPriceUsd,
            loading.RubRateStatus,
            loading.RubPerUsdRate,
            loading.AmountRubAtRubLock,
            loading.SettlementUnitPriceRub,
            loading.SettlementValueRub);

    private static decimal? GetRubEquivalent(decimal sourceAmount, string? sourceCurrency, decimal amountUsd, decimal? rubPerUsdRate)
    {
        if (IsRubCurrency(sourceCurrency))
        {
            return decimal.Round(sourceAmount, 4, MidpointRounding.AwayFromZero);
        }

        return ToRubFromUsd(amountUsd, rubPerUsdRate);
    }

    private static decimal? ToRubFromUsd(decimal amountUsd, decimal? rubPerUsdRate)
        => rubPerUsdRate.HasValue && rubPerUsdRate.Value > 0m
            ? decimal.Round(amountUsd * rubPerUsdRate.Value, 4, MidpointRounding.AwayFromZero)
            : null;

    private static decimal? SumKnown(IEnumerable<decimal?> values)
    {
        var total = 0m;
        var hasAny = false;
        foreach (var value in values)
        {
            if (!value.HasValue)
            {
                continue;
            }

            total += value.Value;
            hasAny = true;
        }

        return hasAny ? total : null;
    }

    private static bool IsRubCurrency(string? currency)
        => string.Equals(SystemCurrency.Normalize(currency), "RUB", StringComparison.OrdinalIgnoreCase);

    internal static bool IsSupplierAccountLedger(LedgerEntry ledger, int supplierId)
        => ledger.SupplierId == supplierId
            || ledger.SourceType == nameof(PaymentKind.SupplierPayment)
            || ledger.SourceType == nameof(PaymentKind.SupplierReceipt)
            || ledger.SourceType == SarrafSettlementService.SupplierLedgerSourceType
            || ledger.SourceType == PaymentsController.ViaSarrafSupplierLedgerSourceType
            || ledger.SourceType == SarrafSettlementService.CancelSourceType
            || ledger.SourceType == SarrafSettlementService.EditReversalSourceType
            || ledger.SourceType == ContractBalanceTransferService.LedgerSourceType;

    private static bool IsSarrafSupplierReductionLedger(LedgerEntry ledger)
        => ledger.SourceType == SarrafSettlementService.SupplierLedgerSourceType
            || ledger.SourceType == PaymentsController.ViaSarrafSupplierLedgerSourceType
            || ledger.SourceType == SarrafSettlementService.CancelSourceType
            || ledger.SourceType == SarrafSettlementService.EditReversalSourceType;

    // همان تعیین‌کنندهٔ مرکزیِ ثبت‌شده در DI؛ چون بدون حالت است، اینجا مستقیم ساخته می‌شود تا
    // امضای سازنده‌های موجود دست‌نخورده بماند. منطق دقیقاً همان یک نسخهٔ واحد است.
    private static readonly ICompanyFlowDirectionResolver SupplierFlowResolver = new CompanyFlowDirectionResolver();

    private static CompanyFlowDirection ResolveSupplierFlow(string sourceType, LedgerSide side)
        => SupplierFlowResolver.Resolve(new CompanyFlowEvent(
            sourceType,
            side,
            CompanyFlowPartyRole.Supplier,
            CompanyFlowSourceTypes.IsReversal(sourceType)
                ? CompanyFlowLifecycle.Reversal
                : CompanyFlowLifecycle.Original));

    private static IReadOnlyList<SupplierStatementRowViewModel> BuildSupplierStatementRows(IEnumerable<LedgerEntry> ledgers)
    {
        var rows = new List<SupplierStatementRowViewModel>();
        var runningBalance = 0m;
        var runningBalanceRub = 0m;
        var hasRunningRub = false;

        foreach (var ledger in ledgers
            .OrderBy(l => l.EntryDate)
            .ThenBy(l => l.Id))
        {
            // جهت از لایهٔ مرکزی می‌آید تا همین سند در صفحهٔ تأمین‌کننده، صورت‌حساب رسمی و
            // دفتر قرارداد یک معنی داشته باشد. پیش‌تر مانده جاری اینجا Credit − Debit بود،
            // یعنی علامتش وارونهٔ کارت «بیلانس» همان صفحه (که Debit − Credit بود).
            var isReceipt = ResolveSupplierFlow(ledger.SourceType, ledger.Side) == CompanyFlowDirection.Receipt;
            var receiptUsd = isReceipt ? ledger.AmountUsd : (decimal?)null;
            var outflowUsd = isReceipt ? (decimal?)null : ledger.AmountUsd;
            runningBalance += (outflowUsd ?? 0m) - (receiptUsd ?? 0m);
            var currency = ledger.SourceCurrencyCode ?? ledger.Currency;
            var sourceAmount = ledger.SourceAmount ?? ledger.AmountUsd;
            var rubPerUsdRate = GetRubPerUsdRate(ledger.Contract)
                ?? GetRubPerUsdRateFromFx(currency, ledger.AppliedFxRateToUsd);
            // معادلِ روبلیِ ردیف: اگر خودِ سند روبلی است و مبلغ مبدأ (SourceAmount) ثبت شده،
            // همان عددِ روبل ملاکِ نمایش و مانده است؛ هیچ تبدیلِ RUB→USD→RUB با نرخِ دیگری انجام نمی‌شود.
            // فقط در نبودِ SourceAmount به AmountUsd × نرخ روبل برمی‌گردیم (fallback مجاز طبق قاعده).
            var rubEquivalent = IsRubCurrency(currency) && ledger.SourceAmount.HasValue
                ? decimal.Round(ledger.SourceAmount.Value, 4, MidpointRounding.AwayFromZero)
                : ToRubFromUsd(ledger.AmountUsd, rubPerUsdRate);
            var receiptRub = isReceipt ? rubEquivalent : null;
            var outflowRub = isReceipt ? null : rubEquivalent;
            if (receiptRub.HasValue || outflowRub.HasValue)
            {
                hasRunningRub = true;
                runningBalanceRub += (outflowRub ?? 0m) - (receiptRub ?? 0m);
            }

            rows.Add(new SupplierStatementRowViewModel
            {
                LedgerEntryId = ledger.Id,
                Date = ledger.EntryDate,
                Type = GetSupplierStatementTypeName(ledger.SourceType, ledger.Side),
                Reference = ledger.Reference,
                SourceDetailsController = IsThreeWaySettlementSource(ledger.SourceType) ? "ThreeWaySettlement" : null,
                SourceDetailsAction = IsThreeWaySettlementSource(ledger.SourceType) ? "Details" : null,
                SourceDetailsRouteId = IsThreeWaySettlementSource(ledger.SourceType) ? ledger.SourceId : null,
                ContractId = ledger.ContractId,
                ContractNumber = ledger.Contract?.ContractNumber,
                Description = ledger.Description,
                Currency = currency,
                Outflow = isReceipt ? null : sourceAmount,
                Receipt = isReceipt ? sourceAmount : null,
                OutflowUsd = outflowUsd,
                ReceiptUsd = receiptUsd,
                OutflowRubEquivalent = outflowRub,
                ReceiptRubEquivalent = receiptRub,
                RunningBalanceUsd = runningBalance,
                RunningBalanceRubEquivalent = hasRunningRub ? runningBalanceRub : null,
                RubPerUsdRate = rubPerUsdRate,
                FxRateUsed = ledger.AppliedFxRateToUsd
            });
        }

        return rows;
    }

    private static string GetSupplierStatementTypeName(string sourceType, LedgerSide side)
        => (sourceType, side) switch
        {
            (nameof(PaymentKind.SupplierPayment), _) => "پرداخت به تأمین‌کننده",
            (nameof(PaymentKind.SupplierReceipt), _) => "دریافت از تأمین‌کننده",
            ("Expense", _) => "هزینه",
            (var s, _) when s == ThreeWaySettlementController.LedgerSourceType => "تسویه سه‌طرفه / حواله",
            (var s, _) when s == ThreeWaySettlementController.CancellationLedgerSourceType => "برگشت تسویه سه‌طرفه",
            (var s, LedgerSide.Debit) when s == ContractBalanceTransferService.LedgerSourceType => "انتقال مانده به قرارداد دیگر",
            (var s, _) when s == ContractBalanceTransferService.LedgerSourceType => "انتقال مانده از قرارداد دیگر",
            (var s, _) when s == SupplierPaymentAllocationService.LedgerSourceType => "مصرف پیش‌پرداخت برای قرارداد",
            (var s, _) when s == SupplierPaymentAllocationService.ReversalLedgerSourceType => "برگشت مصرف پیش‌پرداخت",
            (var s, LedgerSide.Debit) when s == SupplierPaymentAllocationService.ExchangeDifferenceLedgerSourceType => "زیان تسعیر تخصیص پیش‌پرداخت",
            (var s, _) when s == SupplierPaymentAllocationService.ExchangeDifferenceLedgerSourceType => "سود تسعیر تخصیص پیش‌پرداخت",
            (var s, _) when s == SupplierPaymentAllocationService.ExchangeDifferenceReversalLedgerSourceType => "برگشت تسعیر تخصیص پیش‌پرداخت",
            (var s, _) when s == SarrafSettlementService.SupplierLedgerSourceType => "پرداخت از طریق صراف",
            (var s, _) when s == PaymentsController.ViaSarrafSupplierLedgerSourceType => "پرداخت از طریق صراف",
            (var s, _) when s == SarrafSettlementService.ExchangeDifferenceSourceType => "تفاوت نرخ صراف",
            (var s, _) when s == SarrafSettlementService.CancelSourceType => "برگشت پرداخت صراف",
            ("Adjustment", _) => "اصلاح حساب",
            ("Purchase", _) => "خرید / بدهکار شدن بابت بار",
            ("Loading", _) => "بارگیری",
            ("Receipt", _) => "رسید بار",
            _ => "سند مالی"
        };

    private static bool IsThreeWaySettlementSource(string sourceType)
        => string.Equals(sourceType, ThreeWaySettlementController.LedgerSourceType, StringComparison.Ordinal)
            || string.Equals(sourceType, ThreeWaySettlementController.CancellationLedgerSourceType, StringComparison.Ordinal);

    private static IEnumerable<(int SupplierId, SupplierPaymentMetricProjection Payment)> ExpandSupplierPaymentLinks(
        IEnumerable<SupplierPaymentMetricProjection> payments)
    {
        foreach (var payment in payments)
        {
            if (payment.DirectSupplierId.HasValue)
            {
                yield return (payment.DirectSupplierId.Value, payment);
            }

            if (payment.ContractSupplierId.HasValue && payment.ContractSupplierId != payment.DirectSupplierId)
            {
                yield return (payment.ContractSupplierId.Value, payment);
            }
        }
    }

    private static string ResolveActiveTab(string? tab, int? contractId)
    {
        var normalized = string.IsNullOrWhiteSpace(tab)
            ? (contractId.HasValue ? "statement" : "overview")
            : tab.Trim().ToLowerInvariant();

        return normalized is "overview" or "contracts" or "payments" or "sarraf" or "statement" or "documents"
            ? normalized
            : "overview";
    }

    private static string GetContractStatusName(ContractStatus status)
        => status switch
        {
            ContractStatus.Draft => "پیش‌نویس",
            ContractStatus.Active => "فعال",
            ContractStatus.Closed => "بسته",
            ContractStatus.Cancelled => "لغو‌شده",
            _ => status.ToString()
        };

    private static string GetContractStatusBadgeClass(ContractStatus status)
        => status switch
        {
            ContractStatus.Active => "status-badge-success",
            ContractStatus.Draft => "status-badge-warning",
            ContractStatus.Closed => "status-badge-info",
            ContractStatus.Cancelled => "status-badge-danger",
            _ => "status-badge-neutral"
        };

    private static void Normalize(Supplier model)
    {
        model.Code = string.IsNullOrWhiteSpace(model.Code) ? null : model.Code.Trim().ToUpperInvariant();
        model.Name = (model.Name ?? string.Empty).Trim();
        model.NamePersian = string.IsNullOrWhiteSpace(model.NamePersian) ? null : model.NamePersian.Trim();
        model.Country = string.IsNullOrWhiteSpace(model.Country) ? null : model.Country.Trim();
        model.ContactPerson = string.IsNullOrWhiteSpace(model.ContactPerson) ? null : model.ContactPerson.Trim();
        model.Phone = string.IsNullOrWhiteSpace(model.Phone) ? null : model.Phone.Trim();
        model.Address = string.IsNullOrWhiteSpace(model.Address) ? null : model.Address.Trim();
        model.Notes = string.IsNullOrWhiteSpace(model.Notes) ? null : model.Notes.Trim();
    }

    private sealed record LedgerTotals(decimal OutflowUsd, decimal ReceiptUsd);

    private sealed class SupplierPaymentMetricProjection
    {
        public int PaymentId { get; init; }
        public int? DirectSupplierId { get; init; }
        public int? ContractSupplierId { get; init; }
        public DateTime PaymentDate { get; init; }
        public PaymentKind PaymentKind { get; init; }
        public decimal AmountUsd { get; init; }
    }

    // ---- Supplier Details projection helpers ---------------------------------
    // These mirror the entity-based helpers exactly but operate on the slim
    // read-models above. Entity-based helpers stay untouched (some are shared with
    // PaymentsController / ContractJourneyController). Values are bit-identical.

    // Mirror of GetRubPerUsdRate(Contract?) using the contract scalar parts carried
    // on each projection (null parts == null contract == same null result).
    private static decimal? GetRubPerUsdRateFromContractParts(
        decimal? contractRubPerUsdRate, string? contractCurrency, decimal? contractAppliedFxRateToUsd)
    {
        if (contractRubPerUsdRate.HasValue && contractRubPerUsdRate.Value > 0m)
        {
            return contractRubPerUsdRate.Value;
        }

        if (IsRubCurrency(contractCurrency) && contractAppliedFxRateToUsd.HasValue && contractAppliedFxRateToUsd.Value > 0m)
        {
            return decimal.Round(1m / contractAppliedFxRateToUsd.Value, 6, MidpointRounding.AwayFromZero);
        }

        return null;
    }

    private static decimal? GetRubPerUsdRate(SupplierPaymentProjection payment)
        => GetRubPerUsdRateFromContractParts(
            payment.ContractRubPerUsdRate, payment.ContractCurrency, payment.ContractAppliedFxRateToUsd);

    private static decimal? GetRubPerUsdRate(SupplierSarrafSettlementProjection settlement)
        => GetRubPerUsdRateFromContractParts(
            settlement.ContractRubPerUsdRate, settlement.ContractCurrency, settlement.ContractAppliedFxRateToUsd);

    private static decimal? GetPaymentRubEquivalent(SupplierPaymentProjection payment)
        => IsRubCurrency(payment.Currency)
            ? decimal.Round(payment.Amount, 4, MidpointRounding.AwayFromZero)
            : null;

    private static decimal? GetSarrafAmountRub(
        decimal sourceAmount, string sourceCurrency, decimal amountUsd, SupplierSarrafSettlementProjection settlement)
        => GetRubEquivalent(sourceAmount, sourceCurrency, amountUsd, GetRubPerUsdRate(settlement));

    private static decimal? GetSarrafActualPaidRub(SupplierSarrafSettlementProjection settlement)
        => IsRubCurrency(settlement.SarrafCurrency)
            ? decimal.Round(settlement.SarrafChargedAmount, 4, MidpointRounding.AwayFromZero)
            : null;

    private static decimal SupplierReductionAmountUsd(SupplierSarrafSettlementProjection settlement)
        => settlement.DifferenceTreatment == SarrafSettlementDifferenceTreatment.RecognizeExchangeGainLoss
            ? settlement.RequestedAmountUsd
            : settlement.SupplierAcceptedAmountUsd;

    // معادل روبلیِ کاهش بدهیِ تأمین‌کننده از یک تسویهٔ صراف.
    // اگر یکی از طرف‌های تسویه روبلی باشد، همان روبلِ واقعی (بدون تبدیل USD→RUB) ملاک است؛
    // وگرنه به USD × نرخ روبلِ قرارداد برمی‌گردیم. این کار تضمین می‌کند پرداختِ روبلی همیشه از
    // معادل روبلیِ طلب کم شود، حتی وقتی سند دفترکلِ تأمین‌کننده دالری ثبت شده باشد.
    private static decimal? GetSarrafSupplierReductionRub(SupplierSarrafSettlementProjection settlement)
    {
        var amountUsd = SupplierReductionAmountUsd(settlement);
        if (IsRubCurrency(settlement.RequestedCurrency))
        {
            return GetSarrafAmountRub(settlement.RequestedAmount, settlement.RequestedCurrency, amountUsd, settlement);
        }

        return IsRubCurrency(settlement.SupplierAcceptedCurrency)
            ? GetSarrafAmountRub(
                settlement.SupplierAcceptedAmount, settlement.SupplierAcceptedCurrency, amountUsd, settlement)
            : null;
    }

    private static bool HasExactRubSource(SupplierLedgerProjection ledger)
        => IsRubCurrency(ledger.SourceCurrencyCode ?? ledger.Currency) && ledger.SourceAmount.HasValue;

    private static decimal? GetSarrafLedgerRubPaymentImpact(SupplierLedgerProjection ledger)
    {
        var amount = GetLedgerRubEquivalent(ledger);
        if (!amount.HasValue)
        {
            return null;
        }

        return ledger.Side == LedgerSide.Debit ? amount.Value : -amount.Value;
    }

    private static decimal? GetLedgerRubEquivalent(SupplierLedgerProjection ledger)
    {
        var currency = ledger.SourceCurrencyCode ?? ledger.Currency;
        if (IsRubCurrency(currency) && ledger.SourceAmount.HasValue)
        {
            return decimal.Round(ledger.SourceAmount.Value, 4, MidpointRounding.AwayFromZero);
        }

        var rubPerUsdRate = GetRubPerUsdRateFromContractParts(
                ledger.ContractRubPerUsdRate,
                ledger.ContractCurrency,
                ledger.ContractAppliedFxRateToUsd)
            ?? GetRubPerUsdRateFromFx(currency, ledger.AppliedFxRateToUsd);
        return ToRubFromUsd(ledger.AmountUsd, rubPerUsdRate);
    }

    private static bool IsSupplierAccountLedger(SupplierLedgerProjection ledger, int supplierId)
        => ledger.SupplierId == supplierId
            || ledger.SourceType == nameof(PaymentKind.SupplierPayment)
            || ledger.SourceType == nameof(PaymentKind.SupplierReceipt)
            || ledger.SourceType == SarrafSettlementService.SupplierLedgerSourceType
            || ledger.SourceType == SarrafSettlementService.CancelSourceType
            || ledger.SourceType == SarrafSettlementService.EditReversalSourceType
            || ledger.SourceType == ContractBalanceTransferService.LedgerSourceType;

    private static bool IsSarrafSupplierReductionLedger(SupplierLedgerProjection ledger)
        => ledger.SourceType == SarrafSettlementService.SupplierLedgerSourceType
            || ledger.SourceType == SarrafSettlementService.CancelSourceType
            || ledger.SourceType == SarrafSettlementService.EditReversalSourceType;

    private static IReadOnlyList<SupplierStatementRowViewModel> BuildSupplierStatementRows(
        IEnumerable<SupplierLedgerProjection> ledgers,
        IReadOnlyDictionary<int, decimal?>? sarrafRubFallbackBySourceId = null)
    {
        var rows = new List<SupplierStatementRowViewModel>();
        var runningBalance = 0m;
        var runningBalanceRub = 0m;
        var hasRunningRub = false;

        foreach (var ledger in ledgers
            .OrderBy(l => l.EntryDate)
            .ThenBy(l => l.Id))
        {
            // جهت از لایهٔ مرکزی می‌آید تا همین سند در صفحهٔ تأمین‌کننده، صورت‌حساب رسمی و
            // دفتر قرارداد یک معنی داشته باشد. پیش‌تر مانده جاری اینجا Credit − Debit بود،
            // یعنی علامتش وارونهٔ کارت «بیلانس» همان صفحه (که Debit − Credit بود).
            var isReceipt = ResolveSupplierFlow(ledger.SourceType, ledger.Side) == CompanyFlowDirection.Receipt;
            var receiptUsd = isReceipt ? ledger.AmountUsd : (decimal?)null;
            var outflowUsd = isReceipt ? (decimal?)null : ledger.AmountUsd;
            runningBalance += (outflowUsd ?? 0m) - (receiptUsd ?? 0m);
            var currency = ledger.SourceCurrencyCode ?? ledger.Currency;
            var sourceAmount = ledger.SourceAmount ?? ledger.AmountUsd;
            var rubPerUsdRate = GetRubPerUsdRateFromContractParts(
                    ledger.ContractRubPerUsdRate, ledger.ContractCurrency, ledger.ContractAppliedFxRateToUsd)
                ?? GetRubPerUsdRateFromFx(currency, ledger.AppliedFxRateToUsd);
            var rubEquivalent = IsRubCurrency(currency) && ledger.SourceAmount.HasValue
                ? decimal.Round(ledger.SourceAmount.Value, 4, MidpointRounding.AwayFromZero)
                : ToRubFromUsd(ledger.AmountUsd, rubPerUsdRate);
            if (!rubEquivalent.HasValue
                && ledger.SourceId > 0
                && IsSarrafSupplierReductionLedger(ledger)
                && sarrafRubFallbackBySourceId is not null
                && sarrafRubFallbackBySourceId.TryGetValue(ledger.SourceId, out var sarrafRubFallback)
                && sarrafRubFallback.HasValue)
            {
                rubEquivalent = sarrafRubFallback.Value;
            }
            var receiptRub = isReceipt ? rubEquivalent : null;
            var outflowRub = isReceipt ? null : rubEquivalent;
            if (receiptRub.HasValue || outflowRub.HasValue)
            {
                hasRunningRub = true;
                runningBalanceRub += (outflowRub ?? 0m) - (receiptRub ?? 0m);
            }

            rows.Add(new SupplierStatementRowViewModel
            {
                LedgerEntryId = ledger.Id,
                Date = ledger.EntryDate,
                Type = GetSupplierStatementTypeName(ledger.SourceType, ledger.Side),
                Reference = ledger.Reference,
                SourceDetailsController = IsThreeWaySettlementSource(ledger.SourceType) ? "ThreeWaySettlement" : null,
                SourceDetailsAction = IsThreeWaySettlementSource(ledger.SourceType) ? "Details" : null,
                SourceDetailsRouteId = IsThreeWaySettlementSource(ledger.SourceType) ? ledger.SourceId : null,
                ContractId = ledger.ContractId,
                ContractNumber = ledger.ContractNumber,
                Description = ledger.Description,
                Currency = currency,
                Outflow = isReceipt ? null : sourceAmount,
                Receipt = isReceipt ? sourceAmount : null,
                OutflowUsd = outflowUsd,
                ReceiptUsd = receiptUsd,
                OutflowRubEquivalent = outflowRub,
                ReceiptRubEquivalent = receiptRub,
                RunningBalanceUsd = runningBalance,
                RunningBalanceRubEquivalent = hasRunningRub ? runningBalanceRub : null,
                RubPerUsdRate = rubPerUsdRate,
                FxRateUsed = ledger.AppliedFxRateToUsd
            });
        }

        return rows;
    }

    // Slim read-model for supplier ledger rows on the Details page.
    private sealed class SupplierLedgerProjection
    {
        public int Id { get; init; }
        public DateTime EntryDate { get; init; }
        public LedgerSide Side { get; init; }
        public decimal AmountUsd { get; init; }
        public string SourceType { get; init; } = "";
        public int SourceId { get; init; }
        public int? SupplierId { get; init; }
        public int? ContractId { get; init; }
        public string Currency { get; init; } = "USD";
        public string? SourceCurrencyCode { get; init; }
        public decimal? SourceAmount { get; init; }
        public decimal? AppliedFxRateToUsd { get; init; }
        public string? Reference { get; init; }
        public string Description { get; init; } = "";
        public string? ContractNumber { get; init; }
        public decimal? ContractRubPerUsdRate { get; init; }
        public string? ContractCurrency { get; init; }
        public decimal? ContractAppliedFxRateToUsd { get; init; }
    }

    // Slim read-model for supplier payment rows on the Details page.
    private sealed class SupplierPaymentProjection
    {
        public int Id { get; init; }
        public DateTime PaymentDate { get; init; }
        public PaymentDirection Direction { get; init; }
        public PaymentKind PaymentKind { get; init; }
        public int? SarrafId { get; init; }
        public int? ContractId { get; init; }
        public decimal Amount { get; init; }
        public string Currency { get; init; } = "USD";
        public decimal AmountUsd { get; init; }
        public decimal? AppliedFxRateToUsd { get; init; }
        public string? Reference { get; init; }
        public string? Description { get; init; }
        public bool? IsAdvancePayment { get; init; }
        public int? LedgerEntryId { get; init; }
        public DateTime CreatedAtUtc { get; init; }
        public int? CreatedByUserId { get; init; }
        public string? CashAccountName { get; init; }
        public CashAccountType? CashAccountType { get; init; }
        public string? ContractNumber { get; init; }
        public decimal? ContractRubPerUsdRate { get; init; }
        public string? ContractCurrency { get; init; }
        public decimal? ContractAppliedFxRateToUsd { get; init; }
    }

    // Slim read-model for supplier sarraf-settlement rows on the Details page.
    private sealed class SupplierSarrafSettlementProjection
    {
        public int Id { get; init; }
        public DateTime SettlementDate { get; init; }
        public string? SarrafName { get; init; }
        public int? ContractId { get; init; }
        public string? ContractNumber { get; init; }
        public string? ReferenceNumber { get; init; }
        public string? Description { get; init; }
        public decimal RequestedAmount { get; init; }
        public string RequestedCurrency { get; init; } = "USD";
        public decimal RequestedAmountUsd { get; init; }
        public decimal SupplierAcceptedAmount { get; init; }
        public string SupplierAcceptedCurrency { get; init; } = "USD";
        public decimal SupplierAcceptedAmountUsd { get; init; }
        public decimal SarrafChargedAmount { get; init; }
        public string SarrafCurrency { get; init; } = "USD";
        public decimal SarrafChargedAmountUsd { get; init; }
        public decimal DifferenceAmountUsd { get; init; }
        public SarrafSettlementDifferenceType DifferenceType { get; init; }
        public DifferenceReason? DifferenceReason { get; init; }
        public SarrafSettlementDifferenceTreatment DifferenceTreatment { get; init; }
        public SarrafSettlementStatus Status { get; init; }
        public int? LedgerEntryId { get; init; }
        public decimal? ContractRubPerUsdRate { get; init; }
        public string? ContractCurrency { get; init; }
        public decimal? ContractAppliedFxRateToUsd { get; init; }
    }

    // Slim read-model for supplier advance allocations — avoids loading the full
    // SupplierPaymentAllocation entity graph (incl. Contract) on the Details page.
    private sealed class SupplierAdvanceAllocationProjection
    {
        public int Id { get; init; }
        public DateTime AllocationDate { get; init; }
        public int PaymentTransactionId { get; init; }
        public SupplierPaymentAllocationStatus Status { get; init; }
        public decimal AllocatedBookAmountUsd { get; init; }
        public int ContractId { get; init; }
        public string? ContractNumber { get; init; }
        public string ContractCurrencyCode { get; init; } = "USD";
        public decimal AllocatedContractCurrencyAmount { get; init; }
    }

    // Slim read-model for active balance transfers. Original amount + currency come from
    // the transfer document itself: the destination ledger leg is posted in the contract's
    // currency, so a RUB transfer into a USD contract has no RUB amount on that row.
    private sealed class SupplierBalanceTransferContractProjection
    {
        public int Id { get; init; }
        public int ContractId { get; init; }
        public string? ContractNumber { get; init; }
        public DateTime TransferDate { get; init; }
        public decimal TransferOriginalAmount { get; init; }
        public string OriginalCurrencyCode { get; init; } = "USD";
        public decimal TransferValueUsd { get; init; }
        public string? ReferenceNumber { get; init; }
    }

    private sealed class SupplierLoadingRubProjection
    {
        public int ContractId { get; init; }
        public decimal LoadedQuantityMt { get; init; }
        public decimal? LoadingPriceUsd { get; init; }
        public string SettlementCurrencyCode { get; init; } = "USD";
        public RubSettlementRateStatus RubRateStatus { get; init; }
        public decimal? RubPerUsdRate { get; init; }
        public decimal? AmountRubAtRubLock { get; init; }
        public decimal? SettlementUnitPriceRub { get; init; }
        public decimal? SettlementValueRub { get; init; }
    }

    [HttpGet]
    public async Task<IActionResult> GetCloneData(int id)
    {
        var item = await _db.Suppliers.AsNoTracking()
            .Where(s => s.Id == id)
            .Select(s => new { s.Code, s.Name, s.Country, s.ContactPerson, s.Phone, s.IsActive })
            .FirstOrDefaultAsync();
        if (item == null) return NotFound();
        return Json(item);
    }
}
