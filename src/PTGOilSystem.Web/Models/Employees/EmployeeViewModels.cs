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
        EmployeeSalaryTransactionType.AdvanceRecovery => "وصول مساعده از معاش",
        EmployeeSalaryTransactionType.LoanDisbursement => "پرداخت قرضه",
        EmployeeSalaryTransactionType.LoanRecovery => "کسر قسط قرضه از معاش",
        EmployeeSalaryTransactionType.LoanRepayment => "بازپرداخت نقدی قرضه",
        _ => value.ToString()
    };

    public static bool RequiresCashAccount(EmployeeSalaryTransactionType value)
        => value is EmployeeSalaryTransactionType.SalaryPayment
            or EmployeeSalaryTransactionType.SalaryAdvance
            or EmployeeSalaryTransactionType.LoanDisbursement
            or EmployeeSalaryTransactionType.LoanRepayment;

    /// <summary>پول به صندوق برمی‌گردد (بقیهٔ انواعِ نقدی خروج‌اند).</summary>
    public static bool IsCashInflow(EmployeeSalaryTransactionType value)
        => value == EmployeeSalaryTransactionType.LoanRepayment;

    /// <summary>تهاترِ داخلیِ بدهیِ معاش با طلبِ مساعده/قرضه؛ ماندهٔ خالص را تغییر نمی‌دهد.</summary>
    public static bool IsRecovery(EmployeeSalaryTransactionType value)
        => value is EmployeeSalaryTransactionType.AdvanceRecovery or EmployeeSalaryTransactionType.LoanRecovery;

    public static bool RequiresSalaryPeriod(EmployeeSalaryTransactionType value)
        => value == EmployeeSalaryTransactionType.SalaryAccrual;

    /// <summary>
    /// انواعی که کاربر در فرمِ «تراکنش معاش» مستقیم ثبت می‌کند. «وصول مساعده» فقط از معاشِ
    /// ماهانه ساخته می‌شود تا با کسرِ همان ماه جفت بماند.
    /// </summary>
    public static bool IsManualEntryType(EmployeeSalaryTransactionType value)
        => value is not (EmployeeSalaryTransactionType.AdvanceRecovery
            or EmployeeSalaryTransactionType.LoanDisbursement
            or EmployeeSalaryTransactionType.LoanRecovery
            or EmployeeSalaryTransactionType.LoanRepayment);

    /// <summary>نوعی که پول نقد جابه‌جا می‌کند (نیاز به «پرداخت معاش») در برابرِ بقیه («مدیریت معاش»).</summary>
    public static bool IsCashType(EmployeeSalaryTransactionType value) => RequiresCashAccount(value);
}

public sealed class EmployeeFinancialSummaryViewModel
{
    public decimal AccruedSalaryUsd { get; init; }
    public decimal PaidSalaryUsd { get; init; }
    public decimal AdvancesUsd { get; init; }

    /// <summary>بخشی از مساعده که از معاش وصول شده. ماندهٔ خالص را تغییر نمی‌دهد.</summary>
    public decimal RecoveredAdvancesUsd { get; init; }
    public decimal OutstandingAdvanceUsd => AdvancesUsd - RecoveredAdvancesUsd;
    public decimal LoansUsd { get; init; }
    public decimal LoanRecoveredUsd { get; init; }
    public decimal LoanRepaidUsd { get; init; }
    public decimal OutstandingLoanUsd => LoansUsd - LoanRecoveredUsd - LoanRepaidUsd;
    public decimal DeductionsUsd { get; init; }
    public decimal BonusesUsd { get; init; }
    public decimal AdjustmentsUsd { get; init; }
    /// <summary>
    /// ماندهٔ خالص از دیدِ کارمند (مثبت = شرکت بدهکار است): معاشِ ثبت‌شده منهای پرداخت‌ها و
    /// طلب‌های باز (مساعده و قرضه). وصول‌ها تهاترِ داخلی‌اند و اینجا اثری ندارند.
    /// </summary>
    public decimal BalanceUsd => AccruedSalaryUsd + BonusesUsd + AdjustmentsUsd + LoanRepaidUsd
        - PaidSalaryUsd - AdvancesUsd - DeductionsUsd - LoansUsd;

    /// <summary>معاشِ پرداخت‌نشده: ثبت‌شده منهای پرداخت و وصول‌ها.</summary>
    public decimal UnpaidSalaryUsd => AccruedSalaryUsd + BonusesUsd + AdjustmentsUsd
        - PaidSalaryUsd - DeductionsUsd - RecoveredAdvancesUsd - LoanRecoveredUsd;
}

public sealed class EmployeeIndexFilterViewModel
{
    [Display(Name = "جستجو")]
    [StringLength(200)]
    public string? Query { get; set; }

    [Display(Name = "نوع کارمند")]
    public EmployeeType[] EmployeeType { get; set; } = [];

    [Display(Name = "نوع معاش")]
    public EmployeeSalaryType[] SalaryType { get; set; } = [];

    [Display(Name = "وظیفه / دپارتمان")]
    [StringLength(150)]
    public string? Department { get; set; }

    [Display(Name = "بخش")]
    public int[] DepartmentId { get; set; } = [];

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
    public bool CanViewSalary { get; init; }
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

