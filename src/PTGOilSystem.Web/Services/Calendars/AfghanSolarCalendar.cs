using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PTGOilSystem.Web.Services.Calendars;

/// <summary>یک روزِ هجری شمسی (سال/ماه/روز). فقط برای نمایش و ورود؛ هرگز ذخیره نمی‌شود.</summary>
public readonly record struct SolarDate(int Year, int Month, int Day)
{
    public string MonthName => AfghanSolarCalendar.MonthName(Month);

    public DateTime ToGregorian() => AfghanSolarCalendar.ToGregorian(Year, Month, Day);

    public DateOnly ToDateOnly() => AfghanSolarCalendar.ToDateOnly(Year, Month, Day);

    public override string ToString()
        => string.Create(CultureInfo.InvariantCulture, $"{Year:0000}/{Month:00}/{Day:00}");
}

/// <summary>
/// ریاضیِ تقویم هجری شمسی افغانستان. محاسبه کاملاً به <see cref="PersianCalendar"/> داتنت سپرده
/// شده (همان ساختارِ ۶ ماهِ ۳۱ روزه، ۵ ماهِ ۳۰ روزه و حوتِ ۲۹/۳۰ روزه)؛ این کلاس فقط نام‌های
/// دری افغانستان، نرمال‌سازیِ ارقام و پیام‌های اعتبارسنجی را اضافه می‌کند.
/// </summary>
public static class AfghanSolarCalendar
{
    /// <summary>کم‌ترین و بیشترین سالِ شمسیِ پذیرفتنی در ورود (بازهٔ معقولِ اسناد تجاری).</summary>
    public const int MinYear = 1200;
    public const int MaxYear = 1500;

    /// <summary>ورودی با سالِ کمتر از این مقدار هجری شمسی فرض می‌شود، نه میلادی.</summary>
    private const int GregorianYearThreshold = 1700;

    private static readonly PersianCalendar Calendar = new();

    private static readonly string[] MonthNames =
    [
        "حمل", "ثور", "جوزا", "سرطان", "اسد", "سنبله",
        "میزان", "عقرب", "قوس", "جدی", "دلو", "حوت"
    ];

    public static IReadOnlyList<string> Months { get; } = Array.AsReadOnly(MonthNames);

    private static readonly Regex DatePattern = new(
        @"^(?<y>\d{1,4})\s*[/\-.]\s*(?<m>\d{1,2})\s*[/\-.]\s*(?<d>\d{1,2})$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static string MonthName(int month)
        => month is >= 1 and <= 12
            ? MonthNames[month - 1]
            : throw new ArgumentOutOfRangeException(nameof(month));

    public static SolarDate FromGregorian(DateTime value)
    {
        var date = value.Date;
        return new SolarDate(Calendar.GetYear(date), Calendar.GetMonth(date), Calendar.GetDayOfMonth(date));
    }

    /// <summary>
    /// روزِ میلادیِ کانونیک (نیمه‌شب، Kind=Utc) — همان قراردادِ <c>AfghanistanBusinessClock.Today</c>
    /// برای ستون‌های تاریخِ تجاری؛ هیچ جابه‌جاییِ منطقهٔ زمانی انجام نمی‌شود.
    /// </summary>
    public static DateTime ToGregorian(int year, int month, int day)
    {
        if (!IsValid(year, month, day))
            throw new ArgumentOutOfRangeException(nameof(day), $"Invalid Solar Hijri date {year}/{month}/{day}.");

        return DateTime.SpecifyKind(Calendar.ToDateTime(year, month, day, 0, 0, 0, 0), DateTimeKind.Utc);
    }

    public static SolarDate FromGregorian(DateOnly value)
        => FromGregorian(value.ToDateTime(TimeOnly.MinValue));

    /// <summary>همان روزِ میلادی به‌صورت <see cref="DateOnly"/> — بدون هیچ منطقهٔ زمانی.</summary>
    public static DateOnly ToDateOnly(int year, int month, int day)
        => DateOnly.FromDateTime(ToGregorian(year, month, day));

    public static bool IsLeapYear(int year) => Calendar.IsLeapYear(year);

    public static int DaysInMonth(int year, int month) => Calendar.GetDaysInMonth(year, month);

    public static bool IsValid(int year, int month, int day)
        => year is >= MinYear and <= MaxYear
            && month is >= 1 and <= 12
            && day >= 1
            && day <= DaysInMonth(year, month);

    /// <summary>ارقام فارسی (۰-۹) و عربی (٠-٩) را به لاتین برمی‌گرداند و نشانه‌های جهت را حذف می‌کند.</summary>
    public static string NormalizeDigits(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        var builder = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (ch is >= '۰' and <= '۹')
                builder.Append((char)('0' + (ch - '۰')));
            else if (ch is >= '٠' and <= '٩')
                builder.Append((char)('0' + (ch - '٠')));
            else if (ch is '‎' or '‏' or '‌' or '؜')
                continue;
            else if (ch is '٫' or '∕' or '⁄')
                builder.Append('/');
            else
                builder.Append(ch);
        }

        return builder.ToString().Trim();
    }

