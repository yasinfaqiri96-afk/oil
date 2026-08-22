using System.ComponentModel.DataAnnotations;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services.Time;

namespace PTGOilSystem.Web.Models.Payments;

public static class PaymentDirectionLabels
{
    public static string ToPersian(PaymentDirection direction) => direction switch
    {
        PaymentDirection.In => "دریافت",
        PaymentDirection.Out => "پرداخت",
        _ => direction.ToString()
    };
}

// برچسب دری «پول این پرداخت را چه کسی داد». فقط نمایشی.
public static class PaymentFundingSourceLabels
{
    public static string ToPersian(PaymentFundingSource source) => source switch
    {
        PaymentFundingSource.Company => "شرکت",
        PaymentFundingSource.Partner => "شریک",
        _ => source.ToString()
    };
}

public static class PaymentKindLabels
{
    public static string ToPersian(PaymentKind paymentKind) => paymentKind switch
    {
        PaymentKind.CustomerReceipt => "دریافت از مشتری",
        PaymentKind.SupplierPayment => "پرداخت به تأمین‌کننده",
        PaymentKind.ExpensePayment => "پرداخت مصرف",
        PaymentKind.TruckPayment => "پرداخت کرایه / موتر",
        PaymentKind.ManualPayment => "پرداخت دستی",
        PaymentKind.ManualReceipt => "دریافت دستی",
        PaymentKind.EmployeeSalaryPayment => "پرداخت معاش کارمند",
        PaymentKind.EmployeeSalaryAdvance => "برداشت / پیش‌پرداخت کارمند",
        PaymentKind.SupplierReceipt => "دریافت از تأمین‌کننده",
        PaymentKind.CustomerPayment => "پرداخت به مشتری",
        PaymentKind.EmployeeReturn => "دریافت / برگشت از کارمند",
        PaymentKind.ServiceProviderPayment => "پرداخت به شرکت خدماتی",
        PaymentKind.SarrafSettlement => "پرداخت به صراف",
        PaymentKind.CommissionPayment => "پرداخت کمیسیون",
        _ => paymentKind.ToString()
    };
}

// نوع محاسبهٔ کمیسیون در فرم روزنامچه.
public enum PaymentCommissionType
{
    Percent = 1,
    Fixed = 2
}

public static class PaymentCommissionTypeLabels
{
    public static string ToPersian(PaymentCommissionType value) => value switch
    {
        PaymentCommissionType.Percent => "درصدی",
        PaymentCommissionType.Fixed => "مبلغ ثابت",
        _ => value.ToString()
    };
}

// روش پرداخت در فرم روزنامچه: نقد/بانک (حالت عادی) یا از طریق صراف (ثبت تسویه صراف پشت‌صحنه).
public enum PaymentMethod
{
    CashBank = 0,
    ViaSarraf = 1
}

public enum PaymentCounterpartyType
{
    Other = 0,
    Supplier = 1,
    Customer = 2,
    Employee = 3,
    Driver = 4,
    OfficeExpense = 5,
    Contract = 6,
    Sales = 7,
    Shipment = 8,
    ServiceProvider = 9,
    Sarraf = 10
}

public static class PaymentCounterpartyTypeLabels
{
    public static string ToPersian(PaymentCounterpartyType value) => value switch
    {
        PaymentCounterpartyType.Supplier => "تأمین‌کننده",
        PaymentCounterpartyType.Customer => "مشتری",
        PaymentCounterpartyType.Employee => "کارمند",
        PaymentCounterpartyType.Driver => "راننده",
        PaymentCounterpartyType.OfficeExpense => "مصرف دفتری",
        PaymentCounterpartyType.Contract => "قرارداد",
        PaymentCounterpartyType.Sales => "فروش / فاکتور",
        PaymentCounterpartyType.Shipment => "Shipment",
        PaymentCounterpartyType.Other => "متفرقه",
        PaymentCounterpartyType.ServiceProvider => "شرکت خدماتی",
        PaymentCounterpartyType.Sarraf => "صراف",
        _ => value.ToString()
    };
}

public sealed class PaymentCreateViewModel
{
    public int Id { get; set; }

    [Display(Name = "تاریخ")]
    [DataType(DataType.Date)]
    public DateTime PaymentDate { get; set; } = AfghanistanBusinessClock.SystemToday;

    [Display(Name = "جهت")]
    public PaymentDirection Direction { get; set; } = PaymentDirection.In;

    [Display(Name = "نوع پرداخت / دریافت")]
    public PaymentKind PaymentKind { get; set; } = PaymentKind.CustomerReceipt;

