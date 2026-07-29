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
    /// <param name="occurrences">
    /// شمارندهٔ خطوطِ همان فایل. یک واگن می‌تواند در یک سند به چند خط بارگیری با مقدارهای
    /// متفاوت شکسته شود (مثلاً ۳۳.۰۴۹ + ۳.۹ + ۲۱.۶ روی یک واگن)؛ بدون شمارنده هر سه خط یک
    /// کلید می‌سازند و Unique Index فقط یکی را می‌پذیرد. null یعنی رفتار قبلی: فقط کلید پایه.
    /// </param>
    /// <param name="quantityMt">
    /// مقدار همان خط. تمایز بر پایهٔ مقدار است نه ترتیب: دو خط با مقدار یکسان همچنان یک کلید
    /// می‌گیرند و یکی ثبت می‌شود (ردیف تکراری فایل)، ولی مقدار متفاوت کلید جدا می‌گیرد.
    /// </param>
    public static string? Build(
        int contractId,
        string? documentNumber,
        string? transportNumber,
        DateTime loadingDate,
        OccurrenceTracker? occurrences = null,
        decimal quantityMt = 0m)
    {
        var document = Normalize(documentNumber);
        var transport = Normalize(transportNumber);
        if (document.Length == 0 && transport.Length == 0)
        {
            return null;
        }

        var baseKey = document.Length > 0
            ? $"{contractId}|{document}|{transport}"
            : $"{contractId}||{transport}|{loadingDate:yyyyMMdd}";

        if (occurrences is null)
        {
            return baseKey;
        }

        // مقدار اولِ هر کلید پایه عمداً پسوند نمی‌گیرد تا کلید ردیف‌های قبلاً ثبت‌شده عوض نشود.
        var occurrence = occurrences.Next(baseKey, quantityMt);
        return occurrence <= 1 ? baseKey : $"{baseKey}#{occurrence}";
    }

    /// <summary>
    /// برای هر کلید پایه نگه می‌دارد که تا حالا چه مقدارهایی دیده شده و هرکدام چه شماره‌ای گرفته‌اند.
    /// برای هر فایل یک نمونهٔ تازه بساز و ردیف‌ها را به ترتیب فایل بده؛ ایمپورت دوبارهٔ همان فایل
    /// دقیقاً همان کلیدها را می‌سازد، پس تکراری بودن همچنان تشخیص داده می‌شود.
    /// </summary>
    public sealed class OccurrenceTracker
    {
        private readonly Dictionary<string, Dictionary<decimal, int>> _byBaseKey = new(StringComparer.Ordinal);

        /// <summary>
        /// شمارهٔ این مقدار زیر همان کلید پایه: مقدار تکراری همان شمارهٔ قبلی را می‌گیرد،
        /// مقدار تازه شمارهٔ بعدی را.
        /// </summary>
        public int Next(string baseKey, decimal quantityMt)
        {
            if (!_byBaseKey.TryGetValue(baseKey, out var quantities))
            {
                quantities = [];
                _byBaseKey[baseKey] = quantities;
            }

            if (quantities.TryGetValue(quantityMt, out var existing))
            {
                return existing;
            }

            var occurrence = quantities.Count + 1;
            quantities[quantityMt] = occurrence;
            return occurrence;
        }
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
