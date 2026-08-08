using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using System.IO;
using PTGOilSystem.Web.Controllers;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.AccountStatements;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services;
using Xunit;

namespace PTGOilSystem.Web.Tests;

public class AccountStatementsControllerTests
{
    [Fact]
    public async Task Index_Returns_Deterministic_Running_Balance_With_Source_Currency_Trace()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        db.LedgerEntries.AddRange(
            new LedgerEntry
            {
                Id = 1,
                EntryDate = new DateTime(2026, 1, 1),
                Side = LedgerSide.Credit,
                AmountUsd = 1000m,
                Currency = "USD",
                SourceAmount = 1000m,
                SourceCurrencyCode = "USD",
                AppliedFxRateToUsd = 1m,
                AppliedFxRateDate = new DateTime(2026, 1, 1),
                Description = "Opening",
                SourceType = "OpeningBalance",
                SourceId = 1,
                Reference = "OB-1"
            },
            new LedgerEntry
            {
                Id = 2,
                EntryDate = new DateTime(2026, 1, 2),
                Side = LedgerSide.Debit,
                AmountUsd = 250m,
                Currency = "USD",
                SourceAmount = 250m,
                SourceCurrencyCode = "USD",
                AppliedFxRateToUsd = 1m,
                AppliedFxRateDate = new DateTime(2026, 1, 2),
                Description = "Adjustment",
                SourceType = "ManualAdjustment",
                SourceId = 2,
                Reference = "ADJ-1"
            });
        await db.SaveChangesAsync();

        var controller = BuildController(db);

