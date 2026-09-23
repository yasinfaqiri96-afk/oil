using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Audit;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// چیزهایی که فقط روی PostgreSQL واقعی قابل اثبات‌اند: قفلِ <c>FOR UPDATE</c> روی سطرهای
/// بارگیری، تراکنش Serializable و محافظ ضدتکراریِ فرم (که به یک ایندکس یکتای واقعی نیاز دارد).
/// </summary>
[Collection(BulkFromLoadingPerformanceCollection.CollectionName)]
public sealed class BulkFromLoadingConcurrencyTests(BulkFromLoadingPerformanceFixture fixture)
{
    private static readonly DateTime TransportDate = new(2026, 9, 5);

    [Fact]
    public async Task Two_Concurrent_Bulk_Requests_Cannot_Consume_The_Same_Loading_Twice()
    {
        Assert.True(fixture.Available, $"این تست به PostgreSQL واقعی نیاز دارد: {fixture.UnavailableReason}");
        await fixture.TruncateAsync();
        await using (var seed = fixture.CreateDbContext())
        {
            await BulkFromLoadingPerformanceTests.SeedAsync(seed, loadingCount: 3);
        }

        // دو درخواست کاملاً جدا، هر کدام DbContext خودش — همان چیزی که دو کاربر هم‌زمان می‌سازند.
        await using var first = fixture.CreateDbContext();
        await using var second = fixture.CreateDbContext();

        var results = await Task.WhenAll(
            BulkFromLoadingPerformanceTests.BuildWorkflow(first).StartManyFromLoadingAsync(Command()),
            BulkFromLoadingPerformanceTests.BuildWorkflow(second).StartManyFromLoadingAsync(Command()));

        await using var verify = fixture.CreateDbContext();
        // سه بارگیریِ ۱۰۰ تنی؛ هرچه بشود، مجموع تخصیص نباید از ۳۰۰ تن بگذرد.
        var allocated = await verify.InventoryTransportLegAllocations
            .AsNoTracking()
            .Where(a => a.InventoryTransportLeg!.Status != InventoryTransportLegStatus.Cancelled)
            .SumAsync(a => a.QuantityMt);
        Assert.Equal(300m, allocated);
        Assert.Equal(3, results.Sum(r => r.CreatedCount));
        Assert.Equal(3, await verify.InventoryTransportLegs.AsNoTracking().CountAsync());

        // هر بارگیری دقیقاً یک‌بار مصرف شده باشد.
        var perLoading = await verify.InventoryTransportLegAllocations
            .AsNoTracking()
            .GroupBy(a => a.SourceLoadingRegisterId!.Value)
            .Select(g => new { Id = g.Key, Mt = g.Sum(a => a.QuantityMt) })
            .ToListAsync();
        Assert.Equal(3, perLoading.Count);
        Assert.All(perLoading, x => Assert.Equal(100m, x.Mt));
    }

    [Fact]
    public async Task Resubmitting_The_Same_Form_Token_Does_Not_Create_A_Second_Set_Of_Transports()
    {
        Assert.True(fixture.Available, $"این تست به PostgreSQL واقعی نیاز دارد: {fixture.UnavailableReason}");
        await fixture.TruncateAsync();
        await using var db = fixture.CreateDbContext();
        // عمداً دوبرابرِ مقدارِ هر ردیف: بدون محافظِ توکن، ثبت دوم از نظر مقدار مجاز بود و
        // چهار حملِ تکراری می‌ساخت. تنها چیزی که جلویش را می‌گیرد همان توکن است.
        await BulkFromLoadingPerformanceTests.SeedAsync(db, loadingCount: 4, loadedQuantityMt: 200m);
        db.ChangeTracker.Clear();

        var workflow = BuildWorkflowWithTokens(db);
        const string token = "bulk-double-submit-token";

        var first = await workflow.StartManyFromLoadingAsync(Command(4, token));
        Assert.Equal(4, first.CreatedCount);
        db.ChangeTracker.Clear();

        // ثبت دوبارهٔ همان فرم (کلیک دوم، رفرش، retry) نباید حمل تازه بسازد.
        await Assert.ThrowsAsync<DbUpdateException>(
            () => workflow.StartManyFromLoadingAsync(Command(4, token)));

        await using var verify = fixture.CreateDbContext();
        Assert.Equal(4, await verify.InventoryTransportLegs.AsNoTracking().CountAsync());
        Assert.Equal(400m, await verify.InventoryTransportLegAllocations.AsNoTracking().SumAsync(a => a.QuantityMt));

        // و ثبت با توکنِ تازه همچنان کار می‌کند (محافظ نباید کاربر را قفل کند).
        db.ChangeTracker.Clear();
        var retry = await workflow.StartManyFromLoadingAsync(Command(4, "bulk-fresh-token"));
        Assert.Equal(4, retry.CreatedCount);
    }

