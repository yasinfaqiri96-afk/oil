using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PTGOilSystem.Web.Configuration;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services.Time;

namespace PTGOilSystem.Web.Services.Accounting;

public enum PaymentPostingStatus
{
    Skipped = 0,
    Posted = 1,
    Duplicate = 2
}

/// <summary>
/// The accounting nature of a cash payment. Derived from what the payment actually is
/// (kind + advance markers + party), never from the legacy LedgerSide alone: the legacy side
/// only records which way the party balance moved, which is not enough to choose between a
/// receivable settlement and an advance.
/// </summary>
public enum PaymentAccountingEventKind
{
    CustomerReceipt = 1,
    CustomerAdvance = 2,
    SupplierPayment = 3,
    SupplierPrepayment = 4,
    SarrafCashPayment = 5,
    // Stage 5 — settle a liability an expense accrued.
    ExpensePayment = 6,
    CommissionPayment = 7
}

public sealed record PaymentAccountingResult(
    PaymentPostingStatus Status,
    JournalEntry? Journal,
    string? Reason,
    PaymentAccountingEventKind? EventKind = null);

public interface IPaymentAccountingAdapter
{
    Task<PaymentAccountingResult> TryPostPaymentAsync(
        PaymentTransaction payment,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reverses every posted-and-not-yet-reversed revision of a payment's journal, so a corrected
    /// payment starts from a clean double-entry balance before its next revision is posted.
    /// </summary>
    Task<PaymentAccountingResult> TryPostPaymentReversalAsync(
        PaymentTransaction payment,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Stage 4 dual-write pilot: mirrors cash receipts and payments into the double-entry journal
/// while the legacy ledger remains the operational source.
///
/// Mapping (functional currency is USD; the payment currency pair rides on every line):
///   CustomerReceipt    Dr Cash/Bank            Cr Accounts Receivable   (party = customer)
///   CustomerAdvance    Dr Cash/Bank            Cr Customer Advance      (party = customer)
///   SupplierPayment    Dr Accounts Payable     Cr Cash/Bank             (party = supplier)
///   SupplierPrepayment Dr Supplier Prepayment  Cr Cash/Bank             (party = supplier)
///   SarrafCashPayment  Dr Accounts Payable     Cr Cash/Bank             (party = sarraf)
///
/// Stage 5 adds the two flows that settle what an expense accrued. Both take their account and
/// party from the linked expense rather than from the payment, so a settlement can never land on
/// a different account than its accrual:
///   ExpensePayment     Dr &lt;expense payable&gt;    Cr Cash/Bank
///   CommissionPayment  Dr &lt;expense payable&gt;    Cr Cash/Bank
///
/// The cash line always carries CashAccountId; the party line always carries PartyType/PartyId
/// plus the contract/shipment dimensions when the legacy payment supplies them.
///
/// Ambiguous kinds (ManualPayment, ManualReceipt, truck/employee/service-provider flows) are
/// deliberately not mapped here: they are skipped with UNSUPPORTED_PAYMENT_KIND and stay
/// legacy-only until their own stage defines a proven mapping.
///
/// Company ownership comes from <see cref="IPaymentCompanyResolver"/>, which only returns a
/// company it can prove. Unprovable payments skip; legacy behaviour never changes.
/// </summary>
public sealed class PaymentAccountingAdapter(
    ApplicationDbContext db,
    IAccountingPostingService postingService,
    IAccountingJournalNumberGenerator journalNumberGenerator,
    IPaymentCompanyResolver companyResolver,
    IExpenseAccountingAdapter expenseAccounting,
    IOptions<AccountingOptions> options,
    ILogger<PaymentAccountingAdapter> logger)
    : IPaymentAccountingAdapter
{
    public const string SourceModule = "Payment";
    public const string SourceEntityType = nameof(PaymentTransaction);
    private const int MaxRevisions = 100;

    private readonly AccountingOptions _options = options.Value;

    public async Task<PaymentAccountingResult> TryPostPaymentAsync(
        PaymentTransaction payment,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payment);

        var eventKind = ResolveEventKind(payment);
        if (eventKind is null)
        {
            LogOutcome(payment, null, 0, 0m, PaymentPostingStatus.Skipped, "UNSUPPORTED_PAYMENT_KIND");
            return new PaymentAccountingResult(
                PaymentPostingStatus.Skipped, null, "UNSUPPORTED_PAYMENT_KIND");
        }

        var (companyId, skipReason, settlement) = await ResolveCompanyAndSkipReasonAsync(
            payment,
            eventKind.Value,
            cancellationToken);
        if (skipReason is not null)
        {
            LogOutcome(payment, eventKind, companyId, 0m, PaymentPostingStatus.Skipped, skipReason);
            return new PaymentAccountingResult(
                PaymentPostingStatus.Skipped, null, skipReason, eventKind);
        }

        // A correction re-posts the same payment with new figures, so the live revision is the
        // first one not yet posted. An unchanged re-post lands on the standing revision and is a
        // duplicate, which is what makes the create path idempotent under retry.
        var (revision, existing) = await ResolveRevisionAsync(companyId, payment.Id, cancellationToken);
        if (existing is not null)
        {
            LogOutcome(payment, eventKind, companyId, existing.Lines.Sum(x => x.Debit),
                PaymentPostingStatus.Duplicate, "DUPLICATE_SOURCE_EVENT");
            return new PaymentAccountingResult(
                PaymentPostingStatus.Duplicate, existing, "DUPLICATE_SOURCE_EVENT", eventKind);
        }

        if (revision >= MaxRevisions)
        {
            LogOutcome(payment, eventKind, companyId, 0m, PaymentPostingStatus.Skipped, "TOO_MANY_REVISIONS");
            return new PaymentAccountingResult(
                PaymentPostingStatus.Skipped, null, "TOO_MANY_REVISIONS", eventKind);
        }

        var sourceEventId = BuildCreatedSourceEventId(payment.Id, revision);

        var settings = await db.AccountingSettings
            .AsNoTracking()
            .SingleAsync(x => x.CompanyId == companyId, cancellationToken);

        var request = new AccountingPostRequest(
            companyId,
            journalNumberGenerator.ForPaymentRevision(companyId, payment.Id, revision),
            payment.PaymentDate.Date,
            payment.PaymentDate.Date,
            payment.PaymentDate.Date,
            SourceModule,
            BuildLines(payment, eventKind.Value, settings, settlement),
            SourceEventId: sourceEventId,
            SourceEntityType: SourceEntityType,
            SourceEntityId: payment.Id,
            Description: BuildJournalDescription(payment, eventKind.Value));

        try
        {
            var journal = await postingService.PostAsync(request, cancellationToken);
            LogOutcome(payment, eventKind, companyId, journal.Lines.Sum(x => x.Debit),
                PaymentPostingStatus.Posted, null);
            return new PaymentAccountingResult(PaymentPostingStatus.Posted, journal, null, eventKind);
        }
        catch (Exception exception)
        {
            LogFailure(payment, exception);
            throw;
        }
    }

    // Revision 0 keeps the historical revision-less key so journals posted before corrections
    // existed stay addressable by both the old and the new callers.
    public static string BuildCreatedSourceEventId(int paymentId)
        => BuildCreatedSourceEventId(paymentId, 0);

    public static string BuildCreatedSourceEventId(int paymentId, int revision)
        => revision == 0 ? $"Payment:{paymentId}:Created" : $"Payment:{paymentId}:Created:{revision}";

    public static string BuildReversedSourceEventId(int paymentId, int revision)
        => $"Payment:{paymentId}:Reversed:{revision}";

    /// <summary>
    /// Walks the revision chain and returns the first revision with no standing journal, together
    /// with the standing journal when the current revision is still live (making a re-post a
    /// duplicate). Mirrors the sarraf settlement adapter so both correction flows behave alike.
    /// </summary>
    private async Task<(int Revision, JournalEntry? Existing)> ResolveRevisionAsync(
        int companyId,
        int paymentId,
        CancellationToken cancellationToken)
    {
        for (var revision = 0; revision < MaxRevisions; revision++)
        {
            var journal = await FindJournalAsync(
                companyId, BuildCreatedSourceEventId(paymentId, revision), cancellationToken);
            if (journal is null)
                return (revision, null);

            var reversal = await FindJournalAsync(
                companyId, BuildReversedSourceEventId(paymentId, revision), cancellationToken);
            if (reversal is null)
            {
                // This revision still stands, so re-posting the same payment is a duplicate.
                return (revision, journal);
            }
        }

        return (MaxRevisions, null);
    }

    public async Task<PaymentAccountingResult> TryPostPaymentReversalAsync(
        PaymentTransaction payment,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payment);

        // Reversal cleans up whatever was posted, so it is gated only on the core being enabled,
        // not on the per-kind create pilot: if a journal exists it must always be reversible.
        if (!_options.Enabled)
            return new PaymentAccountingResult(PaymentPostingStatus.Skipped, null, "ACCOUNTING_DISABLED");

        var companyId = await companyResolver.ResolveAsync(payment, cancellationToken);
        if (companyId is null)
            return new PaymentAccountingResult(PaymentPostingStatus.Skipped, null, "PAYMENT_COMPANY_UNKNOWN");

        JournalEntry? lastReversal = null;
        var reversedAny = false;

        for (var revision = 0; revision < MaxRevisions; revision++)
        {
            var original = await FindJournalAsync(
                companyId.Value, BuildCreatedSourceEventId(payment.Id, revision), cancellationToken);
            if (original is null)
                break;

            var reversedEventId = BuildReversedSourceEventId(payment.Id, revision);
            var existingReversal = await FindJournalAsync(companyId.Value, reversedEventId, cancellationToken);
            if (existingReversal is not null)
            {
                lastReversal = existingReversal;
                continue;
            }

            var request = new AccountingReversalRequest(
                original.Id,
                journalNumberGenerator.ForPaymentReversal(companyId.Value, payment.Id, revision),
                AfghanistanBusinessClock.SystemToday,
                SourceModule,
                reversedEventId,
                $"Reversal of payment #{payment.Id} revision {revision}");

            try
            {
                lastReversal = await postingService.ReverseAsync(request, cancellationToken);
                reversedAny = true;
            }
            catch (Exception exception)
            {
                LogFailure(payment, exception);
                throw;
            }
        }

        if (lastReversal is null)
            return new PaymentAccountingResult(PaymentPostingStatus.Skipped, null, "ORIGINAL_JOURNAL_NOT_POSTED");

        return reversedAny
            ? new PaymentAccountingResult(PaymentPostingStatus.Posted, lastReversal, null)
            : new PaymentAccountingResult(PaymentPostingStatus.Duplicate, lastReversal, "DUPLICATE_SOURCE_EVENT");
    }

    /// <summary>
    /// Maps a legacy payment onto its accounting nature. Returns null when the payment kind has
    /// no proven Stage 4 mapping, which keeps that flow legacy-only instead of guessing.
    /// </summary>
    public static PaymentAccountingEventKind? ResolveEventKind(PaymentTransaction payment)
        => payment.PaymentKind switch
        {
            PaymentKind.CustomerReceipt => payment.IsCustomerAdvance == true
                ? PaymentAccountingEventKind.CustomerAdvance
                : PaymentAccountingEventKind.CustomerReceipt,
            PaymentKind.SupplierPayment => payment.IsAdvancePayment == true
                ? PaymentAccountingEventKind.SupplierPrepayment
                : PaymentAccountingEventKind.SupplierPayment,
            PaymentKind.SarrafSettlement => PaymentAccountingEventKind.SarrafCashPayment,
            PaymentKind.ExpensePayment => PaymentAccountingEventKind.ExpensePayment,
            PaymentKind.CommissionPayment => PaymentAccountingEventKind.CommissionPayment,
            _ => null
        };

    private bool IsPilotEnabled(PaymentAccountingEventKind eventKind)
        => eventKind switch
        {
            PaymentAccountingEventKind.CustomerReceipt => _options.Pilots.CustomerReceipt,
            PaymentAccountingEventKind.CustomerAdvance => _options.Pilots.CustomerAdvance,
            PaymentAccountingEventKind.SupplierPayment => _options.Pilots.SupplierPayment,
            PaymentAccountingEventKind.SupplierPrepayment => _options.Pilots.SupplierPrepayment,
            PaymentAccountingEventKind.SarrafCashPayment => _options.Pilots.SarrafPayment,
            PaymentAccountingEventKind.ExpensePayment => _options.Pilots.ExpensePayment,
            PaymentAccountingEventKind.CommissionPayment => _options.Pilots.CommissionPayment,
            _ => false
        };

    /// <summary>
    /// The liability an expense-settling payment must debit, resolved from the expense that
    /// accrued it so the settlement can never land on a different account than the accrual.
    /// </summary>
    private sealed record ExpenseSettlement(
        int PayableAccountId,
        AccountingPartyType? PartyType,
        int? PartyId);

    private static AccountingPostLine[] BuildLines(
        PaymentTransaction payment,
        PaymentAccountingEventKind eventKind,
        AccountingSettings settings,
        ExpenseSettlement? settlement)
    {
        if (eventKind is PaymentAccountingEventKind.ExpensePayment
            or PaymentAccountingEventKind.CommissionPayment)
        {
            return BuildSettlementLines(payment, settings, settlement!);
        }

        var (partyAccountId, partyType, partyId) = eventKind switch
        {
            PaymentAccountingEventKind.CustomerReceipt =>
                (settings.AccountsReceivableAccountId, AccountingPartyType.Customer, payment.CustomerId!.Value),
            PaymentAccountingEventKind.CustomerAdvance =>
                (settings.CustomerAdvanceAccountId, AccountingPartyType.Customer, payment.CustomerId!.Value),
            PaymentAccountingEventKind.SupplierPayment =>
                (settings.AccountsPayableAccountId, AccountingPartyType.Supplier, payment.SupplierId!.Value),
            PaymentAccountingEventKind.SupplierPrepayment =>
                (settings.SupplierPrepaymentAccountId, AccountingPartyType.Supplier, payment.SupplierId!.Value),
            PaymentAccountingEventKind.SarrafCashPayment =>
                (settings.AccountsPayableAccountId, AccountingPartyType.Sarraf, payment.SarrafId!.Value),
            _ => throw new InvalidOperationException($"Unmapped accounting event kind {eventKind}.")
        };

        var rate = payment.AppliedFxRateToUsd!.Value;
        var cashIsDebit = IsCashInflow(eventKind);

        var cashLine = new AccountingPostLine(
            settings.CashBankControlAccountId,
            Debit: cashIsDebit ? payment.AmountUsd : 0m,
            Credit: cashIsDebit ? 0m : payment.AmountUsd,
            payment.Currency,
            payment.Amount,
            rate,
            CashAccountId: payment.CashAccountId,
            Description: BuildCashLineDescription(eventKind));

        var partyLine = new AccountingPostLine(
            partyAccountId,
            Debit: cashIsDebit ? 0m : payment.AmountUsd,
            Credit: cashIsDebit ? payment.AmountUsd : 0m,
            payment.Currency,
            payment.Amount,
            rate,
            partyType,
            partyId,
            ContractId: payment.ContractId,
            ShipmentId: payment.ShipmentId,
            Description: BuildPartyLineDescription(eventKind));

        // Debit first keeps the stored line order readable in the journal view.
        return cashIsDebit ? [cashLine, partyLine] : [partyLine, cashLine];
    }

    private static AccountingPostLine[] BuildSettlementLines(
        PaymentTransaction payment,
        AccountingSettings settings,
        ExpenseSettlement settlement)
    {
        var rate = payment.AppliedFxRateToUsd!.Value;

        return
        [
            new AccountingPostLine(
                settlement.PayableAccountId,
                Debit: payment.AmountUsd,
                Credit: 0m,
                payment.Currency,
                payment.Amount,
                rate,
                settlement.PartyType,
                settlement.PartyId,
                ContractId: payment.ContractId,
                ShipmentId: payment.ShipmentId,
                Description: "Expense liability settled"),
            new AccountingPostLine(
                settings.CashBankControlAccountId,
                Debit: 0m,
                Credit: payment.AmountUsd,
                payment.Currency,
                payment.Amount,
                rate,
                CashAccountId: payment.CashAccountId,
                Description: "Cash paid")
        ];
    }

    private static bool IsCashInflow(PaymentAccountingEventKind eventKind)
        => eventKind is PaymentAccountingEventKind.CustomerReceipt
            or PaymentAccountingEventKind.CustomerAdvance;

    // Every non-inflow kind, including the expense settlements, moves cash out.
    private static PaymentDirection ExpectedDirection(PaymentAccountingEventKind eventKind)
        => IsCashInflow(eventKind) ? PaymentDirection.In : PaymentDirection.Out;

    private async Task<(int CompanyId, string? SkipReason, ExpenseSettlement? Settlement)>
        ResolveCompanyAndSkipReasonAsync(
            PaymentTransaction payment,
            PaymentAccountingEventKind eventKind,
            CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
            return (0, "ACCOUNTING_DISABLED", null);
        if (!IsPilotEnabled(eventKind))
            return (0, "PILOT_DISABLED", null);

        if (payment.Direction != ExpectedDirection(eventKind))
            return (0, "DIRECTION_MISMATCH", null);

        // Expense settlements take their party from the expense that accrued the liability, and
        // that party is legitimately absent for accrued/commission expenses, so they are exempt
        // from the party requirement the party-facing kinds enforce.
        var isExpenseSettlement = eventKind is PaymentAccountingEventKind.ExpensePayment
            or PaymentAccountingEventKind.CommissionPayment;
        if (!isExpenseSettlement)
        {
            var partyIsPresent = eventKind switch
            {
                PaymentAccountingEventKind.CustomerReceipt or PaymentAccountingEventKind.CustomerAdvance
                    => payment.CustomerId.HasValue,
                PaymentAccountingEventKind.SupplierPayment or PaymentAccountingEventKind.SupplierPrepayment
                    => payment.SupplierId.HasValue,
                PaymentAccountingEventKind.SarrafCashPayment => payment.SarrafId.HasValue,
                _ => false
            };
            if (!partyIsPresent)
                return (0, "PARTY_MISSING", null);
        }

        if (payment.CashAccountId <= 0)
            return (0, "CASH_ACCOUNT_MISSING", null);
        if (payment.Amount <= 0m || payment.AmountUsd <= 0m)
            return (0, "INVALID_PAYMENT_AMOUNT", null);

        var rate = payment.AppliedFxRateToUsd;
        if (!rate.HasValue || rate.Value <= 0m)
            return (0, "INVALID_PAYMENT_FX", null);
        if (SystemCurrency.IsBaseCurrency(payment.Currency) && rate.Value != 1m)
            return (0, "INVALID_PAYMENT_FX", null);

        // The posting service re-derives the functional amount from the currency pair, so a
        // legacy row whose AmountUsd drifted from Amount x rate must stay legacy-only rather
        // than fail the whole payment.
        var expectedUsd = decimal.Round(payment.Amount * rate.Value, 4, MidpointRounding.AwayFromZero);
        if (payment.AmountUsd != expectedUsd)
            return (0, "INVALID_PAYMENT_CONVERSION", null);

        var companyId = await companyResolver.ResolveAsync(payment, cancellationToken);
        if (companyId is null)
            return (0, "PAYMENT_COMPANY_UNKNOWN", null);

        var settings = await db.AccountingSettings
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.CompanyId == companyId.Value, cancellationToken);
        if (settings is null)
            return (companyId.Value, "ACCOUNTING_SETTINGS_MISSING", null);
        if (!string.Equals(settings.FunctionalCurrencyCode?.Trim(), "USD", StringComparison.OrdinalIgnoreCase))
            return (companyId.Value, "UNSUPPORTED_FUNCTIONAL_CURRENCY", null);

        var configuredAccountIds = GetConfiguredAccountIds(settings);
        if (configuredAccountIds.Any(x => x <= 0)
            || configuredAccountIds.Distinct().Count() != configuredAccountIds.Length)
            return (companyId.Value, "ACCOUNTING_SETTINGS_INCOMPLETE", null);

        var validAccountCount = await db.Accounts.AsNoTracking().CountAsync(
            x => configuredAccountIds.Contains(x.Id)
                && x.CompanyId == companyId.Value
                && x.IsActive,
            cancellationToken);
        if (validAccountCount != configuredAccountIds.Length)
            return (companyId.Value, "ACCOUNTING_SETTINGS_INVALID_ACCOUNTS", null);

        ExpenseSettlement? settlement = null;
        if (isExpenseSettlement)
        {
            if (!payment.ExpenseTransactionId.HasValue)
                return (companyId.Value, "EXPENSE_LINK_MISSING", null);

            var expense = await db.ExpenseTransactions
                .AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == payment.ExpenseTransactionId.Value, cancellationToken);
            if (expense is null)
                return (companyId.Value, "EXPENSE_NOT_FOUND", null);

            var payableAccountId = await expenseAccounting.ResolvePayableAccountIdAsync(
                expense,
                companyId.Value,
                cancellationToken);
            if (payableAccountId is null)
                return (companyId.Value, "EXPENSE_PAYABLE_KIND_NOT_SET", null);

            var (partyType, partyId) = ExpenseAccountingAdapter.ResolveParty(expense);
            settlement = new ExpenseSettlement(payableAccountId.Value, partyType, partyId);
        }

