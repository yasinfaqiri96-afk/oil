using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Caching.Memory;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Helpers;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.Loading;
using PTGOilSystem.Web.Models.LossEvents;
using PTGOilSystem.Web.Security;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Audit;
using PTGOilSystem.Web.Services.Exceptions;
using ServiceProviderEntity = PTGOilSystem.Web.Models.Entities.ServiceProvider;
using PTGOilSystem.Web.Services.Time;

namespace PTGOilSystem.Web.Controllers;

[Authorize]
public partial class LoadingController : Controller
{
    private readonly ApplicationDbContext _db;
    private readonly IAuditService _audit;
    private readonly ILogger<LoadingController> _logger;
    private readonly IPricingService _pricing;
    private readonly ILossEventWorkflowService _lossWorkflow;
    private readonly IMemoryCache? _cache;
    // مرحله ۶ — Dual-write اختیاری به دفتر کل جدید. پشت Feature Flag و null-safe.
    private readonly Services.Accounting.IPurchaseAccountingAdapter? _purchaseAccounting;
    private readonly Services.Accounting.IExpenseAccountingAdapter? _expenseAccounting;
    private const int DefaultListLimit = 100;
    private const int LookupLimit = 200;
    private const int ReceiptAllocationEditorRows = 4;
    private const int LargeImportPreviewRows = 100;
    private const string LoadingTransportExpenseCode = "LOAD-TRANSPORT";
    private const string LoadingStorageExpenseCode = "LOAD-STORAGE";
    private const string LoadingWagonRentExpenseCode = "LOAD-WAGON-RENT";
    private const string LoadingOtherExpenseCode = "LOAD-OTHER";
    private const string SupplierLoadingLedgerSourceType = SupplierLoadingLedger.SourceType;
    private const decimal PercentTolerance = 0.0001m;
    private static readonly JsonSerializerOptions LoadingRowsJsonOptions = CreateLoadingRowsJsonOptions();

    private sealed record PlattsReferenceSuggestion(
        decimal? PriceUsd,
        string Source,
        DateTime? EffectiveDate,
            bool FallbackApplied,
            string Reason);
    private sealed record LookupOption(int Id, string Name);
    private sealed record TankLookupOption(int Id, string Display);
    private sealed record TruckLookupOption(int Id, string PlateNumber);
    private sealed record DriverLookupOption(int Id, string FullName);

    private readonly IAfghanistanBusinessClock _businessClock;

    public LoadingController(
        ApplicationDbContext db,
        IAuditService audit,
        ILogger<LoadingController> logger,
        ILossEventWorkflowService? lossWorkflow = null,
        IPricingService? pricing = null,
        IMemoryCache? cache = null,
        Services.Accounting.IPurchaseAccountingAdapter? purchaseAccounting = null,
        Services.Accounting.IExpenseAccountingAdapter? expenseAccounting = null,
        IAfghanistanBusinessClock? businessClock = null)
    {
        // مرجع «امروزِ کاری» همیشه ساعت کابل است، نه تاریخ UTC سرور.
        _businessClock = businessClock ?? new AfghanistanBusinessClock(TimeProvider.System);
        _purchaseAccounting = purchaseAccounting;
        _expenseAccounting = expenseAccounting;
        _db = db;
        _audit = audit;
        _logger = logger;
        _lossWorkflow = lossWorkflow ?? new LossEventWorkflowService(db, new StockService(db), audit);
        _pricing = pricing ?? new PricingService(db);
        _cache = cache;
    }

