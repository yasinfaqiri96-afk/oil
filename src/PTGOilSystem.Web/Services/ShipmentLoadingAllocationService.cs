using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;

namespace PTGOilSystem.Web.Services;

/// <summary>ظرفیت تخصیص یک بارگیری: چقدر بار داشته، چقدر قبلاً به محموله‌ها رفته، چقدر مانده.</summary>
public sealed record LoadingAllocationCapacity(
    int LoadingRegisterId,
    int ContractId,
    decimal LoadedQuantityMt,
    decimal AllocatedToOtherShipmentsMt,
    decimal AllocatedToThisShipmentMt,
    decimal? LoadingPriceUsd,
    DateTime LoadingDate,
    string Label)
{
    /// <summary>مقدار قابل تخصیص برای محمولهٔ جاری (سهم خودش را دوباره آزاد می‌کند).</summary>
    public decimal RemainingForShipmentMt
        => decimal.Round(
            Math.Max(LoadedQuantityMt - AllocatedToOtherShipmentsMt, 0m),
            4,
            MidpointRounding.AwayFromZero);
}

/// <summary>
/// ظرفیت و اعتبارسنجی سهم بارگیری‌ها در محموله — تنها جایی که قانون «یک بارگیری بیشتر از
/// مقدار خودش تخصیص داده نشود» اجرا می‌شود.
///
/// محاسبهٔ ظرفیت همیشه سمت سرور از دیتابیس خوانده می‌شود؛ به مقدار ارسالی کلاینت اعتماد نمی‌شود.
/// </summary>
public sealed class ShipmentLoadingAllocationService
{
    public const decimal Epsilon = 0.0001m;

    private readonly ApplicationDbContext _db;

    public ShipmentLoadingAllocationService(ApplicationDbContext db) => _db = db;

    /// <summary>ظرفیت همهٔ بارگیری‌های یک قرارداد، نسبت به محمولهٔ جاری (در ثبت جدید: null).</summary>
    public async Task<IReadOnlyList<LoadingAllocationCapacity>> GetCapacityForContractAsync(
        int contractId,
        int? currentShipmentId,
        CancellationToken ct = default)
        => await GetCapacityAsync([contractId], currentShipmentId, ct);

    public async Task<IReadOnlyList<LoadingAllocationCapacity>> GetCapacityAsync(
        IReadOnlyCollection<int> contractIds,
        int? currentShipmentId,
        CancellationToken ct = default)
    {
        var ids = contractIds.Distinct().ToList();
        if (ids.Count == 0)
        {
            return [];
        }

        var loadings = await _db.LoadingRegisters
            .AsNoTracking()
            .Where(lr => ids.Contains(lr.ContractId))
            .OrderBy(lr => lr.LoadingDate)
            .ThenBy(lr => lr.Id)
            .ToListAsync(ct);
        if (loadings.Count == 0)
        {
            return [];
        }

        var loadingIds = loadings.Select(lr => lr.Id).ToList();
        var allocationRows = await _db.ShipmentLoadingAllocations
            .AsNoTracking()
            .Where(a => loadingIds.Contains(a.LoadingRegisterId))
            .Select(a => new { a.LoadingRegisterId, a.ShipmentId, a.QuantityMt })
            .ToListAsync(ct);

        var otherByLoading = allocationRows
            .Where(a => !currentShipmentId.HasValue || a.ShipmentId != currentShipmentId.Value)
            .GroupBy(a => a.LoadingRegisterId)
            .ToDictionary(g => g.Key, g => g.Sum(a => a.QuantityMt));
        var thisByLoading = currentShipmentId.HasValue
            ? allocationRows
                .Where(a => a.ShipmentId == currentShipmentId.Value)
                .GroupBy(a => a.LoadingRegisterId)
                .ToDictionary(g => g.Key, g => g.Sum(a => a.QuantityMt))
            : [];

        return loadings
            .Select(lr => new LoadingAllocationCapacity(
                LoadingRegisterId: lr.Id,
                ContractId: lr.ContractId,
                LoadedQuantityMt: lr.LoadedQuantityMt,
                AllocatedToOtherShipmentsMt: otherByLoading.GetValueOrDefault(lr.Id),
                AllocatedToThisShipmentMt: thisByLoading.GetValueOrDefault(lr.Id),
                LoadingPriceUsd: lr.LoadingPriceUsd,
                LoadingDate: lr.LoadingDate,
                Label: ShipmentPurchaseCostService.BuildLoadingLabel(lr, lr.Id)))
            .ToList();
    }

    /// <summary>خطای اعتبارسنجی یک سهم بارگیری (کلید فیلد + پیام).</summary>
    public sealed record AllocationError(string FieldKey, string Message);

    /// <summary>
    /// اعتبارسنجی سهم‌های بارگیریِ یک ردیف قرارداد.
    /// قوانین: بارگیری باید متعلق به همان قرارداد باشد، مقدار مثبت، بدون تکرار،
    /// در سقف باقی‌ماندهٔ همان بارگیری، و جمع سهم‌ها دقیقاً برابر مقدار تخصیص همان قرارداد.
    /// </summary>
    public static IReadOnlyList<AllocationError> Validate(
        int contractId,
        decimal contractAllocatedMt,
        IReadOnlyList<(int LoadingRegisterId, decimal QuantityMt)> picks,
        IReadOnlyDictionary<int, LoadingAllocationCapacity> capacityByLoadingId,
        string fieldPrefix)
    {
        var errors = new List<AllocationError>();
        if (picks.Count == 0)
        {
            return errors;
        }

        var seen = new HashSet<int>();
        var total = 0m;
        for (var i = 0; i < picks.Count; i++)
        {
            var (loadingRegisterId, quantityMt) = picks[i];
            var fieldKey = $"{fieldPrefix}[{i}].QuantityMt";

            if (!capacityByLoadingId.TryGetValue(loadingRegisterId, out var capacity))
            {
                errors.Add(new AllocationError(fieldKey, "بارگیری انتخاب‌شده پیدا نشد."));
                continue;
            }

            if (capacity.ContractId != contractId)
            {
                errors.Add(new AllocationError(fieldKey, "بارگیری انتخاب‌شده مربوط به قرارداد این ردیف نیست."));
                continue;
            }

            if (quantityMt <= 0m)
            {
                errors.Add(new AllocationError(fieldKey, "مقدار سهم بارگیری باید بزرگ‌تر از صفر باشد."));
                continue;
            }

            if (!seen.Add(loadingRegisterId))
            {
                errors.Add(new AllocationError(fieldKey, $"بارگیری {capacity.Label} در این محموله دوبار تخصیص داده شده است."));
                continue;
            }

            if (quantityMt - capacity.RemainingForShipmentMt > Epsilon)
            {
                errors.Add(new AllocationError(
                    fieldKey,
                    $"مقدار انتخابی از بارگیری {capacity.Label} ({quantityMt:N4} MT) از باقی‌ماندهٔ قابل تخصیص همان بارگیری ({capacity.RemainingForShipmentMt:N4} MT) بیشتر است."));
                continue;
            }

            total += quantityMt;
        }

        if (errors.Count == 0 && Math.Abs(total - contractAllocatedMt) > Epsilon)
        {
            errors.Add(new AllocationError(
                $"{fieldPrefix}",
                $"جمع سهم بارگیری‌ها ({total:N4} MT) باید دقیقاً برابر مقدار تخصیص همین قرارداد ({contractAllocatedMt:N4} MT) باشد."));
        }

        return errors;
    }
}
