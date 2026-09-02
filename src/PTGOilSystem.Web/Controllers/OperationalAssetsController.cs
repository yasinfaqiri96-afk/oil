using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Helpers;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.OperationalAssets;
using PTGOilSystem.Web.Security;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Exceptions;
using System.Text.Json;
using PTGOilSystem.Web.Services.Time;

namespace PTGOilSystem.Web.Controllers;

[Authorize]
public class OperationalAssetsController : Controller
{
    private const int IndexPageSize = 20;
    private const int LookupLimit = 250;
    private const decimal PercentTolerance = 0.0001m;
    private const long MaxDocumentBytes = 10 * 1024 * 1024;
    private static readonly HashSet<string> AllowedDocumentExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".pdf", ".jpg", ".jpeg", ".png", ".webp" };
    private readonly ApplicationDbContext _db;
    private readonly ICurrencyConversionService _currencyConversion;

    // ثبت/برگشتِ مالیِ کرایه در سرویس مشترک است تا مسیر لغوِ بارگیری هم دقیقاً همان کار را بکند.
    // اگر Adapter حسابداری تزریق نشده باشد فقط ژورنال ساخته نمی‌شود و لجر legacy کامل ثبت می‌شود.
    private readonly IAssetRentPostingService _rentPosting;
    private readonly IAfghanistanBusinessClock _businessClock;
    private readonly IWebHostEnvironment? _environment;

    [ActivatorUtilitiesConstructor]
    public OperationalAssetsController(
        ApplicationDbContext db,
        ICurrencyConversionService currencyConversion,
        IAssetRentPostingService? rentPosting = null,
        Services.Accounting.IAssetRentAccountingAdapter? rentAccounting = null,
        IAfghanistanBusinessClock? businessClock = null,
        IWebHostEnvironment? environment = null)
    {
        _db = db;
        _currencyConversion = currencyConversion;
        _rentPosting = rentPosting ?? new AssetRentPostingService(db, rentAccounting);
        _businessClock = businessClock ?? new AfghanistanBusinessClock(TimeProvider.System);
        _environment = environment;
    }

    public OperationalAssetsController(ApplicationDbContext db)
        : this(db, new CurrencyConversionService(new PricingService(db)))
    {
    }

    public async Task<IActionResult> Index([FromQuery] OperationalAssetIndexFilterViewModel? filter = null, int page = 1, [FromQuery(Name = "pageSize")] int? perPage = null)
    {
        var pageSize = ListPageSize.Resolve(perPage, IndexPageSize);
        ViewData["PageSize"] = pageSize;
        ViewData["DefaultPageSize"] = IndexPageSize;

        filter ??= new OperationalAssetIndexFilterViewModel();
        var query = _db.OperationalAssets
            .AsNoTracking()
            .Include(a => a.LinkedTruck)
            .Include(a => a.LinkedStorageTank)
            .AsQueryable();

        if (filter.AssetType.HasValue)
        {
            query = query.Where(a => a.AssetType == filter.AssetType.Value);
        }

        if (filter.IsActive.HasValue)
        {
            query = query.Where(a => a.IsActive == filter.IsActive.Value);
        }

        if (!string.IsNullOrWhiteSpace(filter.Query))
        {
            var q = filter.Query.Trim();
            query = query.Where(a =>
                a.AssetCode.Contains(q)
                || a.Name.Contains(q)
                || (a.LinkedTruck != null && a.LinkedTruck.PlateNumber.Contains(q))
                || (a.LinkedStorageTank != null && (
                    a.LinkedStorageTank.TankCode.Contains(q)
                    || (a.LinkedStorageTank.DisplayName != null && a.LinkedStorageTank.DisplayName.Contains(q)))));
        }

        var filteredAssetIdQuery = query.Select(a => a.Id);
        var totalCount = await filteredAssetIdQuery.CountAsync();
        var pageCount = page <= 0
            ? 1
            : Math.Max(1, (int)Math.Ceiling(totalCount / (double)pageSize));
        var currentPage = page <= 0 ? 1 : Math.Clamp(page, 1, pageCount);

        var assets = await (page <= 0
                ? query.OrderBy(a => a.AssetCode).ThenBy(a => a.Name)
                : query
                    .OrderBy(a => a.AssetCode)
                    .ThenBy(a => a.Name)
                    .Skip((currentPage - 1) * pageSize)
                    .Take(pageSize))
            .ToListAsync();

        var filteredRentTotals = totalCount == 0
            ? new Dictionary<int, (decimal Internal, decimal External)>()
            : await _db.AssetRentTransactions
                .AsNoTracking()
                .Where(r => filteredAssetIdQuery.Contains(r.OperationalAssetId) && !r.IsCancelled)
                .GroupBy(r => r.OperationalAssetId)
                .Select(g => new
                {
                    AssetId = g.Key,
                    Internal = g.Where(r => r.UsageType == AssetRentUsageType.InternalCompanyUse).Sum(r => r.AmountUsd),
                    External = g.Where(r => r.UsageType == AssetRentUsageType.ExternalCustomerRental).Sum(r => r.AmountUsd)
                })
                .ToDictionaryAsync(g => g.AssetId, g => (g.Internal, g.External));

        // مصارف هر دارایی به دو بخش جدا می‌شود: کرایه/حمل با دارایی شرکت (درآمد دارایی) و بقیهٔ مصارف (هزینه).
        // معیار درآمد = دستهٔ «Transport» یا کدهای کرایهٔ سیستم — هم‌راستا با IsAssetFreightIncome.
        var freightCategory = AssetRevenueExpenseCategory.ToLowerInvariant();
        var receiptFreightCode = InventoryTransportReceiptService.ReceiptFreightExpenseCode;
        var transportFreightCode = InventoryTransportReceiptService.TransportFreightExpenseCode;
        var filteredExpenseGroups = totalCount == 0
            ? new Dictionary<int, (decimal Direct, decimal Freight)>()
            : (await _db.ExpenseTransactions
                .AsNoTracking()
                .Where(e => e.OperationalAssetId.HasValue
                    && filteredAssetIdQuery.Contains(e.OperationalAssetId.Value)
                    && !e.IsCancelled)
                .GroupBy(e => e.OperationalAssetId!.Value)
                .Select(g => new
                {
                    AssetId = g.Key,
                    Total = g.Sum(e => e.AmountUsd),
                    Freight = g.Where(e => e.ExpenseType!.Category.ToLower() == freightCategory
                        || e.ExpenseType.Code == receiptFreightCode
                        || e.ExpenseType.Code == transportFreightCode).Sum(e => e.AmountUsd)
                })
                .ToListAsync())
                .ToDictionary(g => g.AssetId, g => (Direct: g.Total - g.Freight, Freight: g.Freight));

        var totalInternalRentUsd = filteredRentTotals.Values.Sum(v => v.Internal);
        var totalExternalRentUsd = filteredRentTotals.Values.Sum(v => v.External);
        var totalFreightIncomeUsd = filteredExpenseGroups.Values.Sum(v => v.Freight);
        var totalDirectExpensesUsd = filteredExpenseGroups.Values.Sum(v => v.Direct);
        var totalMonthlyDepreciationUsd = assets.Count == totalCount
            ? assets.Sum(a => a.MonthlyDepreciationUsd)
            : await query.SumAsync(a => a.MonthlyDepreciationUsd);

        ViewBag.AssetTypes = EnumOptions<OperationalAssetType>(filter.AssetType);
        return View(new OperationalAssetIndexViewModel
        {
            Filter = filter,
            CurrentPage = currentPage,
            PageCount = pageCount,
            TotalCount = totalCount,
            TotalInternalRentUsd = totalInternalRentUsd,
            TotalExternalRentUsd = totalExternalRentUsd,
            TotalFreightIncomeUsd = totalFreightIncomeUsd,
            TotalDirectExpensesUsd = totalDirectExpensesUsd,
            TotalMonthlyDepreciationUsd = totalMonthlyDepreciationUsd,
            Items = assets.Select(a =>
            {
                filteredRentTotals.TryGetValue(a.Id, out var rent);
                filteredExpenseGroups.TryGetValue(a.Id, out var exp);
                return new OperationalAssetIndexItemViewModel
                {
                    Id = a.Id,
                    AssetCode = a.AssetCode,
                    Name = a.Name,
                    AssetType = a.AssetType,
                    LinkedResourceText = BuildLinkedResourceText(a),
                    OwnershipMode = a.OwnershipMode,
                    MonthlyDepreciationUsd = a.MonthlyDepreciationUsd,
                    InternalRentUsd = rent.Internal,
                    ExternalRentUsd = rent.External,
                    FreightIncomeUsd = exp.Freight,
                    DirectExpensesUsd = exp.Direct,
                    IsActive = a.IsActive
                };
            }).ToList()
        });
    }

    public async Task<IActionResult> Details(int id, DateTime? fromDate = null, DateTime? toDate = null)
    {
        var model = await BuildProfileAsync(id, fromDate, toDate);
        return model is null ? NotFound() : View(model);
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    public async Task<IActionResult> Create()
    {
        var model = new OperationalAssetFormViewModel();
        await PopulateAssetFormLookupsAsync(model);
        return View(model);
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(OperationalAssetFormViewModel model)
    {
        NormalizeAssetForm(model);
        await ValidateAssetFormAsync(model);
        if (!ModelState.IsValid)
        {
            await PopulateAssetFormLookupsAsync(model);
            return View(model);
        }

        var asset = new OperationalAsset();
        ApplyAssetForm(asset, model);
        _db.OperationalAssets.Add(asset);
        await _db.SaveChangesAsync();

        TempData["ok"] = Ui("دارایی عملیاتی ذخیره شد.", "Operational asset saved.");
        return RedirectToAction(nameof(Details), new { id = asset.Id });
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    public async Task<IActionResult> Edit(int id)
    {
        var asset = await _db.OperationalAssets
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == id);
        if (asset is null)
        {
            return NotFound();
        }

        var model = ToFormModel(asset);
        await PopulateAssetFormLookupsAsync(model);
        return View(model);
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ToggleActive(int id, string? returnUrl = null)
    {
        var item = await _db.OperationalAssets.FirstOrDefaultAsync(x => x.Id == id);
        if (item is null) return NotFound();

        item.IsActive = !item.IsActive;
        await _db.SaveChangesAsync();

        TempData["ok"] = item.IsActive ? "رکورد فعال شد." : "رکورد غیرفعال شد.";
        if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl)) return Redirect(returnUrl);
        return RedirectToAction(nameof(Index));
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, OperationalAssetFormViewModel model)
    {
        if (id != model.Id)
        {
            return BadRequest();
        }

        NormalizeAssetForm(model);
        await ValidateAssetFormAsync(model);
        if (!ModelState.IsValid)
        {
            await PopulateAssetFormLookupsAsync(model);
            return View(model);
        }

        var asset = await _db.OperationalAssets.FirstOrDefaultAsync(a => a.Id == id);
        if (asset is null)
        {
            return NotFound();
        }

        ApplyAssetForm(asset, model);
        await _db.SaveChangesAsync();

        TempData["ok"] = Ui("دارایی عملیاتی به‌روزرسانی شد.", "Operational asset updated.");
        return RedirectToAction(nameof(Details), new { id = asset.Id });
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> AddOwnershipShare(AssetOwnershipShareCreateViewModel model)
    {
        NormalizeOwnershipModel(model);
        var assetExists = await _db.OperationalAssets
            .AsNoTracking()
            .AnyAsync(a => a.Id == model.OperationalAssetId);
        if (!assetExists)
        {
            return NotFound();
        }

        var issue = await ValidateOwnershipShareAsync(model);
        if (issue is not null)
        {
            TempData["err"] = issue;
            return RedirectToAction(nameof(Details), new { id = model.OperationalAssetId, tab = "ownership" });
        }

        _db.AssetOwnershipShares.Add(new AssetOwnershipShare
        {
            OperationalAssetId = model.OperationalAssetId,
            OwnerType = model.OwnerType,
            CompanyId = model.OwnerType == AssetOwnerType.Company ? model.CompanyId : null,
            PartnerId = model.OwnerType == AssetOwnerType.Partner ? model.PartnerId : null,
            OwnerName = model.OwnerType is AssetOwnerType.ExternalOwner or AssetOwnerType.Other ? model.OwnerName : null,
            SharePercent = model.SharePercent,
            EffectiveFrom = model.EffectiveFrom,
            EffectiveTo = model.EffectiveTo,
            Notes = model.Notes
        });
        await _db.SaveChangesAsync();

        TempData["ok"] = Ui("سهم مالکیت ذخیره شد.", "Ownership share saved.");
        return RedirectToAction(nameof(Details), new { id = model.OperationalAssetId, tab = "ownership" });
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> AddAssignment(AssetAssignmentCreateViewModel model)
    {
        var assetExists = await _db.OperationalAssets.AsNoTracking()
            .AnyAsync(a => a.Id == model.OperationalAssetId);
        if (!assetExists) return NotFound();

        model.Role = model.Role?.Trim() ?? "";
        model.Notes = string.IsNullOrWhiteSpace(model.Notes) ? null : model.Notes.Trim();
        model.FromDate = ToUtcDate(model.FromDate);
        if (!TryParsePartyKey(model.ResponsiblePartyKey, out var partyType, out var partyId)
            || !await PartyExistsAsync(partyType, partyId))
        {
            TempData["err"] = Ui("مسئول انتخاب‌شده معتبر نیست.", "The selected responsible party is invalid.");
            return RedirectToAction(nameof(Details), new { id = model.OperationalAssetId, tab = "responsibility" });
        }

        if (string.IsNullOrWhiteSpace(model.Role))
        {
            TempData["err"] = Ui("نقش مسئول الزامی است.", "The responsibility role is required.");
            return RedirectToAction(nameof(Details), new { id = model.OperationalAssetId, tab = "responsibility" });
        }

        if (model.DriverId.HasValue && !await _db.Drivers.AsNoTracking().AnyAsync(d => d.Id == model.DriverId.Value && d.IsActive))
        {
            TempData["err"] = Ui("راننده انتخاب‌شده معتبر نیست.", "The selected driver is invalid.");
            return RedirectToAction(nameof(Details), new { id = model.OperationalAssetId, tab = "responsibility" });
        }

        if (model.BaseTerminalId.HasValue && !await _db.Terminals.AsNoTracking().AnyAsync(t => t.Id == model.BaseTerminalId.Value && t.IsActive))
        {
            TempData["err"] = Ui("ترمینال پایه معتبر نیست.", "The base terminal is invalid.");
            return RedirectToAction(nameof(Details), new { id = model.OperationalAssetId, tab = "responsibility" });
        }

        var active = await _db.AssetAssignments
            .Where(a => a.OperationalAssetId == model.OperationalAssetId && a.Role == model.Role && a.ToDate == null)
            .ToListAsync();
        if (active.Any(a => a.FromDate > model.FromDate))
        {
            TempData["err"] = Ui("تاریخ مسئولیت جدید نمی‌تواند پیش از مسئولیت فعال باشد.", "The new assignment cannot start before the active assignment.");
            return RedirectToAction(nameof(Details), new { id = model.OperationalAssetId, tab = "responsibility" });
        }

        foreach (var previous in active)
            previous.ToDate = model.FromDate;

        _db.AssetAssignments.Add(new AssetAssignment
        {
            OperationalAssetId = model.OperationalAssetId,
            ResponsiblePartyType = partyType,
            ResponsiblePartyId = partyId,
            DriverId = model.DriverId,
            BaseTerminalId = model.BaseTerminalId,
            Role = model.Role,
            FromDate = model.FromDate,
            Notes = model.Notes
        });
        await _db.SaveChangesAsync();

        TempData["ok"] = Ui("مسئولیت جدید ثبت و سابقه قبلی بسته شد.", "The new assignment was saved and the previous record was closed.");
        return RedirectToAction(nameof(Details), new { id = model.OperationalAssetId, tab = "responsibility" });
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> AddMaintenanceJob(AssetMaintenanceJobCreateViewModel model)
    {
        if (!await _db.OperationalAssets.AsNoTracking().AnyAsync(a => a.Id == model.OperationalAssetId)) return NotFound();
        model.Title = model.Title?.Trim() ?? "";
        model.Notes = string.IsNullOrWhiteSpace(model.Notes) ? null : model.Notes.Trim();
        if (string.IsNullOrWhiteSpace(model.Title)
            || (model.CompletedDate.HasValue && model.StartedDate.HasValue && model.CompletedDate < model.StartedDate)
            || (model.DowntimeTo.HasValue && model.DowntimeFrom.HasValue && model.DowntimeTo < model.DowntimeFrom))
        {
            TempData["err"] = Ui("عنوان و ترتیب تاریخ‌های سرویس را بررسی کنید.", "Check the maintenance title and date order.");
            return RedirectToAction(nameof(Details), new { id = model.OperationalAssetId, tab = "maintenance" });
        }

        if (model.ExpenseTransactionId.HasValue && !await _db.ExpenseTransactions.AsNoTracking()
                .AnyAsync(e => e.Id == model.ExpenseTransactionId.Value && e.OperationalAssetId == model.OperationalAssetId))
        {
            TempData["err"] = Ui("سند مصرف باید متعلق به همین دارایی باشد.", "The expense reference must belong to this asset.");
            return RedirectToAction(nameof(Details), new { id = model.OperationalAssetId, tab = "maintenance" });
        }

        _db.AssetMaintenanceJobs.Add(new AssetMaintenanceJob
        {
            OperationalAssetId = model.OperationalAssetId,
            JobType = model.JobType,
            Status = model.Status,
            Title = model.Title,
            ScheduledDate = ToUtcDate(model.ScheduledDate),
            StartedDate = ToUtcDate(model.StartedDate),
            CompletedDate = ToUtcDate(model.CompletedDate),
            DowntimeFrom = ToUtcDate(model.DowntimeFrom),
            DowntimeTo = ToUtcDate(model.DowntimeTo),
            ExpenseTransactionId = model.ExpenseTransactionId,
            Notes = model.Notes
        });
        await _db.SaveChangesAsync();
        TempData["ok"] = Ui("سرویس/ترمیم ثبت شد.", "Maintenance was saved.");
        return RedirectToAction(nameof(Details), new { id = model.OperationalAssetId, tab = "maintenance" });
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> AddMeterReading(AssetMeterReadingCreateViewModel model)
    {
        if (!await _db.OperationalAssets.AsNoTracking().AnyAsync(a => a.Id == model.OperationalAssetId)) return NotFound();
        if (model.ReadingValue < 0m)
        {
            TempData["err"] = Ui("عدد کیلومتر/ساعت کار نمی‌تواند منفی باشد.", "The meter reading cannot be negative.");
            return RedirectToAction(nameof(Details), new { id = model.OperationalAssetId, tab = "maintenance" });
        }

        _db.AssetMeterReadings.Add(new AssetMeterReading
        {
            OperationalAssetId = model.OperationalAssetId,
            MeterType = model.MeterType,
            ReadingDate = ToUtcDate(model.ReadingDate),
            ReadingValue = model.ReadingValue,
            Reference = string.IsNullOrWhiteSpace(model.Reference) ? null : model.Reference.Trim(),
            Notes = string.IsNullOrWhiteSpace(model.Notes) ? null : model.Notes.Trim()
        });
        await _db.SaveChangesAsync();
        TempData["ok"] = Ui("عدد کارکرد ثبت شد.", "The meter reading was saved.");
        return RedirectToAction(nameof(Details), new { id = model.OperationalAssetId, tab = "maintenance" });
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> UploadDocument(AssetDocumentCreateViewModel model, CancellationToken ct = default)
    {
        if (!await _db.OperationalAssets.AsNoTracking().AnyAsync(a => a.Id == model.OperationalAssetId, ct)) return NotFound();
        if (model.File is null || model.File.Length == 0 || model.File.Length > MaxDocumentBytes)
        {
            TempData["err"] = Ui("فایل معتبر تا حجم 10MB انتخاب کنید.", "Select a valid file up to 10MB.");
            return RedirectToAction(nameof(Details), new { id = model.OperationalAssetId, tab = "documents" });
        }

        var extension = Path.GetExtension(model.File.FileName);
        if (string.IsNullOrWhiteSpace(extension) || !AllowedDocumentExtensions.Contains(extension))
        {
            TempData["err"] = Ui("فقط PDF، JPG، JPEG، PNG یا WEBP مجاز است.", "Only PDF, JPG, JPEG, PNG or WEBP files are allowed.");
            return RedirectToAction(nameof(Details), new { id = model.OperationalAssetId, tab = "documents" });
        }

        var relativeDirectory = Path.Combine("uploads", "operational-assets", model.OperationalAssetId.ToString());
        var absoluteDirectory = Path.Combine(GetWebRootPath(), relativeDirectory);
        Directory.CreateDirectory(absoluteDirectory);
        var storedFileName = $"{Guid.NewGuid():N}{extension.ToLowerInvariant()}";
        await using (var stream = System.IO.File.Create(Path.Combine(absoluteDirectory, storedFileName)))
            await model.File.CopyToAsync(stream, ct);

        _db.AssetDocuments.Add(new AssetDocument
        {
            OperationalAssetId = model.OperationalAssetId,
            DocumentType = model.DocumentType,
            DocumentNumber = string.IsNullOrWhiteSpace(model.DocumentNumber) ? null : model.DocumentNumber.Trim(),
            IssueDate = ToUtcDate(model.IssueDate),
            ExpiryDate = ToUtcDate(model.ExpiryDate),
            OriginalFileName = Path.GetFileName(model.File.FileName),
            StoredFileName = storedFileName,
            FilePath = "/" + relativeDirectory.Replace('\\', '/') + "/" + storedFileName,
            ContentType = model.File.ContentType,
            FileSizeBytes = model.File.Length,
            UploadedByUserName = User.Identity?.Name,
            Notes = string.IsNullOrWhiteSpace(model.Notes) ? null : model.Notes.Trim()
        });
        await _db.SaveChangesAsync(ct);
        TempData["ok"] = Ui("سند دارایی آپلود شد.", "The asset document was uploaded.");
        return RedirectToAction(nameof(Details), new { id = model.OperationalAssetId, tab = "documents" });
    }

    public async Task<IActionResult> DownloadDocument(int id)
    {
        var document = await _db.AssetDocuments.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id);
        if (document is null) return NotFound();
        var absolutePath = Path.Combine(GetWebRootPath(), document.FilePath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
        if (!System.IO.File.Exists(absolutePath)) return NotFound();
        return PhysicalFile(absolutePath,
            string.IsNullOrWhiteSpace(document.ContentType) ? "application/octet-stream" : document.ContentType,
            document.OriginalFileName);
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    public async Task<IActionResult> CreateRent(int? assetId = null)
    {
        var model = new AssetRentCreateViewModel
        {
            OperationalAssetId = assetId ?? 0,
            RentDate = AfghanistanBusinessClock.SystemToday,
            Currency = SystemCurrency.BaseCurrencyCode,
            FxRateToUsd = 1m
        };

        if (assetId.HasValue)
        {
            var asset = await _db.OperationalAssets
                .AsNoTracking()
                .FirstOrDefaultAsync(a => a.Id == assetId.Value);
            if (asset is not null)
            {
                model.Rate = asset.DefaultInternalRateUsd ?? asset.DefaultExternalRateUsd ?? 0m;
            }
        }

        await PopulateRentLookupsAsync(model);
        return View(model);
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateRent(AssetRentCreateViewModel model)
    {
        NormalizeRentModel(model);
        var asset = await _db.OperationalAssets
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == model.OperationalAssetId);

        if (asset is null)
        {
            ModelState.AddModelError(nameof(model.OperationalAssetId), Ui("انتخاب دارایی عملیاتی معتبر نیست.", "Operational asset selection is invalid."));
        }
        else if (!asset.IsActive)
        {
            ModelState.AddModelError(nameof(model.OperationalAssetId), Ui("دارایی عملیاتی غیرفعال است.", "Operational asset is inactive."));
        }
        else
        {
            ValidateRentMeasurementInputs(model, asset.AssetType);
        }

        await ValidateRentCounterpartyAsync(model);

        var activeOwnershipShares = asset is null
            ? new List<AssetOwnershipShare>()
            : await GetActiveOwnershipSharesAsync(asset.Id, model.RentDate);
        var activeShareTotal = activeOwnershipShares.Sum(s => s.SharePercent);
        if (asset is not null
            && (activeOwnershipShares.Count == 0
                || Math.Abs(decimal.Round(activeShareTotal, 4, MidpointRounding.AwayFromZero) - 100m) > PercentTolerance))
        {
            ModelState.AddModelError(string.Empty, Ui("برای ثبت کرایه، مجموع سهم‌های مالکیت فعال در تاریخ کرایه باید 100٪ باشد.", "Active ownership shares for the rent date must total 100% before rent can be recorded."));
        }

        var amountOriginal = ResolveRentAmountOriginal(model);
        if (amountOriginal <= 0m)
        {
            ModelState.AddModelError(nameof(model.AmountOriginal), Ui("مبلغ کرایه باید بزرگتر از صفر باشد.", "Rent amount must be greater than zero."));
        }

        if (!ModelState.IsValid)
        {
            await PopulateRentLookupsAsync(model);
            return View(model);
        }

        if (asset is not null)
        {
            NormalizeRentMeasurementInputs(model, asset.AssetType);
        }

        var newCustomer = await ResolveNewRentCustomerAsync(model);

        CurrencyConversionResult conversion;
        try
        {
            conversion = await _currencyConversion.ResolveToBaseAsync(
                model.Currency,
                model.RentDate,
                model.FxRateToUsd);
        }
        catch (BusinessRuleException ex)
        {
            ModelState.AddModelError(nameof(model.FxRateToUsd), ex.Message);
            await PopulateRentLookupsAsync(model);
            return View(model);
        }

        var rent = new AssetRentTransaction
        {
            OperationalAssetId = model.OperationalAssetId,
            RentDate = model.RentDate,
            UsageType = model.UsageType,
            ChargedToType = model.ChargedToType,
            ChargedToContractId = model.ChargedToContractId,
            ChargedToCustomerId = model.ChargedToCustomerId,
            ChargedToCustomer = newCustomer,
            ChargedToCompanyId = model.ChargedToCompanyId,
            ChargedToPartnerId = model.ChargedToPartnerId,
            ChargedToServiceProviderId = model.ChargedToServiceProviderId,
            QuantityMt = model.QuantityMt,
            DistanceKm = model.DistanceKm,
            Days = model.Days,
            Rate = model.Rate,
            Currency = conversion.SourceCurrencyCode,
            FxRateToUsd = conversion.AppliedRateToBase,
            AmountOriginal = amountOriginal,
            AmountUsd = conversion.ConvertToBase(amountOriginal),
            ReferenceDocument = model.ReferenceDocument,
            Description = model.Description,
            IsPostedToLedger = false
        };

        // Rent → Share snapshots → Ledger → Link → Journal، همه در یک واحد. اگر هر مرحله شکست
        // بخورد هیچ‌کدام نمی‌ماند، پس هرگز کرایه‌ای بدون سهم یا لجری بدون لینک باقی نمی‌ماند.
        using var transaction = _db.Database.IsRelational()
            ? await _db.Database.BeginTransactionAsync()
            : null;

        _db.AssetRentTransactions.Add(rent);
        await _db.SaveChangesAsync();

        var rentShares = BuildRentShareSnapshots(rent.Id, rent.AmountUsd, activeOwnershipShares);
        _db.AssetRentShares.AddRange(rentShares);
        await _db.SaveChangesAsync();

        var posting = await _rentPosting.PostAsync(rent, conversion, asset?.AssetCode);
        await new AssetUsageChargeService(_db).SyncLegacyRentAsync(rent);

        if (transaction is not null)
        {
            await transaction.CommitAsync();
        }

        TempData["ok"] = posting.Posted
            ? Ui("تراکنش کرایه ذخیره و در حساب طرف مقابل ثبت شد.", "Asset rent transaction saved and posted to the counterparty account.")
            : Ui("تراکنش کرایه/استفاده دارایی ذخیره شد. برای این نوع کرایه ثبت مالی انجام نمی‌شود.", "Asset rent/use transaction saved. This rent kind does not create a financial posting.");
        // بازگشت به همان تبی که کاربر در آن ثبت کرد، با بازه‌ای که ردیف تازه حتماً داخل آن باشد.
        var (rentFromDate, rentToDate) = ResolveProfilePeriodFor(rent.RentDate);
        return RedirectToAction(nameof(Details), new
        {
            id = rent.OperationalAssetId,
            tab = "income",
            fromDate = rentFromDate.ToString("yyyy-MM-dd"),
            toDate = rentToDate.ToString("yyyy-MM-dd")
        });
    }

    /// <summary>
    /// لغو یک کرایه. رکورد اصلی و ردیف لجر اصلی حذف نمی‌شوند؛ یک ردیف جبرانی با جهت معکوس اضافه
    /// می‌شود و ژورنال (اگر وجود داشته باشد) با ژورنال قرینه برمی‌گردد — همان قراردادی که لغو فروش
    /// و لغو مصرف دارند. فراخوانی دوباره هیچ ردیف سومی نمی‌سازد.
    /// </summary>
    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> CancelRent(int id, string? reason = null, string? returnUrl = null)
    {
        var rent = await _db.AssetRentTransactions.FirstOrDefaultAsync(r => r.Id == id);
        if (rent is null)
        {
            return NotFound();
        }

        var (fromDate, toDate) = ResolveProfilePeriodFor(rent.RentDate);
        var backUrl = !string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl)
            ? returnUrl
            : Url.Action(nameof(Details), new
            {
                id = rent.OperationalAssetId,
                tab = "income",
                fromDate = fromDate.ToString("yyyy-MM-dd"),
                toDate = toDate.ToString("yyyy-MM-dd")
            });

        if (rent.IsCancelled)
        {
            // Idempotent: کرایهٔ لغوشده دوباره لغو نمی‌شود و ردیف جبرانی دوم ساخته نمی‌شود.
            TempData["ok"] = Ui("این کرایه قبلاً لغو شده است.", "This rent is already cancelled.");
            return Redirect(backUrl!);
        }

        var reversalDate = _businessClock.Today;

        using var transaction = _db.Database.IsRelational()
            ? await _db.Database.BeginTransactionAsync()
            : null;

        rent.IsCancelled = true;
        rent.CancelledAtUtc = DateTime.UtcNow;
        rent.CancelReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        await _db.SaveChangesAsync();

        var reversal = await _rentPosting.ReverseAsync(rent, reversalDate);
        await new AssetUsageChargeService(_db).CancelLegacyRentChargeAsync(rent.Id, rent.CancelReason);

        if (transaction is not null)
        {
            await transaction.CommitAsync();
        }

        TempData["ok"] = reversal.Reversed
            ? Ui("کرایه لغو و اثر مالی آن برگردانده شد.", "Rent cancelled and its financial effect reversed.")
            : Ui("کرایه لغو شد. این کرایه اثر مالی نداشت، پس ردیف برگشتی ساخته نشد.", "Rent cancelled. It had no financial posting, so no reversal row was created.");
        return Redirect(backUrl!);
    }

    public async Task<IActionResult> Profitability([FromQuery] OperationalAssetProfitabilityFilterViewModel? filter = null)
    {
        filter ??= new OperationalAssetProfitabilityFilterViewModel();
        NormalizeProfitabilityFilter(filter);
        await PopulateProfitabilityLookupsAsync(filter);
        var reportFromDate = filter.FromDate!.Value;
        var reportToDate = filter.ToDate!.Value;

        var assetQuery = _db.OperationalAssets.AsNoTracking().AsQueryable();
        if (filter.AssetType.HasValue)
        {
            assetQuery = assetQuery.Where(a => a.AssetType == filter.AssetType.Value);
        }

        if (filter.OperationalAssetId.HasValue)
        {
            assetQuery = assetQuery.Where(a => a.Id == filter.OperationalAssetId.Value);
        }

        var assets = await assetQuery
            .OrderBy(a => a.AssetCode)
            .ThenBy(a => a.Name)
            .ToListAsync();
        var assetIds = assets.Select(a => a.Id).ToArray();

        var rentQuery = _db.AssetRentTransactions
            .AsNoTracking()
            .Where(r => assetIds.Contains(r.OperationalAssetId)
                && !r.IsCancelled
                && r.RentDate >= reportFromDate
                && r.RentDate <= reportToDate);
        if (filter.UsageType.HasValue)
        {
            rentQuery = rentQuery.Where(r => r.UsageType == filter.UsageType.Value);
        }
        if (filter.ContractId.HasValue)
        {
            rentQuery = rentQuery.Where(r => r.ChargedToContractId == filter.ContractId.Value);
        }
        if (filter.CustomerId.HasValue)
        {
            rentQuery = rentQuery.Where(r => r.ChargedToCustomerId == filter.CustomerId.Value);
        }

        var rents = await rentQuery.ToListAsync();
        var rentIds = rents.Select(r => r.Id).ToArray();

        var expenses = await _db.ExpenseTransactions
            .AsNoTracking()
            .Include(e => e.ExpenseType)
            .Where(e => e.OperationalAssetId.HasValue
                && assetIds.Contains(e.OperationalAssetId.Value)
                && !e.IsCancelled
                && e.ExpenseDate >= reportFromDate
                && e.ExpenseDate <= reportToDate)
            .ToListAsync();

        var ownerShares = rentIds.Length == 0
            ? new List<AssetRentShare>()
            : await _db.AssetRentShares
                .AsNoTracking()
                .Include(s => s.Company)
                .Include(s => s.Partner)
                .Include(s => s.AssetRentTransaction)
                    .ThenInclude(r => r!.OperationalAsset)
                .Where(s => rentIds.Contains(s.AssetRentTransactionId)
                    && (!filter.PartnerId.HasValue || s.PartnerId == filter.PartnerId.Value))
                .OrderByDescending(s => s.AssetRentTransaction!.RentDate)
                .ThenByDescending(s => s.AssetRentTransactionId)
                .ToListAsync();

        var rows = assets.Select(asset =>
        {
            var assetRents = rents.Where(r => r.OperationalAssetId == asset.Id).ToList();
            var assetExpenses = expenses.Where(e => e.OperationalAssetId == asset.Id).ToList();
            return new OperationalAssetProfitabilityRowViewModel
            {
                OperationalAssetId = asset.Id,
                AssetCode = asset.AssetCode,
                AssetName = asset.Name,
                AssetType = asset.AssetType,
                UsageCount = assetRents.Count,
                QuantityMt = assetRents.Sum(r => r.QuantityMt ?? 0m),
                DistanceKm = assetRents.Sum(r => r.DistanceKm ?? 0m),
                Days = assetRents.Sum(r => r.Days ?? 0m),
                InternalRentUsd = assetRents.Where(r => r.UsageType == AssetRentUsageType.InternalCompanyUse).Sum(r => r.AmountUsd),
                ExternalRentUsd = assetRents.Where(r => r.UsageType == AssetRentUsageType.ExternalCustomerRental).Sum(r => r.AmountUsd),
                // کرایهٔ حمل/رسید با دارایی خودِ شرکت = درآمد دارایی؛ بقیهٔ مصارف = هزینه.
                FreightIncomeUsd = assetExpenses.Where(IsAssetFreightIncome).Sum(e => e.AmountUsd),
                DirectExpensesUsd = assetExpenses.Where(e => !IsAssetFreightIncome(e)).Sum(e => e.AmountUsd),
                DepreciationUsd = CalculateDepreciation(asset.MonthlyDepreciationUsd, reportFromDate, reportToDate)
            };
        }).ToList();

        return View(new OperationalAssetProfitabilityViewModel
        {
            Filter = filter,
            Rows = rows,
            OwnerShareRows = ownerShares.Select(ToRentShareRow).ToList()
        });
    }

    private async Task<OperationalAssetProfileViewModel?> BuildProfileAsync(int id, DateTime? fromDate, DateTime? toDate)
    {
        // روز کاری کابل، نه روز UTC: تاریخ‌های کرایه و مصرف با همین تقویم ثبت می‌شوند
        // (AfghanistanBusinessClock.SystemToday) و بازهٔ UTC بین ۱۹:۳۰ تا ۲۴:۰۰ یک روز عقب می‌ماند
        // و ردیف‌های همان روز را از لیست حذف می‌کرد.
        var today = AfghanistanBusinessClock.SystemToday;
        var periodFrom = fromDate.HasValue ? ToUtcDate(fromDate.Value) : DefaultProfilePeriodFrom();
        var periodTo = toDate.HasValue ? ToUtcDate(toDate.Value) : today;
        if (periodTo < periodFrom)
        {
            (periodFrom, periodTo) = (periodTo, periodFrom);
        }

        var asset = await _db.OperationalAssets
            .AsNoTracking()
            .Include(a => a.LinkedTruck)
            .Include(a => a.LinkedStorageTank)
            .Include(a => a.Location)
            .Include(a => a.Terminal)
            .FirstOrDefaultAsync(a => a.Id == id);
        if (asset is null)
        {
            return null;
        }

        var ownershipShares = await _db.AssetOwnershipShares
            .AsNoTracking()
            .Include(s => s.Company)
            .Include(s => s.Partner)
            .Where(s => s.OperationalAssetId == id)
            .OrderByDescending(s => s.EffectiveFrom)
            .ThenBy(s => s.OwnerType)
            .ToListAsync();

        var assignments = await _db.AssetAssignments
            .AsNoTracking()
            .Include(a => a.Driver)
            .Include(a => a.BaseTerminal)
            .Where(a => a.OperationalAssetId == id)
            .OrderByDescending(a => a.ToDate == null)
            .ThenByDescending(a => a.FromDate)
            .ThenByDescending(a => a.Id)
            .ToListAsync();
        var assignmentPartyNames = await ResolvePartyNamesAsync(assignments);

        var maintenanceJobs = await _db.AssetMaintenanceJobs
            .AsNoTracking()
            .Where(j => j.OperationalAssetId == id)
            .OrderByDescending(j => j.StartedDate ?? j.ScheduledDate)
            .ThenByDescending(j => j.Id)
            .ToListAsync();
        var meterReadings = await _db.AssetMeterReadings
            .AsNoTracking()
            .Where(r => r.OperationalAssetId == id)
            .OrderByDescending(r => r.ReadingDate)
            .ThenByDescending(r => r.Id)
            .ToListAsync();
        var documents = await _db.AssetDocuments
            .AsNoTracking()
            .Where(d => d.OperationalAssetId == id)
            .OrderBy(d => d.ExpiryDate == null)
            .ThenBy(d => d.ExpiryDate)
            .ThenByDescending(d => d.Id)
            .ToListAsync();

        var rents = await _db.AssetRentTransactions
            .AsNoTracking()
            .Include(r => r.ChargedToContract)
            .Include(r => r.ChargedToCustomer)
            .Include(r => r.ChargedToCompany)
            .Include(r => r.ChargedToPartner)
            .Include(r => r.ChargedToServiceProvider)
            .Where(r => r.OperationalAssetId == id
                && !r.IsCancelled
                && r.RentDate >= periodFrom
                && r.RentDate <= periodTo)
            .OrderByDescending(r => r.RentDate)
            .ThenByDescending(r => r.Id)
            .ToListAsync();

        var expenses = await _db.ExpenseTransactions
            .AsNoTracking()
            .Include(e => e.ExpenseType)
            .Include(e => e.Contract)
            .Include(e => e.Shipment)
            .Include(e => e.TransportLeg)
            .Include(e => e.TruckDispatch)
                .ThenInclude(d => d!.Truck)
            .Include(e => e.ServiceProvider)
            .Where(e => e.OperationalAssetId == id
                && !e.IsCancelled
                && e.ExpenseDate >= periodFrom
                && e.ExpenseDate <= periodTo)
            .OrderByDescending(e => e.ExpenseDate)
            .ThenByDescending(e => e.Id)
            .ToListAsync();

        var rentIds = rents.Select(r => r.Id).ToArray();
        var rentShares = rentIds.Length == 0
            ? new List<AssetRentShare>()
            : await _db.AssetRentShares
                .AsNoTracking()
                .Include(s => s.Company)
                .Include(s => s.Partner)
                .Include(s => s.AssetRentTransaction)
                    .ThenInclude(r => r!.OperationalAsset)
                .Where(s => rentIds.Contains(s.AssetRentTransactionId))
                .OrderByDescending(s => s.AssetRentTransaction!.RentDate)
                .ThenByDescending(s => s.AssetRentTransactionId)
                .ToListAsync();

        // رسیدِ حمل صفحهٔ جداگانه ندارد؛ برای اینکه ردیف خودکارِ رسید هم لینک زنده داشته باشد،
        // شناسهٔ حملِ همان رسید خوانده می‌شود و لینک به سند حمل می‌رود.
        var receiptIds = rents
            .Where(r => r.InventoryTransportReceiptId.HasValue)
            .Select(r => r.InventoryTransportReceiptId!.Value)
            .Distinct()
            .ToArray();
        var receiptLegMap = receiptIds.Length == 0
            ? new Dictionary<int, int>()
            : await _db.InventoryTransportReceipts
                .AsNoTracking()
                .Where(r => receiptIds.Contains(r.Id))
                .Select(r => new { r.Id, r.InventoryTransportLegId })
                .ToDictionaryAsync(r => r.Id, r => r.InventoryTransportLegId);

        var rentRows = rents.Select(rent => ToRentRow(rent, receiptLegMap)).ToList();
        var expenseRows = expenses.Select(ToExpenseRow).ToList();
        var costRows = expenseRows.Where(row => !row.IsFreightIncome).ToList();
        var freightIncomeRows = expenseRows.Where(row => row.IsFreightIncome).ToList();

        var newRent = new AssetRentCreateViewModel
        {
            OperationalAssetId = asset.Id,
            RentDate = AfghanistanBusinessClock.SystemToday,
            UsageType = AssetRentUsageType.ExternalCustomerRental,
            ChargedToType = AssetRentChargedToType.Customer,
            Rate = asset.DefaultExternalRateUsd ?? asset.DefaultInternalRateUsd ?? 1m,
            Currency = SystemCurrency.BaseCurrencyCode,
            FxRateToUsd = 1m
        };

        await PopulateOwnershipLookupsAsync();
        await PopulateAssetManagementLookupsAsync();
        await PopulateRentLookupsAsync(newRent);

        return new OperationalAssetProfileViewModel
        {
            Id = asset.Id,
            AssetCode = asset.AssetCode,
            Name = asset.Name,
            AssetType = asset.AssetType,
            LinkedResourceText = BuildLinkedResourceText(asset),
            OwnershipMode = asset.OwnershipMode,
            CapacityMt = asset.CapacityMt,
            LocationName = asset.Location?.Name,
            TerminalName = asset.Terminal?.Name,
            AcquisitionDate = asset.AcquisitionDate,
            AcquisitionCostUsd = asset.AcquisitionCostUsd,
            InServiceDate = asset.InServiceDate,
            DisposalDate = asset.DisposalDate,
            OperationalStatus = asset.OperationalStatus,
            MonthlyDepreciationUsd = asset.MonthlyDepreciationUsd,
            DefaultInternalRateUsd = asset.DefaultInternalRateUsd,
            DefaultExternalRateUsd = asset.DefaultExternalRateUsd,
            IsActive = asset.IsActive,
            Notes = asset.Notes,
            FromDate = periodFrom,
            ToDate = periodTo,
            InternalRentUsd = rents.Where(r => r.UsageType == AssetRentUsageType.InternalCompanyUse).Sum(r => r.AmountUsd),
            ExternalRentUsd = rents.Where(r => r.UsageType == AssetRentUsageType.ExternalCustomerRental).Sum(r => r.AmountUsd),
            // کرایهٔ حمل/رسید با دارایی خودِ شرکت = درآمد دارایی؛ بقیهٔ مصارف = هزینه.
            FreightIncomeUsd = expenses.Where(IsAssetFreightIncome).Sum(e => e.AmountUsd),
            DirectExpensesUsd = expenses.Where(e => !IsAssetFreightIncome(e)).Sum(e => e.AmountUsd),
            DepreciationUsd = CalculateDepreciation(asset.MonthlyDepreciationUsd, periodFrom, periodTo),
            OwnershipShares = ownershipShares.Select(ToOwnershipShareRow).ToList(),
            Assignments = assignments.Select(a => new AssetAssignmentRowViewModel
            {
                Id = a.Id,
                ResponsibleName = assignmentPartyNames.GetValueOrDefault((a.ResponsiblePartyType, a.ResponsiblePartyId), Ui("طرف #", "Party #") + a.ResponsiblePartyId),
                Role = a.Role,
                DriverName = a.Driver?.FullName,
                BaseTerminalName = a.BaseTerminal?.Name,
                FromDate = a.FromDate,
                ToDate = a.ToDate,
                Notes = a.Notes
            }).ToList(),
            MaintenanceJobs = maintenanceJobs.Select(j => new AssetMaintenanceJobRowViewModel
            {
                Id = j.Id,
                JobType = j.JobType,
                Status = j.Status,
                Title = j.Title,
                ScheduledDate = j.ScheduledDate,
                StartedDate = j.StartedDate,
                CompletedDate = j.CompletedDate,
                DowntimeFrom = j.DowntimeFrom,
                DowntimeTo = j.DowntimeTo,
                ExpenseTransactionId = j.ExpenseTransactionId,
                Notes = j.Notes
            }).ToList(),
            MeterReadings = meterReadings.Select(r => new AssetMeterReadingRowViewModel
            {
                Id = r.Id,
                MeterType = r.MeterType,
                ReadingDate = r.ReadingDate,
                ReadingValue = r.ReadingValue,
                Reference = r.Reference
            }).ToList(),
            Documents = documents.Select(d => new AssetDocumentRowViewModel
            {
                Id = d.Id,
                DocumentType = d.DocumentType,
                DocumentNumber = d.DocumentNumber,
                IssueDate = d.IssueDate,
                ExpiryDate = d.ExpiryDate,
                OriginalFileName = d.OriginalFileName,
                IsExpired = d.ExpiryDate.HasValue && d.ExpiryDate.Value.Date < today.Date,
                ExpiresSoon = d.ExpiryDate.HasValue && d.ExpiryDate.Value.Date >= today.Date && d.ExpiryDate.Value.Date <= today.AddDays(30).Date,
                Notes = d.Notes
            }).ToList(),
            RentTransactions = rentRows,
            Expenses = expenseRows,
            RentShares = rentShares.Select(ToRentShareRow).ToList(),
            ActiveOwnershipPercent = ownershipShares
                .Where(share => IsActiveOn(share, AfghanistanBusinessClock.SystemToday))
                .Sum(share => share.SharePercent),
            WorkRows = BuildWorkRows(rentRows, freightIncomeRows),
            CostRows = costRows,
            InternalIncomeRows = BuildInternalIncomeRows(rentRows, freightIncomeRows),
            ExternalIncomeRows = BuildExternalIncomeRows(rentRows),
            NewOwnershipShare = new AssetOwnershipShareCreateViewModel
            {
                OperationalAssetId = asset.Id,
                EffectiveFrom = AfghanistanBusinessClock.SystemToday
            },
            NewAssignment = new AssetAssignmentCreateViewModel
            {
                OperationalAssetId = asset.Id,
                FromDate = AfghanistanBusinessClock.SystemToday
            },
            NewMaintenanceJob = new AssetMaintenanceJobCreateViewModel { OperationalAssetId = asset.Id },
            NewMeterReading = new AssetMeterReadingCreateViewModel
            {
                OperationalAssetId = asset.Id,
                ReadingDate = AfghanistanBusinessClock.SystemToday
            },
            NewDocument = new AssetDocumentCreateViewModel { OperationalAssetId = asset.Id },
            NewRent = newRent
        };
    }

    private async Task ValidateAssetFormAsync(OperationalAssetFormViewModel model)
    {
        if (await _db.OperationalAssets.AsNoTracking().AnyAsync(a => a.Id != model.Id && a.AssetCode == model.AssetCode))
        {
            ModelState.AddModelError(nameof(model.AssetCode), Ui("کد دارایی از قبل وجود دارد.", "Asset code already exists."));
        }

        if (model.LinkedTruckId.HasValue
            && !await _db.Trucks.AsNoTracking().AnyAsync(t => t.Id == model.LinkedTruckId.Value))
        {
            ModelState.AddModelError(nameof(model.LinkedTruckId), Ui("انتخاب موتر مرتبط معتبر نیست.", "Linked truck selection is invalid."));
        }

        if (model.LinkedTruckId.HasValue
            && await _db.OperationalAssets.AsNoTracking().AnyAsync(a => a.Id != model.Id && a.LinkedTruckId == model.LinkedTruckId.Value))
        {
            ModelState.AddModelError(nameof(model.LinkedTruckId), Ui("این موتر قبلاً به یک دارایی عملیاتی دیگر وصل شده است.", "This truck is already linked to another operational asset."));
        }

        if (model.LinkedStorageTankId.HasValue
            && !await _db.StorageTanks.AsNoTracking().AnyAsync(t => t.Id == model.LinkedStorageTankId.Value))
        {
            ModelState.AddModelError(nameof(model.LinkedStorageTankId), Ui("انتخاب مخزن مرتبط معتبر نیست.", "Linked storage tank selection is invalid."));
        }

        if (model.LinkedStorageTankId.HasValue
            && await _db.OperationalAssets.AsNoTracking().AnyAsync(a => a.Id != model.Id && a.LinkedStorageTankId == model.LinkedStorageTankId.Value))
        {
            ModelState.AddModelError(nameof(model.LinkedStorageTankId), Ui("این مخزن قبلاً به یک دارایی عملیاتی دیگر وصل شده است.", "This storage tank is already linked to another operational asset."));
        }

        if (model.LocationId.HasValue
            && !await _db.Locations.AsNoTracking().AnyAsync(l => l.Id == model.LocationId.Value))
        {
            ModelState.AddModelError(nameof(model.LocationId), Ui("انتخاب موقعیت معتبر نیست.", "Location selection is invalid."));
        }

        if (model.TerminalId.HasValue
            && !await _db.Terminals.AsNoTracking().AnyAsync(t => t.Id == model.TerminalId.Value))
        {
            ModelState.AddModelError(nameof(model.TerminalId), Ui("انتخاب ترمینال معتبر نیست.", "Terminal selection is invalid."));
        }

        if (model.InServiceDate.HasValue && model.AcquisitionDate.HasValue && model.InServiceDate < model.AcquisitionDate)
            ModelState.AddModelError(nameof(model.InServiceDate), Ui("تاریخ شروع کار نمی‌تواند پیش از تاریخ خرید باشد.", "In-service date cannot be before acquisition date."));
        if (model.DisposalDate.HasValue && model.InServiceDate.HasValue && model.DisposalDate < model.InServiceDate)
            ModelState.AddModelError(nameof(model.DisposalDate), Ui("تاریخ خروج نمی‌تواند پیش از تاریخ شروع کار باشد.", "Disposal date cannot be before in-service date."));
    }

    private async Task<string?> ValidateOwnershipShareAsync(AssetOwnershipShareCreateViewModel model)
    {
        if (model.SharePercent <= 0m || model.SharePercent > 100m)
        {
            return Ui("درصد سهم باید بین 0 و 100 باشد.", "Share percent must be between 0 and 100.");
        }

        if (model.EffectiveTo.HasValue && model.EffectiveTo.Value.Date < model.EffectiveFrom.Date)
        {
            return Ui("تاریخ ختم نمی‌تواند قبل از تاریخ شروع باشد.", "Effective To cannot be before Effective From.");
        }

        if (model.OwnerType == AssetOwnerType.Company)
        {
            if (!model.CompanyId.HasValue)
            {
                return Ui("انتخاب شرکت مالک الزامی است.", "Company owner is required.");
            }

            if (!await _db.Companies.AsNoTracking().AnyAsync(c => c.Id == model.CompanyId.Value))
            {
                return Ui("انتخاب شرکت مالک معتبر نیست.", "Company owner selection is invalid.");
            }
        }
        else if (model.OwnerType == AssetOwnerType.Partner)
        {
            if (!model.PartnerId.HasValue)
            {
                return Ui("انتخاب شریک مالک الزامی است.", "Partner owner is required.");
            }

            if (!await _db.Partners.AsNoTracking().AnyAsync(p => p.Id == model.PartnerId.Value))
            {
                return Ui("انتخاب شریک مالک معتبر نیست.", "Partner owner selection is invalid.");
            }
        }
        else if (string.IsNullOrWhiteSpace(model.OwnerName))
        {
            return Ui("برای مالک بیرونی یا سایر مالک‌ها، نام مالک الزامی است.", "Owner name is required for external or other owner.");
        }

        var existing = await _db.AssetOwnershipShares
            .AsNoTracking()
            .Where(s => s.OperationalAssetId == model.OperationalAssetId)
            .ToListAsync();

        if (existing.Any(s => IsSameOwner(s, model) && DateRangesOverlap(s.EffectiveFrom, s.EffectiveTo, model.EffectiveFrom, model.EffectiveTo)))
        {
            return Ui("همین مالک برای این دارایی یک دوره مالکیت هم‌پوشان دارد.", "The same owner already has an overlapping ownership period for this asset.");
        }

        var activeAtEffectiveFrom = existing
            .Where(s => IsActiveOn(s, model.EffectiveFrom.Date))
            .Sum(s => s.SharePercent);
        if (activeAtEffectiveFrom + model.SharePercent > 100m + PercentTolerance)
        {
            return Ui("مجموع سهم‌های مالکیت فعال در تاریخ شروع از 100٪ بیشتر می‌شود.", "Active ownership shares exceed 100% on the effective date.");
        }

        return null;
    }

    private async Task ValidateRentCounterpartyAsync(AssetRentCreateViewModel model)
    {
        if (model.ChargedToType is AssetRentChargedToType.PurchaseContract or AssetRentChargedToType.SalesContract)
        {
            if (!model.ChargedToContractId.HasValue)
            {
                ModelState.AddModelError(nameof(model.ChargedToContractId), Ui("برای این نوع طرف حساب، انتخاب قرارداد الزامی است.", "Contract is required for this charged-to type."));
                return;
            }

            var contract = await _db.Contracts.AsNoTracking().FirstOrDefaultAsync(c => c.Id == model.ChargedToContractId.Value);
            if (contract is null)
            {
                ModelState.AddModelError(nameof(model.ChargedToContractId), Ui("انتخاب قرارداد معتبر نیست.", "Contract selection is invalid."));
                return;
            }

            if (model.ChargedToType == AssetRentChargedToType.PurchaseContract && contract.ContractType != ContractType.Purchase)
            {
                ModelState.AddModelError(nameof(model.ChargedToContractId), Ui("قرارداد انتخاب‌شده باید قرارداد خرید باشد.", "Selected contract must be a purchase contract."));
            }

            if (model.ChargedToType == AssetRentChargedToType.SalesContract && contract.ContractType != ContractType.Sale)
            {
                ModelState.AddModelError(nameof(model.ChargedToContractId), Ui("قرارداد انتخاب‌شده باید قرارداد فروش باشد.", "Selected contract must be a sales contract."));
            }
        }

        if (model.ChargedToCustomerId.HasValue
            && !await _db.Customers.AsNoTracking().AnyAsync(c => c.Id == model.ChargedToCustomerId.Value))
        {
            ModelState.AddModelError(nameof(model.ChargedToCustomerId), Ui("انتخاب مشتری معتبر نیست.", "Customer selection is invalid."));
        }

        if (model.ChargedToCompanyId.HasValue
            && !await _db.Companies.AsNoTracking().AnyAsync(c => c.Id == model.ChargedToCompanyId.Value))
        {
            ModelState.AddModelError(nameof(model.ChargedToCompanyId), Ui("انتخاب شرکت معتبر نیست.", "Company selection is invalid."));
        }

        if (model.ChargedToPartnerId.HasValue
            && !await _db.Partners.AsNoTracking().AnyAsync(p => p.Id == model.ChargedToPartnerId.Value))
        {
            ModelState.AddModelError(nameof(model.ChargedToPartnerId), Ui("انتخاب شریک معتبر نیست.", "Partner selection is invalid."));
        }

        if (model.ChargedToServiceProviderId.HasValue
            && !await _db.ServiceProviders.AsNoTracking().AnyAsync(p => p.Id == model.ChargedToServiceProviderId.Value))
        {
            ModelState.AddModelError(nameof(model.ChargedToServiceProviderId), Ui("انتخاب شرکت خدماتی معتبر نیست.", "Service provider selection is invalid."));
        }

        if (model.ChargedToType == AssetRentChargedToType.Customer
            && !model.ChargedToCustomerId.HasValue
            && string.IsNullOrWhiteSpace(model.NewCustomerName))
        {
            ModelState.AddModelError(nameof(model.ChargedToCustomerId), Ui("برای کرایه به مشتری، انتخاب مشتری الزامی است.", "Customer is required for customer rental."));
        }

        if (model.ChargedToType == AssetRentChargedToType.Partner && !model.ChargedToPartnerId.HasValue)
        {
            ModelState.AddModelError(nameof(model.ChargedToPartnerId), Ui("برای استفاده شریک، انتخاب شریک الزامی است.", "Partner is required for partner use."));
        }

        if (model.UsageType == AssetRentUsageType.ExternalCustomerRental
            && !model.ChargedToCustomerId.HasValue
            && string.IsNullOrWhiteSpace(model.NewCustomerName)
            && !model.ChargedToCompanyId.HasValue
            && !model.ChargedToServiceProviderId.HasValue)
        {
            ModelState.AddModelError(string.Empty, Ui("کرایه بیرونی باید به مشتری، شرکت یا شرکت خدماتی نسبت داده شود.", "External rental must be charged to a customer, company or service provider."));
        }

        if (model.ChargedToType == AssetRentChargedToType.Other && !model.ChargedToServiceProviderId.HasValue)
        {
            ModelState.AddModelError(nameof(model.ChargedToServiceProviderId), Ui("برای شرکت خدماتی، انتخاب شرکت خدماتی الزامی است.", "Service provider is required."));
        }
    }

    private async Task<List<AssetOwnershipShare>> GetActiveOwnershipSharesAsync(int assetId, DateTime date)
    {
        var utcDate = ToUtcDate(date);
        return await _db.AssetOwnershipShares
            .AsNoTracking()
            .Where(s => s.OperationalAssetId == assetId
                && s.EffectiveFrom <= utcDate
                && (!s.EffectiveTo.HasValue || s.EffectiveTo.Value >= utcDate))
            .OrderBy(s => s.Id)
            .ToListAsync();
    }

    private static IReadOnlyList<AssetRentShare> BuildRentShareSnapshots(
        int rentTransactionId,
        decimal amountUsd,
        IReadOnlyList<AssetOwnershipShare> ownershipShares)
    {
        var rows = new List<AssetRentShare>();
        var allocated = 0m;
        for (var i = 0; i < ownershipShares.Count; i++)
        {
            var share = ownershipShares[i];
            var shareAmount = i == ownershipShares.Count - 1
                ? amountUsd - allocated
                : decimal.Round(amountUsd * share.SharePercent / 100m, 4, MidpointRounding.AwayFromZero);
            allocated += shareAmount;
            rows.Add(new AssetRentShare
            {
                AssetRentTransactionId = rentTransactionId,
                OwnerType = share.OwnerType,
                CompanyId = share.CompanyId,
                PartnerId = share.PartnerId,
                OwnerName = share.OwnerName,
                SharePercent = share.SharePercent,
                ShareAmountUsd = shareAmount,
                Notes = share.Notes
            });
        }

        return rows;
    }

    private async Task PopulateAssetFormLookupsAsync(OperationalAssetFormViewModel model)
    {
        ViewBag.AssetTypes = EnumOptions<OperationalAssetType>(model.AssetType);
        ViewBag.OperationalStatuses = EnumOptions<OperationalAssetStatus>(model.OperationalStatus);
        ViewBag.OwnershipModes = EnumOptions<OperationalAssetOwnershipMode>(model.OwnershipMode);
        ViewBag.Trucks = new SelectList(
            await _db.Trucks.AsNoTracking()
                .OrderBy(t => model.LinkedTruckId.HasValue && t.Id == model.LinkedTruckId.Value ? 0 : 1)
                .ThenBy(t => t.PlateNumber)
                .Take(LookupLimit)
                .Select(t => new { t.Id, Text = t.PlateNumber })
                .ToListAsync(),
            "Id",
            "Text",
            model.LinkedTruckId);
        ViewBag.StorageTanks = new SelectList(
            await StorageTankDisplay.LoadOptionsAsync(_db.StorageTanks.AsNoTracking()
                .OrderBy(t => model.LinkedStorageTankId.HasValue && t.Id == model.LinkedStorageTankId.Value ? 0 : 1)
                .ThenBy(t => t.DisplayName ?? t.TankCode)
                .Take(LookupLimit)),
            "Id",
            "Display",
            model.LinkedStorageTankId);
        ViewBag.Locations = new SelectList(
            await _db.Locations.AsNoTracking()
                .OrderBy(l => l.Name)
                .Take(LookupLimit)
                .Select(l => new { l.Id, Text = l.Name })
                .ToListAsync(),
            "Id",
            "Text",
            model.LocationId);
        ViewBag.Terminals = new SelectList(
            await _db.Terminals.AsNoTracking()
                .OrderBy(t => t.Name)
                .Take(LookupLimit)
                .Select(t => new { t.Id, Text = t.Code + " - " + t.Name })
                .ToListAsync(),
            "Id",
            "Text",
            model.TerminalId);
    }

    private async Task PopulateAssetManagementLookupsAsync()
    {
        var parties = new List<SelectListItem>();
        parties.AddRange(await _db.Companies.AsNoTracking().Where(x => x.IsActive).OrderBy(x => x.Name)
            .Select(x => new SelectListItem { Value = ((int)AccountingPartyType.Company) + ":" + x.Id, Text = "شرکت — " + x.Name }).ToListAsync());
        parties.AddRange(await _db.Partners.AsNoTracking().Where(x => x.IsActive).OrderBy(x => x.Name)
            .Select(x => new SelectListItem { Value = ((int)AccountingPartyType.Partner) + ":" + x.Id, Text = "شریک — " + x.Name }).ToListAsync());
        parties.AddRange(await _db.ServiceProviders.AsNoTracking().Where(x => x.IsActive).OrderBy(x => x.Name)
            .Select(x => new SelectListItem { Value = ((int)AccountingPartyType.ServiceProvider) + ":" + x.Id, Text = "شرکت خدماتی — " + x.Name }).ToListAsync());
        parties.AddRange(await _db.Drivers.AsNoTracking().Where(x => x.IsActive).OrderBy(x => x.FullName)
            .Select(x => new SelectListItem { Value = ((int)AccountingPartyType.Driver) + ":" + x.Id, Text = "راننده — " + x.FullName }).ToListAsync());
        parties.AddRange(await _db.Employees.AsNoTracking().Where(x => x.IsActive).OrderBy(x => x.FullName)
            .Select(x => new SelectListItem { Value = ((int)AccountingPartyType.Employee) + ":" + x.Id, Text = "کارمند — " + x.FullName }).ToListAsync());
        ViewBag.ResponsibleParties = parties;
        ViewBag.AssignmentDrivers = new SelectList(await _db.Drivers.AsNoTracking().Where(x => x.IsActive)
            .OrderBy(x => x.FullName).Select(x => new { x.Id, Name = x.FullName }).ToListAsync(), "Id", "Name");
        ViewBag.AssignmentTerminals = new SelectList(await _db.Terminals.AsNoTracking().Where(x => x.IsActive)
            .OrderBy(x => x.Name).Select(x => new { x.Id, Name = x.Code + " - " + x.Name }).ToListAsync(), "Id", "Name");
        ViewBag.MaintenanceJobTypes = EnumOptions<AssetMaintenanceJobType>();
        ViewBag.MaintenanceStatuses = EnumOptions<AssetMaintenanceStatus>();
        ViewBag.MeterTypes = EnumOptions<AssetMeterType>();
        ViewBag.AssetDocumentTypes = EnumOptions<AssetDocumentType>();
    }

    private async Task PopulateOwnershipLookupsAsync()
    {
        ViewBag.OwnerTypes = EnumOptions<AssetOwnerType>();
        ViewBag.Companies = new SelectList(
            await _db.Companies.AsNoTracking().OrderBy(c => c.Name).Select(c => new { c.Id, c.Name }).ToListAsync(),
            "Id",
            "Name");
        ViewBag.Partners = new SelectList(
            await _db.Partners.AsNoTracking().OrderBy(p => p.Name).Select(p => new { p.Id, p.Name }).ToListAsync(),
            "Id",
            "Name");
    }

    private async Task PopulateRentLookupsAsync(AssetRentCreateViewModel model)
    {
        var assets = await _db.OperationalAssets.AsNoTracking()
            .Where(a => a.IsActive || a.Id == model.OperationalAssetId)
            .OrderBy(a => a.AssetCode)
            .Select(a => new { a.Id, a.AssetType, Text = a.AssetCode + " - " + a.Name })
            .ToListAsync();

        ViewBag.Assets = new SelectList(
            assets.Select(a => new { a.Id, a.Text }).ToList(),
            "Id",
            "Text",
            model.OperationalAssetId);
        ViewBag.AssetTypeByIdJson = JsonSerializer.Serialize(
            assets.ToDictionary(a => a.Id, a => (int)a.AssetType));
        ViewBag.UsageTypes = EnumOptions<AssetRentUsageType>(model.UsageType);
        ViewBag.ChargedToTypes = EnumOptions<AssetRentChargedToType>(model.ChargedToType);
        ViewBag.Contracts = await ContractLookupAsync(model.ChargedToContractId);
        ViewBag.Customers = new SelectList(
            await _db.Customers.AsNoTracking().OrderBy(c => c.Name).Take(LookupLimit).Select(c => new { c.Id, c.Name }).ToListAsync(),
            "Id",
            "Name",
            model.ChargedToCustomerId);
        ViewBag.Companies = new SelectList(
            await _db.Companies.AsNoTracking().OrderBy(c => c.Name).Take(LookupLimit).Select(c => new { c.Id, c.Name }).ToListAsync(),
            "Id",
            "Name",
            model.ChargedToCompanyId);
        ViewBag.Partners = new SelectList(
            await _db.Partners.AsNoTracking().OrderBy(p => p.Name).Take(LookupLimit).Select(p => new { p.Id, p.Name }).ToListAsync(),
            "Id",
            "Name",
            model.ChargedToPartnerId);
        ViewBag.ServiceProviders = new SelectList(
            await _db.ServiceProviders.AsNoTracking().OrderBy(p => p.Name).Take(LookupLimit).Select(p => new { p.Id, p.Name }).ToListAsync(),
            "Id",
            "Name",
            model.ChargedToServiceProviderId);
        ViewBag.Currencies = new SelectList(
            await _db.Currencies.AsNoTracking().Where(c => c.IsActive).OrderBy(c => c.Code).Select(c => new { c.Code }).ToListAsync(),
            "Code",
            "Code",
            model.Currency);
    }

    private async Task PopulateProfitabilityLookupsAsync(OperationalAssetProfitabilityFilterViewModel filter)
    {
        ViewBag.AssetTypes = EnumOptions<OperationalAssetType>(filter.AssetType);
        ViewBag.UsageTypes = EnumOptions<AssetRentUsageType>(filter.UsageType);
        ViewBag.Assets = new SelectList(
            await _db.OperationalAssets.AsNoTracking().OrderBy(a => a.AssetCode).Select(a => new { a.Id, Text = a.AssetCode + " - " + a.Name }).ToListAsync(),
            "Id",
            "Text",
            filter.OperationalAssetId);
        ViewBag.Partners = new SelectList(
            await _db.Partners.AsNoTracking().OrderBy(p => p.Name).Select(p => new { p.Id, p.Name }).ToListAsync(),
            "Id",
            "Name",
            filter.PartnerId);
        ViewBag.Contracts = await ContractLookupAsync(filter.ContractId);
        ViewBag.Customers = new SelectList(
            await _db.Customers.AsNoTracking().OrderBy(c => c.Name).Select(c => new { c.Id, c.Name }).ToListAsync(),
            "Id",
            "Name",
            filter.CustomerId);
    }

    private async Task<SelectList> ContractLookupAsync(int? selectedId)
    {
        var contracts = await _db.Contracts
            .AsNoTracking()
            .OrderBy(c => selectedId.HasValue && c.Id == selectedId.Value ? 0 : 1)
            .ThenByDescending(c => c.ContractDate)
            .ThenBy(c => c.ContractNumber)
            .Take(LookupLimit)
            .Select(c => new { c.Id, c.ContractName, c.ContractNumber })
            .ToListAsync();
        var options = contracts.Select(c => new ContractLookupOption(
            c.Id,
            ContractUiText.FormatDisplayLabel(c.ContractName, c.ContractNumber)));
        return new SelectList(options, "Id", "Display", selectedId);
    }

    private static void ApplyAssetForm(OperationalAsset asset, OperationalAssetFormViewModel model)
    {
        asset.AssetCode = model.AssetCode;
        asset.Name = model.Name;
        asset.AssetType = model.AssetType;
        asset.LinkedTruckId = model.LinkedTruckId;
        asset.LinkedStorageTankId = model.LinkedStorageTankId;
        asset.CapacityMt = model.CapacityMt;
        asset.LocationId = model.LocationId;
        asset.TerminalId = model.TerminalId;
        asset.AcquisitionDate = model.AcquisitionDate;
        asset.AcquisitionCostUsd = model.AcquisitionCostUsd;
        asset.InServiceDate = model.InServiceDate;
        asset.DisposalDate = model.DisposalDate;
        asset.OperationalStatus = model.OperationalStatus;
        asset.OwnershipMode = model.OwnershipMode;
        asset.MonthlyDepreciationUsd = model.MonthlyDepreciationUsd;
        asset.DefaultInternalRateUsd = model.DefaultInternalRateUsd;
        asset.DefaultExternalRateUsd = model.DefaultExternalRateUsd;
        asset.IsActive = model.IsActive;
        asset.Notes = model.Notes;
    }

    private static OperationalAssetFormViewModel ToFormModel(OperationalAsset asset)
        => new()
        {
            Id = asset.Id,
            AssetCode = asset.AssetCode,
            Name = asset.Name,
            AssetType = asset.AssetType,
            LinkedTruckId = asset.LinkedTruckId,
            LinkedStorageTankId = asset.LinkedStorageTankId,
            CapacityMt = asset.CapacityMt,
            LocationId = asset.LocationId,
            TerminalId = asset.TerminalId,
            AcquisitionDate = asset.AcquisitionDate,
            AcquisitionCostUsd = asset.AcquisitionCostUsd,
            InServiceDate = asset.InServiceDate,
            DisposalDate = asset.DisposalDate,
            OperationalStatus = asset.OperationalStatus,
            OwnershipMode = asset.OwnershipMode,
            MonthlyDepreciationUsd = asset.MonthlyDepreciationUsd,
            DefaultInternalRateUsd = asset.DefaultInternalRateUsd,
            DefaultExternalRateUsd = asset.DefaultExternalRateUsd,
            IsActive = asset.IsActive,
            Notes = asset.Notes
        };

    private static void NormalizeAssetForm(OperationalAssetFormViewModel model)
    {
        model.AssetCode = model.AssetCode.Trim();
        model.Name = model.Name.Trim();
        model.AcquisitionDate = ToUtcDate(model.AcquisitionDate);
        model.InServiceDate = ToUtcDate(model.InServiceDate);
        model.DisposalDate = ToUtcDate(model.DisposalDate);
        model.Notes = string.IsNullOrWhiteSpace(model.Notes) ? null : model.Notes.Trim();
    }

    private static void NormalizeOwnershipModel(AssetOwnershipShareCreateViewModel model)
    {
        model.EffectiveFrom = ToUtcDate(model.EffectiveFrom);
        model.EffectiveTo = ToUtcDate(model.EffectiveTo);
        model.OwnerName = string.IsNullOrWhiteSpace(model.OwnerName) ? null : model.OwnerName.Trim();
        model.Notes = string.IsNullOrWhiteSpace(model.Notes) ? null : model.Notes.Trim();
    }

    private static void NormalizeRentModel(AssetRentCreateViewModel model)
    {
        model.RentDate = ToUtcDate(model.RentDate);
        model.Currency = SystemCurrency.Normalize(model.Currency);
        if (SystemCurrency.IsBaseCurrency(model.Currency))
        {
            model.FxRateToUsd = 1m;
        }

        model.ReferenceDocument = string.IsNullOrWhiteSpace(model.ReferenceDocument) ? null : model.ReferenceDocument.Trim();
        model.Description = string.IsNullOrWhiteSpace(model.Description) ? null : model.Description.Trim();
        model.NewCustomerName = string.IsNullOrWhiteSpace(model.NewCustomerName) ? null : model.NewCustomerName.Trim();
    }

    private async Task<Customer?> ResolveNewRentCustomerAsync(AssetRentCreateViewModel model)
    {
        if (model.ChargedToType != AssetRentChargedToType.Customer
            || model.ChargedToCustomerId.HasValue
            || string.IsNullOrWhiteSpace(model.NewCustomerName))
        {
            return null;
        }

        var name = model.NewCustomerName.Trim();
        var existingCustomer = await _db.Customers
            .FirstOrDefaultAsync(c => c.Name == name || c.NamePersian == name);

        if (existingCustomer is not null)
        {
            model.ChargedToCustomerId = existingCustomer.Id;
            return null;
        }

        return new Customer
        {
            Name = name,
            IsActive = true,
            CreatedAtUtc = DateTime.UtcNow
        };
    }

    private static void NormalizeProfitabilityFilter(OperationalAssetProfitabilityFilterViewModel filter)
    {
        var today = AfghanistanBusinessClock.SystemToday;
        var defaultFromDate = new DateTime(today.Year, today.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        filter.FromDate = filter.FromDate.HasValue ? ToUtcDate(filter.FromDate.Value) : defaultFromDate;
        filter.ToDate = filter.ToDate.HasValue ? ToUtcDate(filter.ToDate.Value) : today;
        if (filter.ToDate < filter.FromDate)
        {
            (filter.FromDate, filter.ToDate) = (filter.ToDate, filter.FromDate);
        }
    }

    private static decimal ResolveRentAmountOriginal(AssetRentCreateViewModel model)
    {
        if (model.AmountOriginal.HasValue && model.AmountOriginal.Value > 0m)
        {
            return decimal.Round(model.AmountOriginal.Value, 4, MidpointRounding.AwayFromZero);
        }

        var billableQuantity = model.Days.GetValueOrDefault() > 0m
            ? model.Days!.Value
            : model.DistanceKm.GetValueOrDefault() > 0m
                ? model.DistanceKm!.Value
                : model.QuantityMt.GetValueOrDefault() > 0m
                    ? model.QuantityMt!.Value
                    : 1m;
        return decimal.Round(model.Rate * billableQuantity, 4, MidpointRounding.AwayFromZero);
    }

    private void ValidateRentMeasurementInputs(AssetRentCreateViewModel model, OperationalAssetType assetType)
    {
        var profile = ResolveRentMeasurementProfile(assetType);
        if (profile == RentMeasurementProfile.Transport && model.QuantityMt.GetValueOrDefault() > 0m)
        {
            ModelState.AddModelError(nameof(model.QuantityMt), Ui("برای دارایی حمل‌ونقل، فیلد مقدار MT قابل استفاده نیست.", "Quantity MT is not applicable for transport assets."));
        }

        if (profile == RentMeasurementProfile.Storage && model.DistanceKm.GetValueOrDefault() > 0m)
        {
            ModelState.AddModelError(nameof(model.DistanceKm), Ui("برای دارایی ثابت، فیلد مسافت KM قابل استفاده نیست.", "Distance KM is not applicable for stationary assets."));
        }
    }

    private static void NormalizeRentMeasurementInputs(AssetRentCreateViewModel model, OperationalAssetType assetType)
    {
        var profile = ResolveRentMeasurementProfile(assetType);
        if (profile == RentMeasurementProfile.Transport)
        {
            model.QuantityMt = null;
        }

        if (profile == RentMeasurementProfile.Storage)
        {
            model.DistanceKm = null;
        }
    }

    private static RentMeasurementProfile ResolveRentMeasurementProfile(OperationalAssetType assetType)
        => assetType switch
        {
            OperationalAssetType.Truck or OperationalAssetType.Trailer or OperationalAssetType.TankerTruck or OperationalAssetType.Wagon
                => RentMeasurementProfile.Transport,
            OperationalAssetType.StorageTank or OperationalAssetType.Warehouse or OperationalAssetType.Terminal
                => RentMeasurementProfile.Storage,
            _ => RentMeasurementProfile.Flexible
        };

    private enum RentMeasurementProfile
    {
        Flexible = 0,
        Transport = 1,
        Storage = 2
    }

    private static decimal CalculateDepreciation(decimal monthlyDepreciationUsd, DateTime fromDate, DateTime toDate)
    {
        if (monthlyDepreciationUsd <= 0m || toDate < fromDate)
        {
            return 0m;
        }

        var days = (toDate.Date - fromDate.Date).Days + 1;
        return decimal.Round(monthlyDepreciationUsd * days / 30m, 4, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// بازهٔ پیش‌فرض پروندهٔ دارایی: دوازده ماه گذشته. بازهٔ «ماه جاری» باعث می‌شد کاربر روز اول ماه
    /// صفحهٔ خالی ببیند و فکر کند سابقهٔ دارایی پاک شده است.
    /// </summary>
    private static DateTime DefaultProfilePeriodFrom()
        => ToUtcDate(AfghanistanBusinessClock.SystemToday.AddMonths(-12));

    /// <summary>
    /// بازهٔ پیش‌فرض پروندهٔ دارایی که برای پوشش دادن یک تاریخ مشخص گسترده شده است،
    /// تا ردیف تازه‌ذخیره‌شده بعد از redirect حتماً در لیست دیده شود.
    /// </summary>
    private static (DateTime FromDate, DateTime ToDate) ResolveProfilePeriodFor(DateTime businessDate)
    {
        var today = AfghanistanBusinessClock.SystemToday;
        var from = DefaultProfilePeriodFrom();
        var to = today;
        var target = ToUtcDate(businessDate);
        if (target < from)
        {
            from = target;
        }

        if (target > to)
        {
            to = target;
        }

        return (from, to);
    }

    private static DateTime ToUtcDate(DateTime value)
        => DateTime.SpecifyKind(value.Date, DateTimeKind.Utc);

    private static DateTime? ToUtcDate(DateTime? value)
        => value.HasValue ? ToUtcDate(value.Value) : null;

    private AssetOwnershipShareRowViewModel ToOwnershipShareRow(AssetOwnershipShare share)
        => new()
        {
            Id = share.Id,
            OwnerType = share.OwnerType,
            OwnerName = OwnerLabel(share),
            SharePercent = share.SharePercent,
            EffectiveFrom = share.EffectiveFrom,
            EffectiveTo = share.EffectiveTo,
            Notes = share.Notes,
            IsActiveNow = IsActiveOn(share, AfghanistanBusinessClock.SystemToday)
        };

    private AssetRentRowViewModel ToRentRow(AssetRentTransaction rent, IReadOnlyDictionary<int, int> receiptLegMap)
        => new()
        {
            Id = rent.Id,
            RentDate = rent.RentDate,
            UsageType = rent.UsageType,
            ChargedToType = rent.ChargedToType,
            ChargedToName = ChargedToLabel(rent),
            Source = BuildRentSource(rent, receiptLegMap),
            ReferenceDocument = rent.ReferenceDocument,
            QuantityMt = rent.QuantityMt,
            DistanceKm = rent.DistanceKm,
            Days = rent.Days,
            AmountOriginal = rent.AmountOriginal,
            Currency = rent.Currency,
            FxRateToUsd = rent.FxRateToUsd,
            AmountUsd = rent.AmountUsd,
            Description = rent.Description,
            IsPostedToLedger = rent.IsPostedToLedger,
            // وضعیت دفتر از همان سیاستی خوانده می‌شود که ثبت را انجام می‌دهد، نه از قضاوت View.
            IsSystemGenerated = AssetRentPostingPolicy.IsSystemGenerated(rent),
            PostingSkipReason = AssetRentPostingPolicy.ResolveSkipReason(rent)
        };

    // کرایهٔ حمل/رسید که با دارایی عملیاتی خودِ شرکت انجام شده، برای آن دارایی درآمد است نه هزینه.
    // دستهٔ نوع‌مصرف که نشان می‌دهد کار با خودِ دارایی شرکت انجام شده (کرایه/حمل) ⇒ درآمد دارایی، نه هزینه.
    // همهٔ کدهای کرایهٔ سیستم (TRANSPORT-RECEIPT-FREIGHT / TRANSPORT-FREIGHT / TRUCK-DISPATCH-FREIGHT) و
    // کرایه‌های دستیِ Loading/InventoryTransport («کرایه حمل/واگن») با همین دسته ثبت می‌شوند.
    private const string AssetRevenueExpenseCategory = "Transport";

    // کرایه/استفاده از دارایی شرکت = درآمد دارایی؛ مصارف واقعی (ترمیم، تیل، پرزه، مصرف داخلی) = هزینه می‌مانند.
    private static bool IsAssetFreightIncome(ExpenseTransaction expense)
        => string.Equals(
            expense.ExpenseType?.Category,
            AssetRevenueExpenseCategory,
            StringComparison.OrdinalIgnoreCase)
        || string.Equals(
            expense.ExpenseType?.Code,
            InventoryTransportReceiptService.ReceiptFreightExpenseCode,
            StringComparison.OrdinalIgnoreCase)
        || string.Equals(
            expense.ExpenseType?.Code,
            InventoryTransportReceiptService.TransportFreightExpenseCode,
            StringComparison.OrdinalIgnoreCase);

    private AssetExpenseRowViewModel ToExpenseRow(ExpenseTransaction expense)
        => new()
        {
            Id = expense.Id,
            ExpenseDate = expense.ExpenseDate,
            ExpenseTypeName = expense.ExpenseType?.NamePersian ?? expense.ExpenseType?.Name ?? "-",
            Source = BuildExpenseSource(expense),
            ContractNumber = expense.Contract?.ContractNumber,
            ShipmentCode = expense.Shipment?.ShipmentCode,
            TransportLegLabel = BuildTransportLegLabel(expense.TransportLeg),
            TruckDispatchLabel = BuildTruckDispatchLabel(expense.TruckDispatch),
            ServiceProviderName = expense.ServiceProvider?.Name,
            AmountUsd = expense.AmountUsd,
            IsFreightIncome = IsAssetFreightIncome(expense),
            Description = expense.Description
        };

    // ---------------------------------------------------------------------------------------
    // «از کجا آمده؟» — هر ردیفِ خودکار باید سندِ سازنده‌اش را با متن ساده و لینک زنده نشان دهد.
    // رسیدِ حمل صفحهٔ مستقل ندارد، پس لینکش به حملِ همان رسید می‌رود و متن، رسید را نام می‌برد.
    // ---------------------------------------------------------------------------------------
    private AssetSourceLinkViewModel? BuildRentSource(AssetRentTransaction rent, IReadOnlyDictionary<int, int> receiptLegMap)
    {
        if (rent.LoadingRegisterId.HasValue)
        {
            return SourceLink(
                Ui("بارگیری", "Loading"),
                rent.LoadingRegisterId.Value,
                Url?.Action("Details", "Loading", new { id = rent.LoadingRegisterId.Value }));
        }

        if (rent.TransportLegId.HasValue)
        {
            return SourceLink(
                Ui("حمل", "Transport"),
                rent.TransportLegId.Value,
                Url?.Action("Details", "InventoryTransportLegs", new { id = rent.TransportLegId.Value }));
        }

        if (rent.InventoryTransportReceiptId.HasValue)
        {
            var legId = receiptLegMap.TryGetValue(rent.InventoryTransportReceiptId.Value, out var value) ? value : (int?)null;
            return SourceLink(
                Ui("رسید حمل", "Transport receipt"),
                rent.InventoryTransportReceiptId.Value,
                legId.HasValue ? Url?.Action("Details", "InventoryTransportLegs", new { id = legId.Value }) : null);
        }

        if (rent.TruckDispatchId.HasValue)
        {
            return SourceLink(
                Ui("ارسال با موتر", "Truck dispatch"),
                rent.TruckDispatchId.Value,
                Url?.Action("Details", "Dispatch", new { id = rent.TruckDispatchId.Value }));
        }

        return null;
    }

    private AssetSourceLinkViewModel BuildExpenseSource(ExpenseTransaction expense)
    {
        if (expense.TruckDispatchId.HasValue)
        {
            return SourceLink(
                Ui("ارسال با موتر", "Truck dispatch"),
                expense.TruckDispatchId.Value,
                Url?.Action("Details", "Dispatch", new { id = expense.TruckDispatchId.Value }));
        }

        if (expense.TransportLegId.HasValue)
        {
            return SourceLink(
                Ui("حمل", "Transport"),
                expense.TransportLegId.Value,
                Url?.Action("Details", "InventoryTransportLegs", new { id = expense.TransportLegId.Value }));
        }

        if (expense.LoadingRegisterId.HasValue)
        {
            return SourceLink(
                Ui("بارگیری", "Loading"),
                expense.LoadingRegisterId.Value,
                Url?.Action("Details", "Loading", new { id = expense.LoadingRegisterId.Value }));
        }

        return SourceLink(
            Ui("سند مصرف", "Expense document"),
            expense.Id,
            Url?.Action("Details", "Expenses", new { id = expense.Id }));
    }

    private AssetSourceLinkViewModel SourceLink(string documentName, int documentId, string? url)
        => new()
        {
            DocumentTypeName = documentName,
            DocumentId = documentId,
            Label = Ui($"ایجادشده از {documentName} #{documentId}", $"Created from {documentName} #{documentId}"),
            Url = url
        };

    /// <summary>
    /// «کارکرد» — یک فهرست زمانی از کارِ دارایی. کرایه‌های ثبت‌شده و کرایهٔ حملی که با همین دارایی
    /// انجام شده هر دو یک «کار» را نشان می‌دهند، پس اگر هر دو به یک سند اشاره کنند فقط یک ردیف می‌آید.
    /// </summary>
    private List<AssetWorkRowViewModel> BuildWorkRows(
        IReadOnlyList<AssetRentRowViewModel> rentRows,
        IReadOnlyList<AssetExpenseRowViewModel> freightIncomeRows)
    {
        var rows = new List<AssetWorkRowViewModel>(rentRows.Count + freightIncomeRows.Count);
        var seenSources = new HashSet<string>(StringComparer.Ordinal);

        foreach (var rent in rentRows)
        {
            if (rent.Source is not null)
            {
                seenSources.Add(rent.Source.Label);
            }

            var isInternal = rent.UsageType == AssetRentUsageType.InternalCompanyUse;
            rows.Add(new AssetWorkRowViewModel
            {
                Date = rent.RentDate,
                OperationTypeName = rent.Source?.DocumentTypeName ?? Ui("ثبت دستی", "Recorded by hand"),
                Source = rent.Source,
                ContractNumber = rent.ChargedToType is AssetRentChargedToType.PurchaseContract or AssetRentChargedToType.SalesContract
                    ? rent.ChargedToName
                    : null,
                QuantityMt = rent.QuantityMt,
                DistanceKm = rent.DistanceKm,
                CounterpartyName = isInternal ? null : rent.ChargedToName,
                IsInternalUse = isInternal,
                UsageText = isInternal
                    ? Ui("استفاده داخلی شرکت", "Company internal use")
                    : Ui("کرایه به بیرون", "Rented out"),
                UsageHint = isInternal
                    ? Ui("برای عملیات خود شرکت استفاده شده است.", "Used for the company's own operation.")
                    : null
            });
        }

        foreach (var expense in freightIncomeRows)
        {
            if (expense.Source is not null && !seenSources.Add(expense.Source.Label))
            {
                continue;
            }

            rows.Add(new AssetWorkRowViewModel
            {
                Date = expense.ExpenseDate,
                OperationTypeName = expense.Source?.DocumentTypeName ?? expense.ExpenseTypeName,
                Source = expense.Source,
                ContractNumber = expense.ContractNumber,
                ShipmentCode = expense.ShipmentCode,
                RouteText = expense.TransportLegLabel ?? expense.TruckDispatchLabel,
                IsInternalUse = true,
                UsageText = Ui("استفاده داخلی شرکت", "Company internal use"),
                UsageHint = Ui("برای عملیات خود شرکت استفاده شده است.", "Used for the company's own operation.")
            });
        }

        return rows
            .OrderByDescending(row => row.Date)
            .ThenByDescending(row => row.Source?.Label, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>عوایدِ استفادهٔ خود شرکت: نه طلبِ بیرونی می‌سازد و نه پرداختی دارد.</summary>
    private List<AssetIncomeRowViewModel> BuildInternalIncomeRows(
        IReadOnlyList<AssetRentRowViewModel> rentRows,
        IReadOnlyList<AssetExpenseRowViewModel> freightIncomeRows)
    {
        var rows = rentRows
            .Where(rent => rent.UsageType == AssetRentUsageType.InternalCompanyUse)
            .Select(rent => new AssetIncomeRowViewModel
            {
                Id = rent.Id,
                Date = rent.RentDate,
                SourceTypeName = rent.Source is null
                    ? Ui("ثبت دستی", "Recorded by hand")
                    : Ui("از عملیات شرکت", "From a company operation"),
                Source = rent.Source,
                CounterpartyName = rent.ChargedToName,
                AmountOriginal = rent.AmountOriginal,
                Currency = rent.Currency,
                AmountUsd = rent.AmountUsd,
                StateText = OperationalAssetLabels.PostingState(rent.IsPostedToLedger, rent.PostingSkipReason, HttpContext),
                NeedsAttention = rent.IsPostingMissing,
                CanCancel = !rent.IsSystemGenerated,
                Description = rent.Description
            })
            .ToList();

        rows.AddRange(freightIncomeRows.Select(expense => new AssetIncomeRowViewModel
        {
            Id = expense.Id,
            Date = expense.ExpenseDate,
            SourceTypeName = Ui("کرایه حمل با وسیلهٔ شرکت", "Freight carried by the company's own vehicle"),
            Source = expense.Source,
            ContractNumber = expense.ContractNumber,
            AmountOriginal = expense.AmountUsd,
            Currency = SystemCurrency.BaseCurrencyCode,
            AmountUsd = expense.AmountUsd,
            StateText = Ui("استفاده داخلی شرکت — پرداخت بیرونی ندارد", "Company internal use — no outside payment"),
            NeedsAttention = false,
            CanCancel = false,
            Description = expense.Description
        }));

        return rows.OrderByDescending(row => row.Date).ThenByDescending(row => row.Id).ToList();
    }

    /// <summary>عوایدِ کرایه دادن دارایی به بیرون: طرف حساب واقعی دارد.</summary>
    private List<AssetIncomeRowViewModel> BuildExternalIncomeRows(IReadOnlyList<AssetRentRowViewModel> rentRows)
        => rentRows
            .Where(rent => rent.UsageType != AssetRentUsageType.InternalCompanyUse)
            .Select(rent => new AssetIncomeRowViewModel
            {
                Id = rent.Id,
                Date = rent.RentDate,
                SourceTypeName = OperationalAssetLabels.UsageType(rent.UsageType, HttpContext),
                Source = rent.Source,
                CounterpartyName = rent.ChargedToName,
                AmountOriginal = rent.AmountOriginal,
                Currency = rent.Currency,
                AmountUsd = rent.AmountUsd,
                StateText = OperationalAssetLabels.PostingState(rent.IsPostedToLedger, rent.PostingSkipReason, HttpContext),
                NeedsAttention = rent.IsPostingMissing,
                CanCancel = !rent.IsSystemGenerated,
                Description = rent.Description
            })
            .OrderByDescending(row => row.Date)
            .ThenByDescending(row => row.Id)
            .ToList();

    private AssetRentShareRowViewModel ToRentShareRow(AssetRentShare share)
        => new()
        {
            RentTransactionId = share.AssetRentTransactionId,
            RentDate = share.AssetRentTransaction?.RentDate ?? default,
            AssetName = share.AssetRentTransaction?.OperationalAsset?.Name ?? "-",
            UsageType = share.AssetRentTransaction?.UsageType ?? AssetRentUsageType.Other,
            OwnerType = share.OwnerType,
            OwnerName = OwnerLabel(share),
            SharePercent = share.SharePercent,
            ShareAmountUsd = share.ShareAmountUsd
        };

    private static string? BuildTransportLegLabel(InventoryTransportLeg? leg)
        => leg is null
            ? null
            : $"#{leg.Id} - {FirstNonEmpty(leg.RwbNo, leg.WagonNumber) ?? leg.TransportType.ToString()}";

    private static string? BuildTruckDispatchLabel(TruckDispatch? dispatch)
        => dispatch is null
            ? null
            : $"#{dispatch.Id} - {dispatch.Truck?.PlateNumber ?? dispatch.DispatchDate.ToString("yyyy-MM-dd")}";

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private string BuildLinkedResourceText(OperationalAsset asset)
    {
        if (asset.LinkedTruck is not null)
        {
            return Ui("موتر ", "Truck ") + asset.LinkedTruck.PlateNumber;
        }

        if (asset.LinkedStorageTank is not null)
        {
            return Ui("مخزن ", "Tank ") + StorageTankDisplay.Build(asset.LinkedStorageTank);
        }

        return "-";
    }

    private string OwnerLabel(AssetOwnershipShare share)
        => share.OwnerType switch
        {
            AssetOwnerType.Company => share.Company?.Name ?? (share.CompanyId.HasValue ? Ui("شرکت #", "Company #") + share.CompanyId : Ui("شرکت", "Company")),
            AssetOwnerType.Partner => share.Partner?.Name ?? (share.PartnerId.HasValue ? Ui("شریک #", "Partner #") + share.PartnerId : Ui("شریک", "Partner")),
            _ => share.OwnerName ?? "-"
        };

    private string OwnerLabel(AssetRentShare share)
        => share.OwnerType switch
        {
            AssetOwnerType.Company => share.Company?.Name ?? (share.CompanyId.HasValue ? Ui("شرکت #", "Company #") + share.CompanyId : Ui("شرکت", "Company")),
            AssetOwnerType.Partner => share.Partner?.Name ?? (share.PartnerId.HasValue ? Ui("شریک #", "Partner #") + share.PartnerId : Ui("شریک", "Partner")),
            _ => share.OwnerName ?? "-"
        };

    private string ChargedToLabel(AssetRentTransaction rent)
        => rent.ChargedToType switch
        {
            AssetRentChargedToType.PurchaseContract or AssetRentChargedToType.SalesContract =>
                rent.ChargedToContract?.ContractNumber ?? (rent.ChargedToContractId.HasValue ? Ui("قرارداد #", "Contract #") + rent.ChargedToContractId : "-"),
            AssetRentChargedToType.Customer =>
                rent.ChargedToCustomer?.Name ?? (rent.ChargedToCustomerId.HasValue ? Ui("مشتری #", "Customer #") + rent.ChargedToCustomerId : "-"),
            AssetRentChargedToType.CompanyInternal =>
                rent.ChargedToCompany?.Name ?? (rent.ChargedToCompanyId.HasValue ? Ui("شرکت #", "Company #") + rent.ChargedToCompanyId : Ui("داخلی شرکت", "Company Internal")),
            AssetRentChargedToType.Partner =>
                rent.ChargedToPartner?.Name ?? (rent.ChargedToPartnerId.HasValue ? Ui("شریک #", "Partner #") + rent.ChargedToPartnerId : Ui("شریک", "Partner")),
            _ => rent.ChargedToServiceProvider?.Name ?? "-"
        };

    private static bool IsActiveOn(AssetOwnershipShare share, DateTime date)
        => share.EffectiveFrom.Date <= date.Date
           && (!share.EffectiveTo.HasValue || share.EffectiveTo.Value.Date >= date.Date);

    private static bool DateRangesOverlap(DateTime aFrom, DateTime? aTo, DateTime bFrom, DateTime? bTo)
    {
        var aEnd = aTo?.Date ?? DateTime.MaxValue.Date;
        var bEnd = bTo?.Date ?? DateTime.MaxValue.Date;
        return aFrom.Date <= bEnd && bFrom.Date <= aEnd;
    }

    private static bool IsSameOwner(AssetOwnershipShare share, AssetOwnershipShareCreateViewModel model)
        => share.OwnerType == model.OwnerType
           && share.CompanyId == (model.OwnerType == AssetOwnerType.Company ? model.CompanyId : null)
           && share.PartnerId == (model.OwnerType == AssetOwnerType.Partner ? model.PartnerId : null)
           && string.Equals(share.OwnerName ?? "", model.OwnerName ?? "", StringComparison.OrdinalIgnoreCase);

    private static bool TryParsePartyKey(string? key, out AccountingPartyType partyType, out int partyId)
    {
        partyType = default;
        partyId = 0;
        var parts = key?.Split(':', 2, StringSplitOptions.TrimEntries);
        return parts is { Length: 2 }
            && int.TryParse(parts[0], out var typeValue)
            && Enum.IsDefined(typeof(AccountingPartyType), typeValue)
            && (partyType = (AccountingPartyType)typeValue) != default
            && int.TryParse(parts[1], out partyId)
            && partyId > 0;
    }

    private Task<bool> PartyExistsAsync(AccountingPartyType type, int id)
        => type switch
        {
            AccountingPartyType.Customer => _db.Customers.AsNoTracking().AnyAsync(x => x.Id == id && x.IsActive),
            AccountingPartyType.Supplier => _db.Suppliers.AsNoTracking().AnyAsync(x => x.Id == id && x.IsActive),
            AccountingPartyType.ServiceProvider => _db.ServiceProviders.AsNoTracking().AnyAsync(x => x.Id == id && x.IsActive),
            AccountingPartyType.Sarraf => _db.Sarrafs.AsNoTracking().AnyAsync(x => x.Id == id && x.IsActive),
            AccountingPartyType.Driver => _db.Drivers.AsNoTracking().AnyAsync(x => x.Id == id && x.IsActive),
            AccountingPartyType.Employee => _db.Employees.AsNoTracking().AnyAsync(x => x.Id == id && x.IsActive),
            AccountingPartyType.Partner => _db.Partners.AsNoTracking().AnyAsync(x => x.Id == id && x.IsActive),
            AccountingPartyType.Company => _db.Companies.AsNoTracking().AnyAsync(x => x.Id == id && x.IsActive),
            _ => Task.FromResult(false)
        };

    private async Task<Dictionary<(AccountingPartyType Type, int Id), string>> ResolvePartyNamesAsync(
        IReadOnlyCollection<AssetAssignment> assignments)
    {
        var result = new Dictionary<(AccountingPartyType, int), string>();
        foreach (var group in assignments.GroupBy(a => a.ResponsiblePartyType))
        {
            var ids = group.Select(a => a.ResponsiblePartyId).Distinct().ToArray();
            Dictionary<int, string> names = group.Key switch
            {
                AccountingPartyType.Customer => await _db.Customers.AsNoTracking().Where(x => ids.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Name),
                AccountingPartyType.Supplier => await _db.Suppliers.AsNoTracking().Where(x => ids.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Name),
                AccountingPartyType.ServiceProvider => await _db.ServiceProviders.AsNoTracking().Where(x => ids.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Name),
                AccountingPartyType.Sarraf => await _db.Sarrafs.AsNoTracking().Where(x => ids.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Name),
                AccountingPartyType.Driver => await _db.Drivers.AsNoTracking().Where(x => ids.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.FullName),
                AccountingPartyType.Employee => await _db.Employees.AsNoTracking().Where(x => ids.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.FullName),
                AccountingPartyType.Partner => await _db.Partners.AsNoTracking().Where(x => ids.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Name),
                AccountingPartyType.Company => await _db.Companies.AsNoTracking().Where(x => ids.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Name),
                _ => []
            };
            foreach (var item in names) result[(group.Key, item.Key)] = item.Value;
        }

        return result;
    }

    private string GetWebRootPath()
        => string.IsNullOrWhiteSpace(_environment?.WebRootPath)
            ? Path.Combine(Directory.GetCurrentDirectory(), "wwwroot")
            : _environment.WebRootPath;

    private List<SelectListItem> EnumOptions<TEnum>(TEnum? selected = null)
        where TEnum : struct, Enum
        => Enum.GetValues<TEnum>()
            .Select(value => new SelectListItem
            {
                Value = Convert.ToInt32(value).ToString(),
                Text = value switch
                {
                    OperationalAssetType assetType => OperationalAssetLabels.AssetType(assetType, HttpContext),
                    OperationalAssetOwnershipMode ownershipMode => OperationalAssetLabels.OwnershipMode(ownershipMode, HttpContext),
                    OperationalAssetStatus status => OperationalAssetLabels.Status(status, HttpContext),
                    AssetOwnerType ownerType => OperationalAssetLabels.OwnerType(ownerType, HttpContext),
                    AssetRentUsageType usageType => OperationalAssetLabels.UsageType(usageType, HttpContext),
                    AssetRentChargedToType chargedToType => OperationalAssetLabels.ChargedToType(chargedToType, HttpContext),
                    _ => value.ToString()
                },
                Selected = selected.HasValue && EqualityComparer<TEnum>.Default.Equals(value, selected.Value)
            })
            .ToList();

    private string Ui(string fa, string en) => UiText.T(HttpContext, fa, en);
}
