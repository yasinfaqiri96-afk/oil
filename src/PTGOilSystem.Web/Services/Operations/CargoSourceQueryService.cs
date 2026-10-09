using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;

namespace PTGOilSystem.Web.Services.Operations;

public enum CargoSourceKind { Loading, Stock, Transport, Dispatch, Receipt }
public enum CargoAction { Receive, DirectSale, StartTransport, ContinueTransport, Expense, History }

// These quantities deliberately have different meanings; a physical stock balance is not
// a reservation-adjusted sellable balance or a transport's unreceived balance.
public sealed record CargoSourceSnapshot(
    CargoSourceKind Kind, int Id, int ContractId, int ProductId, int CompanyId,
    string ContractNumber, string ProductName, string CompanyName,
    string Number, string VehicleLabel, string Route, DateTime Date,
    decimal OriginalQuantityMt, decimal ReceivedQuantityMt, decimal ShortageQuantityMt,
    decimal TransportedQuantityMt, decimal RemainingQuantityMt,
    bool IsCancelled, bool IsArchived, bool IsPurchaseContract,
    decimal? PhysicalStockMt = null, decimal? SellableStockMt = null);

public sealed record CargoEligibility(bool Allowed, string? Reason);

public static class CargoOperationEligibility
{
    public static CargoEligibility Evaluate(CargoSourceSnapshot source, CargoAction action)
    {
        if (action == CargoAction.History) return new(true, null);
        if (source.IsCancelled) return new(false, "این بار لغو شده است.");
        if (source.IsArchived) return new(false, "این بار بایگانی شده است.");
        if (!source.IsPurchaseContract) return new(false, "قرارداد خرید این بار معتبر نیست.");
        if (action == CargoAction.Expense) return new(true, null);
        if (source.Kind != CargoSourceKind.Loading)
            return new(false, "این عملیات باید از مسیر معتبر همین بار ثبت شود.");
        if (action is not (CargoAction.Receive or CargoAction.DirectSale or CargoAction.StartTransport))
            return new(false, "این عملیات برای بارگیری مجاز نیست.");
        if (source.RemainingQuantityMt <= 0m)
            return new(false, "ماندهٔ این بار قبلاً دریافت یا به حمل تخصیص یافته است.");
        return new(true, null);
    }
}

// Read-only application projection over the existing receipt/loss/allocation authorities.
// Command callers must acquire the LoadingRegister row lock before re-reading this projection.
public sealed class CargoSourceQueryService(ApplicationDbContext db)
{
    public async Task<IReadOnlyList<CargoSourceSnapshot>> LoadLoadingSourcesAsync(
        IReadOnlyCollection<int>? loadingIds = null, CancellationToken ct = default)
    {
        var query = db.LoadingRegisters.AsNoTracking().AsQueryable();
        if (loadingIds is not null) query = query.Where(l => loadingIds.Contains(l.Id));
        else query = query.Where(l => !l.IsCancelled && !l.IsArchived);
        var rows = await query.Select(l => new
        {
            l.Id, l.ContractId, l.ProductId, l.LoadedQuantityMt, l.LoadingDate,
            l.IsCancelled, l.IsArchived, l.TransportType,
            CompanyId = l.Contract != null ? l.Contract.CompanyId : 0,
            ContractNumber = l.Contract != null ? l.Contract.ContractNumber : "",
            IsPurchaseContract = l.Contract != null && l.Contract.ContractType == ContractType.Purchase,
            CompanyName = l.Contract != null && l.Contract.Company != null ? l.Contract.Company.Name : "",
            ProductName = l.Product != null ? l.Product.Name : "",
            Number = l.WagonNumber ?? l.RwbNo ?? (l.Truck != null ? l.Truck.PlateNumber : null) ?? l.BillOfLadingNumber,
            Route = l.RouteDescription ?? l.DestinationName ?? ""
        }).ToListAsync(ct);
        if (rows.Count == 0) return [];
        var ids = rows.Select(l => l.Id).ToArray();
        var received = await db.LoadingReceipts.AsNoTracking()
            .Where(r => ids.Contains(r.LoadingRegisterId) && !r.IsCancelled)
            .GroupBy(r => r.LoadingRegisterId)
            .Select(g => new { Id = g.Key, Qty = g.Sum(r => r.ReceivedQuantityMt) })
            .ToDictionaryAsync(r => r.Id, r => r.Qty, ct);
        var transported = await db.InventoryTransportLegAllocations.AsNoTracking()
            .Where(a => a.SourceLoadingRegisterId.HasValue && ids.Contains(a.SourceLoadingRegisterId.Value)
                && a.InventoryTransportLeg != null && a.InventoryTransportLeg.Status != InventoryTransportLegStatus.Cancelled)
            .GroupBy(a => a.SourceLoadingRegisterId!.Value)
            .Select(g => new { Id = g.Key, Qty = g.Sum(a => a.QuantityMt) })
            .ToDictionaryAsync(a => a.Id, a => a.Qty, ct);
        var losses = await db.LossEvents.AsNoTracking()
            .Where(e => !e.IsCancelled && e.Stage == LossEventStage.ReceiptShortage
                && (e.LoadingRegisterId.HasValue && ids.Contains(e.LoadingRegisterId.Value)
                    || e.LoadingReceipt != null && ids.Contains(e.LoadingReceipt.LoadingRegisterId)))
            .Select(e => new { Id = e.LoadingRegisterId ?? e.LoadingReceipt!.LoadingRegisterId,
                e.DifferenceQuantityMt, e.ChargeableLossMt }).ToListAsync(ct);
        var shortage = losses.GroupBy(e => e.Id).ToDictionary(g => g.Key,
            g => g.Sum(e => e.DifferenceQuantityMt > 0m ? e.DifferenceQuantityMt : Math.Max(e.ChargeableLossMt, 0m)));
        return rows.Select(l => new CargoSourceSnapshot(CargoSourceKind.Loading,
            l.Id, l.ContractId, l.ProductId, l.CompanyId, l.ContractNumber, l.ProductName, l.CompanyName,
            l.Number ?? $"#{l.Id}", l.TransportType == LoadingTransportType.Wagon ? "واگن"
                : l.TransportType == LoadingTransportType.Truck ? "موتر" : "کشتی", l.Route, l.LoadingDate,
            l.LoadedQuantityMt, received.GetValueOrDefault(l.Id), shortage.GetValueOrDefault(l.Id),
            transported.GetValueOrDefault(l.Id), decimal.Round(Math.Max(0m, l.LoadedQuantityMt
                - received.GetValueOrDefault(l.Id) - shortage.GetValueOrDefault(l.Id)
                - transported.GetValueOrDefault(l.Id)), 4, MidpointRounding.AwayFromZero),
            l.IsCancelled, l.IsArchived, l.IsPurchaseContract)).ToList();
    }
}
