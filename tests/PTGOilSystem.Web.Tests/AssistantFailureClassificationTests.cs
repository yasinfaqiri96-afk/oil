using Google.GenAI;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PTGOilSystem.Web.Configuration;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Services.Assistant;
using PTGOilSystem.Web.Services.Assistant.Tools;
using System.Security.Claims;
using System.Text.Json;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// دو اشکالی که فقط روی سرویس واقعی دیده شدند و باید دیگر برنگردند:
/// طبقه‌بندی خطای Gemini، و ترجمه‌پذیری کوئری جستجوی اشخاص.
/// </summary>
public class AssistantFailureClassificationTests
{
    private static GeminiAssistantProvider Provider()
        => new(Options.Create(new AssistantOptions()), NullLogger<GeminiAssistantProvider>.Instance);

    [Theory]
    [InlineData("<!DOCTYPE html><html><body>403 Forbidden</body></html>", 403)]
    [InlineData("<html><head><title>Blocked</title></head></html>", 403)]
    [InlineData("\n  <!doctype html>\n<html></html>", 401)]
    public void An_Html_Error_Body_Is_A_Network_Problem_Not_A_Bad_Key(string body, int status)
    {
        // پاسخ HTML از Google نمی‌آید؛ یک واسط شبکه آن را ساخته است. اگر
        // AccessDenied حساب شود، جایگزینی انجام نمی‌شود و کاربر پیام اشتباهِ
        // «کلید را بررسی کنید» می‌گیرد.
        var result = Provider().MapClientError(new ClientError(body, status, "PERMISSION_DENIED"), "gemini-3.6-flash");

        Assert.Equal(AssistantFailure.NetworkError, result.Failure);
    }

    [Theory]
    [InlineData(429, "quota exceeded", AssistantFailure.RateLimited)]
    [InlineData(400, "User location is not supported for the API use.", AssistantFailure.RegionUnsupported)]
    [InlineData(400, "API key not valid. Please pass a valid API key.", AssistantFailure.AccessDenied)]
    [InlineData(403, "{\"error\":{\"status\":\"PERMISSION_DENIED\"}}", AssistantFailure.AccessDenied)]
    [InlineData(404, "model not found", AssistantFailure.AccessDenied)]
    [InlineData(400, "Invalid JSON payload received.", AssistantFailure.InvalidRequest)]
    public void A_Json_Error_Keeps_Its_Own_Meaning(int status, string message, AssistantFailure expected)
    {
        var result = Provider().MapClientError(new ClientError(message, status, "ERROR"), "gemini-3.6-flash");

        Assert.Equal(expected, result.Failure);
    }

    /// <summary>
    /// جستجوی اشخاص باید به SQL ترجمه شود. نسخهٔ قبلی ILike را روی نتیجهٔ Select
    /// می‌گذاشت و روی سرویس واقعی با InvalidOperationException می‌افتاد — یعنی
    /// دستیار هیچ‌وقت شناسهٔ شخص را پیدا نمی‌کرد.
    /// </summary>
    [Fact]
    public async Task Searching_A_Party_By_Name_Translates_To_Sql()
    {
        // اتصال عمداً به جایی وصل نمی‌شود: ترجمهٔ کوئری پیش از باز کردن اتصال
        // انجام می‌شود، پس اگر ترجمه بشکند همان استثنا اول ظاهر می‌شود.
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql("Host=127.0.0.1;Port=1;Username=none;Password=none;Database=none;Timeout=1;Command Timeout=1")
            .Options;

        await using var db = new ApplicationDbContext(options);
        var tool = new SearchPartyTool(db, Options.Create(new AssistantOptions()));

        using var document = JsonDocument.Parse("{\"name\":\"Petrogas\"}");
        var user = new ClaimsPrincipal(new ClaimsIdentity());

        var error = await Record.ExceptionAsync(() =>
            tool.ExecuteAsync(document.RootElement, user, CancellationToken.None));

        // اتصال شکست می‌خورد (انتظار داریم)، ولی ترجمه نباید شکست بخورد.
        Assert.NotNull(error);
        Assert.False(
            error is InvalidOperationException invalid && invalid.Message.Contains("could not be translated", StringComparison.Ordinal),
            "کوئری جستجوی اشخاص دوباره غیرقابل ترجمه شده است: " + error!.Message);
    }
}
