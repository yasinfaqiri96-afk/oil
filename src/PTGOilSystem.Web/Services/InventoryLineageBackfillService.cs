using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;

namespace PTGOilSystem.Web.Services;

/// <summary>
/// بازسازی لایهٔ Inventory Lineage از دادهٔ تاریخی. idempotent است: چند بار اجرا duplicate نمی‌سازد.
/// هیچ InventoryMovement / Ledger / Sale / Expense موجود را تغییر نمی‌دهد — فقط Lot/LotMovement/allocation insert می‌کند.
/// از طریق flag «Lineage:BackfillEnabled» در Admin اجرا می‌شود؛ هرگز در startup خودکار نیست.
/// </summary>
public sealed class InventoryLineageBackfillService
{
    private readonly ApplicationDbContext _db;
    private readonly IInventoryLineageWriter _writer;

    public InventoryLineageBackfillService(ApplicationDbContext db, IInventoryLineageWriter writer)
    {
        _db = db;
        _writer = writer;
    }

    public async Task<InventoryLineageBackfillSummary> RunAsync(CancellationToken ct = default)
    {
        var summary = new InventoryLineageBackfillSummary();

        await BackfillInboundLotsAsync(summary, ct);   // قواعد ۱ و ۲: ریشه از vessel-leg/receipt و loading-receipt
        await BackfillTransferMovementsAsync(summary, ct); // قاعدهٔ ۳ و ۴: legهای بعدی + dispatch
        await BackfillFreeStockLotsAsync(summary, ct); // قاعدهٔ ۹: موجودی آزاد/افتتاحیه
        await BackfillSalesAsync(summary, ct);         // قاعدهٔ ۵
        await BackfillLossesAsync(summary, ct);        // قاعدهٔ ۶
        await BackfillExpensesAsync(summary, ct);      // قاعدهٔ ۷

        await RecomputeConfidenceSummaryAsync(summary, ct);
        return summary;
    }

