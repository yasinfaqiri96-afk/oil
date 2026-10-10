using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PTGOilSystem.Web.Configuration;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;

namespace PTGOilSystem.Web.Services.Accounting;

public enum AccountingBackfillItemStatus
{
    Posted,
    AlreadyPosted,
    Skipped,
    Cancelled,
    NeedsReview,
    Error
}

public sealed record AccountingBackfillItem(
    int EntityId,
    AccountingBackfillItemStatus Status,
    string? Reason,
    bool MissingJournal = false);

public sealed record AccountingBackfillStep(
    string Name,
    int Candidates,
    int Posted,
    int SkippedExisting,
    int SkippedOther,
    IReadOnlyList<string> Reasons)
{
    public IReadOnlyList<AccountingBackfillItem> Items { get; init; } = [];
    public int MissingJournal => Items.Count(x => x.MissingJournal);
    public int NeedsReview => Items.Count(x => x.Status == AccountingBackfillItemStatus.NeedsReview);
    public int Cancelled => Items.Count(x => x.Status == AccountingBackfillItemStatus.Cancelled);
}

public sealed record AccountingBackfillReport(
    bool DryRun,
    bool ChartSeeded,
    IReadOnlyList<AccountingBackfillStep> Steps,
    IReadOnlyList<string> Errors)
{
    public bool Succeeded => Errors.Count == 0;
}

public interface IAccountingBackfillService
{
    Task<AccountingBackfillReport> RunAsync(bool dryRun, CancellationToken cancellationToken = default);
}

