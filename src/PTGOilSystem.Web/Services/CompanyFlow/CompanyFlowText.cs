using Microsoft.AspNetCore.Http;
using PTGOilSystem.Web.Helpers;

namespace PTGOilSystem.Web.Services.CompanyFlow;

/// <summary>
/// کلیدهای مرکزی واژگان «رسید، برد و بیلانس». هر کلید دقیقاً یک متن فارسی و یک متن
/// انگلیسی دارد. نگاشت قطعی و غیرقابل‌تغییر: رسید = Debit، برد = Credit، بیلانس = Balance.
/// </summary>
public enum CompanyFlowTextKey
{
    Receipt,
    Outflow,
    Balance,
    TotalReceipt,
    TotalOutflow,
    OpeningBalance,
    RunningBalance,
    ClosingBalance,
    Reversal,
    ReversedRow,
    PeriodTotal,
    PositiveBalance,
    NegativeBalance,
    SettledBalance,
    CashPositiveBalance,
    CashNegativeBalance,
    CashEmptyBalance
}

/// <summary>
/// تنها منبع واژگان کاربریِ «رسید، برد و بیلانس». هیچ صفحه‌ای — View، Controller، PDF یا
/// Excel — اجازه ندارد این عنوان‌ها را مستقیم بنویسد؛ همه از اینجا می‌آیند تا در کل سیستم
/// و در هر دو زبان یکسان بمانند.
///
/// این لایه فقط «متن» است. جهت فعالیت، مبلغ، فرمول، علامت بیلانس، ترتیب ستون‌ها،
/// <see cref="CompanyFlowDirection"/> و ساختار حسابداری (LedgerSide، JournalEntryLine)
/// هرگز با تغییر زبان عوض نمی‌شوند.
/// </summary>
public static class CompanyFlowText
{
    /// <summary>پیشوند کلیدهای مرکزی — مثلاً «CompanyFlow.Receipt».</summary>
    public const string KeyPrefix = "CompanyFlow.";

    private static readonly IReadOnlyDictionary<CompanyFlowTextKey, (string Fa, string En)> Catalog =
        new Dictionary<CompanyFlowTextKey, (string Fa, string En)>
        {
            [CompanyFlowTextKey.Receipt] = ("رسید", "Debit"),
            [CompanyFlowTextKey.Outflow] = ("برد", "Credit"),
            [CompanyFlowTextKey.Balance] = ("بیلانس", "Balance"),
            [CompanyFlowTextKey.TotalReceipt] = ("مجموع رسید", "Total Debit"),
            [CompanyFlowTextKey.TotalOutflow] = ("مجموع برد", "Total Credit"),
            [CompanyFlowTextKey.OpeningBalance] = ("بیلانس اول دوره", "Opening Balance"),
            [CompanyFlowTextKey.RunningBalance] = ("بیلانس جاری", "Running Balance"),
            [CompanyFlowTextKey.ClosingBalance] = ("بیلانس نهایی", "Closing Balance"),
            [CompanyFlowTextKey.Reversal] = ("سند برگشت", "Reversal Entry"),
            [CompanyFlowTextKey.ReversedRow] = ("لغو شده", "Cancelled"),
            [CompanyFlowTextKey.PeriodTotal] = ("مجموع دوره", "Period Total"),
            [CompanyFlowTextKey.PositiveBalance] =
                ("شرکت بیشتر داده است و از طرف مقابل طلب یا پیش‌پرداخت دارد",
                 "The company has given more and holds a receivable or prepayment"),
            [CompanyFlowTextKey.NegativeBalance] =
                ("شرکت بیشتر دریافت کرده و به طرف مقابل بدهکار است",
                 "The company has received more and owes the counterparty"),
            [CompanyFlowTextKey.SettledBalance] = ("حساب تسویه است", "Account is settled"),
            [CompanyFlowTextKey.CashPositiveBalance] = ("موجودی مثبت حساب", "Positive account balance"),
            [CompanyFlowTextKey.CashNegativeBalance] = ("کسری حساب نقدی", "Cash account shortfall"),
            [CompanyFlowTextKey.CashEmptyBalance] = ("حساب خالی است", "Account is empty")
        };

    /// <summary>همهٔ کلیدهای مرکزی — برای تست پوشش و بازبینی.</summary>
    public static IReadOnlyCollection<CompanyFlowTextKey> Keys => Catalog.Keys.ToArray();

    /// <summary>نام کامل کلید مرکزی، مثلاً «CompanyFlow.TotalReceipt».</summary>
    public static string KeyName(CompanyFlowTextKey key) => KeyPrefix + key;

    /// <summary>متن کلید در زبان انتخاب‌شده.</summary>
    public static string Get(CompanyFlowTextKey key, bool isEnglish)
    {
        var entry = Catalog[key];
        return isEnglish ? entry.En : entry.Fa;
    }

