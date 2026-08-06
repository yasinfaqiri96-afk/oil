using System.Security.Claims;
using System.Text.Json;

namespace PTGOilSystem.Web.Services.Assistant.Tools;

/// <summary>
/// یک Tool فقط‌خواندنی که دستیار می‌تواند صدا بزند.
///
/// قواعد ثابت این لایه:
///   • هیچ Tool ای اجازهٔ نوشتن، تغییر یا حذف ندارد.
///   • مدل SQL نمی‌نویسد؛ فقط همین Toolهای ثبت‌شده در دسترس‌اند.
///   • هر Tool پیش از اجرا با دسترسی همان کاربر واردشده سنجیده می‌شود، پس دستیار
///     هرگز چیزی را فاش نمی‌کند که کاربر خودش نتواند در برنامه باز کند.
/// </summary>
public interface IAssistantTool
{
    /// <summary>نامی که مدل صدا می‌زند. فقط حروف کوچک و زیرخط.</summary>
    string Name { get; }

    /// <summary>توضیح برای مدل: این Tool چه می‌دهد و کِی باید صدا زده شود.</summary>
    string Description { get; }

    /// <summary>JSON Schema ورودی‌ها.</summary>
    string ParametersJsonSchema { get; }

    /// <summary>
    /// نام Controller ای که این داده در آن دیده می‌شود. دسترسی با همان قاعدهٔ
    /// ناوبری برنامه سنجیده می‌شود (<c>RoleAccessRules.CanAccessController</c>).
    /// </summary>
    string RequiredController { get; }

    /// <summary>اجرای فقط‌خواندنی. خروجی متنی فشرده که به مدل برگردانده می‌شود.</summary>
    Task<string> ExecuteAsync(JsonElement arguments, ClaimsPrincipal user, CancellationToken cancellationToken);
}

/// <summary>کمک‌کننده‌های مشترک خواندن ورودی Tool. ورودی مدل همیشه بی‌اعتماد است.</summary>
public static class AssistantToolArgs
{
    public static string? GetString(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.ToString(),
            _ => null,
        };
    }

    public static int? GetInt(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var number) => number,
            JsonValueKind.String when int.TryParse(value.GetString(), out var parsed) => parsed,
            _ => null,
        };
    }

    public static DateTime? GetDate(JsonElement args, string name)
    {
        var raw = GetString(args, name);
        return DateTime.TryParse(raw, out var parsed) ? parsed.Date : null;
    }
}
