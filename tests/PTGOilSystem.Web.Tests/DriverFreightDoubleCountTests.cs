using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PTGOilSystem.Web.Controllers;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.ContractJourney;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.Loading;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Reporting;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// کرایهٔ «راننده آزاد» یک سند مصرف (ExpenseTransaction با DriverId) دارد. وقتی همان بارگیری بعداً ردیف
/// «ثبت مصارف» بگیرد، LoadingController مبلغ همان سند را برای نمایش در فیلد درون‌خطی کرایه آینه
/// می‌کند. سود قرارداد باید آن کرایه را فقط یک بار بشمارد.
/// </summary>
public sealed class DriverFreightDoubleCountTests
{
    private const decimal Qty = 100m;
    private const decimal PriceUsd = 100m;   // خرید = 10,000
    private const decimal RateUsd = 10m;     // کرایه = 1,000

    [Fact]
    public async Task C_Driver_Freight_Mirrored_Into_A_Line_Based_Loading_Is_Counted_Once()
    {
        await using var db = await SeedAsync();
        await CreateDriverLoadingAsync(db, CostResponsibility.Buyer);
        await SaveExpenseLinesAsync(db, storageUsd: 50m);

        var loading = await db.LoadingRegisters.AsNoTracking().SingleAsync();
        Assert.Equal(1_000m, loading.TransportExpenseUsd);              // آینهٔ سند راننده
        Assert.Single(await db.ExpenseTransactions.AsNoTracking()
            .Where(e => !e.IsCancelled && e.DriverId != null).ToListAsync());

        var e = await EconomicsAsync(db);

        Assert.Equal(10_000m, e.PurchaseValueUsd);
        Assert.Equal(1_000m, e.TransportCostUsd + e.GeneralExpenseCostUsd);
        Assert.Equal(50m, e.WarehouseCostUsd);
        Assert.Equal(11_050m, e.LifecycleTotalCostUsd);
    }

    [Fact]
    public async Task A_Undocumented_Inline_Freight_Is_Counted_Once()
    {
        await using var db = await SeedAsync();
        db.LoadingRegisters.Add(new LoadingRegister
        {
            Id = 1,
            ContractId = 1,
            ProductId = 1,
            LoadingDate = new DateTime(2026, 4, 23),
            LoadedQuantityMt = Qty,
            LoadingPriceUsd = PriceUsd,
            TransportExpenseUsd = 1_000m
        });
        await db.SaveChangesAsync();

        var e = await EconomicsAsync(db);

        Assert.Equal(1_000m, e.TransportCostUsd);
        Assert.Equal(0m, e.GeneralExpenseCostUsd);
        Assert.Equal(11_000m, e.LifecycleTotalCostUsd);
    }

    [Fact]
    public async Task B_Driver_Expense_Document_Alone_Is_Counted_Once()
    {
        await using var db = await SeedAsync();
        await CreateDriverLoadingAsync(db, CostResponsibility.Buyer);

        var e = await EconomicsAsync(db);

        Assert.Equal(0m, e.TransportCostUsd);                       // بارگیری قدیمیِ دارای سند ⇒ آینه کنار می‌رود
        Assert.Equal(1_000m, e.GeneralExpenseCostUsd);
        Assert.Equal(11_000m, e.LifecycleTotalCostUsd);
    }

    [Fact]
    public async Task C_Journey_Page_Shows_The_Same_Single_Freight()
    {
        await using var db = await SeedAsync();
        await CreateDriverLoadingAsync(db, CostResponsibility.Buyer);
        await SaveExpenseLinesAsync(db, storageUsd: 50m);

        var journey = await JourneyAsync(db);

        // KPI «مصارف» فقط اسناد است (کرایهٔ راننده)؛ هزینهٔ کل = اسناد + درون‌خطیِ بی‌سند (گدام).
        Assert.Equal(1_000m, journey.Kpis.TotalExpensesUsd);
        Assert.Equal(1_050m, journey.ExpenseItems.Sum(x => x.AmountUsd) + journey.LoadingOperationalExpenseUsd);
        Assert.Equal(1_000m, journey.ContractTransportExpenseUsd);
    }

    [Fact]
    public async Task C_Driver_Payable_And_Ledger_Are_Unchanged()
    {
        await using var db = await SeedAsync();
        await CreateDriverLoadingAsync(db, CostResponsibility.Buyer);
        await SaveExpenseLinesAsync(db, storageUsd: 50m);

        var driverId = (await db.Drivers.AsNoTracking().SingleAsync()).Id;
        var owed = (await db.LedgerEntries.AsNoTracking().Where(l => l.DriverId == driverId).ToListAsync())
            .Sum(l => l.Side == LedgerSide.Credit ? l.AmountUsd : -l.AmountUsd);
        Assert.Equal(1_000m, owed);
        Assert.Single(await db.ExpenseTransactions.AsNoTracking().Where(x => !x.IsCancelled && x.DriverId == driverId).ToListAsync());
    }

