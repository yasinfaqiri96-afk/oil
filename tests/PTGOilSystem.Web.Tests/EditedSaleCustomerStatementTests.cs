using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.PartyStatements;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.CompanyFlow;
using PTGOilSystem.Web.Services.Parties;
using PTGOilSystem.Web.Services.PartyStatements;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// ویرایش مقدار فروش سطر قبلی را برگشت می‌زند و سطر تازه با مرجع «فاکتور/E{n}» ثبت می‌کند.
/// صورت‌حساب مشتری فقط ثبتِ جاری را نشان می‌دهد و مانده دقیقاً همان می‌ماند.
/// </summary>
public class EditedSaleCustomerStatementTests
{
    [Fact]
    public async Task Customer_Statement_Shows_Only_The_Current_Posting_Of_An_Edited_Sale()
    {
        await using var db = NewDb();
        Seed(db, edited: true);

        var statement = await BuildStatementAsync(db);

        var row = Assert.Single(statement.Rows.Where(r => !r.IsOpeningBalance));
        Assert.Equal(13_300m, row.ReceiptBase ?? row.OutflowBase);
        Assert.Equal(13_300m, Math.Abs(statement.Summary.ClosingBalance));
    }

    [Fact]
    public async Task Unedited_Sale_With_Plain_Second_Posting_Is_Not_Hidden()
    {
        await using var db = NewDb();
        Seed(db, edited: false);

        var statement = await BuildStatementAsync(db);

        Assert.Single(statement.Rows.Where(r => !r.IsOpeningBalance));
        Assert.Equal(14_560m, Math.Abs(statement.Summary.ClosingBalance));
    }

    private static ApplicationDbContext NewDb()
        => new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static Task<PartyStatementResult> BuildStatementAsync(ApplicationDbContext db)
        => new PartyStatementReadService(
                db,
                new PartyStatementPolicyResolver(),
                new CompanyFlowDirectionResolver(),
                new CompanyFlowBalanceService(),
                Options.Create(new PartyStatementOptions()),
                new PartyDirectory(db))
            .GetStatementAsync(
                new PartyRef(PartyStatementPartyType.Customer, 1),
                new PartyStatementFilter());

    private static void Seed(ApplicationDbContext db, bool edited)
    {
        db.Customers.Add(new Customer { Id = 1, Name = "Customer A" });
        db.Products.Add(new Product { Id = 1, Code = "GO", Name = "Gas Oil" });
        db.SalesTransactions.Add(new SalesTransaction
        {
            Id = 3,
            CustomerId = 1,
            ProductId = 1,
            SaleStage = SaleStage.TerminalStock,
            InvoiceNumber = "INV001",
            SaleDate = new DateTime(2026, 9, 28),
            QuantityMt = edited ? 9.5m : 10.4m,
            Currency = "USD",
            UnitPriceInCurrency = 1_400m,
            AppliedFxRateToUsd = 1m,
            UnitPriceUsd = 1_400m,
            TotalInCurrency = edited ? 13_300m : 14_560m,
            TotalUsd = edited ? 13_300m : 14_560m
        });
        db.LedgerEntries.Add(SaleRow(13, LedgerSide.Credit, 14_560m, "INV001"));
        if (edited)
        {
            db.LedgerEntries.Add(SaleRow(45, LedgerSide.Debit, 14_560m, "INV001" + CompanyFlowSourceTypes.ReversalReferenceSuffix));
            db.LedgerEntries.Add(SaleRow(46, LedgerSide.Credit, 13_300m, "INV001/E1"));
        }
        db.SaveChanges();
    }

    private static LedgerEntry SaleRow(int id, LedgerSide side, decimal amountUsd, string reference) => new()
    {
        Id = id,
        EntryDate = new DateTime(2026, 9, 28),
        Side = side,
        AmountUsd = amountUsd,
        Currency = "USD",
        SourceCurrencyCode = "USD",
        SourceAmount = amountUsd,
        AppliedFxRateToUsd = 1m,
        Description = "ثبت فروش",
        Reference = reference,
        SourceType = "Sale",
        SourceId = 3,
        CustomerId = 1
    };
}
