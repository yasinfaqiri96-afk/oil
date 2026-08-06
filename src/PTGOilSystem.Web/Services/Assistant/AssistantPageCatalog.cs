using System.Text;
using System.Text.Json;

namespace PTGOilSystem.Web.Services.Assistant;

/// <summary>یک ورودی راهنما برای یک صفحه.</summary>
public sealed class AssistantPageEntry
{
    public string? Title { get; set; }
    public string? Module { get; set; }
    public string? Purpose { get; set; }
    public string? Start { get; set; }
}

/// <summary>
/// کاتالوگ راهنمای صفحات. فایل JSON یک‌بار در startup خوانده می‌شود.
/// افزودن صفحه جدید فقط با ویرایش همان فایل انجام می‌شود.
/// </summary>
public sealed class AssistantPageCatalog
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly Dictionary<string, AssistantPageEntry> _pages;
    private readonly Dictionary<string, string> _actions;

    public AssistantPageCatalog(IWebHostEnvironment environment, ILogger<AssistantPageCatalog> logger)
    {
        _pages = new Dictionary<string, AssistantPageEntry>(StringComparer.OrdinalIgnoreCase);
        _actions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var path = Path.Combine(environment.ContentRootPath, "Assistant", "page-guide.json");
        if (!File.Exists(path))
        {
            logger.LogWarning("Assistant page guide not found at {Path}. Assistant will answer without page descriptions.", path);
            return;
        }

        try
        {
            using var stream = File.OpenRead(path);
            using var document = JsonDocument.Parse(stream);
            var root = document.RootElement;

            if (root.TryGetProperty("pages", out var pages) && pages.ValueKind == JsonValueKind.Object)
            {
                foreach (var page in pages.EnumerateObject())
                {
                    var entry = page.Value.Deserialize<AssistantPageEntry>(SerializerOptions);
                    if (entry is not null)
                    {
                        _pages[page.Name] = entry;
                    }
                }
            }

            if (root.TryGetProperty("actions", out var actions) && actions.ValueKind == JsonValueKind.Object)
            {
                foreach (var action in actions.EnumerateObject())
                {
                    if (action.Value.ValueKind == JsonValueKind.String)
                    {
                        _actions[action.Name] = action.Value.GetString() ?? string.Empty;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Assistant page guide could not be parsed. Assistant will answer without page descriptions.");
        }
    }

    public AssistantPageEntry? FindPage(string? controller)
        => string.IsNullOrWhiteSpace(controller) ? null
            : _pages.TryGetValue(controller, out var entry) ? entry : null;

    public string? FindAction(string? action)
        => string.IsNullOrWhiteSpace(action) ? null
            : _actions.TryGetValue(action, out var text) ? text : null;

    /// <summary>
    /// نقشهٔ صفحات برای راهنمایی «کجا بروم؟». بدون این، دستیار فقط صفحهٔ جاری را
    /// می‌شناسد و نمی‌تواند کاربر را به صفحهٔ دیگری هدایت کند.
    ///
    /// <paramref name="allowedControllers"/> فهرست Controllerهایی است که کاربر
    /// جاری اجازهٔ دیدنشان را دارد؛ صفحات خارج از آن اصلاً به مدل معرفی نمی‌شوند
    /// تا دستیار کاربر را به جایی نفرستد که در آن دسترسی ندارد.
    /// </summary>
    /// <param name="compact">
    /// درست باشد، فقط عنوان صفحه‌ها می‌آید و شرح کامل هر صفحه حذف می‌شود. شرح کامل
    /// فقط برای صفحهٔ جاری فرستاده می‌شود، پس حجم هر درخواست چند برابر کوچک‌تر می‌ماند.
    /// </param>
    public string BuildSiteMap(Func<string, bool> allowedControllers, bool compact = false)
    {
        ArgumentNullException.ThrowIfNull(allowedControllers);

        var builder = new StringBuilder();

        foreach (var group in _pages
            .Where(page => allowedControllers(page.Key))
            .GroupBy(page => string.IsNullOrWhiteSpace(page.Value.Module) ? "سایر" : page.Value.Module!)
            .OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            builder.Append("- ").Append(group.Key).Append(": ");
            builder.AppendJoin(
                "؛ ",
                group.Select(page =>
                {
                    var title = string.IsNullOrWhiteSpace(page.Value.Title) ? page.Key : page.Value.Title;
                    return compact || string.IsNullOrWhiteSpace(page.Value.Purpose)
                        ? title
                        : $"{title} ({page.Value.Purpose})";
                }));
            builder.AppendLine();
        }

        return builder.ToString();
    }
}
