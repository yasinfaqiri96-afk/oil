using Microsoft.AspNetCore.Http;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services.Exceptions;

namespace PTGOilSystem.Web.Services.HumanResources;

public interface IHrFileStorage
{
    /// <summary>فایل را اعتبارسنجی و ذخیره می‌کند و سطرِ <see cref="HrAttachment"/> را (بدون SaveChanges) اضافه می‌کند.</summary>
    Task<HrAttachment> SaveAsync(int employeeId, HrAttachmentOwner owner, IFormFile file, CancellationToken ct = default);

    /// <summary>مسیرِ فیزیکیِ فایل برای دانلودِ مجاز؛ اگر فایل روی دیسک نباشد null.</summary>
    string? ResolvePath(HrAttachment attachment);
}

/// <summary>
/// ذخیرهٔ فایل‌های مدیریت بشری. همان قواعدِ آپلودِ اسنادِ گمرک (سقف حجم، پسوندِ مجاز، نامِ GUID)،
/// ولی زیرِ ContentRoot/App_Data و نه wwwroot: پوشهٔ /uploads پیش از احراز هویت و بدون کنترل
/// دسترسی سرو می‌شود و برای تذکره، CV و قرارداد مناسب نیست. دانلود فقط از اکشنِ مجاز است.
/// فایلی که ثبت شد هرگز از دیسک پاک نمی‌شود؛ جایگزینی سطرِ تازه می‌سازد.
/// </summary>
public sealed class HrFileStorage(ApplicationDbContext db, IWebHostEnvironment environment) : IHrFileStorage
{
    public const long MaxFileBytes = 10 * 1024 * 1024;

    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".jpg", ".jpeg", ".png", ".webp", ".doc", ".docx"
    };

    public async Task<HrAttachment> SaveAsync(int employeeId, HrAttachmentOwner owner, IFormFile file, CancellationToken ct = default)
    {
        if (file is null || file.Length == 0)
            throw new BusinessRuleException("HR_FILE_EMPTY", "هیچ فایلی انتخاب نشده است.");
        if (file.Length > MaxFileBytes)
            throw new BusinessRuleException("HR_FILE_TOO_LARGE", "حجم فایل نباید بیشتر از 10MB باشد.");

        var extension = Path.GetExtension(file.FileName);
        if (string.IsNullOrWhiteSpace(extension) || !AllowedExtensions.Contains(extension))
            throw new BusinessRuleException("HR_FILE_TYPE", "فقط فایل PDF، Word یا عکس (JPG، PNG، WEBP) مجاز است.");

        var storedFileName = $"{Guid.NewGuid():N}{extension.ToLowerInvariant()}";
        var directory = EmployeeDirectory(employeeId);
        Directory.CreateDirectory(directory);
        await using (var stream = File.Create(Path.Combine(directory, storedFileName)))
        {
            await file.CopyToAsync(stream, ct);
        }

        var originalName = Path.GetFileName(file.FileName);
        var attachment = new HrAttachment
        {
            EmployeeId = employeeId,
            Owner = owner,
            OriginalFileName = originalName.Length > 260 ? originalName[^260..] : originalName,
            StoredFileName = storedFileName,
            ContentType = string.IsNullOrWhiteSpace(file.ContentType) ? null : file.ContentType,
            FileSizeBytes = file.Length
        };
        db.HrAttachments.Add(attachment);
        return attachment;
    }

    public string? ResolvePath(HrAttachment attachment)
    {
        // نامِ ذخیره‌شده فقط GUID+پسوند است؛ هر چیزِ دیگری (مسیرِ نسبی) رد می‌شود.
        if (attachment.StoredFileName != Path.GetFileName(attachment.StoredFileName))
            return null;

        var path = Path.Combine(EmployeeDirectory(attachment.EmployeeId), attachment.StoredFileName);
        return File.Exists(path) ? path : null;
    }

    private string EmployeeDirectory(int employeeId)
        => Path.Combine(environment.ContentRootPath, "App_Data", "hr-files", employeeId.ToString(System.Globalization.CultureInfo.InvariantCulture));
}
