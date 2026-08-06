using System.Security.Claims;
using System.Text.Json;
using Google.GenAI.Types;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PTGOilSystem.Web.Configuration;
using PTGOilSystem.Web.Security;
using PTGOilSystem.Web.Services.Assistant;
using PTGOilSystem.Web.Services.Assistant.Tools;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// قرارداد قفل‌شده: نوبت مدل در Gemini باید دست‌نخورده به درخواست بعدی برگردد.
///
/// Gemini در هر functionCall یک ThoughtSignature مبهم می‌گذارد و در درخواست بعدیِ
/// همان گفتگو همان امضا را می‌خواهد. اگر نوبت مدل از روی نام و آرگومان بازسازی شود،
/// امضا می‌افتد و سرویس با «Function call is missing a thought_signature in
/// functionCall parts» رد می‌کند — ابزار اجرا شده ولی پاسخ نهایی هرگز نمی‌آید.
///
/// این تست‌ها هیچ تماس واقعی با Gemini نمی‌گیرند: پاسخ ساختگی ساخته می‌شود و
/// درخواست بعدی بازرسی می‌شود.
/// </summary>
public class AssistantGeminiThoughtSignatureTests
{
    private static readonly byte[] FirstSignature = { 1, 2, 3, 4, 5 };
    private static readonly byte[] SecondSignature = { 9, 8, 7 };
    private static readonly byte[] ThirdSignature = { 42, 42 };

    private static GeminiAssistantProvider Provider()
        => new(Options.Create(new AssistantOptions()), NullLogger<GeminiAssistantProvider>.Instance);

    private static Part CallPart(string name, byte[] signature, string? id = null)
        => new()
        {
            FunctionCall = new FunctionCall
            {
                Id = id,
                Name = name,
                Args = new Dictionary<string, object>(),
            },
            ThoughtSignature = signature,
        };

    private static Content ModelTurn(params Part[] parts)
        => new() { Role = "model", Parts = parts.ToList() };

    /// <summary>قطعه‌های functionCall یک نوبت مدل در درخواست ساخته‌شده.</summary>
    private static List<Part> CallPartsOf(Content content)
        => content.Parts!.Where(part => part.FunctionCall is not null).ToList();

    private static List<Content> BuildRequest(params AssistantChatMessage[] messages)
        => Provider().BuildContents(messages);

    [Fact]
    public void The_Model_Turn_Goes_Back_Unchanged_With_Its_Thought_Signature()
    {
        var modelTurn = ModelTurn(CallPart("get_stock_balance", FirstSignature));

        var contents = BuildRequest(
            AssistantChatMessage.User("موجودی فعلی انبار چقدر است؟"),
            new AssistantChatMessage(
                AssistantChatRole.Assistant,
                string.Empty,
                ToolCalls: new[] { new AssistantToolCall("ptg-local-1", "get_stock_balance", "{}") },
                ProviderContent: modelTurn),
            AssistantChatMessage.ToolResult("ptg-local-1", "get_stock_balance", "MT 120"));

        // نوبت مدل باید همان شیء اصلی باشد، نه یک بازسازی.
        var replayed = contents[1];
        Assert.Same(modelTurn, replayed);
        Assert.Same(modelTurn.Parts![0], replayed.Parts![0]);

        // و امضا بدون هیچ تغییری سر جایش باشد.
        var signature = replayed.Parts![0].ThoughtSignature;
        Assert.NotNull(signature);
        Assert.Equal(FirstSignature, signature);
    }

    [Fact]
    public void A_Missing_Signature_In_The_Next_Request_Fails_This_Test()
    {
        // همان سناریوی خطای واقعی Production: اگر روزی دوباره نوبت مدل بازسازی شود،
        // این تست باید بشکند، نه اینکه اشکال تا سرویس زنده برود.
        var modelTurn = ModelTurn(CallPart("get_contracts", SecondSignature));

        var contents = BuildRequest(
            AssistantChatMessage.User("قراردادها را بررسی کن."),
            new AssistantChatMessage(
                AssistantChatRole.Assistant,
                string.Empty,
                ToolCalls: new[] { new AssistantToolCall("ptg-local-1", "get_contracts", "{}") },
                ProviderContent: modelTurn),
            AssistantChatMessage.ToolResult("ptg-local-1", "get_contracts", "۱۵ قرارداد"));

        foreach (var part in CallPartsOf(contents[1]))
        {
            Assert.True(
                part.ThoughtSignature is { Length: > 0 },
                "ThoughtSignature از functionCall حذف شده است؛ درخواست بعدی Gemini رد می‌شود.");
        }
    }

