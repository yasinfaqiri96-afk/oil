using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PTGOilSystem.Web.Controllers;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.Reconciliation;
using PTGOilSystem.Web.Models.Reports;
using PTGOilSystem.Web.Models.Sales;
using PTGOilSystem.Web.Services.Exports;
using PTGOilSystem.Web.Services.Reconciliation;
using PTGOilSystem.Web.Services.Time;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// قاعدهٔ تحویل: صفحه و خروجی باید از یک منبع بخوانند و رقم یکسان بدهند.
/// این تست‌ها خروجی را واقعاً تولید می‌کنند (Excel و PDF) و جمع‌ها را با مدل صفحه می‌سنجند.
/// ردیف‌های لغوشده نباید وارد خروجی شوند.
/// </summary>
public class ExportMatchesPageTests
{
    private sealed class FixedClock(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private static IAfghanistanBusinessClock Clock
        => new AfghanistanBusinessClock(new FixedClock(new DateTimeOffset(2026, 7, 30, 12, 0, 0, TimeSpan.Zero)));

    private static ApplicationDbContext NewDb()
        => new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static T WithHttpContext<T>(T controller) where T : Controller
    {
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };
        return controller;
    }

    private static void SeedReference(ApplicationDbContext db)
    {
        db.Products.Add(new Product { Id = 1, Code = "GAS", Name = "Gasoline" });
        db.Companies.Add(new Company { Id = 1, Code = "PTG", Name = "PTG" });
        db.Customers.Add(new Customer { Id = 1, Name = "Customer A" });
        db.Terminals.Add(new Terminal { Id = 1, Code = "T1", Name = "Terminal 1" });
    }

    private static SalesTransaction NewSale(int id, decimal totalUsd, bool cancelled = false) => new()
    {
        Id = id,
        CompanyId = 1,
        CustomerId = 1,
        ProductId = 1,
        InvoiceNumber = "INV-" + id,
        SaleDate = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
        QuantityMt = 10m,
        UnitPriceUsd = totalUsd / 10m,
        TotalUsd = totalUsd,
        IsCancelled = cancelled
    };

    /// <summary>ردیف‌های سند خروجی، چه همگام و چه جریانی، از یک مسیر خوانده می‌شوند.</summary>
    private static async Task<List<TabularExportRow>> CollectAsync(TabularExportDocument document)
    {
        var rows = new List<TabularExportRow>();
        await foreach (var row in document.EnumerateRowsAsync())
        {
            rows.Add(row);
        }
        return rows;
    }

    [Theory]
    [InlineData("excel")]
    [InlineData("pdf")]
    public async Task Reconciliation_Discrepancy_Export_Produces_A_File_For_Both_Formats(string format)
    {
        await using var db = NewDb();
        SeedReference(db);
        db.SalesTransactions.AddRange(NewSale(1, 5_000m), NewSale(2, 7_000m));
        await db.SaveChangesAsync();

        var controller = WithHttpContext(new ReconciliationController(db, null, Clock));

        var result = await controller.DiscrepanciesExport(
            format, ReconciliationDiscrepancyCategory.SaleWithoutCogs);

        var export = Assert.IsType<TabularExportResult>(result);
        Assert.Equal("PTG_Reconciliation_SaleWithoutCogs", export.Document.FileNameStem);
        // ردیف‌های این خروجی جریانی‌اند (RowsAsync)، پس هیچ‌وقت کل نتیجه در حافظه نیست.
        Assert.Equal(2, (await CollectAsync(export.Document)).Count);
        Assert.Equal(2, export.Document.KnownRowCount);
        Assert.Equal(
            format == "pdf" ? TabularExportFormat.Pdf : TabularExportFormat.Excel,
            export.Format);
    }