    [Fact]
    public async Task D_Service_Provider_Freight_Turned_Into_A_Line_Is_Counted_Once()
    {
        await using var db = await SeedAsync();
        await CreateProviderLoadingAsync(db);
        Assert.Equal(11_000m, (await EconomicsAsync(db)).LifecycleTotalCostUsd);

        await SaveExpenseLinesAsync(db, storageUsd: 50m);
        var e = await EconomicsAsync(db);

        Assert.Equal(1_000m, e.TransportCostUsd + e.GeneralExpenseCostUsd);
        Assert.Equal(11_050m, e.LifecycleTotalCostUsd);
    }

    [Fact]
    public async Task E_Seller_Borne_Freight_Is_Not_A_Company_Cost()
    {
        await using var db = await SeedAsync();
        await CreateDriverLoadingAsync(db, CostResponsibility.Seller);

        var e = await EconomicsAsync(db);

        Assert.Equal(0m, e.TransportCostUsd + e.GeneralExpenseCostUsd);
        Assert.Equal(10_000m, e.LifecycleTotalCostUsd);
    }

    [Fact]
    public async Task F_Cancelled_Driver_Document_Follows_The_Canonical_Rules()
    {
        await using var db = await SeedAsync();
        await CreateDriverLoadingAsync(db, CostResponsibility.Buyer);
        await SaveExpenseLinesAsync(db, storageUsd: 50m);

        var document = await db.ExpenseTransactions.SingleAsync(x => x.DriverId != null);
        document.IsCancelled = true;
        await db.SaveChangesAsync();

        // سند لغوشده مصرف نیست؛ فیلد درون‌خطی (آینهٔ قبلی) تنها ردِ آن کرایه است و یک بار شمرده می‌شود.
        var stale = await EconomicsAsync(db);
        Assert.Equal(0m, stale.GeneralExpenseCostUsd);
        Assert.Equal(1_000m, stale.TransportCostUsd);
        Assert.Equal(11_050m, stale.LifecycleTotalCostUsd);

        // ذخیرهٔ دوبارهٔ ردیف‌ها آینه را با اسناد فعال هماهنگ می‌کند؛ کرایهٔ لغوشده دیگر نیست.
        await SaveExpenseLinesAsync(db, storageUsd: null);
        var resynced = await EconomicsAsync(db);
        Assert.Equal(0m, resynced.TransportCostUsd + resynced.GeneralExpenseCostUsd);
        Assert.Equal(10_050m, resynced.LifecycleTotalCostUsd);
    }

    [Fact]
    public async Task G_Contract_Profit_And_Partner_Basis_Use_The_Single_Freight()
    {
        await using var db = await SeedAsync();
        await CreateDriverLoadingAsync(db, CostResponsibility.Buyer);
        await SaveExpenseLinesAsync(db, storageUsd: 50m);
        db.SalesTransactions.Add(new SalesTransaction
        {
            Id = 100,
            CompanyId = 1,
            CustomerId = 1,
            ProductId = 1,
            InvoiceNumber = "INV-100",
            SaleDate = new DateTime(2026, 4, 30),
            QuantityMt = Qty,
            UnitPriceUsd = 120m,
            TotalUsd = 12_000m,
            TotalInCurrency = 12_000m,
            Currency = "USD"
        });
        db.InventoryMovements.Add(new InventoryMovement
        {
            Id = 1,
            ProductId = 1,
            ContractId = 1,
            TerminalId = 1,
            SalesTransactionId = 100,
            Direction = MovementDirection.Out,
            MovementDate = new DateTime(2026, 4, 30),
            QuantityMt = Qty
        });
        await db.SaveChangesAsync();

        var e = await EconomicsAsync(db);

        // سهم شریک از RealizedNetProfitUsd همین موتور ساخته می‌شود.
        Assert.Equal(12_000m - 10_000m - 1_050m, e.RealizedNetProfitUsd);
        Assert.Equal(950m, e.LifecycleMarginUsd);
    }

    // ── زیرساخت ─────────────────────────────────────────────────────────────

    private static async Task<ContractEconomicsSnapshot> EconomicsAsync(ApplicationDbContext db)
    {
        db.ChangeTracker.Clear();
        return (await new ProfitAndLossService(db).BuildContractEconomicsAsync([1]))[1];
    }

    private static async Task<ApplicationDbContext> SeedAsync()
    {
        var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
        db.Currencies.Add(new Currency { Id = 1, Code = "USD", Name = "US Dollar", Symbol = "$", IsActive = true });
        db.Products.Add(new Product { Id = 1, Code = "GO", Name = "Gas Oil", IsActive = true });
        db.Companies.Add(new Company { Id = 1, Code = "PTG", Name = "Petro Trade Group", IsActive = true });
        db.ServiceProviders.Add(new ServiceProvider { Id = 1, Code = "LOG-A", Name = "Logistics A", ProviderType = ServiceProviderType.TransportCompany, IsActive = true });
        db.Contracts.Add(new Contract
        {
            Id = 1,
            ContractNumber = "PUR-1",
            ContractType = ContractType.Purchase,
            ProductId = 1,
            CompanyId = 1,
            ContractDate = new DateTime(2026, 4, 23),
            QuantityMt = 500m
        });
        db.Trucks.Add(new Truck { Id = 1, PlateNumber = "TRK-01", IsActive = true });
        db.Customers.Add(new Customer { Id = 1, Name = "Customer A" });
        db.Terminals.Add(new Terminal { Id = 1, Code = "ILK", Name = "Ilinka" });
        await db.SaveChangesAsync();
        return db;
    }

