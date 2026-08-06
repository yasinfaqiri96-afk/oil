namespace PTGOilSystem.Web.Services.Assistant;

/// <summary>نقش یک پیام در گفتگو با مدل.</summary>
public enum AssistantChatRole
{
    System = 0,
    User,
    Assistant,

    /// <summary>نتیجهٔ اجرای یک Tool که به مدل برگردانده می‌شود.</summary>
    Tool,
}

/// <summary>
/// یک پیام در گفتگو با مدل. همان شکل مشترکی که هر دو Provider می‌فهمند،
/// تا تعویض Anthropic/Groq فقط یک تنظیم بماند.
///
/// <para>
/// <c>ProviderContent</c> نوبتِ خودِ مدل را همان‌طور که Provider تحویل داده نگه می‌دارد
/// (برای Gemini یک <c>Google.GenAI.Types.Content</c>). سرویس آن را باز نمی‌کند، نمی‌خواند
/// و ذخیره نمی‌کند؛ فقط در درخواست بعدی به همان Provider پس می‌دهد. لازم است چون
/// بازسازی دستیِ FunctionCall از روی نام و آرگومان، فیلدهای مبهم مدل — مثل
/// ThoughtSignature در Gemini — را دور می‌ریزد و درخواست بعدی رد می‌شود.
/// Providerهایی که چنین چیزی ندارند این مقدار را نادیده می‌گیرند.
/// </para>
/// </summary>
public sealed record AssistantChatMessage(
    AssistantChatRole Role,
    string Content,
    IReadOnlyList<AssistantToolCall>? ToolCalls = null,
    string? ToolCallId = null,
    string? ToolName = null,
    object? ProviderContent = null)
{
    public static AssistantChatMessage System(string content) => new(AssistantChatRole.System, content);

    public static AssistantChatMessage User(string content) => new(AssistantChatRole.User, content);

    public static AssistantChatMessage Assistant(string content) => new(AssistantChatRole.Assistant, content);

    /// <summary>
    /// نتیجهٔ یک Tool. نام ابزار کنار شناسه نگه داشته می‌شود چون سرویس‌ها فرق دارند:
    /// Groq نتیجه را با tool_call_id می‌شناسد، ولی Gemini در functionResponse نام
    /// تابع را می‌خواهد.
    /// </summary>
    public static AssistantChatMessage ToolResult(string toolCallId, string toolName, string content)
        => new(AssistantChatRole.Tool, content, ToolCallId: toolCallId, ToolName: toolName);
}

/// <summary>درخواست مدل برای اجرای یک Tool.</summary>
public sealed record AssistantToolCall(string Id, string ToolName, string ArgumentsJson);

/// <summary>
/// تعریف یک Tool که به مدل معرفی می‌شود. Schema به‌صورت JSON Schema نوشته می‌شود
/// چون هر دو سرویس همین قالب را می‌پذیرند.
/// </summary>
public sealed record AssistantToolDefinition(
    string Name,
    string Description,
    string ParametersJsonSchema);

/// <summary>
/// خروجی یک دور گفتگو با مدل: یا متن نهایی، یا درخواست اجرای Tool.
/// </summary>
public sealed record AssistantProviderResult(
    string? Text,
    AssistantFailure Failure,
    IReadOnlyList<AssistantToolCall>? ToolCalls = null,
    object? ProviderContent = null)
{
    public static AssistantProviderResult Success(string text) => new(text, AssistantFailure.None);

    public static AssistantProviderResult Failed(AssistantFailure failure) => new(null, failure);

    /// <summary>
    /// درخواست اجرای ابزار. <paramref name="providerContent"/> نوبتِ دست‌نخوردهٔ مدل است
    /// که باید عیناً در درخواست بعدی برگردد؛ توضیح کامل در AssistantChatMessage.
    /// </summary>
    public static AssistantProviderResult RequestTools(
        string? text,
        IReadOnlyList<AssistantToolCall> calls,
        object? providerContent = null)
        => new(text, AssistantFailure.None, calls, providerContent);

    public bool HasToolCalls => ToolCalls is { Count: > 0 };
}
