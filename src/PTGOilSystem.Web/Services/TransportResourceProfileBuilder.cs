using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.MasterData;

namespace PTGOilSystem.Web.Services;

public static class TransportResourceProfileBuilder
{
    public static async Task<TransportResourceProfileViewModel> ForTruckAsync(
        ApplicationDbContext db,
        Truck truck,
        string? activeTab)
    {
        var trips = await BuildTruckTripsAsync(db, truck.Id);
        var documents = await BuildTruckDocumentsAsync(db, truck.Id);

        return Build(truck.Id, activeTab, trips, documents);
    }

    public static async Task<TransportResourceProfileViewModel> ForDriverAsync(
        ApplicationDbContext db,
        Driver driver,
        string? activeTab)
    {
        var trips = await BuildDriverTripsAsync(db, driver.Id);
        var documents = await BuildDriverDocumentsAsync(db, driver.Id);

        return Build(driver.Id, activeTab, trips, documents);
    }

    public static async Task<TransportResourceProfileViewModel> ForVesselAsync(
        ApplicationDbContext db,
        Vessel vessel,
        string? activeTab)
    {
        var trips = await BuildVesselTripsAsync(db, vessel.Id);
        var documents = await BuildVesselDocumentsAsync(db, vessel.Id);

        return Build(vessel.Id, activeTab, trips, documents);
    }

    public static async Task<TransportResourceProfileViewModel> ForWagonAsync(
        ApplicationDbContext db,
        Wagon wagon,
        string? activeTab)
    {
        var trips = await BuildWagonTripsAsync(db, wagon.WagonNumber);
        var documents = await BuildWagonDocumentsAsync(db, wagon.WagonNumber);

        return Build(wagon.Id, activeTab, trips, documents);
    }

    private static TransportResourceProfileViewModel Build(
        int selectedId,
        string? activeTab,
        IReadOnlyList<TransportResourceTripItem> trips,
        IReadOnlyList<TransportResourceDocumentItem> documents)
        => new()
        {
            SelectedId = selectedId,
            ActiveTab = TransportResourceProfileViewModel.NormalizeTab(activeTab),
            Trips = trips,
            Documents = documents
        };

    private static async Task<IReadOnlyList<TransportResourceTripItem>> BuildTruckTripsAsync(
        ApplicationDbContext db,
        int truckId)
    {
        var dispatches = await db.TruckDispatches
            .AsNoTracking()
            .Include(d => d.Contract)
                .ThenInclude(c => c!.DestinationLocation)
            .Include(d => d.Product)
            .Include(d => d.DestinationLocation)
            .Where(d => d.TruckId == truckId)
            .OrderByDescending(d => d.DispatchDate)
            .ToListAsync();

        return dispatches
            .Select(d => new TransportResourceTripItem
            {
                Date = d.DispatchDate,
                Title = $"حواله موتر #{d.Id}",
                Status = DispatchStatusLabel(d.Status),
                Quantity = FormatMt(d.LoadedQuantityMt),
                Route = d.DestinationLocation?.Name ?? d.Contract?.DestinationLocation?.Name ?? "مسیر ثبت نشده",
                Reference = d.TicketSerialNumber ?? d.Contract?.ContractNumber ?? $"DISP-{d.Id}",
                Controller = "Dispatch",
                Action = "Details",
                RouteId = d.Id
            })
            .ToList();
    }

    private static async Task<IReadOnlyList<TransportResourceTripItem>> BuildDriverTripsAsync(
        ApplicationDbContext db,
        int driverId)
    {
        var dispatches = await db.TruckDispatches
            .AsNoTracking()
            .Include(d => d.Contract)
                .ThenInclude(c => c!.DestinationLocation)
            .Include(d => d.Product)
            .Include(d => d.DestinationLocation)
            .Include(d => d.Truck)
            .Where(d => d.DriverId == driverId)
            .OrderByDescending(d => d.DispatchDate)
            .ToListAsync();

        return dispatches
            .Select(d => new TransportResourceTripItem
            {
                Date = d.DispatchDate,
                Title = d.Truck is null ? $"سفر #{d.Id}" : $"سفر با {d.Truck.PlateNumber}",
                Status = DispatchStatusLabel(d.Status),
                Quantity = FormatMt(d.LoadedQuantityMt),
                Route = d.DestinationLocation?.Name ?? d.Contract?.DestinationLocation?.Name ?? "مسیر ثبت نشده",
                Reference = d.TicketSerialNumber ?? d.Contract?.ContractNumber ?? $"DISP-{d.Id}",
                Controller = "Dispatch",
                Action = "Details",
                RouteId = d.Id
            })
            .ToList();
    }

