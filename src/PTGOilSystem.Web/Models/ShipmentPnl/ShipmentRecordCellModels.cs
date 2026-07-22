namespace PTGOilSystem.Web.Models.ShipmentPnl;

/// <summary>Semantic tone of a record-list status badge. Geometry never changes.</summary>
public enum ShipmentRecordTone
{
    Neutral,
    Success,
    Info,
    Warning,
    Danger
}

/// <summary>
/// Typed presentation model for the entity cell (customer, trip, receipt …) of
/// the shipment record list. Links, permissions and formatting stay with the
/// caller; this only carries what the shared cell needs to draw.
/// </summary>
/// <param name="Primary">Main line (entity name).</param>
/// <param name="Secondary">Quiet second line (invoice/reference code).</param>
/// <param name="AvatarText">Initial(s) for the round avatar; null hides the avatar.</param>
/// <param name="AvatarIcon">Bootstrap-icons class used instead of initials.</param>
/// <param name="Href">Optional link for the primary line.</param>
/// <param name="SecondaryLtr">Render the secondary line left-to-right (codes).</param>
public sealed record ShipmentRecordEntityCell(
    string Primary,
    string? Secondary = null,
    string? AvatarText = null,
    string? AvatarIcon = null,
    string? Href = null,
    bool SecondaryLtr = false);

/// <summary>Status badge of a record row.</summary>
public sealed record ShipmentRecordStatusCell(string Text, ShipmentRecordTone Tone = ShipmentRecordTone.Neutral);

/// <summary>Empty state rendered inside the shared table body.</summary>
public sealed record ShipmentRecordEmptyCell(int ColumnCount, string Text, string Icon = "bi-inbox");

public static class ShipmentRecordToneExtensions
{
    public static string ToCssClass(this ShipmentRecordTone tone) => tone switch
    {
        ShipmentRecordTone.Success => "is-success",
        ShipmentRecordTone.Info => "is-info",
        ShipmentRecordTone.Warning => "is-warning",
        ShipmentRecordTone.Danger => "is-danger",
        _ => "is-neutral"
    };
}
