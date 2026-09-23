using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Helpers;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Security;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Exceptions;
using PTGOilSystem.Web.Services.Time;

namespace PTGOilSystem.Web.Controllers;

// Amendments are immutable: only list & create are exposed.
[Authorize]
public class ContractAmendmentsController : Controller
{
    private readonly ApplicationDbContext _db;
    private readonly IContractAmendmentService _amendments;
    public ContractAmendmentsController(ApplicationDbContext db, IContractAmendmentService amendments)
    {
        _db = db;
        _amendments = amendments;
    }

    public async Task<IActionResult> Index(int contractId, int page = 1, [FromQuery(Name = "pageSize")] int? perPage = null)
    {
        var contract = await _db.Contracts.AsNoTracking().FirstOrDefaultAsync(c => c.Id == contractId);
        if (contract == null) return NotFound();
        ViewBag.Contract = contract;

        const int defaultPageSize = 20;
        var pageSize = ListPageSize.Resolve(perPage, defaultPageSize);
        ViewData["PageSize"] = pageSize;
        ViewData["DefaultPageSize"] = defaultPageSize;

        // صفحه‌بندی در خود دیتابیس اعمال می‌شود؛ ترتیب «شمارهٔ متمم» دست‌نخورده است.
        var query = _db.ContractAmendments.AsNoTracking()
            .Where(a => a.ContractId == contractId);

        var totalCount = await query.CountAsync();
        var pageCount = Math.Max(1, (int)Math.Ceiling(totalCount / (double)pageSize));
        var currentPage = Math.Clamp(page, 1, pageCount);

        var list = await query
            .OrderBy(a => a.AmendmentNumber)
            .Skip((currentPage - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        ViewBag.CurrentPage = currentPage;
        ViewBag.PageCount = pageCount;
        ViewBag.TotalCount = totalCount;
        return View(list);
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    public async Task<IActionResult> Create(int contractId)
    {
        var contract = await _db.Contracts
            .Include(c => c.Product)
            .Include(c => c.Unit)
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == contractId);
        if (contract == null) return NotFound();
        ViewBag.Contract = contract;
        return View(new ContractAmendment
        {
            ContractId = contractId,
            AmendmentDate = AfghanistanBusinessClock.SystemToday,
            NewQuantityMt = contract.QuantityMt,
            NewUnitPriceUsd = contract.UnitPriceUsd,
            NewPremiumUsd = contract.PremiumUsd,
        });
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(ContractAmendment model)
    {
        var contract = await _db.Contracts
            .Include(c => c.Product)
            .Include(c => c.Unit)
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == model.ContractId);
        if (contract == null) return NotFound();

        if (string.IsNullOrWhiteSpace(model.ChangeSummary))
            ModelState.AddModelError(nameof(model.ChangeSummary), "خلاصه تغییرات الزامی است.");

        if (!ModelState.IsValid)
        {
            ViewBag.Contract = contract;
            return View(model);
        }

        try
        {
            var saved = await _amendments.AddAmendmentAsync(
                contractId: model.ContractId,
                changeSummary: model.ChangeSummary,
                newQuantityMt: model.NewQuantityMt,
                newUnitPriceUsd: model.NewUnitPriceUsd,
                newPremiumUsd: model.NewPremiumUsd,
                amendmentDate: model.AmendmentDate);
            TempData["ok"] = $"متمم {saved.AmendmentNumber} ثبت شد.";
            return RedirectToAction("Details", "Contracts", new { id = model.ContractId });
        }
        catch (BusinessRuleException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            ViewBag.Contract = contract;
            return View(model);
        }
    }
}
