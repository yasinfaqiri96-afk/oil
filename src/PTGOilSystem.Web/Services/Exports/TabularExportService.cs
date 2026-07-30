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

        var document = new PartyStatementPdfDocument(
            statement,
            _environment.WebRootPath,
            BuildBrandHeader(isEnglish ? _options.CompanyNameEn : _options.CompanyNameFa),
            isEnglish);
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

        var document = new PartyStatementPdfDocument(
            statement,
            _environment.WebRootPath,
            BuildBrandHeader(isEnglish ? _options.CompanyNameEn : _options.CompanyNameFa),
            isEnglish,
            contractGrouping);
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
            PdfDesignSystem.RegisterReferenceFonts(_environment.WebRootPath);
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
        var generatedAt = DateTime.Now;
        var metrics = BuildPdfMetrics(document, isEnglish);
        var brand = BuildBrandHeader(company);
        var columnCount = document.Columns.Count;
        var dense = columnCount > 6;
        var extraWide = columnCount > 12;
        var landscape = document.ForceLandscape || dense;
        var tableFontSize = PdfDesignSystem.TableFontSize(columnCount);

        container.Page(page =>
        {
            page.Size(extraWide
                ? PageSizes.A3.Landscape()
                : landscape
                    ? PageSizes.Letter.Landscape()
                    : PageSizes.Letter.Portrait());
            page.MarginHorizontal(PdfDesignSystem.HorizontalMargin);
            page.MarginTop(PdfDesignSystem.TopMargin);
            page.MarginBottom(PdfDesignSystem.BottomMargin);
            page.PageColor(QColors.White);
            page.DefaultTextStyle(style => PdfDesignSystem.DefaultTextStyle(style, isEnglish));

            var headerContainer = page.Header();
            if (!isEnglish)
            {
                headerContainer = headerContainer.ContentFromRightToLeft();
            }
            headerContainer.Column(column =>
            {
                column.Item().ShowOnce().Element(full =>
                    PdfDesignSystem.ComposeReportHeader(
                        full,
                        title,
                        generatedAt,
                        filters,
                        metrics,
                        isEnglish,
                        brand));
                column.Item().SkipOnce().Element(compact =>
                    PdfDesignSystem.ComposeReportHeader(
                        compact,
                        title,
                        generatedAt,
                        filters: null,
                        metrics: [],
                        isEnglish: isEnglish,
                        brand: brand,
                        compact: true));
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
                        columns.RelativeColumn(PdfDesignSystem.ColumnWeight(column));
                    }
                });

                table.Header(header =>
                {
                    foreach (var column in document.Columns)
                    {
                        var target = header.Cell().Element(cell =>
                            PdfDesignSystem.HeaderCell(cell, dense));
                        target = AlignHeaderCell(target, column, isEnglish);
                        target.Text(isEnglish ? column.TitleEn : column.TitleFa)
                            .Bold()
                            .FontSize(tableFontSize)
                            .FontColor(PdfDesignSystem.Ink);
                    }
                });

                for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
                {
                    var row = rows[rowIndex];
                    var background = rowIndex % 2 == 0
                        ? "#FFFFFF"
                        : PdfDesignSystem.AlternateRowBackground;

                    for (var columnIndex = 0; columnIndex < row.Cells.Count; columnIndex++)
                    {
                        var cell = row.Cells[columnIndex];
                        var column = document.Columns[columnIndex];
                        var target = table.Cell().Element(container =>
                            PdfDesignSystem.BodyCell(container, background, dense));
                        target = AlignBodyCell(target, column, cell, isEnglish);

                        target.Text(cell.ToDisplayText(isEnglish))
                            .FontSize(tableFontSize)
                            .FontColor(PdfDesignSystem.ValueColor(cell));
                    }
                }

                if (document.Totals is not null)
                {
                    for (var columnIndex = 0; columnIndex < document.Totals.Cells.Count; columnIndex++)
                    {
                        var cell = document.Totals.Cells[columnIndex];
                        var column = document.Columns[columnIndex];
                        var target = table.Cell().Element(container =>
                            PdfDesignSystem.TotalCell(container, dense));
                        target = AlignBodyCell(target, column, cell, isEnglish);

                        target.Text(cell.ToDisplayText(isEnglish))
                            .Bold()
                            .FontSize(tableFontSize)
                            .FontColor(PdfDesignSystem.ValueColor(cell));
                    }
                }
            });

            page.Footer().PaddingTop(8).Element(footer =>
                PdfDesignSystem.ComposeFooter(footer, company, isEnglish));
        });
    }

    private PdfBrandHeader BuildBrandHeader(string companyName)
        => new(
            companyName,
            PdfDesignSystem.ResolveWebAsset(_environment.WebRootPath, _options.CompanyLogoPath),
            _options.CompanyPhone,
            _options.CompanyEmail,
            _options.CompanyWebsite);

    private static IContainer AlignHeaderCell(
        IContainer target,
        TabularExportColumn column,
        bool isEnglish)
        => column.ValueType switch
        {
            TabularExportValueType.Integer
                or TabularExportValueType.Number
                or TabularExportValueType.Percentage
                => target.ContentFromLeftToRight().AlignRight(),
            TabularExportValueType.Date
                or TabularExportValueType.DateTime
                or TabularExportValueType.Boolean
                => target.ContentFromLeftToRight().AlignCenter(),
            _ when isEnglish => target.ContentFromLeftToRight().AlignLeft(),
            _ => target.ContentFromRightToLeft().AlignRight()
        };

    private static IContainer AlignBodyCell(
        IContainer target,
        TabularExportColumn column,
        TabularExportCell cell,
        bool isEnglish)
        => column.ValueType switch
        {
            TabularExportValueType.Integer
                or TabularExportValueType.Number
                or TabularExportValueType.Percentage
                => target.ContentFromLeftToRight().AlignRight(),
            TabularExportValueType.Date
                or TabularExportValueType.DateTime
                or TabularExportValueType.Boolean
                => target.ContentFromLeftToRight().AlignCenter(),
            _ when PdfDesignSystem.IsLeftToRight(cell) || isEnglish
                => target.ContentFromLeftToRight().AlignLeft(),
            _ => target.ContentFromRightToLeft().AlignRight()
        };

    private static IReadOnlyList<PdfSummaryMetric> BuildPdfMetrics(
        TabularExportDocument document,
        bool isEnglish)
    {
        var metrics = new List<PdfSummaryMetric>(4);
        if (document.Totals is not null)
        {
            for (var index = 0; index < document.Totals.Cells.Count && metrics.Count < 3; index++)
            {
                var cell = document.Totals.Cells[index];
                if (cell.Value is null || cell.ValueType is TabularExportValueType.Text
                    or TabularExportValueType.Date
                    or TabularExportValueType.DateTime
                    or TabularExportValueType.Boolean)
                {
                    continue;
                }

                var label = isEnglish
                    ? document.Columns[index].TitleEn
                    : document.Columns[index].TitleFa;
                var color = PdfDesignSystem.ValueColor(cell) == PdfDesignSystem.Negative
                    ? PdfDesignSystem.Negative
                    : PdfDesignSystem.Positive;
                metrics.Add(new PdfSummaryMetric(
                    label,
                    cell.ToDisplayText(isEnglish),
                    color));
            }
        }

        return metrics;
    }

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
