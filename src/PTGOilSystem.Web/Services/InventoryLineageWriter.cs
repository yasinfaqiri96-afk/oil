using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PTGOilSystem.Web.Configuration;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;

namespace PTGOilSystem.Web.Services;

/// <summary>
/// تک‌منبعِ منطقِ نوشتنِ لایهٔ Inventory Lineage: ساخت Lot، مصرف FIFO از Lotها،
/// ثبت حرکت نسب‌نامه‌ای، و تخصیص فروش/کسری/مصرف به Lot.
///
/// هیچ تراکنشی باز نمی‌کند؛ caller مسئول تراکنش است (مثل بقیهٔ سرویس‌های دامنه).
/// هیچ InventoryMovement/Ledger/Sale را تغییر نمی‌دهد — فقط رکوردهای لایهٔ Lineage را insert/به‌روزرسانی می‌کند.
/// با flag «Lineage:WriteLots=false» همهٔ متدها no-op هستند تا رفتار سیستم دقیقاً مثل قبل بماند.
/// </summary>
public interface IInventoryLineageWriter
{
    bool Enabled { get; }

    Task<InventoryLot> CreateLotAsync(LotCreationRequest request, CancellationToken ct = default);

    Task<LotConsumptionResult> ConsumeFifoAsync(LotConsumeRequest request, CancellationToken ct = default);

    // hookها — هرکدام داخل خودشان flag را چک می‌کنند؛ caller می‌تواند بدون نگرانی صدا بزند.
    Task OnLegLoadedAsync(InventoryTransportLeg leg, InventoryMovement outboundMovement, CancellationToken ct = default);

    // legی که چند سهم منبع دارد، چند سند خروجی می‌سازد (یکی به‌ازای هر قرارداد). نسب‌نامه همچنان
    // در سطح leg ساخته می‌شود؛ این overload فقط اجازه می‌دهد آن legها هم بدون انتخابِ دلبخواهیِ
    // «سند اصلی» ثبت شوند. رفتار تک‌سندی دقیقاً مثل قبل است.
    Task OnLegLoadedAsync(InventoryTransportLeg leg, IReadOnlyList<InventoryMovement> outboundMovements, CancellationToken ct = default);

    // برگشتِ اثرِ بارگیریِ یک leg روی نسب‌نامه: حرکت‌های Lot همان leg حذف و مقدارِ مصرف‌شده به
    // Lotهای مبدأ برگردانده می‌شود. برای ویرایش/لغوِ حملی لازم است که هنوز رسید نخورده؛ بدون آن
    // Lotها برای همیشه مصرف‌شده می‌مانند و نسب‌نامه از موجودی واقعی جدا می‌افتد. idempotent است.
    Task OnLegLoadReversedAsync(InventoryTransportLeg leg, CancellationToken ct = default);
    Task OnLegReceiptAsync(InventoryTransportLeg leg, InventoryTransportReceipt receipt, InventoryMovement? inboundMovement, LossEvent? shortageLoss, CancellationToken ct = default);
    Task OnDirectSaleAsync(InventoryTransportLeg leg, SalesTransaction sale, CancellationToken ct = default);
    Task AllocateSaleAsync(SalesTransaction sale, int? sourcePurchaseContractId, int terminalId, int? storageTankId, CancellationToken ct = default);
    Task AllocateLossToLotsAsync(LossEvent loss, int? terminalId, int? storageTankId, CancellationToken ct = default);
    Task AllocateExpenseToShipmentLotsAsync(ExpenseTransaction expense, CancellationToken ct = default);
}

public sealed record LotCreationRequest(
    int ProductId,
    int TerminalId,
    int? StorageTankId,
    decimal QuantityMt,
    InventoryLotSourceType SourceType,
    LineageConfidence Confidence,
    int? RootShipmentId = null,
    int? RootContractId = null,
    int? SupplierId = null,
    int? ParentLotId = null,
    int? CreatedFromMovementId = null,
    string? SourceReferenceType = null,
    int? SourceReferenceId = null,
    DateTime? CreatedAt = null,
    string? Notes = null);

public sealed record LotConsumeRequest(
    int ProductId,
    int TerminalId,
    int? StorageTankId,
    int? PreferredContractId,
    decimal QuantityMt,
    DateTime AsOf);

public sealed record LotConsumption(InventoryLot Lot, decimal QuantityMt);

