using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Accounting;
using PTGOilSystem.Web.Services.Exceptions;
using PTGOilSystem.Web.Services.Ledger;
using PTGOilSystem.Web.Services.LoadingReceipts;
using PTGOilSystem.Web.Services.Time;
using Xunit;

namespace PTGOilSystem.Web.Tests;

public sealed class OperationLifecycleTests
{
    private static ApplicationDbContext NewDb() => new(new DbContextOptionsBuilder<ApplicationDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static OperationLifecycleService Service(ApplicationDbContext db, Accounting? accounting = null)
    {
        accounting ??= new Accounting();
        var audit = new AuditService(db);
        var stock = new StockService(db);
        var movements = new InventoryMovementWriter(db, stock);
        var receipts = new LoadingReceiptCancellationService(db, audit,
            NullLogger<LoadingReceiptCancellationService>.Instance, stock, accounting, movements: movements);
        var workflow = new SimplePurchaseWorkflowService(db, movements, accounting, receipts, audit, new LedgerPostingService(db));
        return new OperationLifecycleService(db, receipts, workflow, accounting, audit, new AfghanistanBusinessClock(TimeProvider.System));
    }

    private static async Task SeedAsync(ApplicationDbContext db)
    {
        db.Contracts.Add(new Contract { Id = 1, ContractNumber = "P-001", ContractName = "Test", ContractType = ContractType.Purchase,
            Status = ContractStatus.Active, QuantityMt = 20, ProductId = 1, CompanyId = 1, SupplierId = 1 });
        db.LoadingRegisters.Add(new LoadingRegister { Id = 1, ContractId = 1, ProductId = 1, LoadedQuantityMt = 20 });
        db.LoadingReceipts.Add(new LoadingReceipt { Id = 1, LoadingRegisterId = 1, TerminalId = 1, StorageTankId = 1, ReceivedQuantityMt = 20 });
        db.InventoryMovements.Add(new InventoryMovement { Id = 1, ContractId = 1, LoadingReceiptId = 1,
            TerminalId = 1, StorageTankId = 1, ProductId = 1, Direction = MovementDirection.In, QuantityMt = 20 });
        await db.SaveChangesAsync();
    }

    [Theory]
    [InlineData("Contract")]
    [InlineData("LoadingRegister")]
    [InlineData("LoadingReceipt")]
    public async Task Active_Record_Cannot_Be_Archived(string kind)
    {
        await using var db = NewDb(); await SeedAsync(db);
        await Assert.ThrowsAsync<BusinessRuleException>(() => Service(db).ArchiveAsync(kind, 1, null));
        Assert.False((await db.Contracts.SingleAsync()).IsArchived);
        Assert.False((await db.LoadingRegisters.SingleAsync()).IsArchived);
        Assert.False((await db.LoadingReceipts.SingleAsync()).IsArchived);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancel_Contract_Reverses_Receipt_Then_Archive_Preserves_Stock_History(bool childrenArchivedFirst)
    {
        await using var db = NewDb(); await SeedAsync(db);
        var service = Service(db);
        if (childrenArchivedFirst)
        {
            await service.CancelAsync("LoadingRegister", 1, "اشتباه", null);
            await service.ArchiveAsync("LoadingReceipt", 1, null);
            await service.ArchiveAsync("LoadingRegister", 1, null);
        }
        await service.CancelAsync("Contract", 1, "اشتباه", null);
        Assert.Equal(ContractStatus.Cancelled, (await db.Contracts.SingleAsync()).Status);
        Assert.True((await db.LoadingRegisters.SingleAsync()).IsCancelled);
        Assert.True((await db.LoadingReceipts.SingleAsync()).IsCancelled);
        var before = await db.InventoryMovements.CountAsync();
        Assert.Equal(2, before);
        Assert.Equal(0, await new StockService(db).GetFreeQuantityMtAsync(1, 1, 1));
        foreach (var kind in new[] { "LoadingReceipt", "LoadingRegister", "Contract" })
            await service.ArchiveAsync(kind, 1, null);
        Assert.Equal(before, await db.InventoryMovements.CountAsync());
        Assert.Equal(0, await db.Contracts.CountAsync(x => !x.IsArchived));
        Assert.Equal(0, await db.LoadingRegisters.CountAsync(x => !x.IsArchived));
        Assert.Equal(0, await db.LoadingReceipts.CountAsync(x => !x.IsArchived));
        Assert.Single(await db.Contracts.ToListAsync());
        Assert.Single(await db.LoadingReceipts.ToListAsync());
    }

    [Fact]
    public async Task Repeated_Loading_Cancellation_Does_Not_Double_Reverse()
    {
        await using var db = NewDb(); await SeedAsync(db);
        var accounting = new Accounting(); var service = Service(db, accounting);
        await service.CancelAsync("LoadingRegister", 1, "اشتباه", null);
        await service.CancelAsync("LoadingRegister", 1, "اشتباه", null);
        Assert.Equal(1, accounting.Reversals);
        Assert.Equal(2, await db.InventoryMovements.CountAsync());
    }

    [Fact]
    public async Task Missing_Reason_Does_Not_Modify_Anything()
    {
        await using var db = NewDb(); await SeedAsync(db);
        await Assert.ThrowsAsync<BusinessRuleException>(() => Service(db).CancelAsync("Contract", 1, " ", null));
        Assert.Equal(ContractStatus.Active, (await db.Contracts.SingleAsync()).Status);
        Assert.Single(await db.InventoryMovements.ToListAsync());
    }

    [Fact]
    public async Task Contract_With_Payment_Is_Blocked_Before_Changing_Receipt()
    {
        await using var db = NewDb(); await SeedAsync(db);
        db.PaymentTransactions.Add(new PaymentTransaction { Id = 1, ContractId = 1 }); await db.SaveChangesAsync();
        await Assert.ThrowsAsync<BusinessRuleException>(() => Service(db).CancelAsync("Contract", 1, "اشتباه", null));
        Assert.False((await db.LoadingReceipts.SingleAsync()).IsCancelled);
        Assert.Single(await db.InventoryMovements.ToListAsync());
    }

    [Fact]
    public async Task Cancelled_Loading_Is_Excluded_From_Purchase_Aggregation()
    {
        await using var db = NewDb(); await SeedAsync(db);
        (await db.LoadingRegisters.SingleAsync()).IsCancelled = true; await db.SaveChangesAsync();
        var snapshot = await new PurchaseAggregationService(db).AggregateForContractAsync(1, 100);
        Assert.Equal(0, snapshot.TotalLoadedQuantityMt);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Simple_Purchase_Cancel_Does_Not_Reverse_The_Reversal(bool receiptAlreadyCancelled)
    {
        await using var db = NewDb(); await SeedAsync(db);
        (await db.LoadingRegisters.SingleAsync()).ImportUniqueKey = "SIMPLE-PURCHASE-CONTRACT:1";
        db.LedgerEntries.Add(new LedgerEntry { Id = 1, ContractId = 1, SupplierId = 1, SourceType = "Loading", SourceId = 1,
            Side = LedgerSide.Credit, AmountUsd = 200, SourceAmount = 200, Currency = "USD",
            SourceCurrencyCode = "USD", AppliedFxRateToUsd = 1, Reference = "LOAD-1" });
        await db.SaveChangesAsync();
        if (receiptAlreadyCancelled)
        {
            var stock = new StockService(db);
            var cancellation = new LoadingReceiptCancellationService(db, new AuditService(db),
                NullLogger<LoadingReceiptCancellationService>.Instance, stock, new Accounting(),
                movements: new InventoryMovementWriter(db, stock));
            var result = await cancellation.CancelAsync([1], "اشتباه", null);
            Assert.True(result.Succeeded);
        }
        await Service(db).CancelAsync("Contract", 1, "اشتباه", null);
        var rows = await db.LedgerEntries.ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Equal(0, rows.Sum(x => x.Side == LedgerSide.Credit ? x.AmountUsd : -x.AmountUsd));
        Assert.Equal(2, await db.InventoryMovements.CountAsync());
    }

    private sealed class Accounting : IPurchaseAccountingAdapter
    {
        public int Reversals { get; private set; }
        private static Task<PurchaseAccountingResult> Skip() => Task.FromResult(new PurchaseAccountingResult(PaymentPostingStatus.Skipped, null, "test"));
        public Task<PurchaseAccountingResult> TryPostPurchaseAsync(LoadingRegister item, CancellationToken ct = default) => Skip();
        public Task<IReadOnlyList<PurchaseAccountingResult>> TryPostPurchasesAsync(IReadOnlyList<LoadingRegister> items, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<PurchaseAccountingResult> TryPostInventoryReceiptAsync(LoadingReceipt item, CancellationToken ct = default) => Skip();
        public Task<PurchaseAccountingResult> TryPostTransportReceiptAsync(InventoryTransportReceipt item, CancellationToken ct = default) => Skip();
        public Task<PurchaseAccountingResult> TryPostPurchaseReversalAsync(LoadingRegister item, CancellationToken ct = default) { Reversals++; return Skip(); }
        public Task<PurchaseAccountingResult> TryPostInventoryReceiptReversalAsync(LoadingReceipt item, CancellationToken ct = default) => Skip();
    }
}
