using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PTGOilSystem.Web.Controllers;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Helpers;
using PTGOilSystem.Web.Models.Contracts;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.Expenses;
using PTGOilSystem.Web.Models.Loading;
using PTGOilSystem.Web.Services;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// قرارداد چندتأمین‌کننده با مقدار و قیمت هر تأمین‌کننده: یک قرارداد، بارگیری بدون تأمین‌کننده،
/// طلب به نسبت ارزش خرید هر نفر، و مصرف قرارداد با دو روش «بر اساس تُن» و «مبلغ ثابت».
/// </summary>
public sealed class MultiSupplierContractTermsTests
{
    private const int Ahmad = 1;
    private const int Mahmoud = 2;

    [Fact]
    public void PurchaseTotals_AreSumAndWeightedAverage()
    {
        var totals = ContractSupplierScope.PurchaseTotals(Terms())!.Value;

        Assert.Equal(150m, totals.QuantityMt);
        Assert.Equal(76000m, totals.ValueUsd);
        Assert.Equal(506.6667m, totals.AveragePriceUsd);
    }

    [Fact]
    public void LoadingWithoutSupplier_SplitsPayableByPurchaseValue_WithoutDoubleCounting()
    {
        var contract = MultiContract();
        var loading = Loading(supplierId: null, quantityMt: 150m, priceUsd: 506.6667m);

        Assert.True(SupplierLoadingLedger.IsPostable(loading, contract));
        var rows = SupplierLoadingLedger.CreateAll(loading, contract);

        Assert.Equal(2, rows.Count);
        Assert.Equal(LoadingRubSettlement.CalculateLoadingValueUsd(150m, 506.6667m), rows.Sum(r => r.AmountUsd));
        Assert.Equal(50000m, decimal.Round(rows.Single(r => r.SupplierId == Ahmad).AmountUsd, 2));
        Assert.Equal(26000m, decimal.Round(rows.Single(r => r.SupplierId == Mahmoud).AmountUsd, 2));
        Assert.All(rows, r => Assert.Equal(contract.Id, r.ContractId));
    }

    [Fact]
    public void LoadingWithExplicitSupplier_StillCreditsOnlyThatSupplier()
    {
        var rows = SupplierLoadingLedger.CreateAll(Loading(supplierId: Mahmoud, quantityMt: 10m, priceUsd: 520m), MultiContract());

        var row = Assert.Single(rows);
        Assert.Equal(Mahmoud, row.SupplierId);
        Assert.Equal(5200m, row.AmountUsd);
    }

    [Fact]
    public void ShareRow_Resync_UpdatesOnlyItsOwnShare()
    {
        var contract = MultiContract();
        var loading = Loading(supplierId: null, quantityMt: 15m, priceUsd: 500m);
        var entry = new LedgerEntry { SupplierId = Mahmoud, AmountUsd = 1m, SourceAmount = 1m };

        loading.LoadingPriceUsd = 760m; // 15 × 760 = 11,400 → Mahmoud 26,000/76,000
        Assert.True(SupplierLoadingLedger.ApplySnapshot(entry, loading, contract));

        Assert.Equal(3900m, entry.AmountUsd);
        Assert.Equal(3900m, entry.SourceAmount);
    }

    [Fact]
    public void SplitAmount_AlwaysSumsToTheAmount()
    {
        var shares = ContractSupplierScope.PurchaseShares(MultiContract())!;
        foreach (var amount in new[] { 0.0001m, 1m, 760.0001m, 12345.6789m })
        {
            Assert.Equal(amount, ContractSupplierScope.SplitAmount(amount, shares).Sum(p => p.Amount));
        }
    }

