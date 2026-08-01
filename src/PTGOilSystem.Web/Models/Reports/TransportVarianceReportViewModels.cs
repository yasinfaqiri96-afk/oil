using System.ComponentModel.DataAnnotations;

namespace PTGOilSystem.Web.Models.Reports;

// نوع تفاوت یک ردیفِ حمل. قرارداد علامت با TransportVarianceMath یکی است:
// تفاوت = بارگیری − تخلیه ⇒ مثبت = کسری، منفی = اضافه‌بار.
public enum TransportVarianceKind
{
    Shortage = 0,
    Surplus = 1
}

// فیلتر «نوع تفاوت» در راپور. کسری و اضافه‌بار هرگز با هم جمع نمی‌شوند.
public enum TransportVarianceFilterKind
{
    All = 0,
    ShortageOnly = 1,
    SurplusOnly = 2
}

// مسیرِ ثبتِ تفاوت: تخلیهٔ موتر (دیسپچ) یا رسید انتقال داخلی.
public enum TransportVarianceSource
{
    TruckDispatch = 0,
    InventoryTransfer = 1
}

public sealed class TransportVarianceReportFilterViewModel
{
    [Display(Name = "از تاریخ")]
    [DataType(DataType.Date)]
    public DateTime? FromDate { get; set; }

    [Display(Name = "تا تاریخ")]
    [DataType(DataType.Date)]
    public DateTime? ToDate { get; set; }

    [Display(Name = "جنس")]
    public int? ProductId { get; set; }

    [Display(Name = "قرارداد")]
    public int? ContractId { get; set; }

    [Display(Name = "نوع تفاوت")]
    public TransportVarianceFilterKind Kind { get; set; } = TransportVarianceFilterKind.All;

    [Display(Name = "مسیر ثبت")]
    public TransportVarianceSource? Source { get; set; }

    [Display(Name = "موتر یا راننده")]
    [StringLength(200)]
    public string? Search { get; set; }

    public bool HasAny
        => FromDate.HasValue
            || ToDate.HasValue
            || ProductId.HasValue
            || ContractId.HasValue
            || Kind != TransportVarianceFilterKind.All
            || Source.HasValue
            || !string.IsNullOrWhiteSpace(Search);
}

public sealed class TransportVarianceRowViewModel
{
    public int LossEventId { get; init; }
    public DateTime EventDate { get; init; }
    public TransportVarianceSource Source { get; init; }
    public TransportVarianceKind Kind { get; init; }
    public string ProductName { get; init; } = string.Empty;
    public string? ContractNumber { get; init; }
    public string? VehicleNumber { get; init; }
    public string? DriverName { get; init; }
    public string? RouteText { get; init; }

    /// <summary>مقدار بارگیری (مبنای تفاوت).</summary>
    public decimal LoadedQuantityMt { get; init; }

    /// <summary>مقدار تخلیهٔ واقعی.</summary>
    public decimal UnloadedQuantityMt { get; init; }

    /// <summary>تفاوت علامت‌دار: مثبت = کسری، منفی = اضافه‌بار.</summary>
    public decimal DifferenceQuantityMt { get; init; }

    /// <summary>مقدار مطلقِ تفاوت برای نمایش در ستون نوع.</summary>
    public decimal AbsoluteDifferenceMt => Math.Abs(DifferenceQuantityMt);

    /// <summary>فیصدی تفاوت نسبت به بارگیری (علامت‌دار).</summary>
    public decimal DifferencePercent { get; init; }

    /// <summary>فقط برای کسری مقدار دارد؛ اضافه‌بار همیشه صفر است.</summary>
    public decimal ChargeableShortageMt { get; init; }

    public decimal ToleranceQuantityMt { get; init; }
    public int? TruckDispatchId { get; init; }
    public int? TransportLegId { get; init; }
    public string? Notes { get; init; }

    // ── سود و زیانِ اضافه‌بار (فقط برای ردیف‌های اضافه‌بار؛ در ردیف کسری همه صفر است) ──
    // همهٔ این اعداد مشتق‌شده‌اند: هیچ رکورد مالی تازه‌ای پشت آن‌ها ساخته نمی‌شود، پس
    // عاید و سود دوباره حساب نمی‌شوند. عایدِ فروش از قبل در راپور سود و زیان هست؛
    // این ستون‌ها فقط نشان می‌دهند چه مقدارش از دلِ اضافه‌بار آمده است.

