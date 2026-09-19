using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Services;

namespace PTGOilSystem.Web.Infrastructure.Api;

public sealed record MobileIdempotencyContext(string Token, string Purpose);

/// <summary>
/// زیرساخت ضد ثبت تکراری برای نوشتن از موبایل. شبکهٔ ضعیف درخواست را دوباره می‌فرستد؛ کلاینت
/// برای هر عملیات یک UUID می‌سازد و در همهٔ تلاش‌ها همان را در هدر <c>Idempotency-Key</c> می‌فرستد.
///
/// تضمین نهایی همان جدول و unique index موجودِ <c>ProcessedFormTokens</c> است که فرم‌های وب هم
/// استفاده می‌کنند؛ جدول تازه‌ای ساخته نشده. هر endpointِ نوشتنی باید:
/// <list type="number">
///   <item>این ویژگی را با یک Purpose ثابت داشته باشد (مثلاً <c>Mobile.Sale.Create</c>)،</item>
///   <item>پیش از <c>SaveChanges</c>، <see cref="MobileIdempotency.Stamp"/> را در همان تراکنشِ رکورد اصلی صدا بزند،</item>
///   <item>خطای <see cref="IFormTokenGuard.IsDuplicate"/> را به 409 <c>duplicate_request</c> ترجمه کند.</item>
/// </list>
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class RequireIdempotencyKeyAttribute(string purpose) : Attribute, IAsyncActionFilter
{
    public const string HeaderName = "Idempotency-Key";

    public string Purpose { get; } = purpose;

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var httpContext = context.HttpContext;
        var raw = httpContext.Request.Headers[HeaderName].ToString();

        if (string.IsNullOrWhiteSpace(raw))
        {
            context.Result = ApiProblem.Result(
                httpContext,
                StatusCodes.Status400BadRequest,
                ApiErrorCodes.IdempotencyKeyRequired,
                "برای ثبت، شناسهٔ یکتای درخواست (Idempotency-Key) لازم است.");
            return;
        }

        if (!Guid.TryParse(raw.Trim(), out var key) || key == Guid.Empty)
        {
            context.Result = ApiProblem.Result(
                httpContext,
                StatusCodes.Status400BadRequest,
                ApiErrorCodes.IdempotencyKeyInvalid,
                "شناسهٔ یکتای درخواست (Idempotency-Key) باید UUID معتبر باشد.");
            return;
        }

        var token = MobileIdempotency.ToToken(key);
        var db = httpContext.RequestServices.GetRequiredService<ApplicationDbContext>();
        var existing = await db.ProcessedFormTokens
            .AsNoTracking()
            .Where(t => t.Token == token)
            .Select(t => new { t.ReferenceType, t.ReferenceId })
            .FirstOrDefaultAsync(httpContext.RequestAborted);

        if (existing is not null)
        {
            context.Result = ApiProblem.Result(
                httpContext,
                StatusCodes.Status409Conflict,
                ApiErrorCodes.DuplicateRequest,
                "این درخواست قبلاً ثبت شده است.",
                new Dictionary<string, object?>
                {
                    ["referenceType"] = existing.ReferenceType,
                    ["referenceId"] = existing.ReferenceId
                });
            return;
        }

        httpContext.Items[MobileIdempotency.ItemKey] = new MobileIdempotencyContext(token, Purpose);
        await next();
    }
}

public static class MobileIdempotency
{
    public const string TokenPrefix = "mobile:";
    internal const string ItemKey = "ptg:idempotency";

    public static string ToToken(Guid key) => TokenPrefix + key.ToString("D");

    public static MobileIdempotencyContext? Get(HttpContext context)
        => context.Items.TryGetValue(ItemKey, out var value) ? value as MobileIdempotencyContext : null;

    /// <summary>ردیف مصرفِ کلید را در همان SaveChangesِ رکورد اصلی ثبت می‌کند.</summary>
    public static void Stamp(HttpContext context, IFormTokenGuard guard, string? referenceType = null)
    {
        var idempotency = Get(context)
            ?? throw new InvalidOperationException("RequireIdempotencyKey did not run for this action.");
        guard.Stamp(idempotency.Token, idempotency.Purpose, referenceType);
    }
}
