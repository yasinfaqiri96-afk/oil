using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PTGOilSystem.Web.Controllers;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.Loading;
using PTGOilSystem.Web.Models.PartyStatements;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.CompanyFlow;
using PTGOilSystem.Web.Services.PartyStatements;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// بدهی تأمین‌کننده از بارگیریِ دالری. پیش از این فقط بارگیری روبلیِ قفل‌شده سطر دفتر می‌ساخت،
/// بنابراین برای قرارداد دالری مانده و صورت‌حساب تأمین‌کننده صفر می‌ماند در حالی که پرداخت‌ها
/// سطر دفتر داشتند (مانده منفیِ ساختگی). حالا بارگیری دالریِ قیمت‌دار هم دقیقاً یک سطر Credit دارد.
///
/// قرارداد ذخیره‌سازی دست‌نخورده است: خرید در Storage همچنان Credit و پرداخت همچنان Debit؛
/// وارونه‌سازی فقط در لایهٔ نمایشِ صورت‌حساب (ReverseLegacyLedgerSides) انجام می‌شود.
/// </summary>
public class SupplierUsdLoadingLedgerTests
{
    [Fact]
    public async Task UsdLoading_Creates_Exactly_One_Ledger_Row_Stored_As_Credit()
    {
        await using var db = NewDb();
        SeedUsdContract(db);

        var result = await NewLoadingController(db).Create(UsdCreateModel(NewUsdRow()));

        Assert.IsType<RedirectToActionResult>(result);
        var loading = await db.LoadingRegisters.SingleAsync();
        var rows = await LoadingRowsAsync(db, loading.Id);
        Assert.Single(rows);
        Assert.Equal(LedgerSide.Credit, rows[0].Side);
        Assert.Equal(1, rows[0].SupplierId);
        Assert.Equal(loading.ContractId, rows[0].ContractId);
        Assert.Equal(loading.LoadingDate.Date, rows[0].EntryDate);
        Assert.Equal("USD", rows[0].SourceCurrencyCode);
        Assert.Equal(1m, rows[0].AppliedFxRateToUsd);
    }

    [Fact]
    public async Task Ledger_Amount_Equals_Quantity_Times_Unit_Price()
    {
        await using var db = NewDb();
        SeedUsdContract(db);
        var row = NewUsdRow();
        row.LoadedQuantityMt = 12.5m;
        row.LoadingPriceUsd = 137.25m;

        await NewLoadingController(db).Create(UsdCreateModel(row));

        var entry = (await LoadingRowsAsync(db, (await db.LoadingRegisters.SingleAsync()).Id)).Single();
        Assert.Equal(1_715.625m, entry.AmountUsd); // 12.5 × 137.25
        Assert.Equal(entry.AmountUsd, entry.SourceAmount);
    }

    [Fact]
    public async Task Statement_Shows_Loading_In_Debit_Column_And_Balance_Goes_Negative()
    {
        await using var db = NewDb();
        SeedUsdContract(db);
        await NewLoadingController(db).Create(UsdCreateModel(NewUsdRow()));

        var statement = await BuildStatementAsync(db);

        var row = Assert.Single(statement.Rows.Where(r => !r.IsOpeningBalance));
        Assert.Equal(1_000m, row.ReceiptBase); // Credit ذخیره‌شده در نمایش Debit می‌شود
        Assert.Null(row.OutflowBase);
        Assert.Equal(-1_000m, statement.Summary.ClosingBalance);
    }

    [Fact]
    public async Task Balance_Is_Credit_Minus_Debit_After_Supplier_Payment()
    {
        await using var db = NewDb();
        SeedUsdContract(db);
        await NewLoadingController(db).Create(UsdCreateModel(NewUsdRow()));
        SeedSupplierPaymentLedger(db, amountUsd: 400m);

        var statement = await BuildStatementAsync(db);

        // نمایش: بارگیری Debit 1000، پرداخت Credit 400 → مانده = 400 − 1000
        Assert.Equal(1_000m, statement.Summary.TotalReceipt);
        Assert.Equal(400m, statement.Summary.TotalOutflow);
        Assert.Equal(-600m, statement.Summary.ClosingBalance);
    }

