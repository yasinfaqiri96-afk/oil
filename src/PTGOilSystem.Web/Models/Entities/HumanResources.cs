using System.ComponentModel.DataAnnotations;

namespace PTGOilSystem.Web.Models.Entities;

/// <summary>
/// بخشِ سازمانی (مالی، عملیات، فروش، اداری، مدیریت). عمداً درختی نیست: سازمان کوچک است و
/// یک سطح بخش کافی است. نام یکتاست تا دو بخشِ هم‌نام ساخته نشود.
/// </summary>
public class Department : BaseEntity
{
    [Required, MaxLength(150)] public string Name { get; set; } = "";
    [MaxLength(30)] public string? Code { get; set; }
    [MaxLength(500)] public string? Description { get; set; }
    public bool IsActive { get; set; } = true;

    public ICollection<Position> Positions { get; set; } = new List<Position>();
}

/// <summary>
/// بست/وظیفهٔ کاری (حسابدار، مدیر عملیات، راننده). ربطی به نقشِ کاربری (Role) ندارد؛ نقش
/// دسترسی به سیستم است و بست جایگاهِ کاری در سازمان. بخش اختیاری است.
/// </summary>
public class Position : BaseEntity
{
    [Required, MaxLength(150)] public string Name { get; set; } = "";
    public int? DepartmentId { get; set; }
    public Department? Department { get; set; }
    [MaxLength(500)] public string? Description { get; set; }
    public bool IsActive { get; set; } = true;
}

public enum EmploymentContractType
{
    Permanent = 1,
    FixedTerm = 2,
    Probation = 3,
    DailyWage = 4,
    Consultancy = 5
}

/// <summary>
/// سه وضعیت بس است. «منقضی» یعنی قرارداد به پایان رسیده یا با قراردادِ تازه تمدید شده؛
/// «فسخ» یعنی پیش از موعد با دلیل بسته شده. قراردادِ فعالی که تاریخِ پایانش گذشته هم «منقضی»
/// نمایش داده می‌شود (<see cref="EmploymentContract.EffectiveStatus"/>).
/// </summary>
public enum EmploymentContractStatus
{
    Active = 1,
    Expired = 2,
    Terminated = 3
}

/// <summary>
/// قرارداد کاری. شرایطِ اصلی (معاش، بخش، بست، تاریخ‌ها) بعد از ثبت ویرایش نمی‌شوند: تغییرشان
/// «تمدید» است و قراردادِ تازه می‌سازد تا تاریخچه بماند. فقط یادداشت و پیوستِ قرارداد قابل
/// ویرایش‌اند. هر کارمند حداکثر یک قراردادِ فعال دارد.
/// </summary>
public class EmploymentContract : BaseEntity
{
    public int EmployeeId { get; set; }
    public Employee? Employee { get; set; }

    [Required, MaxLength(50)] public string ContractNumber { get; set; } = "";
    public EmploymentContractType ContractType { get; set; } = EmploymentContractType.Permanent;
    public DateTime StartDate { get; set; }
    public DateTime? EndDate { get; set; }

    public int? DepartmentId { get; set; }
    public Department? Department { get; set; }
    public int? PositionId { get; set; }
    public Position? Position { get; set; }

    public decimal BaseSalary { get; set; }
    [Required, MaxLength(10)] public string Currency { get; set; } = "USD";
    public decimal? WorkingDaysPerWeek { get; set; }
    public decimal? WorkingHoursPerDay { get; set; }

    [MaxLength(2000)] public string? Notes { get; set; }
    public int? AttachmentId { get; set; }
    public HrAttachment? Attachment { get; set; }

    public EmploymentContractStatus Status { get; set; } = EmploymentContractStatus.Active;
    public DateTime? TerminatedOn { get; set; }
    [MaxLength(1000)] public string? TerminationReason { get; set; }

    /// <summary>قراردادی که این یکی تمدیدش است (زنجیرهٔ تاریخچه).</summary>
    public int? RenewedFromContractId { get; set; }
    public EmploymentContract? RenewedFromContract { get; set; }

    public EmploymentContractStatus EffectiveStatus(DateTime today)
        => Status == EmploymentContractStatus.Active && EndDate.HasValue && EndDate.Value.Date < today.Date
            ? EmploymentContractStatus.Expired
            : Status;
}

public enum CompensationSource
{
    Manual = 1,
    Contract = 2,
    Migration = 3
}

