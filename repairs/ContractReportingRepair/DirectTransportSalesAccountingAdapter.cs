using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PTGOilSystem.Web.Configuration;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services.Accounting;

namespace PTG.ContractReportingRepair;

/// <summary>
/// Adds COGS for a direct-sale receipt sourced solely from priced purchase loadings.
/// Inventory sales and all other accounting operations retain the installed adapter.
/// TerminalId=0 denotes in-transit cost, never a terminal or a valuation pool.
/// </summary>
public sealed class DirectTransportSalesAccountingAdapter(
    SalesAccountingAdapter original,
    ApplicationDbContext db,
    IAccountingPostingService posting,
    IAccountingJournalNumberGenerator numbers,
    IOptions<AccountingOptions> options) : ISalesAccountingAdapter
{
    public const string JournalMarker = "Direct transport COGS";
    public const int InTransitCostSource = 0;
    private static SalesAccountingResult Skip(string reason) => new(PaymentPostingStatus.Skipped, null, reason);

    public Task<SalesAccountingResult> TryPostSaleAsync(SalesTransaction sale, CancellationToken ct = default)
        => original.TryPostSaleAsync(sale, ct);
    public Task<SalesAccountingResult> TryReverseSaleAsync(SalesTransaction sale, DateTime date, CancellationToken ct = default)
        => original.TryReverseSaleAsync(sale, date, ct);
    public Task<int> TrySettleDeliveredReceivableAsync(int id, CancellationToken ct = default)
        => original.TrySettleDeliveredReceivableAsync(id, ct);
    public Task<int> TryReleaseAdvanceApplicationsAsync(SalesTransaction sale, DateTime date, CancellationToken ct = default)
        => original.TryReleaseAdvanceApplicationsAsync(sale, date, ct);

    public async Task<SalesAccountingResult> TryPostCogsAsync(SalesTransaction sale, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sale);
        // The persisted direct-receipt link, not SaleStage or contract price, identifies this path.
        var receiptIds = await db.InventoryTransportReceipts.AsNoTracking()
            .Where(r => r.SalesTransactionId == sale.Id && !r.IsCancelled
                && r.ReceiptDestination == InventoryTransportReceiptDestination.DirectSale)
            .Select(r => r.Id).ToListAsync(ct);
        if (receiptIds.Count == 0 || await db.InventoryMovements.AsNoTracking()
            .AnyAsync(m => m.SalesTransactionId == sale.Id && m.Direction == MovementDirection.Out, ct))
            return await original.TryPostCogsAsync(sale, ct);
        if (!options.Value.Enabled) return Skip("ACCOUNTING_DISABLED");
        if (!options.Value.Pilots.Cogs) return Skip("PILOT_DISABLED");
        if (sale.IsCancelled) return Skip("SALE_CANCELLED");
        if (sale.QuantityMt <= 0m || sale.TotalUsd <= 0m) return Skip("INVALID_SALE_QUANTITY_OR_AMOUNT");

        await using var owned = db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(ct) : null;
        // Serialise replay and reversal of the same sale. The unique source-event constraint
        // remains the final guard; journal plus cost snapshot commit as one unit.
        await LockSaleAsync(sale.Id, ct);
        var currentSale = await db.SalesTransactions.AsNoTracking().SingleAsync(x => x.Id == sale.Id, ct);
        if (currentSale.IsCancelled) return Skip("SALE_CANCELLED");
        if (currentSale.QuantityMt != sale.QuantityMt || currentSale.TotalUsd != sale.TotalUsd
            || currentSale.SourcePurchaseContractId != sale.SourcePurchaseContractId)
            return Skip("SALE_CHANGED_DURING_COST_POSTING");
        var eventId = SalesAccountingAdapter.BuildCogsSourceEventId(sale.Id);
        var existing = await FindAsync(eventId, ct);
        if (existing is not null)
            return new(PaymentPostingStatus.Duplicate, existing, "DUPLICATE_SOURCE_EVENT");
        if (await db.SalesCostConsumptions.AnyAsync(c => c.SalesTransactionId == sale.Id, ct))
            return Skip("COST_SNAPSHOT_WITHOUT_JOURNAL");

        var receipts = await db.InventoryTransportReceipts.AsNoTracking()
            .Where(r => receiptIds.Contains(r.Id) && !r.IsCancelled).OrderBy(r => r.Id).ToListAsync(ct);
        if (Math.Abs(receipts.Sum(r => r.ReceivedQuantityMt) - sale.QuantityMt) > 0.0001m
            || receipts.Any(r => r.ReceivedQuantityMt <= 0m))
            return Skip("DIRECT_RECEIPT_QUANTITY_MISMATCH");
        var legIds = receipts.Select(r => r.InventoryTransportLegId).Distinct().ToList();
        var legs = await db.InventoryTransportLegs.AsNoTracking()
            .Where(l => legIds.Contains(l.Id)).ToListAsync(ct);
        if (legs.Count != legIds.Count || legs.Any(l => l.Status == InventoryTransportLegStatus.Cancelled
            || l.ProductId != sale.ProductId || l.SourceTerminalId.HasValue || l.OutboundInventoryMovementId.HasValue))
            return Skip("UNSUPPORTED_DIRECT_COST_SOURCE");
        var allocations = await db.InventoryTransportLegAllocations.AsNoTracking()
            .Where(a => legIds.Contains(a.InventoryTransportLegId)).OrderBy(a => a.Id).ToListAsync(ct);
        if (allocations.Count == 0 || allocations.Any(a => !a.SourceLoadingRegisterId.HasValue
            || a.SourceInventoryMovementId.HasValue || a.SourceTransportLegId.HasValue
            || a.SourceLoadingReceiptId.HasValue || a.QuantityMt <= 0m))
            return Skip("UNSUPPORTED_DIRECT_COST_SOURCE");
        var contractIds = allocations.Select(a => a.SourcePurchaseContractId).Distinct().ToList();
        if (contractIds.Count != 1 || sale.SourcePurchaseContractId != contractIds[0]
            || legs.Any(l => l.SourcePurchaseContractId != contractIds[0]))
            return Skip("AMBIGUOUS_DIRECT_COST_OWNERSHIP");
        var contractId = contractIds[0];
        var contract = await db.Contracts.AsNoTracking().SingleOrDefaultAsync(c => c.Id == contractId, ct);
        if (contract is null || sale.CompanyId.HasValue && sale.CompanyId != contract.CompanyId)
            return Skip("DIRECT_COST_COMPANY_MISMATCH");
        var companyId = contract.CompanyId;
        var settings = await db.AccountingSettings.AsNoTracking().SingleOrDefaultAsync(x => x.CompanyId == companyId, ct);
        if (settings is null) return Skip("ACCOUNTING_SETTINGS_MISSING");
        var loadingIds = allocations.Select(a => a.SourceLoadingRegisterId!.Value).Distinct().ToList();
        var loadings = await db.LoadingRegisters.AsNoTracking().Where(l => loadingIds.Contains(l.Id)).ToListAsync(ct);
        if (loadings.Count != loadingIds.Count || loadings.Any(l => l.IsCancelled || l.LoadedQuantityMt <= 0m
            || l.ContractId != contractId || l.ProductId != sale.ProductId))
            return Skip("INVALID_DIRECT_PURCHASE_SOURCE");
        var prices = new Dictionary<int, decimal>();
        foreach (var loading in loadings)
        {
            // Historical posted purchase, including the active repricing revision. Current
            // contract/replacement prices cannot turn an unknown cost into a verified one.
            var journals = await db.JournalEntries.AsNoTracking().Include(j => j.Lines)
                .Where(j => j.CompanyId == companyId && j.SourceModule == PurchaseAccountingAdapter.SourceModule
                    && j.SourceEntityType == PurchaseAccountingAdapter.PurchaseSourceEntityType
                    && j.SourceEntityId == loading.Id && j.Status == JournalEntryStatus.Posted && !j.IsReversal
                    && !db.JournalEntries.Any(r => r.ReversalOfJournalEntryId == j.Id && r.Status == JournalEntryStatus.Posted))
                .ToListAsync(ct);
            if (journals.Count != 1) return Skip("PURCHASE_COST_NOT_POSTED");
            var amount = journals[0].Lines.Where(l => l.AccountId == settings.InventoryInTransitAccountId)
                .Sum(l => l.Debit - l.Credit);
            if (amount <= 0m) return Skip("PURCHASE_COST_NOT_POSTED");
            prices[loading.Id] = amount / loading.LoadedQuantityMt;
        }
        decimal cost = 0m;
        var provenance = new List<string>();
        foreach (var leg in legs)
        {
            var shares = allocations.Where(a => a.InventoryTransportLegId == leg.Id).ToList();
            var total = shares.Sum(a => a.QuantityMt);
            if (Math.Abs(total - leg.QuantityMt) > 0.0001m) return Skip("DIRECT_SOURCE_QUANTITY_MISMATCH");
            var receivedTotal = await db.InventoryTransportReceipts.AsNoTracking()
                .Where(r => r.InventoryTransportLegId == leg.Id && !r.IsCancelled)
                .SumAsync(r => r.ReceivedQuantityMt + r.ShortageQuantityMt, ct);
            if (receivedTotal > total + 0.0001m) return Skip("DIRECT_RECEIPT_EXCEEDS_SOURCE");
            var quantity = receipts.Where(r => r.InventoryTransportLegId == leg.Id).Sum(r => r.ReceivedQuantityMt);
            decimal assigned = 0m;
            for (var i = 0; i < shares.Count; i++)
            {
                var shareQuantity = i == shares.Count - 1 ? quantity - assigned
                    : decimal.Round(quantity * shares[i].QuantityMt / total, 4, MidpointRounding.AwayFromZero);
                assigned += shareQuantity;
                var loadingId = shares[i].SourceLoadingRegisterId!.Value;
                cost += decimal.Round(shareQuantity * prices[loadingId], 4, MidpointRounding.AwayFromZero);
                provenance.Add($"L{loadingId}:{shareQuantity}MT");
            }
        }
        if (cost <= 0m) return Skip("DIRECT_COST_NOT_VALUED");
        var transitBalance = await db.JournalEntryLines.AsNoTracking()
            .Where(l => l.AccountId == settings.InventoryInTransitAccountId && l.ContractId == contractId
                && l.ProductId == sale.ProductId && l.JournalEntry!.Status == JournalEntryStatus.Posted)
            .SumAsync(l => l.Debit - l.Credit, ct);
        if (transitBalance < cost) return Skip("DIRECT_COST_EXCEEDS_TRANSIT_VALUE");
        var description = $"{JournalMarker}; receipts={string.Join(",", receiptIds)}; {string.Join(",", provenance)}";
        var journal = await posting.PostAsync(new AccountingPostRequest(
            companyId, numbers.ForCogs(companyId, sale.Id), sale.SaleDate.Date, sale.SaleDate.Date,
            sale.SaleDate.Date, SalesAccountingAdapter.SourceModule,
            [
                new(settings.CostOfGoodsSoldAccountId, cost, 0m, "USD", cost, 1m,
                    ContractId: contractId, ShipmentId: sale.ShipmentId, ProductId: sale.ProductId,
                    Description: $"Direct-sale cost for {sale.InvoiceNumber}"),
                new(settings.InventoryInTransitAccountId, 0m, cost, "USD", cost, 1m,
                    ContractId: contractId, ShipmentId: sale.ShipmentId, ProductId: sale.ProductId,
                    Description: "Purchased goods sold directly from transit")
            ], SourceEventId: eventId, SourceEntityType: SalesAccountingAdapter.SourceEntityType,
            SourceEntityId: sale.Id, Description: description.Length > 500 ? description[..500] : description), ct);
        db.SalesCostConsumptions.Add(new SalesCostConsumption {
            SalesTransactionId = sale.Id, CompanyId = companyId, ProductId = sale.ProductId,
            TerminalId = InTransitCostSource, QuantityMt = sale.QuantityMt, CostUsd = cost,
            Status = SalesCostConsumptionStatus.Active
        });
        await db.SaveChangesAsync(ct);
        if (owned is not null) await owned.CommitAsync(ct);
        return new(PaymentPostingStatus.Posted, journal, null);
    }

    public async Task<SalesAccountingResult> TryReverseCogsAsync(SalesTransaction sale, DateTime date, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sale);
        var originalJournal = await FindAsync(SalesAccountingAdapter.BuildCogsSourceEventId(sale.Id), ct);
        if (originalJournal is null || !(originalJournal.Description?.StartsWith(JournalMarker, StringComparison.Ordinal) ?? false))
            return await original.TryReverseCogsAsync(sale, date, ct);
        if (!options.Value.Enabled) return Skip("ACCOUNTING_DISABLED");
        if (!options.Value.Pilots.Cogs) return Skip("PILOT_DISABLED");
        await using var owned = db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(ct) : null;
        await LockSaleAsync(sale.Id, ct);
        var eventId = SalesAccountingAdapter.BuildCogsReversedSourceEventId(sale.Id);
        var existing = await FindAsync(eventId, ct);
        if (existing is not null) return new(PaymentPostingStatus.Duplicate, existing, "DUPLICATE_SOURCE_EVENT");
        var consumptions = await db.SalesCostConsumptions.Where(c => c.SalesTransactionId == sale.Id
            && c.Status == SalesCostConsumptionStatus.Active).ToListAsync(ct);
        if (consumptions.Count != 1 || consumptions[0].TerminalId != InTransitCostSource
            || consumptions[0].CostUsd != originalJournal.Lines.Sum(l => l.Debit))
            throw new InvalidOperationException("Direct COGS reversal requires its exact active in-transit cost snapshot.");
        var reversal = await posting.ReverseAsync(new AccountingReversalRequest(
            originalJournal.Id, numbers.ForCogsReversal(originalJournal.CompanyId, sale.Id), date.Date,
            SalesAccountingAdapter.SourceModule, eventId, Description: $"Reversal of {JournalMarker} for sale {sale.Id}"), ct);
        consumptions[0].Status = SalesCostConsumptionStatus.Reversed;
        consumptions[0].ReversedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        // There was no tank consumption: reverse the transit journal, never ReturnAsync.
        if (owned is not null) await owned.CommitAsync(ct);
        return new(PaymentPostingStatus.Posted, reversal, null);
    }

    private async Task LockSaleAsync(int saleId, CancellationToken ct) {
        // Same lock order as operational updates: sale row first, then event lock.
        await db.Database.ExecuteSqlInterpolatedAsync(
            $@"SELECT 1 FROM ""SalesTransactions"" WHERE ""Id"" = {saleId} FOR UPDATE", ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(71007, {saleId})", ct);
    }

    private Task<JournalEntry?> FindAsync(string eventId, CancellationToken ct) =>
        db.JournalEntries.AsNoTracking().Include(j => j.Lines).SingleOrDefaultAsync(
            j => j.SourceModule == SalesAccountingAdapter.SourceModule && j.SourceEventId == eventId, ct);
}
