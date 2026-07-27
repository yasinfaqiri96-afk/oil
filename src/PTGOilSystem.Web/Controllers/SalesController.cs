using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Caching.Memory;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Helpers;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.LossEvents;
using PTGOilSystem.Web.Models.Sales;
using PTGOilSystem.Web.Security;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Audit;
using PTGOilSystem.Web.Services.Exceptions;

namespace PTGOilSystem.Web.Controllers;

[Authorize]
public partial class SalesController : Controller
{
    private readonly ApplicationDbContext _db;
    private readonly IStockService _stock;
    private readonly IPricingService _pricing;
    private readonly ICurrencyConversionService _currencyConversion;
    private readonly IAuditService _audit;
    private readonly ILossEventWorkflowService _lossWorkflow;
    private readonly ILogger<SalesController> _logger;
    private readonly IMemoryCache? _cache;
    private readonly IInventoryLineageWriter _lineage;
    private readonly IFormTokenGuard _formTokens;
    // مرحله ۷ — Dual-write اختیاری به دفتر کل جدید. پشت Feature Flag و null-safe.
    private readonly Services.Accounting.ISalesAccountingAdapter? _salesAccounting;
    private const int DefaultListLimit = 100;
    private const int LookupLimit = 200;

    // مرحله ۷ — Dual-write داخل همان Transaction قدیمی. باید بعد از ذخیرهٔ حرکات خروجِ موجودی
    // صدا زده شود: COGS مقدار و ترمینال را از همان سطرهای InventoryMovement می‌خواند.
    // درآمد و COGS دو Flag جدا دارند، پس هر کدام مستقل Skip می‌شود.
    private async Task PostSaleAccountingAsync(SalesTransaction sale)
    {
        if (_salesAccounting is null)
        {
            return;
        }

        await _salesAccounting.TryPostSaleAsync(sale);
        await _salesAccounting.TryPostCogsAsync(sale);
    }

    // لغو فروش باید هر دو دفتر را با هم برگرداند. ردیف‌های Legacy همان‌جا معکوس می‌شوند و
    // ژورنال‌های دفتر کل جدید اینجا، از طریق سرویس مرکزی، برگشت رسمی و متوازن می‌گیرند.
    //
    // برای «تحویل پیش‌فروش» شکست برگشتِ ژورنال عمداً خطا می‌دهد تا کل لغو برگردد و هرگز
    // تحویلی لغو نشود که درآمد و بهای تمام‌شده‌اش هنوز روی حساب‌هاست. برای فروش‌های دیگر رفتار
    // تاریخی حفظ می‌شود: لغو Legacy انجام می‌شود و شکست ژورنال فقط لاگ می‌شود، چون این مسیر
    // سال‌ها بدون ثبت جدید کار کرده و شکست‌دادن ناگهانیِ لغوهای قدیمی (مثلاً دورهٔ بسته) ریسک
    // عملیاتی بزرگ‌تری از خودِ گپ است.
    private async Task ReverseSaleAccountingAsync(SalesTransaction sale)
    {
        if (_salesAccounting is null)
        {
            return;
        }

        var reversalDate = DateTime.UtcNow.Date;

        try
        {
            await _salesAccounting.TryReverseSaleAsync(sale, reversalDate);
            await _salesAccounting.TryReverseCogsAsync(sale, reversalDate);
            // مصرف‌های پیش‌دریافتِ همین تحویل آزاد می‌شوند؛ ژورنالِ مستقلِ مصرف‌های «بعد از تحویل» هم
            // معکوس می‌شود. Idempotent: لغو دوباره چیزی برای آزادکردن پیدا نمی‌کند.
            await _salesAccounting.TryReleaseAdvanceApplicationsAsync(sale, reversalDate);
        }
        catch (Exception exception) when (!sale.PreSaleOrderId.HasValue)
        {
            _logger.LogError(
                exception,
                "Journal reversal failed for cancelled sale {SaleId}; legacy cancellation kept.",
                sale.Id);
        }
    }

    private sealed record LookupOption(int Id, string Name);
    private sealed record TankLookupOption(int Id, string Display);
    private sealed record CurrencyLookupOption(string Code);
    private sealed record TerminalStockAllocation(int ContractId, decimal QuantityMt);

    private sealed record TerminalStockSourceBalance(
        int ContractId,
        int CompanyId,
        string ContractNumber,
        DateTime ContractDate,
        decimal AvailableMt);

    [ActivatorUtilitiesConstructor]
    public SalesController(
        ApplicationDbContext db,
        IStockService stock,
        ICurrencyConversionService currencyConversion,
        IAuditService audit,
        ILogger<SalesController> logger,
        IPricingService? pricing = null,
        ILossEventWorkflowService? lossWorkflow = null,
        IMemoryCache? cache = null,
        IInventoryLineageWriter? lineage = null,
        IFormTokenGuard? formTokens = null,
        Services.Accounting.ISalesAccountingAdapter? salesAccounting = null)
    {
        _salesAccounting = salesAccounting;
        _db = db;
        _stock = stock;
        _pricing = pricing ?? new PricingService(db);
        _currencyConversion = currencyConversion;
        _audit = audit;
        _lossWorkflow = lossWorkflow ?? new LossEventWorkflowService(db, stock, audit);
        _logger = logger;
        _cache = cache;
        _lineage = lineage ?? InventoryLineageWriterFactory.Disabled(db);
        _formTokens = formTokens ?? new FormTokenGuard(db);
    }