    // قواعد ۱ و ۲ — ساخت Lotهای ورودی از رسیدهای ToInventory و allocationهای LoadingReceipt.
    private async Task BackfillInboundLotsAsync(InventoryLineageBackfillSummary s, CancellationToken ct)
    {
        // (الف) رسیدهای انتقال از موجودی (leg + receipt + movement مقصد). به‌ترتیب تاریخ تا
        // ریشه‌ها (تخلیهٔ کشتی) پیش از حمل‌های پایین‌دستی ساخته شوند و زنجیرهٔ FIFO درست بخواند.
        var receipts = await _db.InventoryTransportReceipts.AsNoTracking()
            .Where(r => !r.IsCancelled
                        && r.ReceiptDestination == InventoryTransportReceiptDestination.ToInventory
                        && r.InventoryMovementId != null)
            .OrderBy(r => r.ReceiptDate).ThenBy(r => r.Id)
            .ToListAsync(ct);

        foreach (var receipt in receipts)
        {
            if (await LotExistsAsync(InventoryLineageWriter.ReceiptRef, receipt.Id, ct)) continue;

            var leg = await _db.InventoryTransportLegs.AsNoTracking()
                .FirstOrDefaultAsync(l => l.Id == receipt.InventoryTransportLegId, ct);
            if (leg is null) continue;

            var isVessel = leg.TransportType == LoadingTransportType.Vessel;

            // برای حمل غیرکشتی، از Lot منبع FIFO مصرف می‌کنیم تا زنجیرهٔ چندمرحله‌ای درست بخواند
            // و موجودیِ مبدأ دوبار شمرده نشود. ریشه از Lot منبع به Lot مقصد منتقل می‌شود.
            InventoryLot? fromLot = null;
            if (!isVessel && leg.SourceTerminalId.HasValue)
            {
                var srcConsume = await _writer.ConsumeFifoAsync(new LotConsumeRequest(
                    leg.ProductId, leg.SourceTerminalId.Value, leg.SourceStorageTankId,
                    leg.SourcePurchaseContractId, leg.QuantityMt, leg.LoadedDate), ct);
                fromLot = srcConsume.Consumptions.Count > 0 ? srcConsume.Consumptions[0].Lot : null;
            }

            var confidence = isVessel
                ? (leg.ShipmentId.HasValue ? LineageConfidence.Verified : LineageConfidence.Estimated)
                : (fromLot?.LineageConfidence ?? LineageConfidence.Estimated);

            var rootShipmentId = isVessel ? leg.ShipmentId : (fromLot?.RootShipmentId ?? leg.ShipmentId);
            var rootContractId = fromLot?.RootContractId ?? leg.SourcePurchaseContractId;
            var supplierId = fromLot?.SupplierId ?? await ResolveSupplierAsync(leg.SourcePurchaseContractId, ct);
            var destTerminalId = receipt.DestinationTerminalId ?? leg.DestinationTerminalId ?? leg.SourceTerminalId;
            if (!destTerminalId.HasValue) continue;

            var lot = await _writer.CreateLotAsync(new LotCreationRequest(
                leg.ProductId, destTerminalId.Value, receipt.DestinationStorageTankId ?? leg.DestinationStorageTankId,
                receipt.ReceivedQuantityMt,
                isVessel ? InventoryLotSourceType.VesselInbound : InventoryLotSourceType.TransportReceipt,
                confidence,
                RootShipmentId: rootShipmentId,
                RootContractId: rootContractId,
                SupplierId: supplierId,
                ParentLotId: fromLot?.Id,
                CreatedFromMovementId: receipt.InventoryMovementId,
                SourceReferenceType: InventoryLineageWriter.ReceiptRef,
                SourceReferenceId: receipt.Id,
                CreatedAt: receipt.ReceiptDate,
                Notes: "Backfill: transport receipt inbound"), ct);
            s.LotsCreated++;
            Tally(s, confidence);

            // حرکت ورودی (ریشهٔ کشتی برای vessel، یا حملِ موتری/واگنی با FromLot).
            if (!await MovementExistsAsync(InventoryLineageWriter.ReceiptRef, receipt.Id, ct))
            {
                _db.InventoryLotMovements.Add(new InventoryLotMovement
                {
                    FromLotId = fromLot?.Id,
                    ToLotId = lot.Id,
                    MovementKind = isVessel ? InventoryLotMovementKind.VesselInbound : KindFor(leg.TransportType),
                    FromTerminalId = isVessel ? null : leg.SourceTerminalId,
                    FromStorageTankId = isVessel ? null : leg.SourceStorageTankId,
                    ToTerminalId = destTerminalId,
                    ToStorageTankId = lot.StorageTankId,
                    VehicleType = leg.TransportType,
                    VehicleRefType = InventoryLineageWriter.LegRef,
                    VehicleRefId = leg.Id,
                    LoadedQuantityMt = leg.QuantityMt,
                    ReceivedQuantityMt = receipt.ReceivedQuantityMt,
                    ShortageQuantityMt = receipt.ShortageQuantityMt,
                    MovementDate = receipt.ReceiptDate,
                    Status = InventoryLotMovementStatus.Received,
                    ShipmentId = rootShipmentId,
                    SourceReferenceType = InventoryLineageWriter.ReceiptRef,
                    SourceReferenceId = receipt.Id,
                    InventoryMovementId = receipt.InventoryMovementId,
                    LineageConfidence = confidence
                });
                await _db.SaveChangesAsync(ct);
                s.MovementsCreated++;
            }
        }

        // (ب) ورودهای LoadingReceiptAllocation که به موجودی رفته‌اند و leg ندارند.
        var allocations = await _db.LoadingReceiptAllocations.AsNoTracking()
            .Where(a => a.Destination == LoadingReceiptAllocationDestination.ToInventory
                        && a.InventoryMovementId != null)
            .ToListAsync(ct);

        foreach (var alloc in allocations)
        {
            if (await LotExistsAsync(LoadingAllocRef, alloc.Id, ct)) continue;

            var register = await _db.LoadingReceipts.AsNoTracking()
                .Where(r => r.Id == alloc.LoadingReceiptId)
                .Select(r => r.LoadingRegister)
                .FirstOrDefaultAsync(ct);

            var contractId = alloc.SourcePurchaseContractId ?? register?.ContractId;
            var shipmentId = await TryResolveShipmentByVesselAsync(register?.VesselId, ct);
            var confidence = shipmentId.HasValue ? LineageConfidence.Estimated : LineageConfidence.Legacy;
            var supplierId = await ResolveSupplierAsync(contractId, ct);
            var productId = ProductIdForAllocation(register);
            if (productId <= 0)
            {
                s.NeedsReviewItems.Add($"تخصیص رسید #{alloc.Id}: محصول منبع پیدا نشد — NeedsReview.");
                s.NeedsReview++;
                continue;
            }

            await _writer.CreateLotAsync(new LotCreationRequest(
                productId, alloc.TerminalId, alloc.StorageTankId,
                alloc.QuantityMt,
                InventoryLotSourceType.LoadingReceiptAllocation,
                confidence,
                RootShipmentId: shipmentId,
                RootContractId: contractId,
                SupplierId: supplierId,
                CreatedFromMovementId: alloc.InventoryMovementId,
                SourceReferenceType: LoadingAllocRef,
                SourceReferenceId: alloc.Id,
                Notes: "Backfill: loading receipt allocation inbound"), ct);
            s.LotsCreated++;
            Tally(s, confidence);
        }
    }

