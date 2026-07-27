using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services.Accounting;

namespace PTGOilSystem.Web.Services;

public sealed record PaymentCorrectionResult(int ReversedAllocationCount, bool JournalResynced);

/// <summary>
/// Central "reverse and re-post" for correcting a posted payment / receipt, shared by every cash
/// path (bank, cash box, sarraf-cash). The caller updates the <see cref="PaymentTransaction"/> and
/// its legacy ledger row in place; this service undoes the dependent financial effects of the
/// pre-edit state and re-posts them from the new state, all inside the caller's transaction so the
/// whole correction commits or rolls back as one unit.
///
/// Two dependent effects are handled:
///   1. Active supplier-payment allocations, which are reversed (never edited) through the tested
///      <see cref="ISupplierPaymentAllocationService.ReverseAsync"/> so the contract balance never
///      keeps an allocation made against the old amount.
///   2. The double-entry journal, which is reversed and re-posted through
///      <see cref="IPaymentAccountingAdapter"/>. When the accounting core (or the payment's pilot)
///      is disabled the adapter simply skips, so this is a no-op there and the legacy ledger stays
///      the single source of truth exactly as before.
///
/// The service opens no transaction of its own: the allocation service and the posting service
/// both compose inside an already-open transaction, so correctness relies on the caller having
/// begun one.
/// </summary>
public interface IPaymentCorrectionService
{
    Task<PaymentCorrectionResult> ApplyAsync(
        PaymentTransaction payment,
        bool reverseAllocations,
        bool resyncJournal,
        string reason,
        string? userName,
        CancellationToken ct = default);
}

public sealed class PaymentCorrectionService : IPaymentCorrectionService
{
    private readonly ApplicationDbContext _db;
    private readonly ISupplierPaymentAllocationService _allocations;
    private readonly IPaymentAccountingAdapter? _paymentAccounting;

    public PaymentCorrectionService(
        ApplicationDbContext db,
        ISupplierPaymentAllocationService allocations,
        IPaymentAccountingAdapter? paymentAccounting = null)
    {
        _db = db;
        _allocations = allocations;
        _paymentAccounting = paymentAccounting;
    }

    public async Task<PaymentCorrectionResult> ApplyAsync(
        PaymentTransaction payment,
        bool reverseAllocations,
        bool resyncJournal,
        string reason,
        string? userName,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(payment);

        var reversedAllocationCount = 0;
        if (reverseAllocations)
        {
            // Snapshot the ids first: ReverseAsync mutates status, so re-querying mid-loop would
            // shift the set. Each reversal posts its own balanced counter-entries.
            var activeAllocationIds = await _db.SupplierPaymentAllocations
                .AsNoTracking()
                .Where(a => a.PaymentTransactionId == payment.Id
                    && a.Status == SupplierPaymentAllocationStatus.Active)
                .Select(a => a.Id)
                .ToListAsync(ct);

            foreach (var allocationId in activeAllocationIds)
            {
                await _allocations.ReverseAsync(
                    new SupplierPaymentAllocationReverseRequest(allocationId, reason, userName),
                    ct);
                reversedAllocationCount++;
            }
        }

        var journalResynced = false;
        if (resyncJournal && _paymentAccounting is not null)
        {
            // Reverse every standing revision of the old journal, then post a fresh revision from
            // the updated payment. Both are idempotent: a retry finds the reversal/new revision
            // already present and reports Duplicate instead of double-posting.
            await _paymentAccounting.TryPostPaymentReversalAsync(payment, ct);
            await _paymentAccounting.TryPostPaymentAsync(payment, ct);
            journalResynced = true;
        }

        return new PaymentCorrectionResult(reversedAllocationCount, journalResynced);
    }
}
