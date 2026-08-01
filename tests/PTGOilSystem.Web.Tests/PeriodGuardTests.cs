using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Security;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Accounting;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// مرحله ۱۱ — AccountingDate و قفلِ دوره.
///
/// این تست‌ها روی خودِ گارد کار می‌کنند، نه روی یک آداپتر: گارد تنها دروازه است و هر آداپتری از
/// <see cref="AccountingPostingService"/> و آن هم از همین گارد رد می‌شود. آنچه اینجا اثبات
/// می‌شود درباره‌ی **همه‌ی** آداپترهای مراحل ۱ تا ۸.۵ صادق است — و
/// <see cref="Only_The_Posting_Service_Creates_Journal_Entries"/> اثبات می‌کند مسیر دیگری نیست.
/// </summary>
public class PeriodGuardTests
{
    private static readonly DateTime InPeriod = new(2026, 1, 15);

    // ── ثبت در دورهٔ باز ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Posting_Into_An_Open_Period_Is_Allowed()
    {
        await using var db = NewDb();
        Seed(db);
        await db.SaveChangesAsync();

        var selection = await NewGuard(db).EnsurePostingAllowedAsync(1, InPeriod);

        Assert.Equal(1, selection.FiscalYear.Id);
        Assert.Equal(1, selection.FiscalPeriod.Id);
    }

    [Fact]
    public async Task A_Reopened_Fiscal_Year_Posts_Like_An_Open_One()
    {
        await using var db = NewDb();
        Seed(db, yearStatus: FiscalYearStatus.Reopened);
        await db.SaveChangesAsync();

        var selection = await NewGuard(db).EnsurePostingAllowedAsync(1, InPeriod);

        Assert.Equal(1, selection.FiscalPeriod.Id);
    }

    // ── قفل نرم ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Normal_Posting_Into_A_Soft_Locked_Period_Is_Rejected()
    {
        await using var db = NewDb();
        Seed(db, periodStatus: FiscalPeriodStatus.SoftLocked);
        await db.SaveChangesAsync();

        var error = await Assert.ThrowsAsync<AccountingValidationException>(
            () => NewGuard(db).EnsurePostingAllowedAsync(1, InPeriod));

        Assert.Equal("PERIOD_SOFT_LOCKED", error.Code);
    }

    [Fact]
    public async Task An_Exceptional_Posting_Into_A_Soft_Locked_Period_Needs_The_Explicit_Permission()
    {
        await using var db = NewDb();
        Seed(db, periodStatus: FiscalPeriodStatus.SoftLocked);
        await db.SaveChangesAsync();

        // نقشِ Admin عمداً کافی نیست — «فقط با Permission مشخص».
        var admin = NewActor(roles: [AuthRoles.Admin], permissions: []);
        var error = await Assert.ThrowsAsync<AccountingValidationException>(
            () => NewGuard(db).EnsureExceptionalPostingAllowedAsync(
                1, InPeriod, new SoftLockPostingException(admin, 7, "correction")));

        Assert.Equal("PERIOD_SOFT_LOCKED", error.Code);
        Assert.Empty(db.AuditLogs);
    }

    [Fact]
    public async Task An_Exceptional_Posting_Into_A_Soft_Locked_Period_Needs_A_Reason()
    {
        await using var db = NewDb();
        Seed(db, periodStatus: FiscalPeriodStatus.SoftLocked);
        await db.SaveChangesAsync();

        var error = await Assert.ThrowsAsync<AccountingValidationException>(
            () => NewGuard(db).EnsureExceptionalPostingAllowedAsync(
                1, InPeriod, new SoftLockPostingException(PermittedActor(), 7, "   ")));

        Assert.Equal("PERIOD_EXCEPTION_REASON_REQUIRED", error.Code);
    }

    [Fact]
    public async Task A_Permitted_Exceptional_Posting_Into_A_Soft_Locked_Period_Is_Allowed_And_Audited()
    {
        await using var db = NewDb();
        Seed(db, periodStatus: FiscalPeriodStatus.SoftLocked);
        await db.SaveChangesAsync();

        var selection = await NewGuard(db).EnsureExceptionalPostingAllowedAsync(
            1, InPeriod, new SoftLockPostingException(PermittedActor(), 7, "audited correction"));
        await db.SaveChangesAsync();

        Assert.Equal(1, selection.FiscalPeriod.Id);
        var log = Assert.Single(db.AuditLogs.Where(a => a.EntityName == nameof(FiscalPeriod)));
        Assert.Equal(7, log.ActorUserId);
        Assert.Contains("ExceptionalPostingIntoSoftLockedPeriod", log.Diff);
        Assert.Contains("audited correction", log.Diff);
    }

