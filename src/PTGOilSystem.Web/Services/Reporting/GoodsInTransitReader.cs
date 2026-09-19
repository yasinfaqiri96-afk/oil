using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Helpers;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.Reports;
using PTGOilSystem.Web.Services.Time;

namespace PTGOilSystem.Web.Services.Reporting;

public sealed record GoodsInTransitSnapshot(
    List<GoodsInTransitRowViewModel> Rows,
    GoodsInTransitTotalsViewModel Totals);

public interface IGoodsInTransitReader
{
    /// <summary>همهٔ ردیف‌های فیلترشده (بدون صفحه‌بندی) به‌همراه جمع‌ها.</summary>
    Task<GoodsInTransitSnapshot> ReadAsync(GoodsInTransitFilterViewModel filter, CancellationToken cancellationToken = default);
}

/// <summary>
/// «بارهای در مسیر» — فقط‌خواندنی. هر ردیف یک بارِ زنده است که هنوز به مقصد نرسیده:
///
///   • بارگیری از مبدأ که هنوز کامل رسید نخورده  ⇒ <see cref="LoadingRegister"/>
///   • حمل داخلی مخزن/ترمینال با باقیماندهٔ مثبت ⇒ <see cref="InventoryTransportLeg"/>
///   • موتر بارگیری‌شده که هنوز تخلیه نشده        ⇒ <see cref="TruckDispatch"/>
///
/// هیچ مقداری اینجا دوباره محاسبه نمی‌شود: باقیماندهٔ حمل داخلی فقط از
/// <see cref="ITransportQuantityService"/> می‌آید و باقیماندهٔ بارگیری همان فرمول
/// صفحهٔ بارگیری است (بارگیری − رسید − کسری رسید). این کد بدون تغییر از
/// <c>ReportsController.GoodsInTransit</c> منتقل شد تا راپور وب و داشبورد موبایل یک مرجع داشته باشند.
/// </summary>
public sealed class GoodsInTransitReader(ApplicationDbContext db, IAfghanistanBusinessClock businessClock) : IGoodsInTransitReader
{
    private const decimal GoodsInTransitEpsilon = 0.0001m;

