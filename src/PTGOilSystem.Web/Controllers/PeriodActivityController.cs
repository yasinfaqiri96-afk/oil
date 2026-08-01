using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using PTGOilSystem.Web.Configuration;
using PTGOilSystem.Web.Helpers;
using PTGOilSystem.Web.Infrastructure.RateLimiting;
using PTGOilSystem.Web.Models.Accounting;
using PTGOilSystem.Web.Security;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Accounting;
using PTGOilSystem.Web.Services.Exports;

namespace PTGOilSystem.Web.Controllers;

/// <summary>
/// صفحهٔ فقط‌خواندنیِ «مشاهده فعالیت‌ها» برای یک دورهٔ مالی. خلاصهٔ همهٔ عملیاتِ همان بازهٔ تاریخی
/// را نشان می‌دهد. شرکت همیشه شرکتِ مالکِ سیستم است و سمتِ سرور تعیین می‌شود؛ دورهٔ شرکتِ دیگر
/// دیده نمی‌شود. اگر <c>Accounting.Enabled=false</c> باشد بخشِ اسناد حسابداری خالی می‌ماند.
/// </summary>
[Authorize(Policy = AuthPolicies.ManageData)]
[Route("fiscal/period-activity")]
public sealed class PeriodActivityController(
    IPeriodActivityService activity,
    ISystemCompanyProvider systemCompany,
    IOptions<AccountingOptions> accountingOptions) : Controller
{
    [HttpGet("")]
    public async Task<IActionResult> Index(int periodId, CancellationToken cancellationToken)
    {
        var ownerCompanyId = await systemCompany.GetOwnerCompanyIdAsync(cancellationToken);

        // ورود از «مرکز گزارشات» شناسهٔ دوره ندارد؛ در آن حالت دورهٔ جاری انتخاب و به همان
        // نشانی با periodId هدایت می‌شود تا لینک قابل اشتراک بماند و صفحه ۴۰۴ ندهد.
        if (periodId <= 0)
        {
            var defaultPeriodId = await activity.FindDefaultPeriodIdAsync(ownerCompanyId, cancellationToken);
            return defaultPeriodId is int resolved
                ? RedirectToAction(nameof(Index), new { periodId = resolved })
                : RedirectToAction("Index", "FiscalYears");
        }

        var model = await activity.BuildAsync(
            periodId, ownerCompanyId, accountingOptions.Value.Enabled, cancellationToken);

        return model is null ? NotFound() : View(model);
    }

    /// <summary>
    /// خروجی Excel/PDF همان صفحه. دقیقاً همان سرویس و همان دوره را می‌خواند؛ هیچ عددی
    /// دوباره محاسبه نمی‌شود. هشت بخش صفحه در یک جدول با ستون «بخش» می‌آید.
    /// </summary>
    [HttpGet("export")]
    [EnableRateLimiting(RateLimitPolicies.CsvExport)]
    public async Task<IActionResult> Export(string? format, int periodId, CancellationToken cancellationToken)
    {
        var ownerCompanyId = await systemCompany.GetOwnerCompanyIdAsync(cancellationToken);

        if (periodId <= 0)
        {
            var defaultPeriodId = await activity.FindDefaultPeriodIdAsync(ownerCompanyId, cancellationToken);
            if (defaultPeriodId is not int resolved)
            {
                return RedirectToAction("Index", "FiscalYears");
            }

            periodId = resolved;
        }

        var model = await activity.BuildAsync(
            periodId, ownerCompanyId, accountingOptions.Value.Enabled, cancellationToken);

        if (model is null)
        {
            return NotFound();
        }

        var isEn = UiText.IsEn(HttpContext);

        IEnumerable<TabularExportRow> Section(string sectionFa, string sectionEn, IReadOnlyList<PeriodActivityRow> rows)
            => rows.Select(row => new TabularExportRow(
            [
                TabularExportCell.Text(isEn ? sectionEn : sectionFa),
                TabularExportCell.Date(row.Date),
                TabularExportCell.Text(row.Title),
                TabularExportCell.Text(row.Subtitle),
                TabularExportCell.Number(row.QuantityMt),
                TabularExportCell.Number(row.AmountUsd),
                TabularExportCell.Text(row.Status)
            ]));

        var exportRows = Section("خرید", "Purchases", model.Purchases)
            .Concat(Section("بارگیری", "Loadings", model.Loadings))
            .Concat(Section("فروش", "Sales", model.Sales))
            .Concat(Section("دریافت", "Receipts", model.Receipts))
            .Concat(Section("پرداخت", "Payments", model.Payments))
            .Concat(Section("مصارف", "Expenses", model.Expenses))
            .Concat(Section("حرکت موجودی", "Inventory movements", model.InventoryMovements))
            .Concat(Section("اسناد حسابداری", "Journals", model.Journals))
            .ToList();

        return TabularExportSupport.File(this, format, new TabularExportDocument
        {
            FileNameStem = "PTG_Period_Activity",
            TitleFa = "فعالیت دوره",
            TitleEn = "Period Activity",
            KnownRowCount = exportRows.Count,
            ForceLandscape = true,
            Filters = TabularExportSupport.FilterSummary(
                ("شرکت / Company", model.CompanyName),
                ("سال مالی / Fiscal year", model.FiscalYearName),
                ("دوره / Period", model.PeriodName),
                ("از تاریخ / From", model.StartDate.ToString("yyyy-MM-dd")),
                ("تا تاریخ / To", model.EndDate.ToString("yyyy-MM-dd"))),
            Columns =
            [
                new("بخش", "Section", Width: 18),
                new("تاریخ", "Date", TabularExportValueType.Date, 13),
                new("عنوان", "Title", Width: 26),
                new("شرح", "Detail", Width: 30, Wrap: true),
                new("مقدار MT", "Quantity MT", TabularExportValueType.Number, 15),
                new("مبلغ USD", "Amount USD", TabularExportValueType.Number, 16),
                new("وضعیت", "Status", Width: 16)
            ],
            Rows = exportRows
        });
    }
}
