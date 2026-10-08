using PTGOilSystem.Web.Services;

namespace PTGOilSystem.Web.Models.Reports;

public sealed class InventorySalesReportViewModel
{
    public SellableStockReportViewModel Stock { get; init; } = new();
    public InventoryOperationsReportViewModel? Operations { get; init; }
    public IReadOnlyList<StockCardItem> Movements { get; init; } = [];
    public bool ShowDetails { get; init; }
    public bool CanViewMovements { get; init; }
    public int Page { get; init; } = 1;
    public int TotalMovements { get; init; }
    public const int PageSize = 50;
    public int PageCount => Math.Max(1, (int)Math.Ceiling(TotalMovements / (double)PageSize));
}
