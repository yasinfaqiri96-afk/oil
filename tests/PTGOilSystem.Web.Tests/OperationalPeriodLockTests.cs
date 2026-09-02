using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Security;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.OperationalPeriod;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// PTG-P1-01 — قفلِ دورهٔ عملیاتی.
///
/// شکستِ واقعی که این تست‌ها pin می‌کنند: دفتری که ماندهٔ واقعی طرف‌حساب‌ها را می‌سازد
/// (<c>LedgerEntries</c>) هیچ مفهومی از «ماه بسته» نداشت، پس کاربر می‌توانست در دسامبر
/// سندِ جنوری را عوض کند و گزارشی که ماه پیش به شریک داده شده بود عدد دیگری بگیرد.
///
/// دو لایه بررسی می‌شود: خودِ سرویس (پیام روی فرم) و پشتوانهٔ <c>SaveChanges</c>
/// (هیچ سرویس، ایمپورت یا اسکریپتی نتواند دور بزند).
/// </summary>
public sealed class OperationalPeriodLockTests
{
    private static readonly DateTime Closed = new(2026, 3, 31, 0, 0, 0, DateTimeKind.Utc);

    private static ApplicationDbContext NewDb()
        => new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static async Task CloseThroughAsync(ApplicationDbContext db, DateTime through, string reason = "بستن ربع اول")
    {
        db.OperationalPeriodLocks.Add(new OperationalPeriodLock
        {
            LockedThroughDate = through,
            IsActive = true,
            Reason = reason
        });
        await db.SaveChangesAsync();
    }

    private static ExpenseTransaction Expense(DateTime date) => new()
    {
        ExpenseDate = date,
        Description = "کرایه",
        AmountUsd = 100m
    };

    // ------------------------------------------------------------------
    // ۱ — بدون قفل، هیچ چیز عوض نمی‌شود
    // ------------------------------------------------------------------

    [Fact]
    public async Task WithNoLockRecorded_EveryDateStaysOpen()
    {
        await using var db = NewDb();
        var guard = new OperationalPeriodGuard(db);

        var status = await guard.GetStatusAsync();

        Assert.False(status.IsLocked);
        await guard.EnsureOpenAsync(new DateTime(2020, 1, 1), "سند فروش");

        db.ExpenseTransactions.Add(Expense(new DateTime(2020, 1, 1)));
        await db.SaveChangesAsync();
        Assert.Equal(1, await db.ExpenseTransactions.CountAsync());
    }

    // ------------------------------------------------------------------
    // ۲ — دورهٔ بسته: ثبت، ویرایش و حذف هر سه مسدود می‌شوند
    // ------------------------------------------------------------------

    [Fact]
    public async Task ClosedPeriod_BlocksCreate()
    {
        await using var db = NewDb();
        await CloseThroughAsync(db, Closed);

        db.ExpenseTransactions.Add(Expense(new DateTime(2026, 2, 10, 0, 0, 0, DateTimeKind.Utc)));

        var error = await Assert.ThrowsAsync<OperationalPeriodLockedException>(() => db.SaveChangesAsync());
        Assert.Equal(Closed.Date, error.LockedThroughDate);
        Assert.Contains("دوره مالی این تاریخ بسته شده است", error.Message);
        Assert.Contains("2026-03-31", error.Message);
    }

    [Fact]
    public async Task ClosedPeriod_BlocksEdit()
    {
        await using var db = NewDb();
        var expense = Expense(new DateTime(2026, 2, 10, 0, 0, 0, DateTimeKind.Utc));
        db.ExpenseTransactions.Add(expense);
        await db.SaveChangesAsync();

        await CloseThroughAsync(db, Closed);

        expense.AmountUsd = 999m;
        await Assert.ThrowsAsync<OperationalPeriodLockedException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task ClosedPeriod_BlocksDeleteAndReversal()
    {
        await using var db = NewDb();
        var expense = Expense(new DateTime(2026, 2, 10, 0, 0, 0, DateTimeKind.Utc));
        db.ExpenseTransactions.Add(expense);
        await db.SaveChangesAsync();

        await CloseThroughAsync(db, Closed);

        db.ExpenseTransactions.Remove(expense);
        await Assert.ThrowsAsync<OperationalPeriodLockedException>(() => db.SaveChangesAsync());
    }

    /// <summary>روزِ آخرِ دوره خودش بسته است — «تا تاریخ» شاملِ همان روز است.</summary>
    [Fact]
    public async Task TheLastDayOfTheClosedPeriod_IsItselfClosed()
    {
        await using var db = NewDb();
        await CloseThroughAsync(db, Closed);

        db.ExpenseTransactions.Add(Expense(Closed));
        await Assert.ThrowsAsync<OperationalPeriodLockedException>(() => db.SaveChangesAsync());
    }

    // ------------------------------------------------------------------
    // ۳ — دورهٔ جاری باز می‌ماند
    // ------------------------------------------------------------------

    [Fact]
    public async Task TheDayAfterTheClosedPeriod_StillPosts()
    {
        await using var db = NewDb();
        await CloseThroughAsync(db, Closed);

        db.ExpenseTransactions.Add(Expense(new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc)));
        await db.SaveChangesAsync();

        Assert.Equal(1, await db.ExpenseTransactions.CountAsync());
    }

