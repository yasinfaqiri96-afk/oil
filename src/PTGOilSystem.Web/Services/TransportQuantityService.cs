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
    {
        var loadedMt = await _db.InventoryTransportLegs
            .AsNoTracking()
            .Where(l => l.Id == legId)
            .Select(l => (decimal?)l.QuantityMt)
            .FirstOrDefaultAsync(ct) ?? 0m;

        var receipts = await _db.InventoryTransportReceipts
            .AsNoTracking()
            .Where(r => r.InventoryTransportLegId == legId && !r.IsCancelled)
            .Select(r => new { r.ReceiptDestination, r.ReceivedQuantityMt, r.ShortageQuantityMt })
            .ToListAsync(ct);

        // مقصد هر رسید سرنوشت فیزیکی همان مقدار است؛ کسری جدا شمرده می‌شود چون به هیچ مقصدی نرسید.
        var soldMt = receipts
            .Where(r => r.ReceiptDestination == InventoryTransportReceiptDestination.DirectSale)
            .Sum(r => r.ReceivedQuantityMt);
        var receivedMt = receipts
            .Where(r => r.ReceiptDestination == InventoryTransportReceiptDestination.ToInventory)
            .Sum(r => r.ReceivedQuantityMt);
        var transferredMt = receipts
            .Where(r => r.ReceiptDestination == InventoryTransportReceiptDestination.DirectDispatch)
            .Sum(r => r.ReceivedQuantityMt);
        // مقصد Mixed به هیچ‌کدام از سه سطل بالا تعلق ندارد؛ در مصرف هست ولی تفکیک نمی‌شود.
        var mixedMt = receipts
            .Where(r => r.ReceiptDestination == InventoryTransportReceiptDestination.Mixed)
            .Sum(r => r.ReceivedQuantityMt);
        var shortageMt = receipts.Sum(r => r.ShortageQuantityMt);

        var consumedMt = soldMt + receivedMt + transferredMt + mixedMt + shortageMt;

        return new TransportLegQuantities(
            legId,
            Round(loadedMt),
            Round(soldMt),
            Round(receivedMt + mixedMt),
            Round(transferredMt),
            Round(shortageMt),
            Round(loadedMt - consumedMt));
    }

    private IQueryable<decimal> ConsumedQuery(int legId)
        => _db.InventoryTransportReceipts
            .AsNoTracking()
            .Where(r => r.InventoryTransportLegId == legId && !r.IsCancelled)
            .Select(r => r.ReceivedQuantityMt + r.ShortageQuantityMt);

    private static decimal Round(decimal value)
        => decimal.Round(value, 4, MidpointRounding.AwayFromZero);
}
