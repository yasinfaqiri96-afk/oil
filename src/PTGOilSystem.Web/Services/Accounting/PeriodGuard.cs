using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Security;
using PTGOilSystem.Web.Services;

namespace PTGOilSystem.Web.Services.Accounting;

/// <summary>
/// درخواستِ ثبتِ استثنایی در دورهٔ قفل‌نرم. عمداً هم بازیگر می‌خواهد هم دلیل: بدون این دو، ثبت
/// استثنایی قابل بازرسی نیست و «استثنا» عملاً یعنی «قفل نیست».
/// </summary>
public sealed record SoftLockPostingException(ClaimsPrincipal Actor, int? ActorUserId, string Reason);

public interface IPeriodGuard
{
    Task<FiscalCalendarSelection> EnsurePostingAllowedAsync(
        int companyId,
        DateTime accountingDate,
        CancellationToken cancellationToken = default);

    Task<FiscalCalendarSelection> EnsureExceptionalPostingAllowedAsync(
        int companyId,
        DateTime accountingDate,
        SoftLockPostingException exception,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// مرحله ۱۱ — تنها دروازهٔ تاریخِ حسابداری.
///
/// هر سند و هر برگشتی از <see cref="AccountingPostingService"/> رد می‌شود و آن هم پیش از ساختنِ
/// هر چیزی همین گارد را صدا می‌زند؛ یعنی مسیرِ دورزننده‌ای وجود ندارد
/// (تست‌شده: `Only_The_Posting_Service_Creates_Journal_Entries`).
///
/// AccountingDate عمداً تنها تاریخی است که این گارد می‌سنجد. `DocumentDate` و `OperationDate`
/// می‌گویند سند و رویدادِ تجاری کِی بودند؛ AccountingDate می‌گوید سند در کدام دفتر می‌نشیند و
/// فقط همین یکی به دوره ربط دارد.
/// </summary>
public sealed class PeriodGuard(
    ApplicationDbContext db,
    IFiscalCalendarService fiscalCalendar,
    IAuditService? audit = null,
    Time.IAfghanistanBusinessClock? businessClock = null) : IPeriodGuard
{
    // «امروز» باید روز کاری کابل باشد. کابل UTC+04:30 است، پس بین 19:30 تا 23:59 به وقت
    // UTC، «امروزِ کابل» یک روز جلوتر از تاریخ UTC است؛ با معیار UTC، سندی که همین امروز
    // در کابل ثبت می‌شود به‌اشتباه «تاریخ آینده» شناخته می‌شد.
    private readonly Time.IAfghanistanBusinessClock _businessClock =
        businessClock ?? new Time.AfghanistanBusinessClock(TimeProvider.System);

    public Task<FiscalCalendarSelection> EnsurePostingAllowedAsync(
        int companyId,
        DateTime accountingDate,
        CancellationToken cancellationToken = default)
        => EnsureAllowedAsync(companyId, accountingDate, exception: null, cancellationToken);

    public Task<FiscalCalendarSelection> EnsureExceptionalPostingAllowedAsync(
        int companyId,
        DateTime accountingDate,
        SoftLockPostingException exception,
        CancellationToken cancellationToken = default)
        => EnsureAllowedAsync(companyId, accountingDate, exception, cancellationToken);

    private async Task<FiscalCalendarSelection> EnsureAllowedAsync(
        int companyId,
        DateTime accountingDate,
        SoftLockPostingException? exception,
        CancellationToken cancellationToken)
    {
        if (companyId <= 0 || !await db.Companies.AsNoTracking()
                .AnyAsync(x => x.Id == companyId && x.IsActive, cancellationToken))
        {
            throw new AccountingValidationException(
                "INVALID_COMPANY",
                "The accounting company is missing or inactive.");
        }

        // تاریخ آینده هرگز — سندی که هنوز اتفاق نیفتاده نباید دفتر را تکان بدهد. تاریخِ گذشته
        // به‌خودی‌خود ممنوع نیست؛ آنچه Backdating را محدود می‌کند قفلِ دوره است، نه قدمتِ تاریخ.
        if (accountingDate.Date > _businessClock.Today)
        {
            throw new AccountingValidationException(
                "ACCOUNTING_DATE_OUT_OF_RANGE",
                "The accounting date is in the future.");
        }

        var lookup = await fiscalCalendar.ResolveAsync(companyId, accountingDate, cancellationToken);

        switch (lookup.Resolution)
        {
            case FiscalCalendarResolution.Open:
                return new FiscalCalendarSelection(lookup.FiscalYear!, lookup.FiscalPeriod!);

            case FiscalCalendarResolution.PeriodSoftLocked when exception is not null:
                await AuthorizeAndAuditExceptionAsync(lookup, exception, cancellationToken);
                return new FiscalCalendarSelection(lookup.FiscalYear!, lookup.FiscalPeriod!);

            case FiscalCalendarResolution.FiscalYearNotFound:
                throw new AccountingValidationException(
                    "ACCOUNTING_DATE_OUT_OF_RANGE",
                    "The accounting date is outside every fiscal year of this company.");

            case FiscalCalendarResolution.FiscalYearClosed:
                throw new AccountingValidationException(
                    "FISCAL_YEAR_CLOSED",
                    "The fiscal year of this accounting date is closed.");

            case FiscalCalendarResolution.FiscalYearNotOpen:
                throw new AccountingValidationException(
                    "FISCAL_YEAR_NOT_OPEN",
                    "The fiscal year of this accounting date is not open for posting.");

            case FiscalCalendarResolution.PeriodNotFound:
                throw new AccountingValidationException(
                    "PERIOD_NOT_FOUND",
                    "The fiscal year has no period covering this accounting date.");

            case FiscalCalendarResolution.CompanyPeriodMismatch:
                throw new AccountingValidationException(
                    "COMPANY_PERIOD_MISMATCH",
                    "The fiscal period belongs to a different company.");

            case FiscalCalendarResolution.PeriodSoftLocked:
                throw new AccountingValidationException(
                    "PERIOD_SOFT_LOCKED",
                    "The fiscal period is soft locked; normal posting is not allowed.");

            default:
                // قفل سخت استثنا ندارد — نه Permission، نه دلیل، هیچ چیز بازش نمی‌کند.
                throw new AccountingValidationException(
                    "PERIOD_HARD_LOCKED",
                    "The fiscal period is hard locked; posting, reversal and repost are not allowed.");
        }
    }

    private async Task AuthorizeAndAuditExceptionAsync(
        FiscalCalendarLookup lookup,
        SoftLockPostingException exception,
        CancellationToken cancellationToken)
    {
        if (!exception.Actor.HasClaim(AppClaimTypes.Permission, AppPermissions.PostToSoftLockedPeriod))
        {
            throw new AccountingValidationException(
                "PERIOD_SOFT_LOCKED",
                "Posting into a soft locked period requires the PostToSoftLockedPeriod permission.");
        }

        if (string.IsNullOrWhiteSpace(exception.Reason))
        {
            throw new AccountingValidationException(
                "PERIOD_EXCEPTION_REASON_REQUIRED",
                "An exceptional posting into a soft locked period requires a reason.");
        }

        // بدون Audit، ثبتِ استثنایی انجام نمی‌شود. استثنای بی‌ردّ با «قفل نبودن» فرقی ندارد.
        if (audit is null)
        {
            throw new AccountingValidationException(
                "PERIOD_EXCEPTION_AUDIT_UNAVAILABLE",
                "An exceptional posting cannot proceed without the audit service.");
        }

        await audit.LogAsync(
            nameof(FiscalPeriod),
            lookup.FiscalPeriod!.Id,
            AuditAction.Approve,
            exception.ActorUserId,
            JsonSerializer.Serialize(new
            {
                Action = "ExceptionalPostingIntoSoftLockedPeriod",
                lookup.FiscalPeriod.CompanyId,
                FiscalYearId = lookup.FiscalYear!.Id,
                FiscalPeriodId = lookup.FiscalPeriod.Id,
                PeriodStatus = nameof(FiscalPeriodStatus.SoftLocked),
                exception.Reason
            }),
            cancellationToken);
    }
}
