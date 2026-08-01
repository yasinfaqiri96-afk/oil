using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Security;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Audit;
using PTGOilSystem.Web.Services.DeleteSafety;
using PTGOilSystem.Web.Helpers;

namespace PTGOilSystem.Web.Controllers;

[Authorize]
public class TerminalsController : Controller
{
    private readonly ApplicationDbContext _db;
    private readonly IAuditService _audit;
    private readonly MasterDataDeleteSafetyService _deleteSafety;

    public TerminalsController(
        ApplicationDbContext db,
        IAuditService audit,
        MasterDataDeleteSafetyService deleteSafety)
    {
        _db = db;
        _audit = audit;
        _deleteSafety = deleteSafety;
    }

    public async Task<IActionResult> Index(string? q, bool? isActive, int page = 1, [FromQuery(Name = "pageSize")] int? perPage = null)
    {
        var pageSize = ListPageSize.Resolve(perPage, 12);
        ViewData["PageSize"] = pageSize;
        ViewData["DefaultPageSize"] = 12;

        var query = _db.Terminals.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(q))
            query = query.Where(p => p.Code.Contains(q) || p.Name.Contains(q) || (p.Location != null && p.Location.Contains(q)) || (p.Notes != null && p.Notes.Contains(q)));
        if (isActive.HasValue)
            query = query.Where(p => p.IsActive == isActive.Value);

        var totalCount = await query.CountAsync();
        var pageCount = Math.Max(1, (int)Math.Ceiling(totalCount / (double)pageSize));
        page = Math.Clamp(page, 1, pageCount);

        ViewData["q"] = q;
        ViewData["isActive"] = isActive;
        ViewData["CurrentPage"] = page;
        ViewData["PageCount"] = pageCount;
        ViewData["TotalCount"] = totalCount;

        return View(await query
            .OrderBy(p => p.Code)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync());
    }

    public async Task<IActionResult> Details(int id)
    {
        var item = await _db.Terminals.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (item == null) return NotFound();
        return View(item);
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    public IActionResult Create() => View(new Terminal());

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create([Bind("Id,Code,Name,Location,IsActive,Notes")] Terminal model, string? returnUrl = null)
    {
        Normalize(model);
        if (!ModelState.IsValid) return View(model);
        if (await _db.Terminals.AnyAsync(p => p.Code == model.Code))
        {
            ModelState.AddModelError(nameof(model.Code), "این کد قبلاً ثبت شده است.");
            return View(model);
        }
        model.CreatedAtUtc = DateTime.UtcNow;
        _db.Terminals.Add(model);
        await _db.SaveChangesAsync();
        TempData["ok"] = "ترمینال با موفقیت ثبت شد.";
        if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl)) return LocalRedirect(returnUrl);
        return RedirectToAction(nameof(Index));
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    public async Task<IActionResult> Edit(int id)
    {
        var item = await _db.Terminals.FirstOrDefaultAsync(x => x.Id == id);
        if (item == null) return NotFound();
        return View(item);
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, [Bind("Id,Code,Name,Location,IsActive,Notes")] Terminal model)
    {
        if (id != model.Id) return BadRequest();
        Normalize(model);
        if (!ModelState.IsValid) return View(model);
        var existing = await _db.Terminals.FirstOrDefaultAsync(x => x.Id == id);
        if (existing == null) return NotFound();
        if (existing.Code != model.Code && await _db.Terminals.AnyAsync(p => p.Code == model.Code))
        {
            ModelState.AddModelError(nameof(model.Code), "این کد قبلاً ثبت شده است.");
            return View(model);
        }
        existing.Code = model.Code;
        existing.Name = model.Name;
        existing.Location = model.Location;
        existing.IsActive = model.IsActive;
        existing.Notes = model.Notes;
        existing.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        TempData["ok"] = "ویرایش با موفقیت انجام شد.";
        return RedirectToAction(nameof(Index));
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        var item = await _db.Terminals.FirstOrDefaultAsync(x => x.Id == id);
        if (item == null) return NotFound();

        var safety = await _deleteSafety.EvaluateTerminalAsync(id);
        if (!safety.CanDelete)
        {
            if (safety.ArchiveInsteadOfDelete && item.IsActive)
            {
                var archiveDiff = $"ArchiveInsteadOfDelete: {safety.DependencySummary} | "
                    + AuditDiffFormatter.ForUpdate(("IsActive", item.IsActive, false));
                item.IsActive = false;
                await _db.SaveChangesAsync();
                await _audit.LogAndSaveAsync(nameof(Terminal), item.Id, AuditAction.Update, diff: archiveDiff);
                TempData["ok"] = safety.BuildArchivedMessage("ترمینال");
                return RedirectToAction(nameof(Index));
            }

            TempData["err"] = safety.ArchiveInsteadOfDelete
                ? $"{safety.BuildBlockedMessage("ترمینال")} این رکورد قبلاً غیرفعال شده است."
                : safety.BuildBlockedMessage("ترمینال");
            return RedirectToAction(nameof(Index));
        }

        var deleteDiff = AuditDiffFormatter.ForDelete(
            ("Code", item.Code),
            ("Name", item.Name),
            ("Location", item.Location));
        _db.Terminals.Remove(item);
        await _db.SaveChangesAsync();
        await _audit.LogAndSaveAsync(nameof(Terminal), item.Id, AuditAction.Delete, diff: deleteDiff);
        TempData["ok"] = "ترمینال حذف شد.";
        return RedirectToAction(nameof(Index));
    }

    private static void Normalize(Terminal model)
    {
        model.Code = (model.Code ?? string.Empty).Trim().ToUpperInvariant();
        model.Name = (model.Name ?? string.Empty).Trim();
        model.Location = string.IsNullOrWhiteSpace(model.Location) ? null : model.Location.Trim();
        model.Notes = string.IsNullOrWhiteSpace(model.Notes) ? null : model.Notes.Trim();
    }
}