    [Fact]
    public async Task CreateContract_TwoSuppliers_IsOneContractWithTermsAndDerivedTotals()
    {
        await using var db = NewDb();
        SeedLookups(db);
        await db.SaveChangesAsync();
        var controller = new ContractsController(db, new AuditService(db)) { TempData = TempData() };

        var result = await controller.Create(new ContractFormViewModel
        {
            ContractName = "001",
            ContractType = ContractType.Purchase,
            Status = ContractStatus.Active,
            CompanyId = 1,
            ProductId = 1,
            UnitId = 1,
            ContractDate = new DateTime(2026, 11, 1),
            Currency = "USD",
            SupplierId = Ahmad,
            PrimarySupplierQuantityMt = 100m,
            PrimarySupplierUnitPriceUsd = 500m,
            AdditionalSupplierIds = [Mahmoud],
            AdditionalSupplierQuantitiesMt = [50m],
            AdditionalSupplierUnitPricesUsd = [520m]
        });

        Assert.True(controller.ModelState.IsValid,
            string.Join(" | ", controller.ModelState.SelectMany(kv => kv.Value!.Errors.Select(e => $"{kv.Key}: {e.ErrorMessage}"))));
        Assert.IsAssignableFrom<RedirectToActionResult>(result);

        var contract = await db.Contracts.Include(c => c.ContractSuppliers).SingleAsync();
        Assert.Null(contract.SupplierId);
        Assert.Equal(150m, contract.QuantityMt);
        Assert.Equal(506.6667m, contract.ManualFinalPriceUsd);
        Assert.Equal(2, contract.ContractSuppliers.Count);
        var ahmad = contract.ContractSuppliers.Single(s => s.SupplierId == Ahmad);
        var mahmoud = contract.ContractSuppliers.Single(s => s.SupplierId == Mahmoud);
        Assert.Equal((100m, 500m, 50000m), (ahmad.QuantityMt!.Value, ahmad.UnitPriceUsd!.Value, ahmad.PurchaseValueUsd!.Value));
        Assert.Equal((50m, 520m, 26000m), (mahmoud.QuantityMt!.Value, mahmoud.UnitPriceUsd!.Value, mahmoud.PurchaseValueUsd!.Value));
    }

    [Fact]
    public async Task CreateContract_MultiSupplierWithoutQuantityOrPrice_IsRejected()
    {
        await using var db = NewDb();
        SeedLookups(db);
        await db.SaveChangesAsync();
        var controller = new ContractsController(db, new AuditService(db)) { TempData = TempData() };

        await controller.Create(new ContractFormViewModel
        {
            ContractName = "002",
            ContractType = ContractType.Purchase,
            Status = ContractStatus.Active,
            CompanyId = 1,
            ProductId = 1,
            UnitId = 1,
            ContractDate = new DateTime(2026, 11, 1),
            Currency = "USD",
            SupplierId = Ahmad,
            PrimarySupplierQuantityMt = 100m,
            PrimarySupplierUnitPriceUsd = 500m,
            AdditionalSupplierIds = [Mahmoud]
        });

        Assert.False(controller.ModelState.IsValid);
        Assert.Equal(0, await db.Contracts.CountAsync());
    }

    [Fact]
    public async Task Import100Loadings_WithoutSupplier_AllOnOneContract_AndSupplierDebtMatchesPurchaseValue()
    {
        await using var db = NewDb();
        SeedLookups(db);
        var contract = MultiContract();
        db.Contracts.Add(contract);
        await db.SaveChangesAsync();

        var model = new LoadingCreateViewModel
        {
            ContractId = contract.Id,
            SelectedContractIds = [contract.Id],
            ProductId = 1,
            TransportType = LoadingTransportType.Wagon,
            LoadingDate = new DateTime(2026, 11, 2),
            Rows = Enumerable.Range(1, 100)
                .Select(i => new LoadingCreateRowViewModel
                {
                    RowKey = $"row_{i}",
                    ContractId = contract.Id,
                    LoadingDate = new DateTime(2026, 11, 2),
                    BillOfLadingNumber = $"CMR-{i:000}",
                    WagonNumber = $"WG-{i:000}",
                    LoadedQuantityMt = 1.5m,
                    LoadingPriceUsd = 506.6667m
                })
                .ToList()
        };
        var screening = await LoadingExcelImportController.ScreenDuplicateRowsAsync(db, model, CancellationToken.None);
        Assert.Equal(100, screening.AcceptedRows.Count);
        model.Rows = screening.AcceptedRows;

        var loadingController = new LoadingController(db, new AuditService(db), NullLogger<LoadingController>.Instance) { TempData = TempData() };
        var result = await loadingController.Create(model);
        Assert.True(loadingController.ModelState.IsValid,
            string.Join(" | ", loadingController.ModelState.SelectMany(kv => kv.Value!.Errors.Select(e => $"{kv.Key}: {e.ErrorMessage}"))));
        Assert.IsType<RedirectToActionResult>(result);

        var loadings = await db.LoadingRegisters.AsNoTracking().ToListAsync();
        Assert.Equal(100, loadings.Count);
        Assert.All(loadings, l => Assert.Equal(contract.Id, l.ContractId));
        Assert.All(loadings, l => Assert.Null(l.SupplierId));
        Assert.Equal(1, await db.Contracts.CountAsync());

        var ledger = await db.LedgerEntries.AsNoTracking()
            .Where(l => l.SourceType == SupplierLoadingLedger.SourceType)
            .ToListAsync();
        Assert.Equal(200, ledger.Count);
        var loadedValue = loadings.Sum(l => LoadingRubSettlement.CalculateLoadingValueUsd(l.LoadedQuantityMt, l.LoadingPriceUsd)!.Value);
        Assert.Equal(loadedValue, ledger.Sum(l => l.AmountUsd)); // بدهی دوباره ساخته نمی‌شود
        Assert.InRange(ledger.Where(l => l.SupplierId == Ahmad).Sum(l => l.AmountUsd), 49999.95m, 50000.05m);
        Assert.InRange(ledger.Where(l => l.SupplierId == Mahmoud).Sum(l => l.AmountUsd), 25999.95m, 26000.05m);
        Assert.Equal(1, await db.Contracts.CountAsync());
    }

