using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Helpers;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.PartyStatements;

namespace PTGOilSystem.Web.Services.PartyStatements;

/// <summary>
/// ساختِ ViewModel صفحهٔ صورت‌حساب (سطرها + خلاصهٔ قراردادها + گزینه‌های نوار فیلتر).
/// پیش‌تر همین منطق فقط داخل PartyStatementsController بود؛ حالا تب «صورت‌حساب» پروفایل
/// طرف‌حساب هم همین را سمت سرور رندر می‌کند و دیگر صفحه را دوباره با fetch نمی‌گیرد.
/// فقط ترکیب و خواندن است: هیچ مبلغ، جهت یا مانده‌ای اینجا محاسبه نمی‌شود.
/// </summary>
public sealed class PartyStatementPageBuilder
{
    private readonly IPartyStatementReadService _statementService;
    private readonly ApplicationDbContext _db;

    public PartyStatementPageBuilder(IPartyStatementReadService statementService, ApplicationDbContext db)
    {
        _statementService = statementService;
        _db = db;
    }

    public async Task<PartyStatementViewModel> BuildDocumentAsync(
        PartyStatementPartyType partyType,
        int id,
        PartyStatementFilter filter,
        bool print,
        SupplierStatementView view,
        CancellationToken ct)
    {
        var statement = await _statementService.GetStatementAsync(new PartyRef(partyType, id, filter.CompanyId), filter, ct);
        var options = await LoadFilterOptionsAsync(partyType, id, ct);

        // اگر هیچ سندِ این دوره به قرارداد وصل نباشد، «خلاصه قراردادها» بی‌معنا است و
        // فقط یک ردیفِ «بدون قرارداد» می‌شود؛ در این حالت صفحه همان گردش حساب ساده است.
        var showsContracts = UsesContractSummary(partyType) && HasContractRows(statement);
        if (!showsContracts)
        {
            view = SupplierStatementView.Ledger;
        }

        SupplierContractStatementViewModel? grouping = null;
        if (view == SupplierStatementView.Contracts && showsContracts)
        {
            grouping = await BuildContractGroupingAsync(statement, filter, ct);
        }
        else if (view == SupplierStatementView.Ledger && showsContracts)
        {
            // گردش فشرده: بارگیری/فروش‌های یک قرارداد در یک سطر جمع می‌شوند. جمع دوره،
            // مانده و بیلانس نهایی از همان Summary می‌آید و تغییر نمی‌کند.
            statement = WithRows(statement, SupplierContractStatementBuilder.BuildCompactLedgerRows(statement));
        }

        return new PartyStatementViewModel
        {
            Statement = statement,
            Filter = filter,
            IsPrintMode = print,
            SupplierView = view,
            HasContractRows = showsContracts,
            ContractGrouping = grouping,
            ContractOptions = options.Contracts,
            CompanyOptions = options.Companies,
            CurrencyOptions = options.Currencies,
            PartyOptions = options.Parties
        };
    }

    // اطلاعات نمایشیِ قرارداد (محصول/مقدار/نرخ/ارزش/بارگیری‌شده) را برای نمای «قراردادها»
    // بارگذاری می‌کند. صرفاً metadata است؛ اعداد رسید/برد/بیلانس از خودِ سطرهای مالی می‌آیند.
    public async Task<SupplierContractStatementViewModel> BuildContractGroupingAsync(
        PartyStatementResult statement,
        PartyStatementFilter filter,
        CancellationToken ct)
    {
        var contractIds = statement.Rows
            .Where(r => !r.IsOpeningBalance && r.ContractId.HasValue)
            .Select(r => r.ContractId!.Value)
            .Distinct()
            .ToList();

        var facts = await LoadContractFactsAsync(
            contractIds,
            ct,
            statement.Party.PartyType,
            statement.Party.PartyId);
        return SupplierContractStatementBuilder.Build(
            statement,
            facts,
            page: Math.Max(1, filter.Page),
            pageSize: Math.Clamp(filter.PageSize, 10, 100));
    }

