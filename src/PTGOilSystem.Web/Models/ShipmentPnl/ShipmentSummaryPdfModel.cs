namespace PTGOilSystem.Web.Models.ShipmentPnl;

/// <summary>Visual tone only; it never changes shipment, stock or finance values.</summary>
public enum ShipmentSummaryPdfTone
{
    Neutral,
    Positive,
    Negative,
    Warning
}

public sealed record ShipmentSummaryPdfMetric(
    string Label,
    string Value,
    string? Unit = null,
    ShipmentSummaryPdfTone Tone = ShipmentSummaryPdfTone.Neutral);

public sealed record ShipmentSummaryPdfStage(
    int Number,
    string Title,
    string StatusText,
    ShipmentSummaryPdfTone Tone,
    IReadOnlyList<ShipmentSummaryPdfMetric> Metrics);

/// <summary>
/// Presentation-ready data for the shipment executive PDF. Every value comes from the
/// already-built details view model; the PDF document only lays it out.
/// </summary>
public sealed class ShipmentSummaryPdfModel
{
    public string FileNameStem { get; init; } = "PTG_Shipment_Summary";
    public string VesselName { get; init; } = string.Empty;
    public string ShipmentCode { get; init; } = string.Empty;
    public string ProductName { get; init; } = string.Empty;
    public string ContractNumber { get; init; } = string.Empty;
    public string StatusText { get; init; } = string.Empty;
    public ShipmentSummaryPdfTone StatusTone { get; init; }
    public string CompanyName { get; init; } = string.Empty;
    public DateTime GeneratedAt { get; init; }
    public string Origin { get; init; } = string.Empty;
    public string Destination { get; init; } = string.Empty;
    public string DepartureDateText { get; init; } = string.Empty;
    public string ArrivalDateText { get; init; } = string.Empty;
    public IReadOnlyList<ShipmentSummaryPdfStage> Stages { get; init; } = [];
}
