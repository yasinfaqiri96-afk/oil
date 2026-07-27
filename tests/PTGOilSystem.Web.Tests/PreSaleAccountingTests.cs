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
/// پیش‌فروش در دفتر کل جدید:
///   • لغو تحویل باید ژورنال درآمد و ژورنال بهای تمام‌شده را با ژورنال معکوسِ متوازن برگرداند
///     و ارزش را به pool موجودی بازگرداند — بدون حذف هیچ سندی و بدون امکان ثبت دوباره.
///   • تحویل پیش‌فروشی که پیش‌دریافت تخصیص‌یافته دارد باید همان مقدار را از بدهیِ پیش‌دریافت
///     مصرف کند و فقط باقیمانده را مطالبات کند.
/// </summary>
[Collection(AccountingPostgreSqlCollection.CollectionName)]
public sealed class PreSaleAccountingTests(AccountingPostgreSqlFixture fixture)
{
    private static readonly DateTime SaleDate = new(2026, 7, 5);
    private static readonly DateTime ReversalDate = new(2026, 7, 20);

    [Fact]
    public void Reversal_SourceEventId_Formats_Are_Stable()
    {
        Assert.Equal("Sale:7:Reversed", SalesAccountingAdapter.BuildReversedSourceEventId(7));
        Assert.Equal("Sale:7:CogsReversed", SalesAccountingAdapter.BuildCogsReversedSourceEventId(7));
    }

    [Fact]
    public async Task Cancelling_A_Delivery_Reverses_Revenue_And_Cost_With_Balanced_Journals()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var order = await AddPreSaleAsync(db, scope, totalUsd: 10_000m);
        var sale = await AddDeliveryAsync(db, scope, order, quantityMt: 10m, totalUsd: 5_000m);

        await new InventoryValuationService(db).ApplyReceiptAsync(
            scope.Company.Id, scope.Product.Id, scope.Terminal.Id, 10m, 4_000m);
        await AddOutMovementAsync(db, scope, sale, 10m);

        var adapter = CreateAdapter(db);
        Assert.Equal(PaymentPostingStatus.Posted, (await adapter.TryPostSaleAsync(sale)).Status);
        Assert.Equal(PaymentPostingStatus.Posted, (await adapter.TryPostCogsAsync(sale)).Status);

        // ── لغو ──
        sale.IsCancelled = true;
        await db.SaveChangesAsync();

        var saleReversal = await adapter.TryReverseSaleAsync(sale, ReversalDate);
        var cogsReversal = await adapter.TryReverseCogsAsync(sale, ReversalDate);

        Assert.Equal(PaymentPostingStatus.Posted, saleReversal.Status);
        Assert.Equal(PaymentPostingStatus.Posted, cogsReversal.Status);

        var original = await LoadJournalAsync(db, SalesAccountingAdapter.BuildCreatedSourceEventId(sale.Id));
        var reversed = await LoadJournalAsync(db, SalesAccountingAdapter.BuildReversedSourceEventId(sale.Id));
        var originalCogs = await LoadJournalAsync(db, SalesAccountingAdapter.BuildCogsSourceEventId(sale.Id));
        var reversedCogs = await LoadJournalAsync(db, SalesAccountingAdapter.BuildCogsReversedSourceEventId(sale.Id));

        // ژورنال معکوس متوازن است و به سند اصلی لینک دارد؛ سند اصلی حذف نشده است.
        AssertBalanced(reversed);
        AssertBalanced(reversedCogs);
        Assert.True(reversed.IsReversal);
        Assert.Equal(original.Id, reversed.ReversalOfJournalEntryId);
        Assert.Equal(originalCogs.Id, reversedCogs.ReversalOfJournalEntryId);
        Assert.Equal(JournalEntryStatus.Posted, original.Status);

