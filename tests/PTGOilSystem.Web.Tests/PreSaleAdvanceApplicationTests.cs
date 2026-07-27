using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PTGOilSystem.Web.Configuration;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Accounting;
using PTGOilSystem.Web.Services.Exceptions;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// مصرفِ ردیابی‌پذیرِ پیش‌دریافت (CustomerPaymentAllocationApplication):
///   • هر مصرف یک Applicationِ مستقل می‌سازد و مانده مصرف‌نشده از داده واقعی می‌آید، نه از حدسِ ترتیب.
///   • تخصیصِ بعد از تحویل طلبِ تحویلِ قبلی را با ژورنالِ انتقالِ متوازن تسویه می‌کند.
///   • لغو تحویل Applicationها را آزاد و ژورنالِ انتقال را معکوس می‌کند و لغو دوباره تکراری نمی‌سازد.
///   • برگشت COGS چندمنبعی دقیقاً به همان poolها با همان ارزش برمی‌گردد.
/// </summary>
[Collection(AccountingPostgreSqlCollection.CollectionName)]
public sealed class PreSaleAdvanceApplicationTests(AccountingPostgreSqlFixture fixture)
{
    private static readonly DateTime SaleDate = new(2026, 7, 5);
    private static readonly DateTime ReversalDate = new(2026, 7, 20);

    // ---------- Path A: مصرفِ هنگام تحویل ----------

    [Fact]
    public async Task Each_Advance_Consumption_Creates_An_Independent_Application()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var order = await AddPreSaleAsync(db, scope, totalUsd: 10_000m);
        await AddAllocationAsync(db, scope, order, amountUsd: 4_000m);
        var sale = await AddDeliveryAsync(db, scope, order, quantityMt: 10m, totalUsd: 5_000m);

        Assert.Equal(PaymentPostingStatus.Posted, (await CreateAdapter(db).TryPostSaleAsync(sale)).Status);

