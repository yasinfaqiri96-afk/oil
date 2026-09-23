namespace PTGOilSystem.Web.Helpers;

/// <summary>
/// اندازهٔ صفحه در لیست‌ها. تنها نقطهٔ تصمیم‌گیری برای «تعداد سطر در هر صفحه»؛
/// همهٔ کنترلرها از پیش‌فرض مشترک ۱۰ استفاده می‌کنند و کاربر می‌تواند از طریق پارامتر
/// <c>pageSize</c> در کوئری‌استرینگ آن را تغییر دهد.
/// </summary>
public static class ListPageSize
{
    /// <summary>پیش‌فرض مشترک همهٔ لیست‌ها.</summary>
    public const int Default = 10;

    /// <summary>گزینه‌های استاندارد نمایش داده‌شده در انتخابگر.</summary>
    public static readonly int[] Options = { 10, 20, 50, 100, 200, 500 };

    /// <summary>سقف امن برای جلوگیری از بارگذاری کل جدول با یک درخواست.</summary>
    public const int Max = 500;

    public static int Resolve(int? requested, int fallback)
    {
        // پارامتر fallback برای سازگاری با فراخوانی‌های فعلی نگه داشته شده است؛
        // سیاست جدید، پیش‌فرض واحد برای همهٔ لیست‌ها است.
        _ = fallback;
        if (requested is null || requested.Value <= 0)
        {
            return Default;
        }

        return Math.Min(requested.Value, Max);
    }

    /// <summary>گزینه‌های قابل انتخاب = گزینه‌های استاندارد + مقدار جاری.</summary>
    public static IEnumerable<int> OptionsFor(int fallback, int current)
    {
        _ = fallback;
        return Options
            .Concat(new[] { Default, current })
            .Where(v => v > 0 && v <= Max)
            .Distinct()
            .OrderBy(v => v);
    }
}
