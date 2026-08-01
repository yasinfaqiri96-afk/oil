using System.Runtime.CompilerServices;
using Xunit;

namespace PTGOilSystem.Web.Tests;

public class ShellViewStructureTests
{
    private static readonly string[] CanonicalPtgStylesheets =
    [
        "~/css/ptg/01-tokens.css",
        "~/css/ptg/02-base.css",
        "~/css/ptg/03-layout.css",
        "~/css/ptg/04-sidebar.css",
        "~/css/ptg/05-components.css",
        "~/css/ptg/06-forms.css",
        "~/css/ptg/07-tables.css",
        "~/css/ptg/08-modals.css",
        "~/css/ptg/09-pages.css",
        "~/css/ptg/10-responsive.css",
        "~/css/ptg/11-details.css",
        "~/css/ptg/12-dashboard.css",
        "~/css/ptg/13-compat.css",
    ];

    [Theory]
    [InlineData("~/css/ptg/01-tokens.css")]
    [InlineData("~/css/ptg/02-base.css")]
    [InlineData("~/css/ptg/03-layout.css")]
    [InlineData("~/css/ptg/04-sidebar.css")]
    [InlineData("~/css/ptg/05-components.css")]
    [InlineData("~/css/ptg/06-forms.css")]
    [InlineData("~/css/ptg/07-tables.css")]
    [InlineData("~/css/ptg/08-modals.css")]
    [InlineData("~/css/ptg/09-pages.css")]
    [InlineData("~/css/ptg/10-responsive.css")]
    [InlineData("~/css/ptg/11-details.css")]
    [InlineData("~/css/ptg/12-dashboard.css")]
    [InlineData("~/css/ptg/13-compat.css")]
    public void Layout_Loads_Each_Canonical_Ptg_Stylesheet_In_Order(string expectedStylesheet)
    {
        var layout = ReadRepoFile("src/PTGOilSystem.Web/Views/Shared/_Layout.cshtml");
        var modalLayout = ReadRepoFile("src/PTGOilSystem.Web/Views/Shared/_ModalLayout.cshtml");

        Assert.Contains(expectedStylesheet, layout);
        Assert.Contains(expectedStylesheet, modalLayout);

        var previousIndex = -1;
        foreach (var stylesheet in CanonicalPtgStylesheets)
        {
            var currentIndex = layout.IndexOf(stylesheet, StringComparison.Ordinal);
            Assert.True(currentIndex > previousIndex, $"{stylesheet} must load once and after the preceding PTG layer.");
            previousIndex = currentIndex;
        }
    }

    [Fact]
    public void Desktop_Sidebar_Collapse_Keeps_An_Icon_Rail()
    {
        var layout = ReadRepoFile("src/PTGOilSystem.Web/Views/Shared/_Layout.cshtml");
        var tokensCss = ReadRepoFile("src/PTGOilSystem.Web/wwwroot/css/ptg/01-tokens.css");
        var sidebarCss = ReadRepoFile("src/PTGOilSystem.Web/wwwroot/css/ptg/04-sidebar.css")
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        var navigationJs = ReadRepoFile("src/PTGOilSystem.Web/wwwroot/js/navigation.js");

        // The rail is the invariant this test guards: the collapsed sidebar must stay an
        // 88px icon rail and the expanded sidebar must be strictly wider than it. The exact
        // expanded width is a design choice (currently 224px) and is deliberately not pinned.
        Assert.Contains("--layout-sidebar-mini: 88px", tokensCss);
        var expandedSidebarPx = ReadPixelToken(tokensCss, "--layout-sidebar");
        var railPx = ReadPixelToken(tokensCss, "--layout-sidebar-mini");
        Assert.Equal(88, railPx);
        Assert.True(
            expandedSidebarPx > railPx,
            $"Expanded sidebar ({expandedSidebarPx}px) must be wider than the icon rail ({railPx}px).");
        Assert.Contains("is-sidebar-collapsed .ak-navpanel {\n        display: flex;", sidebarCss);
        Assert.Contains("is-sidebar-collapsed .ptg-nav-ic > svg {\n        inline-size: 27px;\n        block-size: 27px;", sidebarCss);
        Assert.Contains("is-sidebar-collapsed .boltz-nav-link > span", sidebarCss);
        Assert.Contains("window.matchMedia(\"(min-width: 1200px)\")", navigationJs);
        Assert.Contains("window.matchMedia(\"(min-width: 992px)\")", navigationJs);
        Assert.Contains("document.body.classList.remove(\"is-sidebar-collapsed\")", navigationJs);
        Assert.Contains("aria-label=\"@node.Label\"", layout);
        Assert.Contains("title=\"@node.Label\"", layout);
    }