    public SalesController(
        ApplicationDbContext db,
        IStockService stock,
        IAuditService audit,
        ILogger<SalesController> logger,
        IPricingService? pricing = null,
        ILossEventWorkflowService? lossWorkflow = null)
        : this(
            db,
            stock,
            new CurrencyConversionService(pricing ?? new PricingService(db)),
            audit,
            logger,
            pricing,
            lossWorkflow)
    {
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

    private async Task PopulateLookupsAsync(
        SalesCreateViewModel? createModel = null,
        SalesIndexFilterViewModel? filter = null)
    {
        var selectedSaleContractId = createModel?.ContractId ?? filter?.ContractId;
        var selectedShipmentId = createModel?.ShipmentId;

        var saleContracts = await _db.Contracts
            .AsNoTracking()
            .Where(c => c.ContractType == ContractType.Sale)
            .OrderBy(c => selectedSaleContractId.HasValue && c.Id == selectedSaleContractId.Value ? 0 : 1)
            .ThenByDescending(c => c.ContractDate)
            .ThenBy(c => c.ContractNumber)
            .Take(LookupLimit)
            .Select(c => new
            {
                c.Id,
                c.ContractNumber,
                c.ContractType,
                ProductName = c.Product != null ? c.Product.Name : null,
                UnitSymbol = c.Unit != null ? c.Unit.Symbol : null,
                UnitCode = c.Unit != null ? c.Unit.Code : null,
                UnitNamePersian = c.Unit != null ? c.Unit.NamePersian : null,
                UnitName = c.Unit != null ? c.Unit.Name : null,
                c.DestinationLocationId
            })
            .ToListAsync();

        ViewBag.SaleContractDestinationMap = saleContracts
            .Select(c => new { contractId = c.Id, destinationLocationId = c.DestinationLocationId })
            .ToList();

        ViewBag.Contracts = new SelectList(
            saleContracts
                .Select(c => new ContractLookupOption(
                    c.Id,
                    ContractUiText.FormatLookup(
                        c.ContractNumber,
                        c.ContractType,
                        c.ProductName,
                        ContractUiText.ResolveUnitText(c.UnitSymbol, c.UnitCode, c.UnitNamePersian, c.UnitName))))
                .ToList(),
            nameof(ContractLookupOption.Id),
            nameof(ContractLookupOption.Display),
            selectedSaleContractId);

        var companyLookups = await GetCachedLookupAsync(
            "sales:lookups:companies:v1",
            () => _db.Companies
                .AsNoTracking()
                .Where(c => c.IsActive)
                .OrderBy(c => c.Code)
                .Select(c => new LookupOption(c.Id, c.Name))
                .ToListAsync());
        ViewBag.Companies = new SelectList(
            companyLookups,
            "Id",
            "Name",
            createModel?.CompanyId ?? filter?.CompanyId);

        var customerLookups = await GetCachedLookupAsync(
            "sales:lookups:customers:v1",
            () => _db.Customers
                .AsNoTracking()
                .Where(c => c.IsActive)
                .OrderBy(c => c.Name)
                .Select(c => new LookupOption(c.Id, c.Name))
                .ToListAsync());
        ViewBag.Customers = new SelectList(
            customerLookups,
            "Id",
            "Name",
            createModel?.CustomerId ?? filter?.CustomerId);

        var productLookups = await GetCachedLookupAsync(
            "sales:lookups:products:v1",
            () => _db.Products
                .AsNoTracking()
                .Where(p => p.IsActive)
                .OrderBy(p => p.Code)
                .Select(p => new LookupOption(p.Id, p.Name))
                .ToListAsync());
        ViewBag.Products = new SelectList(
            productLookups,
            "Id",
            "Name",
            createModel?.ProductId ?? filter?.ProductId);

        var destinationLookups = await GetCachedLookupAsync(
            "sales:lookups:destinations:v1",
            () => _db.Locations
                .AsNoTracking()
                .OrderBy(l => l.Name)
                .Select(l => new LookupOption(l.Id, l.Name))
                .ToListAsync());
        ViewBag.Destinations = new SelectList(
            destinationLookups,
            "Id",
            "Name",
            createModel?.DestinationLocationId);

        var shipments = await _db.Shipments
            .AsNoTracking()
            .OrderBy(s => selectedShipmentId.HasValue && s.Id == selectedShipmentId.Value ? 0 : 1)
            .ThenByDescending(s => s.DepartureDate)
            .ThenBy(s => s.ShipmentCode)
            .ThenByDescending(s => s.Id)
            .Take(LookupLimit)
            .Select(s => new { s.Id, s.ShipmentCode, s.ContractId, s.DestinationLocationId })
            .ToListAsync();
        ViewBag.ShipmentContractMap = shipments
            .Select(s => new { shipmentId = s.Id, contractId = s.ContractId, destinationLocationId = s.DestinationLocationId })
            .ToList();
        ViewBag.Shipments = shipments
            .Select(s => new SelectListItem
            {
                Value = s.Id.ToString(),
                Text = string.IsNullOrWhiteSpace(s.ShipmentCode)
                    ? $"Shipment #{s.Id}"
                    : s.ShipmentCode,
                Selected = createModel?.ShipmentId == s.Id
            })
            .ToList();

        var terminalLookups = await GetCachedLookupAsync(
            "sales:lookups:terminals:v1",
            () => _db.Terminals
                .AsNoTracking()
                .Where(t => t.IsActive)
                .OrderBy(t => t.Code)
                .Select(t => new LookupOption(t.Id, t.Name))
                .ToListAsync());
        ViewBag.SourceTerminals = new SelectList(
            terminalLookups,
            "Id",
            "Name",
            createModel?.SourceTerminalId);

        var tankLookups = await GetCachedLookupAsync(
            "sales:lookups:storage-tanks:v2",
            async () => (await StorageTankDisplay.LoadOptionsAsync(_db.StorageTanks
                    .AsNoTracking()
                    .OrderBy(t => t.DisplayName ?? t.TankCode)))
                .Select(t => new TankLookupOption(t.Id, t.Display))
                .ToList());
        ViewBag.SourceStorageTanks = new SelectList(
            tankLookups,
            "Id",
            "Display",
            createModel?.SourceStorageTankId);

        int? selectedProductId = createModel is not null && createModel.ProductId > 0
            ? createModel.ProductId
            : null;
        int? selectedCompanyId = createModel is not null && createModel.CompanyId > 0
            ? createModel.CompanyId
            : filter?.CompanyId;
        var selectedSourcePurchaseContractId = createModel?.SourcePurchaseContractId;
        var purchaseContractsQuery = _db.Contracts
            .AsNoTracking()
            .Where(c => c.ContractType == ContractType.Purchase)
            .Where(c => !selectedProductId.HasValue
                || c.ProductId == selectedProductId.Value
                || c.Id == selectedSourcePurchaseContractId)
            .Where(c => !selectedCompanyId.HasValue
                || c.CompanyId == selectedCompanyId.Value
                || c.Id == selectedSourcePurchaseContractId)
            .OrderBy(c => selectedSourcePurchaseContractId.HasValue && c.Id == selectedSourcePurchaseContractId.Value ? 0 : 1)
            .ThenByDescending(c => c.ContractDate)
            .ThenBy(c => c.ContractNumber)
            .Take(LookupLimit)
            .Select(c => new
            {
                c.Id,
                c.ContractNumber,
                c.ContractType,
                ProductName = c.Product != null ? c.Product.Name : null,
                UnitSymbol = c.Unit != null ? c.Unit.Symbol : null,
                UnitCode = c.Unit != null ? c.Unit.Code : null,
                UnitNamePersian = c.Unit != null ? c.Unit.NamePersian : null,
                UnitName = c.Unit != null ? c.Unit.Name : null,
                CompanyName = c.Company != null ? c.Company.Name : null
            });
        var purchaseContracts = await purchaseContractsQuery.ToListAsync();

        ViewBag.SourcePurchaseContracts = new SelectList(
            purchaseContracts
                .Select(c =>
                {
                    var display = ContractUiText.FormatLookup(
                        c.ContractNumber,
                        c.ContractType,
                        c.ProductName,
                        ContractUiText.ResolveUnitText(c.UnitSymbol, c.UnitCode, c.UnitNamePersian, c.UnitName));

                    if (!string.IsNullOrWhiteSpace(c.CompanyName))
                    {
                        display = $"{display} | شرکت: {c.CompanyName}";
                    }

                    return new ContractLookupOption(c.Id, display);
                })
                .ToList(),
            nameof(ContractLookupOption.Id),
            nameof(ContractLookupOption.Display),
            selectedSourcePurchaseContractId);

        var currencyLookups = await GetCachedLookupAsync(
            "sales:lookups:currencies:v1",
            () => _db.Currencies
                .AsNoTracking()
                .Where(c => c.IsActive)
                .OrderBy(c => c.Code)
                .Select(c => new CurrencyLookupOption(c.Code))
                .ToListAsync());
        ViewBag.Currencies = new SelectList(
            currencyLookups,
            "Code",
            "Code",
            createModel?.Currency);

        ViewBag.SaleStages = GetSaleStageItems(createModel?.SaleStage ?? SaleStage.TerminalStock);
        ViewBag.SaleLossStages = GetSaleLossStageItems(
            createModel?.Loss.Stage ?? ResolveDefaultLossStage(createModel?.SaleStage ?? SaleStage.TerminalStock));
    }

    private async Task<StorageTank?> LockStorageTankAsync(int storageTankId)
    {
        if (_db.Database.IsRelational()
            && string.Equals(_db.Database.ProviderName, "Npgsql.EntityFrameworkCore.PostgreSQL", StringComparison.Ordinal))
        {
            return await _db.StorageTanks
                .FromSqlInterpolated($@"SELECT * FROM ""StorageTanks"" WHERE ""Id"" = {storageTankId} FOR UPDATE")
                .AsNoTracking()
                .SingleOrDefaultAsync();
        }

        return await _db.StorageTanks
            .AsNoTracking()
            .SingleOrDefaultAsync(t => t.Id == storageTankId);
    }

    private async Task<IReadOnlyList<TerminalStockAllocation>> EnsureSufficientTerminalStockAsync(
        int productId,
        decimal quantityMt,
        DateTime saleDate,
        int terminalId,
        int storageTankId,
        int companyId,
        int sourcePurchaseContractId)
    {
        var preferredAvailable = await _stock.GetFreeQuantityMtAsync(
            productId,
            terminalId: terminalId,
            contractId: sourcePurchaseContractId,
            storageTankId: storageTankId,
            asOfUtc: saleDate);

        if (preferredAvailable >= quantityMt)
        {
            return [new TerminalStockAllocation(sourcePurchaseContractId, quantityMt)];
        }

        var balances = await GetTerminalStockSourceBalancesAsync(
            productId,
            terminalId,
            storageTankId,
            saleDate);

        var eligibleBalances = balances
            .Where(b => b.CompanyId == companyId && b.AvailableMt > 0m)
            .OrderBy(b => b.ContractId == sourcePurchaseContractId ? 0 : 1)
            .ThenBy(b => b.ContractDate)
            .ThenBy(b => b.ContractNumber)
            .ThenBy(b => b.ContractId)
            .ToList();

        var available = eligibleBalances.Sum(b => b.AvailableMt);
        if (available < quantityMt)
        {
            throw new BusinessRuleException(
                "SALE_TERMINAL_STOCK_INSUFFICIENT",
                $"موجودی کافی در مخزن انتخابی وجود ندارد. موجودی فعلی: {available:N4} MT، درخواست: {quantityMt:N4} MT.");
        }

        var remaining = quantityMt;
        var allocations = new List<TerminalStockAllocation>();
        foreach (var balance in eligibleBalances)
        {
            if (remaining <= 0m)
            {
                break;
            }

            var allocated = Math.Min(balance.AvailableMt, remaining);
            if (allocated <= 0m)
            {
                continue;
            }

            allocations.Add(new TerminalStockAllocation(balance.ContractId, allocated));
            remaining -= allocated;
        }

        return allocations;
    }

    private async Task<IReadOnlyList<TerminalStockSourceBalance>> GetTerminalStockSourceBalancesAsync(
        int productId,
        int terminalId,
        int storageTankId,
        DateTime saleDate)
    {
        var movementBalances = await _db.InventoryMovements
            .AsNoTracking()
            .Where(m => m.ProductId == productId
                && m.TerminalId == terminalId
                && m.StorageTankId == storageTankId
                && m.MovementDate <= saleDate)
            .Select(m => new
            {
                ContractId = m.ContractId
                    ?? (m.LoadingReceipt != null && m.LoadingReceipt.LoadingRegister != null
                        ? (int?)m.LoadingReceipt.LoadingRegister.ContractId
                        : null),
                m.Direction,
                m.QuantityMt
            })
            .Where(m => m.ContractId.HasValue)
            .GroupBy(m => m.ContractId!.Value)
            .Select(g => new
            {
                ContractId = g.Key,
                AvailableMt = g.Sum(m =>
                    m.Direction == MovementDirection.In || m.Direction == MovementDirection.Adjustment
                        ? m.QuantityMt
                        : m.Direction == MovementDirection.Out || m.Direction == MovementDirection.Transfer
                            ? -m.QuantityMt
                            : 0m)
            })
            .Where(b => b.AvailableMt > 0m)
            .ToListAsync();

        var contractIds = movementBalances.Select(b => b.ContractId).Distinct().ToArray();
        var contracts = await _db.Contracts
            .AsNoTracking()
            .Where(c => contractIds.Contains(c.Id) && c.ContractType == ContractType.Purchase)
            .Select(c => new
            {
                c.Id,
                c.CompanyId,
                c.ContractNumber,
                c.ContractDate
            })
            .ToDictionaryAsync(c => c.Id);

        return movementBalances
            .Where(b => contracts.ContainsKey(b.ContractId))
            .Select(b =>
            {
                var contract = contracts[b.ContractId];
                return new TerminalStockSourceBalance(
                    b.ContractId,
                    contract.CompanyId,
                    contract.ContractNumber,
                    contract.ContractDate,
                    b.AvailableMt);
            })
            .ToList();
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

    public async Task<IActionResult> Index([FromQuery] SalesIndexFilterViewModel? filter = null, int page = 1)
    {
        const int pageSize = 5;
        var exportAll = page <= 0;
        filter ??= new SalesIndexFilterViewModel();

        var query = _db.SalesTransactions
            .AsNoTracking()
            .AsQueryable();

        query = query.Where(s => !s.IsCancelled);

        if (filter.ContractId.HasValue)
        {
            var contractId = filter.ContractId.Value;
            query = query.Where(s =>
                s.ContractId == contractId
                || _db.LoadingReceiptAllocations.Any(a =>
                    a.SourcePurchaseContractId == contractId
                    && a.SalesTransactionId == s.Id)
                || _db.InventoryMovements.Any(m =>
                    m.Direction == MovementDirection.Out
                    && m.ContractId == contractId
                    && m.SalesTransactionId == s.Id)
                || _db.TruckDispatches.Any(d =>
                    d.ContractId == contractId
                    && d.SalesTransactionId == s.Id)
                || _db.InventoryTransportReceipts.Any(r =>
                    r.SalesTransactionId == s.Id
                    && !r.IsCancelled
                    && _db.InventoryTransportLegs.Any(l =>
                        l.Id == r.InventoryTransportLegId
                        && l.SourcePurchaseContractId == contractId)));
        }
        if (filter.CompanyId.HasValue) query = query.Where(s => s.CompanyId == filter.CompanyId.Value);
        if (filter.CustomerId.HasValue) query = query.Where(s => s.CustomerId == filter.CustomerId.Value);
        if (filter.ProductId.HasValue) query = query.Where(s => s.ProductId == filter.ProductId.Value);
        if (!string.IsNullOrWhiteSpace(filter.InvoiceNumber))
        {
            var invoice = filter.InvoiceNumber.Trim();
            query = query.Where(s => s.InvoiceNumber.Contains(invoice));
        }
        if (filter.FromDate.HasValue) query = query.Where(s => s.SaleDate >= filter.FromDate.Value);
        if (filter.ToDate.HasValue) query = query.Where(s => s.SaleDate <= filter.ToDate.Value);

        var totalCount = await query.CountAsync();
        var pageCount = Math.Max(1, (int)Math.Ceiling((double)totalCount / pageSize));
        page = Math.Clamp(page, 1, pageCount);

        await PopulateLookupsAsync(filter: filter);

        var items = await query
            .OrderByDescending(s => s.SaleDate)
            .ThenByDescending(s => s.Id)
            .Skip(exportAll ? 0 : (page - 1) * pageSize)
            .Take(exportAll ? totalCount : pageSize)
            .Select(s => new SalesListItemViewModel
            {
                Id = s.Id,
                SaleDate = s.SaleDate,
                SaleStage = s.SaleStage,
                InvoiceNumber = s.InvoiceNumber,
                ContractNumber = s.Contract != null ? s.Contract.ContractNumber : SalesContractText.WithoutSalesContract,
                // CompanyId=null یعنی فروش چندجوازی؛ «چند جواز» نمایش داده می‌شود نه خالی/«بدون جواز».
                CompanyName = s.CompanyId == null
                    ? SalesContractText.MultiLicense
                    : (s.Company != null ? s.Company.Name : ""),
                CustomerName = s.Customer != null ? s.Customer.Name : "",
                ProductName = s.Product != null ? s.Product.Name : "",
                DestinationName = s.DestinationLocation != null ? s.DestinationLocation.Name : null,
                QuantityMt = s.QuantityMt,
                Currency = s.Currency,
                UnitPriceInCurrency = s.UnitPriceInCurrency,
                TotalInCurrency = s.TotalInCurrency,
                AppliedFxRateToUsd = s.AppliedFxRateToUsd,
                UnitPriceUsd = s.UnitPriceUsd,
                TotalUsd = s.TotalUsd
            })
            .ToListAsync();

        // آمار کامل روی همهٔ رکوردهای مطابق فیلتر (نه فقط این صفحه) برای کارت‌های آماری و ردیف جمع.
        ViewBag.SumQuantity = await query.SumAsync(s => s.QuantityMt);
        ViewBag.SumTotalUsd = await query.SumAsync(s => s.TotalUsd);
        ViewBag.CustomerCount = await query
            .Where(s => s.CustomerId != null)
            .Select(s => s.CustomerId)
            .Distinct()
            .CountAsync();

        return View(new SalesIndexViewModel
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
        var sale = await _db.SalesTransactions
            .FirstOrDefaultAsync(s => s.Id == id);

        if (sale is null)
        {
            return NotFound();
        }

        if (sale.IsCancelled)
        {
            TempData["ok"] = "این فروش قبلاً لغو شده است.";
            if (!string.IsNullOrWhiteSpace(returnUrl) && Url?.IsLocalUrl(returnUrl) == true)
                return Redirect(returnUrl);
            return RedirectToAction(nameof(Details), new { id });
        }

        var originalLedger = await _db.LedgerEntries
            .AsNoTracking()
            .Where(l => l.SourceType == "Sale" && l.SourceId == sale.Id)
            .OrderByDescending(l => l.Id)
            .FirstOrDefaultAsync();

        if (originalLedger is null)
        {
            TempData["err"] = "سند مالی این فروش پیدا نشد؛ لغو انجام نشد.";
            if (!string.IsNullOrWhiteSpace(returnUrl) && Url?.IsLocalUrl(returnUrl) == true)
                return Redirect(returnUrl);
            return RedirectToAction(nameof(Details), new { id });
        }

        var stockOutMovement = await _db.InventoryMovements
            .AsNoTracking()
            .Where(m => m.SalesTransactionId == sale.Id && m.Direction == MovementDirection.Out)
            .OrderByDescending(m => m.Id)
            .FirstOrDefaultAsync();

        IDbContextTransaction? transaction = null;
        if (_db.Database.IsRelational())
        {
            transaction = await _db.Database.BeginTransactionAsync();
        }

        sale.IsCancelled = true;

        var reversalLedger = new LedgerEntry
        {
            EntryDate = DateTime.UtcNow.Date,
            Side = LedgerSide.Debit,
            AmountUsd = originalLedger.AmountUsd,
            Currency = originalLedger.Currency,
            SourceAmount = originalLedger.SourceAmount,
            SourceCurrencyCode = originalLedger.SourceCurrencyCode,
            AppliedFxRateToUsd = originalLedger.AppliedFxRateToUsd,
            AppliedFxRateDate = originalLedger.AppliedFxRateDate,
            AppliedFxRateSource = originalLedger.AppliedFxRateSource,
            Description = $"لغو فروش #{sale.Id} | {originalLedger.Description}",
            SourceType = "Sale",
            SourceId = sale.Id,
            Reference = (originalLedger.Reference ?? sale.InvoiceNumber) + "-CANCEL",
            ContractId = originalLedger.ContractId,
            CustomerId = originalLedger.CustomerId,
            ShipmentId = originalLedger.ShipmentId
        };
        _db.LedgerEntries.Add(reversalLedger);

        if (stockOutMovement is not null)
        {
            var reversalMovement = new InventoryMovement
            {
                ProductId = stockOutMovement.ProductId,
                ContractId = stockOutMovement.ContractId,
                TerminalId = stockOutMovement.TerminalId,
                StorageTankId = stockOutMovement.StorageTankId,
                SalesTransactionId = sale.Id,
                Direction = MovementDirection.In,
                MovementDate = DateTime.UtcNow.Date,
                QuantityMt = stockOutMovement.QuantityMt,
                ReferenceDocument = (stockOutMovement.ReferenceDocument ?? sale.InvoiceNumber) + "-CANCEL",
                Notes = $"Reversal for cancelled SaleId={sale.Id}"
            };
            _db.InventoryMovements.Add(reversalMovement);
        }

        try
        {
            await _db.SaveChangesAsync();
            await ReverseSaleAccountingAsync(sale);

            if (transaction is not null)
            {
                await transaction.CommitAsync();
            }
        }
        catch (Exception exception)
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync();
            }

            _logger.LogError(exception, "Failed to cancel sale {SaleId}.", sale.Id);
            TempData["err"] = "لغو فروش انجام نشد؛ برگشت اسناد حسابداری ناموفق بود و هیچ تغییری ثبت نشد.";

            if (!string.IsNullOrWhiteSpace(returnUrl) && Url?.IsLocalUrl(returnUrl) == true)
                return Redirect(returnUrl);

            return RedirectToAction(nameof(Details), new { id });
        }
        finally
        {
            if (transaction is not null)
            {
                await transaction.DisposeAsync();
            }
        }

        TempData["ok"] = "فروش لغو شد.";
        if (!string.IsNullOrWhiteSpace(returnUrl) && Url?.IsLocalUrl(returnUrl) == true)
            return Redirect(returnUrl);

        return RedirectToAction(nameof(Index));
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    public async Task<IActionResult> Create(int? contractId = null, int? sourcePurchaseContractId = null, string? returnUrl = null)
    {
        var model = new SalesCreateViewModel
        {
            SaleDate = DateTime.UtcNow.Date,
            SaleStage = SaleStage.TerminalStock,
            Currency = SystemCurrency.BaseCurrencyCode
        };

        if (contractId.HasValue)
        {
            var contract = await _db.Contracts
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == contractId.Value && c.ContractType == ContractType.Sale);
            if (contract is not null)
            {
                model.ContractId = contract.Id;
                model.CompanyId = contract.CompanyId;
                if (contract.CustomerId.HasValue)
                {
                    model.CustomerId = contract.CustomerId.Value;
                }

                model.ProductId = contract.ProductId;
                model.DestinationLocationId = contract.DestinationLocationId;
            }
        }

        if (sourcePurchaseContractId.HasValue)
        {
            var sourceContract = await _db.Contracts
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == sourcePurchaseContractId.Value && c.ContractType == ContractType.Purchase);
            if (sourceContract is not null)
            {
                model.SourcePurchaseContractId = sourceContract.Id;
                if (!model.ContractId.HasValue || model.ProductId <= 0)
                {
                    model.ProductId = sourceContract.ProductId;
                }

                if (!model.ContractId.HasValue || model.CompanyId <= 0)
                {
                    model.CompanyId = sourceContract.CompanyId;
                }
            }
        }

        model.ReturnUrl = returnUrl;

        await PopulateLookupsAsync(createModel: model);
        return View(model);
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpGet]
    public async Task<IActionResult> CreateFromShipment(int shipmentId, string? returnUrl = null)
    {
        var context = await BuildShipmentFlowSaleContextAsync(shipmentId);
        if (context is null)
        {
            return NotFound();
        }

        var model = context.Model;

        // محمولهٔ تک‌قراردادی: قرارداد منبع بدیهی است و پیش‌فرض ست می‌شود تا ردیابی جواز حفظ شود.
        if (context.ContractFacts.Count == 1)
        {
            model.SourcePurchaseContractId = context.ContractFacts.Keys.First();
        }

        model.QuantityMt = ResolveShipmentFlowAvailableMt(context, model.SourcePurchaseContractId);
        model.SaleDate = DateTime.UtcNow.Date;
        model.Currency = SystemCurrency.BaseCurrencyCode;
        model.InvoiceNumber = $"SHIP-{shipmentId}-{DateTime.UtcNow:yyyyMMddHHmmss}";
        model.ReturnUrl = NormalizeLocalReturnUrl(returnUrl);
        await PopulateShipmentFlowSaleLookupsAsync(model);
        return View(model);
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateFromShipment(ShipmentFlowSaleCreateViewModel model)
    {
        var context = await BuildShipmentFlowSaleContextAsync(model.ShipmentId);
        if (context is null)
        {
            return NotFound();
        }

        ApplyShipmentFlowSaleDisplay(model, context.Model);
        model.Currency = SystemCurrency.Normalize(model.Currency);
        model.InvoiceNumber = model.InvoiceNumber?.Trim() ?? string.Empty;
        model.Notes = string.IsNullOrWhiteSpace(model.Notes) ? null : model.Notes.Trim();
        model.ReturnUrl = NormalizeLocalReturnUrl(model.ReturnUrl);

        // قرارداد منبع اختیاری است و فقط برای فروشِ واقعاً تک‌قراردادی معنا دارد. فروش کلی از محموله‌ای
        // با چند قرارداد (حتی با جواز/تأمین‌کنندهٔ متفاوت) مجاز است و با همان تقسیم وزنیِ موجود بین
        // قراردادها سرشکن می‌شود؛ جواز فقط برچسب قرارداد است، نه شرط فروش.
        if (context.ContractFacts.Count == 1)
        {
            model.SourcePurchaseContractId ??= context.ContractFacts.Keys.First();
        }

        ShipmentFlowSaleContractFacts? sourceContract = null;
        if (model.SourcePurchaseContractId.HasValue
            && !context.ContractFacts.TryGetValue(model.SourcePurchaseContractId.Value, out sourceContract))
        {
            ModelState.AddModelError(
                nameof(model.SourcePurchaseContractId),
                "قرارداد منبع انتخاب‌شده جزو قراردادهای بارگیری‌شدهٔ این محموله نیست.");
        }

        // جواز فروش فقط وقتی معنا دارد که قرارداد منبع مشخص باشد یا همهٔ قراردادهای محموله یک جواز
        // داشته باشند. در فروش چندجوازی هیچ جواز ساختگی ثبت نمی‌شود و مقدار null می‌ماند.
        int? saleCompanyId = sourceContract?.CompanyId ?? (context.CompanyId > 0 ? context.CompanyId : null);
        var saleProductId = sourceContract?.ProductId ?? context.ProductId;
        var availableQuantityMt = sourceContract?.AvailableQuantityMt ?? context.Model.AvailableQuantityMt;

        if (sourceContract is null && saleProductId <= 0)
        {
            ModelState.AddModelError(
                nameof(model.SourcePurchaseContractId),
                "قراردادهای این محموله محصول یکسان ندارند؛ برای فروش، قرارداد منبع بار را انتخاب کنید.");
        }

        if (model.QuantityMt <= 0m)
        {
            ModelState.AddModelError(nameof(model.QuantityMt), "مقدار فروش باید بزرگ‌تر از صفر باشد.");
        }

        if (model.QuantityMt > availableQuantityMt + 0.0001m)
        {
            ModelState.AddModelError(nameof(model.QuantityMt),
                $"مقدار فروش نمی‌تواند از مقدار قابل فروش ({availableQuantityMt:N4} تن) بیشتر باشد.");
        }

        if (model.UnitPriceInCurrency <= 0m)
        {
            ModelState.AddModelError(nameof(model.UnitPriceInCurrency), "قیمت هر تن باید بزرگ‌تر از صفر باشد.");
        }

        if (model.SaleDate == default)
        {
            ModelState.AddModelError(nameof(model.SaleDate), "تاریخ فروش الزامی است.");
        }

        if (!await _db.Customers.AsNoTracking().AnyAsync(c => c.Id == model.CustomerId && c.IsActive))
        {
            ModelState.AddModelError(nameof(model.CustomerId), "مشتری انتخاب‌شده معتبر نیست.");
        }

        if (model.DestinationLocationId.HasValue
            && !await _db.Locations.AsNoTracking().AnyAsync(l => l.Id == model.DestinationLocationId.Value))
        {
            ModelState.AddModelError(nameof(model.DestinationLocationId), "مقصد انتخاب‌شده معتبر نیست.");
        }

        if (string.IsNullOrWhiteSpace(model.InvoiceNumber))
        {
            ModelState.AddModelError(nameof(model.InvoiceNumber), "شماره فاکتور الزامی است.");
        }
        else if (await _db.SalesTransactions.AsNoTracking().AnyAsync(s => s.InvoiceNumber == model.InvoiceNumber))
        {
            ModelState.AddModelError(nameof(model.InvoiceNumber), "این شماره فاکتور قبلاً ثبت شده است.");
        }

        var hasActiveCurrencies = await _db.Currencies.AsNoTracking().AnyAsync(c => c.IsActive);
        if (hasActiveCurrencies
            && !await _db.Currencies.AsNoTracking().AnyAsync(c => c.Code == model.Currency && c.IsActive))
        {
            ModelState.AddModelError(nameof(model.Currency), "ارز انتخاب‌شده معتبر نیست.");
        }

        if (!ModelState.IsValid)
        {
            await PopulateShipmentFlowSaleLookupsAsync(model);
            return View(model);
        }

        CurrencyConversionResult conversion;
        try
        {
            conversion = await _currencyConversion.ResolveToBaseAsync(
                model.Currency,
                model.SaleDate.Date,
                model.AppliedFxRateToUsd);
        }
        catch (BusinessRuleException ex)
        {
            ModelState.AddModelError(nameof(model.AppliedFxRateToUsd), ex.Message);
            await PopulateShipmentFlowSaleLookupsAsync(model);
            return View(model);
        }

        IDbContextTransaction? transaction = null;
        try
        {
            if (_db.Database.IsRelational())
            {
                transaction = await _db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
            }

            // بازخوانی سقف با همان قرارداد منبع؛ مقدار قابل فروشِ همان قرارداد ملاک است، نه کل محموله.
            var latestContext = await BuildShipmentFlowSaleContextAsync(model.ShipmentId);
            var latestAvailableMt = latestContext is null
                ? 0m
                : ResolveShipmentFlowAvailableMt(latestContext, model.SourcePurchaseContractId);
            if (latestContext is null || model.QuantityMt > latestAvailableMt + 0.0001m)
            {
                throw new BusinessRuleException(
                    "SHIPMENT_SALE_QUANTITY_EXCEEDED",
                    $"مقدار قابل فروش محموله تغییر کرده است. مقدار فعلی: {latestAvailableMt:N4} تن.");
            }

            // جواز/شرکت و محصولِ فروش از قرارداد منبع می‌آید؛ اگر قرارداد منبع نداریم (محمولهٔ یکنواخت
            // یا رفتار قدیمی) همان مقدار سطح‌محموله استفاده می‌شود.
            var latestSourceContract = model.SourcePurchaseContractId.HasValue
                && latestContext.ContractFacts.TryGetValue(model.SourcePurchaseContractId.Value, out var latestFacts)
                    ? latestFacts
                    : null;

            var totalInCurrency = decimal.Round(
                model.QuantityMt * model.UnitPriceInCurrency,
                4,
                MidpointRounding.AwayFromZero);
            var sale = new SalesTransaction
            {
                ContractId = null,
                SourcePurchaseContractId = latestSourceContract?.ContractId,
                // فروش چندجوازی: جواز واحدی وجود ندارد و null ثبت می‌شود (هیچ جواز ساختگی).
                CompanyId = latestSourceContract?.CompanyId
                    ?? (latestContext.CompanyId > 0 ? latestContext.CompanyId : null),
                CustomerId = model.CustomerId,
                ProductId = latestSourceContract?.ProductId ?? latestContext.ProductId,
                DestinationLocationId = model.DestinationLocationId ?? latestContext.DestinationLocationId,
                ShipmentId = model.ShipmentId,
                SaleStage = SaleStage.InTransit,
                InvoiceNumber = model.InvoiceNumber,
                SaleDate = model.SaleDate.Date,
                QuantityMt = model.QuantityMt,
                Currency = conversion.SourceCurrencyCode,
                UnitPriceInCurrency = model.UnitPriceInCurrency,
                AppliedFxRateToUsd = conversion.AppliedRateToBase,
                UnitPriceUsd = conversion.ConvertToBase(model.UnitPriceInCurrency),
                TotalInCurrency = totalInCurrency,
                TotalUsd = conversion.ConvertToBase(totalInCurrency),
                Notes = model.Notes
            };
            _db.SalesTransactions.Add(sale);
            await _db.SaveChangesAsync();

            var ledgerEntry = SaleLedgerFactory.BuildSaleLedgerEntry(sale, conversion, contractId: null);
            _db.LedgerEntries.Add(ledgerEntry);
            await _db.SaveChangesAsync();

            await PostSaleAccountingAsync(sale);

            await _audit.LogAsync(
                nameof(SalesTransaction),
                sale.Id,
                AuditAction.Insert,
                diff: AuditDiffFormatter.ForCreate(
                    ("CompanyId", sale.CompanyId),
                    ("CustomerId", sale.CustomerId),
                    ("ProductId", sale.ProductId),
                    ("ShipmentId", sale.ShipmentId),
                    ("SourcePurchaseContractId", sale.SourcePurchaseContractId),
                    ("SaleStage", sale.SaleStage),
                    ("InvoiceNumber", sale.InvoiceNumber),
                    ("QuantityMt", sale.QuantityMt),
                    ("TotalUsd", sale.TotalUsd),
                    ("LedgerReference", ledgerEntry.Reference)));

            if (transaction is not null)
            {
                await transaction.CommitAsync();
            }

            TempData["ok"] = "فروش از جریان حمل محموله با موفقیت ثبت شد.";
            if (model.PrintAfterSave)
            {
                return RedirectToAction("Sale", "Invoices", new { id = sale.Id });
            }

            if (!string.IsNullOrWhiteSpace(model.ReturnUrl))
            {
                return Redirect(model.ReturnUrl);
            }

            return RedirectToAction("Details", "ShipmentPnl", new { id = model.ShipmentId });
        }
        catch (BusinessRuleException ex)
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync();
            }

            var latest = await BuildShipmentFlowSaleContextAsync(model.ShipmentId);
            if (latest is not null)
            {
                ApplyShipmentFlowSaleDisplay(model, latest.Model);
            }
            ModelState.AddModelError(nameof(model.QuantityMt), ex.Message);
            await PopulateShipmentFlowSaleLookupsAsync(model);
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

    [Authorize(Policy = AuthPolicies.ManageData)]
    public async Task<IActionResult> Edit(int id, string? returnUrl = null)
    {
        var sale = await _db.SalesTransactions
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == id);

        if (sale is null)
        {
            return NotFound();
        }

        if (sale.IsCancelled)
        {
            TempData["err"] = "این فروش لغو شده است و قابل ویرایش نیست.";
            if (!string.IsNullOrWhiteSpace(returnUrl) && Url?.IsLocalUrl(returnUrl) == true)
                return Redirect(returnUrl);
            return RedirectToAction(nameof(Details), new { id });
        }

        var model = new SalesCreateViewModel
        {
            SaleStage = sale.SaleStage,
            ContractId = sale.ContractId,
            // فروش‌های چندجوازیِ محموله جواز واحد ندارند؛ فرم ویرایش انتخاب جواز را اجباری می‌کند.
            CompanyId = sale.CompanyId ?? 0,
            CustomerId = sale.CustomerId,
            ProductId = sale.ProductId,
            DestinationLocationId = sale.DestinationLocationId,
            ShipmentId = sale.ShipmentId,
            QuantityMt = sale.QuantityMt,
            Currency = sale.Currency,
            UnitPriceInCurrency = sale.UnitPriceInCurrency,
            AppliedFxRateToUsd = sale.AppliedFxRateToUsd,
            InvoiceNumber = sale.InvoiceNumber,
            SaleDate = sale.SaleDate,
            Notes = sale.Notes,
            // Gap #4 — show existing values as context (edit POST only allows Notes changes)
            TicketSerialNumber = sale.TicketSerialNumber,
            StockSourceType = sale.StockSourceType,
            ReturnUrl = returnUrl
        };

        ViewData["IsEdit"] = true;
        ViewData["EditId"] = id;

        await PopulateLookupsAsync(createModel: model);
        return View("Create", model);
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, SalesCreateViewModel model)
    {
        var sale = await _db.SalesTransactions
            .FirstOrDefaultAsync(s => s.Id == id);

        if (sale is null)
        {
            return NotFound();
        }

        if (sale.IsCancelled)
        {
            ModelState.AddModelError(string.Empty, "این فروش لغو شده است و قابل ویرایش نیست.");
        }

        if (model.CompanyId != sale.CompanyId
            || model.CustomerId != sale.CustomerId
            || model.ProductId != sale.ProductId
            || model.ContractId != sale.ContractId
            || model.DestinationLocationId != sale.DestinationLocationId
            || model.ShipmentId != sale.ShipmentId
            || model.SaleStage != sale.SaleStage
            || model.SaleDate.Date != sale.SaleDate.Date
            || model.InvoiceNumber != sale.InvoiceNumber
            || model.Currency != sale.Currency
            || model.QuantityMt != sale.QuantityMt
            || model.UnitPriceInCurrency != sale.UnitPriceInCurrency
            || model.AppliedFxRateToUsd != sale.AppliedFxRateToUsd)
        {
            ModelState.AddModelError(string.Empty, "در نسخه فعلی فقط ویرایش یادداشت فروش مجاز است.");
        }

        if (!ModelState.IsValid)
        {
            ViewData["IsEdit"] = true;
            ViewData["EditId"] = id;
            await PopulateLookupsAsync(createModel: model);
            return View("Create", model);
        }

        sale.Notes = string.IsNullOrWhiteSpace(model.Notes) ? null : model.Notes.Trim();
        await _db.SaveChangesAsync();

        TempData["ok"] = "یادداشت فروش به‌روزرسانی شد.";
        if (TryGetLocalReturnUrl(model.ReturnUrl, out var localReturnUrl))
        {
            return Redirect(localReturnUrl);
        }

        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpGet]
    public async Task<IActionResult> SuggestedPrice(int? sourcePurchaseContractId)
    {
        if (!sourcePurchaseContractId.HasValue || sourcePurchaseContractId <= 0)
        {
            return Json(new
            {
                ok = false,
                finalUnitPrice = (decimal?)null,
                formulaText = string.Empty,
                needsReview = true,
                reason = "قرارداد انتخاب نشده",
                fallbackApplied = false
            });
        }

        var contract = await _db.Contracts
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == sourcePurchaseContractId.Value);

