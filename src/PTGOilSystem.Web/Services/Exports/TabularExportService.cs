using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Microsoft.Extensions.Options;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using QuestPDF.Drawing;
using QColors = QuestPDF.Helpers.Colors;
using SColor = DocumentFormat.OpenXml.Spreadsheet.Color;
using SFonts = DocumentFormat.OpenXml.Spreadsheet.Fonts;

namespace PTGOilSystem.Web.Services.Exports;

public sealed class TabularExportService : ITabularExportService
{
    private const uint TitleStyle = 1;
    private const uint MetaStyle = 2;
    private const uint HeaderStyle = 3;
    private const uint TextStyle = 4;
    private const uint IntegerStyle = 5;
    private const uint NumberStyle = 6;
    private const uint PercentageStyle = 7;
    private const uint DateStyle = 8;
    private const uint DateTimeStyle = 9;
    private const uint TotalTextStyle = 10;
    private const uint TotalNumberStyle = 11;

    private static readonly object QuestPdfInitializationLock = new();
    private static bool _questPdfInitialized;

    private readonly TabularExportOptions _options;
    private readonly IWebHostEnvironment _environment;

    public TabularExportService(IOptions<TabularExportOptions> options, IWebHostEnvironment environment)
    {
        _options = options.Value;
        _environment = environment;
        InitializeQuestPdf();
    }

    public int GetRowLimit(TabularExportFormat format)
        => format == TabularExportFormat.Excel ? _options.ExcelMaxRows : _options.PdfMaxRows;

    public Task WriteAsync(
        TabularExportDocument document,
        TabularExportFormat format,
        bool isEnglish,
        Stream destination,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        return WriteWorkbookAsync([document], format, isEnglish, destination, cancellationToken);
    }

    public Task WriteWorkbookAsync(
        IReadOnlyList<TabularExportDocument> sheets,
        TabularExportFormat format,
        bool isEnglish,
        Stream destination,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sheets);
        if (sheets.Count == 0)
        {
            throw new InvalidOperationException("Export requires at least one sheet.");
        }

        foreach (var sheet in sheets)
        {
            ValidateDocument(sheet, format);
        }

