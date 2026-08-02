using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Helpers;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.Expenses;
using PTGOilSystem.Web.Security;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Audit;
using PTGOilSystem.Web.Services.Exceptions;
using ServiceProviderEntity = PTGOilSystem.Web.Models.Entities.ServiceProvider;
using PTGOilSystem.Web.Services.Time;

namespace PTGOilSystem.Web.Controllers;

[Authorize]
public partial class ExpensesController : Controller
{
    private readonly ApplicationDbContext _db;
    private readonly ICurrencyConversionService _currencyConversion;
    private readonly IAuditService _audit;
    private readonly ILogger<ExpensesController> _logger;
    // مرحله ۵ — Dual-write اختیاری به دفتر کل جدید. پشت Feature Flag و null-safe: اگر تزریق
    // نشود یا خاموش باشد، مسیر قدیمی هیچ تغییری نمی‌کند.
    private readonly Services.Accounting.IExpenseAccountingAdapter? _expenseAccounting;
    private const int DefaultListLimit = 100;
    private const int LookupLimit = 200;
    private const string DefaultWagonRentExpenseName = "Wagon Rent";

    private readonly IAfghanistanBusinessClock _businessClock;

    [ActivatorUtilitiesConstructor]
    public ExpensesController(
        ApplicationDbContext db,
        ICurrencyConversionService currencyConversion,
        IAuditService audit,
        ILogger<ExpensesController> logger,
        Services.Accounting.IExpenseAccountingAdapter? expenseAccounting = null,
        IAfghanistanBusinessClock? businessClock = null)
    {
        // مرجع «امروزِ کاری» همیشه ساعت کابل است، نه تاریخ UTC سرور.
        _businessClock = businessClock ?? new AfghanistanBusinessClock(TimeProvider.System);
        _db = db;
        _currencyConversion = currencyConversion;
        _audit = audit;
        _logger = logger;
        _expenseAccounting = expenseAccounting;
    }

