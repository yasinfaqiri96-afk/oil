using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PTGOilSystem.Web.Configuration;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services.Time;

namespace PTGOilSystem.Web.Services.Accounting;

public sealed record InventoryLossAccountingResult(
    PaymentPostingStatus Status,
    JournalEntry? Journal,
    string? Reason);

public interface IInventoryLossAccountingAdapter
{
    Task<InventoryLossAccountingResult> TryPostLossAsync(
        LossEvent lossEvent,
        CancellationToken cancellationToken = default);

    Task<InventoryLossAccountingResult> TryPostLossReversalAsync(
        LossEvent lossEvent,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Stage 8 dual-write pilot for inventory losses.
///
///   Dr Inventory Loss   Cr Inventory
///
/// This is the one Stage 8 mapping with no legacy counterpart at all: a LossEvent moves stock
/// and writes no ledger row, so account 5400 has never carried a figure. The pilot recognises
/// the loss the operational system already accepted, and the confirmed decision is to do so only
/// for the loss stages that genuinely reduce stock — TankNaturalLoss, ManualAdjustment and
/// TankFinalSettlement. Every other stage is provenance only: a receipt shortage, for one, is
/// already recognised through the shortage charge, and posting it here as well would count the
/// same barrels twice.
///
/// The quantity is the one the legacy InventoryMovement removed, never a re-derived figure, and
/// the value comes from <see cref="IInventoryValuationService"/> at the moving average — the
/// same authority that prices COGS, so a loss and a sale out of one pool can never disagree
/// about what the goods cost.
///
/// A loss whose pool cannot cover it is skipped with INVENTORY_NOT_VALUED rather than written
/// off at a guessed cost, exactly as Stage 7 skips COGS.
/// </summary>
public sealed class InventoryLossAccountingAdapter(
    ApplicationDbContext db,
    IAccountingPostingService postingService,
    IAccountingJournalNumberGenerator journalNumberGenerator,
    IInventoryValuationService valuation,
    IOptions<AccountingOptions> options,
    ILogger<InventoryLossAccountingAdapter> logger)
    : IInventoryLossAccountingAdapter
{
    public const string SourceModule = "InventoryLoss";
    public const string SourceEntityType = nameof(LossEvent);

    private readonly AccountingOptions _options = options.Value;

    public async Task<InventoryLossAccountingResult> TryPostLossAsync(
        LossEvent lossEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lossEvent);

        if (!_options.Enabled)
            return Skipped(lossEvent, 0, "ACCOUNTING_DISABLED");
        if (!_options.Pilots.InventoryLoss)
            return Skipped(lossEvent, 0, "PILOT_DISABLED");

        var (companyId, skipReason) = await ResolveCompanyAndSkipReasonAsync(lossEvent, cancellationToken);
        if (skipReason is not null)
            return Skipped(lossEvent, companyId, skipReason);

        var sourceEventId = BuildCreatedSourceEventId(lossEvent.Id);
        var existing = await FindJournalAsync(companyId, sourceEventId, cancellationToken);
        if (existing is not null)
        {
            LogOutcome(lossEvent, companyId, existing.Lines.Sum(x => x.Debit),
                PaymentPostingStatus.Duplicate, "DUPLICATE_SOURCE_EVENT");
            return new InventoryLossAccountingResult(
                PaymentPostingStatus.Duplicate, existing, "DUPLICATE_SOURCE_EVENT");
        }

        // What the operational system actually removed from the tank.
        var movement = await db.InventoryMovements
            .AsNoTracking()
            .Where(x => x.Id == lossEvent.InventoryMovementId!.Value)
            .Select(x => new { x.TerminalId, x.ProductId, x.QuantityMt, x.Direction })
            .SingleOrDefaultAsync(cancellationToken);
        if (movement is null)
            return Skipped(lossEvent, companyId, "INVENTORY_MOVEMENT_NOT_FOUND");
        if (movement.Direction != MovementDirection.Out)
            return Skipped(lossEvent, companyId, "INVENTORY_MOVEMENT_NOT_OUTBOUND");
        if (movement.QuantityMt <= 0m)
            return Skipped(lossEvent, companyId, "INVALID_MOVEMENT_QUANTITY");

        var consumption = await valuation.TryConsumeAsync(
            companyId,
            movement.ProductId,
            movement.TerminalId,
            movement.QuantityMt,
            cancellationToken);
        if (!consumption.Succeeded)
            return Skipped(lossEvent, companyId, consumption.Reason ?? "INVENTORY_NOT_VALUED");
        if (consumption.CostUsd <= 0m)
        {
            await valuation.ReturnAsync(
                companyId, movement.ProductId, movement.TerminalId,
                movement.QuantityMt, consumption.CostUsd, cancellationToken);
            return Skipped(lossEvent, companyId, "INVENTORY_NOT_VALUED");
        }

        var settings = await db.AccountingSettings
            .AsNoTracking()
            .SingleAsync(x => x.CompanyId == companyId, cancellationToken);
        var costUsd = consumption.CostUsd;

        var request = new AccountingPostRequest(
            companyId,
            journalNumberGenerator.ForInventoryLoss(companyId, lossEvent.Id),
            lossEvent.EventDate.Date,
            lossEvent.EventDate.Date,
            lossEvent.EventDate.Date,
            SourceModule,
            [
                new AccountingPostLine(
                    settings.InventoryLossAccountId,
                    Debit: costUsd,
                    Credit: 0m,
                    SystemCurrency.BaseCurrencyCode,
                    costUsd,
                    1m,
                    ContractId: lossEvent.ContractId,
                    ShipmentId: lossEvent.ShipmentId,
                    TankId: lossEvent.StorageTankId,
                    ProductId: lossEvent.ProductId,
                    Description: $"Inventory loss ({lossEvent.Stage}) of {movement.QuantityMt} MT"),
                new AccountingPostLine(
                    settings.InventoryAccountId,
                    Debit: 0m,
                    Credit: costUsd,
                    SystemCurrency.BaseCurrencyCode,
                    costUsd,
                    1m,
                    ContractId: lossEvent.ContractId,
                    ShipmentId: lossEvent.ShipmentId,
                    TankId: lossEvent.StorageTankId,
                    ProductId: lossEvent.ProductId,
                    Description: "Goods written off inventory")
            ],
            SourceEventId: sourceEventId,
            SourceEntityType: SourceEntityType,
            SourceEntityId: lossEvent.Id,
            Description: $"Inventory loss #{lossEvent.Id} ({lossEvent.Stage}) on {lossEvent.EventDate:yyyy-MM-dd}");

        try
        {
            var journal = await postingService.PostAsync(request, cancellationToken);
            LogOutcome(lossEvent, companyId, journal.Lines.Sum(x => x.Debit),
                PaymentPostingStatus.Posted, null);
            return new InventoryLossAccountingResult(PaymentPostingStatus.Posted, journal, null);
        }
        catch (Exception exception)
        {
            // The pool has already given the goods up; put them back so a failed posting leaves
            // the valuation exactly as it was.
            await valuation.ReturnAsync(
                companyId, movement.ProductId, movement.TerminalId,
                movement.QuantityMt, costUsd, cancellationToken);
            LogFailure(lossEvent, exception);
            throw;
        }
    }

    /// <summary>
    /// Undoes a cancelled loss. Legacy cancellation writes a compensating inbound movement, so
    /// the goods come back into stock; the value comes back into the pool to match, at exactly
    /// what the loss took out.
    /// </summary>
    public async Task<InventoryLossAccountingResult> TryPostLossReversalAsync(
        LossEvent lossEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lossEvent);

        if (!_options.Enabled)
            return Skipped(lossEvent, 0, "ACCOUNTING_DISABLED");
        if (!_options.Pilots.InventoryLoss)
            return Skipped(lossEvent, 0, "PILOT_DISABLED");

        var companyId = await ResolveCompanyAsync(lossEvent, cancellationToken);
        if (companyId is null)
            return Skipped(lossEvent, 0, "LOSS_COMPANY_UNKNOWN");

        var reversedEventId = BuildReversedSourceEventId(lossEvent.Id);
        var alreadyReversed = await FindJournalAsync(companyId.Value, reversedEventId, cancellationToken);
        if (alreadyReversed is not null)
        {
            LogOutcome(lossEvent, companyId.Value, alreadyReversed.Lines.Sum(x => x.Debit),
                PaymentPostingStatus.Duplicate, "DUPLICATE_SOURCE_EVENT");
            return new InventoryLossAccountingResult(
                PaymentPostingStatus.Duplicate, alreadyReversed, "DUPLICATE_SOURCE_EVENT");
        }

        var original = await FindJournalAsync(
            companyId.Value, BuildCreatedSourceEventId(lossEvent.Id), cancellationToken);
        if (original is null)
            return Skipped(lossEvent, companyId.Value, "ORIGINAL_JOURNAL_NOT_POSTED");

        var movement = await db.InventoryMovements
            .AsNoTracking()
            .Where(x => x.Id == lossEvent.InventoryMovementId!.Value)
            .Select(x => new { x.TerminalId, x.ProductId, x.QuantityMt })
            .SingleOrDefaultAsync(cancellationToken);
        if (movement is null)
            return Skipped(lossEvent, companyId.Value, "INVENTORY_MOVEMENT_NOT_FOUND");

        var request = new AccountingReversalRequest(
            original.Id,
            journalNumberGenerator.ForInventoryLossReversal(companyId.Value, lossEvent.Id),
            AfghanistanBusinessClock.SystemToday,
            SourceModule,
            reversedEventId,
            $"Reversal of inventory loss #{lossEvent.Id}");

        try
        {
            var journal = await postingService.ReverseAsync(request, cancellationToken);
            await valuation.ReturnAsync(
                companyId.Value,
                movement.ProductId,
                movement.TerminalId,
                movement.QuantityMt,
                original.Lines.Sum(x => x.Debit),
                cancellationToken);
            LogOutcome(lossEvent, companyId.Value, journal.Lines.Sum(x => x.Debit),
                PaymentPostingStatus.Posted, null);
            return new InventoryLossAccountingResult(PaymentPostingStatus.Posted, journal, null);
        }
        catch (Exception exception)
        {
            LogFailure(lossEvent, exception);
            throw;
        }
    }

