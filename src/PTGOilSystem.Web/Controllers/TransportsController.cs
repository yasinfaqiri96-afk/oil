using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Helpers;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.InventoryTransport;
using PTGOilSystem.Web.Security;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Exceptions;
using PTGOilSystem.Web.Services.Time;

namespace PTGOilSystem.Web.Controllers;

/// <summary>
/// Facade وبِ «حمل‌ها». این کنترلر فقط انتخاب مسیر و orchestration تراکنش را انجام می‌دهد؛
/// تمام قواعد مقدار/موجودی/فروش/کسری در ITransportWorkflowService باقی می‌ماند.
/// </summary>
[Authorize]
public sealed class TransportsController : Controller
{
    private const decimal Epsilon = 0.0001m;
    private readonly ApplicationDbContext _db;
    private readonly ITransportWorkflowService _workflow;
    private readonly ITransportQuantityService _quantities;
    private readonly IAfghanistanBusinessClock _clock;

    // PTG-P3-B — همان محافظ ضدتکراری فاز P0، برای مسیرهایی که بار واقعی جابه‌جا می‌کنند.
    private readonly IFormTokenGuard? _formTokens;

    public TransportsController(
        ApplicationDbContext db,
        ITransportWorkflowService workflow,
        ITransportQuantityService quantities,
        IAfghanistanBusinessClock clock,
        IFormTokenGuard? formTokens = null)
    {
        _db = db;
        _workflow = workflow;
        _quantities = quantities;
        _clock = clock;
        _formTokens = formTokens;
    }

    public IActionResult Index(string? state = null)
        => string.IsNullOrWhiteSpace(state)
            ? RedirectToAction("Index", "InventoryTransportLegs")
            : Redirect($"/InventoryTransportLegs?Filter.WorkflowState={Uri.EscapeDataString(state)}");

