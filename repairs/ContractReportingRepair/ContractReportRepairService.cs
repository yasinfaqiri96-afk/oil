using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.ContractJourney;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services.Reporting;

namespace PTG.ContractReportingRepair;

public sealed record ContractReportRepairSnapshot(
    decimal ExpenseUsd, decimal PhysicalLossMt, decimal GrossLossUsd,
    decimal RecoveredFreightUsd, decimal CompanyLossUsd, decimal? CompletedProfitUsd,
    decimal LoadedQuantityMt, int LoadingCount, decimal SaleableRemainingMt,
    decimal TransportReceivedMt, decimal TransportShortageMt, decimal TransportInTransitMt);

public static class CompletedJourneyMath
{
    public static decimal? Profit(decimal contractMt, decimal loadedMt, decimal pricedMt,
        decimal pendingMt, decimal soldMt, decimal physicalLossMt, decimal stockMt,
        decimal pendingTankMt, decimal revenueUsd, decimal purchaseUsd, decimal expenseUsd,
        decimal realisedFxNetUsd)
    {
        const decimal tolerance = 0.000001m;
        if (loadedMt <= 0m || pendingMt > 0m || pendingTankMt > tolerance
            || Math.Abs(loadedMt - contractMt) > tolerance
            || Math.Abs(pricedMt - loadedMt) > tolerance
            || Math.Abs(stockMt) > tolerance
            || Math.Abs(soldMt + physicalLossMt - loadedMt) > tolerance)
            return null;
        // Purchase value already contains the cost of lost goods. Freight expenses
        // are already net of recovered shortage: neither loss nor recovery is added again.
        return Math.Round(revenueUsd - purchaseUsd - expenseUsd + realisedFxNetUsd,
            2, MidpointRounding.AwayFromZero);
    }

    public static decimal ActualFreightRecovery(decimal? gross, decimal? net, decimal? charge)
        => gross.HasValue && net.HasValue
            ? Math.Min(Math.Max(charge ?? 0m, 0m), Math.Max(gross.Value - net.Value, 0m))
            : 0m;
}

public sealed class ContractReportRepairService(ApplicationDbContext db, IProfitAndLossService pnl)
{
    public async Task<ContractReportRepairSnapshot> ReadAsync(ContractJourneyDetailsViewModel model,
        CancellationToken ct)
    {
        var economics = (await pnl.BuildContractEconomicsAsync([model.ContractId], ct))
            .GetValueOrDefault(model.ContractId);
        if (economics is null)
            throw new InvalidOperationException("Contract economics are missing.");
        // Use the installed financial service's company responsibility policy, customs
        // deduplication, cancellations and shipment allocations. Loss is displayed separately.
        var expense = economics.TransportCostUsd + economics.WarehouseCostUsd
            + economics.OtherCostUsd + economics.RailwayCostUsd + economics.CustomsCostUsd
            + economics.GeneralExpenseCostUsd + economics.SharedShipmentExpenseUsd;
        var metrics = model.SummaryMetrics;
        var useMetrics = model.IsInitialSummaryPayload && metrics.HasValues;
        // Non-summary tabs load only their own paged rows. Derive contract-wide
        // statistics from active source records so each tab displays the same facts.
        var loadingTotals = await db.LoadingRegisters.AsNoTracking()
            .Where(x => x.ContractId == model.ContractId && !x.IsCancelled)
            .GroupBy(x => x.ContractId)
            .Select(g => new { Quantity = g.Sum(x => x.LoadedQuantityMt), Count = g.Count() })
            .SingleOrDefaultAsync(ct);
        var loaded = loadingTotals?.Quantity ?? 0m;
        var loadingCount = loadingTotals?.Count ?? 0;
        var sold = economics.SoldQuantityMt;
        var physicalLoss = await db.LossEvents.AsNoTracking()
            .Where(x => x.ContractId == model.ContractId && !x.IsCancelled)
            .SumAsync(x => x.DifferenceQuantityMt > 0m ? x.DifferenceQuantityMt
                : x.ChargeableLossMt > 0m ? x.ChargeableLossMt : 0m, ct);
        var average = economics.WeightedAveragePurchasePriceUsd;
        var grossLoss = Math.Round(physicalLoss * (average ?? 0m), 2, MidpointRounding.AwayFromZero);
        var legs = await db.InventoryTransportLegs.AsNoTracking()
            .Where(x => x.SourcePurchaseContractId == model.ContractId && x.Status != InventoryTransportLegStatus.Cancelled)
            .Select(x => new { x.Id, x.QuantityMt, x.Status }).ToListAsync(ct);
        var ids = legs.Select(x => x.Id).ToArray();
        var receipts = await db.InventoryTransportReceipts.AsNoTracking()
            .Where(x => ids.Contains(x.InventoryTransportLegId) && !x.IsCancelled)
            .Select(x => new { x.InventoryTransportLegId, x.ReceivedQuantityMt, x.ShortageQuantityMt,
                x.FreightCostUsd, x.FreightPayableUsd, x.ShortageChargeUsd }).ToListAsync(ct);
        // Multi-contract legs require attribution; keep their original figures until allocated.
        var hasMixedLeg = await db.InventoryTransportLegAllocations.AsNoTracking()
            .AnyAsync(x => ids.Contains(x.InventoryTransportLegId)
                && x.SourcePurchaseContractId != model.ContractId, ct);
        var recovered = hasMixedLeg ? 0m : receipts.Sum(x =>
            CompletedJourneyMath.ActualFreightRecovery(x.FreightCostUsd, x.FreightPayableUsd, x.ShortageChargeUsd));
        var received = hasMixedLeg ? (useMetrics ? metrics.InventoryTransportReceivedMt : model.InventoryTransportLegItems.Sum(x => x.ReceivedQuantityMt))
            : receipts.Sum(x => x.ReceivedQuantityMt);
        var shortage = hasMixedLeg ? (useMetrics ? metrics.InventoryTransportShortageMt : model.InventoryTransportLegItems.Sum(x => x.ShortageQuantityMt))
            : receipts.Sum(x => x.ShortageQuantityMt);
        var inTransit = hasMixedLeg ? metrics.InventoryTransportInTransitMt : legs.Sum(l =>
            l.Status == InventoryTransportLegStatus.Received ? 0m
            : Math.Max(l.QuantityMt - receipts.Where(x => x.InventoryTransportLegId == l.Id)
                .Sum(x => x.ReceivedQuantityMt + x.ShortageQuantityMt), 0m));
        var profit = hasMixedLeg ? null : CompletedJourneyMath.Profit(model.ContractQuantityMt,
            loaded, economics.PricedLoadedMt, economics.PendingLoadedMt,
            sold, physicalLoss, model.Kpis.CurrentStockQuantityMt, model.PendingTankSettlementQuantityMt,
            economics.RevenueUsd, economics.PurchaseValueUsd, expense, model.MiniPnl.RealizedFxNetUsd);
        return new(expense, physicalLoss, grossLoss, recovered, Math.Max(grossLoss - recovered, 0m),
            profit, loaded, loadingCount, Math.Max(loaded - sold - physicalLoss, 0m), received, shortage, inTransit);
    }
}
