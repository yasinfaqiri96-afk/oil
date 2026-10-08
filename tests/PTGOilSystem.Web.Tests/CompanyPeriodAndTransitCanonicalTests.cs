using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Controllers;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.Reports;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Reporting;
using PTGOilSystem.Web.Services.Time;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// یک منبع حقیقت برای «عملکرد دوره» و «بار در مسیر»: وضعیت مالی شرکت، بیلانس کلی شرکت و راپور
/// بارهای در مسیر برای یک تاریخ نباید دو عدد متفاوت بدهند.
/// </summary>
public class CompanyPeriodAndTransitCanonicalTests
{
    private const int PurchaseId = 5;
    private static readonly DateTime From = new(2026, 4, 1);
    private static readonly DateTime To = new(2026, 4, 30);

    // ── عملکرد دوره ────────────────────────────────────────────────────────

    [Fact]
    public async Task Company_Overview_And_Company_Balance_Show_The_Same_Period_Performance()
    {
        await using var db = NewDb();
        Seed(db);
        // کرایهٔ بی‌سند ۱٬۰۰۰ ⇒ ۱۰ دالر بر تن روی بهای واحد؛ گدام بی‌سند ۲۰۰ ⇒ مصرف دوره.
        var loading = Loading(1, 100m);
        loading.TransportExpenseUsd = 1_000m;
        loading.WarehouseExpenseUsd = 200m;
        db.LoadingRegisters.Add(loading);
        db.SalesTransactions.Add(Sale(100, 10m, 6_000m, new DateTime(2026, 4, 20)));
        db.InventoryMovements.Add(StockOut(1, saleId: 100, 10m, new DateTime(2026, 4, 20)));
        db.ExpenseTransactions.Add(Expense(1, 300m, new DateTime(2026, 4, 12)));
        await db.SaveChangesAsync();

        var engine = await new ProfitAndLossService(db).BuildCompanyPeriodAsync(
            new ManagementReportFilterViewModel { FromDate = From, ToDate = To }, To);
        Assert.Equal(6_000m, engine.SalesRevenueUsd);
        Assert.Equal(5_100m, engine.CostOfSalesUsd);
        Assert.Equal(500m, engine.PeriodExpensesUsd);
        Assert.Equal(400m, engine.NetProfitUsd);
        Assert.Equal(PnlConfidence.Estimated, engine.Confidence);

        var balance = await new CompanyBalanceReportService(db, new ProfitAndLossService(db), new StockService(db), new PricingService(db))
            .BuildAsync(new CompanyBalanceReportRequest(From, To, "USD", new DateTime(2026, 5, 1)), []);
        var view = Assert.IsType<ViewResult>(await new ReportsController(db, clock: new FixedClock(To))
            .CompanyOverview(new ManagementReportFilterViewModel { FromDate = From, ToDate = To }));
        var overview = Assert.IsType<CompanyFinancialOverviewViewModel>(view.Model);

        Assert.Equal(engine.SalesRevenueUsd, balance.SalesRevenueUsd);
        Assert.Equal(engine.SalesRevenueUsd, overview.RevenueUsd);
        Assert.Equal(engine.CostOfSalesUsd, balance.CostOfSalesUsd);
        Assert.Equal(engine.CostOfSalesUsd, overview.PurchaseCostUsd);
        Assert.Equal(engine.PeriodExpensesUsd, balance.PeriodExpensesUsd);
        Assert.Equal(engine.PeriodExpensesUsd, overview.ExpenseUsd);
        Assert.Equal(engine.NetProfitUsd, balance.NetProfitLoss);
        Assert.Equal(engine.NetProfitUsd, overview.NetProfitUsd);
        Assert.True(overview.IsProfitPublishable);
        Assert.Equal(PnlConfidence.Estimated, overview.PnlConfidence);
    }

