using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Helpers;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Security;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Audit;
using PTGOilSystem.Web.Services.Time;

namespace PTGOilSystem.Web.Controllers;

[Authorize]
public class DailyFxRatesController : Controller
{
    private readonly ApplicationDbContext _db;
    private readonly IAuditService _audit;

    public DailyFxRatesController(ApplicationDbContext db, IAuditService audit)
    {
        _db = db;
        _audit = audit;
    }

    private async Task PopulateCurrenciesAsync(string? baseCurrency = null, string? quoteCurrency = null)
    {
        var items = await _db.Currencies
            .AsNoTracking()
            .Where(c => c.IsActive)
            .OrderBy(c => c.Code)
            .Select(c => new
            {
                c.Code,
                DisplayName = string.IsNullOrWhiteSpace(c.NamePersian)
                    ? $"{c.Code} - {c.Name}"
                    : $"{c.Code} - {c.NamePersian}"
            })
            .ToListAsync();

        ViewBag.BaseCurrencies = new SelectList(items, "Code", "DisplayName", SystemCurrency.Normalize(baseCurrency));
        ViewBag.QuoteCurrencies = new SelectList(items, "Code", "DisplayName", SystemCurrency.Normalize(quoteCurrency));
    }

    public async Task<IActionResult> Index(string? q, string? baseCcy, string? quoteCcy, DateTime? from, DateTime? to, int page = 1, [FromQuery(Name = "pageSize")] int? perPage = null)
    {
        var pageSize = ListPageSize.Resolve(perPage, 20);
        ViewData["PageSize"] = pageSize;
        ViewData["DefaultPageSize"] = 20;

        baseCcy = string.IsNullOrWhiteSpace(baseCcy) ? null : SystemCurrency.Normalize(baseCcy);
        quoteCcy = string.IsNullOrWhiteSpace(quoteCcy) ? null : SystemCurrency.Normalize(quoteCcy);

        var query = _db.DailyFxRates.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(q))
        {
            var search = q.Trim();
            var searchCode = search.ToUpperInvariant();
            query = query.Where(p =>
                p.BaseCurrency.Contains(searchCode)
                || p.QuoteCurrency.Contains(searchCode)
                || (p.Source != null && p.Source.Contains(search)));
        }
        if (!string.IsNullOrWhiteSpace(baseCcy)) query = query.Where(p => p.BaseCurrency == baseCcy);
        if (!string.IsNullOrWhiteSpace(quoteCcy)) query = query.Where(p => p.QuoteCurrency == quoteCcy);
        if (from.HasValue) query = query.Where(p => p.RateDate >= from.Value.Date);
        if (to.HasValue) query = query.Where(p => p.RateDate <= to.Value.Date);

        var totalCount = await query.CountAsync();
        var pageCount = page <= 0
            ? 1
            : Math.Max(1, (int)Math.Ceiling(totalCount / (double)pageSize));
        var currentPage = page <= 0 ? 1 : Math.Clamp(page, 1, pageCount);

        await PopulateCurrenciesAsync(baseCcy, quoteCcy);
        ViewData["q"] = q;
        ViewData["baseCcy"] = baseCcy;
        ViewData["quoteCcy"] = quoteCcy;
        ViewData["from"] = from.ToHtmlDateInput();
        ViewData["to"] = to.ToHtmlDateInput();
        ViewData["CurrentPage"] = currentPage;
        ViewData["PageCount"] = pageCount;
        ViewData["TotalCount"] = totalCount;

        var items = await (page <= 0
                ? query.OrderByDescending(p => p.RateDate).ThenByDescending(p => p.Id)
                : query
                    .OrderByDescending(p => p.RateDate)
                    .ThenByDescending(p => p.Id)
                    .Skip((currentPage - 1) * pageSize)
                    .Take(pageSize))
            .ToListAsync();

