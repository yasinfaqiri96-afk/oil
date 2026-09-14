using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PTGOilSystem.Web.Security;
using PTGOilSystem.Web.Services.Accounting;

namespace PTGOilSystem.Web.Controllers;

/// <summary>
/// Mappingِ سرفصل‌ها به بخش‌های عملیاتی (صندوق، طرف حساب‌ها، فروش، موجودی، کالای در راه، بهای
/// تمام‌شده …). همان <c>AccountingSettings</c> که Adapterها برای سند خودکار می‌خوانند؛ قواعدِ قفل و
/// اعتبارسنجی در <see cref="IAccountingMappingService"/> است.
/// </summary>
public sealed partial class ChartOfAccountsController
{
    [HttpGet("mapping")]
    public async Task<IActionResult> Mapping(
        [FromServices] IAccountingMappingService mapping,
        CancellationToken cancellationToken = default)
        => View(await mapping.BuildAsync(cancellationToken));

    [HttpPost("mapping")]
    [Authorize(Policy = AuthPolicies.ManageData)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Mapping(
        string? role,
        int accountId,
        [FromServices] IAccountingMappingService mapping,
        CancellationToken cancellationToken = default)
    {
        var result = await mapping.UpdateAsync(role, accountId, cancellationToken);
        TempData[result.Succeeded ? "ok" : "error"] = result.Message;
        return RedirectToAction(nameof(Mapping));
    }
}
