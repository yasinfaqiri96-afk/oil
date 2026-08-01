namespace PTGOilSystem.Web.Models;

public class DashboardViewModel
{
    public int ProductCount { get; set; }
    public int ActiveContractCount { get; set; }
    public int TotalContractCount { get; set; }
    public int LoadingCount { get; set; }
    public int LoadingReceiptCount { get; set; }
    public int SalesCount { get; set; }
    public int ShipmentCount { get; set; }
    public int RecentDispatchCount { get; set; }
    public decimal TotalSalesUsd { get; set; }
    public decimal TotalExpensesUsd { get; set; }
    public decimal TotalSalesWeekChangePercent { get; set; }
    public decimal TotalExpensesWeekChangePercent { get; set; }
    public decimal LoadingReceiptsWeekChangePercent { get; set; }
    public decimal LedgerEntriesWeekChangePercent { get; set; }
    public decimal NetUsd => TotalSalesUsd - TotalExpensesUsd;
    public decimal GrossMarginUsd => TotalSalesUsd - TotalExpensesUsd;
    public decimal TerminalStockMt { get; set; }
    public DashboardBalanceSummaryViewModel ContractBalanceSummary { get; set; } = new();
    public DashboardBalanceSummaryViewModel CustomerBalanceSummary { get; set; } = new();
    public DashboardBalanceSummaryViewModel SupplierBalanceSummary { get; set; } = new();
    public List<DashboardAlertViewModel> LowStockAlerts { get; set; } = new();
    public List<DashboardAlertViewModel> ContractsEndingSoonAlerts { get; set; } = new();
    public List<DashboardAlertViewModel> ShipmentsWithoutSalesAlerts { get; set; } = new();
    public List<DashboardAlertViewModel> ShipmentsWithoutExpensesAlerts { get; set; } = new();
    public List<string> MarketLabels { get; set; } = new();
    public List<decimal> MarketSalesSeries { get; set; } = new();
    public List<decimal> MarketExpenseSeries { get; set; } = new();
    public string MarketSubtitle { get; set; } = "";
    public string MarketRangeLabel { get; set; } = "";
    public List<DashboardActivityViewModel> RecentActivities { get; set; } = new();
    public List<DashboardShipmentProgressViewModel> ActiveShipmentProgress { get; set; } = new();
    public List<DashboardTankCapacityViewModel> TankCapacities { get; set; } = new();
    public decimal TotalTankStockMt { get; set; }
    public decimal TotalTankCapacityMt { get; set; }
    public decimal TankFillPercent => TotalTankCapacityMt <= 0m
        ? 0m
        : Math.Clamp(TotalTankStockMt / TotalTankCapacityMt * 100m, 0m, 100m);
    public DashboardOrderPanelViewModel OutboundOrderPanel { get; set; } = new();
    public DashboardOrderPanelViewModel InboundOrderPanel { get; set; } = new();
    public decimal PurchaseReserveUsd { get; set; }

    // --- Operational read-only stats (light, AsNoTracking) ---
    public int ShipmentsInTransitCount { get; set; }
    public decimal TodaySalesUsd { get; set; }
    public int TodaySalesCount { get; set; }
    public decimal TodayReceiptsUsd { get; set; }
    public decimal TodayPaymentsUsd { get; set; }
    public decimal TodayExpensesUsd { get; set; }
    public decimal MonthExpensesUsd { get; set; }
    public int ActiveSarrafCount { get; set; }
    public int TodayLoadingCount { get; set; }
    public int TodayDispatchCount { get; set; }
    public int LoadingsWithoutReceiptCount { get; set; }
    public int ShortageCount { get; set; }
    public int ContractsWithoutFinalPriceCount { get; set; }
    public int ReceiptsWithoutAllocationCount { get; set; }
    public int LoadingsWithoutCustomsCount { get; set; }
    public int SalesWithoutPaymentCount { get; set; }
    public int SarrafRateDiffCount { get; set; }
    public int ExcessShortageCount { get; set; }
    public int LowStockTankCount { get; set; }
}

public class DashboardBalanceSummaryViewModel
{
    public int ItemCount { get; set; }
    public decimal DebitTotalUsd { get; set; }
    public decimal CreditTotalUsd { get; set; }
    // مانده نمایشی مطابق قرارداد صورت‌حساب: Σ(داده − گرفته). هم‌علامتِ صفحات بیلانس.
    public decimal BaseBalanceUsd => DebitTotalUsd - CreditTotalUsd;
}

public class DashboardAlertViewModel
{
    public string Title { get; set; } = "";
    public string Message { get; set; } = "";
    public string Severity { get; set; } = "warning";
    public string? Reference { get; set; }
}

public class DashboardActivityViewModel
{
    public string DirectionClass { get; set; } = "is-buy";
    public string DirectionIcon { get; set; } = "bi-arrow-down-right";
    public string ProductIcon { get; set; } = "bi-box-seam-fill";
    public string Name { get; set; } = "";
    public string TimeText { get; set; } = "";
    public string Amount { get; set; } = "";
    public string AmountClass { get; set; } = "is-positive";
    public string Status { get; set; } = "";
    public string StatusClass { get; set; } = "status-badge-neutral";
}

// یک ردیف «محموله در جریان»: مقدار حمل‌شده در برابر مقدار کل محموله.
public class DashboardShipmentProgressViewModel
{
    public int ShipmentId { get; set; }
    public string ShipmentCode { get; set; } = "";
    public string SubtitleText { get; set; } = "";
    public string IconClass { get; set; } = "bi-box-seam";
    public string AvatarPath { get; set; } = "/images/stat-cards/ref-blue/ops-transport.webp";
    public decimal LoadedMt { get; set; }
    public decimal TotalMt { get; set; }
    public decimal ProgressPercent { get; set; }
    public string StatusText { get; set; } = "";
    public string StatusClass { get; set; } = "is-inactive";
    public string ProgressTone { get; set; } = "is-normal";
}

// یک ردیف «ظرفیت مخزن»: موجودی جاری در برابر ظرفیت اسمی مخزن.
public class DashboardTankCapacityViewModel
{
    public int TankId { get; set; }
    public string TankName { get; set; } = "";
    public string ProductName { get; set; } = "";
    public decimal StockMt { get; set; }
    public decimal CapacityMt { get; set; }
    public decimal FillPercent { get; set; }
    public string ProgressTone { get; set; } = "is-normal";
}

public class DashboardOrderPanelViewModel
{
    public string Title { get; set; } = "";
    public string TokenLabel { get; set; } = "";
    public string TokenIcon { get; set; } = "bi bi-box-seam";
    public string TokenClass { get; set; } = "";
    public List<DashboardOrderRowViewModel> Rows { get; set; } = new();
}

public class DashboardOrderRowViewModel
{
    public string Reference { get; set; } = "";
    public string Price { get; set; } = "";
    public string Amount { get; set; } = "";
    public string Total { get; set; } = "";
}

public class ErrorViewModel
{
    public string? RequestId { get; set; }
    public bool ShowRequestId => !string.IsNullOrEmpty(RequestId);
}
