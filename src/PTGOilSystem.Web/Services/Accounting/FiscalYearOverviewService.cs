using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Accounting;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services.Time;

namespace PTGOilSystem.Web.Services.Accounting;

public interface IFiscalYearOverviewService
{
    Task<FiscalYearIndexViewModel> BuildIndexAsync(
        int? companyId,
        bool canManage,
        CancellationToken cancellationToken = default);

    Task<FiscalYearDetailsViewModel?> BuildDetailsAsync(
        int fiscalYearId,
        bool canManage,
        CancellationToken cancellationToken = default);

    Task<NextFiscalYearProposal> BuildNextYearProposalAsync(
        int companyId,
        CancellationToken cancellationToken = default);

    Task<FiscalYear?> FindSourceYearAsync(
        int companyId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// مرحله ۱۰ — تنها منبعِ محاسبهٔ صفحه‌های سال مالی. **فقط می‌خواند**؛ هیچ چیزی نمی‌نویسد.
///
/// همه‌ی جمع‌ها و تصمیم‌های «چه چیزی مجاز است» اینجا حساب می‌شوند تا View فقط چاپ کند.
/// </summary>
public sealed class FiscalYearOverviewService(
    ApplicationDbContext db,
    IAccountingReadinessService readiness) : IFiscalYearOverviewService
{
    /// <summary>
    /// یافته‌های Readiness که به همین صفحه مربوط‌اند. بقیه‌ی یافته‌ها جای خودشان را در
    /// <c>/accounting/readiness</c> دارند و تکرارشان اینجا صفحه را شلوغ می‌کند.
    /// </summary>
    private static readonly string[] FiscalReadinessCodes =
    [
        "NO_OPEN_FISCAL_YEAR",
        "NO_OPEN_FISCAL_PERIOD",
        "MULTIPLE_CURRENT_FISCAL_YEARS"
    ];

    public async Task<FiscalYearIndexViewModel> BuildIndexAsync(
        int? companyId,
        bool canManage,
        CancellationToken cancellationToken = default)
    {
        var companies = await db.Companies.AsNoTracking()
            .Where(c => c.IsActive)
            .OrderBy(c => c.Code)
            .Select(c => new { c.Id, c.Name })
            .ToListAsync(cancellationToken);

        var selectedId = companyId is int requested && companies.Any(c => c.Id == requested)
            ? requested
            : companies.Select(c => (int?)c.Id).FirstOrDefault();

        var options = companies
            .Select(c => new FiscalCompanyOption(c.Id, c.Name, c.Id == selectedId))
            .ToList();

        if (selectedId is not int selected)
        {
            return new FiscalYearIndexViewModel(
                options,
                SelectedCompanyId: null,
                SelectedCompanyName: null,
                CurrentYear: null,
                OtherYears: [],
                new NextFiscalYearProposal(false, "هیچ شرکت فعالی وجود ندارد.", null, null, null, 0),
                ReadinessFindings: [],
                LastCloseRun: null,
                canManage,
                CurrentYearPeriodLockActions: new Dictionary<int, IReadOnlyList<FiscalPeriodLockAction>>());
        }

        var years = await LoadYearSummariesAsync(selected, cancellationToken);
        var currentYear = PickCurrentYear(years);

        var findings = (await readiness.BuildAsync(cancellationToken))
            .Companies
            .FirstOrDefault(c => c.CompanyId == selected)?
            .Findings
            .Where(f => FiscalReadinessCodes.Contains(f.Code))
            .ToList() ?? [];

        var lastRun = currentYear is null
            ? null
            : (await LoadCloseRunsAsync(currentYear.FiscalYearId, cancellationToken)).FirstOrDefault();

        // قفل/بازکردنِ دورهٔ سالِ جاری روی صفحهٔ سادهٔ سال مالی. همان قاعدهٔ صفحهٔ جزئیات بازاستفاده
        // می‌شود تا دکمه هرگز کاری را پیشنهاد ندهد که سرویس ردش می‌کند.
        var lockActions = currentYear is null
            ? new Dictionary<int, IReadOnlyList<FiscalPeriodLockAction>>()
            : currentYear.Periods.ToDictionary(
                p => p.PeriodId,
                p => BuildLockActions(currentYear.Status, p.Status, canManage));

        return new FiscalYearIndexViewModel(
            options,
            selected,
            companies.First(c => c.Id == selected).Name,
            currentYear,
            years.Where(y => currentYear is null || y.FiscalYearId != currentYear.FiscalYearId).ToList(),
            await BuildNextYearProposalAsync(selected, cancellationToken),
            findings,
            lastRun,
            canManage,
            lockActions);
    }

    public async Task<FiscalYearDetailsViewModel?> BuildDetailsAsync(
        int fiscalYearId,
        bool canManage,
        CancellationToken cancellationToken = default)
    {
        var summary = (await LoadYearSummariesAsync(companyId: null, cancellationToken, fiscalYearId))
            .FirstOrDefault();
        if (summary is null)
            return null;

        // جمع بدهکار/بستانکار از سطرهای سندِ پست‌شدهٔ همین دوره. سند Draft در جمع نمی‌آید چون
        // هنوز اثر حسابداری ندارد؛ مرحله ۱۲ جداگانه وجودشان را گزارش می‌کند.
        var totals = await db.JournalEntryLines.AsNoTracking()
            .Where(l => l.JournalEntry!.FiscalYearId == fiscalYearId
                && l.JournalEntry.Status == JournalEntryStatus.Posted)
            .GroupBy(l => l.JournalEntry!.FiscalPeriodId)
            .Select(g => new
            {
                PeriodId = g.Key,
                Debit = g.Sum(x => x.Debit),
                Credit = g.Sum(x => x.Credit)
            })
            .ToListAsync(cancellationToken);

        var counts = await db.JournalEntries.AsNoTracking()
            .Where(j => j.FiscalYearId == fiscalYearId && j.Status == JournalEntryStatus.Posted)
            .GroupBy(j => j.FiscalPeriodId)
            .Select(g => new { PeriodId = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        var lockOwners = await db.FiscalPeriods.AsNoTracking()
            .Where(p => p.FiscalYearId == fiscalYearId)
            .Select(p => new
            {
                p.Id,
                p.LockedAt,
                LockedBy = p.LockedByUser != null ? p.LockedByUser.Username : null
            })
            .ToListAsync(cancellationToken);

        var rows = summary.Periods
            .Select(p =>
            {
                var total = totals.FirstOrDefault(t => t.PeriodId == p.PeriodId);
                var owner = lockOwners.FirstOrDefault(l => l.Id == p.PeriodId);
                return new FiscalPeriodDetailRow(
                    p.PeriodId,
                    p.PeriodNumber,
                    p.Name,
                    p.StartDate,
                    p.EndDate,
                    p.Status,
                    p.IsCurrent,
                    owner?.LockedAt,
                    owner?.LockedBy,
                    counts.FirstOrDefault(c => c.PeriodId == p.PeriodId)?.Count ?? 0,
                    total?.Debit ?? 0m,
                    total?.Credit ?? 0m,
                    BuildLockActions(summary.Status, p.Status, canManage));
            })
            .ToList();

        return new FiscalYearDetailsViewModel(
            summary,
            rows,
            await LoadCloseRunsAsync(fiscalYearId, cancellationToken),
            rows.Sum(r => r.JournalCount),
            rows.Sum(r => r.TotalDebit),
            rows.Sum(r => r.TotalCredit),
            canManage);
    }

    /// <summary>
    /// سالِ بعد **از روی سالِ جاری آینه می‌شود** — همان تعداد دوره، هر تاریخ دقیقاً یک سال جلوتر.
    /// قرارداد تقویمِ شرکت حدس زده نمی‌شود؛ اگر سال مالی‌ای وجود نداشته باشد که از رویش آینه شود،
    /// دکمه اصلاً مجاز نیست.
    ///
    /// منبعِ آینه عمداً **سالِ جاری** است، نه تازه‌ترین سال: وگرنه هر بار که سالِ بعد ساخته می‌شد
    /// خودش منبعِ بعدی می‌شد و کاربر می‌توانست زنجیرهٔ بی‌پایانی از سال‌های پیش‌نویس بسازد. با این
    /// قاعده حداکثر یک سال جلوتر از سالِ جاری وجود دارد.
    /// </summary>
    public async Task<NextFiscalYearProposal> BuildNextYearProposalAsync(
        int companyId,
        CancellationToken cancellationToken = default)
    {
        var source = await FindSourceYearAsync(companyId, cancellationToken);

        if (source is null)
        {
            return new NextFiscalYearProposal(
                false,
                "این شرکت هیچ سال مالی ندارد؛ سال اول باید صریح ساخته شود تا قرارداد تقویم معلوم شود.",
                null, null, null, 0);
        }

        var periods = await db.FiscalPeriods.AsNoTracking()
            .Where(p => p.FiscalYearId == source.Id)
            .OrderBy(p => p.PeriodNumber)
            .Select(p => new { p.PeriodNumber, p.StartDate, p.EndDate })
            .ToListAsync(cancellationToken);

        if (periods.Count == 0)
        {
            return new NextFiscalYearProposal(
                false,
                $"سال مالی جاری («{source.Name}») هیچ دوره‌ای ندارد؛ چیزی برای آینه‌کردن وجود ندارد.",
                null, null, null, 0);
        }

        var start = source.StartDate.AddYears(1).Date;
        var end = source.EndDate.AddYears(1).Date;

        var overlaps = await db.FiscalYears.AsNoTracking()
            .AnyAsync(
                y => y.CompanyId == companyId && y.StartDate <= end && y.EndDate >= start,
                cancellationToken);

        if (overlaps)
        {
            return new NextFiscalYearProposal(
                false,
                "سال مالی بعدی از قبل وجود دارد.",
                null, null, null, 0);
        }

        return new NextFiscalYearProposal(
            true,
            null,
            BuildNextYearName(source.Name, source.StartDate),
            start,
            end,
            periods.Count);
    }

    /// <summary>
    /// نامِ سال بعد: اگر نام سالِ قبل عددِ سالِ شروعش را داشته باشد همان عدد یکی جلو می‌رود،
    /// وگرنه نام از تاریخ ساخته می‌شود. هیچ الگوی ناشناخته‌ای اختراع نمی‌شود.
    /// </summary>
    /// <summary>
    /// عملیاتِ قفلی که روی این دوره مجاز است — همان قاعده‌ای که
    /// <see cref="FiscalPeriodLockService"/> اجرا می‌کند، تا دکمهٔ صفحه هرگز کاری را پیشنهاد نکند
    /// که سرویس ردش می‌کند.
    ///
    /// قفلِ سخت هیچ عملیاتی ندارد: برگشت‌ناپذیر است. دورهٔ سالِ بسته هم هیچ عملیاتی ندارد.
    /// </summary>
    private static IReadOnlyList<FiscalPeriodLockAction> BuildLockActions(
        FiscalYearStatus yearStatus,
        FiscalPeriodStatus periodStatus,
        bool canManage)
    {
        if (!canManage
            || yearStatus == FiscalYearStatus.Closed
            || periodStatus == FiscalPeriodStatus.HardLocked)
        {
            return [];
        }

        return periodStatus switch
        {
            FiscalPeriodStatus.Open =>
            [
                new(FiscalPeriodStatus.SoftLocked, "قفل نرم",
                    "ثبت عادی در این دوره بسته شود؟ فقط عملیات استثنایی با مجوز صریح ممکن می‌ماند."),
                new(FiscalPeriodStatus.HardLocked, "قفل سخت",
                    "قفل سخت برگشت‌ناپذیر است. بعد از آن هیچ ثبت، برگشت یا اصلاحی در این دوره ممکن نیست. ادامه می‌دهید؟")
            ],
            FiscalPeriodStatus.SoftLocked =>
            [
                new(FiscalPeriodStatus.Open, "بازکردن", "دوره دوباره برای ثبت عادی باز شود؟"),
                new(FiscalPeriodStatus.HardLocked, "قفل سخت",
                    "قفل سخت برگشت‌ناپذیر است. بعد از آن هیچ ثبت، برگشت یا اصلاحی در این دوره ممکن نیست. ادامه می‌دهید؟")
            ],
            _ => []
        };
    }

    /// <summary>
    /// سالی که «سالِ بعد» از رویش آینه می‌شود — دقیقاً همان سالی که صفحه به‌عنوان سالِ جاری نشان
    /// می‌دهد. <see cref="FiscalYearProvisioningService"/> هم از همین متد استفاده می‌کند تا پیشنهاد
    /// و ساخت هرگز به دو سال متفاوت اشاره نکنند.
    /// </summary>
    public async Task<FiscalYear?> FindSourceYearAsync(
        int companyId,
        CancellationToken cancellationToken = default)
    {
        var years = await db.FiscalYears.AsNoTracking()
            .Where(y => y.CompanyId == companyId)
            .OrderByDescending(y => y.StartDate)
            .ThenByDescending(y => y.Id)
            .ToListAsync(cancellationToken);

        if (years.Count == 0)
            return null;

        var today = AfghanistanBusinessClock.SystemToday;
        return years.FirstOrDefault(y => y.IsCurrent)
            ?? years.FirstOrDefault(y => y.StartDate <= today && y.EndDate >= today)
            ?? years[0];
    }

    private static string BuildNextYearName(string sourceName, DateTime sourceStart)
    {
        var sourceYear = sourceStart.Year.ToString();
        var nextYear = sourceStart.AddYears(1).Year.ToString();
        return sourceName.Contains(sourceYear, StringComparison.Ordinal)
            ? sourceName.Replace(sourceYear, nextYear, StringComparison.Ordinal)
            : $"FY-{nextYear}";
    }

    private async Task<List<FiscalYearSummary>> LoadYearSummariesAsync(
        int? companyId,
        CancellationToken cancellationToken,
        int? fiscalYearId = null)
    {
        var query = db.FiscalYears.AsNoTracking().AsQueryable();
        if (companyId is int cid)
            query = query.Where(y => y.CompanyId == cid);
        if (fiscalYearId is int yid)
            query = query.Where(y => y.Id == yid);

        var years = await query
            .OrderByDescending(y => y.StartDate)
            .Select(y => new
            {
                y.Id,
                y.CompanyId,
                CompanyName = y.Company != null ? y.Company.Name : "",
                y.Name,
                y.StartDate,
                y.EndDate,
                y.Status,
                y.IsCurrent,
                y.ClosedAt,
                ClosedByUser = y.ClosedByUser != null ? y.ClosedByUser.Username : null,
                y.ClosingJournalEntryId
            })
            .ToListAsync(cancellationToken);

        if (years.Count == 0)
            return [];

        var yearIds = years.Select(y => y.Id).ToList();
        var periods = await db.FiscalPeriods.AsNoTracking()
            .Where(p => yearIds.Contains(p.FiscalYearId))
            .OrderBy(p => p.FiscalYearId).ThenBy(p => p.PeriodNumber)
            .Select(p => new
            {
                p.Id,
                p.FiscalYearId,
                p.PeriodNumber,
                p.Name,
                p.StartDate,
                p.EndDate,
                p.Status
            })
            .ToListAsync(cancellationToken);

        var today = AfghanistanBusinessClock.SystemToday;

        return years.Select(y =>
        {
            var rows = periods
                .Where(p => p.FiscalYearId == y.Id)
                .Select(p => new FiscalPeriodRow(
                    p.Id,
                    p.PeriodNumber,
                    p.Name,
                    p.StartDate,
                    p.EndDate,
                    p.Status,
                    p.StartDate <= today && p.EndDate >= today))
                .ToList();

            return new FiscalYearSummary(
                y.Id,
                y.CompanyId,
                y.CompanyName,
                y.Name,
                y.StartDate,
                y.EndDate,
                y.Status,
                y.IsCurrent,
                rows.Count,
                rows.FirstOrDefault(p => p.IsCurrent),
                rows,
                y.ClosedAt,
                y.ClosedByUser,
                y.ClosingJournalEntryId);
        }).ToList();
    }

    /// <summary>
    /// سالِ «جاری»: اول پرچمِ صریحِ <see cref="FiscalYear.IsCurrent"/>، وگرنه سالی که امروز
    /// داخلش است. اگر هیچ‌کدام، تازه‌ترین سال.
    /// </summary>
    private static FiscalYearSummary? PickCurrentYear(List<FiscalYearSummary> years)
    {
        var today = AfghanistanBusinessClock.SystemToday;
        return years.FirstOrDefault(y => y.IsCurrent)
            ?? years.FirstOrDefault(y => y.StartDate <= today && y.EndDate >= today)
            ?? years.FirstOrDefault();
    }

    private async Task<List<FiscalCloseRunRow>> LoadCloseRunsAsync(
        int fiscalYearId,
        CancellationToken cancellationToken)
        => await db.FiscalYearCloseRuns.AsNoTracking()
            .Where(r => r.FiscalYearId == fiscalYearId)
            .OrderByDescending(r => r.StartedAt)
            .ThenByDescending(r => r.Id)
            .Select(r => new FiscalCloseRunRow(
                r.Id,
                r.Status,
                r.StartedAt,
                r.StartedByUser != null ? r.StartedByUser.Username : null,
                r.CompletedAt,
                r.CompletedByUser != null ? r.CompletedByUser.Username : null,
                r.ClosingJournalEntryId,
                r.OpeningJournalEntryId,
                r.FailureMessage))
            .ToListAsync(cancellationToken);
}
