using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.Reports;
using PTGOilSystem.Web.Services;

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

/// <summary>
/// سود/زیانِ ارزیِ محقق. تنها مرجعِ «تفاوت نرخ» در سودِ شرکت و سودِ قرارداد.
/// </summary>
public sealed record RealizedFxSnapshot(decimal GainUsd, decimal LossUsd)
{
    public decimal NetUsd => GainUsd - LossUsd;
    public static RealizedFxSnapshot Zero { get; } = new(0m, 0m);
}

/// <summary>
/// اثرِ ارزیِ یک قرارداد. کسریِ صراف (فقط مبلغِ قبول‌شده) جدا نگه داشته می‌شود چون گزارش
/// قرارداد آن را ستونِ جدا نشان می‌دهد؛ در سودِ شرکت همان مبلغ از راهِ سندِ مصرفِ تفاوت نرخ می‌آید.
/// </summary>
public sealed record ContractRealizedFxSnapshot(decimal GainUsd, decimal LossUsd, decimal SupplierShortfallUsd)
{
    public static ContractRealizedFxSnapshot Zero { get; } = new(0m, 0m, 0m);
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

    /// <summary>
    /// اثرِ ارزیِ محقق به تفکیکِ قرارداد: تفاوتِ تسویهٔ صراف، تفاوتِ نرخِ انتقالِ مانده و تخصیصِ
    /// پرداختِ تأمین‌کننده، و سودِ شناسایی‌شدهٔ تفاوتِ نرخِ تأمین‌کننده که روی قرارداد نشسته است.
    /// </summary>
    Task<IReadOnlyDictionary<int, ContractRealizedFxSnapshot>> BuildRealizedFxByContractAsync(
        IReadOnlyCollection<int> contractIds,
        CancellationToken ct = default);

    /// <summary>
    /// Realised P&amp;L for an explicit set of sales, grouped by an arbitrary caller key
    /// (shipment, contract journey, wagon…). The caller owns the lineage that maps a sale
    /// to its group; revenue and COGS still come only from this service.
    /// </summary>
    Task<IReadOnlyDictionary<int, SalesPnlSnapshot>> BuildForSaleGroupsAsync(
        IReadOnlyDictionary<int, int> saleIdToGroupKey,
        CancellationToken ct = default);

    /// <summary>
    /// اقتصادِ هر قرارداد: سودِ محقق (فقط بخشِ فروخته‌شده) و سودِ چرخهٔ کامل، هر دو از یک مبنا.
    /// تنها مرجعِ «سود قرارداد» برای گزارش، پروندهٔ قرارداد، صورت‌حساب شراکت و ثبتِ سهمِ شریک.
    /// </summary>
    Task<IReadOnlyDictionary<int, ContractEconomicsSnapshot>> BuildContractEconomicsAsync(
        IReadOnlyCollection<int> contractIds,
        CancellationToken ct = default);

    Task<SalesPnlSnapshot> BuildForSalesAsync(
        IReadOnlyCollection<int> salesTransactionIds,
        CancellationToken ct = default);
}

public sealed partial class ProfitAndLossService : IProfitAndLossService
{
    private readonly ApplicationDbContext _db;
    private readonly IPurchaseAggregationService _purchaseAggregation;
    private readonly ISaleContractAttributionReader _saleAttribution;

