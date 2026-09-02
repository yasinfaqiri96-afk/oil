using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services;
using Xunit;

namespace PTGOilSystem.Web.Tests;

public class OperationalAssetUsageP2Tests
{
    [Fact]
    public async Task Carrier_mapping_prefers_service_provider_then_deterministic_asset_owner_then_driver()
    {
        await using var db = CreateDb();
        db.OperationalAssets.Add(new OperationalAsset { Id = 1, AssetCode = "A1", Name = "Asset", IsActive = true });
        db.AssetOwnershipShares.Add(new AssetOwnershipShare
        {
            OperationalAssetId = 1,
            OwnerType = AssetOwnerType.Company,
            CompanyId = 7,
            SharePercent = 100m,
            EffectiveFrom = Utc(2026, 1, 1)
        });
        await db.SaveChangesAsync();
        var service = new AssetUsageChargeService(db);

        Assert.Equal(new CarrierPartyRef(AccountingPartyType.ServiceProvider, 3),
            await service.ResolveCarrierPartyAsync(3, 4, 1, Utc(2026, 6, 1)));
        Assert.Equal(new CarrierPartyRef(AccountingPartyType.Company, 7),
            await service.ResolveCarrierPartyAsync(null, 4, 1, Utc(2026, 6, 1)));
        Assert.Equal(new CarrierPartyRef(AccountingPartyType.Driver, 4),
            await service.ResolveCarrierPartyAsync(null, 4, null, Utc(2026, 6, 1)));
    }

    [Fact]
    public async Task Legacy_rent_dual_write_is_idempotent_and_cancellation_preserves_usage()
    {
        await using var db = CreateDb();
        db.OperationalAssets.Add(new OperationalAsset { Id = 1, AssetCode = "A1", Name = "Asset", IsActive = true });
        var rent = new AssetRentTransaction
        {
            Id = 9,
            OperationalAssetId = 1,
            RentDate = Utc(2026, 6, 1),
            UsageType = AssetRentUsageType.ExternalCustomerRental,
            ChargedToType = AssetRentChargedToType.Customer,
            ChargedToCustomerId = 12,
            QuantityMt = 20m,
            Rate = 5m,
            Currency = "USD",
            FxRateToUsd = 1m,
            AmountOriginal = 100m,
            AmountUsd = 100m
        };
        db.AssetRentTransactions.Add(rent);
        await db.SaveChangesAsync();
        var service = new AssetUsageChargeService(db);

        await service.SyncLegacyRentAsync(rent);
        await service.SyncLegacyRentAsync(rent);

        var usage = Assert.Single(await db.AssetUsages.ToListAsync());
        Assert.Equal(20m, usage.QuantityMt);
        var charge = Assert.Single(await db.AssetCharges.ToListAsync());
        Assert.Equal(AssetChargeKind.ExternalRental, charge.ChargeKind);
        Assert.Equal(AccountingPartyType.Customer, charge.CounterpartyPartyType);
        Assert.Equal(12, charge.CounterpartyPartyId);

        await service.CancelLegacyRentChargeAsync(rent.Id, "cancelled");

        Assert.Single(await db.AssetUsages.ToListAsync());
        Assert.False((await db.AssetUsages.SingleAsync()).IsReversed);
        charge = await db.AssetCharges.SingleAsync();
        Assert.True(charge.IsCancelled);
        Assert.Equal(AssetChargePostingStatus.Cancelled, charge.PostingStatus);
    }

    [Fact]
    public async Task Operation_usage_contains_no_money_and_is_unique_per_asset_document()
    {
        await using var db = CreateDb();
        db.OperationalAssets.Add(new OperationalAsset { Id = 1, AssetCode = "A1", Name = "Asset", IsActive = true });
        var dispatch = new TruckDispatch
        {
            Id = 20,
            OperationalAssetId = 1,
            ContractId = 1,
            ProductId = 1,
            TruckId = 1,
            DispatchDate = Utc(2026, 6, 2),
            LoadedQuantityMt = 33m,
            FreightCostUsd = 999m
        };
        db.TruckDispatches.Add(dispatch);
        await db.SaveChangesAsync();
        var service = new AssetUsageChargeService(db);

        await service.SyncOperationAsync(dispatch);
        dispatch.LoadedQuantityMt = 34m;
        await service.SyncOperationAsync(dispatch);

        var usage = Assert.Single(await db.AssetUsages.ToListAsync());
        Assert.Equal(34m, usage.QuantityMt);
        Assert.DoesNotContain(typeof(AssetUsage).GetProperties(), p =>
            p.Name.Contains("Amount", StringComparison.OrdinalIgnoreCase)
            || p.Name.Contains("Rate", StringComparison.OrdinalIgnoreCase)
            || p.Name.Contains("Currency", StringComparison.OrdinalIgnoreCase));
    }

    private static ApplicationDbContext CreateDb()
        => new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static DateTime Utc(int year, int month, int day)
        => new(year, month, day, 0, 0, 0, DateTimeKind.Utc);
}
