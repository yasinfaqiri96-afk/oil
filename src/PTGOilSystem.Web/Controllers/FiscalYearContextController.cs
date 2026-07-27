using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PTGOilSystem.Web.Services.Accounting;

namespace PTGOilSystem.Web.Controllers;

/// <summary>
/// تنها مسیرِ نوشتنِ «سال مالیِ فعالِ کانتکست». برای همهٔ کاربرانِ احرازشده باز است چون این
/// انتخاب فقط یک کانتکستِ نمایشی/گزارشی است و هیچ منطقِ مالی‌ای را تغییر نمی‌دهد.
///
/// تعلقِ سال به شرکتِ مالک سمتِ سرور در <see cref="IFiscalYearContext.TrySelectAsync"/> بررسی
/// می‌شود؛ سالِ شرکتِ دیگر هرگز پذیرفته نمی‌شود.
/// </summary>
[Authorize]
public sealed class FiscalYearContextController(IFiscalYearContext context) : Controller
{
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Select(int fiscalYearId, string? returnUrl, CancellationToken cancellationToken)
    {
        var ok = await context.TrySelectAsync(fiscalYearId, cancellationToken);
        if (!ok)
        {
            TempData["err"] = "این سال مالی برای این شرکت معتبر نیست.";
        }

        if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
        {
            return LocalRedirect(returnUrl);
        }

        return RedirectToAction("Index", "Home");
    }
}
