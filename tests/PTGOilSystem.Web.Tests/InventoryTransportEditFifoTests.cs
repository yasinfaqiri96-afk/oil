using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.InventoryTransport;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Exceptions;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// ویرایشِ یک سند حملِ پیش‌نویس. پیش‌نویس موجودی را رزرو نمی‌کند، پس «قابل حمل»ِ منابع بین
/// ثبت و ویرایش جابه‌جا می‌شود. این تست‌ها می‌گویند ذخیرهٔ بدونِ تغییر نباید فقط به‌خاطر
/// توزیعِ کهنه رد شود — ولی کمبودِ واقعی موجودی و توزیعِ دستکاری‌شدهٔ غیر-FIFO باید همچنان رد شود.
/// </summary>
public class InventoryTransportEditFifoTests
{
    [Fact]
    public async Task Stale_Draft_Allocation_Is_Rejected_With_Not_Fifo_Without_Normalization()
    {
        await using var db = CreateDb();
        var sourceIds = await SeedAsync(db);
        // منبع اول در زمان ثبت فقط ۶۰ MT قابل حمل داشت (۴۰ MT جای دیگر رفته بود)،
        // بعد آن خروجی برگشت خورد و منبع اول دوباره ۱۰۰ MT شد.
        var blocking = await AddOutboundAsync(db, contractId: 1, quantityMt: 40m);
        var batch = await BuildService(db).CreateAsync(BuildSplitDraftModel(sourceIds), null);
        db.InventoryMovements.Remove(blocking);
        await db.SaveChangesAsync();

        // همان چیزی که فرم ویرایش بدون آماده‌سازی می‌فرستد: توزیعِ ذخیره‌شده، دست‌نخورده.
        var model = BuildEditModel(await ReloadAsync(db, batch.Id));

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => BuildService(db).UpdateDraftAsync(batch.Id, model));

