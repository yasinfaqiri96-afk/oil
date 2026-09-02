using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Microsoft.Extensions.Logging.Abstractions;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Security;
using PTGOilSystem.Web.Services;
using Xunit;
using Xunit.Abstractions;

namespace PTGOilSystem.Web.Tests.Simulation;

/// <summary>
/// ابزار توسعه‌دهنده: یک دیتابیس «بازرسی دستی» ماندگار می‌سازد و با همان دادهٔ
/// deterministic دوازده‌ماههٔ <see cref="SimulationWorld"/> پر می‌کند تا بتوان آن را
/// در UI واقعی PTG و pgAdmin باز کرد.
///
/// این کلاس عمداً از <see cref="SimulationPostgresFixture"/> استفاده نمی‌کند، چون آن
/// fixture در پایان دیتابیس را Drop می‌کند. رفتار تست‌ها دست‌نخورده می‌ماند: آن‌ها
/// همچنان دیتابیس موقتی خود را پاک می‌کنند و اینجا هیچ Drop خودکاری وجود ندارد.
///
/// اجرا فقط با درخواست صریح:
///   PTG_INSPECTION_SEED=1 dotnet test --filter FullyQualifiedName~InspectionDatabaseSeeder
/// متغیرهای اختیاری:
///   PTG_INSPECTION_DATABASE  نام دیتابیس (پیش‌فرض ptg_oil_accounting_test_12month_inspection)
///   PTG_INSPECTION_RESET=1   اگر دیتابیس از قبل هست، دوباره از صفر ساخته شود
/// </summary>
public sealed class InspectionDatabaseSeeder
{
    public const string DefaultDatabaseName =
        DatabaseSafetyGuard.AccountingTestDatabasePrefix + "12month_inspection";

    private readonly ITestOutputHelper _output;

