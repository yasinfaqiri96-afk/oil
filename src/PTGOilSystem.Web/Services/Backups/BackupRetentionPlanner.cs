using System.Globalization;
using PTGOilSystem.Web.Models.Entities;

namespace PTGOilSystem.Web.Services.Backups;

/// <summary>یک بکاپ موجود، در حداقل شکل لازم برای تصمیم نگهداری.</summary>
public sealed record BackupRetentionCandidate(int Id, DateTime StartedAtUtc, BackupRetentionTier Tier);

/// <summary>سیاست نگهداری Grandfather-Father-Son.</summary>
public sealed record BackupRetentionPolicy(int KeepDaily, int KeepWeekly, int KeepMonthly)
{
    public static BackupRetentionPolicy FromSettings(BackupSetting settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new BackupRetentionPolicy(
            Math.Max(1, settings.KeepDaily),
            Math.Max(1, settings.KeepWeekly),
            Math.Max(1, settings.KeepMonthly));
    }

    public int KeepFor(BackupRetentionTier tier) => tier switch
    {
        BackupRetentionTier.Monthly => KeepMonthly,
        BackupRetentionTier.Weekly => KeepWeekly,
        _ => KeepDaily
    };
}

/// <summary>
/// ردهٔ نگهداری هر بکاپ و انتخاب نسخه‌های قابل حذف. کاملاً خالص و قابل آزمون.
///
/// قاعده: اولین بکاپ موفقِ هر ماه «ماهانه»، اولین بکاپ موفقِ هر هفته «هفتگی»
/// و بقیه «روزانه» هستند. سپس از هر رده فقط N نسخهٔ تازه‌تر نگه داشته می‌شود.
/// </summary>
public static class BackupRetentionPlanner
{
    private static readonly Calendar IsoCalendar = CultureInfo.InvariantCulture.Calendar;

    public static BackupRetentionTier ClassifyTier(
        DateTime candidateUtc,
        IEnumerable<DateTime> existingUtc,
        TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(timeZone);

        var candidateLocal = ToLocal(candidateUtc, timeZone);
        var existingLocal = (existingUtc ?? []).Select(value => ToLocal(value, timeZone)).ToList();

        var hasSameMonth = existingLocal.Any(value =>
            value.Year == candidateLocal.Year && value.Month == candidateLocal.Month);
        if (!hasSameMonth)
        {
            return BackupRetentionTier.Monthly;
        }

        var candidateWeek = WeekKey(candidateLocal);
        var hasSameWeek = existingLocal.Any(value => WeekKey(value) == candidateWeek);
        return hasSameWeek ? BackupRetentionTier.Daily : BackupRetentionTier.Weekly;
    }

    /// <summary>نسخه‌هایی که طبق سیاست نگهداری باید حذف شوند، تازه‌ترین‌ها همیشه می‌مانند.</summary>
    public static IReadOnlyList<BackupRetentionCandidate> SelectExpired(
        IEnumerable<BackupRetentionCandidate> candidates,
        BackupRetentionPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        return (candidates ?? [])
            .GroupBy(candidate => candidate.Tier)
            .SelectMany(group => group
                .OrderByDescending(candidate => candidate.StartedAtUtc)
                .ThenByDescending(candidate => candidate.Id)
                .Skip(policy.KeepFor(group.Key)))
            .OrderBy(candidate => candidate.StartedAtUtc)
            .ToList();
    }

    private static string WeekKey(DateTime local)
    {
        var week = IsoCalendar.GetWeekOfYear(local, CalendarWeekRule.FirstDay, BackupScheduleCalculator.WeeklyRunDay);
        // سال از خودِ هفته گرفته می‌شود تا هفتهٔ مشترک دو سال، دو کلید جدا نگیرد.
        var year = local.AddDays(-(((int)local.DayOfWeek - (int)BackupScheduleCalculator.WeeklyRunDay + 7) % 7)).Year;
        return $"{year}-W{week:00}";
    }

    private static DateTime ToLocal(DateTime value, TimeZoneInfo timeZone)
    {
        var utc = value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };

        return TimeZoneInfo.ConvertTimeFromUtc(utc, timeZone);
    }
}
