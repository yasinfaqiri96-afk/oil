using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Helpers;
using PTGOilSystem.Web.Infrastructure.RateLimiting;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.PartyStatements;
using PTGOilSystem.Web.Services.PartyStatements;

namespace PTGOilSystem.Web.Controllers;

[Authorize]
public sealed class PartyStatementsController : Controller
{
    private readonly IPartyStatementReadService _statementService;
    private readonly ApplicationDbContext _db;
    private readonly Services.Exports.ITabularExportService? _exportService;

    public PartyStatementsController(
        IPartyStatementReadService statementService,
        ApplicationDbContext db,
        Services.Exports.ITabularExportService? exportService = null)
    {
        _statementService = statementService;
        _db = db;
        _exportService = exportService;
    }

    [HttpGet("Customers/{id:int}/Statement")]
    public Task<IActionResult> Customer(int id, [FromQuery] PartyStatementFilter filter, bool print = false, CancellationToken ct = default)
        => RenderAsync(PartyStatementPartyType.Customer, id, filter, print, ct);

    [HttpGet("Suppliers/{id:int}/Statement")]
    public Task<IActionResult> Supplier(
        int id,
        [FromQuery] PartyStatementFilter filter,
        SupplierStatementView view = SupplierStatementView.Contracts,
        bool print = false,
        CancellationToken ct = default)
        => RenderAsync(PartyStatementPartyType.Supplier, id, filter, print, ct, view);

