namespace PTGOilSystem.Web.Models.Loading;

/// <summary>
/// پاسخ اکشن «امپورت اکسل» فرم ثبت بارگیری. هیچ رکوردی در دیتابیس ثبت نمی‌شود؛
/// فقط سطرهای آمادهٔ فرم به همراه مشکلات هر سطر برگردانده می‌شود.
/// </summary>
public sealed class LoadingImportResponse
{
    public bool Success { get; set; }

    /// <summary>خطاهایی که کل فایل را غیرقابل امپورت می‌کند (ساختار یا ستون‌های ناقص).</summary>
    public List<string> GlobalErrors { get; set; } = [];

    public List<LoadingImportRow> Rows { get; set; } = [];

    /// <summary>تعداد سطرهای خوانده‌شده از فایل.</summary>
    public int TotalRows { get; set; }

    /// <summary>سطرهایی که بدون مشکل وارد فرم شدند.</summary>
    public int ImportedRows { get; set; }

    /// <summary>سطرهایی که وارد فرم شدند ولی نیاز به اصلاح دارند.</summary>
    public int InvalidRows { get; set; }

    public string? SheetName { get; set; }
    public int TransportType { get; set; }
    public int? OriginLocationId { get; set; }
    public int? ProductId { get; set; }
    public string? LoadingDate { get; set; }
    public string? Message { get; set; }
}

public sealed class LoadingImportRow
{
    public LoadingCreateRowViewModel Row { get; set; } = new();

    /// <summary>شمارهٔ واقعی سطر در فایل اکسل.</summary>
    public int ExcelRowNumber { get; set; }

    public string? SheetName { get; set; }

    public List<LoadingImportRowIssue> Issues { get; set; } = [];
}

public sealed class LoadingImportRowIssue
{
    /// <summary>نام ویژگی مدل سطر (camelCase در JSON) برای وصل‌کردن پیام به همان کنترل فرم.</summary>
    public string? Field { get; set; }

    /// <summary>عنوان واقعی ستون در فایل اکسل.</summary>
    public string? Column { get; set; }

    public string Message { get; set; } = string.Empty;
}
