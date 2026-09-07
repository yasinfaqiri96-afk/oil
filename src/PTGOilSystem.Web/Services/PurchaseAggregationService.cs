using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;

namespace PTGOilSystem.Web.Services;

/// <summary>
/// Default implementation of <see cref="IPurchaseAggregationService"/>.
///
/// Phase C (no migration): every current LoadingRegister row is treated as
/// a purchase loading, exactly as before. The service centralizes the
/// arithmetic that used to live in controllers:
///
///   - <c>HasValidLoadingPrice</c> is the canonical price guard.
///   - Missing LoadingPriceUsd falls back to the caller-provided contract
///     final price.
///   - Traceable purchase cost is rounded per LoadingRegister at 4 decimal
///     places with <c>MidpointRounding.AwayFromZero</c>, matching the
///     ContractJourney mini P&amp;L behavior.
/// </summary>
public sealed class PurchaseAggregationService : IPurchaseAggregationService
{
    private readonly ApplicationDbContext _db;

    public PurchaseAggregationService(ApplicationDbContext db) => _db = db;

    private static decimal? ResolveEffectivePrice(
        decimal? loadingPriceUsd,
        decimal? contractFinalPriceUsd)
        => IPurchaseAggregationService.HasValidLoadingPrice(loadingPriceUsd)
            ? loadingPriceUsd
            : contractFinalPriceUsd;

    private static PurchaseAggregationSnapshot BuildSnapshot(
        int contractId,
        IEnumerable<LoadingRegisterRow> rows,
        decimal? contractFinalPriceUsd,
        IReadOnlySet<int>? loadingRegisterIdsWithOfficialExpenses = null,
        IReadOnlySet<int>? loadingRegisterIdsWithExpenseLines = null)
    {
        decimal totalLoaded = 0m;
        decimal pricedLoaded = 0m;
        decimal pendingLoaded = 0m;
        int pendingCount = 0;
        decimal traceableCost = 0m;
        decimal transport = 0m;
        decimal warehouse = 0m;
        decimal other = 0m;
        decimal railway = 0m;
        decimal railwayFromLines = 0m;

        foreach (var row in rows)
        {
            totalLoaded += row.LoadedQuantityMt;

            // Row-based loadings keep their inline (now mirrored from "None"
            // expense lines) money fields ALWAYS counted: their fixed fields never
            // overlap with the official ServiceProvider ExpenseTransactions, so the
            // old all-or-nothing official guard would only drop amounts that exist
            // nowhere else. Legacy loadings (no expense lines) keep the old guard.
            var isLineBased = loadingRegisterIdsWithExpenseLines is not null
                && loadingRegisterIdsWithExpenseLines.Contains(row.Id);
            var dropFixedFields = !isLineBased
                && loadingRegisterIdsWithOfficialExpenses is not null
                && loadingRegisterIdsWithOfficialExpenses.Contains(row.Id);

            if (!dropFixedFields)
            {
                transport += row.TransportExpenseUsd ?? 0m;
                warehouse += row.WarehouseExpenseUsd ?? 0m;
                other += row.OtherExpenseUsd ?? 0m;
                railway += row.RailwayExpenseUsd ?? 0m;
                if (isLineBased)
                {
                    railwayFromLines += row.RailwayExpenseUsd ?? 0m;
                }
            }

            var effective = ResolveEffectivePrice(row.LoadingPriceUsd, contractFinalPriceUsd);
            if (IPurchaseAggregationService.HasValidLoadingPrice(effective))
            {
                pricedLoaded += row.LoadedQuantityMt;
                traceableCost += decimal.Round(
                    row.LoadedQuantityMt * effective!.Value,
                    4,
                    MidpointRounding.AwayFromZero);
            }
            else
            {
                pendingLoaded += row.LoadedQuantityMt;
                pendingCount += 1;
            }
        }

        decimal? weightedAverage = pricedLoaded > 0m
            ? decimal.Round(traceableCost / pricedLoaded, 4, MidpointRounding.AwayFromZero)
            : null;

        return new PurchaseAggregationSnapshot(
            ContractId: contractId,
            TotalLoadedQuantityMt: totalLoaded,
            PricedPurchaseQuantityMt: pricedLoaded,
            PendingPurchaseQuantityMt: pendingLoaded,
            PendingLoadingCount: pendingCount,
            TraceablePurchaseCostUsd: traceableCost,
            WeightedAveragePurchasePriceUsd: weightedAverage,
            LoadingTransportExpenseUsd: transport,
            LoadingWarehouseExpenseUsd: warehouse,
            LoadingOtherExpenseUsd: other,
            LoadingRailwayExpenseUsd: railway,
            LoadingRailwayExpenseUsdFromLines: railwayFromLines);
    }

    public async Task<PurchaseAggregationSnapshot> AggregateForContractAsync(
        int contractId,
        decimal? contractFinalPriceUsd,
        CancellationToken ct = default)
    {
        var rows = await _db.LoadingRegisters
            .AsNoTracking()
            .Where(lr => lr.ContractId == contractId)
            .Select(lr => new LoadingRegisterRow(
                lr.Id,
                lr.ContractId,
                lr.LoadedQuantityMt,
                lr.LoadingPriceUsd,
                lr.TransportExpenseUsd,
                lr.WarehouseExpenseUsd,
                lr.OtherExpenseUsd,
                lr.RailwayExpenseUsd))
            .ToListAsync(ct);

        var loadingIds = rows.Select(r => r.Id).ToList();
        var loadingIdsWithOfficialExpenses = await LoadLoadingIdsWithOfficialExpensesAsync(loadingIds, ct);
        var loadingIdsWithLines = await LoadLoadingIdsWithExpenseLinesAsync(loadingIds, ct);

        return BuildSnapshot(contractId, rows, contractFinalPriceUsd, loadingIdsWithOfficialExpenses, loadingIdsWithLines);
    }

