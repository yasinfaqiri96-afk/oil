using PTGOilSystem.Web.Models.Entities;

namespace PTGOilSystem.Web.Models.Suppliers;

public sealed class SupplierIndexViewModel
{
    public string? Search { get; init; }
    public IReadOnlyList<SupplierIndexItemViewModel> Items { get; init; } = [];
    public int CurrentPage { get; init; } = 1;
    public int PageCount { get; init; } = 1;
    public int TotalCount { get; init; }
}

public sealed class SupplierIndexItemViewModel
{
    public int SupplierId { get; init; }
    public string? Code { get; init; }
    public string Name { get; init; } = string.Empty;
    public bool IsActive { get; init; }
    public int PurchaseContractsCount { get; set; }
    public int ActivePurchaseContractsCount { get; set; }
    public decimal TotalPurchaseQuantityMt { get; set; }
    public decimal LoadedPurchaseValueUsd { get; set; }
    public decimal LedgerDebitUsd { get; set; }
    public decimal LedgerCreditUsd { get; set; }
    // مانده نمایشی مطابق قرارداد صورت‌حساب: Σ(داده − گرفته) = پرداخت‌ها − بار.
    // مثبت یعنی شرکت پیش‌پرداخت دارد. هم‌علامتِ PartyStatementSummary.ClosingBalance
    // است تا صفحاتی که به این مقدار fallback می‌کنند علامت متضاد نشان ندهند.
    public decimal LedgerBalanceUsd => LedgerDebitUsd - LedgerCreditUsd;
    public decimal TotalPaidUsd { get; set; }
    public decimal SupplierTotalClaimUsd => LoadedPurchaseValueUsd - TotalPaidUsd;
    public DateTime? LastPaymentDate { get; set; }
}

