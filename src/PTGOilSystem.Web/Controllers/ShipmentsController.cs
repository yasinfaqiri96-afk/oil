using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Helpers;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.Shipments;
using PTGOilSystem.Web.Security;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Exceptions;
using PTGOilSystem.Web.Services.Time;

namespace PTGOilSystem.Web.Controllers;

[Authorize]
public class ShipmentsController : Controller
{
    private const int MinimumAllocationRows = 2;

    private readonly ApplicationDbContext _db;
    private readonly IStockService _stock;
    private readonly InventoryTransportLegLoadService _legLoad;

    // Dual-write اختیاری به دفتر کل جدید — برگشتِ بارگیری «کالای در راه» را برمی‌گرداند. پشت Feature Flag و null-safe.
    private readonly Services.Accounting.IInventoryTransferAccountingAdapter? _transferAccounting;

    public ShipmentsController(ApplicationDbContext db)
        : this(db, new StockService(db), new InventoryTransportLegLoadService(db, new StockService(db)))
    {
    }

    [ActivatorUtilitiesConstructor]
    public ShipmentsController(
        ApplicationDbContext db,
        IStockService stock,
        InventoryTransportLegLoadService legLoad,
        Services.Accounting.IInventoryTransferAccountingAdapter? transferAccounting = null)
    {
        _db = db;
        _stock = stock;
        _legLoad = legLoad;
        _transferAccounting = transferAccounting;
    }

    public IActionResult Index()
        => RedirectToAction("Index", "ShipmentPnl");

