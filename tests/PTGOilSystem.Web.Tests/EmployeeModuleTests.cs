using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using PTGOilSystem.Web.Controllers;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Employees;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.Reconciliation;
using PTGOilSystem.Web.Security;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Employees;
using PTGOilSystem.Web.Services.Exceptions;
using Xunit;

namespace PTGOilSystem.Web.Tests;

public class EmployeeModuleTests
{
    [Fact]
    public async Task Employee_Create_Valid_Persists_Profile()
    {
        await using var db = new ApplicationDbContext(NewDbOptions());
        SeedReferenceData(db);
        await db.SaveChangesAsync();

        var controller = BuildEmployeesController(db);
        var result = await controller.Create(new EmployeeFormViewModel
        {
            EmployeeCode = "EMP-001",
            FullName = "Ahmad Herati",
            JobTitle = "Accountant",
            Department = "Finance",
            EmployeeType = EmployeeType.Permanent,
            SalaryType = EmployeeSalaryType.Monthly,
            BaseSalaryAmount = 1000m,
            SalaryCurrency = "USD",
            HireDate = new DateTime(2026, 5, 1),
            IsActive = true
        });

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Details", redirect.ActionName);
        var employee = await db.Employees.SingleAsync(e => e.EmployeeCode == "EMP-001");
        Assert.Equal("EMP-001", employee.EmployeeCode);
        Assert.Equal(1000m, employee.BaseSalaryAmount);
        Assert.Contains(await db.AuditLogs.ToListAsync(), log => log.EntityName == nameof(Employee) && log.Action == "Insert");
    }

    [Fact]
    public async Task Employee_Create_With_Photo_Persists_PhotoPath()
    {
        await using var db = new ApplicationDbContext(NewDbOptions());
        SeedReferenceData(db);
        await db.SaveChangesAsync();

        var controller = BuildEmployeesController(db);
        await using var stream = new MemoryStream([1, 2, 3, 4]);
        var photo = new FormFile(stream, 0, stream.Length, "PhotoFile", "employee.png")
        {
            Headers = new HeaderDictionary(),
            ContentType = "image/png"
        };

        var result = await controller.Create(new EmployeeFormViewModel
        {
            EmployeeCode = "EMP-PHOTO",
            FullName = "Photo Employee",
            EmployeeType = EmployeeType.Permanent,
            SalaryType = EmployeeSalaryType.Monthly,
            BaseSalaryAmount = 1000m,
            SalaryCurrency = "USD",
            HireDate = new DateTime(2026, 5, 1),
            IsActive = true,
            PhotoFile = photo
        });

        Assert.IsType<RedirectToActionResult>(result);
        var employee = await db.Employees.SingleAsync(e => e.EmployeeCode == "EMP-PHOTO");
        Assert.StartsWith("/uploads/employees/", employee.PhotoPath);
    }

    [Fact]
    public async Task SalaryAccrual_Does_Not_Require_CashAccount_And_Does_Not_Create_Ledger()
    {
        await using var db = new ApplicationDbContext(NewDbOptions());
        SeedReferenceData(db);
        await db.SaveChangesAsync();

        var service = BuildSalaryService(db);
        var transaction = await service.CreateAsync(new EmployeeSalaryTransactionCommand(
            EmployeeId: 1,
            TransactionDate: new DateTime(2026, 5, 31),
            TransactionType: EmployeeSalaryTransactionType.SalaryAccrual,
            Amount: 1000m,
            Currency: "USD",
            AppliedFxRateToUsd: null,
            CashAccountId: null,
            Reference: "SAL-2026-05",
            Description: "May salary accrual",
            SalaryPeriodYear: 2026,
            SalaryPeriodMonth: 5));

        Assert.Null(transaction.CashAccountId);
        Assert.Null(transaction.PaymentTransactionId);
        Assert.Null(transaction.LedgerEntryId);
        Assert.Empty(await db.PaymentTransactions.ToListAsync());
        Assert.Empty(await db.LedgerEntries.ToListAsync());
    }

