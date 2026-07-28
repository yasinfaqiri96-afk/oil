using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Helpers;
using PTGOilSystem.Web.Models.ContractJourney;
using PTGOilSystem.Web.Models.Contracts;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Security;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Audit;
using PTGOilSystem.Web.Services.Exceptions;

namespace PTGOilSystem.Web.Controllers;

[Authorize]
public partial class ContractsController : Controller
{
    private readonly ApplicationDbContext _db;
    private readonly IAuditService _audit;
    private readonly ICurrencyConversionService _currencyConversion;
    private readonly IFormTokenGuard _formTokens;
    // مرحله ۶ — Dual-write اختیاری به دفتر کل جدید. پشت Feature Flag و null-safe.
    private readonly Services.Accounting.IPurchaseAccountingAdapter? _purchaseAccounting;

    [ActivatorUtilitiesConstructor]
    public ContractsController(
        ApplicationDbContext db,
        IAuditService audit,
        ICurrencyConversionService currencyConversion,
        IFormTokenGuard formTokens,
        Services.Accounting.IPurchaseAccountingAdapter? purchaseAccounting = null)
    {
        _db = db;
        _audit = audit;
        _currencyConversion = currencyConversion;
        _formTokens = formTokens;
        _purchaseAccounting = purchaseAccounting;
    }

    public ContractsController(ApplicationDbContext db, IAuditService audit)
        : this(
            db,
            audit,
            new CurrencyConversionService(new PricingService(db)),
            new FormTokenGuard(db))
    {
    }

    private async Task PopulateLookupsAsync(ContractFormViewModel? model = null)
    {
        var products = await _db.Products
            .AsNoTracking()
            .Where(p => p.IsActive)
            .OrderBy(p => p.Code)
            .Select(p => new
            {
                p.Id,
                p.Name,
                p.UnitId
            })
            .ToListAsync();

        ViewBag.Companies = new SelectList(
            await _db.Companies
                .AsNoTracking()
                .Where(c => c.IsActive)
                .OrderBy(c => c.Code)
                .Select(c => new { c.Id, c.Name })
                .ToListAsync(),
            "Id",
            "Name",
            model?.CompanyId);
        ViewBag.Products = new SelectList(
            products,
            "Id",
            "Name",
            model?.ProductId);
        ViewBag.ProductUnitMap = products.ToDictionary(p => p.Id, p => p.UnitId);
        ViewBag.Units = new SelectList(
            await _db.Units
                .AsNoTracking()
                .Where(u => u.IsActive)
                .OrderBy(u => u.Code)
                .Select(u => new
                {
                    u.Id,
                    DisplayName = string.IsNullOrWhiteSpace(u.Symbol)
                        ? $"{u.Code} - {u.Name}"
                        : $"{u.Code} - {u.Name} ({u.Symbol})"
                })
                .ToListAsync(),
            "Id",
            "DisplayName",
            model?.UnitId);
        ViewBag.Suppliers = new SelectList(
            await _db.Suppliers
                .AsNoTracking()
                .Where(s => s.IsActive)
                .OrderBy(s => s.Name)
                .Select(s => new { s.Id, s.Name })
                .ToListAsync(),
            "Id",
            "Name",
            model?.SupplierId);
        ViewBag.Customers = new SelectList(
            await _db.Customers
                .AsNoTracking()
                .Where(c => c.IsActive)
                .OrderBy(c => c.Name)
                .Select(c => new { c.Id, c.Name })
                .ToListAsync(),
            "Id",
            "Name",
            model?.CustomerId);
        ViewBag.Locations = new SelectList(
            await _db.Locations
                .AsNoTracking()
                .OrderBy(l => l.Name)
                .Select(l => new { l.Id, l.Name })
                .ToListAsync(),
            "Id",
            "Name",
            model?.DestinationLocationId);
        // فقط قراردادهایی که خودشان زیرقرارداد نیستند می‌توانند «قرارداد اصلی» باشند (یک سطح).
        // قرارداد در حال ویرایش هم نمی‌تواند والد خودش باشد.
        ViewBag.ParentContracts = new SelectList(
            await _db.Contracts
                .AsNoTracking()
                .Where(c => c.ParentContractId == null && (model == null || c.Id != model.Id))
                .OrderByDescending(c => c.ContractDate)
                .ThenBy(c => c.ContractNumber)
                .Select(c => new { c.Id, c.ContractNumber })
                .ToListAsync(),
            "Id",
            "ContractNumber",
            model?.ParentContractId);
        ViewBag.Currencies = new SelectList(
            await _db.Currencies
                .AsNoTracking()
                .Where(c => c.IsActive)
                .OrderBy(c => c.Code)
                .Select(c => new { c.Code })
                .ToListAsync(),
            "Code",
            "Code",
            model?.Currency);
        ViewBag.Partners = new SelectList(
            await _db.Partners
                .AsNoTracking()
                .Where(p => p.IsActive)
                .OrderBy(p => p.Code)
                .Select(p => new
                {
                    p.Id,
                    DisplayName = string.IsNullOrWhiteSpace(p.NamePersian)
                        ? $"{p.Code} - {p.Name}"
                        : $"{p.Code} - {p.NamePersian}"
                })
                .ToListAsync(),
            "Id",
            "DisplayName");
        ViewBag.PurchaseContractNumberPreview = await GenerateNextContractNumberAsync(ContractType.Purchase);
        ViewBag.SaleContractNumberPreview = await GenerateNextContractNumberAsync(ContractType.Sale);
    }

    public async Task<IActionResult> Index(string? q, ContractType? type, ContractStatus? status, int page = 1)
    {
        const int pageSize = 20;

        var query = _db.Contracts
            .Include(c => c.Company)
            .Include(c => c.Product)
            .Include(c => c.Unit)
            .Include(c => c.Supplier)
            .Include(c => c.Customer)
            .Include(c => c.ContractPartners)
                .ThenInclude(cp => cp.Partner)
            .AsSplitQuery()
            .AsNoTracking()
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim();
            query = query.Where(c =>
                c.ContractNumber.Contains(term) ||
                (c.Supplier != null && c.Supplier.Name.Contains(term)) ||
                (c.Customer != null && c.Customer.Name.Contains(term)) ||
                c.ContractPartners.Any(cp => cp.Partner != null && cp.Partner.Name.Contains(term)));
        }
        if (type.HasValue) query = query.Where(c => c.ContractType == type.Value);
        if (status.HasValue) query = query.Where(c => c.Status == status.Value);

        var stats = await query
            .GroupBy(_ => 1)
            .Select(group => new
            {
                TotalCount = group.Count(),
                ActiveCount = group.Count(contract => contract.Status == ContractStatus.Active),
                PurchaseCount = group.Count(contract => contract.ContractType == ContractType.Purchase),
                SaleCount = group.Count(contract => contract.ContractType == ContractType.Sale)
            })
            .SingleOrDefaultAsync();
        var totalCount = stats?.TotalCount ?? 0;
        var pageCount = page <= 0
            ? 1
            : Math.Max(1, (int)Math.Ceiling(totalCount / (double)pageSize));
        var currentPage = page <= 0 ? 1 : Math.Clamp(page, 1, pageCount);

        var items = await (page <= 0
                ? query.OrderByDescending(c => c.ContractDate).ThenByDescending(c => c.Id)
                : query
                    .OrderByDescending(c => c.ContractDate)
                    .ThenByDescending(c => c.Id)
                    .Skip((currentPage - 1) * pageSize)
                    .Take(pageSize))
            .ToListAsync();

