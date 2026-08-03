using System.Diagnostics;
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

/// <summary>
/// Stage 6 — purchase and inventory. A purchase must value the supplier claim with exactly the
/// arithmetic the legacy aggregation already uses, and repricing must never edit a posted
/// journal: it reverses and reposts.
/// </summary>
[Collection(AccountingPostgreSqlCollection.CollectionName)]
[Trait("Category", "PostgreSql")]
[Trait("Category", "Integration")]
public sealed class PurchaseAccountingAdapterTests(AccountingPostgreSqlFixture fixture)
{
    private static readonly DateTime LoadingDate = new(2026, 7, 5);

    [Fact]
    public void SourceEventId_Formats_Are_Stable()
    {
        Assert.Equal("Purchase:7:Created:0", PurchaseAccountingAdapter.BuildCreatedSourceEventId(7, 0));
        Assert.Equal("Purchase:7:Reversed:1", PurchaseAccountingAdapter.BuildReversedSourceEventId(7, 1));
        Assert.Equal("InventoryReceipt:9:Created", PurchaseAccountingAdapter.BuildReceiptSourceEventId(9));
    }

    [Fact]
    public async Task Purchase_Debits_In_Transit_And_Credits_The_Supplier_Payable()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var loading = await AddLoadingAsync(db, scope, quantityMt: 20m, priceUsd: 500m);

        var result = await CreateAdapter(db, purchase: true).TryPostPurchaseAsync(loading);

        Assert.Equal(PaymentPostingStatus.Posted, result.Status);

        var journal = await LoadPurchaseJournalAsync(db, loading.Id, revision: 0);
        var debitLine = journal.Lines.Single(x => x.Debit > 0m);
        var creditLine = journal.Lines.Single(x => x.Credit > 0m);

