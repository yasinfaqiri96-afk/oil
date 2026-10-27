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
    public decimal TerminalStockMt { get; set; }
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

    // --- کارت‌های بالای داشبورد؛ null یعنی کاربر به گزارش مرجعِ همان عدد دسترسی ندارد ---
    /// <summary>جمع بارهای در مسیر — همان راپور «بارهای در مسیر» (IGoodsInTransitReader).</summary>
    public decimal? GoodsInTransitMt { get; set; }
    /// <summary>کل طلبات — همان TotalReceivableUsd گزارش «طلبات و بدهی‌ها».</summary>
    public decimal? TotalReceivableUsd { get; set; }
    /// <summary>کل بدهی‌ها — همان TotalPayableUsd گزارش «طلبات و بدهی‌ها».</summary>
    public decimal? TotalPayableUsd { get; set; }

    /// <summary>ترکیب فروش: مقدار فروش هر محصول در همان بازهٔ نمودار روند.</summary>
    public List<DashboardSalesMixItemViewModel> SalesMix { get; set; } = new();

    // ابزارک‌های «بدهکاران» و «بستانکاران»: همان ردیف‌های گزارش «طلبات و بدهی‌ها» که کارت‌های بالا هم می‌خوانند.
    public List<DashboardWidgetRowViewModel> TopDebtors { get; set; } = new();
    public int DebtorCount { get; set; }
    public List<DashboardWidgetRowViewModel> TopCreditors { get; set; } = new();
    public int CreditorCount { get; set; }
}

// یک ردیف ابزارک اختیاری داشبورد (بدهکار، صندوق، نرخ ارز، مصرف). مقدار از مرجع رسمی خودش می‌آید.
public class DashboardWidgetRowViewModel
{
    public string Title { get; set; } = "";
    public string Subtitle { get; set; } = "";
    public decimal Value { get; set; }
    public int Decimals { get; set; }
    public string Unit { get; set; } = "";
    public string? Controller { get; set; }
    public string? Action { get; set; }
    public int? RouteId { get; set; }
}

// ابزارکی که جدا و فقط پس از انتخاب کاربر بارگذاری می‌شود.
public class DashboardLazyWidgetViewModel
{
    public string Kind { get; set; } = "";
    public List<DashboardWidgetRowViewModel> Rows { get; set; } = new();
    public DashboardProfitWidgetViewModel? Profit { get; set; }
}

// سود و زیان امروز — همان موتور «عملکرد دوره» که مفاد امروزِ موبایل می‌خواند.
public class DashboardProfitWidgetViewModel
{
    public DateTime BusinessDate { get; set; }
    public decimal RevenueUsd { get; set; }
    public decimal CostOfSalesUsd { get; set; }
    public decimal PeriodExpensesUsd { get; set; }
    public decimal NetProfitUsd { get; set; }
    public int SaleCount { get; set; }
    public int UncostedSaleCount { get; set; }
}

public class DashboardSalesMixItemViewModel
{
    public int ProductId { get; set; }
    public string ProductName { get; set; } = "";
    public decimal QuantityMt { get; set; }
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
