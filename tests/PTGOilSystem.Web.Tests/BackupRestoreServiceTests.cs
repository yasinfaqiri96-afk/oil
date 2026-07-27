using System.IO.Compression;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
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
/// بازیابی: چهار سدِ پیش از دست‌زدن به دیتابیس — فعال‌بودن قابلیت، وجود فایل،
/// یکی‌بودن SHA-256 و خواندنی‌بودن dump.
/// </summary>
public sealed class BackupRestoreServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly string _root;

    public BackupRestoreServiceTests()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();
        _root = Path.Combine(Path.GetTempPath(), $"ptg-restore-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_root, "wwwroot"));
        Directory.CreateDirectory(Path.Combine(_root, "backups"));

        using var db = NewContext();
        db.Database.EnsureCreated();
    }

    [Fact]
    public async Task Restore_Is_Refused_While_The_Feature_Is_Disabled_In_Configuration()
    {
        await using var db = NewContext();
        var job = await SeedBackupAsync(db, BuildValidArchive("valid.zip"));
        var service = NewService(db, restoreEnabled: false);

        var result = await service.RestoreAsync(new BackupRestoreRequest(job.Id, "admin", 1, false));

        Assert.False(result.Succeeded);
        Assert.Contains("غیرفعال", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Restore_Is_Refused_When_The_Archive_Is_No_Longer_On_The_Server()
    {
        await using var db = NewContext();
        var path = BuildValidArchive("missing.zip");
        var job = await SeedBackupAsync(db, path);
        File.Delete(path);
        var service = NewService(db, restoreEnabled: true);

        var result = await service.RestoreAsync(new BackupRestoreRequest(job.Id, "admin", 1, false));

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task A_Tampered_Archive_Is_Rejected_Before_The_Database_Is_Touched()
    {
        await using var db = NewContext();
        var path = BuildValidArchive("tampered.zip");
        var job = await SeedBackupAsync(db, path, sha256: new string('0', 64));
        var postgres = new RecordingPostgresTool();
        var service = NewService(db, restoreEnabled: true, postgres: postgres);

        var result = await service.RestoreAsync(new BackupRestoreRequest(job.Id, "admin", 1, false));

        Assert.False(result.Succeeded);
        Assert.Contains("SHA-256", result.Message, StringComparison.Ordinal);
        Assert.False(postgres.RestoreCalled);
    }

    [Fact]
    public async Task An_Archive_Without_A_Manifest_Is_Rejected()
    {
        await using var db = NewContext();
        var path = Path.Combine(_root, "backups", "no-manifest.zip");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            WriteEntry(archive, "database.dump", "PGDMP fake");
        }

        var job = await SeedBackupAsync(db, path);
        var postgres = new RecordingPostgresTool();
        var service = NewService(db, restoreEnabled: true, postgres: postgres);

        var result = await service.RestoreAsync(new BackupRestoreRequest(job.Id, "admin", 1, false));

        Assert.False(result.Succeeded);
        Assert.False(postgres.RestoreCalled);
    }

    [Fact]
    public async Task An_Unreadable_Dump_Stops_The_Restore_Before_pg_restore_Runs()
    {
        await using var db = NewContext();
        var job = await SeedBackupAsync(db, BuildValidArchive("unreadable.zip"));
        var postgres = new RecordingPostgresTool { VerifySucceeds = false };
        var service = NewService(db, restoreEnabled: true, postgres: postgres);

        var result = await service.RestoreAsync(new BackupRestoreRequest(job.Id, "admin", 1, false));

        Assert.False(result.Succeeded);
        Assert.False(postgres.RestoreCalled);
    }

    [Fact]
    public async Task A_Valid_Archive_Restores_The_Database_And_The_Attachments()
    {
        await using var db = NewContext();
        var job = await SeedBackupAsync(db, BuildValidArchive("good.zip", withUpload: true));
        var postgres = new RecordingPostgresTool();
        var service = NewService(db, restoreEnabled: true, postgres: postgres);

        var result = await service.RestoreAsync(new BackupRestoreRequest(job.Id, "admin", 1, RestoreUploads: true));

        Assert.True(result.Succeeded, result.Message);
        Assert.True(postgres.RestoreCalled);
        Assert.True(File.Exists(Path.Combine(_root, "wwwroot", "uploads", "note.txt")));
    }

    [Fact]
    public async Task Restore_Is_Refused_While_A_Backup_Is_Running()
    {
        await using var db = NewContext();
        var job = await SeedBackupAsync(db, BuildValidArchive("busy.zip"));
        var executionLock = new BackupExecutionLock(TimeProvider.System);
        using var held = executionLock.TryAcquire("backup");
        var service = NewService(db, restoreEnabled: true, executionLock: executionLock);

        var result = await service.RestoreAsync(new BackupRestoreRequest(job.Id, "admin", 1, false));

        Assert.False(result.Succeeded);
    }

    // ---- helpers ------------------------------------------------------------

    private ApplicationDbContext NewContext()
        => new(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);

    private string BuildValidArchive(string fileName, bool withUpload = false)
    {
        var path = Path.Combine(_root, "backups", fileName);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);

        WriteEntry(archive, "database.dump", "PGDMP fake custom-format dump payload");
        WriteEntry(archive, "settings.json", "{\"environment\":\"Testing\"}");
        WriteEntry(archive, "manifest.json", new BackupManifest
        {
            CreatedAtUtc = DateTime.UtcNow,
            DatabaseName = "main",
            DatabaseDumpBytes = 36
        }.ToJson());

        if (withUpload)
        {
            WriteEntry(archive, "uploads/note.txt", "attachment");
        }

        return path;
    }

    private static void WriteEntry(ZipArchive archive, string name, string content)
    {
        using var stream = archive.CreateEntry(name).Open();
        var bytes = Encoding.UTF8.GetBytes(content);
        stream.Write(bytes, 0, bytes.Length);
    }

    private static async Task<BackupJob> SeedBackupAsync(ApplicationDbContext db, string path, string? sha256 = null)
    {
        var job = new BackupJob
        {
            StartedAtUtc = DateTime.UtcNow,
            CompletedAtUtc = DateTime.UtcNow,
            Status = BackupRunStatus.Succeeded,
            FileName = Path.GetFileName(path),
            LocalPath = path,
            SizeBytes = new FileInfo(path).Length,
            Sha256 = sha256
        };

        db.BackupJobs.Add(job);
        await db.SaveChangesAsync();
        return job;
    }

    private BackupRestoreService NewService(
        ApplicationDbContext db,
        bool restoreEnabled,
        IPostgresBackupTool? postgres = null,
        IBackupExecutionLock? executionLock = null)
        => new(
            db,
            postgres ?? new RecordingPostgresTool(),
            executionLock ?? new BackupExecutionLock(TimeProvider.System),
            new NoopAuditService(),
            new FakeEnvironment(_root),
            Options.Create(new BackupOptions { RestoreEnabled = restoreEnabled, StorageDirectory = Path.Combine(_root, "backups") }),
            NullLogger<BackupRestoreService>.Instance);

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

    private sealed class RecordingPostgresTool : IPostgresBackupTool
    {
        public bool VerifySucceeds { get; init; } = true;
        public bool RestoreCalled { get; private set; }

        public Task<PostgresToolResult> DumpAsync(string connectionString, string outputFilePath, CancellationToken ct = default)
            => Task.FromResult(new PostgresToolResult(true, 0, "", ""));

        public Task<PostgresToolResult> VerifyDumpAsync(string dumpFilePath, CancellationToken ct = default)
            => Task.FromResult(VerifySucceeds
                ? new PostgresToolResult(true, 0, "2660; TABLE Contracts", "")
                : new PostgresToolResult(false, 1, "", "pg_restore: did not find magic string in file header"));

        public Task<PostgresToolResult> RestoreAsync(string connectionString, string dumpFilePath, CancellationToken ct = default)
        {
            RestoreCalled = true;
            return Task.FromResult(new PostgresToolResult(true, 0, "", ""));
        }
    }

    private sealed class NoopAuditService : IAuditService
    {
        public Task LogAsync(string entityName, int entityId, AuditAction action, int? actorUserId = null, string? diff = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task LogAndSaveAsync(string entityName, int entityId, AuditAction action, int? actorUserId = null, string? diff = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task LogActivityAsync(AuditLogEntryInput entry, CancellationToken ct = default) => Task.CompletedTask;
        public Task LogActivityAndSaveAsync(AuditLogEntryInput entry, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FakeEnvironment : IWebHostEnvironment
    {
        public FakeEnvironment(string root)
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
