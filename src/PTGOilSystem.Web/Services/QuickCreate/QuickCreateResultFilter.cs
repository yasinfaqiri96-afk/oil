using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using PTGOilSystem.Web.Data;

namespace PTGOilSystem.Web.Services.QuickCreate;

public sealed class QuickCreateResultFilter : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var controller = context.RouteData.Values["controller"]?.ToString();
        var definition = QuickCreateEntityRegistry.ForController(controller);
        if (definition is null || !IsQuickCreateRequest(context.HttpContext.Request, context.RouteData.Values["action"]?.ToString()))
        {
            await next();
            return;
        }

        // فیلتر سراسری است: DbContext فقط در همین مسیر نادر resolve می‌شود، نه در هر
        // درخواست. همان نمونهٔ scoped کنترلر است، پس ChangeTracker یکی است.
        var db = context.HttpContext.RequestServices.GetRequiredService<ApplicationDbContext>();

        var trackedBefore = db.ChangeTracker.Entries()
            .Select(entry => entry.Entity)
            .ToHashSet(ReferenceEqualityComparer.Instance);

        var executed = await next();
        if (executed.Canceled
            || executed.Exception is not null
            || !IsSuccessfulCreateResult(executed.Result))
        {
            return;
        }

        var created = FindCreatedEntity(db, definition, trackedBefore);
        if (created is null)
        {
            executed.Result = new ObjectResult(new
            {
                success = false,
                message = "رکورد ثبت شد، اما اطلاعات لازم برای انتخاب خودکار آن برگردانده نشد."
            })
            {
                StatusCode = StatusCodes.Status500InternalServerError
            };
            return;
        }

        if (context.Controller is Controller mvcController)
        {
            mvcController.TempData.Remove("ok");
        }

        executed.Result = new JsonResult(new
        {
            success = true,
            item = new
            {
                id = QuickCreateEntityRegistry.ReadString(created.Entity, "Id"),
                code = QuickCreateEntityRegistry.ReadString(created.Entity, "Code"),
                label = definition.BuildLabel(created.Entity)
            }
        });
    }

    internal static bool IsQuickCreateRequest(HttpRequest request, string? action)
        => HttpMethods.IsPost(request.Method)
            && string.Equals(action, "Create", StringComparison.OrdinalIgnoreCase)
            && string.Equals(request.Query["modal"], "1", StringComparison.Ordinal)
            && string.Equals(request.Query["quickCreate"], "1", StringComparison.Ordinal)
            && string.Equals(request.Headers["X-Requested-With"], "XMLHttpRequest", StringComparison.OrdinalIgnoreCase);

    private static EntityEntry? FindCreatedEntity(
        ApplicationDbContext db,
        QuickCreateEntityDefinition definition,
        HashSet<object> trackedBefore)
        => db.ChangeTracker.Entries()
            .Where(entry => !trackedBefore.Contains(entry.Entity))
            .Where(entry => string.Equals(
                entry.Metadata.ClrType.Name,
                definition.EntityTypeName,
                StringComparison.Ordinal))
            .Where(entry =>
            {
                var id = QuickCreateEntityRegistry.ReadString(entry.Entity, "Id");
                return !string.IsNullOrWhiteSpace(id) && !string.Equals(id, "0", StringComparison.Ordinal);
            })
            .LastOrDefault();

    // فرم‌های Create وقتی returnUrl دارند LocalRedirect برمی‌گردانند؛ این نوع از
    // RedirectResult ارث نمی‌برد، پس بدون آن پاسخ ثبت موفق به JSON تبدیل نمی‌شد و
    // iframe به‌جای انتخاب خودکار، صفحهٔ فهرست را نشان می‌داد.
    private static bool IsSuccessfulCreateResult(IActionResult? result)
        => result is RedirectResult
            or LocalRedirectResult
            or RedirectToActionResult
            or RedirectToRouteResult;
}
