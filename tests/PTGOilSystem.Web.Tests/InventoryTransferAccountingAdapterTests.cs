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

    /// <summary>
    /// یک leg می‌تواند سهم چند قرارداد خرید را با هم ببرد (InventoryTransportLegAllocation).
    /// بهای انتقال از حوضِ (شرکت، کالا، ترمینال) برداشته می‌شود و قرارداد در آن حوض نقشی ندارد،
    /// پس کلِ مقدار leg با میانگینِ همان ترمینال قیمت می‌خورد؛ ContractId روی خطوط فقط ارجاع است.
    /// </summary>
    [Fact]
    public async Task Load_Of_A_Multi_Contract_Leg_Values_The_Whole_Quantity_At_The_Terminal_Average()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);

        // 40 MT at 20,000 averages 500 per MT.
        await new InventoryValuationService(db)
            .ApplyReceiptAsync(scope.Company.Id, scope.Product.Id, scope.Terminal.Id, 40m, 20_000m);
        var secondContract = await AddPurchaseContractAsync(db, scope);
        var leg = await AddLegAsync(db, scope, quantityMt: 25m);
        db.InventoryTransportLegAllocations.AddRange(
            new InventoryTransportLegAllocation
            {
                InventoryTransportLegId = leg.Id,
                SourcePurchaseContractId = scope.Contract.Id,
                QuantityMt = 10m
            },
            new InventoryTransportLegAllocation
            {
                InventoryTransportLegId = leg.Id,
                SourcePurchaseContractId = secondContract.Id,
                QuantityMt = 15m
            });
        await db.SaveChangesAsync();

        var adapter = CreateAdapter(db);
        var result = await adapter.TryPostLegLoadAsync(leg);

        // 25 x 500 = 12,500 — کل مقدار leg، نه سهم یک قرارداد.
        Assert.Equal(PaymentPostingStatus.Posted, result.Status);
        Assert.Equal(12_500m, result.Journal!.Lines.Sum(x => x.Debit));
        Assert.Equal(12_500m, result.Journal.Lines.Sum(x => x.Credit));
        // بُعدِ قرارداد = قراردادِ سرصفحهٔ leg (فقط ارجاع؛ در ارزش‌گذاری اثر ندارد).
        Assert.All(result.Journal.Lines, line => Assert.Equal(scope.Contract.Id, line.ContractId));

        var pool = await GetPoolAsync(db, scope.Company.Id, scope.Product.Id, scope.Terminal.Id);
        Assert.Equal(15m, pool!.QuantityMt);
        Assert.Equal(7_500m, pool.TotalValueUsd);

        // ثبتِ دوباره (retry) سند تکراری نمی‌سازد و حوض را دوباره مصرف نمی‌کند.
        var retry = await adapter.TryPostLegLoadAsync(leg);
        Assert.Equal(PaymentPostingStatus.Duplicate, retry.Status);
        var poolAfterRetry = await GetPoolAsync(db, scope.Company.Id, scope.Product.Id, scope.Terminal.Id);
        Assert.Equal(15m, poolAfterRetry!.QuantityMt);
        Assert.Equal(7_500m, poolAfterRetry.TotalValueUsd);
    }

    internal static async Task<Contract> AddPurchaseContractAsync(
        ApplicationDbContext db,
        PaymentAccountingAdapterTests.PaymentScope scope)
    {
        var contract = new Contract
        {
            ContractNumber = PaymentAccountingAdapterTests.Unique("CN"),
            ContractType = ContractType.Purchase,
            Status = ContractStatus.Active,
            CompanyId = scope.Company.Id,
            ProductId = scope.Product.Id,
            SupplierId = scope.Supplier.Id,
            ContractDate = new DateTime(2026, 7, 1),
            PricingMethod = PricingMethod.ManualFinalPrice,
            QuantityMt = 100m,
            Currency = "USD",
            SettlementCurrencyCode = "USD"
        };
        db.Contracts.Add(contract);
        await db.SaveChangesAsync();
        return contract;
    }

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

    // ---- Multi-company legs ----
    //
    // One physical truck can carry the goods of two internal companies at once — 10 MT on
    // P-016/company A and 20 MT on P-017/company B. The truck is not split, the batch is not
    // split, but the valuation pool is keyed by company, so each owner may give up only its own
    // share. The ledger itself is still single-company — AccountingPostingService refuses any
    // journal that is not the system owner's — so a co-owner's share stays out of the ledger
    // rather than being billed to the owner.

    [Fact]
    public async Task Load_Of_A_Multi_Company_Leg_Consumes_Only_The_Owner_Share()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var second = await AddCompanyWithContractAsync(db, scope);

        // Company A (the system owner): 40 MT at 20,000 → 500 per MT. Company B: 30 MT at 9,000.
        var valuation = new InventoryValuationService(db);
        await valuation.ApplyReceiptAsync(scope.Company.Id, scope.Product.Id, scope.Terminal.Id, 40m, 20_000m);
        await valuation.ApplyReceiptAsync(second.Company.Id, scope.Product.Id, scope.Terminal.Id, 30m, 9_000m);

        var leg = await AddLegAsync(db, scope, quantityMt: 30m);
        await AddAllocationsAsync(db, leg, (scope.Contract.Id, 10m), (second.Contract.Id, 20m));

        var adapter = CreateAdapter(db);
        var result = await adapter.TryPostLegLoadAsync(leg);

        Assert.Equal(PaymentPostingStatus.Posted, result.Status);

        // 10 x 500 — company A's own share, not the whole 30 MT of a truck it only part owns.
        var journalA = await FindJournalAsync(
            db, scope.Company.Id,
            InventoryTransferAccountingAdapter.BuildLegLoadedSourceEventId(leg.Id, scope.Company.Id));
        Assert.NotNull(journalA);
        Assert.Equal(5_000m, journalA!.Lines.Sum(x => x.Debit));
        Assert.All(journalA.Lines, line => Assert.Equal(scope.Contract.Id, line.ContractId));
        Assert.Equal(1, await CountLegJournalsAsync(db, leg.Id));

        var poolA = await GetPoolAsync(db, scope.Company.Id, scope.Product.Id, scope.Terminal.Id);
        Assert.Equal(30m, poolA!.QuantityMt);
        Assert.Equal(15_000m, poolA.TotalValueUsd);

        // Company B's goods are untouched: the owner did not consume them, and B has no journal.
        var poolB = await GetPoolAsync(db, second.Company.Id, scope.Product.Id, scope.Terminal.Id);
        Assert.Equal(30m, poolB!.QuantityMt);
        Assert.Equal(9_000m, poolB.TotalValueUsd);
        Assert.Null(await FindJournalAsync(
            db, second.Company.Id,
            InventoryTransferAccountingAdapter.BuildLegLoadedSourceEventId(leg.Id, second.Company.Id)));

        // Retry posts nothing new and consumes the pool no second time.
        var retry = await adapter.TryPostLegLoadAsync(leg);
        Assert.Equal(PaymentPostingStatus.Duplicate, retry.Status);
        Assert.Equal(1, await CountLegJournalsAsync(db, leg.Id));
        var poolAfterRetry = await GetPoolAsync(db, scope.Company.Id, scope.Product.Id, scope.Terminal.Id);
        Assert.Equal(30m, poolAfterRetry!.QuantityMt);
        Assert.Equal(15_000m, poolAfterRetry.TotalValueUsd);
    }

    // Two contracts of the same company are one owner: A/P-016 = 10 and A/P-018 = 5 post as 15.
    [Fact]
    public async Task Two_Contracts_Of_One_Company_Post_As_A_Single_Company_Slice()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var secondContractSameCompany = await AddPurchaseContractAsync(db, scope);
        var second = await AddCompanyWithContractAsync(db, scope);

        var valuation = new InventoryValuationService(db);
        await valuation.ApplyReceiptAsync(scope.Company.Id, scope.Product.Id, scope.Terminal.Id, 40m, 20_000m);
        await valuation.ApplyReceiptAsync(second.Company.Id, scope.Product.Id, scope.Terminal.Id, 30m, 9_000m);

        var leg = await AddLegAsync(db, scope, quantityMt: 30m);
        await AddAllocationsAsync(
            db, leg,
            (scope.Contract.Id, 10m),
            (secondContractSameCompany.Id, 5m),
            (second.Contract.Id, 15m));

        var result = await CreateAdapter(db).TryPostLegLoadAsync(leg);

        Assert.Equal(PaymentPostingStatus.Posted, result.Status);
        Assert.Equal(1, await CountLegJournalsAsync(db, leg.Id));

        // A = 15 x 500: the two contracts of company A are added up before anything is consumed,
        // rather than posted twice or reduced to whichever one heads the leg.
        var journalA = await FindJournalAsync(
            db, scope.Company.Id,
            InventoryTransferAccountingAdapter.BuildLegLoadedSourceEventId(leg.Id, scope.Company.Id));
        Assert.Equal(7_500m, journalA!.Lines.Sum(x => x.Debit));

        // Company A brought two contracts into the same truck, so the contract dimension on its
        // lines has no single answer and is left empty rather than guessed.
        Assert.All(journalA.Lines, line => Assert.Null(line.ContractId));

        var poolA = await GetPoolAsync(db, scope.Company.Id, scope.Product.Id, scope.Terminal.Id);
        Assert.Equal(25m, poolA!.QuantityMt);
        var poolB = await GetPoolAsync(db, second.Company.Id, scope.Product.Id, scope.Terminal.Id);
        Assert.Equal(30m, poolB!.QuantityMt);
    }

    [Fact]
    public async Task Receipt_Of_A_Multi_Company_Leg_Lands_Only_The_Owner_Share()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var second = await AddCompanyWithContractAsync(db, scope);
        var destination = await AddTerminalAsync(db);

        var valuation = new InventoryValuationService(db);
        await valuation.ApplyReceiptAsync(scope.Company.Id, scope.Product.Id, scope.Terminal.Id, 40m, 20_000m);
        await valuation.ApplyReceiptAsync(second.Company.Id, scope.Product.Id, scope.Terminal.Id, 30m, 9_000m);

        var leg = await AddLegAsync(db, scope, quantityMt: 30m, destinationTerminalId: destination.Id);
        await AddAllocationsAsync(db, leg, (scope.Contract.Id, 10m), (second.Contract.Id, 20m));

        var adapter = CreateAdapter(db);
        await adapter.TryPostLegLoadAsync(leg);

        var receipt = await AddReceiptAsync(db, leg, destination.Id, receivedMt: 30m, shortageMt: 0m);
        var result = await adapter.TryPostReceiptAsync(receipt);

        Assert.Equal(PaymentPostingStatus.Posted, result.Status);

        // 10 of the 30 MT that arrived are company A's, and they land with the cost they left with.
        var destinationA = await GetPoolAsync(db, scope.Company.Id, scope.Product.Id, destination.Id);
        Assert.Equal(10m, destinationA!.QuantityMt);
        Assert.Equal(5_000m, destinationA.TotalValueUsd);
        Assert.Null(await GetPoolAsync(db, second.Company.Id, scope.Product.Id, destination.Id));

        // Nothing of company A's is left on the road.
        var journalA = await FindJournalAsync(
            db, scope.Company.Id,
            InventoryTransferAccountingAdapter.BuildReceiptSourceEventId(receipt.Id, scope.Company.Id));
        Assert.Equal(5_000m, journalA!.Lines.Sum(x => x.Credit));

        // Retry adds nothing.
        var retry = await adapter.TryPostReceiptAsync(receipt);
        Assert.Equal(PaymentPostingStatus.Duplicate, retry.Status);
        Assert.Equal(1, await CountReceiptJournalsAsync(db, receipt.Id));
    }

    // 0.6 MT short on a 10/20 truck is 0.2 MT against A and 0.4 MT against B — the shortage
    // follows the same shares the cargo did, so A is never written down for B's loss.
    [Fact]
    public async Task Shortage_On_A_Multi_Company_Leg_Falls_On_The_Owner_In_Proportion()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var second = await AddCompanyWithContractAsync(db, scope);
        var destination = await AddTerminalAsync(db);

        var valuation = new InventoryValuationService(db);
        await valuation.ApplyReceiptAsync(scope.Company.Id, scope.Product.Id, scope.Terminal.Id, 40m, 20_000m);
        await valuation.ApplyReceiptAsync(second.Company.Id, scope.Product.Id, scope.Terminal.Id, 30m, 9_000m);

        var leg = await AddLegAsync(db, scope, quantityMt: 30m, destinationTerminalId: destination.Id);
        await AddAllocationsAsync(db, leg, (scope.Contract.Id, 10m), (second.Contract.Id, 20m));

        var adapter = CreateAdapter(db);
        await adapter.TryPostLegLoadAsync(leg);

        var receipt = await AddReceiptAsync(db, leg, destination.Id, receivedMt: 29.4m, shortageMt: 0.6m);
        var result = await adapter.TryPostReceiptAsync(receipt);

        Assert.Equal(PaymentPostingStatus.Posted, result.Status);

        // A: 9.8 MT arrive at 500 = 4,900; 0.2 MT written off = 100 — a third of the truck's
        // shortage, because A owned a third of the truck.
        var destinationA = await GetPoolAsync(db, scope.Company.Id, scope.Product.Id, destination.Id);
        Assert.Equal(9.8m, destinationA!.QuantityMt);
        Assert.Equal(4_900m, destinationA.TotalValueUsd);

        var journalA = await FindJournalAsync(
            db, scope.Company.Id,
            InventoryTransferAccountingAdapter.BuildReceiptSourceEventId(receipt.Id, scope.Company.Id));
        Assert.Equal(100m, journalA!.Lines.Single(x => x.AccountId == scope.Settings.InventoryLossAccountId).Debit);
        // Nothing of A's is left on the road.
        Assert.Equal(5_000m, journalA.Lines.Sum(x => x.Credit));
    }

    // A leg whose cargo belongs entirely to a company the ledger cannot accept posts nothing at
    // all — and, the point of this, does not blow up the loading it is attached to.
    [Fact]
    public async Task A_Leg_Owned_By_Another_Company_Is_Skipped_Not_Charged_To_The_Owner()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var second = await AddCompanyWithContractAsync(db, scope);

        var valuation = new InventoryValuationService(db);
        await valuation.ApplyReceiptAsync(scope.Company.Id, scope.Product.Id, scope.Terminal.Id, 40m, 20_000m);
        await valuation.ApplyReceiptAsync(second.Company.Id, scope.Product.Id, scope.Terminal.Id, 30m, 9_000m);

        var leg = await AddLegAsync(db, scope, quantityMt: 20m);
        await AddAllocationsAsync(db, leg, (second.Contract.Id, 20m));

        var result = await CreateAdapter(db).TryPostLegLoadAsync(leg);

        Assert.Equal(PaymentPostingStatus.Skipped, result.Status);
        Assert.Equal("COMPANY_NOT_OWNER", result.Reason);
        Assert.Equal(0, await CountLegJournalsAsync(db, leg.Id));
        Assert.Equal(40m, (await GetPoolAsync(db, scope.Company.Id, scope.Product.Id, scope.Terminal.Id))!.QuantityMt);
        Assert.Equal(30m, (await GetPoolAsync(db, second.Company.Id, scope.Product.Id, scope.Terminal.Id))!.QuantityMt);
    }

    // A posting that fails must leave the pool exactly as it was, and the journals go back with
    // the caller's transaction: one leg, one atomic outcome, however many owners it has.
    [Fact]
    public async Task A_Failed_Posting_Leaves_Neither_Journal_Nor_Consumed_Pool()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var second = await AddCompanyWithContractAsync(db, scope);

        var valuation = new InventoryValuationService(db);
        await valuation.ApplyReceiptAsync(scope.Company.Id, scope.Product.Id, scope.Terminal.Id, 40m, 20_000m);
        await valuation.ApplyReceiptAsync(second.Company.Id, scope.Product.Id, scope.Terminal.Id, 30m, 9_000m);

        var leg = await AddLegAsync(db, scope, quantityMt: 30m);
        await AddAllocationsAsync(db, leg, (scope.Contract.Id, 10m), (second.Contract.Id, 20m));

        // Closing every period of the owner makes its posting fail after its pool was consumed.
        await db.Database.ExecuteSqlRawAsync(
            "UPDATE \"FiscalPeriods\" SET \"Status\" = {0} WHERE \"CompanyId\" = {1}",
            (int)FiscalPeriodStatus.Closed, scope.Company.Id);

        await using var transaction = await db.Database.BeginTransactionAsync();
        await Assert.ThrowsAnyAsync<Exception>(() => CreateAdapter(db).TryPostLegLoadAsync(leg));
        await transaction.RollbackAsync();

        Assert.Equal(0, await CountLegJournalsAsync(db, leg.Id));
        var poolA = await GetPoolAsync(db, scope.Company.Id, scope.Product.Id, scope.Terminal.Id);
        Assert.Equal(40m, poolA!.QuantityMt);
        Assert.Equal(20_000m, poolA.TotalValueUsd);
        var poolB = await GetPoolAsync(db, second.Company.Id, scope.Product.Id, scope.Terminal.Id);
        Assert.Equal(30m, poolB!.QuantityMt);
        Assert.Equal(9_000m, poolB.TotalValueUsd);
    }

    [Fact]
    public async Task A_Multi_Company_Leg_Does_Nothing_While_The_Pilot_Is_Off()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var second = await AddCompanyWithContractAsync(db, scope);

        var valuation = new InventoryValuationService(db);
        await valuation.ApplyReceiptAsync(scope.Company.Id, scope.Product.Id, scope.Terminal.Id, 40m, 20_000m);
        await valuation.ApplyReceiptAsync(second.Company.Id, scope.Product.Id, scope.Terminal.Id, 30m, 9_000m);

        var leg = await AddLegAsync(db, scope, quantityMt: 30m);
        await AddAllocationsAsync(db, leg, (scope.Contract.Id, 10m), (second.Contract.Id, 20m));

        var adapter = new InventoryTransferAccountingAdapter(
            db,
            CreatePostingService(db),
            new AccountingJournalNumberGenerator(),
            new InventoryValuationService(db),
            Options.Create(new AccountingOptions { Enabled = true, DefaultFunctionalCurrencyCode = "USD" }),
            NullLogger<InventoryTransferAccountingAdapter>.Instance);

        var result = await adapter.TryPostLegLoadAsync(leg);

        Assert.Equal(PaymentPostingStatus.Skipped, result.Status);
        Assert.Equal("PILOT_DISABLED", result.Reason);
        Assert.Equal(0, await CountLegJournalsAsync(db, leg.Id));
        Assert.Equal(40m, (await GetPoolAsync(db, scope.Company.Id, scope.Product.Id, scope.Terminal.Id))!.QuantityMt);
        Assert.Equal(30m, (await GetPoolAsync(db, second.Company.Id, scope.Product.Id, scope.Terminal.Id))!.QuantityMt);
    }

    private static async Task AddAllocationsAsync(
        ApplicationDbContext db,
        InventoryTransportLeg leg,
        params (int ContractId, decimal QuantityMt)[] allocations)
    {
        db.InventoryTransportLegAllocations.AddRange(allocations.Select(a => new InventoryTransportLegAllocation
        {
            InventoryTransportLegId = leg.Id,
            SourcePurchaseContractId = a.ContractId,
            QuantityMt = a.QuantityMt
        }));
        await db.SaveChangesAsync();
    }

    private static Task<JournalEntry?> FindJournalAsync(
        ApplicationDbContext db,
        int companyId,
        string sourceEventId)
        => db.JournalEntries
            .AsNoTracking()
            .Include(x => x.Lines)
            .SingleOrDefaultAsync(x => x.CompanyId == companyId
                && x.SourceModule == InventoryTransferAccountingAdapter.SourceModule
                && x.SourceEventId == sourceEventId);

    private static Task<int> CountLegJournalsAsync(ApplicationDbContext db, int legId)
        => db.JournalEntries
            .AsNoTracking()
            .CountAsync(x => x.SourceModule == InventoryTransferAccountingAdapter.SourceModule
                && x.SourceEntityType == InventoryTransferAccountingAdapter.LegEntityType
                && x.SourceEntityId == legId
                && !x.IsReversal);

    private static Task<int> CountReceiptJournalsAsync(ApplicationDbContext db, int receiptId)
        => db.JournalEntries
            .AsNoTracking()
            .CountAsync(x => x.SourceModule == InventoryTransferAccountingAdapter.SourceModule
                && x.SourceEntityType == InventoryTransferAccountingAdapter.ReceiptEntityType
                && x.SourceEntityId == receiptId
                && !x.IsReversal);

    /// <summary>
    /// A second internal company, with its own chart, settings and purchase contract, so that one
    /// leg can genuinely be owned by two companies at once.
    /// </summary>
    internal static async Task<SecondCompany> AddCompanyWithContractAsync(
        ApplicationDbContext db,
        PaymentAccountingAdapterTests.PaymentScope scope)
    {
        var company = new Company
        {
            Code = PaymentAccountingAdapterTests.Unique("C2"),
            Name = PaymentAccountingAdapterTests.Unique("Company2"),
            Country = "AF",
            IsActive = true
        };
        db.Companies.Add(company);
        await db.SaveChangesAsync();

        await new AccountingChartSeeder(
            db,
            Options.Create(new AccountingOptions { DefaultFunctionalCurrencyCode = "USD" })).SeedAsync();
        var settings = await db.AccountingSettings.AsNoTracking().SingleAsync(x => x.CompanyId == company.Id);

        var year = new FiscalYear
        {
            CompanyId = company.Id,
            Name = PaymentAccountingAdapterTests.Unique("FY"),
            StartDate = new DateTime(2026, 1, 1),
            EndDate = new DateTime(2026, 12, 31),
            Status = FiscalYearStatus.Open,
            IsCurrent = true
        };
        db.FiscalYears.Add(year);
        await db.SaveChangesAsync();

        db.FiscalPeriods.AddRange(Enumerable.Range(1, 12).Select(month => new FiscalPeriod
        {
            CompanyId = company.Id,
            FiscalYearId = year.Id,
            PeriodNumber = month,
            Name = $"P{month}-{year.Id}",
            StartDate = new DateTime(2026, month, 1),
            EndDate = new DateTime(2026, month, DateTime.DaysInMonth(2026, month)),
            Status = FiscalPeriodStatus.Open
        }));
        await db.SaveChangesAsync();

        var contract = new Contract
        {
            ContractNumber = PaymentAccountingAdapterTests.Unique("CN2"),
            ContractType = ContractType.Purchase,
            Status = ContractStatus.Active,
            CompanyId = company.Id,
            ProductId = scope.Product.Id,
            SupplierId = scope.Supplier.Id,
            ContractDate = new DateTime(2026, 7, 1),
            PricingMethod = PricingMethod.ManualFinalPrice,
            QuantityMt = 100m,
            Currency = "USD",
            SettlementCurrencyCode = "USD"
        };
        db.Contracts.Add(contract);
        await db.SaveChangesAsync();

        return new SecondCompany(company, contract, settings);
    }

    internal sealed record SecondCompany(Company Company, Contract Contract, AccountingSettings Settings);
}
