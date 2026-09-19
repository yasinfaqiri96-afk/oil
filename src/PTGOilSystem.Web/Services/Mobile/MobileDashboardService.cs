using System.Globalization;
using System.Security.Claims;
using PTGOilSystem.Web.Models;
using PTGOilSystem.Web.Models.Mobile;
using PTGOilSystem.Web.Models.PartyStatements;
using PTGOilSystem.Web.Models.Reports;
using PTGOilSystem.Web.Security;
using PTGOilSystem.Web.Services.PartyStatements;
using PTGOilSystem.Web.Services.Reporting;
using PTGOilSystem.Web.Services.Time;

namespace PTGOilSystem.Web.Services.Mobile;

public interface IMobileDashboardService
{
    Task<MobileDashboardResponse> BuildAsync(ClaimsPrincipal user, CancellationToken ct = default);
}

/// <summary>
/// داشبورد موبایل — فقط هماهنگ‌کننده و نگاشت. هیچ فرمول موجودی، مانده، بهای تمام‌شده یا سود
/// اینجا ساخته نمی‌شود؛ هر عدد از مرجع رسمی خودش می‌آید (docs/REPORTING-SOURCES-OF-TRUTH.md):
/// <list type="bullet">
///   <item>موجودی: <see cref="IStockService"/></item>
///   <item>بار در مسیر: <see cref="IGoodsInTransitReader"/> (همان راپور بارهای در مسیر)</item>
///   <item>فروش امروز، محموله‌ها، هشدارها و شمارنده‌ها: <see cref="IDashboardService"/> (همان داشبورد وب)</item>
///   <item>طلبات: <see cref="IPartyBalanceReadService"/></item>
///   <item>مفاد امروز: <see cref="IProfitAndLossService"/></item>
///   <item>وضعیت نقدی: <see cref="ICashPositionReader"/> (همان صفحهٔ روزنامچه)</item>
/// </list>
/// بخش‌های مالی فقط برای کاربری ساخته می‌شوند که در وب به صفحهٔ مرجعِ همان عدد دسترسی دارد.
/// </summary>
public sealed class MobileDashboardService(
    IDashboardService dashboard,
    IStockService stock,
    IGoodsInTransitReader goodsInTransit,
    IPartyBalanceReadService partyBalances,
    IProfitAndLossService profitAndLoss,
    ICashPositionReader cashPosition,
    IAfghanistanBusinessClock clock,
    TimeProvider time) : IMobileDashboardService
{
    public async Task<MobileDashboardResponse> BuildAsync(ClaimsPrincipal user, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(user);

        var today = clock.Today.Date;
        // گزارش بارهای در مسیر، طلبات و سود و زیان در وب زیر «گزارشات» هستند؛ موجودی نقدی زیر «روزنامچه».
        var canReports = RoleAccessRules.CanAccessNavigation(user, RoleNavigationKeys.Reports);
        var canPayments = RoleAccessRules.CanAccessNavigation(user, RoleNavigationKeys.Payments);

        // یک DbContext مشترک است؛ کوئری‌ها عمداً پشت‌سرهم اجرا می‌شوند.
        var vm = await dashboard.BuildDashboardAsync(ct);
        var totalStockMt = await stock.GetTotalFreeQuantityMtAsync(ct: ct);
        var goodsInTransitKpi = canReports ? await BuildGoodsInTransitAsync(ct) : null;
        var receivablesKpi = canReports ? await BuildReceivablesAsync(ct) : null;
        var profitKpi = canReports ? await BuildTodayProfitAsync(today, ct) : null;
        var cashKpi = canPayments ? await BuildCashPositionAsync(ct) : null;

        return new MobileDashboardResponse
        {
            AsOfUtc = time.GetUtcNow().UtcDateTime,
            BusinessDate = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            Inventory = new MobileInventoryKpi
            {
                TotalMt = totalStockMt,
                LowStockTankCount = vm.LowStockTankCount
            },
            GoodsInTransit = goodsInTransitKpi,
            TodaySales = new MobileTodaySalesKpi
            {
                AmountUsd = vm.TodaySalesUsd,
                Count = vm.TodaySalesCount
            },
            Receivables = receivablesKpi,
            CashPosition = cashKpi,
            TodayProfit = profitKpi,
            ActiveShipments = vm.ActiveShipmentProgress
                .Select(shipment => new MobileActiveShipment
                {
                    Id = shipment.ShipmentId,
                    Code = shipment.ShipmentCode,
                    Subtitle = shipment.SubtitleText,
                    LoadedMt = shipment.LoadedMt,
                    TotalMt = shipment.TotalMt,
                    ProgressPercent = shipment.ProgressPercent,
                    StatusText = shipment.StatusText
                })
                .ToList(),
            Alerts = MapAlerts("low_stock", vm.LowStockAlerts)
                .Concat(MapAlerts("contract_ending_soon", vm.ContractsEndingSoonAlerts))
                .Concat(MapAlerts("shipment_without_sales", vm.ShipmentsWithoutSalesAlerts))
                .Concat(MapAlerts("shipment_without_expenses", vm.ShipmentsWithoutExpensesAlerts))
                .ToList(),
            PendingActions = BuildPendingActions(vm)
        };
    }

    private async Task<MobileGoodsInTransitKpi> BuildGoodsInTransitAsync(CancellationToken ct)
    {
        var snapshot = await goodsInTransit.ReadAsync(new GoodsInTransitFilterViewModel(), ct);
        return new MobileGoodsInTransitKpi
        {
            TotalMt = snapshot.Totals.TotalQuantityMt,
            LoadCount = snapshot.Totals.RowCount,
            FromOriginCount = snapshot.Totals.FromOriginCount,
            InternalTransferCount = snapshot.Totals.InternalTransferCount,
            CustomerDeliveryCount = snapshot.Totals.CustomerDeliveryCount,
            DelayedCount = snapshot.Totals.DelayedCount
        };
    }

    private async Task<MobileReceivablesKpi> BuildReceivablesAsync(CancellationToken ct)
    {
        var customers = (await partyBalances.GetBalancesAsync(new ManagementReportFilterViewModel(), ct))
            .ExternalPartiesOnly()
            .Where(row => row.PartyType == PartyStatementPartyType.Customer)
            .ToList();

        // حساب طرف‌حساب: مانده = افتتاحیه + فروش/تحویل − دریافت؛ پس مثبت یعنی مشتری بدهکار است.
        var debtors = customers.Where(row => row.ClosingBalanceUsd > 0m).ToList();
        return new MobileReceivablesKpi
        {
            TotalUsd = debtors.Sum(row => row.ClosingBalanceUsd),
            CustomerCount = debtors.Count,
            CustomerAdvancesUsd = customers.Where(row => row.ClosingBalanceUsd < 0m).Sum(row => -row.ClosingBalanceUsd)
        };
    }

    private async Task<MobileTodayProfitKpi> BuildTodayProfitAsync(DateTime today, CancellationToken ct)
    {
        var pnl = await profitAndLoss.BuildCompanyAsync(
            new ManagementReportFilterViewModel { FromDate = today, ToDate = today },
            ct);
        var sales = pnl.Sales;

        return new MobileTodayProfitKpi
        {
            RevenueUsd = sales.RevenueUsd,
            CostOfGoodsSoldUsd = sales.CostOfGoodsSoldUsd,
            GrossProfitUsd = sales.GrossProfitUsd,
            SaleCount = sales.SaleCount,
            CostedSaleCount = sales.CostedSaleCount,
            UncostedSaleCount = sales.UncostedSaleCount,
            Confidence = ToWire(sales.Confidence)
        };
    }

    private async Task<MobileCashPositionKpi> BuildCashPositionAsync(CancellationToken ct)
    {
        var totals = await cashPosition.ReadAccountTotalsAsync(ct);
        return new MobileCashPositionKpi
        {
            TotalUsd = CashPositionReader.TotalBalanceUsd(totals),
            AccountCount = totals.Count
        };
    }

    private static IEnumerable<MobileDashboardAlert> MapAlerts(string code, IEnumerable<DashboardAlertViewModel> alerts)
        => alerts.Select(alert => new MobileDashboardAlert
        {
            Code = code,
            Severity = NormalizeSeverity(alert.Severity),
            Title = alert.Title,
            Message = alert.Message,
            Reference = alert.Reference
        });

    private static IReadOnlyList<MobilePendingAction> BuildPendingActions(DashboardViewModel vm) =>
    [
        new() { Code = "loadings_without_receipt", Title = "بارگیری بدون رسید", Count = vm.LoadingsWithoutReceiptCount },
        new() { Code = "receipts_without_allocation", Title = "رسید بدون تخصیص", Count = vm.ReceiptsWithoutAllocationCount },
        new() { Code = "sales_without_payment", Title = "فروش در انتظار پرداخت", Count = vm.SalesWithoutPaymentCount },
        new() { Code = "contracts_without_final_price", Title = "قرارداد بدون قیمت نهایی", Count = vm.ContractsWithoutFinalPriceCount },
        new() { Code = "loadings_without_customs", Title = "بارگیری بدون اسناد گمرکی", Count = vm.LoadingsWithoutCustomsCount },
        new() { Code = "shortage", Title = "کسری", Count = vm.ShortageCount },
        new() { Code = "excess_shortage", Title = "کسری بیش از حد مجاز", Count = vm.ExcessShortageCount },
        new() { Code = "sarraf_rate_difference", Title = "اختلاف نرخ صراف", Count = vm.SarrafRateDiffCount }
    ];

    internal static string NormalizeSeverity(string? severity) => severity?.Trim().ToLowerInvariant() switch
    {
        "danger" or "critical" => "critical",
        "warning" => "warning",
        _ => "info"
    };

    internal static string ToWire(PnlConfidence confidence) => confidence switch
    {
        PnlConfidence.Verified => "verified",
        PnlConfidence.Estimated => "estimated",
        PnlConfidence.Legacy => "legacy",
        _ => "needsReview"
    };
}
