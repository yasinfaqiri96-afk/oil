namespace PTGOilSystem.Web.Helpers;

/// <summary>تاریخِ یک سند نسبت به «امروزِ کاری کابل».</summary>
public enum TransactionDateKind
{
    Today = 0,
    Backdated = 1,
    FutureDated = 2,
}

/// <summary>
/// PTG-P3-A — «این سند تاریخِ گذشته/آینده دارد» باید دیده شود.
///
/// تاریخِ گذشته به‌خودیِ‌خود ممنوع نیست و این کلاس چیزی را مسدود نمی‌کند؛ اجرا با
/// نگهبانِ موجودی (PTG-P0-02) و قفلِ دوره (PTG-P1-01) است. کاری که اینجا انجام می‌شود
/// فقط برچسب‌زدن است، چون در گزارش دیده شد یک اشتباهِ تاریخ ماه‌ها بعد کشف می‌شود.
///
/// عمداً هیچ دسترسی‌ای به دیتابیس یا ساعت سیستم ندارد: «امروز» ورودی است، تا هم
/// ساعتِ کاری کابل مبنا بماند و هم رفتار قابل تست باشد.
/// </summary>
public static class TransactionDateNotice
{
    public static TransactionDateKind Classify(DateTime transactionDate, DateTime today)
    {
        var date = transactionDate.Date;
        var reference = today.Date;

        if (date > reference)
        {
            return TransactionDateKind.FutureDated;
        }

        return date < reference ? TransactionDateKind.Backdated : TransactionDateKind.Today;
    }

    /// <summary>برچسب کوتاه برای کنارِ تاریخ. سندِ امروز برچسب نمی‌گیرد (نویز نمی‌سازد).</summary>
    public static string? Badge(TransactionDateKind kind) => kind switch
    {
        TransactionDateKind.Backdated => "تاریخ گذشته",
        TransactionDateKind.FutureDated => "تاریخ آینده",
        _ => null,
    };

    /// <summary>هشدارِ پیش از ثبت. تعداد روز اختلاف گفته می‌شود تا اشتباه تایپی زود دیده شود.</summary>
    public static string? Warning(DateTime transactionDate, DateTime today)
    {
        var kind = Classify(transactionDate, today);
        var days = Math.Abs((transactionDate.Date - today.Date).Days);

        return kind switch
        {
            TransactionDateKind.Backdated =>
                $"تاریخ این سند {days:N0} روز پیش از امروز است. اگر عمدی نیست، تاریخ را بررسی کنید.",
            TransactionDateKind.FutureDated =>
                $"تاریخ این سند {days:N0} روز بعد از امروز است. اگر عمدی نیست، تاریخ را بررسی کنید.",
            _ => null,
        };
    }
}
