using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;

namespace PTGOilSystem.Web.Services;

public sealed record CompletedBulkTransportRow(
    int LoadingRegisterId,
    int TransportLegId,
    decimal QuantityMt,
    bool IsCancelled,
    string? Reference);

/// <summary>Read-only recovery of committed row results after a disconnected bulk request.</summary>
public sealed class TransportBulkRequestQuery(ApplicationDbContext db)
{
    public async Task<IReadOnlyList<CompletedBulkTransportRow>> GetCompletedRowsAsync(
        string requestToken, int? userId = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestToken);
        var prefix = TransportWorkflowService.BulkRequestPrefix(requestToken.Trim());
        return await (from token in db.ProcessedFormTokens.AsNoTracking()
                      join leg in db.InventoryTransportLegs.AsNoTracking() on token.ReferenceId equals (int?)leg.Id
                      join allocation in db.InventoryTransportLegAllocations.AsNoTracking() on leg.Id equals allocation.InventoryTransportLegId
                      where token.Purpose.StartsWith(prefix) && token.UserId == userId
                          && token.ReferenceType == nameof(InventoryTransportLeg)
                          && allocation.SourceLoadingRegisterId.HasValue
                      orderby leg.Id
                      select new CompletedBulkTransportRow(
                          allocation.SourceLoadingRegisterId!.Value, leg.Id, allocation.QuantityMt,
                          leg.Status == InventoryTransportLegStatus.Cancelled, leg.RwbNo))
            .ToListAsync(ct);
    }
}
