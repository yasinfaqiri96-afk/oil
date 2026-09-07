using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Controllers;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.Reports;
using PTGOilSystem.Web.Services;
using Xunit;

namespace PTGOilSystem.Web.Tests;

public class ReceivablesPayablesFxReportTests
{
    [Fact]
    public async Task Equal_Rub_Debt_And_Payment_Does_Not_Show_A_False_Supplier_Receivable()
    {
        await using var db = NewDb();
        SeedEqualRubScenario(db, includeRecognizedDifference: false);
        await db.SaveChangesAsync();

        var model = Assert.IsType<ReceivablesPayablesReportViewModel>(
            Assert.IsType<ViewResult>(await new ReportsController(db)
                .ReceivablesPayables(new ManagementReportFilterViewModel())).Model);

        var supplier = Assert.Single(model.Rows.Where(row => row.PartyType == "Supplier"));
        Assert.Equal(-62_500m, supplier.FxAdjustmentUsd);
        Assert.Equal(0m, supplier.BalanceUsd);
        Assert.Equal(0m, model.SupplierPayableUsd);
    }

    [Fact]
    public async Task Posted_Fx_Difference_Is_Not_Applied_Twice()
    {
        await using var db = NewDb();
        SeedEqualRubScenario(db, includeRecognizedDifference: true);
        await db.SaveChangesAsync();

        var model = Assert.IsType<ReceivablesPayablesReportViewModel>(
            Assert.IsType<ViewResult>(await new ReportsController(db)
                .ReceivablesPayables(new ManagementReportFilterViewModel())).Model);

        var supplier = Assert.Single(model.Rows.Where(row => row.PartyType == "Supplier"));
        Assert.Equal(0m, supplier.FxAdjustmentUsd);
        Assert.Equal(0m, supplier.BalanceUsd);
    }

    private static ApplicationDbContext NewDb()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ApplicationDbContext(options);
    }

    private static void SeedEqualRubScenario(ApplicationDbContext db, bool includeRecognizedDifference)
    {
        db.Suppliers.Add(new Supplier { Id = 1, Name = "Supplier A" });
        db.Contracts.AddRange(
            new Contract
            {
                Id = 1,
                ContractNumber = "RUB-70",
                ContractType = ContractType.Purchase,
                SupplierId = 1,
                Currency = "RUB"
            },
            new Contract
            {
                Id = 2,
                ContractNumber = "RUB-80",
                ContractType = ContractType.Purchase,
                SupplierId = 1,
                Currency = "RUB"
            });

        db.LedgerEntries.AddRange(
            RubEntry(1, new DateTime(2026, 9, 1), LedgerSide.Credit, 500_000m, 35_000_000m, "Loading", 1, 1),
            RubEntry(2, new DateTime(2026, 9, 2), LedgerSide.Credit, 437_500m, 35_000_000m, "Loading", 2, 2),
            RubEntry(3, new DateTime(2026, 9, 3), LedgerSide.Debit, 1_000_000m, 70_000_000m, "SupplierPayment", 3, null));

        if (includeRecognizedDifference)
        {
            db.LedgerEntries.Add(new LedgerEntry
            {
                Id = 4,
                EntryDate = new DateTime(2026, 9, 3),
                Side = LedgerSide.Credit,
                AmountUsd = 62_500m,
                Currency = "USD",
                SourceAmount = 62_500m,
                SourceCurrencyCode = "USD",
                SupplierId = 1,
                SourceType = SupplierFxRecognitionService.PartyLedgerSourceType,
                SourceId = 1
            });
        }
    }

    private static LedgerEntry RubEntry(
        int id,
        DateTime date,
        LedgerSide side,
        decimal amountUsd,
        decimal sourceAmount,
        string sourceType,
        int sourceId,
        int? contractId)
        => new()
        {
            Id = id,
            EntryDate = date,
            Side = side,
            AmountUsd = amountUsd,
            Currency = "USD",
            SourceAmount = sourceAmount,
            SourceCurrencyCode = "RUB",
            AppliedFxRateToUsd = amountUsd / sourceAmount,
            SupplierId = 1,
            ContractId = contractId,
            SourceType = sourceType,
            SourceId = sourceId
        };
}
