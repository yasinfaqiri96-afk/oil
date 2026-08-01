namespace PTGOilSystem.Web.Models.ContractJourney;

/// <summary>رنگ/لحن نمایشی یک عدد یا مرحله در PDF خلاصهٔ گشت قرارداد.</summary>
public enum ContractJourneySummaryPdfTone
{
    Neutral,
    Positive,
    Negative,
    Warning
}

/// <summary>
/// یک قلم «عنوان + عدد». عدد همیشه با فرمت مرکزی اعداد سیستم ساخته می‌شود و واحد
/// جدا نگه داشته می‌شود تا ستون اعداد در PDF هم‌تراز و خوانا بماند.
/// </summary>
public sealed record ContractJourneySummaryPdfLine(
    string Label,
    string Value,
    string? Unit = null,
    string? Note = null,
    ContractJourneySummaryPdfTone Tone = ContractJourneySummaryPdfTone.Neutral);

public sealed record ContractJourneySummaryPdfMetric(
    string Label,
    string Value,
    string? Unit = null,
    ContractJourneySummaryPdfTone Tone = ContractJourneySummaryPdfTone.Neutral,
    string? Detail = null);

/// <summary>یک مرحله از «چرخه قرارداد» — همان مراحل تب خلاصه با همان آمارها.</summary>
public sealed record ContractJourneySummaryPdfStage(
    int Number,
    string Title,
    string StatusText,
    ContractJourneySummaryPdfTone Tone,
    IReadOnlyList<ContractJourneySummaryPdfLine> Metrics);

public sealed record ContractJourneySummaryPdfSection(
    string Title,
    IReadOnlyList<ContractJourneySummaryPdfLine> Lines);

/// <summary>
/// دادهٔ آمادهٔ نمایش برای PDF خلاصهٔ گشت قرارداد. همهٔ اعداد و متن‌ها در کنترلر و از
/// همان مقادیر تب خلاصه ساخته می‌شوند؛ سند PDF فقط چیدمان می‌کند و محاسبه‌ای ندارد.
/// </summary>
public sealed class ContractJourneySummaryPdfModel
{
    public string FileNameStem { get; init; } = "PTG_Contract_Journey";
    public string DocumentTitle { get; init; } = string.Empty;
    public string JourneyName { get; init; } = string.Empty;
    public string JourneySubtitle { get; init; } = string.Empty;
    public string StatusText { get; init; } = string.Empty;
    public ContractJourneySummaryPdfTone StatusTone { get; init; } = ContractJourneySummaryPdfTone.Neutral;
    public string CompanyName { get; init; } = string.Empty;
    public DateTime GeneratedAt { get; init; }
    public IReadOnlyList<ContractJourneySummaryPdfMetric> HeadlineMetrics { get; init; } = [];
    public IReadOnlyList<ContractJourneySummaryPdfLine> ContractInfo { get; init; } = [];
    public IReadOnlyList<ContractJourneySummaryPdfLine> PartyInfo { get; init; } = [];
    public IReadOnlyList<ContractJourneySummaryPdfStage> Stages { get; init; } = [];
    public IReadOnlyList<ContractJourneySummaryPdfSection> Sections { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public string? Note { get; init; }
}
