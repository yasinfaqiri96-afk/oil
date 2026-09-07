using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services.PartyStatements;

namespace PTGOilSystem.Web.Services;

/// <summary>
/// تفاوت نرخ ارزِ «تسویهٔ بدهی ارزی» تأمین‌کننده — فقط خواندنی.
///
/// مسئله: بدهی خرید وقتی ارز تسویه‌اش روبل است، به همان روبل ایجاد می‌شود ولی در دفتر با
/// نرخ روز بارگیری به دالر ثبت می‌شود. پرداخت هم با نرخ روز خودش به دالر ثبت می‌شود. وقتی
/// این دو نرخ فرق دارند، جمعِ دالریِ حساب نه بدهی است نه طلب — بخشی از آن «تفاوت نرخ» است
/// که هنوز شناسایی نشده. مثال واقعی: ۷۱ میلیون روبل بدهی (۳۵ با نرخ ۷۰ و ۳۶ با نرخ ۸۰) و
/// ۷۰ میلیون روبل پرداخت با نرخ ۷۰ → به روبل هنوز ۱ میلیون بدهکاریم، ولی جمعِ دالری
/// ۵۰٬۰۰۰ طلب نشان می‌دهد. ۶۲٬۵۰۰ از آن ضررِ تفاوت نرخ است و ۱۲٬۵۰۰ بدهیِ باقی‌مانده.
///
/// روش: سطرهای ارزیِ همان تأمین‌کننده به ترتیب تاریخ، FIFO با هم تطبیق می‌شوند — هر واحد
/// پرداخت، قدیمی‌ترین بدهیِ باز را می‌بندد. تفاوتِ «نرخ دفتریِ بدهی» با «نرخ دفتریِ پرداخت»
/// روی همان مقدارِ ارز، تفاوت نرخِ محقق‌شده است. نرخ هر سطر از خودِ سطر می‌آید
/// (AmountUsd ÷ SourceAmount)، نه از جدول نرخ، پس هیچ ورودی نرخ جدیدی لازم نیست.
///
/// این سرویس چیزی در دفتر نمی‌نویسد و هیچ مبلغی را تغییر نمی‌دهد؛ فقط همان داده‌های
/// موجود را طوری می‌خواند که «بدهیِ واقعی» از «تفاوت نرخ» جدا شود.
/// </summary>
public interface ISupplierFxSettlementService
{
    Task<SupplierFxSettlement> GetAsync(int supplierId, CancellationToken ct = default);

    Task<IReadOnlyDictionary<int, SupplierFxSettlement>> GetManyAsync(
        IReadOnlyCollection<int> supplierIds,
        int? contractId = null,
        DateTime? toDate = null,
        CancellationToken ct = default);
}

/// <summary>
/// نتیجهٔ تطبیق برای یک ارزِ غیرپایه (عملاً RUB).
/// </summary>
/// <param name="CurrencyCode">ارز بدهی (مثلاً RUB).</param>
/// <param name="OutstandingSourceAmount">مانده به همان ارز. مثبت = هنوز بدهکاریم، منفی = اضافه پرداخت شده.</param>
/// <param name="OutstandingBookUsd">ارزش دفتریِ همان مانده با نرخِ خودِ سطرهای باز.</param>
/// <param name="RealizedFxDifferenceUsd">تفاوت نرخِ محقق‌شده. مثبت = ضرر، منفی = سود.</param>
/// <param name="HistoricalNetUsd">جمعِ دالریِ فعلیِ همان سطرها (آنچه سیستم امروز نشان می‌دهد). مثبت = بدهکاریم.</param>
/// <param name="MatchedSourceAmount">مقدار ارزی که تطبیق شده است.</param>
/// <param name="LastMatchedDate">تاریخ آخرین سندی که در تطبیق شرکت کرده — تاریخ شناسایی تفاوت نرخ.</param>
public sealed record SupplierFxCurrencySettlement(
    string CurrencyCode,
    decimal OutstandingSourceAmount,
    decimal OutstandingBookUsd,
    decimal RealizedFxDifferenceUsd,
    decimal HistoricalNetUsd,
    decimal MatchedSourceAmount,
    DateTime LastMatchedDate)
{
    /// <summary>ضرر تفاوت نرخ (فقط سمت مثبت).</summary>
    public decimal FxLossUsd => RealizedFxDifferenceUsd > 0m ? RealizedFxDifferenceUsd : 0m;

    /// <summary>سود تفاوت نرخ (فقط سمت منفی، بدون علامت).</summary>
    public decimal FxGainUsd => RealizedFxDifferenceUsd < 0m ? -RealizedFxDifferenceUsd : 0m;

    public bool HasFxDifference => RealizedFxDifferenceUsd != 0m;
}