    public async Task<GoodsInTransitSnapshot> ReadAsync(
        GoodsInTransitFilterViewModel filter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var today = businessClock.Today.Date;
        var rows = new List<GoodsInTransitRowViewModel>();

        if (filter.Kind is null or GoodsInTransitKind.FromOrigin)
        {
            rows.AddRange(await LoadInTransitLoadingsAsync(filter, today, cancellationToken));
        }

        if (filter.Kind is null or GoodsInTransitKind.InternalTransfer)
        {
            rows.AddRange(await LoadInTransitTransportLegsAsync(filter, today, cancellationToken));
        }

        if (filter.Kind is null or GoodsInTransitKind.CustomerDelivery)
        {
            rows.AddRange(await LoadInTransitTruckDispatchesAsync(filter, today, cancellationToken));
        }

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var term = filter.Search.Trim();
            rows = rows
                .Where(r => r.SearchSource.Contains(term, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        if (filter.DelayedOnly)
        {
            rows = rows.Where(r => r.IsDelayed).ToList();
        }

        // قدیمی‌ترین بار بالای جدول: باری که بیشتر از همه در راه مانده اول دیده می‌شود.
        rows = rows
            .OrderBy(r => r.DepartureDate)
            .ThenBy(r => r.VehicleLabel, StringComparer.Ordinal)
            .ToList();

        var totals = new GoodsInTransitTotalsViewModel
        {
            RowCount = rows.Count,
            TotalQuantityMt = decimal.Round(rows.Sum(r => r.QuantityMt), 4, MidpointRounding.AwayFromZero),
            DelayedCount = rows.Count(r => r.IsDelayed),
            MaxDaysOnRoad = rows.Count == 0 ? 0 : rows.Max(r => r.DaysOnRoad),
            FromOriginCount = rows.Count(r => r.Kind == GoodsInTransitKind.FromOrigin),
            InternalTransferCount = rows.Count(r => r.Kind == GoodsInTransitKind.InternalTransfer),
            CustomerDeliveryCount = rows.Count(r => r.Kind == GoodsInTransitKind.CustomerDelivery)
        };

        return new GoodsInTransitSnapshot(rows, totals);
    }

    /// <summary>بارگیری‌هایی که هنوز کامل رسید نخورده‌اند — بار در راه از مبدأ.</summary>
    private async Task<List<GoodsInTransitRowViewModel>> LoadInTransitLoadingsAsync(
        GoodsInTransitFilterViewModel filter,
        DateTime today,
        CancellationToken cancellationToken)
    {
        var query = db.LoadingRegisters.AsNoTracking().AsQueryable();

        if (filter.FromDate.HasValue)
        {
            query = query.Where(l => l.LoadingDate >= filter.FromDate.Value.Date);
        }

        if (filter.ToDate.HasValue)
        {
            query = query.Where(l => l.LoadingDate <= filter.ToDate.Value.Date);
        }

        if (filter.ProductId.HasValue)
        {
            query = query.Where(l => l.ProductId == filter.ProductId.Value);
        }

        var items = await query
            .Select(l => new
            {
                l.Id,
                l.LoadingDate,
                ProductName = l.Product != null ? l.Product.Name : "",
                ContractNumber = l.Contract != null ? l.Contract.ContractNumber : null,
                SupplierName = l.Contract != null && l.Contract.Supplier != null ? l.Contract.Supplier.Name : null,
                OriginName = l.OriginLocation != null ? l.OriginLocation.Name : null,
                l.DestinationName,
                l.ConsigneeName,
                l.RouteDescription,
                VesselName = l.Vessel != null ? l.Vessel.Name : null,
                TruckPlate = l.Truck != null ? l.Truck.PlateNumber : null,
                l.WagonNumber,
                l.BillOfLadingNumber,
                l.RwbNo,
                CarrierName = l.LogisticsServiceProvider != null
                    ? l.LogisticsServiceProvider.Name
                    : l.LogisticsCompanyName,
                // همان فرمول باقیماندهٔ صفحهٔ بارگیری: بارگیری − رسید لغو‌نشده − کسری رسید.
                RemainingMt = l.LoadedQuantityMt
                    - (l.Receipts.Where(r => !r.IsCancelled).Sum(r => (decimal?)r.ReceivedQuantityMt) ?? 0m)
                    - (db.InventoryTransportLegAllocations
                        .Where(a => a.SourceLoadingRegisterId == l.Id
                            && a.InventoryTransportLeg != null
                            && a.InventoryTransportLeg.Status != InventoryTransportLegStatus.Cancelled)
                        .Sum(a => (decimal?)a.QuantityMt) ?? 0m)
                    - (db.LossEvents
                        .Where(e => (e.LoadingRegisterId == l.Id
                                || e.LoadingReceiptId.HasValue
                                    && e.LoadingReceipt != null
                                    && e.LoadingReceipt.LoadingRegisterId == l.Id)
                            && !e.IsCancelled
                            && e.Stage == LossEventStage.ReceiptShortage)
                        .Sum(e => (decimal?)(e.DifferenceQuantityMt > 0m
                            ? e.DifferenceQuantityMt
                            : e.ChargeableLossMt > 0m ? e.ChargeableLossMt : 0m)) ?? 0m)
            })
            .Where(x => x.RemainingMt > GoodsInTransitEpsilon)
            .ToListAsync(cancellationToken);

        return items.Select(x => new GoodsInTransitRowViewModel
        {
            Kind = GoodsInTransitKind.FromOrigin,
            Stage = GoodsInTransitStage.InTransit,
            SourceId = x.Id,
            LinkController = "Loading",
            VehicleLabel = FirstText(x.VesselName, x.WagonNumber, x.TruckPlate, x.BillOfLadingNumber, x.RwbNo),
            CarrierName = x.CarrierName,
            ProductName = x.ProductName,
            ContractNumber = x.ContractNumber,
            PartyName = x.SupplierName,
            OriginLabel = x.OriginName,
            DestinationLabel = FirstOrNull(x.DestinationName, x.ConsigneeName),
            RouteNote = x.RouteDescription,
            QuantityMt = decimal.Round(x.RemainingMt, 4, MidpointRounding.AwayFromZero),
            DepartureDate = x.LoadingDate,
            DaysOnRoad = DaysBetween(x.LoadingDate, today)
        }).ToList();
    }

    /// <summary>حمل‌های داخلی بارگیری‌شده/در مسیر که هنوز باقیمانده دارند.</summary>
    private async Task<List<GoodsInTransitRowViewModel>> LoadInTransitTransportLegsAsync(
        GoodsInTransitFilterViewModel filter,
        DateTime today,
        CancellationToken cancellationToken)
    {
        var query = db.InventoryTransportLegs
            .AsNoTracking()
            .Where(l => l.Status == InventoryTransportLegStatus.Loaded
                || l.Status == InventoryTransportLegStatus.InTransit);

        if (filter.FromDate.HasValue)
        {
            query = query.Where(l => l.LoadedDate >= filter.FromDate.Value.Date);
        }

        if (filter.ToDate.HasValue)
        {
            query = query.Where(l => l.LoadedDate <= filter.ToDate.Value.Date);
        }

        if (filter.ProductId.HasValue)
        {
            query = query.Where(l => l.ProductId == filter.ProductId.Value);
        }

        var items = await query
            .Select(l => new
            {
                l.Id,
                l.LoadedDate,
                l.ExpectedArrivalDate,
                l.Status,
                ProductName = l.Product != null ? l.Product.Name : "",
                ContractNumber = l.SourcePurchaseContract != null ? l.SourcePurchaseContract.ContractNumber : null,
                SourceTerminalName = l.SourceTerminal != null ? l.SourceTerminal.Name : null,
                SourceTankCode = l.SourceStorageTank == null
                    ? null
                    : l.SourceStorageTank.DisplayName == null || l.SourceStorageTank.DisplayName == ""
                        ? l.SourceStorageTank.TankCode
                        : l.SourceStorageTank.DisplayName,
                DestinationTerminalName = l.DestinationTerminal != null ? l.DestinationTerminal.Name : null,
                DestinationTankCode = l.DestinationStorageTank == null
                    ? null
                    : l.DestinationStorageTank.DisplayName == null || l.DestinationStorageTank.DisplayName == ""
                        ? l.DestinationStorageTank.TankCode
                        : l.DestinationStorageTank.DisplayName,
                DestinationLocationName = l.DestinationLocation != null ? l.DestinationLocation.Name : null,
                TruckPlate = l.Truck != null ? l.Truck.PlateNumber : null,
                WagonPlate = l.Wagon != null ? l.Wagon.WagonNumber : null,
                VesselName = l.Vessel != null ? l.Vessel.Name : null,
                l.WagonNumber,
                l.RwbNo,
                l.BillOfLadingNumber,
                l.RouteDescription,
                DriverName = l.Driver != null ? l.Driver.FullName : null,
                CarrierName = l.ServiceProvider != null
                    ? l.ServiceProvider.Name
                    : l.OperationalAsset != null ? l.OperationalAsset.Name : null
            })
            .ToListAsync(cancellationToken);

        if (items.Count == 0)
        {
            return [];
        }

        // تک‌منبع باقیماندهٔ حمل — هیچ فرمول موازی‌ای اینجا ساخته نمی‌شود.
        var remainingByLeg = await new TransportQuantityService(db)
            .GetRemainingMtAsync(items.Select(x => x.Id).ToList(), cancellationToken);

        var rows = new List<GoodsInTransitRowViewModel>();
        foreach (var x in items)
        {
            var remaining = remainingByLeg.TryGetValue(x.Id, out var mt) ? mt : 0m;
            if (remaining <= GoodsInTransitEpsilon)
            {
                continue;
            }

            rows.Add(new GoodsInTransitRowViewModel
            {
                Kind = GoodsInTransitKind.InternalTransfer,
                Stage = x.Status == InventoryTransportLegStatus.InTransit
                    ? GoodsInTransitStage.InTransit
                    : GoodsInTransitStage.Loaded,
                SourceId = x.Id,
                LinkController = "InventoryTransportLegs",
                VehicleLabel = FirstText(x.TruckPlate, x.WagonPlate, x.VesselName, x.WagonNumber, x.RwbNo, x.BillOfLadingNumber),
                CarrierName = x.CarrierName,
                DriverName = x.DriverName,
                ProductName = x.ProductName,
                ContractNumber = x.ContractNumber,
                OriginLabel = JoinPlace(x.SourceTerminalName, x.SourceTankCode),
                DestinationLabel = FirstOrNull(
                    JoinPlace(x.DestinationTerminalName, x.DestinationTankCode),
                    x.DestinationLocationName),
                RouteNote = x.RouteDescription,
                QuantityMt = remaining,
                DepartureDate = x.LoadedDate,
                ExpectedArrivalDate = x.ExpectedArrivalDate,
                DaysOnRoad = DaysBetween(x.LoadedDate, today),
                IsDelayed = x.ExpectedArrivalDate.HasValue && x.ExpectedArrivalDate.Value.Date < today
            });
        }

        return rows;
    }

    /// <summary>موترهای بارگیری‌شده/در مسیر که هنوز تخلیه نشده‌اند.</summary>
    private async Task<List<GoodsInTransitRowViewModel>> LoadInTransitTruckDispatchesAsync(
        GoodsInTransitFilterViewModel filter,
        DateTime today,
        CancellationToken cancellationToken)
    {
        // دیسپچ سازگاریِ «ادامهٔ حمل» همان بارِ مرحلهٔ فرزند است؛ اگر اینجا هم بیاید، یک بار
        // دو ردیف می‌شود و در جمعِ مقدار دوبار شمرده می‌شود.
        var continuedReceiptIds = TransportChainProjection.ContinuedTransferReceiptIds(db);

        var query = db.TruckDispatches
            .AsNoTracking()
            .Where(d => (d.Status == DispatchStatus.Loaded || d.Status == DispatchStatus.InTransit)
                && !(d.InventoryTransportReceiptId != null
                    && continuedReceiptIds.Contains(d.InventoryTransportReceiptId.Value)));

        if (filter.FromDate.HasValue)
        {
            query = query.Where(d => d.DispatchDate >= filter.FromDate.Value.Date);
        }

        if (filter.ToDate.HasValue)
        {
            query = query.Where(d => d.DispatchDate <= filter.ToDate.Value.Date);
        }

        if (filter.ProductId.HasValue)
        {
            query = query.Where(d => d.ProductId == filter.ProductId.Value);
        }

        var items = await query
            .Select(d => new
            {
                d.Id,
                d.DispatchDate,
                d.Status,
                d.LoadedQuantityMt,
                ProductName = d.Product != null ? d.Product.Name : "",
                ContractNumber = d.Contract != null ? d.Contract.ContractNumber : null,
                CustomerName = d.Contract != null && d.Contract.Customer != null ? d.Contract.Customer.Name : null,
                TruckPlate = d.Truck != null ? d.Truck.PlateNumber : null,
                DriverName = d.Driver != null ? d.Driver.FullName : null,
                DestinationName = d.DestinationLocation != null ? d.DestinationLocation.Name : null,
                OriginTerminalName = d.LoadingReceiptAllocation != null && d.LoadingReceiptAllocation.Terminal != null
                    ? d.LoadingReceiptAllocation.Terminal.Name
                    : d.InventoryTransportReceipt != null && d.InventoryTransportReceipt.DestinationTerminal != null
                        ? d.InventoryTransportReceipt.DestinationTerminal.Name
                        : null,
                CarrierName = d.ServiceProvider != null
                    ? d.ServiceProvider.Name
                    : d.OperationalAsset != null ? d.OperationalAsset.Name : null,
                d.TicketSerialNumber
            })
            .ToListAsync(cancellationToken);

        return items.Select(d => new GoodsInTransitRowViewModel
        {
            Kind = GoodsInTransitKind.CustomerDelivery,
            Stage = d.Status == DispatchStatus.InTransit
                ? GoodsInTransitStage.InTransit
                : GoodsInTransitStage.Loaded,
            SourceId = d.Id,
            LinkController = "Dispatch",
            VehicleLabel = FirstText(d.TruckPlate, d.TicketSerialNumber),
            CarrierName = d.CarrierName,
            DriverName = d.DriverName,
            ProductName = d.ProductName,
            ContractNumber = d.ContractNumber,
            PartyName = d.CustomerName,
            OriginLabel = d.OriginTerminalName,
            DestinationLabel = FirstOrNull(d.DestinationName, d.CustomerName),
            QuantityMt = decimal.Round(d.LoadedQuantityMt, 4, MidpointRounding.AwayFromZero),
            DepartureDate = d.DispatchDate,
            DaysOnRoad = DaysBetween(d.DispatchDate, today)
        }).ToList();
    }

    private static int DaysBetween(DateTime departure, DateTime today)
        => Math.Max(0, (today - departure.Date).Days);

    private static string FirstText(params string?[] candidates)
        => FirstOrNull(candidates) ?? "—";

    private static string? FirstOrNull(params string?[] candidates)
        => candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c))?.Trim();

    private static string? JoinPlace(string? terminal, string? tank)
    {
        if (string.IsNullOrWhiteSpace(terminal))
        {
            return string.IsNullOrWhiteSpace(tank) ? null : tank.Trim();
        }

        return string.IsNullOrWhiteSpace(tank) ? terminal.Trim() : $"{terminal.Trim()} — {tank.Trim()}";
    }
}
