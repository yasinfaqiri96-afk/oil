using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Helpers;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Security;

namespace PTGOilSystem.Web.Controllers;

// Gap #7 — multi-contract vessel shipment: manage ShipmentContracts junction
[Authorize]
public class ShipmentContractsController : Controller
{
    private readonly ApplicationDbContext _db;

    public ShipmentContractsController(ApplicationDbContext db)
    {
        _db = db;
    }

    private bool TryGetLocalReturnUrl(string? url, out string local)
    {
        if (!string.IsNullOrWhiteSpace(url) && Url?.IsLocalUrl(url) == true) { local = url; return true; }
        local = string.Empty; return false;
    }

    // GET: /ShipmentContracts?shipmentId=X
    public async Task<IActionResult> Index(int shipmentId)
    {
        var shipment = await _db.Shipments
            .AsNoTracking()
            .Include(s => s.Vessel)
            .Include(s => s.Contract)
            .FirstOrDefaultAsync(s => s.Id == shipmentId);

        if (shipment is null) return NotFound();

        var links = await _db.ShipmentContracts
            .AsNoTracking()
            .Where(sc => sc.ShipmentId == shipmentId)
            .Include(sc => sc.Contract).ThenInclude(c => c!.Product)
            .OrderBy(sc => sc.Id)
            .Select(sc => new ShipmentContractRowViewModel
            {
                Id = sc.Id,
                ShipmentId = sc.ShipmentId,
                ContractId = sc.ContractId,
                ContractName = sc.Contract != null ? sc.Contract.ContractName : "",
                ContractNumber = sc.Contract != null ? sc.Contract.ContractNumber : "",
                ContractType = sc.Contract != null ? sc.Contract.ContractType : ContractType.Purchase,
                ProductName = sc.Contract != null && sc.Contract.Product != null ? sc.Contract.Product.Name : "",
                QuantityMt = sc.QuantityMt,
                Notes = sc.Notes
            })
            .ToListAsync();

        ViewBag.ShipmentId = shipmentId;
        ViewBag.ShipmentCode = shipment.ShipmentCode;
        ViewBag.PrimaryContractNumber = shipment.Contract?.DisplayLabel ?? "—";
        return View(links);
    }

    // GET: /ShipmentContracts/Create?shipmentId=X
    [Authorize(Policy = AuthPolicies.ManageData)]
    public async Task<IActionResult> Create(int shipmentId, string? returnUrl = null)
    {
        var shipment = await _db.Shipments.AsNoTracking().FirstOrDefaultAsync(s => s.Id == shipmentId);
        if (shipment is null) return NotFound();

        var existingContractIds = await _db.ShipmentContracts
            .AsNoTracking()
            .Where(sc => sc.ShipmentId == shipmentId)
            .Select(sc => sc.ContractId)
            .ToListAsync();

        var contracts = await _db.Contracts
            .AsNoTracking()
            .Where(c => !existingContractIds.Contains(c.Id))
            .OrderByDescending(c => c.ContractDate)
            .Take(200)
            .Select(c => new ContractOptionRow(c.Id, c.ContractName, c.ContractNumber))
            .ToListAsync();

        ViewBag.ShipmentId = shipmentId;
        ViewBag.ShipmentCode = shipment.ShipmentCode;
        ViewBag.ContractOptions = ToContractOptions(contracts);
        ViewBag.ReturnUrl = returnUrl;
        return View(new ShipmentContractCreateViewModel { ShipmentId = shipmentId, ReturnUrl = returnUrl });
    }