    public ExpensesController(
        ApplicationDbContext db,
        IAuditService audit,
        ILogger<ExpensesController> logger)
        : this(
            db,
            new CurrencyConversionService(new PricingService(db)),
            audit,
            logger)
    {
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

    private async Task PopulateLookupsAsync(
        ExpenseCreateViewModel? createModel = null,
        ExpenseIndexFilterViewModel? filter = null)
    {
        var selectedContractId = createModel?.ContractId ?? filter?.ContractId;
        var selectedShipmentId = createModel?.ShipmentId ?? filter?.ShipmentId;
        var selectedTruckDispatchId = createModel?.TruckDispatchId ?? filter?.TruckDispatchId;
        var selectedTransportLegId = createModel?.TransportLegId ?? filter?.TransportLegId;
        var selectedServiceProviderId = createModel?.ServiceProviderId ?? filter?.ServiceProviderId;
        var selectedOperationalAssetId = createModel?.OperationalAssetId ?? filter?.OperationalAssetId;

        var contracts = await _db.Contracts
            .AsNoTracking()
            .OrderBy(c => selectedContractId.HasValue && c.Id == selectedContractId.Value ? 0 : 1)
            .ThenByDescending(c => c.ContractDate)
            .ThenBy(c => c.ContractNumber)
            .Take(LookupLimit)
            .Select(c => new
            {
                c.Id,
                c.ContractName,
                c.ContractNumber,
                c.ContractType,
                ProductName = c.Product != null ? c.Product.Name : null,
                UnitSymbol = c.Unit != null ? c.Unit.Symbol : null,
                UnitCode = c.Unit != null ? c.Unit.Code : null,
                UnitNamePersian = c.Unit != null ? c.Unit.NamePersian : null,
                UnitName = c.Unit != null ? c.Unit.Name : null
            })
            .ToListAsync();

        var expenseTypeOptions = await _db.ExpenseTypes
                .AsNoTracking()
                .Where(e => e.IsActive)
                .OrderBy(e => e.Category)
                .ThenBy(e => e.Code)
                .Select(e => new
                {
                    e.Id,
                    Name = e.NamePersian ?? e.Name,
                    DisplayName = (e.NamePersian ?? e.Name) + " (" + e.Code + ")"
                })
                .ToListAsync();

        ViewBag.ExpenseTypeNames = expenseTypeOptions.Select(e => e.Name).ToList();
        ViewBag.ExpenseTypes = new SelectList(
            expenseTypeOptions,
            "Id",
            "DisplayName",
            createModel?.ExpenseTypeId ?? filter?.ExpenseTypeId);

        ViewBag.Contracts = new SelectList(
            contracts
                .Select(c => new ContractLookupOption(
                    c.Id,
                    ContractUiText.FormatLookup(
                        c.ContractName,
                        c.ContractNumber,
                        c.ContractType,
                        c.ProductName,
                        ContractUiText.ResolveUnitText(c.UnitSymbol, c.UnitCode, c.UnitNamePersian, c.UnitName))))
                .ToList(),
            nameof(ContractLookupOption.Id),
            nameof(ContractLookupOption.Display),
            selectedContractId);

        var shipments = await _db.Shipments
            .AsNoTracking()
            .OrderBy(s => selectedShipmentId.HasValue && s.Id == selectedShipmentId.Value ? 0 : 1)
            .ThenByDescending(s => s.DepartureDate)
            .ThenByDescending(s => s.Id)
            .Take(LookupLimit)
            .Select(s => new { s.Id, s.ShipmentCode })
            .ToListAsync();

        ViewBag.Shipments = shipments
            .Select(s => new SelectListItem
            {
                Value = s.Id.ToString(),
                Text = string.IsNullOrWhiteSpace(s.ShipmentCode)
                    ? $"Shipment #{s.Id}"
                    : s.ShipmentCode,
                Selected = (createModel?.ShipmentId ?? filter?.ShipmentId) == s.Id
            })
            .ToList();

        var dispatches = await _db.TruckDispatches
            .AsNoTracking()
            .OrderBy(d => selectedTruckDispatchId.HasValue && d.Id == selectedTruckDispatchId.Value ? 0 : 1)
            .ThenByDescending(d => d.DispatchDate)
            .ThenByDescending(d => d.Id)
            .Take(LookupLimit)
            .Select(d => new
            {
                d.Id,
                d.DispatchDate,
                PlateNumber = d.Truck != null ? d.Truck.PlateNumber : null
            })
            .ToListAsync();

        ViewBag.TruckDispatches = dispatches
            .Select(d => new SelectListItem
            {
                Value = d.Id.ToString(),
                Text = $"#{d.Id} - {(d.PlateNumber ?? "بدون پلاک")} - {DateDisplay.Date(d.DispatchDate)}",

                Selected = (createModel?.TruckDispatchId ?? filter?.TruckDispatchId) == d.Id
            })
            .ToList();

        var transportLegs = await _db.InventoryTransportLegs
            .AsNoTracking()
            .OrderBy(l => selectedTransportLegId.HasValue && l.Id == selectedTransportLegId.Value ? 0 : 1)
            .ThenByDescending(l => l.LoadedDate)
            .ThenByDescending(l => l.Id)
            .Take(LookupLimit)
            .Select(l => new
            {
                l.Id,
                l.LoadedDate,
                l.WagonNumber,
                l.RwbNo,
                ContractNumber = l.SourcePurchaseContract != null ? l.SourcePurchaseContract.ContractNumber : null,
                ProductName = l.Product != null ? l.Product.Name : null
            })
            .ToListAsync();

        ViewBag.TransportLegs = transportLegs
            .Select(l => new SelectListItem
            {
                Value = l.Id.ToString(),
                Text = $"#{l.Id} - {(l.WagonNumber ?? l.RwbNo ?? "Transport leg")} - {DateDisplay.Date(l.LoadedDate)} - {l.ContractNumber} - {l.ProductName}",
                Selected = selectedTransportLegId == l.Id
            })
            .ToList();

        var serviceProviders = await _db.ServiceProviders
            .AsNoTracking()
            .Where(p => p.IsActive || (selectedServiceProviderId.HasValue && p.Id == selectedServiceProviderId.Value))
            .OrderBy(p => selectedServiceProviderId.HasValue && p.Id == selectedServiceProviderId.Value ? 0 : 1)
            .ThenBy(p => p.Name)
            .Select(p => new { p.Id, p.Name, p.Code })
            .Take(LookupLimit)
            .ToListAsync();

        ViewBag.ServiceProviders = serviceProviders
            .Select(p => new SelectListItem
            {
                Value = p.Id.ToString(),
                Text = string.IsNullOrWhiteSpace(p.Code) ? p.Name : $"{p.Name} ({p.Code})",
                Selected = selectedServiceProviderId == p.Id
            })
            .ToList();

        var operationalAssets = await _db.OperationalAssets
            .AsNoTracking()
            .Where(a => a.IsActive || (selectedOperationalAssetId.HasValue && a.Id == selectedOperationalAssetId.Value))
            .OrderBy(a => selectedOperationalAssetId.HasValue && a.Id == selectedOperationalAssetId.Value ? 0 : 1)
            .ThenBy(a => a.AssetCode)
            .ThenBy(a => a.Name)
            .Select(a => new { a.Id, a.AssetCode, a.Name })
            .Take(LookupLimit)
            .ToListAsync();

        ViewBag.OperationalAssets = operationalAssets
            .Select(a => new SelectListItem
            {
                Value = a.Id.ToString(),
                Text = $"{a.AssetCode} - {a.Name}",
                Selected = selectedOperationalAssetId == a.Id
            })
            .ToList();

        ViewBag.Currencies = new SelectList(
            await _db.Currencies
                .AsNoTracking()
                .Where(c => c.IsActive)
                .OrderBy(c => c.Code)
                .Select(c => new { c.Code })
                .ToListAsync(),
            "Code",

            "Code",
            createModel?.Currency);
    }

    private async Task PopulateWagonRentLookupsAsync(WagonRentCreateViewModel model)
    {
        var expenseTypeOptions = await _db.ExpenseTypes
            .AsNoTracking()
            .Where(e => e.IsActive)
            .OrderBy(e => e.Category)
            .ThenBy(e => e.Code)
            .Select(e => new
            {
                e.Id,
                Name = e.NamePersian ?? e.Name,
                DisplayName = (e.NamePersian ?? e.Name) + " (" + e.Code + ")"
            })
            .ToListAsync();

        ViewBag.ExpenseTypeNames = expenseTypeOptions.Select(e => e.Name).ToList();
        ViewBag.ExpenseTypes = new SelectList(
            expenseTypeOptions,
            "Id",
            "DisplayName",
            model.ExpenseTypeId);

        var contracts = await _db.Contracts
            .AsNoTracking()
            .OrderBy(c => model.ContractId > 0 && c.Id == model.ContractId ? 0 : 1)
            .ThenByDescending(c => c.ContractDate)
            .ThenBy(c => c.ContractNumber)
            .Take(LookupLimit)
            .Select(c => new
            {
                c.Id,
                c.ContractName,
                c.ContractNumber,
                c.ContractType,
                ProductName = c.Product != null ? c.Product.Name : null,
                UnitSymbol = c.Unit != null ? c.Unit.Symbol : null,
                UnitCode = c.Unit != null ? c.Unit.Code : null,
                UnitNamePersian = c.Unit != null ? c.Unit.NamePersian : null,
                UnitName = c.Unit != null ? c.Unit.Name : null
            })
            .ToListAsync();

        ViewBag.Contracts = new SelectList(
            contracts
                .Select(c => new ContractLookupOption(
                    c.Id,
                    ContractUiText.FormatLookup(
                        c.ContractName,
                        c.ContractNumber,
                        c.ContractType,
                        c.ProductName,
                        ContractUiText.ResolveUnitText(c.UnitSymbol, c.UnitCode, c.UnitNamePersian, c.UnitName))))
                .ToList(),
            nameof(ContractLookupOption.Id),
            nameof(ContractLookupOption.Display),
            model.ContractId);

        var currencies = await _db.Currencies
            .AsNoTracking()
            .Where(c => c.IsActive)
            .OrderBy(c => c.Code)
            .Select(c => new { c.Code })
            .ToListAsync();
        if (currencies.Count == 0)
        {
            currencies = [new { Code = SystemCurrency.BaseCurrencyCode }, new { Code = "RUB" }];
        }

        ViewBag.Currencies = new SelectList(currencies, "Code", "Code", model.Currency);
    }

    private async Task<int?> FindWagonRentExpenseTypeIdAsync()
    {
        var types = await _db.ExpenseTypes
            .AsNoTracking()
            .Where(e => e.IsActive)
            .Select(e => new { e.Id, e.Code, e.Name, e.NamePersian })
            .ToListAsync();

        return types
            .FirstOrDefault(e => ExpenseClassification.IsWagonRent(e.Code, e.Name, e.NamePersian, null))
            ?.Id;
    }

    private static void NormalizeWagonRentModel(WagonRentCreateViewModel model)
    {
        model.ExpenseDate = model.ExpenseDate.Date;
        model.AmountOriginal = decimal.Round(
            model.QuantityMt * model.UnitPriceOriginal,
            4,
            MidpointRounding.AwayFromZero);

        if (!string.IsNullOrWhiteSpace(model.Currency))
        {
            model.Currency = SystemCurrency.Normalize(model.Currency);
            if (SystemCurrency.IsBaseCurrency(model.Currency))
            {
                model.AppliedFxRateToUsd = 1m;
            }
            else if (model.DocumentCurrencyPerUsdRate.HasValue && model.DocumentCurrencyPerUsdRate.Value > 0m)
            {
                model.AppliedFxRateToUsd = decimal.Round(
                    1m / model.DocumentCurrencyPerUsdRate.Value,
                    6,
                    MidpointRounding.AwayFromZero);
            }
        }

        model.ManualExpenseTypeName = string.IsNullOrWhiteSpace(model.ManualExpenseTypeName)
            ? null
            : model.ManualExpenseTypeName.Trim();
        model.Reference = string.IsNullOrWhiteSpace(model.Reference) ? null : model.Reference.Trim();
        model.Notes = string.IsNullOrWhiteSpace(model.Notes) ? null : model.Notes.Trim();
    }

    private static string BuildWagonRentDescription(WagonRentCreateViewModel model, CurrencyConversionResult conversion)
    {
        var parts = new List<string>
        {
            "Wagon Rent",
            $"M-Tone: {model.QuantityMt:N4}",
            $"Unit Price: {model.UnitPriceOriginal:N4} {conversion.SourceCurrencyCode}/MT",
            $"Rent Amount: {model.AmountOriginal:N4} {conversion.SourceCurrencyCode}",
            $"FX USD-per-unit: {conversion.AppliedRateToBase:N6}"
        };

        if (model.DocumentCurrencyPerUsdRate.HasValue)
        {
            parts.Add($"Document rate: {model.DocumentCurrencyPerUsdRate.Value:N6} {conversion.SourceCurrencyCode}/USD");
        }

        if (!string.IsNullOrWhiteSpace(model.Reference))
        {
            parts.Add($"Reference: {model.Reference}");
        }

        if (!string.IsNullOrWhiteSpace(model.Notes))
        {
            parts.Add($"Notes: {model.Notes}");
        }

        var description = string.Join(" | ", parts);
        return description.Length <= 1000 ? description : description[..1000];
    }

    public async Task<IActionResult> Index([FromQuery] ExpenseIndexFilterViewModel? filter = null, int page = 1, [FromQuery(Name = "pageSize")] int? perPage = null)
    {
        var pageSize = ListPageSize.Resolve(perPage, 5);
        ViewData["PageSize"] = pageSize;
        ViewData["DefaultPageSize"] = 5;
        var exportAll = page <= 0;
        filter ??= new ExpenseIndexFilterViewModel();

        var query = _db.ExpenseTransactions
            .AsNoTracking()
            .AsQueryable();

        query = query.Where(e => !e.IsCancelled);

        if (filter.ExpenseTypeId.HasValue)
            query = query.Where(e => e.ExpenseTypeId == filter.ExpenseTypeId.Value);
        if (filter.ContractId.HasValue)
            query = query.Where(e => e.ContractId == filter.ContractId.Value);
        if (filter.ShipmentId.HasValue)
            query = query.Where(e => e.ShipmentId == filter.ShipmentId.Value);
        if (filter.TruckDispatchId.HasValue)
            query = query.Where(e => e.TruckDispatchId == filter.TruckDispatchId.Value);
        if (filter.TransportLegId.HasValue)
            query = query.Where(e => e.TransportLegId == filter.TransportLegId.Value);
        if (filter.ServiceProviderId.HasValue)
            query = query.Where(e => e.ServiceProviderId == filter.ServiceProviderId.Value);
        if (filter.OperationalAssetId.HasValue)
            query = query.Where(e => e.OperationalAssetId == filter.OperationalAssetId.Value);
        if (!string.IsNullOrWhiteSpace(filter.Query))
        {
            var q = filter.Query.Trim();
            query = query.Where(e => e.Description != null && e.Description.Contains(q));
        }
        if (filter.FromDate.HasValue)
            query = query.Where(e => e.ExpenseDate >= filter.FromDate.Value);
        if (filter.ToDate.HasValue)
            query = query.Where(e => e.ExpenseDate <= filter.ToDate.Value);

        var totalCount = await query.CountAsync();
        var pageCount = Math.Max(1, (int)Math.Ceiling(totalCount / (double)pageSize));
        page = Math.Clamp(page, 1, pageCount);

        await PopulateLookupsAsync(filter: filter);

        var expenses = await query
            .OrderByDescending(e => e.ExpenseDate)
            .ThenByDescending(e => e.Id)
            .Skip(exportAll ? 0 : (page - 1) * pageSize)
            .Take(exportAll ? totalCount : pageSize)
            .Select(e => new ExpenseListItemViewModel
            {
                Id = e.Id,
                ExpenseDate = e.ExpenseDate,
                ExpenseTypeName = e.ExpenseType != null ? e.ExpenseType.NamePersian ?? e.ExpenseType.Name : string.Empty,
                ContractName = e.Contract != null ? e.Contract.ContractName : null,
                ContractNumber = e.Contract != null ? e.Contract.ContractNumber : null,
                ShipmentCode = e.Shipment != null ? e.Shipment.ShipmentCode : null,
                TruckDispatchLabel = e.TruckDispatch == null
                    ? null
                    : $"#{e.TruckDispatch.Id} - {(e.TruckDispatch.Truck != null ? e.TruckDispatch.Truck.PlateNumber : "بدون پلاک")}",
                TransportLegLabel = e.TransportLeg == null
                    ? null
                    : $"#{e.TransportLeg.Id} - {(e.TransportLeg.WagonNumber ?? e.TransportLeg.RwbNo ?? "Transport leg")}",
                // نمبر وسیله برای اینکه در خروجی معلوم باشد مصرف برای کدام موتر/واگن است.
                VehicleNumber = e.TruckDispatch != null && e.TruckDispatch.Truck != null
                    ? e.TruckDispatch.Truck.PlateNumber
                    : e.TransportLeg != null
                        ? (e.TransportLeg.Truck != null
                            ? e.TransportLeg.Truck.PlateNumber
                            : e.TransportLeg.WagonNumber ?? e.TransportLeg.RwbNo)
                        : null,
                ServiceProviderName = e.ServiceProvider != null ? e.ServiceProvider.Name : null,
                OperationalAssetName = e.OperationalAsset != null ? e.OperationalAsset.AssetCode + " - " + e.OperationalAsset.Name : null,
                Amount = e.Amount,
                Currency = e.Currency,
                AppliedFxRateToUsd = e.AppliedFxRateToUsd,
                AmountUsd = e.AmountUsd,
                Description = e.Description ?? string.Empty
            })
            .ToListAsync();

        // آمار کامل روی همهٔ رکوردهای مطابق فیلتر (نه فقط این صفحه) برای کارت‌های آماری و ردیف جمع.
        ViewBag.SumAmountUsd = await query.SumAsync(e => e.AmountUsd);
        ViewBag.ExpenseTypeCount = await query
            .Where(e => e.ExpenseTypeId != null)
            .Select(e => e.ExpenseTypeId)
            .Distinct()
            .CountAsync();
        ViewBag.RelatedContractCount = await query
            .Where(e => e.ContractId != null)
            .Select(e => e.ContractId)
            .Distinct()
            .CountAsync();

        return View(new ExpenseIndexViewModel
        {
            Filter = filter,
            Items = expenses,
            CurrentPage = page,
            PageCount = pageCount,
            TotalCount = totalCount
        });
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Cancel(int id, string? returnUrl = null)
    {
        var expense = await _db.ExpenseTransactions
            .FirstOrDefaultAsync(e => e.Id == id);

        if (expense is null)
        {
            return NotFound();
        }

        if (expense.IsCancelled)
        {
            TempData["ok"] = "این هزینه قبلاً لغو شده است.";
            if (!string.IsNullOrWhiteSpace(returnUrl) && Url?.IsLocalUrl(returnUrl) == true)
                return Redirect(returnUrl);
            return RedirectToAction(nameof(Details), new { id });
        }

        var originalLedger = await _db.LedgerEntries
            .AsNoTracking()
            .Where(l => l.SourceType == "Expense" && l.SourceId == expense.Id)
            .OrderByDescending(l => l.Id)
            .FirstOrDefaultAsync();

        if (originalLedger is null)
        {
            TempData["err"] = "سند مالی این هزینه پیدا نشد؛ لغو انجام نشد.";
            if (!string.IsNullOrWhiteSpace(returnUrl) && Url?.IsLocalUrl(returnUrl) == true)
                return Redirect(returnUrl);
            return RedirectToAction(nameof(Details), new { id });
        }

        // مرحله ۵ — Reversal قبل از علامت‌خوردن IsCancelled، تا Adapter شرکت را از همان
        // روابط قبلی حل کند. Idempotent است و اگر سند اصلی پست نشده باشد بی‌اثر می‌ماند.
        if (_expenseAccounting is not null)
        {
            await _expenseAccounting.TryPostExpenseReversalAsync(expense);
        }

        expense.IsCancelled = true;

        var reversal = new LedgerEntry
        {
            EntryDate = _businessClock.Today,
            Side = ReverseSide(originalLedger.Side),
            AmountUsd = originalLedger.AmountUsd,
            Currency = originalLedger.Currency,
            SourceAmount = originalLedger.SourceAmount,
            SourceCurrencyCode = originalLedger.SourceCurrencyCode,
            AppliedFxRateToUsd = originalLedger.AppliedFxRateToUsd,
            AppliedFxRateDate = originalLedger.AppliedFxRateDate,
            AppliedFxRateSource = originalLedger.AppliedFxRateSource,
            Description = $"لغو هزینه #{expense.Id} | {originalLedger.Description}",
            SourceType = "Expense",
            SourceId = expense.Id,
            Reference = (originalLedger.Reference ?? $"EXP-{expense.Id}") + "-CANCEL",
            ContractId = originalLedger.ContractId,
            ServiceProviderId = originalLedger.ServiceProviderId,
            ShipmentId = originalLedger.ShipmentId
        };

        _db.LedgerEntries.Add(reversal);
        await _db.SaveChangesAsync();

        TempData["ok"] = "هزینه لغو شد.";
        if (!string.IsNullOrWhiteSpace(returnUrl) && Url?.IsLocalUrl(returnUrl) == true)
            return Redirect(returnUrl);

        return RedirectToAction(nameof(Index));
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    public async Task<IActionResult> Create(int? contractId = null, int? transportLegId = null, int? truckDispatchId = null, int? serviceProviderId = null, int? operationalAssetId = null, string? returnUrl = null)
    {
        var model = new ExpenseCreateViewModel
        {
            ExpenseDate = _businessClock.Today,
            Currency = SystemCurrency.BaseCurrencyCode
        };

        if (contractId.HasValue)
        {
            var contract = await _db.Contracts
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == contractId.Value);
            if (contract is not null)
            {
                model.ContractId = contract.Id;
            }
        }

        if (transportLegId.HasValue)
        {
            var transportLeg = await _db.InventoryTransportLegs
                .AsNoTracking()
                .FirstOrDefaultAsync(l => l.Id == transportLegId.Value);
            if (transportLeg is not null)
            {
                model.TransportLegId = transportLeg.Id;
                model.ContractId = transportLeg.SourcePurchaseContractId;
                model.ShipmentId = transportLeg.ShipmentId;
            }
        }

        if (truckDispatchId.HasValue)
        {
            var truckDispatch = await _db.TruckDispatches
                .AsNoTracking()
                .FirstOrDefaultAsync(d => d.Id == truckDispatchId.Value);
            if (truckDispatch is not null)
            {
                model.TruckDispatchId = truckDispatch.Id;
                model.ContractId = truckDispatch.ContractId;
            }
        }

        if (serviceProviderId.HasValue)
        {
            model.ServiceProviderId = serviceProviderId.Value;
        }

        if (operationalAssetId.HasValue)
        {
            model.OperationalAssetId = operationalAssetId.Value;
        }

        model.ReturnUrl = returnUrl;

        await PopulateLookupsAsync(createModel: model);
        return View(model);
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    public async Task<IActionResult> CreateWagonRent(int? contractId = null, string? returnUrl = null)
    {
        var model = new WagonRentCreateViewModel
        {
            ExpenseDate = _businessClock.Today,
            ContractId = contractId ?? 0,
            Currency = SystemCurrency.BaseCurrencyCode,
            AppliedFxRateToUsd = 1m,
            ManualExpenseTypeName = DefaultWagonRentExpenseName,
            ReturnUrl = returnUrl
        };

        var wagonRentTypeId = await FindWagonRentExpenseTypeIdAsync();
        if (wagonRentTypeId.HasValue)
        {
            model.ExpenseTypeId = wagonRentTypeId;
            model.ManualExpenseTypeName = null;
        }

        await PopulateWagonRentLookupsAsync(model);
        return View(model);
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateWagonRent(WagonRentCreateViewModel model)
    {
        NormalizeWagonRentModel(model);
        var manualExpenseTypeName = model.ManualExpenseTypeName?.Trim() ?? string.Empty;

        ExpenseType? expenseType = null;
        if (model.ExpenseTypeId.HasValue)
        {
            expenseType = await _db.ExpenseTypes
                .AsNoTracking()
                .FirstOrDefaultAsync(e => e.Id == model.ExpenseTypeId.Value && e.IsActive);
            if (expenseType is null)
            {
                ModelState.AddModelError(nameof(model.ExpenseTypeId), "نوع مصرف انتخاب‌شده معتبر نیست.");
            }
        }
        else if (!string.IsNullOrWhiteSpace(manualExpenseTypeName))
        {
            expenseType = await FindExpenseTypeByManualNameAsync(manualExpenseTypeName);
        }
        else
        {
            ModelState.AddModelError(nameof(model.ManualExpenseTypeName), "نوع مصرف را از لیست انتخاب کنید یا دستی وارد کنید.");
        }

        var contractExists = await _db.Contracts
            .AsNoTracking()
            .AnyAsync(c => c.Id == model.ContractId);
        if (!contractExists)
        {
            ModelState.AddModelError(nameof(model.ContractId), "قرارداد انتخاب‌شده معتبر نیست.");
        }

        var hasActiveCurrencies = await _db.Currencies.AsNoTracking().AnyAsync(c => c.IsActive);
        if (hasActiveCurrencies
            && !await _db.Currencies.AsNoTracking().AnyAsync(c => c.Code == model.Currency && c.IsActive))
        {
            ModelState.AddModelError(nameof(model.Currency), "Invalid currency selection.");
        }

        if (!ModelState.IsValid)
        {
            model.ManualExpenseTypeName = manualExpenseTypeName;
            await PopulateWagonRentLookupsAsync(model);
            return View(model);
        }

        CurrencyConversionResult conversion;
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
            model.ManualExpenseTypeName = manualExpenseTypeName;
            await PopulateWagonRentLookupsAsync(model);
            return View(model);
        }

        IDbContextTransaction? transaction = null;
        if (_db.Database.IsRelational())
        {
            transaction = await _db.Database.BeginTransactionAsync();
        }

        try
        {
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

            var description = BuildWagonRentDescription(model, conversion);
            var expense = new ExpenseTransaction
            {
                ExpenseTypeId = expenseType.Id,
                ContractId = model.ContractId,
                ExpenseDate = model.ExpenseDate.Date,
                Amount = model.AmountOriginal,
                Currency = conversion.SourceCurrencyCode,
                AppliedFxRateToUsd = conversion.AppliedRateToBase,
                AmountUsd = conversion.ConvertToBase(model.AmountOriginal),
                Description = description,
                // Phase 1 — مسئول کرایه واگن (فقط ثبت/نمایش، به Ledger اثر ندارد).
                CostResponsibility = model.CostResponsibility
            };

            _db.ExpenseTransactions.Add(expense);
            await _db.SaveChangesAsync();

            var ledgerEntry = new LedgerEntry
            {
                EntryDate = expense.ExpenseDate,
                Side = LedgerSide.Debit,
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
                ContractId = expense.ContractId
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
                    ("ExpenseDate", expense.ExpenseDate),
                    ("Amount", expense.Amount),
                    ("Currency", expense.Currency),
                    ("AppliedFxRateToUsd", expense.AppliedFxRateToUsd),
                    ("AmountUsd", expense.AmountUsd),
                    ("Description", expense.Description),
                    ("LedgerReference", ledgerEntry.Reference)));

            if (transaction is not null)
            {
                await transaction.CommitAsync();
            }

            TempData["ok"] = "کرایه واگون با موفقیت به عنوان مصرف قرارداد ثبت شد.";
            if (TryGetLocalReturnUrl(model.ReturnUrl, out var localReturnUrl))
            {
                return Redirect(localReturnUrl);
            }

            return RedirectToAction(nameof(Details), new { id = expense.Id });
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

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(ExpenseCreateViewModel model)
    {
        NormalizeCreateModel(model);
        var normalizedDescription = model.Description?.Trim() ?? string.Empty;
        var manualExpenseTypeName = model.ManualExpenseTypeName?.Trim() ?? string.Empty;

        ExpenseType? expenseType = null;
        if (model.ExpenseTypeId.HasValue)
        {
            expenseType = await _db.ExpenseTypes
            .AsNoTracking()
                .FirstOrDefaultAsync(e => e.Id == model.ExpenseTypeId.Value && e.IsActive);
            if (expenseType is null)
            {
                ModelState.AddModelError(nameof(model.ExpenseTypeId), "نوع مصرف انتخاب‌شده معتبر نیست.");
            }
        }
        else if (!string.IsNullOrWhiteSpace(manualExpenseTypeName))
        {
            expenseType = await FindExpenseTypeByManualNameAsync(manualExpenseTypeName);
        }
        else
        {
            ModelState.AddModelError(nameof(model.ManualExpenseTypeName), "نوع مصرف را از لیست انتخاب کنید یا دستی وارد کنید.");
        }

        InventoryTransportLeg? transportLeg = null;
        if (model.TransportLegId.HasValue)
        {
            transportLeg = await _db.InventoryTransportLegs
                .AsNoTracking()
                .FirstOrDefaultAsync(l => l.Id == model.TransportLegId.Value);
            if (transportLeg is null)
            {
                ModelState.AddModelError(nameof(model.TransportLegId), "Transport leg selection is invalid.");
            }
            else
            {
                if (!model.ContractId.HasValue)
                {
                    model.ContractId = transportLeg.SourcePurchaseContractId;
                }
                else if (model.ContractId.Value != transportLeg.SourcePurchaseContractId)
                {
                    ModelState.AddModelError(nameof(model.TransportLegId), "Transport leg must match the selected purchase contract.");
                }

                if (transportLeg.ShipmentId.HasValue && !model.ShipmentId.HasValue)
                {
                    model.ShipmentId = transportLeg.ShipmentId;
                }
                else if (transportLeg.ShipmentId.HasValue
                         && model.ShipmentId.HasValue
                         && model.ShipmentId.Value != transportLeg.ShipmentId.Value)
                {
                    ModelState.AddModelError(nameof(model.ShipmentId), "Shipment must match the selected transport leg.");
                }
            }
        }

        Contract? contract = null;
        if (model.ContractId.HasValue)
        {
            contract = await _db.Contracts
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == model.ContractId.Value);
            if (contract is null)
            {
                ModelState.AddModelError(nameof(model.ContractId), "قرارداد انتخاب‌شده معتبر نیست.");
            }
        }

        Shipment? shipment = null;
        if (model.ShipmentId.HasValue)
        {
            shipment = await _db.Shipments
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == model.ShipmentId.Value);
            if (shipment is null)
            {
                ModelState.AddModelError(nameof(model.ShipmentId), "Shipment انتخاب‌شده معتبر نیست.");
            }
            else if (contract is not null
                     && !await ShipmentAllowsContractAsync(shipment.Id, shipment.ContractId, contract.Id))
            {
                ModelState.AddModelError(nameof(model.ShipmentId), "Shipment انتخاب‌شده با قرارداد انتخابی هم‌خوان نیست.");
            }
        }

        TruckDispatch? truckDispatch = null;
        if (model.TruckDispatchId.HasValue)
        {
            truckDispatch = await _db.TruckDispatches
                .AsNoTracking()
                .FirstOrDefaultAsync(d => d.Id == model.TruckDispatchId.Value);
            if (truckDispatch is null)
            {
                ModelState.AddModelError(nameof(model.TruckDispatchId), "دیسپچ انتخاب‌شده معتبر نیست.");
            }
            else if (contract is not null && truckDispatch.ContractId != contract.Id)
            {
                ModelState.AddModelError(nameof(model.TruckDispatchId), "دیسپچ انتخاب‌شده با قرارداد انتخابی هم‌خوان نیست.");
            }
        }

        if (shipment is not null
            && truckDispatch is not null
            && !await ShipmentAllowsContractAsync(shipment.Id, shipment.ContractId, truckDispatch.ContractId))
        {
            ModelState.AddModelError(string.Empty, "Shipment و دیسپچ انتخاب‌شده به یک قرارداد واحد اشاره نمی‌کنند.");
        }

        if (string.IsNullOrWhiteSpace(normalizedDescription))
        {
            ModelState.AddModelError(nameof(model.Description), "ثبت شرح یا مرجع برای trace هزینه الزامی است.");
        }

        var hasActiveCurrencies = await _db.Currencies.AsNoTracking().AnyAsync(c => c.IsActive);
        if (hasActiveCurrencies
            && !await _db.Currencies.AsNoTracking().AnyAsync(c => c.Code == model.Currency && c.IsActive))
        {
            ModelState.AddModelError(nameof(model.Currency), "Invalid currency selection.");
        }

        ServiceProviderEntity? serviceProvider = null;
        if (model.ServiceProviderId.HasValue)
        {
            serviceProvider = await _db.ServiceProviders
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == model.ServiceProviderId.Value);
            if (serviceProvider is null)
            {
                ModelState.AddModelError(nameof(model.ServiceProviderId), "Service provider selection is invalid.");
            }
            else if (!serviceProvider.IsActive)
            {
                ModelState.AddModelError(nameof(model.ServiceProviderId), "Service provider is inactive.");
            }
        }

        OperationalAsset? operationalAsset = null;
        if (model.OperationalAssetId.HasValue)
        {
            operationalAsset = await _db.OperationalAssets
                .AsNoTracking()
                .FirstOrDefaultAsync(a => a.Id == model.OperationalAssetId.Value);
            if (operationalAsset is null)
            {
                ModelState.AddModelError(nameof(model.OperationalAssetId), "Operational asset selection is invalid.");
            }
            else if (!operationalAsset.IsActive)
            {
                ModelState.AddModelError(nameof(model.OperationalAssetId), "Operational asset is inactive.");
            }
        }

        if (!ModelState.IsValid)
        {
            model.Description = normalizedDescription;
            model.ManualExpenseTypeName = manualExpenseTypeName;
            await PopulateLookupsAsync(createModel: model);
            return View(model);
        }

        CurrencyConversionResult conversion;
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
            model.Description = normalizedDescription;
            model.ManualExpenseTypeName = manualExpenseTypeName;
            await PopulateLookupsAsync(createModel: model);
            return View(model);
        }

        try
        {
            IDbContextTransaction? transaction = null;
            if (_db.Database.IsRelational())
            {
                transaction = await _db.Database.BeginTransactionAsync();
            }

            try
            {
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
                        Category = "Other",
                        IsActive = true
                    };
                    _db.ExpenseTypes.Add(expenseType);
                    await _db.SaveChangesAsync();
                }

                var expense = new ExpenseTransaction
                {
                    ExpenseTypeId = expenseType.Id,
                    ContractId = model.ContractId,
                    ShipmentId = model.ShipmentId,
                    TruckDispatchId = model.TruckDispatchId,
                    TransportLegId = model.TransportLegId,
                    ServiceProviderId = serviceProvider?.Id,
                    OperationalAssetId = operationalAsset?.Id,
                    ExpenseDate = model.ExpenseDate,
                    Amount = model.Amount,
                    Currency = conversion.SourceCurrencyCode,
                    AppliedFxRateToUsd = conversion.AppliedRateToBase,
                    AmountUsd = conversion.ConvertToBase(model.Amount),
                    Description = normalizedDescription,
                    // Phase 1 — مسئول هزینه/کرایه (فقط ثبت/نمایش، به Ledger اثر ندارد).
                    CostResponsibility = model.CostResponsibility
                };

                _db.ExpenseTransactions.Add(expense);
                await _db.SaveChangesAsync();

                var ledgerReference = BuildLedgerReference(expenseType!, expense);
                var ledgerDescription = BuildLedgerDescription(expenseType!, expense);

                var ledgerEntry = new LedgerEntry
                {
                    EntryDate = expense.ExpenseDate,
                    Side = GetExpenseLedgerSide(expense),
                    AmountUsd = expense.AmountUsd,
                    Currency = SystemCurrency.BaseCurrencyCode,
                    SourceAmount = expense.Amount,
                    SourceCurrencyCode = expense.Currency,
                    AppliedFxRateToUsd = expense.AppliedFxRateToUsd,
                    AppliedFxRateDate = conversion.EffectiveDate.Date,
                    AppliedFxRateSource = conversion.SourceDescription,
                    Description = ledgerDescription,
                    SourceType = "Expense",
                    SourceId = expense.Id,
                    Reference = ledgerReference,
                    ContractId = expense.ContractId,
                    ServiceProviderId = expense.ServiceProviderId,
                    ShipmentId = expense.ShipmentId
                };

                _db.LedgerEntries.Add(ledgerEntry);
                await _db.SaveChangesAsync();

                // مرحله ۵ — Dual-write داخل همان Transaction قدیمی.
                if (_expenseAccounting is not null)
                {
                    await _expenseAccounting.TryPostExpenseAsync(expense);
                }

                await _audit.LogAndSaveAsync(
                    nameof(ExpenseTransaction),
                    expense.Id,
                    AuditAction.Insert,
                    diff: AuditDiffFormatter.ForCreate(
                        ("ExpenseTypeId", expense.ExpenseTypeId),
                        ("ContractId", expense.ContractId),
                        ("ShipmentId", expense.ShipmentId),
                        ("TruckDispatchId", expense.TruckDispatchId),
                        ("TransportLegId", expense.TransportLegId),
                        ("ServiceProviderId", expense.ServiceProviderId),
                        ("OperationalAssetId", expense.OperationalAssetId),
                        ("ExpenseDate", expense.ExpenseDate),
                        ("Amount", expense.Amount),
                        ("Currency", expense.Currency),
                        ("AppliedFxRateToUsd", expense.AppliedFxRateToUsd),
                        ("AmountUsd", expense.AmountUsd),
                        ("Description", expense.Description),
                        ("LedgerReference", ledgerEntry.Reference)));

                if (transaction is not null)
                {
                    await transaction.CommitAsync();
                }

                TempData["ok"] = "هزینه با موفقیت ثبت شد.";
                if (TryGetLocalReturnUrl(model.ReturnUrl, out var localReturnUrl))
                {
                    return Redirect(localReturnUrl);
                }

                return RedirectToAction(nameof(Details), new { id = expense.Id });
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
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create expense transaction.");
            ModelState.AddModelError(string.Empty, "ثبت هزینه انجام نشد. دوباره تلاش کنید.");
        }

