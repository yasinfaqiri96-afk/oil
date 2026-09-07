using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;

namespace PTGOilSystem.Web.Services;

/// <summary>
/// خواندنِ لایهٔ Lineage برای «پرونده کشتی». فقط چیزهایی را جمع می‌زند که واقعاً
/// RootShipmentId همان کشتی را دارند — پس موتر/واگنِ غیرمرتبط با کشتی هرگز در پرونده نمی‌آید.
/// محاسبهٔ قیمت خرید را عوض نمی‌کند؛ آن از منطق فعلی قراردادی می‌آید (fallback).
/// </summary>
public sealed class InventoryLineagePnlService
{
    private readonly ApplicationDbContext _db;

    public InventoryLineagePnlService(ApplicationDbContext db) => _db = db;

    public async Task<ShipmentLineageRollup?> BuildAsync(int shipmentId, CancellationToken ct = default)
    {
        var lots = await _db.InventoryLots.AsNoTracking()
            .Where(l => l.RootShipmentId == shipmentId)
            .ToListAsync(ct);

        if (lots.Count == 0)
        {
            return null; // داده‌ای برای این کشتی در لایهٔ Lineage نیست → caller به منطق فعلی fallback می‌کند.
        }

        var lotIds = lots.Select(l => l.Id).ToHashSet();

        var movements = await _db.InventoryLotMovements.AsNoTracking()
            .Where(m => (m.FromLotId != null && lotIds.Contains(m.FromLotId.Value))
                        || (m.ToLotId != null && lotIds.Contains(m.ToLotId.Value)))
            .ToListAsync(ct);

        var saleAllocs = await _db.SaleLotAllocations.AsNoTracking()
            .Where(a => lotIds.Contains(a.LotId))
            .ToListAsync(ct);

        var lossAllocs = await _db.LossLotAllocations.AsNoTracking()
            .Where(a => lotIds.Contains(a.LotId))
            .ToListAsync(ct);

        // تخصیصِ مصرف به Lot برای هر مصرفِ leg-linked نوشته می‌شود — از جمله مصرفی که از
        // اظهارنامهٔ گمرکی ساخته شده. ستون «گمرک» پایین‌تر همان مبلغ را از خودِ اظهارنامه
        // می‌گیرد، پس اینجا کنار می‌رود تا دو بار شمرده نشود. مصرفِ لغوشده هم پول نیست و
        // تخصیصِ نوشته‌شده‌اش باید در خواندن نادیده گرفته شود.
        var expenseAllocs = await _db.ExpenseLotAllocations.AsNoTracking()
            .Where(a => lotIds.Contains(a.LotId)
                && !_db.ExpenseTransactions.Any(e => e.Id == a.ExpenseTransactionId
                    && (e.IsCancelled || e.CustomsDeclarationId != null)))
            .ToListAsync(ct);

        // گمرکِ مرتبط: declarationهایی که TransportLegId آن‌ها در legهای همین کشتی است.
        var legIds = movements
            .Select(m => m.SourceReferenceType == InventoryLineageWriter.LegRef
                ? m.SourceReferenceId
                : m.VehicleRefType == InventoryLineageWriter.LegRef
                    ? m.VehicleRefId
                    : null)
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .Distinct()
            .ToList();
        // گمرک یک منبع دارد: خودِ اظهارنامه. CustomsDeclarationExpenseSync فقط برای گروهی
        // مصرف می‌سازد که هویتِ تسویه‌اش انتخاب شده باشد، پس جمع‌زدن از روی مصرف‌ها گروهِ
        // «طبقه‌بندی‌نشده» را خاموش از هزینه می‌انداخت. TotalUsd همان جمعِ کاملِ اقلام است.
        var customsUsd = legIds.Count == 0
            ? 0m
            : await _db.CustomsDeclarations.AsNoTracking()
                .Where(c => c.TransportLegId != null && legIds.Contains(c.TransportLegId.Value))
                .SumAsync(c => (decimal?)c.TotalUsd, ct) ?? 0m;

        var vesselInboundMt = lots
            .Where(l => l.SourceType == InventoryLotSourceType.VesselInbound)
            .Sum(l => l.QuantityMt);

        var receivedMt = movements
            .Where(m => m.Status == InventoryLotMovementStatus.Received)
            .Sum(m => m.ReceivedQuantityMt ?? 0m);

        var inTransitMt = movements
            .Where(m => m.Status is InventoryLotMovementStatus.Loaded or InventoryLotMovementStatus.InTransit)
            .Sum(m => m.LoadedQuantityMt);

        var inStockByTank = lots
            .Where(l => l.Status == InventoryLotStatus.Open && l.RemainingQuantityMt > 0m)
            .GroupBy(l => new { l.TerminalId, l.StorageTankId })
            .Select(g => new LineageTankStock(g.Key.TerminalId, g.Key.StorageTankId, g.Sum(x => x.RemainingQuantityMt)))
            .ToList();

        var soldMt = saleAllocs.Sum(a => a.QuantityMt);
        var soldUsd = saleAllocs.Sum(a => a.AmountUsd ?? 0m);

        // برای جلوگیری از double count: کسری فقط از LossLotAllocation خوانده می‌شود (نه shortage حرکت‌ها).
        // سپس کسری/مصرفِ shipment-linked که هنوز allocation ندارد به‌صورت fallback (Estimated) اضافه می‌شود
        // تا «missing sale/expense/loss» رخ ندهد. هر رویداد فقط یک‌بار شمرده می‌شود.
        var lossMt = lossAllocs.Sum(a => a.QuantityMt);
        var expenseUsd = expenseAllocs.Sum(a => a.AmountUsd);

        var allocatedLossIds = lossAllocs.Select(a => a.LossEventId).Distinct().ToHashSet();
        var fallbackLossMt = await _db.LossEvents.AsNoTracking()
            .Where(e => e.ShipmentId == shipmentId && !e.IsCancelled && !allocatedLossIds.Contains(e.Id))
            .SumAsync(e => (decimal?)e.DifferenceQuantityMt, ct) ?? 0m;

        var allocatedExpenseIds = expenseAllocs.Select(a => a.ExpenseTransactionId).Distinct().ToHashSet();
        // مصرفِ گمرک بالا یک بار از خودِ اظهارنامه شمرده شد؛ اینجا دوباره نمی‌آید.
        var fallbackExpenseUsd = await _db.ExpenseTransactions.AsNoTracking()
            .Where(e => e.ShipmentId == shipmentId
                && !e.IsCancelled
                && !e.CustomsDeclarationId.HasValue
                && e.AmountUsd > 0m
                && !allocatedExpenseIds.Contains(e.Id))
            .SumAsync(e => (decimal?)e.AmountUsd, ct) ?? 0m;

        var usedFallback = fallbackLossMt > 0m || fallbackExpenseUsd > 0m;
        lossMt += fallbackLossMt;
        expenseUsd += fallbackExpenseUsd;

        // منشأ مخزن مخلوط: هر Lot چقدر از کدام کشتی/قرارداد دارد (برای تب موجودی).
        var stockSources = lots
            .Where(l => l.Status == InventoryLotStatus.Open && l.RemainingQuantityMt > 0m)
            .GroupBy(l => new { l.RootShipmentId, l.RootContractId })
            .Select(g => new LineageStockSource(g.Key.RootShipmentId, g.Key.RootContractId, g.Sum(x => x.RemainingQuantityMt)))
            .ToList();

        // خلاصهٔ اطمینان: از همهٔ Lotها + allocationها.
        var confidences = lots.Select(l => l.LineageConfidence)
            .Concat(saleAllocs.Select(a => a.LineageConfidence))
            .Concat(lossAllocs.Select(a => a.LineageConfidence))
            .Concat(expenseAllocs.Select(a => a.LineageConfidence))
            .Concat(movements.Select(m => m.LineageConfidence))
            .ToList();

        var needsReview = confidences.Count(c => c == LineageConfidence.NeedsReview);
        var hasVerified = confidences.Any(c => c == LineageConfidence.Verified);
        var hasEstimated = usedFallback || confidences.Any(c => c == LineageConfidence.Estimated);
        var hasLegacy = confidences.Any(c => c == LineageConfidence.Legacy);

        var overall = needsReview > 0 ? LineageConfidence.NeedsReview
            : hasLegacy ? LineageConfidence.Legacy
            : hasEstimated ? LineageConfidence.Estimated
            : LineageConfidence.Verified;

        var warnings = new List<string>();
        if (needsReview > 0) warnings.Add($"{needsReview} مورد نسب‌نامه نیازمند بازبینی است.");
        if (hasLegacy) warnings.Add("بخشی از داده‌ها قدیمی‌اند و نسب‌نامهٔ کامل ندارند.");

        return new ShipmentLineageRollup
        {
            HasLineageData = true,
            CargoInVesselMt = Round(vesselInboundMt),
            ArrivedReceivedMt = Round(receivedMt),
            InTransitMt = Round(inTransitMt),
            InStockMt = Round(inStockByTank.Sum(s => s.QuantityMt)),
            SoldMt = Round(soldMt),
            SoldUsd = Round(soldUsd),
            LossMt = Round(lossMt),
            ExpenseUsd = Round(expenseUsd),
            CustomsUsd = Round(customsUsd),
            TankStock = inStockByTank,
            StockSources = stockSources,
            LotCount = lots.Count,
            MovementCount = movements.Count,
            SaleAllocationCount = saleAllocs.Count,
            OverallConfidence = overall,
            HasVerified = hasVerified,
            HasEstimated = hasEstimated,
            HasLegacy = hasLegacy,
            NeedsReviewCount = needsReview,
            Warnings = warnings
        };
    }

