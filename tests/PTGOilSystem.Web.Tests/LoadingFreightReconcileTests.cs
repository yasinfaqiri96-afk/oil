using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PTGOilSystem.Web.Controllers;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.Loading;
using PTGOilSystem.Web.Models.PartyStatements;
using PTGOilSystem.Web.Models.Payments;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.CompanyFlow;
using PTGOilSystem.Web.Services.Parties;
using PTGOilSystem.Web.Services.PartyStatements;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// کرایهٔ بارگیری: قاعدهٔ واحد مسئول کرایه برای شرکت خدماتی و راننده، هماهنگ‌سازی سند مصرف
/// هنگام ویرایش (لغو + ثبت دوباره، بدون Posting تکراری) و حساب واقعی راننده تا پرداخت.
/// </summary>
public class LoadingFreightReconcileTests
{
    private const decimal Qty = 30m;

    [Theory]
    [InlineData(CostResponsibility.Seller)]
    [InlineData(CostResponsibility.Shared)]
    public async Task Provider_With_NonBuyer_Responsibility_Creates_No_Company_Payable(CostResponsibility responsibility)
    {
        await using var db = await SeedAsync();
        await CreateProviderLoadingAsync(db, responsibility, providerId: 1, rate: 10m);

        Assert.Single(await db.LoadingRegisters.ToListAsync());
        Assert.Empty(await db.ExpenseTransactions.ToListAsync());
        Assert.Empty(await db.LedgerEntries.Where(l => l.SourceType == "Expense").ToListAsync());
    }

    [Fact]
    public async Task Provider_With_Buyer_Responsibility_Creates_Payable()
    {
        await using var db = await SeedAsync();
        await CreateProviderLoadingAsync(db, CostResponsibility.Buyer, providerId: 1, rate: 10m);

        var expense = Assert.Single(await ActiveExpensesAsync(db));
        Assert.Equal(1, expense.ServiceProviderId);
        Assert.Equal(300m, expense.AmountUsd);
        Assert.Equal(300m, await ProviderOwedAsync(db, 1));
    }

    [Fact]
    public async Task Edit_Driver_A_To_B_Moves_The_Payable_To_The_New_Driver()
    {
        await using var db = await SeedAsync();
        await CreateDriverLoadingAsync(db, CostResponsibility.Buyer, "Ahmad", "0700 000 001", rate: 3m);
        var ahmad = await db.Drivers.SingleAsync();
        Assert.Equal(90m, await DriverOwedAsync(db, ahmad.Id));

        var loading = await LoadingAsync(db);
        await EditAsync(db, EditModel(loading, m => { m.DriverName = "Mahmood"; m.DriverPhone = "0700 000 002"; }));

        var mahmood = await db.Drivers.SingleAsync(d => d.FullName == "Mahmood");
        Assert.Equal(0m, await DriverOwedAsync(db, ahmad.Id));
        Assert.Equal(90m, await DriverOwedAsync(db, mahmood.Id));
        var active = Assert.Single(await ActiveExpensesAsync(db));
        Assert.Equal(mahmood.Id, active.DriverId);
        Assert.Single(await db.ExpenseTransactions.Where(e => e.IsCancelled).ToListAsync());
    }

    [Fact]
    public async Task Edit_Provider_A_To_B_Leaves_No_Payable_On_A()
    {
        await using var db = await SeedAsync();
        await CreateProviderLoadingAsync(db, CostResponsibility.Buyer, providerId: 1, rate: 10m);

        await EditAsync(db, EditModel(await LoadingAsync(db), m => m.LogisticsServiceProviderId = 2));

        Assert.Equal(0m, await ProviderOwedAsync(db, 1));
        Assert.Equal(300m, await ProviderOwedAsync(db, 2));
        Assert.Equal(2, Assert.Single(await ActiveExpensesAsync(db)).ServiceProviderId);
    }

