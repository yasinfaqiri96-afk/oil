using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Helpers;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.Partners;
using PTGOilSystem.Web.Models.Payments;
using PTGOilSystem.Web.Security;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Audit;
using PTGOilSystem.Web.Services.DeleteSafety;
using PTGOilSystem.Web.Services.PartyStatements;

namespace PTGOilSystem.Web.Controllers;

[Authorize]
public partial class PartnersController : Controller
{
    private readonly ApplicationDbContext _db;
    private readonly IAuditService _audit;
    private readonly MasterDataDeleteSafetyService _deleteSafety;

    // ارقام مالی پروفایل شریک فقط از این سرویس می‌آیند. کنترلر دیگر فرمول مستقلِ خودش را
    // برای مانده شریک ندارد؛ داشتنش باعث می‌شد پروفایل و صورت‌حساب شراکت دو عدد بدهند.
    private readonly IPartnershipStatementService _partnershipStatements;

    public PartnersController(
        ApplicationDbContext db,
        IAuditService audit,
        MasterDataDeleteSafetyService deleteSafety,
        IPartnershipStatementService? partnershipStatements = null)
    {
        _db = db;
        _audit = audit;
        _deleteSafety = deleteSafety;
        _partnershipStatements = partnershipStatements ?? new PartnershipStatementService(db);
    }

    public async Task<IActionResult> Index(string? q, int page = 1, [FromQuery(Name = "pageSize")] int? perPage = null)
    {
        var pageSize = ListPageSize.Resolve(perPage, 20);
        ViewData["PageSize"] = pageSize;
        ViewData["DefaultPageSize"] = 20;

        var query = _db.Partners.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim();
            // PTG canonical search — کلیدِ canonical به شرطِ قبلی اضافه می‌شود، جایگزینِ آن نمی‌شود:
            // هیچ نتیجه‌ای از دست نمی‌رود و «یوسف» سطرِ «يوسف» را هم پیدا می‌کند.
            // SearchKey خالی یعنی سطرِ پیش از Backfill؛ همان شرطِ قبلی هنوز آن را می‌یابد.
            var canonicalTerm = AfghanTextNormalizer.NormalizeForSearch(term);
            query = query.Where(p =>
                (p.SearchKey != null && p.SearchKey.Contains(canonicalTerm)) ||
                p.Code.Contains(term) ||
                p.Name.Contains(term) ||
                (p.NamePersian != null && p.NamePersian.Contains(term)) ||
                (p.Country != null && p.Country.Contains(term)) ||
                (p.ContactPerson != null && p.ContactPerson.Contains(term)) ||
                (p.Email != null && p.Email.Contains(term)));
        }

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

    public async Task<IActionResult> Details(
        int id,
        string? tab = null,
        int? contractId = null,
        DateTime? fromDate = null,
        DateTime? toDate = null)
    {
        var partner = await _db.Partners
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id, HttpContext.RequestAborted);
        if (partner is null)
        {
            return NotFound();
        }

        var statement = await _partnershipStatements.BuildForPartnerAsync(
            id,
            contractIds: null,
            HttpContext.RequestAborted);

        // فیلترها فقط نمایشی‌اند: مانده تجمعی از ابتدای حساب محاسبه شده و با فیلتر جابه‌جا نمی‌شود.
        var entries = (statement?.Entries ?? []).AsEnumerable();
        if (contractId.HasValue)
        {
            entries = entries.Where(e => e.ContractId == contractId.Value);
        }

        if (fromDate.HasValue)
        {
            var from = fromDate.Value.Date;
            entries = entries.Where(e => e.Date.HasValue && e.Date.Value.Date >= from);
        }

        if (toDate.HasValue)
        {
            var to = toDate.Value.Date;
            entries = entries.Where(e => e.Date.HasValue && e.Date.Value.Date <= to);
        }

