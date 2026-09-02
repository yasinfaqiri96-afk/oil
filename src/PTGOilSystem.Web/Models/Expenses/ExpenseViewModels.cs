using System.ComponentModel.DataAnnotations;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services.Time;

namespace PTGOilSystem.Web.Models.Expenses;

public sealed class ExpenseCreateViewModel
{
    /// <summary>
    /// PTG-P1-05 — نسخهٔ سطری که کاربر هنگامِ بازکردنِ فرم دید. با فرم برمی‌گردد تا ذخیره
    /// روی نسخهٔ کهنه رد شود. صفر یعنی فرم نسخه نفرستاده است.
    /// </summary>
    public long Version { get; set; }

    public int Id { get; set; }

    [Display(Name = "نوع مصرف")]
    public int? ExpenseTypeId { get; set; }

    [Display(Name = "نوع مصرف دستی")]
    [StringLength(200, ErrorMessage = "نام نوع مصرف دستی نمی‌تواند بیشتر از ۲۰۰ حرف باشد.")]
    public string? ManualExpenseTypeName { get; set; }

    [Display(Name = "قرارداد")]
    public int? ContractId { get; set; }

    [Display(Name = "Shipment")]
    public int? ShipmentId { get; set; }

    [Display(Name = "دیسپچ موتر باربری")]
    public int? TruckDispatchId { get; set; }

    [Display(Name = "Transport Leg")]
    public int? TransportLegId { get; set; }

    [Display(Name = "Service Provider")]
    public int? ServiceProviderId { get; set; }

    [Display(Name = "Operational Asset")]
    public int? OperationalAssetId { get; set; }

    [Display(Name = "تاریخ مصرف")]
    [DataType(DataType.Date)]
    public DateTime ExpenseDate { get; set; } = AfghanistanBusinessClock.SystemToday;

    [Display(Name = "مبلغ")]
    [Range(typeof(decimal), "0.0001", "79228162514264337593543950335", ErrorMessage = "مبلغ مصرف باید بزرگ‌تر از صفر باشد.")]
    public decimal Amount { get; set; }

    public decimal AmountUsd
    {
        get => Amount;
        set => Amount = value;
    }

    [Display(Name = "ارز")]
    [Required(ErrorMessage = "ارز الزامی است.")]
    [StringLength(10)]
    public string Currency { get; set; } = "USD";

    [Display(Name = "نرخ تبدیل به USD")]
    [Range(typeof(decimal), "0.000001", "79228162514264337593543950335", ErrorMessage = "نرخ تبدیل باید بزرگ‌تر از صفر باشد.")]
    public decimal? AppliedFxRateToUsd { get; set; }

    [Display(Name = "شرح / مرجع")]
    [Required(ErrorMessage = "ثبت شرح یا مرجع برای trace مصرف الزامی است.")]
    [StringLength(1000)]
    public string Description { get; set; } = string.Empty;

    // Phase 1 — مسئول این مصرف/کرایه (فقط ثبت/نمایش، nullable).
    [Display(Name = "مصرف بدوش کیست")]
    public PTGOilSystem.Web.Models.Entities.CostResponsibility? CostResponsibility { get; set; }

    [StringLength(1000)]
    public string? ReturnUrl { get; set; }
}

public sealed class WagonRentCreateViewModel
{
    [Display(Name = "نوع مصرف")]
    public int? ExpenseTypeId { get; set; }

    [Display(Name = "نوع مصرف دستی")]
    [StringLength(200, ErrorMessage = "نام نوع مصرف دستی نمی‌تواند بیشتر از ۲۰۰ حرف باشد.")]
    public string? ManualExpenseTypeName { get; set; } = "Wagon Rent";

    [Display(Name = "قرارداد")]
    [Range(1, int.MaxValue, ErrorMessage = "انتخاب قرارداد الزامی است.")]
    public int ContractId { get; set; }

