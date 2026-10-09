using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PTGOilSystem.Web.Configuration;
using PTGOilSystem.Web.Controllers;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.Loading;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Accounting;
using Xunit;

namespace PTGOilSystem.Web.Tests;

[Collection(AccountingPostgreSqlCollection.CollectionName)]
[Trait("Category", "PostgreSql")]
public sealed class LoadingReceiptIntegrityTests(AccountingPostgreSqlFixture fixture)
{
    [Fact]
    public async Task SingleAndBulk_HaveEquivalentPhysicalFinancialAndSourceEffects()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var loadings = await AddLoadings(db, scope, 2);
        var adapter = Adapter(db);
        await adapter.TryPostPurchasesAsync(loadings);
        var controller = Controller(db, adapter);
        Assert.IsType<RedirectResult>(await controller.Create(Single(scope, loadings[0].Id, 50m), Guid.NewGuid().ToString("N")));
        Assert.IsType<OkObjectResult>(await controller.BulkCreate(Bulk(scope, [loadings[1].Id], 50m), Guid.NewGuid().ToString("N")));

        var receipts = await db.LoadingReceipts.AsNoTracking().Where(x => loadings.Select(l => l.Id).Contains(x.LoadingRegisterId))
            .OrderBy(x => x.LoadingRegisterId).ToListAsync();
        Assert.Equal(2, receipts.Count);
        foreach (var receipt in receipts)
        {
            Assert.Equal(50m, receipt.ReceivedQuantityMt);
            var movement = await db.InventoryMovements.AsNoTracking().SingleAsync(x => x.LoadingReceiptId == receipt.Id);
            Assert.Equal(MovementDirection.In, movement.Direction);
            Assert.Equal(50m, movement.QuantityMt);
            Assert.Equal(scope.Contract.Id, movement.ContractId);
            var allocation = await db.LoadingReceiptAllocations.AsNoTracking().SingleAsync(x => x.LoadingReceiptId == receipt.Id);
            Assert.Equal(scope.Contract.Id, allocation.SourcePurchaseContractId);
            Assert.Equal(movement.Id, allocation.InventoryMovementId);
            Assert.Equal(LoadingReceiptAllocationStatus.Completed, allocation.Status);
            var journal = await db.JournalEntries.AsNoTracking().Include(x => x.Lines)
                .SingleAsync(x => x.SourceEventId == PurchaseAccountingAdapter.BuildReceiptSourceEventId(receipt.Id));
            Assert.Equal(receipt.Id, journal.SourceEntityId);
            Assert.Equal(25_000m, journal.Lines.Sum(x => x.Debit));
            Assert.Equal(journal.Lines.Sum(x => x.Debit), journal.Lines.Sum(x => x.Credit));
            Assert.True(await db.AuditLogs.AnyAsync(x => x.EntityName == nameof(LoadingReceipt) && x.EntityId == receipt.Id));
            Assert.Equal(PaymentPostingStatus.Duplicate, (await adapter.TryPostInventoryReceiptAsync(receipt)).Status);
        }
        var pool = await db.InventoryAverageCosts.AsNoTracking().SingleAsync(x => x.CompanyId == scope.Company.Id && x.ProductId == scope.Product.Id && x.TerminalId == scope.Terminal.Id);
        Assert.Equal(100m, pool.QuantityMt);
        Assert.Equal(50_000m, pool.TotalValueUsd);
        Assert.Equal(100m, await new StockService(db).GetFreeQuantityMtAsync(scope.Product.Id, terminalId: scope.Terminal.Id, storageTankId: scope.Tank.Id));
    }

    [Theory]
    [InlineData(false, false, "ACCOUNTING_DISABLED")]
    [InlineData(true, true, "PURCHASE_PRICE_PENDING")]
    [InlineData(true, false, "PURCHASE_NOT_POSTED")]
    public async Task SkippedAccounting_IsReportedWithoutInventingJournal(bool enabled, bool pending, string reason)
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var loadings = await AddLoadings(db, scope, 1, pending ? null : 500m);
        var controller = Controller(db, Adapter(db, enabled));
        var result = Assert.IsType<OkObjectResult>(await controller.BulkCreate(Bulk(scope, [loadings[0].Id], 50m)));
        var outcomes = Assert.IsAssignableFrom<IReadOnlyList<ReceiptAccountingOutcome>>(result.Value!.GetType().GetProperty("accounting")!.GetValue(result.Value));
        var outcome = Assert.Single(outcomes);
        Assert.Equal("Skipped", outcome.Status);
        Assert.Equal(reason, outcome.Reason);
        Assert.NotNull(controller.TempData.Peek("warn"));
        Assert.False(await db.JournalEntries.AnyAsync(x => x.SourceEventId == outcome.SourceEventId));
        Assert.Equal(50m, await db.InventoryMovements.Where(x => x.LoadingReceiptId == outcome.ReceiptId).SumAsync(x => x.QuantityMt));
    }

    [Fact]
    public async Task SameRequest_RetryDoesNotConsumeRemainingAgain()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var loading = (await AddLoadings(db, scope, 1))[0];
        var adapter = Adapter(db); await adapter.TryPostPurchaseAsync(loading);
        var token = Guid.NewGuid().ToString("N");
        Assert.IsType<OkObjectResult>(await Controller(db, adapter).BulkCreate(Bulk(scope, [loading.Id], 30m), token));
        db.ChangeTracker.Clear();
        var retry = Assert.IsType<OkObjectResult>(await Controller(db, Adapter(db)).BulkCreate(Bulk(scope, [loading.Id], 30m), token));
        Assert.Equal(true, retry.Value!.GetType().GetProperty("duplicate")!.GetValue(retry.Value));
        Assert.Single(await db.LoadingReceipts.Where(x => x.LoadingRegisterId == loading.Id).ToListAsync());
        Assert.Equal(30m, await db.InventoryAverageCosts.Where(x => x.CompanyId == scope.Company.Id && x.ProductId == scope.Product.Id).SumAsync(x => x.QuantityMt));
    }

    [Fact]
    public async Task FailedSecondAccountingCall_RollsBackEveryReceiptAndFinancialEffect()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var loadings = await AddLoadings(db, scope, 2);
        var adapter = Adapter(db); await adapter.TryPostPurchasesAsync(loadings);
        var token = Guid.NewGuid().ToString("N");
        var result = await Controller(db, new FailSecondReceiptAdapter(adapter)).BulkCreate(Bulk(scope, loadings.Select(x => x.Id).ToList(), 100m), token);
        Assert.IsType<BadRequestObjectResult>(result);
        db.ChangeTracker.Clear();
        var ids = loadings.Select(x => x.Id).ToArray();
        Assert.Empty(await db.LoadingReceipts.Where(x => ids.Contains(x.LoadingRegisterId)).ToListAsync());
        Assert.Empty(await db.InventoryMovements.Where(x => x.ProductId == scope.Product.Id).ToListAsync());
        Assert.Empty(await db.LoadingReceiptAllocations.Where(x => x.SourcePurchaseContractId == scope.Contract.Id).ToListAsync());
        Assert.False(await db.JournalEntries.AnyAsync(x => x.CompanyId == scope.Company.Id && x.SourceEntityType == nameof(LoadingReceipt)));
        Assert.False(await db.InventoryAverageCosts.AnyAsync(x => x.CompanyId == scope.Company.Id && x.ProductId == scope.Product.Id));
        Assert.False(await db.ProcessedFormTokens.AnyAsync(x => x.Token == token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentSeventyTonneRequests_CannotConsumeMoreThanHundred(bool mixSingleAndBulk)
    {
        await using var seed = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(seed);
        var loading = (await AddLoadings(seed, scope, 1))[0];
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<IActionResult> Request(bool single)
        {
            await using var db = fixture.CreateDbContext();
            var controller = Controller(db, null);
            await start.Task;
            return single ? await controller.Create(Single(scope, loading.Id, 70m), Guid.NewGuid().ToString("N"))
                : await controller.BulkCreate(Bulk(scope, [loading.Id], 70m), Guid.NewGuid().ToString("N"));
        }
        var first = Request(mixSingleAndBulk); var second = Request(false); start.SetResult();
        await Task.WhenAll(first, second);
        var receipts = await seed.LoadingReceipts.AsNoTracking().Where(x => x.LoadingRegisterId == loading.Id && !x.IsCancelled).ToListAsync();
        Assert.Single(receipts);
        Assert.Equal(70m, receipts.Sum(x => x.ReceivedQuantityMt));
        Assert.Equal(30m, loading.LoadedQuantityMt - receipts.Sum(x => x.ReceivedQuantityMt));
        Assert.Equal(70m, await seed.InventoryMovements.Where(x => x.ProductId == scope.Product.Id).SumAsync(x => x.QuantityMt));
    }

    private static async Task<List<LoadingRegister>> AddLoadings(ApplicationDbContext db, PaymentAccountingAdapterTests.PaymentScope scope, int count, decimal? price = 500m)
    {
        var rows = Enumerable.Range(0, count).Select(_ => new LoadingRegister
        {
            ContractId = scope.Contract.Id, ProductId = scope.Product.Id, LoadedQuantityMt = 100m,
            LoadingDate = new DateTime(2026, 7, 5), LoadingPriceUsd = price,
            TransportType = LoadingTransportType.Unspecified, SettlementCurrencyCode = "USD"
        }).ToList();
        db.LoadingRegisters.AddRange(rows); await db.SaveChangesAsync(); return rows;
    }
    private static LoadingReceiptCreateViewModel Single(PaymentAccountingAdapterTests.PaymentScope scope, int id, decimal quantity) => new()
    {
        LoadingRegisterId = id, TerminalId = scope.Terminal.Id, StorageTankId = scope.Tank.Id,
        ReceivedQuantityMt = quantity, ReceiptDate = new DateTime(2026, 7, 9), LossMode = ReceiptLossMode.ImmediateKnownLoss,
        ReturnUrl = "/ContractJourney/Details?contractId=" + scope.Contract.Id
    };
    private static LoadingReceiptBulkCreateViewModel Bulk(PaymentAccountingAdapterTests.PaymentScope scope, List<int> ids, decimal quantity) => new()
    {
        ContractId = scope.Contract.Id, LoadingRegisterIds = ids, TerminalId = scope.Terminal.Id, StorageTankId = scope.Tank.Id,
        TotalReceivedQuantityMt = quantity, ReceiptDate = new DateTime(2026, 7, 9), LossMode = BulkReceiptLossMode.None,
        ReturnUrl = "/ContractJourney/Details?contractId=" + scope.Contract.Id
    };
    private static PurchaseAccountingAdapter Adapter(ApplicationDbContext db, bool enabled = true)
    {
        var options = Options.Create(new AccountingOptions { Enabled = enabled, Pilots = new AccountingPilotOptions { Purchase = true, InventoryReceipt = true } });
        return new PurchaseAccountingAdapter(db,
            new AccountingPostingService(db, new PeriodGuard(db, new FiscalCalendarService(db)), options, new SystemCompanyProvider(db)),
            new AccountingJournalNumberGenerator(), new PricingService(db), new InventoryValuationService(db), options, NullLogger<PurchaseAccountingAdapter>.Instance);
    }
    private static LoadingReceiptsController Controller(ApplicationDbContext db, IPurchaseAccountingAdapter? adapter)
    {
        var http = new DefaultHttpContext(); http.Request.Headers["X-Requested-With"] = "XMLHttpRequest";
        return new LoadingReceiptsController(db, new AuditService(db), NullLogger<LoadingReceiptsController>.Instance, purchaseAccounting: adapter)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
            TempData = new TempDataDictionary(http, new EmptyTempData()),
            Url = new UrlHelper(new ActionContext(http, new RouteData(), new ActionDescriptor()))
        };
    }
    private sealed class EmptyTempData : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }
    private sealed class FailSecondReceiptAdapter(IPurchaseAccountingAdapter inner) : IPurchaseAccountingAdapter
    {
        private int _calls;
        public Task<PurchaseAccountingResult> TryPostInventoryReceiptAsync(LoadingReceipt receipt, CancellationToken cancellationToken = default)
            => ++_calls == 2 ? throw new InvalidOperationException("Injected failure after first journal and valuation") : inner.TryPostInventoryReceiptAsync(receipt, cancellationToken);
        public Task<PurchaseAccountingResult> TryPostPurchaseAsync(LoadingRegister x, CancellationToken c = default) => inner.TryPostPurchaseAsync(x, c);
        public Task<IReadOnlyList<PurchaseAccountingResult>> TryPostPurchasesAsync(IReadOnlyList<LoadingRegister> x, CancellationToken c = default) => inner.TryPostPurchasesAsync(x, c);
        public Task<PurchaseAccountingResult> TryPostTransportReceiptAsync(InventoryTransportReceipt x, CancellationToken c = default) => inner.TryPostTransportReceiptAsync(x, c);
        public Task<PurchaseAccountingResult> TryPostPurchaseReversalAsync(LoadingRegister x, CancellationToken c = default) => inner.TryPostPurchaseReversalAsync(x, c);
        public Task<PurchaseAccountingResult> TryPostInventoryReceiptReversalAsync(LoadingReceipt x, CancellationToken c = default) => inner.TryPostInventoryReceiptReversalAsync(x, c);
    }
}
