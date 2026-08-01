using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PTGOilSystem.Web.Configuration;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services.Time;

namespace PTGOilSystem.Web.Services.Accounting;

public sealed record ThreeWaySettlementAccountingResult(
    PaymentPostingStatus Status,
    JournalEntry? Journal,
    string? Reason);

public interface IThreeWaySettlementAccountingAdapter
{
    Task<ThreeWaySettlementAccountingResult> TryPostSettlementAsync(
        ThreeWaySettlement settlement,
        CancellationToken cancellationToken = default);

    Task<ThreeWaySettlementAccountingResult> TryPostSettlementReversalAsync(
        ThreeWaySettlement settlement,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Stage 8 dual-write pilot for three-way settlements routed through a sarraf, where a customer
/// pays the sarraf and the sarraf pays the supplier, settling both legs at once.
///
///   Dr Accounts Payable    (party = supplier)  SupplierAcceptedUsd
///   Cr Accounts Receivable (party = customer)  CustomerPaidUsd
///   with the gap on Exchange Loss or Exchange Gain
///
/// No sarraf line appears, and that is not an omission: the sarraf holds nothing once both legs
/// settle together, which is exactly why the legacy flow stores SarrafId as provenance and
/// writes no ledger row with it. This is the one Stage 8 sarraf mapping where the sarraf is
/// genuinely only a conduit.
///
/// The two legacy rows are both Debits under the legacy convention, where a Debit on a customer
/// means their receivable fell. Double entry says the same thing with a credit, so the customer
/// line's side is flipped relative to the legacy row while meaning the identical event — the
/// same translation Stage 4 makes for customer receipts.
///
/// Scope: PayeeType Sarraf only, per the confirmed Stage 8 decision. A settlement paid directly
/// to the supplier takes the identical mapping and would need only this condition widened, but
/// it stays legacy-only until that is asked for.
/// </summary>
public sealed class ThreeWaySettlementAccountingAdapter(
    ApplicationDbContext db,
    IAccountingPostingService postingService,
    IAccountingJournalNumberGenerator journalNumberGenerator,
    IOptions<AccountingOptions> options,
    ILogger<ThreeWaySettlementAccountingAdapter> logger)
    : IThreeWaySettlementAccountingAdapter
{
    public const string SourceModule = "ThreeWaySettlement";
    public const string SourceEntityType = nameof(ThreeWaySettlement);

    private readonly AccountingOptions _options = options.Value;

    public async Task<ThreeWaySettlementAccountingResult> TryPostSettlementAsync(
        ThreeWaySettlement settlement,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settlement);

        if (!_options.Enabled)
            return Skipped(settlement, 0, "ACCOUNTING_DISABLED");
        if (!_options.Pilots.ThreeWaySettlement)
            return Skipped(settlement, 0, "PILOT_DISABLED");

        var (companyId, skipReason) = await ResolveCompanyAndSkipReasonAsync(settlement, cancellationToken);
        if (skipReason is not null)
            return Skipped(settlement, companyId, skipReason);

        var sourceEventId = BuildCreatedSourceEventId(settlement.Id);
        var existing = await FindJournalAsync(companyId, sourceEventId, cancellationToken);
        if (existing is not null)
        {
            LogOutcome(settlement, companyId, existing.Lines.Sum(x => x.Debit),
                PaymentPostingStatus.Duplicate, "DUPLICATE_SOURCE_EVENT");
            return new ThreeWaySettlementAccountingResult(
                PaymentPostingStatus.Duplicate, existing, "DUPLICATE_SOURCE_EVENT");
        }

        var settings = await db.AccountingSettings
            .AsNoTracking()
            .SingleAsync(x => x.CompanyId == companyId, cancellationToken);

        var lines = new List<AccountingPostLine>
        {
            new(
                settings.AccountsPayableAccountId,
                Debit: settlement.SupplierAcceptedUsd,
                Credit: 0m,
                settlement.EffectiveSupplierAcceptedCurrency,
                settlement.SupplierAcceptedAmount,
                settlement.EffectiveSupplierAcceptedFxRateToUsd,
                AccountingPartyType.Supplier,
                settlement.SupplierId,
                ContractId: settlement.SupplierPurchaseContractId,
                Description: "Supplier payable settled through sarraf"),
            new(
                settings.AccountsReceivableAccountId,
                Debit: 0m,
                Credit: settlement.CustomerPaidUsd,
                settlement.EffectiveCustomerPaidCurrency,
                settlement.CustomerPaidAmount,
                settlement.EffectiveCustomerPaidFxRateToUsd,
                AccountingPartyType.Customer,
                settlement.CustomerId,
                ContractId: settlement.CustomerSaleContractId,
                Description: "Customer receivable settled through sarraf")
        };

        // DifferenceUsd is CustomerPaid − SupplierAccepted. Paying more than the supplier took
        // credit for costs us the gap; taking less costs the customer's balance less than the
        // supplier's fell, which is a gain.
        var gapUsd = decimal.Round(
            settlement.CustomerPaidUsd - settlement.SupplierAcceptedUsd, 4, MidpointRounding.AwayFromZero);
        if (gapUsd != 0m)
        {
            var isLoss = gapUsd > 0m;
            var gapAmount = Math.Abs(gapUsd);
            lines.Add(new AccountingPostLine(
                isLoss ? settings.ExchangeLossAccountId : settings.ExchangeGainAccountId,
                Debit: isLoss ? gapAmount : 0m,
                Credit: isLoss ? 0m : gapAmount,
                SystemCurrency.BaseCurrencyCode,
                gapAmount,
                1m,
                Description: isLoss
                    ? "Three-way settlement exchange loss"
                    : "Three-way settlement exchange gain"));
        }

        var request = new AccountingPostRequest(
            companyId,
            journalNumberGenerator.ForThreeWaySettlement(companyId, settlement.Id),
            settlement.SettlementDate.Date,
            settlement.SettlementDate.Date,
            settlement.SettlementDate.Date,
            SourceModule,
            lines,
            SourceEventId: sourceEventId,
            SourceEntityType: SourceEntityType,
            SourceEntityId: settlement.Id,
            Description: $"Three-way settlement #{settlement.Id} via sarraf on {settlement.SettlementDate:yyyy-MM-dd}");

        try
        {
            var journal = await postingService.PostAsync(request, cancellationToken);
            LogOutcome(settlement, companyId, journal.Lines.Sum(x => x.Debit),
                PaymentPostingStatus.Posted, null);
            return new ThreeWaySettlementAccountingResult(PaymentPostingStatus.Posted, journal, null);
        }
        catch (Exception exception)
        {
            LogFailure(settlement, exception);
            throw;
        }
    }

    public async Task<ThreeWaySettlementAccountingResult> TryPostSettlementReversalAsync(
        ThreeWaySettlement settlement,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settlement);

        if (!_options.Enabled)
            return Skipped(settlement, 0, "ACCOUNTING_DISABLED");
        if (!_options.Pilots.ThreeWaySettlement)
            return Skipped(settlement, 0, "PILOT_DISABLED");

        if (settlement.PayeeType != ThreeWayPayeeType.Sarraf)
            return Skipped(settlement, 0, "UNSUPPORTED_PAYEE_TYPE");

        var companyId = await ResolveCompanyAsync(settlement, cancellationToken);
        if (companyId is null)
            return Skipped(settlement, 0, "SETTLEMENT_COMPANY_UNKNOWN");

        var reversedEventId = BuildReversedSourceEventId(settlement.Id);
        var alreadyReversed = await FindJournalAsync(companyId.Value, reversedEventId, cancellationToken);
        if (alreadyReversed is not null)
        {
            LogOutcome(settlement, companyId.Value, alreadyReversed.Lines.Sum(x => x.Debit),
                PaymentPostingStatus.Duplicate, "DUPLICATE_SOURCE_EVENT");
            return new ThreeWaySettlementAccountingResult(
                PaymentPostingStatus.Duplicate, alreadyReversed, "DUPLICATE_SOURCE_EVENT");
        }

        var original = await FindJournalAsync(
            companyId.Value, BuildCreatedSourceEventId(settlement.Id), cancellationToken);
        if (original is null)
            return Skipped(settlement, companyId.Value, "ORIGINAL_JOURNAL_NOT_POSTED");

        var request = new AccountingReversalRequest(
            original.Id,
            journalNumberGenerator.ForThreeWaySettlementReversal(companyId.Value, settlement.Id),
            AfghanistanBusinessClock.SystemToday,
            SourceModule,
            reversedEventId,
            $"Reversal of three-way settlement #{settlement.Id}");

        try
        {
            var journal = await postingService.ReverseAsync(request, cancellationToken);
            LogOutcome(settlement, companyId.Value, journal.Lines.Sum(x => x.Debit),
                PaymentPostingStatus.Posted, null);
            return new ThreeWaySettlementAccountingResult(PaymentPostingStatus.Posted, journal, null);
        }
        catch (Exception exception)
        {
            LogFailure(settlement, exception);
            throw;
        }
    }

