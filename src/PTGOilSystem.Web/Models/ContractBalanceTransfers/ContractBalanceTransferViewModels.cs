using System.ComponentModel.DataAnnotations;
using PTGOilSystem.Web.Services.Time;

namespace PTGOilSystem.Web.Models.ContractBalanceTransfers;

public sealed class ContractBalanceTransferCreateViewModel
{
    [Display(Name = "تاریخ انتقال")]
    [DataType(DataType.Date)]
    [Required(ErrorMessage = "تاریخ انتقال الزامی است.")]
    public DateTime TransferDate { get; set; } = AfghanistanBusinessClock.SystemToday;

    [Display(Name = "از قرارداد")]
    [Required(ErrorMessage = "قرارداد مبدا الزامی است.")]
    public int FromContractId { get; set; }

    [Display(Name = "به قرارداد")]
    [Required(ErrorMessage = "قرارداد مقصد الزامی است.")]
    public int ToContractId { get; set; }

    [Display(Name = "مبلغ اصلی")]
    [Range(typeof(decimal), "0.0001", "999999999999", ErrorMessage = "مبلغ اصلی باید بزرگ‌تر از صفر باشد.")]
    public decimal AmountOriginal { get; set; }

    [Display(Name = "ارز")]
    [Required(ErrorMessage = "ارز الزامی است.")]
    [StringLength(10)]
    public string CurrencyCode { get; set; } = "USD";

    [Display(Name = "نرخ تبدیل به USD")]
    [Range(typeof(decimal), "0.000001", "999999999999", ErrorMessage = "نرخ تبدیل باید بزرگ‌تر از صفر باشد.")]
    public decimal FxRateToUsd { get; set; } = 1m;

    [Display(Name = "نرخ سند (ارز در برابر ۱ USD)")]
    [Range(typeof(decimal), "0.000001", "999999999999", ErrorMessage = "نرخ سند باید بزرگ‌تر از صفر باشد.")]
    public decimal? DocumentCurrencyPerUsdRate { get; set; }

    [Display(Name = "تاریخ نرخ")]
    [DataType(DataType.Date)]
    public DateTime? FxRateDate { get; set; }

    [Display(Name = "منبع نرخ")]
    [StringLength(500)]
    public string? FxRateSource { get; set; }

    [Display(Name = "پرداخت اولیه")]
    public int? OriginalPaymentTransactionId { get; set; }

    [Display(Name = "نرخ پرداخت اولیه")]
    [Range(typeof(decimal), "0.000001", "999999999999", ErrorMessage = "نرخ پرداخت اولیه باید بزرگ‌تر از صفر باشد.")]
    public decimal? OriginalPaymentFxRateToUsd { get; set; }

    [Display(Name = "مرجع")]
    [StringLength(200)]
    public string? Reference { get; set; }

    [Display(Name = "یادداشت")]
    [StringLength(1000)]
    public string? Notes { get; set; }

    public string? ReturnUrl { get; set; }
}

public sealed class ContractBalanceTransferListItemViewModel
{
    public int Id { get; init; }
    public DateTime TransferDate { get; init; }
    public string FromContractNumber { get; init; } = string.Empty;
    public string ToContractNumber { get; init; } = string.Empty;
    public decimal AmountOriginal { get; init; }
    public string CurrencyCode { get; init; } = string.Empty;
    public decimal FxRateToUsd { get; init; }
    public decimal AmountUsd { get; init; }
    public string? Reference { get; init; }
    public bool IsCancelled { get; init; }
}

public sealed class ContractBalanceTransferIndexViewModel
{
    public int? ContractId { get; init; }
    public string? ContractNumber { get; init; }
    public IReadOnlyList<ContractBalanceTransferListItemViewModel> Items { get; init; } = [];
    public int CurrentPage { get; init; } = 1;
    public int PageCount { get; init; } = 1;
    public int TotalCount { get; init; }
}

public sealed class ContractBalanceTransferDetailsViewModel
{
    public int Id { get; init; }
    public DateTime TransferDate { get; init; }
    public int FromContractId { get; init; }
    public string FromContractNumber { get; init; } = string.Empty;
    public int ToContractId { get; init; }
    public string ToContractNumber { get; init; } = string.Empty;
    public decimal AmountOriginal { get; init; }
    public string CurrencyCode { get; init; } = string.Empty;
    public decimal FxRateToUsd { get; init; }
    public decimal AmountUsd { get; init; }
    public DateTime? FxRateDate { get; init; }
    public string? FxRateSource { get; init; }
    public int? OriginalPaymentTransactionId { get; init; }
    public decimal? OriginalPaymentFxRateToUsd { get; init; }
    public string? Reference { get; init; }
    public string? Notes { get; init; }
    public bool IsCancelled { get; init; }
    public IReadOnlyList<ContractBalanceTransferLedgerEntryViewModel> LedgerEntries { get; init; } = [];
}

public sealed class ContractBalanceTransferLedgerEntryViewModel
{
    public int Id { get; init; }
    public DateTime EntryDate { get; init; }
    public string Side { get; init; } = string.Empty;
    public string ContractNumber { get; init; } = string.Empty;
    public decimal AmountUsd { get; init; }
    public string? SourceCurrencyCode { get; init; }
    public decimal? SourceAmount { get; init; }
}