    [Fact]
    public async Task Sale_Without_Contract_Cost_Or_Pool_Cost_Stays_Uncosted_Everywhere()
    {
        await using var db = NewDb();
        Seed(db);
        db.SalesTransactions.Add(Sale(100, 10m, 6_000m, new DateTime(2026, 4, 20)));
        await db.SaveChangesAsync();

        var engine = await new ProfitAndLossService(db).BuildCompanyPeriodAsync(
            new ManagementReportFilterViewModel { FromDate = From, ToDate = To }, To);
        var view = Assert.IsType<ViewResult>(await new ReportsController(db, clock: new FixedClock(To))
            .CompanyOverview(new ManagementReportFilterViewModel { FromDate = From, ToDate = To }));
        var overview = Assert.IsType<CompanyFinancialOverviewViewModel>(view.Model);

        Assert.Equal(0m, engine.CostOfSalesUsd);
        Assert.Equal(1, engine.UncostedSaleCount);
        Assert.Equal(PnlConfidence.NeedsReview, engine.Confidence);
        Assert.False(overview.IsProfitPublishable);
    }

    // ── بار در مسیر ────────────────────────────────────────────────────────

    [Fact]
    public async Task Partial_Receipt_Leaves_Only_The_Remaining_Quantity_In_Transit()
    {
        await using var db = NewDb();
        Seed(db);
        db.LoadingRegisters.Add(Loading(1, 100m));
        db.LoadingReceipts.Add(Receipt(1, 1, 70m, new DateTime(2026, 4, 10)));
        await db.SaveChangesAsync();

        var row = Assert.Single(await Transit(db, To));
        Assert.Equal(30m, row.RemainingMt);
        Assert.Equal(70m, row.ReceivedMt);
    }

    [Fact]
    public async Task Cancelled_Loading_Is_Never_In_Transit()
    {
        await using var db = NewDb();
        Seed(db);
        var cancelled = Loading(1, 100m);
        cancelled.IsCancelled = true;
        db.LoadingRegisters.Add(cancelled);
        await db.SaveChangesAsync();

        Assert.Empty(await Transit(db, To));
        var report = await ReportAsync(db, To);
        Assert.Equal(0, report.Totals.RowCount);
    }

    [Fact]
    public async Task Loading_After_The_As_Of_Date_Is_Not_In_Transit_Yet()
    {
        await using var db = NewDb();
        Seed(db);
        db.LoadingRegisters.Add(Loading(1, 100m, new DateTime(2026, 5, 3)));
        await db.SaveChangesAsync();

        Assert.Empty(await Transit(db, To));
        Assert.Single(await Transit(db, new DateTime(2026, 5, 3)));
    }

    [Fact]
    public async Task Receipt_After_The_As_Of_Date_Keeps_The_Load_In_Transit_On_That_Date()
    {
        await using var db = NewDb();
        Seed(db);
        db.LoadingRegisters.Add(Loading(1, 100m));
        db.LoadingReceipts.Add(Receipt(1, 1, 100m, new DateTime(2026, 5, 3)));
        await db.SaveChangesAsync();

        Assert.Equal(100m, Assert.Single(await Transit(db, To)).RemainingMt);
        Assert.Empty(await Transit(db, new DateTime(2026, 5, 3)));
        // راپور با «تا تاریخ» همان تاریخ گزارش را می‌سنجد، نه امروز.
        Assert.Equal(100m, (await ReportAsync(db, To)).Totals.TotalQuantityMt);
    }

    [Fact]
    public async Task Cancelled_Receipt_And_Its_Shortage_Do_Not_Reduce_Transit()
    {
        await using var db = NewDb();
        Seed(db);
        db.LoadingRegisters.Add(Loading(1, 100m));
        var receipt = Receipt(1, 1, 60m, new DateTime(2026, 4, 10));
        receipt.IsCancelled = true;
        db.LoadingReceipts.Add(receipt);
        db.LossEvents.Add(Shortage(1, loadingId: null, receiptId: 1, 5m, new DateTime(2026, 4, 10)));
        await db.SaveChangesAsync();

        Assert.Equal(100m, Assert.Single(await Transit(db, To)).RemainingMt);
    }

