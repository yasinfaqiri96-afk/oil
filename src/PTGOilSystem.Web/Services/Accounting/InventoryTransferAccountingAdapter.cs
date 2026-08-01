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

    /// <summary>
    /// The leg has loaded: the goods have left the source terminal and are on the road. Takes
    /// their cost out of the source pool at its moving average and parks it in transit.
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

        var (companyId, skipReason) = await ResolveLegCompanyAndSkipReasonAsync(leg, cancellationToken);
        if (skipReason is not null)
            return SkippedLeg(leg, companyId, skipReason);

        var sourceEventId = BuildLegLoadedSourceEventId(leg.Id);
        var existing = await FindJournalAsync(companyId, sourceEventId, cancellationToken);
        if (existing is not null)
        {
            LogLegOutcome(leg, companyId, existing.Lines.Sum(x => x.Debit),
                PaymentPostingStatus.Duplicate, "DUPLICATE_SOURCE_EVENT");
            return new InventoryTransferAccountingResult(
                PaymentPostingStatus.Duplicate, existing, "DUPLICATE_SOURCE_EVENT");
        }

        var consumption = await valuation.TryConsumeAsync(
            companyId, leg.ProductId, leg.SourceTerminalId, leg.QuantityMt, cancellationToken);
        if (!consumption.Succeeded)
            return SkippedLeg(leg, companyId, consumption.Reason ?? "INVENTORY_NOT_VALUED");
        if (consumption.CostUsd <= 0m)
        {
            await valuation.ReturnAsync(
                companyId, leg.ProductId, leg.SourceTerminalId,
                leg.QuantityMt, consumption.CostUsd, cancellationToken);
            return SkippedLeg(leg, companyId, "INVENTORY_NOT_VALUED");
        }

        var settings = await db.AccountingSettings
            .AsNoTracking()
            .SingleAsync(x => x.CompanyId == companyId, cancellationToken);
        var costUsd = consumption.CostUsd;

        var request = new AccountingPostRequest(
            companyId,
            journalNumberGenerator.ForTransportLegLoad(companyId, leg.Id),
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
                    ContractId: leg.SourcePurchaseContractId,
                    ShipmentId: leg.ShipmentId,
                    ProductId: leg.ProductId,
                    Description: $"Goods in transit on leg #{leg.Id} ({leg.QuantityMt} MT)"),
                new AccountingPostLine(
                    settings.InventoryAccountId,
                    Debit: 0m,
                    Credit: costUsd,
                    SystemCurrency.BaseCurrencyCode,
                    costUsd,
                    1m,
                    ContractId: leg.SourcePurchaseContractId,
                    ShipmentId: leg.ShipmentId,
                    TankId: leg.SourceStorageTankId,
                    ProductId: leg.ProductId,
                    Description: "Goods dispatched from source terminal")
            ],
            SourceEventId: sourceEventId,
            SourceEntityType: LegEntityType,
            SourceEntityId: leg.Id,
            Description: $"Transport leg #{leg.Id} loaded on {leg.LoadedDate:yyyy-MM-dd}");

        try
        {
            var journal = await postingService.PostAsync(request, cancellationToken);
            LogLegOutcome(leg, companyId, journal.Lines.Sum(x => x.Debit), PaymentPostingStatus.Posted, null);
            return new InventoryTransferAccountingResult(PaymentPostingStatus.Posted, journal, null);
        }
        catch (Exception exception)
        {
            // The pool has already given the goods up; put them back so a failed posting leaves
            // the valuation exactly as it was.
            await valuation.ReturnAsync(
                companyId, leg.ProductId, leg.SourceTerminalId, leg.QuantityMt, costUsd, cancellationToken);
            LogLegFailure(leg, exception);
            throw;
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

        var companyId = await ResolveLegCompanyAsync(leg, cancellationToken);
        if (companyId is null)
            return SkippedLeg(leg, 0, "LEG_COMPANY_UNKNOWN");

        var reversedEventId = BuildLegLoadReversedSourceEventId(leg.Id);
        var alreadyReversed = await FindJournalAsync(companyId.Value, reversedEventId, cancellationToken);
        if (alreadyReversed is not null)
        {
            LogLegOutcome(leg, companyId.Value, alreadyReversed.Lines.Sum(x => x.Debit),
                PaymentPostingStatus.Duplicate, "DUPLICATE_SOURCE_EVENT");
            return new InventoryTransferAccountingResult(
                PaymentPostingStatus.Duplicate, alreadyReversed, "DUPLICATE_SOURCE_EVENT");
        }

        var original = await FindJournalAsync(
            companyId.Value, BuildLegLoadedSourceEventId(leg.Id), cancellationToken);
        if (original is null)
            return SkippedLeg(leg, companyId.Value, "ORIGINAL_JOURNAL_NOT_POSTED");

        // Goods that already left transit cannot be put back into the source pool: part of their
        // cost is sitting at the destination and taking it back here would count it twice.
        var consumedCostUsd = await SumReceiptedInTransitCostAsync(companyId.Value, leg.Id, cancellationToken);
        if (consumedCostUsd > 0m)
            return SkippedLeg(leg, companyId.Value, "LEG_ALREADY_RECEIPTED");

        var request = new AccountingReversalRequest(
            original.Id,
            journalNumberGenerator.ForTransportLegLoadReversal(companyId.Value, leg.Id),
            AfghanistanBusinessClock.SystemToday,
            SourceModule,
            reversedEventId,
            $"Reversal of transport leg #{leg.Id} load");

        try
        {
            var journal = await postingService.ReverseAsync(request, cancellationToken);
            await valuation.ReturnAsync(
                companyId.Value,
                leg.ProductId,
                leg.SourceTerminalId,
                leg.QuantityMt,
                original.Lines.Sum(x => x.Debit),
                cancellationToken);
            LogLegOutcome(leg, companyId.Value, journal.Lines.Sum(x => x.Debit), PaymentPostingStatus.Posted, null);
            return new InventoryTransferAccountingResult(PaymentPostingStatus.Posted, journal, null);
        }
        catch (Exception exception)
        {
            LogLegFailure(leg, exception);
            throw;
        }
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

        var (companyId, skipReason) = await ResolveLegCompanyAndSkipReasonAsync(leg, cancellationToken);
        if (skipReason is not null)
            return SkippedReceipt(receipt, companyId, skipReason);

        var sourceEventId = BuildReceiptSourceEventId(receipt.Id);
        var existing = await FindJournalAsync(companyId, sourceEventId, cancellationToken);
        if (existing is not null)
        {
            LogReceiptOutcome(receipt, leg, companyId, 0m, 0m,
                existing.Lines.Sum(x => x.Debit), PaymentPostingStatus.Duplicate, "DUPLICATE_SOURCE_EVENT");
            return new InventoryTransferAccountingResult(
                PaymentPostingStatus.Duplicate, existing, "DUPLICATE_SOURCE_EVENT");
        }

        var loadJournal = await FindJournalAsync(
            companyId, BuildLegLoadedSourceEventId(leg.Id), cancellationToken);
        if (loadJournal is null)
            return SkippedReceipt(receipt, companyId, "LEG_LOAD_NOT_POSTED");

        var loadReversed = await FindJournalAsync(
            companyId, BuildLegLoadReversedSourceEventId(leg.Id), cancellationToken);
        if (loadReversed is not null)
            return SkippedReceipt(receipt, companyId, "LEG_LOAD_REVERSED");

        // What is still on the road: the load's cost less whatever earlier receipts already took
        // out of transit, and the leg's tonnes less what those same receipts accounted for. Both
        // sides are read back from the journals actually posted rather than recomputed from a
        // schedule, so a receipt this pilot skipped simply never consumed anything and the
        // arithmetic still adds up.
        var inTransitCostUsd = loadJournal.Lines.Sum(x => x.Debit);
        var consumedCostUsd = await SumReceiptedInTransitCostAsync(companyId, leg.Id, cancellationToken);
        var consumedQuantityMt = await SumReceiptedQuantityAsync(companyId, leg.Id, cancellationToken);

        var remainingCostUsd = inTransitCostUsd - consumedCostUsd;
        var remainingQuantityMt = leg.QuantityMt - consumedQuantityMt;
        if (remainingCostUsd <= 0m || remainingQuantityMt <= 0m)
            return SkippedReceipt(receipt, companyId, "NOTHING_LEFT_IN_TRANSIT");

        // A truck may be allowed to hand over more than the leg still owes; the extra has no cost
        // in transit to draw on, and inventing one would misprice the destination.
        var receiptQuantityMt = receipt.ReceivedQuantityMt + receipt.ShortageQuantityMt;
        if (receiptQuantityMt > remainingQuantityMt + QuantityTolerance)
            return SkippedReceipt(receipt, companyId, "RECEIPT_EXCEEDS_IN_TRANSIT");

        // Taking the last of the goods takes the last of the cost, so no crumb is stranded in
        // transit by rounding — the same rule the valuation pool uses when it empties.
        var isFinalDraw = receiptQuantityMt >= remainingQuantityMt - QuantityTolerance;
        var drawUsd = isFinalDraw
            ? remainingCostUsd
            : decimal.Round(remainingCostUsd * receiptQuantityMt / remainingQuantityMt, 4, MidpointRounding.AwayFromZero);
        if (drawUsd > remainingCostUsd)
            drawUsd = remainingCostUsd;
        if (drawUsd <= 0m)
            return SkippedReceipt(receipt, companyId, "NOTHING_LEFT_IN_TRANSIT");

        var shortageCostUsd = receipt.ShortageQuantityMt <= 0m
            ? 0m
            : decimal.Round(drawUsd * receipt.ShortageQuantityMt / receiptQuantityMt, 4, MidpointRounding.AwayFromZero);
        if (shortageCostUsd > drawUsd)
            shortageCostUsd = drawUsd;
        var receivedCostUsd = drawUsd - shortageCostUsd;
        if (receivedCostUsd <= 0m)
            return SkippedReceipt(receipt, companyId, "NO_RECEIVED_COST");

        var settings = await db.AccountingSettings
            .AsNoTracking()
            .SingleAsync(x => x.CompanyId == companyId, cancellationToken);

        var lines = new List<AccountingPostLine>
        {
            new(
                settings.InventoryAccountId,
                Debit: receivedCostUsd,
                Credit: 0m,
                SystemCurrency.BaseCurrencyCode,
                receivedCostUsd,
                1m,
                ContractId: leg.SourcePurchaseContractId,
                ShipmentId: leg.ShipmentId,
                TankId: receipt.DestinationStorageTankId,
                ProductId: leg.ProductId,
                Description: $"Goods received at destination terminal ({receipt.ReceivedQuantityMt} MT)")
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
                ContractId: leg.SourcePurchaseContractId,
                ShipmentId: leg.ShipmentId,
                ProductId: leg.ProductId,
                Description: $"Cost of shortage on transport leg #{leg.Id} ({receipt.ShortageQuantityMt} MT)"));
        }

        lines.Add(new AccountingPostLine(
            settings.InventoryInTransitAccountId,
            Debit: 0m,
            Credit: drawUsd,
            SystemCurrency.BaseCurrencyCode,
            drawUsd,
            1m,
            ContractId: leg.SourcePurchaseContractId,
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

        // The journal and the destination pool move together: the pool is what a later sale reads
        // to price COGS, and the two must agree to the cent about what arrived.
        await valuation.ApplyReceiptAsync(
            companyId,
            leg.ProductId,
            receipt.DestinationTerminalId.Value,
            receipt.ReceivedQuantityMt,
            receivedCostUsd,
            cancellationToken);

        LogReceiptOutcome(receipt, leg, companyId, receivedCostUsd, shortageCostUsd,
            journalEntry.Lines.Sum(x => x.Debit), PaymentPostingStatus.Posted, null);
        return new InventoryTransferAccountingResult(PaymentPostingStatus.Posted, journalEntry, null);
    }

    public static string BuildLegLoadedSourceEventId(int transportLegId)
        => $"InventoryTransportLeg:{transportLegId}:Loaded";

    public static string BuildLegLoadReversedSourceEventId(int transportLegId)
        => $"InventoryTransportLeg:{transportLegId}:LoadReversed";

    public static string BuildReceiptSourceEventId(int transportReceiptId)
        => $"InventoryTransportReceipt:{transportReceiptId}:Received";

    /// <summary>
    /// What earlier receipts on this leg have already taken out of transit, read from the credits
    /// their journals actually posted.
    /// </summary>
    private async Task<decimal> SumReceiptedInTransitCostAsync(
        int companyId,
        int legId,
        CancellationToken cancellationToken)
    {
        var eventIds = await BuildReceiptEventIdsAsync(legId, cancellationToken);
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
    /// The tonnes those same journals accounted for. Only receipts whose journal was posted count,
    /// so a skipped receipt leaves both the cost and the tonnes in transit and the share the next
    /// receipt draws stays consistent with them.
    /// </summary>
    private async Task<decimal> SumReceiptedQuantityAsync(
        int companyId,
        int legId,
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

        return await db.InventoryTransportReceipts
            .AsNoTracking()
            .Where(x => x.InventoryTransportLegId == legId && postedReceiptIds.Contains(x.Id))
            .SumAsync(x => x.ReceivedQuantityMt + x.ShortageQuantityMt, cancellationToken);
    }

    private async Task<List<string>> BuildReceiptEventIdsAsync(int legId, CancellationToken cancellationToken)
    {
        var receiptIds = await db.InventoryTransportReceipts
            .AsNoTracking()
            .Where(x => x.InventoryTransportLegId == legId)
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);

        return receiptIds.Select(BuildReceiptSourceEventId).ToList();
    }

    private async Task<(int CompanyId, string? SkipReason)> ResolveLegCompanyAndSkipReasonAsync(
        InventoryTransportLeg leg,
        CancellationToken cancellationToken)
    {
        var companyId = await ResolveLegCompanyAsync(leg, cancellationToken);
        if (companyId is null)
            return (0, "LEG_COMPANY_UNKNOWN");

        var settings = await db.AccountingSettings
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.CompanyId == companyId.Value, cancellationToken);
        if (settings is null)
            return (companyId.Value, "ACCOUNTING_SETTINGS_MISSING");
        if (!string.Equals(settings.FunctionalCurrencyCode?.Trim(), "USD", StringComparison.OrdinalIgnoreCase))
            return (companyId.Value, "UNSUPPORTED_FUNCTIONAL_CURRENCY");

        var accountIds = new[]
        {
            settings.InventoryAccountId,
            settings.InventoryInTransitAccountId,
            settings.InventoryLossAccountId
        };
        var validAccountCount = await db.Accounts.AsNoTracking().CountAsync(
            x => accountIds.Contains(x.Id) && x.CompanyId == companyId.Value && x.IsActive,
            cancellationToken);
        if (validAccountCount != accountIds.Distinct().Count())
            return (companyId.Value, "ACCOUNTING_SETTINGS_INVALID_ACCOUNTS");

        return (companyId.Value, null);
    }

    /// <summary>
    /// A leg always names the purchase contract it draws from, and a contract always names its
    /// company, so the owner of these goods is provable outright — no fallback is needed.
    /// </summary>
    private async Task<int?> ResolveLegCompanyAsync(
        InventoryTransportLeg leg,
        CancellationToken cancellationToken)
        => await db.Contracts
            .AsNoTracking()
            .Where(x => x.Id == leg.SourcePurchaseContractId)
            .Select(x => (int?)x.CompanyId)
            .SingleOrDefaultAsync(cancellationToken);

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
