using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PTGOilSystem.Web.Controllers;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.Payments;
using PTGOilSystem.Web.Models.Reports;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Reporting;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// «ماندهٔ نقدی» یک مفهوم است و یک مرجع دارد (<see cref="CashPositionReader"/>). کارت‌های مالی،
/// روزنامچه، جزئیات صندوق، گردش پول و وضعیت مالی شرکت برای همان داده باید یک عدد بدهند.
/// </summary>
public sealed class CashPositionCanonicalTests
{
    // 1000 رسید − 200 پرداخت − 50 کمیسیون (سند روزنامچه + مصرفِ پیوندی، یک بار) − 100 گمرکِ نقدی = 650
    private const decimal ExpectedBalanceUsd = 650m;

    [Fact]
    public async Task Reader_Counts_Payments_And_Standalone_Cash_Expenses_Without_Double_Counting_Commission()
    {
        await using var db = NewDb();
        await SeedLifecycleAsync(db);

        var totals = await new CashPositionReader(db).ReadAccountTotalsAsync();

        var account = Assert.Single(totals);
        Assert.Equal(1000m, account.TotalIn);
        Assert.Equal(350m, account.TotalOut);
        Assert.Equal("USD", account.TotalsCurrency);
        Assert.Equal(ExpectedBalanceUsd, account.BalanceUsd);
        Assert.Equal(ExpectedBalanceUsd, CashPositionReader.TotalBalanceUsd(totals));
    }

    [Fact]
    public async Task Opening_Balance_Uses_Only_Documents_Before_The_Date()
    {
        await using var db = NewDb();
        await SeedLifecycleAsync(db);

        var opening = await new CashPositionReader(db).ReadAccountTotalsAsync(null, new DateTime(2026, 6, 3));

        Assert.Equal(800m, CashPositionReader.TotalBalanceUsd(opening));
    }

    [Fact]
    public async Task Finance_Cards_Do_Not_Subtract_Commission_Twice()
    {
        await using var db = NewDb();
        await SeedLifecycleAsync(db);

        var cards = await FinanceMetricCardsQuery.BuildAsync(db);

        Assert.Equal(ExpectedBalanceUsd, cards.CashAccountsBalanceUsd);
    }

    [Fact]
    public async Task Every_Cash_Balance_Consumer_Returns_The_Same_Number()
    {
        await using var db = NewDb();
        await SeedLifecycleAsync(db);

        var details = Assert.IsType<ViewResult>(await BuildCashAccountsController(db).Details(1));
        var paymentsIndex = Assert.IsType<PaymentIndexViewModel>(
            Assert.IsType<ViewResult>(await BuildPaymentsController(db).Index()).Model);
        var reports = BuildReportsController(db);
        var cashFlow = Assert.IsType<CashFlowReportViewModel>(
            Assert.IsType<ViewResult>(await reports.CashFlow()).Model);
        var overview = Assert.IsType<CompanyFinancialOverviewViewModel>(
            Assert.IsType<ViewResult>(await reports.CompanyOverview()).Model);

        Assert.Equal(ExpectedBalanceUsd, (decimal)details.ViewData["ClosingBalance"]!);
        Assert.Equal(1000m, (decimal)details.ViewData["TotalIn"]!);
        Assert.Equal(350m, (decimal)details.ViewData["TotalOut"]!);
        Assert.Equal(ExpectedBalanceUsd, paymentsIndex.CashAccountsBalanceUsd);
        Assert.Equal(ExpectedBalanceUsd, cashFlow.ClosingBalanceUsd);
        Assert.Equal(ExpectedBalanceUsd, overview.CashOnHandUsd);
        Assert.Equal(ExpectedBalanceUsd, overview.NetCashMovementUsd);
    }

    [Fact]
    public async Task Cash_Flow_Opening_Balance_Matches_Reader_And_Unlinked_Payments_Stay_Visible_But_Uncounted()
    {
        await using var db = NewDb();
        await SeedLifecycleAsync(db);

        var cashFlow = Assert.IsType<CashFlowReportViewModel>(Assert.IsType<ViewResult>(
            await BuildReportsController(db).CashFlow(new ManagementReportFilterViewModel
            {
                FromDate = new DateTime(2026, 6, 3)
            })).Model);

        Assert.Equal(800m, cashFlow.OpeningBalanceUsd);
        Assert.Equal(ExpectedBalanceUsd, cashFlow.ClosingBalanceUsd);
        Assert.DoesNotContain(cashFlow.AccountRows, row => row.CashAccountName == "بدون حساب نقدی");
        Assert.Contains("1 سند روزنامچه", cashFlow.ScopeWarning);
    }

    [Fact]
    public async Task Account_In_Its_Own_Currency_Shows_Native_Totals_Until_A_Foreign_Document_Appears()
    {
        await using var db = NewDb();
        db.CashAccounts.Add(new CashAccount { Id = 2, Code = "AFN", Name = "AFN Cash", Currency = "AFN", AccountType = CashAccountType.Cash });
        db.ExpenseTypes.Add(new ExpenseType { Id = 1, Code = "OPS", Name = "Operations" });
        db.PaymentTransactions.Add(Payment(10, 2, PaymentDirection.In, 7000m, "AFN", 100m));
        db.ExpenseTransactions.Add(Expense(10, 2, 700m, "AFN", 10m));
        await db.SaveChangesAsync();

        var native = Assert.Single(await new CashPositionReader(db).ReadAccountTotalsAsync());
        Assert.False(native.UsesUsdTotals);
        Assert.Equal("AFN", native.TotalsCurrency);
        Assert.Equal(6300m, native.Balance);
        Assert.Equal(90m, native.BalanceUsd);

        db.PaymentTransactions.Add(Payment(11, 2, PaymentDirection.In, 50m, "USD", 50m));
        await db.SaveChangesAsync();

        var mixed = Assert.Single(await new CashPositionReader(db).ReadAccountTotalsAsync());
        Assert.True(mixed.UsesUsdTotals);
        Assert.Equal("USD", mixed.TotalsCurrency);
        Assert.Equal(150m, mixed.TotalIn);
        Assert.Equal(10m, mixed.TotalOut);
        Assert.Equal(140m, mixed.Balance);
    }

