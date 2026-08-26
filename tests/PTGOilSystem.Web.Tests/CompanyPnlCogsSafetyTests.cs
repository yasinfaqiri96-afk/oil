using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Controllers;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.Reports;
using PTGOilSystem.Web.Services.Reporting;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// تا وقتی بهای تمام‌شدهٔ فروش‌ها ثبت نشده، COGS صفر خوانده می‌شود و «سود» بیش از واقع
/// درمی‌آید. نمای کلی مالی حق ندارد چنین عددی را به‌عنوان سود قطعی منتشر کند — نه در
/// صفحه و نه در خروجی. هیچ COGS حدسی هم جایگزین نمی‌شود.
/// </summary>
public class CompanyPnlCogsSafetyTests
{
    [Fact]
    public async Task Company_Overview_Does_Not_Publish_Profit_When_Cogs_Is_Missing()
    {
        await using var db = NewDb();
        Seed(db);
        db.SalesTransactions.Add(Sale(1, 10m, 5_000m));
        await db.SaveChangesAsync();

        var model = await CompanyOverviewAsync(db);

        Assert.False(model.IsProfitPublishable);
        Assert.Equal(1, model.UncostedSaleCount);
        Assert.Equal(PnlConfidence.NeedsReview, model.PnlConfidence);

        // درآمد و مصرف همچنان عدد واقعی‌اند؛ فقط سود منتشر نمی‌شود.
        Assert.Equal(5_000m, model.RevenueUsd);
        Assert.Equal(0m, model.PurchaseCostUsd);

        var profitMetric = Assert.Single(model.Metrics.Where(m => m.Label == "سود خالص"));
        Assert.Equal("—", profitMetric.Value);
        Assert.Contains("COGS incomplete", profitMetric.Detail);
        Assert.Contains("سود نهایی قابل محاسبه نیست", model.ProfitUnavailableNoteFa);
    }

    [Fact]
    public async Task Company_Overview_Publishes_Profit_Once_Every_Sale_Is_Costed()
    {
        await using var db = NewDb();
        Seed(db);
        db.SalesTransactions.Add(Sale(1, 10m, 5_000m));
        db.SalesCostConsumptions.Add(new SalesCostConsumption
        {
            SalesTransactionId = 1,
            CompanyId = 1,
            ProductId = 1,
            TerminalId = 1,
            QuantityMt = 10m,
            CostUsd = 3_200m,
            Status = SalesCostConsumptionStatus.Active
        });
        await db.SaveChangesAsync();

        var model = await CompanyOverviewAsync(db);

        Assert.True(model.IsProfitPublishable);
        Assert.Equal(0, model.UncostedSaleCount);
        Assert.Equal(3_200m, model.PurchaseCostUsd);
        Assert.Equal(1_800m, model.GrossProfitUsd);

        var profitMetric = Assert.Single(model.Metrics.Where(m => m.Label == "سود خالص"));
        Assert.NotEqual("—", profitMetric.Value);
        Assert.Equal("Net profit", profitMetric.Detail);
    }

    private static async Task<CompanyFinancialOverviewViewModel> CompanyOverviewAsync(ApplicationDbContext db)
    {
        var view = Assert.IsType<ViewResult>(
            await new ReportsController(db).CompanyOverview(new ManagementReportFilterViewModel()));
        return Assert.IsType<CompanyFinancialOverviewViewModel>(view.Model);
    }

    private static ApplicationDbContext NewDb()
        => new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static void Seed(ApplicationDbContext db)
    {
        db.Companies.Add(new Company { Id = 1, Code = "PTG", Name = "PTG" });
        db.Products.Add(new Product { Id = 1, Code = "GAS", Name = "Gasoline" });
        db.Customers.Add(new Customer { Id = 1, Name = "Customer A" });
        db.Terminals.Add(new Terminal { Id = 1, Code = "ILK", Name = "Ilinka" });
    }

    private static SalesTransaction Sale(int id, decimal quantityMt, decimal totalUsd)
        => new()
        {
            Id = id,
            CompanyId = 1,
            CustomerId = 1,
            ProductId = 1,
            InvoiceNumber = $"INV-{id}",
            SaleDate = new DateTime(2026, 4, 22),
            QuantityMt = quantityMt,
            UnitPriceUsd = totalUsd / quantityMt,
            TotalUsd = totalUsd,
            TotalInCurrency = totalUsd,
            Currency = "USD"
        };
}
