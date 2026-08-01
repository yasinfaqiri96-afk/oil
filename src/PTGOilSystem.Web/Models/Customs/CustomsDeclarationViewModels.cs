using System.ComponentModel.DataAnnotations;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services.Time;

namespace PTGOilSystem.Web.Models.Customs;

public sealed class CustomsDeclarationCreateViewModel
{
    // وقتی > 0 باشد یعنی فرم در حالت ویرایش است (همان فرم برای ثبت و ویرایش استفاده می‌شود).
    public int Id { get; set; }

    public int? LoadingRegisterId { get; set; }
    public int? TransportLegId { get; set; }
    public int? TruckDispatchId { get; set; }

    [Display(Name = "تاریخ اعلامیه گمرکی")]
    [DataType(DataType.Date)]
    public DateTime DeclarationDate { get; set; } = AfghanistanBusinessClock.SystemToday;

    [Display(Name = "شماره واگن / موتر")]
    [StringLength(200)]
    public string? WagonOrTruckNumber { get; set; }

    [Display(Name = "نمبر ACCD / شماره مرجع اعلامیه")]
    [StringLength(100)]
    public string? DeclarationReference { get; set; }

    [Display(Name = "نمبر جواز")]
    [StringLength(100)]
    public string? PermitNumber { get; set; }

    [Display(Name = "صاحب جواز")]
    [StringLength(200)]
    public string? PermitHolderName { get; set; }

    [Display(Name = "نوع")]
    [StringLength(100)]
    public string? CustomsType { get; set; }

    [Display(Name = "نوع جنس")]
    [StringLength(200)]
    public string? GoodsName { get; set; }

    [Display(Name = "مسیر")]
    [StringLength(300)]
    public string? Route { get; set; }

    [Display(Name = "وزن کنسمنت (MT)")]
    [Range(typeof(decimal), "0", "79228162514264337593543950335")]
    public decimal? ConsignmentWeightMt { get; set; }

    [Display(Name = "یادداشت")]
    [StringLength(1000)]
    public string? Notes { get; set; }

    public List<CustomsDeclarationItemRowViewModel> Items { get; set; } = [];

    // Read-only display fields
    public string LoadingRegisterLabel { get; set; } = "";
    public string TransportLegLabel { get; set; } = "";
    public string TruckDispatchLabel { get; set; } = "";
    public string ContractNumber { get; set; } = "";
    public string ProductName { get; set; } = "";
    public string WagonNumber { get; set; } = "";

    [StringLength(1000)]
    public string? ReturnUrl { get; set; }
}

public sealed class CustomsDeclarationItemRowViewModel
{
    public CustomsComponentType ComponentType { get; set; }
    public string ComponentLabel { get; set; } = "";

    // هر ردیف فقط یک مبلغ اصلی دارد؛ کاربر ارز را انتخاب می‌کند و فقط همان مبلغ editable است.
    [Display(Name = "ارز")]
    public string Currency { get; set; } = CustomsCurrency.Afn; // "AFN" یا "USD"

    [Display(Name = "مبلغ")]
    [Range(typeof(decimal), "0", "79228162514264337593543950335")]
    public decimal Amount { get; set; }

    // نرخ تبدیل = چند AFN در برابر هر ۱ USD (مثلاً ۷۰). برای محاسبهٔ معادل ارز دوم.
    [Display(Name = "نرخ تبدیل (AFN/USD)")]
    [Range(typeof(decimal), "0", "79228162514264337593543950335")]
    public decimal? Rate { get; set; }

    [Display(Name = "یادداشت")]
    [StringLength(500)]
    public string? Notes { get; set; }
}

// ارزهای مجاز برای ردیف‌های گمرکی.
public static class CustomsCurrency
{
    public const string Afn = "AFN";
    public const string Usd = "USD";

    public static bool IsUsd(string? currency)
        => string.Equals(currency, Usd, StringComparison.OrdinalIgnoreCase);

    public static string Normalize(string? currency)
        => IsUsd(currency) ? Usd : Afn;
}

public sealed class CustomsDeclarationListItemViewModel
{
    public int Id { get; init; }
    public int? LoadingRegisterId { get; init; }
    public int? TransportLegId { get; init; }
    public int? TruckDispatchId { get; init; }
    public string SourceLabel { get; init; } = "";
    public DateTime DeclarationDate { get; init; }
    public string? WagonOrTruckNumber { get; init; }
    public string? DeclarationReference { get; init; }
    public decimal? ConsignmentWeightMt { get; init; }
    public decimal TotalAfn { get; init; }
    public decimal TotalUsd { get; init; }
    public decimal? RatePerMtAfn { get; init; }
    public decimal? RatePerMtUsd { get; init; }
    public string ContractNumber { get; init; } = "";
    public string ProductName { get; init; } = "";
}

