using System.ComponentModel.DataAnnotations;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services.Time;

namespace PTGOilSystem.Web.Models.InventoryTransport;

public enum TransportStartSourceKind
{
    [Display(Name = "موجودی مخزن")]
    Inventory = 1,
    [Display(Name = "رسید/بارگیری مستقیم")]
    LoadingReceipt = 2,
    [Display(Name = "حمل در جریان")]
    ActiveTransport = 3
}

public sealed class TransportStartViewModel
{
    public TransportStartSourceKind SourceKind { get; set; } = TransportStartSourceKind.Inventory;
    public int? LoadingReceiptId { get; set; }
    public int? TransportLegId { get; set; }
    public IReadOnlyList<TransportLookupItem> LoadingReceipts { get; set; } = [];
    public IReadOnlyList<TransportLookupItem> ActiveTransports { get; set; } = [];
}

public sealed record TransportLookupItem(int Id, string Label, decimal AvailableQuantityMt = 0m);

public sealed class TransportStartFromReceiptViewModel
{
    [Range(1, int.MaxValue)]
    public int LoadingReceiptId { get; set; }
    public string ReceiptLabel { get; set; } = "";
    public string ProductName { get; set; } = "";
    public decimal AvailableQuantityMt { get; set; }

    [Range(typeof(decimal), "0.0001", "79228162514264337593543950335")]
    public decimal QuantityMt { get; set; }

    public LoadingTransportType TransportType { get; set; } = LoadingTransportType.Truck;
    public int? TruckId { get; set; }
    public int? WagonId { get; set; }
    public int? VesselId { get; set; }
    public int? DriverId { get; set; }
    public int? ServiceProviderId { get; set; }

    [DataType(DataType.Date)]
    public DateTime TransportDate { get; set; } = AfghanistanBusinessClock.SystemToday;

    [StringLength(100)]
    public string? Reference { get; set; }

    [StringLength(1000)]
    public string? Notes { get; set; }
}

public sealed class TransportStartFromLoadingViewModel
{
    [Range(1, int.MaxValue)]
    public int LoadingRegisterId { get; set; }
    public string LoadingLabel { get; set; } = "";
    public string ProductName { get; set; } = "";
    public decimal AvailableQuantityMt { get; set; }

    [Range(typeof(decimal), "0.0001", "79228162514264337593543950335")]
    public decimal QuantityMt { get; set; }

    public LoadingTransportType TransportType { get; set; } = LoadingTransportType.Truck;
    public int? TruckId { get; set; }
    public int? WagonId { get; set; }
    public int? VesselId { get; set; }
    public int? DriverId { get; set; }
    public int? ServiceProviderId { get; set; }

    [DataType(DataType.Date)]
    public DateTime TransportDate { get; set; } = AfghanistanBusinessClock.SystemToday;

    [StringLength(100)]
    public string? Reference { get; set; }

    [StringLength(1000)]
    public string? Notes { get; set; }
}

public sealed class TransportContinueViewModel
{
    public List<TransportContinueSourceInput> Sources { get; set; } = [];
    public LoadingTransportType TargetTransportType { get; set; } = LoadingTransportType.Truck;
    public int? TargetTruckId { get; set; }
    public int? TargetWagonId { get; set; }
    public int? TargetVesselId { get; set; }
    public int? DriverId { get; set; }

    [DataType(DataType.Date)]
    public DateTime TransferDate { get; set; } = AfghanistanBusinessClock.SystemToday;

    [StringLength(100)]
    public string? TicketSerialNumber { get; set; }

    [StringLength(1000)]
    public string? Notes { get; set; }
}

public sealed class TransportFreightSettlementViewModel
{
    public int TransportLegId { get; set; }

    [DataType(DataType.Date)]
    public DateTime SettlementDate { get; set; }

    [Range(typeof(decimal), "0", "79228162514264337593543950335")]
    public decimal? FreightRateUsdPerMt { get; set; }

    [Range(typeof(decimal), "0", "79228162514264337593543950335")]
    public decimal? FreightCostUsd { get; set; }

    // رانندهٔ طرفِ کرایه — فقط وقتی حمل شرکت خدماتی/دارایی ملکی ندارد به کار می‌آید.
    public int? DriverId { get; set; }

