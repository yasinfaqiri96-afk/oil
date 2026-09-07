using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Expenses;
using PTGOilSystem.Web.Services.Ledger;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// یک مصرف، یک قاعده. پیش از تمرکز، نُه مسیرِ مستقل هرکدام <c>LedgerPostingRequest</c> خودشان
/// را می‌ساختند: سه قاعدهٔ متفاوت برای <c>Side</c> و فیلدهای طرف‌حسابِ متفاوت در هر مسیر.
/// این تست‌ها قاعدهٔ واحد و کاملِ فیلدها را pin می‌کنند.
/// </summary>
public sealed class ExpenseLedgerPosterTests
{
    private static readonly DateTime ExpenseDate = new(2026, 5, 20);

    private static ApplicationDbContext CreateDb()
        => new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static ExpenseLedgerPoster CreatePoster(ApplicationDbContext db)
        => new(new LedgerPostingService(db));

    private static ExpenseTransaction Expense(Action<ExpenseTransaction>? configure = null)
    {
        var expense = new ExpenseTransaction
        {
            Id = 42,
            ExpenseTypeId = 1,
            ExpenseDate = ExpenseDate,
            Amount = 900m,
            Currency = "USD",
            AppliedFxRateToUsd = 1m,
            AmountUsd = 900m,
            Description = "کرایه راننده"
        };
        configure?.Invoke(expense);
        return expense;
    }

    private static ExpenseType Type(string code = "FRT", string? name = "Freight")
        => new() { Id = 1, Code = code, Name = name ?? "", NamePersian = "کرایه" };

    // ---------------------------------------------------------------- جهت

    [Fact]
    public void Expense_Without_A_Counterparty_Is_A_Debit()
    {
        using var db = CreateDb();
        Assert.Equal(LedgerSide.Debit, CreatePoster(db).ResolveSide(Expense()));
    }

    [Fact]
    public void Expense_Owed_To_A_Service_Provider_Is_A_Credit()
    {
        using var db = CreateDb();
        var expense = Expense(e => e.ServiceProviderId = 7);
        Assert.Equal(LedgerSide.Credit, CreatePoster(db).ResolveSide(expense));
    }

    /// <summary>
    /// قاعدهٔ قبلی فقط <c>ServiceProviderId</c> را می‌دید، پس کرایهٔ راننده <c>Debit</c>
    /// می‌شد — یعنی تعهدی که به راننده داریم اصلاً بدهی شمرده نمی‌شد.
    /// </summary>
    [Fact]
    public void Expense_Owed_To_A_Driver_Is_A_Credit_Too()
    {
        using var db = CreateDb();
        var expense = Expense(e => e.DriverId = 3);
        Assert.Equal(LedgerSide.Credit, CreatePoster(db).ResolveSide(expense));
    }

    // ------------------------------------------------- فیلدهای طرف‌حساب

    /// <summary>
    /// ریشهٔ «مصرف در گزارش طلب و بدهی دیده نمی‌شود»: از نُه مسیر، فقط دو مسیر
    /// <c>DriverId</c> را روی سطر دفتر می‌نوشتند. حالا از خودِ سند خوانده می‌شود.
    /// </summary>
    [Fact]
    public void Driver_Always_Reaches_The_Ledger_Row()
    {
        using var db = CreateDb();
        var expense = Expense(e => e.DriverId = 3);

        var entry = CreatePoster(db).Post(new ExpenseLedgerRequest
        {
            Expense = expense,
            ExpenseType = Type()
        });

        Assert.Equal(3, entry.DriverId);
        Assert.Equal(LedgerSide.Credit, entry.Side);
    }

    [Fact]
    public void Carries_Every_Reference_From_The_Expense_Itself()
    {
        using var db = CreateDb();
        var expense = Expense(e =>
        {
            e.ContractId = 11;
            e.ShipmentId = 12;
            e.ServiceProviderId = 13;
            e.DriverId = 14;
        });

        var entry = CreatePoster(db).Post(new ExpenseLedgerRequest
        {
            Expense = expense,
            ExpenseType = Type()
        });

        Assert.Equal(11, entry.ContractId);
        Assert.Equal(12, entry.ShipmentId);
        Assert.Equal(13, entry.ServiceProviderId);
        Assert.Equal(14, entry.DriverId);
        Assert.Equal("Expense", entry.SourceType);
        Assert.Equal(expense.Id, entry.SourceId);
        Assert.Equal(expense.AmountUsd, entry.AmountUsd);
        Assert.Equal(expense.Amount, entry.SourceAmount);
        Assert.Equal(expense.Currency, entry.SourceCurrencyCode);
        Assert.Equal(SystemCurrency.BaseCurrencyCode, entry.Currency);
    }

