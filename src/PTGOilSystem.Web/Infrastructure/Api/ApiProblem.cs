using System.Text.Json;
using Microsoft.AspNetCore.Mvc;

namespace PTGOilSystem.Web.Infrastructure.Api;

public static class ApiRequest
{
    public const string PathPrefix = "/api";

    public static bool IsApi(HttpContext context)
        => context.Request.Path.StartsWithSegments(PathPrefix, StringComparison.OrdinalIgnoreCase);
}

/// <summary>کد ماشینیِ خطا در <c>ProblemDetails.code</c>؛ کلاینت موبایل روی همین تصمیم می‌گیرد.</summary>
public static class ApiErrorCodes
{
    public const string Unauthorized = "unauthorized";
    public const string TokenExpired = "token_expired";
    public const string SessionRevoked = "session_revoked";
    public const string Forbidden = "forbidden";
    public const string Validation = "validation";
    public const string BusinessRule = "business_rule";
    public const string Conflict = "conflict";
    public const string DuplicateRequest = "duplicate_request";
    public const string NotFound = "not_found";
    public const string MethodNotAllowed = "method_not_allowed";
    public const string RateLimited = "rate_limited";
    public const string ServerError = "server_error";
    public const string InvalidCredentials = "invalid_credentials";
    public const string AccountLocked = "account_locked";
    public const string RefreshTokenInvalid = "refresh_token_invalid";
    public const string RefreshTokenReused = "refresh_token_reused";
    public const string MobileAuthUnavailable = "mobile_auth_unavailable";
    public const string IdempotencyKeyRequired = "idempotency_key_required";
    public const string IdempotencyKeyInvalid = "idempotency_key_invalid";
}

/// <summary>
/// قرارداد خطای API: RFC 7807 ProblemDetails با <c>code</c> و <c>traceId</c>. متن <c>detail</c>
/// برای کاربر است؛ جزئیات فنی (Stack، SQL، Connection string) هرگز در پاسخ نمی‌آید و فقط لاگ می‌شود.
/// </summary>
public static class ApiProblem
{
    public const string ContentType = "application/problem+json";

    internal const string UnauthorizedMessage = "برای این درخواست باید وارد سیستم شوید.";
    internal const string TokenExpiredMessage = "اعتبار نشست تمام شده است.";
    internal const string SessionRevokedMessage = "این نشست باطل شده است. دوباره وارد شوید.";
    internal const string ForbiddenMessage = "به این بخش دسترسی ندارید.";
    internal const string NotFoundMessage = "مسیر یا رکورد درخواست‌شده پیدا نشد.";
    internal const string MethodNotAllowedMessage = "این نوع درخواست برای این مسیر مجاز نیست.";
    internal const string ServerErrorMessage = "خطای داخلی سرور رخ داد. لطفاً دوباره تلاش کنید.";
    internal const string ValidationMessage = "اطلاعات ارسال‌شده معتبر نیست.";
    internal const string RateLimitedMessage = "تعداد درخواست‌ها بیش از حد مجاز است. لطفاً کمی بعد دوباره تلاش کنید.";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static ProblemDetails Create(
        HttpContext context,
        int status,
        string code,
        string detail,
        IReadOnlyDictionary<string, object?>? extensions = null)
    {
        var problem = new ProblemDetails
        {
            Status = status,
            Title = TitleFor(status),
            Detail = detail,
            Type = "about:blank"
        };
        Decorate(problem, context, code, extensions);
        return problem;
    }

    public static void Decorate(
        ProblemDetails problem,
        HttpContext context,
        string code,
        IReadOnlyDictionary<string, object?>? extensions = null)
    {
        problem.Extensions["code"] = code;
        problem.Extensions["traceId"] = context.TraceIdentifier;
        if (extensions is null)
        {
            return;
        }

        foreach (var (key, value) in extensions)
        {
            problem.Extensions[key] = value;
        }
    }

    public static ObjectResult Result(
        HttpContext context,
        int status,
        string code,
        string detail,
        IReadOnlyDictionary<string, object?>? extensions = null)
    {
        var result = new ObjectResult(Create(context, status, code, detail, extensions)) { StatusCode = status };
        result.ContentTypes.Add(ContentType);
        return result;
    }

    public static Task WriteAsync(HttpContext context, int status, string code, string detail)
    {
        context.Response.StatusCode = status;
        context.Response.Headers.CacheControl = "no-store";
        return context.Response.WriteAsJsonAsync(Create(context, status, code, detail), JsonOptions, ContentType);
    }

    private static string TitleFor(int status) => status switch
    {
        400 => "Bad Request",
        401 => "Unauthorized",
        403 => "Forbidden",
        404 => "Not Found",
        405 => "Method Not Allowed",
        409 => "Conflict",
        422 => "Unprocessable Entity",
        429 => "Too Many Requests",
        503 => "Service Unavailable",
        >= 500 => "Internal Server Error",
        _ => "Error"
    };
}
