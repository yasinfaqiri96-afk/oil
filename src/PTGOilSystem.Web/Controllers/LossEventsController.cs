using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Helpers;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.LossEvents;
using PTGOilSystem.Web.Security;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Audit;
using PTGOilSystem.Web.Services.Exceptions;
using PTGOilSystem.Web.Services.Time;

namespace PTGOilSystem.Web.Controllers;

[Authorize]
public partial class LossEventsController : Controller
{
    private readonly ApplicationDbContext _db;
    private readonly IStockService _stock;
    private readonly IAuditService _audit;
    private readonly ILogger<LossEventsController> _logger;
    private readonly Services.Accounting.IInventoryLossAccountingAdapter? _lossAccounting;

    public LossEventsController(
        ApplicationDbContext db,
        IStockService stock,
        IAuditService audit,
        ILogger<LossEventsController> logger,
        Services.Accounting.IInventoryLossAccountingAdapter? lossAccounting = null)
    {
        _db = db;
        _stock = stock;
        _audit = audit;
        _logger = logger;
        _lossAccounting = lossAccounting;
    }

    private bool TryGetLocalReturnUrl(string? returnUrl, out string localReturnUrl)
    {
        if (!string.IsNullOrWhiteSpace(returnUrl) && Url?.IsLocalUrl(returnUrl) == true)
        {
            localReturnUrl = returnUrl;
            return true;
        }

        localReturnUrl = string.Empty;
        return false;
    }

