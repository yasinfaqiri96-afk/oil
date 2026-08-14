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
            Assert.Contains("ak-detail-reference-layout", view);
            Assert.Contains("AkHeaderIdentity", view);
            Assert.Contains("_DetailOverview.cshtml", view);
            Assert.Contains("_DetailActivityList.cshtml", view);
            Assert.Contains("_DetailActionBar.cshtml", view);
            Assert.Contains("_DetailSecondary.cshtml", view);
            Assert.Contains("TimelineLimit = 4", view);
            Assert.DoesNotContain("_DetailsTabs.cshtml", view);
            Assert.DoesNotContain("_DetailPager.cshtml", view);
            Assert.DoesNotContain("data-ptcd-tab", view);
            Assert.DoesNotContain("data-ptcd-pager", view);
            Assert.DoesNotContain("_OperationsDetailMore.cshtml", view);
            Assert.DoesNotContain("<details", view);
        }

        // Both pilots carry a real workflow, so their doable next steps sit in the shared
        // bottom bar and only side routes fall back to the kebab. The two lists must stay
        // disjoint: the page never feeds the same collection to the bar and to the kebab,
        // which is what used to print every action twice.
        var loading = ReadView("Loading");
        Assert.DoesNotContain("AkHeaderOverflowActions", loading);

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
        Assert.Contains("خلاصه اطلاعات بارگیری", loading);
        Assert.Contains("قابل ارسال / تخصیص", loading);
        Assert.Contains("TimelineTitle = T(\"آخرین فعالیت‌ها\"", loading);
        Assert.DoesNotContain("ak-loading-rub-secondary", loading);
        Assert.DoesNotContain("ak-detail-tabbed-sections", loading);

        var transport = ReadView("InventoryTransportLegs");
        Assert.Contains("خلاصه اطلاعات حمل", transport);
        Assert.Contains("VisualAvatar = transportVisual", transport);
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
            Assert.Contains("ak-detail-reference-layout", view);
            Assert.Contains("_DetailOverview.cshtml", view);
            Assert.Contains("_DetailActivityList.cshtml", view);
            Assert.Contains("_DetailSecondary.cshtml", view);
            Assert.Contains("_DetailActionBar.cshtml", view);
            Assert.Contains("TimelineTitle = T(\"آخرین فعالیت‌ها\"", view);
            Assert.DoesNotContain("Items = headerItems", view);
            Assert.DoesNotContain("Items = identityItems", view);

            var activity = view.LastIndexOf("_DetailActivityList.cshtml", StringComparison.Ordinal);
            var timeline = view.LastIndexOf("_DetailSecondary.cshtml", StringComparison.Ordinal);
            var actions = view.LastIndexOf("_DetailActionBar.cshtml", StringComparison.Ordinal);
            Assert.True(activity >= 0 && timeline > activity && actions > timeline);
        }

        var overview = ReadRepoFile("src/PTGOilSystem.Web/Views/Shared/Partials/_DetailOverview.cshtml");
        var css = ReadRepoFile("src/PTGOilSystem.Web/wwwroot/css/ptg/73-detail-system.css");
        Assert.Contains("width=\"38\" height=\"38\"", overview);
        Assert.Contains("inline-size: 38px", css);
        Assert.Contains(".ak-detail-reference-layout > .ak-detail-reference-section", css);
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
            Assert.Contains("ak-linear-detail", view);
            Assert.Contains("data-ak-operations-detail=\"true\"", view);
            Assert.Contains("AkHeaderIdentity", view);
            Assert.Contains("_DetailOverview.cshtml", view);
            Assert.Contains("_DetailActivityList.cshtml", view);
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

        // The page identity facts live in the one shared summary card above the tabs — the same
        // partial every other master-data Details view uses — not in a KPI strip and not in a
        // "status" tab. Exactly one card, and it must precede the tab strip, otherwise the page
        // is no longer summary-then-tabs.
        Assert.Equal(1, Count(view, "_DetailSummaryCard.cshtml"));
        Assert.True(
            view.IndexOf("_DetailSummaryCard.cshtml", StringComparison.Ordinal)
                < view.IndexOf("_DetailsTabs.cshtml", StringComparison.Ordinal));
        Assert.Contains("_DetailAdvancedSection.cshtml", view);
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
        Assert.Contains(".operational-asset-details-page > .ak-summary-card", css);
        Assert.Contains(".operational-asset-details-page .oa-period-zone", css);
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
