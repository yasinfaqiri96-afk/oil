using Microsoft.EntityFrameworkCore;
using Npgsql;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// نرخ ارز «مانده قابل انتقال» روی PostgreSQL واقعی.
///
/// تست‌های InMemory ریاضیِ نرخ را قفل می‌کنند ولی چیزی دربارهٔ ستون واقعی نمی‌گویند.
/// این کلاس همان سناریوها را روی schema واقعی با numeric(24,12) اجرا می‌کند تا ثابت شود
/// نرخ‌های ۷۰، ۷۷.۳۵، ۹۰ و نرخ ترکیبی ۷۵ در رفت‌وبرگشتِ دیتابیس هم دقیق می‌مانند و
/// انتقال و برگشت انتقال روی همان schema سالم کار می‌کنند.
/// </summary>
[Collection(SupplierBalanceTransferRatePostgreSqlCollection.CollectionName)]
public sealed class SupplierBalanceTransferRatePostgreSqlTests
{
    private const int SupplierId = 1;
    private const int CompanyId = 1;
    private const int ContractId = 1;

    private readonly SupplierBalanceTransferRatePostgreSqlFixture _fixture;

    public SupplierBalanceTransferRatePostgreSqlTests(SupplierBalanceTransferRatePostgreSqlFixture fixture)
        => _fixture = fixture;

    [Theory]
    [InlineData("70")]
    [InlineData("77.35")]
    [InlineData("90")]
    [InlineData("75")]
    [InlineData("0.913700000000")]
    public async Task Direct_Rate_Survives_A_Real_Postgres_Round_Trip(string rateText)
    {
        var rate = decimal.Parse(rateText, System.Globalization.CultureInfo.InvariantCulture);

        await using var db = _fixture.CreateDbContext();
        var id = await SeedLedgerAsync(db, originalAmount: 10_000m, perUsdRate: rate);

        // از دیتابیس تازه خوانده می‌شود، نه از cache مربوط به همان context.
        await using var read = _fixture.CreateDbContext();
        var stored = await read.LedgerEntries.AsNoTracking().SingleAsync(l => l.Id == id);

        Assert.Equal(rate, stored.AppliedCurrencyPerUsdRate);
        Assert.Equal(FxRateMath.ToUsdFromPerUsd(rate), stored.AppliedFxRateToUsd);
    }

    [Fact]
    public async Task Rate_70_Reads_Back_As_70_Through_The_Balance_Engine()
    {
        await using var db = await FreshAsync();
        await SeedLedgerAsync(db, originalAmount: 7000m, perUsdRate: 70m);

        await using var read = _fixture.CreateDbContext();
        var balance = await new SupplierTransferableBalanceService(read).GetAsync(SupplierId);
        var bucket = Assert.Single(balance.Company(CompanyId)!.Buckets);

        Assert.Equal(70m, bucket.WeightedHistoricalPerUsdRate);
        Assert.False(bucket.RateIsEstimated);
        Assert.Equal(100m, bucket.RemainingBookAmountUsd);
    }

    [Fact]
    public async Task Blended_Rate_75_Is_Exact_On_Real_Postgres()
    {
        await using var db = await FreshAsync();
        await SeedLedgerAsync(db, 7000m, 70m, day: 1);
        await SeedLedgerAsync(db, 8000m, 80m, day: 2);

        await using var read = _fixture.CreateDbContext();
        var bucket = Assert.Single(
            (await new SupplierTransferableBalanceService(read).GetAsync(SupplierId)).Company(CompanyId)!.Buckets);

        Assert.Equal(15_000m, bucket.RemainingOriginalAmount);
        Assert.Equal(200m, bucket.RemainingBookAmountUsd);
        Assert.Equal(75m, bucket.WeightedHistoricalPerUsdRate);
        Assert.False(bucket.RateIsEstimated);
    }

