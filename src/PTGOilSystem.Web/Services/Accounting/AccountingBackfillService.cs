using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PTGOilSystem.Web.Configuration;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;

namespace PTGOilSystem.Web.Services.Accounting;

public sealed record AccountingBackfillStep(
    string Name,
    int Candidates,
    int Posted,
    int SkippedExisting,
    int SkippedOther,
    IReadOnlyList<string> Reasons);

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
            cancellationToken));

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
            .Where(x => !x.IsCancelled
                && x.ReceiptDestination == InventoryTransportReceiptDestination.ToInventory
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
        CancellationToken cancellationToken)
        where TResult : class
    {
        var posted = 0;
        var skippedExisting = 0;
        var skippedOther = 0;
        var reasons = new List<string>();

        if (dryRun)
        {
            // Read-only preview: what already carries its journal, and what does not. It cannot
            // say why a pending item would skip, because that answer depends on the postings the
            // earlier stages of this same run would have made.
            var eventIds = entities.Select(sourceEventIdOf).ToList();
            var existing = await db.JournalEntries.AsNoTracking()
                .Where(x => x.SourceModule == sourceModule
                    && x.SourceEventId != null
                    && eventIds.Contains(x.SourceEventId))
                .Select(x => x.SourceEventId!)
                .ToListAsync(cancellationToken);
            var existingSet = existing.ToHashSet(StringComparer.Ordinal);
            skippedExisting = entities.Count(x => existingSet.Contains(sourceEventIdOf(x)));
            posted = entities.Count - skippedExisting;
            return new AccountingBackfillStep(
                name, entities.Count, posted, skippedExisting, 0, reasons);
        }

        foreach (var entity in entities)
        {

            var relational = db.Database.IsRelational();
            await using var transaction = relational
                ? await db.Database.BeginTransactionAsync(cancellationToken)
                : null;
            try
            {
                var result = await post(entity, cancellationToken);
                var (status, reason) = Describe(result);

                switch (status)
                {
                    case PaymentPostingStatus.Posted:
                        posted++;
                        break;
                    case PaymentPostingStatus.Duplicate:
                        skippedExisting++;
                        break;
                    default:
                        skippedOther++;
                        if (reason is not null && !reasons.Contains(reason))
                            reasons.Add(reason);
                        break;
                }

                if (transaction is not null)
                    await transaction.CommitAsync(cancellationToken);
            }
            catch (AccountingValidationException validation)
            {
                // The ledger refused this document — a wrong company, a closed period, a broken
                // mapping. Nothing was written, and it is the document that is wrong, not the
                // backfill: record why and carry on with the rest.
                if (transaction is not null)
                    await transaction.RollbackAsync(cancellationToken);
                db.ChangeTracker.Clear();

                skippedOther++;
                if (!reasons.Contains(validation.Code))
                    reasons.Add(validation.Code);
                logger.LogWarning(
                    "Accounting backfill skipped {Step} #{EntityId}: {Code} - {Message}",
                    name, idOf(entity), validation.Code, validation.Message);
            }
            catch (Exception exception)
            {
                if (transaction is not null)
                    await transaction.RollbackAsync(cancellationToken);
                db.ChangeTracker.Clear();

                var message = $"{name} #{idOf(entity)}: {exception.GetType().Name}: {exception.Message}";
                errors.Add(message);
                logger.LogError(exception, "Accounting backfill failed on {Step} #{EntityId}", name, idOf(entity));
            }
        }

        return new AccountingBackfillStep(name, entities.Count, posted, skippedExisting, skippedOther, reasons);
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
