using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Http;

namespace PTGOilSystem.Web.Helpers;

/// <summary>
/// خواندن مقدارهای اعمال‌شدهٔ یک فیلتر از query string برای نوار جستجوی مشترک
/// (<c>_AkSearchFilter.cshtml</c>).
///
/// فیلتر چندانتخابی همان نام پارامتر را تکرار می‌کند (<c>key=1&amp;key=2</c>)؛
/// بنابراین «مقدار اعمال‌شده» یک فهرست است، نه یک رشته. قرارداد درخواست عوض
/// نمی‌شود: همان نام پارامترِ قبلی، فقط با امکان تکرار. منطق OR بین مقادیرِ یک
/// فیلتر و AND بین فیلترهای مختلف، در همان Where های موجود کنترلر می‌ماند.
/// </summary>
public static class AkFilterQuery
{
    /// <summary>همهٔ مقدارهای غیرخالیِ ثبت‌شده برای این پارامتر در نشانی جاری.</summary>
    public static IReadOnlyList<string> Values(HttpContext? context, string key)
    {
        if (context is null || string.IsNullOrEmpty(key)) return Array.Empty<string>();

        return context.Request.Query[key]
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }
}

/// <summary>
/// کمک‌کنندهٔ فیلترهای چندانتخابی در سمت سرور.
/// </summary>
public static class MultiFilterValues
{
    /// <summary>
    /// وقتی دقیقاً یک مقدار انتخاب شده همان را برمی‌گرداند، وگرنه null.
    /// برای جاهایی که یک «انتخابِ جاری» تکی لازم است (لیست‌های کمکی، عنوان دامنه،
    /// پیوندها) و نه خودِ فیلتر.
    /// </summary>
    public static T? Only<T>(this T[]? values) where T : struct
        => values is { Length: 1 } ? values[0] : null;

    /// <inheritdoc cref="Only{T}(T[])"/>
    public static T? Only<T>(this IReadOnlyList<T>? values) where T : struct
        => values is { Count: 1 } ? values[0] : null;

    /// <summary>نسخهٔ رشته‌ای <see cref="Only{T}(T[])"/>.</summary>
    public static string? OnlyText(this string[]? values)
        => values is { Length: 1 } ? values[0] : null;
}