        // The cash account must belong to the journal company when it declares an owner;
        // an unowned legacy cash account stays usable so the pilot does not block operations.
        var cashAccountCompanyId = await db.CashAccounts
            .AsNoTracking()
            .Where(x => x.Id == payment.CashAccountId)
            .Select(x => x.CompanyId)
            .SingleOrDefaultAsync(cancellationToken);
        if (cashAccountCompanyId.HasValue && cashAccountCompanyId.Value != companyId.Value)
            return (companyId.Value, "CASH_ACCOUNT_COMPANY_MISMATCH", null);

        if (payment.ContractId.HasValue)
        {
            var contractCompanyId = await db.Contracts
                .AsNoTracking()
                .Where(x => x.Id == payment.ContractId.Value)
                .Select(x => (int?)x.CompanyId)
                .SingleOrDefaultAsync(cancellationToken);
            if (contractCompanyId.HasValue && contractCompanyId.Value != companyId.Value)
                return (companyId.Value, "COMPANY_MISMATCH", null);
        }

        return (companyId.Value, null, settlement);
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

    private static string BuildJournalDescription(
        PaymentTransaction payment,
        PaymentAccountingEventKind eventKind)
        => $"{eventKind} #{payment.Id} on {payment.PaymentDate:yyyy-MM-dd}";