    [Fact]
    public void Sidebar_Uses_Slightly_Brighter_Navy_Surfaces_Without_Changing_State_Colors()
    {
        var tokensCss = ReadRepoFile("src/PTGOilSystem.Web/wwwroot/css/ptg/01-tokens.css");
        var sidebarCss = ReadRepoFile("src/PTGOilSystem.Web/wwwroot/css/ptg/04-sidebar.css");

        Assert.Contains("--ptg-sidebar-panel: #1A467D", tokensCss);
        Assert.Contains("--ptg-sidebar-rail: #14355F", tokensCss);
        Assert.Contains("--ptg-sidebar-panel: #173D6A", tokensCss);
        Assert.Contains("--ptg-sidebar-rail: #113057", tokensCss);
        Assert.Contains("--ptg-sidebar-hover-bg: rgba(255, 255, 255, 0.10)", tokensCss);
        Assert.Contains("--ptg-sidebar-active-bg: rgba(255, 255, 255, 0.14)", tokensCss);
        Assert.Contains("--ptg-sidebar-danger: #FF9A9A", tokensCss);
        Assert.Contains("background: var(--ptg-sidebar-panel)", sidebarCss);
        Assert.Contains("background: var(--ptg-sidebar-rail)", sidebarCss);
    }

    [Fact]
    public void Shared_PageFrame_Owns_Workspace_Width_Gutters_And_Form_Columns()
    {
        var layout = ReadRepoFile("src/PTGOilSystem.Web/Views/Shared/_Layout.cshtml");
        var tokensCss = ReadRepoFile("src/PTGOilSystem.Web/wwwroot/css/ptg/01-tokens.css");
        var pageFrameCss = ReadRepoFile("src/PTGOilSystem.Web/wwwroot/css/ptg/70-page-frame.css");
        var akauntingCss = ReadRepoFile("src/PTGOilSystem.Web/wwwroot/css/ptg/45-akaunting.css");
        var formCss = ReadRepoFile("src/PTGOilSystem.Web/wwwroot/css/ptg/50-ak-components.css");

        Assert.Contains("ptg-page-body ptg-page-frame", layout);
        Assert.Contains("~/css/ptg/70-page-frame.css", layout);
        Assert.True(
            layout.LastIndexOf("~/css/ptg/70-page-frame.css", StringComparison.Ordinal)
            > layout.LastIndexOf("~/css/ptg/61-finance-workspace.css", StringComparison.Ordinal));
        Assert.Contains("--layout-content-max: 1200px", tokensCss);
        Assert.Contains("--layout-form-max: 860px", tokensCss);
        Assert.Contains("--layout-form-wide-max: 1080px", tokensCss);
        Assert.Contains("padding-inline: var(--layout-gutter-desktop)", pageFrameCss);
        Assert.Contains("max-inline-size: var(--layout-content-max)", pageFrameCss);
        Assert.Contains("@media (min-width: 768px) and (max-width: 1199.98px)", pageFrameCss);
        Assert.DoesNotContain(".ptg-page > .boltz-content-body", akauntingCss);
        Assert.Contains(".ak-form-page:has(> form.ak-form)", formCss);
        Assert.Contains("padding-inline: 0", formCss);
    }

    [Fact]
    public void Header_Removes_Settings_And_Fits_The_Account_Avatar()
    {
        var layout = ReadRepoFile("src/PTGOilSystem.Web/Views/Shared/_Layout.cshtml");
        var shellCss = ReadRepoFile("src/PTGOilSystem.Web/wwwroot/css/boltz-shell.css")
            .Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.DoesNotContain("ptg-hgear", layout);
        Assert.DoesNotContain("bi bi-gear", layout);
        Assert.Contains("ptg-havatar-ring ptg-havatar-trigger", layout);
        Assert.Contains(".ptg-havatar-ring {\n    width: 2.5rem;\n    height: 2.5rem;\n    padding: 2px;", shellCss);
        Assert.Contains("object-fit: contain", shellCss);
    }

    [Fact]
    public void Header_Renders_The_Active_Fiscal_Year_Switcher()
    {
        var layout = ReadRepoFile("src/PTGOilSystem.Web/Views/Shared/_Layout.cshtml");
        var component = ReadRepoFile(
            "src/PTGOilSystem.Web/Views/Shared/Components/FiscalYearSwitcher/Default.cshtml");
        var shellCss = ReadRepoFile("src/PTGOilSystem.Web/wwwroot/css/boltz-shell.css");

        // The switcher lives inside the authenticated topbar cluster, before the language menu.
        Assert.Contains("<vc:fiscal-year-switcher>", layout);
        Assert.True(
            layout.IndexOf("<vc:fiscal-year-switcher>", StringComparison.Ordinal)
            < layout.IndexOf("boltz-language-menu", StringComparison.Ordinal),
            "The fiscal-year switcher must render before the language selector.");

        // Selection posts to the dedicated context controller with an antiforgery token, and
        // the year id is the only thing submitted — the company is resolved server-side.
        Assert.Contains("asp-controller=\"FiscalYearContext\"", component);
        Assert.Contains("asp-action=\"Select\"", component);
        Assert.Contains("Html.AntiForgeryToken()", component);
        Assert.Contains("name=\"fiscalYearId\"", component);
        Assert.DoesNotContain("name=\"companyId\"", component);

        // Status badges reuse the canonical ak-status skin, not a bespoke one.
        Assert.Contains("ak-status ptg-hfyear-status", component);
        Assert.Contains(".ptg-hfyear", shellCss);
        Assert.Contains("@media (max-width: 575.98px)", shellCss);
    }

