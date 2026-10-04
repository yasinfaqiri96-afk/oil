using System.Globalization;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using PTGOilSystem.Web.Models.Expenses;

namespace PTGOilSystem.Web.Helpers;

/// <summary>
/// Reads a simple expenses workbook (.xlsx) into raw import rows. The parser only
/// extracts and canonicalizes cell text; all business validation (expense type,
/// currency, contract, FX rate) is done later against the database in the controller.
/// Header matching accepts both Dari/Persian and English column titles.
/// </summary>
public static class ExpenseWorkbookParser
{
    private static readonly string[] DateAliases =
        ["date", "expensedate", NormalizeHeader("تاریخ"), NormalizeHeader("تاريخ")];

    private static readonly string[] TypeAliases =
        ["expensetype", "type", "expense", NormalizeHeader("نوعمصرف"), NormalizeHeader("نوع"), NormalizeHeader("مصرف")];

    private static readonly string[] AmountAliases =
        ["amount", "total", NormalizeHeader("مبلغ"), NormalizeHeader("مقدار")];

    private static readonly string[] CurrencyAliases =
        ["currency", NormalizeHeader("ارز"), NormalizeHeader("واحدپول"), NormalizeHeader("اسعار")];

    private static readonly string[] RateAliases =
        ["rate", "fxrate", "exchangerate", NormalizeHeader("نرخ"), NormalizeHeader("نرخدالر"), NormalizeHeader("نرخاسعار")];

    private static readonly string[] ContractAliases =
        ["contract", "contractnumber", "contractno", NormalizeHeader("قرارداد"), NormalizeHeader("شمارهقرارداد")];

    private static readonly string[] DescriptionAliases =
        ["description", "notes", "remarks", NormalizeHeader("شرح"), NormalizeHeader("توضیحات"), NormalizeHeader("ملاحظات")];

    private static readonly string[] ExplicitDateFormats =
    [
        "yyyy-MM-dd",
        "yyyy/M/d",
        "yyyy/MM/dd",
        "d.M.yyyy",
        "dd.MM.yyyy",
        "M/d/yyyy",
        "d/M/yyyy"
    ];

    public static IReadOnlyList<ExpenseImportRowViewModel> Parse(Stream stream)
    {
        using var document = SpreadsheetDocument.Open(stream, false);
        var workbookPart = document.WorkbookPart
            ?? throw new InvalidDataException("ساختار فایل اکسل معتبر نیست.");

        var sheets = workbookPart.Workbook.Sheets?.Elements<Sheet>().ToList()
            ?? throw new InvalidDataException("در فایل اکسل هیچ شیتی پیدا نشد.");

        foreach (var sheet in sheets)
        {
            if (sheet.Id?.Value is null)
            {
                continue;
            }

            var worksheetPart = (WorksheetPart)workbookPart.GetPartById(sheet.Id!.Value!);
            var sheetData = worksheetPart.Worksheet.GetFirstChild<SheetData>();
            if (sheetData is null)
            {
                continue;
            }

            var rows = sheetData.Elements<Row>().ToList();
            var headerRow = FindHeaderRow(rows, workbookPart);
            if (headerRow is null)
            {
                continue;
            }

            var columns = BuildColumnMap(headerRow, workbookPart);
            if (!columns.ContainsKey("Date")
                || !columns.ContainsKey("Type")
                || !columns.ContainsKey("Amount")
                || !columns.ContainsKey("Currency"))
            {
                throw new InvalidDataException(
                    "ستون‌های اصلی فایل مصارف کامل نیستند. حداقل «تاریخ»، «نوع مصرف»، «مبلغ» و «ارز» لازم است.");
            }

            var result = new List<ExpenseImportRowViewModel>();
            foreach (var row in rows.Where(r => (r.RowIndex?.Value ?? 0) > (headerRow.RowIndex?.Value ?? 0)))
            {
                var cells = ToCellMap(row);
                var parsed = MapRow(cells, columns, workbookPart, (int)(row.RowIndex?.Value ?? 0));
                if (IsEmptyRow(parsed))
                {
                    continue;
                }

                result.Add(parsed);
            }

            if (result.Count > 0)
            {
                return result;
            }
        }

        throw new InvalidDataException(
            "در فایل اکسل هیچ ردیف مصرفی پیدا نشد. سرستون‌ها و داده‌ها را بررسی کنید.");
    }

    private static ExpenseImportRowViewModel MapRow(
        IReadOnlyDictionary<string, Cell> cells,
        IReadOnlyDictionary<string, string> columns,
        WorkbookPart workbookPart,
        int excelRowNumber)
    {
        return new ExpenseImportRowViewModel
        {
            ExcelRowNumber = excelRowNumber,
            ExpenseDateText = CanonicalizeDate(ReadText(cells, columns, "Date", workbookPart)),
            ExpenseTypeName = ReadText(cells, columns, "Type", workbookPart),
            AmountText = CanonicalizeNumber(ReadText(cells, columns, "Amount", workbookPart)),
            Currency = ReadText(cells, columns, "Currency", workbookPart)?.ToUpperInvariant(),
            RatePerUsdText = CanonicalizeNumber(ReadText(cells, columns, "Rate", workbookPart)),
            ContractNumber = ReadText(cells, columns, "Contract", workbookPart),
            Description = ReadText(cells, columns, "Description", workbookPart)
        };
    }

