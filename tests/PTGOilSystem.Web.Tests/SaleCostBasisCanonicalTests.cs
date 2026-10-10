using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Controllers;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.Reports;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Exports;
using PTGOilSystem.Web.Services.Reporting;
using PTGOilSystem.Web.Services.Time;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// بهای تمام‌شدهٔ هر فروش یک قاعده دارد (شرکت، محموله، قرارداد): سهمِ قرارداد خرید × بهای واحدِ همان قرارداد
/// تا تاریخ خودِ فروش؛ اگر نبود Pool فعال؛ هرگز هر دو. بارگیریِ بعد از فروش بهای فروشِ گذشته را عوض نمی‌کند.
/// </summary>
public class SaleCostBasisCanonicalTests
{
    private const int PurchaseA = 5;
    private const int PurchaseB = 6;
    private static readonly DateTime From = new(2026, 7, 1);
    private static readonly DateTime To = new(2026, 7, 31);

    [Fact]
    public async Task Loading_After_The_Sale_Does_Not_Change_The_Cost_Of_That_Sale()
    {
        await using var db = NewDb();
        Seed(db);
        db.LoadingRegisters.AddRange(
            Loading(1, PurchaseA, 100m, 500m, new DateTime(2026, 7, 1)),
            Loading(2, PurchaseA, 100m, 700m, new DateTime(2026, 7, 25)));
        db.SalesTransactions.Add(Sale(100, 100m, 80_000m, new DateTime(2026, 7, 5)));
        db.InventoryMovements.Add(StockOut(1, saleId: 100, PurchaseA, 100m, new DateTime(2026, 7, 5)));
        await db.SaveChangesAsync();

        var engine = await new ProfitAndLossService(db).BuildCompanyPeriodAsync(
            new ManagementReportFilterViewModel { FromDate = From, ToDate = To }, To);
        var balance = await new CompanyBalanceReportService(db, new ProfitAndLossService(db), new StockService(db), new PricingService(db))
            .BuildAsync(new CompanyBalanceReportRequest(From, To, "USD", new DateTime(2026, 8, 1)), []);
        var view = Assert.IsType<ViewResult>(await new ReportsController(db, clock: new FixedClock(To))
            .CompanyOverview(new ManagementReportFilterViewModel { FromDate = From, ToDate = To }));
        var overview = Assert.IsType<CompanyFinancialOverviewViewModel>(view.Model);
        var sale = await new ProfitAndLossService(db).BuildForSalesAsync([100]);

        // ۱۰۰ تن × ۵۰۰ (میانگین تا ۵ جولای)، نه ۶۰۰ (میانگین تا آخر ماه).
        Assert.Equal(50_000m, engine.CostOfSalesUsd);
        Assert.Equal(50_000m, balance.CostOfSalesUsd);
        Assert.Equal(50_000m, overview.PurchaseCostUsd);
        Assert.Equal(50_000m, sale.CostOfGoodsSoldUsd);
    }

    [Fact]
    public async Task Contract_Cost_Is_Used_Before_Pool_And_Pool_Only_Fills_Unattributed_Sales()
    {
        await using var db = NewDb();
        Seed(db);
        db.LoadingRegisters.Add(Loading(1, PurchaseA, 100m, 500m, new DateTime(2026, 7, 1)));
        db.SalesTransactions.AddRange(
            Sale(100, 10m, 7_000m, new DateTime(2026, 7, 5)),   // انتساب به قرارداد خرید + ردیف Pool
            Sale(101, 4m, 3_000m, new DateTime(2026, 7, 6)),    // بدون انتساب، با Pool
            Sale(102, 2m, 1_500m, new DateTime(2026, 7, 7)));   // بدون انتساب و بدون Pool
        db.InventoryMovements.Add(StockOut(1, saleId: 100, PurchaseA, 10m, new DateTime(2026, 7, 5)));
        db.SalesCostConsumptions.AddRange(Pool(100, 9_999m), Pool(101, 2_100m), Pool(102, 800m, SalesCostConsumptionStatus.Reversed));
        await db.SaveChangesAsync();

        var groups = await new ProfitAndLossService(db).BuildForSaleGroupsAsync(
            new Dictionary<int, int> { [100] = 1, [101] = 1, [102] = 2 });

        // ۱۰ × ۵۰۰ از قرارداد (Pool همان فروش جمع نمی‌شود) + ۲٬۱۰۰ از Pool.
        Assert.Equal(7_100m, groups[1].CostOfGoodsSoldUsd);
        Assert.Equal(0, groups[1].UncostedSaleCount);
        Assert.Equal(PnlConfidence.Estimated, groups[1].Confidence);
        Assert.Equal(0m, groups[2].CostOfGoodsSoldUsd);
        Assert.Equal(1, groups[2].UncostedSaleCount);
        Assert.Equal(PnlConfidence.NeedsReview, groups[2].Confidence);
    }

