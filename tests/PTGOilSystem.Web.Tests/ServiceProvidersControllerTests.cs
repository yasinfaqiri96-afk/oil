using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Controllers;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.ServiceProviders;
using Xunit;
using ServiceProviderEntity = PTGOilSystem.Web.Models.Entities.ServiceProvider;

namespace PTGOilSystem.Web.Tests;

public class ServiceProvidersControllerTests
{
    [Fact]
    public async Task Create_Post_Persists_ServiceProvider()
    {
        await using var db = CreateDb();
        var controller = BuildController(db);

        var result = await controller.Create(new ServiceProviderCreateViewModel
        {
            Code = "RAIL-A",
            Name = "Railway Services A",
            ProviderType = ServiceProviderType.RailwayService,
            City = "Herat",
            Phone = "0700000000",
            IsActive = true
        });

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Details", redirect.ActionName);

        var provider = await db.ServiceProviders.SingleAsync();
        Assert.Equal("RAIL-A", provider.Code);
        Assert.Equal("Railway Services A", provider.Name);
        Assert.Equal(ServiceProviderType.RailwayService, provider.ProviderType);
        Assert.True(provider.IsActive);
    }

    [Fact]
    public async Task Create_Post_With_Local_ReturnUrl_Returns_To_The_Origin_List()
    {
        await using var db = CreateDb();
        var controller = BuildController(db);
        const string returnUrl = "/ServiceProviders?q=rail&page=2";

        var result = await controller.Create(new ServiceProviderCreateViewModel
        {
            Code = "RAIL-B",
            Name = "Railway Services B",
            ProviderType = ServiceProviderType.RailwayService,
            IsActive = true
        }, returnUrl);

        var redirect = Assert.IsType<LocalRedirectResult>(result);
        Assert.Equal(returnUrl, redirect.Url);
    }

    [Fact]
    public async Task Create_Get_Returns_The_Shared_Create_Page()
    {
        await using var db = CreateDb();
        var controller = BuildController(db);

        var result = await controller.Create();

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<ServiceProviderCreateViewModel>(view.Model);
        Assert.True(model.IsActive);
    }

    [Fact]
    public async Task Create_Post_Invalid_Model_Returns_The_Create_Page()
    {
        await using var db = CreateDb();
        var controller = BuildController(db);
        controller.ModelState.AddModelError(nameof(ServiceProviderCreateViewModel.Name), "Required");

        var model = new ServiceProviderCreateViewModel
        {
            Name = "",
            ProviderType = ServiceProviderType.TransportCompany
        };

        var result = await controller.Create(model, "/ServiceProviders?q=invalid");

        var view = Assert.IsType<ViewResult>(result);
        Assert.Null(view.ViewName);
        Assert.Same(model, view.Model);
    }

    [Fact]
    public async Task Details_Builds_Ledger_Statement_And_Excludes_Cancelled_Expenses_From_Active_Total()
    {
        await using var db = CreateDb();
        SeedProfileData(db);
        await db.SaveChangesAsync();

        var controller = BuildController(db);

        var result = await controller.Details(1);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<ServiceProviderProfileViewModel>(view.Model);

        Assert.Equal(100m, model.TotalExpensesUsd);
        Assert.Equal(40m, model.TotalPaymentsUsd);
        Assert.Equal(150m, model.LedgerCreditUsd);
        Assert.Equal(90m, model.LedgerDebitUsd);
        // مانده نمایشی = Σ(داده − گرفته) = 90 − 150؛ منفی یعنی بدهی به شرکت خدماتی.
        Assert.Equal(-60m, model.LedgerBalanceUsd);
        Assert.Equal("Payable to provider", model.BalanceStatus);

        Assert.Single(model.Expenses);
        Assert.Single(model.Payments);
        Assert.Single(model.RelatedContracts);
        Assert.Equal(4, model.StatementRows.Count);
        Assert.Collection(
            model.StatementRows,
            row => Assert.Equal(100m, row.RunningBalanceUsd),
            row => Assert.Equal(60m, row.RunningBalanceUsd),
            row => Assert.Equal(110m, row.RunningBalanceUsd),
            row => Assert.Equal(60m, row.RunningBalanceUsd));
    }

