using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Exceptions;
using Xunit;

namespace PTGOilSystem.Web.Tests;

// انتقال گروهی دیگر موتور تجاری خودش را ندارد: هر موترِ مقصد یک اجرای
// ContinueToVehicle است. Bulk UI و تقسیم چند واگن بین چند موتر حفظ شده.
public class GroupTransferSharedEngineTests
{
    [Fact]
    public void The_Group_Transfer_Action_Delegates_To_The_Shared_Engine()
    {
        var source = ReadRepoFile("src/PTGOilSystem.Web/Controllers/InventoryTransportLegsController.cs");

        Assert.Contains("await _transportChain.ContinueToVehicleAsync(transferCommand)", source);
        // منطق موازیِ ساختِ رسیدِ انتقال دیگر در کنترلر نیست.
        Assert.DoesNotContain("SkipDirectDispatchRecord = !isPrimary", source);
        Assert.DoesNotContain("AllowDirectDispatchBeyondReceipt = isPrimary", source);
        // الگوریتم تقسیم Bulk می‌ماند؛ فقط منطق تجاری رفته است.
        Assert.Contains("BuildTransferChunks", source);
    }

    // یک واگن → یک موتر: یک مرحلهٔ فرزند، یک رکورد سازگاری، صفر حرکت موجودی.
    [Fact]
    public async Task A_Single_Wagon_To_Truck_Transfer_Creates_One_Child_Leg_And_One_Dispatch()
    {
        await using var db = TransportChainScenario.BuildDb();
        await TransportChainScenario.SeedAsync(db);
        await TransportChainScenario.SeedLegAsync(db, 1, LoadingTransportType.Wagon, 100m, contractId: 1);

        var result = await TransportChainScenario.Continue(db, 1, LoadingTransportType.Truck, 100m);

        Assert.Equal(LoadingTransportType.Truck, result.ChildLeg.TransportType);
        Assert.Equal(100m, result.ChildLeg.QuantityMt);
        Assert.Single(await db.TruckDispatches.ToListAsync());
        Assert.Empty(await db.InventoryMovements.ToListAsync());
        Assert.Equal(0m, await TransportChainScenario.RemainingAsync(db, 1));
    }

    // Merge داخل انتقال گروهی: دو واگن → یک موتر.
    [Fact]
    public async Task Two_Wagons_Into_One_Truck_Produce_One_Load_With_Two_Traceable_Sources()
    {
        await using var db = TransportChainScenario.BuildDb();
        await TransportChainScenario.SeedAsync(db);
        await TransportChainScenario.SeedLegAsync(db, 1, LoadingTransportType.Wagon, 60m, contractId: 1);
        await TransportChainScenario.SeedLegAsync(db, 2, LoadingTransportType.Wagon, 40m, contractId: 2);

        var merged = await TransportChainScenario.ContinueFrom(
            db,
            [new ContinueToVehicleSource(1, 60m), new ContinueToVehicleSource(2, 40m)],
            LoadingTransportType.Truck);

        Assert.Equal(100m, merged.ChildLeg.QuantityMt);
        Assert.Equal(2, merged.ChildAllocations.Count);
        Assert.Equal(2, merged.SourceReceipts.Count);
        Assert.Empty(await db.InventoryMovements.ToListAsync());

        // یک دیسپچ با وزن کاملِ موتر — همان رفتاری که انتقال گروهی قبلاً داشت.
        var dispatch = Assert.Single(await db.TruckDispatches.ToListAsync());
        Assert.Equal(100m, dispatch.LoadedQuantityMt);

        // رسیدهای «همراه» با نشانهٔ پیوند به رسید اصلی ثبت می‌شوند تا لغو گروهی نشکند.
        Assert.StartsWith(TransportChainService.CompanionReceiptNotePrefix, merged.SourceReceipts[1].Notes);
        Assert.Contains($"{TransportChainService.CompanionReceiptNotePrefix}{merged.SourceReceipts[0].Id}]", merged.SourceReceipts[1].Notes);
    }

    // Split داخل انتقال گروهی: یک واگن → دو موتر، با باقیمانده.
    [Fact]
    public async Task One_Wagon_Split_Across_Two_Trucks_Keeps_The_Remainder()
    {
        await using var db = TransportChainScenario.BuildDb();
        await TransportChainScenario.SeedAsync(db);
        await TransportChainScenario.SeedLegAsync(db, 1, LoadingTransportType.Wagon, 200m, contractId: 1);

        var first = await TransportChainScenario.Continue(db, 1, LoadingTransportType.Truck, 80m);
        var second = await TransportChainScenario.Continue(db, 1, LoadingTransportType.Truck, 70m);

        Assert.Equal(80m, first.ChildLeg.QuantityMt);
        Assert.Equal(70m, second.ChildLeg.QuantityMt);
        Assert.Equal(50m, await TransportChainScenario.RemainingAsync(db, 1));
        Assert.Equal(2, await db.TruckDispatches.CountAsync());
        Assert.Empty(await db.InventoryMovements.ToListAsync());
    }