    [Fact]
    public void Canonical_Tabs_Skin_Loads_Last_And_Covers_Every_Tab_Family()
    {
        var layout = ReadRepoFile("src/PTGOilSystem.Web/Views/Shared/_Layout.cshtml");
        var modalLayout = ReadRepoFile("src/PTGOilSystem.Web/Views/Shared/_ModalLayout.cshtml");
        var tabsCss = ReadRepoFile("src/PTGOilSystem.Web/wwwroot/css/ptg/16-system-tabs.css");
        var listsCss = ReadRepoFile("src/PTGOilSystem.Web/wwwroot/css/ptg/15-system-lists.css");
        var tabsJs = ReadRepoFile("src/PTGOilSystem.Web/wwwroot/js/ptg-tabs.js");
        var sectionTabs = ReadRepoFile("src/PTGOilSystem.Web/Views/Shared/_SectionTabs.cshtml");
        var shipmentTabs = ReadRepoFile("src/PTGOilSystem.Web/Views/ShipmentPnl/Details.cshtml");
        var journeyTabs = ReadRepoFile("src/PTGOilSystem.Web/Views/ContractJourney/_ContractJourneyDetailTabsRail.cshtml");

        Assert.True(
            layout.LastIndexOf("~/css/ptg/16-system-tabs.css", StringComparison.Ordinal)
            > layout.IndexOf("RenderSectionAsync(\"Styles\"", StringComparison.Ordinal),
            "The canonical tabs skin must load after page-specific styles.");
        Assert.True(
            modalLayout.LastIndexOf("~/css/ptg/16-system-tabs.css", StringComparison.Ordinal)
            > modalLayout.IndexOf("RenderSectionAsync(\"Styles\"", StringComparison.Ordinal),
            "Modal tabs must use the same final skin.");

        // Akaunting tab tokens. These are the contract every rail renders against.
        Assert.Contains("--ptg-tabs-font-size: 14px", tabsCss);
        Assert.Contains("--ptg-tabs-text-color: #424242", tabsCss);
        Assert.Contains("--ptg-tabs-active-color: #173F73", tabsCss);
        Assert.Contains("--ptg-tabs-border-color: #E5E7EB", tabsCss);
        Assert.Contains("--ptg-tabs-horizontal-padding: 16px", tabsCss);
        Assert.Contains("--ptg-tabs-bottom-padding: 8px", tabsCss);
        Assert.Contains("--ptg-tabs-indicator-height: 2px", tabsCss);
        Assert.Contains("--ptg-tabs-transition-duration: 180ms", tabsCss);
        Assert.Contains("--ptg-tabs-focus-color: rgba(23, 63, 115, .25)", tabsCss);
        Assert.Contains("border-bottom: 1px solid var(--ptg-tabs-border-color)", tabsCss);

        // Flat rail: spacing comes from tab padding, never from a gap, and the
        // rail must not wrap to a second line on narrow screens.
        Assert.Contains("gap: 0;", tabsCss);
        Assert.Contains("flex-wrap: nowrap", tabsCss);
        Assert.Contains("scrollbar-width: thin", tabsCss);
        Assert.DoesNotContain("font-weight: 750 !important", listsCss);
        Assert.Contains(".ptg-tabs-rail", tabsCss);
        Assert.Contains(".ptg-tab-item", tabsCss);
        Assert.DoesNotContain(".section-tabs", tabsCss);
        Assert.DoesNotContain(".oa-reference-tabs", tabsCss);
        Assert.DoesNotContain(".transport-profile-tabs", tabsCss);

        // 45-akaunting.css loads after the canonical skin, so it must not restyle
        // tabs — its old block beat every rail with !important. It may only refer
        // to tabs to exclude them from the global link colour, which otherwise
        // outweighs .ptg-tab-item and paints idle link-tabs in the active purple.
        var akauntingCss = ReadRepoFile("src/PTGOilSystem.Web/wwwroot/css/ptg/45-akaunting.css");
        Assert.Contains(":not(.ptg-tab-item)", akauntingCss);
        Assert.DoesNotContain("font-size: 14px !important", akauntingCss);
        Assert.DoesNotContain("font-weight: 500 !important", akauntingCss);
        Assert.DoesNotContain(".ptg-tab-item {", akauntingCss);

        Assert.Contains("class=\"ptg-tabs-rail\"", sectionTabs);
        Assert.Contains("class=\"ptg-tab-item ", sectionTabs);
        Assert.Contains("data-section-tabs-group", sectionTabs);
        Assert.Contains("_DetailsTabs.cshtml", shipmentTabs);
        Assert.Contains("Context.Request.Query[\"tab\"]", shipmentTabs);
        Assert.DoesNotContain("data-shipment-file-tabs", shipmentTabs);
        Assert.Contains("class=\"ptg-tabs-rail\"", journeyTabs);
        Assert.DoesNotContain("cj-tabs-card", journeyTabs);
        Assert.DoesNotContain("cj-tab-pill", journeyTabs);
        Assert.DoesNotContain("cj-step-sep", journeyTabs);

        // Tabs are text-only, like the Akaunting reference: no icon markup inside
        // a rail, and no Bootstrap tab toggle anywhere in the system. (The page
        // action button next to the rail keeps its own icon — that is not a tab.)
        Assert.DoesNotContain("<i class=\"bi @tab.Icon\"", sectionTabs);
        Assert.DoesNotContain("<use href=\"#@tab.Icon\"", sectionTabs);
        Assert.DoesNotContain("<i class=\"bi @tab.Icon\"", journeyTabs);
        Assert.DoesNotContain("data-bs-toggle=\"tab\"", shipmentTabs);
        Assert.DoesNotContain("data-bs-toggle=\"tab\"", ReadRepoFile("src/PTGOilSystem.Web/Views/InventoryTransportLegs/Journey.cshtml"));
        Assert.DoesNotContain("data-bs-toggle=\"tab\"", ReadRepoFile("src/PTGOilSystem.Web/Views/OperationalAssets/Details.cshtml"));

        // One engine drives every rail: keyboard, ARIA and overflow scrolling.
        Assert.Contains("wireHorizontalScroll", tabsJs);
        Assert.Contains("event.preventDefault();", tabsJs);
        Assert.Contains("ArrowLeft", tabsJs);
        Assert.Contains("ArrowRight", tabsJs);
        Assert.Contains("role\", \"tablist\"", tabsJs);
        Assert.Contains("role\", \"tabpanel\"", tabsJs);
        Assert.Contains("aria-controls", tabsJs);
    }

