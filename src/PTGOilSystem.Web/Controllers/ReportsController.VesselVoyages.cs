using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Helpers;
using PTGOilSystem.Web.Infrastructure.RateLimiting;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.Reports;
using PTGOilSystem.Web.Models.ShipmentPnl;
using PTGOilSystem.Web.Services.Exports;

namespace PTGOilSystem.Web.Controllers;

/// <summary>
/// «گزارش کشتی‌ها» — فقط‌خواندنی. یک سطر = یک <see cref="Shipment"/> (یک سفر/محموله)،
/// نه یک کشتی؛ <see cref="Vessel"/> جدول جداست و یک کشتی می‌تواند چند سفر داشته باشد.
///
/// هیچ جدول، ستون یا منطق موازی ساخته نمی‌شود:
///  • محصول / تأمین‌کننده / تخصیص ⇐ <c>ShipmentContracts → Contract</c>
///  • مشتری ⇐ فروش‌های لغونشدهٔ همان محموله، با fallback به مشتریِ قرارداد
///  • کرایهٔ کشتی ⇐ <c>ExpenseTransaction</c>های همان محموله که
///    <see cref="VesselFreightClassifier"/> آن‌ها را کرایهٔ خودِ کشتی می‌شناسد. عمداً
///    محدودتر از دستهٔ <see cref="ShipmentExpenseCategory.Freight"/> است: آن دسته کرایهٔ
///    خط‌آهن، مخزن و موتر را هم شامل می‌شود و برای این گزارش عدد را بزرگ‌تر از واقع
///    نشان می‌داد. دیمرج هم کرایه نیست و نمی‌آید.
///  • وضعیت سفر ⇐ مشتق از <c>InventoryTransportLeg.Status</c> و تاریخ رسیدن.
///
/// «نرخ کرایه کشتی» ستون ذخیره‌شده ندارد و مشتق است (مبلغ ÷ مقدار). «Consignee» در ساختار
/// فعلی هیچ منبعی ندارد و عمداً خالی می‌ماند؛ هیچ مقداری حدس زده نمی‌شود.
/// </summary>
public partial class ReportsController
{
    private const int VesselVoyagePageSize = 50;

    [EnableRateLimiting(RateLimitPolicies.HeavyReport)]
    public async Task<IActionResult> VesselVoyages(
        [FromQuery] VesselVoyageReportFilterViewModel? filter = null,
        int page = 1,
        [FromQuery(Name = "pageSize")] int? perPage = null,
        CancellationToken cancellationToken = default)
    {
        filter ??= new VesselVoyageReportFilterViewModel();
        await PopulateVesselVoyageLookupsAsync(filter, cancellationToken);

        var pageSize = ListPageSize.Resolve(perPage, VesselVoyagePageSize);
        ViewData["PageSize"] = pageSize;
        ViewData["DefaultPageSize"] = VesselVoyagePageSize;

        return View(await BuildVesselVoyageReportAsync(filter, page, paginate: true, cancellationToken, pageSize));
    }

