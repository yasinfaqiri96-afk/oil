using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PTGOilSystem.Web.Configuration;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services.Accounting;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// Stage 4 — the non-cash "payment via sarraf" flow: the sarraf pays the supplier for us, so
/// the supplier claim goes down and the sarraf becomes our creditor. Cash must never appear.
/// </summary>
[Collection(AccountingPostgreSqlCollection.CollectionName)]
[Trait("Category", "PostgreSql")]
[Trait("Category", "Integration")]
public sealed class ViaSarrafAccountingAdapterTests(AccountingPostgreSqlFixture fixture)
{
    private static readonly DateTime PaymentDate = new(2026, 7, 15);

    [Fact]
    public void SourceEventId_Format_Is_Stable()
        => Assert.Equal(
            "ViaSarrafSupplierPayment:7:Created",
            ViaSarrafAccountingAdapter.BuildCreatedSourceEventId(7));

    [Fact]
    public async Task Posts_Supplier_Payable_Down_And_Sarraf_Payable_Up_Without_Touching_Cash()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var ledgerEntryId = await AddSupplierLedgerAsync(db, scope, amountUsd: 500m);

        var result = await CreateAdapter(db, pilotEnabled: true).TryPostSupplierPaymentAsync(
            NewEvent(scope, ledgerEntryId, amount: 500m, amountUsd: 500m));

        Assert.Equal(PaymentPostingStatus.Posted, result.Status);

        var journal = await LoadJournalAsync(db, ledgerEntryId);
        Assert.Equal(JournalEntryStatus.Posted, journal.Status);
        Assert.Equal(scope.Company.Id, journal.CompanyId);
        Assert.Equal(2, journal.Lines.Count);

        var debitLine = journal.Lines.Single(x => x.Debit > 0m);
        var creditLine = journal.Lines.Single(x => x.Credit > 0m);
        var payableAccountId = scope.Settings.AccountsPayableAccountId;

        Assert.Equal(payableAccountId, debitLine.AccountId);
        Assert.Equal(payableAccountId, creditLine.AccountId);
        Assert.Equal(AccountingPartyType.Supplier, debitLine.PartyType);
        Assert.Equal(scope.Supplier.Id, debitLine.PartyId);
        Assert.Equal(AccountingPartyType.Sarraf, creditLine.PartyType);
        Assert.Equal(scope.Sarraf.Id, creditLine.PartyId);
        Assert.Equal(scope.Contract.Id, debitLine.ContractId);
        Assert.Equal(scope.Contract.Id, creditLine.ContractId);

