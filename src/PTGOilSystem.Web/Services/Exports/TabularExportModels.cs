using System.Globalization;

namespace PTGOilSystem.Web.Services.Exports;

public enum TabularExportFormat
{
    Excel,
    Pdf
}

public enum TabularExportValueType
{
    Text,
    Integer,
    Number,
    Percentage,
    Date,
    DateTime,
    Boolean
}

public sealed record TabularExportColumn(
    string TitleFa,
    string TitleEn,
    TabularExportValueType ValueType = TabularExportValueType.Text,
    double Width = 16,
    bool Wrap = false);

public sealed record TabularExportFilter(string LabelFa, string LabelEn, string? Value);

public sealed record TabularExportCell(TabularExportValueType ValueType, object? Value)
{
    public static TabularExportCell Text(string? value) => new(TabularExportValueType.Text, value);
    public static TabularExportCell Integer(long? value) => new(TabularExportValueType.Integer, value);
    public static TabularExportCell Number(decimal? value) => new(TabularExportValueType.Number, value);
    public static TabularExportCell Percentage(decimal? value) => new(TabularExportValueType.Percentage, value);
    public static TabularExportCell Date(DateTime? value) => new(TabularExportValueType.Date, value);
    public static TabularExportCell DateTime(DateTime? value) => new(TabularExportValueType.DateTime, value);
    public static TabularExportCell Boolean(bool? value) => new(TabularExportValueType.Boolean, value);

    public string ToDisplayText(bool isEnglish)
    {
        if (Value is null)
        {
            return string.Empty;
        }

        return ValueType switch
        {
            TabularExportValueType.Date when Value is DateTime date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            TabularExportValueType.DateTime when Value is DateTime dateTime => dateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
            TabularExportValueType.Integer => Convert.ToInt64(Value, CultureInfo.InvariantCulture).ToString("N0", CultureInfo.InvariantCulture),
            TabularExportValueType.Number => Convert.ToDecimal(Value, CultureInfo.InvariantCulture).ToString("N2", CultureInfo.InvariantCulture),
            TabularExportValueType.Percentage => Convert.ToDecimal(Value, CultureInfo.InvariantCulture).ToString("P2", CultureInfo.InvariantCulture),
            TabularExportValueType.Boolean => Convert.ToBoolean(Value, CultureInfo.InvariantCulture)
                ? (isEnglish ? "Yes" : "بلی")
                : (isEnglish ? "No" : "نخیر"),
            _ => Convert.ToString(Value, CultureInfo.InvariantCulture) ?? string.Empty
        };
    }
}

public sealed record TabularExportRow(IReadOnlyList<TabularExportCell> Cells);

public sealed class TabularExportDocument
{
    public required string FileNameStem { get; init; }
    public required string TitleFa { get; init; }
    public required string TitleEn { get; init; }
    public required IReadOnlyList<TabularExportColumn> Columns { get; init; }
    public required IEnumerable<TabularExportRow> Rows { get; init; }

    /// <summary>
    /// منبع سطرِ صفحه‌به‌صفحه برای گزارش‌های حجیم. اگر مقدار داشته باشد به‌جای
    /// <see cref="Rows"/> خوانده می‌شود و نویسندهٔ Excel سطرها را همان‌طور که می‌رسند
    /// می‌نویسد؛ بنابراین هیچ‌وقت کل نتیجه در حافظه جمع نمی‌شود.
    /// </summary>
    public IAsyncEnumerable<TabularExportRow>? RowsAsync { get; init; }

    public IReadOnlyList<TabularExportFilter> Filters { get; init; } = [];
    public TabularExportRow? Totals { get; init; }
    public int? KnownRowCount { get; init; }
    public bool ForceLandscape { get; init; }

    /// <summary>منبع واحدِ سطرها؛ هر دو حالت همگام و ناهمگام از همین‌جا خوانده می‌شوند.</summary>
    public async IAsyncEnumerable<TabularExportRow> EnumerateRowsAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (RowsAsync is not null)
        {
            await foreach (var row in RowsAsync.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                yield return row;
            }
            yield break;
        }

        foreach (var row in Rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return row;
        }
    }
}

public sealed record ExportMenuModel(
    string Action,
    string? Controller = null,
    IReadOnlyDictionary<string, string?>? RouteValues = null);