    [Fact]
    public async Task An_Exceptional_Posting_Cannot_Proceed_Without_The_Audit_Service()
    {
        await using var db = NewDb();
        Seed(db, periodStatus: FiscalPeriodStatus.SoftLocked);
        await db.SaveChangesAsync();

        var guard = new PeriodGuard(db, new FiscalCalendarService(db));
        var error = await Assert.ThrowsAsync<AccountingValidationException>(
            () => guard.EnsureExceptionalPostingAllowedAsync(
                1, InPeriod, new SoftLockPostingException(PermittedActor(), 7, "reason")));

        Assert.Equal("PERIOD_EXCEPTION_AUDIT_UNAVAILABLE", error.Code);
    }

    // ── قفل سخت ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Posting_Into_A_Hard_Locked_Period_Is_Rejected()
    {
        await using var db = NewDb();
        Seed(db, periodStatus: FiscalPeriodStatus.HardLocked);
        await db.SaveChangesAsync();

        var error = await Assert.ThrowsAsync<AccountingValidationException>(
            () => NewGuard(db).EnsurePostingAllowedAsync(1, InPeriod));

        Assert.Equal("PERIOD_HARD_LOCKED", error.Code);
    }

    [Fact]
    public async Task A_Hard_Locked_Period_Has_No_Exception_Even_With_The_Permission()
    {
        await using var db = NewDb();
        Seed(db, periodStatus: FiscalPeriodStatus.HardLocked);
        await db.SaveChangesAsync();

        var error = await Assert.ThrowsAsync<AccountingValidationException>(
            () => NewGuard(db).EnsureExceptionalPostingAllowedAsync(
                1, InPeriod, new SoftLockPostingException(PermittedActor(), 7, "please")));

        Assert.Equal("PERIOD_HARD_LOCKED", error.Code);
        Assert.Empty(db.AuditLogs);
    }

    [Fact]
    public async Task A_Closed_Period_Is_As_Strict_As_A_Hard_Locked_One()
    {
        await using var db = NewDb();
        Seed(db, periodStatus: FiscalPeriodStatus.Closed);
        await db.SaveChangesAsync();

        var error = await Assert.ThrowsAsync<AccountingValidationException>(
            () => NewGuard(db).EnsurePostingAllowedAsync(1, InPeriod));

        Assert.Equal("PERIOD_HARD_LOCKED", error.Code);
    }

    // ── سال بسته ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Posting_Into_A_Closed_Fiscal_Year_Is_Rejected_Even_When_The_Period_Is_Open()
    {
        await using var db = NewDb();
        Seed(db, yearStatus: FiscalYearStatus.Closed, periodStatus: FiscalPeriodStatus.Open);
        await db.SaveChangesAsync();

        var error = await Assert.ThrowsAsync<AccountingValidationException>(
            () => NewGuard(db).EnsurePostingAllowedAsync(1, InPeriod));

        Assert.Equal("FISCAL_YEAR_CLOSED", error.Code);
    }

    [Theory]
    [InlineData(FiscalYearStatus.Draft)]
    [InlineData(FiscalYearStatus.Closing)]
    public async Task Posting_Into_A_Fiscal_Year_That_Is_Not_Open_Is_Rejected(FiscalYearStatus status)
    {
        await using var db = NewDb();
        Seed(db, yearStatus: status);
        await db.SaveChangesAsync();

        var error = await Assert.ThrowsAsync<AccountingValidationException>(
            () => NewGuard(db).EnsurePostingAllowedAsync(1, InPeriod));

        Assert.Equal("FISCAL_YEAR_NOT_OPEN", error.Code);
    }

    // ── شرکت و بازهٔ تاریخ ─────────────────────────────────────────────────────────

    [Fact]
    public async Task A_Period_Of_Another_Company_Inside_This_Year_Is_Rejected()
    {
        await using var db = NewDb();
        Seed(db);
        db.Companies.Add(new Company { Id = 2, Code = "B", Name = "B", Country = "AF", IsActive = true });
        await db.SaveChangesAsync();

        db.FiscalPeriods.Single(p => p.Id == 1).CompanyId = 2;
        await db.SaveChangesAsync();

        var error = await Assert.ThrowsAsync<AccountingValidationException>(
            () => NewGuard(db).EnsurePostingAllowedAsync(1, InPeriod));

        Assert.Equal("COMPANY_PERIOD_MISMATCH", error.Code);
    }

