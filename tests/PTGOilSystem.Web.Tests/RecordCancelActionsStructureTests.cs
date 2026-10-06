using System.Runtime.CompilerServices;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// Expenses, losses and sales can be cancelled next to Edit, from the list row and
/// from the record page. Cancelled rows leave these lists because their Index queries
/// skip cancelled records. A customs-generated expense is cancelled only through its
/// declaration, so the declaration and its expenses reverse together.
/// </summary>
public sealed class RecordCancelActionsStructureTests
{
    [Fact]
    public void Expense_Cancel_Is_Offered_And_Customs_Expense_Stays_Owned_By_Declaration()
    {
        var controller = ReadRepoFile("src/PTGOilSystem.Web/Controllers/ExpensesController.cs");
        var details = ReadRepoFile("src/PTGOilSystem.Web/Views/Expenses/Details.cshtml");
        var index = ReadRepoFile("src/PTGOilSystem.Web/Views/Expenses/Index.cshtml");

        Assert.Contains("if (expense.CustomsDeclarationId.HasValue)", controller);
        Assert.Contains("query = query.Where(e => !e.IsCancelled);", controller);
        Assert.Contains("PostUrl = Url.Action(\"Cancel\"", details);
        Assert.Contains("!Model.IsCancelled && !Model.CustomsDeclarationId.HasValue", details);
        Assert.Contains("asp-action=\"Cancel\"", index);
        Assert.Contains("@if (!item.CustomsDeclarationId.HasValue)", index);
        Assert.Contains("asp-action=\"Edit\"", index);
    }

    [Fact]
    public void Loss_And_Sale_Cancel_Are_Offered_Next_To_Edit()
    {
        var lossDetails = ReadRepoFile("src/PTGOilSystem.Web/Views/LossEvents/Details.cshtml");
        var lossIndex = ReadRepoFile("src/PTGOilSystem.Web/Views/LossEvents/Index.cshtml");
        var salesIndex = ReadRepoFile("src/PTGOilSystem.Web/Views/Sales/Index.cshtml");

        Assert.Contains("PostUrl = Url.Action(\"Cancel\"", lossDetails);
        Assert.Contains("if (!Model.IsCancelled)", lossDetails);
        Assert.Contains("asp-action=\"Cancel\"", lossIndex);
        Assert.Contains("asp-action=\"Edit\"", lossIndex);
        // Sale cancellation needs a mandatory reason, so the row opens the existing
        // cancel / correct page instead of posting directly.
        Assert.Contains("asp-action=\"Correct\"", salesIndex);
        Assert.Contains("asp-action=\"Edit\"", salesIndex);
    }

    private static string ReadRepoFile(string relativePath, [CallerFilePath] string sourceFilePath = "")
    {
        var normalizedPath = relativePath.Replace('/', Path.DirectorySeparatorChar);
        var directory = new DirectoryInfo(Path.GetDirectoryName(sourceFilePath)!);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, normalizedPath);
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            directory = directory.Parent;
        }

        throw new FileNotFoundException(relativePath);
    }
}
