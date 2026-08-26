namespace PTGOilSystem.Web.Services.CompanyFlow;

/// <summary>
/// SourceTypeهایی که جهت تجاری‌شان قطعی است و نباید از سمت حسابداری حدس زده شود.
/// رشته‌ها عیناً همان مقادیری‌اند که در LedgerEntry.SourceType نوشته می‌شوند؛
/// هیچ داده‌ای تغییر نمی‌کند، فقط اینجا یکجا نام‌گذاری شده‌اند.
/// </summary>
public static class CompanyFlowSourceTypes
{
    // ---- رویدادهای کالا/خدمت ----
    public const string Loading = "Loading";
    public const string Sale = "Sale";
    public const string Expense = "Expense";
    public const string ShortageCharge = "ShortageCharge";

    // ---- صراف ----
    public const string SarrafSettlement = "SarrafSettlement";
    public const string SarrafSettlementCancel = "SarrafSettlementCancel";
    public const string SarrafSettlementEditReversal = "SarrafSettlementEditReversal";
    public const string SarrafSettlementExchangeDifference = "SarrafSettlementExchangeDifference";
    public const string SupplierViaSarrafPayment = "SupplierViaSarrafPayment";
    public const string SupplierViaSarrafPayable = "SupplierViaSarrafPayable";
    public const string SarrafFxGain = "SarrafFxGain";

    // ---- تخصیص پیش‌پرداخت تأمین‌کننده ----
    public const string SupplierPaymentAllocation = "SupplierPaymentAllocation";
    public const string SupplierPaymentAllocationReversal = "SupplierPaymentAllocationReversal";
    public const string SupplierPaymentAllocationExchangeDifference = "SupplierPaymentAllocationExchangeDifference";
    public const string SupplierPaymentAllocationExchangeDifferenceReversal = "SupplierPaymentAllocationExchangeDifferenceReversal";

    // ---- سایر ----
    public const string ContractBalanceTransfer = "ContractBalanceTransfer";
    public const string ThreeWaySettlement = "ThreeWaySettlement";
    public const string ThreeWaySettlementCancellation = "ThreeWaySettlementCancellation";
    public const string OpeningBalance = "OpeningBalance";

    // ---- تراکنش معاش کارمند (از EmployeeSalaryTransaction، نه LedgerEntry) ----
    public const string SalaryAccrual = "SalaryAccrual";
    public const string SalaryPayment = "SalaryPayment";
    public const string SalaryAdvance = "SalaryAdvance";
    public const string SalaryDeduction = "SalaryDeduction";
    public const string Bonus = "Bonus";
    public const string Adjustment = "Adjustment";

    /// <summary>
    /// پسوندِ مرجعِ سطرِ معکوس‌کننده. برگشتِ بارگیری/فروش/مصرف عمداً SourceType سند اصلی را
    /// نگه می‌دارد (تا ردیابی به همان سند حفظ شود)، پس نوعِ سند به‌تنهایی نمی‌گوید این سطر
    /// برگشت است. همهٔ مسیرهای برگشت — <c>LedgerReversalWriter</c>، لغو مصرف، لغو کرایهٔ
    /// دارایی — دقیقاً همین پسوند را به مرجع اصلی می‌چسبانند، بنابراین این نشانهٔ صریح و
    /// از پیش ذخیره‌شده است و به هیچ ستون تازه یا Migration نیاز ندارد.
    /// </summary>
    public const string ReversalReferenceSuffix = "-CANCEL";

    /// <summary>سطرهایی که فقط معکوس‌کننده‌اند و باید با lifecycle=Reversal خوانده شوند.</summary>
    public static bool IsReversal(string? sourceType)
        => sourceType is SarrafSettlementCancel
            or SarrafSettlementEditReversal
            or SupplierPaymentAllocationReversal
            or SupplierPaymentAllocationExchangeDifferenceReversal
            or ThreeWaySettlementCancellation;

    /// <summary>مرجعِ سطر، نشانهٔ صریحِ برگشت را دارد؟ (به Debit/Credit تکیه نمی‌کند.)</summary>
    public static bool IsReversalReference(string? reference)
        => reference is not null
            && reference.EndsWith(ReversalReferenceSuffix, StringComparison.Ordinal);

    /// <summary>
    /// تشخیص کاملِ سطر برگشت: یا SourceType اختصاصیِ برگشت دارد، یا مرجعش با پسوند برگشت
    /// تمام می‌شود. هر خوانندهٔ صورت‌حساب باید از همین استفاده کند تا اصل و برگشتِ یک سند
    /// همدیگر را خنثی کنند، نه اینکه دو برابر شوند.
    /// </summary>
    public static bool IsReversal(string? sourceType, string? reference)
        => IsReversal(sourceType) || IsReversalReference(reference);
}
