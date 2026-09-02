using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Security;

namespace PTGOilSystem.Web.Services.OperationalPeriod;

/// <summary>
/// «دورهٔ مالی این تاریخ بسته است». عمداً یک استثنای اختصاصی است تا هر مسیر ثبتی بتواند
/// آن را بگیرد و پیام فارسی را روی فرم بگذارد، به‌جای خطای ۵۰۰.
/// </summary>
public sealed class OperationalPeriodLockedException(string message, DateTime lockedThroughDate)
    : InvalidOperationException(message)
{
    public DateTime LockedThroughDate { get; } = lockedThroughDate;
}

/// <summary>وضعیت قفل برای نمایش و تصمیم‌گیری، بدون پرتاب استثنا.</summary>
public sealed record OperationalPeriodLockStatus(bool IsLocked, DateTime? LockedThroughDate, string? Reason)
{
    public static readonly OperationalPeriodLockStatus Open = new(false, null, null);

    public bool Covers(DateTime date) => IsLocked && date.Date <= LockedThroughDate!.Value.Date;
}

/// <summary>
/// درخواستِ ثبتِ استثنایی در دورهٔ بسته. مثل <c>SoftLockPostingException</c> ماژول حسابداری،
/// هم بازیگر می‌خواهد هم دلیل — بدون این دو، «استثنا» یعنی «قفل نیست».
/// </summary>
public sealed record ClosedPeriodOverride(ClaimsPrincipal Actor, int? ActorUserId, string Reason);

public interface IOperationalPeriodGuard
{
    Task<OperationalPeriodLockStatus> GetStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// اگر تاریخ در دورهٔ بسته باشد <see cref="OperationalPeriodLockedException"/> می‌اندازد.
    /// <paramref name="documentKind"/> فقط برای متن پیام است.
    /// </summary>
    Task EnsureOpenAsync(DateTime transactionDate, string documentKind, CancellationToken cancellationToken = default);

