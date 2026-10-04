using System.Globalization;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using PTGOilSystem.Web.Models.Entities;

namespace PTGOilSystem.Web.Helpers;

/// <summary>
/// یک ردیف از فایل اکسل گروهی گمرک. هر ردیف = یک موتر/واگن با نمبر پلیت و سیمیر (CMR)
/// و مبالغ اجزای گمرکی. تطبیق با عملیات جاری بر اساس پلیت/سیمیر در کنترلر انجام می‌شود.
/// </summary>
public sealed record CustomsBatchImportRow(
    int RowNumber,
    DateTime? Date,
    string? Simir,
    string? PlateNumber,
    decimal? WeightMt,
    string? Destination,
    IReadOnlyList<CustomsBatchImportAmount> Amounts);

/// <summary>مبلغ یک جزء گمرکی در یک ردیف؛ IsUsd مشخص می‌کند مبلغ به دالر است یا افغانی.</summary>
public sealed record CustomsBatchImportAmount(CustomsComponentType ComponentType, decimal Amount, bool IsUsd);

/// <summary>
/// پارسر تحمل‌پذیر فایل گروهی گمرک. ستون‌ها با نام هدر (aliasها) پیدا می‌شوند؛
/// ترتیب ستون‌ها اهمیت ندارد. ستون «مجموع افغانی» خوانده نمی‌شود (در سیستم دوباره محاسبه می‌شود).
/// همهٔ اجزا افغانی هستند به‌جز «مصرف محصول به دالر» که USD است.
/// </summary>
public static class CustomsBatchWorkbookParser
{
    // ریشه‌های شناسایی ستون‌ها. تطبیق «شامل بودن» است نه برابری کامل، تا جابه‌جایی
    // کلمات، کلمهٔ اضافه (مثل «فی» یا «۵۰۰») یا تفاوت نگارش، ستون را از قلم نیندازد.
    // هر ستون فقط یک‌بار به یک جزء تخصیص می‌یابد (مجموعهٔ used مشترک در Parse).
    private static readonly string[] DateStems = ["تاریخ", "تاريخ", "date"];
    private static readonly string[] SimirStems = ["سیمیر", "سيمير", "سمیر", "cmr", "rwb", "billoflading"];
    private static readonly string[] PlateStems =
        ["نمبرموتر", "نمبرموتور", "نمبرپلیت", "پلیت", "نمبرواگن", "نمبرواگون",
         "platenumber", "trucknumber", "wagonnumber", "وسیله"];
    private static readonly string[] WeightStems = ["وزن", "تن", "وزنخالص", "netweight", "weight", "quantity"];
    private static readonly string[] DestStems = ["مقصد", "مسیر", "destination", "route"];

    // ── اجزای افغانی (ریشه) ── ترتیب اهمیت دارد: خاص‌تر قبل از عام‌تر.
    private static readonly string[] HaqKhidmaStems = ["خدمهمواد", "خذمهمواد", "حقالخدمه", "حقالخذمه", "haqkhidma"];
    private static readonly string[] Pul20Stems = ["20پول", "۲۰پول", "20pul"];
    private static readonly string[] MahsooliStems = ["محصولی", "محصول", "mahsooli"];
    private static readonly string[] FawaidStems = ["فواید", "فایده", "fawaid"];
    private static readonly string[] NormStems = ["نورم", "norm"];
    private static readonly string[] KhatAhanStems = ["خطآهن", "خطاهن", "khatahan", "railway"];
    private static readonly string[] BankStems = ["بانک", "bank"];
    private static readonly string[] BarnamaStems = ["بارنامه", "barnama", "waybill"];
    private static readonly string[] TarazuStems = ["ترازو", "tarazu", "weighbridge"];
    private static readonly string[] BarchalaniStems = ["بارچلان", "barchalani"];
    private static readonly string[] YozbulaghStems = ["یوزبلاغ", "یوزبلاق", "yozbulagh"];
    // ── ستون دالریِ رهنما ── مصرف می‌شود تا در جمع نیاید و با «محصولی» اشتباه نشود.
    private static readonly string[] DolariStems = ["محصولبهدالر", "محصولدالر", "محصولیدالری", "دالر", "usd"];

    public static IReadOnlyList<CustomsBatchImportRow> Parse(Stream stream)
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
        var headerRow = FindHeaderRow(rows, workbookPart)
            ?? throw new InvalidDataException("سطر عنوان (هدر) در فایل پیدا نشد. باید ستون سیمیر/نمبر موتر و اقلام گمرکی داشته باشد.");

        var headerCells = HeaderCells(headerRow, workbookPart);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // متادیتا اول. ترتیب مهم است:
        //  • پلیت قبل از وزن تا «نمبر موتر» با «وزن موتر» اشتباه نشود.
        //  • وزن قبل از سیمیر تا ستونِ «وزن سیمیر» به‌عنوان وزن گرفته شود، نه سیمیر
        //    (سیمیرِ واقعی ستون جداگانه‌ای با نام «سیمیر/CMR/RWB» است).
        var dateCol = FindCol(headerCells, used, DateStems);
        var plateCol = FindCol(headerCells, used, PlateStems);
        var weightCol = FindCol(headerCells, used, WeightStems);
        var simirCol = FindCol(headerCells, used, SimirStems);
        var destCol = FindCol(headerCells, used, DestStems);

        var componentCols = new List<(string Col, CustomsComponentType Type, bool IsUsd)>();
        void Add(string? col, CustomsComponentType type)
        {
            if (col is not null) componentCols.Add((col, type, false));
        }

