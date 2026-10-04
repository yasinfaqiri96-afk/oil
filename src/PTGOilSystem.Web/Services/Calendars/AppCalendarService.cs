using System.Globalization;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services.Time;

namespace PTGOilSystem.Web.Services.Calendars;

/// <summary>بازهٔ تاریخِ کانونیک (میلادی) که فیلترِ گزارش بدون تغییر رویش اجرا می‌شود.</summary>
public sealed record CalendarDateRange(DateTime? From, DateTime? To, IReadOnlyList<string> Errors)
{
    public bool IsValid => Errors.Count == 0;
}

/// <summary>
/// تنها نقطهٔ تبدیلِ تقویم در برنامه. ذخیره‌سازی همیشه میلادیِ کانونیک است؛ این سرویس فقط نمایش و
/// ورود را بر اساسِ تقویمِ انتخاب‌شده در تنظیمات انجام می‌دهد.
/// </summary>
public interface IAppCalendarService
{
    CalendarType CalendarType { get; }
    bool IsSolarHijri { get; }
    IReadOnlyList<string> MonthNames { get; }

    SolarDate ToSolar(DateTime value);
    DateTime ToGregorian(int year, int month, int day);

    /// <summary>«1405/07/12» یا «2026/10/04» — بدون نشانهٔ جهت، برای متن و خروجی.</summary>
    string FormatDate(DateTime? value, string empty = "-");

    /// <summary>تاریخ + ساعت، بدون تبدیلِ منطقهٔ زمانی.</summary>
    string FormatDateTime(DateTime? value, string empty = "-");

    /// <summary>«12 میزان 1405» یا «4 Oct 2026».</summary>
    string FormatLongDate(DateTime? value, string empty = "-");

    /// <summary>
    /// متنِ کاربر را به روزِ کانونیک تبدیل می‌کند. در حالت شمسی، هم شمسی (با ارقام لاتین یا فارسی/عربی)
    /// و هم ISO میلادی پذیرفته می‌شود؛ در حالت میلادی فقط میلادی.
    /// </summary>
    bool TryParseDate(string? text, out DateTime value, out string? error);

    /// <summary>«از تاریخ / تا تاریخ» را به بازهٔ کانونیک تبدیل می‌کند؛ مقدارِ خالی یعنی بدون مرز.</summary>
    CalendarDateRange ParseRange(string? from, string? to);

    /// <summary>اول و آخرِ یک ماهِ شمسی به روزِ کانونیک.</summary>
    (DateTime Start, DateTime End) SolarMonthRange(int year, int month);

    /// <summary>اول و آخرِ یک سالِ شمسی (۱ حمل تا آخرِ حوت) به روزِ کانونیک.</summary>
    (DateTime Start, DateTime End) SolarYearRange(int year);

    /// <summary>«امروزِ کاری» کابل (کانونیک).</summary>
    DateTime Today { get; }

    string TodayText { get; }
}

public sealed class AppCalendarService : IAppCalendarService
{
    private const string GregorianFormatMessage = "تاریخ میلادی معتبر نیست؛ مثال: 2026-10-04";

    private static readonly string[] GregorianFormats = ["yyyy-MM-dd", "yyyy/MM/dd", "yyyy-M-d", "yyyy/M/d"];

    private readonly IAfghanistanBusinessClock _clock;

    public AppCalendarService(IAfghanistanBusinessClock clock) => _clock = clock;

    public CalendarType CalendarType => AppCalendarContext.Current;
    public bool IsSolarHijri => AppCalendarContext.IsSolarHijri;
    public IReadOnlyList<string> MonthNames => AfghanSolarCalendar.Months;

    public SolarDate ToSolar(DateTime value) => AfghanSolarCalendar.FromGregorian(value);

    public DateTime ToGregorian(int year, int month, int day) => AfghanSolarCalendar.ToGregorian(year, month, day);

    public string FormatDate(DateTime? value, string empty = "-")
        => value.HasValue ? Helpers.DateDisplay.Format(value.Value, Helpers.DateDisplay.DisplayDatePattern) : empty;

    public string FormatDateTime(DateTime? value, string empty = "-")
        => value.HasValue ? Helpers.DateDisplay.Format(value.Value, Helpers.DateDisplay.DisplayDateTimePattern) : empty;

    public string FormatLongDate(DateTime? value, string empty = "-")
        => value.HasValue ? Helpers.DateDisplay.LongDateText(value.Value) : empty;

    public bool TryParseDate(string? text, out DateTime value, out string? error)
    {
        value = default;
        var normalized = AfghanSolarCalendar.NormalizeDigits(text);
        if (normalized.Length == 0)
        {
            error = CalendarMessages.Required;
            return false;
        }

        if (AfghanSolarCalendar.LooksLikeSolar(normalized))
        {
            // در حالت میلادی متنِ شمسی‌شکل (مثل 1405/07/12) هرگز به‌عنوان سالِ ۱۴۰۵ میلادی پذیرفته نمی‌شود.
            if (!IsSolarHijri)
            {
                error = GregorianFormatMessage;
                return false;
            }

            if (!AfghanSolarCalendar.TryParse(normalized, out var solar, out error))
                return false;

            value = solar.ToGregorian();
            return true;
        }

        if (DateTime.TryParseExact(normalized, GregorianFormats, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var gregorian))
        {
            value = DateTime.SpecifyKind(gregorian.Date, DateTimeKind.Utc);
            error = null;
            return true;
        }

        error = IsSolarHijri ? CalendarMessages.Format : GregorianFormatMessage;
        return false;
    }

    public CalendarDateRange ParseRange(string? from, string? to)
    {
        var errors = new List<string>();
        DateTime? start = null, end = null;

        if (!string.IsNullOrWhiteSpace(from))
        {
            if (TryParseDate(from, out var parsed, out var error)) start = parsed;
            else errors.Add("از تاریخ: " + error);
        }

        if (!string.IsNullOrWhiteSpace(to))
        {
            if (TryParseDate(to, out var parsed, out var error)) end = parsed;
            else errors.Add("تا تاریخ: " + error);
        }

        if (start.HasValue && end.HasValue && start > end)
            errors.Add("«از تاریخ» نباید بعد از «تا تاریخ» باشد.");

        return new CalendarDateRange(start, end, errors);
    }

    public (DateTime Start, DateTime End) SolarMonthRange(int year, int month)
        => (AfghanSolarCalendar.ToGregorian(year, month, 1),
            AfghanSolarCalendar.ToGregorian(year, month, AfghanSolarCalendar.DaysInMonth(year, month)));

    public (DateTime Start, DateTime End) SolarYearRange(int year)
        => (AfghanSolarCalendar.ToGregorian(year, 1, 1),
            AfghanSolarCalendar.ToGregorian(year, 12, AfghanSolarCalendar.DaysInMonth(year, 12)));

    public DateTime Today => _clock.Today;

    public string TodayText => FormatDate(_clock.Today);
}
