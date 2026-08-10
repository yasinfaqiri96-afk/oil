using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Exceptions;
using Xunit;

namespace PTGOilSystem.Web.Tests;

// لغو یک انتقال وسیله→وسیله باید مرحلهٔ فرزند را هم ببندد، وگرنه زنجیره یتیم می‌ماند.
// چون این انتقال هیچ حرکت موجودی نساخته بود، لغوش هم نباید بسازد.
public class TransportChainCancelTests
{
    [Fact]
    public async Task Cancelling_A_Transfer_Cancels_The_Child_Leg_And_Restores_The_Parent()
    {
        await using var db = TransportChainScenario.BuildDb();
        await TransportChainScenario.SeedAsync(db);
        await TransportChainScenario.SeedLegAsync(db, 1, LoadingTransportType.Wagon, 100m, contractId: 1);

        var transfer = await TransportChainScenario.Continue(db, 1, LoadingTransportType.Truck, 60m);
        Assert.Equal(40m, await TransportChainScenario.RemainingAsync(db, 1));

        await CancelAsync(db, transfer);

        Assert.Equal(InventoryTransportLegStatus.Cancelled, await LegStatusAsync(db, transfer.ChildLeg.Id));
        Assert.Equal(100m, await TransportChainScenario.RemainingAsync(db, 1));
        Assert.Empty(await db.InventoryMovements.ToListAsync());
    }

    // همان مقدار باید بعد از لغو دوباره قابل انتقال باشد.
    [Fact]
    public async Task The_Released_Quantity_Can_Be_Transferred_Again()
    {
        await using var db = TransportChainScenario.BuildDb();
        await TransportChainScenario.SeedAsync(db);
        await TransportChainScenario.SeedLegAsync(db, 1, LoadingTransportType.Wagon, 100m, contractId: 1);

        var first = await TransportChainScenario.Continue(db, 1, LoadingTransportType.Truck, 60m);
        await CancelAsync(db, first);

        var second = await TransportChainScenario.Continue(db, 1, LoadingTransportType.Truck, 60m);

        Assert.Equal(60m, second.ChildLeg.QuantityMt);
        Assert.Equal(40m, await TransportChainScenario.RemainingAsync(db, 1));
        Assert.Empty(await db.InventoryMovements.ToListAsync());
    }

    // Merge: لغو باید باقیماندهٔ هر دو والد را برگرداند.
    [Fact]
    public async Task Cancelling_A_Merged_Transfer_Restores_Both_Parents()
    {
        await using var db = TransportChainScenario.BuildDb();
        await TransportChainScenario.SeedAsync(db);
        await TransportChainScenario.SeedLegAsync(db, 1, LoadingTransportType.Wagon, 60m, contractId: 1);
        await TransportChainScenario.SeedLegAsync(db, 2, LoadingTransportType.Wagon, 40m, contractId: 2);

        var merged = await TransportChainScenario.ContinueFrom(
            db,
            [new ContinueToVehicleSource(1, 60m), new ContinueToVehicleSource(2, 40m)],
            LoadingTransportType.Truck);

        await CancelAsync(db, merged);

        Assert.Equal(InventoryTransportLegStatus.Cancelled, await LegStatusAsync(db, merged.ChildLeg.Id));
        Assert.Equal(60m, await TransportChainScenario.RemainingAsync(db, 1));
        Assert.Equal(40m, await TransportChainScenario.RemainingAsync(db, 2));
        Assert.Empty(await db.InventoryMovements.ToListAsync());
    }

    // Split: لغو یک فرزند نباید فرزند دیگر را خراب کند.
    [Fact]
    public async Task Cancelling_One_Split_Branch_Leaves_The_Other_Untouched()
    {
        await using var db = TransportChainScenario.BuildDb();
        await TransportChainScenario.SeedAsync(db);
        await TransportChainScenario.SeedLegAsync(db, 1, LoadingTransportType.Wagon, 200m, contractId: 1);

        var toT1 = await TransportChainScenario.Continue(db, 1, LoadingTransportType.Truck, 80m);
        var toT2 = await TransportChainScenario.Continue(db, 1, LoadingTransportType.Truck, 70m);
        Assert.Equal(50m, await TransportChainScenario.RemainingAsync(db, 1));

        await CancelAsync(db, toT1);

        Assert.Equal(130m, await TransportChainScenario.RemainingAsync(db, 1));
        Assert.Equal(InventoryTransportLegStatus.Cancelled, await LegStatusAsync(db, toT1.ChildLeg.Id));
        Assert.Equal(InventoryTransportLegStatus.Loaded, await LegStatusAsync(db, toT2.ChildLeg.Id));
        Assert.Empty(await db.InventoryMovements.ToListAsync());
    }

    [Fact]
    public async Task Multi_Contract_Shares_Are_Released_And_Stay_Traceable()
    {
        await using var db = TransportChainScenario.BuildDb();
        await TransportChainScenario.SeedAsync(db);
        await TransportChainScenario.SeedLegAsync(db, 1, LoadingTransportType.Wagon, 100m, contractId: 1);
        await TransportChainScenario.AddSourceAllocationAsync(db, legId: 1, contractId: 1, quantityMt: 40m);
        await TransportChainScenario.AddSourceAllocationAsync(db, legId: 1, contractId: 2, quantityMt: 60m);

        var transfer = await TransportChainScenario.Continue(db, 1, LoadingTransportType.Truck, 50m);
        await CancelAsync(db, transfer);

        Assert.Equal(100m, await TransportChainScenario.RemainingAsync(db, 1));

        // سهم‌های فرزند حذف فیزیکی نمی‌شوند؛ تاریخچه برای Audit می‌ماند.
        var childAllocations = await db.InventoryTransportLegAllocations
            .AsNoTracking()
            .Where(a => a.InventoryTransportLegId == transfer.ChildLeg.Id)
            .ToListAsync();
        Assert.Equal(2, childAllocations.Count);
        Assert.Equal(50m, childAllocations.Sum(a => a.QuantityMt));

        // و همان ۵۰ تن دوباره قابل انتقال است.
        var again = await TransportChainScenario.Continue(db, 1, LoadingTransportType.Truck, 50m);
        Assert.Equal(2, again.ChildAllocations.Count);
    }