    [Display(Name = "نوع طرف حساب")]
    public PaymentCounterpartyType CounterpartyType { get; set; } = PaymentCounterpartyType.Customer;

    // برای پرداختِ شرکت اجباری است و برای پرداختِ شریک اصلاً استفاده نمی‌شود؛ چون این شرط
    // به FundingSource وابسته است، اعتبارسنجی‌اش در ValidateAndResolveAsync انجام می‌شود نه با Range.
    [Display(Name = "حساب نقد / بانک")]
    public int? CashAccountId { get; set; }

    [Display(Name = "پرداخت توسط")]
    public PaymentFundingSource FundingSource { get; set; } = PaymentFundingSource.Company;

    [Display(Name = "شریک پرداخت‌کننده")]
    public int? PaidByPartnerId { get; set; }

    [Display(Name = "مشتری")]
    public int? CustomerId { get; set; }

    [Display(Name = "تأمین‌کننده")]
    public int? SupplierId { get; set; }

    [Display(Name = "شرکت خدماتی")]
    public int? ServiceProviderId { get; set; }

    [Display(Name = "صراف")]
    public int? SarrafId { get; set; }

    [Display(Name = "راننده")]
    public int? DriverId { get; set; }

    [Display(Name = "کارمند")]
    public int? EmployeeId { get; set; }

    [Display(Name = "قرارداد")]
    public int? ContractId { get; set; }

    [Display(Name = "Shipment")]
    public int? ShipmentId { get; set; }

    [Display(Name = "فروش")]
    public int? SalesTransactionId { get; set; }

    [Display(Name = "مصرف")]
    public int? ExpenseTransactionId { get; set; }

    [Display(Name = "Truck Dispatch")]
    public int? TruckDispatchId { get; set; }

    [Display(Name = "مبلغ")]
    [Range(typeof(decimal), "0.0001", "79228162514264337593543950335", ErrorMessage = "مبلغ باید بزرگ‌تر از صفر باشد.")]
    public decimal Amount { get; set; }

    [Display(Name = "ارز")]
    [Required(ErrorMessage = "ارز الزامی است.")]
    [StringLength(10)]
    public string Currency { get; set; } = "USD";

    [Display(Name = "نرخ تبدیل به USD")]
    [Range(typeof(decimal), "0.000001", "79228162514264337593543950335", ErrorMessage = "نرخ تبدیل باید بزرگ‌تر از صفر باشد.")]
    public decimal? AppliedFxRateToUsd { get; set; }

    // فیلد کمکی — کاربر نرخ را به‌صورت «ارز در برابر ۱ دالر» وارد می‌کند (مثلاً ۹۱.۳۳۹۸ برای روبل).
    // در NormalizeCreateModel تبدیل می‌شود: AppliedFxRateToUsd = 1 / DocumentCurrencyPerUsdRate
    [Display(Name = "نرخ ارز (تعداد ارز برای ۱ دالر)")]
    [Range(typeof(decimal), "0", "79228162514264337593543950335")]
    public decimal? DocumentCurrencyPerUsdRate { get; set; }

    [Display(Name = "مرجع / واوچر")]
    [StringLength(200)]
    public string? Reference { get; set; }

    [Display(Name = "شرح")]
    [StringLength(1000)]
    public string? Description { get; set; }

    // Phase 1 — نشانهٔ پیش‌پرداخت (فقط برای پرداخت تأمین‌کننده؛ ثبت/نمایش، بدون اثر مالی).
    [Display(Name = "پیش‌پرداخت است")]
    public bool? IsAdvancePayment { get; set; }

    // مرحله ۴ — نشانهٔ پیش‌دریافت (فقط برای دریافت از مشتری؛ ثبت/نمایش، بدون اثر روی Ledger).
    [Display(Name = "پیش‌دریافت است")]
    public bool? IsCustomerAdvance { get; set; }

    [StringLength(1000)]
    public string? ReturnUrl { get; set; }

    // ——— روش پرداخت ———
    // CashBank: فرم عادی روزنامچه. ViaSarraf: پرداخت از طریق صراف که پشت‌صحنه به‌صورت SarrafSettlement ثبت می‌شود.
    [Display(Name = "روش پرداخت")]
    public PaymentMethod PaymentMethod { get; set; } = PaymentMethod.CashBank;

    // فیلدهای «پرداخت از طریق صراف» (فقط وقتی PaymentMethod = ViaSarraf استفاده می‌شوند).
    // صراف، تأمین‌کننده، قرارداد، تاریخ، شماره حواله و توضیح از فیلدهای موجود همین فرم گرفته می‌شوند.
    [Display(Name = "مبلغ پرداخت‌شده به تأمین‌کننده")]
    public decimal? SarrafSupplierAmount { get; set; }

