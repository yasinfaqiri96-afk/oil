using System.IO.Compression;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PTGOilSystem.Web.Configuration;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;

namespace PTGOilSystem.Web.Services.Backups;

public sealed record BackupRestoreRequest(int JobId, string RequestedBy, int? RequestedByUserId, bool RestoreUploads);

public sealed record BackupRestoreResult(bool Succeeded, string Message);

/// <summary>
/// بازیابی بکاپ. عمداً از ماژول پشتیبان‌گیری جدا نگه داشته شده: ساختن بکاپ عملیاتی
/// روزمره است، اما بازیابی داده‌های فعلی را بازنویسی می‌کند و مسیر جداگانه،
/// کلید پیکربندی جداگانه و تأیید امنیتی جداگانه دارد.
/// </summary>
public interface IBackupRestoreService
{
    /// <summary>بازیابی در این نصب اجازه دارد یا نه (کلید Backup:RestoreEnabled).</summary>
    bool IsRestoreEnabled { get; }

    Task<BackupRestoreResult> RestoreAsync(BackupRestoreRequest request, CancellationToken ct = default);
}

public sealed class BackupRestoreService : IBackupRestoreService
{
    private readonly ApplicationDbContext _db;
    private readonly IPostgresBackupTool _postgresTool;
    private readonly IBackupExecutionLock _executionLock;
    private readonly IAuditService _audit;
    private readonly IWebHostEnvironment _environment;
    private readonly BackupOptions _options;
    private readonly ILogger<BackupRestoreService> _logger;

    public BackupRestoreService(
        ApplicationDbContext db,
        IPostgresBackupTool postgresTool,
        IBackupExecutionLock executionLock,
        IAuditService audit,
        IWebHostEnvironment environment,
        IOptions<BackupOptions> options,
        ILogger<BackupRestoreService> logger)
    {
        _db = db;
        _postgresTool = postgresTool;
        _executionLock = executionLock;
        _audit = audit;
        _environment = environment;
        _options = options.Value;
        _logger = logger;
    }

    public bool IsRestoreEnabled => _options.RestoreEnabled;

    public async Task<BackupRestoreResult> RestoreAsync(BackupRestoreRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!IsRestoreEnabled)
        {
            return new BackupRestoreResult(false,
                "بازیابی در این نصب غیرفعال است. برای فعال‌کردن، کلید Backup:RestoreEnabled را روی true بگذارید و سرویس را دوباره راه‌اندازی کنید.");
        }

        using var handle = _executionLock.TryAcquire($"restore:{request.RequestedBy}");
        if (handle is null)
        {
            return new BackupRestoreResult(false, "یک بکاپ در حال اجراست. بازیابی تا پایان آن شروع نمی‌شود.");
        }

        var job = await _db.BackupJobs.AsNoTracking().FirstOrDefaultAsync(item => item.Id == request.JobId, ct);
        if (job is null)
        {
            return new BackupRestoreResult(false, "بکاپ انتخاب‌شده پیدا نشد.");
        }

        if (string.IsNullOrWhiteSpace(job.LocalPath) || !File.Exists(job.LocalPath))
        {
            return new BackupRestoreResult(false, "فایل این بکاپ روی سرور موجود نیست. ابتدا آن را از Google Drive برگردانید.");
        }

        await LogAuditAsync("RestoreStarted", job.Id,
            $"بازیابی از «{job.FileName}» توسط {request.RequestedBy} آغاز شد.", true, ct);

        var workingDirectory = Path.Combine(Path.GetTempPath(), $"ptg-restore-{Guid.NewGuid():N}");

