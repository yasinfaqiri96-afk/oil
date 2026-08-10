using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services;
using Xunit;

namespace PTGOilSystem.Web.Tests;

// اتحاد نگهداشت مقدار برای هر حمل:
// Loaded = Sold + ReceivedToInventory + TransferredToVehicle + Shortage + Remaining
// پیش از این، همین فرمول در پنج نقطه کپی شده بود. این تست‌ها موتور مرکزی را قفل می‌کنند.
public class TransportQuantityConservationTests
{
    [Fact]
    public async Task Remaining_Is_Loaded_Minus_Received_And_Shortage()
    {
        await using var db = BuildDb();
        await SeedLegAsync(db, loadedMt: 300m);
        await AddReceiptAsync(db, InventoryTransportReceiptDestination.ToInventory, receivedMt: 100m, shortageMt: 5m);

        Assert.Equal(195m, await BuildService(db).GetRemainingMtAsync(1));
    }

    [Fact]
    public async Task Cancelled_Receipts_Release_Their_Quantity_Back()
    {
        await using var db = BuildDb();
        await SeedLegAsync(db, loadedMt: 300m);
        await AddReceiptAsync(db, InventoryTransportReceiptDestination.ToInventory, receivedMt: 100m, shortageMt: 0m);
        await AddReceiptAsync(db, InventoryTransportReceiptDestination.DirectSale, receivedMt: 50m, shortageMt: 0m, cancelled: true);

        Assert.Equal(200m, await BuildService(db).GetRemainingMtAsync(1));
    }

    [Fact]
    public async Task Every_Outcome_Is_Counted_Once_And_The_Identity_Balances()
    {
        await using var db = BuildDb();
        await SeedLegAsync(db, loadedMt: 300m);
        await AddReceiptAsync(db, InventoryTransportReceiptDestination.ToInventory, receivedMt: 100m, shortageMt: 0m);
        await AddReceiptAsync(db, InventoryTransportReceiptDestination.DirectDispatch, receivedMt: 120m, shortageMt: 0m);
        await AddReceiptAsync(db, InventoryTransportReceiptDestination.DirectSale, receivedMt: 50m, shortageMt: 10m);

        var quantities = await BuildService(db).GetQuantitiesAsync(1);

        Assert.Equal(300m, quantities.LoadedMt);
        Assert.Equal(100m, quantities.ReceivedToInventoryMt);
        Assert.Equal(120m, quantities.TransferredToVehicleMt);
        Assert.Equal(50m, quantities.SoldMt);
        Assert.Equal(10m, quantities.ShortageMt);
        Assert.Equal(20m, quantities.RemainingMt);
        Assert.True(quantities.IsBalanced);
    }

    // تخلیهٔ جزئی نباید مقدار را دوبار مصرف کند.
    [Fact]
    public async Task Partial_Outcomes_Never_Double_Consume()
    {
        await using var db = BuildDb();
        await SeedLegAsync(db, loadedMt: 300m);
        await AddReceiptAsync(db, InventoryTransportReceiptDestination.ToInventory, receivedMt: 100m, shortageMt: 0m);
        await AddReceiptAsync(db, InventoryTransportReceiptDestination.DirectDispatch, receivedMt: 120m, shortageMt: 0m);
        await AddReceiptAsync(db, InventoryTransportReceiptDestination.DirectDispatch, receivedMt: 50m, shortageMt: 0m);

        var quantities = await BuildService(db).GetQuantitiesAsync(1);

        Assert.Equal(170m, quantities.TransferredToVehicleMt);
        Assert.Equal(30m, quantities.RemainingMt);
        Assert.True(quantities.IsBalanced);
    }

    [Fact]
    public async Task Batch_Lookup_Matches_The_Single_Leg_Lookup()
    {
        await using var db = BuildDb();
        await SeedLegAsync(db, loadedMt: 300m);
        await AddReceiptAsync(db, InventoryTransportReceiptDestination.ToInventory, receivedMt: 100m, shortageMt: 5m);
        await SeedLegAsync(db, loadedMt: 80m, legId: 2);

        var batch = await BuildService(db).GetRemainingMtAsync(new[] { 1, 2 });

        Assert.Equal(await BuildService(db).GetRemainingMtAsync(1), batch[1]);
        Assert.Equal(80m, batch[2]);
    }

    [Fact]
    public async Task An_Unknown_Leg_Reports_Nothing_Remaining()
    {
        await using var db = BuildDb();

        Assert.Equal(0m, await BuildService(db).GetRemainingMtAsync(999));
        Assert.Empty(await BuildService(db).GetRemainingMtAsync(Array.Empty<int>()));
    }

    private static TransportQuantityService BuildService(ApplicationDbContext db) => new(db);

    private static ApplicationDbContext BuildDb()
        => new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static async Task SeedLegAsync(ApplicationDbContext db, decimal loadedMt, int legId = 1)
    {
        if (legId == 1)
        {
            db.Products.Add(new Product { Id = 1, Code = "GO", Name = "Gas Oil", IsActive = true });
            db.Terminals.Add(new Terminal { Id = 1, Code = "T1", Name = "Terminal 1", IsActive = true });
            db.Contracts.Add(new Contract
            {
                Id = 1,
                ContractNumber = "CTR-1",
                ContractType = ContractType.Purchase,
                ProductId = 1,
                ContractDate = new DateTime(2026, 4, 1),
                QuantityMt = 1000m
            });
        }

        db.InventoryTransportLegs.Add(new InventoryTransportLeg
        {
            Id = legId,
            SourcePurchaseContractId = 1,
            ProductId = 1,
            SourceTerminalId = 1,
            TransportType = LoadingTransportType.Wagon,
            LoadedDate = new DateTime(2026, 5, 1),
            QuantityMt = loadedMt,
            Status = InventoryTransportLegStatus.Loaded
        });
        await db.SaveChangesAsync();
    }

    private static async Task AddReceiptAsync(
        ApplicationDbContext db,
        InventoryTransportReceiptDestination destination,
        decimal receivedMt,
        decimal shortageMt,
        bool cancelled = false)
    {
        db.InventoryTransportReceipts.Add(new InventoryTransportReceipt
        {
            InventoryTransportLegId = 1,
            ReceiptDate = new DateTime(2026, 5, 3),
            ReceivedQuantityMt = receivedMt,
            ShortageQuantityMt = shortageMt,
            ReceiptDestination = destination,
            IsCancelled = cancelled
        });
        await db.SaveChangesAsync();
    }
}