    public InspectionDatabaseSeeder(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task Seed_Persistent_TwelveMonth_Inspection_Database()
    {
        var requested = Environment.GetEnvironmentVariable("PTG_INSPECTION_SEED");
        if (!string.Equals(requested, "1", StringComparison.Ordinal)
            && !string.Equals(requested, "true", StringComparison.OrdinalIgnoreCase))
        {
            // در اجرای عادی تست‌ها این ابزار هیچ کاری نمی‌کند.
            _output.WriteLine("Inspection seeding is opt-in. Set PTG_INSPECTION_SEED=1 to run it.");
            return;
        }

        var databaseName = Environment.GetEnvironmentVariable("PTG_INSPECTION_DATABASE");
        if (string.IsNullOrWhiteSpace(databaseName))
            databaseName = DefaultDatabaseName;
        databaseName = databaseName.Trim();

        // همان نگهبان پروژه: نام باید با پیشوند دیتابیس تستی شروع شود، پس هرگز
        // نمی‌توان این ابزار را به ptg_oil_system (Production) وصل کرد.
        DatabaseSafetyGuard.EnsureIntegrationTestCreateAllowed(databaseName);
        DatabaseSafetyGuard.EnsureIntegrationTestUseAllowed(databaseName);

        var admin = AdminConnectionString();
        var builder = new NpgsqlConnectionStringBuilder(admin) { Database = databaseName };
        var connectionString = builder.ConnectionString;

        var reset = string.Equals(
            Environment.GetEnvironmentVariable("PTG_INSPECTION_RESET"), "1", StringComparison.Ordinal);

        var existed = await DatabaseExistsAsync(admin, databaseName);
        if (existed && reset)
        {
            // Drop هم از همان نگهبان رد می‌شود و فقط روی نام‌های پیشونددار مجاز است.
            DatabaseSafetyGuard.EnsureIntegrationTestDropAllowed(databaseName);
            NpgsqlConnection.ClearAllPools();
            await ExecuteAdminAsync(admin, $"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)");
            existed = false;
            _output.WriteLine($"Existing inspection database dropped for a clean reseed: {databaseName}");
        }

        var freshlyCreated = false;
        if (!existed)
        {
            await ExecuteAdminAsync(admin, $"CREATE DATABASE \"{databaseName}\"");
            freshlyCreated = true;
            _output.WriteLine($"Inspection database created: {databaseName}");
        }
        else
        {
            _output.WriteLine(
                $"Inspection database already exists and was kept as-is: {databaseName} " +
                "(set PTG_INSPECTION_RESET=1 to rebuild it from scratch).");
        }

        var log = new SimulationFindingLog();
        log.Fact($"Inspection database: {databaseName}");

        // زنجیرهٔ کامل Migration فعلی.
        await using (var db = CreateDbContext(connectionString))
        {
            db.Database.SetCommandTimeout(600);
            await db.Database.MigrateAsync();
            var applied = (await db.Database.GetAppliedMigrationsAsync()).Count();
            log.Fact($"Applied migrations: {applied}");
            _output.WriteLine($"Migrations applied: {applied}");
        }

        if (freshlyCreated)
        {
            var world = new SimulationWorld();
            var stopwatch = Stopwatch.StartNew();

            await using (var db = CreateDbContext(connectionString))
            {
                db.Database.SetCommandTimeout(600);
                await world.SeedMasterDataAsync(db);
                await world.SeedContractsAsync(db);
            }

            await using (var db = CreateDbContext(connectionString))
            {
                db.Database.SetCommandTimeout(600);
                await world.RunTwelveMonthsAsync(db);
            }

            stopwatch.Stop();
            log.Fact($"12-month deterministic generation took {stopwatch.Elapsed.TotalSeconds:N1}s.");
            _output.WriteLine($"Seeding finished in {stopwatch.Elapsed.TotalSeconds:N1}s.");
            Assert.NotEmpty(world.Volumes);
        }
        else
        {
            log.Fact("Seeding skipped: database already existed (data left untouched).");
        }

        await using (var db = CreateDbContext(connectionString))
        {
            db.Database.SetCommandTimeout(600);
            await EnsureInspectionAdminAsync(db, log);
            await TwelveMonthProductionSimulationTests.RunAllScannersAsync(db, log);
            await ReportMasterDataCountsAsync(db, log);
        }

        var path = log.WriteToDisk(
            "inspection-database-12-month.md",
            $"PTG inspection database — {databaseName}");

        _output.WriteLine(log.Render($"PTG inspection database — {databaseName}"));
        _output.WriteLine($"Report written to: {path}");
        _output.WriteLine($"Connection string: {connectionString}");
        _output.WriteLine("INSPECTION DATABASE READY (not dropped).");
    }

    /// <summary>
    /// دادهٔ شبیه‌سازی کاربران خودش را با نقش Operator می‌سازد، پس هیچ کاربر Admin
    /// برای بازرسی وجود ندارد. اینجا فقط یک کاربر Admin با رمز مشخص اضافه می‌شود
    /// (با همان UserService و AuthBootstrapper خودِ برنامه) تا بتوان همهٔ منوها را دید.
    /// هیچ قاعدهٔ کسب‌وکاری شبیه‌سازی تغییر نمی‌کند.
    /// </summary>
    private static async Task EnsureInspectionAdminAsync(ApplicationDbContext db, SimulationFindingLog log)
    {
        var username = Environment.GetEnvironmentVariable("PTG_INSPECTION_ADMIN_USERNAME");
        if (string.IsNullOrWhiteSpace(username))
            username = "inspector";
        username = username.Trim();

        var password = Environment.GetEnvironmentVariable("PTG_INSPECTION_ADMIN_PASSWORD");
        if (string.IsNullOrWhiteSpace(password))
            password = "PtgInspect2026!";

        var users = new UserService(db);
        var bootstrapper = new AuthBootstrapper(db, users, NullLogger<AuthBootstrapper>.Instance);
        await bootstrapper.EnsureDefaultRolesAsync();

        var adminRole = await db.Roles.AsNoTracking().SingleAsync(r => r.Name == AuthRoles.Admin);

        var existing = await db.Users.SingleOrDefaultAsync(u => u.Username == username);
        if (existing is null)
        {
            await users.CreateUserAsync(username, "Inspection Admin", password, adminRole.Id);
            log.Fact($"Inspection admin user created: {username}");
            return;
        }

        existing.PasswordHash = users.HashPassword(password);
        existing.RoleId = adminRole.Id;
        existing.IsActive = true;
        await db.SaveChangesAsync();
        log.Fact($"Inspection admin user refreshed: {username}");
    }

    private static async Task ReportMasterDataCountsAsync(ApplicationDbContext db, SimulationFindingLog log)
    {
        log.Fact($"ContractPartners: {await db.ContractPartners.CountAsync()}");
        log.Fact($"Suppliers: {await db.Suppliers.CountAsync()}");
        log.Fact($"Customers: {await db.Customers.CountAsync()}");
        log.Fact($"Partners: {await db.Partners.CountAsync()}");
        log.Fact($"StorageTanks: {await db.StorageTanks.CountAsync()}");
    }

    private static ApplicationDbContext CreateDbContext(string connectionString)
        => new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(connectionString)
            .Options);

    private static string AdminConnectionString()
    {
        var explicitAdmin = Environment.GetEnvironmentVariable("PTG_TEST_POSTGRES_ADMIN");
        if (!string.IsNullOrWhiteSpace(explicitAdmin))
            return explicitAdmin;

        var localPassword = Environment.GetEnvironmentVariable("PTG_LOCAL_DB_PASSWORD");
        var password = string.IsNullOrWhiteSpace(localPassword) ? "postgres" : localPassword;
        return $"Host=localhost;Port=5432;Username=postgres;Password={password};Database=postgres;" +
               "Timeout=10;Command Timeout=600";
    }

    private static async Task<bool> DatabaseExistsAsync(string admin, string databaseName)
    {
        await using var connection = new NpgsqlConnection(admin);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT 1 FROM pg_database WHERE datname = @name", connection);
        command.Parameters.AddWithValue("name", databaseName);
        return await command.ExecuteScalarAsync() is not null;
    }

    private static async Task ExecuteAdminAsync(string admin, string sql)
    {
        await using var connection = new NpgsqlConnection(admin);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
