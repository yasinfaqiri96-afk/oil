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