    [Display(Name = "تاریخ مصرف")]
    [DataType(DataType.Date)]
    public DateTime ExpenseDate { get; set; } = AfghanistanBusinessClock.SystemToday;

    [Display(Name = "M-Tone")]
    [Range(typeof(decimal), "0.0001", "79228162514264337593543950335", ErrorMessage = "M-Tone باید بزرگ‌تر از صفر باشد.")]
    public decimal QuantityMt { get; set; }

    [Display(Name = "Unit Price")]
    [Range(typeof(decimal), "0.0001", "79228162514264337593543950335", ErrorMessage = "Unit Price باید بزرگ‌تر از صفر باشد.")]
    public decimal UnitPriceOriginal { get; set; }

    [Display(Name = "Rent Amount")]
    public decimal AmountOriginal { get; set; }

    [Display(Name = "ارز")]
    [Required(ErrorMessage = "ارز الزامی است.")]
    [StringLength(10)]
    public string Currency { get; set; } = "USD";

    [Display(Name = "نرخ سند (ارز در برابر ۱ USD)")]
    [Range(typeof(decimal), "0.000001", "79228162514264337593543950335", ErrorMessage = "نرخ سند باید بزرگ‌تر از صفر باشد.")]
    public decimal? DocumentCurrencyPerUsdRate { get; set; }

    [Display(Name = "نرخ تبدیل به USD")]
    [Range(typeof(decimal), "0.000001", "79228162514264337593543950335", ErrorMessage = "نرخ تبدیل باید بزرگ‌تر از صفر باشد.")]
    public decimal? AppliedFxRateToUsd { get; set; }

    [Display(Name = "Reference")]
    [StringLength(200)]
    public string? Reference { get; set; }

    [Display(Name = "یادداشت")]
    [StringLength(800)]
    public string? Notes { get; set; }

    // Phase 1 — مسئول کرایه واگن (فقط ثبت/نمایش، nullable).
    [Display(Name = "کرایه بدوش کیست")]
    public PTGOilSystem.Web.Models.Entities.CostResponsibility? CostResponsibility { get; set; }

    [StringLength(1000)]
    public string? ReturnUrl { get; set; }
}

public sealed class CustomsBatchRowInput
{
    public int ExpenseTypeId { get; set; }
    public string ExpenseTypeName { get; set; } = string.Empty;
    [Range(typeof(decimal), "0", "79228162514264337593543950335")]
    public decimal? Amount { get; set; }
}

public sealed class CustomsBatchViewModel
{
    [Display(Name = "قرارداد")]
    [Range(1, int.MaxValue, ErrorMessage = "انتخاب قرارداد الزامی است.")]
    public int ContractId { get; set; }

    [Display(Name = "دیسپچ / واگن")]
    public int? TruckDispatchId { get; set; }

    [Display(Name = "تاریخ مصرف")]
    [DataType(DataType.Date)]
    public DateTime ExpenseDate { get; set; } = AfghanistanBusinessClock.SystemToday;

    [Display(Name = "ارز")]
    [Required(ErrorMessage = "ارز الزامی است.")]
    [StringLength(10)]
    public string Currency { get; set; } = "AFN";

    [Display(Name = "نرخ تبدیل به USD")]
    [Range(typeof(decimal), "0.000001", "79228162514264337593543950335", ErrorMessage = "نرخ تبدیل باید بزرگ‌تر از صفر باشد.")]
    public decimal? AppliedFxRateToUsd { get; set; }

    [Display(Name = "شرح / مرجع")]
    [StringLength(500)]
    public string? Description { get; set; }

    // Phase 1 — مسئول مصارف گمرکی (فقط ثبت/نمایش، روی همهٔ سطرها اعمال می‌شود).
    [Display(Name = "این مصارف بدوش کیست")]
    public PTGOilSystem.Web.Models.Entities.CostResponsibility? CostResponsibility { get; set; }

    public List<CustomsBatchRowInput> Rows { get; set; } = [];

