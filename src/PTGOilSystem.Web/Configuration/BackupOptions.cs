namespace PTGOilSystem.Web.Configuration;

/// <summary>
/// پیکربندی زیرساختِ پشتیبان‌گیری. از بخش "Backup" خوانده می‌شود.
/// این گزینه‌ها «کجا و با چه ابزاری» را تعیین می‌کنند؛ «چه زمانی و چند نسخه»
/// در پایگاه داده (BackupSetting) و از صفحهٔ پشتیبان‌گیری تنظیم می‌شود.
/// </summary>
public sealed class BackupOptions
{
    public const string SectionName = "Backup";

    /// <summary>پوشهٔ نگهداری فایل‌های بکاپ روی سرور. نسبی نسبت به ContentRoot هم پذیرفته می‌شود.</summary>
    public string StorageDirectory { get; set; } = "backups/app";

    /// <summary>پوشهٔ کارِ موقت هنگام ساخت آرشیو. خالی یعنی زیرپوشهٔ tmp داخل StorageDirectory.</summary>
    public string? WorkingDirectory { get; set; }

    /// <summary>
    /// پوشهٔ ابزارهای PostgreSQL (pg_dump / pg_restore). خالی یعنی از PATH خوانده شود.
    /// روی ویندوز معمولاً چیزی شبیه C:\Program Files\PostgreSQL\16\bin است.
    /// </summary>
    public string? PostgresToolsDirectory { get; set; }

    /// <summary>سقف زمان اجرای pg_dump به دقیقه.</summary>
    public int DumpTimeoutMinutes { get; set; } = 60;

    /// <summary>منطقهٔ زمانی‌ای که ساعت زمان‌بندی («۲ بامداد») نسبت به آن تفسیر می‌شود.</summary>
    public string TimeZoneId { get; set; } = "Asia/Kabul";

    /// <summary>فاصلهٔ بیدارشدن زمان‌بند برای بررسی سررسید بکاپ (دقیقه).</summary>
    public int SchedulerPollMinutes { get; set; } = 5;

    /// <summary>سرویس زمان‌بند در این پروسه اجرا شود یا نه. روی چند نمونه فقط یکی باید true باشد.</summary>
    public bool SchedulerEnabled { get; set; } = true;

    /// <summary>اندازهٔ هر قطعهٔ آپلود resumable در Google Drive (مضربی از 256KB).</summary>
    public int DriveUploadChunkSizeBytes { get; set; } = 8 * 1024 * 1024;

    /// <summary>تعداد تلاش مجدد برای هر قطعه هنگام قطع اینترنت.</summary>
    public int DriveUploadMaxAttempts { get; set; } = 5;

    /// <summary>
    /// آدرس بازگشت OAuth گوگل. باید دقیقاً با Authorized redirect URI در Google Cloud Console یکی باشد.
    /// خالی یعنی از آدرس خودِ درخواست ساخته شود.
    /// </summary>
    public string? DriveRedirectUri { get; set; }

    /// <summary>اجازهٔ اجرای بازیابی از داخل برنامه. پیش‌فرض false تا بازیابی تصادفی ممکن نباشد.</summary>
    public bool RestoreEnabled { get; set; }
}
