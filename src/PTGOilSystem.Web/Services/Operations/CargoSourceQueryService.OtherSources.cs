using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Helpers;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.Reports;
using PTGOilSystem.Web.Services.Reporting;
using PTGOilSystem.Web.Services.Time;

namespace PTGOilSystem.Web.Services.Operations;

public sealed partial class CargoSourceQueryService
{
    public async Task<IReadOnlyList<CargoSourceSnapshot>> LoadTransportSourcesAsync(
        IReadOnlyCollection<int>? legIds = null, CancellationToken ct = default)
    {
        var query = db.InventoryTransportLegs.AsNoTracking().AsQueryable();
        if (legIds is not null) query = query.Where(l => legIds.Contains(l.Id));
        else query = query.Where(l => !l.IsArchived && (l.Status == InventoryTransportLegStatus.Loaded
            || l.Status == InventoryTransportLegStatus.InTransit));
        var legs = await query.Include(l => l.SourcePurchaseContract).ThenInclude(c => c!.Company)
            .Include(l => l.Product).Include(l => l.Truck).Include(l => l.SourceTerminal)
            .Include(l => l.DestinationTerminal).Include(l => l.Allocations).ToListAsync(ct);
        var remaining = await new TransportQuantityService(db).GetRemainingMtAsync(legs.Select(l => l.Id).ToArray(), ct);
        return legs.Select(l => new CargoSourceSnapshot(CargoSourceKind.Transport, l.Id,
            l.SourcePurchaseContractId, l.ProductId, l.SourcePurchaseContract?.CompanyId ?? 0,
            l.SourcePurchaseContract?.ContractNumber ?? "", l.Product?.Name ?? "", l.SourcePurchaseContract?.Company?.Name ?? "",
            l.WagonNumber ?? l.RwbNo ?? l.Truck?.PlateNumber ?? $"#{l.Id}",
            l.TransportType == LoadingTransportType.Wagon ? "واگن" : "موتر",
            $"{l.SourceTerminal?.Name} ← {l.DestinationTerminal?.Name}", l.LoadedDate, l.QuantityMt,
            0m, 0m, 0m, remaining.GetValueOrDefault(l.Id),
            l.Status == InventoryTransportLegStatus.Cancelled, l.IsArchived,
            l.SourcePurchaseContract?.ContractType == ContractType.Purchase,
            SupportedActions: l.Status is InventoryTransportLegStatus.Loaded or InventoryTransportLegStatus.InTransit
                ? [CargoAction.Receive, CargoAction.DirectSale, CargoAction.ContinueTransport, CargoAction.Expense]
                : [CargoAction.Expense],
            OwnershipShares: l.Allocations.Select(a => new CargoOwnershipShare(a.SourcePurchaseContractId,
                a.QuantityMt, a.SourceLoadingRegisterId, a.SourceLoadingReceiptId)).ToList(),
            ContractStatus: l.SourcePurchaseContract?.Status)).ToList();
    }

    public async Task<IReadOnlyList<CargoSourceSnapshot>> LoadDispatchSourcesAsync(CancellationToken ct = default)
    {
        var continued = TransportChainProjection.ContinuedTransferReceiptIds(db);
        var rows = await db.TruckDispatches.AsNoTracking()
            .Where(d => (d.Status == DispatchStatus.Loaded || d.Status == DispatchStatus.InTransit)
                && !(d.InventoryTransportReceiptId.HasValue && continued.Contains(d.InventoryTransportReceiptId.Value)))
            .Include(d => d.Contract).ThenInclude(c => c!.Company).Include(d => d.Product)
            .Include(d => d.Truck).Include(d => d.DestinationLocation).ToListAsync(ct);
        return rows.Select(d => new CargoSourceSnapshot(CargoSourceKind.Dispatch, d.Id, d.ContractId,
            d.ProductId, d.Contract?.CompanyId ?? 0, d.Contract?.ContractNumber ?? "", d.Product?.Name ?? "",
            d.Contract?.Company?.Name ?? "", d.Truck?.PlateNumber ?? $"#{d.Id}", "موتر",
            d.DestinationLocation?.Name ?? "", d.DispatchDate, d.LoadedQuantityMt, 0m, 0m, 0m,
            d.SalesTransactionId.HasValue ? 0m : d.DischargedQuantityMt ?? d.LoadedQuantityMt,
            false, false, d.Contract?.ContractType == ContractType.Purchase,
            SupportedActions: [CargoAction.DirectSale, CargoAction.Expense], ContractStatus: d.Contract?.Status)).ToList();
    }

