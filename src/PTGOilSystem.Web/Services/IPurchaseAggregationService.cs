using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PTGOilSystem.Web.Models.Entities;

namespace PTGOilSystem.Web.Services;

/// <summary>
/// Centralized purchase-side aggregation over <see cref="LoadingRegister"/>
/// rows. This is the single source of truth that
/// <see cref="Controllers.ContractJourneyController"/>,
/// <see cref="Controllers.ReportsController"/>,
/// <see cref="Controllers.ReconciliationController"/> and any future report
/// MUST use when they need to compute the loaded quantity, the priced /
/// pending split, the traceable purchase cost or the weighted-average
/// purchase price from current LoadingRegister purchase rows.
///
/// Behavior parity (Phase C, no migration):
///   - All current LoadingRegister rows are treated as Purchase loadings.
///   - LoadingPriceUsd is the primary price; when missing the caller-
///     provided final-price lookup is the fallback. This mirrors the
///     existing <c>ResolveEffectiveLoadingPriceUsd</c> helpers that lived
///     locally inside the controllers before centralization.
///   - Numerical results are protected by parity unit tests.
///
/// Forward compatibility:
///   - When a future <c>InventoryTransportLeg</c> entity is introduced for
///     "re-loading from inventory" (rented tank -> wagon), it MUST NOT be
///     summed in any of these methods. Routing every aggregation through
///     this single service guarantees that a one-line filter (or the
///     simple omission of TransportLeg from the underlying query) keeps
///     purchase quantity / cost from being double-counted.
/// </summary>
public interface IPurchaseAggregationService
{
    /// <summary>
    /// Returns aggregation for a single Purchase contract using the
    /// caller-supplied loading price fallback (typically the contract's
    /// final price when LoadingPriceUsd is missing).
    /// </summary>
    Task<PurchaseAggregationSnapshot> AggregateForContractAsync(
        int contractId,
        decimal? contractFinalPriceUsd,
        CancellationToken ct = default);

    /// <summary>
    /// Returns one snapshot per provided Purchase contract. Contracts with
    /// no LoadingRegister rows are still represented in the dictionary
    /// with all-zero aggregates so that callers can rely on a populated
    /// lookup without nullable handling.
    /// </summary>
    Task<IReadOnlyDictionary<int, PurchaseAggregationSnapshot>> AggregateForContractsAsync(
        IReadOnlyCollection<int> contractIds,
        IReadOnlyDictionary<int, decimal?> contractFinalPriceById,
        CancellationToken ct = default);

    /// <summary>
    /// Returns the simple <c>sum(LoadedQuantityMt)</c> per contract. Used
    /// by Reconciliation to keep the current open-contract math unchanged.
    /// </summary>
    Task<IReadOnlyDictionary<int, decimal>> GetLoadedQuantityByContractAsync(
        CancellationToken ct = default);

    /// <summary>
    /// In-memory aggregation over a list of <see cref="LoadingRegister"/>
    /// rows that the caller has already loaded for a single Purchase
    /// contract. The service applies the same arithmetic, rounding and
    /// price-fallback logic as <see cref="AggregateForContractAsync"/>
    /// but performs no database I/O. Call this from controllers that
    /// already eagerly load LoadingRegisters for view rendering so the
    /// aggregation does not re-query the database.
    /// </summary>
    /// <param name="mirroredDriverExpenseAmounts">
    /// Driver ExpenseTransactions that LoadingController mirrors into the
    /// inline money fields of a line-based loading (see
    /// <see cref="LoadMirroredDriverExpenseAmountsAsync"/>). Those amounts
    /// are subtracted from the inline fields because the document itself is
    /// already a contract expense. Null keeps the previous behavior.
    /// </param>
    PurchaseAggregationSnapshot AggregateForLoadedRegisters(
        int contractId,
        IEnumerable<LoadingRegister> loadingRegisters,
        decimal? contractFinalPriceUsd,
        IReadOnlySet<int>? loadingRegisterIdsWithOfficialExpenses = null,
        IReadOnlySet<int>? loadingRegisterIdsWithExpenseLines = null,
        IReadOnlyDictionary<int, LoadingMirroredExpenseAmounts>? mirroredDriverExpenseAmounts = null);

    /// <summary>
    /// Active (non-cancelled, non-customs) driver ExpenseTransactions per
    /// loading, split into the same four inline buckets LoadingController
    /// mirrors them into (transport / storage / wagon rent / other).
    /// </summary>
    Task<IReadOnlyDictionary<int, LoadingMirroredExpenseAmounts>> LoadMirroredDriverExpenseAmountsAsync(
        IReadOnlyCollection<int> loadingRegisterIds,
        CancellationToken ct = default);

    /// <summary>
    /// Returns true when the supplied price is a usable purchase price
    /// (non-null and strictly positive). Centralized so that the numeric
    /// guard cannot drift between callers.
    /// </summary>
    static bool HasValidLoadingPrice(decimal? loadingPriceUsd)
        => loadingPriceUsd.HasValue && loadingPriceUsd.Value > 0m;
}

/// <summary>
/// Amounts of documented driver expenses that are mirrored into the inline
/// money fields of one loading, per inline bucket.
/// </summary>
public sealed record LoadingMirroredExpenseAmounts(
    decimal TransportUsd,
    decimal WarehouseUsd,
    decimal RailwayUsd,
    decimal OtherUsd);

/// <summary>
/// Read-only snapshot of purchase aggregation for a single contract.
/// </summary>
public sealed record PurchaseAggregationSnapshot(
    int ContractId,
    decimal TotalLoadedQuantityMt,
    decimal PricedPurchaseQuantityMt,
    decimal PendingPurchaseQuantityMt,
    int PendingLoadingCount,
    decimal TraceablePurchaseCostUsd,
    decimal? WeightedAveragePurchasePriceUsd,
    decimal LoadingTransportExpenseUsd,
    decimal LoadingWarehouseExpenseUsd,
    decimal LoadingOtherExpenseUsd,
    decimal LoadingRailwayExpenseUsd,
    decimal LoadingRailwayExpenseUsdFromLines = 0m)
{
    public static PurchaseAggregationSnapshot Empty(int contractId)
        => new(
            ContractId: contractId,
            TotalLoadedQuantityMt: 0m,
            PricedPurchaseQuantityMt: 0m,
            PendingPurchaseQuantityMt: 0m,
            PendingLoadingCount: 0,
            TraceablePurchaseCostUsd: 0m,
            WeightedAveragePurchasePriceUsd: null,
            LoadingTransportExpenseUsd: 0m,
            LoadingWarehouseExpenseUsd: 0m,
            LoadingOtherExpenseUsd: 0m,
            LoadingRailwayExpenseUsd: 0m,
            LoadingRailwayExpenseUsdFromLines: 0m);
}