    // قواعد ۳ و ۴ — حمل‌های بعدی (leg بدون رسیدِ ساخته‌شده در پاس قبل با FromLot) و dispatch.
    private async Task BackfillTransferMovementsAsync(InventoryLineageBackfillSummary s, CancellationToken ct)
    {
        // جفت TruckDispatch (Out) + DeliveryReceipt (In) → حرکت لاتِ تخمینی.
        var dispatches = await _db.TruckDispatches.AsNoTracking()
            .Where(d => d.Status != DispatchStatus.Cancelled)
            .ToListAsync(ct);

        foreach (var dispatch in dispatches)
        {
            if (await MovementExistsAsync(DispatchRef, dispatch.Id, ct)) continue;

            var stockOutMovement = await _db.InventoryMovements.AsNoTracking()
                .Where(m => m.Direction == MovementDirection.Out
                            && m.ReferenceDocument == $"TRUCK-DISPATCH:{dispatch.Id}")
                .OrderByDescending(m => m.Id)
                .FirstOrDefaultAsync(ct);
            if (stockOutMovement is null)
            {
                s.NeedsReviewItems.Add($"دیسپچ #{dispatch.Id}: حرکت خروجی مبدأ پیدا نشد — NeedsReview.");
                s.NeedsReview++;
                continue;
            }

            var consume = await _writer.ConsumeFifoAsync(new LotConsumeRequest(
                stockOutMovement.ProductId, stockOutMovement.TerminalId, stockOutMovement.StorageTankId,
                stockOutMovement.ContractId ?? dispatch.ContractId,
                dispatch.LoadedQuantityMt, dispatch.DispatchDate), ct);
            if (consume.Consumptions.Count == 0)
            {
                s.NeedsReviewItems.Add($"دیسپچ #{dispatch.Id}: Lot مبدأ کافی پیدا نشد — NeedsReview.");
                s.NeedsReview++;
                continue;
            }

            var delivery = await _db.DeliveryReceipts.AsNoTracking()
                .FirstOrDefaultAsync(r => r.TruckDispatchId == dispatch.Id, ct);
            var receivedMt = delivery?.ReceivedQuantityMt ?? dispatch.DischargedQuantityMt;
            var totalConsumed = consume.Consumptions.Sum(c => c.QuantityMt);
            decimal receivedAllocated = 0m;
            decimal shortageAllocated = 0m;
            for (var i = 0; i < consume.Consumptions.Count; i++)
            {
                var consumption = consume.Consumptions[i];
                var isLast = i == consume.Consumptions.Count - 1;
                var receivedShare = receivedMt.HasValue
                    ? isLast
                        ? Round(receivedMt.Value - receivedAllocated)
                        : Round(receivedMt.Value * consumption.QuantityMt / totalConsumed)
                    : (decimal?)null;
                var shortageShare = dispatch.ShortageMt.HasValue
                    ? isLast
                        ? Round(dispatch.ShortageMt.Value - shortageAllocated)
                        : Round(dispatch.ShortageMt.Value * consumption.QuantityMt / totalConsumed)
                    : (decimal?)null;
                receivedAllocated += receivedShare ?? 0m;
                shortageAllocated += shortageShare ?? 0m;

                _db.InventoryLotMovements.Add(new InventoryLotMovement
                {
                    FromLotId = consumption.Lot.Id,
                    MovementKind = InventoryLotMovementKind.TruckLeg,
                    FromTerminalId = consumption.Lot.TerminalId,
                    FromStorageTankId = consumption.Lot.StorageTankId,
                    VehicleType = LoadingTransportType.Truck,
                    VehicleRefType = DispatchRef,
                    VehicleRefId = dispatch.Id,
                    LoadedQuantityMt = consumption.QuantityMt,
                    ReceivedQuantityMt = receivedShare,
                    ShortageQuantityMt = shortageShare,
                    MovementDate = dispatch.DispatchDate,
                    Status = delivery is not null ? InventoryLotMovementStatus.Received : InventoryLotMovementStatus.Loaded,
                    ShipmentId = consumption.Lot.RootShipmentId,
                    SourceReferenceType = DispatchRef,
                    SourceReferenceId = dispatch.Id,
                    InventoryMovementId = stockOutMovement.Id,
                    LineageConfidence = LineageConfidence.Estimated,
                    Notes = consume.FullyAllocated ? null : $"Lineage shortfall {consume.Shortfall:N4} MT — needs review"
                });
                s.MovementsCreated++;
            }
            await _db.SaveChangesAsync(ct);
            if (!consume.FullyAllocated)
            {
                s.NeedsReviewItems.Add($"دیسپچ #{dispatch.Id}: {consume.Shortfall:N4} MT بدون Lot کافی — NeedsReview.");
                s.NeedsReview++;
            }
            Tally(s, LineageConfidence.Estimated);
        }
    }