        try
        {
            // ۱) فایل نباید از زمان ساخت تغییر کرده باشد.
            var actualHash = await ComputeSha256Async(job.LocalPath, ct);
            if (!string.IsNullOrWhiteSpace(job.Sha256)
                && !string.Equals(actualHash, job.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return await FailAsync(job, "SHA-256 فایل با مقدار ثبت‌شده یکی نیست؛ فایل دستکاری یا خراب شده است.", ct);
            }

            // ۲) باز کردن آرشیو و بررسی شناسنامه
            Directory.CreateDirectory(workingDirectory);
            ZipFile.ExtractToDirectory(job.LocalPath, workingDirectory);

            var manifestPath = Path.Combine(workingDirectory, BackupManifest.FileName);
            if (!File.Exists(manifestPath))
            {
                return await FailAsync(job, "آرشیو شناسنامه (manifest.json) ندارد.", ct);
            }

            var manifest = BackupManifest.FromJson(await File.ReadAllTextAsync(manifestPath, ct));
            if (manifest is null || manifest.FormatVersion != 1)
            {
                return await FailAsync(job, "ساختار این آرشیو برای نسخهٔ فعلی برنامه شناخته‌شده نیست.", ct);
            }

            var dumpPath = Path.Combine(workingDirectory, BackupManifest.DatabaseDumpEntryName);
            if (!File.Exists(dumpPath))
            {
                return await FailAsync(job, "آرشیو فایل database.dump را ندارد.", ct);
            }

            // ۳) dump باید قابل خواندن باشد، پیش از آنکه دیتابیس فعلی دست بخورد.
            var verifyResult = await _postgresTool.VerifyDumpAsync(dumpPath, ct);
            if (!verifyResult.Succeeded)
            {
                return await FailAsync(job, $"فایل dump قابل خواندن نیست: {Truncate(verifyResult.FailureText, 400)}", ct);
            }

            // ۴) هدف بازیابی هرگز نباید یک دیتابیس سیستمی PostgreSQL باشد.
            var connectionString = _db.Database.GetConnectionString()
                ?? throw new InvalidOperationException("رشتهٔ اتصال پایگاه داده در دسترس نیست.");
            DatabaseSafetyGuard.EnsureMigrationAllowed(_db.Database.GetDbConnection().Database);

            var restoreResult = await _postgresTool.RestoreAsync(connectionString, dumpPath, ct);
            if (!restoreResult.Succeeded)
            {
                return await FailAsync(job, $"pg_restore ناموفق بود: {Truncate(restoreResult.FailureText, 400)}", ct);
            }

            var restoredFiles = request.RestoreUploads ? RestoreUploads(workingDirectory) : 0;

            await LogAuditAsync("RestoreCompleted", job.Id,
                $"بازیابی از «{job.FileName}» کامل شد ({restoredFiles} فایل ضمیمه بازگردانده شد).", true, ct);

            return new BackupRestoreResult(true,
                $"بازیابی از «{job.FileName}» کامل شد. برنامه را دوباره راه‌اندازی کنید تا همهٔ سرویس‌ها با داده‌های بازگردانده‌شده کار کنند.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Restore from backup {JobId} failed.", request.JobId);
            return await FailAsync(job, ex.Message, ct);
        }
        finally
        {
            TryDeleteDirectory(workingDirectory);
        }
    }

    /// <summary>
    /// ضمایم روی نسخهٔ فعلی نوشته می‌شوند و فایل‌های اضافهٔ فعلی حذف نمی‌شوند؛
    /// بازیابی نباید ضمیمه‌ای را که در بکاپ نبوده از بین ببرد.
    /// </summary>
    private int RestoreUploads(string workingDirectory)
    {
        var source = Path.Combine(workingDirectory, "uploads");
        if (!Directory.Exists(source) || string.IsNullOrWhiteSpace(_environment.WebRootPath))
        {
            return 0;
        }

        var target = Path.Combine(_environment.WebRootPath, "uploads");
        Directory.CreateDirectory(target);

        var restored = 0;
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(source, file);
            var destination = Path.Combine(target, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
            restored++;
        }

        return restored;
    }

    private async Task<BackupRestoreResult> FailAsync(BackupJob job, string reason, CancellationToken ct)
    {
        await LogAuditAsync("RestoreFailed", job.Id, $"بازیابی از «{job.FileName}» ناموفق بود: {reason}", false, ct);
        return new BackupRestoreResult(false, reason);
    }

    private static async Task<string> ComputeSha256Async(string filePath, CancellationToken ct)
    {
        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private async Task LogAuditAsync(string action, int entityId, string description, bool isSuccess, CancellationToken ct)
    {
        try
        {
            await _audit.LogActivityAndSaveAsync(new AuditLogEntryInput
            {
                Category = AuditLogCategories.Security,
                Module = "BackupRestore",
                EntityName = nameof(BackupJob),
                EntityId = entityId,
                Action = action,
                Description = Truncate(description, 500),
                IsSuccess = isSuccess
            }, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unable to write the restore audit entry for action {Action}.", action);
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
            _logger.LogWarning(ex, "Unable to clean the restore working directory {Path}.", path);
        }
    }

    private static string Truncate(string? value, int maxLength)
        => string.IsNullOrEmpty(value) || value.Length <= maxLength ? value ?? "" : value[..maxLength];
}
