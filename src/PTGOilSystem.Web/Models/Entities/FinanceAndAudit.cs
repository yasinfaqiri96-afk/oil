using System;
using System.ComponentModel.DataAnnotations;

namespace PTGOilSystem.Web.Models.Entities;

public enum LedgerSide { Debit = 1, Credit = 2 }
public enum CashAccountType { Cash = 1, Bank = 2, Mixed = 3 }
public enum PaymentDirection { In = 1, Out = 2 }

/// <summary>
/// منبعِ واقعیِ پولِ یک پرداخت. پیش‌فرض <see cref="Company"/> است و همهٔ رکوردهای قبلی همین‌اند،
/// پس رفتار موجود تغییر نمی‌کند. <see cref="Partner"/> یعنی شریکِ قرارداد مستقیماً از جیب خودش
/// پرداخت کرده و صندوق/بانکِ شرکت اصلاً حرکت نکرده است.
/// </summary>
public enum PaymentFundingSource
{
    [Display(Name = "شرکت")] Company = 1,
    [Display(Name = "شریک")] Partner = 2
}
public enum PaymentKind
{
    CustomerReceipt = 1,
    SupplierPayment = 2,
    ExpensePayment = 3,
    TruckPayment = 4,
    ManualPayment = 5,
    ManualReceipt = 6,
    EmployeeSalaryPayment = 7,
    EmployeeSalaryAdvance = 8,
    SupplierReceipt = 9,
    CustomerPayment = 10,
    EmployeeReturn = 11,
    ServiceProviderPayment = 12,
    SarrafSettlement = 13,
    // خروجِ نقدیِ کمیسیون از صندوق/بانک. فقط cash movement است؛ مصرف واقعی در P&L
    // توسط ExpenseTransaction مرتبط (SourceType="Expense") ثبت می‌شود، نه این پرداخت.
    CommissionPayment = 14
}

public enum SarrafSettlementDifferenceType
{
    None = 0,
    Gain = 1,
    Loss = 2,
    SupplierShortfall = 3
}

// Phase 1 — دلیل تفاوت (فقط ثبت و نمایش، هیچ منطق حسابداری به آن وابسته نیست).
// برای توضیح فرق بین «مبلغی که یک طرف داد» و «مبلغی که طرف دیگر گرفت».
public enum DifferenceReason
{
    [System.ComponentModel.DataAnnotations.Display(Name = "تفاوت نرخ")] FxDifference = 1,
    [System.ComponentModel.DataAnnotations.Display(Name = "کمیشن")] Commission = 2,
    [System.ComponentModel.DataAnnotations.Display(Name = "کرایه حواله")] TransferFee = 3,
    [System.ComponentModel.DataAnnotations.Display(Name = "مارجین دلال")] BrokerMargin = 4,
    [System.ComponentModel.DataAnnotations.Display(Name = "تخفیف")] Discount = 5,
    [System.ComponentModel.DataAnnotations.Display(Name = "اصلاح حساب")] Adjustment = 6,
    [System.ComponentModel.DataAnnotations.Display(Name = "سایر")] Other = 99
}

public enum SarrafSettlementDifferenceTreatment
{
    AcceptedAmountOnly = 1,
    RecognizeExchangeGainLoss = 2
}

public enum SarrafSettlementStatus
{
    Draft = 1,
    Posted = 2,
    Cancelled = 3
}

// جهت عملیاتِ صراف نسبت به شرکت:
//   Out = صراف از طرف شرکت به طرف مقابل پرداخت کرد (ما به صراف بدهکار می‌شویم).
//   In  = صراف برای شرکت از طرف مقابل پول دریافت کرد (صراف به ما بدهکار می‌شود).
// پیش‌فرض Out است تا رکوردهای قدیمی (پرداخت به تأمین‌کننده) بدون تغییر بمانند.
public enum SarrafSettlementDirection
{
    Out = 1,
    In = 2
}

// نوع طرف‌حسابِ تسویهٔ صراف. Phase 2: راننده و کارمند هم اضافه شدند.
// Expense/Customs/Other عمداً اضافه نشده‌اند (بعداً قابل افزودن بدون شکستن داده).
public enum SarrafSettlementCounterpartyType
{
    Supplier = 1,
    Customer = 2,
    ServiceProvider = 3,
    Driver = 4,
    Employee = 5
}

public class CashAccount : BaseEntity
{
    [Required, MaxLength(50)] public string Code { get; set; } = "";
    [Required, MaxLength(200)] public string Name { get; set; } = "";
    // مالکیت شرکت (nullable). هیچ رابطه قطعی تاریخی وجود ندارد، بنابراین Backfill نمی‌شود؛
    // فقط برای رکوردهای جدید/تأییدشده توسط مدیر مالی تعیین می‌گردد. GL جدید بدون Company قطعی
    // به این حساب متصل نمی‌شود.
    public int? CompanyId { get; set; }
    public Company? Company { get; set; }
    public CashAccountType AccountType { get; set; } = CashAccountType.Bank;
    [Required, MaxLength(10)] public string Currency { get; set; } = "USD";
    public bool IsActive { get; set; } = true;
    [MaxLength(50)] public string? AccountNumber { get; set; }
    [MaxLength(150)] public string? BankName { get; set; }
    [MaxLength(150)] public string? Branch { get; set; }
    [MaxLength(1000)] public string? Notes { get; set; }
}