        // No cash moved in this flow, so no line may reference a cash account.
        Assert.All(journal.Lines, line => Assert.Null(line.CashAccountId));
        Assert.Equal(500m, journal.Lines.Sum(x => x.Debit));
        Assert.Equal(500m, journal.Lines.Sum(x => x.Credit));
    }

    [Fact]
    public async Task Keeps_Legacy_Only_When_Pilot_Is_Disabled()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var ledgerEntryId = await AddSupplierLedgerAsync(db, scope, amountUsd: 500m);

        var result = await CreateAdapter(db, pilotEnabled: false).TryPostSupplierPaymentAsync(
            NewEvent(scope, ledgerEntryId, amount: 500m, amountUsd: 500m));

        await AssertSkippedAsync(db, ledgerEntryId, result, "PILOT_DISABLED");
    }

    [Fact]
    public async Task Skips_When_No_Contract_Proves_The_Company()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var ledgerEntryId = await AddSupplierLedgerAsync(db, scope, amountUsd: 500m);

        var result = await CreateAdapter(db, pilotEnabled: true).TryPostSupplierPaymentAsync(
            NewEvent(scope, ledgerEntryId, amount: 500m, amountUsd: 500m) with { ContractId = null });

        await AssertSkippedAsync(db, ledgerEntryId, result, "PAYMENT_COMPANY_UNKNOWN");
    }

    [Fact]
    public async Task Skips_Non_Usd_Because_The_Legacy_Rate_Cannot_Reproduce_AmountUsd()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db, paymentCurrency: "RUB");
        var ledgerEntryId = await AddSupplierLedgerAsync(db, scope, amountUsd: 100m);

        // Legacy derives AmountUsd by dividing (9200 / 92 = 100.0000) while storing a rate
        // rounded to six places, so Amount x rate = 100.0040 and the pair cannot reproduce it.
        var result = await CreateAdapter(db, pilotEnabled: true).TryPostSupplierPaymentAsync(
            NewEvent(scope, ledgerEntryId, amount: 9_200m, amountUsd: 100m) with
            {
                Currency = "RUB",
                FxRateToUsd = decimal.Round(1m / 92m, 6, MidpointRounding.AwayFromZero)
            });

        await AssertSkippedAsync(db, ledgerEntryId, result, "INVALID_PAYMENT_CONVERSION");
    }

    [Fact]
    public async Task Duplicate_Source_Event_Does_Not_Create_A_Second_Journal()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var ledgerEntryId = await AddSupplierLedgerAsync(db, scope, amountUsd: 500m);
        var adapter = CreateAdapter(db, pilotEnabled: true);
        var paymentEvent = NewEvent(scope, ledgerEntryId, amount: 500m, amountUsd: 500m);

        Assert.Equal(
            PaymentPostingStatus.Posted,
            (await adapter.TryPostSupplierPaymentAsync(paymentEvent)).Status);
        var second = await adapter.TryPostSupplierPaymentAsync(paymentEvent);

        Assert.Equal(PaymentPostingStatus.Duplicate, second.Status);
        Assert.Equal("DUPLICATE_SOURCE_EVENT", second.Reason);
        Assert.Equal(1, await db.JournalEntries.CountAsync(
            x => x.SourceEventId == ViaSarrafAccountingAdapter.BuildCreatedSourceEventId(ledgerEntryId)));
    }

    private static async Task AssertSkippedAsync(
        ApplicationDbContext db,
        int ledgerEntryId,
        ViaSarrafAccountingResult result,
        string expectedReason)
    {
        Assert.Equal(PaymentPostingStatus.Skipped, result.Status);
        Assert.Equal(expectedReason, result.Reason);
        Assert.Null(result.Journal);
        Assert.Equal(0, await db.JournalEntries.CountAsync(
            x => x.SourceEventId == ViaSarrafAccountingAdapter.BuildCreatedSourceEventId(ledgerEntryId)));
    }

    private static async Task<JournalEntry> LoadJournalAsync(ApplicationDbContext db, int ledgerEntryId)
        => await db.JournalEntries
            .AsNoTracking()
            .Include(x => x.Lines)
            .SingleAsync(x => x.SourceModule == ViaSarrafAccountingAdapter.SourceModule
                && x.SourceEventId == ViaSarrafAccountingAdapter.BuildCreatedSourceEventId(ledgerEntryId));

    private static ViaSarrafSupplierPaymentEvent NewEvent(
        PaymentAccountingAdapterTests.PaymentScope scope,
        int supplierLedgerEntryId,
        decimal amount,
        decimal amountUsd)
        => new(
            supplierLedgerEntryId,
            scope.Supplier.Id,
            scope.Sarraf.Id,
            scope.Contract.Id,
            PaymentDate,
            "USD",
            amount,
            amountUsd,
            1m);

    private static async Task<int> AddSupplierLedgerAsync(
        ApplicationDbContext db,
        PaymentAccountingAdapterTests.PaymentScope scope,
        decimal amountUsd)
    {
        var ledger = new LedgerEntry
        {
            EntryDate = PaymentDate,
            Side = LedgerSide.Debit,
            AmountUsd = amountUsd,
            Currency = "USD",
            SourceAmount = amountUsd,
            SourceCurrencyCode = "USD",
            AppliedFxRateToUsd = 1m,
            Description = "Via sarraf supplier payment",
            SourceType = "SupplierViaSarrafPayment",
            SourceId = scope.Sarraf.Id,
            SupplierId = scope.Supplier.Id,
            ContractId = scope.Contract.Id
        };
        db.LedgerEntries.Add(ledger);
        await db.SaveChangesAsync();
        return ledger.Id;
    }

    private static ViaSarrafAccountingAdapter CreateAdapter(ApplicationDbContext db, bool pilotEnabled)
    {
        var options = Options.Create(new AccountingOptions
        {
            Enabled = true,
            Pilots = new AccountingPilotOptions { SarrafPayment = pilotEnabled }
        });
        return new ViaSarrafAccountingAdapter(
            db,
            new AccountingPostingService(db, new PeriodGuard(db, new FiscalCalendarService(db)), options, new SystemCompanyProvider(db)),
            new AccountingJournalNumberGenerator(),
            options,
            NullLogger<ViaSarrafAccountingAdapter>.Instance);
    }
}
