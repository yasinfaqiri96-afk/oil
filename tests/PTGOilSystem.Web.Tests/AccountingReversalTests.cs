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
/// Cancelling a legacy record must reverse what it posted, exactly once. A posted journal is
/// never edited or deleted, so every one of these paths goes through ReverseAsync, and calling
/// any of them twice must leave the books unchanged the second time.
/// </summary>
[Collection(AccountingPostgreSqlCollection.CollectionName)]
[Trait("Category", "PostgreSql")]
[Trait("Category", "Integration")]
public sealed class AccountingReversalTests(AccountingPostgreSqlFixture fixture)
{
    private static readonly DateTime EventDate = new(2026, 7, 5);

    [Fact]
    public void SourceEventId_Formats_Are_Stable()
    {
        Assert.Equal("Expense:7:Reversed", ExpenseAccountingAdapter.BuildReversedSourceEventId(7));
        Assert.Equal(
            "InventoryReceipt:9:Reversed",
            PurchaseAccountingAdapter.BuildReceiptReversedSourceEventId(9));
    }

    // ── Expense ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Expense_Reversal_Cancels_The_Journal_It_Raised()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var expense = await AddExpenseAsync(db, scope, ExpensePayableKind.AccountsPayable);
        var adapter = CreateExpenseAdapter(db, pilotEnabled: true);

        Assert.Equal(PaymentPostingStatus.Posted, (await adapter.TryPostExpenseAsync(expense)).Status);

        expense.IsCancelled = true;
        var result = await adapter.TryPostExpenseReversalAsync(expense);

        Assert.Equal(PaymentPostingStatus.Posted, result.Status);

        var original = await LoadExpenseJournalAsync(db, expense.Id);
        var reversal = await db.JournalEntries
            .AsNoTracking()
            .Include(x => x.Lines)
            .SingleAsync(x => x.SourceEventId == ExpenseAccountingAdapter.BuildReversedSourceEventId(expense.Id));

        // The original is untouched; the reversal is a separate, opposite journal.
        Assert.Equal(JournalEntryStatus.Posted, original.Status);
        Assert.True(reversal.IsReversal);
        Assert.Equal(original.Id, reversal.ReversalOfJournalEntryId);
        Assert.Equal(
            original.Lines.Sum(x => x.Debit),
            reversal.Lines.Sum(x => x.Credit));