public class PaymentTransaction : BaseEntity
{
    public DateTime PaymentDate { get; set; }
    public PaymentDirection Direction { get; set; }
    public PaymentKind PaymentKind { get; set; }

    // مالکیت شرکت (nullable). Backfill فقط از روابط قابل‌اثبات (قرارداد، فروش، مصرف، محموله
    // تک‌شرکتی) انجام شده؛ رکوردهای مبهم null می‌مانند و به GL جدید متصل نمی‌شوند.
    public int? CompanyId { get; set; }
    public Company? Company { get; set; }

    // پرداختِ تأمین‌شده توسط شریک از صندوق/بانکِ شرکت خارج نمی‌شود، پس حسابِ نقدی ندارد.
    // برای پرداختِ شرکت (FundingSource = Company) همچنان اجباری است — اعتبارسنجی در PaymentsController.
    public int? CashAccountId { get; set; }
    public CashAccount? CashAccount { get; set; }

    // منبعِ پول. رکوردهای قدیمی همگی Company هستند (پیش‌فرضِ ستون در Migration).
    public PaymentFundingSource FundingSource { get; set; } = PaymentFundingSource.Company;

    // شریکی که این پرداخت را واقعاً انجام داده. فقط وقتی مقدار دارد که FundingSource = Partner باشد.
    public int? PaidByPartnerId { get; set; }
    public Partner? PaidByPartner { get; set; }

    public int? CustomerId { get; set; }
    public Customer? Customer { get; set; }
    public int? SupplierId { get; set; }
    public Supplier? Supplier { get; set; }
    public int? ServiceProviderId { get; set; }
    public ServiceProvider? ServiceProvider { get; set; }
    public int? SarrafId { get; set; }
    public Sarraf? Sarraf { get; set; }
    public int? DriverId { get; set; }
    public Driver? Driver { get; set; }
    public int? EmployeeId { get; set; }
    public Employee? Employee { get; set; }

    public int? ContractId { get; set; }
    public Contract? Contract { get; set; }
    public int? ShipmentId { get; set; }
    public Shipment? Shipment { get; set; }
    public int? SalesTransactionId { get; set; }
    public SalesTransaction? SalesTransaction { get; set; }
    public int? ExpenseTransactionId { get; set; }
    public ExpenseTransaction? ExpenseTransaction { get; set; }
    public int? TruckDispatchId { get; set; }
    public TruckDispatch? TruckDispatch { get; set; }

    public decimal Amount { get; set; }
    [Required, MaxLength(10)] public string Currency { get; set; } = "USD";
    public decimal? AppliedFxRateToUsd { get; set; }
    public decimal AmountUsd { get; set; }
    [MaxLength(200)] public string? Reference { get; set; }
    [MaxLength(1000)] public string? Description { get; set; }

    // Phase 1 — نشانهٔ «پیش‌پرداخت» برای پرداخت تأمین‌کننده (فقط ثبت/نمایش، nullable).
    // منطق Ledger و مانده تغییری نمی‌کند؛ فقط در صورت‌حساب جدا نشان داده می‌شود.
    public bool? IsAdvancePayment { get; set; }

    // مرحله ۴ — نشانهٔ «پیش‌دریافت» برای دریافت از مشتری (فقط ثبت/نمایش، nullable).
    // معادلِ سمتِ مشتریِ IsAdvancePayment است و عمداً از آن جدا نگه داشته شده تا معنای
    // فیلد تأمین‌کننده تغییر نکند. منطق Ledger و مانده تغییری نمی‌کند؛ فقط دفتر کل جدید
    // با آن بین «تسویهٔ مطالبات» و «پیش‌دریافت مشتری» تفکیک می‌کند. رکوردهای قدیمی null
    // می‌مانند و هرگز حدس زده نمی‌شوند.
    public bool? IsCustomerAdvance { get; set; }

    public int? LedgerEntryId { get; set; }
    public LedgerEntry? LedgerEntry { get; set; }

    // کمیسیون — لینک ردیابی. روی پرداخت/دریافت اصلی: ExpenseTransactionِ کمیسیون مرتبط.
    // ستون سادهٔ nullable بدون navigation/FK (backward-compatible). برای نمایش و ویرایش کمیسیون.
    public int? RelatedExpenseTransactionId { get; set; }

    // تخصیصِ این دریافت به پیش‌فروش‌ها با CustomerPaymentAllocation انجام می‌شود (تخصیص جزئی و
    // چندگانه)، نه با یک FK مستقیم روی خودِ دریافت. مبلغ، ارز و نرخ روزِ دریافت و سند حسابداری آن
    // هرگز با تخصیص تغییر نمی‌کند.
    public ICollection<CustomerPaymentAllocation> CustomerPaymentAllocations { get; set; }
        = new List<CustomerPaymentAllocation>();
}

public class Sarraf : BaseEntity
{
    [Required, MaxLength(200)] public string Name { get; set; } = "";
    [MaxLength(50)] public string? PhoneNumber { get; set; }
    [MaxLength(300)] public string? Address { get; set; }
    [MaxLength(1000)] public string? Notes { get; set; }
    public bool IsActive { get; set; } = true;

    public ICollection<SarrafSettlement> Settlements { get; set; } = new List<SarrafSettlement>();
    public ICollection<PaymentTransaction> PaymentTransactions { get; set; } = new List<PaymentTransaction>();
}

