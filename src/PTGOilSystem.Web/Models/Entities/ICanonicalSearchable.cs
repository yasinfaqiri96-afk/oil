namespace PTGOilSystem.Web.Models.Entities;

/// <summary>
/// PTG — جستجوی canonical روی متنِ ذخیره‌شده.
///
/// <b>مسئله:</b> P1-04 متنِ افغانی را canonical کرد، ولی فقط برای <i>هویتِ ایمپورت</i>.
/// جستجوی صفحه‌ها همچنان روی ستونِ خامِ دیتابیس اجرا می‌شود، پس «یوسف» چیزی را پیدا
/// نمی‌کند که با «يوسف» (یای عربی) ذخیره شده، و «۱۲۳۴۵» شماره‌ای را که «12345» نوشته شده.
/// canonical کردنِ فقط عبارتِ جستجو کمکی نمی‌کند، چون سمتِ دیتابیس هنوز خام است.
///
/// <b>راه‌حل:</b> یک ستونِ کمکیِ <see cref="SearchKey"/> کنارِ ستونِ نمایشی. مقدارِ نمایشی
/// <b>هرگز</b> تغییر نمی‌کند — نامِ آدم‌ها همان‌طور که نوشته شده می‌ماند — و فقط این ستونِ
/// اضافه canonical است و ایندکس می‌شود.
///
/// مقدار در <c>ApplicationDbContext.ApplyCanonicalSearchKeys</c> هنگام هر ذخیره ساخته
/// می‌شود، پس تنها یک تعریف از «canonical یعنی چه» وجود دارد: <see cref="Helpers.AfghanTextNormalizer"/>.
/// </summary>
public interface ICanonicalSearchable
{
    /// <summary>
    /// شکلِ canonical فیلدهای هویتیِ همین سطر. <c>null</c> یعنی هنوز ساخته نشده
    /// (سطرهای پیش از این تغییر، تا وقتی Backfill اجرا نشده) — و جستجو در آن حالت
    /// به رفتار قبلی برمی‌گردد، پس چیزی از دست نمی‌رود.
    /// </summary>
    string? SearchKey { get; set; }

    /// <summary>متنِ خامی که باید canonical شود. ترکیبِ فیلدهای هویتیِ همین موجودیت.</summary>
    string BuildSearchSource();
}