    /// <summary>
    /// همان بررسی، ولی با امکان عبورِ صریح: فقط با Permission مشخص، با دلیل، و با ثبت در Audit.
    /// </summary>
    Task EnsureOpenAsync(
        DateTime transactionDate,
        string documentKind,
        ClosedPeriodOverride? approvedOverride,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// PTG فاز ۹ — عبورِ صریحِ یک‌بارمصرف، بدون آن‌که تاریخِ سند از قبل معلوم باشد.
    ///
    /// فرم‌های ثبت تاریخ را در مدلِ خودشان دارند و همه هم <c>EnsureOpenAsync</c> را صدا
    /// نمی‌زنند؛ قاعده در واپسین لحظه داخل <c>SaveChanges</c> اعمال می‌شود. پس درخواستِ
    /// عبور هم باید همان‌جا شنیده شود: این متد دسترسی و دلیل را بررسی می‌کند، Audit
    /// می‌نویسد و عبور را فقط برای همین <c>DbContext</c> (یعنی همین درخواست) باز می‌کند.
    ///
    /// اگر دورهٔ بسته‌ای در کار نباشد، هیچ اتفاقی نمی‌افتد و چیزی هم ثبت نمی‌شود.
    /// </summary>
    Task ApproveOverrideAsync(
        ClosedPeriodOverride requestedOverride,
        string requestPath,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// PTG-P1-01 — تنها دروازهٔ «آیا در این تاریخ می‌شود سند زد؟» برای دفترِ عملیاتی.
///
/// هیچ کنترلری خودش تاریخ را با چیزی مقایسه نمی‌کند؛ همه همین سرویس را صدا می‌زنند، پس
/// قاعده یک‌جا عوض می‌شود و هیچ مسیری از قلم نمی‌افتد.
/// </summary>
public sealed class OperationalPeriodGuard(
    ApplicationDbContext db,
    IAuditService? audit = null) : IOperationalPeriodGuard
{
    // بدون Encoder صریح، متنِ فارسیِ دلیل به‌صورت escape‌شده در Audit می‌نشیند و همان‌جایی
    // که باید بازرسی‌پذیر باشد خوانده نمی‌شود.
    private static readonly JsonSerializerOptions AuditJson = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private OperationalPeriodLockStatus? _cached;

    public async Task<OperationalPeriodLockStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        // یک درخواست، یک خواندن. قفل در طول یک درخواست عوض نمی‌شود.
        if (_cached is not null)
        {
            return _cached;
        }

        var current = await db.OperationalPeriodLocks
            .AsNoTracking()
            .Where(x => x.IsActive)
            .OrderByDescending(x => x.LockedThroughDate)
            .ThenByDescending(x => x.Id)
            .FirstOrDefaultAsync(cancellationToken);

        _cached = current is null
            ? OperationalPeriodLockStatus.Open
            : new OperationalPeriodLockStatus(true, current.LockedThroughDate.Date, current.Reason);

        return _cached;
    }

    public Task EnsureOpenAsync(DateTime transactionDate, string documentKind, CancellationToken cancellationToken = default)
        => EnsureOpenAsync(transactionDate, documentKind, approvedOverride: null, cancellationToken);

    public async Task EnsureOpenAsync(
        DateTime transactionDate,
        string documentKind,
        ClosedPeriodOverride? approvedOverride,
        CancellationToken cancellationToken = default)
    {
        var status = await GetStatusAsync(cancellationToken);
        if (!status.Covers(transactionDate))
        {
            return;
        }

        var lockedThrough = status.LockedThroughDate!.Value;

        if (approvedOverride is null)
        {
            throw new OperationalPeriodLockedException(BuildMessage(documentKind, transactionDate, lockedThrough), lockedThrough);
        }

        if (!approvedOverride.Actor.HasClaim(AppClaimTypes.Permission, AppPermissions.PostToClosedOperationalPeriod))
        {
            throw new OperationalPeriodLockedException(
                BuildMessage(documentKind, transactionDate, lockedThrough)
                + " ثبت استثنایی در دورهٔ بسته نیاز به دسترسی مخصوص دارد.",
                lockedThrough);
        }

        if (string.IsNullOrWhiteSpace(approvedOverride.Reason))
        {
            throw new OperationalPeriodLockedException(
                BuildMessage(documentKind, transactionDate, lockedThrough)
                + " برای ثبت استثنایی باید دلیل نوشته شود.",
                lockedThrough);
        }

        // بدون ردّ بازرسی، ثبتِ استثنایی انجام نمی‌شود.
        if (audit is null)
        {
            throw new OperationalPeriodLockedException(
                BuildMessage(documentKind, transactionDate, lockedThrough)
                + " ثبت استثنایی بدون سرویس بازرسی ممکن نیست.",
                lockedThrough);
        }

        await audit.LogAsync(
            nameof(OperationalPeriodLock),
            0,
            AuditAction.Approve,
            approvedOverride.ActorUserId,
            JsonSerializer.Serialize(new
            {
                Action = "ExceptionalPostingIntoClosedOperationalPeriod",
                DocumentKind = documentKind,
                TransactionDate = transactionDate.Date,
                LockedThroughDate = lockedThrough,
                approvedOverride.Reason
            }, AuditJson),
            cancellationToken);

        // پشتوانهٔ SaveChanges هم باید بداند این عبور مجاز شمرده شده، وگرنه همان ثبت را
        // یک لایه پایین‌تر رد می‌کند. دامنه‌اش همین DbContext (یعنی همین درخواست) است.
        db.ClosedOperationalPeriodOverrideApproved = true;
    }

    public async Task ApproveOverrideAsync(
        ClosedPeriodOverride requestedOverride,
        string requestPath,
        CancellationToken cancellationToken = default)
    {
        var status = await GetStatusAsync(cancellationToken);
        if (!status.IsLocked)
        {
            // هیچ دوره‌ای بسته نیست — درخواستِ عبور بی‌موضوع است و ردّی هم نمی‌گذارد.
            return;
        }

        var lockedThrough = status.LockedThroughDate!.Value;

        if (!requestedOverride.Actor.HasClaim(AppClaimTypes.Permission, AppPermissions.PostToClosedOperationalPeriod))
        {
            throw new OperationalPeriodLockedException(
                "ثبت استثنایی در دورهٔ بسته نیاز به دسترسی مخصوص دارد.",
                lockedThrough);
        }

        if (string.IsNullOrWhiteSpace(requestedOverride.Reason))
        {
            throw new OperationalPeriodLockedException(
                "برای ثبت استثنایی در دورهٔ بسته باید دلیل نوشته شود.",
                lockedThrough);
        }

        if (audit is null)
        {
            throw new OperationalPeriodLockedException(
                "ثبت استثنایی بدون سرویس بازرسی ممکن نیست.",
                lockedThrough);
        }

        await audit.LogAsync(
            nameof(OperationalPeriodLock),
            0,
            AuditAction.Approve,
            requestedOverride.ActorUserId,
            JsonSerializer.Serialize(new
            {
                Action = "ExceptionalPostingIntoClosedOperationalPeriod",
                RequestPath = requestPath,
                LockedThroughDate = lockedThrough,
                requestedOverride.Reason
            }, AuditJson),
            cancellationToken);

        // دامنه: همین DbContext، یعنی همین درخواست. هیچ چیزی ذخیره یا تمدید نمی‌شود.
        db.ClosedOperationalPeriodOverrideApproved = true;
    }

    /// <summary>
    /// پیام باید بگوید «کدام دوره» بسته است، وگرنه کاربر نمی‌داند تاریخ را به کجا ببرد.
    /// </summary>
    public static string BuildMessage(string documentKind, DateTime transactionDate, DateTime lockedThroughDate)
        => $"دوره مالی این تاریخ بسته شده است و ثبت یا تغییر سند در این دوره مجاز نیست. "
           + $"({documentKind} به تاریخ {transactionDate:yyyy-MM-dd}؛ دوره تا {lockedThroughDate:yyyy-MM-dd} بسته است.)";
}