    // قاعدهٔ ۹ — موجودی آزاد/افتتاحیه: حرکت‌های In که هیچ leg/receipt/loadingreceipt/allocation ندارند.
    private async Task BackfillFreeStockLotsAsync(InventoryLineageBackfillSummary s, CancellationToken ct)
    {
        var freeMovements = await _db.InventoryMovements.AsNoTracking()
            .Where(m => (m.Direction == MovementDirection.In || m.Direction == MovementDirection.Adjustment)
                        && m.LoadingReceiptId == null
                        && m.SalesTransactionId == null)
            .ToListAsync(ct);

        foreach (var m in freeMovements)
        {
            // اگر این movement قبلاً ریشهٔ یک Lot است (vessel/receipt/loadingalloc)، رها کن.
            if (await _db.InventoryLots.AnyAsync(l => l.CreatedFromMovementId == m.Id, ct)) continue;
            if (await LotExistsAsync(FreeStockRef, m.Id, ct)) continue;
            // اگر این movement خروجی یک leg است (TRANSPORT-LEG ref)، ریشه نیست.
            if (m.ReferenceDocument != null && m.ReferenceDocument.StartsWith("TRANSPORT-LEG", StringComparison.Ordinal)) continue;

            await _writer.CreateLotAsync(new LotCreationRequest(
                m.ProductId, m.TerminalId, m.StorageTankId, m.QuantityMt,
                InventoryLotSourceType.LegacyOpening, LineageConfidence.Legacy,
                RootShipmentId: null,
                RootContractId: m.ContractId,
                CreatedFromMovementId: m.Id,
                SourceReferenceType: FreeStockRef,
                SourceReferenceId: m.Id,
                CreatedAt: m.MovementDate,
                Notes: "Backfill: free/opening stock"), ct);
            s.LotsCreated++;
            Tally(s, LineageConfidence.Legacy);
        }
    }