    [Fact]
    public async Task Edit_Buyer_To_Seller_Reverses_Company_Payable()
    {
        await using var db = await SeedAsync();
        await CreateProviderLoadingAsync(db, CostResponsibility.Buyer, providerId: 1, rate: 10m);

        await EditAsync(db, EditModel(await LoadingAsync(db), m => m.FreightCostResponsibility = CostResponsibility.Seller));

        Assert.Empty(await ActiveExpensesAsync(db));
        Assert.Equal(0m, await ProviderOwedAsync(db, 1));
        // سند اصلی حذف نمی‌شود؛ لغو + سطر برگشتی در دفتر کل.
        Assert.Single(await db.ExpenseTransactions.Where(e => e.IsCancelled).ToListAsync());
        Assert.Equal(2, await db.LedgerEntries.CountAsync(l => l.ServiceProviderId == 1));
    }

    [Fact]
    public async Task Edit_Seller_To_Buyer_Creates_Payable()
    {
        await using var db = await SeedAsync();
        await CreateProviderLoadingAsync(db, CostResponsibility.Seller, providerId: 1, rate: 10m);
        Assert.Empty(await db.ExpenseTransactions.ToListAsync());

        await EditAsync(db, EditModel(await LoadingAsync(db), m => m.FreightCostResponsibility = CostResponsibility.Buyer));

        Assert.Equal(300m, Assert.Single(await ActiveExpensesAsync(db)).AmountUsd);
        Assert.Equal(300m, await ProviderOwedAsync(db, 1));
    }

    [Fact]
    public async Task Edit_Freight_Rate_10_To_12_Reposts_Amount_Once()
    {
        await using var db = await SeedAsync();
        await CreateProviderLoadingAsync(db, CostResponsibility.Buyer, providerId: 1, rate: 10m);

        await EditAsync(db, EditModel(await LoadingAsync(db), m => m.FreightRateUsdPerMt = 12m));

        var loading = await LoadingAsync(db);
        Assert.Equal(360m, loading.TransportExpenseUsd);
        Assert.Equal(360m, Assert.Single(await ActiveExpensesAsync(db)).AmountUsd);
        Assert.Equal(360m, await ProviderOwedAsync(db, 1));
    }

    [Fact]
    public async Task Edit_Quantity_Recomputes_Freight_From_Rate()
    {
        await using var db = await SeedAsync();
        await CreateProviderLoadingAsync(db, CostResponsibility.Buyer, providerId: 1, rate: 10m);

        await EditAsync(db, EditModel(await LoadingAsync(db), m => m.LoadedQuantityMt = 40m));

        Assert.Equal(400m, Assert.Single(await ActiveExpensesAsync(db)).AmountUsd);
        Assert.Equal(400m, await ProviderOwedAsync(db, 1));
    }

    [Fact]
    public async Task Edit_Turning_Freight_Off_Cancels_Expense_Without_Orphans()
    {
        await using var db = await SeedAsync();
        await CreateProviderLoadingAsync(db, CostResponsibility.Buyer, providerId: 1, rate: 10m);

        await EditAsync(db, EditModel(await LoadingAsync(db), m => m.FreightRateUsdPerMt = null));

        var loading = await LoadingAsync(db);
        Assert.Null(loading.FreightRateUsdPerMt);
        Assert.Null(loading.TransportExpenseUsd);
        Assert.Empty(await ActiveExpensesAsync(db));
        Assert.Equal(0m, await ProviderOwedAsync(db, 1));
    }

    [Fact]
    public async Task Edit_Resave_Without_Changes_Creates_No_Duplicate_Posting()
    {
        await using var db = await SeedAsync();
        await CreateDriverLoadingAsync(db, CostResponsibility.Buyer, "Ahmad", "0700 000 001", rate: 3m);
        var expensesBefore = await db.ExpenseTransactions.CountAsync();
        var ledgerBefore = await db.LedgerEntries.CountAsync();

        await EditAsync(db, EditModel(await LoadingAsync(db), _ => { }));
        await EditAsync(db, EditModel(await LoadingAsync(db), m => m.Notes = "only a note"));

        Assert.Equal(expensesBefore, await db.ExpenseTransactions.CountAsync());
        Assert.Equal(ledgerBefore, await db.LedgerEntries.CountAsync());
        Assert.Empty(await db.ExpenseTransactions.Where(e => e.IsCancelled).ToListAsync());
        Assert.Equal(1, await db.Drivers.CountAsync());
    }

