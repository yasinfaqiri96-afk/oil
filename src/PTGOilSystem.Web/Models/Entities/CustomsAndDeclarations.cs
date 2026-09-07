using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace PTGOilSystem.Web.Models.Entities;

// Gap #1 — per-wagon/truck customs AFN breakdown (15 component types)
public enum CustomsComponentType
{
    [Display(Name = "محصولی (AFN)")] Mahsooli = 1,
    [Display(Name = "فواید عامه")] FawaidAama = 2,
    [Display(Name = "محصولی دالری")] MahsooliDolari = 3,
    [Display(Name = "کمیشن تعرفه")] KomisionTarifa = 4,
    [Display(Name = "نورم استندرد")] NormStandard = 5,
    [Display(Name = "خط آهن")] KhatAhan = 6,
    [Display(Name = "علم و خبر")] ElmKhabar = 7,
    [Display(Name = "گمرک سرحدی")] GomrokSarhadi = 8,
    [Display(Name = "کمیشنکار")] Komisionkar = 9,
    [Display(Name = "مثبت بودن گاز")] GasMasbut = 10,
    [Display(Name = "متفرقه")] Mutafarraka = 11,
    [Display(Name = "حق الخدمه مواد نفت")] HaqKhidma = 12,
    [Display(Name = "یوزبلاغ")] Yozbulagh = 13,
    [Display(Name = "کمیشن بارچلانی")] KomisionBarchalani = 14,
    [Display(Name = "کمیشن بانک")] KomisionBank = 15,
    [Display(Name = "مصرف ۲۰ پول")] Masraf20Pul = 16,
    [Display(Name = "بارنامه واگن")] BarnamaWagon = 17,
    [Display(Name = "ترازوی موتر")] TarazuMotor = 18,
    [Display(Name = "سایر")] Other = 99
}

// One declaration per wagon/truck — links to a LoadingRegister row
public class CustomsDeclaration : BaseEntity
{
    public int? LoadingRegisterId { get; set; }
    public LoadingRegister? LoadingRegister { get; set; }
    public int? TransportLegId { get; set; }
    public InventoryTransportLeg? TransportLeg { get; set; }
    public int? TruckDispatchId { get; set; }
    public TruckDispatch? TruckDispatch { get; set; }

    // Wagon / truck reference fields
    [MaxLength(200)] public string? WagonOrTruckNumber { get; set; }
    [MaxLength(100)] public string? DeclarationReference { get; set; }

    // Permit / turnover reporting fields (additive, optional — used by the
    // read-only Customs Permit Turnover report; no business logic depends on them).
    [MaxLength(100)] public string? PermitNumber { get; set; }
    [MaxLength(200)] public string? PermitHolderName { get; set; }
    [MaxLength(100)] public string? CustomsType { get; set; }
    [MaxLength(200)] public string? GoodsName { get; set; }
    [MaxLength(300)] public string? Route { get; set; }

    public DateTime DeclarationDate { get; set; }

    // Consignment weight (from B/L or RWB)
    public decimal? ConsignmentWeightMt { get; set; }

    // Totals (computed from items, also stored for quick reporting)
    public decimal TotalAfn { get; set; }
    public decimal TotalUsd { get; set; }
    // Per-MT rate for comparison
    public decimal? RatePerMtAfn { get; set; }
    public decimal? RatePerMtUsd { get; set; }

    [MaxLength(1000)] public string? Notes { get; set; }

    // ---------------------------------------------------------------------
    // PTG-P1-04 — هویتِ تسویه، جدا برای دو جنسِ اظهارنامه.
    //
    // یک اظهارنامه یک مبلغ نیست: حقوقِ دولتی همان‌جا نقد می‌شود و کمیشنکار اغلب بدهی
    // می‌ماند. اگر هویتِ تسویه یکی بود، همین حالتِ رایج قابل ثبت نمی‌شد و ناچار یکی از
    // دو طرف دروغ می‌شد. تفکیکِ اجزا در Services/Customs/CustomsComponentGroupMap.cs.
    //
    // هر گروه به یک ExpenseTransaction جدا می‌رسد و از همان ExpenseLedgerPoster عبور
    // می‌کند؛ لایهٔ مالیِ تازه‌ای ساخته نمی‌شود.
    // ---------------------------------------------------------------------

    /// <summary>حقوق دولتی: نقد از صندوق، یا بدهی به کسی که از جیب خود پرداخته.</summary>
    public ExpenseSettlementMode DutySettlementMode { get; set; } = ExpenseSettlementMode.Unknown;
    public int? DutyCashAccountId { get; set; }
    public CashAccount? DutyCashAccount { get; set; }

    /// <summary>
    /// طرف‌حسابِ حقوق دولتی وقتی شرکت خودش نپرداخته — معمولاً همان کمیشنکار.
    /// دولت طرف‌حسابِ ماندگار نیست، پس نوعِ طرف‌حسابِ تازه‌ای ساخته نمی‌شود.
    /// </summary>
    public int? DutyServiceProviderId { get; set; }

    /// <summary>کمیشن و خدمات: بدهی به کمیشنکار/شرکتِ خدماتی، یا نقد.</summary>
    public ExpenseSettlementMode ServiceSettlementMode { get; set; } = ExpenseSettlementMode.Unknown;
    public int? ServiceCashAccountId { get; set; }
    public CashAccount? ServiceCashAccount { get; set; }
    public int? ServiceProviderId { get; set; }
    public ServiceProvider? ServiceProvider { get; set; }

    public ICollection<CustomsDeclarationItem> Items { get; set; } = new List<CustomsDeclarationItem>();
    public ICollection<CustomsDeclarationDocument> Documents { get; set; } = new List<CustomsDeclarationDocument>();

    /// <summary>مصرف‌هایی که از همین اظهارنامه ساخته شده‌اند (حداکثر یکی per گروه).</summary>
    public ICollection<ExpenseTransaction> ExpenseTransactions { get; set; } = new List<ExpenseTransaction>();
}

// Uploaded supporting documents for a customs declaration (payment receipt,
// customs receipt, ACCD scan, Mahsooli document, etc.). Files live under
// wwwroot/uploads/customs-declarations/{CustomsDeclarationId}/.
public class CustomsDeclarationDocument : BaseEntity
{
    public int CustomsDeclarationId { get; set; }
    public CustomsDeclaration? CustomsDeclaration { get; set; }

    [MaxLength(100)] public string? DocumentType { get; set; }
    [Required, MaxLength(300)] public string OriginalFileName { get; set; } = "";
    [Required, MaxLength(200)] public string StoredFileName { get; set; } = "";
    [Required, MaxLength(500)] public string FilePath { get; set; } = "";
    [MaxLength(150)] public string? ContentType { get; set; }
    public long FileSizeBytes { get; set; }
    [MaxLength(1000)] public string? Notes { get; set; }
    public DateTime UploadedAt { get; set; } = DateTime.UtcNow;
    [MaxLength(200)] public string? UploadedByUserName { get; set; }
}

// One row per cost component (up to 15 types) per declaration
public class CustomsDeclarationItem : BaseEntity
{
    public int CustomsDeclarationId { get; set; }
    public CustomsDeclaration? CustomsDeclaration { get; set; }

    public CustomsComponentType ComponentType { get; set; }
    [MaxLength(200)] public string? CustomLabel { get; set; }

    // Primary currency is AFN; optional USD equivalent
    public decimal AmountAfn { get; set; }
    public decimal? AmountUsd { get; set; }

    [MaxLength(500)] public string? Notes { get; set; }
}