    [Fact]
    public async Task Contract_Journey_Realised_Keeps_Only_The_Share_Of_That_Contract()
    {
        await using var db = NewDb();
        Seed(db);
        db.LoadingRegisters.AddRange(
            Loading(1, PurchaseA, 100m, 500m, new DateTime(2026, 7, 1)),
            Loading(2, PurchaseB, 100m, 600m, new DateTime(2026, 7, 1)));
        db.SalesTransactions.Add(Sale(100, 10m, 8_000m, new DateTime(2026, 7, 5)));
        db.SalesTransactionSourceAllocations.AddRange(
            new SalesTransactionSourceAllocation { Id = 1, SalesTransactionId = 100, SourcePurchaseContractId = PurchaseA, QuantityMt = 6m, AmountUsd = 4_800m },
            new SalesTransactionSourceAllocation { Id = 2, SalesTransactionId = 100, SourcePurchaseContractId = PurchaseB, QuantityMt = 4m, AmountUsd = 3_200m });
        await db.SaveChangesAsync();

        var service = new ProfitAndLossService(db);
        var a = await service.BuildForPurchaseContractSalesAsync(PurchaseA, [100]);
        var b = await service.BuildForPurchaseContractSalesAsync(PurchaseB, [100]);
        var whole = await service.BuildForSalesAsync([100]);

        Assert.Equal(4_800m, a.RevenueUsd);
        Assert.Equal(3_000m, a.CostOfGoodsSoldUsd);
        Assert.Equal(3_200m, b.RevenueUsd);
        Assert.Equal(2_400m, b.CostOfGoodsSoldUsd);
        Assert.Equal(8_000m, whole.RevenueUsd);
        Assert.Equal(a.CostOfGoodsSoldUsd + b.CostOfGoodsSoldUsd, whole.CostOfGoodsSoldUsd);
    }

    [Fact]
    public async Task Sale_With_An_Uncosted_Contract_Share_Needs_Review_Instead_Of_Counting_As_Costed()
    {
        await using var db = NewDb();
        Seed(db);
        // قرارداد B تا تاریخ فروش هیچ بارگیری ندارد، پس سهمِ آن بهای واحد ندارد.
        db.LoadingRegisters.AddRange(
            Loading(1, PurchaseA, 100m, 500m, new DateTime(2026, 7, 1)),
            Loading(2, PurchaseB, 100m, 600m, new DateTime(2026, 7, 25)));
        db.SalesTransactions.Add(Sale(100, 10m, 8_000m, new DateTime(2026, 7, 5)));
        db.SalesTransactionSourceAllocations.AddRange(
            new SalesTransactionSourceAllocation { Id = 1, SalesTransactionId = 100, SourcePurchaseContractId = PurchaseA, QuantityMt = 6m, AmountUsd = 4_800m },
            new SalesTransactionSourceAllocation { Id = 2, SalesTransactionId = 100, SourcePurchaseContractId = PurchaseB, QuantityMt = 4m, AmountUsd = 3_200m });
        await db.SaveChangesAsync();

        var service = new ProfitAndLossService(db);
        var whole = await service.BuildForSalesAsync([100]);
        var company = await service.BuildCompanyPeriodAsync(
            new ManagementReportFilterViewModel { FromDate = From, ToDate = To }, To);
        var a = await service.BuildForPurchaseContractSalesAsync(PurchaseA, [100]);

        // بهای سهمِ بهادار می‌ماند، ولی فروش کامل‌بها نیست و سود آن قطعی خوانده نمی‌شود.
        Assert.Equal(3_000m, whole.CostOfGoodsSoldUsd);
        Assert.Equal(1, whole.UncostedSaleCount);
        Assert.Equal(PnlConfidence.NeedsReview, whole.Confidence);
        Assert.Equal(1, company.UncostedSaleCount);
        Assert.Equal(0, company.ContractCostedSaleCount);
        Assert.Equal(PnlConfidence.NeedsReview, company.Confidence);
        // در پروندهٔ قرارداد A فقط سهمِ A دیده می‌شود که بها دارد.
        Assert.Equal(3_000m, a.CostOfGoodsSoldUsd);
        Assert.Equal(PnlConfidence.Estimated, a.Confidence);
    }