        return View(new ContractIndexViewModel
        {
            Query = q,
            Type = type,
            Status = status,
            Items = items,
            CurrentPage = currentPage,
            PageCount = pageCount,
            TotalCount = totalCount,
            ActiveCount = stats?.ActiveCount ?? 0,
            PurchaseCount = stats?.PurchaseCount ?? 0,
            SaleCount = stats?.SaleCount ?? 0
        });
    }

    public Task<IActionResult> Details(int id, string? tab = null)
    {
        var journeyTab = MapLegacyDetailsTabToJourneyTab(tab);

        return Task.FromResult<IActionResult>(RedirectToAction(
            nameof(ContractJourneyController.Details),
            "ContractJourney",
            new
            {
                contractId = id,
                tab = journeyTab,
                lockContract = true
            }));
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
        public async Task<IActionResult> Create(int? supplierId = null)
    {
        var model = new ContractFormViewModel
        {
            ContractNumber = await GenerateNextContractNumberAsync(ContractType.Purchase),
            ContractDate = DateTime.UtcNow.Date,
            Status = ContractStatus.Active,
            PricingMethod = PricingMethod.ManualFinalPrice,
            UiPricingType = UiPricingType.Agreed,
            PlattsUiMode = PlattsUiMode.ManualDescriptive,
            Currency = SystemCurrency.BaseCurrencyCode,
            OwnershipType = ContractOwnershipType.Personal,
            ContractType = ContractType.Purchase,
            SupplierId = supplierId
        };
        EnsurePartnerRows(model);
        await PopulateLookupsAsync(model);
        return View(model);
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(
        ContractFormViewModel model,
        [FromForm(Name = FormTokenHtmlHelper.FieldName)] string? formToken = null)
    {
        NormalizeFormModel(model);
        model.ContractNumber = await GenerateNextContractNumberAsync(model.ContractType);
        ModelState.Remove(nameof(model.ContractNumber));
        await ValidateLookupsAsync(model, isCreate: true);
        await ValidateOwnershipAsync(model);
        ValidatePricingModel(model);
        ValidateRubSettlementModel(model);

        if (!ModelState.IsValid)
        {
            EnsurePartnerRows(model);
            await PopulateLookupsAsync(model);
            return View(model);
        }

        var contract = new Contract
        {
            ContractNumber = model.ContractNumber,
            ContractType = model.ContractType,
            Status = model.Status,
            ParentContractId = model.ParentContractId,
            CompanyId = model.CompanyId,
            ProductId = model.ProductId,
            UnitId = model.UnitId,
            SupplierId = model.SupplierId,
            CustomerId = model.CustomerId,
            DestinationLocationId = model.DestinationLocationId,
            OwnershipType = model.OwnershipType,
            ContractDate = model.ContractDate,
            StartDate = model.StartDate,
            EndDate = model.EndDate,
            PricingMethod = model.PricingMethod,
            QuantityMt = model.QuantityMt,
            Currency = model.Currency,
            SettlementCurrencyCode = model.SettlementCurrencyCode,
            RubRatePolicy = model.RubRatePolicy,
            ContractRubPerUsdRate = model.ContractRubPerUsdRate,
            ContractRubRateDate = model.ContractRubRateDate,
            ContractRubRateSource = model.ContractRubRateSource,
            UnitPriceInCurrency = model.UnitPriceInCurrency,
            AppliedFxRateToUsd = model.AppliedFxRateToUsd,
            UnitPriceUsd = model.UnitPriceUsd,
            BenchmarkCode = model.BenchmarkCode,
            PlattsPeriodType = model.PlattsPeriodType,
            PremiumDiscountUsd = model.PremiumDiscountUsd,
            PlattsManualPriceUsd = model.PlattsManualPriceUsd,
            PlattsBasisDate = model.PlattsBasisDate,
            PlattsBasisMonth = model.PlattsBasisMonth,
            MinimumPriceUsd = model.MinimumPriceUsd,
            ManualFinalPriceUsd = model.ManualFinalPriceUsd,
            PricingFormulaNote = model.PricingFormulaNote,
            Notes = model.Notes
        };

        ApplyContractCleanup(contract);

        if (!await ResolveFixedPricingAsync(contract, model))
        {
            EnsurePartnerRows(model);
            await PopulateLookupsAsync(model);
            return View(model);
        }

        var partnerShares = GetNormalizedPartnerShares(model);
        contract.ContractPartners = partnerShares
            .Select(p => new ContractPartner
            {
                PartnerId = p.PartnerId!.Value,
                SharePercent = p.SharePercent!.Value
            })
            .ToList();

        // Duplicate-submit guard: token persists atomically with the contract.
        _formTokens.Stamp(formToken, "Contract.Create", nameof(Contract));

        _db.Contracts.Add(contract);
        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (_formTokens.IsDuplicate(ex))
        {
            TempData["err"] = "این عملیات قبلاً ثبت شده است و دوباره ثبت نشد.";
            return RedirectToAction(nameof(Index));
        }

        await _audit.LogAndSaveAsync(
            nameof(Contract),
            contract.Id,
            AuditAction.Insert,
            diff: AuditDiffFormatter.ForCreate(
                ("ContractNumber", contract.ContractNumber),
                ("ContractType", contract.ContractType),
                ("Status", contract.Status),
                ("ParentContractId", contract.ParentContractId),
                ("CompanyId", contract.CompanyId),
                ("ProductId", contract.ProductId),
                ("UnitId", contract.UnitId),
                ("SupplierId", contract.SupplierId),
                ("CustomerId", contract.CustomerId),
                ("OwnershipType", contract.OwnershipType),
                ("Partners", BuildPartnerSummary(contract.ContractPartners)),
                ("PricingMethod", contract.PricingMethod),
                ("QuantityMt", contract.QuantityMt),
                ("Currency", contract.Currency),
                ("SettlementCurrencyCode", contract.SettlementCurrencyCode),
                ("RubRatePolicy", contract.RubRatePolicy),
                ("ContractRubPerUsdRate", contract.ContractRubPerUsdRate),
                ("ContractRubRateDate", contract.ContractRubRateDate),
                ("ContractRubRateSource", contract.ContractRubRateSource),
                ("UnitPriceInCurrency", contract.UnitPriceInCurrency),
                ("AppliedFxRateToUsd", contract.AppliedFxRateToUsd),
                ("UnitPriceUsd", contract.UnitPriceUsd),
                ("PremiumUsd", contract.PremiumUsd),
                ("BenchmarkCode", contract.BenchmarkCode),
                ("PlattsPeriodType", contract.PlattsPeriodType),
                ("PremiumDiscountUsd", contract.PremiumDiscountUsd),
                ("PlattsManualPriceUsd", contract.PlattsManualPriceUsd),
                ("PlattsBasisDate", contract.PlattsBasisDate),
                ("PlattsBasisMonth", contract.PlattsBasisMonth),
                ("MinimumPriceUsd", contract.MinimumPriceUsd),
                ("ManualFinalPriceUsd", contract.ManualFinalPriceUsd),
                ("PricingFormulaNote", contract.PricingFormulaNote),
                ("ContractDate", contract.ContractDate)));

        TempData["ok"] = "قرارداد ثبت شد.";
        return RedirectToAction(nameof(Details), new { id = contract.Id });
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    public async Task<IActionResult> Edit(int id)
    {
        var item = await _db.Contracts
            .Include(c => c.Company)
            .Include(c => c.Product)
            .Include(c => c.Unit)
            .Include(c => c.Supplier)
            .Include(c => c.Customer)
            .Include(c => c.ContractPartners)
                .ThenInclude(cp => cp.Partner)
            .AsSplitQuery()
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id);
        if (item == null) return NotFound();

        var model = BuildFormModel(item);
        EnsurePartnerRows(model);
        await PopulateLookupsAsync(model);
        return View(model);
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, ContractFormViewModel model)
    {
        if (id != model.Id) return BadRequest();

        NormalizeFormModel(model);

        var existing = await _db.Contracts
            .Include(c => c.Company)
            .Include(c => c.Product)
            .Include(c => c.Unit)
            .Include(c => c.Supplier)
            .Include(c => c.Customer)
            .Include(c => c.ContractPartners)
                .ThenInclude(cp => cp.Partner)
            .AsSplitQuery()
            .FirstOrDefaultAsync(x => x.Id == id);
        if (existing == null) return NotFound();

        HydrateSummaryFields(model, existing);
        await ValidateLookupsAsync(model, isCreate: false);
        await ValidateOwnershipAsync(model);
        ValidatePricingModel(model);
        ValidateRubSettlementModel(model);

        if (!ModelState.IsValid)
        {
            EnsurePartnerRows(model);
            await PopulateLookupsAsync(model);
            return View(model);
        }

        var candidate = new Contract
        {
            Id = existing.Id,
            ContractNumber = model.ContractNumber,
            ContractType = model.ContractType,
            Status = model.Status,
            ParentContractId = model.ParentContractId,
            CompanyId = model.CompanyId,
            ProductId = model.ProductId,
            UnitId = model.UnitId,
            SupplierId = model.SupplierId,
            CustomerId = model.CustomerId,
            DestinationLocationId = model.DestinationLocationId,
            OwnershipType = model.OwnershipType,
            ContractDate = model.ContractDate,
            StartDate = model.StartDate,
            EndDate = model.EndDate,
            PricingMethod = model.PricingMethod,
            QuantityMt = model.QuantityMt,
            Currency = model.Currency,
            SettlementCurrencyCode = model.SettlementCurrencyCode,
            RubRatePolicy = model.RubRatePolicy,
            ContractRubPerUsdRate = model.ContractRubPerUsdRate,
            ContractRubRateDate = model.ContractRubRateDate,
            ContractRubRateSource = model.ContractRubRateSource,
            UnitPriceInCurrency = model.UnitPriceInCurrency,
            AppliedFxRateToUsd = model.AppliedFxRateToUsd,
            UnitPriceUsd = model.UnitPriceUsd,
            BenchmarkCode = model.BenchmarkCode,
            PlattsPeriodType = model.PlattsPeriodType,
            PremiumDiscountUsd = model.PremiumDiscountUsd,
            PlattsManualPriceUsd = model.PlattsManualPriceUsd,
            PlattsBasisDate = model.PlattsBasisDate,
            PlattsBasisMonth = model.PlattsBasisMonth,
            MinimumPriceUsd = model.MinimumPriceUsd,
            ManualFinalPriceUsd = model.ManualFinalPriceUsd,
            PricingFormulaNote = model.PricingFormulaNote,
            Notes = model.Notes
        };

        ApplyContractCleanup(candidate);

        if (!await ResolveFixedPricingAsync(candidate, model))
        {
            EnsurePartnerRows(model);
            await PopulateLookupsAsync(model);
            return View(model);
        }

        // نرخ نهاییِ قبل از اعمال تغییرات را نگه می‌داریم تا اگر نرخ قرارداد عوض شد،
        // همهٔ بارگیری‌ها (حتی قطعی‌شده‌ها) خودکار با نرخ جدید هماهنگ شوند.
        var previousCanonicalFinalPrice = ContractPricingAdapter.GetCanonicalFinalPrice(existing);

        var previousPartnerSummary = BuildPartnerSummary(existing.ContractPartners);
        var normalizedPartnerShares = GetNormalizedPartnerShares(model);
        var nextPartnerSummary = BuildPartnerSummary(normalizedPartnerShares);

        var diff = AuditDiffFormatter.ForUpdate(
            ("ContractNumber", existing.ContractNumber, candidate.ContractNumber),
            ("ContractType", existing.ContractType, candidate.ContractType),
            ("Status", existing.Status, candidate.Status),
            ("CompanyId", existing.CompanyId, candidate.CompanyId),
            ("ProductId", existing.ProductId, candidate.ProductId),
            ("SupplierId", existing.SupplierId, candidate.SupplierId),
            ("CustomerId", existing.CustomerId, candidate.CustomerId),
            ("ContractDate", existing.ContractDate, candidate.ContractDate),
            ("StartDate", existing.StartDate, candidate.StartDate),
            ("EndDate", existing.EndDate, candidate.EndDate),
            ("DestinationLocationId", existing.DestinationLocationId, candidate.DestinationLocationId),
            ("UnitId", existing.UnitId, candidate.UnitId),
            ("OwnershipType", existing.OwnershipType, candidate.OwnershipType),
            ("Partners", previousPartnerSummary, nextPartnerSummary),
            ("Notes", existing.Notes, candidate.Notes),
            ("PricingMethod", existing.PricingMethod, candidate.PricingMethod),
            ("QuantityMt", existing.QuantityMt, candidate.QuantityMt),
            ("Currency", existing.Currency, candidate.Currency),
            ("SettlementCurrencyCode", existing.SettlementCurrencyCode, candidate.SettlementCurrencyCode),
            ("RubRatePolicy", existing.RubRatePolicy, candidate.RubRatePolicy),
            ("ContractRubPerUsdRate", existing.ContractRubPerUsdRate, candidate.ContractRubPerUsdRate),
            ("ContractRubRateDate", existing.ContractRubRateDate, candidate.ContractRubRateDate),
            ("ContractRubRateSource", existing.ContractRubRateSource, candidate.ContractRubRateSource),
            ("UnitPriceInCurrency", existing.UnitPriceInCurrency, candidate.UnitPriceInCurrency),
            ("AppliedFxRateToUsd", existing.AppliedFxRateToUsd, candidate.AppliedFxRateToUsd),
            ("UnitPriceUsd", existing.UnitPriceUsd, candidate.UnitPriceUsd),
            ("PremiumUsd", existing.PremiumUsd, candidate.PremiumUsd),
            ("BenchmarkCode", existing.BenchmarkCode, candidate.BenchmarkCode),
            ("PlattsPeriodType", existing.PlattsPeriodType, candidate.PlattsPeriodType),
            ("PremiumDiscountUsd", existing.PremiumDiscountUsd, candidate.PremiumDiscountUsd),
            ("PlattsManualPriceUsd", existing.PlattsManualPriceUsd, candidate.PlattsManualPriceUsd),
            ("PlattsBasisDate", existing.PlattsBasisDate, candidate.PlattsBasisDate),
            ("PlattsBasisMonth", existing.PlattsBasisMonth, candidate.PlattsBasisMonth),
            ("MinimumPriceUsd", existing.MinimumPriceUsd, candidate.MinimumPriceUsd),
            ("ManualFinalPriceUsd", existing.ManualFinalPriceUsd, candidate.ManualFinalPriceUsd),
            ("PricingFormulaNote", existing.PricingFormulaNote, candidate.PricingFormulaNote));

        existing.ContractNumber = candidate.ContractNumber;
        existing.ContractType = candidate.ContractType;
        existing.Status = candidate.Status;
        existing.CompanyId = candidate.CompanyId;
        existing.ProductId = candidate.ProductId;
        existing.SupplierId = candidate.SupplierId;
        existing.CustomerId = candidate.CustomerId;
        existing.ContractDate = candidate.ContractDate;
        existing.StartDate = candidate.StartDate;
        existing.EndDate = candidate.EndDate;
        existing.DestinationLocationId = candidate.DestinationLocationId;
        existing.UnitId = candidate.UnitId;
        existing.OwnershipType = candidate.OwnershipType;
        existing.Notes = candidate.Notes;
        existing.PricingMethod = candidate.PricingMethod;
        existing.QuantityMt = candidate.QuantityMt;
        existing.Currency = candidate.Currency;
        existing.SettlementCurrencyCode = candidate.SettlementCurrencyCode;
        existing.RubRatePolicy = candidate.RubRatePolicy;
        existing.ContractRubPerUsdRate = candidate.ContractRubPerUsdRate;
        existing.ContractRubRateDate = candidate.ContractRubRateDate;
        existing.ContractRubRateSource = candidate.ContractRubRateSource;
        existing.UnitPriceInCurrency = candidate.UnitPriceInCurrency;
        existing.AppliedFxRateToUsd = candidate.AppliedFxRateToUsd;
        existing.UnitPriceUsd = candidate.UnitPriceUsd;
        existing.PremiumUsd = candidate.PremiumUsd;
        existing.BenchmarkCode = candidate.BenchmarkCode;
        existing.PlattsPeriodType = candidate.PlattsPeriodType;
        existing.PremiumDiscountUsd = candidate.PremiumDiscountUsd;
        existing.PlattsManualPriceUsd = candidate.PlattsManualPriceUsd;
        existing.PlattsBasisDate = candidate.PlattsBasisDate;
        existing.PlattsBasisMonth = candidate.PlattsBasisMonth;
        existing.MinimumPriceUsd = candidate.MinimumPriceUsd;
        existing.ManualFinalPriceUsd = candidate.ManualFinalPriceUsd;
        existing.PricingFormulaNote = candidate.PricingFormulaNote;

        _db.ContractPartners.RemoveRange(existing.ContractPartners);
        foreach (var partnerShare in normalizedPartnerShares)
        {
            existing.ContractPartners.Add(new ContractPartner
            {
                ContractId = existing.Id,
                PartnerId = partnerShare.PartnerId!.Value,
                SharePercent = partnerShare.SharePercent!.Value
            });
        }

        // قاعدهٔ #9: ویرایش عمومی قرارداد هم مانند EditPricing فقط بارگیری‌های «در انتظار قیمت» را قطعی
        // می‌کند. تغییر نرخ قرارداد، بارگیریِ از پیش قطعی‌شده را بازقیمت‌گذاری/بازقفل نمی‌کند.
        var newCanonicalFinalPrice = ContractPricingAdapter.GetCanonicalFinalPrice(existing);
        var contractPriceChanged = newCanonicalFinalPrice.HasValue
            && newCanonicalFinalPrice != previousCanonicalFinalPrice;

        var skippedFinalizedCount = contractPriceChanged
            ? await CountFinalizedPurchaseLoadingsAsync(existing)
            : 0;
        var syncedLoadingCount = await SyncPurchaseLoadingPricesAsync(existing);
        await _db.SaveChangesAsync();
        await PostRepricedPurchasesAsync(existing.Id);
        await _audit.LogAndSaveAsync(nameof(Contract), existing.Id, AuditAction.Update, diff: diff);
        TempData["ok"] = BuildPricingSyncMessage(
            syncedLoadingCount > 0
                ? $"تغییرات قرارداد اعمال شد و قیمت خرید {syncedLoadingCount:N0} بارگیری هماهنگ شد."
                : "تغییرات قرارداد اعمال شد.",
            skippedFinalizedCount);
        return RedirectToAction(nameof(Details), new { id });
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        var item = await _db.Contracts
            .Include(c => c.ContractPartners)
            .FirstOrDefaultAsync(x => x.Id == id);
        if (item == null) return NotFound();
        var hasChildren = await HasContractDependenciesAsync(id);
        if (hasChildren)
        {
            TempData["err"] = "این قرارداد دارای رکورد وابسته است و قابل حذف نیست.";
            return RedirectToAction(nameof(Index));
        }

        var diff = AuditDiffFormatter.ForDelete(
            ("ContractNumber", item.ContractNumber),
            ("ContractType", item.ContractType),
            ("Status", item.Status));

        if (item.ContractPartners.Any())
        {
            _db.ContractPartners.RemoveRange(item.ContractPartners);
        }

        _db.Contracts.Remove(item);
        await _db.SaveChangesAsync();
        await _audit.LogAndSaveAsync(nameof(Contract), id, AuditAction.Delete, diff: diff);
        TempData["ok"] = "قرارداد حذف شد.";
        return RedirectToAction(nameof(Index));
    }

    private async Task<bool> HasContractDependenciesAsync(int id)
    {
        return await _db.ContractAmendments.AnyAsync(a => a.ContractId == id)
            || await _db.ContractPricingRules.AnyAsync(r => r.ContractId == id)
            || await _db.InventoryBatches.AnyAsync(b => b.ContractId == id)
            || await _db.InventoryMovements.AnyAsync(m => m.ContractId == id)
            || await _db.LoadingRegisters.AnyAsync(l => l.ContractId == id)
            || await _db.LoadingReceiptAllocations.AnyAsync(a => a.SourcePurchaseContractId == id)
            || await _db.InventoryTransportLegs.AnyAsync(l => l.SourcePurchaseContractId == id)
            || await _db.TruckDispatches.AnyAsync(d => d.ContractId == id)
            || await _db.Shipments.AnyAsync(s => s.ContractId == id)
            || await _db.ShipmentContracts.AnyAsync(sc => sc.ContractId == id)
            || await _db.LossEvents.AnyAsync(e => e.ContractId == id)
            || await _db.SalesTransactions.AnyAsync(s => s.ContractId == id)
            || await _db.ExpenseTransactions.AnyAsync(e => e.ContractId == id)
            || await _db.PaymentTransactions.AnyAsync(p => p.ContractId == id)
            || await _db.SarrafSettlements.AnyAsync(s => s.ContractId == id)
            || await _db.ThreeWaySettlements.AnyAsync(s => s.CustomerSaleContractId == id || s.SupplierPurchaseContractId == id)
            || await _db.LedgerEntries.AnyAsync(l => l.ContractId == id)
            || await _db.ContractBalanceTransfers.AnyAsync(t => t.FromContractId == id || t.ToContractId == id)
            || await _db.SupplierPaymentAllocations.AnyAsync(a => a.ContractId == id)
            || await _db.AssetRentTransactions.AnyAsync(r => r.ChargedToContractId == id);
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    public async Task<IActionResult> EditPricing(int id, string? returnUrl = null, UiPricingType? pricingType = null)
    {
        var contract = await _db.Contracts
            .Include(c => c.Product)
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == id);
        if (contract is null) return NotFound();

        var model = new EditPricingViewModel
        {
            Id = contract.Id,
            ContractNumber = contract.ContractNumber,
            ProductName = contract.Product?.Name,
            PricingMethod = contract.PricingMethod,
            UiPricingType = pricingType ?? ContractPricingAdapter.GetUiPricingType(contract),
            PlattsUiMode = ContractPricingAdapter.GetPlattsUiMode(contract),
            PlattsPeriodType = contract.PlattsPeriodType,
            PlattsManualPriceUsd = contract.PlattsManualPriceUsd,
            PremiumDiscountUsd = contract.PremiumDiscountUsd ?? contract.PremiumUsd,
            PricingFormulaNote = contract.PricingFormulaNote,
            PricingNote = contract.PricingFormulaNote,
            ManualFinalPriceUsd = contract.ManualFinalPriceUsd,
            FinalPriceUsdPerMt = ContractPricingAdapter.GetCanonicalFinalPrice(contract),
            IsPricingCompleted = ContractPricingAdapter.GetPricingStatus(contract) == PricingCompletionStatus.Completed,
            RubRatePolicy = contract.RubRatePolicy,
            ShowRubRateEntry = contract.RubRatePolicy == RubSettlementRatePolicy.RateLater,
            ContractRubPerUsdRate = contract.ContractRubPerUsdRate,
            ContractRubRateDate = contract.ContractRubRateDate,
            ContractRubRateSource = contract.ContractRubRateSource,
            ReturnUrl = returnUrl
        };
        return View(model);
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> EditPricing(int id, EditPricingViewModel model)
    {
        if (id != model.Id) return BadRequest();

        var contract = await _db.Contracts
            .Include(c => c.Product)
            .FirstOrDefaultAsync(c => c.Id == id);
        if (contract is null) return NotFound();

        NormalizeEditPricingModel(model);

        if (model.PricingMethod == PricingMethod.FormulaPlatts
            && model.PlattsPeriodType == PlattsPeriodType.Manual
            && string.IsNullOrWhiteSpace(model.PricingFormulaNote))
        {
            ModelState.AddModelError(nameof(model.PricingNote),
                "برای Platt's دستی / توضیحی، توضیح نرخ الزامی است.");
        }

        if (model.ManualFinalPriceUsd.HasValue && model.ManualFinalPriceUsd <= 0m)
        {
            ModelState.AddModelError(nameof(model.ManualFinalPriceUsd),
                "برای قیمت نهایی توافقی، مقدار قیمت الزامی و بزرگ‌تر از صفر است.");
        }

        // ورود نرخ روبلِ کل قرارداد فقط برای قراردادهایی با سیاست «نرخ بعداً مشخص می‌شود» مجاز است.
        var canEnterRubRate = contract.RubRatePolicy == RubSettlementRatePolicy.RateLater;
        if (model.ContractRubPerUsdRate.HasValue && model.ContractRubPerUsdRate.Value <= 0m)
        {
            ModelState.AddModelError(nameof(model.ContractRubPerUsdRate), "نرخ روبل باید بزرگ‌تر از صفر باشد.");
        }

        if (!ModelState.IsValid)
        {
            model.ContractNumber = contract.ContractNumber;
            model.ProductName = contract.Product?.Name;
            model.RubRatePolicy = contract.RubRatePolicy;
            model.ShowRubRateEntry = canEnterRubRate;
            model.ReturnUrl ??= string.Empty;
            return View(model);
        }

        // نرخ نهاییِ قبل از تغییر را نگه می‌داریم تا اگر عوض شد، همهٔ بارگیری‌ها بازقیمت‌گذاری شوند.
        var previousCanonicalFinalPrice = ContractPricingAdapter.GetCanonicalFinalPrice(contract);

        var prevPricingMethod = contract.PricingMethod;
        var prevUnitPriceUsd = contract.UnitPriceUsd;
        var prevPlattsPeriodType = contract.PlattsPeriodType;
        var prevPlattsManualPriceUsd = contract.PlattsManualPriceUsd;
        var prevPremiumDiscountUsd = contract.PremiumDiscountUsd;
        var prevManualFinalPriceUsd = contract.ManualFinalPriceUsd;
        var prevPricingFormulaNote = contract.PricingFormulaNote;

        var newNote = string.IsNullOrWhiteSpace(model.PricingFormulaNote) ? null : model.PricingFormulaNote.Trim();

        contract.PricingMethod = model.PricingMethod;
        contract.ManualFinalPriceUsd = model.ManualFinalPriceUsd;
        contract.PricingFormulaNote = newNote;
        contract.MinimumPriceUsd = null;

        if (model.PricingMethod == PricingMethod.FormulaPlatts)
        {
            contract.PlattsPeriodType = model.PlattsPeriodType;
            contract.PlattsManualPriceUsd = null;
            contract.PremiumDiscountUsd = model.PremiumDiscountUsd;
            contract.PremiumUsd = model.PremiumDiscountUsd;
            contract.Currency = SystemCurrency.BaseCurrencyCode;
            contract.UnitPriceInCurrency = null;
            contract.AppliedFxRateToUsd = null;
            contract.UnitPriceUsd = null;
        }
        else
        {
            contract.PlattsPeriodType = null;
            contract.PlattsManualPriceUsd = null;
            contract.PremiumDiscountUsd = null;
            contract.PremiumUsd = null;
            contract.BenchmarkCode = null;
            contract.PlattsBasisDate = null;
            contract.PlattsBasisMonth = null;
            contract.Currency = SystemCurrency.BaseCurrencyCode;
            contract.UnitPriceInCurrency = null;
            contract.AppliedFxRateToUsd = null;
            contract.UnitPriceUsd = null;
        }

        var prevRubRatePolicy = contract.RubRatePolicy;
        var prevContractRubPerUsdRate = contract.ContractRubPerUsdRate;
        var prevContractRubRateDate = contract.ContractRubRateDate;
        var prevContractRubRateSource = contract.ContractRubRateSource;

        // اگر قرارداد سیاست «نرخ بعداً مشخص می‌شود» داشت و حالا نرخ روبلِ کل قرارداد وارد شده،
        // آن را ثبت و سیاست را به «نرخ ثابت قرارداد» تبدیل می‌کنیم تا معادل روبلیِ کل قرارداد محاسبه/نمایش شود.
        if (canEnterRubRate && model.ContractRubPerUsdRate.HasValue && model.ContractRubPerUsdRate.Value > 0m)
        {
            contract.ContractRubPerUsdRate = model.ContractRubPerUsdRate;
            contract.ContractRubRateDate = model.ContractRubRateDate ?? DateTime.UtcNow.Date;
            contract.ContractRubRateSource = string.IsNullOrWhiteSpace(model.ContractRubRateSource)
                ? null
                : model.ContractRubRateSource.Trim();
            contract.RubRatePolicy = RubSettlementRatePolicy.FixedContractRate;
            contract.SettlementCurrencyCode = "RUB";
        }

        // قاعدهٔ #9: این مسیر عمومی است، پس حتی با تغییر نرخ نهاییِ قرارداد فقط بارگیری‌های «در انتظار قیمت»
        // قطعی می‌شوند. بارگیریِ قطعی‌شده دست‌نخورده می‌ماند تا Legacy Ledger و AmountUsdAtRubLock و سند
        // دفتر کل جدید بی‌صدا عوض نشوند؛ اصلاح آن‌ها فقط از مسیر صریحِ «اصلاح قیمت» ممکن است.
        var newCanonicalFinalPrice = ContractPricingAdapter.GetCanonicalFinalPrice(contract);
        var contractPriceChanged = newCanonicalFinalPrice.HasValue
            && newCanonicalFinalPrice != previousCanonicalFinalPrice;

        var skippedFinalizedCount = contractPriceChanged
            ? await CountFinalizedPurchaseLoadingsAsync(contract)
            : 0;
        var syncedLoadingCount = await SyncPurchaseLoadingPricesAsync(contract);
        await _db.SaveChangesAsync();
        await PostRepricedPurchasesAsync(contract.Id);

        var diff = AuditDiffFormatter.ForUpdate(
            ("PricingMethod", prevPricingMethod, contract.PricingMethod),
            ("UnitPriceUsd", prevUnitPriceUsd, contract.UnitPriceUsd),
            ("PlattsPeriodType", prevPlattsPeriodType, contract.PlattsPeriodType),
            ("PlattsManualPriceUsd", prevPlattsManualPriceUsd, contract.PlattsManualPriceUsd),
            ("PremiumDiscountUsd", prevPremiumDiscountUsd, contract.PremiumDiscountUsd),
            ("ManualFinalPriceUsd", prevManualFinalPriceUsd, contract.ManualFinalPriceUsd),
            ("PricingFormulaNote", prevPricingFormulaNote, contract.PricingFormulaNote),
            ("RubRatePolicy", prevRubRatePolicy, contract.RubRatePolicy),
            ("ContractRubPerUsdRate", prevContractRubPerUsdRate, contract.ContractRubPerUsdRate),
            ("ContractRubRateDate", prevContractRubRateDate, contract.ContractRubRateDate),
            ("ContractRubRateSource", prevContractRubRateSource, contract.ContractRubRateSource),
            ("SyncedLoadingPriceCount", 0, syncedLoadingCount),
            ("SkippedFinalizedLoadingCount", 0, skippedFinalizedCount));
        await _audit.LogAndSaveAsync(nameof(Contract), contract.Id, AuditAction.Update, diff: diff);

        TempData["ok"] = BuildPricingSyncMessage(
            syncedLoadingCount > 0
                ? $"نرخ قرارداد با موفقیت تکمیل شد و قیمت خرید {syncedLoadingCount:N0} بارگیری هماهنگ شد."
                : "نرخ قرارداد با موفقیت تکمیل شد.",
            skippedFinalizedCount);

        if (!string.IsNullOrWhiteSpace(model.ReturnUrl) && Url?.IsLocalUrl(model.ReturnUrl) == true)
        {
            return Redirect(model.ReturnUrl);
        }

        return RedirectToAction(nameof(Details), new { id });
    }

    // «اصلاح قیمت» — بازقیمت‌گذاریِ صریح و محافظت‌شدهٔ بارگیری‌های قطعی‌شده با نرخ فعلی قرارداد.
    // برخلاف قطعی‌سازی عادی (که بارگیری‌های قطعی را دست نمی‌زند)، این مسیر همهٔ بارگیری‌ها را
    // با نرخ فعلی قرارداد به‌روزرسانی و مبلغ روبل را بازقفل می‌کند. فقط با نقش ManageData،
    // تأیید کاربر (فرم POST + ضدجعل) و ثبت لاگ اجرا می‌شود (قاعدهٔ #9 — بخش محافظت‌شده).
    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> RepricePurchaseLoadings(int id, string? returnUrl = null)
    {
        var contract = await _db.Contracts
            .Include(c => c.Product)
            .FirstOrDefaultAsync(c => c.Id == id);
        if (contract is null) return NotFound();

        if (contract.ContractType != ContractType.Purchase
            || !ContractPricingAdapter.GetCanonicalFinalPrice(contract).HasValue)
        {
            TempData["err"] = "اصلاح قیمت فقط برای قرارداد خرید با نرخ نهایی ثبت‌شده ممکن است.";
            return RepriceRedirect(id, returnUrl);
        }

        // قیمت مستقل هر بارگیری (که از فایل اکسل خودش آمده) حفظ می‌شود؛ این اکشن فقط
        // بارگیری‌های بدون قیمت را با نرخ فعلی قرارداد تکمیل می‌کند.
        var repricedCount = await SyncPurchaseLoadingPricesAsync(contract);
        await _db.SaveChangesAsync();
        await PostRepricedPurchasesAsync(contract.Id);

        var diff = AuditDiffFormatter.ForUpdate(("RepricedLoadingCount", 0, repricedCount));
        await _audit.LogAndSaveAsync(nameof(Contract), contract.Id, AuditAction.Update, diff: diff);

        TempData["ok"] = repricedCount > 0
            ? $"قیمت خرید {repricedCount:N0} بارگیریِ بدون قیمت با نرخ فعلی قرارداد تکمیل شد."
            : "بارگیریِ بدون قیمتی برای اصلاح یافت نشد؛ قیمت بارگیری‌های قیمت‌دار دست‌نخورده ماند.";
        return RepriceRedirect(id, returnUrl);
    }

    private IActionResult RepriceRedirect(int id, string? returnUrl)
        => !string.IsNullOrWhiteSpace(returnUrl) && Url?.IsLocalUrl(returnUrl) == true
            ? Redirect(returnUrl)
            : RedirectToAction(nameof(Details), new { id });

    // قطعی‌سازی قیمت خرید بارگیری‌های قرارداد از روی نرخ نهایی قرارداد.
    // قاعدهٔ #9: به‌صورت پیش‌فرض فقط بارگیری‌های «در انتظار قیمت» (بدون قیمت معتبر) قطعی می‌شوند؛
    // بارگیری‌های از پیش قطعی‌شده با ویرایش دوبارهٔ نرخ قرارداد تغییر نمی‌کنند مگر repriceFinalized=true
    // (مسیر صریحِ «اصلاح قیمت» — که جداگانه محافظت/لاگ می‌شود).
    // مرحله ۶ — بعد از هر بازقیمت‌گذاری، خریدِ هر بارگیری دوباره به Adapter داده می‌شود.
    // Adapter خودش تشخیص می‌دهد: مبلغ عوض نشده → Duplicate (بی‌اثر)، عوض شده → Reversal سند
    // قبلی و پست نسخهٔ جدید، قیمت‌نداشتن → Skip. پس فراخوانی برای همهٔ بارگیری‌ها امن است.
    private async Task PostRepricedPurchasesAsync(int contractId)
    {
        if (_purchaseAccounting is null)
        {
            return;
        }

        var loadings = await _db.LoadingRegisters
            .AsNoTracking()
            .Where(l => l.ContractId == contractId)
            .ToListAsync();
        foreach (var loading in loadings)
        {
            await _purchaseAccounting.TryPostPurchaseAsync(loading);
        }
    }

    // شمارشِ بارگیری‌های از پیش قطعی‌شده‌ای که مسیر عمومی عمداً دست نمی‌زند، تا به کاربر اطلاع داده شود
    // نرخ قرارداد عوض شده ولی این بارگیری‌ها با نرخ قبلی مانده‌اند.
    // باید *قبل از* SyncPurchaseLoadingPricesAsync فراخوانی شود، وگرنه بارگیری‌های در انتظار قیمت که
    // همان لحظه قطعی شده‌اند هم به اشتباه «قطعی‌شدهٔ دست‌نخورده» شمرده می‌شوند.
    private async Task<int> CountFinalizedPurchaseLoadingsAsync(Contract contract)
    {
        if (contract.ContractType != ContractType.Purchase)
        {
            return 0;
        }

        return await _db.LoadingRegisters
            .CountAsync(l => l.ContractId == contract.Id
                && l.LoadingPriceUsd.HasValue
                && l.LoadingPriceUsd.Value > 0m);
    }

    private static string BuildPricingSyncMessage(string baseMessage, int skippedFinalizedCount)
        => skippedFinalizedCount > 0
            ? $"{baseMessage} نرخ قرارداد عوض شد ولی قیمت {skippedFinalizedCount:N0} بارگیریِ قیمت‌دار عمداً دست‌نخورده ماند؛ قیمت هر بارگیری مستقل است و از صفحهٔ همان بارگیری اصلاح می‌شود."
            : baseMessage;

    private async Task<int> SyncPurchaseLoadingPricesAsync(Contract contract, bool repriceFinalized = false)
    {
        if (contract.ContractType != ContractType.Purchase)
        {
            return 0;
        }

        var finalPrice = ContractPricingAdapter.GetCanonicalFinalPrice(contract);
        if (!finalPrice.HasValue || finalPrice.Value <= 0m)
        {
            return 0;
        }

        var loadings = await _db.LoadingRegisters
            .Where(l => l.ContractId == contract.Id
                && (repriceFinalized
                    || !l.LoadingPriceUsd.HasValue
                    || l.LoadingPriceUsd.Value <= 0m))
            .ToListAsync();

        var count = 0;
        var relockedLoadings = new List<LoadingRegister>();
        foreach (var loading in loadings)
        {
            loading.LoadingPriceUsd = finalPrice.Value;

            // اگر تسویه روبلی است، همان لحظهٔ قطعی‌سازی مبلغ روبل هم قفل می‌شود.
            // نرخ بر اساس سیاست قرارداد حل می‌شود: نرخ ثابت قرارداد، یا نرخ ذخیره‌شدهٔ همان بارگیری
            // (قاعدهٔ #7/#8 — نرخ امروز روی بارگیری‌های قدیمی اعمال نمی‌شود).
            var resolvedRubRate = contract.RubRatePolicy switch
            {
                RubSettlementRatePolicy.FixedContractRate => contract.ContractRubPerUsdRate,
                RubSettlementRatePolicy.PerLoadingRate => loading.RubPerUsdRate,
                _ => null
            };
            if (LoadingRubSettlement.TryLockFinalizedRub(loading, resolvedRubRate, forceRelock: repriceFinalized))
            {
                relockedLoadings.Add(loading);
            }

            count++;
        }

        // همهٔ بارگیری‌های دست‌خورده فرستاده می‌شوند، نه فقط روبلی‌های بازقفل‌شده: بارگیری دالری
        // با همین اصلاح قیمت مبلغش عوض می‌شود (یا تازه قیمت‌دار می‌شود) و سطر دفترش باید هماهنگ شود.
        await SyncSupplierLoadingLegacyLedgerAsync(contract, loadings);

        return count;
    }

    // اصلاح قیمت، مبلغ بارگیری را عوض می‌کند ولی سطر Legacy دفتر قدیمی با مبلغ قبلی می‌ماند و
    // طلب تأمین‌کننده کهنه می‌شود. همان سطر با snapshot جدید هماهنگ می‌شود (سطر دوم ساخته نمی‌شود).
    // بارگیری‌ای که هنگام ثبت هنوز قیمت نداشت اینجا برای اولین بار سطر می‌گیرد؛ کلید یکتای
    // (SourceType, SourceId) پیش از ساخت بررسی می‌شود تا اجرای دوباره رکورد تکراری نسازد.
    private async Task SyncSupplierLoadingLegacyLedgerAsync(Contract contract, IReadOnlyList<LoadingRegister> touchedLoadings)
    {
        if (touchedLoadings.Count == 0)
        {
            return;
        }

        var postable = touchedLoadings
            .Where(l => SupplierLoadingLedger.IsPostable(l, contract))
            .ToList();
        if (postable.Count == 0)
        {
            return;
        }

        var loadingIds = postable.Select(l => l.Id).ToList();
        var entries = await _db.LedgerEntries
            .Where(l => l.SourceType == SupplierLoadingLedger.SourceType && loadingIds.Contains(l.SourceId))
            .ToListAsync();

        var entriesByLoadingId = entries.ToDictionary(l => l.SourceId);
        var created = new List<LedgerEntry>();
        foreach (var loading in postable)
        {
            if (!entriesByLoadingId.TryGetValue(loading.Id, out var entry))
            {
                created.Add(SupplierLoadingLedger.Create(loading, contract));
                continue;
            }

            var previousAmountUsd = entry.AmountUsd;
            var previousSourceAmount = entry.SourceAmount;
            var previousFxRateToUsd = entry.AppliedFxRateToUsd;
            if (!SupplierLoadingLedger.ApplySnapshot(entry, loading))
            {
                continue;
            }

            await _audit.LogAsync(
                nameof(LedgerEntry),
                entry.Id,
                AuditAction.Update,
                diff: AuditDiffFormatter.ForUpdate(
                    ("AmountUsd", previousAmountUsd, entry.AmountUsd),
                    ("SourceAmount", previousSourceAmount, entry.SourceAmount),
                    ("AppliedFxRateToUsd", previousFxRateToUsd, entry.AppliedFxRateToUsd)));
        }

        if (created.Count == 0)
        {
            return;
        }

        // ذخیره لازم است تا شناسهٔ سطرهای تازه برای لاگ حسابرسی موجود شود؛ خودِ لاگ‌ها
        // مثل قبل با SaveChangesAsync فراخوان ثبت می‌شوند.
        _db.LedgerEntries.AddRange(created);
        await _db.SaveChangesAsync();
        foreach (var entry in created)
        {
            await _audit.LogAsync(
                nameof(LedgerEntry),
                entry.Id,
                AuditAction.Insert,
                diff: AuditDiffFormatter.ForCreate(
                    ("EntryDate", entry.EntryDate),
                    ("Side", entry.Side),
                    ("AmountUsd", entry.AmountUsd),
                    ("SourceAmount", entry.SourceAmount),
                    ("SourceCurrencyCode", entry.SourceCurrencyCode),
                    ("AppliedFxRateToUsd", entry.AppliedFxRateToUsd),
                    ("SourceType", entry.SourceType),
                    ("SourceId", entry.SourceId),
                    ("ContractId", entry.ContractId),
                    ("SupplierId", entry.SupplierId)));
        }
    }

    private static void NormalizeEditPricingModel(EditPricingViewModel model)
    {
        model.PricingMethod = ContractPricingAdapter.ToPricingMethod(model.UiPricingType);
        model.PlattsPeriodType = model.PricingMethod == PricingMethod.FormulaPlatts
            ? ContractPricingAdapter.ToPlattsPeriodType(model.PlattsUiMode)
            : null;

        var finalPrice = model.FinalPriceUsdPerMt ?? model.ManualFinalPriceUsd;
        model.FinalPriceUsdPerMt = finalPrice;
        model.ManualFinalPriceUsd = finalPrice;

        var note = string.IsNullOrWhiteSpace(model.PricingNote)
            ? model.PricingFormulaNote
            : model.PricingNote;
        model.PricingNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        model.PricingFormulaNote = model.PricingNote;
        model.IsPricingCompleted = finalPrice.HasValue && finalPrice.Value > 0m;
    }

    // قواعد قرارداد اصلی/زیرقرارداد: هر زیرقرارداد فقط یک قرارداد اصلی دارد، قرارداد نمی‌تواند
    // والد خودش باشد و بیشتر از یک سطح مجاز نیست (والدی که خودش زیرقرارداد است رد می‌شود).
    private async Task ValidateParentContractAsync(ContractFormViewModel model, bool isCreate)
    {
        if (!model.ParentContractId.HasValue)
        {
            return;
        }

        if (!isCreate && model.ParentContractId.Value == model.Id)
        {
            ModelState.AddModelError(nameof(model.ParentContractId), "قرارداد نمی‌تواند قرارداد اصلی خودش باشد.");
            return;
        }

        var parent = await _db.Contracts
            .AsNoTracking()
            .Where(c => c.Id == model.ParentContractId.Value)
            .Select(c => new { c.Id, c.ParentContractId })
            .FirstOrDefaultAsync();

        if (parent is null)
        {
            ModelState.AddModelError(nameof(model.ParentContractId), "قرارداد اصلی انتخاب‌شده معتبر نیست.");
            return;
        }

        if (parent.ParentContractId.HasValue)
        {
            ModelState.AddModelError(
                nameof(model.ParentContractId),
                "قراردادی که خودش زیرقرارداد است نمی‌تواند به‌عنوان قرارداد اصلی انتخاب شود.");
            return;
        }

        if (!isCreate && await _db.Contracts.AnyAsync(c => c.ParentContractId == model.Id))
        {
            ModelState.AddModelError(
                nameof(model.ParentContractId),
                "این قرارداد خودش زیرقرارداد دارد، پس نمی‌تواند زیرقراردادِ قرارداد دیگری شود.");
        }
    }

    private async Task ValidateLookupsAsync(ContractFormViewModel model, bool isCreate)
    {
        if (await _db.Contracts.AnyAsync(c => c.ContractNumber == model.ContractNumber
            && (isCreate || c.Id != model.Id)))
        {
            ModelState.AddModelError(nameof(model.ContractNumber), "این شماره قرارداد قبلاً ثبت شده است.");
        }

        await ValidateParentContractAsync(model, isCreate);

        if (!await _db.Companies.AsNoTracking().AnyAsync(c => c.Id == model.CompanyId && c.IsActive))
        {
            ModelState.AddModelError(nameof(model.CompanyId), "شرکت انتخاب‌شده معتبر نیست.");
        }

        var product = await _db.Products.AsNoTracking()
            .Where(p => p.Id == model.ProductId && p.IsActive)
            .Select(p => new { p.Id, p.UnitId })
            .FirstOrDefaultAsync();

        if (product is null)
        {
            ModelState.AddModelError(nameof(model.ProductId), "کالای انتخاب‌شده معتبر نیست.");
        }
        else if (!model.UnitId.HasValue && product.UnitId.HasValue)
        {
            model.UnitId = product.UnitId;
        }

        if (!model.UnitId.HasValue)
        {
            ModelState.AddModelError(nameof(model.UnitId), "انتخاب واحد الزامی است.");
        }
        else if (!await _db.Units.AsNoTracking().AnyAsync(u => u.Id == model.UnitId.Value && u.IsActive))
        {
            ModelState.AddModelError(nameof(model.UnitId), "واحد انتخاب‌شده معتبر نیست.");
        }

        if (model.ContractType == ContractType.Purchase)
        {
            if (!model.SupplierId.HasValue)
            {
                ModelState.AddModelError(nameof(model.SupplierId), "برای قرارداد خرید، تأمین‌کننده الزامی است.");
            }
            else if (!await _db.Suppliers.AsNoTracking().AnyAsync(s => s.Id == model.SupplierId.Value && s.IsActive))
            {
                ModelState.AddModelError(nameof(model.SupplierId), "تأمین‌کننده انتخاب‌شده معتبر نیست.");
            }
        }

        if (model.ContractType == ContractType.Sale)
        {
            if (!model.CustomerId.HasValue)
            {
                ModelState.AddModelError(nameof(model.CustomerId), "برای قرارداد فروش، مشتری الزامی است.");
            }
            else if (!await _db.Customers.AsNoTracking().AnyAsync(c => c.Id == model.CustomerId.Value && c.IsActive))
            {
                ModelState.AddModelError(nameof(model.CustomerId), "مشتری انتخاب‌شده معتبر نیست.");
            }
        }

        if (model.DestinationLocationId.HasValue &&
            !await _db.Locations.AsNoTracking().AnyAsync(l => l.Id == model.DestinationLocationId.Value))
        {
            ModelState.AddModelError(nameof(model.DestinationLocationId), "مقصد انتخاب‌شده معتبر نیست.");
        }

        var shouldValidateCurrency = model.PricingMethod == PricingMethod.Fixed || !string.IsNullOrWhiteSpace(model.Currency);
        if (shouldValidateCurrency)
        {
            var hasActiveCurrencies = await _db.Currencies.AsNoTracking().AnyAsync(c => c.IsActive);
            if (hasActiveCurrencies &&
                !await _db.Currencies.AsNoTracking().AnyAsync(c => c.IsActive && c.Code == model.Currency))
            {
                ModelState.AddModelError(nameof(model.Currency), "ارز انتخاب‌شده در master data وجود ندارد یا غیرفعال است.");
            }
        }

        // ارز تسویهٔ پویا باید یک ارز فعال در master data باشد (ارز پایه USD همیشه مجاز است).
        // RUB برای سازگاری با قراردادهای قدیمی معاف است؛ پیش‌تر hardcode بود و ردیف master نداشت.
        var isLegacyRubSettlement = string.Equals(model.SettlementCurrencyCode, "RUB", StringComparison.OrdinalIgnoreCase);
        if (model.RubRatePolicy != RubSettlementRatePolicy.NotApplicable
            && !SystemCurrency.IsBaseCurrency(model.SettlementCurrencyCode)
            && !isLegacyRubSettlement)
        {
            var hasActiveCurrencies = await _db.Currencies.AsNoTracking().AnyAsync(c => c.IsActive);
            if (hasActiveCurrencies &&
                !await _db.Currencies.AsNoTracking().AnyAsync(c => c.IsActive && c.Code == model.SettlementCurrencyCode))
            {
                ModelState.AddModelError(nameof(model.SettlementCurrencyCode),
                    "ارز تسویهٔ انتخاب‌شده در master data وجود ندارد یا غیرفعال است.");
            }
        }
    }

    private async Task<string> GenerateNextContractNumberAsync(ContractType contractType)
    {
        var prefix = contractType == ContractType.Sale ? "S" : "P";
        var marker = prefix + "-";
        var existingNumbers = await _db.Contracts
            .AsNoTracking()
            .Where(c => c.ContractNumber.StartsWith(marker))
            .Select(c => c.ContractNumber)
            .ToListAsync();

        var existingSet = existingNumbers.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var maxSequence = 0;

        foreach (var number in existingNumbers)
        {
            if (number.Length <= marker.Length)
            {
                continue;
            }

            var suffix = number[marker.Length..];
            if (int.TryParse(suffix, out var sequence) && sequence > maxSequence)
            {
                maxSequence = sequence;
            }
        }

        string candidate;
        do
        {
            maxSequence++;
            candidate = $"{marker}{maxSequence:000}";
        }
        while (existingSet.Contains(candidate)
            || await _db.Contracts.AsNoTracking().AnyAsync(c => c.ContractNumber == candidate));

        return candidate;
    }

    private async Task ValidateOwnershipAsync(ContractFormViewModel model)
    {
        if (model.OwnershipType == ContractOwnershipType.Personal)
        {
            return;
        }

        var partnerShares = GetNormalizedPartnerShares(model);
        if (!partnerShares.Any())
        {
            ModelState.AddModelError(nameof(model.PartnerShares), "برای قرارداد شراکتی، حداقل یک شریک باید ثبت شود.");
            return;
        }

        var partnerIds = partnerShares.Select(p => p.PartnerId!.Value).ToList();
        if (partnerIds.Count != partnerIds.Distinct().Count())
        {
            ModelState.AddModelError(nameof(model.PartnerShares), "یک شریک نمی‌تواند بیش از یک‌بار تکرار شود.");
        }

        var validPartnerIds = await _db.Partners.AsNoTracking()
            .Where(p => p.IsActive && partnerIds.Contains(p.Id))
            .Select(p => p.Id)
            .ToListAsync();

        if (validPartnerIds.Count != partnerIds.Distinct().Count())
        {
            ModelState.AddModelError(nameof(model.PartnerShares), "یکی از شریک‌های انتخاب‌شده معتبر نیست یا غیرفعال است.");
        }

        var totalShare = partnerShares.Sum(p => p.SharePercent ?? 0m);
        if (Math.Abs(totalShare - 100m) > 0.0001m)
        {
            ModelState.AddModelError(nameof(model.PartnerShares), "جمع درصد سهم شریک‌ها باید دقیقاً 100 باشد.");
        }
    }

    private static string MapLegacyDetailsTabToJourneyTab(string? tab)
    {
        var normalized = tab?.Trim().ToLowerInvariant();

        return normalized switch
        {
            ContractDetailsTabs.Loading
                or ContractJourneyTabs.Details.Operations
                or ContractJourneyTabs.Details.Loadings => ContractJourneyTabs.Details.Loadings,
            ContractJourneyTabs.Details.Receipts => ContractJourneyTabs.Details.Receipts,
            ContractJourneyTabs.Details.Inventory => ContractJourneyTabs.Details.Inventory,
            ContractDetailsTabs.Dispatch => ContractJourneyTabs.Details.Dispatch,
            ContractJourneyTabs.Details.Sales => ContractJourneyTabs.Details.Sales,
            ContractDetailsTabs.LoadingExpenses
                or ContractDetailsTabs.Expenses
                or ContractDetailsTabs.Losses
                or ContractJourneyTabs.Details.Costs => ContractJourneyTabs.Details.Costs,
            ContractJourneyTabs.Details.Finance => ContractJourneyTabs.Details.Finance,
            ContractDetailsTabs.ShipmentPnl or ContractJourneyTabs.Details.Ledger => ContractJourneyTabs.Details.Ledger,
            ContractJourneyTabs.Details.Dashboard => ContractJourneyTabs.Details.Summary,
            _ => ContractJourneyTabs.Details.Summary
        };
    }

    private static List<ContractPartnerShareInput> GetNormalizedPartnerShares(ContractFormViewModel model)
    {
        if (model.OwnershipType != ContractOwnershipType.Partnership)
        {
            return [];
        }

        return (model.PartnerShares ?? [])
            .Where(p => p.PartnerId.HasValue || p.SharePercent.HasValue)
            .Select(p => new ContractPartnerShareInput
            {
                PartnerId = p.PartnerId,
                SharePercent = p.SharePercent
            })
            .ToList();
    }

    private void ValidatePricingModel(ContractFormViewModel model)
    {
        if (model.ManualFinalPriceUsd.HasValue && model.ManualFinalPriceUsd <= 0m)
        {
            ModelState.AddModelError(nameof(model.FinalPriceUsdPerMt), "قیمت نهایی باید بزرگ‌تر از صفر باشد.");
        }

        if (model.PricingMethod == PricingMethod.FormulaPlatts)
        {
            if (!model.PlattsPeriodType.HasValue)
            {
                ModelState.AddModelError(nameof(model.PlattsUiMode), "نوع Platt's الزامی است.");
            }

            if (model.PlattsPeriodType == PlattsPeriodType.Daily
                && !model.PlattsBasisDate.HasValue)
            {
                ModelState.AddModelError(nameof(model.PlattsBasisDate), "برای پلتس روزانه، تاریخ مرجع الزامی است.");
            }

            if (model.PlattsPeriodType == PlattsPeriodType.Monthly
                && !model.PlattsBasisMonth.HasValue)
            {
                ModelState.AddModelError(nameof(model.PlattsBasisMonth), "برای پلتس ماهانه، ماه مرجع الزامی است.");
            }

            if (model.PlattsPeriodType == PlattsPeriodType.Manual
                && string.IsNullOrWhiteSpace(model.PricingFormulaNote))
            {
                ModelState.AddModelError(nameof(model.PricingNote), "برای Platt's دستی / توضیحی، توضیح نرخ الزامی است.");
            }

            return;
        }

        if (model.PricingMethod == PricingMethod.ManualFinalPrice)
        {
            return;
        }

        if (model.PricingMethod == PricingMethod.Fixed)
        {
            if (!model.UnitPriceInCurrency.HasValue || model.UnitPriceInCurrency <= 0m)
            {
                ModelState.AddModelError(nameof(model.UnitPriceInCurrency), "برای قیمت ثابت، قیمت واحد الزامی و بزرگ‌تر از صفر است.");
            }

            return;
        }

        if (string.IsNullOrWhiteSpace(model.BenchmarkCode))
        {
            ModelState.AddModelError(nameof(model.BenchmarkCode), "برای قیمت‌گذاری پلتس، Benchmark الزامی است.");
        }

        if (!model.PlattsPeriodType.HasValue)
        {
            ModelState.AddModelError(nameof(model.PlattsPeriodType), "برای قیمت‌گذاری پلتس، نوع دوره الزامی است.");
            return;
        }

        switch (model.PlattsPeriodType.Value)
        {
            case PlattsPeriodType.Daily:
                if (!model.PlattsBasisDate.HasValue)
                    ModelState.AddModelError(nameof(model.PlattsBasisDate), "برای پلتس روزانه، تاریخ مرجع الزامی است.");
                break;
            case PlattsPeriodType.Monthly:
                if (!model.PlattsBasisMonth.HasValue)
                    ModelState.AddModelError(nameof(model.PlattsBasisMonth), "برای پلتس ماهانه، ماه مرجع الزامی است.");
                break;
            case PlattsPeriodType.Manual:
                // Platt's Base Price is optional — contract can be saved without it (NeedsReview state)
                break;
            default:
                ModelState.AddModelError(nameof(model.PlattsPeriodType), "نوع دوره پلتس معتبر نیست.");
                break;
        }
    }

    private void ValidateRubSettlementModel(ContractFormViewModel model)
    {
        if (model.RubRatePolicy == RubSettlementRatePolicy.NotApplicable)
        {
            return;
        }

        var isRub = string.Equals(model.SettlementCurrencyCode, "RUB", StringComparison.OrdinalIgnoreCase);

        // فاز امن: برای ارز تسویهٔ غیر روبل فعلاً فقط «نرخ ثابت قرارداد» پشتیبانی می‌شود.
        // مسیر نرخ per-loading / «نرخ بعداً» فعلاً فقط برای RUB سیم‌کشی شده و در این فاز تغییر نکرده است.
        if (!isRub && model.RubRatePolicy != RubSettlementRatePolicy.FixedContractRate)
        {
            ModelState.AddModelError(nameof(model.RubRatePolicy),
                "برای ارز تسویهٔ غیر روبل فعلاً فقط «نرخ ثابت برای همین قرارداد» پشتیبانی می‌شود.");
            return;
        }

        if (model.RubRatePolicy == RubSettlementRatePolicy.FixedContractRate
            && (!model.ContractRubPerUsdRate.HasValue || model.ContractRubPerUsdRate.Value <= 0m))
        {
            ModelState.AddModelError(nameof(model.ContractRubPerUsdRate),
                $"برای نرخ ثابت تسویه، مقدار «{model.SettlementCurrencyCode} برای هر 1 USD» را وارد کنید.");
        }
    }

    private async Task<bool> ResolveFixedPricingAsync(Contract contract, ContractFormViewModel model)
    {
        if (contract.PricingMethod != PricingMethod.Fixed)
        {
            return true;
        }

        try
        {
            var conversion = await _currencyConversion.ResolveToBaseAsync(
                contract.Currency,
                contract.ContractDate.Date,
                model.AppliedFxRateToUsd);

            contract.Currency = conversion.SourceCurrencyCode;
            contract.AppliedFxRateToUsd = conversion.AppliedRateToBase;
            contract.UnitPriceInCurrency = model.UnitPriceInCurrency;
            contract.UnitPriceUsd = conversion.ConvertToBase(model.UnitPriceInCurrency!.Value);
            model.AppliedFxRateToUsd = contract.AppliedFxRateToUsd;
            model.UnitPriceUsd = contract.UnitPriceUsd;
            return true;
        }
        catch (BusinessRuleException ex)
        {
            ModelState.AddModelError(nameof(model.AppliedFxRateToUsd), ex.Message);
            return false;
        }
    }

    private static void ApplyContractCleanup(Contract model)
    {
        if (model.ContractType == ContractType.Purchase)
            model.CustomerId = null;
        if (model.ContractType == ContractType.Sale)
            model.SupplierId = null;

        model.BenchmarkCode = string.IsNullOrWhiteSpace(model.BenchmarkCode)
            ? null
            : model.BenchmarkCode.Trim().ToUpperInvariant();
        model.Currency = SystemCurrency.Normalize(model.Currency);
        model.SettlementCurrencyCode = SystemCurrency.Normalize(model.SettlementCurrencyCode);
        model.ContractRubRateSource = string.IsNullOrWhiteSpace(model.ContractRubRateSource)
            ? null
            : model.ContractRubRateSource.Trim();
        if (model.RubRatePolicy == RubSettlementRatePolicy.NotApplicable)
        {
            model.SettlementCurrencyCode = SystemCurrency.BaseCurrencyCode;
            model.RubRatePolicy = RubSettlementRatePolicy.NotApplicable;
            model.ContractRubPerUsdRate = null;
            model.ContractRubRateDate = null;
            model.ContractRubRateSource = null;
        }
        else
        {
            // ارز تسویه پویا: ارز انتخاب‌شده حفظ می‌شود؛ خالی/پایه → RUB پیش‌فرض (سازگاری قبلی).
            model.SettlementCurrencyCode = SystemCurrency.Normalize(model.SettlementCurrencyCode);
            if (SystemCurrency.IsBaseCurrency(model.SettlementCurrencyCode))
            {
                model.SettlementCurrencyCode = "RUB";
            }

            if (model.RubRatePolicy != RubSettlementRatePolicy.FixedContractRate)
            {
                model.ContractRubPerUsdRate = null;
                model.ContractRubRateDate = null;
                model.ContractRubRateSource = null;
            }
        }
        model.Notes = string.IsNullOrWhiteSpace(model.Notes) ? null : model.Notes.Trim();

        if (model.PlattsBasisDate.HasValue)
            model.PlattsBasisDate = NormalizeDate(model.PlattsBasisDate.Value);
        if (model.PlattsBasisMonth.HasValue)
            model.PlattsBasisMonth = NormalizeMonth(model.PlattsBasisMonth.Value);

        if (model.PricingMethod == PricingMethod.Fixed)
        {
            model.PremiumUsd = null;
            model.BenchmarkCode = null;
            model.PlattsPeriodType = null;
            model.PremiumDiscountUsd = null;
            model.PlattsManualPriceUsd = null;
            model.PlattsBasisDate = null;
            model.PlattsBasisMonth = null;
            model.ManualFinalPriceUsd = null;
            return;
        }

        if (model.PricingMethod == PricingMethod.ManualFinalPrice)
        {
            model.Currency = SystemCurrency.BaseCurrencyCode;
            model.UnitPriceInCurrency = null;
            model.AppliedFxRateToUsd = null;
            model.UnitPriceUsd = null;
            model.PremiumUsd = null;
            model.PremiumDiscountUsd = null;
            model.BenchmarkCode = null;
            model.PlattsPeriodType = null;
            model.PlattsManualPriceUsd = null;
            model.PlattsBasisDate = null;
            model.PlattsBasisMonth = null;
            model.PricingFormulaNote = string.IsNullOrWhiteSpace(model.PricingFormulaNote) ? null : model.PricingFormulaNote.Trim();
            return;
        }

        model.Currency = SystemCurrency.BaseCurrencyCode;
        model.UnitPriceInCurrency = null;
        model.AppliedFxRateToUsd = null;
        model.UnitPriceUsd = null;
        model.PremiumUsd = model.PremiumDiscountUsd;
        model.PricingFormulaNote = string.IsNullOrWhiteSpace(model.PricingFormulaNote) ? null : model.PricingFormulaNote.Trim();

        switch (model.PlattsPeriodType)
        {
            case PlattsPeriodType.Daily:
                model.PlattsManualPriceUsd = null;
                model.PlattsBasisMonth = null;
                break;
            case PlattsPeriodType.Monthly:
                model.PlattsManualPriceUsd = null;
                model.PlattsBasisDate = null;
                break;
            case PlattsPeriodType.Manual:
                model.PlattsBasisDate = null;
                model.PlattsBasisMonth = null;
                break;
        }
    }

    private static ContractFormViewModel BuildFormModel(Contract contract)
    {
        var model = new ContractFormViewModel
        {
            Id = contract.Id,
            ContractNumber = contract.ContractNumber,
            ContractType = contract.ContractType,
            Status = contract.Status,
            ParentContractId = contract.ParentContractId,
            CompanyId = contract.CompanyId,
            ProductId = contract.ProductId,
            UnitId = contract.UnitId ?? contract.Product?.UnitId,
            SupplierId = contract.SupplierId,
            CustomerId = contract.CustomerId,
            DestinationLocationId = contract.DestinationLocationId,
            OwnershipType = contract.OwnershipType,
            ContractDate = contract.ContractDate,
            StartDate = contract.StartDate,
            EndDate = contract.EndDate,
            PricingMethod = contract.PricingMethod,
            UiPricingType = ContractPricingAdapter.GetUiPricingType(contract),
            PlattsUiMode = ContractPricingAdapter.GetPlattsUiMode(contract),
            QuantityMt = contract.QuantityMt,
            Currency = contract.PricingMethod == PricingMethod.Fixed
                ? contract.Currency
                : SystemCurrency.BaseCurrencyCode,
            SettlementCurrencyCode = contract.SettlementCurrencyCode,
            RubRatePolicy = contract.RubRatePolicy,
            ContractRubPerUsdRate = contract.ContractRubPerUsdRate,
            ContractRubRateDate = contract.ContractRubRateDate,
            ContractRubRateSource = contract.ContractRubRateSource,
            UnitPriceInCurrency = contract.UnitPriceInCurrency ?? contract.UnitPriceUsd,
            AppliedFxRateToUsd = contract.AppliedFxRateToUsd,
            UnitPriceUsd = contract.UnitPriceUsd,
            BenchmarkCode = contract.BenchmarkCode,
            PlattsPeriodType = contract.PlattsPeriodType,
            PremiumDiscountUsd = contract.PremiumDiscountUsd ?? contract.PremiumUsd,
            PlattsManualPriceUsd = contract.PlattsManualPriceUsd,
            PlattsBasisDate = contract.PlattsBasisDate,
            PlattsBasisMonth = contract.PlattsBasisMonth,
            MinimumPriceUsd = contract.MinimumPriceUsd,
            ManualFinalPriceUsd = contract.ManualFinalPriceUsd,
            FinalPriceUsdPerMt = ContractPricingAdapter.GetCanonicalFinalPrice(contract),
            PricingFormulaNote = contract.PricingFormulaNote,
            PricingNote = contract.PricingFormulaNote,
            IsPricingCompleted = ContractPricingAdapter.GetPricingStatus(contract) == PricingCompletionStatus.Completed,
            Notes = contract.Notes,
            CompanyName = contract.Company?.Name,
            ProductName = contract.Product?.Name,
            UnitName = contract.Unit?.NamePersian ?? contract.Unit?.Name,
            CounterpartyName = contract.ContractType == ContractType.Purchase
                ? contract.Supplier?.Name
                : contract.Customer?.Name,
            PartnerShares = contract.ContractPartners
                .OrderBy(cp => cp.Partner?.Code)
                .Select(cp => new ContractPartnerShareInput
                {
                    PartnerId = cp.PartnerId,
                    SharePercent = cp.SharePercent
                })
                .ToList()
        };

        return model;
    }

    private static void HydrateSummaryFields(ContractFormViewModel model, Contract contract)
    {
        model.CompanyName = contract.Company?.Name;
        model.ProductName = contract.Product?.Name;
        model.UnitName = contract.Unit?.NamePersian ?? contract.Unit?.Name;
        model.CounterpartyName = contract.ContractType == ContractType.Purchase
            ? contract.Supplier?.Name
            : contract.Customer?.Name;
    }

    private static string BuildPartnerSummary(IEnumerable<ContractPartner> partners)
        => string.Join(" | ", partners
            .OrderBy(p => p.Partner?.Code ?? p.PartnerId.ToString())
            .Select(p => $"{p.Partner?.Code ?? p.PartnerId.ToString()}: {p.SharePercent:N2}%"));

    private static string BuildPartnerSummary(IEnumerable<ContractPartnerShareInput> partners)
        => string.Join(" | ", partners
            .OrderBy(p => p.PartnerId)
            .Select(p => $"{p.PartnerId}: {p.SharePercent:N2}%"));

    private static void EnsurePartnerRows(ContractFormViewModel model)
    {
        model.PartnerShares ??= [];
        if (!model.PartnerShares.Any())
        {
            model.PartnerShares.Add(new ContractPartnerShareInput());
        }
    }

    private static void NormalizeFormModel(ContractFormViewModel model)
    {
        model.ContractNumber = (model.ContractNumber ?? string.Empty).Trim().ToUpperInvariant();
        model.Currency = SystemCurrency.Normalize(model.Currency);
        model.SettlementCurrencyCode = SystemCurrency.Normalize(model.SettlementCurrencyCode);
        model.ContractRubRateSource = string.IsNullOrWhiteSpace(model.ContractRubRateSource)
            ? null
            : model.ContractRubRateSource.Trim();
        model.BenchmarkCode = string.IsNullOrWhiteSpace(model.BenchmarkCode)
            ? null
            : model.BenchmarkCode.Trim().ToUpperInvariant();
        model.Notes = string.IsNullOrWhiteSpace(model.Notes) ? null : model.Notes.Trim();

        if (model.StartDate.HasValue)
            model.StartDate = NormalizeDate(model.StartDate.Value);
        if (model.EndDate.HasValue)
            model.EndDate = NormalizeDate(model.EndDate.Value);
        model.ContractDate = NormalizeDate(model.ContractDate);
        if (model.PlattsBasisDate.HasValue)
            model.PlattsBasisDate = NormalizeDate(model.PlattsBasisDate.Value);
        if (model.PlattsBasisMonth.HasValue)
            model.PlattsBasisMonth = NormalizeMonth(model.PlattsBasisMonth.Value);
        if (model.ContractRubRateDate.HasValue)
            model.ContractRubRateDate = NormalizeDate(model.ContractRubRateDate.Value);

        NormalizePricingUiFields(model);
        NormalizeRubSettlementFields(model);
    }

    private static void NormalizeRubSettlementFields(ContractFormViewModel model)
    {
        if (model.RubRatePolicy == RubSettlementRatePolicy.NotApplicable)
        {
            model.SettlementCurrencyCode = SystemCurrency.BaseCurrencyCode;
            model.RubRatePolicy = RubSettlementRatePolicy.NotApplicable;
            model.ContractRubPerUsdRate = null;
            model.ContractRubRateDate = null;
            model.ContractRubRateSource = null;
            return;
        }

        // ارز تسویه پویا است: ارز انتخاب‌شده توسط کاربر حفظ می‌شود.
        // اگر ارزی انتخاب نشده یا برابر ارز پایه (USD) بود، برای سازگاری با رفتار قبلی به RUB پیش‌فرض می‌شود.
        model.SettlementCurrencyCode = SystemCurrency.Normalize(model.SettlementCurrencyCode);
        if (SystemCurrency.IsBaseCurrency(model.SettlementCurrencyCode))
        {
            model.SettlementCurrencyCode = "RUB";
        }

        if (model.RubRatePolicy != RubSettlementRatePolicy.FixedContractRate)
        {
            model.ContractRubPerUsdRate = null;
            model.ContractRubRateDate = null;
            model.ContractRubRateSource = null;
        }
    }

    private static void NormalizePricingUiFields(ContractFormViewModel model)
    {
        if (model.PricingMethod == PricingMethod.FormulaPlatts || model.UiPricingType == UiPricingType.Platts)
        {
            model.UiPricingType = UiPricingType.Platts;
            model.PricingMethod = PricingMethod.FormulaPlatts;
            if (!model.PlattsPeriodType.HasValue)
            {
                model.PlattsPeriodType = ContractPricingAdapter.ToPlattsPeriodType(model.PlattsUiMode);
            }
            else
            {
                model.PlattsUiMode = model.PlattsPeriodType.Value switch
                {
                    PlattsPeriodType.Daily => PlattsUiMode.Daily,
                    PlattsPeriodType.Monthly => PlattsUiMode.MonthlyAverage,
                    _ => PlattsUiMode.ManualDescriptive
                };
            }
        }
        else
        {
            model.UiPricingType = UiPricingType.Agreed;
            if (model.PricingMethod != PricingMethod.Fixed)
            {
                model.PricingMethod = PricingMethod.ManualFinalPrice;
            }
        }

        var finalPrice = model.FinalPriceUsdPerMt ?? model.ManualFinalPriceUsd;
        model.FinalPriceUsdPerMt = finalPrice;
        model.ManualFinalPriceUsd = finalPrice;

        var note = string.IsNullOrWhiteSpace(model.PricingNote)
            ? model.PricingFormulaNote
            : model.PricingNote;
        model.PricingNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        model.PricingFormulaNote = model.PricingNote;
        model.IsPricingCompleted = finalPrice.HasValue && finalPrice.Value > 0m;
    }

    private static DateTime NormalizeDate(DateTime value)
        => new(value.Year, value.Month, value.Day, 0, 0, 0, DateTimeKind.Utc);

    private static DateTime NormalizeMonth(DateTime value)
        => new(value.Year, value.Month, 1, 0, 0, 0, DateTimeKind.Utc);
}
