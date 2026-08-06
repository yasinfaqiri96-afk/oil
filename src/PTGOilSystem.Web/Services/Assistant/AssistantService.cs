using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using PTGOilSystem.Web.Configuration;
using PTGOilSystem.Web.Security;
using PTGOilSystem.Web.Services.Assistant.Tools;

namespace PTGOilSystem.Web.Services.Assistant;

public interface IAssistantService
{
    bool IsConfigured { get; }

    Task<AssistantAnswer> AskAsync(AssistantAskRequest request, ClaimsPrincipal user, CancellationToken cancellationToken);
}

/// <summary>
/// «راهنمای هوشمند». دو کار می‌کند: آموزش کار با نرم‌افزار، و پاسخ به سؤال دربارهٔ
/// داده واقعی از راه Toolهای فقط‌خواندنی.
///
/// مرزهای ثابت:
///   • هیچ عملیات نوشتن، تغییر یا حذفی وجود ندارد — هیچ Tool نوشتنی ثبت نشده است.
///   • مدل SQL نمی‌نویسد؛ فقط Toolهای ثبت‌شده را صدا می‌زند.
///   • دسترسی هر Tool با همان قاعدهٔ ناوبری کاربر سنجیده می‌شود، پس دستیار هرگز
///     چیزی را فاش نمی‌کند که کاربر خودش نتواند در برنامه باز کند.
///   • کلید API هرگز به Frontend نمی‌رود.
/// </summary>
public sealed class AssistantService : IAssistantService
{
    private const string BaseSystemPrompt =
        "تو دستیار نرم‌افزار مدیریت عملیات نفت و گاز (PTG Oil System) هستی. " +
        "دو وظیفه داری: راهنمایی کاربر برای کار با نرم‌افزار، و پاسخ به سؤال دربارهٔ اطلاعات ثبت‌شده. " +
        "پاسخ‌ها را به زبان دری ساده، کوتاه و دقیق بده. " +
        "نام دکمه‌ها و فیلدهای واقعی نرم‌افزار را دقیق همان‌طور که در Context آمده استفاده کن. " +
        "هرگز حدس نزن. اگر معلومات کافی نداری، واضح بگو که معلومات موجود نیست و کاربر از کدام صفحه می‌تواند ببیند. " +
        "هیچ عملیات مالی یا تغییری در اطلاعات انجام نده و هرگز نگو که خودت چیزی را ثبت، حذف یا اصلاح می‌کنی؛ " +
        "تو فقط می‌توانی بخوانی.";

    private const string ToolSystemPrompt =
        "\n\nبرای پاسخ دربارهٔ ارقام واقعی — مانده، موجودی، قرارداد — حتماً ابزارهای موجود را صدا بزن " +
        "و هرگز عدد از خودت نساز. اگر برای پاسخ به شناسه نیاز داری، اول search_party یا get_contracts را صدا بزن. " +
        "ارقام را دقیقاً همان‌طور که ابزار برگردانده گزارش کن و واحد (دلار یا MT) را بنویس. " +
        "اگر ابزار گفت کاربر دسترسی ندارد، همان را محترمانه به کاربر بگو و عدد نساز. " +
        "اگر ابزار چیزی برنگرداند، بگو رکوردی ثبت نشده است. " +
        "هیچ محاسبه‌ای خودت انجام نده: جمع، تفریق، مانده، باقی‌مانده و درصد را همان‌طور که ابزار داده گزارش کن. " +
        "\n\nدر پایان پاسخ، لینک صفحه‌های مربوط را با همین قالب بده و فقط از شناسه‌ای که ابزار برگردانده استفاده کن:\n" +
        "- بارگیری: [بارگیری](/Loading/Details/{id})\n" +
        "- قرارداد: [قرارداد](/Contracts/Details/{id})\n" +
        "- پروندهٔ قرارداد: [پروندهٔ قرارداد](/ContractJourney/Details?contractId={id})\n" +
        "- تأمین‌کننده: [تأمین‌کننده](/Suppliers/Details/{id})  و صورتحساب او: [صورتحساب](/Suppliers/{id}/Statement)\n" +
        "- مشتری: [مشتری](/Customers/Details/{id})  و صورتحساب او: [صورتحساب](/Customers/{id}/Statement)\n" +
        "- موجودی: [موجودی](/Inventory)\n" +
        "اگر شناسه‌ای نداری، لینک نساز.";

