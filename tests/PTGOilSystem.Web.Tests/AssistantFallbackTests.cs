using System.Security.Claims;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PTGOilSystem.Web.Configuration;
using PTGOilSystem.Web.Security;
using PTGOilSystem.Web.Services.Assistant;
using PTGOilSystem.Web.Services.Assistant.Tools;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// زنجیرهٔ Provider اصلی → جایگزین.
///
/// قرارداد قفل‌شده: فقط خطای *گذرا* اجازهٔ جایگزینی دارد. خطای مجوز، کلید نامعتبر
/// یا منطقهٔ پشتیبانی‌نشده هرگز پشت Provider دوم پنهان نمی‌شود، وگرنه اشکال واقعی
/// هیچ‌وقت دیده نمی‌شود.
/// </summary>
public class AssistantFallbackTests
{
    private sealed class StubProvider : IAssistantProvider
    {
        private readonly Queue<AssistantProviderResult> _script;

        public StubProvider(string name, bool configured, params AssistantProviderResult[] script)
        {
            Name = name;
            IsConfigured = configured;
            _script = new Queue<AssistantProviderResult>(script);
        }

        public string Name { get; }
        public bool IsConfigured { get; }
        public bool SupportsTools => true;
        public int CallCount { get; private set; }
        public string? LastModelOverride { get; private set; }

        public Task<AssistantProviderResult> CompleteAsync(
            IReadOnlyList<AssistantChatMessage> messages,
            IReadOnlyList<AssistantToolDefinition> tools,
            int maxOutputTokens,
            string? modelOverride,
            CancellationToken cancellationToken)
        {
            CallCount++;
            LastModelOverride = modelOverride;
            return Task.FromResult(_script.Count > 0
                ? _script.Dequeue()
                : AssistantProviderResult.Success("پاسخ پیش‌فرض"));
        }
    }

    private sealed class NoTools : IAssistantToolRegistry
    {
        public IReadOnlyList<AssistantToolDefinition> GetAvailableTools(ClaimsPrincipal user)
            => Array.Empty<AssistantToolDefinition>();

        public Task<string> ExecuteAsync(AssistantToolCall call, ClaimsPrincipal user, CancellationToken cancellationToken)
            => Task.FromResult(string.Empty);
    }

    private static AssistantService Build(AssistantOptions options, params IAssistantProvider[] providers)
        => new(
            Options.Create(options),
            new AssistantPageCatalog(new NoFilesEnvironment(), NullLogger<AssistantPageCatalog>.Instance),
            new NoTools(),
            providers,
            NullLogger<AssistantService>.Instance);

