using System.Globalization;
using System.Text.RegularExpressions;
using PTGOilSystem.Web.Services.Calendars;

namespace PTGOilSystem.Web.Helpers;

/// <summary>
/// قالب‌بندیِ تاریخ برای نمایش. تقویمِ فعال (<see cref="AppCalendarContext"/>) را رعایت می‌کند: در حالت
/// میلادی خروجی دقیقاً مثل قبل است و در حالت هجری شمسی افغانستان همان الگو با اجزای شمسی ساخته می‌شود.
/// مقدارِ input تاریخ (<see cref="HtmlDateInput(System.DateTime)"/>) همیشه ISO میلادیِ کانونیک می‌ماند.
/// ماه‌های <see cref="Month(System.DateTime)"/> عمداً میلادی‌اند: دورهٔ Platts و ماهِ معاش ماهِ میلادی‌اند.
/// </summary>
public static class DateDisplay
{
    public const string DisplayDatePattern = "yyyy/MM/dd";
    public const string DisplayDateTimePattern = "yyyy/MM/dd HH:mm";
    public const string DisplayTimePattern = "HH:mm:ss";
    public const string DisplayMonthPattern = "yyyy/MM";
    public const string HtmlDateInputPattern = "yyyy-MM-dd";
    public const string HtmlMonthInputPattern = "yyyy-MM";

    private const string EmptyValue = "-";
    private const char LeftToRightMark = '\u200E';
    private static readonly CultureInfo InvariantCulture = CultureInfo.InvariantCulture;
    private static readonly TimeZoneInfo AfghanistanTimeZone = ResolveAfghanistanTimeZone();
    private static readonly Regex IsoDatePattern = new(@"^\d{4}-\d{2}-\d{2}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static bool IsSolarHijri => AppCalendarContext.IsSolarHijri;

    /// <summary>
    /// همان <c>value.ToString(pattern, provider)</c>؛ در حالت شمسی سال/ماه/روز شمسی می‌شوند و
    /// جداکننده‌ها و اجزای ساعت دست‌نخورده می‌مانند.
    /// </summary>
    public static string Format(DateTime value, string pattern, IFormatProvider? provider = null)
        => IsSolarHijri
            ? AfghanSolarCalendar.Format(value, pattern)
            : value.ToString(pattern, provider ?? InvariantCulture);

    public static string Date(DateTime value)
        => Isolate(Format(value, DisplayDatePattern));

    public static string Date(DateTime? value, string empty = EmptyValue)
        => value.HasValue ? Date(value.Value) : empty;

    public static string DateTime(DateTime value)
        => Isolate(Format(value, DisplayDateTimePattern));

    public static string DateTime(DateTime? value, string empty = EmptyValue)
        => value.HasValue ? DateTime(value.Value) : empty;

    public static string AfghanistanDateTime(DateTime value)
        => DateTime(ToAfghanistanLocalTime(value));

    public static string AfghanistanDateTime(DateTime? value, string empty = EmptyValue)
        => value.HasValue ? AfghanistanDateTime(value.Value) : empty;

    // ساعت محلی کابل — برای ستون «ساعت» فهرست‌هایی که تاریخشان بدون ساعت ذخیره می‌شود.
    public static string AfghanistanTime(DateTime value)
        => Isolate(ToAfghanistanLocalTime(value).ToString(DisplayTimePattern, InvariantCulture));

    public static string AfghanistanTime(DateTime? value, string empty = EmptyValue)
        => value.HasValue ? AfghanistanTime(value.Value) : empty;

    public static string Month(DateTime value)
        => Isolate(value.ToString(DisplayMonthPattern, InvariantCulture));

    public static string Month(DateTime? value, string empty = EmptyValue)
        => value.HasValue ? Month(value.Value) : empty;

    /// <summary>تاریخ بدون نشانهٔ جهت — برای پیام‌ها، خروجی Excel/PDF و متنِ ساده.</summary>
    public static string PlainDate(DateTime value)
        => Format(value, DisplayDatePattern);

    public static string PlainDate(DateTime? value, string empty = EmptyValue)
        => value.HasValue ? PlainDate(value.Value) : empty;

    public static string PlainDateTime(DateTime value)
        => Format(value, DisplayDateTimePattern);

    /// <summary>«12 میزان 1405» در حالت شمسی؛ «4 Oct 2026» در حالت میلادی.</summary>
    public static string LongDateText(DateTime value)
        => IsSolarHijri
            ? AfghanSolarCalendar.Format(value, "d MMMM yyyy")
            : value.ToString("d MMM yyyy", InvariantCulture);

    public static string LongDate(DateTime value)
        => Isolate(LongDateText(value));

    public static string LongDate(DateTime? value, string empty = EmptyValue)
        => value.HasValue ? LongDate(value.Value) : empty;

    /// <summary>
    /// متنِ ISO «yyyy-MM-dd» (مقدارِ فیلتر یا query string) را برای نمایش به تقویمِ فعال می‌برد. در حالت
    /// میلادی یا برای متنِ غیرتاریخ، خودِ متن برمی‌گردد.
    /// </summary>
    public static string IsoTextToDisplay(string? value)
    {
        if (string.IsNullOrEmpty(value) || !IsSolarHijri || !IsoDatePattern.IsMatch(value.Trim()))
            return value ?? string.Empty;

        return global::System.DateTime.TryParseExact(value.Trim(), HtmlDateInputPattern, InvariantCulture,
                DateTimeStyles.None, out var parsed)
            ? PlainDate(parsed)
            : value;
    }

    public static string HtmlDateInput(DateTime value)
        => value.ToString(HtmlDateInputPattern, InvariantCulture);

    public static string HtmlDateInput(DateTime? value)
        => value.HasValue ? HtmlDateInput(value.Value) : string.Empty;

    public static string HtmlMonthInput(DateTime value)
        => value.ToString(HtmlMonthInputPattern, InvariantCulture);

    public static string HtmlMonthInput(DateTime? value)
        => value.HasValue ? HtmlMonthInput(value.Value) : string.Empty;

    private static string Isolate(string value)
        => string.Concat(LeftToRightMark, value, LeftToRightMark);

    private static DateTime ToAfghanistanLocalTime(DateTime value)
    {
        var utcValue = value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => global::System.DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };

        return TimeZoneInfo.ConvertTimeFromUtc(utcValue, AfghanistanTimeZone);
    }

