using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Options;
using Npgsql;
using PTGOilSystem.Web.Configuration;

namespace PTGOilSystem.Web.Services.Backups;

/// <summary>
/// پیدا کردن مسیر ابزارهای رسمی PostgreSQL (pg_dump/pg_restore). ترتیب جست‌وجو:
/// ۱) پوشهٔ صریحِ تنظیمات <c>Backup:PostgresToolsDirectory</c>،
/// ۲) مسیرهای نصب متعارف همان سیستم‌عامل (جدیدترین نسخه اول)،
/// ۳) نامِ خالیِ ابزار تا PATH خودش تصمیم بگیرد.
/// نتیجه Cache می‌شود، پس هر بکاپ دیسک را دوباره نمی‌گردد.
/// </summary>
public static class PostgresToolLocator
{
    private static readonly ConcurrentDictionary<string, string> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>نام فایل اجرایی ابزار روی سیستم‌عامل جاری.</summary>
    public static string ExecutableName(string toolName)
        => OperatingSystem.IsWindows() ? toolName + ".exe" : toolName;

    /// <summary>
    /// مسیر قابل اجرا برای ابزار. اگر هیچ نصبی پیدا نشود، خودِ نام ابزار برگردانده
    /// می‌شود تا اجرا از PATH تلاش شود (و در صورت شکست، پیام خطای واضح بدهد).
    /// </summary>
    public static string Resolve(string toolName, string? configuredDirectory)
    {
        var executable = ExecutableName(toolName);
        var key = (configuredDirectory ?? string.Empty) + "|" + executable;
        return Cache.GetOrAdd(key, _ => Locate(executable, configuredDirectory));
    }

    /// <summary>
    /// پوشه‌های نصب متعارف، به ترتیب اولویت. برای ویندوز نصبِ رسمیِ نسخه‌دار
    /// (<c>Program Files\PostgreSQL\18\bin</c>) و برای لینوکس/مک هم چیدمان بسته‌های
    /// Debian/RHEL و هم مسیرهای متعارف دیگر پوشش داده می‌شود.
    /// </summary>
    public static IReadOnlyList<string> ProbeDirectories()
    {
        var directories = new List<string>();

        if (OperatingSystem.IsWindows())
        {
            // ProgramW6432 در پروسهٔ ۳۲بیتی هم پوشهٔ ۶۴بیتی واقعی را می‌دهد.
            foreach (var root in new[]
                     {
                         Environment.GetEnvironmentVariable("ProgramW6432"),
                         Environment.GetEnvironmentVariable("ProgramFiles"),
                         Environment.GetEnvironmentVariable("ProgramFiles(x86)"),
                         @"C:\Program Files\PostgreSQL",
                         @"C:\PostgreSQL"
                     })
            {
                if (string.IsNullOrWhiteSpace(root)) continue;

                var postgresRoot = root.EndsWith("PostgreSQL", StringComparison.OrdinalIgnoreCase)
                    ? root
                    : Path.Combine(root, "PostgreSQL");
                directories.AddRange(VersionedBinDirectories(postgresRoot));
            }

            return Distinct(directories);
        }

        // Debian/Ubuntu: /usr/lib/postgresql/<major>/bin — RHEL: /usr/pgsql-<major>/bin
        directories.AddRange(VersionedBinDirectories("/usr/lib/postgresql"));
        directories.AddRange(VersionedBinDirectories("/usr", "pgsql-"));
        directories.Add("/usr/local/pgsql/bin");
        directories.Add("/opt/homebrew/bin");
        directories.Add("/usr/local/bin");
        directories.Add("/usr/bin");

        return Distinct(directories);
    }

    private static string Locate(string executable, string? configuredDirectory)
    {
        if (!string.IsNullOrWhiteSpace(configuredDirectory))
        {
            var explicitPath = Path.Combine(configuredDirectory, executable);
            if (File.Exists(explicitPath)) return explicitPath;
        }

        foreach (var directory in ProbeDirectories())
        {
            var candidate = Path.Combine(directory, executable);
            if (File.Exists(candidate)) return candidate;
        }

        return executable;
    }

    /// <summary>
    /// زیرپوشه‌های نسخه‌دارِ یک ریشه، از جدیدترین نسخه به قدیمی‌ترین. نام‌هایی که
    /// شمارهٔ نسخه ندارند آخر می‌آیند تا نصب اصلی همیشه اول امتحان شود.
    /// </summary>
    private static IEnumerable<string> VersionedBinDirectories(string root, string? namePrefix = null)
    {
        if (!Directory.Exists(root)) return [];

        try
        {
            return Directory.EnumerateDirectories(root, (namePrefix ?? string.Empty) + "*")
                .OrderByDescending(ParseMajorVersion)
                .Select(directory => Path.Combine(directory, "bin"))
                .ToList();
        }
        catch (Exception)
        {
            // پوشهٔ بدون دسترسی نباید کل جست‌وجو را از کار بیندازد.
            return [];
        }
    }

    private static int ParseMajorVersion(string directoryPath)
    {
        var name = Path.GetFileName(directoryPath);
        var digits = new string(name.SkipWhile(c => !char.IsAsciiDigit(c)).TakeWhile(char.IsAsciiDigit).ToArray());
        return int.TryParse(digits, out var major) ? major : -1;
    }

    private static IReadOnlyList<string> Distinct(IEnumerable<string> directories)
        => directories.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}

