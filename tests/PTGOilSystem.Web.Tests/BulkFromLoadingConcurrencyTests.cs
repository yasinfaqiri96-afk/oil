using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using PTGOilSystem.Web.Controllers;
using PTGOilSystem.Web.Models.InventoryTransport;
using PTGOilSystem.Web.Services.Time;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Data.Common;
using PTGOilSystem.Web.Services.Exceptions;
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
        var replay = await workflow.StartManyFromLoadingAsync(Command(4, token));
        Assert.Equal(0, replay.CreatedCount);
        Assert.Equal(4, replay.CompletedCount);
        Assert.Equal(first.CreatedLegIds.OrderBy(x => x), replay.PreviouslyCreatedLegIds.OrderBy(x => x));
        Assert.Empty(replay.Failures);

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

    [Fact]
    public async Task Retry_After_First_Chunk_Commits_Resumes_Without_Repeating_Any_Row()
    {
        Assert.True(fixture.Available, fixture.UnavailableReason);
        await fixture.TruncateAsync();
        await using (var seed = fixture.CreateDbContext())
            await BulkFromLoadingPerformanceTests.SeedAsync(seed, 4, loadedQuantityMt: 200m);
        using var disconnect = new CancellationTokenSource();
        var command = Command(4, "disconnect-resume") with { ChunkSize = 2 };
        await using (var interrupted = fixture.CreateDbContext(new CancelAfterCommit(disconnect)))
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => BuildWorkflowWithTokens(interrupted).StartManyFromLoadingAsync(command, disconnect.Token));
        await using var retry = fixture.CreateDbContext();
        var recovered = await new TransportBulkRequestQuery(retry).GetCompletedRowsAsync(command.FormToken!);
        Assert.Equal(2, recovered.Count);
        Assert.All(recovered, row => Assert.Equal(100m, row.QuantityMt));
        var result = await BuildWorkflowWithTokens(retry).StartManyFromLoadingAsync(command);
        Assert.Equal(2, result.CreatedCount);
        Assert.Equal(2, result.PreviouslyCreatedLegIds.Count);
        Assert.Equal(4, result.CompletedCount);
        Assert.Empty(result.Failures);
        await using var verify = fixture.CreateDbContext();
        Assert.Equal(4, await verify.InventoryTransportLegs.CountAsync());
        Assert.Equal(400m, await verify.InventoryTransportLegAllocations.SumAsync(a => a.QuantityMt));
        Assert.Equal(4, await verify.ProcessedFormTokens.CountAsync(t => t.ReferenceId.HasValue));
    }

    [Fact]
    public async Task Fallback_Records_Each_Success_And_Retry_Can_Fix_Only_Failed_Rows()
    {
        Assert.True(fixture.Available, fixture.UnavailableReason);
        await fixture.TruncateAsync();
        await using var db = fixture.CreateDbContext();
        await BulkFromLoadingPerformanceTests.SeedAsync(db, 4, loadedQuantityMt: 200m);
        db.ChangeTracker.Clear();
        var command = Command(4, "partial-fallback");
        var broken = command with { Rows = command.Rows.Select(row => row.LoadingRegisterId == 3
            ? row with { Reference = new string('X', 150) } : row).ToList() };
        var initial = await BuildWorkflowWithTokens(db).StartManyFromLoadingAsync(broken);
        Assert.Equal(3, initial.CreatedCount);
        Assert.Equal(3, Assert.Single(initial.Failures).LoadingRegisterId);
        db.ChangeTracker.Clear();
        var resumed = await BuildWorkflowWithTokens(db).StartManyFromLoadingAsync(command);
        Assert.Equal(1, resumed.CreatedCount);
        Assert.Equal(3, resumed.PreviouslyCreatedLegIds.Count);
        Assert.Empty(resumed.Failures);
        await using var verify = fixture.CreateDbContext();
        Assert.Equal(4, await verify.InventoryTransportLegs.CountAsync());
        Assert.Equal(400m, await verify.InventoryTransportLegAllocations.SumAsync(a => a.QuantityMt));
        Assert.Equal(4, await verify.ProcessedFormTokens.CountAsync(t => t.ReferenceId.HasValue));
    }

    [Fact]
    public async Task A_Completed_Row_Cannot_Be_Changed_Under_The_Same_Request_Identity()
    {
        Assert.True(fixture.Available, fixture.UnavailableReason);
        await fixture.TruncateAsync();
        await using var db = fixture.CreateDbContext();
        await BulkFromLoadingPerformanceTests.SeedAsync(db, 1, loadedQuantityMt: 200m);
        db.ChangeTracker.Clear();
        var command = Command(1, "unchanged-request");
        await BuildWorkflowWithTokens(db).StartManyFromLoadingAsync(command);
        db.ChangeTracker.Clear();
        var changed = command with { Rows = [command.Rows[0] with { QuantityMt = 99m }] };
        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => BuildWorkflowWithTokens(db).StartManyFromLoadingAsync(changed));
        Assert.Equal("TRANSPORT_BULK_REQUEST_CHANGED", error.Code);
        await using var verify = fixture.CreateDbContext();
        Assert.Equal(1, await verify.InventoryTransportLegs.CountAsync());
        Assert.Equal(100m, await verify.InventoryTransportLegAllocations.SumAsync(a => a.QuantityMt));
    }

    [Fact]
    public async Task Subset_Retry_Uses_Stable_Ids_When_Two_Rows_Share_One_Loading()
    {
        Assert.True(fixture.Available, fixture.UnavailableReason);
        await fixture.TruncateAsync();
        await using (var seed = fixture.CreateDbContext())
            await BulkFromLoadingPerformanceTests.SeedAsync(seed, 1, loadedQuantityMt: 200m);
        using var disconnect = new CancellationTokenSource();
        var rowA = Command(1).Rows[0] with { QuantityMt = 50m, RequestRowId = "vehicle-A" };
        var rowB = rowA with { RequestRowId = "vehicle-B", Reference = "second-vehicle" };
        var command = new BulkStartTransportFromLoadingCommand
        {
            Rows = [rowA, rowB], TransportDate = TransportDate, FormToken = "subset-resume", ChunkSize = 1
        };
        await using (var interrupted = fixture.CreateDbContext(new CancelAfterCommit(disconnect)))
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => BuildWorkflowWithTokens(interrupted).StartManyFromLoadingAsync(command, disconnect.Token));
        await using var retry = fixture.CreateDbContext();
        var result = await BuildWorkflowWithTokens(retry).StartManyFromLoadingAsync(command with { Rows = [rowB] });
        Assert.Equal(1, result.CreatedCount);
        Assert.Equal(1, result.PreviouslyCreatedLegIds.Count);
        Assert.Empty(result.Failures);
        await using var verify = fixture.CreateDbContext();
        Assert.Equal(2, await verify.InventoryTransportLegs.CountAsync());
        Assert.Equal(100m, await verify.InventoryTransportLegAllocations.SumAsync(a => a.QuantityMt));
    }

    [Fact]
    public async Task Cancelled_Loadings_Cannot_Be_Revived_By_Single_Or_Bulk_Conversion()
    {
        Assert.True(fixture.Available, fixture.UnavailableReason);
        await fixture.TruncateAsync();
        await using var db = fixture.CreateDbContext();
        await BulkFromLoadingPerformanceTests.SeedAsync(db, 1);
        var loading = await db.LoadingRegisters.SingleAsync();
        loading.IsCancelled = true;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var workflow = BuildWorkflowWithTokens(db);
        var single = await Assert.ThrowsAsync<BusinessRuleException>(() => workflow.StartFromLoadingAsync(new()
        {
            LoadingRegisterId = 1, QuantityMt = 100m, TransportType = LoadingTransportType.Truck,
            TruckId = 1, TransportDate = TransportDate
        }));
        Assert.Equal("TRANSPORT_LOADING_INACTIVE", single.Code);
        db.ChangeTracker.Clear();
        var bulk = await workflow.StartManyFromLoadingAsync(Command(1, "cancelled-loading"));
        Assert.Equal(0, bulk.CompletedCount);
        Assert.Equal("TRANSPORT_LOADING_INACTIVE", Assert.Single(bulk.Failures).Code);
        await using var verify = fixture.CreateDbContext();
        Assert.Empty(await verify.InventoryTransportLegs.ToListAsync());
        Assert.Empty(await verify.ProcessedFormTokens.ToListAsync());
    }

    [Fact]
    public async Task Progress_And_Retry_Cannot_Read_Another_Users_Request()
    {
        Assert.True(fixture.Available, fixture.UnavailableReason);
        await fixture.TruncateAsync();
        await using var db = fixture.CreateDbContext();
        await BulkFromLoadingPerformanceTests.SeedAsync(db, 1, loadedQuantityMt: 200m);
        db.ChangeTracker.Clear();
        var command = Command(1, "owned-request");
        await BuildWorkflowWithTokens(db).StartManyFromLoadingAsync(command);
        var token = await db.ProcessedFormTokens.SingleAsync();
        token.UserId = 42;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var query = new TransportBulkRequestQuery(db);
        Assert.Empty(await query.GetCompletedRowsAsync(command.FormToken!, userId: 41));
        Assert.Single(await query.GetCompletedRowsAsync(command.FormToken!, userId: 42));
        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => BuildWorkflowWithTokens(db).StartManyFromLoadingAsync(command));
        Assert.Equal("TRANSPORT_BULK_REQUEST_OWNER", error.Code);
        Assert.Equal(1, await db.InventoryTransportLegs.CountAsync());
    }

    [Fact]
    public async Task A_Fully_Committed_Filter_Request_Replays_Without_An_Empty_Selection_Error()
    {
        Assert.True(fixture.Available, fixture.UnavailableReason);
        await fixture.TruncateAsync();
        await using var db = fixture.CreateDbContext();
        await BulkFromLoadingPerformanceTests.SeedAsync(db, 2);
        db.ChangeTracker.Clear();
        var model = new TransportBulkFromLoadingViewModel
        {
            UseFilterSelection = true, TransportDate = TransportDate,
            Filter = new TransportBulkLoadingFilter { ContractId = [1] }
        };
        var first = Controller(db);
        Assert.IsType<RedirectToActionResult>(await first.BulkFromLoading(model, "filter-request"));
        db.ChangeTracker.Clear();
        var replay = Controller(db);
        Assert.IsType<RedirectToActionResult>(await replay.BulkFromLoading(model, "filter-request"));
        Assert.True(replay.ModelState.IsValid);
        Assert.Contains("تلاش قبلی", replay.TempData["ok"]!.ToString());
        Assert.Equal(2, await db.InventoryTransportLegs.CountAsync());
        Assert.Equal(200m, await db.InventoryTransportLegAllocations.SumAsync(a => a.QuantityMt));
    }

    [Fact]
    public async Task Partial_Manual_Request_Keeps_Only_Failed_Rows_And_The_Same_Token_In_The_Form()
    {
        Assert.True(fixture.Available, fixture.UnavailableReason);
        await fixture.TruncateAsync();
        await using var db = fixture.CreateDbContext();
        await BulkFromLoadingPerformanceTests.SeedAsync(db, 2);
        (await db.LoadingRegisters.SingleAsync(l => l.Id == 2)).IsCancelled = true;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var model = new TransportBulkFromLoadingViewModel
        {
            TransportDate = TransportDate,
            Rows = [new() { LoadingRegisterId = 1, QuantityMt = 100m, TruckId = 1 },
                    new() { LoadingRegisterId = 2, QuantityMt = 100m, TruckId = 1 }]
        };
        var controller = Controller(db);
        var view = Assert.IsType<ViewResult>(await controller.BulkFromLoading(model, "partial-manual"));
        var returned = Assert.IsType<TransportBulkFromLoadingViewModel>(view.Model);
        Assert.Equal(2, Assert.Single(returned.Rows).LoadingRegisterId);
        Assert.Equal("partial-manual", controller.ViewData["BulkRequestToken"]);
        Assert.Contains("بخشی ثبت شد", controller.TempData["ok"]!.ToString());
        Assert.Equal(1, await db.InventoryTransportLegs.CountAsync());
        Assert.Equal(100m, await db.InventoryTransportLegAllocations.SumAsync(a => a.QuantityMt));
    }

    private static TransportsController Controller(ApplicationDbContext db)
    {
        var context = new DefaultHttpContext();
        return new TransportsController(db, BuildWorkflowWithTokens(db), new TransportQuantityService(db),
            new AfghanistanBusinessClock(TimeProvider.System), new InventoryTransportBatchService(db, new StockService(db)),
            new FormTokenGuard(db))
        {
            ControllerContext = new ControllerContext { HttpContext = context },
            TempData = new TempDataDictionary(context, new EmptyTempDataProvider())
        };
    }

    private sealed class EmptyTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }

    private sealed class CancelAfterCommit(CancellationTokenSource source) : DbTransactionInterceptor
    {
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            source.Cancel();
            return Task.CompletedTask;
        }
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
