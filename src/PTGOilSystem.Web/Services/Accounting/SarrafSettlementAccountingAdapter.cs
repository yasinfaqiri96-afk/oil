using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PTGOilSystem.Web.Configuration;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services.Time;

namespace PTGOilSystem.Web.Services.Accounting;

public sealed record SarrafSettlementAccountingResult(
    PaymentPostingStatus Status,
    JournalEntry? Journal,
    string? Reason);

public interface ISarrafSettlementAccountingAdapter
{
    Task<SarrafSettlementAccountingResult> TryPostSettlementAsync(
        SarrafSettlement settlement,
        CancellationToken cancellationToken = default);

    Task<SarrafSettlementAccountingResult> TryPostSettlementReversalAsync(
        SarrafSettlement settlement,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Stage 8 dual-write pilot for sarraf settlements, where a money changer moves value between
/// the company and a counterparty and the company's balance with the sarraf is what remains.
///
///   Out (the sarraf paid the counterparty for us):
///     Dr counterparty control account   SupplierLedgerAmountUsd
///     Cr Accounts Payable, party=Sarraf SarrafChargedAmountUsd
///   In (the sarraf collected for us): the same two lines with the sides swapped.
///   Either way the gap between the two figures lands on Exchange Loss or Exchange Gain.
///
/// The counterparty line mirrors the single legacy row exactly — same amount, same source
/// currency and rate, same side. The sarraf line has no legacy counterpart at all: legacy never
/// writes a sarraf row and rebuilds the sarraf balance in memory instead. The confirmed decision
/// is that what we owe the sarraf is what the sarraf charged us, so all three amounts the
/// settlement records finally enter the books and the journal balances on its own figures.
///
/// Consequence to know before enabling the flag: this journal's gain or loss is
/// SarrafCharged − counterparty amount, whereas the legacy DifferenceAmountUsd row is
/// Requested − SupplierAccepted. They measure different gaps and will not agree. Legacy also
/// only writes its difference row under RecognizeExchangeGainLoss, while a balanced journal must
/// account for the gap every time. The log prints both so the divergence is visible per
/// settlement.
///
/// A settlement can be edited after posting, so the journal carries a revision like a repriced
/// purchase: an edit reverses every posted revision and posts the next.
/// </summary>
public sealed class SarrafSettlementAccountingAdapter(
    ApplicationDbContext db,
    IAccountingPostingService postingService,
    IAccountingJournalNumberGenerator journalNumberGenerator,
    IOptions<AccountingOptions> options,
    ILogger<SarrafSettlementAccountingAdapter> logger)
    : ISarrafSettlementAccountingAdapter
{
    public const string SourceModule = "SarrafSettlement";
    public const string SourceEntityType = nameof(SarrafSettlement);
    private const int MaxRevisions = 100;

    private readonly AccountingOptions _options = options.Value;

    public async Task<SarrafSettlementAccountingResult> TryPostSettlementAsync(
        SarrafSettlement settlement,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settlement);

        if (!_options.Enabled)
            return Skipped(settlement, 0, "ACCOUNTING_DISABLED");
        if (!_options.Pilots.SarrafSettlement)
            return Skipped(settlement, 0, "PILOT_DISABLED");

        var (companyId, skipReason) = await ResolveCompanyAndSkipReasonAsync(settlement, cancellationToken);
        if (skipReason is not null)
            return Skipped(settlement, companyId, skipReason);

        var counterparty = ResolveCounterparty(settlement);
        if (counterparty is null)
            return Skipped(settlement, companyId, "COUNTERPARTY_MISSING");

        // An edit re-posts the same settlement with new figures, so the current revision is the
        // first one not yet posted. An unchanged re-post lands on the existing revision and is a
        // duplicate, which is what makes this idempotent.
        var (revision, existing) = await ResolveRevisionAsync(companyId, settlement, cancellationToken);
        if (existing is not null)
        {
            LogOutcome(settlement, companyId, existing.Lines.Sum(x => x.Debit),
                PaymentPostingStatus.Duplicate, "DUPLICATE_SOURCE_EVENT");
            return new SarrafSettlementAccountingResult(
                PaymentPostingStatus.Duplicate, existing, "DUPLICATE_SOURCE_EVENT");
        }

        if (revision >= MaxRevisions)
            return Skipped(settlement, companyId, "TOO_MANY_REVISIONS");

        var settings = await db.AccountingSettings
            .AsNoTracking()
            .SingleAsync(x => x.CompanyId == companyId, cancellationToken);

        var counterpartyAmountUsd = SupplierLedgerAmountUsd(settlement);
        var sarrafAmountUsd = settlement.SarrafChargedAmountUsd;

        // Out means the sarraf paid on our behalf: the counterparty's claim falls and we owe the
        // sarraf. In means the sarraf collected for us: the sarraf owes us and the counterparty's
        // balance moves the other way. Customers are the mirror of everyone else because a
        // receivable and a payable move in opposite directions for the same event.
        var reducesCounterparty = settlement.CounterpartyType == SarrafSettlementCounterpartyType.Customer
            ? settlement.Direction == SarrafSettlementDirection.In
            : settlement.Direction == SarrafSettlementDirection.Out;

        var lines = new List<AccountingPostLine>
        {
            new(
                counterparty.Value.AccountId(settings),
                Debit: reducesCounterparty ? counterpartyAmountUsd : 0m,
                Credit: reducesCounterparty ? 0m : counterpartyAmountUsd,
                SupplierLedgerCurrency(settlement),
                SupplierLedgerSourceAmount(settlement),
                SupplierLedgerFxRate(settlement),
                counterparty.Value.PartyType,
                counterparty.Value.PartyId,
                ContractId: settlement.ContractId,
                Description: $"Sarraf settlement {settlement.Direction} for {counterparty.Value.PartyType}"),
            new(
                settings.AccountsPayableAccountId,
                Debit: reducesCounterparty ? 0m : sarrafAmountUsd,
                Credit: reducesCounterparty ? sarrafAmountUsd : 0m,
                settlement.SarrafCurrency,
                settlement.SarrafChargedAmount,
                settlement.SarrafFxRateToUsd,
                AccountingPartyType.Sarraf,
                settlement.SarrafId,
                ContractId: settlement.ContractId,
                Description: reducesCounterparty
                    ? "Payable to sarraf for settlement"
                    : "Receivable from sarraf for settlement")
        };

        // What the sarraf charged and what the counterparty accepted are two different figures;
        // the gap between them is the cost or benefit of routing the money through the sarraf.
        var gapUsd = decimal.Round(sarrafAmountUsd - counterpartyAmountUsd, 4, MidpointRounding.AwayFromZero);
        if (gapUsd != 0m)
        {
            var isLoss = JournalGapIsLoss(settlement, gapUsd);
            var gapAmount = Math.Abs(gapUsd);
            lines.Add(new AccountingPostLine(
                isLoss ? settings.ExchangeLossAccountId : settings.ExchangeGainAccountId,
                Debit: isLoss ? gapAmount : 0m,
                Credit: isLoss ? 0m : gapAmount,
                SystemCurrency.BaseCurrencyCode,
                gapAmount,
                1m,
                ContractId: settlement.ContractId,
                Description: isLoss ? "Sarraf settlement exchange loss" : "Sarraf settlement exchange gain"));
        }

        var request = new AccountingPostRequest(
            companyId,
            journalNumberGenerator.ForSarrafSettlement(companyId, settlement.Id, revision),
            settlement.SettlementDate.Date,
            settlement.SettlementDate.Date,
            settlement.SettlementDate.Date,
            SourceModule,
            lines,
            SourceEventId: BuildCreatedSourceEventId(settlement.Id, revision),
            SourceEntityType: SourceEntityType,
            SourceEntityId: settlement.Id,
            Description: $"Sarraf settlement #{settlement.Id} revision {revision} on {settlement.SettlementDate:yyyy-MM-dd}");

        try
        {
            var journal = await postingService.PostAsync(request, cancellationToken);
            LogOutcome(settlement, companyId, journal.Lines.Sum(x => x.Debit),
                PaymentPostingStatus.Posted, null, gapUsd);
            return new SarrafSettlementAccountingResult(PaymentPostingStatus.Posted, journal, null);
        }
        catch (Exception exception)
        {
            LogFailure(settlement, exception);
            throw;
        }
    }

    /// <summary>
    /// Reverses every posted revision, so a cancelled settlement leaves nothing behind and an
    /// edited one is fully undone before its next revision posts.
    /// </summary>
    public async Task<SarrafSettlementAccountingResult> TryPostSettlementReversalAsync(
        SarrafSettlement settlement,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settlement);

        if (!_options.Enabled)
            return Skipped(settlement, 0, "ACCOUNTING_DISABLED");
        if (!_options.Pilots.SarrafSettlement)
            return Skipped(settlement, 0, "PILOT_DISABLED");

        var companyId = await ResolveCompanyAsync(settlement, cancellationToken);
        if (companyId is null)
            return Skipped(settlement, 0, "SARRAF_COMPANY_UNKNOWN");

        JournalEntry? lastReversal = null;
        var reversedAny = false;
        var alreadyReversedAll = true;

        for (var revision = 0; revision < MaxRevisions; revision++)
        {
            var original = await FindJournalAsync(
                companyId.Value, BuildCreatedSourceEventId(settlement.Id, revision), cancellationToken);
            if (original is null)
                break;

            var reversedEventId = BuildReversedSourceEventId(settlement.Id, revision);
            var existingReversal = await FindJournalAsync(companyId.Value, reversedEventId, cancellationToken);
            if (existingReversal is not null)
            {
                lastReversal = existingReversal;
                continue;
            }

            alreadyReversedAll = false;
            var request = new AccountingReversalRequest(
                original.Id,
                journalNumberGenerator.ForSarrafSettlementReversal(companyId.Value, settlement.Id, revision),
                AfghanistanBusinessClock.SystemToday,
                SourceModule,
                reversedEventId,
                $"Reversal of sarraf settlement #{settlement.Id} revision {revision}");

            try
            {
                lastReversal = await postingService.ReverseAsync(request, cancellationToken);
                reversedAny = true;
            }
            catch (Exception exception)
            {
                LogFailure(settlement, exception);
                throw;
            }
        }

        if (lastReversal is null)
            return Skipped(settlement, companyId.Value, "ORIGINAL_JOURNAL_NOT_POSTED");

        if (!reversedAny && alreadyReversedAll)
        {
            LogOutcome(settlement, companyId.Value, lastReversal.Lines.Sum(x => x.Debit),
                PaymentPostingStatus.Duplicate, "DUPLICATE_SOURCE_EVENT");
            return new SarrafSettlementAccountingResult(
                PaymentPostingStatus.Duplicate, lastReversal, "DUPLICATE_SOURCE_EVENT");
        }

        LogOutcome(settlement, companyId.Value, lastReversal.Lines.Sum(x => x.Debit),
            PaymentPostingStatus.Posted, null);
        return new SarrafSettlementAccountingResult(PaymentPostingStatus.Posted, lastReversal, null);
    }

