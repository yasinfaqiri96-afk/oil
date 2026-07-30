using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.Reports;

namespace PTGOilSystem.Web.Services.Reporting;

public enum PnlConfidence
{
    Verified = 1,
    Estimated = 2,
    Legacy = 3,
    NeedsReview = 4
}

public sealed record SalesPnlSnapshot(
    decimal RevenueUsd,
    decimal CostOfGoodsSoldUsd,
    int SaleCount,
    int CostedSaleCount,
    int UncostedSaleCount,
    PnlConfidence Confidence)
{
    public decimal GrossProfitUsd => PnlMath.GrossProfit(RevenueUsd, CostOfGoodsSoldUsd);
}

public sealed record CompanyPnlSnapshot(
    SalesPnlSnapshot Sales,
    decimal OperatingExpenseUsd,
    decimal ExchangeGainUsd,
    decimal ExchangeLossUsd)
{
    public decimal GrossProfitUsd => Sales.GrossProfitUsd;
    public decimal NetProfitUsd => PnlMath.NetProfit(
        Sales.RevenueUsd,
        Sales.CostOfGoodsSoldUsd,
        OperatingExpenseUsd,
        ExchangeGainUsd,
        ExchangeLossUsd);
}

public static class PnlMath
{
    public static decimal GrossProfit(decimal revenueUsd, decimal costOfGoodsSoldUsd)
        => decimal.Round(revenueUsd - costOfGoodsSoldUsd, 4, MidpointRounding.AwayFromZero);

    public static decimal NetProfit(
        decimal revenueUsd,
        decimal costOfGoodsSoldUsd,
        decimal operatingExpenseUsd,
        decimal exchangeGainUsd = 0m,
        decimal exchangeLossUsd = 0m)
        => decimal.Round(
            revenueUsd
            - costOfGoodsSoldUsd
            - operatingExpenseUsd
            + exchangeGainUsd
            - exchangeLossUsd,
            4,
            MidpointRounding.AwayFromZero);
}

/// <summary>
/// Authoritative read engine for realised sales P&amp;L.
/// Revenue comes from non-cancelled <see cref="SalesTransaction"/> rows and COGS
/// comes only from active <see cref="SalesCostConsumption"/> snapshots. Missing
/// consumption is surfaced as <see cref="PnlConfidence.NeedsReview"/>; it is never
/// replaced with a guessed current price.
/// </summary>
public interface IProfitAndLossService
{
    Task<IReadOnlyDictionary<int, SalesPnlSnapshot>> BuildForSaleContractsAsync(
        IReadOnlyCollection<int> saleContractIds,
        CancellationToken ct = default);

    Task<CompanyPnlSnapshot> BuildCompanyAsync(
        ManagementReportFilterViewModel filter,
        CancellationToken ct = default);
}

public sealed class ProfitAndLossService : IProfitAndLossService
{
    private readonly ApplicationDbContext _db;

