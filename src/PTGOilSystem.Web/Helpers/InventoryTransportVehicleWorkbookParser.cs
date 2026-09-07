using System.Globalization;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using PTGOilSystem.Web.Models.Entities;

namespace PTGOilSystem.Web.Helpers;

/// <summary>
/// Row parsed from a "group from inventory" vehicle Excel file. Each row is one
/// truck or wagon. Carrier / driver are intentionally NOT read — the user fills
/// them in the grid after import.
/// </summary>
public sealed record InventoryTransportVehicleImportRow(
    int TransportTypeValue,
    string VehicleNumber,
    decimal QuantityMt,
    decimal? FreightWeightMt,
    decimal? FreightAmount,
    string? CurrencyText,
    string? RwbNo,
    DateTime? LoadedDate);

/// <summary>
/// Tolerant parser for the vehicle import sheet. Supports the real railway-expense
/// layout (تاریخ | سیمیر | نمبر واگون | وزن محاسبه | وزن سیمیر | فی تن محاسبه) as well
/// as a plain layout (نوع | نمبر | مقدار MT | کرایه | ارز). Columns are located by
/// header aliases. When a rate-per-ton column exists the freight is computed as
/// rate × quantity in USD; otherwise a freight-amount column is read directly.
/// </summary>
public static class InventoryTransportVehicleWorkbookParser
{
    private static readonly string[] TypeAliases = ["نوع", "type", "نوعوسیله", "کتگوری"];
    private static readonly string[] ReferenceAliases =
        ["سیمیر", "سيمير", "cmr", "cmrno", "rwb", "rwbno", "billoflading", "billofladingno", "reference", "referenceno"];
    private static readonly string[] NumberAliases =
        ["نمبرواگون", "نمبرواگن", "نمبرموتر", "نمبرموتور", "نمبرپلیت", "پلیت", "platenumber", "plate",
         "wagonnumber", "wagonno", "trucknumber", "wagon", "truck", "truckno", "نمبر", "number", "وسیله"];
    // «وزن سیمیر» = وزن واقعیِ حمل (مقدار MT اصلی که در موجودی/تخصیص استفاده می‌شود).
    private static readonly string[] ActualWeightAliases =
        ["وزنسیمیر", "وزنسمیر", "cmrweight", "simirweight", "مقدار", "مقدارmt", "quantity", "quantitymt",
         "netweight", "وزنخالص", "وزن", "mt", "تن", "tonnage", "weight"];
    // «وزن محاسبه» = وزنِ مبنای محاسبهٔ کرایه (کرایه = فی‌تن × وزن محاسبه). فقط برای کرایه.
    private static readonly string[] CalcWeightAliases =
        ["وزنمحاسبه", "chargeableweight", "chargeablequantity", "calcweight", "محاسبه"];
    private static readonly string[] RateAliases =
        ["فیتنمحاسبه", "فیتن", "ratepermt", "railwayrate", "rate", "perton", "unitrate", "price", "قیمتفیتن"];
    private static readonly string[] FreightAliases =
        ["کرایه", "freight", "freightamount", "کرایهمبلغ", "مبلغکرایه", "rent", "amount"];
    private static readonly string[] CurrencyAliases =
        ["ارز", "currency", "واحدپول", "واحدپولی", "پول"];
    // تاریخ بارگیریِ همان ردیف. یک موتر می‌تواند چند سفر با تاریخ‌های متفاوت در یک فایل داشته باشد.
    private static readonly string[] DateAliases =
        ["تاریخ", "تاریخبارگیری", "تاریخحمل", "date", "loadingdate", "loaddate", "loadeddate", "transportdate"];

    private static readonly string[] WagonWords = ["واگن", "واگون", "vagon", "wagon"];

    public static IReadOnlyList<InventoryTransportVehicleImportRow> Parse(Stream stream)
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

        var typeCol = MatchColumn(headerRow, workbookPart, TypeAliases);
        var referenceCol = MatchColumn(headerRow, workbookPart, ReferenceAliases);
        var numberCol = MatchColumn(headerRow, workbookPart, NumberAliases) ?? "B";
        var actualWeightCol = MatchColumn(headerRow, workbookPart, ActualWeightAliases);
        var calcWeightCol = MatchColumn(headerRow, workbookPart, CalcWeightAliases);
        // مقدار حمل از «وزن سیمیر»؛ اگر نبود، از «وزن محاسبه»؛ در نبودِ هر دو، ستون پیش‌فرض C.
        var quantityCol = actualWeightCol ?? calcWeightCol ?? "C";
        var rateCol = MatchColumn(headerRow, workbookPart, RateAliases);
        var freightCol = MatchColumn(headerRow, workbookPart, FreightAliases);
        var currencyCol = MatchColumn(headerRow, workbookPart, CurrencyAliases);
        var dateCol = MatchColumn(headerRow, workbookPart, DateAliases);

