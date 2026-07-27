using PTGOilSystem.Web.Models.Entities;

namespace PTGOilSystem.Web.Models.Accounting;

/// <summary>
/// یک گزینهٔ سال مالی در انتخاب‌گرِ هدر. فقط داده‌ی نمایشی است؛ هیچ تصمیمِ مالی اینجا نیست.
/// وضعیت مستقیماً از <see cref="FiscalYear.Status"/> می‌آید و برچسب/کلاسش را
/// <see cref="Helpers.FiscalStatusDisplay"/> می‌سازد.
/// </summary>
public sealed record FiscalYearContextOption(
    int FiscalYearId,
    string Name,
    FiscalYearStatus Status,
    bool IsCurrent,
    bool IsSelected,
    DateTime StartDate,
    DateTime EndDate);

/// <summary>
/// وضعیتِ کاملِ انتخاب‌گرِ سال مالیِ هدر برای یک درخواست.
///
/// این «کانتکستِ کاری/گزارشی» است، نه منبعِ تعیینِ دورهٔ ثبت — دورهٔ ثبت همچنان از
/// AccountingDate و <see cref="Services.Accounting.PeriodGuard"/> می‌آید و اینجا تغییری نمی‌کند.
/// <see cref="SelectedIsWritable"/> فقط برای نمایشِ «فقط‌خواندنی» است؛ مسدودسازیِ واقعیِ ثبت در
/// سالِ بسته کارِ همان گاردِ موجود است.
/// </summary>
public sealed record FiscalYearContextView(
    int? OwnerCompanyId,
    int? SelectedFiscalYearId,
    string? SelectedName,
    FiscalYearStatus? SelectedStatus,
    bool SelectedIsWritable,
    IReadOnlyList<FiscalYearContextOption> Options)
{
    public bool HasSelection => SelectedFiscalYearId is not null;
    public bool HasOptions => Options.Count > 0;
}