    [Fact]
    public void Finance_Assets_Remain_Available_Across_Spa_And_Back_Navigation()
    {
        var layout = ReadRepoFile("src/PTGOilSystem.Web/Views/Shared/_Layout.cshtml");
        var financeCss = ReadRepoFile("src/PTGOilSystem.Web/wwwroot/css/ptg/61-finance-workspace.css");
        var invoiceDocument = ReadRepoFile("src/PTGOilSystem.Web/Views/Invoices/Document.cshtml");

        Assert.Contains("~/css/ptg/61-finance-workspace.css", layout);
        Assert.DoesNotContain("@if (isFinanceWorkspace)", layout);

        // The light/dark switch was removed from the topbar; the shell is light-only.
        Assert.DoesNotContain("data-finance-theme-toggle", layout);
        Assert.DoesNotContain("~/js/finance-theme.js", layout);

        Assert.Contains("body.is-finance-workspace", financeCss);
        Assert.Contains("data-ptg-page-asset=\"invoice-document\"", invoiceDocument);
    }

    [Fact]
    public void Dashboard_Uses_A_Scoped_Responsive_Erp_Composition()
    {
        var view = ReadRepoFile("src/PTGOilSystem.Web/Views/Home/Index.cshtml");
        var insights = ReadRepoFile("src/PTGOilSystem.Web/Views/Home/_DashboardInsights.cshtml");
        var css = ReadRepoFile("src/PTGOilSystem.Web/wwwroot/css/ptg/12-dashboard.css");
        var script = ReadRepoFile("src/PTGOilSystem.Web/wwwroot/js/dashboard.js");

        // The dashboard is the `dash-*` composition: a quick-action tile grid, headline
        // totals and one trend chart. The older `dashboard-shortcut` / `dashboard-chart-*`
        // naming and the hero banner are gone and must not come back.
        Assert.Contains("ViewData[\"DashboardAssets\"] = true", view);
        Assert.DoesNotContain("dashboard-hero", view);
        Assert.DoesNotContain("dashboard-period", view);
        Assert.DoesNotContain("PTG OIL SYSTEM", view);
        Assert.DoesNotContain("داشبورد مدیریتی", view);
        Assert.DoesNotContain("تصویر روشن از عملیات", view);
        Assert.DoesNotContain("dashboard-shortcut", view);
        Assert.DoesNotContain("dashboard-kpis", view);
        Assert.DoesNotContain("dashboard-metric-card", view);
        Assert.DoesNotContain("dashboard-activity", view);
        Assert.DoesNotContain("RecentActivities", view);

        // Seven quick-action tiles, exactly one of them the primary call to action.
        Assert.Equal(7, view.Split("dash-quick-item", StringSplitOptions.None).Length - 1
            - (view.Split("dash-quick-item--primary", StringSplitOptions.None).Length - 1));
        Assert.Equal(1, view.Split("dash-quick-item--primary", StringSplitOptions.None).Length - 1);
        Assert.Contains("asp-controller=\"Sales\" asp-action=\"Create\"", view);
        Assert.Contains("asp-controller=\"Loading\" asp-action=\"Index\"", view);
        Assert.Contains("asp-controller=\"Payments\" asp-action=\"Index\"", view);
        Assert.Contains("dash-quick-rail", view);
        Assert.Contains("دسترسی سریع", view);

        // The lower dashboard is a scoped 5/3 bento: the in-progress shipment flow and
        // the tank stack. It reuses the shared status and empty state.
        Assert.DoesNotContain("dash-activity-card", insights);
        Assert.DoesNotContain("RecentActivities", insights);
        Assert.Contains("dash-flow-card", insights);
        Assert.Contains("dash-capacity-card", insights);
        Assert.Contains("Model.ActiveShipmentProgress.Take(3)", insights);
        Assert.Contains("class=\"ak-status @item.StatusClass\"", insights);
        Assert.Contains("<partial name=\"_EmptyState\"", insights);
        Assert.Contains("<progress", insights);
        Assert.DoesNotContain("style=", insights);
        Assert.DoesNotContain("<script", insights);

        // Exactly one trend chart, fed entirely from data-* attributes.
        Assert.Equal(1, view.Split("data-dashboard-chart", StringSplitOptions.None).Length - 1);
        Assert.Contains("data-labels=\"@chartLabels\"", view);
        Assert.Contains("data-sales=\"@chartSales\"", view);
        Assert.Contains("data-expenses=\"@chartExpenses\"", view);

        // The only inline style is the mix-legend dot, whose colour comes from the model.
        Assert.Equal(1, view.Split("style=", StringSplitOptions.None).Length - 1);
        Assert.Contains("class=\"dash-dot\" style=\"background:@seg.color\"", view);

        Assert.Contains(".dashboard-page", css);
        Assert.Contains(".dash-quick-item", css);
        Assert.Contains("grid-template-columns: minmax(0, 5fr) minmax(230px, 3fr)", css);
        Assert.Contains(".dash-mix", css);
        Assert.Contains(".dash-trend", css);
        Assert.Contains(".dash-total", css);
        Assert.DoesNotContain(".dashboard-hero", css);
        Assert.DoesNotContain(".dashboard-period", css);
        Assert.DoesNotContain(".dashboard-metric", css);
        Assert.DoesNotContain(".dashboard-activity", css);
        Assert.Contains("max-inline-size: 100%", css);
        Assert.Contains("overflow: visible", css);
        Assert.Contains("@media (max-width: 575.98px)", css);

        Assert.Contains("ptg:page-ready", script);
        Assert.Contains("pageshow", script);
        Assert.Contains("replaceChildren", script);
        Assert.Contains("Intl.NumberFormat", script);
        Assert.Contains("function linePath(points)", script);
        Assert.Contains("dash-chart-line--", script);
        Assert.Contains("dash-chart-area--", script);
        Assert.DoesNotContain("dashboard-radial", script);
    }

