using System.Text.RegularExpressions;
using Xunit;

namespace PTGOilSystem.Web.Tests;

public sealed class UnifiedUiContractTests
{
    [Fact]
    public void Shared_Transfer_Form_Has_The_Same_Managed_Assets_In_Create_And_Edit()
    {
        var create = Read("Views/CustomerToCustomerPayments/Create.cshtml");
        var edit = Read("Views/CustomerToCustomerPayments/Edit.cshtml");
        foreach (var source in new[] { create, edit })
        {
            Assert.Contains("/Views/CustomerToCustomerPayments/_CreateForm.cshtml", source);
            Assert.Matches("<link[^>]+82-customer-transfer\\.css[^>]+data-ptg-page-asset=\"customer-transfer-style\"", source);
            Assert.Matches("<script[^>]+customer-transfer\\.js[^>]+data-ptg-page-asset=\"customer-transfer-script\"", source);
        }

        var form = Read("Views/CustomerToCustomerPayments/_CreateForm.cshtml");
        foreach (var field in new[] { "PayeeCustomerId", "PayerCustomerId", "Amount", "Currency", "PaymentDate", "Reference", "Description" })
            Assert.Contains($"asp-for=\"{field}\"", form);
        Assert.Contains("@Html.AntiForgeryToken()", form);
    }

    [Fact]
    public void Embedded_Forms_Load_The_Same_Final_Content_Layers_In_Order()
    {
        foreach (var layout in new[] { "Views/Shared/_Layout.cshtml", "Views/Shared/_ModalLayout.cshtml" })
        {
            var source = Read(layout);
            var previous = -1;
            foreach (var asset in new[] { "01-tokens.css", "50-ak-components.css", "71-typography.css", "73-detail-system.css", "76-form-system.css", "78-mashal-reference.css" })
            {
                var current = source.IndexOf(asset, StringComparison.Ordinal);
                Assert.True(current > previous, $"{asset} must follow the preceding layer in {layout}");
                previous = current;
            }
            Assert.Contains("mashal-ui", source);
            Assert.Contains("id=\"ptg-main-content\"", source);
        }
    }

    [Fact]
    public void Content_Direction_Uses_Language_And_Numbers_Remain_Isolated()
    {
        var root = Path.Combine(Repo(), "src", "PTGOilSystem.Web", "Views");
        var offenders = Directory.EnumerateFiles(root, "*.cshtml", SearchOption.AllDirectories)
            .Where(file => Regex.IsMatch(File.ReadAllText(file), "\\bdir=\"rtl\""))
            .Select(file => Path.GetRelativePath(root, file)).ToArray();
        Assert.Empty(offenders);
        var type = Read("wwwroot/css/ptg/71-typography.css");
        Assert.Contains("direction: ltr", type);
        Assert.Contains("unicode-bidi: isolate", type);
    }

    private static string Read(string path) => File.ReadAllText(Path.Combine(Repo(), "src", "PTGOilSystem.Web", path.Replace('/', Path.DirectorySeparatorChar)));

    private static string Repo()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
            if (Directory.Exists(Path.Combine(directory.FullName, "src", "PTGOilSystem.Web"))) return directory.FullName;
        throw new DirectoryNotFoundException("Repository root not found.");
    }
}
