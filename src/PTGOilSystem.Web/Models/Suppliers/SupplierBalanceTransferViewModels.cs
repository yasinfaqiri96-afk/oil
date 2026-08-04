using System.ComponentModel.DataAnnotations;
using PTGOilSystem.Web.Models.Entities;

namespace PTGOilSystem.Web.Models.Suppliers;

/// <summary>یک ارز از «مانده قابل انتقال» — کارت جزئیات تأمین‌کننده و فورم انتقال از این می‌خوانند.</summary>
public sealed class SupplierTransferableBucketViewModel
{
    public string CurrencyCode { get; init; } = "USD";
    public decimal RemainingOriginalAmount { get; init; }
    public decimal RemainingBookAmountUsd { get; init; }
    public decimal HistoricalPerUsdRate { get; init; } = 1m;

    // true یعنی این نرخ از روی مبالغ بازسازی شده (منبع Legacy بدون نرخ مستقیم) و قطعی نیست.
    public bool HistoricalRateIsEstimated { get; init; }

    public bool IsUsd => string.Equals(CurrencyCode?.Trim(), "USD", StringComparison.OrdinalIgnoreCase);
    public IReadOnlyList<SupplierTransferableSourceViewModel> Sources { get; init; } = [];
}

/// <summary>منبع ایجاد مانده — برای ردیابی نمایش داده می‌شود.</summary>
public sealed class SupplierTransferableSourceViewModel
{
    public string SourceType { get; init; } = "";
    public string SourceName { get; init; } = "";
    public DateTime SourceDate { get; init; }
    public string CurrencyCode { get; init; } = "USD";
    public decimal RemainingOriginalAmount { get; init; }
    public decimal RemainingBookAmountUsd { get; init; }
    public string Description { get; init; } = "";
}

/// <summary>یک سطل مانده قابل انتقال — یک شرکت اثبات‌شده یا سطل «سطح گروه».</summary>
public sealed class SupplierCompanyBalanceViewModel
{
    public int CompanyId { get; init; }
    public string CompanyName { get; init; } = string.Empty;
    public decimal NetAccountBalanceUsd { get; init; }
    public decimal ClaimUsd { get; init; }
    public decimal TransferableTotalUsd { get; init; }
    public decimal ConsumedUsd { get; init; }
    public IReadOnlyList<SupplierTransferableBucketViewModel> Buckets { get; init; } = [];
    public bool HasTransferable => TransferableTotalUsd > 0m && Buckets.Count > 0;

    // سطل سطح گروه: شرکت منبع از سند اثبات نشده. مانده پنهان یا مسدود نمی‌شود؛ شرکتِ هر ردیف
    // انتقال از قرارداد مقصد گرفته می‌شود.
    public bool IsGroupLevel { get; init; }
}

/// <summary>کارت «مانده قابل انتقال» در جزئیات تأمین‌کننده — شرکت‌ها به‌علاوهٔ سطل سطح گروه.</summary>
public sealed class SupplierTransferableBalanceCardViewModel
{
    public int SupplierId { get; init; }
    public IReadOnlyList<SupplierCompanyBalanceViewModel> Companies { get; init; } = [];
    public bool HasTransferable => Companies.Any(c => c.HasTransferable);
    public decimal TransferableTotalUsd => Companies.Sum(c => c.TransferableTotalUsd);
    public int ActiveTransferCount { get; init; }

    // منابعی که شرکتشان اثبات نشده. اینها در سطل سطح گروه قابل انتقال‌اند؛ این دو عدد فقط
    // برای شفافیت نگه داشته می‌شوند.
    public decimal UnknownCompanyOutflowUsd { get; init; }
    public int UnknownCompanyRowCount { get; init; }
    public bool HasUnknownCompany => UnknownCompanyRowCount > 0;

    // مانده‌ای که هنوز هیچ سطلی آن را قابل انتقال نکرده (مثلاً طلب کمتر از منابع). کارت باید
    // در این حالت هم رندر شود تا مانده واقعی از دید کاربر پنهان نماند.
    public bool HasAnyPool => Companies.Count > 0;
}

/// <summary>یک قرارداد مقصد در فورم انتقال.</summary>
public sealed class SupplierBalanceTransferLineViewModel
{
    public int ContractId { get; set; }

    [Display(Name = "مبلغ انتقال")]
    public decimal TransferOriginalAmount { get; set; }

    [Display(Name = "نرخ ارز قرارداد")]
    public decimal ContractCurrencyPerUsdRate { get; set; } = 1m;
}

/// <summary>فورم یک‌صفحه‌ای «انتقال به قرارداد».</summary>
public sealed class SupplierBalanceTransferCreateViewModel
{
    public int SupplierId { get; set; }
    public string SupplierName { get; set; } = string.Empty;

