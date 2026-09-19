using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using PTGOilSystem.Web.Services.ContractClosure;
using PTGOilSystem.Web.Services.Exceptions;
using PTGOilSystem.Web.Services.OperationalPeriod;

namespace PTGOilSystem.Web.Infrastructure.Api;

public sealed record ApiErrorMapping(
    int Status,
    string Code,
    string Detail,
    IReadOnlyDictionary<string, object?>? Extensions = null);

/// <summary>
/// مترجم خطای کنترلرهای API به ProblemDetails. همان پیام‌های کاربرپسندِ
/// <see cref="BusinessRuleExceptionFilter"/> وب استفاده می‌شود، اما به‌جای Redirect پاسخ JSON است.
/// خطای ناشناخته فقط لاگ می‌شود و کلاینت پیام عمومی 500 می‌گیرد.
/// </summary>
public sealed class ApiExceptionFilter(ILogger<ApiExceptionFilter> logger) : IExceptionFilter
{
    public void OnException(ExceptionContext context)
    {
        if (context.ExceptionHandled)
        {
            return;
        }

        var httpContext = context.HttpContext;
        if (context.Exception is OperationCanceledException && httpContext.RequestAborted.IsCancellationRequested)
        {
            return;
        }

        var mapping = Map(context.Exception);
        if (mapping.Status >= StatusCodes.Status500InternalServerError)
        {
            logger.LogError(context.Exception, "Unhandled API exception on {Path}.", httpContext.Request.Path.Value);
        }
        else
        {
            logger.LogWarning(
                context.Exception,
                "API request rejected on {Path} with {Code}.",
                httpContext.Request.Path.Value,
                mapping.Code);
        }

        context.Result = ApiProblem.Result(httpContext, mapping.Status, mapping.Code, mapping.Detail, mapping.Extensions);
        context.ExceptionHandled = true;
    }

    internal static ApiErrorMapping Map(Exception exception) => exception switch
    {
        BusinessRuleException rule => new ApiErrorMapping(
            StatusCodes.Status422UnprocessableEntity,
            ApiErrorCodes.BusinessRule,
            rule.Message,
            new Dictionary<string, object?> { ["ruleCode"] = rule.Code }),
        OperationalPeriodLockedException locked => new ApiErrorMapping(
            StatusCodes.Status422UnprocessableEntity, ApiErrorCodes.BusinessRule, locked.Message),
        ContractClosedException closed => new ApiErrorMapping(
            StatusCodes.Status422UnprocessableEntity, ApiErrorCodes.BusinessRule, closed.Message),
        DbUpdateConcurrencyException => new ApiErrorMapping(
            StatusCodes.Status409Conflict, ApiErrorCodes.Conflict, BusinessRuleExceptionFilter.ConcurrencyMessage),
        DbUpdateException { InnerException: PostgresException } => new ApiErrorMapping(
            StatusCodes.Status409Conflict, ApiErrorCodes.Conflict, BusinessRuleExceptionFilter.DatabaseRuleMessage),
        PostgresException => new ApiErrorMapping(
            StatusCodes.Status409Conflict, ApiErrorCodes.Conflict, BusinessRuleExceptionFilter.DatabaseRuleMessage),
        _ => new ApiErrorMapping(
            StatusCodes.Status500InternalServerError, ApiErrorCodes.ServerError, ApiProblem.ServerErrorMessage)
    };
}