    [Display(Name = "ارز پرداخت")]
    [StringLength(10)]
    public string? SarrafSupplierCurrency { get; set; }

    // نرخ ساده «۱ دالر = چند واحد ارز» — کاربر نرخ معکوس نمی‌بیند؛ کنترلر خودش 1/نرخ را حساب می‌کند.
    [Display(Name = "نرخ حساب تأمین‌کننده")]
    public decimal? SarrafSupplierPerUsdRate { get; set; }

    [Display(Name = "نرخ حساب صراف با شرکت")]
    public decimal? SarrafCompanyPerUsdRate { get; set; }

    // ——— عمومی‌سازی صراف (Phase 1) ———
    // نوع طرف‌حساب و جهت. nullable تا فرم‌های قدیمی که فقط SupplierId می‌فرستند
    // به‌صورت پیش‌فرض «پرداخت به تأمین‌کننده / Out» تعبیر شوند (backward-compatible).
    [Display(Name = "صراف چه کرد؟")]
    public SarrafSettlementDirection? SarrafDirection { get; set; }

    [Display(Name = "طرف حساب صراف")]
    public SarrafSettlementCounterpartyType? SarrafCounterpartyType { get; set; }

    [Display(Name = "مشتری")]
    public int? SarrafCustomerId { get; set; }

    [Display(Name = "شرکت خدماتی")]
    public int? SarrafServiceProviderId { get; set; }

    [Display(Name = "راننده")]
    public int? SarrafDriverId { get; set; }

    [Display(Name = "کارمند")]
    public int? SarrafEmployeeId { get; set; }

    // ——— کمیسیون (اختیاری) ———
    // اعتبارسنجی سمت سرور به‌صورت شرطی انجام می‌شود (نه با DataAnnotations) تا وقتی کمیسیون
    // غیرفعال است، فیلدهای خالی باعث ModelState invalid نشوند.
    [Display(Name = "افزودن کمیسیون")]
    public bool CommissionEnabled { get; set; }

    [Display(Name = "نوع کمیسیون")]
    public PaymentCommissionType? CommissionType { get; set; }

    [Display(Name = "درصد کمیسیون")]
    public decimal? CommissionPercent { get; set; }

    [Display(Name = "مبلغ ثابت کمیسیون")]
    public decimal? CommissionFixedAmount { get; set; }

    [Display(Name = "ارز کمیسیون")]
    [StringLength(10)]
    public string? CommissionCurrency { get; set; }

    // نرخ ساده «۱ دالر = چند واحد ارز کمیسیون» (فقط برای مبلغ ثابت با ارز غیر‌دالری).
    [Display(Name = "نرخ دالر به ارز کمیسیون")]
    public decimal? CommissionPerUsdRate { get; set; }

    // حساب پرداخت کمیسیون (فقط نقد/بانک). پیش‌فرض همان حساب پرداخت اصلی.
    [Display(Name = "حساب پرداخت کمیسیون")]
    public int? CommissionCashAccountId { get; set; }

    [Display(Name = "توضیح کمیسیون")]
    [StringLength(1000)]
    public string? CommissionDescription { get; set; }
}

public sealed class PaymentIndexFilterViewModel
{
    [Display(Name = "از تاریخ")]
    [DataType(DataType.Date)]
    public DateTime? FromDate { get; set; }

    [Display(Name = "تا تاریخ")]
    [DataType(DataType.Date)]
    public DateTime? ToDate { get; set; }

    [Display(Name = "جهت")]
    public PaymentDirection? Direction { get; set; }

    [Display(Name = "نوع")]
    public PaymentKind? PaymentKind { get; set; }

    [Display(Name = "نوع طرف حساب")]
    public PaymentCounterpartyType? CounterpartyType { get; set; }

    [Display(Name = "حساب نقد / بانک")]
    public int? CashAccountId { get; set; }

    [Display(Name = "مشتری")]
    public int? CustomerId { get; set; }

    [Display(Name = "تأمین‌کننده")]
    public int? SupplierId { get; set; }

    [Display(Name = "شرکت خدماتی")]
    public int? ServiceProviderId { get; set; }

    [Display(Name = "کارمند")]
    public int? EmployeeId { get; set; }

    [Display(Name = "راننده")]
    public int? DriverId { get; set; }

    [Display(Name = "قرارداد")]
    public int? ContractId { get; set; }

    [Display(Name = "Shipment")]
    public int? ShipmentId { get; set; }

