using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.Payments;
using PTGOilSystem.Web.Services.Reporting;
using PTGOilSystem.Web.Services.Time;

namespace PTGOilSystem.Web.Services;

/// <summary>
/// تنها مرجعِ کارت‌های «دریافتی/پرداختی امروز» و «ماندهٔ نقدی». روزنامچه، مرکز روزنامچه،
/// دفتر حساب‌ها، فهرست صندوق‌ها و وضعیت مالی شرکت همه همین را می‌خوانند.
/// </summary>
public static class FinanceMetricCardsQuery
{
    private const string CacheKey = "finance-metric-cards-v2";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(45);

    public static async Task<FinanceMetricCardsViewModel> BuildAsync(
        ApplicationDbContext db,
        IMemoryCache? cache = null,
        string? ariaLabel = null,
        IAfghanistanBusinessClock? businessClock = null)
    {
        businessClock ??= new AfghanistanBusinessClock(TimeProvider.System);
        if (cache is null)
        {
            return await BuildCoreAsync(db, ariaLabel, businessClock);
        }

        var metrics = await cache.GetOrCreateAsync(CacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheTtl;
            return await BuildCoreAsync(db, ariaLabel: null, businessClock);
        }) ?? await BuildCoreAsync(db, ariaLabel: null, businessClock);

        return string.IsNullOrWhiteSpace(ariaLabel)
            ? metrics
            : metrics.WithAriaLabel(ariaLabel);
    }

    private static async Task<FinanceMetricCardsViewModel> BuildCoreAsync(
        ApplicationDbContext db,
        string? ariaLabel,
        IAfghanistanBusinessClock businessClock)
    {
        // «امروز» یک روزِ کامل است، نه تساویِ دقیقِ زمان؛ اگر سندی با ساعت ذخیره شده باشد هم شمرده می‌شود.
        var today = businessClock.Today.Date;
        var tomorrow = today.AddDays(1);

        // فعالیتِ روزنامچه: همهٔ اسنادِ امروز، از جمله پرداختی که شریک از جیب خودش داده.
        // سندِ ارزیِ بی‌معادلِ دالری در جمع صفر می‌افتد، پس تعدادش جدا برگردانده می‌شود.
        var todayTotals = await db.PaymentTransactions
            .AsNoTracking()
            .Where(p => p.PaymentDate >= today && p.PaymentDate < tomorrow)
            .GroupBy(_ => 1)
            .Select(g => new
            {
                ReceiptUsd = g.Where(p => p.Direction == PaymentDirection.In).Sum(p => (decimal?)p.AmountUsd) ?? 0m,
                PaymentUsd = g.Where(p => p.Direction == PaymentDirection.Out).Sum(p => (decimal?)p.AmountUsd) ?? 0m,
                ReceiptMissingUsdEquivalentCount = g.Count(p =>
                    p.Direction == PaymentDirection.In
                    && p.Amount > 0m
                    && p.Currency != SystemCurrency.BaseCurrencyCode
                    && p.AmountUsd == 0m),
                PaymentMissingUsdEquivalentCount = g.Count(p =>
                    p.Direction == PaymentDirection.Out
                    && p.Amount > 0m
                    && p.Currency != SystemCurrency.BaseCurrencyCode
                    && p.AmountUsd == 0m)
            })
            .FirstOrDefaultAsync();

        var cashTotals = await new CashPositionReader(db).ReadAccountTotalsAsync();

        var transactionCount = await db.PaymentTransactions
            .AsNoTracking()
            .CountAsync();

        return new FinanceMetricCardsViewModel
        {
            AriaLabel = string.IsNullOrWhiteSpace(ariaLabel) ? "آمار روزنامچه دریافت و پرداخت" : ariaLabel,
            TodayReceiptUsd = todayTotals?.ReceiptUsd ?? 0m,
            TodayPaymentUsd = todayTotals?.PaymentUsd ?? 0m,
            TodayReceiptMissingUsdEquivalentCount = todayTotals?.ReceiptMissingUsdEquivalentCount ?? 0,
            TodayPaymentMissingUsdEquivalentCount = todayTotals?.PaymentMissingUsdEquivalentCount ?? 0,
            CashAccountsBalanceUsd = CashPositionReader.TotalBalanceUsd(cashTotals),
            CashBalanceMissingUsdEquivalentCount = CashPositionReader.TotalMissingUsdEquivalentCount(cashTotals),
            TransactionCount = transactionCount
        };
    }
}
