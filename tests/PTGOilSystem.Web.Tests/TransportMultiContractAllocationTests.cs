using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.InventoryTransport;
using PTGOilSystem.Web.Models.LossEvents;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Audit;
using PTGOilSystem.Web.Services.Exceptions;
using Xunit;

namespace PTGOilSystem.Web.Tests;

// یک حمل می‌تواند از چند قرارداد خرید تغذیه شود. خروجی مبدأ از اول به‌ازای هر Allocation
// جدا ثبت می‌شد، ولی ورودی مقصد کل مقدار را به قرارداد سرصفحهٔ حمل می‌بست. نتیجه:
// قرارداد اول موجودی مصنوعی می‌گرفت و قرارداد دوم موجودی منفی. این تست‌ها همان تناسب را قفل می‌کنند.
public class TransportMultiContractAllocationTests
{
    [Fact]
    public async Task Receipt_Splits_The_Destination_Inventory_By_The_Source_Contract_Shares()
    {
        await using var db = BuildDb();
        await SeedMultiContractLegAsync(db, contractAMt: 100m, contractBMt: 200m);

        var receipt = await ApplyReceiptAsync(db, receivedQuantityMt: 300m);

        var inbound = await InboundMovementsAsync(db, receipt.Id);
        Assert.Equal(2, inbound.Count);
        Assert.Equal(200m, inbound.Single(m => m.ContractId == 2).QuantityMt);
        Assert.Equal(100m, inbound.Single(m => m.ContractId == 1).QuantityMt);

        // موجودی هر قرارداد در مخزن مقصد دقیقاً برابر همان چیزی است که از مبدأ خارج شده.
        Assert.Equal(100m, await ContractBalanceAsync(db, contractId: 1, storageTankId: 2));
        Assert.Equal(200m, await ContractBalanceAsync(db, contractId: 2, storageTankId: 2));

        // مخزن مبدأ برای هر دو قرارداد صفر شده است؛ هیچ موجودی مصنوعی ساخته نشد.
        Assert.Equal(0m, await ContractBalanceAsync(db, contractId: 1, storageTankId: 1));
        Assert.Equal(0m, await ContractBalanceAsync(db, contractId: 2, storageTankId: 1));
    }

    [Fact]
    public async Task Partial_Receipts_Keep_The_Contract_Ratio_And_Sum_To_The_Received_Quantity()
    {
        await using var db = BuildDb();
        await SeedMultiContractLegAsync(db, contractAMt: 100m, contractBMt: 200m);

        var first = await ApplyReceiptAsync(db, receivedQuantityMt: 120m);
        var second = await ApplyReceiptAsync(db, receivedQuantityMt: 180m);

        Assert.Equal(120m, (await InboundMovementsAsync(db, first.Id)).Sum(m => m.QuantityMt));
        Assert.Equal(180m, (await InboundMovementsAsync(db, second.Id)).Sum(m => m.QuantityMt));

        Assert.Equal(100m, await ContractBalanceAsync(db, contractId: 1, storageTankId: 2));
        Assert.Equal(200m, await ContractBalanceAsync(db, contractId: 2, storageTankId: 2));
    }

    // مقدارهایی که تقسیم دقیق نمی‌شوند نباید تن گم یا اضافه کنند.
    [Fact]
    public async Task Rounding_Never_Loses_Or_Invents_Tonnage()
    {
        await using var db = BuildDb();
        await SeedMultiContractLegAsync(db, contractAMt: 100m, contractBMt: 200m);

        var receipt = await ApplyReceiptAsync(db, receivedQuantityMt: 100.0001m);

        Assert.Equal(100.0001m, (await InboundMovementsAsync(db, receipt.Id)).Sum(m => m.QuantityMt));
    }

    // حمل تک‌قراردادی باید دقیقاً رفتار قبلی را نگه دارد: یک سند ورودی.
    [Fact]
    public async Task Single_Contract_Leg_Still_Posts_Exactly_One_Inbound_Movement()
    {
        await using var db = BuildDb();
        await SeedMultiContractLegAsync(db, contractAMt: 300m, contractBMt: 0m);

        var receipt = await ApplyReceiptAsync(db, receivedQuantityMt: 300m);

        var inbound = await InboundMovementsAsync(db, receipt.Id);
        Assert.Single(inbound);
        Assert.Equal(1, inbound[0].ContractId);
        Assert.Equal(receipt.InventoryMovementId, inbound[0].Id);
    }

