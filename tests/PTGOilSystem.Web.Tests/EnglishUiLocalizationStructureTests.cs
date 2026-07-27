using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace PTGOilSystem.Web.Tests;

public sealed class EnglishUiLocalizationStructureTests
{
    [Fact]
    public void Layout_Loads_English_Catalog_Before_Language_Runtime()
    {
        var layout = ReadRepoFile("src/PTGOilSystem.Web/Views/Shared/_Layout.cshtml");
        var catalogIndex = layout.IndexOf("~/js/language-catalog.en.js", StringComparison.Ordinal);
        var runtimeIndex = layout.IndexOf("~/js/language.js", StringComparison.Ordinal);

        Assert.True(catalogIndex >= 0, "The English UI catalog must be loaded by the shared layout.");
        Assert.True(runtimeIndex > catalogIndex, "The English UI catalog must load before language.js.");
    }

    [Fact]
    public void English_Catalog_Has_Reviewed_Core_Terms_And_No_Persian_Values()
    {
        var source = ReadRepoFile("src/PTGOilSystem.Web/wwwroot/js/language-catalog.en.js");
        var jsonStart = source.IndexOf('{');
        var jsonEnd = source.LastIndexOf('}');
        Assert.True(jsonStart >= 0 && jsonEnd > jsonStart, "The generated catalog must contain a JSON object.");

        using var document = JsonDocument.Parse(source[jsonStart..(jsonEnd + 1)]);
        var catalog = document.RootElement;

        Assert.True(catalog.EnumerateObject().Count() >= 2_000);
        Assert.Equal("Contract", catalog.GetProperty("قرارداد").GetString());
        Assert.Equal("Supplier", catalog.GetProperty("تأمین‌کننده").GetString());
        Assert.Equal("Truck", catalog.GetProperty("موتر").GetString());
        Assert.Equal("Loading", catalog.GetProperty("بارگیری").GetString());

        var persian = new Regex("[\\u0600-\\u06ff]", RegexOptions.CultureInvariant);
        Assert.DoesNotContain(
            catalog.EnumerateObject(),
            entry => persian.IsMatch(entry.Value.GetString() ?? string.Empty));
    }

    [Fact]
    public void Language_Runtime_Translates_Form_Chrome_And_Dynamic_Content()
    {
        var runtime = ReadRepoFile("src/PTGOilSystem.Web/wwwroot/js/language.js");

        Assert.Contains("MutationObserver", runtime);
        Assert.Contains("option", runtime);
        Assert.Contains("placeholder", runtime);
        Assert.Contains("data-ptg-confirm-message", runtime);
        Assert.Contains("data-page-modal-title", runtime);
        Assert.Contains("input[type=\"submit\"]", runtime);
        Assert.Contains("translateKnownPhrases", runtime);
        Assert.Contains("toEnglishDigits", runtime);
        Assert.Contains("window.PTG.t", runtime);
        Assert.Contains("data-ptg-no-translate", runtime);
    }

    private static string ReadRepoFile(string relativePath)
    {
        return File.ReadAllText(Path.Combine(FindRepositoryRoot(), relativePath));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "src", "PTGOilSystem.Web")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
