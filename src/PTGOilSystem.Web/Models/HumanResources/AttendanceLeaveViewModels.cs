using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services.Time;

namespace PTGOilSystem.Web.Models.HumanResources;

public static class AttendanceLabels
{
    public static string Status(AttendanceStatus status) => status switch
    {
        AttendanceStatus.Present => "حاضر",
        AttendanceStatus.Absent => "غایب",
        AttendanceStatus.Leave => "رخصتی",
        AttendanceStatus.Holiday => "رخصتی رسمی",
        _ => status.ToString()
    };

    /// <summary>حرفِ کوتاه برای جدولِ ماهانه.</summary>
    public static string Short(AttendanceStatus status) => status switch
    {
        AttendanceStatus.Present => "ح",
        AttendanceStatus.Absent => "غ",
        AttendanceStatus.Leave => "ر",
        AttendanceStatus.Holiday => "ت",
        _ => "?"
    };

    public static string Css(AttendanceStatus status) => status switch
    {
        AttendanceStatus.Present => "is-active",
        AttendanceStatus.Absent => "is-danger",
        AttendanceStatus.Leave => "is-warning",
        _ => "is-inactive"
    };
}

public static class LeaveLabels
{
    public static string Status(LeaveRequestStatus status) => status switch
    {
        LeaveRequestStatus.Pending => "در انتظار",
        LeaveRequestStatus.Approved => "تأییدشده",
        LeaveRequestStatus.Rejected => "ردشده",
        LeaveRequestStatus.Cancelled => "لغوشده",
        _ => status.ToString()
    };

    public static string Css(LeaveRequestStatus status) => status switch
    {
        LeaveRequestStatus.Approved => "is-active",
        LeaveRequestStatus.Pending => "is-warning",
        LeaveRequestStatus.Rejected => "is-danger",
        _ => "is-inactive"
    };
}

public sealed class AttendanceRowInput
{
    public int EmployeeId { get; set; }
    public AttendanceStatus? Status { get; set; }
    public TimeSpan? CheckIn { get; set; }
    public TimeSpan? CheckOut { get; set; }
    public string? Notes { get; set; }
}

public sealed class AttendanceDayRowViewModel
{
    public int EmployeeId { get; init; }
    public string EmployeeName { get; init; } = "";
    public string EmployeeCode { get; init; } = "";
    public string? DepartmentName { get; init; }
    public AttendanceStatus? Status { get; init; }
    public TimeSpan? CheckIn { get; init; }
    public TimeSpan? CheckOut { get; init; }
    public int? MinutesLate { get; init; }
    public string? Notes { get; init; }
    public bool IsRecorded { get; init; }
    public bool LockedByLeave { get; init; }
    public string? LastCorrectionReason { get; init; }
}

public sealed class AttendanceDayViewModel
{
    public DateTime Date { get; init; } = AfghanistanBusinessClock.SystemToday;
    public bool IsWeeklyOff { get; init; }
    public string? HolidayName { get; init; }
    public int? DepartmentId { get; init; }
    public IReadOnlyList<AttendanceDayRowViewModel> Rows { get; init; } = [];
    public bool CanEdit { get; init; }
    public int PresentCount => Rows.Count(r => r.Status == AttendanceStatus.Present);
    public int AbsentCount => Rows.Count(r => r.Status == AttendanceStatus.Absent);
    public int LeaveCount => Rows.Count(r => r.Status == AttendanceStatus.Leave);
    public int LateCount => Rows.Count(r => r.MinutesLate > 0);
    public int NotRecordedCount => Rows.Count(r => !r.IsRecorded);
}

public sealed class AttendanceMonthRowViewModel
{
    public int EmployeeId { get; init; }
    public string EmployeeName { get; init; } = "";
    public string EmployeeCode { get; init; } = "";
    public IReadOnlyDictionary<int, AttendanceStatus> Days { get; init; } = new Dictionary<int, AttendanceStatus>();
    public IReadOnlySet<int> LateDays { get; init; } = new HashSet<int>();
    public int Present { get; init; }
    public int Absent { get; init; }
    public int Leave { get; init; }
    public int Late { get; init; }
}

