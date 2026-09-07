using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Helpers;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.TruckSettlements;

namespace PTGOilSystem.Web.Controllers;

// ── فهرست «تسویه‌شده‌ها» ──
// آینهٔ فقط‌خواندنیِ Index برای leg/dispatch هایی که IsFreightSettled=true دارند.
// هیچ ثبتی ندارد و هیچ منطق مالی را صدا نمی‌زند؛ فقط همان داده‌های ثبت‌شده را نشان می‌دهد
// تا معلوم شود چه چیزی تسویه شده و کدام‌یک هنوز تخلیه نشده است.
public partial class TruckSettlementsController
{
    public async Task<IActionResult> Settled(
        string? q,
        TruckSettlementSourceKind? kind,
        int page = 1,
        [FromQuery(Name = "pageSize")] int? perPage = null)
    {
        var pageSize = perPage is > 0 and <= 200 ? perPage.Value : 25;
        var rows = await BuildSettledRowsAsync(q, kind);

        var model = new TruckSettlementSettledIndexViewModel
        {
            Query = q,
            Kind = kind,
            PageSize = pageSize,
            TotalCount = rows.Count,
            TotalFreightUsd = rows.Sum(r => r.FreightUsd),
            TotalShortageMt = rows.Sum(r => r.ShortageMt),
            PendingUnloadCount = rows.Count(r => !r.IsUnloaded)
        };
        model.Page = Math.Clamp(page, 1, model.PageCount);
        model.Rows = rows
            .Skip((model.Page - 1) * pageSize)
            .Take(pageSize)
            .ToList();

        ViewData["CurrentPage"] = model.Page;
        ViewData["PageCount"] = model.PageCount;
        ViewData["ShownCount"] = model.TotalCount;
        ViewData["PageSize"] = pageSize;
        ViewData["DefaultPageSize"] = 25;
        return View(model);
    }

