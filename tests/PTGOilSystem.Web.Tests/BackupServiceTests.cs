using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PTGOilSystem.Web.Configuration;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Backups;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// اجرای بکاپ سرتاسر: ساخت آرشیو، صحت‌سنجی، SHA-256، سیاست نگهداری و رفتار
/// هنگام قطع اینترنت. ابزارهای PostgreSQL با یک بدل جایگزین شده‌اند تا آزمون به
/// نصب‌بودن pg_dump روی ماشین وابسته نباشد؛ بقیهٔ مسیر واقعی است.
/// </summary>
public sealed class BackupServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly string _root;

    public BackupServiceTests()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();

        _root = Path.Combine(Path.GetTempPath(), $"ptg-backup-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_root, "wwwroot", "uploads", "employees"));
        File.WriteAllText(Path.Combine(_root, "wwwroot", "uploads", "employees", "photo.txt"), "attachment");

        using var db = NewContext();
        db.Database.EnsureCreated();
    }

    [Fact]
    public async Task Default_Settings_Are_Daily_At_Two_AM_Keeping_Seven_Four_Six()
    {
        await using var db = NewContext();
        var service = NewService(db, new FakePostgresTool());

        var settings = await service.GetSettingsAsync();

        Assert.True(settings.AutoBackupEnabled);
        Assert.Equal(BackupSchedule.Daily, settings.Schedule);
        Assert.Equal(2, settings.RunAtHourLocal);
        Assert.Equal(0, settings.RunAtMinuteLocal);
        Assert.Equal(BackupStorageTarget.Server, settings.StorageTarget);
        Assert.Equal(7, settings.KeepDaily);
        Assert.Equal(4, settings.KeepWeekly);
        Assert.Equal(6, settings.KeepMonthly);
        Assert.NotNull(settings.NextRunAtUtc);
    }

    [Fact]
    public async Task A_Successful_Run_Produces_A_Verified_Archive_With_All_Four_Parts()
    {
        await using var db = NewContext();
        var service = NewService(db, new FakePostgresTool());

        var result = await service.RunAsync(new BackupTriggerRequest(BackupTriggerKind.Manual, "admin", 1));

        Assert.True(result.Succeeded, result.Message);

        var job = await db.BackupJobs.SingleAsync();
        Assert.Equal(BackupRunStatus.Succeeded, job.Status);
        Assert.True(job.SizeBytes > 0);
        Assert.Equal(64, job.Sha256!.Length);
        Assert.True(File.Exists(job.LocalPath));
        Assert.Equal(job.Sha256, Sha256OfFile(job.LocalPath!));

        using var archive = ZipFile.OpenRead(job.LocalPath!);
        Assert.NotNull(archive.GetEntry("database.dump"));
        Assert.NotNull(archive.GetEntry("settings.json"));
        Assert.NotNull(archive.GetEntry("manifest.json"));
        Assert.Contains(archive.Entries, entry => entry.FullName.StartsWith("uploads/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_Manifest_Records_The_Dump_Size_And_Attachment_Count()
    {
        await using var db = NewContext();
        var service = NewService(db, new FakePostgresTool());

        await service.RunAsync(new BackupTriggerRequest(BackupTriggerKind.Scheduled, "system", null));

        var job = await db.BackupJobs.SingleAsync();
        using var archive = ZipFile.OpenRead(job.LocalPath!);
        using var reader = new StreamReader(archive.GetEntry("manifest.json")!.Open());
        var manifest = BackupManifest.FromJson(await reader.ReadToEndAsync());

        Assert.NotNull(manifest);
        Assert.Equal(1, manifest!.FormatVersion);
        Assert.True(manifest.DatabaseDumpBytes > 0);
        Assert.Equal(1, manifest.UploadsFileCount);
        Assert.Equal("Scheduled", manifest.TriggerKind);
    }

    [Fact]
    public async Task Redacted_Settings_Never_Carry_The_Connection_String_Into_The_Archive()
    {
        await using var db = NewContext();
        var service = NewService(db, new FakePostgresTool());

        await service.RunAsync(new BackupTriggerRequest(BackupTriggerKind.Manual, "admin", 1));

        var job = await db.BackupJobs.SingleAsync();
        using var archive = ZipFile.OpenRead(job.LocalPath!);
        using var reader = new StreamReader(archive.GetEntry("settings.json")!.Open());
        var settingsJson = await reader.ReadToEndAsync();

        Assert.DoesNotContain("super-secret-value", settingsJson, StringComparison.Ordinal);
        Assert.Contains("***redacted***", settingsJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_Failed_Dump_Marks_The_Job_Failed_And_Leaves_No_Archive_Behind()
    {
        await using var db = NewContext();
        var service = NewService(db, new FakePostgresTool { DumpSucceeds = false });

        var result = await service.RunAsync(new BackupTriggerRequest(BackupTriggerKind.Manual, "admin", 1));

        Assert.False(result.Succeeded);

        var job = await db.BackupJobs.SingleAsync();
        Assert.Equal(BackupRunStatus.Failed, job.Status);
        Assert.Null(job.LocalPath);
        Assert.False(Directory.Exists(StorageDirectory) && Directory.EnumerateFiles(StorageDirectory).Any());
    }

    [Fact]
    public async Task An_Unreadable_Dump_Is_Never_Reported_As_A_Successful_Backup()
    {
        await using var db = NewContext();
        var service = NewService(db, new FakePostgresTool { VerifySucceeds = false });

        var result = await service.RunAsync(new BackupTriggerRequest(BackupTriggerKind.Manual, "admin", 1));

        Assert.False(result.Succeeded);
        Assert.Equal(BackupRunStatus.Failed, (await db.BackupJobs.SingleAsync()).Status);
    }

    [Fact]
    public async Task A_Second_Backup_Is_Refused_While_One_Is_Already_Running()
    {
        await using var db = NewContext();
        var executionLock = new BackupExecutionLock(TimeProvider.System);
        var service = NewService(db, new FakePostgresTool(), executionLock: executionLock);

        using var held = executionLock.TryAcquire("first");
        Assert.NotNull(held);

        var result = await service.RunAsync(new BackupTriggerRequest(BackupTriggerKind.Manual, "admin", 1));

        Assert.False(result.Succeeded);
        Assert.True(result.WasBlockedByRunningBackup);
        Assert.Empty(await db.BackupJobs.ToListAsync());
    }

    [Fact]
    public async Task A_Failed_Drive_Upload_Keeps_The_Local_Copy_And_The_Resume_Address()
    {
        await using var db = NewContext();
        var drive = new FakeDriveClient
        {
            Connected = true,
            Result = new DriveUploadResult(false, null, "https://upload.example/session/1", "اینترنت قطع شد.")
        };
        var service = NewService(db, new FakePostgresTool(), drive);
        await service.UpdateSettingsAsync(SettingsWith(BackupStorageTarget.Both));

        var result = await service.RunAsync(new BackupTriggerRequest(BackupTriggerKind.Manual, "admin", 1));

        Assert.True(result.Succeeded, result.Message);

        var job = await db.BackupJobs.SingleAsync();
        Assert.Equal(BackupRunStatus.Succeeded, job.Status);
        Assert.Equal(BackupDriveUploadStatus.Failed, job.DriveUploadStatus);
        Assert.Equal("https://upload.example/session/1", job.DriveResumeUri);
        Assert.True(File.Exists(job.LocalPath));
    }

    [Fact]
    public async Task Retrying_A_Pending_Upload_Resumes_From_The_Stored_Session_Address()
    {
        await using var db = NewContext();
        var drive = new FakeDriveClient
        {
            Connected = true,
            Result = new DriveUploadResult(false, null, "https://upload.example/session/1", "اینترنت قطع شد.")
        };
        var service = NewService(db, new FakePostgresTool(), drive);
        await service.UpdateSettingsAsync(SettingsWith(BackupStorageTarget.Both));
        await service.RunAsync(new BackupTriggerRequest(BackupTriggerKind.Manual, "admin", 1));

        drive.Result = new DriveUploadResult(true, "drive-file-1", null, null);
        var uploaded = await service.RetryPendingDriveUploadsAsync();

        Assert.Equal(1, uploaded);
        Assert.Equal("https://upload.example/session/1", drive.LastResumeUri);

        var job = await db.BackupJobs.SingleAsync();
        Assert.Equal(BackupDriveUploadStatus.Uploaded, job.DriveUploadStatus);
        Assert.Equal("drive-file-1", job.DriveFileId);
        Assert.Null(job.DriveResumeUri);
    }

    [Fact]
    public async Task Retention_Removes_Expired_Copies_And_Their_Files()
    {
        await using var db = NewContext();
        var service = NewService(db, new FakePostgresTool());
        await service.UpdateSettingsAsync(SettingsWith(BackupStorageTarget.Server) with { KeepDaily = 1, KeepWeekly = 1, KeepMonthly = 1 });

        // سه نسخهٔ روزانهٔ قدیمی با فایل واقعی روی دیسک. فاصله‌شان ساعتی است تا هر سه
        // قطعاً در همان ماه و همان هفتهٔ اجرای جدید بیفتند و ردهٔ همه «روزانه» بماند.
        Directory.CreateDirectory(StorageDirectory);
        var stalePaths = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            var path = Path.Combine(StorageDirectory, $"stale-{i}.zip");
            await File.WriteAllTextAsync(path, "old");
            stalePaths.Add(path);

            db.BackupJobs.Add(new BackupJob
            {
                StartedAtUtc = DateTime.UtcNow.AddHours(-3 + i),
                CompletedAtUtc = DateTime.UtcNow.AddHours(-3 + i),
                Status = BackupRunStatus.Succeeded,
                RetentionTier = BackupRetentionTier.Daily,
                FileName = $"stale-{i}.zip",
                LocalPath = path,
                SizeBytes = 3
            });
        }
        await db.SaveChangesAsync();

        await service.RunAsync(new BackupTriggerRequest(BackupTriggerKind.Manual, "admin", 1));

        var remainingDaily = await db.BackupJobs.CountAsync(job => job.RetentionTier == BackupRetentionTier.Daily);
        Assert.Equal(1, remainingDaily);
        Assert.All(stalePaths, path => Assert.False(File.Exists(path)));
    }

    [Fact]
    public async Task Deleting_A_Backup_Removes_Both_The_Row_And_The_File()
    {
        await using var db = NewContext();
        var service = NewService(db, new FakePostgresTool());
        await service.RunAsync(new BackupTriggerRequest(BackupTriggerKind.Manual, "admin", 1));

        var job = await db.BackupJobs.SingleAsync();
        var path = job.LocalPath!;

        Assert.True(await service.DeleteJobAsync(job.Id));
        Assert.False(File.Exists(path));
        Assert.Empty(await db.BackupJobs.ToListAsync());
    }

    [Fact]
    public async Task Every_Backup_Operation_Is_Written_To_The_Audit_Log()
    {
        await using var db = NewContext();
        var audit = new RecordingAuditService();
        var service = NewService(db, new FakePostgresTool(), audit: audit);

        await service.RunAsync(new BackupTriggerRequest(BackupTriggerKind.Manual, "admin", 1));

        Assert.Contains("Started", audit.Actions);
        Assert.Contains("Completed", audit.Actions);
        Assert.All(audit.Modules, module => Assert.Equal("Backups", module));
    }

    [Fact]
    public async Task Turning_Automatic_Backup_Off_Clears_The_Next_Run_Time()
    {
        await using var db = NewContext();
        var service = NewService(db, new FakePostgresTool());

        var settings = await service.UpdateSettingsAsync(SettingsWith(BackupStorageTarget.Server) with { AutoBackupEnabled = false });

        Assert.False(settings.AutoBackupEnabled);
        Assert.Null(settings.NextRunAtUtc);
    }

    // ---- helpers ------------------------------------------------------------

    private string StorageDirectory => Path.Combine(_root, "backups");

    private static BackupSettingsInput SettingsWith(BackupStorageTarget target)
        => new(true, BackupSchedule.Daily, 2, 0, target, 7, 4, 6);

    private ApplicationDbContext NewContext()
        => new(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);

    private BackupService NewService(
        ApplicationDbContext db,
        IPostgresBackupTool postgresTool,
        IGoogleDriveBackupClient? drive = null,
        IBackupExecutionLock? executionLock = null,
        IAuditService? audit = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = "super-secret-value",
                ["AllowedHosts"] = "*"
            })
            .Build();

        var options = Options.Create(new BackupOptions
        {
            StorageDirectory = StorageDirectory,
            WorkingDirectory = Path.Combine(_root, "work"),
            TimeZoneId = "Asia/Kabul",
            RestoreEnabled = false
        });

        return new BackupService(
            db,
            postgresTool,
            drive ?? new FakeDriveClient(),
            executionLock ?? new BackupExecutionLock(TimeProvider.System),
            audit ?? new RecordingAuditService(),
            new FakeWebHostEnvironment(_root),
            configuration,
            options,
            TimeProvider.System,
            NullLogger<BackupService>.Instance);
    }

    private static string Sha256OfFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    public void Dispose()
    {
        _connection.Dispose();
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // پاک‌سازی پوشهٔ موقت آزمون حیاتی نیست.
        }
    }

    private sealed class FakePostgresTool : IPostgresBackupTool
    {
        public bool DumpSucceeds { get; init; } = true;
        public bool VerifySucceeds { get; init; } = true;

        public async Task<PostgresToolResult> DumpAsync(string connectionString, string outputFilePath, CancellationToken ct = default)
        {
            if (!DumpSucceeds)
            {
                return new PostgresToolResult(false, 1, "", "pg_dump: connection refused");
            }

            await File.WriteAllTextAsync(outputFilePath, "PGDMP fake custom-format dump payload", Encoding.UTF8, ct);
            return new PostgresToolResult(true, 0, "", "");
        }

        public Task<PostgresToolResult> VerifyDumpAsync(string dumpFilePath, CancellationToken ct = default)
            => Task.FromResult(VerifySucceeds
                ? new PostgresToolResult(true, 0, "; Archive created at 2026-03-10\n2660; TABLE Contracts", "")
                : new PostgresToolResult(false, 1, "", "pg_restore: did not find magic string in file header"));

        public Task<PostgresToolResult> RestoreAsync(string connectionString, string dumpFilePath, CancellationToken ct = default)
            => Task.FromResult(new PostgresToolResult(true, 0, "", ""));
    }

    private sealed class FakeDriveClient : IGoogleDriveBackupClient
    {
        public bool Connected { get; init; }
        public DriveUploadResult Result { get; set; } = new(false, null, null, "Google Drive متصل نیست.");
        public string? LastResumeUri { get; private set; }

        public Task<BackupDriveCredential?> GetCredentialAsync(CancellationToken ct = default)
            => Task.FromResult<BackupDriveCredential?>(Connected
                ? new BackupDriveCredential { ClientId = "client", ProtectedRefreshToken = "token" }
                : null);

        public Task<string> BeginAuthorizationAsync(string clientId, string clientSecret, string redirectUri, string state, CancellationToken ct = default)
            => Task.FromResult("https://accounts.google.com/o/oauth2/v2/auth");

        public Task<(bool Succeeded, string Message)> CompleteAuthorizationAsync(string code, string redirectUri, CancellationToken ct = default)
            => Task.FromResult((true, "ok"));

        public Task DisconnectAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task SetFolderAsync(string? folderId, CancellationToken ct = default) => Task.CompletedTask;

        public Task<DriveUploadResult> UploadBackupAsync(string filePath, string fileName, string? resumeUri, CancellationToken ct = default)
        {
            LastResumeUri = resumeUri;
            return Task.FromResult(Result);
        }
    }

    private sealed class RecordingAuditService : IAuditService
    {
        public List<string> Actions { get; } = [];
        public List<string?> Modules { get; } = [];

        public Task LogAsync(string entityName, int entityId, AuditAction action, int? actorUserId = null, string? diff = null, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task LogAndSaveAsync(string entityName, int entityId, AuditAction action, int? actorUserId = null, string? diff = null, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task LogActivityAsync(AuditLogEntryInput entry, CancellationToken ct = default) => Record(entry);

        public Task LogActivityAndSaveAsync(AuditLogEntryInput entry, CancellationToken ct = default) => Record(entry);

        private Task Record(AuditLogEntryInput entry)
        {
            Actions.Add(entry.Action);
            Modules.Add(entry.Module);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeWebHostEnvironment : IWebHostEnvironment
    {
        public FakeWebHostEnvironment(string root)
        {
            ContentRootPath = root;
            WebRootPath = Path.Combine(root, "wwwroot");
        }

        public string ApplicationName { get; set; } = "PTGOilSystem.Web.Tests";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; }
        public string EnvironmentName { get; set; } = "Testing";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; }
    }
}
