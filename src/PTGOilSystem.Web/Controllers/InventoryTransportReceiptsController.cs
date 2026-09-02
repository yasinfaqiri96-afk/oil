using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Helpers;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.InventoryTransport;
using PTGOilSystem.Web.Models.Sales;
using PTGOilSystem.Web.Security;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Exceptions;
using PTGOilSystem.Web.Services.Time;

namespace PTGOilSystem.Web.Controllers;

[Authorize]
public class InventoryTransportReceiptsController : Controller
{
    private readonly ApplicationDbContext _db;
    private readonly ICurrencyConversionService _currencyConversion;
    private readonly ILogger<InventoryTransportReceiptsController> _logger;
    private readonly InventoryTransportReceiptService _receiptService;

    private readonly IAfghanistanBusinessClock _businessClock;

    public InventoryTransportReceiptsController(
        ApplicationDbContext db,
        ICurrencyConversionService currencyConversion,
        ILogger<InventoryTransportReceiptsController> logger,
        IInventoryLineageWriter? lineage = null,
        IAfghanistanBusinessClock? businessClock = null,
        InventoryTransportReceiptService? receiptService = null)
    {
        // مرجع «امروزِ کاری» همیشه ساعت کابل است، نه تاریخ UTC سرور.
        _businessClock = businessClock ?? new AfghanistanBusinessClock(TimeProvider.System);
        _db = db;
        _currencyConversion = currencyConversion;
        _logger = logger;
        // از DI می‌آید تا آداپترهای حسابداری وصل باشند؛ ساخت دستی آن‌ها را null می‌گذارد.
        _receiptService = receiptService ?? new InventoryTransportReceiptService(db, currencyConversion, lineage);
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    public async Task<IActionResult> Create(
        int transportLegId,
        InventoryTransportReceiptDestination? destination = null,
        bool focused = false)
    {
        if (destination == InventoryTransportReceiptDestination.DirectDispatch)
        {
            return RedirectToAction("Continue", "Transports", new { transportLegId });
        }

        var leg = await LoadLegAsync(transportLegId, tracking: false);
        if (leg is null) return NotFound();

        // فقط مقصدهایی که فرم رسید کامل پشتیبانی می‌کند از روی deep-link انتخاب می‌شوند؛
        // بقیه به «ورود به موجودی» برمی‌گردند.
        var selectedDestination = destination is InventoryTransportReceiptDestination.DirectSale
            or InventoryTransportReceiptDestination.DirectDispatch
            ? destination.Value
            : InventoryTransportReceiptDestination.ToInventory;

        // باقیمانده حمل (مقدار کل منهای رسیدهای قبلی) پیش‌فرض مقدار دریافت می‌شود تا تخلیهٔ جزئی بعدی ممکن باشد.
        var consumedMt = await _db.InventoryTransportReceipts
            .Where(r => r.InventoryTransportLegId == leg.Id && !r.IsCancelled)
            .SumAsync(r => r.ReceivedQuantityMt + r.ShortageQuantityMt);
        var remainingMt = decimal.Round(leg.QuantityMt - consumedMt, 4, MidpointRounding.AwayFromZero);
        if (remainingMt < 0m) remainingMt = 0m;

        var model = new InventoryTransportReceiptCreateViewModel
        {
            InventoryTransportLegId = leg.Id,
            ReceiptDate = _businessClock.Today,
            ReceiptDestination = selectedDestination,
            DestinationTerminalId = leg.DestinationTerminalId,
            DestinationStorageTankId = leg.DestinationStorageTankId,
            ReceivedQuantityMt = remainingMt,
            ShortageQuantityMt = 0m,
            AllowanceMt = leg.TransportType is LoadingTransportType.Truck or LoadingTransportType.Wagon ? 0m : null,
            ChargeableShortageMt = leg.TransportType is LoadingTransportType.Truck or LoadingTransportType.Wagon ? 0m : null,
            ShortageChargeUsd = leg.TransportType is LoadingTransportType.Truck or LoadingTransportType.Wagon ? 0m : null,
            SaleDate = _businessClock.Today,
            SaleCurrency = SystemCurrency.BaseCurrencyCode,
            DirectDispatchDate = _businessClock.Today,
            DirectDispatchLoadedQuantityMt = remainingMt,
            DirectDispatchDestinationLocationId = leg.DestinationLocationId
        };
        PopulateLegDisplay(model, leg);
        await PopulateLookupsAsync(model);
        // حالتِ «فروش مستقیم و تسویه»: فرم ساده و متمرکز فقط برای سناریوی «موتر بعد از رسیدن،
        // بار را مستقیم می‌فروشد» (بدون گزینه‌های مخزن/تخلیه). فقط نمایشی؛ منطق ثبت همان DirectSale است.
        ViewData["FocusedSale"] = selectedDestination == InventoryTransportReceiptDestination.DirectSale;
        ViewData["FocusedInventoryReceipt"] = focused
            && selectedDestination == InventoryTransportReceiptDestination.ToInventory;
        ViewData["RemainingMt"] = remainingMt;
        return View(model);
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(InventoryTransportReceiptCreateViewModel model, bool focused = false)
    {
        if (model.ReceiptDestination == InventoryTransportReceiptDestination.DirectDispatch)
        {
            TempData["ok"] = "انتقال به وسیلهٔ دیگر اکنون از Workflow مشترک حمل ثبت می‌شود.";
            return RedirectToAction("Continue", "Transports", new { transportLegId = model.InventoryTransportLegId });
        }

        // فرم متمرکز فروش پس از خطای اعتبارسنجی هم باید ساده بماند.
        ViewData["FocusedSale"] = model.ReceiptDestination == InventoryTransportReceiptDestination.DirectSale;
        ViewData["FocusedInventoryReceipt"] = focused
            && model.ReceiptDestination == InventoryTransportReceiptDestination.ToInventory;
        var leg = await _receiptService.LoadLegAsync(model.InventoryTransportLegId, tracking: true);
        if (leg is null)
        {
            ModelState.AddModelError(nameof(model.InventoryTransportLegId), "Transport leg مورد نظر وجود ندارد.");
        }
        else
        {
            PopulateLegDisplay(model, leg);
            await _receiptService.ValidateAsync(model, leg, ModelState);
        }

        var saleConversion = ModelState.IsValid
            ? await _receiptService.ResolveSaleConversionAsync(model, ModelState)
            : null;

        if (!ModelState.IsValid)
        {
            await PopulateLookupsAsync(model);
            return View(model);
        }

        await using var transaction = _db.Database.IsRelational()
            ? await _db.Database.BeginTransactionAsync()
            : null;

        var receipt = await _receiptService.ApplyAsync(model, leg!, saleConversion);

        // وقتی آخرین نتیجهٔ حمل ثبت شده و همین نتیجه واقعاً کرایه دارد، وضعیت کرایهٔ کل
        // سفر نیز نهایی است. در تحویل جزئی پرچم باز می‌ماند تا کرایهٔ باقیمانده بعداً
        // قابل ثبت باشد؛ موجودی و محاسبات کرایه در اینجا دوباره نوشته نمی‌شوند.
        var freightSettledWithFinalOutcome = leg!.Status == InventoryTransportLegStatus.Received
            && (receipt.FreightRateUsdPerMt.HasValue || receipt.FreightCostUsd.HasValue);
        if (freightSettledWithFinalOutcome)
        {
            leg.IsFreightSettled = true;
            leg.FreightSettledDate = receipt.ReceiptDate.Date;
            await _db.SaveChangesAsync();
        }

        if (transaction is not null)
        {
            await transaction.CommitAsync();
        }

        TempData["ok"] = model.ReceiptDestination == InventoryTransportReceiptDestination.DirectSale
            ? freightSettledWithFinalOutcome
                ? "فروش مستقیم از حمل ثبت و کرایه تسویه شد (بدون ورود به مخزن)."
                : "فروش مستقیم از حمل ثبت شد (بدون ورود به مخزن)؛ کرایه هنوز تسویه نشده است."
            : "رسید مقصد انتقال از موجودی ثبت شد و موجودی مقصد افزایش یافت.";
        return RedirectToAction("Details", "InventoryTransportLegs", new { id = leg!.Id });
    }

    private Task<InventoryTransportLeg?> LoadLegAsync(int id, bool tracking)
        => _receiptService.LoadLegAsync(id, tracking);

    private async Task PopulateLookupsAsync(InventoryTransportReceiptCreateViewModel model)
    {
        ViewBag.DestinationTerminals = new SelectList(
            await _db.Terminals.AsNoTracking().OrderBy(t => t.Name).ToListAsync(),
            "Id",
            "Name",
            model.DestinationTerminalId);
        var destinationTanks = await StorageTankDisplay.LoadOptionsAsync(
            _db.StorageTanks.AsNoTracking().OrderBy(t => t.DisplayName ?? t.TankCode));
        ViewBag.DestinationStorageTanks = new SelectList(
            destinationTanks,
            "Id",
            "Display",
            model.DestinationStorageTankId);
        // نگاشت مخزن → ترمینال برای فیلتر سمت کلاینت (فقط مخزن‌های همان ترمینال نمایش داده شوند)
        ViewBag.DestinationStorageTankTerminalMap = destinationTanks
            .Select(t => new { id = t.Id, code = t.Display, terminalId = t.TerminalId })
            .ToList();
        ViewBag.Customers = new SelectList(
            await _db.Customers.AsNoTracking().Where(c => c.IsActive).OrderBy(c => c.Name).ToListAsync(),
            "Id",
            "Name",
            model.SaleCustomerId);
        ViewBag.Trucks = new SelectList(
            await _db.Trucks.AsNoTracking().Where(t => t.IsActive).OrderBy(t => t.PlateNumber).ToListAsync(),
            "Id",
            "PlateNumber",
            model.DirectDispatchTruckId);
        ViewBag.Drivers = new SelectList(
            await _db.Drivers.AsNoTracking().Where(d => d.IsActive).OrderBy(d => d.FullName).ToListAsync(),
            "Id",
            "FullName",
            model.DirectDispatchDriverId);
        ViewBag.Locations = new SelectList(
            await _db.Locations.AsNoTracking().Where(l => l.IsActive).OrderBy(l => l.Name).ToListAsync(),
            "Id",
            "Name",
            model.DirectDispatchDestinationLocationId);
        ViewBag.ServiceProviders = new SelectList(
            await _db.ServiceProviders
                .AsNoTracking()
                .Where(p => p.IsActive || (model.ServiceProviderId.HasValue && p.Id == model.ServiceProviderId.Value))
                .OrderBy(p => p.Name)
                .Select(p => new
                {
                    p.Id,
                    Text = string.IsNullOrWhiteSpace(p.Code) ? p.Name : p.Code + " - " + p.Name
                })
                .ToListAsync(),
            "Id",
            "Text",
            model.ServiceProviderId);
        ViewBag.OperationalAssets = new SelectList(
            await _db.OperationalAssets
                .AsNoTracking()
                .Where(a => a.IsActive || (model.OperationalAssetId.HasValue && a.Id == model.OperationalAssetId.Value))
                .OrderBy(a => a.AssetCode)
                .ThenBy(a => a.Name)
                .Select(a => new
                {
                    a.Id,
                    Text = a.AssetCode + " - " + a.Name
                })
                .ToListAsync(),
            "Id",
            "Text",
            model.OperationalAssetId);
        var currencies = await _db.Currencies.AsNoTracking().Where(c => c.IsActive).OrderBy(c => c.Code).ToListAsync();
        ViewBag.Currencies = currencies.Count == 0
            ? new SelectList(new[] { SystemCurrency.BaseCurrencyCode }, model.SaleCurrency)
            : new SelectList(currencies, "Code", "Code", model.SaleCurrency);
        ViewBag.ReceiptDestinations = Enum.GetValues<InventoryTransportReceiptDestination>()
            .Select(destination => new SelectListItem
            {
                Value = ((int)destination).ToString(),
                Text = ReceiptDestinationText(destination),
                Selected = destination == model.ReceiptDestination
            })
            .ToList();

        // کرایه‌های قبلی همین مرحله را برای نمایش read-only بارگذاری کن (هم در GET هم در redisplay پس از خطا).
        await PopulateExistingFreightExpensesAsync(model);
    }

    private string ReceiptDestinationText(InventoryTransportReceiptDestination destination)
        => destination switch
        {
            InventoryTransportReceiptDestination.ToInventory => UiText.T(HttpContext, "ورود به موجودی", "To Inventory"),
            InventoryTransportReceiptDestination.DirectSale => UiText.T(HttpContext, "فروش مستقیم", "Direct Sale"),
            InventoryTransportReceiptDestination.DirectDispatch => UiText.T(HttpContext, "نقل مستقیم", "Direct Dispatch"),
            InventoryTransportReceiptDestination.Mixed => UiText.T(HttpContext, "ترکیبی", "Mixed"),
            _ => destination.ToString()
        };

    private static void PopulateLegDisplay(InventoryTransportReceiptCreateViewModel model, InventoryTransportLeg leg)
    {
        model.LegLabel = $"Transport Leg #{leg.Id} — {DateDisplay.Date(leg.LoadedDate)} — {leg.WagonNumber ?? leg.RwbNo ?? leg.BillOfLadingNumber}";
        model.ContractNumber = leg.SourcePurchaseContract?.ContractNumber ?? "";
        model.ProductName = leg.Product?.Name ?? "";
        model.SourceStorageTankName = leg.SourceStorageTank?.DisplayName
            ?? leg.SourceStorageTank?.TankCode
            ?? "";
        model.TransportType = leg.TransportType;
        model.LoadedQuantityMt = leg.QuantityMt;
        // فقط‌نمایشی: نمبر موتر و سند حمل از مرحلهٔ حمل می‌آید (در فرم read-only نمایش داده می‌شود).
        model.VehicleNumber = leg.WagonNumber;
        model.DocumentReference = leg.RwbNo ?? leg.BillOfLadingNumber;
    }

    // کرایه‌های قبلاً ثبت‌شدهٔ همین مرحلهٔ حمل (نوع کرایهٔ رسید) را فقط برای نمایش/لینک بارگذاری می‌کند.
    // فقط‌خواندنی است و هیچ هزینه/سند مالی نمی‌سازد.
    private async Task PopulateExistingFreightExpensesAsync(InventoryTransportReceiptCreateViewModel model)
    {
        if (model.InventoryTransportLegId <= 0)
        {
            model.ExistingFreightExpenses = [];
            return;
        }

        model.ExistingFreightExpenses = await _db.ExpenseTransactions
            .AsNoTracking()
            .Where(e => e.TransportLegId == model.InventoryTransportLegId
                && !e.IsCancelled
                && e.ExpenseType != null
                && e.ExpenseType.Code == InventoryTransportReceiptService.ReceiptFreightExpenseCode)
            .OrderByDescending(e => e.ExpenseDate)
            .ThenByDescending(e => e.Id)
            .Select(e => new InventoryTransportLegExpenseItemViewModel
            {
                Id = e.Id,
                ExpenseDate = e.ExpenseDate,
                ExpenseTypeName = e.ExpenseType!.NamePersian ?? e.ExpenseType.Name,
                ServiceProviderName = e.ServiceProvider != null ? e.ServiceProvider.Name : null,
                OperationalAssetName = e.OperationalAsset != null ? e.OperationalAsset.Name : null,
                AmountUsd = e.AmountUsd,
                Description = e.Description
            })
            .ToListAsync();
    }
}