    [Fact]
    public async Task An_Inactive_Or_Unknown_Company_Is_Rejected()
    {
        await using var db = NewDb();
        Seed(db);
        await db.SaveChangesAsync();

        var unknown = await Assert.ThrowsAsync<AccountingValidationException>(
            () => NewGuard(db).EnsurePostingAllowedAsync(99, InPeriod));
        Assert.Equal("INVALID_COMPANY", unknown.Code);

        db.Companies.Single(c => c.Id == 1).IsActive = false;
        await db.SaveChangesAsync();

        var inactive = await Assert.ThrowsAsync<AccountingValidationException>(
            () => NewGuard(db).EnsurePostingAllowedAsync(1, InPeriod));
        Assert.Equal("INVALID_COMPANY", inactive.Code);
    }

    [Fact]
    public async Task An_Accounting_Date_Outside_Every_Fiscal_Year_Is_Rejected()
    {
        await using var db = NewDb();
        Seed(db);
        await db.SaveChangesAsync();

        var error = await Assert.ThrowsAsync<AccountingValidationException>(
            () => NewGuard(db).EnsurePostingAllowedAsync(1, new DateTime(2025, 6, 1)));

        Assert.Equal("ACCOUNTING_DATE_OUT_OF_RANGE", error.Code);
    }