    [HttpGet]
    [EnableRateLimiting(RateLimitPolicies.CsvExport)]
    public async Task<IActionResult> VesselVoyagesExport(
        string? format,
        [FromQuery] VesselVoyageReportFilterViewModel? filter = null,
        CancellationToken cancellationToken = default)
    {
        filter ??= new VesselVoyageReportFilterViewModel();
        // paginate: false — خروجی همهٔ نتایج فیلترشده است، نه فقط صفحهٔ جاری، و از همان
        // Builder صفحه می‌آید تا اعداد خروجی و صفحه نتوانند فرق کنند.
        var model = await BuildVesselVoyageReportAsync(filter, page: 1, paginate: false, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        var fiscalYearName = filter.FiscalYearId.HasValue
            ? await _db.FiscalYears.AsNoTracking()
                .Where(y => y.Id == filter.FiscalYearId.Value)
                .Select(y => y.Name)
                .FirstOrDefaultAsync(cancellationToken)
            : null;

        return TabularExportSupport.File(this, format, new TabularExportDocument
        {
            FileNameStem = "PTG_Vessel_Voyages",
            TitleFa = "گزارش کشتی‌ها",
            TitleEn = "Vessel Voyages Report",
            KnownRowCount = model.Rows.Count,
            ForceLandscape = true,
            Filters = TabularExportSupport.FilterSummary(
                ("سال مالی / Fiscal year", fiscalYearName),
                ("از تاریخ / From", filter.FromDate?.ToString("yyyy-MM-dd")),
                ("تا تاریخ / To", filter.ToDate?.ToString("yyyy-MM-dd")),
                ("کشتی / Vessel", filter.VesselId),
                ("محصول / Product", filter.ProductId),
                ("مشتری / Customer", filter.CustomerId),
                ("تأمین‌کننده / Supplier", filter.SupplierId),
                ("مقصد / Destination", filter.DestinationLocationId),
                ("کمپنی ترانسپورتی / Transport company", filter.ServiceProviderId)),
            Columns =
            [
                new("ردیف", "No", TabularExportValueType.Number, 8),
                new("تاریخ", "Date", TabularExportValueType.Date, 13),
                new("کد سفر", "Code No", Width: 14),
                new("نام کشتی", "Vessel Name", Width: 20),
                new("محصول", "Kind - Cargo", Width: 16),
                new("مقدار MT", "Quantity MT", TabularExportValueType.Number, 15),
                new("Consignee", "Consignee", Width: 20),
                new("بندر بارگیری", "Loading Port", Width: 18),
                new("مقصد", "Destination", Width: 18),
                new("مشتری", "Customer", Width: 20),
                new("تأمین‌کننده / Shipper", "Shipper", Width: 22),
                new("تخصیص Shipper", "Shipper allocation", Width: 30, Wrap: true),
                new("کمپنی ترانسپورتی", "Transport company", Width: 20),
                new("نوع کرایه کشتی", "Vessel freight type", Width: 20),
                new("نرخ کرایه کشتی USD/MT", "Vessel freight rate USD/MT", TabularExportValueType.Number, 17),
                new("مبلغ کل کرایه کشتی USD", "Total vessel freight USD", TabularExportValueType.Number, 18),
                new("وضعیت سفر", "Voyage status", Width: 14),
                new("توضیحات", "Notes", Width: 28, Wrap: true)
            ],
            Rows = model.Rows.Select(r => new TabularExportRow(
            [
                TabularExportCell.Number(r.RowNumber),
                TabularExportCell.Date(r.VoyageDate),
                TabularExportCell.Text(r.ShipmentCode),
                TabularExportCell.Text(r.VesselName ?? "—"),
                TabularExportCell.Text(r.ProductText ?? "—"),
                TabularExportCell.Number(r.QuantityMt),
                TabularExportCell.Text(r.ConsigneeText ?? "—"),
                TabularExportCell.Text(r.LoadingPortName ?? "—"),
                TabularExportCell.Text(r.DestinationName ?? "—"),
                TabularExportCell.Text(r.CustomerText ?? "—"),
                TabularExportCell.Text(r.ShipperText ?? "—"),
                TabularExportCell.Text(FormatShipperAllocations(r)),
                TabularExportCell.Text(r.TransportCompanyText ?? "—"),
                TabularExportCell.Text(r.FreightTypeText ?? "—"),
                TabularExportCell.Number(r.FreightRateUsdPerMt ?? 0m),
                TabularExportCell.Number(r.FreightTotalUsd),
                TabularExportCell.Text(VesselVoyageStatusLabel(r.Status)),
                TabularExportCell.Text(r.Notes)
            ])),
            Totals = new TabularExportRow(
            [
                TabularExportCell.Text("جمع / Total"),
                TabularExportCell.Text(null), TabularExportCell.Text(null),
                TabularExportCell.Text($"{model.Totals.VesselCount:N0} کشتی / vessels"),
                TabularExportCell.Text($"دیزل {model.Totals.TotalDieselMt:N4} / بنزین {model.Totals.TotalGasolineMt:N4}"),
                TabularExportCell.Number(model.Totals.TotalQuantityMt),
                TabularExportCell.Text(null), TabularExportCell.Text(null), TabularExportCell.Text(null),
                TabularExportCell.Text(null), TabularExportCell.Text(null), TabularExportCell.Text(null),
                TabularExportCell.Text(null), TabularExportCell.Text(null), TabularExportCell.Text(null),
                TabularExportCell.Number(model.Totals.TotalFreightUsd),
                TabularExportCell.Text($"{model.Totals.VoyageCount:N0} سفر / voyages"),
                TabularExportCell.Text(null)
            ])
        });
    }

    internal static string VesselVoyageStatusLabel(VesselVoyageStatus status) => status switch
    {
        VesselVoyageStatus.InTransit => "در مسیر",
        VesselVoyageStatus.Arrived => "رسیده",
        VesselVoyageStatus.Completed => "تکمیل‌شده",
        VesselVoyageStatus.Cancelled => "لغوشده",
        _ => "ثبت‌شده"
    };

    internal static string VesselVoyageStatusCss(VesselVoyageStatus status) => status switch
    {
        VesselVoyageStatus.Completed => "ak-status is-active",
        VesselVoyageStatus.Cancelled => "ak-status is-danger",
        VesselVoyageStatus.InTransit or VesselVoyageStatus.Arrived => "ak-status is-pending",
        _ => "ak-status"
    };

    private static string FormatShipperAllocations(VesselVoyageRowViewModel row)
        => row.ShipperLines.Count == 0
            ? "—"
            : string.Join(" # ", row.ShipperLines.Select(line =>
                $"{line.AllocatedQuantityMt:N3} {line.SupplierName ?? line.ContractNumber}"));

    // ------------------------------------------------------------------ builder

    /// <summary>
    /// نقطهٔ ورودِ تست به همان Builderی که صفحه و خروجی استفاده می‌کنند — تا تست هرگز
    /// نسخهٔ دوم منطق را نسنجد.
    /// </summary>
    internal Task<VesselVoyageReportViewModel> BuildVesselVoyagesForTestAsync(
        VesselVoyageReportFilterViewModel filter,
        bool paginate = true,
        CancellationToken cancellationToken = default)
        => BuildVesselVoyageReportAsync(filter, page: 1, paginate, cancellationToken);

    private async Task<VesselVoyageReportViewModel> BuildVesselVoyageReportAsync(
        VesselVoyageReportFilterViewModel filter,
        int page,
        bool paginate,
        CancellationToken cancellationToken,
        int pageSize = VesselVoyagePageSize)
    {
        var (fiscalFrom, fiscalTo) = await ResolveFiscalYearRangeAsync(filter.FiscalYearId, cancellationToken);

        // بازهٔ مؤثر = تقاطع سال مالی و بازهٔ دستی، تا فیلتر دستی هرگز از سال مالی بیرون نزند.
        var from = MaxDate(fiscalFrom, filter.FromDate?.Date);
        var to = MinDate(fiscalTo, filter.ToDate?.Date);

        var query = _db.Shipments.AsNoTracking();

        if (from.HasValue)
        {
            query = query.Where(s => (s.DepartureDate ?? s.ArrivalDate) >= from.Value);
        }

        if (to.HasValue)
        {
            query = query.Where(s => (s.DepartureDate ?? s.ArrivalDate) <= to.Value);
        }

        if (filter.VesselId.HasValue)
        {
            query = query.Where(s => s.VesselId == filter.VesselId.Value);
        }

        if (filter.DestinationLocationId.HasValue)
        {
            query = query.Where(s => s.DestinationLocationId == filter.DestinationLocationId.Value);
        }

        if (filter.ProductId.HasValue)
        {
            var productId = filter.ProductId.Value;
            query = query.Where(s =>
                s.ShipmentContracts.Any(sc => sc.Contract!.ProductId == productId)
                || (s.Contract != null && s.Contract.ProductId == productId));
        }

        if (filter.SupplierId.HasValue)
        {
            var supplierId = filter.SupplierId.Value;
            query = query.Where(s =>
                s.ShipmentContracts.Any(sc => sc.Contract!.SupplierId == supplierId)
                || (s.Contract != null && s.Contract.SupplierId == supplierId));
        }

        if (filter.CustomerId.HasValue)
        {
            var customerId = filter.CustomerId.Value;
            query = query.Where(s =>
                _db.SalesTransactions.Any(t => t.ShipmentId == s.Id && !t.IsCancelled && t.CustomerId == customerId)
                || s.ShipmentContracts.Any(sc => sc.Contract!.CustomerId == customerId)
                || (s.Contract != null && s.Contract.CustomerId == customerId));
        }

        if (filter.ServiceProviderId.HasValue)
        {
            var providerId = filter.ServiceProviderId.Value;
            query = query.Where(s => _db.ExpenseTransactions.Any(e =>
                e.ShipmentId == s.Id && !e.IsCancelled && e.ServiceProviderId == providerId));
        }

        var totals = await BuildVesselVoyageTotalsAsync(query, cancellationToken);

        var pageCount = Math.Max(1, (int)Math.Ceiling(totals.VoyageCount / (double)pageSize));
        page = Math.Clamp(page, 1, pageCount);

        var ordered = query
            .OrderByDescending(s => s.DepartureDate ?? s.ArrivalDate)
            .ThenByDescending(s => s.Id);

        var pageQuery = paginate
            ? ordered.Skip((page - 1) * pageSize).Take(pageSize)
            : ordered;

        // فقط شناسه‌ها؛ بقیهٔ داده در چند Query دسته‌ای می‌آید تا هیچ N+1 ساخته نشود.
        var headers = await pageQuery
            .Select(s => new VesselVoyageHeaderRow(
                s.Id,
                s.ShipmentCode,
                s.DepartureDate ?? s.ArrivalDate,
                s.ArrivalDate,
                s.VesselId,
                s.Vessel != null ? s.Vessel.Name : null,
                s.OriginLocation != null ? s.OriginLocation.Name : null,
                s.DestinationLocation != null ? s.DestinationLocation.Name : null,
                s.QuantityMt,
                s.Notes))
            .ToListAsync(cancellationToken);

        var rows = await BuildVesselVoyageRowsAsync(
            headers,
            startRowNumber: paginate ? ((page - 1) * pageSize) + 1 : 1,
            cancellationToken);

        return new VesselVoyageReportViewModel
        {
            Filter = filter,
            Rows = rows,
            Totals = totals,
            CurrentPage = page,
            PageCount = pageCount
        };
    }

    private sealed record VesselVoyageHeaderRow(
        int Id,
        string ShipmentCode,
        DateTime? VoyageDate,
        DateTime? ArrivalDate,
        int? VesselId,
        string? VesselName,
        string? OriginName,
        string? DestinationName,
        decimal QuantityMt,
        string? Notes);

    /// <summary>
    /// جمع‌های کارت‌های بالای گزارش روی **کل نتایج فیلترشده** (نه فقط صفحهٔ جاری) تا عدد
    /// کارت‌ها با خروجی Excel/PDF یکی بماند.
    /// </summary>
    private async Task<VesselVoyageTotalsViewModel> BuildVesselVoyageTotalsAsync(
        IQueryable<Shipment> filtered,
        CancellationToken cancellationToken)
    {
        var headline = await filtered
            .GroupBy(_ => 1)
            .Select(g => new
            {
                VoyageCount = g.Count(),
                TotalQuantityMt = g.Sum(s => s.QuantityMt)
            })
            .FirstOrDefaultAsync(cancellationToken);

        // شمارش کشتی‌های متمایز عمداً یک Query سادهٔ جداست (COUNT DISTINCT) و نه Aggregate
        // تودرتو داخل GroupBy، تا ترجمهٔ SQL روی PostgreSQL قابل پیش‌بینی بماند.
        var vesselCount = await filtered
            .Where(s => s.VesselId != null)
            .Select(s => s.VesselId!.Value)
            .Distinct()
            .CountAsync(cancellationToken);

        // تفکیک دیزل/بنزین از تخصیص قراردادها می‌آید (SQL group by نام محصول)، و برای
        // سفرهایی که هنوز تخصیص ندارند از قرارداد اصلیِ خودِ محموله.
        var allocationSplit = await filtered
            .SelectMany(s => s.ShipmentContracts
                .Select(sc => new { Name = sc.Contract!.Product!.Name, Qty = sc.QuantityMt ?? 0m }))
            .GroupBy(x => x.Name)
            .Select(g => new { ProductName = g.Key, Qty = g.Sum(x => x.Qty) })
            .ToListAsync(cancellationToken);

        var unallocatedSplit = await filtered
            .Where(s => !s.ShipmentContracts.Any() && s.Contract != null)
            .GroupBy(s => s.Contract!.Product!.Name)
            .Select(g => new { ProductName = g.Key, Qty = g.Sum(s => s.QuantityMt) })
            .ToListAsync(cancellationToken);

        var dieselMt = 0m;
        var gasolineMt = 0m;
        foreach (var entry in allocationSplit.Concat(unallocatedSplit))
        {
            switch (VesselVoyageFuelClassifier.Classify(entry.ProductName))
            {
                case VesselVoyageFuelKind.Diesel:
                    dieselMt += entry.Qty;
                    break;
                case VesselVoyageFuelKind.Gasoline:
                    gasolineMt += entry.Qty;
                    break;
            }
        }

        // کرایه دریایی: همان دسته‌بندیِ صفحهٔ سود و زیان محموله. چون دسته‌بندی در حافظه
        // انجام می‌شود، فقط ستون‌های لازم کشیده می‌شوند (Projection باریک، بدون Include).
        var freightUsd = (await FetchVesselVoyageFreightAsync(
                _db.ExpenseTransactions.AsNoTracking()
                    .Where(e => e.ShipmentId != null && filtered.Select(s => s.Id).Contains(e.ShipmentId!.Value)),
                cancellationToken))
            .Sum(e => e.AmountUsd);

        return new VesselVoyageTotalsViewModel
        {
            VoyageCount = headline?.VoyageCount ?? 0,
            VesselCount = vesselCount,
            TotalQuantityMt = headline?.TotalQuantityMt ?? 0m,
            TotalDieselMt = decimal.Round(dieselMt, 4, MidpointRounding.AwayFromZero),
            TotalGasolineMt = decimal.Round(gasolineMt, 4, MidpointRounding.AwayFromZero),
            TotalFreightUsd = decimal.Round(freightUsd, 4, MidpointRounding.AwayFromZero)
        };
    }

    private sealed record VesselVoyageFreightRow(
        int Id,
        int ShipmentId,
        DateTime ExpenseDate,
        string ExpenseTypeName,
        string? ServiceProviderName,
        decimal AmountUsd);

    /// <summary>
    /// «کرایهٔ کشتی» یک مجموعه محموله — فقط کرایهٔ خودِ کشتی، نه کرایهٔ کل مسیر.
    /// عمداً از <see cref="ShipmentExpenseCategory.Freight"/> باریک‌تر است: آن دسته هر مصرفِ
    /// حمل (خط‌آهن، کرایهٔ مخزن، موتر) را هم می‌گیرد، ولی این گزارش فقط کرایهٔ دریایی را
    /// می‌خواهد. تشخیص با <see cref="VesselFreightClassifier"/> است.
    /// «کرایه رسید حمل» مثل صفحهٔ سود و زیان کنار گذاشته می‌شود تا دوباره‌شماری نشود.
    /// </summary>
    private static async Task<List<VesselVoyageFreightRow>> FetchVesselVoyageFreightAsync(
        IQueryable<ExpenseTransaction> scoped,
        CancellationToken cancellationToken)
    {
        var candidates = await scoped
            .Where(e => !e.IsCancelled
                && (e.ExpenseType == null
                    || e.ExpenseType.Code != Services.InventoryTransportReceiptService.ReceiptFreightExpenseCode))
            .Select(e => new
            {
                e.Id,
                ShipmentId = e.ShipmentId!.Value,
                e.ExpenseDate,
                e.AmountUsd,
                e.Description,
                ExpenseTypeName = e.ExpenseType != null ? e.ExpenseType.Name : null,
                ServiceProviderName = e.ServiceProvider != null ? e.ServiceProvider.Name : null
            })
            .ToListAsync(cancellationToken);

        return candidates
            .Where(e => VesselFreightClassifier.IsVesselFreight(e.ExpenseTypeName, e.Description))
            .Select(e => new VesselVoyageFreightRow(
                e.Id,
                e.ShipmentId,
                e.ExpenseDate,
                e.ExpenseTypeName ?? "-",
                e.ServiceProviderName,
                e.AmountUsd))
            .ToList();
    }

    private async Task<List<VesselVoyageRowViewModel>> BuildVesselVoyageRowsAsync(
        IReadOnlyList<VesselVoyageHeaderRow> headers,
        int startRowNumber,
        CancellationToken cancellationToken)
    {
        if (headers.Count == 0)
        {
            return [];
        }

        var ids = headers.Select(h => h.Id).ToList();

        var allocations = await _db.ShipmentContracts.AsNoTracking()
            .Where(sc => ids.Contains(sc.ShipmentId))
            .OrderBy(sc => sc.Id)
            .Select(sc => new
            {
                sc.ShipmentId,
                sc.ContractId,
                ContractNumber = sc.Contract != null ? sc.Contract.ContractNumber : null,
                SupplierName = sc.Contract != null && sc.Contract.Supplier != null ? sc.Contract.Supplier.Name : null,
                ProductName = sc.Contract != null && sc.Contract.Product != null ? sc.Contract.Product.Name : null,
                CompanyName = sc.Contract != null && sc.Contract.Company != null ? sc.Contract.Company.Name : null,
                CustomerName = sc.Contract != null && sc.Contract.Customer != null ? sc.Contract.Customer.Name : null,
                QuantityMt = sc.QuantityMt ?? 0m
            })
            .ToListAsync(cancellationToken);

        var freight = await FetchVesselVoyageFreightAsync(
            _db.ExpenseTransactions.AsNoTracking().Where(e => e.ShipmentId != null && ids.Contains(e.ShipmentId!.Value)),
            cancellationToken);

        // مشتریِ سفر از فروش‌های لغونشدهٔ همان محموله.
        var saleCustomers = await _db.SalesTransactions.AsNoTracking()
            .Where(t => t.ShipmentId != null && ids.Contains(t.ShipmentId!.Value) && !t.IsCancelled)
            .Select(t => new
            {
                ShipmentId = t.ShipmentId!.Value,
                CustomerName = t.Customer != null ? t.Customer.Name : null
            })
            .Distinct()
            .ToListAsync(cancellationToken);

        // وضعیت سفر از وضعیت حمل‌های همان محموله.
        var legStatuses = await _db.InventoryTransportLegs.AsNoTracking()
            .Where(l => l.ShipmentId != null && ids.Contains(l.ShipmentId!.Value))
            .GroupBy(l => l.ShipmentId!.Value)
            .Select(g => new
            {
                ShipmentId = g.Key,
                Total = g.Count(),
                Cancelled = g.Count(l => l.Status == InventoryTransportLegStatus.Cancelled),
                Received = g.Count(l => l.Status == InventoryTransportLegStatus.Received),
                Moving = g.Count(l => l.Status == InventoryTransportLegStatus.Loaded
                    || l.Status == InventoryTransportLegStatus.InTransit)
            })
            .ToListAsync(cancellationToken);

        var allocationsByShipment = allocations.GroupBy(a => a.ShipmentId).ToDictionary(g => g.Key, g => g.ToList());
        var freightByShipment = freight.GroupBy(f => f.ShipmentId).ToDictionary(g => g.Key, g => g.ToList());
        var customersByShipment = saleCustomers.GroupBy(c => c.ShipmentId).ToDictionary(g => g.Key, g => g.ToList());
        var legsByShipment = legStatuses.ToDictionary(l => l.ShipmentId);

        var rows = new List<VesselVoyageRowViewModel>(headers.Count);
        var rowNumber = startRowNumber;

        foreach (var header in headers)
        {
            allocationsByShipment.TryGetValue(header.Id, out var shipmentAllocations);
            shipmentAllocations ??= [];
            freightByShipment.TryGetValue(header.Id, out var shipmentFreight);
            shipmentFreight ??= [];

            var shipperLines = shipmentAllocations
                .Select(a => new VesselVoyageShipperLineViewModel
                {
                    ContractId = a.ContractId,
                    ContractNumber = a.ContractNumber ?? $"#{a.ContractId}",
                    SupplierName = a.SupplierName,
                    ProductName = a.ProductName,
                    CompanyName = a.CompanyName,
                    AllocatedQuantityMt = a.QuantityMt
                })
                .ToList();

            var freightLines = shipmentFreight
                .OrderByDescending(f => f.ExpenseDate)
                .ThenByDescending(f => f.Id)
                .Select(f => new VesselVoyageFreightLineViewModel
                {
                    ExpenseId = f.Id,
                    ExpenseDate = f.ExpenseDate,
                    ExpenseTypeName = f.ExpenseTypeName,
                    ServiceProviderName = f.ServiceProviderName,
                    AmountUsd = f.AmountUsd
                })
                .ToList();

            customersByShipment.TryGetValue(header.Id, out var saleCustomerRows);
            var customerNames = (saleCustomerRows ?? [])
                .Select(c => c.CustomerName)
                .Concat(shipmentAllocations.Select(a => a.CustomerName));

            rows.Add(new VesselVoyageRowViewModel
            {
                RowNumber = rowNumber++,
                ShipmentId = header.Id,
                VoyageDate = header.VoyageDate,
                ShipmentCode = header.ShipmentCode,
                VesselId = header.VesselId,
                VesselName = header.VesselName,
                ProductText = JoinDistinct(shipmentAllocations.Select(a => a.ProductName), "چند محصول"),
                QuantityMt = header.QuantityMt,
                // ساختار فعلی هیچ فیلدی برای Consignee ندارد؛ حدس زده نمی‌شود.
                ConsigneeText = null,
                LoadingPortName = header.OriginName,
                DestinationName = header.DestinationName,
                CustomerText = JoinDistinct(customerNames, "چند مشتری"),
                ShipperText = JoinDistinct(shipmentAllocations.Select(a => a.SupplierName), "چند تأمین‌کننده"),
                ShipperLines = shipperLines,
                AllocatedQuantityMt = decimal.Round(
                    shipperLines.Sum(l => l.AllocatedQuantityMt), 4, MidpointRounding.AwayFromZero),
                TransportCompanyText = JoinDistinct(
                    shipmentFreight.Select(f => f.ServiceProviderName), "چند کمپنی"),
                FreightTypeText = JoinDistinct(
                    shipmentFreight.Select(f => f.ExpenseTypeName), "چند نوع کرایه"),
                FreightTotalUsd = decimal.Round(
                    shipmentFreight.Sum(f => f.AmountUsd), 4, MidpointRounding.AwayFromZero),
                FreightLines = freightLines,
                Status = ResolveVesselVoyageStatus(
                    legsByShipment.TryGetValue(header.Id, out var legs)
                        ? (legs.Total, legs.Cancelled, legs.Received, legs.Moving)
                        : null,
                    header.ArrivalDate),
                Notes = header.Notes
            });
        }

        return rows;
    }

    /// <summary>
    /// وضعیتِ مشتقِ سفر. هیچ‌جا ذخیره نمی‌شود و هیچ منطق موجودی/مالی به آن وابسته نیست.
    /// </summary>
    internal static VesselVoyageStatus ResolveVesselVoyageStatus(
        (int Total, int Cancelled, int Received, int Moving)? legs,
        DateTime? arrivalDate)
    {
        if (legs is not { Total: > 0 } counts)
        {
            return arrivalDate.HasValue ? VesselVoyageStatus.Arrived : VesselVoyageStatus.Registered;
        }

        if (counts.Cancelled == counts.Total)
        {
            return VesselVoyageStatus.Cancelled;
        }

        var active = counts.Total - counts.Cancelled;
        if (counts.Received == active)
        {
            return VesselVoyageStatus.Completed;
        }

        return counts.Moving > 0 ? VesselVoyageStatus.InTransit : VesselVoyageStatus.Registered;
    }

    /// <summary>
    /// بازهٔ یک سال مالی. سال فقط وقتی پذیرفته می‌شود که به شرکت مالک سیستم تعلق داشته
    /// باشد — همان گاردی که <c>FiscalYearContextService</c> دارد؛ سال شرکت دیگر بی‌اثر است.
    /// </summary>
    private async Task<(DateTime? From, DateTime? To)> ResolveFiscalYearRangeAsync(
        int? fiscalYearId,
        CancellationToken cancellationToken)
    {
        if (!fiscalYearId.HasValue)
        {
            return (null, null);
        }

        var ownerCompanyId = await _systemCompany.FindOwnerCompanyIdAsync(cancellationToken);
        if (ownerCompanyId is not int owner)
        {
            return (null, null);
        }

        var range = await _db.FiscalYears.AsNoTracking()
            .Where(y => y.Id == fiscalYearId.Value && y.CompanyId == owner)
            .Select(y => new { y.StartDate, y.EndDate })
            .FirstOrDefaultAsync(cancellationToken);

        return range is null ? (null, null) : (range.StartDate.Date, range.EndDate.Date);
    }

    private static DateTime? MaxDate(DateTime? a, DateTime? b)
        => a.HasValue && b.HasValue ? (a.Value > b.Value ? a : b) : a ?? b;

    private static DateTime? MinDate(DateTime? a, DateTime? b)
        => a.HasValue && b.HasValue ? (a.Value < b.Value ? a : b) : a ?? b;

    private static string? JoinDistinct(IEnumerable<string?> values, string mixedText)
    {
        var distinct = values
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .ToList();

        return distinct.Count switch
        {
            0 => null,
            1 => distinct[0],
            2 => string.Join(" # ", distinct),
            _ => mixedText
        };
    }

    private async Task PopulateVesselVoyageLookupsAsync(
        VesselVoyageReportFilterViewModel filter,
        CancellationToken cancellationToken)
    {
        var ownerCompanyId = await _systemCompany.FindOwnerCompanyIdAsync(cancellationToken);
        var fiscalYears = ownerCompanyId is int owner
            ? await _db.FiscalYears.AsNoTracking()
                .Where(y => y.CompanyId == owner)
                .OrderByDescending(y => y.StartDate)
                .ThenByDescending(y => y.Id)
                .Select(y => new LookupOption(y.Id, y.Name))
                .ToListAsync(cancellationToken)
            : [];

        ViewBag.VesselVoyageFiscalYears = new SelectList(
            fiscalYears, nameof(LookupOption.Id), nameof(LookupOption.Name), filter.FiscalYearId);

        ViewBag.VesselVoyageVessels = new SelectList(
            await _db.Vessels.AsNoTracking().OrderBy(v => v.Name)
                .Select(v => new LookupOption(v.Id, v.Name)).ToListAsync(cancellationToken),
            nameof(LookupOption.Id), nameof(LookupOption.Name), filter.VesselId);

        ViewBag.VesselVoyageProducts = new SelectList(
            await _db.Products.AsNoTracking().OrderBy(p => p.Name)
                .Select(p => new LookupOption(p.Id, p.Name)).ToListAsync(cancellationToken),
            nameof(LookupOption.Id), nameof(LookupOption.Name), filter.ProductId);

        ViewBag.VesselVoyageCustomers = new SelectList(
            await _db.Customers.AsNoTracking().Where(c => c.IsActive).OrderBy(c => c.Name)
                .Select(c => new LookupOption(c.Id, c.Name)).ToListAsync(cancellationToken),
            nameof(LookupOption.Id), nameof(LookupOption.Name), filter.CustomerId);

        ViewBag.VesselVoyageSuppliers = new SelectList(
            await _db.Suppliers.AsNoTracking().Where(s => s.IsActive).OrderBy(s => s.Name)
                .Select(s => new LookupOption(s.Id, s.Name)).ToListAsync(cancellationToken),
            nameof(LookupOption.Id), nameof(LookupOption.Name), filter.SupplierId);

        ViewBag.VesselVoyageDestinations = new SelectList(
            await _db.Locations.AsNoTracking().OrderBy(l => l.Name)
                .Select(l => new LookupOption(l.Id, l.Name)).ToListAsync(cancellationToken),
            nameof(LookupOption.Id), nameof(LookupOption.Name), filter.DestinationLocationId);

        ViewBag.VesselVoyageServiceProviders = new SelectList(
            await _db.ServiceProviders.AsNoTracking().Where(p => p.IsActive).OrderBy(p => p.Name)
                .Select(p => new LookupOption(p.Id, p.Name)).ToListAsync(cancellationToken),
            nameof(LookupOption.Id), nameof(LookupOption.Name), filter.ServiceProviderId);
    }
}