    [Fact]
    public void Parallel_Function_Calls_Keep_Every_Signature_And_Answer_In_One_Turn()
    {
        var modelTurn = ModelTurn(
            CallPart("get_stock_balance", FirstSignature),
            CallPart("get_contracts", SecondSignature));

        var contents = BuildRequest(
            AssistantChatMessage.User("موجودی و قراردادها را بگو."),
            new AssistantChatMessage(
                AssistantChatRole.Assistant,
                string.Empty,
                ToolCalls: new[]
                {
                    new AssistantToolCall("ptg-local-1", "get_stock_balance", "{}"),
                    new AssistantToolCall("ptg-local-2", "get_contracts", "{}"),
                },
                ProviderContent: modelTurn),
            AssistantChatMessage.ToolResult("ptg-local-1", "get_stock_balance", "MT 120"),
            AssistantChatMessage.ToolResult("ptg-local-2", "get_contracts", "۱۵ قرارداد"));

        var calls = CallPartsOf(contents[1]);
        Assert.Equal(2, calls.Count);
        Assert.Equal(FirstSignature, calls[0].ThoughtSignature);
        Assert.Equal(SecondSignature, calls[1].ThoughtSignature);

        // هر دو نتیجه در یک نوبت user برمی‌گردند: Gemini برای فراخوانی‌های موازی
        // همهٔ functionResponseها را در همان نوبت می‌خواهد.
        Assert.Equal(3, contents.Count);
        var responses = contents[2].Parts!;
        Assert.Equal(2, responses.Count);
        Assert.Equal("get_stock_balance", responses[0].FunctionResponse!.Name);
        Assert.Equal("get_contracts", responses[1].FunctionResponse!.Name);
    }

    [Fact]
    public void Text_And_Function_Call_In_One_Turn_Both_Survive_In_Order()
    {
        var textPart = new Part { Text = "لحظه‌ای صبر کنید." };
        var callPart = CallPart("get_stock_balance", FirstSignature);
        var modelTurn = ModelTurn(textPart, callPart);

        var contents = BuildRequest(
            AssistantChatMessage.User("موجودی؟"),
            new AssistantChatMessage(
                AssistantChatRole.Assistant,
                "لحظه‌ای صبر کنید.",
                ToolCalls: new[] { new AssistantToolCall("ptg-local-1", "get_stock_balance", "{}") },
                ProviderContent: modelTurn),
            AssistantChatMessage.ToolResult("ptg-local-1", "get_stock_balance", "MT 120"));

        var parts = contents[1].Parts!;
        Assert.Equal(2, parts.Count);
        Assert.Same(textPart, parts[0]);
        Assert.Same(callPart, parts[1]);
        Assert.Equal(FirstSignature, parts[1].ThoughtSignature);
    }

    [Fact]
    public void Three_Rounds_Of_Tool_Calling_Keep_Every_Signature()
    {
        var first = ModelTurn(CallPart("search_party", FirstSignature));
        var second = ModelTurn(CallPart("get_party_balance", SecondSignature));
        var third = ModelTurn(CallPart("get_contracts", ThirdSignature));

        var contents = BuildRequest(
            AssistantChatMessage.User("مانده و قرارداد این تأمین‌کننده؟"),
            new AssistantChatMessage(AssistantChatRole.Assistant, string.Empty,
                ToolCalls: new[] { new AssistantToolCall("ptg-local-1", "search_party", "{}") },
                ProviderContent: first),
            AssistantChatMessage.ToolResult("ptg-local-1", "search_party", "id=4"),
            new AssistantChatMessage(AssistantChatRole.Assistant, string.Empty,
                ToolCalls: new[] { new AssistantToolCall("ptg-local-2", "get_party_balance", "{}") },
                ProviderContent: second),
            AssistantChatMessage.ToolResult("ptg-local-2", "get_party_balance", "USD 1200"),
            new AssistantChatMessage(AssistantChatRole.Assistant, string.Empty,
                ToolCalls: new[] { new AssistantToolCall("ptg-local-3", "get_contracts", "{}") },
                ProviderContent: third),
            AssistantChatMessage.ToolResult("ptg-local-3", "get_contracts", "۲ قرارداد"));

        Assert.Equal(FirstSignature, CallPartsOf(contents[1])[0].ThoughtSignature);
        Assert.Equal(SecondSignature, CallPartsOf(contents[3])[0].ThoughtSignature);
        Assert.Equal(ThirdSignature, CallPartsOf(contents[5])[0].ThoughtSignature);
    }

