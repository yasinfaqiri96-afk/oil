using PTGOilSystem.Web.Helpers;

namespace PTGOilSystem.Web.Services.OperationalPeriod;

/// <summary>
/// «دورهٔ مالی این تاریخ بسته است». عمداً یک استثنای اختصاصی است تا هر مسیر ثبتی بتواند
/// آن را بگیرد و پیام فارسی را روی فرم بگذارد، به‌جای خطای ۵۰۰.
/// </summary>
public sealed class OperationalPeriodLockedException(string message, DateTime lockedThroughDate)
    : InvalidOperationException(message)
{
    public DateTime LockedThroughDate { get; } = lockedThroughDate;
}

public static partial class OperationalPeriodScope
{
    public static string BuildMessage(string documentKind, DateTime transactionDate, DateTime lockedThroughDate)
        => $"دوره مالی این تاریخ بسته شده است و ثبت یا تغییر سند در این دوره مجاز نیست. "
           + $"({documentKind} به تاریخ {transactionDate.ToCalendarString("yyyy-MM-dd")}؛ دوره تا {lockedThroughDate.ToCalendarString("yyyy-MM-dd")} بسته است.)";
}
