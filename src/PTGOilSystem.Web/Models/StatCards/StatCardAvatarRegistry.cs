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
        ["shipments"]        = "shipments",        // oil tanker vessel
        ["vessel"]           = "vessel",           // oil tanker vessel (alternate angle)
        ["wagon"]            = "wagon",            // rail tank wagon
        ["transported"]      = "transported",      // fuel tanker truck
        ["in-transit"]       = "in-transit",       // tanker truck on a route
        ["loading"]          = "loading",          // crane / loading port
        ["loading-terminal"] = "loading-terminal", // truck at the loading gantry
        ["dispatch-station"] = "dispatch-station", // fuel dispatch / gate station
        ["locations"]        = "locations",        // map pin + route

        // ── Purchase & supply ───────────────────────────────
        ["purchase-contracts"] = "purchase-contracts", // contract doc + pen
        ["contract-signed"]    = "contract-signed",    // signed & sealed contract
        ["purchase-value"]     = "purchase-value",     // business handshake / finance doc
        ["suppliers"]          = "suppliers",          // team of professionals
        ["partners"]           = "partners",           // two business partners
        ["employees"]          = "employees",          // field operator
        ["rules-checklist"]    = "rules-checklist",    // checklist + tools (rules / active state)
        ["equipment"]          = "equipment",          // motor + gear + wrench
        ["security"]           = "security",           // ID card + shield

        // ── Sales & revenue ─────────────────────────────────
        ["sold-quantity"] = "sold-quantity", // transport tanker
        ["sales-value"]   = "sales-value",   // soft banknotes / coins
        ["customers"]     = "customers",     // two managers dealing
        ["analysis"]      = "analysis",      // report + magnifier + growth chart
        ["reports"]       = "reports",       // dashboard screen

        // ── Finance & accounting ────────────────────────────
        ["receivables"]   = "receivables",   // wallet receiving money
        ["receipts"]      = "receipts",      // cash-in receipt
        ["payments"]      = "payments",      // bank cards / payment
        ["payments-out"]  = "payments-out",  // invoice + card + outgoing arrow
        ["cash-flow"]     = "cash-flow",     // coins + banknote + refresh
        ["treasury"]      = "treasury",      // safe + money + growth arrow
        ["profit-loss"]   = "profit-loss",   // calculator / finance chart (net sales − expenses)
        ["statement"]     = "statement",     // financial statement + calculator
        ["ledger"]        = "ledger",        // ledger book + calculator
        ["expenses"]      = "expenses",      // expense receipt / outflow (dedicated, never reused)
        ["expense-types"] = "expense-types", // expense categories doc
        ["bank-accounts"] = "bank-accounts", // bank building + card
        ["exchange-rate"] = "exchange-rate", // USD/EUR conversion + calculator
        ["documents"]     = "documents",     // archive box + magnifier

        // ── Inventory & facilities ──────────────────────────
        ["total-inventory"] = "total-inventory", // oil barrels
        ["storage-tanks"]   = "storage-tanks",   // storage tank group
        ["tank-farm"]       = "tank-farm",       // tank farm / discharge tanks
        ["warehouse"]       = "warehouse",       // warehouse + stock
        ["products"]        = "products",        // barrel + jerrycan
        ["units"]           = "units",           // ruler + caliper
        ["quality"]         = "quality",         // lab test / inspection
        ["refinery"]        = "refinery",        // refinery plant
        ["pipeline"]        = "pipeline",        // pipeline manifold
        ["pump-station"]    = "pump-station",    // pumping station
        ["production"]      = "production",      // pumpjack
        ["losses"]          = "losses",          // barrel + warning

        // ── Legacy keys kept working (aliases to real assets) ─
        ["unloaded"]          = "tank-farm",
        ["unloading"]         = "tank-farm",
        ["tank-inventory"]    = "storage-tanks",
        ["shortage"]          = "losses",
        ["stock-shortage"]    = "losses",
        ["realized-profit"]   = "profit-loss",
        ["sales-contracts"]   = "contract-signed",
        ["purchase-quantity"] = "products",
        ["purchase-requests"] = "rules-checklist",
        ["average-price"]     = "analysis",
        ["cash"]              = "cash-flow",
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
