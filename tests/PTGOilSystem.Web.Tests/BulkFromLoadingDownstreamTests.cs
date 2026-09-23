using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.InventoryTransport;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Audit;
using PTGOilSystem.Web.Services.ContractClosure;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// مراحل بعدیِ حمل روی حمل‌هایی که با مسیر گروهی ساخته شده‌اند: تخلیه به مخزن، فروش در مسیر،
/// تسویهٔ کرایه و بستن قرارداد. هدف این است که ثابت شود هیچ مرحله‌ای «حملِ گروهی» را متفاوت
/// از «حملِ تکی» نمی‌بیند.
/// </summary>
public class BulkFromLoadingDownstreamTests
{
    private static readonly DateTime TransportDate = new(2026, 9, 5);
    private static readonly DateTime ReceiptDate = new(2026, 9, 8);

    [Fact]
    public async Task ToInventory_Receipt_On_A_Bulk_Created_Leg_Moves_The_Goods_Into_The_Tank()
    {
        await using var db = BuildDb();
        await SeedAsync(db, loadingCount: 2);
        var (workflow, receipts) = BuildServices(db);
        await ConvertAsync(workflow, 2);
        var leg = await receipts.LoadLegAsync(await FirstLegIdAsync(db), tracking: true);

        var receipt = await workflow.ReceiveToInventoryAsync(new InventoryTransportReceiptCreateViewModel
        {
            InventoryTransportLegId = leg!.Id,
            ReceiptDate = ReceiptDate,
            ReceivedQuantityMt = 100m,
            ShortageQuantityMt = 0m,
            ReceiptDestination = InventoryTransportReceiptDestination.ToInventory,
            DestinationTerminalId = 1,
            DestinationStorageTankId = 1
        }, leg);

        Assert.False(receipt.IsCancelled);
        Assert.Equal(InventoryTransportReceiptDestination.ToInventory, receipt.ReceiptDestination);
        Assert.Equal(0m, await new TransportQuantityService(db).GetRemainingMtAsync(leg.Id));
        // بار وارد مخزن شده: یک حرکت موجودی ورودی ساخته شده است.
        Assert.Contains(
            await db.InventoryMovements.AsNoTracking().ToListAsync(),
            m => m.Direction == MovementDirection.In && m.QuantityMt == 100m);

        // ردیابی تا مبدأ دست‌نخورده: رسید → حمل → سهم → بارگیری.
        var allocation = await db.InventoryTransportLegAllocations.AsNoTracking()
            .SingleAsync(a => a.InventoryTransportLegId == leg.Id);
        Assert.Equal(1, allocation.SourceLoadingRegisterId);
        Assert.Equal(1, allocation.SourcePurchaseContractId);
    }

    [Fact]
    public async Task DirectSale_On_A_Bulk_Created_Leg_Consumes_The_Leg_Without_Entering_A_Tank()
    {
        await using var db = BuildDb();
        await SeedAsync(db, loadingCount: 1);
        db.Customers.Add(new Customer { Id = 1, Name = "Customer" });
        await db.SaveChangesAsync();
        var (workflow, receipts) = BuildServices(db);
        await ConvertAsync(workflow, 1);
        var leg = await receipts.LoadLegAsync(await FirstLegIdAsync(db), tracking: true);

        var receipt = await workflow.SellQuantityAsync(
            new InventoryTransportReceiptCreateViewModel
            {
                InventoryTransportLegId = leg!.Id,
                ReceiptDate = ReceiptDate,
                ReceivedQuantityMt = 100m,
                ShortageQuantityMt = 0m,
                ReceiptDestination = InventoryTransportReceiptDestination.DirectSale,
                SaleCustomerId = 1,
                SaleInvoiceNumber = "INV-BULK-1",
                SaleDate = ReceiptDate,
                SaleCurrency = "USD",
                SaleUnitPriceInCurrency = 700m
            },
            leg,
            new CurrencyConversionResult(
                SourceCurrencyCode: "USD",
                BaseCurrencyCode: SystemCurrency.BaseCurrencyCode,
                AppliedRateToBase: 1m,
                EffectiveDate: ReceiptDate,
                FallbackApplied: false,
                ManualOverride: false,
                SourceDescription: "test"));

        Assert.Equal(InventoryTransportReceiptDestination.DirectSale, receipt.ReceiptDestination);
        Assert.NotNull(receipt.SalesTransactionId);
        var quantities = await new TransportQuantityService(db).GetQuantitiesAsync(leg.Id);
        Assert.Equal(100m, quantities.SoldMt);
        Assert.Equal(0m, quantities.RemainingMt);
        Assert.True(quantities.IsBalanced);
        // فروش در مسیر وارد مخزن نمی‌شود.
        Assert.DoesNotContain(
            await db.InventoryMovements.AsNoTracking().ToListAsync(),
            m => m.Direction == MovementDirection.In);
    }