    [Fact]
    public async Task EditPrice_Updates_The_Same_Row_Instead_Of_Adding_A_Second()
    {
        await using var db = NewDb();
        SeedUsdContract(db);
        await NewLoadingController(db).Create(UsdCreateModel(NewUsdRow()));
        var loadingId = (await db.LoadingRegisters.SingleAsync()).Id;
        var idBefore = (await LoadingRowsAsync(db, loadingId)).Single().Id;

        var result = await NewLoadingController(db).EditPrice(loadingId, new LoadingPriceEditViewModel
        {
            Id = loadingId,
            LoadingPriceUsd = 150m
        });

        Assert.IsType<RedirectToActionResult>(result);
        var rows = await LoadingRowsAsync(db, loadingId);
        Assert.Single(rows);
        Assert.Equal(idBefore, rows[0].Id);
        Assert.Equal(1_500m, rows[0].AmountUsd); // 10 × 150
    }

    [Fact]
    public async Task Edit_Quantity_Updates_The_Ledger_Amount()
    {
        await using var db = NewDb();
        SeedUsdContract(db);
        await NewLoadingController(db).Create(UsdCreateModel(NewUsdRow()));
        var loading = await db.LoadingRegisters.SingleAsync();

        var result = await NewLoadingController(db).Edit(loading.Id, new LoadingEditViewModel
        {
            Id = loading.Id,
            LoadedQuantityMt = 8m,
            LoadingPriceUsd = 100m,
            LoadingDate = loading.LoadingDate
        });

        Assert.IsType<RedirectToActionResult>(result);
        var rows = await LoadingRowsAsync(db, loading.Id);
        Assert.Single(rows);
        Assert.Equal(800m, rows[0].AmountUsd); // 8 × 100
    }

    [Fact]
    public async Task Contract_Reprice_Creates_The_Missing_Row_For_A_Loading_Priced_Later()
    {
        await using var db = NewDb();
        SeedUsdContract(db);
        SeedUnpricedUsdLoading(db);
        Assert.Empty(await LoadingRowsAsync(db, LoadingId));

        var contract = await db.Contracts.SingleAsync();
        contract.PricingMethod = PricingMethod.ManualFinalPrice;
        contract.ManualFinalPriceUsd = 120m;
        await db.SaveChangesAsync();

        await NewContractsController(db).RepricePurchaseLoadings(contract.Id);

        var rows = await LoadingRowsAsync(db, LoadingId);
        Assert.Single(rows);
        Assert.Equal(1_200m, rows[0].AmountUsd); // 10 × 120
        Assert.Equal(LedgerSide.Credit, rows[0].Side);
    }

    [Fact]
    public async Task Backfill_DryRun_Writes_Nothing()
    {
        await using var db = NewDb();
        SeedUsdContract(db);
        SeedPricedUsdLoadingWithoutLedger(db);

        var result = Assert.IsType<OkObjectResult>(await NewMaintenanceController(db).BackfillSupplierUsdLoadingLedger());

        Assert.Equal(1, ReadInt(result.Value, "candidates"));
        Assert.Equal(1_000m, ReadDecimal(result.Value, "totalUsd"));
        Assert.Empty(await LoadingRowsAsync(db, LoadingId));
    }

