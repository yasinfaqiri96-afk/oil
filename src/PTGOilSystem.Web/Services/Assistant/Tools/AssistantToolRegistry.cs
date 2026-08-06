using System.Diagnostics;
using System.Security.Claims;
using System.Text.Json;
using PTGOilSystem.Web.Security;

namespace PTGOilSystem.Web.Services.Assistant.Tools;

/// <summary>
/// تنها نقطه‌ای که Tool اجرا می‌شود.
///
/// دو تضمین اینجا برقرار می‌شود و در جای دیگری تکرار نمی‌گردد:
///   ۱. مدل فقط Toolهایی را «می‌بیند» که کاربر جاری اجازهٔ همان بخش را دارد،
///      پس نمی‌تواند داده‌ای را که کاربر در برنامه نمی‌بیند حتی درخواست کند.
///   ۲. پیش از اجرا دسترسی دوباره سنجیده می‌شود، تا نام Tool ساختگی یا
///      دست‌کاری‌شده از سمت مدل هم رد شود.
/// </summary>
public interface IAssistantToolRegistry
{
    /// <summary>Toolهایی که این کاربر اجازهٔ استفاده از آن‌ها را دارد.</summary>
    IReadOnlyList<AssistantToolDefinition> GetAvailableTools(ClaimsPrincipal user);

    /// <summary>اجرای یک درخواست Tool با بررسی دوبارهٔ دسترسی.</summary>
    Task<string> ExecuteAsync(AssistantToolCall call, ClaimsPrincipal user, CancellationToken cancellationToken);
}

public sealed class AssistantToolRegistry : IAssistantToolRegistry
{
    private readonly IReadOnlyList<IAssistantTool> _tools;
    private readonly ILogger<AssistantToolRegistry> _logger;

    public AssistantToolRegistry(IEnumerable<IAssistantTool> tools, ILogger<AssistantToolRegistry> logger)
    {
        _tools = tools.ToList();
        _logger = logger;
    }

    public IReadOnlyList<AssistantToolDefinition> GetAvailableTools(ClaimsPrincipal user)
        => _tools
            .Where(tool => IsAllowed(tool, user))
            .Select(tool => new AssistantToolDefinition(tool.Name, tool.Description, tool.ParametersJsonSchema))
            .ToList();

    public async Task<string> ExecuteAsync(AssistantToolCall call, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        var tool = _tools.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, call.ToolName, StringComparison.OrdinalIgnoreCase));

        if (tool is null)
        {
            _logger.LogWarning("Assistant requested unknown tool '{Tool}'.", call.ToolName);
            return "چنین ابزاری وجود ندارد.";
        }

        // بررسی دوم و قطعی. حتی اگر مدل نام Tool ای را از جای دیگری حدس زده باشد،
        // بدون دسترسی کاربر اجرا نمی‌شود.
        if (!IsAllowed(tool, user))
        {
            _logger.LogWarning(
                "Assistant tool '{Tool}' was blocked for user '{User}' by navigation access rules.",
                tool.Name,
                user.Identity?.Name);
            return "کاربر به این بخش دسترسی ندارد. به کاربر بگو این معلومات در سطح دسترسی او نیست.";
        }

        JsonElement arguments;
        try
        {
            using var document = JsonDocument.Parse(
                string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
            arguments = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return "ورودی ابزار نامعتبر بود. ورودی را ساده‌تر بفرست.";
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var result = await tool.ExecuteAsync(arguments, user, cancellationToken);
            _logger.LogInformation(
                "Assistant tool '{Tool}' ran for user '{User}' in {Elapsed}ms.",
                tool.Name,
                user.Identity?.Name,
                stopwatch.ElapsedMilliseconds);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // جزئیات فنی فقط در Log می‌ماند؛ به مدل و کاربر نشت نمی‌کند.
            _logger.LogError(ex, "Assistant tool '{Tool}' failed.", tool.Name);
            return "خواندن این معلومات ناموفق بود. به کاربر بگو معلومات در دسترس نیست.";
        }
    }

    private static bool IsAllowed(IAssistantTool tool, ClaimsPrincipal user)
        => RoleAccessRules.CanAccessController(user, tool.RequiredController);
}
