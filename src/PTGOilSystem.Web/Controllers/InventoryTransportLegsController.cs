using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using System.Security.Cryptography;
using System.Text;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Helpers;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.InventoryTransport;
using PTGOilSystem.Web.Security;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Audit;
using PTGOilSystem.Web.Services.Exceptions;
using PTGOilSystem.Web.Services.Time;

namespace PTGOilSystem.Web.Controllers;

[Authorize]
public partial class InventoryTransportLegsController : Controller
{
    // پیشوند یادداشت رسیدهای «همراه» موتر چندواگنه در انتقال گروهی؛ پیوند لغو گروهی به رسید اصلی.
    private const string GroupTransferCompanionNotePrefix = "[انتقال همراه رسید #";

    private readonly ApplicationDbContext _db;
    private readonly IStockService _stock;
    private readonly InventoryTransportLegLoadService _legLoad;
    private readonly InventoryTransportBatchService _batchTransport;
    private readonly ICurrencyConversionService _currencyConversion;
    private readonly IAuditService _audit;
    private readonly ILogger<InventoryTransportLegsController> _logger;
    private readonly IInventoryLineageWriter _lineage;

    public InventoryTransportLegsController(ApplicationDbContext db, IStockService stock)
        : this(
            db,
            stock,
            new InventoryTransportLegLoadService(db, stock),
            new InventoryTransportBatchService(db, stock),
            new CurrencyConversionService(new PricingService(db)),
            new AuditService(db),
            NullLogger<InventoryTransportLegsController>.Instance,
            InventoryLineageWriterFactory.Disabled(db))
    {
    }

    private readonly IAfghanistanBusinessClock _businessClock;

    [ActivatorUtilitiesConstructor]
    public InventoryTransportLegsController(
        ApplicationDbContext db,
        IStockService stock,
        InventoryTransportLegLoadService legLoad,
        InventoryTransportBatchService batchTransport,
        ICurrencyConversionService currencyConversion,
        IAuditService audit,
        ILogger<InventoryTransportLegsController> logger,
        IInventoryLineageWriter lineage,
        Services.Accounting.IExpenseAccountingAdapter? expenseAccounting = null,
        IAfghanistanBusinessClock? businessClock = null)
    {
        // مرجع «امروزِ کاری» همیشه ساعت کابل است، نه تاریخ UTC سرور.
        _businessClock = businessClock ?? new AfghanistanBusinessClock(TimeProvider.System);
        _db = db;
        _stock = stock;
        _legLoad = legLoad;
        _batchTransport = batchTransport;
        _currencyConversion = currencyConversion;
        _audit = audit;
        _logger = logger;
        _lineage = lineage;
        _expenseAccounting = expenseAccounting;
    }

    // مرحله ۵ — Dual-write اختیاری به دفتر کل جدید. پشت Feature Flag و null-safe.
    private readonly Services.Accounting.IExpenseAccountingAdapter? _expenseAccounting;

    public async Task<IActionResult> Index(InventoryTransportLegIndexFilterViewModel filter, int page = 1, [FromQuery(Name = "pageSize")] int? perPage = null)
    {
        var pageSize = ListPageSize.Resolve(perPage, 5);
        ViewData["PageSize"] = pageSize;
        ViewData["DefaultPageSize"] = 5;
        var exportAll = page <= 0;

        var query = _db.InventoryTransportLegs
            .AsNoTracking()
            .Include(l => l.SourcePurchaseContract)
            .Include(l => l.Shipment)
            .Include(l => l.Product)
            .Include(l => l.SourceTerminal)
            .Include(l => l.SourceStorageTank)
            .AsQueryable();

        // Apply filters
        if (filter.FromDate.HasValue)
        {
            query = query.Where(l => l.LoadedDate >= filter.FromDate.Value);
        }

        if (filter.ToDate.HasValue)
        {
            query = query.Where(l => l.LoadedDate <= filter.ToDate.Value);
        }

        if (filter.ContractId.HasValue)
        {
            query = query.Where(l => l.SourcePurchaseContractId == filter.ContractId.Value);
        }

        if (filter.ProductId.HasValue)
        {
            query = query.Where(l => l.ProductId == filter.ProductId.Value);
        }

        if (filter.Status.HasValue)
        {
            query = query.Where(l => l.Status == filter.Status.Value);
        }

        if (!string.IsNullOrWhiteSpace(filter.Query))
        {
            var searchTerm = filter.Query.Trim().ToLower();
            query = query.Where(l =>
                (l.WagonNumber != null && l.WagonNumber.ToLower().Contains(searchTerm)) ||
                (l.RwbNo != null && l.RwbNo.ToLower().Contains(searchTerm)) ||
                (l.SourcePurchaseContract != null && l.SourcePurchaseContract.ContractNumber.ToLower().Contains(searchTerm)) ||
                (l.Product != null && l.Product.Name.ToLower().Contains(searchTerm)));
        }

        var totalCount = await query.CountAsync();
        var pageCount = Math.Max(1, (int)Math.Ceiling(totalCount / (double)pageSize));
        page = Math.Clamp(page, 1, pageCount);

        var items = await query
            .OrderByDescending(l => l.LoadedDate)
            .ThenByDescending(l => l.Id)
            .Skip(exportAll ? 0 : (page - 1) * pageSize)
            .Take(exportAll ? totalCount : pageSize)
            .Select(l => new InventoryTransportLegListItemViewModel
            {
                Id = l.Id,
                ShipmentId = l.ShipmentId,
                ShipmentCode = l.Shipment != null ? l.Shipment.ShipmentCode : null,
                ContractNumber = l.SourcePurchaseContract != null ? l.SourcePurchaseContract.ContractNumber : "",
                ProductName = l.Product != null ? l.Product.Name : "",
                SourceTerminalName = l.SourceTerminal != null ? l.SourceTerminal.Name : "",
                SourceTankCode = l.SourceStorageTank == null
                    ? null
                    : l.SourceStorageTank.DisplayName == null || l.SourceStorageTank.DisplayName == ""
                        ? l.SourceStorageTank.TankCode
                        : l.SourceStorageTank.DisplayName,
                WagonNumber = l.Wagon != null ? l.Wagon.WagonNumber : l.Truck != null ? l.Truck.PlateNumber : l.WagonNumber,
                RwbNo = l.RwbNo,
                    TransportType = l.TransportType,
                PurchaseUnitCostUsd = l.PurchaseUnitCostUsd,
                ServiceProviderId = l.ServiceProviderId,
                ServiceProviderName = l.ServiceProvider != null ? l.ServiceProvider.Name : null,
                OperationalAssetId = l.OperationalAssetId,
                OperationalAssetName = l.OperationalAsset != null ? l.OperationalAsset.Name : null,
                QuantityMt = l.QuantityMt,
                LoadedDate = l.LoadedDate,
                Status = l.Status,
                OutboundInventoryMovementId = l.OutboundInventoryMovementId
            })
            .ToListAsync();

        await PopulateIndexLookupsAsync();

        // آمار کامل روی همهٔ رکوردهای مطابق فیلتر (نه فقط این صفحه) برای کارت‌های آماری و ردیف جمع.
        ViewBag.SumQuantity = await query.SumAsync(l => l.QuantityMt);
        ViewBag.ActiveCount = await query.CountAsync(l =>
            l.Status != InventoryTransportLegStatus.Cancelled
            && l.Status != InventoryTransportLegStatus.Received);
        ViewBag.PostedCount = await query.CountAsync(l => l.OutboundInventoryMovementId != null);

        return View(new InventoryTransportLegIndexViewModel
        {
            Filter = filter,
            Items = items,
            CurrentPage = page,
            PageCount = pageCount,
            TotalCount = totalCount
        });
    }

    private async Task PopulateIndexLookupsAsync()
    {
        var contracts = await _db.Contracts
            .AsNoTracking()
            .Where(c => c.ContractType == ContractType.Purchase)
            .OrderByDescending(c => c.Id)
            .Select(c => new { c.Id, c.ContractNumber })
            .ToListAsync();
        ViewBag.Contracts = new SelectList(contracts, "Id", "ContractNumber");

        var products = await _db.Products
            .AsNoTracking()
            .OrderBy(p => p.Name)
            .Select(p => new { p.Id, p.Name })
            .ToListAsync();
        ViewBag.Products = new SelectList(products, "Id", "Name");

        ViewBag.StatusOptions = new SelectList(new[]
        {
            new { Value = (int)InventoryTransportLegStatus.Draft, Text = "Draft" },
            new { Value = (int)InventoryTransportLegStatus.Loaded, Text = "Loaded" },
            new { Value = (int)InventoryTransportLegStatus.InTransit, Text = "In Transit" },
            new { Value = (int)InventoryTransportLegStatus.Received, Text = "Received" },
            new { Value = (int)InventoryTransportLegStatus.Cancelled, Text = "Cancelled" }
        }, "Value", "Text");
    }

    public async Task<IActionResult> Active()
    {
        var transports = await BuildActiveTransportFlowsAsync();
        return View(new InventoryTransportFlowDashboardViewModel { Transports = transports });
    }

    public async Task<IActionResult> Details(int id, string? returnUrl = null)
    {
        var model = await BuildDetailsViewModelAsync(id);
        ViewBag.ReturnUrl = !string.IsNullOrWhiteSpace(returnUrl) && Url?.IsLocalUrl(returnUrl) == true
            ? returnUrl
            : null;
        return model is null ? NotFound() : View(model);
    }

    // صفحهٔ «جریان کشتی» حذف شده است: همهٔ ورودی‌های قدیمی (لینک‌ها، ریدایرکت‌های بعد از ثبت/ویرایش/تخلیه)
    // به صفحهٔ جزئیات حمل هدایت می‌شوند. فقط ریدایرکت؛ هیچ داده‌ای تغییر نمی‌کند.
    public async Task<IActionResult> Journey(string? groupKey = null, int? legId = null, string? returnUrl = null)
    {
        if (legId.HasValue)
        {
            return RedirectToAction(nameof(Details), new { id = legId.Value, returnUrl });
        }

        var resolvedGroupKey = groupKey?.Trim();
        if (!string.IsNullOrWhiteSpace(resolvedGroupKey))
        {
            var legs = await LoadTransportGroupLegsAllStatusesAsync(resolvedGroupKey);
            if (legs.Count > 0)
            {
                return RedirectToAction(nameof(Details), new { id = legs[0].Id, returnUrl });
            }
        }

        return RedirectToAction(nameof(Index));
    }

    public IActionResult Create(int? shipmentId = null, string? returnUrl = null)
        => RedirectToAction(nameof(CreateFromInventory), new { shipmentId });

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult Create(InventoryTransportLegCreateViewModel model)
        => RedirectToAction(nameof(CreateFromInventory), new { shipmentId = model.ShipmentId });

