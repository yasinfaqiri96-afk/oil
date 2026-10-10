using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PTGOilSystem.Web.Controllers;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.Loading;
using PTGOilSystem.Web.Services;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// لِجِ حمل قیمت بارگیری را snapshot می‌کند و P&amp;L حمل/محموله آن را مقدم بر قرارداد می‌خواند.
/// اصلاح قیمت بارگیری یا قرارداد باید به لِج‌های وابسته برسد، وگرنه لِج با قیمت کهنه می‌ماند.
/// </summary>
public class TransportLegPurchaseCostSyncTests
{
    private static readonly DateTime LoadingDate = new(2026, 6, 1);

    [Fact]
    public async Task EditPrice_Carries_The_New_Price_To_Legs_Built_From_That_Loading()
    {
        await using var db = NewDb();
        Seed(db, loadingPriceUsd: 500m);
        db.InventoryTransportLegs.Add(Leg(1, unitCostUsd: 500m, loadingIds: [10]));
        await db.SaveChangesAsync();

        var result = await NewLoadingController(db).EditPrice(10, new LoadingPriceEditViewModel
        {
            Id = 10,
            LoadingPriceUsd = 700m
        });

        Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(700m, (await db.InventoryTransportLegs.AsNoTracking().SingleAsync()).PurchaseUnitCostUsd);
    }

    [Fact]
    public async Task Edit_Carries_The_New_Price_But_Leaves_Manual_And_Mixed_Legs_Alone()
    {
        await using var db = NewDb();
        Seed(db, loadingPriceUsd: 500m);
        db.LoadingRegisters.Add(new LoadingRegister
        {
            Id = 11, ContractId = 1, ProductId = 1, LoadingDate = LoadingDate,
            LoadedQuantityMt = 5m, LoadingPriceUsd = 500m, SettlementCurrencyCode = "USD"
        });
        db.InventoryTransportLegs.AddRange(
            Leg(1, unitCostUsd: 500m, loadingIds: [10]),
            Leg(2, unitCostUsd: 999m, loadingIds: [10]),       // بهای دستی
            Leg(3, unitCostUsd: 500m, loadingIds: [10, 11]));  // چند بارگیری
        await db.SaveChangesAsync();

        var result = await NewLoadingController(db).Edit(10, new LoadingEditViewModel
        {
            Id = 10,
            LoadingDate = LoadingDate,
            LoadedQuantityMt = 15m,
            LoadingPriceUsd = 700m
        });

        Assert.IsType<RedirectToActionResult>(result);
        var legs = await db.InventoryTransportLegs.AsNoTracking().OrderBy(l => l.Id).ToListAsync();
        Assert.Equal(700m, legs[0].PurchaseUnitCostUsd);
        Assert.Equal(999m, legs[1].PurchaseUnitCostUsd);
        Assert.Equal(500m, legs[2].PurchaseUnitCostUsd);
    }

    [Fact]
    public async Task Contract_Reprice_Carries_The_Final_Price_To_Legs_Of_Unpriced_Loadings()
    {
        await using var db = NewDb();
        Seed(db, loadingPriceUsd: null);
        db.InventoryTransportLegs.Add(Leg(1, unitCostUsd: null, loadingIds: [10]));
        await db.SaveChangesAsync();

        await new ContractsController(db, new AuditService(db)) { TempData = NewTempData() }
            .RepricePurchaseLoadings(1);

        Assert.Equal(700m, (await db.LoadingRegisters.AsNoTracking().SingleAsync()).LoadingPriceUsd);
        Assert.Equal(700m, (await db.InventoryTransportLegs.AsNoTracking().SingleAsync()).PurchaseUnitCostUsd);
    }