    public static string BuildCreatedSourceEventId(int settlementId)
        => $"ThreeWaySettlement:{settlementId}:Created";

    public static string BuildReversedSourceEventId(int settlementId)
        => $"ThreeWaySettlement:{settlementId}:Reversed";

    private async Task<(int CompanyId, string? SkipReason)> ResolveCompanyAndSkipReasonAsync(
        ThreeWaySettlement settlement,
        CancellationToken cancellationToken)
    {
        if (settlement.PayeeType != ThreeWayPayeeType.Sarraf)
            return (0, "UNSUPPORTED_PAYEE_TYPE");
        if (settlement.Status != ThreeWaySettlementStatus.Posted)
            return (0, "SETTLEMENT_NOT_POSTED");
        if (!settlement.SupplierId.HasValue || settlement.CustomerId <= 0)
            return (0, "PARTY_MISSING");
        if (settlement.CustomerPaidUsd <= 0m || settlement.SupplierAcceptedUsd <= 0m)
            return (0, "INVALID_SETTLEMENT_AMOUNT");
        if (settlement.CustomerPaidAmount <= 0m || settlement.SupplierAcceptedAmount <= 0m)
            return (0, "INVALID_SETTLEMENT_AMOUNT");

        // Both money lines must survive the posting service's re-derivation of the functional
        // amount from (transaction amount, rate); a settlement whose stored figures do not
        // satisfy that identity stays legacy-only rather than posting at a fabricated rate.
        if (!ConversionHolds(
            settlement.CustomerPaidAmount,
            settlement.EffectiveCustomerPaidFxRateToUsd,
            settlement.CustomerPaidUsd,
            settlement.EffectiveCustomerPaidCurrency))
        {
            return (0, "INVALID_CUSTOMER_CONVERSION");
        }

        if (!ConversionHolds(
            settlement.SupplierAcceptedAmount,
            settlement.EffectiveSupplierAcceptedFxRateToUsd,
            settlement.SupplierAcceptedUsd,
            settlement.EffectiveSupplierAcceptedCurrency))
        {
            return (0, "INVALID_SUPPLIER_CONVERSION");
        }

        var companyId = await ResolveCompanyAsync(settlement, cancellationToken);
        if (companyId is null)
            return (0, "SETTLEMENT_COMPANY_UNKNOWN");

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
    /// The settlement carries no company of its own. Either contract proves one; the purchase
    /// contract is asked first because the payable leg is the one the sarraf actually settles.
    /// Two contracts owned by different companies would make this settlement span companies, so
    /// that stays unresolved rather than guessed.
    /// </summary>
    private async Task<int?> ResolveCompanyAsync(ThreeWaySettlement settlement, CancellationToken cancellationToken)
    {
        var contractIds = new[] { settlement.SupplierPurchaseContractId, settlement.CustomerSaleContractId }
            .Where(x => x.HasValue)
            .Select(x => x!.Value)
            .Distinct()
            .ToList();
        if (contractIds.Count == 0)
            return null;

        var companyIds = await db.Contracts
            .AsNoTracking()
            .Where(x => contractIds.Contains(x.Id))
            .Select(x => x.CompanyId)
            .Distinct()
            .ToListAsync(cancellationToken);

        return companyIds.Count == 1 ? companyIds[0] : null;
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

    private ThreeWaySettlementAccountingResult Skipped(
        ThreeWaySettlement settlement,
        int companyId,
        string reason)
    {
        LogOutcome(settlement, companyId, 0m, PaymentPostingStatus.Skipped, reason);
        return new ThreeWaySettlementAccountingResult(PaymentPostingStatus.Skipped, null, reason);
    }

    private void LogOutcome(
        ThreeWaySettlement settlement,
        int companyId,
        decimal journalDebitTotal,
        PaymentPostingStatus status,
        string? reason)
    {
        // Legacy writes two Debit rows: CustomerPaidUsd and SupplierAcceptedUsd. The journal
        // debits the supplier leg and any loss, so its debit total equals CustomerPaidUsd
        // whenever the customer paid the more of the two.
        logger.LogInformation(
            "Three-way settlement accounting pilot comparison: SettlementId {SettlementId}, CompanyId {CompanyId}, SarrafId {SarrafId}, CustomerId {CustomerId}, SupplierId {SupplierId}, LegacyCustomerPaidUsd {LegacyCustomerPaidUsd}, LegacySupplierAcceptedUsd {LegacySupplierAcceptedUsd}, LegacyDifferenceUsd {LegacyDifferenceUsd}, JournalDebitTotal {JournalDebitTotal}, PostingStatus {PostingStatus}, SkipOrFailureReason {SkipOrFailureReason}",
            settlement.Id,
            companyId,
            settlement.SarrafId,
            settlement.CustomerId,
            settlement.SupplierId,
            settlement.CustomerPaidUsd,
            settlement.SupplierAcceptedUsd,
            settlement.DifferenceUsd,
            journalDebitTotal,
            status,
            reason);
    }

    private void LogFailure(ThreeWaySettlement settlement, Exception exception)
    {
        var failureReason = exception is AccountingValidationException validation
            ? validation.Code
            : exception.GetType().Name;
        logger.LogError(
            exception,
            "Three-way settlement accounting pilot posting failed for SettlementId {SettlementId} with FailureReason {FailureReason}",
            settlement.Id,
            failureReason);
    }
}
