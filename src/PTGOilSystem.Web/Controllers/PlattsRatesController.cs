using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.PlattsRates;
using PTGOilSystem.Web.Security;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Audit;
using PTGOilSystem.Web.Services.Time;

namespace PTGOilSystem.Web.Controllers;

[Authorize]
public class PlattsRatesController : Controller
{
    private readonly ApplicationDbContext _db;
    private readonly IAuditService _audit;

    public PlattsRatesController(ApplicationDbContext db, IAuditService audit)
    {
        _db = db;
        _audit = audit;
    }

    public async Task<IActionResult> Index(
        [FromQuery] PlattsRatesPageViewModel model,
        int? dailyEditId,
        int? manualEditId)
    {
        await PopulateLookupsAsync();
        return View(await BuildPageViewModelAsync(model, dailyEditId, manualEditId));
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveDaily(PlattsRatesPageViewModel model)
    {
        model.ActiveTab = PlattsRatesTabs.Daily;
        RevalidateDailyForm(model);

        var normalizedDate = NormalizeDate(model.DailyForm.PriceDate);
        if (ModelState.IsValid && await _db.DailyPlattsPrices.AnyAsync(p =>
                p.ProductId == model.DailyForm.ProductId
                && p.BenchmarkCode == model.DailyForm.BenchmarkCode
                && p.PriceDate == normalizedDate
                && (!model.DailyForm.Id.HasValue || p.Id != model.DailyForm.Id.Value)))
        {
            ModelState.AddModelError(string.Empty, "برای این کالا، benchmark و تاریخ، نرخ روزانه قبلاً ثبت شده است.");
        }

        if (!ModelState.IsValid)
        {
            await PopulateLookupsAsync();
            model.DailyForm.PriceDate = normalizedDate;
            return View("Index", await BuildPageViewModelAsync(model, model.DailyForm.Id, model.MonthlyManualForm.Id));
        }

        if (model.DailyForm.Id.HasValue)
        {
            var existing = await _db.DailyPlattsPrices.FirstOrDefaultAsync(x => x.Id == model.DailyForm.Id.Value);
            if (existing == null) return NotFound();

            var diff = AuditDiffFormatter.ForUpdate(
                ("ProductId", existing.ProductId, model.DailyForm.ProductId),
                ("BenchmarkCode", existing.BenchmarkCode, model.DailyForm.BenchmarkCode),
                ("PriceDate", existing.PriceDate, normalizedDate),
                ("PriceUsdPerMt", existing.PriceUsdPerMt, model.DailyForm.PriceUsdPerMt),
                ("Source", existing.Source, model.DailyForm.Source));

            existing.ProductId = model.DailyForm.ProductId;
            existing.BenchmarkCode = model.DailyForm.BenchmarkCode.Trim();
            existing.PriceDate = normalizedDate;
            existing.PriceUsdPerMt = model.DailyForm.PriceUsdPerMt;
            existing.Source = model.DailyForm.Source?.Trim();

            await _db.SaveChangesAsync();
            await _audit.LogAndSaveAsync(nameof(DailyPlattsPrice), existing.Id, AuditAction.Update, diff: diff);
            TempData["ok"] = "نرخ روزانه پلتس ویرایش شد.";
        }
        else
        {
            var entity = new DailyPlattsPrice
            {
                ProductId = model.DailyForm.ProductId,
                BenchmarkCode = model.DailyForm.BenchmarkCode.Trim(),
                PriceDate = normalizedDate,
                PriceUsdPerMt = model.DailyForm.PriceUsdPerMt,
                Source = model.DailyForm.Source?.Trim()
            };

            _db.DailyPlattsPrices.Add(entity);
            await _db.SaveChangesAsync();
            await _audit.LogAndSaveAsync(
                nameof(DailyPlattsPrice),
                entity.Id,
                AuditAction.Insert,
                diff: AuditDiffFormatter.ForCreate(
                    ("ProductId", entity.ProductId),
                    ("BenchmarkCode", entity.BenchmarkCode),
                    ("PriceDate", entity.PriceDate),
                    ("PriceUsdPerMt", entity.PriceUsdPerMt),
                    ("Source", entity.Source)));
            TempData["ok"] = "نرخ روزانه پلتس ثبت شد.";
        }

        return RedirectToAction(nameof(Index), new { activeTab = PlattsRatesTabs.Daily });
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteDaily(int id)
    {
        var item = await _db.DailyPlattsPrices.FirstOrDefaultAsync(x => x.Id == id);
        if (item == null) return NotFound();

        if (await IsDailyRateProtectedAsync(item))
        {
            TempData["err"] = "این نرخ روزانه با قراردادهای قیمت‌گذاری پلتس مرتبط است و حذف آن ایمن نیست.";
            return RedirectToAction(nameof(Index), new { activeTab = PlattsRatesTabs.Daily });
        }

        var diff = AuditDiffFormatter.ForDelete(
            ("ProductId", item.ProductId),
            ("BenchmarkCode", item.BenchmarkCode),
            ("PriceDate", item.PriceDate),
            ("PriceUsdPerMt", item.PriceUsdPerMt));

        _db.DailyPlattsPrices.Remove(item);
        await _db.SaveChangesAsync();
        await _audit.LogAndSaveAsync(nameof(DailyPlattsPrice), item.Id, AuditAction.Delete, diff: diff);
        TempData["ok"] = "نرخ روزانه پلتس حذف شد.";
        return RedirectToAction(nameof(Index), new { activeTab = PlattsRatesTabs.Daily });
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveMonthlyManual(PlattsRatesPageViewModel model)
    {
        model.ActiveTab = PlattsRatesTabs.Manual;
        RevalidateMonthlyManualForm(model);

        var normalizedMonth = NormalizeMonth(model.MonthlyManualForm.Month);
        if (ModelState.IsValid && await _db.PlattsMonthlyManuals.AnyAsync(p =>
                p.ProductId == model.MonthlyManualForm.ProductId
                && p.BenchmarkCode == model.MonthlyManualForm.BenchmarkCode
                && p.Month == normalizedMonth
                && (!model.MonthlyManualForm.Id.HasValue || p.Id != model.MonthlyManualForm.Id.Value)))
        {
            ModelState.AddModelError(string.Empty, "برای این کالا، benchmark و ماه، نرخ دستی ماهانه قبلاً ثبت شده است.");
        }

        if (!ModelState.IsValid)
        {
            await PopulateLookupsAsync();
            model.MonthlyManualForm.Month = normalizedMonth;
            return View("Index", await BuildPageViewModelAsync(model, model.DailyForm.Id, model.MonthlyManualForm.Id));
        }

        if (model.MonthlyManualForm.Id.HasValue)
        {
            var existing = await _db.PlattsMonthlyManuals.FirstOrDefaultAsync(x => x.Id == model.MonthlyManualForm.Id.Value);
            if (existing == null) return NotFound();

            var diff = AuditDiffFormatter.ForUpdate(
                ("ProductId", existing.ProductId, model.MonthlyManualForm.ProductId),
                ("BenchmarkCode", existing.BenchmarkCode, model.MonthlyManualForm.BenchmarkCode),
                ("Month", existing.Month, normalizedMonth),
                ("PriceUsdPerMt", existing.PriceUsdPerMt, model.MonthlyManualForm.PriceUsdPerMt),
                ("Notes", existing.Notes, model.MonthlyManualForm.Notes));

            existing.ProductId = model.MonthlyManualForm.ProductId;
            existing.BenchmarkCode = model.MonthlyManualForm.BenchmarkCode.Trim();
            existing.Month = normalizedMonth;
            existing.PriceUsdPerMt = model.MonthlyManualForm.PriceUsdPerMt;
            existing.Notes = model.MonthlyManualForm.Notes?.Trim();

            await _db.SaveChangesAsync();
            await _audit.LogAndSaveAsync(nameof(PlattsMonthlyManual), existing.Id, AuditAction.Update, diff: diff);
            TempData["ok"] = "نرخ دستی ماهانه پلتس ویرایش شد.";
        }
        else
        {
            var entity = new PlattsMonthlyManual
            {
                ProductId = model.MonthlyManualForm.ProductId,
                BenchmarkCode = model.MonthlyManualForm.BenchmarkCode.Trim(),
                Month = normalizedMonth,
                PriceUsdPerMt = model.MonthlyManualForm.PriceUsdPerMt,
                Notes = model.MonthlyManualForm.Notes?.Trim()
            };

            _db.PlattsMonthlyManuals.Add(entity);
            await _db.SaveChangesAsync();
            await _audit.LogAndSaveAsync(
                nameof(PlattsMonthlyManual),
                entity.Id,
                AuditAction.Insert,
                diff: AuditDiffFormatter.ForCreate(
                    ("ProductId", entity.ProductId),
                    ("BenchmarkCode", entity.BenchmarkCode),
                    ("Month", entity.Month),
                    ("PriceUsdPerMt", entity.PriceUsdPerMt),
                    ("Notes", entity.Notes)));
            TempData["ok"] = "نرخ دستی ماهانه پلتس ثبت شد.";
        }

        return RedirectToAction(nameof(Index), new { activeTab = PlattsRatesTabs.Manual });
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteMonthlyManual(int id)
    {
        var item = await _db.PlattsMonthlyManuals.FirstOrDefaultAsync(x => x.Id == id);
        if (item == null) return NotFound();

        if (await IsMonthlyManualProtectedAsync(item))
        {
            TempData["err"] = "این نرخ دستی ماهانه با قراردادهای پلتس ماهانه مرتبط است و حذف آن ایمن نیست.";
            return RedirectToAction(nameof(Index), new { activeTab = PlattsRatesTabs.Manual });
        }

        var diff = AuditDiffFormatter.ForDelete(
            ("ProductId", item.ProductId),
            ("BenchmarkCode", item.BenchmarkCode),
            ("Month", item.Month),
            ("PriceUsdPerMt", item.PriceUsdPerMt),
            ("Notes", item.Notes));

        _db.PlattsMonthlyManuals.Remove(item);
        await _db.SaveChangesAsync();
        await _audit.LogAndSaveAsync(nameof(PlattsMonthlyManual), item.Id, AuditAction.Delete, diff: diff);
        TempData["ok"] = "نرخ دستی ماهانه پلتس حذف شد.";
        return RedirectToAction(nameof(Index), new { activeTab = PlattsRatesTabs.Manual });
    }

    private async Task PopulateLookupsAsync()
    {
        ViewBag.Products = new SelectList(
            await _db.Products.AsNoTracking().Where(p => p.IsActive).OrderBy(p => p.Code).ToListAsync(),
            "Id",
            "Name");
    }

    private async Task<PlattsRatesPageViewModel> BuildPageViewModelAsync(
        PlattsRatesPageViewModel? source,
        int? dailyEditId,
        int? manualEditId)
    {
        var model = source ?? new PlattsRatesPageViewModel();
        model.ActiveTab = NormalizeTab(model.ActiveTab);
        model.MonthlyFilter.Year ??= DateTime.UtcNow.Year;
        model.DailyForm.PriceDate = model.DailyForm.PriceDate == default
            ? AfghanistanBusinessClock.SystemToday
            : NormalizeDate(model.DailyForm.PriceDate);
        model.MonthlyManualForm.Month = model.MonthlyManualForm.Month == default
            ? NormalizeMonth(DateTime.UtcNow)
            : NormalizeMonth(model.MonthlyManualForm.Month);

        if (dailyEditId.HasValue && model.DailyForm.Id != dailyEditId)
        {
            var editItem = await _db.DailyPlattsPrices
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == dailyEditId.Value);

            if (editItem is not null)
            {
                model.DailyForm = new DailyPlattsRateFormViewModel
                {
                    Id = editItem.Id,
                    ProductId = editItem.ProductId,
                    BenchmarkCode = editItem.BenchmarkCode,
                    PriceDate = editItem.PriceDate,
                    PriceUsdPerMt = editItem.PriceUsdPerMt,
                    Source = editItem.Source
                };
            }
        }

        if (manualEditId.HasValue && model.MonthlyManualForm.Id != manualEditId)
        {
            var editItem = await _db.PlattsMonthlyManuals
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == manualEditId.Value);

            if (editItem is not null)
            {
                model.MonthlyManualForm = new MonthlyManualRateFormViewModel
                {
                    Id = editItem.Id,
                    ProductId = editItem.ProductId,
                    BenchmarkCode = editItem.BenchmarkCode,
                    Month = editItem.Month,
                    PriceUsdPerMt = editItem.PriceUsdPerMt,
                    Notes = editItem.Notes
                };
            }
        }

        model.DailyRates = await LoadDailyRatesAsync(model.DailyFilter);
        model.MonthlySummaries = await LoadMonthlySummariesAsync(model.MonthlyFilter);
        model.MonthlyManualRates = await LoadMonthlyManualRatesAsync(model.ManualFilter);
        return model;
    }

    private async Task<IReadOnlyList<DailyPlattsRateRowViewModel>> LoadDailyRatesAsync(DailyPlattsRatesFilterViewModel filter)
    {
        var query = _db.DailyPlattsPrices
            .Include(p => p.Product)
            .AsNoTracking();

        if (filter.ProductId.HasValue)
            query = query.Where(p => p.ProductId == filter.ProductId.Value);
        if (!string.IsNullOrWhiteSpace(filter.BenchmarkCode))
            query = query.Where(p => p.BenchmarkCode.Contains(filter.BenchmarkCode.Trim()));
        if (filter.From.HasValue)
            query = query.Where(p => p.PriceDate >= NormalizeDate(filter.From.Value));
        if (filter.To.HasValue)
            query = query.Where(p => p.PriceDate <= NormalizeDate(filter.To.Value));

        return await query
            .OrderByDescending(p => p.PriceDate)
            .ThenBy(p => p.BenchmarkCode)
            .Take(500)
            .Select(p => new DailyPlattsRateRowViewModel
            {
                Id = p.Id,
                ProductId = p.ProductId,
                ProductName = p.Product != null ? p.Product.Name : "-",
                BenchmarkCode = p.BenchmarkCode,
                PriceDate = p.PriceDate,
                PriceUsdPerMt = p.PriceUsdPerMt,
                Source = p.Source
            })
            .ToListAsync();
    }

    private async Task<IReadOnlyList<MonthlyPlattsSummaryItemViewModel>> LoadMonthlySummariesAsync(MonthlyPlattsSummaryFilterViewModel filter)
    {
        var year = filter.Year ?? DateTime.UtcNow.Year;
        var from = new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var to = from.AddYears(1);

        var query = _db.DailyPlattsPrices
            .Include(p => p.Product)
            .AsNoTracking()
            .Where(p => p.PriceDate >= from && p.PriceDate < to);

        if (filter.ProductId.HasValue)
            query = query.Where(p => p.ProductId == filter.ProductId.Value);
        if (!string.IsNullOrWhiteSpace(filter.BenchmarkCode))
            query = query.Where(p => p.BenchmarkCode.Contains(filter.BenchmarkCode.Trim()));

        var rows = await query
            .OrderBy(p => p.PriceDate)
            .ToListAsync();

        return rows
            .GroupBy(p => new
            {
                p.ProductId,
                ProductName = p.Product?.Name ?? "-",
                p.BenchmarkCode,
                Month = NormalizeMonth(p.PriceDate)
            })
            .OrderBy(g => g.Key.Month)
            .ThenBy(g => g.Key.ProductName)
            .ThenBy(g => g.Key.BenchmarkCode)
            .Select(g => new MonthlyPlattsSummaryItemViewModel
            {
                ProductId = g.Key.ProductId,
                ProductName = g.Key.ProductName,
                BenchmarkCode = g.Key.BenchmarkCode,
                Month = g.Key.Month,
                AveragePriceUsdPerMt = decimal.Round(g.Average(x => x.PriceUsdPerMt), 4, MidpointRounding.AwayFromZero),
                DayCount = g.Count(),
                MinPriceUsdPerMt = g.Min(x => x.PriceUsdPerMt),
                MaxPriceUsdPerMt = g.Max(x => x.PriceUsdPerMt)
            })
            .ToList();
    }

    private async Task<IReadOnlyList<MonthlyManualRateRowViewModel>> LoadMonthlyManualRatesAsync(MonthlyManualRatesFilterViewModel filter)
    {
        var query = _db.PlattsMonthlyManuals
            .Include(p => p.Product)
            .AsNoTracking();

        if (filter.ProductId.HasValue)
            query = query.Where(p => p.ProductId == filter.ProductId.Value);
        if (!string.IsNullOrWhiteSpace(filter.BenchmarkCode))
            query = query.Where(p => p.BenchmarkCode.Contains(filter.BenchmarkCode.Trim()));

        return await query
            .OrderByDescending(p => p.Month)
            .ThenBy(p => p.BenchmarkCode)
            .Take(500)
            .Select(p => new MonthlyManualRateRowViewModel
            {
                Id = p.Id,
                ProductId = p.ProductId,
                ProductName = p.Product != null ? p.Product.Name : "-",
                BenchmarkCode = p.BenchmarkCode,
                Month = p.Month,
                PriceUsdPerMt = p.PriceUsdPerMt,
                Notes = p.Notes
            })
            .ToListAsync();
    }

    private async Task<bool> IsDailyRateProtectedAsync(DailyPlattsPrice rate)
        => await _db.Contracts.AsNoTracking().AnyAsync(c =>
            c.ProductId == rate.ProductId
            && c.PricingMethod == PricingMethod.FormulaPlatts
            && c.BenchmarkCode == rate.BenchmarkCode
            && (c.PlattsPeriodType == Models.Entities.PlattsPeriodType.Daily
                || c.PlattsPeriodType == Models.Entities.PlattsPeriodType.Monthly));

    private async Task<bool> IsMonthlyManualProtectedAsync(PlattsMonthlyManual rate)
    {
        var month = NormalizeMonth(rate.Month);
        return await _db.Contracts.AsNoTracking().AnyAsync(c =>
            c.ProductId == rate.ProductId
            && c.PricingMethod == PricingMethod.FormulaPlatts
            && c.PlattsPeriodType == Models.Entities.PlattsPeriodType.Monthly
            && c.BenchmarkCode == rate.BenchmarkCode
            && c.PlattsBasisMonth.HasValue
            && c.PlattsBasisMonth.Value.Year == month.Year
            && c.PlattsBasisMonth.Value.Month == month.Month);
    }

    private void RevalidateDailyForm(PlattsRatesPageViewModel model)
    {
        ModelState.Clear();
        TryValidateModel(model.DailyForm, nameof(model.DailyForm));
    }

    private void RevalidateMonthlyManualForm(PlattsRatesPageViewModel model)
    {
        ModelState.Clear();
        TryValidateModel(model.MonthlyManualForm, nameof(model.MonthlyManualForm));
    }

    private static string NormalizeTab(string? tab)
        => tab switch
        {
            PlattsRatesTabs.Monthly => PlattsRatesTabs.Monthly,
            PlattsRatesTabs.Manual => PlattsRatesTabs.Manual,
            _ => PlattsRatesTabs.Daily
        };

    private static DateTime NormalizeDate(DateTime value)
        => new(value.Year, value.Month, value.Day, 0, 0, 0, DateTimeKind.Utc);

    private static DateTime NormalizeMonth(DateTime value)
        => new(value.Year, value.Month, 1, 0, 0, 0, DateTimeKind.Utc);
}
