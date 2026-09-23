using System.Text.Json;
using Xunit;

namespace PTGOilSystem.Web.Tests;

public sealed class TestExecutionInfrastructureTests
{
    [Fact]
    public void Xunit_Runner_Limits_Normal_Collection_Parallelism()
    {
        using var config = JsonDocument.Parse(ReadRepoFile(
            "tests", "PTGOilSystem.Web.Tests", "xunit.runner.json"));
        var root = config.RootElement;

        Assert.Equal(6, root.GetProperty("maxParallelThreads").GetInt32());
        Assert.Equal("conservative", root.GetProperty("parallelAlgorithm").GetString());
        Assert.True(root.GetProperty("parallelizeTestCollections").GetBoolean());

        var project = ReadRepoFile(
            "tests", "PTGOilSystem.Web.Tests", "PTGOilSystem.Web.Tests.csproj");
        Assert.Contains("<None Update=\"xunit.runner.json\"", project);
        Assert.Contains("CopyToOutputDirectory=\"PreserveNewest\"", project);
    }

    [Fact]
    public void Test_Scripts_Reuse_The_Existing_Build()
    {
        foreach (var scriptName in new[] { "test-fast.ps1", "test-full.ps1", "test-accounting.ps1" })
        {
            var script = ReadRepoFile("scripts", scriptName);
            var testCommand = Assert.Single(
                script.Split('\n').Where(line => line.TrimStart().StartsWith("dotnet test ", StringComparison.Ordinal)));

            Assert.Contains("--no-build", testCommand);
            Assert.Contains("--no-restore", testCommand);
        }
    }

    [Fact]
    public void Fast_Excludes_Heavy_Categories_And_Full_Remains_Unfiltered()
    {
        var fast = ReadRepoFile("scripts", "test-fast.ps1");
        var full = ReadRepoFile("scripts", "test-full.ps1");

        Assert.Contains("Category!=PostgreSql", fast);
        Assert.Contains("Category!=Integration", fast);
        Assert.Contains("Category!=Simulation", fast);
        Assert.Contains("Category!=Performance", fast);
        Assert.DoesNotContain("--filter", full);
    }

    private static string ReadRepoFile(params string[] relativeSegments)
    {
        var segments = new List<string> { AppContext.BaseDirectory, "..", "..", "..", "..", ".." };
        segments.AddRange(relativeSegments);
        return File.ReadAllText(Path.GetFullPath(Path.Combine([.. segments])));
    }
}
