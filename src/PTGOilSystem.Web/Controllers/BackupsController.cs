using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using PTGOilSystem.Web.Configuration;
using PTGOilSystem.Web.Models.Backups;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Security;
using PTGOilSystem.Web.Services.Backups;

namespace PTGOilSystem.Web.Controllers;

/// <summary>
/// صفحهٔ «پشتیبان‌گیری» — فقط SuperAdmin.
///
/// صفحه عمداً کوچک نگه داشته شده است: وضعیت، آخرین/بعدی، وضعیت Google Drive،
/// دکمهٔ ایجاد بکاپ، تنظیمات خودکار و جدول تاریخچه. بازیابی اینجا نیست؛
/// مسیر جداگانهٔ <see cref="BackupRestoreController"/> را دارد.
/// </summary>
[Authorize(Policy = AuthPolicies.BackupAdmin)]
public class BackupsController : Controller
{
    private const int HistoryPageSize = 30;

    private readonly IBackupService _backups;
    private readonly IGoogleDriveBackupClient _drive;
    private readonly IBackupTriggerQueue _queue;
    private readonly IBackupExecutionLock _executionLock;
    private readonly IBackupRestoreService _restore;
    private readonly ICurrentUserContext _currentUser;
    private readonly BackupOptions _options;

    public BackupsController(
        IBackupService backups,
        IGoogleDriveBackupClient drive,
        IBackupTriggerQueue queue,
        IBackupExecutionLock executionLock,
        IBackupRestoreService restore,
        ICurrentUserContext currentUser,
        IOptions<BackupOptions> options)
    {
        _backups = backups;
        _drive = drive;
        _queue = queue;
        _executionLock = executionLock;
        _restore = restore;
        _currentUser = currentUser;
        _options = options.Value;
    }

    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
        => View(await BuildIndexViewModelAsync(cancellationToken));

    /// <summary>
    /// ناحیهٔ زندهٔ صفحه (کارت‌های وضعیت + جدول تاریخچه). صفحه هنگام اجرای بکاپ همین
    /// را Poll و جایگزین می‌کند تا نیازی به بارگذاری دوبارهٔ کل صفحه نباشد.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Status(CancellationToken cancellationToken)
        => PartialView("_BackupLive", await BuildIndexViewModelAsync(cancellationToken));

    /// <summary>
    /// بکاپ دستی. اجرا به صفِ پشت‌صحنه سپرده می‌شود تا درخواست HTTP منتظر pg_dump نماند
    /// و دو بکاپ هم‌زمان ممکن نشود.
    ///
    /// پاسخ عمداً JSON است و نه Redirect: صفحه باید بتواند دکمه را دقیقاً در همان لحظه
    /// آزاد کند و نتیجه یا خطا را نشان دهد. با Redirect، دکمه تا پایان ناوبری قفل می‌ماند
    /// و صفحهٔ تازه هم دوباره آن را غیرفعال تحویل می‌داد — همان حالتی که «گیرکرده» دیده می‌شد.
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult CreateNow()
    {
        if (_executionLock.IsRunning)
        {
            return Json(new BackupTriggerResponse(
                false, true, false, "یک بکاپ در حال اجرا است. تا پایان آن، بکاپ جدید شروع نمی‌شود."));
        }

        var requestedBy = _currentUser.Username ?? User.Identity?.Name ?? "unknown";
        var enqueued = _queue.TryEnqueue(
            new BackupTriggerRequest(BackupTriggerKind.Manual, requestedBy, _currentUser.UserId));

        return Json(new BackupTriggerResponse(
            enqueued,
            _executionLock.IsRunning,
            _queue.HasPending,
            enqueued
                ? "بکاپ شروع شد. وضعیت همین‌جا به‌روزرسانی می‌شود."
                : "یک درخواست بکاپ از قبل در صف است."));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveSettings(BackupSettingsFormViewModel model, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            var viewModel = await BuildIndexViewModelAsync(cancellationToken, model);
            return View(nameof(Index), viewModel);
        }

        await _backups.UpdateSettingsAsync(
            new BackupSettingsInput(
                model.AutoBackupEnabled,
                model.Schedule,
                model.RunAtHourLocal,
                model.RunAtMinuteLocal,
                model.StorageTarget,
                model.KeepDaily,
                model.KeepWeekly,
                model.KeepMonthly),
            cancellationToken);

        TempData["ok"] = "تنظیمات پشتیبان‌گیری ذخیره شد.";
        return RedirectToAction(nameof(Index));
    }

