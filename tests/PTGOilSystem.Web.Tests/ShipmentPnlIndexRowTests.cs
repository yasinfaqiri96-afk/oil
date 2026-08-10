using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Controllers;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.ShipmentPnl;
using PTGOilSystem.Web.Services.Reporting;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// لیست محموله‌ها با query سبک ساخته می‌شود و رول‌آپ کامل مالی را اجرا نمی‌کند.
/// این تست‌ها نگه می‌دارند که «سبک» به معنی «متفاوت» نباشد: مقدار، تعداد حمل، تعداد فروش
/// و تعداد مصرفِ ردیف لیست باید دقیقاً همان چیزی باشد که رول‌آپ می‌شمارد، و سود محقق‌شده
/// همچنان فقط از ProfitAndLossService بیاید.
/// </summary>
public class ShipmentPnlIndexRowTests
{
    [Fact]
    public async Task Index_Row_Counts_Match_The_Financial_Rollup()
    {
        await using var db = NewDb();
        SeedShipmentWithFullActivity(db);
        await db.SaveChangesAsync();

        var controller = new ShipmentPnlController(db);

        var view = Assert.IsType<ViewResult>(await controller.Index());
        var model = Assert.IsType<ShipmentPnlIndexViewModel>(view.Model);
        var row = Assert.Single(model.Items);

        var rollupItem = Assert.Single(await controller.BuildAllIndexItemsAsync());

        Assert.Equal(rollupItem.QuantityMt, row.QuantityMt);
        Assert.Equal(rollupItem.ProductName, row.ProductName);
        Assert.Equal(rollupItem.RelatedTransportLegCount, row.RelatedTransportLegCount);
        Assert.Equal(rollupItem.RelatedSalesCount, row.RelatedSalesCount);
        Assert.Equal(rollupItem.RelatedExpensesCount, row.RelatedExpensesCount);
        Assert.Equal(rollupItem.TotalSalesUsd, row.TotalSalesUsd);
    }

    [Fact]
    public async Task Index_Row_Realised_Profit_Comes_From_ProfitAndLossService()
    {
        await using var db = NewDb();
        SeedShipmentWithFullActivity(db);
        await db.SaveChangesAsync();

        // هر دو فروش به همین محموله وصل‌اند: یکی مستقیم (ShipmentId) و یکی از راه رسید حمل.
        var expected = await new ProfitAndLossService(db).BuildForSalesAsync([1, 2]);

        var view = Assert.IsType<ViewResult>(await new ShipmentPnlController(db).Index());
        var model = Assert.IsType<ShipmentPnlIndexViewModel>(view.Model);
        var row = Assert.Single(model.Items);

        Assert.Equal(expected.RevenueUsd, row.TotalSalesUsd);
        Assert.Equal(expected.RevenueUsd, row.RealisedPnl.RevenueUsd);
        Assert.Equal(expected.CostOfGoodsSoldUsd, row.RealisedPnl.CostOfGoodsSoldUsd);
        Assert.Equal(expected.GrossProfitUsd, row.RealisedPnl.GrossProfitUsd);
    }

    [Fact]
    public async Task Index_Stat_Cards_Match_The_Rows_Of_A_Single_Shipment()
    {
        await using var db = NewDb();
        SeedShipmentWithFullActivity(db);
        await db.SaveChangesAsync();

        var view = Assert.IsType<ViewResult>(await new ShipmentPnlController(db).Index());
        var model = Assert.IsType<ShipmentPnlIndexViewModel>(view.Model);
        var row = Assert.Single(model.Items);

        Assert.Equal(1, model.Totals.TotalCount);
        Assert.Equal(row.QuantityMt, model.Totals.SumQuantityMt);
        Assert.Equal(row.RelatedSalesCount, model.Totals.SumRelatedSalesCount);
        Assert.Equal(row.RealisedPnl.GrossProfitUsd, model.Totals.RealisedGrossProfitUsd);
    }

