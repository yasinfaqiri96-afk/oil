using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;

namespace PTGOilSystem.Web.Services;

/// <summary>
/// قواعد تأمین‌کنندهٔ قرارداد خرید. قرارداد عادی یک <see cref="Contract.SupplierId"/> دارد؛
/// قرارداد چندتأمین‌کننده فهرست <see cref="Contract.ContractSuppliers"/> دارد و SupplierId خالی است.
/// طلب هر بارگیری به <c>loading.SupplierId ?? contract.SupplierId</c> تعلق دارد؛ بارگیری بدون
/// تأمین‌کننده در قرارداد چندتأمین‌کننده به نسبت ارزش خرید هر تأمین‌کننده تقسیم می‌شود
/// (<see cref="PayableShares"/>).
/// </summary>
public static class ContractSupplierScope
{
    /// <summary>تأمین‌کننده‌ای که طلبِ این بارگیری به او می‌رسد.</summary>
    public static int? EffectiveSupplierId(LoadingRegister loading, Contract? contract)
        => loading.SupplierId ?? contract?.SupplierId;

    /// <summary>
    /// سهم هر تأمین‌کننده از طلبِ این بارگیری (کسرها جمعاً ۱).
    /// <list type="bullet">
    /// <item>بارگیری با تأمین‌کنندهٔ مشخص، یا قرارداد عادی: یک سهم کامل (رفتار قبلی).</item>
    /// <item>قرارداد چندتأمین‌کننده و بارگیری بدون تأمین‌کننده: به نسبت ارزش خرید هر ردیف
    /// (<see cref="ContractSupplier.PurchaseValueUsd"/>)؛ مقدار تُن بارگیری تقسیم نمی‌شود.</item>
    /// <item>اگر سهم قطعی نباشد (ردیف بدون مقدار/قیمت یا ContractSuppliers بارگذاری نشده) null.</item>
    /// </list>
    /// </summary>
    public static IReadOnlyList<(int SupplierId, decimal Fraction)>? PayableShares(LoadingRegister loading, Contract? contract)
    {
        ArgumentNullException.ThrowIfNull(loading);
        if (EffectiveSupplierId(loading, contract) is { } single)
        {
            return [(single, 1m)];
        }

        return contract is null ? null : PurchaseShares(contract);
    }

    /// <summary>سهم ارزشی تأمین‌کننده‌های قرارداد چندتأمین‌کننده؛ null وقتی ردیفی مقدار یا قیمت ندارد.</summary>
    public static IReadOnlyList<(int SupplierId, decimal Fraction)>? PurchaseShares(Contract contract)
    {
        var rows = contract.ContractSuppliers.OrderBy(s => s.Id).ThenBy(s => s.SupplierId).ToList();
        if (rows.Count == 0 || rows.Any(r => r.PurchaseValueUsd is not > 0m))
        {
            return null;
        }

        var total = rows.Sum(r => r.PurchaseValueUsd!.Value);
        return rows.Select(r => (r.SupplierId, r.PurchaseValueUsd!.Value / total)).ToList();
    }

    /// <summary>
    /// تقسیم یک مبلغ بین سهم‌ها با گرد کردن ۴ رقم؛ باقیماندهٔ گرد کردن به آخرین سهم می‌رسد تا
    /// جمع سهم‌ها دقیقاً همان مبلغ باشد (سند دفتر کل تراز بماند).
    /// </summary>
    public static IReadOnlyList<(int SupplierId, decimal Amount)> SplitAmount(
        decimal amount,
        IReadOnlyList<(int SupplierId, decimal Fraction)> shares)
    {
        if (shares.Count == 1)
        {
            return [(shares[0].SupplierId, amount)];
        }

        var result = new List<(int SupplierId, decimal Amount)>(shares.Count);
        var allocated = 0m;
        for (var i = 0; i < shares.Count; i++)
        {
            var part = i == shares.Count - 1
                ? amount - allocated
                : decimal.Round(amount * shares[i].Fraction, 4, MidpointRounding.AwayFromZero);
            allocated += part;
            result.Add((shares[i].SupplierId, part));
        }

        return result;
    }

    /// <summary>سهم یک تأمین‌کنندهٔ مشخص از مبلغ (برای هماهنگ‌سازی سطر موجود).</summary>
    public static decimal? ShareOf(decimal amount, IReadOnlyList<(int SupplierId, decimal Fraction)> shares, int supplierId)
    {
        foreach (var (id, part) in SplitAmount(amount, shares))
        {
            if (id == supplierId)
            {
                return part;
            }
        }

        return null;
    }

    /// <summary>
    /// مجموع مقدار، ارزش و میانگین موزون قیمت ردیف‌های تأمین‌کننده. null وقتی سهم قطعی نیست.
    /// </summary>
    public static (decimal QuantityMt, decimal ValueUsd, decimal AveragePriceUsd)? PurchaseTotals(IEnumerable<ContractSupplier> rows)
    {
        var list = rows.ToList();
        if (list.Count == 0 || list.Any(r => r.PurchaseValueUsd is not > 0m))
        {
            return null;
        }

        var quantity = list.Sum(r => r.QuantityMt!.Value);
        var value = list.Sum(r => r.PurchaseValueUsd!.Value);
        return (quantity, value, decimal.Round(value / quantity, 4, MidpointRounding.AwayFromZero));
    }

