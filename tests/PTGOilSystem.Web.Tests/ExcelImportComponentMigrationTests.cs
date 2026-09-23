using Xunit;

namespace PTGOilSystem.Web.Tests;

public sealed class ExcelImportComponentMigrationTests
{
    private static readonly string WebRoot = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        "..", "..", "..", "..", "..",
        "src", "PTGOilSystem.Web"));

    // فرم ثبت بارگیری عمداً از این کامپوننت استفاده نمی‌کند: امپورتش پنل و مرحلهٔ جدا ندارد و
    // سطرها مستقیم داخل جدول خود فرم می‌نشینند.
    [Theory]
    [InlineData("Views/Expenses/Import.cshtml")]
    [InlineData("Views/CustomsDeclarations/ImportExcel.cshtml")]
    [InlineData("Views/InventoryTransportLegs/CreateFromInventory.cshtml")]
    [InlineData("Views/InventoryTransportLegs/GroupTransfer.cshtml")]
    [InlineData("Views/TruckSettlements/Index.cshtml")]
    public void EveryExcelFileImport_UsesTheSharedComponent(string relativePath)
    {
        var content = File.ReadAllText(Path.Combine(WebRoot, relativePath));

        Assert.Contains("~/Views/Shared/ExcelImport/_ExcelImport.cshtml", content);
    }

    // امپورت فرم ثبت بارگیری همان کامپوننت مشترک است در حالت external: خودِ فرم فایل را
    // به LoadingController.ImportWorkbook می‌فرستد و سطرها را به جدول همین فرم اضافه می‌کند.
    [Fact]
    public void LoadingCreate_ImportsWithASingleToolbarButton()
    {
        var content = File.ReadAllText(Path.Combine(WebRoot, "Views/Loading/Create.cshtml"));

        Assert.Contains("~/Views/Shared/ExcelImport/_ExcelImport.cshtml", content);
        Assert.Contains("Mode = \"external\"", content);
        Assert.Contains("Url.Action(\"ImportWorkbook\", \"Loading\")", content);
        Assert.Contains("data-loading-import-summary", content);
    }

    [Fact]
    public void ExcelFileHook_ExistsOnlyInsideTheSharedUploader()
    {
        var matches = Directory.EnumerateFiles(Path.Combine(WebRoot, "Views"), "*.cshtml", SearchOption.AllDirectories)
            .Where(path => File.ReadAllText(path).Contains("data-excel-file", StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(WebRoot, path).Replace('\\', '/'))
            .ToList();

        Assert.Equal(["Views/Shared/ExcelImport/_ExcelImportUploader.cshtml"], matches);
    }
}
