using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Controllers;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.Reports;
using PTGOilSystem.Web.Services.Time;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// راپور «بارهای در مسیر» فقط باید باری را نشان دهد که هنوز نرسیده است:
/// بارگیری رسیدنشده، حمل داخلی با باقیمانده و موتر تخلیه‌نشده.
/// </summary>
public class GoodsInTransitReportTests
{
    private static readonly DateTime Today = new(2026, 9, 4);

    [Fact]
    public async Task Shows_Only_Loads_That_Have_Not_Arrived_Yet()
    {
        await using var db = NewDb();
        Seed(db);
        await db.SaveChangesAsync();

        var controller = new ReportsController(db, clock: new FixedClock(Today));

        var result = await controller.GoodsInTransit(new GoodsInTransitFilterViewModel());

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<GoodsInTransitReportViewModel>(view.Model);

        // بارگیری رسیدنشده ۶۰ + حمل داخلی ۳۰ + موتر بارگیری‌شده ۱۰ = ۱۰۰
        Assert.Equal(3, model.Totals.RowCount);
        Assert.Equal(100m, model.Totals.TotalQuantityMt);

        Assert.Equal(1, model.Totals.FromOriginCount);
        Assert.Equal(1, model.Totals.InternalTransferCount);
        Assert.Equal(1, model.Totals.CustomerDeliveryCount);

        var fromOrigin = Assert.Single(model.Rows, r => r.Kind == GoodsInTransitKind.FromOrigin);
        Assert.Equal(60m, fromOrigin.QuantityMt);

        var leg = Assert.Single(model.Rows, r => r.Kind == GoodsInTransitKind.InternalTransfer);
        Assert.Equal(30m, leg.QuantityMt);
        Assert.True(leg.IsDelayed);
        Assert.Equal(4, leg.DaysOnRoad);
    }

    [Fact]
    public async Task Loading_Origin_Allocation_Is_Not_Counting_The_Same_Quantity_Twice()
    {
        await using var db = NewDb();
        Seed(db);
        db.InventoryTransportLegAllocations.Add(new InventoryTransportLegAllocation
        {
            InventoryTransportLegId = 1,
            SourcePurchaseContractId = 1,
            SourceLoadingRegisterId = 1,
            QuantityMt = 30m
        });
        await db.SaveChangesAsync();

        var controller = new ReportsController(db, clock: new FixedClock(Today));
        var result = await controller.GoodsInTransit(new GoodsInTransitFilterViewModel());

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<GoodsInTransitReportViewModel>(view.Model);
        Assert.Equal(30m, Assert.Single(model.Rows, r => r.Kind == GoodsInTransitKind.FromOrigin).QuantityMt);
        Assert.Equal(30m, Assert.Single(model.Rows, r => r.Kind == GoodsInTransitKind.InternalTransfer).QuantityMt);
        Assert.Equal(70m, model.Totals.TotalQuantityMt);
    }

    [Fact]
    public async Task Delayed_Filter_Keeps_Only_Loads_Past_Their_Expected_Arrival()
    {
        await using var db = NewDb();
        Seed(db);
        await db.SaveChangesAsync();

        var controller = new ReportsController(db, clock: new FixedClock(Today));

        var result = await controller.GoodsInTransit(new GoodsInTransitFilterViewModel { DelayedOnly = true });

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<GoodsInTransitReportViewModel>(view.Model);

        var row = Assert.Single(model.Rows);
        Assert.Equal(GoodsInTransitKind.InternalTransfer, row.Kind);
    }

    [Fact]
    public async Task Kind_Filter_Limits_The_Report_To_One_Source()
    {
        await using var db = NewDb();
        Seed(db);
        await db.SaveChangesAsync();

        var controller = new ReportsController(db, clock: new FixedClock(Today));

        var result = await controller.GoodsInTransit(new GoodsInTransitFilterViewModel
        {
            Kind = GoodsInTransitKind.CustomerDelivery
        });

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<GoodsInTransitReportViewModel>(view.Model);

        var row = Assert.Single(model.Rows);
        Assert.Equal(10m, row.QuantityMt);
        Assert.Equal("TRK-1", row.VehicleLabel);
    }