public sealed class SupplierProfileViewModel
{
    public int SupplierId { get; init; }
    public int Id => SupplierId;
    public string? Code { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? NamePersian { get; init; }
    public string? Country { get; init; }
    public string? ContactPerson { get; init; }
    public string? Phone { get; init; }
    public string? Address { get; init; }
    public string? Notes { get; init; }
    public bool IsActive { get; init; }
    public DateTime CreatedAtUtc { get; init; }
    public DateTime? UpdatedAtUtc { get; init; }

    public int PurchaseContractsCount { get; init; }
    public int ActivePurchaseContractsCount { get; init; }
    public decimal TotalPurchaseQuantityMt { get; init; }
    public decimal EstimatedContractValueUsd { get; init; }
    public decimal? EstimatedContractValueRub { get; init; }
    public decimal LoadedPurchaseQuantityMt { get; init; }
    public decimal LoadedPurchaseValueUsd { get; init; }
    public decimal? LoadedPurchaseValueRub { get; init; }
    public decimal RemainingPurchaseQuantityMt { get; init; }
    public decimal EstimatedRemainingContractValueUsd { get; init; }
    public decimal? EstimatedRemainingContractValueRub { get; init; }
    public decimal LedgerDebitUsd { get; init; }
    public decimal LedgerCreditUsd { get; init; }
    // مانده نمایشی مطابق قرارداد صورت‌حساب: Σ(داده − گرفته) = پرداخت‌ها − بار.
    // مثبت یعنی شرکت پیش‌پرداخت دارد. هم‌علامتِ PartyStatementSummary.ClosingBalance
    // است تا صفحاتی که به این مقدار fallback می‌کنند علامت متضاد نشان ندهند.
    public decimal LedgerBalanceUsd => LedgerDebitUsd - LedgerCreditUsd;
    public decimal TotalPaidUsd { get; init; }
    public decimal? TotalPaidRub { get; init; }
    public decimal TotalPaidActualUsd { get; init; }
    public decimal? TotalPaidActualRub { get; init; }
    public decimal SupplierRemainingClaimUsd => LoadedPurchaseValueUsd - TotalPaidUsd;
    // مانده روبلی فقط وقتی معنا دارد که هر دو طرف معادله منبع روبلیِ واقعی داشته باشند.
    // null یعنی «قابل محاسبه نیست»، نه صفر؛ اگر پرداخت روبلی نداریم نباید طلبِ روبلی را
    // با فرضِ «صفر روبل پرداخت شده» بسازیم (پرداخت دالری هم طلب را کم می‌کند).
    public decimal? SupplierRemainingClaimRub => LoadedPurchaseValueRub.HasValue && TotalPaidRub.HasValue
        ? LoadedPurchaseValueRub.Value - TotalPaidRub.Value
        : null;
    public DateTime? LastPaymentDate { get; init; }
    public decimal SarrafAcceptedUsd { get; init; }
    public decimal SarrafSupplierShortfallUsd { get; init; }

    // D4 — تفاوت نرخ ارز شناسایی‌شده (فقط نمایش، از تسویه‌های صراف Posted که با
    // روش «شناسایی سود/زیان نرخ ارز» ثبت شده‌اند). هیچ محاسبهٔ P&L رسمی تغییر نمی‌کند.
    // اگر HasRecognizedFx برابر false باشد، View برچسب «نیاز به نرخ/داده تکمیلی» را نشان می‌دهد.
    public bool HasRecognizedFx { get; init; }
    public decimal RecognizedFxGainUsd { get; init; }
    public decimal RecognizedFxLossUsd { get; init; }
    public decimal RecognizedFxNetUsd => RecognizedFxLossUsd - RecognizedFxGainUsd;

    public int? SelectedContractId { get; init; }
    public string ActiveTab { get; init; } = "overview";
    public SupplierContractSummaryViewModel? SelectedContract { get; init; }
    public IReadOnlyList<SupplierStatementContractFilterOptionViewModel> StatementContractOptions { get; init; } = [];
    public IReadOnlyList<SupplierContractSummaryViewModel> Contracts { get; init; } = [];
    public IReadOnlyList<SupplierPaymentSummaryViewModel> Payments { get; init; } = [];
    public IReadOnlyList<SupplierSarrafSettlementViewModel> SarrafSettlements { get; init; } = [];
    public IReadOnlyList<SupplierStatementRowViewModel> StatementRows { get; init; } = [];

    // لیست نمایشیِ یکپارچهٔ پرداخت‌ها (نقدی/بانکی + صراف) برای تب «پرداخت‌ها».
    // فقط نمایش است؛ هیچ مبلغی از این لیست در جمع‌های مالی (TotalPaidUsd و …) دوباره حساب نمی‌شود.
    public IReadOnlyList<SupplierPaymentLineViewModel> PaymentLines { get; init; } = [];
    public decimal UnallocatedPaymentTotalUsd { get; init; }

    // پیش‌پرداخت نزد تأمین‌کننده (جمع، مصرف‌شده برای قراردادها، مانده آزاد، تفکیک ارز).
    public bool HasAdvances { get; init; }
    public decimal AdvanceTotalUsd { get; init; }
    public decimal AdvanceAllocatedUsd { get; init; }
    public decimal AdvanceFreeUsd { get; init; }
    public IReadOnlyList<SupplierAdvanceCurrencyRowViewModel> AdvanceByCurrency { get; init; } = [];
    public IReadOnlyList<SupplierAdvanceAllocationRowViewModel> AdvanceAllocations { get; init; } = [];
}

public sealed class SupplierAdvanceCurrencyRowViewModel
{
    public string Currency { get; init; } = "USD";
    public decimal AdvanceTotalUsd { get; init; }
    public decimal AllocatedUsd { get; init; }
    public decimal FreeUsd => AdvanceTotalUsd - AllocatedUsd;
}

public sealed class SupplierAdvanceAllocationRowViewModel
{
    public int Id { get; init; }
    public DateTime AllocationDate { get; init; }
    public int PaymentTransactionId { get; init; }
    public string PaymentReference { get; init; } = string.Empty;
    public int ContractId { get; init; }
    public string ContractNumber { get; init; } = string.Empty;
    public decimal AllocatedBookAmountUsd { get; init; }
    public string ContractCurrencyCode { get; init; } = "USD";
    public decimal AllocatedContractCurrencyAmount { get; init; }
    public bool IsActive { get; init; }
    public string StatusName => IsActive ? "فعال" : "برگشت‌شده";
}

public sealed class SupplierSarrafSettlementViewModel
{
    public int Id { get; init; }
    public DateTime SettlementDate { get; init; }
    public string SarrafName { get; init; } = string.Empty;
    public int? ContractId { get; init; }
    public string? ContractNumber { get; init; }
    public string? ReferenceNumber { get; init; }
    public decimal RequestedAmountUsd { get; init; }
    public decimal? RequestedAmountRub { get; init; }
    public decimal SupplierAcceptedAmountUsd { get; init; }
    public decimal? SupplierAcceptedAmountRub { get; init; }
    public decimal SupplierReductionAmountUsd { get; init; }
    public decimal? SupplierReductionAmountRub { get; init; }
    public decimal SarrafChargedAmountUsd { get; init; }
    public decimal? SarrafChargedAmountRub { get; init; }
    public decimal DifferenceAmountUsd { get; init; }
    public decimal? DifferenceAmountRub { get; init; }
    public SarrafSettlementDifferenceType DifferenceType { get; init; }
    public DifferenceReason? DifferenceReason { get; init; }
    public SarrafSettlementDifferenceTreatment DifferenceTreatment { get; init; }
    public SarrafSettlementStatus Status { get; init; }
    public int? LedgerEntryId { get; init; }
}

// ردیف نمایشیِ یکپارچهٔ پرداخت (PaymentTransaction نقدی/بانکی یا SarrafSettlement صراف).
// فقط برای نمایش در تب «پرداخت‌ها»؛ در هیچ جمع مالی دوباره استفاده نمی‌شود.
public sealed class SupplierPaymentLineViewModel
{
    public DateTime Date { get; init; }
    public string Method { get; init; } = string.Empty; // نقدی / بانکی / صراف
    public decimal Amount { get; init; }
    public string Currency { get; init; } = "USD";
    public decimal AmountUsd { get; init; }
    public string? ContractNumber { get; init; }
    public string Reference { get; init; } = string.Empty;
    public string? Description { get; init; }
    public bool IsSarraf { get; init; }
    public bool IsLedgerOnlyViaSarraf { get; init; }
    public int? LedgerEntryId { get; init; }
    public int? PaymentId { get; init; }
    public int? SarrafSettlementId { get; init; }
}

public sealed class SupplierStatementContractFilterOptionViewModel
{
    public int ContractId { get; init; }
    public string ContractNumber { get; init; } = string.Empty;
}

public sealed class SupplierContractSummaryViewModel
{
    public int ContractId { get; init; }
    public string ContractNumber { get; init; } = string.Empty;
    public string Product { get; init; } = string.Empty;
    public DateTime ContractDate { get; init; }
    public decimal QuantityMt { get; init; }
    public string Currency { get; init; } = "USD";
    public decimal? UnitPriceOriginal { get; init; }
    public decimal? FinalPriceUsd { get; init; }
    public decimal? RubPerUsdRate { get; init; }
    public decimal? EstimatedTotalOriginal { get; init; }
    public decimal? EstimatedTotalUsd { get; init; }
    public decimal? EstimatedTotalRub { get; init; }
    public decimal LoadedQuantityMt { get; init; }
    public decimal LoadedValueUsd { get; init; }
    public decimal? LoadedValueRub { get; init; }
    public decimal RemainingQuantityMt => Math.Max(QuantityMt - LoadedQuantityMt, 0m);
    public decimal? EstimatedUnloadedValueUsd => EstimatedTotalUsd.HasValue
        ? Math.Max(EstimatedTotalUsd.Value - LoadedValueUsd, 0m)
        : null;
    public decimal? EstimatedUnloadedValueRub => EstimatedTotalRub.HasValue && LoadedValueRub.HasValue
        ? Math.Max(EstimatedTotalRub.Value - LoadedValueRub.Value, 0m)
        : null;
    public decimal LedgerDebitUsd { get; init; }
    public decimal LedgerCreditUsd { get; init; }
    // مانده نمایشی مطابق قرارداد صورت‌حساب: Σ(داده − گرفته) = پرداخت‌ها − بار.
    // مثبت یعنی شرکت پیش‌پرداخت دارد. هم‌علامتِ PartyStatementSummary.ClosingBalance
    // است تا صفحاتی که به این مقدار fallback می‌کنند علامت متضاد نشان ندهند.
    public decimal LedgerBalanceUsd => LedgerDebitUsd - LedgerCreditUsd;
    public decimal PaidUsd { get; init; }
    public decimal? PaidRub { get; init; }

