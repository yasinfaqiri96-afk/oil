using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PTGOilSystem.Web.Configuration;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services.Time;

namespace PTGOilSystem.Web.Services.Accounting;

public sealed record InventoryTransferAccountingResult(
    PaymentPostingStatus Status,
    JournalEntry? Journal,
    string? Reason);

public interface IInventoryTransferAccountingAdapter
{
    Task<InventoryTransferAccountingResult> TryPostLegLoadAsync(
        InventoryTransportLeg leg,
        CancellationToken cancellationToken = default);

    Task<InventoryTransferAccountingResult> TryPostLegLoadReversalAsync(
        InventoryTransportLeg leg,
        CancellationToken cancellationToken = default);

    Task<InventoryTransferAccountingResult> TryPostReceiptAsync(
        InventoryTransportReceipt receipt,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Dual-write pilot for inter-terminal transfers, which the valuation pool cannot do without.
///
/// The pool is keyed by (company, product, terminal) because goods at different terminals
/// genuinely cost different amounts to have got there. Legacy moves a transfer as two inventory
/// movements — Out of the source terminal when the leg loads, In at the destination when a
/// receipt lands — with real time in between. Until this pilot, neither movement touched a pool,
/// so a transfer moved tonnes and no money: the source pool kept paying for goods it no longer
/// held, and a sale at the destination valued against whatever that pool happened to contain.
/// That is why Cogs is unsafe while this is off.
///
///   Leg load:  Dr Inventory In Transit   Cr Inventory (source terminal, at its moving average)
///   Receipt:   Dr Inventory (destination) + Dr Inventory Loss (shortage)   Cr Inventory In Transit
///
/// Account 1310 is what makes the two halves one transaction. Goods in a truck belong to nobody's
/// terminal, and dating the destination debit at the load date would be a lie about where they
/// were; 1310 holds their cost for exactly as long as they are on the road. A leg still in
/// transit at period end leaves a balance there, which is the correct answer, not a leak.
///
/// The shortage debit to 5400 is the other half of the Stage 8 shortage charge, not a double
/// count of it. Stage 8 posts Dr Freight Payable / Cr Inventory Loss for what the carrier owes,
/// leaving 5400 with a naked credit; the cost of the barrels that never arrived has to come out
/// of 1310, where those barrels actually sit, and 5400 is where it lands. Net 5400 is then the
/// real loss: what the goods cost minus what the carrier is charged for them. Stage 8's
/// InventoryLoss adapter deliberately skips ReceiptShortage events for this reason — it would
/// have taken the cost from a terminal pool the goods never reached.
/// </summary>
public sealed class InventoryTransferAccountingAdapter(
    ApplicationDbContext db,
    IAccountingPostingService postingService,
    IAccountingJournalNumberGenerator journalNumberGenerator,
    IInventoryValuationService valuation,
    IOptions<AccountingOptions> options,
    ILogger<InventoryTransferAccountingAdapter> logger)
    : IInventoryTransferAccountingAdapter
{
    public const string SourceModule = "InventoryTransfer";
    public const string LegEntityType = nameof(InventoryTransportLeg);
    public const string ReceiptEntityType = nameof(InventoryTransportReceipt);

    // Quantities are stored to four decimals, so anything under half of the last place is the
    // same figure written twice, not a real remainder.
    private const decimal QuantityTolerance = 0.0001m;

    private readonly AccountingOptions _options = options.Value;

    // A leg is one physical movement, but its cargo can belong to several internal companies at
    // once (10 MT on P-016/company A, 20 MT on P-017/company B in the same 30 MT truck). The pool
    // is keyed by company, so the ownership of every share has to be read from the source
    // allocations before anything is consumed — the leg's header contract is only its first share.
    private readonly IInventoryTransportLegOwnershipResolver _ownership =
        new InventoryTransportLegOwnershipResolver(db);

    private readonly ISystemCompanyProvider _systemCompany = new SystemCompanyProvider(db);

    /// <summary>
    /// The leg has loaded: the goods have left the source terminal and are on the road. Takes
    /// their cost out of the source pool at its moving average and parks it in transit.
    ///
    /// One journal per owning company: each company gives up exactly its own share and no more.
    /// A leg owned by a single company posts exactly what it always did, under the same event id.
    /// </summary>
    public async Task<InventoryTransferAccountingResult> TryPostLegLoadAsync(
        InventoryTransportLeg leg,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(leg);

        if (!_options.Enabled)
            return SkippedLeg(leg, 0, "ACCOUNTING_DISABLED");
        if (!_options.Pilots.InventoryTransfer)
            return SkippedLeg(leg, 0, "PILOT_DISABLED");
        if (leg.QuantityMt <= 0m)
            return SkippedLeg(leg, 0, "INVALID_LEG_QUANTITY");

        var slices = await _ownership.ResolveCompanyOwnershipSlicesAsync(leg, cancellationToken);
        if (slices.Count == 0)
            return SkippedLeg(leg, 0, "LEG_COMPANY_UNKNOWN");
        var multiCompany = slices.Count > 1;

        // Each owner is handled on its own: it gives up its own share out of its own pool and
        // nothing more. A company the ledger cannot accept — today, any company that is not the
        // system owner — is skipped rather than charged to whoever happens to head the leg.
        var consumed = new List<(LegCompanyOwnershipSlice Slice, decimal CostUsd)>(slices.Count);
        JournalEntry? firstJournal = null;
        JournalEntry? firstDuplicate = null;
        string? firstSkipReason = null;

        try
        {
            foreach (var slice in slices)
            {
                var skipReason = await ResolveSkipReasonAsync(slice.CompanyId, cancellationToken);
                if (skipReason is not null)
                {
                    firstSkipReason ??= skipReason;
                    LogLegOutcome(leg, slice.CompanyId, 0m, PaymentPostingStatus.Skipped, skipReason);
                    continue;
                }

                var existing = await FindJournalAsync(
                    slice.CompanyId, LegLoadedEventId(leg.Id, slice.CompanyId, multiCompany), cancellationToken);
                if (existing is not null)
                {
                    firstDuplicate ??= existing;
                    LogLegOutcome(leg, slice.CompanyId, existing.Lines.Sum(x => x.Debit),
                        PaymentPostingStatus.Duplicate, "DUPLICATE_SOURCE_EVENT");
                    continue;
                }

                var consumption = await valuation.TryConsumeAsync(
                    slice.CompanyId, leg.ProductId, leg.SourceTerminalId, slice.QuantityMt, cancellationToken);
                if (!consumption.Succeeded || consumption.CostUsd <= 0m)
                {
                    if (consumption.Succeeded)
                    {
                        await valuation.ReturnAsync(
                            slice.CompanyId, leg.ProductId, leg.SourceTerminalId,
                            slice.QuantityMt, consumption.CostUsd, cancellationToken);
                    }

                    var reason = consumption.Succeeded
                        ? "INVENTORY_NOT_VALUED"
                        : consumption.Reason ?? "INVENTORY_NOT_VALUED";
                    firstSkipReason ??= reason;
                    LogLegOutcome(leg, slice.CompanyId, 0m, PaymentPostingStatus.Skipped, reason);
                    continue;
                }

                var costUsd = consumption.CostUsd;
                consumed.Add((slice, costUsd));

                var settings = await db.AccountingSettings
                    .AsNoTracking()
                    .SingleAsync(x => x.CompanyId == slice.CompanyId, cancellationToken);
                var contractId = LineContractId(leg, slice, multiCompany);

                var request = new AccountingPostRequest(
                    slice.CompanyId,
                    journalNumberGenerator.ForTransportLegLoad(slice.CompanyId, leg.Id),
                    leg.LoadedDate.Date,
                    leg.LoadedDate.Date,
                    leg.LoadedDate.Date,
                    SourceModule,
                    [
                        new AccountingPostLine(
                            settings.InventoryInTransitAccountId,
                            Debit: costUsd,
                            Credit: 0m,
                            SystemCurrency.BaseCurrencyCode,
                            costUsd,
                            1m,
                            ContractId: contractId,
                            ShipmentId: leg.ShipmentId,
                            ProductId: leg.ProductId,
                            Description: $"Goods in transit on leg #{leg.Id} ({slice.QuantityMt} MT)"),
                        new AccountingPostLine(
                            settings.InventoryAccountId,
                            Debit: 0m,
                            Credit: costUsd,
                            SystemCurrency.BaseCurrencyCode,
                            costUsd,
                            1m,
                            ContractId: contractId,
                            ShipmentId: leg.ShipmentId,
                            TankId: leg.SourceStorageTankId,
                            ProductId: leg.ProductId,
                            Description: "Goods dispatched from source terminal")
                    ],
                    SourceEventId: LegLoadedEventId(leg.Id, slice.CompanyId, multiCompany),
                    SourceEntityType: LegEntityType,
                    SourceEntityId: leg.Id,
                    Description: $"Transport leg #{leg.Id} loaded on {leg.LoadedDate:yyyy-MM-dd}");

                var journal = await postingService.PostAsync(request, cancellationToken);
                firstJournal ??= journal;
                LogLegOutcome(leg, slice.CompanyId, journal.Lines.Sum(x => x.Debit),
                    PaymentPostingStatus.Posted, null);
            }
        }
        catch (Exception exception)
        {
            // Every pool touched in this call has already given its goods up; put them all back so
            // a failed posting leaves the valuation exactly as it was. The journals already
            // written go back with the caller's transaction — one leg, one atomic outcome.
            await ReturnConsumedAsync(leg, consumed, cancellationToken);
            LogLegFailure(leg, exception);
            throw;
        }

        // Posted beats duplicate beats skipped: the caller is told the strongest thing that
        // actually happened, so a retry on a leg one of whose owners the ledger cannot take still
        // reads as the duplicate it is.
        if (firstJournal is not null)
            return new InventoryTransferAccountingResult(PaymentPostingStatus.Posted, firstJournal, null);
        if (firstDuplicate is not null)
            return new InventoryTransferAccountingResult(
                PaymentPostingStatus.Duplicate, firstDuplicate, "DUPLICATE_SOURCE_EVENT");
        return new InventoryTransferAccountingResult(
            PaymentPostingStatus.Skipped, null, firstSkipReason ?? "NOTHING_TO_POST");
    }

    private async Task ReturnConsumedAsync(
        InventoryTransportLeg leg,
        IReadOnlyList<(LegCompanyOwnershipSlice Slice, decimal CostUsd)> consumed,
        CancellationToken cancellationToken)
    {
        foreach (var (slice, costUsd) in consumed)
        {
            await valuation.ReturnAsync(
                slice.CompanyId, leg.ProductId, leg.SourceTerminalId,
                slice.QuantityMt, costUsd, cancellationToken);
        }
    }

    /// <summary>
    /// Undoes a load. Legacy deletes the outbound movement outright, guarded so that this can
    /// only happen while nothing downstream has consumed the leg — so nothing can have left
    /// transit yet, and the whole cost goes back to the source pool it came from.
    /// </summary>
    public async Task<InventoryTransferAccountingResult> TryPostLegLoadReversalAsync(
        InventoryTransportLeg leg,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(leg);

        if (!_options.Enabled)
            return SkippedLeg(leg, 0, "ACCOUNTING_DISABLED");
        if (!_options.Pilots.InventoryTransfer)
            return SkippedLeg(leg, 0, "PILOT_DISABLED");

        var slices = await _ownership.ResolveCompanyOwnershipSlicesAsync(leg, cancellationToken);
        if (slices.Count == 0)
            return SkippedLeg(leg, 0, "LEG_COMPANY_UNKNOWN");
        var multiCompany = slices.Count > 1;

        JournalEntry? firstJournal = null;
        JournalEntry? firstDuplicate = null;
        string? firstSkipReason = null;

        foreach (var slice in slices)
        {
            var reversedEventId = LegLoadReversedEventId(leg.Id, slice.CompanyId, multiCompany);
            var alreadyReversed = await FindJournalAsync(slice.CompanyId, reversedEventId, cancellationToken);
            if (alreadyReversed is not null)
            {
                firstDuplicate ??= alreadyReversed;
                LogLegOutcome(leg, slice.CompanyId, alreadyReversed.Lines.Sum(x => x.Debit),
                    PaymentPostingStatus.Duplicate, "DUPLICATE_SOURCE_EVENT");
                continue;
            }

            var original = await FindJournalAsync(
                slice.CompanyId, LegLoadedEventId(leg.Id, slice.CompanyId, multiCompany), cancellationToken);
            if (original is null)
            {
                firstSkipReason ??= "ORIGINAL_JOURNAL_NOT_POSTED";
                LogLegOutcome(leg, slice.CompanyId, 0m, PaymentPostingStatus.Skipped, "ORIGINAL_JOURNAL_NOT_POSTED");
                continue;
            }

            // Goods that already left transit cannot be put back into the source pool: part of
            // their cost is sitting at the destination and taking it back here would count it twice.
            var consumedCostUsd = await SumReceiptedInTransitCostAsync(
                slice.CompanyId, leg.Id, multiCompany, cancellationToken);
            if (consumedCostUsd > 0m)
            {
                firstSkipReason ??= "LEG_ALREADY_RECEIPTED";
                LogLegOutcome(leg, slice.CompanyId, 0m, PaymentPostingStatus.Skipped, "LEG_ALREADY_RECEIPTED");
                continue;
            }

            var request = new AccountingReversalRequest(
                original.Id,
                journalNumberGenerator.ForTransportLegLoadReversal(slice.CompanyId, leg.Id),
                AfghanistanBusinessClock.SystemToday,
                SourceModule,
                reversedEventId,
                $"Reversal of transport leg #{leg.Id} load");

            try
            {
                var journal = await postingService.ReverseAsync(request, cancellationToken);
                await valuation.ReturnAsync(
                    slice.CompanyId,
                    leg.ProductId,
                    leg.SourceTerminalId,
                    slice.QuantityMt,
                    original.Lines.Sum(x => x.Debit),
                    cancellationToken);
                firstJournal ??= journal;
                LogLegOutcome(leg, slice.CompanyId, journal.Lines.Sum(x => x.Debit),
                    PaymentPostingStatus.Posted, null);
            }
            catch (Exception exception)
            {
                LogLegFailure(leg, exception);
                throw;
            }
        }

        // Posted beats duplicate beats skipped: the caller is told the strongest thing that
        // actually happened, so a retry on a leg one of whose owners the ledger cannot take still
        // reads as the duplicate it is.
        if (firstJournal is not null)
            return new InventoryTransferAccountingResult(PaymentPostingStatus.Posted, firstJournal, null);
        if (firstDuplicate is not null)
            return new InventoryTransferAccountingResult(
                PaymentPostingStatus.Duplicate, firstDuplicate, "DUPLICATE_SOURCE_EVENT");
        return new InventoryTransferAccountingResult(
            PaymentPostingStatus.Skipped, null, firstSkipReason ?? "NOTHING_TO_POST");
    }

    /// <summary>
    /// A receipt has landed goods at the destination terminal. Takes their share of the cost out
    /// of transit, puts the received part into the destination pool, and writes the shortage off.
    ///
    /// Only the ToInventory path is in scope, and only when the receipt received something —
    /// exactly the condition under which legacy writes the inbound movement. A direct sale or a
    /// direct dispatch out of a truck never reaches a terminal pool, so there is no destination
    /// average for it to join; those receipts are skipped and their cost stays in transit.
    /// </summary>
    public async Task<InventoryTransferAccountingResult> TryPostReceiptAsync(
        InventoryTransportReceipt receipt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(receipt);

        if (!_options.Enabled)
            return SkippedReceipt(receipt, 0, "ACCOUNTING_DISABLED");
        if (!_options.Pilots.InventoryTransfer)
            return SkippedReceipt(receipt, 0, "PILOT_DISABLED");
        if (receipt.IsCancelled)
            return SkippedReceipt(receipt, 0, "RECEIPT_CANCELLED");
        if (receipt.ReceiptDestination != InventoryTransportReceiptDestination.ToInventory)
            return SkippedReceipt(receipt, 0, "RECEIPT_DESTINATION_NOT_INVENTORY");
        if (receipt.ReceivedQuantityMt <= 0m)
            return SkippedReceipt(receipt, 0, "NO_QUANTITY_RECEIVED");
        if (!receipt.DestinationTerminalId.HasValue)
            return SkippedReceipt(receipt, 0, "DESTINATION_TERMINAL_UNKNOWN");
        if (receipt.ShortageQuantityMt < 0m)
            return SkippedReceipt(receipt, 0, "INVALID_SHORTAGE_QUANTITY");

        var leg = await db.InventoryTransportLegs
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == receipt.InventoryTransportLegId, cancellationToken);
        if (leg is null)
            return SkippedReceipt(receipt, 0, "TRANSPORT_LEG_NOT_FOUND");

        var slices = await _ownership.ResolveCompanyOwnershipSlicesAsync(leg, cancellationToken);
        if (slices.Count == 0)
            return SkippedReceipt(receipt, 0, "LEG_COMPANY_UNKNOWN");
        var multiCompany = slices.Count > 1;

        // What arrived is split between the owners in the same proportion as what left, so the
        // shortage falls on each of them in proportion too: 0.6 MT short on a 10/20 truck is
        // 0.2 MT against company A and 0.4 MT against company B.
        var sliceQuantities = slices.Select(x => x.QuantityMt).ToList();
        var receivedByCompany = InventoryTransportLegOwnershipResolver
            .ProportionalSplit(receipt.ReceivedQuantityMt, sliceQuantities);
        var shortageByCompany = InventoryTransportLegOwnershipResolver
            .ProportionalSplit(receipt.ShortageQuantityMt, sliceQuantities);

        JournalEntry? firstJournal = null;
        JournalEntry? firstDuplicate = null;
        string? firstSkipReason = null;

        for (var index = 0; index < slices.Count; index++)
        {
            var slice = slices[index];
            var companyId = slice.CompanyId;
            var sliceReceivedMt = receivedByCompany[index];
            var sliceShortageMt = shortageByCompany[index];

            var companySkipReason = await ResolveSkipReasonAsync(companyId, cancellationToken);
            if (companySkipReason is not null)
            {
                firstSkipReason ??= companySkipReason;
                LogReceiptOutcome(receipt, leg, companyId, 0m, 0m, 0m,
                    PaymentPostingStatus.Skipped, companySkipReason);
                continue;
            }

            var sourceEventId = ReceiptEventId(receipt.Id, companyId, multiCompany);
            var existing = await FindJournalAsync(companyId, sourceEventId, cancellationToken);
            if (existing is not null)
            {
                firstDuplicate ??= existing;
                LogReceiptOutcome(receipt, leg, companyId, 0m, 0m,
                    existing.Lines.Sum(x => x.Debit), PaymentPostingStatus.Duplicate, "DUPLICATE_SOURCE_EVENT");
                continue;
            }

            var loadJournal = await FindJournalAsync(
                companyId, LegLoadedEventId(leg.Id, companyId, multiCompany), cancellationToken);
            if (loadJournal is null)
            {
                firstSkipReason ??= "LEG_LOAD_NOT_POSTED";
                LogReceiptOutcome(receipt, leg, companyId, 0m, 0m, 0m,
                    PaymentPostingStatus.Skipped, "LEG_LOAD_NOT_POSTED");
                continue;
            }

            var loadReversed = await FindJournalAsync(
                companyId, LegLoadReversedEventId(leg.Id, companyId, multiCompany), cancellationToken);
            if (loadReversed is not null)
            {
                firstSkipReason ??= "LEG_LOAD_REVERSED";
                LogReceiptOutcome(receipt, leg, companyId, 0m, 0m, 0m,
                    PaymentPostingStatus.Skipped, "LEG_LOAD_REVERSED");
                continue;
            }

            // What is still on the road for this owner: its load cost less whatever earlier
            // receipts already took out of transit, and its share of the leg less what those same
            // receipts accounted for. Both sides are read back from the journals actually posted
            // rather than recomputed from a schedule, so a receipt this pilot skipped simply never
            // consumed anything and the arithmetic still adds up.
            var inTransitCostUsd = loadJournal.Lines.Sum(x => x.Debit);
            var consumedCostUsd = await SumReceiptedInTransitCostAsync(
                companyId, leg.Id, multiCompany, cancellationToken);
            var consumedQuantityMt = await SumReceiptedQuantityAsync(
                companyId, leg.Id, sliceQuantities, index, cancellationToken);

            var remainingCostUsd = inTransitCostUsd - consumedCostUsd;
            var remainingQuantityMt = slice.QuantityMt - consumedQuantityMt;
            if (remainingCostUsd <= 0m || remainingQuantityMt <= 0m)
            {
                firstSkipReason ??= "NOTHING_LEFT_IN_TRANSIT";
                LogReceiptOutcome(receipt, leg, companyId, 0m, 0m, 0m,
                    PaymentPostingStatus.Skipped, "NOTHING_LEFT_IN_TRANSIT");
                continue;
            }

            // A truck may be allowed to hand over more than the leg still owes; the extra has no
            // cost in transit to draw on, and inventing one would misprice the destination.
            var receiptQuantityMt = sliceReceivedMt + sliceShortageMt;
            if (receiptQuantityMt > remainingQuantityMt + QuantityTolerance)
            {
                firstSkipReason ??= "RECEIPT_EXCEEDS_IN_TRANSIT";
                LogReceiptOutcome(receipt, leg, companyId, 0m, 0m, 0m,
                    PaymentPostingStatus.Skipped, "RECEIPT_EXCEEDS_IN_TRANSIT");
                continue;
            }

            // Taking the last of the goods takes the last of the cost, so no crumb is stranded in
            // transit by rounding — the same rule the valuation pool uses when it empties.
            var isFinalDraw = receiptQuantityMt >= remainingQuantityMt - QuantityTolerance;
            var drawUsd = isFinalDraw
                ? remainingCostUsd
                : decimal.Round(remainingCostUsd * receiptQuantityMt / remainingQuantityMt, 4, MidpointRounding.AwayFromZero);
            if (drawUsd > remainingCostUsd)
                drawUsd = remainingCostUsd;
            if (drawUsd <= 0m)
            {
                firstSkipReason ??= "NOTHING_LEFT_IN_TRANSIT";
                LogReceiptOutcome(receipt, leg, companyId, 0m, 0m, 0m,
                    PaymentPostingStatus.Skipped, "NOTHING_LEFT_IN_TRANSIT");
                continue;
            }

            var shortageCostUsd = sliceShortageMt <= 0m
                ? 0m
                : decimal.Round(drawUsd * sliceShortageMt / receiptQuantityMt, 4, MidpointRounding.AwayFromZero);
            if (shortageCostUsd > drawUsd)
                shortageCostUsd = drawUsd;
            var receivedCostUsd = drawUsd - shortageCostUsd;
            if (receivedCostUsd <= 0m)
            {
                firstSkipReason ??= "NO_RECEIVED_COST";
                LogReceiptOutcome(receipt, leg, companyId, 0m, 0m, 0m,
                    PaymentPostingStatus.Skipped, "NO_RECEIVED_COST");
                continue;
            }

            var settings = await db.AccountingSettings
                .AsNoTracking()
                .SingleAsync(x => x.CompanyId == companyId, cancellationToken);
            var contractId = LineContractId(leg, slice, multiCompany);

            var lines = new List<AccountingPostLine>
            {
                new(
                    settings.InventoryAccountId,
                    Debit: receivedCostUsd,
                    Credit: 0m,
                    SystemCurrency.BaseCurrencyCode,
                    receivedCostUsd,
                    1m,
                    ContractId: contractId,
                    ShipmentId: leg.ShipmentId,
                    TankId: receipt.DestinationStorageTankId,
                    ProductId: leg.ProductId,
                    Description: $"Goods received at destination terminal ({sliceReceivedMt} MT)")
            };

            if (shortageCostUsd > 0m)
            {
                lines.Add(new AccountingPostLine(
                    settings.InventoryLossAccountId,
                    Debit: shortageCostUsd,
                    Credit: 0m,
                    SystemCurrency.BaseCurrencyCode,
                    shortageCostUsd,
                    1m,
                    ContractId: contractId,
                    ShipmentId: leg.ShipmentId,
                    ProductId: leg.ProductId,
                    Description: $"Cost of shortage on transport leg #{leg.Id} ({sliceShortageMt} MT)"));
            }

            lines.Add(new AccountingPostLine(
                settings.InventoryInTransitAccountId,
                Debit: 0m,
                Credit: drawUsd,
                SystemCurrency.BaseCurrencyCode,
                drawUsd,
                1m,
                ContractId: contractId,
                ShipmentId: leg.ShipmentId,
                ProductId: leg.ProductId,
                Description: $"Goods out of transit on leg #{leg.Id}"));

            var request = new AccountingPostRequest(
                companyId,
                journalNumberGenerator.ForTransportReceipt(companyId, receipt.Id),
                receipt.ReceiptDate.Date,
                receipt.ReceiptDate.Date,
                receipt.ReceiptDate.Date,
                SourceModule,
                lines,
                SourceEventId: sourceEventId,
                SourceEntityType: ReceiptEntityType,
                SourceEntityId: receipt.Id,
                Description: $"Transport receipt #{receipt.Id} on {receipt.ReceiptDate:yyyy-MM-dd}");

            JournalEntry journalEntry;
            try
            {
                journalEntry = await postingService.PostAsync(request, cancellationToken);
            }
            catch (Exception exception)
            {
                LogReceiptFailure(receipt, exception);
                throw;
            }

            // The journal and the destination pool move together: the pool is what a later sale
            // reads to price COGS, and the two must agree to the cent about what arrived.
            await valuation.ApplyReceiptAsync(
                companyId,
                leg.ProductId,
                receipt.DestinationTerminalId.Value,
                sliceReceivedMt,
                receivedCostUsd,
                cancellationToken);

            firstJournal ??= journalEntry;
            LogReceiptOutcome(receipt, leg, companyId, receivedCostUsd, shortageCostUsd,
                journalEntry.Lines.Sum(x => x.Debit), PaymentPostingStatus.Posted, null);
        }

        // Posted beats duplicate beats skipped: the caller is told the strongest thing that
        // actually happened, so a retry on a leg one of whose owners the ledger cannot take still
        // reads as the duplicate it is.
        if (firstJournal is not null)
            return new InventoryTransferAccountingResult(PaymentPostingStatus.Posted, firstJournal, null);
        if (firstDuplicate is not null)
            return new InventoryTransferAccountingResult(
                PaymentPostingStatus.Duplicate, firstDuplicate, "DUPLICATE_SOURCE_EVENT");
        return new InventoryTransferAccountingResult(
            PaymentPostingStatus.Skipped, null, firstSkipReason ?? "NOTHING_TO_POST");
    }

    public static string BuildLegLoadedSourceEventId(int transportLegId)
        => $"InventoryTransportLeg:{transportLegId}:Loaded";

    public static string BuildLegLoadReversedSourceEventId(int transportLegId)
        => $"InventoryTransportLeg:{transportLegId}:LoadReversed";

    public static string BuildReceiptSourceEventId(int transportReceiptId)
        => $"InventoryTransportReceipt:{transportReceiptId}:Received";

    /// <summary>
    /// A leg whose cargo belongs to more than one company posts one journal per owner, so the
    /// event id has to name the owner too. A single-owner leg keeps the original id unchanged,
    /// which is what makes every journal posted before this existed still findable.
    /// </summary>
    public static string BuildLegLoadedSourceEventId(int transportLegId, int companyId)
        => $"{BuildLegLoadedSourceEventId(transportLegId)}:Company:{companyId}";

    public static string BuildLegLoadReversedSourceEventId(int transportLegId, int companyId)
        => $"{BuildLegLoadReversedSourceEventId(transportLegId)}:Company:{companyId}";

    public static string BuildReceiptSourceEventId(int transportReceiptId, int companyId)
        => $"{BuildReceiptSourceEventId(transportReceiptId)}:Company:{companyId}";

    private static string LegLoadedEventId(int legId, int companyId, bool multiCompany)
        => multiCompany ? BuildLegLoadedSourceEventId(legId, companyId) : BuildLegLoadedSourceEventId(legId);

    private static string LegLoadReversedEventId(int legId, int companyId, bool multiCompany)
        => multiCompany
            ? BuildLegLoadReversedSourceEventId(legId, companyId)
            : BuildLegLoadReversedSourceEventId(legId);

    private static string ReceiptEventId(int receiptId, int companyId, bool multiCompany)
        => multiCompany ? BuildReceiptSourceEventId(receiptId, companyId) : BuildReceiptSourceEventId(receiptId);

    /// <summary>
    /// The contract dimension on the journal lines. It is a reference, not a driver of any
    /// figure: nothing reads it back to compute money. A single-owner leg keeps naming its header
    /// contract exactly as before; a multi-owner leg names the owner's own contract when it has
    /// just one, and nothing when the same owner brought several into the same truck.
    /// </summary>
    private static int? LineContractId(
        InventoryTransportLeg leg,
        LegCompanyOwnershipSlice slice,
        bool multiCompany)
        => multiCompany ? slice.SingleContractId : leg.SourcePurchaseContractId;

    /// <summary>
    /// Whether this company can be posted to at all — the same settings and account checks the
    /// single-company path always ran, now asked once per owning company.
    ///
    /// The ledger itself is still single-company: <c>AccountingPostingService</c> refuses any
    /// journal whose company is not the system owner. Asking that here turns what would be a hard
    /// failure in the middle of a loading into an ordinary skip, so a co-owner's share is simply
    /// left out of the ledger instead of being billed to the owner or blocking the operation.
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

        var accountIds = new[]
        {
            settings.InventoryAccountId,
            settings.InventoryInTransitAccountId,
            settings.InventoryLossAccountId
        };
        var validAccountCount = await db.Accounts.AsNoTracking().CountAsync(
            x => accountIds.Contains(x.Id) && x.CompanyId == companyId && x.IsActive,
            cancellationToken);
        if (validAccountCount != accountIds.Distinct().Count())
            return "ACCOUNTING_SETTINGS_INVALID_ACCOUNTS";

        return null;
    }

