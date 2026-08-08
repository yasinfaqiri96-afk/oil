using Xunit;

namespace PTGOilSystem.Web.Tests;

public sealed class PartyStatementFilterViewStructureTests
{
    [Fact]
    public void Official_Statements_Use_The_Shared_Search_Filter_Without_Legacy_Filter_Chrome()
    {
        var view = ReadRepoFile("src", "PTGOilSystem.Web", "Views", "PartyStatements", "Document.cshtml");
        var statementCss = ReadRepoFile("src", "PTGOilSystem.Web", "wwwroot", "css", "ptg", "62-party-statement.css");
        var surfaceCss = ReadRepoFile("src", "PTGOilSystem.Web", "wwwroot", "css", "ptg", "72-surfaces.css");
        var sharedFilterCss = ReadRepoFile("src", "PTGOilSystem.Web", "wwwroot", "css", "ptg", "45-akaunting.css");
        var embedScript = ReadRepoFile("src", "PTGOilSystem.Web", "wwwroot", "js", "party-statement-embed.js");

        Assert.Contains("Views/Shared/_AkSearchFilter.cshtml", view);
        Assert.Contains("new AkSearchFilterModel(", view);
        Assert.Contains("new(\"FromDate\"", view);
        Assert.Contains("new(\"ContractId\"", view);
        Assert.Contains("new(\"CompanyId\"", view);
        Assert.Contains("new(\"CurrencyCode\"", view);
        Assert.Contains("new(\"IncludeOperationalColumns\"", view);
        Assert.Contains("new(\"SourceType\"", view);
        Assert.Contains("\"Search\"", view);
        Assert.DoesNotContain("statement-filter-bar", view);
        Assert.DoesNotContain("statement-filter-bar", statementCss);
        Assert.DoesNotContain("statement-filter-bar", surfaceCss);
        Assert.Contains("flex: 0 0 22px", sharedFilterCss);
        Assert.Contains("line-height: 1", sharedFilterCss);
        Assert.Contains("host.querySelector(\"[data-ak-filter]\")", embedScript);
        Assert.Contains("window.dispatchEvent(new CustomEvent(\"ptg:page-ready\"))", embedScript);
        Assert.Contains("رسیدگی", view);
        Assert.Contains("بردگی", view);
        Assert.Contains("بیلانس فعلی", view);
        Assert.Contains("statement-money statement-receipt", view);
        Assert.Contains("statement-money statement-outflow", view);
        Assert.Contains(".statement-receipt { color: var(--ptg-success-text", statementCss);
        Assert.Contains(".statement-outflow { color: var(--ptg-danger-text", statementCss);
        Assert.Contains("data-statement-auto-print", view);
        Assert.DoesNotContain("ClosingBalanceMeaningFor", view);
        Assert.DoesNotContain("statement-summary-icon", view);
        Assert.DoesNotContain(">بارگیری‌ها</a>", view);
    }

    private static string ReadRepoFile(params string[] segments)
    {
        var relativePath = Path.Combine(segments);
        var path = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            relativePath));
        return File.ReadAllText(path);
    }
}