public class SarrafSettlement : BaseEntity
{
    public DateTime SettlementDate { get; set; }

    // جهت و نوع طرف‌حساب (Phase 1 عمومی‌سازی). پیش‌فرض Out/Supplier تا رفتار قبلی حفظ شود.
    public SarrafSettlementDirection Direction { get; set; } = SarrafSettlementDirection.Out;
    public SarrafSettlementCounterpartyType CounterpartyType { get; set; } = SarrafSettlementCounterpartyType.Supplier;

    public int SarrafId { get; set; }
    public Sarraf? Sarraf { get; set; }
    public int? SupplierId { get; set; }
    public Supplier? Supplier { get; set; }
    // طرف‌حساب‌های جدید (فقط یکی هم‌زمان پر می‌شود، بسته به CounterpartyType).
    public int? CustomerId { get; set; }
    public Customer? Customer { get; set; }
    public int? ServiceProviderId { get; set; }
    public ServiceProvider? ServiceProvider { get; set; }
    public int? DriverId { get; set; }
    public Driver? Driver { get; set; }
    public int? EmployeeId { get; set; }
    public Employee? Employee { get; set; }
    public int? ContractId { get; set; }
    public Contract? Contract { get; set; }
    public int? PaymentTransactionId { get; set; }
    public PaymentTransaction? PaymentTransaction { get; set; }
    public int? CashAccountId { get; set; }
    public CashAccount? CashAccount { get; set; }

    [MaxLength(200)] public string? ReferenceNumber { get; set; }
    [MaxLength(1000)] public string? Description { get; set; }

    public decimal RequestedAmount { get; set; }
    [Required, MaxLength(10)] public string RequestedCurrency { get; set; } = "USD";
    public decimal RequestedFxRateToUsd { get; set; } = 1m;
    public decimal RequestedAmountUsd { get; set; }

    [Required, MaxLength(10)] public string SarrafCurrency { get; set; } = "AFN";
    public decimal SarrafRate { get; set; }
    public decimal SarrafChargedAmount { get; set; }
    public decimal SarrafFxRateToUsd { get; set; }
    public decimal SarrafChargedAmountUsd { get; set; }

    public decimal SupplierAcceptedAmount { get; set; }
    [Required, MaxLength(10)] public string SupplierAcceptedCurrency { get; set; } = "USD";
    public decimal SupplierAcceptedFxRateToUsd { get; set; } = 1m;
    public decimal SupplierAcceptedAmountUsd { get; set; }
    public decimal? SupplierRate { get; set; }

    public decimal DifferenceAmountUsd { get; set; }
    public SarrafSettlementDifferenceType DifferenceType { get; set; } = SarrafSettlementDifferenceType.None;
    // Phase 1 — دلیل تفاوت (فقط ثبت/نمایش، nullable). به منطق تسویه وابسته نیست.
    public DifferenceReason? DifferenceReason { get; set; }
    public SarrafSettlementDifferenceTreatment DifferenceTreatment { get; set; } = SarrafSettlementDifferenceTreatment.AcceptedAmountOnly;
    public SarrafSettlementStatus Status { get; set; } = SarrafSettlementStatus.Draft;

    public DateTime? PostedAtUtc { get; set; }
    public DateTime? CancelledAtUtc { get; set; }
    [MaxLength(500)] public string? CancelReason { get; set; }

    public int? LedgerEntryId { get; set; }
    public LedgerEntry? LedgerEntry { get; set; }
    public int? ExchangeDifferenceLedgerEntryId { get; set; }
    public LedgerEntry? ExchangeDifferenceLedgerEntry { get; set; }
}

public class LedgerEntry : BaseEntity
{
    public DateTime EntryDate { get; set; }
    public LedgerSide Side { get; set; }
    public decimal AmountUsd { get; set; }
    [MaxLength(10)] public string Currency { get; set; } = "USD";
    public decimal? SourceAmount { get; set; }
    [MaxLength(10)] public string? SourceCurrencyCode { get; set; }
    public decimal? AppliedFxRateToUsd { get; set; }

    // نرخِ همان‌طور که کاربر وارد کرده: «۱ دالر = چند واحد ارز منبع» (مثلاً ۷۰).
    // AppliedFxRateToUsd جهت معکوس همین نرخ است و برای محاسبه به‌کار می‌رود، ولی چون
    // 1/70 عددِ متناهی نیست، بازسازیِ ۷۰ از روی آن همیشه ۶۹.۹۹۸۶ می‌دهد. این ستون نرخِ
    // واردشده را بدون معکوس‌سازی نگه می‌دارد تا نمایش و نرخ پیش‌فرض دقیق بماند.
    // Nullable است: سطرهای Legacy آن را ندارند و نرخشان «تخمینی» علامت می‌خورد.
    public decimal? AppliedCurrencyPerUsdRate { get; set; }

    public DateTime? AppliedFxRateDate { get; set; }
    [MaxLength(500)] public string? AppliedFxRateSource { get; set; }
    [MaxLength(500)] public string Description { get; set; } = "";

    // Mandatory traceability (rule #9)
    [Required, MaxLength(50)] public string SourceType { get; set; } = ""; // Sale / Expense / Adjustment / ...
    public int SourceId { get; set; }

    // Human-readable trace reference (e.g. invoice number, BOL, voucher).
    // Optional but indexed; complements (SourceType, SourceId) for fast search.
    [MaxLength(200)] public string? Reference { get; set; }

