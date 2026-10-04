using System.Text.RegularExpressions;
using PTGOilSystem.Web.Services.Calendars;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// ریاضی، اعتبارسنجی و قالب‌بندیِ تقویم هجری شمسی افغانستان. جفت‌های تبدیل با جدولِ کاملِ
/// <c>PersianCalendar</c> داتنت و تاریخ‌های مشهور (نوروزها) هم‌خوان‌اند.
/// </summary>
public class AfghanSolarCalendarTests
{
    public static IEnumerable<object[]> KnownConversionPairs() =>
    [
        // Solar (y, m, d), Gregorian ISO
        [1300, 1, 1, "1921-03-21"],
        [1357, 1, 1, "1978-03-21"],
        [1399, 1, 1, "2020-03-20"],
        [1399, 12, 30, "2021-03-20"],   // 1399 کبیسه — حوت ۳۰ روزه
        [1400, 1, 1, "2021-03-21"],
        [1402, 1, 1, "2023-03-21"],
        [1402, 12, 29, "2024-03-19"],
        [1403, 1, 1, "2024-03-20"],
        [1403, 12, 30, "2025-03-20"],   // 1403 کبیسه
        [1404, 1, 1, "2025-03-21"],
        [1404, 6, 31, "2025-09-22"],    // آخرِ سنبله
        [1404, 7, 1, "2025-09-23"],     // ۱ میزان
        [1404, 12, 29, "2026-03-20"],   // 1404 عادی — آخرِ حوت ۲۹
        [1405, 1, 1, "2026-03-21"],     // ۱ حمل ۱۴۰۵
        [1405, 7, 12, "2026-10-04"],    // ۱۲ میزان ۱۴۰۵
        [1405, 10, 1, "2026-12-22"],    // ۱ جدی
        [1405, 11, 1, "2027-01-21"],    // ۱ دلو
        [1405, 12, 29, "2027-03-20"],
        [1406, 1, 1, "2027-03-21"],
        [1408, 12, 30, "2030-03-20"],   // 1408 کبیسه
        [1409, 1, 1, "2030-03-21"],
    ];

    [Theory]
    [MemberData(nameof(KnownConversionPairs))]
    public void Gregorian_To_Solar_Matches_Known_Pairs(int year, int month, int day, string iso)
    {
        var solar = AfghanSolarCalendar.FromGregorian(DateTime.Parse(iso));

        Assert.Equal(new SolarDate(year, month, day), solar);
    }

    [Theory]
    [MemberData(nameof(KnownConversionPairs))]
    public void Solar_To_Gregorian_Matches_Known_Pairs(int year, int month, int day, string iso)
    {
        var gregorian = AfghanSolarCalendar.ToGregorian(year, month, day);

        Assert.Equal(iso, gregorian.ToString("yyyy-MM-dd"));
        Assert.Equal(TimeSpan.Zero, gregorian.TimeOfDay);
        Assert.Equal(DateTimeKind.Utc, gregorian.Kind);
    }

    [Theory]
    [MemberData(nameof(KnownConversionPairs))]
    public void DateOnly_Conversion_Matches_Known_Pairs(int year, int month, int day, string iso)
    {
        var dateOnly = DateOnly.Parse(iso);

        Assert.Equal(new SolarDate(year, month, day), AfghanSolarCalendar.FromGregorian(dateOnly));
        Assert.Equal(dateOnly, AfghanSolarCalendar.ToDateOnly(year, month, day));
        Assert.Equal(dateOnly, new SolarDate(year, month, day).ToDateOnly());
    }

    [Fact]
    public void Every_Day_Round_Trips_Without_Shifting()
    {
        for (var day = new DateTime(2015, 1, 1); day <= new DateTime(2035, 12, 31); day = day.AddDays(1))
        {
            var solar = AfghanSolarCalendar.FromGregorian(day);
            Assert.Equal(day, AfghanSolarCalendar.ToGregorian(solar.Year, solar.Month, solar.Day));
        }
    }

