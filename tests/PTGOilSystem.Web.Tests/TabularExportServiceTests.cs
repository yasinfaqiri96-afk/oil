using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using PTGOilSystem.Web.Services.Exports;
using System.Runtime.CompilerServices;
using Xunit;

namespace PTGOilSystem.Web.Tests;

public sealed class TabularExportServiceTests
{
    [Fact]
    public async Task Excel_Uses_Rtl_Typed_Cells_Filters_And_Formula_Injection_Protection()
    {
        var service = CreateService(excelMaxRows: 10, pdfMaxRows: 10);
        var document = BuildDocument();
        await using var stream = new MemoryStream();

        await service.WriteAsync(document, TabularExportFormat.Excel, isEnglish: false, stream, CancellationToken.None);
        stream.Position = 0;

        using var workbook = SpreadsheetDocument.Open(stream, false);
        var workbookPart = Assert.IsType<WorkbookPart>(workbook.WorkbookPart);
        var worksheetPart = workbookPart.WorksheetParts.Single();
        var worksheet = worksheetPart.Worksheet;
        Assert.True(worksheet.GetFirstChild<SheetViews>()!.Elements<SheetView>().Single().RightToLeft!.Value);
        Assert.NotNull(worksheet.GetFirstChild<AutoFilter>());

        var cells = worksheet.Descendants<Cell>().ToList();
        Assert.Contains(cells, cell => cell.DataType?.Value == CellValues.Number && cell.CellValue?.Text == "1250.5");
        Assert.Contains(cells, cell => cell.InlineString?.InnerText == "'=SUM(A1:A2)");
    }

