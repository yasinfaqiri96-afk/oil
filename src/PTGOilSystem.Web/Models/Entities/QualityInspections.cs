using System.ComponentModel.DataAnnotations;

namespace PTGOilSystem.Web.Models.Entities;

/// <summary>
/// وضعیت یک آزمایش کیفیت. عمداً فقط سه حالت دارد: هیچ Workflow چندمرحله‌ای ساخته نمی‌شود.
/// </summary>
public enum QualityInspectionStatus
{
    [Display(Name = "در انتظار نتیجه")] Pending = 1,
    [Display(Name = "قبول")] Accepted = 2,
    [Display(Name = "رد")] Rejected = 3
}

/// <summary>
/// یک آزمایش/بررسی کیفیت روی یک بار. یک محموله می‌تواند چند آزمایش داشته باشد؛
/// «نتیجهٔ نهایی» همیشه آخرین آزمایشِ غیرِ Pending بر اساس <see cref="ResultDate"/> است و
/// در ستون جداگانه‌ای ذخیره نمی‌شود تا دو منبع حقیقت ساخته نشود.
///
/// این موجودیت هیچ اثر مالی، موجودی یا لجری ندارد: فقط سند کیفیت است.
/// </summary>
public class QualityInspection : BaseEntity
{
    // ---- دامنه (همه اختیاری‌اند تا سند ناقصِ واقعی قابل ثبت و بعداً قابل تکمیل باشد؛
    //      ناقص‌بودن در Reconciliation دیده می‌شود، نه با رد کردن ثبت).
    public int? CompanyId { get; set; }
    public Company? Company { get; set; }

    public int? ContractId { get; set; }
    public Contract? Contract { get; set; }

    public int? ShipmentId { get; set; }
    public Shipment? Shipment { get; set; }

    public int? LoadingRegisterId { get; set; }
    public LoadingRegister? LoadingRegister { get; set; }

    public int? CustomsDeclarationId { get; set; }
    public CustomsDeclaration? CustomsDeclaration { get; set; }

    public int ProductId { get; set; }
    public Product? Product { get; set; }

    // ---- شناسه‌های آزمایش
    [Required, MaxLength(200)] public string LaboratoryName { get; set; } = "";
    [MaxLength(100)] public string? ResultNumber { get; set; }
    public DateTime SampleDate { get; set; }
    public DateTime? ResultDate { get; set; }

    public QualityInspectionStatus Status { get; set; } = QualityInspectionStatus.Pending;

    // ---- مشخصات اصلی کیفیت (همه اختیاری؛ هر آزمایشگاه همهٔ آیتم‌ها را نمی‌دهد).
    public decimal? DensityKgM3 { get; set; }
    public decimal? SulphurPercent { get; set; }
    public decimal? FlashPointC { get; set; }
    public decimal? WaterContentPercent { get; set; }
    public decimal? OctaneOrCetaneNumber { get; set; }
    /// <summary>هر مشخصهٔ دیگری که ستون اختصاصی ندارد، به‌صورت متن آزاد.</summary>
    [MaxLength(2000)] public string? AdditionalSpecifications { get; set; }

    [MaxLength(2000)] public string? Description { get; set; }
    /// <summary>فقط وقتی <see cref="Status"/> برابر Rejected است معنا دارد.</summary>
    [MaxLength(2000)] public string? RejectionReason { get; set; }

    // ---- ردیابی. CreatedAtUtc و UpdatedAtUtc از BaseEntity می‌آیند؛ اینجا فقط نام کاربر اضافه می‌شود.
    [MaxLength(150)] public string? CreatedByUserName { get; set; }
    [MaxLength(150)] public string? UpdatedByUserName { get; set; }

    public ICollection<QualityInspectionDocument> Documents { get; set; }
        = new List<QualityInspectionDocument>();
}

/// <summary>
/// فایل نتیجهٔ آزمایش. دقیقاً همان الگوی <see cref="CustomsDeclarationDocument"/> را دنبال
/// می‌کند تا از سیستم Upload فعلی استفاده شود و مسیر جدیدی ساخته نشود.
/// فایل‌ها زیر wwwroot/uploads/quality-inspections/{QualityInspectionId}/ ذخیره می‌شوند.
/// </summary>
public class QualityInspectionDocument : BaseEntity
{
    public int QualityInspectionId { get; set; }
    public QualityInspection? QualityInspection { get; set; }

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