    public static string BuildCreatedSourceEventId(int settlementId, int revision)
        => $"SarrafSettlement:{settlementId}:Created:{revision}";

    public static string BuildReversedSourceEventId(int settlementId, int revision)
        => $"SarrafSettlement:{settlementId}:Reversed:{revision}";

    /// <summary>
    /// The amount the legacy counterparty row carries, reproduced from the settlement's own
    /// stored figures rather than recomputed: RecognizeExchangeGainLoss books the requested
    /// amount and treats the shortfall as an exchange effect, AcceptedAmountOnly books only what
    /// the counterparty accepted.
    /// </summary>
    private static decimal SupplierLedgerAmountUsd(SarrafSettlement settlement)
        => settlement.DifferenceTreatment == SarrafSettlementDifferenceTreatment.RecognizeExchangeGainLoss
            ? settlement.RequestedAmountUsd
            : settlement.SupplierAcceptedAmountUsd;

    private static decimal SupplierLedgerSourceAmount(SarrafSettlement settlement)
        => settlement.DifferenceTreatment == SarrafSettlementDifferenceTreatment.RecognizeExchangeGainLoss
            ? settlement.RequestedAmount
            : settlement.SupplierAcceptedAmount;

    private static string SupplierLedgerCurrency(SarrafSettlement settlement)
        => settlement.DifferenceTreatment == SarrafSettlementDifferenceTreatment.RecognizeExchangeGainLoss
            ? settlement.RequestedCurrency
            : settlement.SupplierAcceptedCurrency;

