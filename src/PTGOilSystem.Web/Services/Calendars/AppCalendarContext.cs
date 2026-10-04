using PTGOilSystem.Web.Models.Entities;

namespace PTGOilSystem.Web.Services.Calendars;

/// <summary>
/// تقویمِ فعالِ نمایش برای کل برنامه. <see cref="Helpers.DateDisplay"/> ایستا است و در صدها View و
/// خروجی استفاده می‌شود، پس مقدار فعال اینجا نگه داشته می‌شود و فقط
/// <see cref="ICalendarSettingsService"/> (هنگام شروع برنامه و ذخیرهٔ تنظیمات) آن را عوض می‌کند.
///
/// <see cref="Use"/> یک محدودهٔ جدا (AsyncLocal) می‌سازد؛ تست‌ها داخل آن تقویم را عوض می‌کنند بدون اینکه
/// روی درخواست‌ها یا تست‌های موازیِ دیگر اثر بگذارند.
/// </summary>
public static class AppCalendarContext
{
    private static int _global = (int)CalendarType.Gregorian;
    private static readonly AsyncLocal<Scope?> CurrentScope = new();

    public static CalendarType Current
        => CurrentScope.Value?.CalendarType ?? (CalendarType)Volatile.Read(ref _global);

    public static bool IsSolarHijri => Current == CalendarType.AfghanSolarHijri;

    /// <summary>تقویمِ فعال را عوض می‌کند؛ داخلِ یک <see cref="Use"/> فقط همان محدوده عوض می‌شود.</summary>
    public static void Set(CalendarType calendarType)
    {
        var scope = CurrentScope.Value;
        if (scope is not null)
        {
            scope.CalendarType = calendarType;
            return;
        }

        Volatile.Write(ref _global, (int)calendarType);
    }

    public static IDisposable Use(CalendarType calendarType)
    {
        var previous = CurrentScope.Value;
        CurrentScope.Value = new Scope { CalendarType = calendarType };
        return new Restore(previous);
    }

    private sealed class Scope
    {
        public CalendarType CalendarType { get; set; }
    }

    private sealed class Restore(Scope? previous) : IDisposable
    {
        public void Dispose() => CurrentScope.Value = previous;
    }
}
