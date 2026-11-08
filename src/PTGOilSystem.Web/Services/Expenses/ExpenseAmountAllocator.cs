namespace PTGOilSystem.Web.Services.Expenses;

/// <summary>
/// تقسیم یک مبلغ کل به نسبت وزن (مثلاً مقدار تن هر عملیات) با دقت سنت. روش بزرگ‌ترین باقیمانده:
/// هر سهم کفِ سهم دقیق را می‌گیرد و سنت‌های باقی‌مانده به بزرگ‌ترین کسرها داده می‌شود، پس
/// جمع سهم‌ها دقیقاً برابر مبلغ کل است و اختلاف گردکردن روی یک ردیف انباشته نمی‌شود.
/// </summary>
public static class ExpenseAmountAllocator
{
    private const decimal Cent = 0.01m;

    public static IReadOnlyList<decimal> SplitByWeight(decimal total, IReadOnlyList<decimal> weights)
    {
        if (weights.Count == 0)
        {
            return [];
        }

        if (total < 0m || weights.Any(weight => weight <= 0m))
        {
            throw new ArgumentOutOfRangeException(nameof(weights), "Total must be non-negative and every weight positive.");
        }

        var totalCents = checked((long)decimal.Round(total / Cent, 0, MidpointRounding.AwayFromZero));
        var weightSum = weights.Sum();
        var cents = new long[weights.Count];
        var ranked = weights
            .Select((weight, index) =>
            {
                var exact = totalCents * weight / weightSum;
                var floor = (long)decimal.Floor(exact);
                cents[index] = floor;
                return (Index: index, Remainder: exact - floor);
            })
            .OrderByDescending(row => row.Remainder)
            .ThenBy(row => row.Index)
            .ToList();

        var leftover = totalCents - cents.Sum();
        foreach (var row in ranked)
        {
            if (leftover <= 0)
            {
                break;
            }

            cents[row.Index]++;
            leftover--;
        }

        if (leftover != 0)
        {
            throw new InvalidOperationException("The allocation did not conserve the total amount.");
        }

        return cents.Select(value => value * Cent).ToArray();
    }
}
