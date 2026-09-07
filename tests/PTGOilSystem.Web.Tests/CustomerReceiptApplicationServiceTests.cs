using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Exceptions;
using Xunit;

namespace PTGOilSystem.Web.Tests;

// تطبیقِ نقدِ یک دریافت با چند فروش — همان چیزی که فروش گروهی لازم دارد: یک دریافت، ۵۲ فاکتور،
// و سهم هر فاکتور یک ردیف واقعیِ ذخیره‌شده. این مسیر هیچ سند مالی جدیدی نمی‌سازد.
public class CustomerReceiptApplicationServiceTests
{
    [Fact]
    public async Task One_Receipt_Settles_Every_Line_Of_A_Group_Sale()
    {
        await using var db = NewDb();
        Seed(db);
        for (var i = 1; i <= 3; i++)
        {
            AddSale(db, id: i, totalUsd: 1_000m);
        }

        AddReceipt(db, id: 1, amount: 3_000m);
        await db.SaveChangesAsync();

        var service = new CustomerReceiptApplicationService(db);
        var plan = await service.BuildFifoPlanAsync(1);

        Assert.Equal(3, plan.Count);
        Assert.All(plan, line => Assert.Equal(1_000m, line.AmountUsd));

        await service.ApplyAsync(new CustomerReceiptApplyRequest(1, plan));

        Assert.Equal(3, await db.CustomerPaymentAllocationApplications.CountAsync());
        Assert.Equal(0m, await service.GetOpenReceivableUsdAsync(1));
        Assert.Equal(0m, await service.GetOpenReceivableUsdAsync(2));
        Assert.Equal(0m, await service.GetOpenReceivableUsdAsync(3));
        Assert.Equal(0m, await service.GetUnappliedReceiptAmountAsync(1));

        // تطبیق فقط اطلاعات است: هیچ ژورنال و هیچ سطر دفتر کلی ساخته نمی‌شود.
        Assert.Empty(db.LedgerEntries);
        Assert.Empty(db.JournalEntries);
    }

    [Fact]
    public async Task A_Partial_Receipt_Leaves_The_Remaining_Lines_Open()
    {
        await using var db = NewDb();
        Seed(db);
        AddSale(db, id: 1, totalUsd: 1_000m);
        AddSale(db, id: 2, totalUsd: 1_000m);
        AddReceipt(db, id: 1, amount: 1_500m);
        await db.SaveChangesAsync();

        var service = new CustomerReceiptApplicationService(db);
        await service.ApplyAsync(new CustomerReceiptApplyRequest(1, await service.BuildFifoPlanAsync(1)));

        Assert.Equal(0m, await service.GetOpenReceivableUsdAsync(1));
        Assert.Equal(500m, await service.GetOpenReceivableUsdAsync(2));
        Assert.Equal(0m, await service.GetUnappliedReceiptAmountAsync(1));
    }

    [Fact]
    public async Task A_Receipt_Cannot_Be_Applied_Beyond_Its_Own_Balance()
    {
        await using var db = NewDb();
        Seed(db);
        AddSale(db, id: 1, totalUsd: 5_000m);
        AddReceipt(db, id: 1, amount: 1_000m);
        await db.SaveChangesAsync();

        var service = new CustomerReceiptApplicationService(db);
        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => service.ApplyAsync(
            new CustomerReceiptApplyRequest(1, [new CustomerReceiptApplicationLine(1, 2_000m)])));

