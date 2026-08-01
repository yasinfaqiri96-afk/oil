using PTGOilSystem.Web.Models.Reconciliation;

namespace PTGOilSystem.Web.Services.Reconciliation;

/// <summary>شمارندهٔ کارت‌های صفحهٔ اصلی مغایرت‌گیری.</summary>
public sealed class ReconciliationSummaryCounts
{
    public int OpenContracts { get; init; }
    public int OpenShipments { get; init; }
    public int IncompleteAfterReceipt { get; init; }
    public int MissingLedger { get; init; }
    public int NonZeroBalances { get; init; }
    public int LossEvents { get; init; }
    public int EmployeeIssues { get; init; }
    public int RoznamchaIssues { get; init; }
    public int SuspenseMoney { get; init; }

    public int FinanceIssues => MissingLedger + NonZeroBalances;
}

/// <summary>
/// تمام منطق مغایرت‌گیری. کنترلر هیچ query مستقیمی به دیتابیس نمی‌زند و فقط
/// این سرویس را صدا می‌کند.
/// </summary>
public interface IReconciliationService
{
    Task<ReconciliationSummaryCounts> BuildSummaryCountsAsync();

    Task<OpenContractsViewModel> BuildOpenContractsAsync();
    Task<OpenShipmentsViewModel> BuildOpenShipmentsAsync();
    Task<MissingLedgerViewModel> BuildMissingLedgerAsync();
    Task<NonZeroBalancesViewModel> BuildNonZeroBalancesAsync();
    Task<IncompleteAfterReceiptViewModel> BuildIncompleteAfterReceiptAsync();
    Task<EmployeeReconciliationViewModel> BuildEmployeeReconciliationAsync();
    Task<RoznamchaReconciliationViewModel> BuildRoznamchaReconciliationAsync();

    Task<SuspenseMoneyViewModel> BuildSuspenseMoneyAsync();
    Task<SuspenseMoneyViewModel> BuildSuspenseMoneyAsync(string? severity, DateTime? fromDate, DateTime? toDate);

    Task<IReadOnlyList<ReconciliationDiscrepancyCount>> BuildDiscrepancyCountsAsync(
        DateTime? fromDate = null,
        DateTime? toDate = null,
        CancellationToken ct = default);

    /// <summary>شمارش یک دسته؛ UI، Export و تست همگی همین عدد را می‌بینند.</summary>
    Task<int> BuildDiscrepancyCountAsync(
        ReconciliationDiscrepancyCategory category,
        DateTime? fromDate = null,
        DateTime? toDate = null,
        CancellationToken ct = default);

    /// <summary>
    /// همان query صفحه، ولی به‌صورت جریانی برای Export. ردیف‌ها همان‌طور که از دیتابیس
    /// می‌رسند مصرف می‌شوند و هیچ‌وقت کل نتیجه در حافظه جمع نمی‌شود.
    /// </summary>
    IAsyncEnumerable<ReconciliationDiscrepancyRow> StreamDiscrepancyRowsAsync(
        ReconciliationDiscrepancyCategory category,
        DateTime? fromDate = null,
        DateTime? toDate = null,
        CancellationToken ct = default);

    Task<ReconciliationDiscrepancyPage> BuildDiscrepancyPageAsync(
        ReconciliationDiscrepancyCategory category,
        int page = 1,
        int pageSize = 50,
        DateTime? fromDate = null,
        DateTime? toDate = null,
        CancellationToken ct = default);
}
