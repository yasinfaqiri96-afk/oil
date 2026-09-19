using System.Reflection;
using System.Security.Claims;
using PTGOilSystem.Web.Models;
using PTGOilSystem.Web.Models.PartyStatements;
using PTGOilSystem.Web.Models.Reports;
using PTGOilSystem.Web.Security;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Mobile;
using PTGOilSystem.Web.Services.PartyStatements;
using PTGOilSystem.Web.Services.Reporting;
using PTGOilSystem.Web.Services.Time;
using Xunit;

namespace PTGOilSystem.Web.Tests.MobileApi;

/// <summary>
/// داشبورد موبایل فقط نگاشت است: هر عدد باید دقیقاً از سرویس مرجع خودش بیاید، و بخش مالی
/// برای کاربری که در وب به گزارش مرجع دسترسی ندارد اصلاً خوانده نشود.
/// </summary>
public sealed class MobileDashboardServiceTests
{
    private static readonly DateTime Today = new(2026, 9, 15);

    [Fact]
    public async Task Maps_Every_Kpi_From_Its_Reference_Service()
    {
        ManagementReportFilterViewModel? pnlFilter = null;
        var service = CreateService(onPnl: filter => pnlFilter = filter);

        var result = await service.BuildAsync(Principal(AuthRoles.Admin));

        Assert.Equal("2026-09-15", result.BusinessDate);
        Assert.Equal(18420.5m, result.Inventory!.TotalMt);
        Assert.Equal(2, result.Inventory.LowStockTankCount);
        Assert.Equal(1500m, result.TodaySales!.AmountUsd);
        Assert.Equal(3, result.TodaySales.Count);

        Assert.Equal(321.5m, result.GoodsInTransit!.TotalMt);
        Assert.Equal(5, result.GoodsInTransit.LoadCount);
        Assert.Equal(1, result.GoodsInTransit.DelayedCount);

        // Only external customers; positive balance = customer owes the company; advances are separate.
        Assert.Equal(700m, result.Receivables!.TotalUsd);
        Assert.Equal(1, result.Receivables.CustomerCount);
        Assert.Equal(200m, result.Receivables.CustomerAdvancesUsd);

        Assert.Equal(800m, result.CashPosition!.TotalUsd);
        Assert.Equal(2, result.CashPosition.AccountCount);

        Assert.Equal(300m, result.TodayProfit!.GrossProfitUsd);
        Assert.Equal(1, result.TodayProfit.UncostedSaleCount);
        Assert.Equal("needsReview", result.TodayProfit.Confidence);
        Assert.Equal(Today, pnlFilter!.FromDate);
        Assert.Equal(Today, pnlFilter.ToDate);

        var shipment = Assert.Single(result.ActiveShipments);
        Assert.Equal("SH-9", shipment.Code);
        Assert.Equal(50m, shipment.ProgressPercent);

        var alert = Assert.Single(result.Alerts);
        Assert.Equal("low_stock", alert.Code);
        Assert.Equal("critical", alert.Severity);

        Assert.Equal(4, Assert.Single(result.PendingActions, a => a.Code == "loadings_without_receipt").Count);
    }

    [Fact]
    public async Task Financial_Sections_Are_Not_Read_Without_Web_Access_To_Their_Reports()
    {
        // Reference readers for finance are absent: any call would throw NotSupportedException.
        var service = CreateService(includeFinancialReaders: false);
        var user = Principal("DashboardOnly", RoleNavigationKeys.Dashboard);

        var result = await service.BuildAsync(user);

        Assert.NotNull(result.Inventory);
        Assert.NotNull(result.TodaySales);
        Assert.Null(result.GoodsInTransit);
        Assert.Null(result.Receivables);
        Assert.Null(result.TodayProfit);
        Assert.Null(result.CashPosition);
    }

