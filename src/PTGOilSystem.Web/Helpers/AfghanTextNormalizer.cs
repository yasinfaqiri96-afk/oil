using System.Globalization;
using System.Text;

namespace PTGOilSystem.Web.Helpers;

/// <summary>
/// تنها مرجعِ یکسان‌سازی متنِ افغانی/فارسی برای «مقایسه و تشخیص تکراری».
///
/// چرا لازم است: همان شمارهٔ سند در دادهٔ واقعی به سه شکل نوشته می‌شود —
/// <c>RWB-12345</c> ، <c>RWB-۱۲۳۴۵</c> (ارقام فارسی) و <c>RWB-١٢٣٤٥</c> (ارقام عربی) —
/// و «ی/ي» و «ک/ك» هم بسته به کیبورد فرق می‌کنند. بدون یکسان‌سازی، Unique Index
/// روی کلیدِ ایمپورت بی‌اثر می‌شود و همان واگن دوباره وارد موجودی می‌گردد
/// (PTG-P1-04).
///
/// قاعدهٔ مهم: <b>مقدارِ نمایشی هرگز اینجا تغییر نمی‌کند.</b> این کلاس فقط برای
/// «کلیدِ مقایسه» است. نامِ آدم‌ها و شرحِ اسناد همان‌طور که کاربر نوشته ذخیره و نمایش
/// داده می‌شوند؛ فقط نسخهٔ canonical برای مقایسه ساخته می‌شود.
/// </summary>
public static class AfghanTextNormalizer
{
    // فاصلهٔ مجازی، اتصال‌دهنده، نشانه‌های جهتِ متن و BOM. در متنِ فارسی نامرئی‌اند و
    // با کپی/پیست از اکسل و واتس‌اپ بی‌صدا وارد می‌شوند، پس در کلید نباید بمانند.
    private const char ZeroWidthNonJoiner = '\u200C';
    private const char ZeroWidthJoiner = '\u200D';

    private static bool IsInvisible(char character) => character switch
    {
        ZeroWidthNonJoiner or ZeroWidthJoiner => true,
        '\u200E' or '\u200F' => true,            // LRM / RLM
        '\u202A' or '\u202B' or '\u202C' or '\u202D' or '\u202E' => true, // embedding/override
        '\u2066' or '\u2067' or '\u2068' or '\u2069' => true,             // isolates
        '\uFEFF' => true,                        // BOM
        '\u0640' => true,                        // کشیدهٔ عربی (tatweel) — فقط تزئینی است
        _ => false,
    };

    // اعرابِ عربی: در شمارهٔ سند معنایی ندارد ولی گاهی با کپی وارد می‌شود.
    private static bool IsArabicDiacritic(char character)
        => character >= '\u064B' && character <= '\u0652';

    /// <summary>
    /// ارقام فارسی (U+06F0..U+06F9) و عربی-هندی (U+0660..U+0669) را به ارقام لاتین
    /// تبدیل می‌کند و بقیهٔ متن را دست‌نخورده برمی‌گرداند.
    /// </summary>
    public static char NormalizeDigit(char character) => character switch
    {
        >= '\u06F0' and <= '\u06F9' => (char)('0' + (character - '\u06F0')),
        >= '\u0660' and <= '\u0669' => (char)('0' + (character - '\u0660')),
        _ => character,
    };

    /// <summary>
    /// حروفِ هم‌معنیِ عربی را به شکلِ فارسی/دریِ رایج می‌برد تا «کشتي» و «کشتی» یکی شمرده شوند.
    /// </summary>
    public static char NormalizeLetter(char character) => character switch
    {
        '\u064A' => '\u06CC', // ي عربی  → ی فارسی
        '\u0649' => '\u06CC', // ى الف مقصوره → ی
        '\u06CD' => '\u06CC', // ۍ
        '\u0643' => '\u06A9', // ك عربی → ک فارسی
        '\u0629' => '\u0647', // ة → ه
        '\u06C0' => '\u0647', // ۀ → ه
        '\u0623' or '\u0625' or '\u0622' or '\u0671' => '\u0627', // أ إ آ ٱ → ا
        '\u0624' => '\u0648', // ؤ → و
        '\u0626' => '\u06CC', // ئ → ی
        _ => character,
    };