public sealed record SupplierFxSettlement(
    int SupplierId,
    IReadOnlyList<SupplierFxCurrencySettlement> Currencies)
{
    public static SupplierFxSettlement Empty(int supplierId) => new(supplierId, []);

    public bool HasForeignCurrencyDebt => Currencies.Count > 0;

    /// <summary>مجموع تفاوت نرخ همهٔ ارزها. مثبت = ضرر.</summary>
    public decimal RealizedFxDifferenceUsd => Currencies.Sum(c => c.RealizedFxDifferenceUsd);

    /// <summary>
    /// مانده دالریِ اصلاح‌شده: جمعِ تاریخی منهای تفاوت نرخِ شناسایی‌شده.
    /// مثبت = هنوز به تأمین‌کننده بدهکاریم.
    /// </summary>
    public decimal AdjustedBalanceUsd => Currencies.Sum(c => c.OutstandingBookUsd);

    public decimal HistoricalNetUsd => Currencies.Sum(c => c.HistoricalNetUsd);

    /// <summary>تاریخ شناسایی: آخرین سندی که در تطبیق شرکت کرده است.</summary>
    public DateTime? RecognitionDate => Currencies.Count == 0
        ? null
        : Currencies.Max(c => c.LastMatchedDate);
}

public sealed class SupplierFxSettlementService : ISupplierFxSettlementService
{
    private readonly ApplicationDbContext _db;

    public SupplierFxSettlementService(ApplicationDbContext db) => _db = db;

    /// <summary>
    /// انتقالِ مانده بین قراردادهای همین تأمین‌کننده، جابه‌جاییِ داخلی است نه بدهی/پرداخت؛
    /// در تطبیق FIFO شرکت نمی‌کند تا صفِ بدهی‌ها را جابه‌جا نکند. (خودِ انتقال و برگشتش در
    /// جمعِ دالری هم خنثی‌اند.)
    /// </summary>
    private static bool IsInternalReclassification(string sourceType)
        => sourceType.Contains("Transfer", StringComparison.OrdinalIgnoreCase);

    public async Task<SupplierFxSettlement> GetAsync(int supplierId, CancellationToken ct = default)
    {
        var results = await GetManyAsync([supplierId], ct: ct);
        return results.GetValueOrDefault(supplierId, SupplierFxSettlement.Empty(supplierId));
    }

    public async Task<IReadOnlyDictionary<int, SupplierFxSettlement>> GetManyAsync(
        IReadOnlyCollection<int> supplierIds,
        int? contractId = null,
        DateTime? toDate = null,
        CancellationToken ct = default)
    {
        var ids = supplierIds.Distinct().ToArray();
        if (ids.Length == 0)
        {
            return new Dictionary<int, SupplierFxSettlement>();
        }

        var query = _db.LedgerEntries
            .AsNoTracking()
            .Where(LedgerEntryOwnership.SupplierOwnedAny(ids))
            .WhereEffectiveSarrafSettlementLegs(_db)
            .Where(l => l.SourceAmount != null
                && l.SourceAmount > 0m
                && l.SourceCurrencyCode != null
                && l.SourceCurrencyCode != SystemCurrency.BaseCurrencyCode);

        if (contractId.HasValue)
        {
            query = query.Where(l => l.ContractId == contractId.Value);
        }

        if (toDate.HasValue)
        {
            var end = toDate.Value.Date.AddDays(1);
            query = query.Where(l => l.EntryDate < end);
        }

        var rows = await query
            .Select(l => new FxRow(
                l.SupplierId ?? (l.Contract != null ? l.Contract.SupplierId : null),
                l.Id,
                l.EntryDate,
                l.Side,
                l.AmountUsd,
                l.SourceAmount!.Value,
                l.SourceCurrencyCode!,
                l.SourceType,
                l.SourceId))
            .ToListAsync(ct);

        if (rows.Count == 0)
        {
            return new Dictionary<int, SupplierFxSettlement>();
        }

        return rows
            .Where(r => r.SupplierId.HasValue && !IsInternalReclassification(r.SourceType))
            .GroupBy(r => r.SupplierId!.Value)
            .ToDictionary(
                supplier => supplier.Key,
                supplier => new SupplierFxSettlement(
                    supplier.Key,
                    supplier
                        .GroupBy(r => SystemCurrency.Normalize(r.SourceCurrencyCode))
                        .Select(g => Match(g.Key, g.ToList()))
                        .Where(c => c is not null)
                        .Select(c => c!)
                        .OrderBy(c => c.CurrencyCode, StringComparer.Ordinal)
                        .ToList()));
    }