    [Fact]
    public async Task MasterDataOutsideTheLockScope_IsNeverBlocked()
    {
        await using var db = NewDb();
        await CloseThroughAsync(db, Closed);

        // کالا و ترمینال تاریخِ دوره ندارند؛ قفل نباید به دادهٔ پایه کار داشته باشد.
        db.Products.Add(new Product { Code = "GAS", Name = "Gasoline" });
        await db.SaveChangesAsync();

        Assert.Equal(1, await db.Products.CountAsync());
    }

    // ------------------------------------------------------------------
    // ۴ — هر سند مالی/عملیاتی در دامنهٔ قفل است، نه فقط مصرف
    // ------------------------------------------------------------------

    [Fact]
    public async Task EveryFinancialDocumentKind_IsInsideTheLockScope()
    {
        var backdated = new DateTime(2026, 2, 10, 0, 0, 0, DateTimeKind.Utc);

        object[] documents =
        [
            new SalesTransaction { SaleDate = backdated },
            new ExpenseTransaction { ExpenseDate = backdated, Description = "x" },
            new PaymentTransaction { PaymentDate = backdated },
            new PartnerSettlement { SettlementDate = backdated },
            new SupplierBalanceTransfer { TransferDate = backdated },
            new LoadingRegister { LoadingDate = backdated },
            new TruckDispatch { DispatchDate = backdated },
            new LossEvent { EventDate = backdated },
            new InventoryMovement { MovementDate = backdated },
            new LedgerEntry { EntryDate = backdated },
        ];

        foreach (var document in documents)
        {
            Assert.Equal(backdated, OperationalPeriodScope.BusinessDateOf(document));
            Assert.NotEqual("سند", OperationalPeriodScope.DescribeKind(document));
        }
    }

    // ------------------------------------------------------------------
    // ۵ — عبورِ استثنایی: فقط با Permission، با دلیل، و با ثبت در Audit
    // ------------------------------------------------------------------