    [Fact]
    public void An_Empty_Tool_Result_Still_Produces_A_Function_Response()
    {
        var contents = BuildRequest(
            AssistantChatMessage.User("موجودی؟"),
            new AssistantChatMessage(AssistantChatRole.Assistant, string.Empty,
                ToolCalls: new[] { new AssistantToolCall("ptg-local-1", "get_stock_balance", "{}") },
                ProviderContent: ModelTurn(CallPart("get_stock_balance", FirstSignature))),
            AssistantChatMessage.ToolResult("ptg-local-1", "get_stock_balance", string.Empty));

        var response = contents[2].Parts![0].FunctionResponse!;
        Assert.Equal("get_stock_balance", response.Name);
        Assert.Equal(string.Empty, response.Response!["result"]);
    }

    [Fact]
    public void A_Json_Tool_Result_Is_Passed_Through_Unchanged()
    {
        const string json = "{\"items\":[{\"product\":\"AGO\",\"mt\":120.5}]}";

        var contents = BuildRequest(
            AssistantChatMessage.User("موجودی؟"),
            new AssistantChatMessage(AssistantChatRole.Assistant, string.Empty,
                ToolCalls: new[] { new AssistantToolCall("ptg-local-1", "get_stock_balance", "{}") },
                ProviderContent: ModelTurn(CallPart("get_stock_balance", FirstSignature))),
            AssistantChatMessage.ToolResult("ptg-local-1", "get_stock_balance", json));

        Assert.Equal(json, contents[2].Parts![0].FunctionResponse!.Response!["result"]);
    }

    [Fact]
    public void A_Synthetic_Call_Id_Is_Never_Sent_Back_To_Gemini()
    {
        // مدل‌های Gemini معمولاً functionCall را بدون id می‌فرستند. شناسهٔ داخلی فقط
        // برای جفت‌کردن نتیجه در خود برنامه است و در functionResponse نباید بیاید.
        var contents = BuildRequest(
            AssistantChatMessage.User("موجودی؟"),
            new AssistantChatMessage(AssistantChatRole.Assistant, string.Empty,
                ToolCalls: new[] { new AssistantToolCall("ptg-local-abc", "get_stock_balance", "{}") },
                ProviderContent: ModelTurn(CallPart("get_stock_balance", FirstSignature))),
            AssistantChatMessage.ToolResult("ptg-local-abc", "get_stock_balance", "MT 120"));

        Assert.Null(contents[2].Parts![0].FunctionResponse!.Id);
    }

    [Fact]
    public void A_Real_Call_Id_From_Gemini_Is_Echoed_Back()
    {
        var contents = BuildRequest(
            AssistantChatMessage.User("موجودی؟"),
            new AssistantChatMessage(AssistantChatRole.Assistant, string.Empty,
                ToolCalls: new[] { new AssistantToolCall("call-77", "get_stock_balance", "{}") },
                ProviderContent: ModelTurn(CallPart("get_stock_balance", FirstSignature, id: "call-77"))),
            AssistantChatMessage.ToolResult("call-77", "get_stock_balance", "MT 120"));

        Assert.Equal("call-77", contents[2].Parts![0].FunctionResponse!.Id);
    }

    [Fact]
    public void A_Turn_Without_Provider_Content_Still_Builds_A_Valid_Request()
    {
        // تاریخچهٔ ارسالی Frontend یا گفتگویی که با Provider دیگری شروع شده اصلاً
        // ThoughtSignature ندارد؛ آن مسیر باید بدون خطا کار کند.
        var contents = BuildRequest(
            AssistantChatMessage.User("موجودی؟"),
            new AssistantChatMessage(AssistantChatRole.Assistant, "بررسی می‌کنم.",
                ToolCalls: new[] { new AssistantToolCall("ptg-local-1", "get_stock_balance", "{\"productId\":3}") }),
            AssistantChatMessage.ToolResult("ptg-local-1", "get_stock_balance", "MT 120"));

        var parts = contents[1].Parts!;
        Assert.Equal("model", contents[1].Role);
        Assert.Equal(2, parts.Count);
        Assert.Equal("get_stock_balance", parts[1].FunctionCall!.Name);
        Assert.Null(parts[1].ThoughtSignature);
    }

    [Fact]
    public void Reading_A_Response_Keeps_The_Model_Content_For_The_Next_Request()
    {
        var modelTurn = ModelTurn(CallPart("get_stock_balance", FirstSignature));
        var response = new GenerateContentResponse
        {
            Candidates = new List<Candidate> { new() { Content = modelTurn } },
        };

        var result = Provider().ReadResponse(response);

        Assert.True(result.HasToolCalls);
        Assert.Equal("get_stock_balance", result.ToolCalls![0].ToolName);
        Assert.Same(modelTurn, result.ProviderContent);
    }

    // ---------------------------------------------------------------------
    // سطح سرویس: ProviderContent باید از یک دور به دور بعد منتقل شود.
    // ---------------------------------------------------------------------

    private sealed class RecordingProvider : IAssistantProvider
    {
        private readonly Queue<AssistantProviderResult> _script;