        var applications = await db.CustomerPaymentAllocationApplications.AsNoTracking()
            .Where(a => a.SalesTransactionId == sale.Id).ToListAsync();
        var app = Assert.Single(applications);
        Assert.Equal(CustomerPaymentAllocationApplicationStatus.Active, app.Status);
        Assert.Equal(4_000m, app.AppliedAmountUsd);
        Assert.Null(app.JournalEntryId); // مصرفِ هنگام تحویل، اثرش داخلِ ژورنالِ فروش است.
    }

    [Fact]
    public async Task One_Allocation_Is_Consumed_Across_Several_Partial_Deliveries()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var order = await AddPreSaleAsync(db, scope, totalUsd: 12_000m);
        var allocation = await AddAllocationAsync(db, scope, order, amountUsd: 6_000m);

        var first = await AddDeliveryAsync(db, scope, order, quantityMt: 8m, totalUsd: 4_000m);
        var second = await AddDeliveryAsync(db, scope, order, quantityMt: 6m, totalUsd: 3_000m);

        var adapter = CreateAdapter(db);
        await adapter.TryPostSaleAsync(first);
        await adapter.TryPostSaleAsync(second);

        var apps = await db.CustomerPaymentAllocationApplications.AsNoTracking()
            .Where(a => a.CustomerPaymentAllocationId == allocation.Id).OrderBy(a => a.Id).ToListAsync();
        Assert.Equal(2, apps.Count);
        Assert.Equal(4_000m, apps[0].AppliedAmountUsd);
        Assert.Equal(2_000m, apps[1].AppliedAmountUsd); // فقط باقیماندهٔ تخصیص.
        Assert.Equal(0m, await new CustomerPaymentAllocationService(db).GetUnappliedAllocationUsdAsync(allocation.Id));

        // تحویل دوم ۲۰۰۰ با باقیماندهٔ پیش‌دریافت پوشش یافت، پس ۱۰۰۰ مطالبات است.
        var secondJournal = await LoadJournalAsync(db, SalesAccountingAdapter.BuildCreatedSourceEventId(second.Id));
        Assert.Equal(1_000m, DebitOf(secondJournal, scope.Settings.AccountsReceivableAccountId));
    }

    [Fact]
    public async Task Several_Allocations_Are_Consumed_In_One_Delivery()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var order = await AddPreSaleAsync(db, scope, totalUsd: 10_000m);
        await AddAllocationAsync(db, scope, order, amountUsd: 3_000m);
        await AddAllocationAsync(db, scope, order, amountUsd: 2_000m);
        var sale = await AddDeliveryAsync(db, scope, order, quantityMt: 10m, totalUsd: 5_000m);

        await CreateAdapter(db).TryPostSaleAsync(sale);

        var apps = await db.CustomerPaymentAllocationApplications.AsNoTracking()
            .Where(a => a.SalesTransactionId == sale.Id).ToListAsync();
        Assert.Equal(2, apps.Count);
        Assert.Equal(5_000m, apps.Sum(a => a.AppliedAmountUsd));

        var journal = await LoadJournalAsync(db, SalesAccountingAdapter.BuildCreatedSourceEventId(sale.Id));
        Assert.Equal(5_000m, DebitOf(journal, scope.Settings.CustomerAdvanceAccountId));
        Assert.Equal(0m, DebitOf(journal, scope.Settings.AccountsReceivableAccountId));
    }

    [Fact]
    public async Task Unconsumed_Allocation_Balance_Is_Computed_Exactly()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var order = await AddPreSaleAsync(db, scope, totalUsd: 10_000m);
        var allocation = await AddAllocationAsync(db, scope, order, amountUsd: 5_000m);
        var sale = await AddDeliveryAsync(db, scope, order, quantityMt: 6m, totalUsd: 3_000m);

        await CreateAdapter(db).TryPostSaleAsync(sale);

        Assert.Equal(2_000m, await new CustomerPaymentAllocationService(db).GetUnappliedAllocationUsdAsync(allocation.Id));
    }

    [Fact]
    public async Task No_Amount_Is_Ever_Consumed_Twice()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var order = await AddPreSaleAsync(db, scope, totalUsd: 20_000m);
        var allocation = await AddAllocationAsync(db, scope, order, amountUsd: 4_000m);

        var first = await AddDeliveryAsync(db, scope, order, quantityMt: 10m, totalUsd: 5_000m);
        var second = await AddDeliveryAsync(db, scope, order, quantityMt: 10m, totalUsd: 5_000m);

        var adapter = CreateAdapter(db);
        await adapter.TryPostSaleAsync(first);
        await adapter.TryPostSaleAsync(second);

        var consumed = await db.CustomerPaymentAllocationApplications.AsNoTracking()
            .Where(a => a.CustomerPaymentAllocationId == allocation.Id
                && a.Status == CustomerPaymentAllocationApplicationStatus.Active)
            .SumAsync(a => a.AppliedAmountUsd);
        Assert.Equal(4_000m, consumed); // نه ۸۰۰۰
    }

    [Fact]
    public async Task With_Advance_There_Is_No_Silent_Fallback_To_All_Receivable()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var order = await AddPreSaleAsync(db, scope, totalUsd: 10_000m);
        await AddAllocationAsync(db, scope, order, amountUsd: 4_000m);
        var sale = await AddDeliveryAsync(db, scope, order, quantityMt: 10m, totalUsd: 5_000m);

        await CreateAdapter(db).TryPostSaleAsync(sale);

        var journal = await LoadJournalAsync(db, SalesAccountingAdapter.BuildCreatedSourceEventId(sale.Id));
        AssertBalanced(journal);
        // پیش‌دریافت واقعاً مصرف شده و مطالبات فقط باقیمانده است؛ همه‌چیز در مطالبات نیفتاده.
        Assert.Equal(4_000m, DebitOf(journal, scope.Settings.CustomerAdvanceAccountId));
        Assert.Equal(1_000m, DebitOf(journal, scope.Settings.AccountsReceivableAccountId));
        Assert.NotEqual(5_000m, DebitOf(journal, scope.Settings.AccountsReceivableAccountId));
    }

    [Fact]
    public async Task Advance_Larger_Than_Delivery_Does_Not_Create_A_Negative_Receivable()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var order = await AddPreSaleAsync(db, scope, totalUsd: 10_000m);
        var allocation = await AddAllocationAsync(db, scope, order, amountUsd: 8_000m);
        var sale = await AddDeliveryAsync(db, scope, order, quantityMt: 10m, totalUsd: 5_000m);

        await CreateAdapter(db).TryPostSaleAsync(sale);

        var journal = await LoadJournalAsync(db, SalesAccountingAdapter.BuildCreatedSourceEventId(sale.Id));
        AssertBalanced(journal);
        Assert.Equal(5_000m, DebitOf(journal, scope.Settings.CustomerAdvanceAccountId));
        Assert.Equal(0m, DebitOf(journal, scope.Settings.AccountsReceivableAccountId)); // نه منفی
        // ۳۰۰۰ باقیماندهٔ پیش‌دریافت آزاد می‌ماند.
        Assert.Equal(3_000m, await new CustomerPaymentAllocationService(db).GetUnappliedAllocationUsdAsync(allocation.Id));
    }

    // ---------- Path B: تخصیص بعد از تحویل ----------

    [Fact]
    public async Task Allocation_After_Delivery_Settles_The_Prior_Receivable_With_A_Balanced_Journal()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var order = await AddPreSaleAsync(db, scope, totalUsd: 10_000m);
        var sale = await AddDeliveryAsync(db, scope, order, quantityMt: 10m, totalUsd: 5_000m);

        var adapter = CreateAdapter(db);
        await adapter.TryPostSaleAsync(sale); // طلبِ ۵۰۰۰ روی حساب

        var allocation = await AddAllocationAsync(db, scope, order, amountUsd: 4_000m);
        var settled = await adapter.TrySettleDeliveredReceivableAsync(allocation.Id);
        Assert.Equal(1, settled);

        var app = await db.CustomerPaymentAllocationApplications.AsNoTracking()
            .SingleAsync(a => a.CustomerPaymentAllocationId == allocation.Id);
        Assert.Equal(4_000m, app.AppliedAmountUsd);
        Assert.NotNull(app.JournalEntryId); // مصرفِ بعد از تحویل ژورنالِ مستقل دارد.

        var transfer = await db.JournalEntries.AsNoTracking().Include(j => j.Lines)
            .SingleAsync(j => j.Id == app.JournalEntryId!.Value);
        AssertBalanced(transfer);
        Assert.Equal(4_000m, DebitOf(transfer, scope.Settings.CustomerAdvanceAccountId));
        Assert.Equal(4_000m, CreditOf(transfer, scope.Settings.AccountsReceivableAccountId));
    }

    [Fact]
    public async Task Allocation_After_Delivery_Only_Consumes_Up_To_The_Open_Receivable()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var order = await AddPreSaleAsync(db, scope, totalUsd: 10_000m);
        var sale = await AddDeliveryAsync(db, scope, order, quantityMt: 6m, totalUsd: 3_000m);

        var adapter = CreateAdapter(db);
        await adapter.TryPostSaleAsync(sale);

        var allocation = await AddAllocationAsync(db, scope, order, amountUsd: 5_000m);
        await adapter.TrySettleDeliveredReceivableAsync(allocation.Id);

        var app = await db.CustomerPaymentAllocationApplications.AsNoTracking()
            .SingleAsync(a => a.CustomerPaymentAllocationId == allocation.Id);
        Assert.Equal(3_000m, app.AppliedAmountUsd); // فقط تا سقفِ طلبِ باز
        // ۲۰۰۰ باقیمانده برای تحویلِ آینده آزاد می‌ماند.
        Assert.Equal(2_000m, await new CustomerPaymentAllocationService(db).GetUnappliedAllocationUsdAsync(allocation.Id));
    }

    [Fact]
    public async Task After_Delivery_Remainder_Is_Available_For_A_Future_Delivery()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var order = await AddPreSaleAsync(db, scope, totalUsd: 12_000m);
        var first = await AddDeliveryAsync(db, scope, order, quantityMt: 6m, totalUsd: 3_000m);

        var adapter = CreateAdapter(db);
        await adapter.TryPostSaleAsync(first);

        var allocation = await AddAllocationAsync(db, scope, order, amountUsd: 5_000m);
        await adapter.TrySettleDeliveredReceivableAsync(allocation.Id); // ۳۰۰۰ مصرف، ۲۰۰۰ آزاد

        var second = await AddDeliveryAsync(db, scope, order, quantityMt: 8m, totalUsd: 4_000m);
        await adapter.TryPostSaleAsync(second);

        var secondJournal = await LoadJournalAsync(db, SalesAccountingAdapter.BuildCreatedSourceEventId(second.Id));
        Assert.Equal(2_000m, DebitOf(secondJournal, scope.Settings.CustomerAdvanceAccountId)); // باقیماندهٔ آزاد
        Assert.Equal(2_000m, DebitOf(secondJournal, scope.Settings.AccountsReceivableAccountId));
        Assert.Equal(0m, await new CustomerPaymentAllocationService(db).GetUnappliedAllocationUsdAsync(allocation.Id));
    }

    // ---------- لغو تحویل ----------

    [Fact]
    public async Task Cancelling_A_Delivery_Frees_Its_Advance_Applications()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var order = await AddPreSaleAsync(db, scope, totalUsd: 10_000m);
        var allocation = await AddAllocationAsync(db, scope, order, amountUsd: 4_000m);
        var sale = await AddDeliveryAsync(db, scope, order, quantityMt: 10m, totalUsd: 5_000m);

        var adapter = CreateAdapter(db);
        await adapter.TryPostSaleAsync(sale);

        sale.IsCancelled = true;
        await db.SaveChangesAsync();
        await adapter.TryReverseSaleAsync(sale, ReversalDate);
        var released = await adapter.TryReleaseAdvanceApplicationsAsync(sale, ReversalDate);

        Assert.Equal(1, released);
        var app = await db.CustomerPaymentAllocationApplications.AsNoTracking()
            .SingleAsync(a => a.SalesTransactionId == sale.Id);
        Assert.Equal(CustomerPaymentAllocationApplicationStatus.Reversed, app.Status);
        // پیش‌دریافت دوباره آزاد شد.
        Assert.Equal(4_000m, await new CustomerPaymentAllocationService(db).GetUnappliedAllocationUsdAsync(allocation.Id));
    }

    [Fact]
    public async Task Cancelling_A_Delivery_Reverses_The_After_Delivery_Application_Journal()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var order = await AddPreSaleAsync(db, scope, totalUsd: 10_000m);
        var sale = await AddDeliveryAsync(db, scope, order, quantityMt: 10m, totalUsd: 5_000m);

        var adapter = CreateAdapter(db);
        await adapter.TryPostSaleAsync(sale);

        var allocation = await AddAllocationAsync(db, scope, order, amountUsd: 4_000m);
        await adapter.TrySettleDeliveredReceivableAsync(allocation.Id);
        var app = await db.CustomerPaymentAllocationApplications.AsNoTracking()
            .SingleAsync(a => a.CustomerPaymentAllocationId == allocation.Id);
        var transferId = app.JournalEntryId!.Value;

        sale.IsCancelled = true;
        await db.SaveChangesAsync();
        await adapter.TryReverseSaleAsync(sale, ReversalDate);
        await adapter.TryReleaseAdvanceApplicationsAsync(sale, ReversalDate);

        // ژورنالِ انتقال معکوسِ رسمی خورده و اثر خالصِ پیش‌دریافت/مطالبات صفر است.
        var reversal = await db.JournalEntries.AsNoTracking().Include(j => j.Lines)
            .SingleAsync(j => j.ReversalOfJournalEntryId == transferId);
        AssertBalanced(reversal);
        Assert.Equal(CustomerPaymentAllocationApplicationStatus.Reversed,
            (await db.CustomerPaymentAllocationApplications.AsNoTracking().SingleAsync(a => a.Id == app.Id)).Status);
        Assert.Equal(4_000m, await new CustomerPaymentAllocationService(db).GetUnappliedAllocationUsdAsync(allocation.Id));
    }

    [Fact]
    public async Task Releasing_Applications_Twice_Does_Not_Duplicate_Reversals()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var order = await AddPreSaleAsync(db, scope, totalUsd: 10_000m);
        var sale = await AddDeliveryAsync(db, scope, order, quantityMt: 10m, totalUsd: 5_000m);

        var adapter = CreateAdapter(db);
        await adapter.TryPostSaleAsync(sale);
        var allocation = await AddAllocationAsync(db, scope, order, amountUsd: 4_000m);
        await adapter.TrySettleDeliveredReceivableAsync(allocation.Id);
        var app = await db.CustomerPaymentAllocationApplications.AsNoTracking()
            .SingleAsync(a => a.CustomerPaymentAllocationId == allocation.Id);
        var transferId = app.JournalEntryId!.Value;

        sale.IsCancelled = true;
        await db.SaveChangesAsync();
        await adapter.TryReverseSaleAsync(sale, ReversalDate);
        await adapter.TryReleaseAdvanceApplicationsAsync(sale, ReversalDate);
        var secondRelease = await adapter.TryReleaseAdvanceApplicationsAsync(sale, ReversalDate);

        Assert.Equal(0, secondRelease); // چیزی برای آزادکردن نمانده
        Assert.Equal(1, await db.JournalEntries.CountAsync(j => j.ReversalOfJournalEntryId == transferId));
    }

    // ---------- برگشت دقیقِ COGS ----------

    [Fact]
    public async Task Single_Source_Cogs_Reversal_Returns_Exact_Value()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var order = await AddPreSaleAsync(db, scope, totalUsd: 10_000m);
        var sale = await AddDeliveryAsync(db, scope, order, quantityMt: 10m, totalUsd: 5_000m);

        await new InventoryValuationService(db).ApplyReceiptAsync(
            scope.Company.Id, scope.Product.Id, scope.Terminal.Id, 10m, 4_000m);
        await AddOutMovementAsync(db, scope, sale, scope.Terminal.Id, 10m);

        var adapter = CreateAdapter(db);
        await adapter.TryPostCogsAsync(sale);

        sale.IsCancelled = true;
        await db.SaveChangesAsync();
        await adapter.TryReverseCogsAsync(sale, ReversalDate);

        var pool = await db.InventoryAverageCosts.AsNoTracking().SingleAsync(
            x => x.CompanyId == scope.Company.Id && x.ProductId == scope.Product.Id && x.TerminalId == scope.Terminal.Id);
        Assert.Equal(10m, pool.QuantityMt);
        Assert.Equal(4_000m, pool.TotalValueUsd);
    }

    [Fact]
    public async Task Multi_Source_Cogs_Reversal_Returns_Exact_Value_To_Each_Original_Pool()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);

        var terminalB = new Terminal { Code = PaymentAccountingAdapterTests.Unique("T"), Name = PaymentAccountingAdapterTests.Unique("Terminal"), IsActive = true };
        db.Terminals.Add(terminalB);
        await db.SaveChangesAsync();

        var order = await AddPreSaleAsync(db, scope, totalUsd: 20_000m);
        var sale = await AddDeliveryAsync(db, scope, order, quantityMt: 200m, totalUsd: 10_000m);

        var valuation = new InventoryValuationService(db);
        // مخزن A: ۱۰۰ واحد با بهای ۲۰۰ دلار (میانگین ۲). مخزن B: ۱۰۰ واحد با بهای ۵۰۰ دلار (میانگین ۵).
        await valuation.ApplyReceiptAsync(scope.Company.Id, scope.Product.Id, scope.Terminal.Id, 100m, 200m);
        await valuation.ApplyReceiptAsync(scope.Company.Id, scope.Product.Id, terminalB.Id, 100m, 500m);
        await AddOutMovementAsync(db, scope, sale, scope.Terminal.Id, 100m);
        await AddOutMovementAsync(db, scope, sale, terminalB.Id, 100m);

        var adapter = CreateAdapter(db);
        Assert.Equal(PaymentPostingStatus.Posted, (await adapter.TryPostCogsAsync(sale)).Status);

        sale.IsCancelled = true;
        await db.SaveChangesAsync();
        Assert.Equal(PaymentPostingStatus.Posted, (await adapter.TryReverseCogsAsync(sale, ReversalDate)).Status);

        var poolA = await db.InventoryAverageCosts.AsNoTracking().SingleAsync(
            x => x.CompanyId == scope.Company.Id && x.ProductId == scope.Product.Id && x.TerminalId == scope.Terminal.Id);
        var poolB = await db.InventoryAverageCosts.AsNoTracking().SingleAsync(
            x => x.CompanyId == scope.Company.Id && x.ProductId == scope.Product.Id && x.TerminalId == terminalB.Id);

        // هر مخزن دقیقاً بهای خودش را پس گرفته — نه تقسیمِ نسبتیِ ۷۰۰ بر مقدار (که ۳۵۰/۳۵۰ می‌شد).
        Assert.Equal(100m, poolA.QuantityMt);
        Assert.Equal(200m, poolA.TotalValueUsd);
        Assert.Equal(100m, poolB.QuantityMt);
        Assert.Equal(500m, poolB.TotalValueUsd);
    }

    // ---------- گاردِ مهاجرت ----------

    // اگر ستونِ قدیمیِ PreSaleOrderId هنوز روی PaymentTransactions باشد و لینکی منتقل‌نشده بماند،
    // گاردِ مهاجرت به‌جای حذفِ خاموش با پیامِ واضح شکست می‌خورد. اینجا سناریوی orphan را داخل یک
    // تراکنشِ برگشتی می‌سازیم تا اسکیمای مشترک دست‌نخورده بماند.
    [Fact]
    public async Task Migration_Guard_Fails_Loudly_On_An_Unmigrated_PreSaleOrderId_Link()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);

        const string guardSql = """
            DO $$
            DECLARE
                col_exists boolean;
                orphan_count integer;
            BEGIN
                SELECT EXISTS (
                    SELECT 1 FROM information_schema.columns
                    WHERE table_name = 'PaymentTransactions'
                      AND column_name = 'PreSaleOrderId'
                ) INTO col_exists;

                IF col_exists THEN
                    SELECT COUNT(*) INTO orphan_count
                    FROM "PaymentTransactions" p
                    WHERE p."PreSaleOrderId" IS NOT NULL
                      AND NOT EXISTS (
                          SELECT 1 FROM "CustomerPaymentAllocations" a
                          WHERE a."PaymentTransactionId" = p."Id"
                            AND a."PreSaleOrderId" = p."PreSaleOrderId"
                      );

                    IF orphan_count > 0 THEN
                        RAISE EXCEPTION 'Found % PaymentTransaction(s) with a PreSaleOrderId link that was not migrated to CustomerPaymentAllocations (likely an invalid FX rate). Re-record these links manually with a valid rate before the column is dropped; refusing to drop financial links silently.', orphan_count;
                    END IF;
                END IF;
            END $$;
            """;

        var tx = await db.Database.BeginTransactionAsync();
        try
        {
            await db.Database.ExecuteSqlRawAsync(
                @"ALTER TABLE ""PaymentTransactions"" ADD COLUMN ""PreSaleOrderId"" integer NULL");
            var utcNow = DateTime.UtcNow;
            await db.Database.ExecuteSqlInterpolatedAsync(
                $@"INSERT INTO ""PaymentTransactions"" (""PaymentDate"",""Direction"",""PaymentKind"",""CashAccountId"",""Amount"",""Currency"",""AmountUsd"",""IsCustomerAdvance"",""CreatedAtUtc"",""PreSaleOrderId"")
                   VALUES ({utcNow},1,1,{scope.CashAccount.Id},100,'USD',100,true,{utcNow},999999)");

            var error = await Assert.ThrowsAnyAsync<Exception>(
                () => db.Database.ExecuteSqlRawAsync(guardSql));
            var message = (error.InnerException?.Message ?? error.Message);
            Assert.Contains("not migrated", message);
        }
        finally
        {
            await tx.RollbackAsync();
            await tx.DisposeAsync();
        }
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

    private static async Task<JournalEntry> LoadJournalAsync(ApplicationDbContext db, string sourceEventId)
        => await db.JournalEntries.AsNoTracking().Include(x => x.Lines)
            .SingleAsync(x => x.SourceEventId == sourceEventId);

    private static async Task<PreSaleOrder> AddPreSaleAsync(
        ApplicationDbContext db, PaymentAccountingAdapterTests.PaymentScope scope, decimal totalUsd)
    {
        var order = new PreSaleOrder
        {
            OrderNumber = PaymentAccountingAdapterTests.Unique("PRE"),
            CustomerId = scope.Customer.Id,
            ProductId = scope.Product.Id,
            CompanyId = scope.Company.Id,
            OrderDate = SaleDate,
            QuantityMt = 100m,
            Currency = "USD",
            UnitPriceInCurrency = totalUsd / 100m,
            AppliedFxRateToUsd = 1m,
            UnitPriceUsd = totalUsd / 100m,
            TotalInCurrency = totalUsd,
            TotalUsd = totalUsd,
            Status = PreSaleOrderStatus.Confirmed
        };
        db.PreSaleOrders.Add(order);
        await db.SaveChangesAsync();
        return order;
    }

    private static async Task<CustomerPaymentAllocation> AddAllocationAsync(
        ApplicationDbContext db, PaymentAccountingAdapterTests.PaymentScope scope, PreSaleOrder order, decimal amountUsd)
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

        var allocation = new CustomerPaymentAllocation
        {
            PaymentTransactionId = payment.Id,
            PreSaleOrderId = order.Id,
            AllocationDate = SaleDate,
            AllocatedPaymentAmount = amountUsd,
            PaymentCurrencyCode = "USD",
            PaymentFxRateToUsd = 1m,
            AllocatedAmountUsd = amountUsd,
            Status = CustomerPaymentAllocationStatus.Active
        };
        db.CustomerPaymentAllocations.Add(allocation);
        await db.SaveChangesAsync();
        return allocation;
    }

    private static async Task<SalesTransaction> AddDeliveryAsync(
        ApplicationDbContext db, PaymentAccountingAdapterTests.PaymentScope scope,
        PreSaleOrder order, decimal quantityMt, decimal totalUsd)
    {
        var sale = new SalesTransaction
        {
            CompanyId = scope.Company.Id,
            ContractId = scope.Contract.Id,
            CustomerId = scope.Customer.Id,
            ProductId = scope.Product.Id,
            PreSaleOrderId = order.Id,
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
        ApplicationDbContext db, PaymentAccountingAdapterTests.PaymentScope scope,
        SalesTransaction sale, int terminalId, decimal quantityMt)
    {
        db.InventoryMovements.Add(new InventoryMovement
        {
            TerminalId = terminalId,
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
