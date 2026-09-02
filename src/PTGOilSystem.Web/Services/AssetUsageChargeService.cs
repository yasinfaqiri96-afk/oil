using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;

namespace PTGOilSystem.Web.Services;

public readonly record struct CarrierPartyRef(AccountingPartyType PartyType, int PartyId);

public sealed class AssetUsageChargeService(ApplicationDbContext db)
{
    public Task SyncOperationAsync(InventoryTransportLeg leg, CancellationToken cancellationToken = default)
        => UpsertUsageAsync(
            leg.OperationalAssetId,
            AssetUsageDocumentType.InventoryTransportLeg,
            leg.Id,
            leg.LoadedDate,
            leg.QuantityMt,
            null,
            cancellationToken,
            // همان قاعدهٔ دیسپچ: حملِ لغوشده استفادهٔ دارایی نیست، پس سطر مصرف برگشتی می‌شود
            // و از محاسبهٔ کرایه/استهلاک کنار می‌رود (سطر حذف نمی‌شود تا تاریخچه بماند).
            leg.Status == InventoryTransportLegStatus.Cancelled);

    /// <summary>
    /// سطر مصرفِ legهایی که در ویرایش سند حمل فیزیکی حذف می‌شوند را برگشتی علامت می‌زند.
    /// حذف نمی‌کند چون ممکن است AssetCharge ثبت‌شده به آن وصل باشد؛ برگشتی‌کردن همان حالتِ
    /// درست است: این استفاده اتفاق نیفتاده. idempotent است.
    /// </summary>
    public async Task MarkLegUsagesReversedAsync(
        IReadOnlyCollection<int> legIds,
        CancellationToken cancellationToken = default)
    {
        if (legIds.Count == 0)
        {
            return;
        }

        var usages = await db.AssetUsages
            .Where(u => u.DocumentType == AssetUsageDocumentType.InventoryTransportLeg
                && legIds.Contains(u.DocumentId)
                && !u.IsReversed)
            .ToListAsync(cancellationToken);
        if (usages.Count == 0)
        {
            return;
        }

        foreach (var usage in usages)
        {
            usage.IsReversed = true;
            usage.UpdatedAtUtc = DateTime.UtcNow;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public Task SyncOperationAsync(
        InventoryTransportReceipt receipt,
        InventoryTransportLeg leg,
        CancellationToken cancellationToken = default)
        => UpsertUsageAsync(
            receipt.OperationalAssetId ?? leg.OperationalAssetId,
            AssetUsageDocumentType.InventoryTransportReceipt,
            receipt.Id,
            receipt.ReceiptDate,
            receipt.ReceivedQuantityMt,
            leg.DestinationLocationId,
            cancellationToken,
            receipt.IsCancelled);

    public Task SyncOperationAsync(TruckDispatch dispatch, CancellationToken cancellationToken = default)
        => UpsertUsageAsync(
            dispatch.OperationalAssetId,
            AssetUsageDocumentType.TruckDispatch,
            dispatch.Id,
            dispatch.DispatchDate,
            dispatch.LoadedQuantityMt,
            dispatch.DestinationLocationId,
            cancellationToken,
            dispatch.Status == DispatchStatus.Cancelled);

    public async Task<CarrierPartyRef?> ResolveCarrierPartyAsync(
        int? serviceProviderId,
        int? driverId,
        int? operationalAssetId,
        DateTime operationDate,
        CancellationToken cancellationToken = default)
    {
        if (serviceProviderId is > 0)
        {
            return new CarrierPartyRef(AccountingPartyType.ServiceProvider, serviceProviderId.Value);
        }

        if (operationalAssetId is > 0)
        {
            var activeOwners = await db.AssetOwnershipShares
                .AsNoTracking()
                .Where(s => s.OperationalAssetId == operationalAssetId.Value
                    && s.EffectiveFrom <= operationDate
                    && (!s.EffectiveTo.HasValue || s.EffectiveTo.Value >= operationDate))
                .Select(s => new { s.OwnerType, s.CompanyId, s.PartnerId, s.SharePercent })
                .ToListAsync(cancellationToken);

            if (activeOwners.Count == 1 && activeOwners[0].SharePercent == 100m)
            {
                var owner = activeOwners[0];
                if (owner.OwnerType == AssetOwnerType.Company && owner.CompanyId is > 0)
                {
                    return new CarrierPartyRef(AccountingPartyType.Company, owner.CompanyId.Value);
                }

                if (owner.OwnerType == AssetOwnerType.Partner && owner.PartnerId is > 0)
                {
                    return new CarrierPartyRef(AccountingPartyType.Partner, owner.PartnerId.Value);
                }
            }
        }

        return driverId is > 0
            ? new CarrierPartyRef(AccountingPartyType.Driver, driverId.Value)
            : null;
    }

    public async Task SyncLegacyRentAsync(AssetRentTransaction rent, CancellationToken cancellationToken = default)
    {
        var (documentType, documentId) = ResolveUsageDocument(rent);
        var usage = await db.AssetUsages
            .Include(u => u.Charges)
            .SingleOrDefaultAsync(u => u.OperationalAssetId == rent.OperationalAssetId
                && u.DocumentType == documentType
                && u.DocumentId == documentId, cancellationToken);

        if (usage is null)
        {
            usage = new AssetUsage
            {
                OperationalAssetId = rent.OperationalAssetId,
                DocumentType = documentType,
                DocumentId = documentId
            };
            db.AssetUsages.Add(usage);
        }

        usage.UsageDate = rent.RentDate;
        usage.QuantityMt = rent.QuantityMt;
        usage.DistanceKm = rent.DistanceKm;
        usage.Days = rent.Days;
        usage.IsReversed = false;
        await PopulateLocationsAsync(usage, rent, cancellationToken);

        if (usage.Id == 0)
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        var chargeKind = rent.UsageType == AssetRentUsageType.InternalCompanyUse
            ? AssetChargeKind.InternalTransfer
            : AssetChargeKind.ExternalRental;
        var charge = usage.Charges.FirstOrDefault(c => c.LegacyAssetRentTransactionId == rent.Id)
            ?? usage.Charges.FirstOrDefault(c => c.ChargeKind == chargeKind);
        if (charge is null)
        {
            charge = new AssetCharge
            {
                AssetUsageId = usage.Id,
                ChargeKind = chargeKind,
                LegacyAssetRentTransactionId = rent.Id
            };
            db.AssetCharges.Add(charge);
        }

        charge.ChargeKind = chargeKind;
        charge.RateBasis = ResolveRateBasis(rent);
        charge.Rate = rent.Rate;
        charge.QuantityBasis = ResolveQuantityBasis(rent);
        charge.Currency = rent.Currency;
        charge.FxRateToUsd = rent.FxRateToUsd;
        charge.AmountOriginal = rent.AmountOriginal;
        charge.AmountUsd = rent.AmountUsd;
        charge.ContractId = rent.ChargedToContractId;
        (charge.CounterpartyPartyType, charge.CounterpartyPartyId) = await ResolveCounterpartyAsync(rent, cancellationToken);
        charge.LedgerEntryId = rent.LedgerEntryId;
        charge.JournalEntryId = await db.JournalEntries.AsNoTracking()
            .Where(j => j.SourceEntityType == nameof(AssetRentTransaction)
                && j.SourceEntityId == rent.Id
                && !j.IsReversal)
            .OrderBy(j => j.Id)
            .Select(j => (int?)j.Id)
            .FirstOrDefaultAsync(cancellationToken);
        charge.IsCancelled = rent.IsCancelled;
        charge.PostingStatus = rent.IsCancelled
            ? AssetChargePostingStatus.Cancelled
            : rent.UsageType is not (AssetRentUsageType.InternalCompanyUse or AssetRentUsageType.ExternalCustomerRental)
                ? AssetChargePostingStatus.Skipped
            : rent.IsPostedToLedger || charge.JournalEntryId.HasValue
                ? AssetChargePostingStatus.Posted
                : AssetChargePostingStatus.Pending;
        charge.SkipReason = rent.IsCancelled
            ? rent.CancelReason
            : rent.UsageType is not (AssetRentUsageType.InternalCompanyUse or AssetRentUsageType.ExternalCustomerRental)
                ? "Legacy usage type requires manual classification."
                : null;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task CancelLegacyRentChargeAsync(
        int legacyAssetRentTransactionId,
        string? reason,
        CancellationToken cancellationToken = default)
    {
        var charge = await db.AssetCharges
            .SingleOrDefaultAsync(c => c.LegacyAssetRentTransactionId == legacyAssetRentTransactionId, cancellationToken);
        if (charge is null)
        {
            return;
        }

        charge.IsCancelled = true;
        charge.PostingStatus = AssetChargePostingStatus.Cancelled;
        charge.SkipReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task UpsertUsageAsync(
        int? operationalAssetId,
        AssetUsageDocumentType documentType,
        int documentId,
        DateTime usageDate,
        decimal? quantityMt,
        int? toLocationId,
        CancellationToken cancellationToken,
        bool isReversed = false)
    {
        if (operationalAssetId is not > 0 || documentId <= 0)
        {
            return;
        }

        var usage = await db.AssetUsages.SingleOrDefaultAsync(
            u => u.OperationalAssetId == operationalAssetId.Value
                && u.DocumentType == documentType
                && u.DocumentId == documentId,
            cancellationToken);
        if (usage is null)
        {
            usage = new AssetUsage
            {
                OperationalAssetId = operationalAssetId.Value,
                DocumentType = documentType,
                DocumentId = documentId
            };
            db.AssetUsages.Add(usage);
        }

        usage.UsageDate = usageDate;
        usage.QuantityMt = quantityMt;
        usage.ToLocationId = toLocationId;
        usage.IsReversed = isReversed;
        await db.SaveChangesAsync(cancellationToken);
    }

    private static (AssetUsageDocumentType Type, int Id) ResolveUsageDocument(AssetRentTransaction rent)
    {
        if (rent.LoadingRegisterId is > 0) return (AssetUsageDocumentType.LoadingRegister, rent.LoadingRegisterId.Value);
        if (rent.TransportLegId is > 0) return (AssetUsageDocumentType.InventoryTransportLeg, rent.TransportLegId.Value);
        if (rent.InventoryTransportReceiptId is > 0) return (AssetUsageDocumentType.InventoryTransportReceipt, rent.InventoryTransportReceiptId.Value);
        if (rent.TruckDispatchId is > 0) return (AssetUsageDocumentType.TruckDispatch, rent.TruckDispatchId.Value);
        return (AssetUsageDocumentType.AssetRentTransaction, rent.Id);
    }

    private async Task PopulateLocationsAsync(AssetUsage usage, AssetRentTransaction rent, CancellationToken cancellationToken)
    {
        if (rent.LoadingRegisterId is > 0)
        {
            var row = await db.LoadingRegisters.AsNoTracking()
                .Where(x => x.Id == rent.LoadingRegisterId.Value)
                .Select(x => new { From = x.OriginLocationId, To = x.Contract!.DestinationLocationId })
                .SingleOrDefaultAsync(cancellationToken);
            usage.FromLocationId = row?.From;
            usage.ToLocationId = row?.To;
        }
        else if (rent.TransportLegId is > 0)
        {
            var row = await db.InventoryTransportLegs.AsNoTracking()
                .Where(x => x.Id == rent.TransportLegId.Value)
                .Select(x => new { From = (int?)null, To = x.DestinationLocationId })
                .SingleOrDefaultAsync(cancellationToken);
            usage.FromLocationId = row?.From;
            usage.ToLocationId = row?.To;
        }
        else if (rent.InventoryTransportReceiptId is > 0)
        {
            var row = await db.InventoryTransportReceipts.AsNoTracking()
                .Where(x => x.Id == rent.InventoryTransportReceiptId.Value)
                .Select(x => new { From = (int?)null, To = x.InventoryTransportLeg!.DestinationLocationId })
                .SingleOrDefaultAsync(cancellationToken);
            usage.FromLocationId = row?.From;
            usage.ToLocationId = row?.To;
        }
        else if (rent.TruckDispatchId is > 0)
        {
            usage.ToLocationId = await db.TruckDispatches.AsNoTracking()
                .Where(x => x.Id == rent.TruckDispatchId.Value)
                .Select(x => x.DestinationLocationId)
                .SingleOrDefaultAsync(cancellationToken);
        }
    }

    private async Task<(AccountingPartyType? Type, int? Id)> ResolveCounterpartyAsync(
        AssetRentTransaction rent,
        CancellationToken cancellationToken)
    {
        if (rent.ChargedToCustomerId is > 0) return (AccountingPartyType.Customer, rent.ChargedToCustomerId);
        if (rent.ChargedToCompanyId is > 0) return (AccountingPartyType.Company, rent.ChargedToCompanyId);
        if (rent.ChargedToPartnerId is > 0) return (AccountingPartyType.Partner, rent.ChargedToPartnerId);
        if (rent.ChargedToServiceProviderId is > 0) return (AccountingPartyType.ServiceProvider, rent.ChargedToServiceProviderId);

        if (rent.ChargedToContractId is > 0)
        {
            var contract = await db.Contracts.AsNoTracking()
                .Where(c => c.Id == rent.ChargedToContractId.Value)
                .Select(c => new { c.ContractType, c.SupplierId, c.CustomerId, c.CompanyId })
                .SingleOrDefaultAsync(cancellationToken);
            if (contract?.ContractType == ContractType.Purchase && contract.SupplierId is > 0)
                return (AccountingPartyType.Supplier, contract.SupplierId);
            if (contract?.ContractType == ContractType.Sale && contract.CustomerId is > 0)
                return (AccountingPartyType.Customer, contract.CustomerId);
            if (contract?.CompanyId is > 0)
                return (AccountingPartyType.Company, contract.CompanyId);
        }

        return (null, null);
    }

    private static AssetChargeRateBasis ResolveRateBasis(AssetRentTransaction rent)
        => rent.Days is > 0m ? AssetChargeRateBasis.Days
            : rent.DistanceKm is > 0m ? AssetChargeRateBasis.DistanceKm
            : rent.QuantityMt is > 0m ? AssetChargeRateBasis.QuantityMt
            : AssetChargeRateBasis.FixedAmount;

    private static decimal? ResolveQuantityBasis(AssetRentTransaction rent)
        => rent.Days is > 0m ? rent.Days
            : rent.DistanceKm is > 0m ? rent.DistanceKm
            : rent.QuantityMt is > 0m ? rent.QuantityMt
            : null;
}