        // ستون دالریِ رهنما را مصرف کن تا در جمع نیاید و ستون «محصولی» را ندزدد.
        FindCol(headerCells, used, DolariStems);

        // ترتیب: حق‌الخدمه قبل از ۲۰ پول، تا ستونِ ترکیبی «حق‌الخدمه...و کمیشن ۲۰ پول»
        // یک‌بار (به‌نام حق‌الخدمه) شمرده شود نه دوبار. محصولی بعد از مصرفِ ستون دالری.
        Add(FindCol(headerCells, used, HaqKhidmaStems), CustomsComponentType.HaqKhidma);
        Add(FindCol(headerCells, used, Pul20Stems), CustomsComponentType.Masraf20Pul);
        Add(FindCol(headerCells, used, MahsooliStems), CustomsComponentType.Mahsooli);
        Add(FindCol(headerCells, used, FawaidStems), CustomsComponentType.FawaidAama);
        Add(FindCol(headerCells, used, NormStems), CustomsComponentType.NormStandard);
        Add(FindCol(headerCells, used, KhatAhanStems), CustomsComponentType.KhatAhan);
        Add(FindCol(headerCells, used, BankStems), CustomsComponentType.KomisionBank);
        Add(FindCol(headerCells, used, BarnamaStems), CustomsComponentType.BarnamaWagon);
        Add(FindCol(headerCells, used, TarazuStems), CustomsComponentType.TarazuMotor);
        Add(FindCol(headerCells, used, BarchalaniStems), CustomsComponentType.KomisionBarchalani);
        Add(FindCol(headerCells, used, YozbulaghStems), CustomsComponentType.Yozbulagh);

        var headerIndex = headerRow.RowIndex?.Value ?? 0;
        var result = new List<CustomsBatchImportRow>();
        var lineNo = 0;

        foreach (var row in rows.Where(r => (r.RowIndex?.Value ?? 0) > headerIndex))
        {
            var cells = ToCellMap(row);
            var simir = ReadText(cells, simirCol, workbookPart);
            var plate = ReadText(cells, plateCol, workbookPart);

            // ردیف بدون پلیت و بدون سیمیر = خالی/جمع؛ رد شود.
            if (string.IsNullOrWhiteSpace(simir) && string.IsNullOrWhiteSpace(plate))
            {
                continue;
            }

            lineNo++;
            var amounts = new List<CustomsBatchImportAmount>();
            foreach (var (col, type, isUsd) in componentCols)
            {
                var value = ReadDecimal(cells, col, workbookPart);
                if (value is > 0m)
                {
                    amounts.Add(new CustomsBatchImportAmount(type, value.Value, isUsd));
                }
            }

            result.Add(new CustomsBatchImportRow(
                lineNo,
                ReadDate(cells, dateCol, workbookPart),
                string.IsNullOrWhiteSpace(simir) ? null : simir.Trim(),
                string.IsNullOrWhiteSpace(plate) ? null : plate.Trim(),
                ReadDecimal(cells, weightCol, workbookPart),
                ReadText(cells, destCol, workbookPart)?.Trim(),
                amounts));
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

            var hasKey = Contains(values, SimirStems) || Contains(values, PlateStems);
            var hasAmount = Contains(values, MahsooliStems)
                || Contains(values, FawaidStems)
                || Contains(values, HaqKhidmaStems)
                || Contains(values, DolariStems);
            if (hasKey && hasAmount)
            {
                return row;
            }
        }

        return null;
    }

    // ستون‌های هدر (حرفِ ستون + متنِ نرمال‌شده) به‌ترتیب ظاهر شدن.
    private static List<(string Col, string Norm)> HeaderCells(Row headerRow, WorkbookPart workbookPart)
        => headerRow.Elements<Cell>()
            .Where(c => c.CellReference is not null)
            .Select(c => (
                Col: GetColumnName(c.CellReference!.Value ?? string.Empty),
                Norm: Normalize(ReadCellText(c, workbookPart))))
            .Where(x => x.Norm.Length > 0)
            .ToList();

    // اولین ستونِ مصرف‌نشده که هدرش یکی از ریشه‌ها را «در بر داشته باشد» را برمی‌گرداند
    // و آن را مصرف‌شده علامت می‌زند تا به جزء دیگری تخصیص نیابد.
    private static string? FindCol(List<(string Col, string Norm)> cells, HashSet<string> used, string[] stems)
    {
        foreach (var (col, norm) in cells)
        {
            if (used.Contains(col))
            {
                continue;
            }

            if (stems.Any(norm.Contains))
            {
                used.Add(col);
                return col;
            }
        }

        return null;
    }

    private static bool Contains(IEnumerable<string> norms, string[] stems)
        => norms.Any(n => stems.Any(n.Contains));

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
            .Replace("؋", string.Empty, StringComparison.Ordinal)
            .Replace("€", string.Empty, StringComparison.Ordinal)
            .Trim();

        return decimal.TryParse(normalized, NumberStyles.Any, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

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

        // اکسل تاریخ را به‌صورت عددِ سریال ذخیره می‌کند؛ اگر عدد بود آن را به تاریخ تبدیل کن.
        if (double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var serial) && serial > 0 && serial < 100000)
        {
            try { return DateTime.FromOADate(serial); }
            catch { /* ignore */ }
        }

        if (PTGOilSystem.Web.Services.Calendars.AfghanSolarCalendar.TryParseActiveCalendarText(text, out var solar))
        {
            return solar;
        }

        return DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
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