    [Authorize(Policy = AuthPolicies.ManageData)]
    public async Task<IActionResult> Create(string? returnUrl = null)
    {
        var model = new ShipmentCreateViewModel
        {
            DepartureDate = AfghanistanBusinessClock.SystemToday,
            ReturnUrl = returnUrl,
            ContractAllocations = BuildEmptyAllocationRows()
        };

        await PopulateLookupsAsync();
        return View(model);
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(ShipmentCreateViewModel model)
    {
        Normalize(model);
        var allocations = GetEffectiveAllocations(model);

        // مقدار کل محموله منبع ورودی جداگانه ندارد؛ همیشه از جمع ردیف‌های قرارداد ساخته می‌شود.
        // ModelState پاک می‌شود تا مقدار ارسال‌شدهٔ کلاینت نتواند مقدار محاسبه‌شده را جایگزین کند.
        model.QuantityMt = decimal.Round(
            allocations.Sum(a => a.QuantityMt ?? 0m),
            4,
            MidpointRounding.AwayFromZero);
        ModelState.Remove(nameof(model.QuantityMt));

        await ValidateCreateAsync(model, allocations);

        // تخصیص اختیاری از موجودی مخزن — اعتبارسنجی سمت سرور قبل از باز کردن تراکنش
        // (به مقادیر ارسالی اعتماد نمی‌کنیم؛ موجودی مخزن و باقی‌مانده قرارداد بازخوانده می‌شود).
        // انتخاب مخزن اکنون مستقیماً داخل ردیف قرارداد bind می‌شود تا ثبت خروج به JavaScript
        // و ساخت فیلدهای مخفی وابسته نباشد. TankPicks قدیمی برای سازگاری حفظ شده است.
        var inlineTankPicks = allocations
            .Where(a => a.ContractId.GetValueOrDefault() > 0
                && a.StorageTankId.GetValueOrDefault() > 0
                && a.QuantityMt.GetValueOrDefault() > 0m)
            .Select(a => new ShipmentTankPickInput
            {
                ContractId = a.ContractId!.Value,
                StorageTankId = a.StorageTankId!.Value,
                QuantityMt = a.QuantityMt
            })
            .ToList();
        var tankPicks = inlineTankPicks.Count > 0
            ? inlineTankPicks
            : model.TankPicks.Where(p => p.HasQuantity).ToList();
        var tankPlans = await ValidateTankPicksAsync(allocations, tankPicks);

        if (!ModelState.IsValid)
        {
            model.ContractAllocations = PadAllocationRows(allocations);
            await PopulateLookupsAsync();
            return View(model);
        }

        var primaryContractId = model.PrimaryContractId ?? allocations.FirstOrDefault()?.ContractId;
        var shipment = new Shipment
        {
            ShipmentCode = model.ShipmentCode,
            VesselId = model.VesselId,
            ContractId = primaryContractId,
            DepartureDate = model.DepartureDate,
            ArrivalDate = model.ArrivalDate,
            OriginLocationId = model.OriginLocationId,
            DestinationLocationId = model.DestinationLocationId,
            QuantityMt = model.QuantityMt,
            Notes = model.Notes
        };

        IDbContextTransaction? transaction = null;
        try
        {
            transaction = await BeginTransactionIfSupportedAsync();

            _db.Shipments.Add(shipment);
            await AddAllocationsAndLoadTanksAsync(shipment, allocations, tankPlans);

            if (transaction is not null)
            {
                await transaction.CommitAsync();
            }
        }
        catch (BusinessRuleException ex)
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync();
            }

            ModelState.AddModelError(string.Empty, ex.Message);
            model.ContractAllocations = PadAllocationRows(allocations);
            await PopulateLookupsAsync();
            return View(model);
        }
        catch
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync();
            }

            throw;
        }
        finally
        {
            if (transaction is not null)
            {
                await transaction.DisposeAsync();
            }
        }

        var loadedFromInventory = tankPlans.Count;
        TempData["ok"] = allocations.Count == 0
            ? T("محموله ثبت شد.", "Shipment was created.")
            : loadedFromInventory > 0
                ? T(
                    $"محموله با {allocations.Count:N0} تخصیص قرارداد خرید ثبت شد و {loadedFromInventory:N0} بارگیری از موجودی مخزن انجام شد.",
                    $"Shipment was created with {allocations.Count:N0} purchase contract allocation(s) and {loadedFromInventory:N0} tank load(s).")
                : T(
                    $"محموله با {allocations.Count:N0} تخصیص قرارداد خرید ثبت شد.",
                    $"Shipment was created with {allocations.Count:N0} purchase contract allocation(s).");

        if (TryGetLocalReturnUrl(model.ReturnUrl, out var local))
        {
            return Redirect(local);
        }

        return RedirectToAction("Details", "ShipmentPnl", new { id = shipment.Id });
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    public async Task<IActionResult> Edit(int id, string? returnUrl = null)
    {
        var shipment = await _db.Shipments
            .AsNoTracking()
            .Include(s => s.ShipmentContracts)
            .Include(s => s.InventoryTransportLegs)
            .FirstOrDefaultAsync(s => s.Id == id);

        if (shipment is null)
        {
            return NotFound();
        }

        var canEditAllocations = await CanReeditAllocationsAsync(shipment);

        var allocationRows = shipment.ShipmentContracts
            .OrderBy(c => c.Id)
            .Select(c => new ShipmentContractAllocationInput
            {
                ContractId = c.ContractId,
                // مخزنِ ردیف از leg بارگیری‌شدهٔ همان قرارداد (در صورت وجود) بازسازی می‌شود.
                StorageTankId = shipment.InventoryTransportLegs
                    .Where(l => l.SourcePurchaseContractId == c.ContractId && l.SourceStorageTankId.HasValue)
                    .Select(l => l.SourceStorageTankId)
                    .FirstOrDefault(),
                QuantityMt = c.QuantityMt,
                Notes = c.Notes
            })
            .ToList();

        var model = new ShipmentCreateViewModel
        {
            Id = shipment.Id,
            CanEditAllocations = canEditAllocations,
            ShipmentCode = shipment.ShipmentCode,
            VesselId = shipment.VesselId,
            PrimaryContractId = shipment.ContractId,
            DepartureDate = shipment.DepartureDate,
            ArrivalDate = shipment.ArrivalDate,
            OriginLocationId = shipment.OriginLocationId,
            DestinationLocationId = shipment.DestinationLocationId,
            QuantityMt = shipment.QuantityMt,
            Notes = shipment.Notes,
            ReturnUrl = returnUrl,
            ContractAllocations = PadAllocationRows(allocationRows),
            TankPicks = shipment.InventoryTransportLegs
                .Where(l => l.SourceStorageTankId.HasValue)
                .Select(l => new ShipmentTankPickInput
                {
                    ContractId = l.SourcePurchaseContractId,
                    StorageTankId = l.SourceStorageTankId!.Value,
                    QuantityMt = l.QuantityMt
                })
                .ToList()
        };

        await PopulateLookupsAsync();
        return View(model);
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(ShipmentCreateViewModel model)
    {
        var shipment = await _db.Shipments
            .Include(s => s.ShipmentContracts)
            .Include(s => s.InventoryTransportLegs)
            .FirstOrDefaultAsync(s => s.Id == model.Id);

        if (shipment is null)
        {
            return NotFound();
        }

        // بازخوانی مجوز از سرور؛ به مقدار ارسالی کلاینت اعتماد نمی‌کنیم.
        var canEditAllocations = await CanReeditAllocationsAsync(shipment);
        model.CanEditAllocations = canEditAllocations;

        Normalize(model);
        var allocations = GetEffectiveAllocations(model);

        if (canEditAllocations)
        {
            model.QuantityMt = decimal.Round(
                allocations.Sum(a => a.QuantityMt ?? 0m),
                4,
                MidpointRounding.AwayFromZero);
            ModelState.Remove(nameof(model.QuantityMt));

            await ValidateCreateAsync(model, allocations, excludeShipmentId: shipment.Id);
        }
        else
        {
            // فقط هدر: مقدار و تخصیص‌ها دست‌نخورده می‌مانند.
            model.QuantityMt = shipment.QuantityMt;
            ModelState.Remove(nameof(model.QuantityMt));
            allocations = new List<ShipmentContractAllocationInput>();
            await ValidateHeaderAsync(model, excludeShipmentId: shipment.Id);
        }

        var inlineTankPicks = allocations
            .Where(a => a.ContractId.GetValueOrDefault() > 0
                && a.StorageTankId.GetValueOrDefault() > 0
                && a.QuantityMt.GetValueOrDefault() > 0m)
            .Select(a => new ShipmentTankPickInput
            {
                ContractId = a.ContractId!.Value,
                StorageTankId = a.StorageTankId!.Value,
                QuantityMt = a.QuantityMt
            })
            .ToList();
        var tankPicks = inlineTankPicks.Count > 0
            ? inlineTankPicks
            : model.TankPicks.Where(p => p.HasQuantity).ToList();

        // آیا تخصیص/مخزن نسبت به وضعیت فعلی تغییر کرده؟ اگر نه، فقط هدر ذخیره می‌شود
        // تا leg ها بی‌دلیل حذف/بازساخته نشوند.
        var allocationsChanged = canEditAllocations
            && AllocationsDiffer(shipment, allocations, tankPicks);

        if (!ModelState.IsValid)
        {
            model.ContractAllocations = PadAllocationRows(allocations.Count > 0
                ? allocations
                : shipment.ShipmentContracts.Select(c => new ShipmentContractAllocationInput
                {
                    ContractId = c.ContractId,
                    QuantityMt = c.QuantityMt,
                    Notes = c.Notes
                }).ToList());
            await PopulateLookupsAsync();
            return View(model);
        }

        IDbContextTransaction? transaction = null;
        try
        {
            transaction = await BeginTransactionIfSupportedAsync();

            // هدر همیشه به‌روزرسانی می‌شود.
            shipment.ShipmentCode = model.ShipmentCode;
            shipment.VesselId = model.VesselId;
            shipment.DepartureDate = model.DepartureDate;
            shipment.ArrivalDate = model.ArrivalDate;
            shipment.OriginLocationId = model.OriginLocationId;
            shipment.DestinationLocationId = model.DestinationLocationId;
            shipment.Notes = model.Notes;
            shipment.UpdatedAtUtc = DateTime.UtcNow;

            if (allocationsChanged)
            {
                // اعتبارسنجی مخزن باید پس از برگرداندن موجودی legهای فعلی انجام شود؛
                // پس اول برمی‌گردانیم، سپس با موجودی بازیابی‌شده اعتبارسنجی و بازسازی می‌کنیم.
                await ReverseShipmentDerivedAsync(shipment);

                var tankPlans = await ValidateTankPicksAsync(allocations, tankPicks);
                if (!ModelState.IsValid)
                {
                    if (transaction is not null)
                    {
                        await transaction.RollbackAsync();
                    }

                    model.ContractAllocations = PadAllocationRows(allocations);
                    await PopulateLookupsAsync();
                    return View(model);
                }

                shipment.ContractId = model.PrimaryContractId ?? allocations.FirstOrDefault()?.ContractId;
                shipment.QuantityMt = model.QuantityMt;
                await AddAllocationsAndLoadTanksAsync(shipment, allocations, tankPlans);
            }
            else
            {
                await _db.SaveChangesAsync();
            }

            if (transaction is not null)
            {
                await transaction.CommitAsync();
            }
        }
        catch (BusinessRuleException ex)
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync();
            }

            ModelState.AddModelError(string.Empty, ex.Message);
            model.ContractAllocations = PadAllocationRows(allocations);
            await PopulateLookupsAsync();
            return View(model);
        }
        catch
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync();
            }

            throw;
        }
        finally
        {
            if (transaction is not null)
            {
                await transaction.DisposeAsync();
            }
        }

        TempData["ok"] = canEditAllocations
            ? T("محموله ویرایش شد.", "Shipment was updated.")
            : T("اطلاعات هدر محموله ویرایش شد. به‌دلیل فعالیت ثبت‌شده، مقدار و تخصیص قابل ویرایش نبود.",
                "Shipment header was updated. Quantity and allocations were locked due to existing activity.");

        if (TryGetLocalReturnUrl(model.ReturnUrl, out var local))
        {
            return Redirect(local);
        }

        return RedirectToAction("Details", "ShipmentPnl", new { id = shipment.Id });
    }

    // تخصیص‌ها و بارگیری مخازن را برای یک محموله می‌سازد و بلافاصله موجودی مخزن را کسر می‌کند.
    // مشترک بین ثبت و ویرایش؛ باید داخل تراکنش فراخوانی شود.
    private async Task AddAllocationsAndLoadTanksAsync(
        Shipment shipment,
        IReadOnlyList<ShipmentContractAllocationInput> allocations,
        IReadOnlyList<TankPickPlan> tankPlans)
    {
        foreach (var allocation in allocations)
        {
            _db.ShipmentContracts.Add(new ShipmentContract
            {
                Shipment = shipment,
                ContractId = allocation.ContractId!.Value,
                QuantityMt = allocation.QuantityMt,
                Notes = allocation.Notes
            });
        }

        await _db.SaveChangesAsync();

        // برای هر انتخاب مخزن یک تخصیص حمل (leg) متصل به همین محموله می‌سازیم و بلافاصله
        // بارگیری می‌کنیم تا موجودی همان مخزن کسر شود. کل کار داخل همین تراکنش است؛
        // هر خطا باعث rollback کل محموله می‌شود (هیچ کسر دوباره/جزئی).
        foreach (var plan in tankPlans)
        {
            var leg = new InventoryTransportLeg
            {
                Shipment = shipment,
                SourcePurchaseContractId = plan.Contract.Id,
                SourcePurchaseContract = plan.Contract,
                ProductId = plan.Contract.ProductId,
                SourceTerminalId = plan.Tank.TerminalId,
                SourceStorageTankId = plan.Tank.Id,
                SourceStorageTank = plan.Tank,
                TransportType = LoadingTransportType.Unspecified,
                LoadedDate = AfghanistanBusinessClock.SystemToday,
                QuantityMt = plan.QuantityMt,
                Status = InventoryTransportLegStatus.Draft,
                Notes = T("بارگیری از موجودی هنگام ثبت محموله", "Loaded from inventory at shipment creation")
            };

            _db.InventoryTransportLegs.Add(leg);
            await _db.SaveChangesAsync();

            await _legLoad.LoadAsync(leg);
        }
    }

    // legهای فعلیِ محموله و خروجی موجودی آن‌ها را حذف می‌کند (موجودی مخزن برمی‌گردد)
    // و تخصیص‌های قرارداد قبلی را پاک می‌کند. فقط زمانی امن است که گاردِ CanReeditAllocationsAsync
    // اجازه داده باشد (هیچ مصرف پایین‌دستی روی این legها وجود ندارد).
    private async Task ReverseShipmentDerivedAsync(Shipment shipment)
    {
        var legs = shipment.InventoryTransportLegs.ToList();
        var movementIds = legs
            .Where(l => l.OutboundInventoryMovementId.HasValue)
            .Select(l => l.OutboundInventoryMovementId!.Value)
            .ToList();

        // Dual-write داخل همان تراکنش: بهای «کالای در راه» به حوضچهٔ ترمینال مبدأ برمی‌گردد.
        // باید پیش از حذف legها انجام شود، چون آداپتر مقدار و ترمینال مبدأ را از خودِ leg می‌خواند.
        if (_transferAccounting is not null)
        {
            foreach (var leg in legs.Where(l => l.OutboundInventoryMovementId.HasValue))
            {
                await _transferAccounting.TryPostLegLoadReversalAsync(leg);
            }
        }

        _db.InventoryTransportLegs.RemoveRange(legs);
        shipment.InventoryTransportLegs.Clear();

        if (movementIds.Count > 0)
        {
            var movements = await _db.InventoryMovements
                .Where(m => movementIds.Contains(m.Id))
                .ToListAsync();
            _db.InventoryMovements.RemoveRange(movements);
        }

        var contracts = shipment.ShipmentContracts.ToList();
        _db.ShipmentContracts.RemoveRange(contracts);
        shipment.ShipmentContracts.Clear();

        await _db.SaveChangesAsync();
    }

    // آیا مجموعهٔ تخصیص/مخزنِ ارسالی با وضعیت فعلی محموله فرق دارد؟
    private static bool AllocationsDiffer(
        Shipment shipment,
        IReadOnlyList<ShipmentContractAllocationInput> allocations,
        IReadOnlyList<ShipmentTankPickInput> tankPicks)
    {
        static string AllocSignature(IEnumerable<(int ContractId, decimal Qty)> rows)
            => string.Join("|", rows
                .GroupBy(r => r.ContractId)
                .OrderBy(g => g.Key)
                .Select(g => $"{g.Key}:{decimal.Round(g.Sum(r => r.Qty), 4):0.####}"));

        static string TankSignature(IEnumerable<(int ContractId, int TankId, decimal Qty)> rows)
            => string.Join("|", rows
                .GroupBy(r => (r.ContractId, r.TankId))
                .OrderBy(g => g.Key.ContractId).ThenBy(g => g.Key.TankId)
                .Select(g => $"{g.Key.ContractId}-{g.Key.TankId}:{decimal.Round(g.Sum(r => r.Qty), 4):0.####}"));

        var currentAlloc = AllocSignature(shipment.ShipmentContracts
            .Select(c => (c.ContractId, c.QuantityMt ?? 0m)));
        var newAlloc = AllocSignature(allocations
            .Where(a => a.ContractId.GetValueOrDefault() > 0)
            .Select(a => (a.ContractId!.Value, a.QuantityMt ?? 0m)));

        var currentTanks = TankSignature(shipment.InventoryTransportLegs
            .Where(l => l.SourceStorageTankId.HasValue)
            .Select(l => (l.SourcePurchaseContractId, l.SourceStorageTankId!.Value, l.QuantityMt)));
        var newTanks = TankSignature(tankPicks
            .Select(p => (p.ContractId, p.StorageTankId, p.QuantityMt ?? 0m)));

        return currentAlloc != newAlloc || currentTanks != newTanks;
    }

    // گاردِ ایمنی: ویرایش مقدار/تخصیص/مخزن فقط وقتی مجاز است که هیچ فعالیت پایین‌دستی
    // روی legهای محموله ثبت نشده باشد؛ در غیر این صورت حذف/بازسازیِ legها موجودی و اسناد را خراب می‌کند.
    private async Task<bool> CanReeditAllocationsAsync(Shipment shipment)
    {
        var legIds = shipment.InventoryTransportLegs.Select(l => l.Id).ToList();

        // هر leg باید هنوز Draft یا Loaded و غیرِ batch باشد.
        if (shipment.InventoryTransportLegs.Any(l =>
                l.InventoryTransportBatchId.HasValue
                || (l.Status != InventoryTransportLegStatus.Draft
                    && l.Status != InventoryTransportLegStatus.Loaded)))
        {
            return false;
        }

        if (legIds.Count > 0)
        {
            if (await _db.InventoryTransportReceipts.AnyAsync(r => legIds.Contains(r.InventoryTransportLegId)))
            {
                return false;
            }

            if (await _db.CustomsDeclarations.AnyAsync(c => c.TransportLegId.HasValue && legIds.Contains(c.TransportLegId.Value)))
            {
                return false;
            }

            if (await _db.ExpenseTransactions.AnyAsync(e => e.TransportLegId.HasValue && legIds.Contains(e.TransportLegId.Value)))
            {
                return false;
            }
        }

        if (await _db.SalesTransactions.AnyAsync(s => s.ShipmentId == shipment.Id))
        {
            return false;
        }

        if (await _db.LossEvents.AnyAsync(l => l.ShipmentId == shipment.Id))
        {
            return false;
        }

        if (await _db.DeliveryReceipts.AnyAsync(d => d.ShipmentId == shipment.Id))
        {
            return false;
        }

        // نسب‌نامهٔ موجودی (اگر فعال باشد): وجود هر لات مانع حذف امنِ خروجی‌ها می‌شود.
        if (await _db.InventoryLots.AnyAsync(lot => lot.RootShipmentId == shipment.Id))
        {
            return false;
        }

        if (await _db.InventoryLotMovements.AnyAsync(m => m.ShipmentId == shipment.Id))
        {
            return false;
        }

        return true;
    }

    // فقط اعتبارسنجی فیلدهای هدر (بدون تخصیص) — برای ویرایش هدرِ محموله‌ای که تخصیص آن قفل است.
    private async Task ValidateHeaderAsync(ShipmentCreateViewModel model, int? excludeShipmentId = null)
    {
        if (string.IsNullOrWhiteSpace(model.ShipmentCode))
        {
            ModelState.AddModelError(nameof(model.ShipmentCode), T("کد محموله الزامی است.", "Shipment code is required."));
        }
        else
        {
            var normalizedCode = model.ShipmentCode.Trim().ToLower();
            var duplicate = await _db.Shipments
                .AsNoTracking()
                .AnyAsync(s => s.ShipmentCode.ToLower() == normalizedCode
                    && (!excludeShipmentId.HasValue || s.Id != excludeShipmentId.Value));
            if (duplicate)
            {
                ModelState.AddModelError(nameof(model.ShipmentCode), T("محموله‌ای با این کد قبلاً ثبت شده است.", "A shipment with this code already exists."));
            }
        }

        if (model.DepartureDate.HasValue
            && model.ArrivalDate.HasValue
            && model.ArrivalDate.Value.Date < model.DepartureDate.Value.Date)
        {
            ModelState.AddModelError(nameof(model.ArrivalDate), T("تاریخ رسیدن نمی‌تواند قبل از تاریخ حرکت باشد.", "Arrival date cannot be before departure date."));
        }

        if (model.VesselId.HasValue
            && !await _db.Vessels.AsNoTracking().AnyAsync(v => v.Id == model.VesselId.Value))
        {
            ModelState.AddModelError(nameof(model.VesselId), T("کشتی پیدا نشد.", "Vessel was not found."));
        }

        if (model.OriginLocationId.HasValue
            && !await _db.Locations.AsNoTracking().AnyAsync(l => l.Id == model.OriginLocationId.Value))
        {
            ModelState.AddModelError(nameof(model.OriginLocationId), T("موقعیت مبدا پیدا نشد.", "Origin location was not found."));
        }

        if (model.DestinationLocationId.HasValue
            && !await _db.Locations.AsNoTracking().AnyAsync(l => l.Id == model.DestinationLocationId.Value))
        {
            ModelState.AddModelError(nameof(model.DestinationLocationId), T("موقعیت مقصد پیدا نشد.", "Destination location was not found."));
        }
    }

    private async Task ValidateCreateAsync(
        ShipmentCreateViewModel model,
        IReadOnlyList<ShipmentContractAllocationInput> allocations,
        int? excludeShipmentId = null)
    {
        if (string.IsNullOrWhiteSpace(model.ShipmentCode))
        {
            ModelState.AddModelError(nameof(model.ShipmentCode), T("کد محموله الزامی است.", "Shipment code is required."));
        }
        else
        {
            var normalizedCode = model.ShipmentCode.Trim().ToLower();
            var duplicate = await _db.Shipments
                .AsNoTracking()
                .AnyAsync(s => s.ShipmentCode.ToLower() == normalizedCode
                    && (!excludeShipmentId.HasValue || s.Id != excludeShipmentId.Value));
            if (duplicate)
            {
                ModelState.AddModelError(nameof(model.ShipmentCode), T("محموله‌ای با این کد قبلاً ثبت شده است.", "A shipment with this code already exists."));
            }
        }

        if (model.QuantityMt <= 0m)
        {
            ModelState.AddModelError(nameof(model.QuantityMt), T("مقدار محموله باید بزرگ‌تر از صفر باشد.", "Shipment quantity must be greater than zero."));
        }

        if (model.DepartureDate.HasValue
            && model.ArrivalDate.HasValue
            && model.ArrivalDate.Value.Date < model.DepartureDate.Value.Date)
        {
            ModelState.AddModelError(nameof(model.ArrivalDate), T("تاریخ رسیدن نمی‌تواند قبل از تاریخ حرکت باشد.", "Arrival date cannot be before departure date."));
        }

        if (model.VesselId.HasValue
            && !await _db.Vessels.AsNoTracking().AnyAsync(v => v.Id == model.VesselId.Value))
        {
            ModelState.AddModelError(nameof(model.VesselId), T("کشتی پیدا نشد.", "Vessel was not found."));
        }

        if (model.PrimaryContractId.HasValue
            && !await _db.Contracts.AsNoTracking().AnyAsync(c => c.Id == model.PrimaryContractId.Value))
        {
            ModelState.AddModelError(nameof(model.PrimaryContractId), T("قرارداد اصلی پیدا نشد.", "Primary contract was not found."));
        }

        if (model.OriginLocationId.HasValue
            && !await _db.Locations.AsNoTracking().AnyAsync(l => l.Id == model.OriginLocationId.Value))
        {
            ModelState.AddModelError(nameof(model.OriginLocationId), T("موقعیت مبدا پیدا نشد.", "Origin location was not found."));
        }

        if (model.DestinationLocationId.HasValue
            && !await _db.Locations.AsNoTracking().AnyAsync(l => l.Id == model.DestinationLocationId.Value))
        {
            ModelState.AddModelError(nameof(model.DestinationLocationId), T("موقعیت مقصد پیدا نشد.", "Destination location was not found."));
        }

        var seenContractIds = new HashSet<int>();
        for (var i = 0; i < allocations.Count; i++)
        {
            var allocation = allocations[i];
            var prefix = $"{nameof(model.ContractAllocations)}[{i}]";

            if (!allocation.ContractId.HasValue || allocation.ContractId.Value <= 0)
            {
                ModelState.AddModelError($"{prefix}.{nameof(allocation.ContractId)}", T("قرارداد خرید الزامی است.", "Purchase contract is required."));
                continue;
            }

            var contract = await _db.Contracts
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == allocation.ContractId.Value);

            if (contract is null)
            {
                ModelState.AddModelError($"{prefix}.{nameof(allocation.ContractId)}", T("قرارداد خرید پیدا نشد.", "Purchase contract was not found."));
                continue;
            }

            if (contract.ContractType != ContractType.Purchase)
            {
                ModelState.AddModelError($"{prefix}.{nameof(allocation.ContractId)}", T("تخصیص محموله فقط باید از قراردادهای خرید باشد.", "Shipment allocations must use purchase contracts."));
            }

            if (!seenContractIds.Add(contract.Id))
            {
                ModelState.AddModelError($"{prefix}.{nameof(allocation.ContractId)}", T("این قرارداد خرید قبلاً در ردیف دیگری اضافه شده است.", "This purchase contract is already linked in another row."));
            }

            if (!allocation.QuantityMt.HasValue || allocation.QuantityMt.Value <= 0m)
            {
                ModelState.AddModelError($"{prefix}.{nameof(allocation.QuantityMt)}", T("مقدار تخصیص باید بزرگ‌تر از صفر باشد.", "Allocated quantity must be greater than zero."));
            }
        }

        if (allocations.Count > 0 && model.QuantityMt > 0m)
        {
            var allocatedTotal = allocations.Sum(a => a.QuantityMt ?? 0m);
            if (decimal.Abs(allocatedTotal - model.QuantityMt) > 0.0001m)
            {
                ModelState.AddModelError(
                    nameof(model.ContractAllocations),
                    T(
                        $"جمع مقدار تخصیص قراردادها ({allocatedTotal:N4} MT) باید برابر مقدار کل محموله ({model.QuantityMt:N4} MT) باشد.",
                        $"Allocated contract quantity ({allocatedTotal:N4} MT) must equal shipment quantity ({model.QuantityMt:N4} MT)."));
            }
        }
    }

    // endpoint کمکی: مخازنِ دارای موجودیِ یک قرارداد خرید (همان قرارداد + محصول آن).
    // UI با انتخاب هر قرارداد، کارت مخازن آن را lazy می‌سازد.
    [Authorize(Policy = AuthPolicies.ManageData)]
    public async Task<IActionResult> TankAvailability(int contractId)
    {
        var contract = await _db.Contracts
            .AsNoTracking()
            .Include(c => c.Product)
            .FirstOrDefaultAsync(c => c.Id == contractId && c.ContractType == ContractType.Purchase);
        if (contract is null)
        {
            return NotFound();
        }

        var tanks = await _stock.GetTankAvailabilityAsync(contract.ProductId, contract.Id);
        var tankIds = tanks.Select(t => t.StorageTankId).Distinct().ToList();
        var tankNames = await StorageTankDisplay.LoadNamesAsync(
            _db.StorageTanks.AsNoTracking().Where(t => tankIds.Contains(t.Id)));

        return Json(new ShipmentTankAvailabilityViewModel
        {
            ContractId = contract.Id,
            ProductId = contract.ProductId,
            ContractNumber = contract.ContractNumber,
            ProductName = contract.Product?.Name,
            TotalAvailableQuantityMt = tanks.Sum(t => t.FreeQuantityMt),
            Tanks = tanks
                .Select(t => new ShipmentTankAvailabilityRow
                {
                    StorageTankId = t.StorageTankId,
                    TankCode = StorageTankDisplay.Resolve(tankNames, t.StorageTankId, t.TankCode) ?? t.TankCode,
                    TerminalId = t.TerminalId,
                    TerminalName = t.TerminalName,
                    AvailableQuantityMt = t.FreeQuantityMt
                })
                .ToList()
        });
    }

    // اعتبارسنجی سمت سرورِ تخصیص‌های مخزن (قانون ۱۰). موجودی مخزن و باقی‌ماندهٔ قرارداد را
    // سرور-محاسبه می‌خواند؛ خروجی، برنامهٔ ساخت leg برای ردیف‌های معتبر است. خطاها به ModelState.
    private async Task<List<TankPickPlan>> ValidateTankPicksAsync(
        IReadOnlyList<ShipmentContractAllocationInput> allocations,
        IReadOnlyList<ShipmentTankPickInput> picks)
    {
        var plans = new List<TankPickPlan>();
        if (picks.Count == 0)
        {
            return plans;
        }

        var pickedTankIds = picks.Select(p => p.StorageTankId).Distinct().ToList();
        var tankNames = await StorageTankDisplay.LoadNamesAsync(
            _db.StorageTanks.AsNoTracking().Where(t => pickedTankIds.Contains(t.Id)));

        // باقی‌ماندهٔ مجاز برای محموله جدید = مقدار تخصیص همان قرارداد در همین محموله (هنوز هیچ legی ندارد).
        var allocatedByContract = allocations
            .Where(a => a.ContractId.GetValueOrDefault() > 0)
            .GroupBy(a => a.ContractId!.Value)
            .ToDictionary(g => g.Key, g => g.Sum(a => a.QuantityMt ?? 0m));

        var pickedByContract = new Dictionary<int, decimal>();

        for (var i = 0; i < picks.Count; i++)
        {
            var pick = picks[i];
            var prefix = $"{nameof(ShipmentCreateViewModel.TankPicks)}[{i}]";
            var qty = pick.QuantityMt!.Value;

            if (!allocatedByContract.TryGetValue(pick.ContractId, out var contractAllocated))
            {
                ModelState.AddModelError(
                    $"{prefix}.{nameof(pick.ContractId)}",
                    T("تخصیص مخزن باید مربوط به یکی از قراردادهای همین محموله باشد.",
                        "Tank allocation must reference a contract linked to this shipment."));
                continue;
            }

            var contract = await _db.Contracts.FirstOrDefaultAsync(c => c.Id == pick.ContractId);
            if (contract is null || contract.ContractType != ContractType.Purchase)
            {
                ModelState.AddModelError(
                    $"{prefix}.{nameof(pick.ContractId)}",
                    T("قرارداد خرید برای تخصیص مخزن پیدا نشد.",
                        "Purchase contract for the tank allocation was not found."));
                continue;
            }

            // مخازنِ دارای موجودیِ همین قرارداد+محصول (محصول مخزن ذاتاً با قرارداد یکی است).
            var availability = await _stock.GetTankAvailabilityAsync(contract.ProductId, contract.Id);
            var tankAvail = availability.FirstOrDefault(t => t.StorageTankId == pick.StorageTankId);
            if (tankAvail is null)
            {
                ModelState.AddModelError(
                    $"{prefix}.{nameof(pick.StorageTankId)}",
                    T("این مخزن برای این قرارداد و محصول موجودی قابل تخصیص ندارد.",
                        "This tank has no available stock for the selected contract and product."));
                continue;
            }

            if (qty - tankAvail.FreeQuantityMt > 0.0001m)
            {
                var tankDisplayName = StorageTankDisplay.Resolve(
                    tankNames,
                    tankAvail.StorageTankId,
                    tankAvail.TankCode) ?? tankAvail.TankCode;
                ModelState.AddModelError(
                    $"{prefix}.{nameof(pick.QuantityMt)}",
                    T($"مقدار انتخابی ({qty:N4} MT) از موجودی مخزن {tankDisplayName} ({tankAvail.FreeQuantityMt:N4} MT) بیشتر است.",
                        $"Selected quantity ({qty:N4} MT) exceeds tank {tankDisplayName} stock ({tankAvail.FreeQuantityMt:N4} MT)."));
                continue;
            }

            var runningForContract = pickedByContract.TryGetValue(contract.Id, out var soFar) ? soFar : 0m;
            if (runningForContract + qty - contractAllocated > 0.0001m)
            {
                ModelState.AddModelError(
                    $"{prefix}.{nameof(pick.QuantityMt)}",
                    T($"مجموع تخصیص از مخزن برای قرارداد {contract.ContractNumber} ({runningForContract + qty:N4} MT) از مقدار این قرارداد در محموله ({contractAllocated:N4} MT) بیشتر است.",
                        $"Total tank allocation for contract {contract.ContractNumber} ({runningForContract + qty:N4} MT) exceeds its shipment quantity ({contractAllocated:N4} MT)."));
                continue;
            }

            var tank = await _db.StorageTanks.FirstOrDefaultAsync(t => t.Id == pick.StorageTankId);
            if (tank is null)
            {
                ModelState.AddModelError(
                    $"{prefix}.{nameof(pick.StorageTankId)}",
                    T("مخزن پیدا نشد.", "Tank was not found."));
                continue;
            }

            pickedByContract[contract.Id] = runningForContract + qty;
            plans.Add(new TankPickPlan(contract, tank, decimal.Round(qty, 4, MidpointRounding.AwayFromZero)));
        }

        return plans;
    }

    private sealed record TankPickPlan(Contract Contract, StorageTank Tank, decimal QuantityMt);

    private async Task PopulateLookupsAsync()
    {
        ViewBag.Vessels = new SelectList(
            await _db.Vessels
                .AsNoTracking()
                .OrderBy(v => v.Name)
                .Select(v => new { v.Id, Text = v.Name })
                .ToListAsync(),
            "Id",
            "Text");

        ViewBag.Locations = new SelectList(
            await _db.Locations
                .AsNoTracking()
                .OrderBy(l => l.Name)
                .Select(l => new { l.Id, Text = l.Name })
                .ToListAsync(),
            "Id",
            "Text");

        var contracts = await _db.Contracts
            .AsNoTracking()
            .Include(c => c.Product)
            .Include(c => c.Supplier)
            .Where(c => c.ContractType == ContractType.Purchase)
            .OrderByDescending(c => c.ContractDate)
            .ThenBy(c => c.ContractNumber)
            .Select(c => new SelectListItem
            {
                Value = c.Id.ToString(),
                Text = c.ContractNumber
                    + " - "
                    + (c.Product != null ? c.Product.Name : "Product")
                    + (c.Supplier != null ? " / " + c.Supplier.Name : "")
            })
            .ToListAsync();

        ViewBag.PurchaseContracts = contracts;
    }

    private static List<ShipmentContractAllocationInput> GetEffectiveAllocations(ShipmentCreateViewModel model)
        => (model.ContractAllocations ?? [])
            .Where(a => a.HasAnyValue)
            .ToList();

    private static List<ShipmentContractAllocationInput> BuildEmptyAllocationRows()
        => Enumerable.Range(0, MinimumAllocationRows)
            .Select(_ => new ShipmentContractAllocationInput())
            .ToList();

    private static List<ShipmentContractAllocationInput> PadAllocationRows(
        IReadOnlyList<ShipmentContractAllocationInput> allocations)
    {
        var rows = allocations.ToList();
        while (rows.Count < MinimumAllocationRows)
        {
            rows.Add(new ShipmentContractAllocationInput());
        }

        return rows;
    }

    private static void Normalize(ShipmentCreateViewModel model)
    {
        model.ShipmentCode = NormalizeRequiredString(model.ShipmentCode);
        model.Notes = NormalizeOptionalString(model.Notes);
        model.ContractAllocations ??= [];

        foreach (var allocation in model.ContractAllocations)
        {
            allocation.Notes = NormalizeOptionalString(allocation.Notes);
        }
    }

    private static string NormalizeRequiredString(string? value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();

    private static string? NormalizeOptionalString(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private bool TryGetLocalReturnUrl(string? url, out string local)
    {
        if (!string.IsNullOrWhiteSpace(url) && Url?.IsLocalUrl(url) == true)
        {
            local = url;
            return true;
        }

        local = string.Empty;
        return false;
    }

    private string T(string fa, string en)
        => UiText.T(HttpContext, fa, en);

    private async Task<IDbContextTransaction?> BeginTransactionIfSupportedAsync()
    {
        if (_db.Database.ProviderName == "Microsoft.EntityFrameworkCore.InMemory")
        {
            return null;
        }

        return await _db.Database.BeginTransactionAsync();
    }
}
