using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Security;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Audit;
using PTGOilSystem.Web.Services.DeleteSafety;
using PTGOilSystem.Web.Models.PartyStatements;
using PTGOilSystem.Web.Services.PartyStatements;

namespace PTGOilSystem.Web.Controllers;

[Authorize]
public class CompaniesController : Controller
{
    private readonly ApplicationDbContext _db;
    private readonly IAuditService _audit;
    private readonly MasterDataDeleteSafetyService _deleteSafety;
    private readonly IPartyStatementReadService? _partyStatements;

    public CompaniesController(
        ApplicationDbContext db,
        IAuditService audit,
        MasterDataDeleteSafetyService deleteSafety,
        IPartyStatementReadService? partyStatements = null)
    {
        _db = db;
        _audit = audit;
        _deleteSafety = deleteSafety;
        _partyStatements = partyStatements;
    }

    public async Task<IActionResult> Index(string? q, int page = 1)
    {
        const int pageSize = 20;

        var query = _db.Companies.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(q))
            query = query.Where(p => p.Code.Contains(q) || p.Name.Contains(q) || (p.NamePersian != null && p.NamePersian.Contains(q)) || (p.Notes != null && p.Notes.Contains(q)));

        var totalCount = await query.CountAsync();
        var pageCount = Math.Max(1, (int)Math.Ceiling(totalCount / (double)pageSize));
        page = Math.Clamp(page, 1, pageCount);

