using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace PTGOilSystem.Web.Security;

public sealed class RoleNavigationAuthorizationFilter : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var user = context.HttpContext.User;
        if (user.Identity?.IsAuthenticated != true)
        {
            await next();
            return;
        }

        var controller = context.RouteData.Values["controller"]?.ToString();
        if (string.Equals(controller, "Auth", StringComparison.OrdinalIgnoreCase))
        {
            await next();
            return;
        }

        // «راهنمای هوشمند» یک صفحهٔ قابل‌ناوبری نیست و کلید ناوبری ندارد، پس این فیلتر
        // آن را برای همه — حتی Admin — به AccessDenied می‌فرستاد. برای هر کاربر واردشده
        // باز است؛ دسترسی واقعی همچنان با [Authorize] روی خودِ Controller کنترل می‌شود.
        if (string.Equals(controller, "Assistant", StringComparison.OrdinalIgnoreCase))
        {
            await next();
            return;
        }

        if (RoleAccessRules.CanAccessController(user, controller))
        {
            await next();
            return;
        }

        context.Result = new RedirectToActionResult("AccessDenied", "Auth", null);
    }
}
