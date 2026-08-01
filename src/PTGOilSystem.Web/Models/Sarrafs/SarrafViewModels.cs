using System.ComponentModel.DataAnnotations;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.Shared;
using PTGOilSystem.Web.Services.Time;

namespace PTGOilSystem.Web.Models.Sarrafs;

public static class SarrafSettlementLabels
{
    public static string ToPersian(SarrafSettlementStatus status) => status switch
    {
        SarrafSettlementStatus.Draft => "پیش نویس",
        SarrafSettlementStatus.Posted => "ثبت شده",
        SarrafSettlementStatus.Cancelled => "لغو شده",
        _ => status.ToString()
    };

    public static string ToPersian(SarrafSettlementDifferenceTreatment treatment) => treatment switch
    {
        SarrafSettlementDifferenceTreatment.AcceptedAmountOnly => "فقط مبلغ قبول شده",
        SarrafSettlementDifferenceTreatment.RecognizeExchangeGainLoss => "شناسایی سود/زیان ارزی",
        _ => treatment.ToString()
    };

    public static string ToPersian(SarrafSettlementDifferenceType type) => type switch
    {
        SarrafSettlementDifferenceType.None => "بدون اختلاف",
        SarrafSettlementDifferenceType.Gain => "سود ارزی",
        SarrafSettlementDifferenceType.Loss => "زیان ارزی",
        SarrafSettlementDifferenceType.SupplierShortfall => "کسری تأمین کننده",
        _ => type.ToString()
    };

    public static string ToPersian(SarrafSettlementDirection direction) => direction switch
    {
        SarrafSettlementDirection.Out => "پرداخت از طریق صراف",
        SarrafSettlementDirection.In => "دریافت از طریق صراف",
        _ => direction.ToString()
    };

    public static string ToPersian(SarrafSettlementCounterpartyType type) => type switch
    {
        SarrafSettlementCounterpartyType.Supplier => "تأمین‌کننده",
        SarrafSettlementCounterpartyType.Customer => "مشتری",
        SarrafSettlementCounterpartyType.ServiceProvider => "شرکت خدماتی",
        SarrafSettlementCounterpartyType.Driver => "راننده",
        SarrafSettlementCounterpartyType.Employee => "کارمند",
        _ => type.ToString()
    };

    // نام نمایشیِ کاملِ عملیات بر اساس جهت و نوع طرف‌حساب.
    public static string OperationName(SarrafSettlementDirection direction, SarrafSettlementCounterpartyType type) => (direction, type) switch
    {
        (SarrafSettlementDirection.Out, SarrafSettlementCounterpartyType.Supplier) => "پرداخت به تأمین‌کننده از طریق صراف",
        (SarrafSettlementDirection.In, SarrafSettlementCounterpartyType.Customer) => "دریافت از مشتری از طریق صراف",
        (SarrafSettlementDirection.Out, SarrafSettlementCounterpartyType.ServiceProvider) => "پرداخت به شرکت خدماتی از طریق صراف",
        (SarrafSettlementDirection.Out, SarrafSettlementCounterpartyType.Driver) => "پرداخت به راننده از طریق صراف",
        (SarrafSettlementDirection.Out, SarrafSettlementCounterpartyType.Employee) => "پرداخت به کارمند از طریق صراف",
        _ => $"{ToPersian(direction)} ({ToPersian(type)})"
    };

    // Phase 1 — دلیل تفاوت به دری.
    public static string ToPersian(DifferenceReason? reason) => reason switch
    {
        DifferenceReason.FxDifference => "تفاوت نرخ",
        DifferenceReason.Commission => "کمیشن",
        DifferenceReason.TransferFee => "کرایه حواله",
        DifferenceReason.BrokerMargin => "مارجین دلال",
        DifferenceReason.Discount => "تخفیف",
        DifferenceReason.Adjustment => "اصلاح حساب",
        DifferenceReason.Other => "سایر",
        _ => "—"
    };
}

