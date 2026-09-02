using System.Globalization;

namespace PTGOilSystem.Web.Helpers;

/// <summary>
/// PTG-P2-02 — کلید یکتای «همان سطرِ مصرف» برای ایمپورت اکسل.
///
/// چرا لازم است: بدون آن، «ورودِ فقط سطرهای سالم» ناامن بود. کاربر فایلی با ۱۰۰۰ سطر
/// می‌فرستد، ۹۸۰ سطر ثبت می‌شود، ۲۰ سطر را اصلاح می‌کند و <b>همان فایل</b> را دوباره
/// می‌فرستد؛ بدون کلید، آن ۹۸۰ سطر بار دوم هم ثبت می‌شدند و مصارف دو برابر می‌شد.
///
/// کلید از همان چیزی ساخته می‌شود که یک سطرِ مصرف را در فایل یکتا می‌کند: نوع مصرف،
/// تاریخ، مبلغ، ارز، قرارداد و شرح. متن با <see cref="AfghanTextNormalizer"/> canonical
/// می‌شود، پس «۱۲۳» فارسی و «123» لاتین یک سطر شمرده می‌شوند — همان قاعدهٔ P1-04.
///
/// مثل <see cref="LoadingImportKey"/>، سطرهای واقعاً تکراریِ داخل یک فایل (مثلاً دو
/// پرداختِ جدا با همان مبلغ در همان روز) با شمارنده از هم جدا می‌شوند تا هر دو ثبت شوند.
/// </summary>
public static class ExpenseImportKey
{
    /// <summary>
    /// کلید سطر. <c>null</c> یعنی این سطر هویتِ قابل‌اتکایی ندارد (تاریخ یا مبلغ یا نوع
    /// مصرف ندارد) و مثل قبل بدون محافظِ تکراری ثبت می‌شود.
    /// </summary>
    public static string? Build(
        int? expenseTypeId,
        DateTime? expenseDate,
        decimal? amount,
        string? currency,
        int? contractId,
        string? description,
        OccurrenceTracker? occurrences = null)
    {
        if (expenseTypeId is not > 0 || expenseDate is null || amount is null)
        {
            return null;
        }

        var baseKey = string.Join(
            '|',
            expenseTypeId.Value.ToString(CultureInfo.InvariantCulture),
            expenseDate.Value.Date.ToString("yyyyMMdd", CultureInfo.InvariantCulture),
            amount.Value.ToString("0.####", CultureInfo.InvariantCulture),
            AfghanTextNormalizer.CanonicalKey(currency),
            (contractId ?? 0).ToString(CultureInfo.InvariantCulture),
            AfghanTextNormalizer.CanonicalKey(description));

        if (occurrences is null)
        {
            return baseKey;
        }

        // سطر اولِ هر کلید عمداً پسوند نمی‌گیرد تا کلیدِ سطرهای قبلاً ثبت‌شده عوض نشود.
        var occurrence = occurrences.Next(baseKey);
        return occurrence <= 1 ? baseKey : $"{baseKey}#{occurrence}";
    }

    /// <summary>
    /// شمارندهٔ تکرارِ یک کلید داخل یک فایل. برای هر فایل یک نمونهٔ تازه بساز و سطرها را به
    /// ترتیبِ فایل بده؛ ایمپورتِ دوبارهٔ همان فایل دقیقاً همان کلیدها را تولید می‌کند، پس
    /// تکراری‌بودن همچنان تشخیص داده می‌شود.
    /// </summary>
    public sealed class OccurrenceTracker
    {
        private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);

        public int Next(string baseKey)
        {
            _counts.TryGetValue(baseKey, out var seen);
            seen++;
            _counts[baseKey] = seen;
            return seen;
        }
    }
}
