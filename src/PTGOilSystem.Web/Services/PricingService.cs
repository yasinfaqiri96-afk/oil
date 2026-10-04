using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Helpers;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services.Exceptions;

namespace PTGOilSystem.Web.Services;

/// <summary>
/// Pricing policy (business decision):
/// <list type="bullet">
///   <item><description>Platts data (Daily / Monthly / Manual) is <b>reference only</b> — never used to auto-calculate a contract's final price.</description></item>
///   <item><description>For every Platts contract the user MUST enter <c>ManualFinalPriceUsd</c>. Without it, the result is <c>NeedsReview</c>.</description></item>
///   <item><description><c>PlattsPeriodType</c> (Daily/Monthly/Manual) and <c>BenchmarkCode</c> are stored only for trace, audit and downstream reporting.</description></item>
///   <item><description><see cref="GetPlattsPriceAsync"/> remains available so other modules can <i>display</i> the reference rate, but it is intentionally NOT called from <see cref="CalculateContractPriceAsync"/>.</description></item>
/// </list>
/// </summary>
public class PricingService : IPricingService
{
    private readonly ApplicationDbContext _db;

    public PricingService(ApplicationDbContext db) => _db = db;

    public async Task<ContractPriceResult> CalculateContractPriceAsync(
        int contractId,
        CancellationToken ct = default)
    {
        var contract = await _db.Contracts
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == contractId, ct);

        if (contract is null)
        {
            throw new BusinessRuleException(
                "PRICING_CONTRACT_NOT_FOUND",
                "قرارداد برای محاسبه قیمت یافت نشد.");
        }

