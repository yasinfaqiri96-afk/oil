using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Controllers;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.InventoryTransport;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Audit;
using PTGOilSystem.Web.Services.Time;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// صفحهٔ «تبدیل‌های بارگیری به حمل»: فهرست تبدیل‌های قبلی و لغو گروهی آن‌ها با همان
/// CancelAsync «لغو سند حمل».
/// </summary>
public class LoadingConversionCancelTests
{
    private static readonly DateTime TransportDate = new(2026, 9, 5);

    [Fact]
    public async Task Lists_Active_Loading_Conversions_With_Block_Reasons()
    {
        await using var db = BuildDb();
        await SeedAsync(db, loadingCount: 3);
        var (workflow, batches) = BuildServices(db);
        await ConvertAsync(workflow, 3);
        var blockedLegId = await LegIdOfLoadingAsync(db, 3);
        db.LossEvents.Add(new LossEvent
        {
            TransportLegId = blockedLegId,
            ProductId = 1,
            Stage = LossEventStage.TransitLoss,
            EventDate = TransportDate,
            DifferenceQuantityMt = 1m
        });
        await db.SaveChangesAsync();
        var controller = BuildController(db, workflow, batches);

        var model = await GetModelAsync(controller);

        Assert.Equal(3, model.TotalCount);
        Assert.Equal(3, model.Rows.Count);
        Assert.Contains("کسری", model.Rows.Single(r => r.LegId == blockedLegId).BlockReason);
        Assert.All(model.Rows.Where(r => r.LegId != blockedLegId), r => Assert.Null(r.BlockReason));

        var scoped = await GetModelAsync(controller, loadingId: 2);
        Assert.Equal(2, Assert.Single(scoped.Rows).LoadingId);
    }

    [Fact]
    public async Task Bulk_Cancel_Cancels_Selected_Conversions_And_Frees_The_Loading_Remainder()
    {
        await using var db = BuildDb();
        await SeedAsync(db, loadingCount: 3);
        var (workflow, batches) = BuildServices(db);
        await ConvertAsync(workflow, 3);
        var controller = BuildController(db, workflow, batches);
        var selected = new[] { await BatchIdOfLoadingAsync(db, 1), await BatchIdOfLoadingAsync(db, 2) };

        var result = await controller.CancelFromLoading(selected);

        Assert.IsType<RedirectToActionResult>(result);
        Assert.NotNull(controller.TempData["ok"]);
        Assert.Null(controller.TempData["err"]);
        var legs = await db.InventoryTransportLegs.AsNoTracking()
            .Include(l => l.Allocations)
            .ToListAsync();
        Assert.All(
            legs.Where(l => l.Allocations.Any(a => a.SourceLoadingRegisterId is 1 or 2)),
            l => Assert.Equal(InventoryTransportLegStatus.Cancelled, l.Status));
        Assert.Equal(
            InventoryTransportLegStatus.Loaded,
            legs.Single(l => l.Allocations.Any(a => a.SourceLoadingRegisterId == 3)).Status);

        // مقدار لغوشده دوباره قابل تبدیل است.
        var again = await workflow.StartManyFromLoadingAsync(new BulkStartTransportFromLoadingCommand
        {
            Rows = [Row(1)],
            TransportDate = TransportDate
        });
        Assert.Equal(1, again.CreatedCount);

        var remaining = await GetModelAsync(BuildController(db, workflow, batches));
        Assert.Equal(2, remaining.Rows.Count);
    }

    [Fact]
    public async Task Bulk_Cancel_Reports_Blocked_Conversions_And_Still_Cancels_The_Rest()
    {
        await using var db = BuildDb();
        await SeedAsync(db, loadingCount: 2);
        var (workflow, batches) = BuildServices(db);
        await ConvertAsync(workflow, 2);
        var freeLegId = await LegIdOfLoadingAsync(db, 1);
        var blockedLegId = await LegIdOfLoadingAsync(db, 2);
        db.LossEvents.Add(new LossEvent
        {
            TransportLegId = blockedLegId,
            ProductId = 1,
            Stage = LossEventStage.TransitLoss,
            EventDate = TransportDate,
            DifferenceQuantityMt = 1m
        });
        await db.SaveChangesAsync();
        var controller = BuildController(db, workflow, batches);

        await controller.CancelFromLoading(
            [await BatchIdOfLoadingAsync(db, 1), await BatchIdOfLoadingAsync(db, 2)]);

        Assert.NotNull(controller.TempData["ok"]);
        Assert.Contains("TR-", (string)controller.TempData["err"]!);
        Assert.Equal(
            InventoryTransportLegStatus.Cancelled,
            (await db.InventoryTransportLegs.AsNoTracking().SingleAsync(l => l.Id == freeLegId)).Status);
        Assert.Equal(
            InventoryTransportLegStatus.Loaded,
            (await db.InventoryTransportLegs.AsNoTracking().SingleAsync(l => l.Id == blockedLegId)).Status);
    }

