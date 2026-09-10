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
/// Goods bought on a purchase contract can reach a terminal without ever producing a
/// LoadingReceipt: the transport-leg flow allocates the loadings to a leg and records the arrival
/// as an InventoryTransportReceipt. Until that arrival is posted the valuation pool stays empty,
/// so every sale out of that terminal is uncostable and the company P&amp;L cannot publish a profit.
/// </summary>
[Collection(AccountingPostgreSqlCollection.CollectionName)]
[Trait("Category", "PostgreSql")]
[Trait("Category", "Integration")]
public sealed class TransportReceiptAccountingTests(AccountingPostgreSqlFixture fixture)
{
    private static readonly DateTime LoadingDate = new(2026, 7, 5);
    private static readonly DateTime ReceiptDate = new(2026, 7, 9);

    [Fact]
    public void SourceEventId_Is_Distinct_From_The_LoadingReceipt_One()
    {
        Assert.Equal(
            "TransportInventoryReceipt:9:Created",
            PurchaseAccountingAdapter.BuildTransportReceiptSourceEventId(9));

        // Both tables number from 1, so sharing an event id would collapse two real events.
        Assert.NotEqual(
            PurchaseAccountingAdapter.BuildReceiptSourceEventId(9),
            PurchaseAccountingAdapter.BuildTransportReceiptSourceEventId(9));
    }

    [Fact]
    public async Task Receipt_Moves_Goods_Into_Inventory_And_Fills_The_Valuation_Pool()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var adapter = CreateAdapter(db, purchase: true, inventoryReceipt: true);

        var loading = await AddLoadingAsync(db, scope, quantityMt: 20m, priceUsd: 500m);
        Assert.Equal(PaymentPostingStatus.Posted, (await adapter.TryPostPurchaseAsync(loading)).Status);

        var leg = await AddLegAsync(db, scope, loading, quantityMt: 20m);
        var receipt = await AddTransportReceiptAsync(db, scope, leg, receivedMt: 20m, shortageMt: 0m);

        var poolBefore = await PoolAsync(db, scope);
        var result = await adapter.TryPostTransportReceiptAsync(receipt);

        Assert.Equal(PaymentPostingStatus.Posted, result.Status);

        var journal = await LoadJournalAsync(db, receipt.Id);
        var debitLine = journal.Lines.Single(x => x.Debit > 0m);
        var creditLine = journal.Lines.Single(x => x.Credit > 0m);

        Assert.Equal(10_000m, debitLine.Debit);
        Assert.Equal(10_000m, creditLine.Credit);
        Assert.Equal(scope.Settings.InventoryAccountId, debitLine.AccountId);
        Assert.Equal(scope.Settings.InventoryInTransitAccountId, creditLine.AccountId);
        Assert.Equal(scope.Tank.Id, debitLine.TankId);
        Assert.Equal(JournalEntryStatus.Posted, journal.Status);

