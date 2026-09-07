using System.ComponentModel.DataAnnotations;

namespace PTGOilSystem.Web.Models.Reports;

/// <summary>
/// نوع بارِ در مسیر. هر نوع از یک منبع عملیاتی موجود خوانده می‌شود و هیچ مقداری
/// دوباره محاسبه نمی‌شود.
/// </summary>
public enum GoodsInTransitKind
{
    /// <summary>بارگیری‌ای که هنوز کامل رسید نخورده — بار در راه از مبدأ.</summary>
    FromOrigin = 0,

    /// <summary>حمل داخلی بین مخزن/ترمینال که هنوز باقیمانده دارد.</summary>
    InternalTransfer = 1,

    /// <summary>موتری که بار برداشته و هنوز تخلیه نشده است.</summary>
    CustomerDelivery = 2
}

/// <summary>مرحلهٔ فعلی بار — فقط برچسب نمایشی از وضعیت موجود رکورد منبع.</summary>
public enum GoodsInTransitStage
{
    Loaded = 0,
    InTransit = 1
}

public sealed class GoodsInTransitFilterViewModel
{
    [Display(Name = "از تاریخ")]
    [DataType(DataType.Date)]
    public DateTime? FromDate { get; set; }

    [Display(Name = "تا تاریخ")]
    [DataType(DataType.Date)]
    public DateTime? ToDate { get; set; }

    [Display(Name = "جنس")]
    public int? ProductId { get; set; }

    [Display(Name = "نوع بار")]
    public GoodsInTransitKind? Kind { get; set; }

    [Display(Name = "فقط تأخیردار")]
    public bool DelayedOnly { get; set; }

    [Display(Name = "وسیله، راننده یا مسیر")]
    [StringLength(200)]
    public string? Search { get; set; }

    public bool HasAny
        => FromDate.HasValue
            || ToDate.HasValue
            || ProductId.HasValue
            || Kind.HasValue
            || DelayedOnly
            || !string.IsNullOrWhiteSpace(Search);
}

public sealed class GoodsInTransitRowViewModel
{
    public GoodsInTransitKind Kind { get; init; }
    public GoodsInTransitStage Stage { get; init; }

    /// <summary>شمارهٔ سند منبع (بارگیری، حمل یا دیسپچ) برای لینک جزئیات.</summary>
    public int SourceId { get; init; }
    public string LinkController { get; init; } = string.Empty;
    public string LinkAction { get; init; } = "Details";

    /// <summary>شمارهٔ قابل‌فهم بار: پلاک، شماره واگن، نام کشتی یا شمارهٔ بارنامه.</summary>
    public string VehicleLabel { get; init; } = "—";
    public string? CarrierName { get; init; }
    public string? DriverName { get; init; }

    public string ProductName { get; init; } = string.Empty;
    public string? ContractNumber { get; init; }
    public string? PartyName { get; init; }

    public string? OriginLabel { get; init; }
    public string? DestinationLabel { get; init; }
    public string? RouteNote { get; init; }

    /// <summary>مقدارِ همین لحظه در مسیر (باقیمانده تا رسید/تخلیه).</summary>
    public decimal QuantityMt { get; init; }

    public DateTime DepartureDate { get; init; }
    public DateTime? ExpectedArrivalDate { get; init; }

    /// <summary>روزهای سپری‌شده از حرکت تا امروز (وقت کابل).</summary>
    public int DaysOnRoad { get; init; }

    /// <summary>تاریخ رسیدنِ تخمینی گذشته ولی بار هنوز نرسیده است.</summary>
    public bool IsDelayed { get; init; }

    public string RouteText => string.Join(
        " ← ",
        new[] { OriginLabel, DestinationLabel }.Where(s => !string.IsNullOrWhiteSpace(s))!);

    /// <summary>متنی که فیلتر جست‌وجو روی آن اجرا می‌شود.</summary>
    public string SearchSource => string.Join(
        ' ',
        new[] { VehicleLabel, CarrierName, DriverName, ProductName, ContractNumber, PartyName, OriginLabel, DestinationLabel, RouteNote }
            .Where(s => !string.IsNullOrWhiteSpace(s))!);
}

public sealed class GoodsInTransitTotalsViewModel
{
    public int RowCount { get; init; }
    public decimal TotalQuantityMt { get; init; }
    public int DelayedCount { get; init; }
    public int MaxDaysOnRoad { get; init; }

    public int FromOriginCount { get; init; }
    public int InternalTransferCount { get; init; }
    public int CustomerDeliveryCount { get; init; }
}

public sealed class GoodsInTransitReportViewModel
{
    public GoodsInTransitFilterViewModel Filter { get; init; } = new();
    public IReadOnlyList<GoodsInTransitRowViewModel> Rows { get; init; } = [];
    public GoodsInTransitTotalsViewModel Totals { get; init; } = new();
    public int CurrentPage { get; init; } = 1;
    public int PageCount { get; init; } = 1;
}