    // قیمت واحد برای نمایش (فقط مشتق از فیلدهای موجود؛ هیچ محاسبهٔ مالی جدیدی نیست).
    private bool IsRubContract => string.Equals(Currency, "RUB", StringComparison.OrdinalIgnoreCase);
    // USD/MT = قیمت نهایی متعارف قرارداد.
    public decimal? UnitPriceUsdPerMt => FinalPriceUsd;
    // RUB/MT = اگر قرارداد روبلی است همان قیمت اصلی؛ وگرنه از USD/MT × نرخ روبل (اگر نرخ موجود باشد).
    public decimal? UnitPriceRubPerMt => IsRubContract
        ? UnitPriceOriginal
        : (FinalPriceUsd.HasValue && RubPerUsdRate.HasValue
            ? decimal.Round(FinalPriceUsd.Value * RubPerUsdRate.Value, 4, MidpointRounding.AwayFromZero)
            : (decimal?)null);
    public decimal LoadedValueBalanceUsd => LoadedValueUsd - PaidUsd;
    // مانده‌های روبلی فقط با داشتن هر دو طرفِ روبلی محاسبه می‌شوند؛ نبودِ پرداخت روبلی
    // به معنی «صفر روبل پرداخت شده» نیست (همان قاعدهٔ SupplierRemainingClaimRub).
    public decimal? LoadedValueBalanceRub => LoadedValueRub.HasValue && PaidRub.HasValue
        ? LoadedValueRub.Value - PaidRub.Value
        : null;
    public decimal? EstimatedRemainingUsd => EstimatedTotalUsd.HasValue
        ? EstimatedTotalUsd.Value - PaidUsd
        : null;
    public decimal? EstimatedRemainingRub => EstimatedTotalRub.HasValue && PaidRub.HasValue
        ? EstimatedTotalRub.Value - PaidRub.Value
        : null;
    public ContractStatus Status { get; init; }
    public string StatusName { get; init; } = string.Empty;
    public string StatusBadgeClass { get; init; } = "status-badge-neutral";
}

public sealed class SupplierPaymentSummaryViewModel
{
    public int PaymentId { get; init; }
    public DateTime PaymentDate { get; init; }
    public PaymentDirection Direction { get; init; }
    public string DirectionName { get; init; } = string.Empty;
    public PaymentKind PaymentKind { get; init; }
    public string PaymentKindName { get; init; } = string.Empty;
    public string CashAccount { get; init; } = string.Empty;
    public int? ContractId { get; init; }
    public string? ContractNumber { get; init; }
    public decimal Amount { get; init; }
    public string Currency { get; init; } = "USD";
    public decimal? AppliedFxRateToUsd { get; init; }
    public decimal AmountUsd { get; init; }
    public decimal? RubPerUsdRate { get; init; }
    public decimal? AmountRubEquivalent { get; init; }
    public string? Reference { get; init; }
    public string? Description { get; init; }
    public bool? IsAdvancePayment { get; init; }
    public int? LedgerEntryId { get; init; }
    public DateTime CreatedAtUtc { get; init; }
    public int? CreatedByUserId { get; init; }
    public string? CreatedByDisplay { get; init; }
}

public sealed class SupplierStatementRowViewModel
{
    public int LedgerEntryId { get; init; }
    public DateTime Date { get; init; }
    public string Type { get; init; } = string.Empty;
    public string? Reference { get; init; }
    public string? SourceDetailsController { get; init; }
    public string? SourceDetailsAction { get; init; }
    public int? SourceDetailsRouteId { get; init; }
    public int? ContractId { get; init; }
    public string? ContractNumber { get; init; }
    public string Description { get; init; } = string.Empty;
    public string Currency { get; init; } = "USD";
    public decimal? Debit { get; init; }
    public decimal? Credit { get; init; }
    public decimal? DebitUsd { get; init; }
    public decimal? CreditUsd { get; init; }
    public decimal? DebitRubEquivalent { get; init; }
    public decimal? CreditRubEquivalent { get; init; }
    public decimal RunningBalanceUsd { get; init; }
    public decimal? RunningBalanceRubEquivalent { get; init; }
    public decimal? RubPerUsdRate { get; init; }