        // درآمد، مطالبات و بهای تمام‌شده خنثی شده‌اند.
        Assert.Equal(0m, NetOf(scope.Settings.SalesRevenueAccountId, original, reversed));
        Assert.Equal(0m, NetOf(scope.Settings.AccountsReceivableAccountId, original, reversed));
        Assert.Equal(0m, NetOf(scope.Settings.CostOfGoodsSoldAccountId, originalCogs, reversedCogs));
        Assert.Equal(0m, NetOf(scope.Settings.InventoryAccountId, originalCogs, reversedCogs));

        // ارزش موجودی به pool برگشته است.
        var pool = await db.InventoryAverageCosts.AsNoTracking().SingleAsync(
            x => x.CompanyId == scope.Company.Id
                && x.ProductId == scope.Product.Id
                && x.TerminalId == scope.Terminal.Id);
        Assert.Equal(10m, pool.QuantityMt);
        Assert.Equal(4_000m, pool.TotalValueUsd);
    }

    [Fact]
    public async Task Reversing_Twice_Does_Not_Create_A_Second_Journal()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var order = await AddPreSaleAsync(db, scope, totalUsd: 10_000m);
        var sale = await AddDeliveryAsync(db, scope, order, quantityMt: 5m, totalUsd: 2_500m);

        await new InventoryValuationService(db).ApplyReceiptAsync(
            scope.Company.Id, scope.Product.Id, scope.Terminal.Id, 5m, 2_000m);
        await AddOutMovementAsync(db, scope, sale, 5m);

        var adapter = CreateAdapter(db);
        await adapter.TryPostSaleAsync(sale);
        await adapter.TryPostCogsAsync(sale);

        sale.IsCancelled = true;
        await db.SaveChangesAsync();

        await adapter.TryReverseSaleAsync(sale, ReversalDate);
        await adapter.TryReverseCogsAsync(sale, ReversalDate);

        var secondSale = await adapter.TryReverseSaleAsync(sale, ReversalDate);
        var secondCogs = await adapter.TryReverseCogsAsync(sale, ReversalDate);

        Assert.Equal(PaymentPostingStatus.Duplicate, secondSale.Status);
        Assert.Equal(PaymentPostingStatus.Duplicate, secondCogs.Status);
        Assert.Equal("DUPLICATE_SOURCE_EVENT", secondSale.Reason);

        Assert.Equal(1, await db.JournalEntries.CountAsync(
            x => x.SourceEventId == SalesAccountingAdapter.BuildReversedSourceEventId(sale.Id)));
        Assert.Equal(1, await db.JournalEntries.CountAsync(
            x => x.SourceEventId == SalesAccountingAdapter.BuildCogsReversedSourceEventId(sale.Id)));

        // ارزش فقط یک بار به pool برگشته است.
        var pool = await db.InventoryAverageCosts.AsNoTracking().SingleAsync(
            x => x.CompanyId == scope.Company.Id
                && x.ProductId == scope.Product.Id
                && x.TerminalId == scope.Terminal.Id);
        Assert.Equal(5m, pool.QuantityMt);
        Assert.Equal(2_000m, pool.TotalValueUsd);
    }

    [Fact]
    public async Task Delivery_Consumes_The_Allocated_Advance_And_Bills_Only_The_Rest()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var order = await AddPreSaleAsync(db, scope, totalUsd: 10_000m);
        await AddAllocationAsync(db, scope, order, amountUsd: 4_000m);

        var sale = await AddDeliveryAsync(db, scope, order, quantityMt: 10m, totalUsd: 5_000m);

        Assert.Equal(PaymentPostingStatus.Posted, (await CreateAdapter(db).TryPostSaleAsync(sale)).Status);

        var journal = await LoadJournalAsync(db, SalesAccountingAdapter.BuildCreatedSourceEventId(sale.Id));
        AssertBalanced(journal);

        Assert.Equal(4_000m, DebitOf(journal, scope.Settings.CustomerAdvanceAccountId));
        Assert.Equal(1_000m, DebitOf(journal, scope.Settings.AccountsReceivableAccountId));
        Assert.Equal(5_000m, CreditOf(journal, scope.Settings.SalesRevenueAccountId));
    }

    [Fact]
    public async Task A_Second_Delivery_Cannot_Spend_The_Advance_Twice()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var order = await AddPreSaleAsync(db, scope, totalUsd: 10_000m);
        await AddAllocationAsync(db, scope, order, amountUsd: 4_000m);

        var first = await AddDeliveryAsync(db, scope, order, quantityMt: 10m, totalUsd: 5_000m);
        var second = await AddDeliveryAsync(db, scope, order, quantityMt: 10m, totalUsd: 5_000m);

        var adapter = CreateAdapter(db);
        await adapter.TryPostSaleAsync(first);
        await adapter.TryPostSaleAsync(second);

        var secondJournal = await LoadJournalAsync(db, SalesAccountingAdapter.BuildCreatedSourceEventId(second.Id));
        AssertBalanced(secondJournal);

        // پیش‌دریافت با تحویل اول مصرف شده است، پس تحویل دوم کاملاً مطالبات است.
        Assert.Equal(0m, DebitOf(secondJournal, scope.Settings.CustomerAdvanceAccountId));
        Assert.Equal(5_000m, DebitOf(secondJournal, scope.Settings.AccountsReceivableAccountId));
    }

    [Fact]
    public async Task A_Plain_Sale_Without_PreSale_Still_Posts_Receivable_Only()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var sale = await AddDeliveryAsync(db, scope, order: null, quantityMt: 10m, totalUsd: 5_000m);

        Assert.Equal(PaymentPostingStatus.Posted, (await CreateAdapter(db).TryPostSaleAsync(sale)).Status);

        var journal = await LoadJournalAsync(db, SalesAccountingAdapter.BuildCreatedSourceEventId(sale.Id));
        Assert.Equal(2, journal.Lines.Count);
        Assert.Equal(5_000m, DebitOf(journal, scope.Settings.AccountsReceivableAccountId));
        Assert.Equal(0m, DebitOf(journal, scope.Settings.CustomerAdvanceAccountId));
        AssertBalanced(journal);
    }

    // ---------- helpers ----------

    private static void AssertBalanced(JournalEntry journal)
    {
        var debit = journal.Lines.Sum(x => x.Debit);
        var credit = journal.Lines.Sum(x => x.Credit);
        Assert.True(debit > 0m);
        Assert.Equal(debit, credit);
    }

    private static decimal DebitOf(JournalEntry journal, int accountId)
        => journal.Lines.Where(x => x.AccountId == accountId).Sum(x => x.Debit);

    private static decimal CreditOf(JournalEntry journal, int accountId)
        => journal.Lines.Where(x => x.AccountId == accountId).Sum(x => x.Credit);

    private static decimal NetOf(int accountId, params JournalEntry[] journals)
        => journals.Sum(j => DebitOf(j, accountId) - CreditOf(j, accountId));

    private static async Task<JournalEntry> LoadJournalAsync(ApplicationDbContext db, string sourceEventId)
        => await db.JournalEntries
            .AsNoTracking()
            .Include(x => x.Lines)
            .SingleAsync(x => x.SourceEventId == sourceEventId);

    private static async Task<PreSaleOrder> AddPreSaleAsync(
        ApplicationDbContext db,
        PaymentAccountingAdapterTests.PaymentScope scope,
        decimal totalUsd)
    {
        var order = new PreSaleOrder
        {
            OrderNumber = PaymentAccountingAdapterTests.Unique("PRE"),
            CustomerId = scope.Customer.Id,
            ProductId = scope.Product.Id,
            CompanyId = scope.Company.Id,
            OrderDate = SaleDate,
            QuantityMt = 20m,
            Currency = "USD",
            UnitPriceInCurrency = totalUsd / 20m,
            AppliedFxRateToUsd = 1m,
            UnitPriceUsd = totalUsd / 20m,
            TotalInCurrency = totalUsd,
            TotalUsd = totalUsd,
            Status = PreSaleOrderStatus.Confirmed
        };
        db.PreSaleOrders.Add(order);
        await db.SaveChangesAsync();
        return order;
    }

    private static async Task AddAllocationAsync(
        ApplicationDbContext db,
        PaymentAccountingAdapterTests.PaymentScope scope,
        PreSaleOrder order,
        decimal amountUsd)
    {
        var payment = new PaymentTransaction
        {
            PaymentDate = SaleDate,
            Direction = PaymentDirection.In,
            PaymentKind = PaymentKind.CustomerReceipt,
            CompanyId = scope.Company.Id,
            CashAccountId = scope.CashAccount.Id,
            CustomerId = scope.Customer.Id,
            Amount = amountUsd,
            Currency = "USD",
            AppliedFxRateToUsd = 1m,
            AmountUsd = amountUsd,
            IsCustomerAdvance = true
        };
        db.PaymentTransactions.Add(payment);
        await db.SaveChangesAsync();

        db.CustomerPaymentAllocations.Add(new CustomerPaymentAllocation
        {
            PaymentTransactionId = payment.Id,
            PreSaleOrderId = order.Id,
            AllocationDate = SaleDate,
            AllocatedPaymentAmount = amountUsd,
            PaymentCurrencyCode = "USD",
            PaymentFxRateToUsd = 1m,
            AllocatedAmountUsd = amountUsd,
            Status = CustomerPaymentAllocationStatus.Active
        });
        await db.SaveChangesAsync();
    }

    private static async Task<SalesTransaction> AddDeliveryAsync(
        ApplicationDbContext db,
        PaymentAccountingAdapterTests.PaymentScope scope,
        PreSaleOrder? order,
        decimal quantityMt,
        decimal totalUsd)
    {
        var sale = new SalesTransaction
        {
            CompanyId = scope.Company.Id,
            ContractId = scope.Contract.Id,
            CustomerId = scope.Customer.Id,
            ProductId = scope.Product.Id,
            PreSaleOrderId = order?.Id,
            InvoiceNumber = PaymentAccountingAdapterTests.Unique("INV"),
            SaleDate = SaleDate,
            QuantityMt = quantityMt,
            Currency = "USD",
            UnitPriceInCurrency = totalUsd / quantityMt,
            AppliedFxRateToUsd = 1m,
            UnitPriceUsd = totalUsd / quantityMt,
            TotalInCurrency = totalUsd,
            TotalUsd = totalUsd
        };
        db.SalesTransactions.Add(sale);
        await db.SaveChangesAsync();
        return sale;
    }

    private static async Task AddOutMovementAsync(
        ApplicationDbContext db,
        PaymentAccountingAdapterTests.PaymentScope scope,
        SalesTransaction sale,
        decimal quantityMt)
    {
        db.InventoryMovements.Add(new InventoryMovement
        {
            TerminalId = scope.Terminal.Id,
            ProductId = scope.Product.Id,
            ContractId = scope.Contract.Id,
            SalesTransactionId = sale.Id,
            Direction = MovementDirection.Out,
            MovementDate = sale.SaleDate,
            QuantityMt = quantityMt
        });
        await db.SaveChangesAsync();
    }

    private static SalesAccountingAdapter CreateAdapter(ApplicationDbContext db)
    {
        var options = Options.Create(new AccountingOptions
        {
            Enabled = true,
            Pilots = new AccountingPilotOptions { Sale = true, Cogs = true }
        });
        return new SalesAccountingAdapter(
            db,
            new AccountingPostingService(db, new PeriodGuard(db, new FiscalCalendarService(db)), options, new SystemCompanyProvider(db)),
            new AccountingJournalNumberGenerator(),
            new InventoryValuationService(db),
            options,
            NullLogger<SalesAccountingAdapter>.Instance);
    }
}