        // Net effect on the expense account is zero.
        var netExpense = await db.JournalEntryLines
            .AsNoTracking()
            .Where(x => x.AccountId == scope.Settings.GeneralExpenseAccountId)
            .SumAsync(x => x.Debit - x.Credit);
        Assert.Equal(0m, netExpense);
    }

    [Fact]
    public async Task Expense_Reversal_Is_Idempotent()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var expense = await AddExpenseAsync(db, scope, ExpensePayableKind.AccountsPayable);
        var adapter = CreateExpenseAdapter(db, pilotEnabled: true);
        await adapter.TryPostExpenseAsync(expense);

        Assert.Equal(PaymentPostingStatus.Posted, (await adapter.TryPostExpenseReversalAsync(expense)).Status);
        var second = await adapter.TryPostExpenseReversalAsync(expense);

        Assert.Equal(PaymentPostingStatus.Duplicate, second.Status);
        Assert.Equal("DUPLICATE_SOURCE_EVENT", second.Reason);
        Assert.Equal(1, await db.JournalEntries.CountAsync(
            x => x.SourceEventId == ExpenseAccountingAdapter.BuildReversedSourceEventId(expense.Id)));
    }

    [Fact]
    public async Task Expense_Reversal_Stays_Legacy_Only_When_The_Original_Was_Never_Posted()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var expense = await AddExpenseAsync(db, scope, ExpensePayableKind.AccountsPayable);

        // Expense was created while the pilot was off, so there is nothing to reverse.
        var result = await CreateExpenseAdapter(db, pilotEnabled: true).TryPostExpenseReversalAsync(expense);

        Assert.Equal(PaymentPostingStatus.Skipped, result.Status);
        Assert.Equal("ORIGINAL_JOURNAL_NOT_POSTED", result.Reason);
        Assert.Equal(0, await db.JournalEntries.CountAsync(
            x => x.SourceEventId == ExpenseAccountingAdapter.BuildReversedSourceEventId(expense.Id)));
    }

    [Fact]
    public async Task Expense_Reversal_Keeps_Legacy_Only_When_The_Pilot_Is_Disabled()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var expense = await AddExpenseAsync(db, scope, ExpensePayableKind.AccountsPayable);
        await CreateExpenseAdapter(db, pilotEnabled: true).TryPostExpenseAsync(expense);

        var result = await CreateExpenseAdapter(db, pilotEnabled: false).TryPostExpenseReversalAsync(expense);

        Assert.Equal(PaymentPostingStatus.Skipped, result.Status);
        Assert.Equal("PILOT_DISABLED", result.Reason);
    }

    // ── Inventory receipt ───────────────────────────────────────────────────

    [Fact]
    public async Task Receipt_Reversal_Puts_The_Goods_Back_Into_In_Transit()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var (loading, receipt) = await AddPurchaseAndReceiptAsync(db, scope);
        var adapter = CreatePurchaseAdapter(db, purchase: true, inventoryReceipt: true);
        await adapter.TryPostPurchaseAsync(loading);
        await adapter.TryPostInventoryReceiptAsync(receipt);

        var result = await adapter.TryPostInventoryReceiptReversalAsync(receipt);

        Assert.Equal(PaymentPostingStatus.Posted, result.Status);

        // Inventory is back to zero and everything sits in transit again.
        var inventoryBalance = await db.JournalEntryLines
            .AsNoTracking()
            .Where(x => x.AccountId == scope.Settings.InventoryAccountId)
            .SumAsync(x => x.Debit - x.Credit);
        Assert.Equal(0m, inventoryBalance);

        var inTransitBalance = await db.JournalEntryLines
            .AsNoTracking()
            .Where(x => x.AccountId == scope.Settings.InventoryInTransitAccountId)
            .SumAsync(x => x.Debit - x.Credit);
        Assert.Equal(10_000m, inTransitBalance);
    }

    [Fact]
    public async Task Receipt_Reversal_Is_Idempotent()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var (loading, receipt) = await AddPurchaseAndReceiptAsync(db, scope);
        var adapter = CreatePurchaseAdapter(db, purchase: true, inventoryReceipt: true);
        await adapter.TryPostPurchaseAsync(loading);
        await adapter.TryPostInventoryReceiptAsync(receipt);

        Assert.Equal(PaymentPostingStatus.Posted,
            (await adapter.TryPostInventoryReceiptReversalAsync(receipt)).Status);
        var second = await adapter.TryPostInventoryReceiptReversalAsync(receipt);

        Assert.Equal(PaymentPostingStatus.Duplicate, second.Status);
        Assert.Equal(1, await db.JournalEntries.CountAsync(
            x => x.SourceEventId == PurchaseAccountingAdapter.BuildReceiptReversedSourceEventId(receipt.Id)));
    }

    // ── Purchase ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Purchase_Reversal_Cancels_Every_Posted_Revision()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var loading = await AddLoadingAsync(db, scope, quantityMt: 20m, priceUsd: 500m);
        var adapter = CreatePurchaseAdapter(db, purchase: true);

        await adapter.TryPostPurchaseAsync(loading);
        loading.LoadingPriceUsd = 600m;
        await db.SaveChangesAsync();
        await adapter.TryPostPurchaseAsync(loading);

        var result = await adapter.TryPostPurchaseReversalAsync(loading);

        Assert.Equal(PaymentPostingStatus.Posted, result.Status);

        // Two revisions were posted, so both must be reversed and nothing may remain.
        var inTransitBalance = await db.JournalEntryLines
            .AsNoTracking()
            .Where(x => x.AccountId == scope.Settings.InventoryInTransitAccountId)
            .SumAsync(x => x.Debit - x.Credit);
        Assert.Equal(0m, inTransitBalance);

        var payableBalance = await db.JournalEntryLines
            .AsNoTracking()
            .Where(x => x.AccountId == scope.Settings.AccountsPayableAccountId)
            .SumAsync(x => x.Credit - x.Debit);
        Assert.Equal(0m, payableBalance);
    }

    [Fact]
    public async Task Purchase_Reversal_Is_Idempotent()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var loading = await AddLoadingAsync(db, scope, quantityMt: 20m, priceUsd: 500m);
        var adapter = CreatePurchaseAdapter(db, purchase: true);
        await adapter.TryPostPurchaseAsync(loading);

        Assert.Equal(PaymentPostingStatus.Posted, (await adapter.TryPostPurchaseReversalAsync(loading)).Status);
        var second = await adapter.TryPostPurchaseReversalAsync(loading);

        Assert.Equal(PaymentPostingStatus.Duplicate, second.Status);
        Assert.Equal(1, await db.JournalEntries.CountAsync(
            x => x.SourceEventId == PurchaseAccountingAdapter.BuildReversedSourceEventId(loading.Id, 0)));
    }

    [Fact]
    public async Task Purchase_Reversal_Refuses_While_A_Receipt_Still_Holds_The_Goods()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var (loading, receipt) = await AddPurchaseAndReceiptAsync(db, scope);
        var adapter = CreatePurchaseAdapter(db, purchase: true, inventoryReceipt: true);
        await adapter.TryPostPurchaseAsync(loading);
        await adapter.TryPostInventoryReceiptAsync(receipt);

        // Reversing the purchase now would credit in-transit with nothing behind it.
        var blocked = await adapter.TryPostPurchaseReversalAsync(loading);
        Assert.Equal(PaymentPostingStatus.Skipped, blocked.Status);
        Assert.Equal("RECEIPT_STILL_POSTED", blocked.Reason);

        // Once the receipt is reversed, the purchase can go.
        await adapter.TryPostInventoryReceiptReversalAsync(receipt);
        Assert.Equal(PaymentPostingStatus.Posted, (await adapter.TryPostPurchaseReversalAsync(loading)).Status);

        var inTransitBalance = await db.JournalEntryLines
            .AsNoTracking()
            .Where(x => x.AccountId == scope.Settings.InventoryInTransitAccountId)
            .SumAsync(x => x.Debit - x.Credit);
        Assert.Equal(0m, inTransitBalance);
    }

    [Fact]
    public async Task Purchase_Reversal_Stays_Legacy_Only_When_Nothing_Was_Posted()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var loading = await AddLoadingAsync(db, scope, quantityMt: 20m, priceUsd: 500m);

        var result = await CreatePurchaseAdapter(db, purchase: true).TryPostPurchaseReversalAsync(loading);

        Assert.Equal(PaymentPostingStatus.Skipped, result.Status);
        Assert.Equal("ORIGINAL_JOURNAL_NOT_POSTED", result.Reason);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static async Task<JournalEntry> LoadExpenseJournalAsync(ApplicationDbContext db, int expenseId)
        => await db.JournalEntries
            .AsNoTracking()
            .Include(x => x.Lines)
            .SingleAsync(x => x.SourceEventId == ExpenseAccountingAdapter.BuildCreatedSourceEventId(expenseId));

    private static async Task<ExpenseTransaction> AddExpenseAsync(
        ApplicationDbContext db,
        PaymentAccountingAdapterTests.PaymentScope scope,
        ExpensePayableKind payableKind)
    {
        var expenseType = new ExpenseType
        {
            Code = PaymentAccountingAdapterTests.Unique("ET"),
            Name = PaymentAccountingAdapterTests.Unique("ExpenseType"),
            Category = "Other",
            IsActive = true,
            PayableAccountKind = payableKind
        };
        db.ExpenseTypes.Add(expenseType);
        await db.SaveChangesAsync();

        var expense = new ExpenseTransaction
        {
            ExpenseTypeId = expenseType.Id,
            ContractId = scope.Contract.Id,
            ServiceProviderId = scope.ServiceProvider.Id,
            ExpenseDate = EventDate,
            Amount = 300m,
            Currency = "USD",
            AppliedFxRateToUsd = 1m,
            AmountUsd = 300m
        };
        db.ExpenseTransactions.Add(expense);
        await db.SaveChangesAsync();
        return expense;
    }

    private static async Task<(LoadingRegister Loading, LoadingReceipt Receipt)> AddPurchaseAndReceiptAsync(
        ApplicationDbContext db,
        PaymentAccountingAdapterTests.PaymentScope scope)
    {
        var loading = await AddLoadingAsync(db, scope, quantityMt: 20m, priceUsd: 500m);
        var receipt = new LoadingReceipt
        {
            LoadingRegisterId = loading.Id,
            ReceiptDestination = LoadingReceiptDestination.ToInventory,
            TerminalId = scope.Terminal.Id,
            StorageTankId = scope.Tank.Id,
            ReceiptDate = EventDate.AddDays(2),
            ReceivedQuantityMt = 20m
        };
        db.LoadingReceipts.Add(receipt);
        await db.SaveChangesAsync();
        return (loading, receipt);
    }

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
            LoadingDate = EventDate,
            LoadedQuantityMt = quantityMt,
            LoadingPriceUsd = priceUsd,
            SettlementCurrencyCode = "USD"
        };
        db.LoadingRegisters.Add(loading);
        await db.SaveChangesAsync();
        return loading;
    }

    private static ExpenseAccountingAdapter CreateExpenseAdapter(ApplicationDbContext db, bool pilotEnabled)
    {
        var options = Options.Create(new AccountingOptions
        {
            Enabled = true,
            Pilots = new AccountingPilotOptions { Expense = pilotEnabled }
        });
        return new ExpenseAccountingAdapter(
            db,
            new AccountingPostingService(db, new PeriodGuard(db, new FiscalCalendarService(db)), options, new SystemCompanyProvider(db)),
            new AccountingJournalNumberGenerator(),
            options,
            NullLogger<ExpenseAccountingAdapter>.Instance);
    }

    private static PurchaseAccountingAdapter CreatePurchaseAdapter(
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
