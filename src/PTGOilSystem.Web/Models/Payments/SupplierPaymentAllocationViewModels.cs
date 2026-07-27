using System.ComponentModel.DataAnnotations;
using PTGOilSystem.Web.Models.Entities;

namespace PTGOilSystem.Web.Models.Payments;

// یک ردیف تخصیص پیش‌پرداخت به قرارداد (نمایش در جزئیات پرداخت و صورت‌حساب تأمین‌کننده).
public sealed class SupplierPaymentAllocationListItemViewModel
{
    public int Id { get; init; }
    public DateTime AllocationDate { get; init; }
    public int ContractId { get; init; }
    public string ContractNumber { get; init; } = string.Empty;
    public decimal AllocatedPaymentAmount { get; init; }
    public string PaymentCurrencyCode { get; init; } = "USD";
    public decimal AllocatedBookAmountUsd { get; init; }
    public decimal PaymentCurrencyPerUsdRateAtAllocation { get; init; } = 1m;
    public decimal AllocatedValueUsdAtAllocation { get; init; }
    public decimal ExchangeDifferenceUsd { get; init; }
    public SarrafSettlementDifferenceType ExchangeDifferenceType { get; init; }
    public bool HasExchangeDifference => ExchangeDifferenceType != SarrafSettlementDifferenceType.None;
    public string ExchangeDifferenceName => ExchangeDifferenceType switch
    {
        SarrafSettlementDifferenceType.Gain => "سود تسعیر",
        SarrafSettlementDifferenceType.Loss => "زیان تسعیر",
        _ => "—"
    };
    public string ContractCurrencyCode { get; init; } = "USD";
    public decimal ContractCurrencyPerUsdRate { get; init; }
    public decimal AllocatedContractCurrencyAmount { get; init; }
    public string? ReferenceNumber { get; init; }
    public SupplierPaymentAllocationStatus Status { get; init; }
    public bool IsActive => Status == SupplierPaymentAllocationStatus.Active;
    public string StatusName => IsActive ? "فعال" : "برگشت‌شده";
    public DateTime? ReversedAtUtc { get; init; }
    public string? ReversalReason { get; init; }
    public string? CreatedByUserName { get; init; }
}

// فورم «مصرف پیش‌پرداخت برای قرارداد».
public sealed class SupplierPaymentAllocationCreateViewModel
{
    public int PaymentTransactionId { get; set; }

    // فقط نمایشی
    public int SupplierId { get; set; }
    public string SupplierName { get; set; } = string.Empty;
    public string PaymentReference { get; set; } = string.Empty;
    public DateTime PaymentDate { get; set; }
    public decimal PaymentAmount { get; set; }
    public string PaymentCurrencyCode { get; set; } = "USD";
    public decimal PaymentAmountUsd { get; set; }
    public decimal AllocatableBalanceUsd { get; set; }

    // مانده واقعی به ارز پرداخت (مثلاً RUB) — سقف مصرف همین است، نه معادل دلاری.
    public decimal AllocatableBalanceAmount { get; set; }

    // نرخ روز پرداخت به شکل «۱ دلار = ؟ ارز پرداخت» — فقط نمایشی، برای مقایسه با نرخ روز تخصیص.
    public decimal PaymentCurrencyPerUsdRateAtPayment { get; set; } = 1m;

    public bool IsPaymentCurrencyUsd => string.Equals(PaymentCurrencyCode?.Trim(), "USD", StringComparison.OrdinalIgnoreCase);

    [Display(Name = "قرارداد خرید")]
    [Range(1, int.MaxValue, ErrorMessage = "انتخاب قرارداد خرید الزامی است.")]
    public int ContractId { get; set; }

    [Display(Name = "مبلغ مصرف‌شده")]
    [Range(typeof(decimal), "0.0001", "79228162514264337593543950335", ErrorMessage = "مبلغ مصرف‌شده باید بزرگ‌تر از صفر باشد.")]
    public decimal AllocatedPaymentAmount { get; set; }

    [Display(Name = "نرخ تبدیل (هر ۱ دلار = ؟ ارز قرارداد)")]
    [Range(typeof(decimal), "0.0001", "79228162514264337593543950335", ErrorMessage = "نرخ تبدیل باید بزرگ‌تر از صفر باشد.")]
    public decimal ContractCurrencyPerUsdRate { get; set; } = 1m;

    // نرخ روز تخصیص برای ارز پرداخت: «۱ دلار = ؟ ارز پرداخت» (مثلاً ۱۰۰ برای RUB).
    // برای پرداخت دالری استفاده نمی‌شود و سرویس آن را ۱ می‌گیرد.
    [Display(Name = "نرخ روز تخصیص (هر ۱ دلار = ؟ ارز پرداخت)")]
    [Range(typeof(decimal), "0.0001", "79228162514264337593543950335", ErrorMessage = "نرخ روز تخصیص باید بزرگ‌تر از صفر باشد.")]
    public decimal PaymentCurrencyPerUsdRateAtAllocation { get; set; } = 1m;

    // ارز قرارداد — فقط نمایشی، بر اساس قرارداد انتخاب‌شده
    public string ContractCurrencyCode { get; set; } = "USD";

    [Display(Name = "تاریخ تخصیص")]
    public DateTime AllocationDate { get; set; }

    [Display(Name = "شماره مرجع")]
    [StringLength(200)]
    public string? ReferenceNumber { get; set; }

    [Display(Name = "توضیحات")]
    [StringLength(1000)]
    public string? Notes { get; set; }

    [StringLength(1000)]
    public string? ReturnUrl { get; set; }
}
