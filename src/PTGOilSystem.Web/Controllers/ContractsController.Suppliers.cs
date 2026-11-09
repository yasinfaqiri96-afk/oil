using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Models.Contracts;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services;

namespace PTGOilSystem.Web.Controllers;

/// <summary>
/// قرارداد خرید با چند تأمین‌کننده: یک قرارداد و یک کد. با بیش از یک تأمین‌کننده،
/// <see cref="Contract.SupplierId"/> خالی می‌ماند و فهرست در <see cref="Contract.ContractSuppliers"/>
/// است؛ طلب هر تأمین‌کننده فقط از بارگیری‌های خودش ثبت می‌شود.
/// </summary>
public partial class ContractsController
{
    /// <summary>تأمین‌کننده‌های انتخاب‌شده در فرم، به ترتیب و بدون تکرار.</summary>
    private static List<int> CollectSupplierIds(ContractFormViewModel model)
        => model.ContractType != ContractType.Purchase
            ? []
            : new[] { model.SupplierId }
                .Concat(model.AdditionalSupplierIds ?? [])
                .Where(id => id is > 0)
                .Select(id => id!.Value)
                .Distinct()
                .ToList();

    /// <summary>
    /// ردیف‌های تأمین‌کنندهٔ فرم با مقدار و قیمت هر نفر، به ترتیب و بدون تکرار (اولین ردیف هر
    /// تأمین‌کننده معتبر است).
    /// </summary>
    private static List<(int SupplierId, decimal? QuantityMt, decimal? UnitPriceUsd)> CollectSupplierTerms(ContractFormViewModel model)
    {
        if (model.ContractType != ContractType.Purchase)
        {
            return [];
        }

        var rows = new List<(int? SupplierId, decimal? QuantityMt, decimal? UnitPriceUsd)>
        {
            (model.SupplierId, model.PrimarySupplierQuantityMt, model.PrimarySupplierUnitPriceUsd)
        };
        var additional = model.AdditionalSupplierIds ?? [];
        var quantities = model.AdditionalSupplierQuantitiesMt ?? [];
        var prices = model.AdditionalSupplierUnitPricesUsd ?? [];
        for (var i = 0; i < additional.Count; i++)
        {
            rows.Add((
                additional[i],
                i < quantities.Count ? quantities[i] : null,
                i < prices.Count ? prices[i] : null));
        }

        return rows
            .Where(r => r.SupplierId is > 0)
            .GroupBy(r => r.SupplierId!.Value)
            .Select(g => (g.Key, g.First().QuantityMt, g.First().UnitPriceUsd))
            .ToList();
    }

    /// <summary>
    /// قرارداد چندتأمین‌کننده: مقدار و قیمت هر تأمین‌کننده اجباری است؛ مقدار قرارداد = جمع مقدارها و
    /// قیمت قرارداد = میانگین موزون (ارزش کل ÷ مقدار کل) به‌صورت «نرخ توافقی» USD.
    /// قرارداد تک‌تأمین‌کننده دست نمی‌خورد.
    /// </summary>
    private void ApplyMultiSupplierTerms(ContractFormViewModel model)
    {
        var terms = CollectSupplierTerms(model);
        if (terms.Count <= 1)
        {
            return;
        }

        if (terms.Any(t => t.QuantityMt is not > 0m || t.UnitPriceUsd is not > 0m))
        {
            ModelState.AddModelError(nameof(model.AdditionalSupplierIds), "برای هر تأمین‌کننده، مقدار (تُن) و قیمت فی تُن بزرگ‌تر از صفر الزامی است.");
            return;
        }

        var totals = ContractSupplierScope.PurchaseTotals(terms.Select(t => new ContractSupplier
        {
            SupplierId = t.SupplierId,
            QuantityMt = t.QuantityMt,
            UnitPriceUsd = t.UnitPriceUsd
        }))!.Value;

        model.QuantityMt = totals.QuantityMt;
        model.UiPricingType = UiPricingType.Agreed;
        model.PricingMethod = PricingMethod.ManualFinalPrice;
        model.FinalPriceUsdPerMt = totals.AveragePriceUsd;
        model.ManualFinalPriceUsd = totals.AveragePriceUsd;
        model.IsPricingCompleted = true;
        ModelState.Remove(nameof(model.QuantityMt));
        ModelState.Remove(nameof(model.FinalPriceUsdPerMt));
        ModelState.Remove(nameof(model.UnitPriceInCurrency));
    }

    private async Task ValidateAdditionalSuppliersAsync(ContractFormViewModel model)
    {
        if (model.ContractType != ContractType.Purchase)
        {
            model.AdditionalSupplierIds = [];
            return;
        }

        var additional = (model.AdditionalSupplierIds ?? [])
            .Where(id => id is > 0)
            .Select(id => id!.Value)
            .Distinct()
            .ToList();
        if (additional.Count == 0)
        {
            return;
        }

        var activeCount = await _db.Suppliers.AsNoTracking()
            .CountAsync(s => additional.Contains(s.Id) && s.IsActive);
        if (activeCount != additional.Count)
        {
            ModelState.AddModelError(nameof(model.AdditionalSupplierIds), "یکی از تأمین‌کننده‌های انتخاب‌شده معتبر نیست.");
        }

        if (SimplePurchaseEnabled && CollectSupplierIds(model).Count > 1)
        {
            ModelState.AddModelError(nameof(model.AdditionalSupplierIds), "در Profile خرید ساده، قرارداد چندتأمین‌کننده پشتیبانی نمی‌شود.");
            return;
        }

        ApplyMultiSupplierTerms(model);
    }