    [Fact]
    public async Task ContractExpenses_PerTonAndFixed_AreOneRecordEach_TotalAndAveragePerTon()
    {
        await using var db = NewDb();
        SeedLookups(db);
        db.ExpenseTypes.Add(new ExpenseType { Id = 1, Code = "TRN", Name = "Transport", NamePersian = "کرایه حمل‌ونقل" });
        db.Currencies.Add(new Currency { Id = 1, Code = "USD", Name = "US Dollar", Symbol = "$", IsActive = true });
        var contract = MultiContract();
        db.Contracts.Add(contract);
        await db.SaveChangesAsync();
        var expenses = new ExpensesController(db, new AuditService(db), NullLogger<ExpensesController>.Instance) { TempData = TempData() };

        var form = Assert.IsType<ExpenseCreateViewModel>(Assert.IsType<ViewResult>(await expenses.Create(contractId: contract.Id)).Model);
        Assert.Equal(contract.Id, form.ContractId);
        Assert.Equal(150m, form.BasisQuantityMt); // پیش‌فرض = مقدار قرارداد

        var perTonResult = await expenses.Create(new ExpenseCreateViewModel
        {
            ExpenseTypeId = 1,
            ContractId = contract.Id,
            ExpenseDate = new DateTime(2026, 11, 3),
            Currency = "USD",
            Description = "کرایه حمل‌ونقل",
            CalculationMethod = ExpenseCalculationMethod.PerTon,
            BasisQuantityMt = 150m,
            RatePerMt = 2m,
            Amount = 999m // سرور مبلغ را از مقدار × نرخ می‌سازد
        });
        Assert.True(expenses.ModelState.IsValid,
            string.Join(" | ", expenses.ModelState.SelectMany(kv => kv.Value!.Errors.Select(e => $"{kv.Key}: {e.ErrorMessage}"))));
        Assert.IsType<RedirectToActionResult>(perTonResult);
        Assert.IsType<RedirectToActionResult>(await expenses.Create(new ExpenseCreateViewModel
        {
            ExpenseTypeId = 1,
            ContractId = contract.Id,
            ExpenseDate = new DateTime(2026, 11, 3),
            Currency = "USD",
            Description = "کرایه حمل‌ونقل",
            CalculationMethod = ExpenseCalculationMethod.FixedAmount,
            BasisQuantityMt = 5m,
            RatePerMt = 5m,
            Amount = 100m
        }));

        var rows = await db.ExpenseTransactions.AsNoTracking().OrderBy(e => e.Id).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Equal((ExpenseCalculationMethod.PerTon, 150m, 2m, 300m),
            (rows[0].CalculationMethod!.Value, rows[0].BasisQuantityMt!.Value, rows[0].RatePerMt!.Value, rows[0].AmountUsd));
        Assert.Equal(ExpenseCalculationMethod.FixedAmount, rows[1].CalculationMethod);
        Assert.Null(rows[1].BasisQuantityMt);
        Assert.Null(rows[1].RatePerMt);
        Assert.Equal(100m, rows[1].AmountUsd);

        var total = rows.Sum(e => e.AmountUsd);
        Assert.Equal(400m, total);
        Assert.Equal(2.6667m, decimal.Round(total / contract.QuantityMt, 4, MidpointRounding.AwayFromZero));
        Assert.All(rows, e => Assert.Equal(contract.Id, e.ContractId));

        // مصرف قرارداد هرگز طلب تأمین‌کننده نمی‌سازد.
        Assert.DoesNotContain(await db.LedgerEntries.AsNoTracking().ToListAsync(),
            l => l.SupplierId == Ahmad || l.SupplierId == Mahmoud);
        // مقدار مبنای مصرف مقدار قرارداد را تغییر نمی‌دهد.
        Assert.Equal(150m, (await db.Contracts.AsNoTracking().SingleAsync()).QuantityMt);
    }