public sealed record LotConsumptionResult(IReadOnlyList<LotConsumption> Consumptions, decimal Shortfall)
{
    public bool FullyAllocated => Shortfall <= 0.0001m;
}

public sealed class InventoryLineageWriter : IInventoryLineageWriter
{
    private readonly ApplicationDbContext _db;
    private readonly LineageOptions _options;

    public InventoryLineageWriter(ApplicationDbContext db, IOptions<LineageOptions> options)
    {
        _db = db;
        _options = options.Value;
    }

    public bool Enabled => _options.WriteLots;

    public async Task<InventoryLot> CreateLotAsync(LotCreationRequest r, CancellationToken ct = default)
    {
        var lot = new InventoryLot
        {
            ProductId = r.ProductId,
            TerminalId = r.TerminalId,
            StorageTankId = r.StorageTankId,
            QuantityMt = Round(r.QuantityMt),
            RemainingQuantityMt = Round(r.QuantityMt),
            RootShipmentId = r.RootShipmentId,
            RootContractId = r.RootContractId,
            SupplierId = r.SupplierId,
            ParentLotId = r.ParentLotId,
            SourceType = r.SourceType,
            SourceReferenceType = r.SourceReferenceType,
            SourceReferenceId = r.SourceReferenceId,
            CreatedFromMovementId = r.CreatedFromMovementId,
            Status = r.QuantityMt > 0m ? InventoryLotStatus.Open : InventoryLotStatus.Depleted,
            LineageConfidence = r.Confidence,
            CreatedAt = r.CreatedAt ?? DateTime.UtcNow,
            Notes = r.Notes
        };

        if (!Enabled)
        {
            return lot;
        }

        _db.InventoryLots.Add(lot);
        await _db.SaveChangesAsync(ct);
        return lot;
    }

    public async Task<LotConsumptionResult> ConsumeFifoAsync(LotConsumeRequest r, CancellationToken ct = default)
    {
        var remaining = Round(r.QuantityMt);
        if (!Enabled)
        {
            return new LotConsumptionResult(Array.Empty<LotConsumption>(), Math.Max(remaining, 0m));
        }

        if (remaining <= 0m)
        {
            return new LotConsumptionResult(Array.Empty<LotConsumption>(), 0m);
        }

        var candidates = await LoadOpenLotsAsync(r.ProductId, r.TerminalId, r.StorageTankId, ct);
        var ordered = await OrderFifoAsync(candidates, r.PreferredContractId, ct);

        var taken = new List<LotConsumption>();
        foreach (var lot in ordered)
        {
            if (remaining <= 0m) break;
            var available = lot.RemainingQuantityMt;
            if (available <= 0m) continue;

            var take = Math.Min(available, remaining);
            lot.RemainingQuantityMt = Round(available - take);
            if (lot.RemainingQuantityMt <= 0.0001m)
            {
                lot.RemainingQuantityMt = 0m;
                lot.Status = InventoryLotStatus.Depleted;
            }
            lot.UpdatedAtUtc = DateTime.UtcNow;
            taken.Add(new LotConsumption(lot, Round(take)));
            remaining = Round(remaining - take);
        }

        if (taken.Count > 0)
        {
            await _db.SaveChangesAsync(ct);
        }

        return new LotConsumptionResult(taken, Math.Max(remaining, 0m));
    }

    public Task OnLegLoadedAsync(InventoryTransportLeg leg, InventoryMovement outboundMovement, CancellationToken ct = default)
        => OnLegLoadedAsync(leg, [outboundMovement], ct);

