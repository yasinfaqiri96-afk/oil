using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.Payments;
using PTGOilSystem.Web.Services.Time;

namespace PTGOilSystem.Web.Models.Employees;

public static class EmployeeTypeLabels
{
    public static string ToPersian(EmployeeType value) => value switch
    {
        EmployeeType.Permanent => "دایمی",
        EmployeeType.Contract => "قراردادی",
        EmployeeType.DailyWorker => "روزانه",
        EmployeeType.Driver => "راننده",
        EmployeeType.OfficeStaff => "اداری",
        EmployeeType.Other => "سایر",
        _ => value.ToString()
    };
}

public static class EmployeeSalaryTypeLabels
{
    public static string ToPersian(EmployeeSalaryType value) => value switch
    {
        EmployeeSalaryType.Monthly => "ماهانه",
        EmployeeSalaryType.Daily => "روزانه",
        EmployeeSalaryType.Hourly => "ساعتی",
        EmployeeSalaryType.FixedContract => "قرارداد ثابت",
        _ => value.ToString()
    };
}

public static class EmployeeSalaryTransactionTypeLabels
{
    public static string ToPersian(EmployeeSalaryTransactionType value) => value switch
    {
        EmployeeSalaryTransactionType.SalaryAccrual => "ثبت معاش دوره",
        EmployeeSalaryTransactionType.SalaryPayment => "پرداخت معاش",
        EmployeeSalaryTransactionType.SalaryAdvance => "برداشت / پیش‌پرداخت",
        EmployeeSalaryTransactionType.SalaryDeduction => "کسر معاش",
        EmployeeSalaryTransactionType.Bonus => "بونس",
        EmployeeSalaryTransactionType.Adjustment => "اصلاحیه",
        _ => value.ToString()
    };

    public static bool RequiresCashAccount(EmployeeSalaryTransactionType value)
        => value is EmployeeSalaryTransactionType.SalaryPayment or EmployeeSalaryTransactionType.SalaryAdvance;

    public static bool RequiresSalaryPeriod(EmployeeSalaryTransactionType value)
        => value == EmployeeSalaryTransactionType.SalaryAccrual;
}

public sealed class EmployeeFinancialSummaryViewModel
{
    public decimal AccruedSalaryUsd { get; init; }
    public decimal PaidSalaryUsd { get; init; }
    public decimal AdvancesUsd { get; init; }
    public decimal DeductionsUsd { get; init; }
    public decimal BonusesUsd { get; init; }
    public decimal AdjustmentsUsd { get; init; }
    public decimal BalanceUsd => AccruedSalaryUsd + BonusesUsd + AdjustmentsUsd - PaidSalaryUsd - AdvancesUsd - DeductionsUsd;
}

public sealed class EmployeeIndexFilterViewModel
{
    [Display(Name = "جستجو")]
    [StringLength(200)]
    public string? Query { get; set; }

    [Display(Name = "نوع کارمند")]
    public EmployeeType? EmployeeType { get; set; }

    [Display(Name = "نوع معاش")]
    public EmployeeSalaryType? SalaryType { get; set; }

    [Display(Name = "وظیفه / دپارتمان")]
    [StringLength(150)]
    public string? Department { get; set; }

    [Display(Name = "وضعیت")]
    public bool? IsActive { get; set; }

    [Display(Name = "ارز")]
    [StringLength(10)]
    public string? Currency { get; set; }
}

public sealed class EmployeeIndexItemViewModel
{
    public int Id { get; init; }
    public string EmployeeCode { get; init; } = "";
    public string FullName { get; init; } = "";
    public string? Phone { get; init; }
    public string? PhotoPath { get; init; }
    public string? JobTitle { get; init; }
    public string? Department { get; init; }
    public EmployeeType EmployeeType { get; init; }
    public string EmployeeTypeName { get; init; } = "";
    public EmployeeSalaryType SalaryType { get; init; }
    public string SalaryTypeName { get; init; } = "";
    public decimal BaseSalaryAmount { get; init; }
    public string SalaryCurrency { get; init; } = "USD";
    public bool IsActive { get; init; }
    public decimal BalanceUsd { get; init; }
}