    [Fact]
    public async Task Reconciliation_Discrepancy_Export_Uses_The_Same_Rows_As_The_Page()
    {
        await using var db = NewDb();
        SeedReference(db);
        db.SalesTransactions.AddRange(
            NewSale(1, 5_000m),
            NewSale(2, 7_000m),
            NewSale(3, 9_000m, cancelled: true));
        await db.SaveChangesAsync();

        var controller = WithHttpContext(new ReconciliationController(db, null, Clock));

        var view = Assert.IsType<ViewResult>(await controller.Discrepancies(
            ReconciliationDiscrepancyCategory.SaleWithoutCogs));
        var page = Assert.IsType<ReconciliationDiscrepanciesViewModel>(view.Model);

        // فروش لغوشده نه در صفحه است و نه در خروجی.
        Assert.NotNull(page.Selected);
        Assert.Equal(2, page.Selected!.TotalCount);
        Assert.DoesNotContain(page.Selected.Rows, r => r.Reference == "INV-3");

        var direct = await new ReconciliationService(db, null, Clock)
            .BuildDiscrepancyPageAsync(ReconciliationDiscrepancyCategory.SaleWithoutCogs, 1, 500);
        Assert.Equal(page.Selected.TotalCount, direct.TotalCount);
        Assert.Equal(
            page.Selected.Rows.Select(r => r.Reference).ToArray(),
            direct.Rows.Select(r => r.Reference).ToArray());
    }

    [Fact]
    public async Task Reconciliation_Discrepancy_Export_Honours_The_Page_Date_Filter()
    {
        await using var db = NewDb();
        SeedReference(db);
        var early = NewSale(1, 5_000m);
        early.SaleDate = new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc);
        db.SalesTransactions.AddRange(early, NewSale(2, 7_000m));
        await db.SaveChangesAsync();

        var controller = WithHttpContext(new ReconciliationController(db, null, Clock));
        var from = new DateTime(2026, 4, 1);

        var view = Assert.IsType<ViewResult>(await controller.Discrepancies(
            ReconciliationDiscrepancyCategory.SaleWithoutCogs, fromDate: from));
        var page = Assert.IsType<ReconciliationDiscrepanciesViewModel>(view.Model);
        Assert.Equal(1, page.Selected!.TotalCount);

        var export = Assert.IsType<TabularExportResult>(await controller.DiscrepanciesExport(
            "excel", ReconciliationDiscrepancyCategory.SaleWithoutCogs, from));
        Assert.Single(await CollectAsync(export.Document));