    [Fact]
    public async Task Overview_Export_Lists_Operational_Indicators_Apart_From_Net_Profit()
    {
        await using var db = NewDb();
        Seed(db);
        db.LoadingRegisters.Add(Loading(1, PurchaseA, 100m, 500m, new DateTime(2026, 7, 1)));
        db.SalesTransactions.Add(Sale(100, 10m, 7_000m, new DateTime(2026, 7, 5)));
        db.InventoryMovements.Add(StockOut(1, saleId: 100, PurchaseA, 10m, new DateTime(2026, 7, 5)));
        await db.SaveChangesAsync();

        var controller = new ReportsController(db, clock: new FixedClock(To))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        var view = Assert.IsType<ViewResult>(await controller.CompanyOverview());
        var page = Assert.IsType<CompanyFinancialOverviewViewModel>(view.Model);
        var export = Assert.IsType<TabularExportResult>(await controller.CompanyOverviewExport("excel"));
        var rows = export.Document.Rows.ToList();

        var heading = rows.FindIndex(r => Equals(r.Cells[0].Value, CompanyFinancialOverviewViewModel.OperationalIndicatorsTitleFa));
        Assert.True(heading > 0);
        Assert.Null(rows[heading].Cells[1].Value);
        Assert.Equal(CompanyFinancialOverviewViewModel.OperationalIndicatorsNoteFa, rows[heading].Cells[2].Value);
        // مقدار مثبت، جدا از سطرهای مفاد؛ سود خالص همان سود صفحه است.
        Assert.Equal(page.LoadingOperationalCostUsd, rows[heading + 1].Cells[1].Value);
        Assert.Equal(page.LossCostUsd, rows[heading + 2].Cells[1].Value);
        Assert.DoesNotContain(rows.Take(heading), r => Equals(r.Cells[0].Value, "کسر و ضایعات"));
        Assert.Equal(page.NetProfitUsd, export.Document.Totals!.Cells[1].Value);
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
        db.Contracts.AddRange(Purchase(PurchaseA, "P-005", 500m), Purchase(PurchaseB, "P-006", 600m));
    }

    private static Contract Purchase(int id, string number, decimal price)
        => new()
        {
            Id = id,
            ContractNumber = number,
            ContractType = ContractType.Purchase,
            CompanyId = 1,
            ProductId = 1,
            SupplierId = 1,
            ContractDate = new DateTime(2026, 6, 1),
            QuantityMt = 1_000m,
            PricingMethod = PricingMethod.Fixed,
            UnitPriceUsd = price
        };

    private static LoadingRegister Loading(int id, int contractId, decimal quantityMt, decimal priceUsd, DateTime date)
        => new()
        {
            Id = id,
            ContractId = contractId,
            ProductId = 1,
            LoadingDate = date,
            LoadedQuantityMt = quantityMt,
            LoadingPriceUsd = priceUsd
        };

    private static InventoryMovement StockOut(int id, int saleId, int contractId, decimal quantityMt, DateTime date)
        => new()
        {
            Id = id,
            ProductId = 1,
            ContractId = contractId,
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

    private static SalesCostConsumption Pool(int saleId, decimal costUsd, SalesCostConsumptionStatus status = SalesCostConsumptionStatus.Active)
        => new()
        {
            SalesTransactionId = saleId,
            CompanyId = 1,
            ProductId = 1,
            TerminalId = 1,
            QuantityMt = 1m,
            CostUsd = costUsd,
            Status = status
        };

    private sealed class FixedClock(DateTime today) : IAfghanistanBusinessClock
    {
        public DateTime Today => today.Date;
        public DateTimeOffset Now => new(today, TimeSpan.FromHours(4.5));
        public (DateTime StartUtc, DateTime EndUtcExclusive) UtcRange(DateTime localDate)
            => (localDate.Date, localDate.Date.AddDays(1));
    }
}