    private static void Seed(ApplicationDbContext db)
    {
        db.Products.Add(new Product { Id = 1, Code = "GAS", Name = "Gasoline" });
        db.Companies.Add(new Company { Id = 1, Code = "PTG", Name = "PTG" });
        db.Suppliers.Add(new Supplier { Id = 1, Name = "Supplier A" });
        db.Customers.Add(new Customer { Id = 1, Name = "Customer A" });
        db.Terminals.Add(new Terminal { Id = 1, Code = "ILK", Name = "Ilinka" });
        db.Trucks.Add(new Truck { Id = 1, PlateNumber = "TRK-1" });
        db.Contracts.Add(new Contract
        {
            Id = 1,
            ContractNumber = "PUR-1",
            ContractName = "Purchase 1",
            ContractType = ContractType.Purchase,
            CompanyId = 1,
            ProductId = 1,
            SupplierId = 1,
            ContractDate = new DateTime(2026, 8, 1),
            QuantityMt = 500m,
            PricingMethod = PricingMethod.Fixed,
            UnitPriceUsd = 500m
        });

        // بارگیری ناقص‌رسید ⇒ ۶۰ تن هنوز در راه است.
        db.LoadingRegisters.AddRange(
            new LoadingRegister
            {
                Id = 1,
                ContractId = 1,
                ProductId = 1,
                LoadingDate = new DateTime(2026, 8, 25),
                LoadedQuantityMt = 100m,
                WagonNumber = "WGN-1"
            },
            // بارگیری کاملاً رسیده ⇒ نباید در راپور بیاید.
            new LoadingRegister
            {
                Id = 2,
                ContractId = 1,
                ProductId = 1,
                LoadingDate = new DateTime(2026, 8, 20),
                LoadedQuantityMt = 50m,
                WagonNumber = "WGN-2"
            });
        db.LoadingReceipts.AddRange(
            new LoadingReceipt
            {
                Id = 1,
                LoadingRegisterId = 1,
                TerminalId = 1,
                ReceiptDate = new DateTime(2026, 8, 30),
                ReceivedQuantityMt = 40m
            },
            new LoadingReceipt
            {
                Id = 2,
                LoadingRegisterId = 2,
                TerminalId = 1,
                ReceiptDate = new DateTime(2026, 8, 28),
                ReceivedQuantityMt = 50m
            });

        db.InventoryTransportLegs.AddRange(
            // در مسیر با باقیماندهٔ کامل و تاریخ رسیدنِ گذشته ⇒ تأخیردار.
            new InventoryTransportLeg
            {
                Id = 1,
                SourcePurchaseContractId = 1,
                ProductId = 1,
                SourceTerminalId = 1,
                TransportType = LoadingTransportType.Truck,
                TruckId = 1,
                LoadedDate = new DateTime(2026, 8, 31),
                ExpectedArrivalDate = new DateTime(2026, 9, 2),
                QuantityMt = 30m,
                Status = InventoryTransportLegStatus.InTransit
            },
            // رسید کامل خورده ⇒ باقیمانده صفر.
            new InventoryTransportLeg
            {
                Id = 2,
                SourcePurchaseContractId = 1,
                ProductId = 1,
                SourceTerminalId = 1,
                TransportType = LoadingTransportType.Truck,
                LoadedDate = new DateTime(2026, 8, 29),
                QuantityMt = 20m,
                Status = InventoryTransportLegStatus.Loaded
            },
            // رسیده ⇒ اصلاً در مسیر نیست.
            new InventoryTransportLeg
            {
                Id = 3,
                SourcePurchaseContractId = 1,
                ProductId = 1,
                SourceTerminalId = 1,
                TransportType = LoadingTransportType.Truck,
                LoadedDate = new DateTime(2026, 8, 20),
                QuantityMt = 25m,
                Status = InventoryTransportLegStatus.Received
            });
        db.InventoryTransportReceipts.Add(new InventoryTransportReceipt
        {
            Id = 1,
            InventoryTransportLegId = 2,
            ReceiptDate = new DateTime(2026, 9, 1),
            ReceivedQuantityMt = 20m
        });

        db.TruckDispatches.AddRange(
            new TruckDispatch
            {
                Id = 1,
                ContractId = 1,
                ProductId = 1,
                TruckId = 1,
                DispatchDate = new DateTime(2026, 9, 1),
                LoadedQuantityMt = 10m,
                Status = DispatchStatus.Loaded
            },
            // تخلیه‌شده ⇒ نباید در راپور بیاید.
            new TruckDispatch
            {
                Id = 2,
                ContractId = 1,
                ProductId = 1,
                TruckId = 1,
                DispatchDate = new DateTime(2026, 8, 26),
                LoadedQuantityMt = 15m,
                DischargedQuantityMt = 15m,
                Status = DispatchStatus.Delivered
            });
    }

    private static ApplicationDbContext NewDb()
        => new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private sealed class FixedClock(DateTime today) : IAfghanistanBusinessClock
    {
        public DateTime Today => today.Date;
        public DateTimeOffset Now => new(today, TimeSpan.FromHours(4.5));
        public (DateTime StartUtc, DateTime EndUtcExclusive) UtcRange(DateTime localDate)
            => (localDate.Date, localDate.Date.AddDays(1));
    }
}
