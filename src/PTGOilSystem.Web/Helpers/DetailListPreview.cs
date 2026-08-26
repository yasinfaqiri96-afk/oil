using Microsoft.AspNetCore.Http;

namespace PTGOilSystem.Web.Helpers;

/// <summary>
/// «۱۰ مورد اخیر + مشاهده همه» برای جدول‌های بلندِ صفحات جزئیات طرف‌حساب.
/// فقط نمایش را کوتاه می‌کند: هیچ کوئری، مبلغ یا جمعی تغییر نمی‌کند و اعدادِ سرصفحه
/// همچنان روی کل داده محاسبه می‌شوند. باز/بستهٔ هر جدول با پارامتر <c>showAll</c>
/// در همان URL نگه داشته می‌شود تا تب و فیلترهای صفحه از دست نروند.
/// </summary>
public static class DetailListPreview
{
    public const int DefaultLimit = 10;
    private const string QueryKey = "showAll";

    public static bool IsExpanded(HttpRequest request, string key)
        => string.Equals(request.Query[QueryKey].ToString(), key, StringComparison.OrdinalIgnoreCase);

    /// <summary>آخرین N مورد؛ اگر کاربر «مشاهده همه» را زده باشد، کل لیست.</summary>
    public static IReadOnlyList<T> Take<T>(
        HttpRequest request,
        string key,
        IReadOnlyList<T> items,
        int limit = DefaultLimit)
        => IsExpanded(request, key) || items.Count <= limit
            ? items
            : items.Take(limit).ToList();

    /// <summary>همان URL با <c>showAll</c>ِ این جدول (یا بدون آن برای بستن).</summary>
    public static string Url(HttpRequest request, string? key)
    {
        var parts = new List<string>();
        foreach (var pair in request.Query)
        {
            if (string.Equals(pair.Key, QueryKey, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var value in pair.Value)
            {
                parts.Add($"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(value ?? string.Empty)}");
            }
        }

        if (!string.IsNullOrEmpty(key))
        {
            parts.Add($"{QueryKey}={Uri.EscapeDataString(key)}");
        }

        var query = parts.Count == 0 ? string.Empty : "?" + string.Join("&", parts);
        return $"{request.Path}{query}";
    }
}
