using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services.Accounting;

namespace PTGOilSystem.Web.Models.Accounting;

/// <summary>
/// مرحله ۱۰ — مدل‌های نمایشِ سال مالی.
///
/// همه‌ی محاسبه‌ها در سرویس انجام می‌شود و View فقط چاپ می‌کند؛ هیچ منطق مالی داخل Razor نیست.
/// </summary>
public sealed record FiscalCompanyOption(int CompanyId, string Name, bool IsSelected);

public sealed record FiscalPeriodRow(
    int PeriodId,
    int PeriodNumber,
    string Name,
    DateTime StartDate,
    DateTime EndDate,
    FiscalPeriodStatus Status,
    bool IsCurrent);

/// <summary>
/// یک عملیاتِ قفلِ مجاز روی دوره. اینکه چه چیزی مجاز است در سرویس تصمیم گرفته می‌شود، نه در View —
/// وگرنه دکمهٔ صفحه و قاعدهٔ سرویس می‌توانند از هم جدا بیفتند.
/// </summary>
public sealed record FiscalPeriodLockAction(
    FiscalPeriodStatus TargetStatus,
    string Label,
    string ConfirmMessage);

/// <summary>سطر جدولِ دورهٔ صفحهٔ جزئیات — با ارقام سند. بازهٔ AccountingDate همان بازهٔ دوره است.</summary>
public sealed record FiscalPeriodDetailRow(
    int PeriodId,
    int PeriodNumber,
    string Name,
    DateTime StartDate,
    DateTime EndDate,
    FiscalPeriodStatus Status,
    bool IsCurrent,
    DateTime? LockedAt,
    string? LockedByUser,
    int JournalCount,
    decimal TotalDebit,
    decimal TotalCredit,
    IReadOnlyList<FiscalPeriodLockAction> AllowedLockActions)
{
    public decimal Difference => TotalDebit - TotalCredit;
    public bool IsBalanced => Difference == 0m;
}

/// <summary>
/// آخرین اجرای بستن. مرحله‌های ۱۳ (Trial Close) و ۱۴ (Final Close) هنوز شروع نشده‌اند، پس
/// <see cref="FiscalYearCloseRun"/> هیچ نشانه‌ای برای تفکیک آزمایشی از نهایی ندارد و ساختنِ آن
/// نشانه حدس می‌بود. آنچه اثبات‌پذیر است همین است: اجراهای ثبت‌شده، و بسته‌شدنِ خودِ سال.
/// </summary>
public sealed record FiscalCloseRunRow(
    int RunId,
    FiscalYearCloseRunStatus Status,
    DateTime StartedAt,
    string? StartedByUser,
    DateTime? CompletedAt,
    string? CompletedByUser,
    int? ClosingJournalEntryId,
    int? OpeningJournalEntryId,
    string? FailureMessage);

public sealed record FiscalYearSummary(
    int FiscalYearId,
    int CompanyId,
    string CompanyName,
    string Name,
    DateTime StartDate,
    DateTime EndDate,
    FiscalYearStatus Status,
    bool IsCurrent,
    int PeriodCount,
    FiscalPeriodRow? CurrentPeriod,
    IReadOnlyList<FiscalPeriodRow> Periods,
    DateTime? ClosedAt,
    string? ClosedByUser,
    int? ClosingJournalEntryId);

/// <summary>
/// اجازهٔ ساختِ سال بعد. <paramref name="BlockedReason"/> وقتی پر است که دکمه نباید دیده شود —
/// دلیلش به کاربر گفته می‌شود تا نبودِ دکمه معما نباشد.
/// </summary>
public sealed record NextFiscalYearProposal(
    bool IsAllowed,
    string? BlockedReason,
    string? ProposedName,
    DateTime? ProposedStartDate,
    DateTime? ProposedEndDate,
    int ProposedPeriodCount);

public sealed record FiscalYearIndexViewModel(
    IReadOnlyList<FiscalCompanyOption> Companies,
    int? SelectedCompanyId,
    string? SelectedCompanyName,
    FiscalYearSummary? CurrentYear,
    IReadOnlyList<FiscalYearSummary> OtherYears,
    NextFiscalYearProposal NextYear,
    IReadOnlyList<AccountingReadinessFinding> ReadinessFindings,
    FiscalCloseRunRow? LastCloseRun,
    bool CanManage,
    // عملیاتِ قفلِ مجاز برای هر دورهٔ سالِ جاری — همان قاعدهٔ صفحهٔ جزئیات، تا صفحهٔ سادهٔ سال مالی
    // بتواند قفل/بازکردنِ دوره را همان‌جا نشان دهد. تصمیمِ «چه چیزی مجاز است» در سرویس گرفته می‌شود.
    IReadOnlyDictionary<int, IReadOnlyList<FiscalPeriodLockAction>> CurrentYearPeriodLockActions);

public sealed record FiscalYearDetailsViewModel(
    FiscalYearSummary Year,
    IReadOnlyList<FiscalPeriodDetailRow> Periods,
    IReadOnlyList<FiscalCloseRunRow> CloseRuns,
    int TotalJournalCount,
    decimal TotalDebit,
    decimal TotalCredit,
    bool CanManage)
{
    public decimal Difference => TotalDebit - TotalCredit;
    public bool IsBalanced => Difference == 0m;
}
