using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Controllers;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.Payments;
using PTGOilSystem.Web.Services;
using Xunit;

namespace PTGOilSystem.Web.Tests;

public class CashAccountsControllerTests
{
    [Fact]
    public async Task Details_Includes_Unposted_Cash_Expenses_And_Does_Not_Double_Count_Linked_Expenses()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        db.CashAccounts.Add(new CashAccount
        {
            Id = 1,
            Code = "CASH-USD",
            Name = "Main Cash",
            AccountType = CashAccountType.Cash,
            Currency = "USD",
            IsActive = true
        });
        db.ExpenseTypes.Add(new ExpenseType { Id = 1, Code = "OPS", Name = "Operations" });
        db.ExpenseTransactions.AddRange(
            new ExpenseTransaction
            {
                Id = 1,
                ExpenseTypeId = 1,
                ExpenseDate = new DateTime(2026, 4, 2),
                SettlementMode = ExpenseSettlementMode.PaidImmediately,
                CashAccountId = 1,
                Amount = 30m,
                Currency = "USD",
                AmountUsd = 30m,
                Description = "Cash customs fee"
            },
            new ExpenseTransaction
            {
                Id = 2,
                ExpenseTypeId = 1,
                ExpenseDate = new DateTime(2026, 4, 3),
                SettlementMode = ExpenseSettlementMode.PaidImmediately,
                CashAccountId = 1,
                Amount = 20m,
                Currency = "USD",
                AmountUsd = 20m,
                Description = "Linked commission"
            },
            new ExpenseTransaction
            {
                Id = 3,
                ExpenseTypeId = 1,
                ExpenseDate = new DateTime(2026, 4, 4),
                SettlementMode = ExpenseSettlementMode.PaidImmediately,
                CashAccountId = 1,
                Amount = 100m,
                Currency = "USD",
                AmountUsd = 100m,
                IsCancelled = true
            });
        db.PaymentTransactions.AddRange(
            new PaymentTransaction
            {
                Id = 1,
                PaymentDate = new DateTime(2026, 4, 1),
                Direction = PaymentDirection.In,
                PaymentKind = PaymentKind.ManualReceipt,
                CashAccountId = 1,
                Amount = 100m,
                Currency = "USD",
                AmountUsd = 100m
            },
            new PaymentTransaction
            {
                Id = 2,
                PaymentDate = new DateTime(2026, 4, 3),
                Direction = PaymentDirection.Out,
                PaymentKind = PaymentKind.CommissionPayment,
                CashAccountId = 1,
                ExpenseTransactionId = 2,
                Amount = 20m,
                Currency = "USD",
                AmountUsd = 20m
            });
        await db.SaveChangesAsync();

        var result = await BuildController(db).Details(1);

        Assert.IsType<ViewResult>(result);
        var rows = Assert.IsAssignableFrom<IReadOnlyList<CashAccountStatementRowViewModel>>(
            Assert.IsType<ViewResult>(result).ViewData["StatementRows"]);
        Assert.Equal(3, rows.Count);
        Assert.Single(rows, row => row.Source == "Expense" && row.Id == 1);
        Assert.DoesNotContain(rows, row => row.Source == "Expense" && row.Id == 2);
        Assert.Equal(100m, Assert.IsType<decimal>(Assert.IsType<ViewResult>(result).ViewData["TotalIn"]));
        Assert.Equal(50m, Assert.IsType<decimal>(Assert.IsType<ViewResult>(result).ViewData["TotalOut"]));
        Assert.Equal(50m, Assert.IsType<decimal>(Assert.IsType<ViewResult>(result).ViewData["ClosingBalance"]));
        Assert.Equal("USD", Assert.IsType<string>(Assert.IsType<ViewResult>(result).ViewData["TotalsCurrency"]));
    }

    [Fact]
    public async Task Create_Persists_Cash_Account_And_Audits()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedActiveCurrencies(db, "USD", "EUR");
        var controller = BuildController(db);

        var result = await controller.Create(new CashAccount
        {
            Code = "BANK-USD",
            Name = "Main USD Bank",
            AccountType = CashAccountType.Bank,
            Currency = "usd",
            IsActive = true,
            Notes = "Primary settlement account"
        });

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Index", redirect.ActionName);

        var account = await db.CashAccounts.SingleAsync();
        Assert.Equal("BANK-USD", account.Code);
        Assert.Equal("USD", account.Currency);
        Assert.Equal(CashAccountType.Bank, account.AccountType);

        var audit = await db.AuditLogs.SingleAsync();
        Assert.Equal(nameof(CashAccount), audit.EntityName);
        Assert.Equal("Insert", audit.Action);
    }

    [Fact]
    public async Task Create_Returns_View_When_Currency_Is_Not_Active_Master_Data()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedActiveCurrencies(db, "USD");
        var controller = BuildController(db);

        var result = await controller.Create(new CashAccount
        {
            Code = "BANK-EUR",
            Name = "Euro Bank",
            AccountType = CashAccountType.Bank,
            Currency = "EUR",
            IsActive = true
        });

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<CashAccount>(view.Model);
        Assert.Equal("EUR", model.Currency);
        Assert.False(controller.ModelState.IsValid);
        Assert.Empty(db.CashAccounts);
    }

    [Fact]
    public async Task Edit_Updates_Cash_Account_And_Audits()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedActiveCurrencies(db, "USD", "EUR");
        db.CashAccounts.Add(new CashAccount
        {
            Id = 1,
            Code = "BANK-USD",
            Name = "Main USD Bank",
            AccountType = CashAccountType.Bank,
            Currency = "USD",
            IsActive = true
        });
        await db.SaveChangesAsync();

        var controller = BuildController(db);

        var result = await controller.Edit(1, new CashAccount
        {
            Id = 1,
            Code = "BANK-USD",
            Name = "Updated Bank",
            AccountType = CashAccountType.Bank,
            Currency = "usd",
            IsActive = false,
            Notes = "Archived"
        });

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Index", redirect.ActionName);

        var account = await db.CashAccounts.SingleAsync();
        Assert.Equal("Updated Bank", account.Name);
        Assert.Equal("USD", account.Currency);
        Assert.False(account.IsActive);

        Assert.Contains(
            await db.AuditLogs.ToListAsync(),
            log => log.EntityName == nameof(CashAccount) && log.Action == "Update");
    }

    private static void SeedActiveCurrencies(ApplicationDbContext db, params string[] codes)
    {
        var items = codes
            .Select((code, index) => new Currency
            {
                Id = index + 1,
                Code = code,
                Name = code,
                IsActive = true
            });
        db.Currencies.AddRange(items);
        db.SaveChanges();
    }

    private static CashAccountsController BuildController(ApplicationDbContext db)
        => new(db, new AuditService(db))
        {
            TempData = new TempDataDictionary(new DefaultHttpContext(), new TestTempDataProvider())
        };

    private static DbContextOptions<ApplicationDbContext> NewDbOptions()
        => new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

    private sealed class TestTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();

        public void SaveTempData(HttpContext context, IDictionary<string, object> values)
        {
        }
    }
}
