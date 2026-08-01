using System.ComponentModel.DataAnnotations;
using PTGOilSystem.Web.Models.Entities;

namespace PTGOilSystem.Web.Models.Quality;

public sealed class QualityInspectionFilterViewModel
{
    [Display(Name = "جنس")] public int? ProductId { get; set; }
    [Display(Name = "قرارداد")] public int? ContractId { get; set; }
    [Display(Name = "محموله")] public int? ShipmentId { get; set; }
    [Display(Name = "وضعیت")] public QualityInspectionStatus? Status { get; set; }

    [Display(Name = "از تاریخ"), DataType(DataType.Date)] public DateTime? FromDate { get; set; }
    [Display(Name = "تا تاریخ"), DataType(DataType.Date)] public DateTime? ToDate { get; set; }

    public bool HasAny => ProductId.HasValue || ContractId.HasValue || ShipmentId.HasValue
        || Status.HasValue || FromDate.HasValue || ToDate.HasValue;
}

public sealed class QualityInspectionListItemViewModel
{
    public int Id { get; init; }
    public string ProductName { get; init; } = "";
    public string? CompanyName { get; init; }
    public string? ContractNumber { get; init; }
    public string? ShipmentReference { get; init; }
    public string? CustomsDeclarationReference { get; init; }
    public string LaboratoryName { get; init; } = "";
    public string? ResultNumber { get; init; }
    public DateTime SampleDate { get; init; }
    public DateTime? ResultDate { get; init; }
    public QualityInspectionStatus Status { get; init; }
    public int DocumentCount { get; init; }

    /// <summary>آخرین وضعیت قطعی همین بار (نه لزوماً همین ردیف) برای ستون «نتیجهٔ نهایی».</summary>
    public QualityInspectionStatus? FinalStatusForSameLoad { get; init; }
    public bool IsFinalForSameLoad { get; init; }
}

public sealed class QualityInspectionIndexViewModel
{
    public QualityInspectionFilterViewModel Filter { get; init; } = new();
    public IReadOnlyList<QualityInspectionListItemViewModel> Items { get; init; } = [];
    public int CurrentPage { get; init; } = 1;
    public int PageCount { get; init; } = 1;
    public int TotalCount { get; init; }
    public int PendingCount { get; init; }
    public int AcceptedCount { get; init; }
    public int RejectedCount { get; init; }
}

public sealed class QualityInspectionFormViewModel : IValidatableObject
{
    public int Id { get; set; }

    [Display(Name = "جواز/شرکت")] public int? CompanyId { get; set; }
    [Display(Name = "قرارداد")] public int? ContractId { get; set; }
    [Display(Name = "محموله")] public int? ShipmentId { get; set; }
    [Display(Name = "بارگیری")] public int? LoadingRegisterId { get; set; }
    [Display(Name = "اظهارنامه گمرکی")] public int? CustomsDeclarationId { get; set; }

    [Display(Name = "جنس"), Range(1, int.MaxValue, ErrorMessage = "انتخاب جنس الزامی است.")]
    public int ProductId { get; set; }

    [Display(Name = "لابراتوار"), Required(ErrorMessage = "نام لابراتوار الزامی است."), MaxLength(200)]
    public string LaboratoryName { get; set; } = "";

    [Display(Name = "شماره نتیجه"), MaxLength(100)] public string? ResultNumber { get; set; }

    [Display(Name = "تاریخ نمونه‌گیری"), DataType(DataType.Date)]
    public DateTime SampleDate { get; set; }

    [Display(Name = "تاریخ نتیجه"), DataType(DataType.Date)]
    public DateTime? ResultDate { get; set; }

    [Display(Name = "وضعیت")] public QualityInspectionStatus Status { get; set; } = QualityInspectionStatus.Pending;

