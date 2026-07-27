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
        // The KPI strip, summary card and technical-details disclosure are now shared
        // partials, so the page declares data instead of repeating their markup.
        Assert.Contains("data-ak-detail-v2=\"true\"", view);
        Assert.Contains("_AkPageHeader.cshtml", view);
        Assert.Contains("_DetailKpiStrip.cshtml", view);
        Assert.Equal(4, Count(view, "Avatar = \""));
        Assert.Contains("_DetailSummaryCard.cshtml", view);
        Assert.Contains("_DetailAdvancedSection.cshtml", view);
        Assert.Contains("_DetailTimeline.cshtml", view);
        Assert.Contains("_RelatedRecords.cshtml", view);
        Assert.Contains("_DetailActionBar.cshtml", view);
        Assert.Contains("ak-list", view);

        // No page-local stat cards or hand-written key/value lists any more.
        Assert.DoesNotContain("<vc:stat-card", view);
        Assert.DoesNotContain("<dl class=\"ak-list\"", view);

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

        // Source facts are declared as AkInfoItem values; _DetailInfoGrid drops any item
        // without a value, so the page can no longer render a "-" row for a missing field.
        Assert.Contains("Value = stockSourceText", view);
        Assert.Contains("Value = Model.SourcePurchaseContractNumber", view);
        Assert.Contains("Value = Model.SourceTerminalName", view);
        Assert.Contains("Value = Model.SourceStorageTankCode", view);
        Assert.Contains("_DetailInfoGrid.cshtml", ReadSummaryCardPartial());

        Assert.Contains("if (Model.ContractId.HasValue)", view);
        Assert.Contains("if (Model.ShipmentId.HasValue", view);
        Assert.Contains("if (Model.LedgerEntryId.HasValue)", view);
        Assert.Contains("Href = Url.Action", view);
    }

    private static string ReadSummaryCardPartial()
        => ReadRepoFile("src/PTGOilSystem.Web/Views/Shared/Partials/_DetailSummaryCard.cshtml");

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