    private static decimal SupplierLedgerFxRate(SarrafSettlement settlement)
        => settlement.DifferenceTreatment == SarrafSettlementDifferenceTreatment.RecognizeExchangeGainLoss
            ? settlement.RequestedFxRateToUsd
            : settlement.SupplierAcceptedFxRateToUsd;

    private readonly record struct Counterparty(AccountingPartyType PartyType, int PartyId, bool IsReceivable)
    {
        public int AccountId(AccountingSettings settings)
            => IsReceivable ? settings.AccountsReceivableAccountId : settings.AccountsPayableAccountId;
    }

    private static Counterparty? ResolveCounterparty(SarrafSettlement settlement)
        => settlement.CounterpartyType switch
        {
            SarrafSettlementCounterpartyType.Supplier when settlement.SupplierId.HasValue
                => new Counterparty(AccountingPartyType.Supplier, settlement.SupplierId.Value, IsReceivable: false),
            SarrafSettlementCounterpartyType.Customer when settlement.CustomerId.HasValue
                => new Counterparty(AccountingPartyType.Customer, settlement.CustomerId.Value, IsReceivable: true),
            SarrafSettlementCounterpartyType.ServiceProvider when settlement.ServiceProviderId.HasValue
                => new Counterparty(AccountingPartyType.ServiceProvider, settlement.ServiceProviderId.Value, IsReceivable: false),
            SarrafSettlementCounterpartyType.Driver when settlement.DriverId.HasValue
                => new Counterparty(AccountingPartyType.Driver, settlement.DriverId.Value, IsReceivable: false),
            SarrafSettlementCounterpartyType.Employee when settlement.EmployeeId.HasValue
                => new Counterparty(AccountingPartyType.Employee, settlement.EmployeeId.Value, IsReceivable: false),
            _ => null
        };

