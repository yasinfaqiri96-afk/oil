using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.InventoryTransport;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Exceptions;
using Xunit;

namespace PTGOilSystem.Web.Tests;

[Collection(BulkFromLoadingPerformanceCollection.CollectionName)]
[Trait("Category", "PostgreSql")]
public sealed class TransportReceiptConcurrencyPostgresTests(BulkFromLoadingPerformanceFixture fixture)
{
    [Fact]
    public async Task Two_Validated_Receipts_Of_70_From_100_Commit_Only_One_Complete_Outcome()
    {
        await SeedAsync();
        await using var first = fixture.CreateDbContext();
        await using var second = fixture.CreateDbContext();
        var a = Service(first);
        var b = Service(second);
        var legA = (await a.LoadLegAsync(1, true))!;
        var legB = (await b.LoadLegAsync(1, true))!;
        var modelA = Model(70m);
        var modelB = Model(70m);
        var stateA = new ModelStateDictionary();
        var stateB = new ModelStateDictionary();
        await a.ValidateAsync(modelA, legA, stateA);
        await b.ValidateAsync(modelB, legB, stateB);
        Assert.True(stateA.IsValid);
        Assert.True(stateB.IsValid);
        var outcomes = await Task.WhenAll(Apply(a, modelA, legA), Apply(b, modelB, legB));
        Assert.Equal(1, outcomes.Count(success => success));
        await using var verify = fixture.CreateDbContext();
        Assert.Equal(1, await verify.InventoryTransportReceipts.CountAsync());
        Assert.Equal(70m, await verify.InventoryTransportReceipts.SumAsync(r => r.ReceivedQuantityMt + r.ShortageQuantityMt));
        Assert.Equal(70m, await verify.InventoryMovements.Where(m => m.Direction == MovementDirection.In).SumAsync(m => m.QuantityMt));
        Assert.Equal(30m, await new TransportQuantityService(verify).GetRemainingMtAsync(1));
        Assert.True((await verify.InventoryTransportLegs.SingleAsync()).Version > 1);
        Assert.Equal(1, await verify.InventoryTransportLegAllocations.CountAsync());
        Assert.Equal(1, await verify.InventoryMovements.Where(m => m.Direction == MovementDirection.In).Select(m => m.ContractId).SingleAsync());
    }

    [Fact]
    public async Task A_Direct_Service_Call_Cannot_Overconsume_The_Remaining_Load()
    {
        await SeedAsync();
        await using var db = fixture.CreateDbContext();
        var service = Service(db);
        var leg = (await service.LoadLegAsync(1, true))!;
        await service.ApplyAsync(Model(70m), leg, null);
        await Assert.ThrowsAsync<BusinessRuleException>(() => service.ApplyAsync(Model(70m), leg, null));
        await using var verify = fixture.CreateDbContext();
        Assert.Equal(1, await verify.InventoryTransportReceipts.CountAsync());
        Assert.Equal(30m, await new TransportQuantityService(verify).GetRemainingMtAsync(1));
    }

    [Fact]
    public async Task Even_One_Four_Decimal_Unit_Above_Remaining_Is_Rejected()
    {
        await SeedAsync();
        await using var db = fixture.CreateDbContext();
        var service = Service(db);
        var leg = (await service.LoadLegAsync(1, true))!;
        await Assert.ThrowsAsync<BusinessRuleException>(() => service.ApplyAsync(Model(100.0001m), leg, null));
        await using var verify = fixture.CreateDbContext();
        Assert.Empty(await verify.InventoryTransportReceipts.ToListAsync());
        Assert.Empty(await verify.InventoryMovements.ToListAsync());
    }

