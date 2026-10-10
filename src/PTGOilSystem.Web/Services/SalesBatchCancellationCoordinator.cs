using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;

namespace PTGOilSystem.Web.Services;

// Batch first, then sales in ascending ID order. All cancellation paths share this order,
// so concurrent cancellations of different lines cannot leave an empty batch marked active.
public sealed class SalesBatchCancellationCoordinator(ApplicationDbContext db)
{
    public async Task LockAsync(int? batchId, IReadOnlyCollection<int> saleIds, CancellationToken ct = default)
    {
        if (db.Database.ProviderName != "Npgsql.EntityFrameworkCore.PostgreSQL") return;
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Cancellation source locks require the caller's transaction.");
        if (batchId.HasValue)
            await db.Database.ExecuteSqlInterpolatedAsync(
                $@"SELECT 1 FROM ""SalesBatches"" WHERE ""Id"" = {batchId.Value} FOR UPDATE", ct);
        var ids = saleIds.Distinct().OrderBy(id => id).ToArray();
        if (ids.Length > 0)
            await db.Database.ExecuteSqlInterpolatedAsync(
                $@"SELECT 1 FROM ""SalesTransactions"" WHERE ""Id"" = ANY({ids}) ORDER BY ""Id"" FOR UPDATE", ct);
    }

    // Call after saving line cancellations while the batch row is still locked. Historical
    // header totals remain intact; current totals are already calculated from active lines.
    public async Task FinalizeAsync(int? batchId, CancellationToken ct = default)
    {
        if (!batchId.HasValue) return;
        var hasActiveLines = await db.SalesTransactions.AsNoTracking()
            .AnyAsync(s => s.SalesBatchId == batchId.Value && !s.IsCancelled, ct);
        if (hasActiveLines) return;
        var batch = await db.SalesBatches.SingleOrDefaultAsync(b => b.Id == batchId.Value, ct);
        if (batch is null || batch.IsCancelled) return;
        batch.IsCancelled = true;
        await db.SaveChangesAsync(ct);
    }
}