        Assert.Equal("CUSTOMER_APPLICATION_OVER_RECEIPT", ex.Code);
        Assert.Empty(db.CustomerPaymentAllocationApplications);
    }

    [Fact]
    public async Task A_Sale_Cannot_Receive_More_Than_Its_Open_Receivable()
    {
        await using var db = NewDb();
        Seed(db);
        AddSale(db, id: 1, totalUsd: 800m);
        AddReceipt(db, id: 1, amount: 5_000m);
        await db.SaveChangesAsync();

        var service = new CustomerReceiptApplicationService(db);
        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => service.ApplyAsync(
            new CustomerReceiptApplyRequest(1, [new CustomerReceiptApplicationLine(1, 900m)])));

        Assert.Equal("CUSTOMER_APPLICATION_OVER_SALE", ex.Code);
    }

    [Fact]
    public async Task A_Payment_Already_Linked_To_The_Sale_Is_Never_Counted_Twice()
    {
        await using var db = NewDb();
        Seed(db);
        AddSale(db, id: 1, totalUsd: 1_000m);
        AddReceipt(db, id: 1, amount: 1_000m, salesTransactionId: 1);
        await db.SaveChangesAsync();

        var service = new CustomerReceiptApplicationService(db);

        // پرداختِ مستقیماً وصل‌شده همین حالا در «دریافت‌شده» شمرده می‌شود.
        Assert.Equal(0m, await service.GetOpenReceivableUsdAsync(1));

        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => service.ApplyAsync(
            new CustomerReceiptApplyRequest(1, [new CustomerReceiptApplicationLine(1, 1_000m)])));

        Assert.Equal("CUSTOMER_APPLICATION_ALREADY_LINKED", ex.Code);
    }

    [Fact]
    public async Task Reversing_An_Application_Frees_Both_The_Receipt_And_The_Sale()
    {
        await using var db = NewDb();
        Seed(db);
        AddSale(db, id: 1, totalUsd: 1_000m);
        AddReceipt(db, id: 1, amount: 1_000m);
        await db.SaveChangesAsync();

        var service = new CustomerReceiptApplicationService(db);
        await service.ApplyAsync(new CustomerReceiptApplyRequest(1, [new CustomerReceiptApplicationLine(1, 1_000m)]));

        var application = await db.CustomerPaymentAllocationApplications.SingleAsync();
        await service.ReverseApplicationAsync(application.Id, "اشتباه ثبت شد");

        Assert.Equal(1_000m, await service.GetOpenReceivableUsdAsync(1));
        Assert.Equal(1_000m, await service.GetUnappliedReceiptAmountAsync(1));

        // رکورد حذف نمی‌شود؛ فقط برگشت می‌خورد.
        var stored = await db.CustomerPaymentAllocationApplications.SingleAsync();
        Assert.Equal(CustomerPaymentAllocationApplicationStatus.Reversed, stored.Status);
    }

    [Fact]
    public async Task A_Non_Usd_Receipt_Applies_At_Its_Own_Locked_Rate()
    {
        await using var db = NewDb();
        Seed(db);
        AddSale(db, id: 1, totalUsd: 1_000m);
        AddReceipt(db, id: 1, amount: 90_000m, currency: "RUB", fxRateToUsd: 0.0125m);
        await db.SaveChangesAsync();

        var service = new CustomerReceiptApplicationService(db);
        await service.ApplyAsync(new CustomerReceiptApplyRequest(1, [new CustomerReceiptApplicationLine(1, 1_000m)]));

        var application = await db.CustomerPaymentAllocationApplications.SingleAsync();
        Assert.Equal(1_000m, application.AppliedAmountUsd);
        Assert.Equal(80_000m, application.AppliedPaymentAmount);
        Assert.Equal("RUB", application.PaymentCurrencyCode);
        Assert.Equal(10_000m, await service.GetUnappliedReceiptAmountAsync(1));
    }

    [Fact]
    public async Task A_Receipt_Of_Another_Customer_Is_Refused()
    {
        await using var db = NewDb();
        Seed(db);
        db.Customers.Add(new Customer { Id = 2, Name = "Kabul Market" });
        AddSale(db, id: 1, totalUsd: 1_000m);
        AddReceipt(db, id: 1, amount: 1_000m, customerId: 2);
        await db.SaveChangesAsync();

        var service = new CustomerReceiptApplicationService(db);
        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => service.ApplyAsync(
            new CustomerReceiptApplyRequest(1, [new CustomerReceiptApplicationLine(1, 1_000m)])));

        Assert.Equal("CUSTOMER_APPLICATION_CUSTOMER_MISMATCH", ex.Code);
    }

    [Fact]
    public async Task An_Advance_Receipt_Keeps_Using_The_PreSale_Allocation_Path()
    {
        await using var db = NewDb();
        Seed(db);
        AddSale(db, id: 1, totalUsd: 1_000m);
        AddReceipt(db, id: 1, amount: 1_000m, isAdvance: true);
        await db.SaveChangesAsync();

        var service = new CustomerReceiptApplicationService(db);
        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => service.ApplyAsync(
            new CustomerReceiptApplyRequest(1, [new CustomerReceiptApplicationLine(1, 1_000m)])));

        Assert.Equal("CUSTOMER_APPLICATION_IS_ADVANCE", ex.Code);
    }

    [Fact]
    public async Task A_Cancelled_Sale_Is_Not_Offered_And_Not_Applicable()
    {
        await using var db = NewDb();
        Seed(db);
        AddSale(db, id: 1, totalUsd: 1_000m, isCancelled: true);
        AddSale(db, id: 2, totalUsd: 1_000m);
        AddReceipt(db, id: 1, amount: 2_000m);
        await db.SaveChangesAsync();

        var service = new CustomerReceiptApplicationService(db);
        var open = await service.GetOpenSalesAsync(customerId: 1);

        Assert.Single(open);
        Assert.Equal(2, open[0].SalesTransactionId);

        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => service.ApplyAsync(
            new CustomerReceiptApplyRequest(1, [new CustomerReceiptApplicationLine(1, 500m)])));

        Assert.Equal("CUSTOMER_APPLICATION_SALE_CANCELLED", ex.Code);
    }

    [Fact]
    public async Task The_Plan_Can_Be_Limited_To_One_Group_Sale()
    {
        await using var db = NewDb();
        Seed(db);
        AddSale(db, id: 1, totalUsd: 1_000m, salesBatchId: 7);
        AddSale(db, id: 2, totalUsd: 1_000m, salesBatchId: 7);
        AddSale(db, id: 3, totalUsd: 1_000m);
        AddReceipt(db, id: 1, amount: 5_000m);
        await db.SaveChangesAsync();

        var service = new CustomerReceiptApplicationService(db);
        var plan = await service.BuildFifoPlanAsync(1, salesBatchId: 7);

        Assert.Equal(2, plan.Count);
        Assert.DoesNotContain(plan, l => l.SalesTransactionId == 3);
    }

    private static ApplicationDbContext NewDb()
        => new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static void Seed(ApplicationDbContext db)
    {
        db.Currencies.Add(new Currency { Id = 1, Code = "USD", Name = "US Dollar", Symbol = "$", IsActive = true });
        db.Customers.Add(new Customer { Id = 1, Name = "Herat Market" });
        db.Products.Add(new Product { Id = 1, Code = "GO", Name = "Gas Oil" });
        db.CashAccounts.Add(new CashAccount { Id = 1, Name = "Main", Currency = "USD" });
    }

    private static void AddSale(
        ApplicationDbContext db,
        int id,
        decimal totalUsd,
        bool isCancelled = false,
        int? salesBatchId = null)
        => db.SalesTransactions.Add(new SalesTransaction
        {
            Id = id,
            InvoiceNumber = $"INV-{id}",
            CustomerId = 1,
            ProductId = 1,
            SaleDate = new DateTime(2026, 5, id),
            QuantityMt = 20m,
            Currency = "USD",
            UnitPriceInCurrency = totalUsd / 20m,
            AppliedFxRateToUsd = 1m,
            UnitPriceUsd = totalUsd / 20m,
            TotalInCurrency = totalUsd,
            TotalUsd = totalUsd,
            IsCancelled = isCancelled,
            SalesBatchId = salesBatchId
        });

    private static void AddReceipt(
        ApplicationDbContext db,
        int id,
        decimal amount,
        string currency = "USD",
        decimal? fxRateToUsd = 1m,
        int customerId = 1,
        bool isAdvance = false,
        int? salesTransactionId = null)
        => db.PaymentTransactions.Add(new PaymentTransaction
        {
            Id = id,
            PaymentDate = new DateTime(2026, 5, 20),
            Direction = PaymentDirection.In,
            PaymentKind = PaymentKind.CustomerReceipt,
            CashAccountId = 1,
            CustomerId = customerId,
            SalesTransactionId = salesTransactionId,
            Amount = amount,
            Currency = currency,
            AppliedFxRateToUsd = fxRateToUsd,
            AmountUsd = amount * (fxRateToUsd ?? 0m),
            IsCustomerAdvance = isAdvance
        });
}
