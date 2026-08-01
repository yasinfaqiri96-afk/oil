using System.ComponentModel.DataAnnotations;

namespace PTGOilSystem.Web.Models.Reports;

/// <summary>
/// وضعیتِ مشتقِ یک سفر کشتی. هیچ ستونی در دیتابیس ندارد و ذخیره نمی‌شود؛ فقط از وضعیت
/// حمل‌های همان محموله (<c>InventoryTransportLeg.Status</c>) و تاریخ رسیدن ساخته می‌شود.
/// </summary>
public enum VesselVoyageStatus
{
    /// <summary>ثبت شده اما هنوز هیچ حملی روی آن انجام نشده و تاریخ رسیدن ندارد.</summary>
    Registered = 0,

    /// <summary>حداقل یک حملِ بارگیری‌شده یا در مسیر دارد.</summary>
    InTransit = 1,

    /// <summary>حملی ندارد ولی تاریخ رسیدن ثبت شده است.</summary>
    Arrived = 2,

    /// <summary>همهٔ حمل‌های فعالِ آن رسید خورده‌اند.</summary>
    Completed = 3,

    /// <summary>همهٔ حمل‌های آن لغو شده‌اند.</summary>
    Cancelled = 4
}

/// <summary>
/// دستهٔ سوختِ یک محصول — فقط برای دو کارتِ «مجموع دیزل» و «مجموع بنزین».
/// در دیتابیس هیچ ستونی این را نگه نمی‌دارد (<c>Product.Category</c> متن آزاد است)،
/// پس از نام محصول استنتاج می‌شود و در هیچ محاسبهٔ مالی/موجودی به کار نمی‌رود.
/// </summary>
public enum VesselVoyageFuelKind
{
    Unknown = 0,
    Diesel = 1,
    Gasoline = 2
}

/// <summary>
/// تشخیص دیزل/بنزین از نام محصول. تنها مصرفش دو کارتِ خلاصهٔ «گزارش کشتی‌ها» است.
/// عمداً یک جای واحد است تا واژه‌نامه در Razor تکرار نشود.
/// </summary>
public static class VesselVoyageFuelClassifier
{
    private static readonly string[] DieselTerms =
        ["diesel", "gasoil", "gas oil", "gas-oil", "دیزل", "گازوئیل", "گازوییل", "تیل"];

    private static readonly string[] GasolineTerms =
        ["gasoline", "petrol", "benzin", "benzene", "بنزین", "پطرول", "پترول"];

    public static VesselVoyageFuelKind Classify(string? productName)
    {
        if (string.IsNullOrWhiteSpace(productName))
        {
            return VesselVoyageFuelKind.Unknown;
        }

        var text = productName.Trim().ToLowerInvariant();

        // دیزل اول بررسی می‌شود: «gasoil» نباید با «gasoline» اشتباه گرفته شود.
        if (DieselTerms.Any(term => text.Contains(term, StringComparison.Ordinal)))
        {
            return VesselVoyageFuelKind.Diesel;
        }

        return GasolineTerms.Any(term => text.Contains(term, StringComparison.Ordinal))
            ? VesselVoyageFuelKind.Gasoline
            : VesselVoyageFuelKind.Unknown;
    }
}

public sealed class VesselVoyageReportFilterViewModel
{
    [Display(Name = "سال مالی")]
    public int? FiscalYearId { get; set; }

    [Display(Name = "از تاریخ")]
    [DataType(DataType.Date)]
    public DateTime? FromDate { get; set; }

    [Display(Name = "تا تاریخ")]
    [DataType(DataType.Date)]
    public DateTime? ToDate { get; set; }

    [Display(Name = "کشتی")]
    public int? VesselId { get; set; }

    [Display(Name = "محصول")]
    public int? ProductId { get; set; }

    [Display(Name = "مشتری")]
    public int? CustomerId { get; set; }

    [Display(Name = "تأمین‌کننده")]
    public int? SupplierId { get; set; }

    [Display(Name = "مقصد")]
    public int? DestinationLocationId { get; set; }

    [Display(Name = "کمپنی ترانسپورتی")]
    public int? ServiceProviderId { get; set; }

    public bool HasAny
        => FiscalYearId.HasValue
            || FromDate.HasValue
            || ToDate.HasValue
            || VesselId.HasValue
            || ProductId.HasValue
            || CustomerId.HasValue
            || SupplierId.HasValue
            || DestinationLocationId.HasValue
            || ServiceProviderId.HasValue;
}

