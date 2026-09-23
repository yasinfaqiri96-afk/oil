using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// اثبات ترجمهٔ SQL روی PostgreSQL واقعی برای صفحه‌بندی سمت DB و KPIهای بدون تکرارِ
/// پروفایل موتر/راننده/واگون/کشتی (InMemory ترجمه را نمی‌سنجد).
/// </summary>
[Collection(AccountingPostgreSqlCollection.CollectionName)]
[Trait("Category", "PostgreSql")]
[Trait("Category", "Integration")]
public sealed class TransportResourceProfileBuilderPostgresTests(AccountingPostgreSqlFixture fixture)
{
    private ApplicationDbContext CreateContext()
        => new(new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(fixture.ConnectionString).Options);

    [Fact]
    public async Task Profiles_Translate_On_PostgreSql_With_Paging_And_Deduplicated_Kpis()
    {
        var marker = Guid.NewGuid().ToString("N")[..10];
        var day = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);

        await using var db = CreateContext();
        var product = new Product { Code = $"P-{marker}", Name = $"Diesel {marker}" };
        var company = new Company { Code = $"C-{marker}", Name = $"Company {marker}" };
        var supplier = new Supplier { Name = $"Supplier {marker}" };
        var terminal = new Terminal { Code = $"T-{marker}", Name = $"Terminal {marker}" };
        var expenseType = new ExpenseType { Code = $"ET-{marker}", Name = $"Toll {marker}" };
        var sarraf = new Sarraf { Name = $"Sarraf {marker}", IsActive = true };
        var truck = new Truck { PlateNumber = $"TRK-{marker}" };
        var driver = new Driver { FullName = $"Driver {marker}" };
        var vessel = new Vessel { Name = $"Vessel {marker}" };
        var wagon = new Wagon { WagonNumber = $"W-{marker}" };
        db.AddRange(product, company, supplier, terminal, expenseType, sarraf, truck, driver, vessel, wagon);
        await db.SaveChangesAsync();

        var contract = new Contract
        {
            ContractNumber = $"CON-{marker}",
            ContractType = ContractType.Purchase,
            Status = ContractStatus.Active,
            CompanyId = company.Id,
            ProductId = product.Id,
            SupplierId = supplier.Id,
            ContractDate = day,
            QuantityMt = 5000m,
            PricingMethod = PricingMethod.Fixed
        };
        db.Add(contract);
        await db.SaveChangesAsync();

        // موتر: ۱۲ حواله (یکی لغو) + بارگیری + انتقالِ تخصیص‌یافته از همان بارگیری.
        for (var i = 0; i < 12; i++)
        {
            db.Add(new TruckDispatch
            {
                ContractId = contract.Id,
                ProductId = product.Id,
                TruckId = truck.Id,
                DriverId = driver.Id,
                DispatchDate = day.AddDays(i),
                LoadedQuantityMt = 10m,
                TicketSerialNumber = $"TK-{marker}-{i:00}",
                Status = i == 0 ? DispatchStatus.Cancelled : DispatchStatus.Delivered
            });
        }

        var truckLoading = new LoadingRegister { ContractId = contract.Id, ProductId = product.Id, TruckId = truck.Id, LoadingDate = day.AddDays(20), LoadedQuantityMt = 30m, BillOfLadingNumber = $"BL-{marker}" };
        var vesselLoading1 = new LoadingRegister { ContractId = contract.Id, ProductId = product.Id, VesselId = vessel.Id, LoadingDate = day, LoadedQuantityMt = 600m };
        var vesselLoading2 = new LoadingRegister { ContractId = contract.Id, ProductId = product.Id, VesselId = vessel.Id, LoadingDate = day, LoadedQuantityMt = 400m, BillOfLadingNumber = $"VBL-{marker}" };
        var wagonLoading = new LoadingRegister { ContractId = contract.Id, ProductId = product.Id, WagonNumber = wagon.WagonNumber, LoadingDate = day, LoadedQuantityMt = 63m, RwbNo = $"RWB-{marker}" };
        var shipment = new Shipment { ShipmentCode = $"SH-{marker}", VesselId = vessel.Id, ContractId = contract.Id, DepartureDate = day.AddDays(1), QuantityMt = 1000m };
        db.AddRange(truckLoading, vesselLoading1, vesselLoading2, wagonLoading, shipment);
        await db.SaveChangesAsync();

        var truckLeg = new InventoryTransportLeg { SourcePurchaseContractId = contract.Id, ProductId = product.Id, TruckId = truck.Id, DriverId = driver.Id, LoadedDate = day.AddDays(21), QuantityMt = 30m, Status = InventoryTransportLegStatus.InTransit, RwbNo = $"LR-{marker}" };
        var vesselLeg = new InventoryTransportLeg { SourcePurchaseContractId = contract.Id, ProductId = product.Id, VesselId = vessel.Id, ShipmentId = shipment.Id, LoadedDate = day.AddDays(2), QuantityMt = 990m, Status = InventoryTransportLegStatus.Received };
        var wagonLeg = new InventoryTransportLeg { SourcePurchaseContractId = contract.Id, ProductId = product.Id, WagonId = wagon.Id, LoadedDate = day.AddDays(3), QuantityMt = 63m, Status = InventoryTransportLegStatus.Received };
        db.AddRange(truckLeg, vesselLeg, wagonLeg);
        await db.SaveChangesAsync();

