namespace PTGOilSystem.Web.Infrastructure.Api;

/// <summary>
/// پشتوانهٔ خطا فقط برای مسیرهای <c>/api</c>: خطای خارج از MVC (مثلاً در احراز هویت) به 500 JSON
/// تبدیل می‌شود و پاسخ‌های خالیِ 401/403/404/405 بدنهٔ ProblemDetails می‌گیرند. صفحات وب دست
/// نمی‌خورند و همچنان از <c>UseExceptionHandler("/Home/Error")</c> استفاده می‌کنند.
/// </summary>
public sealed class ApiErrorMiddleware(RequestDelegate next, ILogger<ApiErrorMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (!ApiRequest.IsApi(context))
        {
            await next(context);
            return;
        }

        try
        {
            await next(context);
        }
        catch (Exception ex) when (!context.Response.HasStarted && !context.RequestAborted.IsCancellationRequested)
        {
            logger.LogError(ex, "Unhandled API pipeline exception on {Path}.", context.Request.Path.Value);
            context.Response.Clear();
            await ApiProblem.WriteAsync(
                context,
                StatusCodes.Status500InternalServerError,
                ApiErrorCodes.ServerError,
                ApiProblem.ServerErrorMessage);
            return;
        }

        if (context.Response.HasStarted
            || context.Response.ContentLength > 0
            || !string.IsNullOrEmpty(context.Response.ContentType))
        {
            return;
        }

        string? code = null;
        string? detail = null;
        switch (context.Response.StatusCode)
        {
            case StatusCodes.Status401Unauthorized:
                code = ApiErrorCodes.Unauthorized;
                detail = ApiProblem.UnauthorizedMessage;
                break;
            case StatusCodes.Status403Forbidden:
                code = ApiErrorCodes.Forbidden;
                detail = ApiProblem.ForbiddenMessage;
                break;
            case StatusCodes.Status404NotFound:
                code = ApiErrorCodes.NotFound;
                detail = ApiProblem.NotFoundMessage;
                break;
            case StatusCodes.Status405MethodNotAllowed:
                code = ApiErrorCodes.MethodNotAllowed;
                detail = ApiProblem.MethodNotAllowedMessage;
                break;
        }

        if (code is not null && detail is not null)
        {
            await ApiProblem.WriteAsync(context, context.Response.StatusCode, code, detail);
        }
    }
}
