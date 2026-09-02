using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace PTGOilSystem.Web.Services.OperationalPeriod;

/// <summary>
/// PTG-P1-01 / PTG-P1-05 / PTG-P3-C — واپسین مترجمِ خطا به زبانِ کاربر.
///
/// سه خطای زیر «تقصیر برنامه» نیستند؛ قاعدهٔ کسب‌وکارند و کاربر باید بفهمد چه شد:
///   • ثبت در دورهٔ بسته،
///   • برخوردِ هم‌زمانی (کاربر دیگری همان رکورد را عوض کرده),
///   • خطای محدودیتِ دیتابیس.
/// بدون این فیلتر هر سه به صفحهٔ «خطای سرور» می‌رسیدند و متنِ فنی (نام Constraint،
/// PostgresException، Stack trace) به کاربر افغان نشان داده می‌شد.
///
/// جزئیاتِ فنی همچنان لاگ می‌شود؛ فقط از UI بیرون می‌رود.
///
/// عمداً هیچ قاعده‌ای اینجا تصمیم‌گیری نمی‌شود: تصمیم را <see cref="OperationalPeriodGuard"/>
/// و خودِ دیتابیس گرفته‌اند و اینجا فقط ترجمه و هدایت است.
/// </summary>
public sealed class BusinessRuleExceptionFilter(ILogger<BusinessRuleExceptionFilter> logger) : IExceptionFilter
{
    internal const string ConcurrencyMessage =
        "این رکورد توسط کاربر دیگری تغییر کرده است. اطلاعات جدید را دوباره باز کنید.";

    internal const string DatabaseRuleMessage =
        "این ثبت با یکی از قواعد دیتابیس سازگار نیست و انجام نشد. لطفاً اطلاعات را بررسی کنید یا با پشتیبانی تماس بگیرید.";

    public void OnException(ExceptionContext context)
    {
        var message = Translate(context.Exception);
        if (message is null)
        {
            return;
        }

        // متنِ فنی فقط در لاگ می‌ماند.
        logger.LogWarning(
            context.Exception,
            "Business rule rejected a write on {Path}.",
            context.HttpContext.Request.Path.Value);

        context.ExceptionHandled = true;

        if (WantsJson(context))
        {
            context.Result = new BadRequestObjectResult(new { ok = false, error = message });
            return;
        }

        var tempData = context.HttpContext.RequestServices
            .GetService<Microsoft.AspNetCore.Mvc.ViewFeatures.ITempDataDictionaryFactory>()
            ?.GetTempData(context.HttpContext);
        if (tempData is not null)
        {
            tempData["err"] = message;
        }

        // بازگشت به همان صفحه‌ای که کاربر آمده بود؛ اگر معلوم نبود، صفحهٔ خانه.
        var referer = context.HttpContext.Request.Headers.Referer.ToString();
        context.Result = string.IsNullOrWhiteSpace(referer)
            ? new RedirectToActionResult("Index", "Home", null)
            : new RedirectResult(referer);
    }

    internal static string? Translate(Exception exception) => exception switch
    {
        OperationalPeriodLockedException locked => locked.Message,
        DbUpdateConcurrencyException => ConcurrencyMessage,
        DbUpdateException { InnerException: PostgresException } => DatabaseRuleMessage,
        PostgresException => DatabaseRuleMessage,
        _ => null,
    };

    private static bool WantsJson(ExceptionContext context)
    {
        var request = context.HttpContext.Request;
        return string.Equals(request.Headers.XRequestedWith, "XMLHttpRequest", StringComparison.OrdinalIgnoreCase)
            || (request.Headers.Accept.ToString()?.Contains("application/json", StringComparison.OrdinalIgnoreCase) ?? false);
    }
}