    private static async Task<IReadOnlyList<TransportResourceTripItem>> BuildVesselTripsAsync(
        ApplicationDbContext db,
        int vesselId)
    {
        var loadings = await db.LoadingRegisters
            .AsNoTracking()
            .Include(l => l.Contract)
            .Include(l => l.Product)
            .Include(l => l.OriginLocation)
            .Where(l => l.VesselId == vesselId)
            .OrderByDescending(l => l.LoadingDate)
            .ToListAsync();

        var shipments = await db.Shipments
            .AsNoTracking()
            .Include(s => s.OriginLocation)
            .Include(s => s.DestinationLocation)
            .Where(s => s.VesselId == vesselId)
            .OrderByDescending(s => s.DepartureDate ?? s.ArrivalDate ?? s.CreatedAtUtc)
            .ToListAsync();

        return loadings.Select(l => new TransportResourceTripItem
            {
                Date = l.LoadingDate,
                Title = $"بارگیری {l.Product?.Name ?? "محصول"}",
                Status = "بارگیری ثبت شده",
                Quantity = FormatMt(l.LoadedQuantityMt),
                Route = BuildRoute(l.OriginLocation?.Name, l.DestinationName),
                Reference = l.BillOfLadingNumber ?? l.RwbNo ?? l.Contract?.ContractNumber ?? $"LOAD-{l.Id}",
                Controller = "Loading",
                Action = "Details",
                RouteId = l.Id
            })
            .Concat(shipments.Select(s => new TransportResourceTripItem
            {
                Date = s.DepartureDate ?? s.ArrivalDate,
                Title = $"محموله {s.ShipmentCode}",
                Status = s.ArrivalDate.HasValue ? "رسیده" : "در مسیر",
                Quantity = FormatMt(s.QuantityMt),
                Route = BuildRoute(s.OriginLocation?.Name, s.DestinationLocation?.Name),
                Reference = s.ShipmentCode,
                Controller = null,
                Action = null,
                RouteId = null
            }))
            .OrderByDescending(t => t.Date ?? DateTime.MinValue)
            .ToList();
    }

    private static async Task<IReadOnlyList<TransportResourceTripItem>> BuildWagonTripsAsync(
        ApplicationDbContext db,
        string wagonNumber)
    {
        var normalized = wagonNumber.Trim();

        var loadings = await db.LoadingRegisters
            .AsNoTracking()
            .Include(l => l.Contract)
            .Include(l => l.Product)
            .Include(l => l.OriginLocation)
            .Where(l => l.WagonNumber == normalized)
            .OrderByDescending(l => l.LoadingDate)
            .ToListAsync();

        var legs = await db.InventoryTransportLegs
            .AsNoTracking()
            .Include(l => l.Product)
            .Include(l => l.SourceTerminal)
            .Include(l => l.DestinationTerminal)
            .Include(l => l.DestinationLocation)
            .Where(l => l.WagonNumber == normalized)
            .OrderByDescending(l => l.LoadedDate)
            .ToListAsync();

        return loadings.Select(l => new TransportResourceTripItem
            {
                Date = l.LoadingDate,
                Title = $"بارگیری {l.Product?.Name ?? "محصول"}",
                Status = "بارگیری ثبت شده",
                Quantity = FormatMt(l.LoadedQuantityMt),
                Route = BuildRoute(l.OriginLocation?.Name, l.DestinationName),
                Reference = l.RwbNo ?? l.BillOfLadingNumber ?? l.Contract?.ContractNumber ?? $"LOAD-{l.Id}",
                Controller = "Loading",
                Action = "Details",
                RouteId = l.Id
            })
            .Concat(legs.Select(l => new TransportResourceTripItem
            {
                Date = l.LoadedDate,
                Title = $"انتقال ریلی {l.Product?.Name ?? "محصول"}",
                Status = TransportLegStatusLabel(l.Status),
                Quantity = FormatMt(l.QuantityMt),
                Route = BuildRoute(l.SourceTerminal?.Name, l.DestinationTerminal?.Name ?? l.DestinationLocation?.Name),
                Reference = l.RwbNo ?? l.BillOfLadingNumber ?? $"LEG-{l.Id}",
                Controller = "InventoryTransportLegs",
                Action = "Details",
                RouteId = l.Id
            }))
            .OrderByDescending(t => t.Date ?? DateTime.MinValue)
            .ToList();
    }

