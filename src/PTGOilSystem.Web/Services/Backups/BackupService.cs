using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PTGOilSystem.Web.Configuration;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;

namespace PTGOilSystem.Web.Services.Backups;

/// <summary>
/// اجرای بکاپ کامل: dump دیتابیس + فایل‌های uploads + تنظیمات + شناسنامه، همه در یک ZIP.
/// یک بکاپ فقط وقتی «موفق» اعلام می‌شود که فایل ساخته شده، حجم داشته باشد،
/// SHA-256 محاسبه شده باشد و dump آن با pg_restore قابل خواندن باشد.
/// </summary>
public sealed class BackupService : IBackupService
{
    private const int MaxSettingsFileBytes = 512 * 1024;

    private static readonly string[] SecretConfigurationKeyMarkers =
    [
        "password", "secret", "token", "apikey", "api_key", "connectionstring", "connection_string"
    ];

    private readonly ApplicationDbContext _db;
    private readonly IPostgresBackupTool _postgresTool;
    private readonly IGoogleDriveBackupClient _driveClient;
    private readonly IBackupExecutionLock _executionLock;
    private readonly IAuditService _audit;
    private readonly IWebHostEnvironment _environment;
    private readonly IConfiguration _configuration;
    private readonly BackupOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<BackupService> _logger;

