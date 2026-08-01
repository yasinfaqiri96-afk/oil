using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
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
public class ProductsController : Controller
{
    private readonly ApplicationDbContext _db;
    private readonly IAuditService _audit;
    private readonly MasterDataDeleteSafetyService _deleteSafety;

    public ProductsController(
        ApplicationDbContext db,
        IAuditService audit,
        MasterDataDeleteSafetyService deleteSafety)
    {
        _db = db;
        _audit = audit;
        _deleteSafety = deleteSafety;
    }

    private async Task PopulateUnitsAsync(Product? current = null)
    {
        var units = await _db.Units
                .AsNoTracking()
                .Where(u => u.IsActive)
                .OrderBy(u => u.Code)
                .Select(u => new
                {
                    u.Id,
                    DisplayName = string.IsNullOrWhiteSpace(u.Symbol)
                        ? $"{u.Code} - {u.Name}"
                        : $"{u.Code} - {u.Name} ({u.Symbol})"
                })
                .ToListAsync();

        ViewBag.Units = new SelectList(
            units,
            "Id",
            "DisplayName",
            current?.UnitId);

        ViewBag.SecondaryUnits = new SelectList(
            units,
            "Id",
            "DisplayName",
            current?.SecondaryUnitId);
    }

    public async Task<IActionResult> Index(string? q, int? unitId, bool? isActive, int page = 1, [FromQuery(Name = "pageSize")] int? perPage = null)
    {
        var pageSize = ListPageSize.Resolve(perPage, 12);
        ViewData["PageSize"] = pageSize;
        ViewData["DefaultPageSize"] = 12;

        var query = _db.Products
            .Include(p => p.Unit)
            .Include(p => p.SecondaryUnit)
            .AsNoTracking();
        if (!string.IsNullOrWhiteSpace(q))
            query = query.Where(p => p.Code.Contains(q) || p.Name.Contains(q) || (p.NamePersian != null && p.NamePersian.Contains(q)) || (p.Category != null && p.Category.Contains(q)));
        if (unitId.HasValue)
            query = query.Where(p => p.UnitId == unitId.Value);
        if (isActive.HasValue)
            query = query.Where(p => p.IsActive == isActive.Value);

        var totalCount = await query.CountAsync();
        var pageCount = Math.Max(1, (int)Math.Ceiling(totalCount / (double)pageSize));
        page = Math.Clamp(page, 1, pageCount);

        ViewData["q"] = q;
        ViewData["unitId"] = unitId;
        ViewData["isActive"] = isActive;
        ViewData["CurrentPage"] = page;
        ViewData["PageCount"] = pageCount;
        ViewData["TotalCount"] = totalCount;

        await PopulateUnitsAsync();
        return View(await query
            .OrderBy(p => p.Code)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync());
    }