    public async Task<IReadOnlyDictionary<int, PurchaseAggregationSnapshot>> AggregateForContractsAsync(
        IReadOnlyCollection<int> contractIds,
        IReadOnlyDictionary<int, decimal?> contractFinalPriceById,
        CancellationToken ct = default)
    {
        if (contractIds is null || contractIds.Count == 0)
        {
            return new Dictionary<int, PurchaseAggregationSnapshot>();
        }

        var rows = await _db.LoadingRegisters
            .AsNoTracking()
            .Where(lr => contractIds.Contains(lr.ContractId))
            .Select(lr => new LoadingRegisterRow(
                lr.Id,
                lr.ContractId,
                lr.LoadedQuantityMt,
                lr.LoadingPriceUsd,
                lr.TransportExpenseUsd,
                lr.WarehouseExpenseUsd,
                lr.OtherExpenseUsd,
                lr.RailwayExpenseUsd))
            .ToListAsync(ct);

        var loadingIds = rows.Select(r => r.Id).ToList();
        var loadingIdsWithOfficialExpenses = await LoadLoadingIdsWithOfficialExpensesAsync(loadingIds, ct);
        var loadingIdsWithLines = await LoadLoadingIdsWithExpenseLinesAsync(loadingIds, ct);

        var grouped = rows
            .GroupBy(r => r.ContractId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var result = new Dictionary<int, PurchaseAggregationSnapshot>(contractIds.Count);
        foreach (var contractId in contractIds)
        {
            contractFinalPriceById.TryGetValue(contractId, out var finalPrice);
            var contractRows = grouped.TryGetValue(contractId, out var list)
                ? list
                : new List<LoadingRegisterRow>();
            result[contractId] = BuildSnapshot(contractId, contractRows, finalPrice, loadingIdsWithOfficialExpenses, loadingIdsWithLines);
        }

        return result;
    }

    public async Task<IReadOnlyDictionary<int, decimal>> GetLoadedQuantityByContractAsync(
        CancellationToken ct = default)
    {
        var rows = await _db.LoadingRegisters
            .AsNoTracking()
            .GroupBy(lr => lr.ContractId)
            .Select(g => new { ContractId = g.Key, Quantity = g.Sum(lr => lr.LoadedQuantityMt) })
            .ToListAsync(ct);

        return rows.ToDictionary(r => r.ContractId, r => r.Quantity);
    }

    public PurchaseAggregationSnapshot AggregateForLoadedRegisters(
        int contractId,
        IEnumerable<LoadingRegister> loadingRegisters,
        decimal? contractFinalPriceUsd,
        IReadOnlySet<int>? loadingRegisterIdsWithOfficialExpenses = null,
        IReadOnlySet<int>? loadingRegisterIdsWithExpenseLines = null)
    {
        ArgumentNullException.ThrowIfNull(loadingRegisters);

        var projected = loadingRegisters
            .Where(lr => lr.ContractId == contractId)
            .Select(lr => new LoadingRegisterRow(
                lr.Id,
                lr.ContractId,
                lr.LoadedQuantityMt,
                lr.LoadingPriceUsd,
                lr.TransportExpenseUsd,
                lr.WarehouseExpenseUsd,
                lr.OtherExpenseUsd,
                lr.RailwayExpenseUsd));

        return BuildSnapshot(
            contractId,
            projected,
            contractFinalPriceUsd,
            loadingRegisterIdsWithOfficialExpenses,
            loadingRegisterIdsWithExpenseLines);
    }

    private async Task<HashSet<int>> LoadLoadingIdsWithOfficialExpensesAsync(
        IReadOnlyCollection<int> loadingRegisterIds,
        CancellationToken ct)
    {
        if (loadingRegisterIds.Count == 0)
        {
            return [];
        }

        // مصرفِ ساخته‌شده از اظهارنامهٔ گمرکی هم LoadingRegisterId می‌گیرد، ولی هیچ‌وقت
        // آینهٔ فیلدهای درون‌خطیِ حمل/گدام/سایر/خط‌آهن نیست. اگر اینجا شمرده شود، وجودِ یک
        // اظهارنامه باعث می‌شود آن مصارفِ واقعی از گزارش حذف شوند و مصرف کمتر از واقع بیاید.
        var ids = await _db.ExpenseTransactions
            .AsNoTracking()
            .Where(e => !e.IsCancelled
                && !e.CustomsDeclarationId.HasValue
                && e.LoadingRegisterId.HasValue
                && loadingRegisterIds.Contains(e.LoadingRegisterId.Value))
            .Select(e => e.LoadingRegisterId!.Value)
            .Distinct()
            .ToListAsync(ct);

        return ids.ToHashSet();
    }

    private async Task<HashSet<int>> LoadLoadingIdsWithExpenseLinesAsync(
        IReadOnlyCollection<int> loadingRegisterIds,
        CancellationToken ct)
    {
        if (loadingRegisterIds.Count == 0)
        {
            return [];
        }

        var ids = await _db.LoadingExpenseLines
            .AsNoTracking()
            .Where(l => loadingRegisterIds.Contains(l.LoadingRegisterId))
            .Select(l => l.LoadingRegisterId)
            .Distinct()
            .ToListAsync(ct);

        return ids.ToHashSet();
    }

    private readonly record struct LoadingRegisterRow(
        int Id,
        int ContractId,
        decimal LoadedQuantityMt,
        decimal? LoadingPriceUsd,
        decimal? TransportExpenseUsd,
        decimal? WarehouseExpenseUsd,
        decimal? OtherExpenseUsd,
        decimal? RailwayExpenseUsd);
}
