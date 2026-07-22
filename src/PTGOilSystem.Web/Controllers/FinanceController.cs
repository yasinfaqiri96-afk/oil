using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace PTGOilSystem.Web.Controllers;

// هاب مالی: صفحهٔ فرودِ کارت‌محور، دقیقاً مانند هاب گزارش‌ها (Reports/Index).
// فقط لینک به بخش‌های مالیِ موجود را نشان می‌دهد و هیچ محاسبه یا سندی اینجا ساخته نمی‌شود.
[Authorize]
public class FinanceController : Controller
{
    public IActionResult Index() => View();
}
