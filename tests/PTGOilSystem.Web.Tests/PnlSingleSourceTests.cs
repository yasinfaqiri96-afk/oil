using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Controllers;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.Reports;
using PTGOilSystem.Web.Models.ShipmentPnl;
using PTGOilSystem.Web.Services.Reporting;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// تست‌های تطبیقی: هر صفحه‌ای که سود محقق‌شده نشان می‌دهد باید دقیقاً همان عددی را
/// بدهد که <see cref="ProfitAndLossService"/> می‌سازد. rollup عملیاتی (lineage) حذف
/// نمی‌شود، اما اجازه ندارد عدد سود متفاوتی تولید کند.
/// </summary>
public class PnlSingleSourceTests
{
    [Fact]
    public async Task ShipmentPnl_Details_Realised_Profit_Equals_ProfitAndLossService()
    {
        await using var db = NewDb();
        Seed(db);
        db.SalesTransactions.Add(Sale(1, quantityMt: 10m, totalUsd: 5_000m));
        db.SalesCostConsumptions.Add(Cost(1, 3_200m, SalesCostConsumptionStatus.Active));
        await db.SaveChangesAsync();

        var service = new ProfitAndLossService(db);
        var expected = await service.BuildForSalesAsync([1]);

        var view = Assert.IsType<ViewResult>(await new ShipmentPnlController(db).Details(1));
        var model = Assert.IsType<ShipmentPnlDetailsViewModel>(view.Model);

        Assert.Equal(expected.RevenueUsd, model.RealisedPnl.RevenueUsd);
        Assert.Equal(expected.CostOfGoodsSoldUsd, model.RealisedPnl.CostOfGoodsSoldUsd);
        Assert.Equal(expected.GrossProfitUsd, model.RealisedPnl.GrossProfitUsd);
        Assert.Equal(nameof(PnlConfidence.Verified), model.RealisedPnl.Confidence);

        // درآمد صفحه هم از همان منبع می‌آید، نه از جمع مستقل رول‌آپ.
        Assert.Equal(expected.RevenueUsd, model.TotalSalesUsd);
        Assert.Equal(1_800m, model.RealisedPnl.GrossProfitUsd);
    }

    [Fact]
    public async Task ShipmentPnl_Index_Realised_Profit_Equals_ProfitAndLossService()
    {
        await using var db = NewDb();
        Seed(db);
        db.SalesTransactions.Add(Sale(1, quantityMt: 10m, totalUsd: 5_000m));
        db.SalesCostConsumptions.Add(Cost(1, 3_200m, SalesCostConsumptionStatus.Active));
        await db.SaveChangesAsync();

        var expected = await new ProfitAndLossService(db).BuildForSalesAsync([1]);

        var view = Assert.IsType<ViewResult>(await new ShipmentPnlController(db).Index());
        var model = Assert.IsType<ShipmentPnlIndexViewModel>(view.Model);
        var item = Assert.Single(model.Items);

        Assert.Equal(expected.RevenueUsd, item.TotalSalesUsd);
        Assert.Equal(expected.GrossProfitUsd, item.RealisedPnl.GrossProfitUsd);
    }

    [Fact]
    public async Task Sale_Without_Active_Cost_Does_Not_Book_Fake_Profit()
    {
        await using var db = NewDb();
        Seed(db);
        db.SalesTransactions.Add(Sale(1, quantityMt: 10m, totalUsd: 5_000m));
        // فقط یک ردیف برگشت‌خورده وجود دارد → COGS فعالی نیست.
        db.SalesCostConsumptions.Add(Cost(1, 3_200m, SalesCostConsumptionStatus.Reversed));
        await db.SaveChangesAsync();

        var view = Assert.IsType<ViewResult>(await new ShipmentPnlController(db).Details(1));
        var model = Assert.IsType<ShipmentPnlDetailsViewModel>(view.Model);

        Assert.Equal(0m, model.RealisedPnl.CostOfGoodsSoldUsd);
        Assert.Equal(1, model.RealisedPnl.UncostedSaleCount);
        Assert.True(model.RealisedPnl.HasUncostedSales);
        // درآمد پنهان نمی‌شود، اما سود «قطعی» شمرده نمی‌شود.
        Assert.Equal(5_000m, model.RealisedPnl.RevenueUsd);
        Assert.Equal(nameof(PnlConfidence.NeedsReview), model.RealisedPnl.Confidence);
        Assert.Equal("danger", model.RealisedPnl.ConfidenceTone);
    }

    [Fact]
    public async Task Cancelled_Sale_Is_Excluded_From_Every_Pnl_Surface()
    {
        await using var db = NewDb();
        Seed(db);
        db.SalesTransactions.AddRange(
            Sale(1, quantityMt: 10m, totalUsd: 5_000m),
            Sale(2, quantityMt: 4m, totalUsd: 9_999m, cancelled: true));
        db.SalesCostConsumptions.Add(Cost(1, 3_200m, SalesCostConsumptionStatus.Active));
        await db.SaveChangesAsync();

        var view = Assert.IsType<ViewResult>(await new ShipmentPnlController(db).Details(1));
        var model = Assert.IsType<ShipmentPnlDetailsViewModel>(view.Model);

        Assert.Equal(5_000m, model.RealisedPnl.RevenueUsd);
        Assert.Equal(1, model.RealisedPnl.SaleCount);
    }

