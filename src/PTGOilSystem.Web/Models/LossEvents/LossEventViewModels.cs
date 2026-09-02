using System.ComponentModel.DataAnnotations;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services.Time;

namespace PTGOilSystem.Web.Models.LossEvents;

public static class LossEventStageLabels
{
    public static string ToPersian(LossEventStage stage) => stage switch
    {
        LossEventStage.LoadingDifference => "اختلاف بارگیری",
        LossEventStage.TransitLoss => "ضایعات در مسیر",
        LossEventStage.ReceiptShortage => "کمبود هنگام رسید",
        LossEventStage.TankNaturalLoss => "کاهش طبیعی مخزن",
        LossEventStage.DispatchShortage => "کمبود دیسپچ",
        LossEventStage.CustomsLoss => "ضایعات گمرکی",
        LossEventStage.SalesDifference => "اختلاف فروش",
        LossEventStage.ManualAdjustment => "اصلاح دستی",
        LossEventStage.TankFinalSettlement => "تسویه نهایی مخزن",
        _ => stage.ToString()
    };
}

/// <summary>
/// چهار حالتی که کاربر در فرم دستی می‌بیند. مقادیر Enum دیتابیس دست‌نخورده می‌مانند؛ فقط
/// عنوان‌ها ساده شده‌اند و مرحلهٔ دقیق از صفحه‌ای که کاربر از آن آمده تعیین می‌شود
/// (بارگیری → اختلاف بارگیری، ارسال موتر → کمبود دیسپچ، رسید/حمل → کمبود هنگام رسید).
/// اگر رویداد مرحله‌ای خارج از این چهار مورد داشته باشد، همان مرحله با نام اصلی‌اش
/// به فهرست اضافه می‌شود تا داده‌های قدیمی و مسیرهای موجود نشکنند.
/// </summary>
public static class LossEventStageChoices
{
    public const string ShipmentOrLoadingLabel = "کسری هنگام ارسال / بارگیری";
    public const string TransitOrReceiptLabel = "کسری در مسیر / هنگام رسید";
    public const string TankLossLabel = "ضایعات داخل مخزن";
    public const string StockCorrectionLabel = "اصلاح موجودی";

    public static IReadOnlyList<(LossEventStage Stage, string Label)> Build(LossEventStage current)
    {
        var shipmentStage = current == LossEventStage.DispatchShortage
            ? LossEventStage.DispatchShortage
            : LossEventStage.LoadingDifference;
        var transitStage = current == LossEventStage.TransitLoss
            ? LossEventStage.TransitLoss
            : LossEventStage.ReceiptShortage;

        var choices = new List<(LossEventStage Stage, string Label)>
        {
            (shipmentStage, ShipmentOrLoadingLabel),
            (transitStage, TransitOrReceiptLabel),
            (LossEventStage.TankNaturalLoss, TankLossLabel),
            (LossEventStage.ManualAdjustment, StockCorrectionLabel)
        };

        if (choices.All(c => c.Stage != current))
        {
            choices.Insert(0, (current, LossEventStageLabels.ToPersian(current)));
        }

        return choices;
    }
}

public sealed class LossEventCreateViewModel
{
    /// <summary>
    /// PTG-P1-05 — نسخهٔ سطری که کاربر هنگامِ بازکردنِ فرم دید. با فرم برمی‌گردد تا ذخیره
    /// روی نسخهٔ کهنه رد شود. صفر یعنی فرم نسخه نفرستاده است.
    /// </summary>
    public long Version { get; set; }

    [Display(Name = "نوع رویداد")]
    public LossEventStage Stage { get; set; } = LossEventStage.ReceiptShortage;

    [Display(Name = "جنس")]
    [Range(1, int.MaxValue, ErrorMessage = "انتخاب جنس الزامی است.")]
    public int ProductId { get; set; }

    [Display(Name = "قرارداد")]
    public int? ContractId { get; set; }

    [Display(Name = "پرونده کشتی / محموله")]
    public int? ShipmentId { get; set; }

    [Display(Name = "بارگیری")]
    public int? LoadingRegisterId { get; set; }

    [Display(Name = "رسید بارگیری")]
    public int? LoadingReceiptId { get; set; }

    [Display(Name = "دیسپچ")]
    public int? TruckDispatchId { get; set; }

    [Display(Name = "حمل")]
    public int? TransportLegId { get; set; }

    [Display(Name = "فروش")]
    public int? SalesTransactionId { get; set; }

    [Display(Name = "ترمینال")]
    public int? TerminalId { get; set; }

    [Display(Name = "مخزن")]
    public int? StorageTankId { get; set; }

    [Display(Name = "تاریخ رویداد")]
    [DataType(DataType.Date)]
    public DateTime EventDate { get; set; } = AfghanistanBusinessClock.SystemToday;

    [Display(Name = "مقدار مورد انتظار (MT)")]
    [Range(typeof(decimal), "0", "79228162514264337593543950335", ErrorMessage = "مقدار مورد انتظار نامعتبر است.")]
    public decimal ExpectedQuantityMt { get; set; }

    [Display(Name = "مقدار واقعی (MT)")]
    [Range(typeof(decimal), "0", "79228162514264337593543950335", ErrorMessage = "مقدار واقعی نامعتبر است.")]
    public decimal ActualQuantityMt { get; set; }