        // The pool is what a later sale reads to know a unit cost, so the tonnes and the money
        // have to arrive together.
        var poolAfter = await PoolAsync(db, scope);
        Assert.Equal(20m, poolAfter.QuantityMt - poolBefore.QuantityMt);
        Assert.Equal(10_000m, poolAfter.TotalValueUsd - poolBefore.TotalValueUsd);
    }

    [Fact]
    public async Task Second_Call_Neither_Posts_Again_Nor_Doubles_The_Pool()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var adapter = CreateAdapter(db, purchase: true, inventoryReceipt: true);

        var loading = await AddLoadingAsync(db, scope, quantityMt: 12m, priceUsd: 400m);
        await adapter.TryPostPurchaseAsync(loading);
        var leg = await AddLegAsync(db, scope, loading, quantityMt: 12m);
        var receipt = await AddTransportReceiptAsync(db, scope, leg, receivedMt: 12m, shortageMt: 0m);

        Assert.Equal(PaymentPostingStatus.Posted, (await adapter.TryPostTransportReceiptAsync(receipt)).Status);
        var poolAfterFirst = await PoolAsync(db, scope);

        var second = await adapter.TryPostTransportReceiptAsync(receipt);

        Assert.Equal(PaymentPostingStatus.Duplicate, second.Status);
        Assert.Equal(1, await db.JournalEntries.CountAsync(
            x => x.SourceEventId == PurchaseAccountingAdapter.BuildTransportReceiptSourceEventId(receipt.Id)));

        var poolAfterSecond = await PoolAsync(db, scope);
        Assert.Equal(poolAfterFirst.QuantityMt, poolAfterSecond.QuantityMt);
        Assert.Equal(poolAfterFirst.TotalValueUsd, poolAfterSecond.TotalValueUsd);
    }

    [Fact]
    public async Task Receipt_Waits_For_The_Purchase_To_Be_Posted()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);

        var loading = await AddLoadingAsync(db, scope, quantityMt: 20m, priceUsd: 500m);
        var leg = await AddLegAsync(db, scope, loading, quantityMt: 20m);
        var receipt = await AddTransportReceiptAsync(db, scope, leg, receivedMt: 20m, shortageMt: 0m);

        // Nothing put these goods into transit yet, so nothing may take them out of it.
        var result = await CreateAdapter(db, purchase: false, inventoryReceipt: true)
            .TryPostTransportReceiptAsync(receipt);

        Assert.Equal(PaymentPostingStatus.Skipped, result.Status);
        Assert.Equal("PURCHASE_NOT_POSTED", result.Reason);
        Assert.Equal(0, await db.JournalEntries.CountAsync(
            x => x.SourceEventId == PurchaseAccountingAdapter.BuildTransportReceiptSourceEventId(receipt.Id)));
    }

    [Fact]
    public async Task A_Terminal_To_Terminal_Transfer_Is_Left_To_The_Transfer_Adapter()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);

        // A leg with no allocation to a purchase loading is a real transfer, not an arrival.
        var leg = new InventoryTransportLeg
        {
            SourcePurchaseContractId = scope.Contract.Id,
            ProductId = scope.Product.Id,
            SourceTerminalId = scope.Terminal.Id,
            DestinationTerminalId = scope.Terminal.Id,
            DestinationStorageTankId = scope.Tank.Id,
            TransportType = LoadingTransportType.Truck,
            LoadedDate = LoadingDate,
            QuantityMt = 5m,
            PurchaseUnitCostUsd = 500m,
            Status = InventoryTransportLegStatus.Loaded
        };
        db.InventoryTransportLegs.Add(leg);
        await db.SaveChangesAsync();
        var receipt = await AddTransportReceiptAsync(db, scope, leg, receivedMt: 5m, shortageMt: 0m);

        var result = await CreateAdapter(db, purchase: true, inventoryReceipt: true)
            .TryPostTransportReceiptAsync(receipt);

        Assert.Equal(PaymentPostingStatus.Skipped, result.Status);
        Assert.Equal("NO_PURCHASE_SOURCE_ALLOCATION", result.Reason);
    }

    [Fact]
    public async Task A_Cancelled_Receipt_Is_Never_Posted()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var adapter = CreateAdapter(db, purchase: true, inventoryReceipt: true);

        var loading = await AddLoadingAsync(db, scope, quantityMt: 8m, priceUsd: 300m);
        await adapter.TryPostPurchaseAsync(loading);
        var leg = await AddLegAsync(db, scope, loading, quantityMt: 8m);
        var receipt = await AddTransportReceiptAsync(db, scope, leg, receivedMt: 8m, shortageMt: 0m);
        receipt.IsCancelled = true;
        await db.SaveChangesAsync();

        var result = await adapter.TryPostTransportReceiptAsync(receipt);

        Assert.Equal(PaymentPostingStatus.Skipped, result.Status);
        Assert.Equal("RECEIPT_CANCELLED", result.Reason);
    }

    [Fact]
    public async Task A_Shortage_Costs_The_Loss_Account_And_Never_The_Destination_Pool()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var adapter = CreateAdapter(db, purchase: true, inventoryReceipt: true);

        var loading = await AddLoadingAsync(db, scope, quantityMt: 10m, priceUsd: 100m);
        await adapter.TryPostPurchaseAsync(loading);
        var leg = await AddLegAsync(db, scope, loading, quantityMt: 10m);

        // 9 MT arrived, 1 MT did not: the missing barrels never reached the tank, so their cost
        // cannot sit in the pool the destination sells out of.
        var receipt = await AddTransportReceiptAsync(db, scope, leg, receivedMt: 9m, shortageMt: 1m);
        var poolBefore = await PoolAsync(db, scope);

        Assert.Equal(PaymentPostingStatus.Posted, (await adapter.TryPostTransportReceiptAsync(receipt)).Status);

        var journal = await LoadJournalAsync(db, receipt.Id);
        Assert.Equal(900m, journal.Lines.Single(x => x.AccountId == scope.Settings.InventoryAccountId).Debit);
        Assert.Equal(100m, journal.Lines.Single(x => x.AccountId == scope.Settings.InventoryLossAccountId).Debit);
        Assert.Equal(
            1_000m,
            journal.Lines.Single(x => x.AccountId == scope.Settings.InventoryInTransitAccountId).Credit);

        var poolAfter = await PoolAsync(db, scope);
        Assert.Equal(9m, poolAfter.QuantityMt - poolBefore.QuantityMt);
        Assert.Equal(900m, poolAfter.TotalValueUsd - poolBefore.TotalValueUsd);
    }

    [Fact]
    public async Task Backfill_Costs_The_Sale_And_A_Second_Run_Changes_Nothing()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);

        // The exact shape the live data was in: operational rows complete, accounting rows absent.
        var loading = await AddLoadingAsync(db, scope, quantityMt: 30m, priceUsd: 200m);
        var leg = await AddLegAsync(db, scope, loading, quantityMt: 30m);
        var receipt = await AddTransportReceiptAsync(db, scope, leg, receivedMt: 30m, shortageMt: 0m);
        var sale = await AddSaleAsync(db, scope, quantityMt: 30m, totalUsd: 9_000m);

        var backfill = CreateBackfill(db);
        var first = await backfill.RunAsync(dryRun: false);

        Assert.True(first.Succeeded, string.Join(" | ", first.Errors));
        Assert.Equal(
            1,
            await db.SalesCostConsumptions.CountAsync(x =>
                x.SalesTransactionId == sale.Id && x.Status == SalesCostConsumptionStatus.Active));

        // 30 MT bought at 200 USD is what left the pool, and the P&L reads exactly this row.
        Assert.Equal(
            6_000m,
            await db.SalesCostConsumptions
                .Where(x => x.SalesTransactionId == sale.Id && x.Status == SalesCostConsumptionStatus.Active)
                .SumAsync(x => x.CostUsd));

        var journalsAfterFirst = await db.JournalEntries.CountAsync();
        var linesAfterFirst = await db.JournalEntryLines.CountAsync();
        var consumptionsAfterFirst = await db.SalesCostConsumptions.CountAsync();

        var second = await backfill.RunAsync(dryRun: false);

        Assert.True(second.Succeeded, string.Join(" | ", second.Errors));
        Assert.All(second.Steps, step => Assert.Equal(0, step.Posted));
        Assert.All(second.Steps, step => Assert.Equal(step.Candidates - step.SkippedOther, step.SkippedExisting));
        Assert.Equal(journalsAfterFirst, await db.JournalEntries.CountAsync());
        Assert.Equal(linesAfterFirst, await db.JournalEntryLines.CountAsync());
        Assert.Equal(consumptionsAfterFirst, await db.SalesCostConsumptions.CountAsync());
        Assert.Equal(
            receipt.Id,
            (await db.JournalEntries.SingleAsync(x =>
                x.SourceEventId == PurchaseAccountingAdapter.BuildTransportReceiptSourceEventId(receipt.Id)))
                .SourceEntityId);
    }

    [Fact]
    public async Task A_Dry_Run_Writes_Nothing()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);

        var loading = await AddLoadingAsync(db, scope, quantityMt: 6m, priceUsd: 150m);
        await AddLegAsync(db, scope, loading, quantityMt: 6m);

        var journalsBefore = await db.JournalEntries.CountAsync();
        var report = await CreateBackfill(db).RunAsync(dryRun: true);

        Assert.True(report.DryRun);
        Assert.False(report.ChartSeeded);
        Assert.Equal(journalsBefore, await db.JournalEntries.CountAsync());
    }

    private static async Task<InventoryAverageCost> PoolAsync(
        ApplicationDbContext db,
        PaymentAccountingAdapterTests.PaymentScope scope)
        => await db.InventoryAverageCosts.AsNoTracking().SingleOrDefaultAsync(x =>
               x.CompanyId == scope.Company.Id
               && x.ProductId == scope.Product.Id
               && x.TerminalId == scope.Terminal.Id)
           ?? new InventoryAverageCost();

    private static async Task<JournalEntry> LoadJournalAsync(ApplicationDbContext db, int receiptId)
        => await db.JournalEntries
            .AsNoTracking()
            .Include(x => x.Lines)
            .SingleAsync(x => x.SourceEventId
                == PurchaseAccountingAdapter.BuildTransportReceiptSourceEventId(receiptId));

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

    /// <summary>
    /// A leg fed straight from a purchase loading: no source terminal, because the goods were
    /// never in one — they came off the supplier's loading and are on the road to the terminal.
    /// </summary>
    private static async Task<InventoryTransportLeg> AddLegAsync(
        ApplicationDbContext db,
        PaymentAccountingAdapterTests.PaymentScope scope,
        LoadingRegister loading,
        decimal quantityMt)
    {
        var leg = new InventoryTransportLeg
        {
            SourcePurchaseContractId = scope.Contract.Id,
            ProductId = scope.Product.Id,
            DestinationTerminalId = scope.Terminal.Id,
            DestinationStorageTankId = scope.Tank.Id,
            TransportType = LoadingTransportType.Truck,
            LoadedDate = LoadingDate,
            QuantityMt = quantityMt,
            PurchaseUnitCostUsd = loading.LoadingPriceUsd,
            Status = InventoryTransportLegStatus.Loaded
        };
        db.InventoryTransportLegs.Add(leg);
        await db.SaveChangesAsync();

        db.InventoryTransportLegAllocations.Add(new InventoryTransportLegAllocation
        {
            InventoryTransportLegId = leg.Id,
            SourcePurchaseContractId = scope.Contract.Id,
            SourceLoadingRegisterId = loading.Id,
            QuantityMt = quantityMt
        });
        await db.SaveChangesAsync();
        return leg;
    }

    private static async Task<InventoryTransportReceipt> AddTransportReceiptAsync(
        ApplicationDbContext db,
        PaymentAccountingAdapterTests.PaymentScope scope,
        InventoryTransportLeg leg,
        decimal receivedMt,
        decimal shortageMt)
    {
        var receipt = new InventoryTransportReceipt
        {
            InventoryTransportLegId = leg.Id,
            ReceiptDate = ReceiptDate,
            ReceivedQuantityMt = receivedMt,
            ShortageQuantityMt = shortageMt,
            ReceiptDestination = InventoryTransportReceiptDestination.ToInventory,
            DestinationTerminalId = scope.Terminal.Id,
            DestinationStorageTankId = scope.Tank.Id
        };
        db.InventoryTransportReceipts.Add(receipt);
        await db.SaveChangesAsync();
        return receipt;
    }

    private static async Task<SalesTransaction> AddSaleAsync(
        ApplicationDbContext db,
        PaymentAccountingAdapterTests.PaymentScope scope,
        decimal quantityMt,
        decimal totalUsd)
    {
        var sale = new SalesTransaction
        {
            CompanyId = scope.Company.Id,
            CustomerId = scope.Customer.Id,
            ProductId = scope.Product.Id,
            SourcePurchaseContractId = scope.Contract.Id,
            InvoiceNumber = $"TRX-{Guid.NewGuid():N}"[..12],
            SaleDate = ReceiptDate.AddDays(1),
            QuantityMt = quantityMt,
            UnitPriceUsd = totalUsd / quantityMt,
            TotalUsd = totalUsd,
            Currency = "USD",
            AppliedFxRateToUsd = 1m,
            UnitPriceInCurrency = totalUsd / quantityMt,
            TotalInCurrency = totalUsd
        };
        db.SalesTransactions.Add(sale);
        await db.SaveChangesAsync();

        db.InventoryMovements.Add(new InventoryMovement
        {
            TerminalId = scope.Terminal.Id,
            StorageTankId = scope.Tank.Id,
            ProductId = scope.Product.Id,
            Direction = MovementDirection.Out,
            MovementDate = sale.SaleDate,
            QuantityMt = quantityMt,
            SalesTransactionId = sale.Id,
            ReferenceDocument = sale.InvoiceNumber
        });
        await db.SaveChangesAsync();
        return sale;
    }

    private static AccountingOptions BuildOptions(bool purchase, bool inventoryReceipt, bool sale, bool cogs)
        => new()
        {
            Enabled = true,
            Pilots = new AccountingPilotOptions
            {
                Purchase = purchase,
                InventoryReceipt = inventoryReceipt,
                Sale = sale,
                Cogs = cogs
            }
        };

    private static PurchaseAccountingAdapter CreateAdapter(
        ApplicationDbContext db,
        bool purchase = false,
        bool inventoryReceipt = false)
    {
        var options = Options.Create(BuildOptions(purchase, inventoryReceipt, sale: false, cogs: false));
        return new PurchaseAccountingAdapter(
            db,
            new AccountingPostingService(db, new PeriodGuard(db, new FiscalCalendarService(db)), options, new SystemCompanyProvider(db)),
            new AccountingJournalNumberGenerator(),
            new PricingService(db),
            new InventoryValuationService(db),
            options,
            NullLogger<PurchaseAccountingAdapter>.Instance);
    }

    private static AccountingBackfillService CreateBackfill(ApplicationDbContext db)
    {
        var options = Options.Create(BuildOptions(purchase: true, inventoryReceipt: true, sale: true, cogs: true));
        var posting = new AccountingPostingService(
            db, new PeriodGuard(db, new FiscalCalendarService(db)), options, new SystemCompanyProvider(db));
        var numbers = new AccountingJournalNumberGenerator();
        var valuation = new InventoryValuationService(db);

        var expense = new ExpenseAccountingAdapter(
            db, posting, numbers, options, NullLogger<ExpenseAccountingAdapter>.Instance);
        var statements = new PTGOilSystem.Web.Services.PartyStatements.PartnershipStatementService(db);

        return new AccountingBackfillService(
            db,
            new AccountingChartSeeder(db, options),
            new PurchaseAccountingAdapter(
                db, posting, numbers, new PricingService(db), valuation, options,
                NullLogger<PurchaseAccountingAdapter>.Instance),
            new SalesAccountingAdapter(
                db, posting, numbers, valuation, options, NullLogger<SalesAccountingAdapter>.Instance),
            expense,
            new PaymentAccountingAdapter(
                db, posting, numbers, new PaymentCompanyResolver(db), expense, options,
                NullLogger<PaymentAccountingAdapter>.Instance),
            new PartnershipProfitAllocationAdapter(
                db, posting, numbers, statements, options,
                NullLogger<PartnershipProfitAllocationAdapter>.Instance),
            options,
            NullLogger<AccountingBackfillService>.Instance);
    }
}
