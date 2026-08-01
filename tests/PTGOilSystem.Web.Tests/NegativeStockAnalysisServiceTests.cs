using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.Reports;
using PTGOilSystem.Web.Services.Reporting;
using Xunit;

namespace PTGOilSystem.Web.Tests;

public class NegativeStockAnalysisServiceTests
{
    [Fact]
    public async Task A_Scope_That_Never_Goes_Below_Zero_Is_Not_Reported()
    {
        await using var db = NewDb();
        SeedReferences(db);
        db.InventoryMovements.AddRange(
            Movement(1, MovementDirection.In, 100m, new DateTime(2026, 5, 1)),
            Movement(2, MovementDirection.Out, 40m, new DateTime(2026, 5, 2)));
        await db.SaveChangesAsync();

        var findings = await new NegativeStockAnalysisService(db)
            .AnalyzeAsync(new ManagementReportFilterViewModel());

        Assert.Empty(findings);
    }

    [Fact]
    public async Task Still_Negative_Scope_Is_Reported_As_Open_With_The_Causing_Movement()
    {
        await using var db = NewDb();
        SeedReferences(db);
        db.InventoryMovements.AddRange(
            Movement(1, MovementDirection.In, 30m, new DateTime(2026, 5, 1)),
            Movement(2, MovementDirection.Out, 50m, new DateTime(2026, 5, 3), reference: "SALE-OUT"));
        await db.SaveChangesAsync();

        var finding = Assert.Single(await new NegativeStockAnalysisService(db)
            .AnalyzeAsync(new ManagementReportFilterViewModel()));

        Assert.Equal(NegativeStockStatus.Open, finding.Status);
        Assert.Equal(new DateTime(2026, 5, 3), finding.FirstNegativeDate);
        Assert.Equal(-20m, finding.FirstNegativeBalanceMt);
        Assert.Equal(-20m, finding.ClosingBalanceMt);
        Assert.Equal(2, finding.CausingMovementId);
        Assert.Equal("SALE-OUT", finding.CausingMovementReference);
        Assert.Contains("کسری واقعی", finding.ProbableCause);
    }

    [Fact]
    public async Task A_Dip_That_A_Later_Receipt_Heals_Is_Classified_As_Legacy_Date_Ordering()
    {
        await using var db = NewDb();
        SeedReferences(db);
        db.InventoryMovements.AddRange(
            Movement(1, MovementDirection.Out, 50m, new DateTime(2026, 5, 2)),
            // رسیدی که دیرتر (با تاریخ بعدی) ثبت شده و مانده را جبران می‌کند.
            Movement(2, MovementDirection.In, 80m, new DateTime(2026, 5, 5)));
        await db.SaveChangesAsync();

        var finding = Assert.Single(await new NegativeStockAnalysisService(db)
            .AnalyzeAsync(new ManagementReportFilterViewModel()));

        Assert.Equal(NegativeStockStatus.HealedLegacy, finding.Status);
        Assert.Equal(-50m, finding.FirstNegativeBalanceMt);
        Assert.Equal(30m, finding.ClosingBalanceMt);
        Assert.Contains("Legacy backdating", finding.ProbableCause);
    }

    [Fact]
    public async Task A_Scope_With_No_Inbound_At_All_Points_At_A_Wrong_Scope()
    {
        await using var db = NewDb();
        SeedReferences(db);
        db.InventoryMovements.Add(Movement(1, MovementDirection.Out, 10m, new DateTime(2026, 5, 2)));
        await db.SaveChangesAsync();

        var finding = Assert.Single(await new NegativeStockAnalysisService(db)
            .AnalyzeAsync(new ManagementReportFilterViewModel()));

        Assert.Equal(NegativeStockStatus.Open, finding.Status);
        Assert.Contains("scope خروج اشتباه", finding.ProbableCause);
    }

    [Fact]
    public async Task Transfer_Counts_As_An_Outgoing_Movement()
    {
        await using var db = NewDb();
        SeedReferences(db);
        db.InventoryMovements.AddRange(
            Movement(1, MovementDirection.In, 10m, new DateTime(2026, 5, 1)),
            Movement(2, MovementDirection.Transfer, 25m, new DateTime(2026, 5, 2)));
        await db.SaveChangesAsync();

        var finding = Assert.Single(await new NegativeStockAnalysisService(db)
            .AnalyzeAsync(new ManagementReportFilterViewModel()));

        Assert.Equal(-15m, finding.ClosingBalanceMt);
    }

    private static ApplicationDbContext NewDb()
        => new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static void SeedReferences(ApplicationDbContext db)
    {
        db.Companies.Add(new Company { Id = 1, Code = "PTG", Name = "PTG" });
        db.Products.Add(new Product { Id = 1, Code = "GO", Name = "Gas Oil" });
        db.Terminals.Add(new Terminal { Id = 1, Code = "T1", Name = "Terminal 1" });
        db.Contracts.Add(new Contract
        {
            Id = 1,
            ContractNumber = "PUR-001",
            ContractType = ContractType.Purchase,
            CompanyId = 1,
            ProductId = 1,
            ContractDate = new DateTime(2026, 1, 1),
            QuantityMt = 500m,
            PricingMethod = PricingMethod.Fixed
        });
    }

    private static InventoryMovement Movement(
        int id,
        MovementDirection direction,
        decimal quantityMt,
        DateTime movementDate,
        string? reference = null)
        => new()
        {
            Id = id,
            ProductId = 1,
            ContractId = 1,
            TerminalId = 1,
            Direction = direction,
            MovementDate = movementDate,
            QuantityMt = quantityMt,
            ReferenceDocument = reference
        };
}