        var result = await controller.Index(new AccountStatementFilterViewModel
        {
            FromDate = new DateTime(2026, 1, 1),
            ToDate = new DateTime(2026, 1, 2)
        });

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<AccountStatementIndexViewModel>(view.Model);
        Assert.Equal(2, model.Items.Count);
        Assert.Equal(1000m, model.Items[0].RunningBalanceUsd);
        Assert.Equal(750m, model.Items[1].RunningBalanceUsd);
        Assert.Equal("USD", model.Items[0].SourceCurrencyCode);
        Assert.Equal(1m, model.Items[0].AppliedFxRateToUsd);
    }

    [Fact]
    public async Task Create_Manual_Adjustment_Uses_Daily_Fx_Rate_And_Audits()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        db.Currencies.AddRange(
            new Currency { Id = 1, Code = "USD", Name = "US Dollar", Symbol = "$", IsActive = true },
            new Currency { Id = 2, Code = "EUR", Name = "Euro", Symbol = "EUR", IsActive = true });
        db.DailyFxRates.Add(new DailyFxRate
        {
            Id = 1,
            BaseCurrency = "EUR",
            QuoteCurrency = "USD",
            RateDate = new DateTime(2026, 1, 1),
            Rate = 1.2m,
            Source = "Test FX"
        });
        await db.SaveChangesAsync();

        var controller = BuildController(db);

        var result = await controller.Create(new AccountStatementCreateViewModel
        {
            EntryDate = new DateTime(2026, 1, 2),
            EntryKind = AccountStatementEntryKind.ManualAdjustment,
            Side = LedgerSide.Credit,
            SourceAmount = 100m,
            SourceCurrencyCode = "EUR",
            Reference = "ADJ-EUR",
            Description = "EUR adjustment"
        });

        Assert.IsType<RedirectToActionResult>(result);
        var entry = await db.LedgerEntries.SingleAsync();
        Assert.Equal(120m, entry.AmountUsd);
        Assert.Equal("EUR", entry.SourceCurrencyCode);
        Assert.Equal(1.2m, entry.AppliedFxRateToUsd);
        Assert.Equal(new DateTime(2026, 1, 1), entry.AppliedFxRateDate);
        Assert.Equal("ManualAdjustment", entry.SourceType);
        Assert.Equal(entry.Id, entry.SourceId);
        Assert.Single(db.AuditLogs);
    }

    [Fact]
    public async Task Create_Manual_Adjustment_Uses_Manual_Fx_Rate_When_Provided()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        db.Currencies.AddRange(
            new Currency { Id = 1, Code = "USD", Name = "US Dollar", Symbol = "$", IsActive = true },
            new Currency { Id = 2, Code = "EUR", Name = "Euro", Symbol = "EUR", IsActive = true });
        await db.SaveChangesAsync();

        var controller = BuildController(db);

        var result = await controller.Create(new AccountStatementCreateViewModel
        {
            EntryDate = new DateTime(2026, 1, 2),
            EntryKind = AccountStatementEntryKind.ManualAdjustment,
            Side = LedgerSide.Credit,
            SourceAmount = 100m,
            SourceCurrencyCode = "EUR",
            AppliedFxRateToUsd = 1.25m,
            Reference = "ADJ-MANUAL",
            Description = "Manual override FX"
        });

        Assert.IsType<RedirectToActionResult>(result);
        var entry = await db.LedgerEntries.SingleAsync();
        Assert.Equal(125m, entry.AmountUsd);
        Assert.Equal("EUR", entry.SourceCurrencyCode);
        Assert.Equal(1.25m, entry.AppliedFxRateToUsd);
        Assert.Equal(new DateTime(2026, 1, 2), entry.AppliedFxRateDate);
        Assert.Contains("Manual FX EUR/USD", entry.AppliedFxRateSource);
    }

    [Fact]
    public async Task Create_Returns_View_When_Source_Currency_Is_Not_Active_Master_Data()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        db.Currencies.Add(new Currency { Id = 1, Code = "USD", Name = "US Dollar", Symbol = "$", IsActive = true });
        await db.SaveChangesAsync();

        var controller = BuildController(db);

        var result = await controller.Create(new AccountStatementCreateViewModel
        {
            EntryDate = new DateTime(2026, 1, 2),
            EntryKind = AccountStatementEntryKind.ManualAdjustment,
            Side = LedgerSide.Credit,
            SourceAmount = 100m,
            SourceCurrencyCode = "EUR",
            Reference = "ADJ-BAD-CUR",
            Description = "Invalid currency"
        });

        var view = Assert.IsType<ViewResult>(result);
        Assert.IsType<AccountStatementCreateViewModel>(view.Model);
        Assert.False(controller.ModelState.IsValid);
        Assert.Contains(controller.ModelState[nameof(AccountStatementCreateViewModel.SourceCurrencyCode)]!.Errors, e => e.ErrorMessage.Contains("ارز"));
    }

    [Fact]
    public async Task Contract_Shows_Ledger_Rows_For_Selected_Contract()
    {
        var options = NewOptions();
        await using var db = new ApplicationDbContext(options);
        var contract = await SeedPurchaseContractAsync(db);
        db.LedgerEntries.Add(new LedgerEntry
        {
            Id = 10,
            ContractId = contract.Id,
            EntryDate = new DateTime(2026, 2, 1),
            Side = LedgerSide.Credit,
            AmountUsd = 500m,
            Currency = "USD",
            SourceAmount = 500m,
            SourceCurrencyCode = "USD",
            AppliedFxRateToUsd = 1m,
            Description = "Supplier funding",
            SourceType = nameof(PaymentKind.ManualReceipt),
            SourceId = 5,
            Reference = "PAY-1"
        });
        await db.SaveChangesAsync();

        var controller = BuildController(db);

        var result = await controller.Contract(contract.Id);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<ContractAccountStatementViewModel>(view.Model);
        var row = Assert.Single(model.Rows);
        Assert.Equal("PAY-1", row.Reference);
        // دریافت پول روی قرارداد ⇒ رسید (شرکت ارزش گرفته است).
        Assert.Equal(500m, row.ReceiptUsd);
        Assert.True(row.IsFinancial);
    }

    [Fact]
    public async Task Contract_Computes_Outflow_Minus_Receipt_Balance_Usd()
    {
        var options = NewOptions();
        await using var db = new ApplicationDbContext(options);
        var contract = await SeedPurchaseContractAsync(db);
        db.LedgerEntries.AddRange(
            NewLedger(contract.Id, 1, LedgerSide.Credit, 1000m, "USD", 1000m, 1m),
            NewLedger(contract.Id, 2, LedgerSide.Debit, 250m, "USD", 250m, 1m));
        await db.SaveChangesAsync();

        var model = await GetContractStatementAsync(db, contract.Id);

        // بیلانس قرارداد = Σبرد − Σرسید، همان فرمول صورت‌حساب طرف‌حساب.
        Assert.Equal(1000m, model.Totals.TotalReceiptUsd);
        Assert.Equal(250m, model.Totals.TotalOutflowUsd);
        Assert.Equal(-750m, model.Totals.BalanceUsd);
        Assert.Equal(-750m, model.Rows.Last().BalanceUsd);
    }

    [Fact]
    public async Task Contract_Computes_Original_Balances_By_Currency_Separately()
    {
        var options = NewOptions();
        await using var db = new ApplicationDbContext(options);
        var contract = await SeedPurchaseContractAsync(db);
        db.LedgerEntries.AddRange(
            NewLedger(contract.Id, 1, LedgerSide.Credit, 100m, "USD", 100m, 1m),
            NewLedger(contract.Id, 2, LedgerSide.Credit, 20m, "RUB", 2000m, 0.01m),
            NewLedger(contract.Id, 3, LedgerSide.Debit, 5m, "RUB", 500m, 0.01m));
        await db.SaveChangesAsync();

        var model = await GetContractStatementAsync(db, contract.Id);

        Assert.Equal(-100m, model.Totals.BalancesByCurrency.Single(b => b.Currency == "USD").BalanceOriginal);
        Assert.Equal(-1500m, model.Totals.BalancesByCurrency.Single(b => b.Currency == "RUB").BalanceOriginal);
        Assert.Contains("RUB", model.Rows.Last(r => r.SourceCurrency == "RUB").BalanceOriginalByCurrency);
    }

    [Fact]
    public async Task Contract_Does_Not_Duplicate_Payment_When_LedgerEntry_Exists()
    {
        var options = NewOptions();
        await using var db = new ApplicationDbContext(options);
        var contract = await SeedPurchaseContractAsync(db);
        db.CashAccounts.Add(new CashAccount { Id = 1, Code = "BNK", Name = "Bank", Currency = "USD", IsActive = true });
        db.PaymentTransactions.Add(new PaymentTransaction
        {
            Id = 7,
            ContractId = contract.Id,
            CashAccountId = 1,
            PaymentDate = new DateTime(2026, 2, 1),
            Direction = PaymentDirection.In,
            PaymentKind = PaymentKind.ManualReceipt,
            Amount = 100m,
            Currency = "USD",
            AppliedFxRateToUsd = 1m,
            AmountUsd = 100m,
            LedgerEntryId = 70,
            Reference = "PAY-DUP"
        });
        db.LedgerEntries.Add(new LedgerEntry
        {
            Id = 70,
            ContractId = contract.Id,
            EntryDate = new DateTime(2026, 2, 1),
            Side = LedgerSide.Credit,
            AmountUsd = 100m,
            Currency = "USD",
            SourceAmount = 100m,
            SourceCurrencyCode = "USD",
            AppliedFxRateToUsd = 1m,
            Description = "Payment ledger",
            SourceType = nameof(PaymentKind.ManualReceipt),
            SourceId = 7,
            Reference = "PAY-DUP"
        });
        await db.SaveChangesAsync();

        var model = await GetContractStatementAsync(db, contract.Id);

        Assert.Single(model.Rows);
        Assert.Equal(-100m, model.Totals.BalanceUsd);
        Assert.DoesNotContain(model.Rows, r => r.WarningBadge == "Payment without Ledger");
    }

    [Fact]
    public async Task Contract_Shows_Loading_As_Operational_Only_Without_Financial_Posting()
    {
        var options = NewOptions();
        await using var db = new ApplicationDbContext(options);
        var contract = await SeedPurchaseContractAsync(db);
        db.LoadingRegisters.Add(new LoadingRegister
        {
            Id = 3,
            ContractId = contract.Id,
            ProductId = 1,
            TransportType = LoadingTransportType.Wagon,
            LoadingDate = new DateTime(2026, 2, 3),
            LoadedQuantityMt = 30m,
            LoadingPriceUsd = 700m,
            RwbNo = "RWB-1"
        });
        await db.SaveChangesAsync();

        var model = await GetContractStatementAsync(db, contract.Id);

        var row = Assert.Single(model.Rows);
        Assert.Equal(nameof(LoadingRegister), row.SourceType);
        Assert.True(row.IsOperationalOnly);
        Assert.Equal("Operational only", row.WarningBadge);
        Assert.Null(row.ReceiptUsd);
        Assert.Null(row.OutflowUsd);
        Assert.Equal(0m, model.Totals.BalanceUsd);
    }

    [Fact]
    public async Task Contract_Get_Does_Not_Create_Financial_Or_Inventory_Records()
    {
        var options = NewOptions();
        await using var db = new ApplicationDbContext(options);
        var contract = await SeedPurchaseContractAsync(db);
        db.LedgerEntries.Add(NewLedger(contract.Id, 1, LedgerSide.Credit, 100m, "USD", 100m, 1m));
        await db.SaveChangesAsync();

        var beforeLedger = await db.LedgerEntries.CountAsync();
        var beforePayments = await db.PaymentTransactions.CountAsync();
        var beforeMovements = await db.InventoryMovements.CountAsync();

        var controller = BuildController(db);
        var result = await controller.Contract(contract.Id);

        Assert.IsType<ViewResult>(result);
        Assert.Equal(beforeLedger, await db.LedgerEntries.CountAsync());
        Assert.Equal(beforePayments, await db.PaymentTransactions.CountAsync());
        Assert.Equal(beforeMovements, await db.InventoryMovements.CountAsync());
    }

    [Fact]
    public void ContractJourney_Details_Links_To_Contract_Account_Statement()
    {
        var financePartial = ReadRepoFile("src/PTGOilSystem.Web/Views/ContractJourney/_ContractJourneyFinanceTab.cshtml");
        var contents = ReadRepoFile("src/PTGOilSystem.Web/Views/ContractJourney/Details.cshtml") + financePartial;

        Assert.Contains("AccountStatementUrl = Url.Action(\"Contract\", \"AccountStatements\"", contents);
        Assert.Contains("new { contractId = Model.ContractId }", contents);
        Assert.Contains("href=\"@Model.AccountStatementUrl\"", financePartial);
        Assert.Contains("دفتر حساب قرارداد", contents);
    }

    [Fact]
    public void Contract_View_Uses_Compact_Essential_Columns_Without_Losing_Trace_Fields()
    {
        var contents = ReadRepoFile("src/PTGOilSystem.Web/Views/AccountStatements/Contract.cshtml");

        foreach (var heading in new[]
        {
            "<th>تاریخ</th>",
            "<th class=\"ak-col-grow\">جزئیات</th>",
            "<th class=\"ak-col-num\">مبلغ ارز اصلی</th>",
            "<th class=\"ak-col-num\">رسیدگی USD</th>",
            "<th class=\"ak-col-num\">بردگی USD</th>",
            "<th class=\"ak-col-num\">بیلانس USD</th>"
        })
        {
            Assert.Contains(heading, contents);
        }

        Assert.Contains("colspan=\"6\"", contents);
        Assert.Contains("row.SourceType", contents);
        Assert.Contains("row.Reference", contents);
        Assert.Contains("row.RelatedContractNumber", contents);
        Assert.Contains("row.QuantityMt", contents);
        Assert.Contains("row.UnitPrice", contents);
        Assert.Contains("row.FxRateToUsd", contents);
        Assert.Contains("row.WarningBadge", contents);
        Assert.DoesNotContain("<th>نوع</th>", contents);
        Assert.DoesNotContain("<th>وضعیت</th>", contents);
        Assert.DoesNotContain("<th class=\"ak-col-num\">مقدار (تن)</th>", contents);
        Assert.DoesNotContain("<th class=\"ak-col-num\">قیمت واحد</th>", contents);
        Assert.DoesNotContain("<th class=\"ak-col-num\">نرخ ارز</th>", contents);
    }

    private static AccountStatementsController BuildController(ApplicationDbContext db)
        => new(db, new PricingService(db), new AuditService(db))
        {
            TempData = new TempDataDictionary(new DefaultHttpContext(), new TestTempDataProvider())
        };

    private static DbContextOptions<ApplicationDbContext> NewOptions()
        => new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

    private static async Task<Contract> SeedPurchaseContractAsync(ApplicationDbContext db)
    {
        db.Products.Add(new Product { Id = 1, Code = "G92", Name = "Gasoline 92", UnitOfMeasure = "MT", IsActive = true });
        db.Companies.Add(new Company { Id = 1, Code = "PTG", Name = "PTG", Country = "AF", IsActive = true });
        db.Suppliers.Add(new Supplier { Id = 1, Code = "HIMOR", Name = "HIMOR", IsActive = true });

        var contract = new Contract
        {
            Id = 1,
            ContractNumber = "M-15",
            ContractType = ContractType.Purchase,
            Status = ContractStatus.Active,
            CompanyId = 1,
            ProductId = 1,
            SupplierId = 1,
            ContractDate = new DateTime(2026, 1, 1),
            PricingMethod = PricingMethod.ManualFinalPrice,
            QuantityMt = 1000m,
            Currency = "RUB"
        };
        db.Contracts.Add(contract);
        await db.SaveChangesAsync();
        return contract;
    }

    private static LedgerEntry NewLedger(
        int contractId,
        int id,
        LedgerSide side,
        decimal amountUsd,
        string sourceCurrency,
        decimal sourceAmount,
        decimal fxRate)
        => new()
        {
            Id = id,
            ContractId = contractId,
            EntryDate = new DateTime(2026, 2, id),
            Side = side,
            AmountUsd = amountUsd,
            Currency = "USD",
            SourceAmount = sourceAmount,
            SourceCurrencyCode = sourceCurrency,
            AppliedFxRateToUsd = fxRate,
            Description = $"Ledger {id}",
            SourceType = "TestLedger",
            SourceId = id,
            Reference = $"L-{id}"
        };

    private static async Task<ContractAccountStatementViewModel> GetContractStatementAsync(ApplicationDbContext db, int contractId)
    {
        var controller = BuildController(db);
        var result = await controller.Contract(contractId);
        var view = Assert.IsType<ViewResult>(result);
        return Assert.IsType<ContractAccountStatementViewModel>(view.Model);
    }

    private static string ReadRepoFile(string relativePath)
    {
        var path = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            relativePath.Replace('/', Path.DirectorySeparatorChar)));

        return File.ReadAllText(path);
    }

    private sealed class TestTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();

        public void SaveTempData(HttpContext context, IDictionary<string, object> values)
        {
        }
    }
}