    // قاعدهٔ ۵ — فروش‌ها: FIFO طبق منطق فعلی.
    private async Task BackfillSalesAsync(InventoryLineageBackfillSummary s, CancellationToken ct)
    {
        var sales = await _db.SalesTransactions.AsNoTracking()
            .Where(x => !x.IsCancelled)
            .OrderBy(x => x.SaleDate).ThenBy(x => x.Id)
            .ToListAsync(ct);

        foreach (var sale in sales)
        {
            if (await _db.SaleLotAllocations.AnyAsync(a => a.SalesTransactionId == sale.Id, ct)) continue;

            // ترمینال فروش: از Out movementهای فروش پیدا می‌کنیم (اولین ترمینال).
            var outMovement = await _db.InventoryMovements.AsNoTracking()
                .Where(m => m.SalesTransactionId == sale.Id && m.Direction == MovementDirection.Out)
                .OrderBy(m => m.Id)
                .FirstOrDefaultAsync(ct);

            if (outMovement is null) continue; // فروش بدون حرکت خروجی (مثلاً direct sale از leg) — پاس مجزا لازم ندارد اینجا.

            var consume = await _writer.ConsumeFifoAsync(new LotConsumeRequest(
                sale.ProductId, outMovement.TerminalId, outMovement.StorageTankId,
                outMovement.ContractId, sale.QuantityMt, sale.SaleDate), ct);

            var unitPrice = sale.QuantityMt > 0m ? sale.TotalUsd / sale.QuantityMt : 0m;
            foreach (var c in consume.Consumptions)
            {
                _db.SaleLotAllocations.Add(new SaleLotAllocation
                {
                    SalesTransactionId = sale.Id,
                    LotId = c.Lot.Id,
                    QuantityMt = c.QuantityMt,
                    AmountUsd = Round(unitPrice * c.QuantityMt),
                    AllocationMethod = LotAllocationMethod.FIFO,
                    LineageConfidence = c.Lot.LineageConfidence == LineageConfidence.Verified
                        ? LineageConfidence.Verified : LineageConfidence.Estimated
                });
                s.SaleAllocationsCreated++;
            }

            if (!consume.FullyAllocated && consume.Shortfall > 0.0001m)
            {
                s.NeedsReviewItems.Add($"فروش #{sale.Id}: {consume.Shortfall:N4} MT بدون Lot کافی (NeedsReview).");
                s.NeedsReview++;
            }
            await _db.SaveChangesAsync(ct);
        }
    }