    [Fact]
    public async Task One_Sale_Keeps_One_Invoice_And_Splits_Source_Quantity_And_Revenue()
    {
        await using var db = BuildDb();
        await SeedMultiContractLegAsync(db, contractAMt: 100m, contractBMt: 200m);

        var service = new InventoryTransportReceiptService(db, new CurrencyConversionService(new PricingService(db)));
        var leg = await service.LoadLegAsync(1, tracking: true);
        var saleDate = new DateTime(2026, 5, 3);
        var receipt = await service.ApplyAsync(
            new InventoryTransportReceiptCreateViewModel
            {
                InventoryTransportLegId = 1,
                ReceiptDate = saleDate,
                ReceivedQuantityMt = 150m,
                ShortageQuantityMt = 0m,
                ReceiptDestination = InventoryTransportReceiptDestination.DirectSale,
                SaleCustomerId = 1,
                SaleInvoiceNumber = "INV-MULTI-001",
                SaleDate = saleDate,
                SaleCurrency = "USD",
                SaleUnitPriceInCurrency = 100m
            },
            leg!,
            new CurrencyConversionResult("USD", "USD", 1m, saleDate, false, false, "Base currency"));

        var sale = await db.SalesTransactions.SingleAsync();
        Assert.Equal(receipt.SalesTransactionId, sale.Id);
        Assert.Equal("INV-MULTI-001", sale.InvoiceNumber);
        Assert.Equal(150m, sale.QuantityMt);
        Assert.Equal(15_000m, sale.TotalUsd);
        Assert.Null(sale.SourcePurchaseContractId);
        Assert.Null(sale.CompanyId);
        Assert.Single(await db.LedgerEntries.ToListAsync());
        Assert.Empty(await db.InventoryMovements
            .Where(m => m.SalesTransactionId == sale.Id)
            .ToListAsync());

        var allocations = await db.SalesTransactionSourceAllocations
            .OrderBy(a => a.SourcePurchaseContractId)
            .ToListAsync();
        Assert.Equal(2, allocations.Count);
        Assert.Equal(50m, allocations.Single(a => a.SourcePurchaseContractId == 1).QuantityMt);
        Assert.Equal(100m, allocations.Single(a => a.SourcePurchaseContractId == 2).QuantityMt);
        Assert.Equal(5_000m, allocations.Single(a => a.SourcePurchaseContractId == 1).AmountUsd);
        Assert.Equal(10_000m, allocations.Single(a => a.SourcePurchaseContractId == 2).AmountUsd);
        Assert.Equal(150m, allocations.Sum(a => a.QuantityMt));
        Assert.Equal(sale.TotalUsd, allocations.Sum(a => a.AmountUsd));

        var pnl = await new InventoryTransportPnlService(db).BuildForLegsAsync([1]);
        Assert.Equal(150m, pnl[1].SoldQuantityMt);
        Assert.Equal(15_000m, pnl[1].SalesUsd);
    }

    [Fact]
    public async Task One_Loss_Event_Splits_Across_The_Same_Source_Lineage()
    {
        await using var db = BuildDb();
        await SeedMultiContractLegAsync(db, contractAMt: 100m, contractBMt: 200m);
        var workflow = new LossEventWorkflowService(db, new StockService(db), new AuditService(db));

        var result = await workflow.CreateAsync(new LossEventSubmission
        {
            Stage = LossEventStage.TransitLoss,
            ProductId = 1,
            TransportLegId = 1,
            EventDate = new DateTime(2026, 5, 4),
            ExpectedQuantityMt = 300m,
            ActualQuantityMt = 270m,
            ToleranceQuantityMt = 0m,
            AffectsInventory = false,
            Reference = "LOSS-MULTI-001"
        });

        Assert.Single(await db.LossEvents.ToListAsync());
        Assert.Null(result.LossEvent.ContractId);
        Assert.Equal(1, result.LossEvent.TransportLegId);
        Assert.Null(result.InventoryMovement);

        var allocations = await db.LossEventSourceAllocations
            .OrderBy(a => a.SourcePurchaseContractId)
            .ToListAsync();
        Assert.Equal(2, allocations.Count);
        Assert.Equal(10m, allocations.Single(a => a.SourcePurchaseContractId == 1).QuantityMt);
        Assert.Equal(20m, allocations.Single(a => a.SourcePurchaseContractId == 2).QuantityMt);
        Assert.Equal(30m, allocations.Sum(a => a.QuantityMt));
    }