    /// <summary>
    /// مصرف هرگز مشتری/تأمین‌کننده/کارمند/شریک تعیین نمی‌کند. اگر سطرِ موجود آن‌ها را
    /// داشته باشد (سندِ ویرایش‌شده)، باید دست‌نخورده بماند.
    /// </summary>
    [Fact]
    public void Fields_The_Expense_Path_Never_Owns_Are_Carried_From_The_Existing_Row()
    {
        using var db = CreateDb();
        var existing = new LedgerEntry
        {
            CustomerId = 1,
            SupplierId = 2,
            EmployeeId = 3,
            PartnerId = 4,
            ViaSarrafGroupId = Guid.NewGuid(),
            AppliedCurrencyPerUsdRate = 70m
        };

        var entry = CreatePoster(db).Post(new ExpenseLedgerRequest
        {
            Expense = Expense(),
            ExpenseType = Type(),
            CarryFrom = existing
        });

        Assert.Equal(1, entry.CustomerId);
        Assert.Equal(2, entry.SupplierId);
        Assert.Equal(3, entry.EmployeeId);
        Assert.Equal(4, entry.PartnerId);
        Assert.Equal(existing.ViaSarrafGroupId, entry.ViaSarrafGroupId);
        Assert.Equal(70m, entry.AppliedCurrencyPerUsdRate);
    }

    [Fact]
    public void Without_An_Existing_Row_Those_Fields_Stay_Empty()
    {
        using var db = CreateDb();

        var entry = CreatePoster(db).Post(new ExpenseLedgerRequest
        {
            Expense = Expense(),
            ExpenseType = Type()
        });

        Assert.Null(entry.CustomerId);
        Assert.Null(entry.SupplierId);
        Assert.Null(entry.EmployeeId);
        Assert.Null(entry.PartnerId);
        Assert.Null(entry.ViaSarrafGroupId);
    }

    // ------------------------------------------------------------ نرخ ارز

    [Fact]
    public void Fx_Rate_Date_Falls_Back_To_The_Expense_Date()
    {
        using var db = CreateDb();

        var entry = CreatePoster(db).Post(new ExpenseLedgerRequest
        {
            Expense = Expense(),
            ExpenseType = Type()
        });

        Assert.Equal(ExpenseDate, entry.AppliedFxRateDate);
    }

    [Fact]
    public void An_Explicit_Fx_Rate_Date_Wins()
    {
        using var db = CreateDb();
        var effective = new DateTime(2026, 5, 19);

        var entry = CreatePoster(db).Post(new ExpenseLedgerRequest
        {
            Expense = Expense(),
            ExpenseType = Type(),
            FxRateDate = effective,
            FxRateSource = "Daily rate"
        });

        Assert.Equal(effective, entry.AppliedFxRateDate);
        Assert.Equal("Daily rate", entry.AppliedFxRateSource);
    }

    // ---------------------------------------------------------------- متن

    [Fact]
    public void Description_Uses_The_Persian_Type_Name_And_Appends_The_Note()
    {
        Assert.Equal(
            "ثبت هزینه کرایه - کرایه راننده",
            ExpenseLedgerPoster.BuildDescription(Type(), Expense()));
    }

    [Fact]
    public void Description_Without_A_Note_Is_Just_The_Type_Name()
    {
        Assert.Equal(
            "ثبت هزینه کرایه",
            ExpenseLedgerPoster.BuildDescription(Type(), Expense(e => e.Description = null)));
    }

    [Fact]
    public void Reference_Falls_Back_To_Exp_Prefix_When_The_Type_Has_No_Code()
    {
        Assert.Equal(
            "EXP-42",
            ExpenseLedgerPoster.BuildReference(Type(code: ""), Expense(e => e.Description = null)));
    }

    [Fact]
    public void Reference_Is_Truncated_To_The_Column_Width()
    {
        var reference = ExpenseLedgerPoster.BuildReference(
            Type(),
            Expense(e => e.Description = new string('x', 500)));

        Assert.Equal(200, reference.Length);
        Assert.StartsWith("FRT-42 | ", reference);
    }

    [Fact]
    public void Explicit_Text_Overrides_The_Shared_Builders()
    {
        using var db = CreateDb();

        var entry = CreatePoster(db).Post(new ExpenseLedgerRequest
        {
            Expense = Expense(),
            Description = "مصارف گمرکی",
            Reference = "CUSTOMS-1"
        });

        Assert.Equal("مصارف گمرکی", entry.Description);
        Assert.Equal("CUSTOMS-1", entry.Reference);
    }

    [Fact]
    public void Refuses_To_Post_Without_Any_Text_Source()
    {
        using var db = CreateDb();
        var poster = CreatePoster(db);

        Assert.Throws<ArgumentException>(() => poster.Post(new ExpenseLedgerRequest
        {
            Expense = Expense()
        }));
    }

    // -------------------------------------------------------------- ویرایش

    [Fact]
    public void Apply_Updates_The_Existing_Row_Instead_Of_Adding_One()
    {
        using var db = CreateDb();
        var poster = CreatePoster(db);
        var entry = poster.Post(new ExpenseLedgerRequest
        {
            Expense = Expense(),
            ExpenseType = Type()
        });
        db.SaveChanges();

        var edited = Expense(e =>
        {
            e.AmountUsd = 1500m;
            e.Amount = 1500m;
            e.DriverId = 9;
        });
        poster.Apply(entry, new ExpenseLedgerRequest
        {
            Expense = edited,
            ExpenseType = Type(),
            CarryFrom = entry
        });
        db.SaveChanges();

        Assert.Single(db.LedgerEntries);
        Assert.Equal(1500m, entry.AmountUsd);
        Assert.Equal(9, entry.DriverId);
        Assert.Equal(LedgerSide.Credit, entry.Side);
    }
}