        return await CalculateContractPriceAsync(contract, ct);
    }

    public Task<ContractPriceResult> CalculateContractPriceAsync(
        Contract contract,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(contract);

        var premiumDiscountUsd = contract.PremiumDiscountUsd ?? contract.PremiumUsd ?? 0m;

        return ResolveContractPriceAsync(contract, premiumDiscountUsd, ct);
    }

    private async Task<ContractPriceResult> ResolveContractPriceAsync(
        Contract contract,
        decimal premiumDiscountUsd,
        CancellationToken ct)
    {
        var result = contract.PricingMethod switch
        {
            PricingMethod.Fixed => CalculateFixedContractPrice(contract),
            PricingMethod.FormulaPlatts => await CalculatePlattsContractPriceAsync(contract, premiumDiscountUsd, ct),
            PricingMethod.ManualFinalPrice => CalculateManualFinalPrice(contract),
            _ => BuildNeedsReviewResult(
                formulaText: "روش قیمت‌گذاری پشتیبانی نمی‌شود.",
                reason: "روش قیمت‌گذاری قرارداد معتبر نیست.",
                premiumDiscountUsd: premiumDiscountUsd)
        };

        if (contract.MinimumPriceUsd.HasValue
            && result.FinalUnitPrice.HasValue
            && result.FinalUnitPrice < contract.MinimumPriceUsd.Value
            && !ContractPricingAdapter.GetCanonicalFinalPrice(contract).HasValue)
        {
            var minPrice = contract.MinimumPriceUsd.Value;
            result = result with
            {
                FinalUnitPrice = minPrice,
                FormulaText = result.FormulaText + $" → حداقل {FormatAmount(minPrice)} اعمال شد",
                Reason = string.IsNullOrEmpty(result.Reason)
                    ? $"قیمت محاسبه‌شده کمتر از حداقل {FormatAmount(minPrice)} بود."
                    : result.Reason + $" | حداقل {FormatAmount(minPrice)} اعمال شد."
            };
        }

        return result;
    }

    public async Task<PriceLookupResult> GetPlattsPriceAsync(
        int productId,
        string benchmarkCode,
        DateTime transactionDate,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(benchmarkCode))
            throw new ArgumentException("benchmarkCode is required.", nameof(benchmarkCode));

        // Try exact date first, then fall back to the most recent prior price.
        var hit = await _db.DailyPlattsPrices
            .AsNoTracking()
            .Where(p => p.ProductId == productId
                        && p.BenchmarkCode == benchmarkCode
                        && p.PriceDate <= transactionDate)
            .OrderByDescending(p => p.PriceDate)
            .Select(p => new { p.PriceUsdPerMt, p.PriceDate })
            .FirstOrDefaultAsync(ct);

        if (hit is null)
        {
            throw new BusinessRuleException(
                "PRICING_NO_PLATTS",
                $"قیمت Platt's برای benchmark «{benchmarkCode}» در یا قبل از تاریخ {transactionDate.ToCalendarString("yyyy-MM-dd")} ثبت نشده است.");
        }

        return new PriceLookupResult(
            Value: hit.PriceUsdPerMt,
            EffectiveDate: hit.PriceDate,
            FallbackApplied: hit.PriceDate.Date != transactionDate.Date);
    }

    public async Task<PriceLookupResult> GetFxRateAsync(
        string baseCurrency,
        string quoteCurrency,
        DateTime transactionDate,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(baseCurrency))
            throw new ArgumentException("baseCurrency is required.", nameof(baseCurrency));
        if (string.IsNullOrWhiteSpace(quoteCurrency))
            throw new ArgumentException("quoteCurrency is required.", nameof(quoteCurrency));

        // Identity rate avoids a round-trip and a spurious "missing rate" error
        // when callers pass the same currency on both sides.
        if (string.Equals(baseCurrency, quoteCurrency, StringComparison.OrdinalIgnoreCase))
        {
            return new PriceLookupResult(1m, transactionDate, FallbackApplied: false);
        }

        var hit = await _db.DailyFxRates
            .AsNoTracking()
            .Where(r => r.BaseCurrency == baseCurrency
                        && r.QuoteCurrency == quoteCurrency
                        && r.RateDate <= transactionDate)
            .OrderByDescending(r => r.RateDate)
            .Select(r => new { r.Rate, r.RateDate })
            .FirstOrDefaultAsync(ct);

        if (hit is null)
        {
            throw new BusinessRuleException(
                "PRICING_NO_FX",
                $"نرخ ارز {baseCurrency}/{quoteCurrency} در یا قبل از تاریخ {transactionDate.ToCalendarString("yyyy-MM-dd")} ثبت نشده است.");
        }

        return new PriceLookupResult(
            Value: hit.Rate,
            EffectiveDate: hit.RateDate,
            FallbackApplied: hit.RateDate.Date != transactionDate.Date);
    }

    /// <summary>
    /// Platts contracts (Daily / Monthly / Manual) are NEVER auto-calculated.
    /// The user must enter <c>ManualFinalPriceUsd</c>; otherwise the contract
    /// stays in <c>NeedsReview</c> with a clear instruction in <c>Reason</c>.
    /// </summary>
    private Task<ContractPriceResult> CalculatePlattsContractPriceAsync(
        Contract contract,
        decimal premiumDiscountUsd,
        CancellationToken ct)
    {
        if (contract.ManualFinalPriceUsd.HasValue && contract.ManualFinalPriceUsd.Value > 0m)
        {
            var note = string.IsNullOrWhiteSpace(contract.PricingFormulaNote)
                ? string.Empty
                : $" | {contract.PricingFormulaNote.Trim()}";

            return Task.FromResult(new ContractPriceResult(
                FinalUnitPrice: contract.ManualFinalPriceUsd.Value,
                BasePlattsPrice: null,
                PremiumDiscountUsd: contract.PremiumDiscountUsd ?? contract.PremiumUsd,
                FormulaText: $"قیمت نهایی دستی Platts = {FormatAmount(contract.ManualFinalPriceUsd.Value)} ({ContractPricingAdapter.GetPricingDisplayLabel(contract)} — Reference){note}",
                NeedsReview: false,
                Reason: string.Empty,
                FallbackApplied: false));
        }

        return Task.FromResult(BuildNeedsReviewResult(
            formulaText: $"{ContractPricingAdapter.GetPricingDisplayLabel(contract)} - {ContractPricingAdapter.PendingStatusLabel}",
            reason: "قیمت نهایی Platts باید دستی وارد شود. سیستم قیمت Platts را اتومات محاسبه نمی‌کند.",
            premiumDiscountUsd: contract.PremiumDiscountUsd ?? contract.PremiumUsd));
    }

    private ContractPriceResult CalculateFixedContractPrice(Contract contract)
    {
        if (!contract.UnitPriceUsd.HasValue)
        {
            return BuildNeedsReviewResult(
                formulaText: "قیمت ثابت ثبت نشده است.",
                reason: "قیمت ثابت قرارداد وارد نشده.",
                premiumDiscountUsd: contract.PremiumDiscountUsd ?? contract.PremiumUsd);
        }

        return new ContractPriceResult(
            FinalUnitPrice: contract.UnitPriceUsd.Value,
            BasePlattsPrice: null,
            PremiumDiscountUsd: contract.PremiumDiscountUsd ?? contract.PremiumUsd,
            FormulaText: $"قیمت ثابت = {FormatAmount(contract.UnitPriceUsd.Value)} USD/MT",
            NeedsReview: false,
            Reason: string.Empty,
            FallbackApplied: false);
    }

    // Removed: CalculateDailyPlattsPriceAsync / CalculateMonthlyPlattsPriceAsync / CalculateManualPlattsPrice.
    // These were unreferenced and would have synthesized a final price from Platts reference data,
    // which is explicitly forbidden by the current business policy.
    // Platts data is reference-only; the user always enters the final price manually.

    private static ContractPriceResult CalculateManualFinalPrice(Contract contract)
    {
        if (!contract.ManualFinalPriceUsd.HasValue)
        {
            return BuildNeedsReviewResult(
                formulaText: "قیمت نهایی دستی وارد نشده است.",
                reason: "قیمت نهایی دستی وارد نشده است.",
                premiumDiscountUsd: null);
        }

        var note = string.IsNullOrWhiteSpace(contract.PricingFormulaNote)
            ? string.Empty
            : $" | {contract.PricingFormulaNote.Trim()}";

        return new ContractPriceResult(
            FinalUnitPrice: contract.ManualFinalPriceUsd.Value,
            BasePlattsPrice: null,
            PremiumDiscountUsd: null,
            FormulaText: $"قیمت نهایی دستی = {FormatAmount(contract.ManualFinalPriceUsd.Value)}{note}",
            NeedsReview: false,
            Reason: string.Empty,
            FallbackApplied: false);
    }

    private static ContractPriceResult BuildNeedsReviewResult(
        string formulaText,
        string reason,
        decimal? premiumDiscountUsd,
        decimal? basePlattsPrice = null,
        bool fallbackApplied = false)
        => new(
            FinalUnitPrice: null,
            BasePlattsPrice: basePlattsPrice,
            PremiumDiscountUsd: premiumDiscountUsd,
            FormulaText: formulaText,
            NeedsReview: true,
            Reason: reason,
            FallbackApplied: fallbackApplied);

    private static string FormatAmount(decimal value) => $"{value:N4} USD/MT";
}
