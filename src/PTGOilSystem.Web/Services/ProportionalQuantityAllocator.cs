namespace PTGOilSystem.Web.Services;

/// <summary>Capacity-weighted largest-remainder allocation at the operational four-decimal precision.</summary>
public static class ProportionalQuantityAllocator
{
    private const decimal Unit = 0.0001m;

    public static IReadOnlyList<decimal> Allocate(IReadOnlyList<decimal> capacities, decimal requested)
    {
        var capacityUnits = capacities.Select(ToUnits).ToArray();
        var total = capacityUnits.Sum();
        var requestedUnits = ToUnits(requested);
        if (capacityUnits.Any(x => x < 0) || requestedUnits < 0 || requestedUnits > total)
            throw new ArgumentOutOfRangeException(nameof(requested), "Quantity must fit non-negative source capacities.");

        var allocated = new long[capacities.Count];
        if (requestedUnits == 0 || total == 0)
            return allocated.Select(x => x * Unit).ToArray();

        var ranked = capacityUnits.Select((capacity, index) =>
        {
            var numerator = (decimal)requestedUnits * capacity;
            var floor = (long)decimal.Floor(numerator / total);
            allocated[index] = floor;
            return new { Index = index, Capacity = capacity, Remainder = numerator - (decimal)floor * total };
        }).Where(x => allocated[x.Index] < x.Capacity)
          .OrderByDescending(x => x.Remainder).ThenBy(x => x.Index).ToArray();

        // The sum of fractional remainders is less than the number of sources. Each ranked
        // source receives at most one extra unit; never repeatedly favor the first source.
        var leftover = requestedUnits - allocated.Sum();
        foreach (var row in ranked)
        {
            if (leftover == 0) break;
            allocated[row.Index]++;
            leftover--;
        }
        if (leftover != 0)
            throw new InvalidOperationException("The allocation did not conserve the requested quantity.");

        return allocated.Select(x => x * Unit).ToArray();
    }

    private static long ToUnits(decimal value)
        => checked((long)decimal.Round(value / Unit, 0, MidpointRounding.AwayFromZero));
}
