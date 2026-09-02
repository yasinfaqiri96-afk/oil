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

    /// <summary>
    /// Tone class for the linear metric row (<c>_DetailOverview</c>). Only the
    /// meaning-bearing states get a colour; "empty"/"loading" stay neutral so a
    /// placeholder never reads as a real warning.
    /// </summary>
    public string? StateClass => State switch
    {
        "warning" => "is-warning",
        "danger" => "is-danger",
        "success" => "is-success",
        // Neutral emphasis for the total/decisive column of a card metric row.
        "accent" => "is-accent",
        _ => null
    };
}

/// <summary>
/// Presentation-only identity block shared by Operations detail pages. Values
/// are already formatted by the Razor page; the component never derives or
/// changes business data.
/// </summary>
public sealed class AkDetailHeroModel
{
    public required string Title { get; init; }
    public string? Eyebrow { get; init; }
    public string? Meta { get; init; }
    public string Icon { get; init; } = "bi-file-earmark-text";
    public string? Status { get; init; }
    public string? StatusState { get; init; }
    public IReadOnlyList<AkInfoItem> Items { get; init; } = [];
}

/// <summary>Single-surface overview used by the linear Operations detail shell.</summary>
public sealed class AkDetailOverviewModel
{
    public required string AriaLabel { get; init; }
    public string? Title { get; init; }
    public IReadOnlyList<AkKpiItem> Metrics { get; init; } = [];
    public IReadOnlyList<AkInfoItem> Facts { get; init; } = [];
    public string? VisualAvatar { get; init; }
    public string? VisualTitle { get; init; }
    public string? VisualMeta { get; init; }
}

/// <summary>One compact operational or financial row in a detail page.</summary>
public sealed class AkDetailActivityRow
{
    public required string Label { get; init; }
    public string? Value { get; init; }
    public string? Unit { get; init; }
    public string? Meta { get; init; }
    public string? Status { get; init; }
    public string? StatusTone { get; init; }
    public string? Href { get; init; }
    public string? Icon { get; init; }

    public bool HasValue => !string.IsNullOrWhiteSpace(Value)
        || !string.IsNullOrWhiteSpace(Meta)
        || !string.IsNullOrWhiteSpace(Status);

    public string? StatusClass => StatusTone switch
    {
        "success" => "is-success",
        "warning" => "is-warning",
        "danger" => "is-danger",
        _ => null
    };
}

/// <summary>Presentation-only secondary material rendered after the main flow.</summary>
public sealed class AkDetailSecondaryModel
{
    public IReadOnlyList<AkTimelineItem> Timeline { get; init; } = [];
    public IReadOnlyList<AkRelatedRecord> Related { get; init; } = [];
    public IReadOnlyList<AkInfoItem> Technical { get; init; } = [];
    public string? TimelineTitle { get; init; }
    public int? TimelineLimit { get; init; }
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

    /// <summary>
    /// Opt-in: fold the settled bar, the fact chips and the identity rows into a
    /// single collapsed «مشاهده بیشتر» panel, so the summary tab opens on the
    /// direction sentence alone. Off keeps the original layout of every other page.
    /// </summary>
    public bool CollapseSecondary { get; init; }

    /// <summary>Sub-heading of that collapsed panel.</summary>
    public string MoreTitle { get; init; } = "جزئیات بیشتر";

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

/// <summary>
/// Record-detail card composition (_DetailCards): one identity strip, then a
/// grid of small cards. Pages keep owning their own facts and formatting; this
/// model only says which fact belongs to which card.
/// </summary>
public sealed class AkDetailCardsModel
{
    /// <summary>Facts of the horizontal identity strip, after the record code and status.</summary>
    public IReadOnlyList<AkInfoItem> StripItems { get; init; } = [];

    /// <summary>Record code shown first in the strip, e.g. "TR-0247".</summary>
    public string? Code { get; init; }

    public string? CodeLabel { get; init; }

    public string? Status { get; init; }