    [Fact]
    public async Task The_Last_Four_Decimal_Unit_Remains_Receivable_Until_Consumed()
    {
        await SeedAsync();
        await using var db = fixture.CreateDbContext();
        var service = Service(db);
        var leg = (await service.LoadLegAsync(1, true))!;
        await service.ApplyAsync(Model(99.9999m), leg, null);
        Assert.Equal(InventoryTransportLegStatus.Loaded, leg.Status);
        Assert.Equal(0.0001m, await new TransportQuantityService(db).GetRemainingMtAsync(1));
        await service.ApplyAsync(Model(0.0001m), leg, null);
        await using var verify = fixture.CreateDbContext();
        Assert.Equal(2, await verify.InventoryTransportReceipts.CountAsync());
        Assert.Equal(100m, await verify.InventoryTransportReceipts.SumAsync(r => r.ReceivedQuantityMt));
        Assert.Equal(InventoryTransportLegStatus.Received, (await verify.InventoryTransportLegs.SingleAsync()).Status);
    }

    [Fact]
    public async Task Explicit_Weighbridge_Surplus_Preserves_Source_Quantity()
    {
        await SeedAsync();
        await using var db = fixture.CreateDbContext();
        var service = Service(db);
        var leg = (await service.LoadLegAsync(1, true))!;
        var model = Model(101m);
        model.ShortageQuantityMt = -1m;
        await service.ApplyAsync(model, leg, null);
        await using var verify = fixture.CreateDbContext();
        Assert.Equal(101m, await verify.InventoryMovements.Where(m => m.Direction == MovementDirection.In).SumAsync(m => m.QuantityMt));
        Assert.Equal(0m, await new TransportQuantityService(verify).GetRemainingMtAsync(1));
        Assert.Equal(-1m, await verify.LossEvents.SumAsync(e => e.DifferenceQuantityMt));
    }

    [Fact]
    public async Task A_Stale_Browser_Snapshot_Is_Rejected_Without_New_Database_Effects()
    {
        await SeedAsync();
        await using var db = fixture.CreateDbContext();
        var service = Service(db);
        var leg = (await service.LoadLegAsync(1, true))!;
        await service.ApplyAsync(Model(70m), leg, null);
        var stale = Model(20m);
        stale.ExpectedRemainingMt = 100m;
        var error = await Assert.ThrowsAsync<BusinessRuleException>(() => service.ApplyAsync(stale, leg, null));
        Assert.Equal("TRANSPORT_RECEIPT_STALE", error.Code);
        await using var verify = fixture.CreateDbContext();
        Assert.Equal(1, await verify.InventoryTransportReceipts.CountAsync());
        Assert.Equal(30m, await new TransportQuantityService(verify).GetRemainingMtAsync(1));
    }

    private async Task SeedAsync()
    {
        Assert.True(fixture.Available, fixture.UnavailableReason);
        await fixture.TruncateAsync();
        await using var seed = fixture.CreateDbContext();
        await BulkFromLoadingPerformanceTests.SeedAsync(seed, 1);
        await BulkFromLoadingPerformanceTests.BuildWorkflow(seed).StartManyFromLoadingAsync(new()
        {
            TransportDate = new DateTime(2026, 9, 5),
            Rows = [new() { LoadingRegisterId = 1, QuantityMt = 100m, TransportType = LoadingTransportType.Truck, TruckId = 1 }]
        });
    }

    private static InventoryTransportReceiptService Service(ApplicationDbContext db)
        => new(db, new CurrencyConversionService(new PricingService(db)));

    private static InventoryTransportReceiptCreateViewModel Model(decimal quantity)
        => new() { InventoryTransportLegId = 1, ReceiptDate = new DateTime(2026, 9, 6),
            ReceivedQuantityMt = quantity, DestinationTerminalId = 1, ReceiptDestination = InventoryTransportReceiptDestination.ToInventory };

    private static async Task<bool> Apply(InventoryTransportReceiptService service, InventoryTransportReceiptCreateViewModel model, InventoryTransportLeg leg)
    {
        try { await service.ApplyAsync(model, leg, null); return true; }
        catch (BusinessRuleException) { return false; }
    }
}
