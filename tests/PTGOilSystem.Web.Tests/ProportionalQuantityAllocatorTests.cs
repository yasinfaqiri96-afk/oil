using PTGOilSystem.Web.Services;
using Xunit;

namespace PTGOilSystem.Web.Tests;

public sealed class ProportionalQuantityAllocatorTests
{
    [Fact]
    public void ThousandEqualSources_Distribute999RemaindersToDifferentRows()
    {
        var result = ProportionalQuantityAllocator.Allocate(Enumerable.Repeat(1m, 1000).ToArray(), 100.0999m);
        Assert.Equal(100.0999m, result.Sum());
        Assert.Equal(999, result.Count(x => x == .1001m));
        Assert.Equal(.1000m, result[999]);
        Assert.All(result, x => Assert.InRange(x, .1000m, .1001m));
    }

    [Theory]
    [InlineData(.0001)]
    [InlineData(.1234)]
    [InlineData(10.0000)]
    [InlineData(101.0001)]
    public void UnequalCapacities_ConserveQuantityAndStayWithinOneUnitOfIdeal(double quantity)
    {
        decimal[] capacities = [.0001m, 1m, 100m];
        var requested = (decimal)quantity;
        var result = ProportionalQuantityAllocator.Allocate(capacities, requested);
        Assert.Equal(requested, result.Sum());
        for (var i = 0; i < capacities.Length; i++)
        {
            Assert.InRange(result[i], 0m, capacities[i]);
            Assert.True(Math.Abs(result[i] - requested * capacities[i] / capacities.Sum()) <= .0001m);
        }
        Assert.Equal(result, ProportionalQuantityAllocator.Allocate(capacities, requested));
    }

    [Fact]
    public void ZeroCapacityAndZeroRequest_AreHandledWithoutInventingShares()
    {
        Assert.Equal(new[] { 0m, .0001m, 0m }, ProportionalQuantityAllocator.Allocate([0m, 1m, 0m], .0001m));
        Assert.All(ProportionalQuantityAllocator.Allocate([1m, 2m], 0m), x => Assert.Equal(0m, x));
        Assert.Throws<ArgumentOutOfRangeException>(() => ProportionalQuantityAllocator.Allocate([1m], 1.0001m));
    }
}
