using System.Runtime.CompilerServices;
using Xunit;

namespace PTGOilSystem.Web.Tests;

public class AuthLoginViewStructureTests
{
    [Fact]
    public void Login_View_Uses_The_Two_Column_Card_Visual_Contract()
    {
        var view = ReadRepoFile("src/PTGOilSystem.Web/Views/Auth/Login.cshtml");
        var css = ReadRepoFile("src/PTGOilSystem.Web/wwwroot/css/ptg/90-auth-login.css");
        // A single artwork panel replaced the old three-scene carousel. The image is
        // cache-busted with asp-append-version, so the file must exist on disk.
        var artwork = RepoFile("src/PTGOilSystem.Web/wwwroot/images/auth/login-art.jpeg");

        Assert.Contains("~/css/ptg/90-auth-login.css", view);
        Assert.Contains("~/images/auth/login-art.jpeg", view);
        Assert.Contains("asp-append-version=\"true\"", view);
        // Retired artwork contracts must not creep back in.
        Assert.DoesNotContain("~/images/auth/scene-1", view);
        Assert.DoesNotContain("~/images/auth/scene-2", view);
        Assert.DoesNotContain("~/images/auth/scene-3", view);
        Assert.DoesNotContain("~/images/auth/login.webp", view);
        Assert.DoesNotContain("~/images/auth/saddiqi-login.png", view);
        Assert.Contains("class=\"ptg-login-page\"", view);
        Assert.Contains("class=\"ptg-login-card\"", view);
        Assert.Contains("class=\"ptg-login-art\"", view);
        Assert.Contains("class=\"ptg-login-panel\"", view);
        Assert.Contains("class=\"ptg-login-form\"", view);
        Assert.Contains("class=\"ptg-login-submit\"", view);
        Assert.DoesNotContain("<figcaption", view);
        // Heading block: title on its own line with the subtitle under it.
        Assert.Contains("class=\"ptg-login-heading\"", view);
        Assert.Contains("class=\"ptg-login-title\"", view);
        Assert.Contains("class=\"ptg-login-subtitle\"", view);
        Assert.True(
            view.IndexOf("class=\"ptg-login-title\"", StringComparison.Ordinal)
            < view.IndexOf("class=\"ptg-login-subtitle\"", StringComparison.Ordinal));
        Assert.True(
            view.IndexOf("class=\"ptg-login-heading\"", StringComparison.Ordinal)
            < view.IndexOf("class=\"ptg-login-fields\"", StringComparison.Ordinal));
        Assert.Contains(".ptg-login-art", css);
        Assert.Contains(".ptg-login-panel", css);
        Assert.Contains(".ptg-login-heading", css);
        Assert.Contains("--lg-accent: #d9605c;", css);
        Assert.True(artwork.Exists);
        Assert.True(artwork.Length > 20_000);
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