    [Fact]
    public async Task Freight_Settlement_Uses_The_Facade_Without_Changing_Physical_Quantity()
    {
        await using var db = BuildDb();
        await SeedMultiContractLegAsync(db, contractAMt: 100m, contractBMt: 200m);
        db.OperationalAssets.Add(new OperationalAsset
        {
            Id = 1,
            AssetCode = "VESSEL-OWN-1",
            Name = "Company Vessel",
            AssetType = OperationalAssetType.Wagon,
            IsActive = true
        });
        (await db.InventoryTransportLegs.SingleAsync(l => l.Id == 1)).OperationalAssetId = 1;
        await db.SaveChangesAsync();
        var stock = new StockService(db);
        var quantities = new TransportQuantityService(db);
        var receiptService = new InventoryTransportReceiptService(
            db,
            new CurrencyConversionService(new PricingService(db)),
            quantities: quantities);
        var workflow = new TransportWorkflowService(
            db,
            new InventoryTransportBatchService(db, stock),
            new TransportChainService(db, receiptService, quantities),
            receiptService,
            new LossEventWorkflowService(db, stock, new AuditService(db)));
        var movementCount = await db.InventoryMovements.CountAsync();

        var receipt = await workflow.SettleFreightAsync(new SettleTransportFreightCommand
        {
            TransportLegId = 1,
            SettlementDate = new DateTime(2026, 5, 5),
            FreightRateUsdPerMt = 10m,
            Notes = "Freight only"
        });

        Assert.Equal(0m, receipt.ReceivedQuantityMt);
        Assert.Equal(3_000m, receipt.FreightCostUsd);
        Assert.Equal(InventoryTransportReceiptDestination.ToInventory, receipt.ReceiptDestination);
        Assert.Equal(movementCount, await db.InventoryMovements.CountAsync());
        var leg = await db.InventoryTransportLegs.SingleAsync(l => l.Id == 1);
        Assert.True(leg.IsFreightSettled);
        Assert.Equal(new DateTime(2026, 5, 5), leg.FreightSettledDate);
        Assert.Equal(InventoryTransportLegStatus.Loaded, leg.Status);
        Assert.Equal(300m, await quantities.GetRemainingMtAsync(1));
        var assetIncome = await db.ExpenseTransactions.SingleAsync();
        Assert.Equal(3_000m, assetIncome.AmountUsd);
        Assert.Equal(1, assetIncome.OperationalAssetId);
        Assert.Contains($"TRANSPORT-RECEIPT:{receipt.Id}", assetIncome.Description);
        Assert.Empty(await db.LedgerEntries.ToListAsync());

        await Assert.ThrowsAsync<BusinessRuleException>(() => workflow.SettleFreightAsync(new SettleTransportFreightCommand
        {
            TransportLegId = 1,
            SettlementDate = new DateTime(2026, 5, 5),
            FreightCostUsd = 3_000m
        }));
        Assert.Single(await db.InventoryTransportReceipts.ToListAsync());
    }

    [Fact]
    public async Task Start_From_Direct_Receipt_Preserves_All_Source_Contracts_Without_Inventory_Movement()
    {
        await using var db = BuildDb();
        await SeedDirectReceiptAsync(db);
        var stock = new StockService(db);
        var quantities = new TransportQuantityService(db);
        var receiptService = new InventoryTransportReceiptService(
            db,
            new CurrencyConversionService(new PricingService(db)),
            quantities: quantities);
        var workflow = new TransportWorkflowService(
            db,
            new InventoryTransportBatchService(db, stock),
            new TransportChainService(db, receiptService, quantities),
            receiptService,
            new LossEventWorkflowService(db, stock, new AuditService(db)));

        var leg = await workflow.StartFromReceiptAsync(new StartTransportFromReceiptCommand
        {
            LoadingReceiptId = 1,
            QuantityMt = 100m,
            TransportType = LoadingTransportType.Vessel,
            VesselId = 1,
            TransportDate = new DateTime(2026, 5, 6),
            Reference = "BL-DIRECT-001"
        });

        Assert.Equal(LoadingTransportType.Vessel, leg.TransportType);
        Assert.Equal(1, leg.VesselId);
        Assert.Null(leg.SourceStorageTankId);
        Assert.Empty(await db.InventoryMovements.ToListAsync());

        var allocations = await db.InventoryTransportLegAllocations
            .Where(a => a.InventoryTransportLegId == leg.Id)
            .OrderBy(a => a.SourcePurchaseContractId)
            .ToListAsync();
        Assert.Equal(2, allocations.Count);
        Assert.Equal(40m, allocations.Single(a => a.SourcePurchaseContractId == 1).QuantityMt);
        Assert.Equal(60m, allocations.Single(a => a.SourcePurchaseContractId == 2).QuantityMt);
        Assert.All(allocations, allocation => Assert.Equal(1, allocation.SourceLoadingReceiptId));

        var duplicate = await Assert.ThrowsAsync<BusinessRuleException>(() =>
            workflow.StartFromReceiptAsync(new StartTransportFromReceiptCommand
            {
                LoadingReceiptId = 1,
                QuantityMt = 100m,
                TransportType = LoadingTransportType.Vessel,
                VesselId = 1,
                TransportDate = new DateTime(2026, 5, 6)
            }));
        Assert.Equal("TRANSPORT_RECEIPT_INSUFFICIENT", duplicate.Code);
        Assert.Single(await db.InventoryTransportLegs.ToListAsync());
        Assert.Empty(await db.InventoryMovements.ToListAsync());
    }