    /// <summary>Status pill state: "is-active", "is-inactive", "is-warning", "is-danger".</summary>
    public string? StatusState { get; init; }

    /// <summary>Primary page action of the strip (usually "edit").</summary>
    public string? PrimaryLabel { get; init; }

    public string? PrimaryHref { get; init; }

    public string? PrimaryIcon { get; init; }

    public IReadOnlyList<AkDetailCard> Cards { get; init; } = [];
}

/// <summary>
/// One card of the detail grid. A card renders whichever blocks the page
/// filled, in this order: rows, route nodes, stage rail, numeric columns,
/// footer meta, free text.
/// </summary>
public sealed class AkDetailCard
{
    public required string Title { get; init; }

    public string Icon { get; init; } = "bi-info-circle";

    /// <summary>Label / value lines separated by a divider.</summary>
    public IReadOnlyList<AkInfoItem> Rows { get; init; } = [];

    /// <summary>Source / destination pair shown above the stage rail.</summary>
    public IReadOnlyList<AkInfoItem> RouteNodes { get; init; } = [];

    /// <summary>Stage rail of the record's own status cycle.</summary>
    public IReadOnlyList<AkDetailStep> Steps { get; init; } = [];

    /// <summary>Numeric columns, label over value.</summary>
    public IReadOnlyList<AkKpiItem> Metrics { get; init; } = [];

    /// <summary>Compact meta line at the bottom of the card (dates, references).</summary>
    public IReadOnlyList<AkInfoItem> Footer { get; init; } = [];

    /// <summary>Free text (notes, description) under the card content.</summary>
    public string? Text { get; init; }

    /// <summary>The card spans the whole grid width.</summary>
    public bool IsWide { get; init; }

    public bool HasContent
        => Rows.Any(item => item.HasValue)
        || RouteNodes.Any(item => item.HasValue)
        || Steps.Count > 0
        || Metrics.Count > 0
        || Footer.Any(item => item.HasValue)
        || !string.IsNullOrWhiteSpace(Text);
}

/// <summary>One stage of a record's status rail.</summary>
public sealed class AkDetailStep
{
    public required string Label { get; init; }

    /// <summary>"is-done", "is-current" or "is-pending".</summary>
    public string State { get; init; } = "is-pending";

    public bool IsDone => string.Equals(State, "is-done", StringComparison.Ordinal);
}

/// <summary>
/// Secondary material of a card-layout detail page, collapsed behind one
/// "show more" toggle: activity summary, history/related/technical and the
/// next-operations bar. Nothing is dropped from the page — only folded away.
/// </summary>
public sealed class AkDetailMoreModel
{
    public IReadOnlyList<AkDetailActivityRow> Activity { get; init; } = [];

    public AkDetailSecondaryModel? Secondary { get; init; }

    /// <summary>
    /// Opt-in: present the supplied activity summary and timeline as one
    /// coherent record journey inside the disclosure.
    /// </summary>
    public bool CombineActivityAndHistory { get; init; }

    /// <summary>Heading of the combined status/history card.</summary>
    public string? JourneyTitle { get; init; }

    /// <summary>Short explanation shown under the combined-card heading.</summary>
    public string? JourneyDescription { get; init; }

    public IReadOnlyList<AkHeaderMenuItem> NextActions { get; init; } = [];

    public string? Label { get; init; }

    /// <summary>
    /// Optional page-specific block folded into the same disclosure, drawn before
    /// the shared activity/history material. It lets one page hide a section of its
    /// own behind this toggle without duplicating the disclosure markup, and stays
    /// null on every page that does not need it.
    /// </summary>
    public Func<object, Microsoft.AspNetCore.Html.IHtmlContent>? ExtraSection { get; init; }

    public bool HasContent
        => Activity.Any(row => row.HasValue)
        || ExtraSection is not null
        || NextActions.Any(action => action.IsRenderable && !action.IsDestructive)
        || (Secondary is not null
            && (Secondary.Timeline.Count > 0
                || Secondary.Related.Count > 0
                || Secondary.Technical.Any(item => item.HasValue)));
}
