using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PTGOilSystem.Web.Controllers;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Helpers;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.Loading;
using PTGOilSystem.Web.Services;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// سطر Legacy دفتر قدیمی بابت بدهی تأمین‌کننده از بارگیری روبلی.
/// نکتهٔ اصلی: بعد از «اصلاح قیمت»/بازقفل، AmountUsdAtRubLock عوض می‌شد ولی سطر Legacy کهنه می‌ماند
/// و طلب تأمین‌کننده با دفتر کل جدید اختلاف پیدا می‌کرد. حالا همان سطر هماهنگ می‌شود، نه سطر دوم.
/// </summary>
public class SupplierLoadingLegacyLedgerTests
{
    [Fact]
    public async Task RubLoading_Creates_Exactly_One_Legacy_Ledger_Row()
    {
        await using var db = NewDb();
        SeedRubContract(db, fixedRate: 80m);
        var controller = NewLoadingController(db);

        var result = await controller.Create(RubCreateModel(NewRubRow()));

        Assert.IsType<RedirectToActionResult>(result);
        var loading = await db.LoadingRegisters.SingleAsync();
        Assert.Equal(1_000m, loading.AmountUsdAtRubLock);

        var ledger = await LegacyRowsAsync(db, loading.Id);
        Assert.Single(ledger);
        Assert.Equal(1_000m, ledger[0].AmountUsd);
        Assert.Equal(80_000m, ledger[0].SourceAmount);
        Assert.Equal(LedgerSide.Credit, ledger[0].Side);
    }

    // قیمت هر بارگیری مستقل است، پس «اصلاح قیمت» بارگیریِ قیمت‌دار و سطر دفترش را
    // دست نمی‌زند و سطر دوم هم نمی‌سازد.
    [Fact]
    public async Task Repricing_Keeps_The_Same_Legacy_Row_Of_A_Priced_Loading()
    {
        await using var db = NewDb();
        SeedRubContract(db, fixedRate: 80m);
        SeedLockedRubLoading(db);
        var ledgerIdBefore = (await LegacyRowsAsync(db, LoadingId)).Single().Id;

        var contract = await db.Contracts.SingleAsync();
        contract.PricingMethod = PricingMethod.ManualFinalPrice;
        contract.ManualFinalPriceUsd = 120m;
        await db.SaveChangesAsync();

        var result = await NewContractsController(db).RepricePurchaseLoadings(contract.Id);

        Assert.IsType<RedirectToActionResult>(result);
        var loading = await db.LoadingRegisters.SingleAsync();
        Assert.Equal(100m, loading.LoadingPriceUsd);
        Assert.Equal(1_000m, loading.AmountUsdAtRubLock); // 10 * 100 — دست‌نخورده
        Assert.Equal(80_000m, loading.AmountRubAtRubLock);

        var ledger = await LegacyRowsAsync(db, LoadingId);
        Assert.Single(ledger);
        Assert.Equal(ledgerIdBefore, ledger[0].Id);
        Assert.Equal(1_000m, ledger[0].AmountUsd);
        Assert.Equal(80_000m, ledger[0].SourceAmount);
    }

    [Fact]
    public async Task Repricing_Does_Not_Create_A_Second_Legacy_Row()
    {
        await using var db = NewDb();
        SeedRubContract(db, fixedRate: 80m);
        SeedLockedRubLoading(db);

        var contract = await db.Contracts.SingleAsync();
        contract.PricingMethod = PricingMethod.ManualFinalPrice;
        contract.ManualFinalPriceUsd = 120m;
        await db.SaveChangesAsync();

        await NewContractsController(db).RepricePurchaseLoadings(contract.Id);

        Assert.Single(await db.LedgerEntries.Where(l => l.SourceType == "Loading").ToListAsync());
    }

    [Fact]
    public async Task ForceRelock_Keeps_Legacy_Amount_Equal_To_New_AmountUsdAtRubLock()
    {
        await using var db = NewDb();
        SeedRubContract(db, fixedRate: 80m);
        SeedLockedRubLoading(db);

        var contract = await db.Contracts.SingleAsync();
        contract.PricingMethod = PricingMethod.ManualFinalPrice;
        contract.ManualFinalPriceUsd = 137.5m;
        await db.SaveChangesAsync();

        await NewContractsController(db).RepricePurchaseLoadings(contract.Id);

        var loading = await db.LoadingRegisters.SingleAsync();
        var ledger = (await LegacyRowsAsync(db, LoadingId)).Single();
        Assert.Equal(loading.AmountUsdAtRubLock, ledger.AmountUsd);
        Assert.Equal(loading.AmountRubAtRubLock, ledger.SourceAmount);
    }

