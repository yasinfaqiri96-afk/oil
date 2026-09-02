using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services;
using Xunit;

namespace PTGOilSystem.Web.Tests;

public class InventoryTransportPnlServiceTests
{
    // Regression: فروشِ سطح‌محموله/کشتی نباید روی legِ پایین‌دستیِ «حمل از موجودی» ظاهر شود.
    // سناریو: محموله + تخصیص از دو قرارداد → legهای مبدأ. بخشی از داخل کشتی فروش شد (فروشِ سطح‌محموله).
    // باقیمانده به موجودیِ کشور ثالث رسید و بعداً یک leg «حمل از موجودی» (با واگن) از همان موجودی ساخته شد.
    [Fact]
    public async Task ShipmentSale_Does_Not_Leak_Into_Downstream_FromInventory_Leg()
    {
        await using var db = CreateDb();
        await SeedReferenceDataAsync(db);

        db.Shipments.Add(new Shipment { Id = 1, ShipmentCode = "VESSEL-1", QuantityMt = 100m });

        // legهای مبدأِ محموله از دو قرارداد (باید فروشِ کشتی را جذب کنند).
        var sourceLegA = new InventoryTransportLeg
        {
            Id = 1,
            ShipmentId = 1,
            SourcePurchaseContractId = 1,
            ProductId = 1,
            SourceTerminalId = 1,
            SourceStorageTankId = 1,
            TransportType = LoadingTransportType.Unspecified,
            LoadedDate = new DateTime(2026, 5, 1),
            QuantityMt = 60m,
            PurchaseUnitCostUsd = 500m,
            Status = InventoryTransportLegStatus.Loaded
        };
        var sourceLegB = new InventoryTransportLeg
        {
            Id = 2,
            ShipmentId = 1,
            SourcePurchaseContractId = 2,
            ProductId = 1,
            SourceTerminalId = 1,
            SourceStorageTankId = 1,
            TransportType = LoadingTransportType.Unspecified,
            LoadedDate = new DateTime(2026, 5, 1),
            QuantityMt = 40m,
            PurchaseUnitCostUsd = 500m,
            Status = InventoryTransportLegStatus.Loaded
        };

        // legِ «حمل از موجودی»: از موجودیِ کشور ثالث ساخته شده، ShipmentId والد را حفظ کرده،
        // و LoadedDate آن پیش از تاریخِ فروش است (تا ثابت شود گاردِ تاریخِ <= کافی نیست و
        // فیلترِ واقعی، پرچمِ downstream است).
        var fromInventoryLeg = new InventoryTransportLeg
        {
            Id = 3,
            ShipmentId = 1,
            InventoryTransportBatchId = 1,
            SourcePurchaseContractId = 1,
            ProductId = 1,
            SourceTerminalId = 2,
            SourceStorageTankId = 2,
            TransportType = LoadingTransportType.Wagon,
            LoadedDate = new DateTime(2026, 5, 2),
            QuantityMt = 30m,
            PurchaseUnitCostUsd = 500m,
            Status = InventoryTransportLegStatus.Loaded,
            // سهم منبع، همان سندی است که می‌گوید این بار از موجودی برداشته شده. هر مسیری که
            // leg را زیر یک Batch می‌سازد، این سهم را هم می‌سازد.
            Allocations =
            [
                new InventoryTransportLegAllocation
                {
                    SourcePurchaseContractId = 1,
                    SourceInventoryMovementId = 900,
                    QuantityMt = 30m
                }
            ]
        };

        db.InventoryTransportLegs.AddRange(sourceLegA, sourceLegB, fromInventoryLeg);

        // فروشِ سطح‌محموله (از داخل کشتی)؛ به هیچ رسید/legِ مشخصی گره نخورده.
        db.SalesTransactions.Add(new SalesTransaction
        {
            Id = 1,
            CompanyId = 1,
            CustomerId = 1,
            ProductId = 1,
            ShipmentId = 1,
            SaleStage = SaleStage.InTransit,
            InvoiceNumber = "VESSEL-SALE-1",
            SaleDate = new DateTime(2026, 5, 10),
            QuantityMt = 40m,
            UnitPriceUsd = 750m,
            TotalUsd = 30_000m
        });
        await db.SaveChangesAsync();

        var pnl = await new InventoryTransportPnlService(db)
            .BuildForLegsAsync([sourceLegA.Id, sourceLegB.Id, fromInventoryLeg.Id]);

        // legِ حمل از موجودی هیچ فروشی ندارد.
        var fromInventory = pnl[fromInventoryLeg.Id];
        Assert.Empty(fromInventory.Sales);
        Assert.Equal(0m, fromInventory.SoldQuantityMt);
        Assert.Equal(0m, fromInventory.SalesUsd);

        // legهای مبدأِ محموله همچنان کلِ فروشِ کشتی را (به نسبت مقدار) دریافت می‌کنند.
        var totalSoldOnSource = pnl[sourceLegA.Id].SoldQuantityMt + pnl[sourceLegB.Id].SoldQuantityMt;
        var totalSalesUsdOnSource = pnl[sourceLegA.Id].SalesUsd + pnl[sourceLegB.Id].SalesUsd;
        Assert.Equal(40m, totalSoldOnSource);
        Assert.Equal(30_000m, totalSalesUsdOnSource);
    }