    [Fact]
    public async Task Backfill_Commit_Is_Idempotent()
    {
        await using var db = NewDb();
        SeedUsdContract(db);
        SeedPricedUsdLoadingWithoutLedger(db);

        var first = Assert.IsType<OkObjectResult>(await NewMaintenanceController(db).BackfillSupplierUsdLoadingLedger(commit: true));
        Assert.Equal(1, ReadInt(first.Value, "created"));

        var second = Assert.IsType<OkObjectResult>(await NewMaintenanceController(db).BackfillSupplierUsdLoadingLedger(commit: true));
        Assert.Equal(0, ReadInt(second.Value, "created"));

        var rows = await LoadingRowsAsync(db, LoadingId);
        Assert.Single(rows);
        Assert.Equal(1_000m, rows[0].AmountUsd);
        Assert.Equal(LedgerSide.Credit, rows[0].Side);
    }

    [Fact]
    public async Task Backfill_Skips_Unpriced_And_Rub_Loadings()
    {
        await using var db = NewDb();
        SeedUsdContract(db);
        SeedUnpricedUsdLoading(db);
        db.LoadingRegisters.Add(new LoadingRegister
        {
            Id = LoadingId + 1,
            ContractId = 1,
            ProductId = 1,
            LoadingDate = LoadingDate,
            LoadedQuantityMt = 10m,
            LoadingPriceUsd = 100m,
            SettlementCurrencyCode = "RUB",
            RubRateStatus = RubSettlementRateStatus.Pending
        });
        await db.SaveChangesAsync();

        var result = Assert.IsType<OkObjectResult>(await NewMaintenanceController(db).BackfillSupplierUsdLoadingLedger(commit: true));

        Assert.Equal(0, ReadInt(result.Value, "created"));
        Assert.Empty(await db.LedgerEntries.Where(l => l.SourceType == "Loading").ToListAsync());
    }

    private const int LoadingId = 40;
    private static readonly DateTime LoadingDate = new(2026, 6, 3);

    private static ApplicationDbContext NewDb()
        => new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static async Task<List<LedgerEntry>> LoadingRowsAsync(ApplicationDbContext db, int loadingId)
        => await db.LedgerEntries
            .AsNoTracking()
            .Where(l => l.SourceType == "Loading" && l.SourceId == loadingId)
            .ToListAsync();

    private static async Task<PartyStatementResult> BuildStatementAsync(ApplicationDbContext db)
        => await new PartyStatementReadService(
                db,
                new PartyStatementPolicyResolver(),
                new CompanyFlowDirectionResolver(),
                new CompanyFlowBalanceService(),
                Options.Create(new PartyStatementOptions()))
            .GetStatementAsync(
                new PartyRef(PartyStatementPartyType.Supplier, 1),
                new PartyStatementFilter());

    private static void SeedUsdContract(ApplicationDbContext db)
    {
        db.Units.Add(new Unit { Id = 1, Code = "MT", Name = "Metric Ton", Symbol = "MT", IsActive = true });
        db.Products.Add(new Product { Id = 1, Code = "GO", Name = "Gas Oil", UnitId = 1, IsActive = true });
        db.Companies.Add(new Company { Id = 1, Code = "PTG", Name = "PTG", IsActive = true });
        db.Suppliers.Add(new Supplier { Id = 1, Name = "Supplier A", IsActive = true });
        db.Locations.Add(new PTGOilSystem.Web.Models.Entities.Location { Id = 1, Name = "BNK", Kind = "Origin" });
        db.Contracts.Add(new Contract
        {
            Id = 1,
            ContractNumber = "PUR-USD-1",
            ContractType = ContractType.Purchase,
            Status = ContractStatus.Active,
            ProductId = 1,
            UnitId = 1,
            CompanyId = 1,
            SupplierId = 1,
            ContractDate = new DateTime(2026, 6, 1),
            QuantityMt = 500m,
            PricingMethod = PricingMethod.Fixed,
            UnitPriceUsd = 100m,
            SettlementCurrencyCode = "USD",
            RubRatePolicy = RubSettlementRatePolicy.NotApplicable
        });
        db.SaveChanges();
    }

