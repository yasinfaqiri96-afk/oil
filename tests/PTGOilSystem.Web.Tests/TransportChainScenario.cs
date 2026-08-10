using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services;

namespace PTGOilSystem.Web.Tests;

// ساخت صحنهٔ مشترک تست‌های زنجیرهٔ حمل.
internal static class TransportChainScenario
{
    public static ApplicationDbContext BuildDb()
        => new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    public static TransportChainService BuildChainService(ApplicationDbContext db)
        => new(
            db,
            new InventoryTransportReceiptService(db, new CurrencyConversionService(new PricingService(db))),
            new TransportQuantityService(db));

    public static Task<ContinueToVehicleResult> Continue(
        ApplicationDbContext db,
        int sourceLegId,
        LoadingTransportType targetType,
        decimal quantityMt,
        DateTime? transferDate = null)
        => ContinueFrom(db, [new ContinueToVehicleSource(sourceLegId, quantityMt)], targetType, transferDate);

    public static Task<ContinueToVehicleResult> ContinueFrom(
        ApplicationDbContext db,
        IReadOnlyList<ContinueToVehicleSource> sources,
        LoadingTransportType targetType,
        DateTime? transferDate = null)
        => BuildChainService(db).ContinueToVehicleAsync(new ContinueToVehicleCommand
        {
            Sources = sources,
            TargetTransportType = targetType,
            TargetTruckId = targetType == LoadingTransportType.Truck ? 1 : null,
            TargetWagonId = targetType == LoadingTransportType.Wagon ? 2 : null,
            TargetVesselId = targetType == LoadingTransportType.Vessel ? 1 : null,
            TransferDate = transferDate ?? new DateTime(2026, 5, 10)
        });

    public static Task<decimal> RemainingAsync(ApplicationDbContext db, int legId)
        => new TransportQuantityService(db).GetRemainingMtAsync(legId);

    public static Task<decimal> TankBalanceAsync(ApplicationDbContext db, int storageTankId)
        => db.InventoryMovements
            .Where(m => m.StorageTankId == storageTankId)
            .SumAsync(m => m.Direction == MovementDirection.In || m.Direction == MovementDirection.Adjustment
                ? m.QuantityMt
                : -m.QuantityMt);

    public static async Task SeedAsync(ApplicationDbContext db)
    {
        db.Companies.Add(new Company { Id = 1, Code = "PTG", Name = "PTG", IsActive = true });
        db.Customers.Add(new Customer { Id = 1, Name = "Customer A", IsActive = true });
        db.Currencies.Add(new Currency { Id = 1, Code = "USD", Name = "US Dollar", IsActive = true });
        db.Products.Add(new Product { Id = 1, Code = "GO", Name = "Gas Oil", IsActive = true });
        db.Terminals.AddRange(
            new Terminal { Id = 1, Code = "T1", Name = "Terminal 1", IsActive = true },
            new Terminal { Id = 2, Code = "T2", Name = "Terminal 2", IsActive = true },
            new Terminal { Id = 3, Code = "T3", Name = "Terminal 3", IsActive = true });
        db.StorageTanks.AddRange(
            new StorageTank { Id = 1, TankCode = "A", TerminalId = 1, ProductId = 1, IsActive = true },
            new StorageTank { Id = 2, TankCode = "B", TerminalId = 2, ProductId = 1, IsActive = true },
            new StorageTank { Id = 3, TankCode = "C", TerminalId = 3, ProductId = 1, IsActive = true });
        db.Trucks.AddRange(
            new Truck { Id = 1, PlateNumber = "T-1", IsActive = true },
            new Truck { Id = 2, PlateNumber = "T-2", IsActive = true });
        db.Wagons.AddRange(
            new Wagon { Id = 1, WagonNumber = "W-1", IsActive = true },
            new Wagon { Id = 2, WagonNumber = "W-2", IsActive = true });
        db.Vessels.Add(new Vessel { Id = 1, Name = "V-1", IsActive = true });
        db.Contracts.AddRange(
            new Contract { Id = 1, ContractNumber = "CTR-A", ContractType = ContractType.Purchase, ProductId = 1, ContractDate = new DateTime(2026, 4, 1), QuantityMt = 1000m },
            new Contract { Id = 2, ContractNumber = "CTR-B", ContractType = ContractType.Purchase, ProductId = 1, ContractDate = new DateTime(2026, 4, 1), QuantityMt = 1000m });
        await db.SaveChangesAsync();
    }

    public static async Task SeedLegAsync(
        ApplicationDbContext db,
        int legId,
        LoadingTransportType transportType,
        decimal quantityMt,
        int contractId,
        string? groupKey = null,
        int sourceTerminalId = 1,
        int? sourceStorageTankId = 1)
    {
        db.InventoryTransportLegs.Add(new InventoryTransportLeg
        {
            Id = legId,
            SourcePurchaseContractId = contractId,
            ProductId = 1,
            SourceTerminalId = sourceTerminalId,
            SourceStorageTankId = sourceStorageTankId,
            TransportGroupKey = groupKey,
            TransportType = transportType,
            TruckId = transportType == LoadingTransportType.Truck ? 1 : null,
            WagonId = transportType == LoadingTransportType.Wagon ? 1 : null,
            LoadedDate = new DateTime(2026, 5, 1),
            QuantityMt = quantityMt,
            Status = InventoryTransportLegStatus.Loaded
        });
        await db.SaveChangesAsync();
    }

    // یک سهم منبعِ مخزنی برای مرحله: ورودی خرید + خروجی حمل + رکورد تخصیص.
    // seedPurchaseIn=false وقتی مخزن از قبل موجودی دارد؛ در غیر این صورت ورودی خرید
    // با موجودی افتتاحیه جمع می‌شود و تراز مخزن دوبار شمرده می‌شود.
    public static async Task AddSourceAllocationAsync(
        ApplicationDbContext db,
        int legId,
        int contractId,
        decimal quantityMt,
        int storageTankId = 1,
        int terminalId = 1,
        bool seedPurchaseIn = true)
    {
        var purchaseIn = seedPurchaseIn
            ? new InventoryMovement
            {
                ProductId = 1,
                ContractId = contractId,
                TerminalId = terminalId,
                StorageTankId = storageTankId,
                Direction = MovementDirection.In,
                MovementDate = new DateTime(2026, 4, 20),
                QuantityMt = quantityMt,
                ReferenceDocument = $"SEED-IN:{legId}:{contractId}"
            }
            : await db.InventoryMovements
                .Where(m => m.StorageTankId == storageTankId && m.Direction == MovementDirection.In)
                .OrderBy(m => m.Id)
                .FirstAsync();

        var transportOut = new InventoryMovement
        {
            ProductId = 1,
            ContractId = contractId,
            TerminalId = terminalId,
            StorageTankId = storageTankId,
            Direction = MovementDirection.Out,
            MovementDate = new DateTime(2026, 5, 1),
            QuantityMt = quantityMt,
            ReferenceDocument = $"TRANSPORT-LEG:{legId}:{contractId}"
        };
        if (seedPurchaseIn)
        {
            db.InventoryMovements.Add(purchaseIn);
        }

        db.InventoryMovements.Add(transportOut);
        await db.SaveChangesAsync();

        db.InventoryTransportLegAllocations.Add(new InventoryTransportLegAllocation
        {
            InventoryTransportLegId = legId,
            SourcePurchaseContractId = contractId,
            SourceInventoryMovementId = purchaseIn.Id,
            OutboundInventoryMovementId = transportOut.Id,
            QuantityMt = quantityMt
        });
        await db.SaveChangesAsync();
    }
}