    /// <summary>
    /// What earlier receipts on this leg have already taken out of transit, read from the credits
    /// their journals actually posted.
    /// </summary>
    private async Task<decimal> SumReceiptedInTransitCostAsync(
        int companyId,
        int legId,
        bool multiCompany,
        CancellationToken cancellationToken)
    {
        var eventIds = await BuildReceiptEventIdsAsync(legId, companyId, multiCompany, cancellationToken);
        if (eventIds.Count == 0)
            return 0m;

        var settings = await db.AccountingSettings
            .AsNoTracking()
            .Where(x => x.CompanyId == companyId)
            .Select(x => x.InventoryInTransitAccountId)
            .SingleAsync(cancellationToken);

        return await db.JournalEntryLines
            .AsNoTracking()
            .Where(x => x.AccountId == settings
                && x.JournalEntry!.CompanyId == companyId
                && x.JournalEntry.SourceModule == SourceModule
                && !x.JournalEntry.IsReversal
                && eventIds.Contains(x.JournalEntry.SourceEventId!))
            .SumAsync(x => x.Credit, cancellationToken);
    }

    /// <summary>
    /// The tonnes those same journals accounted for, as this owner's share of them. Only receipts
    /// whose journal was posted count, so a skipped receipt leaves both the cost and the tonnes in
    /// transit and the share the next receipt draws stays consistent with them. Each earlier
    /// receipt is split between the owners by the same rule this one is, so the shares of one
    /// receipt can never overlap or leave a gap.
    /// </summary>
    private async Task<decimal> SumReceiptedQuantityAsync(
        int companyId,
        int legId,
        IReadOnlyList<decimal> sliceQuantities,
        int sliceIndex,
        CancellationToken cancellationToken)
    {
        var postedReceiptIds = await db.JournalEntries
            .AsNoTracking()
            .Where(x => x.CompanyId == companyId
                && x.SourceModule == SourceModule
                && x.SourceEntityType == ReceiptEntityType
                && !x.IsReversal
                && x.SourceEntityId != null)
            .Select(x => x.SourceEntityId!.Value)
            .ToListAsync(cancellationToken);
        if (postedReceiptIds.Count == 0)
            return 0m;

        var receiptQuantities = await db.InventoryTransportReceipts
            .AsNoTracking()
            .Where(x => x.InventoryTransportLegId == legId && postedReceiptIds.Contains(x.Id))
            .Select(x => x.ReceivedQuantityMt + x.ShortageQuantityMt)
            .ToListAsync(cancellationToken);

        if (sliceQuantities.Count <= 1)
            return receiptQuantities.Sum();

        return receiptQuantities.Sum(quantityMt =>
            InventoryTransportLegOwnershipResolver.ProportionalSplit(quantityMt, sliceQuantities)[sliceIndex]);
    }

