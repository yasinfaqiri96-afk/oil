using Microsoft.EntityFrameworkCore;
using Npgsql;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Helpers;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Reconciliation;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// PTG فاز ۷ — جستجوی canonical روی PostgreSQL واقعی.
///
/// چرا این‌جا و نه InMemory: سطرِ «پیش از Backfill» یعنی ستونی که واقعاً <c>NULL</c> است.
/// چون <c>ApplicationDbContext</c> عمداً هنگام هر ذخیره کلید را می‌سازد، تنها راهِ
/// ساختنِ آن حالت SQL خام روی دیتابیس واقعی است. طرحِ ستون و ایندکس هم فقط این‌جا
/// قابل اثبات است.
/// </summary>
[Trait("Category", "PostgreSql")]
[Trait("Category", "Integration")]
[Collection(CanonicalSearchPostgresCollection.CollectionName)]
public sealed class CanonicalSearchPostgresTests(CanonicalSearchPostgresFixture fixture)
{
    private void RequirePostgres()
        => Assert.True(fixture.Available, $"فاز ۷ به PostgreSQL واقعی نیاز دارد: {fixture.UnavailableReason}");

    private async Task ResetCustomersAsync()
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("DELETE FROM \"Customers\"", connection);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>ستون را واقعاً NULL می‌کند — بدون عبور از SaveChanges.</summary>
    private async Task NullOutCustomerSearchKeysAsync()
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "UPDATE \"Customers\" SET \"SearchKey\" = NULL", connection);
        await command.ExecuteNonQueryAsync();
    }

    // ------------------------------------------------------------------
    // ۱ — طرحِ دیتابیس: ستونِ nullable + ایندکس، و هیچ ستونِ xmin
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("Partners")]
    [InlineData("Suppliers")]
    [InlineData("Customers")]
    [InlineData("Companies")]
    [InlineData("Trucks")]
    [InlineData("Wagons")]
    [InlineData("Contracts")]
    [InlineData("LoadingRegisters")]
    public async Task SearchKeyColumn_ExistsAsNullableIndexedText(string table)
    {
        RequirePostgres();

        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();

        await using (var column = new NpgsqlCommand(
            "SELECT is_nullable FROM information_schema.columns " +
            "WHERE table_name = @t AND column_name = 'SearchKey'", connection))
        {
            column.Parameters.AddWithValue("t", table);
            var isNullable = (string?)await column.ExecuteScalarAsync();
            Assert.Equal("YES", isNullable); // دادهٔ موجود نباید NOT NULL بشکند
        }

        await using var index = new NpgsqlCommand(
            "SELECT COUNT(*) FROM pg_indexes WHERE tablename = @t AND indexdef LIKE '%SearchKey%'",
            connection);
        index.Parameters.AddWithValue("t", table);
        Assert.True(Convert.ToInt64(await index.ExecuteScalarAsync()) >= 1);
    }

    /// <summary>
    /// روشِ کنارگذاشته‌شدهٔ <c>xmin</c> نباید دوباره برگشته باشد.
    ///
    /// فقط اسکیمای <c>public</c> بررسی می‌شود: <c>xmin</c>ِ واقعیِ PostgreSQL یک ستونِ سیستمی
    /// است و کاتالوگ‌های خودِ موتور هم نامِ مشابه دارند؛ چیزی که این‌جا مهم است، نبودنِ
    /// ستونِ <b>ساخته‌شده توسط مدل</b> در جدول‌های خودِ برنامه است.
    /// </summary>
    [Fact]
    public async Task NoUserDefinedXminColumn_ExistsInThePublicSchema()
    {
        RequirePostgres();

        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT string_agg(table_name || '.' || column_name, ', ') " +
            "FROM information_schema.columns " +
            "WHERE table_schema = 'public' AND lower(column_name) = 'xmin'", connection);

        var offenders = await command.ExecuteScalarAsync();
        Assert.True(offenders is null or DBNull, $"ستونِ xmin دوباره ساخته شده: {offenders}");
    }

    // ------------------------------------------------------------------
    // ۲ — جستجو روی PostgreSQL واقعی
    // ------------------------------------------------------------------

    [Fact]
    public async Task PersianQuery_FindsArabicSpelledCustomer_OnRealPostgres()
    {
        RequirePostgres();
        await ResetCustomersAsync();

        await using (var db = fixture.CreateDbContext())
        {
            db.Customers.Add(new Customer { Code = "C-1", Name = "يوسف اسماعيل" });
            await db.SaveChangesAsync();
        }

        await using (var db = fixture.CreateDbContext())
        {
            var canonical = AfghanTextNormalizer.NormalizeForSearch("یوسف");
            var hits = await db.Customers
                .Where(c => c.SearchKey != null && c.SearchKey.Contains(canonical))
                .ToListAsync();

            Assert.Single(hits);
            Assert.Equal("يوسف اسماعيل", hits[0].Name); // نمایش دست‌نخورده
        }
    }

    // ------------------------------------------------------------------
    // ۳ — سطرِ پیش از Backfill: با شرطِ قدیمی هنوز پیدا می‌شود
    // ------------------------------------------------------------------

    [Fact]
    public async Task RowWithNullSearchKey_RemainsDiscoverableThroughTheFallback()
    {
        RequirePostgres();
        await ResetCustomersAsync();

        await using (var db = fixture.CreateDbContext())
        {
            db.Customers.Add(new Customer { Code = "C-1", Name = "Legacy Row" });
            await db.SaveChangesAsync();
        }

        await NullOutCustomerSearchKeysAsync();

        await using (var db = fixture.CreateDbContext())
        {
            const string q = "Legacy";
            var canonical = AfghanTextNormalizer.NormalizeForSearch(q);
            var hits = await db.Customers
                .Where(c => (c.SearchKey != null && c.SearchKey.Contains(canonical))
                    || c.Name.Contains(q))
                .ToListAsync();

            Assert.Single(hits);
            Assert.Null(hits[0].SearchKey);
        }
    }

    // ------------------------------------------------------------------
    // ۴ — Backfill روی سطرهای واقعاً خالی
    // ------------------------------------------------------------------

    [Fact]
    public async Task Backfill_FillsNullRows_AndIsSafeToRunTwice()
    {
        RequirePostgres();
        await ResetCustomersAsync();

        await using (var db = fixture.CreateDbContext())
        {
            db.Customers.Add(new Customer { Code = "C-1", Name = "يوسف" });
            db.Customers.Add(new Customer { Code = "C-2", Name = "احمد" });
            await db.SaveChangesAsync();
        }

        await NullOutCustomerSearchKeysAsync();

        int firstUpdated;
        await using (var db = fixture.CreateDbContext())
        {
            firstUpdated = (await CanonicalSearchKeyBackfill.RunAsync(db, commit: true))
                .Tables.Single(t => t.Entity == "Customer").Updated;
        }

        await using (var db = fixture.CreateDbContext())
        {
            var second = await CanonicalSearchKeyBackfill.RunAsync(db, commit: true);

            Assert.Equal(2, firstUpdated);
            Assert.Equal(0, second.Tables.Single(t => t.Entity == "Customer").Updated);
            Assert.Equal(2, await db.Customers.CountAsync(c => c.SearchKey != null));
            Assert.Equal(
                AfghanTextNormalizer.NormalizeForSearch("C-1 یوسف"),
                await db.Customers.Where(c => c.Code == "C-1").Select(c => c.SearchKey).SingleAsync());
        }
    }

    [Fact]
    public async Task Backfill_DryRun_LeavesTheColumnUntouched()
    {
        RequirePostgres();
        await ResetCustomersAsync();

        await using (var db = fixture.CreateDbContext())
        {
            db.Customers.Add(new Customer { Code = "C-1", Name = "يوسف" });
            await db.SaveChangesAsync();
        }

        await NullOutCustomerSearchKeysAsync();

        await using (var db = fixture.CreateDbContext())
        {
            var result = await CanonicalSearchKeyBackfill.RunAsync(db, commit: false);
            Assert.False(result.Committed);
            Assert.Equal(1, result.Tables.Single(t => t.Entity == "Customer").Updated);
        }

        await using (var db = fixture.CreateDbContext())
        {
            Assert.Equal(0, await db.Customers.CountAsync(c => c.SearchKey != null));
        }
    }

    /// <summary>Backfill هرگز متنِ نمایشی را بازنویسی نمی‌کند.</summary>
    [Fact]
    public async Task Backfill_NeverRewritesDisplayText()
    {
        RequirePostgres();
        await ResetCustomersAsync();

        const string asTyped = "شركت كابل ١٢٣";
        await using (var db = fixture.CreateDbContext())
        {
            db.Customers.Add(new Customer { Code = "C-١", Name = asTyped });
            await db.SaveChangesAsync();
        }

        await NullOutCustomerSearchKeysAsync();

        await using (var db = fixture.CreateDbContext())
        {
            await CanonicalSearchKeyBackfill.RunAsync(db, commit: true);
        }

        await using (var db = fixture.CreateDbContext())
        {
            var stored = await db.Customers.AsNoTracking().SingleAsync();
            Assert.Equal(asTyped, stored.Name);
            Assert.Equal("C-١", stored.Code);
            Assert.Equal(AfghanTextNormalizer.NormalizeForSearch("C-1 شرکت کابل 123"), stored.SearchKey);
        }
    }

    // ------------------------------------------------------------------
    // ۵ — اسکنرِ CANONICAL-SEARCH-STALE روی سطرِ واقعاً کهنه
    // ------------------------------------------------------------------

    /// <summary>
    /// سطری که کلیدش با متنِ نمایشی نمی‌خواند — همان چیزی که یک نوشتنِ خارج از
    /// <c>SaveChanges</c> (SQL دستی یا Restore) به‌جا می‌گذارد. اسکنر باید آن را بشمارد،
    /// و پس از Backfill دوباره صفر شود.
    /// </summary>
    [Fact]
    public async Task Scanner_ReportsAStaleKey_AndIsCleanAfterBackfill()
    {
        RequirePostgres();
        await ResetCustomersAsync();

        await using (var db = fixture.CreateDbContext())
        {
            db.Customers.Add(new Customer { Code = "C-1", Name = "يوسف" });
            await db.SaveChangesAsync();
        }

        // کلیدِ غلط، بدون عبور از قلاب ذخیره.
        await using (var connection = new NpgsqlConnection(fixture.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(
                "UPDATE \"Customers\" SET \"SearchKey\" = 'stale-key'", connection);
            await command.ExecuteNonQueryAsync();
        }

        await using (var db = fixture.CreateDbContext())
        {
            var report = await new LedgerIntegrityReconciliationService(db).RunAsync();
            var finding = report.Findings.Single(f => f.Code == "CANONICAL-SEARCH-STALE");

            Assert.Equal(1, finding.Count);
            Assert.Contains(finding.Samples, sample => sample.Contains("مشتری"));
        }

        await using (var db = fixture.CreateDbContext())
        {
            await CanonicalSearchKeyBackfill.RunAsync(db, commit: true);
        }

        await using (var db = fixture.CreateDbContext())
        {
            var report = await new LedgerIntegrityReconciliationService(db).RunAsync();
            Assert.Equal(0, report.Findings.Single(f => f.Code == "CANONICAL-SEARCH-STALE").Count);

            // متنِ نمایشی در تمام این رفت‌وبرگشت دست‌نخورده مانده.
            Assert.Equal("يوسف", await db.Customers.Select(c => c.Name).SingleAsync());
        }
    }
}
