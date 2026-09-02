using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.InventoryTransport;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Accounting;
using PTGOilSystem.Web.Services.Exceptions;
using Xunit;

namespace PTGOilSystem.Web.Tests;

public class InventoryTransportBatchServiceTests
{
    [Fact]
    public async Task Draft_Creates_One_Leg_Per_Vehicle_And_No_Stock_Or_Finance()
    {
        await using var db = CreateDb();
        var sourceIds = await SeedAsync(db);
        var service = BuildService(db);
        var model = BuildValidModel(sourceIds, InventoryTransportSubmissionMode.Draft);

        var batch = await service.CreateAsync(model, "draft-token");

        Assert.Equal(InventoryTransportBatchStatus.Draft, batch.Status);
        Assert.Equal(150m, batch.TotalQuantityMt);
        Assert.Equal(2, batch.Legs.Count);
        Assert.Contains(batch.Legs, l => l.TruckId == 1 && l.QuantityMt == 120m && l.Allocations.Count == 2);
        Assert.Contains(batch.Legs, l => l.WagonId == 1 && l.QuantityMt == 30m && l.Allocations.Count == 1);
        Assert.All(batch.Legs, l => Assert.Equal(InventoryTransportLegStatus.Draft, l.Status));
        Assert.Empty(await db.InventoryMovements.Where(m => m.Direction == MovementDirection.Out).ToListAsync());
        Assert.Empty(await db.ExpenseTransactions.ToListAsync());
        Assert.Empty(await db.LedgerEntries.ToListAsync());
        Assert.Empty(await db.PaymentTransactions.ToListAsync());
        Assert.Single(await db.ProcessedFormTokens.Where(t => t.Token == "draft-token").ToListAsync());
    }

    [Fact]
    public async Task Loaded_Creates_One_Outbound_Movement_Per_Allocation()
    {
        await using var db = CreateDb();
        var sourceIds = await SeedAsync(db);
        var service = BuildService(db);

        var batch = await service.CreateAsync(
            BuildValidModel(sourceIds, InventoryTransportSubmissionMode.Loaded),
            "loaded-token");

        var allocations = await db.InventoryTransportLegAllocations
            .Include(a => a.OutboundInventoryMovement)
            .ToListAsync();
        Assert.Equal(3, allocations.Count);
        Assert.All(allocations, a => Assert.NotNull(a.OutboundInventoryMovementId));
        Assert.Equal(150m, allocations.Sum(a => a.OutboundInventoryMovement!.QuantityMt));
        Assert.All(batch.Legs, l => Assert.Equal(InventoryTransportLegStatus.Loaded, l.Status));
        Assert.Equal(InventoryTransportBatchStatus.Loaded, batch.Status);
        Assert.Equal(0m, await new StockService(db).GetFreeQuantityMtAsync(1, 1, 1, storageTankId: 1));
        Assert.Equal(50m, await new StockService(db).GetFreeQuantityMtAsync(1, 1, 2, storageTankId: 1));
        Assert.Empty(await db.ExpenseTransactions.ToListAsync());
        Assert.Empty(await db.LedgerEntries.ToListAsync());
    }

    [Fact]
    public async Task Create_Rejects_Quantity_Above_Vehicle_Capacity()
    {
        await using var db = CreateDb();
        var sourceIds = await SeedAsync(db);
        var model = BuildValidModel(sourceIds, InventoryTransportSubmissionMode.Draft);
        model.Vehicles[0].QuantityMt = 130m;
        model.Vehicles[0].Allocations[0].QuantityMt = 60m;
        model.Vehicles[0].Allocations[1].QuantityMt = 70m;
        model.Vehicles[1].QuantityMt = 20m;
        model.Vehicles[1].Allocations[0].QuantityMt = 20m;

        var error = await Assert.ThrowsAsync<BusinessRuleException>(() => BuildService(db).CreateAsync(model, null));

        Assert.Equal("INVENTORY_TRANSPORT_CAPACITY_EXCEEDED", error.Code);
        Assert.Empty(await db.InventoryTransportBatches.ToListAsync());
    }

    [Fact]
    public async Task Draft_Allows_Standalone_Operational_Asset_With_Document_Capacity()
    {
        await using var db = CreateDb();
        var sourceIds = await SeedAsync(db);
        db.OperationalAssets.Add(new OperationalAsset
        {
            Id = 2,
            AssetCode = "AS-TRUCK",
            Name = "Company Truck",
            AssetType = OperationalAssetType.Truck,
            IsActive = true
        });
        await db.SaveChangesAsync();
        var model = BuildStandaloneAssetModel(sourceIds, capacityMt: 160m);

        var batch = await BuildService(db).CreateAsync(model, null);

        var leg = Assert.Single(batch.Legs);
        Assert.Null(leg.TruckId);
        Assert.Equal(2, leg.OperationalAssetId);
        Assert.Equal(160m, leg.CapacityMt);
        Assert.Equal("AS-TRUCK", leg.WagonNumber);
        Assert.Equal(150m, leg.QuantityMt);
    }