public sealed class SarrafIndexViewModel
{
    public string? Search { get; init; }
    public IReadOnlyList<SarrafIndexItemViewModel> Items { get; init; } = [];
    public decimal TotalPayableUsd { get; init; }
    public decimal TotalChargedUsd { get; init; }
    public decimal TotalPaidUsd { get; init; }
    public int ActiveCount { get; init; }
}

public sealed class SarrafIndexItemViewModel
{
    public int Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? PhoneNumber { get; init; }
    public bool IsActive { get; init; }
    public int SettlementCount { get; init; }
    public decimal ChargedUsd { get; init; }
    public decimal PaidUsd { get; init; }
    public decimal PayableUsd => ChargedUsd - PaidUsd;
    public DateTime? LastSettlementDate { get; init; }
}

public sealed class SarrafFormViewModel
{
    public int Id { get; set; }

    [Required(ErrorMessage = "نام صراف الزامی است.")]
    [StringLength(200)]
    [Display(Name = "نام صراف")]
    public string Name { get; set; } = string.Empty;

    [StringLength(50)]
    [Display(Name = "شماره تماس")]
    public string? PhoneNumber { get; set; }

    [StringLength(300)]
    [Display(Name = "آدرس")]
    public string? Address { get; set; }

    [StringLength(1000)]
    [Display(Name = "یادداشت")]
    public string? Notes { get; set; }

    [Display(Name = "فعال")]
    public bool IsActive { get; set; } = true;
}

public sealed class SarrafDetailsViewModel
{
    public int Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? PhoneNumber { get; init; }
    public string? Address { get; init; }
    public string? Notes { get; init; }
    public bool IsActive { get; init; }
    public DateTime CreatedAtUtc { get; init; }
    public decimal RequestedUsd { get; init; }
    public decimal ChargedUsd { get; init; }
    public decimal AcceptedUsd { get; init; }
    public decimal PaidUsd { get; init; }
    public decimal PayableUsd => ChargedUsd - PaidUsd;
    public decimal SupplierShortfallUsd { get; init; }
    public decimal ExchangeGainUsd { get; init; }
    public decimal ExchangeLossUsd { get; init; }
    public IReadOnlyList<SarrafSettlementListItemViewModel> Settlements { get; init; } = [];
    public IReadOnlyList<SarrafPaymentStatementItemViewModel> Payments { get; init; } = [];

    // صورت‌حساب خطی read-only: ادغام تسویه‌های Posted + پرداخت‌ها به ترتیب تاریخ با مانده تجمعی.
    // مانده فقط از همین دو منبع ساخته می‌شود (نه LedgerEntries) و RunningBalance = Charged - Paid.
    public IReadOnlyList<PartyStatementRowViewModel> StatementRows { get; init; } = [];

    // فاز A — trace-only: حواله‌های مشتری که از طریق این صراف به تأمین‌کننده رسیده‌اند.
    // این فقط نمایش/ردیابی است و هیچ اثری روی مانده صراف ندارد (مانده از تسویه‌ها و پرداخت‌ها می‌آید).
    public IReadOnlyList<SarrafCustomerHawalaTraceItemViewModel> CustomerHawalas { get; init; } = [];
}

// فاز A — یک حواله سه‌طرفه که این صراف فقط واسطه آن بوده است (trace-only).
public sealed class SarrafCustomerHawalaTraceItemViewModel
{
    public int Id { get; init; }
    public DateTime SettlementDate { get; init; }
    public string? CustomerName { get; init; }
    public string? SupplierName { get; init; }
    public string? HawalaReference { get; init; }
    public decimal CustomerPaidUsd { get; init; }
    public decimal SupplierAcceptedUsd { get; init; }
    public string Currency { get; init; } = "USD";
    public ThreeWaySettlementStatus Status { get; init; }
    public string StatusName => Status switch
    {
        ThreeWaySettlementStatus.Posted => "ثبت شده",
        ThreeWaySettlementStatus.Cancelled => "لغو شده",
        _ => "پیش‌نویس"
    };
}

