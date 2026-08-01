using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.Quality;
using PTGOilSystem.Web.Security;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Audit;
using PTGOilSystem.Web.Services.Time;

namespace PTGOilSystem.Web.Controllers;

/// <summary>
/// کیفیت و لابراتوار. عمداً ساده است: ثبت، ویرایش، پیوست فایل نتیجه و مشاهده.
/// هیچ Workflow چندمرحله‌ای، هیچ اثر مالی و هیچ اثر موجودی ندارد.
///
/// مشاهده با [Authorize] معمول باز است و هر تغییری پشت <see cref="AuthPolicies.ManageData"/>
/// می‌نشیند، پس «مشاهده» و «مدیریت» جدا هستند.
/// </summary>
[Authorize]
public partial class QualityInspectionsController : Controller
{
    private const int PageSize = 20;
    private const long MaxDocumentBytes = 10 * 1024 * 1024; // 10 MB
    private static readonly HashSet<string> AllowedDocumentExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".pdf", ".jpg", ".jpeg", ".png", ".webp" };

    private readonly ApplicationDbContext _db;
    private readonly IAuditService _audit;
    private readonly IWebHostEnvironment _environment;
    private readonly IAfghanistanBusinessClock _clock;
    private readonly ILogger<QualityInspectionsController> _logger;

    public QualityInspectionsController(
        ApplicationDbContext db,
        IAuditService audit,
        IWebHostEnvironment environment,
        IAfghanistanBusinessClock clock,
        ILogger<QualityInspectionsController> logger)
    {
        _db = db;
        _audit = audit;
        _environment = environment;
        _clock = clock;
        _logger = logger;
    }

    // ------------------------------------------------------------------- list

    public async Task<IActionResult> Index(
        [FromQuery] QualityInspectionFilterViewModel? filter = null,
        int page = 1,
        [FromQuery(Name = "pageSize")] int? perPage = null,
        CancellationToken ct = default)
    {
        filter ??= new QualityInspectionFilterViewModel();
        page = Math.Max(1, page);

        var pageSize = PTGOilSystem.Web.Helpers.ListPageSize.Resolve(perPage, PageSize);
        ViewData["PageSize"] = pageSize;
        ViewData["DefaultPageSize"] = PageSize;

        var query = BuildFilteredQuery(filter);

        var totalCount = await query.CountAsync(ct);
        var pageCount = Math.Max(1, (int)Math.Ceiling(totalCount / (double)pageSize));
        page = Math.Min(page, pageCount);

        var items = await query
            .OrderByDescending(q => q.SampleDate)
            .ThenByDescending(q => q.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(q => new QualityInspectionListItemViewModel
            {
                Id = q.Id,
                ProductName = q.Product != null ? q.Product.Name : "",
                CompanyName = q.Company != null ? q.Company.Name : null,
                ContractNumber = q.Contract != null ? q.Contract.ContractNumber : null,
                ShipmentReference = q.Shipment != null ? q.Shipment.ShipmentCode : null,
                CustomsDeclarationReference = q.CustomsDeclaration != null
                    ? q.CustomsDeclaration.DeclarationReference
                    : null,
                LaboratoryName = q.LaboratoryName,
                ResultNumber = q.ResultNumber,
                SampleDate = q.SampleDate,
                ResultDate = q.ResultDate,
                Status = q.Status,
                DocumentCount = q.Documents.Count
            })
            .ToListAsync(ct);

        // شمارنده‌ها روی همان فیلتر محاسبه می‌شوند تا با فهرست هم‌خوان باشند.
        var statusCounts = await query
            .GroupBy(q => q.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        return View(new QualityInspectionIndexViewModel
        {
            Filter = filter,
            Items = items,
            CurrentPage = page,
            PageCount = pageCount,
            TotalCount = totalCount,
            PendingCount = statusCounts.FirstOrDefault(c => c.Status == QualityInspectionStatus.Pending)?.Count ?? 0,
            AcceptedCount = statusCounts.FirstOrDefault(c => c.Status == QualityInspectionStatus.Accepted)?.Count ?? 0,
            RejectedCount = statusCounts.FirstOrDefault(c => c.Status == QualityInspectionStatus.Rejected)?.Count ?? 0
        });
    }

    // ---------------------------------------------------------------- details

    public async Task<IActionResult> Details(int id, CancellationToken ct = default)
    {
        var entity = await _db.QualityInspections.AsNoTracking()
            .Include(q => q.Product)
            .Include(q => q.Company)
            .Include(q => q.Contract)
            .Include(q => q.Shipment)
            .Include(q => q.LoadingRegister)
            .Include(q => q.CustomsDeclaration)
            .Include(q => q.Documents)
            .FirstOrDefaultAsync(q => q.Id == id, ct);
        if (entity is null)
        {
            return NotFound();
        }

        var siblings = await BuildSiblingsAsync(entity, ct);

        return View(new QualityInspectionDetailsViewModel
        {
            Id = entity.Id,
            ProductName = entity.Product?.Name ?? "",
            CompanyName = entity.Company?.Name,
            ContractId = entity.ContractId,
            ContractNumber = entity.Contract?.ContractNumber,
            ShipmentId = entity.ShipmentId,
            ShipmentReference = entity.Shipment?.ShipmentCode,
            LoadingRegisterId = entity.LoadingRegisterId,
            LoadingReference = entity.LoadingRegister?.BillOfLadingNumber
                ?? entity.LoadingRegister?.RwbNo
                ?? (entity.LoadingRegisterId.HasValue ? $"#{entity.LoadingRegisterId}" : null),
            CustomsDeclarationId = entity.CustomsDeclarationId,
            CustomsDeclarationReference = entity.CustomsDeclaration?.DeclarationReference,
            LaboratoryName = entity.LaboratoryName,
            ResultNumber = entity.ResultNumber,
            SampleDate = entity.SampleDate,
            ResultDate = entity.ResultDate,
            Status = entity.Status,
            DensityKgM3 = entity.DensityKgM3,
            SulphurPercent = entity.SulphurPercent,
            FlashPointC = entity.FlashPointC,
            WaterContentPercent = entity.WaterContentPercent,
            OctaneOrCetaneNumber = entity.OctaneOrCetaneNumber,
            AdditionalSpecifications = entity.AdditionalSpecifications,
            Description = entity.Description,
            RejectionReason = entity.RejectionReason,
            CreatedByUserName = entity.CreatedByUserName,
            CreatedAtUtc = entity.CreatedAtUtc,
            UpdatedByUserName = entity.UpdatedByUserName,
            UpdatedAtUtc = entity.UpdatedAtUtc,
            Documents = entity.Documents
                .OrderByDescending(d => d.UploadedAt)
                .Select(d => new QualityInspectionDocumentViewModel
                {
                    Id = d.Id,
                    DocumentType = d.DocumentType,
                    OriginalFileName = d.OriginalFileName,
                    FilePath = d.FilePath,
                    FileSizeBytes = d.FileSizeBytes,
                    UploadedAt = d.UploadedAt,
                    UploadedByUserName = d.UploadedByUserName,
                    Notes = d.Notes
                })
                .ToList(),
            SiblingInspections = siblings,
            IsFinalResult = siblings.Count == 0 || siblings[0].Id == entity.Id
        });
    }

    // ----------------------------------------------------------------- create

    [Authorize(Policy = AuthPolicies.ManageData)]
    public async Task<IActionResult> Create(
        int? shipmentId = null,
        int? loadingRegisterId = null,
        int? customsDeclarationId = null,
        string? returnUrl = null,
        CancellationToken ct = default)
    {
        var model = new QualityInspectionFormViewModel
        {
            ShipmentId = shipmentId,
            LoadingRegisterId = loadingRegisterId,
            CustomsDeclarationId = customsDeclarationId,
            SampleDate = _clock.Today,
            ReturnUrl = TryGetLocalReturnUrl(returnUrl)
        };

        await PopulateLookupsAsync(model, ct);
        return View(model);
    }

    [HttpPost, ValidateAntiForgeryToken]
    [Authorize(Policy = AuthPolicies.ManageData)]
    public async Task<IActionResult> Create(QualityInspectionFormViewModel model, CancellationToken ct = default)
    {
        if (!ModelState.IsValid)
        {
            await PopulateLookupsAsync(model, ct);
            return View(model);
        }

        var entity = new QualityInspection { CreatedByUserName = User.Identity?.Name };
        ApplyForm(entity, model);
        _db.QualityInspections.Add(entity);
        await _db.SaveChangesAsync(ct);

        await _audit.LogAndSaveAsync(
            nameof(QualityInspection),
            entity.Id,
            AuditAction.Insert,
            diff: AuditDiffFormatter.ForCreate(
                ("ProductId", entity.ProductId),
                ("ShipmentId", entity.ShipmentId),
                ("LoadingRegisterId", entity.LoadingRegisterId),
                ("CustomsDeclarationId", entity.CustomsDeclarationId),
                ("LaboratoryName", entity.LaboratoryName),
                ("ResultNumber", entity.ResultNumber),
                ("SampleDate", entity.SampleDate),
                ("ResultDate", entity.ResultDate),
                ("Status", entity.Status)),
            ct: ct);

        TempData["ok"] = "آزمایش کیفیت ثبت شد.";
        return RedirectToAction(nameof(Details), new { id = entity.Id });
    }

    // ------------------------------------------------------------------- edit

    [Authorize(Policy = AuthPolicies.ManageData)]
    public async Task<IActionResult> Edit(int id, string? returnUrl = null, CancellationToken ct = default)
    {
        var entity = await _db.QualityInspections.AsNoTracking().FirstOrDefaultAsync(q => q.Id == id, ct);
        if (entity is null)
        {
            return NotFound();
        }

        var model = new QualityInspectionFormViewModel
        {
            Id = entity.Id,
            CompanyId = entity.CompanyId,
            ContractId = entity.ContractId,
            ShipmentId = entity.ShipmentId,
            LoadingRegisterId = entity.LoadingRegisterId,
            CustomsDeclarationId = entity.CustomsDeclarationId,
            ProductId = entity.ProductId,
            LaboratoryName = entity.LaboratoryName,
            ResultNumber = entity.ResultNumber,
            SampleDate = entity.SampleDate,
            ResultDate = entity.ResultDate,
            Status = entity.Status,
            DensityKgM3 = entity.DensityKgM3,
            SulphurPercent = entity.SulphurPercent,
            FlashPointC = entity.FlashPointC,
            WaterContentPercent = entity.WaterContentPercent,
            OctaneOrCetaneNumber = entity.OctaneOrCetaneNumber,
            AdditionalSpecifications = entity.AdditionalSpecifications,
            Description = entity.Description,
            RejectionReason = entity.RejectionReason,
            ReturnUrl = TryGetLocalReturnUrl(returnUrl)
        };

        await PopulateLookupsAsync(model, ct);
        return View(model);
    }

    [HttpPost, ValidateAntiForgeryToken]
    [Authorize(Policy = AuthPolicies.ManageData)]
    public async Task<IActionResult> Edit(QualityInspectionFormViewModel model, CancellationToken ct = default)
    {
        var entity = await _db.QualityInspections.FirstOrDefaultAsync(q => q.Id == model.Id, ct);
        if (entity is null)
        {
            return NotFound();
        }

        if (!ModelState.IsValid)
        {
            await PopulateLookupsAsync(model, ct);
            return View(model);
        }

        var before = Snapshot(entity);
        ApplyForm(entity, model);
        entity.UpdatedByUserName = User.Identity?.Name;
        entity.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        await _audit.LogAndSaveAsync(
            nameof(QualityInspection),
            entity.Id,
            AuditAction.Update,
            diff: BuildUpdateDiff(before, Snapshot(entity)),
            ct: ct);

        TempData["ok"] = "آزمایش کیفیت به‌روزرسانی شد.";
        return RedirectToAction(nameof(Details), new { id = entity.Id });
    }

    // -------------------------------------------------------------- documents

    [HttpPost, ValidateAntiForgeryToken]
    [Authorize(Policy = AuthPolicies.ManageData)]
    public async Task<IActionResult> UploadDocument(
        int id,
        IFormFile? file,
        string? documentType,
        string? notes,
        CancellationToken ct = default)
    {
        if (!await _db.QualityInspections.AnyAsync(q => q.Id == id, ct))
        {
            return NotFound();
        }

        if (file is null || file.Length == 0)
        {
            TempData["error"] = "هیچ فایلی انتخاب نشده است.";
            return RedirectToAction(nameof(Details), new { id });
        }

        if (file.Length > MaxDocumentBytes)
        {
            TempData["error"] = "حجم فایل نباید بیشتر از 10MB باشد.";
            return RedirectToAction(nameof(Details), new { id });
        }

        var extension = Path.GetExtension(file.FileName);
        if (string.IsNullOrWhiteSpace(extension) || !AllowedDocumentExtensions.Contains(extension))
        {
            TempData["error"] = "فقط فایل‌های PDF، JPG، JPEG، PNG یا WEBP مجاز است.";
            return RedirectToAction(nameof(Details), new { id });
        }

        var relativeDirectory = Path.Combine("uploads", "quality-inspections", id.ToString());
        var absoluteDirectory = Path.Combine(GetWebRootPath(), relativeDirectory);
        Directory.CreateDirectory(absoluteDirectory);

        var storedFileName = $"{Guid.NewGuid():N}{extension.ToLowerInvariant()}";
        await using (var stream = System.IO.File.Create(Path.Combine(absoluteDirectory, storedFileName)))
        {
            await file.CopyToAsync(stream, ct);
        }

        var document = new QualityInspectionDocument
        {
            QualityInspectionId = id,
            DocumentType = string.IsNullOrWhiteSpace(documentType) ? null : documentType.Trim(),
            OriginalFileName = Path.GetFileName(file.FileName),
            StoredFileName = storedFileName,
            FilePath = "/" + relativeDirectory.Replace('\\', '/') + "/" + storedFileName,
            ContentType = file.ContentType,
            FileSizeBytes = file.Length,
            Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim(),
            UploadedAt = DateTime.UtcNow,
            UploadedByUserName = User.Identity?.Name
        };
        _db.QualityInspectionDocuments.Add(document);
        await _db.SaveChangesAsync(ct);

        await _audit.LogAndSaveAsync(
            nameof(QualityInspectionDocument),
            document.Id,
            AuditAction.Insert,
            diff: AuditDiffFormatter.ForCreate(
                ("QualityInspectionId", document.QualityInspectionId),
                ("OriginalFileName", document.OriginalFileName),
                ("DocumentType", document.DocumentType)),
            ct: ct);

        TempData["ok"] = "فایل نتیجه آپلود شد.";
        return RedirectToAction(nameof(Details), new { id });
    }

    public async Task<IActionResult> DownloadDocument(int id, CancellationToken ct = default)
    {
        var document = await _db.QualityInspectionDocuments.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == id, ct);
        if (document is null)
        {
            return NotFound();
        }

        var relative = document.FilePath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        var absolutePath = Path.Combine(GetWebRootPath(), relative);
        if (!System.IO.File.Exists(absolutePath))
        {
            _logger.LogWarning("Quality document {DocumentId} is missing on disk at {Path}.", id, absolutePath);
            return NotFound();
        }

        return PhysicalFile(
            absolutePath,
            string.IsNullOrWhiteSpace(document.ContentType) ? "application/octet-stream" : document.ContentType,
            document.OriginalFileName);
    }

    // ---------------------------------------------------------------- helpers

    private IQueryable<QualityInspection> BuildFilteredQuery(QualityInspectionFilterViewModel filter)
    {
        var query = _db.QualityInspections.AsNoTracking().AsQueryable();
        if (filter.ProductId.HasValue) query = query.Where(q => q.ProductId == filter.ProductId.Value);
        if (filter.ContractId.HasValue) query = query.Where(q => q.ContractId == filter.ContractId.Value);
        if (filter.ShipmentId.HasValue) query = query.Where(q => q.ShipmentId == filter.ShipmentId.Value);
        if (filter.Status.HasValue) query = query.Where(q => q.Status == filter.Status.Value);
        if (filter.FromDate.HasValue) query = query.Where(q => q.SampleDate >= filter.FromDate.Value.Date);
        if (filter.ToDate.HasValue) query = query.Where(q => q.SampleDate < filter.ToDate.Value.Date.AddDays(1));
        return query;
    }

    /// <summary>
    /// آزمایش‌های دیگرِ همان بار (همان محموله/بارگیری/اظهارنامه). ترتیب: تازه‌ترین اول،
    /// تا ردیف اول همان «نتیجهٔ نهایی» باشد. اگر هیچ اتصالی وجود نداشته باشد، هم‌خانواده‌ای هم نیست.
    /// </summary>
    private async Task<IReadOnlyList<QualityInspectionListItemViewModel>> BuildSiblingsAsync(
        QualityInspection entity,
        CancellationToken ct)
    {
        if (!entity.ShipmentId.HasValue
            && !entity.LoadingRegisterId.HasValue
            && !entity.CustomsDeclarationId.HasValue)
        {
            return [];
        }

        return await _db.QualityInspections.AsNoTracking()
            .Where(q =>
                (entity.ShipmentId.HasValue && q.ShipmentId == entity.ShipmentId)
                || (entity.LoadingRegisterId.HasValue && q.LoadingRegisterId == entity.LoadingRegisterId)
                || (entity.CustomsDeclarationId.HasValue && q.CustomsDeclarationId == entity.CustomsDeclarationId))
            .OrderByDescending(q => q.ResultDate ?? q.SampleDate)
            .ThenByDescending(q => q.Id)
            .Select(q => new QualityInspectionListItemViewModel
            {
                Id = q.Id,
                ProductName = q.Product != null ? q.Product.Name : "",
                LaboratoryName = q.LaboratoryName,
                ResultNumber = q.ResultNumber,
                SampleDate = q.SampleDate,
                ResultDate = q.ResultDate,
                Status = q.Status,
                DocumentCount = q.Documents.Count
            })
            .ToListAsync(ct);
    }

    private static void ApplyForm(QualityInspection entity, QualityInspectionFormViewModel model)
    {
        entity.CompanyId = model.CompanyId;
        entity.ContractId = model.ContractId;
        entity.ShipmentId = model.ShipmentId;
        entity.LoadingRegisterId = model.LoadingRegisterId;
        entity.CustomsDeclarationId = model.CustomsDeclarationId;
        entity.ProductId = model.ProductId;
        entity.LaboratoryName = model.LaboratoryName.Trim();
        entity.ResultNumber = Clean(model.ResultNumber);
        entity.SampleDate = model.SampleDate.Date;
        entity.ResultDate = model.ResultDate?.Date;
        entity.Status = model.Status;
        entity.DensityKgM3 = model.DensityKgM3;
        entity.SulphurPercent = model.SulphurPercent;
        entity.FlashPointC = model.FlashPointC;
        entity.WaterContentPercent = model.WaterContentPercent;
        entity.OctaneOrCetaneNumber = model.OctaneOrCetaneNumber;
        entity.AdditionalSpecifications = Clean(model.AdditionalSpecifications);
        entity.Description = Clean(model.Description);
        // دلیل رد فقط برای نتیجهٔ «رد» نگه داشته می‌شود تا متن قدیمی روی نتیجهٔ قبول نماند.
        entity.RejectionReason = model.Status == QualityInspectionStatus.Rejected
            ? Clean(model.RejectionReason)
            : null;
    }

    private static Dictionary<string, object?> Snapshot(QualityInspection entity) => new()
    {
        ["CompanyId"] = entity.CompanyId,
        ["ContractId"] = entity.ContractId,
        ["ShipmentId"] = entity.ShipmentId,
        ["LoadingRegisterId"] = entity.LoadingRegisterId,
        ["CustomsDeclarationId"] = entity.CustomsDeclarationId,
        ["ProductId"] = entity.ProductId,
        ["LaboratoryName"] = entity.LaboratoryName,
        ["ResultNumber"] = entity.ResultNumber,
        ["SampleDate"] = entity.SampleDate,
        ["ResultDate"] = entity.ResultDate,
        ["Status"] = entity.Status,
        ["DensityKgM3"] = entity.DensityKgM3,
        ["SulphurPercent"] = entity.SulphurPercent,
        ["FlashPointC"] = entity.FlashPointC,
        ["WaterContentPercent"] = entity.WaterContentPercent,
        ["OctaneOrCetaneNumber"] = entity.OctaneOrCetaneNumber,
        ["AdditionalSpecifications"] = entity.AdditionalSpecifications,
        ["Description"] = entity.Description,
        ["RejectionReason"] = entity.RejectionReason
    };

    private static string BuildUpdateDiff(
        IReadOnlyDictionary<string, object?> before,
        IReadOnlyDictionary<string, object?> after)
        => AuditDiffFormatter.ForUpdate(
            before.Select(pair => (pair.Key, pair.Value, after.GetValueOrDefault(pair.Key))).ToArray());

    private static string? Clean(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private string? TryGetLocalReturnUrl(string? returnUrl)
        => !string.IsNullOrWhiteSpace(returnUrl) && Url?.IsLocalUrl(returnUrl) == true ? returnUrl : null;

    private string GetWebRootPath()
        => string.IsNullOrWhiteSpace(_environment.WebRootPath)
            ? Path.Combine(_environment.ContentRootPath, "wwwroot")
            : _environment.WebRootPath;

    private async Task PopulateLookupsAsync(QualityInspectionFormViewModel model, CancellationToken ct)
    {
        ViewBag.Products = new SelectList(
            await _db.Products.AsNoTracking().OrderBy(p => p.Name)
                .Select(p => new { p.Id, p.Name }).ToListAsync(ct),
            "Id", "Name", model.ProductId);

        ViewBag.Companies = new SelectList(
            await _db.Companies.AsNoTracking().Where(c => c.IsActive).OrderBy(c => c.Name)
                .Select(c => new { c.Id, c.Name }).ToListAsync(ct),
            "Id", "Name", model.CompanyId);

        ViewBag.Contracts = new SelectList(
            await _db.Contracts.AsNoTracking().OrderByDescending(c => c.ContractDate)
                .Select(c => new { c.Id, Name = c.ContractNumber }).Take(500).ToListAsync(ct),
            "Id", "Name", model.ContractId);

        ViewBag.Shipments = new SelectList(
            await _db.Shipments.AsNoTracking().OrderByDescending(s => s.Id)
                .Select(s => new { s.Id, Name = s.ShipmentCode }).Take(500).ToListAsync(ct),
            "Id", "Name", model.ShipmentId);

        ViewBag.CustomsDeclarations = new SelectList(
            await _db.CustomsDeclarations.AsNoTracking().OrderByDescending(c => c.DeclarationDate)
                .Select(c => new
                {
                    c.Id,
                    Name = c.DeclarationReference ?? ("#" + c.Id)
                })
                .Take(500).ToListAsync(ct),
            "Id", "Name", model.CustomsDeclarationId);
    }
}
