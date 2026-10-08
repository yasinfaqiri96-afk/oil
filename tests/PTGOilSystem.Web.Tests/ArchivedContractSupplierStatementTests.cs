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
/// قرارداد لغوشده‌ای که از فهرست حذف (آرشیف) شده، با جفت سند ثبت/برگشتِ صفرشده‌اش در صورت‌حساب
/// تأمین‌کننده نمی‌آید؛ ماندهٔ تأمین‌کننده دقیقاً همان می‌ماند.
/// </summary>
public class ArchivedContractSupplierStatementTests
{
    [Fact]
    public async Task Supplier_Statement_Hides_Archived_Contract_Rows_And_Keeps_Balance()
    {
        await using var db = NewDb();
        Seed(db);

        var statement = await BuildStatementAsync(db);

        var row = Assert.Single(statement.Rows.Where(r => !r.IsOpeningBalance));
        Assert.Equal(1, row.ContractId);
        Assert.Equal(-1_000m, statement.Summary.ClosingBalance);
    }

    [Fact]
    public async Task Same_Rows_Stay_Visible_While_Contract_Is_Not_Archived()
    {
        await using var db = NewDb();
        Seed(db);
        var cancelled = await db.Contracts.SingleAsync(c => c.Id == 2);
        cancelled.IsArchived = false;
        await db.SaveChangesAsync();

        var statement = await BuildStatementAsync(db);

        Assert.Equal(3, statement.Rows.Count(r => !r.IsOpeningBalance));
        Assert.Equal(-1_000m, statement.Summary.ClosingBalance);
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
                new PartyRef(PartyStatementPartyType.Supplier, 1),
                new PartyStatementFilter());

    private static void Seed(ApplicationDbContext db)
    {
        db.Suppliers.Add(new Supplier { Id = 1, Name = "Supplier A", IsActive = true });
        db.Contracts.Add(NewContract(1, "P-001", ContractStatus.Active, isArchived: false));
        db.Contracts.Add(NewContract(2, "P-002", ContractStatus.Cancelled, isArchived: true));
        // همان شکل دادهٔ واقعیِ لغو: سطر برگشت مرجع «-CANCEL» دارد.
        db.LedgerEntries.AddRange(
            NewLoadingEntry(contractId: 1, sourceId: 10, LedgerSide.Credit, 1_000m, "LOAD-10"),
            NewLoadingEntry(contractId: 2, sourceId: 20, LedgerSide.Credit, 500m, "LOAD-20"),
            NewLoadingEntry(contractId: 2, sourceId: 20, LedgerSide.Debit, 500m, "LOAD-20-CANCEL"));
        db.SaveChanges();
    }

    private static Contract NewContract(int id, string number, ContractStatus status, bool isArchived) => new()
    {
        Id = id,
        ContractNumber = number,
        ContractType = ContractType.Purchase,
        Status = status,
        IsArchived = isArchived,
        SupplierId = 1,
        ContractDate = new DateTime(2026, 6, 1),
        QuantityMt = 10m,
        PricingMethod = PricingMethod.Fixed,
        UnitPriceUsd = 100m,
        SettlementCurrencyCode = "USD",
        RubRatePolicy = RubSettlementRatePolicy.NotApplicable
    };

    private static LedgerEntry NewLoadingEntry(int contractId, int sourceId, LedgerSide side, decimal amountUsd, string reference) => new()
    {
        EntryDate = new DateTime(2026, 6, 2),
        Side = side,
        AmountUsd = amountUsd,
        Currency = "USD",
        SourceCurrencyCode = "USD",
        AppliedFxRateToUsd = 1m,
        Description = "بدهی تأمین‌کننده بابت بارگیری",
        Reference = reference,
        SourceType = "Loading",
        SourceId = sourceId,
        ContractId = contractId,
        SupplierId = 1
    };
}
