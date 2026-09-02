using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http;
using PTGOilSystem.Web.Helpers;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services.Time;

namespace PTGOilSystem.Web.Models.OperationalAssets;

public sealed class OperationalAssetIndexFilterViewModel
{
    [Display(Name = "نوع دارایی")]
    public OperationalAssetType? AssetType { get; set; }

    [Display(Name = "وضعیت")]
    public bool? IsActive { get; set; }

    [Display(Name = "جستجو")]
    [StringLength(200)]
    public string? Query { get; set; }
}

public sealed class OperationalAssetIndexItemViewModel
{
    public int Id { get; init; }
    public string AssetCode { get; init; } = "";
    public string Name { get; init; } = "";
    public OperationalAssetType AssetType { get; init; }
    public string AssetTypeName => OperationalAssetLabels.AssetType(AssetType);
    public string? LinkedResourceText { get; init; }
    public OperationalAssetOwnershipMode OwnershipMode { get; init; }
    public string OwnershipModeName => OperationalAssetLabels.OwnershipMode(OwnershipMode);
    public decimal MonthlyDepreciationUsd { get; init; }
    public decimal InternalRentUsd { get; init; }
    public decimal ExternalRentUsd { get; init; }
    // کرایهٔ حمل/رسید با دارایی خودِ شرکت = درآمد دارایی.
    public decimal FreightIncomeUsd { get; init; }
    public decimal DirectExpensesUsd { get; init; }
    public decimal NetBeforeDepreciationUsd => InternalRentUsd + ExternalRentUsd + FreightIncomeUsd - DirectExpensesUsd;
    public bool IsActive { get; init; }
}

public sealed class OperationalAssetIndexViewModel
{
    public OperationalAssetIndexFilterViewModel Filter { get; init; } = new();
    public IReadOnlyList<OperationalAssetIndexItemViewModel> Items { get; init; } = [];
    public int CurrentPage { get; init; } = 1;
    public int PageCount { get; init; } = 1;
    public int TotalCount { get; init; }
    public decimal TotalInternalRentUsd { get; init; }
    public decimal TotalExternalRentUsd { get; init; }
    public decimal TotalFreightIncomeUsd { get; init; }
    public decimal TotalDirectExpensesUsd { get; init; }
    public decimal TotalMonthlyDepreciationUsd { get; init; }
    public decimal TotalNetResultUsd => TotalInternalRentUsd + TotalExternalRentUsd + TotalFreightIncomeUsd - TotalDirectExpensesUsd - TotalMonthlyDepreciationUsd;
}

public sealed class OperationalAssetFormViewModel
{
    public int Id { get; set; }

    [Display(Name = "کد دارایی")]
    [Required(ErrorMessage = "کد دارایی الزامی است.")]
    [StringLength(50)]
    public string AssetCode { get; set; } = "";

    [Display(Name = "نام")]
    [Required(ErrorMessage = "نام دارایی الزامی است.")]
    [StringLength(200)]
    public string Name { get; set; } = "";

    [Display(Name = "نوع دارایی")]
    public OperationalAssetType AssetType { get; set; } = OperationalAssetType.Truck;

    [Display(Name = "موتر مرتبط")]
    public int? LinkedTruckId { get; set; }

    [Display(Name = "مخزن مرتبط")]
    public int? LinkedStorageTankId { get; set; }

    [Display(Name = "ظرفیت MT")]
    [Range(typeof(decimal), "0", "79228162514264337593543950335", ErrorMessage = "ظرفیت نمی‌تواند منفی باشد.")]
    public decimal? CapacityMt { get; set; }

    [Display(Name = "موقعیت")]
    public int? LocationId { get; set; }

    [Display(Name = "ترمینال")]
    public int? TerminalId { get; set; }

    [Display(Name = "نوع مالکیت")]
    public OperationalAssetOwnershipMode OwnershipMode { get; set; } = OperationalAssetOwnershipMode.FullyCompanyOwned;

    [Display(Name = "تاریخ خرید")]
    [DataType(DataType.Date)]
    public DateTime? AcquisitionDate { get; set; }

    [Display(Name = "قیمت خرید USD")]
    [Range(typeof(decimal), "0", "79228162514264337593543950335", ErrorMessage = "قیمت خرید نمی‌تواند منفی باشد.")]
    public decimal? AcquisitionCostUsd { get; set; }

    [Display(Name = "تاریخ شروع کار")]
    [DataType(DataType.Date)]
    public DateTime? InServiceDate { get; set; }

    [Display(Name = "تاریخ خروج")]
    [DataType(DataType.Date)]
    public DateTime? DisposalDate { get; set; }