/// <summary>
/// Replays the accounting events that were never posted because the module was switched off
/// while the operational data was being entered.
///
/// It owns no accounting logic of its own. Every artifact is produced by the same production
/// adapter that would have produced it at create time, in the order the domain requires:
///
///   1. chart of accounts and AccountingSettings   (<see cref="IAccountingChartSeeder"/>)
///   2. purchases                                   Dr 1310 / Cr 2100
///   3. arrivals into inventory                     Dr 1300 / Cr 1310, and the valuation pool
///   4. sales revenue                               Dr 1200 / Cr 4100
///   5. cost of goods sold                          Dr 5100 / Cr 1300, and SalesCostConsumption
///   6. expense accrual                             Dr 5200 / Cr the configured payable
///   7. payments and receipts                       settle those payables and the receivable
///   8. partnership profit allocation               Dr 3200 / Cr 3300 per partner
///
/// Idempotency comes from the adapters themselves: each posting is keyed by a SourceEventId, so
/// a second run finds the journal already there and reports Duplicate without touching either
/// the ledger or the pool. Nothing operational is created, edited or deleted here.
///
/// Expenses are accrued, never capitalised. In this system only the purchase price is
/// inventoriable — freight, customs, warehouse and the rest stay ExpenseTransaction rows that the
/// P&amp;L already subtracts — so 5200 is where they belong and putting them into inventory would
/// double-count them against COGS.
///
/// Only the first four stages are required. The later ones run under their own pilot flags and
/// report PILOT_DISABLED as an ordinary skip when a flag is off, so a partially enabled
/// configuration still backfills what it is allowed to.
/// </summary>
public sealed class AccountingBackfillService(
    ApplicationDbContext db,
    IAccountingChartSeeder chartSeeder,
    IPurchaseAccountingAdapter purchaseAccounting,
    ISalesAccountingAdapter salesAccounting,
    IExpenseAccountingAdapter expenseAccounting,
    IPaymentAccountingAdapter paymentAccounting,
    IPartnershipProfitAllocationAdapter profitAllocation,
    IOptions<AccountingOptions> options,
    ILogger<AccountingBackfillService> logger) : IAccountingBackfillService
{
    private readonly AccountingOptions _options = options.Value;

    public async Task<AccountingBackfillReport> RunAsync(
        bool dryRun,
        CancellationToken cancellationToken = default)
    {
        var errors = new List<string>();
        var steps = new List<AccountingBackfillStep>();

        if (!_options.Enabled)
        {
            errors.Add("Accounting:Enabled is false. Nothing can be posted while the module is off.");
            return new AccountingBackfillReport(dryRun, false, steps, errors);
        }

        foreach (var (flag, name) in new[]
        {
            (_options.Pilots.Purchase, "Accounting:Pilots:Purchase"),
            (_options.Pilots.InventoryReceipt, "Accounting:Pilots:InventoryReceipt"),
            (_options.Pilots.Sale, "Accounting:Pilots:Sale"),
            (_options.Pilots.Cogs, "Accounting:Pilots:Cogs")
        })
        {
            if (!flag)
                errors.Add($"{name} is false. This backfill needs it enabled.");
        }
        if (errors.Count > 0)
            return new AccountingBackfillReport(dryRun, false, steps, errors);

        // The seeder is idempotent and also completes settings rows that were written before a
        // standard account existed, so it runs on every real backfill rather than only when a
        // company has no settings at all — otherwise an account added later never reaches the
        // companies that already had their chart.
        var chartSeeded = false;
        if (!dryRun)
        {
            await chartSeeder.SeedAsync(cancellationToken);
            chartSeeded = true;
        }

        // Ordered oldest first so every posting sees the state its predecessor left behind:
        // in-transit before the arrival that empties it, the pool before the sale that draws on it.
        var loadings = await db.LoadingRegisters.AsNoTracking()
            .OrderBy(x => x.LoadingDate).ThenBy(x => x.Id)
            .ToListAsync(cancellationToken);
        steps.Add(await RunStepAsync(
            "Purchase (LoadingRegister)",
            loadings,
            (x, ct) => purchaseAccounting.TryPostPurchaseAsync(x, ct),
            dryRun,
            errors,
            x => x.Id,
            x => PurchaseAccountingAdapter.BuildCreatedSourceEventId(x.Id, 0),
            PurchaseAccountingAdapter.SourceModule,
            cancellationToken,
            PurchaseAccountingAdapter.PurchaseSourceEntityType));

        var loadingReceipts = await db.LoadingReceipts.AsNoTracking()
            .OrderBy(x => x.ReceiptDate).ThenBy(x => x.Id)
            .ToListAsync(cancellationToken);
        steps.Add(await RunStepAsync(
            "InventoryReceipt (LoadingReceipt)",
            loadingReceipts,
            (x, ct) => purchaseAccounting.TryPostInventoryReceiptAsync(x, ct),
            dryRun,
            errors,
            x => x.Id,
            x => PurchaseAccountingAdapter.BuildReceiptSourceEventId(x.Id),
            PurchaseAccountingAdapter.SourceModule,
            cancellationToken));

        var transportReceipts = await db.InventoryTransportReceipts.AsNoTracking()
            .Where(x => x.ReceiptDestination == InventoryTransportReceiptDestination.ToInventory
                && x.ReceivedQuantityMt > 0m)
            .OrderBy(x => x.ReceiptDate).ThenBy(x => x.Id)
            .ToListAsync(cancellationToken);
        steps.Add(await RunStepAsync(
            "InventoryReceipt (InventoryTransportReceipt)",
            transportReceipts,
            (x, ct) => purchaseAccounting.TryPostTransportReceiptAsync(x, ct),
            dryRun,
            errors,
            x => x.Id,
            x => PurchaseAccountingAdapter.BuildTransportReceiptSourceEventId(x.Id),
            PurchaseAccountingAdapter.SourceModule,
            cancellationToken));

        var sales = await db.SalesTransactions.AsNoTracking()
            .Where(x => !x.IsCancelled)
            .OrderBy(x => x.SaleDate).ThenBy(x => x.Id)
            .ToListAsync(cancellationToken);
        steps.Add(await RunStepAsync(
            "Sale revenue",
            sales,
            (x, ct) => salesAccounting.TryPostSaleAsync(x, ct),
            dryRun,
            errors,
            x => x.Id,
            x => SalesAccountingAdapter.BuildCreatedSourceEventId(x.Id),
            SalesAccountingAdapter.SourceModule,
            cancellationToken));
        steps.Add(await RunStepAsync(
            "Cost of goods sold",
            sales,
            (x, ct) => salesAccounting.TryPostCogsAsync(x, ct),
            dryRun,
            errors,
            x => x.Id,
            x => SalesAccountingAdapter.BuildCogsSourceEventId(x.Id),
            SalesAccountingAdapter.SourceModule,
            cancellationToken));

        // The expense has to accrue its liability before the payment that settles it can debit
        // that liability, otherwise the payable would go negative in between.
        var expenses = await db.ExpenseTransactions.AsNoTracking()
            .Where(x => !x.IsCancelled)
            .OrderBy(x => x.ExpenseDate).ThenBy(x => x.Id)
            .ToListAsync(cancellationToken);
        steps.Add(await RunStepAsync(
            "Expense accrual",
            expenses,
            (x, ct) => expenseAccounting.TryPostExpenseAsync(x, ct),
            dryRun,
            errors,
            x => x.Id,
            x => ExpenseAccountingAdapter.BuildCreatedSourceEventId(x.Id),
            ExpenseAccountingAdapter.SourceModule,
            cancellationToken));

        var payments = await db.PaymentTransactions.AsNoTracking()
            .OrderBy(x => x.PaymentDate).ThenBy(x => x.Id)
            .ToListAsync(cancellationToken);
        steps.Add(await RunStepAsync(
            "Payments and receipts",
            payments,
            (x, ct) => paymentAccounting.TryPostPaymentAsync(x, ct),
            dryRun,
            errors,
            x => x.Id,
            x => PaymentAccountingAdapter.BuildCreatedSourceEventId(x.Id, 0),
            PaymentAccountingAdapter.SourceModule,
            cancellationToken));

        // Last: the partners' share can only be right once every revenue, cost and expense that
        // makes up the book profit is already in the ledger.
        var partnershipContracts = await db.Contracts.AsNoTracking()
            .Where(x => x.OwnershipType == ContractOwnershipType.Partnership)
            .OrderBy(x => x.Id)
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);
        steps.Add(await RunStepAsync(
            "Partnership profit allocation",
            partnershipContracts,
            (x, ct) => profitAllocation.TryPostAllocationAsync(x, ct),
            dryRun,
            errors,
            x => x,
            PartnershipProfitAllocationAdapter.BuildSourceEventId,
            PartnershipProfitAllocationAdapter.SourceModule,
            cancellationToken));

        return new AccountingBackfillReport(dryRun, chartSeeded, steps, errors);
    }

    /// <summary>
    /// One dependency stage. Each item is posted inside its own transaction, so a failure leaves
    /// that item unposted and every earlier item intact rather than half-written — and because
    /// the adapters are keyed by SourceEventId, the next run picks up exactly where this one
    /// stopped.
    /// </summary>
    private async Task<AccountingBackfillStep> RunStepAsync<TEntity, TResult>(
        string name,
        IReadOnlyList<TEntity> entities,
        Func<TEntity, CancellationToken, Task<TResult>> post,
        bool dryRun,
        List<string> errors,
        Func<TEntity, int> idOf,
        Func<TEntity, string> sourceEventIdOf,
        string sourceModule,
        CancellationToken cancellationToken,
        string? revisionedSourceEntityType = null)
        where TResult : class
    {
        var posted = 0;
        var skippedExisting = 0;
        var skippedOther = 0;
        var reasons = new List<string>();
        var items = new List<AccountingBackfillItem>(entities.Count);
        var eventIds = entities.Select(sourceEventIdOf).ToList();
        var entityIds = entities.Select(idOf).ToList();
        var journals = await db.JournalEntries.AsNoTracking()
            .Where(x => x.SourceModule == sourceModule && !x.IsReversal
                && ((x.SourceEventId != null && eventIds.Contains(x.SourceEventId))
                    || (revisionedSourceEntityType != null
                        && x.SourceEntityType == revisionedSourceEntityType
                        && x.SourceEntityId.HasValue && entityIds.Contains(x.SourceEntityId.Value))))
            .Select(x => new { x.Id, x.SourceEventId, x.SourceEntityId, x.Status,
                Reversed = db.JournalEntries.Any(r => r.ReversalOfJournalEntryId == x.Id
                    && r.Status == JournalEntryStatus.Posted) })
            .ToListAsync(cancellationToken);
        var byEvent = journals.Where(x => x.SourceEventId != null)
            .ToLookup(x => x.SourceEventId!, StringComparer.Ordinal);
        var byEntity = journals.Where(x => x.SourceEntityId.HasValue)
            .ToLookup(x => x.SourceEntityId!.Value);

        foreach (var entity in entities)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entityId = idOf(entity);
            var cancellationReason = CancellationReason(entity);
            if (cancellationReason is not null)
            {
                AddSkip(entityId, AccountingBackfillItemStatus.Cancelled, cancellationReason);
                continue;
            }

            var existing = revisionedSourceEntityType is null
                ? byEvent[sourceEventIdOf(entity)].OrderBy(x => x.Id).LastOrDefault()
                : byEntity[entityId].OrderBy(x => x.Id).LastOrDefault();
            if (existing is not null)
            {
                if (existing.Reversed)
                    AddSkip(entityId, AccountingBackfillItemStatus.Skipped, "SOURCE_JOURNAL_REVERSED");
                else if (existing.Status == JournalEntryStatus.Posted)
                {
                    // Backfill fills gaps. Repricing a posted historical purchase is a separate,
                    // explicitly requested revision operation, never a side effect of backfill.
                    skippedExisting++;
                    items.Add(new(entityId, AccountingBackfillItemStatus.AlreadyPosted, "ALREADY_POSTED"));
                }
                else
                    AddSkip(entityId, AccountingBackfillItemStatus.NeedsReview, "SOURCE_JOURNAL_NOT_POSTED");
                continue;
            }

            var knownSkip = KnownSkipReason(entity);
            if (knownSkip is not null)
            {
                AddSkip(entityId, AccountingBackfillItemStatus.Skipped, knownSkip, missingJournal: true);
                continue;
            }
            if (dryRun)
            {
                // Missing is not eligible: prices, mappings, periods and dependency postings
                // are checked by the real adapter. No adapter or seeder runs during preview.
                AddSkip(entityId, AccountingBackfillItemStatus.NeedsReview,
                    "MISSING_JOURNAL_REQUIRES_ADAPTER_VALIDATION", missingJournal: true);
                continue;
            }

            await using var transaction = db.Database.IsRelational()
                ? await db.Database.BeginTransactionAsync(cancellationToken)
                : null;
            try
            {
                // Re-read mutable source rows under the transaction's PostgreSQL row lock.
                // A cancellation committed after candidate discovery must not be resurrected.
                var current = await RefreshSourceAsync(entity, cancellationToken);
                cancellationReason = CancellationReason(current);
                if (cancellationReason is not null)
                {
                    if (transaction is not null)
                        await transaction.CommitAsync(cancellationToken);
                    AddSkip(entityId, AccountingBackfillItemStatus.Cancelled, cancellationReason, true);
                    continue;
                }
                var result = await post(current, cancellationToken);
                var (status, reason) = Describe(result);
                if (transaction is not null)
                    await transaction.CommitAsync(cancellationToken);
                switch (status)
                {
                    case PaymentPostingStatus.Posted:
                        posted++;
                        items.Add(new(entityId, AccountingBackfillItemStatus.Posted, null));
                        break;
                    case PaymentPostingStatus.Duplicate:
                        skippedExisting++;
                        items.Add(new(entityId, AccountingBackfillItemStatus.AlreadyPosted, reason));
                        break;
                    default:
                        AddSkip(entityId, AccountingBackfillItemStatus.Skipped, reason ?? "ADAPTER_SKIPPED", true);
                        break;
                }
            }
            catch (AccountingValidationException validation)
            {
                if (transaction is not null)
                    await transaction.RollbackAsync(cancellationToken);
                db.ChangeTracker.Clear();
                AddSkip(entityId, AccountingBackfillItemStatus.Skipped, validation.Code, true);
                logger.LogWarning(
                    "Accounting backfill skipped {Step} #{EntityId}: {Code} - {Message}",
                    name, entityId, validation.Code, validation.Message);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                if (transaction is not null)
                    await transaction.RollbackAsync(CancellationToken.None);
                db.ChangeTracker.Clear();
                throw;
            }
            catch (Exception exception)
            {
                if (transaction is not null)
                    await transaction.RollbackAsync(cancellationToken);
                db.ChangeTracker.Clear();
                var message = $"{name} #{entityId}: {exception.GetType().Name}: {exception.Message}";
                errors.Add(message);
                items.Add(new(entityId, AccountingBackfillItemStatus.Error, message, true));
                logger.LogError(exception, "Accounting backfill failed on {Step} #{EntityId}", name, entityId);
            }
        }
        return new AccountingBackfillStep(name, entities.Count, posted, skippedExisting, skippedOther, reasons)
        {
            Items = items
        };

        void AddSkip(int id, AccountingBackfillItemStatus status, string reason, bool missingJournal = false)
        {
            skippedOther++;
            if (!reasons.Contains(reason))
                reasons.Add(reason);
            items.Add(new(id, status, reason, missingJournal));
        }
    }

    private static string? CancellationReason<TEntity>(TEntity entity) => entity switch
    {
        LoadingRegister { IsCancelled: true } => "LOADING_CANCELLED",
        LoadingReceipt { IsCancelled: true } => "RECEIPT_CANCELLED",
        InventoryTransportReceipt { IsCancelled: true } => "RECEIPT_CANCELLED",
        _ => null
    };

    private static string? KnownSkipReason<TEntity>(TEntity entity) => entity switch
    {
        LoadingReceipt { ReceiptDestination: not LoadingReceiptDestination.ToInventory }
            => "RECEIPT_DESTINATION_NOT_INVENTORY",
        LoadingReceipt { ReceivedQuantityMt: <= 0m } => "INVALID_RECEIPT_QUANTITY",
        _ => null
    };

    private async Task<TEntity> RefreshSourceAsync<TEntity>(TEntity entity, CancellationToken cancellationToken)
    {
        if (!db.Database.IsNpgsql())
            return entity;
        object current = entity switch
        {
            LoadingRegister loading => await db.LoadingRegisters
                .FromSqlInterpolated($"SELECT * FROM \"LoadingRegisters\" WHERE \"Id\" = {loading.Id} FOR UPDATE")
                .AsNoTracking().SingleAsync(cancellationToken),
            LoadingReceipt receipt => await db.LoadingReceipts
                .FromSqlInterpolated($"SELECT *, xmin FROM \"LoadingReceipts\" WHERE \"Id\" = {receipt.Id} FOR UPDATE")
                .AsNoTracking().SingleAsync(cancellationToken),
            InventoryTransportReceipt receipt => await db.InventoryTransportReceipts
                .FromSqlInterpolated($"SELECT *, xmin FROM \"InventoryTransportReceipts\" WHERE \"Id\" = {receipt.Id} FOR UPDATE")
                .AsNoTracking().SingleAsync(cancellationToken),
            _ => entity!
        };
        return (TEntity)current;
    }

    private static (PaymentPostingStatus Status, string? Reason) Describe(object result) => result switch
    {
        PurchaseAccountingResult purchase => (purchase.Status, purchase.Reason),
        SalesAccountingResult sale => (sale.Status, sale.Reason),
        ExpenseAccountingResult expense => (expense.Status, expense.Reason),
        PaymentAccountingResult payment => (payment.Status, payment.Reason),
        ProfitAllocationResult allocation => (allocation.Status, allocation.Reason),
        _ => (PaymentPostingStatus.Skipped, "UNKNOWN_RESULT_TYPE")
    };
}
