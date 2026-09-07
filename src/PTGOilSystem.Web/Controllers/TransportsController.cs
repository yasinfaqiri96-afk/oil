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

    // فقط برای برگشتِ مصرف کرایه هنگام لغو گروهی؛ همان adapter صفحهٔ دیسپچ.
    private readonly Services.Accounting.IExpenseAccountingAdapter? _expenseAccounting;

    public TransportsController(
        ApplicationDbContext db,
        ITransportWorkflowService workflow,
        ITransportQuantityService quantities,
        IAfghanistanBusinessClock clock,
        IFormTokenGuard? formTokens = null,
        Services.Accounting.IExpenseAccountingAdapter? expenseAccounting = null)
    {
        _db = db;
        _workflow = workflow;
        _quantities = quantities;
        _clock = clock;
        _formTokens = formTokens;
        _expenseAccounting = expenseAccounting;
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
            // انتخاب حملِ مبدأ در خودِ فرم انتقال انجام می‌شود تا انتخاب گروهی هم ممکن باشد.
            case TransportStartSourceKind.ActiveTransport:
                return RedirectToAction(nameof(Continue), new { transportLegId = model.TransportLegId });
            case TransportStartSourceKind.LoadingReceipt:
                ModelState.AddModelError(nameof(model.LoadingReceiptId), "رسید/بارگیری مستقیم را انتخاب کنید.");
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
    [HttpGet]
    public async Task<IActionResult> FromLoading(int loadingRegisterId)
    {
        var model = new TransportStartFromLoadingViewModel
        {
            LoadingRegisterId = loadingRegisterId,
            TransportDate = _clock.Today
        };
        if (!await PopulateLoadingModelAsync(model))
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
    public async Task<IActionResult> FromLoading(
        TransportStartFromLoadingViewModel model,
        [FromForm(Name = FormTokenHtmlHelper.FieldName)] string? formToken = null)
    {
        if (!await PopulateLoadingModelAsync(model))
        {
            return NotFound();
        }
        if (model.QuantityMt > model.AvailableQuantityMt + Epsilon)
        {
            ModelState.AddModelError(nameof(model.QuantityMt), "مقدار حمل از باقیماندهٔ بارگیری بیشتر است.");
        }

        if (!ModelState.IsValid)
        {
            await PopulateVehicleLookupsAsync();
            return View(model);
        }

        _formTokens?.Stamp(formToken, "Transport.FromLoading", nameof(InventoryTransportLeg));
        try
        {
            var leg = await _workflow.StartFromLoadingAsync(new StartTransportFromLoadingCommand
            {
                LoadingRegisterId = model.LoadingRegisterId,
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
            TempData["ok"] = "بارگیری مستقیماً به حمل تبدیل شد؛ هیچ حرکت موجودی ساخته نشد.";
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
            await PopulateLoadingModelAsync(model);
            await PopulateVehicleLookupsAsync();
            return View(model);
        }
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpGet]
    public async Task<IActionResult> BulkFromLoading(int[]? ids = null, string? returnUrl = null)
    {
        var model = new TransportBulkFromLoadingViewModel
        {
            TransportDate = _clock.Today,
            ReturnUrl = returnUrl,
            Rows = await BuildBulkLoadingRowsAsync(ids ?? [])
        };
        var requested = (ids ?? []).Distinct().Count(id => id > 0);
        ViewBag.BulkRequestedCount = requested;
        ViewBag.BulkSkippedCount = Math.Max(requested - model.Rows.Count, 0);
        await PopulateVehicleLookupsAsync();
        return View(model);
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> BulkFromLoading(TransportBulkFromLoadingViewModel model)
    {
        var selected = (model.Rows ?? []).Where(r => r.Selected && r.LoadingRegisterId > 0).ToList();
        if (selected.Count == 0)
        {
            ModelState.AddModelError(string.Empty, "حداقل یک بارگیری را انتخاب کنید.");
        }

        if (!ModelState.IsValid)
        {
            await RefreshBulkLoadingRowsAsync(model);
            await PopulateVehicleLookupsAsync();
            return View(model);
        }

        var created = 0;
        var failures = new List<string>();
        foreach (var row in selected)
        {
            try
            {
                await _workflow.StartFromLoadingAsync(new StartTransportFromLoadingCommand
                {
                    LoadingRegisterId = row.LoadingRegisterId,
                    QuantityMt = row.QuantityMt,
                    TransportType = row.TransportType,
                    TruckId = row.TransportType == LoadingTransportType.Truck ? row.TruckId : null,
                    WagonId = row.TransportType == LoadingTransportType.Wagon ? row.WagonId : null,
                    VesselId = row.TransportType == LoadingTransportType.Vessel ? row.VesselId : null,
                    DriverId = row.TransportType == LoadingTransportType.Truck ? row.DriverId : null,
                    ServiceProviderId = row.ServiceProviderId,
                    TransportDate = model.TransportDate,
                    Reference = row.Reference
                });
                created++;
            }
            catch (BusinessRuleException ex)
            {
                failures.Add($"{row.LoadingLabel}: {ex.Message}");
            }
        }

        if (created == 0)
        {
            foreach (var failure in failures)
            {
                ModelState.AddModelError(string.Empty, failure);
            }
            await RefreshBulkLoadingRowsAsync(model);
            await PopulateVehicleLookupsAsync();
            return View(model);
        }

        TempData["ok"] = $"{created:N0} بارگیری به حمل تبدیل شد.";
        if (failures.Count > 0)
        {
            TempData["err"] = "تبدیل نشد — " + string.Join(" | ", failures);
        }
        return !string.IsNullOrWhiteSpace(model.ReturnUrl) && Url.IsLocalUrl(model.ReturnUrl)
            ? Redirect(model.ReturnUrl)
            : RedirectToAction("Index", "InventoryTransportLegs");
    }

    /// <summary>باقیماندهٔ قابل تبدیل هر بارگیری با همان فرمول فرم تک‌بارگیری محاسبه می‌شود.</summary>
    private async Task<List<TransportBulkFromLoadingRow>> BuildBulkLoadingRowsAsync(IReadOnlyCollection<int> ids)
    {
        var rows = new List<TransportBulkFromLoadingRow>();
        foreach (var id in ids.Distinct().Where(id => id > 0))
        {
            var source = new TransportStartFromLoadingViewModel { LoadingRegisterId = id };
            if (!await PopulateLoadingModelAsync(source))
            {
                continue;
            }
            rows.Add(new TransportBulkFromLoadingRow
            {
                LoadingRegisterId = id,
                Selected = true,
                LoadingLabel = source.LoadingLabel,
                ProductName = source.ProductName,
                AvailableQuantityMt = source.AvailableQuantityMt,
                QuantityMt = source.AvailableQuantityMt,
                TransportType = source.TransportType,
                TruckId = source.TruckId,
                WagonId = source.WagonId,
                VesselId = source.VesselId,
                DriverId = source.DriverId,
                ServiceProviderId = source.ServiceProviderId,
                Reference = source.Reference
            });
        }
        return rows;
    }

    /// <summary>برچسب و باقیماندهٔ ردیف‌های ارسال‌شده را دوباره از دیتابیس می‌خواند (نمایش پس از خطا).</summary>
    private async Task RefreshBulkLoadingRowsAsync(TransportBulkFromLoadingViewModel model)
    {
        foreach (var row in model.Rows ?? [])
        {
            var source = new TransportStartFromLoadingViewModel { LoadingRegisterId = row.LoadingRegisterId };
            if (!await PopulateLoadingModelAsync(source))
            {
                row.Selected = false;
                continue;
            }
            row.LoadingLabel = source.LoadingLabel;
            row.ProductName = source.ProductName;
            row.AvailableQuantityMt = source.AvailableQuantityMt;
        }
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
        var selectedSources = (model.Sources ?? [])
            .Where(s => s.Selected && s.LegId > 0 && s.QuantityMt > 0m)
            .ToList();
        if (selectedSources.Count == 0)
        {
            ModelState.AddModelError(nameof(model.Sources), "حداقل یک حمل مبدأ و مقدار آن را انتخاب کنید.");
        }

        // هر ردیف می‌تواند وسیلهٔ مقصد خودش را داشته باشد (امپورت اکسل همین را پر می‌کند): یا از
        // لیست وسیله‌ها، یا نمبرِ موترِ جدید، وگرنه وسیلهٔ مقصدِ پیش‌فرضِ فرم.
        foreach (var source in selectedSources)
        {
            if (!string.IsNullOrWhiteSpace(source.TargetVehicleNumber) && string.IsNullOrWhiteSpace(source.TargetKey))
            {
                continue; // موترِ جدید؛ داخل تراکنش ساخته می‌شود.
            }
            if (!TryResolveTargetVehicle(source.TargetKey, model, out _, out _))
            {
                ModelState.AddModelError(
                    nameof(model.Sources),
                    $"وسیلهٔ مقصد برای «{source.Label}» مشخص نیست.");
            }
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
            var newPlateTrucks = await ResolveOrCreateTargetTrucksAsync(selectedSources);
            var targetGroups = BuildTargetGroups(selectedSources, model, newPlateTrucks);
            var createdLegIds = new List<int>();
            foreach (var group in targetGroups)
            {
                var result = await _workflow.ContinueToVehicleAsync(new ContinueToVehicleCommand
                {
                    Sources = group.Sources,
                    TargetTransportType = group.TransportType,
                    TargetTruckId = group.TransportType == LoadingTransportType.Truck ? group.VehicleId : null,
                    TargetWagonId = group.TransportType == LoadingTransportType.Wagon ? group.VehicleId : null,
                    TargetVesselId = group.TransportType == LoadingTransportType.Vessel ? group.VehicleId : null,
                    DriverId = model.DriverId,
                    TransferDate = model.TransferDate,
                    TicketSerialNumber = model.TicketSerialNumber,
                    Notes = model.Notes
                });
                createdLegIds.Add(result.ChildLeg.Id);
            }
            if (transaction is not null)
            {
                await transaction.CommitAsync();
            }
            TempData["ok"] = createdLegIds.Count > 1
                ? $"{createdLegIds.Count} حمل مقصد از {selectedSources.Count} حمل در جریان ثبت شد؛ هیچ حرکت موجودی ساخته نشد."
                : selectedSources.Count > 1
                    ? "ادغام حمل‌ها در وسیلهٔ مقصد ثبت شد؛ هیچ حرکت موجودی ساخته نشد."
                    : "انتقال به وسیلهٔ بعدی ثبت شد؛ هیچ حرکت موجودی ساخته نشد.";
            return createdLegIds.Count == 1
                ? RedirectToAction(nameof(Details), new { id = createdLegIds[0] })
                : RedirectToAction("Index", "InventoryTransportLegs");
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

    // ── لغو گروهی حمل‌های ساخته‌شده از «حمل در جریان» ─────────────────────────────
    // همان مسیر لغوِ صفحهٔ دیسپچ است، فقط برای چند حمل در یک تراکنش: نگهبان زنجیره،
    // برگشت رسیدهای مبدأ، لغو دیسپچ سازگاری و برگشت مصرف کرایه.
    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpGet]
    public async Task<IActionResult> CancelContinued()
        => View(new TransportContinueCancelViewModel { Rows = await LoadContinuedLegRowsAsync() });

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CancelContinued(
        int[]? legIds,
        [FromForm(Name = FormTokenHtmlHelper.FieldName)] string? formToken = null)
    {
        var requestedIds = (legIds ?? []).Where(id => id > 0).Distinct().ToList();
        if (requestedIds.Count == 0)
        {
            TempData["err"] = "هیچ حملی برای لغو انتخاب نشده است.";
            return RedirectToAction(nameof(CancelContinued));
        }

        var rows = await LoadContinuedLegRowsAsync();
        var selectable = rows.Where(r => requestedIds.Contains(r.LegId)).ToList();
        var blocked = selectable.Where(r => r.BlockReason is not null).ToList();
        if (blocked.Count > 0)
        {
            TempData["err"] = "لغو انجام نشد: "
                + string.Join("، ", blocked.Select(r => $"حمل #{r.LegId} ({r.BlockReason})"));
            return RedirectToAction(nameof(CancelContinued));
        }

        var legIdsToCancel = selectable.Select(r => r.LegId).ToList();
        if (legIdsToCancel.Count == 0)
        {
            TempData["err"] = "حمل‌های انتخاب‌شده دیگر قابل لغو نیستند.";
            return RedirectToAction(nameof(CancelContinued));
        }

        await using var transaction = _db.Database.IsRelational()
            ? await _db.Database.BeginTransactionAsync()
            : null;
        _formTokens?.Stamp(formToken, "Transport.CancelContinued", nameof(InventoryTransportLeg));

        try
        {
            // رسیدهای والد که این حمل‌ها را ساخته‌اند؛ لغو زنجیره از روی همین‌ها انجام می‌شود.
            var sourceReceiptIds = await _db.InventoryTransportLegAllocations
                .AsNoTracking()
                .Where(a => legIdsToCancel.Contains(a.InventoryTransportLegId) && a.SourceTransportReceiptId != null)
                .Select(a => a.SourceTransportReceiptId!.Value)
                .Distinct()
                .ToListAsync();

            var receipts = await _db.InventoryTransportReceipts
                .Include(r => r.InventoryTransportLeg)
                .Where(r => sourceReceiptIds.Contains(r.Id))
                .ToListAsync();
            var activeReceipts = receipts.Where(r => !r.IsCancelled).ToList();

            var dispatches = await _db.TruckDispatches
                .Where(d => d.InventoryTransportReceiptId != null
                    && sourceReceiptIds.Contains(d.InventoryTransportReceiptId!.Value)
                    && d.Status != DispatchStatus.Cancelled)
                .ToListAsync();
            if (dispatches.Any(d => d.SalesTransactionId.HasValue))
            {
                throw new BusinessRuleException(
                    "TRANSPORT_CONTINUE_CANCEL_HAS_SALE",
                    "برای موتر یکی از این حمل‌ها فروش ثبت شده است؛ ابتدا فروش لینک‌شده را لغو کنید.");
            }

            // اول نگهبان زنجیره: اگر فرزندی بارش را جای دیگری برده باشد، اینجا استثنا می‌آید.
            await _workflow.CancelOrReverseAsync(activeReceipts.Select(r => r.Id).ToList());

            foreach (var receipt in activeReceipts)
            {
                receipt.IsCancelled = true;
                if (receipt.InventoryTransportLeg?.Status == InventoryTransportLegStatus.Received)
                {
                    receipt.InventoryTransportLeg.Status = InventoryTransportLegStatus.InTransit;
                }
            }

            foreach (var dispatch in dispatches)
            {
                dispatch.Status = DispatchStatus.Cancelled;
                await DispatchFreightExpenseSync.CancelByDispatchIdAsync(_db, dispatch.Id, _expenseAccounting);
            }

            await _db.SaveChangesAsync();

            // سطر مصرفِ دارایی اسناد لغوشده برگشتی می‌شود (همان قاعدهٔ لغو در صفحهٔ دیسپچ).
            var usageWriter = new AssetUsageChargeService(_db);
            foreach (var dispatch in dispatches)
            {
                await usageWriter.SyncOperationAsync(dispatch);
            }
            foreach (var receipt in activeReceipts.Where(r => r.InventoryTransportLeg is not null))
            {
                await usageWriter.SyncOperationAsync(receipt, receipt.InventoryTransportLeg!);
            }
            await _db.SaveChangesAsync();

            if (transaction is not null)
            {
                await transaction.CommitAsync();
            }
            TempData["ok"] = $"{legIdsToCancel.Count} حمل لغو شد و باقیماندهٔ حمل‌های مبدأ آزاد شد.";
        }
        catch (DbUpdateException duplicate) when (_formTokens?.IsDuplicate(duplicate) == true)
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync();
            }
            TempData["err"] = "این لغو قبلاً ثبت شده است و دوباره انجام نشد.";
        }
        catch (BusinessRuleException ex)
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync();
            }
            TempData["err"] = ex.Message;
        }

        return RedirectToAction(nameof(CancelContinued));
    }

    // حمل‌هایی که سهمشان از یک رسیدِ حملِ والد آمده = ساخته‌شده با «انتقال به وسیله دیگر».
    private async Task<List<TransportContinueCancelRow>> LoadContinuedLegRowsAsync()
    {
        var childLegIds = await _db.InventoryTransportLegAllocations
            .AsNoTracking()
            .Where(a => a.SourceTransportReceiptId != null)
            .Select(a => a.InventoryTransportLegId)
            .Distinct()
            .ToListAsync();
        if (childLegIds.Count == 0)
        {
            return [];
        }

        var legs = await _db.InventoryTransportLegs
            .AsNoTracking()
            .Where(l => childLegIds.Contains(l.Id) && l.Status != InventoryTransportLegStatus.Cancelled)
            .OrderByDescending(l => l.Id)
            .Take(500)
            .Select(l => new
            {
                l.Id,
                l.TransportType,
                ProductName = l.Product != null ? l.Product.Name : "",
                Vehicle = l.Truck != null ? l.Truck.PlateNumber
                    : l.Wagon != null ? l.Wagon.WagonNumber
                    : l.Vessel != null ? l.Vessel.Name
                    : l.WagonNumber,
                l.QuantityMt,
                l.LoadedDate
            })
            .ToListAsync();
        if (legs.Count == 0)
        {
            return [];
        }

        var legIds = legs.Select(l => l.Id).ToList();

        // والدهای هر حمل، برای نمایش «از کدام حمل آمده».
        var parents = await _db.InventoryTransportLegAllocations
            .AsNoTracking()
            .Where(a => legIds.Contains(a.InventoryTransportLegId) && a.SourceTransportLegId != null)
            .Select(a => new { a.InventoryTransportLegId, ParentLegId = a.SourceTransportLegId!.Value })
            .ToListAsync();

        // اگر خودِ این حمل بارش را جای دیگری برده باشد، لغو مسدود است (همان نگهبان زنجیره).
        var consumedLegIds = await _db.InventoryTransportReceipts
            .AsNoTracking()
            .Where(r => legIds.Contains(r.InventoryTransportLegId) && !r.IsCancelled)
            .Select(r => r.InventoryTransportLegId)
            .Distinct()
            .ToListAsync();

        // فروشِ لینک‌شده به دیسپچِ سازگاریِ همین حمل هم لغو را مسدود می‌کند.
        var soldLegIds = await _db.InventoryTransportLegAllocations
            .AsNoTracking()
            .Where(a => legIds.Contains(a.InventoryTransportLegId) && a.SourceTransportReceiptId != null)
            .Where(a => _db.TruckDispatches.Any(d => d.InventoryTransportReceiptId == a.SourceTransportReceiptId
                && d.Status != DispatchStatus.Cancelled
                && d.SalesTransactionId != null))
            .Select(a => a.InventoryTransportLegId)
            .Distinct()
            .ToListAsync();

        return legs.Select(l =>
        {
            var parentIds = parents.Where(p => p.InventoryTransportLegId == l.Id)
                .Select(p => $"#{p.ParentLegId}")
                .Distinct()
                .ToList();
            return new TransportContinueCancelRow
            {
                LegId = l.Id,
                Label = $"حمل #{l.Id} — {TransportTypeText(l.TransportType)} {(string.IsNullOrWhiteSpace(l.Vehicle) ? "" : l.Vehicle)}",
                ProductName = l.ProductName,
                SourceLabel = parentIds.Count > 0 ? string.Join("، ", parentIds) : "—",
                QuantityMt = l.QuantityMt,
                TransferDate = l.LoadedDate,
                BlockReason = consumedLegIds.Contains(l.Id)
                    ? "برای مرحلهٔ بعدی این حمل عملیات ثبت شده است"
                    : soldLegIds.Contains(l.Id)
                        ? "فروش لینک‌شده دارد"
                        : null
            };
        }).ToList();
    }

    // فایل «نمبر وسیله قبلی | نمبر وسیله جدید | وزن» را می‌خواند و ردیف‌ها را به
    // حمل‌های در جریان و وسیله‌های مقصد تطبیق می‌دهد. هیچ ثبتی انجام نمی‌شود؛ فقط
    // گرید فرم پر می‌شود و کاربر خودش ثبت می‌کند.
    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ImportContinueExcel(IFormFile? file, CancellationToken ct = default)
    {
        if (file is null || file.Length == 0)
        {
            return Json(new { ok = false, message = "فایلی انتخاب نشده است." });
        }
        if (file.Length > 5 * 1024 * 1024)
        {
            return Json(new { ok = false, message = "حجم فایل زیاد است (حداکثر ۵ مگابایت)." });
        }

        IReadOnlyList<TransportContinueImportRow> parsed;
        try
        {
            await using var workbook = await ExcelWorkbookNormalizer.OpenAsync(file, ct);
            parsed = TransportContinueWorkbookParser.Parse(workbook.Stream);
        }
        catch (Exception ex)
        {
            return Json(new { ok = false, message = "خواندن فایل اکسل ناموفق بود: " + ex.Message });
        }

        if (parsed.Count == 0)
        {
            return Json(new { ok = false, message = "در فایل هیچ ردیف معتبری (نمبر وسیله قبلی و وزن) یافت نشد." });
        }

        var legs = await LoadActiveTransportRowsAsync();
        var targets = await LoadTargetVehicleRowsAsync();

        var warnings = new List<string>();
        var usedLegIds = new HashSet<int>();
        var matched = new List<object>();

        foreach (var row in parsed)
        {
            var sourceKey = NormalizeVehicleNumber(row.SourceVehicleNumber);
            // یک وسیله می‌تواند چند حملِ در جریان داشته باشد؛ هر ردیف اکسل یکی را مصرف می‌کند.
            var leg = legs.FirstOrDefault(l => NormalizeVehicleNumber(l.VehicleNumber) == sourceKey && !usedLegIds.Contains(l.LegId));
            if (leg is null)
            {
                warnings.Add($"«{row.SourceVehicleNumber}»: حمل در جریانِ آزاد با این نمبر پیدا نشد.");
                continue;
            }
            usedLegIds.Add(leg.LegId);

            // بیشتر از باقیماندهٔ حمل قابل انتقال نیست؛ مقدار تا سقف باقیمانده کم می‌شود.
            var quantity = row.QuantityMt;
            if (quantity > leg.RemainingQuantityMt + Epsilon)
            {
                warnings.Add(
                    $"«{row.SourceVehicleNumber}»: وزن اکسل ({row.QuantityMt:N4}) از باقیماندهٔ حمل بیشتر بود و به {leg.RemainingQuantityMt:N4} کم شد.");
                quantity = leg.RemainingQuantityMt;
            }

            string? targetKey = null;
            string? targetVehicleNumber = null;
            if (!string.IsNullOrWhiteSpace(row.TargetVehicleNumber))
            {
                var targetNorm = NormalizeVehicleNumber(row.TargetVehicleNumber);
                var target = targets.FirstOrDefault(t => NormalizeVehicleNumber(t.Number) == targetNorm);
                if (target is null)
                {
                    // وسیلهٔ جدید؛ نمبر در ردیف می‌ماند و هنگام ثبت، موتر با همین نمبر ساخته می‌شود.
                    targetVehicleNumber = row.TargetVehicleNumber;
                    warnings.Add($"«{row.TargetVehicleNumber}»: موتر جدید است و هنگام ثبت ساخته می‌شود.");
                }
                else
                {
                    targetKey = target.Key;
                }
            }

            matched.Add(new
            {
                legId = leg.LegId,
                quantityMt = quantity,
                remainingMt = leg.RemainingQuantityMt,
                targetKey,
                targetVehicleNumber
            });
        }

        if (matched.Count == 0)
        {
            return Json(new { ok = false, message = "هیچ ردیفی با حمل‌های در جریان تطبیق نشد.", warnings });
        }

        return Json(new { ok = true, rows = matched, warnings });
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
                DriverId = model.DriverId,
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

    private async Task<bool> PopulateLoadingModelAsync(TransportStartFromLoadingViewModel model)
    {
        var loading = await _db.LoadingRegisters
            .AsNoTracking()
            .Where(l => l.Id == model.LoadingRegisterId)
            .Select(l => new
            {
                l.Id,
                l.LoadedQuantityMt,
                l.TransportType,
                l.TruckId,
                l.VesselId,
                l.LogisticsServiceProviderId,
                l.BillOfLadingNumber,
                l.RwbNo,
                ProductName = l.Product != null ? l.Product.Name : "",
                ContractLabel = l.Contract != null ? l.Contract.ContractNumber : ""
            })
            .SingleOrDefaultAsync();
        if (loading is null)
        {
            return false;
        }

        var receivedMt = await _db.LoadingReceipts.AsNoTracking()
            .Where(r => r.LoadingRegisterId == loading.Id && !r.IsCancelled)
            .SumAsync(r => (decimal?)r.ReceivedQuantityMt) ?? 0m;
        var shortageMt = await _db.LossEvents.AsNoTracking()
            .Where(e => (e.LoadingRegisterId == loading.Id
                    || e.LoadingReceiptId.HasValue && e.LoadingReceipt != null
                        && e.LoadingReceipt.LoadingRegisterId == loading.Id)
                && e.Stage == LossEventStage.ReceiptShortage
                && !e.IsCancelled)
            .SumAsync(e => (decimal?)(e.DifferenceQuantityMt > 0m
                ? e.DifferenceQuantityMt
                : e.ChargeableLossMt > 0m ? e.ChargeableLossMt : 0m)) ?? 0m;
        var transportedMt = await _db.InventoryTransportLegAllocations.AsNoTracking()
            .Where(a => a.SourceLoadingRegisterId == loading.Id
                && a.InventoryTransportLeg != null
                && a.InventoryTransportLeg.Status != InventoryTransportLegStatus.Cancelled)
            .SumAsync(a => (decimal?)a.QuantityMt) ?? 0m;
        model.AvailableQuantityMt = decimal.Round(
            Math.Max(loading.LoadedQuantityMt - receivedMt - shortageMt - transportedMt, 0m), 4);
        if (model.AvailableQuantityMt <= Epsilon)
        {
            return false;
        }

        model.LoadingLabel = $"{(string.IsNullOrWhiteSpace(loading.ContractLabel) ? $"بارگیری #{loading.Id}" : loading.ContractLabel)} — #{loading.Id}";
        model.ProductName = loading.ProductName;
        model.Reference ??= loading.RwbNo ?? loading.BillOfLadingNumber;
        if (model.QuantityMt <= 0m)
        {
            model.TransportType = loading.TransportType;
            model.TruckId = loading.TransportType == LoadingTransportType.Truck ? loading.TruckId : null;
            model.VesselId = loading.TransportType == LoadingTransportType.Vessel ? loading.VesselId : null;
            model.ServiceProviderId = loading.LogisticsServiceProviderId;
        }
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
                VehicleNumber = row.VehicleNumber,
                RemainingQuantityMt = row.RemainingQuantityMt,
                Selected = selected,
                TargetKey = input?.TargetKey,
                TargetVehicleNumber = input?.TargetVehicleNumber,
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
                l.Vehicle ?? string.Empty,
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
        ViewBag.TargetVehicles = (await LoadTargetVehicleRowsAsync())
            .Select(v => new TransportTargetVehicleOption(v.Key, v.Label, v.Group))
            .ToList();
    }

    // موتر/واگن/کشتیِ فعال در یک لیست، با کلید «نوع:شناسه» تا هر ردیف بتواند وسیلهٔ مقصد خودش را داشته باشد.
    private async Task<IReadOnlyList<TargetVehicleRow>> LoadTargetVehicleRowsAsync()
    {
        var trucks = await _db.Trucks.AsNoTracking()
            .Where(t => t.IsActive).OrderBy(t => t.PlateNumber)
            .Select(t => new { t.Id, Number = t.PlateNumber })
            .ToListAsync();
        var wagons = await _db.Wagons.AsNoTracking()
            .Where(w => w.IsActive).OrderBy(w => w.WagonNumber)
            .Select(w => new { w.Id, Number = w.WagonNumber })
            .ToListAsync();
        var vessels = await _db.Vessels.AsNoTracking()
            .Where(v => v.IsActive).OrderBy(v => v.Name)
            .Select(v => new { v.Id, Number = v.Name })
            .ToListAsync();

        var rows = new List<TargetVehicleRow>();
        rows.AddRange(trucks.Select(t => new TargetVehicleRow(
            $"{(int)LoadingTransportType.Truck}:{t.Id}", t.Number ?? string.Empty, "موتر", t.Number ?? string.Empty)));
        rows.AddRange(wagons.Select(w => new TargetVehicleRow(
            $"{(int)LoadingTransportType.Wagon}:{w.Id}", w.Number ?? string.Empty, "واگن", w.Number ?? string.Empty)));
        rows.AddRange(vessels.Select(v => new TargetVehicleRow(
            $"{(int)LoadingTransportType.Vessel}:{v.Id}", v.Number ?? string.Empty, "کشتی", v.Number ?? string.Empty)));
        return rows;
    }

    // نمبرِ موترِ مقصدی که در داده‌های پایه نیست ساخته می‌شود — همان رفتار «انتقال گروهی از موجودی».
    // موترِ غیرفعال ساخته نمی‌شود و خطا می‌دهد تا کاربر خودش آن را فعال کند.
    private async Task<IReadOnlyDictionary<string, int>> ResolveOrCreateTargetTrucksAsync(
        IReadOnlyList<TransportContinueSourceInput> selectedSources)
    {
        var plates = selectedSources
            .Where(s => string.IsNullOrWhiteSpace(s.TargetKey) && !string.IsNullOrWhiteSpace(s.TargetVehicleNumber))
            .Select(s => s.TargetVehicleNumber!.Trim())
            .Where(plate => plate.Length > 0)
            .ToList();
        var resolved = new Dictionary<string, int>(StringComparer.Ordinal);
        if (plates.Count == 0)
        {
            return resolved;
        }

        var existing = await _db.Trucks
            .Where(t => t.PlateNumber != null)
            .Select(t => new { t.Id, t.PlateNumber, t.IsActive })
            .ToListAsync();
        var created = new List<(string Key, Truck Truck)>();

        foreach (var plate in plates)
        {
            var key = NormalizeVehicleNumber(plate);
            if (key.Length == 0 || resolved.ContainsKey(key) || created.Any(c => c.Key == key))
            {
                continue;
            }

            var match = existing.FirstOrDefault(t => NormalizeVehicleNumber(t.PlateNumber) == key);
            if (match is not null)
            {
                if (!match.IsActive)
                {
                    throw new BusinessRuleException(
                        "TRANSPORT_CONTINUE_TARGET_TRUCK_INACTIVE",
                        $"موتر «{plate}» غیرفعال است؛ ابتدا آن را در داده‌های پایه فعال کنید.");
                }
                resolved[key] = match.Id;
                continue;
            }

            var truck = new Truck { PlateNumber = plate, IsActive = true };
            _db.Trucks.Add(truck);
            created.Add((key, truck));
        }

        if (created.Count > 0)
        {
            await _db.SaveChangesAsync();
            foreach (var (key, truck) in created)
            {
                resolved[key] = truck.Id;
            }
        }

        return resolved;
    }

    // ردیف‌هایی که به یک وسیلهٔ مقصد می‌روند در یک حملِ مقصد ادغام می‌شوند.
    private static List<TransportTargetGroup> BuildTargetGroups(
        IReadOnlyList<TransportContinueSourceInput> selectedSources,
        TransportContinueViewModel model,
        IReadOnlyDictionary<string, int> newPlateTrucks)
    {
        var groups = new List<TransportTargetGroup>();
        foreach (var source in selectedSources)
        {
            LoadingTransportType transportType;
            int vehicleId;

            if (string.IsNullOrWhiteSpace(source.TargetKey)
                && !string.IsNullOrWhiteSpace(source.TargetVehicleNumber)
                && newPlateTrucks.TryGetValue(NormalizeVehicleNumber(source.TargetVehicleNumber), out var truckId))
            {
                transportType = LoadingTransportType.Truck;
                vehicleId = truckId;
            }
            else if (!TryResolveTargetVehicle(source.TargetKey, model, out transportType, out vehicleId))
            {
                throw new BusinessRuleException(
                    "TRANSPORT_CONTINUE_TARGET_MISSING",
                    $"وسیلهٔ مقصد برای «{source.Label}» مشخص نیست.");
            }

            var group = groups.FirstOrDefault(g => g.TransportType == transportType && g.VehicleId == vehicleId);
            if (group is null)
            {
                group = new TransportTargetGroup(transportType, vehicleId);
                groups.Add(group);
            }
            group.Sources.Add(new ContinueToVehicleSource(source.LegId, source.QuantityMt));
        }
        return groups;
    }

    private static string NormalizeVehicleNumber(string? value)
        => new((value ?? string.Empty).Trim().ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    // کلیدِ ردیف بر وسیلهٔ پیش‌فرضِ فرم اولویت دارد؛ خالی بودنِ هر دو یعنی مقصد مشخص نیست.
    private static bool TryResolveTargetVehicle(
        string? targetKey,
        TransportContinueViewModel model,
        out LoadingTransportType transportType,
        out int vehicleId)
    {
        transportType = model.TargetTransportType;
        vehicleId = 0;

        if (!string.IsNullOrWhiteSpace(targetKey))
        {
            var parts = targetKey.Split(':', 2);
            if (parts.Length == 2
                && int.TryParse(parts[0], out var rawType)
                && Enum.IsDefined(typeof(LoadingTransportType), rawType)
                && rawType != (int)LoadingTransportType.Unspecified
                && int.TryParse(parts[1], out var parsedId)
                && parsedId > 0)
            {
                transportType = (LoadingTransportType)rawType;
                vehicleId = parsedId;
                return true;
            }
            return false;
        }

        vehicleId = transportType switch
        {
            LoadingTransportType.Truck => model.TargetTruckId ?? 0,
            LoadingTransportType.Wagon => model.TargetWagonId ?? 0,
            LoadingTransportType.Vessel => model.TargetVesselId ?? 0,
            _ => 0
        };
        return vehicleId > 0;
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
        string VehicleNumber,
        decimal RemainingQuantityMt);

    /// <summary>وسیلهٔ مقصدِ قابل انتخاب در ردیف‌ها؛ Number فقط برای تطبیق با اکسل است.</summary>
    private sealed record TargetVehicleRow(string Key, string Label, string Group, string Number);

    /// <summary>حمل‌های مبدأیی که به یک وسیلهٔ مقصد مشترک می‌روند (= یک حمل مقصد).</summary>
    private sealed class TransportTargetGroup(LoadingTransportType transportType, int vehicleId)
    {
        public LoadingTransportType TransportType { get; } = transportType;
        public int VehicleId { get; } = vehicleId;
        public List<ContinueToVehicleSource> Sources { get; } = [];
    }
}