    [Display(Name = "وضعیت عملیاتی")]
    public OperationalAssetStatus OperationalStatus { get; set; } = OperationalAssetStatus.Active;

    [Display(Name = "استهلاک ماهانه USD")]
    [Range(typeof(decimal), "0", "79228162514264337593543950335", ErrorMessage = "استهلاک نمی‌تواند منفی باشد.")]
    public decimal MonthlyDepreciationUsd { get; set; }

    [Display(Name = "نرخ پیش‌فرض داخلی USD")]
    [Range(typeof(decimal), "0", "79228162514264337593543950335", ErrorMessage = "نرخ نمی‌تواند منفی باشد.")]
    public decimal? DefaultInternalRateUsd { get; set; }

    [Display(Name = "نرخ پیش‌فرض بیرونی USD")]
    [Range(typeof(decimal), "0", "79228162514264337593543950335", ErrorMessage = "نرخ نمی‌تواند منفی باشد.")]
    public decimal? DefaultExternalRateUsd { get; set; }

    [Display(Name = "فعال")]
    public bool IsActive { get; set; } = true;

    [Display(Name = "یادداشت")]
    [StringLength(1000)]
    public string? Notes { get; set; }
}

public sealed class AssetOwnershipShareCreateViewModel
{
    public int OperationalAssetId { get; set; }

    [Display(Name = "نوع مالک")]
    public AssetOwnerType OwnerType { get; set; } = AssetOwnerType.Company;

    [Display(Name = "شرکت")]
    public int? CompanyId { get; set; }

    [Display(Name = "شریک")]
    public int? PartnerId { get; set; }

    [Display(Name = "نام مالک")]
    [StringLength(200)]
    public string? OwnerName { get; set; }

    [Display(Name = "درصد سهم")]
    [Range(typeof(decimal), "0.0001", "100", ErrorMessage = "درصد سهم باید بین 0 و 100 باشد.")]
    public decimal SharePercent { get; set; }

    [Display(Name = "از تاریخ")]
    [DataType(DataType.Date)]
    public DateTime EffectiveFrom { get; set; } = AfghanistanBusinessClock.SystemToday;

    [Display(Name = "تا تاریخ")]
    [DataType(DataType.Date)]
    public DateTime? EffectiveTo { get; set; }

    [Display(Name = "یادداشت")]
    [StringLength(1000)]
    public string? Notes { get; set; }
}

public sealed class AssetRentCreateViewModel
{
    public int Id { get; set; }

    [Display(Name = "دارایی عملیاتی")]
    [Range(1, int.MaxValue, ErrorMessage = "انتخاب دارایی عملیاتی الزامی است.")]
    public int OperationalAssetId { get; set; }

    [Display(Name = "تاریخ کرایه / استفاده")]
    [DataType(DataType.Date)]
    public DateTime RentDate { get; set; } = AfghanistanBusinessClock.SystemToday;

    [Display(Name = "نوع استفاده")]
    public AssetRentUsageType UsageType { get; set; } = AssetRentUsageType.InternalCompanyUse;

    [Display(Name = "طرف حساب")]
    public AssetRentChargedToType ChargedToType { get; set; } = AssetRentChargedToType.CompanyInternal;

    [Display(Name = "قرارداد")]
    public int? ChargedToContractId { get; set; }

    [Display(Name = "مشتری")]
    public int? ChargedToCustomerId { get; set; }

    [Display(Name = "نام مشتری جدید")]
    [StringLength(200)]
    public string? NewCustomerName { get; set; }

    [Display(Name = "شرکت")]
    public int? ChargedToCompanyId { get; set; }

    [Display(Name = "شریک")]
    public int? ChargedToPartnerId { get; set; }

    [Display(Name = "شرکت خدماتی")]
    public int? ChargedToServiceProviderId { get; set; }

    [Display(Name = "مقدار MT")]
    [Range(typeof(decimal), "0", "79228162514264337593543950335", ErrorMessage = "مقدار نمی‌تواند منفی باشد.")]
    public decimal? QuantityMt { get; set; }

    [Display(Name = "مسافت KM")]
    [Range(typeof(decimal), "0", "79228162514264337593543950335", ErrorMessage = "مسافت نمی‌تواند منفی باشد.")]
    public decimal? DistanceKm { get; set; }

    [Display(Name = "روز")]
    [Range(typeof(decimal), "0", "79228162514264337593543950335", ErrorMessage = "روز نمی‌تواند منفی باشد.")]
    public decimal? Days { get; set; }