        model.Description = normalizedDescription;
        model.ManualExpenseTypeName = manualExpenseTypeName;
        await PopulateLookupsAsync(createModel: model);
        return View(model);
    }

    // ----------------------------------------------------------------------------
    // Bulk import of expenses from Excel — Phase 1: preview + validation, then save.
    // Reuses the same ExpenseTransaction + LedgerEntry construction as the single
    // create path (no parallel financial logic). Advanced links (service provider,
    // shipment, dispatch, transport leg) are intentionally left out of import and
    // can be added later by editing the saved expense.
    // ----------------------------------------------------------------------------

    [Authorize(Policy = AuthPolicies.ManageData)]
    public async Task<IActionResult> Import(string? returnUrl = null)
    {
        await PopulateImportLookupsAsync();
        return View(new ExpenseImportViewModel { ReturnUrl = returnUrl });
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    [RequestFormLimits(ValueCountLimit = 20000, MultipartBodyLengthLimit = 52_428_800L)]
    public async Task<IActionResult> ImportPreview(ExpenseImportViewModel model)
    {
        if (model.ImportFile is null || model.ImportFile.Length == 0)
        {
            ModelState.AddModelError(nameof(model.ImportFile), "فایل اکسل مصارف را انتخاب کنید.");
            await PopulateImportLookupsAsync();
            return View("Import", model);
        }

        var importExtension = Path.GetExtension(model.ImportFile.FileName);
        if (!string.Equals(importExtension, ".xlsx", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(importExtension, ".xls", StringComparison.OrdinalIgnoreCase))
        {
            ModelState.AddModelError(nameof(model.ImportFile), "فقط فایل‌های Excel با پسوند .xlsx و .xls پشتیبانی می‌شوند.");
            await PopulateImportLookupsAsync();
            return View("Import", model);
        }

        try
        {
            await using var workbook = await ExcelWorkbookNormalizer.OpenAsync(model.ImportFile);
            model.Rows = ExpenseWorkbookParser.Parse(workbook.Stream).ToList();
        }
        catch (InvalidDataException ex)
        {
            ModelState.AddModelError(nameof(model.ImportFile), ex.Message);
            await PopulateImportLookupsAsync();
            return View("Import", model);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read expense import workbook.");
            ModelState.AddModelError(nameof(model.ImportFile), "خواندن فایل اکسل انجام نشد. ساختار فایل را بررسی کنید.");
            await PopulateImportLookupsAsync();
            return View("Import", model);
        }

        await ResolveAndValidateImportRowsAsync(model);
        ModelState.Clear();
        return View("ImportPreview", model);
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    [RequestFormLimits(ValueCountLimit = 20000)]
    public async Task<IActionResult> ImportConfirm(ExpenseImportViewModel model)
    {
        model.Rows ??= new List<ExpenseImportRowViewModel>();
        await ResolveAndValidateImportRowsAsync(model);

        if (!model.CanConfirm)
        {
            ModelState.Clear();
            if (!model.HasRows)
            {
                ModelState.AddModelError(string.Empty, "ردیفی برای ثبت وجود ندارد. دوباره فایل را وارد کنید.");
            }
            else
            {
                ModelState.AddModelError(string.Empty, "هنوز ردیف‌های دارای خطا وجود دارد. ابتدا فایل را اصلاح و دوباره وارد کنید.");
            }

            return View("ImportPreview", model);
        }

        var expenseTypeIds = model.Rows
            .Where(r => r.ResolvedExpenseTypeId.HasValue)
            .Select(r => r.ResolvedExpenseTypeId!.Value)
            .Distinct()
            .ToList();

        var expenseTypeMap = await _db.ExpenseTypes
            .Where(e => expenseTypeIds.Contains(e.Id))
            .ToDictionaryAsync(e => e.Id);

        IDbContextTransaction? transaction = null;
        if (_db.Database.IsRelational())
        {
            transaction = await _db.Database.BeginTransactionAsync();
        }

        try
        {
            var savedCount = 0;
            foreach (var row in model.Rows)
            {
                if (!row.ResolvedExpenseTypeId.HasValue
                    || !expenseTypeMap.TryGetValue(row.ResolvedExpenseTypeId.Value, out var expenseType)
                    || !row.ExpenseDate.HasValue
                    || !row.Amount.HasValue)
                {
                    continue;
                }

                var conversion = await _currencyConversion.ResolveToBaseAsync(
                    row.Currency ?? SystemCurrency.BaseCurrencyCode,
                    row.ExpenseDate.Value,
                    ResolveManualRateToBase(row));

                var description = string.IsNullOrWhiteSpace(row.Description) ? null : row.Description.Trim();

                var expense = new ExpenseTransaction
                {
                    ExpenseTypeId = expenseType.Id,
                    ContractId = row.ResolvedContractId,
                    ExpenseDate = row.ExpenseDate.Value,
                    Amount = row.Amount.Value,
                    Currency = conversion.SourceCurrencyCode,
                    AppliedFxRateToUsd = conversion.AppliedRateToBase,
                    AmountUsd = conversion.ConvertToBase(row.Amount.Value),
                    Description = description
                };

                _db.ExpenseTransactions.Add(expense);
                await _db.SaveChangesAsync();

                var ledgerEntry = new LedgerEntry
                {
                    EntryDate = expense.ExpenseDate,
                    Side = GetExpenseLedgerSide(expense),
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
                    ContractId = expense.ContractId
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
                        ("ExpenseDate", expense.ExpenseDate),
                        ("Amount", expense.Amount),
                        ("Currency", expense.Currency),
                        ("AppliedFxRateToUsd", expense.AppliedFxRateToUsd),
                        ("AmountUsd", expense.AmountUsd),
                        ("Description", expense.Description),
                        ("Source", "ExcelImport"),
                        ("LedgerReference", ledgerEntry.Reference)));

                savedCount++;
            }

            if (transaction is not null)
            {
                await transaction.CommitAsync();
            }

            TempData["ok"] = $"{savedCount} ردیف مصرف از فایل اکسل با موفقیت ثبت شد.";

            if (TryGetLocalReturnUrl(model.ReturnUrl, out var localReturnUrl))
            {
                return Redirect(localReturnUrl);
            }

            return RedirectToAction(nameof(Index));
        }
        catch (Exception ex)
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync();
            }

            _logger.LogError(ex, "Failed to save imported expenses.");
            ModelState.Clear();
            ModelState.AddModelError(string.Empty, "ثبت مصارف انجام نشد. دوباره تلاش کنید.");
            return View("ImportPreview", model);
        }
    }

    private static decimal? ResolveManualRateToBase(ExpenseImportRowViewModel row)
    {
        if (SystemCurrency.IsBaseCurrency(row.Currency) || !row.RatePerUsd.HasValue || row.RatePerUsd.Value <= 0m)
        {
            return null;
        }

        // The import column is "هر دالر = چند واحد ارز" (units per USD); the ledger
        // convention stores USD per 1 unit, so invert it.
        return decimal.Round(1m / row.RatePerUsd.Value, 6, MidpointRounding.AwayFromZero);
    }

    private async Task ResolveAndValidateImportRowsAsync(ExpenseImportViewModel model)
    {
        model.Rows ??= new List<ExpenseImportRowViewModel>();

        var activeCurrencies = await _db.Currencies
            .AsNoTracking()
            .Where(c => c.IsActive)
            .Select(c => c.Code)
            .ToListAsync();
        if (activeCurrencies.Count == 0)
        {
            activeCurrencies = new List<string> { SystemCurrency.BaseCurrencyCode };
        }

        var currencySet = new HashSet<string>(
            activeCurrencies.Select(c => c.ToUpperInvariant()),
            StringComparer.Ordinal);
        currencySet.Add(SystemCurrency.BaseCurrencyCode);

        var expenseTypes = await _db.ExpenseTypes
            .AsNoTracking()
            .Where(e => e.IsActive)
            .Select(e => new { e.Id, e.Name, e.NamePersian })
            .ToListAsync();

        var typeByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var type in expenseTypes)
        {
            if (!string.IsNullOrWhiteSpace(type.Name))
            {
                typeByName.TryAdd(type.Name.Trim(), type.Id);
            }

            if (!string.IsNullOrWhiteSpace(type.NamePersian))
            {
                typeByName.TryAdd(type.NamePersian.Trim(), type.Id);
            }
        }

        var typeNamesHint = string.Join("، ", expenseTypes
            .Select(t => t.NamePersian ?? t.Name)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Take(8));
        var currencyHint = string.Join("، ", currencySet.Take(8));

        var requestedContractNumbers = model.Rows
            .Select(r => r.ContractNumber?.Trim())
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var contractByNumber = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (requestedContractNumbers.Count > 0)
        {
            var contracts = await _db.Contracts
                .AsNoTracking()
                .Where(c => requestedContractNumbers.Contains(c.ContractNumber))
                .Select(c => new { c.Id, c.ContractNumber })
                .ToListAsync();
            foreach (var contract in contracts)
            {
                contractByNumber.TryAdd(contract.ContractNumber, contract.Id);
            }
        }

        var validCount = 0;
        var errorCount = 0;

        foreach (var row in model.Rows)
        {
            row.Errors = new List<string>();
            row.ExpenseDate = null;
            row.Amount = null;
            row.RatePerUsd = null;
            row.AmountUsd = null;
            row.ResolvedExpenseTypeId = null;
            row.ResolvedExpenseTypeName = null;
            row.ResolvedContractId = null;

            // Date
            if (string.IsNullOrWhiteSpace(row.ExpenseDateText))
            {
                row.Errors.Add("ستون «تاریخ» خالی است.");
            }
            else if (DateTime.TryParse(row.ExpenseDateText, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var date))
            {
                row.ExpenseDate = date.Date;
            }
            else
            {
                row.Errors.Add($"تاریخ «{row.ExpenseDateText}» خوانده نشد. قالب درست مانند 2026-06-24 است.");
            }

            // Expense type
            if (string.IsNullOrWhiteSpace(row.ExpenseTypeName))
            {
                row.Errors.Add("ستون «نوع مصرف» خالی است.");
            }
            else if (typeByName.TryGetValue(row.ExpenseTypeName.Trim(), out var typeId))
            {
                row.ResolvedExpenseTypeId = typeId;
                row.ResolvedExpenseTypeName = row.ExpenseTypeName.Trim();
            }
            else
            {
                row.Errors.Add($"نوع مصرف «{row.ExpenseTypeName.Trim()}» در سیستم نیست. از این نام‌ها استفاده کنید: {typeNamesHint}");
            }

            // Amount
            if (string.IsNullOrWhiteSpace(row.AmountText))
            {
                row.Errors.Add("ستون «مبلغ» خالی است.");
            }
            else if (decimal.TryParse(row.AmountText, NumberStyles.Any, CultureInfo.InvariantCulture, out var amount) && amount > 0m)
            {
                row.Amount = decimal.Round(amount, 4, MidpointRounding.AwayFromZero);
            }
            else
            {
                row.Errors.Add($"مبلغ «{row.AmountText}» معتبر نیست. یک عدد بزرگ‌تر از صفر بنویسید.");
            }

            // Currency
            if (string.IsNullOrWhiteSpace(row.Currency))
            {
                row.Errors.Add("ستون «ارز» خالی است.");
            }
            else
            {
                row.Currency = row.Currency.Trim().ToUpperInvariant();
                if (!currencySet.Contains(row.Currency))
                {
                    row.Errors.Add($"ارز «{row.Currency}» تعریف نشده است. ارزهای مجاز: {currencyHint}");
                }
            }

            // Rate (optional; required only when daily rate is missing for non-USD)
            if (!string.IsNullOrWhiteSpace(row.RatePerUsdText))
            {
                if (decimal.TryParse(row.RatePerUsdText, NumberStyles.Any, CultureInfo.InvariantCulture, out var rate) && rate > 0m)
                {
                    row.RatePerUsd = rate;
                }
                else
                {
                    row.Errors.Add($"نرخ «{row.RatePerUsdText}» معتبر نیست. نرخ باید عدد بزرگ‌تر از صفر باشد (هر دالر = چند واحد ارز).");
                }
            }

            // Contract (optional)
            if (!string.IsNullOrWhiteSpace(row.ContractNumber))
            {
                if (contractByNumber.TryGetValue(row.ContractNumber.Trim(), out var contractId))
                {
                    row.ResolvedContractId = contractId;
                }
                else
                {
                    row.Errors.Add($"قرارداد «{row.ContractNumber.Trim()}» پیدا نشد. شمارهٔ قرارداد را بررسی کنید یا خالی بگذارید.");
                }
            }

            // Compute USD equivalent (and surface any FX problem) only if the basics are valid.
            if (row.Errors.Count == 0 && row.ExpenseDate.HasValue && row.Amount.HasValue)
            {
                try
                {
                    var conversion = await _currencyConversion.ResolveToBaseAsync(
                        row.Currency ?? SystemCurrency.BaseCurrencyCode,
                        row.ExpenseDate.Value,
                        ResolveManualRateToBase(row));
                    row.AmountUsd = conversion.ConvertToBase(row.Amount.Value);
                }
                catch (BusinessRuleException)
                {
                    row.Errors.Add($"نرخ دالر برای ارز «{row.Currency}» در این تاریخ موجود نیست. ستون «نرخ» را پر کنید (هر دالر = چند واحد ارز).");
                }
            }

            if (row.Errors.Count == 0)
            {
                validCount++;
            }
            else
            {
                errorCount++;
            }
        }

        model.ValidCount = validCount;
        model.ErrorCount = errorCount;
    }

    private async Task PopulateImportLookupsAsync()
    {
        ViewBag.ImportExpenseTypeNames = await _db.ExpenseTypes
            .AsNoTracking()
            .Where(e => e.IsActive)
            .OrderBy(e => e.Category)
            .ThenBy(e => e.Code)
            .Select(e => e.NamePersian ?? e.Name)
            .ToListAsync();

        var currencies = await _db.Currencies
            .AsNoTracking()
            .Where(c => c.IsActive)
            .OrderBy(c => c.Code)
            .Select(c => c.Code)
            .ToListAsync();
        if (currencies.Count == 0)
        {
            currencies = new List<string> { SystemCurrency.BaseCurrencyCode };
        }

        ViewBag.ImportCurrencies = currencies;
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    public async Task<IActionResult> Edit(int id, string? returnUrl = null)
    {
        var expense = await _db.ExpenseTransactions
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == id);

        if (expense is null)
        {
            return NotFound();
        }

        if (expense.IsCancelled)
        {
            TempData["err"] = "هزینه لغوشده قابل ویرایش نیست.";
            if (TryGetLocalReturnUrl(returnUrl, out var localReturnUrl))
            {
                return Redirect(localReturnUrl);
            }

            return RedirectToAction(nameof(Details), new { id });
        }

        var model = new ExpenseCreateViewModel
        {
            Id = expense.Id,
            ExpenseTypeId = expense.ExpenseTypeId,
            ContractId = expense.ContractId,
            ShipmentId = expense.ShipmentId,
            TruckDispatchId = expense.TruckDispatchId,
            TransportLegId = expense.TransportLegId,
            ServiceProviderId = expense.ServiceProviderId,
            OperationalAssetId = expense.OperationalAssetId,
            ExpenseDate = expense.ExpenseDate,
            Amount = expense.Amount,
            Currency = expense.Currency,
            AppliedFxRateToUsd = expense.AppliedFxRateToUsd,
            Description = expense.Description ?? string.Empty,
            CostResponsibility = expense.CostResponsibility,
            ReturnUrl = returnUrl
        };

        await PopulateLookupsAsync(createModel: model);
        ViewData["ExpenseFormMode"] = "Edit";
        return View("Create", model);
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, ExpenseCreateViewModel model)
    {
        if (id != model.Id)
        {
            return BadRequest();
        }

        var expense = await _db.ExpenseTransactions
            .Include(e => e.ExpenseType)
            .FirstOrDefaultAsync(e => e.Id == id);

        if (expense is null)
        {
            return NotFound();
        }

        if (expense.IsCancelled)
        {
            ModelState.AddModelError(string.Empty, "هزینه لغوشده قابل ویرایش نیست.");
            await PopulateLookupsAsync(createModel: model);
            ViewData["ExpenseFormMode"] = "Edit";
            return View("Create", model);
        }

        NormalizeCreateModel(model);
        var normalizedDescription = model.Description?.Trim() ?? string.Empty;
        var manualExpenseTypeName = model.ManualExpenseTypeName?.Trim() ?? string.Empty;

        ExpenseType? expenseType = null;
        if (model.ExpenseTypeId.HasValue)
        {
            expenseType = await _db.ExpenseTypes
                .AsNoTracking()
                .FirstOrDefaultAsync(e => e.Id == model.ExpenseTypeId.Value && e.IsActive);
            if (expenseType is null)
            {
                ModelState.AddModelError(nameof(model.ExpenseTypeId), "نوع مصرف انتخاب‌شده معتبر نیست.");
            }
        }
        else if (!string.IsNullOrWhiteSpace(manualExpenseTypeName))
        {
            expenseType = await FindExpenseTypeByManualNameAsync(manualExpenseTypeName);
        }
        else
        {
            ModelState.AddModelError(nameof(model.ManualExpenseTypeName), "نوع مصرف را از لیست انتخاب کنید یا دستی وارد کنید.");
        }

        InventoryTransportLeg? transportLeg = null;
        if (model.TransportLegId.HasValue)
        {
            transportLeg = await _db.InventoryTransportLegs
                .AsNoTracking()
                .FirstOrDefaultAsync(l => l.Id == model.TransportLegId.Value);
            if (transportLeg is null)
            {
                ModelState.AddModelError(nameof(model.TransportLegId), "Transport leg selection is invalid.");
            }
            else
            {
                if (!model.ContractId.HasValue)
                {
                    model.ContractId = transportLeg.SourcePurchaseContractId;
                }
                else if (model.ContractId.Value != transportLeg.SourcePurchaseContractId)
                {
                    ModelState.AddModelError(nameof(model.TransportLegId), "Transport leg must match the selected purchase contract.");
                }

                if (transportLeg.ShipmentId.HasValue && !model.ShipmentId.HasValue)
                {
                    model.ShipmentId = transportLeg.ShipmentId;
                }
                else if (transportLeg.ShipmentId.HasValue
                         && model.ShipmentId.HasValue
                         && model.ShipmentId.Value != transportLeg.ShipmentId.Value)
                {
                    ModelState.AddModelError(nameof(model.ShipmentId), "Shipment must match the selected transport leg.");
                }
            }
        }

        Contract? contract = null;
        if (model.ContractId.HasValue)
        {
            contract = await _db.Contracts
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == model.ContractId.Value);
            if (contract is null)
            {
                ModelState.AddModelError(nameof(model.ContractId), "قرارداد انتخاب‌شده معتبر نیست.");
            }
        }

        Shipment? shipment = null;
        if (model.ShipmentId.HasValue)
        {
            shipment = await _db.Shipments
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == model.ShipmentId.Value);
            if (shipment is null)
            {
                ModelState.AddModelError(nameof(model.ShipmentId), "Shipment انتخاب‌شده معتبر نیست.");
            }
            else if (contract is not null
                     && !await ShipmentAllowsContractAsync(shipment.Id, shipment.ContractId, contract.Id))
            {
                ModelState.AddModelError(nameof(model.ShipmentId), "Shipment انتخاب‌شده با قرارداد انتخابی هم‌خوان نیست.");
            }
        }

        TruckDispatch? truckDispatch = null;
        if (model.TruckDispatchId.HasValue)
        {
            truckDispatch = await _db.TruckDispatches
                .AsNoTracking()
                .FirstOrDefaultAsync(d => d.Id == model.TruckDispatchId.Value);
            if (truckDispatch is null)
            {
                ModelState.AddModelError(nameof(model.TruckDispatchId), "دیسپچ انتخاب‌شده معتبر نیست.");
            }
            else if (contract is not null && truckDispatch.ContractId != contract.Id)
            {
                ModelState.AddModelError(nameof(model.TruckDispatchId), "دیسپچ انتخاب‌شده با قرارداد انتخابی هم‌خوان نیست.");
            }
        }

        if (shipment is not null
            && truckDispatch is not null
            && !await ShipmentAllowsContractAsync(shipment.Id, shipment.ContractId, truckDispatch.ContractId))
        {
            ModelState.AddModelError(string.Empty, "Shipment و دیسپچ انتخاب‌شده به یک قرارداد واحد اشاره نمی‌کنند.");
        }

        if (string.IsNullOrWhiteSpace(normalizedDescription))
        {
            ModelState.AddModelError(nameof(model.Description), "ثبت شرح یا مرجع برای trace هزینه الزامی است.");
        }

        var hasActiveCurrencies = await _db.Currencies.AsNoTracking().AnyAsync(c => c.IsActive);
        if (hasActiveCurrencies
            && !await _db.Currencies.AsNoTracking().AnyAsync(c => c.Code == model.Currency && c.IsActive))
        {
            ModelState.AddModelError(nameof(model.Currency), "Invalid currency selection.");
        }

        ServiceProviderEntity? serviceProvider = null;
        if (model.ServiceProviderId.HasValue)
        {
            serviceProvider = await _db.ServiceProviders
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == model.ServiceProviderId.Value);
            if (serviceProvider is null)
            {
                ModelState.AddModelError(nameof(model.ServiceProviderId), "Service provider selection is invalid.");
            }
            else if (!serviceProvider.IsActive)
            {
                ModelState.AddModelError(nameof(model.ServiceProviderId), "Service provider is inactive.");
            }
        }

        OperationalAsset? operationalAsset = null;
        if (model.OperationalAssetId.HasValue)
        {
            operationalAsset = await _db.OperationalAssets
                .AsNoTracking()
                .FirstOrDefaultAsync(a => a.Id == model.OperationalAssetId.Value);
            if (operationalAsset is null)
            {
                ModelState.AddModelError(nameof(model.OperationalAssetId), "Operational asset selection is invalid.");
            }
            else if (!operationalAsset.IsActive)
            {
                ModelState.AddModelError(nameof(model.OperationalAssetId), "Operational asset is inactive.");
            }
        }

        if (!ModelState.IsValid)
        {
            model.Description = normalizedDescription;
            model.ManualExpenseTypeName = manualExpenseTypeName;
            await PopulateLookupsAsync(createModel: model);
            ViewData["ExpenseFormMode"] = "Edit";
            return View("Create", model);
        }

        CurrencyConversionResult conversion;
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
            model.Description = normalizedDescription;
            model.ManualExpenseTypeName = manualExpenseTypeName;
            await PopulateLookupsAsync(createModel: model);
            ViewData["ExpenseFormMode"] = "Edit";
            return View("Create", model);
        }

        var ledgerEntry = await _db.LedgerEntries
            .Where(l => l.SourceType == "Expense" && l.SourceId == expense.Id)
            .OrderBy(l => l.Id)
            .FirstOrDefaultAsync();

        var previousExpense = new
        {
            expense.ExpenseTypeId,
            expense.ContractId,
            expense.ShipmentId,
            expense.TruckDispatchId,
            expense.TransportLegId,
            expense.ServiceProviderId,
            expense.OperationalAssetId,
            expense.ExpenseDate,
            expense.Amount,
            expense.Currency,
            expense.AppliedFxRateToUsd,
            expense.AmountUsd,
            expense.Description
        };

        var ledgerIsNew = ledgerEntry is null;
        var previousLedger = ledgerEntry is null
            ? null
            : new
            {
                ledgerEntry.EntryDate,
                ledgerEntry.Side,
                ledgerEntry.AmountUsd,
                ledgerEntry.SourceAmount,
                ledgerEntry.SourceCurrencyCode,
                ledgerEntry.AppliedFxRateToUsd,
                ledgerEntry.AppliedFxRateDate,
                ledgerEntry.AppliedFxRateSource,
                ledgerEntry.Description,
                ledgerEntry.SourceType,
                ledgerEntry.SourceId,
                ledgerEntry.Reference,
                ledgerEntry.ContractId,
                ledgerEntry.ServiceProviderId,
                ledgerEntry.ShipmentId
            };

        IDbContextTransaction? transaction = null;
        if (_db.Database.IsRelational())
        {
            transaction = await _db.Database.BeginTransactionAsync();
        }

        try
        {
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
                    Category = "Other",
                    IsActive = true
                };
                _db.ExpenseTypes.Add(expenseType);
                await _db.SaveChangesAsync();
            }

            if (ledgerEntry is null)
            {
                ledgerEntry = new LedgerEntry();
                _db.LedgerEntries.Add(ledgerEntry);
            }

            expense.ExpenseTypeId = expenseType.Id;
            expense.ContractId = model.ContractId;
            expense.ShipmentId = model.ShipmentId;
            expense.TruckDispatchId = model.TruckDispatchId;
            expense.TransportLegId = model.TransportLegId;
            expense.ServiceProviderId = serviceProvider?.Id;
            expense.OperationalAssetId = operationalAsset?.Id;
            expense.ExpenseDate = model.ExpenseDate.Date;
            expense.Amount = model.Amount;
            expense.Currency = conversion.SourceCurrencyCode;
            expense.AppliedFxRateToUsd = conversion.AppliedRateToBase;
            expense.AmountUsd = conversion.ConvertToBase(model.Amount);
            expense.Description = normalizedDescription;
            expense.CostResponsibility = model.CostResponsibility;

            ledgerEntry.EntryDate = expense.ExpenseDate;
            ledgerEntry.Side = GetExpenseLedgerSide(expense);
            ledgerEntry.AmountUsd = expense.AmountUsd;
            ledgerEntry.Currency = SystemCurrency.BaseCurrencyCode;
            ledgerEntry.SourceAmount = expense.Amount;
            ledgerEntry.SourceCurrencyCode = expense.Currency;
            ledgerEntry.AppliedFxRateToUsd = expense.AppliedFxRateToUsd;
            ledgerEntry.AppliedFxRateDate = conversion.EffectiveDate.Date;
            ledgerEntry.AppliedFxRateSource = conversion.SourceDescription;
            ledgerEntry.Description = BuildLedgerDescription(expenseType, expense);
            ledgerEntry.SourceType = "Expense";
            ledgerEntry.SourceId = expense.Id;
            ledgerEntry.Reference = BuildLedgerReference(expenseType, expense);
            ledgerEntry.ContractId = expense.ContractId;
            ledgerEntry.ServiceProviderId = expense.ServiceProviderId;
            ledgerEntry.ShipmentId = expense.ShipmentId;

            await _db.SaveChangesAsync();

            await _audit.LogAsync(
                nameof(ExpenseTransaction),
                expense.Id,
                AuditAction.Update,
                diff: AuditDiffFormatter.ForUpdate(
                    ("ExpenseTypeId", previousExpense.ExpenseTypeId, expense.ExpenseTypeId),
                    ("ContractId", previousExpense.ContractId, expense.ContractId),
                    ("ShipmentId", previousExpense.ShipmentId, expense.ShipmentId),
                    ("TruckDispatchId", previousExpense.TruckDispatchId, expense.TruckDispatchId),
                    ("TransportLegId", previousExpense.TransportLegId, expense.TransportLegId),
                    ("ServiceProviderId", previousExpense.ServiceProviderId, expense.ServiceProviderId),
                    ("OperationalAssetId", previousExpense.OperationalAssetId, expense.OperationalAssetId),
                    ("ExpenseDate", previousExpense.ExpenseDate, expense.ExpenseDate),
                    ("Amount", previousExpense.Amount, expense.Amount),
                    ("Currency", previousExpense.Currency, expense.Currency),
                    ("AppliedFxRateToUsd", previousExpense.AppliedFxRateToUsd, expense.AppliedFxRateToUsd),
                    ("AmountUsd", previousExpense.AmountUsd, expense.AmountUsd),
                    ("Description", previousExpense.Description, expense.Description)));

            await _audit.LogAsync(
                nameof(LedgerEntry),
                ledgerEntry.Id,
                ledgerIsNew ? AuditAction.Insert : AuditAction.Update,
                diff: ledgerIsNew || previousLedger is null
                    ? AuditDiffFormatter.ForCreate(
                        ("EntryDate", ledgerEntry.EntryDate),
                        ("Side", ledgerEntry.Side),
                        ("AmountUsd", ledgerEntry.AmountUsd),
                        ("SourceAmount", ledgerEntry.SourceAmount),
                        ("SourceCurrencyCode", ledgerEntry.SourceCurrencyCode),
                        ("AppliedFxRateToUsd", ledgerEntry.AppliedFxRateToUsd),
                        ("SourceType", ledgerEntry.SourceType),
                        ("SourceId", ledgerEntry.SourceId),
                        ("Reference", ledgerEntry.Reference))
                    : AuditDiffFormatter.ForUpdate(
                        ("EntryDate", previousLedger.EntryDate, ledgerEntry.EntryDate),
                        ("Side", previousLedger.Side, ledgerEntry.Side),
                        ("AmountUsd", previousLedger.AmountUsd, ledgerEntry.AmountUsd),
                        ("SourceAmount", previousLedger.SourceAmount, ledgerEntry.SourceAmount),
                        ("SourceCurrencyCode", previousLedger.SourceCurrencyCode, ledgerEntry.SourceCurrencyCode),
                        ("AppliedFxRateToUsd", previousLedger.AppliedFxRateToUsd, ledgerEntry.AppliedFxRateToUsd),
                        ("AppliedFxRateDate", previousLedger.AppliedFxRateDate, ledgerEntry.AppliedFxRateDate),
                        ("AppliedFxRateSource", previousLedger.AppliedFxRateSource, ledgerEntry.AppliedFxRateSource),
                        ("Description", previousLedger.Description, ledgerEntry.Description),
                        ("SourceType", previousLedger.SourceType, ledgerEntry.SourceType),
                        ("SourceId", previousLedger.SourceId, ledgerEntry.SourceId),
                        ("Reference", previousLedger.Reference, ledgerEntry.Reference),
                        ("ContractId", previousLedger.ContractId, ledgerEntry.ContractId),
                        ("ServiceProviderId", previousLedger.ServiceProviderId, ledgerEntry.ServiceProviderId),
                        ("ShipmentId", previousLedger.ShipmentId, ledgerEntry.ShipmentId)));

            await _db.SaveChangesAsync();

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

            _logger.LogError(ex, "Failed to update expense transaction {ExpenseTransactionId}.", id);
            ModelState.AddModelError(string.Empty, "ویرایش هزینه انجام نشد. دوباره تلاش کنید.");
            model.Description = normalizedDescription;
            model.ManualExpenseTypeName = manualExpenseTypeName;
            await PopulateLookupsAsync(createModel: model);
            ViewData["ExpenseFormMode"] = "Edit";
            return View("Create", model);
        }

        TempData["ok"] = "هزینه با موفقیت ویرایش شد.";
        if (TryGetLocalReturnUrl(model.ReturnUrl, out var redirectUrl))
        {
            return Redirect(redirectUrl);
        }

        return RedirectToAction(nameof(Details), new { id = expense.Id });
    }

    public async Task<IActionResult> Details(int id, string? returnUrl = null)
    {
        var expense = await _db.ExpenseTransactions
            .Include(e => e.ExpenseType)
            .Include(e => e.Contract)
            .Include(e => e.Shipment)
            .Include(e => e.TruckDispatch)
                .ThenInclude(d => d!.Truck)
            .Include(e => e.TransportLeg)
            .Include(e => e.ServiceProvider)
            .Include(e => e.OperationalAsset)
            .Include(e => e.ExpenseBatch)
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == id);

        if (expense is null)
            return NotFound();

        var ledgerEntry = await _db.LedgerEntries
            .AsNoTracking()
            .Where(l => l.SourceType == "Expense" && l.SourceId == id)
            .OrderByDescending(l => l.Id)
            .FirstOrDefaultAsync();

        ViewBag.ReturnUrl = TryGetLocalReturnUrl(returnUrl, out var localReturnUrl) ? localReturnUrl : null;

        return View(new ExpenseDetailsViewModel
        {
            Id = expense.Id,
            ExpenseDate = expense.ExpenseDate,
            ExpenseTypeName = expense.ExpenseType?.NamePersian ?? expense.ExpenseType?.Name ?? string.Empty,
            ContractNumber = expense.Contract?.ContractNumber,
            ShipmentCode = expense.Shipment?.ShipmentCode,
            TruckDispatchLabel = expense.TruckDispatch is null
                ? null
                : $"#{expense.TruckDispatch.Id} - {expense.TruckDispatch.Truck?.PlateNumber ?? "بدون پلاک"}",
            TransportLegLabel = expense.TransportLeg is null
                ? null
                : $"#{expense.TransportLeg.Id} - {expense.TransportLeg.WagonNumber ?? expense.TransportLeg.RwbNo ?? "Transport leg"}",
            ServiceProviderId = expense.ServiceProviderId,
            ServiceProviderName = expense.ServiceProvider?.Name,
            OperationalAssetId = expense.OperationalAssetId,
            OperationalAssetName = expense.OperationalAsset is null
                ? null
                : $"{expense.OperationalAsset.AssetCode} - {expense.OperationalAsset.Name}",
            Amount = expense.Amount,
            Currency = expense.Currency,
            AppliedFxRateToUsd = expense.AppliedFxRateToUsd,
            AmountUsd = expense.AmountUsd,
            Description = expense.Description,
            LedgerEntryId = ledgerEntry?.Id,
            LedgerReference = ledgerEntry?.Reference,
            LedgerDescription = ledgerEntry?.Description,
            LedgerAmountUsd = ledgerEntry?.AmountUsd,
            LedgerSideName = ledgerEntry is null ? null : UiText.LedgerSideName(HttpContext, ledgerEntry.Side),
            ExpenseBatchId = expense.ExpenseBatchId,
            ExpenseBatchNumber = expense.ExpenseBatch?.BatchNumber
        });
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

        var suffix = expense.Description.Trim();
        var combined = $"{prefix} | {suffix}";
        return combined.Length <= 200 ? combined : combined[..200];
    }

    private static LedgerSide GetExpenseLedgerSide(ExpenseTransaction expense)
        => expense.ServiceProviderId.HasValue ? LedgerSide.Credit : LedgerSide.Debit;

    private static LedgerSide ReverseSide(LedgerSide side)
        => side == LedgerSide.Credit ? LedgerSide.Debit : LedgerSide.Credit;

    private async Task<bool> ShipmentAllowsContractAsync(int shipmentId, int? primaryContractId, int contractId)
    {
        if (primaryContractId == contractId)
        {
            return true;
        }

        var hasShipmentContracts = await _db.ShipmentContracts
            .AsNoTracking()
            .AnyAsync(sc => sc.ShipmentId == shipmentId);

        if (!hasShipmentContracts && !primaryContractId.HasValue)
        {
            return true;
        }

        return await _db.ShipmentContracts
            .AsNoTracking()
            .AnyAsync(sc => sc.ShipmentId == shipmentId && sc.ContractId == contractId);
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

    [Authorize(Policy = AuthPolicies.ManageData)]
    public async Task<IActionResult> CustomsBatch(int? contractId = null, int? dispatchId = null, string? returnUrl = null)
    {
        var expenseTypes = await _db.ExpenseTypes
            .AsNoTracking()
            .Where(e => e.IsActive)
            .OrderBy(e => e.Category)
            .ThenBy(e => e.Code)
            .ToListAsync();

        var model = new CustomsBatchViewModel
        {
            ExpenseDate = _businessClock.Today,
            Currency = "AFN",
            ContractId = contractId ?? 0,
            TruckDispatchId = dispatchId,
            ReturnUrl = returnUrl,
            Rows = expenseTypes.Select(e => new CustomsBatchRowInput
            {
                ExpenseTypeId = e.Id,
                ExpenseTypeName = e.NamePersian ?? e.Name
            }).ToList()
        };

        await PopulateCustomsBatchLookupsAsync(model);
        return View(model);
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> CustomsBatch(CustomsBatchViewModel model)
    {
        if (model.ContractId <= 0)
            ModelState.AddModelError(nameof(model.ContractId), "انتخاب قرارداد الزامی است.");

        var activeRows = (model.Rows ?? []).Where(r => r.Amount.HasValue && r.Amount.Value > 0).ToList();
        if (activeRows.Count == 0)
            ModelState.AddModelError(string.Empty, "حداقل یک ردیف با مبلغ بزرگ‌تر از صفر وارد کنید.");

        if (!ModelState.IsValid)
        {
            await PopulateCustomsBatchLookupsAsync(model);
            return View(model);
        }

        CurrencyConversionResult conversion;
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
            await PopulateCustomsBatchLookupsAsync(model);
            return View(model);
        }

        var expenseTypeIds = activeRows.Select(r => r.ExpenseTypeId).Distinct().ToList();
        var expenseTypeMap = await _db.ExpenseTypes
            .AsNoTracking()
            .Where(e => expenseTypeIds.Contains(e.Id) && e.IsActive)
            .ToDictionaryAsync(e => e.Id);

        var descriptionBase = string.IsNullOrWhiteSpace(model.Description)
            ? $"مصارف گمرکی {DateDisplay.Date(model.ExpenseDate)}"
            : model.Description.Trim();

        IDbContextTransaction? transaction = null;
        if (_db.Database.IsRelational())
            transaction = await _db.Database.BeginTransactionAsync();

        try
        {
            int savedCount = 0;
            foreach (var row in activeRows)
            {
                if (!expenseTypeMap.TryGetValue(row.ExpenseTypeId, out var expenseType)) continue;

                var expense = new ExpenseTransaction
                {
                    ExpenseTypeId = row.ExpenseTypeId,
                    ContractId = model.ContractId > 0 ? model.ContractId : null,
                    TruckDispatchId = model.TruckDispatchId,
                    ExpenseDate = model.ExpenseDate,
                    Amount = row.Amount!.Value,
                    Currency = conversion.SourceCurrencyCode,
                    AppliedFxRateToUsd = conversion.AppliedRateToBase,
                    AmountUsd = conversion.ConvertToBase(row.Amount.Value),
                    Description = descriptionBase,
                    // Phase 1 — مسئول مصارف گمرکی (فقط ثبت/نمایش، به Ledger اثر ندارد).
                    CostResponsibility = model.CostResponsibility
                };

                _db.ExpenseTransactions.Add(expense);
                await _db.SaveChangesAsync();

                var ledgerEntry = new LedgerEntry
                {
                    EntryDate = expense.ExpenseDate,
                    Side = LedgerSide.Debit,
                    AmountUsd = expense.AmountUsd,
                    Currency = SystemCurrency.BaseCurrencyCode,
                    SourceAmount = expense.Amount,
                    SourceCurrencyCode = expense.Currency,
                    AppliedFxRateToUsd = expense.AppliedFxRateToUsd,
                    AppliedFxRateDate = conversion.EffectiveDate.Date,
                    AppliedFxRateSource = conversion.SourceDescription,
                    Description = $"مصارف گمرکی {expenseType.NamePersian ?? expenseType.Name} - {descriptionBase}",
                    SourceType = "Expense",
                    SourceId = expense.Id,
                    Reference = $"{expenseType.Code}-{expense.Id}",
                    ContractId = expense.ContractId
                };

                _db.LedgerEntries.Add(ledgerEntry);
                await _db.SaveChangesAsync();
                savedCount++;
            }

            if (transaction is not null)
                await transaction.CommitAsync();

            TempData["ok"] = $"{savedCount} ردیف مصارف گمرکی با موفقیت ثبت شد.";

            if (TryGetLocalReturnUrl(model.ReturnUrl, out var localReturnUrl))
                return Redirect(localReturnUrl);

            return RedirectToAction(nameof(Index));
        }
        catch
        {
            if (transaction is not null)
                await transaction.RollbackAsync();
            _logger.LogError("Failed to save customs batch expenses.");
            ModelState.AddModelError(string.Empty, "خطا در ثبت هزینه‌ها. دوباره تلاش کنید.");
        }

        await PopulateCustomsBatchLookupsAsync(model);
        return View(model);
    }

    // ------------------------------------------------------------------------------
    // ثبت مصرف گروهی — یک رکورد اصلی (ExpenseBatch) + سهم هر عملیات به‌صورت
    // ExpenseTransaction عادی با Ledger خودش؛ هیچ منطق مالی موازی ساخته نمی‌شود.
    // ------------------------------------------------------------------------------

    private static string LegVehicleKind(LoadingTransportType type) => type switch
    {
        LoadingTransportType.Wagon => "واگن",
        LoadingTransportType.Truck => "موتر",
        LoadingTransportType.Vessel => "کشتی",
        _ => "نامشخص"
    };

    private static string BuildRoute(string? source, string? destination, string? fallback)
    {
        if (!string.IsNullOrWhiteSpace(source) || !string.IsNullOrWhiteSpace(destination))
        {
            return $"{source ?? "؟"} ← {destination ?? "؟"}";
        }

        return string.IsNullOrWhiteSpace(fallback) ? "-" : fallback.Trim();
    }

    private async Task<List<GroupExpenseOperationItem>> LoadInProgressOperationsAsync()
    {
        var legs = await _db.InventoryTransportLegs
            .AsNoTracking()
            .Where(l => l.Status == InventoryTransportLegStatus.Loaded
                        || l.Status == InventoryTransportLegStatus.InTransit)
            .OrderByDescending(l => l.LoadedDate)
            .ThenByDescending(l => l.Id)
            .Select(l => new
            {
                l.Id,
                l.TransportType,
                l.WagonNumber,
                l.RwbNo,
                TruckPlate = l.Truck != null ? l.Truck.PlateNumber : null,
                SourceName = l.SourceTerminal != null ? l.SourceTerminal.Name : null,
                DestinationName = l.DestinationTerminal != null
                    ? l.DestinationTerminal.Name
                    : (l.DestinationLocation != null ? l.DestinationLocation.Name : null),
                l.RouteDescription,
                l.QuantityMt,
                l.Status,
                l.LoadedDate
            })
            .ToListAsync();

        var dispatches = await _db.TruckDispatches
            .AsNoTracking()
            .Where(d => d.Status == DispatchStatus.Loaded || d.Status == DispatchStatus.InTransit)
            .OrderByDescending(d => d.DispatchDate)
            .ThenByDescending(d => d.Id)
            .Select(d => new
            {
                d.Id,
                TruckPlate = d.Truck != null ? d.Truck.PlateNumber : null,
                DestinationName = d.DestinationLocation != null ? d.DestinationLocation.Name : null,
                ContractNumber = d.Contract != null ? d.Contract.ContractNumber : null,
                d.LoadedQuantityMt,
                d.Status,
                d.DispatchDate
            })
            .ToListAsync();

        var items = new List<GroupExpenseOperationItem>();

        items.AddRange(legs.Select(l => new GroupExpenseOperationItem
        {
            Kind = "Leg",
            Id = l.Id,
            OperationLabel = "حمل از موجودی",
            VehicleKind = LegVehicleKind(l.TransportType),
            Number = l.WagonNumber ?? l.RwbNo ?? l.TruckPlate ?? $"#{l.Id}",
            Route = BuildRoute(l.SourceName, l.DestinationName, l.RouteDescription),
            QuantityMt = l.QuantityMt,
            StatusLabel = l.Status == InventoryTransportLegStatus.InTransit ? "در راه" : "بارگیری‌شده",
            MoveDate = l.LoadedDate
        }));

        items.AddRange(dispatches.Select(d => new GroupExpenseOperationItem
        {
            Kind = "Dispatch",
            Id = d.Id,
            OperationLabel = "ارسال موتر",
            VehicleKind = "موتر",
            Number = d.TruckPlate ?? $"#{d.Id}",
            Route = BuildRoute(d.ContractNumber, d.DestinationName, null),
            QuantityMt = d.LoadedQuantityMt,
            StatusLabel = d.Status == DispatchStatus.InTransit ? "در راه" : "بارگیری‌شده",
            MoveDate = d.DispatchDate
        }));

        return items
            .OrderByDescending(i => i.MoveDate)
            .ThenByDescending(i => i.Id)
            .ToList();
    }

    private async Task PopulateGroupExpenseLookupsAsync(GroupExpenseCreateViewModel model)
    {
        var expenseTypeOptions = await _db.ExpenseTypes
            .AsNoTracking()
            .Where(e => e.IsActive)
            .OrderBy(e => e.Category)
            .ThenBy(e => e.Code)
            .Select(e => new { e.Id, DisplayName = (e.NamePersian ?? e.Name) + " (" + e.Code + ")" })
            .ToListAsync();
        ViewBag.ExpenseTypes = new SelectList(expenseTypeOptions, "Id", "DisplayName",
            model.ExpenseTypeId > 0 ? model.ExpenseTypeId : null);

        ViewBag.ExpenseTypeNames = await _db.ExpenseTypes
            .AsNoTracking()
            .Where(e => e.IsActive)
            .OrderBy(e => e.NamePersian ?? e.Name)
            .Select(e => e.NamePersian ?? e.Name)
            .ToListAsync();

        var providers = await _db.ServiceProviders
            .AsNoTracking()
            .Where(p => p.IsActive)
            .OrderBy(p => p.Name)
            .Select(p => new { p.Id, p.Name })
            .ToListAsync();
        ViewBag.ServiceProviders = new SelectList(providers, "Id", "Name", model.ServiceProviderId);

        ViewBag.Currencies = new SelectList(
            await _db.Currencies.AsNoTracking().Where(c => c.IsActive).OrderBy(c => c.Code)
                .Select(c => new { c.Code }).ToListAsync(),
            "Code", "Code", model.Currency);
    }

    // محاسبهٔ سهم هر عملیات سمت سرور (به ورودی کلاینت اعتماد نمی‌شود).
    // خطای rounding روی سطر آخر تصحیح می‌شود تا Σ سهم‌ها == مبلغ کل بماند.
    private static bool TryComputeShares(
        GroupExpenseCreateViewModel model,
        IReadOnlyList<(GroupExpenseSelectedInput Input, decimal QuantityMt)> items,
        out List<decimal> shares,
        out decimal total,
        out string? error)
    {
        shares = [];
        total = 0m;
        error = null;
        var count = items.Count;

        switch (model.AllocationMethod)
        {
            case ExpenseAllocationMethod.FixedPerOperation:
                if (!model.AmountPerOperation.HasValue || model.AmountPerOperation.Value <= 0)
                {
                    error = "مبلغ برای هر عملیات باید بزرگ‌تر از صفر باشد.";
                    return false;
                }
                shares = Enumerable.Repeat(Math.Round(model.AmountPerOperation.Value, 2), count).ToList();
                total = shares.Sum();
                return true;

            case ExpenseAllocationMethod.EqualSplit:
                if (!model.TotalAmount.HasValue || model.TotalAmount.Value <= 0)
                {
                    error = "مبلغ کل برای تقسیم باید بزرگ‌تر از صفر باشد.";
                    return false;
                }
                total = Math.Round(model.TotalAmount.Value, 2);

                var equal = Math.Round(total / count, 2);
                shares = Enumerable.Repeat(equal, count).ToList();

                // تصحیح rounding: تفاوت روی آخرین سهم.
                var diff = total - shares.Sum();
                shares[^1] = Math.Round(shares[^1] + diff, 2);
                if (shares.Any(s => s <= 0))
                {
                    error = "با این مبلغ و روش تقسیم، سهم بعضی عملیات‌ها صفر یا منفی می‌شود.";
                    return false;
                }
                return true;

            case ExpenseAllocationMethod.ByQuantity:
                // سهم هر عملیات = نرخ فی تن × مقدار (تن) همان حمل. مثال: نرخ ۲ و حمل ۴۰ تن → سهم ۸۰.
                if (!model.RatePerTon.HasValue || model.RatePerTon.Value <= 0)
                {
                    error = "نرخ فی تن باید بزرگ‌تر از صفر باشد.";
                    return false;
                }
                if (items.Any(i => i.QuantityMt <= 0))
                {
                    error = "مقدار (تن) بعضی از عملیات‌های انتخاب‌شده صفر است؛ محاسبه بر اساس مقدار ممکن نیست.";
                    return false;
                }
                var rate = model.RatePerTon.Value;
                shares = items.Select(i => Math.Round(rate * i.QuantityMt, 2)).ToList();
                total = shares.Sum();
                return true;

            case ExpenseAllocationMethod.Manual:
                if (items.Any(i => !i.Input.ManualAmount.HasValue || i.Input.ManualAmount.Value <= 0))
                {
                    error = "در روش دستی، مبلغ هر عملیات باید بزرگ‌تر از صفر وارد شود.";
                    return false;
                }
                shares = items.Select(i => Math.Round(i.Input.ManualAmount!.Value, 2)).ToList();
                total = shares.Sum();
                return true;

            default:
                error = "روش تقسیم نامعتبر است.";
                return false;
        }
    }

    // مصارف گروهی فعال (لغونشده) برای نمایش و لغو در فرم ثبت مصرف گروهی.
    private async Task<List<GroupExpenseBatchListItem>> LoadActiveGroupBatchesAsync()
    {
        return await _db.ExpenseBatches
            .AsNoTracking()
            .Where(b => !b.IsCancelled)
            .OrderByDescending(b => b.ExpenseDate)
            .ThenByDescending(b => b.Id)
            .Select(b => new GroupExpenseBatchListItem
            {
                Id = b.Id,
                BatchNumber = b.BatchNumber,
                ExpenseTypeName = b.ExpenseType != null ? (b.ExpenseType.NamePersian ?? b.ExpenseType.Name) : "-",
                ServiceProviderName = b.ServiceProvider != null ? b.ServiceProvider.Name : null,
                ExpenseDate = b.ExpenseDate,
                Currency = b.Currency,
                TotalAmount = b.TotalAmount,
                TotalAmountUsd = b.TotalAmountUsd,
                OperationCount = b.OperationCount
            })
            .ToListAsync();
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    public async Task<IActionResult> CreateGroup(string? returnUrl = null)
    {
        var model = new GroupExpenseCreateViewModel
        {
            ExpenseDate = _businessClock.Today,
            Currency = SystemCurrency.BaseCurrencyCode,
            ReturnUrl = returnUrl
        };

        await PopulateGroupExpenseLookupsAsync(model);
        ViewBag.Operations = await LoadInProgressOperationsAsync();
        ViewBag.RegisteredGroupBatches = await LoadActiveGroupBatchesAsync();
        return View(model);
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateGroup(GroupExpenseCreateViewModel model)
    {
        model.Currency = SystemCurrency.Normalize(model.Currency);
        var description = model.Description?.Trim() ?? string.Empty;
        var manualExpenseTypeName = model.ManualExpenseTypeName?.Trim() ?? string.Empty;

        ExpenseType? expenseType = null;
        if (model.ExpenseTypeId.HasValue && model.ExpenseTypeId.Value > 0)
        {
            expenseType = await _db.ExpenseTypes
                .AsNoTracking()
                .FirstOrDefaultAsync(e => e.Id == model.ExpenseTypeId.Value && e.IsActive);
            if (expenseType is null)
            {
                ModelState.AddModelError(nameof(model.ExpenseTypeId), "نوع مصرف انتخاب‌شده معتبر نیست.");
            }
        }
        else if (!string.IsNullOrWhiteSpace(manualExpenseTypeName))
        {
            expenseType = await FindExpenseTypeByManualNameAsync(manualExpenseTypeName);
        }
        else
        {
            ModelState.AddModelError(nameof(model.ManualExpenseTypeName), "نوع مصرف را از لیست انتخاب کنید یا دستی وارد کنید.");
        }

        ServiceProviderEntity? serviceProvider = null;
        if (model.ServiceProviderId.HasValue)
        {
            serviceProvider = await _db.ServiceProviders
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == model.ServiceProviderId.Value && p.IsActive);
            if (serviceProvider is null)
            {
                ModelState.AddModelError(nameof(model.ServiceProviderId), "شرکت خدماتی انتخاب‌شده معتبر نیست.");
            }
        }

        var hasActiveCurrencies = await _db.Currencies.AsNoTracking().AnyAsync(c => c.IsActive);
        if (hasActiveCurrencies
            && !await _db.Currencies.AsNoTracking().AnyAsync(c => c.Code == model.Currency && c.IsActive))
        {
            ModelState.AddModelError(nameof(model.Currency), "ارز انتخاب‌شده معتبر نیست.");
        }

        // انتخاب‌ها: dedupe + بارگذاری عملیات‌های معتبرِ در جریان.
        var selections = (model.Items ?? [])
            .Where(i => i.Id > 0 && (i.Kind == "Leg" || i.Kind == "Dispatch"))
            .GroupBy(i => (i.Kind, i.Id))
            .Select(g => g.First())
            .ToList();

        if (selections.Count == 0)
        {
            ModelState.AddModelError(string.Empty, "حداقل یک عملیات در جریان را انتخاب کنید.");
        }

        var legIds = selections.Where(i => i.Kind == "Leg").Select(i => i.Id).ToList();
        var dispatchIds = selections.Where(i => i.Kind == "Dispatch").Select(i => i.Id).ToList();

        var legs = await _db.InventoryTransportLegs
            .AsNoTracking()
            .Where(l => legIds.Contains(l.Id)
                        && (l.Status == InventoryTransportLegStatus.Loaded
                            || l.Status == InventoryTransportLegStatus.InTransit))
            .ToDictionaryAsync(l => l.Id);

        var dispatches = await _db.TruckDispatches
            .AsNoTracking()
            .Where(d => dispatchIds.Contains(d.Id)
                        && (d.Status == DispatchStatus.Loaded || d.Status == DispatchStatus.InTransit))
            .ToDictionaryAsync(d => d.Id);

        if (legs.Count != legIds.Count || dispatches.Count != dispatchIds.Count)
        {
            ModelState.AddModelError(string.Empty, "بعضی از عملیات‌های انتخاب‌شده دیگر در جریان نیستند. لیست را تازه کنید.");
        }

        List<decimal> shares = [];
        decimal totalAmount = 0m;
        if (ModelState.IsValid)
        {
            var resolved = selections
                .Select(i => (Input: i, QuantityMt: i.Kind == "Leg" ? legs[i.Id].QuantityMt : dispatches[i.Id].LoadedQuantityMt))
                .ToList();

            if (!TryComputeShares(model, resolved, out shares, out totalAmount, out var shareError))
            {
                ModelState.AddModelError(string.Empty, shareError!);
            }
        }

        if (!ModelState.IsValid)
        {
            model.Description = description;
            await PopulateGroupExpenseLookupsAsync(model);
            ViewBag.Operations = await LoadInProgressOperationsAsync();
            ViewBag.RegisteredGroupBatches = await LoadActiveGroupBatchesAsync();
            return View(model);
        }

        CurrencyConversionResult conversion;
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
            model.Description = description;
            await PopulateGroupExpenseLookupsAsync(model);
            ViewBag.Operations = await LoadInProgressOperationsAsync();
            ViewBag.RegisteredGroupBatches = await LoadActiveGroupBatchesAsync();
            return View(model);
        }

        IDbContextTransaction? transaction = null;
        if (_db.Database.IsRelational())
        {
            transaction = await _db.Database.BeginTransactionAsync();
        }

        try
        {
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

            var descriptionBase = string.IsNullOrWhiteSpace(description)
                ? $"مصرف گروهی {expenseType.NamePersian ?? expenseType.Name} {DateDisplay.Date(model.ExpenseDate)}"
                : description;

            var batch = new ExpenseBatch
            {
                ExpenseTypeId = expenseType!.Id,
                ServiceProviderId = serviceProvider?.Id,
                ExpenseDate = model.ExpenseDate.Date,
                AllocationMethod = model.AllocationMethod,
                Currency = conversion.SourceCurrencyCode,
                AppliedFxRateToUsd = conversion.AppliedRateToBase,
                TotalAmount = totalAmount,
                TotalAmountUsd = conversion.ConvertToBase(totalAmount),
                OperationCount = selections.Count,
                Description = descriptionBase
            };

            _db.ExpenseBatches.Add(batch);
            await _db.SaveChangesAsync();

            batch.BatchNumber = $"GEXP-{batch.Id}";
            await _db.SaveChangesAsync();

            for (var i = 0; i < selections.Count; i++)
            {
                var selection = selections[i];
                var isLeg = selection.Kind == "Leg";
                var leg = isLeg ? legs[selection.Id] : null;
                var dispatch = isLeg ? null : dispatches[selection.Id];

                var opLabel = isLeg
                    ? $"حمل از موجودی #{selection.Id}"
                    : $"ارسال موتر #{selection.Id}";

                var expense = new ExpenseTransaction
                {
                    ExpenseTypeId = expenseType.Id,
                    ExpenseBatchId = batch.Id,
                    ContractId = isLeg ? leg!.SourcePurchaseContractId : dispatch!.ContractId,
                    ShipmentId = isLeg ? leg!.ShipmentId : null,
                    TransportLegId = isLeg ? selection.Id : null,
                    TruckDispatchId = isLeg ? null : selection.Id,
                    ServiceProviderId = serviceProvider?.Id,
                    ExpenseDate = model.ExpenseDate.Date,
                    Amount = shares[i],
                    Currency = conversion.SourceCurrencyCode,
                    AppliedFxRateToUsd = conversion.AppliedRateToBase,
                    AmountUsd = conversion.ConvertToBase(shares[i]),
                    Description = $"{descriptionBase} | {batch.BatchNumber} — {opLabel}",
                    CostResponsibility = model.CostResponsibility
                };

                _db.ExpenseTransactions.Add(expense);
                await _db.SaveChangesAsync();

                var ledgerEntry = new LedgerEntry
                {
                    EntryDate = expense.ExpenseDate,
                    Side = GetExpenseLedgerSide(expense),
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
                    ServiceProviderId = expense.ServiceProviderId,
                    ShipmentId = expense.ShipmentId
                };

                _db.LedgerEntries.Add(ledgerEntry);
                await _db.SaveChangesAsync();
            }

            await _audit.LogAndSaveAsync(
                nameof(ExpenseBatch),
                batch.Id,
                AuditAction.Insert,
                diff: AuditDiffFormatter.ForCreate(
                    ("BatchNumber", batch.BatchNumber),
                    ("ExpenseTypeId", batch.ExpenseTypeId),
                    ("ServiceProviderId", batch.ServiceProviderId),
                    ("ExpenseDate", batch.ExpenseDate),
                    ("AllocationMethod", batch.AllocationMethod),
                    ("Currency", batch.Currency),
                    ("TotalAmount", batch.TotalAmount),
                    ("TotalAmountUsd", batch.TotalAmountUsd),
                    ("OperationCount", batch.OperationCount)));

            if (transaction is not null)
            {
                await transaction.CommitAsync();
            }

            TempData["ok"] = $"مصرف گروهی {batch.BatchNumber} برای {selections.Count} عملیات ثبت شد.";
            return RedirectToAction(nameof(GroupDetails), new { id = batch.Id });
        }
        catch (Exception ex)
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync();
            }

            _logger.LogError(ex, "Failed to create group expense batch.");
            ModelState.AddModelError(string.Empty, "ثبت مصرف گروهی انجام نشد. دوباره تلاش کنید.");
        }

        model.Description = description;
        await PopulateGroupExpenseLookupsAsync(model);
        ViewBag.Operations = await LoadInProgressOperationsAsync();
        ViewBag.RegisteredGroupBatches = await LoadActiveGroupBatchesAsync();
        return View(model);
    }

    private static string AllocationMethodName(ExpenseAllocationMethod method) => method switch
    {
        ExpenseAllocationMethod.FixedPerOperation => "مبلغ برای هر عملیات",
        ExpenseAllocationMethod.EqualSplit => "تقسیم مساوی",
        ExpenseAllocationMethod.ByQuantity => "بر اساس مقدار",
        ExpenseAllocationMethod.Manual => "دستی",
        _ => method.ToString()
    };

    public async Task<IActionResult> GroupDetails(int id)
    {
        var batch = await _db.ExpenseBatches
            .AsNoTracking()
            .Include(b => b.ExpenseType)
            .Include(b => b.ServiceProvider)
            .FirstOrDefaultAsync(b => b.Id == id);

        if (batch is null)
        {
            return NotFound();
        }

        var shares = await _db.ExpenseTransactions
            .AsNoTracking()
            .Where(e => e.ExpenseBatchId == batch.Id)
            .OrderBy(e => e.Id)
            .Select(e => new GroupExpenseShareViewModel
            {
                ExpenseId = e.Id,
                OperationLabel = e.TransportLegId.HasValue ? "حمل از موجودی" : "ارسال موتر",
                VehicleKind = e.TransportLeg != null
                    ? (e.TransportLeg.TransportType == LoadingTransportType.Wagon ? "واگن"
                        : e.TransportLeg.TransportType == LoadingTransportType.Truck ? "موتر" : "نامشخص")
                    : "موتر",
                Number = e.TransportLeg != null
                    ? (e.TransportLeg.WagonNumber ?? e.TransportLeg.RwbNo ?? ("#" + e.TransportLegId))
                    : (e.TruckDispatch != null && e.TruckDispatch.Truck != null
                        ? e.TruckDispatch.Truck.PlateNumber
                        : ("#" + e.TruckDispatchId)),
                Route = e.TransportLeg != null
                    ? ((e.TransportLeg.SourceTerminal != null ? e.TransportLeg.SourceTerminal.Name : "؟")
                        + " ← "
                        + (e.TransportLeg.DestinationTerminal != null ? e.TransportLeg.DestinationTerminal.Name
                            : e.TransportLeg.DestinationLocation != null ? e.TransportLeg.DestinationLocation.Name : "؟"))
                    : (e.TruckDispatch != null && e.TruckDispatch.DestinationLocation != null
                        ? e.TruckDispatch.DestinationLocation.Name
                        : "-"),
                QuantityMt = e.TransportLeg != null
                    ? e.TransportLeg.QuantityMt
                    : (e.TruckDispatch != null ? e.TruckDispatch.LoadedQuantityMt : 0m),
                Amount = e.Amount,
                AmountUsd = e.AmountUsd,
                IsCancelled = e.IsCancelled,
                TruckDispatchId = e.TruckDispatchId,
                TransportLegId = e.TransportLegId
            })
            .ToListAsync();

        return View(new GroupExpenseDetailsViewModel
        {
            Id = batch.Id,
            BatchNumber = batch.BatchNumber,
            ExpenseTypeName = batch.ExpenseType != null ? batch.ExpenseType.NamePersian ?? batch.ExpenseType.Name : "-",
            ServiceProviderName = batch.ServiceProvider?.Name,
            ExpenseDate = batch.ExpenseDate,
            AllocationMethodName = AllocationMethodName(batch.AllocationMethod),
            Currency = batch.Currency,
            AppliedFxRateToUsd = batch.AppliedFxRateToUsd,
            TotalAmount = batch.TotalAmount,
            TotalAmountUsd = batch.TotalAmountUsd,
            Description = batch.Description,
            IsCancelled = batch.IsCancelled,
            Shares = shares
        });
    }

    // هستهٔ مشترک لغو یک بچ: هر سهم IsCancelled + سند معکوس Ledger (همان مسیر امن لغو تکی).
    // SaveChanges/Transaction با فراخواننده است.
    private async Task<int> ApplyGroupCancellationAsync(ExpenseBatch batch)
    {
        var expenses = await _db.ExpenseTransactions
            .Where(e => e.ExpenseBatchId == batch.Id && !e.IsCancelled)
            .ToListAsync();

        var expenseIds = expenses.Select(e => e.Id).ToList();
        var originalLedgers = await _db.LedgerEntries
            .AsNoTracking()
            .Where(l => l.SourceType == "Expense" && expenseIds.Contains(l.SourceId))
            .ToListAsync();
        var ledgerBySource = originalLedgers
            .GroupBy(l => l.SourceId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(l => l.Id).First());

        var cancelledCount = 0;
        foreach (var expense in expenses)
        {
            if (!ledgerBySource.TryGetValue(expense.Id, out var originalLedger))
            {
                continue; // بدون سند مالی؛ الگوی لغو تکی هم در این حالت لغو نمی‌کند.
            }

            // مرحله ۵ — Reversal قبل از علامت‌خوردن IsCancelled (مانند مسیر لغو تکی).
            if (_expenseAccounting is not null)
            {
                await _expenseAccounting.TryPostExpenseReversalAsync(expense);
            }

            expense.IsCancelled = true;
            _db.LedgerEntries.Add(new LedgerEntry
            {
                EntryDate = _businessClock.Today,
                Side = ReverseSide(originalLedger.Side),
                AmountUsd = originalLedger.AmountUsd,
                Currency = originalLedger.Currency,
                SourceAmount = originalLedger.SourceAmount,
                SourceCurrencyCode = originalLedger.SourceCurrencyCode,
                AppliedFxRateToUsd = originalLedger.AppliedFxRateToUsd,
                AppliedFxRateDate = originalLedger.AppliedFxRateDate,
                AppliedFxRateSource = originalLedger.AppliedFxRateSource,
                Description = $"لغو مصرف گروهی {batch.BatchNumber} - هزینه #{expense.Id} | {originalLedger.Description}",
                SourceType = "Expense",
                SourceId = expense.Id,
                Reference = (originalLedger.Reference ?? $"EXP-{expense.Id}") + "-CANCEL",
                ContractId = originalLedger.ContractId,
                ServiceProviderId = originalLedger.ServiceProviderId,
                ShipmentId = originalLedger.ShipmentId
            });
            cancelledCount++;
        }

        batch.IsCancelled = true;
        return cancelledCount;
    }

    // لغو گروهی: همان مسیر امنِ لغو تکی برای هر سهم (IsCancelled + سند معکوس Ledger).
    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> CancelGroup(int id, string? returnUrl = null)
    {
        IActionResult BackToCaller()
            => !string.IsNullOrWhiteSpace(returnUrl) && Url?.IsLocalUrl(returnUrl) == true
                ? Redirect(returnUrl)
                : RedirectToAction(nameof(GroupDetails), new { id });

        var batch = await _db.ExpenseBatches
            .FirstOrDefaultAsync(b => b.Id == id);

        if (batch is null)
        {
            return NotFound();
        }

        if (batch.IsCancelled)
        {
            TempData["ok"] = "این مصرف گروهی قبلاً لغو شده است.";
            return BackToCaller();
        }

        IDbContextTransaction? transaction = null;
        if (_db.Database.IsRelational())
        {
            transaction = await _db.Database.BeginTransactionAsync();
        }

        try
        {
            var cancelledCount = await ApplyGroupCancellationAsync(batch);
            await _db.SaveChangesAsync();

            await _audit.LogAndSaveAsync(
                nameof(ExpenseBatch),
                batch.Id,
                AuditAction.Update,
                diff: $"CancelGroup: {cancelledCount} share(s) cancelled");

            if (transaction is not null)
            {
                await transaction.CommitAsync();
            }

            TempData["ok"] = $"مصرف گروهی {batch.BatchNumber} با {cancelledCount} سهم لغو شد.";
        }
        catch (Exception ex)
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync();
            }

            _logger.LogError(ex, "Failed to cancel group expense batch {BatchId}.", id);
            TempData["err"] = "لغو مصرف گروهی انجام نشد. دوباره تلاش کنید.";
        }

        return BackToCaller();
    }

    // لغو همهٔ مصارف گروهی فعال — هر بچ با همان هستهٔ امن لغو می‌شود؛ همه در یک تراکنش.
    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> CancelAllGroups(string? returnUrl = null)
    {
        IActionResult BackToCaller()
            => !string.IsNullOrWhiteSpace(returnUrl) && Url?.IsLocalUrl(returnUrl) == true
                ? Redirect(returnUrl)
                : RedirectToAction(nameof(CreateGroup));

        var batches = await _db.ExpenseBatches
            .Where(b => !b.IsCancelled)
            .OrderBy(b => b.Id)
            .ToListAsync();

        if (batches.Count == 0)
        {
            TempData["ok"] = "مصرف گروهی فعالی برای لغو وجود ندارد.";
            return BackToCaller();
        }

        IDbContextTransaction? transaction = null;
        if (_db.Database.IsRelational())
        {
            transaction = await _db.Database.BeginTransactionAsync();
        }

        try
        {
            var totalShares = 0;
            foreach (var batch in batches)
            {
                totalShares += await ApplyGroupCancellationAsync(batch);
            }

            await _db.SaveChangesAsync();

            foreach (var batch in batches)
            {
                await _audit.LogAndSaveAsync(
                    nameof(ExpenseBatch),
                    batch.Id,
                    AuditAction.Update,
                    diff: "CancelAllGroups: batch cancelled");
            }

            if (transaction is not null)
            {
                await transaction.CommitAsync();
            }

            TempData["ok"] = $"{batches.Count:N0} مصرف گروهی با مجموع {totalShares:N0} سهم لغو شد.";
        }
        catch (Exception ex)
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync();
            }

            _logger.LogError(ex, "Failed to cancel all group expense batches.");
            TempData["err"] = "لغو همهٔ مصارف گروهی انجام نشد. دوباره تلاش کنید.";
        }

        return BackToCaller();
    }

    private async Task PopulateCustomsBatchLookupsAsync(CustomsBatchViewModel model)
    {
        var contracts = await _db.Contracts
            .AsNoTracking()
            .OrderBy(c => model.ContractId > 0 && c.Id == model.ContractId ? 0 : 1)
            .ThenByDescending(c => c.ContractDate)
            .Take(LookupLimit)
            .Select(c => new { c.Id, c.ContractName, c.ContractNumber })
            .ToListAsync();

        ViewBag.Contracts = new SelectList(
            contracts.Select(c => new ContractLookupOption(
                c.Id,
                ContractUiText.FormatDisplayLabel(c.ContractName, c.ContractNumber))),
            "Id",
            "Display",
            model.ContractId > 0 ? model.ContractId : null);

        var dispatches = await _db.TruckDispatches
            .AsNoTracking()
            .OrderBy(d => model.TruckDispatchId.HasValue && d.Id == model.TruckDispatchId ? 0 : 1)
            .ThenByDescending(d => d.DispatchDate)
            .Take(LookupLimit)
            .Select(d => new { d.Id, Label = $"#{d.Id} - {(d.Truck != null ? d.Truck.PlateNumber : "بدون پلاک")} - {DateDisplay.Date(d.DispatchDate)}" })
            .ToListAsync();

        ViewBag.TruckDispatches = new SelectList(dispatches, "Id", "Label", model.TruckDispatchId);

        ViewBag.Currencies = new SelectList(
            await _db.Currencies.AsNoTracking().Where(c => c.IsActive).OrderBy(c => c.Code).Select(c => new { c.Code }).ToListAsync(),
            "Code", "Code", model.Currency);
    }

    private static void NormalizeCreateModel(ExpenseCreateViewModel model)
    {
        model.Currency = SystemCurrency.Normalize(model.Currency);
        model.Description = (model.Description ?? string.Empty).Trim();
        model.ManualExpenseTypeName = model.ManualExpenseTypeName?.Trim();
    }
}