    [Fact]
    public async Task Transfer_And_Reversal_Round_Trip_On_Real_Postgres()
    {
        await using var seed = await FreshAsync();
        await SeedLedgerAsync(seed, originalAmount: 7000m, perUsdRate: 70m);

        // --- انتقال با نرخ روز ۸۰ (متفاوت از نرخ تاریخی ۷۰) ---
        await using var createDb = _fixture.CreateDbContext();
        var service = new SupplierBalanceTransferService(createDb, new SupplierTransferableBalanceService(createDb));
        var transfer = Assert.Single(await service.CreateAsync(new SupplierBalanceTransferCreateRequest(
            SupplierId,
            CompanyId,
            new DateTime(2026, 4, 1),
            "RUB",
            80m,
            [new SupplierBalanceTransferLineRequest(ContractId, 7000m, 1m)],
            "REF-PG",
            null,
            "tester")));

        var transferId = transfer.Id;

        await using (var check = _fixture.CreateDbContext())
        {
            var saved = await check.SupplierBalanceTransfers.AsNoTracking()
                .Include(t => t.Sources)
                .SingleAsync(t => t.Id == transferId);

            // نرخ‌ها از دیتابیس واقعی برمی‌گردند، بدون انحراف.
            Assert.Equal(70m, saved.HistoricalCurrencyPerUsdRate);
            Assert.Equal(80m, saved.TransferPerUsdRate);
            Assert.False(saved.HistoricalRateIsEstimated);
            Assert.Equal(FxRateMath.ToUsdFromPerUsd(70m), saved.HistoricalFxRateToUsd);
            Assert.Equal(FxRateMath.ToUsdFromPerUsd(80m), saved.TransferFxRateToUsd);

            Assert.Equal(100m, saved.HistoricalAmountUsd);
            Assert.Equal(87.5m, saved.TransferValueUsd);
            Assert.Equal(-12.5m, saved.ExchangeDifferenceUsd);
            Assert.Equal(SarrafSettlementDifferenceType.Loss, saved.ExchangeDifferenceType);
            Assert.Equal(70m, Assert.Single(saved.Sources).HistoricalCurrencyPerUsdRate);
        }

        // --- برگشت انتقال با همان نرخ‌های قفل‌شده ---
        await using var reverseDb = _fixture.CreateDbContext();
        var reverseService = new SupplierBalanceTransferService(
            reverseDb, new SupplierTransferableBalanceService(reverseDb));
        var reversed = await reverseService.ReverseAsync(
            new SupplierBalanceTransferReverseRequest(transferId, "آزمایش برگشت", "tester"));

        Assert.Equal(SupplierBalanceTransferStatus.Reversed, reversed.Status);
        Assert.Equal(70m, reversed.HistoricalCurrencyPerUsdRate);
        Assert.Equal(80m, reversed.TransferPerUsdRate);

        // مانده دقیقاً به حالت اول برمی‌گردد.
        await using var afterDb = _fixture.CreateDbContext();
        var bucket = Assert.Single(
            (await new SupplierTransferableBalanceService(afterDb).GetAsync(SupplierId)).Company(CompanyId)!.Buckets);
        Assert.Equal(7000m, bucket.RemainingOriginalAmount);
        Assert.Equal(100m, bucket.RemainingBookAmountUsd);
        Assert.Equal(70m, bucket.WeightedHistoricalPerUsdRate);
    }

    [Fact]
    public async Task Legacy_Row_Without_Direct_Rate_Is_Estimated_On_Real_Postgres()
    {
        await using var db = await FreshAsync();
        await SeedLedgerAsync(db, originalAmount: 7000m, perUsdRate: 70m, storeDirectRate: false);

        await using var read = _fixture.CreateDbContext();
        var bucket = Assert.Single(
            (await new SupplierTransferableBalanceService(read).GetAsync(SupplierId)).Company(CompanyId)!.Buckets);

        Assert.True(bucket.RateIsEstimated);
        Assert.Null(bucket.StoredPerUsdRate);
    }

    // ================= helpers =================

    /// <summary>هر تست از دادهٔ تمیز شروع می‌کند؛ این fixture بین تست‌ها مشترک است.</summary>
    private async Task<ApplicationDbContext> FreshAsync()
    {
        var db = _fixture.CreateDbContext();
        await db.Database.ExecuteSqlRawAsync(
            "DELETE FROM \"SupplierBalanceTransferSources\"; "
            + "DELETE FROM \"SupplierBalanceTransfers\"; "
            + "DELETE FROM \"LedgerEntries\";");
        return db;
    }