    // شرکت مالک مانده. از Context صفحه می‌آید و فقط مانده و قراردادهای همین شرکت نشان داده می‌شود.
    // صفر یعنی سطل «سطح گروه»: شرکت منبع اثبات نشده، پس قراردادهای همهٔ شرکت‌ها قابل انتخاب‌اند
    // و شرکتِ هر ردیف از قرارداد مقصد همان ردیف گرفته می‌شود.
    public int CompanyId { get; set; }
    public string CompanyName { get; set; } = string.Empty;

    public bool IsGroupLevel => CompanyId == 0;

    [Display(Name = "ارز مانده")]
    [Required(ErrorMessage = "ارز مانده باید انتخاب شود.")]
    public string CurrencyCode { get; set; } = "USD";

    [Display(Name = "تاریخ انتقال")]
    public DateTime TransferDate { get; set; }

    [Display(Name = "نرخ روز")]
    public decimal TransferPerUsdRate { get; set; } = 1m;

    // منشأ نرخ پیش‌فرض («نرخ روز ۲۰۲۶-۰۸-۰۴»). فقط توضیح نمایشی است.
    public string? DayRateSource { get; set; }

    // true یعنی برای تاریخ انتخاب‌شده نرخ روزی ثبت نشده و کاربر باید خودش وارد کند.
    // نرخ تاریخی عمداً جایگزین نمی‌شود.
    public bool DayRateMissing { get; set; }

    [Display(Name = "مرجع")]
    [StringLength(200)]
    public string? ReferenceNumber { get; set; }

    [Display(Name = "یادداشت")]
    [StringLength(1000)]
    public string? Notes { get; set; }

    public List<SupplierBalanceTransferLineViewModel> Lines { get; set; } = [];

    [StringLength(1000)]
    public string? ReturnUrl { get; set; }

    // فقط نمایشی — از موتور مانده پر می‌شود.
    public IReadOnlyList<SupplierTransferableBucketViewModel> Buckets { get; set; } = [];

    public SupplierTransferableBucketViewModel? SelectedBucket => Buckets
        .FirstOrDefault(b => string.Equals(b.CurrencyCode, CurrencyCode, StringComparison.OrdinalIgnoreCase));

    public bool IsUsd => string.Equals(CurrencyCode?.Trim(), "USD", StringComparison.OrdinalIgnoreCase);
}

/// <summary>یک ردیف در «سوابق انتقال».</summary>
public sealed class SupplierBalanceTransferHistoryRowViewModel
{
    public int Id { get; init; }
    public DateTime TransferDate { get; init; }
    public int ContractId { get; init; }
    public string ContractName { get; init; } = string.Empty;
    public string ContractNumber { get; init; } = string.Empty;
    public string ContractDisplayLabel => Contract.BuildDisplayLabel(ContractName, ContractNumber);
    public decimal TransferOriginalAmount { get; init; }
    public string OriginalCurrencyCode { get; init; } = "USD";
    public decimal HistoricalAmountUsd { get; init; }
    public decimal TransferPerUsdRate { get; init; } = 1m;
    public decimal TransferValueUsd { get; init; }
    public decimal ExchangeDifferenceUsd { get; init; }
    public SarrafSettlementDifferenceType ExchangeDifferenceType { get; init; }
    public bool HasExchangeDifference => ExchangeDifferenceType != SarrafSettlementDifferenceType.None;
    public string ExchangeDifferenceName => ExchangeDifferenceType switch
    {
        SarrafSettlementDifferenceType.Gain => "سود نرخ ارز",
        SarrafSettlementDifferenceType.Loss => "زیان نرخ ارز",
        _ => "—"
    };
    public string ContractCurrencyCode { get; init; } = "USD";
    public decimal TransferContractCurrencyAmount { get; init; }
    public string? ReferenceNumber { get; init; }
    public SupplierBalanceTransferStatus Status { get; init; }
    public bool IsActive => Status == SupplierBalanceTransferStatus.Active;
    public string StatusName => IsActive ? "فعال" : "برگشت‌شده";
    public DateTime? ReversedAtUtc { get; init; }
    public string? ReversalReason { get; init; }
    public string? CreatedByUserName { get; init; }
    public IReadOnlyList<SupplierTransferableSourceViewModel> Sources { get; init; } = [];
}

/// <summary>صفحهٔ «سوابق انتقال» یک تأمین‌کننده.</summary>
public sealed class SupplierBalanceTransferHistoryViewModel
{
    public int SupplierId { get; init; }
    public string SupplierName { get; init; } = string.Empty;
    public IReadOnlyList<SupplierBalanceTransferHistoryRowViewModel> Rows { get; init; } = [];
    public decimal ActiveTotalUsd { get; init; }
}
