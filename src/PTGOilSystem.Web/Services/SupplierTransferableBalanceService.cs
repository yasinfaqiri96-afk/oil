using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Controllers;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services.CompanyFlow;
using PTGOilSystem.Web.Services.PartyStatements;

namespace PTGOilSystem.Web.Services;

/// <summary>یک سطر دفتر که پول/ارزشِ داده‌شده به تأمین‌کننده را نمایندگی می‌کند و هنوز مصرف نشده.</summary>
public sealed record SupplierBalanceSourceSlice(
    string SourceType,
    int SourceId,
    int LedgerEntryId,
    DateTime SourceDate,
    string CurrencyCode,
    decimal RemainingOriginalAmount,
    decimal HistoricalFxRateToUsd,
    decimal RemainingBookAmountUsd,
    string Description,
    // نرخ مستقیمِ ثبت‌شدهٔ همین سطر دفتر («۱ دالر = ۷۰»). null یعنی سطر Legacy است و
    // نرخش فقط از روی مبالغ قابل بازسازی است.
    decimal? HistoricalCurrencyPerUsdRate = null)
{
    public bool HasStoredRate => HistoricalCurrencyPerUsdRate is > 0m;
}

/// <summary>مانده قابل انتقال به تفکیک ارز — همان چیزی که در کارت تأمین‌کننده نشان داده می‌شود.</summary>
public sealed record SupplierTransferableCurrencyBucket(
    string CurrencyCode,
    decimal RemainingOriginalAmount,
    decimal RemainingBookAmountUsd,
    decimal WeightedHistoricalFxRateToUsd,
    IReadOnlyList<SupplierBalanceSourceSlice> Slices,
    // «۱ دالر = چند واحد ارز» برای کل این سطل.
    decimal WeightedHistoricalPerUsdRate,
    // true یعنی حد اقل یک منبع نرخ مستقیم نداشته و این عدد بازسازی‌شده است.
    bool RateIsEstimated)
{
    /// <summary>
    /// نرخِ قابل اتکا برای قفل‌کردن روی یک انتقال — فقط وقتی مقدار دارد که همهٔ منابع
    /// نرخ مستقیمِ ثبت‌شده داشته باشند. در غیر این صورت null است تا نرخِ بازسازی‌شده
    /// ناخواسته به‌عنوان نرخ قطعی ذخیره نشود.
    /// </summary>
    public decimal? StoredPerUsdRate => RateIsEstimated ? null : WeightedHistoricalPerUsdRate;
}

