using System.ComponentModel.DataAnnotations;

namespace PTGOilSystem.Web.Models.Entities;

/// <summary>دوره‌های زمان‌بندی مجاز برای بکاپ خودکار.</summary>
public enum BackupSchedule
{
    Every6Hours = 1,
    Daily = 2,
    Weekly = 3,
    Monthly = 4
}

/// <summary>محل نگهداری فایل بکاپ.</summary>
public enum BackupStorageTarget
{
    Server = 1,
    GoogleDrive = 2,
    Both = 3
}

/// <summary>منشأ اجرای بکاپ.</summary>
public enum BackupTriggerKind
{
    Manual = 1,
    Scheduled = 2
}

/// <summary>وضعیت کلی یک اجرای بکاپ.</summary>
public enum BackupRunStatus
{
    Running = 1,
    Succeeded = 2,
    Failed = 3
}

/// <summary>وضعیت آپلود در Google Drive. جدا از وضعیت کلی است تا «موفق محلی + آپلود ناتمام» قابل بیان باشد.</summary>
public enum BackupDriveUploadStatus
{
    NotRequested = 0,
    Pending = 1,
    Uploaded = 2,
    Failed = 3
}

/// <summary>ردهٔ نگهداری. سیاست نگهداری روزانه/هفتگی/ماهانه روی همین رده اعمال می‌شود.</summary>
public enum BackupRetentionTier
{
    Daily = 1,
    Weekly = 2,
    Monthly = 3
}

/// <summary>
/// تنظیمات ماژول پشتیبان‌گیری. عمداً تک‌ردیفی است (Id = <see cref="SingletonId"/>)
/// تا هیچ‌گاه دو مجموعه تنظیم فعال هم‌زمان وجود نداشته باشد.
/// </summary>
public class BackupSetting : BaseEntity
{
    public const int SingletonId = 1;

    /// <summary>بکاپ خودکار فعال است یا نه. پیش‌فرض سیستم: فعال، روزانه ساعت ۲ بامداد.</summary>
    public bool AutoBackupEnabled { get; set; } = true;

    public BackupSchedule Schedule { get; set; } = BackupSchedule.Daily;

    /// <summary>ساعت اجرای بکاپ به وقت محلیِ پیکربندی‌شده (۰ تا ۲۳). پیش‌فرض ۲ بامداد.</summary>
    public int RunAtHourLocal { get; set; } = 2;

    /// <summary>دقیقهٔ اجرای بکاپ (۰ تا ۵۹).</summary>
    public int RunAtMinuteLocal { get; set; }

    public BackupStorageTarget StorageTarget { get; set; } = BackupStorageTarget.Server;

    public int KeepDaily { get; set; } = 7;
    public int KeepWeekly { get; set; } = 4;
    public int KeepMonthly { get; set; } = 6;

    public DateTime? LastRunAtUtc { get; set; }
    public DateTime? NextRunAtUtc { get; set; }
}

/// <summary>
/// اتصال Google Drive. تک‌ردیفی است؛ Refresh Token و Client Secret هرگز خام ذخیره نمی‌شوند
/// و با DataProtection رمز می‌شوند.
/// </summary>
public class BackupDriveCredential : BaseEntity
{
    public const int SingletonId = 1;

    [MaxLength(300)] public string? ClientId { get; set; }

    /// <summary>Client Secret رمزشده با DataProtection.</summary>
    [MaxLength(2000)] public string? ProtectedClientSecret { get; set; }

    /// <summary>Refresh Token رمزشده با DataProtection.</summary>
    [MaxLength(2000)] public string? ProtectedRefreshToken { get; set; }

    [MaxLength(320)] public string? AccountEmail { get; set; }

    /// <summary>شناسهٔ پوشهٔ مقصد در Drive. خالی یعنی ریشهٔ درایو.</summary>
    [MaxLength(200)] public string? FolderId { get; set; }

    public DateTime? ConnectedAtUtc { get; set; }
    public DateTime? LastTokenRefreshAtUtc { get; set; }
    [MaxLength(1000)] public string? LastError { get; set; }

    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    public bool IsConnected => !string.IsNullOrWhiteSpace(ProtectedRefreshToken);
}

/// <summary>
/// یک ردیف تاریخچهٔ بکاپ. هر اجرا — چه دستی چه زمان‌بندی‌شده — دقیقاً یک ردیف دارد.
/// </summary>
public class BackupJob : BaseEntity
{
    public DateTime StartedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAtUtc { get; set; }

    public BackupTriggerKind TriggerKind { get; set; } = BackupTriggerKind.Manual;
    public BackupRunStatus Status { get; set; } = BackupRunStatus.Running;
    public BackupStorageTarget StorageTarget { get; set; } = BackupStorageTarget.Server;
    public BackupRetentionTier RetentionTier { get; set; } = BackupRetentionTier.Daily;

    [Required, MaxLength(260)] public string FileName { get; set; } = "";

    /// <summary>مسیر فایل روی سرور. پس از حذف محلی خالی می‌شود ولی ردیف تاریخچه می‌ماند.</summary>
    [MaxLength(1000)] public string? LocalPath { get; set; }

    public long SizeBytes { get; set; }

    /// <summary>SHA-256 فایل ZIP نهایی (hex، ۶۴ نویسه).</summary>
    [MaxLength(64)] public string? Sha256 { get; set; }

    public BackupDriveUploadStatus DriveUploadStatus { get; set; } = BackupDriveUploadStatus.NotRequested;
    [MaxLength(200)] public string? DriveFileId { get; set; }

    /// <summary>Session URI آپلود resumable برای ادامهٔ آپلود پس از قطع اینترنت.</summary>
    [MaxLength(2000)] public string? DriveResumeUri { get; set; }
    public int DriveUploadAttempts { get; set; }
    public DateTime? DriveUploadedAtUtc { get; set; }

    [MaxLength(2000)] public string? ErrorMessage { get; set; }

    [MaxLength(150)] public string? CreatedByUsername { get; set; }

    /// <summary>خلاصهٔ محتوای بکاپ (تعداد فایل uploads، نسخهٔ اپ و ...) به‌صورت JSON.</summary>
    [MaxLength(4000)] public string? ManifestJson { get; set; }

    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    public bool LocalFileExists => !string.IsNullOrWhiteSpace(LocalPath);
}