    [Fact]
    public void Base_Definition_Tabs_Use_The_Shared_Statistic_Cards()
    {
        var modules = new[]
        {
            "Products",
            "Units",
            "Currencies",
            "DailyFxRates",
            "Locations",
            "ExpenseTypes",
            "ExpenseRules",
            "StorageTanks",
            "Terminals"
        };

        foreach (var module in modules)
        {
            var view = ReadRepoFile($"src/PTGOilSystem.Web/Views/{module}/Index.cshtml");
            var headerPosition = view.IndexOf("_AkPageHeader.cshtml", StringComparison.Ordinal);
            var statisticsPosition = view.IndexOf("ak-stat-grid mb-3", StringComparison.Ordinal);

            Assert.True(headerPosition >= 0, $"{module} must keep the shared page header.");
            Assert.True(statisticsPosition > headerPosition, $"{module} statistics must follow the page header.");
            Assert.Equal(4, view.Split("<vc:stat-card", StringSplitOptions.None).Length - 1);
        }
    }

    [Fact]
    public void Operations_Tabs_Use_The_Shared_Statistic_Cards_With_Avatars()
    {
        var modules = new[]
        {
            "Loading",
            "InventoryTransportLegs",
            "ShipmentPnl",
            "Dispatch",
            "TruckSettlements",
            "Expenses",
            "LossEvents",
            "LoadingReceipts",
            "CustomsDeclarations",
            "Sales"
        };

        foreach (var module in modules)
        {
            var view = ReadRepoFile($"src/PTGOilSystem.Web/Views/{module}/Index.cshtml");

            Assert.Contains("ak-stat-grid mb-3", view);
            Assert.Equal(4, view.Split("<vc:stat-card", StringSplitOptions.None).Length - 1);
            Assert.Equal(4, view.Split(" avatar=\"", StringSplitOptions.None).Length - 1);
        }
    }

