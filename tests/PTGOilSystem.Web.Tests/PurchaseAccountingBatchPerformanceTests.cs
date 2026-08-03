using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PTGOilSystem.Web.Configuration;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Accounting;
using Xunit;

namespace PTGOilSystem.Web.Tests;

public sealed class PurchaseAccountingBatchPerformanceTests
{
    private const int LoadingCount = 20;
    private static readonly DateTime LoadingDate = new(2026, 7, 5);

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Batch_Preserves_Journals_And_Removes_Serial_Posting_Cost()
    {
        var serial = await MeasureAsync(useBatch: false);
        var batch = await MeasureAsync(useBatch: true);

        Console.WriteLine($"PURCHASE_SERIAL_MS={serial.ElapsedMilliseconds}");
        Console.WriteLine($"PURCHASE_BATCH_MS={batch.ElapsedMilliseconds}");

        Assert.Equal(LoadingCount, serial.JournalCount);
        Assert.Equal(LoadingCount, batch.JournalCount);
        Assert.Equal(LoadingCount * 2, serial.LineCount);
        Assert.Equal(LoadingCount * 2, batch.LineCount);
        Assert.True(
            batch.ElapsedMilliseconds < serial.ElapsedMilliseconds,
            $"Expected batch posting ({batch.ElapsedMilliseconds} ms) to beat serial posting ({serial.ElapsedMilliseconds} ms).");
    }

    private static async Task<Measurement> MeasureAsync(bool useBatch)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new SqliteAccountingDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var loadings = Enumerable.Range(1, LoadingCount)
            .Select(index => new LoadingRegister
            {
                ContractId = scope.Contract.Id,
                ProductId = scope.Product.Id,
                TransportType = LoadingTransportType.Wagon,
                LoadingDate = LoadingDate,
                LoadedQuantityMt = 10m + index,
                LoadingPriceUsd = 500m,
                SettlementCurrencyCode = "USD",
                WagonNumber = $"PERF-{index:000}"
            })
            .ToList();
        db.LoadingRegisters.AddRange(loadings);
        await db.SaveChangesAsync();

        var adapter = CreateAdapter(db);
        var stopwatch = Stopwatch.StartNew();
        if (useBatch)
        {
            await adapter.TryPostPurchasesAsync(loadings);
        }
        else
        {
            foreach (var loading in loadings)
                await adapter.TryPostPurchaseAsync(loading);
        }
        stopwatch.Stop();

        var journalCount = await db.JournalEntries.CountAsync(journal =>
            journal.SourceModule == PurchaseAccountingAdapter.SourceModule
            && journal.SourceEntityType == PurchaseAccountingAdapter.PurchaseSourceEntityType
            && !journal.IsReversal);
        var lineCount = await db.JournalEntryLines.CountAsync(line =>
            line.JournalEntry!.SourceModule == PurchaseAccountingAdapter.SourceModule
            && line.JournalEntry.SourceEntityType == PurchaseAccountingAdapter.PurchaseSourceEntityType);

        return new Measurement(stopwatch.ElapsedMilliseconds, journalCount, lineCount);
    }

    private static PurchaseAccountingAdapter CreateAdapter(ApplicationDbContext db)
    {
        var options = Options.Create(new AccountingOptions
        {
            Enabled = true,
            Pilots = new AccountingPilotOptions { Purchase = true }
        });
        return new PurchaseAccountingAdapter(
            db,
            new AccountingPostingService(
                db,
                new PeriodGuard(db, new FiscalCalendarService(db)),
                options,
                new SystemCompanyProvider(db)),
            new AccountingJournalNumberGenerator(),
            new PricingService(db),
            new InventoryValuationService(db),
            options,
            NullLogger<PurchaseAccountingAdapter>.Instance);
    }

    private sealed record Measurement(long ElapsedMilliseconds, int JournalCount, int LineCount);

    private sealed class SqliteAccountingDbContext(DbContextOptions<ApplicationDbContext> options)
        : ApplicationDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            foreach (var entityType in new[]
                     {
                         typeof(Account),
                         typeof(AccountingSettings),
                         typeof(FiscalYear),
                         typeof(FiscalPeriod),
                         typeof(JournalEntry)
                     })
            {
                modelBuilder.Entity(entityType)
                    .Property(nameof(Account.RowVersion))
                    .ValueGeneratedNever();
            }
        }
    }
}
