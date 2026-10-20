using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Infrastructure.RateLimiting;
using PTGOilSystem.Web.Models.PartyStatements;
using PTGOilSystem.Web.Models.Reports;
using PTGOilSystem.Web.Security;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Calendars;
using PTGOilSystem.Web.Services.Exports;
using PTGOilSystem.Web.Services.PartyStatements;
using PTGOilSystem.Web.Services.Reporting;

namespace PTGOilSystem.Web.Controllers;

/// <summary>
/// «بیلانس کلی شرکت». کنترلر فقط پارامتر، دسترسی (همان کلید ناوبری «Reports» کل این کنترلر)،
/// فراخوانی سرویس و خروجی وب/PDF را مدیریت می‌کند؛ هیچ فرمول مالی اینجا نیست.
/// </summary>
public partial class ReportsController
{
    [EnableRateLimiting(RateLimitPolicies.HeavyReport)]
    public async Task<IActionResult> CompanyBalance([FromQuery] CompanyBalanceReportFilterViewModel? filter = null)
        => View(await BuildCompanyBalanceAsync(filter ?? new CompanyBalanceReportFilterViewModel()));

    /// <summary>جزئیات یک ردیف بیلانس؛ فقط وب. همان گزارش با همان فیلتر ساخته می‌شود تا جمع‌ها یکی بمانند.</summary>
    [EnableRateLimiting(RateLimitPolicies.HeavyReport)]
    public async Task<IActionResult> CompanyBalanceDetails(
        [FromQuery] CompanyBalanceReportFilterViewModel? filter,
        [FromQuery] CompanyBalanceSection? section)
    {
        var model = await BuildCompanyBalanceDetailsAsync(filter, section);
        return model is null ? NotFound() : View(model);
    }

    /// <summary>
    /// خروجی Excel/PDF جزئیات یک ردیف بیلانس؛ همان <see cref="BuildCompanyBalanceDetailsAsync"/> صفحه
    /// با همان فیلتر، پس ردیف‌ها و جمع با صفحه یکی است.
    /// </summary>
    [HttpGet]
    [EnableRateLimiting(RateLimitPolicies.CsvExport)]
    public async Task<IActionResult> CompanyBalanceDetailsExport(
        string? format,
        [FromQuery] CompanyBalanceReportFilterViewModel? filter,
        [FromQuery] CompanyBalanceSection? section)
    {
        var model = await BuildCompanyBalanceDetailsAsync(filter, section);
        if (model is null)
        {
            return NotFound();
        }

        var report = model.Report;
        var currency = report.CurrencyCode;
        var columns = new List<TabularExportColumn>
        {
            new("شرح", "Description", Width: 30),
            new("جزئیات", "Details", Width: 36)
        };
        if (model.HasQuantity)
        {
            columns.Add(new("مقدار (تن)", "Quantity (MT)", TabularExportValueType.Number, 16));
        }
        columns.Add(new($"مبلغ ({currency})", $"Amount ({currency})", TabularExportValueType.Number, 18));

        TabularExportRow Row(string? name, string? secondary, decimal? quantity, decimal? amount)
        {
            var cells = new List<TabularExportCell> { TabularExportCell.Text(name), TabularExportCell.Text(secondary) };
            if (model.HasQuantity)
            {
                cells.Add(TabularExportCell.Number(quantity));
            }
            cells.Add(TabularExportCell.Number(amount));
            return new TabularExportRow(cells);
        }

        return TabularExportSupport.File(this, format, new TabularExportDocument
        {
            FileNameStem = $"PTG_Company_Balance_{model.Section}",
            TitleFa = $"بیلانس کلی شرکت — {model.TitleFa}",
            TitleEn = $"Company Balance Sheet — {model.TitleEn}",
            KnownRowCount = model.Rows.Count,
            Filters = TabularExportSupport.FilterSummary(
                ("از تاریخ / From", model.IsPeriodSection ? report.ReportFromDate.ToString("yyyy-MM-dd") : null),
                ("تا تاریخ / To", report.ReportToDate.ToString("yyyy-MM-dd")),
                ("ارز / Currency", currency)),
            Columns = columns,
            // ردیف بی‌قیمت مبلغ ندارد (خالی)، نه صفر؛ همان «بدون قیمت خرید» صفحه.
            Rows = model.Rows.Select(r => Row(r.Name, r.Secondary, r.QuantityMt, r.IsValued ? r.Amount : null)),
            Totals = Row("جمع / Total", null, null, model.PrintedTotal)
        });
    }

    private async Task<CompanyBalanceDetailsViewModel?> BuildCompanyBalanceDetailsAsync(
        CompanyBalanceReportFilterViewModel? filter,
        CompanyBalanceSection? section)
    {
        if (section is null || !Enum.IsDefined(section.Value))
        {
            return null;
        }

        var report = await BuildCompanyBalanceAsync(filter ?? new CompanyBalanceReportFilterViewModel());
        var line = report.Line(section.Value);
        var (titleFa, titleEn) = section.Value switch
        {
            CompanyBalanceSection.PeriodExpenses => (CompanyBalanceReportText.PeriodExpenses, "Period expenses"),
            CompanyBalanceSection.SalesRevenue => (CompanyBalanceReportText.Sales, "Sales"),
            _ => (line?.LabelFa ?? "", line?.LabelEn ?? "")
        };

        return new CompanyBalanceDetailsViewModel
        {
            Report = report,
            Section = section.Value,
            TitleFa = titleFa,
            TitleEn = titleEn,
            Rows = report.DetailsFor(section.Value)
        };
    }

