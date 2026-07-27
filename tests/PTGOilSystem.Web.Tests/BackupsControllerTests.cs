using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Options;
using PTGOilSystem.Web.Configuration;
using PTGOilSystem.Web.Controllers;
using PTGOilSystem.Web.Models.Backups;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Security;
using PTGOilSystem.Web.Services.Backups;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// صفحهٔ پشتیبان‌گیری: دسترسی، صف اجرا و مسیرهای اصلی.
/// </summary>
public class BackupsControllerTests
{
    [Fact]
    public void The_Backups_Page_Is_Restricted_To_The_Backup_Admin_Policy()
    {
        var attribute = typeof(BackupsController).GetCustomAttribute<AuthorizeAttribute>();

        Assert.NotNull(attribute);
        Assert.Equal(AuthPolicies.BackupAdmin, attribute!.Policy);
    }

    [Fact]
    public void The_Restore_Page_Lives_On_Its_Own_Controller_And_Is_Also_Restricted()
    {
        var attribute = typeof(BackupRestoreController).GetCustomAttribute<AuthorizeAttribute>();

        Assert.NotNull(attribute);
        Assert.Equal(AuthPolicies.BackupAdmin, attribute!.Policy);
        Assert.NotEqual(typeof(BackupsController), typeof(BackupRestoreController));
    }

    [Fact]
    public void Only_The_Admin_Role_Counts_As_SuperAdmin_For_Restore()
    {
        Assert.True(RoleAccessRules.CanRestoreBackups(UserWith(AuthRoles.Admin)));
        Assert.False(RoleAccessRules.CanRestoreBackups(UserWith(AuthRoles.Manager)));
        Assert.False(RoleAccessRules.CanRestoreBackups(UserWith(AuthRoles.Operator)));
        Assert.False(RoleAccessRules.CanRestoreBackups(UserWith(AuthRoles.Viewer)));
    }

    [Fact]
    public void Managing_Users_Alone_Does_Not_Grant_Access_To_Backups()
    {
        var manager = UserWith(AuthRoles.Manager, new Claim(AppClaimTypes.Permission, AppPermissions.ManageUsers));

        Assert.True(RoleAccessRules.CanManageUsers(manager));
        Assert.False(RoleAccessRules.CanManageBackups(manager));
    }

    [Fact]
    public void An_Explicit_ManageBackups_Permission_Grants_Page_Access_But_Not_Restore()
    {
        var user = UserWith(AuthRoles.Manager, new Claim(AppClaimTypes.Permission, AppPermissions.ManageBackups));

        Assert.True(RoleAccessRules.CanManageBackups(user));
        Assert.False(RoleAccessRules.CanRestoreBackups(user));
    }

    [Fact]
    public void Backups_Are_Reachable_Through_The_Management_Navigation_Group()
    {
        Assert.Equal(RoleNavigationKeys.Management, RoleAccessRules.NavigationKeyForController("Backups"));
        Assert.Equal(RoleNavigationKeys.Management, RoleAccessRules.NavigationKeyForController("BackupRestore"));
    }

    [Fact]
    public void Create_Now_Answers_With_Json_So_The_Button_Is_Never_Left_Spinning()
    {
        // ارسال معمولیِ فرم دکمه را تا پایان ناوبری قفل می‌کرد و صفحهٔ تازه هم آن را
        // دوباره غیرفعال تحویل می‌داد. پاسخ JSON به صفحه اجازه می‌دهد فوراً آزادش کند.
        var queue = new BackupTriggerQueue();
        var controller = NewController(queue, new BackupExecutionLock(TimeProvider.System));

        var json = Assert.IsType<JsonResult>(controller.CreateNow());
        var payload = Assert.IsType<BackupTriggerResponse>(json.Value);

        Assert.True(payload.Queued);
        Assert.True(payload.ShouldPoll);
        Assert.False(string.IsNullOrWhiteSpace(payload.Message));
        Assert.True(queue.DequeueAsync(CancellationToken.None).IsCompleted);
    }

