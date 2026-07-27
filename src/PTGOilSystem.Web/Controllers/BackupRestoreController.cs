using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Backups;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Security;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Backups;

namespace PTGOilSystem.Web.Controllers;

/// <summary>
/// بازیابی بکاپ — مسیر جداگانه از صفحهٔ پشتیبان‌گیری.
///
/// چهار سد پشت سر هم: نقش SuperAdmin، فعال‌بودن Backup:RestoreEnabled در پیکربندی،
/// تأیید رمز عبورِ همان کاربر، و تایپ عبارت تأیید. سرویس بازیابی هم پیش از دست‌زدن
/// به دیتابیس، SHA-256 و خواندنی‌بودن dump را دوباره بررسی می‌کند.
/// </summary>
[Authorize(Policy = AuthPolicies.BackupAdmin)]
public class BackupRestoreController : Controller
{
    private const int RestorableListSize = 30;

    private readonly IBackupService _backups;
    private readonly IBackupRestoreService _restore;
    private readonly IUserService _users;
    private readonly ICurrentUserContext _currentUser;
    private readonly ApplicationDbContext _db;
    private readonly IAuditService _audit;

    public BackupRestoreController(
        IBackupService backups,
        IBackupRestoreService restore,
        IUserService users,
        ICurrentUserContext currentUser,
        ApplicationDbContext db,
        IAuditService audit)
    {
        _backups = backups;
        _restore = restore;
        _users = users;
        _currentUser = currentUser;
        _db = db;
        _audit = audit;
    }

    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        if (!RoleAccessRules.CanRestoreBackups(User))
        {
            return RedirectToAction("AccessDenied", "Auth");
        }

        return View(await BuildViewModelAsync(cancellationToken));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Restore(BackupRestoreFormViewModel model, CancellationToken cancellationToken)
    {
        if (!RoleAccessRules.CanRestoreBackups(User))
        {
            return RedirectToAction("AccessDenied", "Auth");
        }

        if (!_restore.IsRestoreEnabled)
        {
            TempData["err"] = "بازیابی در این نصب غیرفعال است.";
            return RedirectToAction(nameof(Index));
        }

        if (!ModelState.IsValid)
        {
            return View(nameof(Index), await BuildViewModelAsync(cancellationToken));
        }

        if (!string.Equals(model.Confirmation?.Trim(), BackupRestoreFormViewModel.ConfirmationPhrase, StringComparison.Ordinal))
        {
            await LogDeniedAsync(model.JobId, "عبارت تأیید نادرست بود.", cancellationToken);
            TempData["err"] = $"برای اجرای بازیابی باید دقیقاً عبارت «{BackupRestoreFormViewModel.ConfirmationPhrase}» را وارد کنید.";
            return RedirectToAction(nameof(Index));
        }

        if (!await VerifyCurrentUserPasswordAsync(model.Password, cancellationToken))
        {
            await LogDeniedAsync(model.JobId, "رمز عبور تأیید نشد.", cancellationToken);
            TempData["err"] = "رمز عبور تأیید نشد. بازیابی انجام نشد.";
            return RedirectToAction(nameof(Index));
        }

        var result = await _restore.RestoreAsync(
            new BackupRestoreRequest(
                model.JobId,
                _currentUser.Username ?? User.Identity?.Name ?? "unknown",
                _currentUser.UserId,
                model.RestoreUploads),
            cancellationToken);

        TempData[result.Succeeded ? "ok" : "err"] = result.Message;
        return RedirectToAction(nameof(Index));
    }

    private async Task<bool> VerifyCurrentUserPasswordAsync(string password, CancellationToken cancellationToken)
    {
        var userId = _currentUser.UserId;
        if (userId is null || string.IsNullOrEmpty(password))
        {
            return false;
        }

        var storedHash = await _db.Users
            .Where(user => user.Id == userId && user.IsActive)
            .Select(user => user.PasswordHash)
            .FirstOrDefaultAsync(cancellationToken);

        return !string.IsNullOrEmpty(storedHash) && _users.VerifyHash(password, storedHash);
    }

    private async Task<BackupRestoreIndexViewModel> BuildViewModelAsync(CancellationToken cancellationToken)
    {
        var history = await _backups.GetHistoryAsync(RestorableListSize, cancellationToken);

        return new BackupRestoreIndexViewModel
        {
            RestoreEnabled = _restore.IsRestoreEnabled,
            IsSuperAdmin = RoleAccessRules.IsSuperAdmin(User),
            RestorableBackups = history
                .Where(job => job.Status == BackupRunStatus.Succeeded && !string.IsNullOrWhiteSpace(job.LocalPath))
                .Select(BackupsController.ToRow)
                .ToList()
        };
    }

    private async Task LogDeniedAsync(int jobId, string reason, CancellationToken cancellationToken)
        => await _audit.LogActivityAndSaveAsync(new AuditLogEntryInput
        {
            Category = AuditLogCategories.Security,
            Module = "BackupRestore",
            EntityName = nameof(BackupJob),
            EntityId = jobId,
            Action = "RestoreDenied",
            Description = $"تلاش برای بازیابی رد شد: {reason}",
            IsSuccess = false,
            ActorUserId = _currentUser.UserId,
            ActorUsername = _currentUser.Username,
            RequestPath = Request.Path.Value,
            ControllerName = "BackupRestore",
            ActionName = nameof(Restore),
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString()
        }, cancellationToken);
}
