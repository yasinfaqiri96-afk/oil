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
public class VesselsController : Controller
{
    private readonly ApplicationDbContext _db;
    private readonly IAuditService _audit;

    public VesselsController(ApplicationDbContext db, IAuditService audit)
    {
        _db = db;
        _audit = audit;
    }

    public async Task<IActionResult> Index(string? q, string? flag, bool? isActive, int? selectedId = null, string? detailTab = null, int page = 1, [FromQuery(Name = "pageSize")] int? perPage = null)
    {
        var pageSize = ListPageSize.Resolve(perPage, 8);
        ViewData["PageSize"] = pageSize;
        ViewData["DefaultPageSize"] = 8;
        var search = string.IsNullOrWhiteSpace(q) ? null : q.Trim();
        var query = _db.Vessels.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
        {
            query = query.Where(v =>
                (v.Code != null && v.Code.Contains(search)) ||
                v.Name.Contains(search) ||
                (v.Imo != null && v.Imo.Contains(search)) ||
                (v.Flag != null && v.Flag.Contains(search)) ||
                (v.OwnerOrOperator != null && v.OwnerOrOperator.Contains(search)));
        }
        if (!string.IsNullOrWhiteSpace(flag))
            query = query.Where(v => v.Flag == flag);
        if (isActive.HasValue)
            query = query.Where(v => v.IsActive == isActive.Value);

        var totalCount = await query.CountAsync();
        var pageCount = Math.Max(1, (int)Math.Ceiling(totalCount / (double)pageSize));
        var currentPage = Math.Clamp(page, 1, pageCount);
        var vessels = await query
            .OrderBy(v => v.Name)
            .Skip((currentPage - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        ViewData["Flags"] = await _db.Vessels.AsNoTracking()
            .Where(v => v.Flag != null && v.Flag != "")
            .Select(v => v.Flag!)
            .Distinct()
            .OrderBy(x => x)
            .ToListAsync();
        ViewData["q"] = search;
        ViewData["flag"] = flag;
        ViewData["isActive"] = isActive;
        ViewData["CurrentPage"] = currentPage;
        ViewData["PageCount"] = pageCount;
        ViewData["TotalCount"] = totalCount;

        return View(vessels);
    }

    public async Task<IActionResult> Details(int id)
    {
        var item = await _db.Vessels.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (item == null) return NotFound();
        ViewData["ResourceProfile"] = await TransportResourceProfileBuilder.ForVesselAsync(_db, item, "info");
        return View(item);
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    public IActionResult Create() => View(new Vessel { IsActive = true });

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create([Bind("Id,Code,Name,Imo,Flag,OwnerOrOperator,IsActive,Notes")] Vessel model, string? returnUrl = null)
    {
        Normalize(model);
        if (!ModelState.IsValid) return View(model);
        if (await _db.Vessels.AnyAsync(v => v.Name == model.Name))
        {
            ModelState.AddModelError(nameof(model.Name), "این نام کشتی قبلاً ثبت شده است.");
            return View(model);
        }

        _db.Vessels.Add(model);
        await _db.SaveChangesAsync();
        await _audit.LogAndSaveAsync(
            nameof(Vessel),
            model.Id,
            AuditAction.Insert,
            diff: AuditDiffFormatter.ForCreate(
                ("Name", model.Name),
                ("Code", model.Code),
                ("Imo", model.Imo),
                ("Flag", model.Flag),
                ("OwnerOrOperator", model.OwnerOrOperator),
                ("IsActive", model.IsActive),
                ("Notes", model.Notes)));

        TempData["ok"] = "کشتی با موفقیت ثبت شد.";
        if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl)) return LocalRedirect(returnUrl);
        return RedirectToAction(nameof(Index));
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    public async Task<IActionResult> Edit(int id)
    {
        var item = await _db.Vessels.FirstOrDefaultAsync(x => x.Id == id);
        if (item == null) return NotFound();
        return View(item);
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ToggleActive(int id, string? returnUrl = null)
    {
        var item = await _db.Vessels.FirstOrDefaultAsync(x => x.Id == id);
        if (item is null) return NotFound();

        var wasActive = item.IsActive;
        item.IsActive = !item.IsActive;
        await _db.SaveChangesAsync();
        await _audit.LogAndSaveAsync(nameof(Vessel), item.Id, AuditAction.Update,
            diff: AuditDiffFormatter.ForUpdate(("IsActive", wasActive, item.IsActive)));

        TempData["ok"] = item.IsActive ? "رکورد فعال شد." : "رکورد غیرفعال شد.";
        if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl)) return Redirect(returnUrl);
        return RedirectToAction(nameof(Index));
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, [Bind("Id,Code,Name,Imo,Flag,OwnerOrOperator,IsActive,Notes")] Vessel model)
    {
        if (id != model.Id) return BadRequest();
        Normalize(model);
        if (!ModelState.IsValid) return View(model);

        var existing = await _db.Vessels.FirstOrDefaultAsync(x => x.Id == id);
        if (existing == null) return NotFound();

        if (!string.Equals(existing.Name, model.Name, StringComparison.OrdinalIgnoreCase)
            && await _db.Vessels.AnyAsync(v => v.Name == model.Name))
        {
            ModelState.AddModelError(nameof(model.Name), "این نام کشتی قبلاً ثبت شده است.");
            return View(model);
        }

        var diff = AuditDiffFormatter.ForUpdate(
            ("Name", existing.Name, model.Name),
            ("Code", existing.Code, model.Code),
            ("Imo", existing.Imo, model.Imo),
            ("Flag", existing.Flag, model.Flag),
            ("OwnerOrOperator", existing.OwnerOrOperator, model.OwnerOrOperator),
            ("IsActive", existing.IsActive, model.IsActive),
            ("Notes", existing.Notes, model.Notes));

        existing.Code = model.Code;
        existing.Name = model.Name;
        existing.Imo = model.Imo;
        existing.Flag = model.Flag;
        existing.OwnerOrOperator = model.OwnerOrOperator;
        existing.IsActive = model.IsActive;
        existing.Notes = model.Notes;

        await _db.SaveChangesAsync();
        await _audit.LogAndSaveAsync(nameof(Vessel), existing.Id, AuditAction.Update, diff: diff);

        TempData["ok"] = "ویرایش کشتی با موفقیت انجام شد.";
        return RedirectToAction(nameof(Index));
    }

    private static void Normalize(Vessel model)
    {
        model.Code = string.IsNullOrWhiteSpace(model.Code) ? null : model.Code.Trim().ToUpperInvariant();
        model.Name = (model.Name ?? string.Empty).Trim();
        model.Imo = string.IsNullOrWhiteSpace(model.Imo) ? null : model.Imo.Trim();
        model.Flag = string.IsNullOrWhiteSpace(model.Flag) ? null : model.Flag.Trim();
        model.OwnerOrOperator = string.IsNullOrWhiteSpace(model.OwnerOrOperator) ? null : model.OwnerOrOperator.Trim();
        model.Notes = string.IsNullOrWhiteSpace(model.Notes) ? null : model.Notes.Trim();
    }
}