    private static string BuildCashLineDescription(PaymentAccountingEventKind eventKind)
        => IsCashInflow(eventKind) ? "Cash received" : "Cash paid";

    private static string BuildPartyLineDescription(PaymentAccountingEventKind eventKind)
        => eventKind switch
        {
            PaymentAccountingEventKind.CustomerReceipt => "Customer receivable settled",
            PaymentAccountingEventKind.CustomerAdvance => "Customer advance received",
            PaymentAccountingEventKind.SupplierPayment => "Supplier payable settled",
            PaymentAccountingEventKind.SupplierPrepayment => "Supplier prepayment made",
            PaymentAccountingEventKind.SarrafCashPayment => "Sarraf payable settled",
            _ => "Payment party movement"
        };

    private static int[] GetConfiguredAccountIds(AccountingSettings settings)
        =>
        [
            settings.CashBankControlAccountId,
            settings.AccountsReceivableAccountId,
            settings.AccountsPayableAccountId,
            settings.InventoryAccountId,
            settings.InventoryInTransitAccountId,
            settings.SupplierPrepaymentAccountId,
            settings.CustomerAdvanceAccountId,
            settings.FreightPayableAccountId,
            settings.CommissionPayableAccountId,
            settings.EmployeeAdvanceAccountId,
            settings.EmployeePayableAccountId,
            settings.AccruedExpenseAccountId,
            settings.SalesRevenueAccountId,
            settings.CostOfGoodsSoldAccountId,
            settings.GeneralExpenseAccountId,
            settings.ExchangeGainAccountId,
            settings.ExchangeLossAccountId,
            settings.InventoryLossAccountId,
            settings.CurrentYearProfitLossAccountId,
            settings.RetainedEarningsAccountId
        ];