    /// <summary>
    /// ردیف‌های تأمین‌کنندهٔ قرارداد بدون تأمین‌کننده را بارگذاری می‌کند تا <see cref="PayableShares"/>
    /// سهم‌ها را ببیند. قرارداد عادی (SupplierId دارد) هیچ کوئری اضافه‌ای ندارد.
    /// </summary>
    public static async Task EnsureSuppliersLoadedAsync(
        ApplicationDbContext db,
        Contract contract,
        CancellationToken cancellationToken = default)
    {
        if (contract.SupplierId.HasValue || contract.Id <= 0)
        {
            return;
        }

        var entry = db.Entry(contract);
        if (entry.State != EntityState.Detached)
        {
            var collection = entry.Collection(c => c.ContractSuppliers);
            if (!collection.IsLoaded)
            {
                await collection.LoadAsync(cancellationToken);
            }

            return;
        }

        if (contract.ContractSuppliers.Count == 0)
        {
            contract.ContractSuppliers = await db.ContractSuppliers.AsNoTracking()
                .Where(s => s.ContractId == contract.Id)
                .ToListAsync(cancellationToken);
        }
    }

    /// <summary>فیلتر قراردادهایی که این تأمین‌کننده در آن‌ها طرف است (عادی یا چندتأمین‌کننده).</summary>
    public static Expression<Func<Contract, bool>> HasSupplier(int supplierId)
        => c => c.SupplierId == supplierId || c.ContractSuppliers.Any(s => s.SupplierId == supplierId);

    public static Expression<Func<Contract, bool>> HasAnySupplier(IReadOnlyCollection<int> supplierIds)
        => c => (c.SupplierId != null && supplierIds.Contains(c.SupplierId.Value))
            || c.ContractSuppliers.Any(s => supplierIds.Contains(s.SupplierId));

    public static IQueryable<Contract> WhereSupplier(this IQueryable<Contract> query, int supplierId)
        => query.Where(HasSupplier(supplierId));

    /// <summary>همهٔ تأمین‌کننده‌های قرارداد؛ قرارداد عادی فقط SupplierId خودش را دارد.</summary>
    public static async Task<IReadOnlyList<int>> SupplierIdsAsync(
        ApplicationDbContext db,
        int contractId,
        CancellationToken cancellationToken = default)
    {
        var multi = await db.ContractSuppliers.AsNoTracking()
            .Where(s => s.ContractId == contractId)
            .Select(s => s.SupplierId)
            .ToListAsync(cancellationToken);
        if (multi.Count > 0)
        {
            return multi;
        }

        var single = await db.Contracts.AsNoTracking()
            .Where(c => c.Id == contractId)
            .Select(c => c.SupplierId)
            .FirstOrDefaultAsync(cancellationToken);
        return single.HasValue ? [single.Value] : [];
    }

    /// <summary>آیا این تأمین‌کننده طرف این قرارداد است؟</summary>
    public static Task<bool> AllowsSupplierAsync(
        ApplicationDbContext db,
        int contractId,
        int supplierId,
        CancellationToken cancellationToken = default)
        => db.Contracts.AsNoTracking()
            .Where(c => c.Id == contractId)
            .AnyAsync(HasSupplier(supplierId), cancellationToken);

    /// <summary>
    /// true وقتی قرارداد تأمین‌کننده (یا فهرست تأمین‌کننده) دارد و این تأمین‌کننده جزو آن نیست.
    /// برای قرارداد عادی دقیقاً همان شرط قدیمی <c>contract.SupplierId != supplierId</c> است.
    /// </summary>
    public static async Task<bool> IsForeignSupplierAsync(
        ApplicationDbContext db,
        Contract contract,
        int supplierId,
        CancellationToken cancellationToken = default)
    {
        if (contract.SupplierId.HasValue)
        {
            return contract.SupplierId.Value != supplierId;
        }

        var ids = await db.ContractSuppliers.AsNoTracking()
            .Where(s => s.ContractId == contract.Id)
            .Select(s => s.SupplierId)
            .ToListAsync(cancellationToken);
        return ids.Count > 0 && !ids.Contains(supplierId);
    }

    public static Task<bool> IsMultiSupplierAsync(
        ApplicationDbContext db,
        int contractId,
        CancellationToken cancellationToken = default)
        => db.ContractSuppliers.AsNoTracking().AnyAsync(s => s.ContractId == contractId, cancellationToken);

    /// <summary>
    /// نام تأمین‌کننده‌های هر قرارداد برای نمایش («الف، ب، ج»). قرارداد عادی در نتیجه نمی‌آید.
    /// </summary>
    public static async Task<IReadOnlyDictionary<int, string>> MultiSupplierNamesAsync(
        ApplicationDbContext db,
        IEnumerable<int> contractIds,
        CancellationToken cancellationToken = default)
    {
        var ids = contractIds.Distinct().ToList();
        if (ids.Count == 0)
        {
            return new Dictionary<int, string>();
        }

        var rows = await db.ContractSuppliers.AsNoTracking()
            .Where(s => ids.Contains(s.ContractId))
            .OrderBy(s => s.Id)
            .Select(s => new { s.ContractId, Name = s.Supplier != null ? s.Supplier.Name : "" })
            .ToListAsync(cancellationToken);
        return rows
            .GroupBy(r => r.ContractId)
            .ToDictionary(g => g.Key, g => string.Join("، ", g.Select(r => r.Name)));
    }
}