/// <summary>مانده قابل انتقال تأمین‌کننده در یک شرکت مشخص.</summary>
public sealed record SupplierCompanyTransferableBalance(
    int CompanyId,
    string CompanyName,
    decimal NetAccountBalanceUsd,
    decimal ClaimUsd,
    decimal ConsumedByLegacyAllocationsUsd,
    decimal ConsumedByTransfersUsd,
    decimal TransferableTotalUsd,
    IReadOnlyList<SupplierTransferableCurrencyBucket> Buckets)
{
    public bool HasTransferable => TransferableTotalUsd > 0m && Buckets.Count > 0;

    public SupplierTransferableCurrencyBucket? Bucket(string? currencyCode)
        => string.IsNullOrWhiteSpace(currencyCode)
            ? null
            : Buckets.FirstOrDefault(b => string.Equals(
                b.CurrencyCode,
                SystemCurrency.Normalize(currencyCode),
                StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// منابعی که شرکتشان از روی سند قابل اثبات نیست. اینها «گم» نیستند: در سطل سطح‌گروه
/// (<see cref="SupplierTransferableBalance.GroupLevel"/>) قابل انتقال‌اند. این رکورد فقط
/// شمارش و ارزش همان سطرها را برای شفافیت نگه می‌دارد.
/// </summary>
public sealed record SupplierUnknownCompanySources(
    decimal OutflowUsd,
    int RowCount,
    IReadOnlyList<SupplierBalanceSourceSlice> Slices)
{
    public bool HasAny => RowCount > 0;
}

public sealed record SupplierTransferableBalance(
    int SupplierId,
    IReadOnlyList<SupplierCompanyTransferableBalance> Companies,
    SupplierUnknownCompanySources UnknownCompany,
    // مانده‌ای که شرکت منبعش اثبات نشده. Group-first: پنهان یا مسدود نمی‌شود؛ شرکتِ هر
    // ردیف انتقال از قرارداد مقصد گرفته می‌شود. CompanyId آن صفر است و هرگز ذخیره نمی‌شود.
    SupplierCompanyTransferableBalance? GroupLevel = null)
{
    /// <summary>شناسهٔ قراردادیِ سطل سطح‌گروه. هیچ رکورد Company با این شناسه وجود ندارد.</summary>
    public const int GroupLevelCompanyId = 0;

    public const string GroupLevelCompanyName = "سطح گروه (شرکت منبع نامعلوم)";

    /// <summary>همهٔ سطل‌ها — شرکت‌های اثبات‌شده به‌علاوهٔ سطح گروه.</summary>
    public IReadOnlyList<SupplierCompanyTransferableBalance> AllPools
        => GroupLevel is null ? Companies : [.. Companies, GroupLevel];

    public bool HasTransferable => AllPools.Any(c => c.HasTransferable);

    public decimal TransferableTotalUsd => AllPools.Sum(c => c.TransferableTotalUsd);

    public SupplierCompanyTransferableBalance? Company(int companyId)
        => companyId == GroupLevelCompanyId
            ? GroupLevel
            : Companies.FirstOrDefault(c => c.CompanyId == companyId);
}

public interface ISupplierTransferableBalanceService
{
    Task<SupplierTransferableBalance> GetAsync(int supplierId, CancellationToken ct = default);
}

/// <summary>
/// «مانده قابل انتقال» تأمین‌کننده — منبع واحد و زندهٔ انتقال طلب به قرارداد.
///
/// چرا محاسبه‌شونده است و در جدول ذخیره نمی‌شود: مانده واقعی با هر بارگیری، پرداخت، اصلاح حساب
/// یا تسویهٔ جدید تغییر می‌کند. اگر snapshot ذخیره شود، همان لحظه کهنه می‌شود و کاربر می‌تواند
/// پولی را منتقل کند که دیگر آزاد نیست. بنابراین فقط خودِ انتقال‌ها و ردیابیِ منبعشان ذخیره
/// می‌شوند (SupplierBalanceTransfer + SupplierBalanceTransferSource) و مانده هربار از دفتر خوانده می‌شود.
///
/// فرمول — دقیقاً روی همان لایهٔ مرکزیِ صورت‌حساب سوار است تا دو عدد متناقض ساخته نشود:
///
///   طلب خالص  = Σبرد − Σرسید  (CompanyFlowBalanceService، مثبت یعنی شرکت طلبکار است)
///   قابل انتقال = طلب خالص − تخصیص‌های فعال قدیمی − انتقال‌های فعال جدید
///
/// هر دو مکانیزم انتقال روی حساب تأمین‌کننده «خالصِ صفر» هستند (Credit آزاد + Debit قرارداد
/// + سطر تسعیر)، پس طلب خالص با انتقال کم نمی‌شود و باید صریح کسر شود.
///
/// همه‌چیز «به تفکیک شرکت» حساب می‌شود: شرکتِ هر سطر از خودِ سند اثبات می‌شود، نه از قرارداد
/// مقصد. سطرهایی که شرکتشان اثبات نمی‌شود در گروه «شرکت نامعلوم» می‌مانند و قابل انتقال نیستند.
///
/// ترکیب ارزی با FIFO ساخته می‌شود: قدیمی‌ترین پول اول مصرف‌شده فرض می‌شود، پس آنچه قابل انتقال
/// می‌ماند تازه‌ترین سطرهای مصرف‌نشده است. این باعث می‌شود مقدار اصلی ارز و نرخ تاریخی هرکدام
/// حفظ شود و ارزش دالریِ خالص به یک عدد بی‌ارز تبدیل نشود.
/// </summary>
public sealed class SupplierTransferableBalanceService : ISupplierTransferableBalanceService
{
    /// <summary>
    /// SourceTypeهایی که «پول تازه به تأمین‌کننده» نیستند بلکه جابه‌جاییِ داخلی یا اثر P&amp;L‌اند.
    /// اینها از فهرست منابع کنار می‌روند وگرنه یک پول دوبار قابل انتقال می‌شود. توجه: همچنان در
    /// «طلب خالص» شمرده می‌شوند، چون آنجا اثرشان دقیقاً صفر است و حذفشان بیلانس را می‌شکند.
    /// </summary>
    private static readonly HashSet<string> InternalMovementSourceTypes = new(StringComparer.Ordinal)
    {
        SupplierPaymentAllocationService.LedgerSourceType,
        SupplierPaymentAllocationService.ReversalLedgerSourceType,
        SupplierPaymentAllocationService.ExchangeDifferenceLedgerSourceType,
        SupplierPaymentAllocationService.ExchangeDifferenceReversalLedgerSourceType,
        SupplierBalanceTransferService.LedgerSourceType,
        SupplierBalanceTransferService.ReversalLedgerSourceType,
        SupplierBalanceTransferService.ExchangeDifferenceLedgerSourceType,
        SupplierBalanceTransferService.ExchangeDifferenceReversalLedgerSourceType,
        ContractBalanceTransferService.LedgerSourceType,
        SarrafSettlementService.ExchangeDifferenceSourceType,
        SarrafFxGainSourceType
    };

    // سطر سود تفاوت نرخ صراف (در PaymentsController ثبت می‌شود). Credit است و به‌هرحال «برد»
    // شمرده نمی‌شود، ولی صریح کنار گذاشته می‌شود تا وابستگی به ترتیب قواعد نداشته باشیم.
    private const string SarrafFxGainSourceType = "SarrafFxGain";
    private const string ThreeWaySettlementSourceType = "ThreeWaySettlement";

    private readonly ApplicationDbContext _db;
    private readonly ICompanyFlowDirectionResolver _directions;
    private readonly ICompanyFlowBalanceService _balances;

    public SupplierTransferableBalanceService(
        ApplicationDbContext db,
        ICompanyFlowDirectionResolver? directions = null,
        ICompanyFlowBalanceService? balances = null)
    {
        _db = db;
        _directions = directions ?? new CompanyFlowDirectionResolver();
        _balances = balances ?? new CompanyFlowBalanceService();
    }

    public async Task<SupplierTransferableBalance> GetAsync(int supplierId, CancellationToken ct = default)
    {
        var rows = await _db.LedgerEntries
            .AsNoTracking()
            .Where(LedgerEntryOwnership.SupplierOwned(supplierId))
            .WhereEffectiveSarrafSettlementLegs(_db)
            .Select(l => new LedgerRow(
                l.Id,
                l.EntryDate,
                l.Side,
                l.AmountUsd,
                l.SourceType,
                l.SourceId,
                l.ContractId,
                l.SourceAmount,
                l.SourceCurrencyCode,
                l.AppliedFxRateToUsd,
                l.Description,
                l.AppliedCurrencyPerUsdRate,
                l.ViaSarrafGroupId))
            .ToListAsync(ct);

        var owners = await LoadCompanyOwnersAsync(rows, ct);

        // ۱) هر سطر به شرکتِ اثبات‌شدهٔ خودش نسبت داده می‌شود؛ نه به قرارداد مقصد انتقال.
        var byCompany = new Dictionary<int, List<LedgerRow>>();
        var unknownRows = new List<LedgerRow>();
        foreach (var row in rows)
        {
            var companyId = owners.Resolve(row);
            if (companyId is null)
            {
                unknownRows.Add(row);
                continue;
            }

            if (!byCompany.TryGetValue(companyId.Value, out var list))
            {
                list = [];
                byCompany[companyId.Value] = list;
            }

            list.Add(row);
        }

        // ۲) آنچه قبلاً از همین طلب برداشته شده — هر دو موتور، به تفکیک شرکت.
        var legacyByCompany = await _db.SupplierPaymentAllocations
            .AsNoTracking()
            .Where(a => a.Status == SupplierPaymentAllocationStatus.Active
                && a.PaymentTransaction != null
                && a.PaymentTransaction.SupplierId == supplierId
                && a.Contract != null)
            .GroupBy(a => a.Contract!.CompanyId)
            .Select(g => new { CompanyId = g.Key, Total = g.Sum(a => a.AllocatedBookAmountUsd) })
            .ToDictionaryAsync(x => x.CompanyId, x => x.Total, ct);

        // مصرفِ انتقال‌ها به «سطلِ منبع» نسبت داده می‌شود، نه به شرکت مقصد. برای انتقال‌های
        // درون‌شرکتی این دو یکی‌اند و رفتار عوض نمی‌شود؛ ولی برای انتقالِ سطح‌گروه، شرکت مقصد
        // با سطل منبع فرق دارد و اگر مصرف به مقصد نسبت داده شود، سطل سطح‌گروه کوچک نمی‌شود
        // و همان پول دوباره قابل انتقال می‌ماند.
        var rowPool = new Dictionary<int, int>();
        foreach (var row in rows)
        {
            rowPool[row.Id] = owners.Resolve(row) ?? SupplierTransferableBalance.GroupLevelCompanyId;
        }

        var transferredByPool = await LoadTransferredByPoolAsync(supplierId, rowPool, ct);

        var companyNames = await LoadCompanyNamesAsync(byCompany.Keys, ct);

        var companies = new List<SupplierCompanyTransferableBalance>();
        foreach (var (companyId, companyRows) in byCompany)
        {
            companies.Add(BuildPool(
                companyId,
                companyNames.GetValueOrDefault(companyId, $"#{companyId}"),
                companyRows,
                Round4(legacyByCompany.GetValueOrDefault(companyId)),
                Round4(transferredByPool.GetValueOrDefault(companyId))));
        }

        var unknownSources = unknownRows.Where(IsEligibleSource).ToList();
        var unknown = new SupplierUnknownCompanySources(
            Round4(unknownSources.Sum(r => Math.Abs(r.AmountUsd))),
            unknownSources.Count,
            unknownSources
                .OrderBy(r => r.EntryDate)
                .Select(r =>
                {
                    var bookUsd = Round4(Math.Abs(r.AmountUsd));
                    var (currency, original, rate, perUsd) = ResolveOriginal(r, bookUsd);
                    return new SupplierBalanceSourceSlice(
                        r.SourceType, r.SourceId, r.Id, r.EntryDate,
                        currency, original, rate, bookUsd, r.Description ?? string.Empty, perUsd);
                })
                .ToList());

        // سطل سطح‌گروه: همان سطرهایی که شرکتشان اثبات نشده. دیگر «مرده» نیستند — قابل انتقال‌اند
        // و شرکت هر ردیف انتقال از قرارداد مقصد گرفته می‌شود.
        var groupLevel = unknownRows.Count == 0
            ? null
            : BuildPool(
                SupplierTransferableBalance.GroupLevelCompanyId,
                SupplierTransferableBalance.GroupLevelCompanyName,
                unknownRows,
                legacyUsd: 0m,
                transferredUsd: Round4(transferredByPool.GetValueOrDefault(
                    SupplierTransferableBalance.GroupLevelCompanyId)));

        return new SupplierTransferableBalance(
            supplierId,
            companies
                .OrderByDescending(c => c.TransferableTotalUsd)
                .ThenBy(c => c.CompanyName, StringComparer.Ordinal)
                .ToList(),
            unknown,
            groupLevel is { TransferableTotalUsd: > 0m } ? groupLevel : null);
    }

    /// <summary>یک سطل مانده — شرکت اثبات‌شده یا سطح گروه. فرمول هر دو دقیقاً یکی است.</summary>
    private SupplierCompanyTransferableBalance BuildPool(
        int companyId,
        string companyName,
        List<LedgerRow> poolRows,
        decimal legacyUsd,
        decimal transferredUsd)
    {
        var netBalanceUsd = Round4(poolRows.Sum(r => _balances.SignedEffect(
            ResolveDirection(r),
            Math.Abs(r.AmountUsd),
            CompanyFlowAccountKind.PartyAccount)));
        var claimUsd = Math.Max(0m, netBalanceUsd);

        var sources = poolRows
            .Where(IsEligibleSource)
            .OrderBy(r => r.EntryDate)
            .ThenBy(r => r.Id)
            .ToList();
        var totalSourceBookUsd = Round4(sources.Sum(r => Math.Abs(r.AmountUsd)));

        var transferableTotalUsd = Round4(Math.Max(
            0m,
            Math.Min(claimUsd - legacyUsd - transferredUsd, totalSourceBookUsd)));

        return new SupplierCompanyTransferableBalance(
            companyId,
            companyName,
            netBalanceUsd,
            claimUsd,
            legacyUsd,
            transferredUsd,
            transferableTotalUsd,
            BuildBuckets(sources, totalSourceBookUsd, transferableTotalUsd));
    }

    /// <summary>
    /// مصرفِ انتقال‌های فعال، به تفکیک سطلِ منبع.
    ///
    /// هر ردیف انتقال snapshot منابع مصرف‌شده‌اش را نگه می‌دارد (SupplierBalanceTransferSource)،
    /// پس سطل واقعی از همان LedgerEntryId منبع خوانده می‌شود. اگر منبع ردیابی نشده باشد
    /// (رکورد قدیمی یا سطری خارج از دامنهٔ این تأمین‌کننده)، به شرکت ثبت‌شدهٔ خودِ انتقال
    /// برمی‌گردد — همان رفتار قبلی.
    /// </summary>
    private async Task<Dictionary<int, decimal>> LoadTransferredByPoolAsync(
        int supplierId,
        Dictionary<int, int> rowPool,
        CancellationToken ct)
    {
        var transfers = await _db.SupplierBalanceTransfers
            .AsNoTracking()
            .Where(t => t.SupplierId == supplierId && t.Status == SupplierBalanceTransferStatus.Active)
            .Select(t => new
            {
                t.CompanyId,
                t.HistoricalAmountUsd,
                Sources = t.Sources
                    .Select(s => new { s.LedgerEntryId, s.ConsumedBookAmountUsd })
                    .ToList()
            })
            .ToListAsync(ct);

        var totals = new Dictionary<int, decimal>();
        void Add(int poolId, decimal amount)
        {
            if (amount == 0m)
            {
                return;
            }

            totals[poolId] = totals.GetValueOrDefault(poolId) + amount;
        }

        foreach (var transfer in transfers)
        {
            var attributed = 0m;
            foreach (var source in transfer.Sources)
            {
                if (source.LedgerEntryId is int ledgerId && rowPool.TryGetValue(ledgerId, out var poolId))
                {
                    Add(poolId, source.ConsumedBookAmountUsd);
                    attributed += source.ConsumedBookAmountUsd;
                }
            }

            // آنچه از روی منابع اثبات نشد به شرکت ثبت‌شدهٔ خودِ انتقال می‌رود تا هیچ مصرفی گم نشود.
            var remainder = Round4(transfer.HistoricalAmountUsd - attributed);
            if (remainder > 0m)
            {
                Add(transfer.CompanyId, remainder);
            }
        }

        return totals.ToDictionary(x => x.Key, x => Round4(x.Value));
    }

    /// <summary>
    /// نقشهٔ «سند ← شرکت». شرکت از خودِ منبع پول اثبات می‌شود، به این ترتیب:
    ///   سطر دارای قرارداد   → Contract.CompanyId
    ///   پرداخت / دریافت      → PaymentTransaction.CompanyId، و اگر خالی بود از قرارداد همان پرداخت
    ///   تسویهٔ صراف          → قرارداد تسویه، وگرنه پرداخت مرتبط، وگرنه حساب نقدیِ تسویه
    ///   تسویهٔ سه‌طرفه       → قرارداد خرید همان سند
    ///   مانده اول دوره/اصلاح → فقط اگر قرارداد داشته باشند
    /// هرچه از این مسیرها اثبات نشود «شرکت نامعلوم» می‌ماند و قابل انتقال نیست.
    /// </summary>
    private async Task<CompanyOwnerMaps> LoadCompanyOwnersAsync(List<LedgerRow> rows, CancellationToken ct)
    {
        var contractIds = rows.Where(r => r.ContractId.HasValue)
            .Select(r => r.ContractId!.Value).Distinct().ToList();

        var paymentIds = rows.Where(r => Enum.TryParse<PaymentKind>(r.SourceType, out _))
            .Select(r => r.SourceId).Distinct().ToList();

        var sarrafIds = rows.Where(r => r.SourceType == SarrafSettlementService.SupplierLedgerSourceType)
            .Select(r => r.SourceId).Distinct().ToList();

        var threeWayIds = rows.Where(r => r.SourceType == ThreeWaySettlementSourceType)
            .Select(r => r.SourceId).Distinct().ToList();

        // سطرِ «مخزن آزاد» هر دو موتور انتقال عمداً ContractId ندارد. بدون این نگاشت، آن سطر
        // در گروه «نامعلوم» می‌افتد و خاصیتِ «اثر خالص صفر روی شرکت» می‌شکند.
        var transferIds = rows.Where(r => IsOwnTransferSourceType(r.SourceType))
            .Select(r => r.SourceId).Distinct().ToList();
        var allocationIds = rows.Where(r => IsLegacyAllocationSourceType(r.SourceType))
            .Select(r => r.SourceId).Distinct().ToList();

        var payments = paymentIds.Count == 0
            ? []
            : await _db.PaymentTransactions.AsNoTracking()
                .Where(p => paymentIds.Contains(p.Id))
                .Select(p => new PartyDocRow(p.Id, p.CompanyId, p.ContractId))
                .ToListAsync(ct);

        var sarrafs = sarrafIds.Count == 0
            ? []
            : await _db.SarrafSettlements.AsNoTracking()
                .Where(s => sarrafIds.Contains(s.Id))
                .Select(s => new SarrafDocRow(s.Id, s.ContractId, s.PaymentTransactionId, s.CashAccountId))
                .ToListAsync(ct);

        var threeWays = threeWayIds.Count == 0
            ? []
            : await _db.ThreeWaySettlements.AsNoTracking()
                .Where(s => threeWayIds.Contains(s.Id))
                .Select(s => new ThreeWayDocRow(s.Id, s.SupplierPurchaseContractId))
                .ToListAsync(ct);

        var extraPaymentIds = sarrafs.Where(s => s.PaymentTransactionId.HasValue)
            .Select(s => s.PaymentTransactionId!.Value)
            .Where(id => payments.All(p => p.Id != id))
            .Distinct()
            .ToList();
        var extraPayments = extraPaymentIds.Count == 0
            ? []
            : await _db.PaymentTransactions.AsNoTracking()
                .Where(p => extraPaymentIds.Contains(p.Id))
                .Select(p => new PartyDocRow(p.Id, p.CompanyId, p.ContractId))
                .ToListAsync(ct);

        // «پرداخت از طریق صراف» خام نه PaymentTransaction دارد نه CashAccount؛ تنها لنگرِ
        // اثبات‌پذیرش قرارداد خودش یا قراردادِ لنگِ مکملِ همان گروه است. بدون این شاخه، سندی که
        // قرارداد دارد ولی فقط روی یک لنگ ثبت شده، بی‌دلیل «نامعلوم» می‌ماند.
        var viaSarrafGroupIds = rows
            .Where(r => IsViaSarrafSourceType(r.SourceType) && r.ViaSarrafGroupId.HasValue)
            .Select(r => r.ViaSarrafGroupId!.Value)
            .Distinct()
            .ToList();
        var viaSarrafGroupContracts = viaSarrafGroupIds.Count == 0
            ? []
            : await _db.LedgerEntries.AsNoTracking()
                .Where(l => l.ViaSarrafGroupId != null
                    && viaSarrafGroupIds.Contains(l.ViaSarrafGroupId!.Value)
                    && l.ContractId != null)
                .Select(l => new ViaSarrafGroupContractRow(l.ViaSarrafGroupId!.Value, l.ContractId!.Value))
                .ToListAsync(ct);

        // قراردادهای غیرمستقیم (از پرداخت، تسویهٔ صراف، سه‌طرفه و گروه صرافی) هم باید نگاشت شوند.
        contractIds.AddRange(payments.Concat(extraPayments)
            .Where(p => p.ContractId.HasValue).Select(p => p.ContractId!.Value));
        contractIds.AddRange(sarrafs.Where(s => s.ContractId.HasValue).Select(s => s.ContractId!.Value));
        contractIds.AddRange(threeWays.Where(s => s.SupplierPurchaseContractId.HasValue)
            .Select(s => s.SupplierPurchaseContractId!.Value));
        contractIds.AddRange(viaSarrafGroupContracts.Select(g => g.ContractId));
        contractIds = contractIds.Distinct().ToList();

        var contractCompany = contractIds.Count == 0
            ? []
            : await _db.Contracts.AsNoTracking()
                .Where(c => contractIds.Contains(c.Id))
                .ToDictionaryAsync(c => c.Id, c => c.CompanyId, ct);

        var cashAccountIds = sarrafs.Where(s => s.CashAccountId.HasValue)
            .Select(s => s.CashAccountId!.Value).Distinct().ToList();
        var cashCompany = cashAccountIds.Count == 0
            ? []
            : await _db.CashAccounts.AsNoTracking()
                .Where(a => cashAccountIds.Contains(a.Id) && a.CompanyId != null)
                .ToDictionaryAsync(a => a.Id, a => a.CompanyId!.Value, ct);

        var allPayments = payments.Concat(extraPayments).ToList();

        int? PaymentCompany(int paymentId)
        {
            var payment = allPayments.FirstOrDefault(p => p.Id == paymentId);
            if (payment is null)
            {
                return null;
            }

            if (payment.CompanyId.HasValue)
            {
                return payment.CompanyId.Value;
            }

            return payment.ContractId.HasValue && contractCompany.TryGetValue(payment.ContractId.Value, out var c)
                ? c
                : null;
        }

        var paymentCompanies = paymentIds.ToDictionary(id => id, PaymentCompany);

        var sarrafCompanies = sarrafs.ToDictionary(
            s => s.Id,
            s =>
            {
                if (s.ContractId.HasValue && contractCompany.TryGetValue(s.ContractId.Value, out var byContract))
                {
                    return (int?)byContract;
                }

                if (s.PaymentTransactionId.HasValue)
                {
                    var byPayment = PaymentCompany(s.PaymentTransactionId.Value);
                    if (byPayment.HasValue)
                    {
                        return byPayment;
                    }
                }

                return s.CashAccountId.HasValue && cashCompany.TryGetValue(s.CashAccountId.Value, out var byCash)
                    ? byCash
                    : null;
            });

        var threeWayCompanies = threeWays.ToDictionary(
            s => s.Id,
            s => s.SupplierPurchaseContractId.HasValue
                && contractCompany.TryGetValue(s.SupplierPurchaseContractId.Value, out var c)
                    ? (int?)c
                    : null);

        var viaSarrafGroupCompanies = viaSarrafGroupContracts
            .GroupBy(g => g.GroupId)
            .ToDictionary(
                g => g.Key,
                g => g
                    .Select(x => contractCompany.TryGetValue(x.ContractId, out var c) ? (int?)c : null)
                    .FirstOrDefault(c => c.HasValue));

        var transferCompanies = transferIds.Count == 0
            ? []
            : await _db.SupplierBalanceTransfers.AsNoTracking()
                .Where(t => transferIds.Contains(t.Id))
                .ToDictionaryAsync(t => t.Id, t => (int?)t.CompanyId, ct);

        // شرکت تخصیص قدیمی از قرارداد مقصدش می‌آید. projection داخل کوئری انجام می‌شود تا
        // navigation بارگذاری‌نشده باعث NullReference نشود.
        var allocationCompanies = allocationIds.Count == 0
            ? []
            : (await _db.SupplierPaymentAllocations.AsNoTracking()
                .Where(a => allocationIds.Contains(a.Id) && a.Contract != null)
                .Select(a => new { a.Id, CompanyId = a.Contract!.CompanyId })
                .ToListAsync(ct))
                .ToDictionary(x => x.Id, x => (int?)x.CompanyId);

        return new CompanyOwnerMaps(
            contractCompany,
            paymentCompanies,
            sarrafCompanies,
            threeWayCompanies,
            transferCompanies,
            allocationCompanies,
            viaSarrafGroupCompanies);
    }

    private async Task<Dictionary<int, string>> LoadCompanyNamesAsync(
        IEnumerable<int> companyIds,
        CancellationToken ct)
    {
        var ids = companyIds.Distinct().ToList();
        return ids.Count == 0
            ? []
            : await _db.Companies.AsNoTracking()
                .Where(c => ids.Contains(c.Id))
                .ToDictionaryAsync(c => c.Id, c => c.Name, ct);
    }

    /// <summary>
    /// FIFO: به‌اندازهٔ «مصرف‌شده» از قدیمی‌ترین سطرها می‌گذریم و باقیمانده را از تازه‌ترین
    /// سطرها برمی‌داریم. سطری که وسط برش می‌افتد به‌نسبت تقسیم می‌شود تا ارز و نرخ تاریخی‌اش حفظ شود.
    /// </summary>
    private static List<SupplierTransferableCurrencyBucket> BuildBuckets(
        List<LedgerRow> sources,
        decimal totalSourceBookUsd,
        decimal transferableTotalUsd)
    {
        if (transferableTotalUsd <= 0m || sources.Count == 0)
        {
            return [];
        }

        var consumedBookUsd = Round4(totalSourceBookUsd - transferableTotalUsd);
        var slices = new List<SupplierBalanceSourceSlice>();
        var skipped = 0m;

        foreach (var row in sources)
        {
            var rowBookUsd = Round4(Math.Abs(row.AmountUsd));
            if (rowBookUsd <= 0m)
            {
                continue;
            }

            if (skipped + rowBookUsd <= consumedBookUsd)
            {
                skipped += rowBookUsd;
                continue;
            }

            var remainingBookUsd = Round4(skipped >= consumedBookUsd
                ? rowBookUsd
                : skipped + rowBookUsd - consumedBookUsd);
            skipped += rowBookUsd;
            if (remainingBookUsd <= 0m)
            {
                continue;
            }

            var (currency, originalAmount, fxRateToUsd, perUsdRate) = ResolveOriginal(row, rowBookUsd);
            // سهم باقیمانده از همان سطر، به همان ارز اصلی.
            var remainingOriginal = rowBookUsd == remainingBookUsd
                ? originalAmount
                : Round4(originalAmount * (remainingBookUsd / rowBookUsd));
            if (remainingOriginal <= 0m)
            {
                continue;
            }

            slices.Add(new SupplierBalanceSourceSlice(
                row.SourceType,
                row.SourceId,
                row.Id,
                row.EntryDate,
                currency,
                remainingOriginal,
                fxRateToUsd,
                remainingBookUsd,
                row.Description ?? string.Empty,
                perUsdRate));
        }

        return slices
            .GroupBy(s => s.CurrencyCode, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var ordered = g.OrderBy(s => s.SourceDate).ThenBy(s => s.LedgerEntryId).ToList();
                var originalTotal = Round4(ordered.Sum(s => s.RemainingOriginalAmount));
                var bookTotal = Round4(ordered.Sum(s => s.RemainingBookAmountUsd));
                var weightedRate = originalTotal > 0m
                    ? FxRateMath.RoundRate(bookTotal / originalTotal)
                    : 1m;
                var (perUsd, estimated) = ResolvePerUsdRate(ordered, originalTotal, bookTotal);
                return new SupplierTransferableCurrencyBucket(
                    g.Key,
                    originalTotal,
                    bookTotal,
                    weightedRate,
                    ordered,
                    perUsd,
                    estimated);
            })
            // دالر آخر می‌آید تا ارزهای اصلی (روبل) اول دیده شوند.
            .OrderBy(b => SystemCurrency.IsBaseCurrency(b.CurrencyCode) ? 1 : 0)
            .ThenByDescending(b => b.RemainingBookAmountUsd)
            .ToList();
    }

    /// <summary>
    /// نرخ «۱ دالر = چند واحد ارز» برای یک سطل.
    ///
    /// ترتیب عمداً این است و نباید ساده‌سازی شود:
    ///   ۱) همهٔ منابع نرخ مستقیمِ ثبت‌شده دارند و همه یکی‌اند → همان عدد، بدون هیچ تقسیمی.
    ///      این تنها مسیری است که ۷۰ را دقیقاً ۷۰ نگه می‌دارد.
    ///   ۲) همهٔ منابع نرخ مستقیم دارند ولی متفاوت‌اند → میانگین وزنی از مبالغ واقعی.
    ///      این یک خلاصه است؛ هنگام ثبت انتقال هر منبع با نرخ خودش مصرف می‌شود.
    ///   ۳) حد اقل یک منبع Legacy است → بازسازی از مبالغ و علامت «تخمینی».
    /// </summary>
    private static (decimal PerUsdRate, bool IsEstimated) ResolvePerUsdRate(
        IReadOnlyList<SupplierBalanceSourceSlice> slices,
        decimal originalTotal,
        decimal bookTotal)
    {
        if (slices.Count == 0 || originalTotal <= 0m || bookTotal <= 0m)
        {
            return (1m, false);
        }

        var estimated = slices.Any(s => !s.HasStoredRate);
        if (!estimated)
        {
            var distinct = slices
                .Select(s => s.HistoricalCurrencyPerUsdRate!.Value)
                .Distinct()
                .ToList();
            if (distinct.Count == 1)
            {
                return (distinct[0], false);
            }
        }

        // نرخ خلاصه از مبالغ واقعی: TotalOriginalAmount / TotalHistoricalAmountUsd.
        return (FxRateMath.RoundRate(originalTotal / bookTotal), estimated);
    }

    /// <summary>ارز اصلی، مقدار اصلی و نرخ تاریخی سطر؛ سطرهای بدون ارز منبع، دالری فرض می‌شوند.</summary>
    private static (string Currency, decimal OriginalAmount, decimal FxRateToUsd, decimal? PerUsdRate) ResolveOriginal(
        LedgerRow row,
        decimal bookUsd)
    {
        var currency = SystemCurrency.Normalize(
            string.IsNullOrWhiteSpace(row.SourceCurrencyCode) ? SystemCurrency.BaseCurrencyCode : row.SourceCurrencyCode);
        var original = row.SourceAmount is > 0m ? row.SourceAmount.Value : bookUsd;
        var rate = row.AppliedFxRateToUsd is > 0m
            ? row.AppliedFxRateToUsd.Value
            : (original > 0m ? FxRateMath.RoundRate(bookUsd / original) : 1m);

        if (SystemCurrency.IsBaseCurrency(currency))
        {
            // دالر همیشه نرخ قطعی ۱ دارد؛ اینجا هیچ چیز بازسازی نمی‌شود.
            return (SystemCurrency.BaseCurrencyCode, bookUsd, 1m, 1m);
        }

        // نرخ مستقیم فقط وقتی برداشته می‌شود که واقعاً ثبت شده باشد. برای سطرهای Legacy
        // عمداً null برمی‌گردد تا سطل «تخمینی» علامت بخورد، نه اینکه عددِ بازسازی‌شده
        // خودش را جای نرخ قطعی جا بزند.
        return (currency, original, rate, row.AppliedCurrencyPerUsdRate is > 0m
            ? row.AppliedCurrencyPerUsdRate.Value
            : null);
    }

    private bool IsEligibleSource(LedgerRow row)
        => !InternalMovementSourceTypes.Contains(row.SourceType)
            && !CompanyFlowSourceTypes.IsReversal(row.SourceType)
            && Math.Abs(row.AmountUsd) > 0m
            && ResolveDirection(row) == CompanyFlowDirection.Outflow;

    private CompanyFlowDirection ResolveDirection(LedgerRow row)
        => _directions.Resolve(new CompanyFlowEvent(
            row.SourceType,
            row.Side,
            CompanyFlowPartyRole.Supplier,
            CompanyFlowSourceTypes.IsReversal(row.SourceType)
                ? CompanyFlowLifecycle.Reversal
                : CompanyFlowLifecycle.Original));

    private static decimal Round4(decimal value)
        => decimal.Round(value, 4, MidpointRounding.AwayFromZero);

    private sealed record PartyDocRow(int Id, int? CompanyId, int? ContractId);

    private sealed record SarrafDocRow(int Id, int? ContractId, int? PaymentTransactionId, int? CashAccountId);

    private sealed record ThreeWayDocRow(int Id, int? SupplierPurchaseContractId);

    /// <summary>سطرهای خودِ موتور جدید (شامل مخزن آزاد، برگشت و تسعیر).</summary>
    private static bool IsOwnTransferSourceType(string sourceType)
        => sourceType == SupplierBalanceTransferService.LedgerSourceType
            || sourceType == SupplierBalanceTransferService.ReversalLedgerSourceType
            || sourceType == SupplierBalanceTransferService.ExchangeDifferenceLedgerSourceType
            || sourceType == SupplierBalanceTransferService.ExchangeDifferenceReversalLedgerSourceType;

    /// <summary>سطرهای موتور قدیمی تخصیص پیش‌پرداخت.</summary>
    private static bool IsLegacyAllocationSourceType(string sourceType)
        => sourceType == SupplierPaymentAllocationService.LedgerSourceType
            || sourceType == SupplierPaymentAllocationService.ReversalLedgerSourceType
            || sourceType == SupplierPaymentAllocationService.ExchangeDifferenceLedgerSourceType
            || sourceType == SupplierPaymentAllocationService.ExchangeDifferenceReversalLedgerSourceType;

    private sealed record CompanyOwnerMaps(
        Dictionary<int, int> ContractCompany,
        Dictionary<int, int?> PaymentCompany,
        Dictionary<int, int?> SarrafSettlementCompany,
        Dictionary<int, int?> ThreeWaySettlementCompany,
        Dictionary<int, int?> TransferCompany,
        Dictionary<int, int?> AllocationCompany,
        Dictionary<Guid, int?> ViaSarrafGroupCompany)
    {
        public int? Resolve(LedgerRow row)
        {
            if (row.ContractId.HasValue && ContractCompany.TryGetValue(row.ContractId.Value, out var byContract))
            {
                return byContract;
            }

            if (Enum.TryParse<PaymentKind>(row.SourceType, out _)
                && PaymentCompany.TryGetValue(row.SourceId, out var byPayment)
                && byPayment.HasValue)
            {
                return byPayment.Value;
            }

            if (row.SourceType == SarrafSettlementService.SupplierLedgerSourceType
                && SarrafSettlementCompany.TryGetValue(row.SourceId, out var bySarraf)
                && bySarraf.HasValue)
            {
                return bySarraf.Value;
            }

            if (row.SourceType == ThreeWaySettlementSourceType
                && ThreeWaySettlementCompany.TryGetValue(row.SourceId, out var byThreeWay)
                && byThreeWay.HasValue)
            {
                return byThreeWay.Value;
            }

            if (IsOwnTransferSourceType(row.SourceType)
                && TransferCompany.TryGetValue(row.SourceId, out var byTransfer)
                && byTransfer.HasValue)
            {
                return byTransfer.Value;
            }

            if (IsLegacyAllocationSourceType(row.SourceType)
                && AllocationCompany.TryGetValue(row.SourceId, out var byAllocation)
                && byAllocation.HasValue)
            {
                return byAllocation.Value;
            }

            // «پرداخت از طریق صراف»: قرارداد لنگِ مکملِ همان گروه هم شرکت را اثبات می‌کند.
            if (IsViaSarrafSourceType(row.SourceType)
                && row.ViaSarrafGroupId.HasValue
                && ViaSarrafGroupCompany.TryGetValue(row.ViaSarrafGroupId.Value, out var byGroup)
                && byGroup.HasValue)
            {
                return byGroup.Value;
            }

            return null;
        }
    }

    /// <summary>دو لنگِ «پرداخت از طریق صراف» خام — بدون PaymentTransaction و بدون CashAccount.</summary>
    private static bool IsViaSarrafSourceType(string sourceType)
        => sourceType == PaymentsController.ViaSarrafSupplierLedgerSourceType
            || sourceType == PaymentsController.ViaSarrafPayableLedgerSourceType;

    private sealed record ViaSarrafGroupContractRow(Guid GroupId, int ContractId);

    private sealed record LedgerRow(
        int Id,
        DateTime EntryDate,
        LedgerSide Side,
        decimal AmountUsd,
        string SourceType,
        int SourceId,
        int? ContractId,
        decimal? SourceAmount,
        string? SourceCurrencyCode,
        decimal? AppliedFxRateToUsd,
        string? Description,
        decimal? AppliedCurrencyPerUsdRate,
        Guid? ViaSarrafGroupId);
}
