using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Audit;
using PTGOilSystem.Web.Services.Exceptions;
using Xunit;

namespace PTGOilSystem.Web.Tests;

public class TransportFromLoadingTests
{
    [Fact]
    public async Task StartFromLoading_Creates_Linked_Transport_Without_Inventory_Or_Accounting_Postings()
    {
        await using var db = BuildDb();
        await SeedAsync(db);
        var (workflow, _) = BuildServices(db);

        var leg = await workflow.StartFromLoadingAsync(Command(60m));

        Assert.Null(leg.SourceTerminalId);
        Assert.Equal(60m, leg.QuantityMt);
        Assert.Equal(InventoryTransportLegStatus.Loaded, leg.Status);
        var allocation = Assert.Single(await db.InventoryTransportLegAllocations.ToListAsync());
        Assert.Equal(1, allocation.SourceLoadingRegisterId);
        Assert.Equal(1, allocation.SourcePurchaseContractId);
        Assert.Equal(60m, allocation.QuantityMt);
        Assert.Empty(await db.LoadingReceipts.ToListAsync());
        Assert.Empty(await db.InventoryMovements.ToListAsync());
        Assert.Empty(await db.LedgerEntries.ToListAsync());
        Assert.Empty(await db.JournalEntries.ToListAsync());
    }

    [Fact]
    public async Task StartFromLoading_Rejects_More_Than_Remaining_After_Receipt_And_Active_Transport()
    {
        await using var db = BuildDb();
        await SeedAsync(db);
        db.LoadingReceipts.Add(new LoadingReceipt
        {
            LoadingRegisterId = 1,
            TerminalId = 1,
            ReceiptDate = new DateTime(2026, 9, 1),
            ReceivedQuantityMt = 30m
        });
        await db.SaveChangesAsync();
        var (workflow, _) = BuildServices(db);
        await workflow.StartFromLoadingAsync(Command(60m));

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => workflow.StartFromLoadingAsync(Command(11m)));

        Assert.Equal("TRANSPORT_LOADING_INSUFFICIENT", error.Code);
        Assert.Single(await db.InventoryTransportLegs.ToListAsync());
        Assert.Equal(60m, await db.InventoryTransportLegAllocations.SumAsync(a => a.QuantityMt));
        Assert.Empty(await db.InventoryMovements.ToListAsync());
    }

    [Fact]
    public async Task Cancelling_An_Unused_Loading_Transport_Releases_Quantity_Without_Reversal_Movement()
    {
        await using var db = BuildDb();
        await SeedAsync(db);
        var (workflow, batches) = BuildServices(db);
        var first = await workflow.StartFromLoadingAsync(Command(60m));

        await batches.CancelAsync(first.InventoryTransportBatchId!.Value);
        var replacement = await workflow.StartFromLoadingAsync(Command(100m));

        Assert.Equal(InventoryTransportLegStatus.Cancelled, first.Status);
        Assert.Equal(100m, replacement.QuantityMt);
        Assert.Empty(await db.InventoryMovements.ToListAsync());
    }

    private static (TransportWorkflowService Workflow, InventoryTransportBatchService Batches) BuildServices(
        ApplicationDbContext db)
    {
        var stock = new StockService(db);
        var quantities = new TransportQuantityService(db);
        var receipts = new InventoryTransportReceiptService(
            db,
            new CurrencyConversionService(new PricingService(db)),
            quantities: quantities);
        var batches = new InventoryTransportBatchService(db, stock);
        var workflow = new TransportWorkflowService(
            db,
            batches,
            new TransportChainService(db, receipts, quantities),
            receipts,
            new LossEventWorkflowService(db, stock, new AuditService(db)));
        return (workflow, batches);
    }

    private static StartTransportFromLoadingCommand Command(decimal quantityMt) => new()
    {
        LoadingRegisterId = 1,
        QuantityMt = quantityMt,
        TransportType = LoadingTransportType.Truck,
        TruckId = 1,
        TransportDate = new DateTime(2026, 9, 5),
        Reference = "LOAD-TO-TRANSPORT-1"
    };

    private static async Task SeedAsync(ApplicationDbContext db)
    {
        db.Products.Add(new Product { Id = 1, Code = "DSL", Name = "Diesel" });
        db.Companies.Add(new Company { Id = 1, Code = "PTG", Name = "PTG" });
        db.Suppliers.Add(new Supplier { Id = 1, Name = "Supplier" });
        db.Terminals.Add(new Terminal { Id = 1, Code = "T1", Name = "Terminal" });
        db.Trucks.Add(new Truck { Id = 1, PlateNumber = "TRK-1", IsActive = true });
        db.Contracts.Add(new Contract
        {
            Id = 1,
            ContractNumber = "PUR-1",
            ContractType = ContractType.Purchase,
            CompanyId = 1,
            ProductId = 1,
            SupplierId = 1,
            ContractDate = new DateTime(2026, 8, 1),
            QuantityMt = 100m,
            PricingMethod = PricingMethod.Fixed,
            UnitPriceUsd = 500m
        });
        db.LoadingRegisters.Add(new LoadingRegister
        {
            Id = 1,
            ContractId = 1,
            ProductId = 1,
            TransportType = LoadingTransportType.Wagon,
            LoadingDate = new DateTime(2026, 8, 25),
            LoadedQuantityMt = 100m,
            LoadingPriceUsd = 500m,
            RwbNo = "RWB-1"
        });
        await db.SaveChangesAsync();
    }

    private static ApplicationDbContext BuildDb()
        => new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
}
