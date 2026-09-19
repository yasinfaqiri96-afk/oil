using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;

namespace PTGOilSystem.Web.Services.Reporting;

public sealed record CashAccountActivityTotals(
    int CashAccountId,
    decimal TotalIn,
    decimal TotalOut,
    decimal TotalInUsd,
    decimal TotalOutUsd);

public interface ICashPositionReader
{
    Task<IReadOnlyList<CashAccountActivityTotals>> ReadAccountTotalsAsync(CancellationToken ct = default);
}

/// <summary>
/// جمع ورود/خروجِ صندوق‌ها و بانک‌ها از روی روزنامچه — همان تجمیعی که صفحهٔ «روزنامچه و حواله‌ها»
/// برای «موجودی حساب‌های نقدی» نشان می‌دهد. فقط از <c>PaymentsController</c> به این مرجع مشترک
/// منتقل شد تا API موبایل فرمول موازی نسازد؛ هیچ قاعدهٔ پرداخت یا دفتر تغییر نکرده است.
/// </summary>
public sealed class CashPositionReader(ApplicationDbContext db) : ICashPositionReader
{
    public async Task<IReadOnlyList<CashAccountActivityTotals>> ReadAccountTotalsAsync(CancellationToken ct = default)
    {
        var rows = await db.PaymentTransactions
            .AsNoTracking()
            .Where(p => p.CashAccountId != null)
            .GroupBy(p => p.CashAccountId!.Value)
            .Select(g => new
            {
                CashAccountId = g.Key,
                TotalIn = g.Where(p => p.Direction == PaymentDirection.In).Sum(p => p.Amount),
                TotalOut = g.Where(p => p.Direction == PaymentDirection.Out).Sum(p => p.Amount),
                TotalInUsd = g.Where(p => p.Direction == PaymentDirection.In).Sum(p => p.AmountUsd),
                TotalOutUsd = g.Where(p => p.Direction == PaymentDirection.Out).Sum(p => p.AmountUsd)
            })
            .ToListAsync(ct);

        return rows
            .Select(r => new CashAccountActivityTotals(r.CashAccountId, r.TotalIn, r.TotalOut, r.TotalInUsd, r.TotalOutUsd))
            .ToList();
    }

    /// <summary>معادل دالریِ ثبت‌شده در هر سند (نه تبدیل با نرخ امروز).</summary>
    public static decimal TotalBalanceUsd(IEnumerable<CashAccountActivityTotals> totals)
        => totals.Sum(t => t.TotalInUsd - t.TotalOutUsd);
}