    [Fact]
    public void Time_Of_Day_And_Kind_Never_Change_The_Solar_Day()
    {
        var expected = new SolarDate(1405, 7, 12);

        Assert.Equal(expected, AfghanSolarCalendar.FromGregorian(new DateTime(2026, 10, 4, 0, 0, 0, DateTimeKind.Utc)));
        Assert.Equal(expected, AfghanSolarCalendar.FromGregorian(new DateTime(2026, 10, 4, 23, 59, 59, DateTimeKind.Utc)));
        Assert.Equal(expected, AfghanSolarCalendar.FromGregorian(new DateTime(2026, 10, 4, 12, 0, 0, DateTimeKind.Unspecified)));
    }

    [Fact]
    public void Uses_Exactly_The_Twelve_Afghan_Dari_Month_Names()
    {
        Assert.Equal(
            ["حمل", "ثور", "جوزا", "سرطان", "اسد", "سنبله", "میزان", "عقرب", "قوس", "جدی", "دلو", "حوت"],
            AfghanSolarCalendar.Months);

        for (var month = 1; month <= 12; month++)
            Assert.Equal(AfghanSolarCalendar.Months[month - 1], AfghanSolarCalendar.MonthName(month));
    }

    [Fact]
    public void Never_Uses_Iranian_Month_Names()
    {
        string[] iranian = ["فروردین", "اردیبهشت", "خرداد", "تیر", "مرداد", "شهریور", "مهر", "آبان", "آذر", "دی", "بهمن", "اسفند"];

        foreach (var name in AfghanSolarCalendar.Months)
            Assert.DoesNotContain(name, iranian);

        for (var month = 1; month <= 12; month++)
        {
            var text = AfghanSolarCalendar.Format(AfghanSolarCalendar.ToGregorian(1405, month, 1), "d MMMM yyyy");
            Assert.DoesNotContain(text.Split(' ')[1], iranian);
        }
    }

    [Theory]
    [InlineData(1404, new[] { 31, 31, 31, 31, 31, 31, 30, 30, 30, 30, 30, 29 })]
    [InlineData(1403, new[] { 31, 31, 31, 31, 31, 31, 30, 30, 30, 30, 30, 30 })]
    [InlineData(1405, new[] { 31, 31, 31, 31, 31, 31, 30, 30, 30, 30, 30, 29 })]
    public void Month_Lengths_And_Leap_Hoot(int year, int[] lengths)
    {
        Assert.Equal(lengths[11] == 30, AfghanSolarCalendar.IsLeapYear(year));
        for (var month = 1; month <= 12; month++)
        {
            Assert.Equal(lengths[month - 1], AfghanSolarCalendar.DaysInMonth(year, month));

            // آخرِ هر ماه معتبر است و روزِ بعدش اولِ ماهِ بعد.
            var last = AfghanSolarCalendar.ToGregorian(year, month, lengths[month - 1]);
            var next = AfghanSolarCalendar.FromGregorian(last.AddDays(1));
            Assert.Equal(month == 12 ? new SolarDate(year + 1, 1, 1) : new SolarDate(year, month + 1, 1), next);
            Assert.False(AfghanSolarCalendar.IsValid(year, month, lengths[month - 1] + 1));
        }
    }

    [Theory]
    [InlineData("1405/07/12")]
    [InlineData("1405-07-12")]
    [InlineData("1405.7.12")]
    [InlineData(" 1405/7/12 ")]
    [InlineData("۱۴۰۵/۰۷/۱۲")]      // ارقام فارسی
    [InlineData("١٤٠٥/٠٧/١٢")]      // ارقام عربی
    [InlineData("‎1405/07/12‎")]
    public void Parses_Latin_And_Persian_And_Arabic_Digits(string text)
    {
        Assert.True(AfghanSolarCalendar.TryParse(text, out var date, out var error), error);
        Assert.Equal(new SolarDate(1405, 7, 12), date);
        Assert.Equal(new DateTime(2026, 10, 4), date.ToGregorian());
    }

    [Fact]
    public void Parses_First_Of_Hamal()
    {
        Assert.True(AfghanSolarCalendar.TryParse("1405/01/01", out var date, out _));
        Assert.Equal(new DateTime(2026, 3, 21), date.ToGregorian());
    }

