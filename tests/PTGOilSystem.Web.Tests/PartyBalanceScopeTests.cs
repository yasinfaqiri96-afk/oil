using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.PartyStatements;
using PTGOilSystem.Web.Models.Reports;
using PTGOilSystem.Web.Services.CompanyFlow;
using PTGOilSystem.Web.Services.Parties;
using PTGOilSystem.Web.Services.PartyStatements;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// صفحه‌هایی مثل «مانده تأمین‌کنندگان»، «مانده مشتریان» و فهرست تأمین‌کنندگان فقط یک نوع
/// طرف‌حساب را نشان می‌دهند، ولی تا پیش از این ماندهٔ صراف، کارمند و شریک را هم می‌ساختند
/// و بعد دور می‌ریختند — همان چیزی که تعداد کوئری این صفحه‌ها را به بالای ۳۰ می‌برد.
///
/// حالا نوع خواسته‌شده پاس می‌شود و منابعی که آن نوع را نمی‌سازند اصلاً خوانده نمی‌شوند.
/// این تست قفل می‌کند که کم‌خواندن، «جور دیگر حساب کردن» نشود: ردیفِ هر نوع باید مو‌به‌مو
/// همان ردیفی باشد که فراخوانیِ بدون محدودیت می‌ساخت.
/// </summary>
public sealed class PartyBalanceScopeTests
{
    private static PartyBalanceReadService NewService(ApplicationDbContext db)
        => new(db,
            new PartyStatementPolicyResolver(),
            new CompanyFlowDirectionResolver(),
            new CompanyFlowBalanceService(),
            new PartyDirectory(db));

    private static ApplicationDbContext NewDb()
        => new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    /// <summary>یک مشتری، یک تأمین‌کننده، یک صراف و یک کارمند — هرکدام با حرکت واقعی.</summary>
    private static async Task SeedAsync(ApplicationDbContext db)
    {
        db.Customers.Add(new Customer { Id = 1, Name = "Customer One" });
        db.Suppliers.Add(new Supplier { Id = 2, Name = "Supplier Two" });
        db.Sarrafs.Add(new Sarraf { Id = 3, Name = "Sarraf Three", IsActive = true });
        db.Employees.Add(new Employee { Id = 4, FullName = "Employee Four" });

        var day = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc);

        db.LedgerEntries.AddRange(
            new LedgerEntry { Id = 1, EntryDate = day, CustomerId = 1, Side = LedgerSide.Debit, AmountUsd = 500m, SourceType = "Sale" },
            new LedgerEntry { Id = 2, EntryDate = day.AddDays(1), CustomerId = 1, Side = LedgerSide.Credit, AmountUsd = 200m, SourceType = "Payment" },
            new LedgerEntry { Id = 3, EntryDate = day, SupplierId = 2, Side = LedgerSide.Credit, AmountUsd = 900m, SourceType = "Purchase" },
            new LedgerEntry { Id = 4, EntryDate = day.AddDays(2), SupplierId = 2, Side = LedgerSide.Debit, AmountUsd = 300m, SourceType = "Payment" });

        db.PaymentTransactions.Add(new PaymentTransaction
        {
            Id = 1,
            SarrafId = 3,
            PaymentDate = day,
            Direction = PaymentDirection.Out,
            AmountUsd = 120m
        });

        db.EmployeeSalaryTransactions.Add(new EmployeeSalaryTransaction
        {
            Id = 1,
            EmployeeId = 4,
            TransactionDate = day,
            TransactionType = EmployeeSalaryTransactionType.SalaryAccrual,
            AmountUsd = 80m
        });

        await db.SaveChangesAsync();
    }

    private static async Task<IReadOnlyList<PartyBalanceSnapshot>> BalancesAsync(
        params PartyStatementPartyType[] types)
    {
        await using var db = NewDb();
        await SeedAsync(db);
        return await NewService(db).GetBalancesAsync(
            new ManagementReportFilterViewModel(),
            CancellationToken.None,
            types.Length == 0 ? null : types);
    }

    [Theory]
    [InlineData(PartyStatementPartyType.Customer)]
    [InlineData(PartyStatementPartyType.Supplier)]
    [InlineData(PartyStatementPartyType.Sarraf)]
    [InlineData(PartyStatementPartyType.Employee)]
    public async Task Narrowing_To_One_Type_Returns_Exactly_The_Same_Rows(PartyStatementPartyType type)
    {
        var all = await BalancesAsync();
        var narrowed = await BalancesAsync(type);

        var expected = all.Where(row => row.PartyType == type).ToList();
        Assert.NotEmpty(expected);
        Assert.Equal(expected.Count, narrowed.Count);

        foreach (var (want, got) in expected.Zip(narrowed))
        {
            // هر عددِ ردیف، نه فقط ماندهٔ پایانی.
            Assert.Equal(want, got);
        }
    }

    [Fact]
    public async Task Narrowing_Drops_Only_The_Types_Left_Out()
    {
        var all = await BalancesAsync();
        var withoutPartner = await BalancesAsync(
            Enum.GetValues<PartyStatementPartyType>()
                .Where(type => type != PartyStatementPartyType.Partner)
                .ToArray());

        // در این دادهٔ آزمایشی اصلاً شریکی نیست، پس خروجی باید کاملاً یکسان بماند.
        Assert.Equal(all, withoutPartner);
    }

    [Fact]
    public async Task An_Empty_Type_List_Means_No_Rows_Not_All_Rows()
    {
        await using var db = NewDb();
        await SeedAsync(db);

        var rows = await NewService(db).GetBalancesAsync(
            new ManagementReportFilterViewModel(),
            CancellationToken.None,
            Array.Empty<PartyStatementPartyType>());

        Assert.Empty(rows);
    }

    [Fact]
    public async Task Null_Still_Means_Every_Type()
    {
        var all = await BalancesAsync();

        Assert.Contains(all, row => row.PartyType == PartyStatementPartyType.Customer);
        Assert.Contains(all, row => row.PartyType == PartyStatementPartyType.Supplier);
        Assert.Contains(all, row => row.PartyType == PartyStatementPartyType.Sarraf);
        Assert.Contains(all, row => row.PartyType == PartyStatementPartyType.Employee);
    }
}
