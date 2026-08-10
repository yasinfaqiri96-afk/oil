using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.InventoryTransport;
using PTGOilSystem.Web.Services;
using Xunit;

namespace PTGOilSystem.Web.Tests;

// سناریوی مرجع چندمرحله‌ای:
//
//   Tank A = 500
//   حمل #1: Tank A → Wagon W1 = 300
//     W1: 100 → Tank B   |   200 → Truck T1
//     T1: 120 → فروش      |    80 → Vessel V1
//     V1:  30 → Wagon W2  |    50 → Tank C
//   حمل #2: Tank B → Truck T2 = 20
//
// انتظار: فقط چهار جا حرکت موجودی ساخته شود (خروج ۳۰۰ از A، ورود ۱۰۰ به B،
// ورود ۵۰ به C، خروج ۲۰ از B). تعویض وسیله و فروشِ بارِ در راه هیچ حرکتی نسازند.
public class TransportGoldenScenarioTests
{
    [Fact]
    public async Task The_Multi_Leg_Chain_Moves_Tonnes_Without_Inventing_Inventory()
    {
        await using var db = TransportChainScenario.BuildDb();
        await TransportChainScenario.SeedAsync(db);

        // مخزن A پر می‌شود و حمل #۱ ۳۰۰ تن را روی واگن W1 می‌گذارد (تنها خروج موجودی).
        await SeedTankAsync(db, storageTankId: 1, terminalId: 1, quantityMt: 500m);
        await TransportChainScenario.SeedLegAsync(db, legId: 1, LoadingTransportType.Wagon, 300m, contractId: 1, groupKey: "TRANSPORT-1");
        await TransportChainScenario.AddSourceAllocationAsync(db, legId: 1, contractId: 1, quantityMt: 300m, seedPurchaseIn: false);

        // W1 → مخزن B (۱۰۰): تنها ورود موجودی این شاخه.
        await ReceiveToTankAsync(db, legId: 1, quantityMt: 100m, terminalId: 2, storageTankId: 2);

        // W1 → موتر T1 (۲۰۰): تعویض وسیله، بدون حرکت موجودی.
        var t1 = await TransportChainScenario.Continue(db, 1, LoadingTransportType.Truck, 200m);

        // T1 → فروش (۱۲۰): بار قبلاً از مخزن خارج شده، پس خروج دوم ساخته نمی‌شود.
        await SellFromVehicleAsync(db, legId: t1.ChildLeg.Id, quantityMt: 120m);

        // T1 → کشتی V1 (۸۰) → واگن W2 (۳۰) و مخزن C (۵۰).
        var v1 = await TransportChainScenario.Continue(db, t1.ChildLeg.Id, LoadingTransportType.Vessel, 80m);
        var w2 = await TransportChainScenario.Continue(db, v1.ChildLeg.Id, LoadingTransportType.Wagon, 30m);
        await ReceiveToTankAsync(db, legId: v1.ChildLeg.Id, quantityMt: 50m, terminalId: 3, storageTankId: 3);

        // حمل #۲ مستقل است: بار دوباره از مخزن خارج می‌شود، پس خروج تازه دارد.
        await TransportChainScenario.SeedLegAsync(db, legId: 99, LoadingTransportType.Truck, 20m, contractId: 1,
            groupKey: "TRANSPORT-2", sourceTerminalId: 2, sourceStorageTankId: 2);
        await TransportChainScenario.AddSourceAllocationAsync(db, legId: 99, contractId: 1, quantityMt: 20m, storageTankId: 2, terminalId: 2, seedPurchaseIn: false);

        // ---- موجودی ----
        Assert.Equal(200m, await TransportChainScenario.TankBalanceAsync(db, 1));  // 500 − 300
        Assert.Equal(80m, await TransportChainScenario.TankBalanceAsync(db, 2));   // +100 − 20
        Assert.Equal(50m, await TransportChainScenario.TankBalanceAsync(db, 3));   // +50

        // ---- حرکات: هیچ حرکتی برای تعویض وسیله یا فروشِ در راه ----
        var movements = await db.InventoryMovements.AsNoTracking().ToListAsync();
        Assert.Equal(300m, movements.Single(m => m.ReferenceDocument == "TRANSPORT-LEG:1:1").QuantityMt);
        Assert.Equal(20m, movements.Single(m => m.ReferenceDocument == "TRANSPORT-LEG:99:1").QuantityMt);
        Assert.Equal(2, movements.Count(m => m.Direction == MovementDirection.Out));
        Assert.Equal(150m, movements.Where(m => m.ReferenceDocument!.StartsWith("TRANSPORT-RECEIPT:")).Sum(m => m.QuantityMt));
        Assert.DoesNotContain(movements, m => m.ReferenceDocument!.StartsWith("SALE"));

        // ---- زنجیره: W1 → T1 → V1 → W2 با سهم منبع قابل ردیابی ----
        Assert.Equal(1, await ParentLegIdAsync(db, t1.ChildLeg.Id));
        Assert.Equal(t1.ChildLeg.Id, await ParentLegIdAsync(db, v1.ChildLeg.Id));
        Assert.Equal(v1.ChildLeg.Id, await ParentLegIdAsync(db, w2.ChildLeg.Id));
        Assert.Equal(1, (await ChainAllocationsAsync(db, w2.ChildLeg.Id)).Single().SourcePurchaseContractId);

        // ---- نگهداشت مقدار در هر مرحله ----
        var quantities = new TransportQuantityService(db);
        var w1 = await quantities.GetQuantitiesAsync(1);
        Assert.Equal(100m, w1.ReceivedToInventoryMt);
        Assert.Equal(200m, w1.TransferredToVehicleMt);
        Assert.Equal(0m, w1.RemainingMt);
        Assert.True(w1.IsBalanced);

        var t1Quantities = await quantities.GetQuantitiesAsync(t1.ChildLeg.Id);
        Assert.Equal(120m, t1Quantities.SoldMt);
        Assert.Equal(80m, t1Quantities.TransferredToVehicleMt);
        Assert.Equal(0m, t1Quantities.RemainingMt);
        Assert.True(t1Quantities.IsBalanced);

        var v1Quantities = await quantities.GetQuantitiesAsync(v1.ChildLeg.Id);
        Assert.Equal(30m, v1Quantities.TransferredToVehicleMt);
        Assert.Equal(50m, v1Quantities.ReceivedToInventoryMt);
        Assert.Equal(0m, v1Quantities.RemainingMt);
        Assert.True(v1Quantities.IsBalanced);
    }