    private async Task<List<string>> BuildReceiptEventIdsAsync(
        int legId,
        int companyId,
        bool multiCompany,
        CancellationToken cancellationToken)
    {
        var receiptIds = await db.InventoryTransportReceipts
            .AsNoTracking()
            .Where(x => x.InventoryTransportLegId == legId)
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);

        return receiptIds.Select(id => ReceiptEventId(id, companyId, multiCompany)).ToList();
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

    private InventoryTransferAccountingResult SkippedLeg(
        InventoryTransportLeg leg,
        int companyId,
        string reason)
    {
        LogLegOutcome(leg, companyId, 0m, PaymentPostingStatus.Skipped, reason);
        return new InventoryTransferAccountingResult(PaymentPostingStatus.Skipped, null, reason);
    }

    private InventoryTransferAccountingResult SkippedReceipt(
        InventoryTransportReceipt receipt,
        int companyId,
        string reason)
    {
        LogReceiptOutcome(receipt, null, companyId, 0m, 0m, 0m, PaymentPostingStatus.Skipped, reason);
        return new InventoryTransferAccountingResult(PaymentPostingStatus.Skipped, null, reason);
    }

    private void LogLegOutcome(
        InventoryTransportLeg leg,
        int companyId,
        decimal journalDebitTotal,
        PaymentPostingStatus status,
        string? reason)
    {
        // Legacy writes no ledger row for a transfer — it is a movement of goods, not of money —
        // so there is no legacy amount to reconcile against. The journal figure is the first
        // monetary statement of this transfer anywhere.
        logger.LogInformation(
            "Inventory transfer accounting pilot comparison: TransportLegId {TransportLegId}, CompanyId {CompanyId}, ProductId {ProductId}, SourceTerminalId {SourceTerminalId}, DestinationTerminalId {DestinationTerminalId}, QuantityMt {QuantityMt}, JournalDebitTotal {JournalDebitTotal}, PostingStatus {PostingStatus}, SkipOrFailureReason {SkipOrFailureReason}",
            leg.Id,
            companyId,
            leg.ProductId,
            leg.SourceTerminalId,
            leg.DestinationTerminalId,
            leg.QuantityMt,
            journalDebitTotal,
            status,
            reason);
    }

