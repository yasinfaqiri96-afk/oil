using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services;
using Xunit;

namespace PTGOilSystem.Web.Tests;

public class TransportResourceProfileBuilderTests
{
    private static ApplicationDbContext NewDb()
        => new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static async Task<(int ContractId, int ProductId, int TruckId)> SeedPrincipalsAsync(ApplicationDbContext db)
    {
        var product = new Product { Name = "Diesel" };
        var truck = new Truck { PlateNumber = "SEED-1" };
        db.Products.Add(product);
        db.Trucks.Add(truck);
        await db.SaveChangesAsync();
        var contract = new Contract { ContractName = "C", ContractNumber = "C-1", ProductId = product.Id };
        db.Contracts.Add(contract);
        await db.SaveChangesAsync();
        return (contract.Id, product.Id, truck.Id);
    }

    [Fact]
    public async Task Truck_Profile_Combines_Dispatch_Loading_And_Leg_And_Excludes_Cancelled_From_Kpis()
    {
        await using var db = NewDb();
        var (contractId, productId, _) = await SeedPrincipalsAsync(db);
        var truck = new Truck { PlateNumber = "KBL-1" };
        db.Trucks.Add(truck);
        await db.SaveChangesAsync();

        db.TruckDispatches.AddRange(
            new TruckDispatch { ContractId = contractId, TruckId = truck.Id, ProductId = productId, DispatchDate = new DateTime(2026, 1, 1), LoadedQuantityMt = 30m, Status = DispatchStatus.InTransit, TicketSerialNumber = "T-1" },
            new TruckDispatch { ContractId = contractId, TruckId = truck.Id, ProductId = productId, DispatchDate = new DateTime(2026, 1, 2), LoadedQuantityMt = 99m, Status = DispatchStatus.Cancelled });
        db.LoadingRegisters.Add(new LoadingRegister { ContractId = contractId, TruckId = truck.Id, ProductId = productId, LoadingDate = new DateTime(2026, 1, 3), LoadedQuantityMt = 20m, BillOfLadingNumber = "BL-1" });
        db.InventoryTransportLegs.Add(new InventoryTransportLeg { TruckId = truck.Id, ProductId = productId, LoadedDate = new DateTime(2026, 1, 4), QuantityMt = 10m, Status = InventoryTransportLegStatus.Received, RwbNo = "R-1" });
        await db.SaveChangesAsync();

        var profile = await TransportResourceProfileBuilder.ForTruckAsync(db, truck, "trips");

        Assert.Equal(4, profile.TripTotalCount);
        Assert.Equal(4, profile.Trips.Count);
        Assert.Equal(3, profile.OperationCount);
        Assert.Equal(1, profile.CancelledRecordCount);
        Assert.Equal(60m, profile.OperationQuantityMt);
        Assert.Equal(1, profile.InProgressOperationCount);
        Assert.Equal(new DateTime(2026, 1, 4), profile.LastOperationDate);
        Assert.All(profile.Trips, t => Assert.Equal("Diesel", t.Product));
        Assert.Contains(profile.Trips, t => t.IsCancelled);
        Assert.Empty(profile.Documents);
        Assert.Equal(3, profile.DocumentTotalCount);

        var docs = await TransportResourceProfileBuilder.ForTruckAsync(db, truck, "docs");
        Assert.Contains(docs.Documents, d => d.Number == "T-1");
        Assert.Contains(docs.Documents, d => d.Number == "BL-1");
        Assert.Contains(docs.Documents, d => d.Number == "R-1");
    }

