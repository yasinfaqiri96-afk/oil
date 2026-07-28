using System.ComponentModel.DataAnnotations;
using PTGOilSystem.Web.Models.Entities;

namespace PTGOilSystem.Web.Models.AccountStatements;

public enum AccountStatementEntryKind
{
    OpeningBalance = 1,
    ManualAdjustment = 2
}

public sealed class AccountStatementFilterViewModel
{
    [Display(Name = "از تاریخ")]
    [DataType(DataType.Date)]
    public DateTime? FromDate { get; set; }

    [Display(Name = "تا تاریخ")]
    [DataType(DataType.Date)]
    public DateTime? ToDate { get; set; }

    [Display(Name = "قرارداد")]
    public int? ContractId { get; set; }

    [Display(Name = "مشتری")]
    public int? CustomerId { get; set; }

    [Display(Name = "تأمین‌کننده")]
    public int? SupplierId { get; set; }

    [Display(Name = "ارز منبع")]
    [StringLength(10)]
    public string? SourceCurrencyCode { get; set; }

    [Display(Name = "مرجع")]
    [StringLength(200)]
    public string? Reference { get; set; }
}

public sealed class AccountStatementListItemViewModel
{
    public int Id { get; init; }
    public DateTime EntryDate { get; init; }
    public LedgerSide Side { get; init; }
    public string SideName { get; init; } = string.Empty;
    public decimal SourceAmount { get; init; }
    public string SourceCurrencyCode { get; init; } = string.Empty;
    public decimal AppliedFxRateToUsd { get; init; }
    public DateTime? AppliedFxRateDate { get; init; }
    public decimal AmountUsd { get; init; }
    public decimal RunningBalanceUsd { get; init; }
    public string SourceType { get; init; } = string.Empty;
    public int SourceId { get; init; }
    public string? Reference { get; init; }
    public string Description { get; init; } = string.Empty;
    public string? ContractNumber { get; init; }
    public string? CustomerName { get; init; }
    public string? SupplierName { get; init; }
}

public sealed class AccountStatementIndexViewModel
{
    public AccountStatementFilterViewModel Filter { get; init; } = new();
    public decimal OpeningBalanceUsd { get; init; }
    public decimal ClosingBalanceUsd { get; init; }
    public IReadOnlyList<AccountStatementListItemViewModel> Items { get; init; } = [];
    public int CurrentPage { get; init; } = 1;
    public int PageCount { get; init; } = 1;
    public int TotalCount { get; init; }
}

public sealed class AccountStatementCreateViewModel
{
    [Display(Name = "نوع ثبت")]
    [Required(ErrorMessage = "نوع ثبت الزامی است.")]
    public AccountStatementEntryKind EntryKind { get; set; } = AccountStatementEntryKind.ManualAdjustment;

    [Display(Name = "تاریخ")]
    [DataType(DataType.Date)]
    [Required(ErrorMessage = "تاریخ الزامی است.")]
    public DateTime EntryDate { get; set; } = DateTime.UtcNow.Date;

    [Display(Name = "سمت")]
    [Required(ErrorMessage = "سمت الزامی است.")]
    public LedgerSide Side { get; set; } = LedgerSide.Credit;

    [Display(Name = "مبلغ ارز منبع")]
    [Range(typeof(decimal), "0.0001", "999999999999", ErrorMessage = "مبلغ باید بزرگ‌تر از صفر باشد.")]
    public decimal SourceAmount { get; set; }

    [Display(Name = "ارز منبع")]
    [Required(ErrorMessage = "ارز منبع الزامی است.")]
    [StringLength(10)]
    public string SourceCurrencyCode { get; set; } = "USD";

    [Display(Name = "نرخ تبدیل به USD")]
    [Range(typeof(decimal), "0.000001", "79228162514264337593543950335", ErrorMessage = "نرخ تبدیل باید بزرگ‌تر از صفر باشد.")]
    public decimal? AppliedFxRateToUsd { get; set; }

    [Display(Name = "قرارداد")]
    public int? ContractId { get; set; }

