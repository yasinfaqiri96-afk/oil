using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using PTGOilSystem.Web.Data;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// دیتابیس PostgreSQL موقت برای اندازه‌گیری واقعیِ مسیر «تبدیل گروهی بارگیری به حمل».
///
/// چرا SQLite کافی نیست: این مسیر <c>Sum</c> روی <c>decimal</c> می‌زند که SQLite ترجمه نمی‌کند،
/// و قفل <c>SELECT … FOR UPDATE</c> و تراکنش Serializable فقط روی همان Provider تولید معنا دارد.
/// نام دیتابیس با همان پیشوند اجباریِ پروژه ساخته و در پایان drop می‌شود.
/// </summary>
[CollectionDefinition(BulkFromLoadingPerformanceCollection.CollectionName, DisableParallelization = true)]
public sealed class BulkFromLoadingPerformanceCollection
    : ICollectionFixture<BulkFromLoadingPerformanceFixture>
{
    public const string CollectionName = "PTG Bulk From Loading Performance";
}

public sealed class BulkFromLoadingPerformanceFixture : IAsyncLifetime
{
    private readonly string _databaseName =
        $"{DatabaseSafetyGuard.AccountingTestDatabasePrefix}bulkperf_{Guid.NewGuid():N}";

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

    public ApplicationDbContext CreateDbContext(params IInterceptor[] interceptors)
        => new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(ConnectionString)
            .AddInterceptors(interceptors)
            .Options);

    /// <summary>هر اندازه‌گیری روی دادهٔ تمیز اجرا می‌شود.</summary>
    public async Task TruncateAsync()
    {
        await using var db = CreateDbContext();
        await db.Database.ExecuteSqlRawAsync(
            """
            TRUNCATE "InventoryTransportLegAllocations", "InventoryTransportLegs",
                     "InventoryTransportBatches", "LoadingReceipts", "LossEvents",
                     "ProcessedFormTokens", "LoadingRegisters", "Contracts",
                     "Trucks", "Terminals", "Products", "Suppliers", "Companies"
            RESTART IDENTITY CASCADE;
            """);
    }
}