    // تصمیمِ «آیا این leg بار را از موجودی برداشته؟» باید از نسب‌نامه بیاید، نه از اینکه leg
    // زیر سند Batch نشسته است. اینجا leg هیچ BatchId ندارد ولی سهمِ منبعِ وسیله‌به‌وسیله دارد،
    // پس نباید فروشِ سطح‌محموله را جذب کند.
    [Fact]
    public async Task ShipmentSale_Does_Not_Leak_Into_Chain_Child_Leg_Without_Batch()
    {
        await using var db = CreateDb();
        await SeedReferenceDataAsync(db);
        db.Shipments.Add(new Shipment { Id = 1, ShipmentCode = "VESSEL-1", QuantityMt = 100m });

        var originLeg = BuildShipmentOriginLeg(id: 1, contractId: 1, quantityMt: 100m);
        var chainChildLeg = new InventoryTransportLeg
        {
            Id = 2,
            ShipmentId = 1,
            InventoryTransportBatchId = null,
            SourcePurchaseContractId = 1,
            ProductId = 1,
            SourceTerminalId = 1,
            SourceStorageTankId = 1,
            TransportType = LoadingTransportType.Truck,
            LoadedDate = new DateTime(2026, 5, 2),
            QuantityMt = 30m,
            PurchaseUnitCostUsd = 500m,
            Status = InventoryTransportLegStatus.Loaded,
            Allocations =
            [
                new InventoryTransportLegAllocation
                {
                    SourcePurchaseContractId = 1,
                    SourceTransportLegId = 1,
                    QuantityMt = 30m
                }
            ]
        };
        db.InventoryTransportLegs.AddRange(originLeg, chainChildLeg);
        db.SalesTransactions.Add(BuildShipmentSale(quantityMt: 40m, totalUsd: 30_000m));
        await db.SaveChangesAsync();

        var pnl = await new InventoryTransportPnlService(db)
            .BuildForLegsAsync([originLeg.Id, chainChildLeg.Id]);

        Assert.Empty(pnl[chainChildLeg.Id].Sales);
        Assert.Equal(0m, pnl[chainChildLeg.Id].SoldQuantityMt);
        Assert.Equal(40m, pnl[originLeg.Id].SoldQuantityMt);
        Assert.Equal(30_000m, pnl[originLeg.Id].SalesUsd);
    }

    // legِ مبدأِ محموله (بدون سهم منبع) همچنان کلِ فروشِ سطح‌محموله را جذب می‌کند.
    [Fact]
    public async Task ShipmentOrigin_Leg_Still_Absorbs_Shipment_Level_Sale()
    {
        await using var db = CreateDb();
        await SeedReferenceDataAsync(db);
        db.Shipments.Add(new Shipment { Id = 1, ShipmentCode = "VESSEL-1", QuantityMt = 100m });

        var originLeg = BuildShipmentOriginLeg(id: 1, contractId: 1, quantityMt: 100m);
        db.InventoryTransportLegs.Add(originLeg);
        db.SalesTransactions.Add(BuildShipmentSale(quantityMt: 40m, totalUsd: 30_000m));
        await db.SaveChangesAsync();

        var pnl = await new InventoryTransportPnlService(db).BuildForLegsAsync([originLeg.Id]);

        Assert.Equal(40m, pnl[originLeg.Id].SoldQuantityMt);
        Assert.Equal(30_000m, pnl[originLeg.Id].SalesUsd);
    }

