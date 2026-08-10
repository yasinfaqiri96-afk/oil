using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Helpers;
using PTGOilSystem.Web.Models.Entities;

namespace PTGOilSystem.Web.Services;

/// <summary>یک سطر منبعِ بهای خرید محموله: از کدام قرارداد/بارگیری، چه مقدار، با چه نرخی.</summary>
public sealed record ShipmentPurchaseSourceLine(
    int ContractId,
    string ContractLabel,
    int? LoadingRegisterId,
    string? LoadingLabel,
    DateTime? LoadingDate,
    decimal QuantityMt,
    decimal? UnitCostUsd,
    decimal ExtendedCostUsd,
    string CostSource)
{
    /// <summary>سطر دقیقِ بارگیری‌محور (نه fallback سطح قرارداد).</summary>
    public bool IsLoadingExact => LoadingRegisterId.HasValue;
}

/// <summary>بهای خرید یک محموله به‌همراه ریز منابع آن.</summary>
public sealed record ShipmentPurchaseCostSnapshot(
    int ShipmentId,
    IReadOnlyList<ShipmentPurchaseSourceLine> Lines)
{
    public static ShipmentPurchaseCostSnapshot Empty(int shipmentId) => new(shipmentId, []);

    public decimal TotalQuantityMt
        => decimal.Round(Lines.Sum(l => l.QuantityMt), 4, MidpointRounding.AwayFromZero);

    public decimal TotalPurchaseCostUsd
        => decimal.Round(Lines.Sum(l => l.ExtendedCostUsd), 4, MidpointRounding.AwayFromZero);

    /// <summary>میانگین وزنی — فقط نمایشی. محاسبهٔ اصلی همیشه Σ(مقدار × نرخ منبع) است.</summary>
    public decimal? WeightedAverageUnitCostUsd
    {
        get
        {
            var pricedQuantityMt = Lines.Where(l => l.UnitCostUsd is > 0m).Sum(l => l.QuantityMt);
            return pricedQuantityMt > 0m
                ? decimal.Round(
                    Lines.Where(l => l.UnitCostUsd is > 0m).Sum(l => l.ExtendedCostUsd) / pricedQuantityMt,
                    4,
                    MidpointRounding.AwayFromZero)
                : null;
        }
    }

    /// <summary>آیا این محموله سهم دقیقِ بارگیری دارد (یعنی Source-exact است)؟</summary>
    public bool HasLoadingExactSources => Lines.Any(l => l.IsLoadingExact);

    /// <summary>قراردادهایی که هیچ نرخ معتبری برایشان پیدا نشد.</summary>
    public IReadOnlyList<int> UnpricedContractIds
        => Lines.Where(l => l.UnitCostUsd is not > 0m).Select(l => l.ContractId).Distinct().ToList();

    public IReadOnlyDictionary<int, decimal> CostByContract
        => Lines.GroupBy(l => l.ContractId)
            .ToDictionary(
                g => g.Key,
                g => decimal.Round(g.Sum(l => l.ExtendedCostUsd), 4, MidpointRounding.AwayFromZero));

    public IReadOnlyDictionary<int, decimal?> UnitCostByContract
        => Lines.GroupBy(l => l.ContractId)
            .ToDictionary(
                g => g.Key,
                g =>
                {
                    var priced = g.Where(l => l.UnitCostUsd is > 0m).ToList();
                    var quantityMt = priced.Sum(l => l.QuantityMt);
                    return quantityMt > 0m
                        ? decimal.Round(priced.Sum(l => l.ExtendedCostUsd) / quantityMt, 4, MidpointRounding.AwayFromZero)
                        : (decimal?)null;
                });

    public IReadOnlyDictionary<int, string> CostSourceByContract
        => Lines.GroupBy(l => l.ContractId)
            .ToDictionary(g => g.Key, g => g.First().CostSource);
}