    [Fact]
    public async Task Driver_Freight_Shows_In_Profile_And_Statement_And_Payment_Settles_It()
    {
        await using var db = await SeedAsync();
        await CreateDriverLoadingAsync(db, CostResponsibility.Buyer, "Karim", "0700 111 222", rate: 3m);
        var driver = await db.Drivers.SingleAsync();
        var loading = await LoadingAsync(db);

        // پروفایل راننده: کرایهٔ طلبکار از روی سند مصرف.
        var driversController = new DriversController(db, new AuditService(db), BuildStatementService(db));
        Assert.IsType<ViewResult>(await driversController.Details(driver.Id));
        Assert.Equal(90m, (decimal)driversController.ViewData["DriverFreightCreditUsd"]!);

        // صورت‌حساب رسمی راننده: ردیف کرایه با مرجع همان بارگیری و مانده = طلب راننده.
        var statement = await StatementAsync(db, driver.Id);
        var freightRow = Assert.Single(statement.Rows.Where(r => !r.IsOpeningBalance));
        Assert.StartsWith($"بارگیری #{loading.Id}", freightRow.Description);
        Assert.Equal(90m, Math.Abs(statement.Summary.ClosingBalance));

        // پرداخت کرایهٔ راننده از جریان موجود (TruckPayment + DriverId) همان بدهی را صفر می‌کند.
        var payments = new PaymentsController(db, new PricingService(db), new AuditService(db), NullLogger<PaymentsController>.Instance)
        {
            TempData = new TempDataDictionary(new DefaultHttpContext(), new InMemoryTempDataProvider())
        };
        Assert.IsType<RedirectToActionResult>(await payments.Create(new PaymentCreateViewModel
        {
            PaymentDate = new DateTime(2026, 4, 25),
            Direction = PaymentDirection.Out,
            PaymentKind = PaymentKind.TruckPayment,
            CashAccountId = 1,
            DriverId = driver.Id,
            ContractId = 1,
            Amount = 90m,
            Currency = "USD",
            Reference = "DRV-PAY-1",
            Description = "پرداخت کرایه راننده"
        }));

        Assert.Equal(0m, await DriverOwedAsync(db, driver.Id));
        var settled = await StatementAsync(db, driver.Id);
        Assert.Equal(0m, settled.Summary.ClosingBalance);
        Assert.Equal(2, settled.Rows.Count(r => !r.IsOpeningBalance));
    }

    [Fact]
    public async Task Empty_Responsibility_With_External_Freight_Is_Rejected()
    {
        await using var db = await SeedAsync();

        foreach (var row in new[]
                 {
                     new LoadingCreateRowViewModel { LoadingDate = new DateTime(2026, 4, 23), TruckId = 1, BillOfLadingNumber = "CMR-E1", LoadedQuantityMt = Qty, LogisticsMode = "free", LogisticsServiceProviderId = 1, FreightRateUsdPerMt = 10m },
                     new LoadingCreateRowViewModel { LoadingDate = new DateTime(2026, 4, 23), TruckId = 1, BillOfLadingNumber = "CMR-E2", LoadedQuantityMt = Qty, LogisticsMode = "driver", DriverName = "Ahmad", FreightRateUsdPerMt = 10m }
                 })
        {
            var controller = NewLoadingController(db);
            var model = CreateModel(CostResponsibility.Buyer, row);
            model.FreightCostResponsibility = null;

            Assert.IsType<ViewResult>(await controller.Create(model));
            Assert.Contains(controller.ModelState[nameof(LoadingCreateViewModel.FreightCostResponsibility)]!.Errors,
                e => e.ErrorMessage.Contains("مسئول کرایه", StringComparison.Ordinal));
        }

        Assert.Empty(await db.LoadingRegisters.ToListAsync());
        Assert.Empty(await db.Drivers.ToListAsync());
        Assert.Empty(await db.ExpenseTransactions.ToListAsync());
    }