    // جزئیات یک قرارداد در نمای «قراردادها» — با کلیک (lazy) بارگذاری می‌شود؛
    // از همان سرویس موجود استفاده می‌کند و سرویس/کوئری موازی نمی‌سازد.
    [HttpGet("Suppliers/{id:int}/Statement/Contract/{contractId:int}")]
    public async Task<IActionResult> SupplierContractDetails(
        int id,
        int contractId,
        [FromQuery] PartyStatementFilter filter,
        CancellationToken ct = default)
    {
        var effective = WithContract(WithOperationalColumns(filter), contractId);
        try
        {
            var statement = await _statementService.GetStatementAsync(
                new PartyRef(PartyStatementPartyType.Supplier, id, filter.CompanyId), effective, ct);
            var facts = await LoadContractFactsAsync([contractId], ct);
            facts.TryGetValue(contractId, out var f);
            return PartialView("_SupplierContractDetails", new SupplierContractDetailsViewModel
            {
                Statement = statement,
                ProductName = f?.ProductName,
                ContractQuantityMt = f?.ContractQuantityMt,
                UnitPriceUsd = f?.UnitPriceUsd,
                ContractValueUsd = f?.ContractValueUsd,
                LoadedQuantityMt = f?.LoadedQuantityMt
            });
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    // خروجی رسمی تأمین‌کننده: PDF واقعی (QuestPDF) و Excel واقعی (XLSX دو شیت) از همان
    // داده و فیلترهای صفحه. CSV سبک قبلی هم برای سازگاری باقی می‌ماند.
    [EnableRateLimiting(RateLimitPolicies.CsvExport)]
    [HttpGet("Suppliers/{id:int}/Statement/Export")]
    public async Task<IActionResult> SupplierExport(
        int id,
        string? format,
        [FromQuery] PartyStatementFilter filter,
        CancellationToken ct = default)
    {
        if (_exportService is null)
        {
            return NotFound();
        }

        PartyStatementResult statement;
        try
        {
            // ستون‌های عملیاتی برای شیتِ جزئیات لازم است.
            statement = await _statementService.GetStatementAsync(
                new PartyRef(PartyStatementPartyType.Supplier, id, filter.CompanyId),
                WithOperationalColumns(filter),
                ct);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }

        var grouping = await BuildContractGroupingAsync(statement, ct);

        // PDF تب «قراردادها» باید همان سند رسمی صورت‌حساب باشد (لوگو، سربرگ شرکت، خلاصهٔ
        // مالی، امضا) و فقط جدولش خلاصهٔ قراردادی باشد؛ نه قالب جدول عمومیِ خروجی‌ها.
        // مسیر Excel/CSV بدون تغییر می‌ماند.
        if (Services.Exports.TabularExportSupport.ParseFormat(format) == Services.Exports.TabularExportFormat.Pdf)
        {
            await using var output = new MemoryStream();
            await _exportService.WriteSupplierContractStatementPdfAsync(statement, grouping, UiText.IsEn(HttpContext), output, ct);
            var fileName = $"statement-supplier-{id}-contracts-{DateTime.UtcNow:yyyyMMdd}.pdf";
            return File(output.ToArray(), "application/pdf", fileName);
        }

        return SupplierStatementExport.Build(this, format, statement, grouping);
    }

    [HttpGet("ServiceProviders/{id:int}/Statement")]
    public Task<IActionResult> ServiceProvider(int id, [FromQuery] PartyStatementFilter filter, bool print = false, CancellationToken ct = default)
        => RenderAsync(PartyStatementPartyType.ServiceProvider, id, filter, print, ct);

    [HttpGet("Sarrafs/{id:int}/Statement")]
    public Task<IActionResult> Sarraf(int id, [FromQuery] PartyStatementFilter filter, bool print = false, CancellationToken ct = default)
        => RenderAsync(PartyStatementPartyType.Sarraf, id, filter, print, ct);

    [HttpGet("Employees/{id:int}/Statement")]
    public Task<IActionResult> Employee(int id, [FromQuery] PartyStatementFilter filter, bool print = false, CancellationToken ct = default)
        => RenderAsync(PartyStatementPartyType.Employee, id, filter, print, ct);

    [HttpGet("Partners/{id:int}/Statement")]
    public Task<IActionResult> Partner(int id, [FromQuery] PartyStatementFilter filter, bool print = false, CancellationToken ct = default)
        => RenderAsync(PartyStatementPartyType.Partner, id, filter, print, ct);

    [HttpGet("Drivers/{id:int}/Statement")]
    public Task<IActionResult> Driver(int id, [FromQuery] PartyStatementFilter filter, bool print = false, CancellationToken ct = default)
        => RenderAsync(PartyStatementPartyType.Driver, id, filter, print, ct);

    [HttpGet("Companies/{id:int}/Statement")]
    public Task<IActionResult> Company(int id, [FromQuery] PartyStatementFilter filter, bool print = false, CancellationToken ct = default)
        => RenderAsync(PartyStatementPartyType.Company, id, filter, print, ct);

    [HttpGet("PartyStatements/{partyType}/{id:int}")]
    public Task<IActionResult> Document(PartyStatementPartyType partyType, int id, [FromQuery] PartyStatementFilter filter, bool print = false, CancellationToken ct = default)
        => RenderAsync(partyType, id, filter, print, ct);

    [EnableRateLimiting(RateLimitPolicies.CsvExport)]
    [HttpGet("PartyStatements/{partyType}/{id:int}/Csv")]
    public async Task<IActionResult> Csv(
        PartyStatementPartyType partyType,
        int id,
        [FromQuery] PartyStatementFilter filter,
        CancellationToken ct = default)
    {
        PartyStatementResult statement;
        try
        {
            statement = await _statementService.GetStatementAsync(new PartyRef(partyType, id, filter.CompanyId), filter, ct);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }

        var headers = BuildCsvHeaders(statement.ColumnOptions);
        var rows = statement.Rows.Select(row => BuildCsvRow(row, statement.ColumnOptions));
        var fileName = $"statement-{partyType.ToString().ToLowerInvariant()}-{id}-{DateTime.UtcNow:yyyyMMdd}.csv";
        return CsvExportSupport.File(this, fileName, headers, rows);
    }

    [EnableRateLimiting(RateLimitPolicies.CsvExport)]
    [HttpGet("PartyStatements/{partyType}/{id:int}/Pdf")]
    public async Task<IActionResult> Pdf(
        PartyStatementPartyType partyType,
        int id,
        [FromQuery] PartyStatementFilter filter,
        CancellationToken ct = default)
    {
        if (_exportService is null)
            return NotFound();

        PartyStatementResult statement;
        try
        {
            statement = await _statementService.GetStatementAsync(
                new PartyRef(partyType, id, filter.CompanyId), filter, ct);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }

        await using var output = new MemoryStream();
        await _exportService.WritePartyStatementPdfAsync(statement, UiText.IsEn(HttpContext), output, ct);
        var fileName = $"statement-{partyType.ToString().ToLowerInvariant()}-{id}-{DateTime.UtcNow:yyyyMMdd}.pdf";
        return File(output.ToArray(), "application/pdf", fileName);
    }

    private async Task<IActionResult> RenderAsync(
        PartyStatementPartyType partyType,
        int id,
        PartyStatementFilter filter,
        bool print,
        CancellationToken ct,
        SupplierStatementView? supplierView = null)
    {
        // فقط تأمین‌کننده تب سه‌گانه دارد؛ سایر طرف‌حساب‌ها همیشه نمای خطی (Ledger).
        var view = partyType == PartyStatementPartyType.Supplier
            ? supplierView ?? SupplierStatementView.Contracts
            : SupplierStatementView.Ledger;

        // نمای «بارگیری‌ها» به ستون‌های عملیاتی نیاز دارد؛ فیلتر مؤثر را می‌سازیم.
        var effectiveFilter = view == SupplierStatementView.Loadings
            ? WithOperationalColumns(filter)
            : filter;

        try
        {
            return await BuildDocumentAsync(partyType, id, effectiveFilter, print, view, ct);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (ArgumentException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            var fallback = new PartyStatementFilter
            {
                FromDate = null,
                ToDate = effectiveFilter.ToDate,
                ContractId = effectiveFilter.ContractId,
                CompanyId = effectiveFilter.CompanyId,
                CurrencyCode = effectiveFilter.CurrencyCode,
                IncludeOperationalColumns = effectiveFilter.IncludeOperationalColumns
            };
            return await BuildDocumentAsync(partyType, id, fallback, print, view, ct);
        }
    }

    private async Task<IActionResult> BuildDocumentAsync(
        PartyStatementPartyType partyType,
        int id,
        PartyStatementFilter filter,
        bool print,
        SupplierStatementView view,
        CancellationToken ct)
    {
        var statement = await _statementService.GetStatementAsync(new PartyRef(partyType, id, filter.CompanyId), filter, ct);
        var options = await LoadFilterOptionsAsync(partyType, id, ct);

        SupplierContractStatementViewModel? grouping = null;
        if (partyType == PartyStatementPartyType.Supplier && view == SupplierStatementView.Contracts)
        {
            grouping = await BuildContractGroupingAsync(statement, ct);
        }

        return View("Document", new PartyStatementViewModel
        {
            Statement = statement,
            Filter = filter,
            IsPrintMode = print,
            SupplierView = view,
            ContractGrouping = grouping,
            ContractOptions = options.Contracts,
            CompanyOptions = options.Companies,
            CurrencyOptions = options.Currencies
        });
    }

    // اطلاعات نمایشیِ قرارداد (محصول/مقدار/نرخ/ارزش/بارگیری‌شده) را برای نمای «قراردادها»
    // بارگذاری می‌کند. صرفاً metadata است؛ اعداد رسید/برد/بیلانس از خودِ سطرهای مالی می‌آیند.
    private async Task<SupplierContractStatementViewModel> BuildContractGroupingAsync(
        PartyStatementResult statement,
        CancellationToken ct)
    {
        var contractIds = statement.Rows
            .Where(r => !r.IsOpeningBalance && r.ContractId.HasValue)
            .Select(r => r.ContractId!.Value)
            .Distinct()
            .ToList();

        var facts = await LoadContractFactsAsync(contractIds, ct);
        return SupplierContractStatementBuilder.Build(statement, facts);
    }

    // metadataِ نمایشیِ قرارداد (محصول/مقدار/نرخ/ارزش/بارگیری‌شده). فقط اطلاعاتی است؛ روی
    // رسید/برد/بیلانس اثری ندارد. با دو کوئری projection (بدون N+1) خوانده می‌شود.
    private async Task<Dictionary<int, SupplierContractStatementBuilder.ContractFacts>> LoadContractFactsAsync(
        IReadOnlyCollection<int> contractIds,
        CancellationToken ct)
    {
        var facts = new Dictionary<int, SupplierContractStatementBuilder.ContractFacts>();
        if (contractIds.Count == 0)
        {
            return facts;
        }

        var contracts = await _db.Contracts.AsNoTracking()
            .Where(c => contractIds.Contains(c.Id))
            .Select(c => new
            {
                c.Id,
                ProductName = c.Product != null ? (c.Product.NamePersian ?? c.Product.Name) : null,
                c.QuantityMt,
                c.ManualFinalPriceUsd,
                c.PricingMethod,
                c.UnitPriceUsd
            })
            .ToListAsync(ct);

        var loadedByContract = await _db.LoadingRegisters.AsNoTracking()
            .Where(l => contractIds.Contains(l.ContractId))
            .GroupBy(l => l.ContractId)
            .Select(g => new { ContractId = g.Key, Qty = g.Sum(x => x.LoadedQuantityMt) })
            .ToDictionaryAsync(x => x.ContractId, x => x.Qty, ct);

        foreach (var c in contracts)
        {
            // نرخ قطعیِ قرارداد را از همان helper مرکزی می‌گیریم تا با بقیهٔ سیستم یکسان بماند.
            var price = ContractPricingAdapter.GetCanonicalFinalPrice(new Contract
            {
                ManualFinalPriceUsd = c.ManualFinalPriceUsd,
                PricingMethod = c.PricingMethod,
                UnitPriceUsd = c.UnitPriceUsd
            });
            var contractValue = price.HasValue ? c.QuantityMt * price.Value : (decimal?)null;
            loadedByContract.TryGetValue(c.Id, out var loaded);

            facts[c.Id] = new SupplierContractStatementBuilder.ContractFacts(
                c.ProductName,
                c.QuantityMt,
                price,
                contractValue,
                loadedByContract.ContainsKey(c.Id) ? loaded : null);
        }

        return facts;
    }

    private static PartyStatementFilter WithOperationalColumns(PartyStatementFilter filter)
        => new()
        {
            FromDate = filter.FromDate,
            ToDate = filter.ToDate,
            ContractId = filter.ContractId,
            CompanyId = filter.CompanyId,
            CurrencyCode = filter.CurrencyCode,
            IncludeOperationalColumns = true
        };

    private static PartyStatementFilter WithContract(PartyStatementFilter filter, int contractId)
        => new()
        {
            FromDate = filter.FromDate,
            ToDate = filter.ToDate,
            ContractId = contractId,
            CompanyId = filter.CompanyId,
            CurrencyCode = filter.CurrencyCode,
            IncludeOperationalColumns = filter.IncludeOperationalColumns
        };

    // گزینه‌های نوار فیلتر: قراردادهای مرتبط با همین طرف‌حساب، شرکت‌ها و ارزهای فعال.
    private async Task<(List<PartyStatementFilterOption> Contracts,
        List<PartyStatementFilterOption> Companies,
        List<string> Currencies)> LoadFilterOptionsAsync(
        PartyStatementPartyType partyType,
        int id,
        CancellationToken ct)
    {
        var contractQuery = _db.Contracts.AsNoTracking().AsQueryable();
        contractQuery = partyType switch
        {
            PartyStatementPartyType.Supplier => contractQuery.Where(x => x.SupplierId == id),
            PartyStatementPartyType.Customer => contractQuery.Where(x => x.CustomerId == id),
            PartyStatementPartyType.Company => contractQuery.Where(x => x.CompanyId == id),
            _ => contractQuery
        };

        var contracts = await contractQuery
            .OrderByDescending(x => x.ContractDate)
            .Select(x => new PartyStatementFilterOption(x.Id, x.ContractNumber))
            .Take(500)
            .ToListAsync(ct);

        var companies = await _db.Companies.AsNoTracking()
            .Where(x => x.IsActive)
            .OrderBy(x => x.Name)
            .Select(x => new PartyStatementFilterOption(x.Id, x.NamePersian ?? x.Name))
            .ToListAsync(ct);

        var currencies = await _db.Currencies.AsNoTracking()
            .Where(x => x.IsActive)
            .OrderBy(x => x.Code)
            .Select(x => x.Code)
            .ToListAsync(ct);

        return (contracts, companies, currencies);
    }

    private static string[] BuildCsvHeaders(PartyStatementColumnOptions columns)
    {
        var headers = new List<string> { "No", "Date", "Reference", "Description" };
        if (columns.ShowRub) headers.Add("RUB");
        if (columns.ShowAed) headers.Add("AED");
        if (columns.ShowOriginalAmount) headers.AddRange(["OriginalAmount", "Currency"]);
        if (columns.ShowFxRate) headers.Add("ExchangeRate");
        if (columns.ShowQuantity) headers.Add("M-Tone");
        if (columns.ShowPlatts) headers.Add("Platts");
        if (columns.ShowPremiumOrDiscount) headers.Add("PremiumDiscount");
        if (columns.ShowUnitPrice) headers.Add("UnitPrice");
        headers.AddRange(["ReceiptUsd", "OutflowUsd", "BalanceUsd"]);
        return headers.ToArray();
    }

    private static string?[] BuildCsvRow(PartyStatementRow row, PartyStatementColumnOptions columns)
    {
        var values = new List<string?>
        {
            row.IsOpeningBalance ? "" : row.Sequence.ToString(),
            CsvExportSupport.Date(row.Date),
            row.Reference,
            row.Description
        };
        if (columns.ShowRub) values.Add(IsCurrency(row, "RUB") ? CsvExportSupport.Decimal(row.OriginalAmount) : "");
        if (columns.ShowAed) values.Add(IsCurrency(row, "AED") ? CsvExportSupport.Decimal(row.OriginalAmount) : "");
        if (columns.ShowOriginalAmount)
        {
            values.Add(row.OriginalCurrency is not "USD" and not "RUB" and not "AED" ? CsvExportSupport.Decimal(row.OriginalAmount) : "");
            values.Add(row.OriginalCurrency is not "USD" and not "RUB" and not "AED" ? row.OriginalCurrency : "");
        }
        if (columns.ShowFxRate) values.Add(row.FxRateDisplay ?? (row.OriginalCurrency == "USD" ? "1" : "Exchange rate not recorded"));
        if (columns.ShowQuantity) values.Add(CsvExportSupport.Decimal(row.Quantity));
        if (columns.ShowPlatts) values.Add(CsvExportSupport.Decimal(row.PlattsPrice));
        if (columns.ShowPremiumOrDiscount) values.Add(CsvExportSupport.Decimal(row.PremiumOrDiscount));
        if (columns.ShowUnitPrice) values.Add(CsvExportSupport.Decimal(row.UnitPrice));
        values.Add(CsvExportSupport.Decimal(row.ReceiptBase));
        values.Add(CsvExportSupport.Decimal(row.OutflowBase));
        values.Add(CsvExportSupport.Decimal(row.RunningBalance));
        return values.ToArray();
    }

    private static bool IsCurrency(PartyStatementRow row, string currency)
        => string.Equals(row.OriginalCurrency, currency, StringComparison.OrdinalIgnoreCase);
}