    // POST: /ShipmentContracts/Create
    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(ShipmentContractCreateViewModel model)
    {
        var shipment = await _db.Shipments.AsNoTracking().FirstOrDefaultAsync(s => s.Id == model.ShipmentId);
        if (shipment is null) { ModelState.AddModelError(string.Empty, "محموله یافت نشد."); }

        var duplicate = await _db.ShipmentContracts
            .AnyAsync(sc => sc.ShipmentId == model.ShipmentId && sc.ContractId == model.ContractId);
        if (duplicate) { ModelState.AddModelError(string.Empty, "این قرارداد قبلاً به این محموله اضافه شده است."); }

        if (!ModelState.IsValid)
        {
            if (shipment is not null)
            {
                var existingIds = await _db.ShipmentContracts.AsNoTracking()
                    .Where(sc => sc.ShipmentId == model.ShipmentId).Select(sc => sc.ContractId).ToListAsync();
                var contractRows = await _db.Contracts.AsNoTracking()
                    .Where(c => !existingIds.Contains(c.Id))
                    .OrderByDescending(c => c.ContractDate)
                    .Take(200)
                    .Select(c => new ContractOptionRow(c.Id, c.ContractName, c.ContractNumber))
                    .ToListAsync();
                ViewBag.ContractOptions = ToContractOptions(contractRows);
            }
            ViewBag.ShipmentId = model.ShipmentId;
            ViewBag.ShipmentCode = shipment?.ShipmentCode ?? "";
            ViewBag.ReturnUrl = model.ReturnUrl;
            return View(model);
        }

        var sc = new ShipmentContract
        {
            ShipmentId = model.ShipmentId,
            ContractId = model.ContractId,
            QuantityMt = model.QuantityMt > 0 ? model.QuantityMt : null,
            Notes = string.IsNullOrWhiteSpace(model.Notes) ? null : model.Notes.Trim()
        };

        _db.ShipmentContracts.Add(sc);
        await _db.SaveChangesAsync();

        TempData["ok"] = "قرارداد با موفقیت به محموله اضافه شد.";

        if (TryGetLocalReturnUrl(model.ReturnUrl, out var local)) return Redirect(local);
        return RedirectToAction(nameof(Index), new { shipmentId = model.ShipmentId });
    }

    // POST: /ShipmentContracts/Delete/5
    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id, string? returnUrl = null)
    {
        var sc = await _db.ShipmentContracts.FirstOrDefaultAsync(x => x.Id == id);
        if (sc is null) return NotFound();

        int shipmentId = sc.ShipmentId;
        _db.ShipmentContracts.Remove(sc);
        await _db.SaveChangesAsync();

        TempData["ok"] = "ربط قرارداد حذف شد.";
        if (TryGetLocalReturnUrl(returnUrl, out var local)) return Redirect(local);
        return RedirectToAction(nameof(Index), new { shipmentId });
    }

    private static List<SelectListItem> ToContractOptions(IEnumerable<ContractOptionRow> rows)
        => rows.Select(contract => new SelectListItem
        {
            Value = contract.Id.ToString(),
            Text = ContractUiText.FormatDisplayLabel(contract.ContractName, contract.ContractNumber)
        }).ToList();

    private sealed record ContractOptionRow(int Id, string ContractName, string ContractNumber);
}

// Local ViewModels for Gap#7
public sealed class ShipmentContractRowViewModel
{
    public int Id { get; init; }
    public int ShipmentId { get; init; }
    public int ContractId { get; init; }
    public string ContractName { get; init; } = "";
    public string ContractNumber { get; init; } = "";
    public string DisplayLabel => Contract.BuildDisplayLabel(ContractName, ContractNumber);
    public ContractType ContractType { get; init; }
    public string ProductName { get; init; } = "";
    public decimal? QuantityMt { get; init; }
    public string? Notes { get; init; }
}

public sealed class ShipmentContractCreateViewModel
{
    [System.ComponentModel.DataAnnotations.Display(Name = "قرارداد")]
    [System.ComponentModel.DataAnnotations.Range(1, int.MaxValue, ErrorMessage = "انتخاب قرارداد الزامی است.")]
    public int ContractId { get; set; }

    public int ShipmentId { get; set; }

    [System.ComponentModel.DataAnnotations.Display(Name = "مقدار (MT)")]
    [System.ComponentModel.DataAnnotations.Range(typeof(decimal), "0", "79228162514264337593543950335")]
    public decimal? QuantityMt { get; set; }

    [System.ComponentModel.DataAnnotations.Display(Name = "یادداشت")]
    [System.ComponentModel.DataAnnotations.StringLength(500)]
    public string? Notes { get; set; }

    [System.ComponentModel.DataAnnotations.StringLength(500)]
    public string? ReturnUrl { get; set; }
}
