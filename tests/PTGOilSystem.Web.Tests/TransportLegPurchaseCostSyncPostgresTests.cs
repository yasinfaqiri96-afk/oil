using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PTGOilSystem.Web.Configuration;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Accounting;
using Xunit;

namespace PTGOilSystem.Web.Tests;

[Collection(AccountingPostgreSqlCollection.CollectionName)]
[Trait("Category", "PostgreSql")]
[Trait("Category", "Integration")]
public sealed class TransportLegPurchaseCostSyncPostgresTests(AccountingPostgreSqlFixture fixture)
{
    private static readonly DateTime LoadingDate = new(2026, 7, 5);

    [Theory]
    [InlineData("open", 1)]
    [InlineData("partial-receipt", 0)]
    [InlineData("closed-year", 0)]
    [InlineData("locked-period", 0)]
    [InlineData("posted-journal", 0)]
    public async Task Real_Postgres_Updates_Only_Unconsumed_Unposted_Open_Estimates(string scenario, int expectedChanged)
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var loading = new LoadingRegister { ContractId = scope.Contract.Id, ProductId = scope.Product.Id,
            LoadingDate = LoadingDate, LoadedQuantityMt = 20m, LoadingPriceUsd = 500m };
        db.LoadingRegisters.Add(loading);
        await db.SaveChangesAsync();
        var leg = new InventoryTransportLeg { SourcePurchaseContractId = scope.Contract.Id,
            ProductId = scope.Product.Id, LoadedDate = LoadingDate, QuantityMt = 20m,
            PurchaseUnitCostUsd = 500m, Status = InventoryTransportLegStatus.Loaded,
            TransportType = LoadingTransportType.Truck };
        leg.Allocations.Add(new InventoryTransportLegAllocation { SourceLoadingRegisterId = loading.Id,
            SourcePurchaseContractId = scope.Contract.Id, QuantityMt = 20m });
        db.InventoryTransportLegs.Add(leg);
        await db.SaveChangesAsync();
        if (scenario == "partial-receipt")
            db.InventoryTransportReceipts.Add(new InventoryTransportReceipt { InventoryTransportLegId = leg.Id,
                ReceiptDate = LoadingDate.AddDays(1), ReceivedQuantityMt = 1m,
                DestinationTerminalId = scope.Terminal.Id, DestinationStorageTankId = scope.Tank.Id });
        if (scenario == "closed-year")
            (await db.FiscalYears.SingleAsync(x => x.CompanyId == scope.Company.Id)).Status = FiscalYearStatus.Closed;
        if (scenario == "locked-period")
            scope.Period.Status = FiscalPeriodStatus.HardLocked;
        await db.SaveChangesAsync();
        if (scenario == "posted-journal")
        {
            var options = Options.Create(new AccountingOptions { Enabled = true });
            var posting = new AccountingPostingService(db, new PeriodGuard(db, new FiscalCalendarService(db)),
                options, new SystemCompanyProvider(db));
            await posting.PostAsync(new AccountingPostRequest(scope.Company.Id, $"SYNC-LEG-{leg.Id}",
                LoadingDate, LoadingDate, LoadingDate, "InventoryTransfer",
                [new AccountingPostLine(scope.Settings.InventoryInTransitAccountId, 10_000m, 0m, "USD", 10_000m, 1m),
                 new AccountingPostLine(scope.Settings.AccountsPayableAccountId, 0m, 10_000m, "USD", 10_000m, 1m,
                     PartyType: AccountingPartyType.Supplier, PartyId: scope.Supplier.Id)],
                SourceEventId: $"SYNC-LEG-{leg.Id}:Created", SourceEntityType: nameof(InventoryTransportLeg),
                SourceEntityId: leg.Id));
        }
        var journalsBefore = await db.JournalEntries.CountAsync();
        var movementsBefore = await db.InventoryMovements.CountAsync();
        var changed = await TransportLegPurchaseCostSync.SyncFromLoadingAsync(db, loading.Id, 500m, 700m);
        Assert.Equal(expectedChanged, changed);
        await db.SaveChangesAsync();
        var actual = await db.InventoryTransportLegs.AsNoTracking().SingleAsync(x => x.Id == leg.Id);
        Assert.Equal(expectedChanged == 1 ? 700m : 500m, actual.PurchaseUnitCostUsd);
        Assert.Equal(20m, actual.QuantityMt);
        Assert.Equal(20m, await db.InventoryTransportLegAllocations.Where(x => x.InventoryTransportLegId == leg.Id)
            .SumAsync(x => x.QuantityMt));
        Assert.Equal(journalsBefore, await db.JournalEntries.CountAsync());
        Assert.Equal(movementsBefore, await db.InventoryMovements.CountAsync());
    }
}
