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
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// کرایهٔ بارگیری: طرف‌حساب (شرکت خدماتی / راننده آزاد / ترانسپورت شخصی)، مسئول کرایه،
/// سند مصرف رسمی، دفتر کل، تب مصارف قرارداد و جزئیات بارگیری.
/// </summary>
public class LoadingFreightPartyTests
{
    [Fact]
    public async Task FreeTransport_Provider_Buyer_Creates_Payable_Expense_Visible_In_Contract_Costs_Once()
    {
        await using var db = NewDb();
        Seed(db);
        await db.SaveChangesAsync();

        var result = await NewLoadingController(db).Create(Model(CostResponsibility.Buyer, new LoadingCreateRowViewModel
        {
            LoadingDate = new DateTime(2026, 4, 23),
            TruckId = 1,
            BillOfLadingNumber = "CMR-P1",
            LoadedQuantityMt = 32.5m,
            LogisticsMode = "free",
            LogisticsServiceProviderId = 1,
            FreightRateUsdPerMt = 2.5m
        }));

        Assert.IsType<RedirectToActionResult>(result);
        var loading = await db.LoadingRegisters.SingleAsync();
        var expense = await db.ExpenseTransactions.SingleAsync();
        Assert.Equal(1, expense.ServiceProviderId);
        Assert.Null(expense.DriverId);
        Assert.Equal(loading.Id, expense.LoadingRegisterId);
        Assert.Equal(loading.ContractId, expense.ContractId);
        Assert.Equal(81.25m, expense.AmountUsd);
        Assert.Equal(ExpenseSettlementMode.Payable, expense.SettlementMode);

        var ledger = await db.LedgerEntries.SingleAsync(l => l.SourceType == "Expense" && l.SourceId == expense.Id);
        Assert.Equal(LedgerSide.Credit, ledger.Side);
        Assert.Equal(1, ledger.ServiceProviderId);
        Assert.Equal(81.25m, ledger.AmountUsd);

        var journey = await JourneyAsync(db);
        var item = Assert.Single(journey.ExpenseItems);
        Assert.Equal(expense.Id, item.ExpenseTransactionId);
        Assert.Equal(81.25m, journey.Kpis.TotalExpensesUsd);
        // TransportExpenseUsd درون‌خطی همان کرایه است؛ کنار سند رسمی دوباره شمرده نمی‌شود.
        Assert.Equal(81.25m, journey.ContractTransportExpenseUsd);
    }

    [Fact]
    public async Task FreeDriver_Buyer_Creates_Driver_Payable_Expense_Visible_In_Contract_Costs()
    {
        await using var db = NewDb();
        Seed(db);
        await db.SaveChangesAsync();

        var result = await NewLoadingController(db).Create(Model(CostResponsibility.Buyer, new LoadingCreateRowViewModel
        {
            LoadingDate = new DateTime(2026, 4, 23),
            TruckId = 1,
            BillOfLadingNumber = "CMR-D1",
            LoadedQuantityMt = 30m,
            LogisticsMode = "driver",
            DriverName = "Karim Khan",
            DriverPhone = "0700 111 222",
            FreightRateUsdPerMt = 3m
        }));

        Assert.IsType<RedirectToActionResult>(result);
        var driver = await db.Drivers.SingleAsync();
        var loading = await db.LoadingRegisters.SingleAsync();
        Assert.Equal(driver.Id, loading.DriverId);

        var expense = await db.ExpenseTransactions.SingleAsync();
        Assert.Equal(driver.Id, expense.DriverId);
        Assert.Null(expense.ServiceProviderId);
        Assert.Equal(loading.Id, expense.LoadingRegisterId);
        Assert.Equal(1, expense.ContractId);
        Assert.Equal(90m, expense.AmountUsd);
        Assert.Equal(ExpenseSettlementMode.Payable, expense.SettlementMode);
        Assert.Equal(AccountingPartyType.Driver, expense.CounterpartyType);

        var ledger = await db.LedgerEntries.SingleAsync(l => l.SourceType == "Expense" && l.SourceId == expense.Id);
        Assert.Equal(LedgerSide.Credit, ledger.Side);
        Assert.Equal(driver.Id, ledger.DriverId);
        Assert.Equal(90m, ledger.AmountUsd);

        var journey = await JourneyAsync(db);
        Assert.Contains(journey.ExpenseItems, e => e.ExpenseTransactionId == expense.Id);
        Assert.Equal(90m, journey.Kpis.TotalExpensesUsd);
        Assert.Equal(90m, journey.ContractTransportExpenseUsd);
    }