    [Display(Name = "نرخ")]
    [Range(typeof(decimal), "0.0001", "79228162514264337593543950335", ErrorMessage = "نرخ باید بزرگتر از صفر باشد.")]
    public decimal Rate { get; set; }

    [Display(Name = "مبلغ")]
    [Range(typeof(decimal), "0", "79228162514264337593543950335", ErrorMessage = "مبلغ نمی‌تواند منفی باشد.")]
    public decimal? AmountOriginal { get; set; }

    [Display(Name = "ارز")]
    [Required]
    [StringLength(10)]
    public string Currency { get; set; } = "USD";

    [Display(Name = "نرخ تبدیل به USD")]
    [Range(typeof(decimal), "0.000001", "79228162514264337593543950335", ErrorMessage = "نرخ تبدیل باید بزرگتر از صفر باشد.")]
    public decimal? FxRateToUsd { get; set; } = 1m;

    [Display(Name = "مرجع")]
    [StringLength(200)]
    public string? ReferenceDocument { get; set; }

    [Display(Name = "یادداشت")]
    [StringLength(1000)]
    public string? Description { get; set; }
}

public sealed class OperationalAssetProfileViewModel
{
    public int Id { get; init; }
    public string AssetCode { get; init; } = "";
    public string Name { get; init; } = "";
    public OperationalAssetType AssetType { get; init; }
    public string AssetTypeName => OperationalAssetLabels.AssetType(AssetType);
    public string? LinkedResourceText { get; init; }
    public OperationalAssetOwnershipMode OwnershipMode { get; init; }
    public string OwnershipModeName => OperationalAssetLabels.OwnershipMode(OwnershipMode);
    public decimal? CapacityMt { get; init; }
    public string? LocationName { get; init; }
    public string? TerminalName { get; init; }
    public DateTime? AcquisitionDate { get; init; }
    public decimal? AcquisitionCostUsd { get; init; }
    public DateTime? InServiceDate { get; init; }
    public DateTime? DisposalDate { get; init; }
    public OperationalAssetStatus OperationalStatus { get; init; }
    public decimal MonthlyDepreciationUsd { get; init; }
    public decimal? DefaultInternalRateUsd { get; init; }
    public decimal? DefaultExternalRateUsd { get; init; }
    public bool IsActive { get; init; }
    public string? Notes { get; init; }
    public DateTime FromDate { get; init; }
    public DateTime ToDate { get; init; }
    public decimal InternalRentUsd { get; init; }
    public decimal ExternalRentUsd { get; init; }
    public decimal TotalRentUsd => InternalRentUsd + ExternalRentUsd;
    // کرایهٔ حمل/رسید که با دارایی خودِ شرکت انجام شده — برای دارایی درآمد است نه مصرف.
    public decimal FreightIncomeUsd { get; init; }
    public decimal DirectExpensesUsd { get; init; }
    public decimal DepreciationUsd { get; init; }
    public decimal NetResultUsd => TotalRentUsd + FreightIncomeUsd - DirectExpensesUsd - DepreciationUsd;
    public IReadOnlyList<AssetOwnershipShareRowViewModel> OwnershipShares { get; init; } = [];
    public IReadOnlyList<AssetAssignmentRowViewModel> Assignments { get; init; } = [];
    public IReadOnlyList<AssetMaintenanceJobRowViewModel> MaintenanceJobs { get; init; } = [];
    public IReadOnlyList<AssetMeterReadingRowViewModel> MeterReadings { get; init; } = [];
    public IReadOnlyList<AssetDocumentRowViewModel> Documents { get; init; } = [];
    public IReadOnlyList<AssetRentRowViewModel> RentTransactions { get; init; } = [];
    public IReadOnlyList<AssetExpenseRowViewModel> Expenses { get; init; } = [];
    public IReadOnlyList<AssetRentShareRowViewModel> RentShares { get; init; } = [];

    /// <summary>«کارکرد» — عملیات‌هایی که این دارایی در آن‌ها کار کرده است. بدون مبلغ.</summary>
    public IReadOnlyList<AssetWorkRowViewModel> WorkRows { get; init; } = [];

    /// <summary>«مصارف» — فقط هزینه‌های دارایی؛ ردیف‌های عایداتی از این لیست بیرون‌اند.</summary>
    public IReadOnlyList<AssetExpenseRowViewModel> CostRows { get; init; } = [];

    /// <summary>«عواید» گروه اول: استفادهٔ خود شرکت از دارایی (پرداخت بیرونی ندارد).</summary>
    public IReadOnlyList<AssetIncomeRowViewModel> InternalIncomeRows { get; init; } = [];

