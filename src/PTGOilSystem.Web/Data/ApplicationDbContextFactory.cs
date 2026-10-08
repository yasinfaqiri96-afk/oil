using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Npgsql;

namespace PTGOilSystem.Web.Data;

public sealed class ApplicationDbContextFactory : IDesignTimeDbContextFactory<ApplicationDbContext>
{
    public ApplicationDbContext CreateDbContext(string[] args)
    {
        var rawConnectionString =
            Environment.GetEnvironmentVariable("DATABASE_URL")
            ?? Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection")
            ?? "Host=localhost;Port=5432;Username=postgres;Password=postgres;Database=ptg_oil_system;SSL Mode=Prefer;Trust Server Certificate=true";

        var connectionString = BuildPostgresConnectionString(rawConnectionString);
        var databaseName = new NpgsqlConnectionStringBuilder(connectionString).Database;
        DatabaseSafetyGuard.EnsureMigrationAllowed(databaseName);

        var optionsBuilder = new DbContextOptionsBuilder<ApplicationDbContext>();
        optionsBuilder.UseNpgsql(connectionString,
            npgsql => npgsql.MigrationsAssembly(ApplicationDbContext.MigrationsAssemblyName));
        return new ApplicationDbContext(optionsBuilder.Options);
    }

    private static string BuildPostgresConnectionString(string raw)
    {
        if (raw.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase)
            || raw.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
        {
            var uri = new Uri(raw);
            var userInfo = uri.UserInfo.Split(':', 2);
            var username = Uri.UnescapeDataString(userInfo[0]);
            var password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : string.Empty;
            var database = uri.AbsolutePath.TrimStart('/');
            var port = uri.Port > 0 ? uri.Port : 5432;
            return $"Host={uri.Host};Port={port};Username={username};Password={password};Database={database};{DbTlsSettings()}";
        }

        return raw;
    }

    // TLS hardening is opt-in via PTG_DB_STRICT_TLS=true so the default design-time
    // connection keeps working unless strict TLS is explicitly enabled.
    private static string DbTlsSettings()
    {
        var strict = string.Equals(
            Environment.GetEnvironmentVariable("PTG_DB_STRICT_TLS"),
            "true",
            StringComparison.OrdinalIgnoreCase);
        return strict
            ? "SSL Mode=Require;Trust Server Certificate=false"
            : "SSL Mode=Prefer;Trust Server Certificate=true";
    }
}