    private async Task<(int Revision, JournalEntry? Existing)> ResolveRevisionAsync(
        int companyId,
        SarrafSettlement settlement,
        CancellationToken cancellationToken)
    {
        for (var revision = 0; revision < MaxRevisions; revision++)
        {
            var journal = await FindJournalAsync(
                companyId, BuildCreatedSourceEventId(settlement.Id, revision), cancellationToken);
            if (journal is null)
                return (revision, null);

            var reversal = await FindJournalAsync(
                companyId, BuildReversedSourceEventId(settlement.Id, revision), cancellationToken);
            if (reversal is null)
            {
                // This revision still stands, so re-posting the same settlement is a duplicate.
                return (revision, journal);
            }
        }

        return (MaxRevisions, null);
    }

    private async Task<(int CompanyId, string? SkipReason)> ResolveCompanyAndSkipReasonAsync(
        SarrafSettlement settlement,
        CancellationToken cancellationToken)
    {
        if (settlement.Status != SarrafSettlementStatus.Posted)
            return (0, "SETTLEMENT_NOT_POSTED");
        if (settlement.SarrafId <= 0)
            return (0, "PARTY_MISSING");
        if (settlement.SarrafChargedAmountUsd <= 0m || settlement.SarrafChargedAmount <= 0m)
            return (0, "INVALID_SARRAF_AMOUNT");
        if (SupplierLedgerAmountUsd(settlement) <= 0m || SupplierLedgerSourceAmount(settlement) <= 0m)
            return (0, "INVALID_COUNTERPARTY_AMOUNT");

        // Both money lines must survive the posting service's own re-derivation of the
        // functional amount from (transaction amount, rate). A settlement whose stored figures
        // do not satisfy that identity stays legacy-only rather than being posted at a
        // fabricated rate — the same rule that keeps non-USD via-sarraf payments out.
        if (!ConversionHolds(
            SupplierLedgerSourceAmount(settlement),
            SupplierLedgerFxRate(settlement),
            SupplierLedgerAmountUsd(settlement),
            SupplierLedgerCurrency(settlement)))
        {
            return (0, "INVALID_COUNTERPARTY_CONVERSION");
        }

        if (!ConversionHolds(
            settlement.SarrafChargedAmount,
            settlement.SarrafFxRateToUsd,
            settlement.SarrafChargedAmountUsd,
            settlement.SarrafCurrency))
        {
            return (0, "INVALID_SARRAF_CONVERSION");
        }

        var companyId = await ResolveCompanyAsync(settlement, cancellationToken);
        if (companyId is null)
            return (0, "SARRAF_COMPANY_UNKNOWN");

        var settings = await db.AccountingSettings
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.CompanyId == companyId.Value, cancellationToken);
        if (settings is null)
            return (companyId.Value, "ACCOUNTING_SETTINGS_MISSING");
        if (!string.Equals(settings.FunctionalCurrencyCode?.Trim(), "USD", StringComparison.OrdinalIgnoreCase))
            return (companyId.Value, "UNSUPPORTED_FUNCTIONAL_CURRENCY");

