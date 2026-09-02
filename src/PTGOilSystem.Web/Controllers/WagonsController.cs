using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Security;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Audit;
using PTGOilSystem.Web.Helpers;

namespace PTGOilSystem.Web.Controllers;

[Authorize]
public class WagonsController : Controller
{
    private readonly ApplicationDbContext _db;
    private readonly IAuditService _audit;

    public WagonsController(ApplicationDbContext db, IAuditService audit)
    {
        _db = db;
        _audit = audit;
    }

    public async Task<IActionResult> Index(string? q, string? wagonType, bool? isActive, int? selectedId = null, string? detailTab = null, int page = 1, [FromQuery(Name = "pageSize")] int? perPage = null)
    {
        var pageSize = ListPageSize.Resolve(perPage, 8);
        ViewData["PageSize"] = pageSize;
        ViewData["DefaultPageSize"] = 8;
        var search = string.IsNullOrWhiteSpace(q) ? null : q.Trim();
        var query = _db.Wagons.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
        {
            // PTG canonical search — کلیدِ canonical به شرطِ قبلی اضافه می‌شود، جایگزینِ آن نمی‌شود:
            // هیچ نتیجه‌ای از دست نمی‌رود و «یوسف» سطرِ «يوسف» را هم پیدا می‌کند.
            // SearchKey خالی یعنی سطرِ پیش از Backfill؛ همان شرطِ قبلی هنوز آن را می‌یابد.
            var canonicalTerm = AfghanTextNormalizer.NormalizeForSearch(search);
            query = query.Where(w =>
                (w.SearchKey != null && w.SearchKey.Contains(canonicalTerm)) ||
                w.WagonNumber.Contains(search) ||
                (w.WagonType != null && w.WagonType.Contains(search)) ||
                (w.Owner != null && w.Owner.Contains(search)) ||
                (w.Notes != null && w.Notes.Contains(search)));
        }
        if (!string.IsNullOrWhiteSpace(wagonType))
            query = query.Where(w => w.WagonType == wagonType);
        if (isActive.HasValue)
            query = query.Where(w => w.IsActive == isActive.Value);

        var totalCount = await query.CountAsync();
        var pageCount = Math.Max(1, (int)Math.Ceiling(totalCount / (double)pageSize));
        var currentPage = Math.Clamp(page, 1, pageCount);
        var wagons = await query
            .OrderBy(w => w.WagonNumber)
            .Skip((currentPage - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        ViewData["WagonTypes"] = await _db.Wagons.AsNoTracking()
            .Where(w => w.WagonType != null && w.WagonType != "")
            .Select(w => w.WagonType!)
            .Distinct()
            .OrderBy(x => x)
            .ToListAsync();
        ViewData["q"] = search;
        ViewData["wagonType"] = wagonType;
        ViewData["isActive"] = isActive;
        ViewData["CurrentPage"] = currentPage;
        ViewData["PageCount"] = pageCount;
        ViewData["TotalCount"] = totalCount;

        return View(wagons);
    }

    public async Task<IActionResult> Details(int id)
    {
        var item = await _db.Wagons.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (item == null) return NotFound();
        ViewData["ResourceProfile"] = await TransportResourceProfileBuilder.ForWagonAsync(_db, item, "info");
        return View(item);
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    public IActionResult Create() => View(new Wagon { IsActive = true });

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create([Bind("Id,WagonNumber,WagonType,Owner,CapacityMt,IsActive,Notes")] Wagon model, string? returnUrl = null)
    {
        Normalize(model);
        if (!ModelState.IsValid) return View(model);
        if (await _db.Wagons.AnyAsync(w => w.WagonNumber == model.WagonNumber))
        {
            ModelState.AddModelError(nameof(model.WagonNumber), "این شماره واگون قبلاً ثبت شده است.");
            return View(model);
        }

        _db.Wagons.Add(model);
        await _db.SaveChangesAsync();
        await _audit.LogAndSaveAsync(
            nameof(Wagon),
            model.Id,
            AuditAction.Insert,
            diff: AuditDiffFormatter.ForCreate(
                ("WagonNumber", model.WagonNumber),
                ("WagonType", model.WagonType),
                ("Owner", model.Owner),
                ("CapacityMt", model.CapacityMt),
                ("IsActive", model.IsActive),
                ("Notes", model.Notes)));

        TempData["ok"] = "واگون با موفقیت ثبت شد.";
        if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl)) return LocalRedirect(returnUrl);
        return RedirectToAction(nameof(Index));
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    public async Task<IActionResult> Edit(int id)
    {
        var item = await _db.Wagons.FirstOrDefaultAsync(x => x.Id == id);
        if (item == null) return NotFound();
        return View(item);
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ToggleActive(int id, string? returnUrl = null)
    {
        var item = await _db.Wagons.FirstOrDefaultAsync(x => x.Id == id);
        if (item is null) return NotFound();

        var wasActive = item.IsActive;
        item.IsActive = !item.IsActive;
        await _db.SaveChangesAsync();
        await _audit.LogAndSaveAsync(nameof(Wagon), item.Id, AuditAction.Update,
            diff: AuditDiffFormatter.ForUpdate(("IsActive", wasActive, item.IsActive)));

        TempData["ok"] = item.IsActive ? "رکورد فعال شد." : "رکورد غیرفعال شد.";
        if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl)) return Redirect(returnUrl);
        return RedirectToAction(nameof(Index));
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, [Bind("Id,WagonNumber,WagonType,Owner,CapacityMt,IsActive,Notes")] Wagon model)
    {
        if (id != model.Id) return BadRequest();
        Normalize(model);
        if (!ModelState.IsValid) return View(model);

        var existing = await _db.Wagons.FirstOrDefaultAsync(x => x.Id == id);
        if (existing == null) return NotFound();

        if (!string.Equals(existing.WagonNumber, model.WagonNumber, StringComparison.OrdinalIgnoreCase)
            && await _db.Wagons.AnyAsync(w => w.WagonNumber == model.WagonNumber))
        {
            ModelState.AddModelError(nameof(model.WagonNumber), "این شماره واگون قبلاً ثبت شده است.");
            return View(model);
        }

        var diff = AuditDiffFormatter.ForUpdate(
            ("WagonNumber", existing.WagonNumber, model.WagonNumber),
            ("WagonType", existing.WagonType, model.WagonType),
            ("Owner", existing.Owner, model.Owner),
            ("CapacityMt", existing.CapacityMt, model.CapacityMt),
            ("IsActive", existing.IsActive, model.IsActive),
            ("Notes", existing.Notes, model.Notes));

        existing.WagonNumber = model.WagonNumber;
        existing.WagonType = model.WagonType;
        existing.Owner = model.Owner;
        existing.CapacityMt = model.CapacityMt;
        existing.IsActive = model.IsActive;
        existing.Notes = model.Notes;

        await _db.SaveChangesAsync();
        await _audit.LogAndSaveAsync(nameof(Wagon), existing.Id, AuditAction.Update, diff: diff);

        TempData["ok"] = "ویرایش واگون با موفقیت انجام شد.";
        return RedirectToAction(nameof(Index));
    }

    private static void Normalize(Wagon model)
    {
        model.WagonNumber = (model.WagonNumber ?? string.Empty).Trim().ToUpperInvariant();
        model.WagonType = string.IsNullOrWhiteSpace(model.WagonType) ? null : model.WagonType.Trim();
        model.Owner = string.IsNullOrWhiteSpace(model.Owner) ? null : model.Owner.Trim();
        model.Notes = string.IsNullOrWhiteSpace(model.Notes) ? null : model.Notes.Trim();
    }
}