    [Display(Name = "مشتری")]
    public int? CustomerId { get; set; }

    [Display(Name = "تأمین‌کننده")]
    public int? SupplierId { get; set; }

    [Display(Name = "مرجع")]
    [Required(ErrorMessage = "مرجع برای trace الزامی است.")]
    [StringLength(200)]
    public string Reference { get; set; } = string.Empty;

    [Display(Name = "شرح")]
    [Required(ErrorMessage = "شرح الزامی است.")]
    [StringLength(500)]
    public string Description { get; set; } = string.Empty;
}

public sealed class AccountStatementDetailsViewModel
{
    public int Id { get; init; }
    public DateTime EntryDate { get; init; }
    public string SideName { get; init; } = string.Empty;
    public decimal SourceAmount { get; init; }
    public string SourceCurrencyCode { get; init; } = string.Empty;
    public decimal AppliedFxRateToUsd { get; init; }
    public DateTime? AppliedFxRateDate { get; init; }
    public string? AppliedFxRateSource { get; init; }
    public decimal AmountUsd { get; init; }
    public decimal RunningBalanceUsd { get; init; }
    public string SourceType { get; init; } = string.Empty;
    public int SourceId { get; init; }
    public string? Reference { get; init; }
    public string Description { get; init; } = string.Empty;
    public string? ContractNumber { get; init; }
    public string? CustomerName { get; init; }
    public string? SupplierName { get; init; }
}

public sealed class ContractAccountStatementViewModel
{
    public int ContractId { get; init; }
    public string ContractNumber { get; init; } = string.Empty;
    public string ProductName { get; init; } = string.Empty;
    public string ContractType { get; init; } = string.Empty;
    public string CounterpartyName { get; init; } = string.Empty;
    public string ContractCurrency { get; init; } = string.Empty;
    public decimal QuantityMt { get; init; }
    public ContractAccountStatementTotalsViewModel Totals { get; init; } = new();
    public IReadOnlyList<ContractAccountStatementRowViewModel> Rows { get; init; } = [];
}

public sealed class ContractAccountStatementTotalsViewModel
{
    /// <summary>مجموع رسید — ارزشی که شرکت روی این قرارداد دریافت کرده است.</summary>
    public decimal TotalReceiptUsd { get; init; }
    /// <summary>مجموع برد — ارزشی که شرکت روی این قرارداد داده است.</summary>
    public decimal TotalOutflowUsd { get; init; }
    public decimal BalanceUsd { get; init; }
    public IReadOnlyList<ContractAccountCurrencyBalanceViewModel> BalancesByCurrency { get; init; } = [];
}

public sealed class ContractAccountCurrencyBalanceViewModel
{
    public string Currency { get; init; } = string.Empty;
    public decimal BalanceOriginal { get; init; }
}

public sealed class ContractAccountStatementRowViewModel
{
    public DateTime Date { get; init; }
    public string SourceType { get; init; } = string.Empty;
    public int SourceId { get; init; }
    public string? Reference { get; init; }
    public string Description { get; init; } = string.Empty;
    public decimal? QuantityMt { get; init; }
    public decimal? UnitPrice { get; init; }
    public string? SourceCurrency { get; init; }
    public decimal? ReceiptOriginal { get; init; }
    public decimal? OutflowOriginal { get; init; }
    public string? BalanceOriginalByCurrency { get; init; }
    public decimal? FxRateToUsd { get; init; }
    public decimal? ReceiptUsd { get; init; }
    public decimal? OutflowUsd { get; init; }
    public decimal BalanceUsd { get; init; }
    public string? RelatedContractNumber { get; init; }
    public string? Notes { get; init; }
    public string? WarningBadge { get; init; }
    public bool IsFinancial { get; init; }
    public bool IsOperationalOnly { get; init; }
    /// <summary>سطر برگشت/لغو — برای Audit نمایش داده می‌شود و برچسب واضح دارد.</summary>
    public bool IsReversalRow { get; init; }
}
