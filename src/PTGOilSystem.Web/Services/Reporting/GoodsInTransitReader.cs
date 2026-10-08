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
/// مقدار هیچ ردیفی اینجا حساب نمی‌شود: مقدار در مسیرِ هر سه نوع فقط از
/// <see cref="GoodsInTransitQuantityReader"/> می‌آید (همان مرجع بیلانس کلی شرکت). «تا تاریخ» فیلتر
/// تاریخ گزارش است (بدون آن امروز) و «از تاریخ» فقط تاریخ حرکت را محدود می‌کند. این کلاس فقط نام،
/// مسیر و وسیله را برای نمایش می‌خواند؛ راپور وب و داشبورد موبایل از همین می‌خوانند.
/// </summary>
public sealed class GoodsInTransitReader(ApplicationDbContext db, IAfghanistanBusinessClock businessClock) : IGoodsInTransitReader
{
    public async Task<GoodsInTransitSnapshot> ReadAsync(
        GoodsInTransitFilterViewModel filter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var asOf = (filter.ToDate ?? businessClock.Today).Date;
        var quantities = (await new GoodsInTransitQuantityReader(db)
                .ReadAsync(asOf, filter.Kind, filter.ProductId, cancellationToken))
            .Where(q => !filter.FromDate.HasValue || q.DepartureDate.Date >= filter.FromDate.Value.Date)
            .ToList();
        IReadOnlyDictionary<int, GoodsInTransitQuantityRow> Of(GoodsInTransitKind kind)
            => quantities.Where(q => q.Kind == kind).ToDictionary(q => q.SourceId);

        var rows = new List<GoodsInTransitRowViewModel>();
        rows.AddRange(await LoadInTransitLoadingsAsync(Of(GoodsInTransitKind.FromOrigin), asOf, cancellationToken));
        rows.AddRange(await LoadInTransitTransportLegsAsync(Of(GoodsInTransitKind.InternalTransfer), asOf, cancellationToken));
        rows.AddRange(await LoadInTransitTruckDispatchesAsync(Of(GoodsInTransitKind.CustomerDelivery), asOf, cancellationToken));

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
        IReadOnlyDictionary<int, GoodsInTransitQuantityRow> quantities,
        DateTime asOf,
        CancellationToken cancellationToken)
    {
        if (quantities.Count == 0)
        {
            return [];
        }

        var ids = quantities.Keys.ToArray();
        var items = await db.LoadingRegisters.AsNoTracking()
            .Where(l => ids.Contains(l.Id))
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
                    : l.LogisticsCompanyName
            })
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
            QuantityMt = quantities[x.Id].RemainingMt,
            DepartureDate = x.LoadingDate,
            DaysOnRoad = DaysBetween(x.LoadingDate, asOf)
        }).ToList();
    }

    /// <summary>حمل‌های داخلی بارگیری‌شده/در مسیر که هنوز باقیمانده دارند.</summary>
    private async Task<List<GoodsInTransitRowViewModel>> LoadInTransitTransportLegsAsync(
        IReadOnlyDictionary<int, GoodsInTransitQuantityRow> quantities,
        DateTime asOf,
        CancellationToken cancellationToken)
    {
        if (quantities.Count == 0)
        {
            return [];
        }

        var ids = quantities.Keys.ToArray();
        var items = await db.InventoryTransportLegs.AsNoTracking()
            .Where(l => ids.Contains(l.Id))
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

        var rows = new List<GoodsInTransitRowViewModel>();
        foreach (var x in items)
        {
            rows.Add(new GoodsInTransitRowViewModel
            {
                Kind = GoodsInTransitKind.InternalTransfer,
                // حملی که امروز «رسیده» است فقط در گزارشِ تاریخ گذشته می‌آید، یعنی آن روز در مسیر بوده.
                Stage = x.Status == InventoryTransportLegStatus.Loaded
                    ? GoodsInTransitStage.Loaded
                    : GoodsInTransitStage.InTransit,
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
                QuantityMt = quantities[x.Id].RemainingMt,
                DepartureDate = x.LoadedDate,
                ExpectedArrivalDate = x.ExpectedArrivalDate,
                DaysOnRoad = DaysBetween(x.LoadedDate, asOf),
                IsDelayed = x.ExpectedArrivalDate.HasValue && x.ExpectedArrivalDate.Value.Date < asOf
            });
        }

        return rows;
    }

    /// <summary>موترهای بارگیری‌شده/در مسیر که هنوز تخلیه نشده‌اند.</summary>
    private async Task<List<GoodsInTransitRowViewModel>> LoadInTransitTruckDispatchesAsync(
        IReadOnlyDictionary<int, GoodsInTransitQuantityRow> quantities,
        DateTime asOf,
        CancellationToken cancellationToken)
    {
        if (quantities.Count == 0)
        {
            return [];
        }

        // موترِ فروخته‌شده هم تا تخلیه فیزیکی در مسیر است، پس اینجا می‌آید (بیلانس آن را طلب می‌شمارد).
        var ids = quantities.Keys.ToArray();
        var items = await db.TruckDispatches.AsNoTracking()
            .Where(d => ids.Contains(d.Id))
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
            QuantityMt = quantities[d.Id].RemainingMt,
            DepartureDate = d.DispatchDate,
            DaysOnRoad = DaysBetween(d.DispatchDate, asOf)
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