public sealed class SarrafPaymentStatementItemViewModel
{
    public int PaymentId { get; init; }
    public DateTime PaymentDate { get; init; }
    public PaymentDirection Direction { get; init; }
    public string CashAccountName { get; init; } = string.Empty;
    public decimal Amount { get; init; }
    public string Currency { get; init; } = "USD";
    public decimal AppliedFxRateToUsd { get; init; }
    public decimal AmountUsd { get; init; }
    public string? Reference { get; init; }
    public string? Description { get; init; }
    public int? LedgerEntryId { get; init; }
    public string UserStatusName => Direction == PaymentDirection.Out ? "تسویه‌شده" : "برگشت پرداخت";
}

public class SarrafSettlementListItemViewModel
{
    public int Id { get; init; }
    public DateTime SettlementDate { get; init; }
    public string SarrafName { get; init; } = string.Empty;
    public SarrafSettlementDirection Direction { get; init; } = SarrafSettlementDirection.Out;
    public SarrafSettlementCounterpartyType CounterpartyType { get; init; } = SarrafSettlementCounterpartyType.Supplier;
    public int? SupplierId { get; init; }
    public string? SupplierName { get; init; }
    public int? CustomerId { get; init; }
    public string? CustomerName { get; init; }
    public int? ServiceProviderId { get; init; }
    public string? ServiceProviderName { get; init; }
    public int? DriverId { get; init; }
    public string? DriverName { get; init; }
    public int? EmployeeId { get; init; }
    public string? EmployeeName { get; init; }
    // نام طرف‌حساب بسته به نوع (برای نمایش یک‌دست در جدول‌ها).
    public string CounterpartyName => CounterpartyType switch
    {
        SarrafSettlementCounterpartyType.Customer => CustomerName ?? "—",
        SarrafSettlementCounterpartyType.ServiceProvider => ServiceProviderName ?? "—",
        SarrafSettlementCounterpartyType.Driver => DriverName ?? "—",
        SarrafSettlementCounterpartyType.Employee => EmployeeName ?? "—",
        _ => SupplierName ?? "—"
    };
    public string DirectionName => SarrafSettlementLabels.ToPersian(Direction);
    public string CounterpartyTypeName => SarrafSettlementLabels.ToPersian(CounterpartyType);
    public string OperationName => SarrafSettlementLabels.OperationName(Direction, CounterpartyType);
    public int? ContractId { get; init; }
    public string? ContractNumber { get; init; }
    public string? ReferenceNumber { get; init; }
    public decimal RequestedAmount { get; init; }
    public string RequestedCurrency { get; init; } = "USD";
    public decimal RequestedFxRateToUsd { get; init; }
    public decimal RequestedAmountUsd { get; init; }
    public decimal SarrafChargedAmount { get; init; }
    public string SarrafCurrency { get; init; } = "AFN";
    public decimal SarrafFxRateToUsd { get; init; }
    public decimal SarrafChargedAmountUsd { get; init; }
    public decimal SupplierAcceptedAmount { get; init; }
    public string SupplierAcceptedCurrency { get; init; } = "USD";
    public decimal SupplierAcceptedFxRateToUsd { get; init; }
    public decimal SupplierAcceptedAmountUsd { get; init; }
    public decimal DifferenceAmountUsd { get; init; }
    public SarrafSettlementDifferenceType DifferenceType { get; init; }
    public string DifferenceTypeName => SarrafSettlementLabels.ToPersian(DifferenceType);
    public SarrafSettlementDifferenceTreatment DifferenceTreatment { get; init; }
    public string DifferenceTreatmentName => SarrafSettlementLabels.ToPersian(DifferenceTreatment);
    public DifferenceReason? DifferenceReason { get; init; }
    public SarrafSettlementStatus Status { get; init; }
    public string StatusName => SarrafSettlementLabels.ToPersian(Status);
    public string UserStatusName
    {
        get
        {
            if (Status == SarrafSettlementStatus.Cancelled) return "لغو شده";
            if (Status != SarrafSettlementStatus.Posted) return "پیش‌نویس";
            return DifferenceAmountUsd == 0m ? "طلب صراف" : "دارای تفاوت";
        }
    }
    public int? LedgerEntryId { get; init; }
    public int? ExchangeDifferenceLedgerEntryId { get; init; }
}

