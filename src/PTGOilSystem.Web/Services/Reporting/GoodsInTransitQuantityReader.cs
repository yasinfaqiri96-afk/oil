using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Helpers;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.Reports;

namespace PTGOilSystem.Web.Services.Reporting;

/// <summary>یک بارِ در مسیر تا یک تاریخ: فقط مقدار و کلیدها؛ نام و برچسب را هر مصرف‌کننده خودش می‌خواند.</summary>
/// <param name="ContractId">بارگیری و حمل داخلی: قرارداد خرید منبع؛ دیسپچ: قرارداد خودِ دیسپچ.</param>
/// <param name="ReceivedMt">بارگیری: رسیدهای تا تاریخ؛ حمل داخلی: دریافت + کسریِ رسیدهای تا تاریخ؛ دیسپچ: صفر.</param>
/// <param name="HasActiveSale">فقط دیسپچ: فروش فعالِ همین موتر ثبت شده است (طلب مشتری، نه دارایی موجودی).</param>
public sealed record GoodsInTransitQuantityRow(
    GoodsInTransitKind Kind,
    int SourceId,
    int? ContractId,
    DateTime DepartureDate,
    decimal LoadedMt,
    decimal ReceivedMt,
    decimal RemainingMt,
    bool HasActiveSale = false);

/// <summary>
/// تنها قاعدهٔ مقدار «بار در مسیر» تا یک تاریخ. راپور بارهای در مسیر، داشبورد موبایل و بیلانس کلی شرکت
/// همه از همین می‌خوانند تا یک بار در دو صفحه دو مقدار نداشته باشد:
/// <list type="bullet">
/// <item>بارگیری از مبدأ: لغونشده، تا تاریخ؛ بارگیری − رسیدهای لغونشدهٔ تا تاریخ − تخصیص به حمل داخلیِ
/// لغونشدهٔ تا تاریخ − کسری رسیدِ ثبت‌شدهٔ تا تاریخ.</item>
/// <item>حمل داخلی: بارگیری‌شده تا تاریخ؛ هر رسیدِ لغونشده «دریافت + کسری» مصرف می‌کند
/// (<see cref="TransportQuantityService"/>). حملِ «رسیده» فقط به‌اندازهٔ رسیدهای بعد از تاریخ در مسیر بوده است.</item>
/// <item>موتر به مشتری: بارگیری‌شده/در مسیر، تا تاریخ؛ دیسپچِ «ادامهٔ حمل» همان بارِ مرحلهٔ بعد است و کنار می‌رود.</item>
/// </list>
/// هیچ مقداری منفی نمی‌شود و بارِ قدیمیِ بی‌رسید حذف نمی‌شود؛ هشدارش با مصرف‌کننده است.
/// </summary>
public sealed class GoodsInTransitQuantityReader(ApplicationDbContext db)
{
    private const decimal Epsilon = 0.0001m;

    public async Task<IReadOnlyList<GoodsInTransitQuantityRow>> ReadAsync(
        DateTime asOfDate,
        GoodsInTransitKind? kind = null,
        int? productId = null,
        CancellationToken ct = default)
    {
        // کلید تاریخ تجاری با Kind=Utc، همان قرارداد ستون‌های timestamptz.
        var asOfExclusive = DateTime.SpecifyKind(asOfDate.Date.AddDays(1), DateTimeKind.Utc);
        var rows = new List<GoodsInTransitQuantityRow>();
        if (kind is null or GoodsInTransitKind.FromOrigin)
        {
            rows.AddRange(await ReadOriginLoadsAsync(asOfExclusive, productId, ct));
        }

        if (kind is null or GoodsInTransitKind.InternalTransfer)
        {
            rows.AddRange(await ReadTransportLegsAsync(asOfExclusive, productId, ct));
        }

        if (kind is null or GoodsInTransitKind.CustomerDelivery)
        {
            rows.AddRange(await ReadDispatchesAsync(asOfExclusive, productId, ct));
        }

        return rows;
    }

    private async Task<List<GoodsInTransitQuantityRow>> ReadOriginLoadsAsync(
        DateTime asOfExclusive,
        int? productId,
        CancellationToken ct)
    {
        var query = db.LoadingRegisters.AsNoTracking()
            .Where(l => !l.IsCancelled && l.LoadingDate < asOfExclusive && l.LoadedQuantityMt > 0m);
        if (productId.HasValue)
        {
            query = query.Where(l => l.ProductId == productId.Value);
        }

        var loads = await query
            .Select(l => new { l.Id, l.ContractId, l.LoadingDate, l.LoadedQuantityMt })
            .ToListAsync(ct);
        if (loads.Count == 0)
        {
            return [];
        }

        var received = await db.LoadingReceipts.AsNoTracking()
            .Where(r => !r.IsCancelled && r.ReceiptDate < asOfExclusive)
            .GroupBy(r => r.LoadingRegisterId)
            .Select(g => new { LoadingId = g.Key, Mt = g.Sum(r => r.ReceivedQuantityMt) })
            .ToDictionaryAsync(x => x.LoadingId, x => x.Mt, ct);

        var allocated = await db.InventoryTransportLegAllocations.AsNoTracking()
            .Where(a => a.SourceLoadingRegisterId != null
                && a.InventoryTransportLeg != null
                && a.InventoryTransportLeg.Status != InventoryTransportLegStatus.Cancelled
                && a.InventoryTransportLeg.LoadedDate < asOfExclusive)
            .GroupBy(a => a.SourceLoadingRegisterId!.Value)
            .Select(g => new { LoadingId = g.Key, Mt = g.Sum(a => a.QuantityMt) })
            .ToDictionaryAsync(x => x.LoadingId, x => x.Mt, ct);

        // کسری رسید: همان مقداری که از وسیله کم شده (تفاوت، وگرنه قابل جریمه). کسریِ رسیدِ لغوشده نمی‌آید.
        var shortages = (await db.LossEvents.AsNoTracking()
                .Where(e => !e.IsCancelled
                    && e.Stage == LossEventStage.ReceiptShortage
                    && e.EventDate < asOfExclusive
                    && (e.LoadingRegisterId != null || e.LoadingReceipt != null)
                    && (e.LoadingReceipt == null || !e.LoadingReceipt.IsCancelled))
                .Select(e => new
                {
                    LoadingId = e.LoadingRegisterId ?? e.LoadingReceipt!.LoadingRegisterId,
                    Mt = e.DifferenceQuantityMt > 0m
                        ? e.DifferenceQuantityMt
                        : e.ChargeableLossMt > 0m ? e.ChargeableLossMt : 0m
                })
                .ToListAsync(ct))
            .GroupBy(x => x.LoadingId)
            .ToDictionary(g => g.Key, g => g.Sum(x => x.Mt));

        var rows = new List<GoodsInTransitQuantityRow>();
        foreach (var load in loads)
        {
            var receivedMt = received.GetValueOrDefault(load.Id);
            var remaining = Remaining(load.LoadedQuantityMt
                - receivedMt
                - allocated.GetValueOrDefault(load.Id)
                - shortages.GetValueOrDefault(load.Id));
            if (remaining > Epsilon)
            {
                rows.Add(new GoodsInTransitQuantityRow(
                    GoodsInTransitKind.FromOrigin, load.Id, load.ContractId, load.LoadingDate,
                    load.LoadedQuantityMt, receivedMt, remaining));
            }
        }

        return rows;
    }

