using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// بخش‌های «فعالیت‌های دوره» قبلاً با یک <c>Take(100)</c> خاموش بریده می‌شدند — هم در صفحه و
/// هم در خروجی Export. حالا صفحه صفحه‌بندی واقعیِ دیتابیس دارد و Export همهٔ سطرها را می‌گیرد.
/// این تست هر دو را قفل می‌کند.
/// </summary>
public sealed class PeriodActivityPagingTests
{
    private const int CompanyId = 1;
    private const int PeriodId = 1;
    private static readonly DateTime PeriodStart = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime PeriodEnd = new(2026, 12, 31, 0, 0, 0, DateTimeKind.Utc);

    private static DbContextOptions<ApplicationDbContext> NewDbOptions()
        => new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

    /// <summary>دوره‌ای با <paramref name="purchaseCount"/> قرارداد خرید در همان بازه.</summary>
    private static void Seed(ApplicationDbContext db, int purchaseCount)
    {
        db.Companies.Add(new Company { Id = CompanyId, Code = "PTG", Name = "PTG" });
        db.Products.Add(new Product { Id = 1, Code = "GO", Name = "Gas Oil" });
        db.Suppliers.Add(new Supplier { Id = 1, Name = "Supplier A" });
        db.FiscalYears.Add(new FiscalYear
        {
            Id = 1,
            CompanyId = CompanyId,
            Name = "1405",
            StartDate = PeriodStart,
            EndDate = PeriodEnd
        });
        db.FiscalPeriods.Add(new FiscalPeriod
        {
            Id = PeriodId,
            CompanyId = CompanyId,
            FiscalYearId = 1,
            Name = "دورهٔ آزمایشی",
            StartDate = PeriodStart,
            EndDate = PeriodEnd
        });

        for (var i = 1; i <= purchaseCount; i++)
        {
            db.Contracts.Add(new Contract
            {
                Id = i,
                ContractNumber = $"PUR-{i:D3}",
                ContractName = $"قرارداد {i}",
                ContractType = ContractType.Purchase,
                Status = ContractStatus.Active,
                CompanyId = CompanyId,
                ProductId = 1,
                SupplierId = 1,
                ContractDate = PeriodStart.AddDays(i),
                QuantityMt = 10m * i
            });
        }

        db.SaveChanges();
    }

    private static PeriodActivityService NewService(ApplicationDbContext db) => new(db);

    [Fact]
    public async Task A_Section_Returns_Only_Its_Page_And_Reports_That_More_Exist()
    {
        var options = NewDbOptions();
        await using var db = new ApplicationDbContext(options);
        Seed(db, purchaseCount: 25);

        var first = await NewService(db).BuildAsync(
            PeriodId, CompanyId, accountingEnabled: false,
            section: PeriodActivitySections.Purchases, page: 1, pageSize: 10, allRows: false);

        Assert.NotNull(first);
        Assert.Equal(10, first!.Purchases.Count);
        Assert.True(first.HasMoreBySection![PeriodActivitySections.Purchases]);

        var third = await NewService(db).BuildAsync(
            PeriodId, CompanyId, accountingEnabled: false,
            section: PeriodActivitySections.Purchases, page: 3, pageSize: 10, allRows: false);

        // صفحهٔ سوم: پنج سطر باقی‌مانده و دیگر صفحه‌ای بعد از آن نیست.
        Assert.Equal(5, third!.Purchases.Count);
        Assert.False(third.HasMoreBySection![PeriodActivitySections.Purchases]);

        // صفحه‌ها روی هم نمی‌افتند.
        var firstRefs = first.Purchases.Select(r => r.Title).ToList();
        var thirdRefs = third.Purchases.Select(r => r.Title).ToList();
        Assert.Empty(firstRefs.Intersect(thirdRefs));
    }

    [Fact]
    public async Task Export_Takes_Every_Row_Not_Just_One_Page()
    {
        var options = NewDbOptions();
        await using var db = new ApplicationDbContext(options);
        Seed(db, purchaseCount: 130);

        var exported = await NewService(db).BuildAsync(
            PeriodId, CompanyId, accountingEnabled: false,
            section: null, page: 1, pageSize: int.MaxValue, allRows: true);

        // پیش از این تغییر، این عدد روی ۱۰۰ سقف می‌خورد.
        Assert.Equal(130, exported!.Purchases.Count);
        Assert.False(exported.HasMoreBySection![PeriodActivitySections.Purchases]);
    }

    [Fact]
    public async Task Kpis_Do_Not_Change_With_The_Page()
    {
        var options = NewDbOptions();
        await using var db = new ApplicationDbContext(options);
        Seed(db, purchaseCount: 25);

        var service = NewService(db);
        var page1 = await service.BuildAsync(
            PeriodId, CompanyId, accountingEnabled: false,
            section: PeriodActivitySections.Purchases, page: 1, pageSize: 10, allRows: false);
        var page2 = await service.BuildAsync(
            PeriodId, CompanyId, accountingEnabled: false,
            section: PeriodActivitySections.Purchases, page: 2, pageSize: 10, allRows: false);

        // شمارنده‌ها و جمع‌های بالای صفحه روی کل دوره‌اند، نه روی صفحهٔ جاری.
        Assert.Equal(25, page1!.Kpis.PurchaseCount);
        Assert.Equal(page1.Kpis.PurchaseCount, page2!.Kpis.PurchaseCount);
        Assert.Equal(page1.Kpis.PurchaseQuantityMt, page2.Kpis.PurchaseQuantityMt);
    }

    [Fact]
    public async Task Only_The_Requested_Section_Is_Paged()
    {
        var options = NewDbOptions();
        await using var db = new ApplicationDbContext(options);
        Seed(db, purchaseCount: 25);

        // بخش فعال «فروش» است، پس «خرید» صفحهٔ اول خودش را نشان می‌دهد نه صفحهٔ سوم را.
        var model = await NewService(db).BuildAsync(
            PeriodId, CompanyId, accountingEnabled: false,
            section: PeriodActivitySections.Sales, page: 3, pageSize: 10, allRows: false);

        Assert.Equal(PeriodActivitySections.Sales, model!.ActiveSection);
        Assert.Equal(10, model.Purchases.Count);
        Assert.Equal("PUR-025", model.Purchases[0].Title);
    }
}