/// <summary>
/// تاریخچهٔ معاش با تاریخِ اعتبار. معاشِ ثبت‌شده هرگز بازنویسی نمی‌شود: تغییرِ معاش دورهٔ قبلی را
/// می‌بندد (<see cref="EffectiveTo"/>) و سطرِ تازه می‌سازد. محاسبهٔ معاشِ ماه از همین جدول می‌خواند؛
/// <see cref="Employee.BaseSalaryAmount"/> فقط آینهٔ معاشِ جاری برای سازگاری است.
/// </summary>
public class EmployeeCompensation : BaseEntity
{
    public int EmployeeId { get; set; }
    public Employee? Employee { get; set; }
    public DateTime EffectiveFrom { get; set; }
    public DateTime? EffectiveTo { get; set; }
    public decimal BaseSalary { get; set; }
    [Required, MaxLength(10)] public string Currency { get; set; } = "USD";
    public EmployeeSalaryType SalaryType { get; set; } = EmployeeSalaryType.Monthly;
    public CompensationSource Source { get; set; } = CompensationSource.Manual;
    public int? EmploymentContractId { get; set; }
    public EmploymentContract? EmploymentContract { get; set; }
    [MaxLength(1000)] public string? Notes { get; set; }
}

public enum HrAttachmentOwner
{
    EmployeeDocument = 1,
    EmploymentContract = 2,
    LeaveRequest = 3
}

/// <summary>
/// فایلِ مدیریت بشری (تذکره، CV، قرارداد، تصدیق). عمداً بیرون از wwwroot ذخیره می‌شود و فقط از
/// اکشنِ مجاز دانلود می‌شود — برخلافِ پوشهٔ عمومیِ /uploads.
/// </summary>
public class HrAttachment : BaseEntity
{
    public int EmployeeId { get; set; }
    public Employee? Employee { get; set; }
    public HrAttachmentOwner Owner { get; set; }
    [Required, MaxLength(260)] public string OriginalFileName { get; set; } = "";
    [Required, MaxLength(100)] public string StoredFileName { get; set; } = "";
    [MaxLength(150)] public string? ContentType { get; set; }
    public long FileSizeBytes { get; set; }
}

/// <summary>
/// تنظیماتِ سادهٔ مدیریت بشری (یک سطر). قواعدِ کسر عمداً پیش‌فرض خاموش‌اند: کسرِ غیبت یا تأخیر
/// تصمیمِ کسب‌وکار است و بی‌اجازه روشن نمی‌شود. رخصتیِ بدون معاش همیشه کسر می‌شود چون خودِ
/// نوعِ رخصتی آن را تعیین کرده است.
/// </summary>
public class HrSettings : BaseEntity
{
    public TimeSpan WorkdayStart { get; set; } = new(8, 0, 0);
    public TimeSpan WorkdayEnd { get; set; } = new(16, 0, 0);
    public int LateGraceMinutes { get; set; } = 10;

    /// <summary>روزهای رخصتیِ هفته با شمارهٔ DayOfWeek (۵ = جمعه)، جداشده با کامه.</summary>
    [MaxLength(20)] public string WeeklyOffDays { get; set; } = "5";

    /// <summary>تقسیمِ معاشِ ماه برای نرخِ روزانه (کسرِ غیبت و رخصتیِ بدون معاش).</summary>
    public int PayrollDaysPerMonth { get; set; } = 30;
    public decimal WorkingHoursPerDay { get; set; } = 8m;
    public bool DeductAbsences { get; set; }
    public bool DeductLateMinutes { get; set; }

    public IReadOnlySet<DayOfWeek> OffDays()
        => (WeeklyOffDays ?? "")
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(v => int.TryParse(v, out var d) && d is >= 0 and <= 6 ? (DayOfWeek?)d : null)
            .Where(d => d.HasValue)
            .Select(d => d!.Value)
            .ToHashSet();
}

/// <summary>رخصتیِ رسمی (عید، روزِ ملی). روزِ رخصتی در حاضری «رخصتی رسمی» است و از رخصتیِ کارمند کم نمی‌شود.</summary>
public class HrHoliday : BaseEntity
{
    public DateTime Date { get; set; }
    [Required, MaxLength(150)] public string Name { get; set; } = "";
}

public enum AttendanceStatus
{
    Present = 1,
    Absent = 2,
    Leave = 3,
    Holiday = 4
}