    public ProfitAndLossService(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyDictionary<int, SalesPnlSnapshot>> BuildForSaleContractsAsync(
        IReadOnlyCollection<int> saleContractIds,
        CancellationToken ct = default)
    {
        var ids = saleContractIds.Where(id => id > 0).Distinct().ToArray();
        if (ids.Length == 0)
        {
            return new Dictionary<int, SalesPnlSnapshot>();
        }

        var sales = await _db.SalesTransactions.AsNoTracking()
            .Where(s => !s.IsCancelled
                && s.ContractId.HasValue
                && ids.Contains(s.ContractId.Value))
            .Select(s => new { s.Id, ContractId = s.ContractId!.Value, s.TotalUsd })
            .ToListAsync(ct);

        var saleIds = sales.Select(s => s.Id).ToArray();
        var costs = saleIds.Length == 0
            ? []
            : await _db.SalesCostConsumptions.AsNoTracking()
                .Where(c => c.Status == SalesCostConsumptionStatus.Active
                    && saleIds.Contains(c.SalesTransactionId))
                .GroupBy(c => c.SalesTransactionId)
                .Select(g => new { SaleId = g.Key, CostUsd = g.Sum(c => c.CostUsd) })
                .ToListAsync(ct);

        var costBySale = costs.ToDictionary(c => c.SaleId, c => c.CostUsd);
        return sales
            .GroupBy(s => s.ContractId)
            .ToDictionary(
                g => g.Key,
                g => BuildSalesSnapshot(g.Select(s => (s.Id, s.TotalUsd)), costBySale));
    }

    public async Task<CompanyPnlSnapshot> BuildCompanyAsync(
        ManagementReportFilterViewModel filter,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var salesQuery = _db.SalesTransactions.AsNoTracking().Where(s => !s.IsCancelled);
        if (filter.FromDate.HasValue) salesQuery = salesQuery.Where(s => s.SaleDate >= filter.FromDate.Value.Date);
        if (filter.ToDate.HasValue) salesQuery = salesQuery.Where(s => s.SaleDate < filter.ToDate.Value.Date.AddDays(1));
        if (filter.ProductId.HasValue) salesQuery = salesQuery.Where(s => s.ProductId == filter.ProductId.Value);
        if (filter.ContractId.HasValue) salesQuery = salesQuery.Where(s => s.ContractId == filter.ContractId.Value);
        if (filter.CustomerId.HasValue) salesQuery = salesQuery.Where(s => s.CustomerId == filter.CustomerId.Value);

        var sales = await salesQuery
            .Select(s => new { s.Id, s.TotalUsd })
            .ToListAsync(ct);
        var saleIds = sales.Select(s => s.Id).ToArray();
        var costs = saleIds.Length == 0
            ? []
            : await _db.SalesCostConsumptions.AsNoTracking()
                .Where(c => c.Status == SalesCostConsumptionStatus.Active
                    && saleIds.Contains(c.SalesTransactionId))
                .GroupBy(c => c.SalesTransactionId)
                .Select(g => new { SaleId = g.Key, CostUsd = g.Sum(c => c.CostUsd) })
                .ToListAsync(ct);
        var costBySale = costs.ToDictionary(c => c.SaleId, c => c.CostUsd);

        var expenseQuery = _db.ExpenseTransactions.AsNoTracking().Where(e => !e.IsCancelled);
        if (filter.FromDate.HasValue) expenseQuery = expenseQuery.Where(e => e.ExpenseDate >= filter.FromDate.Value.Date);
        if (filter.ToDate.HasValue) expenseQuery = expenseQuery.Where(e => e.ExpenseDate < filter.ToDate.Value.Date.AddDays(1));
        if (filter.ContractId.HasValue) expenseQuery = expenseQuery.Where(e => e.ContractId == filter.ContractId.Value);
        var expenseUsd = await expenseQuery.SumAsync(e => (decimal?)e.AmountUsd, ct) ?? 0m;

        var fxQuery = _db.SarrafSettlements.AsNoTracking()
            .Where(s => s.Status == SarrafSettlementStatus.Posted);
        if (filter.FromDate.HasValue) fxQuery = fxQuery.Where(s => s.SettlementDate >= filter.FromDate.Value.Date);
        if (filter.ToDate.HasValue) fxQuery = fxQuery.Where(s => s.SettlementDate < filter.ToDate.Value.Date.AddDays(1));
        if (filter.ContractId.HasValue) fxQuery = fxQuery.Where(s => s.ContractId == filter.ContractId.Value);
        var fx = await fxQuery
            .GroupBy(_ => 1)
            .Select(g => new
            {
                GainUsd = g.Where(s => s.DifferenceType == SarrafSettlementDifferenceType.Gain)
                    .Sum(s => (decimal?)s.DifferenceAmountUsd) ?? 0m,
                LossUsd = g.Where(s => s.DifferenceType == SarrafSettlementDifferenceType.Loss)
                    .Sum(s => (decimal?)s.DifferenceAmountUsd) ?? 0m
            })
            .FirstOrDefaultAsync(ct);

        var salesSnapshot = BuildSalesSnapshot(sales.Select(s => (s.Id, s.TotalUsd)), costBySale);
        return new CompanyPnlSnapshot(
            salesSnapshot,
            expenseUsd,
            Math.Abs(fx?.GainUsd ?? 0m),
            Math.Abs(fx?.LossUsd ?? 0m));
    }

    private static SalesPnlSnapshot BuildSalesSnapshot(
        IEnumerable<(int Id, decimal RevenueUsd)> sales,
        IReadOnlyDictionary<int, decimal> costBySale)
    {
        var rows = sales.ToList();
        var costedSaleCount = rows.Count(s => costBySale.ContainsKey(s.Id));
        var uncostedSaleCount = rows.Count - costedSaleCount;
        var confidence = uncostedSaleCount > 0
            ? PnlConfidence.NeedsReview
            : PnlConfidence.Verified;

        return new SalesPnlSnapshot(
            RevenueUsd: rows.Sum(s => s.RevenueUsd),
            CostOfGoodsSoldUsd: rows.Sum(s => costBySale.GetValueOrDefault(s.Id)),
            SaleCount: rows.Count,
            CostedSaleCount: costedSaleCount,
            UncostedSaleCount: uncostedSaleCount,
            Confidence: confidence);
    }
}
