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
        Assert.Contains("AkHeaderIdentity", view);
        Assert.Contains("_DetailOverview.cshtml", view);
        Assert.Contains("_DetailActivityList.cshtml", view);
        Assert.Contains("_DetailSecondary.cshtml", view);
        Assert.DoesNotContain("_DetailKpiStrip.cshtml", view);
        Assert.DoesNotContain("_OperationsDetailMore.cshtml", view);
        // Next operations live in the action bar only. Feeding the same list to the header
        // kebab as well is what printed every action twice on the page.
        Assert.DoesNotContain("AkHeaderOverflowActions", view);
        Assert.Contains("_DetailActionBar.cshtml", view);
        Assert.Contains("ak-list", view);
        Assert.Contains("ak-linear-detail", view);
        Assert.Contains("data-ak-operations-detail=\"true\"", view);

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

        var detailCss = ReadRepoFile("src/PTGOilSystem.Web/wwwroot/css/ptg/73-detail-system.css");
        Assert.Contains(".ak-linear-detail .ak-detail-overview", detailCss);
        Assert.Contains(".ak-linear-detail .ak-detail-activity-row", detailCss);
        Assert.Contains("grid-template-columns: repeat(2, minmax(0, 1fr))", detailCss);
        Assert.DoesNotContain(".ak-operations-clean-page .ak-operations-clean-overview", detailCss);
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

    [Fact]
    public void Details_Shows_Both_Invoices_And_Uses_Requested_Summary_Fields()
    {
        var view = ReadRepoFile("src/PTGOilSystem.Web/Views/Sales/Details.cshtml");
        var header = ReadRepoFile("src/PTGOilSystem.Web/Views/Shared/Components/Ak/_AkPageHeader.cshtml");

        Assert.DoesNotContain("Sale contract\"), Value = contractText", view);
        Assert.DoesNotContain("Ledger amount", view);
        Assert.DoesNotContain("Booked value", view);
        Assert.Contains("T(\"مقدار فروش\", \"Sale quantity\")", view);
        Assert.Contains("T(\"فاکتور ۱\", \"Invoice 1\")", view);
        Assert.Contains("T(\"فاکتور ۲\", \"Invoice 2\")", view);
        Assert.Contains("template = \"faisal\"", view);
        Assert.Contains("template = \"fawad\"", view);
        Assert.Contains("AkHeaderCompanionAction", header);
    }

    private static int Count(string value, string token)
        => (value.Length - value.Replace(token, string.Empty, StringComparison.Ordinal).Length) / token.Length;

    private static string ReadRepoFile(string relativePath, [CallerFilePath] string sourceFile = "")
    {
        var repoRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", ".."));
        return File.ReadAllText(Path.Combine(repoRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
    }
}