    private static bool IsEmptyRow(ExpenseImportRowViewModel row)
        => string.IsNullOrWhiteSpace(row.ExpenseDateText)
           && string.IsNullOrWhiteSpace(row.ExpenseTypeName)
           && string.IsNullOrWhiteSpace(row.AmountText)
           && string.IsNullOrWhiteSpace(row.Currency)
           && string.IsNullOrWhiteSpace(row.ContractNumber)
           && string.IsNullOrWhiteSpace(row.Description);

    private static Dictionary<string, string> BuildColumnMap(Row headerRow, WorkbookPart workbookPart)
    {
        var columns = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var cell in headerRow.Elements<Cell>())
        {
            if (cell.CellReference is null)
            {
                continue;
            }

            var normalizedHeader = NormalizeHeader(ReadCellText(cell, workbookPart));
            if (string.IsNullOrWhiteSpace(normalizedHeader))
            {
                continue;
            }

            var column = GetColumnName(cell.CellReference?.Value ?? string.Empty);
            Assign(columns, "Date", column, normalizedHeader, DateAliases);
            Assign(columns, "Type", column, normalizedHeader, TypeAliases);
            Assign(columns, "Amount", column, normalizedHeader, AmountAliases);
            Assign(columns, "Currency", column, normalizedHeader, CurrencyAliases);
            Assign(columns, "Rate", column, normalizedHeader, RateAliases);
            Assign(columns, "Contract", column, normalizedHeader, ContractAliases);
            Assign(columns, "Description", column, normalizedHeader, DescriptionAliases);
        }

        return columns;
    }

    private static Row? FindHeaderRow(IEnumerable<Row> rows, WorkbookPart workbookPart)
    {
        foreach (var row in rows)
        {
            var values = row.Elements<Cell>()
                .Select(cell => NormalizeHeader(ReadCellText(cell, workbookPart)))
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToList();

            if (values.Count == 0)
            {
                continue;
            }

            var hasDate = values.Any(v => DateAliases.Contains(v, StringComparer.Ordinal));
            var hasAmount = values.Any(v => AmountAliases.Contains(v, StringComparer.Ordinal));
            var hasType = values.Any(v => TypeAliases.Contains(v, StringComparer.Ordinal));
            if (hasDate && hasAmount && hasType)
            {
                return row;
            }
        }

        return null;
    }

    private static void Assign(
        IDictionary<string, string> columns,
        string field,
        string column,
        string normalizedHeader,
        IEnumerable<string> aliases)
    {
        if (!columns.ContainsKey(field) && aliases.Contains(normalizedHeader, StringComparer.Ordinal))
        {
            columns[field] = column;
        }
    }

    private static IReadOnlyDictionary<string, Cell> ToCellMap(Row row)
        => row.Elements<Cell>()
            .Where(c => c.CellReference is not null)
            .ToDictionary(
                c => GetColumnName(c.CellReference?.Value ?? string.Empty),
                c => c,
                StringComparer.OrdinalIgnoreCase);

    private static string? ReadText(
        IReadOnlyDictionary<string, Cell> cells,
        IReadOnlyDictionary<string, string> columns,
        string field,
        WorkbookPart workbookPart)
    {
        if (!columns.TryGetValue(field, out var column) || !cells.TryGetValue(column, out var cell))
        {
            return null;
        }

        var value = ReadCellText(cell, workbookPart);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim().Trim('"');
    }

    private static string? CanonicalizeDate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var serial) && serial is > 1 and < 80000)
        {
            return DateTime.FromOADate(serial).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        if (PTGOilSystem.Web.Services.Calendars.AfghanSolarCalendar.TryParseActiveCalendarText(text, out var solar))
        {
            // تقویم هجری شمسی: متنِ شمسیِ نامعتبر خام می‌ماند تا اعتبارسنجی آن را گزارش کند.
            return solar?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? text.Trim();
        }

        if (DateTime.TryParseExact(text.Trim(), ExplicitDateFormats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var exact))
        {
            return exact.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var invariant))
        {
            return invariant.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        // Keep the raw text so validation can report it back to the user.
        return text.Trim();
    }

    private static string? CanonicalizeNumber(string? text)
    {
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
            ? value.ToString(CultureInfo.InvariantCulture)
            : text.Trim();
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

    private static string NormalizeHeader(string? value)
        => new((value ?? string.Empty)
            .Trim()
            .ToLowerInvariant()
            .Where(char.IsLetterOrDigit)
            .ToArray());
}