    [Fact]
    public async Task Create_Uses_Operational_Asset_Master_Capacity_Before_Document_Fallback()
    {
        await using var db = CreateDb();
        var sourceIds = await SeedAsync(db);
        db.OperationalAssets.Add(new OperationalAsset
        {
            Id = 2,
            AssetCode = "AS-TRUCK",
            Name = "Company Truck",
            AssetType = OperationalAssetType.Truck,
            CapacityMt = 120m,
            IsActive = true
        });
        await db.SaveChangesAsync();
        var model = BuildStandaloneAssetModel(sourceIds, capacityMt: 200m);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(() => BuildService(db).CreateAsync(model, null));

        Assert.Equal("INVENTORY_TRANSPORT_CAPACITY_EXCEEDED", error.Code);
        Assert.Empty(await db.InventoryTransportBatches.ToListAsync());
    }

    [Fact]
    public async Task Create_Allows_Standalone_Operational_Asset_Without_Any_Capacity()
    {
        await using var db = CreateDb();
        var sourceIds = await SeedAsync(db);
        db.OperationalAssets.Add(new OperationalAsset
        {
            Id = 2,
            AssetCode = "AS-TRUCK",
            Name = "Company Truck",
            AssetType = OperationalAssetType.Truck,
            IsActive = true
        });
        await db.SaveChangesAsync();
        var model = BuildStandaloneAssetModel(sourceIds, capacityMt: null);

        // Capacity is optional: a missing/unknown capacity no longer blocks creation.
        var batch = await BuildService(db).CreateAsync(model, null);

        var leg = Assert.Single(batch.Legs);
        Assert.Equal(2, leg.OperationalAssetId);
        Assert.Equal(150m, leg.QuantityMt);
        Assert.Single(await db.InventoryTransportBatches.ToListAsync());
    }

    [Fact]
    public async Task Create_Rejects_Mixed_Carrier_Identifiers()
    {
        await using var db = CreateDb();
        var sourceIds = await SeedAsync(db);
        var model = BuildValidModel(sourceIds, InventoryTransportSubmissionMode.Draft);
        model.Vehicles[0].OperationalAssetId = 1;

        var error = await Assert.ThrowsAsync<BusinessRuleException>(() => BuildService(db).CreateAsync(model, null));

        Assert.Equal("INVENTORY_TRANSPORT_PROVIDER_INVALID", error.Code);
    }

    [Fact]
    public async Task Loading_Draft_Recalculates_Server_Stock_And_Rejects_Consumed_Source()
    {
        await using var db = CreateDb();
        var sourceIds = await SeedAsync(db);
        var service = BuildService(db);
        var batch = await service.CreateAsync(BuildValidModel(sourceIds, InventoryTransportSubmissionMode.Draft), null);
        db.InventoryMovements.Add(new InventoryMovement
        {
            TerminalId = 1,
            StorageTankId = 1,
            ProductId = 1,
            ContractId = 1,
            Direction = MovementDirection.Out,
            MovementDate = new DateTime(2026, 7, 2),
            QuantityMt = 60m,
            ReferenceDocument = "OTHER-OUT"
        });
        await db.SaveChangesAsync();

        var error = await Assert.ThrowsAsync<BusinessRuleException>(() => service.LoadDraftAsync(batch.Id));

        Assert.Equal("INVENTORY_TRANSPORT_SOURCE_OVERDRAW", error.Code);
        Assert.Empty(await db.InventoryMovements.Where(m => m.ReferenceDocument != null && m.ReferenceDocument.StartsWith("TRANSPORT-ALLOCATION:")).ToListAsync());
        Assert.Equal(InventoryTransportBatchStatus.Draft, (await db.InventoryTransportBatches.FindAsync(batch.Id))!.Status);
    }

    [Fact]
    public async Task Duplicate_Form_Token_Does_Not_Create_Second_Batch()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new ApplicationDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var sourceIds = await SeedAsync(db);
        var service = new InventoryTransportBatchService(db, new FixedStockService(), new FormTokenGuard(db));
        await service.CreateAsync(BuildValidModel(sourceIds, InventoryTransportSubmissionMode.Draft), "same-token");
        db.ChangeTracker.Clear();

        var error = await Assert.ThrowsAsync<BusinessRuleException>(() =>
            service.CreateAsync(BuildValidModel(sourceIds, InventoryTransportSubmissionMode.Draft), "same-token"));

