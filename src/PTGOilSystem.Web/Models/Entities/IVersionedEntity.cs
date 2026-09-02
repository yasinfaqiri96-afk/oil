namespace PTGOilSystem.Web.Models.Entities;

/// <summary>
/// PTG-P1-05 — محافظ «Lost Update» با ستونِ نسخهٔ صریح.
///
/// چرا <c>xmin</c> نه: الگویی که <see cref="LoadingReceipt"/> استفاده می‌کند
/// (<c>uint RowVersion</c> نگاشت‌شده به ستونِ سیستمیِ PostgreSQL) روی موجودیت‌های مالی
/// روی PostgreSQL واقعی شکست: <c>42703: column p.xmin does not exist</c>. ستونِ سیستمی
/// در subquery/derived table قابل ارجاع نیست و EF برای این موجودیت‌ها چنین کوئری‌هایی
/// می‌سازد. جزئیات کامل در PTG_FULL_HARDENING_VALIDATION_REPORT.md ذیل PTG-P1-05.
///
/// جایگزین: یک ستونِ <c>bigint</c> واقعی که خودِ برنامه در
/// <c>ApplicationDbContext.ApplyConcurrencyVersions</c> افزایش می‌دهد. مستقل از شکلِ کوئری،
/// مستقل از Provider (روی SQLite تست‌ها و روی PostgreSQL تولید یکسان کار می‌کند)، و
/// افزودنی است: مقدار پیش‌فرضِ سطرهای موجود <c>1</c> می‌شود و هیچ داده‌ای تغییر نمی‌کند.
/// </summary>
public interface IVersionedEntity
{
    /// <summary>نسخهٔ سطر. هر UPDATE موفق آن را دقیقاً یکی زیاد می‌کند.</summary>
    long Version { get; set; }
}
