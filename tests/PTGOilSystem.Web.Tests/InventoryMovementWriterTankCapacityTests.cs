using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Exceptions;
using Xunit;

namespace PTGOilSystem.Web.Tests;

public class InventoryMovementWriterTankCapacityTests
{
    private const int ProductId = 1;
    private const int TerminalId = 1;
    private const int TankId = 1;

    [Fact]
    public async Task Inbound_UpToExactCapacity_IsAllowed()
    {
        await using var db = await SeedAsync(capacityMt: 100m, currentMt: 80m);

        await Writer(db).PostInboundAsync(Request(20m));

        Assert.Equal(100m, await TankStockAsync(db));
    }

    [Fact]
    public async Task Inbound_OverCapacity_IsRejected()
    {
        await using var db = await SeedAsync(capacityMt: 100m, currentMt: 80m);

        var ex = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Writer(db).PostInboundAsync(Request(21m)));

        Assert.Equal("STOCK_TANK_CAPACITY_EXCEEDED", ex.Code);
        Assert.Contains("ظرفیت مخزن مقصد کافی نیست", ex.Message);
        Assert.Contains("فضای خالی:", ex.Message);
        Assert.Equal(80m, await TankStockAsync(db));
    }

    [Fact]
    public async Task Inbound_IntoFullTank_IsRejected()
    {
        await using var db = await SeedAsync(capacityMt: 100m, currentMt: 100m);

        await Assert.ThrowsAsync<BusinessRuleException>(
            () => Writer(db).PostInboundAsync(Request(0.5m)));
        await Assert.ThrowsAsync<BusinessRuleException>(
            () => Writer(db).PostInboundRangeAsync([Movement(0.5m)]));

        Assert.Equal(100m, await TankStockAsync(db));
    }

    [Fact]
    public async Task InboundRange_SumsMovementsForSameTank()
    {
        await using var db = await SeedAsync(capacityMt: 100m, currentMt: 80m);

        await Assert.ThrowsAsync<BusinessRuleException>(
            () => Writer(db).PostInboundRangeAsync([Movement(15m), Movement(6m)]));

        Assert.Equal(80m, await TankStockAsync(db));
    }

    [Fact]
    public async Task Outbound_FromFullTank_IsAllowed()
    {
        await using var db = await SeedAsync(capacityMt: 100m, currentMt: 100m);

        await Writer(db).PostOutboundAsync(Request(30m));

        Assert.Equal(70m, await TankStockAsync(db));
    }

    [Fact]
    public async Task ZeroCapacity_KeepsUnlimitedBehavior()
    {
        await using var db = await SeedAsync(capacityMt: 0m, currentMt: 500m);

        await Writer(db).PostInboundAsync(Request(1_000m));

        Assert.Equal(1_500m, await TankStockAsync(db));
    }

    private static InventoryMovementWriter Writer(ApplicationDbContext db)
        => new(db, new StockService(db));

    private static Task<decimal> TankStockAsync(ApplicationDbContext db)
        => new StockService(db).GetFreeQuantityMtAsync(ProductId, terminalId: TerminalId, storageTankId: TankId);

    private static InventoryMovementRequest Request(decimal quantityMt) => new()
    {
        ProductId = ProductId,
        TerminalId = TerminalId,
        StorageTankId = TankId,
        MovementDate = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc),
        QuantityMt = quantityMt,
        ReferenceDocument = "TEST-CAPACITY"
    };

    private static InventoryMovement Movement(decimal quantityMt) => new()
    {
        ProductId = ProductId,
        TerminalId = TerminalId,
        StorageTankId = TankId,
        MovementDate = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc),
        QuantityMt = quantityMt,
        ReferenceDocument = "TEST-CAPACITY"
    };

    private static async Task<ApplicationDbContext> SeedAsync(decimal capacityMt, decimal currentMt)
    {
        var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

        db.StorageTanks.Add(new StorageTank
        {
            Id = TankId,
            TerminalId = TerminalId,
            ProductId = ProductId,
            TankCode = "TK-1",
            CapacityMt = capacityMt
        });
        db.InventoryMovements.Add(new InventoryMovement
        {
            ProductId = ProductId,
            TerminalId = TerminalId,
            StorageTankId = TankId,
            Direction = MovementDirection.In,
            MovementDate = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc),
            QuantityMt = currentMt,
            ReferenceDocument = "TEST-OPENING"
        });
        await db.SaveChangesAsync();
        return db;
    }
}