    [Fact]
    public void Create_Now_Reports_A_Clear_Message_While_A_Backup_Is_Already_Running()
    {
        var queue = new BackupTriggerQueue();
        var executionLock = new BackupExecutionLock(TimeProvider.System);
        using var held = executionLock.TryAcquire("scheduler");
        var controller = NewController(queue, executionLock);

        var json = Assert.IsType<JsonResult>(controller.CreateNow());
        var payload = Assert.IsType<BackupTriggerResponse>(json.Value);

        Assert.False(payload.Queued);
        Assert.True(payload.IsRunning);
        Assert.Contains("در حال اجرا", payload.Message, StringComparison.Ordinal);
        Assert.False(queue.DequeueAsync(new CancellationTokenSource(10).Token).IsCompleted);
    }

    [Fact]
    public void A_Queued_Request_Is_Visible_Until_The_Scheduler_Picks_It_Up()
    {
        var queue = new BackupTriggerQueue();

        Assert.False(queue.HasPending);
        Assert.True(queue.TryEnqueue(new BackupTriggerRequest(BackupTriggerKind.Manual, "admin", 1)));
        Assert.True(queue.HasPending);

        Assert.True(queue.DequeueAsync(CancellationToken.None).IsCompleted);
        Assert.False(queue.HasPending);
    }

    [Fact]
    public void The_Live_Region_Is_Served_By_Its_Own_Partial_For_Polling()
    {
        var controller = NewController(new BackupTriggerQueue(), new BackupExecutionLock(TimeProvider.System));

        var result = Assert.IsType<PartialViewResult>(controller.Status(CancellationToken.None).GetAwaiter().GetResult());

        Assert.Equal("_BackupLive", result.ViewName);
        var model = Assert.IsType<BackupIndexViewModel>(result.Model);
        Assert.False(model.IsBusy);
    }

    [Fact]
    public void The_Page_Reports_Busy_While_Either_Running_Or_Queued()
    {
        Assert.True(new BackupIndexViewModel { IsRunning = true }.IsBusy);
        Assert.True(new BackupIndexViewModel { IsQueued = true }.IsBusy);
        Assert.False(new BackupIndexViewModel().IsBusy);
    }

    [Fact]
    public void The_Manual_Queue_Holds_At_Most_One_Pending_Request()
    {
        var queue = new BackupTriggerQueue();
        var request = new BackupTriggerRequest(BackupTriggerKind.Manual, "admin", 1);

        Assert.True(queue.TryEnqueue(request));
        Assert.False(queue.TryEnqueue(request));
    }

    [Fact]
    public void The_Execution_Lock_Admits_Exactly_One_Holder_And_Releases_On_Dispose()
    {
        var executionLock = new BackupExecutionLock(TimeProvider.System);

        var first = executionLock.TryAcquire("first");
        Assert.NotNull(first);
        Assert.True(executionLock.IsRunning);
        Assert.Equal("first", executionLock.CurrentOwner);
        Assert.Null(executionLock.TryAcquire("second"));

        first!.Dispose();
        Assert.False(executionLock.IsRunning);

        using var third = executionLock.TryAcquire("third");
        Assert.NotNull(third);
    }

    [Fact]
    public void History_Rows_Expose_Download_Only_When_The_File_Is_Still_On_The_Server()
    {
        var withFile = BackupsController.ToRow(new BackupJob
        {
            Id = 1,
            FileName = "a.zip",
            Status = BackupRunStatus.Succeeded,
            LocalPath = "/srv/backups/a.zip"
        });
        var uploadedOnly = BackupsController.ToRow(new BackupJob
        {
            Id = 2,
            FileName = "b.zip",
            Status = BackupRunStatus.Succeeded,
            LocalPath = null,
            DriveUploadStatus = BackupDriveUploadStatus.Uploaded
        });
        var running = BackupsController.ToRow(new BackupJob { Id = 3, FileName = "c.zip", Status = BackupRunStatus.Running });

        Assert.True(withFile.CanDownload);
        Assert.False(uploadedOnly.CanDownload);
        Assert.True(withFile.CanDelete);
        Assert.False(running.CanDelete);
    }