/// <summary>
/// تنها محاسبه‌کنندهٔ بهای خرید محموله در کل سیستم.
///
/// ترتیب قطعیِ نرخ برای هر قرارداد داخل محموله:
///   1) سهم دقیق بارگیری (<see cref="ShipmentLoadingAllocation"/>) × نرخ قطعی همان بارگیری
///      (<see cref="LoadingRegister.LoadingPriceUsd"/>) — حقیقتِ تاریخی؛ Platts هرگز دوباره محاسبه نمی‌شود.
///   2) اگر بارگیریِ تخصیص‌یافته نرخ ندارد → نرخ نهایی هدر همان قرارداد (fallback قدیمی).
///   3) اگر محموله برای آن قرارداد هیچ سهم بارگیری ندارد (دادهٔ قدیمی) → میانگین وزنی
///      بارگیری‌های همان قرارداد، و در نبود آن نرخ نهایی هدر — دقیقاً رفتار قبلی، بدون backfill.
///
/// Controller/View/گزارش نباید فرمول جداگانه‌ای برای بهای خرید داشته باشد.
/// </summary>
public sealed class ShipmentPurchaseCostService
{
    public const string SourceLoadingExact = "Allocated loading price";
    public const string SourceLoadingWithoutPrice = "Allocated loading without price";

    private readonly ApplicationDbContext _db;

    public ShipmentPurchaseCostService(ApplicationDbContext db) => _db = db;

    public async Task<ShipmentPurchaseCostSnapshot> BuildAsync(int shipmentId, CancellationToken ct = default)
    {
        var map = await BuildForShipmentsAsync([shipmentId], ct);
        return map.TryGetValue(shipmentId, out var snapshot)
            ? snapshot
            : ShipmentPurchaseCostSnapshot.Empty(shipmentId);
    }

    public async Task<IReadOnlyDictionary<int, ShipmentPurchaseCostSnapshot>> BuildForShipmentsAsync(
        IReadOnlyCollection<int> shipmentIds,
        CancellationToken ct = default)
    {
        var ids = shipmentIds.Distinct().ToList();
        if (ids.Count == 0)
        {
            return new Dictionary<int, ShipmentPurchaseCostSnapshot>();
        }

        var contractAllocations = await _db.ShipmentContracts
            .AsNoTracking()
            .Include(sc => sc.Contract)
            .Where(sc => ids.Contains(sc.ShipmentId)
                && sc.QuantityMt.HasValue
                && sc.QuantityMt.Value > 0m)
            .ToListAsync(ct);

        var loadingAllocations = await _db.ShipmentLoadingAllocations
            .AsNoTracking()
            .Include(a => a.LoadingRegister)
            .Include(a => a.Contract)
            .Where(a => ids.Contains(a.ShipmentId) && a.QuantityMt > 0m)
            .OrderBy(a => a.LoadingRegisterId)
            .ThenBy(a => a.Id)
            .ToListAsync(ct);

        var contractIds = contractAllocations.Select(sc => sc.ContractId)
            .Concat(loadingAllocations.Select(a => a.ContractId))
            .Distinct()
            .ToList();

        var contractsById = contractAllocations
            .Where(sc => sc.Contract is not null)
            .GroupBy(sc => sc.ContractId)
            .ToDictionary(g => g.Key, g => g.First().Contract!);
        foreach (var allocation in loadingAllocations.Where(a => a.Contract is not null))
        {
            contractsById.TryAdd(allocation.ContractId, allocation.Contract!);
        }

        var finalPriceByContract = contractIds.ToDictionary(
            id => id,
            id => contractsById.TryGetValue(id, out var contract)
                ? ContractPricingAdapter.GetCanonicalFinalPrice(contract)
                : null);

        // fallback سطح قرارداد فقط برای قراردادهای بدون سهم بارگیری لازم است، ولی همان یک
        // کوئری تجمیعی برای همه گرفته می‌شود تا تعداد رفت‌وبرگشت ثابت بماند.
        var purchaseSnapshots = await new PurchaseAggregationService(_db)
            .AggregateForContractsAsync(contractIds, finalPriceByContract, ct);

        var loadingByShipmentContract = loadingAllocations
            .GroupBy(a => (a.ShipmentId, a.ContractId))
            .ToDictionary(g => g.Key, g => g.ToList());

        var result = new Dictionary<int, ShipmentPurchaseCostSnapshot>(ids.Count);
        foreach (var shipmentId in ids)
        {
            var lines = new List<ShipmentPurchaseSourceLine>();

            foreach (var contractAllocation in contractAllocations
                .Where(sc => sc.ShipmentId == shipmentId)
                .OrderBy(sc => sc.Id))
            {
                var contractId = contractAllocation.ContractId;
                var contractLabel = contractsById.TryGetValue(contractId, out var contract)
                    ? contract.DisplayLabel
                    : $"#{contractId}";
                var contractFinalPrice = finalPriceByContract.GetValueOrDefault(contractId);

                if (loadingByShipmentContract.TryGetValue((shipmentId, contractId), out var exactRows))
                {
                    // مسیر Source-exact: هر بارگیریِ تخصیص‌یافته یک سطر مستقل با نرخ خودش.
                    lines.AddRange(exactRows.Select(row => BuildLoadingLine(row, contractLabel, contractFinalPrice)));
                    continue;
                }

                // مسیر legacy: محموله برای این قرارداد سهم بارگیری ندارد.
                var (unitCost, source) = ShipmentPurchaseCostResolver.ResolveContractUnitCost(
                    purchaseSnapshots.GetValueOrDefault(contractId),
                    contractFinalPrice);
                var quantityMt = contractAllocation.QuantityMt!.Value;
                lines.Add(new ShipmentPurchaseSourceLine(
                    ContractId: contractId,
                    ContractLabel: contractLabel,
                    LoadingRegisterId: null,
                    LoadingLabel: null,
                    LoadingDate: null,
                    QuantityMt: quantityMt,
                    UnitCostUsd: unitCost,
                    ExtendedCostUsd: unitCost is > 0m ? RoundMoney(quantityMt * unitCost.Value) : 0m,
                    CostSource: source));
            }

            // سهم بارگیری برای قراردادی که ردیف ShipmentContract ندارد هم باید دیده شود
            // (دادهٔ ناهماهنگ؛ نباید silently از بهای خرید بیفتد).
            foreach (var orphan in loadingAllocations
                .Where(a => a.ShipmentId == shipmentId
                    && !contractAllocations.Any(sc => sc.ShipmentId == shipmentId && sc.ContractId == a.ContractId))
                .OrderBy(a => a.Id))
            {
                var contractLabel = contractsById.TryGetValue(orphan.ContractId, out var contract)
                    ? contract.DisplayLabel
                    : $"#{orphan.ContractId}";
                lines.Add(BuildLoadingLine(orphan, contractLabel, finalPriceByContract.GetValueOrDefault(orphan.ContractId)));
            }

            result[shipmentId] = new ShipmentPurchaseCostSnapshot(shipmentId, lines);
        }

        return result;
    }

