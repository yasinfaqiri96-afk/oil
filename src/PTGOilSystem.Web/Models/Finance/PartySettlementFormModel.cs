using PTGOilSystem.Web.Models.Entities;

namespace PTGOilSystem.Web.Models.Finance;

/// <summary>
/// فورم «تسویه بین طرف‌حساب‌ها». طرف‌حساب به شکل یک کلید متنی «نوع:شناسه» می‌آید
/// (مثلاً «1:5» = مشتری ۵) تا کاربر فقط یک لیست برای هر طرف ببیند.
/// </summary>
public sealed class PartySettlementFormModel
{
    public DateTime SettlementDate { get; set; }
    public string? FromParty { get; set; }
    public string? ToParty { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "USD";
    /// <summary>۱ دالر = چند واحد ارز. فقط برای ارز غیر دالری لازم است.</summary>
    public decimal? CurrencyPerUsdRate { get; set; }
    public string? Description { get; set; }
}

public sealed record PartySettlementListRow(
    int Id,
    DateTime SettlementDate,
    string FromName,
    string FromTypeLabel,
    string ToName,
    string ToTypeLabel,
    decimal Amount,
    string Currency,
    PartySettlementStatus Status);

/// <summary>نوع‌های پشتیبانی‌شده و کلید «نوع:شناسه» — فقط طرف‌حساب‌های دفتری.</summary>
public static class PartySettlementParties
{
    public static readonly AccountingPartyType[] Supported =
    [
        AccountingPartyType.Customer,
        AccountingPartyType.Supplier,
        AccountingPartyType.ServiceProvider,
        AccountingPartyType.Driver
    ];

    public static string Key(AccountingPartyType type, int id) => $"{(int)type}:{id}";

    public static bool TryParse(string? key, out AccountingPartyType type, out int id)
    {
        type = default;
        id = 0;
        if (string.IsNullOrWhiteSpace(key)) return false;
        var parts = key.Split(':');
        if (parts.Length != 2
            || !int.TryParse(parts[0], out var rawType)
            || !int.TryParse(parts[1], out id)
            || id <= 0)
        {
            return false;
        }
        type = (AccountingPartyType)rawType;
        return Supported.Contains(type);
    }

    public static string TypeLabel(AccountingPartyType type) => type switch
    {
        AccountingPartyType.Customer => "مشتری",
        AccountingPartyType.Supplier => "تأمین‌کننده",
        AccountingPartyType.ServiceProvider => "شرکت خدماتی",
        AccountingPartyType.Driver => "راننده",
        _ => type.ToString()
    };

    public static string GroupLabel(AccountingPartyType type) => type switch
    {
        AccountingPartyType.Customer => "مشتریان",
        AccountingPartyType.Supplier => "تأمین‌کنندگان",
        AccountingPartyType.ServiceProvider => "شرکت‌های خدماتی",
        AccountingPartyType.Driver => "رانندگان",
        _ => type.ToString()
    };
}
