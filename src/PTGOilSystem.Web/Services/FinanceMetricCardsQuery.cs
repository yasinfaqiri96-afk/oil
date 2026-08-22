using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.Payments;
using PTGOilSystem.Web.Services.Time;

namespace PTGOilSystem.Web.Services;

public static class FinanceMetricCardsQuery
{
    private const string CacheKey = "finance-metric-cards-v1";
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
            : new FinanceMetricCardsViewModel
            {
                AriaLabel = ariaLabel,
                TodayReceiptUsd = metrics.TodayReceiptUsd,
                TodayPaymentUsd = metrics.TodayPaymentUsd,
                CashAccountsBalanceUsd = metrics.CashAccountsBalanceUsd,
                TransactionCount = metrics.TransactionCount
            };
    }

    private static async Task<FinanceMetricCardsViewModel> BuildCoreAsync(
        ApplicationDbContext db,
        string? ariaLabel,
        IAfghanistanBusinessClock businessClock)
    {
        var today = businessClock.Today;

        var todayTotals = await db.PaymentTransactions
            .AsNoTracking()
            .Where(p => p.PaymentDate == today)
            .GroupBy(_ => 1)
            .Select(g => new
            {
                ReceiptUsd = g.Where(p => p.Direction == PaymentDirection.In).Sum(p => (decimal?)p.AmountUsd) ?? 0m,
                PaymentUsd = g.Where(p => p.Direction == PaymentDirection.Out).Sum(p => (decimal?)p.AmountUsd) ?? 0m
            })
            .FirstOrDefaultAsync();

        // موجودی صندوق/بانک فقط از پرداخت‌هایی می‌آید که واقعاً حساب نقدیِ شرکت را تکان داده‌اند.
        // پرداختِ تأمین‌شده توسط شریک حساب نقدی ندارد و اینجا شمرده نمی‌شود.
        var cashBalanceUsd = await db.PaymentTransactions
            .AsNoTracking()
            .Where(p => p.CashAccountId != null)
            .GroupBy(_ => 1)
            .Select(g =>
                (g.Where(p => p.Direction == PaymentDirection.In).Sum(p => (decimal?)p.AmountUsd) ?? 0m)
                - (g.Where(p => p.Direction == PaymentDirection.Out).Sum(p => (decimal?)p.AmountUsd) ?? 0m))
            .FirstOrDefaultAsync();

        var transactionCount = await db.PaymentTransactions
            .AsNoTracking()
            .CountAsync();

        return new FinanceMetricCardsViewModel
        {
            AriaLabel = string.IsNullOrWhiteSpace(ariaLabel) ? "\u0622\u0645\u0627\u0631 \u0631\u0648\u0632\u0646\u0627\u0645\u0686\u0647 \u062f\u0631\u06cc\u0627\u0641\u062a \u0648 \u067e\u0631\u062f\u0627\u062e\u062a" : ariaLabel,
            TodayReceiptUsd = todayTotals?.ReceiptUsd ?? 0m,
            TodayPaymentUsd = todayTotals?.PaymentUsd ?? 0m,
            CashAccountsBalanceUsd = cashBalanceUsd,
            TransactionCount = transactionCount
        };
    }
}