    [Fact]
    public void Canonical_Index_Lists_Use_The_Shared_Module_Row_Avatar()
    {
        var tables = ReadRepoFile("src/PTGOilSystem.Web/wwwroot/js/tables.js");
        var components = ReadRepoFile("src/PTGOilSystem.Web/wwwroot/css/ptg/50-ak-components.css");
        var personCell = ReadRepoFile("src/PTGOilSystem.Web/Views/Shared/Partials/_PersonCell.cshtml");
        var modules = new[]
        {
            "accountstatements", "auditlogs", "cashaccounts", "chartofaccounts",
            "closingchecklist", "companies", "contractamendments", "contractbalancetransfers",
            "contractjourney", "contracts", "currencies", "customers", "customsdeclarations",
            "customspermitturnover", "dailyfxrates", "dispatch", "drivers", "employees",
            "expenserules", "expenses", "expensetypes", "finalclose", "fiscalyears",
            "inventory", "inventorytransportlegs", "ledger", "loading", "loadingreceipts",
            "locations", "lossevents", "operationalassets", "partners", "payments",
            "plattsrates", "products", "roles", "sales", "sarrafs", "sarrafsettlements",
            "serviceproviders", "shipmentcontracts", "shipmentpnl", "storagetanks",
            "suppliers", "terminals", "threewaysettlement", "trialclose", "trucks",
            "trucksettlements", "units", "users", "vessels", "wagons"
        };

        Assert.Contains("initializeListRowAvatars();", tables);
        Assert.Contains("document.body.classList.contains(\"action-index\")", tables);
        Assert.Contains(".ptg-person-avatar, .ptg-list-row-avatar, .ak-empty", tables);
        Assert.Contains("ptg-list-entity-cell", tables);
        Assert.Contains("ptg-list-row-avatar", components);
        Assert.Contains("width: 28px", components);
        Assert.Contains("background: #173F73", components);
        Assert.Contains("class=\"ptg-person-avatar\"", personCell);

        foreach (var module in modules)
        {
            Assert.Contains($"{module}:", tables);
        }
    }

    [Theory]
    [InlineData(".ptg-app")]
    [InlineData(".ptg-page-frame")]
    [InlineData(".ptg-sidebar")]
    [InlineData(".ak-page-header")]
    [InlineData(".ak-form-page")]
    [InlineData(".ak-form-section")]
    [InlineData(".ak-detail-toolbar")]
    [InlineData(".ptg-tabs-rail")]
    [InlineData(".ak-status")]
    [InlineData(".ak-row-menu")]
    [InlineData(".ak-form-grid")]
    [InlineData(".ak-table-wrap")]
    [InlineData(".ak-table")]
    [InlineData(".ak-list")]
    [InlineData(".ak-empty")]
    [InlineData(".ak-pager")]
    [InlineData(".ak-modal-content")]
    public void Ptg_Css_Defines_Each_Canonical_Selector_Or_Intentional_Bridge(string selector)
    {
        var css = ReadPtgCss();

        Assert.Contains(selector, css);
    }

