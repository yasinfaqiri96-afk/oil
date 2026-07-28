using PTGOilSystem.Web.Models.Entities;

namespace PTGOilSystem.Web.Services.CompanyFlow;

/// <summary>
/// ورودیِ تعیین جهت. هرچه سیستم دربارهٔ یک رویداد مالی می‌داند اینجا جمع می‌شود تا
/// جهت از «مفهوم واقعی رویداد» تعیین شود، نه از یک تبدیل کورکورانهٔ Debit/Credit.
/// </summary>
public readonly record struct CompanyFlowEvent
{
    /// <summary>SourceType سند (Loading، Sale، Expense، SupplierPayment، ...). مبنای اصلی تصمیم.</summary>
    public string SourceType { get; init; }

    /// <summary>سمت حسابداری سند اگر وجود داشته باشد. فقط به‌عنوان fallback استفاده می‌شود.</summary>
    public LedgerSide? LedgerSide { get; init; }

    /// <summary>نقش طرف‌حسابی که سند روی صورت‌حساب او نمایش داده می‌شود.</summary>
    public CompanyFlowPartyRole PartyRole { get; init; }

    /// <summary>وضعیت چرخهٔ عمر سند (اصلی / معکوس / لغوشده).</summary>
    public CompanyFlowLifecycle Lifecycle { get; init; }

    /// <summary>
    /// جهت حرکت واقعی پول برای اسناد نقدی (PaymentTransaction). وقتی مقدار دارد،
    /// بر LedgerSide اولویت دارد چون مستقیماً می‌گوید پول از شرکت خارج شده یا وارد.
    /// </summary>
    public PaymentDirection? PaymentDirection { get; init; }

    public CompanyFlowEvent(
        string sourceType,
        LedgerSide? ledgerSide = null,
        CompanyFlowPartyRole partyRole = CompanyFlowPartyRole.Unknown,
        CompanyFlowLifecycle lifecycle = CompanyFlowLifecycle.Original,
        PaymentDirection? paymentDirection = null)
    {
        SourceType = sourceType ?? string.Empty;
        LedgerSide = ledgerSide;
        PartyRole = partyRole;
        Lifecycle = lifecycle;
        PaymentDirection = paymentDirection;
    }
}
