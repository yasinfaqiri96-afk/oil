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

namespace PTGOilSystem.Web.Controllers;

/// <summary>
/// «راپور کسری و اضافه‌بار حمل» — فقط‌خواندنی.
///
/// منبع واحدِ داده، <see cref="LossEvent"/>های مرحلهٔ حمل است؛ هر دو مسیرِ تخلیه در آن می‌ریزند:
///   • تخلیهٔ موتر  ⇒ <see cref="LossEventStage.DispatchShortage"/>
///   • رسید انتقال داخلی ⇒ <see cref="LossEventStage.ReceiptShortage"/>
///
/// قرارداد علامت همان <see cref="TransportVarianceMath"/> است:
/// <c>DifferenceQuantityMt = بارگیری − تخلیه</c> ⇒ مثبت = کسری، منفی = اضافه‌بار.
/// کسری و اضافه‌بار جداگانه جمع می‌شوند و هرگز یکدیگر را خنثی نمی‌کنند؛ «تفاوت خالص» فقط
/// یک عددِ نمایشیِ جداست. اضافه‌بار هیچ سهمی در «کسری قابل جریمه» ندارد.
/// این گزارش هیچ رکوردی نمی‌سازد و هیچ مقداری را دوباره محاسبه نمی‌کند.
/// </summary>
public partial class ReportsController
{
    private const int TransportVariancePageSize = 50;

    private static readonly LossEventStage[] TransportVarianceStages =
    [
        LossEventStage.DispatchShortage,
        LossEventStage.ReceiptShortage
    ];

    [EnableRateLimiting(RateLimitPolicies.HeavyReport)]
    public async Task<IActionResult> TransportVariance(
        [FromQuery] TransportVarianceReportFilterViewModel? filter = null,
        int page = 1,
        [FromQuery(Name = "pageSize")] int? perPage = null,
        CancellationToken cancellationToken = default)
    {
        filter ??= new TransportVarianceReportFilterViewModel();
        await PopulateTransportVarianceLookupsAsync(filter, cancellationToken);

        var pageSize = ListPageSize.Resolve(perPage, TransportVariancePageSize);
        ViewData["PageSize"] = pageSize;
        ViewData["DefaultPageSize"] = TransportVariancePageSize;

        return View(await BuildTransportVarianceReportAsync(filter, page, paginate: true, cancellationToken, pageSize));
    }