    [Fact]
    public async Task Buyer_Freight_Is_Company_Cost_Exactly_Once()
    {
        await using var db = await SeedAsync();
        await CreateProviderLoadingAsync(db, CostResponsibility.Buyer, providerId: 1, rate: 10m);

        // P&L قرارداد (ContractEconomics) کرایهٔ درون‌خطی را از همین aggregator می‌خواند؛ سند رسمی هست ⇒ درون‌خطی کنار می‌رود.
        var agg = await new PurchaseAggregationService(db).AggregateForContractAsync(1, null);
        Assert.Equal(0m, agg.LoadingTransportExpenseUsd);

        var journey = await JourneyAsync(db);
        Assert.Equal(300m, journey.Kpis.TotalExpensesUsd);
        Assert.Equal(300m, journey.ContractTransportExpenseUsd);
        Assert.Equal(300m, journey.ExpenseItems.Sum(e => e.AmountUsd) + journey.LoadingOperationalExpenseUsd);
        Assert.Equal(300m, await ProviderOwedAsync(db, 1));
    }

    [Theory]
    [InlineData(CostResponsibility.Seller)]
    [InlineData(CostResponsibility.Shared)]
    public async Task Seller_Or_Shared_Freight_Is_Neither_Payable_Nor_Company_Cost(CostResponsibility responsibility)
    {
        await using var db = await SeedAsync();
        await CreateDriverLoadingAsync(db, responsibility, "Ahmad", "0700 000 001", rate: 10m);
        await CreateProviderLoadingAsync(db, responsibility, providerId: 1, rate: 10m, reference: "CMR-2");

        var loadings = await db.LoadingRegisters.AsNoTracking().ToListAsync();
        Assert.All(loadings, l => Assert.Equal(300m, l.TransportExpenseUsd)); // معلومات بارگیری می‌ماند
        Assert.Empty(await db.ExpenseTransactions.ToListAsync());

        var agg = await new PurchaseAggregationService(db).AggregateForContractAsync(1, null);
        Assert.Equal(0m, agg.LoadingTransportExpenseUsd);
        Assert.Equal(0m, agg.LoadingRailwayExpenseUsd);

        var journey = await JourneyAsync(db);
        Assert.Equal(0m, journey.Kpis.TotalExpensesUsd);
        Assert.Equal(0m, journey.LoadingOperationalExpenseUsd);
        Assert.Equal(0m, journey.ContractTransportExpenseUsd);
    }

    [Fact]
    public async Task Edit_Provider_To_Driver_Moves_Payable_To_Driver()
    {
        await using var db = await SeedAsync();
        await CreateProviderLoadingAsync(db, CostResponsibility.Buyer, providerId: 1, rate: 10m);

        await EditAsync(db, EditModel(await LoadingAsync(db), m =>
        {
            m.LogisticsMode = "driver";
            m.LogisticsServiceProviderId = null;
            m.DriverName = "Karim";
            m.DriverPhone = "0700 222 333";
        }));

        var loading = await LoadingAsync(db);
        var driver = await db.Drivers.SingleAsync();
        Assert.Equal(driver.Id, loading.DriverId);
        Assert.Null(loading.LogisticsServiceProviderId);
        Assert.Equal(0m, await ProviderOwedAsync(db, 1));
        Assert.Equal(300m, await DriverOwedAsync(db, driver.Id));
        Assert.Equal(driver.Id, Assert.Single(await ActiveExpensesAsync(db)).DriverId);
    }

    [Fact]
    public async Task Edit_Driver_To_Provider_Moves_Payable_To_Provider()
    {
        await using var db = await SeedAsync();
        await CreateDriverLoadingAsync(db, CostResponsibility.Buyer, "Ahmad", "0700 000 001", rate: 10m);
        var driver = await db.Drivers.SingleAsync();

        await EditAsync(db, EditModel(await LoadingAsync(db), m =>
        {
            m.LogisticsMode = "free";
            m.LogisticsServiceProviderId = 2;
        }));

        var loading = await LoadingAsync(db);
        Assert.Null(loading.DriverId);
        Assert.Equal(2, loading.LogisticsServiceProviderId);
        Assert.Equal(0m, await DriverOwedAsync(db, driver.Id));
        Assert.Equal(300m, await ProviderOwedAsync(db, 2));
        Assert.Equal(1, await db.Drivers.CountAsync()); // پروفایل راننده حذف/تکرار نمی‌شود
    }

