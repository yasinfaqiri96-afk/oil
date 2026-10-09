using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using PTGOilSystem.Web.Controllers;
using PTGOilSystem.Web.Models.Expenses;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Audit;
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
/// Stage 5 — expenses, freight, commission. The credit account must come from the explicit
/// field on the expense type and never from the free-text Category, and the settlement must
/// always land on the same account the accrual used.
/// </summary>
[Collection(AccountingPostgreSqlCollection.CollectionName)]
[Trait("Category", "PostgreSql")]
[Trait("Category", "Integration")]
public sealed class ExpenseAccountingAdapterTests(AccountingPostgreSqlFixture fixture)
{
    private static readonly DateTime ExpenseDate = new(2026, 7, 15);

    [Theory]
    [InlineData(ExpenseSettlementMode.Payable)]
    [InlineData(ExpenseSettlementMode.PaidImmediately)]
    [InlineData(ExpenseSettlementMode.NonCash)]
    public async Task Group_Loading_Expenses_Preserve_Original_Quantity_Lineage_Settlement_And_Journals(ExpenseSettlementMode mode)
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var type = await AddExpenseTypeAsync(db, ExpensePayableKind.AccruedExpense);
        var first = new LoadingRegister { ContractId = scope.Contract.Id, ProductId = scope.Contract.ProductId,
            LoadedQuantityMt = 20m, LoadingDate = ExpenseDate, TransportType = LoadingTransportType.Wagon, WagonNumber = "GROUP-ONE" };
        var second = new LoadingRegister { ContractId = scope.Contract.Id, ProductId = scope.Contract.ProductId,
            LoadedQuantityMt = 30m, LoadingDate = ExpenseDate, TransportType = LoadingTransportType.Wagon, WagonNumber = "GROUP-TWO" };
        db.LoadingRegisters.AddRange(first, second);
        await db.SaveChangesAsync();
        // Late costs remain legitimate even after the entire loading is received.
        db.LoadingReceipts.Add(new LoadingReceipt { LoadingRegisterId = first.Id,
            ReceiptDate = ExpenseDate, ReceivedQuantityMt = 20m, TerminalId = scope.Terminal.Id });
        await db.SaveChangesAsync();
        var http = new DefaultHttpContext();
        var controller = new ExpensesController(db, new CurrencyConversionService(new PricingService(db)),
            new AuditService(db), NullLogger<ExpensesController>.Instance, CreateAdapter(db, true))
        {
            ControllerContext = new ControllerContext { HttpContext = http },
            TempData = new TempDataDictionary(http, new GroupExpenseTempDataProvider())
        };
        var model = new GroupExpenseCreateViewModel
        {
            ExpenseDate = ExpenseDate, Currency = "USD", SettlementMode = mode,
            CashAccountId = mode == ExpenseSettlementMode.PaidImmediately ? scope.CashAccount.Id : null,
            Lines = [new GroupExpenseLineInput { ExpenseTypeId = type.Id,
                ServiceProviderId = mode == ExpenseSettlementMode.Payable ? scope.ServiceProvider.Id : null,
                AllocationMethod = ExpenseAllocationMethod.ByQuantity, RatePerTon = 3m }],
            Items = [new GroupExpenseSelectedInput { Kind = "Loading", Id = first.Id },
                new GroupExpenseSelectedInput { Kind = "Loading", Id = second.Id }]
        };
        var result = await controller.CreateGroup(model, Guid.NewGuid().ToString("N"));
        Assert.IsType<RedirectToActionResult>(result);
        var expenses = await db.ExpenseTransactions.AsNoTracking()
            .Where(e => e.LoadingRegisterId == first.Id || e.LoadingRegisterId == second.Id).OrderBy(e => e.Id).ToListAsync();
        Assert.Equal(2, expenses.Count);
        Assert.Equal(new[] { 60m, 90m }, expenses.Select(e => e.AmountUsd));
        Assert.All(expenses, e =>
        {
            Assert.Equal(scope.Contract.Id, e.ContractId);
            Assert.Null(e.TransportLegId); Assert.Null(e.TruckDispatchId);
            Assert.Equal(mode, e.SettlementMode);
        });
        Assert.Equal(expenses[0].ExpenseBatchId, expenses[1].ExpenseBatchId);
        foreach (var expense in expenses)
        {
            var journal = await LoadJournalAsync(db, expense.Id);
            Assert.Equal(expense.AmountUsd, journal.Lines.Sum(l => l.Debit));
            Assert.Equal(expense.AmountUsd, journal.Lines.Sum(l => l.Credit));
            Assert.Equal(mode == ExpenseSettlementMode.PaidImmediately
                ? scope.Settings.CashBankControlAccountId : scope.Settings.AccruedExpenseAccountId,
                journal.Lines.Single(l => l.Credit > 0).AccountId);
            Assert.Single(await db.LedgerEntries.Where(l => l.SourceType == "Expense" && l.SourceId == expense.Id).ToListAsync());
        }
        await controller.CancelGroup(expenses[0].ExpenseBatchId!.Value);
        db.ChangeTracker.Clear();
        Assert.All(await db.ExpenseTransactions.Where(e => e.ExpenseBatchId == expenses[0].ExpenseBatchId).ToListAsync(),
            e => Assert.True(e.IsCancelled));
        var reversedIds = expenses.Select(e => ExpenseAccountingAdapter.BuildReversedSourceEventId(e.Id)).ToArray();
        Assert.Equal(2, await db.JournalEntries.CountAsync(j => reversedIds.Contains(j.SourceEventId!)));
    }

    private sealed class GroupExpenseTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }

    [Fact]
    public async Task Standalone_Cash_Expense_Supports_Mixed_Currency_Account_With_Locked_Fx()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db, paymentCurrency: "RUB");
        scope.CashAccount.AccountType = CashAccountType.Mixed;
        scope.CashAccount.Currency = "USD";
        await db.SaveChangesAsync();
        var type = await AddExpenseTypeAsync(db, null);
        var expense = await AddExpenseAsync(db, scope, type, e =>
        {
            e.SettlementMode = ExpenseSettlementMode.PaidImmediately;
            e.CashAccountId = scope.CashAccount.Id;
            e.Amount = 77m; e.Currency = "RUB";
            e.AppliedFxRateToUsd = 0.012987012987m; e.AmountUsd = 1m;
        });
        Assert.Equal(PaymentPostingStatus.Posted, (await CreateAdapter(db, true).TryPostExpenseAsync(expense)).Status);
        var credit = (await LoadJournalAsync(db, expense.Id)).Lines.Single(l => l.Credit > 0);
        Assert.Equal(scope.Settings.CashBankControlAccountId, credit.AccountId);
        Assert.Equal(0.012987012987m, credit.ExchangeRate);
        Assert.Equal(1m, credit.Credit);
        Assert.Equal("RUB", credit.TransactionCurrencyCode);
    }

    [Fact]
    public async Task Standalone_Paid_Expense_Credits_Cash_Once_Without_A_Payable_Or_New_Payment()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var type = await AddExpenseTypeAsync(db, null);
        var expense = await AddExpenseAsync(db, scope, type, e =>
        {
            e.SettlementMode = ExpenseSettlementMode.PaidImmediately;
            e.CashAccountId = scope.CashAccount.Id;
        });
        var adapter = CreateAdapter(db, true);
        var paymentCount = await db.PaymentTransactions.CountAsync();
        Assert.Equal(PaymentPostingStatus.Posted, (await adapter.TryPostExpenseAsync(expense)).Status);
        Assert.Equal(PaymentPostingStatus.Duplicate, (await adapter.TryPostExpenseAsync(expense)).Status);
        var journal = await LoadJournalAsync(db, expense.Id);
        var credit = Assert.Single(journal.Lines.Where(l => l.Credit > 0));
        Assert.Equal(scope.Settings.CashBankControlAccountId, credit.AccountId);
        Assert.Equal(scope.CashAccount.Id, credit.CashAccountId);
        Assert.Null(credit.PartyId);
        Assert.Equal(paymentCount, await db.PaymentTransactions.CountAsync());
        var totals = await new PTGOilSystem.Web.Services.Reporting.CashPositionReader(db)
            .ReadAccountTotalsAsync(new[] { scope.CashAccount.Id }, null);
        Assert.Equal(expense.Amount, Assert.Single(totals).NativeOut);
        Assert.Equal(PaymentPostingStatus.Posted, (await adapter.TryPostExpenseReversalAsync(expense)).Status);
        Assert.Equal(PaymentPostingStatus.Duplicate, (await adapter.TryPostExpenseReversalAsync(expense)).Status);
    }

    [Fact]
    public async Task Paid_Expense_With_Explicit_Payment_Link_Does_Not_Credit_Cash_Again()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var type = await AddExpenseTypeAsync(db, ExpensePayableKind.CommissionPayable);
        var expense = await AddExpenseAsync(db, scope, type, e =>
        {
            e.SettlementMode = ExpenseSettlementMode.PaidImmediately;
            e.CashAccountId = scope.CashAccount.Id;
        });
        await AddExpensePaymentAsync(db, scope, expense, PaymentKind.CommissionPayment);
        Assert.Equal(PaymentPostingStatus.Posted, (await CreateAdapter(db, true).TryPostExpenseAsync(expense)).Status);
        var credit = Assert.Single((await LoadJournalAsync(db, expense.Id)).Lines.Where(l => l.Credit > 0));
        Assert.Equal(scope.Settings.CommissionPayableAccountId, credit.AccountId);
        Assert.Null(credit.CashAccountId);
    }

    [Fact]
    public void SourceEventId_Format_Is_Stable()
        => Assert.Equal("Expense:7:Created", ExpenseAccountingAdapter.BuildCreatedSourceEventId(7));

    [Theory]
    [InlineData(ExpensePayableKind.AccountsPayable)]
    [InlineData(ExpensePayableKind.FreightPayable)]
    [InlineData(ExpensePayableKind.CommissionPayable)]
    [InlineData(ExpensePayableKind.AccruedExpense)]
    public async Task Credits_The_Account_Configured_On_The_Expense_Type(ExpensePayableKind kind)
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var expenseType = await AddExpenseTypeAsync(db, kind);
        var expense = await AddExpenseAsync(db, scope, expenseType, e =>
            e.ServiceProviderId = scope.ServiceProvider.Id);

        var result = await CreateAdapter(db, pilotEnabled: true).TryPostExpenseAsync(expense);

        Assert.Equal(PaymentPostingStatus.Posted, result.Status);

        var journal = await LoadJournalAsync(db, expense.Id);
        var debitLine = journal.Lines.Single(x => x.Debit > 0m);
        var creditLine = journal.Lines.Single(x => x.Credit > 0m);

        Assert.Equal(scope.Settings.GeneralExpenseAccountId, debitLine.AccountId);
        Assert.Equal(ExpectedAccountId(scope.Settings, kind), creditLine.AccountId);
        Assert.Equal(expense.AmountUsd, debitLine.Debit);
        Assert.Equal(expense.AmountUsd, creditLine.Credit);
        Assert.Equal(JournalEntryStatus.Posted, journal.Status);
    }

    [Fact]
    public async Task Category_Does_Not_Decide_The_Account()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);

        // Category says "Commission" but the explicit field says Freight Payable. The explicit
        // field must win: Category is free text a user can retype at any time.
        var expenseType = await AddExpenseTypeAsync(db, ExpensePayableKind.FreightPayable, category: "Commission");
        var expense = await AddExpenseAsync(db, scope, expenseType, e =>
            e.ServiceProviderId = scope.ServiceProvider.Id);

        Assert.Equal(PaymentPostingStatus.Posted,
            (await CreateAdapter(db, pilotEnabled: true).TryPostExpenseAsync(expense)).Status);

        var journal = await LoadJournalAsync(db, expense.Id);
        Assert.Equal(
            scope.Settings.FreightPayableAccountId,
            journal.Lines.Single(x => x.Credit > 0m).AccountId);
    }

    [Fact]
    public async Task Attaches_The_Service_Provider_As_The_Party()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var expenseType = await AddExpenseTypeAsync(db, ExpensePayableKind.AccountsPayable);
        var expense = await AddExpenseAsync(db, scope, expenseType, e =>
            e.ServiceProviderId = scope.ServiceProvider.Id);

        Assert.Equal(PaymentPostingStatus.Posted,
            (await CreateAdapter(db, pilotEnabled: true).TryPostExpenseAsync(expense)).Status);

        var creditLine = (await LoadJournalAsync(db, expense.Id)).Lines.Single(x => x.Credit > 0m);
        Assert.Equal(AccountingPartyType.ServiceProvider, creditLine.PartyType);
        Assert.Equal(scope.ServiceProvider.Id, creditLine.PartyId);
    }

    [Fact]
    public async Task Falls_Back_To_The_Driver_As_The_Party()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var expenseType = await AddExpenseTypeAsync(db, ExpensePayableKind.FreightPayable);
        var expense = await AddExpenseAsync(db, scope, expenseType, e => e.DriverId = scope.Driver.Id);

        Assert.Equal(PaymentPostingStatus.Posted,
            (await CreateAdapter(db, pilotEnabled: true).TryPostExpenseAsync(expense)).Status);

        var creditLine = (await LoadJournalAsync(db, expense.Id)).Lines.Single(x => x.Credit > 0m);
        Assert.Equal(AccountingPartyType.Driver, creditLine.PartyType);
        Assert.Equal(scope.Driver.Id, creditLine.PartyId);
    }

    [Fact]
    public async Task Posts_Without_A_Party_When_The_Expense_Has_No_External_Counterparty()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var expenseType = await AddExpenseTypeAsync(db, ExpensePayableKind.AccruedExpense);
        var expense = await AddExpenseAsync(db, scope, expenseType, _ => { });

        Assert.Equal(PaymentPostingStatus.Posted,
            (await CreateAdapter(db, pilotEnabled: true).TryPostExpenseAsync(expense)).Status);

        var journal = await LoadJournalAsync(db, expense.Id);
        var creditLine = journal.Lines.Single(x => x.Credit > 0m);
        Assert.Equal(scope.Settings.AccruedExpenseAccountId, creditLine.AccountId);
        Assert.Null(creditLine.PartyType);
        Assert.Null(creditLine.PartyId);
    }

    [Fact]
    public async Task Skips_When_The_Expense_Type_Has_No_Configured_Account()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var expenseType = await AddExpenseTypeAsync(db, payableKind: null);
        var expense = await AddExpenseAsync(db, scope, expenseType, e =>
            e.ServiceProviderId = scope.ServiceProvider.Id);

        var result = await CreateAdapter(db, pilotEnabled: true).TryPostExpenseAsync(expense);

        await AssertSkippedAsync(db, expense, result, "EXPENSE_PAYABLE_KIND_NOT_SET");
    }

    [Fact]
    public async Task Skips_A_Cancelled_Expense()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var expenseType = await AddExpenseTypeAsync(db, ExpensePayableKind.AccountsPayable);
        var expense = await AddExpenseAsync(db, scope, expenseType, e => e.IsCancelled = true);

        var result = await CreateAdapter(db, pilotEnabled: true).TryPostExpenseAsync(expense);

        await AssertSkippedAsync(db, expense, result, "EXPENSE_CANCELLED");
    }

    [Fact]
    public async Task Skips_When_The_Company_Is_Not_Provable()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var expenseType = await AddExpenseTypeAsync(db, ExpensePayableKind.AccountsPayable);
        var expense = await AddExpenseAsync(db, scope, expenseType, e =>
        {
            e.ContractId = null;
            e.ShipmentId = null;
        });

        var result = await CreateAdapter(db, pilotEnabled: true).TryPostExpenseAsync(expense);

        await AssertSkippedAsync(db, expense, result, "EXPENSE_COMPANY_UNKNOWN");
    }

    [Fact]
    public async Task Keeps_Legacy_Only_When_The_Pilot_Is_Disabled()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var expenseType = await AddExpenseTypeAsync(db, ExpensePayableKind.AccountsPayable);
        var expense = await AddExpenseAsync(db, scope, expenseType, _ => { });

        var result = await CreateAdapter(db, pilotEnabled: false).TryPostExpenseAsync(expense);

        await AssertSkippedAsync(db, expense, result, "PILOT_DISABLED");
    }

    [Fact]
    public async Task Duplicate_Source_Event_Does_Not_Create_A_Second_Journal()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var expenseType = await AddExpenseTypeAsync(db, ExpensePayableKind.AccountsPayable);
        var expense = await AddExpenseAsync(db, scope, expenseType, _ => { });
        var adapter = CreateAdapter(db, pilotEnabled: true);

        Assert.Equal(PaymentPostingStatus.Posted, (await adapter.TryPostExpenseAsync(expense)).Status);
        var second = await adapter.TryPostExpenseAsync(expense);

        Assert.Equal(PaymentPostingStatus.Duplicate, second.Status);
        Assert.Equal(1, await db.JournalEntries.CountAsync(
            x => x.SourceEventId == ExpenseAccountingAdapter.BuildCreatedSourceEventId(expense.Id)));
    }

    [Fact]
    public async Task Settlement_Debits_Exactly_The_Account_The_Accrual_Credited()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var expenseType = await AddExpenseTypeAsync(db, ExpensePayableKind.FreightPayable);
        var expense = await AddExpenseAsync(db, scope, expenseType, e =>
            e.ServiceProviderId = scope.ServiceProvider.Id);

        Assert.Equal(PaymentPostingStatus.Posted,
            (await CreateAdapter(db, pilotEnabled: true).TryPostExpenseAsync(expense)).Status);
        var accrualCredit = (await LoadJournalAsync(db, expense.Id)).Lines.Single(x => x.Credit > 0m);

        var payment = await AddExpensePaymentAsync(db, scope, expense, PaymentKind.ExpensePayment);
        var result = await PaymentAccountingAdapterTests
            .CreateAdapter(db, PaymentAccountingAdapterTests.PilotsFor(expensePayment: true))
            .TryPostPaymentAsync(payment);

        Assert.Equal(PaymentPostingStatus.Posted, result.Status);
        Assert.Equal(PaymentAccountingEventKind.ExpensePayment, result.EventKind);

        var settlement = await LoadPaymentJournalAsync(db, payment.Id);
        var settlementDebit = settlement.Lines.Single(x => x.Debit > 0m);
        var settlementCredit = settlement.Lines.Single(x => x.Credit > 0m);

        // The liability must be cleared on the same account and party it was raised on.
        Assert.Equal(accrualCredit.AccountId, settlementDebit.AccountId);
        Assert.Equal(accrualCredit.PartyType, settlementDebit.PartyType);
        Assert.Equal(accrualCredit.PartyId, settlementDebit.PartyId);
        Assert.Equal(scope.Settings.CashBankControlAccountId, settlementCredit.AccountId);
        Assert.Equal(scope.CashAccount.Id, settlementCredit.CashAccountId);
        Assert.Equal(expense.AmountUsd, settlementDebit.Debit);
    }

    [Fact]
    public async Task Commission_Settlement_Clears_The_Commission_Payable()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var expenseType = await AddExpenseTypeAsync(db, ExpensePayableKind.CommissionPayable, category: "Commission");
        var expense = await AddExpenseAsync(db, scope, expenseType, _ => { });

        Assert.Equal(PaymentPostingStatus.Posted,
            (await CreateAdapter(db, pilotEnabled: true).TryPostExpenseAsync(expense)).Status);

        var payment = await AddExpensePaymentAsync(db, scope, expense, PaymentKind.CommissionPayment);
        var result = await PaymentAccountingAdapterTests
            .CreateAdapter(db, PaymentAccountingAdapterTests.PilotsFor(commissionPayment: true))
            .TryPostPaymentAsync(payment);

        Assert.Equal(PaymentPostingStatus.Posted, result.Status);

        var settlement = await LoadPaymentJournalAsync(db, payment.Id);
        Assert.Equal(
            scope.Settings.CommissionPayableAccountId,
            settlement.Lines.Single(x => x.Debit > 0m).AccountId);
        Assert.Equal(
            scope.Settings.CashBankControlAccountId,
            settlement.Lines.Single(x => x.Credit > 0m).AccountId);
    }

    [Fact]
    public async Task Settlement_Skips_When_The_Payment_Has_No_Linked_Expense()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var expenseType = await AddExpenseTypeAsync(db, ExpensePayableKind.AccountsPayable);
        var expense = await AddExpenseAsync(db, scope, expenseType, _ => { });

        var payment = await AddExpensePaymentAsync(db, scope, expense, PaymentKind.ExpensePayment);
        payment.ExpenseTransactionId = null;
        await db.SaveChangesAsync();

        var result = await PaymentAccountingAdapterTests
            .CreateAdapter(db, PaymentAccountingAdapterTests.PilotsFor(expensePayment: true))
            .TryPostPaymentAsync(payment);

        Assert.Equal(PaymentPostingStatus.Skipped, result.Status);
        Assert.Equal("EXPENSE_LINK_MISSING", result.Reason);
    }

    private static int ExpectedAccountId(AccountingSettings settings, ExpensePayableKind kind)
        => kind switch
        {
            ExpensePayableKind.AccountsPayable => settings.AccountsPayableAccountId,
            ExpensePayableKind.FreightPayable => settings.FreightPayableAccountId,
            ExpensePayableKind.CommissionPayable => settings.CommissionPayableAccountId,
            ExpensePayableKind.AccruedExpense => settings.AccruedExpenseAccountId,
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };

    private static async Task AssertSkippedAsync(
        ApplicationDbContext db,
        ExpenseTransaction expense,
        ExpenseAccountingResult result,
        string expectedReason)
    {
        Assert.Equal(PaymentPostingStatus.Skipped, result.Status);
        Assert.Equal(expectedReason, result.Reason);
        Assert.Null(result.Journal);
        Assert.Equal(0, await db.JournalEntries.CountAsync(
            x => x.SourceEventId == ExpenseAccountingAdapter.BuildCreatedSourceEventId(expense.Id)));
    }

    private static async Task<JournalEntry> LoadJournalAsync(ApplicationDbContext db, int expenseId)
        => await db.JournalEntries
            .AsNoTracking()
            .Include(x => x.Lines)
            .SingleAsync(x => x.SourceModule == ExpenseAccountingAdapter.SourceModule
                && x.SourceEventId == ExpenseAccountingAdapter.BuildCreatedSourceEventId(expenseId));

    private static async Task<JournalEntry> LoadPaymentJournalAsync(ApplicationDbContext db, int paymentId)
        => await db.JournalEntries
            .AsNoTracking()
            .Include(x => x.Lines)
            .SingleAsync(x => x.SourceModule == PaymentAccountingAdapter.SourceModule
                && x.SourceEventId == PaymentAccountingAdapter.BuildCreatedSourceEventId(paymentId));

    private static async Task<ExpenseType> AddExpenseTypeAsync(
        ApplicationDbContext db,
        ExpensePayableKind? payableKind,
        string category = "Other")
    {
        var expenseType = new ExpenseType
        {
            Code = PaymentAccountingAdapterTests.Unique("ET"),
            Name = PaymentAccountingAdapterTests.Unique("ExpenseType"),
            Category = category,
            IsActive = true,
            PayableAccountKind = payableKind
        };
        db.ExpenseTypes.Add(expenseType);
        await db.SaveChangesAsync();
        return expenseType;
    }

    private static async Task<ExpenseTransaction> AddExpenseAsync(
        ApplicationDbContext db,
        PaymentAccountingAdapterTests.PaymentScope scope,
        ExpenseType expenseType,
        Action<ExpenseTransaction> configure)
    {
        var expense = new ExpenseTransaction
        {
            ExpenseTypeId = expenseType.Id,
            ContractId = scope.Contract.Id,
            ExpenseDate = ExpenseDate,
            Amount = 300m,
            Currency = "USD",
            AppliedFxRateToUsd = 1m,
            AmountUsd = 300m,
            Description = "Stage 5 test expense"
        };
        configure(expense);

        db.ExpenseTransactions.Add(expense);
        await db.SaveChangesAsync();
        return expense;
    }

    private static async Task<PaymentTransaction> AddExpensePaymentAsync(
        ApplicationDbContext db,
        PaymentAccountingAdapterTests.PaymentScope scope,
        ExpenseTransaction expense,
        PaymentKind paymentKind)
    {
        var payment = new PaymentTransaction
        {
            PaymentDate = ExpenseDate,
            Direction = PaymentDirection.Out,
            PaymentKind = paymentKind,
            CashAccountId = scope.CashAccount.Id,
            ExpenseTransactionId = expense.Id,
            ContractId = scope.Contract.Id,
            Amount = expense.Amount,
            Currency = expense.Currency,
            AppliedFxRateToUsd = expense.AppliedFxRateToUsd,
            AmountUsd = expense.AmountUsd
        };
        db.PaymentTransactions.Add(payment);
        await db.SaveChangesAsync();
        return payment;
    }

    private static ExpenseAccountingAdapter CreateAdapter(ApplicationDbContext db, bool pilotEnabled)
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
}
