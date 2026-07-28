using System.Globalization;
using PTGOilSystem.Web.Services.CompanyFlow;

namespace PTGOilSystem.Web.Models.PartyStatements;

public enum PartyStatementPartyType
{
    Customer = 1,
    Supplier = 2,
    ServiceProvider = 3,
    Sarraf = 4,
    Employee = 5,
    Partner = 6,
    Driver = 7,
    Company = 8
}

public readonly record struct PartyRef(
    PartyStatementPartyType PartyType,
    int PartyId,
    int? CompanyId = null);

public sealed class PartyStatementFilter
{
    public DateTime? FromDate { get; init; }
    public DateTime? ToDate { get; init; }
    public int? ContractId { get; init; }
    public int? CompanyId { get; init; }
    public string? CurrencyCode { get; init; }
    // پیش‌فرض: ستون‌های نفتی مخفی باشند و کاربر در صورت نیاز نمایششان دهد.
    public bool IncludeOperationalColumns { get; init; }
}

public sealed class PartyStatementPolicy
{
    public required PartyStatementPartyType PartyType { get; init; }
    public required string StatementTitleFa { get; init; }
    public required string StatementTitleEn { get; init; }
    public required string PartyInformationTitleFa { get; init; }
    public required string PartyInformationTitleEn { get; init; }
    public required string AccountTypeFa { get; init; }
    public required string AccountTypeEn { get; init; }
    /// <summary>یعنی چه چیزی در ستون «رسید» این طرف‌حساب می‌نشیند.</summary>
    public required string ReceiptMeaningFa { get; init; }
    /// <summary>یعنی چه چیزی در ستون «Debit» این طرف‌حساب می‌نشیند.</summary>
    public required string ReceiptMeaningEn { get; init; }
    /// <summary>یعنی چه چیزی در ستون «برد» این طرف‌حساب می‌نشیند.</summary>
    public required string OutflowMeaningFa { get; init; }
    /// <summary>یعنی چه چیزی در ستون «Credit» این طرف‌حساب می‌نشیند.</summary>
    public required string OutflowMeaningEn { get; init; }
    public bool SupportsOperationalColumns { get; init; }

    /// <summary>نقش طرف‌حساب برای لایهٔ مرکزی تعیین جهت.</summary>
    public required CompanyFlowPartyRole FlowRole { get; init; }

    public string AccountType(bool isEnglish) => isEnglish ? AccountTypeEn : AccountTypeFa;

    public string ReceiptMeaning(bool isEnglish) => isEnglish ? ReceiptMeaningEn : ReceiptMeaningFa;

    public string OutflowMeaning(bool isEnglish) => isEnglish ? OutflowMeaningEn : OutflowMeaningFa;

    /// <summary>
    /// معنی علامت بیلانس — علامت و منطقش مستقل از زبان است و فقط متن ترجمه می‌شود.
    /// همیشه از منبع مرکزی <see cref="CompanyFlowText"/> می‌آید.
    /// </summary>
    public string BalanceMeaning(decimal balance, bool isEnglish = false)
        => CompanyFlowText.BalanceMeaning(balance, CompanyFlowAccountKind.PartyAccount, isEnglish);
}

public sealed class PartyStatementSummary
{
    public decimal OpeningBalance { get; init; }
    public decimal TotalReceipt { get; init; }
    public decimal TotalOutflow { get; init; }
    public decimal ClosingBalance { get; init; }
    public decimal ClosingBalanceAbsolute => Math.Abs(ClosingBalance);
    public string ClosingBalanceMeaning { get; init; } = string.Empty;
    public string ClosingBalanceMeaningEn { get; init; } = string.Empty;

    /// <summary>معنی بیلانس نهایی در زبان انتخاب‌شده — عدد و علامت تغییر نمی‌کند.</summary>
    public string ClosingBalanceMeaningFor(bool isEnglish)
        => isEnglish ? ClosingBalanceMeaningEn : ClosingBalanceMeaning;
    public string BaseCurrencyCode { get; init; } = "USD";

