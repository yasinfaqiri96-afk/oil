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
