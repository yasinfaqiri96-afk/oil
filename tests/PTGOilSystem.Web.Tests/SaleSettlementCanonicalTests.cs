using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PTGOilSystem.Web.Controllers;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Customers;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.Sales;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.DeleteSafety;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// «طلبِ باز یک فروش» یک مرجع دارد (<see cref="CustomerReceiptApplicationService.GetSaleSettlementsAsync"/>):
/// تطبیقِ پیش‌دریافت هم وصولی است. صفحهٔ فروش، صفحهٔ مشتری و داشبورد باید یک جواب بدهند.
/// </summary>
public sealed class SaleSettlementCanonicalTests
{
    [Fact]
    public async Task Sale_Fully_Settled_By_Advance_Allocation_Is_Settled_Everywhere()
    {
        await using var db = NewDb();
        Seed(db);
        db.PaymentTransactions.Add(Advance(1, 1000m));
        db.CustomerPaymentAllocationApplications.Add(Application(1, paymentId: 1, saleId: 1, 1000m));
        await db.SaveChangesAsync();

        var settlement = (await new CustomerReceiptApplicationService(db).GetSaleSettlementsAsync([1]))[1];
        var saleDetails = await SaleDetailsAsync(db, 1);
        var customerSale = await CustomerSaleAsync(db, 1);
        var dashboard = await new DashboardService(db, new HttpContextAccessor()).BuildDashboardAsync();

        Assert.Equal(0m, settlement.OpenReceivableUsd(1000m));
        Assert.Equal(0m, saleDetails.ReceivableBalanceUsd);
        Assert.Equal(0m, customerSale.ReceivableUsd);
        Assert.Equal(0, dashboard.SalesWithoutPaymentCount);
    }

    [Fact]
    public async Task Partial_Allocation_And_Direct_Payment_Leave_The_Same_Open_Amount_Everywhere()
    {
        await using var db = NewDb();
        Seed(db);
        db.PaymentTransactions.AddRange(
            Advance(1, 400m),
            new PaymentTransaction
            {
                Id = 2,
                PaymentDate = new DateTime(2026, 6, 3),
                Direction = PaymentDirection.In,
                PaymentKind = PaymentKind.CustomerReceipt,
                CustomerId = 1,
                SalesTransactionId = 1,
                Amount = 100m,
                Currency = "USD",
                AmountUsd = 100m
            });
        db.CustomerPaymentAllocationApplications.Add(Application(1, paymentId: 1, saleId: 1, 400m));
        await db.SaveChangesAsync();

        var settlement = (await new CustomerReceiptApplicationService(db).GetSaleSettlementsAsync([1]))[1];
        var saleDetails = await SaleDetailsAsync(db, 1);
        var customerSale = await CustomerSaleAsync(db, 1);

        Assert.Equal(500m, settlement.OpenReceivableUsd(1000m));
        Assert.Equal(500m, saleDetails.ReceivableBalanceUsd);
        Assert.Equal(500m, customerSale.ReceivableUsd);
    }

    private static async Task<SalesDetailsViewModel> SaleDetailsAsync(ApplicationDbContext db, int saleId)
    {
        var controller = new SalesController(db, new StockService(db), new AuditService(db), NullLogger<SalesController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
            TempData = new TempDataDictionary(new DefaultHttpContext(), new EmptyTempDataProvider())
        };
        return Assert.IsType<SalesDetailsViewModel>(Assert.IsType<ViewResult>(await controller.Details(saleId)).Model);
    }

    private static async Task<CustomerSaleSummaryViewModel> CustomerSaleAsync(ApplicationDbContext db, int saleId)
    {
        var controller = new CustomersController(db, new AuditService(db), new MasterDataDeleteSafetyService(db))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
            TempData = new TempDataDictionary(new DefaultHttpContext(), new EmptyTempDataProvider())
        };
        var profile = Assert.IsType<CustomerProfileViewModel>(Assert.IsType<ViewResult>(await controller.Details(1)).Model);
        return Assert.Single(profile.Sales, s => s.SaleId == saleId);
    }

    private static void Seed(ApplicationDbContext db)
    {
        db.Units.Add(new Unit { Id = 1, Code = "MT", Name = "Metric Ton", Symbol = "MT", IsActive = true });
        db.Companies.Add(new Company { Id = 1, Code = "PTG", Name = "PTG", IsActive = true });
        db.Products.Add(new Product { Id = 1, Code = "GO", Name = "Gas Oil", UnitId = 1, UnitOfMeasure = "MT", IsActive = true });
        db.Customers.Add(new Customer { Id = 1, Name = "Customer A", IsActive = true });
        db.SalesTransactions.Add(new SalesTransaction
        {
            Id = 1,
            CompanyId = 1,
            CustomerId = 1,
            ProductId = 1,
            SaleDate = new DateTime(2026, 6, 2),
            InvoiceNumber = "INV-1",
            QuantityMt = 10m,
            UnitPriceUsd = 100m,
            TotalUsd = 1000m
        });
    }

    private static PaymentTransaction Advance(int id, decimal amountUsd) => new()
    {
        Id = id,
        PaymentDate = new DateTime(2026, 6, 1),
        Direction = PaymentDirection.In,
        PaymentKind = PaymentKind.CustomerReceipt,
        CustomerId = 1,
        IsCustomerAdvance = true,
        Amount = amountUsd,
        Currency = "USD",
        AmountUsd = amountUsd
    };

    private static CustomerPaymentAllocationApplication Application(int id, int paymentId, int saleId, decimal amountUsd) => new()
    {
        Id = id,
        PaymentTransactionId = paymentId,
        SalesTransactionId = saleId,
        AppliedAt = new DateTime(2026, 6, 2),
        AppliedPaymentAmount = amountUsd,
        PaymentCurrencyCode = "USD",
        AppliedAmountUsd = amountUsd,
        Status = CustomerPaymentAllocationApplicationStatus.Active
    };

    private static ApplicationDbContext NewDb()
        => new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private sealed class EmptyTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();

        public void SaveTempData(HttpContext context, IDictionary<string, object> values)
        {
        }
    }
}
