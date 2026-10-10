using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services;
using Xunit;

namespace PTGOilSystem.Web.Tests;

[Collection(AccountingPostgreSqlCollection.CollectionName)]
[Trait("Category", "PostgreSql")]
[Trait("Category", "Integration")]
public sealed class SupplierAllocationFxMigrationTests
{
    private const string PreviousMigration = "20261008131945_WidenExpenseAndJournalFxRatePrecision";
    private const string PrecisionMigration = "20261009235000_WidenSupplierAllocationFxPrecision";
    private static readonly string[] WidenedColumns =
    [ "ContractCurrencyPerUsdRate", "ContractCurrencyFxRateToUsd",
      "PaymentCurrencyPerUsdRateAtAllocation", "PaymentCurrencyFxRateToUsdAtAllocation" ];

    [Fact]
    public void Current_Model_Matches_The_Migration_Snapshot()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql("Host=localhost;Database=ptg_accountingtest_model;Username=unused;Password=unused").Options;
        using var db = new ApplicationDbContext(options);
        Assert.False(db.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task Up_Preserves_Historical_Values_And_Down_Rejects_Lossy_Rates_Without_Truncating()
    {
        await using var isolated = await IsolatedDatabase.CreateAsync();
        await using var db = isolated.CreateContext();
        var migrator = db.Database.GetService<IMigrator>();
        await migrator.MigrateAsync(PreviousMigration);

        var company = new Company { Code = "FX-MIG", Name = "FX migration test" };
        var supplier = new Supplier { Name = "FX migration supplier" };
        var product = new Product { Code = "FX-MIG", Name = "Fuel", UnitOfMeasure = "MT" };
        db.AddRange(company, supplier, product);
        await db.SaveChangesAsync();
        var contract = new Contract { CompanyId = company.Id, SupplierId = supplier.Id, ProductId = product.Id,
            ContractNumber = "FX-MIG-1", ContractType = ContractType.Purchase, Status = ContractStatus.Active,
            ContractDate = new DateTime(2026, 7, 1), Currency = "RUB", QuantityMt = 100m,
            PricingMethod = PricingMethod.Fixed, UnitPriceUsd = 500m };
        db.Contracts.Add(contract); await db.SaveChangesAsync();
        var payment = new PaymentTransaction { CompanyId = company.Id, SupplierId = supplier.Id,
            PaymentKind = PaymentKind.SupplierPayment, Direction = PaymentDirection.Out,
            PaymentDate = new DateTime(2026, 7, 1), Amount = 1000000m, AmountUsd = 1000000m,
            Currency = "USD", AppliedFxRateToUsd = 1m };
        db.PaymentTransactions.Add(payment); await db.SaveChangesAsync();
        var historical = new SupplierPaymentAllocation {
            PaymentTransactionId = payment.Id, ContractId = contract.Id, AllocationDate = new DateTime(2026, 7, 2),
            AllocatedPaymentAmount = 1000m, PaymentCurrencyCode = "USD", PaymentFxRateToUsd = 1m,
            AllocatedBookAmountUsd = 1000m, AllocatedValueUsdAtAllocation = 1000m,
            PaymentCurrencyPerUsdRateAtAllocation = 1m, PaymentCurrencyFxRateToUsdAtAllocation = 1m,
            ContractCurrencyCode = "RUB", ContractCurrencyPerUsdRate = 80m,
            ContractCurrencyFxRateToUsd = 0.0125m, AllocatedContractCurrencyAmount = 80000m,
            Status = SupplierPaymentAllocationStatus.Active };
        db.SupplierPaymentAllocations.Add(historical); await db.SaveChangesAsync();
        var before = await db.SupplierPaymentAllocations.AsNoTracking().SingleAsync();

        await migrator.MigrateAsync(PrecisionMigration);
        db.ChangeTracker.Clear();
        var afterUp = await db.SupplierPaymentAllocations.AsNoTracking().SingleAsync();
        AssertHistoricalUnchanged(before, afterUp);
        await AssertColumnScalesAsync(isolated.ConnectionString, 12);

        var service = new SupplierPaymentAllocationService(db);
        var fresh = await service.CreateAsync(new SupplierPaymentAllocationCreateRequest(
            payment.Id, contract.Id, new DateTime(2026, 7, 3), 900000m, 77m, null, null, "migration test"));
        db.ChangeTracker.Clear();
        var precise = await db.SupplierPaymentAllocations.AsNoTracking().SingleAsync(a => a.Id == fresh.Id);
        Assert.Equal(0.012987012987m, precise.ContractCurrencyFxRateToUsd);
        Assert.Equal(900000m, decimal.Round(precise.AllocatedContractCurrencyAmount
            * precise.ContractCurrencyFxRateToUsd, 4, MidpointRounding.AwayFromZero));
        var error = await Assert.ThrowsAsync<PostgresException>(() => migrator.MigrateAsync(PreviousMigration));
        Assert.Equal("P0001", error.SqlState);
        Assert.Contains("without losing historical rates", error.MessageText);
        await AssertColumnScalesAsync(isolated.ConnectionString, 12);
        Assert.Contains(PrecisionMigration, await db.Database.GetAppliedMigrationsAsync());
        var afterRejectedDown = await db.SupplierPaymentAllocations.AsNoTracking().SingleAsync(a => a.Id == fresh.Id);
        Assert.Equal(precise.ContractCurrencyFxRateToUsd, afterRejectedDown.ContractCurrencyFxRateToUsd);
        Assert.Equal(precise.AllocatedContractCurrencyAmount, afterRejectedDown.AllocatedContractCurrencyAmount);
        AssertHistoricalUnchanged(before, await db.SupplierPaymentAllocations.AsNoTracking().SingleAsync(a => a.Id == before.Id));

        // Only the extra seed created inside this isolated test database is removed. The
        // real rollback case with representable historical rows must still work and keep them.
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"SupplierPaymentAllocations\" WHERE \"Id\" = {fresh.Id}");
        await migrator.MigrateAsync(PreviousMigration);
        await AssertColumnScalesAsync(isolated.ConnectionString, 6);
        AssertHistoricalUnchanged(before, await db.SupplierPaymentAllocations.AsNoTracking().SingleAsync());
        Assert.DoesNotContain(PrecisionMigration, await db.Database.GetAppliedMigrationsAsync());
    }