    public BackupService(
        ApplicationDbContext db,
        IPostgresBackupTool postgresTool,
        IGoogleDriveBackupClient driveClient,
        IBackupExecutionLock executionLock,
        IAuditService audit,
        IWebHostEnvironment environment,
        IConfiguration configuration,
        IOptions<BackupOptions> options,
        TimeProvider timeProvider,
        ILogger<BackupService> logger)
    {
        _db = db;
        _postgresTool = postgresTool;
        _driveClient = driveClient;
        _executionLock = executionLock;
        _audit = audit;
        _environment = environment;
        _configuration = configuration;
        _options = options.Value;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    // ---- Settings -----------------------------------------------------------

    public async Task<BackupSetting> GetSettingsAsync(CancellationToken ct = default)
    {
        var settings = await _db.BackupSettings.OrderBy(s => s.Id).FirstOrDefaultAsync(ct);
        if (settings is not null)
        {
            return settings;
        }

        // پیش‌فرض سیستم طبق سیاست پشتیبان‌گیری: روزانه ساعت ۲ بامداد، ۷/۴/۶ نسخه.
        settings = new BackupSetting();
        settings.NextRunAtUtc = BackupScheduleCalculator.GetNextRunUtc(UtcNow, settings, TimeZone);
        _db.BackupSettings.Add(settings);
        await _db.SaveChangesAsync(ct);
        return settings;
    }

    public async Task<BackupSetting> EnsureNextRunScheduledAsync(CancellationToken ct = default)
    {
        var settings = await GetSettingsAsync(ct);

        var expected = settings.AutoBackupEnabled
            ? BackupScheduleCalculator.GetNextRunUtc(UtcNow, settings, TimeZone)
            : (DateTime?)null;

        if (settings.NextRunAtUtc == expected || (settings.AutoBackupEnabled && settings.NextRunAtUtc is not null))
        {
            return settings;
        }

        settings.NextRunAtUtc = expected;
        await _db.SaveChangesAsync(ct);
        return settings;
    }

    public async Task<BackupSetting> UpdateSettingsAsync(BackupSettingsInput input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var settings = await GetSettingsAsync(ct);
        settings.AutoBackupEnabled = input.AutoBackupEnabled;
        settings.Schedule = input.Schedule;
        settings.RunAtHourLocal = Math.Clamp(input.RunAtHourLocal, 0, 23);
        settings.RunAtMinuteLocal = Math.Clamp(input.RunAtMinuteLocal, 0, 59);
        settings.StorageTarget = input.StorageTarget;
        settings.KeepDaily = Math.Clamp(input.KeepDaily, 1, 365);
        settings.KeepWeekly = Math.Clamp(input.KeepWeekly, 1, 260);
        settings.KeepMonthly = Math.Clamp(input.KeepMonthly, 1, 120);
        settings.NextRunAtUtc = settings.AutoBackupEnabled
            ? BackupScheduleCalculator.GetNextRunUtc(UtcNow, settings, TimeZone)
            : null;

        await _db.SaveChangesAsync(ct);

        await LogAuditAsync(
            action: "SettingsUpdated",
            entityId: settings.Id,
            description: $"تنظیمات بکاپ خودکار ذخیره شد (فعال: {settings.AutoBackupEnabled}، دوره: {settings.Schedule}، مقصد: {settings.StorageTarget}).",
            isSuccess: true,
            ct: ct);

        return settings;
    }

    // ---- History ------------------------------------------------------------

    public async Task<IReadOnlyList<BackupJob>> GetHistoryAsync(int take, CancellationToken ct = default)
        => await _db.BackupJobs
            .AsNoTracking()
            .OrderByDescending(job => job.StartedAtUtc)
            .ThenByDescending(job => job.Id)
            .Take(Math.Clamp(take, 1, 200))
            .ToListAsync(ct);

    public Task<BackupJob?> GetJobAsync(int id, CancellationToken ct = default)
        => _db.BackupJobs.FirstOrDefaultAsync(job => job.Id == id, ct);

    public async Task<BackupDownload?> GetDownloadAsync(int id, CancellationToken ct = default)
    {
        var job = await _db.BackupJobs.AsNoTracking().FirstOrDefaultAsync(item => item.Id == id, ct);
        if (job is null || string.IsNullOrWhiteSpace(job.LocalPath) || !File.Exists(job.LocalPath))
        {
            return null;
        }

        return new BackupDownload(job.FileName, job.LocalPath, new FileInfo(job.LocalPath).Length);
    }

    public async Task<bool> DeleteJobAsync(int id, CancellationToken ct = default)
    {
        var job = await _db.BackupJobs.FirstOrDefaultAsync(item => item.Id == id, ct);
        if (job is null)
        {
            return false;
        }

        if (job.Status == BackupRunStatus.Running)
        {
            return false;
        }

        TryDeleteFile(job.LocalPath);
        _db.BackupJobs.Remove(job);
        await _db.SaveChangesAsync(ct);

        await LogAuditAsync(
            action: "Delete",
            entityId: id,
            description: $"بکاپ «{job.FileName}» حذف شد.",
            isSuccess: true,
            ct: ct);

        return true;
    }

    // ---- Run ----------------------------------------------------------------

    public async Task<BackupRunResult> RunAsync(BackupTriggerRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var handle = _executionLock.TryAcquire(request.RequestedBy);
        if (handle is null)
        {
            return BackupRunResult.Blocked("یک بکاپ در حال اجراست. تا پایان آن، بکاپ جدید شروع نمی‌شود.");
        }

        var settings = await GetSettingsAsync(ct);
        var startedAtUtc = UtcNow;
        var fileName = BuildFileName(startedAtUtc, request.TriggerKind);

        var job = new BackupJob
        {
            StartedAtUtc = startedAtUtc,
            TriggerKind = request.TriggerKind,
            Status = BackupRunStatus.Running,
            StorageTarget = settings.StorageTarget,
            FileName = fileName,
            CreatedByUsername = request.RequestedBy,
            DriveUploadStatus = settings.StorageTarget == BackupStorageTarget.Server
                ? BackupDriveUploadStatus.NotRequested
                : BackupDriveUploadStatus.Pending,
            RetentionTier = await ClassifyTierAsync(startedAtUtc, ct)
        };

        _db.BackupJobs.Add(job);
        await _db.SaveChangesAsync(ct);

        await LogAuditAsync("Started", job.Id, $"بکاپ «{fileName}» شروع شد ({request.TriggerKind}).", true, ct);

        var workingDirectory = Path.Combine(ResolveWorkingDirectory(), $"job-{job.Id:D8}");

        try
        {
            var archivePath = await BuildArchiveAsync(job, request, workingDirectory, ct);

            job.LocalPath = archivePath;
            job.SizeBytes = new FileInfo(archivePath).Length;
            job.Sha256 = await ComputeSha256Async(archivePath, ct);
            job.Status = BackupRunStatus.Succeeded;
            job.CompletedAtUtc = UtcNow;

            settings.LastRunAtUtc = job.CompletedAtUtc;
            settings.NextRunAtUtc = settings.AutoBackupEnabled
                ? BackupScheduleCalculator.GetNextRunUtc(UtcNow, settings, TimeZone)
                : null;

            await _db.SaveChangesAsync(ct);

            await LogAuditAsync(
                "Completed",
                job.Id,
                $"بکاپ «{fileName}» با حجم {job.SizeBytes} بایت ساخته و صحت‌سنجی شد.",
                true,
                ct);

            if (settings.StorageTarget is BackupStorageTarget.GoogleDrive or BackupStorageTarget.Both)
            {
                await UploadToDriveAsync(job, deleteLocalAfterUpload: settings.StorageTarget == BackupStorageTarget.GoogleDrive, ct);
            }

            await ApplyRetentionAsync(settings, ct);

            return new BackupRunResult(true, job.Id, $"بکاپ «{fileName}» با موفقیت ساخته شد.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Backup {FileName} failed.", fileName);

            job.Status = BackupRunStatus.Failed;
            job.CompletedAtUtc = UtcNow;
            job.ErrorMessage = Truncate(ex.Message, 2000);
            // فایل نیمه‌کارهٔ ناموفق نگه داشته نمی‌شود تا با نسخه‌های سالم اشتباه نشود.
            TryDeleteFile(job.LocalPath);
            job.LocalPath = null;
            await _db.SaveChangesAsync(ct);

            await LogAuditAsync("Failed", job.Id, $"بکاپ «{fileName}» ناموفق بود: {job.ErrorMessage}", false, ct);

            return new BackupRunResult(false, job.Id, $"بکاپ ناموفق بود: {ex.Message}");
        }
        finally
        {
            TryDeleteDirectory(workingDirectory);
        }
    }

    // ---- Archive building ---------------------------------------------------

    private async Task<string> BuildArchiveAsync(
        BackupJob job,
        BackupTriggerRequest request,
        string workingDirectory,
        CancellationToken ct)
    {
        Directory.CreateDirectory(workingDirectory);

        var connectionString = _db.Database.GetConnectionString()
            ?? throw new InvalidOperationException("رشتهٔ اتصال پایگاه داده در دسترس نیست.");

        // ۱) dump با فرمت Custom
        var dumpPath = Path.Combine(workingDirectory, BackupManifest.DatabaseDumpEntryName);
        var dumpResult = await _postgresTool.DumpAsync(connectionString, dumpPath, ct);
        if (!dumpResult.Succeeded || !File.Exists(dumpPath))
        {
            throw new InvalidOperationException($"pg_dump ناموفق بود: {Truncate(dumpResult.FailureText, 500)}");
        }

        var dumpBytes = new FileInfo(dumpPath).Length;
        if (dumpBytes <= 0)
        {
            throw new InvalidOperationException("فایل dump خالی است.");
        }

        // ۲) صحت‌سنجی: فهرست محتویات dump باید خوانده شود، وگرنه فایل قابل بازیابی نیست.
        var verifyResult = await _postgresTool.VerifyDumpAsync(dumpPath, ct);
        if (!verifyResult.Succeeded || string.IsNullOrWhiteSpace(verifyResult.StandardOutput))
        {
            throw new InvalidOperationException($"فایل dump قابل خواندن نیست: {Truncate(verifyResult.FailureText, 500)}");
        }

        // ۳) تنظیمات ضروری
        var settingsPath = Path.Combine(workingDirectory, BackupManifest.SettingsEntryName);
        await File.WriteAllTextAsync(settingsPath, await BuildSettingsJsonAsync(ct), Encoding.UTF8, ct);

        // ۴) شناسنامه
        var (uploadsRoot, uploadsFiles, uploadsBytes) = InspectUploads();
        var manifest = new BackupManifest
        {
            CreatedAtUtc = job.StartedAtUtc,
            CreatedBy = request.RequestedBy,
            TriggerKind = request.TriggerKind.ToString(),
            AppVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString(),
            DatabaseName = _db.Database.GetDbConnection().Database,
            DatabaseDumpBytes = dumpBytes,
            DatabaseDumpSha256 = await ComputeSha256Async(dumpPath, ct),
            UploadsFileCount = uploadsFiles.Count,
            UploadsBytes = uploadsBytes
        };

        var manifestJson = manifest.ToJson();
        var manifestPath = Path.Combine(workingDirectory, BackupManifest.FileName);
        await File.WriteAllTextAsync(manifestPath, manifestJson, Encoding.UTF8, ct);
        job.ManifestJson = Truncate(manifestJson, 4000);

        // ۵) بسته‌بندی — ابتدا در پوشهٔ کار ساخته و صحت‌سنجی می‌شود و تنها پس از آن به
        // پوشهٔ نهایی منتقل می‌شود؛ پس هیچ فایل نیمه‌کاره‌ای در کنار نسخه‌های سالم نمی‌نشیند.
        var stagedArchivePath = Path.Combine(workingDirectory, job.FileName);

        await using (var archiveStream = new FileStream(stagedArchivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var archive = new ZipArchive(archiveStream, ZipArchiveMode.Create))
        {
            archive.CreateEntryFromFile(dumpPath, BackupManifest.DatabaseDumpEntryName, CompressionLevel.SmallestSize);
            archive.CreateEntryFromFile(settingsPath, BackupManifest.SettingsEntryName, CompressionLevel.SmallestSize);
            archive.CreateEntryFromFile(manifestPath, BackupManifest.FileName, CompressionLevel.NoCompression);

            foreach (var file in uploadsFiles)
            {
                var relativePath = Path.GetRelativePath(uploadsRoot, file).Replace('\\', '/');
                archive.CreateEntryFromFile(file, BackupManifest.UploadsEntryPrefix + relativePath, CompressionLevel.Optimal);
            }
        }

        VerifyArchive(stagedArchivePath);

        var archivePath = Path.Combine(EnsureStorageDirectory(), job.FileName);
        TryDeleteFile(archivePath);
        File.Move(stagedArchivePath, archivePath);
        return archivePath;
    }

    /// <summary>
    /// آرشیو فقط وقتی پذیرفته می‌شود که باز شود و هر سه جزء اجباری داخلش باشند.
    /// این همان چیزی است که «ZIP سالم» را از «فایل ۲ کیلوبایتیِ بی‌مصرف» جدا می‌کند.
    /// </summary>
    private static void VerifyArchive(string archivePath)
    {
        var length = new FileInfo(archivePath).Length;
        if (length <= 0)
        {
            throw new InvalidOperationException("آرشیو بکاپ خالی است.");
        }

        using var archive = ZipFile.OpenRead(archivePath);

        foreach (var requiredEntry in new[] { BackupManifest.DatabaseDumpEntryName, BackupManifest.SettingsEntryName, BackupManifest.FileName })
        {
            var entry = archive.GetEntry(requiredEntry)
                ?? throw new InvalidOperationException($"آرشیو بکاپ بخش «{requiredEntry}» را ندارد.");

            if (entry.Length <= 0 && requiredEntry != BackupManifest.SettingsEntryName)
            {
                throw new InvalidOperationException($"بخش «{requiredEntry}» داخل آرشیو خالی است.");
            }
        }
    }

    private (string Root, IReadOnlyList<string> Files, long TotalBytes) InspectUploads()
    {
        var webRoot = _environment.WebRootPath;
        if (string.IsNullOrWhiteSpace(webRoot))
        {
            return (string.Empty, [], 0);
        }

        var uploadsRoot = Path.Combine(webRoot, "uploads");
        if (!Directory.Exists(uploadsRoot))
        {
            return (uploadsRoot, [], 0);
        }

        var files = Directory.EnumerateFiles(uploadsRoot, "*", SearchOption.AllDirectories).ToList();
        var totalBytes = files.Sum(file => new FileInfo(file).Length);
        return (uploadsRoot, files, totalBytes);
    }

    /// <summary>
    /// تنظیمات ضروری برای برپاکردن دوبارهٔ سیستم. مقادیر حساس (رمز، توکن، رشتهٔ اتصال)
    /// عمداً پنهان می‌شوند: فایل بکاپ به Drive می‌رود و نباید کلید تولید را با خود ببرد.
    /// </summary>
    private async Task<string> BuildSettingsJsonAsync(CancellationToken ct)
    {
        var backupSettings = await GetSettingsAsync(ct);

        var payload = new Dictionary<string, object?>
        {
            ["exportedAtUtc"] = UtcNow,
            ["environment"] = _environment.EnvironmentName,
            ["appVersion"] = Assembly.GetExecutingAssembly().GetName().Version?.ToString(),
            ["backupModule"] = new
            {
                autoBackupEnabled = backupSettings.AutoBackupEnabled,
                schedule = backupSettings.Schedule.ToString(),
                runAtHourLocal = backupSettings.RunAtHourLocal,
                runAtMinuteLocal = backupSettings.RunAtMinuteLocal,
                storageTarget = backupSettings.StorageTarget.ToString(),
                keepDaily = backupSettings.KeepDaily,
                keepWeekly = backupSettings.KeepWeekly,
                keepMonthly = backupSettings.KeepMonthly,
                timeZoneId = _options.TimeZoneId
            },
            ["appSettings"] = ReadRedactedAppSettings()
        };

        return JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
    }

    private Dictionary<string, string?> ReadRedactedAppSettings()
    {
        var redacted = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in _configuration.AsEnumerable())
        {
            if (entry.Value is null)
            {
                continue;
            }

            redacted[entry.Key] = IsSecretKey(entry.Key) ? "***redacted***" : entry.Value;

            if (redacted.Count * 200 > MaxSettingsFileBytes)
            {
                break;
            }
        }

        return redacted;
    }

    private static bool IsSecretKey(string key)
        => SecretConfigurationKeyMarkers.Any(marker => key.Contains(marker, StringComparison.OrdinalIgnoreCase));

    // ---- Google Drive -------------------------------------------------------

    private async Task UploadToDriveAsync(BackupJob job, bool deleteLocalAfterUpload, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(job.LocalPath))
        {
            return;
        }

        job.DriveUploadAttempts++;
        var result = await _driveClient.UploadBackupAsync(job.LocalPath, job.FileName, job.DriveResumeUri, ct);

        if (result.Succeeded)
        {
            job.DriveUploadStatus = BackupDriveUploadStatus.Uploaded;
            job.DriveFileId = result.FileId;
            job.DriveResumeUri = null;
            job.DriveUploadedAtUtc = UtcNow;

            if (deleteLocalAfterUpload)
            {
                TryDeleteFile(job.LocalPath);
                job.LocalPath = null;
            }

            await _db.SaveChangesAsync(ct);
            await LogAuditAsync("DriveUploaded", job.Id, $"بکاپ «{job.FileName}» در Google Drive ذخیره شد.", true, ct);
            return;
        }

        // قطع اینترنت نباید نسخهٔ محلی را از بین ببرد؛ آدرس ادامهٔ آپلود برای تلاش بعدی می‌ماند.
        job.DriveUploadStatus = BackupDriveUploadStatus.Failed;
        job.DriveResumeUri = result.ResumeUri is null ? null : Truncate(result.ResumeUri, 2000);
        job.ErrorMessage = result.Error is null ? null : Truncate(result.Error, 2000);
        await _db.SaveChangesAsync(ct);

        await LogAuditAsync(
            "DriveUploadFailed",
            job.Id,
            $"آپلود «{job.FileName}» در Google Drive ناموفق بود: {job.ErrorMessage}. نسخهٔ محلی حفظ شد.",
            false,
            ct);
    }