public sealed class EmployeeIndexViewModel
{
    public EmployeeIndexFilterViewModel Filter { get; init; } = new();
    public IReadOnlyList<EmployeeIndexItemViewModel> Items { get; init; } = [];
    public int CurrentPage { get; init; } = 1;
    public int PageCount { get; init; } = 1;
    public int TotalCount { get; init; }
}

public sealed class EmployeeFormViewModel
{
    public int Id { get; set; }

    [Display(Name = "کد کارمند")]
    [Required(ErrorMessage = "کد کارمند الزامی است.")]
    [StringLength(50)]
    public string EmployeeCode { get; set; } = "";

    [Display(Name = "نام کامل")]
    [Required(ErrorMessage = "نام کامل الزامی است.")]
    [StringLength(200)]
    public string FullName { get; set; } = "";

    [Display(Name = "نام پدر")]
    [StringLength(200)]
    public string? FatherName { get; set; }

    [Display(Name = "تلفن")]
    [StringLength(50)]
    public string? Phone { get; set; }

    [Display(Name = "ایمیل")]
    [StringLength(200)]
    public string? Email { get; set; }

    [Display(Name = "عکس کارمند")]
    public IFormFile? PhotoFile { get; set; }

    public string? PhotoPath { get; set; }

    [Display(Name = "تذکره / نمبر ملی")]
    [StringLength(100)]
    public string? NationalId { get; set; }

    [Display(Name = "آدرس")]
    [StringLength(1000)]
    public string? Address { get; set; }

    [Display(Name = "وظیفه")]
    [StringLength(150)]
    public string? JobTitle { get; set; }

    [Display(Name = "دپارتمان")]
    [StringLength(150)]
    public string? Department { get; set; }

    [Display(Name = "نوع کارمند")]
    public EmployeeType EmployeeType { get; set; } = EmployeeType.Permanent;

    [Display(Name = "نوع معاش")]
    public EmployeeSalaryType SalaryType { get; set; } = EmployeeSalaryType.Monthly;

    [Display(Name = "معاش پایه")]
    [Range(typeof(decimal), "0", "79228162514264337593543950335", ErrorMessage = "معاش پایه نمی‌تواند منفی باشد.")]
    public decimal BaseSalaryAmount { get; set; }

    [Display(Name = "ارز معاش")]
    [Required(ErrorMessage = "ارز معاش الزامی است.")]
    [StringLength(10)]
    public string SalaryCurrency { get; set; } = "USD";

    [Display(Name = "تاریخ شروع کار")]
    [DataType(DataType.Date)]
    public DateTime HireDate { get; set; } = AfghanistanBusinessClock.SystemToday;

    [Display(Name = "تاریخ ختم کار")]
    [DataType(DataType.Date)]
    public DateTime? EndDate { get; set; }

    [Display(Name = "فعال")]
    public bool IsActive { get; set; } = true;

    [Display(Name = "یادداشت")]
    [StringLength(2000)]
    public string? Notes { get; set; }
}

public sealed class EmployeeSalaryTransactionListItemViewModel
{
    public int Id { get; init; }
    public int EmployeeId { get; init; }
    public string EmployeeName { get; init; } = "";
    public string EmployeeCode { get; init; } = "";
    public DateTime TransactionDate { get; init; }
    public EmployeeSalaryTransactionType TransactionType { get; init; }
    public string TransactionTypeName { get; init; } = "";
    public decimal Amount { get; init; }
    public string Currency { get; init; } = "USD";
    public decimal AmountUsd { get; init; }
    public decimal? AppliedFxRateToUsd { get; init; }
    public string? CashAccountName { get; init; }
    public int? PaymentTransactionId { get; init; }
    public int? LedgerEntryId { get; init; }
    public string? Reference { get; init; }
    public string? Description { get; init; }
    public int? SalaryPeriodYear { get; init; }
    public int? SalaryPeriodMonth { get; init; }
    public bool IsCancelled { get; init; }
    public string? CancellationReason { get; init; }
    public DateTime CreatedAtUtc { get; init; }
    public int? CreatedByUserId { get; init; }

