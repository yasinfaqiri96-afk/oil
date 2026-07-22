using System.ComponentModel.DataAnnotations;
using PTGOilSystem.Web.Models.Entities;

namespace PTGOilSystem.Web.Models.Reports;

/// <summary>
/// نتیجهٔ تفاوت نرخ ارز از دید شرکت، بعد از تطبیق (Reconcile) علامتِ محاسبه‌شده با
/// <see cref="SarrafSettlementDifferenceType"/> ذخیره‌شده. هیچ مقداری از این enum در دیتابیس
/// نوشته نمی‌شود؛ فقط برای نمایش گزارش است.
/// </summary>
public enum FxDifferenceResult
{
    /// <summary>هر دو نرخ برابر — نه سود، نه ضرر.</summary>
    None = 0,
    /// <summary>مبلغ قبول‌شدهٔ تأمین‌کننده بیشتر از هزینهٔ واقعی شرکت است.</summary>
    Gain = 1,
    /// <summary>هزینهٔ واقعی شرکت بیشتر از مبلغ قبول‌شدهٔ تأمین‌کننده است.</summary>
    Loss = 2,
    /// <summary>داده‌های ثبت‌شده با هم نمی‌خوانند؛ مبلغ در هیچ جمعی وارد نمی‌شود.</summary>
    NeedsReview = 3
}

/// <summary>وضعیت تطبیق کمیشن واقعی صراف با این تسویه (کمیشن هرگز بخشی از تفاوت نرخ نیست).</summary>
public enum FxCommissionMatchStatus
{
    /// <summary>هیچ سطر کمیشنی برای این تسویه پیدا نشد.</summary>
    None = 0,
    /// <summary>دقیقاً یک سطر کمیشن تطبیق خورد؛ مبلغ قابل اتکا است.</summary>
    Exact = 1,
    /// <summary>بیش از یک سطر کمیشن تطبیق خورد؛ مبلغ‌ها جمع نمی‌شوند و در مجموع‌ها صفر می‌آیند.</summary>
    NeedsReview = 2
}

public enum FxDifferenceResultFilter
{
    [Display(Name = "همه")] All = 0,
    [Display(Name = "فقط سود")] GainOnly = 1,
    [Display(Name = "فقط ضرر")] LossOnly = 2,
    [Display(Name = "بدون تفاوت")] NoDifference = 3,
    [Display(Name = "نیازمند بررسی")] NeedsReview = 4
}

public enum FxCommissionFilter
{
    [Display(Name = "همه")] All = 0,
    [Display(Name = "دارای کمیشن")] WithCommission = 1,
    [Display(Name = "بدون کمیشن")] WithoutCommission = 2
}

public sealed class FxDifferenceReportFilterViewModel
{
    [DataType(DataType.Date)] public DateTime? FromDate { get; set; }
    [DataType(DataType.Date)] public DateTime? ToDate { get; set; }

    /// <summary>شرکتِ قرارداد. تسویهٔ صراف خودش <c>CompanyId</c> ندارد، پس فیلتر روی <c>Contract.CompanyId</c> اجرا می‌شود.</summary>
    public int? CompanyId { get; set; }
    public int? SupplierId { get; set; }
    public int? SarrafId { get; set; }
    public int? ContractId { get; set; }
    public string? Currency { get; set; }
    public string? Reference { get; set; }
    public FxDifferenceResultFilter Result { get; set; } = FxDifferenceResultFilter.All;
    public SarrafSettlementStatus? Status { get; set; }
    public FxCommissionFilter Commission { get; set; } = FxCommissionFilter.All;

    public bool HasAny
        => FromDate.HasValue || ToDate.HasValue || CompanyId.HasValue || SupplierId.HasValue
            || SarrafId.HasValue || ContractId.HasValue || !string.IsNullOrWhiteSpace(Currency)
            || !string.IsNullOrWhiteSpace(Reference) || Result != FxDifferenceResultFilter.All
            || Status.HasValue || Commission != FxCommissionFilter.All;
}

/// <summary>بخش «پرداخت اصلی» در جزئیات ردیف.</summary>
public sealed class FxDifferencePaymentDetail
{
    public string Currency { get; init; } = "";
    public decimal Amount { get; init; }
    public decimal CompanyRate { get; init; }
    public decimal? SupplierRate { get; init; }
    public decimal CompanyCostUsd { get; init; }
    public decimal SupplierAcceptedUsd { get; init; }
}

/// <summary>بخش «اثر تأمین‌کننده» در جزئیات ردیف.</summary>
public sealed class FxDifferenceSupplierDetail
{
    public int? SupplierId { get; init; }
    public string? SupplierName { get; init; }
    /// <summary>مبلغی که واقعاً در سطر دفترِ تأمین‌کنندهٔ همین تسویه نشسته است.</summary>
    public decimal? StatementAmountUsd { get; init; }
    public int? StatementLedgerEntryId { get; init; }
    public int? ContractId { get; init; }
    public string? ContractNumber { get; init; }
    /// <summary>همان سطر دفتر، وقتی به قرارداد بسته شده باشد (سهمِ این تسویه از PaidUsd قرارداد).</summary>
    public decimal? ContractPaidUsd { get; init; }
    /// <summary>سطرهای تفاوت نرخ نه <c>SupplierId</c> دارند نه <c>ContractId</c> — پس وارد حساب تأمین‌کننده نشده‌اند.</summary>
    public bool FxIsolatedFromSupplier { get; init; } = true;
}