        // 20 MT x 500 USD, the same product the aggregation rounds to 4 places.
        Assert.Equal(10_000m, debitLine.Debit);
        Assert.Equal(10_000m, creditLine.Credit);
        Assert.Equal(scope.Settings.InventoryInTransitAccountId, debitLine.AccountId);
        Assert.Equal(scope.Settings.AccountsPayableAccountId, creditLine.AccountId);
        Assert.Equal(AccountingPartyType.Supplier, creditLine.PartyType);
        Assert.Equal(scope.Supplier.Id, creditLine.PartyId);
        Assert.Equal(scope.Contract.Id, debitLine.ContractId);
        Assert.Equal(scope.Product.Id, debitLine.ProductId);
        Assert.Equal(JournalEntryStatus.Posted, journal.Status);
    }

    [Fact]
    public async Task Purchase_Batch_Posts_Each_Loading_Exactly_Once()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var loadings = Enumerable.Range(1, 20)
            .Select(index => new LoadingRegister
            {
                ContractId = scope.Contract.Id,
                ProductId = scope.Product.Id,
                TransportType = LoadingTransportType.Wagon,
                LoadingDate = LoadingDate,
                LoadedQuantityMt = 10m + index,
                LoadingPriceUsd = 500m,
                SettlementCurrencyCode = "USD",
                WagonNumber = $"BATCH-{index:000}"
            })
            .ToList();
        db.LoadingRegisters.AddRange(loadings);
        await db.SaveChangesAsync();

        var adapter = CreateAdapter(db, purchase: true);
        var stopwatch = Stopwatch.StartNew();
        var results = await adapter.TryPostPurchasesAsync(loadings);
        stopwatch.Stop();
        Console.WriteLine($"PURCHASE_BATCH_ELAPSED_MS={stopwatch.ElapsedMilliseconds}");

        Assert.All(results, result => Assert.Equal(PaymentPostingStatus.Posted, result.Status));

        // دیتابیسِ این مجموعه بین تست‌ها مشترک است، پس شمارش باید به همین بارگیری‌ها محدود بماند.
        var loadingIds = loadings.Select(loading => loading.Id).ToList();
        Assert.Equal(
            loadings.Count,
            await db.JournalEntries.CountAsync(journal =>
                journal.SourceModule == PurchaseAccountingAdapter.SourceModule
                && journal.SourceEntityType == PurchaseAccountingAdapter.PurchaseSourceEntityType
                && journal.SourceEntityId.HasValue
                && loadingIds.Contains(journal.SourceEntityId.Value)
                && !journal.IsReversal));
        Assert.Equal(
            loadings.Count * 2,
            await db.JournalEntryLines.CountAsync(line =>
                line.JournalEntry!.SourceModule == PurchaseAccountingAdapter.SourceModule
                && line.JournalEntry.SourceEntityType == PurchaseAccountingAdapter.PurchaseSourceEntityType
                && line.JournalEntry.SourceEntityId.HasValue
                && loadingIds.Contains(line.JournalEntry.SourceEntityId.Value)));
    }

    [Fact]
    public async Task Purchase_Falls_Back_To_The_Contract_Price_When_The_Loading_Has_None()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);

        scope.Contract.ManualFinalPriceUsd = 250m;
        await db.SaveChangesAsync();

        var loading = await AddLoadingAsync(db, scope, quantityMt: 4m, priceUsd: null);

        Assert.Equal(PaymentPostingStatus.Posted,
            (await CreateAdapter(db, purchase: true).TryPostPurchaseAsync(loading)).Status);

        var journal = await LoadPurchaseJournalAsync(db, loading.Id, revision: 0);
        Assert.Equal(1_000m, journal.Lines.Single(x => x.Debit > 0m).Debit);
    }

    [Fact]
    public async Task Purchase_Stays_Unposted_While_The_Price_Is_Pending()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);

        // Neither the loading nor the contract carries a usable price.
        scope.Contract.ManualFinalPriceUsd = null;
        await db.SaveChangesAsync();
        var loading = await AddLoadingAsync(db, scope, quantityMt: 10m, priceUsd: null);

        var result = await CreateAdapter(db, purchase: true).TryPostPurchaseAsync(loading);

        Assert.Equal(PaymentPostingStatus.Skipped, result.Status);
        Assert.Equal("PURCHASE_PRICE_PENDING", result.Reason);
        Assert.Equal(0, await CountPurchaseJournalsAsync(db, loading.Id));
    }

    [Fact]
    public async Task Reposting_An_Unchanged_Purchase_Does_Nothing()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var loading = await AddLoadingAsync(db, scope, quantityMt: 20m, priceUsd: 500m);
        var adapter = CreateAdapter(db, purchase: true);

        Assert.Equal(PaymentPostingStatus.Posted, (await adapter.TryPostPurchaseAsync(loading)).Status);
        var second = await adapter.TryPostPurchaseAsync(loading);

        Assert.Equal(PaymentPostingStatus.Duplicate, second.Status);
        Assert.Equal("DUPLICATE_SOURCE_EVENT", second.Reason);
        Assert.Equal(1, await CountPurchaseJournalsAsync(db, loading.Id));
    }

    [Fact]
    public async Task Repricing_Reverses_The_Old_Revision_And_Posts_A_New_One()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var loading = await AddLoadingAsync(db, scope, quantityMt: 20m, priceUsd: 500m);
        var adapter = CreateAdapter(db, purchase: true);

        Assert.Equal(PaymentPostingStatus.Posted, (await adapter.TryPostPurchaseAsync(loading)).Status);

        loading.LoadingPriceUsd = 600m;
        await db.SaveChangesAsync();
        Assert.Equal(PaymentPostingStatus.Posted, (await adapter.TryPostPurchaseAsync(loading)).Status);

        // The original stays posted and untouched; a reversal and a new revision join it.
        var original = await LoadPurchaseJournalAsync(db, loading.Id, revision: 0);
        Assert.Equal(JournalEntryStatus.Posted, original.Status);
        Assert.Equal(10_000m, original.Lines.Sum(x => x.Debit));

        var reversal = await db.JournalEntries
            .AsNoTracking()
            .Include(x => x.Lines)
            .SingleAsync(x => x.SourceEventId == PurchaseAccountingAdapter.BuildReversedSourceEventId(loading.Id, 0));
        Assert.True(reversal.IsReversal);
        Assert.Equal(original.Id, reversal.ReversalOfJournalEntryId);
        Assert.Equal(10_000m, reversal.Lines.Sum(x => x.Debit));

        var revised = await LoadPurchaseJournalAsync(db, loading.Id, revision: 1);
        Assert.Equal(12_000m, revised.Lines.Sum(x => x.Debit));

        // Net effect across all three: only the new price stands.
        var netDebit = await db.JournalEntryLines
            .AsNoTracking()
            .Where(x => x.AccountId == scope.Settings.InventoryInTransitAccountId)
            .SumAsync(x => x.Debit - x.Credit);
        Assert.Equal(12_000m, netDebit);
    }

    [Fact]
    public async Task Receipt_Moves_Goods_From_In_Transit_Into_Inventory_At_The_Loading_Cost()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var loading = await AddLoadingAsync(db, scope, quantityMt: 20m, priceUsd: 500m);
        var adapter = CreateAdapter(db, purchase: true, inventoryReceipt: true);
        Assert.Equal(PaymentPostingStatus.Posted, (await adapter.TryPostPurchaseAsync(loading)).Status);

        // Only 18 of the 20 loaded MT arrived; the 2 MT difference is a loss for stage 8.
        var receipt = await AddReceiptAsync(db, scope, loading, receivedMt: 18m);
        var result = await adapter.TryPostInventoryReceiptAsync(receipt);

        Assert.Equal(PaymentPostingStatus.Posted, result.Status);

        var journal = await LoadReceiptJournalAsync(db, receipt.Id);
        var debitLine = journal.Lines.Single(x => x.Debit > 0m);
        var creditLine = journal.Lines.Single(x => x.Credit > 0m);

        Assert.Equal(9_000m, debitLine.Debit);
        Assert.Equal(scope.Settings.InventoryAccountId, debitLine.AccountId);
        Assert.Equal(scope.Settings.InventoryInTransitAccountId, creditLine.AccountId);
        Assert.Equal(scope.Tank.Id, debitLine.TankId);

        // 10,000 went into transit, 9,000 came out: the shortfall stays in transit for stage 8.
        var inTransitBalance = await db.JournalEntryLines
            .AsNoTracking()
            .Where(x => x.AccountId == scope.Settings.InventoryInTransitAccountId)
            .SumAsync(x => x.Debit - x.Credit);
        Assert.Equal(1_000m, inTransitBalance);
    }

    [Fact]
    public async Task Receipt_Waits_For_The_Purchase_To_Be_Posted()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var loading = await AddLoadingAsync(db, scope, quantityMt: 20m, priceUsd: 500m);
        var receipt = await AddReceiptAsync(db, scope, loading, receivedMt: 18m);

        // Purchase pilot off, so nothing put these goods into transit yet.
        var result = await CreateAdapter(db, purchase: false, inventoryReceipt: true)
            .TryPostInventoryReceiptAsync(receipt);

        Assert.Equal(PaymentPostingStatus.Skipped, result.Status);
        Assert.Equal("PURCHASE_NOT_POSTED", result.Reason);
        Assert.Equal(0, await db.JournalEntries.CountAsync(
            x => x.SourceEventId == PurchaseAccountingAdapter.BuildReceiptSourceEventId(receipt.Id)));
    }

    [Fact]
    public async Task Receipt_Duplicate_Does_Not_Create_A_Second_Journal()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var loading = await AddLoadingAsync(db, scope, quantityMt: 20m, priceUsd: 500m);
        var adapter = CreateAdapter(db, purchase: true, inventoryReceipt: true);
        await adapter.TryPostPurchaseAsync(loading);
        var receipt = await AddReceiptAsync(db, scope, loading, receivedMt: 18m);

        Assert.Equal(PaymentPostingStatus.Posted, (await adapter.TryPostInventoryReceiptAsync(receipt)).Status);
        var second = await adapter.TryPostInventoryReceiptAsync(receipt);

        Assert.Equal(PaymentPostingStatus.Duplicate, second.Status);
        Assert.Equal(1, await db.JournalEntries.CountAsync(
            x => x.SourceEventId == PurchaseAccountingAdapter.BuildReceiptSourceEventId(receipt.Id)));
    }

    [Fact]
    public async Task Keeps_Legacy_Only_When_The_Pilot_Is_Disabled()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var loading = await AddLoadingAsync(db, scope, quantityMt: 20m, priceUsd: 500m);

        var result = await CreateAdapter(db, purchase: false).TryPostPurchaseAsync(loading);

        Assert.Equal(PaymentPostingStatus.Skipped, result.Status);
        Assert.Equal("PILOT_DISABLED", result.Reason);
        Assert.Equal(0, await CountPurchaseJournalsAsync(db, loading.Id));
    }

    [Fact]
    public async Task Skips_A_Loading_Whose_Contract_Is_Not_A_Purchase()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);

        scope.Contract.ContractType = ContractType.Sale;
        await db.SaveChangesAsync();
        var loading = await AddLoadingAsync(db, scope, quantityMt: 20m, priceUsd: 500m);

        var result = await CreateAdapter(db, purchase: true).TryPostPurchaseAsync(loading);

        Assert.Equal(PaymentPostingStatus.Skipped, result.Status);
        Assert.Equal("CONTRACT_NOT_PURCHASE", result.Reason);
    }

    private static async Task<int> CountPurchaseJournalsAsync(ApplicationDbContext db, int loadingId)
        => await db.JournalEntries.CountAsync(
            x => x.SourceModule == PurchaseAccountingAdapter.SourceModule
                && x.SourceEntityType == PurchaseAccountingAdapter.PurchaseSourceEntityType
                && x.SourceEntityId == loadingId
                && !x.IsReversal);

    private static async Task<JournalEntry> LoadPurchaseJournalAsync(
        ApplicationDbContext db,
        int loadingId,
        int revision)
        => await db.JournalEntries
            .AsNoTracking()
            .Include(x => x.Lines)
            .SingleAsync(x => x.SourceEventId
                == PurchaseAccountingAdapter.BuildCreatedSourceEventId(loadingId, revision));

    private static async Task<JournalEntry> LoadReceiptJournalAsync(ApplicationDbContext db, int receiptId)
        => await db.JournalEntries
            .AsNoTracking()
            .Include(x => x.Lines)
            .SingleAsync(x => x.SourceEventId
                == PurchaseAccountingAdapter.BuildReceiptSourceEventId(receiptId));

    private static async Task<LoadingRegister> AddLoadingAsync(
        ApplicationDbContext db,
        PaymentAccountingAdapterTests.PaymentScope scope,
        decimal quantityMt,
        decimal? priceUsd)
    {
        var loading = new LoadingRegister
        {
            ContractId = scope.Contract.Id,
            ProductId = scope.Product.Id,
            TransportType = LoadingTransportType.Truck,
            LoadingDate = LoadingDate,
            LoadedQuantityMt = quantityMt,
            LoadingPriceUsd = priceUsd,
            SettlementCurrencyCode = "USD"
        };
        db.LoadingRegisters.Add(loading);
        await db.SaveChangesAsync();
        return loading;
    }

    private static async Task<LoadingReceipt> AddReceiptAsync(
        ApplicationDbContext db,
        PaymentAccountingAdapterTests.PaymentScope scope,
        LoadingRegister loading,
        decimal receivedMt)
    {
        var receipt = new LoadingReceipt
        {
            LoadingRegisterId = loading.Id,
            ReceiptDestination = LoadingReceiptDestination.ToInventory,
            TerminalId = scope.Terminal.Id,
            StorageTankId = scope.Tank.Id,
            ReceiptDate = LoadingDate.AddDays(2),
            ReceivedQuantityMt = receivedMt
        };
        db.LoadingReceipts.Add(receipt);
        await db.SaveChangesAsync();
        return receipt;
    }

    private static PurchaseAccountingAdapter CreateAdapter(
        ApplicationDbContext db,
        bool purchase = false,
        bool inventoryReceipt = false)
    {
        var options = Options.Create(new AccountingOptions
        {
            Enabled = true,
            Pilots = new AccountingPilotOptions
            {
                Purchase = purchase,
                InventoryReceipt = inventoryReceipt
            }
        });
        return new PurchaseAccountingAdapter(
            db,
            new AccountingPostingService(db, new PeriodGuard(db, new FiscalCalendarService(db)), options, new SystemCompanyProvider(db)),
            new AccountingJournalNumberGenerator(),
            new PricingService(db),
            new InventoryValuationService(db),
            options,
            NullLogger<PurchaseAccountingAdapter>.Instance);
    }
}
