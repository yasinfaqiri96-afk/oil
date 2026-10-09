using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PTGOilSystem.Web.Configuration;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Accounting;
using Xunit;

namespace PTGOilSystem.Web.Tests;

[Collection(AccountingPostgreSqlCollection.CollectionName)]
[Trait("Category", "PostgreSql")]
[Trait("Category", "Integration")]
public sealed class AccountingBackfillSafetyTests(AccountingPostgreSqlFixture fixture)
{
    private static readonly DateTime EventDate = new(2026, 7, 5);

    [Fact]
    public async Task Cancelled_Purchases_Are_Skipped_By_Single_And_Batch_Adapters()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var first = await AddLoadingAsync(db, scope, cancelled: true);
        var second = await AddLoadingAsync(db, scope, cancelled: true);
        var adapter = CreatePurchaseAdapter(db);
        Assert.Equal("LOADING_CANCELLED", (await adapter.TryPostPurchaseAsync(first)).Reason);
        var results = await adapter.TryPostPurchasesAsync([first, second]);
        Assert.All(results, result =>
        {
            Assert.Equal(PaymentPostingStatus.Skipped, result.Status);
            Assert.Equal("LOADING_CANCELLED", result.Reason);
        });
        Assert.False(await db.JournalEntries.AnyAsync(x => x.SourceModule == PurchaseAccountingAdapter.SourceModule
            && x.SourceEntityType == nameof(LoadingRegister)
            && (x.SourceEntityId == first.Id || x.SourceEntityId == second.Id)));
    }

    [Theory]
    [InlineData(true, LoadingReceiptDestination.ToInventory, "RECEIPT_CANCELLED")]
    [InlineData(false, LoadingReceiptDestination.DirectDispatch, "RECEIPT_DESTINATION_NOT_INVENTORY")]
    [InlineData(false, LoadingReceiptDestination.Mixed, "RECEIPT_DESTINATION_NOT_INVENTORY")]
    public async Task Cancelled_And_Noninventory_Receipts_Never_Fill_The_Valuation_Pool(
        bool cancelled, LoadingReceiptDestination destination, string expectedReason)
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var loading = await AddLoadingAsync(db, scope);
        var adapter = CreatePurchaseAdapter(db);
        await adapter.TryPostPurchaseAsync(loading);
        var receipt = await AddReceiptAsync(db, scope, loading, cancelled, destination);
        var result = await adapter.TryPostInventoryReceiptAsync(receipt);
        Assert.Equal(PaymentPostingStatus.Skipped, result.Status);
        Assert.Equal(expectedReason, result.Reason);
        Assert.False(await db.JournalEntries.AnyAsync(x =>
            x.SourceEventId == PurchaseAccountingAdapter.BuildReceiptSourceEventId(receipt.Id)));
        Assert.False(await db.InventoryAverageCosts.AnyAsync(x => x.CompanyId == scope.Company.Id));
    }

    [Fact]
    public async Task DryRun_Distinguishes_Cancelled_Posted_And_Missing_Without_Claiming_Eligibility()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var missing = await AddLoadingAsync(db, scope);
        var cancelled = await AddLoadingAsync(db, scope, cancelled: true);
        var posted = await AddLoadingAsync(db, scope);
        await CreatePurchaseAdapter(db).TryPostPurchaseAsync(posted);
        var cancelledReceipt = await AddReceiptAsync(db, scope, posted, cancelled: true);
        var journalsBefore = await db.JournalEntries.CountAsync();
        var valuationBefore = await db.InventoryAverageCosts.CountAsync();
        var report = await CreateBackfill(db).RunAsync(dryRun: true);
        Assert.True(report.Succeeded, string.Join(" | ", report.Errors));
        Assert.False(report.ChartSeeded);
        Assert.All(report.Steps, step => Assert.Equal(0, step.Posted));
        var purchases = report.Steps.Single(x => x.Name == "Purchase (LoadingRegister)");
        var missingItem = purchases.Items.Single(x => x.EntityId == missing.Id);
        Assert.Equal(AccountingBackfillItemStatus.NeedsReview, missingItem.Status);
        Assert.True(missingItem.MissingJournal);
        Assert.Equal(AccountingBackfillItemStatus.Cancelled,
            purchases.Items.Single(x => x.EntityId == cancelled.Id).Status);
        Assert.Equal(AccountingBackfillItemStatus.AlreadyPosted,
            purchases.Items.Single(x => x.EntityId == posted.Id).Status);
        Assert.Equal(AccountingBackfillItemStatus.Cancelled,
            report.Steps.Single(x => x.Name == "InventoryReceipt (LoadingReceipt)")
                .Items.Single(x => x.EntityId == cancelledReceipt.Id).Status);
        Assert.Equal(journalsBefore, await db.JournalEntries.CountAsync());
        Assert.Equal(valuationBefore, await db.InventoryAverageCosts.CountAsync());
    }

    [Fact]
    public async Task Backfill_Does_Not_Revive_Cancelled_Loadings_Or_Receipts()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var loading = await AddLoadingAsync(db, scope, cancelled: true);
        var receipt = await AddReceiptAsync(db, scope, loading, cancelled: true);
        var report = await CreateBackfill(db).RunAsync(dryRun: false);
        Assert.True(report.Succeeded, string.Join(" | ", report.Errors));
        Assert.Equal(AccountingBackfillItemStatus.Cancelled,
            report.Steps.Single(x => x.Name == "Purchase (LoadingRegister)")
                .Items.Single(x => x.EntityId == loading.Id).Status);
        Assert.Equal(AccountingBackfillItemStatus.Cancelled,
            report.Steps.Single(x => x.Name == "InventoryReceipt (LoadingReceipt)")
                .Items.Single(x => x.EntityId == receipt.Id).Status);
        Assert.False(await db.JournalEntries.AnyAsync(x =>
            x.SourceEventId == PurchaseAccountingAdapter.BuildCreatedSourceEventId(loading.Id, 0)
            || x.SourceEventId == PurchaseAccountingAdapter.BuildReceiptSourceEventId(receipt.Id)));
        Assert.False(await db.InventoryAverageCosts.AnyAsync(x => x.CompanyId == scope.Company.Id));
    }

    [Fact]
    public async Task Backfill_Preserves_Posted_Revisions_And_Reports_Reversed_Sources()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var historical = await AddLoadingAsync(db, scope);
        var revised = await AddLoadingAsync(db, scope);
        var reversed = await AddLoadingAsync(db, scope);
        var adapter = CreatePurchaseAdapter(db);
        await adapter.TryPostPurchaseAsync(historical);
        await adapter.TryPostPurchaseAsync(revised);
        revised.LoadingPriceUsd = 600m;
        await db.SaveChangesAsync();
        await adapter.TryPostPurchaseAsync(revised);
        await adapter.TryPostPurchaseAsync(reversed);
        await adapter.TryPostPurchaseReversalAsync(reversed);
        historical.LoadingPriceUsd = 700m;
        await db.SaveChangesAsync();
        var receipt = await AddReceiptAsync(db, scope, historical);
        var purchaseJournalsBefore = await db.JournalEntries
            .Where(x => x.SourceEntityType == nameof(LoadingRegister)
                && (x.SourceEntityId == historical.Id || x.SourceEntityId == revised.Id || x.SourceEntityId == reversed.Id))
            .Select(x => x.Id).OrderBy(x => x).ToListAsync();
        var report = await CreateBackfill(db).RunAsync(dryRun: false);
        Assert.True(report.Succeeded, string.Join(" | ", report.Errors));
        var purchases = report.Steps.Single(x => x.Name == "Purchase (LoadingRegister)");
        Assert.Equal(AccountingBackfillItemStatus.AlreadyPosted,
            purchases.Items.Single(x => x.EntityId == historical.Id).Status);
        Assert.Equal(AccountingBackfillItemStatus.AlreadyPosted,
            purchases.Items.Single(x => x.EntityId == revised.Id).Status);
        Assert.Equal("SOURCE_JOURNAL_REVERSED", purchases.Items.Single(x => x.EntityId == reversed.Id).Reason);
        Assert.Equal("PURCHASE_PRICE_CHANGED_NEEDS_REVIEW",
            report.Steps.Single(x => x.Name == "InventoryReceipt (LoadingReceipt)")
                .Items.Single(x => x.EntityId == receipt.Id).Reason);
        Assert.Equal(purchaseJournalsBefore, await db.JournalEntries
            .Where(x => x.SourceEntityType == nameof(LoadingRegister)
                && (x.SourceEntityId == historical.Id || x.SourceEntityId == revised.Id || x.SourceEntityId == reversed.Id))
            .Select(x => x.Id).OrderBy(x => x).ToListAsync());
        Assert.False(await db.InventoryAverageCosts.AnyAsync(x => x.CompanyId == scope.Company.Id));
    }

    [Fact]
    public async Task Reversed_Purchase_Cannot_Be_Reposted_Or_Used_To_Value_A_Receipt()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var loading = await AddLoadingAsync(db, scope);
        var adapter = CreatePurchaseAdapter(db);
        await adapter.TryPostPurchaseAsync(loading);
        await adapter.TryPostPurchaseReversalAsync(loading);
        loading.LoadingPriceUsd = 600m;
        await db.SaveChangesAsync();
        Assert.Equal("PURCHASE_ALREADY_REVERSED", (await adapter.TryPostPurchaseAsync(loading)).Reason);
        var receipt = await AddReceiptAsync(db, scope, loading);
        Assert.Equal("PURCHASE_NOT_POSTED", (await adapter.TryPostInventoryReceiptAsync(receipt)).Reason);
        Assert.False(await db.InventoryAverageCosts.AnyAsync(x => x.CompanyId == scope.Company.Id));
    }

    [Fact]
    public async Task Pending_Price_And_Closed_Period_Are_Explicit_Skips_Without_Journals()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var pending = await AddLoadingAsync(db, scope, price: null);
        var closed = await AddLoadingAsync(db, scope);
        scope.Period.Status = FiscalPeriodStatus.HardLocked;
        await db.SaveChangesAsync();
        var report = await CreateBackfill(db).RunAsync(dryRun: false);
        Assert.True(report.Succeeded, string.Join(" | ", report.Errors));
        var purchases = report.Steps.Single(x => x.Name == "Purchase (LoadingRegister)");
        Assert.Equal("PURCHASE_PRICE_PENDING", purchases.Items.Single(x => x.EntityId == pending.Id).Reason);
        var closedItem = purchases.Items.Single(x => x.EntityId == closed.Id);
        Assert.Equal(AccountingBackfillItemStatus.Skipped, closedItem.Status);
        Assert.Contains("LOCK", closedItem.Reason!, StringComparison.OrdinalIgnoreCase);
        Assert.False(await db.JournalEntries.AnyAsync(x => x.SourceEntityType == nameof(LoadingRegister)
            && (x.SourceEntityId == pending.Id || x.SourceEntityId == closed.Id)));
    }

    [Fact]
    public async Task Reversed_Receipt_Remains_Reversed_In_Adapter_And_Backfill()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var loading = await AddLoadingAsync(db, scope);
        var receipt = await AddReceiptAsync(db, scope, loading);
        var adapter = CreatePurchaseAdapter(db);
        await adapter.TryPostPurchaseAsync(loading);
        await adapter.TryPostInventoryReceiptAsync(receipt);
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            Assert.Equal(PaymentPostingStatus.Posted,
                (await adapter.TryPostInventoryReceiptReversalAsync(receipt)).Status);
            await transaction.CommitAsync();
        }
        var poolBefore = await db.InventoryAverageCosts.AsNoTracking()
            .SingleAsync(x => x.CompanyId == scope.Company.Id);
        Assert.Equal(0m, poolBefore.QuantityMt);
        Assert.Equal(0m, poolBefore.TotalValueUsd);
        Assert.Equal("RECEIPT_ALREADY_REVERSED", (await adapter.TryPostInventoryReceiptAsync(receipt)).Reason);
        var report = await CreateBackfill(db).RunAsync(dryRun: false);
        Assert.Equal("SOURCE_JOURNAL_REVERSED",
            report.Steps.Single(x => x.Name == "InventoryReceipt (LoadingReceipt)")
                .Items.Single(x => x.EntityId == receipt.Id).Reason);
        var poolAfter = await db.InventoryAverageCosts.AsNoTracking()
            .SingleAsync(x => x.CompanyId == scope.Company.Id);
        Assert.Equal(poolBefore.QuantityMt, poolAfter.QuantityMt);
        Assert.Equal(poolBefore.TotalValueUsd, poolAfter.TotalValueUsd);
        Assert.Equal(2, await db.JournalEntries.CountAsync(x => x.SourceModule == PurchaseAccountingAdapter.SourceModule
            && (x.SourceEventId == PurchaseAccountingAdapter.BuildReceiptSourceEventId(receipt.Id)
                || x.SourceEventId == PurchaseAccountingAdapter.BuildReceiptReversedSourceEventId(receipt.Id))));
    }

    [Fact]
    public async Task Cancelled_Transport_Receipt_Is_Reported_Without_Valuation_Or_Journal()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var loading = await AddLoadingAsync(db, scope);
        var adapter = CreatePurchaseAdapter(db);
        await adapter.TryPostPurchaseAsync(loading);
        var leg = new InventoryTransportLeg
        {
            SourcePurchaseContractId = scope.Contract.Id, ProductId = scope.Product.Id,
            DestinationTerminalId = scope.Terminal.Id, DestinationStorageTankId = scope.Tank.Id,
            TransportType = LoadingTransportType.Truck, LoadedDate = EventDate,
            QuantityMt = 20m, PurchaseUnitCostUsd = 500m, Status = InventoryTransportLegStatus.Loaded
        };
        db.InventoryTransportLegs.Add(leg);
        await db.SaveChangesAsync();
        db.InventoryTransportLegAllocations.Add(new InventoryTransportLegAllocation
        {
            InventoryTransportLegId = leg.Id, SourcePurchaseContractId = scope.Contract.Id,
            SourceLoadingRegisterId = loading.Id, QuantityMt = 20m
        });
        var receipt = new InventoryTransportReceipt
        {
            InventoryTransportLegId = leg.Id, ReceiptDate = EventDate.AddDays(2), ReceivedQuantityMt = 18m,
            ReceiptDestination = InventoryTransportReceiptDestination.ToInventory,
            DestinationTerminalId = scope.Terminal.Id, DestinationStorageTankId = scope.Tank.Id,
            IsCancelled = true
        };
        db.InventoryTransportReceipts.Add(receipt);
        await db.SaveChangesAsync();
        Assert.Equal("RECEIPT_CANCELLED", (await adapter.TryPostTransportReceiptAsync(receipt)).Reason);
        var report = await CreateBackfill(db).RunAsync(dryRun: false);
        Assert.Equal(AccountingBackfillItemStatus.Cancelled,
            report.Steps.Single(x => x.Name == "InventoryReceipt (InventoryTransportReceipt)")
                .Items.Single(x => x.EntityId == receipt.Id).Status);
        Assert.False(await db.JournalEntries.AnyAsync(x =>
            x.SourceEventId == PurchaseAccountingAdapter.BuildTransportReceiptSourceEventId(receipt.Id)));
        Assert.False(await db.InventoryAverageCosts.AnyAsync(x => x.CompanyId == scope.Company.Id));
    }

    [Fact]
    public async Task Historical_Document_In_Closed_Fiscal_Year_Is_Not_Backfilled()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var loading = await AddLoadingAsync(db, scope);
        var year = await db.FiscalYears.SingleAsync(x => x.CompanyId == scope.Company.Id);
        year.Status = FiscalYearStatus.Closed;
        await db.SaveChangesAsync();
        var report = await CreateBackfill(db).RunAsync(dryRun: false);
        var item = report.Steps.Single(x => x.Name == "Purchase (LoadingRegister)")
            .Items.Single(x => x.EntityId == loading.Id);
        Assert.Equal(AccountingBackfillItemStatus.Skipped, item.Status);
        Assert.Equal("FISCAL_YEAR_CLOSED", item.Reason);
        Assert.False(await db.JournalEntries.AnyAsync(x =>
            x.SourceEventId == PurchaseAccountingAdapter.BuildCreatedSourceEventId(loading.Id, 0)));
    }

    private static async Task<LoadingRegister> AddLoadingAsync(ApplicationDbContext db,
        PaymentAccountingAdapterTests.PaymentScope scope, bool cancelled = false, decimal? price = 500m)
    {
        var loading = new LoadingRegister
        {
            ContractId = scope.Contract.Id, ProductId = scope.Product.Id,
            TransportType = LoadingTransportType.Truck, LoadingDate = EventDate,
            LoadedQuantityMt = 20m, LoadingPriceUsd = price, IsCancelled = cancelled
        };
        db.LoadingRegisters.Add(loading);
        await db.SaveChangesAsync();
        return loading;
    }

    private static async Task<LoadingReceipt> AddReceiptAsync(ApplicationDbContext db,
        PaymentAccountingAdapterTests.PaymentScope scope, LoadingRegister loading, bool cancelled = false,
        LoadingReceiptDestination destination = LoadingReceiptDestination.ToInventory)
    {
        var receipt = new LoadingReceipt
        {
            LoadingRegisterId = loading.Id, ReceiptDestination = destination,
            TerminalId = scope.Terminal.Id, StorageTankId = scope.Tank.Id,
            ReceiptDate = EventDate.AddDays(2), ReceivedQuantityMt = 18m, IsCancelled = cancelled
        };
        db.LoadingReceipts.Add(receipt);
        await db.SaveChangesAsync();
        return receipt;
    }

    private static IOptions<AccountingOptions> OptionsForTests() => Options.Create(new AccountingOptions
    {
        Enabled = true,
        Pilots = new AccountingPilotOptions { Purchase = true, InventoryReceipt = true, Sale = true, Cogs = true }
    });

    private static PurchaseAccountingAdapter CreatePurchaseAdapter(ApplicationDbContext db)
    {
        var options = OptionsForTests();
        return new PurchaseAccountingAdapter(db,
            new AccountingPostingService(db, new PeriodGuard(db, new FiscalCalendarService(db)), options,
                new SystemCompanyProvider(db)), new AccountingJournalNumberGenerator(), new PricingService(db),
            new InventoryValuationService(db), options, NullLogger<PurchaseAccountingAdapter>.Instance);
    }

    private static AccountingBackfillService CreateBackfill(ApplicationDbContext db)
    {
        var options = OptionsForTests();
        var posting = new AccountingPostingService(db, new PeriodGuard(db, new FiscalCalendarService(db)), options,
            new SystemCompanyProvider(db));
        var numbers = new AccountingJournalNumberGenerator();
        var valuation = new InventoryValuationService(db);
        var expense = new ExpenseAccountingAdapter(db, posting, numbers, options,
            NullLogger<ExpenseAccountingAdapter>.Instance);
        return new AccountingBackfillService(db, new AccountingChartSeeder(db, options),
            CreatePurchaseAdapter(db), new SalesAccountingAdapter(db, posting, numbers, valuation, options,
                NullLogger<SalesAccountingAdapter>.Instance), expense,
            new PaymentAccountingAdapter(db, posting, numbers, new PaymentCompanyResolver(db), expense, options,
                NullLogger<PaymentAccountingAdapter>.Instance),
            new PartnershipProfitAllocationAdapter(db, posting, numbers,
                new PTGOilSystem.Web.Services.PartyStatements.PartnershipStatementService(db), options,
                NullLogger<PartnershipProfitAllocationAdapter>.Instance), options,
            NullLogger<AccountingBackfillService>.Instance);
    }
}