    [Fact]
    public async Task PerTonExpense_WithoutRate_IsRejected()
    {
        await using var db = NewDb();
        SeedLookups(db);
        db.ExpenseTypes.Add(new ExpenseType { Id = 1, Code = "TRN", Name = "Transport", NamePersian = "کرایه" });
        db.Currencies.Add(new Currency { Id = 1, Code = "USD", Name = "US Dollar", Symbol = "$", IsActive = true });
        db.Contracts.Add(MultiContract());
        await db.SaveChangesAsync();
        var expenses = new ExpensesController(db, new AuditService(db), NullLogger<ExpensesController>.Instance) { TempData = TempData() };

        var result = await expenses.Create(new ExpenseCreateViewModel
        {
            ExpenseTypeId = 1,
            ContractId = 1,
            ExpenseDate = new DateTime(2026, 11, 3),
            Currency = "USD",
            Description = "کرایه حمل‌ونقل",
            CalculationMethod = ExpenseCalculationMethod.PerTon,
            BasisQuantityMt = 10m
        });

        Assert.IsType<ViewResult>(result);
        Assert.True(expenses.ModelState.ContainsKey(nameof(ExpenseCreateViewModel.RatePerMt)));
        Assert.Equal(0, await db.ExpenseTransactions.CountAsync());
    }

    private static List<ContractSupplier> Terms()
        =>
        [
            new ContractSupplier { Id = 1, ContractId = 1, SupplierId = Ahmad, QuantityMt = 100m, UnitPriceUsd = 500m },
            new ContractSupplier { Id = 2, ContractId = 1, SupplierId = Mahmoud, QuantityMt = 50m, UnitPriceUsd = 520m }
        ];

    private static Contract MultiContract()
        => new()
        {
            Id = 1,
            ContractName = "001",
            ContractNumber = "P-001",
            ContractType = ContractType.Purchase,
            Status = ContractStatus.Active,
            CompanyId = 1,
            ProductId = 1,
            SupplierId = null,
            ContractDate = new DateTime(2026, 11, 1),
            PricingMethod = PricingMethod.ManualFinalPrice,
            ManualFinalPriceUsd = 506.6667m,
            Currency = "USD",
            QuantityMt = 150m,
            ContractSuppliers = Terms()
        };

    private static LoadingRegister Loading(int? supplierId, decimal quantityMt, decimal priceUsd)
        => new()
        {
            Id = 42,
            ContractId = 1,
            SupplierId = supplierId,
            ProductId = 1,
            LoadingDate = new DateTime(2026, 11, 2),
            LoadedQuantityMt = quantityMt,
            LoadingPriceUsd = priceUsd,
            SettlementCurrencyCode = "USD"
        };

    private static void SeedLookups(ApplicationDbContext db)
    {
        db.Companies.Add(new Company { Id = 1, Code = "PTG", Name = "PTG", IsActive = true });
        db.Products.Add(new Product { Id = 1, Code = "GO", Name = "Gas Oil", IsActive = true });
        db.Units.Add(new Unit { Id = 1, Code = "MT", Name = "Metric ton", IsActive = true });
        db.Suppliers.AddRange(
            new Supplier { Id = Ahmad, Name = "احمد", IsActive = true },
            new Supplier { Id = Mahmoud, Name = "محمود", IsActive = true });
    }

    private static TempDataDictionary TempData()
        => new(new DefaultHttpContext(), new NullTempDataProvider());

    private static ApplicationDbContext NewDb()
        => new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private sealed class NullTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();

        public void SaveTempData(HttpContext context, IDictionary<string, object> values)
        {
        }
    }
}
