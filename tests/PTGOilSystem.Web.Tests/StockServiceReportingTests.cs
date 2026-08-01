using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services;
using Xunit;

namespace PTGOilSystem.Web.Tests;

public class StockServiceReportingTests
{
    [Fact]
    public async Task Stock_Card_Running_Balance_Is_Isolated_Per_Tank_And_Preserves_Opening()
    {
        await using var db = NewDb();
        db.Products.Add(new Product { Id = 1, Code = "GO", Name = "Gas Oil" });
        db.Terminals.Add(new Terminal { Id = 1, Code = "T1", Name = "Terminal" });
        db.StorageTanks.AddRange(
            new StorageTank { Id = 1, TerminalId = 1, ProductId = 1, TankCode = "TK-1" },
            new StorageTank { Id = 2, TerminalId = 1, ProductId = 1, TankCode = "TK-2" });
        db.InventoryMovements.AddRange(
            Movement(1, 1, MovementDirection.In, 100m, new DateTime(2026, 5, 1)),
            Movement(2, 2, MovementDirection.In, 50m, new DateTime(2026, 5, 2)),
            Movement(3, 1, MovementDirection.Out, 20m, new DateTime(2026, 5, 3)));
        await db.SaveChangesAsync();

        var rows = await new StockService(db).GetStockCardAsync(
            productId: 1,
            terminalId: 1,
            fromUtc: new DateTime(2026, 5, 3));

        var tank1 = Assert.Single(rows, x => x.StorageTankId == 1);
        Assert.Equal(-20m, tank1.SignedQuantityMt);
        Assert.Equal(80m, tank1.RunningBalanceMt);
        Assert.DoesNotContain(rows, x => x.StorageTankId == 2);

        var all = await new StockService(db).GetStockCardAsync(productId: 1, terminalId: 1);
        Assert.Equal(50m, all.Single(x => x.StorageTankId == 2).RunningBalanceMt);

        var summary = await new StockService(db).GetMovementSummaryAsync(
            productId: 1,
            terminalId: 1,
            fromUtc: new DateTime(2026, 5, 3));
        var tank1Summary = summary.Single(x => x.StorageTankCode == "TK-1");
        Assert.Equal(100m, tank1Summary.OpeningQuantityMt);
        Assert.Equal(20m, tank1Summary.OutQuantityMt);
        Assert.Equal(80m, tank1Summary.ClosingQuantityMt);
        Assert.Equal(1, tank1Summary.MovementCount);
    }

    private static ApplicationDbContext NewDb()
        => new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static InventoryMovement Movement(
        int id,
        int tankId,
        MovementDirection direction,
        decimal quantity,
        DateTime date)
        => new()
        {
            Id = id,
            ProductId = 1,
            TerminalId = 1,
            StorageTankId = tankId,
            Direction = direction,
            QuantityMt = quantity,
            MovementDate = date,
            ReferenceDocument = $"MOV-{id}"
        };
}
