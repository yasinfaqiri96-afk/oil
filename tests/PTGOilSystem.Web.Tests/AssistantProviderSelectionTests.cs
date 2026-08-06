using System.Security.Claims;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PTGOilSystem.Web.Configuration;
using PTGOilSystem.Web.Services.Assistant;
using PTGOilSystem.Web.Services.Assistant.Tools;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// «راهنمای هوشمند»: انتخاب Provider از پیکربندی و پیام‌های کاربرپسند خطا.
/// هیچ تماس واقعی با سرویس بیرونی انجام نمی‌شود؛ Provider جعلی جای آن را می‌گیرد.
/// </summary>
public class AssistantProviderSelectionTests
{
    private sealed class FakeProvider : IAssistantProvider
    {
        private readonly AssistantFailure _failure;
        private readonly string _text;

        public FakeProvider(string name, bool configured, AssistantFailure failure = AssistantFailure.None, string text = "پاسخ نمونه")
        {
            Name = name;
            IsConfigured = configured;
            _failure = failure;
            _text = text;
        }

        public string Name { get; }
        public bool IsConfigured { get; }
        public bool SupportsTools => true;
        public bool WasCalled { get; private set; }

        /// <summary>آخرین گفتگویی که به Provider رسید. برای بازرسی Prompt و تاریخچه.</summary>
        public IReadOnlyList<AssistantChatMessage> LastMessages { get; private set; } = Array.Empty<AssistantChatMessage>();

        /// <summary>آخرین فهرست ابزاری که به Provider پیشنهاد شد.</summary>
        public IReadOnlyList<AssistantToolDefinition> LastTools { get; private set; } = Array.Empty<AssistantToolDefinition>();

        public Task<AssistantProviderResult> CompleteAsync(
            IReadOnlyList<AssistantChatMessage> messages,
            IReadOnlyList<AssistantToolDefinition> tools,
            int maxOutputTokens,
            string? modelOverride,
            CancellationToken cancellationToken)
        {
            WasCalled = true;
            LastMessages = messages;
            LastTools = tools;
            return Task.FromResult(_failure == AssistantFailure.None
                ? AssistantProviderResult.Success(_text)
                : AssistantProviderResult.Failed(_failure));
        }
    }

    /// <summary>Registry جعلی بدون ابزار. تست‌های این کلاس دربارهٔ انتخاب Provider است، نه ابزار.</summary>
    private sealed class EmptyToolRegistry : IAssistantToolRegistry
    {
        public IReadOnlyList<AssistantToolDefinition> GetAvailableTools(ClaimsPrincipal user)
            => Array.Empty<AssistantToolDefinition>();

        public Task<string> ExecuteAsync(AssistantToolCall call, ClaimsPrincipal user, CancellationToken cancellationToken)
            => Task.FromResult(string.Empty);
    }

    private static AssistantService BuildService(AssistantOptions options, params IAssistantProvider[] providers)
    {
        var catalog = new AssistantPageCatalog(
            new FakeWebHostEnvironment(),
            NullLogger<AssistantPageCatalog>.Instance);

        return new AssistantService(
            Options.Create(options),
            catalog,
            new EmptyToolRegistry(),
            providers,
            NullLogger<AssistantService>.Instance);
    }

    private static ClaimsPrincipal AdminUser()
        => new(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.Name, "tester"), new Claim(ClaimTypes.Role, "Admin") },
            "TestAuth"));

    private sealed class FakeWebHostEnvironment : Microsoft.AspNetCore.Hosting.IWebHostEnvironment
    {
        public string WebRootPath { get; set; } = string.Empty;
        public Microsoft.Extensions.FileProviders.IFileProvider WebRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
        public string ApplicationName { get; set; } = "Tests";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public string EnvironmentName { get; set; } = "Test";
    }

    private static AssistantAskRequest SampleRequest() => new()
    {
        Question = "این صفحه چه کاری انجام می‌دهد؟",
        Context = new AssistantPageContext { Controller = "contracts", Action = "create" },
    };

    [Fact]
    public async Task The_Configured_Provider_Is_Used_And_Not_The_First_Registered_One()
    {
        var anthropic = new FakeProvider("Anthropic", configured: true);
        var groq = new FakeProvider("Groq", configured: true, text: "پاسخ گروک");
        var service = BuildService(new AssistantOptions { Enabled = true, Provider = "Groq" }, anthropic, groq);

        var answer = await service.AskAsync(SampleRequest(), AdminUser(), CancellationToken.None);

        Assert.True(answer.Ok);
        Assert.Equal("پاسخ گروک", answer.Message);
        Assert.True(groq.WasCalled);
        Assert.False(anthropic.WasCalled);
    }

    [Fact]
    public async Task A_Rate_Limited_Free_Plan_Returns_The_Simple_Dari_Message()
    {
        var groq = new FakeProvider("Groq", configured: true, failure: AssistantFailure.RateLimited);
        var service = BuildService(new AssistantOptions { Enabled = true, Provider = "Groq" }, groq);

        var answer = await service.AskAsync(SampleRequest(), AdminUser(), CancellationToken.None);

        Assert.False(answer.Ok);
        Assert.Equal("ظرفیت رایگان دستیار فعلاً تکمیل شده است. لطفاً بعداً دوباره تلاش کنید.", answer.Message);
    }

    [Fact]
    public async Task A_Missing_Groq_Key_Names_The_Groq_Environment_Variable()
    {
        var groq = new FakeProvider("Groq", configured: false);
        var service = BuildService(new AssistantOptions { Enabled = true, Provider = "Groq" }, groq);

        var answer = await service.AskAsync(SampleRequest(), AdminUser(), CancellationToken.None);

        Assert.False(answer.Ok);
        Assert.Contains("GROQ_API_KEY", answer.Message);
    }

    [Fact]
    public async Task A_Missing_Anthropic_Key_Names_The_Anthropic_Environment_Variable()
    {
        var anthropic = new FakeProvider("Anthropic", configured: false);
        var service = BuildService(new AssistantOptions { Enabled = true, Provider = "Anthropic" }, anthropic);

        var answer = await service.AskAsync(SampleRequest(), AdminUser(), CancellationToken.None);

        Assert.False(answer.Ok);
        Assert.Contains("ANTHROPIC_API_KEY", answer.Message);
    }

    [Fact]
    public void The_Groq_Defaults_Match_The_Free_Plan_Endpoint_And_Model()
    {
        var options = new AssistantOptions();

        Assert.Equal("https://api.groq.com/openai/v1", options.Groq.BaseUrl);
        Assert.Equal("llama-3.3-70b-versatile", options.Groq.Model);
    }
}
