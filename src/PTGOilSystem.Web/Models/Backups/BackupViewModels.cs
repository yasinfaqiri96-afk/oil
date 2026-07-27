using System.ComponentModel.DataAnnotations;
using PTGOilSystem.Web.Models.Entities;

namespace PTGOilSystem.Web.Models.Backups;

/// <summary>یک ردیف تاریخچهٔ بکاپ، آماده برای نمایش در جدول.</summary>
public sealed class BackupHistoryRowViewModel
{
    public int Id { get; init; }
    public DateTime StartedAtUtc { get; init; }
    public DateTime? CompletedAtUtc { get; init; }
    public BackupTriggerKind TriggerKind { get; init; }
    public BackupRunStatus Status { get; init; }
    public BackupStorageTarget StorageTarget { get; init; }
    public BackupRetentionTier RetentionTier { get; init; }
    public BackupDriveUploadStatus DriveUploadStatus { get; init; }
    public long SizeBytes { get; init; }
    public string? Sha256 { get; init; }
    public string FileName { get; init; } = "";
    public string? CreatedByUsername { get; init; }
    public string? ErrorMessage { get; init; }

    /// <summary>فایل روی سرور هست و می‌شود دانلودش کرد.</summary>
    public bool CanDownload { get; init; }

    public bool CanDelete => Status != BackupRunStatus.Running;
}

/// <summary>
/// پاسخ JSON دکمهٔ «ایجاد بکاپ اکنون». صفحه بر اساس همین، دکمه را آزاد می‌کند،
/// پیام را نشان می‌دهد و در صورت لزوم Poll وضعیت را شروع می‌کند.
/// </summary>
/// <param name="Queued">درخواست در صف نشست.</param>
/// <param name="IsRunning">بکاپی هم‌اکنون در حال اجراست.</param>
/// <param name="IsPending">درخواستی در صف منتظر شروع است.</param>
/// <param name="Message">پیام قابل نمایش به کاربر.</param>
public sealed record BackupTriggerResponse(bool Queued, bool IsRunning, bool IsPending, string Message)
{
    /// <summary>صفحه باید Poll را روشن نگه دارد.</summary>
    public bool ShouldPoll => Queued || IsRunning || IsPending;
}

/// <summary>وضعیت اتصال Google Drive برای کارت بالای صفحه.</summary>
public sealed class BackupDriveStatusViewModel
{
    public bool IsConfigured { get; init; }
    public bool IsConnected { get; init; }
    public string? AccountEmail { get; init; }
    public string? FolderId { get; init; }
    public DateTime? ConnectedAtUtc { get; init; }
    public string? LastError { get; init; }
}

/// <summary>فرم تنظیمات بکاپ خودکار.</summary>
public sealed class BackupSettingsFormViewModel
{
    [Display(Name = "بکاپ خودکار")]
    public bool AutoBackupEnabled { get; set; } = true;

    [Display(Name = "دوره")]
    public BackupSchedule Schedule { get; set; } = BackupSchedule.Daily;

    [Display(Name = "ساعت اجرا")]
    [Range(0, 23, ErrorMessage = "ساعت باید بین ۰ تا ۲۳ باشد.")]
    public int RunAtHourLocal { get; set; } = 2;

    [Display(Name = "دقیقه اجرا")]
    [Range(0, 59, ErrorMessage = "دقیقه باید بین ۰ تا ۵۹ باشد.")]
    public int RunAtMinuteLocal { get; set; }

    [Display(Name = "محل ذخیره")]
    public BackupStorageTarget StorageTarget { get; set; } = BackupStorageTarget.Server;

    [Display(Name = "نگهداری روزانه")]
    [Range(1, 365, ErrorMessage = "تعداد نسخه‌های روزانه باید بین ۱ تا ۳۶۵ باشد.")]
    public int KeepDaily { get; set; } = 7;

    [Display(Name = "نگهداری هفتگی")]
    [Range(1, 260, ErrorMessage = "تعداد نسخه‌های هفتگی باید بین ۱ تا ۲۶۰ باشد.")]
    public int KeepWeekly { get; set; } = 4;

