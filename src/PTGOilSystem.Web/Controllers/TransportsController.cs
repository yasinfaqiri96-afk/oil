using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Helpers;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.InventoryTransport;
using PTGOilSystem.Web.Security;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Exceptions;
using PTGOilSystem.Web.Services.Time;

namespace PTGOilSystem.Web.Controllers;

/// <summary>
/// Facade وبِ «حمل‌ها». این کنترلر فقط انتخاب مسیر و orchestration تراکنش را انجام می‌دهد؛
/// تمام قواعد مقدار/موجودی/فروش/کسری در ITransportWorkflowService باقی می‌ماند.
/// </summary>
[Authorize]
public sealed class TransportsController : Controller
{
    private const decimal Epsilon = 0.0001m;
    private readonly ApplicationDbContext _db;
    private readonly ITransportWorkflowService _workflow;
    private readonly ITransportQuantityService _quantities;
    private readonly IAfghanistanBusinessClock _clock;

    // PTG-P3-B — همان محافظ ضدتکراری فاز P0، برای مسیرهایی که بار واقعی جابه‌جا می‌کنند.
    private readonly IFormTokenGuard? _formTokens;

    // فقط برای برگشتِ مصرف کرایه هنگام لغو گروهی؛ همان adapter صفحهٔ دیسپچ.
    private readonly Services.Accounting.IExpenseAccountingAdapter? _expenseAccounting;

    // لغو گروهیِ تبدیل‌های بارگیری؛ همان سرویسی که «لغو سند حمل» صفحهٔ جزئیات حمل صدا می‌زند.
    private readonly InventoryTransportBatchService _batchTransport;

    public TransportsController(
        ApplicationDbContext db,
        ITransportWorkflowService workflow,
        ITransportQuantityService quantities,
        IAfghanistanBusinessClock clock,
        InventoryTransportBatchService batchTransport,
        IFormTokenGuard? formTokens = null,
        Services.Accounting.IExpenseAccountingAdapter? expenseAccounting = null)
    {
        _db = db;
        _workflow = workflow;
        _quantities = quantities;
        _clock = clock;
        _batchTransport = batchTransport;
        _formTokens = formTokens;
        _expenseAccounting = expenseAccounting;
    }

    public IActionResult Index(string? state = null)
        => string.IsNullOrWhiteSpace(state)
            ? RedirectToAction("Index", "InventoryTransportLegs")
            : Redirect($"/InventoryTransportLegs?Filter.WorkflowState={Uri.EscapeDataString(state)}");

