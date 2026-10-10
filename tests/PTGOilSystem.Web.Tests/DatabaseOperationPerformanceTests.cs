using System.Diagnostics;
using System.Text.Json;
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
using Xunit.Abstractions;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// Measures persisted controller operations, including audit and enabled accounting.
/// Setup and verification queries are excluded; timing has no machine-dependent pass threshold.
/// The collection creates a guarded, disposable PostgreSQL database and prevents parallel runs.
/// </summary>
[Collection(BulkFromLoadingPerformanceCollection.CollectionName)]
[Trait("Category", "Performance")]
public sealed class DatabaseOperationPerformanceTests(
    BulkFromLoadingPerformanceFixture fixture,
    ITestOutputHelper output)
{
    private const int RowCount = 1000;
    private static readonly DateTime OperationDate = new(2026, 7, 22);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Import_Thousand_Loadings_Preserves_Audit_And_Purchase_Journals(bool accountingEnabled)
    {
        Assert.True(fixture.Available, fixture.UnavailableReason);
        await fixture.TruncateAsync();
        var counter = new BulkFromLoadingPerformanceTests.CommandCounter();
        await using var db = fixture.CreateDbContext(counter);
        var scope = await CreateScopeAsync(db);
        var context = new DefaultHttpContext();
        var controller = new LoadingController(
            db,
            new AuditService(db),
            NullLogger<LoadingController>.Instance,
            purchaseAccounting: CreateAdapter(db, accountingEnabled))
        {
            ControllerContext = new ControllerContext { HttpContext = context },
            Url = new UrlHelper(new ActionContext(context, new RouteData(), new ActionDescriptor())),
            TempData = new TempDataDictionary(context, new EmptyTempDataProvider())
        };
        var rows = Enumerable.Range(1, RowCount).Select(index => new LoadingCreateRowViewModel
        {
            RowKey = $"perf_{index}",
            ContractId = scope.Contract.Id,
            LoadingDate = OperationDate,
            BillOfLadingNumber = $"PERF-RWB-{index:D4}",
            WagonNumber = $"PERF-WG-{index:D4}",
            LoadedQuantityMt = 1m,
            LoadingPriceUsd = 500m
        }).ToList();
        var model = new LoadingCreateViewModel
        {
            ContractId = scope.Contract.Id,
            ProductId = scope.Product.Id,
            TransportType = LoadingTransportType.Wagon,
            Rows = [rows[0]],
            ImportedRowsJson = JsonSerializer.Serialize(rows, new JsonSerializerOptions(JsonSerializerDefaults.Web))
        };

        var result = await MeasureAsync(db, counter, "import", accountingEnabled, () => controller.Create(model));
        AssertSuccessful(controller, result);
        Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(RowCount, await db.LoadingRegisters.CountAsync());
        Assert.Equal(RowCount, await db.AuditLogs.CountAsync(a => a.EntityName == nameof(LoadingRegister)));
        Assert.Equal((decimal)RowCount, await db.LoadingRegisters.SumAsync(a => a.LoadedQuantityMt));
        Assert.Equal(0, await db.InventoryMovements.CountAsync());
        Assert.Equal(0, await db.LoadingReceipts.CountAsync());
        await AssertJournalsAsync(db, PurchaseAccountingAdapter.PurchaseSourceEntityType, accountingEnabled);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Receive_Thousand_Loadings_Preserves_Lineage_Valuation_And_Journals(bool accountingEnabled)
    {
        Assert.True(fixture.Available, fixture.UnavailableReason);
        await fixture.TruncateAsync();
        var counter = new BulkFromLoadingPerformanceTests.CommandCounter();
        await using var db = fixture.CreateDbContext(counter);
        var scope = await CreateScopeAsync(db);
        var loadings = Enumerable.Range(1, RowCount).Select(index => new LoadingRegister
        {
            ContractId = scope.Contract.Id,
            ProductId = scope.Product.Id,
            TransportType = LoadingTransportType.Wagon,
            LoadingDate = OperationDate,
            LoadedQuantityMt = 1m,
            LoadingPriceUsd = 500m,
            SettlementCurrencyCode = "USD",
            WagonNumber = $"PERF-WG-{index:D4}"
        }).ToList();
        db.LoadingRegisters.AddRange(loadings);
        await db.SaveChangesAsync();
        var context = new DefaultHttpContext();
        var controller = new LoadingReceiptsController(
            db,
            new AuditService(db),
            NullLogger<LoadingReceiptsController>.Instance,
            purchaseAccounting: CreateAdapter(db, accountingEnabled))
        {
            ControllerContext = new ControllerContext { HttpContext = context },
            Url = new UrlHelper(new ActionContext(context, new RouteData(), new ActionDescriptor())),
            TempData = new TempDataDictionary(context, new EmptyTempDataProvider())
        };
        var model = new LoadingReceiptBulkCreateViewModel
        {
            ContractId = scope.Contract.Id,
            LoadingRegisterIds = loadings.Select(loading => loading.Id).ToList(),
            ReceiptDate = OperationDate,
            TerminalId = scope.Terminal.Id,
            StorageTankId = scope.Tank.Id,
            TotalReceivedQuantityMt = RowCount,
            ReferenceDocument = "PERF-RECEIPT",
            ReturnUrl = $"/ContractJourney/Details?contractId={scope.Contract.Id}&tab=receipts"
        };

        var result = await MeasureAsync(db, counter, "bulk_receipt", accountingEnabled, () => controller.BulkCreate(model));
        AssertSuccessful(controller, result);
        Assert.IsType<RedirectResult>(result);
        Assert.Equal(RowCount, await db.LoadingReceipts.CountAsync());
        Assert.Equal(RowCount, await db.LoadingReceiptAllocations.CountAsync());
        Assert.Equal(RowCount, await db.InventoryMovements.CountAsync());
        Assert.Equal(RowCount, await db.AuditLogs.CountAsync(a => a.EntityName == nameof(LoadingReceipt)));
        Assert.Equal((decimal)RowCount, await db.InventoryMovements.SumAsync(m => m.QuantityMt));
        Assert.Equal(RowCount, await db.LoadingReceipts.Select(r => r.LoadingRegisterId).Distinct().CountAsync());
        Assert.Equal(0, await db.LoadingReceiptAllocations.CountAsync(a => a.SourcePurchaseContractId != scope.Contract.Id));
        await AssertJournalsAsync(db, PurchaseAccountingAdapter.ReceiptSourceEntityType, accountingEnabled);
        if (accountingEnabled)
        {
            var valuation = await db.InventoryAverageCosts.SingleAsync(v =>
                v.CompanyId == scope.Company.Id && v.ProductId == scope.Product.Id && v.TerminalId == scope.Terminal.Id);
            Assert.Equal(RowCount, valuation.QuantityMt);
            Assert.Equal(RowCount * 500m, valuation.TotalValueUsd);
        }
        else
        {
            Assert.Empty(await db.InventoryAverageCosts.ToListAsync());
        }
    }

    [Fact]
    public async Task Stock_Summary_Aggregates_Thousand_Movements_Without_Writing_Data()
    {
        Assert.True(fixture.Available, fixture.UnavailableReason);
        await fixture.TruncateAsync();
        var counter = new BulkFromLoadingPerformanceTests.CommandCounter();
        await using var db = fixture.CreateDbContext(counter);
        var scope = await CreateScopeAsync(db);
        db.InventoryMovements.AddRange(Enumerable.Range(1, RowCount).Select(index => new InventoryMovement
        {
            ProductId = scope.Product.Id,
            ContractId = scope.Contract.Id,
            TerminalId = scope.Terminal.Id,
            StorageTankId = scope.Tank.Id,
            Direction = index <= 700 ? MovementDirection.In : MovementDirection.Out,
            QuantityMt = 1m,
            MovementDate = DateTime.SpecifyKind(OperationDate.AddDays(index <= 700 ? 0 : 1), DateTimeKind.Utc),
            ReferenceDocument = $"PERF-STOCK-{index}"
        }));
        await db.SaveChangesAsync();

        var rows = await MeasureAsync(db, counter, "stock_summary", false,
            () => new StockService(db).GetStockSummaryAsync(contractId: scope.Contract.Id));
        var row = Assert.Single(rows);
        Assert.Equal(400m, row.FreeQuantityMt);
        Assert.Equal(RowCount, row.MovementCount);
        Assert.Equal(scope.Contract.Id, row.ContractId);
        var historical = Assert.Single(await new StockService(db).GetStockSummaryAsync(
            contractId: scope.Contract.Id, asOfUtc: OperationDate.AddDays(1).AddTicks(-1)));
        Assert.Equal(700m, historical.FreeQuantityMt);
        Assert.Equal(700, historical.MovementCount);
        Assert.Equal(RowCount, await db.InventoryMovements.CountAsync());
        Assert.Empty(await db.JournalEntries.ToListAsync());
    }

    [Fact]
    public async Task Thousand_Row_Conversion_Request_Retry_Preserves_Quantity_And_Reports_Success()
    {
        Assert.True(fixture.Available, fixture.UnavailableReason);
        await fixture.TruncateAsync();
        var counter = new BulkFromLoadingPerformanceTests.CommandCounter();
        await using var db = fixture.CreateDbContext(counter);
        await BulkFromLoadingPerformanceTests.SeedAsync(db, RowCount);
        var workflow = BulkFromLoadingPerformanceTests.BuildWorkflow(db);
        var command = new BulkStartTransportFromLoadingCommand
        {
            Rows = Enumerable.Range(1, RowCount).Select(id => new BulkStartTransportFromLoadingRow
            {
                LoadingRegisterId = id,
                QuantityMt = 100m,
                TransportType = LoadingTransportType.Truck,
                TruckId = 1,
                Reference = $"PERF-CONVERT-{id}"
            }).ToList(),
            TransportDate = new DateTime(2026, 9, 5),
            FormToken = Guid.NewGuid().ToString("N")
        };

        var result = await MeasureAsync(db, counter, "convert_loading", false,
            () => workflow.StartManyFromLoadingAsync(command));
        Assert.Empty(result.Failures);
        Assert.Equal(RowCount, result.CreatedCount);
        Assert.Equal(RowCount, await db.InventoryTransportLegs.CountAsync());
        Assert.Equal(100_000m, await db.InventoryTransportLegAllocations.SumAsync(a => a.QuantityMt));
        Assert.Equal(RowCount, await db.InventoryTransportLegAllocations
            .Select(a => a.SourceLoadingRegisterId).Distinct().CountAsync());
        Assert.True(await db.ProcessedFormTokens.AnyAsync());
        Assert.Empty(await db.InventoryMovements.ToListAsync());
        Assert.Empty(await db.LoadingReceipts.ToListAsync());

        var replay = await MeasureAsync(db, counter, "convert_retry", false,
            () => workflow.StartManyFromLoadingAsync(command));
        output.WriteLine($"CONVERSION_RETRY created={replay.CreatedCount} failures={replay.Failures.Count}");
        Assert.Equal(RowCount, await db.InventoryTransportLegs.CountAsync());
        Assert.Equal(100_000m, await db.InventoryTransportLegAllocations.SumAsync(a => a.QuantityMt));
        Assert.Empty(replay.Failures);
        Assert.Equal(0, replay.CreatedCount);
    }

    private async Task<T> MeasureAsync<T>(
        ApplicationDbContext db,
        BulkFromLoadingPerformanceTests.CommandCounter counter,
        string operation,
        bool accountingEnabled,
        Func<Task<T>> action)
    {
        db.ChangeTracker.Clear();
        var saves = 0;
        var peakTracked = 0;
        EventHandler<SavingChangesEventArgs> onSave = (_, _) =>
        {
            saves++;
            // Enumerating Entries otherwise adds another DetectChanges pass just for
            // measurement. Restore the flag before SaveChanges performs its own work.
            var detectChanges = db.ChangeTracker.AutoDetectChangesEnabled;
            try
            {
                db.ChangeTracker.AutoDetectChangesEnabled = false;
                peakTracked = Math.Max(peakTracked, db.ChangeTracker.Entries().Count());
            }
            finally
            {
                db.ChangeTracker.AutoDetectChangesEnabled = detectChanges;
            }
        };
        db.SavingChanges += onSave;
        counter.Reset();
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            return await action();
        }
        finally
        {
            stopwatch.Stop();
            db.SavingChanges -= onSave;
            var allocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
            output.WriteLine($"DATABASE_PERF operation={operation} rows={RowCount} accounting={accountingEnabled} " +
                $"ms={stopwatch.ElapsedMilliseconds} commands={counter.Commands} transactions={counter.Transactions} " +
                $"saveChanges={saves} peakTracked={peakTracked} allocatedBytes={allocatedBytes}");
        }
    }

    private static void AssertSuccessful(Controller controller, IActionResult result)
    {
        Assert.True(controller.ModelState.IsValid,
            string.Join("; ", controller.ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage)));
    }

    private static async Task AssertJournalsAsync(ApplicationDbContext db, string sourceType, bool accountingEnabled)
    {
        var journals = await db.JournalEntries.AsNoTracking().Include(j => j.Lines)
            .Where(j => j.SourceModule == PurchaseAccountingAdapter.SourceModule && j.SourceEntityType == sourceType)
            .ToListAsync();
        Assert.Equal(accountingEnabled ? RowCount : 0, journals.Count);
        Assert.Equal(journals.Count, journals.Select(j => j.SourceEventId).Distinct().Count());
        Assert.All(journals, journal =>
        {
            Assert.False(string.IsNullOrWhiteSpace(journal.SourceEventId));
            Assert.False(journal.IsReversal);
            Assert.Equal(2, journal.Lines.Count);
            Assert.Equal(500m, journal.Lines.Sum(line => line.Debit));
            Assert.Equal(500m, journal.Lines.Sum(line => line.Credit));
        });
    }

    private static async Task<PaymentAccountingAdapterTests.PaymentScope> CreateScopeAsync(ApplicationDbContext db)
    {
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        scope.Contract.QuantityMt = RowCount;
        scope.Tank.ProductId = scope.Product.Id;
        await db.SaveChangesAsync();
        return scope;
    }

    private static PurchaseAccountingAdapter CreateAdapter(ApplicationDbContext db, bool enabled)
    {
        var options = Options.Create(new AccountingOptions
        {
            Enabled = enabled,
            Pilots = new AccountingPilotOptions { Purchase = true, InventoryReceipt = true }
        });
        return new PurchaseAccountingAdapter(
            db,
            new AccountingPostingService(db, new PeriodGuard(db, new FiscalCalendarService(db)), options, new SystemCompanyProvider(db)),
            new AccountingJournalNumberGenerator(),
            new PricingService(db),
            new InventoryValuationService(db),
            options,
            NullLogger<PurchaseAccountingAdapter>.Instance);
    }

    private sealed class EmptyTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object?> LoadTempData(HttpContext context) => new Dictionary<string, object?>();
        public void SaveTempData(HttpContext context, IDictionary<string, object?> values) { }
    }
}