    public async Task OnLegLoadedAsync(InventoryTransportLeg leg, IReadOnlyList<InventoryMovement> outboundMovements, CancellationToken ct = default)
    {
        if (!Enabled) return;

        // اگر قبلاً برای این leg حرکت نسب‌نامه‌ای ساخته شده، دوباره نساز (idempotent).
        var exists = await _db.InventoryLotMovements.AnyAsync(
            m => m.SourceReferenceType == LegRef && m.SourceReferenceId == leg.Id, ct);
        if (exists) return;

        var consume = await ConsumeFifoAsync(new LotConsumeRequest(
            leg.ProductId, leg.SourceTerminalId, leg.SourceStorageTankId,
            leg.SourcePurchaseContractId, leg.QuantityMt, leg.LoadedDate), ct);

        var sources = consume.Consumptions.ToList();
        if (consume.Shortfall > 0.0001m)
        {
            var isRootVesselLoad = leg.TransportType == LoadingTransportType.Vessel && leg.ShipmentId.HasValue;
            var fallbackLot = await CreateLotAsync(new LotCreationRequest(
                leg.ProductId, leg.SourceTerminalId, leg.SourceStorageTankId, consume.Shortfall,
                isRootVesselLoad ? InventoryLotSourceType.VesselInbound : InventoryLotSourceType.LegacyOpening,
                isRootVesselLoad ? LineageConfidence.Verified : LineageConfidence.NeedsReview,
                RootShipmentId: leg.ShipmentId, RootContractId: leg.SourcePurchaseContractId,
                SourceReferenceType: LegRef, SourceReferenceId: leg.Id,
                CreatedAt: leg.LoadedDate,
                Notes: isRootVesselLoad ? "Root vessel cargo lot" : "Legacy source lot (lineage stock shortfall at load)"), ct);
            fallbackLot.RemainingQuantityMt = 0m;
            fallbackLot.Status = InventoryLotStatus.Depleted;
            sources.Add(new LotConsumption(fallbackLot, Round(consume.Shortfall)));
        }

        foreach (var source in sources)
        {
            _db.InventoryLotMovements.Add(new InventoryLotMovement
            {
                FromLotId = source.Lot.Id,
                ToLotId = null,
                MovementKind = KindFor(leg.TransportType),
                FromTerminalId = leg.SourceTerminalId,
                FromStorageTankId = leg.SourceStorageTankId,
                ToTerminalId = leg.DestinationTerminalId,
                ToStorageTankId = leg.DestinationStorageTankId,
                VehicleType = leg.TransportType,
                VehicleRefType = LegRef,
                VehicleRefId = leg.Id,
                LoadedQuantityMt = source.QuantityMt,
                MovementDate = leg.LoadedDate,
                Status = InventoryLotMovementStatus.Loaded,
                ShipmentId = leg.ShipmentId,
                SourceReferenceType = LegRef,
                SourceReferenceId = leg.Id,
                // اشارهٔ برگشتی به سند موجودی. وقتی leg چند سهم منبع دارد چند سند خروجی وجود
                // دارد و هیچ‌کدام «سند اصلی» نیست، پس این اشاره خالی می‌ماند تا یکی از آن‌ها
                // به‌غلط نمایندهٔ بقیه نشود. پیوند اصلی همیشه SourceReferenceId = leg.Id است و
                // همهٔ خواننده‌های نسب‌نامه از همان استفاده می‌کنند.
                InventoryMovementId = outboundMovements.Count == 1 ? outboundMovements[0].Id : null,
                LineageConfidence = source.Lot.LineageConfidence,
                Notes = source.Lot.LineageConfidence == LineageConfidence.NeedsReview
                    ? "Source quantity needs lineage review"
                    : null
            });
        }
        await _db.SaveChangesAsync(ct);
    }

    public async Task OnLegLoadReversedAsync(InventoryTransportLeg leg, CancellationToken ct = default)
    {
        if (!Enabled) return;

        var movements = await _db.InventoryLotMovements
            .Where(m => m.SourceReferenceType == LegRef && m.SourceReferenceId == leg.Id)
            .ToListAsync(ct);
        if (movements.Count == 0) return;

        // Lotهایی که خودِ همین leg ساخته (کسریِ نسب‌نامه هنگام بارگیری) بازگردانده نمی‌شوند؛
        // اصلاً وجود نداشتند و باید با همان حرکت‌ها حذف شوند، نه اینکه موجودیِ ساختگی بسازند.
        var ownLots = await _db.InventoryLots
            .Where(l => l.SourceReferenceType == LegRef && l.SourceReferenceId == leg.Id)
            .ToListAsync(ct);
        var ownLotIds = ownLots.Select(l => l.Id).ToHashSet();

        var restoreByLotId = new Dictionary<int, decimal>();
        foreach (var movement in movements)
        {
            if (movement.FromLotId is not { } fromLotId || ownLotIds.Contains(fromLotId))
            {
                continue;
            }
            restoreByLotId[fromLotId] = restoreByLotId.GetValueOrDefault(fromLotId) + movement.LoadedQuantityMt;
        }

        if (restoreByLotId.Count > 0)
        {
            var lotIds = restoreByLotId.Keys.ToList();
            var lots = await _db.InventoryLots.Where(l => lotIds.Contains(l.Id)).ToListAsync(ct);
            foreach (var lot in lots)
            {
                lot.RemainingQuantityMt = Round(lot.RemainingQuantityMt + restoreByLotId[lot.Id]);
                if (lot.RemainingQuantityMt > 0.0001m)
                {
                    lot.Status = InventoryLotStatus.Open;
                }
                lot.UpdatedAtUtc = DateTime.UtcNow;
            }
        }

        _db.InventoryLotMovements.RemoveRange(movements);
        if (ownLots.Count > 0)
        {
            _db.InventoryLots.RemoveRange(ownLots);
        }
        await _db.SaveChangesAsync(ct);
    }

