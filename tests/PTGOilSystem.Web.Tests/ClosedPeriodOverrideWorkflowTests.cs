using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Security;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.OperationalPeriod;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// PTG فاز ۹ — «ثبت استثنایی در دورهٔ بسته» به‌عنوان یک درخواستِ صریح و یک‌بارمصرف.
///
/// چیزی که این تست‌ها نگه می‌دارند: عبور باید <b>سخت</b> بماند. بدون دسترسی، بدون دلیل،
/// یا بدون تیکِ صریح هیچ اتفاقی نمی‌افتد؛ و وقتی هم اتفاق می‌افتد فقط برای همان درخواست
/// است و ردّ بازرسی می‌گذارد. هیچ حالتی نباید «قفل را برای همیشه باز» کند.
/// </summary>
public sealed class ClosedPeriodOverrideWorkflowTests
{
    private static readonly DateTime ClosedThrough = new(2026, 3, 31, 0, 0, 0, DateTimeKind.Utc);

    private static ApplicationDbContext NewDb()
        => new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static async Task CloseThroughAsync(ApplicationDbContext db)
    {
        db.OperationalPeriodLocks.Add(new OperationalPeriodLock
        {
            LockedThroughDate = ClosedThrough,
            IsActive = true,
            Reason = "بستن ربع اول"
        });
        await db.SaveChangesAsync();
    }

