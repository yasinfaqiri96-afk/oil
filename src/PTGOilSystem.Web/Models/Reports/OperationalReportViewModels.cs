using PTGOilSystem.Web.Services.Reporting;

namespace PTGOilSystem.Web.Models.Reports;

/// <summary>
/// یک دستهٔ مرکز گزارشات. ساختار عمداً تخت است: دسته → چند گزارش اصلی، بدون طبقهٔ سوم.
/// </summary>
public sealed class ReportHubGroupViewModel
{
    public string TitleFa { get; init; } = "";
    public string TitleEn { get; init; } = "";
    public string Icon { get; init; } = "";
    public IReadOnlyList<ReportHubCardViewModel> Cards { get; init; } = [];
}

/// <summary>
/// موجودی قابل فروش = موجودی فیزیکی (فقط InventoryMovement) منهای رزرو فعال پیش‌فروش.
/// موجودی در مسیر هرگز به این رقم اضافه نمی‌شود.
/// </summary>
public sealed class SellableStockReportViewModel
{
    public ManagementReportFilterViewModel Filter { get; init; } = new();
    public IReadOnlyList<SellableStockRow> Rows { get; init; } = [];
    public IReadOnlyList<ReportMetricViewModel> Metrics { get; init; } = [];

    public decimal TotalPhysicalMt => Rows.Sum(r => r.PhysicalStockMt);
    public decimal TotalReservedMt => Rows.Sum(r => r.ReservedMt);
    public decimal TotalSellableMt => Rows.Sum(r => r.SellableMt);
    public decimal TotalUnallocatedReservedMt => Rows
        .Select(r => (r.ProductId, r.UnallocatedReservedMt))
        .Distinct()
        .Sum(x => x.UnallocatedReservedMt);
    public int OverReservedCount => Rows.Count(r => r.IsOverReserved);
}

public sealed class PreSaleDiscrepancyReportViewModel
{
    public ManagementReportFilterViewModel Filter { get; init; } = new();
    public IReadOnlyList<PreSaleDiscrepancySummary> Summary { get; init; } = [];
    public PreSaleDiscrepancyKind SelectedKind { get; init; } = PreSaleDiscrepancyKind.OverDelivery;
    public IReadOnlyList<PreSaleDiscrepancyRow> Rows { get; init; } = [];
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 50;
    public int TotalCount { get; init; }
    public int PageCount => Math.Max(1, (int)Math.Ceiling(TotalCount / (double)Math.Max(1, PageSize)));
    public int TotalIssueCount => Summary.Sum(s => s.Count);
}

public sealed class NegativeStockReportViewModel
{
    public ManagementReportFilterViewModel Filter { get; init; } = new();
    public IReadOnlyList<NegativeStockFinding> Rows { get; init; } = [];
    public IReadOnlyList<ReportMetricViewModel> Metrics { get; init; } = [];

    public int OpenCount => Rows.Count(r => r.Status == NegativeStockStatus.Open);
    public int HealedCount => Rows.Count(r => r.Status == NegativeStockStatus.HealedLegacy);
    public decimal TotalOpenShortageMt => Rows
        .Where(r => r.Status == NegativeStockStatus.Open)
        .Sum(r => r.ClosingBalanceMt);
}
