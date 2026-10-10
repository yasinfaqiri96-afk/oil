using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PTGOilSystem.Web.Configuration;
using PTGOilSystem.Web.Controllers;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.Expenses;
using PTGOilSystem.Web.Models.Loading;
using PTGOilSystem.Web.Models.Sales;
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
    // PTG_PERF_ROWS only scales a local measurement run; the default is the benchmark size.
    private static readonly int RowCount =
        int.TryParse(Environment.GetEnvironmentVariable("PTG_PERF_ROWS"), out var rows) && rows > 0 ? rows : 1000;
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
        await PrintDocumentCountsAsync(db, "import");
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
        // A receipt journal requires the posted purchase that put the goods in transit.
        // This setup posting is excluded from the operation measurement.
        await CreateAdapter(db, accountingEnabled).TryPostPurchasesAsync(loadings);
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
        await PrintDocumentCountsAsync(db, "bulk_receipt");
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

        var replaySaves = 0;
        db.SavingChanges += (_, _) => replaySaves++;
        var replay = await MeasureAsync(db, counter, "convert_retry", false,
            () => workflow.StartManyFromLoadingAsync(command));
        output.WriteLine($"CONVERSION_RETRY created={replay.CreatedCount} failures={replay.Failures.Count}");
        Assert.Equal(RowCount, await db.InventoryTransportLegs.CountAsync());
        Assert.Equal(100_000m, await db.InventoryTransportLegAllocations.SumAsync(a => a.QuantityMt));
        Assert.Empty(replay.Failures);
        Assert.Equal(0, replay.CreatedCount);
        Assert.Equal(0, replaySaves);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sell_Thousand_Loadings_Directly_Preserves_Receivables_Cost_And_Lineage(bool accountingEnabled)
    {
        Assert.True(fixture.Available, fixture.UnavailableReason);
        await fixture.TruncateAsync();
        var counter = new BulkFromLoadingPerformanceTests.CommandCounter();
        await using var db = fixture.CreateDbContext(counter);
        var scope = await CreateScopeAsync(db);
        var loadings = await SeedPricedLoadingsAsync(db, scope);
        // Direct COGS must use the purchase revision posted before the sale.
        // This setup posting is excluded from the operation measurement.
        await CreateAdapter(db, accountingEnabled).TryPostPurchasesAsync(loadings);
        var context = new DefaultHttpContext();
        var controller = new SalesController(db, new StockService(db),
            new CurrencyConversionService(new PricingService(db)), new AuditService(db),
            NullLogger<SalesController>.Instance, salesAccounting: CreateSalesAdapter(db, accountingEnabled))
        {
            ControllerContext = new ControllerContext { HttpContext = context },
            TempData = new TempDataDictionary(context, new EmptyTempDataProvider())
        };
        var model = new GroupSaleCreateViewModel
        {
            CustomerId = scope.Customer.Id,
            Currency = "USD",
            SaleDate = OperationDate,
            UnitPriceInCurrency = 600m,
            LoadingSaleTerminalId = scope.Terminal.Id,
            Items = loadings.Select(loading => new GroupSaleSelectedInput
            {
                Kind = GroupSaleSourceKind.LoadingRegister,
                Id = loading.Id,
                QuantityMt = 1m
            }).ToList()
        };

        var result = await MeasureAsync(db, counter, "group_loading_sale", accountingEnabled,
            () => controller.CreateGroup(model, Guid.NewGuid().ToString("N")));
        await PrintDocumentCountsAsync(db, "group_loading_sale");
        AssertSuccessful(controller, result);
        Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(RowCount, await db.SalesTransactions.CountAsync());
        Assert.Equal(RowCount * 600m, await db.SalesTransactions.SumAsync(s => s.TotalUsd));
        Assert.Equal(RowCount, await db.LoadingReceipts.CountAsync());
        Assert.Equal(RowCount, await db.LoadingReceiptAllocations.CountAsync());
        Assert.Equal(RowCount, await db.LoadingReceiptAllocations.Select(a => a.SalesTransactionId).Distinct().CountAsync());
        Assert.Equal(RowCount, await db.LoadingReceipts.Select(r => r.LoadingRegisterId).Distinct().CountAsync());
        Assert.Equal(0, await db.LoadingReceiptAllocations.CountAsync(a => a.SourcePurchaseContractId != scope.Contract.Id));
        Assert.Equal(RowCount, await db.LedgerEntries.CountAsync(l => l.SourceType == "Sale"));
        Assert.Empty(await db.InventoryMovements.ToListAsync());
        Assert.Empty(await db.InventoryTransportLegs.ToListAsync());
        Assert.Empty(await db.InventoryAverageCosts.ToListAsync());

        var journals = await db.JournalEntries.AsNoTracking().Include(j => j.Lines)
            .Where(j => j.SourceModule == SalesAccountingAdapter.SourceModule).ToListAsync();
        Assert.Equal(accountingEnabled ? RowCount * 2 : 0, journals.Count);
        Assert.Equal(journals.Count, journals.Select(j => j.SourceEventId).Distinct().Count());
        Assert.All(journals, journal => Assert.Equal(journal.Lines.Sum(l => l.Debit), journal.Lines.Sum(l => l.Credit)));
        if (accountingEnabled)
        {
            Assert.Equal(RowCount * 600m, journals.SelectMany(j => j.Lines)
                .Where(l => l.AccountId == scope.Settings.AccountsReceivableAccountId).Sum(l => l.Debit));
            Assert.Equal(RowCount * 500m, journals.SelectMany(j => j.Lines)
                .Where(l => l.AccountId == scope.Settings.CostOfGoodsSoldAccountId).Sum(l => l.Debit));
            Assert.Equal(RowCount, journals.Count(j => j.SourceEventId == SalesAccountingAdapter.BuildCreatedSourceEventId(j.SourceEntityId!.Value)));
            Assert.Equal(RowCount, journals.Count(j => j.SourceEventId == SalesAccountingAdapter.BuildCogsSourceEventId(j.SourceEntityId!.Value)));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Expense_Thousand_Loadings_Preserves_Payable_Source_And_Accounting(bool accountingEnabled)
    {
        Assert.True(fixture.Available, fixture.UnavailableReason);
        await fixture.TruncateAsync();
        var counter = new BulkFromLoadingPerformanceTests.CommandCounter();
        await using var db = fixture.CreateDbContext(counter);
        var scope = await CreateScopeAsync(db);
        var loadings = await SeedPricedLoadingsAsync(db, scope);
        var type = new ExpenseType
        {
            Code = PaymentAccountingAdapterTests.Unique("PERF-ET"),
            Name = "مصرف آزمایشی",
            Category = "Other",
            IsActive = true,
            PayableAccountKind = ExpensePayableKind.AccruedExpense
        };
        db.ExpenseTypes.Add(type);
        await db.SaveChangesAsync();
        var context = new DefaultHttpContext();
        var controller = new ExpensesController(db, new CurrencyConversionService(new PricingService(db)),
            new AuditService(db), NullLogger<ExpensesController>.Instance, CreateExpenseAdapter(db, accountingEnabled))
        {
            ControllerContext = new ControllerContext { HttpContext = context },
            TempData = new TempDataDictionary(context, new EmptyTempDataProvider())
        };
        var model = new GroupExpenseCreateViewModel
        {
            ExpenseDate = OperationDate,
            Currency = "USD",
            SettlementMode = ExpenseSettlementMode.Payable,
            Lines = [new GroupExpenseLineInput
            {
                ExpenseTypeId = type.Id,
                ServiceProviderId = scope.ServiceProvider.Id,
                AllocationMethod = ExpenseAllocationMethod.ByQuantity,
                RatePerTon = 1m
            }],
            Items = loadings.Select(loading => new GroupExpenseSelectedInput { Kind = "Loading", Id = loading.Id }).ToList()
        };

        var result = await MeasureAsync(db, counter, "group_loading_expense", accountingEnabled,
            () => controller.CreateGroup(model, Guid.NewGuid().ToString("N")));
        await PrintDocumentCountsAsync(db, "group_loading_expense");
        AssertSuccessful(controller, result);
        Assert.IsType<RedirectToActionResult>(result);
        var expenses = await db.ExpenseTransactions.AsNoTracking().ToListAsync();
        Assert.Equal(RowCount, expenses.Count);
        Assert.Equal(RowCount, expenses.Select(e => e.LoadingRegisterId).Distinct().Count());
        Assert.Single(expenses.Select(e => e.ExpenseBatchId).Distinct());
        Assert.Equal(RowCount, expenses.Sum(e => e.AmountUsd));
        Assert.All(expenses, expense =>
        {
            Assert.Equal(scope.Contract.Id, expense.ContractId);
            Assert.Equal(scope.ServiceProvider.Id, expense.ServiceProviderId);
            Assert.Equal(ExpenseSettlementMode.Payable, expense.SettlementMode);
            Assert.NotNull(expense.ExpenseBatchId);
            Assert.Null(expense.TransportLegId);
            Assert.Null(expense.TruckDispatchId);
            Assert.False(expense.IsCancelled);
        });
        Assert.Equal(RowCount, await db.AuditLogs.CountAsync(a => a.EntityName == nameof(ExpenseTransaction)));
        Assert.Equal(RowCount, await db.LedgerEntries.CountAsync(l => l.SourceType == "Expense"));
        Assert.Empty(await db.PaymentTransactions.ToListAsync());
        Assert.Empty(await db.InventoryMovements.ToListAsync());
        Assert.Empty(await db.LoadingReceipts.ToListAsync());
        var journals = await db.JournalEntries.AsNoTracking().Include(j => j.Lines)
            .Where(j => j.SourceModule == ExpenseAccountingAdapter.SourceModule).ToListAsync();
        Assert.Equal(accountingEnabled ? RowCount : 0, journals.Count);
        Assert.Equal(journals.Count, journals.Select(j => j.SourceEventId).Distinct().Count());
        Assert.All(journals, journal =>
        {
            Assert.Equal(1m, journal.Lines.Sum(l => l.Debit));
            Assert.Equal(1m, journal.Lines.Sum(l => l.Credit));
            Assert.Equal(scope.Settings.AccruedExpenseAccountId, journal.Lines.Single(l => l.Credit > 0m).AccountId);
        });
    }

    private async Task PrintDocumentCountsAsync(ApplicationDbContext db, string operation)
    {
        output.WriteLine($"DOCUMENTS operation={operation} loadings={await db.LoadingRegisters.CountAsync()} " +
            $"receipts={await db.LoadingReceipts.CountAsync()} allocations={await db.LoadingReceiptAllocations.CountAsync()} " +
            $"sales={await db.SalesTransactions.CountAsync()} expenses={await db.ExpenseTransactions.CountAsync()} " +
            $"journals={await db.JournalEntries.CountAsync()} audit={await db.AuditLogs.CountAsync()} " +
            $"movements={await db.InventoryMovements.CountAsync()} tokens={await db.ProcessedFormTokens.CountAsync()}");
    }

    private static async Task<List<LoadingRegister>> SeedPricedLoadingsAsync(
        ApplicationDbContext db, PaymentAccountingAdapterTests.PaymentScope scope)
    {
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
        return loadings;
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
        // Full change-detection passes are the per-save cost that grows with tracked entities.
        var detectPasses = 0;
        var detectWatch = new Stopwatch();
        EventHandler<DetectChangesEventArgs> onDetecting = (_, _) => { detectPasses++; detectWatch.Start(); };
        EventHandler<DetectedChangesEventArgs> onDetected = (_, _) => detectWatch.Stop();
        db.SavingChanges += onSave;
        db.ChangeTracker.DetectingAllChanges += onDetecting;
        db.ChangeTracker.DetectedAllChanges += onDetected;
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
            db.ChangeTracker.DetectingAllChanges -= onDetecting;
            db.ChangeTracker.DetectedAllChanges -= onDetected;
            var allocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
            output.WriteLine($"DATABASE_PERF operation={operation} rows={RowCount} accounting={accountingEnabled} " +
                $"ms={stopwatch.ElapsedMilliseconds} commands={counter.Commands} transactions={counter.Transactions} " +
                $"saveChanges={saves} peakTracked={peakTracked} detectChangesPasses={detectPasses} " +
                $"detectChangesMs={detectWatch.ElapsedMilliseconds} allocatedBytes={allocatedBytes}");
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

    private static SalesAccountingAdapter CreateSalesAdapter(ApplicationDbContext db, bool enabled)
    {
        var options = Options.Create(new AccountingOptions
        {
            Enabled = enabled,
            Pilots = new AccountingPilotOptions { Sale = true, Cogs = true }
        });
        return new SalesAccountingAdapter(db,
            new AccountingPostingService(db, new PeriodGuard(db, new FiscalCalendarService(db)), options, new SystemCompanyProvider(db)),
            new AccountingJournalNumberGenerator(), new InventoryValuationService(db), options,
            NullLogger<SalesAccountingAdapter>.Instance);
    }

    private static ExpenseAccountingAdapter CreateExpenseAdapter(ApplicationDbContext db, bool enabled)
    {
        var options = Options.Create(new AccountingOptions
        {
            Enabled = enabled,
            Pilots = new AccountingPilotOptions { Expense = true }
        });
        return new ExpenseAccountingAdapter(db,
            new AccountingPostingService(db, new PeriodGuard(db, new FiscalCalendarService(db)), options, new SystemCompanyProvider(db)),
            new AccountingJournalNumberGenerator(), options, NullLogger<ExpenseAccountingAdapter>.Instance);
    }

    private sealed class EmptyTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object?> LoadTempData(HttpContext context) => new Dictionary<string, object?>();
        public void SaveTempData(HttpContext context, IDictionary<string, object?> values) { }
    }
}