    [Fact]
    public async Task Repricing_Twice_With_Same_Price_Does_Not_Duplicate_Or_Rewrite()
    {
        await using var db = NewDb();
        SeedRubContract(db, fixedRate: 80m);
        SeedLockedRubLoading(db);

        var contract = await db.Contracts.SingleAsync();
        contract.PricingMethod = PricingMethod.ManualFinalPrice;
        contract.ManualFinalPriceUsd = 120m;
        await db.SaveChangesAsync();

        var controller = NewContractsController(db);
        await controller.RepricePurchaseLoadings(contract.Id);
        var afterFirst = (await LegacyRowsAsync(db, LoadingId)).Single();
        var idAfterFirst = afterFirst.Id;
        var amountAfterFirst = afterFirst.AmountUsd;

        await controller.RepricePurchaseLoadings(contract.Id);

        var ledger = await LegacyRowsAsync(db, LoadingId);
        Assert.Single(ledger);
        Assert.Equal(idAfterFirst, ledger[0].Id);
        Assert.Equal(amountAfterFirst, ledger[0].AmountUsd);
    }

    // بارگیری دالری حالا سطر دفتر دارد (مبلغ از مقدار × قیمت، بدون snapshot روبل).
    // جزئیات مسیر دالری در SupplierUsdLoadingLedgerTests پوشش داده شده؛ اینجا فقط
    // تأیید می‌شود که اصلاح قیمتِ قرارداد سطرِ دالری را با مبلغ تازه می‌سازد و روبل را دست نمی‌زند.
    [Fact]
    public async Task UsdLoading_Gets_A_Row_From_Quantity_Times_Price()
    {
        await using var db = NewDb();
        SeedRubContract(db, fixedRate: 80m);
        var contract = await db.Contracts.SingleAsync();
        contract.SettlementCurrencyCode = "USD";
        contract.RubRatePolicy = RubSettlementRatePolicy.RateLater;
        contract.ContractRubPerUsdRate = null;
        contract.PricingMethod = PricingMethod.ManualFinalPrice;
        contract.ManualFinalPriceUsd = 120m;
        db.LoadingRegisters.Add(new LoadingRegister
        {
            Id = LoadingId,
            ContractId = 1,
            ProductId = 1,
            LoadingDate = LoadingDate,
            LoadedQuantityMt = 10m,
            // بدون قیمت ثبت شده: «اصلاح قیمت» فقط همین بارگیری‌ها را با نرخ قرارداد تکمیل می‌کند.
            LoadingPriceUsd = null,
            SettlementCurrencyCode = "USD"
        });
        await db.SaveChangesAsync();

        await NewContractsController(db).RepricePurchaseLoadings(contract.Id);

        var loading = await db.LoadingRegisters.SingleAsync();
        Assert.Equal(120m, loading.LoadingPriceUsd);
        Assert.Null(loading.AmountUsdAtRubLock);

        var rows = await LegacyRowsAsync(db, LoadingId);
        Assert.Single(rows);
        Assert.Equal(1_200m, rows[0].AmountUsd); // 10 × 120
        Assert.Equal("USD", rows[0].SourceCurrencyCode);
        Assert.Equal(LedgerSide.Credit, rows[0].Side);
    }

    [Fact]
    public async Task PendingRateLoading_Stays_Pending_And_Writes_No_Legacy_Row()
    {
        await using var db = NewDb();
        SeedRubContract(db, fixedRate: null); // RateLater — نرخ روبل هنوز نیست
        var contract = await db.Contracts.SingleAsync();
        contract.PricingMethod = PricingMethod.ManualFinalPrice;
        contract.ManualFinalPriceUsd = 120m;
        db.LoadingRegisters.Add(new LoadingRegister
        {
            Id = LoadingId,
            ContractId = 1,
            ProductId = 1,
            LoadingDate = LoadingDate,
            LoadedQuantityMt = 10m,
            LoadingPriceUsd = 100m,
            SettlementCurrencyCode = "RUB",
            RubRateStatus = RubSettlementRateStatus.Pending
        });
        await db.SaveChangesAsync();

        await NewContractsController(db).RepricePurchaseLoadings(contract.Id);

        var loading = await db.LoadingRegisters.SingleAsync();
        Assert.Equal(RubSettlementRateStatus.Pending, loading.RubRateStatus);
        Assert.Null(loading.AmountUsdAtRubLock);
        Assert.Empty(await LegacyRowsAsync(db, LoadingId));
    }

    [Fact]
    public void Legacy_And_Adapter_Share_One_Rounding_Rule()
    {
        // نصفِ دقیقِ رقم چهارم: ToEven به 0.1234 می‌رسید، AwayFromZero به 0.1235.
        Assert.Equal(0.1235m, LoadingRubSettlement.RoundAmountUsd(0.12345m));
        Assert.Equal(0.1235m, decimal.Round(0.12345m, 4, MidpointRounding.AwayFromZero));
        Assert.Equal(1.0002m, LoadingRubSettlement.RoundAmountUsd(1.00015m));
    }