    [Fact]
    public async Task Cancelling_Twice_Changes_Nothing_The_Second_Time()
    {
        await using var db = TransportChainScenario.BuildDb();
        await TransportChainScenario.SeedAsync(db);
        await TransportChainScenario.SeedLegAsync(db, 1, LoadingTransportType.Wagon, 100m, contractId: 1);

        var transfer = await TransportChainScenario.Continue(db, 1, LoadingTransportType.Truck, 60m);

        var firstCancelled = await CancelAsync(db, transfer);
        var secondCancelled = await CancelAsync(db, transfer);

        Assert.Single(firstCancelled);
        Assert.Empty(secondCancelled);
        Assert.Equal(InventoryTransportLegStatus.Cancelled, await LegStatusAsync(db, transfer.ChildLeg.Id));
        Assert.Empty(await db.InventoryMovements.ToListAsync());
    }

    // نگهبان پایین‌دست: واگن → موتر → کشتی. لغو مرحلهٔ اول نباید کشتی را یتیم بگذارد.
    [Fact]
    public async Task A_Parent_Transfer_Cannot_Be_Cancelled_While_Its_Child_Carries_The_Load_Onward()
    {
        await using var db = TransportChainScenario.BuildDb();
        await TransportChainScenario.SeedAsync(db);
        await TransportChainScenario.SeedLegAsync(db, 1, LoadingTransportType.Wagon, 100m, contractId: 1);

        var toTruck = await TransportChainScenario.Continue(db, 1, LoadingTransportType.Truck, 100m);
        await TransportChainScenario.Continue(db, toTruck.ChildLeg.Id, LoadingTransportType.Vessel, 40m);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(() => CancelAsync(db, toTruck));

        Assert.Equal("TRANSPORT_CHAIN_CHILD_HAS_DOWNSTREAM", error.Code);
        Assert.Equal(InventoryTransportLegStatus.Loaded, await LegStatusAsync(db, toTruck.ChildLeg.Id));
        Assert.Equal(0m, await TransportChainScenario.RemainingAsync(db, 1));
    }

    // بعد از لغو مرحلهٔ آخر، لغو مرحلهٔ قبلی ممکن می‌شود (معکوس از آخر به اول).
    [Fact]
    public async Task Reversing_From_The_End_Backwards_Unwinds_The_Whole_Chain()
    {
        await using var db = TransportChainScenario.BuildDb();
        await TransportChainScenario.SeedAsync(db);
        await TransportChainScenario.SeedLegAsync(db, 1, LoadingTransportType.Wagon, 100m, contractId: 1);

        var toTruck = await TransportChainScenario.Continue(db, 1, LoadingTransportType.Truck, 100m);
        var toVessel = await TransportChainScenario.Continue(db, toTruck.ChildLeg.Id, LoadingTransportType.Vessel, 40m);

        await CancelAsync(db, toVessel);
        await CancelAsync(db, toTruck);

        Assert.Equal(InventoryTransportLegStatus.Cancelled, await LegStatusAsync(db, toVessel.ChildLeg.Id));
        Assert.Equal(InventoryTransportLegStatus.Cancelled, await LegStatusAsync(db, toTruck.ChildLeg.Id));
        Assert.Equal(100m, await TransportChainScenario.RemainingAsync(db, 1));
        Assert.Empty(await db.InventoryMovements.ToListAsync());
    }

    // لغو انتقالی که پیش از مدل زنجیره ثبت شده (بدون فرزند) نباید خطا بدهد.
    [Fact]
    public async Task Cancelling_A_Pre_Chain_Transfer_Is_A_Safe_No_Op()
    {
        await using var db = TransportChainScenario.BuildDb();
        await TransportChainScenario.SeedAsync(db);

        var cancelled = await TransportChainScenario
            .BuildChainService(db)
            .CancelVehicleTransferAsync(new[] { 12345 });

        Assert.Empty(cancelled);
    }

    // لغو زنجیره، رسیدها را دست نمی‌زند؛ آن کارِ اکشن لغو است. اینجا فقط باید مطمئن باشیم
    // که مرحلهٔ فرزند بسته شده و هیچ حرکت موجودی ساخته نشده است.
    private static async Task<IReadOnlyList<InventoryTransportLeg>> CancelAsync(
        ApplicationDbContext db,
        ContinueToVehicleResult transfer)
    {
        var receiptIds = transfer.SourceReceipts.Select(r => r.Id).ToList();
        var cancelled = await TransportChainScenario
            .BuildChainService(db)
            .CancelVehicleTransferAsync(receiptIds);

        foreach (var receipt in await db.InventoryTransportReceipts
            .Where(r => receiptIds.Contains(r.Id) && !r.IsCancelled)
            .ToListAsync())
        {
            receipt.IsCancelled = true;
        }

        await db.SaveChangesAsync();
        return cancelled;
    }

    private static async Task<InventoryTransportLegStatus> LegStatusAsync(ApplicationDbContext db, int legId)
        => (await db.InventoryTransportLegs.AsNoTracking().SingleAsync(l => l.Id == legId)).Status;
}
