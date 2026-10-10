using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;

namespace PTGOilSystem.Web.Services;

/// <summary>
/// تفکیک سرنوشت مقدار یک حمل. اتحاد نگهداشت مقدار:
/// <c>Loaded = SoldMt + ReceivedToInventoryMt + TransferredToVehicleMt + ShortageMt + RemainingMt</c>
/// </summary>
public sealed record TransportLegQuantities(
    int LegId,
    decimal LoadedMt,
    decimal SoldMt,
    decimal ReceivedToInventoryMt,
    decimal TransferredToVehicleMt,
    decimal ShortageMt,
    decimal RemainingMt)
{
    /// <summary>مقداری که تا حالا از حمل مصرف شده (هر سرنوشتی به‌جز باقیمانده).</summary>
    public decimal ConsumedMt => SoldMt + ReceivedToInventoryMt + TransferredToVehicleMt + ShortageMt;

    public bool IsBalanced => Math.Abs(LoadedMt - (ConsumedMt + RemainingMt)) <= TransportQuantityService.Epsilon;
}

public interface ITransportQuantityService
{
    Task<decimal> GetRemainingMtAsync(int legId, CancellationToken ct = default);

    Task<IReadOnlyDictionary<int, decimal>> GetRemainingMtAsync(
        IReadOnlyCollection<int> legIds,
        CancellationToken ct = default);

    Task<TransportLegQuantities> GetQuantitiesAsync(int legId, CancellationToken ct = default);

    async Task<IReadOnlyDictionary<int, TransportLegQuantities>> GetQuantitiesAsync(
        IReadOnlyCollection<int> legIds, CancellationToken ct = default)
    {
        var result = new Dictionary<int, TransportLegQuantities>();
        foreach (var id in legIds.Distinct()) result[id] = await GetQuantitiesAsync(id, ct);
        return result;
    }
}

/// <summary>
/// تک‌منبع محاسبهٔ «چقدر از این حمل مانده» و تفکیک سرنوشت مقدار.
///
/// <para>پیش از این، همین فرمول در پنج نقطه جداگانه کپی شده بود (سرویس رسید، فهرست واگن‌های در
/// جریان، تسویهٔ موترها، فروش گروهی). فرمول‌ها هم‌معنی بودند ولی هر تغییری باید در پنج جا
/// تکرار می‌شد و اولین جای فراموش‌شده Double Consumption می‌ساخت.</para>
///
/// <para><b>قرارداد مصرف:</b> هر رسیدِ لغو‌نشده به‌اندازهٔ <c>ReceivedQuantityMt + ShortageQuantityMt</c>
/// از حمل مصرف می‌کند — دقیقاً همان قاعده‌ای که مسیرهای فعلی اجرا می‌کردند. کسری هم مصرف است،
/// چون آن مقدار دیگر روی وسیله نیست.</para>
/// </summary>
public sealed class TransportQuantityService : ITransportQuantityService
{
    internal const decimal Epsilon = 0.0001m;

    private readonly ApplicationDbContext _db;

    public TransportQuantityService(ApplicationDbContext db) => _db = db;

    public async Task<decimal> GetRemainingMtAsync(int legId, CancellationToken ct = default)
    {
        var loadedMt = await _db.InventoryTransportLegs
            .AsNoTracking()
            .Where(l => l.Id == legId)
            .Select(l => (decimal?)l.QuantityMt)
            .FirstOrDefaultAsync(ct);

        if (loadedMt is null)
        {
            return 0m;
        }

        var consumedMt = await ConsumedQuery(legId).SumAsync(ct);
        return Round(loadedMt.Value - consumedMt);
    }

    public async Task<IReadOnlyDictionary<int, decimal>> GetRemainingMtAsync(
        IReadOnlyCollection<int> legIds,
        CancellationToken ct = default)
    {
        if (legIds.Count == 0)
        {
            return new Dictionary<int, decimal>();
        }

        var loaded = await _db.InventoryTransportLegs
            .AsNoTracking()
            .Where(l => legIds.Contains(l.Id))
            .Select(l => new { l.Id, l.QuantityMt })
            .ToDictionaryAsync(l => l.Id, l => l.QuantityMt, ct);

        var consumed = await _db.InventoryTransportReceipts
            .AsNoTracking()
            .Where(r => legIds.Contains(r.InventoryTransportLegId) && !r.IsCancelled)
            .GroupBy(r => r.InventoryTransportLegId)
            .Select(g => new { LegId = g.Key, Mt = g.Sum(r => r.ReceivedQuantityMt + r.ShortageQuantityMt) })
            .ToDictionaryAsync(x => x.LegId, x => x.Mt, ct);

        return loaded.ToDictionary(
            l => l.Key,
            l => Round(l.Value - (consumed.TryGetValue(l.Key, out var mt) ? mt : 0m)));
    }

    public async Task<TransportLegQuantities> GetQuantitiesAsync(int legId, CancellationToken ct = default)
        => (await GetQuantitiesAsync([legId], ct)).GetValueOrDefault(legId)
            ?? new TransportLegQuantities(legId, 0m, 0m, 0m, 0m, 0m, 0m);

    public async Task<IReadOnlyDictionary<int, TransportLegQuantities>> GetQuantitiesAsync(
        IReadOnlyCollection<int> legIds, CancellationToken ct = default)
    {
        if (legIds.Count == 0) return new Dictionary<int, TransportLegQuantities>();
        var loaded = await _db.InventoryTransportLegs.AsNoTracking().Where(l => legIds.Contains(l.Id))
            .Select(l => new { l.Id, l.QuantityMt }).ToDictionaryAsync(l => l.Id, l => l.QuantityMt, ct);
        var receipts = await _db.InventoryTransportReceipts.AsNoTracking()
            .Where(r => legIds.Contains(r.InventoryTransportLegId) && !r.IsCancelled)
            .Select(r => new { r.InventoryTransportLegId, r.ReceiptDestination, r.ReceivedQuantityMt, r.ShortageQuantityMt })
            .ToListAsync(ct);
        var byLeg = receipts.ToLookup(r => r.InventoryTransportLegId);
        return loaded.ToDictionary(l => l.Key, l =>
        {
            var rows = byLeg[l.Key];
            var sold = rows.Where(r => r.ReceiptDestination == InventoryTransportReceiptDestination.DirectSale).Sum(r => r.ReceivedQuantityMt);
            var received = rows.Where(r => r.ReceiptDestination is InventoryTransportReceiptDestination.ToInventory or InventoryTransportReceiptDestination.Mixed)
                .Sum(r => r.ReceivedQuantityMt);
            var transferred = rows.Where(r => r.ReceiptDestination == InventoryTransportReceiptDestination.DirectDispatch).Sum(r => r.ReceivedQuantityMt);
            var shortage = rows.Sum(r => r.ShortageQuantityMt);
            return new TransportLegQuantities(l.Key, Round(l.Value), Round(sold), Round(received), Round(transferred),
                Round(shortage), Round(l.Value - sold - received - transferred - shortage));
        });
    }

    private IQueryable<decimal> ConsumedQuery(int legId)
        => _db.InventoryTransportReceipts
            .AsNoTracking()
            .Where(r => r.InventoryTransportLegId == legId && !r.IsCancelled)
            .Select(r => r.ReceivedQuantityMt + r.ShortageQuantityMt);

    private static decimal Round(decimal value)
        => decimal.Round(value, 4, MidpointRounding.AwayFromZero);
}
