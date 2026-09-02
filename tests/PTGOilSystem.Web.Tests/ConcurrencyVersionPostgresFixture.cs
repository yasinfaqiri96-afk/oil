using Microsoft.EntityFrameworkCore;
using Npgsql;
using PTGOilSystem.Web.Data;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// PTG-P1-05 — دیتابیس PostgreSQL موقت برای اثباتِ واقعیِ محافظ هم‌زمانی.
///
/// چرا SQLite کافی نیست: شکستِ قبلیِ <c>xmin</c> فقط روی PostgreSQL واقعی دیده شد
/// (<c>42703: column p.xmin does not exist</c>) و روی SQLite اصلاً بروز نمی‌کرد. پس هر
/// ادعایی دربارهٔ نشانهٔ هم‌زمانی باید روی همان Provider تولید آزموده شود.
///
/// نام دیتابیس با پیشوندِ اجباری <see cref="DatabaseSafetyGuard.AccountingTestDatabasePrefix"/>
/// ساخته می‌شود و همان نگهبانِ پروژه پیش از Create/Use/Drop صدا زده می‌شود.
/// </summary>
[CollectionDefinition(ConcurrencyVersionPostgresCollection.CollectionName, DisableParallelization = true)]
public sealed class ConcurrencyVersionPostgresCollection : ICollectionFixture<ConcurrencyVersionPostgresFixture>
{
    public const string CollectionName = "PTG Concurrency Version PostgreSQL";
}

public sealed class ConcurrencyVersionPostgresFixture : IAsyncLifetime
{
    private readonly string _databaseName =
        $"{DatabaseSafetyGuard.AccountingTestDatabasePrefix}conc_{Guid.NewGuid():N}";

    private bool _created;

    public string ConnectionString { get; private set; } = "";

    /// <summary>در دسترس نبودن PostgreSQL نباید کل Suite را قرمز کند؛ تست‌ها Skip می‌شوند.</summary>
    public bool Available { get; private set; }

    public string? UnavailableReason { get; private set; }

    private static string AdminConnectionString()
    {
        var explicitAdmin = Environment.GetEnvironmentVariable("PTG_TEST_POSTGRES_ADMIN");
        if (!string.IsNullOrWhiteSpace(explicitAdmin))
            return explicitAdmin;

        var localPassword = Environment.GetEnvironmentVariable("PTG_LOCAL_DB_PASSWORD");
        var password = string.IsNullOrWhiteSpace(localPassword) ? "postgres" : localPassword;
        return $"Host=localhost;Port=5432;Username=postgres;Password={password};Database=postgres;" +
               "Timeout=10;Command Timeout=300";
    }

    public async Task InitializeAsync()
    {
        var admin = AdminConnectionString();
        try
        {
            DatabaseSafetyGuard.EnsureIntegrationTestCreateAllowed(_databaseName);

            await using (var connection = new NpgsqlConnection(admin))
            {
                await connection.OpenAsync();
                await using var create = new NpgsqlCommand($"CREATE DATABASE \"{_databaseName}\"", connection);
                await create.ExecuteNonQueryAsync();
                _created = true;
            }

            var builder = new NpgsqlConnectionStringBuilder(admin) { Database = _databaseName };
            ConnectionString = builder.ConnectionString;
            DatabaseSafetyGuard.EnsureIntegrationTestUseAllowed(builder.Database);

            await using (var db = CreateDbContext())
            {
                await db.Database.MigrateAsync();
            }

            Available = true;
        }
        catch (Exception ex)
        {
            UnavailableReason = ex.Message;
            Available = false;
            await DropDatabaseAsync();
        }
    }

    public Task DisposeAsync() => DropDatabaseAsync();

    private async Task DropDatabaseAsync()
    {
        if (!_created)
            return;

        DatabaseSafetyGuard.EnsureIntegrationTestDropAllowed(_databaseName);
        NpgsqlConnection.ClearAllPools();
        await using var connection = new NpgsqlConnection(AdminConnectionString());
        await connection.OpenAsync();
        await using var drop = new NpgsqlCommand(
            $"DROP DATABASE IF EXISTS \"{_databaseName}\" WITH (FORCE)",
            connection);
        await drop.ExecuteNonQueryAsync();
        _created = false;
    }

    /// <summary>هر فراخوانی یک DbContext کاملاً مستقل می‌دهد — لازمهٔ آزمونِ دو کاربرِ هم‌زمان.</summary>
    public ApplicationDbContext CreateDbContext()
        => new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(ConnectionString)
            .EnableSensitiveDataLogging()
            .Options);
}