    // metadataِ نمایشیِ قرارداد (محصول/مقدار/نرخ/ارزش/بارگیری‌شده). فقط اطلاعاتی است؛ روی
    // رسید/برد/بیلانس اثری ندارد. با دو کوئری projection (بدون N+1) خوانده می‌شود.
    public async Task<Dictionary<int, SupplierContractStatementBuilder.ContractFacts>> LoadContractFactsAsync(
        IReadOnlyCollection<int> contractIds,
        CancellationToken ct,
        PartyStatementPartyType? partyType = null,
        int? partyId = null)
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

        // درصد سهم فقط برای شریک خوانده می‌شود و صرفاً برچسب ستون «سهم» است؛ مبالغِ
        // سطرهای صورت‌حساب شریک از قبل سهم‌بندی شده‌اند و اینجا چیزی محاسبه نمی‌شود.
        // // PTG-P0-03 — سهم تاریخ‌دار شد؛ برای نمایش فقط آخرین بازهٔ هر شریک دیده می‌شود.
        var shareRows = partyType == PartyStatementPartyType.Partner && partyId.HasValue
            ? await _db.ContractPartners.AsNoTracking()
                .Where(cp => cp.PartnerId == partyId.Value && contractIds.Contains(cp.ContractId))
                .Select(cp => new { cp.ContractId, cp.SharePercent, cp.EffectiveFrom })
                .ToListAsync(ct)
            : [];
        var shareByContract = shareRows
            .GroupBy(x => x.ContractId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.EffectiveFrom).First().SharePercent);

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
                hasConfirmedQuantity ? loaded + sold : null,
                shareByContract.TryGetValue(c.Id, out var share) ? share : null);
        }

        return facts;
    }

    // گزینه‌های نوار فیلتر: قراردادهای مرتبط با همین طرف‌حساب، شرکت‌ها، ارزهای فعال و
    // (برای تأمین‌کننده) فهرست تأمین‌کننده‌ها برای جابه‌جایی بین صورت‌حساب‌ها.
    private async Task<(List<PartyStatementFilterOption> Contracts,
        List<PartyStatementFilterOption> Companies,
        List<string> Currencies,
        List<PartyStatementFilterOption> Parties)> LoadFilterOptionsAsync(
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

        // فقط برای تأمین‌کننده: فهرست انتخابِ طرف‌حساب. تأمین‌کنندهٔ غیرفعالِ جاری هم
        // می‌ماند تا انتخابِ فعلی از فهرست نیفتد.
        var parties = partyType == PartyStatementPartyType.Supplier
            ? await _db.Suppliers.AsNoTracking()
                .Where(x => x.IsActive || x.Id == id)
                .OrderBy(x => x.NamePersian ?? x.Name)
                .Select(x => new PartyStatementFilterOption(x.Id, x.NamePersian ?? x.Name))
                .Take(500)
                .ToListAsync(ct)
            : new List<PartyStatementFilterOption>();

        return (contracts, companies, currencies, parties);
    }

    // نمای خلاصهٔ قراردادها برای طرف‌حساب‌های قراردادی؛ بقیه (کارمند، صراف، راننده) همیشه گردش حساب.
    public static bool UsesContractSummary(PartyStatementPartyType partyType)
        => PartyStatementViewModel.SupportsContractSummary(partyType);

    // نمای مؤثر: اگر کاربر چیزی انتخاب نکرده باشد، پیش‌فرضِ همان نوع طرف‌حساب.
    public static SupplierStatementView ResolveView(
        PartyStatementPartyType partyType,
        SupplierStatementView? requested)
        => UsesContractSummary(partyType)
            ? requested ?? PartyStatementViewModel.DefaultViewFor(partyType)
            : SupplierStatementView.Ledger;

    public static bool NeedsOperationalColumns(SupplierStatementView view)
        => view == SupplierStatementView.Loadings;

    // نمای قراردادی فقط وقتی ساخته می‌شود که سندِ وصل‌شده به قرارداد وجود داشته باشد.
    public static bool HasContractRows(PartyStatementResult statement)
        => statement.Rows.Any(r => !r.IsOpeningBalance && r.ContractId.HasValue);

    public static PartyStatementFilter WithOperationalColumns(PartyStatementFilter filter)
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

    public static PartyStatementResult WithRows(
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
