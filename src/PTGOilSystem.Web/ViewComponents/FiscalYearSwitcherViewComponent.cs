using Microsoft.AspNetCore.Mvc;
using PTGOilSystem.Web.Models.Accounting;
using PTGOilSystem.Web.Services.Accounting;

namespace PTGOilSystem.Web.ViewComponents;

/// <summary>
/// انتخاب‌گرِ «سال مالیِ فعال» در هدر. روی همهٔ صفحات پوستهٔ احرازشده رندر می‌شود.
/// خودش داده را از <see cref="IFiscalYearContext"/> می‌گیرد تا هیچ کنترلری مجبور به آماده‌کردنِ
/// این داده نباشد. اگر شرکتِ مالک یا سالی وجود نداشته باشد، چیزی رندر نمی‌کند.
/// </summary>
public sealed class FiscalYearSwitcherViewComponent(IFiscalYearContext context) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync()
    {
        var view = await context.GetAsync(HttpContext.RequestAborted);
        return View(view);
    }
}