    [Theory]
    [InlineData("received")]
    [InlineData("cancelled")]
    [InlineData("archived")]
    [InlineData("partial-receipt")]
    [InlineData("sold")]
    [InlineData("continued")]
    [InlineData("loss")]
    [InlineData("posted-journal")]
    [InlineData("closed-year")]
    [InlineData("locked-period")]
    [InlineData("operational-lock")]
    [InlineData("price-cleared")]
    public async Task Synchronization_Preserves_Consumed_Posted_And_Closed_History(string scenario)
    {
        await using var db = NewDb();
        Seed(db, loadingPriceUsd: 500m);
        var leg = Leg(1, unitCostUsd: 500m, loadingIds: [10]);
        if (scenario == "received") leg.Status = InventoryTransportLegStatus.Received;
        if (scenario == "cancelled") leg.Status = InventoryTransportLegStatus.Cancelled;
        if (scenario == "archived") leg.IsArchived = true;
        db.InventoryTransportLegs.Add(leg);
        await db.SaveChangesAsync();
        switch (scenario)
        {
            case "partial-receipt":
                db.InventoryTransportReceipts.Add(new InventoryTransportReceipt
                {
                    InventoryTransportLegId = leg.Id, ReceiptDate = LoadingDate.AddDays(1),
                    ReceivedQuantityMt = 1m, DestinationTerminalId = 1
                });
                break;
            case "sold":
                var sale = new SalesTransaction { InvoiceNumber = "SYNC-SALE", ProductId = 1,
                    SaleDate = LoadingDate.AddDays(1), QuantityMt = 1m, TotalUsd = 800m };
                db.SalesTransactions.Add(sale);
                db.SalesTransactionSourceAllocations.Add(new SalesTransactionSourceAllocation
                { SalesTransaction = sale, TransportLegId = leg.Id, SourcePurchaseContractId = 1, QuantityMt = 1m });
                break;
            case "continued":
                var child = Leg(2, unitCostUsd: 500m, loadingIds: []);
                child.Allocations.Add(new InventoryTransportLegAllocation
                { SourceTransportLegId = leg.Id, SourcePurchaseContractId = 1, QuantityMt = 1m });
                db.InventoryTransportLegs.Add(child);
                break;
            case "loss":
                db.LossEvents.Add(new LossEvent
                { TransportLegId = leg.Id, ProductId = 1, EventDate = LoadingDate, DifferenceQuantityMt = 1m });
                break;
            case "posted-journal":
                db.JournalEntries.Add(new JournalEntry { CompanyId = 1, JournalNumber = "SYNC-POSTED",
                    SourceModule = "InventoryTransfer", SourceEntityType = nameof(InventoryTransportLeg),
                    SourceEntityId = leg.Id, Status = JournalEntryStatus.Posted, AccountingDate = LoadingDate });
                break;
            case "closed-year":
            case "locked-period":
                var year = new FiscalYear { CompanyId = 1, Name = "SYNC-FY", StartDate = new(2026, 1, 1),
                    EndDate = new(2026, 12, 31), Status = scenario == "closed-year" ? FiscalYearStatus.Closed : FiscalYearStatus.Open };
                db.FiscalYears.Add(year);
                db.FiscalPeriods.Add(new FiscalPeriod { CompanyId = 1, FiscalYear = year, PeriodNumber = 6,
                    Name = "June", StartDate = new(2026, 6, 1), EndDate = new(2026, 6, 30),
                    Status = FiscalPeriodStatus.HardLocked });
                break;
            case "operational-lock":
                db.OperationalPeriodLocks.Add(new OperationalPeriodLock
                { LockedThroughDate = LoadingDate, IsActive = true, Reason = "Historical synchronization test" });
                break;
        }
        await db.SaveChangesAsync();
        Assert.Equal(0, await TransportLegPurchaseCostSync.SyncFromLoadingAsync(db, 10, 500m,
            scenario == "price-cleared" ? null : 700m));
        Assert.Equal(500m, leg.PurchaseUnitCostUsd);
        Assert.Equal(EntityState.Unchanged, db.Entry(leg).State);
    }