    private static ClaimsPrincipal Principal(string role, params string[] navigation)
    {
        var claims = new List<Claim> { new(ClaimTypes.Role, role) };
        claims.AddRange(navigation.Select(key => new Claim(AppClaimTypes.AllowedNavigation, key)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer"));
    }

    private static MobileDashboardService CreateService(
        bool includeFinancialReaders = true,
        Action<ManagementReportFilterViewModel>? onPnl = null)
    {
        var vm = new DashboardViewModel
        {
            TodaySalesUsd = 1500m,
            TodaySalesCount = 3,
            LowStockTankCount = 2,
            LoadingsWithoutReceiptCount = 4,
            ActiveShipmentProgress =
            [
                new DashboardShipmentProgressViewModel
                {
                    ShipmentId = 9, ShipmentCode = "SH-9", SubtitleText = "Diesel",
                    LoadedMt = 50m, TotalMt = 100m, ProgressPercent = 50m, StatusText = "در مسیر"
                }
            ],
            LowStockAlerts =
            [
                new DashboardAlertViewModel { Title = "موجودی کم", Message = "T-07", Severity = "danger", Reference = "C-1" }
            ]
        };

        var dashboard = InterfaceFake<IDashboardService>.Create(
            ("BuildDashboardAsync", _ => Task.FromResult(vm)));
        var stock = InterfaceFake<IStockService>.Create(
            ("GetTotalFreeQuantityMtAsync", _ => Task.FromResult(18420.5m)));
        var clock = InterfaceFake<IAfghanistanBusinessClock>.Create(("get_Today", _ => Today));

        var goodsInTransit = includeFinancialReaders
            ? InterfaceFake<IGoodsInTransitReader>.Create(("ReadAsync", _ => Task.FromResult(new GoodsInTransitSnapshot(
                [],
                new GoodsInTransitTotalsViewModel
                {
                    RowCount = 5, TotalQuantityMt = 321.5m, DelayedCount = 1,
                    FromOriginCount = 2, InternalTransferCount = 2, CustomerDeliveryCount = 1
                }))))
            : InterfaceFake<IGoodsInTransitReader>.Create();

        IReadOnlyList<PartyBalanceSnapshot> balances =
        [
            Balance(PartyStatementPartyType.Customer, 1, 700m),
            Balance(PartyStatementPartyType.Customer, 2, -200m),
            Balance(PartyStatementPartyType.Supplier, 3, 999m),
            Balance(PartyStatementPartyType.Company, 4, 5000m)
        ];
        var partyBalances = includeFinancialReaders
            ? InterfaceFake<IPartyBalanceReadService>.Create(("GetBalancesAsync", _ => Task.FromResult(balances)))
            : InterfaceFake<IPartyBalanceReadService>.Create();

        var profitAndLoss = includeFinancialReaders
            ? InterfaceFake<IProfitAndLossService>.Create(("BuildCompanyAsync", args =>
            {
                onPnl?.Invoke((ManagementReportFilterViewModel)args![0]!);
                return Task.FromResult(new CompanyPnlSnapshot(
                    new SalesPnlSnapshot(1500m, 1200m, 3, 2, 1, PnlConfidence.NeedsReview), 50m, 0m, 0m));
            }))
            : InterfaceFake<IProfitAndLossService>.Create();

        IReadOnlyList<CashAccountActivityTotals> cash =
        [
            new CashAccountActivityTotals(1, 0m, 0m, 1000m, 250m),
            new CashAccountActivityTotals(2, 0m, 0m, 50m, 0m)
        ];
        var cashPosition = includeFinancialReaders
            ? InterfaceFake<ICashPositionReader>.Create(("ReadAccountTotalsAsync", _ => Task.FromResult(cash)))
            : InterfaceFake<ICashPositionReader>.Create();

        return new MobileDashboardService(
            dashboard, stock, goodsInTransit, partyBalances, profitAndLoss, cashPosition, clock, TimeProvider.System);
    }

    private static PartyBalanceSnapshot Balance(PartyStatementPartyType type, int id, decimal closing)
        => new(type, id, $"Party {id}", 0m, 0m, 0m, 0m, closing, null, "", null);
}

/// <summary>Minimal interface fake: unconfigured members throw, so unexpected reads fail the test.</summary>
public class InterfaceFake<T> : DispatchProxy where T : class
{
    private readonly Dictionary<string, Func<object?[]?, object?>> _handlers = new(StringComparer.Ordinal);

    public static T Create(params (string Member, Func<object?[]?, object?> Handler)[] handlers)
    {
        var proxy = Create<T, InterfaceFake<T>>();
        var fake = (InterfaceFake<T>)(object)proxy;
        foreach (var (member, handler) in handlers)
        {
            fake._handlers[member] = handler;
        }

        return proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        => targetMethod is not null && _handlers.TryGetValue(targetMethod.Name, out var handler)
            ? handler(args)
            : throw new NotSupportedException($"{typeof(T).Name}.{targetMethod?.Name} was not expected in this test.");
}
