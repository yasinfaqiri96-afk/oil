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
    public void Pilot_Views_Use_The_Reference_Detail_Composition()
    {
        foreach (var controller in new[] { "InventoryTransportLegs", "Loading" })
        {
            var view = ReadView(controller);
            Assert.Contains("ak-linear-detail", view);
            Assert.Contains("ptg-record-detail", view);
            Assert.Contains("AkHeaderIdentity", view);
            Assert.Contains("_DetailCards.cshtml", view);
            Assert.Contains("_DetailMore.cshtml", view);
            if (controller == "InventoryTransportLegs")
            {
                Assert.Contains("TimelineLimit = 4", view);
            }
            Assert.DoesNotContain("_DetailsTabs.cshtml", view);
            Assert.DoesNotContain("_DetailPager.cshtml", view);
            Assert.DoesNotContain("data-ptcd-tab", view);
            Assert.DoesNotContain("data-ptcd-pager", view);
            Assert.DoesNotContain("_OperationsDetailMore.cshtml", view);
            Assert.DoesNotContain("<details", view);
        }

        // Loading keeps every action in one header group; transport retains the shared
        // next-step bar. Neither page duplicates the same action in both locations.
        var loading = ReadView("Loading");
        Assert.DoesNotContain("AkHeaderOverflowActions", loading);
        Assert.Contains("AkHeaderCompanionAction", loading);
        Assert.DoesNotContain("NextActions =", loading);

        var transport = ReadView("InventoryTransportLegs");
        Assert.Contains("ViewData[\"AkHeaderOverflowActions\"] = overflowActions;", transport);
        Assert.DoesNotContain("ViewData[\"AkHeaderOverflowActions\"] = nextActions;", transport);
    }

    [Fact]
    public void Operation_Record_Sections_Use_One_Shared_Local_Tab_Rail()
    {
        var loading = ReadView("Loading");
        Assert.DoesNotContain("_DetailsTabs.cshtml", loading);
        Assert.DoesNotContain("data-instant-tabs", loading);
        Assert.DoesNotContain("id=\"loading-receipts\"", loading);

        var dispatch = ReadView("Dispatch");
        Assert.Contains("_DetailsTabs.cshtml", dispatch);
        Assert.Contains("data-instant-tabs", dispatch);
        Assert.Contains("#dispatch-{option.Slug}", dispatch);
        Assert.Contains("id=\"dispatch-sales\"", dispatch);
        Assert.Contains("id=\"dispatch-customs\"", dispatch);
        Assert.Contains("id=\"dispatch-expenses\"", dispatch);
        Assert.Contains("id=\"dispatch-receipts\"", dispatch);

        var transport = ReadView("InventoryTransportLegs");
        Assert.DoesNotContain("_DetailsTabs.cshtml", transport);
        Assert.DoesNotContain("id=\"itl-receipts\"", transport);
        Assert.DoesNotContain("transport-chain-disclosure", transport);

        var css = ReadRepoFile("src/PTGOilSystem.Web/wwwroot/css/ptg/73-detail-system.css");
        Assert.Contains(".ak-detail-reference-layout", css);
        Assert.Contains(".ak-linear-detail .ak-detail-metrics", css);
    }

    [Fact]
    public void Loading_And_Transport_Reference_Details_Keep_One_Surface_And_Three_Closing_Columns()
    {
        var loading = ReadView("Loading");
        Assert.Contains("اطلاعات اصلی", loading);
        Assert.Contains("قابل ارسال / تخصیص", loading);
        Assert.Contains("TimelineTitle = T(\"همه رویدادهای بارگیری\"", loading);
        Assert.DoesNotContain("Activity = activityRows", loading);
        Assert.DoesNotContain("ak-loading-rub-secondary", loading);
        Assert.DoesNotContain("ak-detail-tabbed-sections", loading);

        var transport = ReadView("InventoryTransportLegs");
        Assert.Contains("مسیر و وضعیت", transport);
        Assert.Contains("Steps = routeSteps", transport);
        Assert.Contains("if (Model.TransportType == LoadingTransportType.Truck)", transport);
        Assert.Contains("else if (Model.TransportType == LoadingTransportType.Wagon)", transport);
        Assert.Contains("else if (Model.TransportType == LoadingTransportType.Vessel)", transport);
        Assert.DoesNotContain("transport-records-disclosure", transport);
        Assert.DoesNotContain("ptcdDetailModal", transport);

        var css = ReadRepoFile("src/PTGOilSystem.Web/wwwroot/css/ptg/73-detail-system.css");
        Assert.Contains("grid-template-columns: minmax(0, 1.35fr) minmax(0, 1.08fr) minmax(220px, .82fr)", css);
        Assert.Contains(".ak-detail-reference-layout > :is(.ak-detail-activity, .ak-detail-secondary, .ak-detail-actions-panel)", css);
        Assert.Contains(".ak-linear-detail .ak-detail-overview-body.has-visual", css);
    }

    [Fact]
    public void Requested_Operation_Details_Share_Reference_Layout_Without_Header_Info_Strip()
    {
        foreach (var controller in new[]
                 {
                     "Loading", "InventoryTransportLegs", "LoadingReceipts",
                     "CustomsDeclarations", "LossEvents", "Expenses", "Sales"
                 })
        {
            var view = ReadView(controller);
            Assert.Contains("ptg-record-detail", view);
            Assert.Contains("_DetailCards.cshtml", view);
            Assert.Contains("_DetailMore.cshtml", view);
            Assert.Contains("AkDetailCardsModel", view);
            Assert.Contains("AkDetailMoreModel", view);
            if (controller == "Loading")
            {
                Assert.Contains("TimelineTitle = T(\"همه رویدادهای بارگیری\"", view);
                Assert.DoesNotContain("Activity = activityRows", view);
            }
            else
            {
                Assert.Contains("TimelineTitle = T(\"آخرین فعالیت‌ها\"", view);
            }
            Assert.DoesNotContain("_DetailOverview.cshtml", view);
            Assert.DoesNotContain("Items = headerItems", view);
            Assert.DoesNotContain("Items = identityItems", view);

            // Identity strip first, secondary material last.
            var cards = view.LastIndexOf("_DetailCards.cshtml", StringComparison.Ordinal);
            var more = view.LastIndexOf("_DetailMore.cshtml", StringComparison.Ordinal);
            Assert.True(cards >= 0 && more > cards);
        }

        // The shared partials still compose the same three closing blocks, so no
        // activity row, history entry or next operation was dropped from any page.
        var moreShell = ReadRepoFile("src/PTGOilSystem.Web/Views/Shared/Partials/_DetailMore.cshtml");
        Assert.Contains("_DetailActivityList.cshtml", moreShell);
        Assert.Contains("_DetailSecondary.cshtml", moreShell);
        Assert.Contains("_DetailActionBar.cshtml", moreShell);

        var overview = ReadRepoFile("src/PTGOilSystem.Web/Views/Shared/Partials/_DetailOverview.cshtml");
        var css = ReadRepoFile("src/PTGOilSystem.Web/wwwroot/css/ptg/73-detail-system.css");
        Assert.Contains("width=\"38\" height=\"38\"", overview);
        // The metric illustration follows the shared stat card: it owns a share
        // of the card box instead of a fixed 38px thumbnail, and both of its
        // axes are card-driven so a square asset cannot overflow the card.
        Assert.Contains(".ak-linear-detail .ak-detail-metric-avatar", css);
        Assert.Contains("--ak-metric-visual: min(40%, 112px)", css);
        Assert.Contains("block-size: calc(100% - (var(--ak-metric-inset) * 2))", css);
        Assert.Contains(".ptg-record-detail .ptg-td-grid", css);
        Assert.Contains(".ptg-record-detail .ptg-td-step", css);
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
            var usesCards = view.Contains("_DetailCards.cshtml", StringComparison.Ordinal);
            Assert.Contains("ak-operations-detail", view);
            Assert.Contains("ak-linear-detail", view);
            Assert.Contains("data-ak-operations-detail=\"true\"", view);
            Assert.Contains("AkHeaderIdentity", view);
            if (usesCards)
            {
                Assert.Contains("_DetailMore.cshtml", view);
            }
            else
            {
                Assert.Contains("_DetailOverview.cshtml", view);
                Assert.Contains("_DetailActivityList.cshtml", view);
            }
            Assert.DoesNotContain("_OperationsDetailMore.cshtml", view);
            Assert.DoesNotContain("_DetailKpiStrip.cshtml", view);
            Assert.DoesNotContain("<vc:stat-card", view);
            Assert.DoesNotContain("<details", view);
        }

        var shipment = ReadView("ShipmentPnl");

        Assert.Equal(1, Count(shipment, "data-ak-tab=\"shipment-"));
        Assert.Contains("data-ak-tab=\"shipment-@exportTab\"", shipment);
        Assert.Contains("var shipmentExportTabs = new[]", shipment);
        Assert.DoesNotContain("Estimated shortage value\")\" value=", shipment);
        Assert.Contains("shipmentActivityRows", shipment);

        var settlements = ReadRepoFile("src/PTGOilSystem.Web/Views/TruckSettlements/Index.cshtml");
        Assert.Contains("ak-operations-settlement", settlements);
        Assert.Contains("ak-operations-clean-page", settlements);
        Assert.Contains("data-ak-operations-detail=\"true\"", settlements);

        var css = ReadRepoFile("src/PTGOilSystem.Web/wwwroot/css/ptg/73-detail-system.css");
        Assert.Contains(".ak-linear-detail", css);
        Assert.Contains(".ak-linear-detail .ak-detail-overview", css);
        Assert.Contains(".ak-linear-detail .ak-detail-metrics", css);
        Assert.Contains(".ak-linear-detail .ak-detail-activity-row", css);
        Assert.Contains(".ak-linear-detail .ak-detail-actionbar", css);
        Assert.Contains(".ak-linear-detail .ak-detail-secondary", css);
        Assert.Contains("@media (max-width: 991.98px)", css);
        Assert.Contains("grid-template-columns: repeat(2, minmax(0, 1fr))", css);
        Assert.DoesNotContain(".ak-operations-detail .ak-operations-more", css);
        Assert.DoesNotContain(".ak-operations-detail .ak-operations-overview", css);
        Assert.Contains(".ak-operations-detail.ak-operations-settlement > .ak-list", css);
        Assert.Contains("@media print", css);

        // The declaration profile list was renamed summaryItems -> mainItems by the
        // shared linear layout; the rule is unchanged: the consignment weight is a
        // metric, so it must not be repeated as a profile fact.
        var customs = ReadView("CustomsDeclarations");
        var summaryStart = customs.IndexOf("var mainItems", StringComparison.Ordinal);
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

        // Tab-first composition, the same order as Payments/Partners Details: tab rail, then one
        // stat strip for the active tab, then the period filter, then the panes. The strip is the
        // shared partial (capped at five cards), never a page-local card wall.
        Assert.Equal(1, Count(view, "_DetailKpiStrip.cshtml"));
        Assert.True(
            view.IndexOf("_DetailsTabs.cshtml", StringComparison.Ordinal)
                < view.IndexOf("_DetailKpiStrip.cshtml", StringComparison.Ordinal));

        // The identity facts live in the one shared summary card — the same partial every other
        // master-data Details view uses — inside the overview pane, so they no longer push the
        // tabs and the decision-making numbers below the fold.
        Assert.Equal(1, Count(view, "_DetailSummaryCard.cshtml"));
        Assert.True(
            view.IndexOf("_DetailKpiStrip.cshtml", StringComparison.Ordinal)
                < view.IndexOf("_DetailSummaryCard.cshtml", StringComparison.Ordinal));
        Assert.Contains("_DetailAdvancedSection.cshtml", view);

        // One period filter, in the canonical detail toolbar, rendered only for the tabs whose
        // rows are actually filtered by date. No page-local collapse toggle.
        Assert.Equal(1, Count(view, "class=\"ak-detail-toolbar no-print\""));
        Assert.Contains("if (isPeriodTab)", view);
        Assert.DoesNotContain("oa-period", view);
        Assert.Equal(3, Count(view, "class=\"modal fade oa-action-modal\""));
        Assert.Contains("id=\"oaOwnershipModal\"", view);
        Assert.Contains("id=\"oaRentModal\"", view);
        Assert.Contains("id=\"oaExpenseModal\"", view);
        Assert.Equal(3, Count(view, "data-bs-toggle=\"modal\""));
        Assert.DoesNotContain("data-oa-reopen", view);

        // Every POST on the page carries a token — the three modal forms plus the per-row rent
        // cancel form. Counting against the forms rather than a fixed number means adding another
        // action cannot silently ship an unprotected POST.
        Assert.Equal(Count(view, "method=\"post\""), Count(view, "@Html.AntiForgeryToken()"));
        Assert.Contains("asp-action=\"CancelRent\"", view);
        Assert.Contains(".operational-asset-details-page .oa-row-action", css);

        // Every structural hook the view emits is styled in the page's own scoped block — no
        // inline styles and no orphan selectors for markup the page no longer renders.
        Assert.Contains(".ak-detail-page.operational-asset-details-page", css);
        Assert.Contains(".operational-asset-details-page .ak-summary-card", css);
        Assert.Contains(".operational-asset-details-page .ak-detail-kpi-strip", css);
        Assert.Contains(".operational-asset-details-page .ak-detail-toolbar", css);
        Assert.DoesNotContain(".oa-period", css);
        Assert.DoesNotContain(".oa-plain-list", css);
        Assert.DoesNotContain(".oa-total-row", css);
        Assert.Contains(".operational-asset-details-page .oa-block-head", css);
        Assert.Contains(".operational-asset-details-page .oa-modal-trigger", css);
        Assert.Contains(".operational-asset-details-page .oa-action-modal .modal-dialog", css);
        Assert.DoesNotContain("style=\"", view);
    }

    [Fact]
    public void Every_Take_In_A_Detail_View_Is_Paired_With_Shared_Server_Pager()
    {
        foreach (var controller in DetailViews)
        {
            var view = ReadView(controller);
            if (view.Contains("PageSize", StringComparison.OrdinalIgnoreCase)
                && view.Contains(".Skip(", StringComparison.Ordinal))
            {
                Assert.Contains("_DetailPager.cshtml", view);
                Assert.Contains(".Take(", view);
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
        Assert.Contains("ak-detail-actions-panel no-print", actionBar);
        Assert.Contains("class=\"ak-detail-actionbar\"", actionBar);
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