        public RecordingProvider(params AssistantProviderResult[] script)
            => _script = new Queue<AssistantProviderResult>(script);

        public string Name => "Gemini";
        public bool IsConfigured => true;
        public bool SupportsTools => true;

        /// <summary>گفتگویی که در آخرین درخواست به Provider رسید.</summary>
        public IReadOnlyList<AssistantChatMessage> LastMessages { get; private set; } = Array.Empty<AssistantChatMessage>();

        public Task<AssistantProviderResult> CompleteAsync(
            IReadOnlyList<AssistantChatMessage> messages,
            IReadOnlyList<AssistantToolDefinition> tools,
            int maxOutputTokens,
            string? modelOverride,
            CancellationToken cancellationToken)
        {
            LastMessages = messages.ToList();
            return Task.FromResult(_script.Count > 0
                ? _script.Dequeue()
                : AssistantProviderResult.Success("پاسخ نهایی"));
        }
    }

    private sealed class CancellingProvider : IAssistantProvider
    {
        public string Name => "Gemini";
        public bool IsConfigured => true;
        public bool SupportsTools => true;

        public Task<AssistantProviderResult> CompleteAsync(
            IReadOnlyList<AssistantChatMessage> messages,
            IReadOnlyList<AssistantToolDefinition> tools,
            int maxOutputTokens,
            string? modelOverride,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new OperationCanceledException(cancellationToken);
        }
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

    private sealed class OneTool : IAssistantToolRegistry
    {
        public IReadOnlyList<AssistantToolDefinition> GetAvailableTools(ClaimsPrincipal user)
            => new[] { new AssistantToolDefinition("get_stock_balance", "موجودی انبار", "{\"type\":\"object\"}") };

        public Task<string> ExecuteAsync(AssistantToolCall call, ClaimsPrincipal user, CancellationToken cancellationToken)
            => Task.FromResult("MT 120");
    }

    private static AssistantService Service(IAssistantProvider provider)
        => new(
            Options.Create(new AssistantOptions { Enabled = true, Provider = "Gemini", EnableTools = true }),
            new AssistantPageCatalog(new NoFilesEnvironment(), NullLogger<AssistantPageCatalog>.Instance),
            new OneTool(),
            new[] { provider },
            NullLogger<AssistantService>.Instance);

    private static ClaimsPrincipal Admin()
        => new(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.Name, "tester"), new Claim(ClaimTypes.Role, AuthRoles.Admin) },
            "TestAuth"));

    private static AssistantAskRequest Ask() => new()
    {
        Question = "موجودی فعلی انبار چقدر است؟",
        Context = new AssistantPageContext { Controller = "inventory", Action = "index" },
    };

    [Fact]
    public async Task The_Service_Carries_The_Model_Turn_Into_The_Next_Request()
    {
        var modelTurn = ModelTurn(CallPart("get_stock_balance", FirstSignature));
        var provider = new RecordingProvider(
            AssistantProviderResult.RequestTools(
                null,
                new[] { new AssistantToolCall("ptg-local-1", "get_stock_balance", "{}") },
                modelTurn),
            AssistantProviderResult.Success("موجودی ۱۲۰ تُن است."));

        var answer = await Service(provider).AskAsync(Ask(), Admin(), CancellationToken.None);

        Assert.True(answer.Ok);

        var replayed = provider.LastMessages.Single(message => message.Role == AssistantChatRole.Assistant);
        Assert.Same(modelTurn, replayed.ProviderContent);

        // و همان گفتگو، وقتی به درخواست Gemini ترجمه شود، امضا را دارد.
        var contents = Provider().BuildContents(provider.LastMessages);
        var modelContent = contents.Single(content => content.Role == "model");
        Assert.Equal(FirstSignature, CallPartsOf(modelContent)[0].ThoughtSignature);
    }

    [Fact]
    public async Task A_Cancelled_Request_Does_Not_Produce_An_Answer()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        var answer = await Service(new CancellingProvider()).AskAsync(Ask(), Admin(), cancelled.Token);

        Assert.False(answer.Ok);
    }

    [Fact]
    public void Tool_Arguments_Survive_The_Round_Trip_As_Json()
    {
        // ابزارها با آرگومان صدا زده می‌شوند؛ مسیر جایگزین باید همان JSON را بازگرداند.
        var contents = BuildRequest(
            AssistantChatMessage.User("موجودی محصول ۳؟"),
            new AssistantChatMessage(AssistantChatRole.Assistant, string.Empty,
                ToolCalls: new[] { new AssistantToolCall("ptg-local-1", "get_stock_balance", "{\"productId\":3}") }));

        var args = contents[1].Parts![0].FunctionCall!.Args!;
        Assert.Equal(3, ((JsonElement)args["productId"]).GetInt32());
    }
}