    // شناسهٔ گروهِ یک عملیاتِ «پرداخت از طریق صراف». آن عملیات دو LedgerEntry مکمل می‌سازد
    // (SupplierViaSarrafPayment + SupplierViaSarrafPayable) که SourceId هر دو = SarrafId است و
    // بین همهٔ پرداخت‌های همان صراف مشترک است، پس کلید یکتای جفت‌سازی نیست. این GroupId تنها
    // کلید قطعیِ اتصال دو ردیفِ یک عملیات است و برای «ثبت/تغییر قرارداد» روی همان جفت لازم است.
    // Nullable است تا رکوردهای Legacy (پیش از افزودن این ستون) دست‌نخورده بمانند.
    public Guid? ViaSarrafGroupId { get; set; }

    public int? ContractId { get; set; }
    public Contract? Contract { get; set; }
    public int? CustomerId { get; set; }
    public Customer? Customer { get; set; }
    public int? SupplierId { get; set; }
    public Supplier? Supplier { get; set; }
    public int? ServiceProviderId { get; set; }
    public ServiceProvider? ServiceProvider { get; set; }
    // راننده/موتروانِ مستقل (مالکِ موترِ شخصی). کرایه/بدهیِ او روی همین حساب می‌نشیند تا صورت‌حساب راننده کامل شود.
    public int? DriverId { get; set; }
    public Driver? Driver { get; set; }
    public int? EmployeeId { get; set; }
    public Employee? Employee { get; set; }
    public int? ShipmentId { get; set; }
    public Shipment? Shipment { get; set; }
}

/// <summary>
/// تسویهٔ مستقیم پول بین دو شریکِ همان قرارداد/قراردادها — یک یادداشتِ حساب شراکت (memorandum).
///
/// این رکورد عمداً هیچ LedgerEntry، هیچ JournalEntry و هیچ ExpenseTransaction نمی‌سازد:
/// مصرف نیست، فروش نیست، پرداخت تأمین‌کننده/مشتری هم نیست. فقط جابه‌جایی پول بین دو شریک
/// است، پس نه Revenue را تغییر می‌دهد و نه Operating Expense را، و P&amp;L قرارداد دست‌نخورده
/// می‌ماند. تنها اثرش روی «صورت‌حساب شراکت» است.
///
/// چرا به CashAccount/Sarraf وصل نیست: در این سیستم پولِ شریک اصلاً از صندوق شرکت نمی‌گذرد
/// (<see cref="PaymentFundingSource.Partner"/> یعنی صندوق/بانک شرکت حرکت نکرده). انتقالِ
/// شریک‌به‌شریک هم همین‌طور است. اگر پولی واقعاً از حساب نقد/بانک/صرافِ شرکت رفته باشد،
/// همان یک <see cref="PaymentTransaction"/> است و باید در روزنامچه ثبت شود؛ ثبت دوبارهٔ آن
/// اینجا یعنی دوبار شمردن. برای همین اینجا حسابداری موازی ساخته نشده و
/// <see cref="Reference"/> جای نوشتنِ شمارهٔ همان سندِ رسمی است.
///
/// اصلاح و حذف با الگوی reverse-and-repost انجام می‌شود: رکورد اشتباه
/// <see cref="IsReversed"/> می‌شود و رکورد درست دوباره ثبت می‌گردد؛ تاریخچه دست‌نخورده می‌ماند.
/// </summary>
public class PartnerSettlement : BaseEntity
{
    public DateTime SettlementDate { get; set; }

    /// <summary>شریکی که پول را پرداخت کرده.</summary>
    public int FromPartnerId { get; set; }
    public Partner? FromPartner { get; set; }

    /// <summary>شریکی که پول را گرفته.</summary>
    public int ToPartnerId { get; set; }
    public Partner? ToPartner { get; set; }

    /// <summary>قرارداد مرجع. خالی یعنی تسویهٔ کلیِ حساب شراکت، نه مربوط به یک قرارداد خاص.</summary>
    public int? ContractId { get; set; }
    public Contract? Contract { get; set; }

    public decimal Amount { get; set; }
    [Required, MaxLength(10)] public string Currency { get; set; } = "USD";
    public decimal AppliedFxRateToUsd { get; set; } = 1m;
    public decimal AmountUsd { get; set; }

    [MaxLength(200)] public string? Reference { get; set; }
    [MaxLength(1000)] public string? Description { get; set; }

    public bool IsReversed { get; set; }
    public DateTime? ReversedAtUtc { get; set; }
    public int? ReversedByUserId { get; set; }
    [MaxLength(500)] public string? ReversalReason { get; set; }
}

public class ContractBalanceTransfer : BaseEntity
{
    public DateTime TransferDate { get; set; }
    public int FromContractId { get; set; }
    public Contract? FromContract { get; set; }
    public int ToContractId { get; set; }
    public Contract? ToContract { get; set; }
    public decimal AmountOriginal { get; set; }
    [Required, MaxLength(10)] public string CurrencyCode { get; set; } = "USD";
    public decimal FxRateToUsd { get; set; }
    public decimal AmountUsd { get; set; }
    public DateTime? FxRateDate { get; set; }
    [MaxLength(500)] public string? FxRateSource { get; set; }
    public int? OriginalPaymentTransactionId { get; set; }
    public PaymentTransaction? OriginalPaymentTransaction { get; set; }
    public decimal? OriginalPaymentFxRateToUsd { get; set; }
    [MaxLength(200)] public string? Reference { get; set; }
    [MaxLength(1000)] public string? Notes { get; set; }
    public bool IsCancelled { get; set; }
}

