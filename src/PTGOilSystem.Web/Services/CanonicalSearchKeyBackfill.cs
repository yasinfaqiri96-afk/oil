using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Helpers;
using PTGOilSystem.Web.Models.Entities;

namespace PTGOilSystem.Web.Services;

/// <summary>پیامدِ اجرای Backfill برای یک جدول.</summary>
public sealed record CanonicalSearchKeyBackfillTable(
    string Entity,
    int Scanned,
    int Updated,
    IReadOnlyList<CanonicalSearchKeyCollision> Collisions);

/// <summary>
/// دو سطرِ متفاوت که کلیدِ canonical یکسان می‌سازند (مثلاً «يوسف» و «یوسف»).
/// این‌جا فقط <b>گزارش</b> می‌شود؛ هیچ سطری ادغام یا حذف نمی‌شود.
/// </summary>
public sealed record CanonicalSearchKeyCollision(string SearchKey, IReadOnlyList<int> Ids);

/// <summary>خلاصهٔ کلِ اجرا.</summary>
public sealed record CanonicalSearchKeyBackfillResult(
    bool Committed,
    IReadOnlyList<CanonicalSearchKeyBackfillTable> Tables)
{
    public int TotalUpdated => Tables.Sum(t => t.Updated);
    public int TotalCollisions => Tables.Sum(t => t.Collisions.Count);
}

/// <summary>
/// PTG — پرکردنِ <see cref="ICanonicalSearchable.SearchKey"/> برای سطرهایی که پیش از
/// این تغییر ثبت شده‌اند.
///
/// سه تضمین:
/// <list type="bullet">
///   <item>متنِ نمایشی خوانده می‌شود ولی هرگز نوشته نمی‌شود.</item>
///   <item>اجرای دوباره امن است — کلید از همان ورودی ساخته می‌شود، پس بارِ دوم چیزی تغییر نمی‌کند.</item>
///   <item>برخوردِ کلید فقط گزارش می‌شود؛ ادغام یا حذفِ خودکار در کار نیست.</item>
/// </list>
///
/// کلید از همان مسیرِ ذخیره ساخته می‌شود (<see cref="AfghanTextNormalizer.NormalizeForSearch"/>)،
/// پس Backfill و INSERT هرگز دو تعریفِ متفاوت از canonical ندارند.
/// </summary>
public static class CanonicalSearchKeyBackfill
{
    public static async Task<CanonicalSearchKeyBackfillResult> RunAsync(
        ApplicationDbContext db,
        bool commit,
        CancellationToken cancellationToken = default)
    {
        var tables = new List<CanonicalSearchKeyBackfillTable>
        {
            await BackfillAsync(db, db.Partners, "Partner", commit, cancellationToken),
            await BackfillAsync(db, db.Suppliers, "Supplier", commit, cancellationToken),
            await BackfillAsync(db, db.Customers, "Customer", commit, cancellationToken),
            await BackfillAsync(db, db.Companies, "Company", commit, cancellationToken),
            await BackfillAsync(db, db.Trucks, "Truck", commit, cancellationToken),
            await BackfillAsync(db, db.Wagons, "Wagon", commit, cancellationToken),
            await BackfillAsync(db, db.Contracts, "Contract", commit, cancellationToken),
            await BackfillAsync(db, db.LoadingRegisters, "LoadingRegister", commit, cancellationToken),
        };

        return new CanonicalSearchKeyBackfillResult(commit, tables);
    }

    private static async Task<CanonicalSearchKeyBackfillTable> BackfillAsync<TEntity>(
        ApplicationDbContext db,
        DbSet<TEntity> set,
        string entityName,
        bool commit,
        CancellationToken cancellationToken)
        where TEntity : BaseEntity, ICanonicalSearchable
    {
        var rows = await set.ToListAsync(cancellationToken);
        var updated = 0;

        foreach (var row in rows)
        {
            var key = AfghanTextNormalizer.NormalizeForSearch(row.BuildSearchSource());
            var canonical = string.IsNullOrWhiteSpace(key) ? null : key;

            if (!string.Equals(row.SearchKey, canonical, StringComparison.Ordinal))
            {
                row.SearchKey = canonical;
                updated++;
            }
        }

        // برخوردها روی مقدارِ محاسبه‌شده گزارش می‌شوند، چه ذخیره شود چه نه.
        var collisions = rows
            .Where(r => !string.IsNullOrWhiteSpace(r.SearchKey))
            .GroupBy(r => r.SearchKey!, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => new CanonicalSearchKeyCollision(g.Key, g.Select(r => r.Id).OrderBy(id => id).ToList()))
            .OrderBy(c => c.SearchKey, StringComparer.Ordinal)
            .ToList();

        if (commit && updated > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        else if (!commit)
        {
            // Dry run: چیزی از تغییراتِ محاسبه‌شده نباید به SaveChanges بعدی نشت کند.
            foreach (var row in rows)
            {
                db.Entry(row).State = EntityState.Detached;
            }
        }

        return new CanonicalSearchKeyBackfillTable(entityName, rows.Count, updated, collisions);
    }
}