    [EnableRateLimiting(RateLimitPolicies.HeavyReport)]
    public async Task<IActionResult> CompanyBalancePdf([FromQuery] CompanyBalanceReportFilterViewModel? filter = null)
    {
        var exportService = HttpContext?.RequestServices?.GetService<ITabularExportService>();
        if (exportService is null)
        {
            return NotFound();
        }

        var model = await BuildCompanyBalanceAsync(filter ?? new CompanyBalanceReportFilterViewModel());
        using var output = new MemoryStream();
        await exportService.WriteCompanyBalancePdfAsync(model, isEnglish: false, output, HttpContext!.RequestAborted);
        return File(
            output.ToArray(),
            "application/pdf",
            $"MASHAL_Company_Balance_{model.ReportToDate:yyyy-MM-dd}_{model.CurrencyCode}.pdf");
    }

    private async Task<CompanyBalanceReportViewModel> BuildCompanyBalanceAsync(CompanyBalanceReportFilterViewModel filter)
    {
        var ct = HttpContext?.RequestAborted ?? CancellationToken.None;
        // کلید تاریخ تجاری با Kind=Utc، همان قرارداد AfghanistanBusinessClock.Today برای ستون‌های timestamptz.
        var toDate = DateTime.SpecifyKind((filter.ToDate ?? _businessClock.Today).Date, DateTimeKind.Utc);
        var fromDate = DateTime.SpecifyKind((filter.FromDate ?? PeriodStart(toDate)).Date, DateTimeKind.Utc);
        if (fromDate > toDate)
        {
            (fromDate, toDate) = (toDate, fromDate);
        }

        var currencies = await LoadCompanyBalanceCurrenciesAsync(ct);
        var requested = SystemCurrency.Normalize(filter.Currency);
        var currency = currencies.Any(c => string.Equals(c.Code, requested, StringComparison.OrdinalIgnoreCase))
            ? requested
            : SystemCurrency.BaseCurrencyCode;

        // ماندهٔ طرف‌حساب‌ها دقیقاً همان ردیف‌های «طلبات و بدهی‌ها» تا «تا تاریخ» است
        // (با همان تعدیل تفاوت نرخ تأمین‌کننده و همان قاعدهٔ دسترسی معاش). ردیف شریک اینجا لازم نیست:
        // سرویس ماندهٔ واقعیِ شریک را خودش از PartnerCompanyBalanceReader می‌خواند.
        var partyRows = (await BuildReceivablesPayablesReportAsync(
            new ManagementReportFilterViewModel { ToDate = toDate }, CompanyOverviewPartyTypes)).Rows;
        var employeeExcluded = User is not null && !RoleAccessRules.CanViewEmployeeSalary(User);

        var service = HttpContext?.RequestServices?.GetService<ICompanyBalanceReportService>()
            ?? new CompanyBalanceReportService(
                _db,
                _profitAndLoss,
                _stock,
                HttpContext?.RequestServices?.GetService<IPricingService>() ?? new PricingService(_db),
                HttpContext?.RequestServices?.GetService<IPartnershipStatementService>());

        CompanyBalanceReportViewModel model;
        string? currencyError = null;
        try
        {
            model = await service.BuildAsync(
                new CompanyBalanceReportRequest(fromDate, toDate, currency, _businessClock.Now.DateTime, employeeExcluded),
                partyRows,
                ct);
        }
        catch (CompanyBalanceCurrencyException ex)
        {
            currencyError = ex.Message;
            model = await service.BuildAsync(
                new CompanyBalanceReportRequest(fromDate, toDate, SystemCurrency.BaseCurrencyCode, _businessClock.Now.DateTime, employeeExcluded),
                partyRows,
                ct);
        }

        model.Filter = new CompanyBalanceReportFilterViewModel
        {
            FromDate = fromDate,
            ToDate = toDate,
            Currency = model.CurrencyCode
        };
        model.Currencies = currencies;
        model.CurrencyErrorFa = currencyError;
        return model;
    }

    /// <summary>پیش‌فرض «از تاریخ»: اول ماهِ «تا تاریخ» در تقویم فعال سیستم.</summary>
    private static DateTime PeriodStart(DateTime toDate)
    {
        if (AppCalendarContext.IsSolarHijri)
        {
            var solar = AfghanSolarCalendar.FromGregorian(toDate);
            return AfghanSolarCalendar.ToGregorian(solar.Year, solar.Month, 1);
        }

        return new DateTime(toDate.Year, toDate.Month, 1, 0, 0, 0, toDate.Kind);
    }

    /// <summary>ارزهای فعال سیستم؛ ارز پایه همیشه اول و همیشه موجود.</summary>
    private async Task<IReadOnlyList<CompanyBalanceCurrencyOption>> LoadCompanyBalanceCurrenciesAsync(CancellationToken ct)
    {
        var rows = await _db.Currencies.AsNoTracking()
            .Where(c => c.IsActive)
            .Select(c => new { c.Code, c.Name, c.NamePersian })
            .ToListAsync(ct);

        var options = rows
            .Select(c => new CompanyBalanceCurrencyOption(
                SystemCurrency.Normalize(c.Code),
                string.IsNullOrWhiteSpace(c.NamePersian) ? c.Name : c.NamePersian!))
            .Where(c => !string.IsNullOrWhiteSpace(c.Code))
            .DistinctBy(c => c.Code, StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => SystemCurrency.IsBaseCurrency(c.Code) ? 0 : 1)
            .ThenBy(c => c.Code, StringComparer.Ordinal)
            .ToList();
        if (!options.Any(c => SystemCurrency.IsBaseCurrency(c.Code)))
        {
            options.Insert(0, new CompanyBalanceCurrencyOption(SystemCurrency.BaseCurrencyCode, SystemCurrency.BaseCurrencyCode));
        }

        return options;
    }
}
