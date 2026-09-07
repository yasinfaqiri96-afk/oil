using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.PartyStatements;

namespace PTGOilSystem.Web.Services.Parties;

/// <summary>
/// تنها مالکِ رابطهٔ میان دو شمارشِ طرف‌حساب سیستم.
///
/// <see cref="AccountingPartyType"/> قرارداد دفتر کل است (روی JournalEntryLine، AssetCharge،
/// AssetAssignment) و <see cref="PartyStatementPartyType"/> قرارداد لایهٔ صورت‌حساب و مانده.
/// هر دو یک مجموعهٔ هشت‌تایی از طرف‌حساب‌ها را نام می‌برند، ولی <b>مقادیر عددی‌شان یکی نیست</b>:
///
///   Driver   = ۵ در AccountingPartyType   ولی ۷ در PartyStatementPartyType
///   Employee = ۶ در AccountingPartyType   ولی ۵ در PartyStatementPartyType
///   Partner  = ۷ در AccountingPartyType   ولی ۶ در PartyStatementPartyType
///
/// پس هر تبدیلِ عددی (cast) بدهیِ راننده را به حساب کارمند یا شریک می‌برد. برای اینکه چنین
/// تبدیلی هرگز جایی دستی نوشته نشود، تبدیل فقط از همین‌جا می‌گذرد و برای مقدار ناشناخته
/// استثنا پرتاب می‌کند — نه بازگرداندنِ خاموشِ یک مقدار پیش‌فرض.
/// </summary>
public static class PartyTypeMap
{
    public static PartyStatementPartyType ToStatement(AccountingPartyType partyType) => partyType switch
    {
        AccountingPartyType.Customer => PartyStatementPartyType.Customer,
        AccountingPartyType.Supplier => PartyStatementPartyType.Supplier,
        AccountingPartyType.ServiceProvider => PartyStatementPartyType.ServiceProvider,
        AccountingPartyType.Sarraf => PartyStatementPartyType.Sarraf,
        AccountingPartyType.Driver => PartyStatementPartyType.Driver,
        AccountingPartyType.Employee => PartyStatementPartyType.Employee,
        AccountingPartyType.Partner => PartyStatementPartyType.Partner,
        AccountingPartyType.Company => PartyStatementPartyType.Company,
        _ => throw new ArgumentOutOfRangeException(
            nameof(partyType),
            partyType,
            "Unknown accounting party type; add it to PartyTypeMap instead of casting.")
    };

    public static AccountingPartyType ToAccounting(PartyStatementPartyType partyType) => partyType switch
    {
        PartyStatementPartyType.Customer => AccountingPartyType.Customer,
        PartyStatementPartyType.Supplier => AccountingPartyType.Supplier,
        PartyStatementPartyType.ServiceProvider => AccountingPartyType.ServiceProvider,
        PartyStatementPartyType.Sarraf => AccountingPartyType.Sarraf,
        PartyStatementPartyType.Driver => AccountingPartyType.Driver,
        PartyStatementPartyType.Employee => AccountingPartyType.Employee,
        PartyStatementPartyType.Partner => AccountingPartyType.Partner,
        PartyStatementPartyType.Company => AccountingPartyType.Company,
        _ => throw new ArgumentOutOfRangeException(
            nameof(partyType),
            partyType,
            "Unknown statement party type; add it to PartyTypeMap instead of casting.")
    };

    public static PartyStatementPartyType? ToStatementOrNull(AccountingPartyType? partyType)
        => partyType.HasValue ? ToStatement(partyType.Value) : null;

    public static AccountingPartyType? ToAccountingOrNull(PartyStatementPartyType? partyType)
        => partyType.HasValue ? ToAccounting(partyType.Value) : null;
}
