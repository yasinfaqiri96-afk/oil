using System.Globalization;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using ExcelDataReader;
using Microsoft.AspNetCore.Http;

namespace PTGOilSystem.Web.Helpers;

public static class ExcelWorkbookNormalizer
{
    public static async Task<NormalizedExcelFile> OpenAsync(IFormFile file, CancellationToken cancellationToken = default)
    {
        var extension = Path.GetExtension(file.FileName);
        if (string.Equals(extension, ".xlsx", StringComparison.OrdinalIgnoreCase))
        {
            return new NormalizedExcelFile(file.OpenReadStream(), null);
        }

        if (!string.Equals(extension, ".xls", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("فقط فایل‌های .xlsx و .xls قابل قبول است.");
        }

        var sourcePath = Path.Combine(Path.GetTempPath(), $"ptg-excel-source-{Guid.NewGuid():N}.xls");
        string? normalizedPath = null;
        try
        {
            await using (var output = new FileStream(sourcePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, true))
            {
                await file.CopyToAsync(output, cancellationToken);
            }

            normalizedPath = NormalizeToOpenXml(sourcePath, extension, cancellationToken);
            return new NormalizedExcelFile(
                new FileStream(normalizedPath, FileMode.Open, FileAccess.Read, FileShare.Read),
                normalizedPath);
        }
        catch
        {
            TryDelete(normalizedPath);
            throw;
        }
        finally
        {
            TryDelete(sourcePath);
        }
    }

    public static string NormalizeToOpenXml(string sourcePath, string extension, CancellationToken cancellationToken)
    {
        if (string.Equals(extension, ".xlsx", StringComparison.OrdinalIgnoreCase))
        {
            return sourcePath;
        }

        if (!string.Equals(extension, ".xls", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("فقط فایل‌های .xlsx و .xls قابل قبول است.");
        }

        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var targetPath = Path.Combine(Path.GetTempPath(), $"ptg-excel-{Guid.NewGuid():N}.xlsx");

        try
        {
            using var input = File.Open(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = ExcelReaderFactory.CreateReader(input);
            using var document = SpreadsheetDocument.Create(targetPath, SpreadsheetDocumentType.Workbook);
            var workbookPart = document.AddWorkbookPart();
            workbookPart.Workbook = new Workbook();
            var sheets = workbookPart.Workbook.AppendChild(new Sheets());
            uint sheetId = 1;

            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
                var sheetData = new SheetData();
                worksheetPart.Worksheet = new Worksheet(sheetData);
                uint rowIndex = 1;

                while (reader.Read())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var row = new Row { RowIndex = rowIndex };
                    for (var columnIndex = 0; columnIndex < reader.FieldCount; columnIndex++)
                    {
                        var value = reader.GetValue(columnIndex);
                        if (value is null)
                        {
                            continue;
                        }

                        row.Append(CreateCell(value, columnIndex, rowIndex));
                    }

                    sheetData.Append(row);
                    rowIndex++;
                }

                var currentSheetId = sheetId++;
                sheets.Append(new Sheet
                {
                    Id = workbookPart.GetIdOfPart(worksheetPart),
                    SheetId = currentSheetId,
                    Name = SanitizeSheetName(reader.Name, currentSheetId)
                });
            }
            while (reader.NextResult());

            workbookPart.Workbook.Save();
            return targetPath;
        }
        catch
        {
            TryDelete(targetPath);
            throw;
        }
    }

    private static Cell CreateCell(object value, int columnIndex, uint rowIndex)
    {
        var cell = new Cell { CellReference = $"{ColumnName(columnIndex + 1)}{rowIndex}" };
        switch (value)
        {
            case DateTime date:
                cell.DataType = CellValues.InlineString;
                cell.InlineString = new InlineString(new Text(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
                break;
            case bool boolean:
                cell.DataType = CellValues.Boolean;
                cell.CellValue = new CellValue(boolean ? "1" : "0");
                break;
            case byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal:
                cell.DataType = CellValues.Number;
                cell.CellValue = new CellValue(Convert.ToString(value, CultureInfo.InvariantCulture)!);
                break;
            default:
                cell.DataType = CellValues.InlineString;
                cell.InlineString = new InlineString(new Text(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty));
                break;
        }

        return cell;
    }

    private static string ColumnName(int columnNumber)
    {
        var name = string.Empty;
        while (columnNumber > 0)
        {
            columnNumber--;
            name = (char)('A' + columnNumber % 26) + name;
            columnNumber /= 26;
        }

        return name;
    }

    private static string SanitizeSheetName(string? value, uint fallback)
    {
        var name = string.IsNullOrWhiteSpace(value) ? $"Sheet{fallback}" : value.Trim();
        foreach (var invalid in new[] { ':', '\\', '/', '?', '*', '[', ']' })
        {
            name = name.Replace(invalid, '-');
        }

        return name.Length <= 31 ? name : name[..31];
    }

    public static void TryDelete(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch
        {
            // Temporary files are also cleaned by the operating system.
        }
    }
}

public sealed class NormalizedExcelFile : IAsyncDisposable
{
    private readonly string? _temporaryPath;

    internal NormalizedExcelFile(Stream stream, string? temporaryPath)
    {
        Stream = stream;
        _temporaryPath = temporaryPath;
    }

    public Stream Stream { get; }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await Stream.DisposeAsync();
        }
        finally
        {
            ExcelWorkbookNormalizer.TryDelete(_temporaryPath);
        }
    }
}