    [Fact]
    public async Task SalaryAdvance_Requires_CashAccount()
    {
        await using var db = new ApplicationDbContext(NewDbOptions());
        SeedReferenceData(db);
        await db.SaveChangesAsync();

        var service = BuildSalaryService(db);
        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => service.CreateAsync(new EmployeeSalaryTransactionCommand(
            EmployeeId: 1,
            TransactionDate: new DateTime(2026, 5, 15),
            TransactionType: EmployeeSalaryTransactionType.SalaryAdvance,
            Amount: 100m,
            Currency: "USD",
            AppliedFxRateToUsd: null,
            CashAccountId: null,
            Reference: "ADV-001",
            Description: null,
            SalaryPeriodYear: null,
            SalaryPeriodMonth: null)));

        Assert.Equal("EMPLOYEE_SALARY_CASH_REQUIRED", ex.Code);
        Assert.Empty(await db.EmployeeSalaryTransactions.ToListAsync());
    }

    [Theory]
    [InlineData(EmployeeSalaryTransactionType.SalaryPayment, PaymentKind.EmployeeSalaryPayment)]
    [InlineData(EmployeeSalaryTransactionType.SalaryAdvance, PaymentKind.EmployeeSalaryAdvance)]
    public async Task CashSalaryTransactions_Create_Payment_And_Ledger(
        EmployeeSalaryTransactionType transactionType,
        PaymentKind expectedPaymentKind)
    {
        await using var db = new ApplicationDbContext(NewDbOptions());
        SeedReferenceData(db);
        await db.SaveChangesAsync();

        var service = BuildSalaryService(db);
        var transaction = await service.CreateAsync(new EmployeeSalaryTransactionCommand(
            EmployeeId: 1,
            TransactionDate: new DateTime(2026, 5, 20),
            TransactionType: transactionType,
            Amount: 250m,
            Currency: "USD",
            AppliedFxRateToUsd: null,
            CashAccountId: 1,
            Reference: "EMP-CASH-001",
            Description: "Cash salary trace",
            SalaryPeriodYear: null,
            SalaryPeriodMonth: null));

        Assert.NotNull(transaction.PaymentTransactionId);
        Assert.NotNull(transaction.LedgerEntryId);

        var payment = await db.PaymentTransactions.SingleAsync();
        var ledger = await db.LedgerEntries.SingleAsync();

        Assert.Equal(expectedPaymentKind, payment.PaymentKind);
        Assert.Equal(PaymentDirection.Out, payment.Direction);
        Assert.Equal(1, payment.EmployeeId);
        Assert.Equal(ledger.Id, payment.LedgerEntryId);
        Assert.Equal(expectedPaymentKind.ToString(), ledger.SourceType);
        Assert.Equal(payment.Id, ledger.SourceId);
        Assert.Equal(1, ledger.EmployeeId);
        Assert.Equal(250m, ledger.AmountUsd);
    }

    [Fact]
    public void Employee_Profile_Balance_Calculates_Correctly()
    {
        var transactions = new[]
        {
            SalaryTx(EmployeeSalaryTransactionType.SalaryAccrual, 1000m),
            SalaryTx(EmployeeSalaryTransactionType.Bonus, 100m),
            SalaryTx(EmployeeSalaryTransactionType.Adjustment, 25m),
            SalaryTx(EmployeeSalaryTransactionType.SalaryPayment, 200m),
            SalaryTx(EmployeeSalaryTransactionType.SalaryAdvance, 50m),
            SalaryTx(EmployeeSalaryTransactionType.SalaryDeduction, 75m),
            SalaryTx(EmployeeSalaryTransactionType.SalaryPayment, 999m, isCancelled: true)
        };

        var summary = EmployeeSalarySummaryCalculator.FromTransactions(transactions);

        Assert.Equal(1000m, summary.AccruedSalaryUsd);
        Assert.Equal(200m, summary.PaidSalaryUsd);
        Assert.Equal(50m, summary.AdvancesUsd);
        Assert.Equal(75m, summary.DeductionsUsd);
        Assert.Equal(100m, summary.BonusesUsd);
        Assert.Equal(25m, summary.AdjustmentsUsd);
        Assert.Equal(800m, summary.BalanceUsd);
    }

    [Fact]
    public async Task Cancel_Transaction_Does_Not_Delete_Data()
    {
        await using var db = new ApplicationDbContext(NewDbOptions());
        SeedReferenceData(db);
        await db.SaveChangesAsync();

        var service = BuildSalaryService(db);
        var transaction = await service.CreateAsync(new EmployeeSalaryTransactionCommand(
            EmployeeId: 1,
            TransactionDate: new DateTime(2026, 5, 31),
            TransactionType: EmployeeSalaryTransactionType.SalaryAccrual,
            Amount: 1000m,
            Currency: "USD",
            AppliedFxRateToUsd: null,
            CashAccountId: null,
            Reference: "SAL-CANCEL",
            Description: null,
            SalaryPeriodYear: 2026,
            SalaryPeriodMonth: 5));

        await service.CancelAsync(transaction.Id, "Wrong period");

        Assert.Equal(1, await db.EmployeeSalaryTransactions.CountAsync());
        var saved = await db.EmployeeSalaryTransactions.SingleAsync();
        Assert.True(saved.IsCancelled);
        Assert.Equal("Wrong period", saved.CancellationReason);
        Assert.Contains(await db.AuditLogs.ToListAsync(), log => log.EntityName == nameof(EmployeeSalaryTransaction) && log.Action == "Reverse");
    }

    [Fact]
    public async Task Reconciliation_Catches_Missing_Ledger_And_CashAccount()
    {
        await using var db = new ApplicationDbContext(NewDbOptions());
        SeedReferenceData(db);
        db.EmployeeSalaryTransactions.AddRange(
            new EmployeeSalaryTransaction
            {
                Id = 10,
                EmployeeId = 1,
                TransactionDate = new DateTime(2026, 5, 20),
                TransactionType = EmployeeSalaryTransactionType.SalaryPayment,
                Amount = 200m,
                Currency = "USD",
                AppliedFxRateToUsd = 1m,
                AmountUsd = 200m,
                CashAccountId = 1,
                Reference = "NO-LEDGER"
            },
            new EmployeeSalaryTransaction
            {
                Id = 11,
                EmployeeId = 1,
                TransactionDate = new DateTime(2026, 5, 21),
                TransactionType = EmployeeSalaryTransactionType.SalaryAdvance,
                Amount = 100m,
                Currency = "USD",
                AppliedFxRateToUsd = 1m,
                AmountUsd = 100m,
                Reference = "NO-CASH"
            });
        await db.SaveChangesAsync();

        var controller = new ReconciliationController(db);
        var result = await controller.EmployeeTransactions();

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<EmployeeReconciliationViewModel>(view.Model);
        Assert.Contains(model.TransactionsWithoutLedger, row => row.TransactionId == 10);
        Assert.Contains(model.TransactionsWithoutCashAccount, row => row.TransactionId == 11);
    }

    [Fact]
    public async Task Employee_Details_Shows_Salary_Transactions()
    {
        await using var db = new ApplicationDbContext(NewDbOptions());
        SeedReferenceData(db);
        db.EmployeeSalaryTransactions.Add(new EmployeeSalaryTransaction
        {
            EmployeeId = 1,
            TransactionDate = new DateTime(2026, 5, 31),
            TransactionType = EmployeeSalaryTransactionType.SalaryAccrual,
            Amount = 1000m,
            Currency = "USD",
            AppliedFxRateToUsd = 1m,
            AmountUsd = 1000m,
            SalaryPeriodYear = 2026,
            SalaryPeriodMonth = 5,
            Reference = "SAL-DETAIL"
        });
        await db.SaveChangesAsync();

        var controller = BuildEmployeesController(db);
        var result = await controller.Details(1);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<EmployeeDetailsViewModel>(view.Model);
        Assert.Single(model.Transactions);
        Assert.Equal("SAL-DETAIL", model.Transactions[0].Reference);
        Assert.Equal(1000m, model.Summary.BalanceUsd);
    }

    [Fact]
    public async Task Employee_Details_Shows_Direct_Roznamcha_Payments()
    {
        await using var db = new ApplicationDbContext(NewDbOptions());
        SeedReferenceData(db);
        db.PaymentTransactions.Add(new PaymentTransaction
        {
            Id = 20,
            PaymentDate = new DateTime(2026, 5, 22),
            Direction = PaymentDirection.In,
            PaymentKind = PaymentKind.EmployeeReturn,
            CashAccountId = 1,
            EmployeeId = 1,
            Amount = 75m,
            Currency = "USD",
            AppliedFxRateToUsd = 1m,
            AmountUsd = 75m,
            Reference = "EMP-RETURN"
        });
        await db.SaveChangesAsync();

        var controller = BuildEmployeesController(db);
        var result = await controller.Details(1);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<EmployeeDetailsViewModel>(view.Model);
        var row = Assert.Single(model.RoznamchaPayments);
        Assert.Equal("EMP-RETURN", row.Reference);
        Assert.Equal(PaymentKind.EmployeeReturn, row.PaymentKind);
    }

    private static EmployeeSalaryTransaction SalaryTx(
        EmployeeSalaryTransactionType type,
        decimal amountUsd,
        bool isCancelled = false)
        => new()
        {
            TransactionType = type,
            Amount = amountUsd,
            Currency = "USD",
            AppliedFxRateToUsd = 1m,
            AmountUsd = amountUsd,
            IsCancelled = isCancelled
        };

    // پیش‌فرض: کاربرِ Admin (همهٔ دسترسی‌های معاش). تست‌های دسترسی کاربرِ خودشان را می‌دهند.
    internal static EmployeesController BuildEmployeesController(ApplicationDbContext db, ClaimsPrincipal? user = null)
    {
        var httpContext = new DefaultHttpContext
        {
            User = user ?? new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, AuthRoles.Admin)], "Test"))
        };
        return new(db, new AuditService(db), BuildSalaryService(db), new TestWebHostEnvironment())
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
            TempData = new TempDataDictionary(httpContext, new TestTempDataProvider())
        };
    }

    private sealed class TestWebHostEnvironment : IWebHostEnvironment
    {
        public string WebRootPath { get; set; } = Path.Combine(Path.GetTempPath(), "ptg-oil-tests", Guid.NewGuid().ToString("N"), "wwwroot");
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ApplicationName { get; set; } = "PTGOilSystem.Web.Tests";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = Directory.GetCurrentDirectory();
        public string EnvironmentName { get; set; } = "Testing";
    }

    internal static EmployeeSalaryService BuildSalaryService(ApplicationDbContext db)
        => new(
            db,
            new CurrencyConversionService(new PricingService(db)),
            new AuditService(db),
            NullLogger<EmployeeSalaryService>.Instance);

    internal static DbContextOptions<ApplicationDbContext> NewDbOptions()
        => new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

    internal static void SeedReferenceData(ApplicationDbContext db)
    {
        db.Currencies.Add(new Currency { Id = 1, Code = "USD", Name = "US Dollar", Symbol = "$", IsActive = true });
        db.CashAccounts.Add(new CashAccount
        {
            Id = 1,
            Code = "CASH-USD",
            Name = "Main USD Cash",
            AccountType = CashAccountType.Cash,
            Currency = "USD",
            IsActive = true
        });
        db.Employees.Add(new Employee
        {
            Id = 1,
            EmployeeCode = "EMP-BASE",
            FullName = "Base Employee",
            EmployeeType = EmployeeType.Permanent,
            SalaryType = EmployeeSalaryType.Monthly,
            BaseSalaryAmount = 1000m,
            SalaryCurrency = "USD",
            HireDate = new DateTime(2026, 5, 1),
            IsActive = true
        });
    }

    private sealed class TestTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values)
        {
        }
    }
}
