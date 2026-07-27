using System.ComponentModel.DataAnnotations;
using PTGOilSystem.Web.Models.Entities;

namespace PTGOilSystem.Web.Models.Sales;

public static class PreSaleOrderStatusLabels
{
    public static string ToPersian(PreSaleOrderStatus status) => status switch
    {
        PreSaleOrderStatus.Draft => "پیش‌نویس",
        PreSaleOrderStatus.Confirmed => "تأییدشده",
        PreSaleOrderStatus.PartiallyDelivered => "تحویل جزئی",
        PreSaleOrderStatus.FullyDelivered => "تحویل کامل",
        PreSaleOrderStatus.Closed => "بسته‌شده",
        PreSaleOrderStatus.Cancelled => "لغوشده",
        _ => status.ToString()
    };

    public static string ToBadgeClass(PreSaleOrderStatus status) => status switch
    {
        PreSaleOrderStatus.FullyDelivered => "is-success",
        PreSaleOrderStatus.PartiallyDelivered => "is-warn",
        PreSaleOrderStatus.Cancelled => "is-danger",
        PreSaleOrderStatus.Closed => "is-muted",
        _ => "is-info"
    };
}

public sealed class PreSaleCreateViewModel
{
    [Display(Name = "مشتری")]
    [Range(1, int.MaxValue, ErrorMessage = "انتخاب مشتری الزامی است.")]
    public int CustomerId { get; set; }

    [Display(Name = "کالا")]
    [Range(1, int.MaxValue, ErrorMessage = "انتخاب کالا الزامی است.")]
    public int ProductId { get; set; }

    [Display(Name = "مقدار کل (تن)")]
    [Range(typeof(decimal), "0.0001", "79228162514264337593543950335", ErrorMessage = "مقدار باید بزرگ‌تر از صفر باشد.")]
    public decimal QuantityMt { get; set; }

    [Display(Name = "ارز")]
    [Required(ErrorMessage = "ارز الزامی است.")]
    [StringLength(10)]
    public string Currency { get; set; } = "USD";

    [Display(Name = "نرخ تبدیل به USD")]
    [Range(typeof(decimal), "0.000001", "79228162514264337593543950335", ErrorMessage = "نرخ تبدیل باید بزرگ‌تر از صفر باشد.")]
    public decimal? AppliedFxRateToUsd { get; set; }

    [Display(Name = "قیمت هر تن")]
    [Range(typeof(decimal), "0.0001", "79228162514264337593543950335", ErrorMessage = "قیمت هر تن باید بزرگ‌تر از صفر باشد.")]
    public decimal UnitPriceInCurrency { get; set; }

    [Display(Name = "تاریخ پیش‌فروش")]
    [DataType(DataType.Date)]
    public DateTime OrderDate { get; set; } = DateTime.UtcNow.Date;

    [Display(Name = "تحویل از تاریخ")]
    [DataType(DataType.Date)]
    public DateTime? ExpectedDeliveryFrom { get; set; }

    [Display(Name = "تحویل تا تاریخ")]
    [DataType(DataType.Date)]
    public DateTime? ExpectedDeliveryTo { get; set; }

    [Display(Name = "شماره مرجع")]
    [StringLength(200)]
    public string? ReferenceNumber { get; set; }

    [Display(Name = "شرایط پرداخت")]
    [StringLength(500)]
    public string? PaymentTerms { get; set; }

    [Display(Name = "توضیحات")]
    [StringLength(1000)]
    public string? Notes { get; set; }

    [StringLength(1000)]
    public string? ReturnUrl { get; set; }
}

public sealed class PreSaleListItemViewModel
{
    public int Id { get; init; }
    public string OrderNumber { get; init; } = "";
    public string CustomerName { get; init; } = "";
    public string ProductName { get; init; } = "";
    public DateTime OrderDate { get; init; }
    public decimal QuantityMt { get; init; }
    public decimal DeliveredMt { get; init; }
    public decimal RemainingMt => decimal.Round(QuantityMt - DeliveredMt, 4, MidpointRounding.AwayFromZero);
    public string Currency { get; init; } = "USD";
    public decimal TotalInCurrency { get; init; }
    public PreSaleOrderStatus Status { get; init; }
}

public sealed class PreSaleIndexViewModel
{
    public IReadOnlyList<PreSaleListItemViewModel> Items { get; init; } = [];
    public int CurrentPage { get; init; } = 1;
    public int PageCount { get; init; } = 1;
    public int TotalCount { get; init; }
    public decimal SumQuantityMt { get; init; }
    public decimal SumRemainingMt { get; init; }
    public int CustomerCount { get; init; }
}

public sealed class PreSaleDeliveryViewModel
{
    public int SalesTransactionId { get; init; }
    public string InvoiceNumber { get; init; } = "";
    public DateTime DeliveryDate { get; init; }
    public decimal QuantityMt { get; init; }
    public decimal TotalInCurrency { get; init; }
    public decimal TotalUsd { get; init; }
    public string SourceLabel { get; init; } = "";
    public string StageLabel { get; init; } = "";
    public bool IsCancelled { get; init; }
}

// یک تخصیصِ فعالِ دریافت مشتری روی این پیش‌فروش.
public sealed class PreSalePaymentViewModel
{
    public int AllocationId { get; init; }
    public int PaymentTransactionId { get; init; }
    public DateTime PaymentDate { get; init; }
    public DateTime AllocationDate { get; init; }
    public decimal AllocatedAmount { get; init; }
    public string Currency { get; init; } = "USD";
    public decimal AllocatedAmountUsd { get; init; }
    public bool IsAdvance { get; init; }
    public string? Reference { get; init; }
}