        return View(new PartnerProfileViewModel
        {
            PartnerId = partner.Id,
            Code = partner.Code,
            Name = partner.Name,
            NamePersian = partner.NamePersian,
            Country = partner.Country,
            ContactPerson = partner.ContactPerson,
            Phone = partner.Phone,
            Address = partner.Address,
            Email = partner.Email,
            Notes = partner.Notes,
            IsActive = partner.IsActive,
            CreatedAtUtc = partner.CreatedAtUtc,
            ActiveTab = PartnerProfileTabs.Resolve(tab),
            Statement = statement,
            FilterContractId = contractId,
            FromDate = fromDate,
            ToDate = toDate,
            Entries = entries.ToList()
        });
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    public IActionResult Create()
        => View(new Partner { IsActive = true });

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create([Bind("Id,Code,Name,NamePersian,Country,ContactPerson,Phone,Address,Email,IsActive,Notes")] Partner model, string? returnUrl = null)
    {
        Normalize(model);
        await ValidateAsync(model, model.Id);

        if (!ModelState.IsValid)
            return View(model);

        _db.Partners.Add(model);
        await _db.SaveChangesAsync();
        await _audit.LogAndSaveAsync(
            nameof(Partner),
            model.Id,
            AuditAction.Insert,
            diff: AuditDiffFormatter.ForCreate(
                ("Code", model.Code),
                ("Name", model.Name),
                ("NamePersian", model.NamePersian),
                ("Country", model.Country),
                ("ContactPerson", model.ContactPerson),
                ("Phone", model.Phone),
                ("Address", model.Address),
                ("Email", model.Email),
                ("IsActive", model.IsActive),
                ("Notes", model.Notes)));

        TempData["ok"] = "شریک با موفقیت ثبت شد.";
        if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl)) return LocalRedirect(returnUrl);
        return RedirectToAction(nameof(Index));
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    public async Task<IActionResult> Edit(int id)
    {
        var item = await _db.Partners.FirstOrDefaultAsync(x => x.Id == id);
        return item is null ? NotFound() : View(item);
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, [Bind("Id,Code,Name,NamePersian,Country,ContactPerson,Phone,Address,Email,IsActive,Notes")] Partner model)
    {
        if (id != model.Id)
            return BadRequest();

        Normalize(model);
        await ValidateAsync(model, id);

        if (!ModelState.IsValid)
            return View(model);

        var existing = await _db.Partners.FirstOrDefaultAsync(x => x.Id == id);
        if (existing is null)
            return NotFound();

        var diff = AuditDiffFormatter.ForUpdate(
            ("Code", existing.Code, model.Code),
            ("Name", existing.Name, model.Name),
            ("NamePersian", existing.NamePersian, model.NamePersian),
            ("Country", existing.Country, model.Country),
            ("ContactPerson", existing.ContactPerson, model.ContactPerson),
            ("Phone", existing.Phone, model.Phone),
            ("Address", existing.Address, model.Address),
            ("Email", existing.Email, model.Email),
            ("IsActive", existing.IsActive, model.IsActive),
            ("Notes", existing.Notes, model.Notes));

        existing.Code = model.Code;
        existing.Name = model.Name;
        existing.NamePersian = model.NamePersian;
        existing.Country = model.Country;
        existing.ContactPerson = model.ContactPerson;
        existing.Phone = model.Phone;
        existing.Address = model.Address;
        existing.Email = model.Email;
        existing.IsActive = model.IsActive;
        existing.Notes = model.Notes;

        await _db.SaveChangesAsync();
        await _audit.LogAndSaveAsync(nameof(Partner), existing.Id, AuditAction.Update, diff: diff);

        TempData["ok"] = "ویرایش شریک انجام شد.";
        return RedirectToAction(nameof(Index));
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        var item = await _db.Partners.FirstOrDefaultAsync(x => x.Id == id);
        if (item is null)
            return NotFound();

        var safety = await _deleteSafety.EvaluatePartnerAsync(id);
        if (!safety.CanDelete)
        {
            if (safety.ArchiveInsteadOfDelete && item.IsActive)
            {
                var archiveDiff = $"ArchiveInsteadOfDelete: {safety.DependencySummary} | "
                    + AuditDiffFormatter.ForUpdate(("IsActive", item.IsActive, false));
                item.IsActive = false;
                await _db.SaveChangesAsync();
                await _audit.LogAndSaveAsync(nameof(Partner), item.Id, AuditAction.Update, diff: archiveDiff);
                TempData["ok"] = safety.BuildArchivedMessage("شریک");
                return RedirectToAction(nameof(Index));
            }

            TempData["err"] = safety.ArchiveInsteadOfDelete
                ? $"{safety.BuildBlockedMessage("شریک")} این رکورد قبلاً غیرفعال شده است."
                : safety.BuildBlockedMessage("شریک");
            return RedirectToAction(nameof(Index));
        }

        var diff = AuditDiffFormatter.ForDelete(
            ("Code", item.Code),
            ("Name", item.Name),
            ("Country", item.Country));
        _db.Partners.Remove(item);

        // PTG-P2-01 — کلیدهای خارجیِ Restrict آخرین نگهبان‌اند و همیشه باید باشند.
        // EvaluatePartnerAsync ارجاع‌های شناخته‌شده را می‌گیرد، ولی اگر روزی رابطهٔ تازه‌ای
        // اضافه شود و اینجا از قلم بیفتد، کاربر باید پیام فارسی ببیند نه خطای ۵۰۰.
        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            _db.Entry(item).State = EntityState.Unchanged;
            TempData["err"] = "این شریک در اسناد دیگری استفاده شده است و حذف نشد. "
                + "ابتدا آن اسناد را بررسی کنید یا شریک را غیرفعال کنید.";
            return RedirectToAction(nameof(Index));
        }

        await _audit.LogAndSaveAsync(nameof(Partner), id, AuditAction.Delete, diff: diff);
        TempData["ok"] = "شریک حذف شد.";
        return RedirectToAction(nameof(Index));
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ToggleActive(int id, string? returnUrl = null)
    {
        var item = await _db.Partners.FirstOrDefaultAsync(x => x.Id == id);
        if (item is null) return NotFound();

        var wasActive = item.IsActive;
        item.IsActive = !item.IsActive;
        await _db.SaveChangesAsync();
        await _audit.LogAndSaveAsync(nameof(Partner), item.Id, AuditAction.Update,
            diff: AuditDiffFormatter.ForUpdate(("IsActive", wasActive, item.IsActive)));

        TempData["ok"] = item.IsActive ? "رکورد فعال شد." : "رکورد غیرفعال شد.";
        if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl)) return Redirect(returnUrl);
        return RedirectToAction(nameof(Index));
    }

    private async Task ValidateAsync(Partner model, int currentId)
    {
        if (string.IsNullOrWhiteSpace(model.Code))
            ModelState.AddModelError(nameof(model.Code), "کد شریک الزامی است.");

        if (string.IsNullOrWhiteSpace(model.Name))
            ModelState.AddModelError(nameof(model.Name), "نام شریک الزامی است.");

        if (await _db.Partners.AnyAsync(p => p.Id != currentId && p.Code == model.Code))
            ModelState.AddModelError(nameof(model.Code), "کد شریک تکراری است.");
    }

    private static void Normalize(Partner model)
    {
        model.Code = (model.Code ?? string.Empty).Trim().ToUpperInvariant();
        model.Name = (model.Name ?? string.Empty).Trim();
        model.NamePersian = string.IsNullOrWhiteSpace(model.NamePersian) ? null : model.NamePersian.Trim();
        model.Country = string.IsNullOrWhiteSpace(model.Country) ? null : model.Country.Trim();
        model.ContactPerson = string.IsNullOrWhiteSpace(model.ContactPerson) ? null : model.ContactPerson.Trim();
        model.Phone = string.IsNullOrWhiteSpace(model.Phone) ? null : model.Phone.Trim();
        model.Address = string.IsNullOrWhiteSpace(model.Address) ? null : model.Address.Trim();
        model.Email = string.IsNullOrWhiteSpace(model.Email) ? null : model.Email.Trim();
        model.Notes = string.IsNullOrWhiteSpace(model.Notes) ? null : model.Notes.Trim();
    }

    [HttpGet]
    public async Task<IActionResult> GetCloneData(int id)
    {
        var item = await _db.Partners.AsNoTracking()
            .Where(p => p.Id == id)
            .Select(p => new { p.Code, p.Name, p.Country, p.ContactPerson, p.Phone, p.IsActive })
            .FirstOrDefaultAsync();
        if (item == null) return NotFound();
        return Json(item);
    }


}