    public static string BuildCreatedSourceEventId(int lossEventId)
        => $"InventoryLoss:{lossEventId}:Created";

    public static string BuildReversedSourceEventId(int lossEventId)
        => $"InventoryLoss:{lossEventId}:Reversed";

    /// <summary>
    /// The loss stages that genuinely reduce stock, which is the same gate the legacy workflow
    /// applies before it will write an inventory movement at all.
    /// </summary>
    public static bool IsInventoryReducingStage(LossEventStage stage)
        => Models.LossEvents.LossStagePolicy.AffectsInventory(stage);

    private async Task<(int CompanyId, string? SkipReason)> ResolveCompanyAndSkipReasonAsync(
        LossEvent lossEvent,
        CancellationToken cancellationToken)
    {
        if (lossEvent.IsCancelled)
            return (0, "LOSS_CANCELLED");
        if (!IsInventoryReducingStage(lossEvent.Stage))
            return (0, "LOSS_STAGE_NOT_RECOGNISED");
        if (!lossEvent.AffectsInventory || !lossEvent.InventoryMovementId.HasValue)
            return (0, "LOSS_DOES_NOT_AFFECT_INVENTORY");
        if (lossEvent.DifferenceQuantityMt <= 0m)
            return (0, "INVALID_LOSS_QUANTITY");

        var companyId = await ResolveCompanyAsync(lossEvent, cancellationToken);
        if (companyId is null)
            return (0, "LOSS_COMPANY_UNKNOWN");

        var settings = await db.AccountingSettings
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.CompanyId == companyId.Value, cancellationToken);
        if (settings is null)
            return (companyId.Value, "ACCOUNTING_SETTINGS_MISSING");
        if (!string.Equals(settings.FunctionalCurrencyCode?.Trim(), "USD", StringComparison.OrdinalIgnoreCase))
            return (companyId.Value, "UNSUPPORTED_FUNCTIONAL_CURRENCY");

