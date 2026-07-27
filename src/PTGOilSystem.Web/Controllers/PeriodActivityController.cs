using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using PTGOilSystem.Web.Configuration;
using PTGOilSystem.Web.Security;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Accounting;

namespace PTGOilSystem.Web.Controllers;

/// <summary>
/// صفحهٔ فقط‌خواندنیِ «مشاهده فعالیت‌ها» برای یک دورهٔ مالی. خلاصهٔ همهٔ عملیاتِ همان بازهٔ تاریخی
/// را نشان می‌دهد. شرکت همیشه شرکتِ مالکِ سیستم است و سمتِ سرور تعیین می‌شود؛ دورهٔ شرکتِ دیگر
/// دیده نمی‌شود. اگر <c>Accounting.Enabled=false</c> باشد بخشِ اسناد حسابداری خالی می‌ماند.
/// </summary>
[Authorize(Policy = AuthPolicies.ManageData)]
[Route("fiscal/period-activity")]
public sealed class PeriodActivityController(
    IPeriodActivityService activity,
    ISystemCompanyProvider systemCompany,
    IOptions<AccountingOptions> accountingOptions) : Controller
{
    [HttpGet("")]
    public async Task<IActionResult> Index(int periodId, CancellationToken cancellationToken)
    {
        var ownerCompanyId = await systemCompany.GetOwnerCompanyIdAsync(cancellationToken);
        var model = await activity.BuildAsync(
            periodId, ownerCompanyId, accountingOptions.Value.Enabled, cancellationToken);

        return model is null ? NotFound() : View(model);
    }
}