public enum SupplierPaymentAllocationStatus
{
    Active = 1,
    Reversed = 2
}

// تخصیص پیش‌پرداخت تأمین‌کننده به یک قرارداد خرید.
// این «پرداخت جدید» نیست؛ فقط بخشی از پیش‌پرداخت آزاد تأمین‌کننده را به همان قرارداد منتقل می‌کند،
// بنابراین اثر خالص آن روی مانده کلی تأمین‌کننده صفر است (دو LedgerEntry متوازن مانند ContractBalanceTransfer).
public class SupplierPaymentAllocation : BaseEntity
{
    public int PaymentTransactionId { get; set; }
    public PaymentTransaction? PaymentTransaction { get; set; }
    public int ContractId { get; set; }
    public Contract? Contract { get; set; }

    public DateTime AllocationDate { get; set; }

    // مبلغ مصرف‌شده به ارز پرداخت (مثلاً 650,000 USD)
    public decimal AllocatedPaymentAmount { get; set; }
    [Required, MaxLength(10)] public string PaymentCurrencyCode { get; set; } = "USD";
    public decimal PaymentFxRateToUsd { get; set; } = 1m;

    // ارزش دفتری تاریخی به USD (با نرخ روز پرداخت) — مبنای «کاهش پیش‌پرداخت آزاد».
    // این مقدار هرگز با نرخ روز تخصیص بازارزیابی نمی‌شود تا ارزش تاریخی پیش‌پرداخت دست‌نخورده بماند.
    public decimal AllocatedBookAmountUsd { get; set; }

    // ---- نرخ روز تخصیص (بازارزیابی ارز پرداخت در تاریخ تخصیص) ----
    // «در تاریخ تخصیص، هر ۱ دلار چند واحد از ارز پرداخت است؟» (مثلاً 100 برای RUB).
    // برای پرداخت دالری همیشه ۱ است. رکوردهای قدیمی با 1/PaymentFxRateToUsd پر شده‌اند
    // تا رفتارشان دقیقاً مثل قبل بماند (اختلاف صفر).
    public decimal PaymentCurrencyPerUsdRateAtAllocation { get; set; } = 1m;

    // کنوانسیون داخلی سیستم: AmountUsd = AmountOriginal × FxRateToUsd → همان نرخ بالا به شکل 1/PerUsd.
    public decimal PaymentCurrencyFxRateToUsdAtAllocation { get; set; } = 1m;

    // ارزش همان مبلغ ارز پرداخت به USD با نرخ روز تخصیص — مبنای تسویهٔ قرارداد.
    public decimal AllocatedValueUsdAtAllocation { get; set; }

    // اختلاف تسعیر = ارزش روز تخصیص − ارزش تاریخی. مثبت = سود، منفی = زیان.
    public decimal ExchangeDifferenceUsd { get; set; }

    // نوع اختلاف با همان enum تسویهٔ صراف تا منطق موازی ساخته نشود.
    public SarrafSettlementDifferenceType ExchangeDifferenceType { get; set; } = SarrafSettlementDifferenceType.None;

    // سطر دفترکل سود/زیان تسعیر این تخصیص (مانند SarrafSettlement.ExchangeDifferenceLedgerEntryId).
    public int? ExchangeDifferenceLedgerEntryId { get; set; }
    public LedgerEntry? ExchangeDifferenceLedgerEntry { get; set; }

    // ارز قرارداد و نرخ‌های قفل‌شده
    [Required, MaxLength(10)] public string ContractCurrencyCode { get; set; } = "USD";
    // نرخ قابل‌فهم: «هر ۱ دلار چند واحد ارز قرارداد» (مثلاً 80 برای RUB/USD)
    public decimal ContractCurrencyPerUsdRate { get; set; } = 1m;
    // کنوانسیون داخلی سیستم: AmountUsd = AmountOriginal × FxRateToUsd → برای ارز قرارداد = 1 / PerUsd
    public decimal ContractCurrencyFxRateToUsd { get; set; } = 1m;
    // معادل ارز قرارداد (مثلاً 52,000,000 RUB)
    public decimal AllocatedContractCurrencyAmount { get; set; }

    [MaxLength(200)] public string? ReferenceNumber { get; set; }
    [MaxLength(1000)] public string? Notes { get; set; }

    public SupplierPaymentAllocationStatus Status { get; set; } = SupplierPaymentAllocationStatus.Active;

    // اصلاح فقط با «برگشت تخصیص»؛ این رکورد ویرایش/حذف نمی‌شود.
    public int? ReversalOfAllocationId { get; set; }
    public DateTime? ReversedAtUtc { get; set; }
    [MaxLength(150)] public string? ReversedByUserName { get; set; }
    [MaxLength(500)] public string? ReversalReason { get; set; }

    [MaxLength(150)] public string? CreatedByUserName { get; set; }
}

public enum SupplierBalanceTransferStatus
{
    Active = 1,
    Reversed = 2
}