    // نمایش روبلی: وقتی ارز روبل انتخاب شود، جمع‌ها با ارزش روبلی واقعیِ هر سند
    // (نرخ تاریخی همان سند) محاسبه می‌شوند. اسنادی که به روبل ثبت نشده‌اند ارزش
    // روبلی ندارند و در این جمع‌ها شرکت نمی‌کنند (در سطر با «—» نمایش داده می‌شوند).
    public bool IsRubPresentation { get; init; }
    public decimal? OpeningBalanceRub { get; init; }
    public decimal? TotalReceiptRub { get; init; }
    public decimal? TotalOutflowRub { get; init; }
    public decimal? ClosingBalanceRub { get; init; }
    public decimal? ClosingBalanceRubAbsolute => ClosingBalanceRub.HasValue ? Math.Abs(ClosingBalanceRub.Value) : null;
}

public sealed class PartyStatementRow
{
    public int Sequence { get; set; }
    public DateTime Date { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public string? Reference { get; set; }
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// شرح سطر در زبان نمایش. تنها سطری که ترجمه می‌شود سطر «بیلانس اول دوره» است؛ بقیه
    /// شرحِ خودِ سند هستند و ترجمه نمی‌شوند تا داده تاریخی دست‌نخورده بماند.
    /// </summary>
    public string DescriptionFor(bool isEnglish)
        => IsOpeningBalance
            ? CompanyFlowText.Get(CompanyFlowTextKey.OpeningBalance, isEnglish)
            : Description;

    /// <summary>رسید — ارزشی که شرکت در این سطر دریافت کرده است (USD).</summary>
    public decimal? ReceiptBase { get; set; }
    /// <summary>برد — ارزشی که شرکت در این سطر داده است (USD).</summary>
    public decimal? OutflowBase { get; set; }
    public decimal RunningBalance { get; set; }
    // ارزش روبلی سطر با نرخ تاریخی همان سند (فقط اسناد ذاتاً روبلی). null یعنی سند
    // روبلی نیست و در نمایش روبلی «—» نشان داده می‌شود.
    public decimal? ReceiptRub { get; set; }
    public decimal? OutflowRub { get; set; }
    public decimal? RunningBalanceRub { get; set; }
    public decimal? OriginalAmount { get; set; }
    public string OriginalCurrency { get; set; } = "USD";
    public decimal? FxRate { get; set; }
    public string? FxRateDisplay { get; set; }
    public decimal? Quantity { get; set; }
    public string? QuantityUnit { get; set; }
    public decimal? PlattsPrice { get; set; }
    public decimal? PremiumOrDiscount { get; set; }
    public decimal? UnitPrice { get; set; }
    public string SourceType { get; set; } = string.Empty;
    public int SourceId { get; set; }
    public long PostingSequence { get; set; }
    public int? ContractId { get; set; }
    public string? ContractNumber { get; set; }
    public bool IsOpeningBalance { get; set; }

    /// <summary>سند لغوشده/جایگزین‌شده — فقط برای Audit نمایش داده می‌شود و در جمع‌ها نمی‌آید.</summary>
    public bool IsCancelled { get; set; }

    /// <summary>این سطر خودش سند برگشت است (جهتش وارونهٔ سند اصلی محاسبه شده).</summary>
    public bool IsReversalRow { get; set; }

    /// <summary>جهت تجاری سطر از دید شرکت — از لایهٔ مرکزی می‌آید، نه از Debit/Credit.</summary>
    public CompanyFlowDirection? FlowDirection { get; set; }

    // بیلانس طرف‌حساب: اول دوره + Σبرد − Σرسید (مطابق فایل کاری شرکت).
    public decimal SignedAmount => (OutflowBase ?? 0m) - (ReceiptBase ?? 0m);

    // اثر روبلی سطر روی بیلانس. null یعنی سند روبلی نیست (در جمع روبلی شرکت نمی‌کند).
    public decimal? SignedAmountRub =>
        OutflowRub.HasValue || ReceiptRub.HasValue ? (OutflowRub ?? 0m) - (ReceiptRub ?? 0m) : null;
}

public sealed class PartyStatementPartyInfo
{
    public int Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? Code { get; init; }
    public string? Phone { get; init; }
    public string? Email { get; init; }
    public string? Address { get; init; }
}

public sealed class PartyStatementDocumentInfo
{
    public string StatementNumber { get; init; } = string.Empty;
    public DateTime StatementDate { get; init; }
    public DateTime? PeriodFrom { get; init; }
    public DateTime? PeriodTo { get; init; }
    public string BaseCurrencyCode { get; init; } = "USD";
    public DateTime GeneratedAtUtc { get; init; }
}

public sealed class PartyStatementColumnOptions
{
    public bool ShowRub { get; init; }
    public bool ShowAed { get; init; }
    public bool ShowOriginalAmount { get; init; }
    public bool ShowCurrency { get; init; }
    public bool ShowFxRate { get; init; }
    public bool ShowQuantity { get; init; }
    public bool ShowPlatts { get; init; }
    public bool ShowPremiumOrDiscount { get; init; }
    public bool ShowUnitPrice { get; init; }