    public async Task<int> RetryPendingDriveUploadsAsync(CancellationToken ct = default)
    {
        var settings = await GetSettingsAsync(ct);
        if (settings.StorageTarget == BackupStorageTarget.Server)
        {
            return 0;
        }

        var credential = await _driveClient.GetCredentialAsync(ct);
        if (credential is null || !credential.IsConnected)
        {
            return 0;
        }

        var pending = await _db.BackupJobs
            .Where(job => job.Status == BackupRunStatus.Succeeded
                && job.LocalPath != null
                && (job.DriveUploadStatus == BackupDriveUploadStatus.Pending
                    || job.DriveUploadStatus == BackupDriveUploadStatus.Failed))
            .OrderBy(job => job.StartedAtUtc)
            .Take(5)
            .ToListAsync(ct);

        var uploaded = 0;
        foreach (var job in pending)
        {
            await UploadToDriveAsync(job, deleteLocalAfterUpload: settings.StorageTarget == BackupStorageTarget.GoogleDrive, ct);
            if (job.DriveUploadStatus == BackupDriveUploadStatus.Uploaded)
            {
                uploaded++;
            }
        }

        return uploaded;
    }

    // ---- Retention ----------------------------------------------------------

    private async Task<BackupRetentionTier> ClassifyTierAsync(DateTime candidateUtc, CancellationToken ct)
    {
        var existing = await _db.BackupJobs
            .Where(job => job.Status == BackupRunStatus.Succeeded)
            .OrderByDescending(job => job.StartedAtUtc)
            .Take(200)
            .Select(job => job.StartedAtUtc)
            .ToListAsync(ct);

        return BackupRetentionPlanner.ClassifyTier(candidateUtc, existing, TimeZone);
    }