    [HttpGet]
    [EnableRateLimiting(RateLimitPolicies.CsvExport)]
    public async Task<IActionResult> TransportVarianceExport(
        string? format,
        [FromQuery] TransportVarianceReportFilterViewModel? filter = null,
        CancellationToken cancellationToken = default)
    {
        filter ??= new TransportVarianceReportFilterViewModel();
        // paginate: false — خروجی تمام نتایج فیلترشده است، نه فقط صفحهٔ جاری.
        var model = await BuildTransportVarianceReportAsync(filter, page: 1, paginate: false, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        return TabularExportSupport.File(this, format, new TabularExportDocument
        {
            FileNameStem = "PTG_Transport_Variance",
            TitleFa = "راپور کسری و اضافه‌بار حمل",
            TitleEn = "Transport Shortage & Surplus Report",
            KnownRowCount = model.Rows.Count,
            ForceLandscape = true,
            Filters = TabularExportSupport.FilterSummary(
                ("از تاریخ / From", filter.FromDate?.ToString("yyyy-MM-dd")),
                ("تا تاریخ / To", filter.ToDate?.ToString("yyyy-MM-dd")),
                ("نوع تفاوت / Kind", TransportVarianceKindFilterLabel(filter.Kind)),
                ("مسیر ثبت / Source", filter.Source.HasValue ? TransportVarianceSourceLabel(filter.Source.Value) : null),
                ("جست‌وجو / Search", filter.Search)),
            Columns =
            [
                new("تاریخ", "Date", TabularExportValueType.Date, 13),
                new("مسیر ثبت", "Source", Width: 14),
                new("موتر / وسیله", "Vehicle", Width: 16),
                new("راننده", "Driver", Width: 16),
                new("جنس", "Product", Width: 16),
                new("قرارداد", "Contract", Width: 16),
                new("مسیر", "Route", Width: 24, Wrap: true),
                new("بارگیری MT", "Loaded MT", TabularExportValueType.Number, 14),
                new("تخلیه MT", "Unloaded MT", TabularExportValueType.Number, 14),
                new("تفاوت MT", "Difference MT", TabularExportValueType.Number, 14),
                new("نوع تفاوت", "Variance type", Width: 13),
                new("فیصدی تفاوت", "Difference %", TabularExportValueType.Number, 13),
                new("کسری قابل جریمه MT", "Chargeable MT", TabularExportValueType.Number, 17),
                new("اضافه‌بار فروخته‌شده MT", "Surplus sold MT", TabularExportValueType.Number, 18),
                new("عاید اضافه‌بار USD", "Surplus revenue USD", TabularExportValueType.Number, 18),
                new("مصارف اضافه‌بار USD", "Surplus expense USD", TabularExportValueType.Number, 18),
                new("منفعت اضافه‌بار USD", "Surplus profit USD", TabularExportValueType.Number, 18)
            ],
            Rows = model.Rows.Select(r => new TabularExportRow(
            [
                TabularExportCell.Date(r.EventDate),
                TabularExportCell.Text(TransportVarianceSourceLabel(r.Source)),
                TabularExportCell.Text(r.VehicleNumber ?? "—"),
                TabularExportCell.Text(r.DriverName ?? "—"),
                TabularExportCell.Text(r.ProductName),
                TabularExportCell.Text(r.ContractNumber ?? "—"),
                TabularExportCell.Text(r.RouteText ?? "—"),
                TabularExportCell.Number(r.LoadedQuantityMt),
                TabularExportCell.Number(r.UnloadedQuantityMt),
                TabularExportCell.Number(r.DifferenceQuantityMt),
                TabularExportCell.Text(TransportVarianceKindLabel(r.Kind)),
                TabularExportCell.Number(r.DifferencePercent),
                TabularExportCell.Number(r.ChargeableShortageMt),
                TabularExportCell.Number(r.SurplusSoldMt),
                TabularExportCell.Number(r.SurplusRevenueUsd),
                TabularExportCell.Number(r.SurplusAllocatableExpenseUsd),
                TabularExportCell.Number(r.SurplusNetProfitUsd)
            ])),
            // ردیف جمع — دقیقاً همان اعداد کارت‌های بالای راپور (کسری و اضافه‌بار جدا).
            Totals = new TabularExportRow(
            [
                TabularExportCell.Text("جمع / Total"),
                TabularExportCell.Text(null), TabularExportCell.Text(null), TabularExportCell.Text(null),
                TabularExportCell.Text(null), TabularExportCell.Text(null), TabularExportCell.Text(null),
                TabularExportCell.Number(model.Totals.TotalLoadedMt),
                TabularExportCell.Number(model.Totals.TotalUnloadedMt),
                TabularExportCell.Number(model.Totals.NetDifferenceMt),
                TabularExportCell.Text($"کسری {model.Totals.TotalShortageMt:N4} / اضافه‌بار {model.Totals.TotalSurplusMt:N4}"),
                TabularExportCell.Text(null),
                TabularExportCell.Number(model.Totals.TotalChargeableShortageMt),
                TabularExportCell.Number(model.Totals.TotalSurplusSoldMt),
                TabularExportCell.Number(model.Totals.TotalSurplusRevenueUsd),
                TabularExportCell.Number(model.Totals.TotalSurplusExpenseUsd),
                TabularExportCell.Number(model.Totals.TotalSurplusNetProfitUsd)
            ])
        });
    }

    internal static string TransportVarianceKindLabel(TransportVarianceKind kind)
        => kind == TransportVarianceKind.Surplus ? "اضافه‌بار" : "کسری";

    internal static string TransportVarianceSourceLabel(TransportVarianceSource source)
        => source == TransportVarianceSource.InventoryTransfer ? "انتقال داخلی" : "تخلیه موتر";

    private static string TransportVarianceKindFilterLabel(TransportVarianceFilterKind kind) => kind switch
    {
        TransportVarianceFilterKind.ShortageOnly => "فقط کسری",
        TransportVarianceFilterKind.SurplusOnly => "فقط اضافه‌بار",
        _ => "همه"
    };

    private async Task<TransportVarianceReportViewModel> BuildTransportVarianceReportAsync(
        TransportVarianceReportFilterViewModel filter,
        int page,
        bool paginate,
        CancellationToken cancellationToken,
        int pageSize = TransportVariancePageSize)
    {
        var query = _db.LossEvents
            .AsNoTracking()
            .Where(e => !e.IsCancelled && TransportVarianceStages.Contains(e.Stage));

        if (filter.FromDate.HasValue)
        {
            query = query.Where(e => e.EventDate >= filter.FromDate.Value.Date);
        }

        if (filter.ToDate.HasValue)
        {
            query = query.Where(e => e.EventDate <= filter.ToDate.Value.Date);
        }

        if (filter.ProductId.HasValue)
        {
            query = query.Where(e => e.ProductId == filter.ProductId.Value);
        }

        if (filter.ContractId.HasValue)
        {
            query = query.Where(e => e.ContractId == filter.ContractId.Value);
        }

        if (filter.Source == TransportVarianceSource.TruckDispatch)
        {
            query = query.Where(e => e.TruckDispatchId != null);
        }
        else if (filter.Source == TransportVarianceSource.InventoryTransfer)
        {
            query = query.Where(e => e.TransportLegId != null);
        }

        // «فقط کسری» و «فقط اضافه‌بار» روی علامتِ تفاوت کار می‌کنند، نه روی مقدار قابل جریمه،
        // تا هیچ ردیفی — حتی کسریِ کاملاً داخل تلورانس — از راپور بیرون نیفتد.
        if (filter.Kind == TransportVarianceFilterKind.ShortageOnly)
        {
            query = query.Where(e => e.DifferenceQuantityMt > 0m);
        }
        else if (filter.Kind == TransportVarianceFilterKind.SurplusOnly)
        {
            query = query.Where(e => e.DifferenceQuantityMt < 0m);
        }
        else
        {
            query = query.Where(e => e.DifferenceQuantityMt != 0m);
        }

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var search = filter.Search.Trim();
            query = query.Where(e =>
                (e.ResponsiblePartyName != null && e.ResponsiblePartyName.Contains(search))
                || (e.TruckDispatch != null && e.TruckDispatch.Truck != null && e.TruckDispatch.Truck.PlateNumber.Contains(search))
                || (e.TruckDispatch != null && e.TruckDispatch.Driver != null && e.TruckDispatch.Driver.FullName.Contains(search))
                || (e.TransportLeg != null && e.TransportLeg.Truck != null && e.TransportLeg.Truck.PlateNumber.Contains(search))
                || (e.TransportLeg != null && e.TransportLeg.WagonNumber != null && e.TransportLeg.WagonNumber.Contains(search))
                || (e.TransportLeg != null && e.TransportLeg.Driver != null && e.TransportLeg.Driver.FullName.Contains(search)));
        }

        // جمع‌ها روی کل نتایج فیلترشده محاسبه می‌شوند (نه فقط صفحهٔ جاری) و کسری/اضافه‌بار
        // در دو Sum جدا می‌مانند تا هم‌دیگر را خنثی نکنند.
        var aggregate = await query
            .GroupBy(_ => 1)
            .Select(g => new
            {
                RowCount = g.Count(),
                ShortageCount = g.Count(e => e.DifferenceQuantityMt > 0m),
                SurplusCount = g.Count(e => e.DifferenceQuantityMt < 0m),
                ShortageMt = g.Sum(e => e.DifferenceQuantityMt > 0m ? e.DifferenceQuantityMt : 0m),
                SurplusMt = g.Sum(e => e.DifferenceQuantityMt < 0m ? -e.DifferenceQuantityMt : 0m),
                ChargeableMt = g.Sum(e => e.DifferenceQuantityMt > 0m ? e.ChargeableLossMt : 0m),
                LoadedMt = g.Sum(e => e.ExpectedQuantityMt),
                UnloadedMt = g.Sum(e => e.ActualQuantityMt)
            })
            .FirstOrDefaultAsync(cancellationToken);

        // سود و زیانِ اضافه‌بار روی کلِ ردیف‌های اضافه‌بارِ فیلترشده (نه فقط صفحهٔ جاری)
        // تا جمعِ کارت‌ها با جمعِ ستون‌ها بخواند.
        var surplusPnl = await BuildSurplusPnlAsync(query, cancellationToken);

        var totals = new TransportVarianceTotalsViewModel
        {
            RowCount = aggregate?.RowCount ?? 0,
            ShortageCount = aggregate?.ShortageCount ?? 0,
            SurplusCount = aggregate?.SurplusCount ?? 0,
            TotalShortageMt = aggregate?.ShortageMt ?? 0m,
            TotalSurplusMt = aggregate?.SurplusMt ?? 0m,
            TotalChargeableShortageMt = aggregate?.ChargeableMt ?? 0m,
            TotalLoadedMt = aggregate?.LoadedMt ?? 0m,
            TotalUnloadedMt = aggregate?.UnloadedMt ?? 0m,
            TotalSurplusSoldMt = surplusPnl.Values.Sum(x => x.SoldMt),
            TotalSurplusRevenueUsd = surplusPnl.Values.Sum(x => x.RevenueUsd),
            TotalSurplusExpenseUsd = surplusPnl.Values.Sum(x => x.ExpenseUsd)
        };

        var pageCount = Math.Max(1, (int)Math.Ceiling((totals.RowCount) / (double)pageSize));
        page = Math.Clamp(page, 1, pageCount);

        var ordered = query
            .OrderByDescending(e => e.EventDate)
            .ThenByDescending(e => e.Id);

        var source = paginate
            ? ordered.Skip((page - 1) * pageSize).Take(pageSize)
            : ordered;

        var rows = await source
            .Select(e => new
            {
                e.Id,
                e.EventDate,
                e.ExpectedQuantityMt,
                e.ActualQuantityMt,
                e.DifferenceQuantityMt,
                e.ToleranceQuantityMt,
                e.ChargeableLossMt,
                e.ResponsiblePartyName,
                e.Notes,
                e.TruckDispatchId,
                e.TransportLegId,
                ProductName = e.Product != null ? e.Product.Name : "",
                ContractNumber = e.Contract != null ? e.Contract.ContractNumber : null,
                DispatchPlate = e.TruckDispatch != null && e.TruckDispatch.Truck != null ? e.TruckDispatch.Truck.PlateNumber : null,
                DispatchDriver = e.TruckDispatch != null && e.TruckDispatch.Driver != null ? e.TruckDispatch.Driver.FullName : null,
                DispatchDestination = e.TruckDispatch != null && e.TruckDispatch.DestinationLocation != null ? e.TruckDispatch.DestinationLocation.Name : null,
                LegPlate = e.TransportLeg != null && e.TransportLeg.Truck != null ? e.TransportLeg.Truck.PlateNumber : null,
                LegWagon = e.TransportLeg != null ? e.TransportLeg.WagonNumber : null,
                LegDriver = e.TransportLeg != null && e.TransportLeg.Driver != null ? e.TransportLeg.Driver.FullName : null,
                LegSource = e.TransportLeg != null && e.TransportLeg.SourceTerminal != null ? e.TransportLeg.SourceTerminal.Name : null,
                LegDestinationTerminal = e.TransportLeg != null && e.TransportLeg.DestinationTerminal != null ? e.TransportLeg.DestinationTerminal.Name : null,
                LegDestinationLocation = e.TransportLeg != null && e.TransportLeg.DestinationLocation != null ? e.TransportLeg.DestinationLocation.Name : null,
                LegRoute = e.TransportLeg != null ? e.TransportLeg.RouteDescription : null,
                TerminalName = e.Terminal != null ? e.Terminal.Name : null
            })
            .ToListAsync(cancellationToken);

        var items = rows.Select(r =>
        {
            var isTruck = r.TruckDispatchId.HasValue;
            surplusPnl.TryGetValue(r.Id, out var pnl);
            var route = isTruck
                ? JoinRoute(r.TerminalName, r.DispatchDestination)
                : !string.IsNullOrWhiteSpace(r.LegRoute)
                    ? r.LegRoute
                    : JoinRoute(r.LegSource, r.LegDestinationTerminal ?? r.LegDestinationLocation ?? r.TerminalName);

            return new TransportVarianceRowViewModel
            {
                LossEventId = r.Id,
                EventDate = r.EventDate,
                Source = isTruck ? TransportVarianceSource.TruckDispatch : TransportVarianceSource.InventoryTransfer,
                Kind = TransportVarianceMath.IsSurplus(r.DifferenceQuantityMt)
                    ? TransportVarianceKind.Surplus
                    : TransportVarianceKind.Shortage,
                ProductName = r.ProductName,
                ContractNumber = r.ContractNumber,
                VehicleNumber = isTruck ? r.DispatchPlate : r.LegPlate ?? r.LegWagon,
                DriverName = isTruck ? r.DispatchDriver ?? r.ResponsiblePartyName : r.LegDriver ?? r.ResponsiblePartyName,
                RouteText = route,
                LoadedQuantityMt = r.ExpectedQuantityMt,
                UnloadedQuantityMt = r.ActualQuantityMt,
                DifferenceQuantityMt = r.DifferenceQuantityMt,
                DifferencePercent = TransportVarianceMath.DifferencePercent(r.DifferenceQuantityMt, r.ExpectedQuantityMt),
                // اضافه‌بار هرگز قابل جریمه نیست، حتی اگر رکورد قدیمی مقدار داشته باشد.
                ChargeableShortageMt = TransportVarianceMath.IsSurplus(r.DifferenceQuantityMt) ? 0m : r.ChargeableLossMt,
                ToleranceQuantityMt = r.ToleranceQuantityMt,
                TruckDispatchId = r.TruckDispatchId,
                TransportLegId = r.TransportLegId,
                Notes = r.Notes,
                SurplusRecordedMt = pnl?.RecordedMt ?? 0m,
                SurplusSoldMt = pnl?.SoldMt ?? 0m,
                SurplusRevenueUsd = pnl?.RevenueUsd ?? 0m,
                SurplusAllocatableExpenseUsd = pnl?.ExpenseUsd ?? 0m,
                SurplusNetProfitUsd = pnl?.NetProfitUsd ?? 0m,
                SurplusSaleId = pnl?.SaleId,
                SurplusSaleInvoiceNumber = pnl?.InvoiceNumber
            };
        }).ToList();

        return new TransportVarianceReportViewModel
        {
            Filter = filter,
            Rows = items,
            Totals = totals,
            CurrentPage = page,
            PageCount = pageCount
        };
    }

