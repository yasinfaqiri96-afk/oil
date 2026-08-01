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
    public IReadOnlyList<AssetRentRowViewModel> RentTransactions { get; init; } = [];
    public IReadOnlyList<AssetExpenseRowViewModel> Expenses { get; init; } = [];
    public IReadOnlyList<AssetRentShareRowViewModel> RentShares { get; init; } = [];
    public AssetOwnershipShareCreateViewModel NewOwnershipShare { get; init; } = new();
    public AssetRentCreateViewModel NewRent { get; init; } = new();
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
    public string? ReferenceDocument { get; init; }
    public decimal? QuantityMt { get; init; }
    public decimal? DistanceKm { get; init; }
    public decimal? Days { get; init; }
    public decimal AmountUsd { get; init; }
    public string? Description { get; init; }
    public bool IsPostedToLedger { get; init; }
}

public sealed class AssetExpenseRowViewModel
{
    public int Id { get; init; }
    public DateTime ExpenseDate { get; init; }
    public string ExpenseTypeName { get; init; } = "";
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
}