    // قاعدهٔ ۶ — کسری‌ها.
    private async Task BackfillLossesAsync(InventoryLineageBackfillSummary s, CancellationToken ct)
    {
        var losses = await _db.LossEvents.AsNoTracking()
            .Where(x => !x.IsCancelled)
            .OrderBy(x => x.EventDate).ThenBy(x => x.Id)
            .ToListAsync(ct);

        foreach (var loss in losses)
        {
            if (await _db.LossLotAllocations.AnyAsync(a => a.LossEventId == loss.Id, ct)) continue;
            var quantity = loss.DifferenceQuantityMt > 0m ? loss.DifferenceQuantityMt : loss.ChargeableLossMt;
            if (quantity <= 0m) continue;

            List<InventoryLot> targets;
            LotAllocationMethod method;
            var confidence = MapLossConfidence(loss.LossCertainty);

            if (loss.TransportLegId.HasValue)
            {
                var lotIds = await _db.InventoryLotMovements.AsNoTracking()
                    .Where(m => m.SourceReferenceType == InventoryLineageWriter.LegRef && m.SourceReferenceId == loss.TransportLegId.Value
                                || m.VehicleRefType == InventoryLineageWriter.LegRef && m.VehicleRefId == loss.TransportLegId.Value)
                    .Select(m => m.FromLotId ?? m.ToLotId)
                    .Where(id => id != null).Select(id => id!.Value).Distinct().ToListAsync(ct);
                targets = await _db.InventoryLots.Where(l => lotIds.Contains(l.Id)).ToListAsync(ct);
                method = LotAllocationMethod.Manual;
            }
            else if (loss.ShipmentId.HasValue)
            {
                targets = await _db.InventoryLots.Where(l => l.RootShipmentId == loss.ShipmentId.Value).ToListAsync(ct);
                method = LotAllocationMethod.Proportional;
            }
            else
            {
                s.NeedsReviewItems.Add($"کسری #{loss.Id}: بدون leg/shipment — NeedsReview.");
                s.NeedsReview++;
                continue;
            }

            if (targets.Count == 0) continue;
            var basis = targets.Sum(l => l.QuantityMt);
            if (basis <= 0m) continue;

            decimal allocated = 0m;
            for (var i = 0; i < targets.Count; i++)
            {
                var share = i == targets.Count - 1 ? quantity - allocated : Round(quantity * (targets[i].QuantityMt / basis));
                allocated += share;
                if (share <= 0m) continue;
                _db.LossLotAllocations.Add(new LossLotAllocation
                {
                    LossEventId = loss.Id, LotId = targets[i].Id, QuantityMt = Round(share),
                    AllocationMethod = method, LineageConfidence = confidence
                });
                s.LossAllocationsCreated++;
            }
            await _db.SaveChangesAsync(ct);
        }
    }

    // قاعدهٔ ۷ — مصارف.
    private async Task BackfillExpensesAsync(InventoryLineageBackfillSummary s, CancellationToken ct)
    {
        var expenses = await _db.ExpenseTransactions.AsNoTracking()
            .Where(x => !x.IsCancelled && x.AmountUsd > 0m && (x.TransportLegId != null || x.ShipmentId != null))
            .ToListAsync(ct);

        foreach (var expense in expenses)
        {
            if (await _db.ExpenseLotAllocations.AnyAsync(a => a.ExpenseTransactionId == expense.Id, ct)) continue;

            List<InventoryLot> targets;
            LineageConfidence confidence;
            if (expense.TransportLegId.HasValue)
            {
                var lotIds = await _db.InventoryLotMovements.AsNoTracking()
                    .Where(m => (m.SourceReferenceType == InventoryLineageWriter.ReceiptRef || m.VehicleRefType == InventoryLineageWriter.LegRef)
                                && m.VehicleRefId == expense.TransportLegId.Value)
                    .Select(m => m.ToLotId ?? m.FromLotId)
                    .Where(id => id != null).Select(id => id!.Value).Distinct().ToListAsync(ct);
                targets = await _db.InventoryLots.Where(l => lotIds.Contains(l.Id)).ToListAsync(ct);
                confidence = LineageConfidence.Verified;
            }
            else
            {
                targets = await _db.InventoryLots.Where(l => l.RootShipmentId == expense.ShipmentId!.Value).ToListAsync(ct);
                confidence = LineageConfidence.Estimated;
            }

            if (targets.Count == 0) continue;
            var basis = targets.Sum(l => l.QuantityMt);
            decimal allocated = 0m;
            for (var i = 0; i < targets.Count; i++)
            {
                var share = basis <= 0m
                    ? Round(expense.AmountUsd / targets.Count)
                    : (i == targets.Count - 1 ? expense.AmountUsd - allocated : Round(expense.AmountUsd * (targets[i].QuantityMt / basis)));
                allocated += share;
                _db.ExpenseLotAllocations.Add(new ExpenseLotAllocation
                {
                    ExpenseTransactionId = expense.Id, LotId = targets[i].Id, AmountUsd = Round(share),
                    AllocationMethod = LotAllocationMethod.Proportional, LineageConfidence = confidence
                });
                s.ExpenseAllocationsCreated++;
            }
            await _db.SaveChangesAsync(ct);
        }
    }

