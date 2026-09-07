using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using PTGOilSystem.Web.Infrastructure.RateLimiting;
using PTGOilSystem.Web.Models.Reports;
using PTGOilSystem.Web.Services.Exports;

namespace PTGOilSystem.Web.Controllers;

/// <summary>
/// «سررسید طلبات و بدهی‌ها» — فقط‌خواندنی. همان مانده‌های گزارش «طلبات و بدهی‌ها»
/// را می‌گیرد و بر اساس اینکه حساب چند روز است حرکتی نداشته، در چهار دسته می‌گذارد:
/// تا ۳۰ روز، ۳۱ تا ۶۰، ۶۱ تا ۹۰ و بیشتر از ۹۰ روز.
///
/// <para>مبنا عمداً «آخرین حرکت حساب» است، نه تاریخ سررسید: سیستم برای طرف‌حساب‌ها
/// سررسید جداگانه‌ای ثبت نمی‌کند و این گزارش هیچ تاریخی از خود نمی‌سازد.</para>
///
/// <para>هیچ مانده‌ای اینجا دوباره حساب نمی‌شود؛ منبع یگانه همان
/// <c>BuildReceivablesPayablesReportAsync</c> است.</para>
/// </summary>
public partial class ReportsController
{
    [EnableRateLimiting(RateLimitPolicies.HeavyReport)]
    public async Task<IActionResult> PartyAging([FromQuery] ManagementReportFilterViewModel? filter = null)
    {
        filter ??= new ManagementReportFilterViewModel();
        await PopulateLookupsAsync(filter, includeCustomers: true, includeSuppliers: true);
        return View(await BuildPartyAgingReportAsync(filter));
    }

    [HttpGet]
    [EnableRateLimiting(RateLimitPolicies.CsvExport)]
    public async Task<IActionResult> PartyAgingExport(
        string? format,
        [FromQuery] ManagementReportFilterViewModel? filter = null)
    {
        filter ??= new ManagementReportFilterViewModel();
        var model = await BuildPartyAgingReportAsync(filter);

        return TabularExportSupport.File(this, format, new TabularExportDocument
        {
            FileNameStem = "PTG_Party_Aging",
            TitleFa = "سررسید طلبات و بدهی‌ها",
            TitleEn = "Receivable & Payable Aging",
            KnownRowCount = model.Rows.Count,
            ForceLandscape = true,
            Filters = TabularExportSupport.FilterSummary(
                ("از تاریخ / From", filter.FromDate?.ToString("yyyy-MM-dd")),
                ("تا تاریخ / To", filter.ToDate?.ToString("yyyy-MM-dd")),
                ("تاریخ سنجش / As of", model.AsOfDate.ToString("yyyy-MM-dd"))),
            Columns =
            [
                new("طرف حساب", "Party", Width: 24),
                new("نوع", "Type", Width: 16),
                new("طلب (USD)", "Receivable (USD)", TabularExportValueType.Number, 16),
                new("بدهی (USD)", "Payable (USD)", TabularExportValueType.Number, 16),
                new("آخرین حرکت", "Last movement", TabularExportValueType.Date, 14),
                new("روز بدون حرکت", "Days idle", TabularExportValueType.Integer, 14),
                new("دسته", "Bucket", Width: 16)
            ],
            Rows = model.Rows.Select(r => new TabularExportRow(
            [
                TabularExportCell.Text(r.PartyName),
                TabularExportCell.Text(r.PartyTypeLabel),
                TabularExportCell.Number(r.ReceivableUsd),
                TabularExportCell.Number(r.PayableUsd),
                TabularExportCell.Date(r.LastEntryDate),
                TabularExportCell.Integer(r.DaysIdle),
                TabularExportCell.Text(PartyAgingBucketLabel(r.Bucket))
            ])),
            Totals = new TabularExportRow(
            [
                TabularExportCell.Text("جمع / Total"),
                TabularExportCell.Text(null),
                TabularExportCell.Number(model.TotalReceivableUsd),
                TabularExportCell.Number(model.TotalPayableUsd),
                TabularExportCell.Text(null),
                TabularExportCell.Text(null),
                TabularExportCell.Text($"{model.PartyCount:N0} طرف حساب")
            ])
        });
    }

