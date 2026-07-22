using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.InventoryTransport;
using PTGOilSystem.Web.Services;
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
        Assert.Contains(batch.Legs, l => l.TruckId == 1 && l.QuantityMt == 100m && l.Allocations.Count == 2);
        Assert.Contains(batch.Legs, l => l.WagonId == 1 && l.QuantityMt == 50m && l.Allocations.Count == 1);
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
        Assert.Equal(40m, await new StockService(db).GetFreeQuantityMtAsync(1, 1, 1, storageTankId: 1));
        Assert.Equal(10m, await new StockService(db).GetFreeQuantityMtAsync(1, 1, 2, storageTankId: 1));
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
            new() { SourceInventoryMovementId = sources.First, QuantityMt = 60m },
            new() { SourceInventoryMovementId = sources.Second, QuantityMt = 90m }
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
            Sources =
            [
                new() { SourceInventoryMovementId = sources.First, QuantityMt = 60m },
                new() { SourceInventoryMovementId = sources.Second, QuantityMt = 90m }
            ],
            Vehicles =
            [
                new()
                {
                    TransportType = LoadingTransportType.Truck,
                    TruckId = 1,
                    DriverId = 1,
                    QuantityMt = 100m,
                    CarrierType = CarrierType.ServiceProvider,
                    ServiceProviderId = 1,
                    FreightAmount = 500m,
                    FreightCurrencyId = 1,
                    Allocations =
                    [
                        new() { SourceInventoryMovementId = sources.First, QuantityMt = 60m },
                        new() { SourceInventoryMovementId = sources.Second, QuantityMt = 40m }
                    ]
                },
                new()
                {
                    TransportType = LoadingTransportType.Wagon,
                    WagonId = 1,
                    QuantityMt = 50m,
                    CarrierType = CarrierType.OperationalAsset,
                    OperationalAssetId = 1,
                    Allocations =
                    [
                        new() { SourceInventoryMovementId = sources.Second, QuantityMt = 50m }
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