        // No explicit type column → infer from the number-column header: «نمبر موتر» ⇒ truck,
        // otherwise fall back to wagon (railway-expense format).
        var numberHeader = headerRow is null
            ? string.Empty
            : Normalize(ReadText(ToCellMap(headerRow), numberCol, workbookPart));
        var headerIsTruck = numberHeader.Contains(Normalize("موتر"), StringComparison.Ordinal)
            || numberHeader.Contains("truck", StringComparison.Ordinal);
        var defaultType = typeCol is not null
            ? (int)LoadingTransportType.Truck
            : headerIsTruck
                ? (int)LoadingTransportType.Truck
                : (int)LoadingTransportType.Wagon;

        var headerIndex = headerRow?.RowIndex?.Value ?? 0;
        var result = new List<InventoryTransportVehicleImportRow>();

        foreach (var row in rows.Where(r => (r.RowIndex?.Value ?? 0) > headerIndex))
        {
            var cells = ToCellMap(row);
            var number = ReadText(cells, numberCol, workbookPart);
            // وزن سیمیر (وزن واقعی حمل). اگر ستون سیمیر نبود، از همان ستونِ منتخب خوانده می‌شود.
            var actualWeight = ReadDecimal(cells, actualWeightCol ?? quantityCol, workbookPart) ?? 0m;
            // وزن محاسبهٔ کرایه؛ اگر نبود، همان وزن واقعی مبنا می‌شود.
            var calcWeight = calcWeightCol is null ? (decimal?)null : ReadDecimal(cells, calcWeightCol, workbookPart);
            var quantity = actualWeight > 0m ? actualWeight : (calcWeight ?? 0m);

            // A row must carry at least a vehicle number and a positive quantity.
            if (string.IsNullOrWhiteSpace(number) || quantity <= 0m)
            {
                continue;
            }

            var transportType = typeCol is null
                ? defaultType
                : ResolveTransportType(ReadText(cells, typeCol, workbookPart) ?? string.Empty, number, defaultType);

            var currencyText = currencyCol is null ? null : ReadText(cells, currencyCol, workbookPart);

            // مبنای کرایه = وزن محاسبه؛ اگر نبود، وزن واقعیِ حمل.
            var freightWeight = calcWeight is > 0m ? calcWeight.Value : quantity;

            decimal? freight = null;
            var rate = rateCol is null ? null : ReadDecimal(cells, rateCol, workbookPart);
            if (rate is > 0m)
            {
                freight = decimal.Round(rate.Value * freightWeight, 2, MidpointRounding.AwayFromZero);
                currencyText ??= "USD"; // فی تن محاسبه با علامت $ = دالر
            }
            else if (freightCol is not null)
            {
                var amount = ReadDecimal(cells, freightCol, workbookPart);
                freight = amount is > 0m ? amount : null;
            }

            var reference = referenceCol is null ? null : ReadText(cells, referenceCol, workbookPart);
            var loadedDate = dateCol is null ? null : ReadDate(cells, dateCol, workbookPart);

            result.Add(new InventoryTransportVehicleImportRow(
                transportType,
                number.Trim(),
                quantity,
                calcWeight is > 0m ? calcWeight : null,
                freight,
                freight.HasValue ? (string.IsNullOrWhiteSpace(currencyText) ? "USD" : currencyText.Trim()) : null,
                string.IsNullOrWhiteSpace(reference) ? null : reference.Trim(),
                loadedDate));
        }

        return result;
    }

    private static int ResolveTransportType(string typeText, string number, int defaultType)
    {
        var haystack = Normalize(typeText) + Normalize(number);
        foreach (var word in WagonWords)
        {
            if (haystack.Contains(Normalize(word), StringComparison.Ordinal))
            {
                return (int)LoadingTransportType.Wagon; // 2
            }
        }

        if (Normalize(typeText).Length > 0)
        {
            return (int)LoadingTransportType.Truck; // 3 — explicit non-wagon text
        }

        return defaultType;
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

            var matches = (ActualWeightAliases.Any(a => values.Contains(a)) || CalcWeightAliases.Any(a => values.Contains(a)))
                && (NumberAliases.Any(a => values.Contains(a)) || TypeAliases.Any(a => values.Contains(a)));
            if (matches)
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

    // تاریخ در اکسل یا عدد سریال است (فرمت تاریخ) یا متن. هر دو پذیرفته می‌شود.
    private static DateTime? ReadDate(IReadOnlyDictionary<string, Cell> cells, string? column, WorkbookPart workbookPart)
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

        text = text.Trim();
        if (double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var serial))
        {
            // سریال‌های معتبر اکسل؛ خارج از این بازه یعنی ستون تاریخ نیست.
            if (serial is < 1 or > 2958465)
            {
                return null;
            }

            try
            {
                return DateTime.FromOADate(serial).Date;
            }
            catch (ArgumentException)
            {
                return null;
            }
        }

        return DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            || DateTime.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.None, out parsed)
                ? parsed.Date
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
