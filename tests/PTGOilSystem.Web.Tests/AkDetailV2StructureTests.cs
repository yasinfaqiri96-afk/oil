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
        var operationsMore = ReadRepoFile("src/PTGOilSystem.Web/Views/Shared/Partials/_OperationsDetailMore.cshtml");

        foreach (var controller in new[] { "InventoryTransportLegs", "Loading" })
        {
            var view = ReadView(controller);
            Assert.Contains("_DetailPager.cshtml", view);
            Assert.Contains("_OperationsDetailMore.cshtml", view);
            Assert.Contains("_DetailActionBar.cshtml", view);
            Assert.DoesNotContain("data-ptcd-tab", view);
            Assert.DoesNotContain("data-ptcd-pager", view);
        }

        Assert.Contains("_DetailTimeline.cshtml", operationsMore);
        Assert.Contains("_RelatedRecords.cshtml", operationsMore);
        Assert.Contains("_DetailInfoGrid.cshtml", operationsMore);
    }

    [Fact]
    public void Operation_Record_Sections_Use_One_Shared_Local_Tab_Rail()
    {
        var loading = ReadView("Loading");
        Assert.Contains("_DetailsTabs.cshtml", loading);
        Assert.Contains("data-instant-tabs", loading);
        Assert.Contains("#loading-receipts", loading);
        Assert.Contains("#loading-customs", loading);
        Assert.Contains("#loading-losses", loading);
        Assert.Contains("ak-detail-list-actions", loading);

        var dispatch = ReadView("Dispatch");
        Assert.Contains("_DetailsTabs.cshtml", dispatch);
        Assert.Contains("data-instant-tabs", dispatch);
        Assert.Contains("#dispatch-{option.Slug}", dispatch);
        Assert.Contains("id=\"dispatch-sales\"", dispatch);
        Assert.Contains("id=\"dispatch-customs\"", dispatch);
        Assert.Contains("id=\"dispatch-expenses\"", dispatch);
        Assert.Contains("id=\"dispatch-receipts\"", dispatch);

        var transport = ReadView("InventoryTransportLegs");
        Assert.Contains("#itl-receipts", transport);
        Assert.Contains("id=\"itl-receipts\"", transport);
        Assert.Contains("(string?)\"itl-receipts\"", transport);

        var css = ReadRepoFile("src/PTGOilSystem.Web/wwwroot/css/ptg/73-detail-system.css");
        Assert.Contains("[data-loading-details] #loading-receipts .ak-list-row", css);
        Assert.Contains("[data-loading-details] .ak-detail-kpi-strip", css);
    }

    [Fact]
    public void Operations_Details_Use_One_Compact_Shared_Composition()
    {
        var operationsDetails = new[]
        {
            "Loading", "LoadingReceipts", "Expenses", "Sales", "LossEvents",
            "CustomsDeclarations", "Dispatch", "InventoryTransportLegs", "ShipmentPnl"
        };

        foreach (var controller in operationsDetails)
        {
            var view = ReadView(controller);
            Assert.Contains("ak-operations-detail", view);
            Assert.Contains("data-ak-operations-detail=\"true\"", view);
        }

        foreach (var controller in operationsDetails.Where(controller => controller != "ShipmentPnl"))
        {
            var view = ReadView(controller);
            Assert.Contains("ak-operations-overview", view);
            Assert.Contains("ak-operations-clean-page", view);
            Assert.Contains("ak-operations-clean-overview", view);
            Assert.Contains("_OperationsDetailMore.cshtml", view);
        }

        var shipment = ReadView("ShipmentPnl");

        // Seven fixed tabs plus the templated tab the export tab loop renders.
        Assert.Equal(8, Count(shipment, "data-ak-tab=\"shipment-"));
        Assert.Contains("data-ak-tab=\"shipment-@exportTab\"", shipment);
        Assert.DoesNotContain("Estimated shortage value\")\" value=", shipment);
        Assert.Contains("ak-operations-tab-fact", shipment);

        var settlements = ReadRepoFile("src/PTGOilSystem.Web/Views/TruckSettlements/Index.cshtml");
        Assert.Contains("ak-operations-settlement", settlements);
        Assert.Contains("ak-operations-clean-page", settlements);
        Assert.Contains("data-ak-operations-detail=\"true\"", settlements);

        var css = ReadRepoFile("src/PTGOilSystem.Web/wwwroot/css/ptg/73-detail-system.css");
        Assert.Contains(".ak-operations-detail .ak-operations-overview", css);
        Assert.Contains(".ak-operations-detail .ak-operations-more", css);
        Assert.Contains("content: attr(aria-label)", css);
        Assert.Contains("@media (max-width: 991.98px)", css);
        Assert.Contains(".ak-operations-detail .ak-stat-grid", css);
        Assert.Contains("grid-template-columns: repeat(2, minmax(0, 1fr))", css);
        Assert.Contains(".ak-operations-clean-page .ak-operations-clean-overview", css);
        Assert.Contains(".ak-summary-card:last-child:nth-child(odd)", css);
        Assert.Contains("--ak-operations-panel-radius: 16px", css);
        Assert.Contains("--ak-operations-panel-surface: var(--background-paper, #fff)", css);
        Assert.Contains("--ak-operations-panel-shadow: var(--ptg-panel-shadow)", css);
        Assert.Contains(".ak-operations-detail .ak-detail-section", css);
        Assert.Contains(".ak-operations-detail.ak-operations-settlement > .ak-list", css);
        Assert.Contains(".ak-operations-detail .ak-detail-section .ak-detail-section", css);
        Assert.Contains(".ak-operations-detail .modal .ak-detail-section", css);
        Assert.Contains(".ak-operations-detail .ak-detail-section .shipment-record-list", css);
        Assert.Contains("border-color: transparent", css);
        Assert.Contains("@media print", css);

        var operationsSurfaceSelectorLines = css
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line =>
                (line.Contains(".ak-operations-overview", StringComparison.Ordinal)
                 || line.Contains(".ak-operations-more", StringComparison.Ordinal)
                 || line.Contains(".ak-operations-settlement", StringComparison.Ordinal)
                 || line.Contains(".ak-operations-tab-fact", StringComparison.Ordinal))
                && (line.EndsWith('{') || line.EndsWith(',')))
            .ToList();
        Assert.NotEmpty(operationsSurfaceSelectorLines);
        Assert.All(
            operationsSurfaceSelectorLines,
            selector => Assert.Contains(".ak-operations-detail", selector));

        var modalSelector = css.IndexOf(
            ".ak-operations-detail .modal .ak-detail-section",
            StringComparison.Ordinal);
        var recordListSelector = css.IndexOf(
            ".ak-operations-detail .ak-detail-section .shipment-record-list",
            StringComparison.Ordinal);
        var flatRuleStart = css.IndexOf('{', modalSelector);
        var flatRuleEnd = css.IndexOf('}', flatRuleStart);
        Assert.True(modalSelector >= 0 && recordListSelector > modalSelector);
        Assert.True(flatRuleStart > recordListSelector && flatRuleEnd > flatRuleStart);
        var flatRule = css[flatRuleStart..flatRuleEnd];
        Assert.Contains("border-radius: 0", flatRule);
        Assert.Contains("background: transparent", flatRule);
        Assert.Contains("box-shadow: none", flatRule);

        var customs = ReadView("CustomsDeclarations");
        var summaryStart = customs.IndexOf("var summaryItems", StringComparison.Ordinal);
        var advancedStart = customs.IndexOf("var advancedItems", StringComparison.Ordinal);
        Assert.True(summaryStart >= 0 && advancedStart > summaryStart);
        Assert.DoesNotContain(
            "Model.ConsignmentWeightMt",
            customs[summaryStart..advancedStart]);
        Assert.Equal(2, Count(customs, "Model.ConsignmentWeightMt"));
    }

    [Fact]
    public void Master_Data_Detail_Content_Cards_Use_Scoped_Elevation_Without_Touching_Kpis()
    {
        var css = ReadRepoFile("src/PTGOilSystem.Web/wwwroot/css/ptg/73-detail-system.css");
        const string contractStart = "/* ===================================================================\n"
            + "   Shared master-data Details content elevation";
        const string contractEnd = "/* ===================================================================\n"
            + "   Operations dossier";
        const string primarySelector =
            "body.boltz-shell.app-shell-authenticated.is-master-data-compact.action-details "
            + ".ak-detail-page .ak-detail-section"
            + ":not(.ak-detail-section .ak-detail-section)"
            + ":not(.modal .ak-detail-section)";

        var start = css.IndexOf(contractStart, StringComparison.Ordinal);
        var end = css.IndexOf(contractEnd, start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);

        var contract = css[start..end];
        Assert.Equal(2, Count(contract, primarySelector));
        Assert.Contains("border-color: transparent", contract);
        Assert.Contains("border-radius: 16px", contract);
        Assert.Contains("background: var(--background-paper, #fff)", contract);
        Assert.Contains("box-shadow: var(--ptg-panel-shadow)", contract);
        Assert.Contains("@media print", contract);
        Assert.Contains("border: 1px solid #ccc", contract);
        Assert.Contains("box-shadow: none", contract);

        Assert.DoesNotContain(".ak-stat-card", contract);
        Assert.DoesNotContain(".ak-stat-grid", contract);
        Assert.DoesNotContain(".ak-detail-kpi-strip", contract);
        Assert.DoesNotContain("vc:stat-card", contract);
        Assert.DoesNotContain(".ak-summary-card", contract);
        Assert.DoesNotContain(".ak-operations-detail", contract);
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
    public void Operational_Asset_Details_Uses_A_Compact_Tab_First_Composition()
    {
        var view = ReadView("OperationalAssets");
        var css = ReadRepoFile("src/PTGOilSystem.Web/wwwroot/css/ptg/11-details.css");

        Assert.Contains("data-operational-asset-details", view);
        Assert.DoesNotContain("_DetailKpiStrip.cshtml", view);
        Assert.DoesNotContain("_DetailSummaryCard.cshtml", view);
        Assert.Equal(3, Count(view, "class=\"modal fade oa-action-modal\""));
        Assert.Contains("id=\"oaOwnershipModal\"", view);
        Assert.Contains("id=\"oaRentModal\"", view);
        Assert.Contains("id=\"oaExpenseModal\"", view);
        Assert.Equal(3, Count(view, "data-bs-toggle=\"modal\""));
        Assert.Equal(3, Count(view, "@Html.AntiForgeryToken()"));
        Assert.DoesNotContain("data-oa-reopen", view);

        Assert.Contains(".ak-detail-page.operational-asset-details-page", css);
        Assert.Contains(".operational-asset-details-page .tab-pane > .ak-list", css);
        Assert.Contains(".operational-asset-details-page [data-oa-split]", css);
        Assert.Contains(".operational-asset-details-page .oa-modal-trigger", css);
        Assert.Contains(".operational-asset-details-page .oa-action-modal .modal-dialog", css);
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

    // پایان خط فایل‌ها روی ویندوز CRLF و روی لینوکس LF است؛ قراردادهای چندخطیِ این تست‌ها
    // با "\n" نوشته شده‌اند. نرمال‌سازی، تست را مستقل از سکو می‌کند و هیچ فایل UI را تغییر نمی‌دهد.
    private static string ReadRepoFile(string relativePath)
        => File.ReadAllText(GetRepoPath(relativePath)).Replace("\r\n", "\n").Replace("\r", "\n");

    private static int Count(string value, string token)
        => (value.Length - value.Replace(token, string.Empty, StringComparison.Ordinal).Length) / token.Length;

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
