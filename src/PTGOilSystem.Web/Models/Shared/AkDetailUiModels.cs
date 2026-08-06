namespace PTGOilSystem.Web.Models.Shared;

/// <summary>
/// One entry of the detail-page header kebab menu or the bottom "next
/// operations" action bar. Either <see cref="Href"/> or
/// <see cref="ModalTarget"/> must be set for the item to render.
/// </summary>
public sealed class AkHeaderMenuItem
{
    public required string Label { get; init; }
    public string? Href { get; init; }

    /// <summary>Optional accessible name when the visible label needs more context.</summary>
    public string? AccessibleLabel { get; init; }

    /// <summary>Bootstrap modal id (without '#') the item opens instead of navigating.</summary>
    public string? ModalTarget { get; init; }

    /// <summary>
    /// When set, the item is a destructive/state-changing POST rendered as a small
    /// antiforgery-protected form inside the kebab (e.g. cancel, delete). Pair with
    /// <see cref="ConfirmMessage"/> so the existing confirm dialog guards it.
    /// </summary>
    public string? PostUrl { get; init; }

    /// <summary>Bootstrap icon class, e.g. "bi-pencil".</summary>
    public string? Icon { get; init; }

    /// <summary>Destructive items render in the danger tone and always live in the kebab.</summary>
    public bool IsDestructive { get; init; }

    /// <summary>Optional title used by the existing PTG confirmation dialog.</summary>
    public string? ConfirmTitle { get; init; }

    /// <summary>
    /// When set, the existing <c>data-ptg-confirm</c> behavior confirms the action.
    /// Modal-backed actions normally keep confirmation inside their modal instead.
    /// </summary>
    public string? ConfirmMessage { get; init; }

    public bool IsRenderable => !string.IsNullOrWhiteSpace(Href)
        || !string.IsNullOrWhiteSpace(ModalTarget)
        || !string.IsNullOrWhiteSpace(PostUrl);
}

/// <summary>One chronological event on a detail page timeline (_DetailTimeline).</summary>
public sealed class AkTimelineItem
{
    public required string Title { get; init; }

    /// <summary>Already-formatted display date (Persian calendar text as produced by the page).</summary>
    public string? Date { get; init; }

    /// <summary>Optional machine-readable timestamp for the HTML time element.</summary>
    public DateTimeOffset? Timestamp { get; init; }

    /// <summary>Bootstrap icon class, e.g. "bi-truck".</summary>
    public string? Icon { get; init; }

    public string? Href { get; init; }

    /// <summary>Optional short metadata line under the title (amount, counterparty, …).</summary>
    public string? Meta { get; init; }

    /// <summary>Visual state: "is-current" highlights, "is-muted" dims; null = normal.</summary>
    public string? State { get; init; }

    public string? StateClass => State switch
    {
        "is-current" => "is-current",
        "is-muted" => "is-muted",
        _ => null
    };
}

/// <summary>
/// One label/value fact rendered by <c>_DetailInfoGrid</c>. Items whose value is
/// blank — or one of the placeholder glyphs the old pages printed instead of a
/// value — are dropped by the partial, so a page never shows a "-" row.
/// </summary>
public sealed class AkInfoItem
{
    public required string Label { get; init; }

    /// <summary>Already-formatted display value produced by the page.</summary>
    public string? Value { get; init; }

    /// <summary>Optional link target; the value renders as an anchor when set.</summary>
    public string? Href { get; init; }

    /// <summary>Optional short unit shown after the value (MT, USD, USD/MT, …).</summary>
    public string? Unit { get; init; }

    /// <summary>Numbers render with the tabular font and LTR isolation.</summary>
    public bool IsNumeric { get; init; }

    /// <summary>
    /// Tone for state-bearing values only: "warning", "danger", "success",
    /// plus the two neutral accents "primary" (identity: party, product) and
    /// "info" (classification: stage, source type, destination).
    /// </summary>
    public string? Tone { get; init; }

    /// <summary>Long free text (notes, route, address) spans the full grid width.</summary>
    public bool IsWide { get; init; }

    private static readonly string[] PlaceholderValues = ["-", "—", "–", "‌-", "N/A", "n/a"];