public sealed class AttendanceMonthViewModel
{
    public int Year { get; init; }
    public int Month { get; init; }
    public int DaysInMonth => DateTime.DaysInMonth(Year, Month);
    public IReadOnlySet<int> OffDays { get; init; } = new HashSet<int>();
    public IReadOnlyList<AttendanceMonthRowViewModel> Rows { get; init; } = [];
}

public sealed class LeaveRequestFormViewModel
{
    [Display(Name = "کارمند")]
    [Range(1, int.MaxValue, ErrorMessage = "کارمند را انتخاب کنید.")]
    public int EmployeeId { get; set; }

    [Display(Name = "نوع رخصتی")]
    [Range(1, int.MaxValue, ErrorMessage = "نوع رخصتی را انتخاب کنید.")]
    public int LeaveTypeId { get; set; }

    [Display(Name = "از تاریخ")]
    [DataType(DataType.Date)]
    public DateTime FromDate { get; set; } = AfghanistanBusinessClock.SystemToday;

    [Display(Name = "تا تاریخ")]
    [DataType(DataType.Date)]
    public DateTime ToDate { get; set; } = AfghanistanBusinessClock.SystemToday;

    [Display(Name = "نیم‌روز")]
    public bool IsHalfDay { get; set; }

    [Display(Name = "دلیل")]
    [StringLength(1000)]
    public string? Reason { get; set; }

    [Display(Name = "پیوست (اختیاری)")]
    public IFormFile? Attachment { get; set; }
}

public sealed class LeaveRequestListItemViewModel
{
    public int Id { get; init; }
    public int EmployeeId { get; init; }
    public string EmployeeName { get; init; } = "";
    public string EmployeeCode { get; init; } = "";
    public string LeaveTypeName { get; init; } = "";
    public bool IsPaid { get; init; }
    public DateTime FromDate { get; init; }
    public DateTime ToDate { get; init; }
    public bool IsHalfDay { get; init; }
    public decimal TotalDays { get; init; }
    public string? Reason { get; init; }
    public LeaveRequestStatus Status { get; init; }
    public string? DecisionNote { get; init; }
    public bool HasAttachment { get; init; }
}

public sealed class LeaveIndexViewModel
{
    public string? Status { get; init; }
    public int? EmployeeId { get; init; }
    public int PendingCount { get; init; }
    public int OnLeaveToday { get; init; }
    public bool CanManage { get; init; }
    public IReadOnlyList<LeaveRequestListItemViewModel> Items { get; init; } = [];
}

public sealed class HrSettingsFormViewModel
{
    [Display(Name = "شروع روز کاری")]
    public TimeSpan WorkdayStart { get; set; } = new(8, 0, 0);

    [Display(Name = "پایان روز کاری")]
    public TimeSpan WorkdayEnd { get; set; } = new(16, 0, 0);

    [Display(Name = "مهلت تأخیر (دقیقه)")]
    [Range(0, 240)]
    public int LateGraceMinutes { get; set; } = 10;

    [Display(Name = "رخصتی‌های هفته")]
    public int[] WeeklyOffDays { get; set; } = [5];

    [Display(Name = "روزهای معاشِ ماه (برای نرخ روزانه)")]
    [Range(1, 31)]
    public int PayrollDaysPerMonth { get; set; } = 30;

    [Display(Name = "ساعت کاری در روز")]
    [Range(typeof(decimal), "1", "24")]
    public decimal WorkingHoursPerDay { get; set; } = 8m;

    [Display(Name = "کسر معاشِ روزهای غیبت")]
    public bool DeductAbsences { get; set; }

    [Display(Name = "کسر معاشِ دقایق تأخیر")]
    public bool DeductLateMinutes { get; set; }
}

public sealed class HrSettingsPageViewModel
{
    public HrSettingsFormViewModel Settings { get; init; } = new();
    public IReadOnlyList<HrHoliday> Holidays { get; init; } = [];
    public IReadOnlyList<LeaveType> LeaveTypes { get; init; } = [];
    public bool CanEdit { get; init; }
}
