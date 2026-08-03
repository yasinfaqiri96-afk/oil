using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PTGOilSystem.Web.Controllers;
using PTGOilSystem.Web.Helpers;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Security;
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

    // امپورت فقط از راه LoadingController.ImportWorkbook انجام می‌شود و باید Anti-Forgery داشته باشد.
    [Fact]
    public void ImportWorkbook_RequiresAntiForgery()
    {
        var method = typeof(LoadingController).GetMethod(nameof(LoadingController.ImportWorkbook))!;

        Assert.NotNull(method.GetCustomAttributes(typeof(ValidateAntiForgeryTokenAttribute), true).SingleOrDefault());
        Assert.Equal(
            AuthPolicies.ManageData,
            method.GetCustomAttributes(typeof(AuthorizeAttribute), true).Cast<AuthorizeAttribute>().Single().Policy);
    }

    // این کنترلر دیگر هیچ اکشن ثبت‌کننده‌ای ندارد؛ ثبت فقط با دکمهٔ خود فرم انجام می‌شود.
    [Fact]
    public void Controller_HasNoRegistrationEndpoints()
    {
        var actionNames = typeof(LoadingExcelImportController)
            .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly)
            .Select(method => method.Name)
            .ToList();

        Assert.Equal(new List<string> { nameof(LoadingExcelImportController.DownloadSample) }, actionNames);
    }

    [Fact]
    public void DownloadSample_ProducesAWorkbookAcceptedByLoadingParser()
    {
        var controller = new LoadingExcelImportController();

        var file = Assert.IsType<FileContentResult>(controller.DownloadSample());
        using var stream = new MemoryStream(file.FileContents);
        var parsed = LoadingWorkbookParser.Parse(stream);

        Assert.Equal(LoadingTransportType.Truck, parsed.TransportType);
        Assert.Single(parsed.Rows);
        Assert.Equal("CMR-001", parsed.Rows[0].BillOfLadingNumber);
    }
}
