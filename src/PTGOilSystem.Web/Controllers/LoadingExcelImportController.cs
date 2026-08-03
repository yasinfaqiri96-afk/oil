using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Helpers;
using PTGOilSystem.Web.Models.ExcelImport;
using PTGOilSystem.Web.Models.Loading;
using PTGOilSystem.Web.Security;

namespace PTGOilSystem.Web.Controllers;

/// <summary>
/// امپورت اکسل بارگیری هیچ Job، مرحله یا صفحهٔ جداگانه‌ای ندارد؛ خواندن فایل با
/// <see cref="LoadingController.ImportWorkbook"/> انجام می‌شود و سطرها مستقیم داخل فرم می‌نشینند.
/// این کنترلر فقط فایل نمونه و قاعدهٔ تشخیص بارگیری تکراری را نگه می‌دارد.
/// </summary>
[Authorize(Policy = AuthPolicies.ManageData)]
[Route("Loading/ExcelImport")]
public sealed class LoadingExcelImportController : Controller
{
    [HttpGet("sample")]
    public IActionResult DownloadSample()
    {
        using var stream = new MemoryStream();
        using (var document = SpreadsheetDocument.Create(stream, DocumentFormat.OpenXml.SpreadsheetDocumentType.Workbook, true))
        {
            var workbookPart = document.AddWorkbookPart();
            workbookPart.Workbook = new Workbook();
            var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
            var sheetData = new SheetData();
            worksheetPart.Worksheet = new Worksheet(sheetData);
            sheetData.Append(CreateSampleRow(1, "Date", "CMR", "Trucks", "Loaded quantity (MT)", "Consignee", "Destination"));
            sheetData.Append(CreateSampleRow(2, DateTime.UtcNow.ToString("yyyy-MM-dd"), "CMR-001", "ABC-123", "25.5", "نمونه گیرنده", "نمونه مقصد"));
            var sheets = workbookPart.Workbook.AppendChild(new Sheets());
            sheets.Append(new Sheet { Id = workbookPart.GetIdOfPart(worksheetPart), SheetId = 1, Name = "Loading" });
            workbookPart.Workbook.Save();
        }

        return File(stream.ToArray(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "loading-import-sample.xlsx");
    }

    /// <summary>
    /// هر ردیف فایل را با بارگیری‌های موجودِ همان قرارداد و با ردیف‌های قبلی همان فایل مقایسه می‌کند.
    /// ردیف تکراری یا «دارای اختلاف» در AcceptedRows نمی‌آید و پیام مربوط به آن در Issues برمی‌گردد.
    /// شمارهٔ سند یکسان در قرارداد دیگر تکراری نیست، چون ContractId جزء کلید است.
    /// </summary>
    public static async Task<ImportScreeningResult> ScreenDuplicateRowsAsync(
        ApplicationDbContext db,
        LoadingCreateViewModel model,
        CancellationToken token)
    {
        var keysByRowIndex = new Dictionary<int, string>();
        var occurrences = new LoadingImportKey.OccurrenceTracker();
        for (var index = 0; index < model.Rows.Count; index++)
        {
            var row = model.Rows[index];
            var contractId = row.ContractId ?? model.ContractId;
            if (contractId <= 0)
            {
                continue;
            }

            var key = LoadingImportKey.Build(
                contractId,
                row.BillOfLadingNumber,
                row.WagonNumber ?? row.ImportedTransportReference,
                row.LoadingDate,
                occurrences,
                row.LoadedQuantityMt);
            if (key is not null)
            {
                keysByRowIndex[index] = key;
            }
        }

        var keys = keysByRowIndex.Values.Distinct().ToList();
        var existing = keys.Count == 0
            ? []
            : await db.LoadingRegisters
                .AsNoTracking()
                .Where(l => l.ImportUniqueKey != null && keys.Contains(l.ImportUniqueKey))
                .Select(l => new { Key = l.ImportUniqueKey!, l.LoadedQuantityMt, l.LoadingPriceUsd })
                .ToListAsync(token);

        var existingByKey = existing
            .GroupBy(l => l.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        var acceptedRows = new List<LoadingCreateRowViewModel>(model.Rows.Count);
        var issues = new List<ExcelImportIssue>();
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        var duplicateRows = 0;
        var conflictRows = 0;

        for (var index = 0; index < model.Rows.Count; index++)
        {
            var row = model.Rows[index];
            if (!keysByRowIndex.TryGetValue(index, out var key))
            {
                acceptedRows.Add(row);
                continue;
            }

            if (existingByKey.TryGetValue(key, out var stored))
            {
                var sameValues = LoadingImportKey.ValuesMatch(stored.LoadedQuantityMt, row.LoadedQuantityMt)
                    && LoadingImportKey.ValuesMatch(stored.LoadingPriceUsd, row.LoadingPriceUsd);
                if (sameValues)
                {
                    duplicateRows++;
                    issues.Add(new ExcelImportIssue(
                        index + 2,
                        "مرجع حمل",
                        "این بارگیری قبلاً در همین قرارداد ثبت شده است؛ این سطر را حذف کنید.",
                        "warning"));
                }
                else
                {
                    conflictRows++;
                    issues.Add(new ExcelImportIssue(
                        index + 2,
                        "مرجع حمل",
                        $"شمارهٔ سند تکراری است ولی مقدار/قیمت فرق دارد (ثبت‌شده: {LoadingImportKey.Describe(stored.LoadedQuantityMt)} MT / {LoadingImportKey.Describe(stored.LoadingPriceUsd)} USD — فایل: {LoadingImportKey.Describe(row.LoadedQuantityMt)} MT / {LoadingImportKey.Describe(row.LoadingPriceUsd)} USD). این سطر را اصلاح یا حذف کنید.",
                        "warning"));
                }

                continue;
            }

            if (!seenKeys.Add(key))
            {
                duplicateRows++;
                issues.Add(new ExcelImportIssue(
                    index + 2,
                    "مرجع حمل",
                    "این ردیف در همین فایل تکرار شده است؛ یکی از آن‌ها را حذف کنید.",
                    "warning"));
                continue;
            }

            acceptedRows.Add(row);
        }

        return new ImportScreeningResult(acceptedRows, issues, duplicateRows, conflictRows);
    }

    public sealed record ImportScreeningResult(
        List<LoadingCreateRowViewModel> AcceptedRows,
        List<ExcelImportIssue> Issues,
        int DuplicateRows,
        int ConflictRows);

    private static Row CreateSampleRow(uint rowIndex, params string[] values)
    {
        var row = new Row { RowIndex = rowIndex };
        for (var index = 0; index < values.Length; index++)
        {
            row.Append(new Cell
            {
                CellReference = $"{(char)('A' + index)}{rowIndex}",
                DataType = CellValues.InlineString,
                InlineString = new InlineString(new Text(values[index]))
            });
        }

        return row;
    }
}
