using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Infrastructure.Api;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Security;
using Xunit;
using static PTGOilSystem.Web.Tests.MobileApi.MobileApiTestHost;

namespace PTGOilSystem.Web.Tests.MobileApi;

/// <summary>
/// هم‌زیستی کوکی وب و Bearer موبایل، دسترسی API، قرارداد خطا، ورود خودکار توسعه و Idempotency.
/// </summary>
public sealed class MobileApiSecurityTests
{
    [Fact]
    public async Task Me_Returns_Safe_Profile_For_Bearer_User()
    {
        await using var host = await StartAsync();
        var user = await host.SeedUserAsync("admin1", AuthRoles.Admin);
        var auth = await host.LoginAsync("admin1");

        var response = await host.SendAsync(HttpMethod.Get, MePath, accessToken: auth.AccessToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal(user.Id, root.GetProperty("userId").GetInt32());
        Assert.Equal("admin1", root.GetProperty("username").GetString());
        Assert.Equal(AuthRoles.Admin, root.GetProperty("role").GetString());
        Assert.True(root.GetProperty("capabilities").GetProperty("reports").GetBoolean());
        Assert.DoesNotContain("passwordHash", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PBKDF2", json, StringComparison.Ordinal);
        Assert.DoesNotContain("tokenHash", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Web_Cookie_Still_Authenticates_Web_Endpoints_But_Not_The_Mobile_Api()
    {
        await using var host = await StartAsync();
        await host.SeedUserAsync("admin1", AuthRoles.Admin);

        var signIn = await host.Client.GetAsync("/test/web/sign-in/admin1");
        Assert.Equal(HttpStatusCode.OK, signIn.StatusCode);
        var cookie = ExtractAuthCookie(signIn);

        var web = await host.SendAsync(HttpMethod.Get, "/test/web/whoami", cookie: cookie);
        Assert.Equal(HttpStatusCode.OK, web.StatusCode);
        Assert.Equal("admin1 Full", await web.Content.ReadAsStringAsync());

        var api = await host.SendAsync(HttpMethod.Get, MePath, cookie: cookie);
        Assert.Equal(HttpStatusCode.Unauthorized, api.StatusCode);
        Assert.Equal(ApiErrorCodes.Unauthorized, ProblemCode(await ReadProblemAsync(api)));
    }

    [Fact]
    public async Task Web_Endpoints_Keep_Cookie_Redirects_And_Ignore_Bearer_Tokens()
    {
        await using var host = await StartAsync();
        await host.SeedUserAsync("admin1", AuthRoles.Admin);
        var auth = await host.LoginAsync("admin1");

        var withBearer = await host.SendAsync(HttpMethod.Get, "/test/web/whoami", accessToken: auth.AccessToken);
        Assert.Equal(HttpStatusCode.Redirect, withBearer.StatusCode);
        Assert.Contains("/Auth/Login", withBearer.Headers.Location!.ToString(), StringComparison.Ordinal);

        var home = await host.SendAsync(HttpMethod.Get, "/Home/Index");
        Assert.Equal(HttpStatusCode.Redirect, home.StatusCode);
        Assert.Contains("/Auth/Login", home.Headers.Location!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Api_Navigation_Denied_Returns_403_Json_Using_Web_Role_Rules()
    {
        await using var host = await StartAsync();
        await host.SeedUserAsync("dash1", "DashboardOnly", allowedNavigation: RoleNavigationKeys.Dashboard);
        await host.SeedUserAsync("admin1", AuthRoles.Admin);
        var restricted = await host.LoginAsync("dash1");
        var admin = await host.LoginAsync("admin1");

        var denied = await host.SendAsync(HttpMethod.Get, "/api/mobile/v1/test-probe/reports", accessToken: restricted.AccessToken);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal(ApiErrorCodes.Forbidden, ProblemCode(await ReadProblemAsync(denied)));

        Assert.Equal(HttpStatusCode.OK,
            (await host.SendAsync(HttpMethod.Get, "/api/mobile/v1/test-probe/reports", accessToken: admin.AccessToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await host.SendAsync(HttpMethod.Get, MePath, accessToken: restricted.AccessToken)).StatusCode);
        Assert.False(restricted.User.Capabilities.Reports);
    }

    [Fact]
    public async Task Dashboard_Requires_Bearer_And_Returns_Json_For_Authenticated_User()
    {
        var dashboard = new FakeMobileDashboardService();
        await using var host = await StartAsync(dashboard: dashboard);
        await host.SeedUserAsync("operator1", AuthRoles.Operator);

        var anonymous = await host.SendAsync(HttpMethod.Get, DashboardPath);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Null(dashboard.LastUser);

        var auth = await host.LoginAsync("operator1");
        var response = await host.SendAsync(HttpMethod.Get, DashboardPath, accessToken: auth.AccessToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(12.5m, document.RootElement.GetProperty("inventory").GetProperty("totalMt").GetDecimal());
        Assert.Equal("2026-09-15", document.RootElement.GetProperty("businessDate").GetString());
        Assert.Equal("operator1 Full", dashboard.LastUser!.Identity!.Name);
    }

    [Fact]
    public async Task Unknown_Api_Route_Returns_404_Problem()
    {
        await using var host = await StartAsync();

        var response = await host.SendAsync(HttpMethod.Get, "/api/mobile/v1/does-not-exist");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(ApiErrorCodes.NotFound, ProblemCode(await ReadProblemAsync(response)));
    }

    [Fact]
    public async Task Api_Errors_Map_To_ProblemDetails_Without_Leaking_Internal_Details()
    {
        await using var host = await StartAsync();
        await host.SeedUserAsync("admin1", AuthRoles.Admin);
        var auth = await host.LoginAsync("admin1");

        var rule = await host.SendAsync(HttpMethod.Get, "/api/mobile/v1/test-probe/business-rule", accessToken: auth.AccessToken);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, rule.StatusCode);
        var ruleProblem = await ReadProblemAsync(rule);
        Assert.Equal(ApiErrorCodes.BusinessRule, ProblemCode(ruleProblem));
        Assert.Equal("TEST_RULE", ruleProblem.GetProperty("ruleCode").GetString());

        var conflict = await host.SendAsync(HttpMethod.Get, "/api/mobile/v1/test-probe/concurrency", accessToken: auth.AccessToken);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        var conflictBody = await conflict.Content.ReadAsStringAsync();
        Assert.Contains($"\"code\":\"{ApiErrorCodes.Conflict}\"", conflictBody, StringComparison.Ordinal);
        Assert.DoesNotContain("table X", conflictBody, StringComparison.Ordinal);

        var crash = await host.SendAsync(HttpMethod.Get, "/api/mobile/v1/test-probe/crash", accessToken: auth.AccessToken);
        Assert.Equal(HttpStatusCode.InternalServerError, crash.StatusCode);
        Assert.Equal(ApiProblem.ContentType, crash.Content.Headers.ContentType?.MediaType);
        var crashBody = await crash.Content.ReadAsStringAsync();
        Assert.Contains($"\"code\":\"{ApiErrorCodes.ServerError}\"", crashBody, StringComparison.Ordinal);
        Assert.DoesNotContain("super-secret", crashBody, StringComparison.Ordinal);
        Assert.DoesNotContain("Host=", crashBody, StringComparison.Ordinal);
        Assert.DoesNotContain("InvalidOperationException", crashBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Development_Auto_Sign_In_Never_Authenticates_Api_Requests()
    {
        await using var host = await StartAsync(environment: "Development");
        await host.SeedUserAsync("admin", AuthRoles.Admin);

        var api = await host.SendAsync(HttpMethod.Get, MePath, accept: "text/html");
        Assert.Equal(HttpStatusCode.Unauthorized, api.StatusCode);
        Assert.False(api.Headers.Contains("Set-Cookie"));

        // The browser development workflow is unchanged.
        var web = await host.SendAsync(HttpMethod.Get, "/test/web/whoami", accept: "text/html");
        Assert.Contains(web.Headers.GetValues("Set-Cookie"), value => value.StartsWith("PTGOilSystem.Auth=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Idempotency_Key_Is_Required_Validated_And_Duplicates_Are_Rejected()
    {
        await using var host = await StartAsync();
        await host.SeedUserAsync("admin1", AuthRoles.Admin);
        var auth = await host.LoginAsync("admin1");
        const string path = "/api/mobile/v1/test-probe/write";

        var missing = await host.SendAsync(HttpMethod.Post, path, accessToken: auth.AccessToken);
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Equal(ApiErrorCodes.IdempotencyKeyRequired, ProblemCode(await ReadProblemAsync(missing)));

        var invalid = await host.SendAsync(HttpMethod.Post, path, accessToken: auth.AccessToken,
            headers: new Dictionary<string, string> { [RequireIdempotencyKeyAttribute.HeaderName] = "not-a-uuid" });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal(ApiErrorCodes.IdempotencyKeyInvalid, ProblemCode(await ReadProblemAsync(invalid)));

        var key = Guid.NewGuid();
        var headers = new Dictionary<string, string> { [RequireIdempotencyKeyAttribute.HeaderName] = key.ToString() };
        var accepted = await host.SendAsync(HttpMethod.Post, path, accessToken: auth.AccessToken, headers: headers);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Contains(MobileIdempotency.ToToken(key), await accepted.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        await host.WithDbAsync(async db =>
        {
            db.ProcessedFormTokens.Add(new ProcessedFormToken
            {
                Token = MobileIdempotency.ToToken(key),
                Purpose = "Mobile.Test.Write",
                ReferenceType = "Test",
                ReferenceId = 7,
                ConsumedAtUtc = DateTime.UtcNow
            });
            return await db.SaveChangesAsync();
        });

        var duplicate = await host.SendAsync(HttpMethod.Post, path, accessToken: auth.AccessToken, headers: headers);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        var problem = await ReadProblemAsync(duplicate);
        Assert.Equal(ApiErrorCodes.DuplicateRequest, ProblemCode(problem));
        Assert.Equal(7, problem.GetProperty("referenceId").GetInt32());
    }

    [Fact]
    public void Mobile_Token_Must_Fit_The_Existing_Processed_Form_Token_Column()
    {
        Assert.True(MobileIdempotency.ToToken(Guid.NewGuid()).Length <= 64);
    }
}