/// <summary>
/// حاضریِ روزانه — یک سطر برای هر کارمند در هر روز. «تأخیر» وضعیتِ جدا نیست و از ساعتِ ورود
/// حساب می‌شود. تغییرِ سطرِ ثبت‌شده «اصلاح» است و دلیل می‌خواهد. سطری که از رخصتیِ تأییدشده
/// آمده، به همان رخصتی پیوند دارد.
/// </summary>
public class DailyAttendance : BaseEntity
{
    public int EmployeeId { get; set; }
    public Employee? Employee { get; set; }
    public DateTime Date { get; set; }
    public AttendanceStatus Status { get; set; }
    public TimeSpan? CheckIn { get; set; }
    public TimeSpan? CheckOut { get; set; }
    public int? MinutesLate { get; set; }
    [MaxLength(500)] public string? Notes { get; set; }
    public int? LeaveRequestId { get; set; }
    public LeaveRequest? LeaveRequest { get; set; }
    [MaxLength(500)] public string? LastCorrectionReason { get; set; }
}

public class LeaveType : BaseEntity
{
    [Required, MaxLength(100)] public string Name { get; set; } = "";

    /// <summary>سهمیهٔ سالانه (روز). خالی یعنی سقف ندارد و مانده کنترل نمی‌شود.</summary>
    public decimal? AnnualAllowanceDays { get; set; }
    public bool IsPaid { get; set; } = true;
    public bool IsActive { get; set; } = true;
}

public enum LeaveRequestStatus
{
    Pending = 1,
    Approved = 2,
    Rejected = 3,
    Cancelled = 4
}

/// <summary>
/// درخواستِ رخصتی با یک مرحله تأیید. روزهای رخصتی بدونِ رخصتیِ هفته و رخصتیِ رسمی شمرده
/// می‌شوند؛ نیم‌روز فقط برای یک روز. تأیید، حاضریِ همان روزها را «رخصتی» می‌کند.
/// </summary>
public class LeaveRequest : BaseEntity
{
    public int EmployeeId { get; set; }
    public Employee? Employee { get; set; }
    public int LeaveTypeId { get; set; }
    public LeaveType? LeaveType { get; set; }
    public DateTime FromDate { get; set; }
    public DateTime ToDate { get; set; }
    public bool IsHalfDay { get; set; }
    public decimal TotalDays { get; set; }
    [MaxLength(1000)] public string? Reason { get; set; }
    public LeaveRequestStatus Status { get; set; } = LeaveRequestStatus.Pending;
    public int? DecidedByUserId { get; set; }
    public DateTime? DecidedAtUtc { get; set; }
    [MaxLength(1000)] public string? RejectionReason { get; set; }
    [MaxLength(1000)] public string? CancellationReason { get; set; }
    public int? AttachmentId { get; set; }
    public HrAttachment? Attachment { get; set; }
}

public enum PayrollRunStatus
{
    Draft = 1,
    Finalized = 2
}

/// <summary>
/// معاشِ یک ماه (یک سطر برای هر سال/ماه). پیش‌نویس قابلِ محاسبهٔ دوباره و ویرایشِ دستیِ مجاز است؛
/// نهایی‌سازی سطرها را قفل و «ثبت معاش» و وصول‌ها را در حساب‌ها ثبت می‌کند. تغییرِ معاشِ
/// نهایی‌شده فقط با بازگشاییِ صریح و دلیل ممکن است که همهٔ ثبت‌ها را برمی‌گرداند.
/// ماه‌ها میلادی‌اند — همان دورهٔ «ثبت معاش» و تقویمِ مالیِ فعلیِ سیستم.
/// </summary>
public class PayrollRun : BaseEntity, IVersionedEntity
{
    public long Version { get; set; } = 1;
    public int Year { get; set; }
    public int Month { get; set; }
    public PayrollRunStatus Status { get; set; } = PayrollRunStatus.Draft;

    /// <summary>هر بازگشایی یکی زیاد می‌شود.</summary>
    public int Revision { get; set; }
    public DateTime? FinalizedAtUtc { get; set; }
    public int? FinalizedByUserId { get; set; }
    public DateTime? ReopenedAtUtc { get; set; }
    [MaxLength(1000)] public string? LastReopenReason { get; set; }
    [MaxLength(1000)] public string? Notes { get; set; }

    public ICollection<PayrollRunLine> Lines { get; set; } = new List<PayrollRunLine>();
}

/// <summary>
/// سطرِ معاشِ یک کارمند در یک ماه. همهٔ ورودی‌های محاسبه (روزها، نرخ روزانه، دقیقهٔ تأخیر) کنارِ
/// مبالغ ذخیره می‌شوند تا نتیجه بعداً قابلِ بازسازی و بررسی باشد.
/// «معاشِ کارکرده» (ناخالص منهای کسرهای غیبت/تأخیر/رخصتیِ بدون معاش/کسرِ دیگر) همان مصرفِ معاش
/// و بدهی به کارمند است؛ کسرِ قرضه و مساعده فقط وصولِ طلب است و مصرف را کم نمی‌کند.
/// </summary>
public class PayrollRunLine : BaseEntity
{
    public int PayrollRunId { get; set; }
    public PayrollRun? PayrollRun { get; set; }
    public int EmployeeId { get; set; }
    public Employee? Employee { get; set; }
    [Required, MaxLength(10)] public string Currency { get; set; } = "USD";

