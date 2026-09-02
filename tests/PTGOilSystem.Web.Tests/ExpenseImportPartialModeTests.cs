using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PTGOilSystem.Web.Controllers;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Helpers;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.Expenses;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Audit;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// PTG-P2-02 — «ورودِ فقط سطرهای سالم»، بدون از دست دادنِ ایمنی.
///
/// قاعده‌ای که این تست‌ها نگه می‌دارند: حالتِ جزئی فقط با انتخابِ صریحِ کاربر فعال می‌شود،
/// هیچ سطری بی‌صدا رد نمی‌شود، و <b>ایمپورتِ دوبارهٔ همان فایل مصارف را دو برابر نمی‌کند</b> —
/// که بدونِ کلیدِ هویت، خطرناک‌ترین عارضهٔ ورودِ جزئی بود.
/// </summary>
public sealed class ExpenseImportPartialModeTests
{
    private static DbContextOptions<ApplicationDbContext> NewDbOptions()
        => new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase($"expense-import-{Guid.NewGuid():N}")
            .Options;

    private static ExpensesController BuildController(ApplicationDbContext db)
        => new(db, new AuditService(db), NullLogger<ExpensesController>.Instance)
        {
            TempData = new TempDataDictionary(new DefaultHttpContext(), new NullTempDataProvider()),
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

    private static void SeedReferenceData(ApplicationDbContext db)
    {
        db.Currencies.Add(new Currency { Id = 1, Code = "USD", Name = "US Dollar", Symbol = "$", IsActive = true });
        db.ExpenseTypes.Add(new ExpenseType { Id = 1, Code = "PORT", Name = "Port", NamePersian = "هزینه بندری", IsActive = true });
    }

    private static ExpenseImportRowViewModel Row(
        int excelRow,
        string date = "2026-04-25",
        string type = "هزینه بندری",
        string amount = "1200",
        string currency = "USD",
        string? description = "تخلیه بندر")
        => new()
        {
            ExcelRowNumber = excelRow,
            ExpenseDateText = date,
            ExpenseTypeName = type,
            AmountText = amount,
            Currency = currency,
            Description = description,
        };

    [Fact]
    public async Task All_Or_Nothing_Is_Still_The_Default()
    {
        var options = NewDbOptions();
        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        await db.SaveChangesAsync();

        var controller = BuildController(db);

        var result = await controller.ImportConfirm(new ExpenseImportViewModel
        {
            Rows = [Row(2), Row(3, type: "یک نوع ناموجود")],
        });

        // بدون انتخابِ صریحِ کاربر، یک سطر خراب کلِ فایل را متوقف می‌کند — رفتار قبلی.
        Assert.IsType<ViewResult>(result);
        Assert.Empty(db.ExpenseTransactions);
        Assert.Empty(db.LedgerEntries);
    }

    [Fact]
    public async Task Partial_Mode_Imports_Only_The_Healthy_Rows_And_Reports_The_Rest()
    {
        var options = NewDbOptions();
        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        await db.SaveChangesAsync();

        var controller = BuildController(db);

        var result = await controller.ImportConfirm(new ExpenseImportViewModel
        {
            ImportValidRowsOnly = true,
            Rows = [Row(2), Row(3, type: "یک نوع ناموجود"), Row(4, amount: "2500", description: "کرایه")],
        });

        Assert.IsType<RedirectToActionResult>(result);

        var expenses = await db.ExpenseTransactions.ToListAsync();
        Assert.Equal(2, expenses.Count);
        Assert.All(expenses, e => Assert.NotNull(e.ImportUniqueKey));

        // هر سطرِ پذیرفته‌شده سند دفتر کلِ خودش را دارد — هیچ ثبتِ نیم‌بندی نمی‌ماند.
        Assert.Equal(2, await db.LedgerEntries.CountAsync(l => l.SourceType == "Expense"));
    }

    [Fact]
    public async Task Re_Importing_The_Same_Workbook_Does_Not_Duplicate_Anything()
    {
        var options = NewDbOptions();
        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        await db.SaveChangesAsync();

        var rows = new List<ExpenseImportRowViewModel> { Row(2), Row(3, amount: "2500", description: "کرایه") };

        Assert.IsType<RedirectToActionResult>(await BuildController(db).ImportConfirm(new ExpenseImportViewModel
        {
            ImportValidRowsOnly = true,
            Rows = [Row(2), Row(3, amount: "2500", description: "کرایه")],
        }));
        Assert.Equal(2, await db.ExpenseTransactions.CountAsync());

        // همان فایل، بار دوم: همه‌چیز تکراری تشخیص داده می‌شود و چیزی اضافه نمی‌شود.
        var second = await BuildController(db).ImportConfirm(new ExpenseImportViewModel
        {
            ImportValidRowsOnly = true,
            Rows = [Row(2), Row(3, amount: "2500", description: "کرایه")],
        });

        Assert.IsType<ViewResult>(second);
        Assert.Equal(2, await db.ExpenseTransactions.CountAsync());
        Assert.Equal(2, await db.LedgerEntries.CountAsync(l => l.SourceType == "Expense"));
        _ = rows;
    }

    [Fact]
    public async Task A_Corrected_Row_Is_Added_On_The_Second_Pass_Without_Touching_The_First_Ones()
    {
        var options = NewDbOptions();
        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        await db.SaveChangesAsync();

        await BuildController(db).ImportConfirm(new ExpenseImportViewModel
        {
            ImportValidRowsOnly = true,
            Rows = [Row(2), Row(3, type: "یک نوع ناموجود")],
        });
        Assert.Equal(1, await db.ExpenseTransactions.CountAsync());

        // کاربر سطر خراب را اصلاح می‌کند و همان فایل را دوباره می‌فرستد.
        await BuildController(db).ImportConfirm(new ExpenseImportViewModel
        {
            ImportValidRowsOnly = true,
            Rows = [Row(2), Row(3, amount: "999", description: "اصلاح‌شده")],
        });

        Assert.Equal(2, await db.ExpenseTransactions.CountAsync());
        Assert.Equal(2, await db.LedgerEntries.CountAsync(l => l.SourceType == "Expense"));
    }

    /// <summary>
    /// دو سطرِ واقعاً جدا با همان مبلغ در همان روز باید هر دو ثبت شوند — وگرنه محافظِ
    /// ضدتکراری دادهٔ درست را می‌خورد.
    /// </summary>
    [Fact]
    public async Task Two_Genuinely_Repeated_Rows_In_One_File_Both_Survive()
    {
        var options = NewDbOptions();
        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        await db.SaveChangesAsync();

        await BuildController(db).ImportConfirm(new ExpenseImportViewModel
        {
            ImportValidRowsOnly = true,
            Rows = [Row(2), Row(3)],
        });

        Assert.Equal(2, await db.ExpenseTransactions.CountAsync());
    }

    /// <summary>
    /// P1-04 روی همین مسیر: ارقام فارسی و لاتین یک هویت‌اند، پس فایلی که یک‌بار با
    /// «۱۲۰۰» و بار بعد با «1200» می‌آید، دو مصرف نمی‌سازد.
    /// </summary>
    [Fact]
    public void Persian_And_Latin_Digits_Produce_The_Same_Row_Identity()
    {
        var latin = ExpenseImportKey.Build(
            1, new DateTime(2026, 4, 25), 1200m, "USD", null, "کرایه 1200");
        var persian = ExpenseImportKey.Build(
            1, new DateTime(2026, 4, 25), 1200m, "USD", null, "کرایه ۱۲۰۰");
        var arabic = ExpenseImportKey.Build(
            1, new DateTime(2026, 4, 25), 1200m, "USD", null, "کرایه ١٢٠٠");

        Assert.NotNull(latin);
        Assert.Equal(latin, persian);
        Assert.Equal(latin, arabic);

        // ولی شرحِ واقعاً متفاوت همچنان هویتِ متفاوت است.
        Assert.NotEqual(
            latin,
            ExpenseImportKey.Build(1, new DateTime(2026, 4, 25), 1200m, "USD", null, "کرایه دیگر"));
    }

    [Fact]
    public void A_Row_Without_A_Usable_Identity_Gets_No_Key()
    {
        Assert.Null(ExpenseImportKey.Build(null, new DateTime(2026, 4, 25), 100m, "USD", null, "x"));
        Assert.Null(ExpenseImportKey.Build(1, null, 100m, "USD", null, "x"));
        Assert.Null(ExpenseImportKey.Build(1, new DateTime(2026, 4, 25), null, "USD", null, "x"));
    }

    private sealed class NullTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object?> LoadTempData(HttpContext context) => new Dictionary<string, object?>();

        public void SaveTempData(HttpContext context, IDictionary<string, object?> values)
        {
        }
    }
}