public sealed class SarrafSettlementCreateViewModel
{
    // در حالت ویرایش پر می‌شود؛ در ثبت جدید null است.
    public int? Id { get; set; }

    [Display(Name = "تاریخ تسویه")]
    [DataType(DataType.Date)]
    public DateTime SettlementDate { get; set; } = AfghanistanBusinessClock.SystemToday;

    [Range(1, int.MaxValue, ErrorMessage = "انتخاب صراف الزامی است.")]
    [Display(Name = "صراف")]
    public int SarrafId { get; set; }

    [Display(Name = "تأمین کننده")]
    public int? SupplierId { get; set; }

    [Display(Name = "قرارداد")]
    public int? ContractId { get; set; }

    [Display(Name = "پرداخت مرتبط")]
    public int? PaymentTransactionId { get; set; }

    [Display(Name = "حساب نقد/بانک")]
    public int? CashAccountId { get; set; }

    [StringLength(200)]
    [Display(Name = "شماره مرجع")]
    public string? ReferenceNumber { get; set; }

    [StringLength(1000)]
    [Display(Name = "شرح")]
    public string? Description { get; set; }

    [Range(typeof(decimal), "0.0001", "79228162514264337593543950335")]
    [Display(Name = "مبلغ درخواست شرکت")]
    public decimal RequestedAmount { get; set; }

    [Required, StringLength(10)]
    [Display(Name = "ارز درخواست")]
    public string RequestedCurrency { get; set; } = "USD";

    [Range(typeof(decimal), "0.000001", "79228162514264337593543950335")]
    [Display(Name = "نرخ درخواست به USD")]
    public decimal RequestedFxRateToUsd { get; set; } = 1m;

    [Required, StringLength(10)]
    [Display(Name = "ارز پرداختی صراف")]
    public string SarrafCurrency { get; set; } = "AFN";

    [Range(typeof(decimal), "0.000001", "79228162514264337593543950335")]
    [Display(Name = "نرخ صراف")]
    public decimal SarrafRate { get; set; }

    [Range(typeof(decimal), "0.0001", "79228162514264337593543950335")]
    [Display(Name = "مبلغی که صراف از شرکت می گیرد")]
    public decimal SarrafChargedAmount { get; set; }

    [Range(typeof(decimal), "0.000001", "79228162514264337593543950335")]
    [Display(Name = "نرخ مبلغ صراف به USD")]
    public decimal SarrafFxRateToUsd { get; set; }

    [Range(typeof(decimal), "0.0001", "79228162514264337593543950335")]
    [Display(Name = "مبلغ قبول شده تأمین کننده")]
    public decimal SupplierAcceptedAmount { get; set; }

    [Required, StringLength(10)]
    [Display(Name = "ارز مبلغ قبول شده")]
    public string SupplierAcceptedCurrency { get; set; } = "USD";

    [Range(typeof(decimal), "0.000001", "79228162514264337593543950335")]
    [Display(Name = "نرخ مبلغ قبول شده به USD")]
    public decimal SupplierAcceptedFxRateToUsd { get; set; } = 1m;

    [Display(Name = "نرخ قبول شده تأمین کننده")]
    public decimal? SupplierRate { get; set; }

    [Display(Name = "رفتار اختلاف")]
    public SarrafSettlementDifferenceTreatment DifferenceTreatment { get; set; } = SarrafSettlementDifferenceTreatment.AcceptedAmountOnly;

    // Phase 1 — دلیل تفاوت (فقط ثبت/نمایش). به محاسبهٔ تسویه اثر ندارد.
    [Display(Name = "دلیل تفاوت")]
    public DifferenceReason? DifferenceReason { get; set; }

    public string? ReturnUrl { get; set; }
}

public sealed class SarrafSettlementDetailsViewModel : SarrafSettlementListItemViewModel
{
    public string? Description { get; init; }
    public decimal SarrafRate { get; init; }
    public decimal? SupplierRate { get; init; }
    public DateTime? PostedAtUtc { get; init; }
    public DateTime? CancelledAtUtc { get; init; }
    public string? CancelReason { get; init; }
}
