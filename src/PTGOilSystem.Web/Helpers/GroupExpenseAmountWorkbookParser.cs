using System.Globalization;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace PTGOilSystem.Web.Helpers;

/// <summary>
/// One row of the manual group-expense sheet: vehicle number + the amount that
/// operation carries. Nothing else is read; matching to operations is done in the
/// wizard on the client.
/// </summary>
public sealed record GroupExpenseAmountImportRow(string VehicleNumber, decimal Amount);

/// <summary>
/// Tolerant parser for the "manual split" sheet (نمبر وسیله | مقدار مصرف). Columns are
/// located by header aliases; with no recognizable header the first two columns are used.
/// </summary>
public static class GroupExpenseAmountWorkbookParser
{
    private static readonly string[] NumberAliases =
        ["نمبروسیله", "نمبرموتر", "نمبرموتور", "نمبرواگون", "نمبرواگن", "نمبرپلیت", "نمبر", "پلیت", "وسیله",
         "platenumber", "plate", "vehicle", "vehiclenumber", "trucknumber", "truckno", "truck",
         "wagonnumber", "wagonno", "wagon", "number", "no"];

    private static readonly string[] AmountAliases =
        ["مقدارمصرف", "مبلغمصرف", "مصرف", "مبلغ", "سهم", "قیمت", "amount", "expense", "expenseamount",
         "cost", "value", "share", "price", "نرخفیتن", "نرخهرتن", "نرخ", "فیتن", "rateperton", "rate",
         "unitrate", "unitprice", "مقدار"];

    public static IReadOnlyList<GroupExpenseAmountImportRow> Parse(Stream stream)
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

        var numberCol = MatchColumn(headerRow, workbookPart, NumberAliases) ?? "A";
        var amountCol = MatchColumn(headerRow, workbookPart, AmountAliases) ?? "B";
        var headerIndex = headerRow?.RowIndex?.Value ?? 0;

        var result = new List<GroupExpenseAmountImportRow>();
        foreach (var row in rows.Where(r => (r.RowIndex?.Value ?? 0) > headerIndex))
        {
            var cells = ToCellMap(row);
            var number = ReadText(cells, numberCol, workbookPart);
            var amount = ReadDecimal(cells, amountCol, workbookPart);

            // A row must carry a vehicle number and a positive amount.
            if (string.IsNullOrWhiteSpace(number) || amount is not > 0m)
            {
                continue;
            }

            result.Add(new GroupExpenseAmountImportRow(
                number.Trim(),
                decimal.Round(amount.Value, 2, MidpointRounding.AwayFromZero)));
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

            if (NumberAliases.Any(values.Contains) && AmountAliases.Any(values.Contains))
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
            .Replace("€", string.Empty, StringComparison.Ordinal)
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
