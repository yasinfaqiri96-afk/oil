using System.ComponentModel.DataAnnotations;

namespace PTGOilSystem.Web.Models.Payments;

/// <summary>فرم «ثبت / تغییر قرارداد» برای یک گروهِ پرداخت از طریق صراف (جفت LedgerEntry).</summary>
public sealed class ViaSarrafAssignContractViewModel
{
    public Guid GroupId { get; set; }
    public int PaymentLedgerEntryId { get; set; }

    public string SupplierName { get; set; } = string.Empty;
    public string SarrafName { get; set; } = string.Empty;
    public DateTime EntryDate { get; set; }
    public decimal SourceAmount { get; set; }
    public string SourceCurrencyCode { get; set; } = "USD";
    public decimal AmountUsd { get; set; }

    public int? CurrentContractId { get; set; }
    public string? CurrentContractNumber { get; set; }

    [Display(Name = "قرارداد خرید")]
    public int? NewContractId { get; set; }

    [Display(Name = "دلیل اصلاح")]
    [Required(ErrorMessage = "دلیل اصلاح الزامی است.")]
    [MaxLength(500)]
    public string Reason { get; set; } = string.Empty;

    public string? ReturnUrl { get; set; }

    public bool HasContract => CurrentContractId.HasValue;
}

/// <summary>فرمِ تأیید دستیِ جفتِ یک ردیفِ پرداختِ صرافِ گروه‌بندی‌نشده با یک ردیفِ بدهی صراف.</summary>
public sealed class ViaSarrafConfirmPairViewModel
{
    public int PaymentLedgerEntryId { get; set; }
    public string SupplierName { get; set; } = string.Empty;
    public string SarrafName { get; set; } = string.Empty;
    public DateTime EntryDate { get; set; }
    public decimal SourceAmount { get; set; }
    public string SourceCurrencyCode { get; set; } = "USD";
    public decimal AmountUsd { get; set; }

    public List<ViaSarrafPayableCandidateRow> Candidates { get; set; } = [];

    [Display(Name = "ردیف بدهی صراف")]
    public int? PayableLedgerEntryId { get; set; }

    [Display(Name = "دلیل تأیید دستی")]
    [Required(ErrorMessage = "دلیل تأیید دستی الزامی است.")]
    [MaxLength(500)]
    public string Reason { get; set; } = string.Empty;

    public string? ReturnUrl { get; set; }
}

public sealed record ViaSarrafPayableCandidateRow(
    int LedgerEntryId,
    DateTime EntryDate,
    decimal SourceAmount,
    string SourceCurrencyCode,
    decimal AmountUsd,
    string? Reference,
    int? ContractId,
    string? ContractNumber);

/// <summary>گزارشِ Dry Run گروه‌بندیِ رکوردهای Legacy.</summary>
public sealed class ViaSarrafLegacyReviewViewModel
{
    public int TotalUngroupedRows { get; set; }
    public int UngroupedPaymentRows { get; set; }
    public int UngroupedPayableRows { get; set; }
    public int DeterministicPairCount { get; set; }
    public int AmbiguousCount { get; set; }
    public int UnpairedCount { get; set; }
    public List<ViaSarrafLegacyReviewRow> Ambiguous { get; set; } = [];
    public List<ViaSarrafLegacyReviewRow> Unpaired { get; set; } = [];
}

public sealed record ViaSarrafLegacyReviewRow(
    int LedgerEntryId,
    string SourceType,
    int SarrafId,
    DateTime EntryDate,
    decimal SourceAmount,
    string SourceCurrencyCode,
    string Reason);
