using System.Runtime.CompilerServices;
using Xunit;

namespace PTGOilSystem.Web.Tests;

public class AuthLoginViewStructureTests
{
    [Fact]
    public void Login_View_Uses_The_Restored_Saddiqi_Visual_Contract()
    {
        var view = ReadRepoFile("src/PTGOilSystem.Web/Views/Auth/Login.cshtml");
        var css = ReadRepoFile("src/PTGOilSystem.Web/wwwroot/css/ptg/90-auth-login.css");
        // The carousel serves scene 1 and 3 as PNG and scene 2 as WebP; every slide is
        // cache-busted with asp-append-version, so the file must exist on disk.
        var artwork = new[]
        {
            RepoFile("src/PTGOilSystem.Web/wwwroot/images/auth/scene-1.png"),
            RepoFile("src/PTGOilSystem.Web/wwwroot/images/auth/scene-2.webp"),
            RepoFile("src/PTGOilSystem.Web/wwwroot/images/auth/scene-3.png")
        };

        Assert.Contains("~/css/ptg/90-auth-login.css", view);
        Assert.Contains("~/images/auth/scene-1.png", view);
        Assert.Contains("~/images/auth/scene-2.webp", view);
        Assert.Contains("~/images/auth/scene-3.png", view);
        Assert.Contains("asp-append-version=\"true\"", view);
        Assert.DoesNotContain("~/images/auth/login.webp", view);
        Assert.DoesNotContain("~/images/auth/saddiqi-login.png", view);
        Assert.Contains("class=\"sadd-login-page\"", view);
        Assert.Contains("class=\"sadd-login-panel\"", view);
        Assert.Contains("class=\"sadd-login-form\"", view);
        Assert.Contains("class=\"sadd-login-submit\"", view);
        Assert.Contains(".sadd-login-art", css);
        Assert.Contains(".sadd-login-panel", css);
        Assert.All(artwork, image =>
        {
            Assert.True(image.Exists);
            Assert.True(image.Length > 20_000);
        });
    }

    [Fact]
    public void Login_View_Preserves_The_Secure_Form_Contract()
    {
        var view = ReadRepoFile("src/PTGOilSystem.Web/Views/Auth/Login.cshtml");

        Assert.Contains("method=\"post\"", view);
        Assert.Contains("@Html.AntiForgeryToken()", view);
        Assert.Contains("asp-for=\"ReturnUrl\"", view);
        Assert.Contains("autocomplete=\"username\"", view);
        Assert.Contains("autocomplete=\"current-password\"", view);
        Assert.Contains("asp-validation-summary=\"ModelOnly\"", view);
        Assert.Contains("asp-validation-for=\"Username\"", view);
        Assert.Contains("asp-validation-for=\"Password\"", view);
    }

    [Fact]
    public void Program_Audits_Login_Rate_Limit_Without_Reading_Form_Credentials()
    {
        var program = ReadRepoFile("src/PTGOilSystem.Web/Program.cs");

        Assert.Contains("LoginAuditActions.RateLimited", program);
        Assert.Contains("StatusCodes.Status429TooManyRequests", program);
        Assert.DoesNotContain("ReadFormAsync", program);
    }

    private static FileInfo RepoFile(
        string relativePath,
        [CallerFilePath] string callerFilePath = "")
    {
        var testsDirectory = Path.GetDirectoryName(callerFilePath)!;
        var repositoryRoot = Path.GetFullPath(Path.Combine(testsDirectory, "..", ".."));
        return new FileInfo(Path.Combine(repositoryRoot, relativePath));
    }

    private static string ReadRepoFile(
        string relativePath,
        [CallerFilePath] string callerFilePath = "")
        => File.ReadAllText(RepoFile(relativePath, callerFilePath).FullName);
}
