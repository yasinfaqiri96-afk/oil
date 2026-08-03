namespace PTGOilSystem.Web.Models.ShipmentPnl;

/// <summary>Visual tone only; it never changes shipment, stock or finance values.</summary>
public enum ShipmentSummaryPdfTone
{
    Neutral,
    Positive,
    Negative,
    Warning
}

/// <summary>
/// یک سطر جدول خلاصه. ستون‌ها دقیقاً همان ستون‌های خروجی اکسلِ همین تب هستند
/// (شرح، مقدار MT، مبلغ USD، جزئیات) تا PDF و اکسل یک آمار را نشان دهند.
/// </summary>
public sealed record ShipmentSummaryPdfRow(
    string Label,
    string QuantityText,
    string AmountText,
    string DetailText,
    ShipmentSummaryPdfTone Tone = ShipmentSummaryPdfTone.Neutral,
    bool IsTotal = false);

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
    public string CompanyName { get; init; } = string.Empty;
    public DateTime GeneratedAt { get; init; }
    public string Origin { get; init; } = string.Empty;
    public string Destination { get; init; } = string.Empty;
    public string DepartureDateText { get; init; } = string.Empty;
    public string ArrivalDateText { get; init; } = string.Empty;

    /// <summary>سطرهای خلاصه، برگرفته از همان سند خروجی اکسلِ تب خلاصه.</summary>
    public IReadOnlyList<ShipmentSummaryPdfRow> Rows { get; init; } = [];
}
