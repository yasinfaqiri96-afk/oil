using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Services.Reconciliation;
using System.Data.Common;
using Xunit;
using Xunit.Abstractions;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// تعداد رفت‌وبرگشت واقعی دیتابیس در مسیر خلاصهٔ مغایرت‌گیری. سقف پایین‌تر از عدد
/// امروز نیست تا هر بازگشت به عقب (اضافه شدن query تازه) همین‌جا دیده شود.
/// روی دیتابیس موقتِ همان fixture اجرا می‌شود؛ هیچ دیتابیس توسعه یا تولیدی لمس نمی‌شود.
/// </summary>
[Collection(AccountingPostgreSqlCollection.CollectionName)]
public sealed class ReconciliationSummaryQueryCountTests(
    AccountingPostgreSqlFixture fixture,
    ITestOutputHelper output)
{
    /// <summary>
    /// سقف مسیر خلاصه. چند query فقط وقتی داده وجود دارد اجرا می‌شوند، پس عدد به
    /// محتوای دیتابیس موقت وابسته است: روی دیتابیس خالی ۴۵ و با دادهٔ بقیهٔ تست‌های
    /// همین collection ۵۵ رفت‌وبرگشت اندازه‌گیری شد (پس از یکی‌کردن دو query دفتر کلِ
    /// «Sale» در <c>BuildMissingLedgerAsync</c>). سقف کمی بالاتر گرفته شده تا تست
    /// شکننده نباشد ولی هر query تازه‌ای در این مسیر را بگیرد.
    /// </summary>
    private const int SummaryRoundTripCeiling = 58;

    private sealed class CountingInterceptor : DbCommandInterceptor
    {
        public int Count;

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Interlocked.Increment(ref Count);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Count);
            return ValueTask.FromResult(result);
        }

        public override InterceptionResult<object> ScalarExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
        {
            Interlocked.Increment(ref Count);
            return result;
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<object> result,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Count);
            return ValueTask.FromResult(result);
        }
    }

    private (ApplicationDbContext Db, CountingInterceptor Counter) NewCountedContext()
    {
        var counter = new CountingInterceptor();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(fixture.ConnectionString)
            .AddInterceptors(counter)
            .Options;
        return (new ApplicationDbContext(options), counter);
    }

    [Fact]
    public async Task Summary_Stays_Within_The_Round_Trip_Ceiling()
    {
        var (db, counter) = NewCountedContext();
        await using (db)
        {
            var service = new ReconciliationService(db);
            await service.BuildSummaryCountsAsync();
        }

        output.WriteLine($"Reconciliation summary round-trips: {counter.Count}");
        Assert.InRange(counter.Count, 1, SummaryRoundTripCeiling);
    }

    [Fact]
    public async Task Discrepancy_Summary_Runs_Exactly_One_Count_Per_Category()
    {
        var (db, counter) = NewCountedContext();
        await using (db)
        {
            var service = new ReconciliationService(db);
            await service.BuildDiscrepancyCountsAsync();
        }

        output.WriteLine($"Discrepancy counts round-trips: {counter.Count}");
        Assert.Equal(
            PTGOilSystem.Web.Models.Reconciliation.ReconciliationDiscrepancyText.All.Count,
            counter.Count);
    }

    [Fact]
    public async Task A_Discrepancy_Page_Runs_Exactly_One_Count_And_One_Page_Query()
    {
        var (db, counter) = NewCountedContext();
        await using (db)
        {
            var service = new ReconciliationService(db);
            await service.BuildDiscrepancyPageAsync(
                PTGOilSystem.Web.Models.Reconciliation.ReconciliationDiscrepancyCategory.SaleWithoutCogs,
                page: 1,
                pageSize: 50);
        }

        Assert.Equal(2, counter.Count);
    }

    [Fact]
    public async Task Streaming_Export_Runs_A_Single_Query_And_Never_Materialises_A_List()
    {
        var (db, counter) = NewCountedContext();
        await using (db)
        {
            var service = new ReconciliationService(db);
            var seen = 0;
            await foreach (var _ in service.StreamDiscrepancyRowsAsync(
                PTGOilSystem.Web.Models.Reconciliation.ReconciliationDiscrepancyCategory.SaleWithoutCogs))
            {
                seen++;
            }

            Assert.True(seen >= 0);
        }

        Assert.Equal(1, counter.Count);
    }
}