    private static ClaimsPrincipal Actor(bool withPermission)
    {
        var claims = new List<Claim>();
        if (withPermission)
        {
            claims.Add(new Claim(AppClaimTypes.Permission, AppPermissions.PostToClosedOperationalPeriod));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    private sealed class TestUser(ClaimsPrincipal principal, int? userId) : ICurrentUserContext
    {
        public bool IsAuthenticated => true;

        public int? UserId => userId;

        public string? Username => "tester";

        public string? FullName => "Tester";

        public string? RoleName => "Finance";

        public ClaimsPrincipal Principal => principal;
    }

    private sealed class RecordingAudit : IAuditService
    {
        public List<(string EntityName, AuditAction Action, int? ActorUserId, string? Diff)> Entries { get; } = [];

        public Task LogAsync(string entityName, int entityId, AuditAction action,
            int? actorUserId = null, string? diff = null, CancellationToken ct = default)
        {
            Entries.Add((entityName, action, actorUserId, diff));
            return Task.CompletedTask;
        }

        public Task LogAndSaveAsync(string entityName, int entityId, AuditAction action,
            int? actorUserId = null, string? diff = null, CancellationToken ct = default)
            => LogAsync(entityName, entityId, action, actorUserId, diff, ct);

        public Task LogActivityAsync(AuditLogEntryInput entry, CancellationToken ct = default) => Task.CompletedTask;

        public Task LogActivityAndSaveAsync(AuditLogEntryInput entry, CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>یک POST ساختگی با همان دو فیلدی که Partial رندر می‌کند.</summary>
    private static ActionExecutingContext PostWith(params (string Key, string Value)[] fields)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = HttpMethods.Post;
        httpContext.Request.ContentType = "application/x-www-form-urlencoded";
        httpContext.Request.Path = "/Expenses/Create";
        httpContext.Request.Form = new FormCollection(
            fields.ToDictionary(f => f.Key, f => new Microsoft.Extensions.Primitives.StringValues(f.Value)));

        // Body لازم نیست خوانده شود چون Form مستقیم ست شده؛ فقط برای کامل‌بودن.
        httpContext.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(string.Empty));

        return new ActionExecutingContext(
            new ActionContext(httpContext, new RouteData(), new ActionDescriptor()),
            [],
            new Dictionary<string, object?>(),
            controller: null!);
    }

    private static async Task RunFilterAsync(
        ClosedPeriodOverrideFilter filter,
        ActionExecutingContext context)
        => await filter.OnActionExecutionAsync(
            context,
            () => Task.FromResult(new ActionExecutedContext(context, [], controller: null!)));

    private static (ClosedPeriodOverrideFilter Filter, RecordingAudit Audit) BuildFilter(
        ApplicationDbContext db,
        bool withPermission)
    {
        var audit = new RecordingAudit();
        var guard = new OperationalPeriodGuard(db, audit);
        var filter = new ClosedPeriodOverrideFilter(guard, new TestUser(Actor(withPermission), 7));
        return (filter, audit);
    }

    // ------------------------------------------------------------------
    // ۱ — حالتِ عادی: هیچ درخواستی، هیچ عبوری
    // ------------------------------------------------------------------

    [Fact]
    public async Task WithoutTheCheckbox_NothingIsApproved()
    {
        await using var db = NewDb();
        await CloseThroughAsync(db);
        var (filter, audit) = BuildFilter(db, withPermission: true);

        await RunFilterAsync(filter, PostWith(("Description", "کرایه")));

        Assert.False(db.ClosedOperationalPeriodOverrideApproved);
        Assert.Empty(audit.Entries);
    }

    /// <summary>تیک بدون دلیل «درخواست» شمرده نمی‌شود — قفل دست‌نخورده می‌ماند.</summary>
    [Fact]
    public async Task CheckboxWithoutAReason_IsIgnored()
    {
        await using var db = NewDb();
        await CloseThroughAsync(db);
        var (filter, audit) = BuildFilter(db, withPermission: true);

        await RunFilterAsync(filter, PostWith(
            (ClosedPeriodOverrideFilter.RequestedField, "true"),
            (ClosedPeriodOverrideFilter.ReasonField, "   ")));

        Assert.False(db.ClosedOperationalPeriodOverrideApproved);
        Assert.Empty(audit.Entries);
    }

    /// <summary>دلیل بدون تیک هم عبور نیست؛ باید خواستِ صریح باشد.</summary>
    [Fact]
    public async Task ReasonWithoutTheCheckbox_IsIgnored()
    {
        await using var db = NewDb();
        await CloseThroughAsync(db);
        var (filter, _) = BuildFilter(db, withPermission: true);

        await RunFilterAsync(filter, PostWith(
            (ClosedPeriodOverrideFilter.ReasonField, "اصلاح توافق‌شده")));

        Assert.False(db.ClosedOperationalPeriodOverrideApproved);
    }

    // ------------------------------------------------------------------
    // ۲ — دسترسی: کاربرِ غیرمجاز حتی با فیلدهای دستی رد می‌شود
    // ------------------------------------------------------------------

    [Fact]
    public async Task UserWithoutThePermission_IsRejectedEvenWhenPostingTheFieldsByHand()
    {
        await using var db = NewDb();
        await CloseThroughAsync(db);
        var (filter, audit) = BuildFilter(db, withPermission: false);

        var error = await Assert.ThrowsAsync<OperationalPeriodLockedException>(
            () => RunFilterAsync(filter, PostWith(
                (ClosedPeriodOverrideFilter.RequestedField, "true"),
                (ClosedPeriodOverrideFilter.ReasonField, "می‌خواهم قفل را دور بزنم"))));

        Assert.Contains("دسترسی مخصوص", error.Message);
        Assert.False(db.ClosedOperationalPeriodOverrideApproved);
        Assert.Empty(audit.Entries);
    }

    // ------------------------------------------------------------------
    // ۳ — مسیرِ درست: مجاز، با دلیل، ثبت‌شده در بازرسی
    // ------------------------------------------------------------------

    [Fact]
    public async Task AuthorizedRequestWithAReason_OpensExactlyThisOneRequest()
    {
        await using var db = NewDb();
        await CloseThroughAsync(db);
        var (filter, audit) = BuildFilter(db, withPermission: true);

        await RunFilterAsync(filter, PostWith(
            (ClosedPeriodOverrideFilter.RequestedField, "true"),
            (ClosedPeriodOverrideFilter.ReasonField, "سند جامانده، توافق با شریک")));

        Assert.True(db.ClosedOperationalPeriodOverrideApproved);

        var entry = Assert.Single(audit.Entries);
        Assert.Equal(nameof(OperationalPeriodLock), entry.EntityName);
        Assert.Equal(AuditAction.Approve, entry.Action);
        Assert.Equal(7, entry.ActorUserId);
        Assert.Contains("ExceptionalPostingIntoClosedOperationalPeriod", entry.Diff);
        Assert.Contains("سند جامانده، توافق با شریک", entry.Diff);
        Assert.Contains("/Expenses/Create", entry.Diff);
    }

    /// <summary>عبورِ تأییدشده واقعاً می‌گذارد سند در دورهٔ بسته ذخیره شود.</summary>
    [Fact]
    public async Task AfterApproval_ThePostingIntoTheClosedPeriodSucceeds()
    {
        await using var db = NewDb();
        await CloseThroughAsync(db);
        var (filter, _) = BuildFilter(db, withPermission: true);

        var backdated = new ExpenseTransaction
        {
            ExpenseDate = new DateTime(2026, 2, 10, 0, 0, 0, DateTimeKind.Utc),
            Description = "کرایه جامانده",
            AmountUsd = 100m
        };

        // بدون عبور، همین ذخیره رد می‌شود.
        db.ExpenseTransactions.Add(backdated);
        await Assert.ThrowsAsync<OperationalPeriodLockedException>(() => db.SaveChangesAsync());

        await RunFilterAsync(filter, PostWith(
            (ClosedPeriodOverrideFilter.RequestedField, "true"),
            (ClosedPeriodOverrideFilter.ReasonField, "سند جامانده")));

        await db.SaveChangesAsync();

        Assert.Equal(1, await db.ExpenseTransactions.CountAsync());
    }

    // ------------------------------------------------------------------
    // ۴ — بدونِ قفل، این کار اصلاً موضوعیت ندارد
    // ------------------------------------------------------------------

    /// <summary>
    /// وقتی هیچ دوره‌ای بسته نیست، درخواست نه خطا می‌دهد نه ردِّ بازرسی می‌سازد — و مهم‌تر:
    /// پرچمی هم روشن نمی‌شود، پس چیزی برای «روشن ماندن» وجود ندارد.
    /// </summary>
    [Fact]
    public async Task WithNoClosedPeriod_TheRequestIsAQuietNoOp()
    {
        await using var db = NewDb();
        var (filter, audit) = BuildFilter(db, withPermission: true);

        await RunFilterAsync(filter, PostWith(
            (ClosedPeriodOverrideFilter.RequestedField, "true"),
            (ClosedPeriodOverrideFilter.ReasonField, "دلیلی هست ولی قفلی نیست")));

        Assert.False(db.ClosedOperationalPeriodOverrideApproved);
        Assert.Empty(audit.Entries);
    }

    // ------------------------------------------------------------------
    // ۵ — GET هرگز عبور نمی‌گیرد
    // ------------------------------------------------------------------

    [Fact]
    public async Task GetRequests_CanNeverApproveAnOverride()
    {
        await using var db = NewDb();
        await CloseThroughAsync(db);
        var (filter, _) = BuildFilter(db, withPermission: true);

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = HttpMethods.Get;
        httpContext.Request.Path = "/Expenses/Create";
        var context = new ActionExecutingContext(
            new ActionContext(httpContext, new RouteData(), new ActionDescriptor()),
            [],
            new Dictionary<string, object?>(),
            controller: null!);

        await RunFilterAsync(filter, context);

        Assert.False(db.ClosedOperationalPeriodOverrideApproved);
    }
}
