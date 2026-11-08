using System.Globalization;
using System.IO.Compression;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using PTGOilSystem.Web.Models.Sales;

namespace PTGOilSystem.Web.Helpers;

public static class SalesExcelWorkbookParser
{
    public const int MaxRows = 500;
    public const long MaxFileBytes = 5 * 1024 * 1024;
    private static readonly string[] Headers = ["ImportKey", "ContractNumber", "SaleDateSolar", "BuyerName",
        "TicketSerial", "TruckNumber", "DriverName", "QuantityMt", "UnitPriceUSD", "SourceAmountUSD",
        "CalculatedAmountUSD", "DifferenceUSD", "SourceLocationNote", "OriginalDescription", "OriginalExcelRow", "ReviewStatus"];

    public static List<SalesExcelRow> Parse(Stream stream)
    {
        // Bound the decompressed ZIP too, before OpenXML loads XML parts.
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true))
            if (zip.Entries.Count > 200 || zip.Entries.Sum(e => e.Length) > 25 * 1024 * 1024)
                throw new InvalidDataException("حجم بازشدهٔ اکسل بیش از حد مجاز است.");
        stream.Position = 0;
        using var doc = SpreadsheetDocument.Open(stream, false);
        var book = doc.WorkbookPart ?? throw new InvalidDataException("ساختار اکسل معتبر نیست.");
        var strings = book.SharedStringTablePart?.SharedStringTable?.Elements<SharedStringItem>().Select(s => s.InnerText).ToArray() ?? [];
        string Cell(Cell c)
        {
            var column = new string((c.CellReference?.Value ?? "").TakeWhile(char.IsLetter).ToArray());
            // The preparation workbook contains ROUND formulas in K/L. Never
            // execute them: their cached numbers are checked against H × I below.
            if (c.CellFormula is not null && column is not ("K" or "L"))
                throw new InvalidDataException("ستون‌های ورودی باید مقدار ثابت داشته باشند؛ فرمول را به مقدار تبدیل کنید.");
            return c.DataType?.Value == CellValues.SharedString
                ? strings[int.Parse(c.CellValue!.Text, CultureInfo.InvariantCulture)]
                : c.DataType?.Value == CellValues.InlineString ? c.InlineString?.InnerText ?? "" : c.CellValue?.Text ?? "";
        }
        var sheets = book.Workbook.Sheets!.Elements<Sheet>().ToList();
        var sheet = sheets.SingleOrDefault(s => s.Name?.Value == "فروشات_آماده")
            ?? throw new InvalidDataException("شیت فروشات_آماده پیدا نشد؛ فایل اصلی قرارداد را مستقیماً وارد نکنید.");
        List<Dictionary<string, string>> Read(Sheet s) => ((WorksheetPart)book.GetPartById(s.Id!)).Worksheet
            .Descendants<Row>().Select(r => r.Elements<Cell>().ToDictionary(
                c => new string((c.CellReference?.Value ?? "").TakeWhile(char.IsLetter).ToArray()), Cell)).ToList();
        var table = Read(sheet);
        if (table.Count < 2 || table.Count > MaxRows + 1) throw new InvalidDataException("تعداد فروشات باید بین ۱ و ۵۰۰ باشد.");
        for (var i = 0; i < Headers.Length; i++)
            if (table[0].GetValueOrDefault(((char)('A' + i)).ToString()) != Headers[i])
                throw new InvalidDataException($"ستون {Headers[i]} با قالب آماده‌سازی مطابقت ندارد.");

        var reasons = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var reviewSheet = sheets.SingleOrDefault(s => s.Name?.Value == "موارد_بررسی");
        if (reviewSheet is not null)
            foreach (var r in Read(reviewSheet).Skip(1))
            {
                var key = r.GetValueOrDefault("A", "").Trim();
                if (key.Length == 0) continue;
                if (!reasons.ContainsKey(key)) reasons[key] = [];
                reasons[key].Add(r.GetValueOrDefault("H", "نیازمند بررسی"));
            }

        var rows = new List<SalesExcelRow>();
        for (var i = 1; i < table.Count; i++)
        {
            var r = table[i];
            string V(string col) => r.GetValueOrDefault(col, "").Trim();
            if (r.Values.All(string.IsNullOrWhiteSpace)) continue;
            decimal Number(string col) => decimal.TryParse(Digits(V(col)), NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture, out var v) ? v : throw new InvalidDataException($"ردیف {i + 1}: عدد ستون {col} معتبر نیست.");
            var key = V("A");
            var review = reasons.GetValueOrDefault(key)?.Distinct().ToList() ?? [];
            if (V("P") != "آماده برای تطبیق منبع" && review.Count == 0) review.Add("بازبینی پیش از ثبت: " + V("P"));
            var row = new SalesExcelRow
            {
                ExcelRow = i + 1, ImportKey = key, ContractNumber = V("B"), SaleDateSolar = V("C"), SaleDate = ParseSolar(V("C")),
                BuyerName = V("D"), TicketSerial = V("E"), TruckNumber = V("F"), DriverName = V("G"),
                QuantityMt = Number("H"), UnitPriceUsd = Number("I"), SourceAmountUsd = Number("J"),
                Notes = $"{V("N")} | {V("M")} | سطر اصلی: {V("O")}", ReviewReason = string.Join("؛ ", review)
            };
            if (key.Length is 0 or > 100 || row.BuyerName.Length == 0 || row.ContractNumber.Length == 0
                || row.QuantityMt <= 0 || row.UnitPriceUsd <= 0 || row.SourceAmountUsd <= 0
                || row.QuantityMt > 1000000m || row.UnitPriceUsd > 1000000m
                || row.QuantityMt != decimal.Round(row.QuantityMt, 4) || row.UnitPriceUsd != decimal.Round(row.UnitPriceUsd, 4)
                || row.TicketSerial.Length > 100 || BuildNotes(row, "").Length > 1000)
                throw new InvalidDataException($"ردیف {i + 1}: مقدار، مشخصات یا طول متن معتبر نیست.");
            // The existing sale primitive is the only amount authority. Never silently
            // import an adjustment or override the sale total after ledger posting.
            if (row.SourceAmountUsd != Amount(row) || Number("K") != Amount(row) || Number("L") != 0)
                throw new InvalidDataException($"ردیف {i + 1}: مبلغ با وزن × نرخ مطابقت ندارد؛ قبل از ورود اصلاح شود.");
            rows.Add(row);
        }
        if (rows.Count == 0 || rows.Select(r => Token(r.ImportKey)).Distinct().Count() != rows.Count)
            throw new InvalidDataException("ImportKey خالی یا تکراری است؛ تکت مشترک به‌تنهایی تکرار محسوب نمی‌شود.");
        // Shared tickets are warnings on every participating row, never deduplicated.
        var shared = rows.Where(r => r.TicketSerial.Length > 0).GroupBy(r => r.TicketSerial)
            .Where(g => g.Count() > 1).SelectMany(g => g.Select(r => r.ImportKey)).ToHashSet();
        return rows.Select(r => shared.Contains(r.ImportKey) && !r.RequiresReview
            ? r with { ReviewReason = "تکت مشترک؛ فروش تقسیم‌شده را بررسی و جداگانه تأیید کنید." } : r).ToList();
    }

    public static decimal Amount(SalesExcelRow row) => decimal.Round(row.QuantityMt * row.UnitPriceUsd, 4, MidpointRounding.AwayFromZero);
    public static string Digits(string value) => string.Concat(value.Select(c => c is >= '۰' and <= '۹' ? (char)('0' + c - '۰')
        : c is >= '٠' and <= '٩' ? (char)('0' + c - '٠') : c));
    public static DateTime ParseSolar(string value)
    {
        var parts = Digits(value).Split('/');
        if (parts.Length != 3 || !parts.All(p => int.TryParse(p, out _))) throw new InvalidDataException("تاریخ شمسی باید روز/ماه/سال باشد.");
        try
        {
            var day = int.Parse(parts[0]); var month = int.Parse(parts[1]); var year = int.Parse(parts[2]);
            if (year is < 1300 or > 1500) throw new ArgumentOutOfRangeException(nameof(value));
            return DateTime.SpecifyKind(new PersianCalendar().ToDateTime(year, month, day, 0, 0, 0, 0), DateTimeKind.Utc);
        }
        catch (ArgumentOutOfRangeException) { throw new InvalidDataException("تاریخ شمسی معتبر نیست."); }
    }
    public static string Token(string key) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
        System.Text.Encoding.UTF8.GetBytes("Sales.ExcelImport:v1:" + key.Trim().ToUpperInvariant())));
    public static string BuildNotes(SalesExcelRow row, string reviewNote) =>
        $"ImportKey: {row.ImportKey} | قرارداد فایل: {row.ContractNumber} | موتر: {row.TruckNumber} | راننده: {row.DriverName} | {row.Notes}"
        + (string.IsNullOrWhiteSpace(reviewNote) ? "" : " | تأیید بررسی: " + reviewNote.Trim());
}
