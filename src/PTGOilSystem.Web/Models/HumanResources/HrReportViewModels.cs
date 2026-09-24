namespace PTGOilSystem.Web.Models.HumanResources;

public sealed record HrReportTable(string[] Headers, IReadOnlyList<string?[]> Rows, string? Summary);

public sealed class HrReportViewModel
{
    public string Report { get; init; } = "employees";
    public int Year { get; init; }
    public int Month { get; init; }
    public DateTime From { get; init; }
    public DateTime To { get; init; }
    public HrReportTable Table { get; init; } = new([], [], null);
    public IReadOnlyList<(string Key, string Label)> Reports { get; init; } = [];
}
