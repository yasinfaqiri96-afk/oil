using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PTGOilSystem.Web.Configuration;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services.Accounting;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// Inter-terminal transfers — the gap that made Cogs unsafe.
///
/// Legacy writes no ledger row for a transfer, so as with the Stage 8 mappings these tests are
/// the only statement of what the numbers should be. What they are really pinning is that the
/// cost a sale later reads at the destination is the cost the goods actually arrived with, not
/// whatever the destination pool happened to hold.
/// </summary>
[Collection(AccountingPostgreSqlCollection.CollectionName)]
[Trait("Category", "PostgreSql")]
[Trait("Category", "Integration")]
public sealed class InventoryTransferAccountingAdapterTests(AccountingPostgreSqlFixture fixture)
{
    private static readonly DateTime EventDate = new(2026, 7, 5);

    [Fact]
    public void SourceEventId_Formats_Are_Stable()
    {
        Assert.Equal(
            "InventoryTransportLeg:7:Loaded",
            InventoryTransferAccountingAdapter.BuildLegLoadedSourceEventId(7));
        Assert.Equal(
            "InventoryTransportLeg:7:LoadReversed",
            InventoryTransferAccountingAdapter.BuildLegLoadReversedSourceEventId(7));
        Assert.Equal(
            "InventoryTransportReceipt:7:Received",
            InventoryTransferAccountingAdapter.BuildReceiptSourceEventId(7));
    }

    [Fact]
    public void Journal_Numbers_Are_Stable()
    {
        var generator = new AccountingJournalNumberGenerator();
        Assert.Equal("TRL-000003-0000000007", generator.ForTransportLegLoad(3, 7));
        Assert.Equal("TRLR-000003-0000000007", generator.ForTransportLegLoadReversal(3, 7));
        Assert.Equal("TRR-000003-0000000007", generator.ForTransportReceipt(3, 7));
    }

    // ---- Leg load ----

