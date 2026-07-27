namespace PTGOilSystem.Web.Models.StatCards;

/// <summary>
/// Central map from a logical avatar key (e.g. "shipments") to its illustration
/// file under <c>wwwroot/images/stat-cards/</c>. This is the ONLY place that
/// knows illustration filenames, so pages reference concepts, not paths.
///
/// NOTE: the physical asset files are supplied separately (see the Asset Spec).
/// Until a file exists the component keeps the illustration area transparent.
/// </summary>
public static class StatCardAvatarRegistry
{
    /// <summary>Folder (web path) that holds every stat-card illustration.</summary>
    public const string BasePath = "/images/stat-cards/";

    /// <summary>Preferred file extension for the supplied assets.</summary>
    public const string Extension = ".webp";

    /// <summary>
    /// Logical key → base filename (without extension). Grouped to mirror the
    /// reference dashboard sections. Keys are the public contract used in views.
    /// </summary>
    private static readonly Dictionary<string, string> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        // ── Shipment operations ─────────────────────────────
        ["shipments"]        = "ref-blue/ops-loading",        // oil tanker vessel
        ["vessel"]           = "ref-blue/ops-loading",           // oil tanker vessel (alternate angle)
        ["wagon"]            = "ref-icons/wagon",            // rail tank wagon
        ["transported"]      = "ref-blue/ops-transport",      // fuel tanker truck
        ["in-transit"]       = "ref-blue/ops-transport",       // tanker truck on a route
        ["loading"]          = "ref-blue/ops-loading",          // crane / loading port
        ["loading-terminal"] = "ref-blue/ops-loading-receipt", // truck at the loading gantry
        ["dispatch-station"] = "ref-blue/ops-loading-receipt", // fuel dispatch / gate station
        ["locations"]        = "ref-icons/locations",        // map pin + route

        // ── Purchase & supply ───────────────────────────────
        ["purchase-contracts"] = "ref-blue/sale-contracts", // contract doc + pen
        ["contract-signed"]    = "ref-blue/sale-contracts",    // signed & sealed contract
        ["purchase-value"]     = "ref-icons/purchase-value",     // business handshake / finance doc
        ["suppliers"]          = "ref-people/suppliers",          // team of professionals
        ["partners"]           = "ref-people/partners",           // two business partners
        ["employees"]          = "ref-people/employees",          // field operator
        ["rules-checklist"]    = "ref-icons/rules-checklist",    // checklist + tools (rules / active state)
        ["equipment"]          = "ref-people/equipment",          // motor + gear + wrench
        ["security"]           = "ref-icons/security",           // ID card + shield

        // ── Sales & revenue ─────────────────────────────────
        ["sold-quantity"] = "ref-blue/sale-revenue", // transport tanker
        ["sales-value"]   = "ref-blue/sale-revenue",   // soft banknotes / coins
        ["customers"]     = "ref-people/customers",     // two managers dealing
        ["analysis"]      = "ref-icons/analysis",      // report + magnifier + growth chart
        ["reports"]       = "ref-icons/reports",       // dashboard screen

        // ── Finance & accounting ────────────────────────────
        ["receivables"]   = "ref-blue/sale-loan",   // wallet receiving money
        ["receipts"]      = "ref-blue/party-receipt",      // cash-in receipt
        ["payments"]      = "ref-blue/party-payment",      // bank cards / payment
        ["payments-out"]  = "ref-blue/party-payment",  // invoice + card + outgoing arrow
        ["cash-flow"]     = "ref-blue/ops-settlement",     // coins + banknote + refresh
        ["treasury"]      = "ref-blue/ops-settlement",      // safe + money + growth arrow
        ["profit-loss"]   = "ref-icons/profit-loss",   // calculator / finance chart (net sales − expenses)
        ["statement"]     = "ref-blue/party-receipt",     // financial statement + calculator
        ["ledger"]        = "ref-icons/ledger",        // ledger book + calculator
        ["expenses"]      = "ref-blue/sale-expense",      // expense receipt / outflow (dedicated, never reused)
        ["expense-types"] = "ref-blue/sale-expense", // expense categories doc
        ["bank-accounts"] = "ref-icons/bank-accounts", // bank building + card
        ["exchange-rate"] = "ref-people/exchange-rate", // USD/EUR conversion + calculator
        ["documents"]     = "ref-icons/documents",     // archive box + magnifier