    private const string NoToolSystemPrompt =
        "\n\nتو به اطلاعات ثبت‌شده دسترسی نداری. دربارهٔ مبالغ، مانده، موجودی یا رکورد مشخص هرگز عدد نگو؛ " +
        "فقط بگو کاربر آن را از کدام صفحه ببیند.";

    private readonly AssistantOptions _options;
    private readonly AssistantPageCatalog _catalog;
    private readonly IAssistantToolRegistry _tools;
    private readonly ILogger<AssistantService> _logger;
    private readonly IReadOnlyList<IAssistantProvider> _providers;

    public AssistantService(
        IOptions<AssistantOptions> options,
        AssistantPageCatalog catalog,
        IAssistantToolRegistry tools,
        IEnumerable<IAssistantProvider> providers,
        ILogger<AssistantService> logger)
    {
        _options = options.Value;
        _catalog = catalog;
        _tools = tools;
        _providers = providers.ToList();
        _logger = logger;
    }

    public bool IsConfigured => _options.Enabled && ResolveProvider()?.IsConfigured == true;

    /// <summary>انتخاب Provider از روی Assistant.Provider؛ در نبود تطابق، اولین Provider ثبت‌شده.</summary>
    private IAssistantProvider? ResolveProvider()
    {
        var requested = _options.Provider;
        var match = FindProvider(requested);

        if (match is null && !string.IsNullOrWhiteSpace(requested))
        {
            _logger.LogWarning("Assistant provider '{Provider}' is unknown. Falling back to the first registered provider.", requested);
        }

        return match ?? _providers.FirstOrDefault();
    }