    private sealed record SurplusPnlLine(
        decimal RecordedMt,
        decimal SoldMt,
        decimal RevenueUsd,
        decimal ExpenseUsd,
        int? SaleId,
        string? InvoiceNumber)
    {
        public decimal NetProfitUsd => SurplusPnlMath.NetProfit(RevenueUsd, ExpenseUsd);
    }

    private sealed record SurplusSaleLink(
        int OwnerId,
        int SaleId,
        string InvoiceNumber,
        decimal QuantityMt,
        decimal UnitPriceUsd);

    /// <summary>
    /// سود و زیانِ اضافه‌بار — کاملاً مشتق‌شده و فقط‌خواندنی. هیچ رکوردی ساخته یا ذخیره نمی‌شود،
    /// پس عاید/سود دوباره حساب نمی‌شود؛ اعداد از همان فروشِ فعال و همان مصارفِ ثبت‌شده می‌آیند.
    ///
    /// • اضافه‌بارِ فروخته‌شده: آن بخش از مقدار فروشِ فعال که از وزن بارگیری فراتر رفته است.
    ///   فروشِ لغوشده یا نبودِ فروش ⇒ صفر (اضافه‌بار فقط در موجودی می‌ماند).
    ///   ویرایش مقدار فروش و فروش قسمتی هم خودکار پوشش داده می‌شود، چون از مقدار فعلی خوانده می‌شود.
    /// • قیمت خرید: هیچ. شرکت برای این تن‌ها پولی نداده، پس قیمت خریدِ ساختگی تولید نمی‌شود و
    ///   مجموع قیمت تمام‌شده از هزینهٔ واقعیِ خرید فراتر نمی‌رود.
    /// • مصارف قابل تخصیص: همان ExpenseTransactionهای همین وسیله. کرایهٔ دیسپچ و کرایهٔ رسید هر دو
    ///   از قبل به‌صورت ExpenseTransaction آینه می‌شوند، پس جمع‌کردنِ جداگانهٔ کرایه دوباره‌شماری بود.
    /// </summary>
    private async Task<Dictionary<int, SurplusPnlLine>> BuildSurplusPnlAsync(
        IQueryable<LossEvent> filtered,
        CancellationToken cancellationToken)
    {
        var surplusRows = await filtered
            .Where(e => e.DifferenceQuantityMt < 0m)
            .Select(e => new
            {
                e.Id,
                e.ExpectedQuantityMt,
                e.ActualQuantityMt,
                e.DifferenceQuantityMt,
                e.TruckDispatchId,
                e.TransportLegId,
                e.Reference
            })
            .ToListAsync(cancellationToken);

        var result = new Dictionary<int, SurplusPnlLine>();
        if (surplusRows.Count == 0)
        {
            return result;
        }

        var dispatchIds = surplusRows
            .Where(r => r.TruckDispatchId.HasValue)
            .Select(r => r.TruckDispatchId!.Value)
            .Distinct()
            .ToList();
        var legIds = surplusRows
            .Where(r => !r.TruckDispatchId.HasValue && r.TransportLegId.HasValue)
            .Select(r => r.TransportLegId!.Value)
            .Distinct()
            .ToList();

        // فروشِ یک موتر می‌تواند چند ردیف باشد (فروش قسمتی + فروش باقی‌مانده). هر دو مسیرِ لینک
        // خوانده و روی شناسهٔ فروش dedupe می‌شود، سپس مقدارها جمع و قیمت واحد به‌صورت میانگین وزنی
        // محاسبه می‌گردد تا اضافه‌بارِ فروخته‌شده و عایدش دو بار یا ناقص حساب نشود.
        var dispatchSaleRows = dispatchIds.Count == 0
            ? []
            : await (
                from s in _db.SalesTransactions.AsNoTracking()
                where !s.IsCancelled && s.TruckDispatchId != null && dispatchIds.Contains(s.TruckDispatchId.Value)
                select new { DispatchId = s.TruckDispatchId!.Value, SaleId = s.Id, s.InvoiceNumber, s.QuantityMt, s.TotalUsd })
                .ToListAsync(cancellationToken);

        var legacyDispatchSaleRows = dispatchIds.Count == 0
            ? []
            : await _db.TruckDispatches.AsNoTracking()
                .Where(d => dispatchIds.Contains(d.Id)
                    && d.SalesTransactionId != null
                    && d.SalesTransaction != null
                    && !d.SalesTransaction.IsCancelled)
                .Select(d => new
                {
                    DispatchId = d.Id,
                    SaleId = d.SalesTransactionId!.Value,
                    d.SalesTransaction!.InvoiceNumber,
                    d.SalesTransaction.QuantityMt,
                    d.SalesTransaction.TotalUsd
                })
                .ToListAsync(cancellationToken);

        var saleByDispatch = dispatchSaleRows
            .Concat(legacyDispatchSaleRows)
            .GroupBy(x => x.SaleId)
            .Select(g => g.First())
            .GroupBy(x => x.DispatchId)
            .ToDictionary(
                g => g.Key,
                g =>
                {
                    var quantityMt = g.Sum(x => x.QuantityMt);
                    var totalUsd = g.Sum(x => x.TotalUsd);
                    var first = g.OrderBy(x => x.SaleId).First();
                    return new SurplusSaleLink(
                        g.Key,
                        first.SaleId,
                        first.InvoiceNumber,
                        quantityMt,
                        quantityMt > 0m
                            ? decimal.Round(totalUsd / quantityMt, 4, MidpointRounding.AwayFromZero)
                            : 0m);
                });

        // رکوردِ تفاوتِ انتقال داخلی با Reference به رسیدِ خودش وصل است، پس فروشِ همان رسید
        // انتخاب می‌شود و رسیدهای دیگرِ همان حمل اشتباهاً به این ردیف نسبت داده نمی‌شوند.
        var saleByReceipt = legIds.Count == 0
            ? new Dictionary<int, SurplusSaleLink>()
            : (await _db.InventoryTransportReceipts.AsNoTracking()
                .Where(r => legIds.Contains(r.InventoryTransportLegId)
                    && !r.IsCancelled
                    && r.SalesTransactionId != null
                    && r.SalesTransaction != null
                    && !r.SalesTransaction.IsCancelled)
                .Select(r => new SurplusSaleLink(
                    r.Id,
                    r.SalesTransactionId!.Value,
                    r.SalesTransaction!.InvoiceNumber,
                    r.SalesTransaction.QuantityMt,
                    r.SalesTransaction.UnitPriceUsd))
                .ToListAsync(cancellationToken))
                .ToDictionary(x => x.OwnerId);

        var dispatchExpense = dispatchIds.Count == 0
            ? new Dictionary<int, decimal>()
            : (await _db.ExpenseTransactions.AsNoTracking()
                .Where(x => !x.IsCancelled
                    && x.TruckDispatchId != null
                    && dispatchIds.Contains(x.TruckDispatchId.Value))
                .GroupBy(x => x.TruckDispatchId!.Value)
                .Select(g => new { OwnerId = g.Key, AmountUsd = g.Sum(x => x.AmountUsd) })
                .ToListAsync(cancellationToken))
                .ToDictionary(x => x.OwnerId, x => x.AmountUsd);

        var legExpense = legIds.Count == 0
            ? new Dictionary<int, decimal>()
            : (await _db.ExpenseTransactions.AsNoTracking()
                .Where(x => !x.IsCancelled
                    && x.TransportLegId != null
                    && legIds.Contains(x.TransportLegId.Value))
                .GroupBy(x => x.TransportLegId!.Value)
                .Select(g => new { OwnerId = g.Key, AmountUsd = g.Sum(x => x.AmountUsd) })
                .ToListAsync(cancellationToken))
                .ToDictionary(x => x.OwnerId, x => x.AmountUsd);

        // مصارفِ یک حمل روی کلِ تنِ همان حمل پخش است، نه روی یک رسید؛ پس مخرجِ نرخِ هر تن
        // مقدارِ خودِ حمل است تا با چند رسید، مصرف چند بار تخصیص نشود.
        var legQuantity = legIds.Count == 0
            ? new Dictionary<int, decimal>()
            : await _db.InventoryTransportLegs.AsNoTracking()
                .Where(l => legIds.Contains(l.Id))
                .ToDictionaryAsync(l => l.Id, l => l.QuantityMt, cancellationToken);

        foreach (var row in surplusRows)
        {
            var recordedMt = TransportVarianceMath.Surplus(row.DifferenceQuantityMt);
            SurplusSaleLink? sale = null;
            var expenseUsd = 0m;
            var carriedMt = row.ActualQuantityMt;

            if (row.TruckDispatchId.HasValue)
            {
                saleByDispatch.TryGetValue(row.TruckDispatchId.Value, out sale);
                dispatchExpense.TryGetValue(row.TruckDispatchId.Value, out expenseUsd);
            }
            else if (row.TransportLegId.HasValue)
            {
                var receiptId = ParseTransportReceiptReference(row.Reference);
                if (receiptId.HasValue)
                {
                    saleByReceipt.TryGetValue(receiptId.Value, out sale);
                }

                legExpense.TryGetValue(row.TransportLegId.Value, out expenseUsd);
                if (legQuantity.TryGetValue(row.TransportLegId.Value, out var legQty) && legQty > 0m)
                {
                    carriedMt = legQty;
                }
            }

            var soldMt = sale is null
                ? 0m
                : SurplusPnlMath.SoldSurplus(recordedMt, row.ExpectedQuantityMt, sale.QuantityMt);
            var revenueUsd = sale is null ? 0m : SurplusPnlMath.Revenue(soldMt, sale.UnitPriceUsd);
            var allocatedExpenseUsd = SurplusPnlMath.AllocatableExpense(soldMt, carriedMt, expenseUsd);

            result[row.Id] = new SurplusPnlLine(
                recordedMt,
                soldMt,
                revenueUsd,
                allocatedExpenseUsd,
                soldMt > 0m ? sale?.SaleId : null,
                soldMt > 0m ? sale?.InvoiceNumber : null);
        }

        return result;
    }

