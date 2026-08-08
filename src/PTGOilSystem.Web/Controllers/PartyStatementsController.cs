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
    public Task<IActionResult> Customer(
        int id,
        [FromQuery] PartyStatementFilter filter,
        bool print = false,
        SupplierStatementView view = SupplierStatementView.Ledger,
        CancellationToken ct = default)
        => RenderAsync(PartyStatementPartyType.Customer, id, filter, print, ct, view);

    [HttpGet("Suppliers/{id:int}/Statement")]
    public Task<IActionResult> Supplier(
        int id,
        [FromQuery] PartyStatementFilter filter,
        SupplierStatementView view = SupplierStatementView.Ledger,
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
        int detailPage = 1,
        CancellationToken ct = default)
        => await ContractDetailsCore(
            PartyStatementPartyType.Supplier,
            id,
            contractId,
            filter,
            detailPage,
            ct);

    [HttpGet("PartyStatements/{partyType}/{id:int}/Contract/{contractId:int}")]
    public Task<IActionResult> ContractDetails(
        PartyStatementPartyType partyType,
        int id,
        int contractId,
        [FromQuery] PartyStatementFilter filter,
        int detailPage = 1,
        CancellationToken ct = default)
        => ContractDetailsCore(partyType, id, contractId, filter, detailPage, ct);

    private async Task<IActionResult> ContractDetailsCore(
        PartyStatementPartyType partyType,
        int id,
        int contractId,
        PartyStatementFilter filter,
        int detailPage,
        CancellationToken ct)
    {
        var effective = WithContract(WithOperationalColumns(filter), contractId);
        try
        {
            var statement = await _statementService.GetStatementAsync(
                new PartyRef(partyType, id, filter.CompanyId), effective, ct);
            var facts = await LoadContractFactsAsync([contractId], ct);
            facts.TryGetValue(contractId, out var f);
            var detailRows = statement.Rows.Where(r => !r.IsOpeningBalance).ToList();
            const int detailPageSize = 25;
            var detailTotalPages = Math.Max(1, (int)Math.Ceiling(detailRows.Count / (decimal)detailPageSize));
            var safePage = Math.Clamp(detailPage, 1, detailTotalPages);
            return PartialView("_SupplierContractDetails", new SupplierContractDetailsViewModel
            {
                Statement = statement,
                PartyType = partyType,
                PartyId = id,
                ContractId = contractId,
                ProductName = f?.ProductName,
                ContractQuantityMt = f?.ContractQuantityMt,
                UnitPriceUsd = f?.UnitPriceUsd,
                ContractValueUsd = f?.ContractValueUsd,
                LoadedQuantityMt = f?.LoadedQuantityMt,
                DetailRows = detailRows.Skip((safePage - 1) * detailPageSize).Take(detailPageSize).ToList(),
                LoadingRows = detailRows
                    .Where(IsConfirmedOperation)
                    .Skip((safePage - 1) * detailPageSize)
                    .Take(detailPageSize)
                    .ToList(),
                DetailPage = safePage,
                DetailPageSize = detailPageSize,
                DetailTotalRows = detailRows.Count
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
            statement = await _statementService.GetStatementAsync(
                new PartyRef(PartyStatementPartyType.Supplier, id, filter.CompanyId),
                filter,
                ct);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }

        var grouping = await BuildContractGroupingAsync(statement, filter, ct);

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

        return SupplierStatementExport.Build(this, format, statement, grouping, includeDetails: false);
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
    public Task<IActionResult> Company(
        int id,
        [FromQuery] PartyStatementFilter filter,
        bool print = false,
        SupplierStatementView view = SupplierStatementView.Ledger,
        CancellationToken ct = default)
        => RenderAsync(PartyStatementPartyType.Company, id, filter, print, ct, view);

    [HttpGet("PartyStatements/{partyType}/{id:int}")]
    public Task<IActionResult> Document(
        PartyStatementPartyType partyType,
        int id,
        [FromQuery] PartyStatementFilter filter,
        bool print = false,
        SupplierStatementView? view = null,
        CancellationToken ct = default)
        => RenderAsync(partyType, id, filter, print, ct, view);

    [EnableRateLimiting(RateLimitPolicies.CsvExport)]
    [HttpGet("PartyStatements/{partyType}/{id:int}/Csv")]
    public async Task<IActionResult> Csv(
        PartyStatementPartyType partyType,
        int id,
        [FromQuery] PartyStatementFilter filter,
        SupplierStatementView? view = null,
        CancellationToken ct = default)
    {
        var effectiveView = UsesContractSummary(partyType)
            ? view ?? SupplierStatementView.Ledger
            : SupplierStatementView.Ledger;
        var effectiveFilter = effectiveView == SupplierStatementView.Loadings
            ? WithOperationalColumns(filter)
            : filter;
        PartyStatementResult statement;
        try
        {
            statement = await _statementService.GetStatementAsync(
                new PartyRef(partyType, id, filter.CompanyId),
                effectiveFilter,
                ct);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }

        string[] headers;
        IEnumerable<string?[]> rows;
        if (UsesContractSummary(partyType)
            && effectiveView is SupplierStatementView.Contracts or SupplierStatementView.Loadings)
        {
            var grouping = await BuildContractGroupingAsync(statement, effectiveFilter, ct);
            headers = BuildContractCsvHeaders(statement.Summary.BaseCurrencyCode);
            rows = grouping.Rows.Select(row => BuildContractCsvRow(row, grouping.IsRub));
        }
        else
        {
            var isRub = statement.Summary.IsRubPresentation;
            headers = BuildCsvHeaders(statement.ColumnOptions, isRub ? "RUB" : statement.Summary.BaseCurrencyCode);
            rows = statement.Rows.Select(row => BuildCsvRow(row, statement.ColumnOptions, isRub));
        }
        var fileName = $"statement-{partyType.ToString().ToLowerInvariant()}-{id}-{DateTime.UtcNow:yyyyMMdd}.csv";
        return CsvExportSupport.File(this, fileName, headers, rows);
    }

    [EnableRateLimiting(RateLimitPolicies.CsvExport)]
    [HttpGet("PartyStatements/{partyType}/{id:int}/Pdf")]
    public async Task<IActionResult> Pdf(
        PartyStatementPartyType partyType,
        int id,
        [FromQuery] PartyStatementFilter filter,
        SupplierStatementView? view = null,
        CancellationToken ct = default)
    {
        if (_exportService is null)
            return NotFound();

        var effectiveView = UsesContractSummary(partyType)
            ? view ?? SupplierStatementView.Ledger
            : SupplierStatementView.Ledger;
        var effectiveFilter = effectiveView == SupplierStatementView.Loadings
            ? WithOperationalColumns(filter)
            : filter;
        PartyStatementResult statement;
        try
        {
            statement = await _statementService.GetStatementAsync(
                new PartyRef(partyType, id, filter.CompanyId), effectiveFilter, ct);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }

        await using var output = new MemoryStream();
        if (UsesContractSummary(partyType)
            && effectiveView is SupplierStatementView.Contracts or SupplierStatementView.Loadings)
        {
            var grouping = await BuildContractGroupingAsync(statement, effectiveFilter, ct);
            await _exportService.WriteSupplierContractStatementPdfAsync(
                statement,
                grouping,
                UiText.IsEn(HttpContext),
                output,
                ct);
        }
        else
        {
            await _exportService.WritePartyStatementPdfAsync(statement, UiText.IsEn(HttpContext), output, ct);
        }
        var fileName = $"statement-{partyType.ToString().ToLowerInvariant()}-{id}-{DateTime.UtcNow:yyyyMMdd}.pdf";
        return File(output.ToArray(), "application/pdf", fileName);
    }

    [EnableRateLimiting(RateLimitPolicies.CsvExport)]
    [HttpGet("PartyStatements/{partyType}/{id:int}/Export")]
    public async Task<IActionResult> Export(
        PartyStatementPartyType partyType,
        int id,
        string? format,
        [FromQuery] PartyStatementFilter filter,
        SupplierStatementView? view = null,
        CancellationToken ct = default)
    {
        if (_exportService is null)
        {
            return NotFound();
        }

        var effectiveView = UsesContractSummary(partyType)
            ? view ?? SupplierStatementView.Ledger
            : SupplierStatementView.Ledger;
        var effectiveFilter = effectiveView == SupplierStatementView.Loadings
            ? WithOperationalColumns(filter)
            : filter;
        PartyStatementResult statement;
        try
        {
            statement = await _statementService.GetStatementAsync(
                new PartyRef(partyType, id, filter.CompanyId),
                effectiveFilter,
                ct);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }

        if (UsesContractSummary(partyType)
            && effectiveView is SupplierStatementView.Contracts or SupplierStatementView.Loadings)
        {
            var grouping = await BuildContractGroupingAsync(statement, effectiveFilter, ct);
            if (Services.Exports.TabularExportSupport.ParseFormat(format) == Services.Exports.TabularExportFormat.Pdf)
            {
                await using var output = new MemoryStream();
                await _exportService.WriteSupplierContractStatementPdfAsync(
                    statement,
                    grouping,
                    UiText.IsEn(HttpContext),
                    output,
                    ct);
                return File(
                    output.ToArray(),
                    "application/pdf",
                    $"statement-{partyType.ToString().ToLowerInvariant()}-{id}-summary-{DateTime.UtcNow:yyyyMMdd}.pdf");
            }
            return SupplierStatementExport.Build(this, format, statement, grouping, includeDetails: false);
        }

        return SupplierStatementExport.BuildDetailsOnly(this, format, statement);
    }

    private async Task<IActionResult> RenderAsync(
        PartyStatementPartyType partyType,
        int id,
        PartyStatementFilter filter,
        bool print,
        CancellationToken ct,
        SupplierStatementView? supplierView = null)
    {
        var view = UsesContractSummary(partyType)
            ? supplierView ?? SupplierStatementView.Ledger
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
                SourceType = effectiveFilter.SourceType,
                Search = effectiveFilter.Search,
                Page = 1,
                PageSize = effectiveFilter.PageSize,
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
        if (UsesContractSummary(partyType)
            && view is SupplierStatementView.Contracts or SupplierStatementView.Loadings)
        {
            grouping = await BuildContractGroupingAsync(statement, filter, ct);
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
        PartyStatementFilter filter,
        CancellationToken ct)
    {
        var contractIds = statement.Rows
            .Where(r => !r.IsOpeningBalance && r.ContractId.HasValue)
            .Select(r => r.ContractId!.Value)
            .Distinct()
            .ToList();

        var facts = await LoadContractFactsAsync(contractIds, ct);
        return SupplierContractStatementBuilder.Build(
            statement,
            facts,
            page: Math.Max(1, filter.Page),
            pageSize: Math.Clamp(filter.PageSize, 10, 100));
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
        var soldByContract = await _db.SalesTransactions.AsNoTracking()
            .Where(s => s.ContractId.HasValue && contractIds.Contains(s.ContractId.Value))
            .GroupBy(s => s.ContractId!.Value)
            .Select(g => new { ContractId = g.Key, Qty = g.Sum(x => x.QuantityMt) })
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
            soldByContract.TryGetValue(c.Id, out var sold);
            var hasConfirmedQuantity = loadedByContract.ContainsKey(c.Id) || soldByContract.ContainsKey(c.Id);

            facts[c.Id] = new SupplierContractStatementBuilder.ContractFacts(
                c.ProductName,
                c.QuantityMt,
                price,
                contractValue,
                hasConfirmedQuantity ? loaded + sold : null);
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
            SourceType = filter.SourceType,
            Search = filter.Search,
            Page = filter.Page,
            PageSize = filter.PageSize,
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
            SourceType = filter.SourceType,
            Search = filter.Search,
            Page = filter.Page,
            PageSize = filter.PageSize,
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

    private static string[] BuildCsvHeaders(PartyStatementColumnOptions columns, string currency)
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
        headers.AddRange([$"رسیدگی {currency}", $"بردگی {currency}", $"بیلانس {currency}"]);
        return headers.ToArray();
    }

    private static string?[] BuildCsvRow(PartyStatementRow row, PartyStatementColumnOptions columns, bool isRub)
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
        values.Add(CsvExportSupport.Decimal(isRub ? row.ReceiptRub : row.ReceiptBase));
        values.Add(CsvExportSupport.Decimal(isRub ? row.OutflowRub : row.OutflowBase));
        values.Add(CsvExportSupport.Decimal(isRub ? row.RunningBalanceRub : row.RunningBalance));
        return values.ToArray();
    }

    private static string[] BuildContractCsvHeaders(string currency)
        =>
        [
            "No",
            "Contract",
            "Product",
            "ContractQuantityMt",
            "ConfirmedQuantityMt",
            "ContractValueUsd",
            $"ConfirmedValue{currency}",
            $"PaymentOrReceipt{currency}",
            "LoadingCount",
            $"Balance{currency}"
        ];

    private static string?[] BuildContractCsvRow(SupplierContractStatementRow row, bool isRub)
        =>
        [
            row.Sequence.ToString(),
            row.ContractNumber ?? row.Title,
            row.ProductName,
            CsvExportSupport.Decimal(row.ContractQuantityMt),
            CsvExportSupport.Decimal(row.LoadedQuantityMt),
            CsvExportSupport.Decimal(row.ContractValueUsd),
            CsvExportSupport.Decimal(isRub ? row.ConfirmedValueRub : row.ConfirmedValue),
            CsvExportSupport.Decimal(isRub ? row.SettlementTotalRub : row.SettlementTotal),
            row.LoadingCount.ToString(),
            CsvExportSupport.Decimal(isRub ? row.BalanceRub : row.Balance)
        ];

    private static bool IsCurrency(PartyStatementRow row, string currency)
        => string.Equals(row.OriginalCurrency, currency, StringComparison.OrdinalIgnoreCase);

    // نمای خلاصهٔ قراردادها/بارگیری‌ها فقط برای تأمین‌کننده؛ بقیه همیشه گردش حساب.
    private static bool UsesContractSummary(PartyStatementPartyType partyType)
        => partyType == PartyStatementPartyType.Supplier;

    private static bool IsConfirmedOperation(PartyStatementRow row)
        => row.SourceType is "Loading" or "Sale";

    private static PartyStatementResult WithRows(
        PartyStatementResult statement,
        IReadOnlyList<PartyStatementRow> rows)
        => new()
        {
            Party = statement.Party,
            Policy = statement.Policy,
            CompanyInfo = statement.CompanyInfo,
            PartyInfo = statement.PartyInfo,
            DocumentInfo = statement.DocumentInfo,
            Summary = statement.Summary,
            ColumnOptions = statement.ColumnOptions,
            Rows = rows,
            Note = statement.Note,
            Authorization = statement.Authorization,
            CourtesyText = statement.CourtesyText
        };
}
