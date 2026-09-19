using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Helpers;
using PTGOilSystem.Web.Infrastructure.RateLimiting;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.Reports;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Exports;
using PTGOilSystem.Web.Services.Reporting;

namespace PTGOilSystem.Web.Controllers;

/// <summary>
/// «راپور بارهای در مسیر» — فقط‌خواندنی. ساخت ردیف‌ها و جمع‌ها در <see cref="IGoodsInTransitReader"/>
/// است (مرجع مشترک با داشبورد موبایل)؛ این کنترلر فقط فیلتر، صفحه‌بندی و خروجی را انجام می‌دهد.
/// </summary>
public partial class ReportsController
{
    private const int GoodsInTransitPageSize = 50;

    [EnableRateLimiting(RateLimitPolicies.HeavyReport)]
    public async Task<IActionResult> GoodsInTransit(
        [FromQuery] GoodsInTransitFilterViewModel? filter = null,
        int page = 1,
        [FromQuery(Name = "pageSize")] int? perPage = null,
        CancellationToken cancellationToken = default)
    {
        filter ??= new GoodsInTransitFilterViewModel();
        await PopulateGoodsInTransitLookupsAsync(filter, cancellationToken);

        var pageSize = ListPageSize.Resolve(perPage, GoodsInTransitPageSize);
        ViewData["PageSize"] = pageSize;
        ViewData["DefaultPageSize"] = GoodsInTransitPageSize;

        return View(await BuildGoodsInTransitReportAsync(filter, page, paginate: true, pageSize, cancellationToken));
    }

    [HttpGet]
    [EnableRateLimiting(RateLimitPolicies.CsvExport)]
    public async Task<IActionResult> GoodsInTransitExport(
        string? format,
        [FromQuery] GoodsInTransitFilterViewModel? filter = null,
        CancellationToken cancellationToken = default)
    {
        filter ??= new GoodsInTransitFilterViewModel();
        // paginate: false — خروجی تمام نتایج فیلترشده است، نه فقط صفحهٔ جاری.
        var model = await BuildGoodsInTransitReportAsync(filter, page: 1, paginate: false, GoodsInTransitPageSize, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        return TabularExportSupport.File(this, format, new TabularExportDocument
        {
            FileNameStem = "PTG_Goods_In_Transit",
            TitleFa = "راپور بارهای در مسیر",
            TitleEn = "Goods In Transit Report",
            KnownRowCount = model.Rows.Count,
            ForceLandscape = true,
            Filters = TabularExportSupport.FilterSummary(
                ("از تاریخ / From", filter.FromDate?.ToString("yyyy-MM-dd")),
                ("تا تاریخ / To", filter.ToDate?.ToString("yyyy-MM-dd")),
                ("نوع بار / Kind", filter.Kind.HasValue ? GoodsInTransitKindLabel(filter.Kind.Value) : null),
                ("فقط تأخیردار / Delayed only", filter.DelayedOnly ? "بلی" : null),
                ("جست‌وجو / Search", filter.Search)),
            Columns =
            [
                new("تاریخ حرکت", "Departure", TabularExportValueType.Date, 13),
                new("نوع بار", "Kind", Width: 18),
                new("وسیله", "Vehicle", Width: 18),
                new("راننده / حمل‌کننده", "Driver / carrier", Width: 20),
                new("جنس", "Product", Width: 16),
                new("قرارداد", "Contract", Width: 16),
                new("از", "From", Width: 20),
                new("به", "To", Width: 20),
                new("مقدار در مسیر MT", "In transit MT", TabularExportValueType.Number, 16),
                new("روز در راه", "Days on road", TabularExportValueType.Integer, 12),
                new("رسیدن تخمینی", "Expected arrival", TabularExportValueType.Date, 14),
                new("وضعیت", "Status", Width: 14)
            ],
            Rows = model.Rows.Select(r => new TabularExportRow(
            [
                TabularExportCell.Date(r.DepartureDate),
                TabularExportCell.Text(GoodsInTransitKindLabel(r.Kind)),
                TabularExportCell.Text(r.VehicleLabel),
                TabularExportCell.Text(r.DriverName ?? r.CarrierName ?? "—"),
                TabularExportCell.Text(r.ProductName),
                TabularExportCell.Text(r.ContractNumber ?? "—"),
                TabularExportCell.Text(r.OriginLabel ?? "—"),
                TabularExportCell.Text(r.DestinationLabel ?? "—"),
                TabularExportCell.Number(r.QuantityMt),
                TabularExportCell.Integer(r.DaysOnRoad),
                TabularExportCell.Date(r.ExpectedArrivalDate),
                TabularExportCell.Text(r.IsDelayed ? "تأخیردار" : GoodsInTransitStageLabel(r.Stage))
            ])),
            Totals = new TabularExportRow(
            [
                TabularExportCell.Text("جمع / Total"),
                TabularExportCell.Text(null), TabularExportCell.Text(null), TabularExportCell.Text(null),
                TabularExportCell.Text(null), TabularExportCell.Text(null), TabularExportCell.Text(null),
                TabularExportCell.Text(null),
                TabularExportCell.Number(model.Totals.TotalQuantityMt),
                TabularExportCell.Text(null),
                TabularExportCell.Text(null),
                TabularExportCell.Text($"{model.Totals.RowCount:N0} بار / {model.Totals.DelayedCount:N0} تأخیردار")
            ])
        });
    }

    internal static string GoodsInTransitKindLabel(GoodsInTransitKind kind) => kind switch
    {
        GoodsInTransitKind.FromOrigin => "بار در راه از مبدأ",
        GoodsInTransitKind.InternalTransfer => "حمل داخلی",
        _ => "تحویل با موتر"
    };

    internal static string GoodsInTransitStageLabel(GoodsInTransitStage stage)
        => stage == GoodsInTransitStage.InTransit ? "در مسیر" : "بارگیری‌شده";

    private async Task PopulateGoodsInTransitLookupsAsync(
        GoodsInTransitFilterViewModel filter,
        CancellationToken cancellationToken)
    {
        ViewBag.GoodsInTransitProducts = new SelectList(
            await _db.Products.AsNoTracking().OrderBy(p => p.Name)
                .Select(p => new LookupOption(p.Id, p.Name)).ToListAsync(cancellationToken),
            nameof(LookupOption.Id), nameof(LookupOption.Name), filter.ProductId);
    }

    private async Task<GoodsInTransitReportViewModel> BuildGoodsInTransitReportAsync(
        GoodsInTransitFilterViewModel filter,
        int page,
        bool paginate,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var snapshot = await _goodsInTransit.ReadAsync(filter, cancellationToken);
        var rows = snapshot.Rows;

        var pageCount = Math.Max(1, (int)Math.Ceiling(rows.Count / (double)pageSize));
        var currentPage = Math.Clamp(page, 1, pageCount);
        var pageRows = paginate
            ? rows.Skip((currentPage - 1) * pageSize).Take(pageSize).ToList()
            : rows;

        return new GoodsInTransitReportViewModel
        {
            Filter = filter,
            Rows = pageRows,
            Totals = snapshot.Totals,
            CurrentPage = currentPage,
            PageCount = pageCount
        };
    }
}