    public bool HasOperationalColumns => ShowQuantity || ShowPlatts || ShowPremiumOrDiscount || ShowUnitPrice;
    public bool UseLandscape => HasOperationalColumns || ShowRub || ShowAed || ShowOriginalAmount;
}

public sealed class PartyStatementAuthorization
{
    public string? AuthorizedByName { get; init; }
    public string? AuthorizedByTitle { get; init; }
    public string? SignatureImagePath { get; init; }
}

public sealed class PartyStatementCompanyInfo
{
    public string Name { get; init; } = "Saddiqi Group of Companies";
    public string Subtitle { get; init; } = "GROUP OF COMPANIES";
    public string? Address { get; init; }
    public string? Phone { get; init; }
    public string? Email { get; init; }
    public string? Website { get; init; }
    public string LogoPath { get; init; } = "/images/logo1-sidebar.png";
}

public sealed class PartyStatementResult
{
    public required PartyRef Party { get; init; }
    public required PartyStatementPolicy Policy { get; init; }
    public required PartyStatementCompanyInfo CompanyInfo { get; init; }
    public required PartyStatementPartyInfo PartyInfo { get; init; }
    public required PartyStatementDocumentInfo DocumentInfo { get; init; }
    public required PartyStatementSummary Summary { get; init; }
    public required PartyStatementColumnOptions ColumnOptions { get; init; }
    public required IReadOnlyList<PartyStatementRow> Rows { get; init; }
    public string? Note { get; init; }
    public required PartyStatementAuthorization Authorization { get; init; }
    public string CourtesyText { get; init; } = "از همکاری دوامدار شما سپاس‌گزاریم.";
}

// نمای صورت‌حساب تأمین‌کننده. پیش‌فرض «قراردادها»؛ سایر طرف‌حساب‌ها همیشه Ledger می‌مانند.
public enum SupplierStatementView
{
    Contracts = 0,
    Ledger = 1,
    Loadings = 2
}

public sealed class PartyStatementViewModel
{
    public required PartyStatementResult Statement { get; init; }
    public required PartyStatementFilter Filter { get; init; }
    public bool IsPrintMode { get; init; }
    public bool IsRtl { get; init; } = true;
    public PartyStatementPartyType PartyType => Statement.Party.PartyType;

    // گزینه‌های دراپ‌داون نوار فیلتر (قرارداد/شرکت/ارز).
    public IReadOnlyList<PartyStatementFilterOption> ContractOptions { get; init; } = [];
    public IReadOnlyList<PartyStatementFilterOption> CompanyOptions { get; init; } = [];
    public IReadOnlyList<string> CurrencyOptions { get; init; } = [];

    // فقط تأمین‌کننده تب‌های سه‌گانه را می‌بیند؛ پیش‌فرض Ledger تا رفتار بقیهٔ طرف‌حساب‌ها عوض نشود.
    public SupplierStatementView SupplierView { get; init; } = SupplierStatementView.Ledger;
    public bool ShowSupplierViewTabs => PartyType == PartyStatementPartyType.Supplier;