    [Fact]
    public async Task Truck_Leg_Allocated_From_Same_Truck_Loading_Counts_As_One_Operation()
    {
        await using var db = NewDb();
        var (contractId, productId, otherTruckId) = await SeedPrincipalsAsync(db);
        var truck = new Truck { PlateNumber = "KBL-3" };
        db.Trucks.Add(truck);
        await db.SaveChangesAsync();

        var loading = new LoadingRegister { ContractId = contractId, ProductId = productId, TruckId = truck.Id, LoadingDate = new DateTime(2026, 5, 1), LoadedQuantityMt = 30m };
        var otherTruckLoading = new LoadingRegister { ContractId = contractId, ProductId = productId, TruckId = otherTruckId, LoadingDate = new DateTime(2026, 5, 1), LoadedQuantityMt = 44m };
        var leg = new InventoryTransportLeg { ProductId = productId, TruckId = truck.Id, LoadedDate = new DateTime(2026, 5, 2), QuantityMt = 30m, Status = InventoryTransportLegStatus.InTransit };
        var legFromOtherTruck = new InventoryTransportLeg { ProductId = productId, TruckId = truck.Id, LoadedDate = new DateTime(2026, 5, 3), QuantityMt = 12m, Status = InventoryTransportLegStatus.Received };
        db.AddRange(loading, otherTruckLoading, leg, legFromOtherTruck);
        await db.SaveChangesAsync();
        db.InventoryTransportLegAllocations.AddRange(
            new InventoryTransportLegAllocation { InventoryTransportLegId = leg.Id, SourcePurchaseContractId = contractId, SourceLoadingRegisterId = loading.Id, QuantityMt = 30m },
            new InventoryTransportLegAllocation { InventoryTransportLegId = legFromOtherTruck.Id, SourcePurchaseContractId = contractId, SourceLoadingRegisterId = otherTruckLoading.Id, QuantityMt = 12m });
        await db.SaveChangesAsync();

        var profile = await TransportResourceProfileBuilder.ForTruckAsync(db, truck, null);

        // تاریخچه هر سه ثبت را نشان می‌دهد؛ KPI بارگیری + انتقالِ همان موتر را یک عملیات می‌شمارد.
        Assert.Equal(3, profile.TripTotalCount);
        Assert.Equal(2, profile.OperationCount);
        Assert.Equal(42m, profile.OperationQuantityMt);
        Assert.Equal(1, profile.InProgressOperationCount);
    }

    [Fact]
    public async Task Cancelled_Leg_Does_Not_Merge_Or_Count()
    {
        await using var db = NewDb();
        var (contractId, productId, _) = await SeedPrincipalsAsync(db);
        var truck = new Truck { PlateNumber = "KBL-4" };
        db.Trucks.Add(truck);
        await db.SaveChangesAsync();

        var loading = new LoadingRegister { ContractId = contractId, ProductId = productId, TruckId = truck.Id, LoadingDate = new DateTime(2026, 5, 1), LoadedQuantityMt = 30m };
        var cancelledLeg = new InventoryTransportLeg { ProductId = productId, TruckId = truck.Id, LoadedDate = new DateTime(2026, 5, 2), QuantityMt = 30m, Status = InventoryTransportLegStatus.Cancelled };
        db.AddRange(loading, cancelledLeg);
        await db.SaveChangesAsync();
        db.InventoryTransportLegAllocations.Add(new InventoryTransportLegAllocation { InventoryTransportLegId = cancelledLeg.Id, SourcePurchaseContractId = contractId, SourceLoadingRegisterId = loading.Id, QuantityMt = 30m });
        await db.SaveChangesAsync();

        var profile = await TransportResourceProfileBuilder.ForTruckAsync(db, truck, null);

        Assert.Equal(2, profile.TripTotalCount);
        Assert.Equal(1, profile.OperationCount);
        Assert.Equal(1, profile.CancelledRecordCount);
        Assert.Equal(30m, profile.OperationQuantityMt);
        Assert.Equal(new DateTime(2026, 5, 1), profile.LastOperationDate);
    }

    [Fact]
    public async Task Vessel_Shipment_Its_Loadings_And_Its_Leg_Count_As_One_Voyage()
    {
        await using var db = NewDb();
        var (contractId, productId, _) = await SeedPrincipalsAsync(db);
        var vessel = new Vessel { Name = "Caspian-2" };
        db.Vessels.Add(vessel);
        await db.SaveChangesAsync();

        var l1 = new LoadingRegister { ContractId = contractId, ProductId = productId, VesselId = vessel.Id, LoadingDate = new DateTime(2026, 4, 1), LoadedQuantityMt = 600m };
        var l2 = new LoadingRegister { ContractId = contractId, ProductId = productId, VesselId = vessel.Id, LoadingDate = new DateTime(2026, 4, 1), LoadedQuantityMt = 400m };
        var standalone = new LoadingRegister { ContractId = contractId, ProductId = productId, VesselId = vessel.Id, LoadingDate = new DateTime(2026, 3, 1), LoadedQuantityMt = 250m };
        var shipment = new Shipment { ShipmentCode = "SH-9", VesselId = vessel.Id, DepartureDate = new DateTime(2026, 4, 2), QuantityMt = 995m };
        db.AddRange(l1, l2, standalone, shipment);
        await db.SaveChangesAsync();
        db.ShipmentLoadingAllocations.AddRange(
            new ShipmentLoadingAllocation { ShipmentId = shipment.Id, ContractId = contractId, LoadingRegisterId = l1.Id, QuantityMt = 600m },
            new ShipmentLoadingAllocation { ShipmentId = shipment.Id, ContractId = contractId, LoadingRegisterId = l2.Id, QuantityMt = 400m });
        db.InventoryTransportLegs.AddRange(
            new InventoryTransportLeg { ProductId = productId, VesselId = vessel.Id, ShipmentId = shipment.Id, LoadedDate = new DateTime(2026, 4, 3), QuantityMt = 990m, Status = InventoryTransportLegStatus.Received },
            new InventoryTransportLeg { ProductId = productId, VesselId = vessel.Id, LoadedDate = new DateTime(2026, 4, 4), QuantityMt = 5m, Status = InventoryTransportLegStatus.Cancelled });
        await db.SaveChangesAsync();

        var profile = await TransportResourceProfileBuilder.ForVesselAsync(db, vessel, null);

        Assert.Equal(6, profile.TripTotalCount);
        Assert.Equal(2, profile.OperationCount);
        // سفر گروهی = جمع بارگیری‌ها (1000)، نه + محموله (995) + انتقال (990)؛ به‌علاوهٔ بارگیری مستقل.
        Assert.Equal(1250m, profile.OperationQuantityMt);
        Assert.Equal(1, profile.InProgressOperationCount);
        Assert.Equal(1, profile.CancelledRecordCount);
        Assert.Contains(profile.Trips, t => t.Kind == "محموله" && t.Controller == "ShipmentPnl");
    }

