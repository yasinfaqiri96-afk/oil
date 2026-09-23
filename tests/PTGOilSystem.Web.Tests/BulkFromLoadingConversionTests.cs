using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Controllers;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.Reports;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Audit;
using PTGOilSystem.Web.Services.Exceptions;
using PTGOilSystem.Web.Services.Time;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// تبدیل گروهی «بارگیری → حمل».
///
/// ستون فقرات این مجموعه، تستِ هم‌ارزی است: مسیر گروهی باید دقیقاً همان سطرهایی را بسازد که
/// مسیر تکی می‌سازد. هر مصرف‌کنندهٔ پایین‌دست (کالای در راه، بستن قرارداد، رسید، فروش، لغو،
/// گزارش‌های حمل) فقط همین سطرها را می‌خواند؛ بنابراین هم‌ارزیِ سطرها یعنی هم‌ارزیِ رفتار.
/// </summary>
public class BulkFromLoadingConversionTests
{
    private static readonly DateTime TransportDate = new(2026, 9, 5);

    // ───────────────────────── هم‌ارزی با مسیر تکی ─────────────────────────

    [Fact]
    public async Task Bulk_Produces_The_Same_Rows_As_The_One_By_One_Path()
    {
        await using var singleDb = BuildDb();
        await SeedAsync(singleDb, loadingCount: 12);
        var singleWorkflow = BuildWorkflow(singleDb);
        foreach (var row in BulkRows(12))
        {
            await singleWorkflow.StartFromLoadingAsync(ToSingleCommand(row));
        }

        await using var bulkDb = BuildDb();
        await SeedAsync(bulkDb, loadingCount: 12);
        var bulkResult = await BuildWorkflow(bulkDb).StartManyFromLoadingAsync(
            new BulkStartTransportFromLoadingCommand
            {
                Rows = BulkRows(12),
                TransportDate = TransportDate,
                ChunkSize = 5
            });

        Assert.Empty(bulkResult.Failures);
        Assert.Equal(12, bulkResult.CreatedCount);
        Assert.Equal(
            await SnapshotAsync(singleDb),
            await SnapshotAsync(bulkDb));
    }

    [Fact]
    public async Task Bulk_Creates_One_Batch_And_One_Group_Key_Per_Loading()
    {
        await using var db = BuildDb();
        await SeedAsync(db, loadingCount: 5);

        await BuildWorkflow(db).StartManyFromLoadingAsync(new BulkStartTransportFromLoadingCommand
        {
            Rows = BulkRows(5),
            TransportDate = TransportDate
        });

        var batches = await db.InventoryTransportBatches.AsNoTracking().ToListAsync();
        var legs = await db.InventoryTransportLegs.AsNoTracking().ToListAsync();
        Assert.Equal(5, batches.Count);
        Assert.Equal(5, legs.Count);
        // هر تبدیل یک سفر مستقل است: کلید گروه و شمارهٔ سند نباید بین ردیف‌ها مشترک شود،
        // وگرنه صفحهٔ «سفر» پنج بارگیری را یک حمل نشان می‌دهد و لغو، هر پنج را با هم لغو می‌کند.
        Assert.Equal(5, batches.Select(b => b.TransportGroupKey).Distinct().Count());
        Assert.Equal(5, batches.Select(b => b.BatchNumber).Distinct().Count());
        Assert.Equal(5, legs.Select(l => l.InventoryTransportBatchId).Distinct().Count());
    }

    [Fact]
    public async Task Bulk_Creates_No_Inventory_Movement_And_No_Accounting_Posting()
    {
        await using var db = BuildDb();
        await SeedAsync(db, loadingCount: 4);

        await BuildWorkflow(db).StartManyFromLoadingAsync(new BulkStartTransportFromLoadingCommand
        {
            Rows = BulkRows(4),
            TransportDate = TransportDate
        });

        Assert.Empty(await db.InventoryMovements.AsNoTracking().ToListAsync());
        Assert.Empty(await db.LedgerEntries.AsNoTracking().ToListAsync());
        Assert.Empty(await db.JournalEntries.AsNoTracking().ToListAsync());
        Assert.Empty(await db.LoadingReceipts.AsNoTracking().ToListAsync());
    }