    private async Task<List<GoodsInTransitQuantityRow>> ReadTransportLegsAsync(
        DateTime asOfExclusive,
        int? productId,
        CancellationToken ct)
    {
        var query = db.InventoryTransportLegs.AsNoTracking()
            .Where(l => (l.Status == InventoryTransportLegStatus.Loaded
                    || l.Status == InventoryTransportLegStatus.InTransit
                    || l.Status == InventoryTransportLegStatus.Received)
                && l.LoadedDate < asOfExclusive);
        if (productId.HasValue)
        {
            query = query.Where(l => l.ProductId == productId.Value);
        }

        var legs = await query
            .Select(l => new { l.Id, l.SourcePurchaseContractId, l.LoadedDate, l.QuantityMt, l.Status })
            .ToListAsync(ct);
        if (legs.Count == 0)
        {
            return [];
        }

        var legIds = legs.Select(l => l.Id).ToArray();
        var consumed = (await db.InventoryTransportReceipts.AsNoTracking()
                .Where(r => !r.IsCancelled && legIds.Contains(r.InventoryTransportLegId))
                .Select(r => new
                {
                    LegId = r.InventoryTransportLegId,
                    Before = r.ReceiptDate < asOfExclusive,
                    Mt = r.ReceivedQuantityMt + r.ShortageQuantityMt
                })
                .ToListAsync(ct))
            .GroupBy(r => r.LegId)
            .ToDictionary(
                g => g.Key,
                g => (Before: g.Where(r => r.Before).Sum(r => r.Mt), After: g.Where(r => !r.Before).Sum(r => r.Mt)));

        var rows = new List<GoodsInTransitQuantityRow>();
        foreach (var leg in legs)
        {
            var (before, after) = consumed.GetValueOrDefault(leg.Id);
            var open = leg.QuantityMt - before;
            // حملِ «رسیده» بسته شده است؛ تا تاریخ گزارش فقط همان مقداری در مسیر بوده که بعد از آن رسید خورده.
            var remaining = Remaining(leg.Status == InventoryTransportLegStatus.Received ? Math.Min(open, after) : open);
            if (remaining > Epsilon)
            {
                rows.Add(new GoodsInTransitQuantityRow(
                    GoodsInTransitKind.InternalTransfer, leg.Id, leg.SourcePurchaseContractId, leg.LoadedDate,
                    leg.QuantityMt, before, remaining));
            }
        }

        return rows;
    }

    private async Task<List<GoodsInTransitQuantityRow>> ReadDispatchesAsync(
        DateTime asOfExclusive,
        int? productId,
        CancellationToken ct)
    {
        var continuedReceiptIds = TransportChainProjection.ContinuedTransferReceiptIds(db);
        var query = db.TruckDispatches.AsNoTracking()
            .Where(d => (d.Status == DispatchStatus.Loaded || d.Status == DispatchStatus.InTransit)
                && d.DispatchDate < asOfExclusive
                && d.LoadedQuantityMt > 0m
                && !(d.InventoryTransportReceiptId != null
                    && continuedReceiptIds.Contains(d.InventoryTransportReceiptId.Value)));
        if (productId.HasValue)
        {
            query = query.Where(d => d.ProductId == productId.Value);
        }

        return (await query
                .Select(d => new
                {
                    d.Id,
                    d.ContractId,
                    d.DispatchDate,
                    d.LoadedQuantityMt,
                    HasActiveSale = d.SalesTransactionId != null
                        && d.SalesTransaction != null
                        && !d.SalesTransaction.IsCancelled
                })
                .ToListAsync(ct))
            .Select(d => new GoodsInTransitQuantityRow(
                GoodsInTransitKind.CustomerDelivery, d.Id, d.ContractId, d.DispatchDate,
                d.LoadedQuantityMt, 0m, Remaining(d.LoadedQuantityMt), d.HasActiveSale))
            .ToList();
    }

    private static decimal Remaining(decimal value)
        => Math.Max(0m, decimal.Round(value, 4, MidpointRounding.AwayFromZero));
}