    [Theory]
    [InlineData("1403/12/30", true)]   // کبیسه
    [InlineData("1404/12/30", false)]  // عادی: حوت ۲۹ روزه
    [InlineData("1404/12/29", true)]
    [InlineData("1408/12/30", true)]
    public void Validates_Hoot_29_30(string text, bool valid)
    {
        Assert.Equal(valid, AfghanSolarCalendar.TryParse(text, out _, out var error));
        if (!valid)
            Assert.Equal("ماه حوت سال 1404 فقط 29 روز دارد.", error);
    }

    [Theory]
    [InlineData("", CalendarMessages.Required)]
    [InlineData("   ", CalendarMessages.Required)]
    [InlineData("abc", CalendarMessages.Format)]
    [InlineData("12/07/1405x", CalendarMessages.Format)]
    [InlineData("1405/13/01", CalendarMessages.Month)]
    [InlineData("1405/00/10", CalendarMessages.Month)]
    [InlineData("1405/07/31", "ماه میزان سال 1405 فقط 30 روز دارد.")]
    [InlineData("1405/01/32", "ماه حمل سال 1405 فقط 31 روز دارد.")]
    [InlineData("1405/07/00", "ماه میزان سال 1405 فقط 30 روز دارد.")]
    public void Rejects_Invalid_Dates_With_Dari_Messages(string text, string expected)
    {
        Assert.False(AfghanSolarCalendar.TryParse(text, out _, out var error));
        Assert.Equal(expected, error);
    }

    [Theory]
    [InlineData("1100/01/01")]
    [InlineData("1600/01/01")]
    public void Rejects_Years_Outside_The_Supported_Range(string text)
    {
        Assert.False(AfghanSolarCalendar.TryParse(text, out _, out var error));
        Assert.Equal(CalendarMessages.Year, error);
    }

    [Theory]
    [InlineData("1405/07/12", true)]
    [InlineData("۱۴۰۵/۰۷/۱۲", true)]
    [InlineData("1405-07-12", true)]
    [InlineData("2026-10-04", false)]    // ISO میلادیِ input تاریخ هرگز شمسی فرض نمی‌شود
    [InlineData("2026/10/04", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Recognises_Solar_Text_But_Not_Gregorian_Iso(string? text, bool expected)
        => Assert.Equal(expected, AfghanSolarCalendar.LooksLikeSolar(text));

    [Theory]
    [InlineData("yyyy/MM/dd", "1405/07/12")]
    [InlineData("yyyy-MM-dd", "1405-07-12")]
    [InlineData("yyyy-MM-dd HH:mm", "1405-07-12 14:35")]
    [InlineData("yyyy/MM/dd HH:mm", "1405/07/12 14:35")]
    [InlineData("yyyy/M/d", "1405/7/12")]
    [InlineData("MM/dd", "07/12")]
    [InlineData("d MMMM yyyy", "12 میزان 1405")]
    public void Formats_With_The_Same_Pattern_As_Gregorian(string pattern, string expected)
        => Assert.Equal(expected, AfghanSolarCalendar.Format(new DateTime(2026, 10, 4, 14, 35, 0), pattern));

    [Fact]
    public void Browser_Picker_Uses_The_Same_Month_Names_And_Year_Range()
    {
        // ak-datepicker.js همان نام‌ها و بازهٔ سال را دارد؛ الگوریتمِ JS در بازهٔ 1200–1500 روز به روز با
        // PersianCalendar داتنت مقایسه شده است. این تست جلوی جدا شدنِ تصادفیِ دو طرف را می‌گیرد.
        var script = File.ReadAllText(Path.Combine(FindWebRoot(), "wwwroot", "js", "ak-datepicker.js"));

        var months = Regex.Match(script, @"var SOLAR_MONTHS = \[(?<list>[^\]]+)\]").Groups["list"].Value;
        Assert.Equal(
            AfghanSolarCalendar.Months,
            Regex.Matches(months, "\"(?<m>[^\"]+)\"").Select(m => m.Groups["m"].Value).ToArray());
        Assert.Contains($"var SOLAR_MIN_YEAR = {AfghanSolarCalendar.MinYear};", script);
        Assert.Contains($"var SOLAR_MAX_YEAR = {AfghanSolarCalendar.MaxYear};", script);
    }

    private static string FindWebRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "src", "PTGOilSystem.Web");
            if (Directory.Exists(candidate))
                return candidate;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("src/PTGOilSystem.Web not found.");
    }
}
