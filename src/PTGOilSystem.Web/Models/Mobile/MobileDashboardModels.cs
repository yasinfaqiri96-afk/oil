namespace PTGOilSystem.Web.Models.Mobile;

/// <summary>
/// پاسخ <c>GET /api/mobile/v1/dashboard</c>. بخشی که کاربر به گزارش مرجعش دسترسی ندارد null است.
/// همهٔ مقادیر از سرویس‌های مرجع موجود می‌آیند؛ موبایل هیچ عددی را محاسبه نمی‌کند.
/// </summary>
public sealed class MobileDashboardResponse
{
    public DateTime AsOfUtc { get; init; }

    /// <summary>تاریخ کاری کابل (yyyy-MM-dd) از IAfghanistanBusinessClock.</summary>
    public string BusinessDate { get; init; } = "";

    public string Currency { get; init; } = "USD";

    public MobileInventoryKpi? Inventory { get; init; }
    public MobileGoodsInTransitKpi? GoodsInTransit { get; init; }
    public MobileTodaySalesKpi? TodaySales { get; init; }
    public MobileReceivablesKpi? Receivables { get; init; }
    public MobileCashPositionKpi? CashPosition { get; init; }
    public MobileTodayProfitKpi? TodayProfit { get; init; }

    public IReadOnlyList<MobileActiveShipment> ActiveShipments { get; init; } = [];
    public IReadOnlyList<MobileDashboardAlert> Alerts { get; init; } = [];
    public IReadOnlyList<MobilePendingAction> PendingActions { get; init; } = [];
}

public sealed class MobileInventoryKpi
{
    /// <summary>IStockService.GetTotalFreeQuantityMtAsync — همهٔ حرکت‌های موجودی.</summary>
    public decimal TotalMt { get; init; }

    /// <summary>همان شمارش مخزن کم‌موجودی داشبورد وب.</summary>
    public int LowStockTankCount { get; init; }
}

public sealed class MobileGoodsInTransitKpi
{
    public decimal TotalMt { get; init; }
    public int LoadCount { get; init; }
    public int FromOriginCount { get; init; }
    public int InternalTransferCount { get; init; }
    public int CustomerDeliveryCount { get; init; }
    public int DelayedCount { get; init; }
}

public sealed class MobileTodaySalesKpi
{
    public decimal AmountUsd { get; init; }
    public int Count { get; init; }
}

public sealed class MobileReceivablesKpi
{
    /// <summary>جمع ماندهٔ مثبتِ مشتریان (مشتری بدهکار) از PartyBalanceReadService.</summary>
    public decimal TotalUsd { get; init; }

    public int CustomerCount { get; init; }

    /// <summary>پیش‌دریافت مشتریان (ماندهٔ منفی) — جدا نشان داده می‌شود و از طلبات کم نمی‌شود.</summary>
    public decimal CustomerAdvancesUsd { get; init; }
}

public sealed class MobileCashPositionKpi
{
    /// <summary>همان «موجودی حساب‌های نقدی» صفحهٔ روزنامچه (معادل دالری ثبت‌شدهٔ هر سند).</summary>
    public decimal TotalUsd { get; init; }

    public int AccountCount { get; init; }
}

public sealed class MobileTodayProfitKpi
{
    public decimal RevenueUsd { get; init; }
    public decimal CostOfGoodsSoldUsd { get; init; }
    public decimal GrossProfitUsd { get; init; }
    public int SaleCount { get; init; }
    public int CostedSaleCount { get; init; }
    public int UncostedSaleCount { get; init; }

    /// <summary>verified | estimated | legacy | needsReview — از ProfitAndLossService.</summary>
    public string Confidence { get; init; } = "";
}

public sealed class MobileActiveShipment
{
    public int Id { get; init; }
    public string Code { get; init; } = "";
    public string Subtitle { get; init; } = "";
    public decimal LoadedMt { get; init; }
    public decimal TotalMt { get; init; }
    public decimal ProgressPercent { get; init; }
    public string StatusText { get; init; } = "";
}

public sealed class MobileDashboardAlert
{
    public string Code { get; init; } = "";

    /// <summary>info | warning | critical</summary>
    public string Severity { get; init; } = "info";

    public string Title { get; init; } = "";
    public string Message { get; init; } = "";
    public string? Reference { get; init; }
}

public sealed class MobilePendingAction
{
    public string Code { get; init; } = "";
    public string Title { get; init; } = "";
    public int Count { get; init; }
}
