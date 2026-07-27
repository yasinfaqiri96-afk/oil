using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Accounting;
using PTGOilSystem.Web.Services.Exceptions;
using Xunit;

namespace PTGOilSystem.Web.Tests;

// تخصیص دریافت مشتری به پیش‌فروش: جزئی، چندگانه و قابل برگشت — بدون هیچ سند مالی جدید.
public class CustomerPaymentAllocationServiceTests
{
    [Fact]
    public async Task One_Payment_Can_Be_Allocated_To_Two_PreSales()
    {
        await using var db = NewDb();
        Seed(db);
        AddPreSale(db, id: 1, totalUsd: 10_000m);
        AddPreSale(db, id: 2, totalUsd: 10_000m);
        AddPayment(db, id: 1, amount: 5_000m);
        await db.SaveChangesAsync();

        var service = new CustomerPaymentAllocationService(db);

        await service.AllocateAsync(NewRequest(paymentId: 1, preSaleId: 1, amount: 3_000m));
        await service.AllocateAsync(NewRequest(paymentId: 1, preSaleId: 2, amount: 2_000m));

        Assert.Equal(2, await db.CustomerPaymentAllocations.CountAsync());
        Assert.Equal(0m, await service.GetAllocatablePaymentAmountAsync(1));
        Assert.Empty(db.LedgerEntries);
        Assert.Empty(db.JournalEntries);
    }

    [Fact]
    public async Task One_PreSale_Can_Receive_Several_Payments()
    {
        await using var db = NewDb();
        Seed(db);
        AddPreSale(db, id: 1, totalUsd: 10_000m);
        AddPayment(db, id: 1, amount: 4_000m);
        AddPayment(db, id: 2, amount: 6_000m);
        await db.SaveChangesAsync();

        var service = new CustomerPaymentAllocationService(db);
        await service.AllocateAsync(NewRequest(1, 1, 4_000m));
        await service.AllocateAsync(NewRequest(2, 1, 6_000m));

        Assert.Equal(0m, await service.GetUnallocatedPreSaleAmountUsdAsync(1));
    }

    [Fact]
    public async Task Partial_Allocation_Keeps_The_Rest_Of_The_Payment_Free()
    {
        await using var db = NewDb();
        Seed(db);
        AddPreSale(db, id: 1, totalUsd: 10_000m);
        AddPayment(db, id: 1, amount: 5_000m);
        await db.SaveChangesAsync();

        var service = new CustomerPaymentAllocationService(db);
        await service.AllocateAsync(NewRequest(1, 1, 1_500m));

        Assert.Equal(3_500m, await service.GetAllocatablePaymentAmountAsync(1));
        Assert.Equal(8_500m, await service.GetUnallocatedPreSaleAmountUsdAsync(1));
    }

    [Fact]
    public async Task Allocating_More_Than_The_Payment_Balance_Is_Rejected()
    {
        await using var db = NewDb();
        Seed(db);
        AddPreSale(db, id: 1, totalUsd: 10_000m);
        AddPayment(db, id: 1, amount: 5_000m);
        await db.SaveChangesAsync();

        var service = new CustomerPaymentAllocationService(db);
        await service.AllocateAsync(NewRequest(1, 1, 4_000m));

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => service.AllocateAsync(NewRequest(1, 1, 1_500m)));