    [Fact]
    public void Shared_List_Card_Composes_Existing_List_Nodes_And_Preserves_Hover_Actions()
    {
        var listScript = ReadRepoFile("src/PTGOilSystem.Web/wwwroot/js/list-toolbar-row.js");
        var tablesScript = ReadRepoFile("src/PTGOilSystem.Web/wwwroot/js/tables.js");
        var components = ReadRepoFile("src/PTGOilSystem.Web/wwwroot/css/ptg/50-ak-components.css");
        var listStyles = ReadRepoFile("src/PTGOilSystem.Web/wwwroot/css/ptg/15-system-lists.css");
        var operationsFooter = ReadRepoFile("src/PTGOilSystem.Web/Views/Shared/_OperationsListFooter.cshtml");
        var pagedFooter = ReadRepoFile("src/PTGOilSystem.Web/Views/Shared/_PagedListFooter.cshtml");

        Assert.Contains("assembleListCard(page, toolbar)", listScript);
        Assert.Contains("card.classList.add(\"ak-list-card\")", listScript);
        Assert.Contains("window.PTG.setListCardLoading", listScript);
        Assert.Contains("--ptg-list-radius: 12px", listStyles);
        Assert.Contains(".ak-list-card", listStyles);
        Assert.Contains("height: 54px", listStyles);
        Assert.Contains(".ak-list-footer", listStyles);
        Assert.Contains(".ak-list-loading-state", listStyles);
        Assert.Contains("ak-list-result-count", operationsFooter);
        Assert.Contains("ak-list-result-count", pagedFooter);

        Assert.Contains("initializeContextualRowActions();", tablesScript);
        Assert.Contains("action.source.click();", tablesScript);
        Assert.Contains(".ak-table tbody tr:hover .ak-row-actions", components);
        Assert.Contains(".ak-table tbody tr:focus-within .ak-row-actions", components);
    }

    [Theory]
    [InlineData("src/PTGOilSystem.Web/Views/Home/Index.cshtml", "ak-form-page")]
    [InlineData("src/PTGOilSystem.Web/Views/Products/Index.cshtml", "ak-list-page")]
    [InlineData("src/PTGOilSystem.Web/Views/StorageTanks/Index.cshtml", "ak-list-page")]
    [InlineData("src/PTGOilSystem.Web/Views/Customers/Index.cshtml", "ak-list-page")]
    [InlineData("src/PTGOilSystem.Web/Views/Suppliers/Index.cshtml", "ak-list-page")]
    [InlineData("src/PTGOilSystem.Web/Views/Employees/Index.cshtml", "ak-list-page")]
    [InlineData("src/PTGOilSystem.Web/Views/Contracts/Index.cshtml", "ak-table")]
    [InlineData("src/PTGOilSystem.Web/Views/Contracts/Create.cshtml", "ak-form")]
    [InlineData("src/PTGOilSystem.Web/Views/Loading/Details.cshtml", "ak-form-page")]
    [InlineData("src/PTGOilSystem.Web/Views/LoadingReceipts/_ReceiptCreateForm.cshtml", "ak-form")]
    [InlineData("src/PTGOilSystem.Web/Views/Dispatch/Create.cshtml", "_AkPageHeader")]
    [InlineData("src/PTGOilSystem.Web/Views/Sales/Create.cshtml", "ak-form")]
    [InlineData("src/PTGOilSystem.Web/Views/Expenses/Create.cshtml", "ak-form")]
    [InlineData("src/PTGOilSystem.Web/Views/Payments/Create.cshtml", "ak-form")]
    [InlineData("src/PTGOilSystem.Web/Views/CustomsDeclarations/Create.cshtml", "ak-form")]
    [InlineData("src/PTGOilSystem.Web/Views/CustomsPermitTurnover/Index.cshtml", "ak-list-page")]
    [InlineData("src/PTGOilSystem.Web/Views/ContractAmendments/Index.cshtml", "ak-list-page")]
    [InlineData("src/PTGOilSystem.Web/Views/ContractJourney/Index.cshtml", "ak-list-page")]
    [InlineData("src/PTGOilSystem.Web/Views/Inventory/Index.cshtml", "ak-list-page")]
    [InlineData("src/PTGOilSystem.Web/Views/PlattsRates/Index.cshtml", "ak-list-page")]
    [InlineData("src/PTGOilSystem.Web/Views/ShipmentContracts/Index.cshtml", "ak-list-page")]
    [InlineData("src/PTGOilSystem.Web/Views/AccountStatements/Details.cshtml", "ak-form-page")]
    [InlineData("src/PTGOilSystem.Web/Views/ShipmentPnl/Details.cshtml", "ak-form-page")]
    [InlineData("src/PTGOilSystem.Web/Views/Shared/_CreateModalShell.cshtml", "ak-modal")]
    [InlineData("src/PTGOilSystem.Web/Views/Shared/_EmptyState.cshtml", "ak-empty")]
    [InlineData("src/PTGOilSystem.Web/Views/Shared/_Pagination.cshtml", "ak-pager")]
    public void Razor_View_Uses_The_Current_Ptg_Marker_Or_Intentional_Compat_Bridge(
        string relativePath,
        string expectedMarker)
    {
        var view = ReadRepoFile(relativePath);

        Assert.Contains(expectedMarker, view);
    }

