using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;

namespace PTGOilSystem.Web.Services.HumanResources;

public interface IHrCalendarService
{
    /// <summary>تنظیماتِ ذخیره‌شده، یا پیش‌فرض‌ها اگر هنوز ذخیره نشده (بدونِ نوشتن در دیتابیس).</summary>
    Task<HrSettings> GetSettingsAsync(CancellationToken ct = default);

    /// <summary>روزهای کاریِ بازه: بدونِ رخصتیِ هفته و رخصتیِ رسمی.</summary>
    Task<IReadOnlyList<DateTime>> WorkingDaysAsync(DateTime from, DateTime to, CancellationToken ct = default);

    Task<IReadOnlySet<DateTime>> HolidaysAsync(DateTime from, DateTime to, CancellationToken ct = default);
}

public sealed class HrCalendarService(ApplicationDbContext db) : IHrCalendarService
{
    public async Task<HrSettings> GetSettingsAsync(CancellationToken ct = default)
        => await db.HrSettings.AsNoTracking().OrderBy(s => s.Id).FirstOrDefaultAsync(ct) ?? new HrSettings();

    public async Task<IReadOnlySet<DateTime>> HolidaysAsync(DateTime from, DateTime to, CancellationToken ct = default)
    {
        var start = from.Date;
        var end = to.Date;
        var dates = await db.HrHolidays.AsNoTracking()
            .Where(h => h.Date >= start && h.Date <= end)
            .Select(h => h.Date)
            .ToListAsync(ct);
        return dates.Select(d => d.Date).ToHashSet();
    }

    public async Task<IReadOnlyList<DateTime>> WorkingDaysAsync(DateTime from, DateTime to, CancellationToken ct = default)
    {
        var settings = await GetSettingsAsync(ct);
        var offDays = settings.OffDays();
        var holidays = await HolidaysAsync(from, to, ct);
        var days = new List<DateTime>();
        for (var day = from.Date; day <= to.Date; day = day.AddDays(1))
        {
            if (!offDays.Contains(day.DayOfWeek) && !holidays.Contains(day))
                days.Add(day);
        }

        return days;
    }

    /// <summary>دقیقهٔ تأخیر از ساعتِ ورود؛ اگر در مهلتِ مجاز باشد صفر.</summary>
    public static int? LateMinutes(HrSettings settings, TimeSpan? checkIn)
    {
        if (!checkIn.HasValue)
            return null;

        var late = (int)Math.Floor((checkIn.Value - settings.WorkdayStart).TotalMinutes);
        return late > settings.LateGraceMinutes ? late : 0;
    }
}