    private void LogOutcome(
        PaymentTransaction payment,
        PaymentAccountingEventKind? eventKind,
        int companyId,
        decimal journalDebitTotal,
        PaymentPostingStatus status,
        string? reason)
    {
        // Legacy writes a single ledger row of AmountUsd; the journal must debit the same total.
        logger.LogInformation(
            "Payment accounting pilot comparison: PaymentId {PaymentId}, PaymentKind {PaymentKind}, EventKind {EventKind}, CompanyId {CompanyId}, CashAccountId {CashAccountId}, LegacyAmountUsd {LegacyAmountUsd}, JournalDebitTotal {JournalDebitTotal}, Difference {Difference}, PostingStatus {PostingStatus}, SkipOrFailureReason {SkipOrFailureReason}",
            payment.Id,
            payment.PaymentKind,
            eventKind,
            companyId,
            payment.CashAccountId,
            payment.AmountUsd,
            journalDebitTotal,
            journalDebitTotal - payment.AmountUsd,
            status,
            reason);
    }

    private void LogFailure(PaymentTransaction payment, Exception exception)
    {
        var failureReason = exception is AccountingValidationException validation
            ? validation.Code
            : exception.GetType().Name;
        logger.LogError(
            exception,
            "Payment accounting pilot posting failed for PaymentId {PaymentId} with FailureReason {FailureReason}",
            payment.Id,
            failureReason);
    }
}