    [Display(Name = "تلورانس (MT)")]
    [Range(typeof(decimal), "0", "79228162514264337593543950335", ErrorMessage = "تلورانس نامعتبر است.")]
    public decimal ToleranceQuantityMt { get; set; }

    [Display(Name = "نوع مسئول")]
    [StringLength(100)]
    public string? ResponsiblePartyType { get; set; }

    [Display(Name = "نام مسئول")]
    [StringLength(200)]
    public string? ResponsiblePartyName { get; set; }

    [Display(Name = "نحوه برخورد مالی")]
    [StringLength(200)]
    public string? FinancialTreatment { get; set; }

    // اثر روی موجودی تصمیم کاربر نیست: سرور آن را از روی مرحله و با LossStagePolicy تعیین
    // می‌کند و مقدار ارسالی مرورگر را نادیده می‌گیرد. فیلد برای نمایش و سازگاری می‌ماند.
    [Display(Name = "بر موجودی اثر دارد")]
    public bool AffectsInventory { get; set; }

    [Display(Name = "مرجع")]
    [StringLength(200)]
    public string? Reference { get; set; }

    [Display(Name = "یادداشت")]
    [StringLength(1000)]
    public string? Notes { get; set; }

    [StringLength(1000)]
    public string? ReturnUrl { get; set; }
}

public sealed class LossEventIndexFilterViewModel
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

    [Display(Name = "مرحله")]
    public LossEventStage? Stage { get; set; }

    [Display(Name = "مسئول")]
    [StringLength(200)]
    public string? ResponsiblePartyName { get; set; }

    [Display(Name = "اثر بر موجودی")]
    public bool? AffectsInventory { get; set; }

    public bool ChargeableOnly { get; set; }

    // فیلتر نوع تفاوت: کسری (تفاوت مثبت) و اضافه‌بار (تفاوت منفی) هرگز با هم جمع نمی‌شوند.
    [Display(Name = "نوع تفاوت")]
    public LossEventVarianceFilter Variance { get; set; } = LossEventVarianceFilter.All;
}

public enum LossEventVarianceFilter
{
    All = 0,
    ShortageOnly = 1,
    SurplusOnly = 2
}

public sealed class LossEventListItemViewModel
{
    public int Id { get; init; }
    public DateTime EventDate { get; init; }
    public LossEventStage Stage { get; init; }
    public string ProductName { get; init; } = string.Empty;
    public string? ContractName { get; init; }
    public string? ContractNumber { get; init; }
    public string ContractDisplayLabel => Contract.BuildDisplayLabel(ContractName, ContractNumber);
    public string? ShipmentCode { get; init; }
    public decimal DifferenceQuantityMt { get; init; }
    public decimal AllowableLossMt { get; init; }
    public decimal ChargeableLossMt { get; init; }
    public bool AffectsInventory { get; init; }
    public string? ResponsiblePartyName { get; init; }
    /// <summary>نمبر وسیلهٔ مرحله‌ای که کسری روی آن ثبت شده (ارسال، حمل یا بارگیری).</summary>
    public string? VehicleNumber { get; init; }
}

public sealed class LossEventIndexViewModel
{
    public LossEventIndexFilterViewModel Filter { get; init; } = new();
    public IReadOnlyList<LossEventListItemViewModel> Items { get; init; } = [];
    public int CurrentPage { get; init; } = 1;
    public int PageCount { get; init; } = 1;
    public int TotalCount { get; init; }
}

public sealed class LossEventDetailsViewModel
{
    public int Id { get; init; }
    public DateTime EventDate { get; init; }
    public LossEventStage Stage { get; init; }
    public string ProductName { get; init; } = string.Empty;
    public string? ContractName { get; init; }
    public string? ContractNumber { get; init; }
    public string ContractDisplayLabel => Contract.BuildDisplayLabel(ContractName, ContractNumber);
    public string? ShipmentCode { get; init; }
    public int? ShipmentId { get; init; }
    public string? LoadingRegisterLabel { get; init; }
    public int? LoadingRegisterId { get; init; }
    public string? LoadingReceiptLabel { get; init; }
    public int? LoadingReceiptId { get; init; }
    public string? TruckDispatchLabel { get; init; }
    public int? TruckDispatchId { get; init; }
    public string? SalesLabel { get; init; }
    public int? SalesTransactionId { get; init; }
    public string? TerminalName { get; init; }
    public string? StorageTankCode { get; init; }
    public decimal ExpectedQuantityMt { get; init; }
    public decimal ActualQuantityMt { get; init; }
    public decimal DifferenceQuantityMt { get; init; }
    public decimal ToleranceQuantityMt { get; init; }
    public decimal AllowableLossMt { get; init; }
    public decimal ChargeableLossMt { get; init; }
    public string? ResponsiblePartyType { get; init; }
    public string? ResponsiblePartyName { get; init; }
    public string? FinancialTreatment { get; init; }
    public bool AffectsInventory { get; init; }
    public int? InventoryMovementId { get; init; }
    public string? Reference { get; init; }
    public string? Notes { get; init; }
}