    /// <summary>
    /// فهرست تأمین‌کننده‌های قرارداد را با انتخاب فرم هماهنگ می‌کند (Entity باید ContractSuppliers
    /// را بارگذاری کرده باشد). اگر تغییر با داده‌های ثبت‌شده سازگار نباشد، پیام خطا برمی‌گرداند و
    /// چیزی را عوض نمی‌کند.
    /// </summary>
    private Task<string?> ApplySupplierSetAsync(Contract contract, int? previousSupplierId, ContractFormViewModel model)
        => ApplySupplierSetAsync(contract, previousSupplierId, CollectSupplierTerms(model));

    private async Task<string?> ApplySupplierSetAsync(
        Contract contract,
        int? previousSupplierId,
        IReadOnlyList<(int SupplierId, decimal? QuantityMt, decimal? UnitPriceUsd)> terms)
    {
        var supplierIds = terms.Select(t => t.SupplierId).ToList();
        var current = contract.ContractSuppliers.Select(s => s.SupplierId).ToList();
        var previous = current.Count > 0
            ? current
            : previousSupplierId.HasValue ? [previousSupplierId.Value] : new List<int>();
        var removed = previous.Except(supplierIds).ToList();

        if (contract.Id > 0 && removed.Count > 0)
        {
            // بارگیری بدون SupplierId در قرارداد عادی مالِ تأمین‌کنندهٔ قرارداد است.
            var hasLoadings = await _db.LoadingRegisters.AsNoTracking().AnyAsync(l =>
                l.ContractId == contract.Id
                && !l.IsCancelled
                && ((l.SupplierId != null && removed.Contains(l.SupplierId.Value))
                    || (l.SupplierId == null && current.Count == 0)));
            var hasPayments = await _db.PaymentTransactions.AsNoTracking().AnyAsync(p =>
                p.ContractId == contract.Id && p.SupplierId != null && removed.Contains(p.SupplierId.Value));
            if (hasLoadings || hasPayments)
            {
                return "تأمین‌کننده‌ای که در این قرارداد بارگیری یا پرداخت دارد از قرارداد حذف نمی‌شود.";
            }
        }

        // سهم هر تأمین‌کننده مبنای طلبِ بارگیری‌های بدون تأمین‌کننده است. وقتی چنین بارگیری‌ای
        // ثبت شده، تغییر فهرست، مقدار یا قیمت تأمین‌کننده‌ها طلب ثبت‌شده را ناهماهنگ می‌کند.
        if (contract.Id > 0
            && current.Count > 0
            && ContractSupplierScope.PurchaseShares(contract) is not null
            && !SameTerms(contract.ContractSuppliers, terms)
            && await _db.LoadingRegisters.AsNoTracking().AnyAsync(l =>
                l.ContractId == contract.Id && !l.IsCancelled && l.SupplierId == null))
        {
            return "این قرارداد بارگیری بدون تأمین‌کنندهٔ مشخص دارد و طلب آن به نسبت سهم تأمین‌کنندگان ثبت شده است؛ فهرست، مقدار یا قیمت تأمین‌کنندگان قابل تغییر نیست.";
        }

        if (supplierIds.Count <= 1)
        {
            contract.SupplierId = supplierIds.Count == 1 ? supplierIds[0] : contract.SupplierId;
            foreach (var row in contract.ContractSuppliers.ToList())
            {
                contract.ContractSuppliers.Remove(row);
            }

            return null;
        }

        // قرارداد عادی → چندتأمین‌کننده: بارگیری‌های قبلی صراحتاً به تأمین‌کنندهٔ قبلی نسبت داده
        // می‌شوند تا طلب ثبت‌شدهٔ او همان بماند.
        if (contract.Id > 0 && current.Count == 0 && previousSupplierId.HasValue)
        {
            var legacyLoadings = await _db.LoadingRegisters
                .Where(l => l.ContractId == contract.Id && l.SupplierId == null)
                .ToListAsync();
            foreach (var loading in legacyLoadings)
            {
                loading.SupplierId = previousSupplierId.Value;
            }
        }

        contract.SupplierId = null;
        foreach (var row in contract.ContractSuppliers.Where(s => !supplierIds.Contains(s.SupplierId)).ToList())
        {
            contract.ContractSuppliers.Remove(row);
        }

        foreach (var supplierId in supplierIds.Where(id => !current.Contains(id)))
        {
            contract.ContractSuppliers.Add(new ContractSupplier { SupplierId = supplierId });
        }

        foreach (var row in contract.ContractSuppliers)
        {
            var term = terms.First(t => t.SupplierId == row.SupplierId);
            row.QuantityMt = term.QuantityMt;
            row.UnitPriceUsd = term.UnitPriceUsd;
        }

        return null;
    }

    private static bool SameTerms(
        IEnumerable<ContractSupplier> rows,
        IReadOnlyList<(int SupplierId, decimal? QuantityMt, decimal? UnitPriceUsd)> terms)
    {
        var list = rows.ToList();
        return list.Count == terms.Count
            && list.All(r => terms.Any(t => t.SupplierId == r.SupplierId
                && t.QuantityMt == r.QuantityMt
                && t.UnitPriceUsd == r.UnitPriceUsd));
    }

    private static string SupplierSetSummary(IEnumerable<int> supplierIds)
        => string.Join(",", supplierIds);
}