    private static void AssertHistoricalUnchanged(SupplierPaymentAllocation expected, SupplierPaymentAllocation actual)
    {
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.ContractCurrencyPerUsdRate, actual.ContractCurrencyPerUsdRate);
        Assert.Equal(expected.ContractCurrencyFxRateToUsd, actual.ContractCurrencyFxRateToUsd);
        Assert.Equal(expected.PaymentCurrencyPerUsdRateAtAllocation, actual.PaymentCurrencyPerUsdRateAtAllocation);
        Assert.Equal(expected.PaymentCurrencyFxRateToUsdAtAllocation, actual.PaymentCurrencyFxRateToUsdAtAllocation);
        Assert.Equal(expected.PaymentFxRateToUsd, actual.PaymentFxRateToUsd);
        Assert.Equal(expected.AllocatedPaymentAmount, actual.AllocatedPaymentAmount);
        Assert.Equal(expected.AllocatedBookAmountUsd, actual.AllocatedBookAmountUsd);
        Assert.Equal(expected.AllocatedValueUsdAtAllocation, actual.AllocatedValueUsdAtAllocation);
        Assert.Equal(expected.AllocatedContractCurrencyAmount, actual.AllocatedContractCurrencyAmount);
        Assert.Equal(expected.ExchangeDifferenceUsd, actual.ExchangeDifferenceUsd);
        Assert.Equal(expected.Status, actual.Status);
    }

    private static async Task AssertColumnScalesAsync(string connectionString, int expected)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        foreach (var column in WidenedColumns)
        {
            await using var command = new NpgsqlCommand("""
                SELECT numeric_scale FROM information_schema.columns
                WHERE table_schema = 'public' AND table_name = 'SupplierPaymentAllocations' AND column_name = @column
                """, connection);
            command.Parameters.AddWithValue("column", column);
            Assert.Equal(expected, Convert.ToInt32(await command.ExecuteScalarAsync()));
        }
    }

    private sealed class IsolatedDatabase(string adminConnectionString, string databaseName, string connectionString)
        : IAsyncDisposable
    {
        public string ConnectionString { get; } = connectionString;
        public ApplicationDbContext CreateContext() => new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(ConnectionString).Options);

        public static async Task<IsolatedDatabase> CreateAsync()
        {
            var adminString = Environment.GetEnvironmentVariable("PTG_TEST_POSTGRES_ADMIN")
                ?? "Host=localhost;Username=postgres;Password=postgres;Database=postgres";
            var name = DatabaseSafetyGuard.AccountingTestDatabasePrefix + "fxmig_" + Guid.NewGuid().ToString("N");
            DatabaseSafetyGuard.EnsureIntegrationTestCreateAllowed(name);
            await using var admin = new NpgsqlConnection(adminString);
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", admin);
            await create.ExecuteNonQueryAsync();
            var target = new NpgsqlConnectionStringBuilder(adminString) { Database = name };
            DatabaseSafetyGuard.EnsureIntegrationTestUseAllowed(name);
            return new IsolatedDatabase(adminString, name, target.ConnectionString);
        }

        public async ValueTask DisposeAsync()
        {
            DatabaseSafetyGuard.EnsureIntegrationTestDropAllowed(databaseName);
            NpgsqlConnection.ClearAllPools();
            await using var admin = new NpgsqlConnection(adminConnectionString);
            await admin.OpenAsync();
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }
}