        return format == TabularExportFormat.Excel
            ? WriteExcelWorkbookAsync(sheets, isEnglish, destination, cancellationToken)
            : WritePdfWorkbookAsync(sheets, isEnglish, destination, cancellationToken);
    }

    public Task WritePartyStatementPdfAsync(
        Models.PartyStatements.PartyStatementResult statement,
        bool isEnglish,
        Stream destination,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(statement);
        ArgumentNullException.ThrowIfNull(destination);
        cancellationToken.ThrowIfCancellationRequested();
        if (statement.Rows.Count > _options.PdfMaxRows)
            throw new TabularExportLimitException(statement.Rows.Count, _options.PdfMaxRows);

        var document = new PartyStatementPdfDocument(statement, _environment.WebRootPath, isEnglish);
        document.GeneratePdf(destination);
        return Task.CompletedTask;
    }

    public Task WriteSupplierContractStatementPdfAsync(
        Models.PartyStatements.PartyStatementResult statement,
        Models.PartyStatements.SupplierContractStatementViewModel contractGrouping,
        bool isEnglish,
        Stream destination,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(statement);
        ArgumentNullException.ThrowIfNull(contractGrouping);
        ArgumentNullException.ThrowIfNull(destination);
        cancellationToken.ThrowIfCancellationRequested();
        // سقف بر اساس سطرهای همین سند است؛ هر سطر یک قرارداد، نه یک تراکنش.
        if (contractGrouping.Rows.Count > _options.PdfMaxRows)
            throw new TabularExportLimitException(contractGrouping.Rows.Count, _options.PdfMaxRows);

        var document = new PartyStatementPdfDocument(statement, _environment.WebRootPath, isEnglish, contractGrouping);
        document.GeneratePdf(destination);
        return Task.CompletedTask;
    }

    private void InitializeQuestPdf()
    {
        lock (QuestPdfInitializationLock)
        {
            if (_questPdfInitialized)
            {
                return;
            }

            QuestPDF.Settings.License = _options.QuestPdfLicense.Trim().ToLowerInvariant() switch
            {
                "professional" => LicenseType.Professional,
                "enterprise" => LicenseType.Enterprise,
                _ => LicenseType.Community
            };

            RegisterFont("vazirmatn-400.ttf");
            RegisterFont("vazirmatn-700.ttf");
            RegisterFont("poppins-400.ttf", "poppins");
            RegisterFont("poppins-700.ttf", "poppins");
            _questPdfInitialized = true;
        }
    }

    private void RegisterFont(string fileName, string familyFolder = "vazirmatn")
    {
        var path = Path.Combine(
            _environment.WebRootPath,
            "vendor",
            "fonts",
            familyFolder,
            "files",
            fileName);

        if (!System.IO.File.Exists(path))
        {
            return;
        }

        using var stream = System.IO.File.OpenRead(path);
        FontManager.RegisterFont(stream);
    }

    private void ValidateDocument(TabularExportDocument document, TabularExportFormat format)
    {
        if (document.Columns.Count == 0)
        {
            throw new InvalidOperationException("Export requires at least one column.");
        }

        if (document.KnownRowCount is { } rowCount && rowCount > GetRowLimit(format))
        {
            throw new TabularExportLimitException(rowCount, GetRowLimit(format));
        }

        if (document.Totals is not null && document.Totals.Cells.Count != document.Columns.Count)
        {
            throw new InvalidOperationException("The totals row must match the export column count.");
        }
    }

    private Task WriteExcelWorkbookAsync(
        IReadOnlyList<TabularExportDocument> documents,
        bool isEnglish,
        Stream destination,
        CancellationToken cancellationToken)
    {
        using var spreadsheet = SpreadsheetDocument.Create(destination, SpreadsheetDocumentType.Workbook, true);
        var workbookPart = spreadsheet.AddWorkbookPart();
        workbookPart.Workbook = new Workbook();

        var stylesPart = workbookPart.AddNewPart<WorkbookStylesPart>();
        stylesPart.Stylesheet = BuildStylesheet(isEnglish);
        stylesPart.Stylesheet.Save();

        var sheets = workbookPart.Workbook.AppendChild(new Sheets());
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        uint sheetId = 1;
        foreach (var document in documents)
        {
            var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
            WriteWorksheet(worksheetPart, document, isEnglish, cancellationToken);
            sheets.Append(new Sheet
            {
                Id = workbookPart.GetIdOfPart(worksheetPart),
                SheetId = sheetId++,
                Name = UniqueSheetName(SanitizeSheetName(isEnglish ? document.TitleEn : document.TitleFa), usedNames)
            });
        }

        workbookPart.Workbook.Save();
        return Task.CompletedTask;
    }

    private void WriteWorksheet(
        WorksheetPart worksheetPart,
        TabularExportDocument document,
        bool isEnglish,
        CancellationToken cancellationToken)
    {
        var lastColumn = GetColumnName(document.Columns.Count);
        const uint headerRowIndex = 6;
        var rowIndex = headerRowIndex + 1;
        var rowCount = 0;
        var rowLimit = _options.ExcelMaxRows;

        using var writer = OpenXmlWriter.Create(worksheetPart);
        writer.WriteStartElement(new Worksheet());
        writer.WriteElement(new SheetViews(
            new SheetView(
                new Pane
                {
                    VerticalSplit = 6D,
                    TopLeftCell = "A7",
                    ActivePane = PaneValues.BottomLeft,
                    State = PaneStateValues.Frozen
                })
            {
                WorkbookViewId = 0,
                RightToLeft = !isEnglish,
                ShowGridLines = false
            }));

        writer.WriteStartElement(new Columns());
        for (var index = 0; index < document.Columns.Count; index++)
        {
            var width = Math.Clamp(document.Columns[index].Width, 8D, 42D);
            writer.WriteElement(new Column
            {
                Min = (uint)index + 1,
                Max = (uint)index + 1,
                Width = width,
                CustomWidth = true
            });
        }
        writer.WriteEndElement();

        writer.WriteStartElement(new SheetData());
        WriteMergedTextRow(writer, 1, isEnglish ? _options.CompanyNameEn : _options.CompanyNameFa, TitleStyle);
        WriteMergedTextRow(writer, 2, isEnglish ? document.TitleEn : document.TitleFa, TitleStyle);
        WriteMergedTextRow(
            writer,
            3,
            (isEnglish ? "Generated: " : "تاریخ تولید: ") + DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
            MetaStyle);
        WriteMergedTextRow(writer, 4, BuildFilterText(document, isEnglish), MetaStyle);
        writer.WriteElement(new Row { RowIndex = 5 });

        writer.WriteStartElement(new Row { RowIndex = headerRowIndex, Height = 24D, CustomHeight = true });
        foreach (var column in document.Columns)
        {
            WriteInlineTextCell(writer, isEnglish ? column.TitleEn : column.TitleFa, HeaderStyle);
        }
        writer.WriteEndElement();

        foreach (var row in document.Rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            rowCount++;
            if (rowCount > rowLimit)
            {
                throw new TabularExportLimitException(rowCount, rowLimit);
            }
            if (row.Cells.Count != document.Columns.Count)
            {
                throw new InvalidOperationException($"Export row {rowCount:N0} does not match the column count.");
            }

            writer.WriteStartElement(new Row { RowIndex = rowIndex++ });
            foreach (var cell in row.Cells)
            {
                WriteExcelCell(writer, cell, isEnglish, isTotal: false);
            }
            writer.WriteEndElement();
        }

        if (document.Totals is not null)
        {
            writer.WriteStartElement(new Row { RowIndex = rowIndex++ });
            foreach (var cell in document.Totals.Cells)
            {
                WriteExcelCell(writer, cell, isEnglish, isTotal: true);
            }
            writer.WriteEndElement();
        }

        writer.WriteEndElement();
        writer.WriteElement(new AutoFilter { Reference = $"A{headerRowIndex}:{lastColumn}{Math.Max(headerRowIndex, rowIndex - 1)}" });
        writer.WriteElement(new MergeCells(
            new MergeCell { Reference = $"A1:{lastColumn}1" },
            new MergeCell { Reference = $"A2:{lastColumn}2" },
            new MergeCell { Reference = $"A3:{lastColumn}3" },
            new MergeCell { Reference = $"A4:{lastColumn}4" }));
        writer.WriteEndElement();
    }

    private Task WritePdfWorkbookAsync(
        IReadOnlyList<TabularExportDocument> documents,
        bool isEnglish,
        Stream destination,
        CancellationToken cancellationToken)
    {
        var pdf = Document.Create(container =>
        {
            foreach (var document in documents)
            {
                AddPdfPage(container, document, isEnglish, cancellationToken);
            }
        });

        pdf.GeneratePdf(destination);
        return Task.CompletedTask;
    }

    private void AddPdfPage(
        IDocumentContainer container,
        TabularExportDocument document,
        bool isEnglish,
        CancellationToken cancellationToken)
    {
        var rows = new List<TabularExportRow>(Math.Min(document.KnownRowCount ?? 256, _options.PdfMaxRows));
        foreach (var row in document.Rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (rows.Count >= _options.PdfMaxRows)
            {
                throw new TabularExportLimitException(rows.Count + 1, _options.PdfMaxRows);
            }
            if (row.Cells.Count != document.Columns.Count)
            {
                throw new InvalidOperationException($"Export row {rows.Count + 1:N0} does not match the column count.");
            }
            rows.Add(row);
        }

        var title = isEnglish ? document.TitleEn : document.TitleFa;
        var company = isEnglish ? _options.CompanyNameEn : _options.CompanyNameFa;
        var filters = BuildFilterText(document, isEnglish);
        var generated = DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        var landscape = document.ForceLandscape || document.Columns.Count > 7;

        container.Page(page =>
        {
            page.Size(landscape ? PageSizes.A4.Landscape() : PageSizes.A4.Portrait());
            page.Margin(24);
            page.PageColor(QColors.White);
            page.DefaultTextStyle(style => style.FontFamily(isEnglish ? "Poppins" : "Vazirmatn").FontSize(8).FontColor("#263244"));

            var headerContainer = page.Header();
            if (!isEnglish)
            {
                headerContainer = headerContainer.ContentFromRightToLeft();
            }
            headerContainer.Column(column =>
            {
                column.Item().Text(company).SemiBold().FontSize(9).FontColor("#64748B");
                column.Item().PaddingTop(2).Text(title).Bold().FontSize(15).FontColor("#172033");
                column.Item().PaddingTop(4).Text((isEnglish ? "Generated: " : "تاریخ تولید: ") + generated).FontSize(7).FontColor("#64748B");
                if (!string.IsNullOrWhiteSpace(filters))
                {
                    column.Item().PaddingTop(2).Text(filters).FontSize(7).FontColor("#64748B");
                }
                column.Item().PaddingTop(8).LineHorizontal(0.6f).LineColor("#D8DEE8");
            });

            var contentContainer = page.Content().PaddingTop(8);
            if (!isEnglish)
            {
                contentContainer = contentContainer.ContentFromRightToLeft();
            }
            contentContainer.Table(table =>
            {
                table.ColumnsDefinition(columns =>
                {
                    foreach (var column in document.Columns)
                    {
                        columns.RelativeColumn((float)Math.Max(1D, column.Width));
                    }
                });

                table.Header(header =>
                {
                    foreach (var column in document.Columns)
                    {
                        header.Cell().Element(HeaderCellStyle)
                            .Text(isEnglish ? column.TitleEn : column.TitleFa)
                            .SemiBold().FontSize(7.5f).FontColor(QColors.White);
                    }
                });

                var alternate = false;
                foreach (var row in rows)
                {
                    foreach (var cell in row.Cells)
                    {
                        var currentAlternate = alternate;
                        table.Cell().Element(element => BodyCellStyle(element, currentAlternate))
                            .Text(cell.ToDisplayText(isEnglish));
                    }
                    alternate = !alternate;
                }

                if (document.Totals is not null)
                {
                    foreach (var cell in document.Totals.Cells)
                    {
                        table.Cell().Element(TotalCellStyle)
                            .Text(cell.ToDisplayText(isEnglish)).SemiBold();
                    }
                }
            });

            var footerContainer = page.Footer().PaddingTop(8);
            if (!isEnglish)
            {
                footerContainer = footerContainer.ContentFromRightToLeft();
            }
            footerContainer.Row(row =>
            {
                row.RelativeItem().Text("PTG Oil System").FontSize(6.5f).FontColor("#94A3B8");
                row.RelativeItem().AlignRight().Text(text =>
                {
                    text.DefaultTextStyle(style => style.FontSize(6.5f).FontColor("#94A3B8"));
                    text.Span(isEnglish ? "Page " : "صفحه ");
                    text.CurrentPageNumber();
                    text.Span(" / ");
                    text.TotalPages();
                });
            });
        });
    }

    private static IContainer HeaderCellStyle(IContainer container)
        => container.Background("#334155").BorderBottom(0.5f).BorderColor("#CBD5E1").PaddingVertical(5).PaddingHorizontal(4);

    private static IContainer BodyCellStyle(IContainer container, bool alternate)
        => container.Background(alternate ? "#F8FAFC" : "#FFFFFF").BorderBottom(0.35f).BorderColor("#E2E8F0").PaddingVertical(4).PaddingHorizontal(4);

    private static IContainer TotalCellStyle(IContainer container)
        => container.Background("#EEF2F7").BorderTop(0.8f).BorderColor("#94A3B8").PaddingVertical(5).PaddingHorizontal(4);

    private static Stylesheet BuildStylesheet(bool isEnglish)
    {
        var fontName = isEnglish ? "Poppins" : "Vazirmatn";
        var fonts = new SFonts(
            new Font(new FontName { Val = fontName }, new FontSize { Val = 10D }, new FontFamilyNumbering { Val = 2 }),
            new Font(new Bold(), new FontName { Val = fontName }, new FontSize { Val = 15D }, new FontFamilyNumbering { Val = 2 }),
            new Font(new Bold(), new SColor { Rgb = "FFFFFFFF" }, new FontName { Val = fontName }, new FontSize { Val = 10D }, new FontFamilyNumbering { Val = 2 }),
            new Font(new Bold(), new FontName { Val = fontName }, new FontSize { Val = 10D }, new FontFamilyNumbering { Val = 2 }));

        var fills = new Fills(
            new Fill(new PatternFill { PatternType = PatternValues.None }),
            new Fill(new PatternFill { PatternType = PatternValues.Gray125 }),
            new Fill(new PatternFill(new ForegroundColor { Rgb = "FF334155" }) { PatternType = PatternValues.Solid }),
            new Fill(new PatternFill(new ForegroundColor { Rgb = "FFEEF2F7" }) { PatternType = PatternValues.Solid }));

        var borders = new Borders(
            new Border(),
            new Border(
                new LeftBorder { Style = BorderStyleValues.Hair, Color = new SColor { Rgb = "FFD8DEE8" } },
                new RightBorder { Style = BorderStyleValues.Hair, Color = new SColor { Rgb = "FFD8DEE8" } },
                new TopBorder { Style = BorderStyleValues.Hair, Color = new SColor { Rgb = "FFD8DEE8" } },
                new BottomBorder { Style = BorderStyleValues.Hair, Color = new SColor { Rgb = "FFD8DEE8" } },
                new DiagonalBorder()));

        var numberFormats = new NumberingFormats(
            new NumberingFormat { NumberFormatId = 164, FormatCode = "#,##0" },
            new NumberingFormat { NumberFormatId = 165, FormatCode = "#,##0.00" },
            new NumberingFormat { NumberFormatId = 166, FormatCode = "0.00%" },
            new NumberingFormat { NumberFormatId = 167, FormatCode = "yyyy-mm-dd" },
            new NumberingFormat { NumberFormatId = 168, FormatCode = "yyyy-mm-dd hh:mm" });

        var cellFormats = new CellFormats(
            new CellFormat { FontId = 0, FillId = 0, BorderId = 0 },
            new CellFormat { FontId = 1, FillId = 0, BorderId = 0, ApplyFont = true, Alignment = new Alignment { Horizontal = isEnglish ? HorizontalAlignmentValues.Left : HorizontalAlignmentValues.Right } },
            new CellFormat { FontId = 0, FillId = 0, BorderId = 0, ApplyFont = true, Alignment = new Alignment { Horizontal = isEnglish ? HorizontalAlignmentValues.Left : HorizontalAlignmentValues.Right } },
            new CellFormat { FontId = 2, FillId = 2, BorderId = 1, ApplyFont = true, ApplyFill = true, ApplyBorder = true, Alignment = new Alignment { Horizontal = HorizontalAlignmentValues.Center, Vertical = VerticalAlignmentValues.Center, WrapText = true } },
            new CellFormat { FontId = 0, FillId = 0, BorderId = 1, ApplyBorder = true, Alignment = new Alignment { Horizontal = isEnglish ? HorizontalAlignmentValues.Left : HorizontalAlignmentValues.Right, Vertical = VerticalAlignmentValues.Center, WrapText = true } },
            NumberCellFormat(164),
            NumberCellFormat(165),
            NumberCellFormat(166),
            NumberCellFormat(167),
            NumberCellFormat(168),
            new CellFormat { FontId = 3, FillId = 3, BorderId = 1, ApplyFont = true, ApplyFill = true, ApplyBorder = true, Alignment = new Alignment { Horizontal = isEnglish ? HorizontalAlignmentValues.Left : HorizontalAlignmentValues.Right } },
            new CellFormat { FontId = 3, FillId = 3, BorderId = 1, NumberFormatId = 165, ApplyFont = true, ApplyFill = true, ApplyBorder = true, ApplyNumberFormat = true, Alignment = new Alignment { Horizontal = HorizontalAlignmentValues.Right } });

        return new Stylesheet(numberFormats, fonts, fills, borders, cellFormats);

        static CellFormat NumberCellFormat(uint numberFormatId)
            => new()
            {
                FontId = 0,
                FillId = 0,
                BorderId = 1,
                NumberFormatId = numberFormatId,
                ApplyBorder = true,
                ApplyNumberFormat = true,
                Alignment = new Alignment { Horizontal = HorizontalAlignmentValues.Right, Vertical = VerticalAlignmentValues.Center }
            };
    }

    private static void WriteMergedTextRow(OpenXmlWriter writer, uint rowIndex, string value, uint styleIndex)
    {
        writer.WriteStartElement(new Row { RowIndex = rowIndex });
        WriteInlineTextCell(writer, value, styleIndex);
        writer.WriteEndElement();
    }

    private static void WriteExcelCell(OpenXmlWriter writer, TabularExportCell cell, bool isEnglish, bool isTotal)
    {
        if (cell.Value is null)
        {
            writer.WriteElement(new Cell { StyleIndex = isTotal ? TotalTextStyle : TextStyle });
            return;
        }

        switch (cell.ValueType)
        {
            case TabularExportValueType.Integer:
                WriteNumberCell(writer, Convert.ToInt64(cell.Value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture), isTotal ? TotalNumberStyle : IntegerStyle);
                break;
            case TabularExportValueType.Number:
                WriteNumberCell(writer, Convert.ToDecimal(cell.Value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture), isTotal ? TotalNumberStyle : NumberStyle);
                break;
            case TabularExportValueType.Percentage:
                WriteNumberCell(writer, Convert.ToDecimal(cell.Value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture), isTotal ? TotalNumberStyle : PercentageStyle);
                break;
            case TabularExportValueType.Date when cell.Value is DateTime date:
                WriteNumberCell(writer, date.ToOADate().ToString(CultureInfo.InvariantCulture), DateStyle);
                break;
            case TabularExportValueType.DateTime when cell.Value is DateTime dateTime:
                WriteNumberCell(writer, dateTime.ToOADate().ToString(CultureInfo.InvariantCulture), DateTimeStyle);
                break;
            case TabularExportValueType.Boolean:
                WriteInlineTextCell(writer, Convert.ToBoolean(cell.Value, CultureInfo.InvariantCulture) ? (isEnglish ? "Yes" : "بلی") : (isEnglish ? "No" : "نخیر"), isTotal ? TotalTextStyle : TextStyle);
                break;
            default:
                WriteInlineTextCell(writer, SanitizeSpreadsheetText(Convert.ToString(cell.Value, CultureInfo.InvariantCulture)), isTotal ? TotalTextStyle : TextStyle);
                break;
        }
    }

    private static void WriteNumberCell(OpenXmlWriter writer, string value, uint styleIndex)
        => writer.WriteElement(new Cell
        {
            DataType = CellValues.Number,
            CellValue = new CellValue(value),
            StyleIndex = styleIndex
        });

    private static void WriteInlineTextCell(OpenXmlWriter writer, string? value, uint styleIndex)
        => writer.WriteElement(new Cell
        {
            DataType = CellValues.InlineString,
            InlineString = new InlineString(new Text(value ?? string.Empty) { Space = SpaceProcessingModeValues.Preserve }),
            StyleIndex = styleIndex
        });

    internal static string SanitizeSpreadsheetText(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value ?? string.Empty;
        }

        var trimmed = value.TrimStart();
        return trimmed.Length > 0 && "=+-@".Contains(trimmed[0], StringComparison.Ordinal)
            ? "'" + value
            : value;
    }

    private static string BuildFilterText(TabularExportDocument document, bool isEnglish)
    {
        var activeFilters = document.Filters
            .Where(filter => !string.IsNullOrWhiteSpace(filter.Value))
            .Select(filter => $"{(isEnglish ? filter.LabelEn : filter.LabelFa)}: {filter.Value!.Trim()}")
            .ToArray();

        return activeFilters.Length == 0
            ? (isEnglish ? "Filters: none" : "فیلترها: بدون فیلتر")
            : string.Join(isEnglish ? " | " : " | ", activeFilters);
    }

    private static string GetColumnName(int columnCount)
    {
        var dividend = columnCount;
        var columnName = string.Empty;
        while (dividend > 0)
        {
            var modulo = (dividend - 1) % 26;
            columnName = Convert.ToChar('A' + modulo) + columnName;
            dividend = (dividend - modulo) / 26;
        }
        return columnName;
    }

    private static string SanitizeSheetName(string value)
    {
        var invalid = new[] { ':', '\\', '/', '?', '*', '[', ']' };
        var sanitized = new string(value.Where(character => !invalid.Contains(character)).ToArray()).Trim();
        if (string.IsNullOrWhiteSpace(sanitized))
        {
            sanitized = "Export";
        }
        return sanitized.Length <= 31 ? sanitized : sanitized[..31];
    }

    private static string UniqueSheetName(string name, HashSet<string> usedNames)
    {
        if (usedNames.Add(name))
        {
            return name;
        }

        for (var suffix = 2; suffix < 1000; suffix++)
        {
            var tag = $" ({suffix})";
            var trimmed = name.Length + tag.Length <= 31 ? name : name[..(31 - tag.Length)];
            var candidate = trimmed + tag;
            if (usedNames.Add(candidate))
            {
                return candidate;
            }
        }

        return name;
    }
}

public sealed class TabularExportLimitException(int actualRows, int maximumRows) : Exception
{
    public int ActualRows { get; } = actualRows;
    public int MaximumRows { get; } = maximumRows;
}
