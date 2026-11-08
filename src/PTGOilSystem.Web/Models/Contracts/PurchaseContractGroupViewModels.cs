using PTGOilSystem.Web.Models.Entities;

namespace PTGOilSystem.Web.Models.Contracts;

public sealed record PurchaseGroupTotalsInput(int? SupplierId, decimal QuantityMt, decimal? UnitPriceUsd);

/// <summary>
/// جمع‌بندی گزارشی قرارداد چندتأمین‌کننده. میانگین وزنی فقط برای نمایش است و هیچ‌جا به‌جای
/// بهای تمام‌شدهٔ موجودی یا قیمت خرید هر ردیف به کار نمی‌رود.
/// </summary>
public sealed record PurchaseGroupTotals(
    decimal TotalQuantityMt,
    decimal TotalValueUsd,
    int SupplierCount,
    decimal? WeightedAveragePriceUsd,
    int UnpricedLineCount)
{
    public static PurchaseGroupTotals Calculate(IEnumerable<PurchaseGroupTotalsInput> lines)
    {
        var rows = lines.ToList();
        var priced = rows.Where(row => row.UnitPriceUsd is > 0m).ToList();
        var pricedQuantity = priced.Sum(row => row.QuantityMt);
        var totalValue = priced.Sum(row => row.QuantityMt * row.UnitPriceUsd!.Value);

        return new PurchaseGroupTotals(
            rows.Sum(row => row.QuantityMt),
            totalValue,
            rows.Where(row => row.SupplierId.HasValue).Select(row => row.SupplierId!.Value).Distinct().Count(),
            pricedQuantity > 0m ? decimal.Round(totalValue / pricedQuantity, 4, MidpointRounding.AwayFromZero) : null,
            rows.Count - priced.Count);
    }
}

public sealed class PurchaseContractGroupLineViewModel
{
    public int ContractId { get; init; }
    public string ContractNumber { get; init; } = "";
    public string ContractLabel { get; init; } = "";
    public int? SupplierId { get; init; }
    public string SupplierName { get; init; } = "";
    public string ProductName { get; init; } = "";
    public decimal QuantityMt { get; init; }
    public decimal? UnitPriceUsd { get; init; }
    public decimal? ValueUsd { get; init; }
    public decimal LoadedQuantityMt { get; init; }
    public decimal ReceivedQuantityMt { get; init; }
    public decimal RemainingQuantityMt { get; init; }
    public decimal PaidUsd { get; init; }
    /// <summary>ماندهٔ رسمی همین تأمین‌کننده روی همین ردیف. مثبت = طلب شرکت، منفی = بدهی شرکت.</summary>
    public decimal SupplierBalanceUsd { get; init; }
    public ContractStatus Status { get; init; }
}

public sealed class PurchaseContractGroupDetailsViewModel
{
    public int Id { get; init; }
    public string GroupNumber { get; init; } = "";
    public string Name { get; init; } = "";
    public string CompanyName { get; init; } = "";
    public DateTime ContractDate { get; init; }
    public string? Notes { get; init; }
    public string Currency { get; init; } = "USD";
    public IReadOnlyList<PurchaseContractGroupLineViewModel> Lines { get; init; } = [];
    public PurchaseGroupTotals Totals { get; init; } = PurchaseGroupTotals.Calculate([]);
    public bool CanAddLine { get; init; }

    /// <summary>چرخهٔ هر ردیف لغونشده از خلاصهٔ رسمی همان ردیف (صفحهٔ جزئیات قرارداد عادی).</summary>
    public IReadOnlyList<PTGOilSystem.Web.Models.ContractJourney.PurchaseGroupLineLifecycle> LineLifecycles { get; init; } = [];

    /// <summary>جمع چرخهٔ ردیف‌ها؛ خالی وقتی خلاصهٔ ردیف‌ها ساخته نشد.</summary>
    public PTGOilSystem.Web.Models.ContractJourney.ContractLifecycleFigures? Lifecycle { get; init; }
}
