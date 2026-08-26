using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ExcelDataReader;

namespace PartnerSettlementImport;

/// <summary>سمتِ ستونِ مبلغ در فایل مبدأ. جهتِ تسویه فقط از همین تعیین می‌شود، نه از متنِ شرح.</summary>
public enum SourceColumn
{
    TCredit = 1,
    TDebit = 2
}

/// <summary>یک ردیفِ معنادارِ فایل مبدأ. هیچ مبلغی اینجا ساخته نمی‌شود؛ همه از خودِ فایل خوانده می‌شود.</summary>
public sealed record SettlementSourceRow(
    int RowNumber,
    string JalaliDate,
    DateTime SettlementDate,
    string Description,
    decimal Amount,
    SourceColumn Column,
    string? SourceNote);

/// <summary>
/// خوانندهٔ فایلِ پرداخت‌های بین دو شریک. تاریخِ جلالی مبنا است چون سلول‌های میلادیِ فایل
/// جابه‌جا (day/month) ثبت شده‌اند. ستونِ بعد از Balance فقط به‌عنوان یادداشتِ مبدأ نگه داشته
/// می‌شود و هرگز مبلغِ تسویه نیست.
/// </summary>
public static class SettlementSourceReader
{
    private static readonly Regex JalaliPattern =
        new(@"^\s*(\d{1,2})/(\d{1,2})/(1[34]\d{2})\s*$", RegexOptions.Compiled);

    public static IReadOnlyList<SettlementSourceRow> Read(Stream stream)
    {
        var grid = ReadGrid(stream);
        var headerIndex = grid.FindIndex(r => Find(r, "T-Credit") >= 0 && Find(r, "T-Debit") >= 0);
        if (headerIndex < 0)
        {
            throw new InvalidOperationException("سطر عنوان با ستون‌های T-Credit/T-Debit پیدا نشد.");
        }

        var header = grid[headerIndex];
        var noCol = Find(header, "No");
        var detailsCol = Find(header, "Details");
        var creditCol = Find(header, "T-Credit");
        var debitCol = Find(header, "T-Debit");
        var balanceCol = Find(header, "Balance");

        if (noCol < 0 || detailsCol < 0)
        {
            throw new InvalidOperationException("ستون‌های لازم (No/Details) در فایل نیست.");
        }

        var rows = new List<SettlementSourceRow>();
        for (var r = headerIndex + 1; r < grid.Count; r++)
        {
            var row = grid[r];

            var rowNumber = ReadInt(At(row, noCol));
            if (rowNumber is null)
            {
                // سطرِ جمع یا سطرِ خالی؛ تسویه‌ای ندارد.
                continue;
            }

            var credit = ReadDecimal(At(row, creditCol));
            var debit = ReadDecimal(At(row, debitCol));
            if (credit is null && debit is null)
            {
                continue;
            }

            if (credit is not null && debit is not null)
            {
                throw new InvalidOperationException(
                    $"ردیف {rowNumber}: هم T-Credit و هم T-Debit مقدار دارد؛ جهتِ تسویه مبهم است.");
            }

            var amount = credit ?? debit!.Value;
            if (amount <= 0m)
            {
                throw new InvalidOperationException($"ردیف {rowNumber}: مبلغ باید بزرگ‌تر از صفر باشد.");
            }

            var jalali = FindJalali(row);
            if (jalali is null)
            {
                throw new InvalidOperationException($"ردیف {rowNumber}: تاریخ جلالی خوانده نشد.");
            }

            rows.Add(new SettlementSourceRow(
                RowNumber: rowNumber.Value,
                JalaliDate: jalali.Value.Text,
                SettlementDate: jalali.Value.Date,
                Description: ReadText(At(row, detailsCol))?.Trim() ?? string.Empty,
                Amount: amount,
                Column: credit is not null ? SourceColumn.TCredit : SourceColumn.TDebit,
                SourceNote: FindNote(row, balanceCol)));
        }

        return rows;
    }

    public static DateTime JalaliToGregorian(int day, int month, int year)
        => new PersianCalendar().ToDateTime(year, month, day, 0, 0, 0, 0).Date;

    private static List<object?[]> ReadGrid(Stream stream)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        using var reader = ExcelReaderFactory.CreateReader(stream);

        var grid = new List<object?[]>();
        while (reader.Read())
        {
            var row = new object?[reader.FieldCount];
            for (var c = 0; c < reader.FieldCount; c++)
            {
                row[c] = reader.GetValue(c);
            }

            grid.Add(row);
        }

        return grid;
    }

    private static object? At(object?[] row, int index)
        => index >= 0 && index < row.Length ? row[index] : null;

    private static (string Text, DateTime Date)? FindJalali(object?[] row)
    {
        foreach (var cell in row)
        {
            var text = ReadText(cell);
            if (text is null)
            {
                continue;
            }

            var match = JalaliPattern.Match(text);
            if (!match.Success)
            {
                continue;
            }

            var day = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            var month = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
            var year = int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
            return (text.Trim(), JalaliToGregorian(day, month, year));
        }

        return null;
    }

    private static string? FindNote(object?[] row, int balanceCol)
    {
        if (balanceCol < 0)
        {
            return null;
        }

        for (var c = balanceCol + 1; c < row.Length; c++)
        {
            var text = ReadText(row[c]);
            if (text is not null)
            {
                return text.Trim();
            }
        }

        return null;
    }

    private static int Find(object?[] header, string title)
    {
        for (var c = 0; c < header.Length; c++)
        {
            if (string.Equals(ReadText(header[c])?.Trim(), title, StringComparison.OrdinalIgnoreCase))
            {
                return c;
            }
        }

        return -1;
    }

    private static string? ReadText(object? value)
    {
        if (value is null)
        {
            return null;
        }

        var text = value as string ?? Convert.ToString(value, CultureInfo.InvariantCulture);
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static int? ReadInt(object? value) => value switch
    {
        null => null,
        double d => (int)d,
        int i => i,
        string s when int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
        _ => null
    };

    private static decimal? ReadDecimal(object? value) => value switch
    {
        null => null,
        double d => (decimal)d,
        decimal m => m,
        int i => i,
        string s when decimal.TryParse(s.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed) => parsed,
        _ => null
    };
}
