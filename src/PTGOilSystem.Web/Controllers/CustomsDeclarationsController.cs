using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Helpers;
using PTGOilSystem.Web.Models.Customs;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Security;
using System.ComponentModel.DataAnnotations;
using PTGOilSystem.Web.Services.Time;

namespace PTGOilSystem.Web.Controllers;

[Authorize]
public partial class CustomsDeclarationsController : Controller
{
    private readonly ApplicationDbContext _db;
    private readonly ILogger<CustomsDeclarationsController> _logger;
    private readonly IWebHostEnvironment _environment;

    private const long MaxDocumentBytes = 10 * 1024 * 1024; // 10 MB
    private static readonly HashSet<string> AllowedDocumentExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".pdf", ".jpg", ".jpeg", ".png", ".webp" };

    private readonly IAfghanistanBusinessClock _businessClock;

    public CustomsDeclarationsController(
        ApplicationDbContext db,
        ILogger<CustomsDeclarationsController> logger,
        IWebHostEnvironment environment,
        IAfghanistanBusinessClock? businessClock = null)
    {
        // مرجع «امروزِ کاری» همیشه ساعت کابل است، نه تاریخ UTC سرور.
        _businessClock = businessClock ?? new AfghanistanBusinessClock(TimeProvider.System);
        _db = db;
        _logger = logger;
        _environment = environment;
    }

    private bool TryGetLocalReturnUrl(string? returnUrl, out string local)
    {
        if (!string.IsNullOrWhiteSpace(returnUrl) && Url?.IsLocalUrl(returnUrl) == true)
        { local = returnUrl; return true; }
        local = string.Empty; return false;
    }

    // GET: /CustomsDeclarations?loadingRegisterId=X&transportLegId=Y&page=1
    public async Task<IActionResult> Index(
        int? loadingRegisterId = null,
        int? transportLegId = null,
        int? truckDispatchId = null,
        string? q = null,
        DateTime? fromDate = null,
        DateTime? toDate = null,
        int page = 1,
        [FromQuery(Name = "pageSize")] int? perPage = null)
    {
        var pageSize = ListPageSize.Resolve(perPage, 5);
        ViewData["PageSize"] = pageSize;
        ViewData["DefaultPageSize"] = 5;
        var exportAll = page <= 0;
        var normalizedQuery = string.IsNullOrWhiteSpace(q) ? null : q.Trim();

        var query = _db.CustomsDeclarations
            .AsNoTracking()
            .AsQueryable();

        if (loadingRegisterId.HasValue)
            query = query.Where(cd => cd.LoadingRegisterId == loadingRegisterId.Value);
        if (transportLegId.HasValue)
            query = query.Where(cd => cd.TransportLegId == transportLegId.Value);
        if (truckDispatchId.HasValue)
            query = query.Where(cd => cd.TruckDispatchId == truckDispatchId.Value);
        if (fromDate.HasValue)
            query = query.Where(cd => cd.DeclarationDate >= fromDate.Value.Date);
        if (toDate.HasValue)
        {
            var exclusiveToDate = toDate.Value.Date.AddDays(1);
            query = query.Where(cd => cd.DeclarationDate < exclusiveToDate);
        }
        if (!string.IsNullOrWhiteSpace(normalizedQuery))
        {
            query = query.Where(cd =>
                (cd.DeclarationReference != null && cd.DeclarationReference.Contains(normalizedQuery))
                || (cd.WagonOrTruckNumber != null && cd.WagonOrTruckNumber.Contains(normalizedQuery))
                || (cd.LoadingRegister != null
                    && cd.LoadingRegister.Contract != null
                    && cd.LoadingRegister.Contract.ContractNumber.Contains(normalizedQuery))
                || (cd.LoadingRegister != null
                    && cd.LoadingRegister.Product != null
                    && cd.LoadingRegister.Product.Name.Contains(normalizedQuery))
                || (cd.TransportLeg != null
                    && cd.TransportLeg.SourcePurchaseContract != null
                    && cd.TransportLeg.SourcePurchaseContract.ContractNumber.Contains(normalizedQuery))
                || (cd.TransportLeg != null
                    && cd.TransportLeg.Product != null
                    && cd.TransportLeg.Product.Name.Contains(normalizedQuery))
                || (cd.TruckDispatch != null
                    && cd.TruckDispatch.Contract != null
                    && cd.TruckDispatch.Contract.ContractNumber.Contains(normalizedQuery))
                || (cd.TruckDispatch != null
                    && cd.TruckDispatch.Product != null
                    && cd.TruckDispatch.Product.Name.Contains(normalizedQuery)));
        }

        var totalCount = await query.CountAsync();
        var pageCount = Math.Max(1, (int)Math.Ceiling((double)totalCount / pageSize));
        page = Math.Clamp(page, 1, pageCount);

        var items = await query
            .OrderByDescending(cd => cd.DeclarationDate)
            .ThenByDescending(cd => cd.Id)
            .Skip(exportAll ? 0 : (page - 1) * pageSize)
            .Take(exportAll ? totalCount : pageSize)
            .Select(cd => new CustomsDeclarationListItemViewModel
            {
                Id = cd.Id,
                LoadingRegisterId = cd.LoadingRegisterId,
                TransportLegId = cd.TransportLegId,
                TruckDispatchId = cd.TruckDispatchId,
                SourceLabel = cd.LoadingRegisterId.HasValue
                    ? "Loading #" + cd.LoadingRegisterId.Value
                    : (cd.TransportLegId.HasValue
                        ? "Transport Leg #" + cd.TransportLegId.Value
                        : (cd.TruckDispatchId.HasValue ? "Truck Dispatch #" + cd.TruckDispatchId.Value : "—")),
                DeclarationDate = cd.DeclarationDate,
                WagonOrTruckNumber = cd.WagonOrTruckNumber,
                DeclarationReference = cd.DeclarationReference,
                ConsignmentWeightMt = cd.ConsignmentWeightMt,
                TotalAfn = cd.TotalAfn,
                TotalUsd = cd.TotalUsd,
                RatePerMtAfn = cd.RatePerMtAfn,
                RatePerMtUsd = cd.RatePerMtUsd,
                ContractNumber = cd.LoadingRegister != null && cd.LoadingRegister.Contract != null
                    ? cd.LoadingRegister.Contract.ContractNumber
                    : (cd.TransportLeg != null && cd.TransportLeg.SourcePurchaseContract != null
                        ? cd.TransportLeg.SourcePurchaseContract.ContractNumber
                        : (cd.TruckDispatch != null && cd.TruckDispatch.Contract != null
                            ? cd.TruckDispatch.Contract.ContractNumber
                            : "")),
                ProductName = cd.LoadingRegister != null && cd.LoadingRegister.Product != null
                    ? cd.LoadingRegister.Product.Name
                    : (cd.TransportLeg != null && cd.TransportLeg.Product != null
                        ? cd.TransportLeg.Product.Name
                        : (cd.TruckDispatch != null && cd.TruckDispatch.Product != null ? cd.TruckDispatch.Product.Name : ""))
            })
            .ToListAsync();

        // مجموع کلِ همهٔ رکوردهای مطابق فیلتر (نه فقط این صفحه) برای ردیف جمع در انتهای لیست.
        ViewBag.SumWeight = await query.SumAsync(cd => cd.ConsignmentWeightMt ?? 0m);
        ViewBag.SumUsd = await query.SumAsync(cd => cd.TotalUsd);
        ViewBag.SumAfn = await query.SumAsync(cd => cd.TotalAfn);

        string label = "";
        string transportLabel = "";
        string truckDispatchLabel = "";
        if (loadingRegisterId.HasValue)
        {
            var lr = await _db.LoadingRegisters.AsNoTracking()
                .FirstOrDefaultAsync(l => l.Id == loadingRegisterId.Value);
            if (lr != null)
                label = $"بارگیری #{lr.Id} — {DateDisplay.Date(lr.LoadingDate)} — {lr.WagonNumber ?? lr.BillOfLadingNumber}";
        }

        if (transportLegId.HasValue)
        {
            var leg = await _db.InventoryTransportLegs.AsNoTracking()
                .FirstOrDefaultAsync(l => l.Id == transportLegId.Value);
            if (leg != null)
                transportLabel = $"انتقال از موجودی #{leg.Id} — {DateDisplay.Date(leg.LoadedDate)} — {leg.WagonNumber ?? leg.RwbNo ?? leg.BillOfLadingNumber}";
        }

        if (truckDispatchId.HasValue)
        {
            var dispatch = await _db.TruckDispatches.AsNoTracking()
                .Include(d => d.Truck)
                .FirstOrDefaultAsync(d => d.Id == truckDispatchId.Value);
            if (dispatch != null)
                truckDispatchLabel = $"ارسال با موتر #{dispatch.Id} — {DateDisplay.Date(dispatch.DispatchDate)} — {dispatch.Truck?.PlateNumber}";
        }

        return View(new CustomsDeclarationIndexViewModel
        {
            LoadingRegisterId = loadingRegisterId,
            TransportLegId = transportLegId,
            TruckDispatchId = truckDispatchId,
            LoadingRegisterLabel = label,
            TransportLegLabel = transportLabel,
            TruckDispatchLabel = truckDispatchLabel,
            Items = items,
            CurrentPage = page,
            PageCount = pageCount,
            TotalCount = totalCount,
            Query = normalizedQuery,
            FromDate = fromDate,
            ToDate = toDate
        });
    }

    // GET: /CustomsDeclarations/Details/5
    public async Task<IActionResult> Details(int id, string? returnUrl = null)
    {
        var cd = await _db.CustomsDeclarations
            .AsNoTracking()
            .Include(c => c.Items)
            .Include(c => c.Documents)
            .Include(c => c.LoadingRegister).ThenInclude(lr => lr!.Contract)
            .Include(c => c.LoadingRegister).ThenInclude(lr => lr!.Product)
            .Include(c => c.TransportLeg).ThenInclude(l => l!.SourcePurchaseContract)
            .Include(c => c.TransportLeg).ThenInclude(l => l!.Product)
            .Include(c => c.TruckDispatch).ThenInclude(d => d!.Contract)
            .Include(c => c.TruckDispatch).ThenInclude(d => d!.Product)
            .Include(c => c.TruckDispatch).ThenInclude(d => d!.Truck)
            .AsSplitQuery()
            .FirstOrDefaultAsync(c => c.Id == id);

        if (cd is null) return NotFound();

        var vm = new CustomsDeclarationDetailsViewModel
        {
            Id = cd.Id,
            LoadingRegisterId = cd.LoadingRegisterId,
            TransportLegId = cd.TransportLegId,
            TruckDispatchId = cd.TruckDispatchId,
            SourceLabel = BuildSourceLabel(cd.LoadingRegisterId, cd.TransportLegId, cd.TruckDispatchId),
            DeclarationDate = cd.DeclarationDate,
            WagonOrTruckNumber = cd.WagonOrTruckNumber,
            DeclarationReference = cd.DeclarationReference,
            ConsignmentWeightMt = cd.ConsignmentWeightMt,
            TotalAfn = cd.TotalAfn,
            TotalUsd = cd.TotalUsd,
            RatePerMtAfn = cd.RatePerMtAfn,
            RatePerMtUsd = cd.RatePerMtUsd,
            Notes = cd.Notes,
            ContractNumber = cd.LoadingRegister?.Contract?.ContractNumber
                ?? cd.TransportLeg?.SourcePurchaseContract?.ContractNumber
                ?? cd.TruckDispatch?.Contract?.ContractNumber
                ?? "",
            ProductName = cd.LoadingRegister?.Product?.Name ?? cd.TransportLeg?.Product?.Name ?? cd.TruckDispatch?.Product?.Name ?? "",
            WagonNumber = cd.LoadingRegister?.WagonNumber ?? cd.TransportLeg?.WagonNumber ?? cd.TruckDispatch?.Truck?.PlateNumber,
            Items = cd.Items.OrderBy(i => i.ComponentType).Select(i => new CustomsDeclarationItemDetailViewModel
            {
                Id = i.Id,
                ComponentLabel = GetLabel(i.ComponentType, i.CustomLabel),
                AmountAfn = i.AmountAfn,
                AmountUsd = i.AmountUsd,
                Notes = i.Notes
            }).ToList(),
            Documents = cd.Documents
                .OrderByDescending(d => d.UploadedAt)
                .ThenByDescending(d => d.Id)
                .Select(d => new CustomsDeclarationDocumentViewModel
                {
                    Id = d.Id,
                    DocumentType = d.DocumentType,
                    OriginalFileName = d.OriginalFileName,
                    FilePath = d.FilePath,
                    ContentType = d.ContentType,
                    FileSizeBytes = d.FileSizeBytes,
                    Notes = d.Notes,
                    UploadedAt = d.UploadedAt,
                    UploadedByUserName = d.UploadedByUserName
                })
                .ToList()
        };

        ViewBag.ReturnUrl = TryGetLocalReturnUrl(returnUrl, out var localReturnUrl) ? localReturnUrl : null;

        return View(vm);
    }

    // POST: /CustomsDeclarations/UploadDocument/5
    [HttpPost, ValidateAntiForgeryToken]
    [Authorize(Policy = AuthPolicies.ManageData)]
    public async Task<IActionResult> UploadDocument(int id, IFormFile? file, string? documentType, string? notes)
    {
        var exists = await _db.CustomsDeclarations.AnyAsync(c => c.Id == id);
        if (!exists)
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

        var relativeDirectory = Path.Combine("uploads", "customs-declarations", id.ToString());
        var absoluteDirectory = Path.Combine(GetWebRootPath(), relativeDirectory);
        Directory.CreateDirectory(absoluteDirectory);

        var storedFileName = $"{Guid.NewGuid():N}{extension.ToLowerInvariant()}";
        var absolutePath = Path.Combine(absoluteDirectory, storedFileName);
        await using (var stream = System.IO.File.Create(absolutePath))
        {
            await file.CopyToAsync(stream);
        }

        _db.CustomsDeclarationDocuments.Add(new CustomsDeclarationDocument
        {
            CustomsDeclarationId = id,
            DocumentType = string.IsNullOrWhiteSpace(documentType) ? null : documentType.Trim(),
            OriginalFileName = Path.GetFileName(file.FileName),
            StoredFileName = storedFileName,
            FilePath = "/" + relativeDirectory.Replace('\\', '/') + "/" + storedFileName,
            ContentType = file.ContentType,
            FileSizeBytes = file.Length,
            Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim(),
            UploadedAt = DateTime.UtcNow,
            UploadedByUserName = User.Identity?.Name
        });
        await _db.SaveChangesAsync();

        TempData["ok"] = "سند با موفقیت آپلود شد.";
        return RedirectToAction(nameof(Details), new { id });
    }

    // GET: /CustomsDeclarations/DownloadDocument/12
    public async Task<IActionResult> DownloadDocument(int id)
    {
        var document = await _db.CustomsDeclarationDocuments
            .AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == id);
        if (document is null)
        {
            return NotFound();
        }

        var relative = document.FilePath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        var absolutePath = Path.Combine(GetWebRootPath(), relative);
        if (!System.IO.File.Exists(absolutePath))
        {
            return NotFound();
        }

        var contentType = string.IsNullOrWhiteSpace(document.ContentType)
            ? "application/octet-stream"
            : document.ContentType;
        // Inline so PDFs/images open in the browser; keep the original file name.
        Response.Headers["Content-Disposition"] = $"inline; filename=\"{Uri.EscapeDataString(document.OriginalFileName)}\"";
        return PhysicalFile(absolutePath, contentType);
    }

    // POST: /CustomsDeclarations/DeleteDocument/12
    [HttpPost, ValidateAntiForgeryToken]
    [Authorize(Policy = AuthPolicies.ManageData)]
    public async Task<IActionResult> DeleteDocument(int id)
    {
        var document = await _db.CustomsDeclarationDocuments.FirstOrDefaultAsync(d => d.Id == id);
        if (document is null)
        {
            return NotFound();
        }

        var declarationId = document.CustomsDeclarationId;
        var relative = document.FilePath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        var absolutePath = Path.Combine(GetWebRootPath(), relative);
        if (System.IO.File.Exists(absolutePath))
        {
            System.IO.File.Delete(absolutePath);
        }

        _db.CustomsDeclarationDocuments.Remove(document);
        await _db.SaveChangesAsync();

        TempData["ok"] = "سند حذف شد.";
        return RedirectToAction(nameof(Details), new { id = declarationId });
    }

    private string GetWebRootPath()
        => string.IsNullOrWhiteSpace(_environment.WebRootPath)
            ? Path.Combine(_environment.ContentRootPath, "wwwroot")
            : _environment.WebRootPath;

    // GET: /CustomsDeclarations/Create?loadingRegisterId=X
    [Authorize(Policy = AuthPolicies.ManageData)]
    public async Task<IActionResult> Create(int? loadingRegisterId = null, int? transportLegId = null, int? truckDispatchId = null, string? returnUrl = null)
    {
        var sourceCount = (loadingRegisterId.HasValue ? 1 : 0)
            + (transportLegId.HasValue ? 1 : 0)
            + (truckDispatchId.HasValue ? 1 : 0);

        if (sourceCount != 1)
        {
            if (sourceCount > 1)
            {
                ModelState.AddModelError(string.Empty, "برای ثبت اعلامیه فقط یک منبع را انتخاب کنید.");
            }

            var sourceSelectionModel = new CustomsDeclarationCreateViewModel
            {
                DeclarationDate = _businessClock.Today,
                ReturnUrl = TryGetLocalReturnUrl(returnUrl, out var sourceReturnUrl) ? sourceReturnUrl : null
            };
            await PopulateCreateSourceOptionsAsync();
            return View(sourceSelectionModel);
        }

        if (transportLegId.HasValue)
        {
            var leg = await LoadTransportLegSourceAsync(transportLegId.Value);
            if (leg is null) return NotFound();

            var transportModel = new CustomsDeclarationCreateViewModel
            {
                TransportLegId = transportLegId,
                DeclarationDate = _businessClock.Today,
                ReturnUrl = TryGetLocalReturnUrl(returnUrl, out var transportReturnUrl) ? transportReturnUrl : null,
                Items = BuildDefaultItemRows()
            };
            PopulateTransportLegSource(transportModel, leg);
            return View(transportModel);
        }

        if (truckDispatchId.HasValue)
        {
            var dispatch = await LoadTruckDispatchSourceAsync(truckDispatchId.Value);
            if (dispatch is null) return NotFound();

            var dispatchModel = new CustomsDeclarationCreateViewModel
            {
                TruckDispatchId = truckDispatchId,
                DeclarationDate = _businessClock.Today,
                ReturnUrl = TryGetLocalReturnUrl(returnUrl, out var dispatchReturnUrl) ? dispatchReturnUrl : null,
                Items = BuildDefaultItemRows()
            };
            PopulateTruckDispatchSource(dispatchModel, dispatch);
            return View(dispatchModel);
        }

        var requiredLoadingRegisterId = loadingRegisterId!.Value;
        var lr = await _db.LoadingRegisters.AsNoTracking()
            .Include(l => l.Contract)
            .Include(l => l.Product)
            .FirstOrDefaultAsync(l => l.Id == requiredLoadingRegisterId);

        if (lr is null) return NotFound();

        var allTypes = Enum.GetValues<CustomsComponentType>()
            .Where(t => t != CustomsComponentType.Other)
            .OrderBy(t => (int)t)
            .ToList();
        allTypes.Add(CustomsComponentType.Other);

        var model = new CustomsDeclarationCreateViewModel
        {
            LoadingRegisterId = loadingRegisterId,
            DeclarationDate = _businessClock.Today,
            WagonOrTruckNumber = lr.WagonNumber,
            ConsignmentWeightMt = lr.ChargeableQuantityMt ?? lr.LoadedQuantityMt,
            LoadingRegisterLabel = $"بارگیری #{lr.Id} — {DateDisplay.Date(lr.LoadingDate)}",
            ContractNumber = lr.Contract?.ContractNumber ?? "",
            ProductName = lr.Product?.Name ?? "",
            WagonNumber = lr.WagonNumber ?? "",
            ReturnUrl = TryGetLocalReturnUrl(returnUrl, out var localReturnUrl) ? localReturnUrl : null,
            Items = allTypes.Select(t => new CustomsDeclarationItemRowViewModel
            {
                ComponentType = t,
                ComponentLabel = GetLabel(t, null),
                Currency = DefaultCurrencyFor(t),
                Amount = 0,
                Rate = null
            }).ToList()
        };

        return View(model);
    }

    // POST: /CustomsDeclarations/Create
    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(CustomsDeclarationCreateViewModel model)
    {
        if (!HasExactlyOneSource(model.LoadingRegisterId, model.TransportLegId, model.TruckDispatchId))
            ModelState.AddModelError(string.Empty, "Customs declaration must reference exactly one source.");

        var lr = model.LoadingRegisterId.HasValue
            ? await LoadLoadingRegisterSourceAsync(model.LoadingRegisterId.Value)
            : null;
        var leg = model.TransportLegId.HasValue
            ? await LoadTransportLegSourceAsync(model.TransportLegId.Value)
            : null;
        var dispatch = model.TruckDispatchId.HasValue
            ? await LoadTruckDispatchSourceAsync(model.TruckDispatchId.Value)
            : null;

        if (model.LoadingRegisterId.HasValue && lr is null)
        {
            ModelState.AddModelError(string.Empty, "رکورد بارگیری مورد نظر وجود ندارد.");
        }

        if (model.TransportLegId.HasValue && leg is null)
        {
            ModelState.AddModelError(string.Empty, "Transport leg مورد نظر وجود ندارد.");
        }

        if (model.TruckDispatchId.HasValue && dispatch is null)
        {
            ModelState.AddModelError(string.Empty, "ارسال با موتر مورد نظر وجود ندارد.");
        }

        if (!ModelState.IsValid)
        {
            if (lr != null)
            {
                model.LoadingRegisterLabel = $"بارگیری #{lr.Id} — {DateDisplay.Date(lr.LoadingDate)}";
                model.ContractNumber = lr.Contract?.ContractNumber ?? "";
                model.ProductName = lr.Product?.Name ?? "";
                model.WagonNumber = lr.WagonNumber ?? "";
            }

            if (leg != null)
            {
                PopulateTransportLegSource(model, leg);
            }

            if (dispatch != null)
            {
                PopulateTruckDispatchSource(model, dispatch);
            }
            if (model.Items.Count == 0)
            {
                model.Items = BuildDefaultItemRows();
            }

            model.ReturnUrl = TryGetLocalReturnUrl(model.ReturnUrl, out var localReturnUrl) ? localReturnUrl : null;
            return View(model);
        }

        // هر ردیف یک مبلغ اصلی + ارز + نرخ دارد؛ معادل ارز دوم را اینجا یک‌بار حساب می‌کنیم و
        // هر دو ستون (AFN/USD) را به‌صورت «معادلِ همان مبلغ» ذخیره می‌کنیم تا در جمع‌ها دوباره حساب نشود.
        var activeItems = model.Items
            .Where(i => i.Amount > 0)
            .Select(i =>
            {
                var (amountAfn, amountUsd) = ConvertItemAmounts(i.Currency, i.Amount, i.Rate);
                return new
                {
                    Row = i,
                    AmountAfn = amountAfn,
                    AmountUsd = amountUsd
                };
            })
            .ToList();

        decimal totalAfn = activeItems.Sum(i => i.AmountAfn);
        decimal totalUsd = activeItems.Sum(i => i.AmountUsd ?? 0);
        decimal? weight = model.ConsignmentWeightMt > 0 ? model.ConsignmentWeightMt : null;

        var cd = new CustomsDeclaration
        {
            LoadingRegisterId = model.LoadingRegisterId,
            TransportLegId = model.TransportLegId,
            TruckDispatchId = model.TruckDispatchId,
            DeclarationDate = model.DeclarationDate,
            WagonOrTruckNumber = string.IsNullOrWhiteSpace(model.WagonOrTruckNumber) ? null : model.WagonOrTruckNumber.Trim(),
            DeclarationReference = string.IsNullOrWhiteSpace(model.DeclarationReference) ? null : model.DeclarationReference.Trim(),
            PermitNumber = string.IsNullOrWhiteSpace(model.PermitNumber) ? null : model.PermitNumber.Trim(),
            PermitHolderName = string.IsNullOrWhiteSpace(model.PermitHolderName) ? null : model.PermitHolderName.Trim(),
            CustomsType = string.IsNullOrWhiteSpace(model.CustomsType) ? null : model.CustomsType.Trim(),
            GoodsName = string.IsNullOrWhiteSpace(model.GoodsName) ? null : model.GoodsName.Trim(),
            Route = string.IsNullOrWhiteSpace(model.Route) ? null : model.Route.Trim(),
            ConsignmentWeightMt = weight,
            TotalAfn = totalAfn,
            TotalUsd = totalUsd,
            RatePerMtAfn = weight > 0 ? totalAfn / weight : null,
            RatePerMtUsd = weight > 0 && totalUsd > 0 ? totalUsd / weight : null,
            Notes = string.IsNullOrWhiteSpace(model.Notes) ? null : model.Notes.Trim(),
            Items = activeItems.Select(i => new CustomsDeclarationItem
            {
                ComponentType = i.Row.ComponentType,
                CustomLabel = string.IsNullOrWhiteSpace(i.Row.ComponentLabel) ? null : i.Row.ComponentLabel.Trim(),
                AmountAfn = i.AmountAfn,
                AmountUsd = i.AmountUsd > 0 ? i.AmountUsd : null,
                Notes = string.IsNullOrWhiteSpace(i.Row.Notes) ? null : i.Row.Notes.Trim()
            }).ToList()
        };

        _db.CustomsDeclarations.Add(cd);
        await _db.SaveChangesAsync();

        TempData["ok"] = $"اعلامیه گمرکی برای واگن/موتر «{cd.WagonOrTruckNumber ?? "—"}» ثبت شد.";

        if (TryGetLocalReturnUrl(model.ReturnUrl, out var localUrl))
            return Redirect(localUrl);

        return RedirectToAction(nameof(Details), new { id = cd.Id });
    }

    // GET: /CustomsDeclarations/Edit/5 — همان فرم ثبت، با اقلامِ بازسازی‌شده برای اصلاح.
    [Authorize(Policy = AuthPolicies.ManageData)]
    public async Task<IActionResult> Edit(int id, string? returnUrl = null)
    {
        var cd = await _db.CustomsDeclarations
            .AsNoTracking()
            .Include(c => c.Items)
            .Include(c => c.LoadingRegister).ThenInclude(lr => lr!.Contract)
            .Include(c => c.LoadingRegister).ThenInclude(lr => lr!.Product)
            .Include(c => c.TransportLeg).ThenInclude(l => l!.SourcePurchaseContract)
            .Include(c => c.TransportLeg).ThenInclude(l => l!.Product)
            .Include(c => c.TruckDispatch).ThenInclude(d => d!.Contract)
            .Include(c => c.TruckDispatch).ThenInclude(d => d!.Product)
            .Include(c => c.TruckDispatch).ThenInclude(d => d!.Truck)
            .AsSplitQuery()
            .FirstOrDefaultAsync(c => c.Id == id);

        if (cd is null) return NotFound();

        var model = new CustomsDeclarationCreateViewModel
        {
            Id = cd.Id,
            LoadingRegisterId = cd.LoadingRegisterId,
            TransportLegId = cd.TransportLegId,
            TruckDispatchId = cd.TruckDispatchId,
            DeclarationDate = cd.DeclarationDate,
            WagonOrTruckNumber = cd.WagonOrTruckNumber,
            DeclarationReference = cd.DeclarationReference,
            PermitNumber = cd.PermitNumber,
            PermitHolderName = cd.PermitHolderName,
            CustomsType = cd.CustomsType,
            GoodsName = cd.GoodsName,
            Route = cd.Route,
            ConsignmentWeightMt = cd.ConsignmentWeightMt,
            Notes = cd.Notes,
            ReturnUrl = TryGetLocalReturnUrl(returnUrl, out var localReturnUrl) ? localReturnUrl : null,
            Items = cd.Items
                .OrderBy(i => i.ComponentType)
                .Select(ReconstructItemRow)
                .ToList()
        };

        if (model.Items.Count == 0)
        {
            model.Items = BuildDefaultItemRows();
        }

        if (cd.LoadingRegister is not null)
        {
            model.LoadingRegisterLabel = $"بارگیری #{cd.LoadingRegister.Id} — {DateDisplay.Date(cd.LoadingRegister.LoadingDate)}";
            model.ContractNumber = cd.LoadingRegister.Contract?.ContractNumber ?? "";
            model.ProductName = cd.LoadingRegister.Product?.Name ?? "";
            model.WagonNumber = cd.LoadingRegister.WagonNumber ?? "";
        }

        if (cd.TransportLeg is not null)
        {
            PopulateTransportLegSource(model, cd.TransportLeg);
            model.WagonOrTruckNumber = cd.WagonOrTruckNumber;
            model.ConsignmentWeightMt = cd.ConsignmentWeightMt;
        }

        if (cd.TruckDispatch is not null)
        {
            PopulateTruckDispatchSource(model, cd.TruckDispatch);
            model.WagonOrTruckNumber = cd.WagonOrTruckNumber;
            model.ConsignmentWeightMt = cd.ConsignmentWeightMt;
        }

        return View("Create", model);
    }

    // POST: /CustomsDeclarations/Edit/5 — به‌روزرسانی اعلامیه و اقلامش با همان منطق محاسبهٔ معادل ارز.
    // هیچ Ledger/Stock/Payment تغییر نمی‌کند؛ مبالغ گمرکی فقط نمایشی/گزارشی هستند.
    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(CustomsDeclarationCreateViewModel model)
    {
        if (model.Id <= 0)
        {
            return BadRequest();
        }

        var cd = await _db.CustomsDeclarations
            .Include(c => c.Items)
            .FirstOrDefaultAsync(c => c.Id == model.Id);

        if (cd is null) return NotFound();

        // منبع از خودِ رکورد گرفته می‌شود؛ کاربر نمی‌تواند منبع را در ویرایش تغییر دهد.
        model.LoadingRegisterId = cd.LoadingRegisterId;
        model.TransportLegId = cd.TransportLegId;
        model.TruckDispatchId = cd.TruckDispatchId;

        var lr = cd.LoadingRegisterId.HasValue
            ? await LoadLoadingRegisterSourceAsync(cd.LoadingRegisterId.Value)
            : null;
        var leg = cd.TransportLegId.HasValue
            ? await LoadTransportLegSourceAsync(cd.TransportLegId.Value)
            : null;
        var dispatch = cd.TruckDispatchId.HasValue
            ? await LoadTruckDispatchSourceAsync(cd.TruckDispatchId.Value)
            : null;

        if (!ModelState.IsValid)
        {
            if (lr != null)
            {
                model.LoadingRegisterLabel = $"بارگیری #{lr.Id} — {DateDisplay.Date(lr.LoadingDate)}";
                model.ContractNumber = lr.Contract?.ContractNumber ?? "";
                model.ProductName = lr.Product?.Name ?? "";
                model.WagonNumber = lr.WagonNumber ?? "";
            }

            if (leg != null)
            {
                PopulateTransportLegSource(model, leg);
            }

            if (dispatch != null)
            {
                PopulateTruckDispatchSource(model, dispatch);
            }

            if (model.Items.Count == 0)
            {
                model.Items = BuildDefaultItemRows();
            }

            model.ReturnUrl = TryGetLocalReturnUrl(model.ReturnUrl, out var invalidReturnUrl) ? invalidReturnUrl : null;
            return View("Create", model);
        }

        var activeItems = model.Items
            .Where(i => i.Amount > 0)
            .Select(i =>
            {
                var (amountAfn, amountUsd) = ConvertItemAmounts(i.Currency, i.Amount, i.Rate);
                return new { Row = i, AmountAfn = amountAfn, AmountUsd = amountUsd };
            })
            .ToList();

        decimal totalAfn = activeItems.Sum(i => i.AmountAfn);
        decimal totalUsd = activeItems.Sum(i => i.AmountUsd ?? 0);
        decimal? weight = model.ConsignmentWeightMt > 0 ? model.ConsignmentWeightMt : null;

        cd.DeclarationDate = model.DeclarationDate;
        cd.WagonOrTruckNumber = string.IsNullOrWhiteSpace(model.WagonOrTruckNumber) ? null : model.WagonOrTruckNumber.Trim();
        cd.DeclarationReference = string.IsNullOrWhiteSpace(model.DeclarationReference) ? null : model.DeclarationReference.Trim();
        cd.PermitNumber = string.IsNullOrWhiteSpace(model.PermitNumber) ? null : model.PermitNumber.Trim();
        cd.PermitHolderName = string.IsNullOrWhiteSpace(model.PermitHolderName) ? null : model.PermitHolderName.Trim();
        cd.CustomsType = string.IsNullOrWhiteSpace(model.CustomsType) ? null : model.CustomsType.Trim();
        cd.GoodsName = string.IsNullOrWhiteSpace(model.GoodsName) ? null : model.GoodsName.Trim();
        cd.Route = string.IsNullOrWhiteSpace(model.Route) ? null : model.Route.Trim();
        cd.ConsignmentWeightMt = weight;
        cd.TotalAfn = totalAfn;
        cd.TotalUsd = totalUsd;
        cd.RatePerMtAfn = weight > 0 ? totalAfn / weight : null;
        cd.RatePerMtUsd = weight > 0 && totalUsd > 0 ? totalUsd / weight : null;
        cd.Notes = string.IsNullOrWhiteSpace(model.Notes) ? null : model.Notes.Trim();
        cd.UpdatedAtUtc = DateTime.UtcNow;

        // اقلام قبلی حذف و با اقلام جدید جایگزین می‌شوند (ساده و بدون وابستگی به Ledger).
        _db.CustomsDeclarationItems.RemoveRange(cd.Items);
        cd.Items = activeItems.Select(i => new CustomsDeclarationItem
        {
            CustomsDeclarationId = cd.Id,
            ComponentType = i.Row.ComponentType,
            CustomLabel = string.IsNullOrWhiteSpace(i.Row.ComponentLabel) ? null : i.Row.ComponentLabel.Trim(),
            AmountAfn = i.AmountAfn,
            AmountUsd = i.AmountUsd > 0 ? i.AmountUsd : null,
            Notes = string.IsNullOrWhiteSpace(i.Row.Notes) ? null : i.Row.Notes.Trim()
        }).ToList();

        await _db.SaveChangesAsync();

        TempData["ok"] = $"اعلامیه گمرکی «{cd.WagonOrTruckNumber ?? "—"}» ویرایش شد.";

        if (TryGetLocalReturnUrl(model.ReturnUrl, out var localUrl))
            return Redirect(localUrl);

        return RedirectToAction(nameof(Details), new { id = cd.Id });
    }

    // POST: /CustomsDeclarations/Delete/5
    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id, string? returnUrl = null)
    {
        var cd = await _db.CustomsDeclarations.FirstOrDefaultAsync(c => c.Id == id);
        if (cd is null) return NotFound();

        int? lrId = cd.LoadingRegisterId;
        int? legId = cd.TransportLegId;
        int? dispatchId = cd.TruckDispatchId;
        _db.CustomsDeclarations.Remove(cd);
        await _db.SaveChangesAsync();
        TempData["ok"] = "اعلامیه گمرکی حذف شد.";

        if (TryGetLocalReturnUrl(returnUrl, out var local)) return Redirect(local);
        return RedirectToAction(nameof(Index), new { loadingRegisterId = lrId, transportLegId = legId, truckDispatchId = dispatchId });
    }

    // POST: /CustomsDeclarations/DeleteAll — لغو همه: فقط اعلامیه‌های مطابق فیلتر فعلیِ لیست را حذف می‌کند.
    // هیچ Ledger/Stock/Payment لمس نمی‌شود (اعلامیه‌های گمرکی صرفاً نمایشی/گزارشی‌اند).
    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteAll(
        int? loadingRegisterId = null,
        int? transportLegId = null,
        int? truckDispatchId = null,
        string? q = null,
        DateTime? fromDate = null,
        DateTime? toDate = null,
        string? returnUrl = null)
    {
        var normalizedQuery = string.IsNullOrWhiteSpace(q) ? null : q.Trim();

        var query = _db.CustomsDeclarations.AsQueryable();
        if (loadingRegisterId.HasValue)
            query = query.Where(cd => cd.LoadingRegisterId == loadingRegisterId.Value);
        if (transportLegId.HasValue)
            query = query.Where(cd => cd.TransportLegId == transportLegId.Value);
        if (truckDispatchId.HasValue)
            query = query.Where(cd => cd.TruckDispatchId == truckDispatchId.Value);
        if (fromDate.HasValue)
            query = query.Where(cd => cd.DeclarationDate >= fromDate.Value.Date);
        if (toDate.HasValue)
        {
            var exclusiveToDate = toDate.Value.Date.AddDays(1);
            query = query.Where(cd => cd.DeclarationDate < exclusiveToDate);
        }
        if (!string.IsNullOrWhiteSpace(normalizedQuery))
        {
            query = query.Where(cd =>
                (cd.DeclarationReference != null && cd.DeclarationReference.Contains(normalizedQuery))
                || (cd.WagonOrTruckNumber != null && cd.WagonOrTruckNumber.Contains(normalizedQuery))
                || (cd.LoadingRegister != null && cd.LoadingRegister.Contract != null
                    && cd.LoadingRegister.Contract.ContractNumber.Contains(normalizedQuery))
                || (cd.LoadingRegister != null && cd.LoadingRegister.Product != null
                    && cd.LoadingRegister.Product.Name.Contains(normalizedQuery))
                || (cd.TransportLeg != null && cd.TransportLeg.SourcePurchaseContract != null
                    && cd.TransportLeg.SourcePurchaseContract.ContractNumber.Contains(normalizedQuery))
                || (cd.TransportLeg != null && cd.TransportLeg.Product != null
                    && cd.TransportLeg.Product.Name.Contains(normalizedQuery))
                || (cd.TruckDispatch != null && cd.TruckDispatch.Contract != null
                    && cd.TruckDispatch.Contract.ContractNumber.Contains(normalizedQuery))
                || (cd.TruckDispatch != null && cd.TruckDispatch.Product != null
                    && cd.TruckDispatch.Product.Name.Contains(normalizedQuery)));
        }

        // دو Include مجموعه‌ای در یک کوئری = ضرب دکارتی (Items × Documents).
        // AsSplitQuery هر مجموعه را در کوئری جدا می‌خواند؛ ردیف‌های بارگذاری‌شده تغییری نمی‌کنند.
        var toDelete = await query
            .Include(c => c.Items)
            .Include(c => c.Documents)
            .AsSplitQuery()
            .ToListAsync();

        if (toDelete.Count == 0)
        {
            TempData["error"] = "هیچ اعلامیه‌ای برای لغو در این فیلتر یافت نشد.";
        }
        else
        {
            _db.CustomsDeclarations.RemoveRange(toDelete);
            await _db.SaveChangesAsync();
            TempData["ok"] = $"{toDelete.Count} اعلامیه گمرکی لغو شد.";
        }

        if (TryGetLocalReturnUrl(returnUrl, out var local)) return Redirect(local);
        return RedirectToAction(nameof(Index), new { loadingRegisterId, transportLegId, truckDispatchId, q, fromDate, toDate });
    }

    // GET: /CustomsDeclarations/PerWagonPnl?contractId=X
    public async Task<IActionResult> PerWagonPnl(int contractId)
    {
        var contract = await _db.Contracts.AsNoTracking()
            .Include(c => c.Product)
            .FirstOrDefaultAsync(c => c.Id == contractId);
        if (contract is null) return NotFound();

        var loadingRegisters = await _db.LoadingRegisters
            .AsNoTracking()
            .Where(lr => lr.ContractId == contractId)
            .OrderBy(lr => lr.LoadingDate)
            .ThenBy(lr => lr.Id)
            .ToListAsync();

        var lrIds = loadingRegisters.Select(lr => lr.Id).ToList();

        var customsTotals = await _db.CustomsDeclarations
            .AsNoTracking()
            .Where(cd => cd.LoadingRegisterId.HasValue && lrIds.Contains(cd.LoadingRegisterId.Value))
            .GroupBy(cd => cd.LoadingRegisterId!.Value)
            .Select(g => new { LoadingRegisterId = g.Key, TotalAfn = g.Sum(x => x.TotalAfn), TotalUsd = g.Sum(x => x.TotalUsd), Count = g.Count() })
            .ToListAsync();

        var rows = loadingRegisters.Select(lr =>
        {
            var ct = customsTotals.FirstOrDefault(c => c.LoadingRegisterId == lr.Id);
            decimal purchaseCost = (lr.ChargeableQuantityMt ?? lr.LoadedQuantityMt)
                * (lr.LoadingPriceUsd ?? 0)
                + (lr.TransportExpenseUsd ?? 0)
                + (lr.WarehouseExpenseUsd ?? 0)
                + (lr.OtherExpenseUsd ?? 0)
                + (lr.RailwayExpenseUsd ?? 0);
            decimal? customsUsd = ct?.TotalUsd;

            return new PerWagonPnlRowViewModel
            {
                LoadingRegisterId = lr.Id,
                LoadingDate = lr.LoadingDate,
                WagonNumber = lr.WagonNumber,
                LoadedQuantityMt = lr.LoadedQuantityMt,
                ChargeableQuantityMt = lr.ChargeableQuantityMt,
                LoadingPriceUsd = lr.LoadingPriceUsd,
                PurchaseCostUsd = purchaseCost,
                TotalCustomsAfn = ct?.TotalAfn,
                TotalCustomsUsd = ct?.TotalUsd,
                RailwayExpenseUsd = lr.RailwayExpenseUsd,
                OtherExpenseUsd = lr.OtherExpenseUsd,
                GrossMarginUsd = null,
                MarginPerMtUsd = null,
                CustomsDeclarationCount = ct?.Count ?? 0
            };
        }).ToList();

        ViewBag.ContractNumber = contract.ContractNumber;
        ViewBag.ProductName = contract.Product?.Name ?? "";
        ViewBag.ContractId = contractId;

        return View(rows);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // اپلود اکسل گروهی گمرک: هر ردیف با نمبر پلیت/سیمیر به عملیاتِ «در جریان»
    // (نه تکمیل‌شده) تطبیق می‌شود و یک اعلامیهٔ گمرکی برایش ثبت می‌گردد.
    // موجودی = پلیت + سیمیر، ارسال موتر = فقط پلیت. رکوردهای مالی/Ledger لمس نمی‌شوند.
    // ─────────────────────────────────────────────────────────────────────────
    [Authorize(Policy = AuthPolicies.ManageData)]
    public IActionResult ImportExcel(CustomsImportScope scope = CustomsImportScope.InventoryTransport, string? returnUrl = null)
    {
        return View(new CustomsImportViewModel
        {
            Scope = scope,
            ReturnUrl = TryGetLocalReturnUrl(returnUrl, out var local) ? local : null
        });
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ImportExcel(CustomsImportViewModel model, IFormFile? file, CancellationToken ct = default)
    {
        model.ReturnUrl = TryGetLocalReturnUrl(model.ReturnUrl, out var local) ? local : null;
        var isAjax = string.Equals(Request.Headers["X-Requested-With"], "XMLHttpRequest", StringComparison.OrdinalIgnoreCase);

        // خطای اعتبارسنجی/ثبت: در حالت AJAX به‌صورت JSON، در حالت عادی به‌صورت View برمی‌گردد.
        IActionResult Reject(string message, string key = "")
        {
            if (isAjax) return Json(new { ok = false, message });
            ModelState.AddModelError(key, message);
            return View(model);
        }

        if (file is null || file.Length == 0)
            return Reject("فایلی انتخاب نشده است.");
        if (file.Length > 5 * 1024 * 1024)
            return Reject("حجم فایل زیاد است (حداکثر ۵ مگابایت).");
        if (!(model.FxRateToUsd > 0m))
            return Reject("نرخ تبدیل افغانی به دالر را وارد کنید.", nameof(model.FxRateToUsd));
        var fxRate = model.FxRateToUsd!.Value;

        IReadOnlyList<CustomsBatchImportRow> parsed;
        try
        {
            await using var workbook = await ExcelWorkbookNormalizer.OpenAsync(file, ct);
            parsed = CustomsBatchWorkbookParser.Parse(workbook.Stream);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Customs batch import failed to parse.");
            return Reject("خواندن فایل اکسل ناموفق بود: " + ex.Message);
        }

        if (parsed.Count == 0)
            return Reject("در فایل هیچ ردیف معتبری (نمبر پلیت یا سیمیر) یافت نشد.");

        var (resultRows, toSave) = await MatchCustomsImportAsync(model.Scope, fxRate, parsed, ct);

        if (toSave.Count > 0)
        {
            IDbContextTransaction? transaction = null;
            if (_db.Database.IsRelational())
                transaction = await _db.Database.BeginTransactionAsync(ct);
            try
            {
                _db.CustomsDeclarations.AddRange(toSave);
                await _db.SaveChangesAsync(ct);
                if (transaction is not null) await transaction.CommitAsync(ct);
            }
            catch (Exception ex)
            {
                if (transaction is not null) await transaction.RollbackAsync(ct);
                _logger.LogError(ex, "Failed to save customs batch import.");
                return Reject("خطا در ثبت اعلامیه‌ها. دوباره تلاش کنید.");
            }

            // Id رکوردهای ثبت‌شده را به ردیف‌های موفق تطبیق می‌دهیم (به ترتیب).
            var savedIds = new Queue<int>(toSave.Select(c => c.Id));
            for (var i = 0; i < resultRows.Count; i++)
            {
                if (resultRows[i].Matched && savedIds.Count > 0)
                {
                    resultRows[i] = resultRows[i] with { DeclarationId = savedIds.Dequeue() };
                }
            }
        }

        model.HasResult = true;
        model.Rows = resultRows;
        model.SavedCount = resultRows.Count(r => r.Matched);
        model.SkippedCount = resultRows.Count(r => !r.Matched);

        if (isAjax)
        {
            return Json(new
            {
                ok = true,
                savedCount = model.SavedCount,
                skippedCount = model.SkippedCount,
                rows = resultRows.Select(ToCustomsImportJsonRow)
            });
        }

        return View(model);
    }

    // پیش‌نمایش (بدون ثبت): فایل خوانده و با عملیاتِ در جریان تطبیق می‌شود و نتیجه به‌صورت JSON
    // برمی‌گردد تا کاربر قبل از ثبت نهایی ببیند چه ردیف‌هایی ثبت و چه ردیف‌هایی رد می‌شوند.
    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ImportExcelPreview(CustomsImportViewModel model, IFormFile? file, CancellationToken ct = default)
    {
        if (file is null || file.Length == 0)
            return Json(new { ok = false, message = "فایلی انتخاب نشده است." });
        if (file.Length > 5 * 1024 * 1024)
            return Json(new { ok = false, message = "حجم فایل زیاد است (حداکثر ۵ مگابایت)." });
        if (!(model.FxRateToUsd > 0m))
            return Json(new { ok = false, message = "نرخ تبدیل افغانی به دالر را وارد کنید." });
        var fxRate = model.FxRateToUsd!.Value;

        IReadOnlyList<CustomsBatchImportRow> parsed;
        try
        {
            await using var workbook = await ExcelWorkbookNormalizer.OpenAsync(file, ct);
            parsed = CustomsBatchWorkbookParser.Parse(workbook.Stream);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Customs batch import preview failed to parse.");
            return Json(new { ok = false, message = "خواندن فایل اکسل ناموفق بود: " + ex.Message });
        }
        if (parsed.Count == 0)
            return Json(new { ok = false, message = "در فایل هیچ ردیف معتبری (نمبر پلیت یا سیمیر) یافت نشد." });

        var (rows, _) = await MatchCustomsImportAsync(model.Scope, fxRate, parsed, ct);
        return Json(new
        {
            ok = true,
            savedCount = rows.Count(r => r.Matched),
            skippedCount = rows.Count(r => !r.Matched),
            rows = rows.Select(ToCustomsImportJsonRow)
        });
    }

    private static object ToCustomsImportJsonRow(CustomsImportResultRow r) => new
    {
        rowNumber = r.RowNumber,
        simir = r.Simir,
        plate = r.PlateNumber,
        weight = r.WeightMt,
        afn = r.TotalAfn,
        usd = r.TotalUsd,
        matched = r.Matched,
        label = r.MatchedLabel,
        skip = r.SkipReason,
        declarationId = r.DeclarationId
    };

    // هر ردیف اکسل با یک عملیاتِ «در جریان» (نه تکمیل‌شده/لغوشده) تطبیق داده می‌شود:
    // حمل موجودی = پلیت + سیمیر، ارسال موتر = فقط پلیت. خروجی: ردیف‌های نتیجه + اعلامیه‌های آمادهٔ ثبت.
    private async Task<(List<CustomsImportResultRow> Rows, List<CustomsDeclaration> ToSave)> MatchCustomsImportAsync(
        CustomsImportScope scope, decimal fxRate, IReadOnlyList<CustomsBatchImportRow> parsed, CancellationToken ct)
    {
        var legs = scope == CustomsImportScope.InventoryTransport
            ? await _db.InventoryTransportLegs.AsNoTracking()
                .Where(l => l.Status != InventoryTransportLegStatus.Received
                    && l.Status != InventoryTransportLegStatus.Cancelled)
                .Include(l => l.Truck)
                .Select(l => new { l.Id, l.LoadedDate, Plate = l.Truck != null ? l.Truck.PlateNumber : null, l.WagonNumber, l.RwbNo })
                .ToListAsync(ct)
            : null;

        var dispatches = scope == CustomsImportScope.TruckDispatch
            ? await _db.TruckDispatches.AsNoTracking()
                .Where(d => d.Status != DispatchStatus.Delivered && d.Status != DispatchStatus.Cancelled)
                .Include(d => d.Truck)
                .Select(d => new { d.Id, d.DispatchDate, Plate = d.Truck != null ? d.Truck.PlateNumber : null })
                .ToListAsync(ct)
            : null;

        var resultRows = new List<CustomsImportResultRow>();
        var toSave = new List<CustomsDeclaration>();

        foreach (var row in parsed)
        {
            var (totalAfn, totalUsd, items) = BuildImportItems(row, fxRate);
            decimal? weight = row.WeightMt is > 0m ? row.WeightMt : null;

            int? matchedId = null;
            string? matchedLabel = null;
            string? skip = null;

            if (scope == CustomsImportScope.InventoryTransport)
            {
                if (string.IsNullOrWhiteSpace(row.PlateNumber) || string.IsNullOrWhiteSpace(row.Simir))
                {
                    skip = "برای حمل موجودی هم نمبر پلیت و هم سیمیر لازم است.";
                }
                else
                {
                    var plate = NormKey(row.PlateNumber);
                    var simir = NormKey(row.Simir);
                    var hits = legs!.Where(l =>
                        NormKey(l.RwbNo) == simir
                        && (NormKey(l.Plate) == plate || NormKey(l.WagonNumber) == plate)).ToList();
                    if (hits.Count == 1)
                    {
                        matchedId = hits[0].Id;
                        matchedLabel = $"حمل موجودی #{hits[0].Id} — {DateDisplay.Date(hits[0].LoadedDate)}";
                    }
                    else
                    {
                        skip = hits.Count == 0 ? "حمل موجودی جاری با این پلیت+سیمیر یافت نشد." : $"{hits.Count} تطبیق پیدا شد (مبهم).";
                    }
                }
            }
            else
            {
                if (string.IsNullOrWhiteSpace(row.PlateNumber))
                {
                    skip = "نمبر پلیت خالی است.";
                }
                else
                {
                    var plate = NormKey(row.PlateNumber);
                    var hits = dispatches!.Where(d => NormKey(d.Plate) == plate).ToList();
                    if (hits.Count == 1)
                    {
                        matchedId = hits[0].Id;
                        matchedLabel = $"ارسال موتر #{hits[0].Id} — {DateDisplay.Date(hits[0].DispatchDate)}";
                    }
                    else
                    {
                        skip = hits.Count == 0 ? "ارسال موتر جاری با این پلیت یافت نشد." : $"{hits.Count} تطبیق پیدا شد (مبهم).";
                    }
                }
            }

            if (matchedId is null || items.Count == 0)
            {
                if (matchedId is not null && items.Count == 0)
                {
                    skip = "هیچ مبلغ گمرکی مثبتی در این ردیف نبود.";
                }
                resultRows.Add(new CustomsImportResultRow
                {
                    RowNumber = row.RowNumber,
                    Simir = row.Simir,
                    PlateNumber = row.PlateNumber,
                    WeightMt = weight,
                    TotalAfn = totalAfn,
                    TotalUsd = totalUsd,
                    Matched = false,
                    SkipReason = skip
                });
                continue;
            }

            var cd = new CustomsDeclaration
            {
                TransportLegId = scope == CustomsImportScope.InventoryTransport ? matchedId : null,
                TruckDispatchId = scope == CustomsImportScope.TruckDispatch ? matchedId : null,
                DeclarationDate = row.Date?.Date ?? _businessClock.Today,
                WagonOrTruckNumber = row.PlateNumber,
                DeclarationReference = row.Simir,
                Route = row.Destination,
                ConsignmentWeightMt = weight,
                TotalAfn = totalAfn,
                TotalUsd = totalUsd,
                RatePerMtAfn = weight > 0 ? totalAfn / weight : null,
                RatePerMtUsd = weight > 0 && totalUsd > 0 ? totalUsd / weight : null,
                Items = items
            };
            toSave.Add(cd);

            resultRows.Add(new CustomsImportResultRow
            {
                RowNumber = row.RowNumber,
                Simir = row.Simir,
                PlateNumber = row.PlateNumber,
                WeightMt = weight,
                TotalAfn = totalAfn,
                TotalUsd = totalUsd,
                Matched = true,
                MatchedLabel = matchedLabel
            });
        }

        return (resultRows, toSave);
    }

    // همه مبالغِ ردیف افغانی هستند؛ هر کدام با نرخِ واردشده (AFN به ازای هر ۱ USD) به دالر تبدیل
    // و هر دو مقدار (AFN اصلی + معادل USD) ذخیره می‌شوند. ستون «مصرف محصول به دالر» رهنماست و اینجا نیست.
    private static (decimal TotalAfn, decimal TotalUsd, List<CustomsDeclarationItem> Items) BuildImportItems(
        CustomsBatchImportRow row, decimal fxRate)
    {
        var items = new List<CustomsDeclarationItem>();
        decimal totalAfn = 0m, totalUsd = 0m;
        foreach (var a in row.Amounts)
        {
            var amountUsd = decimal.Round(a.Amount / fxRate, 2, MidpointRounding.AwayFromZero);
            totalAfn += a.Amount;
            totalUsd += amountUsd;
            items.Add(new CustomsDeclarationItem
            {
                ComponentType = a.ComponentType,
                AmountAfn = a.Amount,
                AmountUsd = amountUsd
            });
        }
        return (totalAfn, totalUsd, items);
    }

    // کلید تطبیق: فقط حروف/ارقام، بدون فاصله و علائم، حروف کوچک.
    private static string NormKey(string? value)
        => new((value ?? string.Empty).Trim().ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    private static bool HasExactlyOneSource(int? loadingRegisterId, int? transportLegId, int? truckDispatchId)
        => ((loadingRegisterId.HasValue ? 1 : 0)
            + (transportLegId.HasValue ? 1 : 0)
            + (truckDispatchId.HasValue ? 1 : 0)) == 1;

    private async Task PopulateCreateSourceOptionsAsync()
    {
        var loadingSources = await _db.LoadingRegisters.AsNoTracking()
            .Include(l => l.Contract)
            .Include(l => l.Product)
            .OrderByDescending(l => l.LoadingDate)
            .ThenByDescending(l => l.Id)
            .Take(150)
            .Select(l => new
            {
                l.Id,
                l.LoadingDate,
                ContractNumber = l.Contract != null ? l.Contract.ContractNumber : "",
                ProductName = l.Product != null ? l.Product.Name : "",
                Reference = l.WagonNumber ?? l.BillOfLadingNumber ?? l.RwbNo
            })
            .ToListAsync();

        var transportSources = await _db.InventoryTransportLegs.AsNoTracking()
            .Include(l => l.SourcePurchaseContract)
            .Include(l => l.Product)
            .OrderByDescending(l => l.LoadedDate)
            .ThenByDescending(l => l.Id)
            .Take(150)
            .Select(l => new
            {
                l.Id,
                l.LoadedDate,
                ContractNumber = l.SourcePurchaseContract != null ? l.SourcePurchaseContract.ContractNumber : "",
                ProductName = l.Product != null ? l.Product.Name : "",
                Reference = l.WagonNumber ?? l.RwbNo ?? l.BillOfLadingNumber
            })
            .ToListAsync();

        ViewBag.LoadingRegisterOptions = loadingSources
            .Select(l => new SelectListItem
            {
                Value = l.Id.ToString(),
                Text = $"#{l.Id} — {DateDisplay.Date(l.LoadingDate)} — {l.ContractNumber} — {l.ProductName} — {l.Reference}"
            })
            .ToList();

        ViewBag.TransportLegOptions = transportSources
            .Select(l => new SelectListItem
            {
                Value = l.Id.ToString(),
                Text = $"#{l.Id} — {DateDisplay.Date(l.LoadedDate)} — {l.ContractNumber} — {l.ProductName} — {l.Reference}"
            })
            .ToList();

        var truckDispatchSources = await _db.TruckDispatches.AsNoTracking()
            .Include(d => d.Contract)
            .Include(d => d.Product)
            .Include(d => d.Truck)
            .OrderByDescending(d => d.DispatchDate)
            .ThenByDescending(d => d.Id)
            .Take(150)
            .Select(d => new
            {
                d.Id,
                d.DispatchDate,
                ContractNumber = d.Contract != null ? d.Contract.ContractNumber : "",
                ProductName = d.Product != null ? d.Product.Name : "",
                Reference = d.Truck != null ? d.Truck.PlateNumber : ""
            })
            .ToListAsync();

        ViewBag.TruckDispatchOptions = truckDispatchSources
            .Select(d => new SelectListItem
            {
                Value = d.Id.ToString(),
                Text = $"#{d.Id} — {DateDisplay.Date(d.DispatchDate)} — {d.ContractNumber} — {d.ProductName} — {d.Reference}"
            })
            .ToList();
    }

    private async Task<LoadingRegister?> LoadLoadingRegisterSourceAsync(int loadingRegisterId)
        => await _db.LoadingRegisters.AsNoTracking()
            .Include(l => l.Contract)
            .Include(l => l.Product)
            .FirstOrDefaultAsync(l => l.Id == loadingRegisterId);

    private async Task<InventoryTransportLeg?> LoadTransportLegSourceAsync(int transportLegId)
        => await _db.InventoryTransportLegs.AsNoTracking()
            .Include(l => l.SourcePurchaseContract)
            .Include(l => l.Product)
            .FirstOrDefaultAsync(l => l.Id == transportLegId);

    private async Task<TruckDispatch?> LoadTruckDispatchSourceAsync(int truckDispatchId)
        => await _db.TruckDispatches.AsNoTracking()
            .Include(d => d.Contract)
            .Include(d => d.Product)
            .Include(d => d.Truck)
            .FirstOrDefaultAsync(d => d.Id == truckDispatchId);

    // مبلغ اصلیِ یک ردیف (در ارز انتخابی) را به جفت معادلِ (AFN, USD) تبدیل می‌کند.
    // نرخ = چند AFN در هر ۱ USD. اگر نرخ نباشد، فقط مبلغِ ارز انتخابی ذخیره می‌شود و معادل خالی می‌ماند.
    private static (decimal AmountAfn, decimal? AmountUsd) ConvertItemAmounts(string? currency, decimal amount, decimal? rate)
    {
        var effectiveRate = rate.GetValueOrDefault();
        if (CustomsCurrency.IsUsd(currency))
        {
            var afn = effectiveRate > 0m ? decimal.Round(amount * effectiveRate, 2, MidpointRounding.AwayFromZero) : 0m;
            return (afn, amount);
        }

        var usd = effectiveRate > 0m ? decimal.Round(amount / effectiveRate, 2, MidpointRounding.AwayFromZero) : (decimal?)null;
        return (amount, usd);
    }

    // یک قلمِ ذخیره‌شده را به ردیفِ فرم (ارز/مبلغ/نرخ) برمی‌گرداند تا قابل ویرایش باشد.
    // اگر هر دو ارز ذخیره شده باشند، AFN را به‌عنوان ارز اصلی نمایش می‌دهیم و نرخ = AFN/USD بازسازی می‌شود
    // (مبالغِ ذخیره‌شده با re-save تغییر نمی‌کند، فقط ارزِ نمایشیِ ردیف ممکن است با ورودی اولیه فرق کند).
    private static CustomsDeclarationItemRowViewModel ReconstructItemRow(CustomsDeclarationItem item)
    {
        var row = new CustomsDeclarationItemRowViewModel
        {
            ComponentType = item.ComponentType,
            ComponentLabel = GetLabel(item.ComponentType, item.CustomLabel),
            Notes = item.Notes
        };

        var usd = item.AmountUsd.GetValueOrDefault();
        if (item.AmountAfn > 0m && usd > 0m)
        {
            row.Currency = CustomsCurrency.Afn;
            row.Amount = item.AmountAfn;
            row.Rate = decimal.Round(item.AmountAfn / usd, 4, MidpointRounding.AwayFromZero);
        }
        else if (item.AmountAfn > 0m)
        {
            row.Currency = CustomsCurrency.Afn;
            row.Amount = item.AmountAfn;
            row.Rate = null;
        }
        else if (usd > 0m)
        {
            row.Currency = CustomsCurrency.Usd;
            row.Amount = usd;
            row.Rate = null;
        }
        else
        {
            row.Currency = DefaultCurrencyFor(item.ComponentType);
            row.Amount = 0m;
            row.Rate = null;
        }

        return row;
    }

    private static List<CustomsDeclarationItemRowViewModel> BuildDefaultItemRows()
    {
        var allTypes = Enum.GetValues<CustomsComponentType>()
            .Where(t => t != CustomsComponentType.Other)
            .OrderBy(t => (int)t)
            .ToList();
        allTypes.Add(CustomsComponentType.Other);

        return allTypes.Select(t => new CustomsDeclarationItemRowViewModel
        {
            ComponentType = t,
            ComponentLabel = GetLabel(t, null),
            Currency = DefaultCurrencyFor(t),
            Amount = 0,
            Rate = null
        }).ToList();
    }

    // پیش‌فرض ارز ردیف: انواع «دالری» با USD، بقیه با AFN شروع می‌شوند (کاربر می‌تواند تغییر دهد).
    private static string DefaultCurrencyFor(CustomsComponentType type)
        => type == CustomsComponentType.MahsooliDolari ? CustomsCurrency.Usd : CustomsCurrency.Afn;

    private static void PopulateTransportLegSource(CustomsDeclarationCreateViewModel model, InventoryTransportLeg leg)
    {
        model.TransportLegId = leg.Id;
        model.TransportLegLabel = $"انتقال از موجودی #{leg.Id} — {DateDisplay.Date(leg.LoadedDate)}";
        model.ContractNumber = leg.SourcePurchaseContract?.ContractNumber ?? "";
        model.ProductName = leg.Product?.Name ?? "";
        model.WagonNumber = leg.WagonNumber ?? leg.RwbNo ?? "";
        model.WagonOrTruckNumber = leg.WagonNumber ?? leg.RwbNo ?? leg.BillOfLadingNumber;
        model.ConsignmentWeightMt = leg.ChargeableQuantityMt ?? leg.QuantityMt;
    }

    private static void PopulateTruckDispatchSource(CustomsDeclarationCreateViewModel model, TruckDispatch dispatch)
    {
        model.TruckDispatchId = dispatch.Id;
        model.TruckDispatchLabel = $"ارسال با موتر #{dispatch.Id} — {DateDisplay.Date(dispatch.DispatchDate)}";
        model.ContractNumber = dispatch.Contract?.ContractNumber ?? "";
        model.ProductName = dispatch.Product?.Name ?? "";
        model.WagonNumber = dispatch.Truck?.PlateNumber ?? "";
        model.WagonOrTruckNumber = dispatch.Truck?.PlateNumber;
        model.ConsignmentWeightMt = dispatch.DischargedQuantityMt ?? dispatch.LoadedQuantityMt;
    }

    private static string BuildSourceLabel(int? loadingRegisterId, int? transportLegId, int? truckDispatchId)
        => loadingRegisterId.HasValue
            ? $"Loading #{loadingRegisterId.Value}"
            : transportLegId.HasValue
                ? $"Transport Leg #{transportLegId.Value}"
                : (truckDispatchId.HasValue ? $"Truck Dispatch #{truckDispatchId.Value}" : "—");

    private static string GetLabel(CustomsComponentType t, string? custom)
    {
        if (!string.IsNullOrWhiteSpace(custom)) return custom;
        return t switch
        {
            CustomsComponentType.Mahsooli => "محصولی (AFN)",
            CustomsComponentType.FawaidAama => "فواید عامه",
            CustomsComponentType.MahsooliDolari => "محصولی دالری (USD)",
            CustomsComponentType.KomisionTarifa => "کمیشن تعرفه",
            CustomsComponentType.NormStandard => "نورم استندرد",
            CustomsComponentType.KhatAhan => "خط آهن",
            CustomsComponentType.ElmKhabar => "علم و خبر",
            CustomsComponentType.GomrokSarhadi => "گمرک سرحدی",
            CustomsComponentType.Komisionkar => "کمیشنکار",
            CustomsComponentType.GasMasbut => "مثبت بودن گاز",
            CustomsComponentType.Mutafarraka => "متفرقه",
            CustomsComponentType.HaqKhidma => "حق الخدمه مواد نفت",
            CustomsComponentType.Yozbulagh => "یوزبلاغ",
            CustomsComponentType.KomisionBarchalani => "کمیشن بارچلانی",
            CustomsComponentType.KomisionBank => "کمیشن بانک",
            CustomsComponentType.Masraf20Pul => "مصرف ۲۰ پول",
            CustomsComponentType.BarnamaWagon => "بارنامه واگن",
            CustomsComponentType.TarazuMotor => "ترازوی موتر",
            CustomsComponentType.Other => "سایر",
            _ => t.ToString()
        };
    }
}
