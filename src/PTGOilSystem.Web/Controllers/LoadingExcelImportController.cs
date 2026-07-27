using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Security.Claims;
using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Routing;
using PTGOilSystem.Web.Helpers;
using PTGOilSystem.Web.Models.ExcelImport;
using PTGOilSystem.Web.Models.Loading;
using PTGOilSystem.Web.Security;
using PTGOilSystem.Web.Services.Imports;

namespace PTGOilSystem.Web.Controllers;

[Authorize(Policy = AuthPolicies.ManageData)]
[Route("Loading/ExcelImport")]
public sealed class LoadingExcelImportController : Controller
{
    private const long MaxFileSize = 100L * 1024 * 1024;
    private readonly ExcelImportJobCoordinator _jobs;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<LoadingExcelImportController> _logger;

    public LoadingExcelImportController(
        ExcelImportJobCoordinator jobs,
        IServiceScopeFactory scopeFactory,
        ILogger<LoadingExcelImportController> logger)
    {
        _jobs = jobs;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    [HttpPost("start")]
    [ValidateAntiForgeryToken]
    [RequestFormLimits(ValueCountLimit = 20_000, ValueLengthLimit = 104_857_600, MultipartBodyLengthLimit = 115_343_360L)]
    [RequestSizeLimit(115_343_360L)]
    public async Task<IActionResult> Start(LoadingCreateViewModel model, CancellationToken cancellationToken)
    {
        var file = model.ImportWorkbookFile;
        if (file is null || file.Length == 0)
        {
            return BadRequest(new { message = "فایل اکسل را انتخاب کنید." });
        }

        var extension = Path.GetExtension(file.FileName);
        if (!string.Equals(extension, ".xlsx", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(extension, ".xls", StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest(new { message = "فقط فایل‌های Excel با پسوند .xlsx و .xls قابل قبول است." });
        }

        if (file.Length > MaxFileSize)
        {
            return BadRequest(new { message = "حجم فایل بیشتر از حد مجاز ۱۰۰ مگابایت است." });
        }

        var ownerKey = ResolveOwnerKey(User);
        var safeFileName = Path.GetFileName(file.FileName);
        var sourcePath = Path.Combine(Path.GetTempPath(), $"ptg-loading-import-{Guid.NewGuid():N}{extension.ToLowerInvariant()}");
        await using (var output = new FileStream(sourcePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, true))
        {
            await file.CopyToAsync(output, cancellationToken);
        }

        model.ImportWorkbookFile = null;
        var principal = ClonePrincipal(User);
        var jobId = _jobs.Create(ownerKey, safeFileName, file.Length);
        var payload = new LoadingImportPayload(model, principal, sourcePath, extension, safeFileName);
        _jobs.StartValidation(jobId, (context, token) => ValidateAsync(context, payload, token));

        return Accepted(new { jobId });
    }

    [HttpGet("{id:guid}/status")]
    public IActionResult Status(Guid id)
        => _jobs.TryGet(id, ResolveOwnerKey(User), out var snapshot)
            ? Ok(snapshot)
            : NotFound(new { message = "Job موردنظر پیدا نشد یا به این کاربر تعلق ندارد." });

    [HttpPost("{id:guid}/confirm")]
    [ValidateAntiForgeryToken]
    public IActionResult Confirm(Guid id)
    {
        var ownerKey = ResolveOwnerKey(User);
        if (_jobs.GetPayload(id, ownerKey) is not LoadingImportPayload payload)
        {
            return NotFound(new { message = "اطلاعات Import پیدا نشد. فایل را دوباره بارگذاری کنید." });
        }

        return _jobs.TryStartRegistration(id, ownerKey, (context, token) => RegisterAsync(context, payload, token))
            ? Accepted(new { jobId = id })
            : Conflict(new { message = "این فایل هنوز آماده ثبت نیست یا خطای حل‌نشده دارد." });
    }

    [HttpPost("{id:guid}/cancel")]
    [ValidateAntiForgeryToken]
    public IActionResult Cancel(Guid id)
        => _jobs.TryCancel(id, ResolveOwnerKey(User))
            ? Ok(new { message = "درخواست لغو ثبت شد." })
            : Conflict(new { message = "در این مرحله لغو امن نیست." });

    [HttpGet("{id:guid}/errors.csv")]
    public IActionResult DownloadErrors(Guid id)
    {
        if (!_jobs.TryGet(id, ResolveOwnerKey(User), out var snapshot) || snapshot is null)
        {
            return NotFound();
        }

        var csv = new StringBuilder("ردیف,ستون,نوع,پیام\r\n");
        foreach (var issue in snapshot.Issues)
        {
            csv.Append(Csv(issue.Row.ToString(CultureInfo.InvariantCulture))).Append(',')
                .Append(Csv(issue.Column)).Append(',')
                .Append(Csv(issue.Severity)).Append(',')
                .Append(Csv(issue.Message)).Append("\r\n");
        }

        return File(new UTF8Encoding(true).GetBytes(csv.ToString()), "text/csv; charset=utf-8", "loading-import-errors.csv");
    }

    [HttpGet("{id:guid}/report.csv")]
    public IActionResult DownloadReport(Guid id)
    {
        if (!_jobs.TryGet(id, ResolveOwnerKey(User), out var snapshot) || snapshot is null)
        {
            return NotFound();
        }

        var csv = $"فایل,کل ردیف,ثبت‌شده,ردشده,جدید,به‌روزشده,زمان ثانیه\r\n" +
                  $"{Csv(snapshot.FileName)},{snapshot.TotalRows},{snapshot.CreatedRows},{snapshot.RejectedRows},{snapshot.CreatedRows},{snapshot.UpdatedRows},{snapshot.ElapsedSeconds}\r\n";
        return File(new UTF8Encoding(true).GetBytes(csv), "text/csv; charset=utf-8", "loading-import-report.csv");
    }

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

    private async Task ValidateAsync(ExcelImportJobContext context, LoadingImportPayload payload, CancellationToken token)
    {
        string? normalizedPath = null;
        try
        {
            context.Update(ExcelImportJobStage.Structure, "در حال خواندن فایل اکسل…");
            normalizedPath = ExcelWorkbookNormalizer.NormalizeToOpenXml(payload.SourcePath, payload.Extension, token);
            var sheetCount = CountSheets(normalizedPath);
            context.Update(ExcelImportJobStage.Structure, "در حال بررسی ستون‌ها…", sheetCount: sheetCount);

            await using var scope = _scopeFactory.CreateAsyncScope();
            await using var fileStream = new FileStream(normalizedPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            payload.Model.ImportWorkbookFile = new FormFile(fileStream, 0, fileStream.Length, nameof(payload.Model.ImportWorkbookFile), Path.ChangeExtension(payload.FileName, ".xlsx"))
            {
                Headers = new HeaderDictionary(),
                ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"
            };

            var controller = CreateLoadingController(scope.ServiceProvider, payload.Principal, out var accessor);
            try
            {
                var result = await controller.ImportWorkbook(payload.Model);
                var imported = (result as ViewResult)?.Model as LoadingCreateViewModel ?? payload.Model;
                var issues = ReadModelStateIssues(controller, imported.Rows);

                context.Update(ExcelImportJobStage.Validation, $"در حال اعتبارسنجی {imported.Rows.Count} ردیف…", 0, imported.Rows.Count, sheetCount);
                issues.AddRange(ValidateRows(imported.Rows, context));
                var duplicates = FindDuplicateRows(imported.Rows);
                issues.AddRange(duplicates.Select(row => new ExcelImportIssue(row + 1, "مرجع حمل", "این ردیف در همین فایل تکرار شده است.", "warning")));

                var hasGlobalError = issues.Any(issue => issue.Severity == "error" && issue.Row <= 0);
                var errorRows = hasGlobalError
                    ? Math.Max(imported.Rows.Count, 1)
                    : issues.Where(issue => issue.Severity == "error").Select(issue => issue.Row).Where(row => row > 0).Distinct().Count();
                var warningRows = issues.Where(issue => issue.Severity == "warning").Select(issue => issue.Row).Where(row => row > 0).Distinct().Count();
                var totalRows = imported.Rows.Count;
                payload.Model = imported;
                payload.Model.ImportWorkbookFile = null;

                context.Ready(
                    payload,
                    totalRows,
                    Math.Max(totalRows - errorRows, 0),
                    warningRows,
                    errorRows,
                    duplicates.Count,
                    issues.Take(200).ToList(),
                    BuildPreview(imported.Rows),
                    sheetCount,
                    imported.ImportedSheetName ?? ReadSelectedSheet(normalizedPath));
            }
            finally
            {
                accessor.HttpContext = null;
            }
        }
        catch (InvalidDataException ex)
        {
            context.Fail(ex.Message, [new ExcelImportIssue(0, "فایل", ex.Message)]);
        }
        finally
        {
            ExcelWorkbookNormalizer.TryDelete(payload.SourcePath);
            if (!string.Equals(normalizedPath, payload.SourcePath, StringComparison.OrdinalIgnoreCase))
            {
                ExcelWorkbookNormalizer.TryDelete(normalizedPath);
            }
        }
    }

    private async Task RegisterAsync(ExcelImportJobContext context, LoadingImportPayload payload, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var totalRows = payload.Model.Rows.Count;
        context.Update(ExcelImportJobStage.Registration, $"در حال ثبت ۰ از {totalRows} ردیف داخل Transaction…", 0, totalRows);
        await using var scope = _scopeFactory.CreateAsyncScope();
        var controller = CreateLoadingController(scope.ServiceProvider, payload.Principal, out var accessor);
        try
        {
            payload.Model.ImportWorkbookFile = null;
            payload.Model.ImportedRowsJson = null;
            var result = await controller.Create(payload.Model);
            if (result is ViewResult)
            {
                var issues = ReadModelStateIssues(controller, payload.Model.Rows);
                context.Fail("ثبت انجام نشد؛ خطاهای مشخص‌شده را اصلاح و فایل را دوباره بررسی کنید.", issues);
                return;
            }

            context.Complete(totalRows, 0, 0, "/Loading");
        }
        finally
        {
            accessor.HttpContext = null;
        }
    }

    private static LoadingController CreateLoadingController(IServiceProvider services, ClaimsPrincipal principal, out IHttpContextAccessor accessor)
    {
        var httpContext = new DefaultHttpContext { RequestServices = services, User = principal };
        httpContext.Features.Set<IHttpResponseFeature>(new HttpResponseFeature());
        accessor = services.GetRequiredService<IHttpContextAccessor>();
        accessor.HttpContext = httpContext;
        var controller = ActivatorUtilities.CreateInstance<LoadingController>(services);
        controller.ControllerContext = new ControllerContext(new ActionContext(
            httpContext,
            new RouteData(new RouteValueDictionary(new { controller = "Loading" })),
            new ControllerActionDescriptor { ControllerName = "Loading", ActionName = "Create" }));
        return controller;
    }

    private static List<ExcelImportIssue> ValidateRows(IReadOnlyList<LoadingCreateRowViewModel> rows, ExcelImportJobContext context)
    {
        var issues = new List<ExcelImportIssue>();
        for (var index = 0; index < rows.Count; index++)
        {
            var results = new List<ValidationResult>();
            Validator.TryValidateObject(rows[index], new ValidationContext(rows[index]), results, true);
            foreach (var result in results)
            {
                var member = result.MemberNames.FirstOrDefault() ?? "ردیف";
                issues.Add(new ExcelImportIssue(index + 2, DisplayName(member), result.ErrorMessage ?? "مقدار معتبر نیست."));
            }

            var processed = index + 1;
            if (processed == rows.Count || processed % 100 == 0)
            {
                context.Update(
                    ExcelImportJobStage.Validation,
                    $"در حال اعتبارسنجی ردیف {processed} از {rows.Count}…",
                    processed,
                    rows.Count);
            }
        }

        return issues;
    }

    private static List<ExcelImportIssue> ReadModelStateIssues(Controller controller, IReadOnlyList<LoadingCreateRowViewModel> rows)
    {
        var issues = new List<ExcelImportIssue>();
        foreach (var pair in controller.ModelState.Where(pair => pair.Value?.Errors.Count > 0))
        {
            var row = ResolveRowNumber(pair.Key, rows);
            foreach (var error in pair.Value!.Errors)
            {
                issues.Add(new ExcelImportIssue(row, DisplayName(pair.Key.Split('.').Last()),
                    string.IsNullOrWhiteSpace(error.ErrorMessage) ? "مقدار معتبر نیست." : error.ErrorMessage));
            }
        }

        return issues;
    }

    private static int ResolveRowNumber(string key, IReadOnlyList<LoadingCreateRowViewModel> rows)
    {
        for (var index = 0; index < rows.Count; index++)
        {
            if (!string.IsNullOrWhiteSpace(rows[index].RowKey) && key.Contains(rows[index].RowKey, StringComparison.OrdinalIgnoreCase))
            {
                return index + 2;
            }
        }

        return 0;
    }

    private static HashSet<int> FindDuplicateRows(IReadOnlyList<LoadingCreateRowViewModel> rows)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var duplicates = new HashSet<int>();
        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            var key = $"{row.BillOfLadingNumber?.Trim()}|{row.WagonNumber?.Trim()}|{row.ImportedTransportReference?.Trim()}|{row.LoadingDate:yyyy-MM-dd}";
            if (key.Replace("|", string.Empty).Length > 10 && !seen.Add(key))
            {
                duplicates.Add(index + 1);
            }
        }

        return duplicates;
    }

    private static IReadOnlyList<ExcelImportPreviewRow> BuildPreview(IReadOnlyList<LoadingCreateRowViewModel> rows)
        => rows.Take(10).Select((row, index) => new ExcelImportPreviewRow(index + 2, new Dictionary<string, string>
        {
            ["تاریخ"] = row.LoadingDate.ToString("yyyy-MM-dd"),
            ["مرجع"] = row.BillOfLadingNumber ?? "—",
            ["نمبر موتر/واگن"] = row.ImportedTransportReference ?? row.WagonNumber ?? "—",
            ["مقدار (MT)"] = row.LoadedQuantityMt.ToString("0.###", CultureInfo.InvariantCulture),
            ["مقصد"] = row.DestinationName ?? "—"
        })).ToList();

    private static int CountSheets(string path)
    {
        using var document = SpreadsheetDocument.Open(path, false);
        return document.WorkbookPart?.Workbook.Sheets?.Elements<Sheet>().Count() ?? 0;
    }

    private static string? ReadSelectedSheet(string path)
    {
        using var document = SpreadsheetDocument.Open(path, false);
        return document.WorkbookPart?.Workbook.Sheets?.Elements<Sheet>().FirstOrDefault()?.Name?.Value;
    }

    private static string DisplayName(string member)
    {
        var property = typeof(LoadingCreateRowViewModel).GetProperty(member);
        return property?.GetCustomAttributes(typeof(DisplayAttribute), true).OfType<DisplayAttribute>().FirstOrDefault()?.Name
            ?? (member == nameof(LoadingCreateViewModel.ImportWorkbookFile) ? "فایل" : member);
    }

    private static ClaimsPrincipal ClonePrincipal(ClaimsPrincipal principal)
        => new(new ClaimsIdentity(principal.Claims.Select(claim => new Claim(claim.Type, claim.Value)), principal.Identity?.AuthenticationType));

    private static string ResolveOwnerKey(ClaimsPrincipal principal)
        => principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? principal.Identity?.Name ?? throw new UnauthorizedAccessException();

    private static string Csv(string? value) => $"\"{(value ?? string.Empty).Replace("\"", "\"\"")}\"";

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

    private sealed class LoadingImportPayload
    {
        public LoadingImportPayload(LoadingCreateViewModel model, ClaimsPrincipal principal, string sourcePath, string extension, string fileName)
        {
            Model = model;
            Principal = principal;
            SourcePath = sourcePath;
            Extension = extension;
            FileName = fileName;
        }

        public LoadingCreateViewModel Model { get; set; }
        public ClaimsPrincipal Principal { get; }
        public string SourcePath { get; }
        public string Extension { get; }
        public string FileName { get; }
    }
}
