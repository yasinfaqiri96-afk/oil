using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Exceptions;
using Xunit;

namespace PTGOilSystem.Web.Tests;

// وسیله → وسیله مرز مخزن را رد نمی‌کند، پس هرگز نباید حرکت موجودی بسازد.
// یک موتور برای هر نُه ترکیب موتر/واگن/کشتی.
public class TransportChainTests
{
    [Theory]
    [InlineData(LoadingTransportType.Truck, LoadingTransportType.Truck)]
    [InlineData(LoadingTransportType.Truck, LoadingTransportType.Wagon)]
    [InlineData(LoadingTransportType.Truck, LoadingTransportType.Vessel)]
    [InlineData(LoadingTransportType.Wagon, LoadingTransportType.Truck)]
    [InlineData(LoadingTransportType.Wagon, LoadingTransportType.Wagon)]
    [InlineData(LoadingTransportType.Wagon, LoadingTransportType.Vessel)]
    [InlineData(LoadingTransportType.Vessel, LoadingTransportType.Truck)]
    [InlineData(LoadingTransportType.Vessel, LoadingTransportType.Wagon)]
    [InlineData(LoadingTransportType.Vessel, LoadingTransportType.Vessel)]
    public async Task Every_Vehicle_Combination_Goes_Through_The_Same_Engine(
        LoadingTransportType from,
        LoadingTransportType to)
    {
        await using var db = TransportChainScenario.BuildDb();
        await TransportChainScenario.SeedAsync(db);
        await TransportChainScenario.SeedLegAsync(db, legId: 1, transportType: from, quantityMt: 200m, contractId: 1);

        var result = await TransportChainScenario.Continue(db, sourceLegId: 1, to, quantityMt: 80m);

        Assert.Equal(to, result.ChildLeg.TransportType);
        Assert.Equal(80m, result.ChildLeg.QuantityMt);
        Assert.Equal(120m, await TransportChainScenario.RemainingAsync(db, 1));
        Assert.Empty(await db.InventoryMovements.ToListAsync());
    }

    [Fact]
    public async Task The_Child_Leg_Keeps_The_Transport_Identity_Of_Its_Parent()
    {
        await using var db = TransportChainScenario.BuildDb();
        await TransportChainScenario.SeedAsync(db);
        await TransportChainScenario.SeedLegAsync(db, 1, LoadingTransportType.Wagon, 200m, contractId: 1, groupKey: "GRP-7");

        var result = await TransportChainScenario.Continue(db, 1, LoadingTransportType.Truck, 50m);

        Assert.Equal("GRP-7", result.ChildLeg.TransportGroupKey);
        var allocation = Assert.Single(result.ChildAllocations);
        Assert.Equal(1, allocation.SourceTransportLegId);
        Assert.Null(allocation.SourceInventoryMovementId);
        Assert.Equal(result.SourceReceipt.Id, allocation.SourceTransportReceiptId);
    }

    // Split: یک والد بین چند فرزند تقسیم می‌شود و باقیماندهٔ خودش درست می‌ماند.
    [Fact]
    public async Task Split_Gives_Each_Child_Its_Own_Share_And_Leaves_The_Parent_Remainder()
    {
        await using var db = TransportChainScenario.BuildDb();
        await TransportChainScenario.SeedAsync(db);
        await TransportChainScenario.SeedLegAsync(db, 1, LoadingTransportType.Wagon, 200m, contractId: 1);

        var first = await TransportChainScenario.Continue(db, 1, LoadingTransportType.Truck, 80m);
        var second = await TransportChainScenario.Continue(db, 1, LoadingTransportType.Truck, 70m);

        Assert.Equal(80m, first.ChildLeg.QuantityMt);
        Assert.Equal(70m, second.ChildLeg.QuantityMt);
        Assert.Equal(50m, await TransportChainScenario.RemainingAsync(db, 1));
        Assert.Empty(await db.InventoryMovements.ToListAsync());

        var quantities = await new TransportQuantityService(db).GetQuantitiesAsync(1);
        Assert.Equal(150m, quantities.TransferredToVehicleMt);
        Assert.True(quantities.IsBalanced);
    }