    [Fact]
    public async Task Edit_External_To_Company_Transport_And_Back()
    {
        await using var db = await SeedAsync(withOwnedAsset: true);
        await CreateProviderLoadingAsync(db, CostResponsibility.Buyer, providerId: 1, rate: 10m);

        // بیرونی ⇒ شخصی: بدهی شرکت خدماتی برمی‌گردد، کرایهٔ داخلی بدون Ledger ساخته می‌شود.
        await EditAsync(db, EditModel(await LoadingAsync(db), m =>
        {
            m.LogisticsMode = "owned";
            m.LogisticsServiceProviderId = null;
            m.OperationalAssetId = 1;
        }));

        Assert.Empty(await ActiveExpensesAsync(db));
        Assert.Equal(0m, await ProviderOwedAsync(db, 1));
        var rent = await db.AssetRentTransactions.AsNoTracking().SingleAsync(r => !r.IsCancelled);
        Assert.Equal(300m, rent.AmountUsd);
        Assert.False(rent.IsPostedToLedger);
        var ownedJourney = await JourneyAsync(db);
        Assert.Equal(300m, ownedJourney.ContractTransportExpenseUsd);
        Assert.Equal(0m, ownedJourney.Kpis.TotalExpensesUsd);

        // شخصی ⇒ بیرونی: کرایهٔ داخلی لغو، بدهی تازه به شرکت خدماتی B.
        await EditAsync(db, EditModel(await LoadingAsync(db), m =>
        {
            m.LogisticsMode = "free";
            m.LogisticsServiceProviderId = 2;
            m.OperationalAssetId = null;
        }));

        Assert.Empty(await db.AssetRentTransactions.Where(r => !r.IsCancelled).ToListAsync());
        Assert.Equal(300m, await ProviderOwedAsync(db, 2));
        var externalJourney = await JourneyAsync(db);
        Assert.Equal(300m, externalJourney.ContractTransportExpenseUsd);
        Assert.Equal(300m, externalJourney.Kpis.TotalExpensesUsd);
        Assert.Empty(externalJourney.InternalTransportCostItems);
    }

    [Fact]
    public async Task Manual_Expense_Editor_Allows_Independent_Expense_Alongside_Automatic_Driver_Freight()
    {
        await using var db = await SeedAsync();
        await CreateDriverLoadingAsync(db, CostResponsibility.Buyer, "Ahmad", "0700 000 001", rate: 10m);
        var loading = await LoadingAsync(db);
        var automatic = Assert.Single(await ActiveExpensesAsync(db));
        var ledgerCountBefore = await db.LedgerEntries.CountAsync();

        var otherExpenseType = new ExpenseType
        {
            Code = "LOAD-OTHER",
            Name = "Loading Other Expense",
            NamePersian = "سایر مصارف بارگیری",
            Category = "Other",
            IsActive = true
        };
        db.ExpenseTypes.Add(otherExpenseType);
        await db.SaveChangesAsync();

        var controller = NewLoadingController(db);
        var result = await controller.EditExpenses(loading.Id, new LoadingExpenseEditViewModel
        {
            Id = loading.Id,
            Lines = [new LoadingExpenseLineInputModel
            {
                ExpenseTypeId = otherExpenseType.Id,
                AmountUsd = 45m,
                PartyType = LoadingExpensePartyType.None
            }]
        });

        Assert.IsType<RedirectToActionResult>(result);
        Assert.True(controller.ModelState.IsValid);
        var line = Assert.Single(await db.LoadingExpenseLines.ToListAsync());
        Assert.Equal(45m, line.AmountUsd);
        Assert.Null(line.ExpenseTransactionId);
        Assert.Equal(automatic.Id, Assert.Single(await ActiveExpensesAsync(db)).Id);
        Assert.Equal(ledgerCountBefore, await db.LedgerEntries.CountAsync());
        Assert.Equal(300m, await DriverOwedAsync(db, automatic.DriverId!.Value));

        var updatedLoading = await db.LoadingRegisters.SingleAsync();
        Assert.Equal(300m, updatedLoading.TransportExpenseUsd);
        Assert.Equal(45m, updatedLoading.OtherExpenseUsd);
    }