public sealed record PostgresToolResult(bool Succeeded, int ExitCode, string StandardOutput, string StandardError)
{
    public string FailureText => string.IsNullOrWhiteSpace(StandardError) ? StandardOutput : StandardError;
}

/// <summary>
/// پوستهٔ نازک روی ابزارهای رسمی PostgreSQL. عمداً از pg_dump/pg_restore استفاده می‌شود،
/// نه از خواندن جدول‌ها با EF: فقط ابزار رسمی می‌تواند یک تصویر سازگار و قابل بازیابیِ
/// کاملِ دیتابیس بسازد.
/// </summary>
public interface IPostgresBackupTool
{
    /// <summary>خروجی با فرمت Custom (‎-Fc‎) که هم فشرده است هم بازیابی گزینشی را ممکن می‌کند.</summary>
    Task<PostgresToolResult> DumpAsync(string connectionString, string outputFilePath, CancellationToken ct = default);

    /// <summary>خواندن فهرست محتویات dump. اگر این کار شکست بخورد، فایل قابل بازیابی نیست.</summary>
    Task<PostgresToolResult> VerifyDumpAsync(string dumpFilePath, CancellationToken ct = default);

    /// <summary>بازیابی کاملِ dump روی همان دیتابیسِ اتصال. برگشت‌ناپذیر است.</summary>
    Task<PostgresToolResult> RestoreAsync(string connectionString, string dumpFilePath, CancellationToken ct = default);
}

public sealed class PostgresBackupTool : IPostgresBackupTool
{
    private readonly BackupOptions _options;
    private readonly ILogger<PostgresBackupTool> _logger;

    public PostgresBackupTool(IOptions<BackupOptions> options, ILogger<PostgresBackupTool> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public Task<PostgresToolResult> DumpAsync(string connectionString, string outputFilePath, CancellationToken ct = default)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        var arguments = new List<string>
        {
            "--format=custom",
            "--no-owner",
            "--no-privileges",
            "--blobs",
            $"--host={builder.Host}",
            $"--port={(builder.Port == 0 ? 5432 : builder.Port)}",
            $"--username={builder.Username}",
            $"--dbname={builder.Database}",
            $"--file={outputFilePath}"
        };

        return RunAsync("pg_dump", arguments, builder.Password, ct);
    }

    public Task<PostgresToolResult> VerifyDumpAsync(string dumpFilePath, CancellationToken ct = default)
        => RunAsync("pg_restore", ["--list", dumpFilePath], password: null, ct);

    public Task<PostgresToolResult> RestoreAsync(string connectionString, string dumpFilePath, CancellationToken ct = default)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        var arguments = new List<string>
        {
            "--clean",
            "--if-exists",
            "--no-owner",
            "--no-privileges",
            "--single-transaction",
            $"--host={builder.Host}",
            $"--port={(builder.Port == 0 ? 5432 : builder.Port)}",
            $"--username={builder.Username}",
            $"--dbname={builder.Database}",
            dumpFilePath
        };

        return RunAsync("pg_restore", arguments, builder.Password, ct);
    }

    private async Task<PostgresToolResult> RunAsync(
        string toolName,
        IReadOnlyList<string> arguments,
        string? password,
        CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ResolveToolPath(toolName),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (!string.IsNullOrEmpty(password))
        {
            // رمز از طریق متغیر محیطیِ همین پروسه می‌رود تا در خط فرمان و لاگ‌ها دیده نشود.
            startInfo.Environment["PGPASSWORD"] = password;
        }

        // پیام‌های ابزار انگلیسی بماند تا تحلیل خطا وابسته به locale سرور نباشد.
        startInfo.Environment["LC_MESSAGES"] = "C";

        using var process = new Process { StartInfo = startInfo };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unable to start {Tool}.", toolName);
            return new PostgresToolResult(
                false,
                -1,
                string.Empty,
                $"اجرای «{toolName}» ممکن نشد. مسیر ابزارهای PostgreSQL را در تنظیمات Backup:PostgresToolsDirectory بررسی کنید. ({ex.Message})");
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(Math.Max(1, _options.DumpTimeoutMinutes)));

        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            var reason = ct.IsCancellationRequested
                ? "عملیات لغو شد."
                : $"اجرای «{toolName}» از سقف {_options.DumpTimeoutMinutes} دقیقه گذشت.";
            return new PostgresToolResult(false, -1, string.Empty, reason);
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        return new PostgresToolResult(process.ExitCode == 0, process.ExitCode, stdout, stderr);
    }

    private string ResolveToolPath(string toolName)
    {
        var resolved = PostgresToolLocator.Resolve(toolName, _options.PostgresToolsDirectory);

        // مسیر پیدا نشد و فقط نام ابزار مانده: اجرا به PATH واگذار می‌شود، پس اگر
        // آنجا هم نباشد باید در لاگ روشن باشد که کجاها گشته‌ایم.
        if (string.Equals(resolved, PostgresToolLocator.ExecutableName(toolName), StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(
                "PostgreSQL tool {Tool} was not found in Backup:PostgresToolsDirectory ({Configured}) or the known install paths ({Probed}); relying on PATH.",
                toolName,
                string.IsNullOrWhiteSpace(_options.PostgresToolsDirectory) ? "<not set>" : _options.PostgresToolsDirectory,
                string.Join(", ", PostgresToolLocator.ProbeDirectories()));
        }
        else
        {
            _logger.LogDebug("Using PostgreSQL tool {Tool} from {Path}.", toolName, resolved);
        }

        return resolved;
    }

    private void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unable to terminate the PostgreSQL tool process.");
        }
    }
}