    [Fact]
    public async Task Foreign_Document_Without_Usd_Equivalent_Is_Surfaced_Not_Hidden()
    {
        await using var db = NewDb();
        db.CashAccounts.Add(new CashAccount { Id = 2, Code = "AFN", Name = "AFN Cash", Currency = "AFN", AccountType = CashAccountType.Cash });
        db.PaymentTransactions.Add(Payment(12, 2, PaymentDirection.In, 500m, "AFN", 0m));
        await db.SaveChangesAsync();

        var account = Assert.Single(await new CashPositionReader(db).ReadAccountTotalsAsync());
        var cards = await FinanceMetricCardsQuery.BuildAsync(db);

        Assert.Equal(1, account.MissingUsdEquivalentCount);
        Assert.Equal(1, cards.CashBalanceMissingUsdEquivalentCount);
    }

    private static async Task SeedLifecycleAsync(ApplicationDbContext db)
    {
        db.CashAccounts.Add(new CashAccount { Id = 1, Code = "CASH", Name = "Main Cash", Currency = "USD", AccountType = CashAccountType.Cash });
        db.ExpenseTypes.Add(new ExpenseType { Id = 1, Code = "OPS", Name = "Operations" });

        db.PaymentTransactions.AddRange(
            Payment(1, 1, PaymentDirection.In, 1000m, "USD", 1000m, new DateTime(2026, 6, 1), PaymentKind.ManualReceipt),
            Payment(2, 1, PaymentDirection.Out, 200m, "USD", 200m, new DateTime(2026, 6, 2), PaymentKind.SupplierPayment),
            // خروجِ نقدیِ کمیسیون: هم سند روزنامچه دارد هم مصرفِ پیوندی (Expense 1).
            Payment(3, 1, PaymentDirection.Out, 50m, "USD", 50m, new DateTime(2026, 6, 3), PaymentKind.CommissionPayment, expenseId: 1),
            // شریک از جیب خودش داده؛ صندوق شرکت حرکت نکرده.
            Payment(4, null, PaymentDirection.Out, 300m, "USD", 300m, new DateTime(2026, 6, 4), PaymentKind.SupplierPayment, funding: PaymentFundingSource.Partner),
            // سند شرکتِ بی‌صندوق: مغایرت است، نه حرکتِ صندوق.
            Payment(5, null, PaymentDirection.Out, 40m, "USD", 40m, new DateTime(2026, 6, 4), PaymentKind.ManualPayment));

        db.ExpenseTransactions.AddRange(
            Expense(1, 1, 50m, "USD", 50m, new DateTime(2026, 6, 3)),
            // گمرکِ نقدی بدون سند روزنامچه.
            Expense(2, 1, 100m, "USD", 100m, new DateTime(2026, 6, 4)),
            Expense(3, 1, 70m, "USD", 70m, new DateTime(2026, 6, 4), cancelled: true),
            Expense(4, 1, 80m, "USD", 80m, new DateTime(2026, 6, 4), mode: ExpenseSettlementMode.Payable));

        await db.SaveChangesAsync();
    }

    private static PaymentTransaction Payment(
        int id,
        int? cashAccountId,
        PaymentDirection direction,
        decimal amount,
        string currency,
        decimal amountUsd,
        DateTime? date = null,
        PaymentKind kind = PaymentKind.ManualReceipt,
        int? expenseId = null,
        PaymentFundingSource funding = PaymentFundingSource.Company)
        => new()
        {
            Id = id,
            PaymentDate = date ?? new DateTime(2026, 6, 1),
            Direction = direction,
            PaymentKind = kind,
            CashAccountId = cashAccountId,
            FundingSource = funding,
            ExpenseTransactionId = expenseId,
            Amount = amount,
            Currency = currency,
            AmountUsd = amountUsd
        };

    private static ExpenseTransaction Expense(
        int id,
        int cashAccountId,
        decimal amount,
        string currency,
        decimal amountUsd,
        DateTime? date = null,
        bool cancelled = false,
        ExpenseSettlementMode mode = ExpenseSettlementMode.PaidImmediately)
        => new()
        {
            Id = id,
            ExpenseTypeId = 1,
            ExpenseDate = date ?? new DateTime(2026, 6, 1),
            SettlementMode = mode,
            CashAccountId = cashAccountId,
            Amount = amount,
            Currency = currency,
            AmountUsd = amountUsd,
            IsCancelled = cancelled
        };

    private static ApplicationDbContext NewDb()
        => new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static CashAccountsController BuildCashAccountsController(ApplicationDbContext db)
        => new(db, new AuditService(db))
        {
            TempData = new TempDataDictionary(new DefaultHttpContext(), new EmptyTempDataProvider())
        };

    private static PaymentsController BuildPaymentsController(ApplicationDbContext db)
        => new(db, new PricingService(db), new AuditService(db), NullLogger<PaymentsController>.Instance)
        {
            TempData = new TempDataDictionary(new DefaultHttpContext(), new EmptyTempDataProvider())
        };

    private static ReportsController BuildReportsController(ApplicationDbContext db)
        => new(db)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

    private sealed class EmptyTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();

        public void SaveTempData(HttpContext context, IDictionary<string, object> values)
        {
        }
    }
}