    /// <summary>«عواید» گروه دوم: کرایه دادن دارایی به بیرون (طلب واقعی از طرف بیرونی).</summary>
    public IReadOnlyList<AssetIncomeRowViewModel> ExternalIncomeRows { get; init; } = [];

    /// <summary>جمع درصد سهم مالکینی که امروز فعال‌اند؛ برای هشدار «مجموع ۱۰۰٪ نیست».</summary>
    public decimal ActiveOwnershipPercent { get; init; }
    public AssetOwnershipShareCreateViewModel NewOwnershipShare { get; init; } = new();
    public AssetAssignmentCreateViewModel NewAssignment { get; init; } = new();
    public AssetMaintenanceJobCreateViewModel NewMaintenanceJob { get; init; } = new();
    public AssetMeterReadingCreateViewModel NewMeterReading { get; init; } = new();
    public AssetDocumentCreateViewModel NewDocument { get; init; } = new();
    public AssetRentCreateViewModel NewRent { get; init; } = new();
}

public sealed class AssetAssignmentCreateViewModel
{
    public int OperationalAssetId { get; set; }
    [Required, Display(Name = "مسئول")]
    public string ResponsiblePartyKey { get; set; } = "";
    [Display(Name = "راننده")]
    public int? DriverId { get; set; }
    [Display(Name = "ترمینال پایه")]
    public int? BaseTerminalId { get; set; }
    [Required, StringLength(100), Display(Name = "نقش")]
    public string Role { get; set; } = "مسئول اصلی";
    [DataType(DataType.Date), Display(Name = "از تاریخ")]
    public DateTime FromDate { get; set; } = AfghanistanBusinessClock.SystemToday;
    [StringLength(1000), Display(Name = "یادداشت")]
    public string? Notes { get; set; }
}

public sealed class AssetMaintenanceJobCreateViewModel
{
    public int OperationalAssetId { get; set; }
    public AssetMaintenanceJobType JobType { get; set; } = AssetMaintenanceJobType.Service;
    public AssetMaintenanceStatus Status { get; set; } = AssetMaintenanceStatus.Planned;
    [Required, StringLength(200)] public string Title { get; set; } = "";
    [DataType(DataType.Date)] public DateTime? ScheduledDate { get; set; }
    [DataType(DataType.Date)] public DateTime? StartedDate { get; set; }
    [DataType(DataType.Date)] public DateTime? CompletedDate { get; set; }
    [DataType(DataType.Date)] public DateTime? DowntimeFrom { get; set; }
    [DataType(DataType.Date)] public DateTime? DowntimeTo { get; set; }
    public int? ExpenseTransactionId { get; set; }
    [StringLength(1000)] public string? Notes { get; set; }
}

public sealed class AssetMeterReadingCreateViewModel
{
    public int OperationalAssetId { get; set; }
    public AssetMeterType MeterType { get; set; } = AssetMeterType.OdometerKm;
    [DataType(DataType.Date)] public DateTime ReadingDate { get; set; } = AfghanistanBusinessClock.SystemToday;
    [Range(typeof(decimal), "0", "79228162514264337593543950335")] public decimal ReadingValue { get; set; }
    [StringLength(200)] public string? Reference { get; set; }
    [StringLength(1000)] public string? Notes { get; set; }
}

public sealed class AssetDocumentCreateViewModel
{
    public int OperationalAssetId { get; set; }
    public AssetDocumentType DocumentType { get; set; } = AssetDocumentType.Other;
    [StringLength(200)] public string? DocumentNumber { get; set; }
    [DataType(DataType.Date)] public DateTime? IssueDate { get; set; }
    [DataType(DataType.Date)] public DateTime? ExpiryDate { get; set; }
    public IFormFile? File { get; set; }
    [StringLength(1000)] public string? Notes { get; set; }
}

public sealed class AssetAssignmentRowViewModel
{
    public int Id { get; init; }
    public string ResponsibleName { get; init; } = "";
    public string Role { get; init; } = "";
    public string? DriverName { get; init; }
    public string? BaseTerminalName { get; init; }
    public DateTime FromDate { get; init; }
    public DateTime? ToDate { get; init; }
    public string? Notes { get; init; }
    public bool IsCurrent => !ToDate.HasValue;
}

public sealed class AssetMaintenanceJobRowViewModel
{
    public int Id { get; init; }
    public AssetMaintenanceJobType JobType { get; init; }
    public AssetMaintenanceStatus Status { get; init; }
    public string Title { get; init; } = "";
    public DateTime? ScheduledDate { get; init; }
    public DateTime? StartedDate { get; init; }
    public DateTime? CompletedDate { get; init; }
    public DateTime? DowntimeFrom { get; init; }
    public DateTime? DowntimeTo { get; init; }
    public int? ExpenseTransactionId { get; init; }
    public string? Notes { get; init; }
}