    [Fact]
    public async Task Transport_Allocation_And_Receipt_Shortage_Reduce_Transit_Once()
    {
        await using var db = NewDb();
        Seed(db);
        db.LoadingRegisters.Add(Loading(1, 100m));
        db.LoadingReceipts.Add(Receipt(1, 1, 50m, new DateTime(2026, 4, 10)));
        db.LossEvents.AddRange(
            Shortage(1, loadingId: 1, receiptId: null, 4m, new DateTime(2026, 4, 10)),
            Shortage(2, loadingId: 1, receiptId: null, 9m, new DateTime(2026, 4, 11), cancelled: true));
        db.InventoryTransportLegs.AddRange(
            Leg(1, 20m, new DateTime(2026, 4, 12), InventoryTransportLegStatus.InTransit),
            Leg(2, 10m, new DateTime(2026, 5, 5), InventoryTransportLegStatus.InTransit));
        db.InventoryTransportLegAllocations.AddRange(
            new InventoryTransportLegAllocation { Id = 1, InventoryTransportLegId = 1, SourcePurchaseContractId = PurchaseId, SourceLoadingRegisterId = 1, QuantityMt = 20m },
            new InventoryTransportLegAllocation { Id = 2, InventoryTransportLegId = 2, SourcePurchaseContractId = PurchaseId, SourceLoadingRegisterId = 1, QuantityMt = 10m });
        await db.SaveChangesAsync();

        var rows = await Transit(db, To);
        // ۱۰۰ − ۵۰ رسید − ۴ کسری − ۲۰ حمل داخلی تا تاریخ = ۲۶؛ حملِ بعد از تاریخ و کسری لغوشده اثری ندارند.
        Assert.Equal(26m, rows.Single(r => r.Kind == GoodsInTransitKind.FromOrigin).RemainingMt);
        Assert.Equal(20m, rows.Single(r => r.Kind == GoodsInTransitKind.InternalTransfer).RemainingMt);
    }

    [Fact]
    public async Task Over_Received_Load_Never_Becomes_Negative_Transit()
    {
        await using var db = NewDb();
        Seed(db);
        db.LoadingRegisters.Add(Loading(1, 100m));
        db.LoadingReceipts.Add(Receipt(1, 1, 120m, new DateTime(2026, 4, 10)));
        await db.SaveChangesAsync();

        Assert.Empty(await Transit(db, To));
    }

    [Fact]
    public async Task Old_Load_Without_Any_Receipt_Stays_In_Transit()
    {
        await using var db = NewDb();
        Seed(db);
        db.LoadingRegisters.Add(Loading(1, 100m, new DateTime(2025, 10, 1)));
        await db.SaveChangesAsync();

        Assert.Equal(100m, Assert.Single(await Transit(db, To)).RemainingMt);
        var report = await ReportAsync(db, To);
        Assert.Equal(211, Assert.Single(report.Rows).DaysOnRoad);
    }

    [Fact]
    public async Task Received_Leg_Is_In_Transit_Only_Before_Its_Receipt()
    {
        await using var db = NewDb();
        Seed(db);
        db.InventoryTransportLegs.AddRange(
            // «رسیده» بدون هیچ رسیدِ ثبت‌شده ⇒ در هیچ تاریخی بار در مسیر حساب نمی‌شود.
            Leg(1, 25m, new DateTime(2026, 4, 2), InventoryTransportLegStatus.Received),
            Leg(2, 40m, new DateTime(2026, 4, 2), InventoryTransportLegStatus.Received));
        db.InventoryTransportReceipts.Add(new InventoryTransportReceipt
        {
            Id = 1,
            InventoryTransportLegId = 2,
            ReceiptDate = new DateTime(2026, 5, 4),
            ReceivedQuantityMt = 39m,
            ShortageQuantityMt = 1m
        });
        await db.SaveChangesAsync();

        var row = Assert.Single(await Transit(db, To));
        Assert.Equal(2, row.SourceId);
        Assert.Equal(40m, row.RemainingMt);
        Assert.Empty(await Transit(db, new DateTime(2026, 5, 4)));
    }