    private Task<T> GetCachedLookupAsync<T>(string key, Func<Task<T>> factory)
        where T : class
    {
        if (_cache is null)
        {
            return factory();
        }

        return _cache.GetOrCreateAsync(key, entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(2);
            entry.SlidingExpiration = TimeSpan.FromSeconds(30);
            return factory();
        })!;
    }

    private async Task PopulateLookupsAsync(LoadingCreateViewModel? createModel = null)
    {
        createModel ??= new LoadingCreateViewModel();
        EnsureEditableRows(createModel);
        ApplyContractLock(createModel);
        var selectedContractIds = ResolveSelectedContractIds(createModel);

        var purchaseContractQuery = _db.Contracts
            .AsNoTracking()
            .Where(c => c.ContractType == ContractType.Purchase);

        if (createModel.LockContract && createModel.ContractId > 0)
        {
            purchaseContractQuery = purchaseContractQuery.Where(c => c.Id == createModel.ContractId);
        }

        var purchaseContracts = await purchaseContractQuery
            .OrderBy(c => selectedContractIds.Contains(c.Id) ? 0 : 1)
            .ThenByDescending(c => c.ContractDate)
            .ThenBy(c => c.ContractNumber)
            .Take(LookupLimit)
            .Select(c => new
            {
                c.Id,
                c.ContractName,
                c.ContractNumber,
                c.ContractType,
                c.ProductId,
                ProductName = c.Product != null ? c.Product.Name : null,
                UnitSymbol = c.Unit != null ? c.Unit.Symbol : null,
                UnitCode = c.Unit != null ? c.Unit.Code : null,
                UnitNamePersian = c.Unit != null ? c.Unit.NamePersian : null,
                UnitName = c.Unit != null ? c.Unit.Name : null
            })
            .ToListAsync();

        var contractOptions = purchaseContracts
            .Select(c => new ContractLookupOption(
                c.Id,
                ContractUiText.FormatLookup(
                    c.ContractName,
                    c.ContractNumber,
                    c.ContractType,
                    c.ProductName,
                    ContractUiText.ResolveUnitText(c.UnitSymbol, c.UnitCode, c.UnitNamePersian, c.UnitName))))
            .ToList();

        ViewBag.Contracts = new MultiSelectList(
            contractOptions,
            nameof(ContractLookupOption.Id),
            nameof(ContractLookupOption.Display),
            selectedContractIds);
        ViewBag.ContractProductIds = purchaseContracts.ToDictionary(c => c.Id, c => c.ProductId);
        ViewBag.ContractProductNames = purchaseContracts.ToDictionary(c => c.Id, c => c.ProductName ?? "");

        if (selectedContractIds.Count == 1)
        {
            foreach (var row in createModel.Rows)
            {
                row.ContractId = selectedContractIds[0];
            }
        }

        var selectedContractSet = selectedContractIds.ToHashSet();
        ViewBag.ShowRowContractSelector = selectedContractIds.Count > 1;
        ViewBag.RowContractOptions = contractOptions
            .Where(option => selectedContractSet.Contains(option.Id))
            .Select(option => new SelectListItem(option.Display, option.Id.ToString()))
            .ToList();

        var products = await GetCachedLookupAsync("loading:create:products:v1", () => _db.Products
                .AsNoTracking()
                .Where(p => p.IsActive)
                .OrderBy(p => p.Code)
                .Select(p => new LookupOption(p.Id, p.Name))
                .ToListAsync());

        ViewBag.Products = new SelectList(
            products,
            "Id",
            "Name",
            createModel?.ProductId);
        ViewBag.ProductNames = products.ToDictionary(p => p.Id, p => p.Name);

        ViewBag.Origins = new SelectList(
            await GetCachedLookupAsync("loading:create:origins:v1", () => _db.Locations
                .AsNoTracking()
                .OrderBy(l => l.Name)
                .Select(l => new LookupOption(l.Id, l.Name))
                .ToListAsync()),
            nameof(LookupOption.Id),
            nameof(LookupOption.Name),
            createModel?.OriginLocationId);

        var vesselOptions = await GetCachedLookupAsync("loading:create:vessels:v1", () => _db.Vessels
            .AsNoTracking()
            .Where(v => v.IsActive)
            .OrderBy(v => v.Name)
            .Select(v => new LookupOption(v.Id, v.Name))
            .ToListAsync());
        ViewBag.VesselOptions = vesselOptions
            .Select(v => new SelectListItem(v.Name, v.Id.ToString()))
            .ToList();

        var truckOptions = await GetCachedLookupAsync("loading:create:trucks:v1", () => _db.Trucks
            .AsNoTracking()
            .Where(t => t.IsActive)
            .OrderBy(t => t.PlateNumber)
            .Select(t => new LookupOption(t.Id, t.PlateNumber))
            .ToListAsync());
        ViewBag.TruckOptions = truckOptions
            .Select(t => new SelectListItem(t.Name, t.Id.ToString()))
            .ToList();

        var selectedLogisticsServiceProviderIds = createModel!.Rows
            .Select(r => r.LogisticsServiceProviderId)
            .Where(id => id.HasValue && id.Value > 0)
            .Select(id => id!.Value)
            .Distinct()
            .ToList();
        var selectedOperationalAssetIds = createModel.Rows
            .Select(r => r.OperationalAssetId)
            .Where(id => id.HasValue && id.Value > 0)
            .Select(id => id!.Value)
            .Distinct()
            .ToList();

        var logisticsServiceProviders = await _db.ServiceProviders
            .AsNoTracking()
            .Where(p => p.IsActive || selectedLogisticsServiceProviderIds.Contains(p.Id))
            .OrderBy(p => selectedLogisticsServiceProviderIds.Contains(p.Id) ? 0 : 1)
            .ThenBy(p => p.Name)
            .Select(p => new SelectListItem(
                string.IsNullOrWhiteSpace(p.Code) ? p.Name : p.Code + " - " + p.Name,
                p.Id.ToString()))
            .ToListAsync();

        ViewBag.LogisticsServiceProviders = logisticsServiceProviders;
        ViewBag.LogisticsCompanyOptions = logisticsServiceProviders
            .Select(p => p.Text)
            .ToList();
        ViewBag.LoadingOperationalAssets = await _db.OperationalAssets
            .AsNoTracking()
            .Where(a => a.IsActive || selectedOperationalAssetIds.Contains(a.Id))
            .OrderBy(a => selectedOperationalAssetIds.Contains(a.Id) ? 0 : 1)
            .ThenBy(a => a.AssetCode)
            .ThenBy(a => a.Name)
            .Select(a => new SelectListItem(a.AssetCode + " - " + a.Name, a.Id.ToString()))
            .ToListAsync();
    }

    private async Task<Contract?> LockPurchaseContractAsync(int contractId)
    {
        if (_db.Database.IsRelational()
            && string.Equals(_db.Database.ProviderName, "Npgsql.EntityFrameworkCore.PostgreSQL", StringComparison.Ordinal))
        {
            return await _db.Contracts
                .FromSqlInterpolated($@"SELECT * FROM ""Contracts"" WHERE ""Id"" = {contractId} AND ""ContractType"" = {(int)ContractType.Purchase} FOR UPDATE")
                .AsNoTracking()
                .SingleOrDefaultAsync();
        }

        return await _db.Contracts
            .AsNoTracking()
            .SingleOrDefaultAsync(c => c.Id == contractId && c.ContractType == ContractType.Purchase);
    }

    private async Task<decimal> GetCommittedLoadedQuantityMtAsync(int contractId)
        => await _db.LoadingRegisters
            .AsNoTracking()
            .Where(l => l.ContractId == contractId)
            .SumAsync(l => (decimal?)l.LoadedQuantityMt) ?? 0m;

    private static List<int> ResolveSelectedContractIds(LoadingCreateViewModel model)
    {
        var selectedIds = (model.SelectedContractIds ?? [])
            .Where(id => id > 0)
            .Distinct()
            .ToList();

        if (selectedIds.Count == 0 && model.ContractId > 0)
        {
            selectedIds.Add(model.ContractId);
        }

        if (selectedIds.Count > 0)
        {
            model.ContractId = selectedIds[0];
        }

        model.SelectedContractIds = selectedIds;
        return selectedIds;
    }

    private static void ApplyContractLock(LoadingCreateViewModel model)
    {
        if (!model.LockContract || model.ContractId <= 0)
        {
            return;
        }

        model.SelectedContractIds = [model.ContractId];

        foreach (var row in model.Rows ?? [])
        {
            row.ContractId = model.ContractId;
        }
    }

    private static int? ResolveEffectiveRowContractId(
        LoadingCreateViewModel model,
        LoadingCreateRowViewModel row,
        bool useRowContracts)
    {
        if (useRowContracts)
        {
            return row.ContractId;
        }

        return model.ContractId > 0 ? model.ContractId : null;
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

    private static bool IsRubSettlement(string? currencyCode)
        => LoadingRubSettlement.IsRubSettlement(currencyCode);

    private static void ApplyContractRubDefaults(
        Contract contract,
        IEnumerable<LoadingCreateRowViewModel> rows)
    {
        foreach (var row in rows)
        {
            ApplyContractRubDefaults(contract, row);
        }
    }

    private static void ApplyContractRubDefaults(Contract contract, LoadingCreateRowViewModel row)
    {
        row.SettlementCurrencyCode = SystemCurrency.Normalize(contract.SettlementCurrencyCode);
        row.RubRatePolicy = contract.RubRatePolicy;

        if (!IsRubSettlement(row.SettlementCurrencyCode))
        {
            row.RubRateStatus = RubSettlementRateStatus.NotRequired;
            row.RubRateDate = null;
            row.RubRateSource = null;
            row.AmountUsdAtRubLock = null;
            row.AmountRubAtRubLock = null;
            return;
        }

        if (row.RubRatePolicy == RubSettlementRatePolicy.NotApplicable)
        {
            row.RubRatePolicy = RubSettlementRatePolicy.RateLater;
        }

        var loadingValueUsd = CalculateLoadingValueUsd(row.LoadedQuantityMt, row.LoadingPriceUsd);

        if (row.RubRatePolicy == RubSettlementRatePolicy.FixedContractRate)
        {
            row.RubPerUsdRate = NormalizePositiveDecimal(contract.ContractRubPerUsdRate);
            row.RubRateDate = contract.ContractRubRateDate;
            row.RubRateSource = NormalizeNullable(contract.ContractRubRateSource) ?? "Contract";
        }
        else
        {
            row.RubPerUsdRate = NormalizePositiveDecimal(row.RubPerUsdRate);

            // نرخ هر بارگیری: اگر کاربر نرخ روبل/دالر صریح نداده ولی فایل اکسل ارقام روبلی
            // (قیمت/مجموع) دارد، نرخ را از «روبل ÷ دالر» همین بارگیری مشتق کن تا ثبت با ایمپورت
            // بدون نرخ دستی هم کار کند. سایر حالت‌ها (نرخ ثابت/بعداً) دست‌نخورده می‌مانند.
            if (!row.RubPerUsdRate.HasValue
                && row.RubRatePolicy == RubSettlementRatePolicy.PerLoadingRate)
            {
                var derivedRate = DeriveRubPerUsdRateFromFileFigures(row, loadingValueUsd);
                if (derivedRate.HasValue)
                {
                    row.RubPerUsdRate = derivedRate;
                    row.RubRateSource = "Loading file";
                }
            }

            row.RubRateDate = row.RubPerUsdRate.HasValue ? row.RubRateDate ?? row.LoadingDate : null;
            row.RubRateSource = row.RubPerUsdRate.HasValue ? NormalizeNullable(row.RubRateSource) ?? "Manual" : null;
        }
        if (row.RubPerUsdRate.HasValue && loadingValueUsd.HasValue)
        {
            row.RubRateStatus = RubSettlementRateStatus.Locked;
            row.AmountUsdAtRubLock = loadingValueUsd.Value;
            row.AmountRubAtRubLock = CalculateRubAmount(loadingValueUsd.Value, row.RubPerUsdRate.Value);
            return;
        }

        row.RubRateStatus = RubSettlementRateStatus.Pending;
        row.AmountUsdAtRubLock = null;
        row.AmountRubAtRubLock = null;
    }

    private static void ApplyRubSnapshotToLoading(LoadingRegister loading, LoadingCreateRowViewModel row)
    {
        loading.SettlementCurrencyCode = SystemCurrency.Normalize(row.SettlementCurrencyCode);
        loading.RubRateStatus = row.RubRateStatus;
        loading.RubPerUsdRate = row.RubPerUsdRate;
        loading.RubRateDate = row.RubRateDate.HasValue ? ToUtcDate(row.RubRateDate.Value) : null;
        loading.RubRateSource = NormalizeNullable(row.RubRateSource);
        loading.AmountUsdAtRubLock = row.AmountUsdAtRubLock;
        loading.AmountRubAtRubLock = row.AmountRubAtRubLock;

        if (!IsRubSettlement(loading.SettlementCurrencyCode))
        {
            loading.RubRateStatus = RubSettlementRateStatus.NotRequired;
            loading.RubPerUsdRate = null;
            loading.RubRateDate = null;
            loading.RubRateSource = null;
            loading.AmountUsdAtRubLock = null;
            loading.AmountRubAtRubLock = null;
            loading.RubRateLockedAtUtc = null;
            loading.RubRateLockedByUserName = null;
            return;
        }

        if (loading.RubRateStatus == RubSettlementRateStatus.Locked)
        {
            loading.RubRateLockedAtUtc = DateTime.UtcNow;
        }
    }

    private static decimal CalculateRubAmount(decimal amountUsd, decimal rubPerUsdRate)
        => LoadingRubSettlement.CalculateRubAmount(amountUsd, rubPerUsdRate);

    // نرخ روبل/دالر همین بارگیری را از ارقام روبلی فایل اکسل مشتق می‌کند:
    //   RUB/USD = مجموع روبل ÷ ارزش دالری  (یا قیمت روبل فی‌تن ÷ قیمت دالر فی‌تن).
    // خروجی با ۶ رقم اعشار، همسان ستون RubPerUsdRate = numeric(18,6). اگر داده کافی نبود null.
    private static decimal? DeriveRubPerUsdRateFromFileFigures(
        LoadingCreateRowViewModel row,
        decimal? loadingValueUsd)
    {
        var totalRub = NormalizePositiveDecimal(row.SettlementValueRub);
        if (totalRub.HasValue && loadingValueUsd.HasValue && loadingValueUsd.Value > 0m)
        {
            return Math.Round(totalRub.Value / loadingValueUsd.Value, 6, MidpointRounding.AwayFromZero);
        }

        var unitPriceRub = NormalizePositiveDecimal(row.SettlementUnitPriceRub);
        var loadingPriceUsd = NormalizePositiveDecimal(row.LoadingPriceUsd);
        if (unitPriceRub.HasValue && loadingPriceUsd.HasValue)
        {
            return Math.Round(unitPriceRub.Value / loadingPriceUsd.Value, 6, MidpointRounding.AwayFromZero);
        }

        return null;
    }

    private async Task PostSupplierLoadingLedgerIfReadyAsync(LoadingRegister loading, Contract? contract)
    {
        // مرحله ۶ — Dual-write به دفتر کل جدید. عمداً بیرونِ شرطِ روبلیِ زیر است: مسیر قدیمی
        // فقط برای بارگیری‌های روبلیِ قفل‌شده سطر می‌سازد، ولی دفتر کل جدید هر بارگیریِ
        // قیمت‌دار (دلاری و روبلی) را می‌شناسد. Adapter خودش قیمت‌نداشتن را Skip می‌کند.
        if (_purchaseAccounting is not null)
        {
            await _purchaseAccounting.TryPostPurchaseAsync(loading);
        }

        if (contract is null || !SupplierLoadingLedger.IsPostable(loading, contract))
        {
            return;
        }

        // سطر Legacy این بارگیری اگر از قبل هست همان به‌روزرسانی می‌شود، نه اینکه سطر دوم ساخته شود.
        // بعد از «اصلاح قیمت»/بازقفل، AmountUsdAtRubLock عوض می‌شود و سطر قدیمی کهنه می‌ماند.
        var existing = await _db.LedgerEntries
            .SingleOrDefaultAsync(l => l.SourceType == SupplierLoadingLedgerSourceType && l.SourceId == loading.Id);
        if (existing is not null)
        {
            var previousAmountUsd = existing.AmountUsd;
            var previousSourceAmount = existing.SourceAmount;
            var previousFxRateToUsd = existing.AppliedFxRateToUsd;
            if (!SupplierLoadingLedger.ApplySnapshot(existing, loading))
            {
                return;
            }

            await _db.SaveChangesAsync();
            await _audit.LogAndSaveAsync(
                nameof(LedgerEntry),
                existing.Id,
                AuditAction.Update,
                diff: AuditDiffFormatter.ForUpdate(
                    ("AmountUsd", previousAmountUsd, existing.AmountUsd),
                    ("SourceAmount", previousSourceAmount, existing.SourceAmount),
                    ("AppliedFxRateToUsd", previousFxRateToUsd, existing.AppliedFxRateToUsd)));
            return;
        }

        var ledger = SupplierLoadingLedger.Create(loading, contract);

        _db.LedgerEntries.Add(ledger);
        await _db.SaveChangesAsync();
        await _audit.LogAndSaveAsync(
            nameof(LedgerEntry),
            ledger.Id,
            AuditAction.Insert,
            diff: AuditDiffFormatter.ForCreate(
                ("EntryDate", ledger.EntryDate),
                ("Side", ledger.Side),
                ("AmountUsd", ledger.AmountUsd),
                ("SourceAmount", ledger.SourceAmount),
                ("SourceCurrencyCode", ledger.SourceCurrencyCode),
                ("AppliedFxRateToUsd", ledger.AppliedFxRateToUsd),
                ("SourceType", ledger.SourceType),
                ("SourceId", ledger.SourceId),
                ("ContractId", ledger.ContractId),
                ("SupplierId", ledger.SupplierId)));
    }

    private async Task PostSupplierLoadingLedgersForCreateAsync(
        IReadOnlyList<(LoadingRegister Loading, Contract Contract)> createdRows)
    {
        if (_purchaseAccounting is not null)
        {
            foreach (var row in createdRows)
            {
                await _purchaseAccounting.TryPostPurchaseAsync(row.Loading);
            }
        }

        var legacyLedgers = createdRows
            .Where(row => SupplierLoadingLedger.IsPostable(row.Loading, row.Contract))
            .Select(row => SupplierLoadingLedger.Create(row.Loading, row.Contract))
            .ToList();
        if (legacyLedgers.Count == 0)
        {
            return;
        }

        _db.LedgerEntries.AddRange(legacyLedgers);
        await _db.SaveChangesAsync();

        foreach (var ledger in legacyLedgers)
        {
            await _audit.LogAsync(
                nameof(LedgerEntry),
                ledger.Id,
                AuditAction.Insert,
                diff: AuditDiffFormatter.ForCreate(
                    ("EntryDate", ledger.EntryDate),
                    ("Side", ledger.Side),
                    ("AmountUsd", ledger.AmountUsd),
                    ("SourceAmount", ledger.SourceAmount),
                    ("SourceCurrencyCode", ledger.SourceCurrencyCode),
                    ("AppliedFxRateToUsd", ledger.AppliedFxRateToUsd),
                    ("SourceType", ledger.SourceType),
                    ("SourceId", ledger.SourceId),
                    ("ContractId", ledger.ContractId),
                    ("SupplierId", ledger.SupplierId)));
        }

        await _db.SaveChangesAsync();
    }

    private static string BuildLoadingCreateAuditDiff(LoadingRegister loading)
        => AuditDiffFormatter.ForCreate(
            ("ContractId", loading.ContractId),
            ("ProductId", loading.ProductId),
            ("OriginLocationId", loading.OriginLocationId),
            ("TransportType", loading.TransportType),
            ("VesselId", loading.VesselId),
            ("TruckId", loading.TruckId),
            ("LoadingDate", loading.LoadingDate),
            ("LoadedQuantityMt", loading.LoadedQuantityMt),
            ("BillOfLadingNumber", loading.BillOfLadingNumber),
            ("WagonNumber", loading.WagonNumber),
            ("RouteDescription", loading.RouteDescription),
            ("LogisticsServiceProviderId", loading.LogisticsServiceProviderId),
            ("LogisticsCompanyName", loading.LogisticsCompanyName),
            ("ConsigneeName", loading.ConsigneeName),
            ("DestinationName", loading.DestinationName),
            ("PlattsUsd", loading.PlattsUsd),
            ("LoadingPriceUsd", loading.LoadingPriceUsd),
            ("SettlementCurrencyCode", loading.SettlementCurrencyCode),
            ("RubRateStatus", loading.RubRateStatus),
            ("RubPerUsdRate", loading.RubPerUsdRate),
            ("RubRateDate", loading.RubRateDate),
            ("RubRateSource", loading.RubRateSource),
            ("AmountUsdAtRubLock", loading.AmountUsdAtRubLock),
            ("AmountRubAtRubLock", loading.AmountRubAtRubLock),
            ("SettlementUnitPriceRub", loading.SettlementUnitPriceRub),
            ("SettlementValueRub", loading.SettlementValueRub),
            ("FreightRateUsdPerMt", loading.FreightRateUsdPerMt),
            ("TransportExpenseUsd", loading.TransportExpenseUsd),
            ("WarehouseExpenseUsd", loading.WarehouseExpenseUsd),
            ("OtherExpenseUsd", loading.OtherExpenseUsd));

    private static IReadOnlyList<SelectListItem> GetReceiptAllocationDestinationItems(LoadingReceiptAllocationDestination selectedDestination)
        => new[]
            {
                LoadingReceiptAllocationDestination.ToInventory,
                LoadingReceiptAllocationDestination.DirectSale,
                LoadingReceiptAllocationDestination.DirectDispatchToTruck,
                LoadingReceiptAllocationDestination.TransferToOtherTerminal
            }
            .Select(destination => new SelectListItem
            {
                Value = ((int)destination).ToString(),
                Text = destination switch
                {
                    LoadingReceiptAllocationDestination.ToInventory => "ورود به موجودی / تانک",
                    LoadingReceiptAllocationDestination.DirectSale => "فروش مستقیم",
                    LoadingReceiptAllocationDestination.DirectDispatchToTruck => "بارگیری مستقیم در موتر",
                    LoadingReceiptAllocationDestination.TransferToOtherTerminal => "انتقال به ترمینال یا شهر دیگر",
                    _ => "نامشخص"
                },
                Selected = destination == selectedDestination
            })
            .ToList();

    private static void EnsureReceiptAllocationRows(LoadingReceiptCreateViewModel model)
    {
        while (model.AllocationLines.Count < ReceiptAllocationEditorRows)
        {
            model.AllocationLines.Add(new LoadingReceiptAllocationLineInput());
        }
    }

    private static LoadingReceiptCreateViewModel BuildLoadingReceiptCreateModel(
        LoadingRegister loading,
        decimal alreadyReceivedQuantityMt,
        decimal remainingToReceiveMt,
        string returnUrl)
    {
        var model = new LoadingReceiptCreateViewModel
        {
            LoadingRegisterId = loading.Id,
            ReceiptDate = AfghanistanBusinessClock.SystemToday,
            DirectDispatchDate = AfghanistanBusinessClock.SystemToday,
            ReturnUrl = returnUrl,
            ContractNumber = loading.Contract?.DisplayLabel ?? string.Empty,
            ProductName = loading.Product?.Name ?? string.Empty,
            LoadingDate = loading.LoadingDate,
            LoadedQuantityMt = loading.LoadedQuantityMt,
            AlreadyReceivedQuantityMt = alreadyReceivedQuantityMt,
            RemainingToReceiveMt = remainingToReceiveMt,
            LoadingPriceUsd = loading.LoadingPriceUsd,
            BillOfLadingNumber = loading.BillOfLadingNumber,
            RwbNo = loading.RwbNo,
            WagonNumber = loading.WagonNumber,
            VesselName = loading.Vessel?.Name,
            TruckPlateNumber = loading.Truck?.PlateNumber,
            SupplierName = loading.Contract?.Supplier?.Name,
            CustomerName = loading.Contract?.Customer?.Name,
            ConsigneeName = loading.ConsigneeName,
            DestinationName = loading.DestinationName
        };

        EnsureReceiptAllocationRows(model);
        return model;
    }

    private async Task PopulateReceiptLookupsAsync(LoadingReceiptCreateViewModel model)
    {
        var terminalLookups = await GetCachedLookupAsync(
            "loading:receipt:terminals:v1",
            () => _db.Terminals
                .AsNoTracking()
                .Where(t => t.IsActive)
                .OrderBy(t => t.Code)
                .Select(t => new LookupOption(t.Id, t.Name))
                .ToListAsync());
        ViewBag.Terminals = new SelectList(
            terminalLookups,
            "Id",
            "Name",
            model.TerminalId);

        var tankLookups = await GetCachedLookupAsync(
            "loading:receipt:storage-tanks:v2",
            async () => (await StorageTankDisplay.LoadOptionsAsync(_db.StorageTanks
                    .AsNoTracking()
                    .OrderBy(t => t.DisplayName ?? t.TankCode)))
                .Select(t => new TankLookupOption(t.Id, t.Display))
                .ToList());
        ViewBag.StorageTanks = new SelectList(
            tankLookups,
            "Id",
            "Display",
            model.StorageTankId);

        ViewBag.AllocationDestinations = GetReceiptAllocationDestinationItems(model.AllocationDestination);

        ViewBag.DestinationTerminals = new SelectList(
            terminalLookups,
            "Id",
            "Name",
            model.DestinationTerminalId);

        ViewBag.DestinationStorageTanks = new SelectList(
            tankLookups,
            "Id",
            "Display",
            model.DestinationStorageTankId);

        var destinationLookups = await GetCachedLookupAsync(
            "loading:receipt:destinations:v1",
            () => _db.Locations
                .AsNoTracking()
                .OrderBy(l => l.Name)
                .Select(l => new LookupOption(l.Id, l.Name))
                .ToListAsync());
        ViewBag.DestinationLocations = new SelectList(
            destinationLookups,
            "Id",
            "Name",
            model.DestinationLocationId);

        var customerLookups = await GetCachedLookupAsync(
            "loading:receipt:customers:v1",
            () => _db.Customers
                .AsNoTracking()
                .Where(c => c.IsActive)
                .OrderBy(c => c.Name)
                .Select(c => new LookupOption(c.Id, c.Name))
                .ToListAsync());
        ViewBag.SaleCustomers = new SelectList(
            customerLookups,
            "Id",
            "Name",
            model.SaleCustomerId);

        var truckLookups = await GetCachedLookupAsync(
            "loading:receipt:trucks:v1",
            () => _db.Trucks
                .AsNoTracking()
                .Where(t => t.IsActive)
                .OrderBy(t => t.PlateNumber)
                .Select(t => new TruckLookupOption(t.Id, t.PlateNumber))
                .ToListAsync());
        ViewBag.DirectTrucks = new SelectList(
            truckLookups,
            "Id",
            "PlateNumber",
            model.DirectTruckId);

        var driverLookups = await GetCachedLookupAsync(
            "loading:receipt:drivers:v1",
            () => _db.Drivers
                .AsNoTracking()
                .Where(d => d.IsActive)
                .OrderBy(d => d.FullName)
                .Select(d => new DriverLookupOption(d.Id, d.FullName))
                .ToListAsync());
        ViewBag.DirectDrivers = new SelectList(
            driverLookups,
            "Id",
            "FullName",
            model.DirectDriverId);
    }

    public async Task<IActionResult> Index(
        string? q = null,
        int? contractId = null,
        DateTime? fromDate = null,
        DateTime? toDate = null,
        int page = 1,
        [FromQuery(Name = "pageSize")] int? perPage = null)
    {
        var pageSize = ListPageSize.Resolve(perPage, 5);
        ViewData["PageSize"] = pageSize;
        ViewData["DefaultPageSize"] = 5;
        var exportAll = page <= 0;
        var normalizedQuery = string.IsNullOrWhiteSpace(q) ? null : q.Trim();
        ViewData["q"] = normalizedQuery;
        ViewData["contractId"] = contractId;

        var query = _db.LoadingRegisters.AsNoTracking().AsQueryable();

        string? contractNumber = null;
        if (contractId.HasValue)
        {
            query = query.Where(l => l.ContractId == contractId.Value);
            contractNumber = await _db.Contracts
                .AsNoTracking()
                .Where(c => c.Id == contractId.Value)
                .Select(c => c.ContractNumber)
                .FirstOrDefaultAsync();
        }

        if (fromDate.HasValue)
        {
            query = query.Where(l => l.LoadingDate >= fromDate.Value.Date);
        }

        if (toDate.HasValue)
        {
            var exclusiveToDate = toDate.Value.Date.AddDays(1);
            query = query.Where(l => l.LoadingDate < exclusiveToDate);
        }

        if (!string.IsNullOrWhiteSpace(normalizedQuery))
        {
            query = query.Where(l =>
                (l.Contract != null && (l.Contract.ContractName.Contains(normalizedQuery) || l.Contract.ContractNumber.Contains(normalizedQuery))) ||
                (l.Product != null && l.Product.Name.Contains(normalizedQuery)) ||
                (l.WagonNumber != null && l.WagonNumber.Contains(normalizedQuery)) ||
                (l.BillOfLadingNumber != null && l.BillOfLadingNumber.Contains(normalizedQuery)) ||
                (l.DestinationName != null && l.DestinationName.Contains(normalizedQuery))
            );
        }

        // آمار کامل روی همهٔ رکوردهای مطابق فیلتر در یک رفت‌وبرگشت واحد به‌جای
        // چهار کوئری Sum/Count جدا (count + ۴ aggregate → یک کوئری). مقادیر یکسان‌اند
        // (با benchmark و مقایسهٔ برابری تأیید شد)؛ فقط تعداد round-trip کم می‌شود.
        var stats = await query
            .GroupBy(_ => 1)
            .Select(g => new
            {
                TotalCount = g.Count(),
                SumQuantity = g.Sum(l => l.LoadedQuantityMt),
                SumValue = g.Sum(l => l.LoadedQuantityMt * (l.LoadingPriceUsd ?? 0m)),
                SumReceivedQuantity = g.Sum(l => l.Receipts.Where(r => !r.IsCancelled).Sum(r => (decimal?)r.ReceivedQuantityMt) ?? 0m),
                PricePendingCount = g.Count(l => l.LoadingPriceUsd == null || l.LoadingPriceUsd <= 0m)
            })
            .FirstOrDefaultAsync();

        var totalCount = stats?.TotalCount ?? 0;
        var pageCount = Math.Max(1, (int)Math.Ceiling((double)totalCount / pageSize));
        page = Math.Clamp(page, 1, pageCount);

        var loadings = await query
            .OrderByDescending(l => l.LoadingDate)
            .ThenByDescending(l => l.Id)
            .Skip(exportAll ? 0 : (page - 1) * pageSize)
            .Take(exportAll ? totalCount : pageSize)
            .Select(l => new
            {
                l.Id,
                l.ContractId,
                l.LoadingDate,
                l.TransportType,
                l.VesselId,
                l.TruckId,
                l.WagonNumber,
                ContractName = l.Contract != null ? l.Contract.ContractName : "",
                ContractNumber = l.Contract != null ? l.Contract.ContractNumber : "",
                ProductName = l.Product != null ? l.Product.Name : "",
                OriginLocationName = l.OriginLocation != null ? l.OriginLocation.Name : null,
                VesselName = l.Vessel != null ? l.Vessel.Name : null,
                TruckPlateNumber = l.Truck != null ? l.Truck.PlateNumber : null,
                l.LoadedQuantityMt,
                TotalReceivedQuantityMt = l.Receipts.Where(r => !r.IsCancelled).Sum(r => (decimal?)r.ReceivedQuantityMt) ?? 0m,
                l.BillOfLadingNumber,
                l.RouteDescription,
                l.LogisticsServiceProviderId,
                l.LogisticsCompanyName,
                l.ConsigneeName,
                l.DestinationName,
                l.PlattsUsd,
                l.LoadingPriceUsd,
                l.FreightRateUsdPerMt,
                l.TransportExpenseUsd,
                l.WarehouseExpenseUsd,
                l.OtherExpenseUsd,
                l.ChargeableQuantityMt,
                l.RailwayRateUsd,
                l.RailwayExpenseUsd,
                l.SettlementCurrencyCode,
                l.RubRateStatus,
                l.RubPerUsdRate,
                l.RubRateDate,
                l.RubRateSource,
                l.AmountUsdAtRubLock,
                l.AmountRubAtRubLock,
                l.SettlementUnitPriceRub,
                l.SettlementValueRub,
                l.Notes
            })
            .ToListAsync();

        var items = loadings
            .Select(l =>
            {
                var transportType = ResolveTransportType(l.TransportType, l.VesselId, l.TruckId, l.WagonNumber);

                return new LoadingListItemViewModel
                {
                    Id = l.Id,
                    ContractId = l.ContractId,
                    LoadingDate = l.LoadingDate,
                    TransportType = transportType,
                    TransportTypeLabel = GetTransportTypeLabel(transportType),
                    ContractName = l.ContractName,
                    ContractNumber = l.ContractNumber,
                    ProductName = l.ProductName,
                    OriginLocationName = l.OriginLocationName,
                    VehicleSummary = BuildVehicleSummary(
                        transportType,
                        l.VesselName,
                        l.TruckPlateNumber,
                        l.WagonNumber),
                    LoadedQuantityMt = l.LoadedQuantityMt,
                    TotalReceivedQuantityMt = l.TotalReceivedQuantityMt,
                    BillOfLadingNumber = l.BillOfLadingNumber,
                    WagonNumber = l.WagonNumber,
                    RouteDescription = l.RouteDescription,
                    LogisticsServiceProviderId = l.LogisticsServiceProviderId,
                    LogisticsCompanyName = l.LogisticsCompanyName,
                    ConsigneeName = l.ConsigneeName,
                    DestinationName = l.DestinationName,
                    PlattsUsd = l.PlattsUsd,
                    LoadingPriceUsd = l.LoadingPriceUsd,
                    LoadingValueUsd = CalculateLoadingValueUsd(l.LoadedQuantityMt, l.LoadingPriceUsd),
                    FreightRateUsdPerMt = l.FreightRateUsdPerMt,
                    TransportExpenseUsd = l.TransportExpenseUsd,
                    WarehouseExpenseUsd = l.WarehouseExpenseUsd,
                    OtherExpenseUsd = l.OtherExpenseUsd,
                    ChargeableQuantityMt = l.ChargeableQuantityMt,
                    RailwayRateUsd = l.RailwayRateUsd,
                    RailwayExpenseUsd = l.RailwayExpenseUsd,
                    SettlementCurrencyCode = l.SettlementCurrencyCode,
                    RubRateStatus = l.RubRateStatus,
                    RubPerUsdRate = l.RubPerUsdRate,
                    RubRateDate = l.RubRateDate,
                    RubRateSource = l.RubRateSource,
                    AmountUsdAtRubLock = l.AmountUsdAtRubLock,
                    AmountRubAtRubLock = l.AmountRubAtRubLock,
                    SettlementUnitPriceRub = l.SettlementUnitPriceRub,
                    SettlementValueRub = l.SettlementValueRub,
                    Notes = l.Notes
                };
            })
            .ToList();

        // آمار کارت‌ها و ردیف جمع از همان کوئری ترکیبیِ بالا (بدون کوئری اضافه).
        ViewBag.SumQuantity = stats?.SumQuantity ?? 0m;
        ViewBag.SumValue = stats?.SumValue ?? 0m;
        ViewBag.SumReceivedQuantity = stats?.SumReceivedQuantity ?? 0m;
        ViewBag.PricePendingCount = stats?.PricePendingCount ?? 0;

        return View(new LoadingIndexViewModel
        {
            Items = items,
            CurrentPage = page,
            PageCount = pageCount,
            TotalCount = totalCount,
            ContractId = contractId,
            ContractNumber = contractNumber,
            Query = normalizedQuery,
            FromDate = fromDate,
            ToDate = toDate
        });
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    public async Task<IActionResult> Create(int? contractId = null, string? returnUrl = null, bool lockContract = false)
    {
        var model = new LoadingCreateViewModel
        {
            LoadingDate = _businessClock.Today,
            TransportType = LoadingTransportType.Unspecified,
            LockContract = false,
            Rows =
            [
                new LoadingCreateRowViewModel
                {
                    RowKey = CreateRowKey(0),
                    LoadingDate = _businessClock.Today
                }
            ]
        };

        if (contractId.HasValue)
        {
            var contract = await _db.Contracts
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == contractId.Value && c.ContractType == ContractType.Purchase);
            if (contract is not null)
            {
                model.ContractId = contract.Id;
                model.SelectedContractIds = [contract.Id];
                model.ProductId = contract.ProductId;
                model.LockContract = lockContract;
                ApplyContractRubDefaults(contract, model.Rows);
            }
        }

        model.ReturnUrl = returnUrl;

        await PopulateLookupsAsync(model);
        return View(model);
    }

    [HttpGet]
    public async Task<IActionResult> SuggestedPricing(int? contractId)
    {
        if (!contractId.HasValue || contractId <= 0)
        {
            return Json(new
            {
                ok = false,
                isFormulaPlatts = false,
                basePlattsPrice = (decimal?)null,
                premiumDiscountUsd = (decimal?)null,
                finalUnitPrice = (decimal?)null,
                formulaText = string.Empty,
                needsReview = true,
                reason = "قرارداد انتخاب نشده",
                fallbackApplied = false
            });
        }

        var contract = await _db.Contracts
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == contractId.Value);

        if (contract is null)
        {
            return Json(new
            {
                ok = false,
                isFormulaPlatts = false,
                basePlattsPrice = (decimal?)null,
                premiumDiscountUsd = (decimal?)null,
                finalUnitPrice = (decimal?)null,
                formulaText = string.Empty,
                needsReview = true,
                reason = "قرارداد یافت نشد",
                fallbackApplied = false
            });
        }

        var result = await _pricing.CalculateContractPriceAsync(contract.Id);
        var plattsReference = await ResolvePlattsReferenceSuggestionAsync(contract);

        return Json(new
        {
            ok = result.FinalUnitPrice.HasValue,
            isFormulaPlatts = contract.PricingMethod == PricingMethod.FormulaPlatts,
            basePlattsPrice = result.BasePlattsPrice,
            plattsReferencePrice = plattsReference.PriceUsd,
            plattsReferenceSource = plattsReference.Source,
            plattsReferenceDate = plattsReference.EffectiveDate?.ToString("yyyy-MM-dd"),
            plattsReferenceFallbackApplied = plattsReference.FallbackApplied,
            plattsReferenceReason = plattsReference.Reason,
            premiumDiscountUsd = result.PremiumDiscountUsd,
            finalUnitPrice = result.FinalUnitPrice,
            formulaText = result.FormulaText,
            needsReview = result.NeedsReview,
            reason = result.Reason,
            fallbackApplied = result.FallbackApplied,
            settlementCurrencyCode = contract.SettlementCurrencyCode,
            rubRatePolicy = contract.RubRatePolicy.ToString(),
            rubPerUsdRate = contract.ContractRubPerUsdRate,
            rubRateDate = contract.ContractRubRateDate?.ToString("yyyy-MM-dd"),
            rubRateSource = contract.ContractRubRateSource
        });
    }

    private async Task<PlattsReferenceSuggestion> ResolvePlattsReferenceSuggestionAsync(Contract contract)
    {
        if (contract.PricingMethod != PricingMethod.FormulaPlatts)
        {
            return new(null, string.Empty, null, false, string.Empty);
        }

        var manualPrice = NormalizePositiveDecimal(contract.PlattsManualPriceUsd);
        if (manualPrice.HasValue)
        {
            return new(manualPrice.Value, "manual", null, false, string.Empty);
        }

        var benchmarkCode = contract.BenchmarkCode?.Trim();
        if (string.IsNullOrWhiteSpace(benchmarkCode))
        {
            return new(null, string.Empty, null, false, "Benchmark قرارداد Platts ثبت نشده است.");
        }

        if (contract.PlattsPeriodType == PlattsPeriodType.Daily && contract.PlattsBasisDate.HasValue)
        {
            try
            {
                var lookup = await _pricing.GetPlattsPriceAsync(
                    contract.ProductId,
                    benchmarkCode,
                    NormalizeDate(contract.PlattsBasisDate.Value));

                return new(lookup.Value, "daily", lookup.EffectiveDate, lookup.FallbackApplied, string.Empty);
            }
            catch (BusinessRuleException ex)
            {
                return new(null, "daily", null, false, ex.Message);
            }
        }

        if (contract.PlattsPeriodType == PlattsPeriodType.Monthly && contract.PlattsBasisMonth.HasValue)
        {
            var month = NormalizeMonth(contract.PlattsBasisMonth.Value);
            var monthlyPrice = await _db.PlattsMonthlyManuals
                .AsNoTracking()
                .Where(p => p.ProductId == contract.ProductId
                    && p.BenchmarkCode == benchmarkCode
                    && p.Month == month)
                .Select(p => (decimal?)p.PriceUsdPerMt)
                .FirstOrDefaultAsync();

            if (monthlyPrice.HasValue)
            {
                return new(monthlyPrice.Value, "monthly", month, false, string.Empty);
            }

            return new(null, "monthly", month, false, "قیمت ماهانه Platts برای قرارداد ثبت نشده است.");
        }

        return new(null, string.Empty, null, false, "تنظیمات Platts قرارداد کامل نیست.");
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    [RequestFormLimits(ValueCountLimit = 20000, ValueLengthLimit = 104_857_600, MultipartBodyLengthLimit = 104_857_600L)]
    public async Task<IActionResult> ImportWorkbook(LoadingCreateViewModel model)
    {
        model.Notes = NormalizeNullable(model.Notes);
        model.Rows = ExtractSubmittedRows(model);
        ApplyContractLock(model);
        var selectedContractIds = ResolveSelectedContractIds(model);
        if (selectedContractIds.Count > 0)
        {
            var selectedProductIds = await _db.Contracts
                .AsNoTracking()
                .Where(c => selectedContractIds.Contains(c.Id) && c.ContractType == ContractType.Purchase)
                .Select(c => c.ProductId)
                .Distinct()
                .ToListAsync();

            if (selectedProductIds.Count == 1)
            {
                model.ProductId = selectedProductIds[0];
                ModelState.Remove(nameof(model.ProductId));
            }
            else if (selectedProductIds.Count > 1)
            {
                ModelState.AddModelError(nameof(model.ContractId), "قراردادهای انتخاب‌شده باید کالای یکسان داشته باشند.");
                await PopulateLookupsAsync(model);
                return View("Create", model);
            }
        }

        if (model.ImportWorkbookFile is null || model.ImportWorkbookFile.Length == 0)
        {
            ModelState.AddModelError(nameof(model.ImportWorkbookFile), "فایل اکسل بارگیری را انتخاب کنید.");
            await PopulateLookupsAsync(model);
            return View("Create", model);
        }

        if (!string.Equals(Path.GetExtension(model.ImportWorkbookFile.FileName), ".xlsx", StringComparison.OrdinalIgnoreCase))
        {
            ModelState.AddModelError(nameof(model.ImportWorkbookFile), "فعلاً فقط فایل‌های Excel با پسوند .xlsx پشتیبانی می‌شوند.");
            await PopulateLookupsAsync(model);
            return View("Create", model);
        }

        try
        {
            await using var stream = model.ImportWorkbookFile.OpenReadStream();
            var importedWorkbook = LoadingWorkbookParser.Parse(stream);
            model.ImportedSheetName = importedWorkbook.SourceSheetName;

            model.Rows = importedWorkbook.Rows
                .Select((row, index) =>
                {
                    row.RowKey = CreateRowKey(index);
                    if (selectedContractIds.Count == 1)
                    {
                        row.ContractId = selectedContractIds[0];
                    }

                    return row;
                })
                .ToList();

            model.TransportType = importedWorkbook.TransportType;

            if (model.TransportType == LoadingTransportType.Truck)
            {
                await ResolveImportedTruckReferencesAsync(model.Rows, createMissing: false);
            }

            if (!model.OriginLocationId.HasValue)
            {
                model.OriginLocationId = await ResolveLocationIdAsync(importedWorkbook.OriginLocationName);
            }

            if (model.Rows.Count > 0)
            {
                model.LoadingDate = importedWorkbook.ReportDate ?? model.Rows[0].LoadingDate;
            }

            if (selectedContractIds.Count == 1)
            {
                var contract = await _db.Contracts.AsNoTracking().FirstOrDefaultAsync(c => c.Id == selectedContractIds[0]);
                if (contract is not null
                    && contract.ContractType == ContractType.Purchase
                    && contract.ProductId == model.ProductId)
                {
                    var pricingResult = await _pricing.CalculateContractPriceAsync(contract.Id);
                    ApplyContractPricingDefaults(contract, pricingResult, model.Rows, addValidationErrors: false);
                    ApplyContractRubDefaults(contract, model.Rows);
                }
            }

            // The workbook replaces posted row and transport values; clear stale attempted
            // values so the returned form renders the imported model instead of pre-import input.
            ModelState.Clear();

            TempData["ok"] = $"{model.Rows.Count} سطر از فایل اکسل بارگیری وارد فرم شد.";
        }
        catch (InvalidDataException ex)
        {
            ModelState.AddModelError(nameof(model.ImportWorkbookFile), ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to import loading workbook.");
            ModelState.AddModelError(nameof(model.ImportWorkbookFile), "خواندن فایل اکسل بارگیری انجام نشد. ساختار فایل را بررسی کنید.");
        }

        await PopulateLookupsAsync(model);
        PrepareRowsForFastPreview(model);
        return View("Create", model);
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    [RequestFormLimits(ValueCountLimit = 20000, ValueLengthLimit = 104_857_600, MultipartBodyLengthLimit = 104_857_600L)]
    public async Task<IActionResult> Create(LoadingCreateViewModel model)
    {
        model.Notes = NormalizeNullable(model.Notes);
        var submittedWithCompactRows = !string.IsNullOrWhiteSpace(model.ImportedRowsJson);
        model.Rows = ExtractSubmittedRows(model);
        if (submittedWithCompactRows)
        {
            ValidateCompactSubmittedRows(model.Rows);
        }
        if (model.Rows.Count == 0)
        {
            ModelState.AddModelError(nameof(model.Rows), "حداقل یک سطر بارگیری الزامی است.");
            model.Rows =
            [
                new LoadingCreateRowViewModel
                {
                    RowKey = CreateRowKey(0),
                    LoadingDate = model.LoadingDate == default ? _businessClock.Today : model.LoadingDate
                }
            ];
        }

        EnsureEditableRows(model);
        ApplyContractLock(model);
        var selectedContractIds = ResolveSelectedContractIds(model);
        if (selectedContractIds.Count > 0)
        {
            ModelState.Remove(nameof(model.ContractId));
        }

        var useRowContracts = selectedContractIds.Count > 1;
        var selectedContracts = new List<Contract>();
        if (selectedContractIds.Count > 0)
        {
            selectedContracts = await _db.Contracts
                .AsNoTracking()
                .Where(c => selectedContractIds.Contains(c.Id))
                .ToListAsync();
        }

        var contractsById = selectedContracts.ToDictionary(c => c.Id);
        var selectedProductIds = selectedContracts
            .Where(c => c.ContractType == ContractType.Purchase)
            .Select(c => c.ProductId)
            .Distinct()
            .ToList();

        if (selectedProductIds.Count == 1)
        {
            model.ProductId = selectedProductIds[0];
            ModelState.Remove(nameof(model.ProductId));
        }

        var product = await _db.Products.AsNoTracking().FirstOrDefaultAsync(p => p.Id == model.ProductId && p.IsActive);
        if (product is null)
        {
            ModelState.AddModelError(nameof(model.ProductId), "کالای انتخاب‌شده معتبر نیست.");
        }

        var contract = await _db.Contracts.AsNoTracking().FirstOrDefaultAsync(c => c.Id == model.ContractId);
        if (contract is null || contract.ContractType != ContractType.Purchase)
        {
            ModelState.AddModelError(nameof(model.ContractId), "قرارداد خرید انتخاب‌شده معتبر نیست.");
        }

        if (selectedContractIds.Count == 0)
        {
            ModelState.AddModelError(nameof(model.ContractId), "Select at least one purchase contract.");
        }

        foreach (var missingContractId in selectedContractIds.Where(id => !contractsById.ContainsKey(id)))
        {
            ModelState.AddModelError(nameof(model.ContractId), $"Selected purchase contract #{missingContractId} is not valid.");
        }

        foreach (var invalidContract in selectedContracts.Where(c => c.ContractType != ContractType.Purchase))
        {
            ModelState.AddModelError(nameof(model.ContractId), $"Selected contract {invalidContract.ContractNumber} is not a purchase contract.");
        }

        if (selectedProductIds.Count > 1)
        {
            ModelState.AddModelError(nameof(model.ContractId), "All selected purchase contracts must have the same product.");
        }

        if (model.OriginLocationId.HasValue)
        {
            var locationExists = await _db.Locations.AsNoTracking().AnyAsync(l => l.Id == model.OriginLocationId.Value);
            if (!locationExists)
            {
                ModelState.AddModelError(nameof(model.OriginLocationId), "مبدأ انتخاب‌شده معتبر نیست.");
            }
        }

        if (model.TransportType == LoadingTransportType.Unspecified)
        {
            ModelState.AddModelError(nameof(model.TransportType), "انتخاب نوع حمل و نقل الزامی است.");
        }

        var selectedLogisticsServiceProviderIds = model.Rows
            .Select(r => r.LogisticsServiceProviderId)
            .Where(id => id.HasValue && id.Value > 0)
            .Select(id => id!.Value)
            .Distinct()
            .ToList();
        var selectedOperationalAssetIds = model.Rows
            .Select(r => r.OperationalAssetId)
            .Where(id => id.HasValue && id.Value > 0)
            .Select(id => id!.Value)
            .Distinct()
            .ToList();
        var logisticsServiceProvidersById = selectedLogisticsServiceProviderIds.Count == 0
            ? new Dictionary<int, ServiceProviderEntity>()
            : await _db.ServiceProviders
                .AsNoTracking()
                .Where(p => selectedLogisticsServiceProviderIds.Contains(p.Id) && p.IsActive)
                .ToDictionaryAsync(p => p.Id);
        var operationalAssetsById = selectedOperationalAssetIds.Count == 0
            ? new Dictionary<int, OperationalAsset>()
            : await _db.OperationalAssets
                .AsNoTracking()
                .Where(a => selectedOperationalAssetIds.Contains(a.Id) && a.IsActive)
                .ToDictionaryAsync(a => a.Id);
        var selectedTruckIds = model.TransportType == LoadingTransportType.Truck
            ? model.Rows
                .Where(row => row.TruckId.HasValue && row.TruckId.Value > 0)
                .Select(row => row.TruckId!.Value)
                .Distinct()
                .ToList()
            : [];
        var activeTruckIds = selectedTruckIds.Count == 0
            ? []
            : (await _db.Trucks
                .AsNoTracking()
                .Where(truck => selectedTruckIds.Contains(truck.Id) && truck.IsActive)
                .Select(truck => truck.Id)
                .ToListAsync())
                .ToHashSet();
        var ownershipSharesByAssetId = new Dictionary<int, List<AssetOwnershipShare>>();

        foreach (var row in model.Rows)
        {
            NormalizeRow(row, model.TransportType, model.LoadingDate);
            if (!model.RecordFreight)
            {
                ClearFreightSnapshot(row);
            }

            var effectiveContractId = ResolveEffectiveRowContractId(model, row, useRowContracts);

            if (row.LogisticsServiceProviderId.HasValue)
            {
                if (!logisticsServiceProvidersById.TryGetValue(row.LogisticsServiceProviderId.Value, out var logisticsServiceProvider))
                {
                    ModelState.AddModelError(RowField(row.RowKey, nameof(row.LogisticsServiceProviderId)), "شرکت خدماتی / لوجستیکی انتخاب‌شده معتبر نیست.");
                }
                else
                {
                    row.LogisticsCompanyName = logisticsServiceProvider.Name;
                }
            }

            if (row.OperationalAssetId.HasValue)
            {
                if (!operationalAssetsById.TryGetValue(row.OperationalAssetId.Value, out var operationalAsset))
                {
                    ModelState.AddModelError(RowField(row.RowKey, nameof(row.OperationalAssetId)), "دارایی عملیاتی انتخاب‌شده معتبر یا فعال نیست.");
                }
                else
                {
                    row.LogisticsCompanyName = operationalAsset.Name;
                    var ownershipShares = await GetActiveAssetOwnershipSharesAsync(row.OperationalAssetId.Value, row.LoadingDate);
                    ownershipSharesByAssetId[row.OperationalAssetId.Value] = ownershipShares;
                    var ownershipTotal = ownershipShares.Sum(s => s.SharePercent);
                    if (ownershipShares.Count == 0 || decimal.Round(ownershipTotal, 4, MidpointRounding.AwayFromZero) != 100m)
                    {
                        ModelState.AddModelError(RowField(row.RowKey, nameof(row.OperationalAssetId)), "برای ثبت کرایه داخلی دارایی، مجموع سهم مالکیت فعال در تاریخ بارگیری باید 100٪ باشد.");
                    }
                }
            }

            if (row.LogisticsServiceProviderId.HasValue && row.OperationalAssetId.HasValue)
            {
                ModelState.AddModelError(RowField(row.RowKey, nameof(row.OperationalAssetId)), "برای هر ردیف بارگیری فقط یکی از شرکت خدماتی بیرونی یا دارایی عملیاتی ملکی را انتخاب کنید.");
            }

            if (model.RecordFreight)
            {
                ApplyLogisticsFreightSnapshot(row, model.TransportType);
            }

            if (useRowContracts)
            {
                if (!effectiveContractId.HasValue || effectiveContractId.Value <= 0)
                {
                    ModelState.AddModelError(RowField(row.RowKey, nameof(row.ContractId)), "Select a purchase contract for this loading row.");
                }
                else if (!contractsById.ContainsKey(effectiveContractId.Value))
                {
                    ModelState.AddModelError(RowField(row.RowKey, nameof(row.ContractId)), "The row purchase contract is not part of the selected contracts.");
                }
            }
            else if (effectiveContractId.HasValue)
            {
                row.ContractId = effectiveContractId.Value;
            }

            if (row.LoadedQuantityMt <= 0)
            {
                ModelState.AddModelError(RowField(row.RowKey, nameof(row.LoadedQuantityMt)), "مقدار بارگیری باید بزرگ‌تر از صفر باشد.");
            }

            switch (model.TransportType)
            {
                case LoadingTransportType.Vessel:
                    if (string.IsNullOrWhiteSpace(row.ImportedTransportReference))
                    {
                        ModelState.AddModelError(RowField(row.RowKey, nameof(row.ImportedTransportReference)), "نام کشتی برای این سطر الزامی است.");
                    }
                    break;

                case LoadingTransportType.Wagon:
                    if (string.IsNullOrWhiteSpace(row.WagonNumber))
                    {
                        ModelState.AddModelError(RowField(row.RowKey, nameof(row.WagonNumber)), "شماره واگن برای این سطر الزامی است.");
                    }
                    break;

                case LoadingTransportType.Truck:
                    if (!row.TruckId.HasValue)
                    {
                        if (string.IsNullOrWhiteSpace(row.ImportedTransportReference))
                        {
                            ModelState.AddModelError(RowField(row.RowKey, nameof(row.TruckId)), "انتخاب موتر برای این سطر الزامی است.");
                        }

                        break;
                    }

                    if (!activeTruckIds.Contains(row.TruckId.Value))
                    {
                        ModelState.AddModelError(RowField(row.RowKey, nameof(row.TruckId)), "موتر انتخاب‌شده معتبر نیست.");
                    }
                    break;
            }

            StageLossCaptureMapper.Validate(
                row.Loss,
                (field, error) => ModelState.AddModelError(RowLossField(row.RowKey, field), error));

            if (row.Loss.Enabled)
            {
                if (effectiveContractId.HasValue)
                {
                    await _lossWorkflow.ValidateAsync(
                        BuildLoadingLossSubmission(model, row, effectiveContractId.Value),
                        (field, error) => ModelState.AddModelError(RowLossField(row.RowKey, field), error));
                }
            }
        }

        var rowsByContract = model.Rows
            .Select(row => new
            {
                Row = row,
                ContractId = ResolveEffectiveRowContractId(model, row, useRowContracts)
            })
            .Where(row => row.ContractId.HasValue
                && contractsById.ContainsKey(row.ContractId.Value)
                && contractsById[row.ContractId.Value].ContractType == ContractType.Purchase
                && contractsById[row.ContractId.Value].ProductId == model.ProductId)
            .GroupBy(row => row.ContractId!.Value)
            .ToList();

        foreach (var contractRows in rowsByContract)
        {
            var rowContract = contractsById[contractRows.Key];
            var pricingResult = await _pricing.CalculateContractPriceAsync(rowContract.Id);
            ApplyContractPricingDefaults(rowContract, pricingResult, contractRows.Select(row => row.Row), addValidationErrors: true);
            ApplyContractRubDefaults(rowContract, contractRows.Select(row => row.Row));

            foreach (var contractRow in contractRows.Select(row => row.Row))
            {
                if (!IsRubSettlement(contractRow.SettlementCurrencyCode))
                {
                    continue;
                }

                if (contractRow.RubRatePolicy == RubSettlementRatePolicy.PerLoadingRate
                    && !contractRow.RubPerUsdRate.HasValue)
                {
                    ModelState.AddModelError(
                        RowField(contractRow.RowKey, nameof(contractRow.RubPerUsdRate)),
                        "نرخ RUB همین بارگیری الزامی است.");
                }
                else if (contractRow.RubRatePolicy == RubSettlementRatePolicy.FixedContractRate
                         && !contractRow.RubPerUsdRate.HasValue)
                {
                    ModelState.AddModelError(
                        RowField(contractRow.RowKey, nameof(contractRow.RubPerUsdRate)),
                        "نرخ ثابت RUB در قرارداد تکمیل نشده است.");
                }
            }
        }

        if (!ModelState.IsValid)
        {
            await PopulateLookupsAsync(model);
            PrepareRowsForFastPreview(model);
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
                if (useRowContracts)
                {
                    var requestedRowsByContract = model.Rows
                        .Select(row => new
                        {
                            Row = row,
                            ContractId = ResolveEffectiveRowContractId(model, row, useRowContracts)
                        })
                        .Where(row => row.ContractId.HasValue)
                        .GroupBy(row => row.ContractId!.Value)
                        .ToList();

                    foreach (var contractRows in requestedRowsByContract)
                    {
                        var lockedRowContract = await LockPurchaseContractAsync(contractRows.Key);
                        if (lockedRowContract is null)
                        {
                            ModelState.AddModelError(nameof(model.ContractId), $"Selected purchase contract #{contractRows.Key} is no longer valid.");
                            continue;
                        }

                        var committedLoadedQuantityMt = await GetCommittedLoadedQuantityMtAsync(contractRows.Key);
                        var requestedLoadedQuantityMt = contractRows.Sum(row => row.Row.LoadedQuantityMt);
                        var remainingToLoadMt = Math.Max(lockedRowContract.QuantityMt - committedLoadedQuantityMt, 0m);

                        if (requestedLoadedQuantityMt > remainingToLoadMt)
                        {
                            ModelState.AddModelError(
                                nameof(model.Rows),
                                $"Loading quantity for contract {lockedRowContract.ContractNumber} exceeds remaining quantity. Remaining: {remainingToLoadMt:N4} MT.");
                        }
                    }
                }
                else
                {
                    var lockedContract = await LockPurchaseContractAsync(model.ContractId);
                    if (lockedContract is null)
                    {
                        ModelState.AddModelError(nameof(model.ContractId), "قرارداد خرید انتخاب‌شده دیگر معتبر نیست.");
                    }
                    else
                    {
                        var committedLoadedQuantityMt = await GetCommittedLoadedQuantityMtAsync(model.ContractId);
                        var requestedLoadedQuantityMt = model.Rows.Sum(row => row.LoadedQuantityMt);
                        var remainingToLoadMt = Math.Max(lockedContract.QuantityMt - committedLoadedQuantityMt, 0m);

                        if (requestedLoadedQuantityMt > remainingToLoadMt)
                        {
                            ModelState.AddModelError(
                                nameof(model.Rows),
                                $"جمع مقدار سطرهای بارگیری از باقیمانده قرارداد بیشتر است. باقیمانده فعلی قرارداد: {remainingToLoadMt:N4} MT.");
                        }
                    }

                }

                var autoCreatedTrucks = new List<string>();
                if (ModelState.IsValid && model.TransportType == LoadingTransportType.Truck)
                {
                    autoCreatedTrucks = await ResolveImportedTruckReferencesAsync(model.Rows, createMissing: true);
                    foreach (var row in model.Rows.Where(r => !r.TruckId.HasValue))
                    {
                        ModelState.AddModelError(
                            RowField(row.RowKey, nameof(row.ImportedTransportReference)),
                            $"موتر واردشده برای این سطر در سیستم قابل تطبیق نیست: {row.ImportedTransportReference ?? "نامشخص"}");
                    }
                }

                var autoCreatedVessels = new List<string>();
                if (ModelState.IsValid && model.TransportType == LoadingTransportType.Vessel)
                {
                    autoCreatedVessels = await ResolveImportedVesselReferencesAsync(model.Rows, createMissing: true);
                    foreach (var row in model.Rows.Where(r => !r.VesselId.HasValue))
                    {
                        ModelState.AddModelError(
                            RowField(row.RowKey, nameof(row.ImportedTransportReference)),
                            $"کشتی واردشده برای این سطر قابل شناسایی نیست: {row.ImportedTransportReference ?? "نامشخص"}");
                    }
                }

                if (!ModelState.IsValid)
                {
                    if (transaction is not null)
                    {
                        await transaction.RollbackAsync();
                    }

                    await PopulateLookupsAsync(model);
                    PrepareRowsForFastPreview(model);
                    return View(model);
                }

                var createdRows = new List<(
                    LoadingCreateRowViewModel Row,
                    LoadingRegister Loading,
                    Contract Contract)>(model.Rows.Count);

                // همان شمارندهٔ تکرارِ مرحلهٔ اعتبارسنجی، تا کلید ردیف‌ها در ثبت نهایی یکسان بماند.
                var importKeyOccurrences = new LoadingImportKey.OccurrenceTracker();
                foreach (var row in model.Rows)
                {
                    var effectiveContractId = ResolveEffectiveRowContractId(model, row, useRowContracts);
                    if (!effectiveContractId.HasValue)
                    {
                        throw new InvalidOperationException("Loading row contract was not resolved.");
                    }

                    var rowContract = contractsById[effectiveContractId.Value];
                    ApplyContractRubDefaults(rowContract, row);

                    var loading = new LoadingRegister
                    {
                        ContractId = effectiveContractId.Value,
                        ProductId = model.ProductId,
                        OriginLocationId = model.OriginLocationId,
                        TransportType = model.TransportType,
                        VesselId = model.TransportType == LoadingTransportType.Vessel ? row.VesselId : null,
                        TruckId = model.TransportType == LoadingTransportType.Truck ? row.TruckId : null,
                        LoadingDate = row.LoadingDate,
                        LoadedQuantityMt = row.LoadedQuantityMt,
                        BillOfLadingNumber = row.BillOfLadingNumber,
                        WagonNumber = model.TransportType == LoadingTransportType.Wagon ? row.WagonNumber : null,
                        RouteDescription = row.RouteDescription,
                        LogisticsServiceProviderId = row.LogisticsServiceProviderId,
                        LogisticsCompanyName = row.LogisticsCompanyName,
                        ConsigneeName = row.ConsigneeName,
                        DestinationName = row.DestinationName,
                        PlattsUsd = row.PlattsUsd,
                        LoadingPriceUsd = row.LoadingPriceUsd,
                        FreightRateUsdPerMt = row.FreightRateUsdPerMt,
                        TransportExpenseUsd = row.TransportExpenseUsd,
                        WarehouseExpenseUsd = row.WarehouseExpenseUsd,
                        OtherExpenseUsd = row.OtherExpenseUsd,
                        ChargeableQuantityMt = row.ChargeableQuantityMt,
                        RailwayRateUsd = row.RailwayRateUsd,
                        RailwayExpenseUsd = row.RailwayExpenseUsd,
                        // ارقام روبلی فایل (فقط ایمپورت/نمایش؛ بدون اثر بر لِجر/پرداخت/موجودی).
                        SettlementUnitPriceRub = row.SettlementUnitPriceRub,
                        SettlementValueRub = row.SettlementValueRub,
                        // Phase 1 — مسئول کرایه (فقط ثبت/نمایش، به حسابداری اثر ندارد).
                        FreightCostResponsibility = model.FreightCostResponsibility,
                        Notes = model.Notes
                    };
                    loading.ImportUniqueKey = LoadingImportKey.Build(
                        loading.ContractId,
                        loading.BillOfLadingNumber ?? loading.RwbNo,
                        loading.WagonNumber ?? row.ImportedTransportReference,
                        loading.LoadingDate,
                        importKeyOccurrences,
                        loading.LoadedQuantityMt);
                    ApplyRubSnapshotToLoading(loading, row);
                    if (loading.RubRateStatus == RubSettlementRateStatus.Locked)
                    {
                        loading.RubRateLockedByUserName = User?.Identity?.Name;
                    }

                    createdRows.Add((row, loading, rowContract));
                }

                // گارد ضد ثبت تکراری: همان شمارهٔ سند/حمل داخل همان قرارداد نباید دو بار ثبت شود.
                // پیام روشن اینجا داده می‌شود؛ Unique Index دیتابیس فقط پشتیبان نهایی است.
                if (!await ValidateNoDuplicateLoadingsAsync(createdRows.Select(row => (row.Row, row.Loading)).ToList()))
                {
                    if (transaction is not null)
                    {
                        await transaction.RollbackAsync();
                    }

                    await PopulateLookupsAsync(model);
                    PrepareRowsForFastPreview(model);
                    return View(model);
                }

                _db.LoadingRegisters.AddRange(createdRows.Select(row => row.Loading));
                await _db.SaveChangesAsync();

                foreach (var createdRow in createdRows)
                {
                    await _audit.LogAsync(
                        nameof(LoadingRegister),
                        createdRow.Loading.Id,
                        AuditAction.Insert,
                        diff: BuildLoadingCreateAuditDiff(createdRow.Loading));
                }

                await _db.SaveChangesAsync();
                await PostSupplierLoadingLedgersForCreateAsync(
                    createdRows.Select(row => (row.Loading, row.Contract)).ToList());

                foreach (var createdRow in createdRows)
                {
                    var row = createdRow.Row;
                    var loading = createdRow.Loading;

                    if (loading.LogisticsServiceProviderId.HasValue
                        && logisticsServiceProvidersById.TryGetValue(loading.LogisticsServiceProviderId.Value, out var logisticsServiceProvider))
                    {
                        var loadingExpenseModel = BuildLoadingExpenseEditModel(loading, returnUrl: null);
                        await SyncLoadingServiceExpensesAsync(loading, loadingExpenseModel, logisticsServiceProvider);
                    }

                    if (row.OperationalAssetId.HasValue
                        && operationalAssetsById.TryGetValue(row.OperationalAssetId.Value, out var operationalAsset))
                    {
                        var loadingExpenseModel = BuildLoadingExpenseEditModel(loading, returnUrl: null);
                        loadingExpenseModel.OperationalAssetId = operationalAsset.Id;
                        var ownershipShares = ownershipSharesByAssetId.TryGetValue(operationalAsset.Id, out var cachedShares)
                            ? cachedShares
                            : await GetActiveAssetOwnershipSharesAsync(operationalAsset.Id, loading.LoadingDate);
                        var contractType = createdRow.Contract.ContractType;
                        await SyncLoadingAssetRentAsync(loading, loadingExpenseModel, operationalAsset, ownershipShares, contractType);
                    }
                }

                var lossSubmissions = createdRows
                    .Where(createdRow => createdRow.Row.Loss.Enabled
                        && createdRow.Row.Loss.QuantityMt.GetValueOrDefault() > 0m)
                    .Select(createdRow =>
                    {
                        var lossSubmission = BuildLoadingLossSubmission(
                            model,
                            createdRow.Row,
                            createdRow.Loading.ContractId);
                        lossSubmission.LoadingRegisterId = createdRow.Loading.Id;
                        return lossSubmission;
                    })
                    .ToList();
                if (lossSubmissions.Count > 0)
                {
                    await _lossWorkflow.CreateBatchAsync(lossSubmissions);
                }

                var createdLoadings = createdRows.Select(row => row.Loading).ToList();
                var createdLosses = lossSubmissions.Count;

                if (transaction is not null)
                {
                    await transaction.CommitAsync();
                }

                var truckImportSuffix = autoCreatedTrucks.Count == 0
                    ? string.Empty
                    : $" {autoCreatedTrucks.Count} موتر جدید از فایل اکسل ساخته شد.";

                var lossSuffix = createdLosses == 0
                    ? string.Empty
                    : $" {createdLosses} مورد ضایعات مرحله‌ای نیز ثبت شد.";
                TempData["ok"] = createdLoadings.Count == 1
                    ? $"ثبت بارگیری با موفقیت انجام شد.{truckImportSuffix}{lossSuffix}"
                    : $"{createdLoadings.Count} سطر بارگیری با موفقیت ثبت شد.{truckImportSuffix}{lossSuffix}";

                if (TryGetLocalReturnUrl(model.ReturnUrl, out var localReturnUrl))
                {
                    return Redirect(localReturnUrl);
                }

                if (createdLoadings.Count == 1)
                {
                    return RedirectToAction(nameof(Details), new { id = createdLoadings[0].Id });
                }

                return RedirectToAction(nameof(Index));
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
            _logger.LogError(ex, "Failed to create loading register.");
            ModelState.AddModelError(string.Empty, "ثبت loading report انجام نشد. دوباره تلاش کنید.");
        }

        await PopulateLookupsAsync(model);
        PrepareRowsForFastPreview(model);
        return View(model);
    }

    public async Task<IActionResult> Details(int id, string? returnUrl = null)
    {
        var loading = await _db.LoadingRegisters
            .Include(l => l.Contract!.Supplier)
            .Include(l => l.Contract!.Customer)
            .Include(l => l.Product)
            .Include(l => l.OriginLocation)
            .Include(l => l.Vessel)
            .Include(l => l.Truck)
            .Include(l => l.LogisticsServiceProvider)
            .AsNoTracking()
            .FirstOrDefaultAsync(l => l.Id == id);

        if (loading is null)
        {
            return NotFound();
        }

        var receiptItems = await _db.LoadingReceipts
            .AsNoTracking()
            .Where(r => r.LoadingRegisterId == loading.Id)
            .OrderBy(r => r.ReceiptDate)
            .ThenBy(r => r.Id)
            .Select(r => new LoadingReceiptListItemViewModel
            {
                Id = r.Id,
                ReceiptDate = r.ReceiptDate,
                TerminalName = r.Terminal != null ? r.Terminal.Name : "",
                StorageTankCode = r.StorageTank == null
                    ? null
                    : r.StorageTank.DisplayName == null || r.StorageTank.DisplayName == ""
                        ? r.StorageTank.TankCode
                        : r.StorageTank.DisplayName,
                ReceivedQuantityMt = r.ReceivedQuantityMt,
                ReferenceDocument = r.ReferenceDocument,
                InventoryMovementId = r.InventoryMovement != null ? r.InventoryMovement.Id : 0,
                IsCancelled = r.IsCancelled,
                CancelledAtUtc = r.CancelledAtUtc,
                CancellationReason = r.CancellationReason,
                CancelledByUserName = r.CancelledByUserId == null
                    ? null
                    : _db.Users.Where(u => u.Id == r.CancelledByUserId).Select(u => u.Username).FirstOrDefault(),
                RowVersion = r.RowVersion
            })
            .ToListAsync();

        // رسیدهای لغوشده در فهرست دیده می‌شوند اما در هیچ جمعی شمرده نمی‌شوند.
        var totalReceivedQuantityMt = receiptItems.Where(r => !r.IsCancelled).Sum(r => r.ReceivedQuantityMt);
        var transportType = ResolveTransportType(loading.TransportType, loading.VesselId, loading.TruckId, loading.WagonNumber);

        var lossItems = await _db.LossEvents
            .AsNoTracking()
            .Where(l => l.LoadingRegisterId == loading.Id && !l.IsCancelled)
            .OrderBy(l => l.EventDate)
            .Select(l => new LossEventSummaryItem
            {
                Id = l.Id,
                Stage = l.Stage,
                EventDate = l.EventDate,
                DifferenceQuantityMt = l.DifferenceQuantityMt,
                ToleranceQuantityMt = l.ToleranceQuantityMt,
                AllowableLossMt = l.AllowableLossMt,
                ChargeableLossMt = l.ChargeableLossMt,
                ResponsiblePartyName = l.ResponsiblePartyName,
                Reference = l.Reference,
                Notes = l.Notes
            })
            .ToListAsync();

        var customsItems = await _db.CustomsDeclarations
            .AsNoTracking()
            .Where(cd => cd.LoadingRegisterId == loading.Id)
            .OrderBy(cd => cd.DeclarationDate)
            .Select(cd => new Models.Loading.LoadingCustomsSummaryItem
            {
                Id = cd.Id,
                DeclarationDate = cd.DeclarationDate,
                DeclarationReference = cd.DeclarationReference,
                WagonOrTruckNumber = cd.WagonOrTruckNumber,
                // compute totals from items (avoid stale stored totals)
                TotalAfn = cd.Items.Sum(i => i.AmountAfn),
                TotalUsd = cd.Items.Sum(i => i.AmountUsd ?? 0m)
            })
            .ToListAsync();

        // Compute AFN-equivalent for USD totals per declaration (uses pricing service with DB fallback)
        var fxCache = new Dictionary<DateTime, decimal>();
        foreach (var c in customsItems)
        {
            var date = c.DeclarationDate.Date;
            decimal rate = 0m;
            try
            {
                if (!fxCache.TryGetValue(date, out rate))
                {
                    var fx = await _pricing.GetFxRateAsync("USD", "AFN", date);
                    rate = fx.Value;
                    fxCache[date] = rate;
                }
            }
            catch
            {
                rate = await _db.DailyFxRates
                    .AsNoTracking()
                    .Where(r => r.BaseCurrency == "USD" && r.QuoteCurrency == "AFN" && r.RateDate <= date)
                    .OrderByDescending(r => r.RateDate)
                    .Select(r => r.Rate)
                    .FirstOrDefaultAsync();
                fxCache[date] = rate;
            }

            c.TotalAfnEquivalent = c.TotalUsd * rate;
        }

        var receiptShortageLossMt = lossItems
            .Where(l => l.Stage == LossEventStage.ReceiptShortage)
            .Sum(l => l.DifferenceQuantityMt > 0m ? l.DifferenceQuantityMt : Math.Max(l.ChargeableLossMt, 0m));
        var remainingToReceiveMt = Math.Max(loading.LoadedQuantityMt - totalReceivedQuantityMt - receiptShortageLossMt, 0m);
        var currentPageReturnUrl = HttpContext?.Request is { } request
            ? $"{request.Path}{request.QueryString}"
            : Url?.Action(nameof(Details), new { id = loading.Id }) ?? $"/Loading/Details/{loading.Id}";
        var receiptEditor = remainingToReceiveMt > 0m
            ? BuildLoadingReceiptCreateModel(loading, totalReceivedQuantityMt, remainingToReceiveMt, currentPageReturnUrl)
            : null;

        if (receiptEditor is not null)
        {
            await PopulateReceiptLookupsAsync(receiptEditor);
        }

        ViewBag.ReturnUrl = TryGetLocalReturnUrl(returnUrl, out var localReturnUrl) ? localReturnUrl : null;

        var model = new LoadingDetailsViewModel
        {
            Id = loading.Id,
            LoadingDate = loading.LoadingDate,
            TransportType = transportType,
            TransportTypeLabel = GetTransportTypeLabel(transportType),
            ContractId = loading.ContractId,
            ContractNumber = loading.Contract?.DisplayLabel ?? "",
            ProductName = loading.Product?.Name ?? "",
            OriginLocationName = loading.OriginLocation?.Name,
            VesselName = loading.Vessel?.Name,
            TruckPlateNumber = loading.Truck?.PlateNumber,
            VehicleSummary = BuildVehicleSummary(transportType, loading.Vessel?.Name, loading.Truck?.PlateNumber, loading.WagonNumber),
            LoadedQuantityMt = loading.LoadedQuantityMt,
            BillOfLadingNumber = loading.BillOfLadingNumber,
            WagonNumber = loading.WagonNumber,
            RouteDescription = loading.RouteDescription,
            LogisticsServiceProviderId = loading.LogisticsServiceProviderId,
            LogisticsCompanyName = loading.LogisticsServiceProvider?.Name ?? loading.LogisticsCompanyName,
            ConsigneeName = loading.ConsigneeName,
            DestinationName = loading.DestinationName,
            PlattsUsd = loading.PlattsUsd,
            LoadingPriceUsd = loading.LoadingPriceUsd,
            LoadingValueUsd = CalculateLoadingValueUsd(loading.LoadedQuantityMt, loading.LoadingPriceUsd),
            FreightRateUsdPerMt = loading.FreightRateUsdPerMt,
            TransportExpenseUsd = loading.TransportExpenseUsd,
            WarehouseExpenseUsd = loading.WarehouseExpenseUsd,
            OtherExpenseUsd = loading.OtherExpenseUsd,
            ChargeableQuantityMt = loading.ChargeableQuantityMt,
            RailwayRateUsd = loading.RailwayRateUsd,
            RailwayExpenseUsd = loading.RailwayExpenseUsd,
            SettlementCurrencyCode = loading.SettlementCurrencyCode,
            RubRateStatus = loading.RubRateStatus,
            RubPerUsdRate = loading.RubPerUsdRate,
            RubRateDate = loading.RubRateDate,
            RubRateSource = loading.RubRateSource,
            AmountUsdAtRubLock = loading.AmountUsdAtRubLock,
            AmountRubAtRubLock = loading.AmountRubAtRubLock,
            SettlementUnitPriceRub = loading.SettlementUnitPriceRub,
            SettlementValueRub = loading.SettlementValueRub,
            RubRateLockedAtUtc = loading.RubRateLockedAtUtc,
            RubRateLockedByUserName = loading.RubRateLockedByUserName,
            RubleRateEditor = BuildLoadingRubleRateEditModel(loading, currentPageReturnUrl),
            Notes = loading.Notes,
            TotalReceivedQuantityMt = totalReceivedQuantityMt,
            RemainingToReceiveMt = remainingToReceiveMt,
            ExpenseEditor = BuildLoadingExpenseEditModel(loading, returnUrl),
            ReceiptEditor = receiptEditor,
            ReceiptItems = receiptItems,
            LossItems = lossItems,
            CustomsItems = customsItems
        };

        await PopulateLoadingExpenseLinesAsync(model.ExpenseEditor, loading);
        await PopulateLoadingExpenseLookupsAsync(model.ExpenseEditor);
        model.LoadingExpenseTotalUsd = model.ExpenseEditor.Lines.Count > 0
            ? model.ExpenseEditor.Lines.Sum(line => line.AmountUsd)
            : (model.TransportExpenseUsd ?? 0m)
              + (model.WarehouseExpenseUsd ?? 0m)
              + (model.OtherExpenseUsd ?? 0m)
              + (model.RailwayExpenseUsd ?? 0m);
        model.CustomsTotalUsd = model.CustomsItems.Sum(item => item.TotalUsd);
        model.ChargeableLossTotalMt = model.LossItems.Sum(item => item.ChargeableLossMt);
        model.LoadingCostsGrandTotalUsd = model.LoadingExpenseTotalUsd + model.CustomsTotalUsd;
        return View(model);
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    public async Task<IActionResult> EditExpenses(int id, string? returnUrl = null, bool modal = false)
    {
        var loading = await _db.LoadingRegisters
            .Include(l => l.Contract)
            .Include(l => l.Product)
            .Include(l => l.Vessel)
            .Include(l => l.Truck)
            .AsNoTracking()
            .FirstOrDefaultAsync(l => l.Id == id);

        if (loading is null)
        {
            return NotFound();
        }

        var model = BuildLoadingExpenseEditModel(loading, returnUrl);
        await PopulateLoadingExpenseLinesAsync(model, loading);
        await PopulateLoadingExpenseLookupsAsync(model);

        if (modal)
        {
            ViewData["IsExpenseModal"] = true;
            ViewData["CancelUrl"] = returnUrl;
            return PartialView("_LoadingExpenseEditor", model);
        }

        return View(model);
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> EditExpenses(int id, LoadingExpenseEditViewModel model, bool modal = false)
    {
        if (id != model.Id)
        {
            return BadRequest();
        }

        var loading = await _db.LoadingRegisters
            .Include(l => l.Contract)
            .Include(l => l.Product)
            .Include(l => l.Vessel)
            .Include(l => l.Truck)
            .FirstOrDefaultAsync(l => l.Id == id);

        if (loading is null)
        {
            return NotFound();
        }

        ApplyLoadingExpenseEditContext(model, loading);
        model.Lines ??= [];

        // The modal flow is AJAX. Only return the bare partial fragment when the
        // request is actually an AJAX call; a native (non-AJAX) submit must always
        // get a full page or a redirect so the browser never lands on a stray fragment.
        var request = HttpContext?.Request;
        var isAjax = request is null
            || (request.Headers.TryGetValue("X-Requested-With", out var requestedWith)
                && string.Equals(requestedWith.ToString(), "XMLHttpRequest", StringComparison.OrdinalIgnoreCase));
        var useModalResponse = modal && isAjax;

        // 1) Per-row validation + amount normalization (no DB writes yet).
        var validLines = NormalizeLoadingExpenseLines(model, loading);

        if (!ModelState.IsValid)
        {
            await PopulateLoadingExpenseLookupsAsync(model);
            return useModalResponse ? ExpenseModalPartial(model) : View(model);
        }

        // 2) Resolve referenced master data and validate existence / active / ownership.
        var typesById = await ResolveLoadingExpenseTypesAsync(validLines);
        var providersById = await ResolveLoadingExpenseServiceProvidersAsync(validLines);
        var assetsById = await ResolveLoadingExpenseAssetsAsync(validLines);
        var ownershipByAsset = new Dictionary<int, List<AssetOwnershipShare>>();

        for (var i = 0; i < model.Lines.Count; i++)
        {
            var line = model.Lines[i];
            if (!validLines.Contains(line))
            {
                continue;
            }

            if (!typesById.ContainsKey(line.ExpenseTypeId))
            {
                ModelState.AddModelError($"Lines[{i}].ExpenseTypeId", "نوع مصرف انتخاب‌شده معتبر نیست.");
            }

            if (line.PartyType == LoadingExpensePartyType.ServiceProvider
                && line.ServiceProviderId.HasValue
                && !providersById.ContainsKey(line.ServiceProviderId.Value))
            {
                ModelState.AddModelError($"Lines[{i}].ServiceProviderId", "شرکت خدماتی انتخاب‌شده معتبر یا فعال نیست.");
            }

            if (line.PartyType == LoadingExpensePartyType.OperationalAsset && line.OperationalAssetId.HasValue)
            {
                if (!assetsById.ContainsKey(line.OperationalAssetId.Value))
                {
                    ModelState.AddModelError($"Lines[{i}].OperationalAssetId", "دارایی ملکی انتخاب‌شده معتبر یا فعال نیست.");
                }
                else if (!ownershipByAsset.ContainsKey(line.OperationalAssetId.Value))
                {
                    var shares = await GetActiveAssetOwnershipSharesAsync(line.OperationalAssetId.Value, loading.LoadingDate);
                    ownershipByAsset[line.OperationalAssetId.Value] = shares;
                    var shareTotal = shares.Sum(s => s.SharePercent);
                    if (shares.Count == 0
                        || Math.Abs(decimal.Round(shareTotal, 4, MidpointRounding.AwayFromZero) - 100m) > PercentTolerance)
                    {
                        ModelState.AddModelError($"Lines[{i}].OperationalAssetId", "برای کرایه داخلی دارایی، مجموع سهم مالکیت فعال در تاریخ بارگیری باید 100٪ باشد.");
                    }
                }
            }
        }

        if (!ModelState.IsValid)
        {
            await PopulateLoadingExpenseLookupsAsync(model);
            return useModalResponse ? ExpenseModalPartial(model) : View(model);
        }

        // 3) Persist + sync financial documents per row inside one transaction.
        IDbContextTransaction? transaction = null;
        try
        {
            transaction = await BeginTransactionIfSupportedAsync();

            await SyncLoadingExpenseLinesAsync(loading, validLines, typesById, providersById, assetsById, ownershipByAsset);

            if (transaction is not null)
            {
                await transaction.CommitAsync();
            }
        }
        catch
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync();
            }

            throw;
        }

        TempData["ok"] = "مصارف بارگیری ذخیره شد.";

        if (useModalResponse)
        {
            var redirectUrl = TryGetLocalReturnUrl(model.ReturnUrl, out var modalReturnUrl)
                ? modalReturnUrl
                : Url.Action(nameof(Details), new { id = loading.Id }) ?? $"/Loading/Details/{loading.Id}";

            return Json(new { success = true, redirectUrl });
        }

        if (TryGetLocalReturnUrl(model.ReturnUrl, out var localReturnUrl))
        {
            return Redirect(localReturnUrl);
        }

        return RedirectToAction(nameof(Details), new { id = loading.Id });
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    public async Task<IActionResult> Edit(int id, string? returnUrl = null)
    {
        var loading = await _db.LoadingRegisters
            .Include(l => l.Contract)
            .Include(l => l.Product)
            .Include(l => l.Vessel)
            .Include(l => l.Truck)
            .AsNoTracking()
            .FirstOrDefaultAsync(l => l.Id == id);

        if (loading is null)
        {
            return NotFound();
        }

        var totalReceivedMt = await _db.LoadingReceipts
            .AsNoTracking()
            .Where(r => r.LoadingRegisterId == loading.Id && !r.IsCancelled)
            .SumAsync(r => (decimal?)r.ReceivedQuantityMt) ?? 0m;

        var transportType = ResolveTransportType(loading.TransportType, loading.VesselId, loading.TruckId, loading.WagonNumber);
        var model = new LoadingEditViewModel
        {
            Id = loading.Id,
            LoadingDate = loading.LoadingDate,
            ContractNumber = loading.Contract?.DisplayLabel ?? "",
            ProductName = loading.Product?.Name ?? "",
            VehicleSummary = BuildVehicleSummary(transportType, loading.Vessel?.Name, loading.Truck?.PlateNumber, loading.WagonNumber),
            LoadedQuantityMt = loading.LoadedQuantityMt,
            TotalReceivedQuantityMt = totalReceivedMt,
            LoadingPriceUsd = loading.LoadingPriceUsd,
            BillOfLadingNumber = loading.BillOfLadingNumber,
            RwbNo = loading.RwbNo,
            WagonNumber = loading.WagonNumber,
            RouteDescription = loading.RouteDescription,
            ConsigneeName = loading.ConsigneeName,
            DestinationName = loading.DestinationName,
            LogisticsCompanyName = loading.LogisticsServiceProvider?.Name ?? loading.LogisticsCompanyName,
            Notes = loading.Notes,
            ReturnUrl = returnUrl
        };

        return View(model);
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, LoadingEditViewModel model)
    {
        if (id != model.Id)
        {
            return BadRequest();
        }

        var loading = await _db.LoadingRegisters
            .Include(l => l.Contract)
            .Include(l => l.Product)
            .Include(l => l.Vessel)
            .Include(l => l.Truck)
            .FirstOrDefaultAsync(l => l.Id == id);

        if (loading is null)
        {
            return NotFound();
        }

        var transportType = ResolveTransportType(loading.TransportType, loading.VesselId, loading.TruckId, loading.WagonNumber);
        model.ContractNumber = loading.Contract?.DisplayLabel ?? "";
        model.ProductName = loading.Product?.Name ?? "";
        model.VehicleSummary = BuildVehicleSummary(transportType, loading.Vessel?.Name, loading.Truck?.PlateNumber, loading.WagonNumber);
        model.BillOfLadingNumber = NormalizeNullable(model.BillOfLadingNumber);
        model.RwbNo = NormalizeNullable(model.RwbNo);
        model.WagonNumber = NormalizeNullable(model.WagonNumber);
        model.RouteDescription = NormalizeNullable(model.RouteDescription);
        model.ConsigneeName = NormalizeNullable(model.ConsigneeName);
        model.DestinationName = NormalizeNullable(model.DestinationName);
        model.LogisticsCompanyName = NormalizeNullable(model.LogisticsCompanyName);
        model.Notes = NormalizeNullable(model.Notes);
        model.LoadingPriceUsd = NormalizePositiveDecimal(model.LoadingPriceUsd);

        // اصلاح وزن: نباید کمتر از مقدار رسیدشده باشد و نباید مجموع بارگیری قرارداد را از مقدار کل بیشتر کند.
        var totalReceivedMt = await _db.LoadingReceipts
            .AsNoTracking()
            .Where(r => r.LoadingRegisterId == loading.Id && !r.IsCancelled)
            .SumAsync(r => (decimal?)r.ReceivedQuantityMt) ?? 0m;
        model.TotalReceivedQuantityMt = totalReceivedMt;

        if (model.LoadedQuantityMt < totalReceivedMt)
        {
            ModelState.AddModelError(
                nameof(model.LoadedQuantityMt),
                $"وزن بارگیری نمی‌تواند کمتر از مقدار رسیدشده ({totalReceivedMt:N3} MT) باشد.");
        }

        if (loading.Contract is not null)
        {
            var committedOtherMt = await _db.LoadingRegisters
                .AsNoTracking()
                .Where(l => l.ContractId == loading.ContractId && l.Id != loading.Id)
                .SumAsync(l => (decimal?)l.LoadedQuantityMt) ?? 0m;
            var maxForThisLoadingMt = loading.Contract.QuantityMt - committedOtherMt;

            if (model.LoadedQuantityMt > maxForThisLoadingMt)
            {
                ModelState.AddModelError(
                    nameof(model.LoadedQuantityMt),
                    $"مجموع بارگیری از مقدار کل قرارداد بیشتر می‌شود. حداکثر مجاز برای این سطر: {maxForThisLoadingMt:N3} MT.");
            }
        }

        if (!ModelState.IsValid)
        {
            return View(model);
        }

        var diff = AuditDiffFormatter.ForUpdate(
            ("LoadingDate", loading.LoadingDate, model.LoadingDate),
            ("LoadedQuantityMt", loading.LoadedQuantityMt, model.LoadedQuantityMt),
            ("LoadingPriceUsd", loading.LoadingPriceUsd, model.LoadingPriceUsd),
            ("BillOfLadingNumber", loading.BillOfLadingNumber, model.BillOfLadingNumber),
            ("RwbNo", loading.RwbNo, model.RwbNo),
            ("WagonNumber", loading.WagonNumber, model.WagonNumber),
            ("RouteDescription", loading.RouteDescription, model.RouteDescription),
            ("ConsigneeName", loading.ConsigneeName, model.ConsigneeName),
            ("DestinationName", loading.DestinationName, model.DestinationName),
            ("LogisticsCompanyName", loading.LogisticsCompanyName, model.LogisticsCompanyName),
            ("Notes", loading.Notes, model.Notes));

        loading.LoadingDate = model.LoadingDate;
        loading.LoadedQuantityMt = model.LoadedQuantityMt;
        loading.LoadingPriceUsd = model.LoadingPriceUsd;
        loading.BillOfLadingNumber = model.BillOfLadingNumber;
        loading.RwbNo = model.RwbNo;
        loading.WagonNumber = model.WagonNumber;
        loading.RouteDescription = model.RouteDescription;
        loading.ConsigneeName = model.ConsigneeName;
        loading.DestinationName = model.DestinationName;
        if (loading.LogisticsServiceProviderId is null)
        {
            loading.LogisticsCompanyName = model.LogisticsCompanyName;
        }
        loading.Notes = model.Notes;

        await _audit.LogAndSaveAsync(nameof(LoadingRegister), loading.Id, AuditAction.Update, diff: diff);

        // مقدار یا قیمت عوض شده؛ سطر دفترِ بدهی تأمین‌کننده با همان snapshot جدید هماهنگ می‌شود.
        await PostSupplierLoadingLedgerIfReadyAsync(loading, loading.Contract);

        TempData["ok"] = "اطلاعات بارگیری با موفقیت ویرایش شد.";

        if (TryGetLocalReturnUrl(model.ReturnUrl, out var localReturnUrl))
        {
            return Redirect(localReturnUrl);
        }

        return RedirectToAction(nameof(Details), new { id = loading.Id });
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    public async Task<IActionResult> EditPrice(int id, string? returnUrl = null)
    {
        var loading = await _db.LoadingRegisters
            .Include(l => l.Contract)
            .Include(l => l.Product)
            .Include(l => l.Vessel)
            .Include(l => l.Truck)
            .AsNoTracking()
            .FirstOrDefaultAsync(l => l.Id == id);

        if (loading is null)
        {
            return NotFound();
        }

        return View(BuildLoadingPriceEditModel(loading, returnUrl));
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> EditPrice(int id, LoadingPriceEditViewModel model)
    {
        if (id != model.Id)
        {
            return BadRequest();
        }

        var loading = await _db.LoadingRegisters
            .Include(l => l.Contract)
            .Include(l => l.Product)
            .Include(l => l.Vessel)
            .Include(l => l.Truck)
            .FirstOrDefaultAsync(l => l.Id == id);

        if (loading is null)
        {
            return NotFound();
        }

        ApplyLoadingPriceEditContext(model, loading);

        if (!ModelState.IsValid)
        {
            return View(model);
        }

        model.LoadingPriceUsd = NormalizePositiveDecimal(model.LoadingPriceUsd);
        model.PricingNote = NormalizeNullable(model.PricingNote);

        var diff = AuditDiffFormatter.ForUpdate(
            ("LoadingPriceUsd", loading.LoadingPriceUsd, model.LoadingPriceUsd),
            ("Notes", loading.Notes, model.PricingNote));

        loading.LoadingPriceUsd = model.LoadingPriceUsd;
        loading.Notes = model.PricingNote;

        await _audit.LogAndSaveAsync(nameof(LoadingRegister), loading.Id, AuditAction.Update, diff: diff);

        // قیمت قطعی شد یا عوض شد؛ سطر دفترِ بدهی تأمین‌کننده ساخته/هماهنگ می‌شود.
        await PostSupplierLoadingLedgerIfReadyAsync(loading, loading.Contract);

        TempData["ok"] = loading.LoadingPriceUsd.HasValue
            ? "قیمت بارگیری ذخیره شد."
            : "قیمت بارگیری به وضعیت در انتظار نرخ برگشت.";

        if (TryGetLocalReturnUrl(model.ReturnUrl, out var localReturnUrl))
        {
            return Redirect(localReturnUrl);
        }

        return RedirectToAction(nameof(Details), new { id = loading.Id });
    }

    private static LoadingRubleRateEditViewModel BuildLoadingRubleRateEditModel(LoadingRegister loading, string? returnUrl)
    {
        var loadingValueUsd = CalculateLoadingValueUsd(loading.LoadedQuantityMt, loading.LoadingPriceUsd);

        return new LoadingRubleRateEditViewModel
        {
            Id = loading.Id,
            ContractNumber = loading.Contract?.DisplayLabel ?? "",
            LoadedQuantityMt = loading.LoadedQuantityMt,
            LoadingPriceUsd = loading.LoadingPriceUsd,
            LoadingValueUsd = loadingValueUsd,
            SettlementCurrencyCode = loading.SettlementCurrencyCode,
            RubRateStatus = loading.RubRateStatus,
            RubPerUsdRate = loading.RubPerUsdRate,
            RubRateDate = loading.RubRateDate ?? loading.LoadingDate,
            RubRateSource = loading.RubRateSource,
            ReturnUrl = returnUrl
        };
    }

    private static void ApplyLoadingRubleRateEditContext(
        LoadingRubleRateEditViewModel model,
        LoadingRegister loading)
    {
        model.Id = loading.Id;
        model.ContractNumber = loading.Contract?.DisplayLabel ?? "";
        model.LoadedQuantityMt = loading.LoadedQuantityMt;
        model.LoadingPriceUsd = loading.LoadingPriceUsd;
        model.LoadingValueUsd = CalculateLoadingValueUsd(loading.LoadedQuantityMt, loading.LoadingPriceUsd);
        model.SettlementCurrencyCode = loading.SettlementCurrencyCode;
        model.RubRateStatus = loading.RubRateStatus;
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SetRubleRate(int id, LoadingRubleRateEditViewModel model)
    {
        if (id != model.Id)
        {
            return BadRequest();
        }

        var loading = await _db.LoadingRegisters
            .Include(l => l.Contract)
            .Include(l => l.Product)
            .FirstOrDefaultAsync(l => l.Id == id);

        if (loading is null)
        {
            return NotFound();
        }

        ApplyLoadingRubleRateEditContext(model, loading);

        if (!IsRubSettlement(loading.SettlementCurrencyCode))
        {
            ModelState.AddModelError(nameof(model.SettlementCurrencyCode), "این بارگیری تسویه روبلی ندارد.");
        }

        if (loading.RubRateStatus == RubSettlementRateStatus.Locked)
        {
            ModelState.AddModelError(nameof(model.RubRateStatus), "نرخ روبل این بارگیری قبلاً قفل شده است.");
        }

        model.RubPerUsdRate = NormalizePositiveDecimal(model.RubPerUsdRate);
        model.RubRateDate = model.RubRateDate.HasValue ? ToUtcDate(model.RubRateDate.Value) : ToUtcDate(loading.LoadingDate);
        model.RubRateSource = NormalizeNullable(model.RubRateSource) ?? "Manual";

        if (!model.RubPerUsdRate.HasValue)
        {
            ModelState.AddModelError(nameof(model.RubPerUsdRate), "نرخ RUB برای 1 USD را وارد کنید.");
        }

        var loadingValueUsd = CalculateLoadingValueUsd(loading.LoadedQuantityMt, loading.LoadingPriceUsd);
        if (!loadingValueUsd.HasValue)
        {
            ModelState.AddModelError(nameof(model.LoadingValueUsd), "اول قیمت USD این بارگیری را ثبت کنید، بعد نرخ روبل را قفل کنید.");
        }

        if (!ModelState.IsValid)
        {
            TempData["err"] = string.Join(" ", ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage));
            return RedirectToAction(nameof(Details), new { id = loading.Id });
        }

        var amountRub = CalculateRubAmount(loadingValueUsd!.Value, model.RubPerUsdRate!.Value);
        var diff = AuditDiffFormatter.ForUpdate(
            ("RubRateStatus", loading.RubRateStatus, RubSettlementRateStatus.Locked),
            ("RubPerUsdRate", loading.RubPerUsdRate, model.RubPerUsdRate),
            ("RubRateDate", loading.RubRateDate, model.RubRateDate),
            ("RubRateSource", loading.RubRateSource, model.RubRateSource),
            ("AmountUsdAtRubLock", loading.AmountUsdAtRubLock, loadingValueUsd.Value),
            ("AmountRubAtRubLock", loading.AmountRubAtRubLock, amountRub));

        loading.RubRateStatus = RubSettlementRateStatus.Locked;
        loading.RubPerUsdRate = model.RubPerUsdRate.Value;
        loading.RubRateDate = model.RubRateDate;
        loading.RubRateSource = model.RubRateSource;
        loading.AmountUsdAtRubLock = loadingValueUsd.Value;
        loading.AmountRubAtRubLock = amountRub;
        loading.RubRateLockedAtUtc = DateTime.UtcNow;
        loading.RubRateLockedByUserName = User?.Identity?.Name;

        await _audit.LogAndSaveAsync(nameof(LoadingRegister), loading.Id, AuditAction.Update, diff: diff);
        await PostSupplierLoadingLedgerIfReadyAsync(loading, loading.Contract);
        TempData["ok"] = "نرخ روبل بارگیری قفل شد.";

        if (TryGetLocalReturnUrl(model.ReturnUrl, out var localReturnUrl))
        {
            return Redirect(localReturnUrl);
        }

        return RedirectToAction(nameof(Details), new { id = loading.Id });
    }

    private static LoadingExpenseEditViewModel BuildLoadingExpenseEditModel(LoadingRegister loading, string? returnUrl)
    {
        var transportType = ResolveTransportType(loading.TransportType, loading.VesselId, loading.TruckId, loading.WagonNumber);

        return new LoadingExpenseEditViewModel
        {
            Id = loading.Id,
            LoadingDate = loading.LoadingDate,
            TransportType = transportType,
            TransportTypeLabel = GetTransportTypeLabel(transportType),
            ContractNumber = loading.Contract?.DisplayLabel ?? "",
            ProductName = loading.Product?.Name ?? "",
            VehicleSummary = BuildVehicleSummary(transportType, loading.Vessel?.Name, loading.Truck?.PlateNumber, loading.WagonNumber),
            LoadedQuantityMt = loading.LoadedQuantityMt,
            BillOfLadingNumber = loading.BillOfLadingNumber,
            WagonNumber = loading.WagonNumber,
            ServiceProviderId = loading.LogisticsServiceProviderId,
            TransportExpenseUsd = loading.TransportExpenseUsd,
            TransportRateUsd = null,
            WarehouseExpenseUsd = loading.WarehouseExpenseUsd,
            OtherExpenseUsd = loading.OtherExpenseUsd,
            ChargeableQuantityMt = loading.ChargeableQuantityMt ?? loading.LoadedQuantityMt,
            RailwayRateUsd = loading.RailwayRateUsd,
            RailwayExpenseUsd = loading.RailwayExpenseUsd,
            RwbNo = loading.RwbNo,
            DestinationName = loading.DestinationName,
            RouteDescription = loading.RouteDescription,
            ReturnUrl = returnUrl
        };
    }

    private static LoadingPriceEditViewModel BuildLoadingPriceEditModel(LoadingRegister loading, string? returnUrl)
    {
        var transportType = ResolveTransportType(loading.TransportType, loading.VesselId, loading.TruckId, loading.WagonNumber);

        return new LoadingPriceEditViewModel
        {
            Id = loading.Id,
            LoadingDate = loading.LoadingDate,
            ContractNumber = loading.Contract?.DisplayLabel ?? "",
            ProductName = loading.Product?.Name ?? "",
            VehicleSummary = BuildVehicleSummary(transportType, loading.Vessel?.Name, loading.Truck?.PlateNumber, loading.WagonNumber),
            LoadedQuantityMt = loading.LoadedQuantityMt,
            BillOfLadingNumber = loading.BillOfLadingNumber,
            WagonNumber = loading.WagonNumber,
            LoadingPriceUsd = loading.LoadingPriceUsd,
            PricingNote = loading.Notes,
            ReturnUrl = returnUrl
        };
    }

    private static void ApplyLoadingPriceEditContext(LoadingPriceEditViewModel model, LoadingRegister loading)
    {
        var transportType = ResolveTransportType(loading.TransportType, loading.VesselId, loading.TruckId, loading.WagonNumber);

        model.LoadingDate = loading.LoadingDate;
        model.ContractNumber = loading.Contract?.DisplayLabel ?? "";
        model.ProductName = loading.Product?.Name ?? "";
        model.VehicleSummary = BuildVehicleSummary(transportType, loading.Vessel?.Name, loading.Truck?.PlateNumber, loading.WagonNumber);
        model.LoadedQuantityMt = loading.LoadedQuantityMt;
        model.BillOfLadingNumber = loading.BillOfLadingNumber;
        model.WagonNumber = loading.WagonNumber;
    }

    private static void ApplyLoadingExpenseEditContext(LoadingExpenseEditViewModel model, LoadingRegister loading)
    {
        var transportType = ResolveTransportType(loading.TransportType, loading.VesselId, loading.TruckId, loading.WagonNumber);

        model.LoadingDate = loading.LoadingDate;
        model.TransportType = transportType;
        model.TransportTypeLabel = GetTransportTypeLabel(transportType);
        model.ContractNumber = loading.Contract?.DisplayLabel ?? "";
        model.ProductName = loading.Product?.Name ?? "";
        model.VehicleSummary = BuildVehicleSummary(transportType, loading.Vessel?.Name, loading.Truck?.PlateNumber, loading.WagonNumber);
        model.LoadedQuantityMt = loading.LoadedQuantityMt;
        model.BillOfLadingNumber = loading.BillOfLadingNumber;
        model.WagonNumber = loading.WagonNumber;
    }

    private static void NormalizeLoadingExpenseEditModel(LoadingExpenseEditViewModel model, decimal loadedQuantityMt)
    {
        model.TransportExpenseUsd = NormalizePositiveDecimal(model.TransportExpenseUsd);
        model.TransportRateUsd = NormalizePositiveDecimal(model.TransportRateUsd);
        model.WarehouseExpenseUsd = NormalizePositiveDecimal(model.WarehouseExpenseUsd);
        model.OtherExpenseUsd = NormalizePositiveDecimal(model.OtherExpenseUsd);
        model.ChargeableQuantityMt = NormalizePositiveDecimal(model.ChargeableQuantityMt);
        model.RailwayRateUsd = NormalizePositiveDecimal(model.RailwayRateUsd);
        model.RailwayExpenseUsd = NormalizePositiveDecimal(model.RailwayExpenseUsd);

        if (model.RailwayRateUsd.HasValue)
        {
            var chargeableQuantityMt = model.ChargeableQuantityMt.GetValueOrDefault(loadedQuantityMt);
            if (chargeableQuantityMt <= 0m)
            {
                chargeableQuantityMt = loadedQuantityMt;
            }

            model.ChargeableQuantityMt = chargeableQuantityMt;
            model.RailwayExpenseUsd = decimal.Round(chargeableQuantityMt * model.RailwayRateUsd.Value, 4, MidpointRounding.AwayFromZero);
        }
        else if (!model.RailwayExpenseUsd.HasValue)
        {
            model.ChargeableQuantityMt = null;
        }

        if (model.TransportRateUsd.HasValue && loadedQuantityMt > 0m)
        {
            model.TransportExpenseUsd = decimal.Round(loadedQuantityMt * model.TransportRateUsd.Value, 4, MidpointRounding.AwayFromZero);
        }
    }

    private static decimal? NormalizePositiveDecimal(decimal? value)
        => value.HasValue && value.Value > 0m ? value.Value : null;

    private static int? NormalizePositiveInt(int? value)
        => value.HasValue && value.Value > 0 ? value.Value : null;

    private static DateTime ToUtcDate(DateTime value)
        => DateTime.SpecifyKind(value.Date, DateTimeKind.Utc);

    private static decimal CalculateLoadingExpenseTotalUsd(LoadingExpenseEditViewModel model)
        => (model.TransportExpenseUsd ?? 0m)
            + (model.WarehouseExpenseUsd ?? 0m)
            + (model.OtherExpenseUsd ?? 0m)
            + (model.RailwayExpenseUsd ?? 0m);

    private async Task PopulateLoadingExpenseLookupsAsync(LoadingExpenseEditViewModel model)
    {
        if (!model.ServiceProviderId.HasValue)
        {
            model.ServiceProviderId = await _db.ExpenseTransactions
                .AsNoTracking()
                .Where(e => e.LoadingRegisterId == model.Id
                    && e.ServiceProviderId.HasValue
                    && !e.IsCancelled)
                .OrderByDescending(e => e.Id)
                .Select(e => e.ServiceProviderId)
                .FirstOrDefaultAsync();
        }

        if (!model.OperationalAssetId.HasValue)
        {
            model.OperationalAssetId = await _db.AssetRentTransactions
                .AsNoTracking()
                .Where(r => r.LoadingRegisterId == model.Id
                    && r.UsageType == AssetRentUsageType.InternalCompanyUse
                    && !r.IsCancelled)
                .OrderByDescending(r => r.Id)
                .Select(r => (int?)r.OperationalAssetId)
                .FirstOrDefaultAsync();
        }

        var selectedId = model.ServiceProviderId;
        var providers = await _db.ServiceProviders
            .AsNoTracking()
            .Where(p => p.IsActive || (selectedId.HasValue && p.Id == selectedId.Value))
            .OrderBy(p => selectedId.HasValue && p.Id == selectedId.Value ? 0 : 1)
            .ThenBy(p => p.Name)
            .Take(LookupLimit)
            .Select(p => new SelectListItem
            {
                Value = p.Id.ToString(),
                Text = string.IsNullOrWhiteSpace(p.Code) ? p.Name : p.Code + " - " + p.Name,
                Selected = selectedId.HasValue && p.Id == selectedId.Value
            })
            .ToListAsync();

        ViewBag.LoadingExpenseServiceProviders = providers;

        var selectedAssetId = model.OperationalAssetId;
        ViewBag.LoadingExpenseOperationalAssets = await _db.OperationalAssets
            .AsNoTracking()
            .Where(a => a.IsActive || (selectedAssetId.HasValue && a.Id == selectedAssetId.Value))
            .OrderBy(a => selectedAssetId.HasValue && a.Id == selectedAssetId.Value ? 0 : 1)
            .ThenBy(a => a.AssetCode)
            .Take(LookupLimit)
            .Select(a => new SelectListItem
            {
                Value = a.Id.ToString(),
                Text = string.IsNullOrWhiteSpace(a.AssetCode) ? a.Name : a.AssetCode + " - " + a.Name,
                Selected = selectedAssetId.HasValue && a.Id == selectedAssetId.Value
            })
            .ToListAsync();

        await EnsureBaseLoadingExpenseTypesAsync();
        var expenseTypeLookups = await GetCachedLookupAsync(
            "loading:expense-types:v1",
            () => _db.ExpenseTypes
                .AsNoTracking()
                .Where(t => t.IsActive)
                .OrderBy(t => t.NamePersian ?? t.Name)
                .Take(LookupLimit)
                .Select(t => new LookupOption(t.Id, t.NamePersian ?? t.Name))
                .ToListAsync());
        ViewBag.LoadingExpenseTypes = expenseTypeLookups
            .Select(t => new SelectListItem
            {
                Value = t.Id.ToString(),
                Text = t.Name
            })
            .ToList();
    }

    // ---------------------------------------------------------------------
    // Row-based loading expenses (modal). Each row maps to a LoadingExpenseLine.
    // ---------------------------------------------------------------------

    private enum LoadingExpenseBucket { Transport, Warehouse, Railway, Other }

    private static readonly (string Code, string Name, string NamePersian, string Category)[] BaseLoadingExpenseTypeDefs =
    [
        (LoadingTransportExpenseCode, "Loading Transport Freight", "کرایه حمل بارگیری", "Transport"),
        (LoadingStorageExpenseCode, "Loading Storage Rent", "کرایه مخزن بارگیری", "Storage"),
        (LoadingWagonRentExpenseCode, "Loading Wagon Rent", "کرایه واگون بارگیری", "Transport"),
        (LoadingOtherExpenseCode, "Loading Other Service Expense", "سایر مصارف بارگیری", "Other")
    ];

    private async Task EnsureBaseLoadingExpenseTypesAsync()
    {
        var codes = BaseLoadingExpenseTypeDefs.Select(d => d.Code).ToList();
        var existing = await _db.ExpenseTypes
            .Where(t => codes.Contains(t.Code))
            .Select(t => t.Code)
            .ToListAsync();

        var missing = BaseLoadingExpenseTypeDefs
            .Where(d => !existing.Contains(d.Code))
            .ToList();

        if (missing.Count == 0)
        {
            return;
        }

        foreach (var def in missing)
        {
            _db.ExpenseTypes.Add(new ExpenseType
            {
                Code = def.Code,
                Name = def.Name,
                NamePersian = def.NamePersian,
                Category = def.Category,
                IsActive = true
            });
        }

        await _db.SaveChangesAsync();
    }

    private async Task PopulateLoadingExpenseLinesAsync(LoadingExpenseEditViewModel model, LoadingRegister loading)
    {
        var existing = await _db.LoadingExpenseLines
            .AsNoTracking()
            .Where(l => l.LoadingRegisterId == loading.Id)
            .OrderBy(l => l.Id)
            .ToListAsync();

        if (existing.Count > 0)
        {
            model.Lines = existing.Select(MapLoadingExpenseLineToInput).ToList();
            return;
        }

        model.Lines = await SeedLoadingExpenseLinesAsync(loading);
    }

    private static LoadingExpenseLineInputModel MapLoadingExpenseLineToInput(LoadingExpenseLine line)
        => new()
        {
            Id = line.Id,
            ExpenseTypeId = line.ExpenseTypeId,
            CalculationMode = line.CalculationMode,
            QuantityMt = line.QuantityMt,
            UnitRateUsd = line.UnitRateUsd,
            AmountUsd = line.AmountUsd,
            PartyType = line.PartyType,
            ServiceProviderId = line.ServiceProviderId,
            OperationalAssetId = line.OperationalAssetId,
            Notes = line.Notes,
            ExpenseTransactionId = line.ExpenseTransactionId,
            AssetRentTransactionId = line.AssetRentTransactionId
        };

    private async Task<List<LoadingExpenseLineInputModel>> SeedLoadingExpenseLinesAsync(LoadingRegister loading)
    {
        await EnsureBaseLoadingExpenseTypesAsync();
        var codes = BaseLoadingExpenseTypeDefs.Select(d => d.Code).ToList();
        var typeIdByCode = await _db.ExpenseTypes
            .Where(t => codes.Contains(t.Code))
            .ToDictionaryAsync(t => t.Code, t => t.Id);

        var seeds = new List<LoadingExpenseLineInputModel>();

        var expenses = await _db.ExpenseTransactions
            .AsNoTracking()
            .Where(e => e.LoadingRegisterId == loading.Id && !e.IsCancelled)
            .OrderBy(e => e.Id)
            .ToListAsync();

        foreach (var expense in expenses)
        {
            seeds.Add(new LoadingExpenseLineInputModel
            {
                ExpenseTypeId = expense.ExpenseTypeId,
                CalculationMode = LoadingExpenseCalculationMode.FixedAmount,
                AmountUsd = expense.AmountUsd,
                PartyType = LoadingExpensePartyType.ServiceProvider,
                ServiceProviderId = expense.ServiceProviderId,
                ExpenseTransactionId = expense.Id
            });
        }

        var rents = await _db.AssetRentTransactions
            .AsNoTracking()
            .Where(r => r.LoadingRegisterId == loading.Id
                && !r.IsCancelled
                && r.UsageType == AssetRentUsageType.InternalCompanyUse)
            .OrderBy(r => r.Id)
            .ToListAsync();

        foreach (var rent in rents)
        {
            seeds.Add(new LoadingExpenseLineInputModel
            {
                ExpenseTypeId = typeIdByCode.GetValueOrDefault(LoadingTransportExpenseCode),
                CalculationMode = LoadingExpenseCalculationMode.FixedAmount,
                AmountUsd = rent.AmountUsd,
                PartyType = LoadingExpensePartyType.OperationalAsset,
                OperationalAssetId = rent.OperationalAssetId,
                AssetRentTransactionId = rent.Id
            });
        }

        if (expenses.Count == 0 && rents.Count == 0)
        {
            AddSeedNoneLine(seeds, typeIdByCode, LoadingTransportExpenseCode, loading.TransportExpenseUsd);
            AddSeedNoneLine(seeds, typeIdByCode, LoadingStorageExpenseCode, loading.WarehouseExpenseUsd);
            AddSeedNoneLine(seeds, typeIdByCode, LoadingWagonRentExpenseCode, loading.RailwayExpenseUsd);
            AddSeedNoneLine(seeds, typeIdByCode, LoadingOtherExpenseCode, loading.OtherExpenseUsd);
        }

        return seeds;
    }

    private static void AddSeedNoneLine(
        List<LoadingExpenseLineInputModel> seeds,
        IReadOnlyDictionary<string, int> typeIdByCode,
        string code,
        decimal? amountUsd)
    {
        if (!amountUsd.HasValue || amountUsd.Value <= 0m || !typeIdByCode.TryGetValue(code, out var typeId))
        {
            return;
        }

        seeds.Add(new LoadingExpenseLineInputModel
        {
            ExpenseTypeId = typeId,
            CalculationMode = LoadingExpenseCalculationMode.FixedAmount,
            AmountUsd = amountUsd.Value,
            PartyType = LoadingExpensePartyType.None
        });
    }

    private PartialViewResult ExpenseModalPartial(LoadingExpenseEditViewModel model)
    {
        ViewData["IsExpenseModal"] = true;
        ViewData["CancelUrl"] = model.ReturnUrl;
        return PartialView("_LoadingExpenseEditor", model);
    }

    /// <summary>
    /// Validates each submitted expense row, drops empty rows and computes the
    /// per-row amount. Adds ModelState errors keyed by row index. Returns the
    /// rows that should be persisted.
    /// </summary>
    private List<LoadingExpenseLineInputModel> NormalizeLoadingExpenseLines(
        LoadingExpenseEditViewModel model,
        LoadingRegister loading)
    {
        var valid = new List<LoadingExpenseLineInputModel>();

        for (var i = 0; i < model.Lines.Count; i++)
        {
            var line = model.Lines[i];
            line.ServiceProviderId = NormalizePositiveInt(line.ServiceProviderId);
            line.OperationalAssetId = NormalizePositiveInt(line.OperationalAssetId);
            line.QuantityMt = NormalizePositiveDecimal(line.QuantityMt);
            line.UnitRateUsd = NormalizePositiveDecimal(line.UnitRateUsd);
            line.Notes = NormalizeNullable(line.Notes);

            var isEmptyRow = line.ExpenseTypeId <= 0
                && line.AmountUsd <= 0m
                && !line.QuantityMt.HasValue
                && !line.UnitRateUsd.HasValue
                && string.IsNullOrWhiteSpace(line.Notes);
            if (isEmptyRow)
            {
                continue;
            }

            if (line.CalculationMode == LoadingExpenseCalculationMode.PerMetricTon)
            {
                if (!line.QuantityMt.HasValue || !line.UnitRateUsd.HasValue)
                {
                    ModelState.AddModelError($"Lines[{i}].UnitRateUsd", "برای روش «بر اساس تن»، مقدار و نرخ فی تن لازم است.");
                }
                else
                {
                    line.AmountUsd = decimal.Round(line.QuantityMt.Value * line.UnitRateUsd.Value, 4, MidpointRounding.AwayFromZero);
                }
            }

            if (line.ExpenseTypeId <= 0)
            {
                ModelState.AddModelError($"Lines[{i}].ExpenseTypeId", "نوع مصرف را انتخاب کنید.");
            }

            if (line.AmountUsd <= 0m)
            {
                ModelState.AddModelError($"Lines[{i}].AmountUsd", "مبلغ کل باید بزرگ‌تر از صفر باشد.");
            }

            switch (line.PartyType)
            {
                case LoadingExpensePartyType.None:
                    line.ServiceProviderId = null;
                    line.OperationalAssetId = null;
                    break;
                case LoadingExpensePartyType.ServiceProvider:
                    line.OperationalAssetId = null;
                    if (!line.ServiceProviderId.HasValue)
                    {
                        ModelState.AddModelError($"Lines[{i}].ServiceProviderId", "شرکت خدماتی را انتخاب کنید.");
                    }
                    break;
                case LoadingExpensePartyType.OperationalAsset:
                    line.ServiceProviderId = null;
                    if (!line.OperationalAssetId.HasValue)
                    {
                        ModelState.AddModelError($"Lines[{i}].OperationalAssetId", "دارایی ملکی را انتخاب کنید.");
                    }
                    break;
            }

            valid.Add(line);
        }

        return valid;
    }

    private async Task<Dictionary<int, ExpenseType>> ResolveLoadingExpenseTypesAsync(
        IReadOnlyCollection<LoadingExpenseLineInputModel> lines)
    {
        var ids = lines.Select(l => l.ExpenseTypeId).Where(x => x > 0).Distinct().ToList();
        if (ids.Count == 0)
        {
            return [];
        }

        return await _db.ExpenseTypes
            .Where(t => ids.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id);
    }

    private async Task<Dictionary<int, ServiceProviderEntity>> ResolveLoadingExpenseServiceProvidersAsync(
        IReadOnlyCollection<LoadingExpenseLineInputModel> lines)
    {
        var ids = lines
            .Where(l => l.PartyType == LoadingExpensePartyType.ServiceProvider && l.ServiceProviderId.HasValue)
            .Select(l => l.ServiceProviderId!.Value)
            .Distinct()
            .ToList();
        if (ids.Count == 0)
        {
            return [];
        }

        return await _db.ServiceProviders
            .Where(p => ids.Contains(p.Id) && p.IsActive)
            .ToDictionaryAsync(p => p.Id);
    }

    private async Task<Dictionary<int, OperationalAsset>> ResolveLoadingExpenseAssetsAsync(
        IReadOnlyCollection<LoadingExpenseLineInputModel> lines)
    {
        var ids = lines
            .Where(l => l.PartyType == LoadingExpensePartyType.OperationalAsset && l.OperationalAssetId.HasValue)
            .Select(l => l.OperationalAssetId!.Value)
            .Distinct()
            .ToList();
        if (ids.Count == 0)
        {
            return [];
        }

        return await _db.OperationalAssets
            .Where(a => ids.Contains(a.Id) && a.IsActive)
            .ToDictionaryAsync(a => a.Id);
    }

    private async Task SyncLoadingExpenseLinesAsync(
        LoadingRegister loading,
        List<LoadingExpenseLineInputModel> validLines,
        IReadOnlyDictionary<int, ExpenseType> typesById,
        IReadOnlyDictionary<int, ServiceProviderEntity> providersById,
        IReadOnlyDictionary<int, OperationalAsset> assetsById,
        IReadOnlyDictionary<int, List<AssetOwnershipShare>> ownershipByAsset)
    {
        var beforeSpId = loading.LogisticsServiceProviderId;
        var beforeName = loading.LogisticsCompanyName;
        var beforeTransport = loading.TransportExpenseUsd;
        var beforeWarehouse = loading.WarehouseExpenseUsd;
        var beforeOther = loading.OtherExpenseUsd;
        var beforeRailway = loading.RailwayExpenseUsd;

        var existing = await _db.LoadingExpenseLines
            .Where(l => l.LoadingRegisterId == loading.Id)
            .ToListAsync();
        var existingById = existing.ToDictionary(l => l.Id);
        var keptIds = new HashSet<int>();
        var entities = new List<LoadingExpenseLine>();

        foreach (var input in validLines)
        {
            LoadingExpenseLine entity;
            if (input.Id > 0 && existingById.TryGetValue(input.Id, out var found))
            {
                entity = found;
                keptIds.Add(found.Id);
            }
            else
            {
                entity = new LoadingExpenseLine { LoadingRegisterId = loading.Id };
                _db.LoadingExpenseLines.Add(entity);
            }

            entity.ExpenseTypeId = input.ExpenseTypeId;
            entity.CalculationMode = input.CalculationMode;
            entity.QuantityMt = input.CalculationMode == LoadingExpenseCalculationMode.PerMetricTon ? input.QuantityMt : input.QuantityMt;
            entity.UnitRateUsd = input.CalculationMode == LoadingExpenseCalculationMode.PerMetricTon ? input.UnitRateUsd : null;
            entity.AmountUsd = input.AmountUsd;
            entity.PartyType = input.PartyType;
            entity.ServiceProviderId = input.PartyType == LoadingExpensePartyType.ServiceProvider ? input.ServiceProviderId : null;
            entity.OperationalAssetId = input.PartyType == LoadingExpensePartyType.OperationalAsset ? input.OperationalAssetId : null;
            entity.Notes = input.Notes;
            entity.ExpenseTransactionId = input.ExpenseTransactionId;
            entity.AssetRentTransactionId = input.AssetRentTransactionId;
            entities.Add(entity);
        }

        await _db.SaveChangesAsync();

        foreach (var removed in existing.Where(l => !keptIds.Contains(l.Id)))
        {
            await CancelLoadingExpenseLineExpenseAsync(removed);
            await CancelLoadingExpenseLineRentAsync(removed, "ردیف مصرف بارگیری حذف شد.");
            _db.LoadingExpenseLines.Remove(removed);
        }

        await _db.SaveChangesAsync();

        foreach (var entity in entities)
        {
            switch (entity.PartyType)
            {
                case LoadingExpensePartyType.None:
                    await CancelLoadingExpenseLineExpenseAsync(entity);
                    await CancelLoadingExpenseLineRentAsync(entity, "ردیف مصرف بارگیری بدون طرف حساب شد.");
                    break;
                case LoadingExpensePartyType.ServiceProvider:
                    await CancelLoadingExpenseLineRentAsync(entity, "ردیف مصرف بارگیری به شرکت خدماتی تغییر کرد.");
                    await SyncLoadingExpenseLineServiceExpenseAsync(
                        loading,
                        entity,
                        typesById[entity.ExpenseTypeId],
                        providersById[entity.ServiceProviderId!.Value]);
                    break;
                case LoadingExpensePartyType.OperationalAsset:
                    await CancelLoadingExpenseLineExpenseAsync(entity);
                    await SyncLoadingExpenseLineAssetRentAsync(
                        loading,
                        entity,
                        assetsById[entity.OperationalAssetId!.Value],
                        ownershipByAsset[entity.OperationalAssetId!.Value]);
                    break;
            }
        }

        await _db.SaveChangesAsync();

        MirrorLoadingExpenseLinesToLoading(loading, entities, typesById, providersById, assetsById);

        var diff = AuditDiffFormatter.ForUpdate(
            ("LogisticsServiceProviderId", beforeSpId, loading.LogisticsServiceProviderId),
            ("LogisticsCompanyName", beforeName, loading.LogisticsCompanyName),
            ("TransportExpenseUsd", beforeTransport, loading.TransportExpenseUsd),
            ("WarehouseExpenseUsd", beforeWarehouse, loading.WarehouseExpenseUsd),
            ("OtherExpenseUsd", beforeOther, loading.OtherExpenseUsd),
            ("RailwayExpenseUsd", beforeRailway, loading.RailwayExpenseUsd));
        await _audit.LogAndSaveAsync(nameof(LoadingRegister), loading.Id, AuditAction.Update, diff: diff);
    }

    private static void MirrorLoadingExpenseLinesToLoading(
        LoadingRegister loading,
        IEnumerable<LoadingExpenseLine> lines,
        IReadOnlyDictionary<int, ExpenseType> typesById,
        IReadOnlyDictionary<int, ServiceProviderEntity> providersById,
        IReadOnlyDictionary<int, OperationalAsset> assetsById)
    {
        decimal transport = 0m, warehouse = 0m, railway = 0m, other = 0m;
        var materialized = lines.ToList();

        foreach (var line in materialized.Where(l => l.PartyType == LoadingExpensePartyType.None))
        {
            if (!typesById.TryGetValue(line.ExpenseTypeId, out var type))
            {
                other += line.AmountUsd;
                continue;
            }

            switch (ClassifyLoadingExpenseBucket(type))
            {
                case LoadingExpenseBucket.Transport: transport += line.AmountUsd; break;
                case LoadingExpenseBucket.Warehouse: warehouse += line.AmountUsd; break;
                case LoadingExpenseBucket.Railway: railway += line.AmountUsd; break;
                default: other += line.AmountUsd; break;
            }
        }

        loading.TransportExpenseUsd = transport > 0m ? transport : null;
        loading.WarehouseExpenseUsd = warehouse > 0m ? warehouse : null;
        loading.RailwayExpenseUsd = railway > 0m ? railway : null;
        loading.OtherExpenseUsd = other > 0m ? other : null;
        loading.RailwayRateUsd = null;
        loading.ChargeableQuantityMt = null;

        var firstProviderLine = materialized.FirstOrDefault(l =>
            l.PartyType == LoadingExpensePartyType.ServiceProvider && l.ServiceProviderId.HasValue);
        var firstAssetLine = materialized.FirstOrDefault(l =>
            l.PartyType == LoadingExpensePartyType.OperationalAsset && l.OperationalAssetId.HasValue);

        if (firstProviderLine is not null
            && providersById.TryGetValue(firstProviderLine.ServiceProviderId!.Value, out var provider))
        {
            loading.LogisticsServiceProviderId = provider.Id;
            loading.LogisticsCompanyName = provider.Name;
        }
        else if (firstAssetLine is not null
            && assetsById.TryGetValue(firstAssetLine.OperationalAssetId!.Value, out var asset))
        {
            loading.LogisticsServiceProviderId = null;
            loading.LogisticsCompanyName = asset.Name;
        }
        else
        {
            loading.LogisticsServiceProviderId = null;
            loading.LogisticsCompanyName = null;
        }
    }

    private static LoadingExpenseBucket ClassifyLoadingExpenseBucket(ExpenseType type)
    {
        if (ExpenseClassification.IsWagonRent(type.Code, type.Name, type.NamePersian, null))
        {
            return LoadingExpenseBucket.Railway;
        }

        return type.Category switch
        {
            "Storage" => LoadingExpenseBucket.Warehouse,
            "Transport" => LoadingExpenseBucket.Transport,
            _ => LoadingExpenseBucket.Other
        };
    }

    private async Task CancelLoadingExpenseLineExpenseAsync(LoadingExpenseLine line)
    {
        if (line.ExpenseTransactionId.HasValue)
        {
            var expense = await _db.ExpenseTransactions
                .FirstOrDefaultAsync(e => e.Id == line.ExpenseTransactionId.Value && !e.IsCancelled);
            if (expense is not null)
            {
                await CancelLoadingServiceExpenseAsync(expense);
            }
        }

        line.ExpenseTransactionId = null;
        line.LedgerEntryId = null;
    }

    private async Task CancelLoadingExpenseLineRentAsync(LoadingExpenseLine line, string reason)
    {
        if (line.AssetRentTransactionId.HasValue)
        {
            var rent = await _db.AssetRentTransactions
                .Include(r => r.RentShares)
                .FirstOrDefaultAsync(r => r.Id == line.AssetRentTransactionId.Value && !r.IsCancelled);
            if (rent is not null)
            {
                await CancelLoadingAssetRentAsync(rent, reason);
            }
        }

        line.AssetRentTransactionId = null;
    }

    private async Task SyncLoadingExpenseLineServiceExpenseAsync(
        LoadingRegister loading,
        LoadingExpenseLine line,
        ExpenseType expenseType,
        ServiceProviderEntity provider)
    {
        var description = $"{expenseType.NamePersian ?? expenseType.Name} بارگیری #{loading.Id} - {(string.IsNullOrWhiteSpace(loading.Contract?.ContractNumber) ? $"قرارداد #{loading.ContractId}" : $"قرارداد {loading.Contract!.ContractNumber}")} - {provider.Name}";

        ExpenseTransaction? expense = null;
        if (line.ExpenseTransactionId.HasValue)
        {
            expense = await _db.ExpenseTransactions
                .FirstOrDefaultAsync(e => e.Id == line.ExpenseTransactionId.Value && !e.IsCancelled);
        }

        if (expense is null)
        {
            expense = new ExpenseTransaction
            {
                ExpenseTypeId = expenseType.Id,
                ContractId = loading.ContractId,
                LoadingRegisterId = loading.Id,
                ServiceProviderId = provider.Id,
                ExpenseDate = ToUtcDate(loading.LoadingDate),
                Amount = line.AmountUsd,
                Currency = SystemCurrency.BaseCurrencyCode,
                AppliedFxRateToUsd = 1m,
                AmountUsd = line.AmountUsd,
                Description = description
            };
            _db.ExpenseTransactions.Add(expense);
            await _db.SaveChangesAsync();

            var ledger = BuildLoadingServiceExpenseLedger(expenseType, expense, loading);
            _db.LedgerEntries.Add(ledger);
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
                    ("LoadingRegisterId", expense.LoadingRegisterId),
                    ("ServiceProviderId", expense.ServiceProviderId),
                    ("ExpenseDate", expense.ExpenseDate),
                    ("AmountUsd", expense.AmountUsd),
                    ("Description", expense.Description),
                    ("LedgerReference", ledger.Reference)));

            line.ExpenseTransactionId = expense.Id;
            line.LedgerEntryId = ledger.Id;
            return;
        }

        var previousAmountUsd = expense.AmountUsd;
        var previousServiceProviderId = expense.ServiceProviderId;
        var previousDescription = expense.Description;

        expense.ExpenseTypeId = expenseType.Id;
        expense.ContractId = loading.ContractId;
        expense.LoadingRegisterId = loading.Id;
        expense.ServiceProviderId = provider.Id;
        expense.ExpenseDate = ToUtcDate(loading.LoadingDate);
        expense.Amount = line.AmountUsd;
        expense.Currency = SystemCurrency.BaseCurrencyCode;
        expense.AppliedFxRateToUsd = 1m;
        expense.AmountUsd = line.AmountUsd;
        expense.Description = description;

        var existingLedger = await _db.LedgerEntries
            .Where(l => l.SourceType == "Expense" && l.SourceId == expense.Id)
            .OrderByDescending(l => l.Id)
            .FirstOrDefaultAsync();

        if (existingLedger is null)
        {
            existingLedger = BuildLoadingServiceExpenseLedger(expenseType, expense, loading);
            _db.LedgerEntries.Add(existingLedger);
        }
        else
        {
            ApplyLoadingServiceExpenseLedger(existingLedger, expenseType, expense, loading);
        }

        await _audit.LogAsync(
            nameof(ExpenseTransaction),
            expense.Id,
            AuditAction.Update,
            diff: AuditDiffFormatter.ForUpdate(
                ("ServiceProviderId", previousServiceProviderId, expense.ServiceProviderId),
                ("AmountUsd", previousAmountUsd, expense.AmountUsd),
                ("Description", previousDescription, expense.Description)));
        await _db.SaveChangesAsync();

        line.ExpenseTransactionId = expense.Id;
        line.LedgerEntryId = existingLedger.Id;
    }

    private async Task SyncLoadingExpenseLineAssetRentAsync(
        LoadingRegister loading,
        LoadingExpenseLine line,
        OperationalAsset asset,
        IReadOnlyList<AssetOwnershipShare> ownershipShares)
    {
        var amountUsd = line.AmountUsd;
        var rentDate = ToUtcDate(loading.LoadingDate);
        var chargedToType = ResolveLoadingAssetRentChargedToType(loading.Contract?.ContractType);
        var chargedToContractId = chargedToType is AssetRentChargedToType.PurchaseContract or AssetRentChargedToType.SalesContract
            ? loading.ContractId
            : (int?)null;

        var quantity = line.QuantityMt.HasValue && line.QuantityMt.Value > 0m
            ? line.QuantityMt.Value
            : (loading.LoadedQuantityMt > 0m ? loading.LoadedQuantityMt : (decimal?)null);

        decimal rate;
        if (line.CalculationMode == LoadingExpenseCalculationMode.PerMetricTon && line.UnitRateUsd is > 0m)
        {
            rate = line.UnitRateUsd.Value;
        }
        else if (quantity is > 0m)
        {
            rate = decimal.Round(amountUsd / quantity.Value, 4, MidpointRounding.AwayFromZero);
        }
        else
        {
            rate = asset.DefaultInternalRateUsd ?? amountUsd;
        }

        var description = BuildLoadingAssetRentDescription(loading, asset);

        AssetRentTransaction? rent = null;
        if (line.AssetRentTransactionId.HasValue)
        {
            rent = await _db.AssetRentTransactions
                .Include(r => r.RentShares)
                .FirstOrDefaultAsync(r => r.Id == line.AssetRentTransactionId.Value && !r.IsCancelled);
        }

        if (rent is null)
        {
            rent = new AssetRentTransaction
            {
                OperationalAssetId = asset.Id,
                LoadingRegisterId = loading.Id,
                RentDate = rentDate,
                UsageType = AssetRentUsageType.InternalCompanyUse,
                ChargedToType = chargedToType,
                ChargedToContractId = chargedToContractId,
                QuantityMt = quantity,
                Rate = rate,
                Currency = SystemCurrency.BaseCurrencyCode,
                FxRateToUsd = 1m,
                AmountOriginal = amountUsd,
                AmountUsd = amountUsd,
                ReferenceDocument = BuildLoadingAssetRentReference(loading),
                Description = description,
                IsPostedToLedger = false
            };

            _db.AssetRentTransactions.Add(rent);
            await _db.SaveChangesAsync();

            _db.AssetRentShares.AddRange(BuildLoadingAssetRentShareSnapshots(rent.Id, amountUsd, ownershipShares));
            await _audit.LogAndSaveAsync(
                nameof(AssetRentTransaction),
                rent.Id,
                AuditAction.Insert,
                diff: AuditDiffFormatter.ForCreate(
                    ("OperationalAssetId", rent.OperationalAssetId),
                    ("LoadingRegisterId", rent.LoadingRegisterId),
                    ("RentDate", rent.RentDate),
                    ("UsageType", rent.UsageType),
                    ("ChargedToType", rent.ChargedToType),
                    ("ChargedToContractId", rent.ChargedToContractId),
                    ("AmountUsd", rent.AmountUsd),
                    ("IsPostedToLedger", rent.IsPostedToLedger)));

            line.AssetRentTransactionId = rent.Id;
            return;
        }

        var previousAmountUsd = rent.AmountUsd;
        var previousRate = rent.Rate;
        var previousOperationalAssetId = rent.OperationalAssetId;

        rent.OperationalAssetId = asset.Id;
        rent.LoadingRegisterId = loading.Id;
        rent.RentDate = rentDate;
        rent.UsageType = AssetRentUsageType.InternalCompanyUse;
        rent.ChargedToType = chargedToType;
        rent.ChargedToContractId = chargedToContractId;
        rent.ChargedToCustomerId = null;
        rent.ChargedToCompanyId = null;
        rent.ChargedToPartnerId = null;
        rent.ChargedToServiceProviderId = null;
        rent.QuantityMt = quantity;
        rent.DistanceKm = null;
        rent.Days = null;
        rent.Rate = rate;
        rent.Currency = SystemCurrency.BaseCurrencyCode;
        rent.FxRateToUsd = 1m;
        rent.AmountOriginal = amountUsd;
        rent.AmountUsd = amountUsd;
        rent.ReferenceDocument = BuildLoadingAssetRentReference(loading);
        rent.Description = description;
        rent.IsPostedToLedger = false;
        rent.LedgerEntryId = null;

        if (rent.RentShares.Count > 0)
        {
            _db.AssetRentShares.RemoveRange(rent.RentShares);
        }

        await _audit.LogAsync(
            nameof(AssetRentTransaction),
            rent.Id,
            AuditAction.Update,
            diff: AuditDiffFormatter.ForUpdate(
                ("OperationalAssetId", previousOperationalAssetId, rent.OperationalAssetId),
                ("Rate", previousRate, rent.Rate),
                ("AmountUsd", previousAmountUsd, rent.AmountUsd)));
        await _db.SaveChangesAsync();

        _db.AssetRentShares.AddRange(BuildLoadingAssetRentShareSnapshots(rent.Id, amountUsd, ownershipShares));
        await _db.SaveChangesAsync();

        line.AssetRentTransactionId = rent.Id;
    }

    private async Task SyncLoadingServiceExpensesAsync(
        LoadingRegister loading,
        LoadingExpenseEditViewModel model,
        ServiceProviderEntity? serviceProvider)
    {
        var components = BuildLoadingServiceExpenseComponents(model);
        var componentCodes = components.Select(c => c.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var existingExpenses = await _db.ExpenseTransactions
            .Include(e => e.ExpenseType)
            .Where(e => e.LoadingRegisterId == loading.Id
                && e.ExpenseType != null
                && componentCodes.Contains(e.ExpenseType.Code))
            .ToListAsync();

        foreach (var component in components)
        {
            var existing = existingExpenses
                .Where(e => string.Equals(e.ExpenseType?.Code, component.Code, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(e => e.Id)
                .FirstOrDefault(e => !e.IsCancelled);

            if (serviceProvider is null || component.AmountUsd <= 0m)
            {
                if (existing is not null)
                {
                    await CancelLoadingServiceExpenseAsync(existing);
                }

                continue;
            }

            var expenseType = await EnsureLoadingServiceExpenseTypeAsync(component);
            var description = BuildLoadingServiceExpenseDescription(loading, component, serviceProvider.Name);

            if (existing is null)
            {
                var expense = new ExpenseTransaction
                {
                    ExpenseTypeId = expenseType.Id,
                    ContractId = loading.ContractId,
                    LoadingRegisterId = loading.Id,
                    ServiceProviderId = serviceProvider.Id,
                    ExpenseDate = ToUtcDate(loading.LoadingDate),
                    Amount = component.AmountUsd,
                    Currency = SystemCurrency.BaseCurrencyCode,
                    AppliedFxRateToUsd = 1m,
                    AmountUsd = component.AmountUsd,
                    Description = description
                };

                _db.ExpenseTransactions.Add(expense);
                await _db.SaveChangesAsync();

                var newLedger = BuildLoadingServiceExpenseLedger(expenseType, expense, loading);
                _db.LedgerEntries.Add(newLedger);
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
                        ("LoadingRegisterId", expense.LoadingRegisterId),
                        ("ServiceProviderId", expense.ServiceProviderId),
                        ("ExpenseDate", expense.ExpenseDate),
                        ("Amount", expense.Amount),
                        ("Currency", expense.Currency),
                        ("AmountUsd", expense.AmountUsd),
                        ("Description", expense.Description),
                        ("LedgerReference", newLedger.Reference)));
                continue;
            }

            var previousAmount = existing.Amount;
            var previousAmountUsd = existing.AmountUsd;
            var previousServiceProviderId = existing.ServiceProviderId;
            var previousDescription = existing.Description;
            var previousExpenseDate = existing.ExpenseDate;

            existing.ExpenseTypeId = expenseType.Id;
            existing.ContractId = loading.ContractId;
            existing.LoadingRegisterId = loading.Id;
            existing.ServiceProviderId = serviceProvider.Id;
            existing.ExpenseDate = ToUtcDate(loading.LoadingDate);
            existing.Amount = component.AmountUsd;
            existing.Currency = SystemCurrency.BaseCurrencyCode;
            existing.AppliedFxRateToUsd = 1m;
            existing.AmountUsd = component.AmountUsd;
            existing.Description = description;

            var ledger = await _db.LedgerEntries
                .Where(l => l.SourceType == "Expense" && l.SourceId == existing.Id)
                .OrderByDescending(l => l.Id)
                .FirstOrDefaultAsync();

            if (ledger is null)
            {
                ledger = BuildLoadingServiceExpenseLedger(expenseType, existing, loading);
                _db.LedgerEntries.Add(ledger);
            }
            else
            {
                ApplyLoadingServiceExpenseLedger(ledger, expenseType, existing, loading);
            }

            await _audit.LogAsync(
                nameof(ExpenseTransaction),
                existing.Id,
                AuditAction.Update,
                diff: AuditDiffFormatter.ForUpdate(
                    ("ServiceProviderId", previousServiceProviderId, existing.ServiceProviderId),
                    ("ExpenseDate", previousExpenseDate, existing.ExpenseDate),
                    ("Amount", previousAmount, existing.Amount),
                    ("AmountUsd", previousAmountUsd, existing.AmountUsd),
                    ("Description", previousDescription, existing.Description)));
            await _db.SaveChangesAsync();
        }
    }

    private async Task SyncLoadingAssetRentAsync(
        LoadingRegister loading,
        LoadingExpenseEditViewModel model,
        OperationalAsset? operationalAsset,
        IReadOnlyList<AssetOwnershipShare> activeOwnershipShares,
        ContractType? contractType = null)
    {
        var existingRents = await _db.AssetRentTransactions
            .Include(r => r.RentShares)
            .Where(r => r.LoadingRegisterId == loading.Id && !r.IsCancelled)
            .OrderByDescending(r => r.Id)
            .ToListAsync();

        var primaryRent = existingRents.FirstOrDefault();
        foreach (var duplicateRent in existingRents.Skip(1))
        {
            await CancelLoadingAssetRentAsync(duplicateRent, "Duplicate loading asset rent superseded.");
        }

        var amountUsd = CalculateLoadingExpenseTotalUsd(model);
        if (operationalAsset is null || amountUsd <= 0m)
        {
            if (primaryRent is not null)
            {
                await CancelLoadingAssetRentAsync(primaryRent, "Loading expense no longer uses an owned operational asset.");
            }

            return;
        }

        var rentDate = ToUtcDate(loading.LoadingDate);
        var chargedToType = ResolveLoadingAssetRentChargedToType(contractType ?? loading.Contract?.ContractType);
        var chargedToContractId = chargedToType is AssetRentChargedToType.PurchaseContract or AssetRentChargedToType.SalesContract
            ? loading.ContractId
            : (int?)null;
        var rate = ResolveLoadingAssetRentRate(model, operationalAsset, amountUsd);
        var description = BuildLoadingAssetRentDescription(loading, operationalAsset);

        if (primaryRent is null)
        {
            primaryRent = new AssetRentTransaction
            {
                OperationalAssetId = operationalAsset.Id,
                LoadingRegisterId = loading.Id,
                RentDate = rentDate,
                UsageType = AssetRentUsageType.InternalCompanyUse,
                ChargedToType = chargedToType,
                ChargedToContractId = chargedToContractId,
                QuantityMt = loading.LoadedQuantityMt > 0m ? loading.LoadedQuantityMt : null,
                Rate = rate,
                Currency = SystemCurrency.BaseCurrencyCode,
                FxRateToUsd = 1m,
                AmountOriginal = amountUsd,
                AmountUsd = amountUsd,
                ReferenceDocument = BuildLoadingAssetRentReference(loading),
                Description = description,
                IsPostedToLedger = false
            };

            _db.AssetRentTransactions.Add(primaryRent);
            await _db.SaveChangesAsync();

            _db.AssetRentShares.AddRange(BuildLoadingAssetRentShareSnapshots(primaryRent.Id, amountUsd, activeOwnershipShares));
            await _audit.LogAndSaveAsync(
                nameof(AssetRentTransaction),
                primaryRent.Id,
                AuditAction.Insert,
                diff: AuditDiffFormatter.ForCreate(
                    ("OperationalAssetId", primaryRent.OperationalAssetId),
                    ("LoadingRegisterId", primaryRent.LoadingRegisterId),
                    ("RentDate", primaryRent.RentDate),
                    ("UsageType", primaryRent.UsageType),
                    ("ChargedToType", primaryRent.ChargedToType),
                    ("ChargedToContractId", primaryRent.ChargedToContractId),
                    ("AmountUsd", primaryRent.AmountUsd),
                    ("IsPostedToLedger", primaryRent.IsPostedToLedger)));
            return;
        }

        var previousOperationalAssetId = primaryRent.OperationalAssetId;
        var previousRentDate = primaryRent.RentDate;
        var previousAmountUsd = primaryRent.AmountUsd;
        var previousRate = primaryRent.Rate;
        var previousChargedToType = primaryRent.ChargedToType;
        var previousChargedToContractId = primaryRent.ChargedToContractId;
        var previousDescription = primaryRent.Description;

        primaryRent.OperationalAssetId = operationalAsset.Id;
        primaryRent.LoadingRegisterId = loading.Id;
        primaryRent.RentDate = rentDate;
        primaryRent.UsageType = AssetRentUsageType.InternalCompanyUse;
        primaryRent.ChargedToType = chargedToType;
        primaryRent.ChargedToContractId = chargedToContractId;
        primaryRent.ChargedToCustomerId = null;
        primaryRent.ChargedToCompanyId = null;
        primaryRent.ChargedToPartnerId = null;
        primaryRent.ChargedToServiceProviderId = null;
        primaryRent.QuantityMt = loading.LoadedQuantityMt > 0m ? loading.LoadedQuantityMt : null;
        primaryRent.DistanceKm = null;
        primaryRent.Days = null;
        primaryRent.Rate = rate;
        primaryRent.Currency = SystemCurrency.BaseCurrencyCode;
        primaryRent.FxRateToUsd = 1m;
        primaryRent.AmountOriginal = amountUsd;
        primaryRent.AmountUsd = amountUsd;
        primaryRent.ReferenceDocument = BuildLoadingAssetRentReference(loading);
        primaryRent.Description = description;
        primaryRent.IsPostedToLedger = false;
        primaryRent.LedgerEntryId = null;

        if (primaryRent.RentShares.Count > 0)
        {
            _db.AssetRentShares.RemoveRange(primaryRent.RentShares);
        }

        await _audit.LogAsync(
            nameof(AssetRentTransaction),
            primaryRent.Id,
            AuditAction.Update,
            diff: AuditDiffFormatter.ForUpdate(
                ("OperationalAssetId", previousOperationalAssetId, primaryRent.OperationalAssetId),
                ("RentDate", previousRentDate, primaryRent.RentDate),
                ("ChargedToType", previousChargedToType, primaryRent.ChargedToType),
                ("ChargedToContractId", previousChargedToContractId, primaryRent.ChargedToContractId),
                ("Rate", previousRate, primaryRent.Rate),
                ("AmountUsd", previousAmountUsd, primaryRent.AmountUsd),
                ("Description", previousDescription, primaryRent.Description)));
        await _db.SaveChangesAsync();

        _db.AssetRentShares.AddRange(BuildLoadingAssetRentShareSnapshots(primaryRent.Id, amountUsd, activeOwnershipShares));
        await _db.SaveChangesAsync();
    }

    private async Task CancelLoadingAssetRentAsync(AssetRentTransaction rent, string reason)
    {
        if (rent.IsCancelled)
        {
            return;
        }

        rent.IsCancelled = true;
        rent.CancelledAtUtc = DateTime.UtcNow;
        rent.CancelReason = reason;
        await _audit.LogAsync(
            nameof(AssetRentTransaction),
            rent.Id,
            AuditAction.Update,
            diff: AuditDiffFormatter.ForUpdate(
                ("IsCancelled", false, true),
                ("CancelReason", null, rent.CancelReason)));
        await _db.SaveChangesAsync();
    }

    private async Task<List<AssetOwnershipShare>> GetActiveAssetOwnershipSharesAsync(int assetId, DateTime date)
    {
        var utcDate = ToUtcDate(date);
        return await _db.AssetOwnershipShares
            .AsNoTracking()
            .Where(s => s.OperationalAssetId == assetId
                && s.EffectiveFrom <= utcDate
                && (!s.EffectiveTo.HasValue || s.EffectiveTo.Value >= utcDate))
            .OrderBy(s => s.Id)
            .ToListAsync();
    }

    private static IReadOnlyList<AssetRentShare> BuildLoadingAssetRentShareSnapshots(
        int rentTransactionId,
        decimal amountUsd,
        IReadOnlyList<AssetOwnershipShare> ownershipShares)
    {
        var rows = new List<AssetRentShare>();
        var allocated = 0m;
        for (var i = 0; i < ownershipShares.Count; i++)
        {
            var ownershipShare = ownershipShares[i];
            var shareAmount = i == ownershipShares.Count - 1
                ? amountUsd - allocated
                : decimal.Round(amountUsd * ownershipShare.SharePercent / 100m, 4, MidpointRounding.AwayFromZero);
            allocated += shareAmount;
            rows.Add(new AssetRentShare
            {
                AssetRentTransactionId = rentTransactionId,
                OwnerType = ownershipShare.OwnerType,
                CompanyId = ownershipShare.CompanyId,
                PartnerId = ownershipShare.PartnerId,
                OwnerName = ownershipShare.OwnerName,
                SharePercent = ownershipShare.SharePercent,
                ShareAmountUsd = shareAmount,
                Notes = ownershipShare.Notes
            });
        }

        return rows;
    }

    private static AssetRentChargedToType ResolveLoadingAssetRentChargedToType(ContractType? contractType)
        => contractType switch
        {
            ContractType.Purchase => AssetRentChargedToType.PurchaseContract,
            ContractType.Sale => AssetRentChargedToType.SalesContract,
            _ => AssetRentChargedToType.CompanyInternal
        };

    private static decimal ResolveLoadingAssetRentRate(
        LoadingExpenseEditViewModel model,
        OperationalAsset asset,
        decimal amountUsd)
    {
        if (model.TransportRateUsd.HasValue && model.TransportRateUsd.Value > 0m)
        {
            return model.TransportRateUsd.Value;
        }

        if (model.RailwayRateUsd.HasValue && model.RailwayRateUsd.Value > 0m)
        {
            return model.RailwayRateUsd.Value;
        }

        if (model.LoadedQuantityMt > 0m)
        {
            return decimal.Round(amountUsd / model.LoadedQuantityMt, 4, MidpointRounding.AwayFromZero);
        }

        return asset.DefaultInternalRateUsd ?? amountUsd;
    }

    private static string BuildLoadingAssetRentReference(LoadingRegister loading)
        => $"LOAD-ASSET-{loading.Id}";

    private static string BuildLoadingAssetRentDescription(LoadingRegister loading, OperationalAsset asset)
    {
        var contractText = loading.Contract is null
            ? $"قرارداد #{loading.ContractId}"
            : $"قرارداد {loading.Contract.ContractNumber}";
        return $"کرایه داخلی دارایی عملیاتی بارگیری #{loading.Id} - {contractText} - {asset.Name}";
    }

    private async Task<ExpenseType> EnsureLoadingServiceExpenseTypeAsync(LoadingServiceExpenseComponent component)
    {
        var expenseType = await _db.ExpenseTypes
            .FirstOrDefaultAsync(t => t.Code == component.Code);

        if (expenseType is not null)
        {
            return expenseType;
        }

        expenseType = new ExpenseType
        {
            Code = component.Code,
            Name = component.Name,
            NamePersian = component.NamePersian,
            Category = component.Category,
            IsActive = true
        };
        _db.ExpenseTypes.Add(expenseType);
        await _db.SaveChangesAsync();
        return expenseType;
    }

    private async Task CancelLoadingServiceExpenseAsync(ExpenseTransaction expense)
    {
        var originalLedger = await _db.LedgerEntries
            .AsNoTracking()
            .Where(l => l.SourceType == "Expense" && l.SourceId == expense.Id)
            .OrderByDescending(l => l.Id)
            .FirstOrDefaultAsync();

        // مرحله ۵ — Reversal قبل از علامت‌خوردن IsCancelled. Idempotent.
        if (_expenseAccounting is not null)
        {
            await _expenseAccounting.TryPostExpenseReversalAsync(expense);
        }

        expense.IsCancelled = true;
        if (originalLedger is not null)
        {
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
                Description = $"لغو مصرف بارگیری #{expense.LoadingRegisterId} | {originalLedger.Description}",
                SourceType = "Expense",
                SourceId = expense.Id,
                Reference = (originalLedger.Reference ?? $"EXP-{expense.Id}") + "-CANCEL",
                ContractId = originalLedger.ContractId,
                ServiceProviderId = originalLedger.ServiceProviderId,
                ShipmentId = originalLedger.ShipmentId
            });
        }

        await _audit.LogAsync(
            nameof(ExpenseTransaction),
            expense.Id,
            AuditAction.Update,
            diff: AuditDiffFormatter.ForUpdate(("IsCancelled", false, true)));
        await _db.SaveChangesAsync();
    }

    private static LedgerEntry BuildLoadingServiceExpenseLedger(
        ExpenseType expenseType,
        ExpenseTransaction expense,
        LoadingRegister loading)
    {
        var ledger = new LedgerEntry();
        ApplyLoadingServiceExpenseLedger(ledger, expenseType, expense, loading);
        return ledger;
    }

    private static void ApplyLoadingServiceExpenseLedger(
        LedgerEntry ledger,
        ExpenseType expenseType,
        ExpenseTransaction expense,
        LoadingRegister loading)
    {
        ledger.EntryDate = expense.ExpenseDate;
        ledger.Side = LedgerSide.Credit;
        ledger.AmountUsd = expense.AmountUsd;
        ledger.Currency = SystemCurrency.BaseCurrencyCode;
        ledger.SourceAmount = expense.Amount;
        ledger.SourceCurrencyCode = expense.Currency;
        ledger.AppliedFxRateToUsd = expense.AppliedFxRateToUsd;
        ledger.AppliedFxRateDate = ToUtcDate(loading.LoadingDate);
        ledger.AppliedFxRateSource = "USD base currency";
        ledger.Description = $"ثبت مصرف خدماتی بارگیری - {expenseType.NamePersian ?? expenseType.Name} - {expense.Description}";
        ledger.SourceType = "Expense";
        ledger.SourceId = expense.Id;
        ledger.Reference = BuildLoadingServiceExpenseReference(expenseType, expense);
        ledger.ContractId = expense.ContractId;
        ledger.ServiceProviderId = expense.ServiceProviderId;
        ledger.ShipmentId = expense.ShipmentId;
    }

    private static string BuildLoadingServiceExpenseReference(ExpenseType expenseType, ExpenseTransaction expense)
    {
        var prefix = string.IsNullOrWhiteSpace(expenseType.Code)
            ? $"LOAD-EXP-{expense.Id}"
            : $"{expenseType.Code}-{expense.Id}";
        var suffix = expense.LoadingRegisterId.HasValue ? $"LOAD-{expense.LoadingRegisterId.Value}" : "LOAD";
        var value = $"{prefix} | {suffix}";
        return value.Length <= 200 ? value : value[..200];
    }

    private static string BuildLoadingServiceExpenseDescription(
        LoadingRegister loading,
        LoadingServiceExpenseComponent component,
        string serviceProviderName)
    {
        var contractNumber = loading.Contract?.ContractNumber;
        var contractText = string.IsNullOrWhiteSpace(contractNumber)
            ? $"قرارداد #{loading.ContractId}"
            : $"قرارداد {contractNumber}";
        return $"{component.NamePersian} بارگیری #{loading.Id} - {contractText} - {serviceProviderName}";
    }

    private static IReadOnlyList<LoadingServiceExpenseComponent> BuildLoadingServiceExpenseComponents(
        LoadingExpenseEditViewModel model)
        =>
        [
            new(LoadingTransportExpenseCode, "Loading Transport Freight", "کرایه حمل بارگیری", "Transport", model.TransportExpenseUsd ?? 0m),
            new(LoadingStorageExpenseCode, "Loading Storage Rent", "کرایه مخزن بارگیری", "Storage", model.WarehouseExpenseUsd ?? 0m),
            new(LoadingWagonRentExpenseCode, "Loading Wagon Rent", "کرایه واگون بارگیری", "Transport", model.RailwayExpenseUsd ?? 0m),
            new(LoadingOtherExpenseCode, "Loading Other Service Expense", "سایر مصارف خدماتی بارگیری", "Other", model.OtherExpenseUsd ?? 0m)
        ];

    private static LedgerSide ReverseSide(LedgerSide side)
        => side == LedgerSide.Credit ? LedgerSide.Debit : LedgerSide.Credit;

    private sealed record LoadingServiceExpenseComponent(
        string Code,
        string Name,
        string NamePersian,
        string Category,
        decimal AmountUsd);

    private async Task<IDbContextTransaction?> BeginTransactionIfSupportedAsync()
    {
        if (!_db.Database.IsRelational())
        {
            return null;
        }

        return await _db.Database.BeginTransactionAsync();
    }

    private static void ApplyLogisticsFreightSnapshot(LoadingCreateRowViewModel row, LoadingTransportType transportType)
    {
        var hasExplicitFreightRate = row.FreightRateUsdPerMt.HasValue && row.FreightRateUsdPerMt.Value > 0m;
        row.FreightRateUsdPerMt = NormalizePositiveDecimal(row.FreightRateUsdPerMt);
        row.TransportExpenseUsd = NormalizePositiveDecimal(row.TransportExpenseUsd);
        row.RailwayRateUsd = NormalizePositiveDecimal(row.RailwayRateUsd);
        row.RailwayExpenseUsd = NormalizePositiveDecimal(row.RailwayExpenseUsd);
        row.ChargeableQuantityMt = NormalizePositiveDecimal(row.ChargeableQuantityMt);

        if (!row.FreightRateUsdPerMt.HasValue && row.LoadedQuantityMt > 0m)
        {
            if (transportType == LoadingTransportType.Wagon && row.RailwayExpenseUsd.HasValue)
            {
                var quantity = row.ChargeableQuantityMt ?? row.LoadedQuantityMt;
                if (quantity > 0m)
                {
                    row.FreightRateUsdPerMt = decimal.Round(row.RailwayExpenseUsd.Value / quantity, 4, MidpointRounding.AwayFromZero);
                }
            }
            else if (row.TransportExpenseUsd.HasValue)
            {
                row.FreightRateUsdPerMt = decimal.Round(row.TransportExpenseUsd.Value / row.LoadedQuantityMt, 4, MidpointRounding.AwayFromZero);
            }
        }

        if (!hasExplicitFreightRate || !row.FreightRateUsdPerMt.HasValue || row.LoadedQuantityMt <= 0m)
        {
            return;
        }

        if (transportType == LoadingTransportType.Wagon)
        {
            var chargeableQuantityMt = row.ChargeableQuantityMt ?? row.LoadedQuantityMt;
            row.ChargeableQuantityMt = chargeableQuantityMt;
            row.RailwayRateUsd = row.FreightRateUsdPerMt;
            row.RailwayExpenseUsd = FreightShortageMath.GrossFreightUsd(chargeableQuantityMt, row.FreightRateUsdPerMt.Value);
            row.TransportExpenseUsd = null;
            return;
        }

        row.TransportExpenseUsd = FreightShortageMath.GrossFreightUsd(row.LoadedQuantityMt, row.FreightRateUsdPerMt.Value);
        row.ChargeableQuantityMt = null;
        row.RailwayRateUsd = null;
        row.RailwayExpenseUsd = null;
    }

    private static void ClearFreightSnapshot(LoadingCreateRowViewModel row)
    {
        row.LogisticsServiceProviderId = null;
        row.OperationalAssetId = null;
        row.LogisticsCompanyName = null;
        row.FreightRateUsdPerMt = null;
        row.TransportExpenseUsd = null;
        row.ChargeableQuantityMt = null;
        row.RailwayRateUsd = null;
        row.RailwayExpenseUsd = null;
    }

    private List<LoadingCreateRowViewModel> ExtractSubmittedRows(LoadingCreateViewModel model)
    {
        var submittedRows = model.Rows ?? [];
        if (!string.IsNullOrWhiteSpace(model.ImportedRowsJson))
        {
            try
            {
                submittedRows = JsonSerializer.Deserialize<List<LoadingCreateRowViewModel>>(
                        model.ImportedRowsJson,
                        LoadingRowsJsonOptions)
                    ?? [];
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Invalid compact loading rows payload.");
                ModelState.AddModelError(
                    nameof(model.Rows),
                    "اطلاعات سطرهای ایمپورت‌شده کامل دریافت نشد؛ صفحه را تازه کرده و دوباره تلاش کنید.");
            }
        }

        var rows = submittedRows
            .Where(row => row is not null)
            .Select((row, index) =>
            {
                row.RowKey = string.IsNullOrWhiteSpace(row.RowKey) ? CreateRowKey(index) : row.RowKey.Trim();
                row.Loss ??= new StageLossCaptureInput { Stage = LossEventStage.LoadingDifference };
                if (row.LoadingDate == default)
                {
                    row.LoadingDate = model.LoadingDate == default ? _businessClock.Today : model.LoadingDate;
                }

                return row;
            })
            .Where(row => !IsBlankRow(row))
            .ToList();

        if (rows.Count > 0)
        {
            return rows;
        }

        var fallbackRow = new LoadingCreateRowViewModel
        {
            RowKey = CreateRowKey(0),
            ContractId = model.ContractId > 0 ? model.ContractId : null,
            LoadingDate = model.LoadingDate == default ? _businessClock.Today : model.LoadingDate,
            VesselId = model.VesselId,
            TruckId = model.TruckId,
            BillOfLadingNumber = model.BillOfLadingNumber,
            WagonNumber = model.WagonNumber,
            ConsigneeName = model.ConsigneeName,
            DestinationName = model.DestinationName,
            LoadedQuantityMt = model.LoadedQuantityMt
        };

        return IsBlankRow(fallbackRow) ? [] : [fallbackRow];
    }

    private static void PrepareRowsForFastPreview(LoadingCreateViewModel model)
    {
        model.ImportedRowsJson = model.Rows.Count > LargeImportPreviewRows
            ? JsonSerializer.Serialize(model.Rows, LoadingRowsJsonOptions)
            : null;
    }

    private void ValidateCompactSubmittedRows(IReadOnlyList<LoadingCreateRowViewModel> rows)
    {
        foreach (var row in rows)
        {
            var validationResults = new List<ValidationResult>();
            Validator.TryValidateObject(
                row,
                new ValidationContext(row),
                validationResults,
                validateAllProperties: true);

            foreach (var validationResult in validationResults)
            {
                var members = validationResult.MemberNames.DefaultIfEmpty(string.Empty);
                foreach (var member in members)
                {
                    var key = string.IsNullOrWhiteSpace(member)
                        ? nameof(LoadingCreateViewModel.Rows)
                        : RowField(row.RowKey, member);
                    ModelState.AddModelError(key, validationResult.ErrorMessage ?? "مقدار این سطر معتبر نیست.");
                }
            }

            validationResults.Clear();
            Validator.TryValidateObject(
                row.Loss,
                new ValidationContext(row.Loss),
                validationResults,
                validateAllProperties: true);
            foreach (var validationResult in validationResults)
            {
                foreach (var member in validationResult.MemberNames.DefaultIfEmpty(string.Empty))
                {
                    var key = string.IsNullOrWhiteSpace(member)
                        ? nameof(LoadingCreateViewModel.Rows)
                        : RowLossField(row.RowKey, member);
                    ModelState.AddModelError(key, validationResult.ErrorMessage ?? "مقدار ضایعات این سطر معتبر نیست.");
                }
            }
        }
    }

    private static JsonSerializerOptions CreateLoadingRowsJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private static void EnsureEditableRows(LoadingCreateViewModel model)
    {
        model.Rows ??= [];

        if (model.Rows.Count == 0)
        {
            model.Rows.Add(new LoadingCreateRowViewModel
            {
                RowKey = CreateRowKey(0),
                LoadingDate = model.LoadingDate == default ? AfghanistanBusinessClock.SystemToday : model.LoadingDate
            });
        }

        for (var index = 0; index < model.Rows.Count; index++)
        {
            var row = model.Rows[index];
            row.RowKey = string.IsNullOrWhiteSpace(row.RowKey) ? CreateRowKey(index) : row.RowKey.Trim();
            if (row.LoadingDate == default)
            {
                row.LoadingDate = model.LoadingDate == default ? AfghanistanBusinessClock.SystemToday : model.LoadingDate;
            }
        }
    }

    private static void NormalizeRow(LoadingCreateRowViewModel row, LoadingTransportType transportType, DateTime fallbackDate)
    {
        row.RowKey = string.IsNullOrWhiteSpace(row.RowKey) ? CreateRowKey(0) : row.RowKey.Trim();
        row.LoadingDate = row.LoadingDate == default ? (fallbackDate == default ? AfghanistanBusinessClock.SystemToday : fallbackDate) : row.LoadingDate;
        row.BillOfLadingNumber = NormalizeNullable(row.BillOfLadingNumber);
        row.ImportedTransportReference = NormalizeNullable(row.ImportedTransportReference);
        row.WagonNumber = NormalizeNullable(row.WagonNumber);
        row.RouteDescription = NormalizeNullable(row.RouteDescription);
        row.LogisticsCompanyName = NormalizeNullable(row.LogisticsCompanyName);
        if (row.LogisticsServiceProviderId <= 0) row.LogisticsServiceProviderId = null;
        if (row.OperationalAssetId <= 0) row.OperationalAssetId = null;
        row.ConsigneeName = NormalizeNullable(row.ConsigneeName);
        row.DestinationName = NormalizeNullable(row.DestinationName);
        if (row.PlattsUsd <= 0) row.PlattsUsd = null;
        if (row.LoadingPriceUsd <= 0) row.LoadingPriceUsd = null;
        if (row.FreightRateUsdPerMt <= 0) row.FreightRateUsdPerMt = null;
        if (row.TransportExpenseUsd <= 0) row.TransportExpenseUsd = null;
        if (row.WarehouseExpenseUsd <= 0) row.WarehouseExpenseUsd = null;
        if (row.OtherExpenseUsd <= 0) row.OtherExpenseUsd = null;
        if (row.ChargeableQuantityMt <= 0) row.ChargeableQuantityMt = null;
        if (row.RailwayRateUsd <= 0) row.RailwayRateUsd = null;
        if (row.RailwayExpenseUsd <= 0) row.RailwayExpenseUsd = null;
        row.SettlementCurrencyCode = SystemCurrency.Normalize(row.SettlementCurrencyCode);
        row.RubPerUsdRate = NormalizePositiveDecimal(row.RubPerUsdRate);
        row.RubRateDate = row.RubRateDate.HasValue ? ToUtcDate(row.RubRateDate.Value) : null;
        row.RubRateSource = NormalizeNullable(row.RubRateSource);
        if (!IsRubSettlement(row.SettlementCurrencyCode))
        {
            row.RubRateStatus = RubSettlementRateStatus.NotRequired;
            row.RubPerUsdRate = null;
            row.RubRateDate = null;
            row.RubRateSource = null;
            row.AmountUsdAtRubLock = null;
            row.AmountRubAtRubLock = null;
        }

        switch (transportType)
        {
            case LoadingTransportType.Vessel:
                row.TruckId = null;
                row.WagonNumber = null;
                break;

            case LoadingTransportType.Wagon:
                row.VesselId = null;
                row.TruckId = null;
                row.ImportedTransportReference = null;
                break;

            case LoadingTransportType.Truck:
                row.VesselId = null;
                row.WagonNumber = null;
                break;

            default:
                row.VesselId = null;
                row.TruckId = null;
                row.WagonNumber = null;
                row.ImportedTransportReference = null;
                break;
        }
    }

    private async Task<List<string>> ResolveImportedTruckReferencesAsync(
        IEnumerable<LoadingCreateRowViewModel> rows,
        bool createMissing)
    {
        var truckRows = rows
            .Where(row => !row.TruckId.HasValue && !string.IsNullOrWhiteSpace(row.ImportedTransportReference))
            .ToList();

        if (truckRows.Count == 0)
        {
            return [];
        }

        var existingTrucks = await _db.Trucks
            .Where(truck => truck.IsActive)
            .ToListAsync();

        var truckLookup = existingTrucks
            .GroupBy(truck => NormalizeLookupText(truck.PlateNumber), StringComparer.Ordinal)
            .Where(group => !string.IsNullOrWhiteSpace(group.Key))
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        var autoCreatedTrucks = new List<string>();
        var pendingCreations = new Dictionary<string, Truck>(StringComparer.Ordinal);

        foreach (var row in truckRows)
        {
            var normalizedReference = NormalizeLookupText(row.ImportedTransportReference);
            if (string.IsNullOrWhiteSpace(normalizedReference))
            {
                continue;
            }

            if (truckLookup.TryGetValue(normalizedReference, out var existingTruck))
            {
                row.TruckId = existingTruck.Id;
                continue;
            }

            if (!createMissing || pendingCreations.ContainsKey(normalizedReference))
            {
                continue;
            }

            var newTruck = new Truck
            {
                PlateNumber = row.ImportedTransportReference!.Trim(),
                IsActive = true
            };

            pendingCreations[normalizedReference] = newTruck;
            autoCreatedTrucks.Add(newTruck.PlateNumber);
            _db.Trucks.Add(newTruck);
        }

        if (createMissing && pendingCreations.Count > 0)
        {
            await _db.SaveChangesAsync();
            foreach (var pending in pendingCreations)
            {
                truckLookup[pending.Key] = pending.Value;
            }
        }

        foreach (var row in truckRows)
        {
            var normalizedReference = NormalizeLookupText(row.ImportedTransportReference);
            if (!string.IsNullOrWhiteSpace(normalizedReference)
                && truckLookup.TryGetValue(normalizedReference, out var truck))
            {
                row.TruckId = truck.Id;
            }
        }

        return autoCreatedTrucks;
    }

    private async Task<List<string>> ResolveImportedVesselReferencesAsync(
        IEnumerable<LoadingCreateRowViewModel> rows,
        bool createMissing)
    {
        var vesselRows = rows
            .Where(row => !row.VesselId.HasValue && !string.IsNullOrWhiteSpace(row.ImportedTransportReference))
            .ToList();

        if (vesselRows.Count == 0)
        {
            return [];
        }

        var existingVessels = await _db.Vessels
            .Where(v => v.IsActive)
            .ToListAsync();

        var vesselLookup = existingVessels
            .GroupBy(v => NormalizeLookupText(v.Name), StringComparer.Ordinal)
            .Where(g => !string.IsNullOrWhiteSpace(g.Key))
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var autoCreatedVessels = new List<string>();
        var pendingCreations = new Dictionary<string, Vessel>(StringComparer.Ordinal);

        foreach (var row in vesselRows)
        {
            var normalizedRef = NormalizeLookupText(row.ImportedTransportReference);
            if (string.IsNullOrWhiteSpace(normalizedRef))
            {
                continue;
            }

            if (vesselLookup.TryGetValue(normalizedRef, out var existingVessel))
            {
                row.VesselId = existingVessel.Id;
                continue;
            }

            if (!createMissing || pendingCreations.ContainsKey(normalizedRef))
            {
                continue;
            }

            var newVessel = new Vessel
            {
                Name = row.ImportedTransportReference!.Trim(),
                IsActive = true
            };

            pendingCreations[normalizedRef] = newVessel;
            autoCreatedVessels.Add(newVessel.Name);
            _db.Vessels.Add(newVessel);
        }

        if (createMissing && pendingCreations.Count > 0)
        {
            await _db.SaveChangesAsync();
            foreach (var pending in pendingCreations)
            {
                vesselLookup[pending.Key] = pending.Value;
            }
        }

        foreach (var row in vesselRows)
        {
            var normalizedRef = NormalizeLookupText(row.ImportedTransportReference);
            if (!string.IsNullOrWhiteSpace(normalizedRef)
                && vesselLookup.TryGetValue(normalizedRef, out var vessel))
            {
                row.VesselId = vessel.Id;
            }
        }

        return autoCreatedVessels;
    }

    private async Task<int?> ResolveLocationIdAsync(string? importedLocationName)
    {
        var normalizedImportedLocation = NormalizeLookupText(importedLocationName);
        if (string.IsNullOrWhiteSpace(normalizedImportedLocation))
        {
            return null;
        }

        var locations = await _db.Locations
            .AsNoTracking()
            .ToListAsync();

        var location = locations.FirstOrDefault(candidate =>
            string.Equals(NormalizeLookupText(candidate.Name), normalizedImportedLocation, StringComparison.Ordinal)
            || string.Equals(NormalizeLookupText(candidate.NamePersian), normalizedImportedLocation, StringComparison.Ordinal));

        return location?.Id;
    }

    private void ApplyContractPricingDefaults(
        Contract contract,
        ContractPriceResult pricingResult,
        IEnumerable<LoadingCreateRowViewModel> rows,
        bool addValidationErrors)
    {
        foreach (var row in rows)
        {
            row.PlattsUsd = NormalizePositiveDecimal(row.PlattsUsd);
            row.LoadingPriceUsd = NormalizePositiveDecimal(row.LoadingPriceUsd);
            if (addValidationErrors && !addValidationErrors && !row.PlattsUsd.HasValue && contract.PricingMethod == PricingMethod.FormulaPlatts)
            {
                if (pricingResult.BasePlattsPrice.HasValue)
                {
                    row.PlattsUsd = pricingResult.BasePlattsPrice.Value;
                }
                // When Platts price is not yet available (e.g. monthly price not entered),
                // do NOT block saving — allow loading to be recorded without price,
                // so it can be updated later when the monthly rate is determined.
            }

        }
    }

    // بارگیری‌هایی که کلید یکتای‌شان از قبل در دیتابیس هست یا داخل همین درخواست تکرار شده‌اند
    // را رد می‌کند. هیچ داده‌ای حذف یا بازنویسی نمی‌شود؛ فقط ثبت دوباره جلوگیری می‌شود.
    private async Task<bool> ValidateNoDuplicateLoadingsAsync(
        IReadOnlyList<(LoadingCreateRowViewModel Row, LoadingRegister Loading)> createdRows)
    {
        var keyedRows = createdRows
            .Where(row => !string.IsNullOrEmpty(row.Loading.ImportUniqueKey))
            .ToList();
        if (keyedRows.Count == 0)
        {
            return true;
        }

        var keys = keyedRows.Select(row => row.Loading.ImportUniqueKey!).Distinct().ToList();
        var existingKeys = (await _db.LoadingRegisters
                .AsNoTracking()
                .Where(l => l.ImportUniqueKey != null && keys.Contains(l.ImportUniqueKey))
                .Select(l => l.ImportUniqueKey!)
                .ToListAsync())
            .ToHashSet(StringComparer.Ordinal);

        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        var isValid = true;
        foreach (var keyedRow in keyedRows)
        {
            var key = keyedRow.Loading.ImportUniqueKey!;
            if (existingKeys.Contains(key))
            {
                isValid = false;
                ModelState.AddModelError(
                    RowField(keyedRow.Row.RowKey, nameof(keyedRow.Row.BillOfLadingNumber)),
                    "این بارگیری قبلاً در همین قرارداد ثبت شده است و دوباره ثبت نمی‌شود.");
                continue;
            }

            if (!seenKeys.Add(key))
            {
                isValid = false;
                ModelState.AddModelError(
                    RowField(keyedRow.Row.RowKey, nameof(keyedRow.Row.BillOfLadingNumber)),
                    "این بارگیری در همین فایل تکرار شده است و فقط یک بار ثبت می‌شود.");
            }
        }

        return isValid;
    }

    private static bool IsBlankRow(LoadingCreateRowViewModel row)
        => row.VesselId is null
           && row.TruckId is null
           && string.IsNullOrWhiteSpace(row.ImportedTransportReference)
           && string.IsNullOrWhiteSpace(row.BillOfLadingNumber)
           && string.IsNullOrWhiteSpace(row.WagonNumber)
           && string.IsNullOrWhiteSpace(row.LogisticsCompanyName)
           && string.IsNullOrWhiteSpace(row.ConsigneeName)
           && string.IsNullOrWhiteSpace(row.DestinationName)
           && row.LoadedQuantityMt <= 0
           && (!row.PlattsUsd.HasValue || row.PlattsUsd.Value <= 0)
           && (!row.LoadingPriceUsd.HasValue || row.LoadingPriceUsd.Value <= 0)
           && (!row.FreightRateUsdPerMt.HasValue || row.FreightRateUsdPerMt.Value <= 0)
           && (!row.LogisticsServiceProviderId.HasValue || row.LogisticsServiceProviderId.Value <= 0)
           && string.IsNullOrWhiteSpace(row.RouteDescription)
           && (!row.TransportExpenseUsd.HasValue || row.TransportExpenseUsd.Value <= 0)
           && (!row.WarehouseExpenseUsd.HasValue || row.WarehouseExpenseUsd.Value <= 0)
           && (!row.OtherExpenseUsd.HasValue || row.OtherExpenseUsd.Value <= 0);

    private static LossEventSubmission BuildLoadingLossSubmission(
        LoadingCreateViewModel model,
        LoadingCreateRowViewModel row,
        int? contractId = null)
        => StageLossCaptureMapper.ToSubmission(
            row.Loss,
            new StageLossCaptureContext
            {
                Stage = LossEventStage.LoadingDifference,
                ActualQuantityMt = row.LoadedQuantityMt,
                EventDate = row.LoadingDate,
                ProductId = model.ProductId,
                ContractId = contractId ?? model.ContractId,
                DefaultReference = row.BillOfLadingNumber
            });

    private static string RowLossField(string rowKey, string fieldName)
        => $"Rows[{rowKey}].Loss.{fieldName}";

    private static string RowField(string rowKey, string fieldName)
        => $"Rows[{rowKey}].{fieldName}";

    private static string CreateRowKey(int index)
        => $"row_{index}";

    private static string? NormalizeNullable(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static DateTime NormalizeDate(DateTime value)
        => new(value.Year, value.Month, value.Day, 0, 0, 0, DateTimeKind.Utc);

    private static DateTime NormalizeMonth(DateTime value)
        => new(value.Year, value.Month, 1, 0, 0, 0, DateTimeKind.Utc);

    private static string NormalizeLookupText(string? value)
        => new((value ?? string.Empty)
            .Trim()
            .ToUpperInvariant()
            .Where(char.IsLetterOrDigit)
            .ToArray());

    private static LoadingTransportType ResolveTransportType(
        LoadingTransportType storedTransportType,
        int? vesselId,
        int? truckId,
        string? wagonNumber)
    {
        if (storedTransportType != LoadingTransportType.Unspecified)
        {
            return storedTransportType;
        }

        if (vesselId.HasValue)
        {
            return LoadingTransportType.Vessel;
        }

        if (!string.IsNullOrWhiteSpace(wagonNumber))
        {
            return LoadingTransportType.Wagon;
        }

        if (truckId.HasValue)
        {
            return LoadingTransportType.Truck;
        }

        return LoadingTransportType.Unspecified;
    }

    private static string BuildVehicleSummary(
        LoadingTransportType transportType,
        string? vesselName,
        string? truckPlateNumber,
        string? wagonNumber)
    {
        return transportType switch
        {
            LoadingTransportType.Vessel when !string.IsNullOrWhiteSpace(vesselName) => $"کشتی: {vesselName}",
            LoadingTransportType.Wagon when !string.IsNullOrWhiteSpace(wagonNumber) => $"واگن: {wagonNumber}",
            LoadingTransportType.Truck when !string.IsNullOrWhiteSpace(truckPlateNumber) => $"موتر: {truckPlateNumber}",
            _ when !string.IsNullOrWhiteSpace(vesselName) => $"کشتی: {vesselName}",
            _ when !string.IsNullOrWhiteSpace(wagonNumber) => $"واگن: {wagonNumber}",
            _ when !string.IsNullOrWhiteSpace(truckPlateNumber) => $"موتر: {truckPlateNumber}",
            _ => "بدون وسیله ثبت‌شده"
        };
    }

    private static string GetTransportTypeLabel(LoadingTransportType transportType)
        => transportType switch
        {
            LoadingTransportType.Vessel => "کشتی",
            LoadingTransportType.Wagon => "واگن",
            LoadingTransportType.Truck => "موتر",
            _ => "نامشخص"
        };

    private static decimal? CalculateLoadingValueUsd(decimal loadedQuantityMt, decimal? loadingPriceUsd)
        => LoadingRubSettlement.CalculateLoadingValueUsd(loadedQuantityMt, loadingPriceUsd);

    private static decimal? ResolveRowLoadingPriceUsd(
        Contract contract,
        LoadingCreateRowViewModel row,
        ContractPriceResult pricingResult)
    {
        return NormalizePositiveDecimal(row.LoadingPriceUsd);
    }

    private static string BuildPricingResolutionMessage(string fieldLabel, ContractPriceResult pricingResult)
    {
        if (!string.IsNullOrWhiteSpace(pricingResult.Reason))
        {
            return $"{fieldLabel} از قرارداد قابل محاسبه نیست. {pricingResult.Reason}";
        }

        return $"{fieldLabel} از قرارداد قابل محاسبه نیست. تنظیمات قیمت‌گذاری قرارداد را بررسی کنید.";
    }
}