    [Fact]
    public async Task Manual_Expense_Editor_Cannot_Duplicate_Automatic_Driver_Freight()
    {
        await using var db = await SeedAsync();
        await CreateDriverLoadingAsync(db, CostResponsibility.Buyer, "Ahmad", "0700 000 001", rate: 10m);
        var loading = await LoadingAsync(db);
        var transportTypeId = (await db.ExpenseTypes.SingleAsync(t => t.Code == "LOAD-TRANSPORT")).Id;
        var before = await db.ExpenseTransactions.CountAsync();

        var controller = NewLoadingController(db);
        await controller.EditExpenses(loading.Id, new LoadingExpenseEditViewModel
        {
            Id = loading.Id,
            Lines = [new LoadingExpenseLineInputModel { ExpenseTypeId = transportTypeId, AmountUsd = 300m, PartyType = LoadingExpensePartyType.ServiceProvider, ServiceProviderId = 1 }]
        });

        Assert.False(controller.ModelState.IsValid);
        Assert.Equal(before, await db.ExpenseTransactions.CountAsync());
        Assert.Empty(await db.LoadingExpenseLines.ToListAsync());
    }

    [Fact]
    public async Task Manual_Expense_Editor_Rejects_Same_Freight_Twice_For_Same_Party()
    {
        await using var db = await SeedAsync();
        await CreateProviderLoadingAsync(db, CostResponsibility.Buyer, providerId: 1, rate: 10m);
        var loading = await LoadingAsync(db);
        var automatic = Assert.Single(await ActiveExpensesAsync(db));

        var controller = NewLoadingController(db);
        await controller.EditExpenses(loading.Id, new LoadingExpenseEditViewModel
        {
            Id = loading.Id,
            Lines =
            [
                // همان سند خودکار که فرم به‌صورت ردیف نشان می‌دهد
                new LoadingExpenseLineInputModel { ExpenseTypeId = automatic.ExpenseTypeId, AmountUsd = 300m, PartyType = LoadingExpensePartyType.ServiceProvider, ServiceProviderId = 1, ExpenseTransactionId = automatic.Id },
                // و همان کرایه دوباره به‌صورت دستی
                new LoadingExpenseLineInputModel { ExpenseTypeId = automatic.ExpenseTypeId, AmountUsd = 300m, PartyType = LoadingExpensePartyType.ServiceProvider, ServiceProviderId = 1 }
            ]
        });

        Assert.False(controller.ModelState.IsValid);
        Assert.Single(await ActiveExpensesAsync(db));
        Assert.Equal(300m, await ProviderOwedAsync(db, 1));
    }