        return View(items);
    }

    public async Task<IActionResult> Details(int id)
    {
        var item = await _db.DailyFxRates.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (item == null) return NotFound();
        return View(item);
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    public async Task<IActionResult> Create()
    {
        var model = new DailyFxRate
        {
            BaseCurrency = SystemCurrency.BaseCurrencyCode,
            QuoteCurrency = "AFN",
            RateDate = AfghanistanBusinessClock.SystemToday
        };
        await PopulateCurrenciesAsync(model.BaseCurrency, model.QuoteCurrency);
        return View(model);
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(DailyFxRate model, string? returnUrl = null)
    {
        Normalize(model);
        await ValidateAsync(model);
        if (!ModelState.IsValid)
        {
            await PopulateCurrenciesAsync(model.BaseCurrency, model.QuoteCurrency);
            return View(model);
        }

        model.RateDate = model.RateDate.Date;
        _db.DailyFxRates.Add(model);
        await _db.SaveChangesAsync();
        await _audit.LogAndSaveAsync(
            nameof(DailyFxRate),
            model.Id,
            AuditAction.Insert,
            diff: AuditDiffFormatter.ForCreate(
                ("BaseCurrency", model.BaseCurrency),
                ("QuoteCurrency", model.QuoteCurrency),
                ("RateDate", model.RateDate),
                ("Rate", model.Rate),
                ("Source", model.Source)));
        TempData["ok"] = "نرخ ارز ثبت شد.";
        if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl)) return LocalRedirect(returnUrl);
        return RedirectToAction(nameof(Index));
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    public async Task<IActionResult> Edit(int id)
    {
        var item = await _db.DailyFxRates.FirstOrDefaultAsync(x => x.Id == id);
        if (item == null) return NotFound();
        await PopulateCurrenciesAsync(item.BaseCurrency, item.QuoteCurrency);
        return View(item);
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, DailyFxRate model)
    {
        if (id != model.Id) return BadRequest();

        Normalize(model);
        await ValidateAsync(model, id);
        if (!ModelState.IsValid)
        {
            await PopulateCurrenciesAsync(model.BaseCurrency, model.QuoteCurrency);
            return View(model);
        }

        var existing = await _db.DailyFxRates.FirstOrDefaultAsync(x => x.Id == id);
        if (existing == null) return NotFound();

        var diff = AuditDiffFormatter.ForUpdate(
            ("BaseCurrency", existing.BaseCurrency, model.BaseCurrency),
            ("QuoteCurrency", existing.QuoteCurrency, model.QuoteCurrency),
            ("RateDate", existing.RateDate, model.RateDate.Date),
            ("Rate", existing.Rate, model.Rate),
            ("Source", existing.Source, model.Source));

        existing.BaseCurrency = model.BaseCurrency;
        existing.QuoteCurrency = model.QuoteCurrency;
        existing.RateDate = model.RateDate.Date;
        existing.Rate = model.Rate;
        existing.Source = model.Source;

        await _db.SaveChangesAsync();
        await _audit.LogAndSaveAsync(nameof(DailyFxRate), existing.Id, AuditAction.Update, diff: diff);
        TempData["ok"] = "ویرایش انجام شد.";
        return RedirectToAction(nameof(Index));
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        var item = await _db.DailyFxRates.FirstOrDefaultAsync(x => x.Id == id);
        if (item == null) return NotFound();
        var diff = AuditDiffFormatter.ForDelete(
            ("BaseCurrency", item.BaseCurrency),
            ("QuoteCurrency", item.QuoteCurrency),
            ("RateDate", item.RateDate),
            ("Rate", item.Rate));
        _db.DailyFxRates.Remove(item);
        await _db.SaveChangesAsync();
        await _audit.LogAndSaveAsync(nameof(DailyFxRate), id, AuditAction.Delete, diff: diff);
        TempData["ok"] = "حذف شد.";
        return RedirectToAction(nameof(Index));
    }

    private async Task ValidateAsync(DailyFxRate model, int? currentId = null)
    {
        if (model.Rate <= 0m)
        {
            ModelState.AddModelError(nameof(model.Rate), "نرخ باید بزرگ‌تر از صفر باشد.");
        }

        if (string.IsNullOrWhiteSpace(model.BaseCurrency))
        {
            ModelState.AddModelError(nameof(model.BaseCurrency), "ارز پایه الزامی است.");
        }

        if (string.IsNullOrWhiteSpace(model.QuoteCurrency))
        {
            ModelState.AddModelError(nameof(model.QuoteCurrency), "ارز مقصد الزامی است.");
        }

        if (!string.IsNullOrWhiteSpace(model.BaseCurrency) &&
            !string.IsNullOrWhiteSpace(model.QuoteCurrency) &&
            string.Equals(model.BaseCurrency, model.QuoteCurrency, StringComparison.OrdinalIgnoreCase))
        {
            ModelState.AddModelError(nameof(model.QuoteCurrency), "ارز پایه و ارز مقصد نمی‌توانند یکسان باشند.");
        }

        var hasActiveCurrencies = await _db.Currencies.AsNoTracking().AnyAsync(c => c.IsActive);
        if (hasActiveCurrencies)
        {
            var validBaseCurrency = await _db.Currencies.AsNoTracking()
                .AnyAsync(c => c.IsActive && c.Code == model.BaseCurrency);
            if (!validBaseCurrency)
            {
                ModelState.AddModelError(nameof(model.BaseCurrency), "ارز پایه در master data وجود ندارد یا غیرفعال است.");
            }

            var validQuoteCurrency = await _db.Currencies.AsNoTracking()
                .AnyAsync(c => c.IsActive && c.Code == model.QuoteCurrency);
            if (!validQuoteCurrency)
            {
                ModelState.AddModelError(nameof(model.QuoteCurrency), "ارز مقصد در master data وجود ندارد یا غیرفعال است.");
            }
        }

        var normalizedDate = model.RateDate.Date;
        var duplicateExists = await _db.DailyFxRates.AnyAsync(p =>
            p.Id != (currentId ?? 0) &&
            p.BaseCurrency == model.BaseCurrency &&
            p.QuoteCurrency == model.QuoteCurrency &&
            p.RateDate == normalizedDate);
        if (duplicateExists)
        {
            ModelState.AddModelError(string.Empty, "برای این جفت ارز و تاریخ، نرخ قبلاً ثبت شده است.");
        }
    }

    private static void Normalize(DailyFxRate model)
    {
        model.BaseCurrency = SystemCurrency.Normalize(model.BaseCurrency);
        model.QuoteCurrency = SystemCurrency.Normalize(model.QuoteCurrency);
        model.RateDate = model.RateDate.Date;
        model.Source = string.IsNullOrWhiteSpace(model.Source) ? null : model.Source.Trim();
    }
}