public sealed class AssetMeterReadingRowViewModel
{
    public int Id { get; init; }
    public AssetMeterType MeterType { get; init; }
    public DateTime ReadingDate { get; init; }
    public decimal ReadingValue { get; init; }
    public string? Reference { get; init; }
}

public sealed class AssetDocumentRowViewModel
{
    public int Id { get; init; }
    public AssetDocumentType DocumentType { get; init; }
    public string? DocumentNumber { get; init; }
    public DateTime? IssueDate { get; init; }
    public DateTime? ExpiryDate { get; init; }
    public string OriginalFileName { get; init; } = "";
    public bool IsExpired { get; init; }
    public bool ExpiresSoon { get; init; }
    public string? Notes { get; init; }
}

public sealed class AssetOwnershipShareRowViewModel
{
    public int Id { get; init; }
    public AssetOwnerType OwnerType { get; init; }
    public string OwnerTypeName => OperationalAssetLabels.OwnerType(OwnerType);
    public string OwnerName { get; init; } = "";
    public decimal SharePercent { get; init; }
    public DateTime EffectiveFrom { get; init; }
    public DateTime? EffectiveTo { get; init; }
    public string? Notes { get; init; }
    public bool IsActiveNow { get; init; }
}

public sealed class AssetRentRowViewModel
{
    public int Id { get; init; }
    public DateTime RentDate { get; init; }
    public AssetRentUsageType UsageType { get; init; }
    public string UsageTypeName => OperationalAssetLabels.UsageType(UsageType);
    public AssetRentChargedToType ChargedToType { get; init; }
    public string ChargedToTypeName => OperationalAssetLabels.ChargedToType(ChargedToType);
    public string ChargedToName { get; init; } = "";

    /// <summary>سندی که این ردیف را ساخته است — برای ردیف‌های دستی خالی می‌ماند.</summary>
    public AssetSourceLinkViewModel? Source { get; init; }
    public string? ReferenceDocument { get; init; }
    public decimal? QuantityMt { get; init; }
    public decimal? DistanceKm { get; init; }
    public decimal? Days { get; init; }

    // مبلغِ ثبت‌شده به ارز خودش و مبلغِ ارز پایه جدا نگه داشته می‌شوند تا در جدول جای هم را نگیرند.
    public decimal AmountOriginal { get; init; }
    public string Currency { get; init; } = "";
    public decimal FxRateToUsd { get; init; }
    public decimal AmountUsd { get; init; }
    public string? Description { get; init; }
    public bool IsPostedToLedger { get; init; }

    /// <summary>کرایه‌ای که یکی از جریان‌های عملیاتی (بارگیری) ساخته، نه کاربر.</summary>
    public bool IsSystemGenerated { get; init; }

    /// <summary>
    /// دلیلِ نداشتنِ اثر مالی طبق <c>AssetRentPostingPolicy</c>، یا <c>null</c> اگر این کرایه باید
    /// ردیف لجر داشته باشد. با همین مقدار «ثبت‌نشدهٔ عادی» از «ثبت‌نشدهٔ مشکل‌دار» تفکیک می‌شود.
    /// </summary>
    public string? PostingSkipReason { get; init; }

    /// <summary>این کرایه اصلاً قرار نیست اثر مالی بگیرد؛ «ثبت‌نشده» برایش وضعیت درست است نه خطا.</summary>
    public bool IsNonFinancial => PostingSkipReason is not null;

    /// <summary>باید ثبت می‌شد ولی نشده — تنها حالتی که در جدول هشدار می‌گیرد.</summary>
    public bool IsPostingMissing => PostingSkipReason is null && !IsPostedToLedger;
}

/// <summary>
/// لینکِ سندِ منبعِ یک ردیفِ خودکار. کاربر باید همیشه بتواند بپرسد «این رکورد از کجا آمده؟»
/// و با یک کلیک به همان سند برود؛ <see cref="Url"/> فقط وقتی خالی است که صفحهٔ آن سند وجود ندارد.
/// </summary>
public sealed class AssetSourceLinkViewModel
{
    /// <summary>نوع سند به زبان کاربر، مثلاً «بارگیری» یا «ارسال با موتر».</summary>
    public string DocumentTypeName { get; init; } = "";
    public int DocumentId { get; init; }

    /// <summary>متن کامل ردیف، مثلاً «ایجادشده از بارگیری #1042».</summary>
    public string Label { get; init; } = "";
    public string? Url { get; init; }
}

