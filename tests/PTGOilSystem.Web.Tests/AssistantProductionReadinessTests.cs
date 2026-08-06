using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PTGOilSystem.Web.Configuration;
using PTGOilSystem.Web.Security;
using PTGOilSystem.Web.Services.Assistant;
using PTGOilSystem.Web.Services.Assistant.Tools;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// آمادگی Production دستیار: Gemini مدل اصلی، Groq جایگزین واقعی، حفظ کامل
/// گفتگو هنگام جایگزینی، شناختن رکورد صفحهٔ جاری و رعایت دسترسی.
///
/// هیچ تماس واقعی با سرویس بیرونی گرفته نمی‌شود.
/// </summary>
public class AssistantProductionReadinessTests
{
    // ---- ابزارهای کمکی تست -------------------------------------------------

    private sealed class ScriptedProvider : IAssistantProvider
    {
        private readonly Queue<AssistantProviderResult> _script;

        public ScriptedProvider(string name, params AssistantProviderResult[] script)
        {
            Name = name;
            _script = new Queue<AssistantProviderResult>(script);
        }

        public string Name { get; }
        public bool IsConfigured { get; set; } = true;
        public bool SupportsTools => true;
        public int CallCount { get; private set; }
        public string? LastModelOverride { get; private set; }
        public IReadOnlyList<AssistantChatMessage> LastMessages { get; private set; } = Array.Empty<AssistantChatMessage>();
        public IReadOnlyList<AssistantToolDefinition> LastTools { get; private set; } = Array.Empty<AssistantToolDefinition>();

        public Task<AssistantProviderResult> CompleteAsync(
            IReadOnlyList<AssistantChatMessage> messages,
            IReadOnlyList<AssistantToolDefinition> tools,
            int maxOutputTokens,
            string? modelOverride,
            CancellationToken cancellationToken)
        {
            CallCount++;
            LastModelOverride = modelOverride;
            LastMessages = messages.ToList();
            LastTools = tools.ToList();
            return Task.FromResult(_script.Count > 0 ? _script.Dequeue() : AssistantProviderResult.Success("پاسخ پیش‌فرض"));
        }
    }

    /// <summary>ابزار ساختگی با همان قرارداد واقعی: نام، دسترسی و شمارش اجرا.</summary>
    private sealed class FakeTool : IAssistantTool
    {
        private readonly string _output;

        public FakeTool(string name, string requiredController, string output = "نتیجهٔ ابزار")
        {
            Name = name;
            RequiredController = requiredController;
            _output = output;
        }

        public string Name { get; }
        public string Description => "ابزار آزمایشی";
        public string ParametersJsonSchema => "{\"type\":\"object\",\"properties\":{}}";
        public string RequiredController { get; }
        public int Runs { get; private set; }

        public Task<string> ExecuteAsync(JsonElement arguments, ClaimsPrincipal user, CancellationToken cancellationToken)
        {
            Runs++;
            return Task.FromResult(_output);
        }
    }