    public async Task OnLegReceiptAsync(InventoryTransportLeg leg, InventoryTransportReceipt receipt, InventoryMovement? inboundMovement, LossEvent? shortageLoss, CancellationToken ct = default)
    {
        if (!Enabled) return;
        if (receipt.ReceiptDestination != InventoryTransportReceiptDestination.ToInventory) return;
        if (await _db.InventoryLots.AnyAsync(
                l => l.SourceReferenceType == ReceiptRef && l.SourceReferenceId == receipt.Id, ct)) return;

        var movements = await _db.InventoryLotMovements
            .Where(
            m => m.SourceReferenceType == LegRef && m.SourceReferenceId == leg.Id
                 && m.Status != InventoryLotMovementStatus.Received)
            .OrderBy(m => m.Id)
            .ToListAsync(ct);

        var destTerminalId = receipt.DestinationTerminalId ?? leg.DestinationTerminalId ?? leg.SourceTerminalId;
        if (movements.Count == 0)
        {
            await CreateLotAsync(new LotCreationRequest(
                leg.ProductId, destTerminalId, receipt.DestinationStorageTankId ?? leg.DestinationStorageTankId,
                receipt.ReceivedQuantityMt, InventoryLotSourceType.TransportReceipt, LineageConfidence.Estimated,
                RootShipmentId: leg.ShipmentId, RootContractId: leg.SourcePurchaseContractId,
                CreatedFromMovementId: inboundMovement?.Id,
                SourceReferenceType: ReceiptRef, SourceReferenceId: receipt.Id,
                CreatedAt: receipt.ReceiptDate, Notes: "Receipt had no prior lineage movement"), ct);
            if (shortageLoss is not null)
            {
                await AllocateLossToLotsAsync(shortageLoss, destTerminalId, receipt.DestinationStorageTankId, ct);
            }
            return;
        }

        var fromLotIds = movements.Where(m => m.FromLotId.HasValue).Select(m => m.FromLotId!.Value).Distinct().ToList();
        var fromLots = await _db.InventoryLots.Where(l => fromLotIds.Contains(l.Id)).ToDictionaryAsync(l => l.Id, ct);
        var basis = movements.Sum(m => m.LoadedQuantityMt);
        decimal receivedAllocated = 0m;
        decimal shortageAllocated = 0m;
        var hasLossAllocation = shortageLoss is not null
            && await _db.LossLotAllocations.AnyAsync(a => a.LossEventId == shortageLoss.Id, ct);

        for (var i = 0; i < movements.Count; i++)
        {
            var movement = movements[i];
            fromLots.TryGetValue(movement.FromLotId ?? 0, out var fromLot);
            var receivedShare = i == movements.Count - 1
                ? Round(receipt.ReceivedQuantityMt - receivedAllocated)
                : Round(receipt.ReceivedQuantityMt * SafeRatio(movement.LoadedQuantityMt, basis));
            var shortageShare = i == movements.Count - 1
                ? Round(receipt.ShortageQuantityMt - shortageAllocated)
                : Round(receipt.ShortageQuantityMt * SafeRatio(movement.LoadedQuantityMt, basis));
            receivedAllocated += receivedShare;
            shortageAllocated += shortageShare;

            InventoryLot? toLot = null;
            if (receivedShare > 0m)
            {
                toLot = await CreateLotAsync(new LotCreationRequest(
                    leg.ProductId, destTerminalId, receipt.DestinationStorageTankId ?? leg.DestinationStorageTankId,
                    receivedShare, InventoryLotSourceType.TransportReceipt,
                    fromLot?.LineageConfidence ?? movement.LineageConfidence,
                    RootShipmentId: fromLot?.RootShipmentId ?? leg.ShipmentId,
                    RootContractId: fromLot?.RootContractId ?? leg.SourcePurchaseContractId,
                    SupplierId: fromLot?.SupplierId, ParentLotId: fromLot?.Id,
                    CreatedFromMovementId: i == 0 ? inboundMovement?.Id : null,
                    SourceReferenceType: ReceiptRef, SourceReferenceId: receipt.Id,
                    CreatedAt: receipt.ReceiptDate, Notes: "Transport receipt destination lot"), ct);
            }

            movement.ToLotId = toLot?.Id;
            movement.ToTerminalId = destTerminalId;
            movement.ToStorageTankId = receipt.DestinationStorageTankId ?? leg.DestinationStorageTankId;
            movement.ReceivedQuantityMt = receivedShare;
            movement.ShortageQuantityMt = shortageShare;
            movement.Status = InventoryLotMovementStatus.Received;
            movement.UpdatedAtUtc = DateTime.UtcNow;

            if (!hasLossAllocation && shortageLoss is not null && fromLot is not null && shortageShare > 0m)
            {
                _db.LossLotAllocations.Add(new LossLotAllocation
                {
                    LossEventId = shortageLoss.Id,
                    LotId = fromLot.Id,
                    QuantityMt = shortageShare,
                    AllocationMethod = LotAllocationMethod.Proportional,
                    LineageConfidence = MapLossConfidence(shortageLoss.LossCertainty)
                });
            }
        }
        await _db.SaveChangesAsync(ct);
    }