    [Fact]
    public async Task FreeDriver_Seller_Responsibility_Creates_No_Company_Payable()
    {
        await using var db = NewDb();
        Seed(db);
        await db.SaveChangesAsync();

        var result = await NewLoadingController(db).Create(Model(CostResponsibility.Seller, new LoadingCreateRowViewModel
        {
            LoadingDate = new DateTime(2026, 4, 23),
            TruckId = 1,
            BillOfLadingNumber = "CMR-D2",
            LoadedQuantityMt = 30m,
            LogisticsMode = "driver",
            DriverName = "Seller Driver",
            FreightRateUsdPerMt = 3m
        }));

        Assert.IsType<RedirectToActionResult>(result);
        Assert.NotNull((await db.LoadingRegisters.SingleAsync()).DriverId);
        Assert.Empty(await db.ExpenseTransactions.ToListAsync());
        Assert.Empty(await db.LedgerEntries.Where(l => l.SourceType == "Expense").ToListAsync());
    }

    [Fact]
    public async Task FreeTransport_Without_Party_And_Buyer_Responsibility_Is_Rejected()
    {
        await using var db = NewDb();
        Seed(db);
        await db.SaveChangesAsync();

        var controller = NewLoadingController(db);
        var result = await controller.Create(Model(CostResponsibility.Buyer, new LoadingCreateRowViewModel
        {
            LoadingDate = new DateTime(2026, 4, 23),
            TruckId = 1,
            BillOfLadingNumber = "CMR-X1",
            LoadedQuantityMt = 30m,
            LogisticsMode = "free",
            FreightRateUsdPerMt = 3m
        }));

        Assert.IsType<ViewResult>(result);
        Assert.Contains(
            controller.ModelState.Values.SelectMany(v => v.Errors),
            e => e.ErrorMessage.Contains("شرکت خدماتی طرف‌حساب کرایه", StringComparison.Ordinal));
        Assert.Empty(await db.LoadingRegisters.ToListAsync());
        Assert.Empty(await db.ExpenseTransactions.ToListAsync());
    }

    [Fact]
    public async Task Buyer_Freight_Without_Transport_Type_Is_Rejected()
    {
        await using var db = NewDb();
        Seed(db);
        await db.SaveChangesAsync();

        var controller = NewLoadingController(db);
        var result = await controller.Create(Model(CostResponsibility.Buyer, new LoadingCreateRowViewModel
        {
            LoadingDate = new DateTime(2026, 4, 23),
            TruckId = 1,
            BillOfLadingNumber = "CMR-N1",
            LoadedQuantityMt = 50m,
            FreightRateUsdPerMt = 1m
        }));

        Assert.IsType<ViewResult>(result);
        Assert.Contains(
            controller.ModelState.Values.SelectMany(v => v.Errors),
            e => e.ErrorMessage.Contains("«نوعیت ترانسپورت» و طرف‌حساب کرایه", StringComparison.Ordinal));
        Assert.Empty(await db.LoadingRegisters.ToListAsync());
    }

    [Fact]
    public async Task OwnedTransport_Creates_No_External_Payable_But_Shows_Internal_Cost_In_Contract()
    {
        await using var db = NewDb();
        Seed(db, withOwnedAsset: true);
        await db.SaveChangesAsync();

        var result = await NewLoadingController(db).Create(Model(CostResponsibility.Buyer, new LoadingCreateRowViewModel
        {
            LoadingDate = new DateTime(2026, 4, 23),
            TruckId = 1,
            BillOfLadingNumber = "CMR-O1",
            LoadedQuantityMt = 32.5m,
            LogisticsMode = "owned",
            OperationalAssetId = 1,
            FreightRateUsdPerMt = 3m
        }));

        Assert.IsType<RedirectToActionResult>(result);
        Assert.Empty(await db.ExpenseTransactions.ToListAsync());
        Assert.Empty(await db.LedgerEntries.ToListAsync());
        var rent = await db.AssetRentTransactions.SingleAsync();
        Assert.False(rent.IsPostedToLedger);

        var journey = await JourneyAsync(db);
        Assert.Empty(journey.ExpenseItems);
        var internalCost = Assert.Single(journey.InternalTransportCostItems);
        Assert.Equal(97.5m, internalCost.AmountUsd);
        Assert.Equal("Company Truck 1", internalCost.AssetName);
        Assert.Equal(0m, journey.Kpis.TotalExpensesUsd);
        // همان مبلغ فقط یک‌بار (از فیلد درون‌خطی بارگیری) در کرایهٔ قرارداد و در «مجموع مصارف»
        // بالای تب (ExpenseItems + LoadingOperationalExpenseUsd + گمرک) است.
        Assert.Equal(97.5m, journey.ContractTransportExpenseUsd);
        Assert.Equal(97.5m, journey.LoadingOperationalExpenseUsd);
        Assert.Equal(97.5m, journey.ExpenseItems.Sum(e => e.AmountUsd) + journey.LoadingOperationalExpenseUsd);
    }