    [Fact]
    public async Task SettlementOnly_Freight_On_A_Bulk_Created_Leg_Leaves_The_Cargo_For_Later()
    {
        await using var db = BuildDb();
        await SeedAsync(db, loadingCount: 1);
        db.ServiceProviders.Add(new ServiceProvider { Id = 1, Name = "Carrier", IsActive = true });
        await db.SaveChangesAsync();
        var (workflow, _) = BuildServices(db);
        await ConvertAsync(workflow, 1, serviceProviderId: 1);
        var legId = await FirstLegIdAsync(db);

        var receipt = await workflow.SettleFreightAsync(new SettleTransportFreightCommand
        {
            TransportLegId = legId,
            SettlementDate = ReceiptDate,
            FreightRateUsdPerMt = 12m
        });

        var leg = await db.InventoryTransportLegs.AsNoTracking().SingleAsync(l => l.Id == legId);
        Assert.True(leg.IsFreightSettled);
        Assert.Equal(ReceiptDate.Date, leg.FreightSettledDate);
        Assert.Equal(0m, receipt.ReceivedQuantityMt);
        // تسویهٔ کرایه بار را مصرف نمی‌کند؛ تخلیه مرحلهٔ جداگانه‌ای می‌ماند.
        Assert.Equal(100m, await new TransportQuantityService(db).GetRemainingMtAsync(legId));
    }

    [Fact]
    public async Task Contract_Closure_Sees_A_Bulk_Created_Leg_As_One_Open_Transport_Not_Two()
    {
        await using var db = BuildDb();
        await SeedAsync(db, loadingCount: 2);
        var (workflow, _) = BuildServices(db);
        await ConvertAsync(workflow, 2);

        var readiness = await ClosureService(db).EvaluateAsync(1);

        Assert.NotNull(readiness);
        Assert.False(readiness.CanClose);
        // دو حملِ باز با مجموع ۲۰۰ تن — نه ۴۰۰ تن، که نشانهٔ دوبار شمردنِ بارگیری و حمل می‌بود.
        var transport = Assert.Single(
            readiness.Blockers,
            b => b.Kind == ContractClosureBlockerKind.Transport && b.Message.Contains("حمل موجودی"));
        Assert.Contains("2", transport.Message);
        Assert.Contains("200", transport.Message);
    }

    [Fact]
    public async Task Contract_Closure_Does_Not_Double_Count_A_Bulk_Converted_Loading()
    {
        await using var db = BuildDb();
        await SeedAsync(db, loadingCount: 1);
        var (workflow, _) = BuildServices(db);
        await ConvertAsync(workflow, 1);

        var readiness = await ClosureService(db).EvaluateAsync(1);

        Assert.NotNull(readiness);
        // مقدارِ تبدیل‌شده از «بارگیریِ رسیدنشده» کم شده است؛ اگر نمی‌شد، همان ۱۰۰ تن دوبار
        // (یک‌بار به‌عنوان بارگیری، یک‌بار به‌عنوان حمل) مانع بستن قرارداد می‌شد.
        Assert.DoesNotContain(readiness.Blockers, b => b.Message.Contains("بارگیری"));
        var transport = Assert.Single(
            readiness.Blockers,
            b => b.Kind == ContractClosureBlockerKind.Transport && b.Message.Contains("حمل موجودی"));
        Assert.Contains("100", transport.Message);
    }

    // ───────────────────────── کمکی‌ها ─────────────────────────

    private static async Task ConvertAsync(
        TransportWorkflowService workflow,
        int count,
        int? serviceProviderId = null)
    {
        var result = await workflow.StartManyFromLoadingAsync(new BulkStartTransportFromLoadingCommand
        {
            Rows = Enumerable.Range(1, count)
                .Select(id => new BulkStartTransportFromLoadingRow
                {
                    LoadingRegisterId = id,
                    QuantityMt = 100m,
                    TransportType = LoadingTransportType.Truck,
                    TruckId = 1,
                    ServiceProviderId = serviceProviderId,
                    Reference = $"BULK-{id}"
                })
                .ToList(),
            TransportDate = TransportDate
        });
        Assert.Empty(result.Failures);
        Assert.Equal(count, result.CreatedCount);
    }

    private static ContractClosureService ClosureService(ApplicationDbContext db)
        => new(db, new AuditService(db), new StockService(db));

    private static Task<int> FirstLegIdAsync(ApplicationDbContext db)
        => db.InventoryTransportLegs.AsNoTracking().OrderBy(l => l.Id).Select(l => l.Id).FirstAsync();

    private static (TransportWorkflowService Workflow, InventoryTransportReceiptService Receipts) BuildServices(
        ApplicationDbContext db)
    {
        var stock = new StockService(db);
        var quantities = new TransportQuantityService(db);
        var receipts = new InventoryTransportReceiptService(
            db,
            new CurrencyConversionService(new PricingService(db)),
            quantities: quantities);
        var workflow = new TransportWorkflowService(
            db,
            new InventoryTransportBatchService(db, stock),
            new TransportChainService(db, receipts, quantities),
            receipts,
            new LossEventWorkflowService(db, stock, new AuditService(db)));
        return (workflow, receipts);
    }

    private static async Task SeedAsync(ApplicationDbContext db, int loadingCount)
    {
        db.Products.Add(new Product { Id = 1, Code = "DSL", Name = "Diesel" });
        db.Companies.Add(new Company { Id = 1, Code = "PTG", Name = "PTG" });
        db.Suppliers.Add(new Supplier { Id = 1, Name = "Supplier" });
        db.Terminals.Add(new Terminal { Id = 1, Code = "T1", Name = "Terminal" });
        db.StorageTanks.Add(new StorageTank
        {
            Id = 1,
            TerminalId = 1,
            ProductId = 1,
            TankCode = "TK-1",
            DisplayName = "Tank 1",
            CapacityMt = 100_000m
        });
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
}