    private async Task ApplyRetentionAsync(BackupSetting settings, CancellationToken ct)
    {
        var jobs = await _db.BackupJobs
            .Where(job => job.Status == BackupRunStatus.Succeeded)
            .Select(job => new { job.Id, job.StartedAtUtc, job.RetentionTier })
            .ToListAsync(ct);

        var expired = BackupRetentionPlanner.SelectExpired(
            jobs.Select(job => new BackupRetentionCandidate(job.Id, job.StartedAtUtc, job.RetentionTier)),
            BackupRetentionPolicy.FromSettings(settings));

        if (expired.Count == 0)
        {
            return;
        }

        var expiredIds = expired.Select(candidate => candidate.Id).ToHashSet();
        var entities = await _db.BackupJobs.Where(job => expiredIds.Contains(job.Id)).ToListAsync(ct);

        foreach (var entity in entities)
        {
            TryDeleteFile(entity.LocalPath);
        }

        _db.BackupJobs.RemoveRange(entities);
        await _db.SaveChangesAsync(ct);

        await LogAuditAsync(
            "RetentionApplied",
            0,
            $"{entities.Count} نسخهٔ قدیمی طبق سیاست نگهداری ({settings.KeepDaily}/{settings.KeepWeekly}/{settings.KeepMonthly}) حذف شد.",
            true,
            ct);
    }