    public async Task<IReadOnlyList<CargoSourceSnapshot>> LoadReceiptSourcesAsync(CancellationToken ct = default)
    {
        // Only direct-dispatch allocations can start a receipt-sourced transport. Stock receipts
        // must start from the stock service instead; otherwise their inbound would be consumed twice.
        var rows = await db.LoadingReceipts.AsNoTracking().Where(r => !r.IsCancelled && !r.IsArchived)
            .Include(r => r.LoadingRegister).ThenInclude(l => l!.Contract).ThenInclude(c => c!.Company)
            .Include(r => r.LoadingRegister).ThenInclude(l => l!.Product).Include(r => r.Allocations)
            .Where(r => r.Allocations.Any(a => a.Destination == LoadingReceiptAllocationDestination.DirectDispatchToTruck))
            .ToListAsync(ct);
        var ids = rows.Select(r => r.Id).ToArray();
        var used = await db.InventoryTransportLegAllocations.AsNoTracking()
            .Where(a => a.SourceLoadingReceiptId.HasValue && ids.Contains(a.SourceLoadingReceiptId.Value)
                && a.InventoryTransportLeg != null && a.InventoryTransportLeg.Status != InventoryTransportLegStatus.Cancelled)
            .GroupBy(a => a.SourceLoadingReceiptId!.Value).Select(g => new { Id = g.Key, Qty = g.Sum(a => a.QuantityMt) })
            .ToDictionaryAsync(a => a.Id, a => a.Qty, ct);
        return rows.Select(r =>
        {
            var l = r.LoadingRegister!;
            var quantity = r.Allocations.Where(a => a.Destination == LoadingReceiptAllocationDestination.DirectDispatchToTruck
                && a.Status != LoadingReceiptAllocationStatus.Cancelled).Sum(a => a.QuantityMt);
            return new CargoSourceSnapshot(CargoSourceKind.Receipt, r.Id, l.ContractId, l.ProductId,
                l.Contract?.CompanyId ?? 0, l.Contract?.ContractNumber ?? "", l.Product?.Name ?? "",
                l.Contract?.Company?.Name ?? "", r.ReferenceDocument ?? $"#{r.Id}", "رسید بار", "", r.ReceiptDate,
                quantity, 0m, 0m, used.GetValueOrDefault(r.Id), Math.Max(0m, quantity - used.GetValueOrDefault(r.Id)),
                l.IsCancelled, l.IsArchived, l.Contract?.ContractType == ContractType.Purchase,
                TerminalId: r.TerminalId, SupportedActions: [CargoAction.StartTransport],
                OwnershipShares: r.Allocations.Where(a => a.SourcePurchaseContractId.HasValue
                    && a.Destination == LoadingReceiptAllocationDestination.DirectDispatchToTruck
                    && a.Status != LoadingReceiptAllocationStatus.Cancelled)
                    .Select(a => new CargoOwnershipShare(a.SourcePurchaseContractId!.Value, a.QuantityMt, l.Id, r.Id)).ToList(),
                ContractStatus: l.Contract?.Status);
        }).ToList();
    }

    public async Task<IReadOnlyList<CargoSourceSnapshot>> LoadStockSourcesAsync(DateTime asOf, CancellationToken ct = default)
    {
        var pools = await db.InventoryMovements.AsNoTracking()
            .Where(m => m.StorageTankId.HasValue && m.MovementDate <= asOf)
            .Select(m => new { m.ProductId, m.TerminalId, m.StorageTankId,
                ContractId = m.ContractId ?? (m.LoadingReceipt != null ? (int?)m.LoadingReceipt.LoadingRegister!.ContractId : null) })
            .Where(m => m.ContractId.HasValue).Distinct().ToListAsync(ct);
        var contractIds = pools.Select(p => p.ContractId!.Value).Distinct().ToArray();
        var contracts = await db.Contracts.AsNoTracking().Where(c => contractIds.Contains(c.Id)
            && c.ContractType == ContractType.Purchase).Include(c => c.Company).Include(c => c.Product)
            .ToDictionaryAsync(c => c.Id, ct);
        var companyStock = await new PreSaleReservationService(db, new AfghanistanBusinessClock(TimeProvider.System))
            .GetSellableStockAsync(new ManagementReportFilterViewModel { ToDate = asOf }, ct);
        var scoped = companyStock.Where(s => s.CompanyId.HasValue).ToDictionary(s => (s.CompanyId!.Value, s.ProductId));
        var stock = new StockService(db);
        var result = new List<CargoSourceSnapshot>();
        foreach (var p in pools)
        {
            if (!contracts.TryGetValue(p.ContractId!.Value, out var c)) continue;
            var physical = await stock.GetFreeQuantityMtAsync(p.ProductId, p.TerminalId, p.ContractId,
                storageTankId: p.StorageTankId, asOfUtc: asOf, ct: ct);
            if (physical <= 0m) continue;
            var globalSellable = scoped.GetValueOrDefault((c.CompanyId, p.ProductId))?.SellableMt;
            // Reservations are company/product scoped, never invented as a tank allocation.
            var sellableBound = globalSellable.HasValue ? Math.Max(0m, Math.Min(physical, globalSellable.Value)) : (decimal?)null;
            result.Add(new(CargoSourceKind.Stock, 0, c.Id, p.ProductId, c.CompanyId, c.ContractNumber,
                c.Product?.Name ?? "", c.Company?.Name ?? "", $"#{p.StorageTankId}", "مخزن", "", asOf,
                physical, 0m, 0m, 0m, physical, false, false, true, PhysicalStockMt: physical,
                SellableStockMt: sellableBound, TerminalId: p.TerminalId, StorageTankId: p.StorageTankId,
                SupportedActions: [CargoAction.DirectSale, CargoAction.StartTransport, CargoAction.Expense], ContractStatus: c.Status));
        }
        return result;
    }
}