/// <summary>
/// انتقال «مانده قابل انتقال» تأمین‌کننده به یک قرارداد خرید همان تأمین‌کننده.
///
/// این پرداخت جدید نیست و صندوق/بانک را تکان نمی‌دهد؛ فقط طلبِ آزادِ شرکت از تأمین‌کننده را
/// به یک قرارداد وصل می‌کند. برخلاف SupplierPaymentAllocation به یک PaymentTransaction
/// وابسته نیست: منبع آن مانده واقعی حساب تأمین‌کننده است، از هر مسیری که ساخته شده باشد
/// (پرداخت مستقیم، صراف، مانده اول دوره، اصلاح حساب، تسویه سه‌طرفه، اضافه‌پرداخت قرارداد).
///
/// مقدار اصلی ارز و نرخ تاریخی گم نمی‌شود: هر انتقال از یک یا چند سطر دفتر تغذیه می‌شود که
/// در <see cref="Sources"/> ثبت می‌شوند. مانند تخصیص پیش‌پرداخت، سه سطر متوازن می‌سازد:
///   Credit با ContractId = null  → کاهش مانده قابل انتقال به ارزش تاریخی
///   Debit  با ContractId = قرارداد → ثبت ارزش روز انتقال روی قرارداد مقصد
///   سطر سوم فقط هنگام اختلاف دو ارزش: سود/زیان نرخ ارز
/// بنابراین اثر خالص روی مانده کلی تأمین‌کننده صفر است.
///
/// نرخ‌ها و مبالغ هنگام ثبت قفل می‌شوند؛ اصلاح فقط با «برگشت انتقال» انجام می‌شود.
/// </summary>
public class SupplierBalanceTransfer : BaseEntity
{
    // انتقال یک‌بارهٔ چند قرارداد در یک فورم؛ همهٔ سطرهای یک ثبت این شناسه را مشترک دارند.
    public Guid BatchId { get; set; }

    public int SupplierId { get; set; }
    public Supplier? Supplier { get; set; }

    // مالکیت شرکت از قرارداد مقصد گرفته می‌شود (LedgerEntry ستون CompanyId ندارد).
    public int CompanyId { get; set; }
    public Company? Company { get; set; }

    public int ContractId { get; set; }
    public Contract? Contract { get; set; }

    public DateTime TransferDate { get; set; }

    // مقدار اصلی ارز که منتقل می‌شود (مثلاً 2,000,000 RUB).
    public decimal TransferOriginalAmount { get; set; }
    [Required, MaxLength(10)] public string OriginalCurrencyCode { get; set; } = "USD";

    // نرخ تاریخیِ وزنیِ سطرهای مصرف‌شده. کنوانسیون سیستم: AmountUsd = AmountOriginal × FxRateToUsd.
    public decimal HistoricalFxRateToUsd { get; set; } = 1m;

    // همان نرخ تاریخی در جهت مستقیم: «۱ دالر = چند واحد ارز». از snapshot خودِ منابع می‌آید،
    // نه از معکوس‌کردن HistoricalFxRateToUsd. Nullable است چون منابع Legacy نرخ مستقیم ندارند.
    public decimal? HistoricalCurrencyPerUsdRate { get; set; }

    // true یعنی نرخ تاریخی از روی مبالغ بازسازی شده (منبع Legacy) و قطعی نیست.
    public bool HistoricalRateIsEstimated { get; set; }

    // ارزش دفتری تاریخی مصرف‌شده — مبنای کاهش مانده قابل انتقال. هرگز بازارزیابی نمی‌شود.
    public decimal HistoricalAmountUsd { get; set; }

    // نرخ روز انتقال: «۱ دالر = چند واحد ارز مانده؟» (مثلاً ۱۰۰ برای روبل). برای دالر همیشه ۱.
    public decimal TransferPerUsdRate { get; set; } = 1m;
    public decimal TransferFxRateToUsd { get; set; } = 1m;

    // ارزش همان مقدار ارز به دالر با نرخ روز انتقال — مبنای ثبت روی قرارداد مقصد.
    public decimal TransferValueUsd { get; set; }

    // اختلاف نرخ = ارزش روز انتقال − ارزش تاریخی. مثبت = سود، منفی = زیان.
    public decimal ExchangeDifferenceUsd { get; set; }

    // همان enum تسویهٔ صراف تا منطق موازی ساخته نشود.
    public SarrafSettlementDifferenceType ExchangeDifferenceType { get; set; } = SarrafSettlementDifferenceType.None;

    public int? ExchangeDifferenceLedgerEntryId { get; set; }
    public LedgerEntry? ExchangeDifferenceLedgerEntry { get; set; }

    // ارز قرارداد مقصد و نرخ‌های قفل‌شده.
    [Required, MaxLength(10)] public string ContractCurrencyCode { get; set; } = "USD";
    public decimal ContractCurrencyPerUsdRate { get; set; } = 1m;
    public decimal ContractCurrencyFxRateToUsd { get; set; } = 1m;
    public decimal TransferContractCurrencyAmount { get; set; }

    [MaxLength(200)] public string? ReferenceNumber { get; set; }
    [MaxLength(1000)] public string? Notes { get; set; }

    public SupplierBalanceTransferStatus Status { get; set; } = SupplierBalanceTransferStatus.Active;

    public DateTime? ReversedAtUtc { get; set; }
    [MaxLength(150)] public string? ReversedByUserName { get; set; }
    [MaxLength(500)] public string? ReversalReason { get; set; }

    [MaxLength(150)] public string? CreatedByUserName { get; set; }