        Assert.Equal("INVENTORY_TRANSPORT_ALLOCATION_NOT_FIFO", error.Code);
    }

    [Fact]
    public async Task Normalized_Edit_Saves_Unchanged_Draft_After_Availability_Grew()
    {
        await using var db = CreateDb();
        var sourceIds = await SeedAsync(db);
        var blocking = await AddOutboundAsync(db, contractId: 1, quantityMt: 40m);
        var batch = await BuildService(db).CreateAsync(BuildSplitDraftModel(sourceIds), null);
        db.InventoryMovements.Remove(blocking);
        await db.SaveChangesAsync();

        var model = await BuildNormalizedEditModelAsync(db, batch.Id);
        var updated = await BuildService(db).UpdateDraftAsync(batch.Id, model);

        // مقدار وسایط دست‌نخورده؛ فقط توزیع با FIFO جاری (منبع اول ۱۰۰ MT) بازسازی شده است.
        Assert.Equal(150m, updated.TotalQuantityMt);
        var truckLeg = updated.Legs.Single(l => l.QuantityMt == 120m);
        Assert.Equal(100m, truckLeg.Allocations.Single(a => a.SourceInventoryMovementId == sourceIds.First).QuantityMt);
        Assert.Equal(20m, truckLeg.Allocations.Single(a => a.SourceInventoryMovementId == sourceIds.Second).QuantityMt);
        var wagonLeg = updated.Legs.Single(l => l.QuantityMt == 30m);
        Assert.Equal(30m, wagonLeg.Allocations.Single().QuantityMt);
    }

    [Fact]
    public async Task Stale_Draft_Allocation_Is_Rejected_As_Overdraw_When_Availability_Shrinks()
    {
        await using var db = CreateDb();
        var sourceIds = await SeedAsync(db);
        var batch = await CreateDraftAsync(db, sourceIds);
        await ConsumeFromFirstSourceAsync(db, sourceIds, quantityMt: 40m);

        var model = BuildEditModel(await ReloadAsync(db, batch.Id));

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => BuildService(db).UpdateDraftAsync(batch.Id, model));

        Assert.Equal("INVENTORY_TRANSPORT_SOURCE_OVERDRAW", error.Code);
    }

    [Fact]
    public async Task Normalized_Edit_Saves_Unchanged_Draft_After_Availability_Moved()
    {
        await using var db = CreateDb();
        var sourceIds = await SeedAsync(db);
        var batch = await CreateDraftAsync(db, sourceIds);
        await ConsumeFromFirstSourceAsync(db, sourceIds, quantityMt: 40m);

        var model = await BuildNormalizedEditModelAsync(db, batch.Id);
        var updated = await BuildService(db).UpdateDraftAsync(batch.Id, model);

        // مقدار وسایط دست‌نخورده، جمع کل حفظ شده، فقط توزیع سهم‌ها عوض شده است.
        Assert.Equal(InventoryTransportBatchStatus.Draft, updated.Status);
        Assert.Equal(150m, updated.TotalQuantityMt);
        Assert.Equal(2, updated.Legs.Count);
        Assert.Contains(updated.Legs, l => l.QuantityMt == 120m);
        Assert.Contains(updated.Legs, l => l.QuantityMt == 30m);
        Assert.All(updated.Legs, l => Assert.Equal(l.QuantityMt, l.Allocations.Sum(a => a.QuantityMt)));

        // FIFO جاری: منبع اول فقط ۶۰ MT قابل حمل دارد، بقیه از منبع دوم.
        var truckLeg = updated.Legs.Single(l => l.QuantityMt == 120m);
        Assert.Equal(60m, truckLeg.Allocations.Single(a => a.SourceInventoryMovementId == sourceIds.First).QuantityMt);
        Assert.Equal(60m, truckLeg.Allocations.Single(a => a.SourceInventoryMovementId == sourceIds.Second).QuantityMt);
        var wagonLeg = updated.Legs.Single(l => l.QuantityMt == 30m);
        Assert.Equal(30m, wagonLeg.Allocations.Single().QuantityMt);
        Assert.Equal(sourceIds.Second, wagonLeg.Allocations.Single().SourceInventoryMovementId);

        // قرارداد هر سهم = قرارداد همان منبع.
        Assert.Equal(1, truckLeg.Allocations.Single(a => a.SourceInventoryMovementId == sourceIds.First).SourcePurchaseContractId);
        Assert.Equal(2, wagonLeg.Allocations.Single().SourcePurchaseContractId);
    }

    [Fact]
    public async Task Normalization_Touches_Nothing_When_Availability_Is_Unchanged()
    {
        await using var db = CreateDb();
        var sourceIds = await SeedAsync(db);
        var batch = await CreateDraftAsync(db, sourceIds);

        var before = BuildEditModel(await ReloadAsync(db, batch.Id));
        var after = await BuildNormalizedEditModelAsync(db, batch.Id);

        Assert.Equal(
            before.Sources.Select(s => (s.SourceInventoryMovementId, s.QuantityMt)),
            after.Sources.Select(s => (s.SourceInventoryMovementId, s.QuantityMt)));
        for (var i = 0; i < before.Vehicles.Count; i++)
        {
            Assert.Equal(
                before.Vehicles[i].Allocations
                    .Select(a => (a.SourceInventoryMovementId, a.QuantityMt))
                    .OrderBy(x => x.SourceInventoryMovementId),
                after.Vehicles[i].Allocations
                    .Select(a => (a.SourceInventoryMovementId, a.QuantityMt))
                    .OrderBy(x => x.SourceInventoryMovementId));
        }
    }

    [Fact]
    public async Task Normalization_Writes_Nothing_To_The_Database()
    {
        await using var db = CreateDb();
        var sourceIds = await SeedAsync(db);
        var batch = await CreateDraftAsync(db, sourceIds);
        await ConsumeFromFirstSourceAsync(db, sourceIds, quantityMt: 40m);

        var allocationsBefore = await db.InventoryTransportLegAllocations.AsNoTracking()
            .Where(a => a.InventoryTransportLeg!.InventoryTransportBatchId == batch.Id)
            .Select(a => new { a.SourceInventoryMovementId, a.QuantityMt })
            .ToListAsync();

        await BuildNormalizedEditModelAsync(db, batch.Id);

        var allocationsAfter = await db.InventoryTransportLegAllocations.AsNoTracking()
            .Where(a => a.InventoryTransportLeg!.InventoryTransportBatchId == batch.Id)
            .Select(a => new { a.SourceInventoryMovementId, a.QuantityMt })
            .ToListAsync();
        Assert.Equal(allocationsBefore, allocationsAfter);
    }

    [Fact]
    public async Task Normalized_Edit_Is_Still_Rejected_When_Stock_Is_Really_Short()
    {
        await using var db = CreateDb();
        var sourceIds = await SeedAsync(db);
        var batch = await CreateDraftAsync(db, sourceIds);
        // ۱۵۰ MT لازم است ولی از منابعِ انتخابی فقط ۱۱۰ MT می‌ماند.
        await ConsumeFromFirstSourceAsync(db, sourceIds, quantityMt: 90m);

        var model = await BuildNormalizedEditModelAsync(db, batch.Id);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => BuildService(db).UpdateDraftAsync(batch.Id, model));

        Assert.Equal("INVENTORY_TRANSPORT_LEG_TOTAL", error.Code);
        var reloaded = await ReloadAsync(db, batch.Id);
        Assert.Equal(150m, reloaded.TotalQuantityMt);
        Assert.Equal(2, reloaded.Legs.Count);
    }

    [Fact]
    public async Task Hand_Edited_Non_Fifo_Allocation_Is_Still_Rejected_On_Update()
    {
        await using var db = CreateDb();
        var sourceIds = await SeedAsync(db);
        var batch = await CreateDraftAsync(db, sourceIds);

        var model = await BuildNormalizedEditModelAsync(db, batch.Id);
        // همان جمع‌ها، ولی ترتیب مصرف رعایت نشده.
        model.Sources =
        [
            new() { SourceInventoryMovementId = sourceIds.First, QuantityMt = 60m },
            new() { SourceInventoryMovementId = sourceIds.Second, QuantityMt = 90m }
        ];
        model.Vehicles[0].Allocations =
        [
            new() { SourceInventoryMovementId = sourceIds.First, QuantityMt = 60m },
            new() { SourceInventoryMovementId = sourceIds.Second, QuantityMt = 60m }
        ];
        model.Vehicles[1].Allocations =
        [
            new() { SourceInventoryMovementId = sourceIds.Second, QuantityMt = 30m }
        ];

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => BuildService(db).UpdateDraftAsync(batch.Id, model));

        Assert.Equal("INVENTORY_TRANSPORT_ALLOCATION_NOT_FIFO", error.Code);
    }

    [Fact]
    public async Task Normalization_Spreads_Fractional_Quantities_Across_Sources_And_Vehicles()
    {
        await using var db = CreateDb();
        var sourceIds = await SeedAsync(db);
        var batch = await CreateDraftAsync(db, sourceIds);
        await ConsumeFromFirstSourceAsync(db, sourceIds, quantityMt: 39.5555m);

        var model = await BuildNormalizedEditModelAsync(db, batch.Id);
        var updated = await BuildService(db).UpdateDraftAsync(batch.Id, model);

        var allocations = updated.Legs.SelectMany(l => l.Allocations).ToList();
        Assert.Equal(150m, allocations.Sum(a => a.QuantityMt));
        Assert.All(updated.Legs, l => Assert.Equal(l.QuantityMt, l.Allocations.Sum(a => a.QuantityMt)));
        // سقفِ منبع اول = ۱۰۰ − ۳۹٫۵۵۵۵.
        Assert.Equal(
            60.4445m,
            allocations.Where(a => a.SourceInventoryMovementId == sourceIds.First).Sum(a => a.QuantityMt));
    }

    // ---- helpers ----

    // همان کاری که InventoryTransportLegsController.EditBatch (GET) می‌کند:
    // ViewModel از سند ساخته می‌شود و سهم‌ها با «قابل حملِ امروز» بازسازی می‌شوند.
    private static async Task<InventoryTransportFromInventoryViewModel> BuildNormalizedEditModelAsync(
        ApplicationDbContext db,
        int batchId)
    {
        var batch = await ReloadAsync(db, batchId);
        var model = BuildEditModel(batch);
        var sources = await BuildService(db).GetAvailableSourcesAsync(
            model.SourceTerminalId, model.SourceStorageTankId, model.ProductId, model.ShipmentId);
        InventoryTransportBatchService.ApplyCurrentFifoAllocations(model, sources);
        return model;
    }

    private static InventoryTransportFromInventoryViewModel BuildEditModel(InventoryTransportBatch batch)
        => new()
        {
            BatchId = batch.Id,
            SourceTerminalId = batch.SourceTerminalId,
            SourceStorageTankId = batch.SourceStorageTankId ?? 0,
            ProductId = batch.ProductId,
            TransportDate = batch.TransportDate,
            SubmissionMode = InventoryTransportSubmissionMode.Draft,
            Sources = batch.Legs
                .SelectMany(l => l.Allocations)
                .Where(a => a.SourceInventoryMovementId != null)
                .GroupBy(a => a.SourceInventoryMovementId!.Value)
                .Select(g => new InventoryTransportSourceSelectionInput
                {
                    SourceInventoryMovementId = g.Key,
                    QuantityMt = g.Sum(a => a.QuantityMt)
                })
                .ToList(),
            Vehicles = batch.Legs
                .OrderBy(l => l.Id)
                .Select(l => new InventoryTransportVehicleInput
                {
                    TransportType = l.TransportType,
                    TruckId = l.TruckId,
                    WagonId = l.WagonId,
                    DriverId = l.DriverId,
                    QuantityMt = l.QuantityMt,
                    CapacityMt = l.CapacityMt,
                    CarrierType = l.CarrierType ?? CarrierType.ServiceProvider,
                    ServiceProviderId = l.ServiceProviderId,
                    OperationalAssetId = l.OperationalAssetId,
                    FreightAmount = l.FreightAmount,
                    FreightCurrencyId = l.FreightCurrencyId,
                    Allocations = l.Allocations
                        .Where(a => a.SourceInventoryMovementId != null)
                        .Select(a => new InventoryTransportVehicleAllocationInput
                        {
                            SourceInventoryMovementId = a.SourceInventoryMovementId!.Value,
                            QuantityMt = a.QuantityMt
                        })
                        .ToList()
                })
                .ToList()
        };

    private static async Task<InventoryTransportBatch> ReloadAsync(ApplicationDbContext db, int batchId)
        => await db.InventoryTransportBatches
            .AsNoTracking()
            .Include(b => b.Legs).ThenInclude(l => l.Allocations)
            .SingleAsync(b => b.Id == batchId);

    private static async Task<InventoryTransportBatch> CreateDraftAsync(
        ApplicationDbContext db,
        (int First, int Second) sourceIds)
        => await BuildService(db).CreateAsync(BuildDraftModel(sourceIds), null);

    // یک حملِ بارگیری‌شدهٔ دیگر که از منبع اول برمی‌دارد؛ «قابل حمل»ِ آن منبع کم می‌شود.
    private static async Task ConsumeFromFirstSourceAsync(
        ApplicationDbContext db,
        (int First, int Second) sourceIds,
        decimal quantityMt)
    {
        var model = new InventoryTransportFromInventoryViewModel
        {
            SourceTerminalId = 1,
            SourceStorageTankId = 1,
            ProductId = 1,
            TransportDate = new DateTime(2026, 7, 2),
            SubmissionMode = InventoryTransportSubmissionMode.Loaded,
            Sources = [new() { SourceInventoryMovementId = sourceIds.First, QuantityMt = quantityMt }],
            Vehicles =
            [
                new()
                {
                    TransportType = LoadingTransportType.Truck,
                    TruckId = 1,
                    DriverId = 1,
                    QuantityMt = quantityMt,
                    CarrierType = CarrierType.ServiceProvider,
                    ServiceProviderId = 1,
                    Allocations = [new() { SourceInventoryMovementId = sourceIds.First, QuantityMt = quantityMt }]
                }
            ]
        };
        await BuildService(db).CreateAsync(model, null);
    }

    private static InventoryTransportFromInventoryViewModel BuildDraftModel((int First, int Second) sources)
        => new()
        {
            SourceTerminalId = 1,
            SourceStorageTankId = 1,
            ProductId = 1,
            TransportDate = new DateTime(2026, 7, 2),
            SubmissionMode = InventoryTransportSubmissionMode.Draft,
            Sources =
            [
                new() { SourceInventoryMovementId = sources.First, QuantityMt = 100m },
                new() { SourceInventoryMovementId = sources.Second, QuantityMt = 50m }
            ],
            Vehicles =
            [
                new()
                {
                    TransportType = LoadingTransportType.Truck,
                    TruckId = 1,
                    DriverId = 1,
                    QuantityMt = 120m,
                    CarrierType = CarrierType.ServiceProvider,
                    ServiceProviderId = 1,
                    Allocations =
                    [
                        new() { SourceInventoryMovementId = sources.First, QuantityMt = 100m },
                        new() { SourceInventoryMovementId = sources.Second, QuantityMt = 20m }
                    ]
                },
                new()
                {
                    TransportType = LoadingTransportType.Wagon,
                    WagonId = 1,
                    QuantityMt = 30m,
                    CarrierType = CarrierType.OperationalAsset,
                    OperationalAssetId = 1,
                    Allocations =
                    [
                        new() { SourceInventoryMovementId = sources.Second, QuantityMt = 30m }
                    ]
                }
            ]
        };

    // یک خروجیِ مستقیم روی همان قرارداد/مخزن؛ «قابل حملِ» منبعِ همان قرارداد را پایین می‌آورد.
    private static async Task<InventoryMovement> AddOutboundAsync(
        ApplicationDbContext db,
        int contractId,
        decimal quantityMt)
    {
        var movement = new InventoryMovement
        {
            TerminalId = 1,
            StorageTankId = 1,
            ProductId = 1,
            ContractId = contractId,
            Direction = MovementDirection.Out,
            MovementDate = new DateTime(2026, 7, 1),
            QuantityMt = quantityMt,
            ReferenceDocument = "OUT-1"
        };
        db.InventoryMovements.Add(movement);
        await db.SaveChangesAsync();
        return movement;
    }

    // همان دو وسیله، ولی با توزیعی که وقتی منبع اول فقط ۶۰ MT قابل حمل دارد FIFO است.
    private static InventoryTransportFromInventoryViewModel BuildSplitDraftModel((int First, int Second) sources)
    {
        var model = BuildDraftModel(sources);
        model.Sources =
        [
            new() { SourceInventoryMovementId = sources.First, QuantityMt = 60m },
            new() { SourceInventoryMovementId = sources.Second, QuantityMt = 90m }
        ];
        model.Vehicles[0].Allocations =
        [
            new() { SourceInventoryMovementId = sources.First, QuantityMt = 60m },
            new() { SourceInventoryMovementId = sources.Second, QuantityMt = 60m }
        ];
        model.Vehicles[1].Allocations =
        [
            new() { SourceInventoryMovementId = sources.Second, QuantityMt = 30m }
        ];
        return model;
    }

    private static InventoryTransportBatchService BuildService(ApplicationDbContext db)
        => new(db, new StockService(db), new FormTokenGuard(db));

    private static async Task<(int First, int Second)> SeedAsync(ApplicationDbContext db)
    {
        db.Products.Add(new Product { Id = 1, Code = "GO", Name = "Gas Oil", IsActive = true });
        db.Terminals.Add(new Terminal { Id = 1, Code = "T1", Name = "Terminal 1", IsActive = true });
        db.StorageTanks.Add(new StorageTank { Id = 1, TerminalId = 1, TankCode = "TK-1", ProductId = 1, CapacityMt = 1000m, IsActive = true });
        db.Companies.Add(new Company { Id = 1, Code = "PTG", Name = "PTG", IsActive = true });
        db.Suppliers.Add(new Supplier { Id = 1, Name = "Supplier", IsActive = true });
        db.Contracts.AddRange(
            new Contract { Id = 1, ContractNumber = "PUR-1", ContractType = ContractType.Purchase, CompanyId = 1, SupplierId = 1, ProductId = 1, ContractDate = new DateTime(2026, 6, 1), QuantityMt = 200m, PricingMethod = PricingMethod.Fixed },
            new Contract { Id = 2, ContractNumber = "PUR-2", ContractType = ContractType.Purchase, CompanyId = 1, SupplierId = 1, ProductId = 1, ContractDate = new DateTime(2026, 6, 1), QuantityMt = 200m, PricingMethod = PricingMethod.Fixed });
        db.Trucks.Add(new Truck { Id = 1, PlateNumber = "TR-1", MaxLoadMt = 120m, IsActive = true });
        db.Wagons.Add(new Wagon { Id = 1, WagonNumber = "WG-1", CapacityMt = 60m, IsActive = true });
        db.Drivers.Add(new Driver { Id = 1, FullName = "Driver 1", IsActive = true });
        db.ServiceProviders.Add(new ServiceProvider { Id = 1, Name = "Carrier", ProviderType = ServiceProviderType.TransportCompany, IsActive = true });
        db.OperationalAssets.Add(new OperationalAsset { Id = 1, AssetCode = "WA-1", Name = "Company Wagon", AssetType = OperationalAssetType.Wagon, CapacityMt = 60m, IsActive = true });
        db.Currencies.Add(new Currency { Id = 1, Code = "USD", Name = "US Dollar", IsActive = true });
        await db.SaveChangesAsync();

        var first = new InventoryMovement
        {
            TerminalId = 1,
            StorageTankId = 1,
            ProductId = 1,
            ContractId = 1,
            Direction = MovementDirection.In,
            MovementDate = new DateTime(2026, 7, 1),
            QuantityMt = 100m,
            ReferenceDocument = "REC-1"
        };
        var second = new InventoryMovement
        {
            TerminalId = 1,
            StorageTankId = 1,
            ProductId = 1,
            ContractId = 2,
            Direction = MovementDirection.In,
            MovementDate = new DateTime(2026, 7, 1),
            QuantityMt = 100m,
            ReferenceDocument = "REC-2"
        };
        db.InventoryMovements.AddRange(first, second);
        await db.SaveChangesAsync();
        return (first.Id, second.Id);
    }

    private static ApplicationDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ApplicationDbContext(options);
    }
}