    // ---- Infrastructure helpers --------------------------------------------

    private DateTime UtcNow => _timeProvider.GetUtcNow().UtcDateTime;

    private TimeZoneInfo TimeZone => BackupScheduleCalculator.ResolveTimeZone(_options.TimeZoneId);

    private static string BuildFileName(DateTime startedAtUtc, BackupTriggerKind kind)
    {
        var suffix = kind == BackupTriggerKind.Manual ? "manual" : "auto";
        return $"ptg-backup-{startedAtUtc:yyyyMMdd-HHmmss}-{suffix}.zip";
    }

    private string EnsureStorageDirectory()
    {
        var directory = ResolveAbsolutePath(_options.StorageDirectory);
        Directory.CreateDirectory(directory);
        return directory;
    }

    private string ResolveWorkingDirectory()
    {
        var configured = string.IsNullOrWhiteSpace(_options.WorkingDirectory)
            ? Path.Combine(_options.StorageDirectory, "tmp")
            : _options.WorkingDirectory;

        var directory = ResolveAbsolutePath(configured);
        Directory.CreateDirectory(directory);
        return directory;
    }

    private string ResolveAbsolutePath(string path)
        => Path.IsPathRooted(path) ? path : Path.Combine(_environment.ContentRootPath, path);

    private static async Task<string> ComputeSha256Async(string filePath, CancellationToken ct)
    {
        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private void TryDeleteFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Unable to delete backup file {Path}.", path);
        }
    }

    private void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Unable to clean the backup working directory {Path}.", path);
        }
    }

    private async Task LogAuditAsync(string action, int entityId, string description, bool isSuccess, CancellationToken ct)
    {
        try
        {
            await _audit.LogActivityAndSaveAsync(new AuditLogEntryInput
            {
                Category = AuditLogCategories.System,
                Module = "Backups",
                EntityName = nameof(BackupJob),
                EntityId = entityId,
                Action = action,
                Description = Truncate(description, 500),
                IsSuccess = isSuccess
            }, ct);
        }
        catch (Exception ex)
        {
            // شکست در ثبت لاگ نباید خودِ بکاپ را از بین ببرد.
            _logger.LogWarning(ex, "Unable to write the backup audit entry for action {Action}.", action);
        }
    }

    private static string Truncate(string? value, int maxLength)
        => string.IsNullOrEmpty(value) || value.Length <= maxLength ? value ?? "" : value[..maxLength];
}
