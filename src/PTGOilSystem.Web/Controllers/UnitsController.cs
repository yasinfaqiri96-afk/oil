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
public class UnitsController : Controller
{
    private readonly ApplicationDbContext _db;
    private readonly IAuditService _audit;
    private readonly MasterDataDeleteSafetyService _deleteSafety;

    public UnitsController(
        ApplicationDbContext db,
        IAuditService audit,
        MasterDataDeleteSafetyService deleteSafety)
    {
        _db = db;
        _audit = audit;
        _deleteSafety = deleteSafety;
    }

    public async Task<IActionResult> Index(string? q, string? unitType, bool? isActive, int page = 1, [FromQuery(Name = "pageSize")] int? perPage = null)
    {
        var pageSize = ListPageSize.Resolve(perPage, 12);
        ViewData["PageSize"] = pageSize;
        ViewData["DefaultPageSize"] = 12;

        var query = _db.Units.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim();
            query = query.Where(u =>
                u.Code.Contains(term) ||
                u.Name.Contains(term) ||
                (u.NamePersian != null && u.NamePersian.Contains(term)) ||
                (u.Symbol != null && u.Symbol.Contains(term)) ||
                (u.UnitType != null && u.UnitType.Contains(term)) ||
                (u.BaseUnitCode != null && u.BaseUnitCode.Contains(term)));
        }
        if (!string.IsNullOrWhiteSpace(unitType))
            query = query.Where(u => u.UnitType == unitType);
        if (isActive.HasValue)
            query = query.Where(u => u.IsActive == isActive.Value);

        var totalCount = await query.CountAsync();
        var pageCount = Math.Max(1, (int)Math.Ceiling(totalCount / (double)pageSize));
        page = Math.Clamp(page, 1, pageCount);

        ViewData["UnitTypes"] = await _db.Units.AsNoTracking()
            .Where(u => u.UnitType != null && u.UnitType != "")
            .Select(u => u.UnitType!)
            .Distinct()
            .OrderBy(x => x)
            .ToListAsync();
        ViewData["q"] = q;
        ViewData["unitType"] = unitType;
        ViewData["isActive"] = isActive;
        ViewData["CurrentPage"] = page;
        ViewData["PageCount"] = pageCount;
        ViewData["TotalCount"] = totalCount;

