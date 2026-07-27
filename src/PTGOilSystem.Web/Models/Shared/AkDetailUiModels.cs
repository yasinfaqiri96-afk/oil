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

    /// <summary>Tone for state-bearing values only: "warning", "danger", "success".</summary>
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
