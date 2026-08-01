using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using PTGOilSystem.Web.Models.Reconciliation;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Reconciliation;
using PTGOilSystem.Web.Services.Time;

namespace PTGOilSystem.Web.Controllers;

/// <summary>
/// کنترلر فقط فیلتر می‌گیرد، دسترسی را کنترل می‌کند و نتیجهٔ <see cref="IReconciliationService"/>
/// را نمایش یا Export می‌دهد. هیچ query یا فرمول مغایرت‌گیری داخل کنترلر نیست.
/// </summary>
[Authorize]
public partial class ReconciliationController : Controller
{
    private readonly IReconciliationService _reconciliation;
    private readonly IAfghanistanBusinessClock _businessClock;

    // سازندهٔ DI. بدون این نشانه، وجودِ سازندهٔ کمکیِ تست باعث خطای «چند سازنده»
    // در زمان اجرا می‌شود و کل صفحات مغایرت‌گیری ۵۰۰ می‌دهند.
    [ActivatorUtilitiesConstructor]
    public ReconciliationController(
        IReconciliationService reconciliation,
        IAfghanistanBusinessClock businessClock)
    {
        _reconciliation = reconciliation;
        _businessClock = businessClock;
    }

    /// <summary>
    /// سازندهٔ کمکی برای تست‌ها: سرویس پیش‌فرض را روی همان DbContext می‌سازد.
    /// کنترلر خودش هیچ‌جا از DbContext استفاده نمی‌کند.
    /// </summary>
    public ReconciliationController(
        Data.ApplicationDbContext db,
        IPurchaseAggregationService? purchaseAggregation = null,
        IAfghanistanBusinessClock? businessClock = null)
        : this(
            new ReconciliationService(db, purchaseAggregation, businessClock),
            businessClock ?? new AfghanistanBusinessClock(TimeProvider.System))
    {
    }

    public IActionResult Index() => View(new ReconciliationIndexViewModel());

    // شمارنده‌ها بعد از رندر صفحه با AJAX خوانده می‌شوند تا مسیر بحرانی بارگذاری
    // صفحهٔ Index سبک بماند. منطق شمارش در سرویس است.
    [HttpGet]
    public async Task<IActionResult> Summary()
    {
        var counts = await _reconciliation.BuildSummaryCountsAsync();
        return Json(new
        {
            openContracts = counts.OpenContracts,
            openShipments = counts.OpenShipments,
            incompleteAfterReceipt = counts.IncompleteAfterReceipt,
            missingLedger = counts.MissingLedger,
            nonZeroBalances = counts.NonZeroBalances,
            financeIssues = counts.FinanceIssues,
            lossEvents = counts.LossEvents,
            employeeIssues = counts.EmployeeIssues,
            roznamchaIssues = counts.RoznamchaIssues,
            suspenseMoney = counts.SuspenseMoney
        });
    }

    public async Task<IActionResult> OpenContracts() => View(await _reconciliation.BuildOpenContractsAsync());
    public async Task<IActionResult> OpenShipments() => View(await _reconciliation.BuildOpenShipmentsAsync());
    public async Task<IActionResult> MissingLedger() => View(await _reconciliation.BuildMissingLedgerAsync());
    public async Task<IActionResult> NonZeroBalances() => View(await _reconciliation.BuildNonZeroBalancesAsync());
    public async Task<IActionResult> IncompleteAfterReceipt() => View(await _reconciliation.BuildIncompleteAfterReceiptAsync());
    public async Task<IActionResult> EmployeeTransactions() => View(await _reconciliation.BuildEmployeeReconciliationAsync());
    public async Task<IActionResult> Roznamcha() => View(await _reconciliation.BuildRoznamchaReconciliationAsync());

    public async Task<IActionResult> SuspenseMoney(
        string? severity = null,
        DateTime? fromDate = null,
        DateTime? toDate = null)
        => View(await _reconciliation.BuildSuspenseMoneyAsync(severity, fromDate, toDate));

    /// <summary>
    /// مغایرت‌های تفصیلی. هر دسته Paging مستقل دارد و شمارش و صفحه‌بندی در SQL انجام می‌شود.
    /// </summary>
    public async Task<IActionResult> Discrepancies(
        ReconciliationDiscrepancyCategory? category = null,
        int page = 1,
        int pageSize = 50,
        DateTime? fromDate = null,
        DateTime? toDate = null,
        CancellationToken ct = default)
    {
        var counts = await _reconciliation.BuildDiscrepancyCountsAsync(fromDate, toDate, ct);

        ReconciliationDiscrepancyPage? selected = null;
        if (category.HasValue)
        {
            selected = await _reconciliation.BuildDiscrepancyPageAsync(
                category.Value, page, pageSize, fromDate, toDate, ct);
        }

        return View(new ReconciliationDiscrepanciesViewModel
        {
            Counts = counts,
            Selected = selected,
            SelectedCategory = category,
            FromDate = fromDate,
            ToDate = toDate,
            GeneratedOnBusinessDate = _businessClock.Today
        });
    }
}