    // Batch با یک leg و یک سهم، و Batch با یک leg و دو سهم: هر دو «حمل از موجودی» هستند و
    // هیچ‌کدام نباید فروشِ سطح‌محموله را جذب کند؛ نرخ خرید هم از میانگین وزنیِ سهم‌ها می‌آید.
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Batch_Leg_Is_Excluded_Regardless_Of_Allocation_Count(int allocationCount)
    {
        await using var db = CreateDb();
        await SeedReferenceDataAsync(db);
        db.Shipments.Add(new Shipment { Id = 1, ShipmentCode = "VESSEL-1", QuantityMt = 100m });

        var originLeg = BuildShipmentOriginLeg(id: 1, contractId: 1, quantityMt: 100m);
        var batchLeg = new InventoryTransportLeg
        {
            Id = 2,
            ShipmentId = 1,
            InventoryTransportBatchId = 7,
            SourcePurchaseContractId = 1,
            ProductId = 1,
            SourceTerminalId = 2,
            SourceStorageTankId = 2,
            TransportType = LoadingTransportType.Truck,
            LoadedDate = new DateTime(2026, 5, 2),
            QuantityMt = 30m,
            PurchaseUnitCostUsd = 500m,
            Status = InventoryTransportLegStatus.Loaded,
            Allocations = allocationCount == 1
                ?
                [
                    new InventoryTransportLegAllocation
                    {
                        SourcePurchaseContractId = 1, SourceInventoryMovementId = 900, QuantityMt = 30m
                    }
                ]
                :
                [
                    new InventoryTransportLegAllocation
                    {
                        SourcePurchaseContractId = 1, SourceInventoryMovementId = 900, QuantityMt = 20m
                    },
                    new InventoryTransportLegAllocation
                    {
                        SourcePurchaseContractId = 2, SourceInventoryMovementId = 901, QuantityMt = 10m
                    }
                ]
        };
        db.InventoryTransportLegs.AddRange(originLeg, batchLeg);
        db.SalesTransactions.Add(BuildShipmentSale(quantityMt: 40m, totalUsd: 30_000m));
        await db.SaveChangesAsync();

        var pnl = await new InventoryTransportPnlService(db)
            .BuildForLegsAsync([originLeg.Id, batchLeg.Id]);

        Assert.Empty(pnl[batchLeg.Id].Sales);
        Assert.Equal(0m, pnl[batchLeg.Id].SoldQuantityMt);
        Assert.Equal(40m, pnl[originLeg.Id].SoldQuantityMt);
    }

    // چند legِ مبدأ: فروشِ سطح‌محموله به نسبت مقدار بین آن‌ها تقسیم می‌شود و legِ Batch سهمی نمی‌برد.
    [Fact]
    public async Task Shipment_Sale_Splits_Across_Origin_Legs_Only()
    {
        await using var db = CreateDb();
        await SeedReferenceDataAsync(db);
        db.Shipments.Add(new Shipment { Id = 1, ShipmentCode = "VESSEL-1", QuantityMt = 100m });

        var originA = BuildShipmentOriginLeg(id: 1, contractId: 1, quantityMt: 60m);
        var originB = BuildShipmentOriginLeg(id: 2, contractId: 1, quantityMt: 40m);
        var batchLeg = new InventoryTransportLeg
        {
            Id = 3,
            ShipmentId = 1,
            InventoryTransportBatchId = 7,
            SourcePurchaseContractId = 1,
            ProductId = 1,
            SourceTerminalId = 2,
            SourceStorageTankId = 2,
            TransportType = LoadingTransportType.Truck,
            LoadedDate = new DateTime(2026, 5, 2),
            QuantityMt = 50m,
            PurchaseUnitCostUsd = 500m,
            Status = InventoryTransportLegStatus.Loaded,
            Allocations =
            [
                new InventoryTransportLegAllocation
                {
                    SourcePurchaseContractId = 1, SourceInventoryMovementId = 900, QuantityMt = 50m
                }
            ]
        };
        db.InventoryTransportLegs.AddRange(originA, originB, batchLeg);
        db.SalesTransactions.Add(BuildShipmentSale(quantityMt: 50m, totalUsd: 40_000m));
        await db.SaveChangesAsync();

        var pnl = await new InventoryTransportPnlService(db)
            .BuildForLegsAsync([originA.Id, originB.Id, batchLeg.Id]);

        Assert.Equal(50m, pnl[originA.Id].SoldQuantityMt + pnl[originB.Id].SoldQuantityMt);
        Assert.Equal(40_000m, pnl[originA.Id].SalesUsd + pnl[originB.Id].SalesUsd);
        Assert.True(pnl[originA.Id].SoldQuantityMt > pnl[originB.Id].SoldQuantityMt);
        Assert.Equal(0m, pnl[batchLeg.Id].SoldQuantityMt);
    }

