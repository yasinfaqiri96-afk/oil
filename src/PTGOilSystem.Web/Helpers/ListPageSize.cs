namespace PTGOilSystem.Web.Helpers;

/// <summary>
/// اندازهٔ صفحه در لیست‌ها. تنها نقطهٔ تصمیم‌گیری برای «تعداد سطر در هر صفحه»؛
/// کنترلرها مقدار پیش‌فرض خود را می‌دهند و کاربر می‌تواند از طریق پارامتر
/// <c>pageSize</c> در کوئری‌استرینگ آن را تغییر دهد.
/// </summary>
public static class ListPageSize
{
    /// <summary>گزینه‌های استاندارد نمایش داده‌شده در انتخابگر.</summary>
    public static readonly int[] Options = { 10, 20, 50, 100, 200 };

    /// <summary>سقف امن برای جلوگیری از بارگذاری کل جدول با یک درخواست.</summary>
    public const int Max = 200;

    public static int Resolve(int? requested, int fallback)
    {
        if (requested is null || requested.Value <= 0)
        {
            return fallback;
        }

        return Math.Min(requested.Value, Max);
    }

    /// <summary>گزینه‌های قابل انتخاب = گزینه‌های استاندارد + پیش‌فرض صفحه + مقدار جاری.</summary>
    public static IEnumerable<int> OptionsFor(int fallback, int current)
        => Options
            .Concat(new[] { fallback, current })
            .Where(v => v > 0 && v <= Max)
            .Distinct()
            .OrderBy(v => v);
}
