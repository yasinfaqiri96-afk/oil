using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PTGOilSystem.Web.Configuration;
using PTGOilSystem.Web.Controllers;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Security;
using PTGOilSystem.Web.Services.Accounting;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// مرحله ۱۰ — صفحه‌های سال مالی.
///
/// مهم‌ترین چیزهایی که باید تضمین شوند: صفحه فقط می‌خواند، عملیاتِ غیرمجاز اصلاً ساخته نمی‌شود،
/// هر عملیاتِ تغییردهنده POST + ضدجعل است، و هیچ فایل UI نامرتبطی لمس نشده است.
/// </summary>
public class FiscalYearUiTests
{
    // ── دسترسی ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Controller_Requires_ManageData_Policy()
    {
        var policies = typeof(FiscalYearsController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .Cast<AuthorizeAttribute>()
            .Select(a => a.Policy);

        Assert.Contains(AuthPolicies.ManageData, policies);
    }

    [Fact]
    public void CreateNextYear_Is_Post_Only_With_Antiforgery_And_AdminOnly()
    {
        var action = typeof(FiscalYearsController)
            .GetMethod(nameof(FiscalYearsController.CreateNextYear))!;

        Assert.NotEmpty(action.GetCustomAttributes(typeof(Microsoft.AspNetCore.Mvc.HttpPostAttribute), true));
        Assert.Empty(action.GetCustomAttributes(typeof(Microsoft.AspNetCore.Mvc.HttpGetAttribute), true));
        Assert.NotEmpty(action.GetCustomAttributes(typeof(Microsoft.AspNetCore.Mvc.ValidateAntiForgeryTokenAttribute), true));

        var policies = action
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .Cast<AuthorizeAttribute>()
            .Select(a => a.Policy);
        Assert.Contains(AuthPolicies.AdminOnly, policies);
    }

    [Fact]
    public void Read_Actions_Are_Get_Only()
    {
        foreach (var name in new[] { nameof(FiscalYearsController.Index), nameof(FiscalYearsController.Details) })
        {
            var action = typeof(FiscalYearsController).GetMethod(name)!;
            Assert.NotEmpty(action.GetCustomAttributes(typeof(Microsoft.AspNetCore.Mvc.HttpGetAttribute), true));
            Assert.Empty(action.GetCustomAttributes(typeof(Microsoft.AspNetCore.Mvc.HttpPostAttribute), true));
        }
    }

    // ── نمایش سال و دوره‌ها ────────────────────────────────────────────────────────

    [Fact]
    public async Task Index_Shows_Current_Year_Its_Dates_Periods_And_Current_Period()
    {
        await using var db = NewDb();
        SeedCompany(db);
        SeedYearWithPeriods(db, status: FiscalYearStatus.Open, isCurrent: true);
        await db.SaveChangesAsync();

        var model = await NewOverview(db).BuildIndexAsync(companyId: 1, canManage: true);

        Assert.Equal(1, model.SelectedCompanyId);
        var year = Assert.IsType<PTGOilSystem.Web.Models.Accounting.FiscalYearSummary>(model.CurrentYear);
        Assert.Equal("FY-2026", year.Name);
        Assert.Equal(new DateTime(2026, 1, 1), year.StartDate);
        Assert.Equal(new DateTime(2026, 12, 31), year.EndDate);
        Assert.Equal(FiscalYearStatus.Open, year.Status);
        Assert.Equal(3, year.PeriodCount);
        Assert.Equal(3, year.Periods.Count);
    }

    [Fact]
    public async Task Index_Marks_The_Period_That_Contains_Today_As_Current()
    {
        await using var db = NewDb();
        SeedCompany(db);
        var today = DateTime.UtcNow.Date;
        db.FiscalYears.Add(new FiscalYear
        {
            Id = 1,
            CompanyId = 1,
            Name = "FY-NOW",
            StartDate = today.AddMonths(-6),
            EndDate = today.AddMonths(6),
            Status = FiscalYearStatus.Open,
            IsCurrent = true
        });
        db.FiscalPeriods.Add(new FiscalPeriod
        {
            Id = 1,
            CompanyId = 1,
            FiscalYearId = 1,
            PeriodNumber = 1,
            Name = "P-PAST",
            StartDate = today.AddMonths(-6),
            EndDate = today.AddDays(-1),
            Status = FiscalPeriodStatus.HardLocked
        });
        db.FiscalPeriods.Add(new FiscalPeriod
        {
            Id = 2,
            CompanyId = 1,
            FiscalYearId = 1,
            PeriodNumber = 2,
            Name = "P-NOW",
            StartDate = today,
            EndDate = today.AddMonths(6),
            Status = FiscalPeriodStatus.Open
        });
        await db.SaveChangesAsync();

        var model = await NewOverview(db).BuildIndexAsync(companyId: 1, canManage: true);

        Assert.Equal("P-NOW", model.CurrentYear!.CurrentPeriod!.Name);
    }

    [Fact]
    public async Task Index_Surfaces_Related_Readiness_Findings_Only()
    {
        await using var db = NewDb();
        SeedCompany(db);
        SeedAccountsAndSettings(db);
        // شرکت تنظیمات دارد ولی هیچ سال مالی ندارد، و یکی از حساب‌های لازم غیرفعال است.
        // Readiness هر دو را می‌بیند؛ این صفحه فقط یافتهٔ سال مالی را نشان می‌دهد و بقیه جای
        // خودشان را در /accounting/readiness دارند.
        await db.SaveChangesAsync();
        db.Accounts.Single(a => a.Id == 900).IsActive = false;
        await db.SaveChangesAsync();

        var model = await NewOverview(db).BuildIndexAsync(companyId: 1, canManage: true);

        Assert.Contains(model.ReadinessFindings, f => f.Code == "NO_OPEN_FISCAL_YEAR");
        Assert.DoesNotContain(model.ReadinessFindings, f => f.Code == "REQUIRED_ACCOUNT_INACTIVE");
    }

    // ── وضعیت‌ها ──────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(FiscalPeriodStatus.Open)]
    [InlineData(FiscalPeriodStatus.SoftLocked)]
    [InlineData(FiscalPeriodStatus.HardLocked)]
    [InlineData(FiscalPeriodStatus.Closed)]
    public async Task Details_Reports_Every_Period_Lock_Status(FiscalPeriodStatus status)
    {
        await using var db = NewDb();
        SeedCompany(db);
        SeedYearWithPeriods(db, FiscalYearStatus.Open, isCurrent: true, periodStatus: status);
        await db.SaveChangesAsync();

        var model = await NewOverview(db).BuildDetailsAsync(fiscalYearId: 1, canManage: true);

        Assert.All(model!.Periods, p => Assert.Equal(status, p.Status));
    }

    [Theory]
    [InlineData(FiscalYearStatus.Draft)]
    [InlineData(FiscalYearStatus.Open)]
    [InlineData(FiscalYearStatus.Closing)]
    [InlineData(FiscalYearStatus.Closed)]
    [InlineData(FiscalYearStatus.Reopened)]
    public async Task Details_Reports_Every_Year_Status(FiscalYearStatus status)
    {
        await using var db = NewDb();
        SeedCompany(db);
        SeedYearWithPeriods(db, status, isCurrent: true);
        await db.SaveChangesAsync();

        var model = await NewOverview(db).BuildDetailsAsync(fiscalYearId: 1, canManage: true);

        Assert.Equal(status, model!.Year.Status);
    }

    [Fact]
    public async Task Details_Sums_Only_Posted_Journals_And_Reports_The_Difference()
    {
        await using var db = NewDb();
        SeedCompany(db);
        SeedYearWithPeriods(db, FiscalYearStatus.Open, isCurrent: true);
        SeedAccount(db);
        // سند متوازنِ پست‌شده
        SeedJournal(db, id: 1, periodId: 1, JournalEntryStatus.Posted, debit: 100m, credit: 100m);
        // سند نامتوازنِ Draft — نباید در جمع بیاید، چون هنوز اثر حسابداری ندارد.
        SeedJournal(db, id: 2, periodId: 1, JournalEntryStatus.Draft, debit: 55m, credit: 0m);
        await db.SaveChangesAsync();

        var model = await NewOverview(db).BuildDetailsAsync(fiscalYearId: 1, canManage: true);

        Assert.Equal(1, model!.TotalJournalCount);
        Assert.Equal(100m, model.TotalDebit);
        Assert.Equal(100m, model.TotalCredit);
        Assert.Equal(0m, model.Difference);
        Assert.True(model.IsBalanced);
    }

    [Fact]
    public async Task Details_Reports_An_Unbalanced_Period()
    {
        await using var db = NewDb();
        SeedCompany(db);
        SeedYearWithPeriods(db, FiscalYearStatus.Open, isCurrent: true);
        SeedAccount(db);
        SeedJournal(db, id: 1, periodId: 1, JournalEntryStatus.Posted, debit: 100m, credit: 40m);
        await db.SaveChangesAsync();

        var model = await NewOverview(db).BuildDetailsAsync(fiscalYearId: 1, canManage: true);

        Assert.False(model!.IsBalanced);
        Assert.Equal(60m, model.Difference);
    }

    // ── عملیات غیرمجاز ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Next_Year_Is_Not_Allowed_When_The_Company_Has_No_Fiscal_Year()
    {
        await using var db = NewDb();
        SeedCompany(db);
        await db.SaveChangesAsync();

        var proposal = await NewOverview(db).BuildNextYearProposalAsync(companyId: 1);

        Assert.False(proposal.IsAllowed);
        Assert.NotNull(proposal.BlockedReason);
    }

    [Fact]
    public async Task Next_Year_Is_Not_Allowed_When_The_Source_Year_Has_No_Periods()
    {
        await using var db = NewDb();
        SeedCompany(db);
        db.FiscalYears.Add(new FiscalYear
        {
            Id = 1,
            CompanyId = 1,
            Name = "FY-2026",
            StartDate = new DateTime(2026, 1, 1),
            EndDate = new DateTime(2026, 12, 31),
            Status = FiscalYearStatus.Open,
            IsCurrent = true
        });
        await db.SaveChangesAsync();

        var proposal = await NewOverview(db).BuildNextYearProposalAsync(companyId: 1);

        Assert.False(proposal.IsAllowed);
    }

    [Fact]
    public async Task Next_Year_Is_Not_Allowed_When_It_Already_Exists()
    {
        await using var db = NewDb();
        SeedCompany(db);
        SeedYearWithPeriods(db, FiscalYearStatus.Open, isCurrent: true);
        await db.SaveChangesAsync();

        var created = await NewProvisioning(db).CreateNextYearAsync(companyId: 1, actorUserId: 7);
        Assert.True(created.Succeeded);

        var again = await NewOverview(db).BuildNextYearProposalAsync(companyId: 1);
        Assert.False(again.IsAllowed);

        var secondAttempt = await NewProvisioning(db).CreateNextYearAsync(companyId: 1, actorUserId: 7);
        Assert.False(secondAttempt.Succeeded);
        Assert.Equal(2, await db.FiscalYears.CountAsync());
    }

    [Fact]
    public async Task Creating_The_Next_Year_Mirrors_The_Source_Year_As_A_Draft_And_Audits_It()
    {
        await using var db = NewDb();
        SeedCompany(db);
        SeedYearWithPeriods(db, FiscalYearStatus.Open, isCurrent: true);
        await db.SaveChangesAsync();

        var result = await NewProvisioning(db).CreateNextYearAsync(companyId: 1, actorUserId: 7);

        Assert.True(result.Succeeded);
        var created = await db.FiscalYears.SingleAsync(y => y.Id == result.FiscalYearId);
        Assert.Equal("FY-2027", created.Name);
        Assert.Equal(new DateTime(2027, 1, 1), created.StartDate);
        Assert.Equal(new DateTime(2027, 12, 31), created.EndDate);
        Assert.Equal(FiscalYearStatus.Draft, created.Status);
        Assert.False(created.IsCurrent);
        Assert.Equal(1, created.PreviousFiscalYearId);

        var periods = await db.FiscalPeriods.Where(p => p.FiscalYearId == created.Id).OrderBy(p => p.PeriodNumber).ToListAsync();
        Assert.Equal(3, periods.Count);
        Assert.Equal(new DateTime(2027, 1, 1), periods[0].StartDate);
        Assert.Equal(new DateTime(2027, 3, 31), periods[2].EndDate);
        Assert.All(periods, p => Assert.Equal(FiscalPeriodStatus.Open, p.Status));

        var audit = await db.AuditLogs.SingleAsync(a => a.EntityName == nameof(FiscalYear));
        Assert.Contains("CreateNextFiscalYear", audit.Diff);
    }

    [Fact]
    public async Task Creating_The_Next_Year_Never_Moves_The_Current_Year_Flag()
    {
        await using var db = NewDb();
        SeedCompany(db);
        SeedYearWithPeriods(db, FiscalYearStatus.Open, isCurrent: true);
        await db.SaveChangesAsync();

        await NewProvisioning(db).CreateNextYearAsync(companyId: 1, actorUserId: 7);

        Assert.True(await db.FiscalYears.SingleAsync(y => y.Id == 1) is { IsCurrent: true });
        Assert.Equal(1, await db.FiscalYears.CountAsync(y => y.IsCurrent));
    }

    [Fact]
    public async Task Building_The_Pages_Writes_Nothing()
    {
        await using var db = NewDb();
        SeedCompany(db);
        SeedYearWithPeriods(db, FiscalYearStatus.Open, isCurrent: true);
        await db.SaveChangesAsync();

        var overview = NewOverview(db);
        await overview.BuildIndexAsync(companyId: 1, canManage: true);
        await overview.BuildDetailsAsync(fiscalYearId: 1, canManage: true);
        await overview.BuildNextYearProposalAsync(companyId: 1);

        Assert.False(db.ChangeTracker.HasChanges());
        Assert.Equal(1, await db.FiscalYears.CountAsync());
        Assert.Equal(3, await db.FiscalPeriods.CountAsync());
    }

    [Fact]
    public async Task Company_Isolation_Is_Kept()
    {
        await using var db = NewDb();
        SeedCompany(db);
        SeedCompany(db, id: 2, code: "B");
        SeedYearWithPeriods(db, FiscalYearStatus.Open, isCurrent: true);
        await db.SaveChangesAsync();

        var model = await NewOverview(db).BuildIndexAsync(companyId: 2, canManage: true);

        Assert.Equal(2, model.SelectedCompanyId);
        Assert.Null(model.CurrentYear);
        Assert.False(model.NextYear.IsAllowed);
    }

    // ── کامپوننت‌های مشترک و فایل‌های نامرتبط ───────────────────────────────────────

    [Fact]
    public void Views_Use_The_Shared_Ak_Components_And_Never_Create_A_Parallel_Stylesheet()
    {
        foreach (var relative in new[]
        {
            "src/PTGOilSystem.Web/Views/FiscalYears/Index.cshtml",
            "src/PTGOilSystem.Web/Views/FiscalYears/Details.cshtml"
        })
        {
            var view = File.ReadAllText(GetRepoPath(relative));

            Assert.Contains("_AkPageHeader", view);
            Assert.Contains("class=\"ak-table\"", view);
            Assert.DoesNotContain("<style", view);
            Assert.DoesNotContain("<link", view);
        }

        Assert.False(
            Directory.Exists(GetRepoPath("src/PTGOilSystem.Web/wwwroot/css/fiscal-years")),
            "Stage 10 must reuse the existing ak components, not a parallel stylesheet.");
    }

    [Fact]
    public void Index_View_Posts_Every_Mutating_Action_With_An_Antiforgery_Token()
    {
        var view = File.ReadAllText(GetRepoPath("src/PTGOilSystem.Web/Views/FiscalYears/Index.cshtml"));

        Assert.Contains("asp-action=\"CreateNextYear\" method=\"post\"", view);
        Assert.Contains("@Html.AntiForgeryToken()", view);
        // «ایجاد سال بعد» هرگز نباید یک لینک GET باشد.
        Assert.DoesNotContain("asp-action=\"CreateNextYear\" class", view);
    }

    [Fact]
    public void Index_Is_A_Single_Simple_Page_Without_The_Close_Wizard()
    {
        var index = File.ReadAllText(GetRepoPath("src/PTGOilSystem.Web/Views/FiscalYears/Index.cshtml"));
        var details = File.ReadAllText(GetRepoPath("src/PTGOilSystem.Web/Views/FiscalYears/Details.cshtml"));

        // ویزاردِ چندمرحله‌ایِ بستن سال از هر دو صفحهٔ سال مالی حذف شده است.
        Assert.DoesNotContain("_FiscalCloseFlow", index);
        Assert.DoesNotContain("_FiscalCloseFlow", details);

        // قفل/بازکردنِ دوره روی همین صفحهٔ ساده انجام می‌شود (POST).
        Assert.Contains("asp-action=\"ChangePeriodLock\" method=\"post\"", index);

        // بخش‌های پیشرفته فقط برای مدیر و فقط وقتی هستهٔ حسابداری روشن است دیده می‌شوند.
        Assert.Contains("AccountingOptions.Value.Enabled", index);
        Assert.Contains("User.IsInRole(AuthRoles.Admin)", index);
        Assert.Contains("asp-controller=\"TrialClose\"", index);
        Assert.Contains("asp-controller=\"FinalClose\"", index);
        Assert.Contains("asp-controller=\"ReopenFiscalYear\"", index);

        // انتخاب‌گرِ سال مالیِ هدر جای دیگری است و دست‌نخورده می‌ماند.
        Assert.True(File.Exists(GetRepoPath(
            "src/PTGOilSystem.Web/Views/Shared/Components/FiscalYearSwitcher/Default.cshtml")));
    }

    [Fact]
    public void Views_Contain_No_Financial_Logic()
    {
        foreach (var relative in new[]
        {
            "src/PTGOilSystem.Web/Views/FiscalYears/Index.cshtml",
            "src/PTGOilSystem.Web/Views/FiscalYears/Details.cshtml"
        })
        {
            var view = File.ReadAllText(GetRepoPath(relative));

            Assert.DoesNotContain("db.", view);
            Assert.DoesNotContain(".Sum(", view);
            Assert.DoesNotContain("JournalEntries", view);
        }
    }

    [Fact]
    public void Stage_10_Touches_No_Unrelated_Ui_File()
    {
        // مرحله ۱۰ فقط دو ویوی خودش را دارد. هیچ ویوی مشترکی جایگزین یا کپی نشده است.
        var directory = GetRepoPath("src/PTGOilSystem.Web/Views/FiscalYears");
        var views = Directory.GetFiles(directory, "*.cshtml").Select(Path.GetFileName).OrderBy(x => x).ToArray();

        Assert.Equal(new[] { "Details.cshtml", "Index.cshtml" }, views);
    }

    // ── داربست ────────────────────────────────────────────────────────────────────

    private static ApplicationDbContext NewDb()
        => new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static FiscalYearOverviewService NewOverview(ApplicationDbContext db)
        => new(db, new AccountingReadinessService(db, Options.Create(new AccountingOptions())));

    private static FiscalYearProvisioningService NewProvisioning(ApplicationDbContext db)
        => new(db, NewOverview(db), new PTGOilSystem.Web.Services.AuditService(db));

    private static void SeedCompany(ApplicationDbContext db, int id = 1, string code = "A")
        => db.Companies.Add(new Company { Id = id, Code = code, Name = $"Company {code}", Country = "AF", IsActive = true });

    private static void SeedYearWithPeriods(
        ApplicationDbContext db,
        FiscalYearStatus status,
        bool isCurrent,
        FiscalPeriodStatus periodStatus = FiscalPeriodStatus.Open)
    {
        db.FiscalYears.Add(new FiscalYear
        {
            Id = 1,
            CompanyId = 1,
            Name = "FY-2026",
            StartDate = new DateTime(2026, 1, 1),
            EndDate = new DateTime(2026, 12, 31),
            Status = status,
            IsCurrent = isCurrent
        });

        var starts = new[] { new DateTime(2026, 1, 1), new DateTime(2026, 2, 1), new DateTime(2026, 3, 1) };
        var ends = new[] { new DateTime(2026, 1, 31), new DateTime(2026, 2, 28), new DateTime(2026, 3, 31) };
        for (var i = 0; i < starts.Length; i++)
        {
            db.FiscalPeriods.Add(new FiscalPeriod
            {
                Id = i + 1,
                CompanyId = 1,
                FiscalYearId = 1,
                PeriodNumber = i + 1,
                Name = $"P{i + 1}",
                StartDate = starts[i],
                EndDate = ends[i],
                Status = periodStatus
            });
        }
    }

    private static void SeedAccount(ApplicationDbContext db)
        => db.Accounts.Add(new Account
        {
            Id = 900,
            CompanyId = 1,
            Code = "1100",
            Name = "Cash",
            AccountType = AccountType.Asset,
            NormalBalance = NormalBalance.Debit,
            IsActive = true
        });

    // بیست حسابِ لازم + تنظیمات، تا Readiness از گاردِ «تنظیمات نیست» عبور کند و به بررسی سال
    // مالی برسد.
    private static void SeedAccountsAndSettings(ApplicationDbContext db)
    {
        for (var i = 0; i < 20; i++)
        {
            db.Accounts.Add(new Account
            {
                Id = 900 + i,
                CompanyId = 1,
                Code = $"ACC-{i:00}",
                Name = $"Account {i}",
                AccountType = AccountType.Asset,
                NormalBalance = NormalBalance.Debit,
                IsActive = true
            });
        }

        db.AccountingSettings.Add(new AccountingSettings
        {
            Id = 1,
            CompanyId = 1,
            FunctionalCurrencyCode = "USD",
            CashBankControlAccountId = 900,
            AccountsReceivableAccountId = 901,
            AccountsPayableAccountId = 902,
            InventoryAccountId = 903,
            InventoryInTransitAccountId = 904,
            SupplierPrepaymentAccountId = 905,
            CustomerAdvanceAccountId = 906,
            FreightPayableAccountId = 907,
            CommissionPayableAccountId = 908,
            EmployeeAdvanceAccountId = 909,
            EmployeePayableAccountId = 910,
            AccruedExpenseAccountId = 911,
            SalesRevenueAccountId = 912,
            CostOfGoodsSoldAccountId = 913,
            GeneralExpenseAccountId = 914,
            ExchangeGainAccountId = 915,
            ExchangeLossAccountId = 916,
            InventoryLossAccountId = 917,
            CurrentYearProfitLossAccountId = 918,
            RetainedEarningsAccountId = 919
        });
    }

    private static void SeedJournal(
        ApplicationDbContext db,
        int id,
        int periodId,
        JournalEntryStatus status,
        decimal debit,
        decimal credit)
    {
        var lines = new List<JournalEntryLine>();
        if (debit > 0m)
        {
            lines.Add(new JournalEntryLine
            {
                Id = id * 10,
                LineNumber = 1,
                AccountId = 900,
                Debit = debit,
                TransactionCurrencyCode = "USD",
                TransactionAmount = debit,
                ExchangeRate = 1m
            });
        }

        if (credit > 0m)
        {
            lines.Add(new JournalEntryLine
            {
                Id = id * 10 + 1,
                LineNumber = 2,
                AccountId = 900,
                Credit = credit,
                TransactionCurrencyCode = "USD",
                TransactionAmount = credit,
                ExchangeRate = 1m
            });
        }

        db.JournalEntries.Add(new JournalEntry
        {
            Id = id,
            CompanyId = 1,
            FiscalYearId = 1,
            FiscalPeriodId = periodId,
            JournalNumber = $"J-{id}",
            Status = status,
            AccountingDate = new DateTime(2026, 1, 15),
            DocumentDate = new DateTime(2026, 1, 15),
            OperationDate = new DateTime(2026, 1, 15),
            SourceModule = "Test",
            PostedAt = status == JournalEntryStatus.Posted ? new DateTime(2026, 1, 15) : null,
            Lines = lines
        });
    }

    private static string GetRepoPath(string relativePath)
        => Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
}