    // ───────────────────────── مقدار و باقیمانده ─────────────────────────

    [Fact]
    public async Task Single_Loading_Converts_Its_Whole_Quantity()
    {
        await using var db = BuildDb();
        await SeedAsync(db, loadingCount: 1);

        var result = await BuildWorkflow(db).StartManyFromLoadingAsync(
            new BulkStartTransportFromLoadingCommand
            {
                Rows = BulkRows(1),
                TransportDate = TransportDate
            });

        Assert.Equal(1, result.CreatedCount);
        Assert.Equal(100m, await db.InventoryTransportLegAllocations.AsNoTracking().SumAsync(a => a.QuantityMt));
    }

    [Fact]
    public async Task Loading_With_Partial_Remainder_Converts_Only_What_Is_Left()
    {
        await using var db = BuildDb();
        await SeedAsync(db, loadingCount: 1);
        db.LoadingReceipts.Add(new LoadingReceipt
        {
            LoadingRegisterId = 1,
            TerminalId = 1,
            ReceiptDate = new DateTime(2026, 9, 1),
            ReceivedQuantityMt = 70m
        });
        await db.SaveChangesAsync();

        var ok = await BuildWorkflow(db).StartManyFromLoadingAsync(new BulkStartTransportFromLoadingCommand
        {
            Rows = [Row(1, quantityMt: 30m)],
            TransportDate = TransportDate
        });
        Assert.Equal(1, ok.CreatedCount);

        var tooMuch = await BuildWorkflow(db).StartManyFromLoadingAsync(new BulkStartTransportFromLoadingCommand
        {
            Rows = [Row(1, quantityMt: 0.5m)],
            TransportDate = TransportDate
        });
        Assert.Equal(0, tooMuch.CreatedCount);
        Assert.Equal("TRANSPORT_LOADING_INSUFFICIENT", Assert.Single(tooMuch.Failures).Code);
    }

    [Fact]
    public async Task Fully_Consumed_Loading_Is_Rejected_Without_Touching_The_Others()
    {
        await using var db = BuildDb();
        await SeedAsync(db, loadingCount: 3);
        db.LoadingReceipts.Add(new LoadingReceipt
        {
            LoadingRegisterId = 2,
            TerminalId = 1,
            ReceiptDate = new DateTime(2026, 9, 1),
            ReceivedQuantityMt = 100m
        });
        await db.SaveChangesAsync();

        var result = await BuildWorkflow(db).StartManyFromLoadingAsync(
            new BulkStartTransportFromLoadingCommand
            {
                Rows = BulkRows(3),
                TransportDate = TransportDate
            });

        Assert.Equal(2, result.CreatedCount);
        var failure = Assert.Single(result.Failures);
        Assert.Equal(2, failure.LoadingRegisterId);
        Assert.Equal("TRANSPORT_LOADING_INSUFFICIENT", failure.Code);
        // ردیف سالم نباید قربانیِ ردیف خراب شود.
        Assert.Equal(
            [1, 3],
            await db.InventoryTransportLegAllocations.AsNoTracking()
                .Select(a => a.SourceLoadingRegisterId!.Value).OrderBy(id => id).ToListAsync());
    }