    // Merge: یک فرزند از دو والد تغذیه می‌شود — دقیقاً چیزی که یک ستون Parent تکی نمی‌توانست.
    [Fact]
    public async Task Merge_Lets_One_Child_Carry_Shares_From_Two_Parents()
    {
        await using var db = TransportChainScenario.BuildDb();
        await TransportChainScenario.SeedAsync(db);
        await TransportChainScenario.SeedLegAsync(db, 1, LoadingTransportType.Wagon, 60m, contractId: 1);
        await TransportChainScenario.SeedLegAsync(db, 2, LoadingTransportType.Wagon, 40m, contractId: 2);

        // هر دو واگن در یک فرمان روی همان موتر خالی می‌شوند: یک بارِ ۱۰۰ تنی، نه دو بارِ جدا.
        var merged = await TransportChainScenario.ContinueFrom(
            db,
            [new ContinueToVehicleSource(1, 60m), new ContinueToVehicleSource(2, 40m)],
            LoadingTransportType.Truck);

        Assert.Equal(100m, merged.ChildLeg.QuantityMt);
        Assert.Single(await db.InventoryTransportLegs.Where(l => l.Id > 2).ToListAsync());
        Assert.Equal(0m, await TransportChainScenario.RemainingAsync(db, 1));
        Assert.Equal(0m, await TransportChainScenario.RemainingAsync(db, 2));
        Assert.Empty(await db.InventoryMovements.ToListAsync());

        // هر سهم والد خودش را نگه داشته و قرارداد منبع هر کدام جدا قابل ردیابی است.
        var allocations = merged.ChildAllocations;
        Assert.Equal(2, allocations.Count);
        Assert.Equal(60m, allocations.Single(a => a.SourceTransportLegId == 1).QuantityMt);
        Assert.Equal(1, allocations.Single(a => a.SourceTransportLegId == 1).SourcePurchaseContractId);
        Assert.Equal(40m, allocations.Single(a => a.SourceTransportLegId == 2).QuantityMt);
        Assert.Equal(2, allocations.Single(a => a.SourceTransportLegId == 2).SourcePurchaseContractId);

        // رکورد سازگاری موتر یکی است و وزن کاملِ موتر را دارد.
        var dispatch = Assert.Single(await db.TruckDispatches.ToListAsync());
        Assert.Equal(100m, dispatch.LoadedQuantityMt);
    }

    // سهم چندقراردادی والد باید با همان نسبت به فرزند برسد.
    [Fact]
    public async Task Multi_Contract_Parent_Passes_Its_Contract_Ratio_To_The_Child()
    {
        await using var db = TransportChainScenario.BuildDb();
        await TransportChainScenario.SeedAsync(db);
        await TransportChainScenario.SeedLegAsync(db, 1, LoadingTransportType.Wagon, 300m, contractId: 1);
        await TransportChainScenario.AddSourceAllocationAsync(db, legId: 1, contractId: 1, quantityMt: 100m);
        await TransportChainScenario.AddSourceAllocationAsync(db, legId: 1, contractId: 2, quantityMt: 200m);

        var result = await TransportChainScenario.Continue(db, 1, LoadingTransportType.Truck, 120m);

        Assert.Equal(2, result.ChildAllocations.Count);
        Assert.Equal(80m, result.ChildAllocations.Single(a => a.SourcePurchaseContractId == 2).QuantityMt);
        Assert.Equal(40m, result.ChildAllocations.Single(a => a.SourcePurchaseContractId == 1).QuantityMt);
        Assert.Equal(120m, result.ChildAllocations.Sum(a => a.QuantityMt));
    }

    [Fact]
    public async Task A_Transfer_Larger_Than_The_Remaining_Load_Is_Rejected()
    {
        await using var db = TransportChainScenario.BuildDb();
        await TransportChainScenario.SeedAsync(db);
        await TransportChainScenario.SeedLegAsync(db, 1, LoadingTransportType.Wagon, 100m, contractId: 1);
        await TransportChainScenario.Continue(db, 1, LoadingTransportType.Truck, 90m);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => TransportChainScenario.Continue(db, 1, LoadingTransportType.Truck, 20m));

        Assert.Equal("TRANSPORT_CHAIN_QTY_EXCEEDS_REMAINING", error.Code);
        Assert.Equal(10m, await TransportChainScenario.RemainingAsync(db, 1));
    }

    // TruckDispatch فقط برای مقصد موتر ساخته می‌شود و برای واگن/کشتی معنایی ندارد.
    [Fact]
    public async Task Only_A_Truck_Destination_Creates_The_Legacy_Dispatch_Record()
    {
        await using var db = TransportChainScenario.BuildDb();
        await TransportChainScenario.SeedAsync(db);
        await TransportChainScenario.SeedLegAsync(db, 1, LoadingTransportType.Wagon, 200m, contractId: 1);

        await TransportChainScenario.Continue(db, 1, LoadingTransportType.Truck, 50m);
        Assert.Equal(1, await db.TruckDispatches.CountAsync());

        await TransportChainScenario.Continue(db, 1, LoadingTransportType.Vessel, 50m);
        await TransportChainScenario.Continue(db, 1, LoadingTransportType.Wagon, 50m);
        Assert.Equal(1, await db.TruckDispatches.CountAsync());
    }
}