    [StringLength(1000)]
    public string? ReturnUrl { get; set; }
}

public sealed class ExpenseIndexFilterViewModel
{
    [Display(Name = "نوع مصرف")]
    public int? ExpenseTypeId { get; set; }

    [Display(Name = "قرارداد")]
    public int? ContractId { get; set; }

    [Display(Name = "Shipment")]
    public int? ShipmentId { get; set; }

    [Display(Name = "دیسپچ موتر باربری")]
    public int? TruckDispatchId { get; set; }

    [Display(Name = "Transport Leg")]
    public int? TransportLegId { get; set; }

    [Display(Name = "Service Provider")]
    public int? ServiceProviderId { get; set; }

    [Display(Name = "Operational Asset")]
    public int? OperationalAssetId { get; set; }

    [Display(Name = "جست‌وجو در شرح / مرجع")]
    [StringLength(1000)]
    public string? Query { get; set; }

    [Display(Name = "از تاریخ")]
    [DataType(DataType.Date)]
    public DateTime? FromDate { get; set; }

    [Display(Name = "تا تاریخ")]
    [DataType(DataType.Date)]
    public DateTime? ToDate { get; set; }
}

public sealed class ExpenseListItemViewModel
{
    public int Id { get; init; }
    public DateTime ExpenseDate { get; init; }
    public string ExpenseTypeName { get; init; } = string.Empty;
    public string? ContractName { get; init; }
    public string? ContractNumber { get; init; }
    public string ContractDisplayLabel => Contract.BuildDisplayLabel(ContractName, ContractNumber);
    public string? ShipmentCode { get; init; }
    public string? TruckDispatchLabel { get; init; }
    public string? TransportLegLabel { get; init; }
    /// <summary>نمبر وسیله: پلاک موتر ارسال یا شمارهٔ واگن سفر حمل مربوط به این مصرف.</summary>
    public string? VehicleNumber { get; init; }
    public string? ServiceProviderName { get; init; }
    public string? OperationalAssetName { get; init; }
    public decimal Amount { get; init; }
    public string Currency { get; init; } = "USD";
    public decimal? AppliedFxRateToUsd { get; init; }
    public decimal AmountUsd { get; init; }
    public string Description { get; init; } = string.Empty;
}

public sealed class ExpenseIndexViewModel
{
    public ExpenseIndexFilterViewModel Filter { get; init; } = new();
    public IReadOnlyList<ExpenseListItemViewModel> Items { get; init; } = [];
    public int CurrentPage { get; init; } = 1;
    public int PageCount { get; init; } = 1;
    public int TotalCount { get; init; }
}

public sealed class ExpenseDetailsViewModel
{
    public int Id { get; init; }
    public DateTime ExpenseDate { get; init; }
    public string ExpenseTypeName { get; init; } = string.Empty;
    public int? ContractId { get; init; }
    public string? ContractNumber { get; init; }
    public int? ShipmentId { get; init; }
    public string? ShipmentCode { get; init; }
    public string? TruckDispatchLabel { get; init; }
    public string? TransportLegLabel { get; init; }
    public int? ServiceProviderId { get; init; }
    public string? ServiceProviderName { get; init; }
    public int? OperationalAssetId { get; init; }
    public string? OperationalAssetName { get; init; }
    public decimal Amount { get; init; }
    public string Currency { get; init; } = "USD";
    public decimal? AppliedFxRateToUsd { get; init; }
    public decimal AmountUsd { get; init; }
    public string? Description { get; init; }
    public int? LedgerEntryId { get; init; }
    public string? LedgerReference { get; init; }
    public string? LedgerDescription { get; init; }
    public decimal? LedgerAmountUsd { get; init; }
    public string? LedgerSideName { get; init; }
    // مصرف گروهی — اگر این مصرف سهمِ یک ثبت گروهی باشد.
    public int? ExpenseBatchId { get; init; }
    public string? ExpenseBatchNumber { get; init; }
}