    public ProfitAndLossService(
        ApplicationDbContext db,
        IPurchaseAggregationService? purchaseAggregation = null,
        ISaleContractAttributionReader? saleAttribution = null)
    {
        _db = db;
        _purchaseAggregation = purchaseAggregation ?? new PurchaseAggregationService(db);
        _saleAttribution = saleAttribution ?? new SaleContractAttributionReader(db);
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

    public async Task<IReadOnlyDictionary<int, SalesPnlSnapshot>> BuildForSaleGroupsAsync(
        IReadOnlyDictionary<int, int> saleIdToGroupKey,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(saleIdToGroupKey);
        var requestedSaleIds = saleIdToGroupKey.Keys.Where(id => id > 0).Distinct().ToArray();
        if (requestedSaleIds.Length == 0)
        {
            return new Dictionary<int, SalesPnlSnapshot>();
        }

        var (sales, costBySale) = await LoadSalesAndCostsAsync(requestedSaleIds, ct);
        return sales
            .Where(s => saleIdToGroupKey.ContainsKey(s.Id))
            .GroupBy(s => saleIdToGroupKey[s.Id])
            .ToDictionary(
                g => g.Key,
                g => BuildSalesSnapshot(g.Select(s => (s.Id, s.TotalUsd)), costBySale));
    }

    public async Task<SalesPnlSnapshot> BuildForSalesAsync(
        IReadOnlyCollection<int> salesTransactionIds,
        CancellationToken ct = default)
    {
        var ids = salesTransactionIds.Where(id => id > 0).Distinct().ToArray();
        if (ids.Length == 0)
        {
            return BuildSalesSnapshot([], new Dictionary<int, decimal>());
        }

        var (sales, costBySale) = await LoadSalesAndCostsAsync(ids, ct);
        return BuildSalesSnapshot(sales.Select(s => (s.Id, s.TotalUsd)), costBySale);
    }

    /// <summary>
    /// Loads non-cancelled revenue rows plus their active cost snapshots for an explicit id set.
    /// Cancelled sales and reversed consumption rows are dropped here so no caller can re-add them.
    /// </summary>
    private async Task<(List<SaleRevenueRow> Sales, Dictionary<int, decimal> CostBySale)> LoadSalesAndCostsAsync(
        int[] saleIds,
        CancellationToken ct)
    {
        var sales = await _db.SalesTransactions.AsNoTracking()
            .Where(s => !s.IsCancelled && saleIds.Contains(s.Id))
            .Select(s => new SaleRevenueRow(s.Id, s.TotalUsd))
            .ToListAsync(ct);

        if (sales.Count == 0)
        {
            return (sales, new Dictionary<int, decimal>());
        }

        var presentSaleIds = sales.Select(s => s.Id).ToArray();
        var costs = await _db.SalesCostConsumptions.AsNoTracking()
            .Where(c => c.Status == SalesCostConsumptionStatus.Active
                && presentSaleIds.Contains(c.SalesTransactionId))
            .GroupBy(c => c.SalesTransactionId)
            .Select(g => new { SaleId = g.Key, CostUsd = g.Sum(c => c.CostUsd) })
            .ToListAsync(ct);

        return (sales, costs.ToDictionary(c => c.SaleId, c => c.CostUsd));
    }

    private sealed record SaleRevenueRow(int Id, decimal TotalUsd);

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

        // مصرفی که بر عهدهٔ طرفِ بیرونیِ قرارداد است از سودِ شرکت کم نمی‌شود (CostResponsibilityPolicy).
        var expenseQuery = _db.ExpenseTransactions.AsNoTracking()
            .Where(e => !e.IsCancelled)
            .Where(CostResponsibilityPolicy.IsCompanyBorne);
        if (filter.FromDate.HasValue) expenseQuery = expenseQuery.Where(e => e.ExpenseDate >= filter.FromDate.Value.Date);
        if (filter.ToDate.HasValue) expenseQuery = expenseQuery.Where(e => e.ExpenseDate < filter.ToDate.Value.Date.AddDays(1));
        if (filter.ContractId.HasValue) expenseQuery = expenseQuery.Where(e => e.ContractId == filter.ContractId.Value);
        // سندِ مصرفِ «تفاوت نرخ» زیانِ ارزی است، نه مصرفِ عملیاتی؛ در ستونِ ارز می‌نشیند تا سود و
        // زیانِ ارزی کنار هم دیده شوند. مبلغ همان است، پس سودِ خالص از این جابه‌جایی تغییر نمی‌کند.
        var fxExpenseTypeIds = await FxDifferenceExpenseTypeIds().ToListAsync(ct);
        var expenseUsd = await expenseQuery
            .Where(e => !fxExpenseTypeIds.Contains(e.ExpenseTypeId))
            .SumAsync(e => (decimal?)e.AmountUsd, ct) ?? 0m;
        var fxExpenseLossUsd = fxExpenseTypeIds.Count == 0
            ? 0m
            : await expenseQuery
                .Where(e => fxExpenseTypeIds.Contains(e.ExpenseTypeId))
                .SumAsync(e => (decimal?)e.AmountUsd, ct) ?? 0m;

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

        var ledgerQuery = _db.LedgerEntries.AsNoTracking().Where(l => FxLedgerSourceTypes.Contains(l.SourceType));
        if (filter.FromDate.HasValue) ledgerQuery = ledgerQuery.Where(l => l.EntryDate >= filter.FromDate.Value.Date);
        if (filter.ToDate.HasValue) ledgerQuery = ledgerQuery.Where(l => l.EntryDate < filter.ToDate.Value.Date.AddDays(1));
        if (filter.ContractId.HasValue) ledgerQuery = ledgerQuery.Where(l => l.ContractId == filter.ContractId.Value);
        var ledgerFx = ToFx(await ledgerQuery
            .GroupBy(l => new { l.SourceType, l.Side })
            .Select(g => new FxLedgerRow(null, g.Key.SourceType, g.Key.Side, g.Sum(l => l.AmountUsd)))
            .ToListAsync(ct));

        var salesSnapshot = BuildSalesSnapshot(sales.Select(s => (s.Id, s.TotalUsd)), costBySale);
        return new CompanyPnlSnapshot(
            salesSnapshot,
            expenseUsd,
            Math.Abs(fx?.GainUsd ?? 0m) + ledgerFx.GainUsd,
            Math.Abs(fx?.LossUsd ?? 0m) + ledgerFx.LossUsd + fxExpenseLossUsd);
    }