    [Fact]
    public async Task Report_And_Company_Balance_Read_The_Same_Transit_Quantity()
    {
        await using var db = NewDb();
        Seed(db);
        db.LoadingRegisters.AddRange(Loading(1, 100m), Loading(2, 30m));
        db.LoadingReceipts.Add(Receipt(1, 1, 70m, new DateTime(2026, 4, 10)));
        db.SalesTransactions.Add(Sale(100, 8m, 4_000m, new DateTime(2026, 4, 25)));
        db.TruckDispatches.AddRange(
            new TruckDispatch { Id = 1, ContractId = PurchaseId, ProductId = 1, TruckId = 1, DispatchDate = new DateTime(2026, 4, 20), LoadedQuantityMt = 12m, Status = DispatchStatus.InTransit },
            new TruckDispatch { Id = 2, ContractId = PurchaseId, ProductId = 1, TruckId = 1, DispatchDate = new DateTime(2026, 4, 25), LoadedQuantityMt = 8m, Status = DispatchStatus.Loaded, SalesTransactionId = 100 });
        await db.SaveChangesAsync();

        var canonical = await Transit(db, To);
        Assert.Equal(2, canonical.Count(r => r.Kind == GoodsInTransitKind.CustomerDelivery));
        var report = await ReportAsync(db, To);
        Assert.Equal(2, report.Totals.CustomerDeliveryCount);
        var balance = await new CompanyBalanceReportService(db, new ProfitAndLossService(db), new StockService(db), new PricingService(db))
            .BuildAsync(new CompanyBalanceReportRequest(From, To, "USD", new DateTime(2026, 5, 1)), []);
        var balanceRows = balance.DetailsFor(CompanyBalanceSection.GoodsInTransit);

        // ۳۰ + ۳۰ از مبدأ و ۱۲ + ۸ موتر. موترِ فروخته‌شده هنوز در راه است ولی در بیلانس طلب مشتری است، نه دارایی.
        Assert.Equal(80m, report.Totals.TotalQuantityMt);
        Assert.Equal(72m, balanceRows.Sum(r => r.QuantityMt ?? 0m));
        Assert.Equal(
            report.Rows.Where(r => r.Kind != GoodsInTransitKind.CustomerDelivery || r.SourceId != 2).Sum(r => r.QuantityMt),
            balanceRows.Sum(r => r.QuantityMt ?? 0m));
    }

    // ── زیرساخت ─────────────────────────────────────────────────────────────

    private static Task<IReadOnlyList<GoodsInTransitQuantityRow>> Transit(ApplicationDbContext db, DateTime asOf)
        => new GoodsInTransitQuantityReader(db).ReadAsync(asOf);

    private static async Task<GoodsInTransitReportViewModel> ReportAsync(ApplicationDbContext db, DateTime toDate)
    {
        var view = Assert.IsType<ViewResult>(await new ReportsController(db, clock: new FixedClock(new DateTime(2026, 10, 8)))
            .GoodsInTransit(new GoodsInTransitFilterViewModel { ToDate = toDate }));
        return Assert.IsType<GoodsInTransitReportViewModel>(view.Model);
    }