public sealed class CustomsDeclarationIndexViewModel
{
    public int? LoadingRegisterId { get; init; }
    public int? TransportLegId { get; init; }
    public int? TruckDispatchId { get; init; }
    public string LoadingRegisterLabel { get; init; } = "";
    public string TransportLegLabel { get; init; } = "";
    public string TruckDispatchLabel { get; init; } = "";
    public IReadOnlyList<CustomsDeclarationListItemViewModel> Items { get; init; } = [];
    public int CurrentPage { get; init; } = 1;
    public int PageCount { get; init; } = 1;
    public int TotalCount { get; init; }
    public string? Query { get; init; }
    public DateTime? FromDate { get; init; }
    public DateTime? ToDate { get; init; }
}

public sealed class CustomsDeclarationDetailsViewModel
{
    public int Id { get; init; }
    public int? LoadingRegisterId { get; init; }
    public int? TransportLegId { get; init; }
    public int? TruckDispatchId { get; init; }
    public string SourceLabel { get; init; } = "";
    public DateTime DeclarationDate { get; init; }
    public string? WagonOrTruckNumber { get; init; }
    public string? DeclarationReference { get; init; }
    public decimal? ConsignmentWeightMt { get; init; }
    public decimal TotalAfn { get; init; }
    public decimal TotalUsd { get; init; }
    public decimal? RatePerMtAfn { get; init; }
    public decimal? RatePerMtUsd { get; init; }
    public string? Notes { get; init; }
    public string ContractNumber { get; init; } = "";
    public string ProductName { get; init; } = "";
    public string? WagonNumber { get; init; }
    public IReadOnlyList<CustomsDeclarationItemDetailViewModel> Items { get; init; } = [];
    public IReadOnlyList<CustomsDeclarationDocumentViewModel> Documents { get; init; } = [];
}

public sealed class CustomsDeclarationDocumentViewModel
{
    public int Id { get; init; }
    public string? DocumentType { get; init; }
    public string OriginalFileName { get; init; } = "";
    public string FilePath { get; init; } = "";
    public string? ContentType { get; init; }
    public long FileSizeBytes { get; init; }
    public string? Notes { get; init; }
    public DateTime UploadedAt { get; init; }
    public string? UploadedByUserName { get; init; }
}

public sealed class CustomsDeclarationItemDetailViewModel
{
    public int Id { get; init; }
    public string ComponentLabel { get; init; } = "";
    public decimal AmountAfn { get; init; }
    public decimal? AmountUsd { get; init; }
    public string? Notes { get; init; }
}

public sealed class PerWagonPnlRowViewModel
{
    public int LoadingRegisterId { get; init; }
    public DateTime LoadingDate { get; init; }
    public string? WagonNumber { get; init; }
    public decimal LoadedQuantityMt { get; init; }
    public decimal? ChargeableQuantityMt { get; init; }
    public decimal? LoadingPriceUsd { get; init; }
    public decimal? PurchaseCostUsd { get; init; }
    public decimal? SalesRevenueUsd { get; init; }
    public decimal? TotalCustomsAfn { get; init; }
    public decimal? TotalCustomsUsd { get; init; }
    public decimal? RailwayExpenseUsd { get; init; }
    public decimal? OtherExpenseUsd { get; init; }
    public decimal? GrossMarginUsd { get; init; }
    public decimal? MarginPerMtUsd { get; init; }
    public int CustomsDeclarationCount { get; init; }
}

// ── Customs Permit Turnover (read-only internal-control report) ──────────────
public sealed class CustomsPermitTurnoverViewModel
{
    // Filters
    [Display(Name = "از تاریخ")]
    public DateTime? FromDate { get; set; }

    [Display(Name = "تا تاریخ")]
    public DateTime? ToDate { get; set; }

    [Display(Name = "صاحب جواز")]
    public string? PermitHolderName { get; set; }

    [Display(Name = "نمبر جواز")]
    public string? PermitNumber { get; set; }

    [Display(Name = "نمبر ACCD")]
    public string? AccdNumber { get; set; }

    [Display(Name = "نمبر موتر")]
    public string? VehicleNumber { get; set; }

    [Display(Name = "نوع")]
    public string? CustomsType { get; set; }

    [Display(Name = "جنس")]
    public string? GoodsName { get; set; }

    [Display(Name = "مسیر")]
    public string? Route { get; set; }

    [Display(Name = "فیصدی مالیه (%)")]
    public decimal TaxPercent { get; set; }

    // Results
    public List<CustomsPermitTurnoverRowViewModel> Rows { get; set; } = [];

    // Summary
    public int VehicleCount { get; set; }
    public decimal TotalQuantityMt { get; set; }

    // Government tax is computed on the Mahsooli (محصولی) total only.
    public decimal TotalMahsooliAfn { get; set; }
    public decimal TotalMahsooliUsd { get; set; }