    [Theory]
    [InlineData("~/css/ptg/37-masterdata-cards.css", "src/PTGOilSystem.Web/wwwroot/css/ptg/37-masterdata-cards.css")]
    [InlineData("~/css/design-system/core.css", "src/PTGOilSystem.Web/wwwroot/css/design-system/core.css")]
    [InlineData("~/css/design-system/layout.css", "src/PTGOilSystem.Web/wwwroot/css/design-system/layout.css")]
    [InlineData("~/css/design-system/components.css", "src/PTGOilSystem.Web/wwwroot/css/design-system/components.css")]
    [InlineData("~/css/design-system/pages.css", "src/PTGOilSystem.Web/wwwroot/css/design-system/pages.css")]
    [InlineData("~/css/view-structure.css", "src/PTGOilSystem.Web/wwwroot/css/view-structure.css")]
    public void Legacy_Stylesheet_Is_Not_An_Active_Layout_Dependency(string layoutReference, string relativePath)
    {
        var layout = ReadRepoFile("src/PTGOilSystem.Web/Views/Shared/_Layout.cshtml");
        var modalLayout = ReadRepoFile("src/PTGOilSystem.Web/Views/Shared/_ModalLayout.cshtml");

        Assert.DoesNotContain(layoutReference, layout);
        Assert.DoesNotContain(layoutReference, modalLayout);
        Assert.False(File.Exists(GetRepoPath(relativePath)), $"Legacy stylesheet must not be restored: {relativePath}");
    }

    [Fact]
    public void Excel_Import_Scripts_Reinitialise_After_An_Spa_Page_Swap()
    {
        var spaNav = ReadRepoFile("src/PTGOilSystem.Web/wwwroot/js/spa-nav.js");
        var excelImport = ReadRepoFile("src/PTGOilSystem.Web/wwwroot/js/excel-import.js");
        var customsImport = ReadRepoFile("src/PTGOilSystem.Web/wwwroot/js/customs-import.js");

        // The swap replaces the page DOM and announces only "ptg:page-ready". An uploader that
        // waits for any other signal is never initialised after in-app navigation, so its file
        // picker stays unbound and the start button stays disabled.
        Assert.Contains("dispatchEvent(new CustomEvent(\"ptg:page-ready\"", spaNav);
        Assert.Contains("window.addEventListener('ptg:page-ready'", excelImport);
        Assert.Contains("window.addEventListener(\"ptg:page-ready\"", customsImport);
    }

    [Fact]
    public void Shared_Stat_Cards_Fit_Long_Values_Without_Ellipsis()
    {
        var layout = ReadRepoFile("src/PTGOilSystem.Web/Views/Shared/_Layout.cshtml");
        var component = ReadRepoFile("src/PTGOilSystem.Web/Views/Shared/Components/StatCard/Default.cshtml");
        var script = ReadRepoFile("src/PTGOilSystem.Web/wwwroot/js/stat-card-fit.js");
        var css = ReadRepoFile("src/PTGOilSystem.Web/wwwroot/css/ptg/52-stat-card.css");

        Assert.Contains("~/js/stat-card-fit.js", layout);
        Assert.Contains("data-ak-stat-value", component);
        Assert.Contains("new ResizeObserver", script);
        Assert.Contains("value.clientWidth / value.scrollWidth", script);
        Assert.Contains("window.addEventListener(\"ptg:page-ready\"", script);
        Assert.Contains("text-overflow: clip", css);
    }

    private static string ReadPtgCss()
    {
        var ptgRoot = GetRepoPath("src/PTGOilSystem.Web/wwwroot/css/ptg");

        return string.Join(
            Environment.NewLine,
            Directory.GetFiles(ptgRoot, "*.css")
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .Select(File.ReadAllText));
    }

    private static string ReadRepoFile(string relativePath)
        => File.ReadAllText(GetRepoPath(relativePath));

    /// <summary>Reads a `--token: NNNpx;` custom property out of a CSS file.</summary>
    private static int ReadPixelToken(string css, string token)
    {
        var match = System.Text.RegularExpressions.Regex.Match(css, $@"{token}:\s*(\d+)px");
        Assert.True(match.Success, $"CSS custom property '{token}' with a px value was not found.");
        return int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string GetRepoPath(string relativePath, [CallerFilePath] string sourceFilePath = "")
    {
        var normalizedPath = relativePath.Replace('/', Path.DirectorySeparatorChar);

        foreach (var start in new[]
                 {
                     Environment.CurrentDirectory,
                     AppContext.BaseDirectory,
                     Path.GetDirectoryName(sourceFilePath) ?? string.Empty,
                 })
        {
            var directory = new DirectoryInfo(start);

            while (directory is not null)
            {
                if (Directory.Exists(Path.Combine(directory.FullName, "src", "PTGOilSystem.Web"))
                    && Directory.Exists(Path.Combine(directory.FullName, "tests")))
                {
                    return Path.GetFullPath(Path.Combine(directory.FullName, normalizedPath));
                }

                directory = directory.Parent;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the repository root for ShellViewStructureTests.");
    }
}
