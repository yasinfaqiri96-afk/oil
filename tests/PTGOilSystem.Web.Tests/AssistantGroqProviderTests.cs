using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PTGOilSystem.Web.Configuration;
using PTGOilSystem.Web.Services.Assistant;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// Provider گروک روی سیم: شکل درخواست سازگار با OpenAI، خواندن درخواست ابزار، و
/// تفکیک خطای گذرا از دائمی.
///
/// این تفکیک تعیین می‌کند دستیار کِی سراغ Provider جایگزین می‌رود؛ اشتباه بودنش
/// یعنی یا خطای واقعی پنهان می‌شود یا کاربر بی‌دلیل پیام قطع ارتباط می‌بیند.
/// هیچ تماس واقعی با گروک گرفته نمی‌شود.
/// </summary>
public class AssistantGroqProviderTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return _responder(request);
        }
    }

    private sealed class SingleClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;

        public SingleClientFactory(HttpMessageHandler handler) => _handler = handler;

        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static GroqAssistantProvider Provider(HttpMessageHandler handler, string model = "openai/gpt-oss-120b")
        => new(
            Options.Create(new AssistantOptions
            {
                Groq = new AssistantGroqOptions { BaseUrl = "https://api.groq.com/openai/v1", Model = model },
            }),
            new SingleClientFactory(handler),
            NullLogger<GroqAssistantProvider>.Instance);

    private static IReadOnlyList<AssistantToolDefinition> Tools()
        => new[]
        {
            new AssistantToolDefinition(
                "get_loading_details",
                "پروندهٔ بارگیری",
                "{\"type\":\"object\",\"properties\":{\"loading_id\":{\"type\":\"integer\"}}}"),
        };

    private static async Task<AssistantProviderResult> RunAsync(
        StubHandler handler,
        IReadOnlyList<AssistantChatMessage>? messages = null,
        IReadOnlyList<AssistantToolDefinition>? tools = null)
    {
        var key = Environment.GetEnvironmentVariable("GROQ_API_KEY");
        Environment.SetEnvironmentVariable("GROQ_API_KEY", "test-key");
        try
        {
            return await Provider(handler).CompleteAsync(
                messages ?? new[] { AssistantChatMessage.User("موجودی چقدر است؟") },
                tools ?? Array.Empty<AssistantToolDefinition>(),
                2000,
                modelOverride: null,
                CancellationToken.None);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GROQ_API_KEY", key);
        }
    }

    [Fact]
    public async Task A_Tool_Request_From_Groq_Is_Read_Back()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, """
            {
              "choices": [
                {
                  "message": {
                    "content": null,
                    "tool_calls": [
                      { "id": "call_1", "type": "function",
                        "function": { "name": "get_loading_details", "arguments": "{\"loading_id\":42}" } }
                    ]
                  }
                }
              ]
            }
            """));

        var result = await RunAsync(handler, tools: Tools());

        Assert.Equal(AssistantFailure.None, result.Failure);
        Assert.True(result.HasToolCalls);
        var call = result.ToolCalls!.Single();
        Assert.Equal("call_1", call.Id);
        Assert.Equal("get_loading_details", call.ToolName);
        Assert.Equal("{\"loading_id\":42}", call.ArgumentsJson);
    }

    [Fact]
    public async Task Tools_And_Tool_Results_Are_Sent_In_The_Openai_Shape()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK,
            """{"choices":[{"message":{"content":"۱۱۵۰ تن باقی مانده است."}}]}"""));

        var messages = new[]
        {
            AssistantChatMessage.System("راهنما"),
            AssistantChatMessage.User("همین بارگیری را بررسی کن."),
            new AssistantChatMessage(
                AssistantChatRole.Assistant,
                string.Empty,
                ToolCalls: new[] { new AssistantToolCall("call_1", "get_loading_details", "{\"loading_id\":42}") }),
            AssistantChatMessage.ToolResult("call_1", "get_loading_details", "باقی‌مانده=1150 MT"),
        };

        var result = await RunAsync(handler, messages, Tools());

        Assert.Equal(AssistantFailure.None, result.Failure);

        using var document = JsonDocument.Parse(handler.LastBody!);
        var root = document.RootElement;

        Assert.Equal("openai/gpt-oss-120b", root.GetProperty("model").GetString());
        Assert.Equal("auto", root.GetProperty("tool_choice").GetString());
        Assert.Equal("get_loading_details", root.GetProperty("tools")[0].GetProperty("function").GetProperty("name").GetString());

        var sent = root.GetProperty("messages");
        Assert.Equal("system", sent[0].GetProperty("role").GetString());
        Assert.Equal("assistant", sent[2].GetProperty("role").GetString());
        Assert.Equal("call_1", sent[2].GetProperty("tool_calls")[0].GetProperty("id").GetString());

        // نتیجهٔ ابزار با همان شناسه برمی‌گردد، وگرنه گروک آن را بی‌صاحب می‌بیند.
        Assert.Equal("tool", sent[3].GetProperty("role").GetString());
        Assert.Equal("call_1", sent[3].GetProperty("tool_call_id").GetString());
        Assert.Contains("1150", sent[3].GetProperty("content").GetString()!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("26.354s", 26.354)]
    [InlineData("1m30s", 90d)]
    [InlineData("500ms", 0.5)]
    [InlineData("4m19.2s", 259.2)]
    [InlineData("12", 12d)]
    [InlineData("", null)]
    [InlineData("soon", null)]
    public void The_Groq_Reset_Header_Is_Understood(string raw, double? expectedSeconds)
    {
        var parsed = GroqAssistantProvider.ParseGroqDuration(raw);

        if (expectedSeconds is null)
        {
            Assert.Null(parsed);
            return;
        }

        Assert.NotNull(parsed);
        Assert.Equal(expectedSeconds.Value, parsed!.Value.TotalSeconds, 3);
    }

    [Fact]
    public async Task A_Short_Rate_Limit_Window_Is_Waited_Out_Once()
    {
        // سقف گروک بر حسب توکن در دقیقه است؛ وقتی خودش می‌گوید پنجره تا چند ثانیهٔ
        // دیگر باز می‌شود، یک بار صبر و تلاش دوباره ارزش دارد.
        var attempts = 0;
        var handler = new StubHandler(_ =>
        {
            attempts++;
            if (attempts == 1)
            {
                var limited = Json(HttpStatusCode.TooManyRequests, """{"error":{"code":"rate_limit_exceeded"}}""");
                limited.Headers.TryAddWithoutValidation("x-ratelimit-reset-tokens", "0.2s");
                return limited;
            }

            return Json(HttpStatusCode.OK, """{"choices":[{"message":{"content":"پاسخ بعد از صبر"}}]}""");
        });

        var result = await RunAsync(handler);

        Assert.Equal(2, attempts);
        Assert.Equal(AssistantFailure.None, result.Failure);
        Assert.Equal("پاسخ بعد از صبر", result.Text);
    }

    [Fact]
    public async Task A_Long_Rate_Limit_Window_Is_Reported_Instead_Of_Waited()
    {
        // پنجرهٔ بلند یعنی سهمیه واقعاً تمام است؛ کاربر نباید پشت آن بماند.
        var attempts = 0;
        var handler = new StubHandler(_ =>
        {
            attempts++;
            var limited = Json(HttpStatusCode.TooManyRequests, """{"error":{"code":"rate_limit_exceeded"}}""");
            limited.Headers.TryAddWithoutValidation("x-ratelimit-reset-tokens", "7m30s");
            return limited;
        });

        var result = await RunAsync(handler);

        Assert.Equal(1, attempts);
        Assert.Equal(AssistantFailure.RateLimited, result.Failure);
    }

    [Fact]
    public async Task A_Second_Rate_Limit_Is_Not_Waited_Out_Again()
    {
        var attempts = 0;
        var handler = new StubHandler(_ =>
        {
            attempts++;
            var limited = Json(HttpStatusCode.TooManyRequests, """{"error":{"code":"rate_limit_exceeded"}}""");
            limited.Headers.TryAddWithoutValidation("x-ratelimit-reset-tokens", "0.2s");
            return limited;
        });

        var result = await RunAsync(handler);

        Assert.Equal(2, attempts);
        Assert.Equal(AssistantFailure.RateLimited, result.Failure);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, AssistantFailure.RateLimited)]
    [InlineData(HttpStatusCode.InternalServerError, AssistantFailure.ServiceError)]
    [InlineData(HttpStatusCode.ServiceUnavailable, AssistantFailure.ServiceError)]
    [InlineData(HttpStatusCode.Unauthorized, AssistantFailure.AccessDenied)]
    [InlineData(HttpStatusCode.Forbidden, AssistantFailure.AccessDenied)]
    [InlineData(HttpStatusCode.BadRequest, AssistantFailure.InvalidRequest)]
    [InlineData(HttpStatusCode.NotFound, AssistantFailure.InvalidRequest)]
    public async Task Http_Status_Maps_To_The_Right_Failure(HttpStatusCode status, AssistantFailure expected)
    {
        var handler = new StubHandler(_ => Json(status, """{"error":{"message":"nope"}}"""));

        var result = await RunAsync(handler);

        Assert.Equal(expected, result.Failure);
    }

    [Fact]
    public async Task A_Network_Failure_Is_Transient()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("connection refused"));

        var result = await RunAsync(handler);

        Assert.Equal(AssistantFailure.NetworkError, result.Failure);
    }

    [Fact]
    public async Task A_Rejected_Tool_Call_Is_Reported_As_Such()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.BadRequest,
            """{"error":{"code":"tool_use_failed","message":"tool call validation failed"}}"""));

        var result = await RunAsync(handler, tools: Tools());

        Assert.Equal(AssistantFailure.ToolCallRejected, result.Failure);
    }

    [Fact]
    public async Task A_Missing_Key_Is_Reported_Without_Calling_The_Service()
    {
        var called = false;
        var handler = new StubHandler(_ =>
        {
            called = true;
            return Json(HttpStatusCode.OK, """{"choices":[{"message":{"content":"x"}}]}""");
        });

        var key = Environment.GetEnvironmentVariable("GROQ_API_KEY");
        Environment.SetEnvironmentVariable("GROQ_API_KEY", null);
        try
        {
            var result = await Provider(handler).CompleteAsync(
                new[] { AssistantChatMessage.User("سؤال") },
                Array.Empty<AssistantToolDefinition>(),
                2000,
                modelOverride: null,
                CancellationToken.None);

            Assert.Equal(AssistantFailure.NotConfigured, result.Failure);
            Assert.False(called);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GROQ_API_KEY", key);
        }
    }
}