        Assert.Equal("CUSTOMER_ALLOCATION_OVER_PAYMENT", error.Code);
        Assert.Equal(1, await db.CustomerPaymentAllocations.CountAsync());
    }

    [Fact]
    public async Task Allocating_More_Than_The_PreSale_Value_Is_Rejected()
    {
        await using var db = NewDb();
        Seed(db);
        AddPreSale(db, id: 1, totalUsd: 2_000m);
        AddPayment(db, id: 1, amount: 5_000m);
        await db.SaveChangesAsync();

        var service = new CustomerPaymentAllocationService(db);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => service.AllocateAsync(NewRequest(1, 1, 2_500m)));

        Assert.Equal("CUSTOMER_ALLOCATION_OVER_PRESALE", error.Code);
        Assert.Empty(db.CustomerPaymentAllocations);
    }

    [Fact]
    public async Task Reversing_An_Allocation_Frees_The_Payment_Balance_Again()
    {
        await using var db = NewDb();
        Seed(db);
        AddPreSale(db, id: 1, totalUsd: 10_000m);
        AddPayment(db, id: 1, amount: 5_000m);
        await db.SaveChangesAsync();

        var service = new CustomerPaymentAllocationService(db);
        var allocation = await service.AllocateAsync(NewRequest(1, 1, 5_000m));
        Assert.Equal(0m, await service.GetAllocatablePaymentAmountAsync(1));

        await service.ReverseAsync(new CustomerPaymentAllocationReverseRequest(allocation.Id, "تست"));

        Assert.Equal(5_000m, await service.GetAllocatablePaymentAmountAsync(1));
        Assert.Equal(10_000m, await service.GetUnallocatedPreSaleAmountUsdAsync(1));

        // رکورد حذف نمی‌شود، فقط برگشت می‌خورد.
        var stored = await db.CustomerPaymentAllocations.SingleAsync();
        Assert.Equal(CustomerPaymentAllocationStatus.Reversed, stored.Status);
        Assert.NotNull(stored.ReversedAtUtc);
    }

    [Fact]
    public async Task An_Allocation_With_Active_Applications_Cannot_Be_Reversed_Directly()
    {
        await using var db = NewDb();
        Seed(db);
        AddPreSale(db, id: 1, totalUsd: 10_000m);
        AddPayment(db, id: 1, amount: 5_000m);
        await db.SaveChangesAsync();

        var service = new CustomerPaymentAllocationService(db);
        var allocation = await service.AllocateAsync(NewRequest(1, 1, 5_000m));

        // یک مصرفِ فعال روی این تخصیص (مثلاً از یک تحویل) ثبت می‌کنیم.
        db.CustomerPaymentAllocationApplications.Add(new CustomerPaymentAllocationApplication
        {
            CustomerPaymentAllocationId = allocation.Id,
            SalesTransactionId = 1,
            AppliedAt = new DateTime(2026, 6, 5),
            AppliedPaymentAmount = 5_000m,
            PaymentCurrencyCode = "USD",
            AppliedAmountUsd = 5_000m,
            Status = CustomerPaymentAllocationApplicationStatus.Active
        });
        await db.SaveChangesAsync();

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => service.ReverseAsync(new CustomerPaymentAllocationReverseRequest(allocation.Id, "تست")));

        Assert.Equal("CUSTOMER_ALLOCATION_HAS_APPLICATIONS", error.Code);
        // تخصیص هنوز فعال است چون مصرفش آزاد نشده.
        Assert.Equal(CustomerPaymentAllocationStatus.Active,
            (await db.CustomerPaymentAllocations.SingleAsync()).Status);
    }

    [Fact]
    public async Task Unapplied_Allocation_Balance_Reflects_Active_Applications()
    {
        await using var db = NewDb();
        Seed(db);
        AddPreSale(db, id: 1, totalUsd: 10_000m);
        AddPayment(db, id: 1, amount: 5_000m);
        await db.SaveChangesAsync();

        var service = new CustomerPaymentAllocationService(db);
        var allocation = await service.AllocateAsync(NewRequest(1, 1, 5_000m));
        Assert.Equal(5_000m, await service.GetUnappliedAllocationUsdAsync(allocation.Id));

        db.CustomerPaymentAllocationApplications.Add(new CustomerPaymentAllocationApplication
        {
            CustomerPaymentAllocationId = allocation.Id,
            SalesTransactionId = 1,
            AppliedAt = new DateTime(2026, 6, 5),
            AppliedPaymentAmount = 2_000m,
            PaymentCurrencyCode = "USD",
            AppliedAmountUsd = 2_000m,
            Status = CustomerPaymentAllocationApplicationStatus.Active
        });
        await db.SaveChangesAsync();

        Assert.Equal(3_000m, await service.GetUnappliedAllocationUsdAsync(allocation.Id));
    }

    [Fact]
    public async Task Payment_Already_Spent_On_Another_PreSale_Cannot_Be_Spent_Twice()
    {
        await using var db = NewDb();
        Seed(db);
        AddPreSale(db, id: 1, totalUsd: 10_000m);
        AddPreSale(db, id: 2, totalUsd: 10_000m);
        AddPayment(db, id: 1, amount: 5_000m);
        await db.SaveChangesAsync();

        var service = new CustomerPaymentAllocationService(db);
        await service.AllocateAsync(NewRequest(1, 1, 5_000m));

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => service.AllocateAsync(NewRequest(1, 2, 1m)));

        Assert.Equal("CUSTOMER_ALLOCATION_OVER_PAYMENT", error.Code);
    }

    [Fact]
    public async Task Payment_Without_A_Valid_Fx_Rate_Cannot_Be_Allocated()
    {
        await using var db = NewDb();
        Seed(db);
        AddPreSale(db, id: 1, totalUsd: 10_000m);
        AddPayment(db, id: 1, amount: 100_000m, currency: "RUB", fxRateToUsd: null);
        await db.SaveChangesAsync();

        var service = new CustomerPaymentAllocationService(db);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => service.AllocateAsync(NewRequest(1, 1, 10_000m)));

        Assert.Equal("CUSTOMER_ALLOCATION_INVALID_FX", error.Code);
        Assert.Empty(db.CustomerPaymentAllocations);
    }

    [Fact]
    public async Task Foreign_Currency_Payment_Uses_Its_Own_Payment_Day_Rate()
    {
        await using var db = NewDb();
        Seed(db);
        AddPreSale(db, id: 1, totalUsd: 10_000m);
        AddPayment(db, id: 1, amount: 100_000m, currency: "RUB", fxRateToUsd: 0.01m);
        await db.SaveChangesAsync();

        var allocation = await new CustomerPaymentAllocationService(db)
            .AllocateAsync(NewRequest(1, 1, 50_000m));

        Assert.Equal("RUB", allocation.PaymentCurrencyCode);
        Assert.Equal(0.01m, allocation.PaymentFxRateToUsd);   // نرخ روز دریافت، بدون بازارزیابی
        Assert.Equal(500m, allocation.AllocatedAmountUsd);
    }

    [Fact]
    public async Task Payment_Of_Another_Customer_Is_Rejected()
    {
        await using var db = NewDb();
        Seed(db);
        db.Customers.Add(new Customer { Id = 2, Name = "Other" });
        AddPreSale(db, id: 1, totalUsd: 10_000m);
        AddPayment(db, id: 1, amount: 5_000m, customerId: 2);
        await db.SaveChangesAsync();

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => new CustomerPaymentAllocationService(db).AllocateAsync(NewRequest(1, 1, 1_000m)));

        Assert.Equal("CUSTOMER_ALLOCATION_CUSTOMER_MISMATCH", error.Code);
    }

    // رفتار تخصیص تأمین‌کننده نباید با کار مشتری عوض شود: شماره‌گذاری ژورنال و SourceEventId آن ثابت است.
    [Fact]
    public void SupplierPaymentAllocation_Numbering_Is_Unchanged()
    {
        var generator = new AccountingJournalNumberGenerator();

        Assert.Equal("SPA-000001-0000000007", generator.ForSupplierPaymentAllocation(1, 7));
        Assert.Equal("SPAR-000001-0000000007", generator.ForSupplierPaymentAllocationReversal(1, 7));
        Assert.Equal(
            "SupplierPaymentAllocation:7:Created",
            SupplierPaymentAllocationAccountingAdapter.BuildCreatedSourceEventId(7));
        Assert.Equal(
            "SupplierPaymentAllocation:7:Reversed",
            SupplierPaymentAllocationAccountingAdapter.BuildReversedSourceEventId(7));
    }

    // ---------- helpers ----------

    private static ApplicationDbContext NewDb()
        => new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static CustomerPaymentAllocationCreateRequest NewRequest(
        int paymentId, int preSaleId, decimal amount)
        => new(paymentId, preSaleId, new DateTime(2026, 6, 1), amount);

    private static void Seed(ApplicationDbContext db)
    {
        db.Currencies.Add(new Currency { Id = 1, Code = "USD", Name = "US Dollar", Symbol = "$", IsActive = true });
        db.Customers.Add(new Customer { Id = 1, Name = "Herat Market" });
        db.Products.Add(new Product { Id = 1, Code = "GO", Name = "Gas Oil" });
        db.CashAccounts.Add(new CashAccount { Id = 1, Name = "Main", Currency = "USD" });
    }

    private static void AddPreSale(ApplicationDbContext db, int id, decimal totalUsd)
        => db.PreSaleOrders.Add(new PreSaleOrder
        {
            Id = id,
            OrderNumber = $"PRE-{id}",
            CustomerId = 1,
            ProductId = 1,
            OrderDate = new DateTime(2026, 5, 1),
            QuantityMt = 20m,
            Currency = "USD",
            UnitPriceInCurrency = totalUsd / 20m,
            AppliedFxRateToUsd = 1m,
            UnitPriceUsd = totalUsd / 20m,
            TotalInCurrency = totalUsd,
            TotalUsd = totalUsd,
            Status = PreSaleOrderStatus.Confirmed
        });

    private static void AddPayment(
        ApplicationDbContext db,
        int id,
        decimal amount,
        string currency = "USD",
        decimal? fxRateToUsd = 1m,
        int customerId = 1)
        => db.PaymentTransactions.Add(new PaymentTransaction
        {
            Id = id,
            PaymentDate = new DateTime(2026, 5, 20),
            Direction = PaymentDirection.In,
            PaymentKind = PaymentKind.CustomerReceipt,
            CashAccountId = 1,
            CustomerId = customerId,
            Amount = amount,
            Currency = currency,
            AppliedFxRateToUsd = fxRateToUsd,
            AmountUsd = amount * (fxRateToUsd ?? 0m),
            IsCustomerAdvance = true
        });
}
