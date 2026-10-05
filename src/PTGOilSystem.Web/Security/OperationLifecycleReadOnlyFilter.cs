using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;

namespace PTGOilSystem.Web.Security;

public sealed class OperationLifecycleReadOnlyFilter(ApplicationDbContext db) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var controller = context.RouteData.Values["controller"]?.ToString();
        var action = context.RouteData.Values["action"]?.ToString() ?? "";
        if (controller is not ("Contracts" or "Loading" or "LoadingReceipts")
            || action is "Cancel" or "BulkCancel" or "DeleteCancelled" or "Delete"
            || (context.HttpContext.Request.Method != "POST" && !action.StartsWith("Edit", StringComparison.Ordinal)))
        {
            await next(); return;
        }
        int Field(string name)
        {
            foreach (var pair in context.ActionArguments)
            {
                if (string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase) && pair.Value is int direct) return direct;
                var property = pair.Value?.GetType().GetProperty(name);
                if (property?.GetValue(pair.Value) is int nested) return nested;
            }
            return 0;
        }
        var id = Field("Id");
        var blocked = controller switch
        {
            "Contracts" => id > 0 && await db.Contracts.AnyAsync(x => x.Id == id && (x.IsArchived || x.Status == Models.Entities.ContractStatus.Cancelled)),
            "Loading" => id > 0 && await db.LoadingRegisters.AnyAsync(x => x.Id == id && (x.IsArchived || x.IsCancelled)),
            _ => id > 0 && await db.LoadingReceipts.AnyAsync(x => x.Id == id && (x.IsArchived || x.IsCancelled))
        };
        var contractId = Field("ContractId");
        if ((controller is "Loading" or "LoadingReceipts") && contractId > 0)
            blocked |= await db.Contracts.AnyAsync(x => x.Id == contractId && (x.IsArchived || x.Status == Models.Entities.ContractStatus.Cancelled));
        var loadingId = Field("LoadingRegisterId");
        if (controller == "LoadingReceipts" && loadingId > 0)
            blocked |= await db.LoadingRegisters.AnyAsync(x => x.Id == loadingId && (x.IsArchived || x.IsCancelled
                || (x.Contract != null && (x.Contract.IsArchived || x.Contract.Status == Models.Entities.ContractStatus.Cancelled))));
        if (blocked && context.Controller is Controller mvc)
        {
            mvc.TempData["err"] = "رکورد لغوشده یا حذف‌شده از فهرست قابل ویرایش یا استفادهٔ دوباره نیست.";
            context.Result = new RedirectToActionResult("Index", controller, null);
            return;
        }
        await next();
    }
}