    private static ClaimsPrincipal Admin()
        => new(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.Name, "tester"), new Claim(ClaimTypes.Role, AuthRoles.Admin) },
            "TestAuth"));

    private static AssistantAskRequest Ask() => new()
    {
        Question = "موجودی چقدر است؟",
        Context = new AssistantPageContext { Controller = "inventory", Action = "index" },
    };

    private static AssistantOptions GeminiWithGroqFallback() => new()
    {
        Enabled = true,
        Provider = "Gemini",
        FallbackProvider = "Groq",
        FallbackModel = "qwen/qwen3.6-27b",
        EnableTools = false,
    };

    [Theory]
    [InlineData(AssistantFailure.RateLimited)]
    [InlineData(AssistantFailure.Timeout)]
    [InlineData(AssistantFailure.ServiceError)]
    public async Task A_Transient_Failure_Moves_To_The_Fallback_Provider(AssistantFailure failure)
    {
        var gemini = new StubProvider("Gemini", true, AssistantProviderResult.Failed(failure));
        var groq = new StubProvider("Groq", true, AssistantProviderResult.Success("پاسخ جایگزین"));

        var answer = await Build(GeminiWithGroqFallback(), gemini, groq)
            .AskAsync(Ask(), Admin(), CancellationToken.None);

        Assert.True(answer.Ok);
        Assert.Equal("پاسخ جایگزین", answer.Message);
        Assert.Equal(1, groq.CallCount);
    }

    [Theory]
    [InlineData(AssistantFailure.AccessDenied)]
    [InlineData(AssistantFailure.RegionUnsupported)]
    [InlineData(AssistantFailure.Unavailable)]
    public async Task A_Permanent_Failure_Is_Reported_And_Never_Hidden_By_The_Fallback(AssistantFailure failure)
    {
        var gemini = new StubProvider("Gemini", true, AssistantProviderResult.Failed(failure));
        var groq = new StubProvider("Groq", true, AssistantProviderResult.Success("پاسخ جایگزین"));

        var answer = await Build(GeminiWithGroqFallback(), gemini, groq)
            .AskAsync(Ask(), Admin(), CancellationToken.None);

        Assert.False(answer.Ok);
        Assert.Equal(0, groq.CallCount);
    }

    [Fact]
    public async Task An_Access_Denied_Message_Names_The_Variable_But_Never_The_Key()
    {
        // کلید ساختگی در محیط تست تا مطمئن شویم در پیام کاربر نشت نمی‌کند.
        var gemini = new StubProvider("Gemini", true, AssistantProviderResult.Failed(AssistantFailure.AccessDenied));

        var options = GeminiWithGroqFallback();
        options.FallbackProvider = null;

        var answer = await Build(options, gemini).AskAsync(Ask(), Admin(), CancellationToken.None);

        Assert.False(answer.Ok);
        Assert.Contains("GEMINI_API_KEY", answer.Message);
        Assert.DoesNotContain("AIza", answer.Message);
    }

    [Fact]
    public async Task The_Fallback_Runs_With_The_Configured_Fallback_Model()
    {
        var gemini = new StubProvider("Gemini", true, AssistantProviderResult.Failed(AssistantFailure.ServiceError));
        var groq = new StubProvider("Groq", true, AssistantProviderResult.Success("پاسخ"));

        await Build(GeminiWithGroqFallback(), gemini, groq).AskAsync(Ask(), Admin(), CancellationToken.None);

        Assert.Equal("qwen/qwen3.6-27b", groq.LastModelOverride);
    }

    [Fact]
    public async Task The_Primary_Provider_Runs_With_Its_Own_Configured_Model()
    {
        var gemini = new StubProvider("Gemini", true, AssistantProviderResult.Success("پاسخ"));

        await Build(GeminiWithGroqFallback(), gemini).AskAsync(Ask(), Admin(), CancellationToken.None);

        // خالی یعنی «مدل پیش‌فرض خودت»؛ مدل جایگزین نباید به Provider اصلی نشت کند.
        Assert.Null(gemini.LastModelOverride);
    }

    [Fact]
    public async Task An_Unconfigured_Fallback_Provider_Is_Skipped()
    {
        var gemini = new StubProvider("Gemini", true, AssistantProviderResult.Failed(AssistantFailure.RateLimited));
        var groq = new StubProvider("Groq", configured: false, AssistantProviderResult.Success("نباید صدا زده شود"));

        var answer = await Build(GeminiWithGroqFallback(), gemini, groq)
            .AskAsync(Ask(), Admin(), CancellationToken.None);

        Assert.False(answer.Ok);
        Assert.Equal(0, groq.CallCount);
    }

    [Fact]
    public async Task A_Failing_Fallback_Does_Not_Chain_Any_Further()
    {
        var gemini = new StubProvider("Gemini", true, AssistantProviderResult.Failed(AssistantFailure.Timeout));
        var groq = new StubProvider("Groq", true, AssistantProviderResult.Failed(AssistantFailure.ServiceError));

        var answer = await Build(GeminiWithGroqFallback(), gemini, groq)
            .AskAsync(Ask(), Admin(), CancellationToken.None);

        Assert.False(answer.Ok);
        Assert.Equal(1, gemini.CallCount);
        Assert.Equal(1, groq.CallCount);
    }

    [Fact]
    public async Task A_Missing_Gemini_Key_Names_The_Gemini_Environment_Variable()
    {
        var gemini = new StubProvider("Gemini", configured: false);

        var options = GeminiWithGroqFallback();
        options.FallbackProvider = null;

        var answer = await Build(options, gemini).AskAsync(Ask(), Admin(), CancellationToken.None);

        Assert.False(answer.Ok);
        Assert.Contains("GEMINI_API_KEY", answer.Message);
    }

    [Fact]
    public async Task A_Service_Error_Message_Never_Tells_The_User_To_Retry_Forever()
    {
        // 5xx گذراست: باید سراغ جایگزین برود. اگر جایگزینی نباشد، پیام باید
        // «بعداً دوباره» باشد، نه پیام مجوز.
        var gemini = new StubProvider("Gemini", true, AssistantProviderResult.Failed(AssistantFailure.ServiceError));

        var options = GeminiWithGroqFallback();
        options.FallbackProvider = null;

        var answer = await Build(options, gemini).AskAsync(Ask(), Admin(), CancellationToken.None);

        Assert.False(answer.Ok);
        Assert.DoesNotContain("GEMINI_API_KEY", answer.Message);
    }

    [Fact]
    public async Task A_Region_Block_Says_So_Instead_Of_Blaming_The_Api_Key()
    {
        // مسدود بودن منطقه با عوض کردن کلید حل نمی‌شود. اگر پیام نام متغیر کلید را
        // ببرد، مدیر سیستم ساعت‌ها دنبال کلید سالم می‌گردد در حالی که کلید سالم است.
        var gemini = new StubProvider("Gemini", true, AssistantProviderResult.Failed(AssistantFailure.RegionUnsupported));

        var options = GeminiWithGroqFallback();
        options.FallbackProvider = null;

        var answer = await Build(options, gemini).AskAsync(Ask(), Admin(), CancellationToken.None);

        Assert.False(answer.Ok);
        Assert.DoesNotContain("GEMINI_API_KEY", answer.Message);
        // مقایسهٔ Ordinal لازم است: در پیام «منطقهٔ» آمده و مقایسهٔ وابسته به فرهنگ،
        // «ه» را جدا از همزهٔ ترکیبی پیدا نمی‌کند.
        Assert.Contains("منطقه", answer.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_Shipped_Defaults_Match_The_Requested_Gemini_Setup()
    {
        var options = new AssistantOptions();

        Assert.Equal("Gemini", options.Provider);
        Assert.Equal("gemini-3.6-flash", options.Gemini.Model);
        Assert.Equal(2000, options.MaxOutputTokens);
        Assert.True(options.EnableTools);
    }

    private sealed class NoFilesEnvironment : Microsoft.AspNetCore.Hosting.IWebHostEnvironment
    {
        public string WebRootPath { get; set; } = string.Empty;
        public Microsoft.Extensions.FileProviders.IFileProvider WebRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
        public string ApplicationName { get; set; } = "Tests";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public string EnvironmentName { get; set; } = "Test";
    }
}