    public async Task<IReadOnlyDictionary<int, ContractRealizedFxSnapshot>> BuildRealizedFxByContractAsync(
        IReadOnlyCollection<int> contractIds,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(contractIds);
        var ids = contractIds.Distinct().ToArray();
        var result = ids.ToDictionary(id => id, _ => ContractRealizedFxSnapshot.Zero);
        if (ids.Length == 0)
        {
            return result;
        }

        var settlements = await _db.SarrafSettlements.AsNoTracking()
            .Where(s => s.Status == SarrafSettlementStatus.Posted
                && s.ContractId != null
                && ids.Contains(s.ContractId.Value))
            .GroupBy(s => new { ContractId = s.ContractId!.Value, s.DifferenceType })
            .Select(g => new { g.Key.ContractId, g.Key.DifferenceType, AmountUsd = g.Sum(s => Math.Abs(s.DifferenceAmountUsd)) })
            .ToListAsync(ct);
        var ledgerRows = await _db.LedgerEntries.AsNoTracking()
            .Where(l => FxLedgerSourceTypes.Contains(l.SourceType)
                && l.ContractId != null
                && ids.Contains(l.ContractId.Value))
            .GroupBy(l => new { ContractId = l.ContractId!.Value, l.SourceType, l.Side })
            .Select(g => new FxLedgerRow(g.Key.ContractId, g.Key.SourceType, g.Key.Side, g.Sum(l => l.AmountUsd)))
            .ToListAsync(ct);

        foreach (var id in ids)
        {
            var ledger = ToFx(ledgerRows.Where(r => r.ContractId == id));
            var contractSettlements = settlements.Where(s => s.ContractId == id).ToList();
            result[id] = new ContractRealizedFxSnapshot(
                contractSettlements.Where(s => s.DifferenceType == SarrafSettlementDifferenceType.Gain).Sum(s => s.AmountUsd) + ledger.GainUsd,
                contractSettlements.Where(s => s.DifferenceType == SarrafSettlementDifferenceType.Loss).Sum(s => s.AmountUsd) + ledger.LossUsd,
                contractSettlements.Where(s => s.DifferenceType == SarrafSettlementDifferenceType.SupplierShortfall).Sum(s => s.AmountUsd));
        }

        return result;
    }

    /// <summary>
    /// سطرهای دفتر که سود/زیانِ ارزیِ محقق را حمل می‌کنند و در هیچ جای دیگرِ سود شمرده نمی‌شوند:
    /// سودِ شناسایی‌شدهٔ تفاوت نرخِ تأمین‌کننده (زیانِ همان رویداد سندِ مصرف است) و تفاوتِ نرخِ
    /// انتقالِ مانده و تخصیصِ پرداختِ تأمین‌کننده (زیان = بدهکار، سود = بستانکار؛ برگشت سمت را
    /// برعکس می‌کند). سودِ صرافِ «SarrafFxGain» عمداً اینجا نیست: همان رویداد از جدولِ تسویهٔ صراف
    /// شمرده می‌شود و دوباره‌شماری می‌شد.
    /// </summary>
    private static readonly string[] FxLedgerSourceTypes =
    [
        SupplierFxRecognitionService.GainLedgerSourceType,
        SupplierBalanceTransferService.ExchangeDifferenceLedgerSourceType,
        SupplierBalanceTransferService.ExchangeDifferenceReversalLedgerSourceType,
        SupplierPaymentAllocationService.ExchangeDifferenceLedgerSourceType,
        SupplierPaymentAllocationService.ExchangeDifferenceReversalLedgerSourceType
    ];

    private static readonly HashSet<string> FxReversalSourceTypes =
    [
        SupplierBalanceTransferService.ExchangeDifferenceReversalLedgerSourceType,
        SupplierPaymentAllocationService.ExchangeDifferenceReversalLedgerSourceType
    ];

    private sealed record FxLedgerRow(int? ContractId, string SourceType, LedgerSide Side, decimal AmountUsd);

    private static RealizedFxSnapshot ToFx(IEnumerable<FxLedgerRow> rows)
    {
        var gain = 0m;
        var loss = 0m;
        foreach (var row in rows)
        {
            var amount = Math.Abs(row.AmountUsd);
            var isReversal = FxReversalSourceTypes.Contains(row.SourceType);
            // سندِ اصلی: بستانکار = سود، بدهکار = زیان. برگشت همان را با سمتِ مخالف خنثی می‌کند.
            if (row.Side == LedgerSide.Credit)
            {
                if (isReversal) loss -= amount; else gain += amount;
            }
            else
            {
                if (isReversal) gain -= amount; else loss += amount;
            }
        }

        return new RealizedFxSnapshot(gain, loss);
    }

    /// <summary>نوعِ مصرفِ «تفاوت نرخ»؛ همان قاعدهٔ یافتنِ نوع در روزنامچه و شناساییِ تفاوت نرخ.</summary>
    private IQueryable<int> FxDifferenceExpenseTypeIds()
        => _db.ExpenseTypes.AsNoTracking()
            .Where(t => t.Category == "FxDifference" || t.Code == SupplierFxRecognitionService.FxDifferenceExpenseCode)
            .Select(t => t.Id);

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