    // D4 — نرخ ارز به‌کاررفته در همین سند (از LedgerEntry.AppliedFxRateToUsd).
    // فقط نمایش است؛ اگر null باشد یعنی نرخ ذخیره نشده و در View با «—» نشان داده می‌شود.
    public decimal? FxRateUsed { get; init; }

    // D5 — نرخ معکوس برای نمایش کاربرپسند (مثلاً ۹۱.۳۴ روبل در برابر ۱ دالر).
    // فقط وقتی ارز غیر از USD است و FxRateUsed > 0 مقدار دارد.
    public decimal? FxRateDisplayPerUsd =>
        FxRateUsed.HasValue && FxRateUsed.Value > 0m && !string.Equals(Currency, "USD", StringComparison.OrdinalIgnoreCase)
            ? decimal.Round(1m / FxRateUsed.Value, 4, MidpointRounding.AwayFromZero)
            : (decimal?)null;
}

// ───────────────────────────────────────────────────────────────────────────
// صورت‌حساب خطی تأمین‌کننده به سبک دفتر BNK-HIMOR (فقط نمایش/projection).
// مخصوص Supplier است و با جدول مشترک _PartyStatementTable کاری ندارد؛
// همهٔ مقادیر از ردیف‌های موجود statement ساخته می‌شوند و هیچ محاسبهٔ مالی
// جدیدی (Ledger/Payment/FX) انجام نمی‌شود.
public sealed class SupplierStatementLedgerRowViewModel
{
    public int LedgerEntryId { get; init; }
    public DateTime Date { get; init; }
    public string Type { get; init; } = string.Empty;
    public string? Reference { get; init; }
    public string? SourceDetailsController { get; init; }
    public string? SourceDetailsAction { get; init; }
    public int? SourceDetailsRouteId { get; init; }