    public async Task OnDirectSaleAsync(InventoryTransportLeg leg, SalesTransaction sale, CancellationToken ct = default)
    {
        if (!Enabled) return;
        if (await _db.SaleLotAllocations.AnyAsync(a => a.SalesTransactionId == sale.Id, ct)) return;

        // فروش مستقیم از leg بین همه Lotهای منبع همان leg تقسیم می‌شود.
        var sources = await _db.InventoryLotMovements
            .Where(m => m.SourceReferenceType == LegRef && m.SourceReferenceId == leg.Id && m.FromLotId != null)
            .Select(m => new { Lot = m.FromLot!, m.LoadedQuantityMt, m.LineageConfidence })
            .ToListAsync(ct);

        if (sources.Count == 0)
        {
            var lotId = await EnsureDirectSaleLotAsync(leg, sale, ct);
            _db.SaleLotAllocations.Add(new SaleLotAllocation
            {
                SalesTransactionId = sale.Id, LotId = lotId, QuantityMt = Round(sale.QuantityMt),
                AmountUsd = Round(sale.TotalUsd), UnitCostUsd = leg.PurchaseUnitCostUsd,
                AllocationMethod = LotAllocationMethod.Manual, LineageConfidence = LineageConfidence.Estimated,
                Notes = "Direct sale from transport leg"
            });
            await _db.SaveChangesAsync(ct);
            return;
        }

        var basis = sources.Sum(s => s.LoadedQuantityMt);
        decimal quantityAllocated = 0m;
        decimal amountAllocated = 0m;
        for (var i = 0; i < sources.Count; i++)
        {
            var quantityShare = i == sources.Count - 1
                ? Round(sale.QuantityMt - quantityAllocated)
                : Round(sale.QuantityMt * SafeRatio(sources[i].LoadedQuantityMt, basis));
            var amountShare = i == sources.Count - 1
                ? Round(sale.TotalUsd - amountAllocated)
                : Round(sale.TotalUsd * SafeRatio(sources[i].LoadedQuantityMt, basis));
            quantityAllocated += quantityShare;
            amountAllocated += amountShare;
            _db.SaleLotAllocations.Add(new SaleLotAllocation
            {
                SalesTransactionId = sale.Id, LotId = sources[i].Lot.Id,
                QuantityMt = quantityShare, AmountUsd = amountShare,
                UnitCostUsd = leg.PurchaseUnitCostUsd, AllocationMethod = LotAllocationMethod.Proportional,
                LineageConfidence = sources[i].LineageConfidence,
                Notes = "Direct sale from transport leg"
            });
        }
        await _db.SaveChangesAsync(ct);
    }

