using PTGOilSystem.Web.Models.Entities;

namespace PTGOilSystem.Web.Services.Backups;

/// <summary>
/// محاسبهٔ «بکاپ بعدی». عمداً کاملاً خالص و بدون وابستگی است تا رفتار زمان‌بندی
/// مستقل از پایگاه داده و سرویس‌ها قابل آزمون باشد.
///
/// معنای هر دوره (ساعت و دقیقه همیشه به وقت محلیِ پیکربندی‌شده تفسیر می‌شود):
///   • هر ۶ ساعت : چهار نوبت در شبانه‌روز، لنگر روی ساعتِ انتخابی (مثلاً ۲ → ۲، ۸، ۱۴، ۲۰)
///   • روزانه    : هر روز در همان ساعت
///   • هفتگی     : هر شنبه (آغاز هفتهٔ کاری) در همان ساعت
///   • ماهانه    : روز اول هر ماه میلادیِ محلی در همان ساعت
/// </summary>
public static class BackupScheduleCalculator
{
    /// <summary>روز شروع هفته برای زمان‌بندی هفتگی.</summary>
    public const DayOfWeek WeeklyRunDay = DayOfWeek.Saturday;

    public static DateTime GetNextRunUtc(
        DateTime fromUtc,
        BackupSchedule schedule,
        int runAtHourLocal,
        int runAtMinuteLocal,
        TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(timeZone);

        var hour = Math.Clamp(runAtHourLocal, 0, 23);
        var minute = Math.Clamp(runAtMinuteLocal, 0, 59);
        var fromLocal = TimeZoneInfo.ConvertTimeFromUtc(EnsureUtc(fromUtc), timeZone);

        var nextLocal = schedule switch
        {
            BackupSchedule.Every6Hours => NextSixHourSlot(fromLocal, hour, minute),
            BackupSchedule.Weekly => NextWeekly(fromLocal, hour, minute),
            BackupSchedule.Monthly => NextMonthly(fromLocal, hour, minute),
            _ => NextDaily(fromLocal, hour, minute)
        };

        return ToUtc(nextLocal, timeZone);
    }

    /// <summary>نسخهٔ راحت برای فراخوانی با رکورد تنظیمات.</summary>
    public static DateTime GetNextRunUtc(DateTime fromUtc, BackupSetting settings, TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return GetNextRunUtc(fromUtc, settings.Schedule, settings.RunAtHourLocal, settings.RunAtMinuteLocal, timeZone);
    }

    private static DateTime NextSixHourSlot(DateTime fromLocal, int hour, int minute)
    {
        var anchor = hour % 6;
        var day = fromLocal.Date;

        for (var offsetDay = 0; offsetDay <= 1; offsetDay++)
        {
            for (var slotHour = anchor; slotHour < 24; slotHour += 6)
            {
                var candidate = day.AddDays(offsetDay).AddHours(slotHour).AddMinutes(minute);
                if (candidate > fromLocal)
                {
                    return candidate;
                }
            }
        }

        // غیرقابل‌دسترس در عمل؛ فقط برای اطمینان از بازگشت مقدار معتبر.
        return day.AddDays(1).AddHours(anchor).AddMinutes(minute);
    }

    private static DateTime NextDaily(DateTime fromLocal, int hour, int minute)
    {
        var candidate = fromLocal.Date.AddHours(hour).AddMinutes(minute);
        return candidate > fromLocal ? candidate : candidate.AddDays(1);
    }

    private static DateTime NextWeekly(DateTime fromLocal, int hour, int minute)
    {
        var daysUntilRunDay = ((int)WeeklyRunDay - (int)fromLocal.DayOfWeek + 7) % 7;
        var candidate = fromLocal.Date.AddDays(daysUntilRunDay).AddHours(hour).AddMinutes(minute);
        return candidate > fromLocal ? candidate : candidate.AddDays(7);
    }

    private static DateTime NextMonthly(DateTime fromLocal, int hour, int minute)
    {
        var firstOfMonth = new DateTime(fromLocal.Year, fromLocal.Month, 1, 0, 0, 0, DateTimeKind.Unspecified);
        var candidate = firstOfMonth.AddHours(hour).AddMinutes(minute);
        return candidate > fromLocal ? candidate : firstOfMonth.AddMonths(1).AddHours(hour).AddMinutes(minute);
    }

    private static DateTime ToUtc(DateTime local, TimeZoneInfo timeZone)
    {
        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);

        // ساعتِ حذف‌شده در گذر ساعت تابستانی وجود خارجی ندارد؛ به اولین لحظهٔ معتبر بعدی می‌رود.
        if (timeZone.IsInvalidTime(unspecified))
        {
            unspecified = unspecified.AddHours(1);
        }

        return TimeZoneInfo.ConvertTimeToUtc(unspecified, timeZone);
    }

    private static DateTime EnsureUtc(DateTime value)
        => value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };

    /// <summary>خواندن منطقهٔ زمانی پیکربندی‌شده با بازگشت امن به UTC اگر شناسه روی سیستم نبود.</summary>
    public static TimeZoneInfo ResolveTimeZone(string? timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId))
        {
            return TimeZoneInfo.Utc;
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return TimeZoneInfo.Utc;
        }
    }
}
