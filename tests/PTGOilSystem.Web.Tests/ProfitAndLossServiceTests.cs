using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.Reports;
using PTGOilSystem.Web.Services.Reporting;
using Xunit;

namespace PTGOilSystem.Web.Tests;

public class ProfitAndLossServiceTests
{
    [Fact]
    public async Task Contract_Pnl_Uses_Only_Active_Historical_Cost_And_Flags_Missing_Cost()
    {
        await using var db = NewDb();
        SeedReferences(db);
        db.SalesTransactions.AddRange(
            Sale(1, 1_000m),
            Sale(2, 500m),
            Sale(3, 999m, cancelled: true));
        db.SalesCostConsumptions.AddRange(
            Cost(1, 600m, SalesCostConsumptionStatus.Active),
            Cost(2, 400m, SalesCostConsumptionStatus.Reversed));
        db.JournalEntries.Add(new JournalEntry
        {
            Id = 1,
            CompanyId = 1,
            FiscalYearId = 1,
            FiscalPeriodId = 1,
            JournalNumber = "J-DUPLICATE-1",
            Status = JournalEntryStatus.Posted,
            AccountingDate = new DateTime(2026, 5, 10),
            DocumentDate = new DateTime(2026, 5, 10),
            OperationDate = new DateTime(2026, 5, 10),
            SourceModule = "Sales",
            SourceEntityType = nameof(SalesTransaction),
            SourceEntityId = 1
        });
        await db.SaveChangesAsync();

        var service = new ProfitAndLossService(db);
        var snapshot = (await service.BuildForSaleContractsAsync([1]))[1];

        Assert.Equal(1_500m, snapshot.RevenueUsd);
        Assert.Equal(600m, snapshot.CostOfGoodsSoldUsd);
        Assert.Equal(900m, snapshot.GrossProfitUsd);
        Assert.Equal(1, snapshot.UncostedSaleCount);
        Assert.Equal(PnlConfidence.NeedsReview, snapshot.Confidence);

        db.SalesCostConsumptions.Add(Cost(2, 250m, SalesCostConsumptionStatus.Active));
        await db.SaveChangesAsync();
        snapshot = (await service.BuildForSaleContractsAsync([1]))[1];

        Assert.Equal(850m, snapshot.CostOfGoodsSoldUsd);
        Assert.Equal(650m, snapshot.GrossProfitUsd);
        Assert.Equal(0, snapshot.UncostedSaleCount);
        Assert.Equal(PnlConfidence.Verified, snapshot.Confidence);
    }

    [Fact]
    public async Task Company_Pnl_Applies_Date_Filter_And_Excludes_Cancelled_Expenses()
    {
        await using var db = NewDb();
        SeedReferences(db);
        db.SalesTransactions.AddRange(
            Sale(1, 1_000m, date: new DateTime(2026, 5, 10)),
            Sale(2, 700m, date: new DateTime(2026, 4, 10)));
        db.SalesCostConsumptions.AddRange(
            Cost(1, 600m, SalesCostConsumptionStatus.Active),
            Cost(2, 300m, SalesCostConsumptionStatus.Active));
        db.ExpenseTransactions.AddRange(
            Expense(1, 100m, new DateTime(2026, 5, 12)),
            Expense(2, 80m, new DateTime(2026, 5, 13), cancelled: true),
            Expense(3, 90m, new DateTime(2026, 4, 12)));
        db.SarrafSettlements.AddRange(
            new SarrafSettlement
            {
                Id = 1, SarrafId = 1, SettlementDate = new DateTime(2026, 5, 14),
                Status = SarrafSettlementStatus.Posted,
                DifferenceType = SarrafSettlementDifferenceType.Gain,
                DifferenceAmountUsd = 50m
            },
            new SarrafSettlement
            {
                Id = 2, SarrafId = 1, SettlementDate = new DateTime(2026, 5, 15),
                Status = SarrafSettlementStatus.Posted,
                DifferenceType = SarrafSettlementDifferenceType.Loss,
                DifferenceAmountUsd = 20m
            });
        await db.SaveChangesAsync();

        var snapshot = await new ProfitAndLossService(db).BuildCompanyAsync(new ManagementReportFilterViewModel
        {
            FromDate = new DateTime(2026, 5, 1),
            ToDate = new DateTime(2026, 5, 31)
        });

        Assert.Equal(1_000m, snapshot.Sales.RevenueUsd);
        Assert.Equal(600m, snapshot.Sales.CostOfGoodsSoldUsd);
        Assert.Equal(100m, snapshot.OperatingExpenseUsd);
        Assert.Equal(50m, snapshot.ExchangeGainUsd);
        Assert.Equal(20m, snapshot.ExchangeLossUsd);
        Assert.Equal(330m, snapshot.NetProfitUsd);
        Assert.Equal(PnlConfidence.Verified, snapshot.Sales.Confidence);
    }

    private static ApplicationDbContext NewDb()
        => new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static void SeedReferences(ApplicationDbContext db)
    {
        db.Companies.Add(new Company { Id = 1, Code = "PTG", Name = "PTG" });
        db.Customers.Add(new Customer { Id = 1, Name = "Customer" });
        db.Products.Add(new Product { Id = 1, Code = "GO", Name = "Gas Oil" });
        db.Contracts.Add(new Contract
        {
            Id = 1,
            ContractNumber = "SALE-001",
            ContractType = ContractType.Sale,
            CompanyId = 1,
            CustomerId = 1,
            ProductId = 1,
            ContractDate = new DateTime(2026, 1, 1),
            QuantityMt = 100m,
            PricingMethod = PricingMethod.Fixed
        });
    }

    private static SalesTransaction Sale(
        int id,
        decimal totalUsd,
        bool cancelled = false,
        DateTime? date = null)
        => new()
        {
            Id = id,
            ContractId = 1,
            CompanyId = 1,
            CustomerId = 1,
            ProductId = 1,
            InvoiceNumber = $"INV-{id}",
            SaleDate = date ?? new DateTime(2026, 5, 10),
            QuantityMt = 1m,
            TotalUsd = totalUsd,
            TotalInCurrency = totalUsd,
            Currency = "USD",
            IsCancelled = cancelled
        };

    private static SalesCostConsumption Cost(
        int saleId,
        decimal amount,
        SalesCostConsumptionStatus status)
        => new()
        {
            SalesTransactionId = saleId,
            CompanyId = 1,
            ProductId = 1,
            TerminalId = 1,
            QuantityMt = 1m,
            CostUsd = amount,
            Status = status
        };

    private static ExpenseTransaction Expense(int id, decimal amount, DateTime date, bool cancelled = false)
        => new()
        {
            Id = id,
            ExpenseTypeId = 1,
            ExpenseDate = date,
            Amount = amount,
            AmountUsd = amount,
            Currency = "USD",
            IsCancelled = cancelled
        };
}