    private static async Task<ContractJourneyDetailsViewModel> JourneyAsync(ApplicationDbContext db)
    {
        db.ChangeTracker.Clear();
        var controller = new ContractJourneyController(db, new StockService(db))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        var view = Assert.IsType<ViewResult>(await controller.Details(1, tab: ContractJourneyTabs.Details.Costs));
        return Assert.IsType<ContractJourneyDetailsViewModel>(view.Model);
    }

    private static async Task CreateProviderLoadingAsync(ApplicationDbContext db)
    {
        Assert.IsType<RedirectToActionResult>(await NewLoadingController(db).Create(new LoadingCreateViewModel
        {
            ContractId = 1,
            ProductId = 1,
            TransportType = LoadingTransportType.Truck,
            RecordFreight = true,
            FreightCostResponsibility = CostResponsibility.Buyer,
            Rows =
            [
                new LoadingCreateRowViewModel
                {
                    LoadingDate = new DateTime(2026, 4, 23),
                    TruckId = 1,
                    BillOfLadingNumber = "CMR-1",
                    LoadedQuantityMt = Qty,
                    LoadingPriceUsd = PriceUsd,
                    LogisticsMode = "free",
                    LogisticsServiceProviderId = 1,
                    FreightRateUsdPerMt = RateUsd
                }
            ]
        }));
        db.ChangeTracker.Clear();
    }

    private static async Task CreateDriverLoadingAsync(ApplicationDbContext db, CostResponsibility responsibility)
    {
        Assert.IsType<RedirectToActionResult>(await NewLoadingController(db).Create(new LoadingCreateViewModel
        {
            ContractId = 1,
            ProductId = 1,
            TransportType = LoadingTransportType.Truck,
            RecordFreight = true,
            FreightCostResponsibility = responsibility,
            Rows =
            [
                new LoadingCreateRowViewModel
                {
                    LoadingDate = new DateTime(2026, 4, 23),
                    TruckId = 1,
                    BillOfLadingNumber = "CMR-1",
                    LoadedQuantityMt = Qty,
                    LoadingPriceUsd = PriceUsd,
                    LogisticsMode = "driver",
                    DriverName = "Ahmad",
                    DriverPhone = "0700 000 001",
                    FreightRateUsdPerMt = RateUsd
                }
            ]
        }));
        db.ChangeTracker.Clear();
    }

    /// <summary>همان مسیر فرم «ثبت مصارف»: GET برای ساخت نوع‌های پایه، بعد ذخیرهٔ یک ردیف گدام بی‌طرف.</summary>
    private static async Task SaveExpenseLinesAsync(ApplicationDbContext db, decimal? storageUsd)
    {
        var loadingId = (await db.LoadingRegisters.AsNoTracking().SingleAsync()).Id;
        var get = Assert.IsType<ViewResult>(await NewLoadingController(db).EditExpenses(loadingId));
        var model = Assert.IsType<LoadingExpenseEditViewModel>(get.Model);
        var storageTypeId = (await db.ExpenseTypes.AsNoTracking().SingleAsync(t => t.Code == "LOAD-STORAGE")).Id;
        model.Lines = (model.Lines ?? []).ToList();
        if (storageUsd.HasValue)
        {
            model.Lines.Add(new LoadingExpenseLineInputModel
            {
                ExpenseTypeId = storageTypeId,
                CalculationMode = LoadingExpenseCalculationMode.FixedAmount,
                AmountUsd = storageUsd.Value,
                PartyType = LoadingExpensePartyType.None
            });
        }
        db.ChangeTracker.Clear();
        var controller = NewLoadingController(db);
        var result = await controller.EditExpenses(loadingId, model);
        Assert.True(result is RedirectToActionResult or RedirectResult,
            result.GetType().Name + ": " + string.Join(" | ", controller.ModelState.Values.SelectMany(v => v.Errors).Select(x => x.ErrorMessage)));
        db.ChangeTracker.Clear();
    }

    private static LoadingController NewLoadingController(ApplicationDbContext db)
        => new(db, new AuditService(db), NullLogger<LoadingController>.Instance)
        {
            TempData = new TempDataDictionary(new DefaultHttpContext(), new InMemoryTempDataProvider())
        };

    private sealed class InMemoryTempDataProvider : ITempDataProvider
    {
        private IDictionary<string, object> _data = new Dictionary<string, object>();

        public IDictionary<string, object> LoadTempData(HttpContext context) => _data;

        public void SaveTempData(HttpContext context, IDictionary<string, object> values)
            => _data = new Dictionary<string, object>(values);
    }
}
