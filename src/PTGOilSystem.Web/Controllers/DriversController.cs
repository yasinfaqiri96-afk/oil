using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Security;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Audit;
using PTGOilSystem.Web.Models.PartyStatements;
using PTGOilSystem.Web.Services.PartyStatements;
using PTGOilSystem.Web.Helpers;

namespace PTGOilSystem.Web.Controllers;

[Authorize]
public class DriversController : Controller
{
    private readonly ApplicationDbContext _db;
    private readonly IAuditService _audit;
    private readonly IPartyStatementReadService? _partyStatements;

    public DriversController(ApplicationDbContext db, IAuditService audit, IPartyStatementReadService? partyStatements = null)
    {
        _db = db;
        _audit = audit;
        _partyStatements = partyStatements;
    }

    public async Task<IActionResult> Index(string? q, bool? isActive, int? selectedId = null, string? detailTab = null, int page = 1, [FromQuery(Name = "pageSize")] int? perPage = null)
    {
        var pageSize = ListPageSize.Resolve(perPage, 8);
        ViewData["PageSize"] = pageSize;
        ViewData["DefaultPageSize"] = 8;
        var search = string.IsNullOrWhiteSpace(q) ? null : q.Trim();
        var query = _db.Drivers.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
        {
            query = query.Where(d =>
                d.FullName.Contains(search) ||
                (d.LicenseNumber != null && d.LicenseNumber.Contains(search)) ||
                (d.NationalId != null && d.NationalId.Contains(search)) ||
                (d.Phone != null && d.Phone.Contains(search)));
        }
        if (isActive.HasValue)
            query = query.Where(d => d.IsActive == isActive.Value);

        var totalCount = await query.CountAsync();
        var pageCount = Math.Max(1, (int)Math.Ceiling(totalCount / (double)pageSize));
        var currentPage = Math.Clamp(page, 1, pageCount);
        var drivers = await query
            .OrderBy(d => d.FullName)
            .Skip((currentPage - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        ViewData["q"] = search;
        ViewData["isActive"] = isActive;
        ViewData["CurrentPage"] = currentPage;
        ViewData["PageCount"] = pageCount;
        ViewData["TotalCount"] = totalCount;

        return View(drivers);
    }

    public async Task<IActionResult> Details(int id)
    {
        var item = await _db.Drivers.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (item == null) return NotFound();
        ViewData["ResourceProfile"] = await TransportResourceProfileBuilder.ForDriverAsync(_db, item, "info");

        // خلاصهٔ مالیِ راننده از دفتر (Ledger): کرایهٔ بستانکار (Credit) و بدهیِ خسارتِ کسری (Debit).
        // ماندهٔ خالص = کرایه − خسارت = چقدر به راننده بدهکاریم (مثبت) یا او به ما (منفی).
        var driverLedger = await _db.LedgerEntries.AsNoTracking()
            .Where(l => l.DriverId == id)
            .Select(l => new { l.Side, l.AmountUsd })
            .ToListAsync();
        var freightCreditUsd = driverLedger.Where(l => l.Side == LedgerSide.Credit).Sum(l => l.AmountUsd);
        var shortageDebitUsd = driverLedger.Where(l => l.Side == LedgerSide.Debit).Sum(l => l.AmountUsd);
        ViewData["DriverHasLedger"] = driverLedger.Count > 0;
        ViewData["DriverFreightCreditUsd"] = freightCreditUsd;
        ViewData["DriverShortageDebitUsd"] = shortageDebitUsd;
        ViewData["DriverNetOwedUsd"] = freightCreditUsd - shortageDebitUsd;

        if (_partyStatements is not null)
        {
            var statement = await _partyStatements.GetStatementAsync(
                new PartyRef(PartyStatementPartyType.Driver, id),
                new PartyStatementFilter { IncludeOperationalColumns = false },
                HttpContext.RequestAborted);
            ViewData["PartyStatementSummary"] = statement.Summary;
            ViewData["PartyStatementRecentRows"] = statement.Rows.Where(r => !r.IsOpeningBalance).Reverse().Take(5).ToList();
        }

        return View(item);
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    public IActionResult Create() => View(new Driver { IsActive = true });

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create([Bind("Id,FullName,LicenseNumber,NationalId,Phone,Address,IsActive,Notes")] Driver model, string? returnUrl = null)
    {
        Normalize(model);
        if (!ModelState.IsValid) return View(model);

        _db.Drivers.Add(model);
        await _db.SaveChangesAsync();
        await _audit.LogAndSaveAsync(
            nameof(Driver),
            model.Id,
            AuditAction.Insert,
            diff: AuditDiffFormatter.ForCreate(
                ("FullName", model.FullName),
                ("LicenseNumber", model.LicenseNumber),
                ("NationalId", model.NationalId),
                ("Phone", model.Phone),
                ("Address", model.Address),
                ("IsActive", model.IsActive),
                ("Notes", model.Notes)));

        TempData["ok"] = "راننده با موفقیت ثبت شد.";
        if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl)) return LocalRedirect(returnUrl);
        return RedirectToAction(nameof(Index));
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    public async Task<IActionResult> Edit(int id)
    {
        var item = await _db.Drivers.FirstOrDefaultAsync(x => x.Id == id);
        if (item == null) return NotFound();
        return View(item);
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ToggleActive(int id, string? returnUrl = null)
    {
        var item = await _db.Drivers.FirstOrDefaultAsync(x => x.Id == id);
        if (item is null) return NotFound();

        var wasActive = item.IsActive;
        item.IsActive = !item.IsActive;
        await _db.SaveChangesAsync();
        await _audit.LogAndSaveAsync(nameof(Driver), item.Id, AuditAction.Update,
            diff: AuditDiffFormatter.ForUpdate(("IsActive", wasActive, item.IsActive)));

        TempData["ok"] = item.IsActive ? "رکورد فعال شد." : "رکورد غیرفعال شد.";
        if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl)) return Redirect(returnUrl);
        return RedirectToAction(nameof(Index));
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, [Bind("Id,FullName,LicenseNumber,NationalId,Phone,Address,IsActive,Notes")] Driver model)
    {
        if (id != model.Id) return BadRequest();
        Normalize(model);
        if (!ModelState.IsValid) return View(model);

        var existing = await _db.Drivers.FirstOrDefaultAsync(x => x.Id == id);
        if (existing == null) return NotFound();

        var diff = AuditDiffFormatter.ForUpdate(
            ("FullName", existing.FullName, model.FullName),
            ("LicenseNumber", existing.LicenseNumber, model.LicenseNumber),
            ("NationalId", existing.NationalId, model.NationalId),
            ("Phone", existing.Phone, model.Phone),
            ("Address", existing.Address, model.Address),
            ("IsActive", existing.IsActive, model.IsActive),
            ("Notes", existing.Notes, model.Notes));

        existing.FullName = model.FullName;
        existing.LicenseNumber = model.LicenseNumber;
        existing.NationalId = model.NationalId;
        existing.Phone = model.Phone;
        existing.Address = model.Address;
        existing.IsActive = model.IsActive;
        existing.Notes = model.Notes;

        await _db.SaveChangesAsync();
        await _audit.LogAndSaveAsync(nameof(Driver), existing.Id, AuditAction.Update, diff: diff);

        TempData["ok"] = "ویرایش راننده با موفقیت انجام شد.";
        return RedirectToAction(nameof(Index));
    }

    private static void Normalize(Driver model)
    {
        model.FullName = (model.FullName ?? string.Empty).Trim();
        model.LicenseNumber = string.IsNullOrWhiteSpace(model.LicenseNumber) ? null : model.LicenseNumber.Trim();
        model.NationalId = string.IsNullOrWhiteSpace(model.NationalId) ? null : model.NationalId.Trim();
        model.Phone = string.IsNullOrWhiteSpace(model.Phone) ? null : model.Phone.Trim();
        model.Address = string.IsNullOrWhiteSpace(model.Address) ? null : model.Address.Trim();
        model.Notes = string.IsNullOrWhiteSpace(model.Notes) ? null : model.Notes.Trim();
    }
}
