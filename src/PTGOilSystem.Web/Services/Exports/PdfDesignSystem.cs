using System.Globalization;
using QuestPDF.Drawing;
using QuestPDF.Fluent;
using QuestPDF.Infrastructure;

namespace PTGOilSystem.Web.Services.Exports;

internal sealed record PdfSummaryMetric(
    string Label,
    string Value,
    string Color,
    string? Detail = null);

internal sealed record PdfBrandHeader(
    string CompanyName,
    string? LogoPath,
    string? Phone,
    string? Email,
    string? Website);

internal static class PdfDesignSystem
{
    private static readonly TimeZoneInfo AfghanistanTimeZone = ResolveAfghanistanTimeZone();

    public const string PrimaryFont = "Tahoma";
    public const string PersianFallbackFont = "Vazirmatn";
    public const string EnglishFallbackFont = "Poppins";

    public const string Ink = "#000000";
    public const string Muted = "#667085";
    public const string StrongRule = "#333333";
    public const string SummaryBackground = "#F5F5F5";
    public const string HeaderBackground = "#F1F5F9";
    public const string Border = "#E2E8F0";
    public const string AlternateRowBackground = "#FAFBFC";
    public const string TotalsBackground = "#F1F5F9";
    public const string Positive = "#059669";
    public const string Negative = "#DC2626";
    public const string Balance = "#059669";

    public const float HorizontalMargin = 35.25f;
    public const float TopMargin = 36.5f;
    public const float BottomMargin = 14.5f;
    public const float TitleSize = 10.5f;
    public const float MetaSize = 8.25f;
    public const float TableSize = 7.5f;
    public const float DenseTableSize = 7f;
    public const float ExtraWideTableSize = 6.8f;
    public const float ReportMetaSize = 7f;
    public const float FooterSize = 7.99f;
    public const float BrandLogoWidth = 128f;
    public const float BrandRowHeight = 42f;
    public const float BrandContactSize = 5.75f;

    public static TextStyle DefaultTextStyle(TextStyle style, bool isEnglish)
        => style
            .FontFamily(
                PrimaryFont,
                isEnglish ? EnglishFallbackFont : PersianFallbackFont)
            .FontSize(TableSize)
            .FontColor(Ink)
            .LineHeight(1.15f);