    private const string TransportReceiptReferencePrefix = "TRANSPORT-RECEIPT:";

    private static int? ParseTransportReceiptReference(string? reference)
        => !string.IsNullOrWhiteSpace(reference)
            && reference.StartsWith(TransportReceiptReferencePrefix, StringComparison.Ordinal)
            && int.TryParse(reference[TransportReceiptReferencePrefix.Length..], out var id)
                ? id
                : null;

    private static string? JoinRoute(string? from, string? to)
    {
        var start = string.IsNullOrWhiteSpace(from) ? null : from.Trim();
        var end = string.IsNullOrWhiteSpace(to) ? null : to.Trim();
        if (start is null && end is null) return null;
        if (start is null) return end;
        if (end is null) return start;
        return $"{start} ← {end}";
    }

    private async Task PopulateTransportVarianceLookupsAsync(
        TransportVarianceReportFilterViewModel filter,
        CancellationToken cancellationToken)
    {
        ViewBag.TransportVarianceProducts = new SelectList(
            await _db.Products.AsNoTracking().OrderBy(p => p.Name)
                .Select(p => new LookupOption(p.Id, p.Name)).ToListAsync(cancellationToken),
            nameof(LookupOption.Id), nameof(LookupOption.Name), filter.ProductId);

        ViewBag.TransportVarianceContracts = new SelectList(
            await _db.Contracts.AsNoTracking().OrderByDescending(c => c.ContractDate)
                .Select(c => new LookupOption(c.Id, c.ContractNumber)).ToListAsync(cancellationToken),
            nameof(LookupOption.Id), nameof(LookupOption.Name), filter.ContractId);
    }
}
