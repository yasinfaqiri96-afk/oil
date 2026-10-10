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
using PTGOilSystem.Web.Services.Expenses;
using PTGOilSystem.Web.Services.Ledger;
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
        var requestToken = Guid.NewGuid().ToString("N");
        var result = await controller.CreateGroup(model, requestToken);
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
        var retry = await controller.CreateGroup(model, requestToken);
        Assert.IsType<RedirectToActionResult>(retry);
        db.ChangeTracker.Clear();
        Assert.Equal(2, await db.ExpenseTransactions.CountAsync(e => e.LoadingRegisterId == first.Id || e.LoadingRegisterId == second.Id));
        Assert.Single(await db.ExpenseBatches.Where(b => b.Id == expenses[0].ExpenseBatchId).ToListAsync());
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
    public async Task Group_Expense_Failure_After_First_Real_Journal_Rolls_Back_All_Shares_Ledgers_And_Audit()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var type = await AddExpenseTypeAsync(db, ExpensePayableKind.AccountsPayable);
        var first = new LoadingRegister { ContractId = scope.Contract.Id, ProductId = scope.Product.Id,
            LoadedQuantityMt = 20m, LoadingDate = ExpenseDate };
        var second = new LoadingRegister { ContractId = scope.Contract.Id, ProductId = scope.Product.Id,
            LoadedQuantityMt = 30m, LoadingDate = ExpenseDate };
        db.LoadingRegisters.AddRange(first, second); await db.SaveChangesAsync();
        var auditCount = await db.AuditLogs.CountAsync();
        var adapter = new FailSecondExpenseAdapter(CreateAdapter(db, true));
        var http = new DefaultHttpContext();
        var controller = new ExpensesController(db, new CurrencyConversionService(new PricingService(db)),
            new AuditService(db), NullLogger<ExpensesController>.Instance, adapter)
        { ControllerContext = new ControllerContext { HttpContext = http },
            TempData = new TempDataDictionary(http, new GroupExpenseTempDataProvider()) };
        var requestToken = Guid.NewGuid().ToString("N");
        var result = await controller.CreateGroup(new GroupExpenseCreateViewModel {
            ExpenseDate = ExpenseDate, Currency = "USD", SettlementMode = ExpenseSettlementMode.Payable,
            Lines = [new GroupExpenseLineInput { ExpenseTypeId = type.Id, ServiceProviderId = scope.ServiceProvider.Id,
                AllocationMethod = ExpenseAllocationMethod.FixedPerOperation, AmountPerOperation = 10m }],
            Items = [new GroupExpenseSelectedInput { Kind = "Loading", Id = first.Id },
                new GroupExpenseSelectedInput { Kind = "Loading", Id = second.Id }] }, requestToken);
        Assert.IsType<ViewResult>(result);
        Assert.False(controller.ModelState.IsValid);
        Assert.Equal(1, adapter.PostedBeforeFailure);
        await using var verified = fixture.CreateDbContext();
        Assert.Equal(0, await verified.ExpenseTransactions.CountAsync(e => e.LoadingRegisterId == first.Id || e.LoadingRegisterId == second.Id));
        Assert.Equal(0, await verified.ExpenseBatches.CountAsync(b => b.ExpenseTypeId == type.Id));
        Assert.Equal(0, await verified.LedgerEntries.CountAsync(l => l.ContractId == scope.Contract.Id && l.SourceType == "Expense"));
        Assert.Equal(0, await verified.JournalEntries.CountAsync(j => j.CompanyId == scope.Company.Id
            && j.SourceModule == ExpenseAccountingAdapter.SourceModule));
        Assert.Equal(auditCount, await verified.AuditLogs.CountAsync());
        Assert.Equal(0, await verified.ProcessedFormTokens.CountAsync(t => t.Token == requestToken));
    }

    private sealed class FailSecondExpenseAdapter(IExpenseAccountingAdapter inner) : IExpenseAccountingAdapter
    {
        private int calls;
        public int PostedBeforeFailure { get; private set; }
        public async Task<ExpenseAccountingResult> TryPostExpenseAsync(ExpenseTransaction expense, CancellationToken cancellationToken = default)
        {
            if (++calls == 2) throw new InvalidOperationException("Injected failure after the first real accounting post");
            var result = await inner.TryPostExpenseAsync(expense, cancellationToken);
            if (result.Status == PaymentPostingStatus.Posted) PostedBeforeFailure++;
            return result;
        }
        public Task<ExpenseAccountingResult> TryPostExpenseReversalAsync(ExpenseTransaction expense, CancellationToken cancellationToken = default)
            => inner.TryPostExpenseReversalAsync(expense, cancellationToken);
        public Task<int?> ResolvePayableAccountIdAsync(ExpenseTransaction expense, int companyId, CancellationToken cancellationToken = default)
            => inner.ResolvePayableAccountIdAsync(expense, companyId, cancellationToken);
    }

    [Fact]
    public async Task Group_Expense_For_MultiSource_Transport_Preserves_Contract_Shares_And_Exact_Total()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var secondContract = new Contract { CompanyId = scope.Company.Id, ProductId = scope.Product.Id,
            SupplierId = scope.Supplier.Id, ContractNumber = PaymentAccountingAdapterTests.Unique("MULTI"),
            ContractType = ContractType.Purchase, Status = ContractStatus.Active, ContractDate = ExpenseDate,
            QuantityMt = 100m, PricingMethod = PricingMethod.Fixed, UnitPriceUsd = 500m };
        db.Contracts.Add(secondContract); await db.SaveChangesAsync();
        var firstLoading = new LoadingRegister { ContractId = scope.Contract.Id, ProductId = scope.Product.Id,
            LoadedQuantityMt = 40m, LoadingDate = ExpenseDate };
        var secondLoading = new LoadingRegister { ContractId = secondContract.Id, ProductId = scope.Product.Id,
            LoadedQuantityMt = 60m, LoadingDate = ExpenseDate };
        db.LoadingRegisters.AddRange(firstLoading, secondLoading); await db.SaveChangesAsync();
        var leg = new InventoryTransportLeg { SourcePurchaseContractId = scope.Contract.Id, ProductId = scope.Product.Id,
            SourceTerminalId = scope.Terminal.Id, TransportType = LoadingTransportType.Truck,
            QuantityMt = 100m, LoadedDate = ExpenseDate, Status = InventoryTransportLegStatus.Loaded,
            Allocations = [new InventoryTransportLegAllocation { SourcePurchaseContractId = scope.Contract.Id,
                SourceLoadingRegisterId = firstLoading.Id, QuantityMt = 40m },
                new InventoryTransportLegAllocation { SourcePurchaseContractId = secondContract.Id,
                    SourceLoadingRegisterId = secondLoading.Id, QuantityMt = 60m }] };
        db.InventoryTransportLegs.Add(leg); await db.SaveChangesAsync();
        var type = await AddExpenseTypeAsync(db, ExpensePayableKind.AccountsPayable);
        var http = new DefaultHttpContext();
        var controller = new ExpensesController(db, new CurrencyConversionService(new PricingService(db)),
            new AuditService(db), NullLogger<ExpensesController>.Instance, CreateAdapter(db, true))
        { ControllerContext = new ControllerContext { HttpContext = http },
            TempData = new TempDataDictionary(http, new GroupExpenseTempDataProvider()) };
        var result = await controller.CreateGroup(new GroupExpenseCreateViewModel {
            ExpenseDate = ExpenseDate, Currency = "USD", SettlementMode = ExpenseSettlementMode.Payable,
            Lines = [new GroupExpenseLineInput { ExpenseTypeId = type.Id, ServiceProviderId = scope.ServiceProvider.Id,
                AllocationMethod = ExpenseAllocationMethod.FixedPerOperation, AmountPerOperation = 100.01m }],
            Items = [new GroupExpenseSelectedInput { Kind = "Leg", Id = leg.Id }] }, Guid.NewGuid().ToString("N"));
        Assert.IsType<RedirectToActionResult>(result);
        db.ChangeTracker.Clear();
        var expenses = await db.ExpenseTransactions.Where(e => e.TransportLegId == leg.Id).OrderBy(e => e.ContractId).ToListAsync();
        Assert.Equal(2, expenses.Count);
        Assert.Equal(new[] { scope.Contract.Id, secondContract.Id }, expenses.Select(e => e.ContractId!.Value));
        Assert.Equal(new[] { 40m, 60.01m }, expenses.Select(e => e.Amount));
        Assert.Equal(100.01m, expenses.Sum(e => e.AmountUsd));
        Assert.Equal(1, (await db.ExpenseBatches.SingleAsync(b => b.Id == expenses[0].ExpenseBatchId)).OperationCount);
        Assert.Equal(2, await db.InventoryTransportLegAllocations.CountAsync(a => a.InventoryTransportLegId == leg.Id
            && (a.SourceLoadingRegisterId == firstLoading.Id || a.SourceLoadingRegisterId == secondLoading.Id)));
        Assert.Equal(0, await db.InventoryMovements.CountAsync(m => m.ContractId == secondContract.Id));
        foreach (var expense in expenses)
        {
            var journal = await LoadJournalAsync(db, expense.Id);
            Assert.Equal(expense.AmountUsd, journal.Lines.Sum(l => l.Debit));
            Assert.Equal(expense.AmountUsd, journal.Lines.Sum(l => l.Credit));
            Assert.All(journal.Lines, l => Assert.Equal(expense.ContractId, l.ContractId));
        }
    }

    [Fact]
    public async Task Accounting_Rejects_Payment_For_Expense_Already_Posted_To_Cash()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var type = await AddExpenseTypeAsync(db, ExpensePayableKind.AccruedExpense);
        var expense = await AddExpenseAsync(db, scope, type, e =>
        {
            e.SettlementMode = ExpenseSettlementMode.PaidImmediately;
            e.CashAccountId = scope.CashAccount.Id;
        });
        Assert.Equal(PaymentPostingStatus.Posted, (await CreateAdapter(db, true).TryPostExpenseAsync(expense)).Status);
        var secondPayment = await AddExpensePaymentAsync(db, scope, expense, PaymentKind.ExpensePayment);
        var result = await PaymentAccountingAdapterTests.CreateAdapter(db,
            PaymentAccountingAdapterTests.PilotsFor(expensePayment: true)).TryPostPaymentAsync(secondPayment);
        Assert.Equal(PaymentPostingStatus.Skipped, result.Status);
        Assert.Equal("EXPENSE_ALREADY_PAID_CASH", result.Reason);
        Assert.Equal(0, await db.JournalEntries.CountAsync(j => j.SourceModule == PaymentAccountingAdapter.SourceModule
            && j.SourceEntityId == secondPayment.Id));
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

    [Fact]
    public async Task Editing_The_Amount_Of_An_Expense_With_A_Posted_Journal_Cannot_Split_Ledger_From_Journal()
    {
        await using var db = fixture.CreateDbContext();
        var (expense, adapter) = await PostPayableExpenseAsync(db);

        var form = await EditFormAsync(db, adapter, expense.Id);
        form.Amount = 450m;
        var controller = EditController(db, adapter);
        Assert.IsType<ViewResult>(await controller.Edit(expense.Id, form));
        Assert.False(controller.ModelState.IsValid);
        db.ChangeTracker.Clear();

        // The operational ledger and the posted general-ledger journal describe the same amount.
        var journal = await LoadJournalAsync(db, expense.Id);
        var ledger = await db.LedgerEntries.AsNoTracking().SingleAsync(l => l.SourceType == "Expense" && l.SourceId == expense.Id);
        var stored = await db.ExpenseTransactions.AsNoTracking().SingleAsync(e => e.Id == expense.Id);
        Assert.Equal(300m, journal.Lines.Sum(l => l.Debit));
        Assert.Equal(300m, ledger.AmountUsd);
        Assert.Equal(300m, stored.AmountUsd);
    }

    [Fact]
    public async Task Editing_Only_The_Description_Of_An_Expense_With_A_Posted_Journal_Is_Allowed()
    {
        await using var db = fixture.CreateDbContext();
        var (expense, adapter) = await PostPayableExpenseAsync(db);

        var form = await EditFormAsync(db, adapter, expense.Id);
        form.Description = "Corrected description";
        var controller = EditController(db, adapter);
        Assert.IsType<RedirectToActionResult>(await controller.Edit(expense.Id, form));
        db.ChangeTracker.Clear();

        var stored = await db.ExpenseTransactions.AsNoTracking().SingleAsync(e => e.Id == expense.Id);
        Assert.Equal("Corrected description", stored.Description);
        Assert.Equal(300m, stored.AmountUsd);
        Assert.Equal(300m, (await LoadJournalAsync(db, expense.Id)).Lines.Sum(l => l.Debit));
    }

    /// <summary>Posts one payable expense through the canonical share posting (ledger, journal, audit).</summary>
    private static async Task<(ExpenseTransaction Expense, ExpenseAccountingAdapter Adapter)> PostPayableExpenseAsync(ApplicationDbContext db)
    {
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var expenseType = await AddExpenseTypeAsync(db, ExpensePayableKind.AccruedExpense);
        var adapter = CreateAdapter(db, pilotEnabled: true);
        var expense = new ExpenseTransaction
        {
            ExpenseTypeId = expenseType.Id,
            ContractId = scope.Contract.Id,
            ServiceProviderId = scope.ServiceProvider.Id,
            ExpenseDate = ExpenseDate,
            Amount = 300m,
            Currency = "USD",
            AppliedFxRateToUsd = 1m,
            AmountUsd = 300m,
            Description = "Posted expense"
        };
        var conversion = await new CurrencyConversionService(new PricingService(db)).ResolveToBaseAsync("USD", ExpenseDate, null);
        var posting = new GroupExpensePostingService(db, new ExpenseSettlementValidator(),
            new ExpenseLedgerPoster(new LedgerPostingService(db)), new AuditService(db), adapter);
        var result = await posting.PostShareAsync(expense, expenseType, conversion, ExpenseSettlementMode.Payable);
        Assert.Equal(PaymentPostingStatus.Posted, result!.Status);
        db.ChangeTracker.Clear();
        return (expense, adapter);
    }

    private static async Task<ExpenseCreateViewModel> EditFormAsync(ApplicationDbContext db, ExpenseAccountingAdapter adapter, int expenseId)
    {
        var view = Assert.IsType<ViewResult>(await EditController(db, adapter).Edit(expenseId));
        db.ChangeTracker.Clear();
        return Assert.IsType<ExpenseCreateViewModel>(view.Model);
    }

    private static ExpensesController EditController(ApplicationDbContext db, ExpenseAccountingAdapter adapter)
    {
        var context = new DefaultHttpContext();
        return new ExpensesController(db, new CurrencyConversionService(new PricingService(db)),
            new AuditService(db), NullLogger<ExpensesController>.Instance, adapter)
        {
            ControllerContext = new ControllerContext { HttpContext = context },
            TempData = new TempDataDictionary(context, new GroupExpenseTempDataProvider())
        };
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
