using Microsoft.AspNetCore.Mvc.Filters;
using PTGOilSystem.Web.Security;

namespace PTGOilSystem.Web.Services.OperationalPeriod;

/// <summary>
/// PTG فاز ۹ — «ثبتِ استثنایی در دورهٔ بسته»، به‌صورتِ درخواستِ صریح و یک‌بارمصرف.
///
/// <b>مسئله:</b> قفلِ دوره درست کار می‌کند، ولی وقتی مدیرِ مالی <i>واقعاً</i> باید سندِ
/// جامانده را در ماهِ بسته ثبت کند، هیچ راهی جز خاموش‌کردنِ قفل نداشت — و کلیدِ خاموشِ
/// دائمی همان چیزی است که قفل را بی‌معنا می‌کند.
///
/// <b>راه‌حل:</b> دو فیلدِ اختیاری در همان فرم. اگر کاربر تیک بزند و دلیل بنویسد، و
/// دسترسیِ <see cref="AppPermissions.PostToClosedOperationalPeriod"/> داشته باشد، عبور
/// فقط برای <b>همین یک درخواست</b> باز می‌شود.
///
/// چهار قاعده که این را از «سوییچِ بایپس» جدا می‌کند:
/// <list type="number">
///   <item>بدون دسترسیِ مخصوص، فیلدها اصلاً نمایش داده نمی‌شوند و اگر دستی هم فرستاده
///         شوند نادیده گرفته می‌شوند — قفل سر جایش می‌ماند.</item>
///   <item>دلیل اجباری است؛ تیکِ خالی یعنی درخواستی نبوده.</item>
///   <item>دامنه یک درخواست است: پرچم روی همان <c>DbContext</c>ِ Scoped نوشته می‌شود و با
///         پایانِ درخواست از بین می‌رود. هیچ چیزی ذخیره یا تمدید نمی‌شود.</item>
///   <item>هر عبور یک سطرِ Audit با کاربر، تاریخ، مسیر و دلیل می‌سازد.</item>
/// </list>
///
/// خودِ تصمیم این‌جا گرفته نمی‌شود: فیلتر فقط درخواستِ فرم را می‌خواند و به
/// <see cref="IOperationalPeriodGuard"/> می‌دهد — همان جایی که از قبل قاعده را می‌دانست.
/// </summary>
public sealed class ClosedPeriodOverrideFilter(
    IOperationalPeriodGuard guard,
    ICurrentUserContext currentUser) : IAsyncActionFilter
{
    /// <summary>نامِ فیلدِ تیک. عمداً با <c>__</c> شروع می‌شود تا با فیلدهای مدل قاطی نشود.</summary>
    public const string RequestedField = "__closedPeriodOverride";

    /// <summary>نامِ فیلدِ دلیل.</summary>
    public const string ReasonField = "__closedPeriodOverrideReason";

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var request = context.HttpContext.Request;

        // فقط فرم‌های POST. هیچ درخواستِ خواندنی نمی‌تواند عبور بگیرد.
        if (HttpMethods.IsPost(request.Method) && request.HasFormContentType)
        {
            var requested = IsChecked(request.Form[RequestedField]);
            var reason = request.Form[ReasonField].ToString().Trim();

            if (requested && !string.IsNullOrWhiteSpace(reason))
            {
                // اگر دسترسی نباشد، Guard خودش استثنا می‌اندازد و فیلترِ ترجمهٔ خطا آن را
                // به پیامِ دری روی همان فرم تبدیل می‌کند.
                await guard.ApproveOverrideAsync(
                    new ClosedPeriodOverride(currentUser.Principal, currentUser.UserId, reason),
                    request.Path.Value ?? "-",
                    context.HttpContext.RequestAborted);
            }
        }

        await next();
    }

    /// <summary>چک‌باکسِ Razor مقدارِ <c>true,false</c> می‌فرستد؛ فقط «روشن» شمرده می‌شود.</summary>
    private static bool IsChecked(Microsoft.Extensions.Primitives.StringValues values)
        => values.Any(value =>
            string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "on", StringComparison.OrdinalIgnoreCase)
            || value == "1");
}