    public async Task AllocateSaleAsync(SalesTransaction sale, int? sourcePurchaseContractId, int terminalId, int? storageTankId, CancellationToken ct = default)
    {
        if (!Enabled) return;
        if (await _db.SaleLotAllocations.AnyAsync(a => a.SalesTransactionId == sale.Id, ct)) return;

        var consume = await ConsumeFifoAsync(new LotConsumeRequest(
            sale.ProductId, terminalId, storageTankId, sourcePurchaseContractId, sale.QuantityMt, sale.SaleDate), ct);

        var unitPrice = sale.QuantityMt > 0m ? sale.TotalUsd / sale.QuantityMt : 0m;
        SaleLotAllocation? lastAllocation = null;
        decimal amountAllocated = 0m;
        for (var i = 0; i < consume.Consumptions.Count; i++)
        {
            var c = consume.Consumptions[i];
            var isFinalAllocatedQuantity = consume.FullyAllocated && i == consume.Consumptions.Count - 1;
            var amountUsd = isFinalAllocatedQuantity
                ? Round(sale.TotalUsd - amountAllocated)
                : Round(unitPrice * c.QuantityMt);
            amountAllocated += amountUsd;
            lastAllocation = new SaleLotAllocation
            {
                SalesTransactionId = sale.Id,
                LotId = c.Lot.Id,
                QuantityMt = c.QuantityMt,
                AmountUsd = amountUsd,
                AllocationMethod = LotAllocationMethod.FIFO,
                LineageConfidence = c.Lot.LineageConfidence == LineageConfidence.Verified
                    ? LineageConfidence.Verified
                    : LineageConfidence.Estimated
            };
            _db.SaleLotAllocations.Add(lastAllocation);
        }

        if (!consume.FullyAllocated && consume.Shortfall > 0.0001m)
        {
            var note = $"Lineage shortfall {consume.Shortfall:N4} MT for sale #{sale.Id} — needs review";
            var fallbackLot = await CreateLotAsync(new LotCreationRequest(
                sale.ProductId, terminalId, storageTankId, consume.Shortfall,
                InventoryLotSourceType.LegacyOpening, LineageConfidence.NeedsReview,
                RootShipmentId: sale.ShipmentId,
                RootContractId: sourcePurchaseContractId,
                SourceReferenceType: SaleRef,
                SourceReferenceId: sale.Id,
                CreatedAt: sale.SaleDate,
                Notes: note), ct);
            fallbackLot.RemainingQuantityMt = 0m;
            fallbackLot.Status = InventoryLotStatus.Depleted;
            fallbackLot.UpdatedAtUtc = DateTime.UtcNow;

            lastAllocation = new SaleLotAllocation
            {
                SalesTransactionId = sale.Id,
                LotId = fallbackLot.Id,
                QuantityMt = consume.Shortfall,
                AmountUsd = Round(sale.TotalUsd - amountAllocated),
                AllocationMethod = LotAllocationMethod.LegacyEstimated,
                LineageConfidence = LineageConfidence.NeedsReview,
                Notes = note
            };
            _db.SaleLotAllocations.Add(lastAllocation);
        }

        await _db.SaveChangesAsync(ct);
    }

    public async Task AllocateLossToLotsAsync(LossEvent loss, int? terminalId, int? storageTankId, CancellationToken ct = default)
    {
        if (!Enabled) return;
        if (await _db.LossLotAllocations.AnyAsync(a => a.LossEventId == loss.Id, ct)) return;

        var quantity = loss.DifferenceQuantityMt > 0m ? loss.DifferenceQuantityMt : loss.ChargeableLossMt;
        if (quantity <= 0m) return;

        // اگر leg-linked است، روی Lotهای همان leg/مقصد؛ وگرنه proportional روی Lotهای همان Shipment.
        List<InventoryLot> targetLots;
        if (loss.ShipmentId.HasValue)
        {
            targetLots = await _db.InventoryLots
                .Where(l => l.RootShipmentId == loss.ShipmentId.Value)
                .ToListAsync(ct);
        }
        else if (terminalId.HasValue)
        {
            targetLots = await LoadOpenLotsAsync(loss.ProductId, terminalId.Value, storageTankId, ct);
        }
        else
        {
            targetLots = new List<InventoryLot>();
        }

        if (targetLots.Count == 0) return;

        var confidence = MapLossConfidence(loss.LossCertainty);
        var totalBasis = targetLots.Sum(l => l.QuantityMt);
        if (totalBasis <= 0m) return;

        decimal allocated = 0m;
        for (var i = 0; i < targetLots.Count; i++)
        {
            var lot = targetLots[i];
            var share = i == targetLots.Count - 1
                ? quantity - allocated
                : Round(quantity * (lot.QuantityMt / totalBasis));
            allocated += share;
            if (share <= 0m) continue;

            _db.LossLotAllocations.Add(new LossLotAllocation
            {
                LossEventId = loss.Id,
                LotId = lot.Id,
                QuantityMt = Round(share),
                AllocationMethod = LotAllocationMethod.Proportional,
                LineageConfidence = confidence
            });
        }
        await _db.SaveChangesAsync(ct);
    }