    [Fact]
    public async Task Load_Moves_Cost_Out_Of_The_Source_Pool_And_Into_Transit()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);

        // 20 MT at 10,000 averages 500 per MT.
        await new InventoryValuationService(db)
            .ApplyReceiptAsync(scope.Company.Id, scope.Product.Id, scope.Terminal.Id, 20m, 10_000m);
        var leg = await AddLegAsync(db, scope, quantityMt: 8m);

        var result = await CreateAdapter(db).TryPostLegLoadAsync(leg);

        Assert.Equal(PaymentPostingStatus.Posted, result.Status);

        // 8 x 500 = 4,000.
        var debit = Assert.Single(result.Journal!.Lines.Where(x => x.Debit > 0m));
        Assert.Equal(scope.Settings.InventoryInTransitAccountId, debit.AccountId);
        Assert.Equal(4_000m, debit.Debit);
        var credit = Assert.Single(result.Journal.Lines.Where(x => x.Credit > 0m));
        Assert.Equal(scope.Settings.InventoryAccountId, credit.AccountId);
        Assert.Equal(4_000m, credit.Credit);

        var pool = await GetPoolAsync(db, scope.Company.Id, scope.Product.Id, scope.Terminal.Id);
        Assert.Equal(12m, pool!.QuantityMt);
        Assert.Equal(6_000m, pool.TotalValueUsd);
    }

    [Fact]
    public async Task Load_Is_Idempotent_And_Consumes_The_Pool_Once()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);

        await new InventoryValuationService(db)
            .ApplyReceiptAsync(scope.Company.Id, scope.Product.Id, scope.Terminal.Id, 20m, 10_000m);
        var leg = await AddLegAsync(db, scope, quantityMt: 8m);
        var adapter = CreateAdapter(db);

        var first = await adapter.TryPostLegLoadAsync(leg);
        var second = await adapter.TryPostLegLoadAsync(leg);

        Assert.Equal(PaymentPostingStatus.Posted, first.Status);
        Assert.Equal(PaymentPostingStatus.Duplicate, second.Status);
        Assert.Equal("DUPLICATE_SOURCE_EVENT", second.Reason);

        var pool = await GetPoolAsync(db, scope.Company.Id, scope.Product.Id, scope.Terminal.Id);
        Assert.Equal(12m, pool!.QuantityMt);
        Assert.Equal(6_000m, pool.TotalValueUsd);
    }

    [Fact]
    public async Task Load_Beyond_The_Source_Pool_Is_Skipped_Without_Touching_It()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);

        await new InventoryValuationService(db)
            .ApplyReceiptAsync(scope.Company.Id, scope.Product.Id, scope.Terminal.Id, 1m, 500m);
        var leg = await AddLegAsync(db, scope, quantityMt: 5m);

        var result = await CreateAdapter(db).TryPostLegLoadAsync(leg);

        Assert.Equal(PaymentPostingStatus.Skipped, result.Status);
        Assert.Equal("INVENTORY_NOT_VALUED", result.Reason);

        var pool = await GetPoolAsync(db, scope.Company.Id, scope.Product.Id, scope.Terminal.Id);
        Assert.Equal(1m, pool!.QuantityMt);
        Assert.Equal(500m, pool.TotalValueUsd);
    }

    // ---- Receipt ----

    [Fact]
    public async Task Receipt_Lands_The_Cost_In_The_Destination_Pool()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var destination = await AddTerminalAsync(db);

        await new InventoryValuationService(db)
            .ApplyReceiptAsync(scope.Company.Id, scope.Product.Id, scope.Terminal.Id, 20m, 10_000m);
        var leg = await AddLegAsync(db, scope, quantityMt: 8m, destinationTerminalId: destination.Id);
        var adapter = CreateAdapter(db);
        await adapter.TryPostLegLoadAsync(leg);

        var receipt = await AddReceiptAsync(db, leg, destination.Id, receivedMt: 8m, shortageMt: 0m);
        var result = await adapter.TryPostReceiptAsync(receipt);

        Assert.Equal(PaymentPostingStatus.Posted, result.Status);

        var debit = Assert.Single(result.Journal!.Lines.Where(x => x.Debit > 0m));
        Assert.Equal(scope.Settings.InventoryAccountId, debit.AccountId);
        Assert.Equal(4_000m, debit.Debit);
        var credit = Assert.Single(result.Journal.Lines.Where(x => x.Credit > 0m));
        Assert.Equal(scope.Settings.InventoryInTransitAccountId, credit.AccountId);
        Assert.Equal(4_000m, credit.Credit);

        // Transit is empty again and the goods are priced at the destination.
        var destinationPool = await GetPoolAsync(db, scope.Company.Id, scope.Product.Id, destination.Id);
        Assert.Equal(8m, destinationPool!.QuantityMt);
        Assert.Equal(4_000m, destinationPool.TotalValueUsd);
    }

    [Fact]
    public async Task Received_Cost_Joins_The_Destination_Average_Rather_Than_Replacing_It()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var destination = await AddTerminalAsync(db);

        var valuation = new InventoryValuationService(db);
        await valuation.ApplyReceiptAsync(scope.Company.Id, scope.Product.Id, scope.Terminal.Id, 20m, 10_000m);
        // The destination already holds cheaper stock: 2 MT at 200 averages 100 per MT.
        await valuation.ApplyReceiptAsync(scope.Company.Id, scope.Product.Id, destination.Id, 2m, 200m);

        var leg = await AddLegAsync(db, scope, quantityMt: 8m, destinationTerminalId: destination.Id);
        var adapter = CreateAdapter(db);
        await adapter.TryPostLegLoadAsync(leg);
        var receipt = await AddReceiptAsync(db, leg, destination.Id, receivedMt: 8m, shortageMt: 0m);

        await adapter.TryPostReceiptAsync(receipt);

        // 2 MT at 100 plus 8 MT at 500 is 10 MT for 4,200 — an average of 420, which is what a
        // sale out of this terminal must now cost at.
        var pool = await GetPoolAsync(db, scope.Company.Id, scope.Product.Id, destination.Id);
        Assert.Equal(10m, pool!.QuantityMt);
        Assert.Equal(4_200m, pool.TotalValueUsd);
        Assert.Equal(420m, pool.AverageUnitCostUsd);
    }

    [Fact]
    public async Task Receipt_Writes_The_Shortage_Off_At_Its_Share_Of_The_Cost()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var destination = await AddTerminalAsync(db);

        await new InventoryValuationService(db)
            .ApplyReceiptAsync(scope.Company.Id, scope.Product.Id, scope.Terminal.Id, 20m, 10_000m);
        var leg = await AddLegAsync(db, scope, quantityMt: 10m, destinationTerminalId: destination.Id);
        var adapter = CreateAdapter(db);
        await adapter.TryPostLegLoadAsync(leg);

        // 9 MT arrive out of 10; the missing tonne is worth its share of the 5,000 in transit.
        var receipt = await AddReceiptAsync(db, leg, destination.Id, receivedMt: 9m, shortageMt: 1m);
        var result = await adapter.TryPostReceiptAsync(receipt);

        Assert.Equal(PaymentPostingStatus.Posted, result.Status);

        var inventoryDebit = Assert.Single(
            result.Journal!.Lines.Where(x => x.AccountId == scope.Settings.InventoryAccountId));
        Assert.Equal(4_500m, inventoryDebit.Debit);
        var lossDebit = Assert.Single(
            result.Journal.Lines.Where(x => x.AccountId == scope.Settings.InventoryLossAccountId));
        Assert.Equal(500m, lossDebit.Debit);
        var transitCredit = Assert.Single(
            result.Journal.Lines.Where(x => x.AccountId == scope.Settings.InventoryInTransitAccountId));
        Assert.Equal(5_000m, transitCredit.Credit);

        // Only what arrived is priced at the destination.
        var pool = await GetPoolAsync(db, scope.Company.Id, scope.Product.Id, destination.Id);
        Assert.Equal(9m, pool!.QuantityMt);
        Assert.Equal(4_500m, pool.TotalValueUsd);
    }

    [Fact]
    public async Task Partial_Receipts_Drain_Transit_Exactly_With_No_Crumb_Left()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var destination = await AddTerminalAsync(db);

        // 3 MT for 1,000 averages 333.333333 — a figure that cannot be split into thirds cleanly.
        await new InventoryValuationService(db)
            .ApplyReceiptAsync(scope.Company.Id, scope.Product.Id, scope.Terminal.Id, 3m, 1_000m);
        var leg = await AddLegAsync(db, scope, quantityMt: 3m, destinationTerminalId: destination.Id);
        var adapter = CreateAdapter(db);
        await adapter.TryPostLegLoadAsync(leg);

        var first = await AddReceiptAsync(db, leg, destination.Id, receivedMt: 1m, shortageMt: 0m);
        var firstResult = await adapter.TryPostReceiptAsync(first);
        // round(1,000 x 1/3, 4) = 333.3333
        Assert.Equal(333.3333m, firstResult.Journal!.Lines.Sum(x => x.Debit));

        var second = await AddReceiptAsync(db, leg, destination.Id, receivedMt: 2m, shortageMt: 0m);
        var secondResult = await adapter.TryPostReceiptAsync(second);
        // The last receipt takes whatever is left rather than its own rounded share, so the two
        // draws add back to the 1,000 that went into transit.
        Assert.Equal(666.6667m, secondResult.Journal!.Lines.Sum(x => x.Debit));

        var pool = await GetPoolAsync(db, scope.Company.Id, scope.Product.Id, destination.Id);
        Assert.Equal(3m, pool!.QuantityMt);
        Assert.Equal(1_000m, pool.TotalValueUsd);
    }

    [Fact]
    public async Task Receipt_Is_Idempotent_And_Prices_The_Destination_Once()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var destination = await AddTerminalAsync(db);

        await new InventoryValuationService(db)
            .ApplyReceiptAsync(scope.Company.Id, scope.Product.Id, scope.Terminal.Id, 20m, 10_000m);
        var leg = await AddLegAsync(db, scope, quantityMt: 8m, destinationTerminalId: destination.Id);
        var adapter = CreateAdapter(db);
        await adapter.TryPostLegLoadAsync(leg);
        var receipt = await AddReceiptAsync(db, leg, destination.Id, receivedMt: 8m, shortageMt: 0m);

        var first = await adapter.TryPostReceiptAsync(receipt);
        var second = await adapter.TryPostReceiptAsync(receipt);

        Assert.Equal(PaymentPostingStatus.Posted, first.Status);
        Assert.Equal(PaymentPostingStatus.Duplicate, second.Status);
        Assert.Equal("DUPLICATE_SOURCE_EVENT", second.Reason);

        var pool = await GetPoolAsync(db, scope.Company.Id, scope.Product.Id, destination.Id);
        Assert.Equal(8m, pool!.QuantityMt);
        Assert.Equal(4_000m, pool.TotalValueUsd);
    }

    [Fact]
    public async Task Receipt_Without_A_Posted_Load_Is_Skipped()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var destination = await AddTerminalAsync(db);

        var leg = await AddLegAsync(db, scope, quantityMt: 8m, destinationTerminalId: destination.Id);
        var receipt = await AddReceiptAsync(db, leg, destination.Id, receivedMt: 8m, shortageMt: 0m);

        var result = await CreateAdapter(db).TryPostReceiptAsync(receipt);

        Assert.Equal(PaymentPostingStatus.Skipped, result.Status);
        Assert.Equal("LEG_LOAD_NOT_POSTED", result.Reason);
        Assert.Null(await GetPoolAsync(db, scope.Company.Id, scope.Product.Id, destination.Id));
    }

    [Fact]
    public async Task Receipt_Beyond_What_Is_In_Transit_Is_Skipped()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var destination = await AddTerminalAsync(db);

        await new InventoryValuationService(db)
            .ApplyReceiptAsync(scope.Company.Id, scope.Product.Id, scope.Terminal.Id, 20m, 10_000m);
        var leg = await AddLegAsync(db, scope, quantityMt: 8m, destinationTerminalId: destination.Id);
        var adapter = CreateAdapter(db);
        await adapter.TryPostLegLoadAsync(leg);

        // A truck is allowed to hand over more than the leg still owes; the extra has no cost in
        // transit to draw on, so the whole receipt stays legacy-only rather than guess at one.
        var receipt = await AddReceiptAsync(db, leg, destination.Id, receivedMt: 9m, shortageMt: 0m);
        var result = await adapter.TryPostReceiptAsync(receipt);

        Assert.Equal(PaymentPostingStatus.Skipped, result.Status);
        Assert.Equal("RECEIPT_EXCEEDS_IN_TRANSIT", result.Reason);
        Assert.Null(await GetPoolAsync(db, scope.Company.Id, scope.Product.Id, destination.Id));
    }

    [Fact]
    public async Task Direct_Sale_Receipt_Is_Skipped_And_Leaves_The_Cost_In_Transit()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var destination = await AddTerminalAsync(db);

        await new InventoryValuationService(db)
            .ApplyReceiptAsync(scope.Company.Id, scope.Product.Id, scope.Terminal.Id, 20m, 10_000m);
        var leg = await AddLegAsync(db, scope, quantityMt: 8m, destinationTerminalId: destination.Id);
        var adapter = CreateAdapter(db);
        await adapter.TryPostLegLoadAsync(leg);

        var receipt = await AddReceiptAsync(
            db, leg, destination.Id, receivedMt: 8m, shortageMt: 0m,
            destinationKind: InventoryTransportReceiptDestination.DirectSale);
        var result = await adapter.TryPostReceiptAsync(receipt);

        // Goods sold straight off the truck never join a terminal average, so there is nothing
        // here for this pilot to price. Their cost stays in transit until COGS learns to take it.
        Assert.Equal(PaymentPostingStatus.Skipped, result.Status);
        Assert.Equal("RECEIPT_DESTINATION_NOT_INVENTORY", result.Reason);
        Assert.Null(await GetPoolAsync(db, scope.Company.Id, scope.Product.Id, destination.Id));
    }

    [Fact]
    public async Task Settlement_Only_Receipt_Is_Skipped()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var destination = await AddTerminalAsync(db);

        await new InventoryValuationService(db)
            .ApplyReceiptAsync(scope.Company.Id, scope.Product.Id, scope.Terminal.Id, 20m, 10_000m);
        var leg = await AddLegAsync(db, scope, quantityMt: 8m, destinationTerminalId: destination.Id);
        var adapter = CreateAdapter(db);
        await adapter.TryPostLegLoadAsync(leg);

        // Freight-only settlement: the load is still on the truck, so nothing has arrived.
        var receipt = await AddReceiptAsync(db, leg, destination.Id, receivedMt: 0m, shortageMt: 0m);
        var result = await adapter.TryPostReceiptAsync(receipt);

        Assert.Equal(PaymentPostingStatus.Skipped, result.Status);
        Assert.Equal("NO_QUANTITY_RECEIVED", result.Reason);
    }

    // ---- Load reversal ----

    [Fact]
    public async Task Load_Reversal_Returns_The_Cost_To_The_Source_Pool()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);

        await new InventoryValuationService(db)
            .ApplyReceiptAsync(scope.Company.Id, scope.Product.Id, scope.Terminal.Id, 20m, 10_000m);
        var leg = await AddLegAsync(db, scope, quantityMt: 8m);
        var adapter = CreateAdapter(db);
        await adapter.TryPostLegLoadAsync(leg);

        var result = await adapter.TryPostLegLoadReversalAsync(leg);

        Assert.Equal(PaymentPostingStatus.Posted, result.Status);
        Assert.True(result.Journal!.IsReversal);

        // The pool is whole again, and the two journals cancel.
        var pool = await GetPoolAsync(db, scope.Company.Id, scope.Product.Id, scope.Terminal.Id);
        Assert.Equal(20m, pool!.QuantityMt);
        Assert.Equal(10_000m, pool.TotalValueUsd);

        var net = await db.JournalEntryLines
            .AsNoTracking()
            .Where(x => x.JournalEntry!.CompanyId == scope.Company.Id
                && x.JournalEntry.SourceModule == InventoryTransferAccountingAdapter.SourceModule)
            .SumAsync(x => x.Debit - x.Credit);
        Assert.Equal(0m, net);
    }

    [Fact]
    public async Task Load_Reversal_Is_Idempotent()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);

        await new InventoryValuationService(db)
            .ApplyReceiptAsync(scope.Company.Id, scope.Product.Id, scope.Terminal.Id, 20m, 10_000m);
        var leg = await AddLegAsync(db, scope, quantityMt: 8m);
        var adapter = CreateAdapter(db);
        await adapter.TryPostLegLoadAsync(leg);

        var first = await adapter.TryPostLegLoadReversalAsync(leg);
        var second = await adapter.TryPostLegLoadReversalAsync(leg);

        Assert.Equal(PaymentPostingStatus.Posted, first.Status);
        Assert.Equal(PaymentPostingStatus.Duplicate, second.Status);

        var pool = await GetPoolAsync(db, scope.Company.Id, scope.Product.Id, scope.Terminal.Id);
        Assert.Equal(20m, pool!.QuantityMt);
        Assert.Equal(10_000m, pool.TotalValueUsd);
    }

    [Fact]
    public async Task Load_Reversal_After_A_Receipt_Is_Refused()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var destination = await AddTerminalAsync(db);

        await new InventoryValuationService(db)
            .ApplyReceiptAsync(scope.Company.Id, scope.Product.Id, scope.Terminal.Id, 20m, 10_000m);
        var leg = await AddLegAsync(db, scope, quantityMt: 8m, destinationTerminalId: destination.Id);
        var adapter = CreateAdapter(db);
        await adapter.TryPostLegLoadAsync(leg);
        var receipt = await AddReceiptAsync(db, leg, destination.Id, receivedMt: 4m, shortageMt: 0m);
        await adapter.TryPostReceiptAsync(receipt);

        var result = await adapter.TryPostLegLoadReversalAsync(leg);

        // Part of this load is already priced at the destination; taking the whole cost back to
        // the source would count it in two places at once.
        Assert.Equal(PaymentPostingStatus.Skipped, result.Status);
        Assert.Equal("LEG_ALREADY_RECEIPTED", result.Reason);
    }

    [Fact]
    public async Task Receipt_After_A_Reversed_Load_Is_Skipped()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var destination = await AddTerminalAsync(db);

        await new InventoryValuationService(db)
            .ApplyReceiptAsync(scope.Company.Id, scope.Product.Id, scope.Terminal.Id, 20m, 10_000m);
        var leg = await AddLegAsync(db, scope, quantityMt: 8m, destinationTerminalId: destination.Id);
        var adapter = CreateAdapter(db);
        await adapter.TryPostLegLoadAsync(leg);
        await adapter.TryPostLegLoadReversalAsync(leg);

        var receipt = await AddReceiptAsync(db, leg, destination.Id, receivedMt: 8m, shortageMt: 0m);
        var result = await adapter.TryPostReceiptAsync(receipt);

        Assert.Equal(PaymentPostingStatus.Skipped, result.Status);
        Assert.Equal("LEG_LOAD_REVERSED", result.Reason);
    }

    // ---- Flags ----

    [Fact]
    public async Task Pilot_Is_Off_By_Default()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);

        await new InventoryValuationService(db)
            .ApplyReceiptAsync(scope.Company.Id, scope.Product.Id, scope.Terminal.Id, 20m, 10_000m);
        var leg = await AddLegAsync(db, scope, quantityMt: 8m);

        var options = new AccountingOptions { Enabled = true, DefaultFunctionalCurrencyCode = "USD" };
        var adapter = new InventoryTransferAccountingAdapter(
            db,
            CreatePostingService(db),
            new AccountingJournalNumberGenerator(),
            new InventoryValuationService(db),
            Options.Create(options),
            NullLogger<InventoryTransferAccountingAdapter>.Instance);

        var result = await adapter.TryPostLegLoadAsync(leg);

        Assert.Equal(PaymentPostingStatus.Skipped, result.Status);
        Assert.Equal("PILOT_DISABLED", result.Reason);

        var pool = await GetPoolAsync(db, scope.Company.Id, scope.Product.Id, scope.Terminal.Id);
        Assert.Equal(20m, pool!.QuantityMt);
        Assert.Equal(10_000m, pool.TotalValueUsd);
    }

    // ---- Helpers ----

    private static AccountingOptions EnabledOptions()
        => new()
        {
            Enabled = true,
            DefaultFunctionalCurrencyCode = "USD",
            Pilots = new AccountingPilotOptions { InventoryTransfer = true }
        };

    private static AccountingPostingService CreatePostingService(ApplicationDbContext db)
        => new(db, new PeriodGuard(db, new FiscalCalendarService(db)), Options.Create(EnabledOptions()), new SystemCompanyProvider(db));

    private static InventoryTransferAccountingAdapter CreateAdapter(ApplicationDbContext db)
        => new(
            db,
            CreatePostingService(db),
            new AccountingJournalNumberGenerator(),
            new InventoryValuationService(db),
            Options.Create(EnabledOptions()),
            NullLogger<InventoryTransferAccountingAdapter>.Instance);

    private static async Task<Terminal> AddTerminalAsync(ApplicationDbContext db)
    {
        var terminal = new Terminal
        {
            Code = PaymentAccountingAdapterTests.Unique("T"),
            Name = PaymentAccountingAdapterTests.Unique("Terminal"),
            IsActive = true
        };
        db.Terminals.Add(terminal);
        await db.SaveChangesAsync();
        return terminal;
    }

    private static async Task<InventoryTransportLeg> AddLegAsync(
        ApplicationDbContext db,
        PaymentAccountingAdapterTests.PaymentScope scope,
        decimal quantityMt,
        int? destinationTerminalId = null)
    {
        var leg = new InventoryTransportLeg
        {
            SourcePurchaseContractId = scope.Contract.Id,
            ProductId = scope.Product.Id,
            SourceTerminalId = scope.Terminal.Id,
            SourceStorageTankId = scope.Tank.Id,
            DestinationTerminalId = destinationTerminalId,
            TransportType = LoadingTransportType.Truck,
            LoadedDate = EventDate,
            QuantityMt = quantityMt,
            Status = InventoryTransportLegStatus.Loaded
        };
        db.InventoryTransportLegs.Add(leg);
        await db.SaveChangesAsync();
        return leg;
    }

    private static async Task<InventoryTransportReceipt> AddReceiptAsync(
        ApplicationDbContext db,
        InventoryTransportLeg leg,
        int destinationTerminalId,
        decimal receivedMt,
        decimal shortageMt,
        InventoryTransportReceiptDestination destinationKind
            = InventoryTransportReceiptDestination.ToInventory)
    {
        var receipt = new InventoryTransportReceipt
        {
            InventoryTransportLegId = leg.Id,
            ReceiptDate = EventDate,
            ReceivedQuantityMt = receivedMt,
            ShortageQuantityMt = shortageMt,
            ReceiptDestination = destinationKind,
            DestinationTerminalId = destinationTerminalId
        };
        db.InventoryTransportReceipts.Add(receipt);
        await db.SaveChangesAsync();
        return receipt;
    }

    private static async Task<InventoryAverageCost?> GetPoolAsync(
        ApplicationDbContext db,
        int companyId,
        int productId,
        int terminalId)
        => await db.InventoryAverageCosts
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.CompanyId == companyId
                && x.ProductId == productId
                && x.TerminalId == terminalId);
}
