using System.Runtime.CompilerServices;
using Xunit;

namespace PTGOilSystem.Web.Tests;

public sealed class GroupUnloadViewStructureTests
{
    [Fact]
    public void Group_Unload_Reuses_The_Group_Sales_Wizard_Contract()
    {
        var groupSale = ReadRepoFile("src/PTGOilSystem.Web/Views/Sales/CreateGroup.cshtml");
        var groupUnload = ReadRepoFile("src/PTGOilSystem.Web/Views/TruckSettlements/GroupUnload.cshtml");
        var sharedContract = new[]
        {
            "ak-group-wizard-page",
            "_WizardSteps.cshtml",
            "_AkClientSearchFilter",
            "ak-group-wizard-workspace",
            "ak-group-wizard-main",
            "ak-group-wizard-summary",
            "<footer class=\"ak-form-section\">"
        };

        Assert.All(sharedContract, marker => Assert.Contains(marker, groupSale));
        Assert.All(sharedContract, marker => Assert.Contains(marker, groupUnload));
        Assert.DoesNotContain("<style", groupUnload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<link", groupUnload, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Group_Unload_Preserves_The_Server_Validated_Post_Contract()
    {
        var view = ReadRepoFile("src/PTGOilSystem.Web/Views/TruckSettlements/GroupUnload.cshtml");
        var controller = ReadRepoFile(
            "src/PTGOilSystem.Web/Controllers/TruckSettlementsController.GroupUnload.cs");

        Assert.Contains("@Html.AntiForgeryToken()", view);
        Assert.Contains("asp-for=\"SourceKind\"", view);
        Assert.Contains("asp-for=\"Items[index].Selected\"", view);
        Assert.Contains("asp-for=\"Items[index].Kind\"", view);
        Assert.Contains("asp-for=\"Items[index].SourceId\"", view);
        Assert.Contains("asp-for=\"DestinationStorageTankId\"", view);
        Assert.Contains("var isSubmitting = false", view);
        Assert.Contains("if (isSubmitting)", view);
        Assert.Contains("IsolationLevel.Serializable", controller);
        // تخلیهٔ گروهی باید از همان سرویس رسیدِ مشترک عبور کند و آن سرویس از DI بیاید، وگرنه
        // آداپترهای حسابداری/نسب‌نامه در این مسیر null می‌مانند و رفتار با بقیهٔ مسیرها فرق می‌کند.
        Assert.Contains("var receiptService = _receiptService;", controller);
        Assert.DoesNotContain("new InventoryTransportReceiptService(", controller);
        Assert.Contains("ReferenceDocument = $\"TRUCK-UNLOAD:{dispatch.Id}\"", controller);
    }

    [Fact]
    public void Group_Wizard_Summary_Survives_The_Final_Form_System_Reset()
    {
        var formSystem = ReadRepoFile(
            "src/PTGOilSystem.Web/wwwroot/css/ptg/76-form-system.css");

        Assert.Contains("form.ak-form .ak-group-wizard-summary {", formSystem);
        Assert.Contains("padding: 16px;", formSystem);
        Assert.Contains(
            "form.ak-form .ak-group-wizard-summary .ak-list-row {",
            formSystem);
        Assert.Contains("grid-template-columns: minmax(0, 1fr) auto;", formSystem);
        Assert.Contains(
            "form.ak-form .ak-group-wizard-summary .ak-list-row > .ak-list {",
            formSystem);
        Assert.Contains("grid-column: 1 / -1;", formSystem);
    }

    private static string ReadRepoFile(
        string relativePath,
        [CallerFilePath] string callerFilePath = "")
    {
        var testsDirectory = Path.GetDirectoryName(callerFilePath)!;
        var repositoryRoot = Path.GetFullPath(Path.Combine(testsDirectory, "..", ".."));
        return File.ReadAllText(Path.Combine(repositoryRoot, relativePath));
    }
}