        // ── Inventory & facilities ──────────────────────────
        ["total-inventory"] = "ref-blue/ops-stock-transfer", // oil barrels
        ["storage-tanks"]   = "ref-blue/ops-stock-transfer",   // storage tank group
        ["tank-farm"]       = "ref-blue/ops-stock-transfer",       // tank farm / discharge tanks
        ["warehouse"]       = "ref-icons/warehouse",       // warehouse + stock
        ["products"]        = "ref-icons/products",        // barrel + jerrycan
        ["units"]           = "ref-icons/units",           // ruler + caliper
        ["quality"]         = "ref-icons/quality",         // lab test / inspection
        ["refinery"]        = "ref-icons/refinery",        // refinery plant
        ["pipeline"]        = "ref-icons/pipeline",        // pipeline manifold
        ["pump-station"]    = "ref-icons/pump-station",    // pumping station
        ["production"]      = "ref-icons/production",      // pumpjack
        ["losses"]          = "ref-blue/sale-deductions",          // barrel + warning

        // ── Shipment dossier (ShipmentPnl/Details only) ──────
        ["shipment-loaded"]     = "ref-shipment/loaded",     // vessel at the loading arm
        ["shipment-discharged"] = "ref-shipment/discharged", // vessel discharging into the tank farm
        ["shipment-remaining"]  = "ref-shipment/remaining",  // gauge tank showing the level left
        ["shipment-losses"]     = "ref-shipment/losses",     // leaking tank + warning sign
        ["shipment-sales"]      = "ref-shipment/sales",      // vessel + confirmed order basket
        ["shipment-expenses"]   = "ref-shipment/expenses",   // calculator + service costs
        ["shipment-profit"]     = "ref-shipment/profit",     // rising chart + coins
        ["shipment-loss"]       = "ref-shipment/loss",       // falling chart + coins

        // ── Contract dossier (ContractJourney/Details summary) ─
        ["contract-loaded"]   = "ref-contract/contract-loaded",   // barrel + gauge + confirmed check
        ["contract-expenses"] = "ref-contract/contract-expenses", // cost sheet + calculator
        ["contract-sales"]    = "ref-contract/contract-sales",    // tanker truck + handshake
        ["contract-losses"]   = "ref-contract/contract-losses",   // open drum + spilled product
        ["contract-profit"]   = "ref-contract/contract-profit",   // growth chart + net result

        // ── Report pages ────────────────────────────────────
        ["report-inventory"]   = "ref-reports/rep-inventory",   // barrel + stock chart
        ["report-purchase"]    = "ref-reports/rep-purchase",    // truck + tanker + purchase sheet
        ["report-sales"]       = "ref-reports/rep-sales",       // fuel nozzle + sales chart
        ["report-finance"]     = "ref-reports/rep-finance-pnl", // financial statement + coins
        ["report-payments"]    = "ref-reports/rep-payments",    // statement + calculator
        ["report-settlements"] = "ref-reports/rep-settlements", // cleared checklist + shield
        ["report-transport"]   = "ref-reports/rep-transport",   // route map + vessel
        ["report-analysis"]    = "ref-reports/rep-analysis",    // chart under a magnifier

        // ── Reconciliation / mismatch pages ─────────────────
        ["recon-audit"]      = "ref-reconciliation/recon-audit",      // checklists under a magnifier
        ["recon-ledger"]     = "ref-reconciliation/recon-ledger",     // two bank balances that disagree
        ["recon-stock"]      = "ref-reconciliation/recon-stock",      // counted vs recorded cargo
        ["recon-amount"]     = "ref-reconciliation/recon-amount",     // two receipts with different totals
        ["recon-documents"]  = "ref-reconciliation/recon-documents",  // two records in conflict
        ["recon-operations"] = "ref-reconciliation/recon-operations", // open order / transport loop

        // ── Legacy keys kept working (aliases to real assets) ─
        ["unloaded"]          = "ref-blue/ops-stock-transfer",
        ["unloading"]         = "ref-blue/ops-stock-transfer",
        ["tank-inventory"]    = "ref-blue/ops-stock-transfer",
        ["shortage"]          = "ref-blue/sale-deductions",
        ["stock-shortage"]    = "ref-blue/sale-deductions",
        ["realized-profit"]   = "ref-icons/profit-loss",
        ["sales-contracts"]   = "ref-blue/sale-contracts",
        ["purchase-quantity"] = "ref-icons/products",
        ["purchase-requests"] = "ref-icons/rules-checklist",
        ["average-price"]     = "ref-icons/analysis",
        ["cash"]              = "ref-blue/ops-settlement",
    };

    /// <summary>All registered avatar keys (used by the Asset Spec / audits).</summary>
    public static IReadOnlyCollection<string> Keys => Map.Keys;

    /// <summary>
    /// Resolve a web path for the given key, or <c>null</c> when the key is
    /// unknown/blank. Returning null keeps the component visual area transparent.
    /// </summary>
    public static string? ResolvePath(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        return Map.TryGetValue(key, out var file) ? BasePath + file + Extension : null;
    }
}
