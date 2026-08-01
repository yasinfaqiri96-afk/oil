using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PTGOilSystem.Web.Configuration;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services.Time;

namespace PTGOilSystem.Web.Services.Accounting;

public sealed record PurchaseAccountingResult(
    PaymentPostingStatus Status,
    JournalEntry? Journal,
    string? Reason);

public interface IPurchaseAccountingAdapter
{
    /// <summary>
    /// Posts (or reposts, after a reprice) the purchase a priced loading represents.
    /// </summary>
    Task<PurchaseAccountingResult> TryPostPurchaseAsync(
        LoadingRegister loading,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves the received goods from in-transit into inventory at the loading's unit cost.
    /// </summary>
    Task<PurchaseAccountingResult> TryPostInventoryReceiptAsync(
        LoadingReceipt receipt,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reverses every posted revision of a loading's purchase, for when the legacy loading is
    /// cancelled. Idempotent: revisions already reversed are left alone.
    /// </summary>
    Task<PurchaseAccountingResult> TryPostPurchaseReversalAsync(
        LoadingRegister loading,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reverses an inventory receipt, putting the goods back into in-transit.
    /// Idempotent: a second call returns the existing reversal.
    /// </summary>
    Task<PurchaseAccountingResult> TryPostInventoryReceiptReversalAsync(
        LoadingReceipt receipt,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Stage 6 dual-write pilot for purchase and inventory.
///
///   Purchase (a priced loading)   Dr Inventory In Transit   Cr Accounts Payable (party = supplier)
///   InventoryReceipt (goods in)   Dr Inventory              Cr Inventory In Transit
///
/// Amount: the same arithmetic the legacy <see cref="IPurchaseAggregationService"/> already uses
/// to derive the supplier claim — quantity x effective price, rounded to 4 places away from zero,
/// where the effective price is the loading's own LoadingPriceUsd and otherwise the contract's
/// final unit price. A loading with neither stays unposted (PURCHASE_PRICE_PENDING), exactly as
/// the aggregation treats it as pending.
///
/// Repricing: a posted purchase is never edited. When the effective price changes, the previous
/// revision is reversed and the next revision is posted, so the journal keeps the full history.
/// The revision number lives in the SourceEventId, which is what makes each posting idempotent.
///
/// Legacy comparison: only RUB-settled loadings with a locked rate produce a legacy "Loading"
/// ledger row; USD loadings have no legacy row at all and their claim exists only as an
/// aggregate. The pilot therefore posts from the aggregation arithmetic for both, and the
/// comparison log records the difference against the legacy row when one exists.
/// </summary>
public sealed class PurchaseAccountingAdapter(
    ApplicationDbContext db,
    IAccountingPostingService postingService,
    IAccountingJournalNumberGenerator journalNumberGenerator,
    IPricingService pricingService,
    IInventoryValuationService valuation,
    IOptions<AccountingOptions> options,
    ILogger<PurchaseAccountingAdapter> logger)
    : IPurchaseAccountingAdapter
{
    public const string SourceModule = "Purchase";
    public const string PurchaseSourceEntityType = nameof(LoadingRegister);
    public const string ReceiptSourceEntityType = nameof(LoadingReceipt);

    private readonly AccountingOptions _options = options.Value;

    public async Task<PurchaseAccountingResult> TryPostPurchaseAsync(
        LoadingRegister loading,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(loading);

        if (!_options.Enabled)
            return Skipped(loading.Id, "ACCOUNTING_DISABLED");
        if (!_options.Pilots.Purchase)
            return Skipped(loading.Id, "PILOT_DISABLED");

        var context = await ResolvePurchaseContextAsync(loading, cancellationToken);
        if (context.SkipReason is not null)
            return Skipped(loading.Id, context.SkipReason);

        var companyId = context.CompanyId!.Value;
        var amountUsd = context.AmountUsd!.Value;

        // The latest posted revision decides whether this is a first posting, an already-posted
        // amount, or a reprice that must reverse before it re-posts.
        var posted = await LoadPostedRevisionsAsync(companyId, loading.Id, cancellationToken);
        var current = posted.LastOrDefault();
        if (current is not null)
        {
            if (current.Lines.Sum(x => x.Debit) == amountUsd)
            {
                LogOutcome(loading.Id, "Purchase", companyId, amountUsd,
                    current.Lines.Sum(x => x.Debit), PaymentPostingStatus.Duplicate,
                    "DUPLICATE_SOURCE_EVENT");
                return new PurchaseAccountingResult(
                    PaymentPostingStatus.Duplicate, current, "DUPLICATE_SOURCE_EVENT");
            }

            var revisionToReverse = posted.Count - 1;
            await postingService.ReverseAsync(
                new AccountingReversalRequest(
                    current.Id,
                    journalNumberGenerator.ForPurchaseReversal(companyId, loading.Id, revisionToReverse),
                    loading.LoadingDate.Date,
                    SourceModule,
                    BuildReversedSourceEventId(loading.Id, revisionToReverse),
                    $"Reversal of purchase #{loading.Id} revision {revisionToReverse} before repricing"),
                cancellationToken);
        }

        var revision = posted.Count;
        var settings = await db.AccountingSettings
            .AsNoTracking()
            .SingleAsync(x => x.CompanyId == companyId, cancellationToken);

        var request = new AccountingPostRequest(
            companyId,
            journalNumberGenerator.ForPurchase(companyId, loading.Id, revision),
            loading.LoadingDate.Date,
            loading.LoadingDate.Date,
            loading.LoadingDate.Date,
            SourceModule,
            [
                new AccountingPostLine(
                    settings.InventoryInTransitAccountId,
                    Debit: amountUsd,
                    Credit: 0m,
                    SystemCurrency.BaseCurrencyCode,
                    amountUsd,
                    1m,
                    ContractId: loading.ContractId,
                    ProductId: loading.ProductId,
                    Description: "Purchased goods in transit"),
                new AccountingPostLine(
                    settings.AccountsPayableAccountId,
                    Debit: 0m,
                    Credit: amountUsd,
                    SystemCurrency.BaseCurrencyCode,
                    amountUsd,
                    1m,
                    AccountingPartyType.Supplier,
                    context.SupplierId,
                    ContractId: loading.ContractId,
                    ProductId: loading.ProductId,
                    Description: "Supplier payable for purchase")
            ],
            SourceEventId: BuildCreatedSourceEventId(loading.Id, revision),
            SourceEntityType: PurchaseSourceEntityType,
            SourceEntityId: loading.Id,
            Description: $"Purchase #{loading.Id} revision {revision} on {loading.LoadingDate:yyyy-MM-dd}");

        try
        {
            var journal = await postingService.PostAsync(request, cancellationToken);
            LogOutcome(loading.Id, "Purchase", companyId, amountUsd,
                journal.Lines.Sum(x => x.Debit), PaymentPostingStatus.Posted, null);
            return new PurchaseAccountingResult(PaymentPostingStatus.Posted, journal, null);
        }
        catch (Exception exception)
        {
            LogFailure(loading.Id, "Purchase", exception);
            throw;
        }
    }

    public async Task<PurchaseAccountingResult> TryPostInventoryReceiptAsync(
        LoadingReceipt receipt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(receipt);

        if (!_options.Enabled)
            return Skipped(receipt.Id, "ACCOUNTING_DISABLED");
        if (!_options.Pilots.InventoryReceipt)
            return Skipped(receipt.Id, "PILOT_DISABLED");
        if (receipt.ReceivedQuantityMt <= 0m)
            return Skipped(receipt.Id, "INVALID_RECEIPT_QUANTITY");

        var loading = await db.LoadingRegisters
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == receipt.LoadingRegisterId, cancellationToken);
        if (loading is null)
            return Skipped(receipt.Id, "LOADING_NOT_FOUND");

        var context = await ResolvePurchaseContextAsync(loading, cancellationToken);
        if (context.SkipReason is not null)
            return Skipped(receipt.Id, context.SkipReason);

        var companyId = context.CompanyId!.Value;

        // Goods can only leave in-transit once the purchase that put them there is posted.
        var purchaseIsPosted = await db.JournalEntries.AsNoTracking().AnyAsync(
            x => x.CompanyId == companyId
                && x.SourceModule == SourceModule
                && x.SourceEntityType == PurchaseSourceEntityType
                && x.SourceEntityId == loading.Id
                && x.Status == JournalEntryStatus.Posted
                && !x.IsReversal,
            cancellationToken);
        if (!purchaseIsPosted)
            return Skipped(receipt.Id, "PURCHASE_NOT_POSTED");

        var sourceEventId = BuildReceiptSourceEventId(receipt.Id);
        var existing = await FindJournalAsync(companyId, sourceEventId, cancellationToken);
        if (existing is not null)
        {
            LogOutcome(receipt.Id, "InventoryReceipt", companyId, existing.Lines.Sum(x => x.Debit),
                existing.Lines.Sum(x => x.Debit), PaymentPostingStatus.Duplicate, "DUPLICATE_SOURCE_EVENT");
            return new PurchaseAccountingResult(
                PaymentPostingStatus.Duplicate, existing, "DUPLICATE_SOURCE_EVENT");
        }

        // Received quantity is valued at the loading's unit cost. Any quantity difference
        // between loaded and received is a loss, which stage 8 recognises — not this stage.
        var amountUsd = decimal.Round(
            receipt.ReceivedQuantityMt * context.EffectivePrice!.Value,
            4,
            MidpointRounding.AwayFromZero);
        if (amountUsd <= 0m)
            return Skipped(receipt.Id, "INVALID_RECEIPT_AMOUNT");

        var settings = await db.AccountingSettings
            .AsNoTracking()
            .SingleAsync(x => x.CompanyId == companyId, cancellationToken);

        var request = new AccountingPostRequest(
            companyId,
            journalNumberGenerator.ForInventoryReceipt(companyId, receipt.Id),
            receipt.ReceiptDate.Date,
            receipt.ReceiptDate.Date,
            receipt.ReceiptDate.Date,
            SourceModule,
            [
                new AccountingPostLine(
                    settings.InventoryAccountId,
                    Debit: amountUsd,
                    Credit: 0m,
                    SystemCurrency.BaseCurrencyCode,
                    amountUsd,
                    1m,
                    ContractId: loading.ContractId,
                    TankId: receipt.StorageTankId,
                    ProductId: loading.ProductId,
                    Description: "Goods received into inventory"),
                new AccountingPostLine(
                    settings.InventoryInTransitAccountId,
                    Debit: 0m,
                    Credit: amountUsd,
                    SystemCurrency.BaseCurrencyCode,
                    amountUsd,
                    1m,
                    ContractId: loading.ContractId,
                    ProductId: loading.ProductId,
                    Description: "Goods left in transit")
            ],
            SourceEventId: sourceEventId,
            SourceEntityType: ReceiptSourceEntityType,
            SourceEntityId: receipt.Id,
            Description: $"Inventory receipt #{receipt.Id} on {receipt.ReceiptDate:yyyy-MM-dd}");

        try
        {
            var journal = await postingService.PostAsync(request, cancellationToken);

            // The journal records the money; the pool records the money and the tonnes, which is
            // what a later sale needs to know a unit cost. Both move together or neither does.
            await valuation.ApplyReceiptAsync(
                companyId,
                loading.ProductId,
                receipt.TerminalId,
                receipt.ReceivedQuantityMt,
                amountUsd,
                cancellationToken);

            LogOutcome(receipt.Id, "InventoryReceipt", companyId, amountUsd,
                journal.Lines.Sum(x => x.Debit), PaymentPostingStatus.Posted, null);
            return new PurchaseAccountingResult(PaymentPostingStatus.Posted, journal, null);
        }
        catch (Exception exception)
        {
            LogFailure(receipt.Id, "InventoryReceipt", exception);
            throw;
        }
    }

    public async Task<PurchaseAccountingResult> TryPostPurchaseReversalAsync(
        LoadingRegister loading,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(loading);

        if (!_options.Enabled)
            return Skipped(loading.Id, "ACCOUNTING_DISABLED");
        if (!_options.Pilots.Purchase)
            return Skipped(loading.Id, "PILOT_DISABLED");

        // The company must resolve the same way it did when the purchase was posted; the
        // price guard is irrelevant here because we only reverse what already exists.
        var companyId = await db.Contracts
            .AsNoTracking()
            .Where(x => x.Id == loading.ContractId)
            .Select(x => (int?)x.CompanyId)
            .SingleOrDefaultAsync(cancellationToken);
        if (companyId is null)
            return Skipped(loading.Id, "CONTRACT_NOT_FOUND");

        var posted = await LoadPostedRevisionsAsync(companyId.Value, loading.Id, cancellationToken);
        if (posted.Count == 0)
            return Skipped(loading.Id, "ORIGINAL_JOURNAL_NOT_POSTED");

        // A receipt that already moved these goods on must be reversed before the purchase is,
        // otherwise in-transit would be left holding a credit with nothing behind it.
        var hasLiveReceipt = await db.JournalEntries.AsNoTracking().AnyAsync(
            x => x.CompanyId == companyId.Value
                && x.SourceModule == SourceModule
                && x.SourceEntityType == ReceiptSourceEntityType
                && x.Status == JournalEntryStatus.Posted
                && !x.IsReversal
                && db.LoadingReceipts.Any(r => r.Id == x.SourceEntityId && r.LoadingRegisterId == loading.Id)
                && !db.JournalEntries.Any(r => r.ReversalOfJournalEntryId == x.Id
                    && r.Status == JournalEntryStatus.Posted),
            cancellationToken);
        if (hasLiveReceipt)
            return Skipped(loading.Id, "RECEIPT_STILL_POSTED");

        JournalEntry? lastReversal = null;
        var reversedCount = 0;
        for (var revision = 0; revision < posted.Count; revision++)
        {
            var journal = posted[revision];
            var reversedEventId = BuildReversedSourceEventId(loading.Id, revision);
            if (await FindJournalAsync(companyId.Value, reversedEventId, cancellationToken) is not null)
                continue;

            lastReversal = await postingService.ReverseAsync(
                new AccountingReversalRequest(
                    journal.Id,
                    journalNumberGenerator.ForPurchaseReversal(companyId.Value, loading.Id, revision),
                    AfghanistanBusinessClock.SystemToday,
                    SourceModule,
                    reversedEventId,
                    $"Reversal of purchase #{loading.Id} revision {revision}"),
                cancellationToken);
            reversedCount++;
        }

        if (reversedCount == 0)
        {
            LogOutcome(loading.Id, "PurchaseReversal", companyId.Value, 0m, 0m,
                PaymentPostingStatus.Duplicate, "DUPLICATE_SOURCE_EVENT");
            return new PurchaseAccountingResult(
                PaymentPostingStatus.Duplicate, null, "DUPLICATE_SOURCE_EVENT");
        }

        LogOutcome(loading.Id, "PurchaseReversal", companyId.Value, 0m,
            lastReversal!.Lines.Sum(x => x.Debit), PaymentPostingStatus.Posted, null);
        return new PurchaseAccountingResult(PaymentPostingStatus.Posted, lastReversal, null);
    }

    public async Task<PurchaseAccountingResult> TryPostInventoryReceiptReversalAsync(
        LoadingReceipt receipt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(receipt);

        if (!_options.Enabled)
            return Skipped(receipt.Id, "ACCOUNTING_DISABLED");
        if (!_options.Pilots.InventoryReceipt)
            return Skipped(receipt.Id, "PILOT_DISABLED");

        var companyId = await db.LoadingRegisters
            .AsNoTracking()
            .Where(x => x.Id == receipt.LoadingRegisterId)
            .Join(db.Contracts.AsNoTracking(), l => l.ContractId, c => c.Id, (_, c) => (int?)c.CompanyId)
            .SingleOrDefaultAsync(cancellationToken);
        if (companyId is null)
            return Skipped(receipt.Id, "LOADING_NOT_FOUND");

        var reversedEventId = BuildReceiptReversedSourceEventId(receipt.Id);
        var existingReversal = await FindJournalAsync(companyId.Value, reversedEventId, cancellationToken);
        if (existingReversal is not null)
        {
            LogOutcome(receipt.Id, "InventoryReceiptReversal", companyId.Value, 0m,
                existingReversal.Lines.Sum(x => x.Debit), PaymentPostingStatus.Duplicate,
                "DUPLICATE_SOURCE_EVENT");
            return new PurchaseAccountingResult(
                PaymentPostingStatus.Duplicate, existingReversal, "DUPLICATE_SOURCE_EVENT");
        }

        var original = await FindJournalAsync(
            companyId.Value,
            BuildReceiptSourceEventId(receipt.Id),
            cancellationToken);
        if (original is null)
            return Skipped(receipt.Id, "ORIGINAL_JOURNAL_NOT_POSTED");

        // The pool must give the goods back before the journal does. If they have already been
        // sold on, the receipt cannot be undone: reversing it would leave the pool holding
        // tonnes it never had and misprice every sale after it.
        var loadingProductId = await db.LoadingRegisters
            .AsNoTracking()
            .Where(x => x.Id == receipt.LoadingRegisterId)
            .Select(x => x.ProductId)
            .SingleAsync(cancellationToken);
        var poolReversal = await valuation.TryReverseReceiptAsync(
            companyId.Value,
            loadingProductId,
            receipt.TerminalId,
            receipt.ReceivedQuantityMt,
            original.Lines.Sum(x => x.Debit),
            cancellationToken);
        if (!poolReversal.Succeeded)
            return Skipped(receipt.Id, poolReversal.Reason!);

        var journal = await postingService.ReverseAsync(
            new AccountingReversalRequest(
                original.Id,
                journalNumberGenerator.ForInventoryReceiptReversal(companyId.Value, receipt.Id),
                AfghanistanBusinessClock.SystemToday,
                SourceModule,
                reversedEventId,
                $"Reversal of inventory receipt #{receipt.Id}"),
            cancellationToken);

        LogOutcome(receipt.Id, "InventoryReceiptReversal", companyId.Value, 0m,
            journal.Lines.Sum(x => x.Debit), PaymentPostingStatus.Posted, null);
        return new PurchaseAccountingResult(PaymentPostingStatus.Posted, journal, null);
    }

    public static string BuildCreatedSourceEventId(int loadingRegisterId, int revision)
        => $"Purchase:{loadingRegisterId}:Created:{revision}";

    public static string BuildReceiptReversedSourceEventId(int loadingReceiptId)
        => $"InventoryReceipt:{loadingReceiptId}:Reversed";

    public static string BuildReversedSourceEventId(int loadingRegisterId, int revision)
        => $"Purchase:{loadingRegisterId}:Reversed:{revision}";

    public static string BuildReceiptSourceEventId(int loadingReceiptId)
        => $"InventoryReceipt:{loadingReceiptId}:Created";

    private sealed record PurchaseContext(
        int? CompanyId,
        int SupplierId,
        decimal? EffectivePrice,
        decimal? AmountUsd,
        string? SkipReason);

    private async Task<PurchaseContext> ResolvePurchaseContextAsync(
        LoadingRegister loading,
        CancellationToken cancellationToken)
    {
        if (loading.LoadedQuantityMt <= 0m)
            return Fail("INVALID_LOADED_QUANTITY");

        var contract = await db.Contracts
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == loading.ContractId, cancellationToken);
        if (contract is null)
            return Fail("CONTRACT_NOT_FOUND");
        if (contract.ContractType != ContractType.Purchase)
            return Fail("CONTRACT_NOT_PURCHASE");
        if (!contract.SupplierId.HasValue)
            return Fail("SUPPLIER_MISSING");

        // Same fallback the aggregation uses: the loading's own price, else the contract's.
        decimal? effectivePrice = IPurchaseAggregationService.HasValidLoadingPrice(loading.LoadingPriceUsd)
            ? loading.LoadingPriceUsd
            : (await pricingService.CalculateContractPriceAsync(contract.Id, cancellationToken)).FinalUnitPrice;
        if (!IPurchaseAggregationService.HasValidLoadingPrice(effectivePrice))
            return Fail("PURCHASE_PRICE_PENDING");

        // همان قانون گردکردنی که مسیر قدیمی برای مبلغ بارگیری به کار می‌برد.
        var amountUsd = Helpers.LoadingRubSettlement.RoundAmountUsd(
            loading.LoadedQuantityMt * effectivePrice!.Value);
        if (amountUsd <= 0m)
            return Fail("INVALID_PURCHASE_AMOUNT");

        var companyId = contract.CompanyId;
        var settings = await db.AccountingSettings
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.CompanyId == companyId, cancellationToken);
        if (settings is null)
            return Fail("ACCOUNTING_SETTINGS_MISSING", companyId);
        if (!string.Equals(settings.FunctionalCurrencyCode?.Trim(), "USD", StringComparison.OrdinalIgnoreCase))
            return Fail("UNSUPPORTED_FUNCTIONAL_CURRENCY", companyId);

        var accountIds = new[]
        {
            settings.InventoryAccountId,
            settings.InventoryInTransitAccountId,
            settings.AccountsPayableAccountId
        };
        var validAccountCount = await db.Accounts.AsNoTracking().CountAsync(
            x => accountIds.Contains(x.Id) && x.CompanyId == companyId && x.IsActive,
            cancellationToken);
        if (validAccountCount != accountIds.Distinct().Count())
            return Fail("ACCOUNTING_SETTINGS_INVALID_ACCOUNTS", companyId);

        return new PurchaseContext(companyId, contract.SupplierId.Value, effectivePrice, amountUsd, null);

        static PurchaseContext Fail(string reason, int? companyId = null)
            => new(companyId, 0, null, null, reason);
    }

    private async Task<List<JournalEntry>> LoadPostedRevisionsAsync(
        int companyId,
        int loadingRegisterId,
        CancellationToken cancellationToken)
        => await db.JournalEntries
            .AsNoTracking()
            .Include(x => x.Lines)
            .Where(x => x.CompanyId == companyId
                && x.SourceModule == SourceModule
                && x.SourceEntityType == PurchaseSourceEntityType
                && x.SourceEntityId == loadingRegisterId
                && !x.IsReversal
                && x.Status == JournalEntryStatus.Posted)
            .OrderBy(x => x.Id)
            .ToListAsync(cancellationToken);

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

    private PurchaseAccountingResult Skipped(int entityId, string reason)
    {
        LogOutcome(entityId, "Skip", 0, 0m, 0m, PaymentPostingStatus.Skipped, reason);
        return new PurchaseAccountingResult(PaymentPostingStatus.Skipped, null, reason);
    }

    private void LogOutcome(
        int entityId,
        string eventKind,
        int companyId,
        decimal expectedAmountUsd,
        decimal journalDebitTotal,
        PaymentPostingStatus status,
        string? reason)
    {
        // USD loadings have no legacy ledger row at all; RUB-locked loadings have one whose
        // amount comes from the settled RUB figures rather than this arithmetic, so a difference
        // there is expected and must stay visible.
        logger.LogInformation(
            "Purchase accounting pilot comparison: EntityId {EntityId}, EventKind {EventKind}, CompanyId {CompanyId}, ExpectedAmountUsd {ExpectedAmountUsd}, JournalDebitTotal {JournalDebitTotal}, Difference {Difference}, PostingStatus {PostingStatus}, SkipOrFailureReason {SkipOrFailureReason}",
            entityId,
            eventKind,
            companyId,
            expectedAmountUsd,
            journalDebitTotal,
            journalDebitTotal - expectedAmountUsd,
            status,
            reason);
    }

    private void LogFailure(int entityId, string eventKind, Exception exception)
    {
        var failureReason = exception is AccountingValidationException validation
            ? validation.Code
            : exception.GetType().Name;
        logger.LogError(
            exception,
            "Purchase accounting pilot posting failed for EntityId {EntityId} ({EventKind}) with FailureReason {FailureReason}",
            entityId,
            eventKind,
            failureReason);
    }
}