    public IActionResult Details(int id, string? returnUrl = null)
        => RedirectToAction("Details", "InventoryTransportLegs", new { id, returnUrl });

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpGet]
    public async Task<IActionResult> Create(TransportStartSourceKind sourceKind = TransportStartSourceKind.Inventory)
        => View(await BuildStartModelAsync(new TransportStartViewModel { SourceKind = sourceKind }));

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(TransportStartViewModel model)
    {
        switch (model.SourceKind)
        {
            case TransportStartSourceKind.Inventory:
                return RedirectToAction("CreateFromInventory", "InventoryTransportLegs");
            case TransportStartSourceKind.LoadingReceipt when model.LoadingReceiptId is > 0:
                return RedirectToAction(nameof(FromReceipt), new { loadingReceiptId = model.LoadingReceiptId.Value });
            case TransportStartSourceKind.ActiveTransport when model.TransportLegId is > 0:
                return RedirectToAction(nameof(Continue), new { transportLegId = model.TransportLegId.Value });
            case TransportStartSourceKind.LoadingReceipt:
                ModelState.AddModelError(nameof(model.LoadingReceiptId), "رسید/بارگیری مستقیم را انتخاب کنید.");
                break;
            case TransportStartSourceKind.ActiveTransport:
                ModelState.AddModelError(nameof(model.TransportLegId), "حمل در جریان را انتخاب کنید.");
                break;
            default:
                ModelState.AddModelError(nameof(model.SourceKind), "منبع بار معتبر نیست.");
                break;
        }

        return View(await BuildStartModelAsync(model));
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpGet]
    public async Task<IActionResult> FromReceipt(int loadingReceiptId)
    {
        var model = new TransportStartFromReceiptViewModel
        {
            LoadingReceiptId = loadingReceiptId,
            TransportDate = _clock.Today
        };
        if (!await PopulateReceiptModelAsync(model))
        {
            return NotFound();
        }
        model.QuantityMt = model.AvailableQuantityMt;
        await PopulateVehicleLookupsAsync();
        return View(model);
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> FromReceipt(
        TransportStartFromReceiptViewModel model,
        [FromForm(Name = FormTokenHtmlHelper.FieldName)] string? formToken = null)
    {
        if (!await PopulateReceiptModelAsync(model))
        {
            return NotFound();
        }
        if (model.QuantityMt > model.AvailableQuantityMt + Epsilon)
        {
            ModelState.AddModelError(nameof(model.QuantityMt), "مقدار حمل از باقیماندهٔ مستقیم رسید بیشتر است.");
        }

        if (!ModelState.IsValid)
        {
            await PopulateVehicleLookupsAsync();
            return View(model);
        }

        // توکن پیش از فراخوانی سرویس فقط به ChangeTracker اضافه می‌شود و با نخستین
        // SaveChanges داخل همان تراکنش ذخیره می‌گردد؛ اگر سرویس خطا بدهد ذخیره نمی‌شود.
        _formTokens?.Stamp(formToken, "Transport.FromReceipt", nameof(InventoryTransportLeg));

        try
        {
            var leg = await _workflow.StartFromReceiptAsync(new StartTransportFromReceiptCommand
            {
                LoadingReceiptId = model.LoadingReceiptId,
                QuantityMt = model.QuantityMt,
                TransportType = model.TransportType,
                TruckId = model.TruckId,
                WagonId = model.WagonId,
                VesselId = model.VesselId,
                DriverId = model.DriverId,
                ServiceProviderId = model.ServiceProviderId,
                TransportDate = model.TransportDate,
                Reference = model.Reference,
                Notes = model.Notes
            });
            TempData["ok"] = "حمل مستقیم از رسید ثبت شد؛ هیچ خروج مصنوعی موجودی ساخته نشد.";
            return RedirectToAction(nameof(Details), new { id = leg.Id });
        }
        catch (DbUpdateException duplicate) when (_formTokens?.IsDuplicate(duplicate) == true)
        {
            TempData["err"] = "این حمل قبلاً ثبت شده است و دوباره ثبت نشد.";
            return RedirectToAction("Index", "InventoryTransportLegs");
        }
        catch (BusinessRuleException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            await PopulateReceiptModelAsync(model);
            await PopulateVehicleLookupsAsync();
            return View(model);
        }
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpGet]
    public async Task<IActionResult> Continue(int? transportLegId = null)
    {
        var model = new TransportContinueViewModel { TransferDate = _clock.Today };
        await PopulateContinueSourcesAsync(model, transportLegId);
        await PopulateVehicleLookupsAsync();
        return View(model);
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Continue(
        TransportContinueViewModel model,
        [FromForm(Name = FormTokenHtmlHelper.FieldName)] string? formToken = null)
    {
        var requested = (model.Sources ?? [])
            .Where(s => s.Selected && s.LegId > 0 && s.QuantityMt > 0m)
            .Select(s => new ContinueToVehicleSource(s.LegId, s.QuantityMt))
            .ToList();
        if (requested.Count == 0)
        {
            ModelState.AddModelError(nameof(model.Sources), "حداقل یک حمل مبدأ و مقدار آن را انتخاب کنید.");
        }

        if (!ModelState.IsValid)
        {
            await PopulateContinueSourcesAsync(model);
            await PopulateVehicleLookupsAsync();
            return View(model);
        }

        await using var transaction = _db.Database.IsRelational()
            ? await _db.Database.BeginTransactionAsync()
            : null;
        _formTokens?.Stamp(formToken, "Transport.Continue", nameof(InventoryTransportLeg));

        try
        {
            var result = await _workflow.ContinueToVehicleAsync(new ContinueToVehicleCommand
            {
                Sources = requested,
                TargetTransportType = model.TargetTransportType,
                TargetTruckId = model.TargetTruckId,
                TargetWagonId = model.TargetWagonId,
                TargetVesselId = model.TargetVesselId,
                DriverId = model.DriverId,
                TransferDate = model.TransferDate,
                TicketSerialNumber = model.TicketSerialNumber,
                Notes = model.Notes
            });
            if (transaction is not null)
            {
                await transaction.CommitAsync();
            }
            TempData["ok"] = requested.Count > 1
                ? "ادغام حمل‌ها در وسیلهٔ مقصد ثبت شد؛ هیچ حرکت موجودی ساخته نشد."
                : "انتقال به وسیلهٔ بعدی ثبت شد؛ هیچ حرکت موجودی ساخته نشد.";
            return RedirectToAction(nameof(Details), new { id = result.ChildLeg.Id });
        }
        catch (DbUpdateException duplicate) when (_formTokens?.IsDuplicate(duplicate) == true)
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync();
            }
            TempData["err"] = "این انتقال قبلاً ثبت شده است و دوباره ثبت نشد.";
            return RedirectToAction("Index", "InventoryTransportLegs");
        }
        catch (BusinessRuleException ex)
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync();
            }
            ModelState.AddModelError(string.Empty, ex.Message);
            await PopulateContinueSourcesAsync(model);
            await PopulateVehicleLookupsAsync();
            return View(model);
        }
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SettleFreight(
        TransportFreightSettlementViewModel model,
        [FromForm(Name = FormTokenHtmlHelper.FieldName)] string? formToken = null)
    {
        if (!ModelState.IsValid)
        {
            TempData["err"] = "اطلاعات تسویهٔ کرایه کامل یا معتبر نیست.";
            return RedirectToAction(nameof(Details), new { id = model.TransportLegId });
        }

        // تسویهٔ کرایه یک مصرف واقعی می‌سازد؛ ارسال دوم نباید کرایه را دو برابر کند.
        _formTokens?.Stamp(formToken, "Transport.SettleFreight", nameof(InventoryTransportLeg));

        try
        {
            await _workflow.SettleFreightAsync(new SettleTransportFreightCommand
            {
                TransportLegId = model.TransportLegId,
                SettlementDate = model.SettlementDate,
                FreightRateUsdPerMt = model.FreightRateUsdPerMt,
                FreightCostUsd = model.FreightCostUsd,
                Notes = model.Notes
            });
            TempData["ok"] = "کرایهٔ حمل تسویه شد؛ موجودی و وضعیت فیزیکی بار تغییر نکرد.";
        }
        catch (DbUpdateException duplicate) when (_formTokens?.IsDuplicate(duplicate) == true)
        {
            TempData["err"] = "کرایهٔ این حمل قبلاً تسویه شده است و دوباره ثبت نشد.";
        }
        catch (BusinessRuleException ex)
        {
            TempData["err"] = ex.Message;
        }
        return RedirectToAction(nameof(Details), new { id = model.TransportLegId });
    }

    private async Task<TransportStartViewModel> BuildStartModelAsync(TransportStartViewModel model)
    {
        model.LoadingReceipts = await LoadReceiptOptionsAsync();
        model.ActiveTransports = (await LoadActiveTransportRowsAsync())
            .Select(x => new TransportLookupItem(x.LegId, x.Label, x.RemainingQuantityMt))
            .ToList();
        return model;
    }

    private async Task<IReadOnlyList<TransportLookupItem>> LoadReceiptOptionsAsync()
    {
        var receipts = await _db.LoadingReceipts
            .AsNoTracking()
            .Where(r => !r.IsCancelled
                && (r.ReceiptDestination == LoadingReceiptDestination.DirectDispatch
                    || r.ReceiptDestination == LoadingReceiptDestination.Mixed))
            .Select(r => new
            {
                r.Id,
                r.ReceiptDate,
                Reference = r.ReferenceDocument,
                Product = r.LoadingRegister != null && r.LoadingRegister.Product != null
                    ? r.LoadingRegister.Product.Name
                    : "",
                QuantityMt = r.Allocations
                    .Where(a => a.Destination == LoadingReceiptAllocationDestination.DirectDispatchToTruck
                        && a.Status != LoadingReceiptAllocationStatus.Cancelled)
                    .Sum(a => a.QuantityMt)
            })
            .OrderByDescending(r => r.ReceiptDate)
            .ThenByDescending(r => r.Id)
            .Take(250)
            .ToListAsync();

        if (receipts.Count == 0)
        {
            return [];
        }
        var ids = receipts.Select(r => r.Id).ToList();
        var used = await _db.InventoryTransportLegAllocations
            .AsNoTracking()
            .Where(a => a.SourceLoadingReceiptId.HasValue
                && ids.Contains(a.SourceLoadingReceiptId.Value)
                && a.InventoryTransportLeg != null
                && a.InventoryTransportLeg.Status != InventoryTransportLegStatus.Cancelled)
            .GroupBy(a => a.SourceLoadingReceiptId!.Value)
            .Select(g => new { ReceiptId = g.Key, QuantityMt = g.Sum(a => a.QuantityMt) })
            .ToDictionaryAsync(x => x.ReceiptId, x => x.QuantityMt);

        return receipts
            .Select(r => new
            {
                Row = r,
                Available = decimal.Round(Math.Max(r.QuantityMt - used.GetValueOrDefault(r.Id), 0m), 4)
            })
            .Where(x => x.Available > Epsilon)
            .Select(x => new TransportLookupItem(
                x.Row.Id,
                $"{(string.IsNullOrWhiteSpace(x.Row.Reference) ? $"رسید #{x.Row.Id}" : x.Row.Reference)} — {x.Row.Product} — {x.Available:N4} MT",
                x.Available))
            .ToList();
    }

    private async Task<bool> PopulateReceiptModelAsync(TransportStartFromReceiptViewModel model)
    {
        var option = (await LoadReceiptOptionsAsync()).FirstOrDefault(x => x.Id == model.LoadingReceiptId);
        if (option is null)
        {
            return false;
        }
        var product = await _db.LoadingReceipts.AsNoTracking()
            .Where(r => r.Id == model.LoadingReceiptId)
            .Select(r => r.LoadingRegister != null && r.LoadingRegister.Product != null
                ? r.LoadingRegister.Product.Name
                : "")
            .FirstAsync();
        model.ReceiptLabel = option.Label;
        model.ProductName = product;
        model.AvailableQuantityMt = option.AvailableQuantityMt;
        return true;
    }

    private async Task PopulateContinueSourcesAsync(TransportContinueViewModel model, int? selectedLegId = null)
    {
        var posted = (model.Sources ?? []).ToDictionary(s => s.LegId);
        var rows = await LoadActiveTransportRowsAsync();
        model.Sources = rows.Select(row =>
        {
            posted.TryGetValue(row.LegId, out var input);
            var selected = input?.Selected == true || selectedLegId == row.LegId;
            return new TransportContinueSourceInput
            {
                LegId = row.LegId,
                Label = row.Label,
                ProductName = row.ProductName,
                RemainingQuantityMt = row.RemainingQuantityMt,
                Selected = selected,
                QuantityMt = input?.QuantityMt > 0m
                    ? Math.Min(input.QuantityMt, row.RemainingQuantityMt)
                    : selected ? row.RemainingQuantityMt : 0m
            };
        }).ToList();
    }

    private async Task<IReadOnlyList<ActiveTransportRow>> LoadActiveTransportRowsAsync()
    {
        var legs = await _db.InventoryTransportLegs.AsNoTracking()
            .Where(l => l.Status == InventoryTransportLegStatus.Loaded
                || l.Status == InventoryTransportLegStatus.InTransit)
            .OrderByDescending(l => l.LoadedDate)
            .ThenByDescending(l => l.Id)
            .Select(l => new
            {
                l.Id,
                l.TransportType,
                ProductName = l.Product != null ? l.Product.Name : "",
                Contract = l.SourcePurchaseContract != null ? l.SourcePurchaseContract.ContractNumber : "",
                Vehicle = l.Truck != null ? l.Truck.PlateNumber
                    : l.Wagon != null ? l.Wagon.WagonNumber
                    : l.Vessel != null ? l.Vessel.Name
                    : l.WagonNumber,
                l.LoadedDate
            })
            .Take(500)
            .ToListAsync();
        var remaining = await _quantities.GetRemainingMtAsync(legs.Select(l => l.Id).ToList());
        return legs
            .Select(l => new ActiveTransportRow(
                l.Id,
                l.ProductName,
                $"حمل #{l.Id} — {TransportTypeText(l.TransportType)} {(string.IsNullOrWhiteSpace(l.Vehicle) ? "" : l.Vehicle)} — {l.ProductName} — {l.Contract}",
                remaining.GetValueOrDefault(l.Id)))
            .Where(l => l.RemainingQuantityMt > Epsilon)
            .ToList();
    }

    private async Task PopulateVehicleLookupsAsync()
    {
        ViewBag.Trucks = new SelectList(
            await _db.Trucks.AsNoTracking().Where(t => t.IsActive).OrderBy(t => t.PlateNumber).ToListAsync(),
            "Id", "PlateNumber");
        ViewBag.Wagons = new SelectList(
            await _db.Wagons.AsNoTracking().Where(w => w.IsActive).OrderBy(w => w.WagonNumber).ToListAsync(),
            "Id", "WagonNumber");
        ViewBag.Vessels = new SelectList(
            await _db.Vessels.AsNoTracking().Where(v => v.IsActive).OrderBy(v => v.Name).ToListAsync(),
            "Id", "Name");
        ViewBag.Drivers = new SelectList(
            await _db.Drivers.AsNoTracking().Where(d => d.IsActive).OrderBy(d => d.FullName).ToListAsync(),
            "Id", "FullName");
        ViewBag.ServiceProviders = new SelectList(
            await _db.ServiceProviders.AsNoTracking().Where(p => p.IsActive).OrderBy(p => p.Name).ToListAsync(),
            "Id", "Name");
    }

    private static string TransportTypeText(LoadingTransportType type) => type switch
    {
        LoadingTransportType.Truck => "موتر",
        LoadingTransportType.Wagon => "واگن",
        LoadingTransportType.Vessel => "کشتی",
        _ => "وسیله"
    };

    private sealed record ActiveTransportRow(
        int LegId,
        string ProductName,
        string Label,
        decimal RemainingQuantityMt);
}