    [Display(Name = "فروش")]
    public int? SalesTransactionId { get; set; }

    [Display(Name = "مصرف")]
    public int? ExpenseTransactionId { get; set; }

    [Display(Name = "ارز")]
    [StringLength(10)]
    public string? Currency { get; set; }

    [Display(Name = "جستجو")]
    [StringLength(200)]
    public string? Search { get; set; }

    [Display(Name = "مرجع")]
    [StringLength(200)]
    public string? Reference { get; set; }

    [Display(Name = "صراف")]
    public int? SarrafId { get; set; }
}

public sealed class PaymentListItemViewModel
{
    public int Id { get; init; }
    public DateTime PaymentDate { get; init; }
    public PaymentDirection Direction { get; init; }
    public string DirectionName { get; init; } = string.Empty;
    public PaymentKind PaymentKind { get; init; }
    public string PaymentKindName { get; init; } = string.Empty;
    public string CashAccountName { get; init; } = string.Empty;
    public string CashAccountCurrency { get; init; } = "USD";
    public string CounterpartyTypeName { get; init; } = string.Empty;
    public string CounterpartyName { get; init; } = string.Empty;

    // فقط نمایشی — رزنامچه برای ستون «طرف حساب» آیکون متناسب با نوع طرف حساب نشان می‌دهد.
    public PaymentCounterpartyType CounterpartyType { get; init; }

    // فقط نمایشی — PaymentDate تاریخ بدون ساعت است، پس ستون «ساعت» از زمان ثبت رکورد پر می‌شود.
    public DateTime CreatedAtUtc { get; init; }
    public string? CustomerName { get; init; }
    public string? SupplierName { get; init; }
    public string? ServiceProviderName { get; init; }
    public string? SarrafName { get; init; }
    public string? EmployeeName { get; init; }
    public string? DriverName { get; init; }
    public string? ContractNumber { get; init; }
    public string? ShipmentCode { get; init; }
    public string? SalesInvoiceNumber { get; init; }
    public string? ExpenseDescription { get; init; }
    public string RelatedTo { get; init; } = string.Empty;
    public string? Description { get; init; }
    public string? CreatedByDisplay { get; init; }
    public decimal Amount { get; init; }
    public string Currency { get; init; } = "USD";
    public decimal AmountUsd { get; init; }
    public string? Reference { get; init; }
    public int? LedgerEntryId { get; init; }

    // منبع پول. فقط نمایشی در رزنامچه: پرداختِ شریک صندوق شرکت را تکان نداده است.
    public PaymentFundingSource FundingSource { get; init; } = PaymentFundingSource.Company;
    public string? PaidByPartnerName { get; init; }

    // کمیسیون مرتبط با این ردیف (اگر ثبت شده باشد). فقط نمایشی: «کمیسیون: 50 USD».
    public decimal? CommissionAmount { get; set; }
    public string? CommissionCurrency { get; set; }

    // ردیف‌های مجازیِ «حواله صراف به تأمین‌کننده» (SarrafSettlement) فقط برای نمایش در رزنامچه
    // ادغام می‌شوند؛ این‌ها PaymentTransaction نیستند و صندوق را تکان نداده‌اند. ویرایش ندارند و
    // جزئیات‌شان به صفحهٔ تسویهٔ صراف می‌رود.
    public bool IsSarrafHawala { get; init; }
    public int? SarrafSettlementId { get; init; }

    public bool IsLedgerOnlyViaSarraf { get; init; }
}

public sealed class PaymentIndexViewModel
{
    public PaymentIndexFilterViewModel Filter { get; init; } = new();
    public IReadOnlyList<PaymentListItemViewModel> Items { get; init; } = [];
    public int CurrentPage { get; init; } = 1;
    public int PageCount { get; init; } = 1;
    public int TotalCount { get; init; }
    public decimal TodayReceiptUsd { get; init; }
    public decimal TodayPaymentUsd { get; init; }
    public int TodayReceiptMissingUsdEquivalentCount { get; init; }
    public int TodayPaymentMissingUsdEquivalentCount { get; init; }
    public decimal TodayNetUsd => TodayReceiptUsd - TodayPaymentUsd;
    public decimal CashAccountsBalanceUsd { get; init; }
    public string? LastDocumentReference { get; init; }
    public DateTime? LastDocumentDate { get; init; }
    public IReadOnlyList<CashAccountBalanceSummaryViewModel> CashAccountBalances { get; init; } = [];
}

