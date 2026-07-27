using PTGOilSystem.Web.Services.Backups;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// قرارداد پیدا کردن pg_dump/pg_restore. ریشهٔ باگی که این کلاس برایش ساخته شد:
/// وقتی Backup:PostgresToolsDirectory خالی بود و ابزار روی PATH نبود، بکاپ با
/// «اجرای pg_dump ممکن نشد» شکست می‌خورد، حتی وقتی PostgreSQL نصب بود.
/// </summary>
public class PostgresToolLocatorTests
{
    [Fact]
    public void An_Explicit_Directory_Wins_When_The_Executable_Is_There()
    {
        var directory = CreateToolDirectory(out var expectedPath);

        try
        {
            Assert.Equal(expectedPath, PostgresToolLocator.Resolve("pg_dump", directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void A_Wrong_Explicit_Directory_Never_Produces_A_Path_Inside_It()
    {
        var missing = Path.Combine(Path.GetTempPath(), "ptg-postgres-missing-" + Guid.NewGuid().ToString("N"));

        var resolved = PostgresToolLocator.Resolve("pg_dump", missing);

        // نتیجه یا یک نصب واقعی است یا نامِ خالی برای PATH — هرگز مسیر ناموجود.
        Assert.DoesNotContain(missing, resolved, StringComparison.OrdinalIgnoreCase);
        AssertUsablePath(resolved, "pg_dump");
    }

    [Fact]
    public void Resolving_Without_Configuration_Falls_Back_To_An_Install_Path_Or_PATH()
    {
        AssertUsablePath(PostgresToolLocator.Resolve("pg_dump", null), "pg_dump");
        AssertUsablePath(PostgresToolLocator.Resolve("pg_restore", ""), "pg_restore");
    }

    [Fact]
    public void The_Probe_List_Is_Distinct_And_Points_At_Tool_Directories()
    {
        var directories = PostgresToolLocator.ProbeDirectories();

        Assert.NotEmpty(directories);
        Assert.Equal(directories.Count, directories.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(directories, directory => Assert.False(string.IsNullOrWhiteSpace(directory)));

        if (OperatingSystem.IsWindows())
        {
            // روی ویندوز فقط نصب رسمیِ نسخه‌دار جست‌وجو می‌شود، نه پوشه‌های دلخواه.
            Assert.All(directories, directory =>
                Assert.Contains("PostgreSQL", directory, StringComparison.OrdinalIgnoreCase));
        }

        Assert.All(directories, directory =>
            Assert.True(
                directory.EndsWith("bin", StringComparison.OrdinalIgnoreCase)
                || directory.EndsWith("bin" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
                $"Probe entry is not a tools directory: {directory}"));
    }

    [Fact]
    public void The_Executable_Name_Matches_The_Current_Platform()
        => Assert.Equal(
            OperatingSystem.IsWindows() ? "pg_dump.exe" : "pg_dump",
            PostgresToolLocator.ExecutableName("pg_dump"));

    private static void AssertUsablePath(string resolved, string toolName)
    {
        var executable = PostgresToolLocator.ExecutableName(toolName);
        Assert.True(
            string.Equals(resolved, executable, StringComparison.OrdinalIgnoreCase) || File.Exists(resolved),
            $"Resolved path is neither an existing file nor the bare executable name: {resolved}");
    }

    private static string CreateToolDirectory(out string executablePath)
    {
        var directory = Path.Combine(Path.GetTempPath(), "ptg-postgres-tools-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        executablePath = Path.Combine(directory, PostgresToolLocator.ExecutableName("pg_dump"));
        File.WriteAllText(executablePath, string.Empty);
        return directory;
    }
}
