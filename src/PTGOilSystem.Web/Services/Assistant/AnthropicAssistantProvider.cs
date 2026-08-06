using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;
using Microsoft.Extensions.Options;
using PTGOilSystem.Web.Configuration;

namespace PTGOilSystem.Web.Services.Assistant;

/// <summary>
/// Provider رسمی Anthropic با SDK رسمی. کلید فقط از متغیر محیطی ANTHROPIC_API_KEY
/// خوانده می‌شود و هرگز در پیکربندی یا Frontend قرار نمی‌گیرد.
/// </summary>
public sealed class AnthropicAssistantProvider : IAssistantProvider
{
    public const string ProviderName = "Anthropic";
    private const string ApiKeyVariable = "ANTHROPIC_API_KEY";

    private readonly AssistantOptions _options;
    private readonly ILogger<AnthropicAssistantProvider> _logger;
    private readonly Lazy<AnthropicClient?> _client;

    public AnthropicAssistantProvider(
        IOptions<AssistantOptions> options,
        ILogger<AnthropicAssistantProvider> logger)
    {
        _options = options.Value;
        _logger = logger;
        _client = new Lazy<AnthropicClient?>(CreateClient, isThreadSafe: true);
    }

    public string Name => ProviderName;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ApiKeyVariable));

    /// <summary>
    /// این Provider فعلاً فقط گفتگوی متنی را پشتیبانی می‌کند. خواندن داده واقعی روی
    /// Groq پیاده شده؛ تا وقتی اینجا Tool پیاده نشده، AssistantService برای Anthropic
    /// هیچ Tool ای نمی‌فرستد و دستیار در همان حالت راهنمایی می‌ماند.
    /// </summary>
    public bool SupportsTools => false;

    public async Task<AssistantProviderResult> CompleteAsync(
        IReadOnlyList<AssistantChatMessage> messages,
        IReadOnlyList<AssistantToolDefinition> tools,
        int maxOutputTokens,
        string? modelOverride,
        CancellationToken cancellationToken)
    {
        var client = _client.Value;
        if (client is null)
        {
            return AssistantProviderResult.Failed(AssistantFailure.NotConfigured);
        }

        // Anthropic راهنمای سیستمی را جدا از پیام‌ها می‌گیرد.
        var systemPrompt = string.Join(
            "\n\n",
            messages.Where(m => m.Role == AssistantChatRole.System).Select(m => m.Content));

        var conversation = messages
            .Where(m => m.Role is AssistantChatRole.User or AssistantChatRole.Assistant)
            .Where(m => !string.IsNullOrWhiteSpace(m.Content))
            .Select(m => new MessageParam
            {
                Role = m.Role == AssistantChatRole.Assistant ? Role.Assistant : Role.User,
                Content = m.Content,
            })
            .ToList();

        if (conversation.Count == 0)
        {
            return AssistantProviderResult.Failed(AssistantFailure.Unavailable);
        }

        var parameters = new MessageCreateParams
        {
            Model = string.IsNullOrWhiteSpace(modelOverride) ? _options.Model : modelOverride,
            MaxTokens = maxOutputTokens,
            System = systemPrompt,
            Messages = conversation,
        };

        try
        {
            var message = await client.Messages.Create(parameters, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            var builder = new System.Text.StringBuilder();
            foreach (var block in message.Content)
            {
                if (block.TryPickText(out var text) && !string.IsNullOrWhiteSpace(text.Text))
                {
                    builder.AppendLine(text.Text.Trim());
                }
            }

            var answer = builder.ToString().Trim();
            return string.IsNullOrWhiteSpace(answer)
                ? AssistantProviderResult.Failed(AssistantFailure.Unavailable)
                : AssistantProviderResult.Success(answer);
        }
        catch (AnthropicRateLimitException)
        {
            _logger.LogWarning("Anthropic assistant request was rate limited.");
            return AssistantProviderResult.Failed(AssistantFailure.RateLimited);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Anthropic assistant request failed.");
            return AssistantProviderResult.Failed(AssistantFailure.Unavailable);
        }
    }

    private AnthropicClient? CreateClient()
    {
        // کلید از همان متغیر محیطی توسط SDK خوانده می‌شود؛ اینجا فقط وجودش را بررسی می‌کنیم
        // تا کلید در هیچ فایل پیکربندی یا لاگی ننشیند.
        if (!IsConfigured)
        {
            _logger.LogWarning("{Variable} is not set. The Anthropic assistant provider stays unavailable.", ApiKeyVariable);
            return null;
        }

        return new AnthropicClient();
    }
}
