using PTGOilSystem.Web.Models.Entities;

namespace PTGOilSystem.Web.Services.Accounting;

public sealed record ReceiptAccountingOutcome(int ReceiptId, string SourceEventId, string Status, string? Reason);

/// <summary>Single and bulk receipt orchestration; the existing adapter owns all financial rules.</summary>
public sealed class LoadingReceiptAccountingCoordinator(IPurchaseAccountingAdapter? adapter)
{
    public async Task<IReadOnlyList<ReceiptAccountingOutcome>> PostAsync(
        IReadOnlyList<LoadingReceipt> receipts, CancellationToken cancellationToken = default)
    {
        var outcomes = new List<ReceiptAccountingOutcome>(receipts.Count);
        foreach (var receipt in receipts)
        {
            // Caller owns the transaction containing the operational graph, journal and pool.
            // Exceptions deliberately propagate so none of these effects survives alone.
            var result = adapter is null
                ? new PurchaseAccountingResult(PaymentPostingStatus.Skipped, null, "ACCOUNTING_ADAPTER_UNAVAILABLE")
                : await adapter.TryPostInventoryReceiptAsync(receipt, cancellationToken);
            outcomes.Add(new ReceiptAccountingOutcome(receipt.Id,
                PurchaseAccountingAdapter.BuildReceiptSourceEventId(receipt.Id), result.Status.ToString(), result.Reason));
        }
        return outcomes;
    }
}