    private static ServiceProvidersController BuildController(ApplicationDbContext db)
    {
        var httpContext = new DefaultHttpContext();
        return new(db)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
            TempData = new TempDataDictionary(httpContext, new TestTempDataProvider()),
            Url = new LocalOnlyUrlHelper()
        };
    }

    private static ApplicationDbContext CreateDb()
        => new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static void SeedProfileData(ApplicationDbContext db)
    {
        db.Companies.Add(new Company { Id = 1, Code = "PTG", Name = "PTG" });
        db.Products.Add(new Product { Id = 1, Code = "GO", Name = "Gas Oil" });
        db.Suppliers.Add(new Supplier { Id = 1, Name = "Supplier A" });
        db.ServiceProviders.Add(new ServiceProviderEntity
        {
            Id = 1,
            Code = "RAIL-A",
            Name = "Railway Services A",
            ProviderType = ServiceProviderType.RailwayService,
            IsActive = true
        });
        db.ExpenseTypes.Add(new ExpenseType { Id = 1, Code = "WAGON", Name = "Wagon Rent" });
        db.CashAccounts.Add(new CashAccount
        {
            Id = 1,
            Code = "BANK-USD",
            Name = "Main USD Bank",
            AccountType = CashAccountType.Bank,
            Currency = "USD",
            IsActive = true
        });
        db.Contracts.Add(new Contract
        {
            Id = 1,
            ContractNumber = "PUR-001",
            ContractType = ContractType.Purchase,
            CompanyId = 1,
            SupplierId = 1,
            ProductId = 1,
            ContractDate = new DateTime(2026, 1, 1),
            QuantityMt = 100m,
            PricingMethod = PricingMethod.Fixed,
            UnitPriceUsd = 100m
        });
        db.ExpenseTransactions.AddRange(
            new ExpenseTransaction
            {
                Id = 1,
                ExpenseTypeId = 1,
                ServiceProviderId = 1,
                ContractId = 1,
                ExpenseDate = new DateTime(2026, 1, 2),
                Amount = 100m,
                Currency = "USD",
                AmountUsd = 100m,
                Description = "Active wagon rent"
            },
            new ExpenseTransaction
            {
                Id = 2,
                ExpenseTypeId = 1,
                ServiceProviderId = 1,
                ContractId = 1,
                ExpenseDate = new DateTime(2026, 1, 3),
                Amount = 50m,
                Currency = "USD",
                AmountUsd = 50m,
                Description = "Cancelled wagon rent",
                IsCancelled = true
            });
        db.PaymentTransactions.Add(new PaymentTransaction
        {
            Id = 1,
            PaymentDate = new DateTime(2026, 1, 2),
            Direction = PaymentDirection.Out,
            PaymentKind = PaymentKind.ServiceProviderPayment,
            CashAccountId = 1,
            ServiceProviderId = 1,
            ContractId = 1,
            Amount = 40m,
            Currency = "USD",
            AmountUsd = 40m,
            Reference = "SP-PAY"
        });
        db.LedgerEntries.AddRange(
            new LedgerEntry
            {
                Id = 1,
                EntryDate = new DateTime(2026, 1, 2),
                Side = LedgerSide.Credit,
                AmountUsd = 100m,
                Description = "Service expense",
                SourceType = "Expense",
                SourceId = 1,
                Reference = "EXP-1",
                ContractId = 1,
                ServiceProviderId = 1
            },
            new LedgerEntry
            {
                Id = 2,
                EntryDate = new DateTime(2026, 1, 2),
                Side = LedgerSide.Debit,
                AmountUsd = 40m,
                Description = "Service payment",
                SourceType = nameof(PaymentKind.ServiceProviderPayment),
                SourceId = 1,
                Reference = "SP-PAY",
                ContractId = 1,
                ServiceProviderId = 1
            },
            new LedgerEntry
            {
                Id = 3,
                EntryDate = new DateTime(2026, 1, 3),
                Side = LedgerSide.Credit,
                AmountUsd = 50m,
                Description = "Cancelled service expense",
                SourceType = "Expense",
                SourceId = 2,
                Reference = "EXP-2",
                ContractId = 1,
                ServiceProviderId = 1
            },
            new LedgerEntry
            {
                Id = 4,
                EntryDate = new DateTime(2026, 1, 4),
                Side = LedgerSide.Debit,
                AmountUsd = 50m,
                Description = "Cancel reversal",
                SourceType = "Expense",
                SourceId = 2,
                Reference = "EXP-2-CANCEL",
                ContractId = 1,
                ServiceProviderId = 1
            });
    }

    private sealed class TestTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values)
        {
        }
    }

    private sealed class LocalOnlyUrlHelper : IUrlHelper
    {
        public ActionContext ActionContext { get; } = new();
        public string? Action(UrlActionContext actionContext) => "/";
        public string? Content(string? contentPath) => contentPath;
        public bool IsLocalUrl(string? url) => url?.StartsWith('/') == true && !url.StartsWith("//");
        public string? Link(string? routeName, object? values) => "/";
        public string? RouteUrl(UrlRouteContext routeContext) => "/";
    }
}