        if (contract is null)
        {
            return Json(new
            {
                ok = false,
                finalUnitPrice = (decimal?)null,
                formulaText = string.Empty,
                needsReview = true,
                reason = "قرارداد یافت نشد",
                fallbackApplied = false
            });
        }

        var result = await _pricing.CalculateContractPriceAsync(contract.Id);

        return Json(new
        {
            ok = result.FinalUnitPrice.HasValue,
            finalUnitPrice = result.FinalUnitPrice,
            formulaText = result.FormulaText,
            needsReview = result.NeedsReview,
            reason = result.Reason,
            fallbackApplied = result.FallbackApplied
        });
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpGet]
    public async Task<IActionResult> SourceStockBalance(
        int? productId,
        int? sourcePurchaseContractId,
        int? sourceTerminalId,
        int? sourceStorageTankId,
        DateTime? saleDate,
        CancellationToken ct)
    {
        if (!productId.HasValue || productId.Value <= 0
            || !sourcePurchaseContractId.HasValue || sourcePurchaseContractId.Value <= 0
            || !sourceTerminalId.HasValue || sourceTerminalId.Value <= 0
            || !sourceStorageTankId.HasValue || sourceStorageTankId.Value <= 0)
        {
            return Json(new
            {
                ok = false,
                availableMt = (decimal?)null,
                message = "برای نمایش موجودی، قرارداد خرید، ترمینال و مخزن را انتخاب کنید."
            });
        }

        var available = await _stock.GetFreeQuantityMtAsync(
            productId.Value,
            terminalId: sourceTerminalId.Value,
            contractId: sourcePurchaseContractId.Value,
            storageTankId: sourceStorageTankId.Value,
            asOfUtc: (saleDate ?? DateTime.UtcNow).Date,
            ct: ct);

        return Json(new
        {
            ok = true,
            availableMt = available,
            message = string.Empty
        });
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(
        SalesCreateViewModel model,
        [FromForm(Name = FormTokenHtmlHelper.FieldName)] string? formToken = null)
    {
        NormalizeCreateModel(model);
        var normalizedInvoice = model.InvoiceNumber?.Trim() ?? string.Empty;
        var normalizedNotes = string.IsNullOrWhiteSpace(model.Notes) ? null : model.Notes.Trim();
        var requiresTerminalStock = RequiresTerminalStock(model.SaleStage);

        Contract? sourcePurchaseContract = null;
        if (requiresTerminalStock && model.SourcePurchaseContractId is > 0)
        {
            sourcePurchaseContract = await _db.Contracts
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == model.SourcePurchaseContractId.Value);

            if (sourcePurchaseContract is { ContractType: ContractType.Purchase }
                && !model.ContractId.HasValue)
            {
                if (model.CompanyId != sourcePurchaseContract.CompanyId)
                {
                    model.CompanyId = sourcePurchaseContract.CompanyId;
                    ModelState.Remove(nameof(model.CompanyId));
                }

                if (model.ProductId != sourcePurchaseContract.ProductId)
                {
                    model.ProductId = sourcePurchaseContract.ProductId;
                    ModelState.Remove(nameof(model.ProductId));
                }
            }
        }

        var company = await _db.Companies.AsNoTracking().FirstOrDefaultAsync(c => c.Id == model.CompanyId && c.IsActive);
        if (company is null)
        {
            ModelState.AddModelError(nameof(model.CompanyId), "شرکت انتخاب‌شده معتبر نیست.");
        }

        var customer = await _db.Customers.AsNoTracking().FirstOrDefaultAsync(c => c.Id == model.CustomerId && c.IsActive);
        if (customer is null)
        {
            ModelState.AddModelError(nameof(model.CustomerId), "مشتری انتخاب‌شده معتبر نیست.");
        }

        var product = await _db.Products.AsNoTracking().FirstOrDefaultAsync(p => p.Id == model.ProductId && p.IsActive);
        if (product is null)
        {
            ModelState.AddModelError(nameof(model.ProductId), "کالای انتخاب‌شده معتبر نیست.");
        }

        var hasActiveCurrencies = await _db.Currencies.AsNoTracking().AnyAsync(c => c.IsActive);
        if (hasActiveCurrencies
            && !await _db.Currencies.AsNoTracking().AnyAsync(c => c.Code == model.Currency && c.IsActive))
        {
            ModelState.AddModelError(nameof(model.Currency), "Invalid currency selection.");
        }

        if (requiresTerminalStock)
        {
            if (!model.SourceTerminalId.HasValue)
            {
                ModelState.AddModelError(nameof(model.SourceTerminalId), "برای فروش از مخزن، انتخاب ترمینال مبدا الزامی است.");
            }

            if (!model.SourceStorageTankId.HasValue)
            {
                ModelState.AddModelError(nameof(model.SourceStorageTankId), "برای فروش از مخزن، انتخاب مخزن مبدا الزامی است.");
            }

            if (!model.SourcePurchaseContractId.HasValue)
            {
                ModelState.AddModelError(nameof(model.SourcePurchaseContractId), "برای فروش از مخزن، انتخاب قرارداد خرید منبع موجودی الزامی است.");
            }

            var sourceTerminal = model.SourceTerminalId.HasValue
                ? await _db.Terminals.AsNoTracking().FirstOrDefaultAsync(t => t.Id == model.SourceTerminalId.Value && t.IsActive)
                : null;
            if (model.SourceTerminalId.HasValue && sourceTerminal is null)
            {
                ModelState.AddModelError(nameof(model.SourceTerminalId), "ترمینال مبدا‌ی انتخاب‌شده معتبر نیست.");
            }

            if (model.SourceStorageTankId.HasValue)
            {
                var sourceTank = await _db.StorageTanks.AsNoTracking().FirstOrDefaultAsync(t => t.Id == model.SourceStorageTankId.Value);
                if (sourceTank is null)
                {
                    ModelState.AddModelError(nameof(model.SourceStorageTankId), "مخزن مبدا‌ی انتخاب‌شده معتبر نیست.");
                }
                else
                {
                    if (model.SourceTerminalId.HasValue && sourceTank.TerminalId != model.SourceTerminalId.Value)
                    {
                        ModelState.AddModelError(nameof(model.SourceStorageTankId), "مخزن مبدا به ترمینال انتخابی تعلق ندارد.");
                    }

                    if (sourceTank.ProductId.HasValue && sourceTank.ProductId != model.ProductId)
                    {
                        ModelState.AddModelError(nameof(model.SourceStorageTankId), "مخزن مبدا برای کالای انتخابی تعریف نشده است.");
                    }
                }
            }
        }

        Contract? contract = null;
        if (model.ContractId is > 0)
        {
            contract = await _db.Contracts.AsNoTracking().FirstOrDefaultAsync(c => c.Id == model.ContractId.Value);
            if (contract is null || contract.ContractType != ContractType.Sale)
            {
                ModelState.AddModelError(nameof(model.ContractId), "قرارداد فروش انتخاب‌شده معتبر نیست.");
            }
            else
            {
                if (!contract.CustomerId.HasValue)
                {
                    ModelState.AddModelError(nameof(model.ContractId), "قرارداد فروش انتخاب‌شده مشتری معتبر ندارد.");
                }
                else if (contract.CustomerId.Value != model.CustomerId)
                {
                    ModelState.AddModelError(nameof(model.ContractId), "قرارداد انتخاب‌شده با مشتری انتخابی هم‌خوان نیست.");
                }

                if (contract.ProductId != model.ProductId)
                {
                    ModelState.AddModelError(nameof(model.ContractId), "قرارداد انتخاب‌شده با کالای انتخابی هم‌خوان نیست.");
                }

                if (contract.CompanyId != model.CompanyId)
                {
                    ModelState.AddModelError(nameof(model.ContractId), "قرارداد انتخاب‌شده با شرکت انتخابی هم‌خوان نیست.");
                }

                if (contract.DestinationLocationId.HasValue && contract.DestinationLocationId != model.DestinationLocationId)
                {
                    ModelState.AddModelError(nameof(model.DestinationLocationId), "مقصد انتخابی باید با مقصد قرارداد فروش یکسان باشد.");
                }
            }
        }
        else if (model.ContractId.HasValue)
        {
            ModelState.AddModelError(nameof(model.ContractId), "قرارداد فروش انتخاب‌شده معتبر نیست.");
        }

        if (requiresTerminalStock && model.SourcePurchaseContractId.HasValue)
        {
            sourcePurchaseContract ??= await _db.Contracts
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == model.SourcePurchaseContractId.Value);
            if (sourcePurchaseContract is null || sourcePurchaseContract.ContractType != ContractType.Purchase)
            {
                ModelState.AddModelError(nameof(model.SourcePurchaseContractId), "قرارداد خرید منبع موجودی انتخاب‌شده معتبر نیست.");
            }
            else
            {
                if (sourcePurchaseContract.ProductId != model.ProductId)
                {
                    ModelState.AddModelError(nameof(model.SourcePurchaseContractId), "قرارداد خرید منبع موجودی با کالای انتخابی هم‌خوان نیست.");
                }

                if (sourcePurchaseContract.CompanyId != model.CompanyId)
                {
                    ModelState.AddModelError(nameof(model.SourcePurchaseContractId), "قرارداد خرید منبع موجودی با شرکت انتخابی هم‌خوان نیست.");
                }
            }
        }

        if (model.SaleStage == SaleStage.PreSale && !model.ContractId.HasValue)
        {
            ModelState.AddModelError(nameof(model.ContractId), "برای پیش‌فروش، انتخاب قرارداد الزامی است.");
        }

        if (model.ShipmentId.HasValue)
        {
            // قرارداد فروش برای فروشِ مبتنی بر Shipment اجباری نیست؛ فروش مستقیم از محموله بدون قرارداد
            // فروش مجاز است (هم‌راستا با CreateFromShipment). فقط اگر قرارداد فروش انتخاب شده باشد و محموله
            // هم قرارداد صریح داشته باشد، این دو باید هم‌خوان باشند.
            var shipment = await _db.Shipments.AsNoTracking().FirstOrDefaultAsync(s => s.Id == model.ShipmentId.Value);
            if (shipment is null)
            {
                ModelState.AddModelError(nameof(model.ShipmentId), "Shipment انتخاب‌شده معتبر نیست.");
            }
            else
            {
                if (model.ContractId.HasValue
                    && shipment.ContractId.HasValue
                    && shipment.ContractId.Value != model.ContractId.Value)
                {
                    ModelState.AddModelError(nameof(model.ShipmentId), "Shipment انتخاب‌شده با قرارداد فروش هم‌خوان نیست.");
                }

                // فروشِ مبتنی بر Shipment باید محصولش با محصول واقعی محموله و قرارداد خرید منبع هم‌خوان باشد.
                // پیش‌فروش تعهدی است و از این قید مستثناست. اگر محموله قرارداد بار ثبت‌شده نداشته باشد
                // (داده‌ی قدیمی)، اعتبارسنجی موجودِ قرارداد منبع کافی است و اینجا چیزی اضافه نمی‌شود.
                if (model.SaleStage != SaleStage.PreSale)
                {
                    var shipmentContracts = await _db.ShipmentContracts
                        .AsNoTracking()
                        .Where(sc => sc.ShipmentId == model.ShipmentId.Value && sc.Contract != null)
                        .Select(sc => new { sc.ContractId, sc.Contract!.ProductId })
                        .ToListAsync();

                    if (shipmentContracts.Count > 0)
                    {
                        var shipmentProductIds = shipmentContracts.Select(x => x.ProductId).Distinct().ToList();

                        if (model.SourcePurchaseContractId is > 0)
                        {
                            var matchedSource = shipmentContracts
                                .FirstOrDefault(x => x.ContractId == model.SourcePurchaseContractId.Value);
                            if (matchedSource is null)
                            {
                                ModelState.AddModelError(
                                    nameof(model.SourcePurchaseContractId),
                                    "قرارداد خرید منبع باید یکی از قراردادهای بارگیری‌شدهٔ همین محموله باشد.");
                            }
                            else if (matchedSource.ProductId != model.ProductId)
                            {
                                ModelState.AddModelError(
                                    nameof(model.ProductId),
                                    "کالای انتخاب‌شده با محصول قرارداد خرید منبعِ این محموله هم‌خوان نیست.");
                            }
                        }
                        else if (shipmentProductIds.Count > 1)
                        {
                            ModelState.AddModelError(
                                nameof(model.SourcePurchaseContractId),
                                "این محموله چند محصول دارد؛ برای فروش، انتخاب قرارداد خرید منبع الزامی است.");
                        }
                        else if (!shipmentProductIds.Contains(model.ProductId))
                        {
                            ModelState.AddModelError(
                                nameof(model.ProductId),
                                "کالای انتخاب‌شده با محصول این محموله هم‌خوان نیست.");
                        }
                    }
                }
            }
        }

        if (model.DestinationLocationId.HasValue)
        {
            var destinationExists = await _db.Locations.AsNoTracking().AnyAsync(l => l.Id == model.DestinationLocationId.Value);
            if (!destinationExists)
            {
                ModelState.AddModelError(nameof(model.DestinationLocationId), "مقصد انتخاب‌شده معتبر نیست.");
            }
        }

        if (string.IsNullOrWhiteSpace(normalizedInvoice))
        {
            ModelState.AddModelError(nameof(model.InvoiceNumber), "شماره فاکتور الزامی است.");
        }
        else if (await _db.SalesTransactions.AsNoTracking().AnyAsync(s => s.InvoiceNumber == normalizedInvoice))
        {
            ModelState.AddModelError(nameof(model.InvoiceNumber), "این شماره فاکتور قبلاً ثبت شده است.");
        }

        var allowedLossStages = GetAllowedSaleLossStages();
        StageLossCaptureMapper.Validate(
            model.Loss,
            (field, error) => ModelState.AddModelError(BuildLossFieldKey(field), error),
            allowedLossStages);

        if (model.Loss.Enabled)
        {
            await _lossWorkflow.ValidateAsync(
                BuildSaleLossSubmission(model, normalizedInvoice),
                (field, error) => ModelState.AddModelError(BuildLossFieldKey(field), error));
        }

        if (!ModelState.IsValid)
        {
            model.InvoiceNumber = normalizedInvoice;
            model.Notes = normalizedNotes;
            await PopulateLookupsAsync(createModel: model);
            return View(model);
        }

        CurrencyConversionResult conversion;
        try
        {
            conversion = await _currencyConversion.ResolveToBaseAsync(
                model.Currency,
                model.SaleDate.Date,
                model.AppliedFxRateToUsd);
        }
        catch (BusinessRuleException ex)
        {
            ModelState.AddModelError(nameof(model.AppliedFxRateToUsd), ex.Message);
            model.InvoiceNumber = normalizedInvoice;
            model.Notes = normalizedNotes;
            await PopulateLookupsAsync(createModel: model);
            return View(model);
        }

        var totalInCurrency = decimal.Round(model.QuantityMt * model.UnitPriceInCurrency, 4, MidpointRounding.AwayFromZero);
        var unitPriceUsd = conversion.ConvertToBase(model.UnitPriceInCurrency);
        var totalUsd = conversion.ConvertToBase(totalInCurrency);

        var sale = new SalesTransaction
        {
            ContractId = model.ContractId,
            CompanyId = model.CompanyId,
            CustomerId = model.CustomerId,
            ProductId = model.ProductId,
            DestinationLocationId = model.DestinationLocationId,
            ShipmentId = model.ShipmentId,
            SaleStage = model.SaleStage,
            InvoiceNumber = normalizedInvoice,
            SaleDate = model.SaleDate,
            QuantityMt = model.QuantityMt,
            Currency = conversion.SourceCurrencyCode,
            UnitPriceInCurrency = model.UnitPriceInCurrency,
            AppliedFxRateToUsd = conversion.AppliedRateToBase,
            UnitPriceUsd = unitPriceUsd,
            TotalInCurrency = totalInCurrency,
            TotalUsd = totalUsd,
            Notes = normalizedNotes,
            TicketSerialNumber = string.IsNullOrWhiteSpace(model.TicketSerialNumber) ? null : model.TicketSerialNumber.Trim(),
            StockSourceType = model.StockSourceType
        };

        // Duplicate-submit guard: token persists atomically with the sale (inside
        // the transaction below), so a second submit is rejected, not duplicated.
        _formTokens.Stamp(formToken, "Sale.Create", nameof(SalesTransaction));

        IReadOnlyList<TerminalStockAllocation> stockAllocations = [];

        try
        {
            if (requiresTerminalStock)
            {
                stockAllocations = await EnsureSufficientTerminalStockAsync(
                    sale.ProductId,
                    sale.QuantityMt,
                    sale.SaleDate,
                    model.SourceTerminalId!.Value,
                    model.SourceStorageTankId!.Value,
                    model.CompanyId,
                    model.SourcePurchaseContractId!.Value);

            }

            IDbContextTransaction? transaction = null;
            if (_db.Database.IsRelational())
            {
                transaction = await _db.Database.BeginTransactionAsync();
            }

            try
            {
                var stockOutMovements = new List<InventoryMovement>();
                if (requiresTerminalStock)
                {
                    if (!model.SourceStorageTankId.HasValue)
                    {
                        throw new BusinessRuleException(
                            "SALE_TERMINAL_STOCK_TANK_REQUIRED",
                            "برای فروش از مخزن، انتخاب مخزن مبدا الزامی است.");
                    }

                    var lockedTank = await LockStorageTankAsync(model.SourceStorageTankId.Value);
                    if (lockedTank is null)
                    {
                        throw new BusinessRuleException(
                            "SALE_TERMINAL_STOCK_TANK_NOT_FOUND",
                            "مخزن مبدا انتخاب‌شده دیگر معتبر نیست.");
                    }

                    stockAllocations = await EnsureSufficientTerminalStockAsync(
                        sale.ProductId,
                        sale.QuantityMt,
                        sale.SaleDate,
                        model.SourceTerminalId!.Value,
                        model.SourceStorageTankId.Value,
                        model.CompanyId,
                        model.SourcePurchaseContractId!.Value);
                }

                _db.SalesTransactions.Add(sale);
                await _db.SaveChangesAsync();

                if (requiresTerminalStock)
                {
                    foreach (var allocation in stockAllocations)
                    {
                        stockOutMovements.Add(new InventoryMovement
                        {
                            ProductId = sale.ProductId,
                            ContractId = allocation.ContractId,
                            TerminalId = model.SourceTerminalId!.Value,
                            StorageTankId = model.SourceStorageTankId,
                            SalesTransactionId = sale.Id,
                            Direction = MovementDirection.Out,
                            MovementDate = sale.SaleDate,
                            QuantityMt = allocation.QuantityMt,
                            ReferenceDocument = sale.InvoiceNumber,
                            Notes = BuildSaleInventoryNotes(
                                sale.SaleStage,
                                sale.InvoiceNumber,
                                $"SaleId={sale.Id}" + (string.IsNullOrWhiteSpace(sale.Notes) ? string.Empty : $" | {sale.Notes}"))
                        });
                    }

                    // Forward-pass: a backdated SaleDate must not leave any later
                    // running balance negative.
                    foreach (var stockOutMovement in stockOutMovements)
                    {
                        await _stock.EnsureMovementDoesNotCauseFutureNegativeStockAsync(stockOutMovement);
                    }

                    _db.InventoryMovements.AddRange(stockOutMovements);
                    await _db.SaveChangesAsync();

                    // لایهٔ Lineage: تخصیص فروش به Lotها با FIFO (پشت flag Lineage:WriteLots؛ خاموش=no-op).
                    // موجودی فیزیکی و Ledger دست‌نخورده می‌ماند؛ فقط SaleLotAllocation نوشته می‌شود.
                    await _lineage.AllocateSaleAsync(
                        sale,
                        model.SourcePurchaseContractId,
                        model.SourceTerminalId!.Value,
                        model.SourceStorageTankId);
                }

                var hasEmbeddedLoss = model.Loss.Enabled && model.Loss.QuantityMt.GetValueOrDefault() > 0m;
                if (hasEmbeddedLoss)
                {
                    var lossSubmission = BuildSaleLossSubmission(model, normalizedInvoice);
                    lossSubmission.SalesTransactionId = sale.Id;
                    await _lossWorkflow.CreateAsync(lossSubmission);
                }

                var ledgerEntry = SaleLedgerFactory.BuildSaleLedgerEntry(
                    sale,
                    conversion,
                    contractId: sale.ContractId ?? model.SourcePurchaseContractId);

                _db.LedgerEntries.Add(ledgerEntry);
                await _db.SaveChangesAsync();

                await PostSaleAccountingAsync(sale);

                await _audit.LogAsync(
                    nameof(SalesTransaction),
                    sale.Id,
                    AuditAction.Insert,
                    diff: AuditDiffFormatter.ForCreate(
                        ("ContractId", sale.ContractId),
                        ("CompanyId", sale.CompanyId),
                        ("CustomerId", sale.CustomerId),
                        ("ProductId", sale.ProductId),
                        ("DestinationLocationId", sale.DestinationLocationId),
                        ("ShipmentId", sale.ShipmentId),
                        ("SaleStage", sale.SaleStage),
                        ("InvoiceNumber", sale.InvoiceNumber),
                        ("SaleDate", sale.SaleDate),
                        ("QuantityMt", sale.QuantityMt),
                        ("Currency", sale.Currency),
                        ("UnitPriceInCurrency", sale.UnitPriceInCurrency),
                        ("AppliedFxRateToUsd", sale.AppliedFxRateToUsd),
                        ("UnitPriceUsd", sale.UnitPriceUsd),
                        ("TotalInCurrency", sale.TotalInCurrency),
                        ("TotalUsd", sale.TotalUsd),
                        ("SourcePurchaseContractId", stockOutMovements.Count == 1 ? stockOutMovements[0].ContractId : null),
                        ("SourcePurchaseContractIds", string.Join(",", stockOutMovements.Select(m => m.ContractId))),
                        ("SourceTerminalId", stockOutMovements.FirstOrDefault()?.TerminalId),
                        ("SourceStorageTankId", stockOutMovements.FirstOrDefault()?.StorageTankId),
                        ("InventoryMovementId", stockOutMovements.Count == 1 ? stockOutMovements[0].Id : null),
                        ("InventoryMovementIds", string.Join(",", stockOutMovements.Select(m => m.Id))),
                        ("LedgerReference", ledgerEntry.Reference)));

                foreach (var stockOutMovement in stockOutMovements)
                {
                    await _audit.LogAsync(
                        nameof(InventoryMovement),
                        stockOutMovement.Id,
                        AuditAction.Insert,
                        diff: AuditDiffFormatter.ForCreate(
                            ("ProductId", stockOutMovement.ProductId),
                            ("ContractId", stockOutMovement.ContractId),
                            ("TerminalId", stockOutMovement.TerminalId),
                            ("StorageTankId", stockOutMovement.StorageTankId),
                            ("Direction", stockOutMovement.Direction),
                            ("QuantityMt", stockOutMovement.QuantityMt),
                            ("MovementDate", stockOutMovement.MovementDate),
                            ("ReferenceDocument", stockOutMovement.ReferenceDocument),
                            ("SaleId", sale.Id)));
                }

                await _db.SaveChangesAsync();

                if (transaction is not null)
                {
                    await transaction.CommitAsync();
                }

                TempData["ok"] = hasEmbeddedLoss
                    ? "فروش و ضایعات این مرحله با موفقیت ثبت شد."
                    : requiresTerminalStock
                        ? "فروش و خروج موجودی با موفقیت ثبت شد."
                        : "فروش با موفقیت ثبت شد.";

                if (TryGetLocalReturnUrl(model.ReturnUrl, out var localReturnUrl))
                {
                    return Redirect(localReturnUrl);
                }

                return RedirectToAction(nameof(Details), new { id = sale.Id });
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
        catch (DbUpdateException dup) when (_formTokens.IsDuplicate(dup))
        {
            TempData["err"] = "این عملیات قبلاً ثبت شده است و دوباره ثبت نشد.";
            return RedirectToAction(nameof(Index));
        }
        catch (BusinessRuleException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create sale transaction.");
            ModelState.AddModelError(string.Empty, "ثبت فروش انجام نشد. لطفاً اطلاعات فروش را بررسی و دوباره تلاش کنید.");
        }

        model.InvoiceNumber = normalizedInvoice;
        model.Notes = normalizedNotes;
        await PopulateLookupsAsync(createModel: model);
        return View(model);
    }

    public async Task<IActionResult> Details(int id, string? returnUrl = null)
    {
        var sale = await _db.SalesTransactions
            .Include(s => s.Contract)
            .Include(s => s.Company)
            .Include(s => s.Customer)
            .Include(s => s.Product)
            .Include(s => s.DestinationLocation)
            .Include(s => s.Shipment)
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == id);

        if (sale is null)
        {
            return NotFound();
        }

        if (sale.IsCancelled)
        {
            TempData["err"] = "این فروش لغو شده است.";
        }

        var ledgerEntry = await _db.LedgerEntries
            .AsNoTracking()
            .Where(l => l.SourceType == "Sale" && l.SourceId == sale.Id)
            .OrderByDescending(l => l.Id)
            .FirstOrDefaultAsync();

        var directPayments = await _db.PaymentTransactions
            .AsNoTracking()
            .Where(p => p.SalesTransactionId == sale.Id)
            .OrderByDescending(p => p.PaymentDate)
            .ThenByDescending(p => p.Id)
            .Select(p => new SalesPaymentDetailsViewModel
            {
                PaymentTransactionId = p.Id,
                PaymentDate = p.PaymentDate,
                Amount = p.Amount,
                Currency = p.Currency,
                AmountUsd = p.AmountUsd,
                Reference = p.Reference,
                IsIncoming = p.Direction == PaymentDirection.In,
                IsAdvanceApplication = false
            })
            .ToListAsync();

        var appliedAdvances = await _db.CustomerPaymentAllocationApplications
            .AsNoTracking()
            .Where(a => a.SalesTransactionId == sale.Id
                && a.Status == CustomerPaymentAllocationApplicationStatus.Active)
            .OrderByDescending(a => a.AppliedAt)
            .ThenByDescending(a => a.Id)
            .Select(a => new SalesPaymentDetailsViewModel
            {
                PaymentTransactionId = a.CustomerPaymentAllocation!.PaymentTransactionId,
                PaymentDate = a.CustomerPaymentAllocation.PaymentTransaction!.PaymentDate,
                Amount = a.AppliedPaymentAmount,
                Currency = a.PaymentCurrencyCode,
                AmountUsd = a.AppliedAmountUsd,
                Reference = a.CustomerPaymentAllocation.PaymentTransaction.Reference,
                IsIncoming = true,
                IsAdvanceApplication = true
            })
            .ToListAsync();

        var payments = directPayments
            .Concat(appliedAdvances)
            .OrderByDescending(p => p.PaymentDate)
            .ThenByDescending(p => p.PaymentTransactionId)
            .ToList();
        var directReceivedUsd = directPayments.Sum(p => p.IsIncoming ? p.AmountUsd : -p.AmountUsd);
        var receivedUsd = decimal.Round(
            directReceivedUsd + appliedAdvances.Sum(p => p.AmountUsd),
            4,
            MidpointRounding.AwayFromZero);
        var openReceivableUsd = decimal.Round(
            sale.TotalUsd - receivedUsd,
            4,
            MidpointRounding.AwayFromZero);

        var stockOutMovements = await _db.InventoryMovements
            .Include(m => m.Contract)
            .Include(m => m.Terminal)
            .Include(m => m.StorageTank)
            .AsNoTracking()
            .Where(m => m.SalesTransactionId == sale.Id && m.Direction == MovementDirection.Out)
            .OrderBy(m => m.Id)
            .ToListAsync();
        var stockOutMovement = stockOutMovements.FirstOrDefault();
        var stockOutContractNumbers = stockOutMovements
            .Select(m => m.Contract?.ContractNumber ?? (m.ContractId.HasValue ? $"#{m.ContractId}" : null))
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Distinct()
            .ToList();

        var receiptAllocation = await _db.LoadingReceiptAllocations
            .Include(a => a.LoadingReceipt)
            .Include(a => a.SourcePurchaseContract)
            .Include(a => a.Terminal)
            .Include(a => a.StorageTank)
            .AsNoTracking()
            .Where(a => a.SalesTransactionId == sale.Id)
            .OrderByDescending(a => a.Id)
            .FirstOrDefaultAsync();

        var inventoryTransportReceipt = await _db.InventoryTransportReceipts
            .Include(r => r.InventoryTransportLeg)
                .ThenInclude(l => l!.SourcePurchaseContract)
            .Include(r => r.InventoryTransportLeg)
                .ThenInclude(l => l!.SourceTerminal)
            .Include(r => r.InventoryTransportLeg)
                .ThenInclude(l => l!.SourceStorageTank)
            .AsNoTracking()
            .Where(r => r.SalesTransactionId == sale.Id && !r.IsCancelled)
            .OrderByDescending(r => r.Id)
            .FirstOrDefaultAsync();

        var createdByName = sale.CreatedByUserId.HasValue
            ? await _db.Users.AsNoTracking()
                .Where(u => u.Id == sale.CreatedByUserId.Value)
                .Select(u => u.FullName)
                .FirstOrDefaultAsync()
            : null;

        ViewBag.ReturnUrl = TryGetLocalReturnUrl(returnUrl, out var localReturnUrl) ? localReturnUrl : null;

        return View(new SalesDetailsViewModel
        {
            Id = sale.Id,
            ContractId = sale.ContractId,
            ShipmentId = sale.ShipmentId,
            PreSaleOrderId = sale.PreSaleOrderId,
            InvoiceNumber = sale.InvoiceNumber,
            TicketSerialNumber = sale.TicketSerialNumber,
            StockSourceType = sale.StockSourceType,
            IsCancelled = sale.IsCancelled,
            CreatedAtUtc = sale.CreatedAtUtc,
            CreatedByName = createdByName,
            SaleDate = sale.SaleDate,
            SaleStage = sale.SaleStage,
            ContractNumber = sale.Contract?.ContractNumber ?? SalesContractText.WithoutSalesContract,
            CompanyName = sale.CompanyId is null ? SalesContractText.MultiLicense : (sale.Company?.Name ?? ""),
            CustomerName = sale.Customer?.Name ?? "",
            ProductName = sale.Product?.Name ?? "",
            DestinationName = sale.DestinationLocation?.Name,
            ShipmentCode = sale.Shipment?.ShipmentCode,
            QuantityMt = sale.QuantityMt,
            Currency = sale.Currency,
            UnitPriceInCurrency = sale.UnitPriceInCurrency,
            TotalInCurrency = sale.TotalInCurrency,
            AppliedFxRateToUsd = sale.AppliedFxRateToUsd,
            UnitPriceUsd = sale.UnitPriceUsd,
            TotalUsd = sale.TotalUsd,
            ReceivedUsd = receivedUsd,
            ReceivableBalanceUsd = sale.IsCancelled ? 0m : Math.Max(openReceivableUsd, 0m),
            OverpaymentUsd = sale.IsCancelled ? 0m : Math.Max(-openReceivableUsd, 0m),
            Payments = payments,
            Notes = sale.Notes,
            LedgerEntryId = ledgerEntry?.Id,
            LedgerReference = ledgerEntry?.Reference,
            LedgerDescription = ledgerEntry?.Description,
            LedgerAmountUsd = ledgerEntry?.AmountUsd,
            LedgerSideName = ledgerEntry?.Side == LedgerSide.Credit ? "بستانکار" : ledgerEntry is null ? null : "بدهکار",
            InventoryMovementId = stockOutMovement?.Id,
            InventoryMovementCount = stockOutMovements.Count,
            InventoryMovementIdsText = stockOutMovements.Count == 0
                ? null
                : string.Join(", ", stockOutMovements.Select(m => $"#{m.Id}")),
            LoadingReceiptId = receiptAllocation?.LoadingReceiptId,
            LoadingReceiptAllocationId = receiptAllocation?.Id,
            InventoryTransportReceiptId = inventoryTransportReceipt?.Id,
            InventoryTransportLegId = inventoryTransportReceipt?.InventoryTransportLegId,
            InventoryTransportReference = inventoryTransportReceipt?.InventoryTransportLeg is null
                ? null
                : BuildInventoryTransportReference(inventoryTransportReceipt.InventoryTransportLeg),
            SourcePurchaseContractNumber = stockOutContractNumbers.Count > 0
                ? string.Join(", ", stockOutContractNumbers)
                : receiptAllocation?.SourcePurchaseContract?.ContractNumber
                    ?? inventoryTransportReceipt?.InventoryTransportLeg?.SourcePurchaseContract?.ContractNumber,
            SourceTerminalName = stockOutMovement?.Terminal?.Name
                ?? receiptAllocation?.Terminal?.Name
                ?? inventoryTransportReceipt?.InventoryTransportLeg?.SourceTerminal?.Name,
            SourceStorageTankCode = StorageTankDisplay.BuildOptional(stockOutMovement?.StorageTank)
                ?? StorageTankDisplay.BuildOptional(receiptAllocation?.StorageTank)
                ?? StorageTankDisplay.BuildOptional(inventoryTransportReceipt?.InventoryTransportLeg?.SourceStorageTank)
        });
    }

    public async Task<IActionResult> Invoice(int id, string template)
    {
        if (!SalesInvoiceTemplateOptions.TryGet(template, out var templateMetadata))
        {
            return BadRequest("Invalid invoice template.");
        }

        var sale = await _db.SalesTransactions
            .Include(s => s.Contract)
                .ThenInclude(c => c!.DestinationLocation)
            .Include(s => s.Customer)
            .Include(s => s.Product)
            .Include(s => s.DestinationLocation)
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == id);

        if (sale is null)
        {
            return NotFound();
        }

        var borderOrLocation = await ResolveInvoiceBorderOrLocationAsync(sale);
        var model = SalesInvoiceMapper.Build(sale, templateMetadata, borderOrLocation);
        return View(model);
    }

    private async Task<string?> ResolveInvoiceBorderOrLocationAsync(SalesTransaction sale)
    {
        if (!string.IsNullOrWhiteSpace(sale.DestinationLocation?.Name))
        {
            return sale.DestinationLocation.Name;
        }

        if (!string.IsNullOrWhiteSpace(sale.Contract?.DestinationLocation?.Name))
        {
            return sale.Contract.DestinationLocation.Name;
        }

        var allocation = await _db.LoadingReceiptAllocations
            .Include(a => a.DestinationLocation)
            .Include(a => a.DestinationTerminal)
            .Include(a => a.Terminal)
            .AsNoTracking()
            .Where(a => a.SalesTransactionId == sale.Id)
            .OrderByDescending(a => a.Id)
            .FirstOrDefaultAsync();

        if (allocation is not null)
        {
            return FirstNonEmpty(
                allocation.DestinationLocation?.Name,
                allocation.DestinationName,
                allocation.DestinationTerminal?.Name,
                allocation.Terminal?.Name);
        }

        var transportReceipt = await _db.InventoryTransportReceipts
            .Include(r => r.DestinationTerminal)
            .Include(r => r.InventoryTransportLeg)
                .ThenInclude(l => l!.DestinationLocation)
            .Include(r => r.InventoryTransportLeg)
                .ThenInclude(l => l!.DestinationTerminal)
            .Include(r => r.InventoryTransportLeg)
                .ThenInclude(l => l!.SourceTerminal)
            .AsNoTracking()
            .Where(r => r.SalesTransactionId == sale.Id && !r.IsCancelled)
            .OrderByDescending(r => r.Id)
            .FirstOrDefaultAsync();

        if (transportReceipt is not null)
        {
            return FirstNonEmpty(
                transportReceipt.InventoryTransportLeg?.DestinationLocation?.Name,
                transportReceipt.DestinationTerminal?.Name,
                transportReceipt.InventoryTransportLeg?.DestinationTerminal?.Name,
                transportReceipt.InventoryTransportLeg?.SourceTerminal?.Name);
        }

        var stockOut = await _db.InventoryMovements
            .Include(m => m.Terminal)
            .AsNoTracking()
            .Where(m => m.SalesTransactionId == sale.Id && m.Direction == MovementDirection.Out)
            .OrderBy(m => m.Id)
            .FirstOrDefaultAsync();

        return stockOut?.Terminal?.Name;
    }

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private async Task<ShipmentFlowSaleContext?> BuildShipmentFlowSaleContextAsync(int shipmentId)
    {
        var shipment = await _db.Shipments
            .Include(s => s.Vessel)
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == shipmentId);
        if (shipment is null)
        {
            return null;
        }

        var legs = await _db.InventoryTransportLegs
            .Include(l => l.SourcePurchaseContract)
                .ThenInclude(c => c!.Company)
            .Include(l => l.Product)
            .AsNoTracking()
            .Where(l => l.ShipmentId == shipmentId && l.Status != InventoryTransportLegStatus.Cancelled)
            .OrderBy(l => l.Id)
            .ToListAsync();

        // Shipment-flow sales normally represent the original vessel cargo. Later
        // tank-to-truck/wagon legs are the same inventory moving again and must
        // never increase sale capacity here. The current tank-backed shipment flow
        // records its root legs as Unspecified; a persisted outbound movement proves
        // that stock was actually loaded and is therefore eligible for this fallback.
        var originalVesselLegs = legs
            .Where(l => l.TransportType == LoadingTransportType.Vessel
                && l.Status != InventoryTransportLegStatus.Draft)
            .ToList();

        var tankLoadedRootLegs = originalVesselLegs.Count > 0
            ? []
            : legs
                .Where(l => l.TransportType == LoadingTransportType.Unspecified
                    && l.Status != InventoryTransportLegStatus.Draft
                    && l.OutboundInventoryMovementId.HasValue
                    && l.SourceStorageTankId.HasValue)
                .ToList();
        var saleCapacityLegs = originalVesselLegs.Count > 0
            ? originalVesselLegs
            : tankLoadedRootLegs;

        var rawLoadedByContract = saleCapacityLegs
            .GroupBy(l => l.SourcePurchaseContractId)
            .ToDictionary(g => g.Key, g => RoundQuantity(g.Sum(l => l.QuantityMt)));
        var loadedByContract = new Dictionary<int, decimal>(rawLoadedByContract);
        if (originalVesselLegs.Count == 0 && loadedByContract.Count > 0)
        {
            var allocationCaps = await _db.ShipmentContracts
                .AsNoTracking()
                .Where(sc => sc.ShipmentId == shipmentId && sc.QuantityMt.HasValue && sc.QuantityMt > 0m)
                .GroupBy(sc => sc.ContractId)
                .Select(group => new { ContractId = group.Key, QuantityMt = group.Sum(sc => sc.QuantityMt!.Value) })
                .ToDictionaryAsync(row => row.ContractId, row => row.QuantityMt);

            if (allocationCaps.Count == 0
                && shipment.ContractId.HasValue
                && shipment.QuantityMt > 0m)
            {
                allocationCaps[shipment.ContractId.Value] = shipment.QuantityMt;
            }

            foreach (var contractId in loadedByContract.Keys.ToList())
            {
                if (allocationCaps.TryGetValue(contractId, out var allocationCap))
                {
                    loadedByContract[contractId] = RoundQuantity(
                        Math.Min(loadedByContract[contractId], allocationCap));
                }
            }

            var cappedTotal = loadedByContract.Values.Sum();
            if (shipment.QuantityMt > 0m && cappedTotal > shipment.QuantityMt)
            {
                var scale = shipment.QuantityMt / cappedTotal;
                foreach (var contractId in loadedByContract.Keys.ToList())
                {
                    loadedByContract[contractId] = RoundQuantity(loadedByContract[contractId] * scale);
                }
            }
        }

        var saleCapacityLegIds = saleCapacityLegs.Select(l => l.Id).ToList();

        var receiptShortageByContract = await _db.InventoryTransportReceipts
            .AsNoTracking()
            .Where(r => !r.IsCancelled
                && r.InventoryTransportLeg != null
                && r.InventoryTransportLeg.ShipmentId == shipmentId
                && saleCapacityLegIds.Contains(r.InventoryTransportLegId)
                && r.InventoryTransportLeg.Status != InventoryTransportLegStatus.Draft
                && r.InventoryTransportLeg.Status != InventoryTransportLegStatus.Cancelled)
            .GroupBy(r => r.InventoryTransportLeg!.SourcePurchaseContractId)
            .Select(g => new { ContractId = g.Key, QuantityMt = g.Sum(r => r.ShortageQuantityMt) })
            .ToDictionaryAsync(x => x.ContractId, x => x.QuantityMt);

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
            .Select(l => new ShipmentFlowSaleSlice(l.ContractId, l.DifferenceQuantityMt))
            .ToListAsync();

        var directLossCapacity = loadedByContract.ToDictionary(
            row => row.Key,
            row => Math.Max(row.Value - receiptShortageByContract.GetValueOrDefault(row.Key), 0m));
        var directLossByContract = AllocateShipmentFlowSlices(directLosses, directLossCapacity);
        var shortageByContract = loadedByContract.Keys.ToDictionary(
            contractId => contractId,
            contractId => RoundQuantity(
                receiptShortageByContract.GetValueOrDefault(contractId)
                + directLossByContract.GetValueOrDefault(contractId)));

        var previousSales = await _db.SalesTransactions
            .AsNoTracking()
            .Where(s => s.ShipmentId == shipmentId && !s.IsCancelled && s.SaleStage != SaleStage.PreSale)
            .Select(s => new ShipmentFlowSaleSlice(s.SourcePurchaseContractId, s.QuantityMt))
            .ToListAsync();
        var previousSalesQuantityMt = previousSales.Sum(s => s.QuantityMt);

        var capacityAfterShortage = loadedByContract.ToDictionary(
            row => row.Key,
            row => RoundQuantity(Math.Max(row.Value - shortageByContract.GetValueOrDefault(row.Key), 0m)));

        // فروشی که قرارداد منبع دارد دقیقاً به همان قرارداد نسبت داده می‌شود؛ فروش‌های قدیمیِ بدون
        // قرارداد منبع مثل قبل به‌نسبت ظرفیت بین قراردادهای محموله پخش می‌شوند.
        var previousSalesByContract = AllocateShipmentFlowSlices(previousSales, capacityAfterShortage);
        var pnlByLeg = await new InventoryTransportPnlService(_db)
            .BuildForLegsAsync(saleCapacityLegIds);

        var contractRows = saleCapacityLegs
            .GroupBy(l => l.SourcePurchaseContractId)
            .Select(group =>
            {
                var loaded = loadedByContract.GetValueOrDefault(group.Key);
                var shortage = shortageByContract.GetValueOrDefault(group.Key);
                var previousSale = previousSalesByContract.GetValueOrDefault(group.Key);
                var rawLoaded = rawLoadedByContract.GetValueOrDefault(group.Key);
                var capacityScale = rawLoaded > 0m ? loaded / rawLoaded : 0m;
                var costParts = group
                    .Select(l => new
                    {
                        l.QuantityMt,
                        UnitCost = l.PurchaseUnitCostUsd
                            ?? (l.SourcePurchaseContract is null
                                ? null
                                : ContractPricingAdapter.GetCanonicalFinalPrice(l.SourcePurchaseContract))
                    })
                    .Where(x => x.UnitCost.HasValue && x.UnitCost.Value > 0m)
                    .ToList();
                var costQuantity = costParts.Sum(x => x.QuantityMt);
                var unitCost = costQuantity > 0m
                    ? costParts.Sum(x => x.QuantityMt * x.UnitCost!.Value) / costQuantity
                    : (decimal?)null;

                return new ShipmentFlowSaleContractRowViewModel
                {
                    ContractId = group.Key,
                    ContractNumber = group.First().SourcePurchaseContract?.ContractNumber ?? $"#{group.Key}",
                    CompanyName = group.First().SourcePurchaseContract?.Company?.Name,
                    ProductName = group.First().Product?.Name,
                    LoadedQuantityMt = loaded,
                    ShortageQuantityMt = shortage,
                    PreviousSalesQuantityMt = previousSale,
                    AvailableQuantityMt = RoundQuantity(Math.Max(loaded - shortage - previousSale, 0m)),
                    PurchaseUnitCostUsd = unitCost.HasValue ? decimal.Round(unitCost.Value, 4, MidpointRounding.AwayFromZero) : null,
                    TotalCostUsd = decimal.Round(
                        group.Sum(l => pnlByLeg.TryGetValue(l.Id, out var pnl) ? pnl.TotalCostUsd : 0m) * capacityScale,
                        4,
                        MidpointRounding.AwayFromZero)
                };
            })
            .OrderBy(r => r.ContractNumber)
            .ToList();

        var capacityContractIds = loadedByContract
            .Where(row => row.Value > 0m)
            .Select(row => row.Key)
            .ToHashSet();
        var effectiveCapacityLegs = saleCapacityLegs
            .Where(l => capacityContractIds.Contains(l.SourcePurchaseContractId))
            .ToList();
        var companyIds = effectiveCapacityLegs
            .Where(l => l.SourcePurchaseContract is not null)
            .Select(l => l.SourcePurchaseContract!.CompanyId)
            .Distinct()
            .ToList();
        var productIds = effectiveCapacityLegs.Select(l => l.ProductId).Distinct().ToList();
        var productNames = effectiveCapacityLegs
            .Select(l => l.Product?.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var model = new ShipmentFlowSaleCreateViewModel
        {
            ShipmentId = shipment.Id,
            ShipmentCode = shipment.ShipmentCode,
            VesselName = shipment.Vessel?.Name,
            ProductName = productNames.Count == 1 ? productNames[0]! : "چند محصول",
            CurrentStageName = "در جریان حمل",
            LoadedQuantityMt = RoundQuantity(loadedByContract.Values.Sum()),
            RegisteredShortageQuantityMt = RoundQuantity(shortageByContract.Values.Sum()),
            PreviousSalesQuantityMt = RoundQuantity(previousSalesQuantityMt),
            AvailableQuantityMt = RoundQuantity(Math.Max(
                loadedByContract.Values.Sum() - shortageByContract.Values.Sum() - previousSalesQuantityMt,
                0m)),
            DestinationLocationId = shipment.DestinationLocationId,
            Contracts = contractRows
        };

        // مشخصات هر قرارداد محموله: جواز/شرکت و محصولِ خودش می‌ماند و از روی همان leg خوانده می‌شود.
        var availableByContract = contractRows.ToDictionary(r => r.ContractId, r => r.AvailableQuantityMt);
        var contractFacts = effectiveCapacityLegs
            .Where(l => l.SourcePurchaseContract is not null)
            .GroupBy(l => l.SourcePurchaseContractId)
            .ToDictionary(
                group => group.Key,
                group => new ShipmentFlowSaleContractFacts(
                    group.Key,
                    group.First().SourcePurchaseContract!.CompanyId,
                    group.First().ProductId,
                    availableByContract.GetValueOrDefault(group.Key)));

        var context = new ShipmentFlowSaleContext(
            model,
            companyIds.Count == 1 ? companyIds[0] : 0,
            productIds.Count == 1 ? productIds[0] : 0,
            shipment.DestinationLocationId,
            contractFacts);

        model.SourceContractRequired = context.RequiresSourceContract;
        return context;
    }

    private async Task PopulateShipmentFlowSaleLookupsAsync(ShipmentFlowSaleCreateViewModel model)
    {
        ViewBag.Customers = new SelectList(
            await _db.Customers.AsNoTracking().Where(c => c.IsActive).OrderBy(c => c.Name).ToListAsync(),
            "Id", "Name", model.CustomerId);
        ViewBag.Currencies = new SelectList(
            await _db.Currencies.AsNoTracking().Where(c => c.IsActive).OrderBy(c => c.Code).ToListAsync(),
            "Code", "Code", model.Currency);
        ViewBag.Destinations = new SelectList(
            await _db.Locations.AsNoTracking().Where(l => l.IsActive).OrderBy(l => l.Name).ToListAsync(),
            "Id", "Name", model.DestinationLocationId);
    }

    private static void ApplyShipmentFlowSaleDisplay(
        ShipmentFlowSaleCreateViewModel target,
        ShipmentFlowSaleCreateViewModel source)
    {
        target.ShipmentCode = source.ShipmentCode;
        target.VesselName = source.VesselName;
        target.ProductName = source.ProductName;
        target.CurrentStageName = source.CurrentStageName;
        target.LoadedQuantityMt = source.LoadedQuantityMt;
        target.RegisteredShortageQuantityMt = source.RegisteredShortageQuantityMt;
        target.PreviousSalesQuantityMt = source.PreviousSalesQuantityMt;
        target.AvailableQuantityMt = source.AvailableQuantityMt;
        target.SourceContractRequired = source.SourceContractRequired;
        target.Contracts = source.Contracts;
    }

    // سقف مقدار فروش: اگر قرارداد منبع مشخص باشد، مقدار قابل فروشِ همان قرارداد؛ وگرنه کل محموله.
    private static decimal ResolveShipmentFlowAvailableMt(
        ShipmentFlowSaleContext context,
        int? sourcePurchaseContractId)
        => sourcePurchaseContractId.HasValue
            && context.ContractFacts.TryGetValue(sourcePurchaseContractId.Value, out var facts)
                ? facts.AvailableQuantityMt
                : context.Model.AvailableQuantityMt;

    // مقدارهایی که قرارداد منبعشان مشخص است دقیقاً به همان قرارداد (تا سقف ظرفیتش) نسبت داده می‌شوند؛
    // باقی‌مانده به‌نسبت ظرفیت بین قراردادهای محموله پخش می‌شود. برای کسری و فروش مشترک است.
    private static Dictionary<int, decimal> AllocateShipmentFlowSlices(
        IReadOnlyList<ShipmentFlowSaleSlice> slices,
        IReadOnlyDictionary<int, decimal> capacityByContract)
    {
        var result = capacityByContract.Keys.ToDictionary(id => id, _ => 0m);
        var unassigned = 0m;
        foreach (var slice in slices)
        {
            if (slice.ContractId.HasValue && result.ContainsKey(slice.ContractId.Value))
            {
                var available = Math.Max(capacityByContract[slice.ContractId.Value] - result[slice.ContractId.Value], 0m);
                var applied = Math.Min(slice.QuantityMt, available);
                result[slice.ContractId.Value] += applied;
                unassigned += Math.Max(slice.QuantityMt - applied, 0m);
            }
            else
            {
                unassigned += slice.QuantityMt;
            }
        }

        if (unassigned > 0m)
        {
            var remaining = capacityByContract.ToDictionary(
                row => row.Key,
                row => Math.Max(row.Value - result[row.Key], 0m));
            var allocated = AllocateQuantityByWeight(unassigned, remaining);
            foreach (var row in allocated)
            {
                result[row.Key] += row.Value;
            }
        }

        return result;
    }

    private static Dictionary<int, decimal> AllocateQuantityByWeight(
        decimal total,
        IReadOnlyDictionary<int, decimal> weights)
    {
        var result = weights.Keys.ToDictionary(id => id, _ => 0m);
        var positive = weights.Where(row => row.Value > 0m).OrderBy(row => row.Key).ToList();
        var totalWeight = positive.Sum(row => row.Value);
        var quantity = Math.Min(Math.Max(total, 0m), totalWeight);
        if (quantity <= 0m || totalWeight <= 0m)
        {
            return result;
        }

        var allocated = 0m;
        for (var i = 0; i < positive.Count; i++)
        {
            var share = i == positive.Count - 1
                ? quantity - allocated
                : RoundQuantity(quantity * positive[i].Value / totalWeight);
            share = Math.Min(share, positive[i].Value);
            result[positive[i].Key] = share;
            allocated += share;
        }

        return result;
    }

    private string? NormalizeLocalReturnUrl(string? returnUrl)
        => TryGetLocalReturnUrl(returnUrl, out var localReturnUrl) ? localReturnUrl : null;

    private static decimal RoundQuantity(decimal value)
        => decimal.Round(value, 4, MidpointRounding.AwayFromZero);

    private static bool RequiresTerminalStock(SaleStage stage) => stage == SaleStage.TerminalStock;

    private static string BuildInventoryTransportReference(InventoryTransportLeg leg)
        => leg.WagonNumber
            ?? leg.BillOfLadingNumber
            ?? leg.RwbNo
            ?? $"#{leg.Id}";

    private static string BuildSaleInventoryNotes(SaleStage saleStage, string invoiceNumber, string? notes)
    {
        var baseNote = $"Sale trace | Stage={saleStage} | Invoice={invoiceNumber}";
        if (string.IsNullOrWhiteSpace(notes))
        {
            return baseNote;
        }

        var combined = $"{baseNote} | {notes.Trim()}";
        return combined.Length <= 1000 ? combined : combined[..1000];
    }

    private static string BuildLossFieldKey(string fieldName)
        => $"Loss.{fieldName}";

    private static IReadOnlyCollection<LossEventStage> GetAllowedSaleLossStages()
        => [LossEventStage.TransitLoss, LossEventStage.CustomsLoss, LossEventStage.SalesDifference];

    private static LossEventStage ResolveDefaultLossStage(SaleStage saleStage) => saleStage switch
    {
        SaleStage.InTransit => LossEventStage.TransitLoss,
        SaleStage.Border => LossEventStage.CustomsLoss,
        SaleStage.AfterCustoms => LossEventStage.CustomsLoss,
        _ => LossEventStage.SalesDifference
    };

    private static LossEventSubmission BuildSaleLossSubmission(
        SalesCreateViewModel model,
        string normalizedInvoice)
        => StageLossCaptureMapper.ToSubmission(
            model.Loss,
            new StageLossCaptureContext
            {
                Stage = ResolveDefaultLossStage(model.SaleStage),
                AllowCustomStage = true,
                ActualQuantityMt = model.QuantityMt,
                EventDate = model.SaleDate,
                ProductId = model.ProductId,
                ContractId = model.ContractId,
                ShipmentId = model.ShipmentId,
                TerminalId = model.SourceTerminalId,
                StorageTankId = model.SourceStorageTankId,
                DefaultReference = normalizedInvoice
            });

    private static List<SelectListItem> GetSaleLossStageItems(LossEventStage selectedStage)
        => GetAllowedSaleLossStages()
            .Select(stage => new SelectListItem
            {
                Value = ((int)stage).ToString(),
                Text = LossEventStageLabels.ToPersian(stage),
                Selected = stage == selectedStage
            })
            .ToList();

    private static List<SelectListItem> GetSaleStageItems(SaleStage selectedStage)
        => new[]
        {
            SaleStage.PreSale,
            SaleStage.InTransit,
            SaleStage.Border,
            SaleStage.AfterCustoms,
            SaleStage.TerminalStock
        }
        .Select(stage => new SelectListItem
        {
            Value = ((int)stage).ToString(),
            Text = SaleStageLabels.ToPersian(stage),
            Selected = stage == selectedStage
        })
        .ToList();

    private static void NormalizeCreateModel(SalesCreateViewModel model)
    {
        model.Currency = SystemCurrency.Normalize(model.Currency);
        model.InvoiceNumber = (model.InvoiceNumber ?? string.Empty).Trim();
        model.Notes = string.IsNullOrWhiteSpace(model.Notes) ? null : model.Notes.Trim();
    }

    private sealed record ShipmentFlowSaleContext(
        ShipmentFlowSaleCreateViewModel Model,
        int CompanyId,
        int ProductId,
        int? DestinationLocationId,
        IReadOnlyDictionary<int, ShipmentFlowSaleContractFacts> ContractFacts)
    {
        // جواز (Company) فقط برچسبِ قرارداد است و نباید مانع فروش کلی شود؛ اختلاف جواز قراردادها
        // آزاد است. تنها چیزی که فروشِ سهمیِ کل محموله را ناممکن می‌کند، اختلاف محصول است
        // (یک قیمت واحد برای دو محصول معنا ندارد)؛ در آن حالت باید قرارداد منبع انتخاب شود.
        public bool RequiresSourceContract => ContractFacts.Count > 1 && ProductId <= 0;
    }

    private sealed record ShipmentFlowSaleContractFacts(
        int ContractId,
        int CompanyId,
        int ProductId,
        decimal AvailableQuantityMt);

    private sealed record ShipmentFlowSaleSlice(int? ContractId, decimal QuantityMt);
}
