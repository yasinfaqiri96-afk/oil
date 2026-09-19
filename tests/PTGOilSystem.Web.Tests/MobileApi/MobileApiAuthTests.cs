using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Infrastructure.Api;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.Mobile;
using PTGOilSystem.Web.Security;
using PTGOilSystem.Web.Security.Mobile;
using PTGOilSystem.Web.Services;
using Xunit;
using static PTGOilSystem.Web.Tests.MobileApi.MobileApiTestHost;

namespace PTGOilSystem.Web.Tests.MobileApi;

/// <summary>ورود، چرخش توکن، ابطال و خروج موبایل از طریق pipeline واقعی HTTP.</summary>
public sealed class MobileApiAuthTests
{
    [Fact]
    public async Task Login_With_Valid_Credentials_Returns_Tokens_Profile_And_Stores_Only_A_Hash()
    {
        await using var host = await StartAsync();
        var user = await host.SeedUserAsync("operator1", AuthRoles.Operator);

        var auth = await host.LoginAsync("operator1");

        Assert.Equal("Bearer", auth.TokenType);
        Assert.False(string.IsNullOrWhiteSpace(auth.AccessToken));
        Assert.False(string.IsNullOrWhiteSpace(auth.RefreshToken));
        Assert.True(auth.AccessTokenExpiresAtUtc > DateTime.UtcNow);
        Assert.True(auth.AccessTokenExpiresAtUtc <= DateTime.UtcNow.AddMinutes(16));
        Assert.True(auth.RefreshTokenExpiresAtUtc > auth.AccessTokenExpiresAtUtc);
        Assert.Equal(user.Id, auth.User.UserId);
        Assert.Equal(AuthRoles.Operator, auth.User.Role);
        Assert.Contains(RoleNavigationKeys.Dashboard, auth.User.Navigation);
        Assert.Contains(AppPermissions.ManageData, auth.User.Permissions);

        // JWT فقط شناسه دارد؛ نقش و Permission داخل توکن نیست.
        var payload = Encoding.UTF8.GetString(Base64UrlEncoder.DecodeBytes(auth.AccessToken.Split('.')[1]));
        Assert.Contains("\"sid\"", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("role", payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("permission", payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("navigation", payload, StringComparison.OrdinalIgnoreCase);

        var stored = await host.WithDbAsync(db => db.MobileRefreshTokens.AsNoTracking().SingleAsync());
        Assert.NotEqual(auth.RefreshToken, stored.TokenHash);
        Assert.Equal(MobileTokenService.HashRefreshToken(auth.RefreshToken), stored.TokenHash);
        Assert.Equal(64, stored.TokenHash.Length);
        Assert.Equal("Test phone", stored.DeviceName);
        Assert.Null(stored.RevokedAtUtc);

        var success = await host.WithDbAsync(db => db.AuditLogs.AsNoTracking()
            .SingleAsync(a => a.Action == LoginAuditActions.Succeeded));
        Assert.Equal("operator1", success.ActorUsername);
        Assert.Equal(AuditLogCategories.Authentication, success.Category);
    }

    [Fact]
    public async Task Login_With_Wrong_Password_Returns_401_Problem_And_Audits_Without_Secrets()
    {
        await using var host = await StartAsync();
        await host.SeedUserAsync("operator1", AuthRoles.Operator);

        var response = await host.Client.PostAsJsonAsync(LoginPath, new { username = "operator1", password = "wrong-password-value" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var problem = await ReadProblemAsync(response);
        Assert.Equal(ApiErrorCodes.InvalidCredentials, ProblemCode(problem));

        var failed = await host.WithDbAsync(db => db.AuditLogs.AsNoTracking()
            .SingleAsync(a => a.Action == LoginAuditActions.Failed));
        Assert.DoesNotContain("wrong-password-value", failed.Description ?? "", StringComparison.Ordinal);
        Assert.DoesNotContain("wrong-password-value", failed.MetadataJson ?? "", StringComparison.Ordinal);
        Assert.Equal(0, await host.WithDbAsync(db => db.MobileRefreshTokens.CountAsync()));
    }

    [Fact]
    public async Task Login_For_Inactive_User_Returns_The_Same_Generic_401()
    {
        await using var host = await StartAsync();
        await host.SeedUserAsync("disabled1", AuthRoles.Operator, isActive: false);

        var response = await host.Client.PostAsJsonAsync(LoginPath, new { username = "disabled1", password = Password });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(ApiErrorCodes.InvalidCredentials, ProblemCode(await ReadProblemAsync(response)));
        Assert.Equal(0, await host.WithDbAsync(db => db.MobileRefreshTokens.CountAsync()));
    }

    [Fact]
    public async Task Login_With_Empty_Body_Returns_400_Validation_Problem()
    {
        await using var host = await StartAsync();

        var response = await host.Client.PostAsJsonAsync(LoginPath, new { });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await ReadProblemAsync(response);
        Assert.Equal(ApiErrorCodes.Validation, ProblemCode(problem));
        Assert.True(problem.TryGetProperty("errors", out var errors));
        Assert.True(errors.TryGetProperty("Username", out _));
    }

    [Fact]
    public async Task Login_Uses_The_Shared_Lockout_And_Returns_429()
    {
        await using var host = await StartAsync();
        await host.SeedUserAsync("operator1", AuthRoles.Operator);
        await host.WithDbAsync(async db =>
        {
            for (var minutesAgo = 5; minutesAgo > 0; minutesAgo--)
            {
                db.AuditLogs.Add(new AuditLog
                {
                    Category = AuditLogCategories.Authentication,
                    EntityName = nameof(User),
                    Action = LoginAuditActions.Failed,
                    ActorUsername = "operator1",
                    IpAddress = "unknown",
                    ActionAtUtc = DateTime.UtcNow.AddMinutes(-minutesAgo),
                    IsSuccess = false
                });
            }

            return await db.SaveChangesAsync();
        });

        var response = await host.Client.PostAsJsonAsync(LoginPath, new { username = "operator1", password = Password });

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal(ApiErrorCodes.AccountLocked, ProblemCode(await ReadProblemAsync(response)));
        Assert.Equal(0, await host.WithDbAsync(db => db.MobileRefreshTokens.CountAsync()));
    }

    [Fact]
    public async Task Login_Returns_503_When_Signing_Key_Is_Not_Configured_In_Production()
    {
        await using var host = await StartAsync(signingKey: null);
        await host.SeedUserAsync("operator1", AuthRoles.Operator);

        var response = await host.Client.PostAsJsonAsync(LoginPath, new { username = "operator1", password = Password });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(ApiErrorCodes.MobileAuthUnavailable, ProblemCode(await ReadProblemAsync(response)));
    }

    [Fact]
    public async Task Api_Without_Token_Returns_401_Json_Not_A_Redirect()
    {
        await using var host = await StartAsync();

        var response = await host.SendAsync(HttpMethod.Get, MePath, accept: "text/html");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(ApiErrorCodes.Unauthorized, ProblemCode(await ReadProblemAsync(response)));
        Assert.Contains("Bearer", response.Headers.WwwAuthenticate.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Expired_Access_Token_Returns_401_Token_Expired()
    {
        await using var host = await StartAsync();
        await host.SeedUserAsync("operator1", AuthRoles.Operator);
        await host.LoginAsync("operator1");

        var expiredToken = await host.WithScopeAsync(async services =>
        {
            var db = services.GetRequiredService<ApplicationDbContext>();
            var session = await db.MobileRefreshTokens.AsNoTracking().SingleAsync();
            var user = await db.Users.Include(u => u.Role).SingleAsync(u => u.Id == session.UserId);
            var issuedTwoHoursAgo = new MobileTokenService(
                db,
                services.GetRequiredService<MobileJwtKeyProvider>(),
                services.GetRequiredService<IOptions<MobileAuthOptions>>(),
                new FixedTimeProvider(DateTimeOffset.UtcNow.AddHours(-2)));
            return issuedTwoHoursAgo.CreateAccessToken(user, session.SessionId, out _);
        });

        var response = await host.SendAsync(HttpMethod.Get, MePath, accessToken: expiredToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(ApiErrorCodes.TokenExpired, ProblemCode(await ReadProblemAsync(response)));
    }

    [Fact]
    public async Task Refresh_Rotates_Tokens_And_Reusing_The_Old_Token_Revokes_The_Whole_Session()
    {
        await using var host = await StartAsync();
        await host.SeedUserAsync("operator1", AuthRoles.Operator);
        var first = await host.LoginAsync("operator1");

        var refreshed = await host.RefreshAsync(first.RefreshToken);
        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
        var second = (await refreshed.Content.ReadFromJsonAsync<MobileAuthResponse>(JsonOptions))!;
        Assert.NotEqual(first.RefreshToken, second.RefreshToken);
        Assert.NotEqual(first.AccessToken, second.AccessToken);
        Assert.Equal(first.RefreshTokenExpiresAtUtc, second.RefreshTokenExpiresAtUtc);

        var rows = await host.WithDbAsync(db => db.MobileRefreshTokens.AsNoTracking().OrderBy(t => t.Id).ToListAsync());
        Assert.Equal(2, rows.Count);
        Assert.Equal(MobileRefreshTokenRevocationReasons.Rotated, rows[0].RevokedReason);
        Assert.Equal(rows[1].Id, rows[0].ReplacedByTokenId);
        Assert.Equal(rows[0].SessionId, rows[1].SessionId);
        Assert.Null(rows[1].RevokedAtUtc);

        Assert.Equal(HttpStatusCode.OK, (await host.SendAsync(HttpMethod.Get, MePath, accessToken: second.AccessToken)).StatusCode);

        // Replay of the rotated token.
        var replay = await host.RefreshAsync(first.RefreshToken);
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
        Assert.Equal(ApiErrorCodes.RefreshTokenReused, ProblemCode(await ReadProblemAsync(replay)));

        var afterReplay = await host.RefreshAsync(second.RefreshToken);
        Assert.Equal(HttpStatusCode.Unauthorized, afterReplay.StatusCode);
        Assert.Equal(ApiErrorCodes.RefreshTokenInvalid, ProblemCode(await ReadProblemAsync(afterReplay)));

        var me = await host.SendAsync(HttpMethod.Get, MePath, accessToken: second.AccessToken);
        Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);
        Assert.Equal(ApiErrorCodes.SessionRevoked, ProblemCode(await ReadProblemAsync(me)));

        Assert.True(await host.WithDbAsync(db => db.AuditLogs.AnyAsync(a =>
            a.Category == AuditLogCategories.Security && a.Action == "MobileRefreshTokenReuse")));
        Assert.Equal(
            MobileRefreshTokenRevocationReasons.ReuseDetected,
            await host.WithDbAsync(db => db.MobileRefreshTokens.Where(t => t.Id == rows[1].Id).Select(t => t.RevokedReason).SingleAsync()));
    }

    [Fact]
    public async Task Unknown_Refresh_Token_Returns_401()
    {
        await using var host = await StartAsync();

        var response = await host.RefreshAsync("not-a-real-refresh-token");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(ApiErrorCodes.RefreshTokenInvalid, ProblemCode(await ReadProblemAsync(response)));
    }

    [Fact]
    public async Task Logout_Revokes_The_Session_So_Refresh_And_Access_Token_Stop_Working()
    {
        await using var host = await StartAsync();
        await host.SeedUserAsync("operator1", AuthRoles.Operator);
        var auth = await host.LoginAsync("operator1");

        var logout = await host.SendAsync(HttpMethod.Post, LogoutPath, accessToken: auth.AccessToken,
            body: new { refreshToken = auth.RefreshToken });
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);

        var refresh = await host.RefreshAsync(auth.RefreshToken);
        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);
        Assert.Equal(ApiErrorCodes.RefreshTokenInvalid, ProblemCode(await ReadProblemAsync(refresh)));

        var me = await host.SendAsync(HttpMethod.Get, MePath, accessToken: auth.AccessToken);
        Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);
        Assert.Equal(ApiErrorCodes.SessionRevoked, ProblemCode(await ReadProblemAsync(me)));

        var row = await host.WithDbAsync(db => db.MobileRefreshTokens.AsNoTracking().SingleAsync());
        Assert.Equal(MobileRefreshTokenRevocationReasons.Logout, row.RevokedReason);
    }

    [Fact]
    public async Task Logging_Out_One_Device_Keeps_Other_Devices_Signed_In()
    {
        await using var host = await StartAsync();
        await host.SeedUserAsync("operator1", AuthRoles.Operator);
        var phoneA = await host.LoginAsync("operator1", deviceName: "Phone A");
        var phoneB = await host.LoginAsync("operator1", deviceName: "Phone B");

        var logout = await host.SendAsync(HttpMethod.Post, LogoutPath, body: new { refreshToken = phoneA.RefreshToken });
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await host.SendAsync(HttpMethod.Get, MePath, accessToken: phoneA.AccessToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await host.SendAsync(HttpMethod.Get, MePath, accessToken: phoneB.AccessToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await host.RefreshAsync(phoneB.RefreshToken)).StatusCode);
    }

    [Fact]
    public async Task Deactivated_User_Loses_Api_Access_And_Cannot_Refresh()
    {
        await using var host = await StartAsync();
        var user = await host.SeedUserAsync("operator1", AuthRoles.Operator);
        var auth = await host.LoginAsync("operator1");

        await host.SetUserActiveAsync(user.Id, false);

        var me = await host.SendAsync(HttpMethod.Get, MePath, accessToken: auth.AccessToken);
        Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);
        var refresh = await host.RefreshAsync(auth.RefreshToken);
        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);
        Assert.Equal(ApiErrorCodes.RefreshTokenInvalid, ProblemCode(await ReadProblemAsync(refresh)));
        Assert.Equal(
            MobileRefreshTokenRevocationReasons.UserInactive,
            await host.WithDbAsync(db => db.MobileRefreshTokens.Select(t => t.RevokedReason).SingleAsync()));
    }

    [Fact]
    public async Task Password_Change_Revokes_Mobile_Sessions()
    {
        await using var host = await StartAsync();
        await host.SeedUserAsync("operator1", AuthRoles.Operator);
        var auth = await host.LoginAsync("operator1");

        await host.WithScopeAsync(async services =>
        {
            await services.GetRequiredService<IUserService>()
                .ChangePasswordAsync(auth.User.UserId, Password, "AnotherStrong456!");
            return 0;
        });

        Assert.Equal(HttpStatusCode.Unauthorized, (await host.RefreshAsync(auth.RefreshToken)).StatusCode);
        var me = await host.SendAsync(HttpMethod.Get, MePath, accessToken: auth.AccessToken);
        Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);
        Assert.Equal(ApiErrorCodes.SessionRevoked, ProblemCode(await ReadProblemAsync(me)));
        Assert.Equal(
            MobileRefreshTokenRevocationReasons.PasswordChanged,
            await host.WithDbAsync(db => db.MobileRefreshTokens.Select(t => t.RevokedReason).SingleAsync()));
    }
}
