using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using Xunit;

namespace PTGOilSystem.Web.Tests;

public sealed class ContractDisplayNameTests
{
    [Fact]
    public void Contract_Model_Requires_200_Character_Name_And_Keeps_Number_Unique()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        using var db = new ApplicationDbContext(options);
        var entity = db.Model.FindEntityType(typeof(Contract));

        Assert.NotNull(entity);
        var name = entity!.FindProperty(nameof(Contract.ContractName));
        Assert.NotNull(name);
        Assert.False(name.IsNullable);
        Assert.Equal(200, name.GetMaxLength());
        Assert.Contains(entity.GetIndexes(), index => index.IsUnique
            && index.Properties.Select(property => property.Name).SequenceEqual([nameof(Contract.ContractNumber)]));
    }

    [Fact]
    public void Migration_Backfills_Legacy_Contracts_Before_Making_Name_Required()
    {
        var migration = ReadRepoFile(
            "src/PTGOilSystem.Web/Migrations/20260802133147_AddContractDisplayName.cs");

        Assert.Contains("nullable: true", migration);
        Assert.Contains("SET \"ContractName\" = \"ContractNumber\"", migration);
        Assert.Contains("migrationBuilder.AlterColumn<string>", migration);
        Assert.Contains("nullable: false", migration);
        Assert.DoesNotContain("DeleteData", migration);
    }

    [Fact]
    public void Contract_Forms_And_Exports_Expose_Name_And_Number()
    {
        var create = ReadRepoFile("src/PTGOilSystem.Web/Views/Contracts/Create.cshtml");
        var edit = ReadRepoFile("src/PTGOilSystem.Web/Views/Contracts/Edit.cshtml");
        var export = ReadRepoFile("src/PTGOilSystem.Web/Controllers/ContractsController.Export.cs");

        Assert.Contains("asp-for=\"ContractName\"", create);
        Assert.Contains("asp-for=\"ContractName\"", edit);
        Assert.Contains("r.ContractName", export);
        Assert.Contains("r.ContractNumber", export);
    }

    [Fact]
    public void Central_Label_Is_Reused_By_Operational_Contract_Selectors()
    {
        var helper = ReadRepoFile("src/PTGOilSystem.Web/Helpers/ContractUiText.cs");
        Assert.Contains("contract.DisplayLabel", helper);
        Assert.Contains("Contract.BuildDisplayLabel", helper);

        string[] selectorControllers =
        [
            "ShipmentsController.cs",
            "ShipmentContractsController.cs",
            "PaymentsController.cs",
            "ExpensesController.cs",
            "InventoryController.cs",
            "LoadingController.cs"
        ];

        foreach (var controller in selectorControllers)
        {
            var source = ReadRepoFile($"src/PTGOilSystem.Web/Controllers/{controller}");
            Assert.Contains("ContractName", source);
            Assert.Contains("ContractUiText", source);
        }

        var partnerDetails = ReadRepoFile("src/PTGOilSystem.Web/Views/Partners/Details.cshtml");
        var shipmentPnlDetails = ReadRepoFile("src/PTGOilSystem.Web/Views/ShipmentPnl/Details.cshtml");
        // فیلتر قرارداد در پروفایل شریک از گزینه‌های سرویس شراکت می‌آید و همان برچسب مرکزی را
        // نشان می‌دهد؛ PartnershipContractOption.ContractLabel با Contract.BuildDisplayLabel ساخته می‌شود.
        Assert.Contains("option.ContractLabel", partnerDetails);
        Assert.Contains("line.DisplayLabel", shipmentPnlDetails);
    }

    private static string ReadRepoFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine(directory.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar)));
    }
}
