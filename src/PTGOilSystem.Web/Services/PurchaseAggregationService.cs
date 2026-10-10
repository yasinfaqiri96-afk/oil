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

    private readonly DateTime? _reportBefore;

    public PurchaseAggregationService(ApplicationDbContext db, DateTime? reportAsOfDate = null)
    {
        _db = db;
        _reportBefore = reportAsOfDate.HasValue ? DateTime.SpecifyKind(reportAsOfDate.Value.Date.AddDays(1), DateTimeKind.Utc) : null;
    }

    // کد نوع مصرفی که LoadingController کرایهٔ خودکار راننده را با آن ثبت و در فیلد درون‌خطی هم‌نام
    // آینه می‌کند (LoadingController.Loading*ExpenseCode).
    private const string LoadingTransportExpenseCode = "LOAD-TRANSPORT";
    private const string LoadingStorageExpenseCode = "LOAD-STORAGE";
    private const string LoadingWagonRentExpenseCode = "LOAD-WAGON-RENT";
    private const string LoadingOtherExpenseCode = "LOAD-OTHER";

    private static readonly LoadingMirroredExpenseAmounts NoMirroredExpenses = new(0m, 0m, 0m, 0m);

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
        IReadOnlySet<int>? loadingRegisterIdsWithExpenseLines = null,
        IReadOnlyDictionary<int, LoadingMirroredExpenseAmounts>? mirroredDriverExpenseAmounts = null)
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
                // بارگیریِ ردیف‌دار سندِ خودکارِ راننده (ExpenseTransaction) را هم برای نمایش در همین
                // فیلدها آینه می‌کند (LoadingController.MirrorLoadingExpenseLinesToLoading)؛ آن سند خودش
                // مصرف قرارداد است، پس سهمش از فیلد درون‌خطی کم می‌شود تا کرایه دو بار شمرده نشود.
                var mirrored = isLineBased && mirroredDriverExpenseAmounts is not null
                    && mirroredDriverExpenseAmounts.TryGetValue(row.Id, out var amounts)
                        ? amounts
                        : NoMirroredExpenses;
                var inlineTransport = Math.Max((row.TransportExpenseUsd ?? 0m) - mirrored.TransportUsd, 0m);
                var inlineWarehouse = Math.Max((row.WarehouseExpenseUsd ?? 0m) - mirrored.WarehouseUsd, 0m);
                var inlineOther = Math.Max((row.OtherExpenseUsd ?? 0m) - mirrored.OtherUsd, 0m);
                var inlineRailway = Math.Max((row.RailwayExpenseUsd ?? 0m) - mirrored.RailwayUsd, 0m);

                // کرایه (حمل/خط‌آهن) فقط وقتی هزینهٔ شرکت است که بدوش ما باشد. ردیف‌های دستیِ
                // «ثبت مصارف» انتخاب صریح کاربرند و همیشه شمرده می‌شوند.
                var countFreight = isLineBased || IsCompanyFreightCost(row.FreightCostResponsibility);
                transport += countFreight ? inlineTransport : 0m;
                warehouse += inlineWarehouse;
                other += inlineOther;
                railway += countFreight ? inlineRailway : 0m;
                if (isLineBased)
                {
                    railwayFromLines += inlineRailway;
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
            .Where(lr => !_reportBefore.HasValue || lr.LoadingDate < _reportBefore.Value)
            .Where(lr => lr.ContractId == contractId && !lr.IsCancelled)
            .Select(lr => new LoadingRegisterRow(
                lr.Id,
                lr.ContractId,
                lr.LoadedQuantityMt,
                lr.LoadingPriceUsd,
                lr.TransportExpenseUsd,
                lr.WarehouseExpenseUsd,
                lr.OtherExpenseUsd,
                lr.RailwayExpenseUsd,
                lr.FreightCostResponsibility))
            .ToListAsync(ct);

        var loadingIds = rows.Select(r => r.Id).ToList();
        var loadingIdsWithOfficialExpenses = await LoadLoadingIdsWithOfficialExpensesAsync(loadingIds, ct);
        var loadingIdsWithLines = await LoadLoadingIdsWithExpenseLinesAsync(loadingIds, ct);
        var mirroredDriverExpenses = await LoadMirroredDriverExpenseAmountsAsync(loadingIds, ct);

        return BuildSnapshot(contractId, rows, contractFinalPriceUsd, loadingIdsWithOfficialExpenses, loadingIdsWithLines, mirroredDriverExpenses);
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
            .Where(lr => !_reportBefore.HasValue || lr.LoadingDate < _reportBefore.Value)
            .Where(lr => contractIds.Contains(lr.ContractId) && !lr.IsCancelled)
            .Select(lr => new LoadingRegisterRow(
                lr.Id,
                lr.ContractId,
                lr.LoadedQuantityMt,
                lr.LoadingPriceUsd,
                lr.TransportExpenseUsd,
                lr.WarehouseExpenseUsd,
                lr.OtherExpenseUsd,
                lr.RailwayExpenseUsd,
                lr.FreightCostResponsibility))
            .ToListAsync(ct);

        var loadingIds = rows.Select(r => r.Id).ToList();
        var loadingIdsWithOfficialExpenses = await LoadLoadingIdsWithOfficialExpensesAsync(loadingIds, ct);
        var loadingIdsWithLines = await LoadLoadingIdsWithExpenseLinesAsync(loadingIds, ct);
        var mirroredDriverExpenses = await LoadMirroredDriverExpenseAmountsAsync(loadingIds, ct);

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
            result[contractId] = BuildSnapshot(contractId, contractRows, finalPrice, loadingIdsWithOfficialExpenses, loadingIdsWithLines, mirroredDriverExpenses);
        }

        return result;
    }

    public async Task<IReadOnlyDictionary<int, decimal>> GetLoadedQuantityByContractAsync(
        CancellationToken ct = default)
    {
        var rows = await _db.LoadingRegisters
            .AsNoTracking()
            .Where(lr => !_reportBefore.HasValue || lr.LoadingDate < _reportBefore.Value)
            .Where(lr => !lr.IsCancelled)
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
        IReadOnlySet<int>? loadingRegisterIdsWithExpenseLines = null,
        IReadOnlyDictionary<int, LoadingMirroredExpenseAmounts>? mirroredDriverExpenseAmounts = null)
    {
        ArgumentNullException.ThrowIfNull(loadingRegisters);

        var projected = loadingRegisters
            .Where(lr => lr.ContractId == contractId && !lr.IsCancelled)
            .Select(lr => new LoadingRegisterRow(
                lr.Id,
                lr.ContractId,
                lr.LoadedQuantityMt,
                lr.LoadingPriceUsd,
                lr.TransportExpenseUsd,
                lr.WarehouseExpenseUsd,
                lr.OtherExpenseUsd,
                lr.RailwayExpenseUsd,
                lr.FreightCostResponsibility));

        return BuildSnapshot(
            contractId,
            projected,
            contractFinalPriceUsd,
            loadingRegisterIdsWithOfficialExpenses,
            loadingRegisterIdsWithExpenseLines,
            mirroredDriverExpenseAmounts);
    }

    public async Task<IReadOnlyDictionary<int, LoadingMirroredExpenseAmounts>> LoadMirroredDriverExpenseAmountsAsync(
        IReadOnlyCollection<int> loadingRegisterIds,
        CancellationToken ct = default)
    {
        if (loadingRegisterIds is null || loadingRegisterIds.Count == 0)
        {
            return new Dictionary<int, LoadingMirroredExpenseAmounts>();
        }

        // همان انتخابی که LoadingController برای آینه می‌کند: سند فعال، غیرگمرکی، با راننده؛ و همان
        // چهار کد نوع مصرف که در چهار فیلد درون‌خطی نشسته‌اند.
        var rows = await _db.ExpenseTransactions
            .AsNoTracking()
            .Where(e => (!_reportBefore.HasValue || e.ExpenseDate < _reportBefore.Value) && !e.IsCancelled
                && !e.CustomsDeclarationId.HasValue
                && e.DriverId.HasValue
                && e.LoadingRegisterId.HasValue
                && loadingRegisterIds.Contains(e.LoadingRegisterId.Value)
                && e.ExpenseType != null)
            .Select(e => new { LoadingRegisterId = e.LoadingRegisterId!.Value, e.ExpenseType!.Code, e.AmountUsd })
            .ToListAsync(ct);

        decimal Sum(IEnumerable<(string Code, decimal AmountUsd)> items, string code)
            => items.Where(i => string.Equals(i.Code, code, StringComparison.OrdinalIgnoreCase)).Sum(i => i.AmountUsd);

        return rows
            .GroupBy(r => r.LoadingRegisterId)
            .ToDictionary(
                g => g.Key,
                g =>
                {
                    var items = g.Select(r => (r.Code, r.AmountUsd)).ToList();
                    return new LoadingMirroredExpenseAmounts(
                        Sum(items, LoadingTransportExpenseCode),
                        Sum(items, LoadingStorageExpenseCode),
                        Sum(items, LoadingWagonRentExpenseCode),
                        Sum(items, LoadingOtherExpenseCode));
                });
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
            .Where(e => (!_reportBefore.HasValue || e.ExpenseDate < _reportBefore.Value) && !e.IsCancelled
                && !e.CustomsDeclarationId.HasValue
                // A group share is an additional cost, not the original loading's fixed-field mirror.
                && !e.ExpenseBatchId.HasValue
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
        decimal? RailwayExpenseUsd,
        CostResponsibility? FreightCostResponsibility);

    /// <summary>
    /// کرایهٔ بدوش فروشنده/مشترک/نامشخص هزینهٔ شرکت ما نیست (مشترک: سهم شرکت هنوز تعریف نشده)
    /// و فقط معلومات بارگیری است. خالی = بارگیری قدیمی یا ترانسپورت داخلی ⇒ مثل قبل هزینه.
    /// </summary>
    private static bool IsCompanyFreightCost(CostResponsibility? responsibility)
        => responsibility is null or CostResponsibility.Buyer;
}
