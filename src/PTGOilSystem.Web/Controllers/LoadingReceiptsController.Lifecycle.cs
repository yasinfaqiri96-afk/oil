using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Security;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Exceptions;

namespace PTGOilSystem.Web.Controllers;

public partial class LoadingReceiptsController
{
    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteCancelled(int id, string? returnUrl,
        [FromServices] OperationLifecycleService lifecycle,
        [FromServices] ICurrentUserContext actor, CancellationToken cancellationToken)
    {
        try
        {
            await lifecycle.ArchiveAsync("LoadingReceipt", id, actor.UserId, cancellationToken);
            TempData["ok"] = "رکورد لغوشده از فهرست حذف شد؛ اسناد و سوابق محفوظ است.";
        }
        catch (BusinessRuleException ex) { TempData["err"] = ex.Message; }
        catch (DbUpdateConcurrencyException) { TempData["err"] = "رکورد هم‌زمان تغییر کرده است؛ صفحه را تازه کنید."; }
        return !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl) ? LocalRedirect(returnUrl) : RedirectToAction(nameof(Index));
    }
}
