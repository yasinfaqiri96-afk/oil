using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;

namespace PTGOilSystem.Web.Services.Operations;

public enum CargoSourceKind { Loading, Stock, Transport, Dispatch, Receipt }
public enum CargoAction { Receive, DirectSale, StartTransport, ContinueTransport, Expense, History, PreSaleDelivery }

// These quantities deliberately have different meanings; a physical stock balance is not
// a reservation-adjusted sellable balance or a transport's unreceived balance.
public sealed record CargoOwnershipShare(int ContractId, decimal QuantityMt, int? SourceLoadingRegisterId = null, int? SourceLoadingReceiptId = null, ContractStatus? ContractStatus = null);

public sealed record CargoSourceSnapshot(
    CargoSourceKind Kind, int Id, int ContractId, int ProductId, int CompanyId,
    string ContractNumber, string ProductName, string CompanyName,
    string Number, string VehicleLabel, string Route, DateTime Date,
    decimal OriginalQuantityMt, decimal ReceivedQuantityMt, decimal ShortageQuantityMt,
    decimal TransportedQuantityMt, decimal RemainingQuantityMt,
    bool IsCancelled, bool IsArchived, bool IsPurchaseContract,
    decimal? PhysicalStockMt = null, decimal? SellableStockMt = null,
    int? TerminalId = null, int? StorageTankId = null,
    IReadOnlyCollection<CargoAction>? SupportedActions = null,
    IReadOnlyList<CargoOwnershipShare>? OwnershipShares = null,
    ContractStatus? ContractStatus = null,
    string StatusLabel = "", decimal SoldQuantityMt = 0m)
{
    public decimal ConsumedQuantityMt => OriginalQuantityMt - RemainingQuantityMt;
}

public sealed record CargoEligibility(bool Allowed, string? Reason);

public static class CargoOperationEligibility
{
    public static CargoEligibility Evaluate(CargoSourceSnapshot source, CargoAction action)
    {
        if (action == CargoAction.History) return new(true, null);
        if (source.ContractStatus == PTGOilSystem.Web.Models.Entities.ContractStatus.Closed
            || source.OwnershipShares?.Any(s => s.ContractStatus == PTGOilSystem.Web.Models.Entities.ContractStatus.Closed) == true)
            return new(false, "قرارداد این بار بسته شده است؛ نخست آن را از مسیر مجاز باز کنید.");
        if (source.ContractStatus == PTGOilSystem.Web.Models.Entities.ContractStatus.Cancelled
            || source.OwnershipShares?.Any(s => s.ContractStatus == PTGOilSystem.Web.Models.Entities.ContractStatus.Cancelled) == true)
            return new(false, "قرارداد این بار لغو شده است.");
        if (source.IsCancelled) return new(false, "این بار لغو شده است.");
        if (source.IsArchived) return new(false, "این بار بایگانی شده است.");
        if (!source.IsPurchaseContract) return new(false, "قرارداد خرید این بار معتبر نیست.");
        if (action == CargoAction.Expense && source.Kind == CargoSourceKind.Loading) return new(true, null);
        if (source.Kind != CargoSourceKind.Loading)
        {
            if (source.SupportedActions?.Contains(action) != true)
                return new(false, "این عملیات برای وضعیت فعلی بار مجاز نیست.");
            if (action == CargoAction.Expense) return new(true, null);
            var available = source.Kind == CargoSourceKind.Stock && action == CargoAction.DirectSale
                ? source.SellableStockMt : source.RemainingQuantityMt;
            if (!available.HasValue)
                return new(false, "ماندهٔ قابل فروش این منبع نیاز به بررسی دارد.");
            return available > 0m ? new(true, null) : new(false, "ماندهٔ کافی برای این عملیات موجود نیست.");
        }
        if (action is not (CargoAction.Receive or CargoAction.DirectSale or CargoAction.StartTransport))
            return new(false, "این عملیات برای بارگیری مجاز نیست.");
        if (action == CargoAction.Expense) return new(true, null);
        if (source.RemainingQuantityMt <= 0m)
            return new(false, "ماندهٔ این بار قبلاً دریافت یا به حمل تخصیص یافته است.");
        return new(true, null);
    }
}

// Read-only application projection over the existing receipt/loss/allocation authorities.
// Command callers must acquire the LoadingRegister row lock before re-reading this projection.
public sealed partial class CargoSourceQueryService(ApplicationDbContext db, IStockService? stockService = null,
    ITransportQuantityService? quantityService = null, PTGOilSystem.Web.Services.Time.IAfghanistanBusinessClock? businessClock = null)
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
            ContractStatus = l.Contract != null ? (ContractStatus?)l.Contract.Status : null,
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
                : l.TransportType == LoadingTransportType.Truck ? "موتر"
                : l.TransportType == LoadingTransportType.Vessel ? "کشتی" : "نامشخص", l.Route, l.LoadingDate,
            l.LoadedQuantityMt, received.GetValueOrDefault(l.Id), shortage.GetValueOrDefault(l.Id),
            transported.GetValueOrDefault(l.Id), decimal.Round(Math.Max(0m, l.LoadedQuantityMt
                - received.GetValueOrDefault(l.Id) - shortage.GetValueOrDefault(l.Id)
                - transported.GetValueOrDefault(l.Id)), 4, MidpointRounding.AwayFromZero),
            l.IsCancelled, l.IsArchived, l.IsPurchaseContract,
            OwnershipShares: [new CargoOwnershipShare(l.ContractId, l.LoadedQuantityMt, l.Id)], ContractStatus: l.ContractStatus)).ToList();
    }
}