    [Fact]
    public void The_Restore_Confirmation_Phrase_Is_Fixed_And_Non_Empty()
        => Assert.False(string.IsNullOrWhiteSpace(BackupRestoreFormViewModel.ConfirmationPhrase));

    private static ClaimsPrincipal UserWith(string role, params Claim[] extraClaims)
    {
        var claims = new List<Claim> { new(ClaimTypes.Role, role) };
        claims.AddRange(extraClaims);
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));
    }

    private static BackupsController NewController(IBackupTriggerQueue queue, IBackupExecutionLock executionLock)
    {
        var controller = new BackupsController(
            new StubBackupService(),
            new StubDriveClient(),
            queue,
            executionLock,
            new StubRestoreService(),
            new StubCurrentUser(),
            Options.Create(new BackupOptions()))
        {
            TempData = new TempDataDictionary(new DefaultHttpContext(), new StubTempDataProvider())
        };

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = UserWith(AuthRoles.Admin) }
        };

        return controller;
    }

    private sealed class StubBackupService : IBackupService
    {
        public Task<BackupSetting> GetSettingsAsync(CancellationToken ct = default) => Task.FromResult(new BackupSetting());
        public Task<BackupSetting> UpdateSettingsAsync(BackupSettingsInput input, CancellationToken ct = default) => Task.FromResult(new BackupSetting());
        public Task<BackupSetting> EnsureNextRunScheduledAsync(CancellationToken ct = default) => Task.FromResult(new BackupSetting());
        public Task<BackupRunResult> RunAsync(BackupTriggerRequest request, CancellationToken ct = default) => Task.FromResult(new BackupRunResult(true, 1, "ok"));
        public Task<IReadOnlyList<BackupJob>> GetHistoryAsync(int take, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<BackupJob>>([]);
        public Task<BackupJob?> GetJobAsync(int id, CancellationToken ct = default) => Task.FromResult<BackupJob?>(null);
        public Task<BackupDownload?> GetDownloadAsync(int id, CancellationToken ct = default) => Task.FromResult<BackupDownload?>(null);
        public Task<bool> DeleteJobAsync(int id, CancellationToken ct = default) => Task.FromResult(true);
        public Task<int> RetryPendingDriveUploadsAsync(CancellationToken ct = default) => Task.FromResult(0);
    }

    private sealed class StubDriveClient : IGoogleDriveBackupClient
    {
        public Task<BackupDriveCredential?> GetCredentialAsync(CancellationToken ct = default) => Task.FromResult<BackupDriveCredential?>(null);
        public Task<string> BeginAuthorizationAsync(string clientId, string clientSecret, string redirectUri, string state, CancellationToken ct = default) => Task.FromResult("https://example.invalid");
        public Task<(bool Succeeded, string Message)> CompleteAuthorizationAsync(string code, string redirectUri, CancellationToken ct = default) => Task.FromResult((true, "ok"));
        public Task DisconnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task SetFolderAsync(string? folderId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<DriveUploadResult> UploadBackupAsync(string filePath, string fileName, string? resumeUri, CancellationToken ct = default) => Task.FromResult(new DriveUploadResult(false, null, null, "not connected"));
    }

    private sealed class StubRestoreService : IBackupRestoreService
    {
        public bool IsRestoreEnabled => false;
        public Task<BackupRestoreResult> RestoreAsync(BackupRestoreRequest request, CancellationToken ct = default) => Task.FromResult(new BackupRestoreResult(false, "disabled"));
    }

    private sealed class StubCurrentUser : ICurrentUserContext
    {
        public bool IsAuthenticated => true;
        public int? UserId => 1;
        public string? Username => "admin";
        public string? FullName => "System Administrator";
        public string? RoleName => AuthRoles.Admin;
        public ClaimsPrincipal Principal => UserWith(AuthRoles.Admin);
    }

    private sealed class StubTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object?> LoadTempData(HttpContext context) => new Dictionary<string, object?>();
        public void SaveTempData(HttpContext context, IDictionary<string, object?> values) { }
    }
}