    [Fact]
    public async Task Repair_Legacy_Freight_Party_Posts_Payable_Once_Only_For_Incomplete_Loadings_Of_Contract()
    {
        await using var db = await SeedAsync();
        db.Contracts.Add(new Contract { Id = 2, ContractNumber = "PUR-2", ContractType = ContractType.Purchase, ProductId = 1, CompanyId = 1, ContractDate = new DateTime(2026, 4, 23), QuantityMt = 500m });
        LoadingRegister Wagon(int id, int contractId, CostResponsibility? responsibility) => new()
        {
            Id = id,
            ContractId = contractId,
            ProductId = 1,
            TransportType = LoadingTransportType.Wagon,
            WagonNumber = $"W-{id}",
            LoadingDate = new DateTime(2026, 4, 23),
            LoadedQuantityMt = 48m,
            FreightCostResponsibility = responsibility,
            FreightRateUsdPerMt = 52m,
            ChargeableQuantityMt = 50m,
            RailwayRateUsd = 52m,
            RailwayExpenseUsd = 2600m
        };
        db.LoadingRegisters.AddRange(
            Wagon(1, 1, CostResponsibility.Buyer),
            Wagon(2, 1, CostResponsibility.Buyer),
            Wagon(3, 1, CostResponsibility.Seller),
            Wagon(4, 2, CostResponsibility.Buyer));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var provider = await db.ServiceProviders.AsNoTracking().SingleAsync(p => p.Id == 2);

        var dryRun = await NewLoadingController(db).RepairLegacyFreightPartyAsync(1, provider, dryRun: true);
        Assert.Equal([1, 2], dryRun.Select(r => r.LoadingId));
        Assert.Empty(await db.ExpenseTransactions.ToListAsync());

        var repaired = await NewLoadingController(db).RepairLegacyFreightPartyAsync(1, provider, dryRun: false);
        db.ChangeTracker.Clear();
        Assert.Equal(5200m, repaired.Sum(r => r.FreightUsd));
        var expenses = await ActiveExpensesAsync(db);
        Assert.Equal(2, expenses.Count);
        Assert.All(expenses, e => { Assert.Equal(2, e.ServiceProviderId); Assert.Equal(1, e.ContractId); Assert.Equal(2600m, e.AmountUsd); });
        Assert.Equal(5200m, await ProviderOwedAsync(db, 2));
        var loadings = await db.LoadingRegisters.AsNoTracking().OrderBy(l => l.Id).ToListAsync();
        Assert.Equal([2, 2, null, null], loadings.Select(l => l.LogisticsServiceProviderId));
        Assert.All(loadings, l => { Assert.Equal(48m, l.LoadedQuantityMt); Assert.Equal(2600m, l.RailwayExpenseUsd); });

        // دوباره ⇒ هیچ کاری.
        Assert.Empty(await NewLoadingController(db).RepairLegacyFreightPartyAsync(1, provider, dryRun: false));
        Assert.Equal(2, (await ActiveExpensesAsync(db)).Count);
        Assert.Equal(5200m, await ProviderOwedAsync(db, 2));
    }

    // ── helpers ─────────────────────────────────────────────────────────

    private static async Task<PTGOilSystem.Web.Models.ContractJourney.ContractJourneyDetailsViewModel> JourneyAsync(ApplicationDbContext db)
    {
        db.ChangeTracker.Clear();
        var controller = new ContractJourneyController(db, new StockService(db))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        var view = Assert.IsType<ViewResult>(await controller.Details(1, tab: PTGOilSystem.Web.Models.ContractJourney.ContractJourneyTabs.Details.Costs));
        return Assert.IsType<PTGOilSystem.Web.Models.ContractJourney.ContractJourneyDetailsViewModel>(view.Model);
    }

    private static async Task<ApplicationDbContext> SeedAsync(bool withOwnedAsset = false)
    {
        var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
        db.Currencies.Add(new Currency { Id = 1, Code = "USD", Name = "US Dollar", Symbol = "$", IsActive = true });
        db.CashAccounts.Add(new CashAccount { Id = 1, Code = "BANK-USD", Name = "Main USD Bank", AccountType = CashAccountType.Bank, Currency = "USD", IsActive = true });
        db.Products.Add(new Product { Id = 1, Code = "GO", Name = "Gas Oil", IsActive = true });
        db.Companies.Add(new Company { Id = 1, Code = "PTG", Name = "Petro Trade Group", IsActive = true });
        db.ServiceProviders.AddRange(
            new ServiceProvider { Id = 1, Code = "LOG-A", Name = "Logistics A", ProviderType = ServiceProviderType.TransportCompany, IsActive = true },
            new ServiceProvider { Id = 2, Code = "LOG-B", Name = "Logistics B", ProviderType = ServiceProviderType.TransportCompany, IsActive = true });
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

        await db.SaveChangesAsync();
        return db;
    }

    private static async Task CreateProviderLoadingAsync(ApplicationDbContext db, CostResponsibility responsibility, int providerId, decimal rate, string reference = "CMR-1")
        => Assert.IsType<RedirectToActionResult>(await NewLoadingController(db).Create(CreateModel(responsibility, new LoadingCreateRowViewModel
        {
            LoadingDate = new DateTime(2026, 4, 23),
            TruckId = 1,
            BillOfLadingNumber = reference,
            LoadedQuantityMt = Qty,
            LogisticsMode = "free",
            LogisticsServiceProviderId = providerId,
            FreightRateUsdPerMt = rate
        })));

