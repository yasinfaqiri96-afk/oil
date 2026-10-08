using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using PTGOilSystem.Web.Data;
using Xunit;

namespace PTGOilSystem.Web.Tests;

public sealed class MigrationAssemblyCompatibilityTests
{
    private static ApplicationDbContext CreateContext(string? assemblyOverride = null)
        => new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql("Host=localhost;Database=ptg_model_only;Username=unused;Password=unused",
                options => { if (assemblyOverride is not null) options.MigrationsAssembly(assemblyOverride); })
            .Options);

    [Fact]
    public void Runtime_Discovery_Includes_Every_Source_Migration_In_Order_And_The_Snapshot()
    {
        // No connection is opened and no migration is applied.
        using var context = CreateContext();
        var assembly = context.GetService<IMigrationsAssembly>();
        Assert.Equal("PTGOilSystem.Persistence", typeof(ApplicationDbContext).Assembly.GetName().Name);
        Assert.Equal(ApplicationDbContext.MigrationsAssemblyName, assembly.Assembly.GetName().Name);
        Assert.NotNull(assembly.ModelSnapshot);
        Assert.Equal(assembly.Assembly, assembly.ModelSnapshot!.GetType().Assembly);

        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "ptg-oil-system.sln"))) root = root.Parent;
        Assert.NotNull(root);
        var sourceIds = Directory.EnumerateFiles(
                Path.Combine(root!.FullName, "src", "PTGOilSystem.Migrations", "Migrations"), "*.cs")
            .SelectMany(path => Regex.Matches(File.ReadAllText(path), "\\[Migration\\(\"([^\"]+)\"\\)\\]")
                .Select(match => match.Groups[1].Value))
            .OrderBy(id => id, StringComparer.Ordinal).ToArray();
        Assert.NotEmpty(sourceIds);
        Assert.Equal(sourceIds, assembly.Migrations.Keys.ToArray());
        Assert.False(context.Database.HasPendingModelChanges());
    }

    [Fact]
    public void Explicit_Migration_Assembly_Override_Is_Preserved()
    {
        using var context = CreateContext("PTGOilSystem.Web.Tests");
        Assert.Equal("PTGOilSystem.Web.Tests", context.GetService<IMigrationsAssembly>().Assembly.GetName().Name);
    }

    [Fact]
    public void Sqlite_Context_Keeps_Its_Original_Provider()
    {
        using var context = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite("Data Source=:memory:").Options);
        Assert.Equal("Microsoft.EntityFrameworkCore.Sqlite", context.Database.ProviderName);
        Assert.False(context.Database.IsNpgsql());
    }

    [Fact]
    public void InMemory_Context_Does_Not_Acquire_A_Relational_Provider()
    {
        using var context = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        Assert.False(context.Database.IsRelational());
        Assert.NotEmpty(context.Model.GetEntityTypes());
    }
}