    private static ClaimsPrincipal Actor(bool withPermission)
    {
        var claims = new List<Claim>();
        if (withPermission)
        {
            claims.Add(new Claim(AppClaimTypes.Permission, AppPermissions.PostToClosedOperationalPeriod));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    [Fact]
    public async Task Override_WithoutThePermission_IsStillRejected()
    {
        await using var db = NewDb();
        await CloseThroughAsync(db, Closed);
        var guard = new OperationalPeriodGuard(db, new RecordingAudit());

        var error = await Assert.ThrowsAsync<OperationalPeriodLockedException>(() =>
            guard.EnsureOpenAsync(
                new DateTime(2026, 2, 10),
                "سند مصرف",
                new ClosedPeriodOverride(Actor(withPermission: false), 7, "اصلاح توافق‌شده")));

        Assert.Contains("دسترسی مخصوص", error.Message);
        Assert.False(db.ClosedOperationalPeriodOverrideApproved);
    }

    [Fact]
    public async Task Override_WithoutAReason_IsStillRejected()
    {
        await using var db = NewDb();
        await CloseThroughAsync(db, Closed);
        var guard = new OperationalPeriodGuard(db, new RecordingAudit());

        var error = await Assert.ThrowsAsync<OperationalPeriodLockedException>(() =>
            guard.EnsureOpenAsync(
                new DateTime(2026, 2, 10),
                "سند مصرف",
                new ClosedPeriodOverride(Actor(withPermission: true), 7, "   ")));

        Assert.Contains("دلیل", error.Message);
    }

    [Fact]
    public async Task Override_IsNeverSilent_ItIsAudited()
    {
        await using var db = NewDb();
        await CloseThroughAsync(db, Closed);
        var audit = new RecordingAudit();
        var guard = new OperationalPeriodGuard(db, audit);

        await guard.EnsureOpenAsync(
            new DateTime(2026, 2, 10),
            "سند مصرف",
            new ClosedPeriodOverride(Actor(withPermission: true), 7, "اصلاح توافق‌شده با شریک"));

        var entry = Assert.Single(audit.Entries);
        Assert.Equal(nameof(OperationalPeriodLock), entry.EntityName);
        Assert.Equal(AuditAction.Approve, entry.Action);
        Assert.Equal(7, entry.ActorUserId);
        Assert.Contains("ExceptionalPostingIntoClosedOperationalPeriod", entry.Diff);
        Assert.Contains("اصلاح توافق‌شده با شریک", entry.Diff);

        // و پس از عبورِ مجاز، پشتوانهٔ SaveChanges همان ثبت را رد نمی‌کند.
        Assert.True(db.ClosedOperationalPeriodOverrideApproved);
        db.ExpenseTransactions.Add(Expense(new DateTime(2026, 2, 10, 0, 0, 0, DateTimeKind.Utc)));
        await db.SaveChangesAsync();
        Assert.Equal(1, await db.ExpenseTransactions.CountAsync());
    }

    [Fact]
    public async Task Override_WithoutAnAuditService_IsRefused()
    {
        await using var db = NewDb();
        await CloseThroughAsync(db, Closed);
        var guard = new OperationalPeriodGuard(db, audit: null);

        var error = await Assert.ThrowsAsync<OperationalPeriodLockedException>(() =>
            guard.EnsureOpenAsync(
                new DateTime(2026, 2, 10),
                "سند مصرف",
                new ClosedPeriodOverride(Actor(withPermission: true), 7, "اصلاح توافق‌شده")));

        Assert.Contains("بازرسی", error.Message);
    }

    // ------------------------------------------------------------------
    // ۶ — پیام باید بگوید کدام دوره بسته است
    // ------------------------------------------------------------------

    [Fact]
    public void TheMessageNamesTheDocument_TheDate_AndTheClosedPeriod()
    {
        var message = OperationalPeriodGuard.BuildMessage(
            "سند فروش",
            new DateTime(2026, 2, 10),
            new DateTime(2026, 3, 31));

        Assert.Contains("سند فروش", message);
        Assert.Contains("2026-02-10", message);
        Assert.Contains("2026-03-31", message);
    }

    /// <summary>خطای قفل هرگز نباید به‌صورت «خطای سرور» به کاربر برسد.</summary>
    [Fact]
    public void TheFilterTranslatesTheLockAndConcurrencyErrorsIntoDari()
    {
        Assert.Contains(
            "دوره مالی این تاریخ بسته شده است",
            BusinessRuleExceptionFilter.Translate(
                new OperationalPeriodLockedException(
                    OperationalPeriodGuard.BuildMessage("سند فروش", new DateTime(2026, 2, 10), new DateTime(2026, 3, 31)),
                    new DateTime(2026, 3, 31)))!);

        Assert.Equal(
            BusinessRuleExceptionFilter.ConcurrencyMessage,
            BusinessRuleExceptionFilter.Translate(new DbUpdateConcurrencyException("stale")));

        // خطاهای غیرمرتبط دست‌نخورده رد می‌شوند تا واقعاً دیده و اصلاح شوند.
        Assert.Null(BusinessRuleExceptionFilter.Translate(new InvalidOperationException("boom")));
    }

    private sealed class RecordingAudit : IAuditService
    {
        public List<(string EntityName, int EntityId, AuditAction Action, int? ActorUserId, string? Diff)> Entries { get; } = [];

        public Task LogAsync(string entityName, int entityId, AuditAction action, int? actorUserId = null, string? diff = null, CancellationToken ct = default)
        {
            Entries.Add((entityName, entityId, action, actorUserId, diff));
            return Task.CompletedTask;
        }

        public Task LogAndSaveAsync(string entityName, int entityId, AuditAction action, int? actorUserId = null, string? diff = null, CancellationToken ct = default)
            => LogAsync(entityName, entityId, action, actorUserId, diff, ct);

        public Task LogActivityAsync(AuditLogEntryInput entry, CancellationToken ct = default) => Task.CompletedTask;

        public Task LogActivityAndSaveAsync(AuditLogEntryInput entry, CancellationToken ct = default) => Task.CompletedTask;
    }
}
