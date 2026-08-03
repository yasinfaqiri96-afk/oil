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
/// Stage 7 — sales and cost of goods sold on a moving weighted average.
///
/// The arithmetic here decides reported profit, so these tests pin the average itself, not just
/// that a journal appeared: a receipt must move the average, a sale must consume at it, and a
/// sale that outruns the pool must leave the pool alone.
/// </summary>
[Collection(AccountingPostgreSqlCollection.CollectionName)]
[Trait("Category", "PostgreSql")]
[Trait("Category", "Integration")]
public sealed class SalesAccountingAdapterTests(AccountingPostgreSqlFixture fixture)
{
    private static readonly DateTime SaleDate = new(2026, 7, 5);

    [Fact]
    public void SourceEventId_Formats_Are_Stable()
    {
        Assert.Equal("Sale:7:Created", SalesAccountingAdapter.BuildCreatedSourceEventId(7));
        Assert.Equal("Sale:7:Cogs", SalesAccountingAdapter.BuildCogsSourceEventId(7));
    }

    // ── Moving weighted average arithmetic ──────────────────────────────────

    [Fact]
    public async Task Receipts_At_Different_Prices_Move_The_Average()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var valuation = new InventoryValuationService(db);

        await valuation.ApplyReceiptAsync(scope.Company.Id, scope.Product.Id, scope.Terminal.Id, 20m, 10_000m);
        await valuation.ApplyReceiptAsync(scope.Company.Id, scope.Product.Id, scope.Terminal.Id, 10m, 6_000m);

        var pool = await LoadPoolAsync(db, scope, scope.Terminal.Id);

