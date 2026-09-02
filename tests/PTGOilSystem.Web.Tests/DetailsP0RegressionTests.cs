using System.IO;
using System.Runtime.CompilerServices;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// View-structure regression guards for the four P0 fixes on Details pages:
/// 1) Loading must not hide receipts, customs or shortage records,
/// 2) ShipmentPnl expense categorisation must not live in Razor,
/// 3) profile pages must link contracts to ContractJourney (not the legacy
///    Contracts/Details redirect shim),
/// 4) StorageTanks must render the fill-percent KPI with the shared stat card.
/// </summary>
public class DetailsP0RegressionTests
{
    /// <summary>
    /// The original guard pinned the server-side pagers (receiptsPage /
    /// pagedReceiptItems) of the old three-table Loading detail page. Commit
    /// 077bf66 replaced that page with the shared linear detail shell, so those
    /// names are gone; the P0 behaviour they protected is not. This test now
    /// pins the same invariant on the current contract: every receipt, customs
    /// declaration and shortage event of a loading stays reachable on its
    /// detail page, and the shared secondary block never discards the events
    /// that fall outside its compact window.
    /// </summary>
    [Fact]
    public void Loading_Details_Keeps_Every_Receipt_Customs_And_Shortage_Record()
    {
        var view = ReadRepoFile("src/PTGOilSystem.Web/Views/Loading/Details.cshtml");
        var secondary = ReadRepoFile("src/PTGOilSystem.Web/Views/Shared/Partials/_DetailSecondary.cshtml");

        // No caller-side truncation: the whole collection feeds the timeline.
        Assert.DoesNotContain(".Take(5)", view);
        Assert.DoesNotContain(".Take(4)", view);
        Assert.Contains("timelineItems.AddRange(Model.ReceiptItems", view);
        Assert.Contains("timelineItems.AddRange(Model.CustomsItems", view);
        Assert.Contains("timelineItems.AddRange(Model.LossItems", view);

        // And the shared block moves the overflow into a disclosure instead of
        // dropping it, so TimelineLimit stays a layout choice, not data loss.
        Assert.Contains("var olderEvents", secondary);
        Assert.Contains("olderEvents = events.Take(events.Count - visibleCount).ToList();", secondary);
        Assert.Contains("@foreach (var item in olderEvents)", secondary);
        Assert.DoesNotContain("events = events.TakeLast(Model.TimelineLimit.Value).ToList();", secondary);
    }

    [Fact]
    public void ShipmentPnl_Details_Uses_Single_Categorisation_Source()
    {
        var view = ReadRepoFile("src/PTGOilSystem.Web/Views/ShipmentPnl/Details.cshtml");

        // The old in-view keyword classifier (duplicated twice with divergent
        // term lists) must stay out of Razor.
        Assert.DoesNotContain("ExpenseMatches(", view);
        Assert.DoesNotContain("catFreight", view);
        Assert.Contains("Model.ExpenseCategoryGroups", view);
        Assert.Contains("ShipmentExpenseCategorizer.TotalFor", view);
    }

    [Theory]
    [InlineData("src/PTGOilSystem.Web/Views/Suppliers/Details.cshtml")]
    [InlineData("src/PTGOilSystem.Web/Views/Customers/Details.cshtml")]
    [InlineData("src/PTGOilSystem.Web/Views/Payments/Details.cshtml")]
    public void Profile_Pages_Link_Contracts_Directly_To_ContractJourney(string relativePath)
    {
        var view = ReadRepoFile(relativePath);

        Assert.DoesNotContain("asp-controller=\"Contracts\" asp-action=\"Details\"", view);
    }

    [Fact]
    public void Supplier_Details_Combines_Summary_And_Contracts_Into_One_Tab()
    {
        var view = ReadRepoFile("src/PTGOilSystem.Web/Views/Suppliers/Details.cshtml");

        Assert.Contains("overview|خلاصه و قراردادها|", view);
        Assert.DoesNotContain("contracts|قراردادها|", view);
        Assert.Equal(4, Count(view, "showSummaryAndContracts"));
        Assert.Contains("var showSummaryAndContracts = !showStatement;", view);
    }

    [Fact]
    public void StorageTanks_Details_Renders_Fill_Percent_Kpi_With_Shared_StatCard()
    {
        var view = ReadRepoFile("src/PTGOilSystem.Web/Views/StorageTanks/Details.cshtml");

        Assert.Contains("<vc:stat-card", view);
        Assert.Contains("fillPercentValue", view);
        // The computed value must actually be rendered, not just assigned.
        Assert.Contains("value=\"@FormatQuantity(fillPercentValue)\"", view);
    }

    private static string ReadRepoFile(string relativePath)
        => File.ReadAllText(GetRepoPath(relativePath));

    private static int Count(string source, string value)
        => source.Split(value, StringSplitOptions.None).Length - 1;

    private static string GetRepoPath(string relativePath, [CallerFilePath] string sourceFilePath = "")
    {
        var normalizedPath = relativePath.Replace('/', Path.DirectorySeparatorChar);
        foreach (var start in new[]
                 {
                     Environment.CurrentDirectory,
                     AppContext.BaseDirectory,
                     Path.GetDirectoryName(sourceFilePath) ?? string.Empty
                 })
        {
            var directory = new DirectoryInfo(start);
            while (directory is not null)
            {
                var candidate = Path.Combine(directory.FullName, normalizedPath);
                if (File.Exists(candidate)) return candidate;
                directory = directory.Parent;
            }
        }

        throw new FileNotFoundException($"Repo file not found: {relativePath}");
    }
}