    public ICollection<SupplierBalanceTransferSource> Sources { get; set; }
        = new List<SupplierBalanceTransferSource>();
}

/// <summary>
/// ردیابی منبع: هر انتقال از کدام سطرهای دفتر تأمین‌کننده و با کدام نرخ تاریخی تغذیه شده است.
/// ترتیب مصرف FIFO است (قدیمی‌ترین پول اول مصرف می‌شود)، بنابراین آنچه باقی می‌ماند تازه‌ترین
/// پولِ مصرف‌نشده است. این سطرها فقط سند ردیابی‌اند و هیچ اثر مالی مستقلی ندارند.
/// </summary>
public class SupplierBalanceTransferSource : BaseEntity
{
    public int SupplierBalanceTransferId { get; set; }
    public SupplierBalanceTransfer? SupplierBalanceTransfer { get; set; }

    [Required, MaxLength(50)] public string SourceType { get; set; } = "";
    public int SourceId { get; set; }
    public int? LedgerEntryId { get; set; }
    public DateTime SourceDate { get; set; }

    public decimal ConsumedOriginalAmount { get; set; }
    [Required, MaxLength(10)] public string OriginalCurrencyCode { get; set; } = "USD";
    public decimal HistoricalFxRateToUsd { get; set; } = 1m;

    // snapshot نرخ مستقیم همین منبع. هنگام ثبت انتقال، ارزش تاریخی هر منبع با نرخ خودش
    // مصرف می‌شود؛ این ستون همان نرخ را برای ردیابی و برگشت قفل می‌کند.
    public decimal? HistoricalCurrencyPerUsdRate { get; set; }

    public decimal ConsumedBookAmountUsd { get; set; }
}

public enum CustomerPaymentAllocationStatus
{
    Active = 1,
    Reversed = 2
}

// تخصیص یک دریافت از مشتری به یک پیش‌فروش. این «دریافت جدید» نیست و هیچ سند مالی نمی‌سازد؛
// فقط می‌گوید چه بخشی از پول دریافت‌شده به کدام تعهد تعلق دارد.
//
// چرا برخلاف SupplierPaymentAllocation نرخ روز تخصیص و اختلاف تسعیر ندارد: تخصیصِ تأمین‌کننده
// بدهی یک قرارداد را همان لحظه تسویه می‌کند، پس اختلاف ارزش همان‌جا تحقق می‌یابد. اینجا کالا هنوز
// تحویل نشده و پیش‌دریافت مشتری یک بدهیِ ارزی به همان ارز و نرخ روزِ دریافت باقی می‌ماند؛
// شناسایی سود/زیان تسعیر پیش از تحویل یعنی ساختن سودِ تحقق‌نیافته. مصرف پیش‌دریافت و اثر مالی
// آن دقیقاً هنگام تحویل و در ژورنال همان تحویل انجام می‌شود.
public class CustomerPaymentAllocation : BaseEntity
{
    public int PaymentTransactionId { get; set; }
    public PaymentTransaction? PaymentTransaction { get; set; }
    public int PreSaleOrderId { get; set; }
    public PreSaleOrder? PreSaleOrder { get; set; }

    public DateTime AllocationDate { get; set; }

    // مبلغ تخصیص‌یافته به ارز خودِ دریافت (تخصیص جزئی مجاز است).
    public decimal AllocatedPaymentAmount { get; set; }
    [Required, MaxLength(10)] public string PaymentCurrencyCode { get; set; } = "USD";
    // نرخ روزِ دریافت، همان‌طور که روی PaymentTransaction ثبت شده؛ هیچ نرخ جدیدی ساخته نمی‌شود.
    public decimal PaymentFxRateToUsd { get; set; } = 1m;
    // ارزش دفتری همین تخصیص به USD با همان نرخ روز دریافت.
    public decimal AllocatedAmountUsd { get; set; }

    [MaxLength(200)] public string? ReferenceNumber { get; set; }
    [MaxLength(1000)] public string? Notes { get; set; }

    public CustomerPaymentAllocationStatus Status { get; set; } = CustomerPaymentAllocationStatus.Active;

    // اصلاح فقط با «برگشت تخصیص» (مانند SupplierPaymentAllocation)؛ رکورد حذف نمی‌شود.
    public int? ReversalOfAllocationId { get; set; }
    public DateTime? ReversedAtUtc { get; set; }
    [MaxLength(150)] public string? ReversedByUserName { get; set; }
    [MaxLength(500)] public string? ReversalReason { get; set; }

    [MaxLength(150)] public string? CreatedByUserName { get; set; }
}

public enum CustomerPaymentAllocationApplicationStatus
{
    Active = 1,
    Reversed = 2
}