    [Display(Name = "بخش")]
    public int? DepartmentId { get; set; }

    [Display(Name = "بست / وظیفه")]
    public int? PositionId { get; set; }

    [Display(Name = "حساب کاربری (اختیاری)")]
    public int? UserId { get; set; }

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
    public int? ReversalPaymentTransactionId { get; init; }
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

    /// <summary>کاربر اجازهٔ دیدنِ معاش، مانده، قرضه و مساعده را دارد.</summary>
    public bool CanViewSalary { get; init; }
    public bool CanManageSalary { get; init; }
    public bool CanPaySalary { get; init; }
    public bool CanManageData { get; init; }

    // ---- پروفایلِ یکپارچه ----
    public string? DepartmentName { get; init; }
    public string? PositionName { get; init; }
    public string? TerminationReason { get; init; }
    public string? LinkedUsername { get; init; }
    public EmployeeCompensation? CurrentCompensation { get; init; }
    public IReadOnlyList<EmployeeCompensation> CompensationHistory { get; init; } = [];
    public EmploymentContract? ActiveContract { get; init; }
    public IReadOnlyList<EmploymentContract> Contracts { get; init; } = [];
    public EmployeeAttendanceSummary AttendanceThisMonth { get; init; } = new();
    public IReadOnlyList<DailyAttendance> RecentAttendance { get; init; } = [];
    public IReadOnlyList<Services.HumanResources.LeaveBalance> LeaveBalances { get; init; } = [];
    public IReadOnlyList<LeaveRequest> LeaveRequests { get; init; } = [];
    public IReadOnlyList<EmployeePayrollLineItem> PayrollLines { get; init; } = [];
    public IReadOnlyList<EmployeeLoanItem> Loans { get; init; } = [];
    public IReadOnlyList<Services.HumanResources.CurrencyAmount> OutstandingAdvances { get; init; } = [];
    public IReadOnlyList<Services.HumanResources.CurrencyAmount> UnpaidPayroll { get; init; } = [];
    public IReadOnlyList<EmployeeDocument> Documents { get; init; } = [];
}

public sealed class EmployeeAttendanceSummary
{
    public int Present { get; init; }
    public int Absent { get; init; }
    public int Leave { get; init; }
    public int Late { get; init; }
}

public sealed class EmployeePayrollLineItem
{
    public int Year { get; init; }
    public int Month { get; init; }
    public PayrollRunStatus Status { get; init; }
    public string Currency { get; init; } = "USD";
    public decimal Gross { get; init; }
    public decimal Deductions { get; init; }
    public decimal Net { get; init; }
    public decimal Paid { get; init; }
}

public sealed class EmployeeLoanItem
{
    public int Id { get; init; }
    public DateTime LoanDate { get; init; }
    public decimal Principal { get; init; }
    public string Currency { get; init; } = "USD";
    public decimal Installment { get; init; }
    public decimal Outstanding { get; init; }
    public EmployeeLoanStatus Status { get; init; }
}

public sealed class EmployeeTerminationViewModel
{
    public int EmployeeId { get; set; }

    [Display(Name = "روزِ آخرِ کار")]
    [DataType(DataType.Date)]
    public DateTime LastWorkingDate { get; set; } = AfghanistanBusinessClock.SystemToday;

    [Display(Name = "دلیل")]
    [Required(ErrorMessage = "دلیلِ پایانِ همکاری الزامی است.")]
    [StringLength(1000)]
    public string Reason { get; set; } = "";

    [Display(Name = "یادداشت")]
    [StringLength(2000)]
    public string? Notes { get; set; }

    [Display(Name = "موارد باز را می‌دانم و بعداً تسویه می‌شود")]
    public bool AcknowledgeOpenItems { get; set; }
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

    [Display(Name = "سالِ وصول از معاش")]
    [Range(2000, 2100, ErrorMessage = "سال معتبر نیست.")]
    public int? RecoveryYear { get; set; }

    [Display(Name = "ماهِ وصول از معاش")]
    [Range(1, 12, ErrorMessage = "ماه معتبر نیست.")]
    public int? RecoveryMonth { get; set; }
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

public sealed class EmployeeSalaryChangeViewModel
{
    public int EmployeeId { get; set; }
    public string EmployeeName { get; set; } = "";
    public string EmployeeCode { get; set; } = "";
    public string? CurrentSalaryText { get; set; }

    [Display(Name = "تاریخ اعتبار")]
    [DataType(DataType.Date)]
    public DateTime EffectiveFrom { get; set; } = AfghanistanBusinessClock.SystemToday;

    [Display(Name = "معاش پایه")]
    [Range(typeof(decimal), "0", "79228162514264337593543950335", ErrorMessage = "معاش نمی‌تواند منفی باشد.")]
    public decimal BaseSalary { get; set; }

    [Display(Name = "ارز")]
    [Required(ErrorMessage = "ارز الزامی است.")]
    [StringLength(10)]
    public string Currency { get; set; } = "USD";

    [Display(Name = "نوع معاش")]
    public EmployeeSalaryType SalaryType { get; set; } = EmployeeSalaryType.Monthly;

    [Display(Name = "دلیل / یادداشت")]
    [StringLength(1000)]
    public string? Notes { get; set; }
}
