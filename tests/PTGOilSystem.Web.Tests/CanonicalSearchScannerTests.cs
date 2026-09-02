using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Reconciliation;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// PTG فاز ۷/۱۱ — اسکنرِ <c>CANONICAL-SEARCH-STALE</c>.
///
/// چیزی که می‌بیند: سطری که کلیدِ جستجویش با متنِ نمایشی جور نیست. برای کاربر یعنی آن
/// نام با املای دیگر پیدا نمی‌شود — خرابی‌ای که هیچ خطایی نمی‌دهد و فقط با شمردن دیده
/// می‌شود. مثل بقیهٔ اسکنرها، فقط‌خواندنی است.
/// </summary>
public sealed class CanonicalSearchScannerTests
{
    private static ApplicationDbContext NewDb()
        => new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase($"canonical-scanner-{Guid.NewGuid():N}")
            .Options);

    private static LedgerIntegrityFinding Find(LedgerIntegrityReport report)
        => report.Findings.Single(f => f.Code == "CANONICAL-SEARCH-STALE");

    [Fact]
    public async Task RowsSavedThroughTheApplication_AreNeverReportedAsStale()
    {
        await using var db = NewDb();
        db.Customers.Add(new Customer { Code = "C-1", Name = "يوسف" });
        db.Suppliers.Add(new Supplier { Code = "S-1", Name = "شركت كابل" });
        db.Wagons.Add(new Wagon { WagonNumber = "۱۲۳۴۵" });
        await db.SaveChangesAsync();

        var report = await new LedgerIntegrityReconciliationService(db).RunAsync();

        Assert.Equal(0, Find(report).Count);
    }

    /// <summary>اسکنر چیزی را اصلاح نمی‌کند؛ Backfill این کار را می‌کند.</summary>
    [Fact]
    public async Task TheScannerNeverWritesAnything()
    {
        await using var db = NewDb();
        db.Customers.Add(new Customer { Code = "C-1", Name = "يوسف" });
        await db.SaveChangesAsync();

        var before = await db.Customers.AsNoTracking().Select(c => new { c.Name, c.SearchKey }).SingleAsync();
        await new LedgerIntegrityReconciliationService(db).RunAsync();
        var after = await db.Customers.AsNoTracking().Select(c => new { c.Name, c.SearchKey }).SingleAsync();

        Assert.Equal(before.Name, after.Name);
        Assert.Equal(before.SearchKey, after.SearchKey);
    }

    /// <summary>اسکنر در فهرست گزارش هست — وگرنه هیچ‌کس آن را نمی‌بیند.</summary>
    [Fact]
    public async Task TheScannerIsPartOfTheStandardReport()
    {
        await using var db = NewDb();

        var report = await new LedgerIntegrityReconciliationService(db).RunAsync();

        Assert.Contains(report.Findings, f => f.Code == "CANONICAL-SEARCH-STALE");
        Assert.Contains(report.Findings, f => f.Code == "LEDGER-ORPHAN");
        Assert.Contains(report.Findings, f => f.Code == "PARTNER-PERIOD-COST-BASIS");
    }

    /// <summary>Backfill خروجیِ اسکنر را پاک می‌کند — دو ابزار یک تعریف از canonical دارند.</summary>
    [Fact]
    public async Task AfterBackfill_TheScannerIsClean()
    {
        await using var db = NewDb();
        db.Customers.Add(new Customer { Code = "C-1", Name = "يوسف" });
        db.Suppliers.Add(new Supplier { Code = "S-1", Name = "كابل" });
        await db.SaveChangesAsync();

        await CanonicalSearchKeyBackfill.RunAsync(db, commit: true);
        var report = await new LedgerIntegrityReconciliationService(db).RunAsync();

        Assert.Equal(0, Find(report).Count);
    }
}