    [Fact]
    public async Task Company_Pnl_Reconciles_With_The_Sum_Of_Its_Sale_Contracts()
    {
        await using var db = NewDb();
        Seed(db);
        db.Contracts.Add(new Contract
        {
            Id = 2,
            ContractNumber = "CON-002",
            ContractType = ContractType.Sale,
            CompanyId = 1,
            ProductId = 1,
            CustomerId = 1,
            ContractDate = new DateTime(2026, 4, 20),
            QuantityMt = 100m,
            PricingMethod = PricingMethod.Fixed,
            UnitPriceUsd = 500m
        });
        db.SalesTransactions.AddRange(
            Sale(1, quantityMt: 10m, totalUsd: 5_000m),
            Sale(2, quantityMt: 6m, totalUsd: 3_000m, contractId: 2));
        db.SalesCostConsumptions.AddRange(
            Cost(1, 3_200m, SalesCostConsumptionStatus.Active),
            Cost(2, 2_100m, SalesCostConsumptionStatus.Active));
        await db.SaveChangesAsync();

        var service = new ProfitAndLossService(db);
        var company = await service.BuildCompanyAsync(new ManagementReportFilterViewModel());
        var byContract = await service.BuildForSaleContractsAsync([1, 2]);

        Assert.Equal(company.Sales.RevenueUsd, byContract.Values.Sum(v => v.RevenueUsd));
        Assert.Equal(company.Sales.CostOfGoodsSoldUsd, byContract.Values.Sum(v => v.CostOfGoodsSoldUsd));
        Assert.Equal(company.GrossProfitUsd, byContract.Values.Sum(v => v.GrossProfitUsd));
        Assert.Equal(2_700m, company.GrossProfitUsd);
    }

    [Fact]
    public async Task Company_Pnl_Counts_A_Shipment_And_Contract_Expense_Only_Once()
    {
        await using var db = NewDb();
        Seed(db);
        db.SalesTransactions.Add(Sale(1, quantityMt: 10m, totalUsd: 5_000m));
        db.SalesCostConsumptions.Add(Cost(1, 3_200m, SalesCostConsumptionStatus.Active));
        db.ExpenseTypes.Add(new ExpenseType { Id = 1, Code = "CUSTOMS", Name = "Customs", NamePersian = "گمرک" });
        // یک مصرف گمرکی که هم به قرارداد و هم به محموله وصل است؛ نباید دوبار شمرده شود.
        db.ExpenseTransactions.Add(new ExpenseTransaction
        {
            Id = 1,
            ExpenseTypeId = 1,
            ContractId = 1,
            ShipmentId = 1,
            ExpenseDate = new DateTime(2026, 4, 23),
            Amount = 400m,
            AmountUsd = 400m,
            Currency = "USD"
        });
        await db.SaveChangesAsync();

        var company = await new ProfitAndLossService(db)
            .BuildCompanyAsync(new ManagementReportFilterViewModel());

        Assert.Equal(400m, company.OperatingExpenseUsd);
        Assert.Equal(1_400m, company.NetProfitUsd);
    }

    private static ApplicationDbContext NewDb()
        => new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static void Seed(ApplicationDbContext db)
    {
        db.Companies.Add(new Company { Id = 1, Code = "PTG", Name = "PTG Trading" });
        db.Products.Add(new Product { Id = 1, Code = "GO", Name = "Gas Oil" });
        db.Customers.Add(new Customer { Id = 1, Name = "Customer A" });
        db.Suppliers.Add(new Supplier { Id = 1, Name = "Supplier A" });
        db.Locations.AddRange(
            new Location { Id = 1, Name = "Bandar Abbas" },
            new Location { Id = 2, Name = "Kabul Depot" });
        db.Contracts.Add(new Contract
        {
            Id = 1,
            ContractNumber = "CON-001",
            ContractType = ContractType.Sale,
            CompanyId = 1,
            ProductId = 1,
            CustomerId = 1,
            SupplierId = 1,
            DestinationLocationId = 2,
            ContractDate = new DateTime(2026, 4, 20),
            QuantityMt = 100m,
            PricingMethod = PricingMethod.Fixed,
            UnitPriceUsd = 500m
        });
        db.Vessels.Add(new Vessel { Id = 1, Name = "MV Test" });
        db.Shipments.Add(new Shipment
        {
            Id = 1,
            ShipmentCode = "SHIP-01",
            VesselId = 1,
            ContractId = 1,
            DepartureDate = new DateTime(2026, 4, 21),
            ArrivalDate = new DateTime(2026, 4, 28),
            OriginLocationId = 1,
            DestinationLocationId = 2,
            QuantityMt = 50m
        });
    }

    private static SalesTransaction Sale(
        int id,
        decimal quantityMt,
        decimal totalUsd,
        bool cancelled = false,
        int contractId = 1)
        => new()
        {
            Id = id,
            CompanyId = 1,
            ContractId = contractId,
            CustomerId = 1,
            ProductId = 1,
            ShipmentId = 1,
            InvoiceNumber = $"INV-{id}",
            SaleDate = new DateTime(2026, 4, 22),
            QuantityMt = quantityMt,
            UnitPriceUsd = quantityMt > 0m ? totalUsd / quantityMt : 0m,
            TotalUsd = totalUsd,
            TotalInCurrency = totalUsd,
            Currency = "USD",
            IsCancelled = cancelled
        };

    private static SalesCostConsumption Cost(int saleId, decimal costUsd, SalesCostConsumptionStatus status)
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
}