    public string PeriodText => SalaryPeriodYear.HasValue && SalaryPeriodMonth.HasValue
        ? $"{SalaryPeriodYear.Value:0000}/{SalaryPeriodMonth.Value:00}"
        : "—";
}

public sealed class EmployeeAuditItemViewModel
{
    public DateTime ActionAtUtc { get; init; }
    public string Action { get; init; } = "";
    public string? ActorUsername { get; init; }
    public string? Description { get; init; }
    public string? Diff { get; init; }
}

public sealed class EmployeeDetailsViewModel
{
    public int Id { get; init; }
    public string EmployeeCode { get; init; } = "";
    public string FullName { get; init; } = "";
    public string? FatherName { get; init; }
    public string? Phone { get; init; }
    public string? Email { get; init; }
    public string? PhotoPath { get; init; }
    public string? NationalId { get; init; }
    public string? Address { get; init; }
    public string? JobTitle { get; init; }
    public string? Department { get; init; }
    public EmployeeType EmployeeType { get; init; }
    public string EmployeeTypeName { get; init; } = "";
    public EmployeeSalaryType SalaryType { get; init; }
    public string SalaryTypeName { get; init; } = "";
    public decimal BaseSalaryAmount { get; init; }
    public string SalaryCurrency { get; init; } = "USD";
    public DateTime HireDate { get; init; }
    public DateTime? EndDate { get; init; }
    public bool IsActive { get; init; }
    public string? Notes { get; init; }
    public EmployeeFinancialSummaryViewModel Summary { get; init; } = new();
    public IReadOnlyList<EmployeeSalaryTransactionListItemViewModel> Transactions { get; init; } = [];
    public IReadOnlyList<PaymentListItemViewModel> RoznamchaPayments { get; init; } = [];
    public IReadOnlyList<EmployeeAuditItemViewModel> AuditItems { get; init; } = [];
}

public sealed class EmployeeSalaryTransactionCreateViewModel
{
    public int EmployeeId { get; set; }
    public string EmployeeName { get; set; } = "";
    public string EmployeeCode { get; set; } = "";
    public string? ReturnUrl { get; set; }

    [Display(Name = "تاریخ")]
    [DataType(DataType.Date)]
    public DateTime TransactionDate { get; set; } = AfghanistanBusinessClock.SystemToday;

    [Display(Name = "نوع تراکنش")]
    public EmployeeSalaryTransactionType TransactionType { get; set; } = EmployeeSalaryTransactionType.SalaryAccrual;

    [Display(Name = "مبلغ")]
    public decimal Amount { get; set; }

    [Display(Name = "ارز")]
    [Required(ErrorMessage = "ارز الزامی است.")]
    [StringLength(10)]
    public string Currency { get; set; } = "USD";

    [Display(Name = "نرخ تبدیل به USD")]
    [Range(typeof(decimal), "0.000001", "79228162514264337593543950335", ErrorMessage = "نرخ تبدیل باید بزرگ‌تر از صفر باشد.")]
    public decimal? AppliedFxRateToUsd { get; set; }

    [Display(Name = "حساب نقد / بانک")]
    public int? CashAccountId { get; set; }

    [Display(Name = "مرجع / واوچر")]
    [StringLength(200)]
    public string? Reference { get; set; }

    [Display(Name = "شرح")]
    [StringLength(1000)]
    public string? Description { get; set; }

    [Display(Name = "سال معاش")]
    [Range(2000, 2100, ErrorMessage = "سال معاش معتبر نیست.")]
    public int? SalaryPeriodYear { get; set; }

    [Display(Name = "ماه معاش")]
    [Range(1, 12, ErrorMessage = "ماه معاش معتبر نیست.")]
    public int? SalaryPeriodMonth { get; set; }
}

public sealed class EmployeeSalaryTransactionCancelViewModel
{
    public int TransactionId { get; set; }
    public int EmployeeId { get; set; }

    [Display(Name = "دلیل لغو")]
    [Required(ErrorMessage = "دلیل لغو الزامی است.")]
    [StringLength(1000)]
    public string CancellationReason { get; set; } = "";
}
