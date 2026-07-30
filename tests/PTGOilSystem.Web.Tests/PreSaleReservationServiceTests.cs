using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.Reports;
using PTGOilSystem.Web.Services.Reporting;
using PTGOilSystem.Web.Services.Time;
using Xunit;

namespace PTGOilSystem.Web.Tests;

public class PreSaleReservationServiceTests
{
    private static readonly DateTimeOffset FixedUtcNow = new(2026, 5, 20, 6, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Only_Active_Orders_Reserve_And_Cancelled_Deliveries_Do_Not_Consume_The_Commitment()
    {
        await using var db = NewDb();
        SeedReferences(db);
        db.PreSaleOrders.AddRange(
            Order(1, quantityMt: 100m, PreSaleOrderStatus.Confirmed),
            Order(2, quantityMt: 50m, PreSaleOrderStatus.PartiallyDelivered),
            Order(3, quantityMt: 40m, PreSaleOrderStatus.Closed),
            Order(4, quantityMt: 30m, PreSaleOrderStatus.Cancelled),
            Order(5, quantityMt: 20m, PreSaleOrderStatus.Draft));
        db.SalesTransactions.AddRange(
            Delivery(1, preSaleOrderId: 2, quantityMt: 20m),
            // تحویل لغوشده نباید تعهد را مصرف کند.
            Delivery(2, preSaleOrderId: 2, quantityMt: 10m, cancelled: true));
        await db.SaveChangesAsync();

        var rows = await NewService(db).GetReservationsAsync(new ManagementReportFilterViewModel());

        var row = Assert.Single(rows);
        // فقط سفارش‌های Confirmed و PartiallyDelivered: 100 + 50 = 150 تعهد.
        Assert.Equal(150m, row.CommittedMt);
        Assert.Equal(20m, row.DeliveredMt);
        Assert.Equal(130m, row.ReservedMt);
        Assert.Equal(2, row.OrderCount);
        Assert.Equal(ReservationAttribution.Scoped, row.Attribution);
    }

    [Fact]
    public async Task Over_Delivery_On_One_Order_Cannot_Reduce_The_Reservation_Of_Another()
    {
        await using var db = NewDb();
        SeedReferences(db);
        db.PreSaleOrders.AddRange(
            Order(1, quantityMt: 100m, PreSaleOrderStatus.Confirmed),
            Order(2, quantityMt: 60m, PreSaleOrderStatus.PartiallyDelivered));
        // سفارش ۲ بیش از تعهدش تحویل گرفته؛ مازاد نباید رزرو سفارش ۱ را کم کند.
        db.SalesTransactions.Add(Delivery(1, preSaleOrderId: 2, quantityMt: 90m));
        await db.SaveChangesAsync();

        var row = Assert.Single(await NewService(db).GetReservationsAsync(new ManagementReportFilterViewModel()));

        Assert.Equal(100m, row.ReservedMt);
    }

    [Fact]
    public async Task Reservation_Without_A_Company_Is_Reported_Unallocated_And_Is_Not_Netted_Against_Stock()
    {
        await using var db = NewDb();
        SeedReferences(db);
        db.PreSaleOrders.AddRange(
            Order(1, quantityMt: 40m, PreSaleOrderStatus.Confirmed),
            Order(2, quantityMt: 25m, PreSaleOrderStatus.Confirmed, companyId: null));
        db.InventoryMovements.Add(new InventoryMovement
        {
            Id = 1,
            ProductId = 1,
            ContractId = 1,
            TerminalId = 1,
            Direction = MovementDirection.In,
            MovementDate = new DateTime(2026, 5, 1),
            QuantityMt = 100m
        });
        await db.SaveChangesAsync();

        var rows = await NewService(db).GetSellableStockAsync(new ManagementReportFilterViewModel());

        var scoped = Assert.Single(rows, r => r.Attribution == ReservationAttribution.Scoped);
        Assert.Equal(100m, scoped.PhysicalStockMt);
        Assert.Equal(40m, scoped.ReservedMt);
        Assert.Equal(60m, scoped.SellableMt);
        // رزرو بدون جواز جداگانه گزارش می‌شود و از حوضچهٔ شرکت کم نمی‌شود.
        Assert.Equal(25m, scoped.UnallocatedReservedMt);
        Assert.False(scoped.IsOverReserved);
    }

    [Fact]
    public async Task Reservation_Above_Physical_Stock_Is_Flagged()
    {
        await using var db = NewDb();
        SeedReferences(db);
        db.PreSaleOrders.Add(Order(1, quantityMt: 150m, PreSaleOrderStatus.Confirmed));
        db.InventoryMovements.Add(new InventoryMovement
        {
            Id = 1,
            ProductId = 1,
            ContractId = 1,
            TerminalId = 1,
            Direction = MovementDirection.In,
            MovementDate = new DateTime(2026, 5, 1),
            QuantityMt = 100m
        });
        await db.SaveChangesAsync();

        var row = Assert.Single(await NewService(db).GetSellableStockAsync(new ManagementReportFilterViewModel()));

        Assert.True(row.IsOverReserved);
        Assert.Equal(-50m, row.SellableMt);
    }

    [Fact]
    public async Task Overdue_Commitment_Uses_The_Kabul_Business_Day()
    {
        await using var db = NewDb();
        SeedReferences(db);
        var order = Order(1, quantityMt: 100m, PreSaleOrderStatus.Confirmed);
        // امروزِ کابل در 2026-05-20 است؛ سررسید دیروز یعنی معوق.
        order.ExpectedDeliveryTo = new DateTime(2026, 5, 19);
        db.PreSaleOrders.Add(order);
        await db.SaveChangesAsync();

        var rows = await NewService(db).GetDiscrepanciesAsync(
            new ManagementReportFilterViewModel(),
            PreSaleDiscrepancyKind.OverdueUndelivered,
            skip: 0,
            take: 50);

        var row = Assert.Single(rows);
        Assert.Equal(100m, row.QuantityMt);
        Assert.Equal("Sales", row.DocumentController);
        Assert.Equal("PreSaleDetails", row.DocumentAction);
        Assert.Equal(1, row.DocumentId);
    }

    [Fact]
    public async Task Over_Delivery_Is_Reported_With_A_Drill_Down_To_The_Order()
    {
        await using var db = NewDb();
        SeedReferences(db);
        db.PreSaleOrders.Add(Order(1, quantityMt: 50m, PreSaleOrderStatus.PartiallyDelivered));
        db.SalesTransactions.AddRange(
            Delivery(1, preSaleOrderId: 1, quantityMt: 40m),
            Delivery(2, preSaleOrderId: 1, quantityMt: 25m),
            Delivery(3, preSaleOrderId: 1, quantityMt: 99m, cancelled: true));
        await db.SaveChangesAsync();

        var rows = await NewService(db).GetDiscrepanciesAsync(
            new ManagementReportFilterViewModel(),
            PreSaleDiscrepancyKind.OverDelivery,
            skip: 0,
            take: 50);

        var row = Assert.Single(rows);
        Assert.Equal(15m, row.QuantityMt);
        Assert.Equal(1, row.DocumentId);
    }

    [Fact]
    public async Task Summary_Covers_Every_Discrepancy_Category()
    {
        await using var db = NewDb();
        SeedReferences(db);
        await db.SaveChangesAsync();

        var summary = await NewService(db).GetDiscrepancySummaryAsync(new ManagementReportFilterViewModel());

        Assert.Equal(Enum.GetValues<PreSaleDiscrepancyKind>().Length, summary.Count);
        Assert.All(summary, item => Assert.False(string.IsNullOrWhiteSpace(item.Title)));
    }

    private static PreSaleReservationService NewService(ApplicationDbContext db)
        => new(db, new AfghanistanBusinessClock(new FixedTimeProvider(FixedUtcNow)));

    private static ApplicationDbContext NewDb()
        => new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static void SeedReferences(ApplicationDbContext db)
    {
        db.Companies.Add(new Company { Id = 1, Code = "PTG", Name = "PTG" });
        db.Customers.Add(new Customer { Id = 1, Name = "Customer" });
        db.Products.Add(new Product { Id = 1, Code = "GO", Name = "Gas Oil" });
        db.Terminals.Add(new Terminal { Id = 1, Code = "T1", Name = "Terminal 1" });
        db.Contracts.Add(new Contract
        {
            Id = 1,
            ContractNumber = "PUR-001",
            ContractType = ContractType.Purchase,
            CompanyId = 1,
            ProductId = 1,
            ContractDate = new DateTime(2026, 1, 1),
            QuantityMt = 500m,
            PricingMethod = PricingMethod.Fixed
        });
    }

    private static PreSaleOrder Order(
        int id,
        decimal quantityMt,
        PreSaleOrderStatus status,
        int? companyId = 1)
        => new()
        {
            Id = id,
            OrderNumber = $"PS-{id:000}",
            CustomerId = 1,
            ProductId = 1,
            CompanyId = companyId,
            OrderDate = new DateTime(2026, 5, 1),
            QuantityMt = quantityMt,
            UnitPriceUsd = 700m,
            TotalUsd = quantityMt * 700m,
            Status = status
        };

    private static SalesTransaction Delivery(
        int id,
        int preSaleOrderId,
        decimal quantityMt,
        bool cancelled = false)
        => new()
        {
            Id = id,
            CustomerId = 1,
            ProductId = 1,
            CompanyId = 1,
            PreSaleOrderId = preSaleOrderId,
            InvoiceNumber = $"INV-{id:000}",
            SaleDate = new DateTime(2026, 5, 10),
            QuantityMt = quantityMt,
            UnitPriceUsd = 700m,
            TotalUsd = quantityMt * 700m,
            IsCancelled = cancelled
        };

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