    public async Task<IActionResult> Index([FromQuery] LossEventIndexFilterViewModel? filter = null, int page = 1, [FromQuery(Name = "pageSize")] int? perPage = null)
    {
        var pageSize = ListPageSize.Resolve(perPage, 5);
        ViewData["PageSize"] = pageSize;
        ViewData["DefaultPageSize"] = 5;
        var exportAll = page <= 0;
        filter ??= new LossEventIndexFilterViewModel();
        await PopulateLookupsAsync(filter: filter);

        var query = _db.LossEvents
            .AsNoTracking()
            .Include(e => e.Product)
            .Include(e => e.Contract)
            .Include(e => e.Shipment)
            .AsQueryable();

        query = query.Where(e => !e.IsCancelled);

        if (filter.FromDate.HasValue) query = query.Where(e => e.EventDate >= filter.FromDate.Value);
        if (filter.ToDate.HasValue) query = query.Where(e => e.EventDate <= filter.ToDate.Value);
        if (filter.ProductId.HasValue) query = query.Where(e => e.ProductId == filter.ProductId.Value);
        if (filter.ContractId.HasValue) query = query.Where(e => e.ContractId == filter.ContractId.Value);
        if (filter.Stage.HasValue) query = query.Where(e => e.Stage == filter.Stage.Value);
        if (!string.IsNullOrWhiteSpace(filter.ResponsiblePartyName))
        {
            var responsibleParty = filter.ResponsiblePartyName.Trim();
            query = query.Where(e => e.ResponsiblePartyName != null && e.ResponsiblePartyName.Contains(responsibleParty));
        }
        if (filter.AffectsInventory.HasValue) query = query.Where(e => e.AffectsInventory == filter.AffectsInventory.Value);

        // تفاوت مثبت = کسری، تفاوت منفی = اضافه‌بار (تخلیهٔ بیشتر از بارگیری).
        if (filter.Variance == LossEventVarianceFilter.ShortageOnly)
        {
            query = query.Where(e => e.DifferenceQuantityMt > 0m);
        }
        else if (filter.Variance == LossEventVarianceFilter.SurplusOnly)
        {
            query = query.Where(e => e.DifferenceQuantityMt < 0m);
        }

        var totalCount = await query.CountAsync();
        var pageCount = Math.Max(1, (int)Math.Ceiling(totalCount / (double)pageSize));
        page = Math.Clamp(page, 1, pageCount);

        var items = await query
            .OrderByDescending(e => e.EventDate)
            .ThenByDescending(e => e.Id)
            .Skip(exportAll ? 0 : (page - 1) * pageSize)
            .Take(exportAll ? totalCount : pageSize)
            .Select(e => new LossEventListItemViewModel
            {
                Id = e.Id,
                EventDate = e.EventDate,
                Stage = e.Stage,
                ProductName = e.Product != null ? e.Product.Name : "",
                ContractNumber = e.Contract != null ? e.Contract.ContractNumber : null,
                ShipmentCode = e.Shipment != null ? e.Shipment.ShipmentCode : null,
                DifferenceQuantityMt = e.DifferenceQuantityMt,
                AllowableLossMt = e.AllowableLossMt,
                ChargeableLossMt = e.ChargeableLossMt,
                AffectsInventory = e.AffectsInventory,
                ResponsiblePartyName = e.ResponsiblePartyName,
                // نمبر وسیله از همان مرحله‌ای که کسری روی آن ثبت شده.
                VehicleNumber = e.TruckDispatch != null && e.TruckDispatch.Truck != null
                    ? e.TruckDispatch.Truck.PlateNumber
                    : e.TransportLeg != null
                        ? (e.TransportLeg.Truck != null
                            ? e.TransportLeg.Truck.PlateNumber
                            : e.TransportLeg.WagonNumber ?? e.TransportLeg.RwbNo)
                        : e.LoadingRegister != null
                            ? (e.LoadingRegister.Truck != null
                                ? e.LoadingRegister.Truck.PlateNumber
                                : e.LoadingRegister.WagonNumber ?? e.LoadingRegister.RwbNo)
                            : e.LoadingReceipt != null && e.LoadingReceipt.LoadingRegister != null
                                ? (e.LoadingReceipt.LoadingRegister.Truck != null
                                    ? e.LoadingReceipt.LoadingRegister.Truck.PlateNumber
                                    : e.LoadingReceipt.LoadingRegister.WagonNumber ?? e.LoadingReceipt.LoadingRegister.RwbNo)
                                : null
            })
            .ToListAsync();

        // آمار کامل روی همهٔ رکوردهای مطابق فیلتر (نه فقط این صفحه) برای کارت‌های آماری و ردیف جمع.
        // کسری و اضافه‌بار در دو جمعِ جدا می‌مانند تا یکدیگر را خنثی نکنند؛ «خالص» جداگانه محاسبه می‌شود.
        ViewBag.SumChargeableLoss = await query.SumAsync(e => e.ChargeableLossMt);
        ViewBag.SumShortageQuantity = await query.SumAsync(e => e.DifferenceQuantityMt > 0m ? e.DifferenceQuantityMt : 0m);
        ViewBag.SumSurplusQuantity = await query.SumAsync(e => e.DifferenceQuantityMt < 0m ? -e.DifferenceQuantityMt : 0m);
        ViewBag.SumDifferenceQuantity = await query.SumAsync(e => e.DifferenceQuantityMt);
        ViewBag.NeedsReviewCount = await query.CountAsync(e => e.ChargeableLossMt > 0m);

        return View(new LossEventIndexViewModel
        {
            Filter = filter,
            Items = items,
            CurrentPage = page,
            PageCount = pageCount,
            TotalCount = totalCount
        });
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Cancel(int id, string? returnUrl = null)
    {
        var item = await _db.LossEvents
            .FirstOrDefaultAsync(e => e.Id == id);

        if (item is null)
        {
            return NotFound();
        }

        if (item.IsCancelled)
        {
            TempData["ok"] = "این رویداد قبلاً لغو شده است.";
            if (!string.IsNullOrWhiteSpace(returnUrl) && Url?.IsLocalUrl(returnUrl) == true)
                return Redirect(returnUrl);
            return RedirectToAction(nameof(Details), new { id });
        }

        item.IsCancelled = true;

        if (item.InventoryMovementId.HasValue)
        {
            var linkedMovement = await _db.InventoryMovements
                .AsNoTracking()
                .FirstOrDefaultAsync(m => m.Id == item.InventoryMovementId.Value);

            if (linkedMovement is not null)
            {
                var reversal = new InventoryMovement
                {
                    ProductId = linkedMovement.ProductId,
                    ContractId = linkedMovement.ContractId,
                    TerminalId = linkedMovement.TerminalId,
                    StorageTankId = linkedMovement.StorageTankId,
                    Direction = MovementDirection.In,
                    MovementDate = AfghanistanBusinessClock.SystemToday,
                    QuantityMt = linkedMovement.QuantityMt,
                    ReferenceDocument = (linkedMovement.ReferenceDocument ?? $"LOSS-{item.Id}") + "-CANCEL",
                    Notes = $"Reversal for cancelled LossEventId={item.Id}"
                };

                _db.InventoryMovements.Add(reversal);
            }
        }

        await _db.SaveChangesAsync();

        if (_lossAccounting is not null)
        {
            await _lossAccounting.TryPostLossReversalAsync(item);
        }

        TempData["ok"] = "رویداد ضایعات لغو شد.";
        if (!string.IsNullOrWhiteSpace(returnUrl) && Url?.IsLocalUrl(returnUrl) == true)
            return Redirect(returnUrl);

        return RedirectToAction(nameof(Index));
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    public async Task<IActionResult> Create(
        int? contractId = null,
        int? loadingRegisterId = null,
        int? shipmentId = null,
        int? transportLegId = null,
        string? returnUrl = null)
    {
        var model = new LossEventCreateViewModel
        {
            EventDate = AfghanistanBusinessClock.SystemToday,
            Stage = LossEventStage.ReceiptShortage
        };

        if (loadingRegisterId.HasValue)
        {
            var loading = await _db.LoadingRegisters
                .AsNoTracking()
                .FirstOrDefaultAsync(l => l.Id == loadingRegisterId.Value);
            if (loading is not null)
            {
                model.LoadingRegisterId = loading.Id;
                model.ContractId = loading.ContractId;
                model.ProductId = loading.ProductId;
                model.Stage = LossEventStage.LoadingDifference;
            }
        }
        else if (contractId.HasValue)
        {
            var contract = await _db.Contracts
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == contractId.Value);
            if (contract is not null)
            {
                model.ContractId = contract.Id;
                model.ProductId = contract.ProductId;
            }
        }

        // پیش‌پُرکردن اختیاری از پروندهٔ کشتی/محموله یا حملِ منبع (بدون اجبار؛
        // منطق ذخیره و اعتبارسنجی تغییر نمی‌کند — فقط فیلدهای موجود از قبل ست می‌شوند).
        int? prefillShipmentId = shipmentId;
        if (transportLegId.HasValue)
        {
            var leg = await _db.InventoryTransportLegs
                .AsNoTracking()
                .FirstOrDefaultAsync(l => l.Id == transportLegId.Value);
            if (leg is not null)
            {
                if (model.ProductId <= 0)
                {
                    model.ProductId = leg.ProductId;
                }
                prefillShipmentId ??= leg.ShipmentId;
            }
        }

        if (prefillShipmentId.HasValue)
        {
            var shipment = await _db.Shipments
                .AsNoTracking()
                .Include(s => s.Contract)
                .FirstOrDefaultAsync(s => s.Id == prefillShipmentId.Value);
            if (shipment is not null)
            {
                model.ShipmentId = shipment.Id;
                if (model.ProductId <= 0 && shipment.Contract is not null)
                {
                    model.ProductId = shipment.Contract.ProductId;
                }
            }
        }

        model.ReturnUrl = returnUrl;

        await PopulateLookupsAsync(createModel: model);
        return View(model);
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    public async Task<IActionResult> Edit(int id, string? returnUrl = null)
    {
        var item = await LoadLossEventForEditAsync(id);
        if (item is null)
        {
            return NotFound();
        }

        if (item.IsCancelled)
        {
            TempData["ok"] = "ویرایش برای رویداد لغوشده در دسترس نیست.";
            return RedirectToAction(nameof(Details), new { id, returnUrl });
        }

        if (item.InventoryMovementId.HasValue || item.AffectsInventory)
        {
            TempData["ok"] = "ویرایش ضایعاتی که روی موجودی اثر گذاشته‌اند فعلاً از این صفحه پشتیبانی نمی‌شود.";
            return RedirectToAction(nameof(Details), new { id, returnUrl });
        }

        PopulateLossEventEditContext(item);
        return View(BuildEditModel(item, returnUrl));
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(LossEventCreateViewModel model)
    {
        var normalizedReference = TrimToNull(model.Reference);
        var normalizedNotes = TrimToNull(model.Notes);
        var normalizedResponsiblePartyType = TrimToNull(model.ResponsiblePartyType);
        var normalizedResponsiblePartyName = TrimToNull(model.ResponsiblePartyName);
        var normalizedFinancialTreatment = TrimToNull(model.FinancialTreatment);

        var product = await _db.Products.AsNoTracking().FirstOrDefaultAsync(p => p.Id == model.ProductId && p.IsActive);
        if (product is null)
        {
            ModelState.AddModelError(nameof(model.ProductId), "کالای انتخاب‌شده معتبر نیست.");
        }

        Contract? contract = null;
        if (model.ContractId.HasValue)
        {
            contract = await _db.Contracts.AsNoTracking().FirstOrDefaultAsync(c => c.Id == model.ContractId.Value);
            if (contract is null)
            {
                ModelState.AddModelError(nameof(model.ContractId), "قرارداد انتخاب‌شده معتبر نیست.");
            }
            else if (contract.ProductId != model.ProductId)
            {
                ModelState.AddModelError(nameof(model.ContractId), "قرارداد انتخاب‌شده با کالای انتخابی هم‌خوان نیست.");
            }
        }

        if (model.ShipmentId.HasValue)
        {
            var shipment = await _db.Shipments.AsNoTracking().FirstOrDefaultAsync(s => s.Id == model.ShipmentId.Value);
            if (shipment is null)
            {
                ModelState.AddModelError(nameof(model.ShipmentId), "Shipment انتخاب‌شده معتبر نیست.");
            }
            else if (model.ContractId.HasValue && shipment.ContractId != model.ContractId)
            {
                ModelState.AddModelError(nameof(model.ShipmentId), "Shipment انتخاب‌شده با قرارداد انتخابی هم‌خوان نیست.");
            }
        }

        if (model.LoadingRegisterId.HasValue)
        {
            var loading = await _db.LoadingRegisters.AsNoTracking().FirstOrDefaultAsync(l => l.Id == model.LoadingRegisterId.Value);
            if (loading is null)
            {
                ModelState.AddModelError(nameof(model.LoadingRegisterId), "Loading انتخاب‌شده معتبر نیست.");
            }
            else
            {
                if (loading.ProductId != model.ProductId)
                {
                    ModelState.AddModelError(nameof(model.LoadingRegisterId), "Loading انتخاب‌شده با کالای انتخابی هم‌خوان نیست.");
                }

                if (model.ContractId.HasValue && loading.ContractId != model.ContractId)
                {
                    ModelState.AddModelError(nameof(model.LoadingRegisterId), "Loading انتخاب‌شده با قرارداد انتخابی هم‌خوان نیست.");
                }
            }
        }

        if (model.LoadingReceiptId.HasValue)
        {
            var receipt = await _db.LoadingReceipts
                .AsNoTracking()
                .Include(r => r.LoadingRegister)
                .FirstOrDefaultAsync(r => r.Id == model.LoadingReceiptId.Value);
            if (receipt is null)
            {
                ModelState.AddModelError(nameof(model.LoadingReceiptId), "Loading Receipt انتخاب‌شده معتبر نیست.");
            }
            else if (receipt.LoadingRegister is not null)
            {
                if (receipt.LoadingRegister.ProductId != model.ProductId)
                {
                    ModelState.AddModelError(nameof(model.LoadingReceiptId), "Loading Receipt انتخاب‌شده با کالای انتخابی هم‌خوان نیست.");
                }

                if (model.ContractId.HasValue && receipt.LoadingRegister.ContractId != model.ContractId)
                {
                    ModelState.AddModelError(nameof(model.LoadingReceiptId), "Loading Receipt انتخاب‌شده با قرارداد انتخابی هم‌خوان نیست.");
                }
            }
        }

        if (model.TruckDispatchId.HasValue)
        {
            var dispatch = await _db.TruckDispatches.AsNoTracking().FirstOrDefaultAsync(d => d.Id == model.TruckDispatchId.Value);
            if (dispatch is null)
            {
                ModelState.AddModelError(nameof(model.TruckDispatchId), "Dispatch انتخاب‌شده معتبر نیست.");
            }
            else
            {
                if (dispatch.ProductId != model.ProductId)
                {
                    ModelState.AddModelError(nameof(model.TruckDispatchId), "Dispatch انتخاب‌شده با کالای انتخابی هم‌خوان نیست.");
                }

                if (model.ContractId.HasValue && dispatch.ContractId != model.ContractId)
                {
                    ModelState.AddModelError(nameof(model.TruckDispatchId), "Dispatch انتخاب‌شده با قرارداد انتخابی هم‌خوان نیست.");
                }
            }
        }

        if (model.SalesTransactionId.HasValue)
        {
            var sale = await _db.SalesTransactions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == model.SalesTransactionId.Value);
            if (sale is null)
            {
                ModelState.AddModelError(nameof(model.SalesTransactionId), "Sale انتخاب‌شده معتبر نیست.");
            }
            else
            {
                if (sale.ProductId != model.ProductId)
                {
                    ModelState.AddModelError(nameof(model.SalesTransactionId), "Sale انتخاب‌شده با کالای انتخابی هم‌خوان نیست.");
                }

                if (model.ContractId.HasValue && sale.ContractId != model.ContractId)
                {
                    ModelState.AddModelError(nameof(model.SalesTransactionId), "Sale انتخاب‌شده با قرارداد انتخابی هم‌خوان نیست.");
                }
            }
        }

        if (model.AffectsInventory)
        {
            if (!CanAffectInventory(model.Stage))
            {
                ModelState.AddModelError(nameof(model.AffectsInventory), "این مرحله فقط برای گزارش است و نباید موجودی را دوباره کم کند.");
            }

            if (!model.TerminalId.HasValue)
            {
                ModelState.AddModelError(nameof(model.TerminalId), "برای ثبت اثر روی موجودی، انتخاب ترمینال الزامی است.");
            }

            if (!model.StorageTankId.HasValue)
            {
                ModelState.AddModelError(nameof(model.StorageTankId), "برای ثبت اثر روی موجودی، انتخاب مخزن الزامی است.");
            }
        }

        if (model.TerminalId.HasValue)
        {
            var terminal = await _db.Terminals.AsNoTracking().FirstOrDefaultAsync(t => t.Id == model.TerminalId.Value && t.IsActive);
            if (terminal is null)
            {
                ModelState.AddModelError(nameof(model.TerminalId), "ترمینال انتخاب‌شده معتبر نیست.");
            }
        }

        if (model.StorageTankId.HasValue)
        {
            var tank = await _db.StorageTanks.AsNoTracking().FirstOrDefaultAsync(t => t.Id == model.StorageTankId.Value);
            if (tank is null)
            {
                ModelState.AddModelError(nameof(model.StorageTankId), "مخزن انتخاب‌شده معتبر نیست.");
            }
            else
            {
                if (model.TerminalId.HasValue && tank.TerminalId != model.TerminalId.Value)
                {
                    ModelState.AddModelError(nameof(model.StorageTankId), "مخزن انتخاب‌شده به ترمینال انتخابی تعلق ندارد.");
                }

                if (tank.ProductId.HasValue && tank.ProductId != model.ProductId)
                {
                    ModelState.AddModelError(nameof(model.StorageTankId), "مخزن انتخاب‌شده برای کالای انتخابی تعریف نشده است.");
                }
            }
        }

        if (model.ToleranceQuantityMt < 0m)
        {
            ModelState.AddModelError(nameof(model.ToleranceQuantityMt), "تلورانس نامعتبر است.");
        }

        var metrics = ComputeLossMetrics(
            model.ExpectedQuantityMt,
            model.ActualQuantityMt,
            model.ToleranceQuantityMt);
        var inventoryLossMt = Math.Max(metrics.DifferenceQuantityMt, 0m);

        if (model.AffectsInventory && inventoryLossMt <= 0m)
        {
            ModelState.AddModelError(nameof(model.AffectsInventory), "برای کاهش موجودی باید اختلاف مثبت باشد.");
        }

        if (!ModelState.IsValid)
        {
            model.Reference = normalizedReference;
            model.Notes = normalizedNotes;
            model.ResponsiblePartyType = normalizedResponsiblePartyType;
            model.ResponsiblePartyName = normalizedResponsiblePartyName;
            model.FinancialTreatment = normalizedFinancialTreatment;
            await PopulateLookupsAsync(createModel: model);
            return View(model);
        }

        InventoryMovement? movement = null;
        if (model.AffectsInventory)
        {
            movement = new InventoryMovement
            {
                ProductId = model.ProductId,
                ContractId = model.ContractId,
                TerminalId = model.TerminalId!.Value,
                StorageTankId = model.StorageTankId,
                Direction = MovementDirection.Out,
                MovementDate = model.EventDate,
                QuantityMt = inventoryLossMt,
                ReferenceDocument = normalizedReference ?? $"LOSS-{model.EventDate:yyyyMMdd}",
                Notes = BuildInventoryNotes(model.Stage, normalizedNotes)
            };
        }

        try
        {
            if (movement is not null)
            {
                await _stock.EnsureSufficientStockForMovementAsync(movement);
            }

            IDbContextTransaction? transaction = null;
            if (_db.Database.IsRelational())
            {
                transaction = await _db.Database.BeginTransactionAsync();
            }

            try
            {
                var lossEvent = new LossEvent
                {
                    Stage = model.Stage,
                    ProductId = model.ProductId,
                    ContractId = model.ContractId,
                    ShipmentId = model.ShipmentId,
                    LoadingRegisterId = model.LoadingRegisterId,
                    LoadingReceiptId = model.LoadingReceiptId,
                    TruckDispatchId = model.TruckDispatchId,
                    SalesTransactionId = model.SalesTransactionId,
                    TerminalId = model.TerminalId,
                    StorageTankId = model.StorageTankId,
                    EventDate = model.EventDate,
                    ExpectedQuantityMt = model.ExpectedQuantityMt,
                    ActualQuantityMt = model.ActualQuantityMt,
                    DifferenceQuantityMt = metrics.DifferenceQuantityMt,
                    ToleranceQuantityMt = model.ToleranceQuantityMt,
                    AllowableLossMt = metrics.AllowableLossMt,
                    ChargeableLossMt = metrics.ChargeableLossMt,
                    ResponsiblePartyType = normalizedResponsiblePartyType,
                    ResponsiblePartyName = normalizedResponsiblePartyName,
                    FinancialTreatment = normalizedFinancialTreatment,
                    AffectsInventory = model.AffectsInventory,
                    Reference = normalizedReference,
                    Notes = normalizedNotes
                };

                _db.LossEvents.Add(lossEvent);
                await _db.SaveChangesAsync();

                if (movement is not null)
                {
                    movement.Notes = BuildInventoryNotes(model.Stage, $"LossEventId={lossEvent.Id}" + (string.IsNullOrWhiteSpace(normalizedNotes) ? string.Empty : $" | {normalizedNotes}"));
                    _db.InventoryMovements.Add(movement);
                    await _db.SaveChangesAsync();

                    lossEvent.InventoryMovementId = movement.Id;
                    await _db.SaveChangesAsync();
                }

                await _audit.LogAsync(
                    nameof(LossEvent),
                    lossEvent.Id,
                    AuditAction.Insert,
                    diff: AuditDiffFormatter.ForCreate(
                        ("Stage", lossEvent.Stage),
                        ("ProductId", lossEvent.ProductId),
                        ("ContractId", lossEvent.ContractId),
                        ("ShipmentId", lossEvent.ShipmentId),
                        ("LoadingRegisterId", lossEvent.LoadingRegisterId),
                        ("LoadingReceiptId", lossEvent.LoadingReceiptId),
                        ("TruckDispatchId", lossEvent.TruckDispatchId),
                        ("SalesTransactionId", lossEvent.SalesTransactionId),
                        ("TerminalId", lossEvent.TerminalId),
                        ("StorageTankId", lossEvent.StorageTankId),
                        ("EventDate", lossEvent.EventDate),
                        ("ExpectedQuantityMt", lossEvent.ExpectedQuantityMt),
                        ("ActualQuantityMt", lossEvent.ActualQuantityMt),
                        ("DifferenceQuantityMt", lossEvent.DifferenceQuantityMt),
                        ("ToleranceQuantityMt", lossEvent.ToleranceQuantityMt),
                        ("AllowableLossMt", lossEvent.AllowableLossMt),
                        ("ChargeableLossMt", lossEvent.ChargeableLossMt),
                        ("AffectsInventory", lossEvent.AffectsInventory),
                        ("InventoryMovementId", lossEvent.InventoryMovementId),
                        ("Reference", lossEvent.Reference)));

                if (movement is not null)
                {
                    await _audit.LogAsync(
                        nameof(InventoryMovement),
                        movement.Id,
                        AuditAction.Insert,
                        diff: AuditDiffFormatter.ForCreate(
                            ("ProductId", movement.ProductId),
                            ("ContractId", movement.ContractId),
                            ("TerminalId", movement.TerminalId),
                            ("StorageTankId", movement.StorageTankId),
                            ("Direction", movement.Direction),
                            ("QuantityMt", movement.QuantityMt),
                            ("MovementDate", movement.MovementDate),
                            ("ReferenceDocument", movement.ReferenceDocument),
                            ("LossEventStage", model.Stage)));
                }

                await _db.SaveChangesAsync();

                if (_lossAccounting is not null)
                {
                    await _lossAccounting.TryPostLossAsync(lossEvent);
                }

                if (transaction is not null)
                {
                    await transaction.CommitAsync();
                }

                TempData["ok"] = "Loss Event با موفقیت ثبت شد.";
                if (TryGetLocalReturnUrl(model.ReturnUrl, out var localReturnUrl))
                {
                    return Redirect(localReturnUrl);
                }

                return RedirectToAction(nameof(Details), new { id = lossEvent.Id });
            }
            catch
            {
                if (transaction is not null)
                {
                    await transaction.RollbackAsync();
                }

                throw;
            }
        }
        catch (BusinessRuleException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create loss event.");
            ModelState.AddModelError(string.Empty, "ثبت Loss Event انجام نشد. لطفاً اطلاعات را بررسی و دوباره تلاش کنید.");
        }

        model.Reference = normalizedReference;
        model.Notes = normalizedNotes;
        model.ResponsiblePartyType = normalizedResponsiblePartyType;
        model.ResponsiblePartyName = normalizedResponsiblePartyName;
        model.FinancialTreatment = normalizedFinancialTreatment;
        await PopulateLookupsAsync(createModel: model);
        return View(model);
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, LossEventCreateViewModel model)
    {
        var item = await LoadLossEventForEditAsync(id);
        if (item is null)
        {
            return NotFound();
        }

        if (item.IsCancelled)
        {
            TempData["ok"] = "ویرایش برای رویداد لغوشده در دسترس نیست.";
            return RedirectToAction(nameof(Details), new { id, returnUrl = model.ReturnUrl });
        }

        if (item.InventoryMovementId.HasValue || item.AffectsInventory)
        {
            TempData["ok"] = "ویرایش ضایعاتی که روی موجودی اثر گذاشته‌اند فعلاً از این صفحه پشتیبانی نمی‌شود.";
            return RedirectToAction(nameof(Details), new { id, returnUrl = model.ReturnUrl });
        }

        var normalizedReference = TrimToNull(model.Reference);
        var normalizedNotes = TrimToNull(model.Notes);
        var normalizedResponsiblePartyType = TrimToNull(model.ResponsiblePartyType);
        var normalizedResponsiblePartyName = TrimToNull(model.ResponsiblePartyName);
        var normalizedFinancialTreatment = TrimToNull(model.FinancialTreatment);

        if (model.ToleranceQuantityMt < 0m)
        {
            ModelState.AddModelError(nameof(model.ToleranceQuantityMt), "تلورانس نامعتبر است.");
        }

        var metrics = ComputeLossMetrics(
            model.ExpectedQuantityMt,
            model.ActualQuantityMt,
            model.ToleranceQuantityMt);

        if (!ModelState.IsValid)
        {
            PopulateLossEventEditContext(item);
            model.Stage = item.Stage;
            model.ProductId = item.ProductId;
            model.ContractId = item.ContractId;
            model.ShipmentId = item.ShipmentId;
            model.LoadingRegisterId = item.LoadingRegisterId;
            model.LoadingReceiptId = item.LoadingReceiptId;
            model.TruckDispatchId = item.TruckDispatchId;
            model.SalesTransactionId = item.SalesTransactionId;
            model.TerminalId = item.TerminalId;
            model.StorageTankId = item.StorageTankId;
            model.AffectsInventory = item.AffectsInventory;
            model.Reference = normalizedReference;
            model.Notes = normalizedNotes;
            model.ResponsiblePartyType = normalizedResponsiblePartyType;
            model.ResponsiblePartyName = normalizedResponsiblePartyName;
            model.FinancialTreatment = normalizedFinancialTreatment;
            return View(model);
        }

        var diff = AuditDiffFormatter.ForUpdate(
            ("EventDate", item.EventDate, model.EventDate),
            ("ExpectedQuantityMt", item.ExpectedQuantityMt, model.ExpectedQuantityMt),
            ("ActualQuantityMt", item.ActualQuantityMt, model.ActualQuantityMt),
            ("DifferenceQuantityMt", item.DifferenceQuantityMt, metrics.DifferenceQuantityMt),
            ("ToleranceQuantityMt", item.ToleranceQuantityMt, model.ToleranceQuantityMt),
            ("AllowableLossMt", item.AllowableLossMt, metrics.AllowableLossMt),
            ("ChargeableLossMt", item.ChargeableLossMt, metrics.ChargeableLossMt),
            ("ResponsiblePartyType", item.ResponsiblePartyType, normalizedResponsiblePartyType),
            ("ResponsiblePartyName", item.ResponsiblePartyName, normalizedResponsiblePartyName),
            ("FinancialTreatment", item.FinancialTreatment, normalizedFinancialTreatment),
            ("Reference", item.Reference, normalizedReference),
            ("Notes", item.Notes, normalizedNotes));

        item.EventDate = model.EventDate;
        item.ExpectedQuantityMt = model.ExpectedQuantityMt;
        item.ActualQuantityMt = model.ActualQuantityMt;
        item.DifferenceQuantityMt = metrics.DifferenceQuantityMt;
        item.ToleranceQuantityMt = model.ToleranceQuantityMt;
        item.AllowableLossMt = metrics.AllowableLossMt;
        item.ChargeableLossMt = metrics.ChargeableLossMt;
        item.ResponsiblePartyType = normalizedResponsiblePartyType;
        item.ResponsiblePartyName = normalizedResponsiblePartyName;
        item.FinancialTreatment = normalizedFinancialTreatment;
        item.Reference = normalizedReference;
        item.Notes = normalizedNotes;

        await _db.SaveChangesAsync();
        await _audit.LogAndSaveAsync(nameof(LossEvent), item.Id, AuditAction.Update, diff: diff);

        TempData["ok"] = "ویرایش رویداد ضایعات انجام شد.";
        if (TryGetLocalReturnUrl(model.ReturnUrl, out var localReturnUrl))
        {
            return Redirect(localReturnUrl);
        }

        return RedirectToAction(nameof(Details), new { id = item.Id });
    }

    public async Task<IActionResult> Details(int id, string? returnUrl = null)
    {
        var item = await _db.LossEvents
            .AsNoTracking()
            .Include(e => e.Product)
            .Include(e => e.Contract)
            .Include(e => e.Shipment)
            .Include(e => e.LoadingRegister)
            .Include(e => e.LoadingReceipt)
            .Include(e => e.TruckDispatch)
                .ThenInclude(d => d!.Truck)
            .Include(e => e.SalesTransaction)
            .Include(e => e.Terminal)
            .Include(e => e.StorageTank)
            .FirstOrDefaultAsync(e => e.Id == id);

        if (item is null)
        {
            return NotFound();
        }

        ViewBag.ReturnUrl = TryGetLocalReturnUrl(returnUrl, out var localReturnUrl) ? localReturnUrl : null;

        return View(new LossEventDetailsViewModel
        {
            Id = item.Id,
            EventDate = item.EventDate,
            Stage = item.Stage,
            ProductName = item.Product?.Name ?? "",
            ContractNumber = item.Contract?.ContractNumber,
            ShipmentCode = item.Shipment?.ShipmentCode,
            LoadingRegisterLabel = item.LoadingRegister is null
                ? null
                : $"#{item.LoadingRegister.Id} - {DateDisplay.Date(item.LoadingRegister.LoadingDate)}",
            LoadingReceiptLabel = item.LoadingReceipt is null
                ? null
                : $"#{item.LoadingReceipt.Id} - {DateDisplay.Date(item.LoadingReceipt.ReceiptDate)}",
            TruckDispatchLabel = item.TruckDispatch is null
                ? null
                : $"#{item.TruckDispatch.Id} - {(item.TruckDispatch.Truck?.PlateNumber ?? "Dispatch")}",
            SalesLabel = item.SalesTransaction is null
                ? null
                : $"#{item.SalesTransaction.Id} - {item.SalesTransaction.InvoiceNumber}",
            TerminalName = item.Terminal?.Name,
            StorageTankCode = StorageTankDisplay.BuildOptional(item.StorageTank),
            ExpectedQuantityMt = item.ExpectedQuantityMt,
            ActualQuantityMt = item.ActualQuantityMt,
            DifferenceQuantityMt = item.DifferenceQuantityMt,
            ToleranceQuantityMt = item.ToleranceQuantityMt,
            AllowableLossMt = item.AllowableLossMt,
            ChargeableLossMt = item.ChargeableLossMt,
            ResponsiblePartyType = item.ResponsiblePartyType,
            ResponsiblePartyName = item.ResponsiblePartyName,
            FinancialTreatment = item.FinancialTreatment,
            AffectsInventory = item.AffectsInventory,
            InventoryMovementId = item.InventoryMovementId,
            Reference = item.Reference,
            Notes = item.Notes
        });
    }

    private async Task PopulateLookupsAsync(
        LossEventCreateViewModel? createModel = null,
        LossEventIndexFilterViewModel? filter = null)
    {
        ViewBag.Products = new SelectList(
            await _db.Products.AsNoTracking().Where(p => p.IsActive).OrderBy(p => p.Code).ToListAsync(),
            "Id",
            "Name",
            createModel?.ProductId ?? filter?.ProductId);

        ViewBag.Contracts = new SelectList(
            ContractUiText.ToLookupOptions(
                await _db.Contracts
                    .AsNoTracking()
                    .Include(c => c.Product)
                    .Include(c => c.Unit)
                    .OrderByDescending(c => c.ContractDate)
                    .ThenBy(c => c.ContractNumber)
                    .ToListAsync()),
            nameof(ContractLookupOption.Id),
            nameof(ContractLookupOption.Display),
            createModel?.ContractId ?? filter?.ContractId);

        ViewBag.Shipments = new SelectList(
            await _db.Shipments.AsNoTracking().OrderByDescending(s => s.DepartureDate).ThenBy(s => s.ShipmentCode).Take(200).ToListAsync(),
            "Id",
            "ShipmentCode",
            createModel?.ShipmentId);

        ViewBag.Loadings = new SelectList(
            await _db.LoadingRegisters
                .AsNoTracking()
                .Include(l => l.Contract)
                .OrderByDescending(l => l.LoadingDate)
                .ThenByDescending(l => l.Id)
                .Take(200)
                .Select(l => new
                {
                    l.Id,
                    Label = (l.Contract != null ? l.Contract.ContractNumber + " | " : string.Empty)
                        + DateDisplay.Date(l.LoadingDate)
                        + " | "
                        + l.LoadedQuantityMt
                })
                .ToListAsync(),
            "Id",
            "Label",
            createModel?.LoadingRegisterId);

        ViewBag.Receipts = new SelectList(
            await _db.LoadingReceipts
                .AsNoTracking()
                .OrderByDescending(r => r.ReceiptDate)
                .ThenByDescending(r => r.Id)
                .Take(200)
                .Select(r => new
                {
                    r.Id,
                    Label = $"#{r.Id} | {DateDisplay.Date(r.ReceiptDate)} | {r.ReceivedQuantityMt}"
                })
                .ToListAsync(),
            "Id",
            "Label",
            createModel?.LoadingReceiptId);

        ViewBag.Dispatches = new SelectList(
            await _db.TruckDispatches
                .AsNoTracking()
                .Include(d => d.Truck)
                .OrderByDescending(d => d.DispatchDate)
                .ThenByDescending(d => d.Id)
                .Take(200)
                .Select(d => new
                {
                    d.Id,
                    Label = $"#{d.Id} | {DateDisplay.Date(d.DispatchDate)} | {(d.Truck != null ? d.Truck.PlateNumber : "Dispatch")}"
                })
                .ToListAsync(),
            "Id",
            "Label",
            createModel?.TruckDispatchId);

        ViewBag.Sales = new SelectList(
            await _db.SalesTransactions
                .AsNoTracking()
                .OrderByDescending(s => s.SaleDate)
                .ThenByDescending(s => s.Id)
                .Take(200)
                .Select(s => new
                {
                    s.Id,
                    Label = $"#{s.Id} | {s.InvoiceNumber}"
                })
                .ToListAsync(),
            "Id",
            "Label",
            createModel?.SalesTransactionId);

        ViewBag.Terminals = new SelectList(
            await _db.Terminals.AsNoTracking().Where(t => t.IsActive).OrderBy(t => t.Code).ToListAsync(),
            "Id",
            "Name",
            createModel?.TerminalId);

        ViewBag.StorageTanks = new SelectList(
            await StorageTankDisplay.LoadOptionsAsync(_db.StorageTanks.AsNoTracking().OrderBy(t => t.DisplayName ?? t.TankCode)),
            "Id",
            "Display",
            createModel?.StorageTankId);

        ViewBag.Stages = Enum.GetValues<LossEventStage>()
            // تسویه نهایی مخزن فقط از مسیر «تسویهٔ مخزن» ساخته می‌شود، نه از فرم دستی ضایعات.
            .Where(stage => stage != LossEventStage.TankFinalSettlement)
            .Select(stage => new SelectListItem
            {
                Value = ((int)stage).ToString(),
                Text = LossEventStageLabels.ToPersian(stage),
                Selected = stage == (createModel?.Stage ?? filter?.Stage)
            })
            .ToList();

        ViewBag.AffectsInventoryOptions = new SelectList(
            new[]
            {
                new { Id = "", Name = "همه" },
                new { Id = "true", Name = "بله" },
                new { Id = "false", Name = "خیر" }
            },
            "Id",
            "Name",
            filter?.AffectsInventory?.ToString().ToLowerInvariant());

        ViewBag.VarianceOptions = new SelectList(
            new[]
            {
                new { Id = nameof(LossEventVarianceFilter.All), Name = "همه" },
                new { Id = nameof(LossEventVarianceFilter.ShortageOnly), Name = "فقط کسری" },
                new { Id = nameof(LossEventVarianceFilter.SurplusOnly), Name = "فقط اضافه‌بار" }
            },
            "Id",
            "Name",
            (filter?.Variance ?? LossEventVarianceFilter.All).ToString());
    }

    private async Task<LossEvent?> LoadLossEventForEditAsync(int id)
        => await _db.LossEvents
            .Include(e => e.Product)
            .Include(e => e.Contract)
            .Include(e => e.Shipment)
            .Include(e => e.LoadingRegister)
            .Include(e => e.LoadingReceipt)
            .Include(e => e.TruckDispatch)
                .ThenInclude(d => d!.Truck)
            .Include(e => e.SalesTransaction)
            .Include(e => e.Terminal)
            .Include(e => e.StorageTank)
            .FirstOrDefaultAsync(e => e.Id == id);

    private static LossEventCreateViewModel BuildEditModel(LossEvent item, string? returnUrl)
        => new()
        {
            Stage = item.Stage,
            ProductId = item.ProductId,
            ContractId = item.ContractId,
            ShipmentId = item.ShipmentId,
            LoadingRegisterId = item.LoadingRegisterId,
            LoadingReceiptId = item.LoadingReceiptId,
            TruckDispatchId = item.TruckDispatchId,
            SalesTransactionId = item.SalesTransactionId,
            TerminalId = item.TerminalId,
            StorageTankId = item.StorageTankId,
            EventDate = item.EventDate,
            ExpectedQuantityMt = item.ExpectedQuantityMt,
            ActualQuantityMt = item.ActualQuantityMt,
            ToleranceQuantityMt = item.ToleranceQuantityMt,
            ResponsiblePartyType = item.ResponsiblePartyType,
            ResponsiblePartyName = item.ResponsiblePartyName,
            FinancialTreatment = item.FinancialTreatment,
            AffectsInventory = item.AffectsInventory,
            Reference = item.Reference,
            Notes = item.Notes,
            ReturnUrl = returnUrl
        };

    private void PopulateLossEventEditContext(LossEvent item)
    {
        ViewBag.LossEventId = item.Id;
        ViewBag.StageLabel = LossEventStageLabels.ToPersian(item.Stage);
        ViewBag.ProductName = item.Product?.Name ?? string.Empty;
        ViewBag.ContractNumber = item.Contract?.ContractNumber;
        ViewBag.ShipmentCode = item.Shipment?.ShipmentCode;
        ViewBag.LoadingRegisterLabel = item.LoadingRegister is null
            ? null
            : $"#{item.LoadingRegister.Id} - {DateDisplay.Date(item.LoadingRegister.LoadingDate)}";
        ViewBag.LoadingReceiptLabel = item.LoadingReceipt is null
            ? null
            : $"#{item.LoadingReceipt.Id} - {DateDisplay.Date(item.LoadingReceipt.ReceiptDate)}";
        ViewBag.TruckDispatchLabel = item.TruckDispatch is null
            ? null
            : $"#{item.TruckDispatch.Id} - {(item.TruckDispatch.Truck?.PlateNumber ?? "Dispatch")}";
        ViewBag.SalesLabel = item.SalesTransaction is null
            ? null
            : $"#{item.SalesTransaction.Id} - {item.SalesTransaction.InvoiceNumber}";
        ViewBag.TerminalName = item.Terminal?.Name;
        ViewBag.StorageTankCode = StorageTankDisplay.BuildOptional(item.StorageTank);
    }

    private static bool CanAffectInventory(LossEventStage stage)
        => stage == LossEventStage.TankNaturalLoss || stage == LossEventStage.ManualAdjustment;

    private static (decimal DifferenceQuantityMt, decimal AllowableLossMt, decimal ChargeableLossMt) ComputeLossMetrics(
        decimal expectedQuantityMt,
        decimal actualQuantityMt,
        decimal toleranceQuantityMt)
    {
        var differenceQuantityMt = expectedQuantityMt - actualQuantityMt;
        var positiveLossMt = Math.Max(differenceQuantityMt, 0m);
        var allowableLossMt = Math.Min(positiveLossMt, toleranceQuantityMt);
        var chargeableLossMt = Math.Max(0m, positiveLossMt - toleranceQuantityMt);
        return (differenceQuantityMt, allowableLossMt, chargeableLossMt);
    }

    private static string? TrimToNull(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string BuildInventoryNotes(LossEventStage stage, string? notes)
    {
        var prefix = $"LossEvent trace | Stage={stage}";
        if (string.IsNullOrWhiteSpace(notes))
        {
            return prefix;
        }

        var combined = $"{prefix} | {notes.Trim()}";
        return combined.Length <= 1000 ? combined : combined[..1000];
    }
}
