using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services.Ledger;

namespace PTGOilSystem.Web.Services;

/// <summary>
/// ساختِ مشترکِ ردیفِ لجرِ «کرایه دارایی عملیاتی» از روی یک <see cref="AssetRentTransaction"/>.
///
/// قرینهٔ <see cref="SaleLedgerFactory"/> است و عمداً همان شکل را دارد: ساختِ ردیف بیرون از
/// Controller می‌ماند تا جهتِ Debit/Credit و نگاشتِ طرف‌حساب در یک جا تعریف شود.
///
/// جهتِ ردیف از منطقِ واقعیِ همین سیستم گرفته شده، نه از قرارداد حسابداری عمومی:
///   • فروش <c>Side = Credit</c> با <c>CustomerId</c> ثبت می‌شود (SaleLedgerFactory).
///   • صورت‌حساب مشتری مانده را با <c>running += Credit - Debit</c> می‌سازد (CustomersController).
///   • دریافت از مشتری <c>Side = Debit</c> است (PaymentsController).
/// یعنی در حسابِ مشتریِ این سیستم Credit = «مشتری بدهکار شد». کرایه هم درآمد از همان جنس است،
/// پس همان Credit را می‌گیرد.
/// </summary>
public static class AssetRentLedgerFactory
{
    /// <summary>ثابتِ ردیابیِ ردیف لجر. با <c>SourceId = rent.Id</c> کلید یکتای ثبت است.</summary>
    public const string LedgerSourceType = "AssetRent";

    /// <summary>پسوندِ ردیفِ برگشت — همان قراردادی که لغو فروش و لغو مصرف استفاده می‌کنند.</summary>
    public const string CancelReferenceSuffix = CompanyFlow.CompanyFlowSourceTypes.ReversalReferenceSuffix;

    public static string BuildReference(AssetRentTransaction rent)
        => string.IsNullOrWhiteSpace(rent.ReferenceDocument)
            ? $"RENT-{rent.Id}"
            : rent.ReferenceDocument!;

    public static string BuildDescription(AssetRentTransaction rent, string? assetCode)
        => string.IsNullOrWhiteSpace(assetCode)
            ? $"ثبت کرایه دارایی عملیاتی #{rent.OperationalAssetId}"
            : $"ثبت کرایه دارایی عملیاتی {assetCode}";

    /// <summary>
    /// ردیفِ لجرِ کرایه. مقادیر ارز/نرخ از همان <see cref="CurrencyConversionResult"/> می‌آید که
    /// خودِ تراکنش با آن به ارز پایه تبدیل شده، پس ردیف و تراکنش هرگز دو نرخ متفاوت نمی‌گیرند.
    ///
    /// نگاشتِ طرف‌حساب هیچ حدسی ندارد: هر شناسه از فیلدِ <c>ChargedTo*</c> خودش می‌آید و
    /// <paramref name="contractCustomerId"/> تنها وقتی پر می‌شود که فراخوان از روی قراردادِ فروشِ
    /// واقعی آن را خوانده باشد. شریک نیز فقط از ChargedToPartnerId صریح نگاشت می‌شود.
    /// </summary>
    public static LedgerPostingRequest BuildRentLedgerEntry(
        AssetRentTransaction rent,
        CurrencyConversionResult conversion,
        string? assetCode,
        int? contractCustomerId)
        => new()
        {
            EntryDate = rent.RentDate,
            Side = LedgerSide.Credit,
            AmountUsd = rent.AmountUsd,
            Currency = SystemCurrency.BaseCurrencyCode,
            SourceAmount = rent.AmountOriginal,
            SourceCurrencyCode = rent.Currency,
            AppliedFxRateToUsd = rent.FxRateToUsd,
            AppliedFxRateDate = conversion.EffectiveDate.Date,
            AppliedFxRateSource = conversion.SourceDescription,
            Description = BuildDescription(rent, assetCode),
            SourceType = LedgerSourceType,
            SourceId = rent.Id,
            Reference = BuildReference(rent),
            ContractId = rent.ChargedToContractId,
            CustomerId = rent.ChargedToCustomerId ?? contractCustomerId,
            ServiceProviderId = rent.ChargedToServiceProviderId,
            PartnerId = rent.ChargedToPartnerId
        };

    /// <summary>
    /// ردیفِ برگشت. عیناً الگوی لغو فروش: سطر اصلی دست‌نخورده می‌ماند، یک سطر جبرانی با جهتِ
    /// معکوس و همان <c>(SourceType, SourceId)</c> اضافه می‌شود تا هر دو در همان گردش حساب کنار هم
    /// دیده شوند و جمعشان صفر باشد. تاریخِ ردیف، تاریخِ لغو است نه تاریخِ کرایه.
    /// </summary>
    public static LedgerPostingRequest BuildReversalLedgerEntry(
        AssetRentTransaction rent,
        LedgerEntry originalLedger,
        DateTime reversalDate)
        => new()
        {
            EntryDate = reversalDate,
            Side = LedgerSide.Debit,
            AmountUsd = originalLedger.AmountUsd,
            Currency = originalLedger.Currency,
            SourceAmount = originalLedger.SourceAmount,
            SourceCurrencyCode = originalLedger.SourceCurrencyCode,
            AppliedFxRateToUsd = originalLedger.AppliedFxRateToUsd,
            AppliedFxRateDate = originalLedger.AppliedFxRateDate,
            AppliedFxRateSource = originalLedger.AppliedFxRateSource,
            Description = $"لغو کرایه دارایی #{rent.Id} | {originalLedger.Description}",
            SourceType = LedgerSourceType,
            SourceId = rent.Id,
            Reference = (originalLedger.Reference ?? BuildReference(rent)) + CancelReferenceSuffix,
            ContractId = originalLedger.ContractId,
            CustomerId = originalLedger.CustomerId,
            ServiceProviderId = originalLedger.ServiceProviderId,
            PartnerId = originalLedger.PartnerId
        };
}