    [Fact]
    public async Task Pdf_Is_Searchable_Pdf_And_Supports_Persian_Rtl_Content()
    {
        var service = CreateService(excelMaxRows: 10, pdfMaxRows: 10);
        await using var stream = new MemoryStream();

        await service.WriteAsync(BuildDocument(), TabularExportFormat.Pdf, isEnglish: false, stream, CancellationToken.None);

        var bytes = stream.ToArray();
        Assert.True(bytes.Length > 1_000);
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));
    }

    [Fact]
    public async Task English_Excel_Is_Left_To_Right_And_Preserves_An_Empty_Table()
    {
        var service = CreateService(excelMaxRows: 10, pdfMaxRows: 10);
        var document = BuildDocument();
        document = new TabularExportDocument
        {
            FileNameStem = document.FileNameStem,
            TitleFa = document.TitleFa,
            TitleEn = document.TitleEn,
            Columns = document.Columns,
            Rows = [],
            KnownRowCount = 0
        };
        await using var stream = new MemoryStream();

        await service.WriteAsync(document, TabularExportFormat.Excel, isEnglish: true, stream, CancellationToken.None);
        stream.Position = 0;
        using var workbook = SpreadsheetDocument.Open(stream, false);
        var worksheet = workbook.WorkbookPart!.WorksheetParts.Single().Worksheet;

        Assert.False(worksheet.GetFirstChild<SheetViews>()!.Elements<SheetView>().Single().RightToLeft!.Value);
        Assert.NotNull(worksheet.GetFirstChild<AutoFilter>());
    }

    [Fact]
    public async Task English_Pdf_And_Long_Multipage_Persian_Pdf_Are_Generated()
    {
        var service = CreateService(excelMaxRows: 500, pdfMaxRows: 500);
        await using var english = new MemoryStream();
        await service.WriteAsync(BuildDocument(), TabularExportFormat.Pdf, isEnglish: true, english, CancellationToken.None);

        var source = BuildDocument();
        var longDocument = new TabularExportDocument
        {
            FileNameStem = "PTG_Long_Persian",
            TitleFa = source.TitleFa,
            TitleEn = source.TitleEn,
            Columns = source.Columns,
            KnownRowCount = 180,
            Rows = Enumerable.Range(1, 180).Select(index => new TabularExportRow(
            [
                TabularExportCell.Date(new DateTime(2026, 7, 1).AddDays(index % 17)),
                TabularExportCell.Text($"شرح فارسی طولانی ردیف {index} برای بررسی اتصال حروف و شکست درست متن"),
                TabularExportCell.Number(index * 12.75m)
            ]))
        };
        await using var persian = new MemoryStream();
        await service.WriteAsync(longDocument, TabularExportFormat.Pdf, isEnglish: false, persian, CancellationToken.None);

        Assert.True(english.Length > 1_000);
        Assert.True(persian.Length > english.Length);
    }

    [Fact]
    public async Task Export_Observes_Cancellation()
    {
        var service = CreateService(excelMaxRows: 10, pdfMaxRows: 10);
        await using var stream = new MemoryStream();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.WriteAsync(BuildDocument(), TabularExportFormat.Excel, isEnglish: false, stream, cancellation.Token));
    }

    [Fact]
    public async Task Export_Rejects_Rows_Above_The_Configured_Limit()
    {
        var service = CreateService(excelMaxRows: 1, pdfMaxRows: 1);
        await using var stream = new MemoryStream();

        var exception = await Assert.ThrowsAsync<TabularExportLimitException>(() =>
            service.WriteAsync(BuildDocument(), TabularExportFormat.Excel, isEnglish: true, stream, CancellationToken.None));

        Assert.Equal(2, exception.ActualRows);
        Assert.Equal(1, exception.MaximumRows);
    }

    [Fact]
    public async Task Reference_Samples_Can_Be_Written_For_Visual_Inspection()
    {
        var sampleDirectory = Environment.GetEnvironmentVariable("PTG_EXPORT_SAMPLE_DIR");
        if (string.IsNullOrWhiteSpace(sampleDirectory))
            return;

        Directory.CreateDirectory(sampleDirectory);
        var service = CreateService(excelMaxRows: 500, pdfMaxRows: 500);
        foreach (var language in new[] { (Code: "fa", IsEnglish: false), (Code: "en", IsEnglish: true) })
        {
            foreach (var format in new[] { TabularExportFormat.Excel, TabularExportFormat.Pdf })
            {
                var extension = format == TabularExportFormat.Excel ? "xlsx" : "pdf";
                await using var output = File.Create(Path.Combine(sampleDirectory, $"PTG_Export_Sample_{language.Code}.{extension}"));
                await service.WriteAsync(BuildDocument(), format, language.IsEnglish, output, CancellationToken.None);
            }
        }
    }

    [Fact]
    public void Views_Do_Not_Contain_Print_Buttons_Or_Print_Handlers()
    {
        var viewsRoot = Path.Combine(Directory.GetParent(FindWebRoot())!.FullName, "Views");
        var forbidden = new[] { "window.print(", "bi-printer", "data-print-list", "data-receipt-print", "Print / Save PDF" };

        foreach (var view in Directory.EnumerateFiles(viewsRoot, "*.cshtml", SearchOption.AllDirectories))
        {
            var content = File.ReadAllText(view);
            foreach (var token in forbidden)
                Assert.DoesNotContain(token, content, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Every_Operations_Index_Uses_Exactly_One_Shared_Export_Menu()
    {
        var viewsRoot = Path.Combine(Directory.GetParent(FindWebRoot())!.FullName, "Views");
        var operationControllers = new[]
        {
            "Loading", "InventoryTransportLegs", "ShipmentPnl", "Dispatch", "TruckSettlements",
            "Expenses", "LossEvents", "LoadingReceipts", "CustomsDeclarations", "Sales"
        };

        foreach (var controller in operationControllers)
        {
            var content = File.ReadAllText(Path.Combine(viewsRoot, controller, "Index.cshtml"));
            Assert.Equal(1, CountOccurrences(content, "_ExportMenu.cshtml"));
        }
    }

    [Fact]
    public async Task Workbook_Excel_Writes_Every_Sheet_With_Unique_Names()
    {
        var service = CreateService(excelMaxRows: 500, pdfMaxRows: 500);
        var first = BuildDocument();
        // عمداً هم‌عنوان تا یکتاسازیِ نام شیت آزموده شود.
        var second = new TabularExportDocument
        {
            FileNameStem = first.FileNameStem,
            TitleFa = first.TitleFa,
            TitleEn = first.TitleEn,
            Columns = first.Columns,
            Rows = first.Rows,
            KnownRowCount = 2
        };
        await using var stream = new MemoryStream();

        await service.WriteWorkbookAsync([first, second], TabularExportFormat.Excel, isEnglish: false, stream, CancellationToken.None);
        stream.Position = 0;

        using var workbook = SpreadsheetDocument.Open(stream, false);
        var sheets = workbook.WorkbookPart!.Workbook.Sheets!.Elements<Sheet>().ToList();
        Assert.Equal(2, sheets.Count);
        Assert.Equal(2, workbook.WorkbookPart!.WorksheetParts.Count());
        Assert.Equal(sheets.Count, sheets.Select(sheet => sheet.Name!.Value).Distinct().Count());
    }

    [Fact]
    public async Task Workbook_Pdf_Is_Searchable_With_Multiple_Sections()
    {
        var service = CreateService(excelMaxRows: 500, pdfMaxRows: 500);
        await using var stream = new MemoryStream();

        await service.WriteWorkbookAsync([BuildDocument(), BuildDocument()], TabularExportFormat.Pdf, isEnglish: false, stream, CancellationToken.None);

        var bytes = stream.ToArray();
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));
        Assert.True(bytes.Length > 1_000);
    }

    [Fact]
    public void Party_Statement_View_Uses_Server_Pdf_For_All_Party_Types()
    {
        var webRoot = FindWebRoot();
        var projectRoot = Directory.GetParent(webRoot)!.FullName;
        var view = File.ReadAllText(Path.Combine(projectRoot, "Views", "PartyStatements", "Document.cshtml"));
        var script = File.ReadAllText(Path.Combine(webRoot, "js", "party-statement.js"));
        var controller = File.ReadAllText(Path.Combine(projectRoot, "Controllers", "PartyStatementsController.cs"));

        Assert.Contains("Url.Action(\"Pdf\", \"PartyStatements\"", view);
        Assert.Contains("PartyStatements/{partyType}/{id:int}/Pdf", controller);
        Assert.DoesNotContain("data-statement-print", view);
        Assert.DoesNotContain("window.print", script);
    }
    private static TabularExportService CreateService(int excelMaxRows, int pdfMaxRows)
    {
        var webRoot = FindWebRoot();
        var environment = new TestWebHostEnvironment
        {
            WebRootPath = webRoot,
            ContentRootPath = Directory.GetParent(webRoot)!.FullName
        };
        var options = Options.Create(new TabularExportOptions
        {
            ExcelMaxRows = excelMaxRows,
            PdfMaxRows = pdfMaxRows,
            QuestPdfLicense = "Community"
        });
        return new TabularExportService(options, environment);
    }

    private static TabularExportDocument BuildDocument()
        => new()
        {
            FileNameStem = "PTG_Export_Test",
            TitleFa = "گزارش آزمایشی",
            TitleEn = "Export Test",
            KnownRowCount = 2,
            Filters = [new("بازه", "Range", "2026-07-01 تا 2026-07-17")],
            Columns =
            [
                new("تاریخ", "Date", TabularExportValueType.Date, 14),
                new("شرح", "Description", TabularExportValueType.Text, 28, true),
                new("مبلغ", "Amount", TabularExportValueType.Number, 16)
            ],
            Rows =
            [
                new([TabularExportCell.Date(new DateTime(2026, 7, 17)), TabularExportCell.Text("دریافت نقدی"), TabularExportCell.Number(1250.5m)]),
                new([TabularExportCell.Date(new DateTime(2026, 7, 18)), TabularExportCell.Text("=SUM(A1:A2)"), TabularExportCell.Number(75m)])
            ]
        };

    private static string FindWebRoot([CallerFilePath] string sourceFilePath = "")
    {
        var current = new DirectoryInfo(Path.GetDirectoryName(sourceFilePath)!);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "src", "PTGOilSystem.Web", "wwwroot");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Repository root was not found from the test output directory.");
    }

    private static int CountOccurrences(string value, string token)
    {
        var count = 0;
        var start = 0;
        while ((start = value.IndexOf(token, start, StringComparison.Ordinal)) >= 0)
        {
            count++;
            start += token.Length;
        }
        return count;
    }

    private sealed class TestWebHostEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "PTGOilSystem.Web.Tests";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = string.Empty;
        public string EnvironmentName { get; set; } = "Development";
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
