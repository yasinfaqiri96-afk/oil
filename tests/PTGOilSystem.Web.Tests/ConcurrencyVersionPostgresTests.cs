using Microsoft.EntityFrameworkCore;
using Npgsql;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services.OperationalPeriod;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// PTG-P1-05 — اثباتِ محافظِ «Lost Update» روی PostgreSQL واقعی، با دو DbContext مستقل.
///
/// این همان سناریویی است که گزارشِ فاز قبل خواسته بود: دو کاربر یک سند مالی را باز
/// می‌کنند، اولی ذخیره می‌کند، دومی روی نسخهٔ کهنه ذخیره می‌کند و باید **رد شود**.
/// </summary>
[Trait("Category", "PostgreSql")]
[Trait("Category", "Integration")]
[Collection(ConcurrencyVersionPostgresCollection.CollectionName)]
public sealed class ConcurrencyVersionPostgresTests(ConcurrencyVersionPostgresFixture fixture)
{
    /// <summary>
    /// این تست‌ها عمداً روی PostgreSQL واقعی اصرار دارند: شکستِ قبلیِ <c>xmin</c> فقط
    /// همان‌جا دیده شد. اگر PostgreSQL نباشد، تست ساکت سبز نمی‌شود.
    /// </summary>
    private void RequirePostgres()
        => Assert.True(fixture.Available, $"PTG-P1-05 به PostgreSQL واقعی نیاز دارد: {fixture.UnavailableReason}");

    private static PaymentTransaction NewPayment(decimal amount = 1_000m) => new()
    {
        PaymentDate = new DateTime(2026, 3, 10, 0, 0, 0, DateTimeKind.Utc),
        Direction = PaymentDirection.Out,
        PaymentKind = PaymentKind.SupplierPayment,
        Amount = amount,
        Currency = "USD",
        AmountUsd = amount,
        Description = "PTG-P1-05 proof",
    };

    [Fact]
    public async Task New_Row_Starts_At_Version_One()
    {
        RequirePostgres();

        int id;
        await using (var db = fixture.CreateDbContext())
        {
            var payment = NewPayment();
            db.PaymentTransactions.Add(payment);
            await db.SaveChangesAsync();
            id = payment.Id;
            Assert.Equal(1L, payment.Version);
        }

        await using var verify = fixture.CreateDbContext();
        Assert.Equal(1L, await verify.PaymentTransactions.Where(p => p.Id == id).Select(p => p.Version).SingleAsync());
    }

    [Fact]
    public async Task Every_Successful_Update_Increments_Version_By_Exactly_One()
    {
        RequirePostgres();

        int id;
        await using (var seed = fixture.CreateDbContext())
        {
            var payment = NewPayment();
            seed.PaymentTransactions.Add(payment);
            await seed.SaveChangesAsync();
            id = payment.Id;
        }

        for (var expected = 2L; expected <= 4L; expected++)
        {
            await using var db = fixture.CreateDbContext();
            var payment = await db.PaymentTransactions.SingleAsync(p => p.Id == id);
            payment.Description = $"edit {expected}";
            await db.SaveChangesAsync();
            Assert.Equal(expected, payment.Version);
        }

        await using var verify = fixture.CreateDbContext();
        Assert.Equal(4L, await verify.PaymentTransactions.Where(p => p.Id == id).Select(p => p.Version).SingleAsync());
    }

    /// <summary>هستهٔ P1-05: نوشتنِ کهنه باید شکست بخورد، نه اینکه بی‌صدا برنده شود.</summary>
    [Fact]
    public async Task Stale_Second_Writer_Is_Rejected_With_Concurrency_Conflict()
    {
        RequirePostgres();

        int id;
        await using (var seed = fixture.CreateDbContext())
        {
            var payment = NewPayment();
            seed.PaymentTransactions.Add(payment);
            await seed.SaveChangesAsync();
            id = payment.Id;
        }

        await using var contextA = fixture.CreateDbContext();
        await using var contextB = fixture.CreateDbContext();

        var rowForA = await contextA.PaymentTransactions.SingleAsync(p => p.Id == id);
        var rowForB = await contextB.PaymentTransactions.SingleAsync(p => p.Id == id);

        // هر دو کاربر همان نسخه را دیده‌اند.
        Assert.Equal(rowForA.Version, rowForB.Version);

        rowForA.Amount = 2_000m;
        rowForA.AmountUsd = 2_000m;
        await contextA.SaveChangesAsync();

        rowForB.Amount = 3_000m;
        rowForB.AmountUsd = 3_000m;
        var conflict = await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => contextB.SaveChangesAsync());