    [Fact]
    public async Task A_Multi_Contract_Wagon_Passes_Both_Contracts_To_The_Truck()
    {
        await using var db = TransportChainScenario.BuildDb();
        await TransportChainScenario.SeedAsync(db);
        await TransportChainScenario.SeedLegAsync(db, 1, LoadingTransportType.Wagon, 100m, contractId: 1);
        await TransportChainScenario.AddSourceAllocationAsync(db, legId: 1, contractId: 1, quantityMt: 40m);
        await TransportChainScenario.AddSourceAllocationAsync(db, legId: 1, contractId: 2, quantityMt: 60m);

        var result = await TransportChainScenario.Continue(db, 1, LoadingTransportType.Truck, 50m);

        Assert.Equal(2, result.ChildAllocations.Count);
        Assert.Equal(30m, result.ChildAllocations.Single(a => a.SourcePurchaseContractId == 2).QuantityMt);
        Assert.Equal(20m, result.ChildAllocations.Single(a => a.SourcePurchaseContractId == 1).QuantityMt);
        Assert.Equal(50m, result.ChildAllocations.Sum(a => a.QuantityMt));
    }

    // ارسال دوباره نباید مقدار را دو بار مصرف کند.
    [Fact]
    public async Task Re_Submitting_The_Same_Transfer_Is_Rejected_By_The_Remaining_Guard()
    {
        await using var db = TransportChainScenario.BuildDb();
        await TransportChainScenario.SeedAsync(db);
        await TransportChainScenario.SeedLegAsync(db, 1, LoadingTransportType.Wagon, 100m, contractId: 1);

        await TransportChainScenario.Continue(db, 1, LoadingTransportType.Truck, 100m);
        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => TransportChainScenario.Continue(db, 1, LoadingTransportType.Truck, 100m));

        Assert.Equal("TRANSPORT_CHAIN_QTY_EXCEEDS_REMAINING", error.Code);
        Assert.Single(await db.TruckDispatches.ToListAsync());
        Assert.Equal(0m, await TransportChainScenario.RemainingAsync(db, 1));
    }

    // یک سهم نامعتبر نباید رکورد نیمه‌ساخته بگذارد: اعتبارسنجی پیش از ساختِ هر چیزی تمام می‌شود.
    [Fact]
    public async Task An_Invalid_Source_Aborts_The_Whole_Transfer_Before_Anything_Is_Written()
    {
        await using var db = TransportChainScenario.BuildDb();
        await TransportChainScenario.SeedAsync(db);
        await TransportChainScenario.SeedLegAsync(db, 1, LoadingTransportType.Wagon, 60m, contractId: 1);
        await TransportChainScenario.SeedLegAsync(db, 2, LoadingTransportType.Wagon, 40m, contractId: 2);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => TransportChainScenario.ContinueFrom(
                db,
                [new ContinueToVehicleSource(1, 60m), new ContinueToVehicleSource(2, 999m)],
                LoadingTransportType.Truck));

        Assert.Equal("TRANSPORT_CHAIN_QTY_EXCEEDS_REMAINING", error.Code);
        Assert.Empty(await db.TruckDispatches.ToListAsync());
        Assert.Empty(await db.InventoryTransportReceipts.ToListAsync());
        Assert.Equal(2, await db.InventoryTransportLegs.CountAsync());
        Assert.Equal(60m, await TransportChainScenario.RemainingAsync(db, 1));
    }

    // مسیر و View فعلی نباید خراب شوند.
    [Fact]
    public void The_Legacy_Group_Transfer_Route_And_View_Still_Exist()
    {
        var controller = ReadRepoFile("src/PTGOilSystem.Web/Controllers/InventoryTransportLegsController.cs");

        Assert.Contains("public async Task<IActionResult> GroupTransfer(InventoryTransportGroupTransferViewModel model)", controller);
        Assert.Contains("public async Task<IActionResult> CancelGroupTransfer(", controller);
        Assert.True(File.Exists(RepoPath("src/PTGOilSystem.Web/Views/InventoryTransportLegs/GroupTransfer.cshtml")));
    }

    // لغو هم باید از موتور مشترک عبور کند، و نگهبان پیش از هر تغییری اجرا شود.
    [Fact]
    public void Cancelling_A_Group_Transfer_Goes_Through_The_Shared_Engine_First()
    {
        var source = ReadRepoFile("src/PTGOilSystem.Web/Controllers/InventoryTransportLegsController.cs");

        var cancelIndex = source.IndexOf(
            "public async Task<IActionResult> CancelGroupTransfer(",
            StringComparison.Ordinal);
        Assert.True(cancelIndex > 0);

        var cancelBody = source[cancelIndex..];
        var chainCallIndex = cancelBody.IndexOf("_transportChain.CancelVehicleTransferAsync(", StringComparison.Ordinal);
        var receiptMutationIndex = cancelBody.IndexOf("receipt.IsCancelled = true;", StringComparison.Ordinal);

        Assert.True(chainCallIndex > 0, "لغو گروهی باید موتور زنجیره را صدا بزند.");
        Assert.True(
            chainCallIndex < receiptMutationIndex,
            "نگهبان زنجیره باید پیش از تغییر رسید اجرا شود تا لغو ناامن رکورد نیمه‌کاره نگذارد.");
    }

    private static string ReadRepoFile(string relativePath) => File.ReadAllText(RepoPath(relativePath));

    private static string RepoPath(string relativePath)
        => Path.Combine(
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..")),
            relativePath);
}