    public async Task AllocateExpenseToShipmentLotsAsync(ExpenseTransaction expense, CancellationToken ct = default)
    {
        if (!Enabled) return;
        if (await _db.ExpenseLotAllocations.AnyAsync(a => a.ExpenseTransactionId == expense.Id, ct)) return;
        if (expense.AmountUsd <= 0m) return;

        // اگر leg-linked است → Lotهای همان leg؛ وگرنه shipment-level → proportional روی Lotهای shipment.
        List<InventoryLot> targetLots;
        LineageConfidence confidence;
        if (expense.TransportLegId.HasValue)
        {
            var lotIds = await _db.InventoryLotMovements
                .Where(m => m.SourceReferenceType == LegRef && m.SourceReferenceId == expense.TransportLegId.Value)
                .Select(m => m.ToLotId ?? m.FromLotId)
                .Where(id => id != null)
                .Select(id => id!.Value)
                .ToListAsync(ct);
            targetLots = await _db.InventoryLots.Where(l => lotIds.Contains(l.Id)).ToListAsync(ct);
            confidence = LineageConfidence.Verified;
        }
        else if (expense.ShipmentId.HasValue)
        {
            targetLots = await _db.InventoryLots.Where(l => l.RootShipmentId == expense.ShipmentId.Value).ToListAsync(ct);
            confidence = LineageConfidence.Estimated;
        }
        else
        {
            return;
        }

        if (targetLots.Count == 0) return;
        var totalBasis = targetLots.Sum(l => l.QuantityMt);
        if (totalBasis <= 0m)
        {
            // تقسیم مساوی اگر مبنای مقداری صفر است.
            var each = Round(expense.AmountUsd / targetLots.Count);
            foreach (var lot in targetLots)
            {
                _db.ExpenseLotAllocations.Add(new ExpenseLotAllocation
                {
                    ExpenseTransactionId = expense.Id, LotId = lot.Id,
                    AmountUsd = each, AllocationMethod = LotAllocationMethod.Proportional, LineageConfidence = confidence
                });
            }
            await _db.SaveChangesAsync(ct);
            return;
        }

        decimal allocated = 0m;
        for (var i = 0; i < targetLots.Count; i++)
        {
            var lot = targetLots[i];
            var share = i == targetLots.Count - 1
                ? expense.AmountUsd - allocated
                : Round(expense.AmountUsd * (lot.QuantityMt / totalBasis));
            allocated += share;
            _db.ExpenseLotAllocations.Add(new ExpenseLotAllocation
            {
                ExpenseTransactionId = expense.Id, LotId = lot.Id,
                AmountUsd = Round(share), AllocationMethod = LotAllocationMethod.Proportional, LineageConfidence = confidence
            });
        }
        await _db.SaveChangesAsync(ct);
    }

    // ===================== internals =====================

    public const string LegRef = "InventoryTransportLeg";
    public const string ReceiptRef = "InventoryTransportReceipt";
    public const string SaleRef = "SalesTransaction";

    private async Task AllocateLossToSpecificLotAsync(LossEvent loss, InventoryLot lot, decimal quantity, CancellationToken ct)
    {
        if (await _db.LossLotAllocations.AnyAsync(a => a.LossEventId == loss.Id, ct)) return;
        _db.LossLotAllocations.Add(new LossLotAllocation
        {
            LossEventId = loss.Id,
            LotId = lot.Id,
            QuantityMt = Round(quantity),
            AllocationMethod = LotAllocationMethod.Manual,
            LineageConfidence = MapLossConfidence(loss.LossCertainty)
        });
        await _db.SaveChangesAsync(ct);
    }

