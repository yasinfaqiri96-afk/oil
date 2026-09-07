using System.Globalization;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace PTGOilSystem.Web.Helpers;

/// <summary>
/// یک ردیف از فایل «انتقال گروهی به وسیله دیگر»: نمبر وسیلهٔ قبلی (حمل در جریان)،
/// نمبر وسیلهٔ جدید (مقصد) و وزن انتقال.
/// </summary>
public sealed record TransportContinueImportRow(
    string SourceVehicleNumber,
    string TargetVehicleNumber,
    decimal QuantityMt);

/// <summary>
/// Parser مدارا برای شیتِ انتقال وسیله‌به‌وسیله. ستون‌ها با نام هدر پیدا می‌شوند
/// (نمبر وسیله قبلی | نمبر وسیله جدید | وزن) و اگر هدر پیدا نشد به A/B/C برمی‌گردد.
/// </summary>
public static class TransportContinueWorkbookParser
{
    private static readonly string[] SourceAliases =
        ["نمبروسیلهقبلی", "نمبروسیلهقبلی", "وسیلهقبلی", "نمبرقبلی", "وسیلهمبدا", "وسیلهمبدأ", "نمبرمبدا", "نمبرمبدأ",
         "مبدا", "مبدأ", "حملقبلی", "نمبرحملقبلی", "وسیلهفعلی", "نمبروسیلهفعلی",
         "oldvehicle", "oldvehiclenumber", "old", "fromvehicle", "from", "source", "sourcevehicle", "previousvehicle"];

    private static readonly string[] TargetAliases =
        ["نمبروسیلهجدید", "وسیلهجدید", "نمبرجدید", "وسیلهمقصد", "نمبرمقصد", "مقصد", "حملجدید", "نمبرحملجدید",
         "newvehicle", "newvehiclenumber", "new", "tovehicle", "to", "target", "targetvehicle", "destination"];

    private static readonly string[] QuantityAliases =
        ["وزن", "وزنسیمیر", "وزنسمیر", "وزنخالص", "مقدار", "مقدارmt", "مقدارانتقال", "وزنانتقال",
         "quantity", "quantitymt", "weight", "netweight", "mt", "تن", "tonnage"];

    public static IReadOnlyList<TransportContinueImportRow> Parse(Stream stream)
    {
        using var document = SpreadsheetDocument.Open(stream, false);
        var workbookPart = document.WorkbookPart
            ?? throw new InvalidDataException("ساختار فایل اکسل معتبر نیست.");
        var sheet = workbookPart.Workbook.Sheets?.Elements<Sheet>().FirstOrDefault()
            ?? throw new InvalidDataException("در فایل اکسل هیچ شیتی پیدا نشد.");
        var worksheetPart = (WorksheetPart)workbookPart.GetPartById(sheet.Id!);
        var sheetData = worksheetPart.Worksheet.GetFirstChild<SheetData>();
        if (sheetData is null)
        {
            return [];
        }

        var rows = sheetData.Elements<Row>().ToList();
        var headerRow = FindHeaderRow(rows, workbookPart);

        var sourceCol = MatchColumn(headerRow, workbookPart, SourceAliases) ?? "A";
        var targetCol = MatchColumn(headerRow, workbookPart, TargetAliases) ?? "B";
        var quantityCol = MatchColumn(headerRow, workbookPart, QuantityAliases) ?? "C";

        var headerIndex = headerRow?.RowIndex?.Value ?? 0;
        var result = new List<TransportContinueImportRow>();

        foreach (var row in rows.Where(r => (r.RowIndex?.Value ?? 0) > headerIndex))
        {
            var cells = ToCellMap(row);
            var source = ReadText(cells, sourceCol, workbookPart);
            var target = ReadText(cells, targetCol, workbookPart);
            var quantity = ReadDecimal(cells, quantityCol, workbookPart) ?? 0m;

            // یک ردیف حداقل باید وسیلهٔ قبلی و مقدار مثبت داشته باشد.
            if (string.IsNullOrWhiteSpace(source) || quantity <= 0m)
            {
                continue;
            }

            result.Add(new TransportContinueImportRow(
                source.Trim(),
                (target ?? string.Empty).Trim(),
                quantity));
        }

        return result;
    }

    private static Row? FindHeaderRow(IReadOnlyList<Row> rows, WorkbookPart workbookPart)
    {
        foreach (var row in rows)
        {
            var values = row.Elements<Cell>()
                .Select(cell => Normalize(ReadCellText(cell, workbookPart)))
                .Where(value => value.Length > 0)
                .ToList();
            if (values.Count == 0)
            {
                continue;
            }

            var hasSource = SourceAliases.Any(values.Contains);
            var hasTarget = TargetAliases.Any(values.Contains);
            var hasQuantity = QuantityAliases.Any(values.Contains);
            if ((hasSource && hasTarget) || (hasSource && hasQuantity) || (hasTarget && hasQuantity))
            {
                return row;
            }
        }

        return null;
    }

    private static string? MatchColumn(Row? headerRow, WorkbookPart workbookPart, string[] aliases)
    {
        if (headerRow is null)
        {
            return null;
        }

        foreach (var cell in headerRow.Elements<Cell>())
        {
            if (cell.CellReference is null)
            {
                continue;
            }

            var normalized = Normalize(ReadCellText(cell, workbookPart));
            if (normalized.Length > 0 && aliases.Contains(normalized))
            {
                return GetColumnName(cell.CellReference.Value ?? string.Empty);
            }
        }

        return null;
    }

    private static IReadOnlyDictionary<string, Cell> ToCellMap(Row row)
        => row.Elements<Cell>()
            .Where(c => c.CellReference is not null)
            .ToDictionary(
                c => GetColumnName(c.CellReference!.Value ?? string.Empty),
                c => c,
                StringComparer.OrdinalIgnoreCase);

    private static string? ReadText(IReadOnlyDictionary<string, Cell> cells, string? column, WorkbookPart workbookPart)
    {
        if (column is null || !cells.TryGetValue(column, out var cell))
        {
            return null;
        }

        var value = ReadCellText(cell, workbookPart);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim().Trim('"');
    }

    private static decimal? ReadDecimal(IReadOnlyDictionary<string, Cell> cells, string? column, WorkbookPart workbookPart)
    {
        if (column is null || !cells.TryGetValue(column, out var cell))
        {
            return null;
        }

        var text = ReadCellText(cell, workbookPart);
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var normalized = text
            .Replace(",", string.Empty, StringComparison.Ordinal)
            .Replace("$", string.Empty, StringComparison.Ordinal)
            .Trim();

        return decimal.TryParse(normalized, NumberStyles.Any, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    private static string ReadCellText(Cell cell, WorkbookPart workbookPart)
    {
        if (cell.DataType?.Value == CellValues.SharedString)
        {
            var sharedStrings = workbookPart.SharedStringTablePart?.SharedStringTable;
            if (sharedStrings is null)
            {
                return string.Empty;
            }

            return int.TryParse(cell.CellValue?.Text, out var index)
                ? sharedStrings.ElementAt(index).InnerText
                : string.Empty;
        }

        if (cell.DataType?.Value == CellValues.InlineString)
        {
            return cell.InlineString?.InnerText ?? string.Empty;
        }

        return cell.CellValue?.Text ?? cell.InnerText ?? string.Empty;
    }

    private static string GetColumnName(string cellReference)
        => new(cellReference.TakeWhile(char.IsLetter).ToArray());

    private static string Normalize(string? value)
        => new((value ?? string.Empty)
            .Trim()
            .ToLowerInvariant()
            .Where(char.IsLetterOrDigit)
            .ToArray());
}