/// <summary>بخش «اثر صراف» در جزئیات ردیف.</summary>
public sealed class FxDifferenceSarrafDetail
{
    public int SarrafId { get; init; }
    public string SarrafName { get; init; } = "";
    public decimal CompanyCostUsd { get; init; }
    public SarrafSettlementDirection Direction { get; init; }
    public decimal? CommissionUsd { get; init; }
    public FxCommissionMatchStatus CommissionStatus { get; init; }
    public int CommissionMatchCount { get; init; }
    public int? CommissionExpenseTransactionId { get; init; }
}

/// <summary>بخش «اثر سود و زیان» در جزئیات ردیف.</summary>
public sealed class FxDifferenceProfitDetail
{
    public string? LedgerSourceType { get; init; }
    public int? ExpenseTransactionId { get; init; }
    public int? LedgerEntryId { get; init; }
    public int? ExchangeDifferenceLedgerEntryId { get; init; }
    /// <summary>تسویهٔ دو‌نرخی نه <c>PaymentTransaction</c> دارد نه <c>CashAccount</c> — صندوق دست نخورده است.</summary>
    public bool NoCashEffect { get; init; } = true;
}

public sealed class FxDifferenceRowViewModel
{
    public int SettlementId { get; init; }
    public DateTime SettlementDate { get; init; }
    public string? ReferenceNumber { get; init; }
    public string? CompanyName { get; init; }
    public string? SupplierName { get; init; }
    public string SarrafName { get; init; } = "";
    public string? ContractNumber { get; init; }
    public string Currency { get; init; } = "";
    public decimal Amount { get; init; }
    public decimal CompanyRate { get; init; }
    public decimal? SupplierRate { get; init; }
    public decimal CompanyCostUsd { get; init; }
    public decimal SupplierAcceptedUsd { get; init; }

    /// <summary>
    /// <c>SupplierAcceptedAmountUsd − SarrafChargedAmountUsd</c>. تنها منبعِ سود/زیانِ گزارش.
    /// دقت کامل نگه داشته می‌شود؛ گرد کردن فقط در نمایش/خروجی.
    /// </summary>
    public decimal FxResultUsd { get; init; }
    public FxDifferenceResult Result { get; init; }
    public decimal GainUsd { get; init; }
    public decimal LossUsd { get; init; }

    public decimal? CommissionUsd { get; init; }
    public FxCommissionMatchStatus CommissionStatus { get; init; }

    public SarrafSettlementStatus Status { get; init; }
    public string? Description { get; init; }
    public string? CreatedByUserName { get; init; }
    public DateTime CreatedAtUtc { get; init; }

    /// <summary>دلیل(های) ناسازگاری وقتی <see cref="Result"/> برابر NeedsReview است.</summary>
    public IReadOnlyList<string> ReviewNotes { get; init; } = [];

    public FxDifferencePaymentDetail Payment { get; init; } = new();
    public FxDifferenceSupplierDetail Supplier { get; init; } = new();
    public FxDifferenceSarrafDetail Sarraf { get; init; } = new();
    public FxDifferenceProfitDetail Profit { get; init; } = new();
}

public sealed class FxDifferenceCurrencyTotalViewModel
{
    public string Currency { get; init; } = "";
    public decimal Amount { get; init; }
}

public sealed class FxDifferenceTotalsViewModel
{
    public int SettlementCount { get; init; }
    public IReadOnlyList<FxDifferenceCurrencyTotalViewModel> OriginalAmounts { get; init; } = [];
    public decimal TotalCompanyCostUsd { get; init; }
    public decimal TotalSupplierAcceptedUsd { get; init; }
    public decimal TotalGainUsd { get; init; }
    public decimal TotalLossUsd { get; init; }
    public decimal NetFxResultUsd => TotalGainUsd - TotalLossUsd;
    public decimal TotalCommissionUsd { get; init; }
    public int GainCount { get; init; }
    public int LossCount { get; init; }
    public int NoDifferenceCount { get; init; }
    public int NeedsReviewCount { get; init; }
    public int CommissionNeedsReviewCount { get; init; }
}

public sealed class FxDifferenceReportViewModel
{
    public FxDifferenceReportFilterViewModel Filter { get; init; } = new();
    public IReadOnlyList<FxDifferenceRowViewModel> Rows { get; init; } = [];
    public FxDifferenceTotalsViewModel Totals { get; init; } = new();
    public int CurrentPage { get; init; } = 1;
    public int PageCount { get; init; } = 1;
    public int TotalRowCount { get; init; }
}
