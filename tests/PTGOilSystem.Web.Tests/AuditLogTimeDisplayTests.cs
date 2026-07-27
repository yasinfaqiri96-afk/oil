using System.Runtime.CompilerServices;
using PTGOilSystem.Web.Helpers;
using Xunit;

namespace PTGOilSystem.Web.Tests;

public sealed class AuditLogTimeDisplayTests
{
    [Fact]
    public void AfghanistanDisplayDateTime_ConvertsUtcToKabulTime()
    {
        var utc = new DateTime(2026, 7, 26, 12, 0, 0, DateTimeKind.Utc);

        var displayed = utc.ToAfghanistanDisplayDateTime().Replace("\u200E", string.Empty);

        Assert.Equal("2026/07/26 16:30", displayed);
    }

    [Fact]
    public void AuditLogViews_UseAfghanistanTimeForEveryTimestamp()
    {
        var index = ReadRepoFile("src/PTGOilSystem.Web/Views/AuditLogs/Index.cshtml");
        var details = ReadRepoFile("src/PTGOilSystem.Web/Views/AuditLogs/Details.cshtml");

        Assert.Contains("item.ActionAtUtc.ToAfghanistanDisplayDateTime()", index);
        Assert.Contains("Model.ActionAtUtc.ToAfghanistanDisplayDateTime()", details);
        Assert.Contains("Model.CreatedAtUtc.ToAfghanistanDisplayDateTime()", details);
        Assert.Contains("Model.UpdatedAtUtc.ToAfghanistanDisplayDateTime()", details);
        Assert.DoesNotContain("ToDisplayDateTime()", index);
        Assert.DoesNotContain("ToDisplayDateTime()", details);
    }

    private static string ReadRepoFile(string relativePath)
        => File.ReadAllText(GetRepoPath(relativePath));

    private static string GetRepoPath(
        string relativePath,
        [CallerFilePath] string sourceFilePath = "")
    {
        var normalizedPath = relativePath.Replace('/', Path.DirectorySeparatorChar);
        var directory = new DirectoryInfo(Path.GetDirectoryName(sourceFilePath)!);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, normalizedPath);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Repository file not found: {relativePath}");
    }
}
