using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services;

namespace PTGOilSystem.Web.Helpers;

/// <summary>
/// از کجا رقم روبلی یک بارگیری آمده است. برای تفکیک «رقم واقعی» از «تبدیل با نرخ قرارداد».
/// </summary>
public enum LoadingRubBasis
{
    /// <summary>هیچ منبع روبلی موجود نیست.</summary>
    None = 0,
    /// <summary>snapshot لحظهٔ قفل نرخ روبل همان بارگیری (AmountRubAtRubLock).</summary>
    LockSnapshot = 1,
    /// <summary>نرخ قفل‌شدهٔ همان بارگیری × ارزش دالری جاری آن بارگیری.</summary>
    LoadingLockedRate = 2,
    /// <summary>رقم روبلی خودِ بارگیری (SettlementValueRub یا SettlementUnitPriceRub).</summary>
    LoadingAmount = 3,
    /// <summary>قیمت واحد روبلی قرارداد روبلی × مقدار بارگیری‌شده.</summary>
    ContractUnitPrice = 4,
    /// <summary>آخرین fallback: ContractRubPerUsdRate × ارزش دالری. تخمینی است.</summary>
    ContractRate = 5
}

/// <summary>ارزش روبلی یک بارگیری به‌همراه منبع آن.</summary>
public readonly record struct LoadingRubValue(decimal? AmountRub, LoadingRubBasis Basis)
{
    public static readonly LoadingRubValue None = new(null, LoadingRubBasis.None);
    public bool HasValue => AmountRub.HasValue;
}

/// <summary>ورودی حداقلیِ یک بارگیری برای ارزش‌گذاری روبلی (مشترک بین Entity و projectionها).</summary>
public readonly record struct LoadingRubFacts(
    decimal LoadedQuantityMt,
    decimal? LoadingPriceUsd,
    RubSettlementRateStatus RubRateStatus,
    decimal? RubPerUsdRate,
    decimal? AmountRubAtRubLock,
    decimal? SettlementUnitPriceRub,
    decimal? SettlementValueRub);

/// <summary>ارزش روبلی یک قرارداد (جمع بارگیری‌ها) به‌همراه اینکه تخمینی است یا واقعی.</summary>
public readonly record struct ContractRubTotal(decimal? AmountRub, bool IsEstimated)
{
    public static readonly ContractRubTotal None = new(null, false);
    public bool HasValue => AmountRub.HasValue;
}

/// <summary>
/// منبع واحد ارزش روبلی بارگیری‌ها. صفحهٔ جریان قرارداد و صفحهٔ تأمین‌کننده هر دو از همین‌جا
/// می‌خوانند تا دو صفحه برای یک قرارداد دو عدد متفاوت نشان ندهند.
///
/// ترتیب منابع (اولویت با رقم واقعیِ خودِ بارگیری):
///   ۱) snapshot قفل روبل — همان چیزی که دفترکل (SupplierLoadingLedger) استفاده می‌کند،
///      پس اگر قیمت بعد از قفل بدون بازقفل اصلاح شود، پروفایل و دفترکل یک عدد می‌دهند.
///   ۲) نرخ قفل‌شدهٔ همان بارگیری × ارزش دالری جاری آن.
///   ۳) رقم روبلی فایل بارگیری (ارزش کل، یا قیمت واحد × مقدار).
///   ۴) قیمت واحد روبلیِ قراردادِ روبلی × مقدار بارگیری‌شده (فقط در سطح قرارداد).
///   ۵) ContractRubPerUsdRate × ارزش دالری — آخرین fallback و «تخمینی» علامت می‌خورد.
///
/// SettlementCurrencyCode شرط ورود نیست: بارگیری‌ای که رقم روبلی یا نرخ قفل‌شده دارد روبلی
/// شناخته می‌شود، حتی اگر ارز تسویه‌اش USD مانده باشد.
/// </summary>
public static class LoadingRubValuation
{
    public static LoadingRubValue Resolve(in LoadingRubFacts loading, decimal? contractFinalPriceUsd)
    {
        if (loading.RubRateStatus == RubSettlementRateStatus.Locked
            && loading.AmountRubAtRubLock is > 0m)
        {
            return new LoadingRubValue(loading.AmountRubAtRubLock.Value, LoadingRubBasis.LockSnapshot);
        }

        var loadingValueUsd = LoadingValueUsd(loading.LoadedQuantityMt, loading.LoadingPriceUsd, contractFinalPriceUsd);
        if (loading.RubRateStatus == RubSettlementRateStatus.Locked
            && loadingValueUsd.HasValue
            && loading.RubPerUsdRate is > 0m)
        {
            return new LoadingRubValue(
                decimal.Round(loadingValueUsd.Value * loading.RubPerUsdRate.Value, 2, MidpointRounding.AwayFromZero),
                LoadingRubBasis.LoadingLockedRate);
        }

        if (loading.SettlementValueRub.HasValue)
        {
            return new LoadingRubValue(loading.SettlementValueRub.Value, LoadingRubBasis.LoadingAmount);
        }

        if (loading.SettlementUnitPriceRub.HasValue)
        {
            return new LoadingRubValue(
                decimal.Round(loading.LoadedQuantityMt * loading.SettlementUnitPriceRub.Value, 2, MidpointRounding.AwayFromZero),
                LoadingRubBasis.LoadingAmount);
        }

        return LoadingRubValue.None;
    }