    [Fact]
    public async Task Two_Rows_On_The_Same_Loading_Cannot_Together_Exceed_Its_Remainder()
    {
        await using var db = BuildDb();
        await SeedAsync(db, loadingCount: 1);

        // هر دو ردیف در یک دسته‌اند؛ مانده فقط یک‌بار خوانده می‌شود، پس مصرفِ ردیف اول باید
        // در حافظه لحاظ شود وگرنه ۱۵۰ تن از یک بارگیریِ ۱۰۰ تنی حمل می‌شود.
        var result = await BuildWorkflow(db).StartManyFromLoadingAsync(
            new BulkStartTransportFromLoadingCommand
            {
                Rows = [Row(1, quantityMt: 60m), Row(1, quantityMt: 90m)],
                TransportDate = TransportDate
            });

        Assert.Equal(1, result.CreatedCount);
        Assert.Equal("TRANSPORT_LOADING_INSUFFICIENT", Assert.Single(result.Failures).Code);
        Assert.Equal(60m, await db.InventoryTransportLegAllocations.AsNoTracking().SumAsync(a => a.QuantityMt));
    }

    [Fact]
    public async Task Receipt_Shortage_Reduces_The_Convertible_Remainder()
    {
        await using var db = BuildDb();
        await SeedAsync(db, loadingCount: 1);
        db.LossEvents.Add(new LossEvent
        {
            LoadingRegisterId = 1,
            Stage = LossEventStage.ReceiptShortage,
            EventDate = new DateTime(2026, 9, 1),
            DifferenceQuantityMt = 25m
        });
        await db.SaveChangesAsync();

        var result = await BuildWorkflow(db).StartManyFromLoadingAsync(
            new BulkStartTransportFromLoadingCommand
            {
                Rows = [Row(1, quantityMt: 80m)],
                TransportDate = TransportDate
            });

        Assert.Equal(0, result.CreatedCount);
        Assert.Equal("TRANSPORT_LOADING_INSUFFICIENT", Assert.Single(result.Failures).Code);
    }

    // ───────────────────── تنوع قرارداد، جنس، وسیله و طرفِ کرایه ─────────────────────

    [Fact]
    public async Task Mixed_Contracts_Products_Trucks_Drivers_And_Carriers_Keep_Their_Own_Data()
    {
        await using var db = BuildDb();
        await SeedAsync(db, loadingCount: 0);
        db.Products.Add(new Product { Id = 2, Code = "PET", Name = "Petrol" });
        db.Contracts.Add(new Contract
        {
            Id = 2,
            ContractNumber = "PUR-2",
            ContractType = ContractType.Purchase,
            CompanyId = 1,
            ProductId = 2,
            SupplierId = 1,
            ContractDate = new DateTime(2026, 8, 1),
            QuantityMt = 1000m,
            PricingMethod = PricingMethod.Fixed,
            UnitPriceUsd = 600m
        });
        db.Trucks.Add(new Truck { Id = 2, PlateNumber = "TRK-2", IsActive = true });
        db.Wagons.Add(new Wagon { Id = 1, WagonNumber = "WGN-1", IsActive = true });
        db.Drivers.Add(new Driver { Id = 1, FullName = "Driver One", IsActive = true });
        db.Drivers.Add(new Driver { Id = 2, FullName = "Driver Two", IsActive = true });
        db.ServiceProviders.Add(new ServiceProvider { Id = 1, Name = "Carrier A", IsActive = true });
        AddLoading(db, id: 1, contractId: 1, productId: 1);
        AddLoading(db, id: 2, contractId: 2, productId: 2);
        AddLoading(db, id: 3, contractId: 2, productId: 2);
        await db.SaveChangesAsync();

        var result = await BuildWorkflow(db).StartManyFromLoadingAsync(
            new BulkStartTransportFromLoadingCommand
            {
                Rows =
                [
                    Row(1, truckId: 1, driverId: 1),
                    Row(2, truckId: 2, driverId: 2, serviceProviderId: 1),
                    new BulkStartTransportFromLoadingRow
                    {
                        LoadingRegisterId = 3,
                        QuantityMt = 100m,
                        TransportType = LoadingTransportType.Wagon,
                        WagonId = 1
                    }
                ],
                TransportDate = TransportDate
            });

        Assert.Empty(result.Failures);
        var legs = await db.InventoryTransportLegs.AsNoTracking()
            .Include(l => l.Allocations)
            .OrderBy(l => l.Id)
            .ToListAsync();

        Assert.Equal(1, legs[0].SourcePurchaseContractId);
        Assert.Equal(1, legs[0].ProductId);
        Assert.Equal(1, legs[0].TruckId);
        Assert.Equal(1, legs[0].DriverId);
        // بدون شرکت خدماتی، طرفِ کرایه همان راننده است.
        Assert.Equal(AccountingPartyType.Driver, legs[0].CarrierPartyType);
        Assert.Equal(1, legs[0].CarrierPartyId);

        Assert.Equal(2, legs[1].SourcePurchaseContractId);
        Assert.Equal(2, legs[1].ProductId);
        Assert.Equal(2, legs[1].TruckId);
        // شرکت خدماتی بر راننده اولویت دارد.
        Assert.Equal(AccountingPartyType.ServiceProvider, legs[1].CarrierPartyType);
        Assert.Equal(1, legs[1].CarrierPartyId);

        Assert.Equal(LoadingTransportType.Wagon, legs[2].TransportType);
        Assert.Equal(1, legs[2].WagonId);
        Assert.Null(legs[2].TruckId);
        Assert.Null(legs[2].DriverId);

        // ردیابی: هر حمل دقیقاً به بارگیری و قرارداد خودش وصل است.
        Assert.Equal([1, 2, 3], legs.Select(l => l.Allocations.Single().SourceLoadingRegisterId!.Value).ToList());
        Assert.Equal([1, 2, 2], legs.Select(l => l.Allocations.Single().SourcePurchaseContractId).ToList());
    }