    [Fact]
    public async Task Bulk_Cancel_Ignores_Batches_That_Are_Not_Loading_Conversions()
    {
        await using var db = BuildDb();
        await SeedAsync(db, loadingCount: 1);
        var batch = new InventoryTransportBatch
        {
            BatchNumber = "ITB-OTHER",
            ProductId = 1,
            TotalQuantityMt = 10m,
            TransportDate = TransportDate,
            Status = InventoryTransportBatchStatus.Loaded,
            TransportGroupKey = "ITG:OTHER"
        };
        batch.Legs.Add(new InventoryTransportLeg
        {
            TransportGroupKey = "ITG:OTHER",
            ProductId = 1,
            TransportType = LoadingTransportType.Truck,
            LoadedDate = TransportDate,
            QuantityMt = 10m,
            Status = InventoryTransportLegStatus.Loaded
        });
        db.InventoryTransportBatches.Add(batch);
        await db.SaveChangesAsync();
        var (workflow, batches) = BuildServices(db);
        var controller = BuildController(db, workflow, batches);

        await controller.CancelFromLoading([batch.Id]);

        Assert.NotNull(controller.TempData["err"]);
        Assert.Equal(
            InventoryTransportBatchStatus.Loaded,
            (await db.InventoryTransportBatches.AsNoTracking().SingleAsync(b => b.Id == batch.Id)).Status);
    }

    // ───────────────────────── کمکی‌ها ─────────────────────────

    private static async Task<TransportLoadingCancelViewModel> GetModelAsync(
        TransportsController controller,
        int? loadingId = null)
    {
        var view = Assert.IsType<ViewResult>(await controller.CancelFromLoading(loadingId: loadingId));
        return Assert.IsType<TransportLoadingCancelViewModel>(view.Model);
    }

    private static Task<int> LegIdOfLoadingAsync(ApplicationDbContext db, int loadingId)
        => db.InventoryTransportLegAllocations.AsNoTracking()
            .Where(a => a.SourceLoadingRegisterId == loadingId)
            .Select(a => a.InventoryTransportLegId)
            .FirstAsync();

    private static Task<int> BatchIdOfLoadingAsync(ApplicationDbContext db, int loadingId)
        => db.InventoryTransportLegs.AsNoTracking()
            .Where(l => l.Allocations.Any(a => a.SourceLoadingRegisterId == loadingId))
            .Select(l => l.InventoryTransportBatchId!.Value)
            .FirstAsync();

    private static BulkStartTransportFromLoadingRow Row(int loadingId) => new()
    {
        LoadingRegisterId = loadingId,
        QuantityMt = 100m,
        TransportType = LoadingTransportType.Truck,
        TruckId = 1,
        Reference = $"BULK-{loadingId}"
    };

    private static async Task ConvertAsync(TransportWorkflowService workflow, int count)
    {
        var result = await workflow.StartManyFromLoadingAsync(new BulkStartTransportFromLoadingCommand
        {
            Rows = Enumerable.Range(1, count).Select(Row).ToList(),
            TransportDate = TransportDate
        });
        Assert.Empty(result.Failures);
        Assert.Equal(count, result.CreatedCount);
    }

    private static TransportsController BuildController(
        ApplicationDbContext db,
        TransportWorkflowService workflow,
        InventoryTransportBatchService batches)
        => new(db, workflow, new TransportQuantityService(db), new FixedClock(TransportDate), batches)
        {
            TempData = new TempDataDictionary(new DefaultHttpContext(), new TestTempDataProvider())
        };

    private static (TransportWorkflowService Workflow, InventoryTransportBatchService Batches) BuildServices(
        ApplicationDbContext db)
    {
        var stock = new StockService(db);
        var quantities = new TransportQuantityService(db);
        var receipts = new InventoryTransportReceiptService(
            db,
            new CurrencyConversionService(new PricingService(db)),
            quantities: quantities);
        var batches = new InventoryTransportBatchService(db, stock);
        var workflow = new TransportWorkflowService(
            db,
            batches,
            new TransportChainService(db, receipts, quantities),
            receipts,
            new LossEventWorkflowService(db, stock, new AuditService(db)));
        return (workflow, batches);
    }

    private static async Task SeedAsync(ApplicationDbContext db, int loadingCount)
    {
        db.Products.Add(new Product { Id = 1, Code = "DSL", Name = "Diesel" });
        db.Companies.Add(new Company { Id = 1, Code = "PTG", Name = "PTG" });
        db.Suppliers.Add(new Supplier { Id = 1, Name = "Supplier" });
        db.Trucks.Add(new Truck { Id = 1, PlateNumber = "TRK-1", IsActive = true });
        db.Contracts.Add(new Contract
        {
            Id = 1,
            ContractNumber = "PUR-1",
            ContractType = ContractType.Purchase,
            CompanyId = 1,
            ProductId = 1,
            SupplierId = 1,
            ContractDate = new DateTime(2026, 8, 1),
            QuantityMt = 10_000m,
            PricingMethod = PricingMethod.Fixed,
            UnitPriceUsd = 500m
        });
        for (var i = 1; i <= loadingCount; i++)
        {
            db.LoadingRegisters.Add(new LoadingRegister
            {
                Id = i,
                ContractId = 1,
                ProductId = 1,
                TransportType = LoadingTransportType.Truck,
                LoadingDate = new DateTime(2026, 8, 25),
                LoadedQuantityMt = 100m,
                LoadingPriceUsd = 500m,
                RwbNo = $"RWB-{i}"
            });
        }
        await db.SaveChangesAsync();
    }

    private static ApplicationDbContext BuildDb()
        => new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private sealed class FixedClock(DateTime today) : IAfghanistanBusinessClock
    {
        public DateTime Today => today.Date;
        public DateTimeOffset Now => new(today, TimeSpan.FromHours(4.5));
        public (DateTime StartUtc, DateTime EndUtcExclusive) UtcRange(DateTime localDate)
            => (localDate.Date, localDate.Date.AddDays(1));
    }

    private sealed class TestTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();

        public void SaveTempData(HttpContext context, IDictionary<string, object> values)
        {
        }
    }
}