    public static void RegisterReferenceFonts(string webRootPath)
    {
        var candidates = new List<string>
        {
            Path.Combine(webRootPath, "vendor", "fonts", "tahoma", "files", "tahoma.ttf"),
            Path.Combine(webRootPath, "vendor", "fonts", "tahoma", "files", "tahomabd.ttf")
        };

        if (OperatingSystem.IsWindows())
        {
            var windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (!string.IsNullOrWhiteSpace(windowsDirectory))
            {
                candidates.Add(Path.Combine(windowsDirectory, "Fonts", "tahoma.ttf"));
                candidates.Add(Path.Combine(windowsDirectory, "Fonts", "tahomabd.ttf"));
            }
        }

        foreach (var path in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(path))
            {
                continue;
            }

            using var stream = File.OpenRead(path);
            FontManager.RegisterFont(stream);
        }
    }

    public static void ComposeReportHeader(
        IContainer container,
        string title,
        DateTime generatedAt,
        string? filters,
        IReadOnlyList<PdfSummaryMetric> metrics,
        bool isEnglish,
        PdfBrandHeader? brand = null,
        bool compact = false)
    {
        var content = isEnglish
            ? container.ContentFromLeftToRight()
            : container.ContentFromRightToLeft();

        content.Column(column =>
        {
            if (brand is not null)
            {
                column.Item().Element(brandContainer => ComposeBrandHeader(brandContainer, brand));
            }

            column.Item().PaddingTop(compact ? 6 : 7).ContentFromLeftToRight().Row(row =>
            {
                if (isEnglish)
                {
                    row.RelativeItem(1.4f).AlignMiddle().AlignLeft().Text(title)
                        .Bold().FontSize(TitleSize).FontColor(Ink);
                    row.RelativeItem().AlignMiddle().AlignRight().Text(FormatPrintDate(generatedAt, true))
                        .FontSize(ReportMetaSize).FontColor(Muted);
                }
                else
                {
                    row.RelativeItem().AlignMiddle().AlignLeft().ContentFromRightToLeft()
                        .Text(FormatPrintDate(generatedAt, false))
                        .FontSize(ReportMetaSize).FontColor(Muted);
                    row.RelativeItem(1.4f).AlignMiddle().AlignRight().ContentFromRightToLeft()
                        .Text(title).Bold().FontSize(TitleSize).FontColor(Ink);
                }
            });

            if (!compact && !string.IsNullOrWhiteSpace(filters))
            {
                column.Item().PaddingTop(2).AlignRight().Text(filters)
                    .FontSize(ReportMetaSize).FontColor(Muted);
            }

            column.Item().PaddingTop(compact ? 4 : 6).Height(0.8f).Background(StrongRule);

            if (!compact && metrics.Count > 0)
            {
                column.Item().PaddingTop(8).Element(metricContainer =>
                    ComposeSummaryStrip(metricContainer, metrics, isEnglish));
            }
        });
    }

    public static void ComposeBrandHeader(IContainer container, PdfBrandHeader brand)
    {
        container.ContentFromLeftToRight().Column(column =>
        {
            column.Item().Height(BrandRowHeight).Row(row =>
            {
                if (!string.IsNullOrWhiteSpace(brand.LogoPath) && File.Exists(brand.LogoPath))
                {
                    row.ConstantItem(BrandLogoWidth)
                        .AlignMiddle()
                        .AlignLeft()
                        .Image(brand.LogoPath)
                        .FitArea();
                }
                else
                {
                    row.ConstantItem(BrandLogoWidth)
                        .AlignMiddle()
                        .AlignLeft()
                        .Text(brand.CompanyName)
                        .Bold()
                        .FontSize(10)
                        .FontColor(Ink);
                }

                var contacts = new[] { brand.Phone, brand.Email, brand.Website }
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Select(value => value!.Trim())
                    .ToArray();

                row.RelativeItem()
                    .AlignMiddle()
                    .AlignRight()
                    .Text(string.Join("  •  ", contacts))
                    .FontSize(BrandContactSize)
                    .FontColor(Muted);
            });

            column.Item().PaddingTop(8).Height(1.35f).Row(rule =>
            {
                rule.RelativeItem(4.4f).Background(Positive);
                rule.RelativeItem().Background(Negative);
            });
        });
    }

    public static void ComposeSummaryStrip(
        IContainer container,
        IReadOnlyList<PdfSummaryMetric> metrics,
        bool isEnglish)
    {
        var content = isEnglish
            ? container.ContentFromLeftToRight()
            : container.ContentFromRightToLeft();

        content
            .Background(SummaryBackground)
            .CornerRadius(7.5f)
            .PaddingVertical(7.25f)
            .PaddingHorizontal(8)
            .Row(row =>
            {
                foreach (var metric in metrics)
                {
                    row.RelativeItem().AlignMiddle().AlignCenter().Column(column =>
                    {
                        column.Item().AlignCenter().Text(metric.Label)
                            .FontSize(MetaSize).FontColor(Ink);
                        column.Item().PaddingTop(1).AlignCenter().ContentFromLeftToRight()
                            .Text(metric.Value).FontSize(MetaSize).FontColor(metric.Color);
                        if (!string.IsNullOrWhiteSpace(metric.Detail))
                        {
                            column.Item().PaddingTop(1).AlignCenter().Text(metric.Detail)
                                .FontSize(6.5f).FontColor(Muted);
                        }
                    });
                }
            });
    }

    public static IContainer HeaderCell(IContainer container, bool dense = false)
        => container
            .Background(HeaderBackground)
            .Border(0.55f)
            .BorderColor(Border)
            .PaddingVertical(dense ? 4.5f : 6.55f)
            .PaddingHorizontal(dense ? 2.5f : 4)
            .AlignMiddle();

    public static IContainer BodyCell(IContainer container)
        => BodyCell(container, "#FFFFFF", false);

    public static IContainer BodyCell(IContainer container, string background)
        => BodyCell(container, background, false);

    public static IContainer BodyCell(IContainer container, string background, bool dense)
        => container
            .Background(background)
            .Border(0.45f)
            .BorderColor(Border)
            .PaddingVertical(dense ? 3.75f : 5.05f)
            .PaddingHorizontal(dense ? 2.5f : 4)
            .AlignMiddle();

    public static IContainer TotalCell(IContainer container, bool dense = false)
        => container
            .Background(TotalsBackground)
            .Border(0.55f)
            .BorderColor(Border)
            .PaddingVertical(dense ? 4 : 5)
            .PaddingHorizontal(dense ? 2.5f : 4)
            .AlignMiddle();

    public static float TableFontSize(int columnCount)
        => columnCount switch
        {
            <= 6 => TableSize,
            <= 12 => DenseTableSize,
            _ => ExtraWideTableSize
        };

    public static float ColumnWeight(TabularExportColumn column)
    {
        var minimum = column.ValueType switch
        {
            TabularExportValueType.Date => 12D,
            TabularExportValueType.DateTime => 16D,
            TabularExportValueType.Integer => 10D,
            TabularExportValueType.Number => 12D,
            TabularExportValueType.Percentage => 11D,
            TabularExportValueType.Boolean => 9D,
            _ => 10D
        };
        var maximum = column.Wrap ? 34D : 26D;
        return (float)Math.Clamp(column.Width, minimum, maximum);
    }

    public static void ComposeFooter(
        IContainer container,
        string company,
        bool isEnglish)
    {
        container.ContentFromLeftToRight().Row(row =>
        {
            row.RelativeItem().Text(company).FontSize(FooterSize).FontColor(Ink);
            row.RelativeItem().AlignRight().ContentFromLeftToRight().Text(text =>
            {
                text.DefaultTextStyle(style => style.FontSize(FooterSize).FontColor(Ink));
                text.CurrentPageNumber();
                text.Span("/");
                text.TotalPages();
            });
        });
    }

    public static string FormatPrintDate(DateTime value, bool isEnglish)
    {
        value = ToAfghanistanLocalTime(value);
        if (isEnglish)
        {
            return $"Print date: {value:yyyy/M/d}";
        }

        var calendar = new PersianCalendar();
        var date = string.Create(
            CultureInfo.InvariantCulture,
            $"{calendar.GetYear(value)}/{calendar.GetMonth(value)}/{calendar.GetDayOfMonth(value)}");
        return "تاریخ چاپ: " + ToPersianDigits(date);
    }

    public static string ToPersianDigits(string value)
    {
        const string western = "0123456789";
        const string persian = "۰۱۲۳۴۵۶۷۸۹";
        var characters = value.ToCharArray();
        for (var index = 0; index < characters.Length; index++)
        {
            var digit = western.IndexOf(characters[index]);
            if (digit >= 0)
            {
                characters[index] = persian[digit];
            }
        }
        return new string(characters);
    }

    public static bool IsLeftToRight(TabularExportCell cell)
        => cell.ValueType is not TabularExportValueType.Text
            || cell.Value is string value && IsMostlyLatin(value);

    public static string ValueColor(TabularExportCell cell)
    {
        if (cell.Value is null)
        {
            return Ink;
        }

        decimal? number = cell.ValueType switch
        {
            TabularExportValueType.Integer => Convert.ToDecimal(cell.Value, CultureInfo.InvariantCulture),
            TabularExportValueType.Number => Convert.ToDecimal(cell.Value, CultureInfo.InvariantCulture),
            TabularExportValueType.Percentage => Convert.ToDecimal(cell.Value, CultureInfo.InvariantCulture),
            _ => null
        };

        return number < 0 ? Negative : Ink;
    }

    public static string? ResolveWebAsset(string webRootPath, string? configuredPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath) || string.IsNullOrWhiteSpace(webRootPath))
        {
            return null;
        }

        var root = Path.GetFullPath(webRootPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var relative = configuredPath.Trim().TrimStart('~', '/', '\\')
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar);
        var candidate = Path.GetFullPath(Path.Combine(root, relative));
        return candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase) && File.Exists(candidate)
            ? candidate
            : null;
    }

    private static bool IsMostlyLatin(string value)
    {
        var letters = value.Where(char.IsLetter).ToArray();
        return letters.Length > 0 && letters.Count(character => character <= '\u024F') * 2 >= letters.Length;
    }

    private static DateTime ToAfghanistanLocalTime(DateTime value)
    {
        var utcValue = value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };
        return TimeZoneInfo.ConvertTimeFromUtc(utcValue, AfghanistanTimeZone);
    }

    private static TimeZoneInfo ResolveAfghanistanTimeZone()
    {
        foreach (var timeZoneId in new[] { "Asia/Kabul", "Afghanistan Standard Time" })
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            }
            catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                // Try the equivalent IANA/Windows identifier.
            }
        }

        return TimeZoneInfo.CreateCustomTimeZone(
            "Afghanistan",
            TimeSpan.FromMinutes(270),
            "Afghanistan",
            "Afghanistan");
    }
}