    /// <summary>نام دری دستهٔ سن حساب.</summary>
    internal static string PartyAgingBucketLabel(PartyAgingBucket bucket) => bucket switch
    {
        PartyAgingBucket.UpTo30 => "تا ۳۰ روز",
        PartyAgingBucket.From31To60 => "۳۱ تا ۶۰ روز",
        PartyAgingBucket.From61To90 => "۶۱ تا ۹۰ روز",
        _ => "بیشتر از ۹۰ روز"
    };

    /// <summary>نام دری نوع طرف‌حساب.</summary>
    internal static string PartyAgingPartyTypeLabel(string partyType) => partyType switch
    {
        "Customer" => "مشتری",
        "Supplier" => "تأمین‌کننده",
        "ServiceProvider" => "شرکت خدماتی",
        "Sarraf" => "صراف",
        "Employee" => "کارمند",
        "Driver" => "دریور",
        "Partner" => "شریک",
        "Company" => "شرکت",
        _ => partyType
    };

    private async Task<PartyAgingReportViewModel> BuildPartyAgingReportAsync(ManagementReportFilterViewModel filter)
    {
        var balances = await BuildReceivablesPayablesReportAsync(filter);
        var asOfDate = balances.AsOfDate;

        var rows = balances.Rows
            .Where(r => r.BalanceUsd != 0m)
            .Select(r =>
            {
                var daysIdle = r.LastEntryDate is null
                    ? int.MaxValue
                    : Math.Max(0, (int)Math.Round((asOfDate.Date - r.LastEntryDate.Value.Date).TotalDays));

                return new PartyAgingRowViewModel
                {
                    PartyType = r.PartyType,
                    PartyTypeLabel = PartyAgingPartyTypeLabel(r.PartyType),
                    PartyId = r.PartyId,
                    PartyName = r.PartyName,
                    ReceivableUsd = r.BalanceUsd > 0m ? r.BalanceUsd : 0m,
                    PayableUsd = r.BalanceUsd < 0m ? -r.BalanceUsd : 0m,
                    LastEntryDate = r.LastEntryDate,
                    // حسابِ بدون هیچ حرکت، کهنه‌ترین حالت است و روزش نمایش داده نمی‌شود.
                    DaysIdle = daysIdle == int.MaxValue ? 0 : daysIdle,
                    Bucket = BucketOf(daysIdle),
                    DetailsController = r.DetailsController
                };
            })
            // کهنه‌ترین حساب بالای جدول: چیزی که بیشتر از همه معطل مانده اول دیده شود.
            .OrderByDescending(r => r.Bucket)
            .ThenByDescending(r => r.ReceivableUsd + r.PayableUsd)
            .ToList();

        var model = new PartyAgingReportViewModel
        {
            Filter = filter,
            AsOfDate = asOfDate,
            Rows = rows
        };

        return new PartyAgingReportViewModel
        {
            Filter = filter,
            AsOfDate = asOfDate,
            Rows = rows,
            Metrics =
            [
                new() { Label = "طلب تا ۳۰ روز", Value = Money(model.ReceivableIn(PartyAgingBucket.UpTo30)), Detail = "0-30 days", Icon = "bi-arrow-down-circle", ToneClass = "finance-positive" },
                new() { Label = "طلب ۳۱ تا ۶۰ روز", Value = Money(model.ReceivableIn(PartyAgingBucket.From31To60)), Detail = "31-60 days", Icon = "bi-hourglass-split", ToneClass = "finance-positive" },
                new() { Label = "طلب ۶۱ تا ۹۰ روز", Value = Money(model.ReceivableIn(PartyAgingBucket.From61To90)), Detail = "61-90 days", Icon = "bi-exclamation-triangle", ToneClass = "finance-negative" },
                new() { Label = "طلب بیشتر از ۹۰ روز", Value = Money(model.ReceivableIn(PartyAgingBucket.Over90)), Detail = "90+ days", Icon = "bi-exclamation-octagon", ToneClass = "finance-negative" }
            ]
        };
    }

    private static PartyAgingBucket BucketOf(int daysIdle) => daysIdle switch
    {
        <= 30 => PartyAgingBucket.UpTo30,
        <= 60 => PartyAgingBucket.From31To60,
        <= 90 => PartyAgingBucket.From61To90,
        _ => PartyAgingBucket.Over90
    };
}