    // ورودی‌های محاسبه
    public decimal MonthlySalary { get; set; }
    public decimal EmployedDays { get; set; }
    public decimal DailyRate { get; set; }
    public decimal AbsentDays { get; set; }
    public decimal UnpaidLeaveDays { get; set; }
    public int LateMinutes { get; set; }

    // درآمد
    public decimal BaseSalary { get; set; }
    public decimal OvertimeAmount { get; set; }
    public decimal BonusAmount { get; set; }
    public decimal AllowanceAmount { get; set; }
    public decimal OtherEarning { get; set; }

    // کسر
    public decimal AbsenceDeduction { get; set; }
    public decimal LateDeduction { get; set; }
    public decimal UnpaidLeaveDeduction { get; set; }
    public decimal LoanDeduction { get; set; }
    public decimal AdvanceDeduction { get; set; }
    public decimal OtherDeduction { get; set; }

    public decimal GrossSalary { get; set; }
    public decimal TotalDeduction { get; set; }
    public decimal NetSalary { get; set; }

    [MaxLength(1000)] public string? Notes { get; set; }

    /// <summary>هشدارِ محاسبه (مثلاً قرضهٔ ارزِ دیگر که خودکار کسر نشد).</summary>
    [MaxLength(1000)] public string? Warning { get; set; }

    /// <summary>مصرف و بدهیِ معاش: ناخالص منهای کسرهایی که از کارکرد کم می‌شوند.</summary>
    public decimal EarnedSalary => GrossSalary - AbsenceDeduction - LateDeduction - UnpaidLeaveDeduction - OtherDeduction;
}

public enum EmployeeLoanStatus
{
    Active = 1,
    Paid = 2,
    Cancelled = 3
}

/// <summary>
/// قرضهٔ کارمند (جدا از مساعده). بدونِ سود. پرداختِ قرضه، قسط‌های کسرشده از معاش و بازپرداختِ
/// نقدی همه تراکنشِ معاشِ پیوسته به این قرضه‌اند، پس ماندهٔ قرضه همیشه از همان تراکنش‌ها حساب
/// می‌شود و در صورت‌حسابِ کارمند و دفتر کل هم دیده می‌شود.
/// </summary>
public class EmployeeLoan : BaseEntity
{
    public int EmployeeId { get; set; }
    public Employee? Employee { get; set; }
    public DateTime LoanDate { get; set; }
    public decimal PrincipalAmount { get; set; }
    [Required, MaxLength(10)] public string Currency { get; set; } = "USD";
    public int InstallmentCount { get; set; }
    public decimal InstallmentAmount { get; set; }
    public int StartRecoveryYear { get; set; }
    public int StartRecoveryMonth { get; set; }
    public EmployeeLoanStatus Status { get; set; } = EmployeeLoanStatus.Active;
    [MaxLength(1000)] public string? Notes { get; set; }
    public int? DisbursementTransactionId { get; set; }
    public EmployeeSalaryTransaction? DisbursementTransaction { get; set; }
    [MaxLength(1000)] public string? CancellationReason { get; set; }
}

public enum EmployeeDocumentType
{
    Tazkira = 1,
    Cv = 2,
    Contract = 3,
    EducationCertificate = 4,
    ExperienceCertificate = 5,
    Photo = 6,
    Other = 99
}

/// <summary>
/// سندِ کارمند (تذکره، CV، قرارداد، تصدیق). فایل در <see cref="HrAttachment"/> بیرون از wwwroot
/// است. حذف «نرم» است (دلیل و زمان می‌ماند) و جایگزینی سندِ تازه می‌سازد و قبلی را علامت می‌زند؛
/// هیچ فایلی از دیسک پاک نمی‌شود.
/// </summary>
public class EmployeeDocument : BaseEntity
{
    public int EmployeeId { get; set; }
    public Employee? Employee { get; set; }
    public EmployeeDocumentType DocumentType { get; set; }
    [Required, MaxLength(200)] public string Title { get; set; } = "";
    public int AttachmentId { get; set; }
    public HrAttachment? Attachment { get; set; }
    public DateTime? IssueDate { get; set; }
    public DateTime? ExpiryDate { get; set; }
    [MaxLength(1000)] public string? Notes { get; set; }
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAtUtc { get; set; }
    [MaxLength(500)] public string? DeletedReason { get; set; }
    public int? ReplacedByDocumentId { get; set; }
}
