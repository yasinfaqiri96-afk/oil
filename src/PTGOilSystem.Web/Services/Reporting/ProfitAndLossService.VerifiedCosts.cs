using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services.Accounting;

namespace PTGOilSystem.Web.Services.Reporting;

public sealed partial class ProfitAndLossService
{
    // A direct loading sale never visited a tank, so it has no inventory-pool consumption.
    // Its exact posted COGS event is still an immutable cost snapshot. Prefer the existing
    // active pool snapshot and fall back only for missing sales; never add both books.
    private async Task<Dictionary<int, decimal>> LoadVerifiedSaleCostsAsync(
        IReadOnlyCollection<int> salesTransactionIds, CancellationToken ct)
    {
        var ids = salesTransactionIds.Distinct().ToArray();
        if (ids.Length == 0) return [];
        var costs = await _db.SalesCostConsumptions.AsNoTracking()
            .Where(c => c.Status == SalesCostConsumptionStatus.Active && ids.Contains(c.SalesTransactionId))
            .GroupBy(c => c.SalesTransactionId)
            .Select(g => new { SaleId = g.Key, CostUsd = g.Sum(c => c.CostUsd) })
            .ToDictionaryAsync(c => c.SaleId, c => c.CostUsd, ct);
        var missing = ids.Where(id => !costs.ContainsKey(id)).ToArray();
        if (missing.Length == 0) return costs;
        var events = missing.Select(SalesAccountingAdapter.BuildCogsSourceEventId).ToArray();
        var journals = await _db.JournalEntries.AsNoTracking()
            .Where(j => j.Status == JournalEntryStatus.Posted && !j.IsReversal
                && j.SourceModule == SalesAccountingAdapter.SourceModule
                && j.SourceEntityType == nameof(SalesTransaction)
                && j.SourceEntityId.HasValue && missing.Contains(j.SourceEntityId.Value)
                && events.Contains(j.SourceEventId!)
                && !j.Reversals.Any(r => r.Status == JournalEntryStatus.Posted)
                && (!_reportBefore.HasValue || j.AccountingDate < _reportBefore.Value))
            .Select(j => new { SaleId = j.SourceEntityId!.Value, j.SourceEventId,
                Debit = j.Lines.Sum(l => l.Debit), Credit = j.Lines.Sum(l => l.Credit) })
            .ToListAsync(ct);
        foreach (var group in journals.GroupBy(j => j.SaleId))
        {
            // A duplicated/corrupt historical journal remains unverified; no guessing.
            var exact = group.Where(j => j.SourceEventId == SalesAccountingAdapter.BuildCogsSourceEventId(j.SaleId)).ToList();
            if (exact.Count == 1 && exact[0].Debit > 0m && exact[0].Debit == exact[0].Credit)
                costs[group.Key] = exact[0].Debit;
        }
        return costs;
    }
}