    private static async Task<IReadOnlyList<TransportResourceDocumentItem>> BuildTruckDocumentsAsync(
        ApplicationDbContext db,
        int truckId)
    {
        var tickets = await db.TruckDispatches
            .AsNoTracking()
            .Where(d => d.TruckId == truckId && d.TicketSerialNumber != null)
            .OrderByDescending(d => d.DispatchDate)
            .Select(d => new TransportResourceDocumentItem
            {
                Date = d.DispatchDate,
                Type = "تکت / حواله بارگیری",
                Number = d.TicketSerialNumber!,
                Source = $"حواله موتر #{d.Id}",
                Controller = "Dispatch",
                Action = "Details",
                RouteId = d.Id
            })
            .ToListAsync();

        var receipts = await db.DeliveryReceipts
            .AsNoTracking()
            .Include(r => r.TruckDispatch)
            .Where(r => r.TruckDispatch != null
                        && r.TruckDispatch.TruckId == truckId
                        && r.DocumentReference != null)
            .OrderByDescending(r => r.ReceiptDate)
            .Select(r => new TransportResourceDocumentItem
            {
                Date = r.ReceiptDate,
                Type = "رسید تخلیه",
                Number = r.DocumentReference!,
                Source = r.ReceivedBy ?? $"حواله موتر #{r.TruckDispatchId}",
                Controller = "Dispatch",
                Action = "Details",
                RouteId = r.TruckDispatchId
            })
            .ToListAsync();

        return tickets.Concat(receipts)
            .OrderByDescending(d => d.Date ?? DateTime.MinValue)
            .ToList();
    }

    private static async Task<IReadOnlyList<TransportResourceDocumentItem>> BuildDriverDocumentsAsync(
        ApplicationDbContext db,
        int driverId)
    {
        var dispatchDocs = await db.TruckDispatches
            .AsNoTracking()
            .Where(d => d.DriverId == driverId && d.TicketSerialNumber != null)
            .OrderByDescending(d => d.DispatchDate)
            .Select(d => new TransportResourceDocumentItem
            {
                Date = d.DispatchDate,
                Type = "تکت / حواله بارگیری",
                Number = d.TicketSerialNumber!,
                Source = $"حواله موتر #{d.Id}",
                Controller = "Dispatch",
                Action = "Details",
                RouteId = d.Id
            })
            .ToListAsync();

        var payments = await db.PaymentTransactions
            .AsNoTracking()
            .Where(p => p.DriverId == driverId && p.Reference != null)
            .OrderByDescending(p => p.PaymentDate)
            .ToListAsync();

        var paymentDocs = payments
            .Select(p => new TransportResourceDocumentItem
            {
                Date = p.PaymentDate,
                Type = "سند پرداخت",
                Number = p.Reference!,
                Source = p.Description ?? $"{p.Amount:N2} {p.Currency}",
                Controller = "Payments",
                Action = "Details",
                RouteId = p.Id
            })
            .ToList();

        return dispatchDocs.Concat(paymentDocs)
            .OrderByDescending(d => d.Date ?? DateTime.MinValue)
            .ToList();
    }

