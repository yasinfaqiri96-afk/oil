using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using PTGOilSystem.Web.Controllers;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.Loading;
using PTGOilSystem.Web.Services;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// Regression cover for the batched ImmediateKnownLoss path of bulk loading receipts.
///
/// The business outcome must match the previous per-row implementation exactly, while the
/// number of SaveChanges round trips stays flat as the row count grows.
///
/// Note on measurement: these tests run on the EF in-memory provider, so a DbCommandInterceptor
/// would observe nothing (no SQL is issued). SaveChanges interceptors are provider independent,
/// so the flat-cost property is asserted on SaveChanges count, which is the metric the batching
/// actually changed.
/// </summary>
public class BulkReceiptLossBatchingTests
{
    private sealed class SaveChangesCountingInterceptor : SaveChangesInterceptor
    {
        public int Count { get; private set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Count++;
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    [Fact]
    public async Task BulkCreate_ImmediateLoss_Across_Thirty_Loadings_Produces_Exact_Receipts_And_LossEvents()
    {
        var counter = new SaveChangesCountingInterceptor();
        await using var db = BuildContext(counter);
        SeedLoadings(db, loadingCount: 30, loadedQuantityMtEach: 100m);
        await db.SaveChangesAsync();

        var result = await BuildController(db).BulkCreate(BuildModel(
            loadingIds: Enumerable.Range(1, 30).ToList(),
            totalReceivedQuantityMt: 2700m,
            totalLossQuantityMt: 300m,
            totalLossToleranceQuantityMt: 60m));

        Assert.IsType<RedirectResult>(result);

        var receipts = await db.LoadingReceipts.AsNoTracking().OrderBy(r => r.LoadingRegisterId).ToListAsync();
        Assert.Equal(30, receipts.Count);
        Assert.Equal(2700m, receipts.Sum(r => r.ReceivedQuantityMt));
        Assert.All(receipts, r => Assert.Equal(90m, r.ReceivedQuantityMt));
        Assert.All(receipts, r => Assert.Equal(ReceiptLossMode.ImmediateKnownLoss, r.LossMode));

        var losses = await db.LossEvents.AsNoTracking().OrderBy(l => l.LoadingRegisterId).ToListAsync();
        Assert.Equal(30, losses.Count);
        Assert.Equal(300m, losses.Sum(l => l.DifferenceQuantityMt));
        Assert.Equal(60m, losses.Sum(l => l.AllowableLossMt));
        Assert.Equal(240m, losses.Sum(l => l.ChargeableLossMt));
        Assert.All(losses, l =>
        {
            Assert.Equal(LossEventStage.ReceiptShortage, l.Stage);
            Assert.Equal(100m, l.ExpectedQuantityMt);
            Assert.Equal(90m, l.ActualQuantityMt);
            Assert.Equal(10m, l.DifferenceQuantityMt);
            Assert.Equal(2m, l.AllowableLossMt);
            Assert.Equal(8m, l.ChargeableLossMt);
            // A receipt shortage is recognised through the shortage charge, never by moving stock.
            Assert.False(l.AffectsInventory);
            Assert.Null(l.InventoryMovementId);
        });

        // Every loss event is tied to the receipt written for the same loading — one each, no duplicates.
        var receiptIdByLoading = receipts.ToDictionary(r => r.LoadingRegisterId, r => r.Id);
        Assert.All(losses, l => Assert.Equal(receiptIdByLoading[l.LoadingRegisterId!.Value], l.LoadingReceiptId));
        Assert.Equal(30, losses.Select(l => l.LoadingRegisterId).Distinct().Count());

        // Inventory movements come from the receipts only: 30 inbound, nothing outbound.
        var movements = await db.InventoryMovements.AsNoTracking().ToListAsync();
        Assert.Equal(30, movements.Count);
        Assert.All(movements, m => Assert.Equal(MovementDirection.In, m.Direction));
        Assert.Equal(2700m, movements.Sum(m => m.QuantityMt));

        // Audit trail: one row per receipt, movement, allocation and loss event.
        var auditCounts = await db.AuditLogs.AsNoTracking()
            .GroupBy(a => a.EntityName)
            .Select(g => new { EntityName = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.EntityName, x => x.Count);
        Assert.Equal(30, auditCounts[nameof(LoadingReceipt)]);
        Assert.Equal(30, auditCounts[nameof(InventoryMovement)]);
        Assert.Equal(30, auditCounts[nameof(LoadingReceiptAllocation)]);
        Assert.Equal(30, auditCounts[nameof(LossEvent)]);
    }

    [Fact]
    public async Task BulkCreate_ImmediateLoss_SaveChanges_Count_Does_Not_Grow_With_Row_Count()
    {
        var fiveRowCount = await CountSaveChangesForImmediateLossAsync(loadingCount: 5);
        var thirtyRowCount = await CountSaveChangesForImmediateLossAsync(loadingCount: 30);

        Assert.Equal(fiveRowCount, thirtyRowCount);

        // Guard the absolute figure too, so a future regression that reintroduces a per-row save
        // fails here even if it happens to scale both sample sizes equally.
        Assert.True(
            thirtyRowCount <= 6,
            $"Expected a flat, small number of SaveChanges for the bulk loss path; observed {thirtyRowCount}.");
    }

    [Fact]
    public async Task BulkCreate_ImmediateLoss_Writes_Nothing_When_Requested_Total_Exceeds_Remaining()
    {
        await using var db = BuildContext(new SaveChangesCountingInterceptor());
        SeedLoadings(db, loadingCount: 30, loadedQuantityMtEach: 100m);
        await db.SaveChangesAsync();

        var result = await BuildController(db).BulkCreate(BuildModel(
            loadingIds: Enumerable.Range(1, 30).ToList(),
            totalReceivedQuantityMt: 2900m,
            totalLossQuantityMt: 300m,
            totalLossToleranceQuantityMt: 60m));

        Assert.IsType<RedirectResult>(result);
        Assert.Equal(0, await db.LoadingReceipts.CountAsync());
        Assert.Equal(0, await db.LossEvents.CountAsync());
        Assert.Equal(0, await db.InventoryMovements.CountAsync());
        Assert.Equal(0, await db.LoadingReceiptAllocations.CountAsync());
    }

    [Fact]
    public async Task BulkCreate_Ajax_Valid_Request_Returns_Json_Success_Without_Redirect()
    {
        await using var db = BuildContext(new SaveChangesCountingInterceptor());
        SeedLoadings(db, loadingCount: 1, loadedQuantityMtEach: 100m);
        await db.SaveChangesAsync();
        var controller = BuildAjaxController(db);

        var result = await controller.BulkCreate(BuildModel(
            loadingIds: [1],
            totalReceivedQuantityMt: 90m,
            totalLossQuantityMt: 10m,
            totalLossToleranceQuantityMt: 2m));

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Contains("success = True", ok.Value?.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Single(await db.LoadingReceipts.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task BulkCreate_Ajax_Validation_Error_Returns_BadRequest_And_Writes_Nothing()
    {
        await using var db = BuildContext(new SaveChangesCountingInterceptor());
        SeedLoadings(db, loadingCount: 1, loadedQuantityMtEach: 100m);
        await db.SaveChangesAsync();
        var controller = BuildAjaxController(db);

        var result = await controller.BulkCreate(BuildModel(
            loadingIds: [1],
            totalReceivedQuantityMt: 100m,
            totalLossQuantityMt: 10m,
            totalLossToleranceQuantityMt: 2m));

        var error = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("success = False", error.Value?.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, await db.LoadingReceipts.CountAsync());
        Assert.Equal(0, await db.InventoryMovements.CountAsync());
    }

    private static async Task<int> CountSaveChangesForImmediateLossAsync(int loadingCount)
    {
        var counter = new SaveChangesCountingInterceptor();
        await using var db = BuildContext(counter);
        SeedLoadings(db, loadingCount, loadedQuantityMtEach: 100m);
        await db.SaveChangesAsync();

        var baseline = counter.Count;

        var result = await BuildController(db).BulkCreate(BuildModel(
            loadingIds: Enumerable.Range(1, loadingCount).ToList(),
            totalReceivedQuantityMt: 90m * loadingCount,
            totalLossQuantityMt: 10m * loadingCount,
            totalLossToleranceQuantityMt: 2m * loadingCount));

        Assert.IsType<RedirectResult>(result);
        Assert.Equal(loadingCount, await db.LossEvents.CountAsync());

        return counter.Count - baseline;
    }

    private static LoadingReceiptBulkCreateViewModel BuildModel(
        List<int> loadingIds,
        decimal totalReceivedQuantityMt,
        decimal totalLossQuantityMt,
        decimal totalLossToleranceQuantityMt)
        => new()
        {
            ContractId = 1,
            LoadingRegisterIds = loadingIds,
            ReceiptDate = new DateTime(2026, 4, 25),
            TerminalId = 1,
            StorageTankId = 1,
            TotalReceivedQuantityMt = totalReceivedQuantityMt,
            LossMode = BulkReceiptLossMode.ImmediateKnownLoss,
            TotalLossQuantityMt = totalLossQuantityMt,
            TotalLossToleranceQuantityMt = totalLossToleranceQuantityMt,
            ReferenceDocument = "BULK-LOSS-BATCH",
            ReturnUrl = "/ContractJourney/Details?contractId=1&tab=receipts"
        };

    private static ApplicationDbContext BuildContext(SaveChangesCountingInterceptor counter)
        => new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .AddInterceptors(counter)
            .Options);

    private static LoadingReceiptsController BuildController(ApplicationDbContext db)
        => new(db, new AuditService(db), NullLogger<LoadingReceiptsController>.Instance)
        {
            TempData = new TempDataDictionary(new DefaultHttpContext(), new NullTempDataProvider()),
            Url = new UrlHelper(new ActionContext(
                new DefaultHttpContext(),
                new RouteData(),
                new ActionDescriptor()))
        };

    private static LoadingReceiptsController BuildAjaxController(ApplicationDbContext db)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["X-Requested-With"] = "XMLHttpRequest";
        var controller = BuildController(db);
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        controller.TempData = new TempDataDictionary(httpContext, new NullTempDataProvider());
        return controller;
    }

    private static void SeedLoadings(ApplicationDbContext db, int loadingCount, decimal loadedQuantityMtEach)
    {
        db.Products.Add(new Product { Id = 1, Code = "GO", Name = "Gas Oil" });
        db.Companies.Add(new Company { Id = 1, Code = "PTG", Name = "Petro Trade Group" });
        db.Contracts.Add(new Contract
        {
            Id = 1,
            ContractNumber = "PUR-1",
            ContractType = ContractType.Purchase,
            ProductId = 1,
            CompanyId = 1,
            ContractDate = new DateTime(2026, 4, 23),
            QuantityMt = loadedQuantityMtEach * loadingCount
        });
        db.Terminals.Add(new Terminal { Id = 1, Code = "TERM-1", Name = "Ilinka Terminal" });
        db.StorageTanks.Add(new StorageTank { Id = 1, TerminalId = 1, TankCode = "TK-01", ProductId = 1, CapacityMt = 80000m });

        for (var i = 1; i <= loadingCount; i++)
        {
            db.LoadingRegisters.Add(new LoadingRegister
            {
                Id = i,
                ContractId = 1,
                ProductId = 1,
                LoadingDate = new DateTime(2026, 4, 23),
                LoadedQuantityMt = loadedQuantityMtEach,
                BillOfLadingNumber = $"RWB-{i:D3}",
                WagonNumber = $"WG-{i:D3}",
                ConsigneeName = "Terminal Ilinka",
                DestinationName = "Novopolotsk"
            });
        }
    }

    private sealed class NullTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object?> LoadTempData(HttpContext context) => new Dictionary<string, object?>();

        public void SaveTempData(HttpContext context, IDictionary<string, object?> values)
        {
        }
    }
}