        Assert.Equal("INVENTORY_TRANSPORT_DUPLICATE_SUBMIT", error.Code);
        Assert.Single(await db.InventoryTransportBatches.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task UpdateDraft_Rebuilds_Legs_And_Keeps_Group_Key()
    {
        await using var db = CreateDb();
        var sourceIds = await SeedAsync(db);
        var service = BuildService(db);
        var batch = await service.CreateAsync(BuildValidModel(sourceIds, InventoryTransportSubmissionMode.Draft), "draft-token");
        var groupKey = batch.TransportGroupKey;

        // تصحیح: کل بار روی یک موتر و مقدار کمتر.
        var edited = BuildValidModel(sourceIds, InventoryTransportSubmissionMode.Draft);
        edited.Sources = [new() { SourceInventoryMovementId = sourceIds.First, QuantityMt = 40m }];
        edited.Vehicles =
        [
            new()
            {
                TransportType = LoadingTransportType.Truck,
                TruckId = 1,
                DriverId = 1,
                QuantityMt = 40m,
                CarrierType = CarrierType.ServiceProvider,
                ServiceProviderId = 1,
                RwbNo = "RWB-FIXED",
                Allocations = [new() { SourceInventoryMovementId = sourceIds.First, QuantityMt = 40m }]
            }
        ];

        var updated = await service.UpdateDraftAsync(batch.Id, edited);

        Assert.Equal(batch.Id, updated.Id);
        Assert.Equal(groupKey, updated.TransportGroupKey);
        Assert.Equal(InventoryTransportBatchStatus.Draft, updated.Status);
        Assert.Equal(40m, updated.TotalQuantityMt);
        var legs = await db.InventoryTransportLegs.Include(l => l.Allocations).ToListAsync();
        Assert.Single(legs);
        Assert.Equal("RWB-FIXED", legs[0].RwbNo);
        Assert.Equal(40m, legs[0].QuantityMt);
        Assert.Single(legs[0].Allocations);
        Assert.Equal(groupKey, legs[0].TransportGroupKey);
        Assert.Empty(await db.InventoryMovements.Where(m => m.Direction == MovementDirection.Out).ToListAsync());
    }

    // ───── ویرایش سندِ بارگیری‌شده و لغو ─────

    [Fact]
    public async Task UpdateLoaded_Reverses_Previous_Outbound_And_Reposts()
    {
        await using var db = CreateDb();
        var sourceIds = await SeedAsync(db);
        var service = BuildService(db);
        var batch = await service.CreateAsync(BuildValidModel(sourceIds, InventoryTransportSubmissionMode.Loaded), "loaded-token");
        var groupKey = batch.TransportGroupKey;

        // اصلاح: کل بار روی یک موتر و فقط ۴۰ MT از منبع اول، دوباره در حالت بارگیری.
        var edited = BuildValidModel(sourceIds, InventoryTransportSubmissionMode.Loaded);
        edited.Sources = [new() { SourceInventoryMovementId = sourceIds.First, QuantityMt = 40m }];
        edited.Vehicles =
        [
            new()
            {
                TransportType = LoadingTransportType.Truck,
                TruckId = 1,
                DriverId = 1,
                QuantityMt = 40m,
                CarrierType = CarrierType.ServiceProvider,
                ServiceProviderId = 1,
                Allocations = [new() { SourceInventoryMovementId = sourceIds.First, QuantityMt = 40m }]
            }
        ];

        var updated = await service.UpdateDraftAsync(batch.Id, edited);

        Assert.Equal(groupKey, updated.TransportGroupKey);
        Assert.Equal(InventoryTransportBatchStatus.Loaded, updated.Status);
        Assert.Equal(40m, updated.TotalQuantityMt);

        // سه خروجیِ قبلی معکوس شده‌اند و یک خروجی تازه ثبت شده است.
        var movements = await db.InventoryMovements.AsNoTracking().ToListAsync();
        Assert.Equal(3, movements.Count(m => m.ReversalOfInventoryMovementId.HasValue));
        Assert.Equal(4, movements.Count(m => m.Direction == MovementDirection.Out));

        // خالصِ موجودی دقیقاً برابر بارِ سندِ ویرایش‌شده کم شده است، نه بیشتر.
        var stock = new StockService(db);
        Assert.Equal(60m, await stock.GetFreeQuantityMtAsync(1, 1, 1, storageTankId: 1));
        Assert.Equal(100m, await stock.GetFreeQuantityMtAsync(1, 1, 2, storageTankId: 1));
    }

    [Fact]
    public async Task Update_Rejects_Batch_That_Has_A_Receipt()
    {
        await using var db = CreateDb();
        var sourceIds = await SeedAsync(db);
        var service = BuildService(db);
        var batch = await service.CreateAsync(BuildValidModel(sourceIds, InventoryTransportSubmissionMode.Loaded), "loaded-token");
        db.InventoryTransportReceipts.Add(new InventoryTransportReceipt
        {
            InventoryTransportLegId = batch.Legs.First().Id,
            ReceiptDate = new DateTime(2026, 7, 5),
            ReceivedQuantityMt = 10m
        });
        await db.SaveChangesAsync();

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => service.UpdateDraftAsync(batch.Id, BuildValidModel(sourceIds, InventoryTransportSubmissionMode.Draft)));

        Assert.Equal("INVENTORY_TRANSPORT_BATCH_HAS_DOWNSTREAM", error.Code);
        Assert.Contains("رسید تحویل", error.Message);
    }

