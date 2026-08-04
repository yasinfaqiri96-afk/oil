using PTGOilSystem.Web.Services.Exceptions;

namespace PTGOilSystem.Web.Services;

public static class SystemCurrency
{
    public const string BaseCurrencyCode = "USD";

    public static bool IsBaseCurrency(string? currencyCode)
        => string.Equals((currencyCode ?? string.Empty).Trim(), BaseCurrencyCode, StringComparison.OrdinalIgnoreCase);

    public static string Normalize(string? currencyCode)
        => string.IsNullOrWhiteSpace(currencyCode)
            ? BaseCurrencyCode
            : currencyCode.Trim().ToUpperInvariant();
}

/// <summary>
/// ریاضیِ نرخ ارز با یک دقتِ واحد.
///
/// دو جهتِ نرخ در سیستم وجود دارد و نباید یکی از دیگری بازسازی شود:
///   PerUsd  — «۱ دالر = چند واحد ارز» (چیزی که کاربر وارد می‌کند، مثلاً ۷۰)
///   ToUsd   — ضریبِ محاسباتی: AmountUsd = AmountOriginal × ToUsd
/// چون 1/70 اعشارِ متناهی ندارد، رفت‌وبرگشت بین این دو همیشه انحراف می‌سازد
/// (۷۰ → ۶۹.۹۹۸۶). به همین دلیل هر دو جهت جداگانه ذخیره می‌شوند و این کلاس فقط
/// جایی استفاده می‌شود که واقعاً چاره‌ای جز تبدیل نیست.
/// </summary>
public static class FxRateMath
{
    /// <summary>دقت ستون‌های نرخ: numeric(24,12).</summary>
    public const int RateDecimals = 12;

    public static decimal RoundRate(decimal rate)
        => decimal.Round(rate, RateDecimals, MidpointRounding.AwayFromZero);

    /// <summary>PerUsd → ToUsd. فقط برای محاسبه؛ نتیجه هرگز به‌جای نرخ واردشده ذخیره نشود.</summary>
    public static decimal ToUsdFromPerUsd(decimal perUsdRate)
        => perUsdRate > 0m ? RoundRate(1m / perUsdRate) : 1m;

    /// <summary>ToUsd → PerUsd. فقط برای دادهٔ Legacy که نرخ مستقیم ندارد؛ نتیجه «تخمینی» است.</summary>
    public static decimal PerUsdFromToUsd(decimal fxRateToUsd)
        => fxRateToUsd > 0m ? RoundRate(1m / fxRateToUsd) : 1m;
}

public sealed record CurrencyConversionResult(
    string SourceCurrencyCode,
    string BaseCurrencyCode,
    decimal AppliedRateToBase,
    DateTime EffectiveDate,
    bool FallbackApplied,
    bool ManualOverride,
    string SourceDescription)
{
    public decimal ConvertToBase(decimal sourceAmount)
        => decimal.Round(sourceAmount * AppliedRateToBase, 4, MidpointRounding.AwayFromZero);
}

public interface ICurrencyConversionService
{
    Task<CurrencyConversionResult> ResolveToBaseAsync(
        string sourceCurrencyCode,
        DateTime transactionDate,
        decimal? manualRateToBase = null,
        CancellationToken ct = default);
}

public sealed class CurrencyConversionService : ICurrencyConversionService
{
    private readonly IPricingService _pricing;

    public CurrencyConversionService(IPricingService pricing)
        => _pricing = pricing;

    public async Task<CurrencyConversionResult> ResolveToBaseAsync(
        string sourceCurrencyCode,
        DateTime transactionDate,
        decimal? manualRateToBase = null,
        CancellationToken ct = default)
    {
        var normalizedCurrency = SystemCurrency.Normalize(sourceCurrencyCode);
        var normalizedDate = DateTime.SpecifyKind(transactionDate.Date, DateTimeKind.Utc);

        if (SystemCurrency.IsBaseCurrency(normalizedCurrency))
        {
            return new CurrencyConversionResult(
                SourceCurrencyCode: SystemCurrency.BaseCurrencyCode,
                BaseCurrencyCode: SystemCurrency.BaseCurrencyCode,
                AppliedRateToBase: 1m,
                EffectiveDate: normalizedDate,
                FallbackApplied: false,
                ManualOverride: false,
                SourceDescription: "Identity USD/USD");
        }

        if (manualRateToBase.HasValue)
        {
            if (manualRateToBase <= 0m)
            {
                throw new BusinessRuleException(
                    "FX_RATE_INVALID",
                    "نرخ تبدیل باید بزرگ‌تر از صفر باشد.");
            }

            return new CurrencyConversionResult(
                SourceCurrencyCode: normalizedCurrency,
                BaseCurrencyCode: SystemCurrency.BaseCurrencyCode,
                AppliedRateToBase: manualRateToBase.Value,
                EffectiveDate: normalizedDate,
                FallbackApplied: false,
                ManualOverride: true,
                SourceDescription: $"Manual FX {normalizedCurrency}/{SystemCurrency.BaseCurrencyCode}");
        }

        var fxRate = await _pricing.GetFxRateAsync(
            normalizedCurrency,
            SystemCurrency.BaseCurrencyCode,
            normalizedDate,
            ct);

        return new CurrencyConversionResult(
            SourceCurrencyCode: normalizedCurrency,
            BaseCurrencyCode: SystemCurrency.BaseCurrencyCode,
            AppliedRateToBase: fxRate.Value,
            EffectiveDate: fxRate.EffectiveDate.Date,
            FallbackApplied: fxRate.FallbackApplied,
            ManualOverride: false,
            SourceDescription: fxRate.FallbackApplied
                ? $"DailyFxRate {normalizedCurrency}/{SystemCurrency.BaseCurrencyCode} fallback from {fxRate.EffectiveDate:yyyy-MM-dd}"
                : $"DailyFxRate {normalizedCurrency}/{SystemCurrency.BaseCurrencyCode} on {fxRate.EffectiveDate:yyyy-MM-dd}");
    }
}