    [Display(Name = "دانسیته (kg/m³)")] public decimal? DensityKgM3 { get; set; }
    [Display(Name = "گوگرد (%)")] public decimal? SulphurPercent { get; set; }
    [Display(Name = "نقطه اشتعال (°C)")] public decimal? FlashPointC { get; set; }
    [Display(Name = "آب (%)")] public decimal? WaterContentPercent { get; set; }
    [Display(Name = "عدد اکتان/ستان")] public decimal? OctaneOrCetaneNumber { get; set; }

    [Display(Name = "سایر مشخصات"), MaxLength(2000)] public string? AdditionalSpecifications { get; set; }
    [Display(Name = "توضیح"), MaxLength(2000)] public string? Description { get; set; }
    [Display(Name = "دلیل رد"), MaxLength(2000)] public string? RejectionReason { get; set; }

    public string? ReturnUrl { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Status == QualityInspectionStatus.Rejected && string.IsNullOrWhiteSpace(RejectionReason))
        {
            yield return new ValidationResult(
                "برای نتیجهٔ «رد» ثبت دلیل الزامی است.",
                [nameof(RejectionReason)]);
        }

        if (Status != QualityInspectionStatus.Pending && !ResultDate.HasValue)
        {
            yield return new ValidationResult(
                "برای نتیجهٔ قطعی، تاریخ نتیجه الزامی است.",
                [nameof(ResultDate)]);
        }

        if (ResultDate.HasValue && ResultDate.Value.Date < SampleDate.Date)
        {
            yield return new ValidationResult(
                "تاریخ نتیجه نمی‌تواند پیش از تاریخ نمونه‌گیری باشد.",
                [nameof(ResultDate)]);
        }

        // سند بدون هیچ اتصال عملیاتی مجاز است (بعداً تکمیل می‌شود) اما در گزارش
        // «بررسی ناهماهنگی‌ها» به‌عنوان سند ناقص دیده می‌شود؛ اینجا ثبت را رد نمی‌کنیم.
    }
}

public sealed class QualityInspectionDocumentViewModel
{
    public int Id { get; init; }
    public string? DocumentType { get; init; }
    public string OriginalFileName { get; init; } = "";
    public string FilePath { get; init; } = "";
    public long FileSizeBytes { get; init; }
    public DateTime UploadedAt { get; init; }
    public string? UploadedByUserName { get; init; }
    public string? Notes { get; init; }
}

public sealed class QualityInspectionDetailsViewModel
{
    public int Id { get; init; }
    public string ProductName { get; init; } = "";
    public string? CompanyName { get; init; }
    public int? ContractId { get; init; }
    public string? ContractNumber { get; init; }
    public int? ShipmentId { get; init; }
    public string? ShipmentReference { get; init; }
    public int? LoadingRegisterId { get; init; }
    public string? LoadingReference { get; init; }
    public int? CustomsDeclarationId { get; init; }
    public string? CustomsDeclarationReference { get; init; }

    public string LaboratoryName { get; init; } = "";
    public string? ResultNumber { get; init; }
    public DateTime SampleDate { get; init; }
    public DateTime? ResultDate { get; init; }
    public QualityInspectionStatus Status { get; init; }

    public decimal? DensityKgM3 { get; init; }
    public decimal? SulphurPercent { get; init; }
    public decimal? FlashPointC { get; init; }
    public decimal? WaterContentPercent { get; init; }
    public decimal? OctaneOrCetaneNumber { get; init; }
    public string? AdditionalSpecifications { get; init; }
    public string? Description { get; init; }
    public string? RejectionReason { get; init; }

    public string? CreatedByUserName { get; init; }
    public DateTime CreatedAtUtc { get; init; }
    public string? UpdatedByUserName { get; init; }
    public DateTime? UpdatedAtUtc { get; init; }

    public IReadOnlyList<QualityInspectionDocumentViewModel> Documents { get; init; } = [];

    /// <summary>
    /// همهٔ آزمایش‌های همین بار به ترتیب زمانی، تا معلوم باشد این ردیف آخرین نتیجه است یا نه.
    /// </summary>
    public IReadOnlyList<QualityInspectionListItemViewModel> SiblingInspections { get; init; } = [];
    public bool IsFinalResult { get; init; }
}