    // کرایه/کسری leg روی رسیدهای همان حمل نشسته است (تسویه با SettlementOnly رسید می‌سازد)؛
    // برای dispatch روی خود رکورد ارسال. هر دو فقط خوانده می‌شوند.
    private async Task<List<TruckSettlementSettledRowViewModel>> BuildSettledRowsAsync(
        string? query,
        TruckSettlementSourceKind? kind)
    {
        var rows = new List<TruckSettlementSettledRowViewModel>();

        if (kind is null or TruckSettlementSourceKind.Leg)
        {
            var legs = await _db.InventoryTransportLegs
                .AsNoTracking()
                .Where(l => l.IsFreightSettled
                    && (l.TransportType == LoadingTransportType.Truck || l.TransportType == LoadingTransportType.Wagon))
                .Select(l => new
                {
                    l.Id,
                    l.TransportType,
                    l.Status,
                    TruckPlateNumber = l.Truck != null ? l.Truck.PlateNumber : null,
                    l.WagonNumber,
                    DriverName = l.Driver != null ? l.Driver.FullName : null,
                    ProductName = l.Product != null ? l.Product.Name : null,
                    ContractNumber = l.SourcePurchaseContract != null ? l.SourcePurchaseContract.ContractNumber : null,
                    SourceName = l.SourceTerminal != null ? l.SourceTerminal.Name : null,
                    DestinationTerminalName = l.DestinationTerminal != null ? l.DestinationTerminal.Name : null,
                    DestinationLocationName = l.DestinationLocation != null ? l.DestinationLocation.Name : null,
                    ServiceProviderName = l.ServiceProvider != null ? l.ServiceProvider.Name : null,
                    OperationalAssetName = l.OperationalAsset != null ? l.OperationalAsset.Name : null,
                    l.LoadedDate,
                    l.FreightSettledDate,
                    l.QuantityMt
                })
                .ToListAsync();

            var legIds = legs.Select(l => l.Id).ToList();
            var remainingByLeg = await _quantities.GetRemainingMtAsync(legIds);
            var receiptTotals = await _db.InventoryTransportReceipts
                .AsNoTracking()
                .Where(r => legIds.Contains(r.InventoryTransportLegId) && !r.IsCancelled)
                .GroupBy(r => r.InventoryTransportLegId)
                .Select(g => new
                {
                    LegId = g.Key,
                    FreightUsd = g.Sum(r => r.FreightPayableUsd ?? r.FreightCostUsd ?? 0m),
                    ShortageMt = g.Sum(r => r.ShortageQuantityMt)
                })
                .ToDictionaryAsync(x => x.LegId, x => x);

            foreach (var leg in legs)
            {
                remainingByLeg.TryGetValue(leg.Id, out var remainingMt);
                receiptTotals.TryGetValue(leg.Id, out var totals);
                rows.Add(new TruckSettlementSettledRowViewModel
                {
                    Kind = TruckSettlementSourceKind.Leg,
                    SourceId = leg.Id,
                    TypeLabel = leg.TransportType == LoadingTransportType.Wagon
                        ? UiText.T(HttpContext, "واگن (حمل از موجودی)", "Wagon (inventory transfer)")
                        : UiText.T(HttpContext, "موتر (حمل از موجودی)", "Truck (inventory transfer)"),
                    VehicleNumber = leg.TruckPlateNumber ?? leg.WagonNumber ?? $"#{leg.Id}",
                    DriverName = leg.DriverName,
                    ProductName = leg.ProductName ?? "",
                    ContractNumber = leg.ContractNumber ?? "",
                    SourceName = leg.SourceName,
                    DestinationName = leg.DestinationTerminalName ?? leg.DestinationLocationName,
                    Date = leg.LoadedDate,
                    SettledDate = leg.FreightSettledDate,
                    QuantityMt = leg.QuantityMt,
                    RemainingQuantityMt = remainingMt,
                    ShortageMt = totals?.ShortageMt ?? 0m,
                    FreightUsd = totals?.FreightUsd ?? 0m,
                    FreightPartyName = leg.ServiceProviderName ?? leg.OperationalAssetName ?? leg.DriverName,
                    IsUnloaded = remainingMt <= QuantityEpsilon
                });
            }
        }

        if (kind is null or TruckSettlementSourceKind.Dispatch)
        {
            var dispatches = await _db.TruckDispatches
                .AsNoTracking()
                .Where(d => d.IsFreightSettled && d.Status != DispatchStatus.Cancelled)
                .Select(d => new
                {
                    d.Id,
                    d.Status,
                    TruckPlateNumber = d.Truck != null ? d.Truck.PlateNumber : null,
                    DriverName = d.Driver != null ? d.Driver.FullName : null,
                    ProductName = d.Product != null ? d.Product.Name : null,
                    ContractNumber = d.Contract != null ? d.Contract.ContractNumber : null,
                    DestinationLocationName = d.DestinationLocation != null ? d.DestinationLocation.Name : null,
                    ServiceProviderName = d.ServiceProvider != null ? d.ServiceProvider.Name : null,
                    OperationalAssetName = d.OperationalAsset != null ? d.OperationalAsset.Name : null,
                    d.DispatchDate,
                    d.FreightSettledDate,
                    d.LoadedQuantityMt,
                    d.ShortageMt,
                    d.FreightPayableUsd,
                    d.FreightCostUsd
                })
                .ToListAsync();

            var arrivalMovements = await _db.InventoryMovements
                .AsNoTracking()
                .Where(m => m.Direction == MovementDirection.In
                    && m.ReferenceDocument != null
                    && m.ReferenceDocument.StartsWith(ArrivalRefPrefix))
                .Select(m => new { m.ReferenceDocument, m.QuantityMt })
                .ToListAsync();

            var arrivalsByDispatch = arrivalMovements
                .Select(m => new { DispatchId = ParseArrivalDispatchId(m.ReferenceDocument!), m.QuantityMt })
                .Where(x => x.DispatchId.HasValue)
                .GroupBy(x => x.DispatchId!.Value)
                .ToDictionary(g => g.Key, g => g.Sum(x => x.QuantityMt));

            foreach (var dispatch in dispatches)
            {
                arrivalsByDispatch.TryGetValue(dispatch.Id, out var arrivalsMt);
                var remainingMt = decimal.Round(
                    dispatch.LoadedQuantityMt - arrivalsMt,
                    4,
                    MidpointRounding.AwayFromZero);
                rows.Add(new TruckSettlementSettledRowViewModel
                {
                    Kind = TruckSettlementSourceKind.Dispatch,
                    SourceId = dispatch.Id,
                    TypeLabel = UiText.T(HttpContext, "ارسال موتر", "Truck send"),
                    VehicleNumber = dispatch.TruckPlateNumber ?? $"#{dispatch.Id}",
                    DriverName = dispatch.DriverName,
                    ProductName = dispatch.ProductName ?? "",
                    ContractNumber = dispatch.ContractNumber ?? "",
                    SourceName = null,
                    DestinationName = dispatch.DestinationLocationName,
                    Date = dispatch.DispatchDate,
                    SettledDate = dispatch.FreightSettledDate,
                    QuantityMt = dispatch.LoadedQuantityMt,
                    RemainingQuantityMt = remainingMt,
                    ShortageMt = dispatch.ShortageMt ?? 0m,
                    FreightUsd = dispatch.FreightPayableUsd ?? dispatch.FreightCostUsd ?? 0m,
                    FreightPartyName = dispatch.ServiceProviderName ?? dispatch.OperationalAssetName ?? dispatch.DriverName,
                    IsUnloaded = remainingMt <= QuantityEpsilon || dispatch.Status == DispatchStatus.Delivered
                });
            }
        }

        rows = rows
            .OrderByDescending(r => r.SettledDate ?? r.Date)
            .ThenByDescending(r => r.SourceId)
            .ToList();

        var term = query?.Trim();
        if (!string.IsNullOrEmpty(term))
        {
            rows = rows.Where(r =>
                    Contains(r.VehicleNumber, term)
                    || Contains(r.DriverName, term)
                    || Contains(r.ProductName, term)
                    || Contains(r.ContractNumber, term)
                    || Contains(r.SourceName, term)
                    || Contains(r.DestinationName, term)
                    || Contains(r.FreightPartyName, term)
                    || Contains(r.TypeLabel, term)
                    || Contains(r.Date.ToString("yyyy-MM-dd"), term)
                    || Contains(r.SettledDate?.ToString("yyyy-MM-dd"), term))
                .ToList();

            static bool Contains(string? value, string term)
                => !string.IsNullOrEmpty(value)
                    && value.Contains(term, StringComparison.OrdinalIgnoreCase);
        }

        return rows;
    }
}
