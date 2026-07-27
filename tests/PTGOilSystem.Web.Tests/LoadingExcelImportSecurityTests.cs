using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PTGOilSystem.Web.Controllers;
using PTGOilSystem.Web.Helpers;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Security;
using PTGOilSystem.Web.Services.Imports;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace PTGOilSystem.Web.Tests;

public sealed class LoadingExcelImportSecurityTests
{
    [Fact]
    public void Controller_RequiresManageDataPermission()
    {
        var authorize = typeof(LoadingExcelImportController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), true)
            .Cast<AuthorizeAttribute>()
            .Single();

        Assert.Equal(AuthPolicies.ManageData, authorize.Policy);
    }

    [Theory]
    [InlineData(nameof(LoadingExcelImportController.Start))]
    [InlineData(nameof(LoadingExcelImportController.Confirm))]
    [InlineData(nameof(LoadingExcelImportController.Cancel))]
    public void MutationEndpoints_RequireAntiForgery(string actionName)
    {
        var method = typeof(LoadingExcelImportController).GetMethod(actionName)!;

        Assert.NotNull(method.GetCustomAttributes(typeof(ValidateAntiForgeryTokenAttribute), true).SingleOrDefault());
    }

    [Fact]
    public void DownloadSample_ProducesAWorkbookAcceptedByLoadingParser()
    {
        var jobs = new ExcelImportJobCoordinator(NullLogger<ExcelImportJobCoordinator>.Instance);
        var controller = new LoadingExcelImportController(
            jobs,
            null!,
            NullLogger<LoadingExcelImportController>.Instance);

        var file = Assert.IsType<FileContentResult>(controller.DownloadSample());
        using var stream = new MemoryStream(file.FileContents);
        var parsed = LoadingWorkbookParser.Parse(stream);

        Assert.Equal(LoadingTransportType.Truck, parsed.TransportType);
        Assert.Single(parsed.Rows);
        Assert.Equal("CMR-001", parsed.Rows[0].BillOfLadingNumber);
    }
}