    [Display(Name = "نگهداری ماهانه")]
    [Range(1, 120, ErrorMessage = "تعداد نسخه‌های ماهانه باید بین ۱ تا ۱۲۰ باشد.")]
    public int KeepMonthly { get; set; } = 6;
}

/// <summary>مدل صفحهٔ «پشتیبان‌گیری». عمداً فقط شش بخش دارد و بیشتر نمی‌شود.</summary>
public sealed class BackupIndexViewModel
{
    /// <summary>الان بکاپی در حال اجراست.</summary>
    public bool IsRunning { get; init; }

    /// <summary>درخواستی ثبت شده ولی هنوز شروع نشده.</summary>
    public bool IsQueued { get; init; }

    /// <summary>تا وقتی این true است، صفحه وضعیت را Poll می‌کند.</summary>
    public bool IsBusy => IsRunning || IsQueued;

    public DateTime? RunningSinceUtc { get; init; }

    public BackupHistoryRowViewModel? LastBackup { get; init; }

    /// <summary>تازه‌ترین ردیف تاریخچه، فارغ از نتیجه. برای اعلام نتیجه در پایان Poll.</summary>
    public BackupHistoryRowViewModel? LatestRun { get; init; }

    public DateTime? NextRunAtUtc { get; init; }

    public BackupDriveStatusViewModel Drive { get; init; } = new();
    public BackupSettingsFormViewModel Settings { get; init; } = new();
    public IReadOnlyList<BackupHistoryRowViewModel> History { get; init; } = [];

    /// <summary>منطقهٔ زمانی‌ای که ساعت زمان‌بندی نسبت به آن تفسیر می‌شود.</summary>
    public string TimeZoneId { get; init; } = "UTC";

    /// <summary>کاربر فعلی اجازهٔ ورود به صفحهٔ بازیابی را دارد.</summary>
    public bool CanRestore { get; init; }

    /// <summary>بازیابی در پیکربندی این نصب فعال است.</summary>
    public bool RestoreEnabled { get; init; }
}

/// <summary>فرم ثبت اطلاعات اتصال Google Drive.</summary>
public sealed class BackupDriveConnectViewModel
{
    [Required(ErrorMessage = "Client ID الزامی است.")]
    [Display(Name = "Client ID")]
    [MaxLength(300)]
    public string ClientId { get; set; } = "";

    [Required(ErrorMessage = "Client Secret الزامی است.")]
    [Display(Name = "Client Secret")]
    [MaxLength(300)]
    public string ClientSecret { get; set; } = "";

    [Display(Name = "شناسه پوشه در Drive")]
    [MaxLength(200)]
    public string? FolderId { get; set; }
}

/// <summary>مدل صفحهٔ جداگانهٔ بازیابی.</summary>
public sealed class BackupRestoreIndexViewModel
{
    public bool RestoreEnabled { get; init; }
    public bool IsSuperAdmin { get; init; }
    public IReadOnlyList<BackupHistoryRowViewModel> RestorableBackups { get; init; } = [];

    /// <summary>عبارتی که کاربر باید عیناً تایپ کند تا بازیابی اجرا شود.</summary>
    public string RequiredConfirmationPhrase { get; init; } = BackupRestoreFormViewModel.ConfirmationPhrase;
}

/// <summary>فرم اجرای بازیابی: انتخاب نسخه + تأیید امنیتی.</summary>
public sealed class BackupRestoreFormViewModel
{
    /// <summary>عبارت تأیید. عمداً فارسی و صریح است تا تصادفی تایپ نشود.</summary>
    public const string ConfirmationPhrase = "بازیابی کامل";

    [Display(Name = "نسخهٔ بکاپ")]
    public int JobId { get; set; }

    [Required(ErrorMessage = "رمز عبور خود را وارد کنید.")]
    [DataType(DataType.Password)]
    [Display(Name = "رمز عبور")]
    public string Password { get; set; } = "";

    [Required(ErrorMessage = "عبارت تأیید را وارد کنید.")]
    [Display(Name = "عبارت تأیید")]
    public string Confirmation { get; set; } = "";

    [Display(Name = "بازگرداندن فایل‌های ضمیمه")]
    public bool RestoreUploads { get; set; } = true;
}