    private static decimal Round(decimal value) => decimal.Round(value, 4, MidpointRounding.AwayFromZero);
}

public sealed record LineageTankStock(int TerminalId, int? StorageTankId, decimal QuantityMt);
public sealed record LineageStockSource(int? RootShipmentId, int? RootContractId, decimal QuantityMt);

public sealed class ShipmentLineageRollup
{
    public bool HasLineageData { get; init; }
    public decimal CargoInVesselMt { get; init; }
    public decimal ArrivedReceivedMt { get; init; }
    public decimal InTransitMt { get; init; }
    public decimal InStockMt { get; init; }
    public decimal SoldMt { get; init; }
    public decimal SoldUsd { get; init; }
    public decimal LossMt { get; init; }
    public decimal ExpenseUsd { get; init; }
    public decimal CustomsUsd { get; init; }
    public IReadOnlyList<LineageTankStock> TankStock { get; init; } = [];
    public IReadOnlyList<LineageStockSource> StockSources { get; init; } = [];
    public int LotCount { get; init; }
    public int MovementCount { get; init; }
    public int SaleAllocationCount { get; init; }
    public LineageConfidence OverallConfidence { get; init; }
    public bool HasVerified { get; init; }
    public bool HasEstimated { get; init; }
    public bool HasLegacy { get; init; }
    public int NeedsReviewCount { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
}