        ViewData["q"] = q;
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
        var item = await _db.Companies.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (item == null) return NotFound();
        if (_partyStatements is not null)
        {
            var statement = await _partyStatements.GetStatementAsync(
                new PartyRef(PartyStatementPartyType.Company, id),
                new PartyStatementFilter { IncludeOperationalColumns = false },
                HttpContext.RequestAborted);
            ViewData["PartyStatementSummary"] = statement.Summary;
            ViewData["PartyStatementRecentRows"] = statement.Rows.Where(r => !r.IsOpeningBalance).Reverse().Take(5).ToList();
        }
        return View(item);
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    public IActionResult Create() => View(new Company());

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create([Bind("Id,Code,Name,NamePersian,Country,Address,IsActive,Notes")] Company model, string? returnUrl = null)
    {
        Normalize(model);
        if (!ModelState.IsValid) return View(model);
        if (await _db.Companies.AnyAsync(p => p.Code == model.Code))
        {
            ModelState.AddModelError(nameof(model.Code), "این کد قبلاً ثبت شده است.");
            return View(model);
        }
        _db.Companies.Add(model);
        await _db.SaveChangesAsync();
        await _audit.LogAndSaveAsync(
            nameof(Company),
            model.Id,
            AuditAction.Insert,
            diff: AuditDiffFormatter.ForCreate(
                ("Code", model.Code),
                ("Name", model.Name),
                ("NamePersian", model.NamePersian),
                ("Country", model.Country),
                ("Address", model.Address),
                ("IsActive", model.IsActive),
                ("Notes", model.Notes)));
        TempData["ok"] = "شرکت با موفقیت ثبت شد.";
        if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl)) return LocalRedirect(returnUrl);
        return RedirectToAction(nameof(Index));
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    public async Task<IActionResult> Edit(int id)
    {
        var item = await _db.Companies.FirstOrDefaultAsync(x => x.Id == id);
        if (item == null) return NotFound();
        return View(item);
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, [Bind("Id,Code,Name,NamePersian,Country,Address,IsActive,Notes")] Company model)
    {
        if (id != model.Id) return BadRequest();
        Normalize(model);
        if (!ModelState.IsValid) return View(model);
        var existing = await _db.Companies.FirstOrDefaultAsync(x => x.Id == id);
        if (existing == null) return NotFound();
        if (existing.Code != model.Code && await _db.Companies.AnyAsync(p => p.Code == model.Code))
        {
            ModelState.AddModelError(nameof(model.Code), "این کد قبلاً ثبت شده است.");
            return View(model);
        }
        var diff = AuditDiffFormatter.ForUpdate(
            ("Code", existing.Code, model.Code),
            ("Name", existing.Name, model.Name),
            ("NamePersian", existing.NamePersian, model.NamePersian),
            ("Country", existing.Country, model.Country),
            ("Address", existing.Address, model.Address),
            ("IsActive", existing.IsActive, model.IsActive),
            ("Notes", existing.Notes, model.Notes));
        existing.Code = model.Code;
        existing.Name = model.Name;
        existing.NamePersian = model.NamePersian;
        existing.Country = model.Country;
        existing.Address = model.Address;
        existing.IsActive = model.IsActive;
        existing.Notes = model.Notes;
        await _db.SaveChangesAsync();
        await _audit.LogAndSaveAsync(nameof(Company), existing.Id, AuditAction.Update, diff: diff);
        TempData["ok"] = "ویرایش با موفقیت انجام شد.";
        return RedirectToAction(nameof(Index));
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        var item = await _db.Companies.FirstOrDefaultAsync(x => x.Id == id);
        if (item == null) return NotFound();

        var safety = await _deleteSafety.EvaluateCompanyAsync(id);
        if (!safety.CanDelete)
        {
            if (safety.ArchiveInsteadOfDelete && item.IsActive)
            {
                var archiveDiff = $"ArchiveInsteadOfDelete: {safety.DependencySummary} | "
                    + AuditDiffFormatter.ForUpdate(("IsActive", item.IsActive, false));
                item.IsActive = false;
                await _db.SaveChangesAsync();
                await _audit.LogAndSaveAsync(nameof(Company), item.Id, AuditAction.Update, diff: archiveDiff);
                TempData["ok"] = safety.BuildArchivedMessage("شرکت");
                return RedirectToAction(nameof(Index));
            }

            TempData["err"] = safety.ArchiveInsteadOfDelete
                ? $"{safety.BuildBlockedMessage("شرکت")} این رکورد قبلاً غیرفعال شده است."
                : safety.BuildBlockedMessage("شرکت");
            return RedirectToAction(nameof(Index));
        }

        var deleteDiff = AuditDiffFormatter.ForDelete(
            ("Code", item.Code),
            ("Name", item.Name),
            ("Country", item.Country));
        _db.Companies.Remove(item);
        await _db.SaveChangesAsync();
        await _audit.LogAndSaveAsync(nameof(Company), id, AuditAction.Delete, diff: deleteDiff);
        TempData["ok"] = "شرکت حذف شد.";
        return RedirectToAction(nameof(Index));
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ToggleActive(int id, string? returnUrl = null)
    {
        var item = await _db.Companies.FirstOrDefaultAsync(x => x.Id == id);
        if (item == null) return NotFound();

        var wasActive = item.IsActive;
        item.IsActive = !item.IsActive;
        await _db.SaveChangesAsync();
        await _audit.LogAndSaveAsync(nameof(Company), item.Id, AuditAction.Update,
            diff: AuditDiffFormatter.ForUpdate(("IsActive", wasActive, item.IsActive)));

        TempData["ok"] = item.IsActive ? "رکورد فعال شد." : "رکورد غیرفعال شد.";
        if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl)) return Redirect(returnUrl);
        return RedirectToAction(nameof(Index));
    }

    /// <summary>
    /// تعیینِ «شرکتِ مالکِ سیستم». دقیقاً یک شرکت می‌تواند مالک باشد (ایندکسِ یکتای جزئی روی
    /// <see cref="Company.IsSystemOwner"/>)، بنابراین مالکِ قبلی در همین تراکنش پاک می‌شود و بعد
    /// مالکِ جدید ست می‌شود. این ساختارِ دفاترِ مالی را تعیین می‌کند و کارِ نقشِ عملیاتی نیست.
    /// </summary>
    [Authorize(Policy = AuthPolicies.AdminOnly)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SetSystemOwner(int id, string? returnUrl = null)
    {
        var item = await _db.Companies.FirstOrDefaultAsync(x => x.Id == id);
        if (item == null) return NotFound();

        if (item.IsSystemOwner)
        {
            TempData["ok"] = "این شرکت از قبل مالکِ سیستم است.";
            return RedirectBack(returnUrl);
        }

        var previousOwners = await _db.Companies.Where(x => x.IsSystemOwner && x.Id != id).ToListAsync();

        await using var tx = await _db.Database.BeginTransactionAsync();

        // مالکِ قبلی باید قبل از ست‌شدنِ مالکِ جدید و در یک SaveChanges جدا پاک شود، وگرنه
        // ایندکسِ یکتای جزئی وسطِ همان دستور می‌شکند.
        if (previousOwners.Count > 0)
        {
            foreach (var previous in previousOwners)
                previous.IsSystemOwner = false;
            await _db.SaveChangesAsync();
        }

        item.IsSystemOwner = true;
        await _db.SaveChangesAsync();

        foreach (var previous in previousOwners)
        {
            await _audit.LogAndSaveAsync(nameof(Company), previous.Id, AuditAction.Update,
                diff: AuditDiffFormatter.ForUpdate(("IsSystemOwner", true, false)));
        }
        await _audit.LogAndSaveAsync(nameof(Company), item.Id, AuditAction.Update,
            diff: AuditDiffFormatter.ForUpdate(("IsSystemOwner", false, true)));

        await tx.CommitAsync();

        TempData["ok"] = previousOwners.Count > 0
            ? $"«{item.Name}» مالکِ سیستم شد و مالکِ قبلی برداشته شد."
            : $"«{item.Name}» مالکِ سیستم شد.";
        return RedirectBack(returnUrl);
    }

    private IActionResult RedirectBack(string? returnUrl)
        => !string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl)
            ? Redirect(returnUrl)
            : RedirectToAction(nameof(Index));

    private static void Normalize(Company model)
    {
        model.Code = (model.Code ?? string.Empty).Trim().ToUpperInvariant();
        model.Name = (model.Name ?? string.Empty).Trim();
        model.NamePersian = string.IsNullOrWhiteSpace(model.NamePersian) ? null : model.NamePersian.Trim();
        model.Country = (model.Country ?? string.Empty).Trim();
        model.Address = string.IsNullOrWhiteSpace(model.Address) ? null : model.Address.Trim();
        model.Notes = string.IsNullOrWhiteSpace(model.Notes) ? null : model.Notes.Trim();
    }

    [HttpGet]
    public async Task<IActionResult> GetCloneData(int id)
    {
        var item = await _db.Companies.AsNoTracking()
            .Where(c => c.Id == id)
            .Select(c => new { c.Code, c.Name, c.Country, c.IsActive })
            .FirstOrDefaultAsync();
        if (item == null) return NotFound();
        return Json(item);
    }
}