    /// <summary>
    /// متن شکلِ «سال/ماه/روز» دارد و سالِ آن کمتر از ۱۷۰۰ است (یعنی میلادی نیست)، یا ارقام فارسی/عربی دارد.
    /// تاریخ میلادیِ ISO (مثل مقدارِ input تاریخ) هرگز اینجا شمسی فرض نمی‌شود.
    /// </summary>
    public static bool LooksLikeSolar(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        if (text.Any(ch => ch is >= '۰' and <= '۹' or >= '٠' and <= '٩'))
            return true;

        var match = DatePattern.Match(NormalizeDigits(text));
        return match.Success
            && int.TryParse(match.Groups["y"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var year)
            && year < GregorianYearThreshold;
    }

    public static bool TryParse(string? text, out SolarDate date, out string? error)
    {
        date = default;
        var normalized = NormalizeDigits(text);
        if (normalized.Length == 0)
        {
            error = CalendarMessages.Required;
            return false;
        }

        var match = DatePattern.Match(normalized);
        if (!match.Success)
        {
            error = CalendarMessages.Format;
            return false;
        }

        var year = int.Parse(match.Groups["y"].Value, CultureInfo.InvariantCulture);
        var month = int.Parse(match.Groups["m"].Value, CultureInfo.InvariantCulture);
        var day = int.Parse(match.Groups["d"].Value, CultureInfo.InvariantCulture);

        if (year is < MinYear or > MaxYear)
        {
            error = CalendarMessages.Year;
            return false;
        }

        if (month is < 1 or > 12)
        {
            error = CalendarMessages.Month;
            return false;
        }

        var daysInMonth = DaysInMonth(year, month);
        if (day < 1 || day > daysInMonth)
        {
            error = CalendarMessages.Day(year, month, daysInMonth);
            return false;
        }

        date = new SolarDate(year, month, day);
        error = null;
        return true;
    }

    /// <summary>
    /// برای متنِ واردشده خارج از فرم (سلولِ Excel/CSV): در حالت هجری شمسی، متنی که شکلِ تاریخِ شمسی دارد
    /// اینجا تبدیل می‌شود تا هرگز به‌اشتباه «سال ۱۴۰۵ میلادی» خوانده نشود. <c>true</c> یعنی متن شمسی
    /// تشخیص داده شد؛ <paramref name="value"/> برای شمسیِ نامعتبر null است. در حالت میلادی همیشه <c>false</c>.
    /// </summary>
    public static bool TryParseActiveCalendarText(string? text, out DateTime? value)
    {
        value = null;
        if (!AppCalendarContext.IsSolarHijri || !LooksLikeSolar(text))
            return false;

        if (TryParse(text, out var solar, out _))
            value = solar.ToGregorian();
        return true;
    }

    /// <summary>
    /// قالب‌بندیِ شمسی با همان الگوی میلادی (yyyy، MM، M، dd، d، MMMM، HH، H، hh، mm، ss، tt) تا
    /// جداکننده‌ها و ترتیبِ هر صفحه بدون تغییر بماند. MMMM نامِ دریِ ماه است.
    /// </summary>
    public static string Format(DateTime value, string pattern)
    {
        var solar = FromGregorian(value);
        var builder = new StringBuilder(pattern.Length + 8);
        for (var index = 0; index < pattern.Length;)
        {
            var ch = pattern[index];
            if (ch is '\'' or '"')
            {
                var end = pattern.IndexOf(ch, index + 1);
                if (end < 0) end = pattern.Length;
                builder.Append(pattern, index + 1, end - index - 1);
                index = end + 1;
                continue;
            }

            if (ch == '\\' && index + 1 < pattern.Length)
            {
                builder.Append(pattern[index + 1]);
                index += 2;
                continue;
            }

            var run = 1;
            while (index + run < pattern.Length && pattern[index + run] == ch) run++;

            switch (ch)
            {
                case 'y':
                    builder.Append(run <= 2
                        ? (solar.Year % 100).ToString(run == 2 ? "00" : "0", CultureInfo.InvariantCulture)
                        : solar.Year.ToString(new string('0', run), CultureInfo.InvariantCulture));
                    break;
                case 'M':
                    builder.Append(run >= 3
                        ? solar.MonthName
                        : solar.Month.ToString(run == 2 ? "00" : "0", CultureInfo.InvariantCulture));
                    break;
                case 'd' when run <= 2:
                    builder.Append(solar.Day.ToString(run == 2 ? "00" : "0", CultureInfo.InvariantCulture));
                    break;
                case 'H' or 'h' or 'm' or 's' or 't' or 'f' or 'F' or 'z' or 'K':
                    // اجزای ساعت از خودِ مقدار می‌آیند؛ تقویم فقط روز را عوض می‌کند.
                    builder.Append(value.ToString(run == 1 ? "%" + ch : new string(ch, run), CultureInfo.InvariantCulture));
                    break;
                default:
                    builder.Append(ch, run);
                    break;
            }

            index += run;
        }

        return builder.ToString();
    }
}

/// <summary>پیام‌های اعتبارسنجیِ تاریخ به دری افغانستان.</summary>
public static class CalendarMessages
{
    public const string Example = "1405/07/12";
    public const string Required = "تاریخ را وارد کنید.";
    public const string Format = "تاریخ را به شکل سال/ماه/روز بنویسید؛ مثال: " + Example;
    public const string Month = "ماه باید عددی از ۱ تا ۱۲ باشد.";

    public static readonly string Year = string.Create(CultureInfo.InvariantCulture,
        $"سال هجری شمسی معتبر نیست؛ سال باید بین {AfghanSolarCalendar.MinYear} و {AfghanSolarCalendar.MaxYear} باشد.");

    public static string Day(int year, int month, int daysInMonth)
        => string.Create(CultureInfo.InvariantCulture,
            $"ماه {AfghanSolarCalendar.MonthName(month)} سال {year} فقط {daysInMonth} روز دارد.");
}
