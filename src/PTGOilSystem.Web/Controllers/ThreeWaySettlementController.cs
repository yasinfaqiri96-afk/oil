using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.ThreeWaySettlement;
using PTGOilSystem.Web.Services.Time;

namespace PTGOilSystem.Web.Controllers;

[Authorize]
public class ThreeWaySettlementController : Controller
{
    public const string LedgerSourceType = "ThreeWaySettlement";
    public const string CancellationLedgerSourceType = "ThreeWaySettlementCancellation";

    private const string CustomerLedgerDescription = "تسویه سه‌طرفه: پرداخت مشتری به تأمین‌کننده";
    private const string SupplierLedgerDescription = "تسویه سه‌طرفه: کاهش بدهی تأمین‌کننده";
    private const string CancellationLedgerDescription = "لغو تسویه سه‌طرفه";
    private const string PreviewOnlyMessage = "فعلاً فقط پیش‌نمایش است";

    private readonly ApplicationDbContext _db;

    // مرحلهٔ ۸ — Dual-write اختیاری به دفتر کل جدید. پشت Feature Flag و null-safe.
    private readonly Services.Accounting.IThreeWaySettlementAccountingAdapter? _settlementAccounting;

    public ThreeWaySettlementController(
        ApplicationDbContext db,
        Services.Accounting.IThreeWaySettlementAccountingAdapter? settlementAccounting = null)
    {
        _db = db;
        _settlementAccounting = settlementAccounting;
    }

    [HttpGet]
    public async Task<IActionResult> Index(
        int? customerId = null,
        int? supplierId = null,
        int? sarrafId = null,
        decimal? amount = null,
        string? currency = null,
        int? payeeType = null,
        int? paymentTransactionId = null,
        int? paymentId = null)
    {
        var resolvedPayee = ResolvePayeeType(payeeType, supplierId, sarrafId);
        var normalizedCurrency = NormalizeCurrency(currency);

        var model = new ThreeWaySettlementPreviewViewModel
        {
            CustomerId = customerId,
            SupplierId = supplierId,
            SarrafId = sarrafId,
            PayeeType = resolvedPayee,
            CustomerPaidAmount = amount ?? 0m,
            PayeeAcceptedAmount = amount ?? 0m,
            Currency = normalizedCurrency
        };

        var sourcePaymentId = paymentTransactionId ?? paymentId;
        if (sourcePaymentId.HasValue)
        {
            await ApplyPaymentPrefillAsync(model, sourcePaymentId.Value, payeeType.HasValue);
        }

        // پیش‌فرض نمایش فرم: ارز/نرخ هر طرف از ارز/نرخ پایه پر می‌شود تا حالت تک‌ارز ساده بماند.
        model.CustomerPaidCurrency ??= model.Currency;
        model.SupplierAcceptedCurrency ??= model.Currency;
        model.CustomerPaidFxRateToUsd ??= model.FxRateToUsd;
        model.SupplierAcceptedFxRateToUsd ??= model.FxRateToUsd;

        await PopulateNamesAsync(model);
        await PopulateLookupsAsync(model);
        return View(model);
    }