/// <summary>
/// یک ردیفِ «کارکرد»: این دارایی در کدام عملیات و با چه مقداری کار کرده است.
/// عمداً هیچ مبلغی ندارد — پول در بخش «عواید» و «مصارف» نشان داده می‌شود.
/// </summary>
public sealed class AssetWorkRowViewModel
{
    public DateTime Date { get; init; }
    public string OperationTypeName { get; init; } = "";
    public AssetSourceLinkViewModel? Source { get; init; }
    public string? ContractNumber { get; init; }
    public string? ShipmentCode { get; init; }
    public decimal? QuantityMt { get; init; }
    public decimal? DistanceKm { get; init; }
    public string? RouteText { get; init; }
    public string? CounterpartyName { get; init; }

    /// <summary>استفادهٔ خود شرکت (نه کرایه به بیرون).</summary>
    public bool IsInternalUse { get; init; }
    public string UsageText { get; init; } = "";
    public string? UsageHint { get; init; }
}

/// <summary>
/// یک ردیفِ «عواید» — چه از کرایهٔ ثبت‌شده و چه از کرایهٔ حملی که با دارایی خود شرکت انجام شده.
/// وضعیت مالی به زبان ساده در <see cref="StateText"/> می‌آید، نه با کد داخلی سیستم.
/// </summary>
public sealed class AssetIncomeRowViewModel
{
    public int Id { get; init; }
    public DateTime Date { get; init; }
    public string SourceTypeName { get; init; } = "";
    public AssetSourceLinkViewModel? Source { get; init; }
    public string? CounterpartyName { get; init; }
    public string? ContractNumber { get; init; }
    public decimal AmountOriginal { get; init; }
    public string Currency { get; init; } = "USD";
    public decimal AmountUsd { get; init; }
    public string StateText { get; init; } = "";

    /// <summary>تنها حالتی که باید مثل هشدار دیده شود: باید در حساب ثبت می‌شد ولی نشده است.</summary>
    public bool NeedsAttention { get; init; }

    /// <summary>ردیف‌های خودکار از سند خودشان لغو می‌شوند، نه از این صفحه.</summary>
    public bool CanCancel { get; init; }
    public string? Description { get; init; }
}

public sealed class AssetExpenseRowViewModel
{
    public int Id { get; init; }
    public DateTime ExpenseDate { get; init; }
    public string ExpenseTypeName { get; init; } = "";
    public AssetSourceLinkViewModel? Source { get; init; }
    public string? ContractNumber { get; init; }
    public string? ShipmentCode { get; init; }
    public string? TransportLegLabel { get; init; }
    public string? TruckDispatchLabel { get; init; }
    public string? ServiceProviderName { get; init; }
    public decimal AmountUsd { get; init; }
    // کرایهٔ حمل/رسید با دارایی خودِ شرکت = درآمد دارایی (نه مصرف).
    public bool IsFreightIncome { get; init; }
    public string? Description { get; init; }
}

public sealed class AssetRentShareRowViewModel
{
    public int RentTransactionId { get; init; }
    public DateTime RentDate { get; init; }
    public string AssetName { get; init; } = "";
    public AssetRentUsageType UsageType { get; init; }
    public string UsageTypeName => OperationalAssetLabels.UsageType(UsageType);
    public AssetOwnerType OwnerType { get; init; }
    public string OwnerTypeName => OperationalAssetLabels.OwnerType(OwnerType);
    public string OwnerName { get; init; } = "";
    public decimal SharePercent { get; init; }
    public decimal ShareAmountUsd { get; init; }
}

public sealed class OperationalAssetProfitabilityFilterViewModel
{
    [Display(Name = "از تاریخ")]
    [DataType(DataType.Date)]
    public DateTime? FromDate { get; set; }

    [Display(Name = "تا تاریخ")]
    [DataType(DataType.Date)]
    public DateTime? ToDate { get; set; }

    [Display(Name = "نوع دارایی")]
    public OperationalAssetType? AssetType { get; set; }

    [Display(Name = "دارایی")]
    public int? OperationalAssetId { get; set; }

    [Display(Name = "نوع استفاده")]
    public AssetRentUsageType? UsageType { get; set; }

    [Display(Name = "شریک")]
    public int? PartnerId { get; set; }

    [Display(Name = "قرارداد")]
    public int? ContractId { get; set; }

    [Display(Name = "مشتری")]
    public int? CustomerId { get; set; }
}

