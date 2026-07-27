using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services.Backups;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// منطق زمان‌بندی بکاپ خودکار. ساعت و دقیقه همیشه به وقت محلی تفسیر می‌شوند،
/// پس آزمون‌ها با یک منطقهٔ زمانی ثابتِ بدون DST (+04:30 کابل) نوشته شده‌اند.
/// </summary>
public class BackupScheduleCalculatorTests
{
    // کابل +04:30 و بدون ساعت تابستانی؛ اگر روی سیستم نبود، UTC جایگزین می‌شود.
    private static readonly TimeZoneInfo Kabul = BackupScheduleCalculator.ResolveTimeZone("Asia/Kabul");

    private static DateTime LocalOf(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(utc, Kabul);

    [Fact]
    public void Daily_Default_Runs_At_Two_AM_Local_On_The_Same_Day_When_Still_Ahead()
    {
        // ساعت محلی ۰۰:۳۰ همان روز
        var fromUtc = ToUtc(new DateTime(2026, 3, 10, 0, 30, 0));

        var next = BackupScheduleCalculator.GetNextRunUtc(fromUtc, BackupSchedule.Daily, 2, 0, Kabul);

        var nextLocal = LocalOf(next);
        Assert.Equal(new DateTime(2026, 3, 10, 2, 0, 0), nextLocal);
    }

    [Fact]
    public void Daily_Rolls_To_Tomorrow_When_The_Slot_Already_Passed()
    {
        var fromUtc = ToUtc(new DateTime(2026, 3, 10, 2, 0, 1));

        var next = BackupScheduleCalculator.GetNextRunUtc(fromUtc, BackupSchedule.Daily, 2, 0, Kabul);

        Assert.Equal(new DateTime(2026, 3, 11, 2, 0, 0), LocalOf(next));
    }

    [Theory]
    [InlineData(1, 2)]    // ۰۱:۰۰ → ۰۲:۰۰
    [InlineData(3, 8)]    // ۰۳:۰۰ → ۰۸:۰۰
    [InlineData(9, 14)]   // ۰۹:۰۰ → ۱۴:۰۰
    [InlineData(15, 20)]  // ۱۵:۰۰ → ۲۰:۰۰
    public void Every6Hours_Uses_Four_Slots_Anchored_On_The_Configured_Hour(int fromHourLocal, int expectedHourLocal)
    {
        var fromUtc = ToUtc(new DateTime(2026, 3, 10, fromHourLocal, 0, 0));

        var next = BackupScheduleCalculator.GetNextRunUtc(fromUtc, BackupSchedule.Every6Hours, 2, 0, Kabul);

        Assert.Equal(expectedHourLocal, LocalOf(next).Hour);
        Assert.Equal(10, LocalOf(next).Day);
    }

    [Fact]
    public void Every6Hours_Wraps_To_The_First_Slot_Of_The_Next_Day()
    {
        // بعد از آخرین نوبت روز (۲۰:۰۰) نوبت بعدی ۰۲:۰۰ فردا است.
        var fromUtc = ToUtc(new DateTime(2026, 3, 10, 21, 0, 0));

        var next = BackupScheduleCalculator.GetNextRunUtc(fromUtc, BackupSchedule.Every6Hours, 2, 0, Kabul);

        Assert.Equal(new DateTime(2026, 3, 11, 2, 0, 0), LocalOf(next));
    }

    [Fact]
    public void Weekly_Runs_On_Saturday_At_The_Configured_Time()
    {
        // ۱۰ مارس ۲۰۲۶ سه‌شنبه است؛ اولین شنبهٔ بعد ۱۴ مارس است.
        var fromUtc = ToUtc(new DateTime(2026, 3, 10, 5, 0, 0));

        var next = BackupScheduleCalculator.GetNextRunUtc(fromUtc, BackupSchedule.Weekly, 2, 0, Kabul);

        var nextLocal = LocalOf(next);
        Assert.Equal(DayOfWeek.Saturday, nextLocal.DayOfWeek);
        Assert.Equal(new DateTime(2026, 3, 14, 2, 0, 0), nextLocal);
    }

    [Fact]
    public void Weekly_Skips_A_Full_Week_When_Saturdays_Slot_Already_Passed()
    {
        var fromUtc = ToUtc(new DateTime(2026, 3, 14, 3, 0, 0)); // شنبه، بعد از ۰۲:۰۰

        var next = BackupScheduleCalculator.GetNextRunUtc(fromUtc, BackupSchedule.Weekly, 2, 0, Kabul);

        Assert.Equal(new DateTime(2026, 3, 21, 2, 0, 0), LocalOf(next));
    }

    [Fact]
    public void Monthly_Runs_On_The_First_Day_Of_The_Next_Month()
    {
        var fromUtc = ToUtc(new DateTime(2026, 3, 10, 5, 0, 0));

        var next = BackupScheduleCalculator.GetNextRunUtc(fromUtc, BackupSchedule.Monthly, 2, 0, Kabul);

        Assert.Equal(new DateTime(2026, 4, 1, 2, 0, 0), LocalOf(next));
    }

    [Fact]
    public void Monthly_Uses_Today_When_It_Is_The_First_And_The_Slot_Is_Ahead()
    {
        var fromUtc = ToUtc(new DateTime(2026, 4, 1, 0, 15, 0));

        var next = BackupScheduleCalculator.GetNextRunUtc(fromUtc, BackupSchedule.Monthly, 2, 0, Kabul);

        Assert.Equal(new DateTime(2026, 4, 1, 2, 0, 0), LocalOf(next));
    }

    [Fact]
    public void Minutes_Are_Honoured_And_Out_Of_Range_Values_Are_Clamped()
    {
        var fromUtc = ToUtc(new DateTime(2026, 3, 10, 0, 0, 0));

        var next = BackupScheduleCalculator.GetNextRunUtc(fromUtc, BackupSchedule.Daily, 99, 99, Kabul);

        var nextLocal = LocalOf(next);
        Assert.Equal(23, nextLocal.Hour);
        Assert.Equal(59, nextLocal.Minute);
    }

    [Fact]
    public void The_Result_Is_Always_Strictly_In_The_Future()
    {
        var fromUtc = ToUtc(new DateTime(2026, 3, 10, 2, 0, 0)); // دقیقاً روی نوبت

        var next = BackupScheduleCalculator.GetNextRunUtc(fromUtc, BackupSchedule.Daily, 2, 0, Kabul);

        Assert.True(next > fromUtc);
    }

    [Fact]
    public void Unknown_TimeZone_Falls_Back_To_Utc_Instead_Of_Throwing()
        => Assert.Equal(TimeZoneInfo.Utc, BackupScheduleCalculator.ResolveTimeZone("Mars/Olympus"));

    [Fact]
    public void Settings_Overload_Matches_The_Explicit_Overload()
    {
        var settings = new BackupSetting { Schedule = BackupSchedule.Daily, RunAtHourLocal = 2, RunAtMinuteLocal = 0 };
        var fromUtc = ToUtc(new DateTime(2026, 3, 10, 0, 30, 0));

        Assert.Equal(
            BackupScheduleCalculator.GetNextRunUtc(fromUtc, BackupSchedule.Daily, 2, 0, Kabul),
            BackupScheduleCalculator.GetNextRunUtc(fromUtc, settings, Kabul));
    }

    private static DateTime ToUtc(DateTime local)
        => TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), Kabul);
}
