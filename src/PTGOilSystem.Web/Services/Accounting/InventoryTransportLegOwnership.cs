using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;

namespace PTGOilSystem.Web.Services.Accounting;

/// <summary>
/// سهم یک شرکت داخلی از بارِ یک مرحلهٔ حمل.
///
/// یک حملِ فیزیکی واحد می‌تواند از قراردادهای چند شرکت پر شده باشد
/// (۱۰ تن از P-016/شرکت A و ۲۰ تن از P-017/شرکت B در یک موترِ ۳۰ تنی).
/// حمل تقسیم نمی‌شود؛ فقط مالکیت اقتصادی‌اش به تفکیک شرکت خوانده می‌شود.
/// </summary>
public sealed record LegCompanyOwnershipSlice(
    int CompanyId,
    decimal QuantityMt,
    IReadOnlyList<int> ContractIds)
{
    /// <summary>وقتی این شرکت در همین حمل بیش از یک قرارداد دارد، بُعدِ قرارداد معنای واحد ندارد.</summary>
    public int? SingleContractId => ContractIds.Count == 1 ? ContractIds[0] : null;
}

public interface IInventoryTransportLegOwnershipResolver
{
    Task<IReadOnlyList<LegCompanyOwnershipSlice>> ResolveCompanyOwnershipSlicesAsync(
        InventoryTransportLeg leg,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// مالکیتِ شرکتیِ یک مرحلهٔ حمل را از روی سهم‌های منبع (<see cref="InventoryTransportLegAllocation"/>)
/// می‌سازد و بر اساس شرکتِ قرارداد گروه می‌کند.
///
/// سهم‌های منبع، وقتی وجود دارند، تنها مرجع مالکیت‌اند: قرارداد سرصفحهٔ leg فقط اولین/بزرگ‌ترین
/// سهم است و در حملِ چندقراردادی مالکیتِ کل بار را نشان نمی‌دهد. حمل‌های قدیمیِ بدون سهم
/// (و legهای تک‌منبعیِ ساخته‌شده در ثبت محموله) به همان قرارداد سرصفحه برمی‌گردند، پس رفتارشان
/// دقیقاً مثل قبل می‌ماند.
///
/// مجموع سهم‌های خروجی همیشه دقیقاً برابر <c>leg.QuantityMt</c> است؛ باقیماندهٔ گِرد به
/// بزرگ‌ترین سهم می‌رود تا هیچ کسری از بار بی‌مالک نماند.
/// </summary>
public sealed class InventoryTransportLegOwnershipResolver(ApplicationDbContext db)
    : IInventoryTransportLegOwnershipResolver
{
    public async Task<IReadOnlyList<LegCompanyOwnershipSlice>> ResolveCompanyOwnershipSlicesAsync(
        InventoryTransportLeg leg,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(leg);
        if (leg.QuantityMt <= 0m)
            return [];

        var allocations = await db.InventoryTransportLegAllocations
            .AsNoTracking()
            .Where(a => a.InventoryTransportLegId == leg.Id && a.QuantityMt > 0m)
            .Select(a => new { a.SourcePurchaseContractId, a.QuantityMt })
            .ToListAsync(cancellationToken);

        var rows = allocations.Count > 0
            ? allocations.Select(a => (ContractId: a.SourcePurchaseContractId, a.QuantityMt)).ToList()
            : [(ContractId: leg.SourcePurchaseContractId, QuantityMt: leg.QuantityMt)];

        var contractIds = rows.Select(r => r.ContractId).Distinct().ToList();
        var companyByContract = await db.Contracts
            .AsNoTracking()
            .Where(c => contractIds.Contains(c.Id))
            .Select(c => new { c.Id, c.CompanyId })
            .ToDictionaryAsync(c => c.Id, c => c.CompanyId, cancellationToken);

        // قراردادِ گمشده یعنی مالکیت این بار قابل اثبات نیست؛ حدس زدن بدتر از ثبت‌نکردن است.
        if (contractIds.Any(id => !companyByContract.ContainsKey(id)))
            return [];

        var grouped = rows
            .GroupBy(r => companyByContract[r.ContractId])
            .Select(g => new
            {
                CompanyId = g.Key,
                QuantityMt = g.Sum(r => r.QuantityMt),
                ContractIds = (IReadOnlyList<int>)g.Select(r => r.ContractId).Distinct().OrderBy(x => x).ToList()
            })
            .OrderBy(x => x.CompanyId)
            .ToList();

        var totalMt = grouped.Sum(x => x.QuantityMt);
        if (totalMt <= 0m)
            return [];

        // سهم‌ها به مقدار واقعی خودِ حمل مقیاس می‌شوند: سندِ فیزیکی، نه جمعِ سهم‌ها، مبنای مقدار است.
        var quantities = ProportionalSplit(leg.QuantityMt, grouped.Select(x => x.QuantityMt).ToList());
        var slices = new List<LegCompanyOwnershipSlice>(grouped.Count);
        for (var i = 0; i < grouped.Count; i++)
        {
            if (quantities[i] <= 0m)
                continue;
            slices.Add(new LegCompanyOwnershipSlice(grouped[i].CompanyId, quantities[i], grouped[i].ContractIds));
        }

        return slices;
    }

    /// <summary>
    /// یک مقدار را به نسبت وزن‌های داده‌شده تقسیم می‌کند، به‌طوری‌که جمعِ نتیجه دقیقاً برابر
    /// همان مقدار بماند. باقیماندهٔ گِرد و سهم‌های حذف‌شدهٔ صفر به بزرگ‌ترین سهم می‌روند.
    /// </summary>
    public static IReadOnlyList<decimal> ProportionalSplit(decimal total, IReadOnlyList<decimal> weights)
    {
        var result = new decimal[weights.Count];
        if (weights.Count == 0)
            return result;

        var totalWeight = weights.Sum();
        if (totalWeight <= 0m)
            return result;

        var assigned = 0m;
        for (var i = 0; i < weights.Count; i++)
        {
            result[i] = i == weights.Count - 1
                ? total - assigned
                : decimal.Round(total * weights[i] / totalWeight, 4, MidpointRounding.AwayFromZero);
            assigned += result[i];
        }

        // سهم‌های ناچیز حذف می‌شوند، ولی مقدارشان گم نمی‌شود: به بزرگ‌ترین سهم اضافه می‌شود.
        var dropped = 0m;
        var largest = 0;
        for (var i = 0; i < result.Length; i++)
        {
            if (result[i] <= 0m)
            {
                dropped += result[i];
                result[i] = 0m;
            }
            else if (result[i] > result[largest])
            {
                largest = i;
            }
        }

        if (dropped != 0m && result[largest] > 0m)
            result[largest] += dropped;

        return result;
    }
}
