using PTGOilSystem.Web.Services.Time;
using Xunit;

namespace PTGOilSystem.Web.Tests;

public class AfghanistanBusinessClockTests
{
    [Fact]
    public void Today_And_Utc_Range_Use_Kabul_Boundary()
    {
        var provider = new FixedTimeProvider(new DateTimeOffset(2026, 5, 1, 19, 30, 0, TimeSpan.Zero));
        var clock = new AfghanistanBusinessClock(provider);

        Assert.Equal(new DateTime(2026, 5, 2), clock.Today);
        var range = clock.UtcRange(new DateTime(2026, 5, 2));
        Assert.Equal(new DateTime(2026, 5, 1, 19, 30, 0, DateTimeKind.Utc), range.StartUtc);
        Assert.Equal(new DateTime(2026, 5, 2, 19, 30, 0, DateTimeKind.Utc), range.EndUtcExclusive);
    }

    // مرز روز کاری کابل: UTC+04:30 بدون تغییر ساعت تابستانی.
    // 00:00 کابل = 19:30 روز قبل به وقت UTC، و 23:59 کابل = 19:29 همان روز به وقت UTC.
    [Theory]
    // یک ثانیه پیش از نیمه‌شب کابل، هنوز روز قبل است.
    [InlineData(2026, 5, 1, 19, 29, 59, 2026, 5, 1)]
    // دقیقاً 00:00:00 کابل — روز جدید شروع می‌شود.
    [InlineData(2026, 5, 1, 19, 30, 0, 2026, 5, 2)]
    // 23:59:59 کابل — هنوز همان روز است.
    [InlineData(2026, 5, 2, 19, 29, 59, 2026, 5, 2)]
    // 00:00:00 کابلِ روز بعد.
    [InlineData(2026, 5, 2, 19, 30, 0, 2026, 5, 3)]
    public void Today_Rolls_Over_Exactly_At_Kabul_Midnight(
        int utcYear, int utcMonth, int utcDay, int utcHour, int utcMinute, int utcSecond,
        int expectedYear, int expectedMonth, int expectedDay)
    {
        var provider = new FixedTimeProvider(
            new DateTimeOffset(utcYear, utcMonth, utcDay, utcHour, utcMinute, utcSecond, TimeSpan.Zero));
        var clock = new AfghanistanBusinessClock(provider);

        Assert.Equal(new DateTime(expectedYear, expectedMonth, expectedDay), clock.Today);
    }

    [Fact]
    public void Today_Is_Marked_Utc_So_Npgsql_Can_Bind_It_As_A_Date_Key()
    {
        var provider = new FixedTimeProvider(new DateTimeOffset(2026, 5, 1, 19, 30, 0, TimeSpan.Zero));
        var clock = new AfghanistanBusinessClock(provider);

        Assert.Equal(DateTimeKind.Utc, clock.Today.Kind);
        Assert.Equal(TimeSpan.Zero, clock.Today.TimeOfDay);
    }

    [Fact]
    public void Utc_Range_Covers_Exactly_One_Kabul_Day_And_Is_Half_Open()
    {
        var provider = new FixedTimeProvider(new DateTimeOffset(2026, 5, 2, 6, 0, 0, TimeSpan.Zero));
        var clock = new AfghanistanBusinessClock(provider);

        var range = clock.UtcRange(new DateTime(2026, 5, 2));

        Assert.Equal(TimeSpan.FromDays(1), range.EndUtcExclusive - range.StartUtc);
        // آخرین لحظهٔ روز کاری کابل داخل بازه است، اما ابتدای روز بعد نیست.
        Assert.True(range.StartUtc <= new DateTime(2026, 5, 2, 19, 29, 59, DateTimeKind.Utc));
        Assert.True(new DateTime(2026, 5, 2, 19, 29, 59, DateTimeKind.Utc) < range.EndUtcExclusive);
        Assert.Equal(range.EndUtcExclusive, clock.UtcRange(new DateTime(2026, 5, 3)).StartUtc);
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
