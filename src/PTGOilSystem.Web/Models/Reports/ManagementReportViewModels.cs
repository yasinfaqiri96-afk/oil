using System.ComponentModel.DataAnnotations;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.PartyStatements;
using PTGOilSystem.Web.Services.Reporting;

namespace PTGOilSystem.Web.Models.Reports;

// مرکز گزارشات هیچ محاسبه‌ای انجام نمی‌دهد؛ فقط دسته‌بندی و مسیر نگه می‌دارد.
// شمارنده‌های قبلی (فروش/مصارف/قرارداد/...) حذف شدند چون هیچ Query پشتشان نبود.
public sealed class ReportHubViewModel
{
    public IReadOnlyList<ReportHubGroupViewModel> Groups { get; init; } = [];
}

public sealed class ReportHubCardViewModel
{
    public string Controller { get; init; } = "";
    public string Action { get; init; } = "";
    public string TitleFa { get; init; } = "";
    public string TitleEn { get; init; } = "";
    public string DescriptionFa { get; init; } = "";
    public string DescriptionEn { get; init; } = "";
    public string Icon { get; init; } = "";
    public string ToneClass { get; init; } = "";
}

public sealed class ReportMetricViewModel
{
    public string Label { get; init; } = "";
    public string Value { get; init; } = "";
    public string? Detail { get; init; }
    public string Icon { get; init; } = "";
    public string ToneClass { get; init; } = "";
}

public sealed class CompanyFinancialOverviewViewModel
{
    public ManagementReportFilterViewModel Filter { get; init; } = new();
    public decimal RevenueUsd { get; init; }
    public decimal PurchaseCostUsd { get; init; }
    public decimal ExpenseUsd { get; init; }
    public decimal LossCostUsd { get; init; }
    public decimal ExchangeGainUsd { get; init; }
    public decimal ExchangeLossUsd { get; init; }
    public decimal NetCashMovementUsd { get; init; }
    public decimal CustomerReceivableUsd { get; init; }
    public decimal SupplierPayableUsd { get; init; }
    public decimal SarrafNetUsd { get; init; }
    public int WarningCount { get; init; }
    public int UncostedSaleCount { get; init; }
    public PnlConfidence PnlConfidence { get; init; } = PnlConfidence.NeedsReview;
    public IReadOnlyList<ReportMetricViewModel> Metrics { get; init; } = [];
    public IReadOnlyList<ContractPnlRowViewModel> TopContracts { get; init; } = [];
    public decimal GrossProfitUsd => PnlMath.GrossProfit(RevenueUsd, PurchaseCostUsd);
    public decimal NetProfitUsd => PnlMath.NetProfit(
        RevenueUsd,
        PurchaseCostUsd,
        ExpenseUsd + LossCostUsd,
        ExchangeGainUsd,
        ExchangeLossUsd);
}

public sealed class CashFlowReportRowViewModel
{
    public string GroupName { get; init; } = "";
    public decimal InflowUsd { get; init; }
    public decimal OutflowUsd { get; init; }
    public decimal NetUsd => InflowUsd - OutflowUsd;
    public int Count { get; init; }
}

public sealed class CashFlowAccountRowViewModel
{
    public string CashAccountName { get; init; } = "";
    public string Currency { get; init; } = "";
    public decimal InflowUsd { get; init; }
    public decimal OutflowUsd { get; init; }
    public decimal NetUsd => InflowUsd - OutflowUsd;
}

public sealed class CashFlowReportViewModel
{
    public ManagementReportFilterViewModel Filter { get; init; } = new();
    public IReadOnlyList<ReportMetricViewModel> Metrics { get; init; } = [];
    public IReadOnlyList<CashFlowReportRowViewModel> Rows { get; init; } = [];
    public IReadOnlyList<CashFlowAccountRowViewModel> AccountRows { get; init; } = [];
    public decimal TotalInflowUsd => Rows.Sum(r => r.InflowUsd);
    public decimal TotalOutflowUsd => Rows.Sum(r => r.OutflowUsd);
    public decimal NetCashFlowUsd => TotalInflowUsd - TotalOutflowUsd;
}

public sealed class ReceivablePayableRowViewModel
{
    public string PartyType { get; init; } = "";
    public int? PartyId { get; init; }
    public string PartyName { get; init; } = "";
    public decimal OpeningBalanceUsd { get; init; }
    public decimal DebitUsd { get; init; }
    public decimal CreditUsd { get; init; }
    public decimal PeriodMovementUsd => CreditUsd - DebitUsd;
    public decimal BalanceUsd => OpeningBalanceUsd + PeriodMovementUsd;
    public string BalanceKind { get; init; } = "";
    public DateTime? LastEntryDate { get; init; }
    public string? DetailsController { get; init; }
}

