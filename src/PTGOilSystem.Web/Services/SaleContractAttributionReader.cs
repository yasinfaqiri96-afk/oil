using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;

namespace PTGOilSystem.Web.Services;

/// <summary>
/// سهم اثبات‌شدهٔ یک قرارداد خرید از یک فروش. مقدار و مبلغ هر دو از
/// <c>SalesTransactionSourceAllocations</c> می‌آیند و جمعشان روی یک فروش دقیقاً
/// برابر <c>SalesTransaction.TotalUsd</c> است.
/// </summary>
public sealed record SaleContractShare(
    int SalesTransactionId,
    int SourcePurchaseContractId,
    decimal QuantityMt,
    decimal AmountUsd);

/// <summary>
/// پاسخ فقط‌خواندنیِ «کدام قرارداد خرید چقدر از این فروش را دارد».
/// فروشی که ردیف allocation ندارد «اثبات‌نشده» است و این نقشه دربارهٔ آن هیچ حدسی نمی‌زند؛
/// فراخوان باید خودش تصمیم بگیرد (معمولاً: رفتار قدیمیِ همان صفحه را نگه دارد).
/// </summary>
public sealed class SaleContractAttributionMap
{
    public static SaleContractAttributionMap Empty { get; } =
        new(new Dictionary<int, IReadOnlyList<SaleContractShare>>());

    private readonly IReadOnlyDictionary<int, IReadOnlyList<SaleContractShare>> _bySale;

    internal SaleContractAttributionMap(IReadOnlyDictionary<int, IReadOnlyList<SaleContractShare>> bySale)
    {
        _bySale = bySale;
    }

    /// <summary>آیا برای این فروش انتساب اثبات‌شده وجود دارد؟</summary>
    public bool HasProvenAllocation(int salesTransactionId)
        => _bySale.ContainsKey(salesTransactionId);

    /// <summary>سهم‌های اثبات‌شدهٔ یک فروش (خالی یعنی اثبات‌نشده).</summary>
    public IReadOnlyList<SaleContractShare> SharesFor(int salesTransactionId)
        => _bySale.TryGetValue(salesTransactionId, out var shares) ? shares : [];

    /// <summary>
    /// سهم یک قرارداد مشخص از یک فروش. <c>null</c> یعنی انتساب اثبات‌شده‌ای در کار نیست؛
    /// <c>0</c> یعنی اثبات شده که این قرارداد سهمی ندارد.
    /// </summary>
    public (decimal QuantityMt, decimal AmountUsd)? ShareFor(int salesTransactionId, int purchaseContractId)
    {
        if (!_bySale.TryGetValue(salesTransactionId, out var shares))
        {
            return null;
        }

        var quantityMt = 0m;
        var amountUsd = 0m;
        foreach (var share in shares)
        {
            if (share.SourcePurchaseContractId != purchaseContractId)
            {
                continue;
            }

            quantityMt += share.QuantityMt;
            amountUsd += share.AmountUsd;
        }

        return (quantityMt, amountUsd);
    }
}

/// <summary>
/// تنها مرجع خواندنِ «این فروش به کدام قرارداد خرید تعلق دارد».
/// </summary>
public interface ISaleContractAttributionReader
{
    Task<SaleContractAttributionMap> LoadForSalesAsync(
        IReadOnlyCollection<int> salesTransactionIds,
        CancellationToken ct = default);

    Task<SaleContractAttributionMap> LoadForPurchaseContractAsync(
        int purchaseContractId,
        CancellationToken ct = default);
}

/// <summary>
/// منبع واحد حقیقتِ انتسابِ «فروش → قرارداد خرید».
/// <para>
/// فقط <c>SalesTransactionSourceAllocations</c> خوانده می‌شود؛ همان ردیف‌هایی که از
/// <c>InventoryMovement.ContractId</c> واقعی (FIFO) ساخته شده‌اند. نه
/// <c>SalesTransaction.ContractId</c> (که قرارداد <em>فروش</em> است، نه خرید)، نه
/// <c>SalesTransaction.SourcePurchaseContractId</c> (که فقط برای فروش تک‌قراردادی پر
/// می‌شود) و نه <c>LedgerEntry.ContractId</c> مبنای انتساب قرار نمی‌گیرند.
/// </para>
/// <para>
/// این کلاس چیزی نمی‌نویسد و هیچ قراردادی را حدس نمی‌زند. فروشِ بدون allocation
/// «اثبات‌نشده» برمی‌گردد تا صفحه بتواند بدون ساختن عدد ساختگی تصمیم بگیرد.
/// </para>
/// </summary>
public sealed class SaleContractAttributionReader : ISaleContractAttributionReader
{
    private readonly ApplicationDbContext _db;

    public SaleContractAttributionReader(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<SaleContractAttributionMap> LoadForSalesAsync(
        IReadOnlyCollection<int> salesTransactionIds,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(salesTransactionIds);
        if (salesTransactionIds.Count == 0)
        {
            return SaleContractAttributionMap.Empty;
        }

        var ids = salesTransactionIds.Distinct().ToArray();
        var rows = await _db.SalesTransactionSourceAllocations
            .AsNoTracking()
            .Where(a => ids.Contains(a.SalesTransactionId))
            .Select(a => new SaleContractShare(
                a.SalesTransactionId,
                a.SourcePurchaseContractId,
                a.QuantityMt,
                a.AmountUsd))
            .ToListAsync(ct);

        return Build(rows);
    }

    public async Task<SaleContractAttributionMap> LoadForPurchaseContractAsync(
        int purchaseContractId,
        CancellationToken ct = default)
    {
        // ابتدا فروش‌هایی که این قرارداد در آن‌ها سهم دارد، بعد همهٔ سهم‌های همان فروش‌ها؛
        // بدون گام دوم نمی‌شد فهمید فروش چند-قراردادی است یا تک‌قراردادی.
        var saleIds = await _db.SalesTransactionSourceAllocations
            .AsNoTracking()
            .Where(a => a.SourcePurchaseContractId == purchaseContractId)
            .Select(a => a.SalesTransactionId)
            .Distinct()
            .ToListAsync(ct);

        return await LoadForSalesAsync(saleIds, ct);
    }

    private static SaleContractAttributionMap Build(IReadOnlyCollection<SaleContractShare> rows)
    {
        if (rows.Count == 0)
        {
            return SaleContractAttributionMap.Empty;
        }

        var bySale = rows
            .GroupBy(r => r.SalesTransactionId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<SaleContractShare>)g
                    .GroupBy(r => r.SourcePurchaseContractId)
                    .Select(c => new SaleContractShare(
                        g.Key,
                        c.Key,
                        c.Sum(r => r.QuantityMt),
                        c.Sum(r => r.AmountUsd)))
                    .OrderBy(c => c.SourcePurchaseContractId)
                    .ToList());

        return new SaleContractAttributionMap(bySale);
    }
}