    private static async Task CreateDriverLoadingAsync(ApplicationDbContext db, CostResponsibility responsibility, string name, string phone, decimal rate)
        => Assert.IsType<RedirectToActionResult>(await NewLoadingController(db).Create(CreateModel(responsibility, new LoadingCreateRowViewModel
        {
            LoadingDate = new DateTime(2026, 4, 23),
            TruckId = 1,
            BillOfLadingNumber = "CMR-1",
            LoadedQuantityMt = Qty,
            LogisticsMode = "driver",
            DriverName = name,
            DriverPhone = phone,
            FreightRateUsdPerMt = rate
        })));

    private static LoadingCreateViewModel CreateModel(CostResponsibility responsibility, LoadingCreateRowViewModel row)
        => new()
        {
            ContractId = 1,
            ProductId = 1,
            TransportType = LoadingTransportType.Truck,
            RecordFreight = true,
            FreightCostResponsibility = responsibility,
            Rows = [row]
        };

    private static Task<LoadingRegister> LoadingAsync(ApplicationDbContext db)
        => db.LoadingRegisters.AsNoTracking().SingleAsync();

    private static LoadingEditViewModel EditModel(LoadingRegister loading, Action<LoadingEditViewModel> change)
    {
        var model = new LoadingEditViewModel
        {
            Id = loading.Id,
            Version = loading.Version,
            LoadingDate = loading.LoadingDate,
            LoadedQuantityMt = loading.LoadedQuantityMt,
            LoadingPriceUsd = loading.LoadingPriceUsd,
            BillOfLadingNumber = loading.BillOfLadingNumber,
            TransitNumber = loading.TransitNumber,
            DriverName = loading.DriverName,
            DriverPhone = loading.DriverPhone,
            LogisticsCompanyName = loading.LogisticsCompanyName,
            FreightCostResponsibility = loading.FreightCostResponsibility,
            FreightRateUsdPerMt = loading.FreightRateUsdPerMt,
            LogisticsServiceProviderId = loading.LogisticsServiceProviderId,
            Notes = loading.Notes
        };
        change(model);
        return model;
    }

    private static async Task EditAsync(ApplicationDbContext db, LoadingEditViewModel model)
    {
        db.ChangeTracker.Clear();
        var controller = NewLoadingController(db);
        var result = await controller.Edit(model.Id, model);
        Assert.True(result is RedirectToActionResult,
            string.Join(" | ", controller.ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage)));
        db.ChangeTracker.Clear();
    }

    private static Task<List<ExpenseTransaction>> ActiveExpensesAsync(ApplicationDbContext db)
        => db.ExpenseTransactions.AsNoTracking().Where(e => !e.IsCancelled).ToListAsync();

    // طلب خالص طرف‌حساب از دفتر کل: Credit (کرایه) منهای Debit (برگشت/پرداخت).
    private static async Task<decimal> ProviderOwedAsync(ApplicationDbContext db, int providerId)
        => (await db.LedgerEntries.AsNoTracking().Where(l => l.ServiceProviderId == providerId).ToListAsync())
            .Sum(l => l.Side == LedgerSide.Credit ? l.AmountUsd : -l.AmountUsd);

    private static async Task<decimal> DriverOwedAsync(ApplicationDbContext db, int driverId)
        => (await db.LedgerEntries.AsNoTracking().Where(l => l.DriverId == driverId).ToListAsync())
            .Sum(l => l.Side == LedgerSide.Credit ? l.AmountUsd : -l.AmountUsd);

    private static PartyStatementReadService BuildStatementService(ApplicationDbContext db)
        => new(db, new PartyStatementPolicyResolver(), new CompanyFlowDirectionResolver(), new CompanyFlowBalanceService(), Options.Create(new PartyStatementOptions()), new PartyDirectory(db));

    private static Task<PartyStatementResult> StatementAsync(ApplicationDbContext db, int driverId)
        => BuildStatementService(db).GetStatementAsync(
            new PartyRef(PartyStatementPartyType.Driver, driverId),
            new PartyStatementFilter { IncludeOperationalColumns = false });

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