        var accountIds = new[] { settings.InventoryLossAccountId, settings.InventoryAccountId };
        var validAccountCount = await db.Accounts.AsNoTracking().CountAsync(
            x => accountIds.Contains(x.Id) && x.CompanyId == companyId.Value && x.IsActive,
            cancellationToken);
        if (validAccountCount != accountIds.Distinct().Count())
            return (companyId.Value, "ACCOUNTING_SETTINGS_INVALID_ACCOUNTS");

        return (companyId.Value, null);
    }

    /// <summary>
    /// A loss event carries no company of its own. The contract owns one, and a tank belongs to
    /// a terminal whose stock a single company holds — but only the contract proves it outright,
    /// so a tank loss falls back to the company whose valuation pool holds that tank's product.
    /// Anything unprovable stays unresolved rather than guessed.
    /// </summary>
    private async Task<int?> ResolveCompanyAsync(LossEvent lossEvent, CancellationToken cancellationToken)
    {
        if (lossEvent.ContractId.HasValue)
        {
            var contractCompanyId = await db.Contracts
                .AsNoTracking()
                .Where(x => x.Id == lossEvent.ContractId.Value)
                .Select(x => (int?)x.CompanyId)
                .SingleOrDefaultAsync(cancellationToken);
            if (contractCompanyId.HasValue)
                return contractCompanyId;
        }

        if (lossEvent.TerminalId.HasValue)
        {
            // Exactly one company holding valued stock of this product at this terminal is proof
            // enough; two would not be, so that stays unresolved.
            var candidates = await db.InventoryAverageCosts
                .AsNoTracking()
                .Where(x => x.ProductId == lossEvent.ProductId
                    && x.TerminalId == lossEvent.TerminalId.Value
                    && x.QuantityMt > 0m)
                .Select(x => x.CompanyId)
                .Distinct()
                .Take(2)
                .ToListAsync(cancellationToken);
            if (candidates.Count == 1)
                return candidates[0];
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

    private InventoryLossAccountingResult Skipped(LossEvent lossEvent, int companyId, string reason)
    {
        LogOutcome(lossEvent, companyId, 0m, PaymentPostingStatus.Skipped, reason);
        return new InventoryLossAccountingResult(PaymentPostingStatus.Skipped, null, reason);
    }

    private void LogOutcome(
        LossEvent lossEvent,
        int companyId,
        decimal journalDebitTotal,
        PaymentPostingStatus status,
        string? reason)
    {
        // Legacy writes no ledger row for a loss, so there is no legacy amount to reconcile
        // against: the journal figure is the first monetary statement of this loss anywhere.
        logger.LogInformation(
            "Inventory loss accounting pilot comparison: LossEventId {LossEventId}, Stage {Stage}, CompanyId {CompanyId}, ProductId {ProductId}, TerminalId {TerminalId}, LossQuantityMt {LossQuantityMt}, JournalDebitTotal {JournalDebitTotal}, PostingStatus {PostingStatus}, SkipOrFailureReason {SkipOrFailureReason}",
            lossEvent.Id,
            lossEvent.Stage,
            companyId,
            lossEvent.ProductId,
            lossEvent.TerminalId,
            lossEvent.DifferenceQuantityMt,
            journalDebitTotal,
            status,
            reason);
    }

    private void LogFailure(LossEvent lossEvent, Exception exception)
    {
        var failureReason = exception is AccountingValidationException validation
            ? validation.Code
            : exception.GetType().Name;
        logger.LogError(
            exception,
            "Inventory loss accounting pilot posting failed for LossEventId {LossEventId} with FailureReason {FailureReason}",
            lossEvent.Id,
            failureReason);
    }
}
