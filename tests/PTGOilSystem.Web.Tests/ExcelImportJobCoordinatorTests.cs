using Microsoft.Extensions.Logging.Abstractions;
using PTGOilSystem.Web.Models.ExcelImport;
using PTGOilSystem.Web.Services.Imports;
using Xunit;

namespace PTGOilSystem.Web.Tests;

public sealed class ExcelImportJobCoordinatorTests
{
    [Fact]
    public async Task Validation_ExposesOnlyRealRowProgress_AndRequiresSameOwner()
    {
        var coordinator = new ExcelImportJobCoordinator(NullLogger<ExcelImportJobCoordinator>.Instance);
        var id = coordinator.Create("user-1", "loadings.xlsx", 1024);

        coordinator.StartValidation(id, (context, _) =>
        {
            context.Update(ExcelImportJobStage.Validation, "validating", 4, 10, 2, "Loading");
            context.Ready(new object(), 10, 9, 1, 0, 1, [], [], 2, "Loading");
            return Task.CompletedTask;
        });

        var snapshot = await WaitForStateAsync(coordinator, id, "user-1", ExcelImportJobState.ReadyForConfirmation);

        Assert.Equal(100d, snapshot.Percent);
        Assert.Equal(10, snapshot.ProcessedRows);
        Assert.Equal(10, snapshot.TotalRows);
        Assert.Equal("Loading", snapshot.SelectedSheet);
        Assert.False(coordinator.TryGet(id, "user-2", out _));
    }

    [Fact]
    public async Task Registration_CannotBeCancelled_AndCompletesWithCounts()
    {
        var coordinator = new ExcelImportJobCoordinator(NullLogger<ExcelImportJobCoordinator>.Instance);
        var id = coordinator.Create("user-1", "loadings.xlsx", 2048);
        coordinator.StartValidation(id, (context, _) =>
        {
            context.Ready(new object(), 12, 12, 0, 0, 0, [], [], 1, "Loading");
            return Task.CompletedTask;
        });
        await WaitForStateAsync(coordinator, id, "user-1", ExcelImportJobState.ReadyForConfirmation);

        Assert.True(coordinator.TryStartRegistration(id, "user-1", (context, _) =>
        {
            context.Complete(12, 0, 0, "/Loading");
            return Task.CompletedTask;
        }));
        Assert.False(coordinator.TryCancel(id, "user-1"));

        var snapshot = await WaitForStateAsync(coordinator, id, "user-1", ExcelImportJobState.Completed);
        Assert.Equal(12, snapshot.CreatedRows);
        Assert.Equal("/Loading", snapshot.RedirectUrl);
        Assert.Equal(100d, snapshot.Percent);
    }

    private static async Task<ExcelImportJobSnapshot> WaitForStateAsync(
        ExcelImportJobCoordinator coordinator,
        Guid id,
        string owner,
        ExcelImportJobState expected)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (coordinator.TryGet(id, owner, out var snapshot) && snapshot?.State == expected)
            {
                return snapshot;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException($"Job did not reach {expected}.");
    }
}
