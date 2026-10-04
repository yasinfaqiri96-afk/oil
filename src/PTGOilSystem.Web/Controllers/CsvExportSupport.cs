using System.Text;
using Microsoft.AspNetCore.Mvc;
using PTGOilSystem.Web.Helpers;

namespace PTGOilSystem.Web.Controllers;

internal static class CsvExportSupport
{
    /// <summary>سقف ردیف مجاز برای خروجی CSV — جلوگیری از بارگذاری کل جدول در حافظه.</summary>
    public const int MaxRows = 50_000;

    /// <summary>
    /// خروجی CSV را به‌صورت جریانی روی بدنهٔ پاسخ می‌نویسد؛ کل فایل هیچ‌گاه در حافظه ساخته نمی‌شود.
    /// انکودینگ UTF-8 با BOM است تا Excel متن فارسی را درست بخواند.
    /// </summary>
    public static IActionResult File(Controller controller, string filename, string[] headers, IEnumerable<string?[]> rows)
        => new CsvStreamResult(filename, headers, rows);

    // میلادی: همان ISO قبلی؛ هجری شمسی: همان الگو با تاریخِ شمسی.
    public static string Date(DateTime? value)
        => value.HasValue ? DateDisplay.Format(value.Value, DateDisplay.HtmlDateInputPattern) : string.Empty;
    public static string Decimal(decimal? value) => value?.ToString("0.####") ?? "";

    internal static string Escape(string? value)
    {
        value ??= "";
        if (!value.Contains(',') && !value.Contains('"') && !value.Contains('\n') && !value.Contains('\r'))
        {
            return value;
        }

        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}

/// <summary>
/// نتیجهٔ جریانی CSV. ردیف‌ها یکی‌یکی روی <c>Response.Body</c> نوشته می‌شوند، پس برخلاف
/// روش قبلی (StringBuilder → string → byte[]) کل خروجی سه بار در RAM کپی نمی‌شود.
/// </summary>
internal sealed class CsvStreamResult : IActionResult
{
    // encoderShouldEmitUTF8Identifier: true → StreamWriter پیش از اولین نوشتن، BOM را می‌نویسد.
    private static readonly UTF8Encoding Utf8WithBom = new(encoderShouldEmitUTF8Identifier: true);

    private readonly string _filename;
    private readonly string[] _headers;
    private readonly IEnumerable<string?[]> _rows;

    public CsvStreamResult(string filename, string[] headers, IEnumerable<string?[]> rows)
    {
        _filename = filename;
        _headers = headers;
        _rows = rows;
    }

    public string ContentType => "text/csv; charset=utf-8";

    public async Task ExecuteResultAsync(ActionContext context)
    {
        var response = context.HttpContext.Response;
        response.ContentType = ContentType;
        response.Headers.ContentDisposition = $"attachment; filename=\"{_filename}\"";

        // leaveOpen: بدنهٔ پاسخ متعلق به میزبان است و نباید اینجا بسته شود.
        await using var writer = new StreamWriter(response.Body, Utf8WithBom, bufferSize: 16 * 1024, leaveOpen: true);

        await writer.WriteLineAsync(string.Join(",", _headers.Select(CsvExportSupport.Escape)));
        foreach (var row in _rows)
        {
            await writer.WriteLineAsync(string.Join(",", row.Select(CsvExportSupport.Escape)));
        }

        await writer.FlushAsync();
    }
}