    public IActionResult Details(int id, string? returnUrl = null)
        => RedirectToAction("Details", "InventoryTransportLegs", new { id, returnUrl });

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpGet]
    public async Task<IActionResult> Create(TransportStartSourceKind sourceKind = TransportStartSourceKind.Inventory)
        => View(await BuildStartModelAsync(new TransportStartViewModel { SourceKind = sourceKind }));

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(TransportStartViewModel model)
    {
        switch (model.SourceKind)
        {
            case TransportStartSourceKind.Inventory:
                return RedirectToAction("CreateFromInventory", "InventoryTransportLegs");
            case TransportStartSourceKind.LoadingReceipt when model.LoadingReceiptId is > 0:
                return RedirectToAction(nameof(FromReceipt), new { loadingReceiptId = model.LoadingReceiptId.Value });
            // انتخاب حملِ مبدأ در خودِ فرم انتقال انجام می‌شود تا انتخاب گروهی هم ممکن باشد.
            case TransportStartSourceKind.ActiveTransport:
                return RedirectToAction(nameof(Continue), new { transportLegId = model.TransportLegId });
            case TransportStartSourceKind.LoadingReceipt:
                ModelState.AddModelError(nameof(model.LoadingReceiptId), "رسید/بارگیری مستقیم را انتخاب کنید.");
                break;
            default:
                ModelState.AddModelError(nameof(model.SourceKind), "منبع بار معتبر نیست.");
                break;
        }

        return View(await BuildStartModelAsync(model));
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpGet]
    public async Task<IActionResult> FromReceipt(int loadingReceiptId)
    {
        var model = new TransportStartFromReceiptViewModel
        {
            LoadingReceiptId = loadingReceiptId,
            TransportDate = _clock.Today
        };
        if (!await PopulateReceiptModelAsync(model))
        {
            return NotFound();
        }
        model.QuantityMt = model.AvailableQuantityMt;
        await PopulateVehicleLookupsAsync();
        return View(model);
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpGet]
    public async Task<IActionResult> FromLoading(int loadingRegisterId)
    {
        var model = new TransportStartFromLoadingViewModel
        {
            LoadingRegisterId = loadingRegisterId,
            TransportDate = _clock.Today
        };
        if (!await PopulateLoadingModelAsync(model))
        {
            return NotFound();
        }
        model.QuantityMt = model.AvailableQuantityMt;
        await PopulateVehicleLookupsAsync();
        return View(model);
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> FromLoading(
        TransportStartFromLoadingViewModel model,
        [FromForm(Name = FormTokenHtmlHelper.FieldName)] string? formToken = null)
    {
        if (!await PopulateLoadingModelAsync(model))
        {
            return NotFound();
        }
        if (model.QuantityMt > model.AvailableQuantityMt + Epsilon)
        {
            ModelState.AddModelError(nameof(model.QuantityMt), "مقدار حمل از باقیماندهٔ بارگیری بیشتر است.");
        }

        if (!ModelState.IsValid)
        {
            await PopulateVehicleLookupsAsync();
            return View(model);
        }

        _formTokens?.Stamp(formToken, "Transport.FromLoading", nameof(InventoryTransportLeg));
        try
        {
            var leg = await _workflow.StartFromLoadingAsync(new StartTransportFromLoadingCommand
            {
                LoadingRegisterId = model.LoadingRegisterId,
                QuantityMt = model.QuantityMt,
                TransportType = model.TransportType,
                TruckId = model.TruckId,
                WagonId = model.WagonId,
                VesselId = model.VesselId,
                DriverId = model.DriverId,
                ServiceProviderId = model.ServiceProviderId,
                TransportDate = model.TransportDate,
                Reference = model.Reference,
                Notes = model.Notes
            });
            TempData["ok"] = "بارگیری مستقیماً به حمل تبدیل شد؛ هیچ حرکت موجودی ساخته نشد.";
            return RedirectToAction(nameof(Details), new { id = leg.Id });
        }
        catch (DbUpdateException duplicate) when (_formTokens?.IsDuplicate(duplicate) == true)
        {
            TempData["err"] = "این حمل قبلاً ثبت شده است و دوباره ثبت نشد.";
            return RedirectToAction("Index", "InventoryTransportLegs");
        }
        catch (BusinessRuleException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            await PopulateLoadingModelAsync(model);
            await PopulateVehicleLookupsAsync();
            return View(model);
        }
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpGet]
    public async Task<IActionResult> BulkFromLoading(
        int[]? ids = null,
        string? returnUrl = null,
        [FromQuery] TransportBulkLoadingFilter? filter = null,
        bool useFilterSelection = false)
    {
        filter ??= new TransportBulkLoadingFilter();
        var model = new TransportBulkFromLoadingViewModel
        {
            TransportDate = _clock.Today,
            ReturnUrl = returnUrl,
            Filter = filter,
            UseFilterSelection = useFilterSelection
        };

        // خلاصهٔ فیلتر همیشه محاسبه می‌شود تا صفحه بتواند «به‌جای این N ردیف، همهٔ M نتیجه
        // را تبدیل کن» را پیشنهاد دهد. یک کوئری تجمیعی، مستقل از تعداد نتایج.
        var summary = await SummarizeConvertibleLoadingsAsync(filter);
        model.FilterMatchCount = summary.Count;
        model.FilterMatchQuantityMt = summary.QuantityMt;

        if (!useFilterSelection)
        {
            var requestedIds = (ids ?? []).Distinct().Where(id => id > 0).ToList();
            model.Rows = await BuildBulkLoadingRowsAsync(requestedIds);
            ViewBag.BulkRequestedCount = requestedIds.Count;
            ViewBag.BulkSkippedCount = Math.Max(requestedIds.Count - model.Rows.Count, 0);
        }
        else
        {
            // حالت فیلتر: تا سقفِ رندر، ردیف‌ها نشان داده می‌شوند و صفحه دقیقاً مثل حالت
            // انتخابِ دستی کار می‌کند (کاربر می‌تواند وسیله و مقدار هر ردیف را عوض کند)؛
            // فیلتر فقط راهی برای انتخابِ شناسه‌ها بوده است.
            //
            // بالاتر از آن، ردیف رندر نمی‌شود: هزاران <select> وسیله در هر ردیف صفحه را چند ده
            // مگابایت می‌کند و فرم هم از سقف فیلدها می‌گذرد. آنجا فقط خلاصه نشان داده می‌شود و
            // سرور خودش مجموعه را از همان فیلتر می‌سازد.
            ViewBag.BulkRequestedCount = summary.Count;
            ViewBag.BulkSkippedCount = 0;
            if (summary.Count > 0 && summary.Count <= TransportBulkFromLoadingViewModel.MaxRenderedRows)
            {
                model.Rows = await BuildBulkLoadingRowsAsync(
                    await ConvertibleLoadingIdsAsync(filter, TransportBulkFromLoadingViewModel.MaxRenderedRows));
                model.UseFilterSelection = false;
            }
        }

        await PopulateVehicleLookupsAsync();
        return View(model);
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost]
    [ValidateAntiForgeryToken]
    // فرمِ ردیفی برای هر بارگیری ~۱۱ فیلد post می‌کند؛ با سقف پیش‌فرض ۱۰۲۴ فیلد
    // (FormOptions.ValueCountLimit) درخواست از حدود ۹۳ ردیف به بالا پیش از رسیدن به اکشن
    // با 400 رد می‌شد. همان سقفی که مسیرهای گروهیِ دیگر پروژه دارند اینجا هم لازم است.
    [RequestFormLimits(ValueCountLimit = 200_000)]
    public async Task<IActionResult> BulkFromLoading(
        TransportBulkFromLoadingViewModel model,
        [FromForm(Name = FormTokenHtmlHelper.FieldName)] string? formToken = null)
    {
        model.Filter ??= new TransportBulkLoadingFilter();

        List<BulkStartTransportFromLoadingRow> rows;
        if (model.UseFilterSelection && ToCriteria(model.Filter).IsEmpty)
        {
            // محافظ: «همهٔ نتایج فیلتر» بدون هیچ فیلتری یعنی کل جدول.
            rows = [];
            ModelState.AddModelError(string.Empty, "برای تبدیل گروهیِ مبتنی بر فیلتر، دست‌کم یک شرط فیلتر لازم است.");
        }
        else if (model.UseFilterSelection)
        {
            // هیچ ردیفی post نشده؛ مجموعهٔ تبدیل دوباره از همان فیلتر ساخته می‌شود تا
            // کاربر مجبور نباشد هزاران فیلد بفرستد.
            //
            // یکی بیشتر از سقف خوانده می‌شود: اگر فیلتر بزرگ‌تر از سقف باشد، عملیات باید رد شود،
            // نه اینکه بی‌صدا فقط بخشی از نتایج را تبدیل کند و کاربر فکر کند همه تبدیل شده‌اند.
            var ids = await ConvertibleLoadingIdsAsync(
                model.Filter, TransportBulkFromLoadingViewModel.MaxRows + 1);
            rows = ids.Count > TransportBulkFromLoadingViewModel.MaxRows
                ? []
                : await BuildFilterSelectionRowsAsync(ids);
            if (ids.Count > TransportBulkFromLoadingViewModel.MaxRows)
            {
                ModelState.AddModelError(
                    string.Empty,
                    $"در یک عملیات حداکثر {TransportBulkFromLoadingViewModel.MaxRows:N0} بارگیری تبدیل می‌شود؛ فیلتر را محدودتر کنید.");
            }
        }
        else
        {
            rows = (model.Rows ?? [])
                .Where(r => r.Selected && r.LoadingRegisterId > 0)
                .Select(r => new BulkStartTransportFromLoadingRow
                {
                    LoadingRegisterId = r.LoadingRegisterId,
                    QuantityMt = r.QuantityMt,
                    TransportType = r.TransportType,
                    TruckId = r.TransportType == LoadingTransportType.Truck ? r.TruckId : null,
                    WagonId = r.TransportType == LoadingTransportType.Wagon ? r.WagonId : null,
                    VesselId = r.TransportType == LoadingTransportType.Vessel ? r.VesselId : null,
                    DriverId = r.TransportType == LoadingTransportType.Truck ? r.DriverId : null,
                    ServiceProviderId = r.ServiceProviderId,
                    Reference = r.Reference,
                    Label = r.LoadingLabel
                })
                .ToList();
        }

        if (rows.Count == 0 && ModelState.IsValid)
        {
            ModelState.AddModelError(string.Empty, model.UseFilterSelection
                ? "هیچ بارگیریِ قابل تبدیلی مطابق این فیلتر پیدا نشد."
                : "حداقل یک بارگیری را انتخاب کنید.");
        }
        else if (rows.Count > TransportBulkFromLoadingViewModel.MaxRows)
        {
            ModelState.AddModelError(
                string.Empty,
                $"در یک عملیات حداکثر {TransportBulkFromLoadingViewModel.MaxRows:N0} بارگیری تبدیل می‌شود؛ فیلتر را محدودتر کنید.");
        }

        if (!ModelState.IsValid)
        {
            return await RenderBulkAsync(model);
        }

        BulkStartTransportFromLoadingResult result;
        try
        {
            result = await _workflow.StartManyFromLoadingAsync(new BulkStartTransportFromLoadingCommand
            {
                Rows = rows,
                TransportDate = model.TransportDate,
                FormToken = formToken
            });
        }
        catch (DbUpdateException duplicate) when (_formTokens?.IsDuplicate(duplicate) == true)
        {
            TempData["err"] = "این عملیات قبلاً ثبت شده است و دوباره ثبت نشد.";
            return RedirectToAction("Index", "InventoryTransportLegs");
        }
        catch (BusinessRuleException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            return await RenderBulkAsync(model);
        }

        if (result.CreatedCount == 0)
        {
            foreach (var failure in DescribeFailures(result.Failures))
            {
                ModelState.AddModelError(string.Empty, failure);
            }
            return await RenderBulkAsync(model);
        }

        TempData["ok"] = $"{result.CreatedCount:N0} بارگیری به حمل تبدیل شد.";
        if (result.Failures.Count > 0)
        {
            TempData["err"] = "تبدیل نشد — " + string.Join(" | ", DescribeFailures(result.Failures));
        }
        return !string.IsNullOrWhiteSpace(model.ReturnUrl) && Url.IsLocalUrl(model.ReturnUrl)
            ? Redirect(model.ReturnUrl)
            : RedirectToAction("Index", "InventoryTransportLegs");
    }

    /// <summary>
    /// پیام خطای ردیف‌ها. برای عملیات بزرگ فقط چند نمونه نشان داده می‌شود؛ یک TempData با
    /// هزاران پیام نه در کوکی جا می‌شود و نه برای کاربر خواندنی است.
    /// </summary>
    private static IEnumerable<string> DescribeFailures(
        IReadOnlyList<BulkStartTransportFromLoadingFailure> failures)
    {
        const int sampleSize = 10;
        foreach (var failure in failures.Take(sampleSize))
        {
            var label = string.IsNullOrWhiteSpace(failure.Label)
                ? $"بارگیری #{failure.LoadingRegisterId}"
                : failure.Label;
            yield return $"{label}: {failure.Message}";
        }

        if (failures.Count > sampleSize)
        {
            yield return $"و {failures.Count - sampleSize:N0} مورد دیگر";
        }
    }

    private async Task<IActionResult> RenderBulkAsync(TransportBulkFromLoadingViewModel model)
    {
        var summary = await SummarizeConvertibleLoadingsAsync(model.Filter);
        model.FilterMatchCount = summary.Count;
        model.FilterMatchQuantityMt = summary.QuantityMt;
        if (!model.UseFilterSelection)
        {
            await RefreshBulkLoadingRowsAsync(model);
        }
        ViewBag.BulkRequestedCount = model.UseFilterSelection ? summary.Count : (model.Rows?.Count ?? 0);
        ViewBag.BulkSkippedCount = 0;
        await PopulateVehicleLookupsAsync();
        return View(model);
    }

    /// <summary>
    /// «بارگیریِ قابل تبدیل» = همان فیلترِ صفحهٔ لیست، به‌علاوهٔ باقیماندهٔ مثبت. فرمولِ باقیمانده
    /// همان فرمولِ فرم تک‌بارگیری است و در یک کوئریِ مجموعه‌ای اجرا می‌شود.
    /// </summary>
    private static LoadingListFilterCriteria ToCriteria(TransportBulkLoadingFilter filter)
        => new()
        {
            Query = filter.Q,
            ContractIds = filter.ContractId,
            ProductIds = filter.ProductId,
            WithoutReceipt = filter.WithoutReceipt,
            TransportTypes = filter.TransportType,
            ReceiptStatus = filter.ReceiptStatus,
            PriceStatus = filter.PriceStatus,
            FromDate = filter.FromDate,
            ToDate = filter.ToDate
        };

    private IQueryable<ConvertibleLoadingProjection> ConvertibleLoadingsQuery(TransportBulkLoadingFilter filter)
    {
        var filtered = LoadingListFilter.Apply(_db.LoadingRegisters.AsNoTracking(), ToCriteria(filter));

        return filtered
            .Select(l => new ConvertibleLoadingProjection
            {
                Id = l.Id,
                AvailableQuantityMt = l.LoadedQuantityMt
                    - (l.Receipts.Where(r => !r.IsCancelled).Sum(r => (decimal?)r.ReceivedQuantityMt) ?? 0m)
                    - (_db.LossEvents
                        .Where(e => (e.LoadingRegisterId == l.Id
                                || e.LoadingReceiptId.HasValue
                                    && e.LoadingReceipt != null
                                    && e.LoadingReceipt.LoadingRegisterId == l.Id)
                            && e.Stage == LossEventStage.ReceiptShortage
                            && !e.IsCancelled)
                        .Sum(e => (decimal?)(e.DifferenceQuantityMt > 0m
                            ? e.DifferenceQuantityMt
                            : e.ChargeableLossMt > 0m ? e.ChargeableLossMt : 0m)) ?? 0m)
                    - (_db.InventoryTransportLegAllocations
                        .Where(a => a.SourceLoadingRegisterId == l.Id
                            && a.InventoryTransportLeg != null
                            && a.InventoryTransportLeg.Status != InventoryTransportLegStatus.Cancelled)
                        .Sum(a => (decimal?)a.QuantityMt) ?? 0m)
            })
            .Where(x => x.AvailableQuantityMt > Epsilon);
    }

    private async Task<(int Count, decimal QuantityMt)> SummarizeConvertibleLoadingsAsync(
        TransportBulkLoadingFilter? filter)
    {
        // فیلترِ خالی یعنی «کل جدول». نه پیشنهادش درست است (یک کلیک، تبدیلِ هرچه بارگیری در
        // سیستم هست) و نه شمردنش ارزان. حالت فیلتر فقط با دست‌کم یک شرط فعال می‌شود.
        if (filter is null || ToCriteria(filter).IsEmpty)
        {
            return (0, 0m);
        }

        var summary = await ConvertibleLoadingsQuery(filter)
            .GroupBy(_ => 1)
            .Select(g => new { Count = g.Count(), QuantityMt = g.Sum(x => x.AvailableQuantityMt) })
            .FirstOrDefaultAsync();
        return (summary?.Count ?? 0, summary?.QuantityMt ?? 0m);
    }

    private Task<List<int>> ConvertibleLoadingIdsAsync(TransportBulkLoadingFilter filter, int take)
        => ConvertibleLoadingsQuery(filter)
            .OrderBy(x => x.Id)
            .Select(x => x.Id)
            .Take(take)
            .ToListAsync();

    private sealed class ConvertibleLoadingProjection
    {
        public int Id { get; set; }
        public decimal AvailableQuantityMt { get; set; }
    }

    /// <summary>
    /// ردیف‌های حالت فیلتر: هر بارگیری با وسیله و شرکت خدماتیِ خودش و کل باقیمانده‌اش — همان
    /// مقادیر پیش‌فرضی که فرم ردیفی هم نشان می‌دهد. مقدار نهایی را سرویس زیر قفل دوباره کنترل می‌کند.
    /// </summary>
    private async Task<List<BulkStartTransportFromLoadingRow>> BuildFilterSelectionRowsAsync(
        IReadOnlyCollection<int> ids)
    {
        if (ids.Count == 0)
        {
            return [];
        }

        var defaults = await LoadBulkRowDefaultsAsync(ids);
        return defaults
            .Select(d => new BulkStartTransportFromLoadingRow
            {
                LoadingRegisterId = d.Id,
                QuantityMt = d.AvailableQuantityMt,
                TransportType = d.TransportType,
                TruckId = d.TransportType == LoadingTransportType.Truck ? d.TruckId : null,
                WagonId = null,
                VesselId = d.TransportType == LoadingTransportType.Vessel ? d.VesselId : null,
                DriverId = null,
                ServiceProviderId = d.LogisticsServiceProviderId,
                Reference = d.Reference,
                Label = d.Label
            })
            .ToList();
    }

    /// <summary>باقیماندهٔ قابل تبدیل هر بارگیری با همان فرمول فرم تک‌بارگیری محاسبه می‌شود.</summary>
    private async Task<List<TransportBulkFromLoadingRow>> BuildBulkLoadingRowsAsync(IReadOnlyCollection<int> ids)
    {
        var defaults = await LoadBulkRowDefaultsAsync(ids);
        return defaults
            .Select(d => new TransportBulkFromLoadingRow
            {
                LoadingRegisterId = d.Id,
                Selected = true,
                LoadingLabel = d.Label,
                ProductName = d.ProductName,
                AvailableQuantityMt = d.AvailableQuantityMt,
                QuantityMt = d.AvailableQuantityMt,
                TransportType = d.TransportType,
                TruckId = d.TransportType == LoadingTransportType.Truck ? d.TruckId : null,
                VesselId = d.TransportType == LoadingTransportType.Vessel ? d.VesselId : null,
                ServiceProviderId = d.LogisticsServiceProviderId,
                Reference = d.Reference
            })
            .ToList();
    }

    /// <summary>برچسب و باقیماندهٔ ردیف‌های ارسال‌شده را دوباره از دیتابیس می‌خواند (نمایش پس از خطا).</summary>
    private async Task RefreshBulkLoadingRowsAsync(TransportBulkFromLoadingViewModel model)
    {
        var rows = model.Rows ?? [];
        if (rows.Count == 0)
        {
            return;
        }

        var fresh = (await LoadBulkRowDefaultsAsync(rows.Select(r => r.LoadingRegisterId).ToList()))
            .ToDictionary(d => d.Id);
        foreach (var row in rows)
        {
            if (!fresh.TryGetValue(row.LoadingRegisterId, out var current))
            {
                row.Selected = false;
                continue;
            }
            row.LoadingLabel = current.Label;
            row.ProductName = current.ProductName;
            row.AvailableQuantityMt = current.AvailableQuantityMt;
        }
    }

    /// <summary>
    /// پیش‌فرضِ ردیف‌های تبدیل گروهی در یک رفت‌وبرگشت برای کل مجموعه.
    ///
    /// پیش از این، همین کار برای هر بارگیری ۴ کوئری جدا می‌زد (۴N کوئری برای N ردیف) و
    /// صفحه با چند صد انتخاب عملاً باز نمی‌شد. فرمولِ باقیمانده ذره‌ای عوض نشده است.
    /// </summary>
    private async Task<List<BulkRowDefaults>> LoadBulkRowDefaultsAsync(IReadOnlyCollection<int> ids)
    {
        var wanted = ids.Distinct().Where(id => id > 0).ToList();
        if (wanted.Count == 0)
        {
            return [];
        }

        var rows = await _db.LoadingRegisters
            .AsNoTracking()
            .Where(l => wanted.Contains(l.Id))
            .Select(l => new
            {
                l.Id,
                l.LoadedQuantityMt,
                l.TransportType,
                l.TruckId,
                l.VesselId,
                l.LogisticsServiceProviderId,
                l.BillOfLadingNumber,
                l.RwbNo,
                ProductName = l.Product != null ? l.Product.Name : "",
                ContractLabel = l.Contract != null ? l.Contract.ContractNumber : "",
                ReceivedMt = l.Receipts.Where(r => !r.IsCancelled).Sum(r => (decimal?)r.ReceivedQuantityMt) ?? 0m,
                ShortageMt = _db.LossEvents
                    .Where(e => (e.LoadingRegisterId == l.Id
                            || e.LoadingReceiptId.HasValue
                                && e.LoadingReceipt != null
                                && e.LoadingReceipt.LoadingRegisterId == l.Id)
                        && e.Stage == LossEventStage.ReceiptShortage
                        && !e.IsCancelled)
                    .Sum(e => (decimal?)(e.DifferenceQuantityMt > 0m
                        ? e.DifferenceQuantityMt
                        : e.ChargeableLossMt > 0m ? e.ChargeableLossMt : 0m)) ?? 0m,
                TransportedMt = _db.InventoryTransportLegAllocations
                    .Where(a => a.SourceLoadingRegisterId == l.Id
                        && a.InventoryTransportLeg != null
                        && a.InventoryTransportLeg.Status != InventoryTransportLegStatus.Cancelled)
                    .Sum(a => (decimal?)a.QuantityMt) ?? 0m
            })
            .ToListAsync();

        var ordered = wanted
            .Select(id => rows.FirstOrDefault(r => r.Id == id))
            .Where(r => r is not null)
            .Select(r => r!)
            .ToList();

        var result = new List<BulkRowDefaults>(ordered.Count);
        foreach (var row in ordered)
        {
            var availableMt = decimal.Round(
                Math.Max(row.LoadedQuantityMt - row.ReceivedMt - row.ShortageMt - row.TransportedMt, 0m), 4);
            if (availableMt <= Epsilon)
            {
                continue;
            }

            result.Add(new BulkRowDefaults
            {
                Id = row.Id,
                Label = $"{(string.IsNullOrWhiteSpace(row.ContractLabel) ? $"بارگیری #{row.Id}" : row.ContractLabel)} — #{row.Id}",
                ProductName = row.ProductName,
                AvailableQuantityMt = availableMt,
                TransportType = row.TransportType,
                TruckId = row.TruckId,
                VesselId = row.VesselId,
                LogisticsServiceProviderId = row.LogisticsServiceProviderId,
                Reference = row.RwbNo ?? row.BillOfLadingNumber
            });
        }
        return result;
    }

    private sealed class BulkRowDefaults
    {
        public int Id { get; init; }
        public string Label { get; init; } = "";
        public string ProductName { get; init; } = "";
        public decimal AvailableQuantityMt { get; init; }
        public LoadingTransportType TransportType { get; init; }
        public int? TruckId { get; init; }
        public int? VesselId { get; init; }
        public int? LogisticsServiceProviderId { get; init; }
        public string? Reference { get; init; }
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> FromReceipt(
        TransportStartFromReceiptViewModel model,
        [FromForm(Name = FormTokenHtmlHelper.FieldName)] string? formToken = null)
    {
        if (!await PopulateReceiptModelAsync(model))
        {
            return NotFound();
        }
        if (model.QuantityMt > model.AvailableQuantityMt + Epsilon)
        {
            ModelState.AddModelError(nameof(model.QuantityMt), "مقدار حمل از باقیماندهٔ مستقیم رسید بیشتر است.");
        }

        if (!ModelState.IsValid)
        {
            await PopulateVehicleLookupsAsync();
            return View(model);
        }

        // توکن پیش از فراخوانی سرویس فقط به ChangeTracker اضافه می‌شود و با نخستین
        // SaveChanges داخل همان تراکنش ذخیره می‌گردد؛ اگر سرویس خطا بدهد ذخیره نمی‌شود.
        _formTokens?.Stamp(formToken, "Transport.FromReceipt", nameof(InventoryTransportLeg));

        try
        {
            var leg = await _workflow.StartFromReceiptAsync(new StartTransportFromReceiptCommand
            {
                LoadingReceiptId = model.LoadingReceiptId,
                QuantityMt = model.QuantityMt,
                TransportType = model.TransportType,
                TruckId = model.TruckId,
                WagonId = model.WagonId,
                VesselId = model.VesselId,
                DriverId = model.DriverId,
                ServiceProviderId = model.ServiceProviderId,
                TransportDate = model.TransportDate,
                Reference = model.Reference,
                Notes = model.Notes
            });
            TempData["ok"] = "حمل مستقیم از رسید ثبت شد؛ هیچ خروج مصنوعی موجودی ساخته نشد.";
            return RedirectToAction(nameof(Details), new { id = leg.Id });
        }
        catch (DbUpdateException duplicate) when (_formTokens?.IsDuplicate(duplicate) == true)
        {
            TempData["err"] = "این حمل قبلاً ثبت شده است و دوباره ثبت نشد.";
            return RedirectToAction("Index", "InventoryTransportLegs");
        }
        catch (BusinessRuleException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            await PopulateReceiptModelAsync(model);
            await PopulateVehicleLookupsAsync();
            return View(model);
        }
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpGet]
    public async Task<IActionResult> Continue(int? transportLegId = null)
    {
        var model = new TransportContinueViewModel { TransferDate = _clock.Today };
        await PopulateContinueSourcesAsync(model, transportLegId);
        await PopulateVehicleLookupsAsync();
        return View(model);
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Continue(
        TransportContinueViewModel model,
        [FromForm(Name = FormTokenHtmlHelper.FieldName)] string? formToken = null)
    {
        var selectedSources = (model.Sources ?? [])
            .Where(s => s.Selected && s.LegId > 0 && s.QuantityMt > 0m)
            .ToList();
        if (selectedSources.Count == 0)
        {
            ModelState.AddModelError(nameof(model.Sources), "حداقل یک حمل مبدأ و مقدار آن را انتخاب کنید.");
        }

        // هر ردیف می‌تواند وسیلهٔ مقصد خودش را داشته باشد (امپورت اکسل همین را پر می‌کند): یا از
        // لیست وسیله‌ها، یا نمبرِ موترِ جدید، وگرنه وسیلهٔ مقصدِ پیش‌فرضِ فرم.
        foreach (var source in selectedSources)
        {
            if (!string.IsNullOrWhiteSpace(source.TargetVehicleNumber) && string.IsNullOrWhiteSpace(source.TargetKey))
            {
                continue; // موترِ جدید؛ داخل تراکنش ساخته می‌شود.
            }
            if (!TryResolveTargetVehicle(source.TargetKey, model, out _, out _))
            {
                ModelState.AddModelError(
                    nameof(model.Sources),
                    $"وسیلهٔ مقصد برای «{DescribeSource(source)}» مشخص نیست.");
            }
        }

        if (!ModelState.IsValid)
        {
            await PopulateContinueSourcesAsync(model);
            await PopulateVehicleLookupsAsync();
            return View(model);
        }

        await using var transaction = _db.Database.IsRelational()
            ? await _db.Database.BeginTransactionAsync()
            : null;
        _formTokens?.Stamp(formToken, "Transport.Continue", nameof(InventoryTransportLeg));

        try
        {
            var newPlateTrucks = await ResolveOrCreateTargetTrucksAsync(selectedSources);
            var targetGroups = BuildTargetGroups(selectedSources, model, newPlateTrucks);
            var createdLegIds = new List<int>();
            foreach (var group in targetGroups)
            {
                var result = await _workflow.ContinueToVehicleAsync(new ContinueToVehicleCommand
                {
                    Sources = group.Sources,
                    TargetTransportType = group.TransportType,
                    TargetTruckId = group.TransportType == LoadingTransportType.Truck ? group.VehicleId : null,
                    TargetWagonId = group.TransportType == LoadingTransportType.Wagon ? group.VehicleId : null,
                    TargetVesselId = group.TransportType == LoadingTransportType.Vessel ? group.VehicleId : null,
                    DriverId = model.DriverId,
                    TransferDate = model.TransferDate,
                    TicketSerialNumber = model.TicketSerialNumber,
                    Notes = model.Notes
                });
                createdLegIds.Add(result.ChildLeg.Id);
            }
            if (transaction is not null)
            {
                await transaction.CommitAsync();
            }
            TempData["ok"] = createdLegIds.Count > 1
                ? $"{createdLegIds.Count} حمل مقصد از {selectedSources.Count} حمل در جریان ثبت شد؛ هیچ حرکت موجودی ساخته نشد."
                : selectedSources.Count > 1
                    ? "ادغام حمل‌ها در وسیلهٔ مقصد ثبت شد؛ هیچ حرکت موجودی ساخته نشد."
                    : "انتقال به وسیلهٔ بعدی ثبت شد؛ هیچ حرکت موجودی ساخته نشد.";
            return createdLegIds.Count == 1
                ? RedirectToAction(nameof(Details), new { id = createdLegIds[0] })
                : RedirectToAction("Index", "InventoryTransportLegs");
        }
        catch (DbUpdateException duplicate) when (_formTokens?.IsDuplicate(duplicate) == true)
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync();
            }
            TempData["err"] = "این انتقال قبلاً ثبت شده است و دوباره ثبت نشد.";
            return RedirectToAction("Index", "InventoryTransportLegs");
        }
        catch (BusinessRuleException ex)
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync();
            }
            ModelState.AddModelError(string.Empty, ex.Message);
            await PopulateContinueSourcesAsync(model);
            await PopulateVehicleLookupsAsync();
            return View(model);
        }
    }

    // ── لغو گروهی حمل‌های ساخته‌شده از «حمل در جریان» ─────────────────────────────
    // همان مسیر لغوِ صفحهٔ دیسپچ است، فقط برای چند حمل در یک تراکنش: نگهبان زنجیره،
    // برگشت رسیدهای مبدأ، لغو دیسپچ سازگاری و برگشت مصرف کرایه.
    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpGet]
    public async Task<IActionResult> CancelContinued()
        => View(new TransportContinueCancelViewModel { Rows = await LoadContinuedLegRowsAsync() });

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CancelContinued(
        int[]? legIds,
        [FromForm(Name = FormTokenHtmlHelper.FieldName)] string? formToken = null)
    {
        var requestedIds = (legIds ?? []).Where(id => id > 0).Distinct().ToList();
        if (requestedIds.Count == 0)
        {
            TempData["err"] = "هیچ حملی برای لغو انتخاب نشده است.";
            return RedirectToAction(nameof(CancelContinued));
        }

        var rows = await LoadContinuedLegRowsAsync();
        var selectable = rows.Where(r => requestedIds.Contains(r.LegId)).ToList();
        var blocked = selectable.Where(r => r.BlockReason is not null).ToList();
        if (blocked.Count > 0)
        {
            TempData["err"] = "لغو انجام نشد: "
                + string.Join("، ", blocked.Select(r => $"حمل #{r.LegId} ({r.BlockReason})"));
            return RedirectToAction(nameof(CancelContinued));
        }

        var legIdsToCancel = selectable.Select(r => r.LegId).ToList();
        if (legIdsToCancel.Count == 0)
        {
            TempData["err"] = "حمل‌های انتخاب‌شده دیگر قابل لغو نیستند.";
            return RedirectToAction(nameof(CancelContinued));
        }

        await using var transaction = _db.Database.IsRelational()
            ? await _db.Database.BeginTransactionAsync()
            : null;
        _formTokens?.Stamp(formToken, "Transport.CancelContinued", nameof(InventoryTransportLeg));

        try
        {
            // رسیدهای والد که این حمل‌ها را ساخته‌اند؛ لغو زنجیره از روی همین‌ها انجام می‌شود.
            var sourceReceiptIds = await _db.InventoryTransportLegAllocations
                .AsNoTracking()
                .Where(a => legIdsToCancel.Contains(a.InventoryTransportLegId) && a.SourceTransportReceiptId != null)
                .Select(a => a.SourceTransportReceiptId!.Value)
                .Distinct()
                .ToListAsync();

            var receipts = await _db.InventoryTransportReceipts
                .Include(r => r.InventoryTransportLeg)
                .Where(r => sourceReceiptIds.Contains(r.Id))
                .ToListAsync();
            var activeReceipts = receipts.Where(r => !r.IsCancelled).ToList();

            var dispatches = await _db.TruckDispatches
                .Where(d => d.InventoryTransportReceiptId != null
                    && sourceReceiptIds.Contains(d.InventoryTransportReceiptId!.Value)
                    && d.Status != DispatchStatus.Cancelled)
                .ToListAsync();
            if (dispatches.Any(d => d.SalesTransactionId.HasValue))
            {
                throw new BusinessRuleException(
                    "TRANSPORT_CONTINUE_CANCEL_HAS_SALE",
                    "برای موتر یکی از این حمل‌ها فروش ثبت شده است؛ ابتدا فروش لینک‌شده را لغو کنید.");
            }

            // اول نگهبان زنجیره: اگر فرزندی بارش را جای دیگری برده باشد، اینجا استثنا می‌آید.
            await _workflow.CancelOrReverseAsync(activeReceipts.Select(r => r.Id).ToList());

            foreach (var receipt in activeReceipts)
            {
                receipt.IsCancelled = true;
                if (receipt.InventoryTransportLeg?.Status == InventoryTransportLegStatus.Received)
                {
                    receipt.InventoryTransportLeg.Status = InventoryTransportLegStatus.InTransit;
                }
            }

            foreach (var dispatch in dispatches)
            {
                dispatch.Status = DispatchStatus.Cancelled;
                await DispatchFreightExpenseSync.CancelByDispatchIdAsync(_db, dispatch.Id, _expenseAccounting);
            }

            await _db.SaveChangesAsync();

            // سطر مصرفِ دارایی اسناد لغوشده برگشتی می‌شود (همان قاعدهٔ لغو در صفحهٔ دیسپچ).
            var usageWriter = new AssetUsageChargeService(_db);
            foreach (var dispatch in dispatches)
            {
                await usageWriter.SyncOperationAsync(dispatch);
            }
            foreach (var receipt in activeReceipts.Where(r => r.InventoryTransportLeg is not null))
            {
                await usageWriter.SyncOperationAsync(receipt, receipt.InventoryTransportLeg!);
            }
            await _db.SaveChangesAsync();

            if (transaction is not null)
            {
                await transaction.CommitAsync();
            }
            TempData["ok"] = $"{legIdsToCancel.Count} حمل لغو شد و باقیماندهٔ حمل‌های مبدأ آزاد شد.";
        }
        catch (DbUpdateException duplicate) when (_formTokens?.IsDuplicate(duplicate) == true)
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync();
            }
            TempData["err"] = "این لغو قبلاً ثبت شده است و دوباره انجام نشد.";
        }
        catch (BusinessRuleException ex)
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync();
            }
            TempData["err"] = ex.Message;
        }

        return RedirectToAction(nameof(CancelContinued));
    }

    // حمل‌هایی که سهمشان از یک رسیدِ حملِ والد آمده = ساخته‌شده با «انتقال به وسیله دیگر».
    private async Task<List<TransportContinueCancelRow>> LoadContinuedLegRowsAsync()
    {
        var childLegIds = await _db.InventoryTransportLegAllocations
            .AsNoTracking()
            .Where(a => a.SourceTransportReceiptId != null)
            .Select(a => a.InventoryTransportLegId)
            .Distinct()
            .ToListAsync();
        if (childLegIds.Count == 0)
        {
            return [];
        }

        var legs = await _db.InventoryTransportLegs
            .AsNoTracking()
            .Where(l => childLegIds.Contains(l.Id) && l.Status != InventoryTransportLegStatus.Cancelled)
            .OrderByDescending(l => l.Id)
            .Take(500)
            .Select(l => new
            {
                l.Id,
                l.TransportType,
                ProductName = l.Product != null ? l.Product.Name : "",
                Vehicle = l.Truck != null ? l.Truck.PlateNumber
                    : l.Wagon != null ? l.Wagon.WagonNumber
                    : l.Vessel != null ? l.Vessel.Name
                    : l.WagonNumber,
                l.QuantityMt,
                l.LoadedDate
            })
            .ToListAsync();
        if (legs.Count == 0)
        {
            return [];
        }

        var legIds = legs.Select(l => l.Id).ToList();

        // والدهای هر حمل، برای نمایش «از کدام حمل آمده».
        var parents = await _db.InventoryTransportLegAllocations
            .AsNoTracking()
            .Where(a => legIds.Contains(a.InventoryTransportLegId) && a.SourceTransportLegId != null)
            .Select(a => new { a.InventoryTransportLegId, ParentLegId = a.SourceTransportLegId!.Value })
            .ToListAsync();

        // اگر خودِ این حمل بارش را جای دیگری برده باشد، لغو مسدود است (همان نگهبان زنجیره).
        var consumedLegIds = await _db.InventoryTransportReceipts
            .AsNoTracking()
            .Where(r => legIds.Contains(r.InventoryTransportLegId) && !r.IsCancelled)
            .Select(r => r.InventoryTransportLegId)
            .Distinct()
            .ToListAsync();

        // فروشِ لینک‌شده به دیسپچِ سازگاریِ همین حمل هم لغو را مسدود می‌کند.
        var soldLegIds = await _db.InventoryTransportLegAllocations
            .AsNoTracking()
            .Where(a => legIds.Contains(a.InventoryTransportLegId) && a.SourceTransportReceiptId != null)
            .Where(a => _db.TruckDispatches.Any(d => d.InventoryTransportReceiptId == a.SourceTransportReceiptId
                && d.Status != DispatchStatus.Cancelled
                && d.SalesTransactionId != null))
            .Select(a => a.InventoryTransportLegId)
            .Distinct()
            .ToListAsync();

        return legs.Select(l =>
        {
            var parentIds = parents.Where(p => p.InventoryTransportLegId == l.Id)
                .Select(p => $"#{p.ParentLegId}")
                .Distinct()
                .ToList();
            return new TransportContinueCancelRow
            {
                LegId = l.Id,
                Label = $"حمل #{l.Id} — {TransportTypeText(l.TransportType)} {(string.IsNullOrWhiteSpace(l.Vehicle) ? "" : l.Vehicle)}",
                ProductName = l.ProductName,
                SourceLabel = parentIds.Count > 0 ? string.Join("، ", parentIds) : "—",
                QuantityMt = l.QuantityMt,
                TransferDate = l.LoadedDate,
                BlockReason = consumedLegIds.Contains(l.Id)
                    ? "برای مرحلهٔ بعدی این حمل عملیات ثبت شده است"
                    : soldLegIds.Contains(l.Id)
                        ? "فروش لینک‌شده دارد"
                        : null
            };
        }).ToList();
    }

    // ── دیدن و لغو گروهی تبدیل‌های «بارگیری به حمل» ─────────────────────────────
    // هر تبدیل یک سند حمل (Batch) با یک Leg است. لغو همان CancelAsync «لغو سند حمل» در صفحهٔ
    // جزئیات حمل است و برای هر سند جدا و در تراکنش خودش اجرا می‌شود؛ اینجا قاعدهٔ تازه‌ای نیست.
    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpGet]
    public async Task<IActionResult> CancelFromLoading(
        DateTime? fromDate = null,
        DateTime? toDate = null,
        string? q = null,
        int? loadingId = null)
    {
        var model = new TransportLoadingCancelViewModel
        {
            FromDate = fromDate?.Date,
            ToDate = toDate?.Date,
            Q = string.IsNullOrWhiteSpace(q) ? null : q.Trim(),
            LoadingId = loadingId is > 0 ? loadingId : null
        };
        await PopulateLoadingConversionRowsAsync(model);
        return View(model);
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CancelFromLoading(
        int[]? batchIds,
        DateTime? fromDate = null,
        DateTime? toDate = null,
        string? q = null,
        int? loadingId = null)
    {
        var back = new
        {
            fromDate = fromDate?.ToString("yyyy-MM-dd"),
            toDate = toDate?.ToString("yyyy-MM-dd"),
            q,
            loadingId
        };

        var requestedIds = (batchIds ?? []).Where(id => id > 0).Distinct().ToList();
        if (requestedIds.Count == 0)
        {
            TempData["err"] = "هیچ حملی برای لغو انتخاب نشده است.";
            return RedirectToAction(nameof(CancelFromLoading), back);
        }
        if (requestedIds.Count > TransportLoadingCancelViewModel.MaxRows)
        {
            TempData["err"] = $"در یک عملیات حداکثر {TransportLoadingCancelViewModel.MaxRows:N0} حمل لغو می‌شود.";
            return RedirectToAction(nameof(CancelFromLoading), back);
        }

        // فقط سندهای «تبدیل بارگیری» از این صفحه لغو می‌شوند؛ شناسهٔ سندِ دیگر نادیده می‌ماند.
        var labels = (await _db.InventoryTransportLegs
                .AsNoTracking()
                .Where(l => l.InventoryTransportBatchId != null
                    && requestedIds.Contains(l.InventoryTransportBatchId.Value)
                    && l.Allocations.Any(a => a.SourceLoadingRegisterId != null))
                .Select(l => new { BatchId = l.InventoryTransportBatchId!.Value, l.Id })
                .ToListAsync())
            .GroupBy(l => l.BatchId)
            .OrderBy(g => g.Key)
            .Select(g => (BatchId: g.Key, Label: $"TR-{g.Min(l => l.Id):0000}"))
            .ToList();
        if (labels.Count == 0)
        {
            TempData["err"] = "حمل‌های انتخاب‌شده از تبدیل بارگیری نیستند یا دیگر وجود ندارند.";
            return RedirectToAction(nameof(CancelFromLoading), back);
        }

        var cancelled = 0;
        var failures = new List<string>();
        foreach (var (batchId, label) in labels)
        {
            try
            {
                await _batchTransport.CancelAsync(batchId);
                cancelled++;
            }
            catch (BusinessRuleException ex)
            {
                failures.Add($"{label}: {ex.Message}");
            }
            catch (DbUpdateException)
            {
                failures.Add($"{label}: هم‌زمان تغییر کرده است؛ دوباره تلاش کنید.");
            }
            finally
            {
                // هر لغو تراکنش خودش را دارد؛ تغییرِ ردیابی‌شدهٔ یک لغوِ ردشده نباید با
                // SaveChanges لغو بعدی ذخیره شود.
                _db.ChangeTracker.Clear();
            }
        }

        if (cancelled > 0)
        {
            TempData["ok"] = $"{cancelled:N0} حمل لغو شد و مقدارش به باقیماندهٔ بارگیری برگشت.";
        }
        if (failures.Count > 0)
        {
            const int sampleSize = 10;
            var message = string.Join(" | ", failures.Take(sampleSize));
            if (failures.Count > sampleSize)
            {
                message += $" | و {failures.Count - sampleSize:N0} مورد دیگر";
            }
            TempData["err"] = "لغو نشد — " + message;
        }

        return RedirectToAction(nameof(CancelFromLoading), back);
    }

    // حمل‌هایی که سهمشان از خودِ بارگیری آمده = ساخته‌شده با «تبدیل بارگیری به حمل».
    // علت مسدودبودن فقط برای نمایش است و همان فهرست موانعِ InventoryTransportBatchService
    // را دنبال می‌کند؛ تصمیم نهایی را خودِ CancelAsync هنگام لغو می‌گیرد.
    private async Task PopulateLoadingConversionRowsAsync(TransportLoadingCancelViewModel model)
    {
        var query = _db.InventoryTransportLegs
            .AsNoTracking()
            .Where(l => l.Status != InventoryTransportLegStatus.Cancelled
                && l.Allocations.Any(a => a.SourceLoadingRegisterId != null));

        if (model.FromDate is { } fromDate)
        {
            query = query.Where(l => l.LoadedDate >= fromDate);
        }
        if (model.ToDate is { } toDate)
        {
            var toExclusive = toDate.AddDays(1);
            query = query.Where(l => l.LoadedDate < toExclusive);
        }
        if (model.LoadingId is { } loadingId)
        {
            query = query.Where(l => l.Allocations.Any(a => a.SourceLoadingRegisterId == loadingId));
        }
        if (!string.IsNullOrWhiteSpace(model.Q))
        {
            var term = model.Q.ToLower();
            query = query.Where(l =>
                (l.RwbNo != null && l.RwbNo.ToLower().Contains(term))
                || (l.Truck != null && l.Truck.PlateNumber.ToLower().Contains(term))
                || (l.Wagon != null && l.Wagon.WagonNumber.ToLower().Contains(term))
                || (l.Vessel != null && l.Vessel.Name.ToLower().Contains(term))
                || (l.WagonNumber != null && l.WagonNumber.ToLower().Contains(term))
                || (l.SourcePurchaseContract != null && l.SourcePurchaseContract.ContractNumber.ToLower().Contains(term)));
        }

        model.TotalCount = await query.CountAsync();
        if (model.TotalCount == 0)
        {
            return;
        }

        var legs = await query
            .OrderByDescending(l => l.Id)
            .Take(TransportLoadingCancelViewModel.MaxRows)
            .Select(l => new
            {
                l.Id,
                l.InventoryTransportBatchId,
                l.TransportType,
                ProductName = l.Product != null ? l.Product.Name : "",
                Vehicle = l.Truck != null ? l.Truck.PlateNumber
                    : l.Wagon != null ? l.Wagon.WagonNumber
                    : l.Vessel != null ? l.Vessel.Name
                    : l.WagonNumber,
                l.QuantityMt,
                l.LoadedDate,
                LoadingId = l.Allocations
                    .Where(a => a.SourceLoadingRegisterId != null)
                    .Select(a => a.SourceLoadingRegisterId)
                    .FirstOrDefault(),
                ContractNumber = l.SourcePurchaseContract != null ? l.SourcePurchaseContract.ContractNumber : null
            })
            .ToListAsync();

        var legIds = legs.Select(l => l.Id).ToList();

        var receivedLegIds = (await _db.InventoryTransportReceipts
            .AsNoTracking()
            .Where(r => legIds.Contains(r.InventoryTransportLegId) && !r.IsCancelled)
            .Select(r => r.InventoryTransportLegId)
            .Distinct()
            .ToListAsync()).ToHashSet();

        var saleLinks = await _db.SalesTransactionSourceAllocations
            .AsNoTracking()
            .Where(a => (a.TransportLegId.HasValue && legIds.Contains(a.TransportLegId.Value))
                || (a.SourceTransportLegId.HasValue && legIds.Contains(a.SourceTransportLegId.Value)))
            .Select(a => new { a.TransportLegId, a.SourceTransportLegId })
            .ToListAsync();
        var soldLegIds = saleLinks
            .SelectMany(a => new[] { a.TransportLegId, a.SourceTransportLegId })
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .ToHashSet();

        var continuedLegIds = (await _db.InventoryTransportLegAllocations
            .AsNoTracking()
            .Where(a => a.SourceTransportLegId.HasValue
                && legIds.Contains(a.SourceTransportLegId.Value)
                && a.InventoryTransportLeg != null
                && a.InventoryTransportLeg.Status != InventoryTransportLegStatus.Cancelled)
            .Select(a => a.SourceTransportLegId!.Value)
            .Distinct()
            .ToListAsync()).ToHashSet();

        var lossLegIds = (await _db.LossEvents
            .AsNoTracking()
            .Where(e => e.TransportLegId.HasValue && legIds.Contains(e.TransportLegId.Value) && !e.IsCancelled)
            .Select(e => e.TransportLegId!.Value)
            .Distinct()
            .ToListAsync()).ToHashSet();

        var customsLegIds = (await _db.CustomsDeclarations
            .AsNoTracking()
            .Where(c => c.TransportLegId.HasValue && legIds.Contains(c.TransportLegId.Value))
            .Select(c => c.TransportLegId!.Value)
            .Distinct()
            .ToListAsync()).ToHashSet();

        model.Rows = legs.Select(l =>
        {
            var blockers = new List<string>();
            if (l.InventoryTransportBatchId is null)
            {
                blockers.Add("سند حمل ندارد و از این صفحه لغو نمی‌شود");
            }
            if (receivedLegIds.Contains(l.Id))
            {
                blockers.Add("رسید تحویل ثبت شده است");
            }
            if (soldLegIds.Contains(l.Id))
            {
                blockers.Add("فروش ثبت‌شده دارد");
            }
            if (continuedLegIds.Contains(l.Id))
            {
                blockers.Add("مرحلهٔ بعدی حمل ثبت شده است");
            }
            if (lossLegIds.Contains(l.Id))
            {
                blockers.Add("کسری/ضایعات ثبت شده است");
            }
            if (customsLegIds.Contains(l.Id))
            {
                blockers.Add("اظهار گمرکی دارد");
            }

            var loadingLabel = l.LoadingId is { } id ? $"LD-{id:0000}" : "—";
            if (!string.IsNullOrWhiteSpace(l.ContractNumber))
            {
                loadingLabel += $" · {l.ContractNumber}";
            }

            return new TransportLoadingCancelRow
            {
                LegId = l.Id,
                BatchId = l.InventoryTransportBatchId,
                LoadingId = l.LoadingId,
                Label = $"TR-{l.Id:0000} — {TransportTypeText(l.TransportType)} {(string.IsNullOrWhiteSpace(l.Vehicle) ? "" : l.Vehicle)}".TrimEnd(),
                LoadingLabel = loadingLabel,
                ProductName = l.ProductName,
                QuantityMt = l.QuantityMt,
                TransportDate = l.LoadedDate,
                BlockReason = blockers.Count > 0 ? string.Join("، ", blockers) : null
            };
        }).ToList();
    }

    // فایل «نمبر وسیله قبلی | نمبر وسیله جدید | وزن» را می‌خواند و ردیف‌ها را به
    // حمل‌های در جریان و وسیله‌های مقصد تطبیق می‌دهد. هیچ ثبتی انجام نمی‌شود؛ فقط
    // گرید فرم پر می‌شود و کاربر خودش ثبت می‌کند.
    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ImportContinueExcel(IFormFile? file, CancellationToken ct = default)
    {
        if (file is null || file.Length == 0)
        {
            return Json(new { ok = false, message = "فایلی انتخاب نشده است." });
        }
        if (file.Length > 5 * 1024 * 1024)
        {
            return Json(new { ok = false, message = "حجم فایل زیاد است (حداکثر ۵ مگابایت)." });
        }

        IReadOnlyList<TransportContinueImportRow> parsed;
        try
        {
            await using var workbook = await ExcelWorkbookNormalizer.OpenAsync(file, ct);
            parsed = TransportContinueWorkbookParser.Parse(workbook.Stream);
        }
        catch (Exception ex)
        {
            return Json(new { ok = false, message = "خواندن فایل اکسل ناموفق بود: " + ex.Message });
        }

        if (parsed.Count == 0)
        {
            return Json(new { ok = false, message = "در فایل هیچ ردیف معتبری (نمبر وسیله قبلی و وزن) یافت نشد." });
        }

        var legs = await LoadActiveTransportRowsAsync();
        var targets = await LoadTargetVehicleRowsAsync();

        var warnings = new List<string>();
        var usedLegIds = new HashSet<int>();
        var matched = new List<object>();

        foreach (var row in parsed)
        {
            var sourceKey = NormalizeVehicleNumber(row.SourceVehicleNumber);
            // یک وسیله می‌تواند چند حملِ در جریان داشته باشد؛ هر ردیف اکسل یکی را مصرف می‌کند.
            var leg = legs.FirstOrDefault(l => NormalizeVehicleNumber(l.VehicleNumber) == sourceKey && !usedLegIds.Contains(l.LegId));
            if (leg is null)
            {
                warnings.Add($"«{row.SourceVehicleNumber}»: حمل در جریانِ آزاد با این نمبر پیدا نشد.");
                continue;
            }
            usedLegIds.Add(leg.LegId);

            // بیشتر از باقیماندهٔ حمل قابل انتقال نیست؛ مقدار تا سقف باقیمانده کم می‌شود.
            var quantity = row.QuantityMt;
            if (quantity > leg.RemainingQuantityMt + Epsilon)
            {
                warnings.Add(
                    $"«{row.SourceVehicleNumber}»: وزن اکسل ({row.QuantityMt:N4}) از باقیماندهٔ حمل بیشتر بود و به {leg.RemainingQuantityMt:N4} کم شد.");
                quantity = leg.RemainingQuantityMt;
            }

            string? targetKey = null;
            string? targetVehicleNumber = null;
            if (!string.IsNullOrWhiteSpace(row.TargetVehicleNumber))
            {
                var targetNorm = NormalizeVehicleNumber(row.TargetVehicleNumber);
                var target = targets.FirstOrDefault(t => NormalizeVehicleNumber(t.Number) == targetNorm);
                if (target is null)
                {
                    // وسیلهٔ جدید؛ نمبر در ردیف می‌ماند و هنگام ثبت، موتر با همین نمبر ساخته می‌شود.
                    targetVehicleNumber = row.TargetVehicleNumber;
                    warnings.Add($"«{row.TargetVehicleNumber}»: موتر جدید است و هنگام ثبت ساخته می‌شود.");
                }
                else
                {
                    targetKey = target.Key;
                }
            }

            matched.Add(new
            {
                legId = leg.LegId,
                quantityMt = quantity,
                remainingMt = leg.RemainingQuantityMt,
                targetKey,
                targetVehicleNumber,
                mergeGroup = row.MergeGroup
            });
        }

        if (matched.Count == 0)
        {
            return Json(new { ok = false, message = "هیچ ردیفی با حمل‌های در جریان تطبیق نشد.", warnings });
        }

        return Json(new { ok = true, rows = matched, warnings });
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SettleFreight(
        TransportFreightSettlementViewModel model,
        [FromForm(Name = FormTokenHtmlHelper.FieldName)] string? formToken = null)
    {
        if (!ModelState.IsValid)
        {
            TempData["err"] = "اطلاعات تسویهٔ کرایه کامل یا معتبر نیست.";
            return RedirectToAction(nameof(Details), new { id = model.TransportLegId });
        }

        // تسویهٔ کرایه یک مصرف واقعی می‌سازد؛ ارسال دوم نباید کرایه را دو برابر کند.
        _formTokens?.Stamp(formToken, "Transport.SettleFreight", nameof(InventoryTransportLeg));

        try
        {
            await _workflow.SettleFreightAsync(new SettleTransportFreightCommand
            {
                TransportLegId = model.TransportLegId,
                SettlementDate = model.SettlementDate,
                FreightRateUsdPerMt = model.FreightRateUsdPerMt,
                FreightCostUsd = model.FreightCostUsd,
                DriverId = model.DriverId,
                Notes = model.Notes
            });
            TempData["ok"] = "کرایهٔ حمل تسویه شد؛ موجودی و وضعیت فیزیکی بار تغییر نکرد.";
        }
        catch (DbUpdateException duplicate) when (_formTokens?.IsDuplicate(duplicate) == true)
        {
            TempData["err"] = "کرایهٔ این حمل قبلاً تسویه شده است و دوباره ثبت نشد.";
        }
        catch (BusinessRuleException ex)
        {
            TempData["err"] = ex.Message;
        }
        return RedirectToAction(nameof(Details), new { id = model.TransportLegId });
    }

    private async Task<TransportStartViewModel> BuildStartModelAsync(TransportStartViewModel model)
    {
        model.LoadingReceipts = await LoadReceiptOptionsAsync();
        model.ActiveTransports = (await LoadActiveTransportRowsAsync())
            .Select(x => new TransportLookupItem(x.LegId, x.Label, x.RemainingQuantityMt))
            .ToList();
        return model;
    }

    private async Task<IReadOnlyList<TransportLookupItem>> LoadReceiptOptionsAsync()
    {
        var receipts = await _db.LoadingReceipts
            .AsNoTracking()
            .Where(r => !r.IsCancelled
                && (r.ReceiptDestination == LoadingReceiptDestination.DirectDispatch
                    || r.ReceiptDestination == LoadingReceiptDestination.Mixed))
            .Select(r => new
            {
                r.Id,
                r.ReceiptDate,
                Reference = r.ReferenceDocument,
                Product = r.LoadingRegister != null && r.LoadingRegister.Product != null
                    ? r.LoadingRegister.Product.Name
                    : "",
                QuantityMt = r.Allocations
                    .Where(a => a.Destination == LoadingReceiptAllocationDestination.DirectDispatchToTruck
                        && a.Status != LoadingReceiptAllocationStatus.Cancelled)
                    .Sum(a => a.QuantityMt)
            })
            .OrderByDescending(r => r.ReceiptDate)
            .ThenByDescending(r => r.Id)
            .Take(250)
            .ToListAsync();

        if (receipts.Count == 0)
        {
            return [];
        }
        var ids = receipts.Select(r => r.Id).ToList();
        var used = await _db.InventoryTransportLegAllocations
            .AsNoTracking()
            .Where(a => a.SourceLoadingReceiptId.HasValue
                && ids.Contains(a.SourceLoadingReceiptId.Value)
                && a.InventoryTransportLeg != null
                && a.InventoryTransportLeg.Status != InventoryTransportLegStatus.Cancelled)
            .GroupBy(a => a.SourceLoadingReceiptId!.Value)
            .Select(g => new { ReceiptId = g.Key, QuantityMt = g.Sum(a => a.QuantityMt) })
            .ToDictionaryAsync(x => x.ReceiptId, x => x.QuantityMt);

        return receipts
            .Select(r => new
            {
                Row = r,
                Available = decimal.Round(Math.Max(r.QuantityMt - used.GetValueOrDefault(r.Id), 0m), 4)
            })
            .Where(x => x.Available > Epsilon)
            .Select(x => new TransportLookupItem(
                x.Row.Id,
                $"{(string.IsNullOrWhiteSpace(x.Row.Reference) ? $"رسید #{x.Row.Id}" : x.Row.Reference)} — {x.Row.Product} — {x.Available:N4} MT",
                x.Available))
            .ToList();
    }

    private async Task<bool> PopulateReceiptModelAsync(TransportStartFromReceiptViewModel model)
    {
        var option = (await LoadReceiptOptionsAsync()).FirstOrDefault(x => x.Id == model.LoadingReceiptId);
        if (option is null)
        {
            return false;
        }
        var product = await _db.LoadingReceipts.AsNoTracking()
            .Where(r => r.Id == model.LoadingReceiptId)
            .Select(r => r.LoadingRegister != null && r.LoadingRegister.Product != null
                ? r.LoadingRegister.Product.Name
                : "")
            .FirstAsync();
        model.ReceiptLabel = option.Label;
        model.ProductName = product;
        model.AvailableQuantityMt = option.AvailableQuantityMt;
        return true;
    }

    private async Task<bool> PopulateLoadingModelAsync(TransportStartFromLoadingViewModel model)
    {
        var loading = await _db.LoadingRegisters
            .AsNoTracking()
            .Where(l => l.Id == model.LoadingRegisterId)
            .Select(l => new
            {
                l.Id,
                l.LoadedQuantityMt,
                l.TransportType,
                l.TruckId,
                l.VesselId,
                l.LogisticsServiceProviderId,
                l.BillOfLadingNumber,
                l.RwbNo,
                ProductName = l.Product != null ? l.Product.Name : "",
                ContractLabel = l.Contract != null ? l.Contract.ContractNumber : ""
            })
            .SingleOrDefaultAsync();
        if (loading is null)
        {
            return false;
        }

        var receivedMt = await _db.LoadingReceipts.AsNoTracking()
            .Where(r => r.LoadingRegisterId == loading.Id && !r.IsCancelled)
            .SumAsync(r => (decimal?)r.ReceivedQuantityMt) ?? 0m;
        var shortageMt = await _db.LossEvents.AsNoTracking()
            .Where(e => (e.LoadingRegisterId == loading.Id
                    || e.LoadingReceiptId.HasValue && e.LoadingReceipt != null
                        && e.LoadingReceipt.LoadingRegisterId == loading.Id)
                && e.Stage == LossEventStage.ReceiptShortage
                && !e.IsCancelled)
            .SumAsync(e => (decimal?)(e.DifferenceQuantityMt > 0m
                ? e.DifferenceQuantityMt
                : e.ChargeableLossMt > 0m ? e.ChargeableLossMt : 0m)) ?? 0m;
        var transportedMt = await _db.InventoryTransportLegAllocations.AsNoTracking()
            .Where(a => a.SourceLoadingRegisterId == loading.Id
                && a.InventoryTransportLeg != null
                && a.InventoryTransportLeg.Status != InventoryTransportLegStatus.Cancelled)
            .SumAsync(a => (decimal?)a.QuantityMt) ?? 0m;
        model.AvailableQuantityMt = decimal.Round(
            Math.Max(loading.LoadedQuantityMt - receivedMt - shortageMt - transportedMt, 0m), 4);
        if (model.AvailableQuantityMt <= Epsilon)
        {
            return false;
        }

        model.LoadingLabel = $"{(string.IsNullOrWhiteSpace(loading.ContractLabel) ? $"بارگیری #{loading.Id}" : loading.ContractLabel)} — #{loading.Id}";
        model.ProductName = loading.ProductName;
        model.Reference ??= loading.RwbNo ?? loading.BillOfLadingNumber;
        if (model.QuantityMt <= 0m)
        {
            model.TransportType = loading.TransportType;
            model.TruckId = loading.TransportType == LoadingTransportType.Truck ? loading.TruckId : null;
            model.VesselId = loading.TransportType == LoadingTransportType.Vessel ? loading.VesselId : null;
            model.ServiceProviderId = loading.LogisticsServiceProviderId;
        }
        return true;
    }

    private async Task PopulateContinueSourcesAsync(TransportContinueViewModel model, int? selectedLegId = null)
    {
        var posted = (model.Sources ?? []).ToDictionary(s => s.LegId);
        var rows = await LoadActiveTransportRowsAsync();
        model.Sources = rows.Select(row =>
        {
            posted.TryGetValue(row.LegId, out var input);
            var selected = input?.Selected == true || selectedLegId == row.LegId;
            return new TransportContinueSourceInput
            {
                LegId = row.LegId,
                Label = row.Label,
                ProductName = row.ProductName,
                VehicleNumber = row.VehicleNumber,
                RemainingQuantityMt = row.RemainingQuantityMt,
                Selected = selected,
                TargetKey = input?.TargetKey,
                TargetVehicleNumber = input?.TargetVehicleNumber,
                MergeGroup = input?.MergeGroup,
                QuantityMt = input?.QuantityMt > 0m
                    ? Math.Min(input.QuantityMt, row.RemainingQuantityMt)
                    : selected ? row.RemainingQuantityMt : 0m
            };
        }).ToList();
    }

    private async Task<IReadOnlyList<ActiveTransportRow>> LoadActiveTransportRowsAsync()
    {
        var legs = await _db.InventoryTransportLegs.AsNoTracking()
            .Where(l => l.Status == InventoryTransportLegStatus.Loaded
                || l.Status == InventoryTransportLegStatus.InTransit)
            .OrderByDescending(l => l.LoadedDate)
            .ThenByDescending(l => l.Id)
            .Select(l => new
            {
                l.Id,
                l.TransportType,
                ProductName = l.Product != null ? l.Product.Name : "",
                Contract = l.SourcePurchaseContract != null ? l.SourcePurchaseContract.ContractNumber : "",
                Vehicle = l.Truck != null ? l.Truck.PlateNumber
                    : l.Wagon != null ? l.Wagon.WagonNumber
                    : l.Vessel != null ? l.Vessel.Name
                    : l.WagonNumber,
                l.LoadedDate
            })
            .Take(500)
            .ToListAsync();
        var remaining = await _quantities.GetRemainingMtAsync(legs.Select(l => l.Id).ToList());
        return legs
            .Select(l => new ActiveTransportRow(
                l.Id,
                l.ProductName,
                $"حمل #{l.Id} — {TransportTypeText(l.TransportType)} {(string.IsNullOrWhiteSpace(l.Vehicle) ? "" : l.Vehicle)} — {l.ProductName} — {l.Contract}",
                l.Vehicle ?? string.Empty,
                remaining.GetValueOrDefault(l.Id)))
            .Where(l => l.RemainingQuantityMt > Epsilon)
            .ToList();
    }

    private async Task PopulateVehicleLookupsAsync()
    {
        ViewBag.Trucks = new SelectList(
            await _db.Trucks.AsNoTracking().Where(t => t.IsActive).OrderBy(t => t.PlateNumber).ToListAsync(),
            "Id", "PlateNumber");
        ViewBag.Wagons = new SelectList(
            await _db.Wagons.AsNoTracking().Where(w => w.IsActive).OrderBy(w => w.WagonNumber).ToListAsync(),
            "Id", "WagonNumber");
        ViewBag.Vessels = new SelectList(
            await _db.Vessels.AsNoTracking().Where(v => v.IsActive).OrderBy(v => v.Name).ToListAsync(),
            "Id", "Name");
        ViewBag.Drivers = new SelectList(
            await _db.Drivers.AsNoTracking().Where(d => d.IsActive).OrderBy(d => d.FullName).ToListAsync(),
            "Id", "FullName");
        ViewBag.ServiceProviders = new SelectList(
            await _db.ServiceProviders.AsNoTracking().Where(p => p.IsActive).OrderBy(p => p.Name).ToListAsync(),
            "Id", "Name");
        ViewBag.TargetVehicles = (await LoadTargetVehicleRowsAsync())
            .Select(v => new TransportTargetVehicleOption(v.Key, v.Label, v.Group))
            .ToList();
    }

    // موتر/واگن/کشتیِ فعال در یک لیست، با کلید «نوع:شناسه» تا هر ردیف بتواند وسیلهٔ مقصد خودش را داشته باشد.
    private async Task<IReadOnlyList<TargetVehicleRow>> LoadTargetVehicleRowsAsync()
    {
        var trucks = await _db.Trucks.AsNoTracking()
            .Where(t => t.IsActive).OrderBy(t => t.PlateNumber)
            .Select(t => new { t.Id, Number = t.PlateNumber })
            .ToListAsync();
        var wagons = await _db.Wagons.AsNoTracking()
            .Where(w => w.IsActive).OrderBy(w => w.WagonNumber)
            .Select(w => new { w.Id, Number = w.WagonNumber })
            .ToListAsync();
        var vessels = await _db.Vessels.AsNoTracking()
            .Where(v => v.IsActive).OrderBy(v => v.Name)
            .Select(v => new { v.Id, Number = v.Name })
            .ToListAsync();

        var rows = new List<TargetVehicleRow>();
        rows.AddRange(trucks.Select(t => new TargetVehicleRow(
            $"{(int)LoadingTransportType.Truck}:{t.Id}", t.Number ?? string.Empty, "موتر", t.Number ?? string.Empty)));
        rows.AddRange(wagons.Select(w => new TargetVehicleRow(
            $"{(int)LoadingTransportType.Wagon}:{w.Id}", w.Number ?? string.Empty, "واگن", w.Number ?? string.Empty)));
        rows.AddRange(vessels.Select(v => new TargetVehicleRow(
            $"{(int)LoadingTransportType.Vessel}:{v.Id}", v.Number ?? string.Empty, "کشتی", v.Number ?? string.Empty)));
        return rows;
    }

    // نمبرِ موترِ مقصدی که در داده‌های پایه نیست ساخته می‌شود — همان رفتار «انتقال گروهی از موجودی».
    // موترِ غیرفعال ساخته نمی‌شود و خطا می‌دهد تا کاربر خودش آن را فعال کند.
    private async Task<IReadOnlyDictionary<string, int>> ResolveOrCreateTargetTrucksAsync(
        IReadOnlyList<TransportContinueSourceInput> selectedSources)
    {
        var plates = selectedSources
            .Where(s => string.IsNullOrWhiteSpace(s.TargetKey) && !string.IsNullOrWhiteSpace(s.TargetVehicleNumber))
            .Select(s => s.TargetVehicleNumber!.Trim())
            .Where(plate => plate.Length > 0)
            .ToList();
        var resolved = new Dictionary<string, int>(StringComparer.Ordinal);
        if (plates.Count == 0)
        {
            return resolved;
        }

        var existing = await _db.Trucks
            .Where(t => t.PlateNumber != null)
            .Select(t => new { t.Id, t.PlateNumber, t.IsActive })
            .ToListAsync();
        var created = new List<(string Key, Truck Truck)>();

        foreach (var plate in plates)
        {
            var key = NormalizeVehicleNumber(plate);
            if (key.Length == 0 || resolved.ContainsKey(key) || created.Any(c => c.Key == key))
            {
                continue;
            }

            var match = existing.FirstOrDefault(t => NormalizeVehicleNumber(t.PlateNumber) == key);
            if (match is not null)
            {
                if (!match.IsActive)
                {
                    throw new BusinessRuleException(
                        "TRANSPORT_CONTINUE_TARGET_TRUCK_INACTIVE",
                        $"موتر «{plate}» غیرفعال است؛ ابتدا آن را در داده‌های پایه فعال کنید.");
                }
                resolved[key] = match.Id;
                continue;
            }

            var truck = new Truck { PlateNumber = plate, IsActive = true };
            _db.Trucks.Add(truck);
            created.Add((key, truck));
        }

        if (created.Count > 0)
        {
            await _db.SaveChangesAsync();
            foreach (var (key, truck) in created)
            {
                resolved[key] = truck.Id;
            }
        }

        return resolved;
    }

    // ادغام دیگر خودکار بر پایهٔ نمبر وسیله نیست: یک نمبر می‌تواند چند سفر جدا داشته باشد.
    // ردیف‌ها فقط وقتی در یک حملِ مقصد ادغام می‌شوند که وسیلهٔ مقصدشان یکی باشد و
    // «گروه ادغام» یکسانِ ناخالی داشته باشند، یا هر دو از وسیلهٔ پیش‌فرضِ فرم استفاده کنند.
    private static List<TransportTargetGroup> BuildTargetGroups(
        IReadOnlyList<TransportContinueSourceInput> selectedSources,
        TransportContinueViewModel model,
        IReadOnlyDictionary<string, int> newPlateTrucks)
    {
        var groups = new List<TransportTargetGroup>();
        for (var index = 0; index < selectedSources.Count; index++)
        {
            var source = selectedSources[index];
            LoadingTransportType transportType;
            int vehicleId;

            if (string.IsNullOrWhiteSpace(source.TargetKey)
                && !string.IsNullOrWhiteSpace(source.TargetVehicleNumber)
                && newPlateTrucks.TryGetValue(NormalizeVehicleNumber(source.TargetVehicleNumber), out var truckId))
            {
                transportType = LoadingTransportType.Truck;
                vehicleId = truckId;
            }
            else if (!TryResolveTargetVehicle(source.TargetKey, model, out transportType, out vehicleId))
            {
                throw new BusinessRuleException(
                    "TRANSPORT_CONTINUE_TARGET_MISSING",
                    $"وسیلهٔ مقصد برای «{DescribeSource(source)}» مشخص نیست.");
            }

            var mergeKey = BuildMergeKey(source, index);
            var group = groups.FirstOrDefault(g => g.TransportType == transportType
                && g.VehicleId == vehicleId
                && g.MergeKey == mergeKey);
            if (group is null)
            {
                group = new TransportTargetGroup(transportType, vehicleId, mergeKey);
                groups.Add(group);
            }
            group.Sources.Add(new ContinueToVehicleSource(source.LegId, source.QuantityMt));
        }
        return groups;
    }

    // خالی بودنِ گروه یعنی «ادغام نکن»؛ کلید یکتای ردیف ساخته می‌شود. ردیف‌هایی که وسیلهٔ
    // مقصدِ ردیفی ندارند و از وسیلهٔ پیش‌فرضِ فرم استفاده می‌کنند مثل قبل با هم ادغام می‌شوند.
    private static string BuildMergeKey(TransportContinueSourceInput source, int index)
    {
        if (!string.IsNullOrWhiteSpace(source.MergeGroup))
        {
            return "g:" + source.MergeGroup.Trim().ToLowerInvariant();
        }
        if (string.IsNullOrWhiteSpace(source.TargetKey) && string.IsNullOrWhiteSpace(source.TargetVehicleNumber))
        {
            return "default";
        }
        return "row:" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    // برچسبِ ردیف در فرم پست نمی‌شود؛ برای پیام خطا از نمبر وسیله یا شمارهٔ حمل استفاده می‌کنیم
    // تا کاربر بداند کدام ردیف مقصد ندارد.
    private static string DescribeSource(TransportContinueSourceInput source)
        => !string.IsNullOrWhiteSpace(source.Label)
            ? source.Label
            : !string.IsNullOrWhiteSpace(source.VehicleNumber)
                ? $"وسیله {source.VehicleNumber}"
                : $"حمل #{source.LegId}";

    private static string NormalizeVehicleNumber(string? value)
        => new((value ?? string.Empty).Trim().ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    // کلیدِ ردیف بر وسیلهٔ پیش‌فرضِ فرم اولویت دارد؛ خالی بودنِ هر دو یعنی مقصد مشخص نیست.
    private static bool TryResolveTargetVehicle(
        string? targetKey,
        TransportContinueViewModel model,
        out LoadingTransportType transportType,
        out int vehicleId)
    {
        transportType = model.TargetTransportType;
        vehicleId = 0;

        if (!string.IsNullOrWhiteSpace(targetKey))
        {
            var parts = targetKey.Split(':', 2);
            if (parts.Length == 2
                && int.TryParse(parts[0], out var rawType)
                && Enum.IsDefined(typeof(LoadingTransportType), rawType)
                && rawType != (int)LoadingTransportType.Unspecified
                && int.TryParse(parts[1], out var parsedId)
                && parsedId > 0)
            {
                transportType = (LoadingTransportType)rawType;
                vehicleId = parsedId;
                return true;
            }
            return false;
        }

        vehicleId = transportType switch
        {
            LoadingTransportType.Truck => model.TargetTruckId ?? 0,
            LoadingTransportType.Wagon => model.TargetWagonId ?? 0,
            LoadingTransportType.Vessel => model.TargetVesselId ?? 0,
            _ => 0
        };
        return vehicleId > 0;
    }

    private static string TransportTypeText(LoadingTransportType type) => type switch
    {
        LoadingTransportType.Truck => "موتر",
        LoadingTransportType.Wagon => "واگن",
        LoadingTransportType.Vessel => "کشتی",
        _ => "وسیله"
    };

    private sealed record ActiveTransportRow(
        int LegId,
        string ProductName,
        string Label,
        string VehicleNumber,
        decimal RemainingQuantityMt);

    /// <summary>وسیلهٔ مقصدِ قابل انتخاب در ردیف‌ها؛ Number فقط برای تطبیق با اکسل است.</summary>
    private sealed record TargetVehicleRow(string Key, string Label, string Group, string Number);

    /// <summary>حمل‌های مبدأیی که به یک وسیلهٔ مقصد مشترک می‌روند (= یک حمل مقصد).</summary>
    private sealed class TransportTargetGroup(LoadingTransportType transportType, int vehicleId, string mergeKey)
    {
        public LoadingTransportType TransportType { get; } = transportType;
        public int VehicleId { get; } = vehicleId;
        public string MergeKey { get; } = mergeKey;
        public List<ContinueToVehicleSource> Sources { get; } = [];
    }
}