    public bool HasValue => !string.IsNullOrWhiteSpace(Value)
        && !PlaceholderValues.Contains(Value.Trim());

    public string? ToneClass => Tone switch
    {
        "warning" => "is-warning",
        "danger" => "is-danger",
        "success" => "is-success",
        "primary" => "is-primary",
        "info" => "is-info",
        _ => null
    };
}

/// <summary>
/// One card of the primary KPI strip (<c>_DetailKpiStrip</c>). The strip caps
/// itself at five cards so no page can grow a second metrics wall.
/// </summary>
public sealed class AkKpiItem
{
    public required string Title { get; init; }

    /// <summary>Already-formatted display value produced by the page.</summary>
    public required string Value { get; init; }

    public string? Unit { get; init; }

    /// <summary>Stat-card avatar key, e.g. "loading", "expenses".</summary>
    public string? Avatar { get; init; }

    /// <summary>Stat-card state: "warning", "empty", "loading".</summary>
    public string? State { get; init; }
}

/// <summary>One option of the small display switch in the account card header (USD/RUB).</summary>
public sealed class AkPartyToggleOption
{
    public required string Label { get; init; }
    public required string Href { get; init; }
    public bool IsActive { get; init; }
}

/// <summary>
/// Account status of a counterparty profile summary tab (<c>_AkPartyAccountCard</c>):
/// the plain-language meaning of the balance, an optional settled-share bar, the
/// low-priority facts and the identity reference block — one panel instead of two,
/// so the summary tab carries a single card. The money itself lives in the shared
/// stat cards of <c>_DetailKpiStrip</c> above the card, so no figure is rendered
/// twice. Presentation only — every value arrives pre-formatted from the page.
/// </summary>
public sealed class AkPartyAccountCard
{
    public string Title { get; init; } = "وضعیت حساب";

    /// <summary>
    /// Direction sentence coming from the statement engine or the page fallback —
    /// who owes whom. Never invented by the card.
    /// </summary>
    public required string Meaning { get; init; }

    /// <summary>"danger" (we owe), "success" (they owe us) or null for a settled account.</summary>
    public string? MeaningTone { get; init; }

    /// <summary>Settled share, 0–100. Null hides the bar (no meaningful base amount).</summary>
    public int? SettledPercent { get; init; }

    /// <summary>Caption under the bar, e.g. "۶۳٪ از این مبلغ پرداخت شده است".</summary>
    public string? SettledCaption { get; init; }

    /// <summary>Low-priority facts (counts, last movement date) shown as small chips.</summary>
    public IReadOnlyList<AkInfoItem> Facts { get; init; } = [];

    /// <summary>
    /// Identity reference rows (code, country, contact, address, notes) folded into
    /// the same panel. Empty list hides the block and its sub-heading.
    /// </summary>
    public IReadOnlyList<AkInfoItem> Identity { get; init; } = [];

    /// <summary>Sub-heading of the identity block.</summary>
    public string IdentityTitle { get; init; } = "اطلاعات هویتی";

    /// <summary>Optional display-currency switch rendered in the card header.</summary>
    public IReadOnlyList<AkPartyToggleOption> ToggleOptions { get; init; } = [];

    /// <summary>Accessible name of the display switch, e.g. "ارز نمایش".</summary>
    public string? ToggleLabel { get; init; }

    public string MeaningToneClass => MeaningTone switch
    {
        "danger" => "is-danger",
        "success" => "is-success",
        _ => "is-neutral"
    };
}

/// <summary>One linked related record chip (_RelatedRecords).</summary>
public sealed class AkRelatedRecord
{
    /// <summary>Record type label, e.g. "قرارداد", "سند دفتر کل".</summary>
    public required string TypeLabel { get; init; }

    /// <summary>Business identity, e.g. "PC-1404-017".</summary>
    public required string Label { get; init; }

    public string? Href { get; init; }

    /// <summary>Optional accessible name when the visible labels need more context.</summary>
    public string? AccessibleLabel { get; init; }

    /// <summary>One key figure shown after the label (amount, quantity, count).</summary>
    public string? KeyValue { get; init; }

    /// <summary>Bootstrap icon class.</summary>
    public string? Icon { get; init; }
}