    [Fact]
    public async Task A_Future_Accounting_Date_Is_Rejected()
    {
        await using var db = NewDb();
        // «امروز» را از همان مرجعی می‌گیریم که PeriodGuard استفاده می‌کند (روز کاری کابل).
        // با DateTime.UtcNow.Date این تست بین 19:30 تا 23:59 UTC می‌شکست، چون آن لحظه
        // در کابل روز بعد است و «فردای UTC» دیگر آینده نبود.
        var utcNow = new DateTimeOffset(2026, 5, 2, 6, 0, 0, TimeSpan.Zero);
        var today = new PTGOilSystem.Web.Services.Time.AfghanistanBusinessClock(
            new FixedTimeProvider(utcNow)).Today;
        db.Companies.Add(new Company { Id = 1, Code = "A", Name = "A", Country = "AF", IsActive = true });
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
            Name = "P1",
            StartDate = today.AddMonths(-6),
            EndDate = today.AddMonths(6),
            Status = FiscalPeriodStatus.Open
        });
        await db.SaveChangesAsync();

        // فردا داخل همان دورهٔ باز است و باز هم رد می‌شود: قفلِ دوره تنها قاعده نیست.
        var error = await Assert.ThrowsAsync<AccountingValidationException>(
            () => NewGuardAt(db, utcNow).EnsurePostingAllowedAsync(1, today.AddDays(1)));
        Assert.Equal("ACCOUNTING_DATE_OUT_OF_RANGE", error.Code);

        // امروز قبول است — مرزِ «آینده» روز است، نه لحظه.
        var selection = await NewGuardAt(db, utcNow).EnsurePostingAllowedAsync(1, today);
        Assert.Equal(1, selection.FiscalPeriod.Id);

        // مرز نیمه‌شب کابل: 19:30 UTC یعنی 00:00 روز بعد در کابل. سندی که در کابل
        // «امروز» است باید پذیرفته شود، هرچند به وقت UTC هنوز فردا نشده است.
        var justAfterKabulMidnight = new DateTimeOffset(2026, 5, 2, 19, 30, 0, TimeSpan.Zero);
        var kabulToday = today.AddDays(1);
        var acceptedAtBoundary = await NewGuardAt(db, justAfterKabulMidnight)
            .EnsurePostingAllowedAsync(1, kabulToday);
        Assert.Equal(1, acceptedAtBoundary.FiscalPeriod.Id);

        // و یک ثانیه پیش از آن (23:59:59 کابل) همان تاریخ هنوز آینده است و رد می‌شود.
        var justBeforeKabulMidnight = new DateTimeOffset(2026, 5, 2, 19, 29, 59, TimeSpan.Zero);
        var boundaryError = await Assert.ThrowsAsync<AccountingValidationException>(
            () => NewGuardAt(db, justBeforeKabulMidnight).EnsurePostingAllowedAsync(1, kabulToday));
        Assert.Equal("ACCOUNTING_DATE_OUT_OF_RANGE", boundaryError.Code);
    }

    [Fact]
    public async Task A_Fiscal_Year_Without_A_Period_For_The_Date_Is_Rejected()
    {
        await using var db = NewDb();
        Seed(db);
        await db.SaveChangesAsync();

        db.FiscalPeriods.Single(p => p.Id == 1).EndDate = new DateTime(2026, 1, 10);
        await db.SaveChangesAsync();

        var error = await Assert.ThrowsAsync<AccountingValidationException>(
            () => NewGuard(db).EnsurePostingAllowedAsync(1, InPeriod));

        Assert.Equal("PERIOD_NOT_FOUND", error.Code);
    }

    [Fact]
    public async Task Backdating_Into_An_Open_Earlier_Period_Stays_Allowed()
    {
        await using var db = NewDb();
        Seed(db);
        await db.SaveChangesAsync();

        // قدمتِ تاریخ به‌خودی‌خود ممنوع نیست؛ آنچه Backdating را می‌بندد قفلِ دوره است.
        var selection = await NewGuard(db).EnsurePostingAllowedAsync(1, new DateTime(2026, 1, 2));
        Assert.Equal(1, selection.FiscalPeriod.Id);

        db.FiscalPeriods.Single(p => p.Id == 1).Status = FiscalPeriodStatus.HardLocked;
        await db.SaveChangesAsync();

        var error = await Assert.ThrowsAsync<AccountingValidationException>(
            () => NewGuard(db).EnsurePostingAllowedAsync(1, new DateTime(2026, 1, 2)));
        Assert.Equal("PERIOD_HARD_LOCKED", error.Code);
    }

    [Fact]
    public async Task The_Guard_Reads_Only_The_Accounting_Date_Not_The_Document_Date()
    {
        await using var db = NewDb();
        Seed(db);
        // دورهٔ دوم قفل سخت است.
        db.FiscalPeriods.Add(new FiscalPeriod
        {
            Id = 2,
            CompanyId = 1,
            FiscalYearId = 1,
            PeriodNumber = 2,
            Name = "P2",
            StartDate = new DateTime(2026, 2, 1),
            EndDate = new DateTime(2026, 2, 28),
            Status = FiscalPeriodStatus.HardLocked
        });
        await db.SaveChangesAsync();

        // AccountingDate تعیین می‌کند سند کجا می‌نشیند — و فقط همان سنجیده می‌شود.
        var selection = await NewGuard(db).EnsurePostingAllowedAsync(1, new DateTime(2026, 1, 20));
        Assert.Equal(1, selection.FiscalPeriod.Id);

        var error = await Assert.ThrowsAsync<AccountingValidationException>(
            () => NewGuard(db).EnsurePostingAllowedAsync(1, new DateTime(2026, 2, 20)));
        Assert.Equal("PERIOD_HARD_LOCKED", error.Code);
    }

    // ── هیچ مسیر دورزننده‌ای نیست ───────────────────────────────────────────────────

    [Fact]
    public void Only_The_Posting_Service_Creates_Journal_Entries()
    {
        // اگر روزی جای دیگری سند بسازد، این تست می‌شکند — و باید بشکند: آن مسیر از گارد رد
        // نمی‌شود و همه‌ی تضمین‌های مرحله ۱۱ را دور می‌زند.
        var offenders = EnumerateSourceFiles("src/PTGOilSystem.Web")
            .Where(file => System.IO.File.ReadAllText(file).Contains("new JournalEntry", StringComparison.Ordinal))
            .Select(System.IO.Path.GetFileName)
            .OrderBy(x => x)
            .ToArray();

        Assert.Equal(new[] { "AccountingPostingService.cs" }, offenders);
    }

    [Fact]
    public void Only_The_Period_Guard_Resolves_The_Fiscal_Calendar()
    {
        var offenders = EnumerateSourceFiles("src/PTGOilSystem.Web")
            .Where(file =>
            {
                var text = System.IO.File.ReadAllText(file);
                return text.Contains("IFiscalCalendarService", StringComparison.Ordinal)
                    && !System.IO.Path.GetFileName(file).Equals("Program.cs", StringComparison.Ordinal);
            })
            .Select(System.IO.Path.GetFileName)
            .OrderBy(x => x)
            .ToArray();

        Assert.Equal(new[] { "FiscalCalendarService.cs", "PeriodGuard.cs" }, offenders);
    }

    [Fact]
    public void The_Posting_Service_Guards_Both_Posting_And_Reversal()
    {
        var text = System.IO.File.ReadAllText(GetRepoPath(
            "src/PTGOilSystem.Web/Services/Accounting/AccountingPostingService.cs"));

        // برگشت هم از PostInternalAsync رد می‌شود، پس همان یک فراخوانیِ گارد هر دو مسیر را می‌پوشاند.
        Assert.Contains("periodGuard.EnsurePostingAllowedAsync", text);
        Assert.Contains("=> PostInternalAsync(request, reversalOfJournalEntryId: null, cancellationToken)", text);
        Assert.Contains("return await PostInternalAsync(postRequest, original.Id, cancellationToken);", text);
    }

    [Fact]
    public void Every_Required_Reason_Code_Exists()
    {
        var text = System.IO.File.ReadAllText(GetRepoPath(
            "src/PTGOilSystem.Web/Services/Accounting/PeriodGuard.cs"));

        foreach (var code in new[]
        {
            "PERIOD_NOT_FOUND",
            "PERIOD_SOFT_LOCKED",
            "PERIOD_HARD_LOCKED",
            "FISCAL_YEAR_CLOSED",
            "ACCOUNTING_DATE_OUT_OF_RANGE",
            "COMPANY_PERIOD_MISMATCH"
        })
        {
            Assert.Contains(code, text);
        }
    }

    // ── تغییرِ قفل ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Locking_A_Period_Is_Audited_And_Stamps_The_Actor()
    {
        await using var db = NewDb();
        Seed(db);
        await db.SaveChangesAsync();

        var result = await NewLockService(db).ChangeStatusAsync(1, FiscalPeriodStatus.SoftLocked, actorUserId: 7);

        Assert.True(result.Succeeded);
        var period = await db.FiscalPeriods.SingleAsync(p => p.Id == 1);
        Assert.Equal(FiscalPeriodStatus.SoftLocked, period.Status);
        Assert.Equal(7, period.LockedByUserId);
        Assert.NotNull(period.LockedAt);

        var log = Assert.Single(db.AuditLogs.Where(a => a.EntityName == nameof(FiscalPeriod)));
        Assert.Contains("ChangeFiscalPeriodLock", log.Diff);
        Assert.Contains("Open", log.Diff);
        Assert.Contains("SoftLocked", log.Diff);
    }

    [Fact]
    public async Task Unlocking_A_Soft_Locked_Period_Clears_The_Lock_Stamp()
    {
        await using var db = NewDb();
        Seed(db, periodStatus: FiscalPeriodStatus.SoftLocked);
        await db.SaveChangesAsync();

        var result = await NewLockService(db).ChangeStatusAsync(1, FiscalPeriodStatus.Open, actorUserId: 7);

        Assert.True(result.Succeeded);
        var period = await db.FiscalPeriods.SingleAsync(p => p.Id == 1);
        Assert.Equal(FiscalPeriodStatus.Open, period.Status);
        Assert.Null(period.LockedAt);
        Assert.Null(period.LockedByUserId);
    }

    [Fact]
    public async Task A_Hard_Locked_Period_Can_Never_Be_Unlocked()
    {
        await using var db = NewDb();
        Seed(db, periodStatus: FiscalPeriodStatus.HardLocked);
        await db.SaveChangesAsync();

        foreach (var target in new[] { FiscalPeriodStatus.Open, FiscalPeriodStatus.SoftLocked })
        {
            var result = await NewLockService(db).ChangeStatusAsync(1, target, actorUserId: 7);
            Assert.False(result.Succeeded);
            Assert.Equal("PERIOD_HARD_LOCKED", result.ErrorCode);
        }

        Assert.Equal(FiscalPeriodStatus.HardLocked, (await db.FiscalPeriods.SingleAsync(p => p.Id == 1)).Status);
        Assert.Empty(db.AuditLogs);
    }

    [Fact]
    public async Task A_Period_Of_A_Closed_Fiscal_Year_Cannot_Be_Touched()
    {
        await using var db = NewDb();
        Seed(db, yearStatus: FiscalYearStatus.Closed);
        await db.SaveChangesAsync();

        var result = await NewLockService(db).ChangeStatusAsync(1, FiscalPeriodStatus.SoftLocked, actorUserId: 7);

        Assert.False(result.Succeeded);
        Assert.Equal("FISCAL_YEAR_CLOSED", result.ErrorCode);
    }

    [Fact]
    public async Task Changing_The_Lock_To_The_Same_Status_Is_Idempotent_And_Writes_Nothing()
    {
        await using var db = NewDb();
        Seed(db, periodStatus: FiscalPeriodStatus.SoftLocked);
        await db.SaveChangesAsync();

        var result = await NewLockService(db).ChangeStatusAsync(1, FiscalPeriodStatus.SoftLocked, actorUserId: 7);

        Assert.True(result.Succeeded);
        Assert.Empty(db.AuditLogs);
    }

    [Fact]
    public void ChangePeriodLock_Is_Post_Only_With_Antiforgery_And_AdminOnly()
    {
        var action = typeof(PTGOilSystem.Web.Controllers.FiscalYearsController)
            .GetMethod(nameof(PTGOilSystem.Web.Controllers.FiscalYearsController.ChangePeriodLock))!;

        Assert.NotEmpty(action.GetCustomAttributes(typeof(Microsoft.AspNetCore.Mvc.HttpPostAttribute), true));
        Assert.Empty(action.GetCustomAttributes(typeof(Microsoft.AspNetCore.Mvc.HttpGetAttribute), true));
        Assert.NotEmpty(action.GetCustomAttributes(typeof(Microsoft.AspNetCore.Mvc.ValidateAntiForgeryTokenAttribute), true));
        Assert.Contains(
            AuthPolicies.AdminOnly,
            action.GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), true)
                .Cast<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>()
                .Select(a => a.Policy));
    }

    // ── داربست ────────────────────────────────────────────────────────────────────

    private static ApplicationDbContext NewDb()
        => new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static PeriodGuard NewGuard(ApplicationDbContext db)
        => new(db, new FiscalCalendarService(db), new AuditService(db));

    // ساعت قطعی برای آزمون مرز روز کاری کابل؛ ساعت سیستم عامل هیچ‌جا مرجع نیست.
    private static PeriodGuard NewGuardAt(ApplicationDbContext db, DateTimeOffset utcNow)
        => new(
            db,
            new FiscalCalendarService(db),
            new AuditService(db),
            new PTGOilSystem.Web.Services.Time.AfghanistanBusinessClock(new FixedTimeProvider(utcNow)));

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private static FiscalPeriodLockService NewLockService(ApplicationDbContext db)
        => new(db, new AuditService(db));

    private static ClaimsPrincipal PermittedActor()
        => NewActor(roles: [], permissions: [AppPermissions.PostToSoftLockedPeriod]);

    private static ClaimsPrincipal NewActor(string[] roles, string[] permissions)
    {
        var claims = new List<Claim>();
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));
        claims.AddRange(permissions.Select(p => new Claim(AppClaimTypes.Permission, p)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
    }

    private static void Seed(
        ApplicationDbContext db,
        FiscalYearStatus yearStatus = FiscalYearStatus.Open,
        FiscalPeriodStatus periodStatus = FiscalPeriodStatus.Open)
    {
        db.Companies.Add(new Company { Id = 1, Code = "A", Name = "A", Country = "AF", IsActive = true });
        db.FiscalYears.Add(new FiscalYear
        {
            Id = 1,
            CompanyId = 1,
            Name = "FY-2026",
            StartDate = new DateTime(2026, 1, 1),
            EndDate = new DateTime(2026, 12, 31),
            Status = yearStatus,
            IsCurrent = true
        });
        db.FiscalPeriods.Add(new FiscalPeriod
        {
            Id = 1,
            CompanyId = 1,
            FiscalYearId = 1,
            PeriodNumber = 1,
            Name = "P1",
            StartDate = new DateTime(2026, 1, 1),
            EndDate = new DateTime(2026, 1, 31),
            Status = periodStatus
        });
    }

    private static IEnumerable<string> EnumerateSourceFiles(string relativeRoot)
        => System.IO.Directory.EnumerateFiles(GetRepoPath(relativeRoot), "*.cs", System.IO.SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{System.IO.Path.DirectorySeparatorChar}Migrations{System.IO.Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !file.Contains($"{System.IO.Path.DirectorySeparatorChar}obj{System.IO.Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !file.Contains($"{System.IO.Path.DirectorySeparatorChar}bin{System.IO.Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    private static string GetRepoPath(string relativePath)
        => System.IO.Path.GetFullPath(System.IO.Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar)));
}