public sealed class OperationalAssetProfitabilityViewModel
{
    public OperationalAssetProfitabilityFilterViewModel Filter { get; init; } = new();
    public IReadOnlyList<OperationalAssetProfitabilityRowViewModel> Rows { get; init; } = [];
    public IReadOnlyList<AssetRentShareRowViewModel> OwnerShareRows { get; init; } = [];
    public decimal TotalInternalRentUsd => Rows.Sum(r => r.InternalRentUsd);
    public decimal TotalExternalRentUsd => Rows.Sum(r => r.ExternalRentUsd);
    public decimal TotalFreightIncomeUsd => Rows.Sum(r => r.FreightIncomeUsd);
    public decimal TotalDirectExpensesUsd => Rows.Sum(r => r.DirectExpensesUsd);
    public decimal TotalDepreciationUsd => Rows.Sum(r => r.DepreciationUsd);
    public decimal TotalNetResultUsd => Rows.Sum(r => r.NetResultUsd);
}

public sealed class OperationalAssetProfitabilityRowViewModel
{
    public int OperationalAssetId { get; init; }
    public string AssetCode { get; init; } = "";
    public string AssetName { get; init; } = "";
    public OperationalAssetType AssetType { get; init; }
    public string AssetTypeName => OperationalAssetLabels.AssetType(AssetType);
    public int UsageCount { get; init; }
    public decimal QuantityMt { get; init; }
    public decimal DistanceKm { get; init; }
    public decimal Days { get; init; }
    public decimal InternalRentUsd { get; init; }
    public decimal ExternalRentUsd { get; init; }
    public decimal TotalRentUsd => InternalRentUsd + ExternalRentUsd;
    // کرایهٔ حمل/رسید که با دارایی خودِ شرکت انجام شده — برای دارایی درآمد است نه مصرف.
    public decimal FreightIncomeUsd { get; init; }
    public decimal DirectExpensesUsd { get; init; }
    public decimal DepreciationUsd { get; init; }
    public decimal NetResultUsd => TotalRentUsd + FreightIncomeUsd - DirectExpensesUsd - DepreciationUsd;
}

public static class OperationalAssetLabels
{
    public static string Status(OperationalAssetStatus status, HttpContext? context)
        => UiText.IsEn(context)
            ? status switch
            {
                OperationalAssetStatus.Planned => "Planned",
                OperationalAssetStatus.Active => "Active",
                OperationalAssetStatus.UnderMaintenance => "Under maintenance",
                OperationalAssetStatus.OutOfService => "Out of service",
                OperationalAssetStatus.Disposed => "Disposed",
                _ => "Unknown"
            }
            : status switch
            {
                OperationalAssetStatus.Planned => "برنامه‌ریزی‌شده",
                OperationalAssetStatus.Active => "فعال",
                OperationalAssetStatus.UnderMaintenance => "زیر ترمیم",
                OperationalAssetStatus.OutOfService => "خارج از کار",
                OperationalAssetStatus.Disposed => "واگذار/اسقاط‌شده",
                _ => "نامشخص"
            };

    public static string AssetType(OperationalAssetType type)
        => type switch
        {
            OperationalAssetType.Truck => "Truck",
            OperationalAssetType.Trailer => "Trailer",
            OperationalAssetType.TankerTruck => "Tanker Truck",
            OperationalAssetType.StorageTank => "Storage Tank",
            OperationalAssetType.Warehouse => "Warehouse",
            OperationalAssetType.Terminal => "Property",
            OperationalAssetType.Wagon => "Wagon",
            _ => "Other"
        };

    public static string AssetType(OperationalAssetType type, HttpContext? context)
        => UiText.IsEn(context)
            ? AssetType(type)
            : type switch
            {
                OperationalAssetType.Truck => "موتر",
                OperationalAssetType.Trailer => "تریلر",
                OperationalAssetType.TankerTruck => "تانکر موتر",
                OperationalAssetType.StorageTank => "مخزن ذخیره",
                OperationalAssetType.Warehouse => "گدام",
                OperationalAssetType.Terminal => "املاک",
                OperationalAssetType.Wagon => "واگن",
                _ => "سایر"
            };

    public static string OwnershipMode(OperationalAssetOwnershipMode mode)
        => mode switch
        {
            OperationalAssetOwnershipMode.FullyCompanyOwned => "Fully Company Owned",
            OperationalAssetOwnershipMode.PartnerOwned => "Partner Owned",
            OperationalAssetOwnershipMode.SharedOwnership => "Shared Ownership",
            OperationalAssetOwnershipMode.LeasedButOperated => "Leased But Operated",
            _ => "Other"
        };

