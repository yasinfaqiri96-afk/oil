using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http;
using PTGOilSystem.Web.Helpers;
using PTGOilSystem.Web.Models.Entities;

namespace PTGOilSystem.Web.Models.ServiceProviders;

public static class ServiceProviderTypeLabels
{
    public static string ToDisplay(ServiceProviderType type) => type switch
    {
        ServiceProviderType.RailwayService => "Railway Service",
        ServiceProviderType.WagonRent => "Wagon Rent",
        ServiceProviderType.StorageProvider => "Storage Provider",
        ServiceProviderType.TerminalOperator => "Terminal Operator",
        ServiceProviderType.TransportCompany => "Transport Company",
        ServiceProviderType.CustomsBroker => "Customs Broker",
        ServiceProviderType.LoadingUnloadingService => "Loading / Unloading",
        ServiceProviderType.InspectionService => "Inspection Service",
        ServiceProviderType.DocumentationService => "Documentation Service",
        ServiceProviderType.Other => "Other",
        _ => type.ToString()
    };

    public static string ToDisplay(ServiceProviderType type, HttpContext? context)
        => UiText.IsEn(context)
            ? ToDisplay(type)
            : type switch
            {
                ServiceProviderType.RailwayService => "خدمات راه‌آهن",
                ServiceProviderType.WagonRent => "کرایه واگن",
                ServiceProviderType.StorageProvider => "ارائه‌دهنده ذخیره‌سازی",
                ServiceProviderType.TerminalOperator => "اپراتور ترمینال",
                ServiceProviderType.TransportCompany => "شرکت ترانسپورتی",
                ServiceProviderType.CustomsBroker => "کارگزار گمرکی",
                ServiceProviderType.LoadingUnloadingService => "خدمات بارگیری / تخلیه",
                ServiceProviderType.InspectionService => "خدمات بازرسی",
                ServiceProviderType.DocumentationService => "خدمات اسناد",
                ServiceProviderType.Other => "سایر",
                _ => type.ToString()
            };
}

public sealed class ServiceProviderCreateViewModel
{
    public int Id { get; set; }

    [Display(Name = "کد")]
    [StringLength(50)]
    public string? Code { get; set; }

    [Display(Name = "نام")]
    [Required(ErrorMessage = "نام الزامی است.")]
    [StringLength(200)]
    public string Name { get; set; } = string.Empty;

    [Display(Name = "نوع خدمت")]
    public ServiceProviderType ProviderType { get; set; } = ServiceProviderType.Other;

    [Display(Name = "کشور")]
    [StringLength(80)]
    public string? Country { get; set; }

    [Display(Name = "شهر")]
    [StringLength(120)]
    public string? City { get; set; }

    [Display(Name = "تلفن")]
    [StringLength(50)]
    public string? Phone { get; set; }

    [Display(Name = "ایمیل")]
    [EmailAddress(ErrorMessage = "فرمت ایمیل معتبر نیست.")]
    [StringLength(150)]
    public string? Email { get; set; }

    [Display(Name = "آدرس")]
    [StringLength(300)]
    public string? Address { get; set; }

    [Display(Name = "شماره مالیاتی / ثبت")]
    [StringLength(100)]
    public string? TaxNumber { get; set; }

    [Display(Name = "یادداشت")]
    [StringLength(1000)]
    public string? Notes { get; set; }

    [Display(Name = "فعال")]
    public bool IsActive { get; set; } = true;
}

public sealed class ServiceProviderIndexItemViewModel
{
    public int Id { get; init; }
    public string? Code { get; init; }
    public string Name { get; init; } = string.Empty;
    public ServiceProviderType ProviderType { get; init; }
    public string ProviderTypeName => ServiceProviderTypeLabels.ToDisplay(ProviderType);
    public string ContactText { get; init; } = "-";
    public decimal TotalExpensesUsd { get; init; }
    public decimal TotalPaymentsUsd { get; init; }
    public decimal LedgerBalanceUsd { get; init; }
    public bool IsActive { get; init; }
}

public sealed class ServiceProviderIndexViewModel
{
    public string? Query { get; init; }
    public IReadOnlyList<ServiceProviderIndexItemViewModel> Items { get; init; } = [];
    public int CurrentPage { get; init; } = 1;
    public int PageCount { get; init; } = 1;
    public int TotalCount { get; init; }
}