    /// <summary>
    /// ارزش روبلی کل بارگیری‌های یک قرارداد. اگر حتی یک بارگیری رقم روبلی واقعی داشته باشد،
    /// جمعِ همان ارقام واقعی برگردانده می‌شود و هیچ تبدیلی با نرخ انجام نمی‌شود. تبدیل با نرخ
    /// فقط وقتی اتفاق می‌افتد که هیچ منبع روبلی واقعی وجود نداشته باشد، و نتیجه تخمینی است.
    /// </summary>
    public static ContractRubTotal AggregateForContract(
        IEnumerable<LoadingRubFacts> loadings,
        decimal? contractFinalPriceUsd,
        decimal? contractUnitPriceRub,
        decimal loadedQuantityMt,
        decimal loadedValueUsd,
        decimal? contractRubPerUsdRate)
    {
        var total = 0m;
        var hasActual = false;
        foreach (var loading in loadings)
        {
            var value = Resolve(loading, contractFinalPriceUsd);
            if (!value.HasValue)
            {
                continue;
            }

            total += value.AmountRub!.Value;
            hasActual = true;
        }

        if (hasActual)
        {
            return new ContractRubTotal(total, false);
        }

        // قرارداد روبلی با قیمت واحد روبلی: رقم واقعی است، نه تبدیل با نرخ.
        if (contractUnitPriceRub is > 0m && loadedQuantityMt > 0m)
        {
            return new ContractRubTotal(
                decimal.Round(loadedQuantityMt * contractUnitPriceRub.Value, 4, MidpointRounding.AwayFromZero),
                false);
        }

        if (contractRubPerUsdRate is > 0m)
        {
            return new ContractRubTotal(
                decimal.Round(loadedValueUsd * contractRubPerUsdRate.Value, 4, MidpointRounding.AwayFromZero),
                true);
        }

        return ContractRubTotal.None;
    }

    public static bool IsRubCurrency(string? currency)
        => string.Equals(SystemCurrency.Normalize(currency), "RUB", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// نرخ روبل قرارداد: نرخ ثابت ثبت‌شده، وگرنه نرخ اعمال‌شدهٔ قراردادِ روبلی. اگر هیچ‌کدام
    /// نباشد null — نرخ از نسبت پرداخت‌ها یا نرخ جاری ساخته نمی‌شود.
    /// </summary>
    public static decimal? ContractRubPerUsdRate(
        string? contractCurrency,
        decimal? contractRubPerUsdRate,
        decimal? contractAppliedFxRateToUsd)
    {
        if (contractRubPerUsdRate is > 0m)
        {
            return contractRubPerUsdRate.Value;
        }

        if (IsRubCurrency(contractCurrency) && contractAppliedFxRateToUsd is > 0m)
        {
            return decimal.Round(1m / contractAppliedFxRateToUsd.Value, 6, MidpointRounding.AwayFromZero);
        }

        return null;
    }

    public static decimal? LoadingValueUsd(decimal loadedQuantityMt, decimal? loadingPriceUsd, decimal? contractFinalPriceUsd)
    {
        var effectivePrice = IPurchaseAggregationService.HasValidLoadingPrice(loadingPriceUsd)
            ? loadingPriceUsd
            : contractFinalPriceUsd;

        return IPurchaseAggregationService.HasValidLoadingPrice(effectivePrice)
            ? decimal.Round(loadedQuantityMt * effectivePrice!.Value, 4, MidpointRounding.AwayFromZero)
            : null;
    }
}
