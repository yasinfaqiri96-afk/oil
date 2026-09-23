using System.Data.Common;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Audit;
using Xunit;
using Xunit.Abstractions;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// اندازه‌گیری واقعیِ مسیر «تبدیل گروهی بارگیری به حمل» روی PostgreSQL:
/// تعداد رفت‌وبرگشت دیتابیس، تعداد تراکنش، تعداد SaveChanges، اندازهٔ ChangeTracker و زمان.
/// عدد واقعی است، نه تخمین.
/// </summary>
[Collection(BulkFromLoadingPerformanceCollection.CollectionName)]
public sealed class BulkFromLoadingPerformanceTests(
    BulkFromLoadingPerformanceFixture fixture,
    ITestOutputHelper output)
{
    [Theory]
    [InlineData(10)]
    [InlineData(100)]
    [InlineData(500)]
    public async Task Baseline_PerLoop_Conversion_Cost(int loadingCount)
    {
        Assert.True(fixture.Available, $"این اندازه‌گیری به PostgreSQL واقعی نیاز دارد: {fixture.UnavailableReason}");

        await fixture.TruncateAsync();
        var counter = new CommandCounter();
        await using var db = fixture.CreateDbContext(counter);
        await SeedAsync(db, loadingCount);
        db.ChangeTracker.Clear();

        var workflow = BuildWorkflow(db);
        var saves = 0;
        db.SavingChanges += (_, _) => saves++;
        counter.Reset();
        var watch = Stopwatch.StartNew();

        for (var id = 1; id <= loadingCount; id++)
        {
            await workflow.StartFromLoadingAsync(new StartTransportFromLoadingCommand
            {
                LoadingRegisterId = id,
                QuantityMt = 100m,
                TransportType = LoadingTransportType.Truck,
                TruckId = 1,
                TransportDate = new DateTime(2026, 9, 5),
                Reference = $"BULK-{id}"
            });
        }

        watch.Stop();
        output.WriteLine(
            $"BASELINE N={loadingCount} | commands={counter.Commands} | transactions={counter.Transactions} " +
            $"| saveChanges={saves} | tracked={db.ChangeTracker.Entries().Count()} | ms={watch.ElapsedMilliseconds}");

        Assert.Equal(loadingCount, await db.InventoryTransportLegs.AsNoTracking().CountAsync());
    }

    /// <summary>
    /// همان حلقهٔ بالا، فقط با پاک‌کردن ChangeTracker بعد از هر تبدیل. اگر زمان از درجهٔ دوم
    /// به خطی برگردد، ریشهٔ کندی انباشت ChangeTracker است نه تعداد کوئری.
    /// </summary>
    [Theory]
    [InlineData(100)]
    [InlineData(500)]
    public async Task Diagnostic_ChangeTracker_Growth_Is_The_Superlinear_Term(int loadingCount)
    {
        Assert.True(fixture.Available, $"این اندازه‌گیری به PostgreSQL واقعی نیاز دارد: {fixture.UnavailableReason}");

        await fixture.TruncateAsync();
        var counter = new CommandCounter();
        await using var db = fixture.CreateDbContext(counter);
        await SeedAsync(db, loadingCount);
        db.ChangeTracker.Clear();

        var workflow = BuildWorkflow(db);
        var saves = 0;
        db.SavingChanges += (_, _) => saves++;
        counter.Reset();
        var watch = Stopwatch.StartNew();

        for (var id = 1; id <= loadingCount; id++)
        {
            await workflow.StartFromLoadingAsync(new StartTransportFromLoadingCommand
            {
                LoadingRegisterId = id,
                QuantityMt = 100m,
                TransportType = LoadingTransportType.Truck,
                TruckId = 1,
                TransportDate = new DateTime(2026, 9, 5),
                Reference = $"BULK-{id}"
            });
            db.ChangeTracker.Clear();
        }

        watch.Stop();
        output.WriteLine(
            $"CLEARED N={loadingCount} | commands={counter.Commands} | transactions={counter.Transactions} " +
            $"| saveChanges={saves} | tracked={db.ChangeTracker.Entries().Count()} | ms={watch.ElapsedMilliseconds}");

        Assert.Equal(loadingCount, await db.InventoryTransportLegs.AsNoTracking().CountAsync());
    }

    /// <summary>مسیر گروهیِ جدید با همان دادهٔ baseline — عددها با هم مقایسه‌شدنی‌اند.</summary>
    [Theory]
    [InlineData(10)]
    [InlineData(100)]
    [InlineData(500)]
    [InlineData(2000)]
    [InlineData(10000)]
    public async Task Bulk_SetBased_Conversion_Cost(int loadingCount)
    {
        Assert.True(fixture.Available, $"این اندازه‌گیری به PostgreSQL واقعی نیاز دارد: {fixture.UnavailableReason}");

        await fixture.TruncateAsync();
        var counter = new CommandCounter();
        await using var db = fixture.CreateDbContext(counter);
        await SeedAsync(db, loadingCount);
        db.ChangeTracker.Clear();

        var workflow = BuildWorkflow(db);
        var saves = 0;
        db.SavingChanges += (_, _) => saves++;
        counter.Reset();
        var watch = Stopwatch.StartNew();

        var result = await workflow.StartManyFromLoadingAsync(new BulkStartTransportFromLoadingCommand
        {
            Rows = Enumerable.Range(1, loadingCount)
                .Select(id => new BulkStartTransportFromLoadingRow
                {
                    LoadingRegisterId = id,
                    QuantityMt = 100m,
                    TransportType = LoadingTransportType.Truck,
                    TruckId = 1,
                    Reference = $"BULK-{id}"
                })
                .ToList(),
            TransportDate = new DateTime(2026, 9, 5)
        });

        watch.Stop();
        output.WriteLine(
            $"BULK N={loadingCount} | commands={counter.Commands} | transactions={counter.Transactions} " +
            $"| saveChanges={saves} | tracked={db.ChangeTracker.Entries().Count()} | ms={watch.ElapsedMilliseconds}");

        Assert.Empty(result.Failures);
        Assert.Equal(loadingCount, result.CreatedCount);
        Assert.Equal(loadingCount, await db.InventoryTransportLegs.AsNoTracking().CountAsync());
        Assert.Equal(loadingCount, await db.InventoryTransportLegAllocations.AsNoTracking().CountAsync());
    }

    internal static TransportWorkflowService BuildWorkflow(ApplicationDbContext db)
    {
        var stock = new StockService(db);
        var quantities = new TransportQuantityService(db);
        var receipts = new InventoryTransportReceiptService(
            db,
            new CurrencyConversionService(new PricingService(db)),
            quantities: quantities);
        return new TransportWorkflowService(
            db,
            new InventoryTransportBatchService(db, stock),
            new TransportChainService(db, receipts, quantities),
            receipts,
            new LossEventWorkflowService(db, stock, new AuditService(db)));
    }

    internal static async Task SeedAsync(ApplicationDbContext db, int loadingCount, decimal loadedQuantityMt = 100m)
    {
        db.Products.Add(new Product { Id = 1, Code = "DSL", Name = "Diesel" });
        db.Companies.Add(new Company { Id = 1, Code = "PTG", Name = "PTG" });
        db.Suppliers.Add(new Supplier { Id = 1, Name = "Supplier" });
        db.Terminals.Add(new Terminal { Id = 1, Code = "T1", Name = "Terminal" });
        db.Trucks.Add(new Truck { Id = 1, PlateNumber = "TRK-1", IsActive = true });
        db.Contracts.Add(new Contract
        {
            Id = 1,
            ContractNumber = "PUR-1",
            ContractType = ContractType.Purchase,
            CompanyId = 1,
            ProductId = 1,
            SupplierId = 1,
            ContractDate = new DateTime(2026, 8, 1),
            QuantityMt = 10_000_000m,
            PricingMethod = PricingMethod.Fixed,
            UnitPriceUsd = 500m
        });
        for (var i = 1; i <= loadingCount; i++)
        {
            db.LoadingRegisters.Add(new LoadingRegister
            {
                Id = i,
                ContractId = 1,
                ProductId = 1,
                TransportType = LoadingTransportType.Truck,
                LoadingDate = new DateTime(2026, 8, 25),
                LoadedQuantityMt = loadedQuantityMt,
                LoadingPriceUsd = 500m,
                RwbNo = $"RWB-{i}"
            });
        }
        await db.SaveChangesAsync();
    }

    internal sealed class CommandCounter : DbCommandInterceptor, IDbTransactionInterceptor
    {
        public int Commands;
        public int Transactions;

        public void Reset() { Commands = 0; Transactions = 0; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Commands);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<object> result,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Commands);
            return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Commands);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        public ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(
            DbConnection connection, TransactionStartingEventData eventData,
            InterceptionResult<DbTransaction> result, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Transactions);
            return ValueTask.FromResult(result);
        }
    }
}
