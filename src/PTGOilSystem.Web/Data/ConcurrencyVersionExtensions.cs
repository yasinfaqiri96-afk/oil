using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Models.Entities;

namespace PTGOilSystem.Web.Data;

/// <summary>
/// PTG-P1-05 — بستنِ حلقهٔ «فرم تا ذخیره».
///
/// ستونِ نسخه به‌تنهایی فقط پنجرهٔ چند میلی‌ثانیه‌ایِ داخلِ یک درخواست را می‌بندد. آنچه در
/// عمل داده را از بین می‌برد، پنجرهٔ بلندِ «کاربر فرم را باز کرد، ده دقیقه تایپ کرد، ذخیره
/// کرد» است. برای بستنِ آن، نسخه‌ای که کاربر <b>دیده</b> باید با فرم برگردد و همان مقدار
/// در <c>WHERE</c> بنشیند — نه نسخه‌ای که همین حالا از دیتابیس خوانده شد.
/// </summary>
public static class ConcurrencyVersionExtensions
{
    /// <summary>
    /// نسخهٔ آمده از فرم را به‌عنوان «مقدار اصلی» به EF می‌دهد، تا دستور به شکل
    /// <c>... WHERE "Id" = @id AND "Version" = @versionUserSaw</c> ساخته شود.
    ///
    /// مقدارِ صفر یا منفی یعنی فرم اصلاً نسخه نفرستاده (صفحهٔ کش‌شدهٔ قدیمی، یا فراخوانیِ
    /// برنامه‌ای). در آن حالت عمداً هیچ کاری نمی‌شود و رفتار دقیقاً مثل قبل می‌ماند؛
    /// جایگزینِ آن، رد کردنِ ذخیره بود که کاربر را بی‌دلیل قفل می‌کرد.
    /// <c>ConcurrencyVersionFormCoverageTests</c> ساختاراً pin می‌کند که فرم‌های ویرایشِ
    /// هدف این فیلد را واقعاً دارند، پس این حالت در عمل رخ نمی‌دهد.
    /// </summary>
    public static void UseExpectedVersion<TEntity>(
        this DbContext db,
        TEntity entity,
        long expectedVersion)
        where TEntity : class, IVersionedEntity
    {
        if (expectedVersion <= 0)
        {
            return;
        }

        db.Entry(entity).Property(e => e.Version).OriginalValue = expectedVersion;
    }
}