        return View(await query
            .OrderBy(u => u.Code)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync());
    }

    public async Task<IActionResult> Details(int id)
    {
        var item = await _db.Units.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        return item is null ? NotFound() : View(item);
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    public IActionResult Create()
        => View(new Unit { Code = "MT", Name = "Metric Ton", Symbol = "MT", IsActive = true });

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create([Bind("Id,Code,Name,NamePersian,Symbol,UnitType,BaseUnitCode,ConversionFactorToBase,IsBaseUnit,IsActive,Notes")] Unit model, string? returnUrl = null)
    {
        Normalize(model);
        await ValidateAsync(model, model.Id);

        if (!ModelState.IsValid)
            return View(model);

        _db.Units.Add(model);
        await _db.SaveChangesAsync();
        await _audit.LogAndSaveAsync(
            nameof(Unit),
            model.Id,
            AuditAction.Insert,
            diff: AuditDiffFormatter.ForCreate(
                ("Code", model.Code),
                ("Name", model.Name),
                ("NamePersian", model.NamePersian),
                ("Symbol", model.Symbol),
                ("UnitType", model.UnitType),
                ("BaseUnitCode", model.BaseUnitCode),
                ("ConversionFactorToBase", model.ConversionFactorToBase),
                ("IsBaseUnit", model.IsBaseUnit),
                ("IsActive", model.IsActive),
                ("Notes", model.Notes)));

        TempData["ok"] = "واحد با موفقیت ثبت شد.";
        if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl)) return LocalRedirect(returnUrl);
        return RedirectToAction(nameof(Index));
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    public async Task<IActionResult> Edit(int id)
    {
        var item = await _db.Units.FirstOrDefaultAsync(x => x.Id == id);
        return item is null ? NotFound() : View(item);
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, [Bind("Id,Code,Name,NamePersian,Symbol,UnitType,BaseUnitCode,ConversionFactorToBase,IsBaseUnit,IsActive,Notes")] Unit model)
    {
        if (id != model.Id)
            return BadRequest();

        Normalize(model);
        await ValidateAsync(model, id);

        if (!ModelState.IsValid)
            return View(model);

        var existing = await _db.Units.FirstOrDefaultAsync(x => x.Id == id);
        if (existing is null)
            return NotFound();

        var diff = AuditDiffFormatter.ForUpdate(
            ("Code", existing.Code, model.Code),
            ("Name", existing.Name, model.Name),
            ("NamePersian", existing.NamePersian, model.NamePersian),
            ("Symbol", existing.Symbol, model.Symbol),
            ("UnitType", existing.UnitType, model.UnitType),
            ("BaseUnitCode", existing.BaseUnitCode, model.BaseUnitCode),
            ("ConversionFactorToBase", existing.ConversionFactorToBase, model.ConversionFactorToBase),
            ("IsBaseUnit", existing.IsBaseUnit, model.IsBaseUnit),
            ("IsActive", existing.IsActive, model.IsActive),
            ("Notes", existing.Notes, model.Notes));

        existing.Code = model.Code;
        existing.Name = model.Name;
        existing.NamePersian = model.NamePersian;
        existing.Symbol = model.Symbol;
        existing.UnitType = model.UnitType;
        existing.BaseUnitCode = model.BaseUnitCode;
        existing.ConversionFactorToBase = model.ConversionFactorToBase;
        existing.IsBaseUnit = model.IsBaseUnit;
        existing.IsActive = model.IsActive;
        existing.Notes = model.Notes;

        await _db.SaveChangesAsync();
        await _audit.LogAndSaveAsync(nameof(Unit), existing.Id, AuditAction.Update, diff: diff);

        TempData["ok"] = "ویرایش واحد انجام شد.";
        return RedirectToAction(nameof(Index));
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        var item = await _db.Units.FirstOrDefaultAsync(x => x.Id == id);
        if (item is null)
            return NotFound();

        var safety = await _deleteSafety.EvaluateUnitAsync(id);
        if (!safety.CanDelete)
        {
            if (safety.ArchiveInsteadOfDelete && item.IsActive)
            {
                var archiveDiff = $"ArchiveInsteadOfDelete: {safety.DependencySummary} | "
                    + AuditDiffFormatter.ForUpdate(("IsActive", item.IsActive, false));
                item.IsActive = false;
                await _db.SaveChangesAsync();
                await _audit.LogAndSaveAsync(nameof(Unit), item.Id, AuditAction.Update, diff: archiveDiff);
                TempData["ok"] = safety.BuildArchivedMessage("واحد");
                return RedirectToAction(nameof(Index));
            }

            TempData["err"] = safety.ArchiveInsteadOfDelete
                ? $"{safety.BuildBlockedMessage("واحد")} این رکورد قبلاً غیرفعال شده است."
                : safety.BuildBlockedMessage("واحد");
            return RedirectToAction(nameof(Index));
        }

        var diff = AuditDiffFormatter.ForDelete(
            ("Code", item.Code),
            ("Name", item.Name),
            ("Symbol", item.Symbol));
        _db.Units.Remove(item);
        await _db.SaveChangesAsync();
        await _audit.LogAndSaveAsync(nameof(Unit), id, AuditAction.Delete, diff: diff);
        TempData["ok"] = "واحد حذف شد.";
        return RedirectToAction(nameof(Index));
    }

    private async Task ValidateAsync(Unit model, int currentId)
    {
        if (string.IsNullOrWhiteSpace(model.Code))
            ModelState.AddModelError(nameof(model.Code), "کد واحد الزامی است.");

        if (string.IsNullOrWhiteSpace(model.Name))
            ModelState.AddModelError(nameof(model.Name), "نام واحد الزامی است.");

        if (await _db.Units.AnyAsync(u => u.Id != currentId && u.Code == model.Code))
            ModelState.AddModelError(nameof(model.Code), "کد واحد تکراری است.");

        if (model.ConversionFactorToBase.HasValue && model.ConversionFactorToBase.Value <= 0m)
            ModelState.AddModelError(nameof(model.ConversionFactorToBase), "ضریب تبدیل باید بزرگ‌تر از صفر باشد.");
    }

    private static void Normalize(Unit model)
    {
        model.Code = (model.Code ?? string.Empty).Trim().ToUpperInvariant();
        model.Name = (model.Name ?? string.Empty).Trim();
        model.NamePersian = string.IsNullOrWhiteSpace(model.NamePersian) ? null : model.NamePersian.Trim();
        model.Symbol = string.IsNullOrWhiteSpace(model.Symbol) ? null : model.Symbol.Trim();
        model.UnitType = string.IsNullOrWhiteSpace(model.UnitType) ? null : model.UnitType.Trim();
        model.BaseUnitCode = string.IsNullOrWhiteSpace(model.BaseUnitCode) ? null : model.BaseUnitCode.Trim().ToUpperInvariant();
        model.Notes = string.IsNullOrWhiteSpace(model.Notes) ? null : model.Notes.Trim();
    }
}