    private static ApplicationDbContext BuildDb()
        => new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static async Task<InventoryTransportReceipt> ApplyReceiptAsync(
        ApplicationDbContext db,
        decimal receivedQuantityMt)
    {
        var service = new InventoryTransportReceiptService(db, new CurrencyConversionService(new PricingService(db)));
        var leg = await service.LoadLegAsync(1, tracking: true);

        return await service.ApplyAsync(
            new InventoryTransportReceiptCreateViewModel
            {
                InventoryTransportLegId = 1,
                ReceiptDate = new DateTime(2026, 5, 2),
                ReceivedQuantityMt = receivedQuantityMt,
                ShortageQuantityMt = 0m,
                ReceiptDestination = InventoryTransportReceiptDestination.ToInventory,
                DestinationTerminalId = 2,
                DestinationStorageTankId = 2
            },
            leg!,
            saleConversion: null);
    }

    private static async Task<List<InventoryMovement>> InboundMovementsAsync(ApplicationDbContext db, int receiptId)
        => await db.InventoryMovements
            .AsNoTracking()
            .Where(m => m.ReferenceDocument == $"TRANSPORT-RECEIPT:{receiptId}")
            .ToListAsync();

    private static Task<decimal> ContractBalanceAsync(ApplicationDbContext db, int contractId, int storageTankId)
        => db.InventoryMovements
            .Where(m => m.ContractId == contractId && m.StorageTankId == storageTankId)
            .SumAsync(m => m.Direction == MovementDirection.In || m.Direction == MovementDirection.Adjustment
                ? m.QuantityMt
                : -m.QuantityMt);

    // مخزن ۱: قرارداد ۱ و ۲ بار دارند و هر دو در یک حمل واحد بار می‌شوند.
    private static async Task SeedMultiContractLegAsync(
        ApplicationDbContext db,
        decimal contractAMt,
        decimal contractBMt)
    {
        db.Products.Add(new Product { Id = 1, Code = "GO", Name = "Gas Oil", IsActive = true });
        db.Customers.Add(new Customer { Id = 1, Name = "Customer A" });
        db.Companies.AddRange(
            new Company { Id = 1, Code = "C1", Name = "Company 1", IsActive = true },
            new Company { Id = 2, Code = "C2", Name = "Company 2", IsActive = true });
        db.Terminals.AddRange(
            new Terminal { Id = 1, Code = "T1", Name = "Terminal 1", IsActive = true },
            new Terminal { Id = 2, Code = "T2", Name = "Terminal 2", IsActive = true });
        db.StorageTanks.AddRange(
            new StorageTank { Id = 1, TankCode = "A", TerminalId = 1, ProductId = 1, IsActive = true },
            new StorageTank { Id = 2, TankCode = "B", TerminalId = 2, ProductId = 1, IsActive = true });
        db.Contracts.AddRange(
            new Contract { Id = 1, ContractNumber = "CTR-A", ContractType = ContractType.Purchase, CompanyId = 1, ProductId = 1, ContractDate = new DateTime(2026, 4, 1), QuantityMt = 1000m },
            new Contract { Id = 2, ContractNumber = "CTR-B", ContractType = ContractType.Purchase, CompanyId = 2, ProductId = 1, ContractDate = new DateTime(2026, 4, 1), QuantityMt = 1000m });
        db.Wagons.Add(new Wagon { Id = 1, WagonNumber = "W-1", IsActive = true });

        var totalMt = contractAMt + contractBMt;
        db.InventoryTransportLegs.Add(new InventoryTransportLeg
        {
            Id = 1,
            SourcePurchaseContractId = 1,
            ProductId = 1,
            SourceTerminalId = 1,
            SourceStorageTankId = 1,
            TransportType = LoadingTransportType.Wagon,
            WagonId = 1,
            LoadedDate = new DateTime(2026, 5, 1),
            QuantityMt = totalMt,
            Status = InventoryTransportLegStatus.Loaded
        });
        await db.SaveChangesAsync();

        // ورودی خرید هر قرارداد در مخزن مبدأ، سپس خروجی همان قرارداد برای این حمل.
        await AddSourceAsync(db, contractId: 1, quantityMt: contractAMt);
        await AddSourceAsync(db, contractId: 2, quantityMt: contractBMt);
    }