    [HttpGet]
    public async Task<IActionResult> Download(int id, CancellationToken cancellationToken)
    {
        var download = await _backups.GetDownloadAsync(id, cancellationToken);
        if (download is null)
        {
            TempData["err"] = "فایل این بکاپ روی سرور موجود نیست.";
            return RedirectToAction(nameof(Index));
        }

        var stream = new FileStream(download.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return File(stream, "application/zip", download.FileName);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id, CancellationToken cancellationToken)
    {
        var deleted = await _backups.DeleteJobAsync(id, cancellationToken);
        TempData[deleted ? "ok" : "err"] = deleted
            ? "بکاپ حذف شد."
            : "این بکاپ حذف نشد. ممکن است در حال اجرا باشد یا قبلاً حذف شده باشد.";

        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RetryDriveUpload(CancellationToken cancellationToken)
    {
        var uploaded = await _backups.RetryPendingDriveUploadsAsync(cancellationToken);
        TempData[uploaded > 0 ? "ok" : "err"] = uploaded > 0
            ? $"{uploaded} نسخه در Google Drive بارگذاری شد."
            : "نسخهٔ آماده‌ای برای بارگذاری پیدا نشد یا بارگذاری دوباره ناموفق بود.";

        return RedirectToAction(nameof(Index));
    }

    // ---- Google Drive OAuth -------------------------------------------------

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ConnectDrive(BackupDriveConnectViewModel model, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            TempData["err"] = "Client ID و Client Secret را کامل وارد کنید.";
            return RedirectToAction(nameof(Index));
        }

        await _drive.SetFolderAsync(model.FolderId, cancellationToken);

        var authorizationUrl = await _drive.BeginAuthorizationAsync(
            model.ClientId,
            model.ClientSecret,
            ResolveRedirectUri(),
            state: Guid.NewGuid().ToString("N"),
            cancellationToken);

        return Redirect(authorizationUrl);
    }

    /// <summary>مقصد بازگشت گوگل. همین آدرس باید در Google Cloud Console ثبت شده باشد.</summary>
    [HttpGet]
    public async Task<IActionResult> DriveCallback(string? code, string? error, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(error) || string.IsNullOrWhiteSpace(code))
        {
            TempData["err"] = $"اتصال Google Drive انجام نشد: {error ?? "کد دسترسی دریافت نشد"}";
            return RedirectToAction(nameof(Index));
        }

        var (succeeded, message) = await _drive.CompleteAuthorizationAsync(code, ResolveRedirectUri(), cancellationToken);
        TempData[succeeded ? "ok" : "err"] = message;
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DisconnectDrive(CancellationToken cancellationToken)
    {
        await _drive.DisconnectAsync(cancellationToken);
        TempData["ok"] = "اتصال Google Drive قطع شد. فایل‌های آپلودشده در Drive دست‌نخورده مانده‌اند.";
        return RedirectToAction(nameof(Index));
    }

    // ---- Helpers ------------------------------------------------------------

    private string ResolveRedirectUri()
        => string.IsNullOrWhiteSpace(_options.DriveRedirectUri)
            ? Url.Action(nameof(DriveCallback), "Backups", null, Request.Scheme)!
            : _options.DriveRedirectUri;

    private async Task<BackupIndexViewModel> BuildIndexViewModelAsync(
        CancellationToken cancellationToken,
        BackupSettingsFormViewModel? settingsForm = null)
    {
        var settings = await _backups.GetSettingsAsync(cancellationToken);
        var history = await _backups.GetHistoryAsync(HistoryPageSize, cancellationToken);
        var credential = await _drive.GetCredentialAsync(cancellationToken);

        var rows = history.Select(ToRow).ToList();

        return new BackupIndexViewModel
        {
            IsRunning = _executionLock.IsRunning,
            IsQueued = _queue.HasPending,
            RunningSinceUtc = _executionLock.StartedAtUtc,
            LastBackup = rows.FirstOrDefault(row => row.Status == BackupRunStatus.Succeeded),
            LatestRun = rows.FirstOrDefault(),
            NextRunAtUtc = settings.AutoBackupEnabled ? settings.NextRunAtUtc : null,
            TimeZoneId = _options.TimeZoneId,
            CanRestore = RoleAccessRules.CanRestoreBackups(User),
            RestoreEnabled = _restore.IsRestoreEnabled,
            History = rows,
            Drive = new BackupDriveStatusViewModel
            {
                IsConfigured = !string.IsNullOrWhiteSpace(credential?.ClientId),
                IsConnected = credential?.IsConnected == true,
                AccountEmail = credential?.AccountEmail,
                FolderId = credential?.FolderId,
                ConnectedAtUtc = credential?.ConnectedAtUtc,
                LastError = credential?.LastError
            },
            Settings = settingsForm ?? new BackupSettingsFormViewModel
            {
                AutoBackupEnabled = settings.AutoBackupEnabled,
                Schedule = settings.Schedule,
                RunAtHourLocal = settings.RunAtHourLocal,
                RunAtMinuteLocal = settings.RunAtMinuteLocal,
                StorageTarget = settings.StorageTarget,
                KeepDaily = settings.KeepDaily,
                KeepWeekly = settings.KeepWeekly,
                KeepMonthly = settings.KeepMonthly
            }
        };
    }

    internal static BackupHistoryRowViewModel ToRow(BackupJob job)
        => new()
        {
            Id = job.Id,
            StartedAtUtc = job.StartedAtUtc,
            CompletedAtUtc = job.CompletedAtUtc,
            TriggerKind = job.TriggerKind,
            Status = job.Status,
            StorageTarget = job.StorageTarget,
            RetentionTier = job.RetentionTier,
            DriveUploadStatus = job.DriveUploadStatus,
            SizeBytes = job.SizeBytes,
            Sha256 = job.Sha256,
            FileName = job.FileName,
            CreatedByUsername = job.CreatedByUsername,
            ErrorMessage = job.ErrorMessage,
            CanDownload = job.Status == BackupRunStatus.Succeeded && !string.IsNullOrWhiteSpace(job.LocalPath)
        };
}
