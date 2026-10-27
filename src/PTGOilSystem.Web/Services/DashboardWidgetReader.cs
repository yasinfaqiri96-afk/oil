using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Helpers;
using PTGOilSystem.Web.Models;
using PTGOilSystem.Web.Models.Reports;
using PTGOilSystem.Web.Services.Reporting;
using PTGOilSystem.Web.Services.Time;

namespace PTGOilSystem.Web.Services;

/// <summary>
/// ابزارک‌های اختیاری داشبورد که فقط پس از انتخاب کاربر جدا بارگذاری می‌شوند تا بار صفحهٔ اصلی
/// بالا نرود. هیچ فرمول مانده، سود یا نرخی اینجا ساخته نمی‌شود؛ هر عدد از مرجع رسمی خودش می‌آید:
/// <list type="bullet">
///   <item>سود و زیان امروز: <see cref="IProfitAndLossService.BuildCompanyPeriodAsync"/> (همان مفاد امروزِ موبایل)</item>
///   <item>ماندهٔ صندوق و بانک: <see cref="ICashPositionReader"/></item>
///   <item>نرخ ارز: آخرین <see cref="Models.Entities.DailyFxRate"/> هر جفت ارز</item>
///   <item>مصارف: آخرین مصارف لغونشده، همان قاعدهٔ «آخرین فعالیت‌ها»</item>
/// </list>
/// </summary>
public interface IDashboardWidgetReader
{
    Task<DashboardProfitWidgetViewModel> ReadTodayProfitAsync(CancellationToken ct = default);
    Task<List<DashboardWidgetRowViewModel>> ReadCashAccountsAsync(CancellationToken ct = default);
    Task<List<DashboardWidgetRowViewModel>> ReadFxRatesAsync(CancellationToken ct = default);
    Task<List<DashboardWidgetRowViewModel>> ReadRecentExpensesAsync(CancellationToken ct = default);
}

public sealed class DashboardWidgetReader(
    ApplicationDbContext db,
    IProfitAndLossService profitAndLoss,
    ICashPositionReader cashPosition,
    IAfghanistanBusinessClock clock) : IDashboardWidgetReader
{
    private const int RowLimit = 5;
    // نرخ‌ها روزانه ثبت می‌شوند؛ آخرین ردیف‌ها برای پیدا کردن نرخ تازهٔ هر جفت ارز کافی است.
    private const int FxScanLimit = 200;

    public async Task<DashboardProfitWidgetViewModel> ReadTodayProfitAsync(CancellationToken ct = default)
    {
        var today = clock.Today.Date;
        var period = await profitAndLoss.BuildCompanyPeriodAsync(
            new ManagementReportFilterViewModel { FromDate = today, ToDate = today },
            today,
            ct: ct);

        return new DashboardProfitWidgetViewModel
        {
            BusinessDate = today,
            RevenueUsd = period.SalesRevenueUsd,
            CostOfSalesUsd = period.CostOfSalesUsd,
            PeriodExpensesUsd = period.PeriodExpensesUsd,
            NetProfitUsd = period.NetProfitUsd,
            SaleCount = period.SaleCount,
            UncostedSaleCount = period.UncostedSaleCount
        };
    }

    public async Task<List<DashboardWidgetRowViewModel>> ReadCashAccountsAsync(CancellationToken ct = default)
    {
        var totals = (await cashPosition.ReadAccountTotalsAsync(ct))
            .OrderByDescending(t => Math.Abs(t.BalanceUsd))
            .Take(RowLimit)
            .ToList();
        var ids = totals.Select(t => t.CashAccountId).ToArray();
        var accounts = await db.CashAccounts.AsNoTracking()
            .Where(a => ids.Contains(a.Id))
            .Select(a => new { a.Id, a.Code, a.Name, a.BankName })
            .ToDictionaryAsync(a => a.Id, ct);

        return totals
            .Select(t =>
            {
                accounts.TryGetValue(t.CashAccountId, out var account);
                return new DashboardWidgetRowViewModel
                {
                    Title = account?.Name ?? $"#{t.CashAccountId}",
                    Subtitle = account is null ? "" : string.IsNullOrWhiteSpace(account.BankName) ? account.Code : account.BankName!,
                    Value = t.Balance,
                    Decimals = 0,
                    Unit = t.TotalsCurrency,
                    Controller = "CashAccounts",
                    Action = "Details",
                    RouteId = t.CashAccountId
                };
            })
            .ToList();
    }

    public async Task<List<DashboardWidgetRowViewModel>> ReadFxRatesAsync(CancellationToken ct = default)
    {
        var recent = await db.DailyFxRates.AsNoTracking()
            .OrderByDescending(r => r.RateDate)
            .ThenByDescending(r => r.Id)
            .Select(r => new { r.Id, r.BaseCurrency, r.QuoteCurrency, r.RateDate, r.Rate })
            .Take(FxScanLimit)
            .ToListAsync(ct);

        return recent
            .GroupBy(r => (r.BaseCurrency, r.QuoteCurrency))
            .Select(g => g.First())
            .Take(RowLimit)
            .Select(r => new DashboardWidgetRowViewModel
            {
                Title = $"{r.BaseCurrency} / {r.QuoteCurrency}",
                Subtitle = r.RateDate.ToDisplayDate(),
                Value = r.Rate,
                Decimals = 2,
                Controller = "DailyFxRates",
                Action = "Details",
                RouteId = r.Id
            })
            .ToList();
    }

    public async Task<List<DashboardWidgetRowViewModel>> ReadRecentExpensesAsync(CancellationToken ct = default)
    {
        var expenses = await db.ExpenseTransactions.AsNoTracking()
            .Where(e => !e.IsCancelled)
            .OrderByDescending(e => e.ExpenseDate)
            .ThenByDescending(e => e.Id)
            .Select(e => new
            {
                e.Id,
                e.ExpenseDate,
                e.AmountUsd,
                TypeName = e.ExpenseType == null ? "" : e.ExpenseType.Name
            })
            .Take(RowLimit)
            .ToListAsync(ct);

        return expenses
            .Select(e => new DashboardWidgetRowViewModel
            {
                Title = string.IsNullOrWhiteSpace(e.TypeName) ? $"#{e.Id}" : e.TypeName,
                Subtitle = e.ExpenseDate.ToDisplayDate(),
                Value = e.AmountUsd,
                Decimals = 0,
                Unit = "USD",
                Controller = "Expenses",
                Action = "Details",
                RouteId = e.Id
            })
            .ToList();
    }
}
