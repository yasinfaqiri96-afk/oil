using System.Text.Json;
using Google.GenAI;
using SysEnvironment = System.Environment;
using Google.GenAI.Types;
using Microsoft.Extensions.Options;
using PTGOilSystem.Web.Configuration;

namespace PTGOilSystem.Web.Services.Assistant;

/// <summary>
/// Provider گوگل Gemini با پکیج رسمی Google.GenAI.
///
/// کلید فقط از متغیر محیطی GEMINI_API_KEY خوانده می‌شود و در هیچ پیکربندی، Log،
/// پیام خطا یا خروجی Frontend نوشته نمی‌شود.
///
/// این کلاس هیچ منطق تجاری و هیچ دسترسی داده‌ای ندارد: فقط گفتگو را به Gemini
/// می‌برد و درخواست Tool را برمی‌گرداند. اجرای Tool و بررسی دسترسی کاربر همیشه
/// در AssistantService و AssistantToolRegistry انجام می‌شود.
/// </summary>
public sealed class GeminiAssistantProvider : IAssistantProvider
{
    public const string ProviderName = "Gemini";
    private const string ApiKeyVariable = "GEMINI_API_KEY";

    /// <summary>نقش‌هایی که Gemini در آرایهٔ contents می‌پذیرد.</summary>
    private const string RoleUser = "user";
    private const string RoleModel = "model";

    /// <summary>
    /// پیشوند شناسهٔ داخلی برای فراخوانی‌هایی که خود Gemini شناسه‌ای برایشان نداده است.
    /// سرویس برای جفت‌کردن نتیجهٔ ابزار به شناسه نیاز دارد، ولی چنین شناسه‌ای نباید
    /// به Gemini برگردد چون در نوبت مدل وجود ندارد.
    /// </summary>
    private const string SyntheticIdPrefix = "ptg-local-";

    private static bool IsSyntheticId(string? id)
        => string.IsNullOrWhiteSpace(id) || id!.StartsWith(SyntheticIdPrefix, StringComparison.Ordinal);

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly AssistantOptions _options;
    private readonly ILogger<GeminiAssistantProvider> _logger;
    private readonly Lazy<Client?> _client;

    public GeminiAssistantProvider(
        IOptions<AssistantOptions> options,
        ILogger<GeminiAssistantProvider> logger)
    {
        _options = options.Value;
        _logger = logger;
        _client = new Lazy<Client?>(CreateClient, isThreadSafe: true);
    }

    public string Name => ProviderName;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(SysEnvironment.GetEnvironmentVariable(ApiKeyVariable));

    public bool SupportsTools => true;

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

        var model = string.IsNullOrWhiteSpace(modelOverride) ? _options.Gemini.Model : modelOverride;
        if (string.IsNullOrWhiteSpace(model))
        {
            _logger.LogError("Assistant:Gemini:Model is empty.");
            return AssistantProviderResult.Failed(AssistantFailure.Unavailable);
        }

        var config = BuildConfig(messages, tools, maxOutputTokens);
        var contents = BuildContents(messages);
        if (contents.Count == 0)
        {
            return AssistantProviderResult.Failed(AssistantFailure.Unavailable);
        }