    private async Task RecomputeConfidenceSummaryAsync(InventoryLineageBackfillSummary s, CancellationToken ct)
    {
        s.VerifiedTotal = await _db.InventoryLots.CountAsync(l => l.LineageConfidence == LineageConfidence.Verified, ct);
        s.EstimatedTotal = await _db.InventoryLots.CountAsync(l => l.LineageConfidence == LineageConfidence.Estimated, ct);
        s.LegacyTotal = await _db.InventoryLots.CountAsync(l => l.LineageConfidence == LineageConfidence.Legacy, ct);
        s.NeedsReviewTotal = await _db.InventoryLots.CountAsync(l => l.LineageConfidence == LineageConfidence.NeedsReview, ct);
    }

    // ===================== helpers =====================
    public const string LoadingAllocRef = "LoadingReceiptAllocation";
    public const string DispatchRef = "TruckDispatch";
    public const string FreeStockRef = "FreeStock";

    private Task<bool> LotExistsAsync(string refType, int refId, CancellationToken ct)
        => _db.InventoryLots.AnyAsync(l => l.SourceReferenceType == refType && l.SourceReferenceId == refId, ct);

    private Task<bool> MovementExistsAsync(string refType, int refId, CancellationToken ct)
        => _db.InventoryLotMovements.AnyAsync(m => m.SourceReferenceType == refType && m.SourceReferenceId == refId, ct);

    private async Task<int?> ResolveSupplierAsync(int? contractId, CancellationToken ct)
        => contractId.HasValue
            ? await _db.Contracts.AsNoTracking().Where(c => c.Id == contractId.Value).Select(c => c.SupplierId).FirstOrDefaultAsync(ct)
            : null;

    private async Task<int?> TryResolveShipmentByVesselAsync(int? vesselId, CancellationToken ct)
    {
        if (!vesselId.HasValue) return null;
        var ids = await _db.Shipments.AsNoTracking()
            .Where(sp => sp.VesselId == vesselId.Value)
            .Select(sp => sp.Id).Take(2).ToListAsync(ct);
        return ids.Count == 1 ? ids[0] : null; // فقط اگر یکتا و قابل اعتماد باشد.
    }

    private static int ProductIdForAllocation(LoadingRegister? register) => register?.ProductId ?? 0;

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

    private static void Tally(InventoryLineageBackfillSummary s, LineageConfidence c)
    {
        switch (c)
        {
            case LineageConfidence.Verified: s.Verified++; break;
            case LineageConfidence.Estimated: s.Estimated++; break;
            case LineageConfidence.Legacy: s.Legacy++; break;
            case LineageConfidence.NeedsReview: s.NeedsReview++; break;
        }
    }

    private static decimal Round(decimal value) => decimal.Round(value, 4, MidpointRounding.AwayFromZero);
}

public sealed class InventoryLineageBackfillSummary
{
    public int LotsCreated { get; set; }
    public int MovementsCreated { get; set; }
    public int SaleAllocationsCreated { get; set; }
    public int LossAllocationsCreated { get; set; }
    public int ExpenseAllocationsCreated { get; set; }
    public int Verified { get; set; }
    public int Estimated { get; set; }
    public int Legacy { get; set; }
    public int NeedsReview { get; set; }
    public int VerifiedTotal { get; set; }
    public int EstimatedTotal { get; set; }
    public int LegacyTotal { get; set; }
    public int NeedsReviewTotal { get; set; }
    public List<string> NeedsReviewItems { get; } = new();
}