    [Fact]
    public async Task Details_Shows_Responsibility_Mode_And_Real_Party_Per_Document()
    {
        await using var db = NewDb();
        Seed(db);
        await db.SaveChangesAsync();
        await NewLoadingController(db).Create(Model(CostResponsibility.Buyer, new LoadingCreateRowViewModel
        {
            LoadingDate = new DateTime(2026, 4, 23),
            TruckId = 1,
            BillOfLadingNumber = "CMR-P2",
            LoadedQuantityMt = 32.5m,
            LogisticsMode = "free",
            LogisticsServiceProviderId = 1,
            FreightRateUsdPerMt = 2.5m
        }));
        var loadingId = (await db.LoadingRegisters.SingleAsync()).Id;

        var view = Assert.IsType<ViewResult>(await NewLoadingController(db).Details(loadingId));
        var model = Assert.IsType<LoadingDetailsViewModel>(view.Model);

        Assert.Equal(CostResponsibility.Buyer, model.FreightCostResponsibility);
        Assert.Equal("ترانسپورت آزاد (شرکت خدماتی)", model.FreightTransportModeLabel);
        var party = Assert.Single(model.FreightParties);
        Assert.Equal("Axis Logistics", party.PartyName);
        Assert.Equal(1, party.ServiceProviderId);
        Assert.Equal(81.25m, party.AmountUsd);
        Assert.False(party.IsInternal);
    }

    private static async Task<ContractJourneyDetailsViewModel> JourneyAsync(ApplicationDbContext db)
    {
        var controller = new ContractJourneyController(db, new StockService(db))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        var view = Assert.IsType<ViewResult>(await controller.Details(1, tab: ContractJourneyTabs.Details.Costs));
        return Assert.IsType<ContractJourneyDetailsViewModel>(view.Model);
    }

    private static ApplicationDbContext NewDb()
        => new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static LoadingController NewLoadingController(ApplicationDbContext db)
        => new(db, new AuditService(db), NullLogger<LoadingController>.Instance)
        {
            TempData = new TempDataDictionary(new DefaultHttpContext(), new InMemoryTempDataProvider())
        };

    private static LoadingCreateViewModel Model(CostResponsibility responsibility, params LoadingCreateRowViewModel[] rows)
        => new()
        {
            ContractId = 1,
            ProductId = 1,
            TransportType = LoadingTransportType.Truck,
            RecordFreight = true,
            FreightCostResponsibility = responsibility,
            Rows = rows.ToList()
        };

    private static void Seed(ApplicationDbContext db, bool withOwnedAsset = false)
    {
        db.Products.Add(new Product { Id = 1, Code = "GO", Name = "Gas Oil", IsActive = true });
        db.Companies.Add(new Company { Id = 1, Code = "PTG", Name = "Petro Trade Group", IsActive = true });
        db.ServiceProviders.Add(new ServiceProvider
        {
            Id = 1,
            Code = "LOG-1",
            Name = "Axis Logistics",
            ProviderType = ServiceProviderType.TransportCompany,
            IsActive = true
        });
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

        if (withOwnedAsset)
        {
            db.OperationalAssets.Add(new OperationalAsset
            {
                Id = 1,
                AssetCode = "TRK-OWN-1",
                Name = "Company Truck 1",
                AssetType = OperationalAssetType.Truck,
                OwnershipMode = OperationalAssetOwnershipMode.FullyCompanyOwned,
                DefaultInternalRateUsd = 3m,
                IsActive = true
            });
            db.AssetOwnershipShares.Add(new AssetOwnershipShare
            {
                Id = 1,
                OperationalAssetId = 1,
                OwnerType = AssetOwnerType.Company,
                CompanyId = 1,
                SharePercent = 100m,
                EffectiveFrom = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            });
        }
    }

    private sealed class InMemoryTempDataProvider : ITempDataProvider
    {
        private IDictionary<string, object> _data = new Dictionary<string, object>();

        public IDictionary<string, object> LoadTempData(HttpContext context) => _data;

        public void SaveTempData(HttpContext context, IDictionary<string, object> values)
            => _data = new Dictionary<string, object>(values);
    }
}