    [Fact]
    public async Task A_Failing_Row_Rolls_Back_Only_Its_Own_Transport_And_The_Rest_Commit()
    {
        Assert.True(fixture.Available, $"این تست به PostgreSQL واقعی نیاز دارد: {fixture.UnavailableReason}");
        await fixture.TruncateAsync();
        await using var db = fixture.CreateDbContext();
        await BulkFromLoadingPerformanceTests.SeedAsync(db, loadingCount: 6);
        // بارگیری ۳ قبلاً کاملاً رسیده است، پس وسط دسته شکست می‌خورد.
        db.LoadingReceipts.Add(new LoadingReceipt
        {
            LoadingRegisterId = 3,
            TerminalId = 1,
            ReceiptDate = new DateTime(2026, 9, 1),
            ReceivedQuantityMt = 100m
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var result = await BulkFromLoadingPerformanceTests.BuildWorkflow(db)
            .StartManyFromLoadingAsync(Command(6));

        Assert.Equal(5, result.CreatedCount);
        Assert.Equal(3, Assert.Single(result.Failures).LoadingRegisterId);

        await using var verify = fixture.CreateDbContext();
        Assert.Equal(5, await verify.InventoryTransportLegs.AsNoTracking().CountAsync());
        Assert.Equal(5, await verify.InventoryTransportBatches.AsNoTracking().CountAsync());
        // هیچ سند یا سهمِ یتیمی نمانده باشد.
        Assert.Equal(5, await verify.InventoryTransportLegAllocations.AsNoTracking().CountAsync());
        Assert.Equal(
            await verify.InventoryTransportLegs.AsNoTracking().CountAsync(),
            await verify.InventoryTransportLegs.AsNoTracking()
                .CountAsync(l => l.InventoryTransportBatchId != null));
    }

    [Fact]
    public async Task A_Non_Business_Database_Error_Is_Reported_Per_Row_Instead_Of_Escaping()
    {
        Assert.True(fixture.Available, $"این تست به PostgreSQL واقعی نیاز دارد: {fixture.UnavailableReason}");
        await fixture.TruncateAsync();
        await using var db = fixture.CreateDbContext();
        await BulkFromLoadingPerformanceTests.SeedAsync(db, loadingCount: 4);
        db.ChangeTracker.Clear();

        // ستون RwbNo حداکثر ۱۰۰ نویسه است؛ مرجعِ بلندتر یک خطای واقعی دیتابیس می‌دهد که
        // BusinessRuleException نیست. بدون گرفتنِ آن، یک ردیفِ خراب کلِ عملیات را ۵۰۰ می‌کرد و
        // سه حملِ ثبت‌شده هم به کاربر گزارش نمی‌شدند.
        var rows = Enumerable.Range(1, 4)
            .Select(id => new BulkStartTransportFromLoadingRow
            {
                LoadingRegisterId = id,
                QuantityMt = 100m,
                TransportType = LoadingTransportType.Truck,
                TruckId = 1,
                Reference = id == 3 ? new string('X', 150) : $"BULK-{id}",
                Label = $"بارگیری #{id}"
            })
            .ToList();

        var result = await BulkFromLoadingPerformanceTests.BuildWorkflow(db)
            .StartManyFromLoadingAsync(new BulkStartTransportFromLoadingCommand
            {
                Rows = rows,
                TransportDate = TransportDate
            });

        Assert.Equal(3, result.CreatedCount);
        var failure = Assert.Single(result.Failures);
        Assert.Equal(3, failure.LoadingRegisterId);
        Assert.Equal("TRANSPORT_LOADING_ROW_FAILED", failure.Code);

        await using var verify = fixture.CreateDbContext();
        Assert.Equal(3, await verify.InventoryTransportLegs.AsNoTracking().CountAsync());
        Assert.Equal(3, await verify.InventoryTransportBatches.AsNoTracking().CountAsync());
        Assert.Equal(3, await verify.InventoryTransportLegAllocations.AsNoTracking().CountAsync());
    }

    private static BulkStartTransportFromLoadingCommand Command(int count = 3, string? token = null)
        => new()
        {
            Rows = Enumerable.Range(1, count)
                .Select(id => new BulkStartTransportFromLoadingRow
                {
                    LoadingRegisterId = id,
                    QuantityMt = 100m,
                    TransportType = LoadingTransportType.Truck,
                    TruckId = 1,
                    Reference = $"BULK-{id}"
                })
                .ToList(),
            TransportDate = TransportDate,
            FormToken = token
        };

    private static TransportWorkflowService BuildWorkflowWithTokens(ApplicationDbContext db)
    {
        var stock = new StockService(db);
        var quantities = new TransportQuantityService(db);
        var receipts = new InventoryTransportReceiptService(
            db,
            new CurrencyConversionService(new PricingService(db)),
            quantities: quantities);
        return new TransportWorkflowService(
            db,
            new InventoryTransportBatchService(db, stock),
            new TransportChainService(db, receipts, quantities),
            receipts,
            new LossEventWorkflowService(db, stock, new AuditService(db)),
            new FormTokenGuard(db));
    }
}
