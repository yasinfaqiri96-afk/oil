using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Controllers;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Exceptions;
using Xunit;

namespace PTGOilSystem.Web.Tests;

public sealed class ViaSarrafLegacyGroupingServiceTests
{
    private static ApplicationDbContext NewDb()
        => new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static LedgerEntry Payment(int sarrafId, decimal amount, decimal usd, string? reference = null, int supplierId = 1)
        => new()
        {
            EntryDate = new DateTime(2026, 7, 1),
            Side = LedgerSide.Debit,
            AmountUsd = usd,
            SourceAmount = amount,
            SourceCurrencyCode = "RUB",
            SourceType = PaymentsController.ViaSarrafSupplierLedgerSourceType,
            SourceId = sarrafId,
            SupplierId = supplierId,
            Reference = reference,
            Description = "p"
        };

    private static LedgerEntry Payable(int sarrafId, decimal amount, decimal usd, string? reference = null)
        => new()
        {
            EntryDate = new DateTime(2026, 7, 1),
            Side = LedgerSide.Credit,
            AmountUsd = usd,
            SourceAmount = amount,
            SourceCurrencyCode = "RUB",
            SourceType = PaymentsController.ViaSarrafPayableLedgerSourceType,
            SourceId = sarrafId,
            Reference = reference,
            Description = "q"
        };

    [Fact] // Test 5: a clean 1:1 match is deterministic.
    public async Task Analyze_Finds_Deterministic_Pair()
    {
        await using var db = NewDb();
        db.LedgerEntries.AddRange(Payment(5, 1000m, 12m, "R1"), Payable(5, 1000m, 12m, "R1"));
        db.SaveChanges();

        var report = await new ViaSarrafLegacyGroupingService(db).AnalyzeAsync();

        Assert.Single(report.DeterministicPairs);
        Assert.Empty(report.Ambiguous);
        Assert.Empty(report.Unpaired);
    }

    [Fact] // Test 6: two identical payables make the payment ambiguous.
    public async Task Analyze_Flags_Ambiguous_When_Multiple_Candidates()
    {
        await using var db = NewDb();
        db.LedgerEntries.AddRange(
            Payment(5, 1000m, 12m, "R1"),
            Payable(5, 1000m, 12m, "R1"),
            Payable(5, 1000m, 12m, "R1"));
        db.SaveChanges();

        var report = await new ViaSarrafLegacyGroupingService(db).AnalyzeAsync();

        Assert.Empty(report.DeterministicPairs);
        Assert.NotEmpty(report.Ambiguous);
    }

    [Fact] // a payment with no matching payable is unpaired.
    public async Task Analyze_Flags_Unpaired()
    {
        await using var db = NewDb();
        db.LedgerEntries.Add(Payment(5, 1000m, 12m, "R1"));
        db.SaveChanges();

        var report = await new ViaSarrafLegacyGroupingService(db).AnalyzeAsync();

        Assert.Empty(report.DeterministicPairs);
        Assert.NotEmpty(report.Unpaired);
    }

    [Fact] // Test 7: apply groups only the deterministic pair; ambiguous rows stay ungrouped.
    public async Task Apply_Groups_Only_Deterministic_And_Leaves_Ambiguous_Untouched()
    {
        await using var db = NewDb();
        // deterministic pair for sarraf 5
        db.LedgerEntries.AddRange(Payment(5, 1000m, 12m, "A"), Payable(5, 1000m, 12m, "A"));
        // ambiguous set for sarraf 9
        db.LedgerEntries.AddRange(Payment(9, 500m, 6m, "B"), Payable(9, 500m, 6m, "B"), Payable(9, 500m, 6m, "B"));
        db.SaveChanges();

        var result = await new ViaSarrafLegacyGroupingService(db).ApplyConfirmedMatchesAsync();

        Assert.Equal(1, result.GroupsCreated);
        Assert.Equal(2, result.RowsUpdated);

        var grouped = await db.LedgerEntries.Where(l => l.ViaSarrafGroupId != null).ToListAsync();
        Assert.Equal(2, grouped.Count);
        Assert.All(grouped, g => Assert.Equal(5, g.SourceId));
        Assert.Single(grouped.Select(g => g.ViaSarrafGroupId).Distinct());

        var ambiguousUngrouped = await db.LedgerEntries.Where(l => l.SourceId == 9 && l.ViaSarrafGroupId == null).CountAsync();
        Assert.Equal(3, ambiguousUngrouped);
    }

    [Fact] // Test 8: manual pairing assigns a shared group id to the two chosen rows.
    public async Task ManualPair_Assigns_Shared_Group()
    {
        await using var db = NewDb();
        db.LedgerEntries.AddRange(Payment(9, 500m, 6m, "B"), Payable(9, 500m, 6m, "B"), Payable(9, 500m, 6m, "B"));
        db.SaveChanges();
        var payment = db.LedgerEntries.Single(l => l.SourceType == PaymentsController.ViaSarrafSupplierLedgerSourceType);
        var payable = db.LedgerEntries.First(l => l.SourceType == PaymentsController.ViaSarrafPayableLedgerSourceType);

        var groupId = await new ViaSarrafLegacyGroupingService(db)
            .ManualPairAsync(payment.Id, payable.Id, "amounts match", 7, "tester");

        Assert.NotEqual(Guid.Empty, groupId);
        var p = await db.LedgerEntries.FindAsync(payment.Id);
        var q = await db.LedgerEntries.FindAsync(payable.Id);
        Assert.Equal(groupId, p!.ViaSarrafGroupId);
        Assert.Equal(groupId, q!.ViaSarrafGroupId);
    }

    [Fact] // manual pairing refuses two rows of the same source type.
    public async Task ManualPair_Rejects_Same_Source_Type()
    {
        await using var db = NewDb();
        db.LedgerEntries.AddRange(Payment(9, 500m, 6m), Payment(9, 500m, 6m));
        db.SaveChanges();
        var ids = db.LedgerEntries.Select(l => l.Id).ToList();

        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() =>
            new ViaSarrafLegacyGroupingService(db).ManualPairAsync(ids[0], ids[1], "r", null, null));
        Assert.Equal("VIA_SARRAF_MANUAL_PAIR_SOURCE_TYPE_MISMATCH", ex.Code);
    }

    [Fact] // manual pairing refuses rows from different sarrafs.
    public async Task ManualPair_Rejects_Different_Sarraf()
    {
        await using var db = NewDb();
        db.LedgerEntries.AddRange(Payment(5, 500m, 6m), Payable(9, 500m, 6m));
        db.SaveChanges();
        var payment = db.LedgerEntries.Single(l => l.SourceType == PaymentsController.ViaSarrafSupplierLedgerSourceType);
        var payable = db.LedgerEntries.Single(l => l.SourceType == PaymentsController.ViaSarrafPayableLedgerSourceType);

        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() =>
            new ViaSarrafLegacyGroupingService(db).ManualPairAsync(payment.Id, payable.Id, "r", null, null));
        Assert.Equal("VIA_SARRAF_MANUAL_PAIR_SARRAF_MISMATCH", ex.Code);
    }
}
