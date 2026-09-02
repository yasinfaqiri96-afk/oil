using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Helpers;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// PTG فاز ۷ — جستجوی canonical.
///
/// شکستِ واقعی: کاربر «یوسف» (یای فارسی) را تایپ می‌کند و سطری که با «يوسف» (یای عربی)
/// ذخیره شده پیدا نمی‌شود؛ همین‌طور «۱۲۳۴۵» شمارهٔ «12345» را نمی‌یابد. راه‌حل یک ستونِ
/// کمکیِ canonical است — و شرطِ اصلی این تست‌ها این است که <b>متنِ نمایشی هرگز عوض نشود</b>.
/// </summary>
public sealed class CanonicalSearchKeyTests
{
    private static DbContextOptions<ApplicationDbContext> NewDbOptions()
        => new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase($"canonical-search-{Guid.NewGuid():N}")
            .Options;

    /// <summary>عبارتِ کاربر با همان قاعده‌ای canonical می‌شود که ستون ساخته شده.</summary>
    private static string Canonical(string term) => AfghanTextNormalizer.NormalizeForSearch(term);

    // ------------------------------------------------------------------
    // ۱ — حروف: «یوسف» و «يوسف» باید یکدیگر را پیدا کنند
    // ------------------------------------------------------------------

    [Fact]
    public async Task PersianYeQuery_FindsRowStoredWithArabicYe()
    {
        await using var db = new ApplicationDbContext(NewDbOptions());
        db.Customers.Add(new Customer { Code = "C-1", Name = "يوسف اسماعيل" }); // یای عربی
        await db.SaveChangesAsync();

        var canonical = Canonical("یوسف"); // یای فارسی
        var hits = await db.Customers
            .Where(c => c.SearchKey != null && c.SearchKey.Contains(canonical))
            .ToListAsync();

        Assert.Single(hits);
    }

    [Fact]
    public async Task ArabicYeQuery_FindsRowStoredWithPersianYe()
    {
        await using var db = new ApplicationDbContext(NewDbOptions());
        db.Customers.Add(new Customer { Code = "C-1", Name = "یوسف اسماعیل" }); // یای فارسی
        await db.SaveChangesAsync();

        var canonical = Canonical("يوسف"); // یای عربی
        var hits = await db.Customers
            .Where(c => c.SearchKey != null && c.SearchKey.Contains(canonical))
            .ToListAsync();

        Assert.Single(hits);
    }

    // ------------------------------------------------------------------
    // ۲ — ارقام: لاتین/فارسی/عربی یک هویتِ جستجو
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("12345")]
    [InlineData("۱۲۳۴۵")]
    [InlineData("١٢٣٤٥")]
    public async Task AnyDigitSystemInTheQuery_FindsTheWagon(string typedNumber)
    {
        await using var db = new ApplicationDbContext(NewDbOptions());
        db.Wagons.Add(new Wagon { WagonNumber = "۱۲۳۴۵" }); // ذخیره با ارقام فارسی
        await db.SaveChangesAsync();

        var canonical = Canonical(typedNumber);
        var hits = await db.Wagons
            .Where(w => w.SearchKey != null && w.SearchKey.Contains(canonical))
            .ToListAsync();

        Assert.Single(hits);
    }

    // ------------------------------------------------------------------
    // ۳ — قاعدهٔ اصلی: متنِ نمایشی دست‌نخورده می‌ماند
    // ------------------------------------------------------------------

    [Fact]
    public async Task DisplayValue_IsNeverRewrittenByTheSearchKey()
    {
        const string asTyped = "يوسف اسماعيل"; // با یای عربی، دقیقاً همان‌طور که کاربر نوشته
        await using var db = new ApplicationDbContext(NewDbOptions());
        db.Customers.Add(new Customer { Code = "C-۱", Name = asTyped });
        await db.SaveChangesAsync();

        var stored = await db.Customers.AsNoTracking().SingleAsync();

        Assert.Equal(asTyped, stored.Name);
        Assert.Equal("C-۱", stored.Code);
        Assert.NotEqual(asTyped, stored.SearchKey);
    }

    // ------------------------------------------------------------------
    // ۴ — کلید در INSERT ساخته می‌شود و در UPDATE به‌روز می‌شود
    // ------------------------------------------------------------------

    [Fact]
    public async Task SearchKey_IsPopulatedOnInsert()
    {
        await using var db = new ApplicationDbContext(NewDbOptions());
        db.Suppliers.Add(new Supplier { Code = "S-1", Name = "شركت كابل" });
        await db.SaveChangesAsync();

        var stored = await db.Suppliers.AsNoTracking().SingleAsync();

        Assert.False(string.IsNullOrWhiteSpace(stored.SearchKey));
        Assert.Equal(Canonical("S-1 شرکت کابل"), stored.SearchKey);
    }

    [Fact]
    public async Task SearchKey_FollowsAnIdentityFieldRename()
    {
        await using var db = new ApplicationDbContext(NewDbOptions());
        var supplier = new Supplier { Code = "S-1", Name = "کابل" };
        db.Suppliers.Add(supplier);
        await db.SaveChangesAsync();

        supplier.Name = "هرات";
        await db.SaveChangesAsync();

        var stored = await db.Suppliers.AsNoTracking().SingleAsync();

        Assert.Equal(Canonical("S-1 هرات"), stored.SearchKey);
        Assert.DoesNotContain("کابل", stored.SearchKey);
    }

    // ------------------------------------------------------------------
    // ۵ — برخوردِ کلید: گزارش می‌شود، ادغام نمی‌شود
    // ------------------------------------------------------------------

    /// <summary>
    /// دو نامِ متفاوتِ نمایشی که کلیدِ یکسان می‌سازند: باید <b>گزارش</b> شوند، نه ادغام.
    /// (پرکردنِ سطرهای خالی و تکرارپذیریِ Backfill روی PostgreSQL واقعی آزموده می‌شود —
    /// <see cref="CanonicalSearchPostgresTests"/> — چون فقط آن‌جا می‌توان ستون را واقعاً NULL کرد.)
    /// </summary>
    [Fact]
    public async Task Backfill_ReportsCollisionsWithoutMergingAnyRow()
    {
        await using var db = new ApplicationDbContext(NewDbOptions());
        db.Customers.Add(new Customer { Code = "C-1", Name = "يوسف" }); // یای عربی
        db.Customers.Add(new Customer { Code = "C-1", Name = "یوسف" }); // یای فارسی
        await db.SaveChangesAsync();

        var result = await CanonicalSearchKeyBackfill.RunAsync(db, commit: true);
        var customers = result.Tables.Single(t => t.Entity == "Customer");

        Assert.Single(customers.Collisions);
        Assert.Equal(2, customers.Collisions[0].Ids.Count);
        Assert.Equal(2, await db.Customers.CountAsync()); // هیچ سطری حذف/ادغام نشده
    }

    /// <summary>سطرهایی که کلیدشان درست است، در اجرای دوباره تغییری نمی‌گیرند.</summary>
    [Fact]
    public async Task Backfill_OnAlreadyCanonicalRows_ChangesNothing()
    {
        await using var db = new ApplicationDbContext(NewDbOptions());
        db.Customers.Add(new Customer { Code = "C-1", Name = "احمد" });
        await db.SaveChangesAsync();

        var result = await CanonicalSearchKeyBackfill.RunAsync(db, commit: true);

        Assert.Equal(0, result.TotalUpdated);
    }
}