    [Fact]
    public async Task Invalid_Or_Inactive_Vehicle_Fails_Only_Its_Own_Row()
    {
        await using var db = BuildDb();
        await SeedAsync(db, loadingCount: 4);
        db.Trucks.Add(new Truck { Id = 9, PlateNumber = "TRK-OFF", IsActive = false });
        db.Drivers.Add(new Driver { Id = 9, FullName = "Retired", IsActive = false });
        await db.SaveChangesAsync();

        var result = await BuildWorkflow(db).StartManyFromLoadingAsync(
            new BulkStartTransportFromLoadingCommand
            {
                Rows =
                [
                    Row(1),
                    Row(2, truckId: 9),      // موتر غیرفعال
                    Row(3, truckId: null),   // بدون وسیله
                    Row(4, driverId: 9)      // رانندهٔ غیرفعال
                ],
                TransportDate = TransportDate
            });

        Assert.Equal(1, result.CreatedCount);
        Assert.Equal(
            ["TRANSPORT_RECEIPT_VEHICLE_INVALID", "TRANSPORT_RECEIPT_VEHICLE_INVALID", "TRANSPORT_RECEIPT_DRIVER_INVALID"],
            result.Failures.Select(f => f.Code).ToList());
        Assert.Equal(1, await db.InventoryTransportLegs.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task Inactive_Service_Provider_Fails_Only_Its_Own_Row()
    {
        await using var db = BuildDb();
        await SeedAsync(db, loadingCount: 2);
        db.ServiceProviders.Add(new ServiceProvider { Id = 5, Name = "Closed", IsActive = false });
        await db.SaveChangesAsync();

        var result = await BuildWorkflow(db).StartManyFromLoadingAsync(
            new BulkStartTransportFromLoadingCommand
            {
                Rows = [Row(1), Row(2, serviceProviderId: 5)],
                TransportDate = TransportDate
            });

        Assert.Equal(1, result.CreatedCount);
        Assert.Equal("TRANSPORT_RECEIPT_PROVIDER_INVALID", Assert.Single(result.Failures).Code);
    }

    [Fact]
    public async Task Non_Positive_Quantity_Fails_Only_Its_Own_Row()
    {
        await using var db = BuildDb();
        await SeedAsync(db, loadingCount: 2);

        var result = await BuildWorkflow(db).StartManyFromLoadingAsync(
            new BulkStartTransportFromLoadingCommand
            {
                Rows = [Row(1, quantityMt: 0m), Row(2)],
                TransportDate = TransportDate
            });

        Assert.Equal(1, result.CreatedCount);
        Assert.Equal("TRANSPORT_LOADING_QTY_INVALID", Assert.Single(result.Failures).Code);
    }

    [Fact]
    public async Task Missing_Loading_Fails_Only_Its_Own_Row_And_The_Chunk_Still_Commits()
    {
        await using var db = BuildDb();
        await SeedAsync(db, loadingCount: 2);

        // شناسهٔ ناموجود کلِ دسته را می‌اندازد؛ بازگشت به مسیر تکی باید بقیه را نجات دهد.
        var result = await BuildWorkflow(db).StartManyFromLoadingAsync(
            new BulkStartTransportFromLoadingCommand
            {
                Rows = [Row(1), Row(404), Row(2)],
                TransportDate = TransportDate
            });

        Assert.Equal(2, result.CreatedCount);
        var failure = Assert.Single(result.Failures);
        Assert.Equal(404, failure.LoadingRegisterId);
        Assert.Equal("TRANSPORT_LOADING_NOT_FOUND", failure.Code);
    }

    [Fact]
    public async Task Five_Hundred_Loadings_All_Convert_With_Full_Traceability()
    {
        const int count = 500;
        await using var db = BuildDb();
        await SeedAsync(db, loadingCount: count);

        var result = await BuildWorkflow(db).StartManyFromLoadingAsync(
            new BulkStartTransportFromLoadingCommand
            {
                Rows = BulkRows(count),
                TransportDate = TransportDate
            });

        Assert.Empty(result.Failures);
        Assert.Equal(count, result.CreatedCount);
        var allocations = await db.InventoryTransportLegAllocations.AsNoTracking().ToListAsync();
        Assert.Equal(count, allocations.Count);
        Assert.Equal(count, allocations.Select(a => a.SourceLoadingRegisterId).Distinct().Count());
        Assert.All(allocations, a => Assert.Equal(100m, a.QuantityMt));
        Assert.Equal(count, await db.InventoryTransportBatches.AsNoTracking()
            .Select(b => b.TransportGroupKey).Distinct().CountAsync());
    }

    // ───────────────────────── مراحل بعدی حمل ─────────────────────────

    [Fact]
    public async Task Cancelling_A_Bulk_Created_Transport_Releases_Only_Its_Own_Loading()
    {
        await using var db = BuildDb();
        await SeedAsync(db, loadingCount: 2);
        var workflow = BuildWorkflow(db);
        var batches = new InventoryTransportBatchService(db, new StockService(db));

        await workflow.StartManyFromLoadingAsync(new BulkStartTransportFromLoadingCommand
        {
            Rows = BulkRows(2),
            TransportDate = TransportDate
        });
        var first = await db.InventoryTransportLegs.AsNoTracking().OrderBy(l => l.Id).FirstAsync();
        await batches.CancelAsync(first.InventoryTransportBatchId!.Value);

        // بارگیری اول دوباره کامل آزاد است، بارگیری دوم هنوز مصرف‌شده.
        var replacement = await workflow.StartFromLoadingAsync(new StartTransportFromLoadingCommand
        {
            LoadingRegisterId = 1,
            QuantityMt = 100m,
            TransportType = LoadingTransportType.Truck,
            TruckId = 1,
            TransportDate = TransportDate
        });
        Assert.Equal(100m, replacement.QuantityMt);

        var blocked = await Assert.ThrowsAsync<BusinessRuleException>(() =>
            workflow.StartFromLoadingAsync(new StartTransportFromLoadingCommand
            {
                LoadingRegisterId = 2,
                QuantityMt = 1m,
                TransportType = LoadingTransportType.Truck,
                TruckId = 1,
                TransportDate = TransportDate
            }));
        Assert.Equal("TRANSPORT_LOADING_INSUFFICIENT", blocked.Code);
        Assert.Empty(await db.InventoryMovements.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Goods_In_Transit_Counts_A_Bulk_Converted_Loading_Once_On_The_Transport_Side()
    {
        await using var db = BuildDb();
        await SeedAsync(db, loadingCount: 2);

        await BuildWorkflow(db).StartManyFromLoadingAsync(new BulkStartTransportFromLoadingCommand
        {
            Rows = [Row(1, quantityMt: 40m), Row(2)],
            TransportDate = TransportDate
        });

        var controller = new ReportsController(db, clock: new FixedClock(new DateTime(2026, 9, 10)));
        var view = Assert.IsType<ViewResult>(await controller.GoodsInTransit(new GoodsInTransitFilterViewModel()));
        var model = Assert.IsType<GoodsInTransitReportViewModel>(view.Model);

        // بارگیری ۱: ۴۰ روی حمل، ۶۰ هنوز در مبدأ. بارگیری ۲: کل ۱۰۰ روی حمل، صفر در مبدأ.
        // مجموع باید همان ۲۰۰ تن اولیه بماند — نه بیشتر (دوبار شمردن) و نه کمتر (گم‌شدن).
        Assert.Equal(200m, model.Totals.TotalQuantityMt);
        Assert.Equal(
            60m,
            model.Rows.Where(r => r.Kind == GoodsInTransitKind.FromOrigin).Sum(r => r.QuantityMt));
        Assert.Equal(
            140m,
            model.Rows.Where(r => r.Kind == GoodsInTransitKind.InternalTransfer).Sum(r => r.QuantityMt));
    }

    [Fact]
    public async Task Remaining_Quantity_Of_A_Bulk_Created_Leg_Is_Its_Full_Load()
    {
        await using var db = BuildDb();
        await SeedAsync(db, loadingCount: 3);

        await BuildWorkflow(db).StartManyFromLoadingAsync(new BulkStartTransportFromLoadingCommand
        {
            Rows = BulkRows(3),
            TransportDate = TransportDate
        });

        var legIds = await db.InventoryTransportLegs.AsNoTracking().Select(l => l.Id).ToListAsync();
        var remaining = await new TransportQuantityService(db).GetRemainingMtAsync(legIds);
        Assert.All(legIds, id => Assert.Equal(100m, remaining[id]));

        // اتحاد نگهداشت مقدار برای هر حملِ تازه: همهٔ بار هنوز روی وسیله است.
        foreach (var id in legIds)
        {
            var quantities = await new TransportQuantityService(db).GetQuantitiesAsync(id);
            Assert.True(quantities.IsBalanced);
            Assert.Equal(0m, quantities.ConsumedMt);
        }
    }

    // ───────────────────────── کمکی‌ها ─────────────────────────

    private static StartTransportFromLoadingCommand ToSingleCommand(BulkStartTransportFromLoadingRow row)
        => new()
        {
            LoadingRegisterId = row.LoadingRegisterId,
            QuantityMt = row.QuantityMt,
            TransportType = row.TransportType,
            TruckId = row.TruckId,
            WagonId = row.WagonId,
            VesselId = row.VesselId,
            DriverId = row.DriverId,
            ServiceProviderId = row.ServiceProviderId,
            TransportDate = TransportDate,
            Reference = row.Reference,
            Notes = row.Notes
        };

    /// <summary>
    /// تصویرِ قابل‌مقایسهٔ نتیجه. شناسه‌ها، کلید گروه، شمارهٔ سند و مهرهای زمان عمداً بیرون‌اند
    /// چون ذاتاً یکتا هستند؛ هر ستون دیگری که پایین‌دست خوانده می‌شود باید یکسان باشد.
    /// </summary>
    private static async Task<List<string>> SnapshotAsync(ApplicationDbContext db)
    {
        var legs = await db.InventoryTransportLegs.AsNoTracking()
            .Include(l => l.Allocations)
            .Include(l => l.InventoryTransportBatch)
            .OrderBy(l => l.Id)
            .ToListAsync();

        return legs.Select(l => string.Join("|",
            l.SourcePurchaseContractId,
            l.ProductId,
            l.SourceTerminalId,
            l.SourceStorageTankId,
            l.TransportType,
            l.TruckId,
            l.WagonId,
            l.VesselId,
            l.DriverId,
            l.ServiceProviderId,
            l.OperationalAssetId,
            l.CarrierType,
            l.CarrierPartyType,
            l.CarrierPartyId,
            l.LoadedDate.ToString("O"),
            l.QuantityMt,
            l.Status,
            l.RwbNo,
            l.BillOfLadingNumber,
            l.RouteDescription,
            l.PurchaseUnitCostUsd,
            l.Notes,
            l.ShipmentId,
            l.OutboundInventoryMovementId,
            l.IsFreightSettled,
            // سند حمل
            l.InventoryTransportBatch!.ProductId,
            l.InventoryTransportBatch.TotalQuantityMt,
            l.InventoryTransportBatch.TransportDate.ToString("O"),
            l.InventoryTransportBatch.Status,
            l.InventoryTransportBatch.SourceTerminalId,
            l.InventoryTransportBatch.SourceStorageTankId,
            l.InventoryTransportBatch.Notes,
            // سهم‌های منبع
            string.Join(";", l.Allocations.OrderBy(a => a.Id).Select(a => string.Join(",",
                a.SourceLoadingRegisterId,
                a.SourcePurchaseContractId,
                a.SourceLoadingReceiptId,
                a.SourceInventoryMovementId,
                a.SourceTransportLegId,
                a.SourceTransportReceiptId,
                a.QuantityMt,
                a.OutboundInventoryMovementId)))))
            .ToList();
    }

    private static List<BulkStartTransportFromLoadingRow> BulkRows(int count)
        => Enumerable.Range(1, count).Select(id => Row(id)).ToList();

    private static BulkStartTransportFromLoadingRow Row(
        int loadingRegisterId,
        decimal quantityMt = 100m,
        int? truckId = 1,
        int? driverId = null,
        int? serviceProviderId = null)
        => new()
        {
            LoadingRegisterId = loadingRegisterId,
            QuantityMt = quantityMt,
            TransportType = LoadingTransportType.Truck,
            TruckId = truckId,
            DriverId = driverId,
            ServiceProviderId = serviceProviderId,
            Reference = $"BULK-{loadingRegisterId}",
            Label = $"بارگیری #{loadingRegisterId}"
        };

    private static TransportWorkflowService BuildWorkflow(ApplicationDbContext db)
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
            new LossEventWorkflowService(db, stock, new AuditService(db)));
    }

    private static void AddLoading(ApplicationDbContext db, int id, int contractId, int productId)
        => db.LoadingRegisters.Add(new LoadingRegister
        {
            Id = id,
            ContractId = contractId,
            ProductId = productId,
            TransportType = LoadingTransportType.Truck,
            LoadingDate = new DateTime(2026, 8, 25),
            LoadedQuantityMt = 100m,
            LoadingPriceUsd = 500m,
            RwbNo = $"RWB-{id}"
        });

    private static async Task SeedAsync(ApplicationDbContext db, int loadingCount)
    {
        db.Products.Add(new Product { Id = 1, Code = "DSL", Name = "Diesel" });
        db.Companies.Add(new Company { Id = 1, Code = "PTG", Name = "PTG" });
        db.Suppliers.Add(new Supplier { Id = 1, Name = "Supplier" });
        db.Terminals.Add(new Terminal { Id = 1, Code = "T1", Name = "Terminal" });
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
            QuantityMt = 1_000_000m,
            PricingMethod = PricingMethod.Fixed,
            UnitPriceUsd = 500m
        });
        for (var i = 1; i <= loadingCount; i++)
        {
            AddLoading(db, i, contractId: 1, productId: 1);
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
}