    [Fact]
    public async Task Vessel_Shipment_Without_Loadings_Uses_Shipment_Quantity_Not_Leg()
    {
        await using var db = NewDb();
        var (_, productId, _) = await SeedPrincipalsAsync(db);
        var vessel = new Vessel { Name = "Caspian-3" };
        db.Vessels.Add(vessel);
        await db.SaveChangesAsync();
        var shipment = new Shipment { ShipmentCode = "SH-10", VesselId = vessel.Id, DepartureDate = new DateTime(2026, 4, 2), ArrivalDate = new DateTime(2026, 4, 9), QuantityMt = 500m };
        db.Shipments.Add(shipment);
        await db.SaveChangesAsync();
        db.InventoryTransportLegs.Add(new InventoryTransportLeg { ProductId = productId, VesselId = vessel.Id, ShipmentId = shipment.Id, LoadedDate = new DateTime(2026, 4, 3), QuantityMt = 480m, Status = InventoryTransportLegStatus.Received });
        await db.SaveChangesAsync();

        var profile = await TransportResourceProfileBuilder.ForVesselAsync(db, vessel, null);

        Assert.Equal(2, profile.TripTotalCount);
        Assert.Equal(1, profile.OperationCount);
        Assert.Equal(500m, profile.OperationQuantityMt);
        Assert.Equal(0, profile.InProgressOperationCount);
    }

    [Fact]
    public async Task Truck_Profile_Flags_Expired_Linked_Asset_Documents()
    {
        await using var db = NewDb();
        var truck = new Truck { PlateNumber = "KBL-2" };
        db.Trucks.Add(truck);
        await db.SaveChangesAsync();
        var asset = new OperationalAsset { AssetCode = "A-1", Name = "Tanker", LinkedTruckId = truck.Id };
        db.OperationalAssets.Add(asset);
        await db.SaveChangesAsync();
        db.AssetDocuments.AddRange(
            new AssetDocument { OperationalAssetId = asset.Id, DocumentType = AssetDocumentType.Insurance, DocumentNumber = "INS-1", ExpiryDate = new DateTime(2026, 1, 1), OriginalFileName = "a.pdf", StoredFileName = "a", FilePath = "a" },
            new AssetDocument { OperationalAssetId = asset.Id, DocumentType = AssetDocumentType.Registration, DocumentNumber = "REG-1", ExpiryDate = new DateTime(2027, 1, 1), OriginalFileName = "b.pdf", StoredFileName = "b", FilePath = "b" });
        await db.SaveChangesAsync();

        var profile = await TransportResourceProfileBuilder.ForTruckAsync(db, truck, "docs", today: new DateTime(2026, 6, 1));

        Assert.Single(profile.LinkedAssets);
        Assert.Equal(1, profile.ExpiredDocumentCount);
        Assert.Equal(2, profile.DocumentTotalCount);
        Assert.True(profile.Documents.Single(d => d.Number == "INS-1").IsExpired);
        Assert.False(profile.Documents.Single(d => d.Number == "REG-1").IsExpired);
    }

