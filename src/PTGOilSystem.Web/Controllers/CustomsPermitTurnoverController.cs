using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Infrastructure.RateLimiting;
using PTGOilSystem.Web.Models.Customs;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Exports;
using PTGOilSystem.Web.Models.Entities;

namespace PTGOilSystem.Web.Controllers;

/// <summary>
/// Read-only internal-control report. Aggregates existing customs declarations
/// per permit / vehicle so the company can see, for any date range, how many
/// trucks were cleared under each permit, the total quantity and the total
/// customs duty (turnover). It never creates Ledger/Payment rows and never
/// touches Stock, Inventory, Sales, Dispatch, Pricing or P&L.
/// </summary>
[Authorize]
public class CustomsPermitTurnoverController : Controller
{
    private readonly ApplicationDbContext _db;
    private readonly IPricingService _pricing;

    public CustomsPermitTurnoverController(ApplicationDbContext db, IPricingService? pricing = null)
    {
        _db = db;
        _pricing = pricing ?? new PricingService(db);
    }

    // GET: /CustomsPermitTurnover
    public async Task<IActionResult> Index(
        DateTime? fromDate = null,
        DateTime? toDate = null,
        string? permitHolder = null,
        string? permitNumber = null,
        string? accd = null,
        string? vehicle = null,
        string? type = null,
        string? goods = null,
        string? route = null,
        decimal taxPercent = 0m,
        string? displayCurrency = "USD")
        => View(await BuildAsync(
            fromDate, toDate, permitHolder, permitNumber, accd, vehicle, type, goods, route, taxPercent, displayCurrency));

    /// <summary>
    /// خروجی Excel/PDF همان گزارش. دقیقاً همان <see cref="BuildAsync"/> صفحه با همان فیلترها
    /// خوانده می‌شود؛ هیچ مبلغ گمرکی یا نرخی اینجا دوباره محاسبه نمی‌شود.
    /// </summary>
    [EnableRateLimiting(RateLimitPolicies.CsvExport)]
    public async Task<IActionResult> Export(
        string? format,
        DateTime? fromDate = null,
        DateTime? toDate = null,
        string? permitHolder = null,
        string? permitNumber = null,
        string? accd = null,
        string? vehicle = null,
        string? type = null,
        string? goods = null,
        string? route = null,
        decimal taxPercent = 0m,
        string? displayCurrency = "USD")
    {
        var model = await BuildAsync(
            fromDate, toDate, permitHolder, permitNumber, accd, vehicle, type, goods, route, taxPercent, displayCurrency);
        var rows = model.Rows.ToList();

        return TabularExportSupport.File(this, format, new TabularExportDocument
        {
            FileNameStem = "PTG_Customs_Permit_Turnover",
            TitleFa = "گردش جواز گمرکی",
            TitleEn = "Customs Permit Turnover",
            KnownRowCount = rows.Count,
            ForceLandscape = true,
            Filters = TabularExportSupport.FilterSummary(
                ("از تاریخ / From", model.FromDate?.ToString("yyyy-MM-dd")),
                ("تا تاریخ / To", model.ToDate?.ToString("yyyy-MM-dd")),
                ("صاحب جواز / Permit holder", model.PermitHolderName),
                ("نمبر جواز / Permit number", model.PermitNumber),
                ("نمبر ACCD / ACCD", model.AccdNumber),
                ("نمبر موتر / Vehicle", model.VehicleNumber),
                ("نوع / Type", model.CustomsType),
                ("جنس / Goods", model.GoodsName),
                ("مسیر / Route", model.Route),
                ("ارز نمایش / Display currency", model.SelectedCurrency)),
            Columns =
            [
                new("تاریخ", "Date", TabularExportValueType.Date, 13),
                new("نمبر ACCD", "ACCD", Width: 16), new("نمبر موتر", "Vehicle", Width: 15),
                new("نمبر جواز", "Permit", Width: 16), new("صاحب جواز", "Permit holder", Width: 22),
                new("نوع", "Type", Width: 14), new("جنس", "Goods", Width: 18),
                new("مقدار MT", "Quantity MT", TabularExportValueType.Number, 14),
                new("محصولی AFN", "Mahsooli AFN", TabularExportValueType.Number, 16),
                new("محصولی USD", "Mahsooli USD", TabularExportValueType.Number, 16),
                new("جمع گمرک AFN", "Total customs AFN", TabularExportValueType.Number, 17),
                new("جمع گمرک USD", "Total customs USD", TabularExportValueType.Number, 17),
                new("نرخ USD/AFN", "Rate USD/AFN", TabularExportValueType.Number, 14),
                new("مسیر", "Route", Width: 20)
            ],
            Rows = rows.Select(row => new TabularExportRow(
            [
                TabularExportCell.Date(row.DeclarationDate), TabularExportCell.Text(row.AccdNumber),
                TabularExportCell.Text(row.VehicleNumber), TabularExportCell.Text(row.PermitNumber),
                TabularExportCell.Text(row.PermitHolderName), TabularExportCell.Text(row.CustomsType),
                TabularExportCell.Text(row.GoodsName), TabularExportCell.Number(row.QuantityMt),
                TabularExportCell.Number(row.MahsooliAfn), TabularExportCell.Number(row.MahsooliUsd),
                TabularExportCell.Number(row.TotalCustomsAfn), TabularExportCell.Number(row.TotalCustomsUsd),
                TabularExportCell.Number(row.RateUsdToAfn), TabularExportCell.Text(row.Route)
            ])),
            Totals = new TabularExportRow(
            [
                TabularExportCell.Date(null), TabularExportCell.Text(null), TabularExportCell.Text(null),
                TabularExportCell.Text(null), TabularExportCell.Text(null), TabularExportCell.Text(null),
                TabularExportCell.Text(null), TabularExportCell.Number(model.TotalQuantityMt),
                TabularExportCell.Number(model.TotalMahsooliAfn), TabularExportCell.Number(model.TotalMahsooliUsd),
                TabularExportCell.Number(model.TotalCustomsAfn), TabularExportCell.Number(model.TotalCustomsUsd),
                TabularExportCell.Number(null), TabularExportCell.Text(null)
            ])
        });
    }