    /// <summary>
    /// تطبیق FIFO یک ارز. بدهی = Credit (شرکت گرفته)، پرداخت = Debit (شرکت داده) — همان
    /// قراردادِ علامتِ بقیهٔ سیستم.
    /// </summary>
    private static SupplierFxCurrencySettlement? Match(string currencyCode, List<FxRow> rows)
    {
        // ترتیب کسب‌وکار، نه ترتیب درج: تاریخ سند، بعد منبع، بعد شناسهٔ سطر. سطرهای دفتر
        // لزوماً به ترتیب بارگیری ساخته نشده‌اند، پس Id به‌تنهایی FIFO درست نمی‌دهد.
        static IEnumerable<FxRow> Ordered(IEnumerable<FxRow> source) => source
            .OrderBy(r => r.EntryDate)
            .ThenBy(r => r.SourceType, StringComparer.Ordinal)
            .ThenBy(r => r.SourceId)
            .ThenBy(r => r.Id);

        var debts = Ordered(rows.Where(r => r.Side == LedgerSide.Credit))
            .Select(r => new OpenRow(r))
            .ToList();
        var payments = Ordered(rows.Where(r => r.Side == LedgerSide.Debit))
            .Select(r => new OpenRow(r))
            .ToList();

        if (debts.Count == 0 && payments.Count == 0)
        {
            return null;
        }

        var fxDifference = 0m;
        var matched = 0m;
        var debtIndex = 0;

        foreach (var payment in payments)
        {
            while (payment.Remaining > 0m && debtIndex < debts.Count)
            {
                var debt = debts[debtIndex];
                if (debt.Remaining <= 0m)
                {
                    debtIndex++;
                    continue;
                }

                var amount = Math.Min(payment.Remaining, debt.Remaining);

                // نرخِ دفتریِ خودِ سطر: ارزشِ دالری که واقعاً ثبت شده، تقسیم بر مقدار ارز.
                // عمداً از AppliedFxRateToUsd استفاده نمی‌شود چون گرد شده و جمعِ سطر را
                // بازتولید نمی‌کند؛ این نسبت دقیقاً با AmountUsd همان سطر سازگار است.
                fxDifference += amount * (payment.RateToUsd - debt.RateToUsd);
                matched += amount;

                payment.Remaining -= amount;
                debt.Remaining -= amount;
            }

            if (payment.Remaining <= 0m)
            {
                continue;
            }

            // پرداختِ مازاد بر کل بدهی: بدهیِ باز ندارد که با آن تفاوت نرخ بسازد، پس با
            // نرخ خودش به‌عنوان مانده منفی (اضافه پرداخت) باقی می‌ماند.
            break;
        }

        var outstandingDebt = debts.Sum(d => d.Remaining);
        var outstandingDebtUsd = debts.Sum(d => d.Remaining * d.RateToUsd);
        var unusedPayment = payments.Sum(p => p.Remaining);
        var unusedPaymentUsd = payments.Sum(p => p.Remaining * p.RateToUsd);

        var historicalNetUsd = rows.Sum(r => r.Side == LedgerSide.Credit ? r.AmountUsd : -r.AmountUsd);

        return new SupplierFxCurrencySettlement(
            currencyCode,
            Round4(outstandingDebt - unusedPayment),
            Round4(outstandingDebtUsd - unusedPaymentUsd),
            Round4(fxDifference),
            Round4(historicalNetUsd),
            Round4(matched),
            rows.Max(r => r.EntryDate));
    }

    private static decimal Round4(decimal value)
        => decimal.Round(value, 4, MidpointRounding.AwayFromZero);

    private sealed record FxRow(
        int? SupplierId,
        int Id,
        DateTime EntryDate,
        LedgerSide Side,
        decimal AmountUsd,
        decimal SourceAmount,
        string SourceCurrencyCode,
        string SourceType,
        int SourceId);

    private sealed class OpenRow
    {
        public OpenRow(FxRow row)
        {
            Row = row;
            Remaining = row.SourceAmount;
            RateToUsd = row.SourceAmount == 0m ? 0m : row.AmountUsd / row.SourceAmount;
        }

        public FxRow Row { get; }
        public decimal Remaining { get; set; }

        /// <summary>نرخ دفتریِ همین سطر: هر واحد ارز، چند دالر ثبت شده است.</summary>
        public decimal RateToUsd { get; }
    }
}