public sealed class ReceivablesPayablesReportViewModel
{
    public ManagementReportFilterViewModel Filter { get; init; } = new();
    public IReadOnlyList<ReportMetricViewModel> Metrics { get; init; } = [];
    public IReadOnlyList<ReceivablePayableRowViewModel> Rows { get; init; } = [];
    public decimal CustomerReceivableUsd => Rows
        .Where(r => r.PartyType == nameof(PartyStatementPartyType.Customer) && r.BalanceUsd > 0m)
        .Sum(r => r.BalanceUsd);
    public decimal SupplierPayableUsd => -Rows.Where(r => r.PartyType == "Supplier" && r.BalanceUsd < 0m).Sum(r => r.BalanceUsd);
    public decimal ServiceProviderPayableUsd => -Rows.Where(r => r.PartyType == "ServiceProvider" && r.BalanceUsd < 0m).Sum(r => r.BalanceUsd);
    public decimal SarrafBalanceUsd => Rows.Where(r => r.PartyType == "Sarraf").Sum(r => r.BalanceUsd);
}

public sealed class InventoryOperationsRowViewModel
{
    public string GroupName { get; init; } = "";
    public string? SecondaryName { get; init; }
    public decimal QuantityMt { get; init; }
    public int MovementCount { get; init; }
    public DateTime? LastMovementDate { get; init; }
}

public sealed class InventoryOperationsWarningViewModel
{
    public string Title { get; init; } = "";
    public string Description { get; init; } = "";
    public int Count { get; init; }
    public string? Controller { get; init; }
    public string? Action { get; init; }
}

public sealed class InventoryOperationsReportViewModel
{
    public ManagementReportFilterViewModel Filter { get; init; } = new();
    public IReadOnlyList<ReportMetricViewModel> Metrics { get; init; } = [];
    public IReadOnlyList<InventoryOperationsRowViewModel> ProductRows { get; init; } = [];
    public IReadOnlyList<InventoryOperationsRowViewModel> TerminalRows { get; init; } = [];
    public IReadOnlyList<InventoryOperationsWarningViewModel> Warnings { get; init; } = [];
}

public sealed class ReportsWarningItemViewModel
{
    public string Title { get; init; } = "";
    public string Description { get; init; } = "";
    public int Count { get; init; }
    public string Severity { get; init; } = "";
    public string? Controller { get; init; }
    public string? Action { get; init; }
}

public sealed class ReportsWarningsViewModel
{
    public IReadOnlyList<ReportMetricViewModel> Metrics { get; init; } = [];
    public IReadOnlyList<ReportsWarningItemViewModel> Items { get; init; } = [];
    public int TotalIssueCount => Items.Sum(i => i.Count);
}

public sealed class ManagementReportFilterViewModel
{
    [DataType(DataType.Date)]
    public DateTime? FromDate { get; set; }

    [DataType(DataType.Date)]
    public DateTime? ToDate { get; set; }

    public int? ProductId { get; set; }
    public int? ContractId { get; set; }
    public int? CustomerId { get; set; }
    public int? SupplierId { get; set; }
    public int? TerminalId { get; set; }
    public int? StorageTankId { get; set; }
}

