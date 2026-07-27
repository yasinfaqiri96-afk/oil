using System.Runtime.CompilerServices;
using Xunit;

namespace PTGOilSystem.Web.Tests;

public sealed class AkDetailV2StructureTests
{
    private static readonly string[] DetailViews =
    [
        "AccountStatements", "AuditLogs", "CashAccounts", "Companies",
        "ContractBalanceTransfers", "ContractJourney", "Currencies", "Customers",
        "CustomsDeclarations", "DailyFxRates", "Dispatch", "Drivers", "Employees",
        "ExpenseRules", "Expenses", "ExpenseTypes", "FiscalYears",
        "InventoryTransportLegs", "Ledger", "Loading", "LoadingReceipts",
        "Locations", "LossEvents", "OperationalAssets", "Partners", "Payments",
        "Products", "Roles", "Sales", "Sarrafs", "SarrafSettlements",
        "ServiceProviders", "ShipmentPnl", "StorageTanks", "Suppliers", "Terminals",
        "ThreeWaySettlement", "Trucks", "Units", "Users", "Vessels", "Wagons"
    ];

    private static readonly string[] ServerTabbedViews =
    [
        "CashAccounts", "Customers", "Drivers", "Employees", "OperationalAssets",
        "Partners", "Payments", "Sarrafs", "ServiceProviders", "ShipmentPnl",
        "StorageTanks", "Suppliers"
    ];

    [Fact]
    public void All_42_Detail_Views_Use_V2_Boundary_And_Shared_Header()
    {
        Assert.Equal(42, DetailViews.Length);

        foreach (var controller in DetailViews)
        {
            var view = ReadView(controller);
            Assert.Contains("data-ak-detail-v2=\"true\"", view);
            Assert.Contains("_AkPageHeader.cshtml", view);
            Assert.DoesNotContain("<h1", view);
        }
    }

    [Fact]
    public void Pilot_Views_Use_All_Shared_V2_Zones_Without_Legacy_Tab_Pagers()
    {
        foreach (var controller in new[] { "InventoryTransportLegs", "Loading" })
        {
            var view = ReadView(controller);
            Assert.Contains("_DetailPager.cshtml", view);
            Assert.Contains("_DetailTimeline.cshtml", view);
            Assert.Contains("_RelatedRecords.cshtml", view);
            Assert.Contains("_DetailActionBar.cshtml", view);
            Assert.DoesNotContain("data-ptcd-tab", view);
            Assert.DoesNotContain("data-ptcd-pager", view);
        }
    }

    [Fact]
    public void Multi_Section_Detail_Tabs_Are_Server_Driven_Through_Tab_Query()
    {
        foreach (var controller in ServerTabbedViews)
        {
            var view = ReadView(controller);
            Assert.Contains("_DetailsTabs.cshtml", view);
            Assert.Contains("tab", view, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("data-ptg-tab-target", view);
            Assert.DoesNotContain("data-shipment-file-tabs", view);
        }

        var tabs = ReadRepoFile("src/PTGOilSystem.Web/Views/Shared/Partials/_DetailsTabs.cshtml");
        Assert.Contains("BuildServerTabHref", tabs);
        Assert.Contains("aria-current", tabs);
    }

    [Fact]
    public void Every_Take_In_A_Detail_View_Is_Paired_With_Shared_Server_Pager()
    {
        foreach (var controller in DetailViews)
        {
            var view = ReadView(controller);
            if (view.Contains(".Take(", StringComparison.Ordinal))
            {
                Assert.Contains("_DetailPager.cshtml", view);
                Assert.Contains(".Skip(", view);
            }
        }
    }

    [Fact]
    public void Shared_Detail_Infrastructure_Covers_Accessibility_Rtl_Dark_Print_And_Mobile()
    {
        var css = ReadRepoFile("src/PTGOilSystem.Web/wwwroot/css/ptg/11-details.css");
        var statCss = ReadRepoFile("src/PTGOilSystem.Web/wwwroot/css/ptg/52-stat-card.css");
        var header = ReadRepoFile("src/PTGOilSystem.Web/Views/Shared/Components/Ak/_AkPageHeader.cshtml");
        var pager = ReadRepoFile("src/PTGOilSystem.Web/Views/Shared/Partials/_DetailPager.cshtml");
        var timeline = ReadRepoFile("src/PTGOilSystem.Web/Views/Shared/Partials/_DetailTimeline.cshtml");
        var related = ReadRepoFile("src/PTGOilSystem.Web/Views/Shared/Partials/_RelatedRecords.cshtml");
        var actionBar = ReadRepoFile("src/PTGOilSystem.Web/Views/Shared/Partials/_DetailActionBar.cshtml");

        Assert.Contains(":root[data-theme=\"dark\"]", css);
        Assert.Contains("@media print", css);
        Assert.Contains(":where(html, body)", css);
        Assert.Contains(".ak-stat-grid", css);
        Assert.Contains("@media (max-width: 767.98px)", css);
        Assert.Contains("grid-template-columns: repeat(2, minmax(0, 1fr))", css);
        Assert.Contains(".ak-detail-page .ak-stat-grid", statCss);
        // Four cards share one row down to small tablets; the phone tier is two columns.
        Assert.Contains("--ak-stat-col: calc((100% - 3 * var(--ak-stat-gap)) / 4", statCss);
        Assert.Contains("@media (max-width: 1399.98px)", statCss);
        Assert.Contains("grid-template-columns: repeat(2, minmax(0, 1fr))", statCss);
        Assert.Contains("inset-inline-start", css);
        Assert.Contains(":focus-visible", css);
        Assert.Contains("overflow: visible", css);
        Assert.Contains(".ak-col-priority-2", css);
        Assert.Contains(".ak-col-priority-3", css);

        Assert.Contains("<h1", header);
        Assert.Contains("aria-describedby", header);
        Assert.Contains("aria-label", header);
        Assert.Contains("aria-current", pager);
        Assert.Contains("<time", timeline);
        Assert.Contains("aria-label", related);
        Assert.Contains("ak-detail-actionbar no-print", actionBar);
    }

    [Fact]
    public void Targeted_Legacy_Pager_And_Client_Tab_Implementations_Are_Gone()
    {
        var combined = string.Join(
            Environment.NewLine,
            DetailViews.Select(ReadView));

        Assert.DoesNotContain("ListPager(", combined);
        Assert.DoesNotContain("StorageLedgerPageUrl(", combined);
        Assert.DoesNotContain("PageWindow(", combined);
        Assert.DoesNotContain("data-ptg-tab-target", combined);
        Assert.DoesNotContain("data-shipment-file-tabs", combined);
        Assert.DoesNotContain("data-ptcd-tab", combined);
        Assert.DoesNotContain("data-ptcd-pager", combined);
    }

    private static string ReadView(string controller)
        => ReadRepoFile($"src/PTGOilSystem.Web/Views/{controller}/Details.cshtml");

    private static string ReadRepoFile(string relativePath)
        => File.ReadAllText(GetRepoPath(relativePath));

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
