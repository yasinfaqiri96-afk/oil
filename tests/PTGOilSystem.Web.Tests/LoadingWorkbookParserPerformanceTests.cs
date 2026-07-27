using System.Diagnostics;
using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using PTGOilSystem.Web.Helpers;
using PTGOilSystem.Web.Models.Entities;
using Xunit;
using Xunit.Abstractions;

namespace PTGOilSystem.Web.Tests;

public sealed class LoadingWorkbookParserPerformanceTests
{
    private readonly ITestOutputHelper _output;

    public LoadingWorkbookParserPerformanceTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void Parse_Large_SharedString_Workbook_Returns_All_Rows()
    {
        const int rowCount = 3000;
        using var workbook = BuildWagonWorkbook(rowCount);

        var stopwatch = Stopwatch.StartNew();
        var result = LoadingWorkbookParser.Parse(workbook);
        stopwatch.Stop();

        _output.WriteLine(
            $"Loading workbook parser: rows={rowCount}, elapsed_ms={stopwatch.Elapsed.TotalMilliseconds:F0}");

        Assert.Equal(LoadingTransportType.Wagon, result.TransportType);
        Assert.Equal(rowCount, result.Rows.Count);
        Assert.Equal("RWB-003000", result.Rows[^1].BillOfLadingNumber);
        Assert.Equal("WG-003000", result.Rows[^1].WagonNumber);
    }

    private static MemoryStream BuildWagonWorkbook(int rowCount)
    {
        var stream = new MemoryStream();
        using (var document = SpreadsheetDocument.Create(stream, SpreadsheetDocumentType.Workbook, true))
        {
            var workbookPart = document.AddWorkbookPart();
            workbookPart.Workbook = new Workbook();

            var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
            var sheetData = new SheetData();
            worksheetPart.Worksheet = new Worksheet(sheetData);

            var sharedStringPart = workbookPart.AddNewPart<SharedStringTablePart>();
            var sharedStrings = new SharedStringTable();
            sharedStringPart.SharedStringTable = sharedStrings;

            uint AddSharedString(string value)
            {
                sharedStrings.AppendChild(new SharedStringItem(new Text(value)));
                return (uint)(sharedStrings.ChildElements.Count - 1);
            }

            Cell SharedCell(string reference, string value) => new()
            {
                CellReference = reference,
                DataType = CellValues.SharedString,
                CellValue = new CellValue(AddSharedString(value).ToString(CultureInfo.InvariantCulture))
            };

            sheetData.Append(new Row(
                SharedCell("A1", "Date"),
                SharedCell("B1", "RWB No"),
                SharedCell("C1", "Wagon No"),
                SharedCell("D1", "Loaded quantity (MT)"))
            {
                RowIndex = 1
            });

            for (var index = 1; index <= rowCount; index++)
            {
                var excelRow = (uint)(index + 1);
                sheetData.Append(new Row(
                    SharedCell($"A{excelRow}", "2026-07-22"),
                    SharedCell($"B{excelRow}", $"RWB-{index:D6}"),
                    SharedCell($"C{excelRow}", $"WG-{index:D6}"),
                    new Cell
                    {
                        CellReference = $"D{excelRow}",
                        CellValue = new CellValue("35.5")
                    })
                {
                    RowIndex = excelRow
                });
            }

            sharedStrings.Count = (uint)sharedStrings.ChildElements.Count;
            sharedStrings.UniqueCount = sharedStrings.Count;

            var sheets = workbookPart.Workbook.AppendChild(new Sheets());
            sheets.Append(new Sheet
            {
                Id = workbookPart.GetIdOfPart(worksheetPart),
                SheetId = 1,
                Name = "Loading"
            });

            sharedStrings.Save();
            workbookPart.Workbook.Save();
        }

        stream.Position = 0;
        return stream;
    }
}