public sealed class ContractPnlRowViewModel
{
    public int ContractId { get; init; }
    public string ContractNumber { get; init; } = "";
    public ContractType ContractType { get; init; }
    public ContractStatus Status { get; init; }
    public string ProductName { get; init; } = "";
    public string? CounterpartyName { get; init; }
    public decimal ContractQuantityMt { get; init; }
    public decimal? ContractUnitPriceUsd { get; init; }
    public decimal TotalLoadedMt { get; init; }
    public decimal PricedLoadedMt { get; init; }
    public decimal PendingLoadedMt { get; init; }
    public int PendingLoadingCount { get; init; }
    public decimal PurchaseValueUsd { get; init; }
    public decimal? AveragePurchasePriceUsd => PricedLoadedMt > 0m
        ? Math.Round(PurchaseValueUsd / PricedLoadedMt, 4, MidpointRounding.AwayFromZero)
        : null;
    public bool NeedsReview => PendingLoadingCount > 0 || DirectSaleQuantityMismatchCount > 0;
    public decimal TransportCostUsd { get; init; }
    public decimal WarehouseCostUsd { get; init; }
    public decimal OtherCostUsd { get; init; }
    public decimal RailwayCostUsd { get; init; }
    public decimal CustomsCostUsd { get; init; }
    /// <summary>
    /// Sum of <c>ExpenseTransaction.AmountUsd</c> rows directly linked to this purchase contract
    /// (<c>ContractId == this.ContractId</c>, not cancelled). Inline LoadingRegister expenses
    /// (Transport/Warehouse/Other/Railway) and CustomsDeclaration totals live on separate
    /// records and are tracked in their own columns, so this column adds without double-counting.
    /// </summary>
    public decimal GeneralExpenseCostUsd { get; init; }
    /// <summary>
    /// Read-only USD valuation of chargeable losses on this purchase contract:
    /// <c>Σ(LossEvent.ChargeableLossMt × LoadingRegister.LoadingPriceUsd)</c> over non-cancelled
    /// events whose <c>LoadingRegisterId</c> resolves to a priced loading. Loss events without a
    /// known LoadingPriceUsd are excluded from the cost (and surfaced in
    /// <see cref="UnvaluedLossCount"/> so the operator can fix the missing snapshot).
    /// No data is stored — this is a derived figure for reporting only.
    /// </summary>
    public decimal LossCostUsd { get; init; }
    /// <summary>
    /// Number of non-cancelled chargeable LossEvents on this purchase contract whose linked
    /// LoadingRegister has no <c>LoadingPriceUsd</c>, so the USD value cannot be computed.
    /// </summary>
    public int UnvaluedLossCount { get; init; }
    /// <summary>
    /// مقدار موجودی که هنوز در مخزن است و رسیدش با حالت «ضایعات بعداً از تسویه مخزن»
    /// ثبت شده — یعنی ضایعهٔ قطعی این قرارداد هنوز مشخص نیست. مشتق از موجودی است.
    /// </summary>
    public decimal PendingSettlementQuantityMt { get; init; }
    /// <summary>تا وقتی مقدار بالا مثبت است، سود/زیان این قرارداد «موقت» است.</summary>
    public bool HasPendingTankSettlement => PendingSettlementQuantityMt > 0m;
    public decimal SarrafSupplierShortfallUsd { get; init; }
    public decimal ExchangeGainUsd { get; init; }
    public decimal ExchangeLossUsd { get; init; }
    public decimal NetExchangeDifferenceUsd => ExchangeLossUsd - ExchangeGainUsd;
    public decimal TotalCostUsd => PurchaseValueUsd + TransportCostUsd + WarehouseCostUsd + OtherCostUsd + RailwayCostUsd + CustomsCostUsd + GeneralExpenseCostUsd + LossCostUsd + SarrafSupplierShortfallUsd + ExchangeLossUsd - ExchangeGainUsd;
    public decimal TotalSoldMt { get; init; }
    public decimal TotalRevenueUsd { get; init; }
    public int DirectSaleQuantityMismatchCount { get; init; }
    public int UncostedSaleCount { get; init; }
    public PnlConfidence PnlConfidence { get; init; } = PnlConfidence.Legacy;
    public decimal GrossMarginUsd => PnlMath.GrossProfit(TotalRevenueUsd, TotalCostUsd);
    public decimal? MarginPercent => TotalRevenueUsd > 0 ? Math.Round((GrossMarginUsd / TotalRevenueUsd) * 100m, 1) : null;
}

public sealed class ContractPnlReportViewModel
{
    public ManagementReportFilterViewModel Filter { get; init; } = new();
    public IReadOnlyList<ContractPnlRowViewModel> PurchaseRows { get; init; } = [];
    public IReadOnlyList<ContractPnlRowViewModel> SaleRows { get; init; } = [];
    public int PendingPurchaseLoadingCount => PurchaseRows.Sum(r => r.PendingLoadingCount);
    public decimal PendingPurchaseLoadedMt => PurchaseRows.Sum(r => r.PendingLoadedMt);
    public bool HasPendingPurchasePricing => PendingPurchaseLoadingCount > 0;
    public int PendingTankSettlementCount => PurchaseRows.Count(r => r.HasPendingTankSettlement);
    public decimal PendingTankSettlementQuantityMt => PurchaseRows.Sum(r => r.PendingSettlementQuantityMt);
    public bool HasPendingTankSettlement => PendingTankSettlementCount > 0;
    public decimal TotalPurchaseCostUsd => PurchaseRows.Sum(r => r.TotalCostUsd);
    public decimal TotalSarrafSupplierShortfallUsd => PurchaseRows.Sum(r => r.SarrafSupplierShortfallUsd);
    public decimal TotalExchangeGainUsd => PurchaseRows.Sum(r => r.ExchangeGainUsd);
    public decimal TotalExchangeLossUsd => PurchaseRows.Sum(r => r.ExchangeLossUsd);
    public decimal TotalNetExchangeDifferenceUsd => TotalExchangeLossUsd - TotalExchangeGainUsd;
    public decimal TotalDirectSaleRevenueUsd => PurchaseRows.Sum(r => r.TotalRevenueUsd);
    public decimal TotalSalesRevenueUsd => SaleRows.Sum(r => r.TotalRevenueUsd);
    public decimal TotalRealisedSalesCostUsd => SaleRows.Sum(r => r.TotalCostUsd);
    public decimal TotalGrossMarginUsd => PnlMath.GrossProfit(TotalSalesRevenueUsd, TotalRealisedSalesCostUsd);
    public int TotalUncostedSaleCount => SaleRows.Sum(r => r.UncostedSaleCount);
}