        try
        {
            var response = await client.Models
                .GenerateContentAsync(model, contents, config, cancellationToken)
                .ConfigureAwait(false);

            return ReadResponse(response);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ClientError ex)
        {
            return MapClientError(ex, model);
        }
        catch (ServerError ex)
        {
            // 5xx سمت گوگل. موقت است، پس لایهٔ بالاتر اجازهٔ تلاش با Provider جایگزین دارد.
            _logger.LogWarning("Gemini returned a server error ({Status}) for model '{Model}'.", ex.StatusCode, model);
            return AssistantProviderResult.Failed(AssistantFailure.ServiceError);
        }
        catch (HttpRequestException ex)
        {
            // قطع شبکه یا DNS. پیام استثنا هرگز کلید را در بر ندارد.
            // گذرا حساب می‌شود: سرویس اصلاً پاسخ نداده، پس Provider جایگزین باید تلاش کند.
            _logger.LogError(ex, "Gemini request failed at the network level.");
            return AssistantProviderResult.Failed(AssistantFailure.NetworkError);
        }
        catch (TaskCanceledException)
        {
            _logger.LogWarning("Gemini request timed out.");
            return AssistantProviderResult.Failed(AssistantFailure.Timeout);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Gemini request failed.");
            return AssistantProviderResult.Failed(AssistantFailure.Unavailable);
        }
    }

    /// <summary>
    /// تفکیک خطای گذرا از دائمی. ۴۲۹ موقت است؛ ۴۰۱/۴۰۳ و مدلِ در دسترس نبودن
    /// دائمی‌اند و نباید با Provider جایگزین پنهان شوند.
    /// </summary>
    internal AssistantProviderResult MapClientError(ClientError error, string model)
    {
        var status = error.StatusCode;

        if (status == 429)
        {
            _logger.LogWarning("Gemini rate limit reached (429) for model '{Model}'.", model);
            return AssistantProviderResult.Failed(AssistantFailure.RateLimited);
        }

        var message = error.Message ?? string.Empty;

        // بدنهٔ HTML یعنی پاسخ از خود Google نیامده است: یک واسط شبکه (Proxy یا
        // فیلتر مسیر) درخواست را بریده. تأییدشده روی همین سرور: در همان لحظه که
        // برنامه ۴۰۳ با بدنهٔ <!DOCTYPE html> می‌گرفت، تماس مستقیم با همان کلید
        // ۲۰۰ می‌داد. این خطای کلید نیست و اگر AccessDenied حساب شود، دستیار
        // بی‌دلیل به Provider جایگزین نمی‌رود و کاربر پیام گمراه‌کنندهٔ «کلید را
        // بررسی کنید» می‌بیند. گذرا حساب می‌شود تا Groq ادامه دهد.
        var trimmedBody = message.TrimStart();
        if (trimmedBody.StartsWith("<!DOCTYPE", StringComparison.OrdinalIgnoreCase)
            || trimmedBody.StartsWith("<html", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(
                "Gemini returned a non-JSON HTML body with {Status} for model '{Model}'. This is a network intermediary, not Google, so the request is treated as a transient network failure.",
                status,
                model);
            return AssistantProviderResult.Failed(AssistantFailure.NetworkError);
        }

        // ۴۰۰ با نشانهٔ منطقهٔ پشتیبانی‌نشده. تأیید شده روی سرویس واقعی: Gemini از
        // کشورهای مسدود ۴۰۰/FAILED_PRECONDITION با «User location is not supported»
        // می‌دهد، پیش از آنکه کلید یا مدل را اصلاً بررسی کند. دائمی است: نه با تلاش
        // دوباره حل می‌شود، نه با Provider جایگزین باید پنهان شود.
        if (status == 400 && message.Contains("location is not supported", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogError(
                "Gemini is not available from this server's region. The key and model were never checked. "
                + "Either run the app from a supported region or switch Assistant:Provider to another provider. "
                + "Service message: {Message}",
                Sanitize(message));
            return AssistantProviderResult.Failed(AssistantFailure.RegionUnsupported);
        }

        // ۴۰۰ با نشانهٔ کلید نامعتبر. تأیید شده روی سرویس واقعی: Gemini برای کلید
        // اشتباه ۴۰۰/API_KEY_INVALID می‌دهد، نه ۴۰۱. بدون این شاخه، کاربر پیام مبهم
        // «اتصال برقرار نشد» می‌دید به‌جای اینکه بداند کلید را باید درست کند.
        var looksLikeBadKey = status == 400
            && (message.Contains("API_KEY_INVALID", StringComparison.OrdinalIgnoreCase)
                || message.Contains("API key not valid", StringComparison.OrdinalIgnoreCase));

        if (status is 401 or 403 || looksLikeBadKey)
        {
            _logger.LogError(
                "Gemini refused the request with {Status} for model '{Model}'. Check that GEMINI_API_KEY is valid and that the key's region may use this model. Service message: {Message}",
                status,
                model,
                Sanitize(message));
            return AssistantProviderResult.Failed(AssistantFailure.AccessDenied);
        }

        // ۴۰۴ روی نام مدل یعنی این مدل برای این کلید/منطقه وجود ندارد. عمداً مدل
        // دیگری جایگزین نمی‌شود تا انتخاب مدل تصمیم آگاهانهٔ آدمی بماند.
        if (status == 404)
        {
            _logger.LogError(
                "Gemini model '{Model}' was not found for this key. No substitute model is selected automatically. Service message: {Message}",
                model,
                Sanitize(error.Message));
            return AssistantProviderResult.Failed(AssistantFailure.AccessDenied);
        }

        // ۴۰۰ باقی‌مانده یعنی خودِ درخواست ایراد دارد (Schema، شکل پیام، اندازه).
        // همان درخواست به Provider دوم هم می‌رود و همان‌جا هم رد می‌شود، پس دائمی
        // است و نباید پشت جایگزینی پنهان شود.
        if (status == 400)
        {
            _logger.LogError(
                "Gemini rejected the request shape with 400 for model '{Model}'. No fallback is attempted for a malformed request. Service message: {Message}",
                model,
                Sanitize(error.Message));
            return AssistantProviderResult.Failed(AssistantFailure.InvalidRequest);
        }

        _logger.LogError(
            "Gemini rejected the request with {Status} for model '{Model}'. Service message: {Message}",
            status,
            model,
            Sanitize(error.Message));
        return AssistantProviderResult.Failed(AssistantFailure.Unavailable);
    }

    private GenerateContentConfig BuildConfig(
        IReadOnlyList<AssistantChatMessage> messages,
        IReadOnlyList<AssistantToolDefinition> tools,
        int maxOutputTokens)
    {
        var config = new GenerateContentConfig
        {
            MaxOutputTokens = maxOutputTokens,
            // دمای پایین: راهنمای نرم‌افزار باید تکرارپذیر و بدون خیال‌پردازی باشد.
            Temperature = 0.2,
        };

        // Gemini نقش system ندارد؛ راهنمای سیستمی فیلد جداگانه است.
        var systemText = string.Join(
            "\n\n",
            messages.Where(message => message.Role == AssistantChatRole.System)
                .Select(message => message.Content)
                .Where(text => !string.IsNullOrWhiteSpace(text)));

        if (!string.IsNullOrWhiteSpace(systemText))
        {
            config.SystemInstruction = new Content
            {
                Parts = new List<Part> { new() { Text = systemText } },
            };
        }

        if (tools.Count > 0)
        {
            config.Tools = new List<Tool>
            {
                new()
                {
                    FunctionDeclarations = tools.Select(tool => new FunctionDeclaration
                    {
                        Name = tool.Name,
                        Description = tool.Description,
                        // Schema همان JSON Schema ابزار است و در یک جا نگه داشته می‌شود
                        // تا تعریف ابزار بین Gemini و Groq از هم جدا نیفتد.
                        ParametersJsonSchema = ParseSchema(tool.ParametersJsonSchema),
                    }).ToList(),
                },
            };
        }

        return config;
    }

    private object ParseSchema(string schemaJson)
    {
        try
        {
            using var document = JsonDocument.Parse(schemaJson);
            return document.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "An assistant tool declared an invalid JSON schema.");
            return new { type = "object" };
        }
    }

    /// <summary>
    /// نگاشت گفتگو به contents. پیام system حذف می‌شود (جدا فرستاده شد) و نتیجهٔ
    /// Tool به شکل functionResponse با نقش user برمی‌گردد — همان قراردادی که
    /// Gemini انتظار دارد.
    ///
    /// نتیجهٔ چند ابزارِ یک دور، در یک نوبت user کنار هم می‌آید: Gemini برای هر
    /// functionCall همان نوبت، یک functionResponse در همان نوبتِ پاسخ می‌خواهد.
    /// </summary>
    internal List<Content> BuildContents(IReadOnlyList<AssistantChatMessage> messages)
    {
        var contents = new List<Content>();

        for (var index = 0; index < messages.Count; index++)
        {
            var message = messages[index];

            switch (message.Role)
            {
                case AssistantChatRole.System:
                    continue;

                case AssistantChatRole.User:
                    if (!string.IsNullOrWhiteSpace(message.Content))
                    {
                        contents.Add(TextContent(RoleUser, message.Content));
                    }

                    break;

                case AssistantChatRole.Assistant:
                    contents.Add(BuildAssistantContent(message));
                    break;

                case AssistantChatRole.Tool:
                    var responses = new List<Part>();
                    while (index < messages.Count && messages[index].Role == AssistantChatRole.Tool)
                    {
                        responses.Add(ToolResultPart(messages[index]));
                        index++;
                    }

                    index--;
                    contents.Add(new Content { Role = RoleUser, Parts = responses });
                    break;
            }
        }

        return contents;
    }

    /// <summary>
    /// نوبت مدل. اگر خودِ Provider آن را ساخته باشد، عیناً همان Content برمی‌گردد.
    ///
    /// این نکته حیاتی است: Gemini در هر functionCall یک ThoughtSignature مبهم
    /// می‌گذارد و در درخواست بعدی همان امضا را می‌خواهد. اگر نوبت مدل از روی نام و
    /// آرگومان بازسازی شود، امضا از بین می‌رود و سرویس با
    /// «Function call is missing a thought_signature in functionCall parts» رد می‌کند.
    /// پس هیچ Part حذف، مرتب یا بازساخته نمی‌شود؛ امضا هم خوانده، رمزگشایی، Log یا
    /// ذخیره نمی‌شود — فقط همان‌جا داخل Part باقی می‌ماند.
    /// </summary>
    private Content BuildAssistantContent(AssistantChatMessage message)
    {
        if (message.ProviderContent is Content native && native.Parts is { Count: > 0 })
        {
            // فقط نبودِ نقش جبران می‌شود و همان نمونه‌های Part دوباره استفاده می‌شوند.
            return string.IsNullOrWhiteSpace(native.Role)
                ? new Content { Role = RoleModel, Parts = native.Parts }
                : native;
        }

        // مسیر جایگزین، فقط برای نوبتی که از این Provider نیامده: تاریخچهٔ ارسالی
        // Frontend یا گفتگویی که با Provider دیگری شروع شده. چنین نوبتی اصلاً
        // ThoughtSignature ندارد، پس چیزی هم از دست نمی‌رود.
        var parts = new List<Part>();

        if (!string.IsNullOrWhiteSpace(message.Content))
        {
            parts.Add(new Part { Text = message.Content });
        }

        if (message.ToolCalls is { Count: > 0 })
        {
            foreach (var call in message.ToolCalls)
            {
                parts.Add(new Part
                {
                    FunctionCall = new FunctionCall
                    {
                        Id = call.Id,
                        Name = call.ToolName,
                        Args = ToArgs(call.ArgumentsJson),
                    },
                });
            }
        }

        if (parts.Count == 0)
        {
            parts.Add(new Part { Text = string.Empty });
        }

        return new Content { Role = RoleModel, Parts = parts };
    }

    private static Part ToolResultPart(AssistantChatMessage message)
        => new()
        {
            FunctionResponse = new FunctionResponse
            {
                // شناسه فقط وقتی برمی‌گردد که خود Gemini آن را داده باشد. مدل‌های
                // Gemini معمولاً functionCall را بدون id می‌فرستند و شناسهٔ ساختگی
                // در functionResponse بی‌صاحب است، پس در آن حالت اصلاً نوشته نمی‌شود.
                Id = IsSyntheticId(message.ToolCallId) ? null : message.ToolCallId,
                Name = message.ToolName,
                // خروجی ابزار متن فشرده است؛ در یک فیلد ساده بسته می‌شود.
                Response = new Dictionary<string, object>
                {
                    ["result"] = message.Content ?? string.Empty,
                },
            },
        };

    private static Content TextContent(string role, string text)
        => new()
        {
            Role = role,
            Parts = new List<Part> { new() { Text = text } },
        };

    private Dictionary<string, object> ToArgs(string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            return new Dictionary<string, object>();
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, object>>(argumentsJson, SerializerOptions)
                   ?? new Dictionary<string, object>();
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Could not replay tool-call arguments back to Gemini.");
            return new Dictionary<string, object>();
        }
    }

    internal AssistantProviderResult ReadResponse(GenerateContentResponse response)
    {
        // نوبت مدل همان‌طور که رسید نگه داشته می‌شود تا در درخواست بعدی دست‌نخورده
        // برگردد؛ فهرست فراخوانی‌ها هم از همین Content خوانده می‌شود تا آنچه اجرا
        // می‌شود دقیقاً همان چیزی باشد که به Gemini پس داده می‌شود.
        var modelContent = response.Candidates?.FirstOrDefault()?.Content;

        var calls = modelContent?.Parts?
            .Select(part => part.FunctionCall)
            .Where(call => call is not null)
            .Select(call => call!)
            .ToList();

        if (calls is not { Count: > 0 })
        {
            calls = response.FunctionCalls;
        }

        if (calls is { Count: > 0 })
        {
            var mapped = calls
                .Where(call => !string.IsNullOrWhiteSpace(call.Name))
                .Select(call => new AssistantToolCall(
                    string.IsNullOrWhiteSpace(call.Id) ? SyntheticIdPrefix + Guid.NewGuid().ToString("n") : call.Id!,
                    call.Name!,
                    SerializeArgs(call.Args)))
                .ToList();

            if (mapped.Count > 0)
            {
                return AssistantProviderResult.RequestTools(TextOf(response), mapped, modelContent);
            }
        }

        var text = TextOf(response);
        return string.IsNullOrWhiteSpace(text)
            ? AssistantProviderResult.Failed(AssistantFailure.Unavailable)
            : AssistantProviderResult.Success(text);
    }

    private static string? TextOf(GenerateContentResponse response)
    {
        var text = response.Text?.Trim();
        if (!string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        // پاسخ‌هایی که هم متن و هم فراخوانی ابزار دارند، گاهی Text خلاصه را پر
        // نمی‌کنند؛ در آن حالت متن از قطعه‌ها جمع می‌شود.
        var parts = response.Candidates?.FirstOrDefault()?.Content?.Parts;
        if (parts is null)
        {
            return null;
        }

        var joined = string.Join(
            "\n",
            parts.Select(part => part.Text).Where(value => !string.IsNullOrWhiteSpace(value)));

        return string.IsNullOrWhiteSpace(joined) ? null : joined.Trim();
    }

    private string SerializeArgs(Dictionary<string, object>? args)
    {
        if (args is null || args.Count == 0)
        {
            return "{}";
        }

        try
        {
            return JsonSerializer.Serialize(args, SerializerOptions);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not serialize Gemini tool-call arguments.");
            return "{}";
        }
    }

    private Client? CreateClient()
    {
        var apiKey = SysEnvironment.GetEnvironmentVariable(ApiKeyVariable);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _logger.LogWarning("{Variable} is not set. The Gemini assistant provider stays unavailable.", ApiKeyVariable);
            return null;
        }

        return new Client(apiKey: apiKey);
    }

    /// <summary>
    /// پیام سرویس پیش از رفتن به Log کوتاه می‌شود. کلید در پیام خطای Gemini نمی‌آید،
    /// ولی طول پیام کران‌دار می‌ماند تا Log با بدنهٔ بلند پر نشود.
    /// </summary>
    private static string Sanitize(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return string.Empty;
        }

        var trimmed = message.Length <= 400 ? message : message[..400];

        // دفاع در عمق: اگر روزی کلید در پیام سرویس بازتاب داده شد، در Log ننشیند.
        var apiKey = SysEnvironment.GetEnvironmentVariable(ApiKeyVariable);
        return string.IsNullOrWhiteSpace(apiKey)
            ? trimmed
            : trimmed.Replace(apiKey, "***", StringComparison.Ordinal);
    }
}