public sealed class CashAccountBalanceSummaryViewModel
{
    public int CashAccountId { get; init; }
    public string Code { get; init; } = "";
    public string Name { get; init; } = "";
    public string Currency { get; init; } = "USD";
    public decimal TotalIn { get; init; }
    public decimal TotalOut { get; init; }
    public decimal ClosingBalance => TotalIn - TotalOut;
}

public sealed class PaymentContractLookupItemViewModel
{
    public int Id { get; init; }
    public int? SupplierId { get; init; }
    public ContractType ContractType { get; init; }
    public string Display { get; init; } = "";
}

public sealed class PaymentDetailsViewModel
{
    public int Id { get; init; }
    public DateTime PaymentDate { get; init; }
    public PaymentDirection Direction { get; init; }
    public string DirectionName { get; init; } = string.Empty;
    public PaymentKind PaymentKind { get; init; }
    public string PaymentKindName { get; init; } = string.Empty;
    public string CashAccountCode { get; init; } = string.Empty;
    public string CashAccountName { get; init; } = string.Empty;
    public string CashAccountTypeName { get; init; } = string.Empty;
    public PaymentFundingSource FundingSource { get; init; } = PaymentFundingSource.Company;
    public string FundingSourceName { get; init; } = string.Empty;
    public int? PaidByPartnerId { get; init; }
    public string? PaidByPartnerName { get; init; }
    public string Currency { get; init; } = "USD";
    public decimal Amount { get; init; }
    public decimal AmountUsd { get; init; }
    public decimal? AppliedFxRateToUsd { get; init; }
    public int? CustomerId { get; init; }
    public string? CustomerName { get; init; }
    public int? SupplierId { get; init; }
    public string? SupplierName { get; init; }
    public int? ServiceProviderId { get; init; }
    public string? ServiceProviderName { get; init; }
    public int? SarrafId { get; init; }
    public string? SarrafName { get; init; }
    public int? DriverId { get; init; }
    public string? DriverName { get; init; }
    public int? EmployeeId { get; init; }
    public string? EmployeeName { get; init; }
    public int? ContractId { get; init; }
    public string? ContractNumber { get; init; }
    public int? ShipmentId { get; init; }
    public string? ShipmentCode { get; init; }
    public int? SalesTransactionId { get; init; }
    public string? SalesInvoiceNumber { get; init; }
    public int? ExpenseTransactionId { get; init; }
    public string? ExpenseDescription { get; init; }
    public int? TruckDispatchId { get; init; }
    public string? TruckDispatchLabel { get; init; }
    public string? Reference { get; init; }
    public string? Description { get; init; }
    public int? LedgerEntryId { get; init; }
    public string? LedgerSourceType { get; init; }
    public string? LedgerReference { get; init; }
    public string? LedgerSideName { get; init; }
    public DateTime CreatedAtUtc { get; init; }
    public int? CreatedByUserId { get; init; }
    public DateTime? UpdatedAtUtc { get; init; }
    public int? UpdatedByUserId { get; init; }

    // کمیسیون مرتبط با این پرداخت/دریافت (اگر ثبت شده باشد). فقط نمایشی.
    public int? CommissionExpenseTransactionId { get; init; }
    public decimal? CommissionAmount { get; init; }
    public string? CommissionCurrency { get; init; }
    public decimal? CommissionAmountUsd { get; init; }

    // پیش‌پرداخت و تخصیص به قرارداد — فقط برای «پرداخت به تأمین‌کننده».
    public bool SupportsAdvanceAllocation { get; init; }
    public decimal AllocatedBookAmountUsd { get; init; }
    public decimal AllocatableBalanceUsd { get; init; }
    // مانده و مصرف به ارز اصلی پرداخت (مثلاً RUB) — سقف واقعی تخصیص همین است.
    public decimal AllocatedPaymentAmountTotal { get; init; }
    public decimal AllocatableBalanceAmount { get; init; }
    // جمع سود/زیان تسعیر تخصیص‌های فعال (مثبت = سود، منفی = زیان).
    public decimal AllocationExchangeDifferenceUsd { get; init; }
    public int ActiveAllocationCount { get; init; }
    public IReadOnlyList<SupplierPaymentAllocationListItemViewModel> Allocations { get; init; } = [];
}

// مرکز رزنامچه و تسویه‌ها — فقط خلاصه و navigation (read-only). هیچ posting یا فرمی ندارد.
public sealed class TreasuryHubViewModel
{
    public decimal TodayReceiptUsd { get; init; }
    public decimal TodayPaymentUsd { get; init; }
    public int SuspenseCount { get; init; }
    public int PostedHawalaCount { get; init; }
    public int NeedsReviewCount { get; init; }
}