    private IAssistantProvider? FindProvider(string? name)
        => string.IsNullOrWhiteSpace(name)
            ? null
            : _providers.FirstOrDefault(provider => string.Equals(provider.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Provider جایگزین، فقط اگر پیکربندی شده، شناخته‌شده، کلیددار و غیر از Provider اصلی باشد.
    /// </summary>
    private IAssistantProvider? ResolveFallbackProvider(IAssistantProvider primary)
    {
        var fallback = FindProvider(_options.FallbackProvider);
        if (fallback is null)
        {
            if (!string.IsNullOrWhiteSpace(_options.FallbackProvider))
            {
                _logger.LogWarning("Assistant fallback provider '{Provider}' is unknown.", _options.FallbackProvider);
            }

            return null;
        }

        if (string.Equals(fallback.Name, primary.Name, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (!fallback.IsConfigured)
        {
            _logger.LogWarning(
                "Assistant fallback provider '{Provider}' has no API key set, so no fallback is possible.",
                fallback.Name);
            return null;
        }

        return fallback;
    }

    /// <summary>
    /// خطای گذرا؛ فقط این‌ها اجازهٔ رفتن سراغ Provider جایگزین را می‌دهند.
    /// خطای مجوز، کلید نامعتبر و منطقهٔ پشتیبانی‌نشده عمداً بیرون است تا با
    /// جایگزینی پنهان نشود و مدیر سیستم آن را ببیند.
    /// </summary>
    private static bool IsTransient(AssistantFailure failure)
        => failure is AssistantFailure.RateLimited
            or AssistantFailure.Timeout
            or AssistantFailure.ServiceError
            or AssistantFailure.NetworkError;

    public async Task<AssistantAnswer> AskAsync(
        AssistantAskRequest request,
        ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            return new AssistantAnswer(false, "راهنمای هوشمند در این سیستم غیرفعال است.");
        }

        var question = Truncate(request.Question?.Trim(), _options.MaxQuestionLength);
        if (string.IsNullOrWhiteSpace(question))
        {
            return new AssistantAnswer(false, "لطفاً سؤال خود را بنویسید.");
        }

        var provider = ResolveProvider();
        if (provider is null)
        {
            _logger.LogError("No assistant provider is registered.");
            return new AssistantAnswer(false, "اتصال به دستیار برقرار نیست.");
        }

        if (!provider.IsConfigured)
        {
            return new AssistantAnswer(false, MissingKeyMessage(provider.Name));
        }

        var toolsEnabled = _options.EnableTools && provider.SupportsTools;
        var availableTools = toolsEnabled
            ? _tools.GetAvailableTools(user)
            : Array.Empty<AssistantToolDefinition>();

        // نقشهٔ صفحات فقط شامل بخش‌هایی است که همین کاربر اجازهٔ دیدنشان را دارد،
        // پس دستیار هرگز کاربر را به صفحه‌ای که برایش بسته است راهنمایی نمی‌کند.
        // فشرده است (فقط عنوان صفحه‌ها): شرح کامل هر صفحه فقط برای صفحهٔ جاری فرستاده
        // می‌شود تا حجم هر درخواست کوچک بماند.
        var siteMap = _catalog.BuildSiteMap(
            controller => RoleAccessRules.CanAccessController(user, controller),
            compact: true);

        var messages = BuildConversation(request, question, user, availableTools, siteMap);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(_options.TimeoutSeconds, 5, 120)));

        var maxOutputTokens = Math.Clamp(_options.MaxOutputTokens, 128, 8000);

        try
        {
            var outcome = await RunConversationAsync(
                provider, modelOverride: null, messages, availableTools, maxOutputTokens, user, timeout.Token);

            // فقط خطای گذرا سراغ Provider جایگزین می‌رود. خطای مجوز یا کلید نامعتبر
            // باید به همان شکل دیده شود، وگرنه اشکال واقعی پشت جایگزینی پنهان می‌ماند.
            if (outcome.Answer is null && IsTransient(outcome.Failure))
            {
                var fallback = ResolveFallbackProvider(provider);
                if (fallback is not null)
                {
                    _logger.LogWarning(
                        "Assistant provider '{Primary}' failed with {Failure}. Trying the fallback provider '{Fallback}'.",
                        provider.Name,
                        outcome.Failure,
                        fallback.Name);

                    // گفتگو ادامه می‌یابد، از نو ساخته نمی‌شود: صفحهٔ جاری، شناسهٔ رکورد،
                    // تاریخچه و نتیجهٔ ابزارهایی که قبلاً اجرا شده‌اند باید حفظ شوند،
                    // وگرنه کاربر همان کار را دوباره می‌بیند و ابزارها بیهوده دوباره
                    // اجرا می‌شوند. فقط نوبتِ خامِ Provider اول (ProviderContent) حذف
                    // می‌شود چون برای Provider دوم بی‌معنی است.
                    var carriedMessages = ForOtherProvider(outcome.Messages ?? messages);
                    var fallbackTools = _options.EnableTools && fallback.SupportsTools
                        ? availableTools
                        : Array.Empty<AssistantToolDefinition>();

                    var fallbackOutcome = await RunConversationAsync(
                        fallback,
                        _options.FallbackModel,
                        carriedMessages,
                        fallbackTools,
                        maxOutputTokens,
                        user,
                        timeout.Token,
                        outcome.UsedTools);

                    if (fallbackOutcome.Answer is not null)
                    {
                        return fallbackOutcome.Answer;
                    }

                    return new AssistantAnswer(false, FailureMessage(fallbackOutcome.Failure, fallback.Name));
                }
            }

            return outcome.Answer ?? new AssistantAnswer(false, FailureMessage(outcome.Failure, provider.Name));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Assistant request timed out after {Seconds}s.", _options.TimeoutSeconds);
            return new AssistantAnswer(false, FailureMessage(AssistantFailure.Timeout, provider.Name));
        }
        catch (OperationCanceledException)
        {
            return new AssistantAnswer(false, "درخواست لغو شد.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Assistant request failed.");
            return new AssistantAnswer(false, FailureMessage(AssistantFailure.Unavailable, provider.Name));
        }
    }

    /// <summary>
    /// نتیجهٔ یک گفتگوی کامل: یا پاسخ آماده، یا نوع شکست برای تصمیم جایگزینی.
    /// <c>Messages</c> و <c>UsedTools</c> برای ادامهٔ همان گفتگو با Provider جایگزین
    /// نگه داشته می‌شوند تا نتیجهٔ ابزارهای اجراشده از دست نرود.
    /// </summary>
    private readonly record struct ConversationOutcome(
        AssistantAnswer? Answer,
        AssistantFailure Failure,
        List<AssistantChatMessage>? Messages = null,
        List<string>? UsedTools = null);

    /// <summary>
    /// آماده‌سازی گفتگو برای Provider دیگر: نوبت خام مدل (ProviderContent) حذف
    /// می‌شود چون فقط برای همان Provider معنی دارد. متن، درخواست ابزار، شناسهٔ
    /// فراخوانی و نتیجهٔ ابزار همگی می‌مانند تا کار انجام‌شده دوباره تکرار نشود.
    /// </summary>
    private static List<AssistantChatMessage> ForOtherProvider(IEnumerable<AssistantChatMessage> messages)
        => messages
            .Select(message => message.ProviderContent is null ? message : message with { ProviderContent = null })
            .ToList();

    /// <summary>
    /// ساخت گفتگوی اولیه. جدا نگه داشته شده چون Provider جایگزین باید از یک گفتگوی
    /// تازه شروع کند، نه از پیام‌های ابزارِ نیمه‌کارهٔ Provider قبلی.
    ///
    /// آنچه فرستاده می‌شود عمداً کوچک است: راهنمای سیستمی، نقشهٔ فشردهٔ صفحاتِ مجاز،
    /// ورودی page-guide مربوط به همین صفحه، ساختار صفحهٔ جاری، تاریخچهٔ محدود و
    /// تعریف ابزارهای مجاز همین کاربر. هیچ کدی از پروژه فرستاده نمی‌شود.
    /// </summary>
    private List<AssistantChatMessage> BuildConversation(
        AssistantAskRequest request,
        string question,
        ClaimsPrincipal user,
        IReadOnlyList<AssistantToolDefinition> availableTools,
        string siteMap)
    {
        var systemPrompt = new StringBuilder(BaseSystemPrompt)
            .Append(availableTools.Count > 0 ? ToolSystemPrompt : NoToolSystemPrompt);

        if (!string.IsNullOrWhiteSpace(siteMap))
        {
            systemPrompt.AppendLine()
                .AppendLine()
                .AppendLine("### صفحات در دسترس این کاربر")
                .AppendLine("برای راهنمایی «کجا بروم؟» فقط از همین فهرست استفاده کن و صفحهٔ خارج از آن را پیشنهاد نکن.")
                .Append(siteMap);
        }

        var messages = new List<AssistantChatMessage>
        {
            AssistantChatMessage.System(systemPrompt.ToString()),
        };

        messages.AddRange(BuildHistory(request.History));
        messages.Add(AssistantChatMessage.User(
            BuildUserMessage(question, request.Context ?? new AssistantPageContext(), user)));

        return messages;
    }

    /// <summary>
    /// حلقهٔ گفتگو با یک Provider مشخص، شامل اجرای ابزار. کران‌دار است: پس از
    /// MaxToolIterations دور، ابزار دیگر پیشنهاد نمی‌شود تا مدل مجبور به پاسخ نهایی شود.
    /// </summary>
    private async Task<ConversationOutcome> RunConversationAsync(
        IAssistantProvider provider,
        string? modelOverride,
        List<AssistantChatMessage> messages,
        IReadOnlyList<AssistantToolDefinition> availableTools,
        int maxOutputTokens,
        ClaimsPrincipal user,
        CancellationToken cancellationToken,
        IEnumerable<string>? alreadyUsedTools = null)
    {
        var maxIterations = availableTools.Count > 0 ? Math.Clamp(_options.MaxToolIterations, 1, 8) : 1;

        // ابزارهایی که Provider قبلی اجرا کرده بود هم منبع همین پاسخ‌اند و باید در
        // فهرست «منبع» بمانند.
        var usedTools = alreadyUsedTools?.ToList() ?? new List<string>();

        for (var iteration = 0; iteration < maxIterations; iteration++)
        {
            // در آخرین دور ابزار پیشنهاد نمی‌شود تا مدل مجبور شود جواب نهایی بدهد
            // و حلقه قطعاً تمام شود.
            var offered = iteration == maxIterations - 1
                ? Array.Empty<AssistantToolDefinition>()
                : availableTools;

            var result = await provider
                .CompleteAsync(messages, offered, maxOutputTokens, modelOverride, cancellationToken)
                .ConfigureAwait(false);

            // سرویس، فراخوانی ابزارِ مدل را نپذیرفت. کاربر نباید پیام قطع ارتباط
            // ببیند: همان سؤال یک بار بدون ابزار پرسیده می‌شود تا دست‌کم پاسخ
            // راهنمایی بگیرد. فقط یک بار، تا حلقه ایجاد نشود.
            if (result.Failure == AssistantFailure.ToolCallRejected && offered.Count > 0)
            {
                _logger.LogWarning("Retrying the assistant request without tools after a rejected tool call.");

                var retry = await provider
                    .CompleteAsync(messages, Array.Empty<AssistantToolDefinition>(), maxOutputTokens, modelOverride, cancellationToken)
                    .ConfigureAwait(false);

                return retry.Failure == AssistantFailure.None && !string.IsNullOrWhiteSpace(retry.Text)
                    ? new ConversationOutcome(new AssistantAnswer(true, retry.Text) { UsedTools = usedTools }, AssistantFailure.None, messages, usedTools)
                    : new ConversationOutcome(null, AssistantFailure.Unavailable, messages, usedTools);
            }

            if (result.Failure != AssistantFailure.None)
            {
                return new ConversationOutcome(null, result.Failure, messages, usedTools);
            }

            if (!result.HasToolCalls)
            {
                return string.IsNullOrWhiteSpace(result.Text)
                    ? new ConversationOutcome(null, AssistantFailure.Unavailable, messages, usedTools)
                    : new ConversationOutcome(new AssistantAnswer(true, result.Text) { UsedTools = usedTools }, AssistantFailure.None, messages, usedTools);
            }

            // درخواست Tool مدل باید عیناً در تاریخچه بماند، وگرنه سرویس
            // نتیجهٔ Tool را بی‌صاحب می‌بیند و درخواست بعدی رد می‌شود.
            // ProviderContent هم دست‌نخورده منتقل می‌شود: Gemini نوبت مدل را فقط
            // با تمام قطعه‌های اصلی‌اش می‌پذیرد و بازسازی آن درخواست بعدی را رد می‌کند.
            messages.Add(new AssistantChatMessage(
                AssistantChatRole.Assistant,
                result.Text ?? string.Empty,
                ToolCalls: result.ToolCalls,
                ProviderContent: result.ProviderContent));

            foreach (var call in result.ToolCalls!)
            {
                var output = await _tools.ExecuteAsync(call, user, cancellationToken).ConfigureAwait(false);
                if (!usedTools.Contains(call.ToolName, StringComparer.OrdinalIgnoreCase))
                {
                    usedTools.Add(call.ToolName);
                }

                messages.Add(AssistantChatMessage.ToolResult(call.Id, call.ToolName, output));
            }
        }

        _logger.LogWarning("Assistant hit the tool iteration limit without producing an answer.");
        return new ConversationOutcome(
            new AssistantAnswer(false, "پاسخ‌گویی به این سؤال طولانی شد. لطفاً سؤال را ساده‌تر بپرسید."),
            AssistantFailure.None,
            messages,
            usedTools);
    }

    /// <summary>
    /// تاریخچه از Frontend می‌آید، پس بی‌اعتماد است: فقط نقش‌های شناخته‌شده پذیرفته
    /// می‌شوند، طول هر پیام و تعداد نوبت‌ها کران‌دار است.
    /// </summary>
    private IEnumerable<AssistantChatMessage> BuildHistory(List<AssistantTurn>? history)
    {
        if (history is null || history.Count == 0)
        {
            yield break;
        }

        var maxMessages = Math.Clamp(_options.MaxHistoryTurns, 0, 20) * 2;
        if (maxMessages == 0)
        {
            yield break;
        }

        var recent = history.Count > maxMessages
            ? history.Skip(history.Count - maxMessages)
            : history;

        foreach (var turn in recent)
        {
            var content = Truncate(turn.Content?.Trim(), _options.MaxQuestionLength * 2);
            if (string.IsNullOrWhiteSpace(content))
            {
                continue;
            }

            if (string.Equals(turn.Role, "user", StringComparison.OrdinalIgnoreCase))
            {
                yield return AssistantChatMessage.User(content);
            }
            else if (string.Equals(turn.Role, "assistant", StringComparison.OrdinalIgnoreCase))
            {
                yield return AssistantChatMessage.Assistant(content);
            }
        }
    }

    /// <summary>نام متغیر محیطی هر Provider. مقدار کلید هرگز اینجا خوانده یا نمایش داده نمی‌شود.</summary>
    private static string KeyVariableName(string providerName)
    {
        if (string.Equals(providerName, GeminiAssistantProvider.ProviderName, StringComparison.OrdinalIgnoreCase))
        {
            return "GEMINI_API_KEY";
        }

        return string.Equals(providerName, GroqAssistantProvider.ProviderName, StringComparison.OrdinalIgnoreCase)
            ? "GROQ_API_KEY"
            : "ANTHROPIC_API_KEY";
    }

    private static string MissingKeyMessage(string providerName)
        => $"اتصال به دستیار برقرار نیست. مدیر سیستم باید متغیر محیطی {KeyVariableName(providerName)} را تنظیم کند.";

    private static string FailureMessage(AssistantFailure failure, string providerName) => failure switch
    {
        AssistantFailure.NotConfigured => MissingKeyMessage(providerName),
        AssistantFailure.RateLimited => "ظرفیت رایگان دستیار فعلاً تکمیل شده است. لطفاً بعداً دوباره تلاش کنید.",
        AssistantFailure.Timeout => "پاسخ‌گویی بیش از حد طول کشید. لطفاً دوباره تلاش کنید.",
        AssistantFailure.ToolCallRejected => "خواندن این معلومات ناموفق بود. لطفاً سؤال را واضح‌تر بپرسید.",
        AssistantFailure.ServiceError => "سرویس دستیار فعلاً در دسترس نیست. لطفاً بعداً دوباره تلاش کنید.",
        AssistantFailure.NetworkError => "ارتباط با سرویس دستیار برقرار نشد. لطفاً بعداً دوباره تلاش کنید.",

        // ایراد از خودِ درخواست است، نه از کاربر و نه از شبکه. کاربر نباید منتظر
        // بماند و مدیر سیستم باید در Log ببیند.
        AssistantFailure.InvalidRequest => "درخواست دستیار پذیرفته نشد. لطفاً سؤال را ساده‌تر بپرسید.",

        // عمداً صریح: این خطا با تلاش دوباره حل نمی‌شود و مدیر سیستم باید کلید یا
        // دسترسی مدل را درست کند. خودِ کلید هرگز در پیام نمی‌آید.
        AssistantFailure.AccessDenied =>
            $"دسترسی دستیار به سرویس رد شد. مدیر سیستم باید اعتبار {KeyVariableName(providerName)} و دسترسی مدل را بررسی کند.",

        // تلاش دوباره بی‌فایده است؛ کاربر نباید منتظر بماند.
        AssistantFailure.RegionUnsupported =>
            $"سرویس دستیار ({providerName}) از منطقهٔ این سرور در دسترس نیست. مدیر سیستم باید سرویس دیگری را انتخاب کند.",

        _ => "اتصال به دستیار برقرار نشد. لطفاً بعداً دوباره تلاش کنید.",
    };

    /// <summary>
    /// ساخت پیام کاربر: راهنمای ثابت صفحه + ساختار زندهٔ همان صفحه.
    /// هیچ مقدار رکورد یا مبلغی از صفحه خوانده نمی‌شود و اندازه‌ها محدود می‌شوند.
    /// </summary>
    private string BuildUserMessage(string question, AssistantPageContext context, ClaimsPrincipal user)
    {
        var builder = new StringBuilder();
        builder.AppendLine("### وضعیت صفحه فعلی");

        var entry = _catalog.FindPage(context.Controller);
        if (entry is not null)
        {
            if (!string.IsNullOrWhiteSpace(entry.Module)) builder.AppendLine($"بخش: {entry.Module}");
            if (!string.IsNullOrWhiteSpace(entry.Title)) builder.AppendLine($"صفحه: {entry.Title}");
            if (!string.IsNullOrWhiteSpace(entry.Purpose)) builder.AppendLine($"کار این صفحه: {entry.Purpose}");
            if (!string.IsNullOrWhiteSpace(entry.Start)) builder.AppendLine($"شروع کار: {entry.Start}");
        }

        var actionHint = _catalog.FindAction(context.Action);
        if (!string.IsNullOrWhiteSpace(actionHint))
        {
            builder.AppendLine($"نوع نما: {actionHint}");
        }

        if (!string.IsNullOrWhiteSpace(context.Route)) builder.AppendLine($"مسیر: {Truncate(context.Route, 200)}");
        if (!string.IsNullOrWhiteSpace(context.PageTitle)) builder.AppendLine($"عنوان نمایش‌داده‌شده: {Truncate(context.PageTitle, 150)}");

        // «همین بارگیری» بدون شناسه بی‌معنی است و مدل شناسه از خودش می‌سازد.
        // شناسه از مسیر همین صفحه می‌آید و اجازهٔ دیدنش موقع اجرای ابزار سنجیده می‌شود.
        var record = AssistantPageRecordResolver.Resolve(context);
        if (record is { } pageRecord)
        {
            builder.AppendLine(
                $"رکورد باز در این صفحه: {pageRecord.Kind} با شناسه {pageRecord.Id}. "
                + $"اگر کاربر گفت «همین {pageRecord.Kind}» یا «این صفحه»، همین شناسه را در ورودی {pageRecord.ToolArgument} ابزار بفرست و شناسهٔ دیگری نساز.");
        }

        var fields = Take(context.Fields, _options.MaxContextFields, 60);
        if (fields.Count > 0)
        {
            builder.AppendLine("فیلدهای این صفحه: " + string.Join(" | ", fields));
        }

        var buttons = Take(context.Buttons, _options.MaxContextButtons, 40);
        if (buttons.Count > 0)
        {
            builder.AppendLine("دکمه‌های این صفحه: " + string.Join(" | ", buttons));
        }

        if (!string.IsNullOrWhiteSpace(context.FocusedField))
        {
            builder.AppendLine($"فیلد انتخاب‌شده کاربر: {Truncate(context.FocusedField, 60)}");
        }

        if (!string.IsNullOrWhiteSpace(context.ErrorMessage))
        {
            builder.AppendLine($"پیام خطای فعلی: {Truncate(context.ErrorMessage, _options.MaxErrorLength)}");
        }

        var role = user.FindFirst(ClaimTypes.Role)?.Value;
        if (!string.IsNullOrWhiteSpace(role))
        {
            builder.AppendLine($"نقش کاربر: {Truncate(role, 40)}");
        }

        builder.AppendLine($"تاریخ امروز: {DateTime.Now:yyyy-MM-dd}");

        builder.AppendLine();
        builder.AppendLine("### سؤال کاربر");
        builder.Append(question);
        return builder.ToString();
    }

    private static List<string> Take(IEnumerable<string>? values, int maxCount, int maxLength)
    {
        if (values is null)
        {
            return new List<string>();
        }

        return values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => Truncate(value.Trim(), maxLength)!)
            .Distinct(StringComparer.Ordinal)
            .Take(Math.Max(maxCount, 0))
            .ToList();
    }

    private static string? Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || maxLength <= 0)
        {
            return value;
        }

        return value.Length <= maxLength ? value : value[..maxLength];
    }
}