// مصرفِ ردیابی‌پذیرِ یک تخصیصِ دریافت (CustomerPaymentAllocation) روی یک تحویل (SalesTransaction).
//
// چرا این موجودیت لازم است: پیش‌تر مصرفِ پیش‌دریافت فقط از فرمول «تخصیصِ کل منهای مجموع تحویل‌های
// قبلی» حدس زده می‌شد و مشخص نبود کدام تخصیص در کدام تحویل و چه مقدار مصرف شده است. هر ردیفِ
// Application دقیقاً یک بند از این مصرف را نگه می‌دارد: کدام تخصیص، کدام تحویل، چند دلار. مانده
// مصرف‌نشدهٔ هر تخصیص = AllocatedAmountUsd منهای مجموع Applicationهای فعالِ آن، و پوششِ پیش‌دریافتِ
// هر تحویل = مجموع Applicationهای فعالِ همان تحویل. هیچ مصرفی دو بار شمرده نمی‌شود چون هر بند
// هم‌زمان از هر دو سمت کم می‌کند.
public class CustomerPaymentAllocationApplication : BaseEntity
{
    public int CustomerPaymentAllocationId { get; set; }
    public CustomerPaymentAllocation? CustomerPaymentAllocation { get; set; }
    public int SalesTransactionId { get; set; }
    public SalesTransaction? SalesTransaction { get; set; }

    public DateTime AppliedAt { get; set; }

    // مبلغ مصرف‌شده به ارز خودِ دریافت (فقط اطلاعاتی؛ مبنای حسابداری همیشه USD تاریخی است).
    public decimal AppliedPaymentAmount { get; set; }
    [Required, MaxLength(10)] public string PaymentCurrencyCode { get; set; } = "USD";
    // ارزش تاریخیِ مصرف‌شده به USD (با همان نرخ روزِ دریافتِ تخصیص). مبنای مصرف و برگشت.
    public decimal AppliedAmountUsd { get; set; }

    public CustomerPaymentAllocationApplicationStatus Status { get; set; } = CustomerPaymentAllocationApplicationStatus.Active;

    // اصلاح فقط با برگشت؛ رکورد حذف نمی‌شود.
    public int? ReversalOfApplicationId { get; set; }
    public DateTime? ReversedAtUtc { get; set; }
    [MaxLength(150)] public string? ReversedByUserName { get; set; }
    [MaxLength(500)] public string? ReversalReason { get; set; }

    // فقط برای مصرفِ «تخصیص بعد از تحویل» پر می‌شود: ژورنالِ انتقالِ مستقلِ متوازن
    // (پیش‌دریافت مشتری بدهکار، مطالبات مشتری بستانکار). در مصرفِ «هنگام تحویل»، اثر مالی داخل
    // خودِ ژورنالِ همان تحویل است، پس این فیلد null می‌ماند.
    public int? JournalEntryId { get; set; }
    public JournalEntry? JournalEntry { get; set; }

    public int? CompanyId { get; set; }
    public Company? Company { get; set; }

    [MaxLength(150)] public string? CreatedByUserName { get; set; }
}

public enum SalesCostConsumptionStatus
{
    Active = 1,
    Reversed = 2
}

// بهای واقعیِ مصرف‌شده از هر pool موجودی (company, product, terminal) برای یک تحویل.
//
// چرا لازم است: هنگام برگشت COGS باید دقیقاً همان مقدار و همان ارزشی که هنگام فروش از هر pool
// خارج شد به همان pool برگردد. اگر بهای کلِ ژورنال با نسبتِ مقدار بین چند مخزن تقسیم شود
// (مثلاً مخزنی با ۲ دلار و مخزنی با ۵ دلار)، ارزشِ اشتباه به poolها برمی‌گردد و بهای فروش‌های
// بعدی خراب می‌شود. این ردیف بهای واقعیِ هر pool را در لحظهٔ فروش قفل می‌کند تا برگشت دقیق باشد.
public class SalesCostConsumption : BaseEntity
{
    public int SalesTransactionId { get; set; }
    public SalesTransaction? SalesTransaction { get; set; }
    public int CompanyId { get; set; }
    public int ProductId { get; set; }
    public int TerminalId { get; set; }
    public decimal QuantityMt { get; set; }
    public decimal CostUsd { get; set; }
    public SalesCostConsumptionStatus Status { get; set; } = SalesCostConsumptionStatus.Active;
    public DateTime? ReversedAtUtc { get; set; }
}

public static class AuditLogCategories
{
    public const string Entity = "Entity";
    public const string Request = "Request";
    public const string Authentication = "Authentication";
    public const string Security = "Security";
    public const string System = "System";
}

public class AuditLog : BaseEntity
{
    public DateTime ActionAtUtc { get; set; } = DateTime.UtcNow;
    public int? ActorUserId { get; set; }
    [MaxLength(150)] public string? ActorUsername { get; set; }
    [Required, MaxLength(30)] public string Category { get; set; } = AuditLogCategories.Entity;
    [MaxLength(100)] public string? Module { get; set; }
    [Required, MaxLength(100)] public string EntityName { get; set; } = "";
    public int EntityId { get; set; }
    [Required, MaxLength(20)] public string Action { get; set; } = ""; // Insert / Update / Delete
    [MaxLength(500)] public string? Description { get; set; }
    [MaxLength(4000)] public string? Diff { get; set; }
    [MaxLength(10)] public string? HttpMethod { get; set; }
    [MaxLength(260)] public string? RequestPath { get; set; }
    [MaxLength(100)] public string? ControllerName { get; set; }
    [MaxLength(100)] public string? ActionName { get; set; }
    public int? StatusCode { get; set; }
    public bool IsSuccess { get; set; } = true;
    [MaxLength(80)] public string? CorrelationId { get; set; }
    [MaxLength(80)] public string? IpAddress { get; set; }
    [MaxLength(1000)] public string? UserAgent { get; set; }
    public long? DurationMs { get; set; }
    [MaxLength(4000)] public string? MetadataJson { get; set; }
}
