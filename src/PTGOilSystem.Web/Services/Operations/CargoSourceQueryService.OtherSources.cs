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
            .Include(l => l.DestinationTerminal).Include(l => l.DestinationLocation).Include(l => l.Allocations).ThenInclude(a => a.SourcePurchaseContract).ToListAsync(ct);
        var quantities = await (quantityService ?? new TransportQuantityService(db)).GetQuantitiesAsync(legs.Select(l => l.Id).ToArray(), ct);
        var ids = legs.Select(l => l.Id).ToArray();
        var blockedForFullSale = (await db.InventoryTransportReceipts.AsNoTracking()
            .Where(r => ids.Contains(r.InventoryTransportLegId) && !r.IsCancelled
                && r.ReceiptDestination != InventoryTransportReceiptDestination.DirectSale
                && (r.ReceivedQuantityMt > 0m || r.SalesTransactionId.HasValue))
            .Select(r => r.InventoryTransportLegId).Distinct().ToListAsync(ct)).ToHashSet();
        return legs.Select(l => new CargoSourceSnapshot(CargoSourceKind.Transport, l.Id,
            l.SourcePurchaseContractId, l.ProductId, l.SourcePurchaseContract?.CompanyId ?? 0,
            l.SourcePurchaseContract?.ContractNumber ?? "", l.Product?.Name ?? "", l.SourcePurchaseContract?.Company?.Name ?? "",
            l.WagonNumber ?? l.RwbNo ?? l.Truck?.PlateNumber ?? $"#{l.Id}",
            l.TransportType == LoadingTransportType.Wagon ? "واگن" : l.TransportType == LoadingTransportType.Truck ? "موتر" : "انتقال",
            $"{l.SourceTerminal?.Name ?? "؟"} ← {l.DestinationTerminal?.Name ?? l.DestinationLocation?.Name ?? "؟"}", l.LoadedDate, l.QuantityMt,
            quantities[l.Id].ReceivedToInventoryMt, quantities[l.Id].ShortageMt, quantities[l.Id].TransferredToVehicleMt, quantities[l.Id].RemainingMt,
            l.Status == InventoryTransportLegStatus.Cancelled, l.IsArchived,
            l.SourcePurchaseContract?.ContractType == ContractType.Purchase,
            SupportedActions: l.Status is InventoryTransportLegStatus.Loaded or InventoryTransportLegStatus.InTransit
                ? (!blockedForFullSale.Contains(l.Id)
                    ? [CargoAction.Receive, CargoAction.DirectSale, CargoAction.ContinueTransport, CargoAction.Expense, CargoAction.PreSaleDelivery]
                    : [CargoAction.Receive, CargoAction.ContinueTransport, CargoAction.Expense, CargoAction.PreSaleDelivery])
                : [CargoAction.Expense],
            OwnershipShares: l.Allocations.Select(a => new CargoOwnershipShare(a.SourcePurchaseContractId,
                a.QuantityMt, a.SourceLoadingRegisterId, a.SourceLoadingReceiptId, a.SourcePurchaseContract?.Status)).ToList(),
            ContractStatus: l.SourcePurchaseContract?.Status,
            StatusLabel: l.IsFreightSettled ? "کرایه تسویه‌شده" : l.Status == InventoryTransportLegStatus.InTransit ? "در راه" : "بارگیری‌شده",
            SoldQuantityMt: quantities[l.Id].SoldMt)).ToList();
    }

    public async Task<IReadOnlyList<CargoSourceSnapshot>> LoadDispatchSourcesAsync(IReadOnlyCollection<int>? dispatchIds = null, CancellationToken ct = default)
    {
        var continued = TransportChainProjection.ContinuedTransferReceiptIds(db);
        var query = db.TruckDispatches.AsNoTracking().AsQueryable();
        if (dispatchIds is not null) query = query.Where(d => dispatchIds.Contains(d.Id));
        else query = query.Where(d => d.Status == DispatchStatus.Loaded || d.Status == DispatchStatus.InTransit);
        var rows = await query
            .Where(d => !(d.InventoryTransportReceiptId.HasValue && continued.Contains(d.InventoryTransportReceiptId.Value)))
            .Include(d => d.Contract).ThenInclude(c => c!.Company).Include(d => d.Product)
            .Include(d => d.Truck).Include(d => d.DestinationLocation).ToListAsync(ct);
        return rows.Select(d => new CargoSourceSnapshot(CargoSourceKind.Dispatch, d.Id, d.ContractId,
            d.ProductId, d.Contract?.CompanyId ?? 0, d.Contract?.ContractNumber ?? "", d.Product?.Name ?? "",
            d.Contract?.Company?.Name ?? "", d.Truck?.PlateNumber ?? $"#{d.Id}", "موتر",
            d.DestinationLocation?.Name ?? "", d.DispatchDate, d.LoadedQuantityMt, 0m, 0m, 0m,
            d.SalesTransactionId.HasValue ? 0m : d.DischargedQuantityMt ?? d.LoadedQuantityMt,
            d.Status == DispatchStatus.Cancelled, false, d.Contract?.ContractType == ContractType.Purchase,
            SupportedActions: (d.Status is DispatchStatus.Loaded or DispatchStatus.InTransit
                ? new[] { CargoAction.DirectSale, CargoAction.PreSaleDelivery, CargoAction.Expense } : new[] { CargoAction.Expense }), ContractStatus: d.Contract?.Status,
            StatusLabel: d.IsFreightSettled ? "کرایه تسویه‌شده" : d.Status == DispatchStatus.InTransit ? "در راه" : "بارگیری‌شده")).ToList();
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

    public async Task<IReadOnlyList<CargoSourceSnapshot>> LoadStockSourcesAsync(DateTime asOf, CancellationToken ct = default,
        int? productId = null, int? terminalId = null, int? storageTankId = null, int? contractId = null)
    {
        var movementQuery = db.InventoryMovements.AsNoTracking().AsQueryable();
        if (productId.HasValue) movementQuery = movementQuery.Where(m => m.ProductId == productId);
        if (terminalId.HasValue) movementQuery = movementQuery.Where(m => m.TerminalId == terminalId);
        if (storageTankId.HasValue) movementQuery = movementQuery.Where(m => m.StorageTankId == storageTankId);
        var poolsQuery = movementQuery
            .Where(m => m.StorageTankId.HasValue && m.MovementDate <= asOf)
            .Select(m => new { m.ProductId, m.TerminalId, m.StorageTankId,
                ContractId = m.ContractId ?? (m.LoadingReceipt != null ? (int?)m.LoadingReceipt.LoadingRegister!.ContractId : null) })
            .Where(m => m.ContractId.HasValue).Distinct();
        if (contractId.HasValue) poolsQuery = poolsQuery.Where(p => p.ContractId == contractId);
        var pools = await poolsQuery.ToListAsync(ct);
        var contractIds = pools.Select(p => p.ContractId!.Value).Distinct().ToArray();
        var contracts = await db.Contracts.AsNoTracking().Where(c => contractIds.Contains(c.Id)
            && c.ContractType == ContractType.Purchase).Include(c => c.Company).Include(c => c.Product)
            .ToDictionaryAsync(c => c.Id, ct);
        var companyStock = await new PreSaleReservationService(db, businessClock ?? new AfghanistanBusinessClock(TimeProvider.System))
            .GetSellableStockAsync(new ManagementReportFilterViewModel { ToDate = asOf }, ct);
        var scoped = companyStock.Where(s => s.CompanyId.HasValue).ToDictionary(s => (s.CompanyId!.Value, s.ProductId));
        var stock = stockService ?? new StockService(db);
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
                SupportedActions: [CargoAction.DirectSale, CargoAction.PreSaleDelivery, CargoAction.StartTransport, CargoAction.Expense], ContractStatus: c.Status,
                StatusLabel: "قابل فروش"));
        }
        return result;
    }
}