    [Fact]
    public void ApplySnapshot_Reports_No_Change_When_Amount_Is_Already_Current()
    {
        var loading = new LoadingRegister
        {
            Id = LoadingId,
            LoadingDate = LoadingDate,
            RubPerUsdRate = 80m,
            RubRateStatus = RubSettlementRateStatus.Locked,
            AmountUsdAtRubLock = 1_000m,
            AmountRubAtRubLock = 80_000m,
            RubRateSource = "Contract"
        };
        var entry = new LedgerEntry();

        Assert.True(SupplierLoadingLedger.ApplySnapshot(entry, loading));
        Assert.False(SupplierLoadingLedger.ApplySnapshot(entry, loading));

        loading.AmountUsdAtRubLock = 1_200m;
        Assert.True(SupplierLoadingLedger.ApplySnapshot(entry, loading));
        Assert.Equal(1_200m, entry.AmountUsd);
    }

    private const int LoadingId = 30;
    private static readonly DateTime LoadingDate = new(2026, 5, 2);

    private static ApplicationDbContext NewDb()
        => new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static async Task<List<LedgerEntry>> LegacyRowsAsync(ApplicationDbContext db, int loadingId)
        => await db.LedgerEntries
            .AsNoTracking()
            .Where(l => l.SourceType == "Loading" && l.SourceId == loadingId)
            .ToListAsync();

    private static void SeedRubContract(ApplicationDbContext db, decimal? fixedRate)
    {
        db.Units.Add(new Unit { Id = 1, Code = "MT", Name = "Metric Ton", Symbol = "MT", IsActive = true });
        db.Products.Add(new Product { Id = 1, Code = "GO", Name = "Gas Oil", UnitId = 1, IsActive = true });
        db.Companies.Add(new Company { Id = 1, Code = "PTG", Name = "PTG", IsActive = true });
        db.Suppliers.Add(new Supplier { Id = 1, Name = "Supplier A", IsActive = true });
        db.Locations.Add(new PTGOilSystem.Web.Models.Entities.Location { Id = 1, Name = "BNK", Kind = "Origin" });
        db.Contracts.Add(new Contract
        {
            Id = 1,
            ContractNumber = "PUR-RUB-1",
            ContractType = ContractType.Purchase,
            Status = ContractStatus.Active,
            ProductId = 1,
            UnitId = 1,
            CompanyId = 1,
            SupplierId = 1,
            ContractDate = new DateTime(2026, 5, 1),
            QuantityMt = 500m,
            PricingMethod = PricingMethod.Fixed,
            UnitPriceUsd = 100m,
            SettlementCurrencyCode = "RUB",
            RubRatePolicy = fixedRate.HasValue
                ? RubSettlementRatePolicy.FixedContractRate
                : RubSettlementRatePolicy.RateLater,
            ContractRubPerUsdRate = fixedRate,
            ContractRubRateDate = fixedRate.HasValue ? new DateTime(2026, 5, 1) : null,
            ContractRubRateSource = fixedRate.HasValue ? "Contract" : null
        });
        db.SaveChanges();
    }

    // بارگیری روبلیِ قفل‌شده به همراه سطر Legacy‌ای که مسیر بارگیری ساخته بود.
    private static void SeedLockedRubLoading(ApplicationDbContext db)
    {
        db.LoadingRegisters.Add(new LoadingRegister
        {
            Id = LoadingId,
            ContractId = 1,
            ProductId = 1,
            LoadingDate = LoadingDate,
            LoadedQuantityMt = 10m,
            LoadingPriceUsd = 100m,
            SettlementCurrencyCode = "RUB",
            RubPerUsdRate = 80m,
            RubRateStatus = RubSettlementRateStatus.Locked,
            RubRateDate = new DateTime(2026, 5, 1),
            RubRateSource = "Contract",
            AmountUsdAtRubLock = 1_000m,
            AmountRubAtRubLock = 80_000m
        });
        db.LedgerEntries.Add(new LedgerEntry
        {
            EntryDate = LoadingDate,
            Side = LedgerSide.Credit,
            AmountUsd = 1_000m,
            Currency = "USD",
            SourceAmount = 80_000m,
            SourceCurrencyCode = "RUB",
            AppliedFxRateToUsd = decimal.Round(1m / 80m, 6, MidpointRounding.AwayFromZero),
            AppliedFxRateDate = new DateTime(2026, 5, 1),
            AppliedFxRateSource = "Contract",
            Description = $"بدهی تأمین‌کننده بابت بارگیری #{LoadingId}",
            SourceType = "Loading",
            SourceId = LoadingId,
            Reference = $"LOAD-{LoadingId}",
            ContractId = 1,
            SupplierId = 1
        });
        db.SaveChanges();
    }

    private static LoadingCreateRowViewModel NewRubRow() => new()
    {
        RowKey = "r1",
        ContractId = 1,
        LoadingDate = LoadingDate,
        LoadedQuantityMt = 10m,
        LoadingPriceUsd = 100m,
        SettlementCurrencyCode = "RUB",
        RubRatePolicy = RubSettlementRatePolicy.RateLater,
        WagonNumber = "W-1"
    };

    private static LoadingCreateViewModel RubCreateModel(params LoadingCreateRowViewModel[] rows)
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
