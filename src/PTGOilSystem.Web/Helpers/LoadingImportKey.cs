using System.Globalization;
using System.Text;

namespace PTGOilSystem.Web.Helpers;

/// <summary>
/// کلید یکتای تشخیص «همان بارگیری» داخل یک قرارداد.
/// اولویت با شمارهٔ سند بارگیری (Bill of Lading / RWB / CMR) است و شمارهٔ واگن یا موتر
/// بررسی را کامل می‌کند؛ در نبود شمارهٔ سند، شمارهٔ حمل + تاریخ بارگیری جای آن را می‌گیرد.
/// تکراری‌بودن فقط داخل همان قرارداد معنا دارد، پس ContractId همیشه جزء کلید است.
/// </summary>
public static class LoadingImportKey
{
    /// <summary>
    /// کلید را می‌سازد؛ اگر نه شمارهٔ سند و نه شمارهٔ حمل موجود نباشد null برمی‌گرداند
    /// (چنین ردیفی هویت قابل‌اتکایی برای مقایسه ندارد و مثل قبل ثبت می‌شود).
    /// </summary>
    public static string? Build(int contractId, string? documentNumber, string? transportNumber, DateTime loadingDate)
    {
        var document = Normalize(documentNumber);
        var transport = Normalize(transportNumber);
        if (document.Length == 0 && transport.Length == 0)
        {
            return null;
        }

        return document.Length > 0
            ? $"{contractId}|{document}|{transport}"
            : $"{contractId}||{transport}|{loadingDate:yyyyMMdd}";
    }

    /// <summary>حذف فاصله‌های اضافی و یکسان‌سازی حروف بزرگ و کوچک پیش از مقایسه.</summary>
    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        var previousWasSpace = false;
        foreach (var character in value.Trim())
        {
            if (char.IsWhiteSpace(character))
            {
                previousWasSpace = true;
                continue;
            }

            if (previousWasSpace && builder.Length > 0)
            {
                builder.Append(' ');
            }

            previousWasSpace = false;
            builder.Append(char.ToUpperInvariant(character));
        }

        return builder.ToString();
    }

    /// <summary>مقایسهٔ مقدار/قیمت دو بارگیری با تحمل گِرد‌کردن، برای تشخیص «دارای اختلاف».</summary>
    public static bool ValuesMatch(decimal? left, decimal? right)
    {
        var leftValue = left.GetValueOrDefault();
        var rightValue = right.GetValueOrDefault();
        return Math.Abs(leftValue - rightValue) <= 0.0001m;
    }

    internal static string Describe(decimal? value)
        => value.HasValue ? value.Value.ToString("0.####", CultureInfo.InvariantCulture) : "—";
}