        // و همان استثنا به پیامِ دریِ آمادهٔ فاز قبل ترجمه می‌شود.
        Assert.Equal(BusinessRuleExceptionFilter.ConcurrencyMessage, BusinessRuleExceptionFilter.Translate(conflict));

        await using var verify = fixture.CreateDbContext();
        var saved = await verify.PaymentTransactions.SingleAsync(p => p.Id == id);
        Assert.Equal(2_000m, saved.Amount);   // نوشتهٔ کاربر اول
        Assert.Equal(2L, saved.Version);      // و فقط یک‌بار جلو رفت
    }

    /// <summary>حذفِ کهنه هم باید رد شود، وگرنه ویرایشِ کاربر دیگر بی‌صدا نابود می‌شد.</summary>
    [Fact]
    public async Task Stale_Delete_Is_Rejected()
    {
        RequirePostgres();

        int id;
        await using (var seed = fixture.CreateDbContext())
        {
            var payment = NewPayment();
            seed.PaymentTransactions.Add(payment);
            await seed.SaveChangesAsync();
            id = payment.Id;
        }

        await using var contextA = fixture.CreateDbContext();
        await using var contextB = fixture.CreateDbContext();

        var rowForA = await contextA.PaymentTransactions.SingleAsync(p => p.Id == id);
        var rowForB = await contextB.PaymentTransactions.SingleAsync(p => p.Id == id);

        rowForA.Description = "کاربر اول";
        await contextA.SaveChangesAsync();

        contextB.PaymentTransactions.Remove(rowForB);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => contextB.SaveChangesAsync());

        await using var verify = fixture.CreateDbContext();
        Assert.True(await verify.PaymentTransactions.AnyAsync(p => p.Id == id));
    }

    /// <summary>
    /// ویرایشِ پشت‌سرهم (نه هم‌زمان) نباید هیچ اصطکاکی بسازد — وگرنه محافظ، کارِ روزمره را می‌شکند.
    /// </summary>
    [Fact]
    public async Task Sequential_Edits_By_Different_Contexts_Still_Succeed()
    {
        RequirePostgres();

        int id;
        await using (var seed = fixture.CreateDbContext())
        {
            var payment = NewPayment();
            seed.PaymentTransactions.Add(payment);
            await seed.SaveChangesAsync();
            id = payment.Id;
        }

        await using (var first = fixture.CreateDbContext())
        {
            var row = await first.PaymentTransactions.SingleAsync(p => p.Id == id);
            row.Description = "اول";
            await first.SaveChangesAsync();
        }

        await using (var second = fixture.CreateDbContext())
        {
            var row = await second.PaymentTransactions.SingleAsync(p => p.Id == id);
            row.Description = "دوم";
            await second.SaveChangesAsync();
        }

        await using var verify = fixture.CreateDbContext();
        var saved = await verify.PaymentTransactions.SingleAsync(p => p.Id == id);
        Assert.Equal("دوم", saved.Description);
        Assert.Equal(3L, saved.Version);
    }

    /// <summary>
    /// درسِ فازِ قبل، به‌صورت تست: هیچ ستونِ <c>xmin</c>ای نباید ساخته شده باشد و ستونِ
    /// نسخه باید یک <c>bigint</c> واقعی با پیش‌فرضِ ۱ باشد.
    /// </summary>
    [Fact]
    public async Task Version_Column_Is_A_Real_Bigint_And_No_Xmin_Column_Exists()
    {
        RequirePostgres();

        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();

        await using (var command = new NpgsqlCommand(
            "SELECT data_type, is_nullable, column_default FROM information_schema.columns " +
            "WHERE table_name = 'PaymentTransactions' AND column_name = 'Version'",
            connection))
        await using (var reader = await command.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync(), "ستون Version روی PaymentTransactions ساخته نشده است.");
            Assert.Equal("bigint", reader.GetString(0));
            Assert.Equal("NO", reader.GetString(1));
            Assert.Contains("1", reader.GetString(2));
        }

        // فقط جدول‌های خودِ برنامه؛ pg_catalog.pg_replication_slots ستونی به همین نام دارد
        // که مالِ خودِ PostgreSQL است و ربطی به schema ما ندارد.
        await using var xmin = new NpgsqlCommand(
            "SELECT COUNT(*) FROM information_schema.columns " +
            "WHERE column_name = 'xmin' AND table_schema = 'public'",
            connection);
        Assert.Equal(0L, Convert.ToInt64(await xmin.ExecuteScalarAsync()));
    }

    /// <summary>
    /// هر موجودیتی که <see cref="IVersionedEntity"/> را پیاده کرده باید در مدلِ EF واقعاً
    /// نشانهٔ هم‌زمانی داشته باشد و در دیتابیس یک ستونِ <c>bigint</c> واقعی. بدون این تست،
    /// افزودنِ موجودیتِ تازه به interface بی‌اثر می‌ماند و کسی متوجه نمی‌شد.
    /// </summary>
    [Fact]
    public async Task Every_Versioned_Entity_Has_A_Real_Concurrency_Token_Column()
    {
        RequirePostgres();

        await using var db = fixture.CreateDbContext();

        var versioned = db.Model.GetEntityTypes()
            .Where(e => typeof(IVersionedEntity).IsAssignableFrom(e.ClrType))
            .ToList();

        Assert.NotEmpty(versioned);

        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();

        foreach (var entityType in versioned)
        {
            var property = entityType.FindProperty(nameof(IVersionedEntity.Version));
            Assert.NotNull(property);
            Assert.True(
                property!.IsConcurrencyToken,
                $"{entityType.ClrType.Name}.Version نشانهٔ هم‌زمانی نیست.");

            var table = entityType.GetTableName();
            await using var command = new NpgsqlCommand(
                "SELECT data_type FROM information_schema.columns " +
                "WHERE table_schema = 'public' AND table_name = @t AND column_name = 'Version'",
                connection);
            command.Parameters.AddWithValue("t", table!);
            Assert.Equal("bigint", (string?)await command.ExecuteScalarAsync());
        }
    }

    /// <summary>
    /// همان اثباتِ «نویسندهٔ کهنه رد می‌شود»، این‌بار روی مصرف و قرارداد — تا معلوم شود
    /// الگو فقط روی یک جدول کار نمی‌کند.
    /// </summary>
    [Fact]
    public async Task Stale_Writer_Is_Rejected_On_Expense_And_Contract_Too()
    {
        RequirePostgres();

        int expenseId;
        int contractId;
        await using (var seed = fixture.CreateDbContext())
        {
            var expenseType = new ExpenseType
            {
                Code = $"P105{Random.Shared.Next(100000, 999999)}",
                Name = "PTG-P1-05 proof",
            };
            seed.ExpenseTypes.Add(expenseType);
            await seed.SaveChangesAsync();

            var expense = new ExpenseTransaction
            {
                ExpenseDate = new DateTime(2026, 3, 11, 0, 0, 0, DateTimeKind.Utc),
                ExpenseTypeId = expenseType.Id,
                Amount = 500m,
                Currency = "USD",
                AmountUsd = 500m,
                Description = "PTG-P1-05 proof",
            };
            var company = new Company
            {
                Code = $"P105{Random.Shared.Next(100000, 999999)}",
                Name = "PTG-P1-05 proof",
            };
            var product = new Product
            {
                Code = $"P105{Random.Shared.Next(100000, 999999)}",
                Name = "PTG-P1-05 proof",
            };
            seed.Companies.Add(company);
            seed.Products.Add(product);
            await seed.SaveChangesAsync();

            var contract = new Contract
            {
                ContractName = "PTG-P1-05 proof",
                ContractNumber = $"P105-{Guid.NewGuid():N}"[..20],
                ContractType = ContractType.Purchase,
                QuantityMt = 100m,
                ContractDate = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc),
                CompanyId = company.Id,
                ProductId = product.Id,
            };
            seed.ExpenseTransactions.Add(expense);
            seed.Contracts.Add(contract);
            await seed.SaveChangesAsync();
            expenseId = expense.Id;
            contractId = contract.Id;
        }

        await using (var a = fixture.CreateDbContext())
        await using (var b = fixture.CreateDbContext())
        {
            var forA = await a.ExpenseTransactions.SingleAsync(e => e.Id == expenseId);
            var forB = await b.ExpenseTransactions.SingleAsync(e => e.Id == expenseId);
            forA.Description = "کاربر اول";
            await a.SaveChangesAsync();
            forB.Description = "کاربر دوم";
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => b.SaveChangesAsync());
        }

        await using (var a = fixture.CreateDbContext())
        await using (var b = fixture.CreateDbContext())
        {
            var forA = await a.Contracts.SingleAsync(c => c.Id == contractId);
            var forB = await b.Contracts.SingleAsync(c => c.Id == contractId);
            forA.Notes = "کاربر اول";
            await a.SaveChangesAsync();
            forB.Notes = "کاربر دوم";
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => b.SaveChangesAsync());
        }
    }
}
