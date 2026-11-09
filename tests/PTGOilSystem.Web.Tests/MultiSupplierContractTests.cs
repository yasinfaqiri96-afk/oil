using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Helpers;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// قرارداد خرید چندتأمین‌کننده (یک قرارداد، یک کد): طلبِ هر بارگیری به تأمین‌کنندهٔ همان بارگیری
/// می‌رسد و قرارداد عادی دقیقاً مثل قبل به تأمین‌کنندهٔ قرارداد.
/// </summary>
public class MultiSupplierContractTests
{
    [Fact]
    public void LoadingLedger_MultiSupplierContract_CreditsTheLoadingSupplier()
    {
        var contract = PurchaseContract(supplierId: null);
        var loading = PricedLoading(supplierId: 7, quantityMt: 10m, priceUsd: 500m);

        Assert.True(SupplierLoadingLedger.IsPostable(loading, contract));
        var request = SupplierLoadingLedger.Create(loading, contract);

        Assert.Equal(7, request.SupplierId);
        Assert.Equal(contract.Id, request.ContractId);
        Assert.Equal(5000m, request.AmountUsd);
    }

    [Fact]
    public void LoadingLedger_StandardContract_StillCreditsTheContractSupplier()
    {
        var contract = PurchaseContract(supplierId: 3);
        var loading = PricedLoading(supplierId: null, quantityMt: 2m, priceUsd: 100m);

        var request = SupplierLoadingLedger.Create(loading, contract);

        Assert.Equal(3, request.SupplierId);
        Assert.Equal(200m, request.AmountUsd);
    }

    [Fact]
    public void LoadingLedger_MultiSupplierLoadingWithoutSupplier_IsNotPosted()
    {
        var contract = PurchaseContract(supplierId: null);
        var loading = PricedLoading(supplierId: null, quantityMt: 2m, priceUsd: 100m);

        Assert.False(SupplierLoadingLedger.IsPostable(loading, contract));
    }

    [Fact]
    public async Task SupplierScope_AcceptsOnlySuppliersOfTheContract()
    {
        await using var db = NewDb();
        var multi = PurchaseContract(supplierId: null, id: 10);
        multi.ContractSuppliers.Add(new ContractSupplier { SupplierId = 1 });
        multi.ContractSuppliers.Add(new ContractSupplier { SupplierId = 2 });
        var single = PurchaseContract(supplierId: 5, id: 11);
        db.Contracts.AddRange(multi, single);
        await db.SaveChangesAsync();

        Assert.False(await ContractSupplierScope.IsForeignSupplierAsync(db, multi, 2));
        Assert.True(await ContractSupplierScope.IsForeignSupplierAsync(db, multi, 3));
        Assert.False(await ContractSupplierScope.IsForeignSupplierAsync(db, single, 5));
        Assert.True(await ContractSupplierScope.IsForeignSupplierAsync(db, single, 6));

        Assert.Equal([10], await db.Contracts.WhereSupplier(1).Select(c => c.Id).ToListAsync());
        Assert.Equal([11], await db.Contracts.WhereSupplier(5).Select(c => c.Id).ToListAsync());
        Assert.Equal([1, 2], (await ContractSupplierScope.SupplierIdsAsync(db, 10)).Order().ToList());
        Assert.True(await ContractSupplierScope.IsMultiSupplierAsync(db, 10));
        Assert.False(await ContractSupplierScope.IsMultiSupplierAsync(db, 11));
    }

    private static Contract PurchaseContract(int? supplierId, int id = 1)
        => new()
        {
            Id = id,
            ContractName = $"C-{id}",
            ContractNumber = $"P-{id:000}",
            ContractType = ContractType.Purchase,
            Status = ContractStatus.Active,
            CompanyId = 1,
            ProductId = 1,
            SupplierId = supplierId,
            ContractDate = new DateTime(2026, 11, 1),
            PricingMethod = PricingMethod.Fixed,
            QuantityMt = 100m,
            UnitPriceUsd = 500m
        };

    private static LoadingRegister PricedLoading(int? supplierId, decimal quantityMt, decimal priceUsd)
        => new()
        {
            Id = 42,
            ContractId = 1,
            SupplierId = supplierId,
            ProductId = 1,
            LoadingDate = new DateTime(2026, 11, 2),
            LoadedQuantityMt = quantityMt,
            LoadingPriceUsd = priceUsd,
            SettlementCurrencyCode = "USD"
        };

    private static ApplicationDbContext NewDb()
        => new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
}