    private static async Task<int?> ParentLegIdAsync(ApplicationDbContext db, int legId)
        => (await ChainAllocationsAsync(db, legId)).FirstOrDefault()?.SourceTransportLegId;

    private static async Task<List<InventoryTransportLegAllocation>> ChainAllocationsAsync(
        ApplicationDbContext db,
        int legId)
        => await db.InventoryTransportLegAllocations
            .AsNoTracking()
            .Where(a => a.InventoryTransportLegId == legId && a.SourceTransportLegId != null)
            .ToListAsync();

    private static async Task ReceiveToTankAsync(
        ApplicationDbContext db,
        int legId,
        decimal quantityMt,
        int terminalId,
        int storageTankId)
    {
        var service = new InventoryTransportReceiptService(db, new CurrencyConversionService(new PricingService(db)));
        var leg = await service.LoadLegAsync(legId, tracking: true);
        await service.ApplyAsync(
            new InventoryTransportReceiptCreateViewModel
            {
                InventoryTransportLegId = legId,
                ReceiptDate = new DateTime(2026, 5, 11),
                ReceivedQuantityMt = quantityMt,
                ShortageQuantityMt = 0m,
                ReceiptDestination = InventoryTransportReceiptDestination.ToInventory,
                DestinationTerminalId = terminalId,
                DestinationStorageTankId = storageTankId
            },
            leg!,
            saleConversion: null);
    }

    // فروش بارِ در راه: مقدار از حمل مصرف می‌شود ولی هیچ خروج موجودی تازه‌ای ندارد،
    // چون همان تن‌ها هنگام بارگیری از مخزن خارج شده‌اند.
    private static async Task SellFromVehicleAsync(ApplicationDbContext db, int legId, decimal quantityMt)
    {
        db.InventoryTransportReceipts.Add(new InventoryTransportReceipt
        {
            InventoryTransportLegId = legId,
            ReceiptDate = new DateTime(2026, 5, 12),
            ReceivedQuantityMt = quantityMt,
            ShortageQuantityMt = 0m,
            ReceiptDestination = InventoryTransportReceiptDestination.DirectSale
        });
        await db.SaveChangesAsync();
    }

    private static async Task SeedTankAsync(
        ApplicationDbContext db,
        int storageTankId,
        int terminalId,
        decimal quantityMt)
    {
        db.InventoryMovements.Add(new InventoryMovement
        {
            ProductId = 1,
            ContractId = 1,
            TerminalId = terminalId,
            StorageTankId = storageTankId,
            Direction = MovementDirection.In,
            MovementDate = new DateTime(2026, 4, 15),
            QuantityMt = quantityMt,
            ReferenceDocument = $"OPENING:{storageTankId}"
        });
        await db.SaveChangesAsync();
    }
}