    [HttpGet]
    public async Task<IActionResult> Details(int id)
    {
        var settlement = await _db.ThreeWaySettlements
            .AsNoTracking()
            .Include(s => s.Customer)
            .Include(s => s.Supplier)
            .Include(s => s.Sarraf)
            .Include(s => s.CustomerSaleContract)
            .Include(s => s.SupplierPurchaseContract)
            .Include(s => s.CustomerLedgerEntry)
            .Include(s => s.SupplierLedgerEntry)
            .FirstOrDefaultAsync(s => s.Id == id);

        if (settlement is null)
        {
            return NotFound();
        }

        await PopulateCancellationLedgerViewBagAsync(settlement.Id);
        return View(settlement);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Preview(ThreeWaySettlementPreviewViewModel model)
    {
        NormalizePreviewInput(model);
        CalculatePreview(model);

        await PopulateNamesAsync(model);
        await PopulateLookupsAsync(model);
        ViewBag.DuplicateWarning = await FindDuplicateWarningAsync(model);
        ViewBag.SarrafOverlapWarning = (await DetectSarrafSettlementOverlapAsync(model)).Message;
        return View("Index", model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Confirm(ThreeWaySettlementPreviewViewModel model)
    {
        var currencyWasMissing = string.IsNullOrWhiteSpace(model.Currency);
        NormalizePreviewInput(model);
        CalculatePreview(model);

        await ValidateConfirmAsync(model, currencyWasMissing);

        // گارد ضد دوباره‌کم‌شدن بدهی تأمین‌کننده: اگر تسویه صراف مشابه قبلاً ثبت شده، duplicate قوی Confirm را block می‌کند.
        var sarrafOverlap = await DetectSarrafSettlementOverlapAsync(model);
        if (sarrafOverlap.Level == SarrafOverlapLevel.Strong)
        {
            ModelState.AddModelError(string.Empty, StrongSarrafOverlapMessage);
        }

        if (!ModelState.IsValid)
        {
            await PopulateNamesAsync(model);
            await PopulateLookupsAsync(model);
            ViewBag.DuplicateWarning = await FindDuplicateWarningAsync(model);
            ViewBag.SarrafOverlapWarning = sarrafOverlap.Message;
            return View("Index", model);
        }

        await using var transaction = await BeginTransactionIfRelationalAsync();

        var settlement = BuildSettlement(model, HttpContext?.User?.Identity?.Name);
        _db.ThreeWaySettlements.Add(settlement);
        await _db.SaveChangesAsync();

        var customerLedger = BuildCustomerLedger(settlement);
        var supplierLedger = BuildSupplierLedger(settlement);
        _db.LedgerEntries.AddRange(customerLedger, supplierLedger);
        await _db.SaveChangesAsync();

        settlement.CustomerLedgerEntryId = customerLedger.Id;
        settlement.SupplierLedgerEntryId = supplierLedger.Id;
        await _db.SaveChangesAsync();

        if (_settlementAccounting is not null)
        {
            await _settlementAccounting.TryPostSettlementAsync(settlement);
        }

        if (transaction is not null)
        {
            await transaction.CommitAsync();
        }

        return RedirectToAction(nameof(Details), new { id = settlement.Id });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Cancel(int id, string? cancellationReason)
    {
        var settlement = await _db.ThreeWaySettlements
            .Include(s => s.CustomerLedgerEntry)
            .Include(s => s.SupplierLedgerEntry)
            .FirstOrDefaultAsync(s => s.Id == id);

        if (settlement is null)
        {
            return NotFound();
        }

        var cleanReason = Clean(cancellationReason);
        if (string.IsNullOrWhiteSpace(cleanReason))
        {
            ModelState.AddModelError(nameof(cancellationReason), "دلیل لغو الزامی است.");
        }

        if (settlement.Status != ThreeWaySettlementStatus.Posted)
        {
            ModelState.AddModelError(string.Empty, "این تسویه قبلاً لغو شده یا قابل لغو نیست.");
        }

        if (settlement.CustomerLedgerEntry is null || settlement.SupplierLedgerEntry is null)
        {
            ModelState.AddModelError(string.Empty, "Ledger اصلی تسویه کامل نیست و لغو امن ممکن نیست.");
        }

        var alreadyHasCancellationLedger = await _db.LedgerEntries
            .AsNoTracking()
            .AnyAsync(l => l.SourceType == CancellationLedgerSourceType && l.SourceId == settlement.Id);
        if (alreadyHasCancellationLedger)
        {
            ModelState.AddModelError(string.Empty, "برای این تسویه قبلاً سند لغو ساخته شده است.");
        }

        if (!ModelState.IsValid)
        {
            await ReloadDetailsNavigationAsync(settlement);
            await PopulateCancellationLedgerViewBagAsync(settlement.Id);
            return View("Details", settlement);
        }

        await using var transaction = await BeginTransactionIfRelationalAsync();

        var cancellationDate = AfghanistanBusinessClock.SystemToday;
        var customerReversal = BuildCancellationLedger(settlement.CustomerLedgerEntry!, settlement, cleanReason!, cancellationDate);
        var supplierReversal = BuildCancellationLedger(settlement.SupplierLedgerEntry!, settlement, cleanReason!, cancellationDate);
        _db.LedgerEntries.AddRange(customerReversal, supplierReversal);

        settlement.Status = ThreeWaySettlementStatus.Cancelled;
        settlement.CancelledAtUtc = DateTime.UtcNow;
        settlement.CancelledByUserName = Clean(HttpContext?.User?.Identity?.Name);
        settlement.CancellationReason = cleanReason;

        await _db.SaveChangesAsync();

        if (_settlementAccounting is not null)
        {
            await _settlementAccounting.TryPostSettlementReversalAsync(settlement);
        }

        if (transaction is not null)
        {
            await transaction.CommitAsync();
        }

        return RedirectToAction(nameof(Details), new { id = settlement.Id });
    }

    private async Task ValidateConfirmAsync(ThreeWaySettlementPreviewViewModel model, bool currencyWasMissing)
    {
        foreach (var blocker in model.PostBlockers)
        {
            ModelState.AddModelError(string.Empty, blocker);
        }

        if (currencyWasMissing)
        {
            ModelState.AddModelError(nameof(model.Currency), "ارز باید مشخص باشد.");
        }

        // فاز A: گیرنده تأمین‌کننده و حالت صراف-به‌عنوان-واسطه قابل ثبت‌اند؛ «حساب دیگر» هنوز فقط پیش‌نمایش است.
        if (model.PayeeType != ThreeWayPayeeType.Supplier && model.PayeeType != ThreeWayPayeeType.Sarraf)
        {
            ModelState.AddModelError(nameof(model.PayeeType), PreviewOnlyMessage);
            return;
        }

        // در حالت صراف، صراف فقط واسطه حواله است؛ گیرنده نهایی همان تأمین‌کننده است و باید مشخص و معتبر باشد.
        if (model.PayeeType == ThreeWayPayeeType.Sarraf)
        {
            if (!model.SarrafId.HasValue)
            {
                ModelState.AddModelError(nameof(model.SarrafId), "برای حالت صراف، انتخاب صراف الزامی است.");
            }
            else if (!await _db.Sarrafs.AsNoTracking().AnyAsync(s => s.Id == model.SarrafId.Value && s.IsActive))
            {
                ModelState.AddModelError(nameof(model.SarrafId), "صراف انتخاب‌شده فعال نیست یا وجود ندارد.");
            }

            if (!model.SupplierId.HasValue)
            {
                ModelState.AddModelError(nameof(model.SupplierId), "برای حالت صراف، تأمین‌کننده گیرنده نهایی باید مشخص باشد.");
            }
        }

        if (!model.CustomerId.HasValue || !model.SupplierId.HasValue)
        {
            return;
        }

        var customerExists = await _db.Customers
            .AsNoTracking()
            .AnyAsync(c => c.Id == model.CustomerId.Value);
        if (!customerExists)
        {
            ModelState.AddModelError(nameof(model.CustomerId), "مشتری پیدا نشد.");
        }

        var supplierExists = await _db.Suppliers
            .AsNoTracking()
            .AnyAsync(s => s.Id == model.SupplierId.Value);
        if (!supplierExists)
        {
            ModelState.AddModelError(nameof(model.SupplierId), "تأمین‌کننده پیدا نشد.");
        }

        if (model.CustomerSaleContractId.HasValue)
        {
            var saleContract = await _db.Contracts
                .AsNoTracking()
                .Where(c => c.Id == model.CustomerSaleContractId.Value)
                .Select(c => new { c.ContractType, c.CustomerId })
                .FirstOrDefaultAsync();

            if (saleContract is null)
            {
                ModelState.AddModelError(nameof(model.CustomerSaleContractId), "قرارداد فروش مشتری پیدا نشد.");
            }
            else if (saleContract.ContractType != ContractType.Sale)
            {
                ModelState.AddModelError(nameof(model.CustomerSaleContractId), "قرارداد انتخاب‌شده باید قرارداد فروش باشد.");
            }
            else if (saleContract.CustomerId != model.CustomerId.Value)
            {
                ModelState.AddModelError(nameof(model.CustomerSaleContractId), "قرارداد فروش با مشتری انتخاب‌شده مطابقت ندارد.");
            }
        }

        if (model.SupplierPurchaseContractId.HasValue)
        {
            var purchaseContract = await _db.Contracts
                .AsNoTracking()
                .Where(c => c.Id == model.SupplierPurchaseContractId.Value)
                .Select(c => new { c.ContractType, c.SupplierId })
                .FirstOrDefaultAsync();

            if (purchaseContract is null)
            {
                ModelState.AddModelError(nameof(model.SupplierPurchaseContractId), "قرارداد خرید تأمین‌کننده پیدا نشد.");
            }
            else if (purchaseContract.ContractType != ContractType.Purchase)
            {
                ModelState.AddModelError(nameof(model.SupplierPurchaseContractId), "قرارداد انتخاب‌شده باید قرارداد خرید باشد.");
            }
            else if (purchaseContract.SupplierId != model.SupplierId.Value)
            {
                ModelState.AddModelError(nameof(model.SupplierPurchaseContractId), "قرارداد خرید با تأمین‌کننده انتخاب‌شده مطابقت ندارد.");
            }
        }
    }

    private static ThreeWaySettlement BuildSettlement(ThreeWaySettlementPreviewViewModel model, string? userName)
        => new()
        {
            SettlementDate = model.SettlementDate.Date,
            PayeeType = model.PayeeType == ThreeWayPayeeType.Sarraf
                ? ThreeWayPayeeType.Sarraf
                : ThreeWayPayeeType.Supplier,
            Status = ThreeWaySettlementStatus.Posted,
            CustomerId = model.CustomerId!.Value,
            SupplierId = model.SupplierId!.Value,
            // صراف فقط به‌عنوان واسطه حواله ذخیره می‌شود؛ هیچ LedgerEntry با SarrafId ساخته نمی‌شود.
            SarrafId = model.PayeeType == ThreeWayPayeeType.Sarraf ? model.SarrafId : null,
            CustomerPaidAmount = Money(model.CustomerPaidAmount),
            SupplierAcceptedAmount = Money(model.PayeeAcceptedAmount),
            // Currency/FxRateToUsd پایه = سمت مشتری (نماینده، سازگاری عقب‌رو).
            Currency = model.EffectiveCustomerCurrency,
            FxRateToUsd = Rate(model.EffectiveCustomerRate),
            CustomerPaidCurrency = model.EffectiveCustomerCurrency,
            CustomerPaidFxRateToUsd = Rate(model.EffectiveCustomerRate),
            SupplierAcceptedCurrency = model.EffectiveSupplierCurrency,
            SupplierAcceptedFxRateToUsd = Rate(model.EffectiveSupplierRate),
            CustomerPaidUsd = Money(model.CustomerPaidUsd),
            SupplierAcceptedUsd = Money(model.PayeeAcceptedUsd),
            DifferenceUsd = Money(model.DifferenceUsd),
            DifferenceReason = model.HasDifference ? model.DifferenceReason : null,
            CustomerSaleContractId = model.CustomerSaleContractId,
            SupplierPurchaseContractId = model.SupplierPurchaseContractId,
            HawalaReference = Clean(model.ReferenceNumber),
            Notes = Clean(model.Description),
            PostedAtUtc = DateTime.UtcNow,
            CreatedByUserName = Clean(userName)
        };

    private static LedgerEntry BuildCustomerLedger(ThreeWaySettlement settlement)
        => new()
        {
            EntryDate = settlement.SettlementDate,
            Side = LedgerSide.Debit,
            AmountUsd = settlement.CustomerPaidUsd,
            Currency = "USD",
            SourceAmount = settlement.CustomerPaidAmount,
            SourceCurrencyCode = settlement.EffectiveCustomerPaidCurrency,
            AppliedFxRateToUsd = settlement.EffectiveCustomerPaidFxRateToUsd,
            AppliedFxRateDate = settlement.SettlementDate,
            AppliedFxRateSource = "Three-way settlement",
            Description = CustomerLedgerDescription,
            SourceType = LedgerSourceType,
            SourceId = settlement.Id,
            Reference = settlement.HawalaReference,
            ContractId = settlement.CustomerSaleContractId,
            CustomerId = settlement.CustomerId
        };

    private static LedgerEntry BuildSupplierLedger(ThreeWaySettlement settlement)
        => new()
        {
            EntryDate = settlement.SettlementDate,
            Side = LedgerSide.Debit,
            AmountUsd = settlement.SupplierAcceptedUsd,
            Currency = "USD",
            SourceAmount = settlement.SupplierAcceptedAmount,
            SourceCurrencyCode = settlement.EffectiveSupplierAcceptedCurrency,
            AppliedFxRateToUsd = settlement.EffectiveSupplierAcceptedFxRateToUsd,
            AppliedFxRateDate = settlement.SettlementDate,
            AppliedFxRateSource = "Three-way settlement",
            Description = SupplierLedgerDescription,
            SourceType = LedgerSourceType,
            SourceId = settlement.Id,
            Reference = settlement.HawalaReference,
            ContractId = settlement.SupplierPurchaseContractId,
            SupplierId = settlement.SupplierId
        };

    private static LedgerEntry BuildCancellationLedger(
        LedgerEntry original,
        ThreeWaySettlement settlement,
        string cancellationReason,
        DateTime cancellationDate)
        => new()
        {
            EntryDate = cancellationDate,
            Side = original.Side == LedgerSide.Debit ? LedgerSide.Credit : LedgerSide.Debit,
            AmountUsd = original.AmountUsd,
            Currency = original.Currency,
            SourceAmount = original.SourceAmount,
            SourceCurrencyCode = original.SourceCurrencyCode,
            AppliedFxRateToUsd = original.AppliedFxRateToUsd,
            AppliedFxRateDate = original.AppliedFxRateDate,
            AppliedFxRateSource = original.AppliedFxRateSource,
            Description = $"{CancellationLedgerDescription}: {cancellationReason}",
            SourceType = CancellationLedgerSourceType,
            SourceId = settlement.Id,
            Reference = settlement.HawalaReference,
            ContractId = original.ContractId,
            CustomerId = original.CustomerId,
            SupplierId = original.SupplierId,
            ServiceProviderId = original.ServiceProviderId,
            EmployeeId = original.EmployeeId,
            ShipmentId = original.ShipmentId
        };

    private async Task<string?> FindDuplicateWarningAsync(ThreeWaySettlementPreviewViewModel model)
    {
        if ((model.PayeeType != ThreeWayPayeeType.Supplier && model.PayeeType != ThreeWayPayeeType.Sarraf)
            || !model.CustomerId.HasValue
            || !model.SupplierId.HasValue
            || model.CustomerPaidUsd <= 0m
            || model.PayeeAcceptedUsd <= 0m)
        {
            return null;
        }

        var reference = Clean(model.ReferenceNumber);
        var payeeType = model.PayeeType;
        var sarrafId = model.PayeeType == ThreeWayPayeeType.Sarraf ? model.SarrafId : null;
        var exists = await _db.ThreeWaySettlements
            .AsNoTracking()
            .AnyAsync(s => s.Status == ThreeWaySettlementStatus.Posted
                && s.PayeeType == payeeType
                && s.SettlementDate == model.SettlementDate.Date
                && s.CustomerId == model.CustomerId.Value
                && s.SupplierId == model.SupplierId.Value
                && s.SarrafId == sarrafId
                && s.CustomerPaidUsd == model.CustomerPaidUsd
                && s.SupplierAcceptedUsd == model.PayeeAcceptedUsd
                && s.HawalaReference == reference);

        return exists
            ? "برای همین مشخصات قبلاً یک تسویه ثبت شده است؛ قبل از ثبت دوباره بررسی کنید."
            : null;
    }

    public const string StrongSarrafOverlapMessage =
        "برای همین حواله یا تأمین‌کننده قبلاً تسویه صراف ثبت شده؛ احتمال دوباره‌کم‌شدن بدهی وجود دارد.";
    public const string WeakSarrafOverlapMessage =
        "برای این تأمین‌کننده/صراف قبلاً تسویه صراف ثبت شده است؛ پیش از ثبت، تکراری‌نبودن حواله را بررسی کنید.";

    private enum SarrafOverlapLevel { None, Weak, Strong }

    private readonly record struct SarrafOverlapResult(SarrafOverlapLevel Level, string? Message);

    // فقط بررسی read-only: آیا تسویه صراف (company-funded) مشابهی قبلاً ثبت شده که بدهی همین تأمین‌کننده را کم کرده باشد؟
    // duplicate قوی = مرجع یکسان، یا تاریخ+مبلغ(+صراف) یکسان. هیچ رکوردی ساخته یا تغییر داده نمی‌شود.
    private async Task<SarrafOverlapResult> DetectSarrafSettlementOverlapAsync(ThreeWaySettlementPreviewViewModel model)
    {
        if (!model.SupplierId.HasValue || model.PayeeAcceptedUsd <= 0m)
        {
            return new SarrafOverlapResult(SarrafOverlapLevel.None, null);
        }

        var candidates = await _db.SarrafSettlements
            .AsNoTracking()
            .Where(s => s.Status == SarrafSettlementStatus.Posted && s.SupplierId == model.SupplierId.Value)
            .Select(s => new { s.ReferenceNumber, s.SettlementDate, s.SupplierAcceptedAmountUsd, s.SarrafId })
            .ToListAsync();

        if (candidates.Count == 0)
        {
            return new SarrafOverlapResult(SarrafOverlapLevel.None, null);
        }

        var reference = Clean(model.ReferenceNumber);
        var settlementDate = model.SettlementDate.Date;
        var amountUsd = model.PayeeAcceptedUsd;
        var sarrafId = model.SarrafId;

        var hasStrong = candidates.Any(c =>
            (!string.IsNullOrWhiteSpace(reference)
                && string.Equals(Clean(c.ReferenceNumber), reference, StringComparison.OrdinalIgnoreCase))
            || (c.SettlementDate == settlementDate
                && Math.Abs(c.SupplierAcceptedAmountUsd - amountUsd) <= 0.01m
                && (!sarrafId.HasValue || c.SarrafId == sarrafId.Value)));

        if (hasStrong)
        {
            return new SarrafOverlapResult(SarrafOverlapLevel.Strong, StrongSarrafOverlapMessage);
        }

        // ضعیف: همان تأمین‌کننده (و صراف در صورت مشخص‌بودن) تسویه صراف دارد، ولی مرجع/تاریخ/مبلغ یکی نیست.
        var hasWeak = !sarrafId.HasValue || candidates.Any(c => c.SarrafId == sarrafId.Value);
        return hasWeak
            ? new SarrafOverlapResult(SarrafOverlapLevel.Weak, WeakSarrafOverlapMessage)
            : new SarrafOverlapResult(SarrafOverlapLevel.None, null);
    }

    private static ThreeWayPayeeType ResolvePayeeType(int? payeeType, int? supplierId, int? sarrafId)
    {
        if (payeeType.HasValue && Enum.IsDefined(typeof(ThreeWayPayeeType), payeeType.Value))
        {
            return (ThreeWayPayeeType)payeeType.Value;
        }

        if (sarrafId.HasValue)
        {
            return ThreeWayPayeeType.Sarraf;
        }

        if (supplierId.HasValue)
        {
            return ThreeWayPayeeType.Supplier;
        }

        return ThreeWayPayeeType.Supplier;
    }

    private static string NormalizeCurrency(string? currency)
        => string.IsNullOrWhiteSpace(currency) ? "USD" : currency.Trim().ToUpperInvariant();

    private static decimal Round(decimal value)
        => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    private static decimal Money(decimal value)
        => Math.Round(value, 4, MidpointRounding.AwayFromZero);

    private static decimal Rate(decimal value)
        => Math.Round(value, 6, MidpointRounding.AwayFromZero);

    private static string? Clean(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static DateTime UtcDate(DateTime value)
        => DateTime.SpecifyKind(value.Date, DateTimeKind.Utc);

    private static void NormalizePreviewInput(ThreeWaySettlementPreviewViewModel model)
    {
        model.SettlementDate = UtcDate(model.SettlementDate);
        model.Currency = NormalizeCurrency(model.Currency);
        model.OtherPayeeName = Clean(model.OtherPayeeName);
        model.ReferenceNumber = Clean(model.ReferenceNumber);
        model.Description = Clean(model.Description);

        // فاز B1 — هر طرف ارز/نرخ خودش را می‌گیرد؛ خالی → از ارز/نرخ پایه (و سپس سمت مشتری) پر می‌شود.
        model.CustomerPaidCurrency = string.IsNullOrWhiteSpace(model.CustomerPaidCurrency)
            ? model.Currency
            : NormalizeCurrency(model.CustomerPaidCurrency);
        if (!(model.CustomerPaidFxRateToUsd > 0m))
        {
            model.CustomerPaidFxRateToUsd = model.FxRateToUsd > 0m ? model.FxRateToUsd : 1m;
        }

        model.SupplierAcceptedCurrency = string.IsNullOrWhiteSpace(model.SupplierAcceptedCurrency)
            ? model.CustomerPaidCurrency
            : NormalizeCurrency(model.SupplierAcceptedCurrency);
        if (!(model.SupplierAcceptedFxRateToUsd > 0m))
        {
            model.SupplierAcceptedFxRateToUsd = model.CustomerPaidFxRateToUsd;
        }
    }

    private static void CalculatePreview(ThreeWaySettlementPreviewViewModel model)
    {
        // USD هر طرف مستقل از ارز/نرخ خودش محاسبه می‌شود.
        model.CustomerPaidUsd = Round(model.CustomerPaidAmount * model.EffectiveCustomerRate);
        model.PayeeAcceptedUsd = Round(model.PayeeAcceptedAmount * model.EffectiveSupplierRate);
        model.DifferenceUsd = Round(model.CustomerPaidUsd - model.PayeeAcceptedUsd);
        model.ShowPreview = true;
    }

    private async Task ApplyPaymentPrefillAsync(
        ThreeWaySettlementPreviewViewModel model,
        int paymentTransactionId,
        bool payeeTypeWasExplicit)
    {
        var payment = await _db.PaymentTransactions
            .AsNoTracking()
            .Where(p => p.Id == paymentTransactionId)
            .Select(p => new
            {
                p.Id,
                p.CustomerId,
                p.SupplierId,
                p.SarrafId,
                p.Amount,
                p.Currency,
                p.AmountUsd,
                p.AppliedFxRateToUsd
            })
            .FirstOrDefaultAsync();

        if (payment is null)
        {
            return;
        }

        model.SourcePaymentTransactionId = payment.Id;

        if (!model.CustomerId.HasValue && payment.CustomerId.HasValue)
        {
            model.CustomerId = payment.CustomerId.Value;
        }

        if (!model.SupplierId.HasValue && payment.SupplierId.HasValue)
        {
            model.SupplierId = payment.SupplierId.Value;
        }

        if (!model.SarrafId.HasValue && payment.SarrafId.HasValue)
        {
            model.SarrafId = payment.SarrafId.Value;
        }

        if (!payeeTypeWasExplicit)
        {
            model.PayeeType = payment.SarrafId.HasValue
                ? ThreeWayPayeeType.Sarraf
                : payment.SupplierId.HasValue
                    ? ThreeWayPayeeType.Supplier
                    : model.PayeeType;
        }

        if (model.CustomerPaidAmount <= 0m && payment.Amount > 0m)
        {
            model.CustomerPaidAmount = payment.Amount;
        }

        if (model.PayeeAcceptedAmount <= 0m && payment.Amount > 0m)
        {
            model.PayeeAcceptedAmount = payment.Amount;
        }

        if (!string.IsNullOrWhiteSpace(payment.Currency))
        {
            model.Currency = NormalizeCurrency(payment.Currency);
        }

        if (payment.AppliedFxRateToUsd.HasValue && payment.AppliedFxRateToUsd.Value > 0m)
        {
            model.FxRateToUsd = payment.AppliedFxRateToUsd.Value;
        }
        else if (payment.Amount > 0m && payment.AmountUsd > 0m)
        {
            model.FxRateToUsd = payment.AmountUsd / payment.Amount;
        }
    }

    private async Task PopulateNamesAsync(ThreeWaySettlementPreviewViewModel model)
    {
        if (model.CustomerId.HasValue)
        {
            model.CustomerName = await _db.Customers
                .AsNoTracking()
                .Where(c => c.Id == model.CustomerId.Value)
                .Select(c => c.Name)
                .FirstOrDefaultAsync();
        }
        else
        {
            model.CustomerName = null;
        }

        model.PayeeName = model.PayeeType switch
        {
            ThreeWayPayeeType.Supplier when model.SupplierId.HasValue => await _db.Suppliers
                .AsNoTracking()
                .Where(s => s.Id == model.SupplierId.Value)
                .Select(s => s.Name)
                .FirstOrDefaultAsync(),
            ThreeWayPayeeType.Sarraf when model.SarrafId.HasValue => await _db.Sarrafs
                .AsNoTracking()
                .Where(s => s.Id == model.SarrafId.Value)
                .Select(s => s.Name)
                .FirstOrDefaultAsync(),
            ThreeWayPayeeType.OtherAccount => model.OtherPayeeName,
            _ => null
        };
    }

    private async Task PopulateLookupsAsync(ThreeWaySettlementPreviewViewModel model)
    {
        ViewBag.Customers = new SelectList(
            await _db.Customers.AsNoTracking()
                .Where(c => c.IsActive || (model.CustomerId.HasValue && c.Id == model.CustomerId.Value))
                .OrderBy(c => c.Name)
                .Select(c => new { c.Id, c.Name })
                .ToListAsync(),
            "Id", "Name", model.CustomerId);

        ViewBag.Suppliers = new SelectList(
            await _db.Suppliers.AsNoTracking()
                .Where(s => s.IsActive || (model.SupplierId.HasValue && s.Id == model.SupplierId.Value))
                .OrderBy(s => s.Name)
                .Select(s => new { s.Id, s.Name })
                .ToListAsync(),
            "Id", "Name", model.SupplierId);

        ViewBag.Sarrafs = new SelectList(
            await _db.Sarrafs.AsNoTracking()
                .Where(s => s.IsActive || (model.SarrafId.HasValue && s.Id == model.SarrafId.Value))
                .OrderBy(s => s.Name)
                .Select(s => new { s.Id, s.Name })
                .ToListAsync(),
            "Id", "Name", model.SarrafId);

        ViewBag.SaleContracts = new SelectList(
            await _db.Contracts.AsNoTracking()
                .Where(c => c.ContractType == ContractType.Sale)
                .OrderByDescending(c => c.ContractDate)
                .Select(c => new { c.Id, c.ContractNumber })
                .Take(300)
                .ToListAsync(),
            "Id", "ContractNumber", model.CustomerSaleContractId);

        ViewBag.PurchaseContracts = new SelectList(
            await _db.Contracts.AsNoTracking()
                .Where(c => c.ContractType == ContractType.Purchase)
                .OrderByDescending(c => c.ContractDate)
                .Select(c => new { c.Id, c.ContractNumber })
                .Take(300)
                .ToListAsync(),
            "Id", "ContractNumber", model.SupplierPurchaseContractId);

        ViewBag.Currencies = new SelectList(
            await _db.Currencies.AsNoTracking()
                .Where(c => c.IsActive)
                .OrderBy(c => c.Code)
                .Select(c => c.Code)
                .ToListAsync(),
            model.Currency);
    }

    private async Task ReloadDetailsNavigationAsync(ThreeWaySettlement settlement)
    {
        await _db.Entry(settlement).Reference(s => s.Customer).LoadAsync();
        await _db.Entry(settlement).Reference(s => s.Supplier).LoadAsync();
        await _db.Entry(settlement).Reference(s => s.Sarraf).LoadAsync();
        await _db.Entry(settlement).Reference(s => s.CustomerSaleContract).LoadAsync();
        await _db.Entry(settlement).Reference(s => s.SupplierPurchaseContract).LoadAsync();
        await _db.Entry(settlement).Reference(s => s.CustomerLedgerEntry).LoadAsync();
        await _db.Entry(settlement).Reference(s => s.SupplierLedgerEntry).LoadAsync();
    }

    private async Task PopulateCancellationLedgerViewBagAsync(int settlementId)
    {
        ViewBag.CancellationLedgerEntries = await _db.LedgerEntries
            .AsNoTracking()
            .Where(l => l.SourceType == CancellationLedgerSourceType && l.SourceId == settlementId)
            .OrderBy(l => l.Id)
            .ToListAsync();
    }

    private async Task<IDbContextTransaction?> BeginTransactionIfRelationalAsync()
        => _db.Database.IsRelational()
            ? await _db.Database.BeginTransactionAsync()
            : null;
}
