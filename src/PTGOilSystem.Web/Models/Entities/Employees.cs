using System.ComponentModel.DataAnnotations;
using PTGOilSystem.Web.Services.Time;

namespace PTGOilSystem.Web.Models.Entities;

public enum EmployeeType
{
    Permanent = 1,
    Contract = 2,
    DailyWorker = 3,
    Driver = 4,
    OfficeStaff = 5,
    Other = 99
}

public enum EmployeeSalaryType
{
    Monthly = 1,
    Daily = 2,
    Hourly = 3,
    FixedContract = 4
}

public enum EmployeeSalaryTransactionType
{
    SalaryAccrual = 1,
    SalaryPayment = 2,
    SalaryAdvance = 3,
    SalaryDeduction = 4,
    Bonus = 5,
    Adjustment = 6,

    /// <summary>
    /// وصولِ مساعده/قرضه از معاش. فقط جابه‌جاییِ داخلی است — بدهیِ معاش را به‌اندازهٔ طلبِ
    /// مساعده کم می‌کند — پس ماندهٔ خالصِ کارمند را تغییر نمی‌دهد (مساعده هنگام پرداخت از مانده
    /// کم شده بود). در دفتر کل: Dr Employee Payable / Cr Employee Advance.
    /// </summary>
    AdvanceRecovery = 7,

    /// <summary>پرداختِ نقدیِ قرضه به کارمند (طلبِ شرکت).</summary>
    LoanDisbursement = 8,

    /// <summary>کسرِ قسطِ قرضه از معاش؛ مثل وصولِ مساعده فقط تهاترِ بدهیِ معاش با طلب است.</summary>
    LoanRecovery = 9,

    /// <summary>بازپرداختِ نقدیِ قرضه از طرفِ کارمند (پول به صندوق برمی‌گردد).</summary>
    LoanRepayment = 10
}

public class Employee : BaseEntity
{
    [Required, MaxLength(50)] public string EmployeeCode { get; set; } = "";
    [Required, MaxLength(200)] public string FullName { get; set; } = "";
    [MaxLength(200)] public string? FatherName { get; set; }
    [MaxLength(50)] public string? Phone { get; set; }
    [MaxLength(200)] public string? Email { get; set; }
    [MaxLength(500)] public string? PhotoPath { get; set; }
    [MaxLength(100)] public string? NationalId { get; set; }
    [MaxLength(1000)] public string? Address { get; set; }
    // متنِ آزادِ قدیمی. منبعِ اصلی حالا DepartmentId/PositionId است؛ این دو ستون برای سازگاریِ
    // جستجو و گزارش‌های قدیمی هنگام ذخیره با نامِ بخش/بست همگام می‌شوند و هرگز پاک نمی‌شوند.
    [MaxLength(150)] public string? JobTitle { get; set; }
    [MaxLength(150)] public string? Department { get; set; }
    public int? DepartmentId { get; set; }
    public Department? DepartmentRef { get; set; }
    public int? PositionId { get; set; }
    public Position? PositionRef { get; set; }
    public EmployeeType EmployeeType { get; set; } = EmployeeType.Permanent;
    public EmployeeSalaryType SalaryType { get; set; } = EmployeeSalaryType.Monthly;
    public decimal BaseSalaryAmount { get; set; }
    [Required, MaxLength(10)] public string SalaryCurrency { get; set; } = "USD";
    public DateTime HireDate { get; set; } = AfghanistanBusinessClock.SystemToday;
    public DateTime? EndDate { get; set; }
    public bool IsActive { get; set; } = true;
    [MaxLength(2000)] public string? Notes { get; set; }

    // پایانِ همکاری. کارمند هرگز حذف نمی‌شود؛ EndDate روزِ آخرِ کار است و همهٔ سابقه می‌ماند.
    [MaxLength(1000)] public string? TerminationReason { get; set; }
    [MaxLength(2000)] public string? TerminationNotes { get; set; }
    public DateTime? TerminatedAtUtc { get; set; }

    /// <summary>
    /// حسابِ کاربریِ اختیاری. کارمند بدونِ ورود به سیستم کاملاً معتبر است و کاربر هم لازم نیست
    /// کارمند باشد؛ این فقط پیوندِ اختیاری است (هر کاربر حداکثر به یک کارمند).
    /// </summary>
    public int? UserId { get; set; }
    public User? User { get; set; }

    public ICollection<EmployeeSalaryTransaction> SalaryTransactions { get; set; } = new List<EmployeeSalaryTransaction>();
    public ICollection<PaymentTransaction> PaymentTransactions { get; set; } = new List<PaymentTransaction>();
    public ICollection<LedgerEntry> LedgerEntries { get; set; } = new List<LedgerEntry>();
}

public class EmployeeSalaryTransaction : BaseEntity
{
    public int EmployeeId { get; set; }
    public Employee? Employee { get; set; }

    public DateTime TransactionDate { get; set; } = AfghanistanBusinessClock.SystemToday;
    public EmployeeSalaryTransactionType TransactionType { get; set; }
    public decimal Amount { get; set; }
    [Required, MaxLength(10)] public string Currency { get; set; } = "USD";
    public decimal? AppliedFxRateToUsd { get; set; }
    public decimal AmountUsd { get; set; }

    public int? CashAccountId { get; set; }
    public CashAccount? CashAccount { get; set; }
    public int? PaymentTransactionId { get; set; }
    public PaymentTransaction? PaymentTransaction { get; set; }
    public int? LedgerEntryId { get; set; }
    public LedgerEntry? LedgerEntry { get; set; }

    [MaxLength(200)] public string? Reference { get; set; }
    [MaxLength(1000)] public string? Description { get; set; }
    public int? SalaryPeriodYear { get; set; }
    public int? SalaryPeriodMonth { get; set; }

    public bool IsCancelled { get; set; }
    public DateTime? CancelledAtUtc { get; set; }
    [MaxLength(1000)] public string? CancellationReason { get; set; }

    // لغوِ پرداخت/مساعدهٔ نقدی سند اصلی را پاک نمی‌کند: یک سند روزنامچهٔ معکوس (برگشت پول به
    // همان صندوق) با سطر دفتر خودش ثبت می‌شود و اینجا پیوند می‌خورد تا ردیابی کامل بماند.
    public int? ReversalPaymentTransactionId { get; set; }
    public PaymentTransaction? ReversalPaymentTransaction { get; set; }
    public int? ReversalLedgerEntryId { get; set; }
    public LedgerEntry? ReversalLedgerEntry { get; set; }

    /// <summary>ثبت/پرداخت/وصولی که از معاشِ ماهانه آمده، به سطرِ همان معاش پیوند دارد.</summary>
    public int? PayrollRunLineId { get; set; }
    public PayrollRunLine? PayrollRunLine { get; set; }

    /// <summary>پرداخت، قسط یا بازپرداختِ قرضه.</summary>
    public int? EmployeeLoanId { get; set; }
    public EmployeeLoan? EmployeeLoan { get; set; }

    /// <summary>
    /// فقط برای مساعده: از معاشِ کدام ماه وصول شود. خالی یعنی اولین معاشِ ماهانه بعد از تاریخِ مساعده.
    /// </summary>
    public int? RecoveryYear { get; set; }
    public int? RecoveryMonth { get; set; }
}
