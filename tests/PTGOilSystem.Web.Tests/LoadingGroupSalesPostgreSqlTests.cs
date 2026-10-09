using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PTGOilSystem.Web.Controllers;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.Sales;
using PTGOilSystem.Web.Services;
using Xunit;

namespace PTGOilSystem.Web.Tests;

[Collection(AccountingPostgreSqlCollection.CollectionName)]
[Trait("Category", "PostgreSql")]
[Trait("Category", "Integration")]
public sealed class LoadingGroupSalesPostgreSqlTests(AccountingPostgreSqlFixture fixture)
{
    [Fact]
    public async Task Concurrent_Seventy_Ton_Sales_Of_One_Hundred_Tons_Cannot_Both_Consume_The_Source()
    {
        var (loadingId, scope) = await SeedLoadingAsync();
        async Task<IActionResult> SubmitAsync()
        {
            await using var db = fixture.CreateDbContext();
            return await Controller(db).CreateGroup(Model(loadingId, scope, 70m), Guid.NewGuid().ToString("N"));
        }
        var results = await Task.WhenAll(SubmitAsync(), SubmitAsync());
        Assert.Single(results.OfType<RedirectToActionResult>());
        Assert.Single(results.OfType<ViewResult>());
        await using var verify = fixture.CreateDbContext();
        var receipts = await verify.LoadingReceipts.Where(r => r.LoadingRegisterId == loadingId && !r.IsCancelled).ToListAsync();
        Assert.Single(receipts);
        Assert.Equal(70m, receipts.Sum(r => r.ReceivedQuantityMt));
        var allocations = await verify.LoadingReceiptAllocations.Where(a => a.LoadingReceipt!.LoadingRegisterId == loadingId).ToListAsync();
        Assert.Single(allocations);
        Assert.Equal(scope.Contract.Id, allocations[0].SourcePurchaseContractId);
        Assert.Equal(30m, (await new PTGOilSystem.Web.Services.Operations.CargoSourceQueryService(verify)
            .LoadLoadingSourcesAsync([loadingId])).Single().RemainingQuantityMt);
        Assert.False(await verify.InventoryMovements.AnyAsync(m => m.LoadingReceipt != null && m.LoadingReceipt.LoadingRegisterId == loadingId));
    }

    [Fact]
    public async Task Repeated_Form_Request_Does_Not_Create_A_Second_Sale_Receipt_Or_Ledger()
    {
        var (loadingId, scope) = await SeedLoadingAsync();
        var token = Guid.NewGuid().ToString("N");
        for (var i = 0; i < 2; i++)
        {
            await using var db = fixture.CreateDbContext();
            Assert.IsType<RedirectToActionResult>(await Controller(db).CreateGroup(Model(loadingId, scope, 20m), token));
        }
        await using var verify = fixture.CreateDbContext();
        var allocation = Assert.Single(await verify.LoadingReceiptAllocations
            .Where(a => a.LoadingReceipt!.LoadingRegisterId == loadingId).ToListAsync());
        Assert.Equal(20m, allocation.QuantityMt);
        Assert.Equal(1, await verify.LedgerEntries.CountAsync(l => l.SourceType == "Sale" && l.SourceId == allocation.SalesTransactionId));
        Assert.Equal(1, await verify.ProcessedFormTokens.CountAsync(t => t.Token == token));
        Assert.Equal(80m, (await new PTGOilSystem.Web.Services.Operations.CargoSourceQueryService(verify)
            .LoadLoadingSourcesAsync([loadingId])).Single().RemainingQuantityMt);
    }

    private async Task<(int Id, PaymentAccountingAdapterTests.PaymentScope Scope)> SeedLoadingAsync()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var loading = new LoadingRegister { ContractId = scope.Contract.Id, ProductId = scope.Product.Id,
            LoadedQuantityMt = 100m, LoadingPriceUsd = 250m, LoadingDate = new DateTime(2026, 7, 1) };
        db.LoadingRegisters.Add(loading);
        await db.SaveChangesAsync();
        return (loading.Id, scope);
    }

    private static GroupSaleCreateViewModel Model(int loadingId, PaymentAccountingAdapterTests.PaymentScope scope, decimal quantity)
        => new() { CustomerId = scope.Customer.Id, Currency = "USD", SaleDate = new DateTime(2026, 7, 5),
            UnitPriceInCurrency = 500m, LoadingSaleTerminalId = scope.Terminal.Id,
            Items = [new() { Kind = GroupSaleSourceKind.LoadingRegister, Id = loadingId, QuantityMt = quantity }] };

    private static SalesController Controller(ApplicationDbContext db)
        => new(db, new StockService(db), new AuditService(db), NullLogger<SalesController>.Instance)
        {
            ControllerContext = new() { HttpContext = new DefaultHttpContext() },
            TempData = new TempDataDictionary(new DefaultHttpContext(), new MemoryTempDataProvider())
        };

    private sealed class MemoryTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }
}
