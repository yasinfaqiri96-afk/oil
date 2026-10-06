using System.Runtime.CompilerServices;
using Xunit;

namespace PTGOilSystem.Web.Tests;

public sealed class OperationsLinearDetailStructureTests
{
    private static readonly string[] Views =
    [
        "CustomsDeclarations", "Dispatch", "Expenses", "InventoryTransportLegs",
        "Loading", "LoadingReceipts", "LossEvents", "Sales", "ShipmentPnl"
    ];

    [Fact]
    public void All_Operations_Details_Use_The_Linear_Three_Zone_Composition()
    {
        Assert.Equal(9, Views.Length);

        foreach (var controller in Views)
        {
            var view = ReadRepoFile($"src/PTGOilSystem.Web/Views/{controller}/Details.cshtml");

            // Two shapes of the same linear shell: the card composition
            // (_DetailCards + _DetailMore) and the classic overview + activity list.
            // Either way the page header comes first and the secondary material last.
            var body = view.Contains("_DetailCards.cshtml", StringComparison.Ordinal)
                ? "_DetailCards.cshtml"
                : "_DetailOverview.cshtml";
            var closing = body == "_DetailCards.cshtml"
                ? "_DetailMore.cshtml"
                : "_DetailActivityList.cshtml";

            Assert.Contains("ak-linear-detail", view);
            Assert.Contains("AkHeaderIdentity", view);
            Assert.Contains("_AkPageHeader.cshtml", view);
            Assert.Contains(body, view);
            Assert.Contains(closing, view);
            Assert.Contains("data-ak-operations-detail=\"true\"", view);
            Assert.DoesNotContain("_DetailKpiStrip.cshtml", view);
            Assert.DoesNotContain("<vc:stat-card", view);
            Assert.DoesNotContain("_OperationsDetailMore.cshtml", view);
            Assert.DoesNotContain("<details", view);

            Assert.True(
                view.IndexOf("_AkPageHeader.cshtml", StringComparison.Ordinal)
                    < view.IndexOf(body, StringComparison.Ordinal));
            Assert.True(
                view.IndexOf(body, StringComparison.Ordinal)
                    < view.LastIndexOf(closing, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Shared_Linear_Shell_Owns_Width_Rhythm_Metrics_Rows_And_Responsive_Behavior()
    {
        var css = ReadRepoFile("src/PTGOilSystem.Web/wwwroot/css/ptg/73-detail-system.css");
        var overview = ReadRepoFile("src/PTGOilSystem.Web/Views/Shared/Partials/_DetailOverview.cshtml");
        var activity = ReadRepoFile("src/PTGOilSystem.Web/Views/Shared/Partials/_DetailActivityList.cshtml");
        var header = ReadRepoFile("src/PTGOilSystem.Web/Views/Shared/Components/Ak/_AkPageHeader.cshtml");

        Assert.Contains("inline-size: min(100%, 1120px)", css);
        Assert.Contains(".ak-linear-detail .ak-detail-overview", css);
        Assert.Contains(".ak-linear-detail .ak-detail-metrics", css);
        Assert.Contains(".ak-linear-detail .ak-detail-activity-row", css);
        Assert.Contains("@media (max-width: 991.98px)", css);
        Assert.Contains("@media (max-width: 575.98px)", css);
        Assert.Contains("box-shadow: none", css);

        Assert.Contains("Take(4)", overview);
        Assert.Contains("ak-detail-metric", overview);
        Assert.DoesNotContain("vc:stat-card", overview);
        Assert.Contains("Where(row => row.HasValue)", activity);
        Assert.Contains("ak-detail-activity-link", activity);
        Assert.Contains("AkHeaderIdentity", header);
        Assert.Contains("Take(5)", header);
        Assert.Contains("ak-detail-identity-main", header);
    }

    [Fact]
    public void Shipment_Removes_The_Per_Tab_Kpi_Card_Dashboard()
    {
        var view = ReadRepoFile("src/PTGOilSystem.Web/Views/ShipmentPnl/Details.cshtml");

        Assert.DoesNotContain("ak-stat-grid mb-3", view);
        Assert.DoesNotContain("<vc:stat-card", view);
        Assert.Contains("shipmentOverviewMetrics", view);
        Assert.Contains("shipmentActivityRows", view);
    }

    [Fact]
    public void Loading_And_Transport_Details_Show_The_Vehicle_Type_Visual()
    {
        var loading = ReadRepoFile("src/PTGOilSystem.Web/Views/Loading/Details.cshtml");
        var transport = ReadRepoFile("src/PTGOilSystem.Web/Views/InventoryTransportLegs/Details.cshtml");
        var cards = ReadRepoFile("src/PTGOilSystem.Web/Views/Shared/Partials/_DetailCards.cshtml");
        var css = ReadRepoFile("src/PTGOilSystem.Web/wwwroot/css/ptg/73-detail-system.css");

        foreach (var view in new[] { loading, transport })
        {
            Assert.Contains("LoadingTransportType.Truck => \"transported\"", view);
            Assert.Contains("LoadingTransportType.Wagon => \"wagon\"", view);
            Assert.Contains("T(\"تانکر مواد نفتی\", \"Petroleum tanker truck\")", view);
            Assert.Contains("T(\"واگن نفتی\", \"Rail tank wagon\")", view);
            Assert.Contains("VisualAvatar = transportVisualAvatar", view);
            Assert.Contains("VisualTitle = transportVisualTitle", view);
        }

        Assert.Contains("Metrics = quantityMetrics", loading);
        Assert.Contains("VisualMeta = Model.VehicleSummary", loading);
        Assert.Contains("Title = T(\"مسیر و وسیله\", \"Route & vehicle\")", transport);
        Assert.Contains("Steps = transportVisualAvatar is null ? routeSteps : []", transport);
        Assert.Contains("StatCardAvatarRegistry.ResolvePath(card.VisualAvatar)", cards);
        Assert.Contains("class=\"ptg-td-card-visual\"", cards);
        Assert.Contains("alt=\"@card.VisualTitle\"", cards);
        Assert.Contains(".ptg-record-detail .ptg-td-card-visual", css);
        Assert.Contains("inline-size: min(100%, 360px)", css);
    }

    [Fact]
    public void Loading_Main_Information_Fits_Content_And_Transport_Expense_Rows_Stay_Aligned()
    {
        var loading = ReadRepoFile("src/PTGOilSystem.Web/Views/Loading/Details.cshtml");
        var transport = ReadRepoFile("src/PTGOilSystem.Web/Views/InventoryTransportLegs/Details.cshtml");
        var editor = ReadRepoFile("src/PTGOilSystem.Web/Views/InventoryTransportLegs/_TransportExpenseEditor.cshtml");
        var row = ReadRepoFile("src/PTGOilSystem.Web/Views/InventoryTransportLegs/_TransportExpenseLineRow.cshtml");
        var css = ReadRepoFile("src/PTGOilSystem.Web/wwwroot/css/ptg/76-form-system.css");

        Assert.Contains("data-loading-details", loading);
        Assert.Contains("transport-expense-dialog", transport);
        Assert.Contains("transport-expense-lines", editor);
        Assert.Contains("transport-expense-grid-head", editor);
        Assert.Contains("ak-form-grid transport-expense-grid", row);
        Assert.Contains("form.ak-form .transport-expense-grid > .ak-field", css);
        Assert.Contains("grid-column: span 1", css);
        Assert.Contains("min-inline-size: 1080px", css);
        Assert.Contains("max-inline-size: min(1320px, calc(100dvi - 32px))", css);
        Assert.Contains("[data-loading-details] .ptg-td-grid", css);
        Assert.Contains("align-items: start", css);
    }

    private static string ReadRepoFile(string relativePath, [CallerFilePath] string sourceFilePath = "")
    {
        var normalizedPath = relativePath.Replace('/', Path.DirectorySeparatorChar);
        var directory = new DirectoryInfo(Path.GetDirectoryName(sourceFilePath)!);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, normalizedPath);
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            directory = directory.Parent;
        }

        throw new FileNotFoundException(relativePath);
    }
}