/// <summary>یک تخصیصِ قرارداد/Shipper روی همان سفر — سطر جزئیاتِ قابل بازشدن.</summary>
public sealed class VesselVoyageShipperLineViewModel
{
    public int ContractId { get; init; }
    public string ContractNumber { get; init; } = "-";
    public string? SupplierName { get; init; }
    public string? ProductName { get; init; }
    public string? CompanyName { get; init; }
    public decimal AllocatedQuantityMt { get; init; }
}

/// <summary>یک ردیفِ کرایهٔ ثبت‌شدهٔ همان سفر — سطر جزئیاتِ قابل بازشدن.</summary>
public sealed class VesselVoyageFreightLineViewModel
{
    public int ExpenseId { get; init; }
    public DateTime ExpenseDate { get; init; }
    public string ExpenseTypeName { get; init; } = "-";
    public string? ServiceProviderName { get; init; }
    public decimal AmountUsd { get; init; }
}

public sealed class VesselVoyageRowViewModel
{
    public int RowNumber { get; init; }
    public int ShipmentId { get; init; }
    public DateTime? VoyageDate { get; init; }
    public string ShipmentCode { get; init; } = "";
    public int? VesselId { get; init; }
    public string? VesselName { get; init; }
    public string? ProductText { get; init; }
    public decimal QuantityMt { get; init; }

    /// <summary>
    /// امروز همیشه خالی است: سیستم هیچ فیلدی برای Consignee ندارد. ستون نگه داشته شده
    /// تا ساختار گزارش با فایل مرجع یکی بماند و هیچ مقداری حدس زده نمی‌شود.
    /// </summary>
    public string? ConsigneeText { get; init; }

    public string? LoadingPortName { get; init; }
    public string? DestinationName { get; init; }
    public string? CustomerText { get; init; }
    public string? ShipperText { get; init; }
    public IReadOnlyList<VesselVoyageShipperLineViewModel> ShipperLines { get; init; } = [];

    /// <summary>جمع مقدار تخصیص‌یافته به قراردادها/Shipperها.</summary>
    public decimal AllocatedQuantityMt { get; init; }

    /// <summary>تخصیص منهای مقدار کل سفر. صفر یعنی هم‌خوان.</summary>
    public decimal AllocationDifferenceMt => decimal.Round(AllocatedQuantityMt - QuantityMt, 4, MidpointRounding.AwayFromZero);

    /// <summary>
    /// مغایرت تخصیص. فقط وقتی گزارش می‌شود که سفر اصلاً تخصیص داشته باشد؛ سفرِ بدون
    /// تخصیص «مغایرت» نیست، فقط هنوز تفکیک نشده است.
    /// </summary>
    public bool HasAllocationMismatch
        => ShipperLines.Count > 0 && Math.Abs(AllocationDifferenceMt) > 0.0001m;

    public string? TransportCompanyText { get; init; }
    public string? FreightTypeText { get; init; }
    public decimal FreightTotalUsd { get; init; }

    /// <summary>نرخ مشتق = مبلغ کل کرایهٔ کشتی ÷ مقدار سفر. در دیتابیس ذخیره نمی‌شود.</summary>
    public decimal? FreightRateUsdPerMt
        => QuantityMt > 0m && FreightTotalUsd != 0m
            ? decimal.Round(FreightTotalUsd / QuantityMt, 4, MidpointRounding.AwayFromZero)
            : null;

    public IReadOnlyList<VesselVoyageFreightLineViewModel> FreightLines { get; init; } = [];
    public VesselVoyageStatus Status { get; init; }
    public string? Notes { get; init; }

    public bool HasDetails => ShipperLines.Count > 0 || FreightLines.Count > 0;
}

public sealed class VesselVoyageTotalsViewModel
{
    public int VoyageCount { get; init; }
    public int VesselCount { get; init; }
    public decimal TotalQuantityMt { get; init; }
    public decimal TotalDieselMt { get; init; }
    public decimal TotalGasolineMt { get; init; }
    public decimal TotalFreightUsd { get; init; }
}

public sealed class VesselVoyageReportViewModel
{
    public VesselVoyageReportFilterViewModel Filter { get; init; } = new();
    public IReadOnlyList<VesselVoyageRowViewModel> Rows { get; init; } = [];
    public VesselVoyageTotalsViewModel Totals { get; init; } = new();
    public int CurrentPage { get; init; } = 1;
    public int PageCount { get; init; } = 1;

    /// <summary>تعداد سفرهای همین صفحه که تخصیصشان با مقدار کل نمی‌خواند.</summary>
    public int MismatchCount => Rows.Count(r => r.HasAllocationMismatch);
}
