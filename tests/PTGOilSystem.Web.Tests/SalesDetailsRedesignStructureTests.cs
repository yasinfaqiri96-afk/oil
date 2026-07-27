using System.Runtime.CompilerServices;
using Xunit;

namespace PTGOilSystem.Web.Tests;

public sealed class SalesDetailsRedesignStructureTests
{
    [Fact]
    public void Details_Uses_Shared_Ak_Detail_V2_Components_Not_Bespoke_Sales_Markup()
    {
        var view = ReadRepoFile("src/PTGOilSystem.Web/Views/Sales/Details.cshtml");

        // Shared AK Detail v2 skeleton — same components every operations detail page uses.
        Assert.Contains("data-ak-detail-v2=\"true\"", view);
        Assert.Contains("_AkPageHeader.cshtml", view);
        Assert.Contains("_AkSectionHead.cshtml", view);
        Assert.Contains("ak-stat-grid", view);
        Assert.Equal(4, Count(view, "<vc:stat-card"));
        Assert.Contains("_DetailTimeline.cshtml", view);
        Assert.Contains("_RelatedRecords.cshtml", view);
        Assert.Contains("_DetailActionBar.cshtml", view);
        Assert.Contains("ak-list", view);

        // Bespoke, page-scoped sales markup is fully retired.
        Assert.DoesNotContain("data-sales-details", view);
        Assert.DoesNotContain("sales-kpi", view);
        Assert.DoesNotContain("sales-source-section", view);
        Assert.DoesNotContain("sales-payment", view);
        Assert.DoesNotContain("<table", view);
        Assert.DoesNotContain("<h1", view);
    }

    [Fact]
    public void Details_Hides_Empty_Source_Fields_And_Only_Builds_Linked_Related_Records()
    {
        var view = ReadRepoFile("src/PTGOilSystem.Web/Views/Sales/Details.cshtml");

        Assert.Contains("if (!string.IsNullOrWhiteSpace(stockSourceText))", view);
        Assert.Contains("if (!string.IsNullOrWhiteSpace(Model.SourcePurchaseContractNumber))", view);
        Assert.Contains("if (!string.IsNullOrWhiteSpace(Model.SourceTerminalName))", view);
        Assert.Contains("if (!string.IsNullOrWhiteSpace(Model.SourceStorageTankCode))", view);

        Assert.Contains("if (Model.ContractId.HasValue)", view);
        Assert.Contains("if (Model.ShipmentId.HasValue", view);
        Assert.Contains("if (Model.LedgerEntryId.HasValue)", view);
        Assert.Contains("Href = Url.Action", view);
    }

    [Fact]
    public void Details_Relies_On_Shared_Css_Not_A_Page_Scoped_Sales_Block()
    {
        var css = ReadRepoFile("src/PTGOilSystem.Web/wwwroot/css/ptg/11-details.css");

        // The old bespoke, page-scoped sales block was deleted during harmonization.
        Assert.DoesNotContain("[data-sales-details]", css);
        Assert.DoesNotContain("/* Sales details:", css);
        Assert.DoesNotContain(".sales-kpi", css);
    }

    private static int Count(string value, string token)
        => (value.Length - value.Replace(token, string.Empty, StringComparison.Ordinal).Length) / token.Length;

    private static string ReadRepoFile(string relativePath, [CallerFilePath] string sourceFile = "")
    {
        var repoRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", ".."));
        return File.ReadAllText(Path.Combine(repoRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
    }
}
