using System.ComponentModel.DataAnnotations;

namespace PTGOilSystem.Web.Models.Entities;

public enum PartySettlementStatus
{
    Posted = 1,
    Cancelled = 2
}

/// <summary>
/// تسویه بین طرف‌حساب‌ها: یک طرف‌حساب (بدهکار شرکت) مستقیماً به طرف‌حساب دیگر (طلبکار شرکت)
/// پول می‌دهد و پول از صندوق/بانک شرکت نمی‌گذرد.
///
/// اثر: دو سطر LedgerEntry — «رسید» از پرداخت‌کننده و «برد» به گیرنده — با همان قاعدهٔ
/// <c>CompanyFlowDirectionResolver</c> و تسویه سه‌طرفه. هیچ PaymentTransaction، حساب نقد/بانک
/// یا JournalEntry ساخته نمی‌شود. لغو با سطرهای معکوس انجام می‌شود و اصل سند دست‌نخورده می‌ماند.
///
/// فاز اول فقط طرف‌حساب‌هایی که مانده‌شان از دفتر خوانده می‌شود: مشتری، تأمین‌کننده،
/// شرکت خدماتی، راننده. طرف‌حساب چندریختی است (نوع + شناسه) و FK ندارد، مثل JournalEntryLine.
/// </summary>
public class PartySettlement : BaseEntity
{
    public DateTime SettlementDate { get; set; }
    public PartySettlementStatus Status { get; set; } = PartySettlementStatus.Posted;

    /// <summary>طرف‌حسابی که پول را پرداخت کرد.</summary>
    public AccountingPartyType FromPartyType { get; set; }
    public int FromPartyId { get; set; }

    /// <summary>طرف‌حسابی که پول را گرفت.</summary>
    public AccountingPartyType ToPartyType { get; set; }
    public int ToPartyId { get; set; }

    public decimal Amount { get; set; }
    [Required, MaxLength(10)] public string Currency { get; set; } = "USD";
    /// <summary>۱ دالر = چند واحد ارز (همان نرخی که کاربر وارد کرده؛ برای USD خالی).</summary>
    public decimal? CurrencyPerUsdRate { get; set; }
    public decimal FxRateToUsd { get; set; } = 1m;
    public decimal AmountUsd { get; set; }

    [MaxLength(1000)] public string? Description { get; set; }

    public int? FromLedgerEntryId { get; set; }
    public LedgerEntry? FromLedgerEntry { get; set; }
    public int? ToLedgerEntryId { get; set; }
    public LedgerEntry? ToLedgerEntry { get; set; }

    [MaxLength(200)] public string? CreatedByUserName { get; set; }
    public DateTime? CancelledAtUtc { get; set; }
    [MaxLength(200)] public string? CancelledByUserName { get; set; }
    [MaxLength(1000)] public string? CancellationReason { get; set; }
}