    // نمای «قراردادها»: گروه‌بندی نمایشیِ همان سطرهای مالی؛ بدون محاسبهٔ مالی جدید.
    public SupplierContractStatementViewModel? ContractGrouping { get; init; }
}

// خلاصهٔ نمای «قراردادها» — گروه‌بندیِ نمایشی روی Rows موجود. جمع‌ها عیناً برابر نمای خطی‌اند.
public sealed class SupplierContractStatementViewModel
{
    public required IReadOnlyList<SupplierContractStatementRow> Rows { get; init; }
    public bool HasOpeningRow { get; init; }
    public decimal OpeningBalance { get; init; }
    public decimal? OpeningBalanceRub { get; init; }
    public bool IsRub { get; init; }
    public decimal TotalReceipt { get; init; }
    public decimal TotalOutflow { get; init; }
    public decimal ClosingBalance { get; init; }
    public decimal? TotalReceiptRub { get; init; }
    public decimal? TotalOutflowRub { get; init; }
    public decimal? ClosingBalanceRub { get; init; }
}

// مدلِ ردیف جزئیات (lazy): همان صورت‌حساب قرارداد + اطلاعات نمایشیِ قرارداد برای هدرِ جزئیات.
public sealed class SupplierContractDetailsViewModel
{
    public required PartyStatementResult Statement { get; init; }
    public string? ProductName { get; init; }
    public decimal? ContractQuantityMt { get; init; }
    public decimal? UnitPriceUsd { get; init; }
    public decimal? ContractValueUsd { get; init; }
    public decimal? LoadedQuantityMt { get; init; }
    public decimal? RemainingQuantityMt =>
        ContractQuantityMt.HasValue && LoadedQuantityMt.HasValue
            ? ContractQuantityMt.Value - LoadedQuantityMt.Value
            : null;
}

// یک سطر = یک قرارداد (یا گروهِ «بدون قرارداد»). Debit/Credit/Balance جمعِ سطرهای مالیِ همان قرارداد است.
public sealed class SupplierContractStatementRow
{
    public int Sequence { get; init; }
    public int? ContractId { get; init; }
    public string? ContractNumber { get; init; }
    public DateTime? FirstDate { get; init; }
    public DateTime? LastDate { get; init; }
    public string Title { get; init; } = string.Empty;
    public string? ProductName { get; init; }
    public decimal? ContractQuantityMt { get; init; }
    public decimal? UnitPriceUsd { get; init; }
    public decimal? ContractValueUsd { get; init; }
    public decimal? LoadedQuantityMt { get; init; }
    public decimal? RemainingQuantityMt =>
        ContractQuantityMt.HasValue && LoadedQuantityMt.HasValue
            ? ContractQuantityMt.Value - LoadedQuantityMt.Value
            : null;
    public decimal Receipt { get; init; }
    public decimal Outflow { get; init; }
    public decimal Balance { get; init; }
    public decimal? ReceiptRub { get; init; }
    public decimal? OutflowRub { get; init; }
    public decimal? BalanceRub { get; init; }
}

public sealed record PartyStatementFilterOption(int Id, string Text);

public sealed class PartyStatementOptions
{
    public const string SectionName = "PartyStatements";

    public string CompanyName { get; set; } = "Saddiqi Group of Companies";
    public string CompanySubtitle { get; set; } = "GROUP OF COMPANIES";
    public string? Address { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? Website { get; set; }
    public string LogoPath { get; set; } = "/images/logo1-sidebar.png";
    public string CourtesyText { get; set; } = "از همکاری دوامدار شما سپاس‌گزاریم.";
    public string Note { get; set; } = "لطفاً صورت‌حساب را بررسی کرده و هرگونه مغایرت را با بخش مالی در میان بگذارید.";
    public string? AuthorizedByName { get; set; }
    public string? AuthorizedByTitle { get; set; }
    public string? SignatureImagePath { get; set; }
    public string BaseCurrencyCode { get; set; } = "USD";
}

public static class PartyStatementFormatting
{
    public static string? FxDisplay(decimal? basePerTransactionCurrency, string? currency)
    {
        if (!basePerTransactionCurrency.HasValue
            || basePerTransactionCurrency.Value <= 0m
            || string.Equals(currency, "USD", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var perUsd = 1m / basePerTransactionCurrency.Value;
        return $"1 USD = {perUsd.ToString("0.####", CultureInfo.InvariantCulture)} {currency?.ToUpperInvariant()}";
    }
}
