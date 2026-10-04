using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;

namespace PTGOilSystem.Web.Services.Calendars;

/// <summary>
/// خواندن و ذخیرهٔ «نوع تقویم» در ردیفِ یگانهٔ <see cref="SystemSetting"/>. ذخیره فقط همین یک
/// ستون را عوض می‌کند و هیچ رکوردِ تاریخ‌دارِ دیگری را لمس نمی‌کند.
/// </summary>
public interface ICalendarSettingsService
{
    Task<CalendarType> GetAsync(CancellationToken cancellationToken = default);

    /// <summary>ذخیره و اعمالِ فوری. شناسهٔ ردیفِ تنظیمات و مقدار قبلی را برمی‌گرداند.</summary>
    Task<(int SettingId, CalendarType Previous)> SaveAsync(CalendarType calendarType, CancellationToken cancellationToken = default);

    /// <summary>مقدارِ ذخیره‌شده را در <see cref="AppCalendarContext"/> بارگذاری می‌کند.</summary>
    Task<CalendarType> LoadAsync(CancellationToken cancellationToken = default);
}

public sealed class CalendarSettingsService : ICalendarSettingsService
{
    private readonly ApplicationDbContext _db;

    public CalendarSettingsService(ApplicationDbContext db) => _db = db;

    public async Task<CalendarType> GetAsync(CancellationToken cancellationToken = default)
    {
        var stored = await _db.SystemSettings.AsNoTracking()
            .OrderBy(s => s.Id)
            .Select(s => (CalendarType?)s.CalendarType)
            .FirstOrDefaultAsync(cancellationToken);

        return stored is { } value && Enum.IsDefined(value) ? value : CalendarType.Gregorian;
    }

    public async Task<(int SettingId, CalendarType Previous)> SaveAsync(CalendarType calendarType, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(calendarType))
            throw new ArgumentOutOfRangeException(nameof(calendarType));

        var settings = await _db.SystemSettings.OrderBy(s => s.Id).FirstOrDefaultAsync(cancellationToken);
        var previous = settings?.CalendarType ?? CalendarType.Gregorian;

        if (settings is null)
        {
            settings = new SystemSetting { CalendarType = calendarType };
            _db.SystemSettings.Add(settings);
        }
        else
        {
            settings.CalendarType = calendarType;
            settings.UpdatedAtUtc = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync(cancellationToken);
        AppCalendarContext.Set(calendarType);
        return (settings.Id, previous);
    }

    public async Task<CalendarType> LoadAsync(CancellationToken cancellationToken = default)
    {
        var calendarType = await GetAsync(cancellationToken);
        AppCalendarContext.Set(calendarType);
        return calendarType;
    }
}

/// <summary>
/// هنگام بالا آمدنِ برنامه تقویمِ ذخیره‌شده را بارگذاری می‌کند. اگر دیتابیس هنوز در دسترس نباشد یا
/// جدول ساخته نشده باشد، برنامه با تقویمِ میلادی (رفتار فعلی) بالا می‌آید.
/// </summary>
public sealed class CalendarSettingsInitializer : IHostedService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<CalendarSettingsInitializer> _logger;

    public CalendarSettingsInitializer(IServiceScopeFactory scopeFactory, ILogger<CalendarSettingsInitializer> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var settings = scope.ServiceProvider.GetRequiredService<ICalendarSettingsService>();
            var calendarType = await settings.LoadAsync(cancellationToken);
            _logger.LogInformation("Display calendar: {CalendarType}", calendarType);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppCalendarContext.Set(CalendarType.Gregorian);
            _logger.LogWarning(ex, "Calendar setting could not be loaded; falling back to Gregorian.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