    [Fact]
    public async Task Wagon_Profile_Uses_WagonId_And_Exact_Number_Only()
    {
        await using var db = NewDb();
        var (contractId, productId, _) = await SeedPrincipalsAsync(db);
        var wagon = new Wagon { WagonNumber = "W-100" };
        db.Wagons.Add(wagon);
        await db.SaveChangesAsync();

        db.InventoryTransportLegs.AddRange(
            new InventoryTransportLeg { ProductId = productId, WagonId = wagon.Id, WagonNumber = "OTHER", LoadedDate = new DateTime(2026, 2, 1), QuantityMt = 60m },
            new InventoryTransportLeg { ProductId = productId, WagonNumber = "W-100", LoadedDate = new DateTime(2026, 2, 2), QuantityMt = 61m },
            new InventoryTransportLeg { ProductId = productId, WagonNumber = "W-1000", LoadedDate = new DateTime(2026, 2, 3), QuantityMt = 62m });
        db.LoadingRegisters.AddRange(
            new LoadingRegister { ProductId = productId, ContractId = contractId, WagonNumber = "W-100", LoadingDate = new DateTime(2026, 2, 4), LoadedQuantityMt = 63m },
            new LoadingRegister { ProductId = productId, ContractId = contractId, WagonNumber = "W-100, W-200", LoadingDate = new DateTime(2026, 2, 5), LoadedQuantityMt = 64m });
        await db.SaveChangesAsync();

        var profile = await TransportResourceProfileBuilder.ForWagonAsync(db, wagon, null);

        Assert.Equal(3, profile.TripTotalCount);
        Assert.Equal(3, profile.OperationCount);
        Assert.Equal(184m, profile.OperationQuantityMt);
    }

    [Fact]
    public async Task Wagon_Leg_Allocated_From_Wagon_Loading_Counts_Once()
    {
        await using var db = NewDb();
        var (contractId, productId, _) = await SeedPrincipalsAsync(db);
        var wagon = new Wagon { WagonNumber = "W-7" };
        db.Wagons.Add(wagon);
        await db.SaveChangesAsync();
        var loading = new LoadingRegister { ProductId = productId, ContractId = contractId, WagonNumber = "W-7", LoadingDate = new DateTime(2026, 2, 4), LoadedQuantityMt = 63m };
        var leg = new InventoryTransportLeg { ProductId = productId, WagonId = wagon.Id, LoadedDate = new DateTime(2026, 2, 6), QuantityMt = 63m, Status = InventoryTransportLegStatus.Received };
        db.AddRange(loading, leg);
        await db.SaveChangesAsync();
        db.InventoryTransportLegAllocations.Add(new InventoryTransportLegAllocation { InventoryTransportLegId = leg.Id, SourcePurchaseContractId = contractId, SourceLoadingRegisterId = loading.Id, QuantityMt = 63m });
        await db.SaveChangesAsync();

        var profile = await TransportResourceProfileBuilder.ForWagonAsync(db, wagon, null);

        Assert.Equal(2, profile.TripTotalCount);
        Assert.Equal(1, profile.OperationCount);
        Assert.Equal(63m, profile.OperationQuantityMt);
        Assert.Equal(new DateTime(2026, 2, 6), profile.LastOperationDate);
    }

    [Fact]
    public async Task Driver_Profile_Includes_Legs_Expenses_And_Settlements_Without_Cancelled()
    {
        await using var db = NewDb();
        var (contractId, productId, truckId) = await SeedPrincipalsAsync(db);
        var driver = new Driver { FullName = "Ahmad" };
        db.Drivers.Add(driver);
        var expenseType = new ExpenseType { Name = "Toll" };
        var sarraf = new Sarraf { Name = "Sarraf A" };
        db.ExpenseTypes.Add(expenseType);
        db.Sarrafs.Add(sarraf);
        await db.SaveChangesAsync();

        db.TruckDispatches.Add(new TruckDispatch { ProductId = productId, ContractId = contractId, TruckId = truckId, DriverId = driver.Id, DispatchDate = new DateTime(2026, 3, 1), LoadedQuantityMt = 30m, Status = DispatchStatus.Delivered });
        db.InventoryTransportLegs.Add(new InventoryTransportLeg { ProductId = productId, DriverId = driver.Id, LoadedDate = new DateTime(2026, 3, 2), QuantityMt = 25m, Status = InventoryTransportLegStatus.Loaded });
        db.ExpenseTransactions.AddRange(
            new ExpenseTransaction { ExpenseTypeId = expenseType.Id, DriverId = driver.Id, ExpenseDate = new DateTime(2026, 3, 3), Amount = 10m },
            new ExpenseTransaction { ExpenseTypeId = expenseType.Id, DriverId = driver.Id, ExpenseDate = new DateTime(2026, 3, 4), Amount = 11m, IsCancelled = true });
        db.SarrafSettlements.AddRange(
            new SarrafSettlement { SarrafId = sarraf.Id, DriverId = driver.Id, SettlementDate = new DateTime(2026, 3, 5), ReferenceNumber = "SS-OK", Status = SarrafSettlementStatus.Posted },
            new SarrafSettlement { SarrafId = sarraf.Id, DriverId = driver.Id, SettlementDate = new DateTime(2026, 3, 6), ReferenceNumber = "SS-X", Status = SarrafSettlementStatus.Cancelled });
        await db.SaveChangesAsync();

        var profile = await TransportResourceProfileBuilder.ForDriverAsync(db, driver, "docs");

        Assert.Equal(2, profile.OperationCount);
        Assert.Equal(1, profile.InProgressOperationCount);
        Assert.Single(profile.Documents, d => d.Controller == "Expenses");
        Assert.Contains(profile.Documents, d => d.Number == "SS-OK");
        Assert.DoesNotContain(profile.Documents, d => d.Number == "SS-X");
    }