    [Fact]
    public async Task Cancel_Reverses_Outbound_And_Marks_Everything_Cancelled()
    {
        await using var db = CreateDb();
        var sourceIds = await SeedAsync(db);
        var service = BuildService(db);
        var batch = await service.CreateAsync(BuildValidModel(sourceIds, InventoryTransportSubmissionMode.Loaded), "loaded-token");

        var cancelled = await service.CancelAsync(batch.Id);

        Assert.Equal(InventoryTransportBatchStatus.Cancelled, cancelled.Status);
        Assert.All(cancelled.Legs, l => Assert.Equal(InventoryTransportLegStatus.Cancelled, l.Status));

        // موجودی مبدأ دقیقاً به حالت پیش از حمل برگشته است.
        var stock = new StockService(db);
        Assert.Equal(100m, await stock.GetFreeQuantityMtAsync(1, 1, 1, storageTankId: 1));
        Assert.Equal(100m, await stock.GetFreeQuantityMtAsync(1, 1, 2, storageTankId: 1));

        // برای هر خروجی یک سند معکوس ساخته شده، نه بیشتر.
        var movements = await db.InventoryMovements.AsNoTracking().ToListAsync();
        Assert.Equal(3, movements.Count(m => m.Direction == MovementDirection.Out));
        Assert.Equal(3, movements.Count(m => m.ReversalOfInventoryMovementId.HasValue));
    }

    [Fact]
    public async Task Cancel_Twice_Does_Not_Post_A_Second_Reversal()
    {
        await using var db = CreateDb();
        var sourceIds = await SeedAsync(db);
        var service = BuildService(db);
        var batch = await service.CreateAsync(BuildValidModel(sourceIds, InventoryTransportSubmissionMode.Loaded), "loaded-token");

        await service.CancelAsync(batch.Id);
        await service.CancelAsync(batch.Id);

        var movements = await db.InventoryMovements.AsNoTracking().ToListAsync();
        Assert.Equal(3, movements.Count(m => m.ReversalOfInventoryMovementId.HasValue));
        Assert.Equal(100m, await new StockService(db).GetFreeQuantityMtAsync(1, 1, 1, storageTankId: 1));
    }

    [Fact]
    public async Task Cancel_Is_Blocked_When_A_Sale_Is_Registered()
    {
        await using var db = CreateDb();
        var sourceIds = await SeedAsync(db);
        var service = BuildService(db);
        var batch = await service.CreateAsync(BuildValidModel(sourceIds, InventoryTransportSubmissionMode.Loaded), "loaded-token");
        db.SalesTransactionSourceAllocations.Add(new SalesTransactionSourceAllocation
        {
            SalesTransactionId = 1,
            TransportLegId = batch.Legs.First().Id,
            SourcePurchaseContractId = 1,
            QuantityMt = 10m
        });
        await db.SaveChangesAsync();

        var error = await Assert.ThrowsAsync<BusinessRuleException>(() => service.CancelAsync(batch.Id));

        Assert.Equal("INVENTORY_TRANSPORT_BATCH_HAS_DOWNSTREAM", error.Code);
        Assert.Contains("فروش ثبت‌شده", error.Message);

        // هیچ برگشتی ثبت نشده؛ سند دست‌نخورده مانده است.
        Assert.Empty(await db.InventoryMovements.AsNoTracking()
            .Where(m => m.ReversalOfInventoryMovementId.HasValue).ToListAsync());
        Assert.Equal(InventoryTransportBatchStatus.Loaded,
            (await db.InventoryTransportBatches.AsNoTracking().FirstAsync(b => b.Id == batch.Id)).Status);
    }

    [Fact]
    public async Task Cancel_Reverses_Expenses_Attached_To_The_Legs()
    {
        await using var db = CreateDb();
        var sourceIds = await SeedAsync(db);
        var service = BuildService(db);
        var batch = await service.CreateAsync(BuildValidModel(sourceIds, InventoryTransportSubmissionMode.Loaded), "loaded-token");
        db.ExpenseTypes.Add(new ExpenseType { Id = 1, Code = "FRT", Name = "Freight", IsActive = true });
        await db.SaveChangesAsync();
        db.ExpenseTransactions.Add(new ExpenseTransaction
        {
            ExpenseTypeId = 1,
            TransportLegId = batch.Legs.First().Id,
            ExpenseDate = new DateTime(2026, 7, 2),
            Amount = 500m,
            Currency = "USD",
            AppliedFxRateToUsd = 1m,
            AmountUsd = 500m,
            Description = "Freight"
        });
        await db.SaveChangesAsync();

        await service.CancelAsync(batch.Id);

        var expense = await db.ExpenseTransactions.AsNoTracking().SingleAsync();
        Assert.True(expense.IsCancelled);
    }

    [Fact]
    public async Task Cancelled_Batch_Cannot_Be_Edited()
    {
        await using var db = CreateDb();
        var sourceIds = await SeedAsync(db);
        var service = BuildService(db);
        var batch = await service.CreateAsync(BuildValidModel(sourceIds, InventoryTransportSubmissionMode.Loaded), "loaded-token");
        await service.CancelAsync(batch.Id);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => service.UpdateDraftAsync(batch.Id, BuildValidModel(sourceIds, InventoryTransportSubmissionMode.Draft)));