    private static InventoryTransportLeg BuildShipmentOriginLeg(int id, int contractId, decimal quantityMt)
        => new()
        {
            Id = id,
            ShipmentId = 1,
            InventoryTransportBatchId = null,
            SourcePurchaseContractId = contractId,
            ProductId = 1,
            SourceTerminalId = 1,
            SourceStorageTankId = 1,
            TransportType = LoadingTransportType.Unspecified,
            LoadedDate = new DateTime(2026, 5, 1),
            QuantityMt = quantityMt,
            PurchaseUnitCostUsd = 500m,
            Status = InventoryTransportLegStatus.Loaded
        };

    private static SalesTransaction BuildShipmentSale(decimal quantityMt, decimal totalUsd)
        => new()
        {
            Id = 1,
            CompanyId = 1,
            CustomerId = 1,
            ProductId = 1,
            ShipmentId = 1,
            SaleStage = SaleStage.InTransit,
            InvoiceNumber = "VESSEL-SALE-1",
            SaleDate = new DateTime(2026, 5, 10),
            QuantityMt = quantityMt,
            UnitPriceUsd = totalUsd / quantityMt,
            TotalUsd = totalUsd
        };

    private static ApplicationDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ApplicationDbContext(options);
    }

    private static async Task SeedReferenceDataAsync(ApplicationDbContext db)
    {
        db.Companies.Add(new Company { Id = 1, Code = "PTG", Name = "PTG" });
        db.Suppliers.Add(new Supplier { Id = 1, Name = "Supplier A" });
        db.Customers.Add(new Customer { Id = 1, Name = "Customer A" });
        db.Products.Add(new Product { Id = 1, Code = "GO", Name = "Gas Oil" });
        db.Terminals.AddRange(
            new Terminal { Id = 1, Code = "SRC", Name = "Source Terminal" },
            new Terminal { Id = 2, Code = "DST", Name = "Destination Terminal" });
        db.StorageTanks.AddRange(
            new StorageTank { Id = 1, TerminalId = 1, TankCode = "SRC-TK", ProductId = 1, CapacityMt = 500m },
            new StorageTank { Id = 2, TerminalId = 2, TankCode = "DST-TK", ProductId = 1, CapacityMt = 500m });
        db.Contracts.AddRange(
            new Contract
            {
                Id = 1,
                ContractNumber = "PUR-001",
                ContractType = ContractType.Purchase,
                CompanyId = 1,
                ProductId = 1,
                SupplierId = 1,
                ContractDate = new DateTime(2026, 5, 1),
                QuantityMt = 100m,
                PricingMethod = PricingMethod.Fixed,
                UnitPriceUsd = 500m
            },
            new Contract
            {
                Id = 2,
                ContractNumber = "PUR-002",
                ContractType = ContractType.Purchase,
                CompanyId = 1,
                ProductId = 1,
                SupplierId = 1,
                ContractDate = new DateTime(2026, 5, 1),
                QuantityMt = 100m,
                PricingMethod = PricingMethod.Fixed,
                UnitPriceUsd = 500m
            });
        await db.SaveChangesAsync();
    }
}
