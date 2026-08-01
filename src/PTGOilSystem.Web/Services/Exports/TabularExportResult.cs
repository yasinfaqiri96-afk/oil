using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;
using PTGOilSystem.Web.Helpers;

namespace PTGOilSystem.Web.Services.Exports;

public sealed class TabularExportResult(
    TabularExportDocument document,
    TabularExportFormat format) : IActionResult
{
    /// <summary>سندی که قرار است نوشته شود. تست‌ها با همین، «صفحه == خروجی» را می‌سنجند.</summary>
    public TabularExportDocument Document => document;

    public TabularExportFormat Format => format;

    public async Task ExecuteResultAsync(ActionContext context)
    {
        var httpContext = context.HttpContext;
        var service = httpContext.RequestServices.GetRequiredService<ITabularExportService>();
        var isEnglish = UiText.IsEn(httpContext);
        var extension = format == TabularExportFormat.Excel ? ".xlsx" : ".pdf";
        var contentType = format == TabularExportFormat.Excel
            ? "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"
            : "application/pdf";
        var fileName = BuildFileName(document.FileNameStem, extension);

        var tempPath = Path.Combine(Path.GetTempPath(), $"ptg-export-{Guid.NewGuid():N}{extension}");
        try
        {
            await using (var output = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await service.WriteAsync(document, format, isEnglish, output, httpContext.RequestAborted);
                await output.FlushAsync(httpContext.RequestAborted);
            }

            httpContext.Response.ContentType = contentType;
            var disposition = new ContentDispositionHeaderValue("attachment")
            {
                FileNameStar = fileName
            };
            httpContext.Response.Headers.ContentDisposition = disposition.ToString();

            await using var input = new FileStream(
                tempPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            httpContext.Response.ContentLength = input.Length;
            await input.CopyToAsync(httpContext.Response.Body, 64 * 1024, httpContext.RequestAborted);
        }
        catch (TabularExportLimitException exception)
        {
            httpContext.Response.Clear();
            httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
            httpContext.Response.ContentType = "text/plain; charset=utf-8";
            var message = isEnglish
                ? $"There are too many records ({exception.ActualRows:N0}). Narrow the date range or filters. Maximum: {exception.MaximumRows:N0}."
                : $"تعداد اطلاعات زیاد است ({exception.ActualRows:N0})؛ بازه تاریخ یا فیلترها را محدود کنید. سقف مجاز: {exception.MaximumRows:N0}.";
            await httpContext.Response.WriteAsync(message, httpContext.RequestAborted);
        }
        finally
        {
            if (System.IO.File.Exists(tempPath))
            {
                System.IO.File.Delete(tempPath);
            }
        }
    }

    private static string BuildFileName(string stem, string extension)
    {
        var safeStem = new string(stem
            .Where(character => char.IsLetterOrDigit(character) || character is '-' or '_')
            .ToArray());
        if (string.IsNullOrWhiteSpace(safeStem))
        {
            safeStem = "PTG_Export";
        }
        return $"{safeStem}_{DateTime.UtcNow:yyyy-MM-dd}{extension}";
    }
}

// چند سند در یک فایل (Excel: چند شیت، PDF: چند بخش). نامِ فایل از FileNameStem سندِ اول.
public sealed class TabularExportWorkbookResult(
    IReadOnlyList<TabularExportDocument> sheets,
    TabularExportFormat format) : IActionResult
{
    public async Task ExecuteResultAsync(ActionContext context)
    {
        var httpContext = context.HttpContext;
        var service = httpContext.RequestServices.GetRequiredService<ITabularExportService>();
        var isEnglish = UiText.IsEn(httpContext);
        var extension = format == TabularExportFormat.Excel ? ".xlsx" : ".pdf";
        var contentType = format == TabularExportFormat.Excel
            ? "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"
            : "application/pdf";
        var stem = sheets.Count > 0 ? sheets[0].FileNameStem : "PTG_Export";
        var fileName = BuildFileName(stem, extension);

        var tempPath = Path.Combine(Path.GetTempPath(), $"ptg-export-{Guid.NewGuid():N}{extension}");
        try
        {
            await using (var output = new FileStream(
                tempPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await service.WriteWorkbookAsync(sheets, format, isEnglish, output, httpContext.RequestAborted);
                await output.FlushAsync(httpContext.RequestAborted);
            }

            httpContext.Response.ContentType = contentType;
            httpContext.Response.Headers.ContentDisposition =
                new ContentDispositionHeaderValue("attachment") { FileNameStar = fileName }.ToString();

            await using var input = new FileStream(
                tempPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            httpContext.Response.ContentLength = input.Length;
            await input.CopyToAsync(httpContext.Response.Body, 64 * 1024, httpContext.RequestAborted);
        }
        catch (TabularExportLimitException exception)
        {
            httpContext.Response.Clear();
            httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
            httpContext.Response.ContentType = "text/plain; charset=utf-8";
            var message = isEnglish
                ? $"There are too many records ({exception.ActualRows:N0}). Narrow the date range or filters. Maximum: {exception.MaximumRows:N0}."
                : $"تعداد اطلاعات زیاد است ({exception.ActualRows:N0})؛ بازه تاریخ یا فیلترها را محدود کنید. سقف مجاز: {exception.MaximumRows:N0}.";
            await httpContext.Response.WriteAsync(message, httpContext.RequestAborted);
        }
        finally
        {
            if (System.IO.File.Exists(tempPath))
            {
                System.IO.File.Delete(tempPath);
            }
        }
    }

    private static string BuildFileName(string stem, string extension)
    {
        var safeStem = new string(stem
            .Where(character => char.IsLetterOrDigit(character) || character is '-' or '_')
            .ToArray());
        if (string.IsNullOrWhiteSpace(safeStem))
        {
            safeStem = "PTG_Export";
        }
        return $"{safeStem}_{DateTime.UtcNow:yyyy-MM-dd}{extension}";
    }
}

public static class TabularExportSupport
{
    public static IActionResult File(
        Controller controller,
        string? format,
        TabularExportDocument document)
        => new TabularExportResult(document, ParseFormat(format));

    public static IActionResult File(
        Controller controller,
        string? format,
        IReadOnlyList<TabularExportDocument> sheets)
        => new TabularExportWorkbookResult(sheets, ParseFormat(format));

    public static TabularExportFormat ParseFormat(string? value)
        => string.Equals(value, "pdf", StringComparison.OrdinalIgnoreCase)
            ? TabularExportFormat.Pdf
            : TabularExportFormat.Excel;

    public static IReadOnlyList<TabularExportFilter> FilterSummary(params (string Label, object? Value)[] filters)
        => filters
            .Where(item => item.Value is not null && !string.IsNullOrWhiteSpace(Convert.ToString(item.Value)))
            .Select(item => new TabularExportFilter(item.Label, item.Label, Convert.ToString(item.Value)))
            .ToList();

    public static IReadOnlyList<TabularExportFilter> FiltersFromQuery(HttpRequest request)
        => request.Query
            .Where(item => !string.Equals(item.Key, "format", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(item.Key, "page", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(item.Value.ToString()))
            .Select(item => new TabularExportFilter(item.Key, item.Key, item.Value.ToString()))
            .ToList();
}