    private void LogReceiptOutcome(
        InventoryTransportReceipt receipt,
        InventoryTransportLeg? leg,
        int companyId,
        decimal receivedCostUsd,
        decimal shortageCostUsd,
        decimal journalDebitTotal,
        PaymentPostingStatus status,
        string? reason)
    {
        logger.LogInformation(
            "Inventory transfer receipt accounting pilot comparison: TransportReceiptId {TransportReceiptId}, TransportLegId {TransportLegId}, CompanyId {CompanyId}, DestinationTerminalId {DestinationTerminalId}, ReceivedQuantityMt {ReceivedQuantityMt}, ShortageQuantityMt {ShortageQuantityMt}, ReceivedCostUsd {ReceivedCostUsd}, ShortageCostUsd {ShortageCostUsd}, JournalDebitTotal {JournalDebitTotal}, PostingStatus {PostingStatus}, SkipOrFailureReason {SkipOrFailureReason}",
            receipt.Id,
            receipt.InventoryTransportLegId,
            companyId,
            receipt.DestinationTerminalId,
            receipt.ReceivedQuantityMt,
            receipt.ShortageQuantityMt,
            receivedCostUsd,
            shortageCostUsd,
            journalDebitTotal,
            status,
            reason);
    }

    private void LogLegFailure(InventoryTransportLeg leg, Exception exception)
        => logger.LogError(
            exception,
            "Inventory transfer accounting pilot posting failed for TransportLegId {TransportLegId} with FailureReason {FailureReason}",
            leg.Id,
            FailureReasonOf(exception));

    private void LogReceiptFailure(InventoryTransportReceipt receipt, Exception exception)
        => logger.LogError(
            exception,
            "Inventory transfer accounting pilot posting failed for TransportReceiptId {TransportReceiptId} with FailureReason {FailureReason}",
            receipt.Id,
            FailureReasonOf(exception));

    private static string FailureReasonOf(Exception exception)
        => exception is AccountingValidationException validation
            ? validation.Code
            : exception.GetType().Name;
}