    // The single supported inventory transport entry point.
    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpGet]
    public async Task<IActionResult> CreateFromInventory(
        int? shipmentId = null,
        string? returnUrl = null,
        int? sourceTerminalId = null,
        int? sourceStorageTankId = null,
        int? productId = null)
    {
        // اگر مبدأ (ترمینال/مخزن/محصول) از قبل داده شود — مثلاً از دکمهٔ «حمل بعدی از این مخزن»
        // در پروندهٔ کشتی — همان مبدأ پیش‌پر می‌شود و مستقیم به مرحلهٔ انتخاب موجودی می‌رویم.
        var hasPrefilledSource = sourceTerminalId > 0 && sourceStorageTankId > 0 && productId > 0;

        // انتشار خودکار کشتی: اگر کشتی صریح داده نشده ولی مبدأ پیش‌پر است، از رسیدهای مرحلهٔ قبلِ
        // همان مخزن، کشتی را استنتاج می‌کنیم تا زنجیره بدون کشتی نشکند. اگر مبهم بود، هشدار می‌دهیم.
        int? resolvedShipmentId = shipmentId > 0 ? shipmentId : null;
        var shipmentAmbiguous = false;
        if (resolvedShipmentId is null && hasPrefilledSource)
        {
            var inference = await _batchTransport.InferShipmentForTankAsync(
                sourceTerminalId!.Value, sourceStorageTankId!.Value, productId!.Value);
            resolvedShipmentId = inference.ShipmentId;
            shipmentAmbiguous = inference.IsAmbiguous;
        }

        var model = new InventoryTransportFromInventoryViewModel
        {
            ShipmentId = resolvedShipmentId,
            ShipmentLocked = resolvedShipmentId.HasValue,
            ShipmentLinkAmbiguous = shipmentAmbiguous,
            IsChainContinuation = hasPrefilledSource,
            ReturnUrl = !string.IsNullOrWhiteSpace(returnUrl) && Url?.IsLocalUrl(returnUrl) == true ? returnUrl : null,
            SourceTerminalId = hasPrefilledSource ? sourceTerminalId!.Value : 0,
            SourceStorageTankId = hasPrefilledSource ? sourceStorageTankId!.Value : 0,
            ProductId = hasPrefilledSource ? productId!.Value : 0,
            ActiveStep = hasPrefilledSource ? 2 : 1,
            TransportDate = _businessClock.Today,
            Vehicles = [new InventoryTransportVehicleInput()]
        };
        await PopulateCreateFromInventoryLookupsAsync(model);
        return View(model);
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpGet]
    public async Task<IActionResult> InventorySources(int terminalId, int storageTankId, int productId, int? shipmentId = null)
    {
        var sources = await _batchTransport.GetAvailableSourcesAsync(terminalId, storageTankId, productId, shipmentId);
        return Json(new
        {
            ok = true,
            sources = sources.Select(s => new
            {
                sourceInventoryMovementId = s.SourceInventoryMovementId,
                contractNumber = s.ContractNumber,
                receiptReference = s.ReceiptReference,
                sourceKind = s.SourceKind,
                sourceDate = s.SourceDate.ToString("yyyy-MM-dd"),
                availableQuantityMt = s.AvailableQuantityMt
            })
        });
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequestFormLimits(ValueCountLimit = 200_000)]
    public async Task<IActionResult> CreateFromInventory(
        InventoryTransportFromInventoryViewModel model,
        [FromForm(Name = FormTokenHtmlHelper.FieldName)] string? formToken = null)
    {
        model.Notes = NormalizeOptionalString(model.Notes);
        model.Sources ??= [];
        model.Vehicles ??= [];
        model.ActiveStep = Math.Clamp(model.ActiveStep, 1, 4);

        // ورود از پروندهٔ محموله: مبدأ = محموله؛ ترمینال/مخزن در فرم پنهان‌اند، پس از خودِ محموله استنتاج
        // می‌شوند تا اعتبارسنجی Range روی این دو فیلد رد نشود. کاربر مخزن انتخاب نمی‌کند.
        if (model.ShipmentId is > 0 && model.ProductId > 0
            && (model.SourceTerminalId <= 0 || model.SourceStorageTankId <= 0))
        {
            var (resolvedTerminalId, resolvedStorageTankId) =
                await _batchTransport.ResolveShipmentSourceLocationAsync(model.ShipmentId.Value, model.ProductId);
            if (model.SourceTerminalId <= 0)
            {
                model.SourceTerminalId = resolvedTerminalId;
                ModelState.Remove(nameof(model.SourceTerminalId));
            }
            if (model.SourceStorageTankId <= 0)
            {
                model.SourceStorageTankId = resolvedStorageTankId;
                ModelState.Remove(nameof(model.SourceStorageTankId));
            }
        }

        if (string.IsNullOrWhiteSpace(formToken))
        {
            model.ActiveStep = 4;
            ModelState.AddModelError(
                FormTokenHtmlHelper.FieldName,
                "توکن فورم موجود نیست. صفحه را تازه کنید و دوباره ثبت نمایید.");
        }

        if (ModelState.IsValid)
        {
            try
            {
                var batch = await _batchTransport.CreateAsync(model, formToken);
                TempData["ok"] = model.SubmissionMode == InventoryTransportSubmissionMode.Loaded
                    ? $"سند {batch.BatchNumber} ثبت شد و خروجی موجودی برای هر سهم ساخته شد."
                    : $"پیش‌نویس {batch.BatchNumber} بدون تغییر موجودی ثبت شد.";
                // بعد از ثبت، مستقیم به جزئیات اولین حمل ساخته‌شده برو (صفحهٔ جریان نمایش داده نمی‌شود).
                var firstLegId = batch.Legs.OrderBy(l => l.Id).Select(l => l.Id).FirstOrDefault();
                return firstLegId > 0
                    ? RedirectToAction(nameof(Details), new { id = firstLegId })
                    : RedirectToAction(nameof(Index));
            }
            catch (BusinessRuleException ex)
            {
                ModelState.AddModelError(GetCreateFromInventoryErrorKey(ex.Code), ex.Message);
            }
            catch (Exception ex) when (IsInventoryTransportSchemaUnavailable(ex))
            {
                _logger.LogError(ex, "Inventory transport batch schema is not available.");
                ModelState.AddModelError(
                    string.Empty,
                    "ساختار دیتابیس حمل گروهی آماده نیست. Migration مربوط به InventoryTransportBatches باید اجرا شود.");
            }
        }

        model.ActiveStep = ResolveCreateFromInventoryStep(ModelState, model.ActiveStep);
        if (model.Vehicles.Count == 0)
        {
            model.Vehicles.Add(new InventoryTransportVehicleInput());
        }
        await PopulateCreateFromInventoryLookupsAsync(model);
        return View(model);
    }

    // Reads an Excel sheet of trucks/wagons (نوع | نمبر پلیت/واگن | مقدار MT | کرایه | ارز)
    // and returns the parsed rows as JSON. Carrier/driver are left for the user to
    // pick in the grid; this endpoint only creates no data and touches nothing.
    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ImportVehiclesExcel(IFormFile? file, CancellationToken ct = default)
    {
        if (file is null || file.Length == 0)
        {
            return Json(new { ok = false, message = "فایلی انتخاب نشده است." });
        }
        if (file.Length > 5 * 1024 * 1024)
        {
            return Json(new { ok = false, message = "حجم فایل زیاد است (حداکثر ۵ مگابایت)." });
        }

        IReadOnlyList<InventoryTransportVehicleImportRow> parsed;
        try
        {
            await using var workbook = await ExcelWorkbookNormalizer.OpenAsync(file, ct);
            parsed = InventoryTransportVehicleWorkbookParser.Parse(workbook.Stream);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Inventory transport vehicle import failed to parse.");
            return Json(new { ok = false, message = "خواندن فایل اکسل ناموفق بود: " + ex.Message });
        }

        if (parsed.Count == 0)
        {
            return Json(new { ok = false, message = "در فایل هیچ ردیف معتبری (نمبر و مقدار) یافت نشد." });
        }

        var currencies = await _db.Currencies.AsNoTracking()
            .Where(c => c.IsActive)
            .Select(c => new { c.Id, c.Code, c.Name, c.NamePersian, c.Symbol })
            .ToListAsync(ct);

        static string Norm(string? value)
            => new((value ?? string.Empty).Trim().ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

        int? MatchCurrency(string? text)
        {
            var normalized = Norm(text);
            if (normalized.Length == 0)
            {
                return null;
            }
            var match = currencies.FirstOrDefault(c =>
                Norm(c.Code) == normalized
                || Norm(c.Name) == normalized
                || Norm(c.NamePersian) == normalized
                || Norm(c.Symbol) == normalized);
            return match?.Id;
        }

        var vehicles = parsed.Select(row => new
        {
            transportType = row.TransportTypeValue.ToString(),
            vehicleNumber = row.VehicleNumber,
            quantityMt = row.QuantityMt,
            freightWeightMt = row.FreightWeightMt,
            freightAmount = row.FreightAmount,
            freightCurrencyId = MatchCurrency(row.CurrencyText),
            rwbNo = row.RwbNo
        }).ToList();

        return Json(new { ok = true, vehicles });
    }

    private static string GetCreateFromInventoryErrorKey(string code)
    {
        if (code is "INVENTORY_TRANSPORT_SOURCE_INVALID")
        {
            return nameof(InventoryTransportFromInventoryViewModel.SourceTerminalId);
        }

        if (code.Contains("SOURCE", StringComparison.Ordinal))
        {
            return nameof(InventoryTransportFromInventoryViewModel.Sources);
        }

        if (code.Contains("VEHICLE", StringComparison.Ordinal)
            || code.Contains("TRUCK", StringComparison.Ordinal)
            || code.Contains("WAGON", StringComparison.Ordinal)
            || code.Contains("DRIVER", StringComparison.Ordinal)
            || code.Contains("CARRIER", StringComparison.Ordinal)
            || code.Contains("PROVIDER", StringComparison.Ordinal)
            || code.Contains("ASSET", StringComparison.Ordinal)
            || code.Contains("CAPACITY", StringComparison.Ordinal)
            || code.Contains("FREIGHT", StringComparison.Ordinal)
            || code.Contains("CURRENCY", StringComparison.Ordinal)
            || code.Contains("ALLOCATION", StringComparison.Ordinal)
            || code is "INVENTORY_TRANSPORT_LEG_TOTAL" or "INVENTORY_TRANSPORT_BATCH_TOTAL")
        {
            return nameof(InventoryTransportFromInventoryViewModel.Vehicles);
        }

        return code switch
        {
            "INVENTORY_TRANSPORT_DATE_REQUIRED" => nameof(InventoryTransportFromInventoryViewModel.TransportDate),
            "INVENTORY_TRANSPORT_TANK_INVALID" or "INVENTORY_TRANSPORT_TANK_PRODUCT" => nameof(InventoryTransportFromInventoryViewModel.SourceStorageTankId),
            _ => string.Empty
        };
    }

    private static int ResolveCreateFromInventoryStep(
        Microsoft.AspNetCore.Mvc.ModelBinding.ModelStateDictionary modelState,
        int requestedStep)
    {
        var invalidKeys = modelState
            .Where(entry => entry.Value?.Errors.Count > 0)
            .Select(entry => entry.Key)
            .ToArray();

        if (invalidKeys.Any(key => key.StartsWith(nameof(InventoryTransportFromInventoryViewModel.Vehicles), StringComparison.OrdinalIgnoreCase)))
        {
            return 3;
        }
        if (invalidKeys.Any(key => key.StartsWith(nameof(InventoryTransportFromInventoryViewModel.Sources), StringComparison.OrdinalIgnoreCase)))
        {
            return 2;
        }
        if (invalidKeys.Any(key => key is nameof(InventoryTransportFromInventoryViewModel.SourceTerminalId)
            or nameof(InventoryTransportFromInventoryViewModel.SourceStorageTankId)
            or nameof(InventoryTransportFromInventoryViewModel.ProductId)
            or nameof(InventoryTransportFromInventoryViewModel.TransportDate)))
        {
            return 1;
        }

        return Math.Clamp(requestedStep, 1, 4);
    }

    private static bool IsInventoryTransportSchemaUnavailable(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException postgres
                && postgres.SqlState is "42P01" or "42703")
            {
                return true;
            }
        }

        return false;
    }

    private async Task PopulateCreateFromInventoryLookupsAsync(InventoryTransportFromInventoryViewModel model)
    {
        ViewBag.InventoryTransportTerminals = await _db.Terminals.AsNoTracking()
            .Where(t => t.IsActive)
            .OrderBy(t => t.Name)
            .Select(t => new InventoryTransportLookupOption { Id = t.Id, Name = t.Name })
            .ToListAsync();
        ViewBag.InventoryTransportProducts = await _db.Products.AsNoTracking()
            .Where(p => p.IsActive)
            .OrderBy(p => p.Name)
            .Select(p => new InventoryTransportLookupOption { Id = p.Id, Name = p.Name })
            .ToListAsync();
        ViewBag.InventoryTransportTanks = await _db.StorageTanks.AsNoTracking()
            .Where(t => t.IsActive)
            .OrderBy(t => t.TankCode)
            .Select(t => new InventoryTransportLookupOption
            {
                Id = t.Id,
                TerminalId = t.TerminalId,
                ProductId = t.ProductId,
                Name = t.DisplayName == null || t.DisplayName == "" ? t.TankCode : t.DisplayName
            })
            .ToListAsync();
        ViewBag.InventoryTransportTrucks = await _db.Trucks.AsNoTracking()
            .Where(t => t.IsActive)
            .OrderBy(t => t.PlateNumber)
            .Select(t => new InventoryTransportLookupOption { Id = t.Id, Name = t.PlateNumber, CapacityMt = t.MaxLoadMt })
            .ToListAsync();
        ViewBag.InventoryTransportWagons = await _db.Wagons.AsNoTracking()
            .Where(w => w.IsActive)
            .OrderBy(w => w.WagonNumber)
            .Select(w => new InventoryTransportLookupOption { Id = w.Id, Name = w.WagonNumber, CapacityMt = w.CapacityMt })
            .ToListAsync();
        ViewBag.InventoryTransportDrivers = await _db.Drivers.AsNoTracking()
            .Where(d => d.IsActive)
            .OrderBy(d => d.FullName)
            .Select(d => new InventoryTransportLookupOption { Id = d.Id, Name = d.FullName })
            .ToListAsync();
        ViewBag.InventoryTransportProviders = await _db.ServiceProviders.AsNoTracking()
            .Where(p => p.IsActive)
            .OrderBy(p => p.Name)
            .Select(p => new InventoryTransportLookupOption { Id = p.Id, Name = p.Name })
            .ToListAsync();
        ViewBag.InventoryTransportAssets = await _db.OperationalAssets.AsNoTracking()
            .Where(a => a.IsActive
                && (a.AssetType == OperationalAssetType.Truck
                    || a.AssetType == OperationalAssetType.TankerTruck
                    || a.AssetType == OperationalAssetType.Wagon))
            .OrderBy(a => a.AssetCode)
            .Select(a => new InventoryTransportLookupOption { Id = a.Id, Name = a.AssetCode + " - " + a.Name, Type = (int)a.AssetType, CapacityMt = a.CapacityMt })
            .ToListAsync();
        ViewBag.InventoryTransportCurrencies = await _db.Currencies.AsNoTracking()
            .Where(c => c.IsActive)
            .OrderBy(c => c.Code)
            .Select(c => new InventoryTransportLookupOption { Id = c.Id, Name = c.Code })
            .ToListAsync();
        ViewBag.InventoryTransportSources = await _batchTransport.GetAvailableSourcesAsync(
            model.SourceTerminalId,
            model.SourceStorageTankId,
            model.ProductId,
            model.ShipmentId);
        ViewBag.InventoryTransportShipment = model.ShipmentId.HasValue
            ? await _db.Shipments.AsNoTracking()
                .Where(s => s.Id == model.ShipmentId.Value)
                .Select(s => new InventoryTransportLookupOption { Id = s.Id, Name = s.ShipmentCode })
                .FirstOrDefaultAsync()
            : null;
        // محموله‌های قابل انتخاب به‌عنوان مبدأ در خود فرم — فقط محموله‌های دارای قرارداد خرید.
        ViewBag.InventoryTransportShipments = await _db.Shipments.AsNoTracking()
            .Where(s => s.ContractId != null || _db.ShipmentContracts.Any(link => link.ShipmentId == s.Id))
            .OrderByDescending(s => s.DepartureDate ?? s.ArrivalDate)
            .ThenByDescending(s => s.Id)
            .Select(s => new InventoryTransportLookupOption
            {
                Id = s.Id,
                Name = s.Vessel == null || s.Vessel.Name == ""
                    ? s.ShipmentCode
                    : s.ShipmentCode + " — " + s.Vessel.Name
            })
            .Take(200)
            .ToListAsync();
    }


    [Authorize(Policy = AuthPolicies.ManageData)]
    public IActionResult CreateForShipment(int shipmentId, string? returnUrl = null)
        => RedirectToAction(nameof(CreateFromInventory), new { shipmentId });

    [Authorize(Policy = AuthPolicies.ManageData)]
    public IActionResult CreateBatch(int? shipmentId, string? returnUrl = null)
        => RedirectToAction(nameof(CreateFromInventory), new { shipmentId });

    // دادهٔ منبع/موجودی/KPI یک محموله برای انتخاب درجای «انتقال گروهی» (بدون رفرش صفحه).
    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpGet]
    public IActionResult BatchShipmentData(int shipmentId)
        => Json(new { ok = false, redirect = Url.Action(nameof(CreateFromInventory), new { shipmentId }) });

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult CreateForShipment([FromForm] int shipmentId)
        => RedirectToAction(nameof(CreateFromInventory), new { shipmentId });

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequestFormLimits(ValueCountLimit = 200_000)]
    public IActionResult CreateBatch([FromForm] int? shipmentId)
        => RedirectToAction(nameof(CreateFromInventory), new { shipmentId });

    private static string? NormalizeOptionalString(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public async Task<IActionResult> Edit(int id)
    {
        var leg = await _db.InventoryTransportLegs
            .AsNoTracking()
            .FirstOrDefaultAsync(l => l.Id == id);

        if (leg is null)
        {
            return NotFound();
        }

        if (leg.InventoryTransportBatchId.HasValue)
        {
            TempData["error"] = "Batch transport legs must be managed from the batch journey.";
            return RedirectToAction(nameof(Journey), new { groupKey = leg.TransportGroupKey });
        }

        if (!CanEdit(leg))
        {
            TempData["error"] = "Only draft transport legs without outbound movement can be edited.";
            return RedirectToAction(nameof(Details), new { id });
        }

        await PopulateLookupsAsync();
        return View(ToCreateViewModel(leg));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, InventoryTransportLegCreateViewModel model)
    {
        if (id != model.Id)
        {
            return BadRequest();
        }

        var leg = await _db.InventoryTransportLegs.FirstOrDefaultAsync(l => l.Id == id);
        if (leg is null)
        {
            return NotFound();
        }

        if (leg.InventoryTransportBatchId.HasValue)
        {
            TempData["error"] = "Batch transport legs must be managed from the batch journey.";
            return RedirectToAction(nameof(Journey), new { groupKey = leg.TransportGroupKey });
        }

        if (!CanEdit(leg))
        {
            TempData["error"] = "Only draft transport legs without outbound movement can be edited.";
            return RedirectToAction(nameof(Details), new { id });
        }

        Normalize(model);
        await ValidateCreateAsync(model);

        if (!ModelState.IsValid)
        {
            await PopulateLookupsAsync();
            return View(model);
        }

        leg.ShipmentId = model.ShipmentId;
        leg.SourcePurchaseContractId = model.SourcePurchaseContractId;
        leg.ProductId = model.ProductId;
        leg.SourceTerminalId = model.SourceTerminalId;
        leg.SourceStorageTankId = model.SourceStorageTankId;
        leg.DestinationTerminalId = model.DestinationTerminalId;
        leg.DestinationStorageTankId = model.DestinationStorageTankId;
        leg.DestinationLocationId = model.DestinationLocationId;
        leg.TransportType = model.TransportType;
        leg.WagonNumber = model.WagonNumber;
        leg.RwbNo = model.RwbNo;
        leg.BillOfLadingNumber = model.BillOfLadingNumber;
        leg.RouteDescription = model.RouteDescription;
        leg.ServiceProviderId = model.ServiceProviderId;
        leg.OperationalAssetId = model.OperationalAssetId;
        leg.LoadedDate = model.LoadedDate;
        leg.ExpectedArrivalDate = model.ExpectedArrivalDate;
        leg.QuantityMt = model.QuantityMt;
        leg.ChargeableQuantityMt = model.ChargeableQuantityMt;
        leg.PurchaseUnitCostUsd = model.PurchaseUnitCostUsd;
        leg.Notes = model.Notes;
        leg.UpdatedAtUtc = DateTime.UtcNow;

        await _db.SaveChangesAsync();

        TempData["ok"] = "Transport leg draft was updated.";
        return RedirectToAction(nameof(Details), new { id = leg.Id });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> MarkLoaded(int id)
    {
        var leg = await _db.InventoryTransportLegs
            .Include(l => l.SourcePurchaseContract)
            .Include(l => l.SourceStorageTank)
            .FirstOrDefaultAsync(l => l.Id == id);

        if (leg is null)
        {
            return NotFound();
        }

        if (leg.InventoryTransportBatchId.HasValue)
        {
            try
            {
                var batch = await _batchTransport.LoadDraftAsync(leg.InventoryTransportBatchId.Value);
                TempData["ok"] = "پیش‌نویس بارگیری شد و خروجی موجودی هر سهم ثبت گردید.";
                return RedirectToAction(nameof(Journey), new { groupKey = batch.TransportGroupKey });
            }
            catch (BusinessRuleException ex)
            {
                TempData["error"] = ex.Message;
                return RedirectToAction(nameof(Details), new { id });
            }
        }

        if (leg.Status != InventoryTransportLegStatus.Draft || leg.OutboundInventoryMovementId.HasValue)
        {
            TempData["error"] = "This transport leg is already loaded or cannot be loaded again.";
            return RedirectToAction(nameof(Details), new { id });
        }

        await MarkLegsLoadedAsync([leg]);
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> MarkGroupLoaded(string groupKey)
    {
        if (string.IsNullOrWhiteSpace(groupKey))
        {
            return NotFound();
        }

        var groupLegs = await LoadActiveTransportGroupLegsAsync(groupKey);
        if (groupLegs.Count == 0)
        {
            return NotFound();
        }

        var batchIds = groupLegs
            .Where(l => l.InventoryTransportBatchId.HasValue)
            .Select(l => l.InventoryTransportBatchId!.Value)
            .Distinct()
            .ToList();
        if (batchIds.Count == 1 && groupLegs.All(l => l.InventoryTransportBatchId == batchIds[0]))
        {
            try
            {
                var batch = await _batchTransport.LoadDraftAsync(batchIds[0]);
                TempData["ok"] = "پیش‌نویس بارگیری شد و خروجی موجودی هر سهم ثبت گردید.";
                return RedirectToAction(nameof(Journey), new { groupKey = batch.TransportGroupKey });
            }
            catch (BusinessRuleException ex)
            {
                TempData["error"] = ex.Message;
                return RedirectToAction(nameof(Journey), new { groupKey });
            }
        }

        var draftLegs = groupLegs
            .Where(l => l.Status == InventoryTransportLegStatus.Draft && !l.OutboundInventoryMovementId.HasValue)
            .OrderBy(l => l.LoadedDate)
            .ThenBy(l => l.Id)
            .ToList();

        if (draftLegs.Count == 0)
        {
            TempData["error"] = "This transport group has no draft allocation left to load.";
            return RedirectToAction(nameof(Journey), new { groupKey });
        }

        await MarkLegsLoadedAsync(draftLegs);
        return RedirectToAction(nameof(Journey), new { groupKey });
    }

    // رسید گروهی «به مخزن/انبار»: کل حمل با همهٔ تخصیص‌هایش یک‌جا رسید می‌شود و کسری بین قراردادها تقسیم می‌گردد.
    [Authorize(Policy = AuthPolicies.ManageData)]
    public async Task<IActionResult> CreateGroupReceipt(string groupKey, string? returnUrl = null)
    {
        if (string.IsNullOrWhiteSpace(groupKey))
        {
            return NotFound();
        }

        var legs = await LoadTransportGroupLegsForReceiptAsync(groupKey);
        if (legs.Count == 0)
        {
            return NotFound();
        }

        var model = BuildGroupReceiptCreateModel(legs, returnUrl);
        var availability = await BuildGroupReceiptAvailabilityAsync(legs);
        ApplyGroupReceiptAvailability(model, availability);
        model.TotalReceivedQuantityMt = model.AvailableQuantityMt;
        await PopulateGroupReceiptLookupsAsync(model);
        return View(model);
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateGroupReceipt(InventoryTransportGroupReceiptCreateViewModel model)
    {
        NormalizeGroupReceiptModel(model);

        List<InventoryTransportLeg> legs = string.IsNullOrWhiteSpace(model.GroupKey)
            ? []
            : await LoadTransportGroupLegsForReceiptAsync(model.GroupKey);
        var availability = await BuildGroupReceiptAvailabilityAsync(legs);
        ApplyGroupReceiptAvailability(model, availability);
        await ValidateGroupReceiptAsync(model, legs, availability);

        if (!ModelState.IsValid)
        {
            if (legs.Count > 0)
            {
                RefreshGroupReceiptCreateModel(model, legs);
                ApplyGroupReceiptAvailability(model, availability);
            }

            await PopulateGroupReceiptLookupsAsync(model);
            return View(model);
        }

        IDbContextTransaction? transaction = null;
        try
        {
            transaction = await BeginTransactionIfSupportedAsync();

            var totalLoadedQuantityMt = availability.TotalLoadedQuantityMt;
            var orderedLegs = legs
                .Where(l => availability.AvailableByLeg.GetValueOrDefault(l.Id) > 0m)
                .OrderBy(l => l.SourcePurchaseContractId)
                .ThenBy(l => l.LoadedDate)
                .ThenBy(l => l.Id)
                .ToList();
            var shortageByLeg = AllocateRoundedByWeight(
                model.TotalShortageQuantityMt,
                orderedLegs.Select(l => availability.AvailableByLeg.GetValueOrDefault(l.Id)).ToList());
            var receivedCapacityByLeg = orderedLegs
                .Select((leg, index) => Math.Max(
                    availability.AvailableByLeg.GetValueOrDefault(leg.Id) - shortageByLeg[index],
                    0m))
                .ToList();
            var receivedByLeg = AllocateRoundedByWeight(
                model.TotalReceivedQuantityMt,
                receivedCapacityByLeg);
            var allowanceByLeg = AllocateRoundedByWeight(
                Math.Min(model.AllowanceMt ?? 0m, model.TotalShortageQuantityMt),
                shortageByLeg);
            var normalizedGroupKey = BuildTransportGroupKey(orderedLegs[0]);
            var receiptIds = new List<int>();

            for (var i = 0; i < orderedLegs.Count; i++)
            {
                var leg = orderedLegs[i];
                var shortageQuantityMt = shortageByLeg[i];
                var receivedQuantityMt = receivedByLeg[i];
                if (receivedQuantityMt <= 0m && shortageQuantityMt <= 0m)
                {
                    continue;
                }
                var allowanceMt = allowanceByLeg[i];
                var chargeableShortageMt = decimal.Round(
                    Math.Max(shortageQuantityMt - allowanceMt, 0m),
                    4,
                    MidpointRounding.AwayFromZero);

                var receipt = new InventoryTransportReceipt
                {
                    InventoryTransportLegId = leg.Id,
                    ReceiptDate = model.ReceiptDate.Date,
                    ReceivedQuantityMt = receivedQuantityMt,
                    ShortageQuantityMt = shortageQuantityMt,
                    AllowanceMt = allowanceMt > 0m ? allowanceMt : null,
                    ChargeableShortageMt = shortageQuantityMt > 0m ? chargeableShortageMt : null,
                    ReceiptDestination = InventoryTransportReceiptDestination.ToInventory,
                    DestinationTerminalId = model.DestinationTerminalId,
                    DestinationStorageTankId = model.DestinationStorageTankId,
                    Notes = BuildGroupReceiptNotes(model, normalizedGroupKey, totalLoadedQuantityMt)
                };

                _db.InventoryTransportReceipts.Add(receipt);
                await _db.SaveChangesAsync();
                receiptIds.Add(receipt.Id);

                if (shortageQuantityMt > 0m)
                {
                    _db.LossEvents.Add(new LossEvent
                    {
                        Stage = LossEventStage.ReceiptShortage,
                        ProductId = leg.ProductId,
                        ContractId = leg.SourcePurchaseContractId,
                        ShipmentId = leg.ShipmentId,
                        TransportLegId = leg.Id,
                        TerminalId = model.DestinationTerminalId,
                        StorageTankId = model.DestinationStorageTankId,
                        EventDate = model.ReceiptDate.Date,
                        ExpectedQuantityMt = leg.QuantityMt,
                        ActualQuantityMt = receivedQuantityMt,
                        DifferenceQuantityMt = shortageQuantityMt,
                        ToleranceQuantityMt = allowanceMt,
                        AllowableLossMt = allowanceMt,
                        ChargeableLossMt = chargeableShortageMt,
                        AffectsInventory = false,
                        Reference = $"TRANSPORT-RECEIPT:{receipt.Id}",
                        Notes = "Group inventory transport receipt shortage"
                    });
                }

                if (receivedQuantityMt > 0m)
                {
                    var movement = new InventoryMovement
                    {
                        ProductId = leg.ProductId,
                        ContractId = leg.SourcePurchaseContractId,
                        TerminalId = model.DestinationTerminalId!.Value,
                        StorageTankId = model.DestinationStorageTankId,
                        Direction = MovementDirection.In,
                        MovementDate = model.ReceiptDate.Date,
                        QuantityMt = receivedQuantityMt,
                        ReferenceDocument = $"TRANSPORT-RECEIPT:{receipt.Id}",
                        Notes = "Group inventory transport destination receipt"
                    };

                    _db.InventoryMovements.Add(movement);
                    await _db.SaveChangesAsync();
                    receipt.InventoryMovementId = movement.Id;
                }

                var remainingAfterReceipt = decimal.Round(
                    Math.Max(
                        availability.AvailableByLeg.GetValueOrDefault(leg.Id)
                        - receivedQuantityMt
                        - shortageQuantityMt,
                        0m),
                    4,
                    MidpointRounding.AwayFromZero);
                if (remainingAfterReceipt <= 0.0001m)
                {
                    leg.Status = InventoryTransportLegStatus.Received;
                }
                leg.UpdatedAtUtc = DateTime.UtcNow;
                await _db.SaveChangesAsync();
            }

            if (transaction is not null)
            {
                await transaction.CommitAsync();
            }

            TempData["ok"] = $"{receiptIds.Count:N0} رسید مقصد برای این حمل ثبت شد و ضایعات بین قراردادها تقسیم شد.";
            if (!string.IsNullOrWhiteSpace(model.ReturnUrl) && Url?.IsLocalUrl(model.ReturnUrl) == true)
            {
                return Redirect(model.ReturnUrl);
            }

            return RedirectToAction(nameof(Journey), new { groupKey = model.GroupKey });
        }
        catch (Exception ex)
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync();
            }

            _logger.LogError(ex, "Failed to create grouped transport receipt for {GroupKey}.", model.GroupKey);
            ModelState.AddModelError(string.Empty, "رسید کلی حمل ذخیره نشد. مقدارها و مقصد را دوباره بررسی کنید.");
            if (legs.Count > 0)
            {
                RefreshGroupReceiptCreateModel(model, legs);
                ApplyGroupReceiptAvailability(model, availability);
            }

            await PopulateGroupReceiptLookupsAsync(model);
            return View(model);
        }
        finally
        {
            if (transaction is not null)
            {
                await transaction.DisposeAsync();
            }
        }
    }

    // «ثبت کلی این حمل»: یک فرم گروهی برای رسید/فروش/دیسپچِ همهٔ تخصیص‌های یک حمل.
    // هنگام ذخیره، هر تخصیص جدا با همان منطق InventoryTransportReceiptService اعمال می‌شود (یک تراکنش، بدون partial save).
    [Authorize(Policy = AuthPolicies.ManageData)]
    public async Task<IActionResult> CreateGroupOperation(
        string groupKey,
        InventoryTransportReceiptDestination mode = InventoryTransportReceiptDestination.ToInventory,
        string? returnUrl = null)
    {
        if (string.IsNullOrWhiteSpace(groupKey))
        {
            return NotFound();
        }

        var legs = await LoadReceivableGroupLegsAsync(groupKey);
        if (legs.Count == 0)
        {
            TempData["error"] = "هیچ تخصیصی برای ثبت رسید در این حمل باقی نمانده است.";
            return RedirectToAction(nameof(Journey), new { groupKey = groupKey.Trim() });
        }

        var first = legs[0];
        var model = new InventoryTransportGroupOperationViewModel
        {
            GroupKey = BuildTransportGroupKey(first),
            Mode = mode == InventoryTransportReceiptDestination.Mixed ? InventoryTransportReceiptDestination.ToInventory : mode,
            OperationDate = _businessClock.Today,
            DestinationTerminalId = first.DestinationTerminalId,
            DestinationStorageTankId = first.DestinationStorageTankId,
            DirectDispatchDestinationLocationId = first.DestinationLocationId,
            ReturnUrl = !string.IsNullOrWhiteSpace(returnUrl) && Url?.IsLocalUrl(returnUrl) == true ? returnUrl : null,
            Legs = legs.Select(l => new InventoryTransportGroupOperationLegRow
            {
                LegId = l.Id,
                ReceivedQuantityMt = l.QuantityMt,
                ShortageQuantityMt = 0m,
                AllowanceMt = l.TransportType == LoadingTransportType.Truck ? 0m : null
            }).ToList()
        };
        RefreshGroupOperationDisplay(model, legs);
        await PopulateGroupOperationLookupsAsync(model);
        return View(model);
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateGroupOperation(InventoryTransportGroupOperationViewModel model)
    {
        var legs = string.IsNullOrWhiteSpace(model.GroupKey)
            ? []
            : await LoadReceivableGroupLegsAsync(model.GroupKey);
        var legById = legs.ToDictionary(l => l.Id);

        if (legs.Count == 0)
        {
            ModelState.AddModelError(nameof(model.GroupKey), "حملی برای رسید در این گروه یافت نشد.");
        }

        var receiptService = new InventoryTransportReceiptService(_db, _currencyConversion, _lineage);

        // کرایهٔ قبلاً ثبت‌شده (نوع «کرایه حمل») را سرور-محاسبه می‌خوانیم تا برای آن حمل‌ها
        // کرایه دوباره روی رسید ذخیره/شمارش نشود (جلوگیری از دوباره‌شماری در P&L).
        var registeredFreightByLeg = await GetRegisteredFreightByLegAsync(legById.Keys.ToList());

        var perLeg = new List<(InventoryTransportReceiptCreateViewModel Model, InventoryTransportLeg Leg)>();
        for (var i = 0; i < model.Legs.Count; i++)
        {
            var row = model.Legs[i];
            if (!legById.TryGetValue(row.LegId, out var leg))
            {
                ModelState.AddModelError($"Legs[{i}].LegId", "این تخصیص دیگر قابل رسید نیست.");
                continue;
            }

            var registeredFreight = registeredFreightByLeg.TryGetValue(row.LegId, out var rf) ? rf : 0m;
            var perModel = MapRowToReceiptModel(model, row, registeredFreight);
            await receiptService.ValidateAsync(perModel, leg, ModelState, keyPrefix: $"Legs[{i}].");
            perLeg.Add((perModel, leg));
        }

        // فروش مستقیم: شماره فاکتور هر تخصیص باید یکتا باشد (در همین دسته هم نباید تکراری باشد).
        if (model.Mode == InventoryTransportReceiptDestination.DirectSale)
        {
            var duplicateInvoice = model.Legs
                .Select(r => r.SaleInvoiceNumber?.Trim())
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .GroupBy(v => v, StringComparer.OrdinalIgnoreCase)
                .Any(g => g.Count() > 1);
            if (duplicateInvoice)
            {
                ModelState.AddModelError(string.Empty, "شماره فاکتور هر تخصیص باید یکتا باشد؛ شماره‌های تکراری وجود دارد.");
            }
        }

        CurrencyConversionResult? saleConversion = null;
        if (ModelState.IsValid && model.Mode == InventoryTransportReceiptDestination.DirectSale)
        {
            var sharedSaleModel = new InventoryTransportReceiptCreateViewModel
            {
                ReceiptDestination = InventoryTransportReceiptDestination.DirectSale,
                SaleCurrency = model.SaleCurrency,
                SaleDate = model.OperationDate,
                SaleAppliedFxRateToUsd = model.SaleAppliedFxRateToUsd
            };
            saleConversion = await receiptService.ResolveSaleConversionAsync(sharedSaleModel, ModelState);
        }

        if (!ModelState.IsValid)
        {
            RefreshGroupOperationDisplay(model, legs);
            await PopulateGroupOperationLookupsAsync(model);
            return View(model);
        }

        await using var transaction = _db.Database.IsRelational()
            ? await _db.Database.BeginTransactionAsync()
            : null;
        try
        {
            foreach (var (perModel, leg) in perLeg)
            {
                await receiptService.ApplyAsync(perModel, leg, saleConversion);
            }

            if (transaction is not null)
            {
                await transaction.CommitAsync();
            }
        }
        catch (Exception ex)
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync();
            }

            _logger.LogError(ex, "Group transport operation failed for {GroupKey}.", model.GroupKey);
            ModelState.AddModelError(string.Empty, "ثبت گروهی ذخیره نشد. مقدارها و انتخاب‌ها را دوباره بررسی کنید.");
            RefreshGroupOperationDisplay(model, legs);
            await PopulateGroupOperationLookupsAsync(model);
            return View(model);
        }

        TempData["ok"] = $"{perLeg.Count:N0} تخصیص این حمل به‌صورت گروهی ثبت شد.";
        if (!string.IsNullOrWhiteSpace(model.ReturnUrl) && Url?.IsLocalUrl(model.ReturnUrl) == true)
        {
            return Redirect(model.ReturnUrl);
        }

        return RedirectToAction(nameof(Journey), new { groupKey = model.GroupKey });
    }

    private static InventoryTransportReceiptCreateViewModel MapRowToReceiptModel(
        InventoryTransportGroupOperationViewModel model,
        InventoryTransportGroupOperationLegRow row,
        decimal registeredFreightUsd)
    {
        var perModel = new InventoryTransportReceiptCreateViewModel
        {
            InventoryTransportLegId = row.LegId,
            ReceiptDate = model.OperationDate,
            ReceivedQuantityMt = row.ReceivedQuantityMt,
            ShortageQuantityMt = row.ShortageQuantityMt,
            AllowanceMt = row.AllowanceMt,
            // کرایه/کسری فی‌تن مشترک‌اند؛ سرویس برای هر تخصیص = وزن همان تخصیص × فی‌تن را محاسبه می‌کند.
            // اما اگر کرایه از قبل (مودال مصرف، نوع «کرایه حمل») ثبت شده باشد، فی‌تن را null می‌گذاریم
            // تا کرایه روی رسید ذخیره نشود و در P&L فقط یک‌بار (از همان مصرف) شمرده شود.
            FreightRateUsdPerMt = registeredFreightUsd > 0m ? null : model.FreightRateUsdPerMt,
            ShortageRateUsd = model.ShortageRateUsd,
            // نحوهٔ محاسبهٔ خسارتِ کسری per-row؛ سرویس تضمین می‌کند فقط یکی اجرا شود و
            // ShortageChargeUsd/FreightPayableUsd را می‌سازد تا پیش‌نمایش و سرور یک عدد بدهند.
            DeductShortageFromFreight = row.DeductShortageFromFreight,
            ShortageAsSeparateDebt = row.ShortageAsSeparateDebt,
            ReceiptDestination = model.Mode,
            ServiceProviderId = model.ServiceProviderId,
            OperationalAssetId = model.OperationalAssetId,
            Notes = model.Notes
        };

        switch (model.Mode)
        {
            case InventoryTransportReceiptDestination.ToInventory:
                perModel.DestinationTerminalId = model.DestinationTerminalId;
                perModel.DestinationStorageTankId = model.DestinationStorageTankId;
                break;
            case InventoryTransportReceiptDestination.DirectSale:
                perModel.SaleCustomerId = model.SaleCustomerId;
                perModel.SaleCurrency = model.SaleCurrency;
                perModel.SaleUnitPriceInCurrency = model.SaleUnitPriceInCurrency;
                perModel.SaleAppliedFxRateToUsd = model.SaleAppliedFxRateToUsd;
                perModel.SaleDate = model.OperationDate;
                perModel.SaleInvoiceNumber = row.SaleInvoiceNumber;
                break;
            case InventoryTransportReceiptDestination.DirectDispatch:
                perModel.DirectDispatchTruckId = row.DirectDispatchTruckId;
                perModel.DirectDispatchDriverId = row.DirectDispatchDriverId;
                perModel.DirectDispatchTicketSerialNumber = row.DirectDispatchTicketSerialNumber;
                perModel.DirectDispatchDate = model.OperationDate;
                perModel.DirectDispatchLoadedQuantityMt = row.ReceivedQuantityMt;
                perModel.DirectDispatchDestinationLocationId = model.DirectDispatchDestinationLocationId;
                break;
        }

        return perModel;
    }

    private static void RefreshGroupOperationDisplay(
        InventoryTransportGroupOperationViewModel model,
        IReadOnlyList<InventoryTransportLeg> legs)
    {
        var byId = legs.ToDictionary(l => l.Id);
        foreach (var row in model.Legs)
        {
            if (!byId.TryGetValue(row.LegId, out var leg))
            {
                continue;
            }

            row.ContractNumber = leg.SourcePurchaseContract?.ContractNumber ?? "";
            row.ProductName = leg.Product?.Name ?? "";
            row.TransportLabel = leg.WagonNumber ?? leg.RwbNo ?? leg.BillOfLadingNumber ?? $"#{leg.Id}";
            row.TransportType = leg.TransportType;
            row.QuantityMt = leg.QuantityMt;
        }

        var first = legs.FirstOrDefault();
        if (first is not null)
        {
            model.TransportReference = first.Shipment?.ShipmentCode
                ?? first.WagonNumber ?? first.RwbNo ?? first.BillOfLadingNumber ?? $"#{first.Id}";
            model.TotalLoadedQuantityMt = legs.Sum(l => l.QuantityMt);
        }
    }

    private async Task<List<InventoryTransportLeg>> LoadReceivableGroupLegsAsync(string groupKey)
    {
        var legs = await LoadTransportGroupLegsForReceiptAsync(groupKey);
        var receivable = legs
            .Where(l => l.Status is InventoryTransportLegStatus.Loaded or InventoryTransportLegStatus.InTransit)
            .ToList();
        if (receivable.Count == 0)
        {
            return receivable;
        }

        var ids = receivable.Select(l => l.Id).ToList();
        var withReceipt = await _db.InventoryTransportReceipts
            .AsNoTracking()
            .Where(r => ids.Contains(r.InventoryTransportLegId) && !r.IsCancelled)
            .Select(r => r.InventoryTransportLegId)
            .Distinct()
            .ToListAsync();

        return receivable.Where(l => !withReceipt.Contains(l.Id)).ToList();
    }

    private async Task PopulateGroupOperationLookupsAsync(InventoryTransportGroupOperationViewModel model)
    {
        ViewBag.DestinationTerminals = new SelectList(
            await _db.Terminals.AsNoTracking().OrderBy(t => t.Name).ToListAsync(),
            "Id", "Name", model.DestinationTerminalId);
        var destinationTanks = await StorageTankDisplay.LoadOptionsAsync(
            _db.StorageTanks.AsNoTracking().OrderBy(t => t.DisplayName ?? t.TankCode));
        ViewBag.DestinationStorageTanks = new SelectList(
            destinationTanks, "Id", "Display", model.DestinationStorageTankId);
        // نگاشت مخزن → ترمینال برای فیلتر سمت کلاینت (فقط مخزن‌های همان ترمینال نمایش داده شوند)
        ViewBag.DestinationStorageTankTerminalMap = destinationTanks
            .Select(t => new { id = t.Id, code = t.Display, terminalId = t.TerminalId })
            .ToList();
        ViewBag.Customers = new SelectList(
            await _db.Customers.AsNoTracking().Where(c => c.IsActive).OrderBy(c => c.Name).ToListAsync(),
            "Id", "Name", model.SaleCustomerId);
        ViewBag.Trucks = new SelectList(
            await _db.Trucks.AsNoTracking().Where(t => t.IsActive).OrderBy(t => t.PlateNumber).ToListAsync(),
            "Id", "PlateNumber");
        ViewBag.Drivers = new SelectList(
            await _db.Drivers.AsNoTracking().Where(d => d.IsActive).OrderBy(d => d.FullName).ToListAsync(),
            "Id", "FullName");
        ViewBag.Locations = new SelectList(
            await _db.Locations.AsNoTracking().Where(l => l.IsActive).OrderBy(l => l.Name).ToListAsync(),
            "Id", "Name", model.DirectDispatchDestinationLocationId);
        ViewBag.ServiceProviders = new SelectList(
            await _db.ServiceProviders.AsNoTracking()
                .Where(p => p.IsActive)
                .OrderBy(p => p.Name)
                .Select(p => new { p.Id, Text = string.IsNullOrWhiteSpace(p.Code) ? p.Name : p.Code + " - " + p.Name })
                .ToListAsync(),
            "Id", "Text", model.ServiceProviderId);
        ViewBag.OperationalAssets = new SelectList(
            await _db.OperationalAssets.AsNoTracking()
                .Where(a => a.IsActive)
                .OrderBy(a => a.AssetCode).ThenBy(a => a.Name)
                .Select(a => new { a.Id, Text = a.AssetCode + " - " + a.Name })
                .ToListAsync(),
            "Id", "Text", model.OperationalAssetId);
        var currencies = await _db.Currencies.AsNoTracking().Where(c => c.IsActive).OrderBy(c => c.Code).ToListAsync();
        ViewBag.Currencies = currencies.Count == 0
            ? new SelectList(new[] { "USD" }, model.SaleCurrency)
            : new SelectList(currencies, "Code", "Code", model.SaleCurrency);

        await PopulateRegisteredFreightAsync(model);
    }

    // کرایهٔ قبلاً ثبت‌شدهٔ هر حمل (مصرف نوع «کرایه حمل») را برای نمایش «کرایه نهایی» در ردیف‌ها بارگذاری می‌کند.
    private async Task PopulateRegisteredFreightAsync(InventoryTransportGroupOperationViewModel model)
    {
        var legIds = model.Legs.Select(r => r.LegId).Where(id => id > 0).Distinct().ToList();
        var freightByLeg = await GetRegisteredFreightByLegAsync(legIds);
        foreach (var row in model.Legs)
        {
            row.RegisteredFreightUsd = freightByLeg.TryGetValue(row.LegId, out var amount) ? amount : 0m;
        }
    }

    // مجموع کرایهٔ ثبت‌شدهٔ هر حمل از مصارف نوع «کرایه حمل» (فقط مصارف فعال/غیرلغوشده).
    private async Task<Dictionary<int, decimal>> GetRegisteredFreightByLegAsync(IReadOnlyCollection<int> legIds)
    {
        if (legIds.Count == 0)
        {
            return new Dictionary<int, decimal>();
        }

        return await _db.ExpenseTransactions
            .AsNoTracking()
            .Where(e => e.TransportLegId.HasValue
                && legIds.Contains(e.TransportLegId.Value)
                && !e.IsCancelled
                && e.ExpenseType != null
                && e.ExpenseType.Code == InventoryTransportReceiptService.TransportFreightExpenseCode)
            .GroupBy(e => e.TransportLegId!.Value)
            .Select(g => new { LegId = g.Key, AmountUsd = g.Sum(e => e.AmountUsd) })
            .ToDictionaryAsync(x => x.LegId, x => x.AmountUsd);
    }

    // ── انتقال گروهی از حمل‌های در جریان (واگن → موتر) ──
    // فرم مرحله‌ای: انتخاب کشتی → انتخاب واگن‌های در جریان → افزودن موترها → تقسیم مقدار → پیش‌نمایش/ثبت.
    // هر تخصیص روی همان InventoryTransportReceiptService با ReceiptDestination=DirectDispatch سوار می‌شود؛
    // بنابراین هیچ موجودی/حرکت جدید و هیچ فروشی ساخته نمی‌شود، فقط باقیماندهٔ واگن به موتر منتقل می‌گردد.
    [Authorize(Policy = AuthPolicies.ManageData)]
    public async Task<IActionResult> GroupTransfer(int? shipmentId = null, string? returnUrl = null)
    {
        var model = new InventoryTransportGroupTransferViewModel
        {
            TransferDate = _businessClock.Today,
            ReturnUrl = !string.IsNullOrWhiteSpace(returnUrl) && Url?.IsLocalUrl(returnUrl) == true ? returnUrl : null,
            ActiveStep = shipmentId.HasValue && shipmentId.Value > 0 ? 2 : 1
        };

        if (shipmentId.HasValue && shipmentId.Value > 0)
        {
            model.ShipmentId = shipmentId.Value;
            var wagons = await LoadInTransitWagonRowsForShipmentAsync(shipmentId.Value);
            model.RegisteredTransfers = await LoadRegisteredTransfersForShipmentAsync(shipmentId.Value);
            if (wagons.Count == 0)
            {
                // اگر انتقال ثبت‌شده‌ای دارد، صفحه بدون خطا می‌ماند تا کاربر بتواند لغو کند.
                if (model.RegisteredTransfers.Count == 0)
                {
                    TempData["error"] = "برای این محموله هیچ واگن در جریانی با باقیماندهٔ قابل‌انتقال وجود ندارد.";
                }
                model.ActiveStep = 1;
            }
            else
            {
                model.Wagons = wagons;
                foreach (var w in wagons) { w.Selected = true; }
                model.SelectedRemainingQuantityMt = wagons.Sum(w => w.RemainingQuantityMt);
            }

            await PopulateGroupTransferShipmentInfoAsync(model);
        }

        await PopulateGroupTransferLookupsAsync(model);
        return View(model);
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> GroupTransfer(InventoryTransportGroupTransferViewModel model)
    {
        model.ActiveStep = 5;

        // باقیماندهٔ واقعیِ هر واگن دوباره از دیتابیس خوانده می‌شود (منبع حقیقت؛ ورودیِ کاربر مبنا نیست).
        var wagonRows = model.ShipmentId > 0
            ? await LoadInTransitWagonRowsForShipmentAsync(model.ShipmentId)
            : [];
        var wagonById = wagonRows.ToDictionary(w => w.LegId);

        if (wagonRows.Count == 0)
        {
            ModelState.AddModelError(nameof(model.ShipmentId), "محموله معتبر با واگن در جریان یافت نشد.");
        }

        // فقط واگن‌های انتخاب‌شده با باقیماندهٔ مثبت مبنا هستند.
        var selectedIds = model.Wagons
            .Where(w => w.Selected && w.LegId > 0 && wagonById.ContainsKey(w.LegId))
            .Select(w => w.LegId)
            .Distinct()
            .ToList();
        var selectedWagons = selectedIds
            .Select(id => wagonById[id])
            .Where(w => w.RemainingQuantityMt > 0.0001m)
            .OrderBy(w => w.LegId)
            .ToList();

        if (selectedWagons.Count == 0)
        {
            ModelState.AddModelError(string.Empty, "حداقل یک واگن با باقیماندهٔ قابل‌انتقال انتخاب کنید.");
        }

        var totalRemaining = selectedWagons.Sum(w => w.RemainingQuantityMt);

        // موترهای مقصد: ردیف‌های دارای نمبر پلیت متنی و مقدار مثبت (موتر/راننده اگر نبود ساخته می‌شود).
        var truckRows = model.Trucks
            .Select((t, index) => (Row: t, Index: index))
            .Where(x => !string.IsNullOrWhiteSpace(x.Row.TruckPlateNumber))
            .ToList();

        if (truckRows.Count == 0)
        {
            ModelState.AddModelError(string.Empty, "حداقل یک موتر مقصد اضافه کنید (نمبر پلیت را تایپ کنید).");
        }

        decimal totalAllocated = 0m;
        foreach (var (row, index) in truckRows)
        {
            if (row.QuantityMt <= 0m)
            {
                ModelState.AddModelError($"Trucks[{index}].QuantityMt", "مقدار انتقال هر موتر باید بزرگ‌تر از صفر باشد.");
            }
            totalAllocated += row.QuantityMt > 0m ? row.QuantityMt : 0m;
        }

        // مجموع تخصیص موترها نباید از باقیماندهٔ انتخاب‌شده بیشتر شود.
        if (selectedWagons.Count > 0 && truckRows.Count > 0 && totalAllocated > totalRemaining + 0.0001m)
        {
            ModelState.AddModelError(string.Empty,
                $"مجموع مقدار موترها ({totalAllocated:N4} MT) از باقیماندهٔ واگن‌های انتخاب‌شده ({totalRemaining:N4} MT) بیشتر است.");
        }

        if (!ModelState.IsValid)
        {
            await RebuildGroupTransferForRedisplayAsync(model, wagonRows, selectedIds);
            return View(model);
        }

        var receiptService = new InventoryTransportReceiptService(_db, _currencyConversion, _lineage);
        var appliedCount = 0;

        await using var transaction = _db.Database.IsRelational()
            ? await _db.Database.BeginTransactionAsync()
            : null;
        try
        {
            // موتر/راننده را از نمبر پلیت و نام متنی resolve یا می‌سازیم (داخل همان تراکنش تا atomic بماند).
            await ResolveTransferVehiclesAsync(truckRows);
            if (!ModelState.IsValid)
            {
                if (transaction is not null) { await transaction.RollbackAsync(); }
                await RebuildGroupTransferForRedisplayAsync(model, wagonRows, selectedIds);
                return View(model);
            }

            // وزن هر موتر دقیقاً ورودی کاربر؛ فقط اگر مجموع باقیماندهٔ واگن‌ها کم بیاید ثبت متوقف می‌شود.
            var chunks = BuildTransferChunks(selectedWagons, truckRows, out var unplacedTrucks);
            if (unplacedTrucks.Count > 0)
            {
                ModelState.AddModelError(string.Empty,
                    "باقیماندهٔ واگن‌های انتخاب‌شده برای این موترها کافی نیست: "
                    + string.Join("، ", unplacedTrucks)
                    + " — مقدار موترها را کم کنید یا واگن بیشتری انتخاب کنید.");
                if (transaction is not null) { await transaction.RollbackAsync(); }
                await RebuildGroupTransferForRedisplayAsync(model, wagonRows, selectedIds);
                return View(model);
            }

            var legIds = chunks.Select(c => c.WagonLegId).Distinct().ToList();
            var legs = await _db.InventoryTransportLegs
                .Include(l => l.SourcePurchaseContract)
                .Include(l => l.Product)
                .Where(l => legIds.Contains(l.Id))
                .ToDictionaryAsync(l => l.Id);

            // «هر موتر = یک دیسپچ با وزن کامل». قطعه‌های موتر بر اساس ردیفش گروه می‌شوند:
            // قطعهٔ اول رسیدِ اصلی است و دیسپچ واحد را با وزن کامل موتر می‌سازد؛
            // قطعه‌های بعدی رسیدهای «همراه» هستند (فقط حساب واگن خودشان، بدون دیسپچ جدا).
            var truckGroups = new List<List<(InventoryTransportReceiptCreateViewModel Model, InventoryTransportLeg Leg)>>();
            var chunkIndex = 0;
            foreach (var group in chunks.GroupBy(c => c.TruckRowIndex))
            {
                var groupChunks = group.ToList();
                var truckTotalMt = groupChunks.Sum(c => c.QuantityMt);
                var groupModels = new List<(InventoryTransportReceiptCreateViewModel Model, InventoryTransportLeg Leg)>();

                for (var j = 0; j < groupChunks.Count; j++)
                {
                    var chunk = groupChunks[j];
                    if (!legs.TryGetValue(chunk.WagonLegId, out var leg))
                    {
                        ModelState.AddModelError(string.Empty, "یکی از واگن‌ها دیگر قابل‌انتقال نیست.");
                        continue;
                    }

                    var isPrimary = j == 0;
                    var perModel = new InventoryTransportReceiptCreateViewModel
                    {
                        InventoryTransportLegId = chunk.WagonLegId,
                        ReceiptDate = model.TransferDate,
                        ReceivedQuantityMt = chunk.QuantityMt,
                        ShortageQuantityMt = 0m,
                        ReceiptDestination = InventoryTransportReceiptDestination.DirectDispatch,
                        DirectDispatchTruckId = chunk.TruckId,
                        DirectDispatchDriverId = chunk.DriverId,
                        DirectDispatchDate = model.TransferDate,
                        // دیسپچ واحد وزن کامل موتر را دارد؛ رسیدهای همراه دیسپچ نمی‌سازند.
                        DirectDispatchLoadedQuantityMt = isPrimary ? truckTotalMt : chunk.QuantityMt,
                        AllowDirectDispatchBeyondReceipt = isPrimary && groupChunks.Count > 1,
                        SkipDirectDispatchRecord = !isPrimary,
                        DirectDispatchTicketSerialNumber = chunk.TicketSerialNumber,
                        Notes = model.Notes
                    };
                    await receiptService.ValidateAsync(perModel, leg, ModelState, keyPrefix: $"Chunks[{chunkIndex}].");
                    chunkIndex++;
                    groupModels.Add((perModel, leg));
                }

                if (groupModels.Count > 0)
                {
                    truckGroups.Add(groupModels);
                }
            }

            if (!ModelState.IsValid)
            {
                if (transaction is not null) { await transaction.RollbackAsync(); }
                await RebuildGroupTransferForRedisplayAsync(model, wagonRows, selectedIds);
                return View(model);
            }

            foreach (var groupModels in truckGroups)
            {
                // رسید اصلی اول ثبت می‌شود تا شناسه‌اش در یادداشت رسیدهای همراه بنشیند (پیوند لغو).
                var primaryReceipt = await receiptService.ApplyAsync(groupModels[0].Model, groupModels[0].Leg, saleConversion: null);
                for (var j = 1; j < groupModels.Count; j++)
                {
                    var (companionModel, companionLeg) = groupModels[j];
                    var marker = $"{GroupTransferCompanionNotePrefix}{primaryReceipt.Id}]";
                    companionModel.Notes = string.IsNullOrWhiteSpace(model.Notes) ? marker : $"{marker} {model.Notes.Trim()}";
                    await receiptService.ApplyAsync(companionModel, companionLeg, saleConversion: null);
                }
            }

            appliedCount = truckGroups.Count;

            if (transaction is not null)
            {
                await transaction.CommitAsync();
            }
        }
        catch (Exception ex)
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync();
            }

            _logger.LogError(ex, "Group in-transit transfer failed for shipment {ShipmentId}.", model.ShipmentId);
            ModelState.AddModelError(string.Empty, "انتقال گروهی ذخیره نشد. واگن‌ها، موترها و مقدارها را دوباره بررسی کنید.");
            await RebuildGroupTransferForRedisplayAsync(model, wagonRows, selectedIds);
            return View(model);
        }

        TempData["ok"] = $"انتقال از {selectedWagons.Count:N0} واگن به {appliedCount:N0} موتر ثبت شد (هر موتر یک دیسپچ).";
        if (!string.IsNullOrWhiteSpace(model.ReturnUrl) && Url?.IsLocalUrl(model.ReturnUrl) == true)
        {
            return Redirect(model.ReturnUrl);
        }

        return RedirectToAction(nameof(GroupTransfer), new { shipmentId = model.ShipmentId, returnUrl = model.ReturnUrl });
    }

    // باقیماندهٔ هر واگنِ در جریانِ یک محموله را می‌سازد (فقط واگن‌های Loaded/InTransit با باقیماندهٔ مثبت).
    private async Task<List<InventoryTransportGroupTransferWagonRow>> LoadInTransitWagonRowsForShipmentAsync(int shipmentId)
    {
        var legs = await _db.InventoryTransportLegs
            .AsNoTracking()
            .Include(l => l.SourcePurchaseContract)
            .Include(l => l.Product)
            .Where(l => l.ShipmentId == shipmentId
                && l.TransportType == LoadingTransportType.Wagon
                && (l.Status == InventoryTransportLegStatus.Loaded || l.Status == InventoryTransportLegStatus.InTransit))
            .OrderBy(l => l.LoadedDate)
            .ThenBy(l => l.Id)
            .ToListAsync();

        if (legs.Count == 0)
        {
            return [];
        }

        var ids = legs.Select(l => l.Id).ToList();
        var consumedByLeg = await _db.InventoryTransportReceipts
            .AsNoTracking()
            .Where(r => ids.Contains(r.InventoryTransportLegId) && !r.IsCancelled)
            .GroupBy(r => r.InventoryTransportLegId)
            .Select(g => new { LegId = g.Key, Consumed = g.Sum(r => r.ReceivedQuantityMt + r.ShortageQuantityMt) })
            .ToDictionaryAsync(x => x.LegId, x => x.Consumed);

        var rows = new List<InventoryTransportGroupTransferWagonRow>();
        foreach (var leg in legs)
        {
            var consumed = consumedByLeg.TryGetValue(leg.Id, out var c) ? c : 0m;
            var remaining = decimal.Round(leg.QuantityMt - consumed, 4, MidpointRounding.AwayFromZero);
            if (remaining <= 0.0001m)
            {
                continue;
            }

            rows.Add(new InventoryTransportGroupTransferWagonRow
            {
                LegId = leg.Id,
                ContractNumber = leg.SourcePurchaseContract?.ContractNumber ?? "",
                ProductName = leg.Product?.Name ?? "",
                WagonLabel = leg.WagonNumber ?? leg.RwbNo ?? leg.BillOfLadingNumber ?? $"#{leg.Id}",
                RwbNo = leg.RwbNo,
                InitialQuantityMt = leg.QuantityMt,
                TransferredQuantityMt = consumed,
                RemainingQuantityMt = remaining
            });
        }

        return rows;
    }

    // انتقال‌های ثبت‌شدهٔ یک کشتی: رسیدهای فعالِ DirectDispatch واگن‌ها + دیسپچ موتر لینک‌شده.
    private async Task<List<InventoryTransportGroupTransferHistoryRow>> LoadRegisteredTransfersForShipmentAsync(int shipmentId)
    {
        // رسیدهای «همراه» موتر چندواگنه در فهرست نمی‌آیند؛ ردیفشان زیر رسید اصلی (دیسپچ واحد) است.
        var raw = await _db.InventoryTransportReceipts
            .AsNoTracking()
            .Where(r => !r.IsCancelled
                && r.ReceiptDestination == InventoryTransportReceiptDestination.DirectDispatch
                && (r.Notes == null || !r.Notes.StartsWith(GroupTransferCompanionNotePrefix))
                && r.InventoryTransportLeg != null
                && r.InventoryTransportLeg.ShipmentId == shipmentId
                && r.InventoryTransportLeg.TransportType == LoadingTransportType.Wagon)
            .OrderByDescending(r => r.ReceiptDate)
            .ThenByDescending(r => r.Id)
            .Select(r => new
            {
                r.Id,
                r.ReceiptDate,
                r.ReceivedQuantityMt,
                LegId = r.InventoryTransportLegId,
                r.InventoryTransportLeg!.WagonNumber,
                r.InventoryTransportLeg.RwbNo,
                r.InventoryTransportLeg.BillOfLadingNumber,
                Dispatch = r.DirectTruckDispatches
                    .Where(d => d.Status != DispatchStatus.Cancelled)
                    .Select(d => new
                    {
                        Plate = d.Truck != null ? d.Truck.PlateNumber : null,
                        Driver = d.Driver != null ? d.Driver.FullName : null,
                        d.TicketSerialNumber,
                        d.SalesTransactionId,
                        d.LoadedQuantityMt
                    })
                    .FirstOrDefault()
            })
            .ToListAsync();

        return raw.Select(x => new InventoryTransportGroupTransferHistoryRow
        {
            ReceiptId = x.Id,
            ReceiptDate = x.ReceiptDate,
            WagonLabel = x.WagonNumber ?? x.RwbNo ?? x.BillOfLadingNumber ?? $"#{x.LegId}",
            TruckPlateNumber = x.Dispatch?.Plate ?? "—",
            DriverName = x.Dispatch?.Driver,
            TicketSerialNumber = x.Dispatch?.TicketSerialNumber,
            // وزن کامل موتر از دیسپچ واحد (رسید اصلی فقط سهم واگن اول را دارد).
            QuantityMt = x.Dispatch?.LoadedQuantityMt ?? x.ReceivedQuantityMt,
            HasLinkedSale = x.Dispatch?.SalesTransactionId is not null
        }).ToList();
    }

    // لغو یک انتقال گروهی ثبت‌شده: رسید و دیسپچ لینک‌شده لغو می‌شوند و باقیماندهٔ واگن آزاد می‌شود.
    // مصرف کرایه/کسری لینک‌شده به همین رسید (در صورت وجود) هم برگردانده می‌شود تا P&L دست‌نخورده بماند.
    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CancelGroupTransfer(int id, int shipmentId, string? returnUrl = null)
    {
        var receipt = await _db.InventoryTransportReceipts
            .Include(r => r.InventoryTransportLeg)
            .FirstOrDefaultAsync(r => r.Id == id);

        if (receipt is null || receipt.InventoryTransportLeg is null)
        {
            return NotFound();
        }

        if (receipt.ReceiptDestination != InventoryTransportReceiptDestination.DirectDispatch)
        {
            TempData["error"] = "فقط انتقال‌های «دیسپچ مستقیم» از این صفحه قابل لغو هستند.";
            return RedirectToAction(nameof(GroupTransfer), new { shipmentId, returnUrl });
        }

        if (receipt.IsCancelled)
        {
            TempData["ok"] = "این انتقال قبلاً لغو شده است.";
            return RedirectToAction(nameof(GroupTransfer), new { shipmentId, returnUrl });
        }

        // رسید «همراه» مستقیم لغو نمی‌شود؛ لغو از ردیف اصلی موتر همهٔ همراه‌ها را هم برمی‌گرداند.
        if (receipt.Notes is not null && receipt.Notes.StartsWith(GroupTransferCompanionNotePrefix))
        {
            TempData["error"] = "این رسید بخشی از یک موتر چندواگنه است؛ از ردیف اصلی همان موتر لغو کنید.";
            return RedirectToAction(nameof(GroupTransfer), new { shipmentId, returnUrl });
        }

        var dispatches = await _db.TruckDispatches
            .Where(d => d.InventoryTransportReceiptId == receipt.Id && d.Status != DispatchStatus.Cancelled)
            .ToListAsync();

        if (dispatches.Any(d => d.SalesTransactionId.HasValue))
        {
            TempData["error"] = "برای موتر این انتقال فروش ثبت شده است؛ ابتدا فروش لینک‌شده را لغو کنید.";
            return RedirectToAction(nameof(GroupTransfer), new { shipmentId, returnUrl });
        }

        await using var transaction = _db.Database.IsRelational()
            ? await _db.Database.BeginTransactionAsync()
            : null;
        try
        {
            receipt.IsCancelled = true;
            foreach (var dispatch in dispatches)
            {
                dispatch.Status = DispatchStatus.Cancelled;
            }

            // مصرف کرایهٔ ثبت‌شده برای همین رسید (اگر باشد) لغو و لجر آن حذف می‌شود.
            var receiptReference = $"TRANSPORT-RECEIPT:{receipt.Id}";
            var freightLedgers = await _db.LedgerEntries
                .Where(l => l.SourceType == "Expense" && l.Reference == receiptReference)
                .ToListAsync();
            if (freightLedgers.Count > 0)
            {
                var expenseIds = freightLedgers.Select(l => l.SourceId).ToList();
                var expenses = await _db.ExpenseTransactions
                    .Where(e => expenseIds.Contains(e.Id) && !e.IsCancelled)
                    .ToListAsync();
                foreach (var expense in expenses)
                {
                    expense.IsCancelled = true;
                }
                _db.LedgerEntries.RemoveRange(freightLedgers);
            }

            // رویداد کسریِ همین رسید (در صورت وجود) لغو می‌شود.
            var shortageLosses = await _db.LossEvents
                .Where(le => le.Reference == receiptReference && !le.IsCancelled)
                .ToListAsync();
            foreach (var loss in shortageLosses)
            {
                loss.IsCancelled = true;
            }

            // رسیدهای «همراه» موتر چندواگنه (پیوندشده با یادداشت) هم لغو و واگن‌هایشان آزاد می‌شوند.
            var companionMarker = $"{GroupTransferCompanionNotePrefix}{receipt.Id}]";
            var companions = await _db.InventoryTransportReceipts
                .Include(r => r.InventoryTransportLeg)
                .Where(r => !r.IsCancelled && r.Notes != null && r.Notes.StartsWith(companionMarker))
                .ToListAsync();
            foreach (var companion in companions)
            {
                companion.IsCancelled = true;
                if (companion.InventoryTransportLeg?.Status == InventoryTransportLegStatus.Received)
                {
                    companion.InventoryTransportLeg.Status = InventoryTransportLegStatus.InTransit;
                }
            }

            // با آزادشدن باقیمانده، واگنِ «تکمیل‌شده» دوباره «در راه» می‌شود (الگوی لغو فروش گروهی).
            var leg = receipt.InventoryTransportLeg;
            if (leg.Status == InventoryTransportLegStatus.Received)
            {
                leg.Status = InventoryTransportLegStatus.InTransit;
            }

            await _db.SaveChangesAsync();

            await _audit.LogAndSaveAsync(
                nameof(InventoryTransportReceipt),
                receipt.Id,
                AuditAction.Update,
                diff: AuditDiffFormatter.ForUpdate(("IsCancelled", false, true)));

            if (transaction is not null)
            {
                await transaction.CommitAsync();
            }

            TempData["ok"] = "انتقال لغو شد و باقیماندهٔ واگن برگشت.";
        }
        catch (Exception ex)
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync();
            }

            _logger.LogError(ex, "Cancelling group transfer receipt {ReceiptId} failed.", receipt.Id);
            TempData["error"] = "لغو انتقال انجام نشد؛ دوباره تلاش کنید.";
        }

        return RedirectToAction(nameof(GroupTransfer), new { shipmentId, returnUrl });
    }

    // نگاشت باقیماندهٔ واگن‌های انتخاب‌شده به موترها. وزن هر موتر دقیقاً همان ورودی کاربر می‌ماند:
    // اول واگنی که کل وزن موتر در آن جا شود (یک قطعه)؛ اگر هیچ واگنی به‌تنهایی جا نداشت، وزن همان
    // موتر از چند واگن (FIFO) پر می‌شود — مثل واقعیت که یک موتر از باقیِ دو واگن بار می‌گیرد.
    // چند قطعه فقط رکورد داخلیِ حساب هر واگن است؛ در UI هر موتر یک ردیف نمایش داده می‌شود.
    private static List<InventoryTransportTransferChunk> BuildTransferChunks(
        IReadOnlyList<InventoryTransportGroupTransferWagonRow> selectedWagons,
        IReadOnlyList<(InventoryTransportGroupTransferTruckRow Row, int Index)> truckRows,
        out List<string> unplacedTrucks)
    {
        var chunks = new List<InventoryTransportTransferChunk>();
        var left = selectedWagons.Select(w => w.RemainingQuantityMt).ToArray();
        unplacedTrucks = [];

        void AddChunk(int wagonIdx, InventoryTransportGroupTransferTruckRow truck, int truckIndex, decimal quantityMt)
        {
            var wagon = selectedWagons[wagonIdx];
            chunks.Add(new InventoryTransportTransferChunk
            {
                WagonLegId = wagon.LegId,
                WagonLabel = wagon.WagonLabel,
                TruckRowIndex = truckIndex,
                TruckId = truck.ResolvedTruckId,
                TruckLabel = truck.TruckPlateNumber?.Trim() ?? "",
                DriverId = truck.ResolvedDriverId,
                TicketSerialNumber = string.IsNullOrWhiteSpace(truck.TicketSerialNumber) ? null : truck.TicketSerialNumber.Trim(),
                QuantityMt = quantityMt
            });
        }

        foreach (var (truck, index) in truckRows)
        {
            var need = decimal.Round(truck.QuantityMt, 4, MidpointRounding.AwayFromZero);
            if (need <= 0.0001m)
            {
                continue;
            }

            // اول: واگنی که کل وزن این موتر یک‌جا در آن جا می‌شود.
            var wholeIdx = -1;
            for (var i = 0; i < selectedWagons.Count; i++)
            {
                if (left[i] >= need - 0.0001m)
                {
                    wholeIdx = i;
                    break;
                }
            }

            if (wholeIdx >= 0)
            {
                AddChunk(wholeIdx, truck, index, need);
                left[wholeIdx] -= need;
                continue;
            }

            // هیچ واگنی به‌تنهایی جا ندارد → وزن همین موتر از چند واگن (به‌ترتیب صف) پر می‌شود.
            for (var i = 0; i < selectedWagons.Count && need > 0.0001m; i++)
            {
                if (left[i] <= 0.0001m)
                {
                    continue;
                }

                var take = decimal.Round(Math.Min(need, left[i]), 4, MidpointRounding.AwayFromZero);
                if (take <= 0.0001m)
                {
                    continue;
                }

                AddChunk(i, truck, index, take);
                left[i] -= take;
                need -= take;
            }

            // مجموع باقیماندهٔ همهٔ واگن‌ها هم کافی نبود (validation جدا هم این را می‌گیرد).
            if (need > 0.0001m)
            {
                unplacedTrucks.Add($"{truck.TruckPlateNumber?.Trim()} ({need:N4} MT کمبود)");
            }
        }

        return chunks;
    }

    // موتر و راننده را از متنِ تایپ‌شده resolve می‌کند؛ اگر با آن نمبر/نام نبود، ساخته می‌شود
    // (همان الگوی حمل گروهی از موجودی). داخل تراکنش صدا زده می‌شود تا با رسیدها atomic بماند.
    private async Task ResolveTransferVehiclesAsync(
        IReadOnlyList<(InventoryTransportGroupTransferTruckRow Row, int Index)> truckRows)
    {
        var truckCache = new Dictionary<string, Truck>(StringComparer.OrdinalIgnoreCase);
        var driverCache = new Dictionary<string, Driver>(StringComparer.OrdinalIgnoreCase);
        var pendingTrucks = new List<(InventoryTransportGroupTransferTruckRow Row, Truck Truck)>();
        var pendingDrivers = new List<(InventoryTransportGroupTransferTruckRow Row, Driver Driver)>();

        foreach (var (row, index) in truckRows)
        {
            var plate = row.TruckPlateNumber?.Trim();
            if (string.IsNullOrWhiteSpace(plate))
            {
                ModelState.AddModelError($"Trucks[{index}].TruckPlateNumber", "نمبر پلیت موتر را وارد کنید.");
                continue;
            }

            if (!truckCache.TryGetValue(plate, out var truck))
            {
                var existing = await _db.Trucks.FirstOrDefaultAsync(t => t.PlateNumber == plate);
                if (existing is not null)
                {
                    if (!existing.IsActive)
                    {
                        ModelState.AddModelError($"Trucks[{index}].TruckPlateNumber",
                            $"موتر با نمبر «{plate}» غیرفعال است؛ ابتدا آن را در داده‌های پایه فعال کنید.");
                        continue;
                    }
                    truck = existing;
                }
                else
                {
                    truck = new Truck
                    {
                        PlateNumber = plate,
                        MaxLoadMt = row.CapacityMt is > 0m ? row.CapacityMt : null,
                        IsActive = true
                    };
                    _db.Trucks.Add(truck);
                    pendingTrucks.Add((row, truck));
                }
                truckCache[plate] = truck;
            }

            if (truck.Id > 0)
            {
                row.ResolvedTruckId = truck.Id;
            }

            var driverName = row.DriverName?.Trim();
            if (!string.IsNullOrWhiteSpace(driverName))
            {
                if (!driverCache.TryGetValue(driverName, out var driver))
                {
                    var existing = await _db.Drivers.FirstOrDefaultAsync(d => d.FullName == driverName);
                    if (existing is not null)
                    {
                        if (!existing.IsActive)
                        {
                            ModelState.AddModelError($"Trucks[{index}].DriverName",
                                $"راننده «{driverName}» غیرفعال است؛ ابتدا آن را در داده‌های پایه فعال کنید.");
                            continue;
                        }
                        driver = existing;
                    }
                    else
                    {
                        driver = new Driver { FullName = driverName, IsActive = true };
                        _db.Drivers.Add(driver);
                        pendingDrivers.Add((row, driver));
                    }
                    driverCache[driverName] = driver;
                }

                if (driver.Id > 0)
                {
                    row.ResolvedDriverId = driver.Id;
                }
            }
        }

        if (pendingTrucks.Count > 0 || pendingDrivers.Count > 0)
        {
            await _db.SaveChangesAsync();
        }

        // پس از ذخیره، شناسه‌ها قطعی‌اند؛ همهٔ ردیف‌ها را از کش پر می‌کنیم (شاملِ موترهای مشترکِ نوساخته).
        foreach (var (row, _) in truckRows)
        {
            var plate = row.TruckPlateNumber?.Trim();
            if (!string.IsNullOrWhiteSpace(plate) && truckCache.TryGetValue(plate, out var truck))
            {
                row.ResolvedTruckId = truck.Id;
            }
            var driverName = row.DriverName?.Trim();
            if (!string.IsNullOrWhiteSpace(driverName) && driverCache.TryGetValue(driverName, out var driver))
            {
                row.ResolvedDriverId = driver.Id;
            }
        }
    }

    private async Task RebuildGroupTransferForRedisplayAsync(
        InventoryTransportGroupTransferViewModel model,
        IReadOnlyList<InventoryTransportGroupTransferWagonRow> wagonRows,
        IReadOnlyCollection<int> selectedIds)
    {
        var selectedSet = selectedIds.ToHashSet();
        foreach (var w in wagonRows)
        {
            w.Selected = selectedSet.Contains(w.LegId);
        }
        model.Wagons = wagonRows.ToList();
        model.SelectedRemainingQuantityMt = wagonRows.Where(w => w.Selected).Sum(w => w.RemainingQuantityMt);
        model.RegisteredTransfers = model.ShipmentId > 0
            ? await LoadRegisteredTransfersForShipmentAsync(model.ShipmentId)
            : [];

        // مقادیر متنیِ موتر/راننده مستقیم از خودِ model دوباره رندر می‌شوند؛ نیازی به بازسازی برچسب نیست.
        await PopulateGroupTransferShipmentInfoAsync(model);
        await PopulateGroupTransferLookupsAsync(model);
    }

    private async Task PopulateGroupTransferShipmentInfoAsync(InventoryTransportGroupTransferViewModel model)
    {
        if (model.ShipmentId <= 0)
        {
            return;
        }

        var shipment = await _db.Shipments.AsNoTracking()
            .Include(s => s.Vessel)
            .FirstOrDefaultAsync(s => s.Id == model.ShipmentId);
        model.ShipmentName = shipment?.Vessel?.Name ?? shipment?.ShipmentCode ?? $"#{model.ShipmentId}";
        model.ProductName = model.Wagons
            .Select(w => w.ProductName)
            .FirstOrDefault(p => !string.IsNullOrWhiteSpace(p)) ?? "";
    }

    private async Task PopulateGroupTransferLookupsAsync(InventoryTransportGroupTransferViewModel model)
    {
        // فقط محموله‌هایی که واگن در جریان دارند در انتخابگر مرحلهٔ اول نمایش داده می‌شوند.
        var shipmentIdsWithWagons = await _db.InventoryTransportLegs
            .AsNoTracking()
            .Where(l => l.ShipmentId.HasValue
                && l.TransportType == LoadingTransportType.Wagon
                && (l.Status == InventoryTransportLegStatus.Loaded || l.Status == InventoryTransportLegStatus.InTransit))
            .Select(l => l.ShipmentId!.Value)
            .Distinct()
            .ToListAsync();

        // کشتی‌هایی که انتقال ثبت‌شدهٔ قابل‌لغو دارند هم می‌مانند (حتی اگر همهٔ واگن‌ها تکمیل شده باشند).
        var shipmentIdsWithTransfers = await _db.InventoryTransportReceipts
            .AsNoTracking()
            .Where(r => !r.IsCancelled
                && r.ReceiptDestination == InventoryTransportReceiptDestination.DirectDispatch
                && r.InventoryTransportLeg != null
                && r.InventoryTransportLeg.ShipmentId.HasValue
                && r.InventoryTransportLeg.TransportType == LoadingTransportType.Wagon)
            .Select(r => r.InventoryTransportLeg!.ShipmentId!.Value)
            .Distinct()
            .ToListAsync();
        shipmentIdsWithWagons = shipmentIdsWithWagons.Union(shipmentIdsWithTransfers).ToList();

        var shipments = await _db.Shipments.AsNoTracking()
            .Include(s => s.Vessel)
            .Where(s => shipmentIdsWithWagons.Contains(s.Id))
            .OrderByDescending(s => s.ArrivalDate ?? s.DepartureDate)
            .ThenBy(s => s.ShipmentCode)
            .Select(s => new
            {
                s.Id,
                Text = (s.Vessel != null ? s.Vessel.Name + " — " : "") + s.ShipmentCode
            })
            .ToListAsync();
        ViewBag.TransferShipments = new SelectList(shipments, "Id", "Text", model.ShipmentId);

        ViewBag.Trucks = new SelectList(
            await _db.Trucks.AsNoTracking().Where(t => t.IsActive).OrderBy(t => t.PlateNumber).ToListAsync(),
            "Id", "PlateNumber");
        ViewBag.Drivers = new SelectList(
            await _db.Drivers.AsNoTracking().Where(d => d.IsActive).OrderBy(d => d.FullName).ToListAsync(),
            "Id", "FullName");
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    public async Task<IActionResult> CreateGroupExpense(string groupKey, string? returnUrl = null)
    {
        if (string.IsNullOrWhiteSpace(groupKey))
        {
            return NotFound();
        }

        var legs = await LoadActiveTransportGroupLegsAsync(groupKey);
        if (legs.Count == 0)
        {
            return NotFound();
        }

        var model = BuildGroupExpenseCreateModel(legs, returnUrl);
        await PopulateGroupExpenseLookupsAsync(model);
        return View(model);
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateGroupExpense(InventoryTransportGroupExpenseCreateViewModel model)
    {
        NormalizeGroupExpenseModel(model);

        List<InventoryTransportLeg> legs = string.IsNullOrWhiteSpace(model.GroupKey)
            ? []
            : await LoadActiveTransportGroupLegsAsync(model.GroupKey);
        if (legs.Count == 0)
        {
            ModelState.AddModelError(nameof(model.GroupKey), "Transport group selection is invalid or no longer active.");
        }

        var totalQuantityMt = legs.Sum(l => l.QuantityMt);
        if (totalQuantityMt <= 0m)
        {
            ModelState.AddModelError(nameof(model.GroupKey), "Transport group has no positive allocated quantity.");
        }

        if (model.Amount <= 0m)
        {
            ModelState.AddModelError(nameof(model.Amount), "Expense amount must be greater than zero.");
        }

        var manualExpenseTypeName = model.ManualExpenseTypeName?.Trim() ?? string.Empty;
        ExpenseType? expenseType = null;
        if (model.ExpenseTypeId.HasValue)
        {
            expenseType = await _db.ExpenseTypes
                .AsNoTracking()
                .FirstOrDefaultAsync(e => e.Id == model.ExpenseTypeId.Value && e.IsActive);
            if (expenseType is null)
            {
                ModelState.AddModelError(nameof(model.ExpenseTypeId), "Selected expense type is invalid.");
            }
        }
        else if (!string.IsNullOrWhiteSpace(manualExpenseTypeName))
        {
            expenseType = await FindExpenseTypeByManualNameAsync(manualExpenseTypeName);
        }
        else
        {
            ModelState.AddModelError(nameof(model.ManualExpenseTypeName), "Select an expense type or enter a manual expense type.");
        }

        PTGOilSystem.Web.Models.Entities.ServiceProvider? serviceProvider = null;
        if (model.ServiceProviderId.HasValue)
        {
            serviceProvider = await _db.ServiceProviders
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == model.ServiceProviderId.Value && p.IsActive);
            if (serviceProvider is null)
            {
                ModelState.AddModelError(nameof(model.ServiceProviderId), "Service provider selection is invalid.");
            }
        }

        OperationalAsset? operationalAsset = null;
        if (model.OperationalAssetId.HasValue)
        {
            operationalAsset = await _db.OperationalAssets
                .AsNoTracking()
                .FirstOrDefaultAsync(a => a.Id == model.OperationalAssetId.Value && a.IsActive);
            if (operationalAsset is null)
            {
                ModelState.AddModelError(nameof(model.OperationalAssetId), "Operational asset selection is invalid.");
            }
        }

        if (model.ServiceProviderId.HasValue && model.OperationalAssetId.HasValue)
        {
            ModelState.AddModelError(nameof(model.OperationalAssetId), "Select either a service provider or an operational asset, not both.");
        }

        if (string.IsNullOrWhiteSpace(model.Description))
        {
            ModelState.AddModelError(nameof(model.Description), "Expense description or reference is required.");
        }

        var hasActiveCurrencies = await _db.Currencies.AsNoTracking().AnyAsync(c => c.IsActive);
        if (hasActiveCurrencies
            && !await _db.Currencies.AsNoTracking().AnyAsync(c => c.Code == model.Currency && c.IsActive))
        {
            ModelState.AddModelError(nameof(model.Currency), "Invalid currency selection.");
        }

        CurrencyConversionResult? conversion = null;
        if (ModelState.IsValid)
        {
            try
            {
                conversion = await _currencyConversion.ResolveToBaseAsync(
                    model.Currency,
                    model.ExpenseDate.Date,
                    model.AppliedFxRateToUsd);
            }
            catch (BusinessRuleException ex)
            {
                ModelState.AddModelError(nameof(model.AppliedFxRateToUsd), ex.Message);
            }
        }

        if (!ModelState.IsValid || conversion is null)
        {
            model.ManualExpenseTypeName = manualExpenseTypeName;
            if (legs.Count > 0)
            {
                RefreshGroupExpenseCreateModel(model, legs);
            }
            await PopulateGroupExpenseLookupsAsync(model);
            return View(model);
        }

        IDbContextTransaction? transaction = null;
        try
        {
            transaction = await BeginTransactionIfSupportedAsync();

            if (expenseType is null)
            {
                expenseType = await FindExpenseTypeByManualNameAsync(manualExpenseTypeName);
            }

            if (expenseType is null)
            {
                expenseType = new ExpenseType
                {
                    Code = await BuildManualExpenseTypeCodeAsync(manualExpenseTypeName),
                    Name = manualExpenseTypeName,
                    NamePersian = manualExpenseTypeName,
                    Category = "Transport",
                    IsActive = true
                };
                _db.ExpenseTypes.Add(expenseType);
                await _db.SaveChangesAsync();
            }

            var normalizedGroupKey = BuildTransportGroupKey(legs[0]);
            var orderedLegs = legs
                .OrderBy(l => l.SourcePurchaseContractId)
                .ThenBy(l => l.LoadedDate)
                .ThenBy(l => l.Id)
                .ToList();
            var amountUsd = conversion.ConvertToBase(model.Amount);
            var sourceAmounts = AllocateRoundedByWeight(model.Amount, orderedLegs.Select(l => l.QuantityMt).ToList());
            var usdAmounts = AllocateRoundedByWeight(amountUsd, orderedLegs.Select(l => l.QuantityMt).ToList());
            var expenses = new List<ExpenseTransaction>();

            for (var i = 0; i < orderedLegs.Count; i++)
            {
                var leg = orderedLegs[i];
                var sharePercent = CalculateSharePercent(leg.QuantityMt, totalQuantityMt);
                var expense = new ExpenseTransaction
                {
                    ExpenseTypeId = expenseType.Id,
                    ContractId = leg.SourcePurchaseContractId,
                    ShipmentId = leg.ShipmentId,
                    TransportLegId = leg.Id,
                    ServiceProviderId = serviceProvider?.Id,
                    OperationalAssetId = operationalAsset?.Id,
                    ExpenseDate = model.ExpenseDate.Date,
                    Amount = sourceAmounts[i],
                    Currency = conversion.SourceCurrencyCode,
                    AppliedFxRateToUsd = conversion.AppliedRateToBase,
                    AmountUsd = usdAmounts[i],
                    Description = BuildGroupExpenseDescription(model, leg, normalizedGroupKey, conversion, amountUsd, sharePercent)
                };

                _db.ExpenseTransactions.Add(expense);
                await _db.SaveChangesAsync();

                // مرحله ۵ — Dual-write داخل همان Transaction قدیمی.
                if (_expenseAccounting is not null)
                {
                    await _expenseAccounting.TryPostExpenseAsync(expense);
                }

                var ledgerEntry = new LedgerEntry
                {
                    EntryDate = expense.ExpenseDate,
                    Side = serviceProvider is not null ? LedgerSide.Credit : LedgerSide.Debit,
                    AmountUsd = expense.AmountUsd,
                    Currency = SystemCurrency.BaseCurrencyCode,
                    SourceAmount = expense.Amount,
                    SourceCurrencyCode = expense.Currency,
                    AppliedFxRateToUsd = expense.AppliedFxRateToUsd,
                    AppliedFxRateDate = conversion.EffectiveDate.Date,
                    AppliedFxRateSource = conversion.SourceDescription,
                    Description = BuildLedgerDescription(expenseType, expense),
                    SourceType = "Expense",
                    SourceId = expense.Id,
                    Reference = BuildLedgerReference(expenseType, expense),
                    ContractId = expense.ContractId,
                    ShipmentId = expense.ShipmentId,
                    ServiceProviderId = expense.ServiceProviderId
                };

                _db.LedgerEntries.Add(ledgerEntry);
                await _db.SaveChangesAsync();

                await _audit.LogAndSaveAsync(
                    nameof(ExpenseTransaction),
                    expense.Id,
                    AuditAction.Insert,
                    diff: AuditDiffFormatter.ForCreate(
                        ("ExpenseTypeId", expense.ExpenseTypeId),
                        ("ContractId", expense.ContractId),
                        ("ShipmentId", expense.ShipmentId),
                        ("TransportLegId", expense.TransportLegId),
                        ("ServiceProviderId", expense.ServiceProviderId),
                        ("OperationalAssetId", expense.OperationalAssetId),
                        ("ExpenseDate", expense.ExpenseDate),
                        ("Amount", expense.Amount),
                        ("Currency", expense.Currency),
                        ("AppliedFxRateToUsd", expense.AppliedFxRateToUsd),
                        ("AmountUsd", expense.AmountUsd),
                        ("SharePercent", sharePercent),
                        ("GroupKey", normalizedGroupKey),
                        ("LedgerReference", ledgerEntry.Reference)));

                expenses.Add(expense);
            }

            if (transaction is not null)
            {
                await transaction.CommitAsync();
            }

            TempData["ok"] = $"{expenses.Count:N0} expense allocation(s) were created for this transport group.";
            if (TryGetLocalReturnUrl(model.ReturnUrl, out var localReturnUrl))
            {
                return Redirect(localReturnUrl);
            }

            return RedirectToAction(nameof(Journey), new { groupKey = normalizedGroupKey });
        }
        catch (Exception ex)
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync();
            }

            _logger.LogError(ex, "Failed to create grouped transport expense for {GroupKey}.", model.GroupKey);
            ModelState.AddModelError(string.Empty, "Expense allocation could not be saved. Please review the values and try again.");
            RefreshGroupExpenseCreateModel(model, legs);
            await PopulateGroupExpenseLookupsAsync(model);
            return View(model);
        }
        finally
        {
            if (transaction is not null)
            {
                await transaction.DisposeAsync();
            }
        }
    }

    // ── مودال «ثبت مصارف حمل» (هم‌شکلِ مودال مصارف بارگیری) ──────────────────────────
    // GET: فرم چندردیفی را به‌صورت partial برای بارگذاری داخل مودال برمی‌گرداند (AJAX remote).
    [Authorize(Policy = AuthPolicies.ManageData)]
    public async Task<IActionResult> CreateGroupExpenseModal(string? groupKey = null, int? legId = null, string? returnUrl = null)
    {
        List<InventoryTransportLeg> legs;
        var resolvedGroupKey = groupKey?.Trim();
        if (string.IsNullOrWhiteSpace(resolvedGroupKey) && legId.HasValue)
        {
            var leg = await _db.InventoryTransportLegs
                .AsNoTracking()
                .Include(l => l.SourcePurchaseContract)
                .Include(l => l.SourceStorageTank)
                .Include(l => l.Shipment)
                .FirstOrDefaultAsync(l => l.Id == legId.Value);
            if (leg is null)
            {
                return NotFound();
            }

            resolvedGroupKey = BuildTransportGroupKey(leg);
            legs = [leg];
        }
        else
        {
            if (string.IsNullOrWhiteSpace(resolvedGroupKey))
            {
                return NotFound();
            }

            legs = await LoadActiveTransportGroupLegsAsync(resolvedGroupKey);
        }

        if (string.IsNullOrWhiteSpace(resolvedGroupKey))
        {
            return NotFound();
        }

        if (legs.Count == 0)
        {
            return NotFound();
        }

        var model = new InventoryTransportGroupExpenseModalViewModel
        {
            GroupKey = BuildTransportGroupKey(legs[0]),
            TransportLegId = legId,
            TransportReference = ResolveGroupExpenseTransportReference(legs),
            TotalAllocatedQuantityMt = legs.Sum(l => l.QuantityMt),
            ReturnUrl = !string.IsNullOrWhiteSpace(returnUrl) && Url?.IsLocalUrl(returnUrl) == true ? returnUrl : null,
            Lines = [new InventoryTransportGroupExpenseModalRow()]
        };

        await PopulateGroupExpenseModalLookupsAsync();
        model.ExistingExpenses = await LoadGroupExpenseItemsAsync(legs);
        // پایهٔ تقسیم = مجموع مصارف فعالِ همین گروه؛ سهم هر قرارداد از این مبلغ به‌نسبت وزن محاسبه می‌شود.
        model.Allocations = BuildGroupExpenseAllocationPreview(legs, model.ExistingExpenses.Sum(e => e.Amount));
        ViewData["IsExpenseModal"] = true;
        ViewData["CancelUrl"] = model.ReturnUrl;
        return PartialView("_TransportExpenseEditor", model);
    }

    // POST: هر ردیف با همان منطق موجودِ تخصیص وزنی per-leg به ExpenseTransaction + LedgerEntry تبدیل می‌شود.
    // هیچ منطق Ledger جدیدی ساخته نمی‌شود؛ فقط همان الگوی CreateGroupExpense در یک حلقه بازاستفاده می‌شود.
    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveGroupExpenses(InventoryTransportGroupExpenseModalViewModel model)
    {
        NormalizeGroupExpenseModalModel(model);

        var request = HttpContext?.Request;
        var isAjax = request is not null
            && request.Headers.TryGetValue("X-Requested-With", out var xrw)
            && string.Equals(xrw.ToString(), "XMLHttpRequest", StringComparison.OrdinalIgnoreCase);

        var legs = model.TransportLegId.HasValue
            ? await LoadExpenseTransportLegsAsync(model.TransportLegId.Value)
            : string.IsNullOrWhiteSpace(model.GroupKey)
                ? []
                : await LoadActiveTransportGroupLegsAsync(model.GroupKey);
        if (legs.Count == 0)
        {
            ModelState.AddModelError(nameof(model.GroupKey), "حملی برای ثبت مصرف یافت نشد.");
        }

        model.Lines = (model.Lines ?? []).Where(r => RowHasValue(r)).ToList();
        if (model.Lines.Count == 0)
        {
            ModelState.AddModelError(string.Empty, "حداقل یک ردیف مصرف با مبلغ لازم است.");
        }

        var totalQuantityMt = legs.Sum(l => l.QuantityMt);
        var legIds = legs.Select(l => l.Id).ToList();

        // نوع‌های مصرفِ فعالی که قبلاً برای این گروه ثبت شده‌اند (برای جلوگیری از تکرار).
        var existingTypeIds = legs.Count == 0
            ? new HashSet<int>()
            : (await _db.ExpenseTransactions.AsNoTracking()
                .Where(e => e.TransportLegId.HasValue && legIds.Contains(e.TransportLegId.Value) && !e.IsCancelled)
                .Select(e => e.ExpenseTypeId)
                .Distinct()
                .ToListAsync()).ToHashSet();

        // اعتبارسنجی ردیف‌ها + resolve نوع مصرف/طرف‌حساب.
        var prepared = new List<(InventoryTransportGroupExpenseModalRow Row, ExpenseType Type, PTGOilSystem.Web.Models.Entities.ServiceProvider? Provider, OperationalAsset? Asset)>();
        for (var i = 0; i < model.Lines.Count; i++)
        {
            var row = model.Lines[i];
            var prefix = $"Lines[{i}].";
            var manualName = ValidateManualExpenseTypeName(row.ManualExpenseTypeName, prefix + nameof(row.ManualExpenseTypeName));
            row.ManualExpenseTypeName = manualName;

            if (row.AmountUsd <= 0m)
            {
                ModelState.AddModelError(prefix + nameof(row.AmountUsd), "مبلغ مصرف باید بزرگ‌تر از صفر باشد.");
            }

            ExpenseType? type = null;
            if (row.ExpenseTypeId.HasValue)
            {
                type = await _db.ExpenseTypes.AsNoTracking().FirstOrDefaultAsync(e => e.Id == row.ExpenseTypeId.Value && e.IsActive);
                if (type is null)
                {
                    ModelState.AddModelError(prefix + nameof(row.ExpenseTypeId), "نوع مصرف انتخاب‌شده معتبر نیست.");
                }
            }
            else if (!string.IsNullOrWhiteSpace(manualName))
            {
                type = await FindExpenseTypeByManualNameAsync(manualName)
                    ?? new ExpenseType
                    {
                        Name = manualName,
                        NamePersian = manualName,
                        Category = "Transport",
                        IsActive = true
                    };
            }
            else
            {
                ModelState.AddModelError(prefix + nameof(row.ExpenseTypeId), "نوع مصرف را انتخاب یا وارد کنید.");
            }

            PTGOilSystem.Web.Models.Entities.ServiceProvider? provider = null;
            OperationalAsset? asset = null;
            if (row.PartyType == LoadingExpensePartyType.ServiceProvider)
            {
                if (!row.ServiceProviderId.HasValue)
                {
                    ModelState.AddModelError(prefix + nameof(row.ServiceProviderId), "شرکت خدماتی را انتخاب کنید.");
                }
                else
                {
                    provider = await _db.ServiceProviders.AsNoTracking().FirstOrDefaultAsync(p => p.Id == row.ServiceProviderId.Value && p.IsActive);
                    if (provider is null)
                    {
                        ModelState.AddModelError(prefix + nameof(row.ServiceProviderId), "شرکت خدماتی معتبر یا فعال نیست.");
                    }
                }
            }
            else if (row.PartyType == LoadingExpensePartyType.OperationalAsset)
            {
                if (!row.OperationalAssetId.HasValue)
                {
                    ModelState.AddModelError(prefix + nameof(row.OperationalAssetId), "دارایی عملیاتی را انتخاب کنید.");
                }
                else
                {
                    asset = await _db.OperationalAssets.AsNoTracking().FirstOrDefaultAsync(a => a.Id == row.OperationalAssetId.Value && a.IsActive);
                    if (asset is null)
                    {
                        ModelState.AddModelError(prefix + nameof(row.OperationalAssetId), "دارایی عملیاتی معتبر یا فعال نیست.");
                    }
                }
            }

            // جلوگیری از تکرار: اگر نوع مصرف قبلاً برای این گروه ثبت شده و کاربر تیک «مصرف جدید» را نزده.
            if (type is not null && existingTypeIds.Contains(type.Id) && !row.AllowDuplicate)
            {
                ModelState.AddModelError(prefix + nameof(row.ExpenseTypeId),
                    "برای این نوع مصرف قبلاً هزینه‌ای ثبت شده است. برای جلوگیری از ثبت دوباره، نوع مصرف متفاوتی انتخاب کنید.");
            }

            if (type is not null)
            {
                prepared.Add((row, type, provider, asset));
            }
        }

        if (!ModelState.IsValid)
        {
            await PopulateGroupExpenseModalLookupsAsync();
            model.ExistingExpenses = await LoadGroupExpenseItemsAsync(legs);
            RefreshGroupExpenseModalModel(model, legs);
            ViewData["IsExpenseModal"] = true;
            ViewData["CancelUrl"] = model.ReturnUrl;
            if (isAjax) { Response.StatusCode = 400; }
            return PartialView("_TransportExpenseEditor", model);
        }

        var orderedLegs = legs
            .OrderBy(l => l.SourcePurchaseContractId).ThenBy(l => l.LoadedDate).ThenBy(l => l.Id)
            .ToList();
        var normalizedGroupKey = BuildTransportGroupKey(orderedLegs[0]);
        var createdCount = 0;

        IDbContextTransaction? transaction = null;
        try
        {
            transaction = await BeginTransactionIfSupportedAsync();

            foreach (var (row, type, provider, asset) in prepared)
            {
                // نوع مصرف دستیِ تازه را در صورت نبود بساز (مثل مسیر موجود).
                var expenseType = type;
                if (expenseType.Id == 0)
                {
                    expenseType = new ExpenseType
                    {
                        Code = await BuildManualExpenseTypeCodeAsync(row.ManualExpenseTypeName!.Trim()),
                        Name = row.ManualExpenseTypeName!.Trim(),
                        NamePersian = row.ManualExpenseTypeName!.Trim(),
                        Category = "Transport",
                        IsActive = true
                    };
                    _db.ExpenseTypes.Add(expenseType);
                    await _db.SaveChangesAsync();
                }

                var amountUsd = decimal.Round(row.AmountUsd, 4, MidpointRounding.AwayFromZero);
                var usdByLeg = AllocateRoundedByWeight(amountUsd, orderedLegs.Select(l => l.QuantityMt).ToList());

                for (var i = 0; i < orderedLegs.Count; i++)
                {
                    var leg = orderedLegs[i];
                    var legAmount = usdByLeg[i];
                    if (legAmount <= 0m)
                    {
                        continue;
                    }

                    var sharePercent = CalculateSharePercent(leg.QuantityMt, totalQuantityMt);
                    var expense = new ExpenseTransaction
                    {
                        ExpenseTypeId = expenseType.Id,
                        ContractId = leg.SourcePurchaseContractId,
                        ShipmentId = leg.ShipmentId,
                        TransportLegId = leg.Id,
                        ServiceProviderId = provider?.Id,
                        OperationalAssetId = asset?.Id,
                        ExpenseDate = _businessClock.Today,
                        Amount = legAmount,
                        Currency = SystemCurrency.BaseCurrencyCode,
                        AppliedFxRateToUsd = 1m,
                        AmountUsd = legAmount,
                        Description = BuildModalExpenseDescription(row, leg, normalizedGroupKey, amountUsd, sharePercent)
                    };
                    _db.ExpenseTransactions.Add(expense);
                    await _db.SaveChangesAsync();

                    // مرحله ۵ — Dual-write داخل همان Transaction قدیمی.
                    if (_expenseAccounting is not null)
                    {
                        await _expenseAccounting.TryPostExpenseAsync(expense);
                    }

                    var ledgerEntry = new LedgerEntry
                    {
                        EntryDate = expense.ExpenseDate,
                        Side = provider is not null ? LedgerSide.Credit : LedgerSide.Debit,
                        AmountUsd = expense.AmountUsd,
                        Currency = SystemCurrency.BaseCurrencyCode,
                        SourceAmount = expense.Amount,
                        SourceCurrencyCode = expense.Currency,
                        AppliedFxRateToUsd = expense.AppliedFxRateToUsd,
                        AppliedFxRateDate = expense.ExpenseDate,
                        AppliedFxRateSource = "Base currency",
                        Description = BuildLedgerDescription(expenseType, expense),
                        SourceType = "Expense",
                        SourceId = expense.Id,
                        Reference = BuildLedgerReference(expenseType, expense),
                        ContractId = expense.ContractId,
                        ShipmentId = expense.ShipmentId,
                        ServiceProviderId = expense.ServiceProviderId
                    };
                    _db.LedgerEntries.Add(ledgerEntry);
                    await _db.SaveChangesAsync();

                    await _audit.LogAndSaveAsync(
                        nameof(ExpenseTransaction),
                        expense.Id,
                        AuditAction.Insert,
                        diff: AuditDiffFormatter.ForCreate(
                            ("ExpenseTypeId", expense.ExpenseTypeId),
                            ("ContractId", expense.ContractId),
                            ("TransportLegId", expense.TransportLegId),
                            ("ServiceProviderId", expense.ServiceProviderId),
                            ("OperationalAssetId", expense.OperationalAssetId),
                            ("AmountUsd", expense.AmountUsd),
                            ("GroupKey", normalizedGroupKey),
                            ("LedgerReference", ledgerEntry.Reference)));

                    createdCount++;
                }
            }

            if (transaction is not null)
            {
                await transaction.CommitAsync();
            }
        }
        catch (Exception ex)
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync();
            }

            _logger.LogError(ex, "Failed to save transport group expenses for {GroupKey}.", model.GroupKey);
            ModelState.AddModelError(string.Empty, "ثبت مصارف ذخیره نشد. مقدارها را بررسی کنید و دوباره تلاش کنید.");
            await PopulateGroupExpenseModalLookupsAsync();
            model.ExistingExpenses = await LoadGroupExpenseItemsAsync(legs);
            RefreshGroupExpenseModalModel(model, legs);
            ViewData["IsExpenseModal"] = true;
            ViewData["CancelUrl"] = model.ReturnUrl;
            if (isAjax) { Response.StatusCode = 400; }
            return PartialView("_TransportExpenseEditor", model);
        }
        finally
        {
            if (transaction is not null)
            {
                await transaction.DisposeAsync();
            }
        }

        var redirectUrl = TryGetLocalReturnUrl(model.ReturnUrl, out var local)
            ? local
            : model.TransportLegId.HasValue
                ? Url?.Action(nameof(Details), new { id = model.TransportLegId.Value }) ?? $"/InventoryTransportLegs/Details/{model.TransportLegId.Value}"
            : Url?.Action(nameof(Journey), new { groupKey = normalizedGroupKey }) ?? $"/InventoryTransportLegs/Journey?groupKey={normalizedGroupKey}";

        TempData["ok"] = $"{createdCount:N0} رکورد مصرف برای این حمل ثبت شد.";

        if (isAjax)
        {
            return Json(new { success = true, redirectUrl });
        }

        return Redirect(redirectUrl);
    }

    private static bool RowHasValue(InventoryTransportGroupExpenseModalRow row)
        => row.ExpenseTypeId.HasValue
            || !string.IsNullOrWhiteSpace(row.ManualExpenseTypeName)
            || row.AmountUsd != 0m
            || row.QuantityMt.HasValue
            || row.UnitRateUsd.HasValue
            || !string.IsNullOrWhiteSpace(row.Notes);

    private string? ValidateManualExpenseTypeName(string? value, string modelKey)
    {
        var normalized = NormalizeString(value);
        if (normalized is null)
        {
            return null;
        }

        if (normalized.Length > 200)
        {
            ModelState.AddModelError(modelKey, "نوع مصرف دستی نمی‌تواند بیشتر از 200 کرکتر باشد.");
        }

        if (normalized.Any(char.IsControl))
        {
            ModelState.AddModelError(modelKey, "نوع مصرف دستی دارای کاراکتر نامعتبر است.");
        }

        return normalized;
    }

    private async Task<IReadOnlyList<InventoryTransportFlowExpenseItemViewModel>> LoadGroupExpenseItemsAsync(
        IReadOnlyList<InventoryTransportLeg> legs)
    {
        if (legs.Count == 0)
        {
            return [];
        }

        var legIds = legs.Select(l => l.Id).ToList();
        return await _db.ExpenseTransactions
            .AsNoTracking()
            .Where(e => e.TransportLegId.HasValue && legIds.Contains(e.TransportLegId.Value) && !e.IsCancelled)
            .OrderByDescending(e => e.ExpenseDate)
            .ThenByDescending(e => e.Id)
            .Select(e => new InventoryTransportFlowExpenseItemViewModel
            {
                Id = e.Id,
                TransportLegId = e.TransportLegId,
                ContractId = e.ContractId,
                ContractNumber = e.Contract != null ? e.Contract.ContractNumber : "",
                ExpenseDate = e.ExpenseDate,
                ExpenseTypeName = e.ExpenseType != null ? (e.ExpenseType.NamePersian ?? e.ExpenseType.Name) : "",
                ServiceProviderName = e.ServiceProvider != null ? e.ServiceProvider.Name : null,
                OperationalAssetName = e.OperationalAsset != null ? e.OperationalAsset.Name : null,
                Amount = e.Amount,
                Currency = e.Currency,
                AmountUsd = e.AmountUsd,
                Description = e.Description
            })
            .ToListAsync();
    }

    private async Task<List<InventoryTransportLeg>> LoadExpenseTransportLegsAsync(int transportLegId)
    {
        var leg = await _db.InventoryTransportLegs
            .AsNoTracking()
            .Include(l => l.SourcePurchaseContract)
            .Include(l => l.SourceStorageTank)
            .Include(l => l.Shipment)
            .FirstOrDefaultAsync(l => l.Id == transportLegId && l.Status != InventoryTransportLegStatus.Cancelled);

        return leg is null ? [] : [leg];
    }

    private static void RefreshGroupExpenseModalModel(
        InventoryTransportGroupExpenseModalViewModel model,
        IReadOnlyList<InventoryTransportLeg> legs)
    {
        if (legs.Count == 0)
        {
            model.Allocations = [];
            return;
        }

        model.GroupKey = BuildTransportGroupKey(legs[0]);
        model.TransportReference = ResolveGroupExpenseTransportReference(legs);
        model.TotalAllocatedQuantityMt = legs.Sum(l => l.QuantityMt);
        model.Allocations = BuildGroupExpenseAllocationPreview(legs, model.ExistingExpenses.Sum(e => e.Amount));
    }

    private async Task PopulateGroupExpenseModalLookupsAsync()
    {
        // نوع مصرف استاندارد «کرایه حمل» را تضمین کن تا کاربر بتواند آن را در مودال انتخاب کند.
        await new InventoryTransportReceiptService(_db, _currencyConversion, _lineage).EnsureTransportFreightExpenseTypeAsync();

        ViewBag.ExpenseTypes = new SelectList(
            await _db.ExpenseTypes.AsNoTracking()
                .Where(e => e.IsActive)
                .OrderBy(e => e.Category).ThenBy(e => e.Code)
                .Select(e => new { e.Id, Text = e.Code + " - " + (e.NamePersian ?? e.Name) })
                .ToListAsync(),
            "Id", "Text");
        ViewBag.ServiceProviders = new SelectList(
            await _db.ServiceProviders.AsNoTracking()
                .Where(p => p.IsActive)
                .OrderBy(p => p.Name)
                .Select(p => new { p.Id, Text = string.IsNullOrWhiteSpace(p.Code) ? p.Name : p.Code + " - " + p.Name })
                .ToListAsync(),
            "Id", "Text");
        ViewBag.OperationalAssets = new SelectList(
            await _db.OperationalAssets.AsNoTracking()
                .Where(a => a.IsActive)
                .OrderBy(a => a.AssetCode).ThenBy(a => a.Name)
                .Select(a => new { a.Id, Text = a.AssetCode + " - " + a.Name })
                .ToListAsync(),
            "Id", "Text");
    }

    private static string BuildModalExpenseDescription(
        InventoryTransportGroupExpenseModalRow row,
        InventoryTransportLeg leg,
        string normalizedGroupKey,
        decimal totalAmountUsd,
        decimal sharePercent)
    {
        var contractNumber = leg.SourcePurchaseContract?.ContractNumber ?? $"#{leg.SourcePurchaseContractId}";
        var description = string.Join(" | ", new[]
        {
            string.IsNullOrWhiteSpace(row.Notes) ? "مصرف انتقال از موجودی" : row.Notes!.Trim(),
            $"GroupKey: {normalizedGroupKey}",
            $"Total USD: {totalAmountUsd:N4}",
            $"Contract: {contractNumber}",
            $"Leg: #{leg.Id}",
            $"Quantity: {leg.QuantityMt:N4} MT",
            $"Share: {sharePercent:N4}%"
        });

        return description.Length <= 1000 ? description : description[..1000];
    }

    private async Task MarkLegsLoadedAsync(IReadOnlyList<InventoryTransportLeg> legs)
    {
        if (legs.Count == 0)
        {
            TempData["error"] = "No transport allocation was selected for loading.";
            return;
        }

        IDbContextTransaction? transaction = null;
        try
        {
            foreach (var leg in legs)
            {
                await _legLoad.ValidateForLoadAsync(leg);
            }

            transaction = await BeginTransactionIfSupportedAsync();

            foreach (var leg in legs)
            {
                await _legLoad.LoadAsync(leg);
            }

            if (transaction is not null)
            {
                await transaction.CommitAsync();
            }

            TempData["ok"] = legs.Count == 1
                ? "Source inventory was reduced and outbound movement was created."
                : $"{legs.Count:N0} source allocations were reduced and outbound movements were created.";
        }
        catch (BusinessRuleException ex)
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync();
            }
            TempData["error"] = ex.Message;
        }
        finally
        {
            if (transaction is not null)
            {
                await transaction.DisposeAsync();
            }
        }
    }

    private async Task<InventoryTransportLegDetailsViewModel?> BuildDetailsViewModelAsync(int id)
    {
        var model = await _db.InventoryTransportLegs
            .AsNoTracking()
            .Include(l => l.SourcePurchaseContract)
            .Include(l => l.Product)
            .Include(l => l.SourceTerminal)
            .Include(l => l.SourceStorageTank)
            .Include(l => l.DestinationTerminal)
            .Include(l => l.DestinationStorageTank)
            .Include(l => l.DestinationLocation)
            .Include(l => l.OutboundInventoryMovement)
            .Where(l => l.Id == id)
            .Select(l => new InventoryTransportLegDetailsViewModel
            {
                Id = l.Id,
                ShipmentId = l.ShipmentId,
                ShipmentCode = l.Shipment != null ? l.Shipment.ShipmentCode : null,
                ContractNumber = l.SourcePurchaseContract != null ? l.SourcePurchaseContract.ContractNumber : "",
                ProductName = l.Product != null ? l.Product.Name : "",
                SourceTerminalName = l.SourceTerminal != null ? l.SourceTerminal.Name : "",
                SourceTankCode = l.SourceStorageTank == null
                    ? null
                    : l.SourceStorageTank.DisplayName == null || l.SourceStorageTank.DisplayName == ""
                        ? l.SourceStorageTank.TankCode
                        : l.SourceStorageTank.DisplayName,
                DestinationTerminalName = l.DestinationTerminal != null ? l.DestinationTerminal.Name : null,
                DestinationTankCode = l.DestinationStorageTank == null
                    ? null
                    : l.DestinationStorageTank.DisplayName == null || l.DestinationStorageTank.DisplayName == ""
                        ? l.DestinationStorageTank.TankCode
                        : l.DestinationStorageTank.DisplayName,
                DestinationLocationName = l.DestinationLocation != null ? l.DestinationLocation.Name : null,
                TransportType = l.TransportType,
                WagonNumber = l.WagonNumber,
                RwbNo = l.RwbNo,
                BillOfLadingNumber = l.BillOfLadingNumber,
                RouteDescription = l.RouteDescription,
                ServiceProviderId = l.ServiceProviderId,
                ServiceProviderName = l.ServiceProvider != null ? l.ServiceProvider.Name : null,
                OperationalAssetId = l.OperationalAssetId,
                OperationalAssetName = l.OperationalAsset != null ? l.OperationalAsset.Name : null,
                LoadedDate = l.LoadedDate,
                ExpectedArrivalDate = l.ExpectedArrivalDate,
                QuantityMt = l.QuantityMt,
                ChargeableQuantityMt = l.ChargeableQuantityMt,
                PurchaseUnitCostUsd = l.PurchaseUnitCostUsd,
                Status = l.Status,
                OutboundInventoryMovementId = l.OutboundInventoryMovementId,
                OutboundReferenceDocument = l.OutboundInventoryMovement != null
                    ? l.OutboundInventoryMovement.ReferenceDocument
                    : null,
                Notes = l.Notes
            })
            .FirstOrDefaultAsync();

        if (model is null)
        {
            return null;
        }

        model.Expenses = await _db.ExpenseTransactions
            .AsNoTracking()
            .Include(e => e.ExpenseType)
            .Where(e => e.TransportLegId == id && !e.IsCancelled)
            .OrderByDescending(e => e.ExpenseDate)
            .ThenByDescending(e => e.Id)
            .Select(e => new InventoryTransportLegExpenseItemViewModel
            {
                Id = e.Id,
                ExpenseDate = e.ExpenseDate,
                ExpenseTypeName = e.ExpenseType != null ? (e.ExpenseType.NamePersian ?? e.ExpenseType.Name) : "",
                ServiceProviderName = e.ServiceProvider != null ? e.ServiceProvider.Name : null,
                OperationalAssetName = e.OperationalAsset != null ? e.OperationalAsset.Name : null,
                AmountUsd = e.AmountUsd,
                Description = e.Description
            })
            .ToListAsync();

        model.CustomsDeclarations = await _db.CustomsDeclarations
            .AsNoTracking()
            .Where(cd => cd.TransportLegId == id)
            .OrderByDescending(cd => cd.DeclarationDate)
            .ThenByDescending(cd => cd.Id)
            .Select(cd => new InventoryTransportLegCustomsItemViewModel
            {
                Id = cd.Id,
                DeclarationDate = cd.DeclarationDate,
                WagonOrTruckNumber = cd.WagonOrTruckNumber,
                DeclarationReference = cd.DeclarationReference,
                ConsignmentWeightMt = cd.ConsignmentWeightMt,
                TotalAfn = cd.TotalAfn,
                TotalUsd = cd.TotalUsd
            })
            .ToListAsync();

        model.Losses = await _db.LossEvents
            .AsNoTracking()
            .Where(le => le.TransportLegId == id && !le.IsCancelled)
            .OrderByDescending(le => le.EventDate)
            .ThenByDescending(le => le.Id)
            .Select(le => new InventoryTransportLegLossItemViewModel
            {
                Id = le.Id,
                EventDate = le.EventDate,
                StageName = le.Stage.ToString(),
                ExpectedQuantityMt = le.ExpectedQuantityMt,
                ActualQuantityMt = le.ActualQuantityMt,
                DifferenceQuantityMt = le.DifferenceQuantityMt,
                ChargeableLossMt = le.ChargeableLossMt,
                Reference = le.Reference
            })
            .ToListAsync();

        var destinationReceipts = await _db.InventoryTransportReceipts
            .AsNoTracking()
            .Include(r => r.DestinationTerminal)
            .Include(r => r.DestinationStorageTank)
            .Include(r => r.SalesTransaction)
            .Include(r => r.DirectTruckDispatches)
            .Where(r => r.InventoryTransportLegId == id && !r.IsCancelled)
            .OrderByDescending(r => r.ReceiptDate)
            .ThenByDescending(r => r.Id)
            .Select(r => new InventoryTransportReceiptSummaryViewModel
            {
                Id = r.Id,
                ReceiptDate = r.ReceiptDate,
                ReceiptDestination = r.ReceiptDestination,
                ReceivedQuantityMt = r.ReceivedQuantityMt,
                ShortageQuantityMt = r.ShortageQuantityMt,
                AllowanceMt = r.AllowanceMt,
                ChargeableShortageMt = r.ChargeableShortageMt,
                FreightRateUsdPerMt = r.FreightRateUsdPerMt,
                FreightCostUsd = r.FreightCostUsd,
                ShortageRateUsd = r.ShortageRateUsd,
                ShortageChargeUsd = r.ShortageChargeUsd,
                FreightPayableUsd = r.FreightPayableUsd,
                ServiceProviderName = r.ServiceProvider != null ? r.ServiceProvider.Name : null,
                OperationalAssetName = r.OperationalAsset != null ? r.OperationalAsset.Name : null,
                DestinationTerminalName = r.DestinationTerminal != null ? r.DestinationTerminal.Name : null,
                DestinationTankCode = StorageTankDisplay.BuildOptional(r.DestinationStorageTank),
                InventoryMovementId = r.InventoryMovementId,
                SalesTransactionId = r.SalesTransactionId,
                SaleInvoiceNumber = r.SalesTransaction != null ? r.SalesTransaction.InvoiceNumber : null,
                DirectTruckDispatchCount = r.DirectTruckDispatches.Count(d => d.Status != DispatchStatus.Cancelled),
                DirectTruckDispatchedQuantityMt = r.DirectTruckDispatches
                    .Where(d => d.Status != DispatchStatus.Cancelled)
                    .Sum(d => d.LoadedQuantityMt),
                FirstDirectTruckDispatchId = r.DirectTruckDispatches
                    .Where(d => d.Status != DispatchStatus.Cancelled)
                    .OrderBy(d => d.Id)
                    .Select(d => (int?)d.Id)
                    .FirstOrDefault(),
                Notes = r.Notes
            })
            .ToListAsync();

        model.DestinationReceipts = destinationReceipts;
        model.DestinationReceipt = destinationReceipts.FirstOrDefault();

        var pnlByLeg = await new InventoryTransportPnlService(_db).BuildForLegsAsync([id]);
        if (pnlByLeg.TryGetValue(id, out var pnl))
        {
            model.PurchaseUnitCostUsd = pnl.PurchaseUnitCostUsd ?? model.PurchaseUnitCostUsd;
            model.Pnl = new InventoryTransportLegPnlSummaryViewModel
            {
                PurchaseUnitCostUsd = pnl.PurchaseUnitCostUsd,
                PurchaseCostSource = pnl.PurchaseCostSource,
                PurchaseCostUsd = pnl.PurchaseCostUsd,
                ExpenseTransactionsUsd = pnl.ExpenseTransactionsUsd,
                SharedShipmentExpensesUsd = pnl.SharedShipmentExpensesUsd,
                ReceivedQuantityMt = pnl.ReceivedQuantityMt,
                ShortageQuantityMt = pnl.ShortageQuantityMt,
                CustomsUsd = pnl.CustomsUsd,
                ReceiptFreightCostUsd = pnl.ReceiptFreightCostUsd,
                ShortageChargeUsd = pnl.ShortageChargeUsd,
                ReceiptFreightExpenseUsd = pnl.ReceiptFreightExpenseUsd,
                OperationalExpensesUsd = pnl.OperationalExpensesUsd,
                TotalCostUsd = pnl.TotalCostUsd,
                SoldQuantityMt = pnl.SoldQuantityMt,
                SalesUsd = pnl.SalesUsd,
                UnsoldQuantityMt = pnl.UnsoldQuantityMt,
                LossQuantityMt = pnl.LossQuantityMt,
                LossCostUsd = pnl.LossCostUsd,
                GrossMarginUsd = pnl.GrossMarginUsd,
                SalesTraceNote = pnl.SalesTraceNote,
                Sales = pnl.Sales
                    .Select(s => new InventoryTransportLegPnlSaleItemViewModel
                    {
                        SaleId = s.SaleId,
                        InvoiceNumber = s.InvoiceNumber,
                        SaleDate = s.SaleDate,
                        QuantityMt = s.QuantityMt,
                        AmountUsd = s.AmountUsd,
                        TraceKind = s.TraceKind
                    })
                    .ToList()
            };
        }

        // باقیمانده بر اساس مجموع همه رسیدها (دریافت + کسری تجمعی) محاسبه می‌شود، نه فقط آخرین رسید.
        var totalReceivedMt = destinationReceipts.Sum(r => r.ReceivedQuantityMt);
        var totalShortageMt = destinationReceipts.Sum(r => r.ShortageQuantityMt);
        var processedQuantityMt = Math.Max(
            Math.Max(totalReceivedMt, model.Pnl.ReceivedQuantityMt),
            model.Pnl.SoldQuantityMt);
        var lossQuantityMt = Math.Max(
            Math.Max(totalShortageMt, model.Pnl.ShortageQuantityMt),
            model.Pnl.LossQuantityMt);
        model.RemainingQuantityMt = decimal.Round(
            Math.Max(model.QuantityMt - processedQuantityMt - lossQuantityMt, 0m),
            4,
            MidpointRounding.AwayFromZero);

        return model;
    }

    private async Task<InventoryTransportJourneyViewModel?> BuildJourneyViewModelAsync(string groupKey)
    {
        var legs = await LoadTransportGroupLegsAllStatusesAsync(groupKey);
        if (legs.Count == 0)
        {
            return null;
        }

        var normalizedGroupKey = BuildTransportGroupKey(legs[0]);
        var legIds = legs.Select(l => l.Id).ToList();
        var projections = legs.Select(ToTransportFlowProjection).ToList();

        var receiptTotals = await _db.InventoryTransportReceipts
            .AsNoTracking()
            .Where(r => legIds.Contains(r.InventoryTransportLegId) && !r.IsCancelled)
            .GroupBy(r => r.InventoryTransportLegId)
            .Select(g => new TransportReceiptTotals
            {
                LegId = g.Key,
                ReceivedQuantityMt = g.Sum(r => r.ReceivedQuantityMt),
                ShortageQuantityMt = g.Sum(r => r.ShortageQuantityMt)
            })
            .ToDictionaryAsync(x => x.LegId);

        var expensesByLeg = (await _db.ExpenseTransactions
                .AsNoTracking()
                .Where(e => e.TransportLegId.HasValue
                    && legIds.Contains(e.TransportLegId.Value)
                    && !e.IsCancelled)
                .Select(e => new InventoryTransportFlowExpenseItemViewModel
                {
                    Id = e.Id,
                    TransportLegId = e.TransportLegId,
                    ContractId = e.ContractId,
                    ContractNumber = e.Contract != null
                        ? e.Contract.ContractNumber
                        : e.TransportLeg != null && e.TransportLeg.SourcePurchaseContract != null
                            ? e.TransportLeg.SourcePurchaseContract.ContractNumber
                            : "",
                    ExpenseDate = e.ExpenseDate,
                    ExpenseTypeName = e.ExpenseType != null ? e.ExpenseType.NamePersian ?? e.ExpenseType.Name : "",
                    ServiceProviderName = e.ServiceProvider != null ? e.ServiceProvider.Name : null,
                    OperationalAssetName = e.OperationalAsset != null ? e.OperationalAsset.Name : null,
                    Amount = e.Amount,
                    Currency = e.Currency,
                    AmountUsd = e.AmountUsd,
                    Description = e.Description
                })
                .ToListAsync())
            .GroupBy(e => e.TransportLegId!.Value)
            .ToDictionary(g => g.Key, g => g.ToList());

        var transport = BuildTransportFlow(normalizedGroupKey, projections, receiptTotals, expensesByLeg);

        var pnlByLeg = await new InventoryTransportPnlService(_db).BuildForLegsAsync(legIds);
        var pnl = AggregateJourneyPnl(pnlByLeg.Values);

        var sales = pnlByLeg.Values
            .SelectMany(s => s.Sales)
            .GroupBy(s => s.SaleId)
            .Select(g => new InventoryTransportJourneySaleItemViewModel
            {
                SaleId = g.Key,
                InvoiceNumber = g.First().InvoiceNumber,
                SaleDate = g.First().SaleDate,
                QuantityMt = g.Sum(x => x.QuantityMt),
                AmountUsd = g.Sum(x => x.AmountUsd),
                TraceKind = g.Select(x => x.TraceKind).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? ""
            })
            .OrderByDescending(s => s.SaleDate)
            .ThenByDescending(s => s.SaleId)
            .ToList();

        var losses = await _db.LossEvents
            .AsNoTracking()
            .Where(le => le.TransportLegId.HasValue && legIds.Contains(le.TransportLegId.Value) && !le.IsCancelled)
            .OrderByDescending(le => le.EventDate)
            .ThenByDescending(le => le.Id)
            .Select(le => new InventoryTransportLegLossItemViewModel
            {
                Id = le.Id,
                EventDate = le.EventDate,
                StageName = le.Stage.ToString(),
                ExpectedQuantityMt = le.ExpectedQuantityMt,
                ActualQuantityMt = le.ActualQuantityMt,
                DifferenceQuantityMt = le.DifferenceQuantityMt,
                ChargeableLossMt = le.ChargeableLossMt,
                Reference = le.Reference
            })
            .ToListAsync();

        // گمرکِ ثبت‌شده برای ردیف‌های این حمل — فقط‌خواندنی برای نمایش در صفحهٔ جریان (بدون هیچ منطق مالی/ذخیره).
        var customsRaw = await _db.CustomsDeclarations
            .AsNoTracking()
            .Where(c => c.TransportLegId.HasValue && legIds.Contains(c.TransportLegId.Value))
            .OrderByDescending(c => c.DeclarationDate)
            .ThenByDescending(c => c.Id)
            .Select(c => new
            {
                LegId = c.TransportLegId!.Value,
                c.Id,
                c.DeclarationDate,
                c.WagonOrTruckNumber,
                c.DeclarationReference,
                c.PermitNumber,
                c.GoodsName,
                c.CustomsType,
                c.ConsignmentWeightMt,
                c.TotalAfn,
                c.TotalUsd,
                ItemCount = c.Items.Count,
                DocumentCount = c.Documents.Count
            })
            .ToListAsync();

        var contractNumberByLeg = legs.ToDictionary(
            l => l.Id,
            l => l.SourcePurchaseContract?.ContractNumber ?? $"#{l.SourcePurchaseContractId}");

        var customs = customsRaw
            .Select(c => new InventoryTransportJourneyCustomsItemViewModel
            {
                Id = c.Id,
                LegId = c.LegId,
                ContractNumber = contractNumberByLeg.TryGetValue(c.LegId, out var cn) ? cn : "",
                DeclarationDate = c.DeclarationDate,
                WagonOrTruckNumber = c.WagonOrTruckNumber,
                DeclarationReference = c.DeclarationReference,
                PermitNumber = c.PermitNumber,
                GoodsName = c.GoodsName,
                CustomsType = c.CustomsType,
                ConsignmentWeightMt = c.ConsignmentWeightMt,
                TotalAfn = c.TotalAfn,
                TotalUsd = c.TotalUsd,
                ItemCount = c.ItemCount,
                DocumentCount = c.DocumentCount
            })
            .ToList();

        return new InventoryTransportJourneyViewModel
        {
            Transport = transport,
            Pnl = pnl,
            Stages = BuildJourneyStages(transport, pnl),
            Sales = sales,
            Losses = losses,
            Customs = customs,
            HasDraftAllocations = legs.Any(l => l.Status == InventoryTransportLegStatus.Draft && !l.OutboundInventoryMovementId.HasValue),
            HasReceivableAllocations = legs.Any(l => l.Status is InventoryTransportLegStatus.Loaded or InventoryTransportLegStatus.InTransit)
        };
    }

    private async Task<List<InventoryTransportLeg>> LoadTransportGroupLegsAllStatusesAsync(string groupKey)
    {
        var normalizedGroupKey = groupKey.Trim();
        IQueryable<InventoryTransportLeg> query = _db.InventoryTransportLegs
            .AsNoTracking()
            .Include(l => l.SourcePurchaseContract)
            .Include(l => l.Product)
            .Include(l => l.SourceTerminal)
            .Include(l => l.SourceStorageTank)
            .Include(l => l.DestinationTerminal)
            .Include(l => l.DestinationStorageTank)
            .Include(l => l.DestinationLocation)
            .Include(l => l.Truck)
            .Include(l => l.Wagon)
            .Include(l => l.Shipment);
        query = query
            .Include(l => l.Allocations)
                .ThenInclude(a => a.SourcePurchaseContract);

        // وقتی کلید گروه شناسه‌ی صریح دارد، پیش‌فیلتر ارزان در SQL؛ تطابق دقیق همچنان با کلید گروه.
        if (normalizedGroupKey.StartsWith("ITG:", StringComparison.OrdinalIgnoreCase))
        {
            query = query.Where(l => l.TransportGroupKey == normalizedGroupKey);
        }
        else if (normalizedGroupKey.StartsWith("SHIP:", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(normalizedGroupKey.AsSpan(5), out var shipmentId))
        {
            query = query.Where(l => l.ShipmentId == shipmentId);
        }
        else if (normalizedGroupKey.StartsWith("LEG:", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(normalizedGroupKey.AsSpan(4), out var legId))
        {
            query = query.Where(l => l.Id == legId);
        }

        var candidates = await query.ToListAsync();
        return candidates
            .Where(l => string.Equals(BuildTransportGroupKey(l), normalizedGroupKey, StringComparison.OrdinalIgnoreCase))
            .OrderBy(l => l.LoadedDate)
            .ThenBy(l => l.Id)
            .ToList();
    }

    private static TransportFlowLegProjection ToTransportFlowProjection(InventoryTransportLeg leg)
        => new()
        {
            Id = leg.Id,
            ShipmentId = leg.ShipmentId,
            ShipmentCode = leg.Shipment?.ShipmentCode,
            TransportGroupKey = leg.TransportGroupKey,
            SourcePurchaseContractId = leg.SourcePurchaseContractId,
            ContractNumber = leg.SourcePurchaseContract?.ContractNumber ?? "",
            ProductName = leg.Product?.Name ?? "",
            SourceTerminalName = leg.SourceTerminal?.Name ?? "",
            SourceTankCode = StorageTankDisplay.BuildOptional(leg.SourceStorageTank),
            DestinationTerminalName = leg.DestinationTerminal?.Name,
            DestinationTankCode = StorageTankDisplay.BuildOptional(leg.DestinationStorageTank),
            DestinationLocationName = leg.DestinationLocation?.Name,
            TransportType = leg.TransportType,
            WagonNumber = leg.Wagon?.WagonNumber ?? leg.Truck?.PlateNumber ?? leg.WagonNumber,
            RwbNo = leg.RwbNo,
            BillOfLadingNumber = leg.BillOfLadingNumber,
            RouteDescription = leg.RouteDescription,
            LoadedDate = leg.LoadedDate,
            ExpectedArrivalDate = leg.ExpectedArrivalDate,
            QuantityMt = leg.QuantityMt,
            PurchaseUnitCostUsd = leg.PurchaseUnitCostUsd,
            Status = leg.Status,
            OutboundInventoryMovementId = leg.OutboundInventoryMovementId,
            Allocations = leg.Allocations.Select(a => new TransportFlowAllocationProjection
            {
                SourcePurchaseContractId = a.SourcePurchaseContractId,
                ContractNumber = a.SourcePurchaseContract?.ContractNumber ?? "",
                QuantityMt = a.QuantityMt,
                OutboundInventoryMovementId = a.OutboundInventoryMovementId
            }).ToList()
        };

    private static InventoryTransportJourneyPnlViewModel AggregateJourneyPnl(IEnumerable<InventoryTransportPnlSnapshot> snapshots)
    {
        var list = snapshots.ToList();
        return new InventoryTransportJourneyPnlViewModel
        {
            PurchaseCostUsd = list.Sum(s => s.PurchaseCostUsd),
            ExpenseTransactionsUsd = list.Sum(s => s.ExpenseTransactionsUsd),
            SharedShipmentExpensesUsd = list.Sum(s => s.SharedShipmentExpensesUsd),
            CustomsUsd = list.Sum(s => s.CustomsUsd),
            ReceiptFreightExpenseUsd = list.Sum(s => s.ReceiptFreightExpenseUsd),
            OperationalExpensesUsd = list.Sum(s => s.OperationalExpensesUsd),
            TotalCostUsd = list.Sum(s => s.TotalCostUsd),
            SoldQuantityMt = list.Sum(s => s.SoldQuantityMt),
            SalesUsd = list.Sum(s => s.SalesUsd),
            UnsoldQuantityMt = list.Sum(s => s.UnsoldQuantityMt),
            LossQuantityMt = list.Sum(s => s.LossQuantityMt),
            LossCostUsd = list.Sum(s => s.LossCostUsd),
            GrossMarginUsd = list.Sum(s => s.GrossMarginUsd),
            HasMissingPurchaseCost = list.Any(s => !s.PurchaseUnitCostUsd.HasValue)
        };
    }

    private static IReadOnlyList<InventoryTransportJourneyStageViewModel> BuildJourneyStages(
        InventoryTransportFlowTransportViewModel transport,
        InventoryTransportJourneyPnlViewModel pnl)
        => new List<InventoryTransportJourneyStageViewModel>
        {
            new()
            {
                Title = "ثبت تخصیص",
                QuantityText = $"{transport.TotalAllocatedQuantityMt:N2} MT",
                Note = $"{transport.ContractCount:N0} قرارداد در {transport.LegCount:N0} ردیف",
                IsDone = transport.TotalAllocatedQuantityMt > 0m
            },
            new()
            {
                Title = "بارگیری (خروج موجودی)",
                QuantityText = $"{transport.LoadedQuantityMt:N2} MT",
                Note = transport.PendingQuantityMt > 0m ? $"{transport.PendingQuantityMt:N2} MT باقی‌مانده" : "کامل",
                IsDone = transport.LoadedQuantityMt > 0m && transport.PendingQuantityMt <= 0m
            },
            new()
            {
                Title = "رسید مقصد (ورود موجودی)",
                QuantityText = $"{transport.ReceivedQuantityMt:N2} MT",
                Note = transport.ShortageQuantityMt > 0m ? $"{transport.ShortageQuantityMt:N2} MT کسری" : null,
                IsDone = transport.ReceivedQuantityMt > 0m
            },
            new()
            {
                Title = "فروش / دیسپچ",
                QuantityText = $"{pnl.SoldQuantityMt:N2} MT",
                Note = pnl.UnsoldQuantityMt > 0m ? $"{pnl.UnsoldQuantityMt:N2} MT نافروخته" : "کامل",
                IsDone = pnl.SoldQuantityMt > 0m
            }
        };

    private async Task<IReadOnlyList<InventoryTransportFlowTransportViewModel>> BuildActiveTransportFlowsAsync()
    {
        var activeStatuses = new[]
        {
            InventoryTransportLegStatus.Draft,
            InventoryTransportLegStatus.Loaded,
            InventoryTransportLegStatus.InTransit
        };

        var legs = await _db.InventoryTransportLegs
            .AsNoTracking()
            .Where(l => activeStatuses.Contains(l.Status))
            .Select(l => new TransportFlowLegProjection
            {
                Id = l.Id,
                ShipmentId = l.ShipmentId,
                ShipmentCode = l.Shipment != null ? l.Shipment.ShipmentCode : null,
                TransportGroupKey = l.TransportGroupKey,
                SourcePurchaseContractId = l.SourcePurchaseContractId,
                ContractNumber = l.SourcePurchaseContract != null ? l.SourcePurchaseContract.ContractNumber : "",
                ProductName = l.Product != null ? l.Product.Name : "",
                SourceTerminalName = l.SourceTerminal != null ? l.SourceTerminal.Name : "",
                SourceTankCode = l.SourceStorageTank == null
                    ? null
                    : l.SourceStorageTank.DisplayName == null || l.SourceStorageTank.DisplayName == ""
                        ? l.SourceStorageTank.TankCode
                        : l.SourceStorageTank.DisplayName,
                DestinationTerminalName = l.DestinationTerminal != null ? l.DestinationTerminal.Name : null,
                DestinationTankCode = l.DestinationStorageTank == null
                    ? null
                    : l.DestinationStorageTank.DisplayName == null || l.DestinationStorageTank.DisplayName == ""
                        ? l.DestinationStorageTank.TankCode
                        : l.DestinationStorageTank.DisplayName,
                DestinationLocationName = l.DestinationLocation != null ? l.DestinationLocation.Name : null,
                TransportType = l.TransportType,
                WagonNumber = l.Wagon != null ? l.Wagon.WagonNumber : l.Truck != null ? l.Truck.PlateNumber : l.WagonNumber,
                RwbNo = l.RwbNo,
                BillOfLadingNumber = l.BillOfLadingNumber,
                RouteDescription = l.RouteDescription,
                LoadedDate = l.LoadedDate,
                ExpectedArrivalDate = l.ExpectedArrivalDate,
                QuantityMt = l.QuantityMt,
                PurchaseUnitCostUsd = l.PurchaseUnitCostUsd,
                Status = l.Status,
                OutboundInventoryMovementId = l.OutboundInventoryMovementId,
                Allocations = l.Allocations.Select(a => new TransportFlowAllocationProjection
                {
                    SourcePurchaseContractId = a.SourcePurchaseContractId,
                    ContractNumber = a.SourcePurchaseContract != null ? a.SourcePurchaseContract.ContractNumber : "",
                    QuantityMt = a.QuantityMt,
                    OutboundInventoryMovementId = a.OutboundInventoryMovementId
                }).ToList()
            })
            .ToListAsync();

        if (legs.Count == 0)
        {
            return [];
        }

        var legIds = legs.Select(l => l.Id).ToList();
        var receiptTotals = await _db.InventoryTransportReceipts
            .AsNoTracking()
            .Where(r => legIds.Contains(r.InventoryTransportLegId) && !r.IsCancelled)
            .GroupBy(r => r.InventoryTransportLegId)
            .Select(g => new TransportReceiptTotals
            {
                LegId = g.Key,
                ReceivedQuantityMt = g.Sum(r => r.ReceivedQuantityMt),
                ShortageQuantityMt = g.Sum(r => r.ShortageQuantityMt)
            })
            .ToDictionaryAsync(x => x.LegId);

        var expensesByLeg = (await _db.ExpenseTransactions
                .AsNoTracking()
                .Where(e => e.TransportLegId.HasValue
                    && legIds.Contains(e.TransportLegId.Value)
                    && !e.IsCancelled)
                .Select(e => new InventoryTransportFlowExpenseItemViewModel
                {
                    Id = e.Id,
                    TransportLegId = e.TransportLegId,
                    ContractId = e.ContractId,
                    ContractNumber = e.Contract != null
                        ? e.Contract.ContractNumber
                        : e.TransportLeg != null && e.TransportLeg.SourcePurchaseContract != null
                            ? e.TransportLeg.SourcePurchaseContract.ContractNumber
                            : "",
                    ExpenseDate = e.ExpenseDate,
                    ExpenseTypeName = e.ExpenseType != null ? e.ExpenseType.NamePersian ?? e.ExpenseType.Name : "",
                    ServiceProviderName = e.ServiceProvider != null ? e.ServiceProvider.Name : null,
                    OperationalAssetName = e.OperationalAsset != null ? e.OperationalAsset.Name : null,
                    Amount = e.Amount,
                    Currency = e.Currency,
                    AmountUsd = e.AmountUsd,
                    Description = e.Description
                })
                .ToListAsync())
            .GroupBy(e => e.TransportLegId!.Value)
            .ToDictionary(g => g.Key, g => g.ToList());

        return legs
            .GroupBy(BuildTransportGroupKey, StringComparer.OrdinalIgnoreCase)
            .Select(g => BuildTransportFlow(g.Key, g.ToList(), receiptTotals, expensesByLeg))
            .OrderByDescending(t => t.FirstLoadedDate)
            .ThenBy(t => t.PrimaryReference)
            .ToList();
    }

    private static InventoryTransportFlowTransportViewModel BuildTransportFlow(
        string groupKey,
        IReadOnlyList<TransportFlowLegProjection> legs,
        IReadOnlyDictionary<int, TransportReceiptTotals> receiptTotals,
        IReadOnlyDictionary<int, List<InventoryTransportFlowExpenseItemViewModel>> expensesByLeg)
    {
        var totalAllocated = legs.Sum(l => l.QuantityMt);
        var loaded = legs
            .Where(IsLoadedForTransportFlow)
            .Sum(l => l.QuantityMt);
        var received = legs.Sum(l => receiptTotals.TryGetValue(l.Id, out var totals) ? totals.ReceivedQuantityMt : 0m);
        var shortage = legs.Sum(l => receiptTotals.TryGetValue(l.Id, out var totals) ? totals.ShortageQuantityMt : 0m);
        var firstLeg = legs
            .OrderBy(l => l.LoadedDate)
            .ThenBy(l => l.Id)
            .First();

        var allocationRows = legs
            .SelectMany(l => l.Allocations.Count > 0
                ? l.Allocations.Select(a => new TransportFlowContractLegProjection
                {
                    Leg = l,
                    SourcePurchaseContractId = a.SourcePurchaseContractId,
                    ContractNumber = a.ContractNumber,
                    QuantityMt = a.QuantityMt,
                    IsLoaded = a.OutboundInventoryMovementId.HasValue || IsLoadedForTransportFlow(l)
                })
                :
                [
                    new TransportFlowContractLegProjection
                    {
                        Leg = l,
                        SourcePurchaseContractId = l.SourcePurchaseContractId,
                        ContractNumber = l.ContractNumber,
                        QuantityMt = l.QuantityMt,
                        IsLoaded = IsLoadedForTransportFlow(l)
                    }
                ])
            .ToList();

        var allocations = allocationRows
            .GroupBy(l => new { l.SourcePurchaseContractId, l.ContractNumber })
            .Select(g =>
            {
                var allocated = g.Sum(l => l.QuantityMt);
                var loadedByContract = g.Where(l => l.IsLoaded).Sum(l => l.QuantityMt);
                var receivedByContract = g.Sum(l => receiptTotals.TryGetValue(l.Leg.Id, out var totals) && l.Leg.QuantityMt > 0m
                    ? totals.ReceivedQuantityMt * l.QuantityMt / l.Leg.QuantityMt
                    : 0m);
                var shortageByContract = g.Sum(l => receiptTotals.TryGetValue(l.Leg.Id, out var totals) && l.Leg.QuantityMt > 0m
                    ? totals.ShortageQuantityMt * l.QuantityMt / l.Leg.QuantityMt
                    : 0m);
                return new InventoryTransportFlowContractAllocationViewModel
                {
                    ContractId = g.Key.SourcePurchaseContractId,
                    ContractNumber = string.IsNullOrWhiteSpace(g.Key.ContractNumber) ? $"#{g.Key.SourcePurchaseContractId}" : g.Key.ContractNumber,
                    AllocatedQuantityMt = allocated,
                    LoadedQuantityMt = loadedByContract,
                    PendingQuantityMt = allocated - loadedByContract,
                    ReceivedQuantityMt = receivedByContract,
                    ShortageQuantityMt = shortageByContract,
                    SharePercent = totalAllocated <= 0m ? 0m : decimal.Round(allocated / totalAllocated * 100m, 1),
                    LegCount = g.Select(l => l.Leg.Id).Distinct().Count()
                };
            })
            .OrderByDescending(a => a.AllocatedQuantityMt)
            .ThenBy(a => a.ContractNumber)
            .ToList();

        var legItems = legs
            .OrderBy(l => l.LoadedDate)
            .ThenBy(l => l.Id)
            .Select(l => new InventoryTransportFlowLegItemViewModel
            {
                Id = l.Id,
                ContractNumber = l.Allocations.Count > 0
                    ? string.Join("، ", l.Allocations
                        .Select(a => string.IsNullOrWhiteSpace(a.ContractNumber) ? $"#{a.SourcePurchaseContractId}" : a.ContractNumber)
                        .Distinct())
                    : string.IsNullOrWhiteSpace(l.ContractNumber) ? $"#{l.SourcePurchaseContractId}" : l.ContractNumber,
                WagonNumber = l.WagonNumber,
                RwbNo = l.RwbNo,
                BillOfLadingNumber = l.BillOfLadingNumber,
                PurchaseUnitCostUsd = l.PurchaseUnitCostUsd,
                QuantityMt = l.QuantityMt,
                LoadedQuantityMt = IsLoadedForTransportFlow(l) ? l.QuantityMt : 0m,
                LoadedDate = l.LoadedDate,
                Status = l.Status,
                OutboundInventoryMovementId = l.OutboundInventoryMovementId
            })
            .ToList();

        var expenseItems = legs
            .SelectMany(l => expensesByLeg.TryGetValue(l.Id, out var items) ? items : [])
            .OrderByDescending(e => e.ExpenseDate)
            .ThenByDescending(e => e.Id)
            .ToList();

        var expenseTotalsByContract = expenseItems
            .Where(e => e.ContractId.HasValue)
            .GroupBy(e => e.ContractId!.Value)
            .ToDictionary(
                g => g.Key,
                g => new
                {
                    Count = g.Count(),
                    AmountUsd = g.Sum(e => e.AmountUsd)
                });

        foreach (var allocation in allocations)
        {
            if (expenseTotalsByContract.TryGetValue(allocation.ContractId, out var expenseTotal))
            {
                allocation.ExpenseCount = expenseTotal.Count;
                allocation.ExpenseAmountUsd = expenseTotal.AmountUsd;
            }
        }

        return new InventoryTransportFlowTransportViewModel
        {
            GroupKey = groupKey,
            ShipmentId = firstLeg.ShipmentId,
            ShipmentCode = firstLeg.ShipmentCode,
            PrimaryReference = ResolvePrimaryReference(firstLeg),
            TransportTypeName = FormatTransportType(firstLeg.TransportType),
            ProductName = ResolveSingleOrMixed(legs.Select(l => l.ProductName), "چند محصول"),
            SourceLabel = ResolveSingleOrMixed(legs.Select(l => CombineLocation(l.SourceTerminalName, l.SourceTankCode, null)), "چند منبع"),
            DestinationLabel = ResolveSingleOrMixed(legs.Select(l => CombineLocation(l.DestinationTerminalName, l.DestinationTankCode, l.DestinationLocationName)), "مقصد بعداً تعیین می‌شود"),
            VehicleLabel = ResolveSingleOrMixed(legs.Select(l => l.WagonNumber), "چند وسیله"),
            RouteDescription = legs.Select(l => l.RouteDescription).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)),
            FirstLoadedDate = legs.Min(l => l.LoadedDate),
            ExpectedArrivalDate = legs.Where(l => l.ExpectedArrivalDate.HasValue).Select(l => l.ExpectedArrivalDate).OrderBy(d => d).FirstOrDefault(),
            TotalAllocatedQuantityMt = totalAllocated,
            LoadedQuantityMt = loaded,
            PendingQuantityMt = totalAllocated - loaded,
            ReceivedQuantityMt = received,
            ShortageQuantityMt = shortage,
            LegCount = legs.Count,
            ContractCount = allocations.Count,
            OutboundMovementCount = legs.Sum(l => l.Allocations.Count > 0
                ? l.Allocations.Count(a => a.OutboundInventoryMovementId.HasValue)
                : l.OutboundInventoryMovementId.HasValue ? 1 : 0),
            ExpenseCount = expenseItems.Count,
            TotalExpenseUsd = expenseItems.Sum(e => e.AmountUsd),
            StatusText = BuildTransportStatusText(legs),
            ProgressCssClass = BuildProgressCssClass(totalAllocated, loaded),
            ContractAllocations = allocations,
            Legs = legItems,
            Expenses = expenseItems
        };
    }

    private static bool IsLoadedForTransportFlow(TransportFlowLegProjection leg)
        => leg.Status is InventoryTransportLegStatus.Loaded
            or InventoryTransportLegStatus.InTransit
            or InventoryTransportLegStatus.Received
           || leg.OutboundInventoryMovementId.HasValue;

    private static string BuildTransportGroupKey(TransportFlowLegProjection leg)
    {
        if (!string.IsNullOrWhiteSpace(leg.TransportGroupKey))
        {
            return leg.TransportGroupKey.Trim();
        }

        if (leg.ShipmentId.HasValue)
        {
            return $"SHIP:{leg.ShipmentId.Value}";
        }

        if (!string.IsNullOrWhiteSpace(leg.WagonNumber))
        {
            return $"VEH:{(int)leg.TransportType}:{NormalizeTransportResourceKey(leg.WagonNumber)}";
        }

        if (!string.IsNullOrWhiteSpace(leg.BillOfLadingNumber))
        {
            return $"BL:{NormalizeTransportResourceKey(leg.BillOfLadingNumber)}";
        }

        if (!string.IsNullOrWhiteSpace(leg.RwbNo))
        {
            return $"RWB:{NormalizeTransportResourceKey(leg.RwbNo)}";
        }

        return $"LEG:{leg.Id}";
    }

    private static string ResolvePrimaryReference(TransportFlowLegProjection leg)
        => !string.IsNullOrWhiteSpace(leg.ShipmentCode)
            ? leg.ShipmentCode.Trim()
            : !string.IsNullOrWhiteSpace(leg.WagonNumber)
            ? leg.WagonNumber.Trim()
            : !string.IsNullOrWhiteSpace(leg.BillOfLadingNumber)
            ? leg.BillOfLadingNumber.Trim()
            : !string.IsNullOrWhiteSpace(leg.RwbNo)
                ? leg.RwbNo.Trim()
                : $"#{leg.Id}";

    private static string NormalizeTransportResourceKey(string value)
        => string.Join(" ", value.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();

    private static string ResolveSingleOrMixed(IEnumerable<string?> values, string fallback)
    {
        var distinct = values
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return distinct.Count switch
        {
            0 => fallback,
            1 => distinct[0],
            _ => fallback
        };
    }

    private static string CombineLocation(string? terminalName, string? tankCode, string? locationName)
    {
        var parts = new[] { terminalName, tankCode, locationName }
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v!.Trim())
            .ToList();

        return parts.Count == 0 ? "" : string.Join(" / ", parts);
    }

    private static string BuildTransportStatusText(IReadOnlyList<TransportFlowLegProjection> legs)
    {
        var loadedCount = legs.Count(IsLoadedForTransportFlow);
        if (loadedCount == 0)
        {
            return "ثبت‌شده، هنوز خروج موجودی ندارد";
        }

        if (loadedCount == legs.Count)
        {
            return "بارگیری شده و در جریان";
        }

        return "بخشی بارگیری شده";
    }

    private static string BuildProgressCssClass(decimal totalAllocated, decimal loaded)
    {
        if (totalAllocated <= 0m || loaded <= 0m)
        {
            return "progress-0";
        }

        var percent = loaded / totalAllocated * 100m;
        if (percent >= 100m) return "progress-100";
        if (percent >= 75m) return "progress-75";
        if (percent >= 50m) return "progress-50";
        if (percent >= 25m) return "progress-25";
        return "progress-10";
    }

    private static string BuildTransportGroupKey(InventoryTransportLeg leg)
    {
        // کلید گروهِ مستقل (در صورت وجود) همیشه مرجع است؛ legهای جدید با این کلید گروه می‌شوند.
        if (!string.IsNullOrWhiteSpace(leg.TransportGroupKey))
        {
            return leg.TransportGroupKey.Trim();
        }

        if (leg.ShipmentId.HasValue)
        {
            return $"SHIP:{leg.ShipmentId.Value}";
        }

        if (!string.IsNullOrWhiteSpace(leg.WagonNumber))
        {
            return $"VEH:{(int)leg.TransportType}:{NormalizeTransportResourceKey(leg.WagonNumber)}";
        }

        if (!string.IsNullOrWhiteSpace(leg.BillOfLadingNumber))
        {
            return $"BL:{NormalizeTransportResourceKey(leg.BillOfLadingNumber)}";
        }

        if (!string.IsNullOrWhiteSpace(leg.RwbNo))
        {
            return $"RWB:{NormalizeTransportResourceKey(leg.RwbNo)}";
        }

        return $"LEG:{leg.Id}";
    }

    private static string FormatTransportType(LoadingTransportType transportType)
        => transportType switch
        {
            LoadingTransportType.Wagon => "واگن",
            LoadingTransportType.Truck => "موتر",
            LoadingTransportType.Vessel => "کشتی",
            _ => "حمل"
        };

    private async Task<List<InventoryTransportLeg>> LoadActiveTransportGroupLegsAsync(string groupKey)
    {
        var activeStatuses = new[]
        {
            InventoryTransportLegStatus.Draft,
            InventoryTransportLegStatus.Loaded,
            InventoryTransportLegStatus.InTransit
        };

        var normalizedGroupKey = groupKey.Trim();
        var query = _db.InventoryTransportLegs
            .Include(l => l.SourcePurchaseContract)
            .Include(l => l.SourceStorageTank)
            .Include(l => l.Shipment)
            .Where(l => activeStatuses.Contains(l.Status));

        if (normalizedGroupKey.StartsWith("ITG:", StringComparison.OrdinalIgnoreCase))
        {
            query = query.Where(l => l.TransportGroupKey == normalizedGroupKey);
        }

        var candidates = await query.ToListAsync();

        return candidates
            .Where(l => string.Equals(BuildTransportGroupKey(l), normalizedGroupKey, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private async Task<List<InventoryTransportLeg>> LoadTransportGroupLegsForReceiptAsync(string groupKey)
    {
        var activeStatuses = new[]
        {
            InventoryTransportLegStatus.Draft,
            InventoryTransportLegStatus.Loaded,
            InventoryTransportLegStatus.InTransit,
            InventoryTransportLegStatus.Received
        };

        var normalizedGroupKey = groupKey.Trim();
        var query = _db.InventoryTransportLegs
            .Include(l => l.SourcePurchaseContract)
            .Include(l => l.Product)
            .Include(l => l.Shipment)
                .ThenInclude(s => s!.Vessel)
            .Include(l => l.DestinationTerminal)
            .Include(l => l.DestinationStorageTank)
            .Where(l => activeStatuses.Contains(l.Status));

        if (normalizedGroupKey.StartsWith("ITG:", StringComparison.OrdinalIgnoreCase))
        {
            query = query.Where(l => l.TransportGroupKey == normalizedGroupKey);
        }

        var candidates = await query.ToListAsync();

        return candidates
            .Where(l => string.Equals(BuildTransportGroupKey(l), normalizedGroupKey, StringComparison.OrdinalIgnoreCase))
            .OrderBy(l => l.SourcePurchaseContractId)
            .ThenBy(l => l.LoadedDate)
            .ThenBy(l => l.Id)
            .ToList();
    }

    private static InventoryTransportGroupReceiptCreateViewModel BuildGroupReceiptCreateModel(
        IReadOnlyList<InventoryTransportLeg> legs,
        string? returnUrl)
    {
        var model = new InventoryTransportGroupReceiptCreateViewModel
        {
            ReceiptDate = AfghanistanBusinessClock.SystemToday,
            ReturnUrl = returnUrl
        };

        if (legs.Count > 0)
        {
            model.DestinationTerminalId = ResolveSingleValueOrNull(legs.Select(l => l.DestinationTerminalId));
            model.DestinationStorageTankId = ResolveSingleValueOrNull(legs.Select(l => l.DestinationStorageTankId));
        }

        RefreshGroupReceiptCreateModel(model, legs);
        model.TotalShortageQuantityMt = 0m;
        model.ChargeableShortageMt = 0m;
        model.Allocations = BuildGroupReceiptAllocationPreview(legs, model.TotalShortageQuantityMt);
        return model;
    }

    private static void RefreshGroupReceiptCreateModel(
        InventoryTransportGroupReceiptCreateViewModel model,
        IReadOnlyList<InventoryTransportLeg> legs)
    {
        if (legs.Count == 0)
        {
            model.Allocations = [];
            model.TotalLoadedQuantityMt = 0m;
            return;
        }

        model.GroupKey = BuildTransportGroupKey(legs[0]);
        model.TransportReference = ResolveGroupExpenseTransportReference(legs);
        model.ShipmentId = legs.Select(l => l.ShipmentId).Distinct().Count() == 1
            ? legs[0].ShipmentId
            : null;
        model.ShipmentName = legs
            .Select(l => l.Shipment?.Vessel?.Name ?? l.Shipment?.ShipmentCode)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
            ?? model.TransportReference;
        var productNames = legs
            .Select(l => l.Product?.Name)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        model.ProductName = productNames.Count == 1 ? productNames[0]! : "چند محصول";
        model.Allocations = BuildGroupReceiptAllocationPreview(legs, model.TotalShortageQuantityMt);
    }

    private static IReadOnlyList<InventoryTransportGroupReceiptAllocationPreviewViewModel> BuildGroupReceiptAllocationPreview(
        IReadOnlyList<InventoryTransportLeg> legs,
        decimal totalShortageQuantityMt)
    {
        if (legs.Count == 0)
        {
            return [];
        }

        var totalLoaded = legs.Sum(l => l.QuantityMt);
        var orderedLegs = legs
            .OrderBy(l => l.SourcePurchaseContractId)
            .ThenBy(l => l.LoadedDate)
            .ThenBy(l => l.Id)
            .ToList();
        var shortageByLeg = AllocateRoundedByWeight(
            Math.Min(Math.Max(totalShortageQuantityMt, 0m), totalLoaded),
            orderedLegs.Select(l => l.QuantityMt).ToList());

        return orderedLegs
            .Select((leg, index) => new
            {
                Leg = leg,
                ShortageQuantityMt = shortageByLeg[index],
                ReceivedQuantityMt = decimal.Round(leg.QuantityMt - shortageByLeg[index], 4, MidpointRounding.AwayFromZero)
            })
            .GroupBy(x => new
            {
                x.Leg.SourcePurchaseContractId,
                ContractNumber = x.Leg.SourcePurchaseContract?.ContractNumber ?? ""
            })
            .Select(g => new InventoryTransportGroupReceiptAllocationPreviewViewModel
            {
                ContractId = g.Key.SourcePurchaseContractId,
                ContractNumber = string.IsNullOrWhiteSpace(g.Key.ContractNumber) ? $"#{g.Key.SourcePurchaseContractId}" : g.Key.ContractNumber,
                LoadedQuantityMt = g.Sum(x => x.Leg.QuantityMt),
                ReceivedQuantityMt = g.Sum(x => x.ReceivedQuantityMt),
                ShortageQuantityMt = g.Sum(x => x.ShortageQuantityMt),
                SharePercent = CalculateSharePercent(g.Sum(x => x.Leg.QuantityMt), totalLoaded),
                LegCount = g.Count()
            })
            .OrderByDescending(a => a.LoadedQuantityMt)
            .ThenBy(a => a.ContractNumber)
            .ToList();
    }

    private async Task<GroupReceiptAvailability> BuildGroupReceiptAvailabilityAsync(
        IReadOnlyList<InventoryTransportLeg> legs)
    {
        if (legs.Count == 0)
        {
            return GroupReceiptAvailability.Empty;
        }

        var loadedLegs = legs
            .Where(l => l.Status is InventoryTransportLegStatus.Loaded
                or InventoryTransportLegStatus.InTransit
                or InventoryTransportLegStatus.Received)
            .OrderBy(l => l.SourcePurchaseContractId)
            .ThenBy(l => l.LoadedDate)
            .ThenBy(l => l.Id)
            .ToList();
        var legIds = loadedLegs.Select(l => l.Id).ToList();
        var availableByLeg = loadedLegs.ToDictionary(l => l.Id, l => l.QuantityMt);

        var receipts = await _db.InventoryTransportReceipts
            .AsNoTracking()
            .Where(r => legIds.Contains(r.InventoryTransportLegId) && !r.IsCancelled)
            .Select(r => new
            {
                r.InventoryTransportLegId,
                r.ReceivedQuantityMt,
                r.ShortageQuantityMt,
                r.SalesTransactionId
            })
            .ToListAsync();

        foreach (var receipt in receipts)
        {
            availableByLeg[receipt.InventoryTransportLegId] = decimal.Round(
                Math.Max(
                    availableByLeg.GetValueOrDefault(receipt.InventoryTransportLegId)
                    - receipt.ReceivedQuantityMt
                    - receipt.ShortageQuantityMt,
                    0m),
                4,
                MidpointRounding.AwayFromZero);
        }

        var shipmentIds = loadedLegs
            .Where(l => l.ShipmentId.HasValue)
            .Select(l => l.ShipmentId!.Value)
            .Distinct()
            .ToList();
        var directLossQuantityMt = 0m;
        var previousSalesQuantityMt = 0m;

        if (shipmentIds.Count == 1)
        {
            var shipmentId = shipmentIds[0];
            var directLosses = await _db.LossEvents
                .AsNoTracking()
                .Where(l => l.ShipmentId == shipmentId
                    && !l.IsCancelled
                    && l.TransportLegId == null
                    && l.LoadingRegisterId == null
                    && l.LoadingReceiptId == null
                    && l.TruckDispatchId == null
                    && l.SalesTransactionId == null
                    && l.DifferenceQuantityMt > 0m)
                .Select(l => new { l.ContractId, QuantityMt = l.DifferenceQuantityMt })
                .ToListAsync();

            directLossQuantityMt = directLosses.Sum(l => l.QuantityMt);
            foreach (var loss in directLosses)
            {
                var matchingLegIds = loss.ContractId.HasValue
                    ? loadedLegs
                        .Where(l => l.SourcePurchaseContractId == loss.ContractId.Value)
                        .Select(l => l.Id)
                        .ToList()
                    : legIds;
                var applied = ConsumeGroupReceiptCapacity(
                    loss.QuantityMt,
                    matchingLegIds,
                    availableByLeg);
                var remainder = decimal.Round(
                    Math.Max(loss.QuantityMt - applied, 0m),
                    4,
                    MidpointRounding.AwayFromZero);
                if (remainder > 0m)
                {
                    ConsumeGroupReceiptCapacity(remainder, legIds, availableByLeg);
                }
            }

            var sales = await _db.SalesTransactions
                .AsNoTracking()
                .Where(s => s.ShipmentId == shipmentId
                    && !s.IsCancelled
                    && s.SaleStage != SaleStage.PreSale)
                .Select(s => new { s.Id, s.QuantityMt })
                .ToListAsync();
            previousSalesQuantityMt = sales.Sum(s => s.QuantityMt);
            var receiptSaleIds = receipts
                .Where(r => r.SalesTransactionId.HasValue)
                .Select(r => r.SalesTransactionId!.Value)
                .ToHashSet();
            var standaloneSalesQuantityMt = sales
                .Where(s => !receiptSaleIds.Contains(s.Id))
                .Sum(s => s.QuantityMt);
            ConsumeGroupReceiptCapacity(standaloneSalesQuantityMt, legIds, availableByLeg);
        }

        var receiptShortageQuantityMt = receipts.Sum(r => r.ShortageQuantityMt);
        return new GroupReceiptAvailability(
            TotalLoadedQuantityMt: decimal.Round(loadedLegs.Sum(l => l.QuantityMt), 4, MidpointRounding.AwayFromZero),
            RegisteredShortageQuantityMt: decimal.Round(receiptShortageQuantityMt + directLossQuantityMt, 4, MidpointRounding.AwayFromZero),
            PreviousSalesQuantityMt: decimal.Round(previousSalesQuantityMt, 4, MidpointRounding.AwayFromZero),
            PreviousReceiptQuantityMt: decimal.Round(
                receipts.Where(r => !r.SalesTransactionId.HasValue).Sum(r => r.ReceivedQuantityMt),
                4,
                MidpointRounding.AwayFromZero),
            AvailableByLeg: availableByLeg);
    }

    private static decimal ConsumeGroupReceiptCapacity(
        decimal quantityMt,
        IReadOnlyCollection<int> candidateLegIds,
        IDictionary<int, decimal> availableByLeg)
    {
        var legIds = candidateLegIds
            .Where(id => availableByLeg.TryGetValue(id, out var available) && available > 0m)
            .Distinct()
            .ToList();
        var availableQuantityMt = legIds.Sum(id => availableByLeg[id]);
        var quantityToApplyMt = decimal.Round(
            Math.Min(Math.Max(quantityMt, 0m), availableQuantityMt),
            4,
            MidpointRounding.AwayFromZero);
        if (quantityToApplyMt <= 0m || legIds.Count == 0)
        {
            return 0m;
        }

        var allocations = AllocateRoundedByWeight(
            quantityToApplyMt,
            legIds.Select(id => availableByLeg[id]).ToList());
        for (var i = 0; i < legIds.Count; i++)
        {
            availableByLeg[legIds[i]] = decimal.Round(
                Math.Max(availableByLeg[legIds[i]] - allocations[i], 0m),
                4,
                MidpointRounding.AwayFromZero);
        }

        return allocations.Sum();
    }

    private static void ApplyGroupReceiptAvailability(
        InventoryTransportGroupReceiptCreateViewModel model,
        GroupReceiptAvailability availability)
    {
        model.TotalLoadedQuantityMt = availability.TotalLoadedQuantityMt;
        model.RegisteredShortageQuantityMt = availability.RegisteredShortageQuantityMt;
        model.PreviousSalesQuantityMt = availability.PreviousSalesQuantityMt;
        model.PreviousReceiptQuantityMt = availability.PreviousReceiptQuantityMt;
        model.AvailableQuantityMt = availability.AvailableQuantityMt;
    }

    private sealed record GroupReceiptAvailability(
        decimal TotalLoadedQuantityMt,
        decimal RegisteredShortageQuantityMt,
        decimal PreviousSalesQuantityMt,
        decimal PreviousReceiptQuantityMt,
        IReadOnlyDictionary<int, decimal> AvailableByLeg)
    {
        public static GroupReceiptAvailability Empty { get; } = new(0m, 0m, 0m, 0m, new Dictionary<int, decimal>());
        public decimal AvailableQuantityMt => decimal.Round(
            AvailableByLeg.Values.Sum(),
            4,
            MidpointRounding.AwayFromZero);
    }

    private async Task ValidateGroupReceiptAsync(
        InventoryTransportGroupReceiptCreateViewModel model,
        IReadOnlyList<InventoryTransportLeg> legs,
        GroupReceiptAvailability availability)
    {
        if (legs.Count == 0)
        {
            ModelState.AddModelError(nameof(model.GroupKey), "گروپ حمل انتخاب‌شده معتبر نیست یا دیگر در جریان نیست.");
            return;
        }

        if (legs.Any(l => l.Status == InventoryTransportLegStatus.Draft))
        {
            ModelState.AddModelError(nameof(model.GroupKey), "قبل از ثبت رسید کلی، همه تخصیص‌های این حمل باید بارگیری شده باشند.");
        }

        if (legs.Any(l => l.Status is not (InventoryTransportLegStatus.Loaded or InventoryTransportLegStatus.InTransit)))
        {
            ModelState.AddModelError(nameof(model.GroupKey), "فقط حمل‌های Loaded یا InTransit می‌توانند رسید کلی بگیرند.");
        }

        var productIds = legs.Select(l => l.ProductId).Distinct().ToList();
        if (productIds.Count > 1)
        {
            ModelState.AddModelError(nameof(model.GroupKey), "رسید کلی فقط برای حمل‌های یک‌محصوله قابل ثبت است.");
        }

        var totalLoadedQuantityMt = availability.TotalLoadedQuantityMt;
        if (totalLoadedQuantityMt <= 0m)
        {
            ModelState.AddModelError(nameof(model.GroupKey), "این حمل مقدار بارگیری‌شده معتبر ندارد.");
        }

        if (model.TotalReceivedQuantityMt <= 0m)
        {
            ModelState.AddModelError(nameof(model.TotalReceivedQuantityMt), "مقدار تخلیه‌شده باید بزرگ‌تر از صفر باشد.");
        }

        if (model.TotalShortageQuantityMt < 0m)
        {
            ModelState.AddModelError(nameof(model.TotalShortageQuantityMt), "ضایعات نمی‌تواند منفی باشد.");
        }

        var requestedQuantityMt = decimal.Round(
            model.TotalReceivedQuantityMt + model.TotalShortageQuantityMt,
            4,
            MidpointRounding.AwayFromZero);
        if (requestedQuantityMt > availability.AvailableQuantityMt + 0.0001m)
        {
            ModelState.AddModelError(
                nameof(model.TotalReceivedQuantityMt),
                $"مقدار رسید نمی‌تواند از مقدار قابل رسید ({availability.AvailableQuantityMt:N4} تن) بیشتر باشد.");
        }

        if ((model.AllowanceMt ?? 0m) > model.TotalShortageQuantityMt)
        {
            ModelState.AddModelError(nameof(model.AllowanceMt), "تلورانس مجاز نمی‌تواند از کل ضایعات بیشتر باشد.");
        }

        if (!model.DestinationTerminalId.HasValue)
        {
            ModelState.AddModelError(nameof(model.DestinationTerminalId), "ترمینال مقصد برای تخلیه الزامی است.");
            return;
        }

        var terminalExists = await _db.Terminals
            .AsNoTracking()
            .AnyAsync(t => t.Id == model.DestinationTerminalId.Value);
        if (!terminalExists)
        {
            ModelState.AddModelError(nameof(model.DestinationTerminalId), "ترمینال مقصد معتبر نیست.");
        }

        if (model.DestinationStorageTankId.HasValue)
        {
            var tank = await _db.StorageTanks
                .AsNoTracking()
                .FirstOrDefaultAsync(t => t.Id == model.DestinationStorageTankId.Value);
            if (tank is null)
            {
                ModelState.AddModelError(nameof(model.DestinationStorageTankId), "مخزن مقصد معتبر نیست.");
            }
            else
            {
                if (tank.TerminalId != model.DestinationTerminalId.Value)
                {
                    ModelState.AddModelError(nameof(model.DestinationStorageTankId), "مخزن مقصد باید مربوط به همان ترمینال مقصد باشد.");
                }

                var productId = productIds.Count == 1 ? productIds[0] : 0;
                if (tank.ProductId.HasValue && productId > 0 && tank.ProductId.Value != productId)
                {
                    ModelState.AddModelError(nameof(model.DestinationStorageTankId), "محصول مخزن مقصد با محصول حمل هماهنگ نیست.");
                }
            }
        }
    }

    private async Task PopulateGroupReceiptLookupsAsync(InventoryTransportGroupReceiptCreateViewModel model)
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
    }

    private static string? BuildGroupReceiptNotes(
        InventoryTransportGroupReceiptCreateViewModel model,
        string normalizedGroupKey,
        decimal totalLoadedQuantityMt)
    {
        var parts = new List<string>
        {
            $"Group receipt: {normalizedGroupKey}",
            $"Total loaded: {totalLoadedQuantityMt:N4} MT",
            $"Total received: {model.TotalReceivedQuantityMt:N4} MT",
            $"Total shortage: {model.TotalShortageQuantityMt:N4} MT"
        };

        if (!string.IsNullOrWhiteSpace(model.Notes))
        {
            parts.Insert(0, model.Notes.Trim());
        }

        if (!string.IsNullOrWhiteSpace(model.DocumentReference))
        {
            parts.Insert(0, $"Reference: {model.DocumentReference.Trim()}");
        }

        var notes = string.Join(" | ", parts);
        return notes.Length <= 1000 ? notes : notes[..1000];
    }

    private static InventoryTransportGroupExpenseCreateViewModel BuildGroupExpenseCreateModel(
        IReadOnlyList<InventoryTransportLeg> legs,
        string? returnUrl)
    {
        var model = new InventoryTransportGroupExpenseCreateViewModel
        {
            ExpenseDate = AfghanistanBusinessClock.SystemToday,
            Currency = SystemCurrency.BaseCurrencyCode,
            AppliedFxRateToUsd = 1m,
            ReturnUrl = returnUrl
        };

        RefreshGroupExpenseCreateModel(model, legs);
        return model;
    }

    private static void RefreshGroupExpenseCreateModel(
        InventoryTransportGroupExpenseCreateViewModel model,
        IReadOnlyList<InventoryTransportLeg> legs)
    {
        if (legs.Count == 0)
        {
            model.Allocations = [];
            model.TotalAllocatedQuantityMt = 0m;
            return;
        }

        model.GroupKey = BuildTransportGroupKey(legs[0]);
        model.TransportReference = ResolveGroupExpenseTransportReference(legs);
        model.TotalAllocatedQuantityMt = legs.Sum(l => l.QuantityMt);
        model.Allocations = BuildGroupExpenseAllocationPreview(legs, model.Amount);
    }

    private static IReadOnlyList<InventoryTransportGroupExpenseAllocationPreviewViewModel> BuildGroupExpenseAllocationPreview(
        IReadOnlyList<InventoryTransportLeg> legs,
        decimal sourceAmount)
    {
        var totalQuantityMt = legs.Sum(l => l.QuantityMt);
        var groups = legs
            .GroupBy(l => new
            {
                l.SourcePurchaseContractId,
                ContractNumber = l.SourcePurchaseContract != null ? l.SourcePurchaseContract.ContractNumber : ""
            })
            .Select(g => new
            {
                ContractId = g.Key.SourcePurchaseContractId,
                ContractNumber = string.IsNullOrWhiteSpace(g.Key.ContractNumber) ? $"#{g.Key.SourcePurchaseContractId}" : g.Key.ContractNumber,
                QuantityMt = g.Sum(l => l.QuantityMt),
                LegCount = g.Count()
            })
            .OrderByDescending(g => g.QuantityMt)
            .ThenBy(g => g.ContractNumber)
            .ToList();

        var allocatedAmounts = sourceAmount > 0m
            ? AllocateRoundedByWeight(sourceAmount, groups.Select(g => g.QuantityMt).ToList())
            : groups.Select(_ => 0m).ToList();

        return groups
            .Select((g, index) => new InventoryTransportGroupExpenseAllocationPreviewViewModel
            {
                ContractId = g.ContractId,
                ContractNumber = g.ContractNumber,
                AllocatedQuantityMt = g.QuantityMt,
                SharePercent = CalculateSharePercent(g.QuantityMt, totalQuantityMt),
                LegCount = g.LegCount,
                AllocatedAmount = allocatedAmounts[index]
            })
            .ToList();
    }

    private static string ResolveGroupExpenseTransportReference(IReadOnlyList<InventoryTransportLeg> legs)
    {
        var firstLeg = legs
            .OrderBy(l => l.LoadedDate)
            .ThenBy(l => l.Id)
            .First();

        if (!string.IsNullOrWhiteSpace(firstLeg.Shipment?.ShipmentCode))
        {
            return firstLeg.Shipment.ShipmentCode.Trim();
        }

        if (!string.IsNullOrWhiteSpace(firstLeg.WagonNumber))
        {
            return firstLeg.WagonNumber.Trim();
        }

        if (!string.IsNullOrWhiteSpace(firstLeg.BillOfLadingNumber))
        {
            return firstLeg.BillOfLadingNumber.Trim();
        }

        if (!string.IsNullOrWhiteSpace(firstLeg.RwbNo))
        {
            return firstLeg.RwbNo.Trim();
        }

        return $"#{firstLeg.Id}";
    }

    private static decimal CalculateSharePercent(decimal quantityMt, decimal totalQuantityMt)
        => totalQuantityMt <= 0m
            ? 0m
            : decimal.Round(quantityMt / totalQuantityMt * 100m, 4, MidpointRounding.AwayFromZero);

    private static List<decimal> AllocateRoundedByWeight(decimal totalAmount, IReadOnlyList<decimal> weights)
    {
        if (weights.Count == 0)
        {
            return [];
        }

        var allocations = new List<decimal>(weights.Count);
        var totalWeight = weights.Sum();
        if (totalAmount <= 0m || totalWeight <= 0m)
        {
            allocations.AddRange(weights.Select(_ => 0m));
            return allocations;
        }

        var allocated = 0m;
        for (var i = 0; i < weights.Count; i++)
        {
            var amount = i == weights.Count - 1
                ? decimal.Round(totalAmount - allocated, 4, MidpointRounding.AwayFromZero)
                : decimal.Round(totalAmount * weights[i] / totalWeight, 4, MidpointRounding.AwayFromZero);

            allocations.Add(amount);
            allocated += amount;
        }

        return allocations;
    }

    private static int? ResolveSingleValueOrNull(IEnumerable<int?> values)
    {
        var distinct = values
            .Where(v => v.HasValue)
            .Select(v => v!.Value)
            .Distinct()
            .Take(2)
            .ToList();

        return distinct.Count == 1 ? distinct[0] : null;
    }

    private static bool QuantitiesMatch(decimal left, decimal right)
        => decimal.Abs(left - right) <= 0.0001m;

    private static string BuildGroupExpenseDescription(
        InventoryTransportGroupExpenseCreateViewModel model,
        InventoryTransportLeg leg,
        string normalizedGroupKey,
        CurrencyConversionResult conversion,
        decimal totalAmountUsd,
        decimal sharePercent)
    {
        var contractNumber = leg.SourcePurchaseContract?.ContractNumber ?? $"#{leg.SourcePurchaseContractId}";
        var reference = string.IsNullOrWhiteSpace(model.TransportReference)
            ? normalizedGroupKey
            : model.TransportReference.Trim();

        var description = string.Join(" | ", new[]
        {
            model.Description.Trim(),
            $"Transport group: {reference}",
            $"GroupKey: {normalizedGroupKey}",
            $"Original total: {model.Amount:N4} {conversion.SourceCurrencyCode}",
            $"Original total USD: {totalAmountUsd:N4}",
            $"Contract: {contractNumber}",
            $"Leg: #{leg.Id}",
            $"Quantity: {leg.QuantityMt:N4} MT",
            $"Share: {sharePercent:N4}%"
        });

        return description.Length <= 1000 ? description : description[..1000];
    }

    private static string BuildLedgerDescription(ExpenseType expenseType, ExpenseTransaction expense)
    {
        var baseText = $"ثبت هزینه {(expenseType.NamePersian ?? expenseType.Name)}";
        if (string.IsNullOrWhiteSpace(expense.Description))
        {
            return baseText;
        }

        return $"{baseText} - {expense.Description}";
    }

    private static string BuildLedgerReference(ExpenseType expenseType, ExpenseTransaction expense)
    {
        var prefix = string.IsNullOrWhiteSpace(expenseType.Code)
            ? $"EXP-{expense.Id}"
            : $"{expenseType.Code}-{expense.Id}";

        if (string.IsNullOrWhiteSpace(expense.Description))
        {
            return prefix;
        }

        var combined = $"{prefix} | {expense.Description.Trim()}";
        return combined.Length <= 200 ? combined : combined[..200];
    }

    // پیشوند ITG: تا در SQL بشود ارزان پیش‌فیلتر کرد و از کلیدهای heuristic قدیمی (SHIP/VEH/BL/RWB/LEG) متمایز بماند.





    private async Task ValidateOperationalPartyAsync(int? serviceProviderId, int? operationalAssetId)
    {
        if (serviceProviderId.HasValue && operationalAssetId.HasValue)
        {
            ModelState.AddModelError(nameof(InventoryTransportLegCreateViewModel.OperationalAssetId), "Select either a service provider or an operational asset, not both.");
        }

        if (serviceProviderId.HasValue
            && !await _db.ServiceProviders.AsNoTracking().AnyAsync(p => p.Id == serviceProviderId.Value && p.IsActive))
        {
            ModelState.AddModelError(nameof(InventoryTransportLegCreateViewModel.ServiceProviderId), "Service provider selection is invalid.");
        }

        if (operationalAssetId.HasValue
            && !await _db.OperationalAssets.AsNoTracking().AnyAsync(a => a.Id == operationalAssetId.Value && a.IsActive))
        {
            ModelState.AddModelError(nameof(InventoryTransportLegCreateViewModel.OperationalAssetId), "Operational asset selection is invalid.");
        }
    }



    private async Task<IReadOnlySet<int>?> BuildAllowedShipmentContractIdsAsync(int? shipmentId)
    {
        if (!shipmentId.HasValue)
        {
            return null;
        }

        var shipmentExists = await _db.Shipments
            .AsNoTracking()
            .AnyAsync(s => s.Id == shipmentId.Value);

        if (!shipmentExists)
        {
            return null;
        }

        var contractIds = await _db.ShipmentContracts
            .AsNoTracking()
            .Where(sc => sc.ShipmentId == shipmentId.Value)
            .Select(sc => sc.ContractId)
            .ToListAsync();

        return contractIds.Count == 0
            ? null
            : contractIds.ToHashSet();
    }

    private async Task ValidateCreateAsync(InventoryTransportLegCreateViewModel model)
    {
        if (model.LoadedDate == default)
        {
            ModelState.AddModelError(nameof(model.LoadedDate), "Loaded date is required.");
        }

        if (model.QuantityMt <= 0m)
        {
            ModelState.AddModelError(nameof(model.QuantityMt), "Quantity must be greater than zero.");
        }

        if (model.ChargeableQuantityMt < 0m)
        {
            ModelState.AddModelError(nameof(model.ChargeableQuantityMt), "Chargeable quantity cannot be negative.");
        }

        if (model.PurchaseUnitCostUsd.HasValue && model.PurchaseUnitCostUsd.Value <= 0m)
        {
            ModelState.AddModelError(nameof(model.PurchaseUnitCostUsd), "Purchase unit cost must be greater than zero.");
        }

        if (model.TransportType == LoadingTransportType.Unspecified)
        {
            ModelState.AddModelError(nameof(model.TransportType), "Transport type is required.");
        }

        var allowedShipmentContractIds = await BuildAllowedShipmentContractIdsAsync(model.ShipmentId);
        if (model.ShipmentId.HasValue
            && !await _db.Shipments.AsNoTracking().AnyAsync(s => s.Id == model.ShipmentId.Value))
        {
            ModelState.AddModelError(nameof(model.ShipmentId), "Shipment was not found.");
        }

        var contract = await _db.Contracts
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == model.SourcePurchaseContractId);
        if (contract is null)
        {
            ModelState.AddModelError(nameof(model.SourcePurchaseContractId), "Source purchase contract was not found.");
        }
        else if (contract.ContractType != ContractType.Purchase)
        {
            ModelState.AddModelError(nameof(model.SourcePurchaseContractId), "Source contract must be a purchase contract.");
        }
        else if (allowedShipmentContractIds is not null && !allowedShipmentContractIds.Contains(contract.Id))
        {
            ModelState.AddModelError(nameof(model.SourcePurchaseContractId), "Source purchase contract is not linked to the selected shipment.");
        }
        else if (contract.ProductId != model.ProductId)
        {
            ModelState.AddModelError(nameof(model.ProductId), "Product must match the source purchase contract product.");
        }

        if (!await _db.Products.AsNoTracking().AnyAsync(p => p.Id == model.ProductId))
        {
            ModelState.AddModelError(nameof(model.ProductId), "Product was not found.");
        }

        if (!await _db.Terminals.AsNoTracking().AnyAsync(t => t.Id == model.SourceTerminalId))
        {
            ModelState.AddModelError(nameof(model.SourceTerminalId), "Source terminal was not found.");
        }

        if (model.SourceStorageTankId.HasValue)
        {
            var sourceTank = await _db.StorageTanks
                .AsNoTracking()
                .FirstOrDefaultAsync(t => t.Id == model.SourceStorageTankId.Value);
            if (sourceTank is null)
            {
                ModelState.AddModelError(nameof(model.SourceStorageTankId), "Source tank was not found.");
            }
            else
            {
                if (sourceTank.TerminalId != model.SourceTerminalId)
                {
                    ModelState.AddModelError(nameof(model.SourceStorageTankId), "Source tank must belong to the selected source terminal.");
                }

                if (sourceTank.ProductId.HasValue && sourceTank.ProductId.Value != model.ProductId)
                {
                    ModelState.AddModelError(nameof(model.SourceStorageTankId), "Source tank product does not match the selected product.");
                }
            }
        }

        if (model.DestinationStorageTankId.HasValue)
        {
            var destinationTank = await _db.StorageTanks
                .AsNoTracking()
                .FirstOrDefaultAsync(t => t.Id == model.DestinationStorageTankId.Value);
            if (destinationTank is null)
            {
                ModelState.AddModelError(nameof(model.DestinationStorageTankId), "Destination tank was not found.");
            }
            else if (model.DestinationTerminalId.HasValue
                     && destinationTank.TerminalId != model.DestinationTerminalId.Value)
            {
                ModelState.AddModelError(nameof(model.DestinationStorageTankId), "Destination tank must belong to the selected destination terminal.");
            }
        }

        await ValidateOperationalPartyAsync(model.ServiceProviderId, model.OperationalAssetId);
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

    private async Task PopulateGroupExpenseLookupsAsync(InventoryTransportGroupExpenseCreateViewModel model)
    {
        // نوع مصرف استاندارد «کرایه حمل» را تضمین کن تا در فهرست انتخاب مودال موجود باشد.
        await new InventoryTransportReceiptService(_db, _currencyConversion, _lineage).EnsureTransportFreightExpenseTypeAsync();

        var expenseTypes = await _db.ExpenseTypes
            .AsNoTracking()
            .Where(e => e.IsActive)
            .OrderBy(e => e.Category)
            .ThenBy(e => e.Code)
            .Select(e => new
            {
                e.Id,
                Text = e.Code + " - " + (e.NamePersian ?? e.Name)
            })
            .ToListAsync();

        ViewBag.ExpenseTypes = new SelectList(expenseTypes, "Id", "Text", model.ExpenseTypeId);
        ViewBag.ExpenseTypeNames = await _db.ExpenseTypes
            .AsNoTracking()
            .Where(e => e.IsActive)
            .OrderBy(e => e.Name)
            .Select(e => e.NamePersian ?? e.Name)
            .Distinct()
            .ToListAsync();

        var serviceProviders = await _db.ServiceProviders
            .AsNoTracking()
            .Where(p => p.IsActive || (model.ServiceProviderId.HasValue && p.Id == model.ServiceProviderId.Value))
            .OrderBy(p => model.ServiceProviderId.HasValue && p.Id == model.ServiceProviderId.Value ? 0 : 1)
            .ThenBy(p => p.Name)
            .Select(p => new
            {
                p.Id,
                Text = string.IsNullOrWhiteSpace(p.Code) ? p.Name : p.Code + " - " + p.Name
            })
            .ToListAsync();

        ViewBag.ServiceProviders = new SelectList(serviceProviders, "Id", "Text", model.ServiceProviderId);

        var operationalAssets = await _db.OperationalAssets
            .AsNoTracking()
            .Where(a => a.IsActive || (model.OperationalAssetId.HasValue && a.Id == model.OperationalAssetId.Value))
            .OrderBy(a => model.OperationalAssetId.HasValue && a.Id == model.OperationalAssetId.Value ? 0 : 1)
            .ThenBy(a => a.AssetCode)
            .ThenBy(a => a.Name)
            .Select(a => new
            {
                a.Id,
                Text = a.AssetCode + " - " + a.Name
            })
            .ToListAsync();

        ViewBag.OperationalAssets = new SelectList(operationalAssets, "Id", "Text", model.OperationalAssetId);

        var currencyCodes = await _db.Currencies
            .AsNoTracking()
            .Where(c => c.IsActive)
            .OrderBy(c => c.Code)
            .Select(c => c.Code)
            .ToListAsync();

        if (currencyCodes.Count == 0)
        {
            currencyCodes = [SystemCurrency.BaseCurrencyCode, "AFN", "RUB"];
        }

        ViewBag.Currencies = new SelectList(
            currencyCodes.Select(code => new { Code = code }),
            "Code",
            "Code",
            model.Currency);
    }

    private async Task<ExpenseType?> FindExpenseTypeByManualNameAsync(string manualExpenseTypeName)
    {
        if (string.IsNullOrWhiteSpace(manualExpenseTypeName))
        {
            return null;
        }

        var normalizedName = manualExpenseTypeName.Trim();
        return await _db.ExpenseTypes
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.IsActive
                && (e.Name == normalizedName || e.NamePersian == normalizedName));
    }

    private async Task<string> BuildManualExpenseTypeCodeAsync(string manualExpenseTypeName)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(manualExpenseTypeName))).Substring(0, 8);
        var prefix = $"MAN-{hash}";
        var candidate = prefix;
        var suffix = 2;

        while (await _db.ExpenseTypes.AsNoTracking().AnyAsync(e => e.Code == candidate))
        {
            candidate = $"{prefix}-{suffix++}";
        }

        return candidate;
    }

    private async Task<IDbContextTransaction?> BeginTransactionIfSupportedAsync()
    {
        if (_db.Database.ProviderName == "Microsoft.EntityFrameworkCore.InMemory")
        {
            return null;
        }

        return await _db.Database.BeginTransactionAsync();
    }

    private async Task PopulateLookupsAsync()
    {
        ViewBag.Contracts = new SelectList(
            await _db.Contracts
                .AsNoTracking()
                .Where(c => c.ContractType == ContractType.Purchase)
                .OrderBy(c => c.ContractNumber)
                .Select(c => new { c.Id, Text = c.ContractNumber })
                .ToListAsync(),
            "Id",
            "Text");

        ViewBag.Products = new SelectList(
            await _db.Products
                .AsNoTracking()
                .OrderBy(p => p.Code)
                .Select(p => new { p.Id, Text = p.Code + " - " + p.Name })
                .ToListAsync(),
            "Id",
            "Text");

        ViewBag.SourceTerminals = new SelectList(
            await _db.Terminals
                .AsNoTracking()
                .OrderBy(t => t.Code)
                .Select(t => new { t.Id, Text = t.Code + " - " + t.Name })
                .ToListAsync(),
            "Id",
            "Text");

        ViewBag.SourceStorageTanks = new SelectList(
            await StorageTankDisplay.LoadOptionsAsync(_db.StorageTanks
                .AsNoTracking()
                .OrderBy(t => t.DisplayName ?? t.TankCode)),
            "Id",
            "Display");

        ViewBag.ContractStockScopesJson = await BuildContractStockScopeJsonAsync();

        ViewBag.DestinationTerminals = ViewBag.SourceTerminals;
        ViewBag.DestinationStorageTanks = ViewBag.SourceStorageTanks;

        ViewBag.Destinations = new SelectList(
            await _db.Locations
                .AsNoTracking()
                .OrderBy(l => l.Name)
                .Select(l => new { l.Id, Text = l.Name })
                .ToListAsync(),
            "Id",
            "Text");

        ViewBag.Shipments = new SelectList(
            await _db.Shipments
                .AsNoTracking()
                .OrderByDescending(s => s.DepartureDate)
                .ThenByDescending(s => s.Id)
                .Select(s => new { s.Id, Text = s.ShipmentCode })
                .ToListAsync(),
            "Id",
            "Text");

        // فقط محموله‌های دارای قرارداد خرید — برای انتخاب درجای «انتقال گروهی».
        ViewBag.BatchShipments = new SelectList(
            await _db.Shipments
                .AsNoTracking()
                .Where(shipment => _db.ShipmentContracts.Any(link => link.ShipmentId == shipment.Id))
                .OrderByDescending(shipment => shipment.DepartureDate ?? shipment.ArrivalDate)
                .ThenByDescending(shipment => shipment.Id)
                .Select(shipment => new
                {
                    shipment.Id,
                    Label = shipment.Vessel == null || shipment.Vessel.Name == ""
                        ? shipment.ShipmentCode
                        : shipment.ShipmentCode + " — " + shipment.Vessel.Name
                })
                .Take(200)
                .ToListAsync(),
            "Id",
            "Label");

        // موتروان‌های فعال — برای پیشنهاد (datalist) هنگام انتخاب موتر «شخصی» در لیست انتقال گروهی.
        var batchDrivers = await _db.Drivers
            .AsNoTracking()
            .Where(d => d.IsActive)
            .OrderBy(d => d.FullName)
            .Select(d => new { d.Id, d.FullName })
            .ToListAsync();
        ViewBag.BatchDrivers = batchDrivers
            .Select(driver => new SelectListItem(driver.FullName, driver.Id.ToString()))
            .ToList();

        ViewBag.ServiceProviders = new SelectList(
            await _db.ServiceProviders
                .AsNoTracking()
                .Where(p => p.IsActive)
                .OrderBy(p => p.Name)
                .Select(p => new
                {
                    p.Id,
                    Text = string.IsNullOrWhiteSpace(p.Code) ? p.Name : p.Code + " - " + p.Name
                })
                .ToListAsync(),
            "Id",
            "Text");

        ViewBag.OperationalAssets = new SelectList(
            await _db.OperationalAssets
                .AsNoTracking()
                .Where(a => a.IsActive)
                .OrderBy(a => a.AssetCode)
                .ThenBy(a => a.Name)
                .Select(a => new
                {
                    a.Id,
                    Text = a.AssetCode + " - " + a.Name
                })
                .ToListAsync(),
            "Id",
            "Text");

        ViewBag.WagonNumberOptions = await BuildTransportUnitOptionsAsync(
            LoadingTransportType.Wagon,
            _db.Wagons
                .AsNoTracking()
                .Select(w => w.WagonNumber));

        ViewBag.TruckNumberOptions = await BuildTransportUnitOptionsAsync(
            LoadingTransportType.Truck,
            _db.Trucks
                .AsNoTracking()
                .Select(t => t.PlateNumber));

        ViewBag.VesselNameOptions = await BuildTransportUnitOptionsAsync(
            LoadingTransportType.Vessel,
            _db.Vessels
                .AsNoTracking()
                .Select(v => v.Name));
    }

    private async Task<string> BuildContractStockScopeJsonAsync()
    {
        var movements = await _db.InventoryMovements
            .AsNoTracking()
            .Select(m => new
            {
                ContractId = m.ContractId
                    ?? (m.LoadingReceipt != null && m.LoadingReceipt.LoadingRegister != null
                        ? (int?)m.LoadingReceipt.LoadingRegister.ContractId
                        : null)
                    ?? (m.InventoryBatch != null
                        ? (int?)m.InventoryBatch.ContractId
                        : null),
                m.TerminalId,
                m.StorageTankId,
                m.Direction,
                m.QuantityMt
            })
            .Where(m => m.ContractId.HasValue)
            .ToListAsync();

        var rows = movements
            .GroupBy(m => new { ContractId = m.ContractId!.Value, m.TerminalId, m.StorageTankId })
            .Select(g => new
            {
                g.Key.ContractId,
                g.Key.TerminalId,
                g.Key.StorageTankId,
                QuantityMt = g.Sum(m => ToSignedStockScopeQuantity(m.Direction, m.QuantityMt))
            })
            .Where(r => r.QuantityMt > 0m)
            .OrderBy(r => r.ContractId)
            .ThenBy(r => r.TerminalId)
            .ThenBy(r => r.StorageTankId)
            .ToList();

        return System.Text.Json.JsonSerializer.Serialize(
            rows,
            new System.Text.Json.JsonSerializerOptions
            {
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
            });
    }

    private static decimal ToSignedStockScopeQuantity(MovementDirection direction, decimal quantityMt)
        => direction switch
        {
            MovementDirection.In => quantityMt,
            MovementDirection.Adjustment => quantityMt,
            MovementDirection.Out => -quantityMt,
            MovementDirection.Transfer => -quantityMt,
            _ => 0m
        };

    private async Task<IReadOnlyList<string>> BuildTransportUnitOptionsAsync(
        LoadingTransportType transportType,
        IQueryable<string?> masterValues)
    {
        var masterOptions = await masterValues
            .Where(value => value != null && value.Trim() != "")
            .Select(value => value!.Trim())
            .ToListAsync();

        var historyOptions = await _db.InventoryTransportLegs
            .AsNoTracking()
            .Where(leg => leg.TransportType == transportType && leg.WagonNumber != null && leg.WagonNumber.Trim() != "")
            .Select(leg => leg.WagonNumber!.Trim())
            .ToListAsync();

        return masterOptions
            .Concat(historyOptions)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value)
            .ToList();
    }

    private async Task<List<DirectShipmentLossRow>> GetDirectShipmentLossesAsync(int shipmentId)
        => await _db.LossEvents
            .AsNoTracking()
            .Where(l => l.ShipmentId == shipmentId
                && !l.IsCancelled
                && l.TransportLegId == null
                && l.LoadingRegisterId == null
                && l.LoadingReceiptId == null
                && l.TruckDispatchId == null
                && l.SalesTransactionId == null
                && l.DifferenceQuantityMt > 0m)
            .Select(l => new DirectShipmentLossRow(l.ContractId, l.DifferenceQuantityMt))
            .ToListAsync();

    private static Dictionary<int, decimal> AllocateDirectLossesByContract(
        IReadOnlyList<DirectShipmentLossRow> directLosses,
        IReadOnlyDictionary<int, decimal> remainingBeforeLossByContract)
    {
        var result = remainingBeforeLossByContract.Keys.ToDictionary(id => id, _ => 0m);
        if (directLosses.Count == 0 || remainingBeforeLossByContract.Count == 0)
        {
            return result;
        }

        var unassignedLoss = 0m;
        foreach (var loss in directLosses)
        {
            if (loss.ContractId.HasValue
                && result.ContainsKey(loss.ContractId.Value)
                && remainingBeforeLossByContract.TryGetValue(loss.ContractId.Value, out var remaining))
            {
                var available = Math.Max(remaining - result[loss.ContractId.Value], 0m);
                var applied = Math.Min(loss.QuantityMt, available);
                result[loss.ContractId.Value] += applied;
                unassignedLoss += Math.Max(loss.QuantityMt - applied, 0m);
                continue;
            }

            unassignedLoss += loss.QuantityMt;
        }

        if (unassignedLoss <= 0m)
        {
            return result;
        }

        var weights = remainingBeforeLossByContract
            .Select(kvp => new
            {
                ContractId = kvp.Key,
                Remaining = Math.Max(kvp.Value - result[kvp.Key], 0m)
            })
            .Where(x => x.Remaining > 0m)
            .ToList();

        var totalRemaining = weights.Sum(x => x.Remaining);
        if (totalRemaining <= 0m)
        {
            return result;
        }

        var lossToAllocate = Math.Min(unassignedLoss, totalRemaining);
        var allocated = 0m;
        for (var i = 0; i < weights.Count; i++)
        {
            var share = i == weights.Count - 1
                ? lossToAllocate - allocated
                : decimal.Round(lossToAllocate * weights[i].Remaining / totalRemaining, 4, MidpointRounding.AwayFromZero);
            share = Math.Min(share, weights[i].Remaining);
            result[weights[i].ContractId] += share;
            allocated += share;
        }

        return result;
    }

    private static void NormalizeGroupReceiptModel(InventoryTransportGroupReceiptCreateViewModel model)
    {
        model.GroupKey = (model.GroupKey ?? string.Empty).Trim();
        model.TransportReference = (model.TransportReference ?? string.Empty).Trim();
        model.ReceiptDate = model.ReceiptDate == default ? AfghanistanBusinessClock.SystemToday : model.ReceiptDate.Date;
        model.Notes = NormalizeString(model.Notes);
        model.DocumentReference = NormalizeString(model.DocumentReference);
        model.AllowanceMt = model.AllowanceMt.HasValue
            ? Math.Max(model.AllowanceMt.Value, 0m)
            : 0m;
        model.ChargeableShortageMt = decimal.Round(
            Math.Max(model.TotalShortageQuantityMt - (model.AllowanceMt ?? 0m), 0m),
            4,
            MidpointRounding.AwayFromZero);
    }

    private static void NormalizeGroupExpenseModel(InventoryTransportGroupExpenseCreateViewModel model)
    {
        model.GroupKey = (model.GroupKey ?? string.Empty).Trim();
        model.TransportReference = (model.TransportReference ?? string.Empty).Trim();
        model.Description = (model.Description ?? string.Empty).Trim();
        model.ManualExpenseTypeName = NormalizeString(model.ManualExpenseTypeName);
        model.Currency = SystemCurrency.Normalize(model.Currency);
        model.ExpenseDate = model.ExpenseDate == default ? AfghanistanBusinessClock.SystemToday : model.ExpenseDate.Date;
        model.ServiceProviderId = NormalizePositiveInt(model.ServiceProviderId);
        model.OperationalAssetId = NormalizePositiveInt(model.OperationalAssetId);

        if (SystemCurrency.IsBaseCurrency(model.Currency))
        {
            model.AppliedFxRateToUsd = 1m;
        }
    }

    private static void NormalizeGroupExpenseModalModel(InventoryTransportGroupExpenseModalViewModel model)
    {
        model.GroupKey = (model.GroupKey ?? string.Empty).Trim();
        model.TransportLegId = NormalizePositiveInt(model.TransportLegId);
        model.TransportReference = (model.TransportReference ?? string.Empty).Trim();
        model.ReturnUrl = NormalizeString(model.ReturnUrl);
        model.Lines ??= [];

        foreach (var line in model.Lines)
        {
            line.ExpenseTypeId = NormalizePositiveInt(line.ExpenseTypeId);
            line.ManualExpenseTypeName = NormalizeString(line.ManualExpenseTypeName);
            line.ServiceProviderId = NormalizePositiveInt(line.ServiceProviderId);
            line.OperationalAssetId = NormalizePositiveInt(line.OperationalAssetId);
            line.Notes = NormalizeString(line.Notes);
        }
    }

    private static void Normalize(InventoryTransportLegCreateViewModel model)
    {
        model.WagonNumber = NormalizeString(model.WagonNumber);
        model.RwbNo = NormalizeString(model.RwbNo);
        model.BillOfLadingNumber = NormalizeString(model.BillOfLadingNumber);
        model.RouteDescription = NormalizeString(model.RouteDescription);
        model.ServiceProviderId = NormalizePositiveInt(model.ServiceProviderId);
        model.OperationalAssetId = NormalizePositiveInt(model.OperationalAssetId);
        model.Notes = NormalizeString(model.Notes);

        model.Allocations ??= [];
        foreach (var allocation in model.Allocations)
        {
            allocation.Notes = NormalizeString(allocation.Notes);
        }
    }

    private static string? NormalizeString(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static int? NormalizePositiveInt(int? value)
        => value.HasValue && value.Value > 0 ? value.Value : null;

    private static bool CanEdit(InventoryTransportLeg leg)
        => leg.Status == InventoryTransportLegStatus.Draft && !leg.OutboundInventoryMovementId.HasValue;

    private static InventoryTransportLegCreateViewModel ToCreateViewModel(InventoryTransportLeg leg)
        => new()
        {
            Id = leg.Id,
            ShipmentId = leg.ShipmentId,
            SourcePurchaseContractId = leg.SourcePurchaseContractId,
            ProductId = leg.ProductId,
            SourceTerminalId = leg.SourceTerminalId,
            SourceStorageTankId = leg.SourceStorageTankId,
            DestinationTerminalId = leg.DestinationTerminalId,
            DestinationStorageTankId = leg.DestinationStorageTankId,
            DestinationLocationId = leg.DestinationLocationId,
            TransportType = leg.TransportType,
            WagonNumber = leg.WagonNumber,
            RwbNo = leg.RwbNo,
            BillOfLadingNumber = leg.BillOfLadingNumber,
            RouteDescription = leg.RouteDescription,
            ServiceProviderId = leg.ServiceProviderId,
            OperationalAssetId = leg.OperationalAssetId,
            LoadedDate = leg.LoadedDate,
            ExpectedArrivalDate = leg.ExpectedArrivalDate,
            QuantityMt = leg.QuantityMt,
            ChargeableQuantityMt = leg.ChargeableQuantityMt,
            PurchaseUnitCostUsd = leg.PurchaseUnitCostUsd,
            Notes = leg.Notes,
            Allocations =
            [
                new InventoryTransportLegAllocationInput
                {
                    SourcePurchaseContractId = leg.SourcePurchaseContractId,
                    ProductId = leg.ProductId,
                    SourceTerminalId = leg.SourceTerminalId,
                    SourceStorageTankId = leg.SourceStorageTankId,
                    QuantityMt = leg.QuantityMt,
                    ChargeableQuantityMt = leg.ChargeableQuantityMt,
                    PurchaseUnitCostUsd = leg.PurchaseUnitCostUsd,
                    Notes = leg.Notes
                }
            ]
        };

    private sealed class TransportFlowLegProjection
    {
        public int Id { get; set; }
        public int? ShipmentId { get; set; }
        public string? ShipmentCode { get; set; }
        public string? TransportGroupKey { get; set; }
        public int SourcePurchaseContractId { get; set; }
        public string ContractNumber { get; set; } = "";
        public string ProductName { get; set; } = "";
        public string SourceTerminalName { get; set; } = "";
        public string? SourceTankCode { get; set; }
        public string? DestinationTerminalName { get; set; }
        public string? DestinationTankCode { get; set; }
        public string? DestinationLocationName { get; set; }
        public LoadingTransportType TransportType { get; set; }
        public string? WagonNumber { get; set; }
        public string? RwbNo { get; set; }
        public string? BillOfLadingNumber { get; set; }
        public string? RouteDescription { get; set; }
        public DateTime LoadedDate { get; set; }
        public DateTime? ExpectedArrivalDate { get; set; }
        public decimal QuantityMt { get; set; }
        public decimal? PurchaseUnitCostUsd { get; set; }
        public InventoryTransportLegStatus Status { get; set; }
        public int? OutboundInventoryMovementId { get; set; }
        public List<TransportFlowAllocationProjection> Allocations { get; set; } = [];
    }

    private sealed class TransportFlowAllocationProjection
    {
        public int SourcePurchaseContractId { get; set; }
        public string ContractNumber { get; set; } = "";
        public decimal QuantityMt { get; set; }
        public int? OutboundInventoryMovementId { get; set; }
    }

    private sealed class TransportFlowContractLegProjection
    {
        public required TransportFlowLegProjection Leg { get; set; }
        public int SourcePurchaseContractId { get; set; }
        public string ContractNumber { get; set; } = "";
        public decimal QuantityMt { get; set; }
        public bool IsLoaded { get; set; }
    }

    private sealed class TransportReceiptTotals
    {
        public int LegId { get; set; }
        public decimal ReceivedQuantityMt { get; set; }
        public decimal ShortageQuantityMt { get; set; }
    }

    private sealed record DirectShipmentLossRow(int? ContractId, decimal QuantityMt);
}
