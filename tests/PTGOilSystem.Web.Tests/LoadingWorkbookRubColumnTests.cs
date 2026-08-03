using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using PTGOilSystem.Web.Helpers;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// فایل‌های واقعی بارگیری ستون روبل را «Fix price in rub» و مجموعش را فقط «Total» می‌نویسند.
/// اگر این دو ستون خوانده نشوند، نرخ روبل/دالر از فایل مشتق نمی‌شود و ثبت با پیام
/// «نرخ RUB همین بارگیری الزامی است» رد می‌شود.
/// </summary>
public sealed class LoadingWorkbookRubColumnTests
{
    [Fact]
    public void Parse_Reads_Rub_Unit_Price_And_Its_Total_From_Real_Header_Shape()
    {
        using var workbook = BuildWagonWorkbook();

        var result = LoadingWorkbookParser.Parse(workbook);

        var row = Assert.Single(result.Rows);
        Assert.Equal(60.85m, row.LoadedQuantityMt);
        Assert.Equal(48581.26m, row.SettlementUnitPriceRub);
        Assert.Equal(2956169.671m, row.SettlementValueRub);
        // ستون «Total» دلاری نباید به‌عنوان مجموع روبلی خوانده شود.
        Assert.NotEqual(32285.24m, row.SettlementValueRub);
        Assert.Equal(530.5710m, decimal.Round(row.LoadingPriceUsd!.Value, 4, MidpointRounding.AwayFromZero));
    }

    private static MemoryStream BuildWagonWorkbook()
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

            static Cell NumberCell(string reference, string value) => new()
            {
                CellReference = reference,
                CellValue = new CellValue(value)
            };

            sheetData.Append(new Row(
                SharedCell("A1", "№"),
                SharedCell("B1", "Date"),
                SharedCell("C1", "RWB No"),
                SharedCell("D1", "Wagons No "),
                SharedCell("E1", "Loaded quantity (MT)"),
                SharedCell("F1", "Price in  December 01.12.25-31.12.25 $"),
                SharedCell("G1", "Total"),
                SharedCell("H1", "Fix price in rub "),
                SharedCell("I1", "Total"),
                SharedCell("J1", "Consignee"),
                SharedCell("K1", "Destination"))
            {
                RowIndex = 1
            });

            sheetData.Append(new Row(
                NumberCell("A2", "1"),
                SharedCell("B2", "2025-12-28"),
                SharedCell("C2", "9109855"),
                SharedCell("D2", "50035534"),
                NumberCell("E2", "60.85"),
                NumberCell("F2", "530.57095238095235"),
                NumberCell("G2", "32285.24245238095"),
                NumberCell("H2", "48581.26"),
                NumberCell("I2", "2956169.671"),
                SharedCell("J2", "Terminal Ilinka"),
                SharedCell("K2", "Barbarov"))
            {
                RowIndex = 2
            });

            sharedStrings.Count = (uint)sharedStrings.ChildElements.Count;
            sharedStrings.UniqueCount = sharedStrings.Count;

            var sheets = workbookPart.Workbook.AppendChild(new Sheets());
            sheets.Append(new Sheet
            {
                Id = workbookPart.GetIdOfPart(worksheetPart),
                SheetId = 1,
                Name = "Sheet1"
            });

            sharedStrings.Save();
            workbookPart.Workbook.Save();
        }

        stream.Position = 0;
        return stream;
    }
}