        var accountIds = new[]
        {
            settings.AccountsPayableAccountId,
            settings.AccountsReceivableAccountId,
            settings.ExchangeGainAccountId,
            settings.ExchangeLossAccountId
        };
        var validAccountCount = await db.Accounts.AsNoTracking().CountAsync(
            x => accountIds.Contains(x.Id) && x.CompanyId == companyId.Value && x.IsActive,
            cancellationToken);
        if (validAccountCount != accountIds.Distinct().Count())
            return (companyId.Value, "ACCOUNTING_SETTINGS_INVALID_ACCOUNTS");

        return (companyId.Value, null);
    }

    private static bool ConversionHolds(decimal amount, decimal rate, decimal amountUsd, string currency)
    {
        if (rate <= 0m)
            return false;
        if (SystemCurrency.IsBaseCurrency(currency) && rate != 1m)
            return false;

        return amountUsd == decimal.Round(amount * rate, 4, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// A sarraf settlement carries no company of its own. The contract owns one outright; a cash
    /// account has owned one since Stage 3; and a payment transaction carries one. Anything else
    /// stays unresolved rather than guessed.
    /// </summary>
    private async Task<int?> ResolveCompanyAsync(SarrafSettlement settlement, CancellationToken cancellationToken)
    {
        if (settlement.ContractId.HasValue)
        {
            var contractCompanyId = await db.Contracts
                .AsNoTracking()
                .Where(x => x.Id == settlement.ContractId.Value)
                .Select(x => (int?)x.CompanyId)
                .SingleOrDefaultAsync(cancellationToken);
            if (contractCompanyId.HasValue)
                return contractCompanyId;
        }

        if (settlement.CashAccountId.HasValue)
        {
            var cashCompanyId = await db.CashAccounts
                .AsNoTracking()
                .Where(x => x.Id == settlement.CashAccountId.Value)
                .Select(x => x.CompanyId)
                .SingleOrDefaultAsync(cancellationToken);
            if (cashCompanyId.HasValue)
                return cashCompanyId;
        }

        if (settlement.PaymentTransactionId.HasValue)
        {
            var paymentCompanyId = await db.PaymentTransactions
                .AsNoTracking()
                .Where(x => x.Id == settlement.PaymentTransactionId.Value)
                .Select(x => x.CompanyId)
                .SingleOrDefaultAsync(cancellationToken);
            if (paymentCompanyId.HasValue)
                return paymentCompanyId;
        }

        return null;
    }

    private async Task<JournalEntry?> FindJournalAsync(
        int companyId,
        string sourceEventId,
        CancellationToken cancellationToken)
        => await db.JournalEntries
            .AsNoTracking()
            .Include(x => x.Lines)
            .SingleOrDefaultAsync(
                x => x.CompanyId == companyId
                    && x.SourceModule == SourceModule
                    && x.SourceEventId == sourceEventId,
                cancellationToken);

    private SarrafSettlementAccountingResult Skipped(SarrafSettlement settlement, int companyId, string reason)
    {
        LogOutcome(settlement, companyId, 0m, PaymentPostingStatus.Skipped, reason);
        return new SarrafSettlementAccountingResult(PaymentPostingStatus.Skipped, null, reason);
    }

    private void LogOutcome(
        SarrafSettlement settlement,
        int companyId,
        decimal journalDebitTotal,
        PaymentPostingStatus status,
        string? reason,
        decimal? journalGapUsd = null)
    {
        // Legacy writes one counterparty row and, only under RecognizeExchangeGainLoss, a
        // difference row measured as Requested − SupplierAccepted. The journal instead balances
        // the counterparty amount against what the sarraf charged, so its gain or loss is a
        // different figure by design.
        //
        // The two figures that must be compared before the flag is enabled are LegacyDifferenceUsd
        // and JournalGapUsd, so both are printed rather than left to be derived. JournalGapUsd is
        // SarrafChargedAmountUsd − LegacyCounterpartyAmountUsd: positive means the sarraf charged
        // more than the counterparty accepted. Which side of P&L that lands on also depends on the
        // direction of the settlement, so JournalGapAccountKind names the account actually hit.
        var gapAccountKind = journalGapUsd is null or 0m
            ? "None"
            : JournalGapIsLoss(settlement, journalGapUsd.Value) ? "ExchangeLoss" : "ExchangeGain";

        logger.LogInformation(
            "Sarraf settlement accounting pilot comparison: SettlementId {SettlementId}, CompanyId {CompanyId}, SarrafId {SarrafId}, CounterpartyType {CounterpartyType}, Direction {Direction}, DifferenceTreatment {DifferenceTreatment}, LegacyCounterpartyAmountUsd {LegacyCounterpartyAmountUsd}, LegacyDifferenceUsd {LegacyDifferenceUsd}, SarrafChargedAmountUsd {SarrafChargedAmountUsd}, JournalGapUsd {JournalGapUsd}, JournalGapAccountKind {JournalGapAccountKind}, JournalDebitTotal {JournalDebitTotal}, PostingStatus {PostingStatus}, SkipOrFailureReason {SkipOrFailureReason}",
            settlement.Id,
            companyId,
            settlement.SarrafId,
            settlement.CounterpartyType,
            settlement.Direction,
            settlement.DifferenceTreatment,
            SupplierLedgerAmountUsd(settlement),
            settlement.DifferenceAmountUsd,
            settlement.SarrafChargedAmountUsd,
            journalGapUsd,
            gapAccountKind,
            journalDebitTotal,
            status,
            reason);
    }

    /// <summary>
    /// A gap is a loss when it makes us worse off, which depends on which way the money moved:
    /// paying out more than the counterparty credited costs us, but collecting less than they
    /// were debited costs us too.
    /// </summary>
    internal static bool JournalGapIsLoss(SarrafSettlement settlement, decimal gapUsd)
    {
        var reducesCounterparty = settlement.CounterpartyType == SarrafSettlementCounterpartyType.Customer
            ? settlement.Direction == SarrafSettlementDirection.In
            : settlement.Direction == SarrafSettlementDirection.Out;
        return reducesCounterparty ? gapUsd > 0m : gapUsd < 0m;
    }

    private void LogFailure(SarrafSettlement settlement, Exception exception)
    {
        var failureReason = exception is AccountingValidationException validation
            ? validation.Code
            : exception.GetType().Name;
        logger.LogError(
            exception,
            "Sarraf settlement accounting pilot posting failed for SettlementId {SettlementId} with FailureReason {FailureReason}",
            settlement.Id,
            failureReason);
    }
}
