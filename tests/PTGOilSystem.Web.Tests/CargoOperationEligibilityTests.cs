using PTGOilSystem.Web.Services.Operations;
using Xunit;

namespace PTGOilSystem.Web.Tests;

public sealed class CargoOperationEligibilityTests
{
    private static CargoSourceSnapshot Loading(decimal remaining = 10m) => new(
        CargoSourceKind.Loading, 1, 2, 3, 4, "C-2", "نفت", "شرکت", "W-1", "واگن", "مسیر",
        new DateTime(2026, 5, 1), 100m, 90m, 0m, 0m, remaining, false, false, true);

    [Fact]
    public void Complete_Loading_Permits_Expense_And_History_But_No_Second_Cargo_Consumption()
    {
        var source = Loading(0m);
        Assert.True(CargoOperationEligibility.Evaluate(source, CargoAction.Expense).Allowed);
        Assert.True(CargoOperationEligibility.Evaluate(source, CargoAction.History).Allowed);
        foreach (var action in new[] { CargoAction.Receive, CargoAction.DirectSale, CargoAction.StartTransport })
            Assert.False(CargoOperationEligibility.Evaluate(source, action).Allowed);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Cancelled_Or_Archived_Loading_Has_No_Expense_Or_New_Consumption(bool cancelled, bool archived)
    {
        var source = Loading() with { IsCancelled = cancelled, IsArchived = archived };
        Assert.False(CargoOperationEligibility.Evaluate(source, CargoAction.Expense).Allowed);
        Assert.False(CargoOperationEligibility.Evaluate(source, CargoAction.DirectSale).Allowed);
        Assert.True(CargoOperationEligibility.Evaluate(source, CargoAction.History).Allowed);
    }

    [Fact]
    public void Reserved_Stock_Is_Different_From_Physical_Stock_And_Transport_Remaining()
    {
        var source = Loading(100m) with { Kind = CargoSourceKind.Stock, PhysicalStockMt = 100m,
            SellableStockMt = 0m, SupportedActions = [CargoAction.DirectSale, CargoAction.StartTransport] };
        Assert.False(CargoOperationEligibility.Evaluate(source, CargoAction.DirectSale).Allowed);
        Assert.True(CargoOperationEligibility.Evaluate(source, CargoAction.StartTransport).Allowed);
        Assert.Equal(100m, source.PhysicalStockMt);
        Assert.Equal(0m, source.SellableStockMt);
    }

    [Fact]
    public void Unknown_Sellable_Stock_Is_Not_Presented_As_Definitely_Eligible()
    {
        var source = Loading(100m) with { Kind = CargoSourceKind.Stock,
            SupportedActions = [CargoAction.DirectSale], PhysicalStockMt = 100m, SellableStockMt = null };
        var result = CargoOperationEligibility.Evaluate(source, CargoAction.DirectSale);
        Assert.False(result.Allowed);
        Assert.Contains("بررسی", result.Reason);
    }
}