    public async Task<IActionResult> Details(int id)
    {
        var item = await _db.Products
            .Include(p => p.Unit)
            .Include(p => p.SecondaryUnit)
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id);
        if (item == null) return NotFound();
        return View(item);
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    public async Task<IActionResult> Create()
    {
        var model = new Product();
        await PopulateUnitsAsync(model);
        return View(model);
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create([Bind("Id,Code,Name,NamePersian,UnitId,SecondaryUnitId,UnitOfMeasure,Category,SecondaryUnitConversionNote,IsActive,Notes")] Product model, string? returnUrl = null)
    {
        Normalize(model);
        var unit = await ValidateUnitAsync(model);
        await ValidateSecondaryUnitAsync(model);

        if (!ModelState.IsValid)
        {
            await PopulateUnitsAsync(model);
            return View(model);
        }

        if (await _db.Products.AnyAsync(p => p.Code == model.Code))
        {
            ModelState.AddModelError(nameof(model.Code), "این کد قبلاً ثبت شده است.");
            await PopulateUnitsAsync(model);
            return View(model);
        }

        ApplyUnitSnapshot(model, unit!);
        _db.Products.Add(model);
        await _db.SaveChangesAsync();
        await _audit.LogAndSaveAsync(
            nameof(Product),
            model.Id,
            AuditAction.Insert,
            diff: AuditDiffFormatter.ForCreate(
                ("Code", model.Code),
                ("Name", model.Name),
                ("NamePersian", model.NamePersian),
                ("UnitId", model.UnitId),
                ("UnitOfMeasure", model.UnitOfMeasure),
                ("SecondaryUnitId", model.SecondaryUnitId),
                ("SecondaryUnitConversionNote", model.SecondaryUnitConversionNote),
                ("Category", model.Category),
                ("IsActive", model.IsActive),
                ("Notes", model.Notes)));
        TempData["ok"] = "کالا با موفقیت ثبت شد.";
        if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl)) return LocalRedirect(returnUrl);
        return RedirectToAction(nameof(Index));
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    public async Task<IActionResult> Edit(int id)
    {
        var item = await _db.Products.FirstOrDefaultAsync(x => x.Id == id);
        if (item == null) return NotFound();
        await PopulateUnitsAsync(item);
        return View(item);
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, [Bind("Id,Code,Name,NamePersian,UnitId,SecondaryUnitId,UnitOfMeasure,Category,SecondaryUnitConversionNote,IsActive,Notes")] Product model)
    {
        if (id != model.Id) return BadRequest();

        Normalize(model);
        var unit = await ValidateUnitAsync(model);
        await ValidateSecondaryUnitAsync(model);

        if (!ModelState.IsValid)
        {
            await PopulateUnitsAsync(model);
            return View(model);
        }

        var existing = await _db.Products.FirstOrDefaultAsync(x => x.Id == id);
        if (existing == null) return NotFound();
        if (existing.Code != model.Code && await _db.Products.AnyAsync(p => p.Code == model.Code))
        {
            ModelState.AddModelError(nameof(model.Code), "این کد قبلاً ثبت شده است.");
            await PopulateUnitsAsync(model);
            return View(model);
        }

        var previousCode = existing.Code;
        var previousName = existing.Name;
        var previousNamePersian = existing.NamePersian;
        var previousUnitId = existing.UnitId;
        var previousUnitOfMeasure = existing.UnitOfMeasure;
        var previousSecondaryUnitId = existing.SecondaryUnitId;
        var previousSecondaryUnitConversionNote = existing.SecondaryUnitConversionNote;
        var previousCategory = existing.Category;
        var previousIsActive = existing.IsActive;
        var previousNotes = existing.Notes;

        existing.Code = model.Code;
        existing.Name = model.Name;
        existing.NamePersian = model.NamePersian;
        existing.UnitId = model.UnitId;
        ApplyUnitSnapshot(existing, unit!);
        existing.SecondaryUnitId = model.SecondaryUnitId;
        existing.SecondaryUnitConversionNote = model.SecondaryUnitConversionNote;
        existing.Category = model.Category;
        existing.IsActive = model.IsActive;
        existing.Notes = model.Notes;

        var diff = AuditDiffFormatter.ForUpdate(
            ("Code", previousCode, existing.Code),
            ("Name", previousName, existing.Name),
            ("NamePersian", previousNamePersian, existing.NamePersian),
            ("UnitId", previousUnitId, existing.UnitId),
            ("UnitOfMeasure", previousUnitOfMeasure, existing.UnitOfMeasure),
            ("SecondaryUnitId", previousSecondaryUnitId, existing.SecondaryUnitId),
            ("SecondaryUnitConversionNote", previousSecondaryUnitConversionNote, existing.SecondaryUnitConversionNote),
            ("Category", previousCategory, existing.Category),
            ("IsActive", previousIsActive, existing.IsActive),
            ("Notes", previousNotes, existing.Notes));

        await _db.SaveChangesAsync();
        await _audit.LogAndSaveAsync(nameof(Product), existing.Id, AuditAction.Update, diff: diff);
        TempData["ok"] = "ویرایش با موفقیت انجام شد.";
        return RedirectToAction(nameof(Index));
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        var item = await _db.Products.FirstOrDefaultAsync(x => x.Id == id);
        if (item == null) return NotFound();

        var safety = await _deleteSafety.EvaluateProductAsync(id);
        if (!safety.CanDelete)
        {
            if (safety.ArchiveInsteadOfDelete && item.IsActive)
            {
                var archiveDiff = $"ArchiveInsteadOfDelete: {safety.DependencySummary} | "
                    + AuditDiffFormatter.ForUpdate(("IsActive", item.IsActive, false));
                item.IsActive = false;
                await _db.SaveChangesAsync();
                await _audit.LogAndSaveAsync(nameof(Product), item.Id, AuditAction.Update, diff: archiveDiff);
                TempData["ok"] = safety.BuildArchivedMessage("کالا");
                return RedirectToAction(nameof(Index));
            }

            TempData["err"] = safety.ArchiveInsteadOfDelete
                ? $"{safety.BuildBlockedMessage("کالا")} این رکورد قبلاً غیرفعال شده است."
                : safety.BuildBlockedMessage("کالا");
            return RedirectToAction(nameof(Index));
        }

        var deleteDiff = AuditDiffFormatter.ForDelete(
            ("Code", item.Code),
            ("Name", item.Name),
            ("UnitOfMeasure", item.UnitOfMeasure));
        _db.Products.Remove(item);
        await _db.SaveChangesAsync();
        await _audit.LogAndSaveAsync(nameof(Product), id, AuditAction.Delete, diff: deleteDiff);
        TempData["ok"] = "کالا حذف شد.";
        return RedirectToAction(nameof(Index));
    }

    private async Task<Unit?> ValidateUnitAsync(Product model)
    {
        if (!model.UnitId.HasValue)
        {
            ModelState.AddModelError(nameof(model.UnitId), "انتخاب واحد الزامی است.");
            return null;
        }

        var unit = await _db.Units
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == model.UnitId.Value && u.IsActive);
        if (unit is null)
        {
            ModelState.AddModelError(nameof(model.UnitId), "واحد انتخاب‌شده معتبر نیست.");
        }

        return unit;
    }

    private async Task ValidateSecondaryUnitAsync(Product model)
    {
        if (!model.SecondaryUnitId.HasValue)
            return;

        var exists = await _db.Units
            .AsNoTracking()
            .AnyAsync(u => u.Id == model.SecondaryUnitId.Value && u.IsActive);

        if (!exists)
            ModelState.AddModelError(nameof(model.SecondaryUnitId), "واحد ثانویه انتخاب‌شده معتبر نیست.");
    }

    private static void ApplyUnitSnapshot(Product model, Unit unit)
    {
        model.UnitId = unit.Id;
        model.UnitOfMeasure = string.IsNullOrWhiteSpace(unit.Symbol)
            ? unit.Code
            : unit.Symbol.Trim();
    }

    private static void Normalize(Product model)
    {
        model.Code = (model.Code ?? string.Empty).Trim().ToUpperInvariant();
        model.Name = (model.Name ?? string.Empty).Trim();
        model.NamePersian = string.IsNullOrWhiteSpace(model.NamePersian) ? null : model.NamePersian.Trim();
        model.Category = string.IsNullOrWhiteSpace(model.Category) ? null : model.Category.Trim();
        model.SecondaryUnitConversionNote = string.IsNullOrWhiteSpace(model.SecondaryUnitConversionNote) ? null : model.SecondaryUnitConversionNote.Trim();
        model.Notes = string.IsNullOrWhiteSpace(model.Notes) ? null : model.Notes.Trim();
    }
}
