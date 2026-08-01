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
    Adjustment = 6
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
    [MaxLength(150)] public string? JobTitle { get; set; }
    [MaxLength(150)] public string? Department { get; set; }
    public EmployeeType EmployeeType { get; set; } = EmployeeType.Permanent;
    public EmployeeSalaryType SalaryType { get; set; } = EmployeeSalaryType.Monthly;
    public decimal BaseSalaryAmount { get; set; }
    [Required, MaxLength(10)] public string SalaryCurrency { get; set; } = "USD";
    public DateTime HireDate { get; set; } = AfghanistanBusinessClock.SystemToday;
    public DateTime? EndDate { get; set; }
    public bool IsActive { get; set; } = true;
    [MaxLength(2000)] public string? Notes { get; set; }

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
}