        // (10,000 + 6,000) / (20 + 10) = 533.333333...
        Assert.Equal(30m, pool.QuantityMt);
        Assert.Equal(16_000m, pool.TotalValueUsd);
        Assert.Equal(533.333333m, pool.AverageUnitCostUsd);
    }

    [Fact]
    public async Task Consuming_Takes_Value_Out_At_The_Current_Average()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var valuation = new InventoryValuationService(db);
        await valuation.ApplyReceiptAsync(scope.Company.Id, scope.Product.Id, scope.Terminal.Id, 20m, 10_000m);
        await valuation.ApplyReceiptAsync(scope.Company.Id, scope.Product.Id, scope.Terminal.Id, 10m, 6_000m);

        var consumption = await valuation.TryConsumeAsync(
            scope.Company.Id, scope.Product.Id, scope.Terminal.Id, 5m);

        Assert.True(consumption.Succeeded);
        Assert.Equal(2_666.6667m, consumption.CostUsd);

        // The average is unchanged by a sale — only a purchase moves it.
        var pool = await LoadPoolAsync(db, scope, scope.Terminal.Id);
        Assert.Equal(25m, pool.QuantityMt);
        Assert.Equal(13_333.3333m, pool.TotalValueUsd);
        Assert.Equal(533.333332m, pool.AverageUnitCostUsd);
    }

    [Fact]
    public async Task Consuming_The_Whole_Pool_Leaves_No_Rounding_Crumb_Behind()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var valuation = new InventoryValuationService(db);

        // 3 MT for 10 USD is 3.333333/MT: no exact per-unit cost exists.
        await valuation.ApplyReceiptAsync(scope.Company.Id, scope.Product.Id, scope.Terminal.Id, 3m, 10m);

        var consumption = await valuation.TryConsumeAsync(
            scope.Company.Id, scope.Product.Id, scope.Terminal.Id, 3m);

        Assert.True(consumption.Succeeded);
        Assert.Equal(10m, consumption.CostUsd);

        var pool = await LoadPoolAsync(db, scope, scope.Terminal.Id);
        Assert.Equal(0m, pool.QuantityMt);
        Assert.Equal(0m, pool.TotalValueUsd);
        Assert.Null(pool.AverageUnitCostUsd);
    }

    [Fact]
    public async Task Consuming_More_Than_The_Pool_Holds_Changes_Nothing()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var valuation = new InventoryValuationService(db);
        await valuation.ApplyReceiptAsync(scope.Company.Id, scope.Product.Id, scope.Terminal.Id, 5m, 2_500m);

        var consumption = await valuation.TryConsumeAsync(
            scope.Company.Id, scope.Product.Id, scope.Terminal.Id, 8m);

        Assert.False(consumption.Succeeded);
        Assert.Equal("INVENTORY_NOT_VALUED", consumption.Reason);

        var pool = await LoadPoolAsync(db, scope, scope.Terminal.Id);
        Assert.Equal(5m, pool.QuantityMt);
        Assert.Equal(2_500m, pool.TotalValueUsd);
    }

    [Fact]
    public async Task Pools_Are_Separate_Per_Terminal()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var otherTerminalId = await AddTerminalAsync(db);
        var valuation = new InventoryValuationService(db);

        await valuation.ApplyReceiptAsync(scope.Company.Id, scope.Product.Id, scope.Terminal.Id, 10m, 5_000m);
        await valuation.ApplyReceiptAsync(scope.Company.Id, scope.Product.Id, otherTerminalId, 10m, 9_000m);

        // Same company and product, different terminal: the averages must not blend.
        Assert.Equal(500m, (await LoadPoolAsync(db, scope, scope.Terminal.Id)).AverageUnitCostUsd);
        Assert.Equal(900m, (await LoadPoolAsync(db, scope, otherTerminalId)).AverageUnitCostUsd);
    }

    // ── Sale and COGS ───────────────────────────────────────────────────────

    [Fact]
    public async Task Sale_Debits_Receivable_And_Credits_Revenue()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var sale = await AddSaleAsync(db, scope, quantityMt: 5m, totalUsd: 4_000m);

        var result = await CreateAdapter(db, sale: true).TryPostSaleAsync(sale);

        Assert.Equal(PaymentPostingStatus.Posted, result.Status);

        var journal = await LoadJournalAsync(db, SalesAccountingAdapter.BuildCreatedSourceEventId(sale.Id));
        var debitLine = journal.Lines.Single(x => x.Debit > 0m);
        var creditLine = journal.Lines.Single(x => x.Credit > 0m);

        Assert.Equal(scope.Settings.AccountsReceivableAccountId, debitLine.AccountId);
        Assert.Equal(scope.Settings.SalesRevenueAccountId, creditLine.AccountId);
        Assert.Equal(4_000m, debitLine.Debit);
        Assert.Equal(AccountingPartyType.Customer, debitLine.PartyType);
        Assert.Equal(scope.Customer.Id, debitLine.PartyId);
    }

    [Fact]
    public async Task Cogs_Values_What_Left_The_Tank_At_The_Moving_Average()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var valuation = new InventoryValuationService(db);
        await valuation.ApplyReceiptAsync(scope.Company.Id, scope.Product.Id, scope.Terminal.Id, 20m, 10_000m);
        await valuation.ApplyReceiptAsync(scope.Company.Id, scope.Product.Id, scope.Terminal.Id, 10m, 6_000m);

        var sale = await AddSaleAsync(db, scope, quantityMt: 5m, totalUsd: 4_000m);
        await AddOutMovementAsync(db, scope, sale, scope.Terminal.Id, 5m);

        var result = await CreateAdapter(db, cogs: true).TryPostCogsAsync(sale);

        Assert.Equal(PaymentPostingStatus.Posted, result.Status);

        var journal = await LoadJournalAsync(db, SalesAccountingAdapter.BuildCogsSourceEventId(sale.Id));
        var debitLine = journal.Lines.Single(x => x.Debit > 0m);
        var creditLine = journal.Lines.Single(x => x.Credit > 0m);

        // 5 MT at the 533.333333 average.
        Assert.Equal(2_666.6667m, debitLine.Debit);
        Assert.Equal(scope.Settings.CostOfGoodsSoldAccountId, debitLine.AccountId);
        Assert.Equal(scope.Settings.InventoryAccountId, creditLine.AccountId);

        var pool = await LoadPoolAsync(db, scope, scope.Terminal.Id);
        Assert.Equal(25m, pool.QuantityMt);
    }

    [Fact]
    public async Task Cogs_Skips_But_Revenue_Still_Posts_When_The_Pool_Cannot_Cover_The_Sale()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var valuation = new InventoryValuationService(db);
        await valuation.ApplyReceiptAsync(scope.Company.Id, scope.Product.Id, scope.Terminal.Id, 2m, 1_000m);

        var sale = await AddSaleAsync(db, scope, quantityMt: 5m, totalUsd: 4_000m);
        await AddOutMovementAsync(db, scope, sale, scope.Terminal.Id, 5m);
        var adapter = CreateAdapter(db, sale: true, cogs: true);

        Assert.Equal(PaymentPostingStatus.Posted, (await adapter.TryPostSaleAsync(sale)).Status);
        var cogs = await adapter.TryPostCogsAsync(sale);

        Assert.Equal(PaymentPostingStatus.Skipped, cogs.Status);
        Assert.Equal("INVENTORY_NOT_VALUED", cogs.Reason);

        // The pool is untouched and the revenue journal stands on its own.
        var pool = await LoadPoolAsync(db, scope, scope.Terminal.Id);
        Assert.Equal(2m, pool.QuantityMt);
        Assert.Equal(1, await db.JournalEntries.CountAsync(
            x => x.SourceEventId == SalesAccountingAdapter.BuildCreatedSourceEventId(sale.Id)));
        Assert.Equal(0, await db.JournalEntries.CountAsync(
            x => x.SourceEventId == SalesAccountingAdapter.BuildCogsSourceEventId(sale.Id)));
    }

    [Fact]
    public async Task Cogs_Consumes_From_Every_Terminal_The_Sale_Drew_From()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var otherTerminalId = await AddTerminalAsync(db);
        var valuation = new InventoryValuationService(db);
        await valuation.ApplyReceiptAsync(scope.Company.Id, scope.Product.Id, scope.Terminal.Id, 10m, 5_000m);
        await valuation.ApplyReceiptAsync(scope.Company.Id, scope.Product.Id, otherTerminalId, 10m, 9_000m);

        var sale = await AddSaleAsync(db, scope, quantityMt: 6m, totalUsd: 8_000m);
        await AddOutMovementAsync(db, scope, sale, scope.Terminal.Id, 4m);
        await AddOutMovementAsync(db, scope, sale, otherTerminalId, 2m);

        Assert.Equal(PaymentPostingStatus.Posted, (await CreateAdapter(db, cogs: true).TryPostCogsAsync(sale)).Status);

        // 4 x 500 + 2 x 900 = 3,800: each terminal at its own average.
        var journal = await LoadJournalAsync(db, SalesAccountingAdapter.BuildCogsSourceEventId(sale.Id));
        Assert.Equal(3_800m, journal.Lines.Single(x => x.Debit > 0m).Debit);
        Assert.Equal(6m, (await LoadPoolAsync(db, scope, scope.Terminal.Id)).QuantityMt);
        Assert.Equal(8m, (await LoadPoolAsync(db, scope, otherTerminalId)).QuantityMt);
    }

    [Fact]
    public async Task Cogs_Consumes_Nothing_When_Only_One_Of_Several_Terminals_Falls_Short()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var otherTerminalId = await AddTerminalAsync(db);
        var valuation = new InventoryValuationService(db);
        await valuation.ApplyReceiptAsync(scope.Company.Id, scope.Product.Id, scope.Terminal.Id, 10m, 5_000m);
        await valuation.ApplyReceiptAsync(scope.Company.Id, scope.Product.Id, otherTerminalId, 1m, 900m);

        var sale = await AddSaleAsync(db, scope, quantityMt: 6m, totalUsd: 8_000m);
        await AddOutMovementAsync(db, scope, sale, scope.Terminal.Id, 4m);
        await AddOutMovementAsync(db, scope, sale, otherTerminalId, 2m);

        var result = await CreateAdapter(db, cogs: true).TryPostCogsAsync(sale);

        Assert.Equal(PaymentPostingStatus.Skipped, result.Status);
        Assert.Equal("INVENTORY_NOT_VALUED", result.Reason);

        // A half-valued sale would be worse than an unvalued one: both pools are intact.
        Assert.Equal(10m, (await LoadPoolAsync(db, scope, scope.Terminal.Id)).QuantityMt);
        Assert.Equal(1m, (await LoadPoolAsync(db, scope, otherTerminalId)).QuantityMt);
    }

    [Fact]
    public async Task Cogs_Skips_A_Sale_That_Moved_No_Inventory()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var sale = await AddSaleAsync(db, scope, quantityMt: 5m, totalUsd: 4_000m);

        var result = await CreateAdapter(db, cogs: true).TryPostCogsAsync(sale);

        Assert.Equal(PaymentPostingStatus.Skipped, result.Status);
        Assert.Equal("NO_OUTBOUND_MOVEMENT", result.Reason);
    }

    [Fact]
    public async Task Cogs_Is_Idempotent_And_Does_Not_Consume_Twice()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var valuation = new InventoryValuationService(db);
        await valuation.ApplyReceiptAsync(scope.Company.Id, scope.Product.Id, scope.Terminal.Id, 20m, 10_000m);

        var sale = await AddSaleAsync(db, scope, quantityMt: 5m, totalUsd: 4_000m);
        await AddOutMovementAsync(db, scope, sale, scope.Terminal.Id, 5m);
        var adapter = CreateAdapter(db, cogs: true);

        Assert.Equal(PaymentPostingStatus.Posted, (await adapter.TryPostCogsAsync(sale)).Status);
        var second = await adapter.TryPostCogsAsync(sale);

        Assert.Equal(PaymentPostingStatus.Duplicate, second.Status);

        // The pool must have been drawn down once, not twice.
        Assert.Equal(15m, (await LoadPoolAsync(db, scope, scope.Terminal.Id)).QuantityMt);
        Assert.Equal(1, await db.JournalEntries.CountAsync(
            x => x.SourceEventId == SalesAccountingAdapter.BuildCogsSourceEventId(sale.Id)));
    }

    [Fact]
    public async Task Sale_Skips_When_The_Company_Is_Not_Provable()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var sale = await AddSaleAsync(db, scope, quantityMt: 5m, totalUsd: 4_000m, configure: s =>
        {
            s.CompanyId = null;
            s.ContractId = null;
            s.SourcePurchaseContractId = null;
        });

        var result = await CreateAdapter(db, sale: true).TryPostSaleAsync(sale);

        Assert.Equal(PaymentPostingStatus.Skipped, result.Status);
        Assert.Equal("SALE_COMPANY_UNKNOWN", result.Reason);
    }

    [Fact]
    public async Task Sale_Falls_Back_To_The_Source_Purchase_Contract_For_The_Company()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var sale = await AddSaleAsync(db, scope, quantityMt: 5m, totalUsd: 4_000m, configure: s =>
        {
            s.CompanyId = null;
            s.ContractId = null;
            s.SourcePurchaseContractId = scope.Contract.Id;
        });

        Assert.Equal(PaymentPostingStatus.Posted, (await CreateAdapter(db, sale: true).TryPostSaleAsync(sale)).Status);

        var journal = await LoadJournalAsync(db, SalesAccountingAdapter.BuildCreatedSourceEventId(sale.Id));
        Assert.Equal(scope.Company.Id, journal.CompanyId);
    }

    [Fact]
    public async Task Sale_Skips_A_Cancelled_Sale()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var sale = await AddSaleAsync(db, scope, quantityMt: 5m, totalUsd: 4_000m, configure: s => s.IsCancelled = true);

        var result = await CreateAdapter(db, sale: true).TryPostSaleAsync(sale);

        Assert.Equal(PaymentPostingStatus.Skipped, result.Status);
        Assert.Equal("SALE_CANCELLED", result.Reason);
    }

    [Fact]
    public async Task Keeps_Legacy_Only_When_The_Pilots_Are_Disabled()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var sale = await AddSaleAsync(db, scope, quantityMt: 5m, totalUsd: 4_000m);
        var adapter = CreateAdapter(db);

        Assert.Equal("PILOT_DISABLED", (await adapter.TryPostSaleAsync(sale)).Reason);
        Assert.Equal("PILOT_DISABLED", (await adapter.TryPostCogsAsync(sale)).Reason);
        Assert.Equal(0, await db.JournalEntries.CountAsync(x => x.SourceEntityId == sale.Id
            && x.SourceModule == SalesAccountingAdapter.SourceModule));
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static async Task<InventoryAverageCost> LoadPoolAsync(
        ApplicationDbContext db,
        PaymentAccountingAdapterTests.PaymentScope scope,
        int terminalId)
        => await db.InventoryAverageCosts
            .AsNoTracking()
            .SingleAsync(x => x.CompanyId == scope.Company.Id
                && x.ProductId == scope.Product.Id
                && x.TerminalId == terminalId);

    private static async Task<JournalEntry> LoadJournalAsync(ApplicationDbContext db, string sourceEventId)
        => await db.JournalEntries
            .AsNoTracking()
            .Include(x => x.Lines)
            .SingleAsync(x => x.SourceEventId == sourceEventId);

    private static async Task<int> AddTerminalAsync(ApplicationDbContext db)
    {
        var terminal = new Terminal
        {
            Code = PaymentAccountingAdapterTests.Unique("T"),
            Name = PaymentAccountingAdapterTests.Unique("Terminal"),
            IsActive = true
        };
        db.Terminals.Add(terminal);
        await db.SaveChangesAsync();
        return terminal.Id;
    }

    private static async Task<SalesTransaction> AddSaleAsync(
        ApplicationDbContext db,
        PaymentAccountingAdapterTests.PaymentScope scope,
        decimal quantityMt,
        decimal totalUsd,
        Action<SalesTransaction>? configure = null)
    {
        var sale = new SalesTransaction
        {
            CompanyId = scope.Company.Id,
            ContractId = scope.Contract.Id,
            CustomerId = scope.Customer.Id,
            ProductId = scope.Product.Id,
            InvoiceNumber = PaymentAccountingAdapterTests.Unique("INV"),
            SaleDate = SaleDate,
            QuantityMt = quantityMt,
            Currency = "USD",
            UnitPriceInCurrency = totalUsd / quantityMt,
            AppliedFxRateToUsd = 1m,
            UnitPriceUsd = totalUsd / quantityMt,
            TotalInCurrency = totalUsd,
            TotalUsd = totalUsd
        };
        configure?.Invoke(sale);

        db.SalesTransactions.Add(sale);
        await db.SaveChangesAsync();
        return sale;
    }

    private static async Task AddOutMovementAsync(
        ApplicationDbContext db,
        PaymentAccountingAdapterTests.PaymentScope scope,
        SalesTransaction sale,
        int terminalId,
        decimal quantityMt)
    {
        db.InventoryMovements.Add(new InventoryMovement
        {
            TerminalId = terminalId,
            ProductId = scope.Product.Id,
            ContractId = scope.Contract.Id,
            SalesTransactionId = sale.Id,
            Direction = MovementDirection.Out,
            MovementDate = sale.SaleDate,
            QuantityMt = quantityMt
        });
        await db.SaveChangesAsync();
    }

    private static SalesAccountingAdapter CreateAdapter(
        ApplicationDbContext db,
        bool sale = false,
        bool cogs = false)
    {
        var options = Options.Create(new AccountingOptions
        {
            Enabled = true,
            Pilots = new AccountingPilotOptions { Sale = sale, Cogs = cogs }
        });
        return new SalesAccountingAdapter(
            db,
            new AccountingPostingService(db, new PeriodGuard(db, new FiscalCalendarService(db)), options, new SystemCompanyProvider(db)),
            new AccountingJournalNumberGenerator(),
            new InventoryValuationService(db),
            options,
            NullLogger<SalesAccountingAdapter>.Instance);
    }
}
