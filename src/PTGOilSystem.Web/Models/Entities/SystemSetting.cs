namespace PTGOilSystem.Web.Models.Entities;

/// <summary>
/// تقویمِ نمایش و ورودِ تاریخ در رابط کاربری. فقط لایهٔ نمایش/ورود را عوض می‌کند؛
/// ستون‌های تاریخِ دیتابیس همیشه همان مقدارِ میلادیِ کانونیک می‌مانند.
/// </summary>
public enum CalendarType
{
    Gregorian = 0,
    AfghanSolarHijri = 1
}

/// <summary>
/// تنظیماتِ عمومیِ سامانه. عمداً تک‌ردیفی است (همان الگوی <see cref="BackupSetting"/> و
/// <see cref="HrSettings"/>: اولین ردیف خوانده می‌شود)؛ نبودِ ردیف یعنی پیش‌فرض‌ها (تقویم میلادی).
/// </summary>
public class SystemSetting : BaseEntity
{
    public CalendarType CalendarType { get; set; } = CalendarType.Gregorian;
}