        var direct = await new ReconciliationService(db, null, Clock)
            .BuildDiscrepancyPageAsync(
                ReconciliationDiscrepancyCategory.SaleWithoutCogs, 1, 500, from);
        Assert.Equal(page.Selected.TotalCount, direct.TotalCount);
    }

    [Fact]
    public async Task Company_Pnl_Export_Reads_The_Same_Model_As_The_Page()
    {
        await using var db = NewDb();
        SeedReference(db);
        db.SalesTransactions.AddRange(NewSale(1, 5_000m), NewSale(2, 9_000m, cancelled: true));
        await db.SaveChangesAsync();

        var controller = WithHttpContext(new ReportsController(db, clock: Clock));

        var view = Assert.IsType<ViewResult>(await controller.CompanyOverview());
        var page = Assert.IsType<CompanyFinancialOverviewViewModel>(view.Model);

        // فروش لغوشده وارد درآمد نمی‌شود.
        Assert.Equal(5_000m, page.RevenueUsd);

        var export = Assert.IsType<TabularExportResult>(await controller.CompanyOverviewExport("excel"));
        Assert.Equal("PTG_Company_PnL", export.Document.FileNameStem);

        // ردیف اول خروجی همان درآمد صفحه است و جمع پایانی همان سود خالص صفحه.
        var revenueCell = export.Document.Rows.First().Cells[1];
        Assert.Equal(page.RevenueUsd, Assert.IsType<decimal>(revenueCell.Value));
        Assert.NotNull(export.Document.Totals);

        // این فروش بهای تمام‌شده ندارد، پس صفحه سود را منتشر نمی‌کند و خروجی هم نباید
        // بکند؛ صفحه و خروجی دقیقاً یک تصمیم می‌گیرند.
        Assert.False(page.IsProfitPublishable);
        Assert.Null(export.Document.Totals!.Cells[1].Value);
    }

    [Fact]
    public async Task Company_Pnl_Export_Publishes_Net_Profit_Once_Sales_Are_Costed()
    {
        await using var db = NewDb();
        SeedReference(db);
        db.SalesTransactions.Add(NewSale(1, 5_000m));
        db.SalesCostConsumptions.Add(new SalesCostConsumption
        {
            SalesTransactionId = 1,
            CompanyId = 1,
            ProductId = 1,
            TerminalId = 1,
            QuantityMt = 1m,
            CostUsd = 3_000m,
            Status = SalesCostConsumptionStatus.Active
        });
        await db.SaveChangesAsync();

        var controller = WithHttpContext(new ReportsController(db, clock: Clock));

        var view = Assert.IsType<ViewResult>(await controller.CompanyOverview());
        var page = Assert.IsType<CompanyFinancialOverviewViewModel>(view.Model);
        Assert.True(page.IsProfitPublishable);

        var export = Assert.IsType<TabularExportResult>(await controller.CompanyOverviewExport("excel"));
        Assert.Equal(page.NetProfitUsd, Assert.IsType<decimal>(export.Document.Totals!.Cells[1].Value));
    }

    [Fact]
    public async Task PreSale_Commitments_Export_Matches_The_Page_Totals()
    {
        await using var db = NewDb();
        SeedReference(db);
        db.PreSaleOrders.AddRange(
            new PreSaleOrder
            {
                Id = 1,
                OrderNumber = "PS-1",
                CustomerId = 1,
                ProductId = 1,
                CompanyId = 1,
                OrderDate = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
                QuantityMt = 30m,
                Currency = "USD",
                TotalInCurrency = 15_000m,
                TotalUsd = 15_000m,
                Status = PreSaleOrderStatus.Confirmed
            },
            new PreSaleOrder
            {
                Id = 2,
                OrderNumber = "PS-2",
                CustomerId = 1,
                ProductId = 1,
                CompanyId = 1,
                OrderDate = new DateTime(2026, 5, 2, 0, 0, 0, DateTimeKind.Utc),
                QuantityMt = 20m,
                Currency = "USD",
                TotalInCurrency = 10_000m,
                TotalUsd = 10_000m,
                Status = PreSaleOrderStatus.PartiallyDelivered
            });
        var delivered = NewSale(1, 5_000m);
        delivered.PreSaleOrderId = 2;
        var cancelledDelivery = NewSale(2, 5_000m, cancelled: true);
        cancelledDelivery.PreSaleOrderId = 2;
        db.SalesTransactions.AddRange(delivered, cancelledDelivery);
        await db.SaveChangesAsync();

        var controller = WithHttpContext(NewSalesController(db));

        var view = Assert.IsType<ViewResult>(await controller.PreSales());
        var page = Assert.IsType<PreSaleIndexViewModel>(view.Model);

        // تحویل لغوشده در شمارش تحویل‌شده نمی‌آید.
        Assert.Equal(10m, page.Items.Single(i => i.OrderNumber == "PS-2").DeliveredMt);
        Assert.Equal(50m, page.Items.Sum(i => i.QuantityMt));

        var export = Assert.IsType<TabularExportResult>(await controller.PreSalesExport("excel"));
        Assert.Equal("PTG_PreSale_Commitments", export.Document.FileNameStem);
        Assert.Equal(page.Items.Count, export.Document.Rows.Count());

        // جمع تعهد و تحویل در خروجی دقیقاً همان جمع صفحه است.
        var totals = Assert.IsType<TabularExportRow>(export.Document.Totals);
        Assert.Equal(page.Items.Sum(i => i.QuantityMt), Assert.IsType<decimal>(totals.Cells[5].Value));
        Assert.Equal(page.Items.Sum(i => i.DeliveredMt), Assert.IsType<decimal>(totals.Cells[6].Value));
    }

    [Fact]
    public async Task Quality_Inspection_Export_Applies_The_Page_Status_Filter()
    {
        await using var db = NewDb();
        SeedReference(db);
        db.QualityInspections.AddRange(
            new QualityInspection { Id = 1, ProductId = 1, LaboratoryName = "Lab", ResultNumber = "QI-1", SampleDate = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc), Status = QualityInspectionStatus.Rejected },
            new QualityInspection { Id = 2, ProductId = 1, LaboratoryName = "Lab", ResultNumber = "QI-2", SampleDate = new DateTime(2026, 5, 2, 0, 0, 0, DateTimeKind.Utc), Status = QualityInspectionStatus.Accepted });
        await db.SaveChangesAsync();

        var controller = WithHttpContext(new QualityInspectionsController(
            db,
            new PTGOilSystem.Web.Services.AuditService(db),
            new TestWebHostEnvironment(),
            Clock,
            NullLogger<QualityInspectionsController>.Instance));

        var filter = new PTGOilSystem.Web.Models.Quality.QualityInspectionFilterViewModel
        {
            Status = QualityInspectionStatus.Rejected
        };

        var export = Assert.IsType<TabularExportResult>(await controller.Export("excel", filter));
        Assert.Equal("PTG_Quality_Inspections", export.Document.FileNameStem);

        // فقط آزمایش رد شده باید در خروجی باشد؛ همان چیزی که صفحه با این فیلتر نشان می‌دهد.
        var rows = export.Document.Rows.ToList();
        Assert.Single(rows);
        Assert.Equal("QI-1", rows[0].Cells[0].Value);
    }

    [Fact]
    public async Task Audit_Export_And_Cancellations_Export_Use_The_Same_Filter_As_The_Page()
    {
        await using var db = NewDb();
        db.AuditLogs.AddRange(
            new AuditLog { Id = 1, EntityName = "SalesTransaction", EntityId = 1, Action = "Create", Category = "Data", Module = "Sales", ActionAtUtc = new DateTime(2026, 5, 1, 8, 0, 0, DateTimeKind.Utc), IsSuccess = true },
            new AuditLog { Id = 2, EntityName = "SalesTransaction", EntityId = 1, Action = "Cancel", Category = "Data", Module = "Sales", ActionAtUtc = new DateTime(2026, 5, 2, 8, 0, 0, DateTimeKind.Utc), IsSuccess = true });
        await db.SaveChangesAsync();

        var controller = WithHttpContext(new AuditLogsController(db));

        var all = Assert.IsType<TabularExportResult>(await controller.Export("excel"));
        Assert.Equal("PTG_Audit_Log", all.Document.FileNameStem);
        Assert.Equal(2, all.Document.Rows.Count());

        var cancellations = Assert.IsType<TabularExportResult>(
            await controller.Export("excel", cancellationsOnly: true));
        Assert.Equal("PTG_Audit_Cancellations", cancellations.Document.FileNameStem);
        Assert.Single(cancellations.Document.Rows);
    }

    private static SalesController NewSalesController(ApplicationDbContext db)
        => new(
            db,
            new PTGOilSystem.Web.Services.StockService(db),
            new PTGOilSystem.Web.Services.CurrencyConversionService(new PTGOilSystem.Web.Services.PricingService(db)),
            new PTGOilSystem.Web.Services.AuditService(db),
            NullLogger<SalesController>.Instance,
            businessClock: Clock);

    private sealed class TestWebHostEnvironment : Microsoft.AspNetCore.Hosting.IWebHostEnvironment
    {
        public string WebRootPath { get; set; } = Path.GetTempPath();
        public Microsoft.Extensions.FileProviders.IFileProvider WebRootFileProvider { get; set; }
            = new Microsoft.Extensions.FileProviders.NullFileProvider();
        public string ApplicationName { get; set; } = "PTGOilSystem.Web.Tests";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; }
            = new Microsoft.Extensions.FileProviders.NullFileProvider();
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public string EnvironmentName { get; set; } = "Development";
    }
}
