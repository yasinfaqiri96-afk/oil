using Microsoft.AspNetCore.Mvc.Filters;

namespace PTGOilSystem.Web.Filters;

/// <summary>
/// در فهرست‌ها <c>page &lt;= 0</c> یعنی «همهٔ سطرها بدون صفحه‌بندی»؛ این قرارداد فقط برای
/// اکشن‌های Export است که متد Index را مستقیماً در کد صدا می‌زنند. چون <c>page</c> از
/// کوئری‌استرینگ هم بایند می‌شود، هر کاربری می‌توانست با <c>?page=0</c> صفحه‌بندی را دور
/// بزند و کل جدول را در یک درخواست بخواند.
///
/// این فیلتر فقط روی درخواست‌های واقعی HTTP اجرا می‌شود، پس مقدار آمده از کوئری‌استرینگ را
/// به بازهٔ مجاز برمی‌گرداند؛ صدا زدن مستقیمِ Index از داخل Export از pipeline رد نمی‌شود و
/// دست‌نخورده می‌ماند. خروجی Export دقیقاً مثل قبل کامل است.
/// </summary>
public sealed class ListPageBoundsFilter : IActionFilter
{
    private const string PageArgumentName = "page";

    public void OnActionExecuting(ActionExecutingContext context)
    {
        if (!context.ActionArguments.TryGetValue(PageArgumentName, out var value))
        {
            return;
        }

        if (value is int page && page < 1)
        {
            context.ActionArguments[PageArgumentName] = 1;
        }
    }

    public void OnActionExecuted(ActionExecutedContext context)
    {
    }
}