    private static async Task<int> SeedLedgerAsync(
        ApplicationDbContext db,
        decimal originalAmount,
        decimal perUsdRate,
        int day = 1,
        bool storeDirectRate = true)
    {
        var entry = new LedgerEntry
        {
            EntryDate = new DateTime(2026, 2, day, 0, 0, 0, DateTimeKind.Utc),
            Side = LedgerSide.Debit,
            AmountUsd = decimal.Round(originalAmount / perUsdRate, 4, MidpointRounding.AwayFromZero),
            Currency = "USD",
            SourceAmount = originalAmount,
            SourceCurrencyCode = "RUB",
            AppliedFxRateToUsd = storeDirectRate
                ? FxRateMath.ToUsdFromPerUsd(perUsdRate)
                : decimal.Round(1m / perUsdRate, 6, MidpointRounding.AwayFromZero),
            AppliedCurrencyPerUsdRate = storeDirectRate ? perUsdRate : null,
            AppliedFxRateDate = new DateTime(2026, 2, day, 0, 0, 0, DateTimeKind.Utc),
            Description = "SupplierPayment",
            SourceType = "SupplierPayment",
            SourceId = day,
            SupplierId = SupplierId,
            ContractId = ContractId
        };

        db.LedgerEntries.Add(entry);
        await db.SaveChangesAsync();
        return entry.Id;
    }
}

[CollectionDefinition(CollectionName, DisableParallelization = true)]
public sealed class SupplierBalanceTransferRatePostgreSqlCollection
    : ICollectionFixture<SupplierBalanceTransferRatePostgreSqlFixture>
{
    public const string CollectionName = "Supplier Balance Transfer Rate PostgreSQL";
}

/// <summary>
/// یک دیتابیس واقعیِ یک‌بارمصرف که تمام migrationها رویش اجرا می‌شود — یعنی همان
/// migration نرخ مستقیم هم واقعاً روی PostgreSQL اجرا و آزمایش می‌شود.
/// </summary>
public sealed class SupplierBalanceTransferRatePostgreSqlFixture : IAsyncLifetime
{
    private readonly string _databaseName =
        $"{DatabaseSafetyGuard.AccountingTestDatabasePrefix}{Guid.NewGuid():N}";

    private readonly string _adminConnectionString =
        Environment.GetEnvironmentVariable("PTG_TEST_POSTGRES_ADMIN")
        ?? "Host=localhost;Port=5432;Username=postgres;Password=postgres;Database=postgres;Timeout=10;Command Timeout=60";

    private bool _created;

    public string ConnectionString { get; private set; } = "";

    public async Task InitializeAsync()
    {
        DatabaseSafetyGuard.EnsureIntegrationTestCreateAllowed(_databaseName);

        await using (var admin = new NpgsqlConnection(_adminConnectionString))
        {
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{_databaseName}\"", admin);
            await create.ExecuteNonQueryAsync();
            _created = true;
        }

        var builder = new NpgsqlConnectionStringBuilder(_adminConnectionString) { Database = _databaseName };
        ConnectionString = builder.ConnectionString;
        DatabaseSafetyGuard.EnsureIntegrationTestUseAllowed(builder.Database);

        await using var db = CreateDbContext();
        await db.Database.MigrateAsync();
        await SeedReferenceDataAsync(db);
    }

    public ApplicationDbContext CreateDbContext()
        => new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(ConnectionString)
            .EnableSensitiveDataLogging()
            .Options);

    private static async Task SeedReferenceDataAsync(ApplicationDbContext db)
    {
        db.Products.Add(new Product { Id = 1, Code = "G92", Name = "Gasoline 92", UnitOfMeasure = "MT", IsActive = true });
        db.Companies.Add(new Company { Id = 1, Code = "PTG", Name = "PTG", Country = "AF", IsActive = true });
        db.Suppliers.Add(new Supplier { Id = 1, Code = "SUP1", Name = "Supplier One", IsActive = true });
        await db.SaveChangesAsync();

        db.Contracts.Add(new Contract
        {
            Id = 1,
            ContractNumber = "P-USD-1",
            ContractName = "P-USD-1",
            ContractType = ContractType.Purchase,
            Status = ContractStatus.Active,
            CompanyId = 1,
            ProductId = 1,
            SupplierId = 1,
            ContractDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            PricingMethod = PricingMethod.ManualFinalPrice,
            QuantityMt = 1000m,
            Currency = "USD"
        });
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        if (!_created)
        {
            return;
        }

        await using var admin = new NpgsqlConnection(_adminConnectionString);
        await admin.OpenAsync();
        await using var drop = new NpgsqlCommand(
            $"DROP DATABASE IF EXISTS \"{_databaseName}\" WITH (FORCE)", admin);
        await drop.ExecuteNonQueryAsync();
    }
}
