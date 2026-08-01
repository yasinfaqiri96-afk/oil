using System.ComponentModel.DataAnnotations;
using PTGOilSystem.Web.Services.Time;

namespace PTGOilSystem.Web.Models.TruckSettlements;

// «تسویهٔ کرایه موترها»: فقط ViewModelهای صفحهٔ واحد لیست + تسویهٔ گروهیِ کرایه.
// این صفحه فقط کرایه/کسری را با طرف حمل (راننده/شرکت خدماتی/موتر خودی) تسویه می‌کند؛
// هیچ تخلیهٔ موجودی یا فروشی ندارد — تخلیه/فروش مرحلهٔ بعدی و جداست.
// ثبت‌ها از منطق موجود عبور می‌کند: leg با InventoryTransportReceiptService.SettlementOnly،
// dispatch با DispatchFreightExpenseSync. هیچ منطق مالی موازی ساخته نمی‌شود.

public enum TruckSettlementSourceKind
{
    Leg = 1,      // حمل از موجودی (InventoryTransportLeg — موتر یا واگن)
    Dispatch = 2  // ارسال موتر (TruckDispatch)
}

// ردیف نمایشی لیست (فقط‌خواندنی؛ از leg/dispatch پر می‌شود).
public sealed class TruckSettlementRowViewModel
{
    public TruckSettlementSourceKind Kind { get; set; }
    public int SourceId { get; set; }
    public string RowKey => (Kind == TruckSettlementSourceKind.Leg ? "L" : "D") + SourceId;
    public string TypeLabel { get; set; } = "";
    public string VehicleNumber { get; set; } = "";
    public string? DriverName { get; set; }
    public string ProductName { get; set; } = "";
    public string ContractNumber { get; set; } = "";
    public string? SourceName { get; set; }
    public string? DestinationName { get; set; }
    public DateTime Date { get; set; }
    public decimal RemainingQuantityMt { get; set; }
    // طرف کرایهٔ پیش‌فرض برای انتخاب اولیهٔ فرم: "sp:{id}" / "asset:{id}" / "driver:{id}" یا خالی.
    public string? DefaultFreightParty { get; set; }
}

// ورودی تسویهٔ هر ردیف (فرم گروهی؛ فقط ردیف‌های Selected پردازش می‌شوند).
public sealed class TruckSettlementRowInputViewModel
{
    public bool Selected { get; set; }
    public TruckSettlementSourceKind Kind { get; set; }
    public int SourceId { get; set; }

    [Display(Name = "تاریخ")]
    [DataType(DataType.Date)]
    public DateTime OperationDate { get; set; } = AfghanistanBusinessClock.SystemToday;

    // وزن تخلیه (تن): مبنای محاسبهٔ کسری = باقیمانده − وزن تخلیه. بار برای تخلیهٔ بعدی می‌ماند.
    [Display(Name = "وزن تخلیه (تن)")]
    public decimal QuantityMt { get; set; }

    // تسویه
    // نرخ فی تن: کرایه کلی = نرخ × (وزن تخلیه + کسری = بار حمل). اگر کرایه کلی مستقیم وارد شود، همان مبناست.
    [Display(Name = "نرخ کرایه (دالر فی تن)")]
    public decimal? FreightRateUsdPerMt { get; set; }

    [Display(Name = "کرایه کلی (دالر)")]
    public decimal? FreightUsd { get; set; }

    [Display(Name = "کسری (تن)")]
    public decimal ShortageMt { get; set; }

    // حواکت/تلورانس: بخش مجاز کسری که خسارت ندارد. کسری قابل خسارت = کسری − حواکت.
    [Display(Name = "حواکت / مجاز (تن)")]
    public decimal? AllowanceMt { get; set; }

    [Display(Name = "نرخ کسری (دالر فی تن)")]
    public decimal? ShortageRateUsd { get; set; }

    [Display(Name = "کسورات دیگر (دالر)")]
    public decimal? OtherDeductionsUsd { get; set; }

    // "sp:{id}" | "asset:{id}" | "driver:{id}" | "driver:new" | خالی (پیش‌فرض خود حمل/دیسپچ)
    [Display(Name = "طرف کرایه")]
    public string? FreightParty { get; set; }

    [Display(Name = "نام راننده جدید")]
    [StringLength(200)]
    public string? NewDriverName { get; set; }

    [Display(Name = "یادداشت")]
    [StringLength(500)]
    public string? Notes { get; set; }
}

public sealed class TruckSettlementIndexViewModel
{
    public IReadOnlyList<TruckSettlementRowViewModel> Rows { get; set; } = [];
    public List<TruckSettlementRowInputViewModel> Inputs { get; set; } = [];

    // فیلترهای نوار جست‌وجوی مشترک (مثل بقیهٔ تب‌های عملیات): متن آزاد + نوع منبع.
    public string? Query { get; set; }
    public TruckSettlementSourceKind? Kind { get; set; }
}

// گزینهٔ طرف کرایه برای selectها (راننده / شرکت خدماتی / دارایی عملیاتی).
// نوع public لازم است: viewها با dynamic روی نوع anonymous (internal) به RuntimeBinderException می‌خورند.
public sealed record TruckSettlementPartyOption(int Id, string Name);

// منبعی که کرایه/کسری آن در مرحلهٔ قبل نهایی شده و اکنون فقط باید به مخزن رسید شود.
public sealed class GroupUnloadSourceItem
{
    public TruckSettlementSourceKind Kind { get; init; }
    public int SourceId { get; init; }
    public string Key => $"{(int)Kind}:{SourceId}";
    public string TypeLabel { get; init; } = "";
    public string VehicleNumber { get; init; } = "";
    public string? DriverName { get; init; }
    public string ProductName { get; init; } = "";
    public string ContractNumber { get; init; } = "";
    public string Route { get; init; } = "";
    public DateTime OperationDate { get; init; }
    public decimal QuantityMt { get; init; }
    public int ProductId { get; init; }
}

public sealed class GroupUnloadSelectedInput
{
    public bool Selected { get; set; }
    public TruckSettlementSourceKind Kind { get; set; }
    public int SourceId { get; set; }
}

public sealed class GroupUnloadCreateViewModel
{
    [Display(Name = "نوع منبع")]
    [Required(ErrorMessage = "نوع منبع را انتخاب کنید.")]
    public TruckSettlementSourceKind? SourceKind { get; set; } = TruckSettlementSourceKind.Leg;

    [Display(Name = "تاریخ تخلیه")]
    [DataType(DataType.Date)]
    public DateTime ReceiptDate { get; set; } = AfghanistanBusinessClock.SystemToday;

    [Display(Name = "مخزن مقصد")]
    [Range(1, int.MaxValue, ErrorMessage = "مخزن مقصد را انتخاب کنید.")]
    public int DestinationStorageTankId { get; set; }

    [Display(Name = "شماره سند / مرجع")]
    [StringLength(100)]
    public string? DocumentReference { get; set; }

    [Display(Name = "توضیحات")]
    [StringLength(1000)]
    public string? Notes { get; set; }

    [StringLength(1000)]
    public string? ReturnUrl { get; set; }

    public List<GroupUnloadSelectedInput> Items { get; set; } = [];
}