        db.AddRange(
            new InventoryTransportLegAllocation { InventoryTransportLegId = truckLeg.Id, SourcePurchaseContractId = contract.Id, SourceLoadingRegisterId = truckLoading.Id, QuantityMt = 30m },
            new InventoryTransportLegAllocation { InventoryTransportLegId = wagonLeg.Id, SourcePurchaseContractId = contract.Id, SourceLoadingRegisterId = wagonLoading.Id, QuantityMt = 63m },
            new ShipmentLoadingAllocation { ShipmentId = shipment.Id, ContractId = contract.Id, LoadingRegisterId = vesselLoading1.Id, QuantityMt = 600m },
            new ShipmentLoadingAllocation { ShipmentId = shipment.Id, ContractId = contract.Id, LoadingRegisterId = vesselLoading2.Id, QuantityMt = 400m },
            new LoadingReceipt { LoadingRegisterId = vesselLoading2.Id, TerminalId = terminal.Id, ReceiptDate = day.AddDays(5), ReceivedQuantityMt = 400m, ReferenceDocument = $"RC-{marker}" },
            new ExpenseTransaction { ExpenseTypeId = expenseType.Id, DriverId = driver.Id, ExpenseDate = day, Amount = 10m, AmountUsd = 10m },
            new SarrafSettlement { SarrafId = sarraf.Id, DriverId = driver.Id, SettlementDate = day, ReferenceNumber = $"SS-{marker}", Status = SarrafSettlementStatus.Posted });
        var asset = new OperationalAsset { AssetCode = $"A-{marker}", Name = "Tanker", LinkedTruckId = truck.Id };
        db.Add(asset);
        await db.SaveChangesAsync();
        db.Add(new AssetDocument { OperationalAssetId = asset.Id, DocumentType = AssetDocumentType.Insurance, DocumentNumber = $"INS-{marker}", ExpiryDate = day, OriginalFileName = "a.pdf", StoredFileName = "a", FilePath = "a" });
        await db.SaveChangesAsync();

        await using var read = CreateContext();
        var t = await read.Trucks.SingleAsync(x => x.Id == truck.Id);

        var truckTrips = await TransportResourceProfileBuilder.ForTruckAsync(read, t, "trips", tripsPage: 2, today: day.AddDays(30));
        Assert.Equal(14, truckTrips.TripTotalCount);
        Assert.Equal(2, truckTrips.TripPageCount);
        Assert.Equal(4, truckTrips.Trips.Count);
        Assert.Equal(12, truckTrips.OperationCount); // ۱۱ حوالهٔ فعال + (بارگیری+انتقال)
        Assert.Equal(1, truckTrips.CancelledRecordCount);
        Assert.Equal(140m, truckTrips.OperationQuantityMt);
        Assert.Equal(1, truckTrips.InProgressOperationCount);
        Assert.Equal(1, truckTrips.ExpiredDocumentCount);

        var truckDocs = await TransportResourceProfileBuilder.ForTruckAsync(read, t, "docs", docsPage: 1, today: day.AddDays(30));
        Assert.Equal(15, truckDocs.DocumentTotalCount); // ۱۲ تکت + BL + RWB انتقال + بیمه
        Assert.Equal(10, truckDocs.Documents.Count);

        var d = await read.Drivers.SingleAsync(x => x.Id == driver.Id);
        var driverDocs = await TransportResourceProfileBuilder.ForDriverAsync(read, d, "docs", docsPage: 2);
        Assert.Equal(15, driverDocs.DocumentTotalCount); // ۱۲ تکت + RWB انتقال + مصرف + تسویه
        Assert.Equal(5, driverDocs.Documents.Count);
        Assert.Equal(12, driverDocs.OperationCount);

        var v = await read.Vessels.SingleAsync(x => x.Id == vessel.Id);
        var vesselTrips = await TransportResourceProfileBuilder.ForVesselAsync(read, v, "trips");
        Assert.Equal(4, vesselTrips.TripTotalCount);
        Assert.Equal(1, vesselTrips.OperationCount);
        Assert.Equal(1000m, vesselTrips.OperationQuantityMt);
        var vesselDocs = await TransportResourceProfileBuilder.ForVesselAsync(read, v, "docs");
        Assert.Equal(2, vesselDocs.DocumentTotalCount);

        var w = await read.Wagons.SingleAsync(x => x.Id == wagon.Id);
        var wagonTrips = await TransportResourceProfileBuilder.ForWagonAsync(read, w, "trips");
        Assert.Equal(2, wagonTrips.TripTotalCount);
        Assert.Equal(1, wagonTrips.OperationCount);
        Assert.Equal(63m, wagonTrips.OperationQuantityMt);
        var wagonDocs = await TransportResourceProfileBuilder.ForWagonAsync(read, w, "docs");
        Assert.Equal(1, wagonDocs.DocumentTotalCount);
    }
}