    [Fact]
    public async Task Trips_Are_Paged_Across_Sources_In_Date_Order_And_Page_Is_Clamped()
    {
        await using var db = NewDb();
        var (contractId, productId, _) = await SeedPrincipalsAsync(db);
        var truck = new Truck { PlateNumber = "PAGE-1" };
        db.Trucks.Add(truck);
        await db.SaveChangesAsync();

        var start = new DateTime(2026, 1, 1);
        for (var i = 0; i < 17; i++)
        {
            db.TruckDispatches.Add(new TruckDispatch { ContractId = contractId, ProductId = productId, TruckId = truck.Id, DispatchDate = start.AddDays(i * 2), LoadedQuantityMt = i, TicketSerialNumber = $"D{i:00}" });
        }
        for (var i = 0; i < 8; i++)
        {
            db.LoadingRegisters.Add(new LoadingRegister { ContractId = contractId, ProductId = productId, TruckId = truck.Id, LoadingDate = start.AddDays(i * 2 + 1), LoadedQuantityMt = 100 + i, BillOfLadingNumber = $"L{i:00}" });
        }
        await db.SaveChangesAsync();

        var expected = Enumerable.Range(0, 17).Select(i => (Date: start.AddDays(i * 2), Ref: $"D{i:00}"))
            .Concat(Enumerable.Range(0, 8).Select(i => (Date: start.AddDays(i * 2 + 1), Ref: $"L{i:00}")))
            .OrderByDescending(x => x.Date)
            .Select(x => x.Ref)
            .ToList();

        var page2 = await TransportResourceProfileBuilder.ForTruckAsync(db, truck, "trips", tripsPage: 2);
        Assert.Equal(25, page2.TripTotalCount);
        Assert.Equal(3, page2.TripPageCount);
        Assert.Equal(2, page2.TripPage);
        Assert.Equal(expected.Skip(10).Take(10), page2.Trips.Select(t => t.Reference));

        var clamped = await TransportResourceProfileBuilder.ForTruckAsync(db, truck, "trips", tripsPage: 99);
        Assert.Equal(3, clamped.TripPage);
        Assert.Equal(expected.Skip(20), clamped.Trips.Select(t => t.Reference));

        var docs = await TransportResourceProfileBuilder.ForTruckAsync(db, truck, "docs", docsPage: 3);
        Assert.Equal(25, docs.DocumentTotalCount);
        Assert.Equal(expected.Skip(20), docs.Documents.Select(d => d.Number));
        Assert.Empty(docs.Trips);
    }
    [Theory]
    [InlineData("src/PTGOilSystem.Web/Views/Shared/_TransportResourceTrips.cshtml")]
    [InlineData("src/PTGOilSystem.Web/Views/Shared/_TransportResourceDocs.cshtml")]
    public void Resource_History_Links_Are_Gated_By_Controller_Access(string view)
    {
        var markup = File.ReadAllText(Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", view.Replace('/', Path.DirectorySeparatorChar))));

        // لینک (مثلاً ShipmentPnl) فقط با همان قاعدهٔ فیلتر دسترسی ساخته می‌شود؛ بدون دسترسی، مرجع متن ساده است.
        Assert.Contains("RoleAccessRules.CanAccessController(User, item.Controller)", markup);
        Assert.Contains("@if (CanOpen(item))", markup);
        Assert.DoesNotContain("Model.Trips.Skip", markup);
        Assert.DoesNotContain("Model.Documents.Skip", markup);
    }
}