    private async Task<int> EnsureDirectSaleLotAsync(InventoryTransportLeg leg, SalesTransaction sale, CancellationToken ct)
    {
        var lot = await CreateLotAsync(new LotCreationRequest(
            leg.ProductId, leg.DestinationTerminalId ?? leg.SourceTerminalId, leg.DestinationStorageTankId,
            sale.QuantityMt, InventoryLotSourceType.DirectReceipt, LineageConfidence.Estimated,
            RootShipmentId: leg.ShipmentId, RootContractId: leg.SourcePurchaseContractId,
            SourceReferenceType: LegRef, SourceReferenceId: leg.Id,
            CreatedAt: sale.SaleDate, Notes: "Synthetic lot for direct sale"), ct);
        // فروش مستقیم بلافاصله مصرف می‌شود.
        lot.RemainingQuantityMt = 0m;
        lot.Status = InventoryLotStatus.Depleted;
        await _db.SaveChangesAsync(ct);
        return lot.Id;
    }

    private async Task<List<InventoryLot>> LoadOpenLotsAsync(int productId, int terminalId, int? storageTankId, CancellationToken ct)
    {
        var query = _db.InventoryLots
            .Where(l => l.ProductId == productId
                        && l.TerminalId == terminalId
                        && l.Status == InventoryLotStatus.Open
                        && l.RemainingQuantityMt > 0m);
        if (storageTankId.HasValue)
        {
            query = query.Where(l => l.StorageTankId == storageTankId.Value);
        }
        return await query.ToListAsync(ct);
    }

    // ترتیب FIFO دقیقاً مثل EnsureSufficientTerminalStockAsync فروش:
    // قرارداد ترجیحی → ContractDate → ContractNumber → ContractId → Lot.CreatedAt → Id.
    private async Task<List<InventoryLot>> OrderFifoAsync(List<InventoryLot> lots, int? preferredContractId, CancellationToken ct)
    {
        var contractIds = lots.Where(l => l.RootContractId.HasValue).Select(l => l.RootContractId!.Value).Distinct().ToList();
        var contracts = contractIds.Count == 0
            ? new Dictionary<int, (DateTime Date, string Number)>()
            : await _db.Contracts.AsNoTracking()
                .Where(c => contractIds.Contains(c.Id))
                .Select(c => new { c.Id, c.ContractDate, c.ContractNumber })
                .ToDictionaryAsync(c => c.Id, c => (Date: c.ContractDate, Number: c.ContractNumber ?? string.Empty), ct);

        return lots
            .OrderByDescending(l => preferredContractId.HasValue && l.RootContractId == preferredContractId.Value)
            .ThenBy(l => l.RootContractId.HasValue && contracts.TryGetValue(l.RootContractId.Value, out var c1) ? c1.Date : DateTime.MaxValue)
            .ThenBy(l => l.RootContractId.HasValue && contracts.TryGetValue(l.RootContractId.Value, out var c2) ? c2.Number : "~")
            .ThenBy(l => l.RootContractId ?? int.MaxValue)
            .ThenBy(l => l.CreatedAt)
            .ThenBy(l => l.Id)
            .ToList();
    }

    private static InventoryLotMovementKind KindFor(LoadingTransportType type) => type switch
    {
        LoadingTransportType.Vessel => InventoryLotMovementKind.VesselInbound,
        LoadingTransportType.Wagon => InventoryLotMovementKind.WagonLeg,
        LoadingTransportType.Truck => InventoryLotMovementKind.TruckLeg,
        _ => InventoryLotMovementKind.TankTransfer
    };

    private static LineageConfidence MapLossConfidence(LossCertaintyLevel? certainty) => certainty switch
    {
        LossCertaintyLevel.Measured => LineageConfidence.Verified,
        LossCertaintyLevel.Estimated => LineageConfidence.Estimated,
        _ => LineageConfidence.Estimated
    };

    private static decimal Round(decimal value) => decimal.Round(value, 4, MidpointRounding.AwayFromZero);
    private static decimal SafeRatio(decimal value, decimal total) => total > 0m ? value / total : 0m;
}

// سازندهٔ یک writerِ خاموش (WriteLots=false) برای call siteهایی که سرویس را بدون DI می‌سازند.
public static class InventoryLineageWriterFactory
{
    public static IInventoryLineageWriter Disabled(ApplicationDbContext db)
        => new InventoryLineageWriter(db, Options.Create(new LineageOptions()));
}