    private static ApplicationDbContext NewDb()
        => new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static void Seed(ApplicationDbContext db)
    {
        db.Companies.Add(new Company { Id = 1, Code = "PTG", Name = "PTG" });
        db.Products.Add(new Product { Id = 1, Code = "GAS", Name = "Gasoline" });
        db.Suppliers.Add(new Supplier { Id = 1, Name = "Supplier A" });
        db.Customers.Add(new Customer { Id = 1, Name = "Customer A" });
        db.Terminals.Add(new Terminal { Id = 1, Code = "ILK", Name = "Ilinka" });
        db.StorageTanks.Add(new StorageTank { Id = 1, TerminalId = 1, TankCode = "TANK-A" });
        db.Trucks.Add(new Truck { Id = 1, PlateNumber = "TRK-1" });
        db.ExpenseTypes.Add(new ExpenseType { Id = 1, Code = "GEN", Name = "General" });
        db.Contracts.Add(new Contract
        {
            Id = PurchaseId,
            ContractNumber = "P-005",
            ContractType = ContractType.Purchase,
            CompanyId = 1,
            ProductId = 1,
            SupplierId = 1,
            ContractDate = new DateTime(2026, 3, 1),
            QuantityMt = 500m,
            PricingMethod = PricingMethod.Fixed,
            UnitPriceUsd = 500m
        });
    }

    private static LoadingRegister Loading(int id, decimal quantityMt, DateTime? loadingDate = null)
        => new()
        {
            Id = id,
            ContractId = PurchaseId,
            ProductId = 1,
            LoadingDate = loadingDate ?? new DateTime(2026, 4, 2),
            LoadedQuantityMt = quantityMt,
            LoadingPriceUsd = 500m,
            WagonNumber = $"WGN-{id}"
        };

    private static LoadingReceipt Receipt(int id, int loadingId, decimal quantityMt, DateTime date)
        => new() { Id = id, LoadingRegisterId = loadingId, TerminalId = 1, StorageTankId = 1, ReceiptDate = date, ReceivedQuantityMt = quantityMt };

    private static LossEvent Shortage(int id, int? loadingId, int? receiptId, decimal quantityMt, DateTime date, bool cancelled = false)
        => new()
        {
            Id = id,
            Stage = LossEventStage.ReceiptShortage,
            ProductId = 1,
            LoadingRegisterId = loadingId,
            LoadingReceiptId = receiptId,
            EventDate = date,
            DifferenceQuantityMt = quantityMt,
            IsCancelled = cancelled
        };

    private static InventoryTransportLeg Leg(int id, decimal quantityMt, DateTime loadedDate, InventoryTransportLegStatus status)
        => new()
        {
            Id = id,
            SourcePurchaseContractId = PurchaseId,
            ProductId = 1,
            SourceTerminalId = 1,
            TransportType = LoadingTransportType.Truck,
            LoadedDate = loadedDate,
            QuantityMt = quantityMt,
            Status = status
        };

    private static InventoryMovement StockOut(int id, int saleId, decimal quantityMt, DateTime date)
        => new()
        {
            Id = id,
            ProductId = 1,
            ContractId = PurchaseId,
            TerminalId = 1,
            StorageTankId = 1,
            SalesTransactionId = saleId,
            Direction = MovementDirection.Out,
            MovementDate = date,
            QuantityMt = quantityMt
        };

    private static SalesTransaction Sale(int id, decimal quantityMt, decimal totalUsd, DateTime date)
        => new()
        {
            Id = id,
            CompanyId = 1,
            CustomerId = 1,
            ProductId = 1,
            InvoiceNumber = $"INV-{id}",
            SaleDate = date,
            QuantityMt = quantityMt,
            UnitPriceUsd = totalUsd / quantityMt,
            TotalUsd = totalUsd,
            TotalInCurrency = totalUsd,
            Currency = "USD"
        };

    private static ExpenseTransaction Expense(int id, decimal amountUsd, DateTime date)
        => new()
        {
            Id = id,
            ExpenseTypeId = 1,
            ExpenseDate = date,
            Amount = amountUsd,
            AmountUsd = amountUsd,
            Currency = "USD"
        };

    private sealed class FixedClock(DateTime today) : IAfghanistanBusinessClock
    {
        public DateTime Today => today.Date;
        public DateTimeOffset Now => new(today, TimeSpan.FromHours(4.5));
        public (DateTime StartUtc, DateTime EndUtcExclusive) UtcRange(DateTime localDate)
            => (localDate.Date, localDate.Date.AddDays(1));
    }
}