    private static async Task<IReadOnlyList<TransportResourceDocumentItem>> BuildVesselDocumentsAsync(
        ApplicationDbContext db,
        int vesselId)
    {
        var loadingDocs = await db.LoadingRegisters
            .AsNoTracking()
            .Where(l => l.VesselId == vesselId && (l.BillOfLadingNumber != null || l.RwbNo != null))
            .OrderByDescending(l => l.LoadingDate)
            .Select(l => new TransportResourceDocumentItem
            {
                Date = l.LoadingDate,
                Type = l.BillOfLadingNumber != null ? "Bill of Lading" : "RWB",
                Number = l.BillOfLadingNumber ?? l.RwbNo ?? "",
                Source = l.ConsigneeName ?? l.DestinationName ?? $"بارگیری #{l.Id}",
                Controller = "Loading",
                Action = "Details",
                RouteId = l.Id
            })
            .ToListAsync();

        var loadingIds = await db.LoadingRegisters
            .AsNoTracking()
            .Where(l => l.VesselId == vesselId)
            .Select(l => l.Id)
            .ToListAsync();

        var receiptDocs = await db.LoadingReceipts
            .AsNoTracking()
            .Where(r => loadingIds.Contains(r.LoadingRegisterId) && !r.IsCancelled && r.ReferenceDocument != null)
            .OrderByDescending(r => r.ReceiptDate)
            .Select(r => new TransportResourceDocumentItem
            {
                Date = r.ReceiptDate,
                Type = "رسید ورودی",
                Number = r.ReferenceDocument!,
                Source = $"بارگیری #{r.LoadingRegisterId}",
                Controller = "LoadingReceipts",
                Action = "Details",
                RouteId = r.Id
            })
            .ToListAsync();

        return loadingDocs.Concat(receiptDocs)
            .OrderByDescending(d => d.Date ?? DateTime.MinValue)
            .ToList();
    }

    private static async Task<IReadOnlyList<TransportResourceDocumentItem>> BuildWagonDocumentsAsync(
        ApplicationDbContext db,
        string wagonNumber)
    {
        var normalized = wagonNumber.Trim();

        var loadingDocs = await db.LoadingRegisters
            .AsNoTracking()
            .Where(l => l.WagonNumber == normalized && (l.RwbNo != null || l.BillOfLadingNumber != null))
            .OrderByDescending(l => l.LoadingDate)
            .Select(l => new TransportResourceDocumentItem
            {
                Date = l.LoadingDate,
                Type = l.RwbNo != null ? "RWB" : "Bill of Lading",
                Number = l.RwbNo ?? l.BillOfLadingNumber ?? "",
                Source = l.DestinationName ?? $"بارگیری #{l.Id}",
                Controller = "Loading",
                Action = "Details",
                RouteId = l.Id
            })
            .ToListAsync();

        var legDocs = await db.InventoryTransportLegs
            .AsNoTracking()
            .Where(l => l.WagonNumber == normalized && (l.RwbNo != null || l.BillOfLadingNumber != null))
            .OrderByDescending(l => l.LoadedDate)
            .Select(l => new TransportResourceDocumentItem
            {
                Date = l.LoadedDate,
                Type = l.RwbNo != null ? "RWB انتقال" : "بارنامه انتقال",
                Number = l.RwbNo ?? l.BillOfLadingNumber ?? "",
                Source = $"انتقال ریلی #{l.Id}",
                Controller = "InventoryTransportLegs",
                Action = "Details",
                RouteId = l.Id
            })
            .ToListAsync();

        return loadingDocs.Concat(legDocs)
            .OrderByDescending(d => d.Date ?? DateTime.MinValue)
            .ToList();
    }

    private static string FormatMt(decimal value) => $"{value:N3} MT";

    private static string BuildRoute(string? origin, string? destination)
    {
        var from = string.IsNullOrWhiteSpace(origin) ? null : origin.Trim();
        var to = string.IsNullOrWhiteSpace(destination) ? null : destination.Trim();

        return (from, to) switch
        {
            ({ Length: > 0 }, { Length: > 0 }) => $"{from} → {to}",
            ({ Length: > 0 }, _) => from,
            (_, { Length: > 0 }) => to,
            _ => "مسیر ثبت نشده"
        };
    }

    private static string DispatchStatusLabel(DispatchStatus status)
        => status switch
        {
            DispatchStatus.Loaded => "بارگیری شده",
            DispatchStatus.InTransit => "در حال حمل",
            DispatchStatus.Delivered => "تحویل شده",
            DispatchStatus.Cancelled => "لغو شده",
            _ => status.ToString()
        };

    private static string TransportLegStatusLabel(InventoryTransportLegStatus status)
        => status switch
        {
            InventoryTransportLegStatus.Draft => "پیش نویس",
            InventoryTransportLegStatus.Loaded => "بارگیری شده",
            InventoryTransportLegStatus.InTransit => "در مسیر انتقال",
            InventoryTransportLegStatus.Received => "رسیده",
            InventoryTransportLegStatus.Cancelled => "لغو شده",
            _ => status.ToString()
        };

}
