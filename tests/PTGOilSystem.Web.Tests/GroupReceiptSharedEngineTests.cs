using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.InventoryTransport;
using PTGOilSystem.Web.Services;
using Xunit;

namespace PTGOilSystem.Web.Tests;

// رسید گروهی قبلاً رسید/ضایعات/سند موجودی را خودش می‌ساخت. نتیجه: تقسیم چندقراردادی و
// قلاب‌های حسابداری/نسب‌نامه از این مسیر رد می‌شدند و ReceiveToInventory دو موتور داشت.
// این تست‌ها قفل می‌کنند که فقط یک موتور بماند.
public class GroupReceiptSharedEngineTests
{
    [Fact]
    public void The_Group_Receipt_Action_Builds_Nothing_By_Hand()
    {
        var source = ReadRepoFile("src/PTGOilSystem.Web/Controllers/InventoryTransportLegsController.cs");

        Assert.DoesNotContain("new InventoryMovement", source);
        Assert.DoesNotContain("_db.InventoryTransportReceipts.Add(", source);
        Assert.Contains("await _receiptService.ApplyAsync(", source);
    }

    // همان قاعدهٔ تقسیم که رسید تکی دارد باید روی رسید گروهی هم اجرا شود.
    [Fact]
    public async Task A_Multi_Contract_Group_Receipt_Splits_The_Destination_Inventory()
    {
        await using var db = TransportChainScenario.BuildDb();
        await TransportChainScenario.SeedAsync(db);
        await TransportChainScenario.SeedLegAsync(db, legId: 1, LoadingTransportType.Wagon, 300m, contractId: 1);
        await TransportChainScenario.AddSourceAllocationAsync(db, legId: 1, contractId: 1, quantityMt: 100m);
        await TransportChainScenario.AddSourceAllocationAsync(db, legId: 1, contractId: 2, quantityMt: 200m);

        var service = new InventoryTransportReceiptService(db, new CurrencyConversionService(new PricingService(db)));
        var leg = await service.LoadLegAsync(1, tracking: true);
        var receipt = await service.ApplyAsync(
            new InventoryTransportReceiptCreateViewModel
            {
                InventoryTransportLegId = 1,
                ReceiptDate = new DateTime(2026, 5, 11),
                ReceivedQuantityMt = 300m,
                ShortageQuantityMt = 0m,
                ReceiptDestination = InventoryTransportReceiptDestination.ToInventory,
                DestinationTerminalId = 2,
                DestinationStorageTankId = 2
            },
            leg!,
            saleConversion: null);

        var inbound = await db.InventoryMovements
            .AsNoTracking()
            .Where(m => m.ReferenceDocument == $"TRANSPORT-RECEIPT:{receipt.Id}")
            .ToListAsync();

        Assert.Equal(2, inbound.Count);
        Assert.Equal(100m, inbound.Single(m => m.ContractId == 1).QuantityMt);
        Assert.Equal(200m, inbound.Single(m => m.ContractId == 2).QuantityMt);
        Assert.Equal(300m, inbound.Sum(m => m.QuantityMt));
    }

    private static string ReadRepoFile(string relativePath)
    {
        var repositoryRoot = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        return File.ReadAllText(Path.Combine(repositoryRoot, relativePath));
    }
}