    // Read-only comparison: total customs (all components). NOT used for tax.
    public decimal TotalCustomsAfn { get; set; }
    public decimal TotalCustomsUsd { get; set; }

    // AFN equivalent for total customs (sum of per-row USD->AFN conversions)
    public decimal TotalCustomsAfnEquivalent { get; set; }

    // Display controls
    public string SelectedCurrency { get; set; } = "USD";

    // Summary display/equivalent amounts (respecting SelectedCurrency)
    public decimal TotalCustomsDisplayAmount { get; set; }
    public string TotalCustomsDisplayCurrency { get; set; } = "USD";
    public decimal TotalCustomsEquivalentAmount { get; set; }
    public string TotalCustomsEquivalentCurrency { get; set; } = "AFN";

    public decimal EstimatedTaxAfn =>
        decimal.Round(TotalMahsooliAfn * TaxPercent / 100m, 2, MidpointRounding.AwayFromZero);
}

public sealed class CustomsPermitTurnoverRowViewModel
{
    public int Id { get; init; }
    public DateTime DeclarationDate { get; init; }
    public string? AccdNumber { get; init; }
    public string? VehicleNumber { get; init; }
    public string? PermitNumber { get; init; }
    public string? PermitHolderName { get; init; }
    public string? CustomsType { get; init; }
    public string? GoodsName { get; init; }
    public decimal? QuantityMt { get; init; }

    // Taxable customs duty for this vehicle = sum of Mahsooli (محصولی) items only.
    public decimal MahsooliAfn { get; init; }

    // USD equivalent of the Mahsooli family (محصولی + محصولی دالری) for declarations entered in USD.
    public decimal MahsooliUsd { get; init; }

    // Total customs for this vehicle (all components) — display-only comparison.
    public decimal TotalCustomsAfn { get; init; }

    // Total customs in USD (all components) — for declarations entered in USD.
    public decimal TotalCustomsUsd { get; init; }

    // USD->AFN rate used for this declaration (if available)
    public decimal RateUsdToAfn { get; set; }

    // AFN equivalent for the total customs (TotalCustomsUsd * RateUsdToAfn)
    public decimal TotalCustomsAfnEquivalent { get; set; }

    // Display fields for UI toggling
    public decimal MahsooliDisplayAmount { get; set; }
    public string MahsooliDisplayCurrency { get; set; } = "USD";
    public decimal MahsooliEquivalentAmount { get; set; }
    public string MahsooliEquivalentCurrency { get; set; } = "AFN";

    public decimal TotalCustomsDisplayAmount { get; set; }
    public string TotalCustomsDisplayCurrency { get; set; } = "USD";
    public decimal TotalCustomsEquivalentAmount { get; set; }
    public string TotalCustomsEquivalentCurrency { get; set; } = "AFN";

    public string? Route { get; init; }
    public string? Notes { get; init; }
}

// ─────────────────────────────────────────────────────────────────────────────
// اپلود اکسل گروهی گمرک — دو حوزه: حمل از موجودی یا ارسال موتر.
// هر ردیف با نمبر پلیت/سیمیر به عملیاتِ «در جریان» (نه تکمیل‌شده) تطبیق می‌شود.
// ─────────────────────────────────────────────────────────────────────────────
public enum CustomsImportScope
{
    InventoryTransport = 1, // حمل از موجودی‌ها (تطبیق با پلیت + سیمیر)
    TruckDispatch = 2       // ارسال‌های موتر (تطبیق فقط با پلیت)
}

public sealed class CustomsImportViewModel
{
    [Display(Name = "حوزه")]
    public CustomsImportScope Scope { get; set; } = CustomsImportScope.InventoryTransport;

    // نرخ تبدیل افغانی به دالر (چند افغانی برای هر ۱ دالر). همه مبالغ افغانی با این نرخ به USD تبدیل می‌شوند.
    [Display(Name = "نرخ تبدیل (AFN به ازای هر ۱ USD)")]
    [Range(0.0001, (double)decimal.MaxValue, ErrorMessage = "نرخ تبدیل باید بزرگ‌تر از صفر باشد.")]
    public decimal? FxRateToUsd { get; set; }

    public string? ReturnUrl { get; set; }

    // پس از اپلود پر می‌شود (خلاصهٔ نتیجه).
    public bool HasResult { get; set; }
    public int SavedCount { get; set; }
    public int SkippedCount { get; set; }
    public List<CustomsImportResultRow> Rows { get; set; } = [];
}

public sealed record CustomsImportResultRow
{
    public int RowNumber { get; init; }
    public string? Simir { get; init; }
    public string? PlateNumber { get; init; }
    public decimal? WeightMt { get; init; }
    public decimal TotalAfn { get; init; }
    public decimal TotalUsd { get; init; }
    public bool Matched { get; init; }
    public int? DeclarationId { get; init; }
    public string? MatchedLabel { get; init; }
    public string? SkipReason { get; init; }
}