public sealed class ServiceProviderStatementRowViewModel
{
    public int LedgerEntryId { get; init; }
    public DateTime EntryDate { get; init; }
    public string SourceType { get; init; } = string.Empty;
    public int SourceId { get; init; }
    public string? Reference { get; init; }
    public string? ContractNumber { get; init; }
    public string Description { get; init; } = string.Empty;
    public decimal? DebitUsd { get; init; }
    public decimal? CreditUsd { get; init; }
    public decimal RunningBalanceUsd { get; init; }

    // مبلغ به ارز اصلی سند + نرخ (فقط نمایش؛ از همان LedgerEntry موجود خوانده می‌شود).
    public string Currency { get; init; } = "USD";
    public decimal? Debit { get; init; }
    public decimal? Credit { get; init; }
    public decimal? FxRateUsed { get; init; }
    public decimal? FxRateDisplayPerUsd =>
        FxRateUsed.HasValue && FxRateUsed.Value > 0m && !string.Equals(Currency, "USD", StringComparison.OrdinalIgnoreCase)
            ? decimal.Round(1m / FxRateUsed.Value, 4, MidpointRounding.AwayFromZero)
            : (decimal?)null;
}

public sealed class ServiceProviderExpenseRowViewModel
{
    public int Id { get; init; }
    public DateTime ExpenseDate { get; init; }
    public string ExpenseTypeName { get; init; } = string.Empty;
    public string? ContractNumber { get; init; }
    public string? ShipmentCode { get; init; }
    public string? TransportLegLabel { get; init; }
    public string? TruckDispatchLabel { get; init; }
    public string? OperationalAssetName { get; init; }
    public decimal AmountUsd { get; init; }
    public string? Description { get; init; }
}

public sealed class ServiceProviderPaymentRowViewModel
{
    public int Id { get; init; }
    public DateTime PaymentDate { get; init; }
    public string PaymentKindName { get; init; } = string.Empty;
    public string CashAccountName { get; init; } = string.Empty;
    public string? ContractNumber { get; init; }
    public decimal AmountUsd { get; init; }
    public string? Reference { get; init; }
}

public sealed class ServiceProviderRelatedContractViewModel
{
    public int Id { get; init; }
    public string ContractNumber { get; init; } = string.Empty;
    public decimal ExpenseUsd { get; init; }
    public decimal PaymentUsd { get; init; }
}

public sealed class ServiceProviderProfileViewModel
{
    public int Id { get; init; }
    public string? Code { get; init; }
    public string Name { get; init; } = string.Empty;
    public ServiceProviderType ProviderType { get; init; }
    public string ProviderTypeName => ServiceProviderTypeLabels.ToDisplay(ProviderType);
    public string? Country { get; init; }
    public string? City { get; init; }
    public string? Phone { get; init; }
    public string? Email { get; init; }
    public string? Address { get; init; }
    public string? TaxNumber { get; init; }
    public string? Notes { get; init; }
    public bool IsActive { get; init; }
    public decimal TotalExpensesUsd { get; init; }
    public decimal TotalPaymentsUsd { get; init; }
    public decimal LedgerDebitUsd { get; init; }
    public decimal LedgerCreditUsd { get; init; }
    // مانده نمایشی مطابق قرارداد صورت‌حساب: Σ(داده − گرفته). هم‌علامتِ
    // PartyStatementSummary.ClosingBalance که صفحهٔ جزئیات به آن fallback می‌کند.
    public decimal LedgerBalanceUsd => LedgerDebitUsd - LedgerCreditUsd;
    // مانده مثبت = پیش‌پرداخت ما، منفی = بدهی ما (هم‌جهت با علامت جدید LedgerBalanceUsd).
    public string BalanceStatus =>
        LedgerBalanceUsd > 0m ? "Prepaid / provider owes us"
        : LedgerBalanceUsd < 0m ? "Payable to provider"
        : "Settled";
    public IReadOnlyList<ServiceProviderExpenseRowViewModel> Expenses { get; init; } = [];
    public IReadOnlyList<ServiceProviderPaymentRowViewModel> Payments { get; init; } = [];
    public IReadOnlyList<ServiceProviderStatementRowViewModel> StatementRows { get; init; } = [];
    public IReadOnlyList<ServiceProviderRelatedContractViewModel> RelatedContracts { get; init; } = [];
}
