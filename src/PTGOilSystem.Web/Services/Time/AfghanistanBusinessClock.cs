namespace PTGOilSystem.Web.Services.Time;

/// <summary>
/// The single calendar boundary for business dates shown and filtered by PTG.
/// Persistence timestamps remain UTC; only business-day selection uses Kabul time.
/// </summary>
public interface IAfghanistanBusinessClock
{
    DateTime Today { get; }

    /// <summary>
    /// لحظهٔ جاری به وقت کابل. برای برچسب‌های «تاریخ تولید» و مقدار پیش‌فرض ساعت در فرم‌ها
    /// استفاده می‌شود تا ساعت محلی سیستم‌عاملِ سرور هیچ‌جا منبع قطعی نباشد.
    /// </summary>
    DateTimeOffset Now { get; }

    (DateTime StartUtc, DateTime EndUtcExclusive) UtcRange(DateTime localDate);
}

public sealed class AfghanistanBusinessClock : IAfghanistanBusinessClock
{
    private readonly TimeProvider _timeProvider;
    private readonly TimeZoneInfo _timeZone;

    public AfghanistanBusinessClock(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
        _timeZone = ResolveKabulTimeZone();
    }

    public DateTime Today
        // Existing PTG business-date columns are mapped to PostgreSQL timestamptz.
        // Preserve the Kabul calendar components but mark the value UTC so Npgsql
        // can bind it; this is a date key, not an instant conversion.
        => DateTime.SpecifyKind(
            TimeZoneInfo.ConvertTime(_timeProvider.GetUtcNow(), _timeZone).Date,
            DateTimeKind.Utc);

    public DateTimeOffset Now => TimeZoneInfo.ConvertTime(_timeProvider.GetUtcNow(), _timeZone);

    private static readonly AfghanistanBusinessClock SystemClock = new(TimeProvider.System);

    /// <summary>
    /// «امروزِ کاری» کابل برای جاهایی که تزریق وابستگی ممکن نیست — یعنی مقدار پیش‌فرضِ
    /// property در ViewModel و Entity. این جایگزینِ <c>DateTime.UtcNow.Date</c> است، نه
    /// جایگزینِ تزریق: هر Controller/Service که فرم را می‌سازد باید مقدار قطعی را از
    /// <see cref="IAfghanistanBusinessClock"/> تزریق‌شده بنویسد و روی این پیش‌فرض بازنویسی کند.
    /// هرگز برای timestamp فنی (CreatedAtUtc، PostedAtUtc، Audit، Token) استفاده نشود.
    /// </summary>
    public static DateTime SystemToday => SystemClock.Today;

    public (DateTime StartUtc, DateTime EndUtcExclusive) UtcRange(DateTime localDate)
    {
        var startLocal = DateTime.SpecifyKind(localDate.Date, DateTimeKind.Unspecified);
        var endLocal = startLocal.AddDays(1);
        return (
            TimeZoneInfo.ConvertTimeToUtc(startLocal, _timeZone),
            TimeZoneInfo.ConvertTimeToUtc(endLocal, _timeZone));
    }

    private static TimeZoneInfo ResolveKabulTimeZone()
    {
        foreach (var id in new[] { "Asia/Kabul", "Afghanistan Standard Time" })
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (TimeZoneNotFoundException)
            {
            }
            catch (InvalidTimeZoneException)
            {
            }
        }

        return TimeZoneInfo.CreateCustomTimeZone(
            "PTG-Afghanistan",
            TimeSpan.FromHours(4.5),
            "Afghanistan",
            "Afghanistan");
    }
}
