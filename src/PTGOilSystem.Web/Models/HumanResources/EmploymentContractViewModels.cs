using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services.Time;

namespace PTGOilSystem.Web.Models.HumanResources;

public static class EmploymentContractLabels
{
    public const int ExpiringSoonDays = 30;

    public static string Type(EmploymentContractType type) => type switch
    {
        EmploymentContractType.Permanent => "دایمی",
        EmploymentContractType.FixedTerm => "معیاد معین",
        EmploymentContractType.Probation => "آزمایشی",
        EmploymentContractType.DailyWage => "روزمزد",
        EmploymentContractType.Consultancy => "مشاوره‌ای",
        _ => type.ToString()
    };

    public static string Status(EmploymentContractStatus status) => status switch
    {
        EmploymentContractStatus.Active => "فعال",
        EmploymentContractStatus.Expired => "منقضی",
        EmploymentContractStatus.Terminated => "فسخ‌شده",
        _ => status.ToString()
    };

    public static string StatusCss(EmploymentContractStatus status) => status switch
    {
        EmploymentContractStatus.Active => "is-active",
        EmploymentContractStatus.Terminated => "is-danger",
        _ => "is-inactive"
    };

    public static bool IsExpiringSoon(EmploymentContractStatus effectiveStatus, DateTime? endDate, DateTime today)
        => effectiveStatus == EmploymentContractStatus.Active
            && endDate.HasValue
            && endDate.Value.Date >= today.Date
            && endDate.Value.Date <= today.Date.AddDays(ExpiringSoonDays);
}

public sealed class EmploymentContractFormViewModel
{
    public int? RenewFromContractId { get; set; }

    [Display(Name = "کارمند")]
    [Range(1, int.MaxValue, ErrorMessage = "کارمند را انتخاب کنید.")]
    public int EmployeeId { get; set; }

    public string? EmployeeName { get; set; }

    [Display(Name = "شمارهٔ قرارداد")]
    [StringLength(50)]
    public string? ContractNumber { get; set; }

    [Display(Name = "نوع قرارداد")]
    public EmploymentContractType ContractType { get; set; } = EmploymentContractType.Permanent;

    [Display(Name = "تاریخ شروع")]
    [DataType(DataType.Date)]
    public DateTime StartDate { get; set; } = AfghanistanBusinessClock.SystemToday;

    [Display(Name = "تاریخ پایان")]
    [DataType(DataType.Date)]
    public DateTime? EndDate { get; set; }

    [Display(Name = "بخش")]
    public int? DepartmentId { get; set; }

    [Display(Name = "بست")]
    public int? PositionId { get; set; }

    [Display(Name = "معاش پایه")]
    [Range(typeof(decimal), "0", "79228162514264337593543950335", ErrorMessage = "معاش نمی‌تواند منفی باشد.")]
    public decimal? BaseSalary { get; set; }

    [Display(Name = "ارز")]
    [StringLength(10)]
    public string? Currency { get; set; }

    [Display(Name = "روز کاری در هفته")]
    [Range(typeof(decimal), "0", "7")]
    public decimal? WorkingDaysPerWeek { get; set; }

    [Display(Name = "ساعت کاری در روز")]
    [Range(typeof(decimal), "0", "24")]
    public decimal? WorkingHoursPerDay { get; set; }

    [Display(Name = "یادداشت")]
    [StringLength(2000)]
    public string? Notes { get; set; }

    [Display(Name = "فایل قرارداد")]
    public IFormFile? Attachment { get; set; }
}

public sealed class EmploymentContractListItemViewModel
{
    public int Id { get; init; }
    public int EmployeeId { get; init; }
    public string EmployeeName { get; init; } = "";
    public string EmployeeCode { get; init; } = "";
    public string ContractNumber { get; init; } = "";
    public EmploymentContractType ContractType { get; init; }
    public DateTime StartDate { get; init; }
    public DateTime? EndDate { get; init; }
    public EmploymentContractStatus Status { get; init; }
    public string? DepartmentName { get; init; }
    public string? PositionName { get; init; }
    public decimal? BaseSalary { get; init; }
    public string? Currency { get; init; }
    public bool IsExpiringSoon { get; init; }
}

public sealed class EmploymentContractIndexViewModel
{
    public string? Query { get; init; }
    public string? Status { get; init; }
    public bool ExpiringOnly { get; init; }
    public bool CanViewSalary { get; init; }
    public int ActiveCount { get; init; }
    public int ExpiringSoonCount { get; init; }
    public int ActiveEmployeesWithoutContract { get; init; }
    public IReadOnlyList<EmploymentContractListItemViewModel> Items { get; init; } = [];
}

public sealed class EmploymentContractDetailsViewModel
{
    public EmploymentContract Contract { get; init; } = new();
    public EmploymentContractStatus EffectiveStatus { get; init; }
    public bool IsExpiringSoon { get; init; }
    public bool CanViewSalary { get; init; }
    public bool CanManage { get; init; }
    public IReadOnlyList<EmploymentContractListItemViewModel> History { get; init; } = [];
}