// دریافتی از همین مشتری که هنوز مانده قابل تخصیص دارد.
public sealed class PreSaleAllocatablePaymentViewModel
{
    public int PaymentTransactionId { get; init; }
    public DateTime PaymentDate { get; init; }
    public decimal Amount { get; init; }
    public decimal AllocatableAmount { get; init; }
    public string Currency { get; init; } = "USD";
    public bool IsAdvance { get; init; }
    public string? Reference { get; init; }
}

public sealed class PreSaleDetailsViewModel
{
    public int Id { get; init; }
    public string OrderNumber { get; init; } = "";
    public int CustomerId { get; init; }
    public string CustomerName { get; init; } = "";
    public string ProductName { get; init; } = "";
    public string? CompanyName { get; init; }
    public DateTime OrderDate { get; init; }
    public DateTime? ExpectedDeliveryFrom { get; init; }
    public DateTime? ExpectedDeliveryTo { get; init; }
    public string Currency { get; init; } = "USD";
    public decimal? AppliedFxRateToUsd { get; init; }
    public decimal UnitPriceInCurrency { get; init; }
    public decimal QuantityMt { get; init; }
    public decimal TotalInCurrency { get; init; }
    public decimal TotalUsd { get; init; }
    public string? ReferenceNumber { get; init; }
    public string? PaymentTerms { get; init; }
    public string? Notes { get; init; }
    public PreSaleOrderStatus Status { get; init; }
    public string? CancelReason { get; init; }

    // مجموع‌ها همیشه از رکوردهای معتبر محاسبه می‌شوند، نه از ستون ذخیره‌شده.
    public decimal DeliveredMt { get; init; }
    public decimal DeliveredValueInCurrency { get; init; }
    // ارزش دلاری تحویل‌ها از خودِ سندهای فروش خوانده می‌شود؛ هیچ نرخ مصنوعی ساخته نمی‌شود.
    public decimal DeliveredValueUsd { get; init; }
    public decimal RemainingMt => decimal.Round(QuantityMt - DeliveredMt, 4, MidpointRounding.AwayFromZero);
    // دریافت‌ها می‌توانند ارز متفاوت داشته باشند، پس مقایسه فقط در USD (ارز پایهٔ سیستم) انجام می‌شود.
    // پیش‌دریافتِ تخصیص‌یافته = مجموع تخصیص‌های فعال (کل پولی که به این تعهد وصل شده).
    public decimal ReceivedUsd { get; init; }
    // پیش‌دریافتِ مصرف‌شده = مجموع Applicationهای فعال (پولی که واقعاً به بابت تحویل‌ها خرج شده).
    public decimal ConsumedAdvanceUsd { get; init; }
    // پیش‌دریافتِ باقی‌مانده برای تحویل‌های آینده = تخصیص‌یافته منهای مصرف‌شده (هرگز منفی نیست).
    public decimal RemainingAdvanceUsd => decimal.Round(
        Math.Max(0m, ReceivedUsd - ConsumedAdvanceUsd), 4, MidpointRounding.AwayFromZero);
    // طلبِ مشتری = ارزشِ تحویل‌های معتبر منهای پیش‌دریافتِ واقعاً مصرف‌شده؛ اگر پیش‌دریافت بیشتر از
    // تحویل باشد، به‌جای «طلبِ منفی» صفر می‌شود و مازاد در «پیش‌دریافتِ باقی‌مانده» دیده می‌شود.
    public decimal OutstandingUsd => decimal.Round(
        Math.Max(0m, DeliveredValueUsd - ConsumedAdvanceUsd), 4, MidpointRounding.AwayFromZero);
    // مانده مالیِ تعهد که هنوز هیچ پرداختی برایش تخصیص نیافته است.
    public decimal UnallocatedOrderUsd { get; init; }

    public bool CanDeliver => Status is PreSaleOrderStatus.Draft
        or PreSaleOrderStatus.Confirmed
        or PreSaleOrderStatus.PartiallyDelivered;

    public IReadOnlyList<PreSaleDeliveryViewModel> Deliveries { get; init; } = [];
    public IReadOnlyList<PreSalePaymentViewModel> Payments { get; init; } = [];
    // دریافت‌های همان مشتری که هنوز مانده قابل تخصیص دارند.
    public IReadOnlyList<PreSaleAllocatablePaymentViewModel> AllocatablePayments { get; init; } = [];
    public IReadOnlyList<GroupSaleSourceItem> Sources { get; init; } = [];
}

// فرم ثبت یک تحویل. منبع دقیقاً همان انتخابگرِ منابع فروش گروهی است تا منطق موجودی موازی نسازد.
public sealed class PreSaleDeliveryCreateViewModel
{
    public int PreSaleOrderId { get; set; }

    [Display(Name = "تاریخ تحویل")]
    [DataType(DataType.Date)]
    public DateTime DeliveryDate { get; set; } = DateTime.UtcNow.Date;

    [Display(Name = "مقدار تحویل (تن)")]
    public decimal? QuantityMt { get; set; }

    [Display(Name = "منبع محصول")]
    public GroupSaleSourceKind Kind { get; set; } = GroupSaleSourceKind.TerminalStock;

    public int Id { get; set; }
    public int TerminalId { get; set; }
    public int StorageTankId { get; set; }
    public int SourcePurchaseContractId { get; set; }

    [Display(Name = "توضیحات")]
    [StringLength(1000)]
    public string? Notes { get; set; }
}
