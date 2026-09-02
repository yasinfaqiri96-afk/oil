using System.Text;

namespace PTGOilSystem.Web.Tests.Simulation;

public enum FindingSeverity
{
    P0 = 0,
    P1 = 1,
    P2 = 2,
    P3 = 3,
    P4 = 4
}

public sealed record SimulationFinding(
    string Id,
    FindingSeverity Severity,
    string Module,
    string Title,
    string Evidence,
    bool Confirmed);

/// <summary>
/// یافته‌های شبیه‌سازی را جمع می‌کند و کنارِ خروجی تست در یک فایل markdown می‌نویسد،
/// تا گزارش نهایی از «اجرای واقعی» ساخته شود نه از خواندنِ کد.
/// </summary>
public sealed class SimulationFindingLog
{
    private readonly List<SimulationFinding> _findings = [];
    private readonly List<string> _facts = [];

    public IReadOnlyList<SimulationFinding> Findings => _findings;

    public void Add(
        string id,
        FindingSeverity severity,
        string module,
        string title,
        string evidence,
        bool confirmed = true)
        => _findings.Add(new SimulationFinding(id, severity, module, title, evidence, confirmed));

    public void Fact(string line) => _facts.Add(line);

    public string Render(string heading)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {heading}");
        sb.AppendLine();
        sb.AppendLine($"Generated (UTC): {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine();

        if (_facts.Count > 0)
        {
            sb.AppendLine("## Measured facts");
            sb.AppendLine();
            foreach (var fact in _facts)
                sb.AppendLine($"- {fact}");
            sb.AppendLine();
        }

        sb.AppendLine("## Findings");
        sb.AppendLine();
        if (_findings.Count == 0)
        {
            sb.AppendLine("_No invariant violation detected in this run._");
            return sb.ToString();
        }

        foreach (var group in _findings.GroupBy(f => f.Severity).OrderBy(g => g.Key))
        {
            sb.AppendLine($"### {group.Key}");
            sb.AppendLine();
            foreach (var finding in group)
            {
                sb.AppendLine($"- **{finding.Id}** [{(finding.Confirmed ? "CONFIRMED" : "POTENTIAL")}] " +
                              $"({finding.Module}) {finding.Title}");
                sb.AppendLine($"  - {finding.Evidence.Replace("\n", "\n  - ")}");
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }

    public string WriteToDisk(string fileName, string heading)
    {
        var directory = Environment.GetEnvironmentVariable("PTG_SIM_REPORT_DIR");
        if (string.IsNullOrWhiteSpace(directory))
            directory = Path.Combine(Path.GetTempPath(), "ptg-simulation");

        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileName);
        File.WriteAllText(path, Render(heading), Encoding.UTF8);
        return path;
    }
}