    /// <summary>مقدار اضافه‌بارِ ثبت‌شده (همیشه مثبت).</summary>
    public decimal SurplusRecordedMt { get; init; }

    /// <summary>مقدار اضافه‌بارِ فروخته‌شده به مشتری.</summary>
    public decimal SurplusSoldMt { get; init; }

    /// <summary>اضافه‌بارِ فروخته‌نشده — فقط در موجودی می‌ماند و در سود حساب نمی‌شود.</summary>
    public decimal SurplusUnsoldMt => decimal.Round(
        Math.Max(SurplusRecordedMt - SurplusSoldMt, 0m), 4, MidpointRounding.AwayFromZero);

    /// <summary>عاید حاصل از اضافه‌بارِ فروخته‌شده.</summary>
    public decimal SurplusRevenueUsd { get; init; }

    /// <summary>سهمِ اضافه‌بار از مصارف واقعیِ همان وسیله (کرایه، کمیسیون، سایر).</summary>
    public decimal SurplusAllocatableExpenseUsd { get; init; }

    /// <summary>منفعت خالص اضافه‌بار = عاید منهای مصارف قابل تخصیص (قیمت خرید ندارد).</summary>
    public decimal SurplusNetProfitUsd { get; init; }

    public int? SurplusSaleId { get; init; }
    public string? SurplusSaleInvoiceNumber { get; init; }
}

public sealed class TransportVarianceTotalsViewModel
{
    public int RowCount { get; init; }
    public int ShortageCount { get; init; }
    public int SurplusCount { get; init; }

    /// <summary>مجموع کسری (فقط ردیف‌های کسری، همیشه مثبت).</summary>
    public decimal TotalShortageMt { get; init; }

    /// <summary>مجموع اضافه‌بار (فقط ردیف‌های اضافه‌بار، به‌صورت عدد مثبت).</summary>
    public decimal TotalSurplusMt { get; init; }

    /// <summary>کسری قابل جریمه — اضافه‌بار هیچ سهمی در آن ندارد.</summary>
    public decimal TotalChargeableShortageMt { get; init; }

    /// <summary>تفاوت خالص = مجموع کسری منهای مجموع اضافه‌بار (فقط برای نمایش، جمع‌های بالا را خنثی نمی‌کند).</summary>
    public decimal NetDifferenceMt => TotalShortageMt - TotalSurplusMt;

    public decimal TotalLoadedMt { get; init; }
    public decimal TotalUnloadedMt { get; init; }

    // ── سود و زیانِ اضافه‌بار روی کل نتایجِ فیلترشده ──

    /// <summary>مقدار اضافه‌بارِ فروخته‌شده.</summary>
    public decimal TotalSurplusSoldMt { get; init; }

    /// <summary>اضافه‌بارِ فروخته‌نشده = ثبت‌شده منهای فروخته‌شده (فقط در موجودی).</summary>
    public decimal TotalSurplusUnsoldMt => decimal.Round(
        Math.Max(TotalSurplusMt - TotalSurplusSoldMt, 0m), 4, MidpointRounding.AwayFromZero);

    /// <summary>عاید حاصل از اضافه‌بار.</summary>
    public decimal TotalSurplusRevenueUsd { get; init; }

    /// <summary>مصارف قابل تخصیصِ اضافه‌بار.</summary>
    public decimal TotalSurplusExpenseUsd { get; init; }

    /// <summary>منفعت خالص اضافه‌بار.</summary>
    public decimal TotalSurplusNetProfitUsd => decimal.Round(
        TotalSurplusRevenueUsd - TotalSurplusExpenseUsd, 4, MidpointRounding.AwayFromZero);
}

public sealed class TransportVarianceReportViewModel
{
    public TransportVarianceReportFilterViewModel Filter { get; init; } = new();
    public IReadOnlyList<TransportVarianceRowViewModel> Rows { get; init; } = [];
    public TransportVarianceTotalsViewModel Totals { get; init; } = new();
    public int CurrentPage { get; init; } = 1;
    public int PageCount { get; init; } = 1;
}