    [Fact]
    public async Task Open_Unconsumed_InTransit_Estimate_Can_Be_Finalized()
    {
        await using var db = NewDb();
        Seed(db, loadingPriceUsd: null);
        var leg = Leg(1, unitCostUsd: null, loadingIds: [10]);
        leg.Status = InventoryTransportLegStatus.InTransit;
        db.InventoryTransportLegs.Add(leg);
        await db.SaveChangesAsync();
        Assert.Equal(1, await TransportLegPurchaseCostSync.SyncFromLoadingAsync(db, 10, null, 700m));
        Assert.Equal(700m, leg.PurchaseUnitCostUsd);
    }

    [Fact]
    public async Task Draft_Contract_Does_Not_Add_A_New_Restriction_To_Open_Estimate_Finalization()
    {
        await using var db = NewDb();
        Seed(db, loadingPriceUsd: null);
        db.Contracts.Local.Single().Status = ContractStatus.Draft;
        var leg = Leg(1, unitCostUsd: null, loadingIds: [10]);
        db.InventoryTransportLegs.Add(leg);
        await db.SaveChangesAsync();
        Assert.Equal(1, await TransportLegPurchaseCostSync.SyncFromLoadingAsync(db, 10, null, 700m));
        Assert.Equal(700m, leg.PurchaseUnitCostUsd);
    }

    private static InventoryTransportLeg Leg(int id, decimal? unitCostUsd, int[] loadingIds)
    {
        var leg = new InventoryTransportLeg
        {
            Id = id,
            SourcePurchaseContractId = 1,
            ProductId = 1,
            TransportType = LoadingTransportType.Truck,
            LoadedDate = LoadingDate,
            QuantityMt = 15m,
            Status = InventoryTransportLegStatus.Loaded,
            PurchaseUnitCostUsd = unitCostUsd
        };
        foreach (var loadingId in loadingIds)
        {
            leg.Allocations.Add(new InventoryTransportLegAllocation
            {
                SourceLoadingRegisterId = loadingId,
                SourcePurchaseContractId = 1,
                QuantityMt = 15m / loadingIds.Length
            });
        }

        return leg;
    }

    private static void Seed(ApplicationDbContext db, decimal? loadingPriceUsd)
    {
        db.Units.Add(new Unit { Id = 1, Code = "MT", Name = "Metric Ton", Symbol = "MT", IsActive = true });
        db.Products.Add(new Product { Id = 1, Code = "GO", Name = "Gas Oil", UnitId = 1, IsActive = true });
        db.Companies.Add(new Company { Id = 1, Code = "PTG", Name = "PTG", IsActive = true });
        db.Suppliers.Add(new Supplier { Id = 1, Name = "Supplier A", IsActive = true });
        db.Contracts.Add(new Contract
        {
            Id = 1,
            ContractNumber = "PUR-LEG-1",
            ContractType = ContractType.Purchase,
            Status = ContractStatus.Active,
            ProductId = 1,
            UnitId = 1,
            CompanyId = 1,
            SupplierId = 1,
            ContractDate = LoadingDate,
            QuantityMt = 100m,
            PricingMethod = PricingMethod.Fixed,
            UnitPriceUsd = 700m,
            SettlementCurrencyCode = "USD",
            RubRatePolicy = RubSettlementRatePolicy.NotApplicable
        });
        db.LoadingRegisters.Add(new LoadingRegister
        {
            Id = 10,
            ContractId = 1,
            ProductId = 1,
            LoadingDate = LoadingDate,
            LoadedQuantityMt = 15m,
            LoadingPriceUsd = loadingPriceUsd,
            SettlementCurrencyCode = "USD"
        });
        db.SaveChanges();
    }

    private static ApplicationDbContext NewDb()
        => new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static LoadingController NewLoadingController(ApplicationDbContext db)
        => new(db, new AuditService(db), NullLogger<LoadingController>.Instance)
        {
            TempData = NewTempData()
        };

    private static TempDataDictionary NewTempData()
        => new(new DefaultHttpContext(), new NullTempDataProvider());

    private sealed class NullTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object?> LoadTempData(HttpContext context) => new Dictionary<string, object?>();
        public void SaveTempData(HttpContext context, IDictionary<string, object?> values) { }
    }
}
