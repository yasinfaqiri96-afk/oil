using Microsoft.AspNetCore.Http;

namespace PTGOilSystem.Web.Helpers;

/// <summary>
/// سوییچ «خلاصه / تفصیلی» برای جدول‌های بلندِ صفحات جزئیات طرف‌حساب.
/// پیش‌فرض خلاصه است و انتخاب کاربر با پارامتر <c>listView</c> در همان URL نگه داشته
/// می‌شود تا تب و بقیهٔ پارامترها (مثل <c>showAll</c>) از دست نروند.
/// فقط نمایش است: هیچ کوئری، مبلغ یا جمعی به آن وابسته نیست.
/// </summary>
public static class DetailSummaryView
{
    private const string QueryKey = "listView";

    /// <summary>کاربر برای این جدول نمای تفصیلی (دانه‌دانه) را انتخاب کرده است.</summary>
    public static bool IsDetailed(HttpRequest request, string key)
        => string.Equals(request.Query[QueryKey].ToString(), key, StringComparison.OrdinalIgnoreCase);

    /// <summary>همان URL با نمای تفصیلیِ این جدول (یا بدون آن برای بازگشت به خلاصه).</summary>
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
