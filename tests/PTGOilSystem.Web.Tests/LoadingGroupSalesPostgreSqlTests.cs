using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PTGOilSystem.Web.Configuration;
using PTGOilSystem.Web.Services.Accounting;
using PTGOilSystem.Web.Services.LoadingReceipts;
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

    [Theory]
    [InlineData(0)] // two sale-page cancellations
    [InlineData(1)] // two receipt-page cancellations
    [InlineData(2)] // both entry points share one batch lock
    public async Task Concurrent_Last_Line_Cancellations_Close_Batch_And_Reverse_Financial_Effects(int entryPoints)
    {
        var (loadingId, scope) = await SeedLoadingAsync();
        int batchId;
        int secondLoadingId;
        List<SalesTransaction> sales;
        await using (var db = fixture.CreateDbContext())
        {
            var loading = await db.LoadingRegisters.Include(l => l.Contract).SingleAsync(l => l.Id == loadingId);
            var secondLoading = new LoadingRegister { ContractId = scope.Contract.Id, ProductId = scope.Product.Id,
                LoadedQuantityMt = 100m, LoadingPriceUsd = 250m, LoadingDate = loading.LoadingDate };
            db.LoadingRegisters.Add(secondLoading);
            await db.SaveChangesAsync();
            secondLoadingId = secondLoading.Id;
            var options = Options.Create(new AccountingOptions { Enabled = true,
                Pilots = new AccountingPilotOptions { Purchase = true } });
            var purchase = new PurchaseAccountingAdapter(db,
                new AccountingPostingService(db, new PeriodGuard(db, new FiscalCalendarService(db)), options, new SystemCompanyProvider(db)),
                new AccountingJournalNumberGenerator(), new PricingService(db), new InventoryValuationService(db),
                options, NullLogger<PurchaseAccountingAdapter>.Instance);
            Assert.Equal(PaymentPostingStatus.Posted, (await purchase.TryPostPurchaseAsync(loading)).Status);
            Assert.Equal(PaymentPostingStatus.Posted, (await purchase.TryPostPurchaseAsync(secondLoading)).Status);
            var model = Model(loadingId, scope, 20m);
            model.Items.Add(new() { Kind = GroupSaleSourceKind.LoadingRegister, Id = secondLoadingId, QuantityMt = 30m });
            var created = Assert.IsType<RedirectToActionResult>(await Controller(db, Accounting(db)).CreateGroup(model));
            batchId = Assert.IsType<int>(created.RouteValues!["id"]);
            sales = await db.SalesTransactions.AsNoTracking().Where(s => s.SalesBatchId == batchId).OrderBy(s => s.Id).ToListAsync();
            Assert.Equal(2, sales.Count);
            Assert.Equal(4, await db.JournalEntries.CountAsync(j => j.CompanyId == scope.Company.Id && j.SourceModule == "Sale"));
        }
        async Task CancelAsync(int index)
        {
            await using var db = fixture.CreateDbContext();
            var accounting = Accounting(db);
            if (entryPoints == 1 || entryPoints == 2 && index == 1)
            {
                var receiptId = await db.LoadingReceiptAllocations.Where(a => a.SalesTransactionId == sales[index].Id)
                    .Select(a => a.LoadingReceiptId).SingleAsync();
                var service = new LoadingReceiptCancellationService(db, new AuditService(db),
                    NullLogger<LoadingReceiptCancellationService>.Instance, salesAccounting: accounting);
                Assert.True((await service.CancelAsync([receiptId], "آزمایش لغو همزمان", null)).Succeeded);
            }
            else
            {
                var controller = Controller(db, accounting);
                Assert.IsType<RedirectToActionResult>(await controller.Cancel(sales[index].Id, "آزمایش لغو همزمان", version: sales[index].Version));
                Assert.False(controller.TempData.ContainsKey("err"));
            }
        }
        await Task.WhenAll(CancelAsync(0), CancelAsync(1)).WaitAsync(TimeSpan.FromSeconds(45));
        await using var verify = fixture.CreateDbContext();
        Assert.True((await verify.SalesBatches.SingleAsync(b => b.Id == batchId)).IsCancelled);
        Assert.Equal(2, await verify.SalesTransactions.CountAsync(s => s.SalesBatchId == batchId && s.IsCancelled));
        Assert.Equal(2, await verify.LoadingReceipts.CountAsync(r => (r.LoadingRegisterId == loadingId || r.LoadingRegisterId == secondLoadingId) && r.IsCancelled));
        Assert.Equal(2, await verify.LoadingReceiptAllocations.CountAsync(a => (a.LoadingReceipt!.LoadingRegisterId == loadingId || a.LoadingReceipt.LoadingRegisterId == secondLoadingId)
            && a.Status == LoadingReceiptAllocationStatus.Cancelled));
        var saleIds = sales.Select(s => s.Id).ToArray();
        Assert.Equal(4, await verify.LedgerEntries.CountAsync(l => l.SourceType == "Sale" && saleIds.Contains(l.SourceId)));
        var journals = await verify.JournalEntries.Include(j => j.Lines)
            .Where(j => j.CompanyId == scope.Company.Id && j.SourceModule == "Sale").ToListAsync();
        Assert.Equal(8, journals.Count);
        Assert.Equal(4, journals.Count(j => j.IsReversal));
        Assert.All(journals, j => Assert.Equal(j.Lines.Sum(l => l.Debit), j.Lines.Sum(l => l.Credit)));
        var cogsAccount = await verify.AccountingSettings.Where(s => s.CompanyId == scope.Company.Id)
            .Select(s => s.CostOfGoodsSoldAccountId).SingleAsync();
        Assert.Equal(0m, journals.SelectMany(j => j.Lines).Where(l => l.AccountId == cogsAccount).Sum(l => l.Debit - l.Credit));
        Assert.False(await verify.InventoryMovements.AnyAsync(m => m.LoadingReceipt != null && (m.LoadingReceipt.LoadingRegisterId == loadingId || m.LoadingReceipt.LoadingRegisterId == secondLoadingId)));
        Assert.False(await verify.InventoryValuationPools.AnyAsync(p => p.CompanyId == scope.Company.Id));
        Assert.Equal(100m, (await new PTGOilSystem.Web.Services.Operations.CargoSourceQueryService(verify)
            .LoadLoadingSourcesAsync([loadingId])).Single().RemainingQuantityMt);
        Assert.Equal(100m, (await new PTGOilSystem.Web.Services.Operations.CargoSourceQueryService(verify)
            .LoadLoadingSourcesAsync([secondLoadingId])).Single().RemainingQuantityMt);
        // Retrying either entry point creates no further ledger, journal or quantity change.
        if (entryPoints != 1) await CancelAsync(0);
        Assert.Equal(8, await verify.JournalEntries.CountAsync(j => j.CompanyId == scope.Company.Id && j.SourceModule == "Sale"));
    }

    private static SalesAccountingAdapter Accounting(ApplicationDbContext db)
    {
        var options = Options.Create(new AccountingOptions { Enabled = true,
            Pilots = new AccountingPilotOptions { Sale = true, Cogs = true } });
        return new SalesAccountingAdapter(db,
            new AccountingPostingService(db, new PeriodGuard(db, new FiscalCalendarService(db)), options, new SystemCompanyProvider(db)),
            new AccountingJournalNumberGenerator(), new InventoryValuationService(db), options,
            NullLogger<SalesAccountingAdapter>.Instance);
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

    private static SalesController Controller(ApplicationDbContext db, ISalesAccountingAdapter? accounting = null)
        => new(db, new StockService(db), new CurrencyConversionService(new PricingService(db)), new AuditService(db),
            NullLogger<SalesController>.Instance, salesAccounting: accounting)
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