    /// <summary>
    /// فقط ارقام را لاتین می‌کند (بدون دست‌زدن به حروف یا فاصله). برای خواندنِ عددِ
    /// تایپ‌شده با کیبورد فارسی از فرم یا اکسل.
    /// </summary>
    public static string NormalizeDigits(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            builder.Append(NormalizeDigit(character));
        }

        return builder.ToString();
    }

    /// <summary>
    /// یکسان‌سازیِ «امن برای متنِ خوانا»: شکلِ Unicode، حروفِ عربی، ارقام و کاراکترهای
    /// نامرئی اصلاح و فاصله‌های اضافی یکدست می‌شوند، ولی حروف بزرگ/کوچک دست‌نخورده می‌ماند.
    /// برای مقایسهٔ متن‌های توصیفی (مثل نامِ نوع مصرف) مناسب است، نه برای کلیدِ یکتا.
    /// </summary>
    public static string NormalizeText(string? value) => Normalize(value, upperCase: false);

    /// <summary>
    /// نسخهٔ مقایسه‌ایِ جستجو: مثل <see cref="NormalizeText"/> ولی حروف کوچک می‌شوند.
    /// برای «جستجوی شمارهٔ سند/مرجع» در لیست‌ها.
    /// </summary>
    public static string NormalizeForSearch(string? value)
        => Normalize(value, upperCase: false).ToLowerInvariant();

    /// <summary>
    /// <b>کلیدِ canonical برای هویت/تشخیص تکراری.</b> علاوه بر یکسان‌سازیِ بالا،
    /// فاصله‌های اضافی حذف و حروف لاتین بزرگ می‌شوند.
    ///
    /// عمداً خط تیره/اسلش حذف نمی‌شود: <c>RWB-123</c> و <c>RWB123</c> در دادهٔ واقعی
    /// می‌توانند دو سند باشند، و حذفِ آن‌ها کلیدِ ردیف‌های قبلاً ثبت‌شده را عوض می‌کرد.
    /// برای متنِ کاملاً لاتین، خروجی دقیقاً همان رفتارِ قبلی است (سازگاریِ عقب‌رو).
    /// </summary>
    public static string CanonicalKey(string? value) => Normalize(value, upperCase: true);

    private static string Normalize(string? value, bool upperCase)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        // FormKC شکل‌های نمایشیِ عربی (presentation forms) و کاراکترهای عرض-کامل را به
        // شکلِ استاندارد می‌برد؛ بدون آن «ﻣ» و «م» دو کاراکتر متفاوت می‌مانند.
        var source = value.Normalize(NormalizationForm.FormKC);

        var builder = new StringBuilder(source.Length);
        var pendingSpace = false;
        foreach (var raw in source.Trim())
        {
            if (IsInvisible(raw) || IsArabicDiacritic(raw))
            {
                continue;
            }

            if (char.IsWhiteSpace(raw))
            {
                pendingSpace = true;
                continue;
            }

            if (pendingSpace && builder.Length > 0)
            {
                builder.Append(' ');
            }

            pendingSpace = false;

            var character = NormalizeLetter(NormalizeDigit(raw));
            builder.Append(upperCase ? char.ToUpperInvariant(character) : character);
        }

        return builder.ToString();
    }

    /// <summary>
    /// عددِ اعشاری‌ای که با ارقام فارسی/عربی یا جداکنندهٔ عربی (٫ / ٬) نوشته شده را می‌خواند.
    /// </summary>
    public static bool TryParseDecimal(string? value, out decimal result)
    {
        result = 0m;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = NormalizeDigits(value.Trim())
            .Replace('\u066B', '.')   // جداکنندهٔ اعشار عربی
            .Replace("\u066C", string.Empty) // جداکنندهٔ هزارگان عربی
            .Replace("\u060C", string.Empty) // ویرگول عربی
            .Replace(",", string.Empty)
            .Replace("\u200F", string.Empty)
            .Replace("\u200E", string.Empty);

        return decimal.TryParse(normalized, NumberStyles.Any, CultureInfo.InvariantCulture, out result);
    }
}