    private static ApplicationDbContext NewDb()
        => new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    /// <summary>
    /// یک محموله با هر چهار منبع مصرف (کرایه رسید حمل، مصرف عملیاتی، گمرک حمل، گمرک موتر)،
    /// دو حمل، یک فروش مستقیم و یک فروش از راه رسید حمل.
    /// </summary>
    private static void SeedShipmentWithFullActivity(ApplicationDbContext db)
    {
        db.Companies.Add(new Company { Id = 1, Code = "PTG", Name = "PTG Trading" });
        db.Products.Add(new Product { Id = 1, Code = "GO", Name = "Gas Oil" });
        db.Customers.Add(new Customer { Id = 1, Name = "Customer A" });
        db.Suppliers.Add(new Supplier { Id = 1, Name = "Supplier A" });
        db.Locations.AddRange(
            new Location { Id = 1, Name = "Bandar Abbas" },
            new Location { Id = 2, Name = "Kabul Depot" });
        db.Contracts.AddRange(
            new Contract
            {
                Id = 1,
                ContractNumber = "CON-SALE",
                ContractType = ContractType.Sale,
                CompanyId = 1,
                ProductId = 1,
                CustomerId = 1,
                SupplierId = 1,
                DestinationLocationId = 2,
                ContractDate = new DateTime(2026, 4, 20),
                QuantityMt = 100m,
                PricingMethod = PricingMethod.Fixed,
                UnitPriceUsd = 500m
            },
            new Contract
            {
                Id = 2,
                ContractNumber = "CON-PURCHASE",
                ContractType = ContractType.Purchase,
                CompanyId = 1,
                ProductId = 1,
                SupplierId = 1,
                ContractDate = new DateTime(2026, 4, 18),
                QuantityMt = 100m,
                PricingMethod = PricingMethod.Fixed,
                UnitPriceUsd = 300m
            });
        db.Terminals.Add(new Terminal { Id = 1, Code = "SRC", Name = "Source terminal" });
        db.Vessels.Add(new Vessel { Id = 1, Name = "MV Test" });
        db.Shipments.Add(new Shipment
        {
            Id = 1,
            ShipmentCode = "SHIP-01",
            VesselId = 1,
            ContractId = 1,
            DepartureDate = new DateTime(2026, 4, 21),
            ArrivalDate = new DateTime(2026, 4, 28),
            OriginLocationId = 1,
            DestinationLocationId = 2,
            QuantityMt = 50m
        });
        db.ShipmentContracts.Add(new ShipmentContract { ShipmentId = 1, ContractId = 2, QuantityMt = 50m });

        db.InventoryTransportLegs.AddRange(
            new InventoryTransportLeg
            {
                Id = 100,
                ShipmentId = 1,
                SourcePurchaseContractId = 2,
                ProductId = 1,
                SourceTerminalId = 1,
                TransportType = LoadingTransportType.Vessel,
                LoadedDate = new DateTime(2026, 4, 22),
                QuantityMt = 50m,
                Status = InventoryTransportLegStatus.Received
            },
            new InventoryTransportLeg
            {
                Id = 101,
                ShipmentId = 1,
                SourcePurchaseContractId = 2,
                ProductId = 1,
                SourceTerminalId = 1,
                TransportType = LoadingTransportType.Truck,
                LoadedDate = new DateTime(2026, 4, 24),
                QuantityMt = 20m,
                Status = InventoryTransportLegStatus.Loaded
            },
            // حملِ لغوشده نه در لیست شمرده می‌شود نه در رول‌آپ.
            new InventoryTransportLeg
            {
                Id = 102,
                ShipmentId = 1,
                SourcePurchaseContractId = 2,
                ProductId = 1,
                SourceTerminalId = 1,
                TransportType = LoadingTransportType.Truck,
                LoadedDate = new DateTime(2026, 4, 25),
                QuantityMt = 5m,
                Status = InventoryTransportLegStatus.Cancelled
            });

        // رسید با کرایه ← یک ردیف «کرایه رسید حمل» می‌سازد.
        db.InventoryTransportReceipts.Add(new InventoryTransportReceipt
        {
            Id = 200,
            InventoryTransportLegId = 100,
            ReceiptDate = new DateTime(2026, 4, 23),
            ReceiptDestination = InventoryTransportReceiptDestination.ToInventory,
            DestinationTerminalId = 1,
            ReceivedQuantityMt = 50m,
            FreightCostUsd = 400m
        });
        // رسیدِ وصل به فروش ← فروش از مسیر lineage به همین محموله می‌خورد.
        db.InventoryTransportReceipts.Add(new InventoryTransportReceipt
        {
            Id = 201,
            InventoryTransportLegId = 101,
            ReceiptDate = new DateTime(2026, 4, 26),
            ReceiptDestination = InventoryTransportReceiptDestination.DirectSale,
            ReceivedQuantityMt = 20m,
            SalesTransactionId = 2
        });

        db.SalesTransactions.AddRange(
            new SalesTransaction
            {
                Id = 1,
                CompanyId = 1,
                ContractId = 1,
                CustomerId = 1,
                ProductId = 1,
                ShipmentId = 1,
                InvoiceNumber = "INV-1",
                SaleDate = new DateTime(2026, 4, 27),
                QuantityMt = 10m,
                UnitPriceUsd = 500m,
                TotalUsd = 5_000m,
                TotalInCurrency = 5_000m,
                Currency = "USD"
            },
            new SalesTransaction
            {
                Id = 2,
                CompanyId = 1,
                CustomerId = 1,
                ProductId = 1,
                InvoiceNumber = "INV-2",
                SaleDate = new DateTime(2026, 4, 26),
                QuantityMt = 20m,
                UnitPriceUsd = 450m,
                TotalUsd = 9_000m,
                TotalInCurrency = 9_000m,
                Currency = "USD"
            });
        db.SalesCostConsumptions.Add(new SalesCostConsumption
        {
            Id = 1,
            SalesTransactionId = 1,
            QuantityMt = 10m,
            CostUsd = 3_000m,
            Status = SalesCostConsumptionStatus.Active
        });

        db.ExpenseTypes.Add(new ExpenseType { Id = 1, Code = "PORT", Name = "Port", NamePersian = "هزینه بندری" });
        db.ExpenseTransactions.AddRange(
            new ExpenseTransaction
            {
                Id = 1,
                ExpenseTypeId = 1,
                ShipmentId = 1,
                ExpenseDate = new DateTime(2026, 4, 23),
                AmountUsd = 700m,
                Description = "PORT-1"
            },
            new ExpenseTransaction
            {
                Id = 2,
                ExpenseTypeId = 1,
                TransportLegId = 101,
                ExpenseDate = new DateTime(2026, 4, 25),
                AmountUsd = 150m,
                Description = "PORT-2"
            });

        db.CustomsDeclarations.Add(new CustomsDeclaration
        {
            Id = 1,
            TransportLegId = 100,
            DeclarationReference = "CD-1",
            DeclarationDate = new DateTime(2026, 4, 24),
            TotalUsd = 250m
        });
    }
}
