using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PTGOilSystem.Web.Configuration;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;

namespace PTGOilSystem.Web.Services.Accounting;

public sealed record ShortageChargeAccountingResult(
    PaymentPostingStatus Status,
    JournalEntry? Journal,
    string? Reason);

public interface IShortageChargeAccountingAdapter
{
    Task<ShortageChargeAccountingResult> TryPostShortageChargeAsync(
        InventoryTransportReceipt receipt,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Stage 8 dual-write pilot for the shortage a carrier is charged for.
///
///   Dr Freight Payable  (party = service provider or driver)
///   Cr Inventory Loss
///
/// The debit mirrors the legacy rows exactly: SourceType "ShortageCharge", debiting the responsible
/// carrier for ShortageChargeUsd — split per owning company when one truck carried the cargo of
/// several, so no owner is billed for another owner's goods; the shares still add up to
/// ShortageChargeUsd. Freight Payable is that carrier's control
/// account — the same account Stage 5 credits when their freight is accrued — so debiting it is
/// what "they owe us for what did not arrive" means in a chart with one payable per party type.
/// The legacy figure for the freight itself is untouched here, exactly as the legacy flow leaves
/// it untouched; the two meet in the carrier's balance rather than in one row.
///
/// The credit answers the confirmed decision: the charge recovers the loss, so it offsets
/// account 5400 rather than being recognised as separate income. The recovery and the write-off
/// therefore net against each other, which is what makes a fully recovered shortage cost nothing.
///
/// Amounts are USD at rate 1 because ShortageChargeUsd is derived in USD, so the rounding trap
/// that keeps non-USD via-sarraf payments legacy-only cannot arise here.
///
/// Known limitation — no reversal path: the legacy group-transfer cancellation cancels the
/// freight expenses and their ledger rows but leaves the "ShortageCharge" row standing, and no
/// other path deletes it. There is no legacy cancellation to mirror, so this adapter posts only.
/// Should that row ever gain a cancellation, this needs a matching reversal before the flag is
/// enabled.
/// </summary>
public sealed class ShortageChargeAccountingAdapter(
    ApplicationDbContext db,
    IAccountingPostingService postingService,
    IAccountingJournalNumberGenerator journalNumberGenerator,
    IOptions<AccountingOptions> options,
    ILogger<ShortageChargeAccountingAdapter> logger)
    : IShortageChargeAccountingAdapter
{
    public const string SourceModule = "ShortageCharge";
    public const string SourceEntityType = nameof(InventoryTransportReceipt);

    private readonly AccountingOptions _options = options.Value;

    // One truck can carry the cargo of several internal companies at once. The header contract is
    // only the leg's first share, so who is charged for what did not arrive has to be read from the
    // source allocations — the same ownership the receipt already split the inbound stock and the
    // loss event with.
    private readonly IInventoryTransportLegOwnershipResolver _ownership =
        new InventoryTransportLegOwnershipResolver(db);

    private readonly ISystemCompanyProvider _systemCompany = new SystemCompanyProvider(db);

    public async Task<ShortageChargeAccountingResult> TryPostShortageChargeAsync(
        InventoryTransportReceipt receipt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(receipt);

        if (!_options.Enabled)
            return Skipped(receipt, 0, "ACCOUNTING_DISABLED");
        if (!_options.Pilots.ShortageCharge)
            return Skipped(receipt, 0, "PILOT_DISABLED");

        var leg = await db.InventoryTransportLegs
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == receipt.InventoryTransportLegId, cancellationToken);
        if (leg is null)
            return Skipped(receipt, 0, "TRANSPORT_LEG_NOT_FOUND");

        // The legacy row's own gates, mirrored: a company-owned truck is never charged, and the
        // charge lands on the service provider when there is one, otherwise the driver.
        if (receipt.IsCancelled)
            return Skipped(receipt, 0, "RECEIPT_CANCELLED");
        if (receipt.OperationalAssetId.HasValue)
            return Skipped(receipt, 0, "OPERATIONAL_ASSET_NOT_CHARGED");

        var amountUsd = receipt.ShortageChargeUsd ?? 0m;
        if (amountUsd <= 0m)
            return Skipped(receipt, 0, "NO_SHORTAGE_CHARGE");

        var partyType = receipt.ServiceProviderId.HasValue
            ? AccountingPartyType.ServiceProvider
            : AccountingPartyType.Driver;
        var partyId = receipt.ServiceProviderId ?? leg.DriverId;
        if (partyId is null)
            return Skipped(receipt, 0, "PARTY_MISSING");

        var slices = await _ownership.ResolveCompanyOwnershipSlicesAsync(leg, cancellationToken);
        if (slices.Count == 0)
            return Skipped(receipt, 0, "SHORTAGE_COMPANY_UNKNOWN");
        var multiCompany = slices.Count > 1;

        // The claim follows the cargo: each owner is charged for exactly its own share of the
        // shortage, and the shares add back up to ShortageChargeUsd to the last cent.
        var amounts = InventoryTransportLegOwnershipResolver.ProportionalSplit(
            amountUsd, slices.Select(x => x.QuantityMt).ToList());

        JournalEntry? firstJournal = null;
        JournalEntry? firstDuplicate = null;
        string? firstSkipReason = null;

        for (var index = 0; index < slices.Count; index++)
        {
            var slice = slices[index];
            var companyId = slice.CompanyId;
            var sliceAmountUsd = decimal.Round(amounts[index], 4, MidpointRounding.AwayFromZero);
            if (sliceAmountUsd <= 0m)
            {
                firstSkipReason ??= "NO_SHORTAGE_CHARGE";
                continue;
            }

            var skipReason = await ResolveSkipReasonAsync(companyId, cancellationToken);
            if (skipReason is not null)
            {
                // A co-owner the ledger cannot take is left out of the journal — never billed to
                // whoever heads the leg, and never allowed to fail the receipt being saved.
                firstSkipReason ??= skipReason;
                LogOutcome(receipt, companyId, sliceAmountUsd, 0m, PaymentPostingStatus.Skipped, skipReason);
                continue;
            }

            var sourceEventId = CreatedEventId(receipt.Id, companyId, multiCompany);
            var existing = await FindJournalAsync(companyId, sourceEventId, cancellationToken);
            if (existing is not null)
            {
                firstDuplicate ??= existing;
                LogOutcome(receipt, companyId, sliceAmountUsd, existing.Lines.Sum(x => x.Debit),
                    PaymentPostingStatus.Duplicate, "DUPLICATE_SOURCE_EVENT");
                continue;
            }

            var settings = await db.AccountingSettings
                .AsNoTracking()
                .SingleAsync(x => x.CompanyId == companyId, cancellationToken);

            // Metadata only — nothing reads it back to compute money. A single-company leg names
            // its header contract exactly as before; a multi-company leg names the owner's own
            // contract when it has just one, and nothing when that owner brought several along.
            var lineContractId = multiCompany ? slice.SingleContractId : leg.SourcePurchaseContractId;

            var request = new AccountingPostRequest(
                companyId,
                journalNumberGenerator.ForShortageCharge(companyId, receipt.Id),
                receipt.ReceiptDate.Date,
                receipt.ReceiptDate.Date,
                receipt.ReceiptDate.Date,
                SourceModule,
                [
                    new AccountingPostLine(
                        settings.FreightPayableAccountId,
                        Debit: sliceAmountUsd,
                        Credit: 0m,
                        SystemCurrency.BaseCurrencyCode,
                        sliceAmountUsd,
                        1m,
                        partyType,
                        partyId,
                        ContractId: lineContractId,
                        ShipmentId: leg.ShipmentId,
                        Description: $"Shortage charged for transport leg #{leg.Id}, receipt #{receipt.Id}"),
                    new AccountingPostLine(
                        settings.InventoryLossAccountId,
                        Debit: 0m,
                        Credit: sliceAmountUsd,
                        SystemCurrency.BaseCurrencyCode,
                        sliceAmountUsd,
                        1m,
                        ContractId: lineContractId,
                        ShipmentId: leg.ShipmentId,
                        Description: "Shortage recovered from carrier")
                ],
                SourceEventId: sourceEventId,
                SourceEntityType: SourceEntityType,
                SourceEntityId: receipt.Id,
                Description: $"Shortage charge for transport receipt #{receipt.Id} on {receipt.ReceiptDate:yyyy-MM-dd}");

            try
            {
                var journal = await postingService.PostAsync(request, cancellationToken);
                firstJournal ??= journal;
                LogOutcome(receipt, companyId, sliceAmountUsd, journal.Lines.Sum(x => x.Debit),
                    PaymentPostingStatus.Posted, null);
            }
            catch (Exception exception)
            {
                LogFailure(receipt, exception);
                throw;
            }
        }

        // Posted beats duplicate beats skipped, so a retry on a leg one of whose owners the ledger
        // cannot take still reads as the duplicate it is.
        if (firstJournal is not null)
            return new ShortageChargeAccountingResult(PaymentPostingStatus.Posted, firstJournal, null);
        if (firstDuplicate is not null)
            return new ShortageChargeAccountingResult(
                PaymentPostingStatus.Duplicate, firstDuplicate, "DUPLICATE_SOURCE_EVENT");
        return Skipped(receipt, 0, firstSkipReason ?? "NOTHING_TO_POST");
    }

    public static string BuildCreatedSourceEventId(int transportReceiptId)
        => $"ShortageCharge:{transportReceiptId}:Created";

    /// <summary>
    /// The per-company event id a multi-company receipt posts under. A single-company receipt keeps
    /// the plain id, so nothing already written changes shape.
    /// </summary>
    public static string BuildCreatedSourceEventId(int transportReceiptId, int companyId)
        => $"{BuildCreatedSourceEventId(transportReceiptId)}:Company:{companyId}";

    private static string CreatedEventId(int transportReceiptId, int companyId, bool multiCompany)
        => multiCompany
            ? BuildCreatedSourceEventId(transportReceiptId, companyId)
            : BuildCreatedSourceEventId(transportReceiptId);

    /// <summary>
    /// Whether this company can be charged at all — the same settings and account checks the
    /// single-company path always ran, now asked once per owning company, plus the owner check.
    ///
    /// The ledger itself is still single-company: <c>AccountingPostingService</c> refuses any
    /// journal whose company is not the system owner. Asking that here turns what would be a hard
    /// failure in the middle of saving a receipt into an ordinary, logged skip
    /// (<c>COMPANY_NOT_OWNER</c>), so a co-owner's share is left out of the ledger instead of being
    /// billed to the owner or blocking the operation.
    /// </summary>
    private async Task<string?> ResolveSkipReasonAsync(int companyId, CancellationToken cancellationToken)
    {
        var ownerCompanyId = await _systemCompany.FindOwnerCompanyIdAsync(cancellationToken);
        if (ownerCompanyId is null)
            return "SYSTEM_OWNER_NOT_CONFIGURED";
        if (ownerCompanyId.Value != companyId)
            return "COMPANY_NOT_OWNER";

        var settings = await db.AccountingSettings
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.CompanyId == companyId, cancellationToken);
        if (settings is null)
            return "ACCOUNTING_SETTINGS_MISSING";
        if (!string.Equals(settings.FunctionalCurrencyCode?.Trim(), "USD", StringComparison.OrdinalIgnoreCase))
            return "UNSUPPORTED_FUNCTIONAL_CURRENCY";

        var accountIds = new[] { settings.FreightPayableAccountId, settings.InventoryLossAccountId };
        var validAccountCount = await db.Accounts.AsNoTracking().CountAsync(
            x => accountIds.Contains(x.Id) && x.CompanyId == companyId && x.IsActive,
            cancellationToken);
        if (validAccountCount != accountIds.Distinct().Count())
            return "ACCOUNTING_SETTINGS_INVALID_ACCOUNTS";

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

    private ShortageChargeAccountingResult Skipped(
        InventoryTransportReceipt receipt,
        int companyId,
        string reason)
    {
        LogOutcome(receipt, companyId, receipt.ShortageChargeUsd ?? 0m, 0m, PaymentPostingStatus.Skipped, reason);
        return new ShortageChargeAccountingResult(PaymentPostingStatus.Skipped, null, reason);
    }

    private void LogOutcome(
        InventoryTransportReceipt receipt,
        int companyId,
        decimal expectedAmountUsd,
        decimal journalDebitTotal,
        PaymentPostingStatus status,
        string? reason)
    {
        // Legacy writes one Debit row of ShortageChargeUsd, so the journal must debit the same.
        logger.LogInformation(
            "Shortage charge accounting pilot comparison: TransportReceiptId {TransportReceiptId}, CompanyId {CompanyId}, ServiceProviderId {ServiceProviderId}, ShortageQuantityMt {ShortageQuantityMt}, LegacyAmountUsd {LegacyAmountUsd}, JournalDebitTotal {JournalDebitTotal}, Difference {Difference}, PostingStatus {PostingStatus}, SkipOrFailureReason {SkipOrFailureReason}",
            receipt.Id,
            companyId,
            receipt.ServiceProviderId,
            receipt.ShortageQuantityMt,
            expectedAmountUsd,
            journalDebitTotal,
            journalDebitTotal - expectedAmountUsd,
            status,
            reason);
    }

    private void LogFailure(InventoryTransportReceipt receipt, Exception exception)
    {
        var failureReason = exception is AccountingValidationException validation
            ? validation.Code
            : exception.GetType().Name;
        logger.LogError(
            exception,
            "Shortage charge accounting pilot posting failed for TransportReceiptId {TransportReceiptId} with FailureReason {FailureReason}",
            receipt.Id,
            failureReason);
    }
}