    private static async Task AddSourceAsync(ApplicationDbContext db, int contractId, decimal quantityMt)
    {
        if (quantityMt <= 0m)
        {
            return;
        }

        var purchaseIn = new InventoryMovement
        {
            ProductId = 1,
            ContractId = contractId,
            TerminalId = 1,
            StorageTankId = 1,
            Direction = MovementDirection.In,
            MovementDate = new DateTime(2026, 4, 20),
            QuantityMt = quantityMt,
            ReferenceDocument = $"SEED-IN:{contractId}"
        };
        var transportOut = new InventoryMovement
        {
            ProductId = 1,
            ContractId = contractId,
            TerminalId = 1,
            StorageTankId = 1,
            Direction = MovementDirection.Out,
            MovementDate = new DateTime(2026, 5, 1),
            QuantityMt = quantityMt,
            ReferenceDocument = $"SEED-OUT:{contractId}"
        };
        db.InventoryMovements.AddRange(purchaseIn, transportOut);
        await db.SaveChangesAsync();

        db.InventoryTransportLegAllocations.Add(new InventoryTransportLegAllocation
        {
            InventoryTransportLegId = 1,
            SourcePurchaseContractId = contractId,
            SourceInventoryMovementId = purchaseIn.Id,
            OutboundInventoryMovementId = transportOut.Id,
            QuantityMt = quantityMt
        });
        await db.SaveChangesAsync();
    }

    private static async Task SeedDirectReceiptAsync(ApplicationDbContext db)
    {
        db.Products.Add(new Product { Id = 1, Code = "GO", Name = "Gas Oil", IsActive = true });
        db.Companies.AddRange(
            new Company { Id = 1, Code = "C1", Name = "Company 1", IsActive = true },
            new Company { Id = 2, Code = "C2", Name = "Company 2", IsActive = true });
        db.Contracts.AddRange(
            new Contract { Id = 1, ContractNumber = "CTR-A", ContractType = ContractType.Purchase, CompanyId = 1, ProductId = 1, ContractDate = new DateTime(2026, 4, 1), QuantityMt = 1_000m },
            new Contract { Id = 2, ContractNumber = "CTR-B", ContractType = ContractType.Purchase, CompanyId = 2, ProductId = 1, ContractDate = new DateTime(2026, 4, 1), QuantityMt = 1_000m });
        db.Terminals.Add(new Terminal { Id = 1, Code = "T1", Name = "Terminal 1", IsActive = true });
        db.Vessels.Add(new Vessel { Id = 1, Name = "Vessel 1", IsActive = true });
        db.LoadingRegisters.Add(new LoadingRegister
        {
            Id = 1,
            ContractId = 1,
            ProductId = 1,
            TransportType = LoadingTransportType.Vessel,
            LoadingDate = new DateTime(2026, 5, 1),
            LoadedQuantityMt = 100m
        });
        db.LoadingReceipts.Add(new LoadingReceipt
        {
            Id = 1,
            LoadingRegisterId = 1,
            ReceiptDestination = LoadingReceiptDestination.DirectDispatch,
            TerminalId = 1,
            ReceiptDate = new DateTime(2026, 5, 5),
            ReceivedQuantityMt = 100m
        });
        db.LoadingReceiptAllocations.AddRange(
            new LoadingReceiptAllocation
            {
                Id = 1,
                LoadingReceiptId = 1,
                Destination = LoadingReceiptAllocationDestination.DirectDispatchToTruck,
                Status = LoadingReceiptAllocationStatus.TraceOnly,
                QuantityMt = 40m,
                SourcePurchaseContractId = 1,
                TerminalId = 1
            },
            new LoadingReceiptAllocation
            {
                Id = 2,
                LoadingReceiptId = 1,
                Destination = LoadingReceiptAllocationDestination.DirectDispatchToTruck,
                Status = LoadingReceiptAllocationStatus.TraceOnly,
                QuantityMt = 60m,
                SourcePurchaseContractId = 2,
                TerminalId = 1
            });
        await db.SaveChangesAsync();
    }
}
