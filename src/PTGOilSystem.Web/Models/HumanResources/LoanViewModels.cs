using System.ComponentModel.DataAnnotations;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services.Time;

namespace PTGOilSystem.Web.Models.HumanResources;

public static class LoanLabels
{
    public static string Status(EmployeeLoanStatus status) => status switch
    {
        EmployeeLoanStatus.Active => "فعال",
        EmployeeLoanStatus.Paid => "تسویه‌شده",
        EmployeeLoanStatus.Cancelled => "لغوشده",
        _ => status.ToString()
    };

    public static string Css(EmployeeLoanStatus status) => status switch
    {
        EmployeeLoanStatus.Active => "is-warning",
        EmployeeLoanStatus.Paid => "is-active",
        _ => "is-inactive"
    };
}

public sealed class EmployeeLoanFormViewModel
{
    [Display(Name = "کارمند")]
    [Range(1, int.MaxValue, ErrorMessage = "کارمند را انتخاب کنید.")]
    public int EmployeeId { get; set; }

    [Display(Name = "تاریخ قرضه")]
    [DataType(DataType.Date)]
    public DateTime LoanDate { get; set; } = AfghanistanBusinessClock.SystemToday;

    [Display(Name = "مبلغ قرضه")]
    [Range(typeof(decimal), "0.01", "79228162514264337593543950335", ErrorMessage = "مبلغ باید بیشتر از صفر باشد.")]
    public decimal PrincipalAmount { get; set; }

    [Display(Name = "ارز")]
    [Required]
    [StringLength(10)]
    public string Currency { get; set; } = "USD";

    [Display(Name = "تعداد اقساط")]
    [Range(1, 120)]
    public int InstallmentCount { get; set; } = 1;

    [Display(Name = "سالِ شروع کسر")]
    [Range(2000, 2100)]
    public int StartRecoveryYear { get; set; } = AfghanistanBusinessClock.SystemToday.Year;

    [Display(Name = "ماهِ شروع کسر")]
    [Range(1, 12)]
    public int StartRecoveryMonth { get; set; } = AfghanistanBusinessClock.SystemToday.Month;

    [Display(Name = "صندوق / بانک پرداخت")]
    [Range(1, int.MaxValue, ErrorMessage = "حساب پرداخت را انتخاب کنید.")]
    public int CashAccountId { get; set; }

    [Display(Name = "یادداشت")]
    [StringLength(1000)]
    public string? Notes { get; set; }
}

public sealed class EmployeeLoanListItemViewModel
{
    public int Id { get; init; }
    public int EmployeeId { get; init; }
    public string EmployeeName { get; init; } = "";
    public string EmployeeCode { get; init; } = "";
    public DateTime LoanDate { get; init; }
    public decimal PrincipalAmount { get; init; }
    public string Currency { get; init; } = "USD";
    public int InstallmentCount { get; init; }
    public decimal InstallmentAmount { get; init; }
    public string StartRecovery { get; init; } = "";
    public decimal Outstanding { get; init; }
    public EmployeeLoanStatus Status { get; init; }
}

public sealed class OutstandingAdvanceViewModel
{
    public int EmployeeId { get; init; }
    public string EmployeeName { get; init; } = "";
    public string EmployeeCode { get; init; } = "";
    public string Currency { get; init; } = "USD";
    public decimal Advanced { get; init; }
    public decimal Recovered { get; init; }
    public decimal Outstanding => Advanced - Recovered;
    public string? NextRecovery { get; init; }
}

public sealed class EmployeeLoansIndexViewModel
{
    public string Tab { get; init; } = "loans";
    public IReadOnlyList<EmployeeLoanListItemViewModel> Loans { get; init; } = [];
    public IReadOnlyList<OutstandingAdvanceViewModel> Advances { get; init; } = [];
    public bool CanManage { get; init; }
}

public sealed class EmployeeLoanDetailsViewModel
{
    public EmployeeLoan Loan { get; init; } = new();
    public decimal Outstanding { get; init; }
    public IReadOnlyList<EmployeeSalaryTransaction> Transactions { get; init; } = [];
    public bool CanManage { get; init; }
    public bool CanPay { get; init; }
}

public static class HrDocumentLabels
{
    public static string Type(EmployeeDocumentType type) => type switch
    {
        EmployeeDocumentType.Tazkira => "تذکره",
        EmployeeDocumentType.Cv => "سوانح (CV)",
        EmployeeDocumentType.Contract => "قرارداد",
        EmployeeDocumentType.EducationCertificate => "سند تحصیلی",
        EmployeeDocumentType.ExperienceCertificate => "تصدیق تجربه کاری",
        EmployeeDocumentType.Photo => "عکس",
        _ => "دیگر"
    };
}