    [StringLength(500)]
    public string? Notes { get; set; }
}

/// <summary>
/// فهرست حمل‌هایی که از «حمل در جریان» ساخته شده‌اند، برای لغو گروهی.
/// </summary>
public sealed class TransportContinueCancelViewModel
{
    public List<TransportContinueCancelRow> Rows { get; set; } = [];
}

public sealed class TransportContinueCancelRow
{
    public int LegId { get; set; }
    public string Label { get; set; } = "";
    public string ProductName { get; set; } = "";
    public string SourceLabel { get; set; } = "";
    public decimal QuantityMt { get; set; }
    public DateTime TransferDate { get; set; }

    /// <summary>اگر پر باشد، این حمل قابل لغو نیست و علتش همین متن است.</summary>
    public string? BlockReason { get; set; }
}

/// <summary>یک وسیلهٔ مقصدِ قابل انتخاب؛ Key به شکل «نوع:شناسه» است.</summary>
public sealed record TransportTargetVehicleOption(string Key, string Label, string Group);

public sealed class TransportContinueSourceInput
{
    public int LegId { get; set; }
    public bool Selected { get; set; }
    public string Label { get; set; } = "";
    public string ProductName { get; set; } = "";
    public string VehicleNumber { get; set; } = "";
    public decimal RemainingQuantityMt { get; set; }

    [Range(typeof(decimal), "0", "79228162514264337593543950335")]
    public decimal QuantityMt { get; set; }

    /// <summary>
    /// وسیلهٔ مقصدِ همین ردیف به شکل «نوع:شناسه» (مثلاً 3:12 = موتر با شناسهٔ ۱۲).
    /// خالی یعنی وسیلهٔ مقصدِ پیش‌فرضِ فرم استفاده شود.
    /// </summary>
    public string? TargetKey { get; set; }

    /// <summary>
    /// گروه ادغام: ردیف‌هایی که وسیلهٔ مقصدِ یکسان و همین مقدارِ یکسان (و ناخالی) دارند در
    /// یک حملِ مقصد ادغام می‌شوند. خالی یعنی این ردیف حملِ مقصدِ جداگانهٔ خودش را می‌سازد،
    /// حتی اگر نمبر وسیله با ردیف دیگری یکی باشد (سفر دوم/سوم همان موتر).
    /// ردیف‌های بدون وسیلهٔ مقصدِ ردیفی (وسیلهٔ پیش‌فرضِ فرم) مثل قبل با هم ادغام می‌شوند.
    /// </summary>
    [StringLength(30)]
    public string? MergeGroup { get; set; }

    /// <summary>
    /// نمبر وسیلهٔ مقصد وقتی هنوز در داده‌های پایه نیست (از اکسل یا تایپ دستی).
    /// موقع ثبت، موتر با همین نمبر ساخته یا پیدا می‌شود. اگر TargetKey پر باشد نادیده گرفته می‌شود.
    /// </summary>
    [StringLength(50)]
    public string? TargetVehicleNumber { get; set; }
}

/// <summary>
/// تبدیل گروهی چند بارگیری به حمل موجودی؛ هر ردیف دقیقاً همان دادهٔ فرم تک‌بارگیری را دارد
/// تا قواعد سرویس بدون تغییر باقی بماند.
/// </summary>
public sealed class TransportBulkFromLoadingViewModel
{
    public List<TransportBulkFromLoadingRow> Rows { get; set; } = [];

    [DataType(DataType.Date)]
    public DateTime TransportDate { get; set; } = AfghanistanBusinessClock.SystemToday;

    public string? ReturnUrl { get; set; }
}

public sealed class TransportBulkFromLoadingRow
{
    public int LoadingRegisterId { get; set; }
    public bool Selected { get; set; } = true;
    public string LoadingLabel { get; set; } = "";
    public string ProductName { get; set; } = "";
    public decimal AvailableQuantityMt { get; set; }
    public decimal QuantityMt { get; set; }
    public LoadingTransportType TransportType { get; set; } = LoadingTransportType.Truck;
    public int? TruckId { get; set; }
    public int? WagonId { get; set; }
    public int? VesselId { get; set; }
    public int? DriverId { get; set; }
    public int? ServiceProviderId { get; set; }

    [StringLength(100)]
    public string? Reference { get; set; }
}
