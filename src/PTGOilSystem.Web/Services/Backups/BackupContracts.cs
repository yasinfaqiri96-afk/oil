using PTGOilSystem.Web.Models.Entities;

namespace PTGOilSystem.Web.Services.Backups;

/// <summary>درخواست اجرای یک بکاپ.</summary>
public sealed record BackupTriggerRequest(BackupTriggerKind TriggerKind, string RequestedBy, int? RequestedByUserId);

/// <summary>نتیجهٔ اجرای بکاپ.</summary>
public sealed record BackupRunResult(bool Succeeded, int? JobId, string Message)
{
    /// <summary>true یعنی اجرا اصلاً شروع نشد چون بکاپ دیگری در جریان بود.</summary>
    public bool WasBlockedByRunningBackup { get; init; }

    public static BackupRunResult Blocked(string message)
        => new(false, null, message) { WasBlockedByRunningBackup = true };
}

/// <summary>ورودی ذخیرهٔ تنظیمات بکاپ خودکار.</summary>
public sealed record BackupSettingsInput(
    bool AutoBackupEnabled,
    BackupSchedule Schedule,
    int RunAtHourLocal,
    int RunAtMinuteLocal,
    BackupStorageTarget StorageTarget,
    int KeepDaily,
    int KeepWeekly,
    int KeepMonthly);

/// <summary>فایل آمادهٔ دانلود برای یک بکاپ.</summary>
public sealed record BackupDownload(string FileName, string FullPath, long SizeBytes);

public interface IBackupService
{
    Task<BackupSetting> GetSettingsAsync(CancellationToken ct = default);

    Task<BackupSetting> UpdateSettingsAsync(BackupSettingsInput input, CancellationToken ct = default);

    /// <summary>اگر بکاپ خودکار فعال است ولی زمان اجرای بعدی ثبت نشده، آن را محاسبه و ذخیره می‌کند.</summary>
    Task<BackupSetting> EnsureNextRunScheduledAsync(CancellationToken ct = default);

    /// <summary>اجرای کاملِ یک بکاپ. فقط از صفِ پشت‌صحنه فراخوانی می‌شود تا هم‌زمانی رخ ندهد.</summary>
    Task<BackupRunResult> RunAsync(BackupTriggerRequest request, CancellationToken ct = default);

    Task<IReadOnlyList<BackupJob>> GetHistoryAsync(int take, CancellationToken ct = default);

    Task<BackupJob?> GetJobAsync(int id, CancellationToken ct = default);

    Task<BackupDownload?> GetDownloadAsync(int id, CancellationToken ct = default);

    /// <summary>حذف یک بکاپ (فایل محلی + ردیف تاریخچه). حذف نسخهٔ Drive انجام نمی‌شود.</summary>
    Task<bool> DeleteJobAsync(int id, CancellationToken ct = default);

    /// <summary>تلاش دوبارهٔ آپلود نسخه‌هایی که آپلودشان ناتمام مانده است.</summary>
    Task<int> RetryPendingDriveUploadsAsync(CancellationToken ct = default);
}
