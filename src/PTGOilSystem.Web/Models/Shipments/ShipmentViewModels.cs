using System.ComponentModel.DataAnnotations;

namespace PTGOilSystem.Web.Models.Shipments;

public sealed class ShipmentCreateViewModel
{
    // در حالت ویرایش پر می‌شود؛ در حالت ثبت صفر است.
    public int Id { get; set; }

    // اگر false باشد، فقط فیلدهای هدر قابل ویرایش‌اند (به‌دلیل فعالیت پایین‌دستی روی محموله).
    public bool CanEditAllocations { get; set; } = true;

    [Display(Name = "کد محموله / نام کشتی")]
    [Required]
    [StringLength(100)]
    public string ShipmentCode { get; set; } = "";

    [Display(Name = "کشتی")]
    public int? VesselId { get; set; }

    [Display(Name = "قرارداد اصلی")]
    public int? PrimaryContractId { get; set; }

    [Display(Name = "تاریخ حرکت")]
    [DataType(DataType.Date)]
    public DateTime? DepartureDate { get; set; }

    [Display(Name = "تاریخ رسیدن")]
    [DataType(DataType.Date)]
    public DateTime? ArrivalDate { get; set; }

    [Display(Name = "مبدا")]
    public int? OriginLocationId { get; set; }

    [Display(Name = "مقصد")]
    public int? DestinationLocationId { get; set; }

    [Display(Name = "مقدار کل MT")]
    [Range(typeof(decimal), "0.0001", "79228162514264337593543950335", ErrorMessage = "مقدار محموله باید بزرگ‌تر از صفر باشد.")]
    public decimal QuantityMt { get; set; }

    [Display(Name = "یادداشت")]
    [StringLength(1000)]
    public string? Notes { get; set; }

    [StringLength(500)]
    public string? ReturnUrl { get; set; }

    public List<ShipmentContractAllocationInput> ContractAllocations { get; set; } = [];

    // تخصیص اختیاری از موجودی مخزن: کاربر هنگام ثبت محموله می‌تواند مقدار هر قرارداد را
    // از مخزن مربوطه بردارد. این ردیف‌ها سمت کلاینت (پس از انتخاب قرارداد) ساخته می‌شوند؛
    // مقدار قابل‌اعتماد سمت سرور دوباره بازخوانده و اعتبارسنجی می‌شود.
    public List<ShipmentTankPickInput> TankPicks { get; set; } = [];
}

// یک انتخابِ مقدار از یک مخزن برای یک قرارداد، هنگام ثبت محموله.
public sealed class ShipmentTankPickInput
{
    public int ContractId { get; set; }
    public int StorageTankId { get; set; }

    [Display(Name = "مقدار از مخزن")]
    public decimal? QuantityMt { get; set; }

    public bool HasQuantity => QuantityMt.GetValueOrDefault() > 0m;
}

// خروجی endpoint کمکیِ «مخازن دارای موجودی یک قرارداد» (برای ساخت کارت مخزن سمت کلاینت).
public sealed class ShipmentTankAvailabilityViewModel
{
    public int ContractId { get; set; }
    public int ProductId { get; set; }
    public string ContractNumber { get; set; } = "";
    public string? ProductName { get; set; }
    public decimal RemainingQuantityMt { get; set; }
    public decimal TotalAvailableQuantityMt { get; set; }
    public IReadOnlyList<ShipmentTankAvailabilityRow> Tanks { get; set; } = [];
}

public sealed class ShipmentTankAvailabilityRow
{
    public int StorageTankId { get; set; }
    public string TankCode { get; set; } = "";
    public int TerminalId { get; set; }
    public string TerminalName { get; set; } = "";
    public decimal AvailableQuantityMt { get; set; }
}

public sealed class ShipmentContractAllocationInput
{
    [Display(Name = "قرارداد خرید")]
    public int? ContractId { get; set; }

    [Display(Name = "مخزن")]
    public int? StorageTankId { get; set; }

    [Display(Name = "مقدار MT")]
    public decimal? QuantityMt { get; set; }

    [Display(Name = "یادداشت")]
    [StringLength(500)]
    public string? Notes { get; set; }

    // سهم دقیق بارگیری‌های همین قرارداد در این محموله (اختیاری).
    // اگر پر شود، جمع آن باید با QuantityMt همین ردیف برابر باشد و بهای خرید از نرخ قطعی
    // همان بارگیری‌ها ساخته می‌شود. اگر خالی بماند، مسیر قدیمی (نرخ سطح قرارداد) اجرا می‌شود.
    public List<ShipmentLoadingAllocationInput> LoadingAllocations { get; set; } = [];

    public IEnumerable<ShipmentLoadingAllocationInput> EffectiveLoadingAllocations
        => LoadingAllocations.Where(l => l.HasQuantity);

    public bool HasAnyValue =>
        ContractId.GetValueOrDefault() > 0
        || StorageTankId.GetValueOrDefault() > 0
        || QuantityMt.HasValue
        || !string.IsNullOrWhiteSpace(Notes)
        || LoadingAllocations.Any(l => l.HasQuantity);
}

// یک سهم از یک بارگیریِ مشخص برای این محموله.
public sealed class ShipmentLoadingAllocationInput
{
    public int LoadingRegisterId { get; set; }

    [Display(Name = "مقدار از بارگیری")]
    public decimal? QuantityMt { get; set; }

    public bool HasQuantity => QuantityMt.GetValueOrDefault() > 0m;
}

// خروجی endpoint «بارگیری‌های قابل تخصیص یک قرارداد» — کاربر فقط مقدار را وارد می‌کند؛
// نرخ خرید هرگز دستی گرفته نمی‌شود و همیشه از همین ردیف‌ها می‌آید.
public sealed class ShipmentLoadingAvailabilityViewModel
{
    public int ContractId { get; set; }
    public string ContractNumber { get; set; } = "";
    public decimal TotalRemainingQuantityMt { get; set; }
    public IReadOnlyList<ShipmentLoadingAvailabilityRow> Loadings { get; set; } = [];
}

public sealed class ShipmentLoadingAvailabilityRow
{
    public int LoadingRegisterId { get; set; }
    public string Label { get; set; } = "";
    public DateTime LoadingDate { get; set; }
    public decimal LoadedQuantityMt { get; set; }
    public decimal AllocatedQuantityMt { get; set; }
    public decimal RemainingQuantityMt { get; set; }
    // نرخ قطعی همین بارگیری؛ null یعنی هنوز ثبت نشده و بهای خرید به هدر قرارداد fallback می‌کند.
    public decimal? LoadingPriceUsd { get; set; }
    // مقدار تخصیص‌یافتهٔ همین محموله (در حالت ویرایش) تا فرم بتواند مقدار قبلی را نشان دهد.
    public decimal CurrentShipmentQuantityMt { get; set; }
}
