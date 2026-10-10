using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services.Accounting;
using PTGOilSystem.Web.Services.OperationalPeriod;

namespace PTGOilSystem.Web.Services;

/// <summary>
/// Updates an unconsumed, unposted transport estimate after its sole loading is priced.
/// Received, sold, continued, cancelled, archived or financially locked history is immutable
/// here; correcting that history requires its explicit revision/reversal workflow.
/// SaveChanges remains owned by the caller.
/// </summary>
public static class TransportLegPurchaseCostSync
{
    public static async Task<int> SyncFromLoadingAsync(
        ApplicationDbContext db,
        int loadingRegisterId,
        decimal? previousPriceUsd,
        decimal? newPriceUsd,
        CancellationToken ct = default)
    {
        if (previousPriceUsd == newPriceUsd || newPriceUsd is null or <= 0m)
            return 0;

        var source = await db.LoadingRegisters.AsNoTracking()
            .Where(l => l.Id == loadingRegisterId && !l.IsCancelled && !l.IsArchived
                && l.Contract != null && l.Contract.Status == ContractStatus.Active)
            .Select(l => new { l.ContractId, CompanyId = l.Contract!.CompanyId, l.LoadingDate })
            .SingleOrDefaultAsync(ct);
        if (source is null)
            return 0;

        var candidates = await db.InventoryTransportLegs
            .Where(l => !l.IsArchived
                && (l.Status == InventoryTransportLegStatus.Loaded || l.Status == InventoryTransportLegStatus.InTransit)
                && l.SourcePurchaseContractId == source.ContractId
                && l.PurchaseUnitCostUsd == previousPriceUsd
                && l.OutboundInventoryMovementId == null
                && l.Allocations.Any(a => a.SourceLoadingRegisterId == loadingRegisterId)
                && l.Allocations.All(a => a.SourceLoadingRegisterId == loadingRegisterId)
                && !db.InventoryTransportReceipts.Any(r => r.InventoryTransportLegId == l.Id && !r.IsCancelled)
                && !db.InventoryTransportLegAllocations.Any(a => a.SourceTransportLegId == l.Id
                    && a.InventoryTransportLeg != null && a.InventoryTransportLeg.Status != InventoryTransportLegStatus.Cancelled)
                && !db.SalesTransactionSourceAllocations.Any(a => (a.TransportLegId == l.Id || a.SourceTransportLegId == l.Id)
                    && a.SalesTransaction != null && !a.SalesTransaction.IsCancelled)
                && !db.LossEvents.Any(e => e.TransportLegId == l.Id && !e.IsCancelled)
                && !db.JournalEntries.Any(j => j.SourceEntityType == nameof(InventoryTransportLeg)
                    && j.SourceEntityId == l.Id && j.Status == JournalEntryStatus.Posted))
            .ToListAsync(ct);
        if (candidates.Count == 0)
            return 0;

        var operationalLock = await new OperationalPeriodGuard(db).GetStatusAsync(ct);
        var companyIds = new HashSet<int> { source.CompanyId };
        var ownerCompanyId = await new SystemCompanyProvider(db).FindOwnerCompanyIdAsync(ct);
        if (ownerCompanyId.HasValue)
            companyIds.Add(ownerCompanyId.Value);
        // Legacy accounting-disabled installations have no fiscal calendar. Once configured,
        // use its actual guard resolution instead of inventing an open-period default.
        var calendarCompanyIds = await db.FiscalYears.AsNoTracking()
            .Where(y => companyIds.Contains(y.CompanyId)).Select(y => y.CompanyId).Distinct().ToListAsync(ct);
        var calendar = new FiscalCalendarService(db);
        var openDates = new Dictionary<DateTime, bool>();
        var changed = 0;
        foreach (var leg in candidates)
        {
            if (!await IsOpenAsync(leg.LoadedDate.Date) || !await IsOpenAsync(source.LoadingDate.Date))
                continue;
            leg.PurchaseUnitCostUsd = newPriceUsd;
            changed++;
        }
        return changed;

        async Task<bool> IsOpenAsync(DateTime date)
        {
            if (openDates.TryGetValue(date, out var open))
                return open;
            open = !operationalLock.IsLocked || date > operationalLock.LockedThroughDate!.Value.Date;
            if (open)
            {
                foreach (var companyId in calendarCompanyIds)
                {
                    if ((await calendar.ResolveAsync(companyId, date, ct)).Resolution != FiscalCalendarResolution.Open)
                    {
                        open = false;
                        break;
                    }
                }
            }
            openDates[date] = open;
            return open;
        }
    }
}