    private async Task<CustomsPermitTurnoverViewModel> BuildAsync(
        DateTime? fromDate,
        DateTime? toDate,
        string? permitHolder,
        string? permitNumber,
        string? accd,
        string? vehicle,
        string? type,
        string? goods,
        string? route,
        decimal taxPercent,
        string? displayCurrency)
    {
        string? N(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        permitHolder = N(permitHolder);
        permitNumber = N(permitNumber);
        accd = N(accd);
        vehicle = N(vehicle);
        type = N(type);
        goods = N(goods);
        route = N(route);
        if (taxPercent < 0m)
        {
            taxPercent = 0m;
        }

        var query = _db.CustomsDeclarations.AsNoTracking().AsQueryable();

        if (fromDate.HasValue)
        {
            query = query.Where(cd => cd.DeclarationDate >= fromDate.Value.Date);
        }

        if (toDate.HasValue)
        {
            var exclusiveToDate = toDate.Value.Date.AddDays(1);
            query = query.Where(cd => cd.DeclarationDate < exclusiveToDate);
        }

        if (permitHolder is not null)
        {
            query = query.Where(cd => cd.PermitHolderName != null && cd.PermitHolderName.Contains(permitHolder));
        }

        if (permitNumber is not null)
        {
            query = query.Where(cd => cd.PermitNumber != null && cd.PermitNumber.Contains(permitNumber));
        }

        if (accd is not null)
        {
            query = query.Where(cd => cd.DeclarationReference != null && cd.DeclarationReference.Contains(accd));
        }

        if (vehicle is not null)
        {
            query = query.Where(cd => cd.WagonOrTruckNumber != null && cd.WagonOrTruckNumber.Contains(vehicle));
        }

        if (type is not null)
        {
            query = query.Where(cd => cd.CustomsType != null && cd.CustomsType.Contains(type));
        }

        if (goods is not null)
        {
            query = query.Where(cd =>
                (cd.GoodsName != null && cd.GoodsName.Contains(goods))
                || (cd.LoadingRegister != null && cd.LoadingRegister.Product != null && cd.LoadingRegister.Product.Name.Contains(goods)));
        }

        if (route is not null)
        {
            query = query.Where(cd =>
                (cd.Route != null && cd.Route.Contains(route))
                || (cd.LoadingRegister != null && cd.LoadingRegister.RouteDescription != null && cd.LoadingRegister.RouteDescription.Contains(route)));
        }

        // Load declarations with their item breakdown so we can compute, per item, a single
        // canonical (AFN, USD) equivalent pair. Each customs row carries ONE real amount; the
        // other currency is derived from the rate. This prevents double-counting AFN + USD.
        var declarations = await query
            .OrderBy(cd => cd.DeclarationDate)
            .ThenBy(cd => cd.Id)
            .Select(cd => new
            {
                cd.Id,
                cd.DeclarationDate,
                AccdNumber = cd.DeclarationReference,
                VehicleNumber = cd.WagonOrTruckNumber,
                cd.PermitNumber,
                cd.PermitHolderName,
                cd.CustomsType,
                GoodsName = cd.GoodsName
                    ?? (cd.LoadingRegister != null && cd.LoadingRegister.Product != null ? cd.LoadingRegister.Product.Name : null),
                QuantityMt = cd.ConsignmentWeightMt,
                Route = cd.Route
                    ?? (cd.LoadingRegister != null ? cd.LoadingRegister.RouteDescription : null),
                cd.Notes,
                Items = cd.Items.Select(i => new { i.ComponentType, i.AmountAfn, i.AmountUsd }).ToList()
            })
            .ToListAsync();

        var primaryCurrency = (displayCurrency ?? "USD").Trim().ToUpperInvariant();
        var fxCache = new Dictionary<DateTime, decimal>();
        var rows = new List<CustomsPermitTurnoverRowViewModel>(declarations.Count);

        foreach (var cd in declarations)
        {
            var date = cd.DeclarationDate.Date;
            decimal rate = 0m;
            try
            {
                if (!fxCache.TryGetValue(date, out rate))
                {
                    var fx = await _pricing.GetFxRateAsync("USD", "AFN", date);
                    rate = fx.Value;
                    fxCache[date] = rate;
                }
            }
            catch
            {
                // fallback to DB lookup
                rate = await _db.DailyFxRates
                    .AsNoTracking()
                    .Where(r => r.BaseCurrency == "USD" && r.QuoteCurrency == "AFN" && r.RateDate <= date)
                    .OrderByDescending(r => r.RateDate)
                    .Select(r => r.Rate)
                    .FirstOrDefaultAsync();
                fxCache[date] = rate;
            }

            // Canonical (AFN, USD) equivalents per item: use the stored amount where present,
            // derive the missing currency from the rate. Both totals are real equivalents — the
            // same money expressed in two currencies — so summing either is consistent.
            decimal mahsooliAfn = 0m, mahsooliUsd = 0m, totalAfn = 0m, totalUsd = 0m;
            foreach (var item in cd.Items)
            {
                var (afn, usd) = CanonicalAmounts(item.AmountAfn, item.AmountUsd, rate);
                totalAfn += afn;
                totalUsd += usd;
                if (item.ComponentType is CustomsComponentType.Mahsooli or CustomsComponentType.MahsooliDolari)
                {
                    mahsooliAfn += afn;
                    mahsooliUsd += usd;
                }
            }

            var row = new CustomsPermitTurnoverRowViewModel
            {
                Id = cd.Id,
                DeclarationDate = cd.DeclarationDate,
                AccdNumber = cd.AccdNumber,
                VehicleNumber = cd.VehicleNumber,
                PermitNumber = cd.PermitNumber,
                PermitHolderName = cd.PermitHolderName,
                CustomsType = cd.CustomsType,
                GoodsName = cd.GoodsName,
                QuantityMt = cd.QuantityMt,
                MahsooliAfn = mahsooliAfn,
                MahsooliUsd = mahsooliUsd,
                TotalCustomsAfn = totalAfn,
                TotalCustomsUsd = totalUsd,
                Route = cd.Route,
                Notes = cd.Notes,
                RateUsdToAfn = rate,
                // Kept for backward-compatibility; equals the canonical AFN total (no double count).
                TotalCustomsAfnEquivalent = totalAfn
            };

            // Single-currency display with toggle: show the selected currency, the equivalent is
            // simply the canonical amount in the other currency (never native + converted).
            if (primaryCurrency == "AFN")
            {
                row.MahsooliDisplayAmount = row.MahsooliAfn;
                row.MahsooliDisplayCurrency = "AFN";
                row.MahsooliEquivalentAmount = row.MahsooliUsd;
                row.MahsooliEquivalentCurrency = "USD";

                row.TotalCustomsDisplayAmount = row.TotalCustomsAfn;
                row.TotalCustomsDisplayCurrency = "AFN";
                row.TotalCustomsEquivalentAmount = row.TotalCustomsUsd;
                row.TotalCustomsEquivalentCurrency = "USD";
            }
            else
            {
                row.MahsooliDisplayAmount = row.MahsooliUsd;
                row.MahsooliDisplayCurrency = "USD";
                row.MahsooliEquivalentAmount = row.MahsooliAfn;
                row.MahsooliEquivalentCurrency = "AFN";

                row.TotalCustomsDisplayAmount = row.TotalCustomsUsd;
                row.TotalCustomsDisplayCurrency = "USD";
                row.TotalCustomsEquivalentAmount = row.TotalCustomsAfn;
                row.TotalCustomsEquivalentCurrency = "AFN";
            }

            rows.Add(row);
        }

        var model = new CustomsPermitTurnoverViewModel
        {
            FromDate = fromDate,
            ToDate = toDate,
            PermitHolderName = permitHolder,
            PermitNumber = permitNumber,
            AccdNumber = accd,
            VehicleNumber = vehicle,
            CustomsType = type,
            GoodsName = goods,
            Route = route,
            TaxPercent = taxPercent,
            Rows = rows,
            VehicleCount = rows.Count,
            TotalQuantityMt = rows.Sum(r => r.QuantityMt ?? 0m),
            TotalMahsooliAfn = rows.Sum(r => r.MahsooliAfn),
            TotalMahsooliUsd = rows.Sum(r => r.MahsooliUsd),
            TotalCustomsAfn = rows.Sum(r => r.TotalCustomsAfn),
            TotalCustomsUsd = rows.Sum(r => r.TotalCustomsUsd),
            TotalCustomsAfnEquivalent = rows.Sum(r => r.TotalCustomsAfn)
        };

        // Selected currency and summary display/equivalent amounts
        model.SelectedCurrency = (displayCurrency ?? "USD").Trim().ToUpperInvariant();
        model.TotalCustomsDisplayAmount = rows.Sum(r => r.TotalCustomsDisplayAmount);
        model.TotalCustomsDisplayCurrency = rows.FirstOrDefault()?.TotalCustomsDisplayCurrency ?? "USD";
        model.TotalCustomsEquivalentAmount = rows.Sum(r => r.TotalCustomsEquivalentAmount);
        model.TotalCustomsEquivalentCurrency = rows.FirstOrDefault()?.TotalCustomsEquivalentCurrency ?? "AFN";

        return model;
    }

    // یک قلم گمرکی فقط یک مبلغ اصلی دارد؛ این تابع جفت معادلِ (AFN, USD) را برمی‌گرداند:
    // مبلغِ ذخیره‌شده در هر ارز را همان‌طور نگه می‌دارد و ارز نبودهٔ دیگر را با نرخ (AFN در هر USD) می‌سازد.
    // برای داده‌های جدید هر دو ستون پر است ⇒ مستقیم استفاده می‌شود؛ برای داده‌های قدیمی ناقص با نرخ کامل می‌شود.
    private static (decimal Afn, decimal Usd) CanonicalAmounts(decimal amountAfn, decimal? amountUsd, decimal rate)
    {
        var usd = amountUsd.GetValueOrDefault();
        var afn = amountAfn;

        if (afn <= 0m && usd > 0m)
        {
            afn = usd * rate; // ردیف USD-only: AFN را از نرخ بساز
        }
        else if (usd <= 0m && afn > 0m && rate > 0m)
        {
            usd = afn / rate; // ردیف AFN-only: USD را از نرخ بساز
        }

        return (afn, usd);
    }
}