    /// <summary>
    /// متن کلید بر اساس زبان جاری درخواست. نبودِ درخواست یعنی زبان پیش‌فرض سیستم (فارسی)؛
    /// واژگان مالی نباید به Culture محیط اجرا وابسته باشد.
    /// </summary>
    public static string Get(CompanyFlowTextKey key, HttpContext? context)
        => Get(key, context is not null && UiText.IsEn(context));

    public static string Receipt(HttpContext? context = null) => Get(CompanyFlowTextKey.Receipt, context);
    public static string Outflow(HttpContext? context = null) => Get(CompanyFlowTextKey.Outflow, context);
    public static string Balance(HttpContext? context = null) => Get(CompanyFlowTextKey.Balance, context);
    public static string TotalReceipt(HttpContext? context = null) => Get(CompanyFlowTextKey.TotalReceipt, context);
    public static string TotalOutflow(HttpContext? context = null) => Get(CompanyFlowTextKey.TotalOutflow, context);
    public static string OpeningBalance(HttpContext? context = null) => Get(CompanyFlowTextKey.OpeningBalance, context);
    public static string RunningBalance(HttpContext? context = null) => Get(CompanyFlowTextKey.RunningBalance, context);
    public static string ClosingBalance(HttpContext? context = null) => Get(CompanyFlowTextKey.ClosingBalance, context);
    public static string Reversal(HttpContext? context = null) => Get(CompanyFlowTextKey.Reversal, context);
    public static string ReversedRow(HttpContext? context = null) => Get(CompanyFlowTextKey.ReversedRow, context);
    public static string PeriodTotal(HttpContext? context = null) => Get(CompanyFlowTextKey.PeriodTotal, context);

    /// <summary>عنوان ستون همراه ارز، مثلاً «رسید USD» / «Debit USD».</summary>
    public static string WithCurrency(CompanyFlowTextKey key, string? currencyCode, bool isEnglish)
        => string.IsNullOrWhiteSpace(currencyCode)
            ? Get(key, isEnglish)
            : $"{Get(key, isEnglish)} {currencyCode}";

    public static string WithCurrency(CompanyFlowTextKey key, string? currencyCode, HttpContext? context)
        => WithCurrency(key, currencyCode, context is not null && UiText.IsEn(context));

    /// <summary>عنوان ستون با ارز داخل پرانتز، مثلاً «رسید (USD)» / «Debit (USD)».</summary>
    public static string WithCurrencyParenthesized(CompanyFlowTextKey key, string? currencyCode, bool isEnglish)
        => string.IsNullOrWhiteSpace(currencyCode)
            ? Get(key, isEnglish)
            : $"{Get(key, isEnglish)} ({currencyCode})";

    /// <summary>عنوان جهت یک سطر. جهت خودش هرگز با زبان عوض نمی‌شود، فقط برچسبش ترجمه می‌شود.</summary>
    public static string Label(CompanyFlowDirection direction, bool isEnglish)
        => Get(direction == CompanyFlowDirection.Receipt ? CompanyFlowTextKey.Receipt : CompanyFlowTextKey.Outflow, isEnglish);

    public static string Label(CompanyFlowDirection direction, HttpContext? context = null)
        => Label(direction, context is not null && UiText.IsEn(context));

    /// <summary>معنی علامت بیلانس — یکسان برای همهٔ طرف‌حساب‌ها، فقط زبانش عوض می‌شود.</summary>
    public static string BalanceMeaning(decimal balance, CompanyFlowAccountKind accountKind, bool isEnglish)
        => Get(BalanceMeaningKey(balance, accountKind), isEnglish);

    public static string BalanceMeaning(decimal balance, CompanyFlowAccountKind accountKind, HttpContext? context = null)
        => BalanceMeaning(balance, accountKind, context is not null && UiText.IsEn(context));

    /// <summary>کلید معنی علامت بیلانس — علامت و منطقش مستقل از زبان است.</summary>
    public static CompanyFlowTextKey BalanceMeaningKey(decimal balance, CompanyFlowAccountKind accountKind)
    {
        if (balance == 0m)
        {
            return accountKind == CompanyFlowAccountKind.CashAccount
                ? CompanyFlowTextKey.CashEmptyBalance
                : CompanyFlowTextKey.SettledBalance;
        }

        if (accountKind == CompanyFlowAccountKind.CashAccount)
        {
            return balance > 0m ? CompanyFlowTextKey.CashPositiveBalance : CompanyFlowTextKey.CashNegativeBalance;
        }

        return balance > 0m ? CompanyFlowTextKey.PositiveBalance : CompanyFlowTextKey.NegativeBalance;
    }
}