        Assert.Equal("INVENTORY_TRANSPORT_BATCH_NOT_EDITABLE", error.Code);
    }

    // ───── FIFO در سرور ─────

    [Fact]
    public async Task Fifo_Allocation_Is_Accepted()
    {
        await using var db = CreateDb();
        var sourceIds = await SeedAsync(db);

        var batch = await BuildService(db).CreateAsync(
            BuildValidModel(sourceIds, InventoryTransportSubmissionMode.Draft), null);

        Assert.Equal(2, batch.Legs.Count);
        Assert.Equal(3, batch.Legs.Sum(l => l.Allocations.Count));
    }

    [Fact]
    public async Task Non_Fifo_Allocation_With_Same_Totals_Is_Rejected()
    {
        await using var db = CreateDb();
        var sourceIds = await SeedAsync(db);
        var model = BuildValidModel(sourceIds, InventoryTransportSubmissionMode.Draft);

        // همان جمع کل، ولی ترتیب مصرف رعایت نشده: موتر اول از منبع دوم برمی‌دارد
        // در حالی که منبع اول هنوز موجودی دارد.
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
            () => BuildService(db).CreateAsync(model, null));

        Assert.Equal("INVENTORY_TRANSPORT_ALLOCATION_NOT_FIFO", error.Code);
        Assert.Empty(await db.InventoryTransportBatches.ToListAsync());
    }

    [Fact]
    public async Task Fifo_Single_Vehicle_Drawing_From_Two_Contracts_Is_Accepted()
    {
        await using var db = CreateDb();
        var sourceIds = await SeedAsync(db);
        var model = BuildValidModel(sourceIds, InventoryTransportSubmissionMode.Draft);
        model.Sources =
        [
            new() { SourceInventoryMovementId = sourceIds.First, QuantityMt = 100m },
            new() { SourceInventoryMovementId = sourceIds.Second, QuantityMt = 20m }
        ];
        model.Vehicles =
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
                    new() { SourceInventoryMovementId = sourceIds.First, QuantityMt = 100m },
                    new() { SourceInventoryMovementId = sourceIds.Second, QuantityMt = 20m }
                ]
            }
        ];

        var batch = await BuildService(db).CreateAsync(model, null);

        var leg = Assert.Single(batch.Legs);
        Assert.Equal(2, leg.Allocations.Count);
        Assert.Equal(120m, batch.TotalQuantityMt);
    }

    [Fact]
    public async Task Fifo_Check_Tolerates_Four_Decimal_Rounding()
    {
        await using var db = CreateDb();
        var sourceIds = await SeedAsync(db);
        var model = BuildValidModel(sourceIds, InventoryTransportSubmissionMode.Draft);

        // فرم مقادیر را با چهار رقم اعشار می‌فرستد؛ اختلاف در رقم آخر نباید ثبت را رد کند.
        model.Sources =
        [
            new() { SourceInventoryMovementId = sourceIds.First, QuantityMt = 100m },
            new() { SourceInventoryMovementId = sourceIds.Second, QuantityMt = 20.0001m }
        ];
        model.Vehicles[0].QuantityMt = 120.0001m;
        model.Vehicles[0].Allocations =
        [
            new() { SourceInventoryMovementId = sourceIds.First, QuantityMt = 100m },
            new() { SourceInventoryMovementId = sourceIds.Second, QuantityMt = 20.0001m }
        ];
        model.Vehicles.RemoveAt(1);

        var batch = await BuildService(db).CreateAsync(model, null);

        Assert.Single(batch.Legs);
    }

    [Fact]
    public async Task Fifo_Check_Does_Not_Mask_Source_Overdraw()
    {
        await using var db = CreateDb();
        var sourceIds = await SeedAsync(db);
        var model = BuildValidModel(sourceIds, InventoryTransportSubmissionMode.Draft);
        model.Sources =
        [
            new() { SourceInventoryMovementId = sourceIds.First, QuantityMt = 260m }
        ];
        model.Vehicles =
        [
            new()
            {
                TransportType = LoadingTransportType.Truck,
                TruckId = 1,
                DriverId = 1,
                QuantityMt = 260m,
                CapacityMt = 300m,
                CarrierType = CarrierType.ServiceProvider,
                ServiceProviderId = 1,
                Allocations = [new() { SourceInventoryMovementId = sourceIds.First, QuantityMt = 260m }]
            }
        ];

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => BuildService(db).CreateAsync(model, null));

        Assert.Equal("INVENTORY_TRANSPORT_SOURCE_OVERDRAW", error.Code);
    }

    [Fact]
    public async Task Load_Of_Existing_Draft_Is_Not_Blocked_By_Fifo_Check()
    {
        await using var db = CreateDb();
        var sourceIds = await SeedAsync(db);
        var service = BuildService(db);
        var batch = await service.CreateAsync(
            BuildValidModel(sourceIds, InventoryTransportSubmissionMode.Draft), null);

        // بارگیریِ یک پیش‌نویسِ ذخیره‌شده نباید دوباره شکلِ فرم را قضاوت کند.
        var loaded = await service.LoadDraftAsync(batch.Id);

        Assert.Equal(InventoryTransportBatchStatus.Loaded, loaded.Status);
    }

    // ───── قلاب‌های حسابداری و نسب‌نامه در مسیر Batch ─────

    [Fact]
    public async Task Batch_Load_Calls_Accounting_And_Lineage_Hooks_For_Every_Leg()
    {
        await using var db = CreateDb();
        var sourceIds = await SeedAsync(db);
        var accounting = new RecordingTransferAccounting();
        var lineage = new RecordingLineageWriter();
        var service = new InventoryTransportBatchService(
            db, new StockService(db), new FormTokenGuard(db), null, lineage, accounting);

        var batch = await service.CreateAsync(
            BuildValidModel(sourceIds, InventoryTransportSubmissionMode.Loaded), null);

        var legIds = batch.Legs.Select(l => l.Id).OrderBy(x => x).ToList();
        Assert.Equal(legIds, accounting.PostedLegIds.OrderBy(x => x).ToList());
        Assert.Equal(legIds, lineage.LoadedLegIds.OrderBy(x => x).ToList());
    }

    [Fact]
    public async Task Batch_Load_Passes_Every_Outbound_Movement_Of_A_Multi_Contract_Leg()
    {
        await using var db = CreateDb();
        var sourceIds = await SeedAsync(db);
        var lineage = new RecordingLineageWriter();
        var service = new InventoryTransportBatchService(
            db, new StockService(db), new FormTokenGuard(db), null, lineage, null);

        var batch = await service.CreateAsync(
            BuildValidModel(sourceIds, InventoryTransportSubmissionMode.Loaded), null);

        var truckLeg = batch.Legs.Single(l => l.TruckId == 1);
        var wagonLeg = batch.Legs.Single(l => l.WagonId == 1);
        // سهم‌های چندگانه به یک سند تقلیل نمی‌شوند.
        Assert.Equal(2, lineage.MovementCountByLeg[truckLeg.Id]);
        Assert.Equal(1, lineage.MovementCountByLeg[wagonLeg.Id]);
    }

    [Fact]
    public async Task Batch_Load_Without_Adapters_Behaves_Exactly_As_Before()
    {
        await using var db = CreateDb();
        var sourceIds = await SeedAsync(db);

        // ساختِ دستیِ سرویس: هیچ قلابی وصل نیست و رفتار دقیقاً مثل قبل می‌ماند.
        var batch = await BuildService(db).CreateAsync(
            BuildValidModel(sourceIds, InventoryTransportSubmissionMode.Loaded), null);

        Assert.Equal(InventoryTransportBatchStatus.Loaded, batch.Status);
        Assert.Empty(await db.InventoryLotMovements.ToListAsync());
        Assert.Empty(await db.JournalEntries.ToListAsync());
    }

    [Fact]
    public async Task Reloading_A_Loaded_Batch_Does_Not_Post_Twice()
    {
        await using var db = CreateDb();
        var sourceIds = await SeedAsync(db);
        var accounting = new RecordingTransferAccounting();
        var lineage = new RecordingLineageWriter();
        var service = new InventoryTransportBatchService(
            db, new StockService(db), new FormTokenGuard(db), null, lineage, accounting);
        var batch = await service.CreateAsync(
            BuildValidModel(sourceIds, InventoryTransportSubmissionMode.Loaded), null);
        var postedAfterCreate = accounting.PostedLegIds.Count;
        var lineageAfterCreate = lineage.LoadedLegIds.Count;

        // بارگیریِ دوباره رد می‌شود، پس هیچ قلابی دوباره اجرا نمی‌شود.
        var error = await Assert.ThrowsAsync<BusinessRuleException>(() => service.LoadDraftAsync(batch.Id));

        Assert.Equal("INVENTORY_TRANSPORT_BATCH_ALREADY_LOADED", error.Code);
        Assert.Equal(postedAfterCreate, accounting.PostedLegIds.Count);
        Assert.Equal(lineageAfterCreate, lineage.LoadedLegIds.Count);
    }

    // یک سند حمل می‌تواند منابع دو شرکت داخلی را با هم داشته باشد — حتی داخل یک وسیله.
    // سند به دو سند و وسیله به دو وسیله شکسته نمی‌شود؛ فقط مالکیت هر سهم سرِ جای خودش می‌ماند.
    [Fact]
    public async Task Multi_Company_Batch_Keeps_One_Leg_Per_Vehicle_And_Splits_Ownership()
    {
        await using var db = CreateDb();
        var sourceIds = await SeedAsync(db);
        db.Companies.Add(new Company { Id = 2, Code = "PTG-B", Name = "Company B", IsActive = true });
        (await db.Contracts.SingleAsync(c => c.Id == 2)).CompanyId = 2;
        await db.SaveChangesAsync();

        var batch = await BuildService(db).CreateAsync(
            BuildValidModel(sourceIds, InventoryTransportSubmissionMode.Loaded),
            "multi-company-token");

        // یک وسیله = یک leg، همان‌طور که قبلاً بود.
        Assert.Equal(2, batch.Legs.Count);

        var resolver = new InventoryTransportLegOwnershipResolver(db);

        // موتر ۱۲۰ تنی از هر دو شرکت پر شده: ۱۰۰ تن شرکت ۱ و ۲۰ تن شرکت ۲.
        var truckLeg = batch.Legs.Single(l => l.TruckId == 1);
        var truckSlices = await resolver.ResolveCompanyOwnershipSlicesAsync(truckLeg);
        Assert.Equal(2, truckSlices.Count);
        Assert.Equal(100m, truckSlices.Single(x => x.CompanyId == 1).QuantityMt);
        Assert.Equal(20m, truckSlices.Single(x => x.CompanyId == 2).QuantityMt);
        Assert.Equal(truckLeg.QuantityMt, truckSlices.Sum(x => x.QuantityMt));

        // واگن فقط از شرکت دوم پر شده، هرچند قرارداد سرصفحهٔ سند شرکت اول است.
        var wagonLeg = batch.Legs.Single(l => l.WagonId == 1);
        var wagonSlice = Assert.Single(await resolver.ResolveCompanyOwnershipSlicesAsync(wagonLeg));
        Assert.Equal(2, wagonSlice.CompanyId);
        Assert.Equal(30m, wagonSlice.QuantityMt);

        // خروجی فیزیکی همچنان به تفکیک قرارداد ثبت شده است.
        var outbound = await db.InventoryMovements
            .Where(m => m.Direction == MovementDirection.Out)
            .GroupBy(m => m.ContractId)
            .Select(g => new { ContractId = g.Key, QuantityMt = g.Sum(m => m.QuantityMt) })
            .ToListAsync();
        Assert.Equal(100m, outbound.Single(x => x.ContractId == 1).QuantityMt);
        Assert.Equal(50m, outbound.Single(x => x.ContractId == 2).QuantityMt);
    }

    private static InventoryTransportBatchService BuildService(ApplicationDbContext db)
        => new(db, new StockService(db), new FormTokenGuard(db));

    private static InventoryTransportFromInventoryViewModel BuildStandaloneAssetModel(
        (int First, int Second) sources,
        decimal? capacityMt)
    {
        var model = BuildValidModel(sources, InventoryTransportSubmissionMode.Draft);
        var vehicle = model.Vehicles[0];
        vehicle.TruckId = null;
        vehicle.QuantityMt = 150m;
        vehicle.CapacityMt = capacityMt;
        vehicle.CarrierType = CarrierType.OperationalAsset;
        vehicle.ServiceProviderId = null;
        vehicle.OperationalAssetId = 2;
        vehicle.Allocations =
        [
            new() { SourceInventoryMovementId = sources.First, QuantityMt = 100m },
            new() { SourceInventoryMovementId = sources.Second, QuantityMt = 50m }
        ];
        model.Vehicles = [vehicle];
        return model;
    }

    private static InventoryTransportFromInventoryViewModel BuildValidModel(
        (int First, int Second) sources,
        InventoryTransportSubmissionMode mode)
        => new()
        {
            SourceTerminalId = 1,
            SourceStorageTankId = 1,
            ProductId = 1,
            TransportDate = new DateTime(2026, 7, 2),
            SubmissionMode = mode,
            // شکل FIFO با همان بذر (هر منبع ۱۰۰ MT قابل حمل): موتر اول تا سقف منبع اول پر
            // می‌شود و بقیه از منبع دوم می‌آید، سپس واگن از باقیماندهٔ منبع دوم.
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
                    FreightAmount = 500m,
                    FreightCurrencyId = 1,
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

    private static async Task<(int First, int Second)> SeedAsync(ApplicationDbContext db)
    {
        db.Products.Add(new Product { Id = 1, Code = "GO", Name = "Gas Oil", IsActive = true });
        db.Terminals.Add(new Terminal { Id = 1, Code = "T1", Name = "Terminal 1", IsActive = true });
        db.StorageTanks.Add(new StorageTank { Id = 1, TerminalId = 1, TankCode = "TK-1", ProductId = 1, CapacityMt = 1000m, IsActive = true });
        db.Companies.Add(new Company { Id = 1, Code = "PTG", Name = "PTG", IsActive = true });
        db.Suppliers.Add(new Supplier { Id = 1, Name = "Supplier", IsActive = true });
        db.Contracts.AddRange(
            new Contract { Id = 1, ContractNumber = "PUR-1", ContractType = ContractType.Purchase, CompanyId = 1, SupplierId = 1, ProductId = 1, ContractDate = new DateTime(2026, 6, 1), QuantityMt = 100m, PricingMethod = PricingMethod.Fixed },
            new Contract { Id = 2, ContractNumber = "PUR-2", ContractType = ContractType.Purchase, CompanyId = 1, SupplierId = 1, ProductId = 1, ContractDate = new DateTime(2026, 6, 1), QuantityMt = 100m, PricingMethod = PricingMethod.Fixed });
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

    // قلاب حسابداری: فقط ثبت می‌کند برای کدام legها صدا زده شده. منطق واقعی پشت Feature Flag
    // است و در تست اجرا نمی‌شود؛ آنچه اینجا اثبات می‌شود «صدا زده شدن» است، نه سند حسابداری.
    private sealed class RecordingTransferAccounting : IInventoryTransferAccountingAdapter
    {
        public List<int> PostedLegIds { get; } = [];

        public Task<InventoryTransferAccountingResult> TryPostLegLoadAsync(
            InventoryTransportLeg leg, CancellationToken cancellationToken = default)
        {
            PostedLegIds.Add(leg.Id);
            return Task.FromResult(new InventoryTransferAccountingResult(
                PaymentPostingStatus.Skipped, null, "TEST"));
        }

        public Task<InventoryTransferAccountingResult> TryPostLegLoadReversalAsync(
            InventoryTransportLeg leg, CancellationToken cancellationToken = default)
            => Task.FromResult(new InventoryTransferAccountingResult(
                PaymentPostingStatus.Skipped, null, "TEST"));

        public Task<InventoryTransferAccountingResult> TryPostReceiptAsync(
            InventoryTransportReceipt receipt, CancellationToken cancellationToken = default)
            => Task.FromResult(new InventoryTransferAccountingResult(
                PaymentPostingStatus.Skipped, null, "TEST"));
    }

    // نسب‌نامه: ثبت می‌کند برای هر leg چند سند خروجی تحویل گرفته است.
    private sealed class RecordingLineageWriter : IInventoryLineageWriter
    {
        public List<int> LoadedLegIds { get; } = [];
        public List<int> ReversedLegIds { get; } = [];
        public Dictionary<int, int> MovementCountByLeg { get; } = [];

        public bool Enabled => true;

        public Task OnLegLoadedAsync(InventoryTransportLeg leg, InventoryMovement outboundMovement, CancellationToken ct = default)
            => OnLegLoadedAsync(leg, [outboundMovement], ct);

        public Task OnLegLoadedAsync(InventoryTransportLeg leg, IReadOnlyList<InventoryMovement> outboundMovements, CancellationToken ct = default)
        {
            LoadedLegIds.Add(leg.Id);
            MovementCountByLeg[leg.Id] = outboundMovements.Count;
            return Task.CompletedTask;
        }

        public Task OnLegLoadReversedAsync(InventoryTransportLeg leg, CancellationToken ct = default)
        {
            ReversedLegIds.Add(leg.Id);
            return Task.CompletedTask;
        }

        public Task<InventoryLot> CreateLotAsync(LotCreationRequest r, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<LotConsumptionResult> ConsumeFifoAsync(LotConsumeRequest r, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task OnLegReceiptAsync(InventoryTransportLeg leg, InventoryTransportReceipt receipt, InventoryMovement? inboundMovement, LossEvent? shortageLoss, CancellationToken ct = default)
            => Task.CompletedTask;
        public Task OnDirectSaleAsync(InventoryTransportLeg leg, SalesTransaction sale, CancellationToken ct = default)
            => Task.CompletedTask;
        public Task AllocateSaleAsync(SalesTransaction sale, int? sourcePurchaseContractId, int terminalId, int? storageTankId, CancellationToken ct = default)
            => Task.CompletedTask;
        public Task AllocateLossToLotsAsync(LossEvent loss, int? terminalId, int? storageTankId, CancellationToken ct = default)
            => Task.CompletedTask;
        public Task AllocateExpenseToShipmentLotsAsync(ExpenseTransaction expense, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private sealed class FixedStockService : IStockService
    {
        public Task<decimal> GetFreeQuantityMtAsync(int productId, int? terminalId = null, int? contractId = null, int? inventoryBatchId = null, int? storageTankId = null, DateTime? asOfUtc = null, CancellationToken ct = default)
            => Task.FromResult(100m);
        public Task<decimal> GetTotalFreeQuantityMtAsync(int? terminalId = null, DateTime? asOfUtc = null, CancellationToken ct = default)
            => Task.FromResult(200m);
        public Task<IReadOnlyList<TankStockItem>> GetTankAvailabilityAsync(int productId, int contractId, DateTime? asOfUtc = null, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<TankStockItem>>([]);
        public Task<IReadOnlyList<StockSummaryItem>> GetStockSummaryAsync(int? productId = null, int? contractId = null, int? terminalId = null, DateTime? asOfUtc = null, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<StockSummaryItem>>([]);
        public Task<IReadOnlyList<StockCardItem>> GetStockCardAsync(int? productId = null, int? contractId = null, int? terminalId = null, int? storageTankId = null, DateTime? fromUtc = null, DateTime? toUtc = null, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<StockCardItem>>([]);
        public Task<IReadOnlyList<StockMovementSummaryItem>> GetMovementSummaryAsync(int? productId = null, int? contractId = null, int? terminalId = null, int? storageTankId = null, DateTime? fromUtc = null, DateTime? toUtc = null, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<StockMovementSummaryItem>>([]);
        public Task AcquireStockMutationLockAsync(InventoryMovement movement, CancellationToken ct = default)
            => Task.CompletedTask;
        public Task EnsureSufficientStockForMovementAsync(InventoryMovement movement, CancellationToken ct = default)
            => Task.CompletedTask;
        public Task EnsureMovementDoesNotCauseFutureNegativeStockAsync(InventoryMovement movement, CancellationToken ct = default)
            => Task.CompletedTask;
        public Task EnsureSufficientStockForSaleAsync(SalesTransaction sale, int? sourcePurchaseContractId, CancellationToken ct = default)
            => Task.CompletedTask;

#pragma warning disable CS0618
        public Task EnsureSufficientStockForSaleAsync(SalesTransaction sale, CancellationToken ct = default)
            => Task.CompletedTask;
#pragma warning restore CS0618
    }
}