    // بارگیری دالریِ بی‌قیمت — تا وقتی قیمت ندارد سطر دفتر هم ندارد.
    private static void SeedUnpricedUsdLoading(ApplicationDbContext db)
    {
        db.LoadingRegisters.Add(new LoadingRegister
        {
            Id = LoadingId,
            ContractId = 1,
            ProductId = 1,
            LoadingDate = LoadingDate,
            LoadedQuantityMt = 10m,
            SettlementCurrencyCode = "USD"
        });
        db.SaveChanges();
    }

    // بارگیری دالریِ قیمت‌دارِ قدیمی که پیش از این اصلاح ثبت شده و سطر دفتر ندارد.
    private static void SeedPricedUsdLoadingWithoutLedger(ApplicationDbContext db)
    {
        db.LoadingRegisters.Add(new LoadingRegister
        {
            Id = LoadingId,
            ContractId = 1,
            ProductId = 1,
            LoadingDate = LoadingDate,
            LoadedQuantityMt = 10m,
            LoadingPriceUsd = 100m,
            SettlementCurrencyCode = "USD"
        });
        db.SaveChanges();
    }

    private static void SeedSupplierPaymentLedger(ApplicationDbContext db, decimal amountUsd)
    {
        db.LedgerEntries.Add(new LedgerEntry
        {
            EntryDate = LoadingDate.AddDays(1),
            Side = LedgerSide.Debit,
            AmountUsd = amountUsd,
            Currency = "USD",
            SourceCurrencyCode = "USD",
            AppliedFxRateToUsd = 1m,
            Description = "پرداخت به تأمین‌کننده",
            SourceType = "Payment",
            SourceId = 900,
            ContractId = 1,
            SupplierId = 1
        });
        db.SaveChanges();
    }

    private static LoadingCreateRowViewModel NewUsdRow() => new()
    {
        RowKey = "r1",
        ContractId = 1,
        LoadingDate = LoadingDate,
        LoadedQuantityMt = 10m,
        LoadingPriceUsd = 100m,
        SettlementCurrencyCode = "USD",
        RubRatePolicy = RubSettlementRatePolicy.NotApplicable,
        WagonNumber = "W-1"
    };

    private static LoadingCreateViewModel UsdCreateModel(params LoadingCreateRowViewModel[] rows)
        => new()
        {
            ContractId = 1,
            ProductId = 1,
            OriginLocationId = 1,
            TransportType = LoadingTransportType.Wagon,
            LoadingDate = rows.First().LoadingDate,
            LoadedQuantityMt = rows.Sum(r => r.LoadedQuantityMt),
            LockContract = true,
            Rows = rows.ToList()
        };

    private static LoadingController NewLoadingController(ApplicationDbContext db)
        => new(db, new AuditService(db), NullLogger<LoadingController>.Instance)
        {
            TempData = NewTempData()
        };

    private static ContractsController NewContractsController(ApplicationDbContext db)
        => new(db, new AuditService(db))
        {
            TempData = NewTempData()
        };

    // بک‌فیل به AuthBootstrapper دست نمی‌زند؛ فقط _db را می‌خواند و می‌نویسد.
    private static MaintenanceController NewMaintenanceController(ApplicationDbContext db)
        => new(db, new ConfigurationBuilder().Build(), null!);

    private static int ReadInt(object? payload, string name)
        => Convert.ToInt32(ReadValue(payload, name));

    private static decimal ReadDecimal(object? payload, string name)
        => Convert.ToDecimal(ReadValue(payload, name));

    private static object? ReadValue(object? payload, string name)
    {
        Assert.NotNull(payload);
        var property = payload!.GetType().GetProperty(name);
        Assert.NotNull(property);
        return property!.GetValue(payload);
    }

    private static TempDataDictionary NewTempData()
        => new(new DefaultHttpContext(), new NullTempDataProvider());

    private sealed class NullTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();

        public void SaveTempData(HttpContext context, IDictionary<string, object> values)
        {
        }
    }
}