    private static ShipmentPurchaseSourceLine BuildLoadingLine(
        ShipmentLoadingAllocation allocation,
        string contractLabel,
        decimal? contractFinalPriceUsd)
    {
        var loading = allocation.LoadingRegister;
        var lockedPrice = loading?.LoadingPriceUsd;
        // نرخ قطعیِ همان بارگیری حقیقت تاریخی است؛ فقط اگر اصلاً ثبت نشده باشد به هدر قرارداد می‌رویم.
        var (unitCost, source) = IPurchaseAggregationService.HasValidLoadingPrice(lockedPrice)
            ? (lockedPrice, SourceLoadingExact)
            : contractFinalPriceUsd is > 0m
                ? (contractFinalPriceUsd, SourceLoadingWithoutPrice)
                : (null, ShipmentPurchaseCostResolver.SourceMissing);

        return new ShipmentPurchaseSourceLine(
            ContractId: allocation.ContractId,
            ContractLabel: contractLabel,
            LoadingRegisterId: allocation.LoadingRegisterId,
            LoadingLabel: BuildLoadingLabel(loading, allocation.LoadingRegisterId),
            LoadingDate: loading?.LoadingDate,
            QuantityMt: allocation.QuantityMt,
            UnitCostUsd: unitCost,
            ExtendedCostUsd: unitCost is > 0m ? RoundMoney(allocation.QuantityMt * unitCost.Value) : 0m,
            CostSource: source);
    }

    public static string BuildLoadingLabel(LoadingRegister? loading, int loadingRegisterId)
    {
        if (loading is null)
        {
            return $"#{loadingRegisterId}";
        }

        var reference = loading.BillOfLadingNumber
            ?? loading.RwbNo
            ?? loading.WagonNumber;
        return string.IsNullOrWhiteSpace(reference)
            ? $"#{loading.Id}"
            : $"{reference} (#{loading.Id})";
    }

    private static decimal RoundMoney(decimal value)
        => decimal.Round(value, 4, MidpointRounding.AwayFromZero);
}
