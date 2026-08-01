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
public class TrucksController : Controller
{
    private readonly ApplicationDbContext _db;
    private readonly IAuditService _audit;

    public TrucksController(ApplicationDbContext db, IAuditService audit)
    {
        _db = db;
        _audit = audit;
    }

    public async Task<IActionResult> Index(string? q, bool? isActive, int? selectedId = null, string? detailTab = null, int page = 1, [FromQuery(Name = "pageSize")] int? perPage = null)
    {
        var pageSize = ListPageSize.Resolve(perPage, 8);
        ViewData["PageSize"] = pageSize;
        ViewData["DefaultPageSize"] = 8;
        var search = string.IsNullOrWhiteSpace(q) ? null : q.Trim();
        var query = _db.Trucks.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
        {
            query = query.Where(t =>
                t.PlateNumber.Contains(search) ||
                (t.Owner != null && t.Owner.Contains(search)) ||
                (t.Notes != null && t.Notes.Contains(search)));
        }
        if (isActive.HasValue)
            query = query.Where(t => t.IsActive == isActive.Value);

        var totalCount = await query.CountAsync();
        var pageCount = Math.Max(1, (int)Math.Ceiling(totalCount / (double)pageSize));
        var currentPage = Math.Clamp(page, 1, pageCount);
        var trucks = await query
            .OrderBy(t => t.PlateNumber)
            .Skip((currentPage - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        ViewData["q"] = search;
        ViewData["isActive"] = isActive;
        ViewData["CurrentPage"] = currentPage;
        ViewData["PageCount"] = pageCount;
        ViewData["TotalCount"] = totalCount;

        return View(trucks);
    }

    public async Task<IActionResult> Details(int id)
    {
        var item = await _db.Trucks.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (item == null) return NotFound();
        ViewData["ResourceProfile"] = await TransportResourceProfileBuilder.ForTruckAsync(_db, item, "info");
        return View(item);
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    public IActionResult Create() => View(new Truck { IsActive = true });

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create([Bind("Id,PlateNumber,Owner,MaxLoadMt,IsActive,Notes")] Truck model, string? returnUrl = null)
    {
        Normalize(model);
        if (!ModelState.IsValid) return View(model);
        if (await _db.Trucks.AnyAsync(t => t.PlateNumber == model.PlateNumber))
        {
            ModelState.AddModelError(nameof(model.PlateNumber), "این شماره پلاک قبلاً ثبت شده است.");
            return View(model);
        }

        _db.Trucks.Add(model);
        await _db.SaveChangesAsync();
        await _audit.LogAndSaveAsync(
            nameof(Truck),
            model.Id,
            AuditAction.Insert,
            diff: AuditDiffFormatter.ForCreate(
                ("PlateNumber", model.PlateNumber),
                ("Owner", model.Owner),
                ("MaxLoadMt", model.MaxLoadMt),
                ("IsActive", model.IsActive),
                ("Notes", model.Notes)));

        TempData["ok"] = "موتر با موفقیت ثبت شد.";
        if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl)) return LocalRedirect(returnUrl);
        return RedirectToAction(nameof(Index));
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    public async Task<IActionResult> Edit(int id)
    {
        var item = await _db.Trucks.FirstOrDefaultAsync(x => x.Id == id);
        if (item == null) return NotFound();
        return View(item);
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ToggleActive(int id, string? returnUrl = null)
    {
        var item = await _db.Trucks.FirstOrDefaultAsync(x => x.Id == id);
        if (item is null) return NotFound();

        var wasActive = item.IsActive;
        item.IsActive = !item.IsActive;
        await _db.SaveChangesAsync();
        await _audit.LogAndSaveAsync(nameof(Truck), item.Id, AuditAction.Update,
            diff: AuditDiffFormatter.ForUpdate(("IsActive", wasActive, item.IsActive)));

        TempData["ok"] = item.IsActive ? "رکورد فعال شد." : "رکورد غیرفعال شد.";
        if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl)) return Redirect(returnUrl);
        return RedirectToAction(nameof(Index));
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, [Bind("Id,PlateNumber,Owner,MaxLoadMt,IsActive,Notes")] Truck model)
    {
        if (id != model.Id) return BadRequest();
        Normalize(model);
        if (!ModelState.IsValid) return View(model);

        var existing = await _db.Trucks.FirstOrDefaultAsync(x => x.Id == id);
        if (existing == null) return NotFound();

        if (!string.Equals(existing.PlateNumber, model.PlateNumber, StringComparison.OrdinalIgnoreCase)
            && await _db.Trucks.AnyAsync(t => t.PlateNumber == model.PlateNumber))
        {
            ModelState.AddModelError(nameof(model.PlateNumber), "این شماره پلاک قبلاً ثبت شده است.");
            return View(model);
        }

        var diff = AuditDiffFormatter.ForUpdate(
            ("PlateNumber", existing.PlateNumber, model.PlateNumber),
            ("Owner", existing.Owner, model.Owner),
            ("MaxLoadMt", existing.MaxLoadMt, model.MaxLoadMt),
            ("IsActive", existing.IsActive, model.IsActive),
            ("Notes", existing.Notes, model.Notes));

        existing.PlateNumber = model.PlateNumber;
        existing.Owner = model.Owner;
        existing.MaxLoadMt = model.MaxLoadMt;
        existing.IsActive = model.IsActive;
        existing.Notes = model.Notes;

        await _db.SaveChangesAsync();
        await _audit.LogAndSaveAsync(nameof(Truck), existing.Id, AuditAction.Update, diff: diff);

        TempData["ok"] = "ویرایش موتر با موفقیت انجام شد.";
        return RedirectToAction(nameof(Index));
    }

    private static void Normalize(Truck model)
    {
        model.PlateNumber = (model.PlateNumber ?? string.Empty).Trim().ToUpperInvariant();
        model.Owner = string.IsNullOrWhiteSpace(model.Owner) ? null : model.Owner.Trim();
        model.Notes = string.IsNullOrWhiteSpace(model.Notes) ? null : model.Notes.Trim();
    }
}
