using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using PTGOilSystem.Web.Configuration;

namespace PTGOilSystem.Web.Services.Assistant;

/// <summary>
/// Provider گروک روی API سازگار با OpenAI (POST {BaseUrl}/chat/completions).
/// عمداً بدون SDK اضافه و فقط با HttpClient نوشته شده تا وابستگی جدیدی به پروژه اضافه نشود.
/// کلید فقط از متغیر محیطی GROQ_API_KEY خوانده می‌شود.
/// </summary>
public sealed class GroqAssistantProvider : IAssistantProvider
{
    public const string ProviderName = "Groq";
    private const string ApiKeyVariable = "GROQ_API_KEY";
    public const string HttpClientName = "assistant-groq";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly AssistantOptions _options;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<GroqAssistantProvider> _logger;

    public GroqAssistantProvider(
        IOptions<AssistantOptions> options,
        IHttpClientFactory httpClientFactory,
        ILogger<GroqAssistantProvider> logger)
    {
        _options = options.Value;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public string Name => ProviderName;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ApiKeyVariable));

    public bool SupportsTools => true;

    public async Task<AssistantProviderResult> CompleteAsync(
        IReadOnlyList<AssistantChatMessage> messages,
        IReadOnlyList<AssistantToolDefinition> tools,
        int maxOutputTokens,
        string? modelOverride,
        CancellationToken cancellationToken)
    {
        var apiKey = Environment.GetEnvironmentVariable(ApiKeyVariable);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _logger.LogWarning("{Variable} is not set. The Groq assistant provider stays unavailable.", ApiKeyVariable);
            return AssistantProviderResult.Failed(AssistantFailure.NotConfigured);
        }

        var baseUrl = (_options.Groq.BaseUrl ?? string.Empty).TrimEnd('/');
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            _logger.LogError("Assistant:Groq:BaseUrl is empty.");
            return AssistantProviderResult.Failed(AssistantFailure.Unavailable);
        }

        var model = string.IsNullOrWhiteSpace(modelOverride) ? _options.Groq.Model : modelOverride;
        var payload = BuildPayload(messages, tools, maxOutputTokens, model);

        return await SendAsync(apiKey, baseUrl, model, payload, allowRetryAfterWait: true, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// یک تماس با گروک. در برخورد با ۴۲۹، اگر خود سرویس بگوید پنجره تا چند ثانیهٔ
    /// دیگر باز می‌شود، یک بار صبر و تلاش دوباره انجام می‌شود.
    ///
    /// چرا لازم است: سقف رایگان گروک بر حسب توکن در دقیقه است (۸۰۰۰) و یک پاسخِ
    /// ابزاردار در دو دور بیش از آن مصرف می‌کند. بدون این صبرِ کوتاه، جایگزینی
    /// عملاً روی سؤال‌های واقعی کار نمی‌کند. صبر کران‌دار است و فقط یک بار انجام
    /// می‌شود تا کاربر پشت درخواست نماند.
    /// </summary>
    private async Task<AssistantProviderResult> SendAsync(
        string apiKey,
        string baseUrl,
        string model,
        JsonObject payload,
        bool allowRetryAfterWait,
        CancellationToken cancellationToken)
    {
        try
        {
            var client = _httpClientFactory.CreateClient(HttpClientName);
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/chat/completions")
            {
                Content = JsonContent.Create(payload, options: SerializerOptions),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
                .ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var wait = ResolveRetryDelay(response);
                if (allowRetryAfterWait && wait is { } delay)
                {
                    _logger.LogWarning(
                        "Groq hit the free-tier rate limit (429). Waiting {Seconds:N0}s for the window to reset and trying once more.",
                        delay.TotalSeconds);

                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                    return await SendAsync(apiKey, baseUrl, model, payload, allowRetryAfterWait: false, cancellationToken)
                        .ConfigureAwait(false);
                }

                _logger.LogWarning("Groq assistant request hit the free-tier rate limit (429).");
                return AssistantProviderResult.Failed(AssistantFailure.RateLimited);
            }

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                _logger.LogError("Groq assistant request was rejected with {Status}. Check GROQ_API_KEY.", (int)response.StatusCode);
                return AssistantProviderResult.Failed(AssistantFailure.AccessDenied);
            }

            // 5xx موقت است و اجازهٔ تلاش با Provider جایگزین را می‌دهد؛ 4xx نه.
            if ((int)response.StatusCode >= 500)
            {
                _logger.LogWarning("Groq returned a server error ({Status}).", (int)response.StatusCode);
                return AssistantProviderResult.Failed(AssistantFailure.ServiceError);
            }

            if (!response.IsSuccessStatusCode)
            {
                // بدنهٔ خطا برای عیب‌یابی نام مدل یا شکل درخواست لازم است؛ فقط در Log می‌رود.
                var error = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

                // مدل گاهی ورودی ابزار را ناسازگار با Schema می‌سازد و سرویس کل درخواست
                // را رد می‌کند. این خطای کاربر یا خطای اتصال نیست، پس جدا از بقیه
                // گزارش می‌شود تا لایهٔ بالاتر بتواند بدون ابزار دوباره تلاش کند.
                if (error.Contains("tool_use_failed", StringComparison.OrdinalIgnoreCase)
                    || error.Contains("tool call validation failed", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning("Groq rejected a model-generated tool call. Body: {Body}", Trim(error, 500));
                    return AssistantProviderResult.Failed(AssistantFailure.ToolCallRejected);
                }

                // بقیهٔ 4xx یعنی خودِ درخواست ایراد دارد (نام مدل، Schema، اندازه).
                // دائمی است و نباید Provider دیگری آن را دوباره امتحان کند.
                _logger.LogError("Groq assistant request failed with {Status}. Body: {Body}", (int)response.StatusCode, Trim(error, 500));
                return AssistantProviderResult.Failed(AssistantFailure.InvalidRequest);
            }

            var completion = await response.Content
                .ReadFromJsonAsync<GroqChatResponse>(SerializerOptions, cancellationToken)
                .ConfigureAwait(false);

            var choice = completion?.Choices?.FirstOrDefault();
            var message = choice?.Message;

            var toolCalls = MapToolCalls(message?.ToolCalls);
            if (toolCalls.Count > 0)
            {
                return AssistantProviderResult.RequestTools(message?.Content?.Trim(), toolCalls);
            }

            var answer = message?.Content?.Trim();
            return string.IsNullOrWhiteSpace(answer)
                ? AssistantProviderResult.Failed(AssistantFailure.Unavailable)
                : AssistantProviderResult.Success(answer);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException)
        {
            // مهلت خودِ HttpClient. گذراست.
            _logger.LogWarning("Groq assistant request timed out.");
            return AssistantProviderResult.Failed(AssistantFailure.Timeout);
        }
        catch (HttpRequestException ex)
        {
            // قطع شبکه یا DNS؛ سرویس اصلاً پاسخ نداده است. گذراست.
            _logger.LogError(ex, "Groq assistant request failed at the network level.");
            return AssistantProviderResult.Failed(AssistantFailure.NetworkError);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Groq assistant request failed.");
            return AssistantProviderResult.Failed(AssistantFailure.Unavailable);
        }
    }

    /// <summary>
    /// ساخت بدنهٔ درخواست. با JsonNode نوشته شده چون شکل پیام بسته به وجود
    /// tool_calls / tool_call_id تغییر می‌کند و فیلدهای اضافی نباید فرستاده شوند.
    /// </summary>
    private static JsonObject BuildPayload(
        IReadOnlyList<AssistantChatMessage> messages,
        IReadOnlyList<AssistantToolDefinition> tools,
        int maxOutputTokens,
        string model)
    {
        var messageArray = new JsonArray();
        foreach (var message in messages)
        {
            messageArray.Add(BuildMessage(message));
        }

        var payload = new JsonObject
        {
            ["model"] = model,
            ["max_tokens"] = maxOutputTokens,
            // دمای پایین: راهنمای نرم‌افزار باید تکرارپذیر و بدون خیال‌پردازی باشد.
            ["temperature"] = 0.2,
            ["messages"] = messageArray,
        };

        if (tools.Count > 0)
        {
            var toolArray = new JsonArray();
            foreach (var tool in tools)
            {
                toolArray.Add(new JsonObject
                {
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = tool.Name,
                        ["description"] = tool.Description,
                        ["parameters"] = JsonNode.Parse(tool.ParametersJsonSchema),
                    },
                });
            }

            payload["tools"] = toolArray;
            payload["tool_choice"] = "auto";
        }

        return payload;
    }

    private static JsonObject BuildMessage(AssistantChatMessage message)
    {
        var node = new JsonObject
        {
            ["role"] = RoleName(message.Role),
            ["content"] = message.Content ?? string.Empty,
        };

        if (message.Role == AssistantChatRole.Tool && !string.IsNullOrWhiteSpace(message.ToolCallId))
        {
            node["tool_call_id"] = message.ToolCallId;
        }

        if (message.ToolCalls is { Count: > 0 })
        {
            var calls = new JsonArray();
            foreach (var call in message.ToolCalls)
            {
                calls.Add(new JsonObject
                {
                    ["id"] = call.Id,
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = call.ToolName,
                        ["arguments"] = call.ArgumentsJson,
                    },
                });
            }

            node["tool_calls"] = calls;
        }

        return node;
    }

    private static string RoleName(AssistantChatRole role) => role switch
    {
        AssistantChatRole.System => "system",
        AssistantChatRole.User => "user",
        AssistantChatRole.Assistant => "assistant",
        AssistantChatRole.Tool => "tool",
        _ => "user",
    };

    private static IReadOnlyList<AssistantToolCall> MapToolCalls(IReadOnlyList<GroqToolCall>? calls)
    {
        if (calls is null || calls.Count == 0)
        {
            return Array.Empty<AssistantToolCall>();
        }

        return calls
            .Where(call => !string.IsNullOrWhiteSpace(call.Function?.Name))
            .Select(call => new AssistantToolCall(
                string.IsNullOrWhiteSpace(call.Id) ? Guid.NewGuid().ToString("n") : call.Id!,
                call.Function!.Name!,
                string.IsNullOrWhiteSpace(call.Function.Arguments) ? "{}" : call.Function.Arguments!))
            .ToList();
    }

    private static string Trim(string? value, int max)
        => string.IsNullOrEmpty(value) ? string.Empty : value.Length <= max ? value : value[..max];

    /// <summary>
    /// حداکثر صبری که ارزش دارد کاربر پشت آن بماند.
    ///
    /// سقف رایگان گروک یک سطل توکن در دقیقه است (۸۰۰۰) و پس از یک پاسخِ ابزاردار
    /// حدود ۵۰ ثانیه طول می‌کشد تا دوباره پر شود؛ اندازه‌گیری‌شده روی همین حساب.
    /// کوتاه‌تر از آن یعنی صبر می‌کنیم و باز هم ۴۲۹ می‌گیریم. مهلت کل درخواست
    /// (Assistant:TimeoutSeconds) باید بزرگ‌تر از این باشد.
    /// </summary>
    private static readonly TimeSpan MaxRetryWait = TimeSpan.FromSeconds(60);

    /// <summary>
    /// چقدر تا باز شدن پنجرهٔ نرخ مانده است: اول Retry-After استاندارد، بعد
    /// سرشناسه‌های خود گروک. مقدار نامعلوم یا بلند یعنی صبر نکن.
    /// </summary>
    private static TimeSpan? ResolveRetryDelay(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta)
        {
            return Acceptable(delta);
        }

        if (retryAfter?.Date is { } date)
        {
            return Acceptable(date - DateTimeOffset.UtcNow);
        }

        foreach (var name in new[] { "x-ratelimit-reset-tokens", "x-ratelimit-reset-requests" })
        {
            if (response.Headers.TryGetValues(name, out var values)
                && ParseGroqDuration(values.FirstOrDefault()) is { } parsed)
            {
                return Acceptable(parsed);
            }
        }

        return null;

        static TimeSpan? Acceptable(TimeSpan value)
            => value > TimeSpan.Zero && value <= MaxRetryWait
                // یک ثانیه حاشیه، چون پنجره دقیقاً در همان لحظه باز نمی‌شود.
                ? value + TimeSpan.FromSeconds(1)
                : null;
    }

    /// <summary>خواندن قالب مدت گروک مثل «26.354s»، «1m30s» یا «500ms».</summary>
    internal static TimeSpan? ParseGroqDuration(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var text = raw.Trim();
        var total = TimeSpan.Zero;
        var number = new System.Text.StringBuilder();
        var matched = false;

        for (var index = 0; index < text.Length; index++)
        {
            var current = text[index];
            if (char.IsDigit(current) || current is '.' or ',')
            {
                number.Append(current == ',' ? '.' : current);
                continue;
            }

            if (number.Length == 0)
            {
                return null;
            }

            if (!double.TryParse(number.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value))
            {
                return null;
            }

            number.Clear();
            matched = true;

            if (current == 'm' && index + 1 < text.Length && text[index + 1] == 's')
            {
                total += TimeSpan.FromMilliseconds(value);
                index++;
            }
            else
            {
                total += current switch
                {
                    'h' => TimeSpan.FromHours(value),
                    'm' => TimeSpan.FromMinutes(value),
                    's' => TimeSpan.FromSeconds(value),
                    _ => TimeSpan.Zero,
                };
            }
        }

        // عدد بدون واحد یعنی ثانیه — همان قرارداد Retry-After.
        if (number.Length > 0
            && double.TryParse(number.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var trailing))
        {
            total += TimeSpan.FromSeconds(trailing);
            matched = true;
        }

        return matched ? total : null;
    }

    // ---- شکل پاسخ سازگار با OpenAI ------------------------------------------

    private sealed record GroqChatResponse(
        [property: JsonPropertyName("choices")] IReadOnlyList<GroqChatChoice>? Choices);

    private sealed record GroqChatChoice(
        [property: JsonPropertyName("message")] GroqResponseMessage? Message);

    private sealed record GroqResponseMessage(
        [property: JsonPropertyName("content")] string? Content,
        [property: JsonPropertyName("tool_calls")] IReadOnlyList<GroqToolCall>? ToolCalls);

    private sealed record GroqToolCall(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("function")] GroqToolFunction? Function);

    private sealed record GroqToolFunction(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("arguments")] string? Arguments);
}
