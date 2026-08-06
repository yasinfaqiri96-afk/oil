using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PTGOilSystem.Web.Services.Assistant;

namespace PTGOilSystem.Web.Controllers;

/// <summary>
/// نقطه ورود «راهنمای هوشمند». فقط متن راهنما برمی‌گرداند و هیچ داده‌ای را تغییر نمی‌دهد.
/// دسترسی همان دسترسی کاربر واردشده است؛ هیچ نقشی دور زده نمی‌شود.
/// </summary>
[Authorize]
public sealed class AssistantController : Controller
{
    private readonly IAssistantService _assistant;

    public AssistantController(IAssistantService assistant)
    {
        _assistant = assistant;
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Ask([FromBody] AssistantAskRequest? request)
    {
        if (request is null)
        {
            return BadRequest(new AssistantAnswer(false, "درخواست نامعتبر است."));
        }

        Response.Headers.CacheControl = "no-store";

        // کل ClaimsPrincipal فرستاده می‌شود چون بررسی دسترسی هر ابزار به نقش و
        // Claimهای ناوبری همین کاربر تکیه دارد، نه فقط به نام نقش.
        var answer = await _assistant.AskAsync(request, User, HttpContext.RequestAborted);
        return Json(answer);
    }
}