    public static string OwnershipMode(OperationalAssetOwnershipMode mode, HttpContext? context)
        => UiText.IsEn(context)
            ? OwnershipMode(mode)
            : mode switch
            {
                OperationalAssetOwnershipMode.FullyCompanyOwned => "ملکیت کامل شرکت",
                OperationalAssetOwnershipMode.PartnerOwned => "ملکیت شریک",
                OperationalAssetOwnershipMode.SharedOwnership => "ملکیت مشترک",
                OperationalAssetOwnershipMode.LeasedButOperated => "کرایی اما تحت عملیات شرکت",
                _ => "سایر"
            };

    public static string OwnerType(AssetOwnerType type)
        => type switch
        {
            AssetOwnerType.Company => "Company",
            AssetOwnerType.Partner => "Partner",
            AssetOwnerType.ExternalOwner => "External Owner",
            _ => "Other"
        };

    public static string OwnerType(AssetOwnerType type, HttpContext? context)
        => UiText.IsEn(context)
            ? OwnerType(type)
            : type switch
            {
                AssetOwnerType.Company => "شرکت",
                AssetOwnerType.Partner => "شریک",
                AssetOwnerType.ExternalOwner => "مالک بیرونی",
                _ => "سایر"
            };

    public static string UsageType(AssetRentUsageType type)
        => type switch
        {
            AssetRentUsageType.InternalCompanyUse => "Internal Company Use",
            AssetRentUsageType.ExternalCustomerRental => "External Customer Rental",
            AssetRentUsageType.PartnerUse => "Partner Use",
            _ => "Other"
        };

    public static string UsageType(AssetRentUsageType type, HttpContext? context)
        => UiText.IsEn(context)
            ? UsageType(type)
            : type switch
            {
                AssetRentUsageType.InternalCompanyUse => "استفاده داخلی شرکت",
                AssetRentUsageType.ExternalCustomerRental => "کرایه بیرونی",
                AssetRentUsageType.PartnerUse => "استفاده شریک",
                _ => "سایر"
            };

    public static string ChargedToType(AssetRentChargedToType type)
        => type switch
        {
            AssetRentChargedToType.PurchaseContract => "Purchase Contract",
            AssetRentChargedToType.SalesContract => "Sales Contract",
            AssetRentChargedToType.Customer => "Customer",
            AssetRentChargedToType.CompanyInternal => "Company Internal",
            AssetRentChargedToType.Partner => "Partner",
            _ => "Service Company"
        };

    public static string ChargedToType(AssetRentChargedToType type, HttpContext? context)
        => UiText.IsEn(context)
            ? ChargedToType(type)
            : type switch
            {
                AssetRentChargedToType.PurchaseContract => "قرارداد خرید",
                AssetRentChargedToType.SalesContract => "قرارداد فروش",
                AssetRentChargedToType.Customer => "مشتری",
                AssetRentChargedToType.CompanyInternal => "داخلی شرکت",
                AssetRentChargedToType.Partner => "شریک",
                _ => "شرکت خدماتی"
            };

    /// <summary>
    /// وضعیت مالیِ یک ردیف عواید به زبان کاربر. کدهای داخلی سیاست ثبت
    /// (<c>AssetRentPostingPolicy</c>) هرگز به صفحه نمی‌روند؛ فقط ترجمهٔ ساده‌شان.
    /// </summary>
    public static string PostingState(bool isPostedToLedger, string? skipReason, HttpContext? context)
    {
        var en = UiText.IsEn(context);
        if (isPostedToLedger)
        {
            return en ? "Recorded in accounts" : "در حساب ثبت شده";
        }

        return skipReason switch
        {
            null => en ? "Not recorded in accounts yet" : "هنوز در حساب ثبت نشده",
            Services.AssetRentPostingPolicy.SkipCancelled => en ? "Cancelled" : "لغو شده",
            Services.AssetRentPostingPolicy.SkipSystemGenerated or Services.AssetRentPostingPolicy.SkipInternalUse =>
                en ? "Company internal use — no outside payment" : "استفاده داخلی شرکت — پرداخت بیرونی ندارد",
            Services.AssetRentPostingPolicy.SkipPartnerUnsupported =>
                en ? "Partner share — kept outside the accounts for now" : "سهم شریک — فعلاً در حساب ثبت نمی‌شود",
            Services.AssetRentPostingPolicy.SkipCounterpartyUnresolved =>
                en ? "Counterparty is not selected" : "طرف حساب مشخص نیست",
            Services.AssetRentPostingPolicy.SkipInvalidAmount =>
                en ? "Amount is not valid" : "مبلغ معتبر نیست",
            _ => en ? "No outside payment" : "پرداخت بیرونی ندارد"
        };
    }
}
