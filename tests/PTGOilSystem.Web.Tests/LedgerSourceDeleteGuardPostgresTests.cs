using Microsoft.EntityFrameworkCore;
using Npgsql;
using PTGOilSystem.Web.Models.Entities;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// PTG فاز ۸ — یکپارچگیِ «سندِ مالی ↔ دفتر کل» در سطحِ خودِ PostgreSQL.
///
/// چرا فقط روی PostgreSQL واقعی: محافظ یک <c>CONSTRAINT TRIGGER</c> است. نه InMemory و
/// نه SQLite آن را اجرا نمی‌کنند، پس هر ادعایی دربارهٔ «حذفِ خام رد می‌شود» باید همان‌جا
/// اثبات شود که خطر واقعی است — کنسولِ psql روی دیتابیسِ تولید.
/// </summary>
[Trait("Category", "PostgreSql")]
[Trait("Category", "Integration")]
[Collection(CanonicalSearchPostgresCollection.CollectionName)]
public sealed class LedgerSourceDeleteGuardPostgresTests(CanonicalSearchPostgresFixture fixture)
{
    private void RequirePostgres()
        => Assert.True(fixture.Available, $"فاز ۸ به PostgreSQL واقعی نیاز دارد: {fixture.UnavailableReason}");

    private async Task<NpgsqlConnection> OpenAsync()
    {
        var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<long> ScalarCountAsync(string sql)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    /// <summary>نوعِ مصرفِ مشترکِ آزمون‌ها — یک‌بار ساخته می‌شود و دوباره استفاده می‌گردد.</summary>
    private async Task<int> EnsureExpenseTypeAsync()
    {
        await using var db = fixture.CreateDbContext();

        var existing = await db.ExpenseTypes.FirstOrDefaultAsync(t => t.Code == "PTG8");
        if (existing is not null)
        {
            return existing.Id;
        }

        var expenseType = new ExpenseType { Code = "PTG8", Name = "PTG Phase 8 test" };
        db.ExpenseTypes.Add(expenseType);
        await db.SaveChangesAsync();
        return expenseType.Id;
    }

    /// <summary>یک مصرف + سطر دفتر کلِ آن.</summary>
    private async Task<int> SeedPostedExpenseAsync()
    {
        var expenseTypeId = await EnsureExpenseTypeAsync();
        await using var db = fixture.CreateDbContext();

        var expense = new ExpenseTransaction
        {
            ExpenseTypeId = expenseTypeId,
            ExpenseDate = new DateTime(2026, 3, 10, 0, 0, 0, DateTimeKind.Utc),
            Description = "PTG فاز ۸ — سندِ آزمون",
            Amount = 500m,
            Currency = "USD",
            AmountUsd = 500m,
        };
        db.ExpenseTransactions.Add(expense);
        await db.SaveChangesAsync();

        db.LedgerEntries.Add(new LedgerEntry
        {
            EntryDate = expense.ExpenseDate,
            SourceType = "Expense",
            SourceId = expense.Id,
            Description = "PTG فاز ۸ — دفتر کلِ آزمون",
            AmountUsd = 500m,
        });
        await db.SaveChangesAsync();

        return expense.Id;
    }

    private async Task CleanupAsync(int expenseId)
    {
        // ترتیب مهم است: اول دفتر کل، بعد سند — همان کاری که خودِ برنامه می‌کند.
        await ExecuteAsync($"DELETE FROM \"LedgerEntries\" WHERE \"SourceType\" = 'Expense' AND \"SourceId\" = {expenseId}");
        await ExecuteAsync($"DELETE FROM \"ExpenseTransactions\" WHERE \"Id\" = {expenseId}");
    }

    // ------------------------------------------------------------------
    // ۱ — تریگرها واقعاً نصب شده‌اند
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("SalesTransactions", "ptg_guard_sale_ledger_delete")]
    [InlineData("ExpenseTransactions", "ptg_guard_expense_ledger_delete")]
    [InlineData("PaymentTransactions", "ptg_guard_payment_ledger_delete")]
    [InlineData("SupplierBalanceTransfers", "ptg_guard_supplier_balance_transfer_ledger_delete")]
    [InlineData("ContractBalanceTransfers", "ptg_guard_contract_balance_transfer_ledger_delete")]
    public async Task Guard_IsInstalledAsADeferredConstraintTrigger(string table, string triggerName)
    {
        RequirePostgres();

        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT t.tgdeferrable, t.tginitdeferred
            FROM pg_trigger t
            JOIN pg_class c ON c.oid = t.tgrelid
            WHERE c.relname = @table AND t.tgname = @trigger
            """, connection);
        command.Parameters.AddWithValue("table", table);
        command.Parameters.AddWithValue("trigger", triggerName);

        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), $"تریگرِ {triggerName} روی {table} پیدا نشد.");
        Assert.True(reader.GetBoolean(0), "تریگر باید DEFERRABLE باشد.");
        Assert.True(reader.GetBoolean(1), "تریگر باید INITIALLY DEFERRED باشد.");
    }

    // ------------------------------------------------------------------
    // ۲ — خطرِ اصلی: حذفِ خام از psql
    // ------------------------------------------------------------------

    [Fact]
    public async Task RawDelete_OfAPostedDocument_IsRejected()
    {
        RequirePostgres();
        var expenseId = await SeedPostedExpenseAsync();

        try
        {
            var error = await Assert.ThrowsAsync<PostgresException>(
                () => ExecuteAsync($"DELETE FROM \"ExpenseTransactions\" WHERE \"Id\" = {expenseId}"));

            Assert.Contains("cannot delete ExpenseTransactions", error.MessageText);

            // نه سند پاک شده، نه دفتر کل — هیچ چیزی cascade نشده.
            Assert.Equal(1L, await ScalarCountAsync(
                $"SELECT COUNT(*) FROM \"ExpenseTransactions\" WHERE \"Id\" = {expenseId}"));
            Assert.Equal(1L, await ScalarCountAsync(
                $"SELECT COUNT(*) FROM \"LedgerEntries\" WHERE \"SourceType\" = 'Expense' AND \"SourceId\" = {expenseId}"));
        }
        finally
        {
            await CleanupAsync(expenseId);
        }
    }

    // ------------------------------------------------------------------
    // ۳ — کارِ درستِ برنامه نباید بشکند
    // ------------------------------------------------------------------

    /// <summary>سند و دفتر کلِ آن با هم در یک تراکنش — باید بگذرد.</summary>
    [Fact]
    public async Task DeletingDocumentAndItsLedgerInOneTransaction_IsAllowed()
    {
        RequirePostgres();
        var expenseId = await SeedPostedExpenseAsync();

        await using (var connection = await OpenAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await using (var deleteDocument = new NpgsqlCommand(
                $"DELETE FROM \"ExpenseTransactions\" WHERE \"Id\" = {expenseId}", connection, transaction))
            {
                // سند اول پاک می‌شود — دقیقاً همان ترتیبی که تریگرِ غیرِ Deferred رد می‌کرد.
                await deleteDocument.ExecuteNonQueryAsync();
            }

            await using (var deleteLedger = new NpgsqlCommand(
                $"DELETE FROM \"LedgerEntries\" WHERE \"SourceType\" = 'Expense' AND \"SourceId\" = {expenseId}",
                connection, transaction))
            {
                await deleteLedger.ExecuteNonQueryAsync();
            }

            await transaction.CommitAsync();
        }

        Assert.Equal(0L, await ScalarCountAsync(
            $"SELECT COUNT(*) FROM \"ExpenseTransactions\" WHERE \"Id\" = {expenseId}"));
        Assert.Equal(0L, await ScalarCountAsync(
            $"SELECT COUNT(*) FROM \"LedgerEntries\" WHERE \"SourceType\" = 'Expense' AND \"SourceId\" = {expenseId}"));
    }

    /// <summary>سندی که هرگز پست نشده، آزادانه پاک می‌شود.</summary>
    [Fact]
    public async Task DeletingADocumentWithNoLedgerRow_IsAllowed()
    {
        RequirePostgres();
        var expenseTypeId = await EnsureExpenseTypeAsync();

        int expenseId;
        await using (var db = fixture.CreateDbContext())
        {
            var expense = new ExpenseTransaction
            {
                ExpenseTypeId = expenseTypeId,
                ExpenseDate = new DateTime(2026, 3, 11, 0, 0, 0, DateTimeKind.Utc),
                Description = "PTG فاز ۸ — سندِ بدونِ دفتر کل",
                Amount = 10m,
                Currency = "USD",
                AmountUsd = 10m,
            };
            db.ExpenseTransactions.Add(expense);
            await db.SaveChangesAsync();
            expenseId = expense.Id;
        }

        await ExecuteAsync($"DELETE FROM \"ExpenseTransactions\" WHERE \"Id\" = {expenseId}");

        Assert.Equal(0L, await ScalarCountAsync(
            $"SELECT COUNT(*) FROM \"ExpenseTransactions\" WHERE \"Id\" = {expenseId}"));
    }

    // ------------------------------------------------------------------
    // ۴ — ضدِ رانش: فهرستِ SourceTypeهای پرداخت باید کاملِ enum بماند
    // ------------------------------------------------------------------

    /// <summary>
    /// تریگرِ پرداخت‌ها فهرستِ ثابتی از نام‌های <see cref="PaymentKind"/> دارد. اگر عضوِ
    /// تازه‌ای به enum اضافه شود بدون به‌روزکردنِ آن فهرست، همان نوعِ پرداخت بی‌محافظ
    /// می‌ماند — این آزمون همان لحظه قرمز می‌شود، نه در تولید.
    /// </summary>
    [Fact]
    public async Task PaymentGuard_CoversEveryPaymentKind()
    {
        RequirePostgres();

        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT prosrc FROM pg_proc WHERE proname = 'ptg_guard_payment_ledger_delete'", connection);
        var body = (string?)await command.ExecuteScalarAsync();

        Assert.False(string.IsNullOrWhiteSpace(body));

        var missing = Enum.GetNames<PaymentKind>()
            .Where(name => !body!.Contains($"'{name}'", StringComparison.Ordinal))
            .ToList();

        Assert.True(
            missing.Count == 0,
            $"این PaymentKindها در تریگرِ محافظ نیستند: {string.Join(", ", missing)}");
    }

    // ------------------------------------------------------------------
    // ۵ — استثنای مستند: بارگیری عمداً بی‌محافظ است
    // ------------------------------------------------------------------

    /// <summary>
    /// <c>LoadingController.BulkDelete</c> از قصد سطرِ اصلیِ دفتر کل را نگه می‌دارد و فقط
    /// بارگیریِ اشتباه را پاک می‌کند. اگر روزی تریگری روی این جدول گذاشته شود، آن جریانِ
    /// کاری بی‌صدا می‌شکند — پس نبودنِ تریگر این‌جا یک تصمیم است، نه فراموشی.
    /// </summary>
    [Fact]
    public async Task LoadingRegisters_IsDeliberatelyLeftUnguarded()
    {
        RequirePostgres();

        var triggers = await ScalarCountAsync(
            """
            SELECT COUNT(*) FROM pg_trigger t
            JOIN pg_class c ON c.oid = t.tgrelid
            WHERE c.relname = 'LoadingRegisters' AND t.tgname LIKE 'ptg_guard_%'
            """);

        Assert.Equal(0L, triggers);
    }
}