    /// <summary>Logger ای که همهٔ پیام‌ها را نگه می‌دارد تا نشت کلید بررسی شود.</summary>
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Lines { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Lines.Add(formatter(state, exception));
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

    private static AssistantOptions ProductionShape() => new()
    {
        Enabled = true,
        Provider = "Gemini",
        FallbackProvider = "Groq",
        FallbackModel = "openai/gpt-oss-120b",
        EnableTools = true,
        MaxToolIterations = 3,
    };

    private static AssistantService Build(
        AssistantOptions options,
        IAssistantToolRegistry tools,
        ILogger<AssistantService>? logger = null,
        params IAssistantProvider[] providers)
        => new(
            Options.Create(options),
            new AssistantPageCatalog(new NoFilesEnvironment(), NullLogger<AssistantPageCatalog>.Instance),
            tools,
            providers,
            logger ?? NullLogger<AssistantService>.Instance);

    private static AssistantToolRegistry Registry(params IAssistantTool[] tools)
        => new(tools, NullLogger<AssistantToolRegistry>.Instance);

    private static ClaimsPrincipal Admin()
        => new(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.Name, "tester"), new Claim(ClaimTypes.Role, AuthRoles.Admin) },
            "TestAuth"));

    /// <summary>کاربری با دسترسی فقط به عملیات — بدون بخش مالی و بدون قراردادها.</summary>
    private static ClaimsPrincipal OperationsOnlyUser()
        => new(new ClaimsIdentity(
            new[]
            {
                new Claim(ClaimTypes.Name, "operator"),
                new Claim(ClaimTypes.Role, AuthRoles.Operator),
                new Claim(AppClaimTypes.AllowedNavigation, RoleNavigationKeys.Dashboard),
                new Claim(AppClaimTypes.AllowedNavigation, RoleNavigationKeys.Operations),
            },
            "TestAuth"));

    private static AssistantAskRequest Ask(string question, string controller = "loading", string route = "/Loading/Details/42")
        => new()
        {
            Question = question,
            Context = new AssistantPageContext
            {
                Controller = controller,
                Action = "details",
                Route = route,
                PageTitle = "جزئیات بارگیری",
            },
        };

    private static AssistantChatMessage? UserMessage(IReadOnlyList<AssistantChatMessage> messages)
        => messages.FirstOrDefault(message => message.Role == AssistantChatRole.User);

    // ---- Gemini: پاسخ ساده و Tool Calling ----------------------------------

    [Fact]
    public async Task Gemini_Answers_A_Plain_Question()
    {
        var gemini = new ScriptedProvider("Gemini", AssistantProviderResult.Success("این صفحه برای ثبت بارگیری است."));

        var answer = await Build(ProductionShape(), Registry(), null, gemini)
            .AskAsync(Ask("این صفحه چه کار می‌کند؟"), Admin(), CancellationToken.None);

        Assert.True(answer.Ok);
        Assert.Equal("این صفحه برای ثبت بارگیری است.", answer.Message);
        Assert.Empty(answer.UsedTools);
    }

    [Fact]
    public async Task Gemini_Runs_A_Tool_And_Then_Answers()
    {
        var tool = new FakeTool("get_loading_details", "Loading", "بارگیری شناسه=42 | باقی‌مانده=1150 MT");
        var gemini = new ScriptedProvider(
            "Gemini",
            AssistantProviderResult.RequestTools(null, new[] { new AssistantToolCall("c1", "get_loading_details", "{\"loading_id\":42}") }),
            AssistantProviderResult.Success("۱۱۵۰ تن باقی مانده است."));

        var answer = await Build(ProductionShape(), Registry(tool), null, gemini)
            .AskAsync(Ask("همین بارگیری را بررسی کن."), Admin(), CancellationToken.None);

        Assert.True(answer.Ok);
        Assert.Equal(1, tool.Runs);
        Assert.Equal(new[] { "get_loading_details" }, answer.UsedTools);
    }

    // ---- جایگزینی: فقط خطای گذرا ------------------------------------------

    [Theory]
    [InlineData(AssistantFailure.RateLimited)]
    [InlineData(AssistantFailure.Timeout)]
    [InlineData(AssistantFailure.NetworkError)]
    [InlineData(AssistantFailure.ServiceError)]
    public async Task A_Transient_Gemini_Failure_Continues_On_Groq(AssistantFailure failure)
    {
        var gemini = new ScriptedProvider("Gemini", AssistantProviderResult.Failed(failure));
        var groq = new ScriptedProvider("Groq", AssistantProviderResult.Success("پاسخ گروک"));

        var answer = await Build(ProductionShape(), Registry(), null, gemini, groq)
            .AskAsync(Ask("موجودی چقدر است؟"), Admin(), CancellationToken.None);

        Assert.True(answer.Ok);
        Assert.Equal("پاسخ گروک", answer.Message);
        Assert.Equal("openai/gpt-oss-120b", groq.LastModelOverride);
    }

    [Theory]
    [InlineData(AssistantFailure.AccessDenied)]
    [InlineData(AssistantFailure.RegionUnsupported)]
    [InlineData(AssistantFailure.InvalidRequest)]
    [InlineData(AssistantFailure.NotConfigured)]
    public async Task A_Permanent_Gemini_Failure_Never_Falls_Back(AssistantFailure failure)
    {
        var gemini = new ScriptedProvider("Gemini", AssistantProviderResult.Failed(failure));
        var groq = new ScriptedProvider("Groq", AssistantProviderResult.Success("نباید صدا زده شود"));

        var answer = await Build(ProductionShape(), Registry(), null, gemini, groq)
            .AskAsync(Ask("موجودی چقدر است؟"), Admin(), CancellationToken.None);

        Assert.False(answer.Ok);
        Assert.Equal(0, groq.CallCount);
    }

    [Fact]
    public async Task A_Rejected_Tool_Call_Is_Retried_Without_Tools_And_Never_Falls_Back()
    {
        // ورودی ابزارِ ساختهٔ مدل با Schema نخوانده است. این نه سهمیه است نه شبکه،
        // پس Provider دوم همان اشتباه را تکرار می‌کند و نباید صدا زده شود.
        var gemini = new ScriptedProvider(
            "Gemini",
            AssistantProviderResult.Failed(AssistantFailure.ToolCallRejected),
            AssistantProviderResult.Success("بدون ابزار پاسخ می‌دهم."));
        var groq = new ScriptedProvider("Groq", AssistantProviderResult.Success("نباید صدا زده شود"));

        var answer = await Build(ProductionShape(), Registry(new FakeTool("get_loading_details", "Loading")), null, gemini, groq)
            .AskAsync(Ask("همین بارگیری را بررسی کن."), Admin(), CancellationToken.None);

        Assert.True(answer.Ok);
        Assert.Equal(0, groq.CallCount);
    }

    // ---- جایگزینی: هیچ چیزی از گفتگو گم نمی‌شود ----------------------------

    [Fact]
    public async Task The_Fallback_Keeps_The_Page_Context_And_The_Question()
    {
        var gemini = new ScriptedProvider("Gemini", AssistantProviderResult.Failed(AssistantFailure.RateLimited));
        var groq = new ScriptedProvider("Groq", AssistantProviderResult.Success("پاسخ گروک"));

        await Build(ProductionShape(), Registry(), null, gemini, groq)
            .AskAsync(Ask("همین بارگیری را بررسی کن."), Admin(), CancellationToken.None);

        var carried = UserMessage(groq.LastMessages);
        Assert.NotNull(carried);
        Assert.Contains("همین بارگیری را بررسی کن.", carried!.Content, StringComparison.Ordinal);
        Assert.Contains("جزئیات بارگیری", carried.Content, StringComparison.Ordinal);

        // شناسهٔ رکورد صفحه هم باید به Provider دوم برسد.
        Assert.Contains("42", carried.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_Fallback_Keeps_Tool_Results_And_Does_Not_Run_Tools_Again()
    {
        var tool = new FakeTool("get_loading_details", "Loading", "بارگیری شناسه=42 | باقی‌مانده=1150 MT");

        // Gemini ابزار را اجرا می‌کند و بعد سهمیه‌اش تمام می‌شود.
        var gemini = new ScriptedProvider(
            "Gemini",
            AssistantProviderResult.RequestTools(null, new[] { new AssistantToolCall("c1", "get_loading_details", "{\"loading_id\":42}") }),
            AssistantProviderResult.Failed(AssistantFailure.RateLimited));
        var groq = new ScriptedProvider("Groq", AssistantProviderResult.Success("۱۱۵۰ تن باقی مانده است."));

        var answer = await Build(ProductionShape(), Registry(tool), null, gemini, groq)
            .AskAsync(Ask("همین بارگیری را بررسی کن."), Admin(), CancellationToken.None);

        Assert.True(answer.Ok);

        // ابزار فقط یک بار اجرا شده و نتیجه‌اش به Groq رسیده است.
        Assert.Equal(1, tool.Runs);
        var toolResult = groq.LastMessages.SingleOrDefault(message => message.Role == AssistantChatRole.Tool);
        Assert.NotNull(toolResult);
        Assert.Contains("1150", toolResult!.Content, StringComparison.Ordinal);
        Assert.Equal("get_loading_details", toolResult.ToolName);

        // و در فهرست منبع پاسخ هم می‌ماند.
        Assert.Contains("get_loading_details", answer.UsedTools);
    }

    [Fact]
    public async Task The_Fallback_Drops_Provider_Specific_Content_Only()
    {
        // ProviderContent فقط برای Gemini معنی دارد؛ فرستادنش به Groq بی‌معنی است.
        // ولی درخواست ابزار و شناسهٔ آن باید بماند تا نتیجهٔ ابزار بی‌صاحب نشود.
        var nativeTurn = new object();
        var gemini = new ScriptedProvider(
            "Gemini",
            AssistantProviderResult.RequestTools(
                null,
                new[] { new AssistantToolCall("c1", "get_loading_details", "{}") },
                nativeTurn),
            AssistantProviderResult.Failed(AssistantFailure.ServiceError));
        var groq = new ScriptedProvider("Groq", AssistantProviderResult.Success("پاسخ گروک"));

        await Build(ProductionShape(), Registry(new FakeTool("get_loading_details", "Loading")), null, gemini, groq)
            .AskAsync(Ask("همین بارگیری را بررسی کن."), Admin(), CancellationToken.None);

        var assistantTurn = groq.LastMessages.Single(message => message.Role == AssistantChatRole.Assistant);
        Assert.Null(assistantTurn.ProviderContent);
        Assert.Equal("c1", assistantTurn.ToolCalls!.Single().Id);
        Assert.Equal(1, groq.LastMessages.Count(message => message.Role == AssistantChatRole.Tool));
    }

    [Fact]
    public async Task The_Fallback_Still_Offers_The_Same_Tools()
    {
        var gemini = new ScriptedProvider("Gemini", AssistantProviderResult.Failed(AssistantFailure.RateLimited));
        var groq = new ScriptedProvider("Groq", AssistantProviderResult.Success("پاسخ گروک"));

        await Build(ProductionShape(), Registry(new FakeTool("get_loading_details", "Loading")), null, gemini, groq)
            .AskAsync(Ask("همین بارگیری را بررسی کن."), Admin(), CancellationToken.None);

        Assert.Contains(groq.LastTools, tool => tool.Name == "get_loading_details");
    }

    // ---- شناختن رکورد صفحهٔ جاری -------------------------------------------

    [Theory]
    [InlineData("/Loading/Details/42", "loading", "بارگیری", 42, "loading_id")]
    [InlineData("/Contracts/Details/12", "contracts", "قرارداد", 12, "contract_id")]
    [InlineData("/Suppliers/Details/7", "suppliers", "تأمین‌کننده", 7, "supplier_id")]
    [InlineData("/Customers/9/Statement", "customers", "مشتری", 9, "customer_id")]
    [InlineData("/SupplierBalanceTransfers/History?supplierId=4", "supplierbalancetransfers", "تأمین‌کننده", 4, "supplier_id")]
    [InlineData("/ContractJourney/Details?contractId=33", "contractjourney", "قرارداد", 33, "contract_id")]
    public void The_Record_Of_The_Current_Page_Is_Recognised(
        string route, string controller, string kind, int id, string toolArgument)
    {
        var record = AssistantPageRecordResolver.Resolve(new AssistantPageContext
        {
            Route = route,
            Controller = controller,
        });

        Assert.NotNull(record);
        Assert.Equal(kind, record!.Value.Kind);
        Assert.Equal(id, record.Value.Id);
        Assert.Equal(toolArgument, record.Value.ToolArgument);
    }

    [Theory]
    [InlineData("/Loading", "loading")]
    [InlineData("/Inventory", "inventory")]
    [InlineData("/Reports/Whatever/5", "reports")]
    public void A_Page_Without_An_Open_Record_Reports_Nothing(string route, string controller)
    {
        var record = AssistantPageRecordResolver.Resolve(new AssistantPageContext
        {
            Route = route,
            Controller = controller,
        });

        Assert.Null(record);
    }

    [Fact]
    public async Task A_Question_About_The_Current_Loading_Sends_The_Page_Record_To_The_Model()
    {
        var gemini = new ScriptedProvider("Gemini", AssistantProviderResult.Success("بررسی شد."));

        await Build(ProductionShape(), Registry(new FakeTool("get_loading_details", "Loading")), null, gemini)
            .AskAsync(Ask("همین بارگیری را کامل بررسی کن."), Admin(), CancellationToken.None);

        var content = UserMessage(gemini.LastMessages)!.Content;
        Assert.Contains("بارگیری با شناسه 42", content, StringComparison.Ordinal);
        Assert.Contains("loading_id", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_Question_On_A_Contract_Page_Sends_The_Contract_Id()
    {
        var gemini = new ScriptedProvider("Gemini", AssistantProviderResult.Success("بررسی شد."));

        await Build(ProductionShape(), Registry(new FakeTool("get_contract_progress", "Contracts")), null, gemini)
            .AskAsync(
                Ask("این قرارداد چقدر باقی مانده دارد؟", controller: "contracts", route: "/Contracts/Details/12"),
                Admin(),
                CancellationToken.None);

        var content = UserMessage(gemini.LastMessages)!.Content;
        Assert.Contains("قرارداد با شناسه 12", content, StringComparison.Ordinal);
        Assert.Contains("contract_id", content, StringComparison.Ordinal);
    }

    // ---- دسترسی ------------------------------------------------------------

    [Fact]
    public async Task A_User_Without_Financial_Access_Never_Sees_The_Ledger_Tool()
    {
        var gemini = new ScriptedProvider("Gemini", AssistantProviderResult.Success("پاسخ"));
        var registry = Registry(
            new FakeTool("get_loading_details", "Loading"),
            new FakeTool("get_party_ledger", "PartyStatements"),
            new FakeTool("get_party_balance", "PartyStatements"));

        await Build(ProductionShape(), registry, null, gemini)
            .AskAsync(Ask("مانده این تأمین‌کننده چقدر است؟"), OperationsOnlyUser(), CancellationToken.None);

        var offered = gemini.LastTools.Select(tool => tool.Name).ToList();
        Assert.Contains("get_loading_details", offered);
        Assert.DoesNotContain("get_party_ledger", offered);
        Assert.DoesNotContain("get_party_balance", offered);
    }

    [Fact]
    public async Task A_Financial_Tool_Is_Blocked_Even_If_The_Model_Asks_For_It_By_Name()
    {
        // بررسی دوم: حتی اگر مدل نام ابزاری را از جای دیگری برداشته باشد، اجرا نمی‌شود.
        var ledger = new FakeTool("get_party_ledger", "PartyStatements", "مانده=-42,500");
        var registry = Registry(ledger);

        var result = await registry.ExecuteAsync(
            new AssistantToolCall("c1", "get_party_ledger", "{}"),
            OperationsOnlyUser(),
            CancellationToken.None);

        Assert.Equal(0, ledger.Runs);
        Assert.Contains("دسترسی", result, StringComparison.Ordinal);
        Assert.DoesNotContain("42,500", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_Allowed_Tool_Runs_For_The_Same_User()
    {
        var loading = new FakeTool("get_loading_details", "Loading", "بارگیری شناسه=42");
        var registry = Registry(loading);

        var result = await registry.ExecuteAsync(
            new AssistantToolCall("c1", "get_loading_details", "{}"),
            OperationsOnlyUser(),
            CancellationToken.None);

        Assert.Equal(1, loading.Runs);
        Assert.Contains("42", result, StringComparison.Ordinal);
    }

    // ---- بدون نشت کلید ------------------------------------------------------

    [Fact]
    public async Task No_Api_Key_Ever_Reaches_The_Answer_Or_The_Log()
    {
        const string fakeKey = "AQ.SECRET-TEST-KEY-VALUE";
        var previous = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
        Environment.SetEnvironmentVariable("GEMINI_API_KEY", fakeKey);

        try
        {
            var logger = new CapturingLogger<AssistantService>();
            var gemini = new ScriptedProvider("Gemini", AssistantProviderResult.Failed(AssistantFailure.AccessDenied));
            var groq = new ScriptedProvider("Groq", AssistantProviderResult.Success("نباید صدا زده شود"));

            var answer = await Build(ProductionShape(), Registry(), logger, gemini, groq)
                .AskAsync(Ask("موجودی چقدر است؟"), Admin(), CancellationToken.None);

            Assert.False(answer.Ok);
            Assert.DoesNotContain(fakeKey, answer.Message, StringComparison.Ordinal);

            // نام متغیر می‌آید تا مدیر سیستم بداند کجا را درست کند؛ مقدارش هرگز.
            Assert.Contains("GEMINI_API_KEY", answer.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(fakeKey, string.Join("\n", logger.Lines), StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEMINI_API_KEY", previous);
        }
    }
}