    public string? ContractNumber { get; init; }
    // کلاس رنگ ملایم مخصوص هر قرارداد (مثلاً M-15 یک رنگ، M-16 رنگ دیگر).
    public string ContractColorClass { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;

    // مقدار MT و قیمت واحد در projection فعلی statement موجود نیست؛
    // اگر null باشند در View «—» نشان داده می‌شوند (حدس زده نمی‌شود).
    public decimal? QuantityMt { get; init; }
    public decimal? UnitPrice { get; init; }

    // ارز اصلی سند (معمولاً RUB). اگر USD باشد ستون‌های RUB خالی می‌مانند.
    public string OriginalCurrency { get; init; } = "USD";
    public bool IsOriginalRub { get; init; }
    public decimal? CreditOriginal { get; init; }
    public decimal? DebitOriginal { get; init; }
    // مانده جاری به ارز اصلی (RUB) — تجمع per-currency از همان Credit/Debit موجود.
    // اگر سند به USD باشد یا مبلغ اصلی نداشته باشد، null می‌ماند («—»).
    public decimal? RunningBalanceOriginal { get; init; }
    public decimal? CreditRubEquivalent { get; init; }
    public decimal? DebitRubEquivalent { get; init; }
    public decimal? RunningBalanceRubEquivalent { get; init; }

    // نرخ دالر همان سند (RUB در برابر ۱ USD) و نرخ داخلی برای tooltip.
    public decimal? FxRateDisplayPerUsd { get; init; }
    public decimal? FxRateUsed { get; init; }

    public decimal? CreditUsd { get; init; }
    public decimal? DebitUsd { get; init; }
    public decimal RunningBalanceUsd { get; init; }

    // badge کوچک نوع حرکت (پرداخت/دریافت/انتقال/مصرف-دموراژ/تهاتر).
    public string BadgeText { get; init; } = string.Empty;
    public string BadgeClass { get; init; } = "sl-badge-neutral";
    // جهت اثر روی حساب برای رنگ‌بندی مبلغ USD.
    public string EffectClass { get; init; } = "is-neutral";
}

public sealed class SupplierStatementLedgerTableViewModel
{
    public IReadOnlyList<SupplierStatementLedgerRowViewModel> Rows { get; init; } = [];
    public string EmptyTitle { get; init; } = "صورت‌حساب خالی است";
    public string EmptyMessage { get; init; } = "برای این تأمین‌کننده یا قرارداد انتخاب‌شده هنوز سند مالی ثبت نشده است.";
}