    private static TimeZoneInfo ResolveAfghanistanTimeZone()
    {
        foreach (var timeZoneId in new[] { "Asia/Kabul", "Afghanistan Standard Time" })
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                // Try the equivalent IANA/Windows identifier.
            }
        }

        return TimeZoneInfo.CreateCustomTimeZone(
            "Afghanistan",
            TimeSpan.FromMinutes(270),
            "Afghanistan",
            "Afghanistan");
    }
}

public static class DateDisplayExtensions
{
    /// <summary>
    /// جایگزینِ <c>value.ToString(pattern)</c> برای متنِ قابل‌دیدِ کاربر: در حالت میلادی دقیقاً همان خروجی،
    /// در حالت شمسی همان الگو با تاریخِ شمسی. برای کلید، نام فایل، مقدارِ input یا query string استفاده نشود.
    /// </summary>
    public static string ToCalendarString(this DateTime value, string pattern)
        => DateDisplay.IsSolarHijri
            ? AfghanSolarCalendar.Format(value, pattern)
            : value.ToString(pattern);

    /// <summary>مثل <c>$"{value:pattern}"</c> برای مقدار nullable: null یعنی متنِ خالی.</summary>
    public static string ToCalendarString(this DateTime? value, string pattern)
        => value.HasValue ? value.Value.ToCalendarString(pattern) : string.Empty;

    public static string ToCalendarString(this DateTime value, string pattern, IFormatProvider provider)
        => DateDisplay.IsSolarHijri
            ? AfghanSolarCalendar.Format(value, pattern)
            : value.ToString(pattern, provider);

    public static string ToLongDisplayDate(this DateTime value)
        => DateDisplay.LongDate(value);

    public static string ToLongDisplayDate(this DateTime? value)
        => DateDisplay.LongDate(value);

    public static string ToDisplayDate(this DateTime value)
        => DateDisplay.Date(value);

    public static string ToDisplayDate(this DateTime? value)
        => DateDisplay.Date(value);

    public static string ToDisplayDateTime(this DateTime value)
        => DateDisplay.DateTime(value);

    public static string ToDisplayDateTime(this DateTime? value)
        => DateDisplay.DateTime(value);

    public static string ToAfghanistanDisplayDateTime(this DateTime value)
        => DateDisplay.AfghanistanDateTime(value);

    public static string ToAfghanistanDisplayDateTime(this DateTime? value)
        => DateDisplay.AfghanistanDateTime(value);

    public static string ToAfghanistanDisplayTime(this DateTime value)
        => DateDisplay.AfghanistanTime(value);

    public static string ToAfghanistanDisplayTime(this DateTime? value)
        => DateDisplay.AfghanistanTime(value);

    public static string ToDisplayMonth(this DateTime value)
        => DateDisplay.Month(value);

    public static string ToDisplayMonth(this DateTime? value)
        => DateDisplay.Month(value);

    public static string ToHtmlDateInput(this DateTime value)
        => DateDisplay.HtmlDateInput(value);

    public static string ToHtmlDateInput(this DateTime? value)
        => DateDisplay.HtmlDateInput(value);

    public static string ToHtmlMonthInput(this DateTime value)
        => DateDisplay.HtmlMonthInput(value);

    public static string ToHtmlMonthInput(this DateTime? value)
        => DateDisplay.HtmlMonthInput(value);
}
