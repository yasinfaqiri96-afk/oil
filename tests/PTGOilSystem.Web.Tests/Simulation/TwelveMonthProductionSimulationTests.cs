using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.Reports;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.PartyStatements;
using PTGOilSystem.Web.Services.Reporting;
using Xunit;
using Xunit.Abstractions;

namespace PTGOilSystem.Web.Tests.Simulation;

/// <summary>
/// «۱۲ ماه بهره‌برداری واقعی» روی یک دیتابیس PostgreSQL موقت.
/// هدف: پیدا کردن جایی که پس از یک سال کار روزمره، موجودی یا حساب از هم می‌پاشد.
/// این تست هیچ‌گاه به دیتابیس Production وصل نمی‌شود (نگهبان DatabaseSafetyGuard).
/// </summary>
[Collection(SimulationPostgresCollection.CollectionName)]
public sealed class TwelveMonthProductionSimulationTests
{
    private readonly SimulationPostgresFixture _fixture;
    private readonly ITestOutputHelper _output;

    public TwelveMonthProductionSimulationTests(SimulationPostgresFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [Fact]
    public async Task Twelve_Months_Of_Operation_Keeps_Inventory_And_Ledger_Reconciled()
    {
        Skip.IfNotAvailable(_fixture);

        var log = new SimulationFindingLog();
        var world = new SimulationWorld();

        var stopwatch = Stopwatch.StartNew();
        await using (var db = _fixture.CreateDbContext())
        {
            db.Database.SetCommandTimeout(600);
            await world.SeedMasterDataAsync(db);
            await world.SeedContractsAsync(db);
        }

        await using (var db = _fixture.CreateDbContext())
        {
            db.Database.SetCommandTimeout(600);
            await world.RunTwelveMonthsAsync(db);
        }

        stopwatch.Stop();
        log.Fact($"12-month data generation took {stopwatch.Elapsed.TotalSeconds:N1}s.");

        await using (var db = _fixture.CreateDbContext())
        {
            db.Database.SetCommandTimeout(600);

            await ReportVolumesAsync(db, log);
            await CheckInventoryReconciliationAsync(db, log);
            await CheckNegativeStockTimelineAsync(db, log);
            await CheckLedgerOrphansAsync(db, log);
            await CheckLedgerCoverageAsync(db, log);
            await CheckMonthlyTotalsAsync(db, log);
            await CheckPartnershipConsistencyAsync(db, log);
            await CheckPartyBalanceConsistencyAsync(db, log);
            await CheckIndexCoverageAsync(db, log);
            await MeasureHotPathPerformanceAsync(db, log);
        }

        var path = log.WriteToDisk(
            "simulation-12-month-findings.md",
            "PTG 12-Month Simulation — measured findings");
        _output.WriteLine(log.Render("PTG 12-Month Simulation"));
        _output.WriteLine($"Findings written to: {path}");

        // این تست «گزارش‌گر» است: شکست آن فقط وقتی معنا دارد که هیچ داده‌ای ساخته نشده باشد.
        Assert.NotEmpty(world.Volumes);
    }

    /// <summary>
    /// همان مجموعهٔ گزارش‌ها و اسکنرهای این تست، تا ابزار seed دیتابیس بازرسی
    /// (<c>InspectionDatabaseSeeder</c>) بتواند بدون کپی منطق، دقیقاً همین‌ها را اجرا کند.
    /// </summary>
    internal static async Task RunAllScannersAsync(ApplicationDbContext db, SimulationFindingLog log)
    {
        await ReportVolumesAsync(db, log);
        await CheckInventoryReconciliationAsync(db, log);
        await CheckNegativeStockTimelineAsync(db, log);
        await CheckLedgerOrphansAsync(db, log);
        await CheckLedgerCoverageAsync(db, log);
        await CheckMonthlyTotalsAsync(db, log);
        await CheckPartnershipConsistencyAsync(db, log);
        await CheckPartyBalanceConsistencyAsync(db, log);
        await CheckIndexCoverageAsync(db, log);
    }

    // ------------------------------------------------------------------ volumes

    private static async Task ReportVolumesAsync(ApplicationDbContext db, SimulationFindingLog log)
    {
        log.Fact($"Contracts: {await db.Contracts.CountAsync()}");
        log.Fact($"LoadingRegisters: {await db.LoadingRegisters.CountAsync()}");
        log.Fact($"LoadingReceipts: {await db.LoadingReceipts.CountAsync()}");
        log.Fact($"InventoryMovements: {await db.InventoryMovements.CountAsync()}");
        log.Fact($"SalesTransactions: {await db.SalesTransactions.CountAsync()}");
        log.Fact($"ExpenseTransactions: {await db.ExpenseTransactions.CountAsync()}");
        log.Fact($"PaymentTransactions: {await db.PaymentTransactions.CountAsync()}");
        log.Fact($"TruckDispatches: {await db.TruckDispatches.CountAsync()}");
        log.Fact($"LossEvents: {await db.LossEvents.CountAsync()}");
        log.Fact($"LedgerEntries: {await db.LedgerEntries.CountAsync()}");
    }

    // ------------------------------------------------------- inventory invariant

    /// <summary>
    /// Opening + In + Adjustment − Out − Transfer باید دقیقاً با عددی که
    /// <see cref="StockService"/> به کاربر نشان می‌دهد یکی باشد.
    /// </summary>
    private static async Task CheckInventoryReconciliationAsync(ApplicationDbContext db, SimulationFindingLog log)
    {
        var stock = new StockService(db);

        var scopes = await db.InventoryMovements
            .AsNoTracking()
            .GroupBy(m => new { m.ProductId, m.TerminalId, m.StorageTankId, m.ContractId })
            .Select(g => new
            {
                g.Key.ProductId,
                g.Key.TerminalId,
                g.Key.StorageTankId,
                g.Key.ContractId,
                InQty = g.Where(m => m.Direction == MovementDirection.In).Sum(m => m.QuantityMt),
                OutQty = g.Where(m => m.Direction == MovementDirection.Out).Sum(m => m.QuantityMt),
                AdjustQty = g.Where(m => m.Direction == MovementDirection.Adjustment).Sum(m => m.QuantityMt),
                TransferQty = g.Where(m => m.Direction == MovementDirection.Transfer).Sum(m => m.QuantityMt)
            })
            .ToListAsync();

        var mismatches = new List<string>();
        var negativeScopes = new List<string>();

        foreach (var scope in scopes)
        {
            var expected = scope.InQty + scope.AdjustQty - scope.OutQty - scope.TransferQty;
            var actual = await stock.GetFreeQuantityMtAsync(
                scope.ProductId,
                terminalId: scope.TerminalId,
                contractId: scope.ContractId,
                storageTankId: scope.StorageTankId);

            if (Math.Abs(expected - actual) > 0.0001m)
            {
                mismatches.Add(
                    $"product={scope.ProductId} terminal={scope.TerminalId} tank={scope.StorageTankId} " +
                    $"contract={scope.ContractId} ledgerMath={expected:N4} stockService={actual:N4}");
            }

            if (expected < -0.0001m)
            {
                negativeScopes.Add(
                    $"product={scope.ProductId} terminal={scope.TerminalId} tank={scope.StorageTankId} " +
                    $"contract={scope.ContractId} closing={expected:N4} MT");
            }
        }

        log.Fact($"Inventory scopes reconciled: {scopes.Count}");

        if (mismatches.Count > 0)
        {
            log.Add(
                "SIM-INV-01",
                FindingSeverity.P0,
                "Inventory",
                "StockService با ریاضیِ خودِ حرکات موجودی نمی‌خواند.",
                string.Join("\n", mismatches.Take(10)));
        }

        if (negativeScopes.Count > 0)
        {
            log.Add(
                "SIM-INV-02",
                FindingSeverity.P0,
                "Inventory",
                $"موجودی پایانی در {negativeScopes.Count} scope منفی است.",
                string.Join("\n", negativeScopes.Take(10)));
        }
    }

    /// <summary>
    /// همان تحلیلی که خود سیستم برای «موجودی منفی» دارد — روی دادهٔ یک‌سالِ شبیه‌سازی‌شده.
    /// </summary>
    private static async Task CheckNegativeStockTimelineAsync(ApplicationDbContext db, SimulationFindingLog log)
    {
        var analysis = new NegativeStockAnalysisService(db);
        var findings = await analysis.AnalyzeAsync(new ManagementReportFilterViewModel());

        log.Fact($"NegativeStockAnalysisService findings: {findings.Count}");

        if (findings.Count == 0)
        {
            return;
        }

        var open = findings.Where(f => f.Status == NegativeStockStatus.Open).ToList();
        var healed = findings.Where(f => f.Status == NegativeStockStatus.HealedLegacy).ToList();

        if (open.Count > 0)
        {
            log.Add(
                "SIM-INV-03",
                FindingSeverity.P0,
                "Inventory",
                $"{open.Count} scope با موجودی منفیِ باز.",
                string.Join("\n", open.Take(10).Select(f =>
                    $"product={f.ProductName} tank={f.StorageTankCode} first={f.FirstNegativeDate:yyyy-MM-dd} " +
                    $"balance={f.FirstNegativeBalanceMt:N4} closing={f.ClosingBalanceMt:N4} cause={f.ProbableCause}")));
        }

        if (healed.Count > 0)
        {
            log.Add(
                "SIM-INV-04",
                FindingSeverity.P1,
                "Inventory",
                $"{healed.Count} scope در طول سال موقتاً منفی شده و بعداً ترمیم شده (اثر ثبتِ با تاریخ گذشته).",
                string.Join("\n", healed.Take(10).Select(f =>
                    $"product={f.ProductName} tank={f.StorageTankCode} first={f.FirstNegativeDate:yyyy-MM-dd} " +
                    $"balance={f.FirstNegativeBalanceMt:N4}")));
        }
    }

    // ----------------------------------------------------------- ledger linkage

    private static async Task CheckLedgerOrphansAsync(ApplicationDbContext db, SimulationFindingLog log)
    {
        var orphanSales = await db.LedgerEntries
            .AsNoTracking()
            .Where(l => l.SourceType == "Sale"
                && !db.SalesTransactions.Any(s => s.Id == l.SourceId))
            .CountAsync();

        var orphanExpenses = await db.LedgerEntries
            .AsNoTracking()
            .Where(l => l.SourceType == "Expense"
                && !db.ExpenseTransactions.Any(e => e.Id == l.SourceId))
            .CountAsync();

        var orphanLoadings = await db.LedgerEntries
            .AsNoTracking()
            .Where(l => l.SourceType == "Loading"
                && !db.LoadingRegisters.Any(x => x.Id == l.SourceId))
            .CountAsync();

        log.Fact($"Orphan ledger rows — Sale:{orphanSales} Expense:{orphanExpenses} Loading:{orphanLoadings}");

        if (orphanSales + orphanExpenses + orphanLoadings > 0)
        {
            log.Add(
                "SIM-LED-01",
                FindingSeverity.P0,
                "Ledger",
                "ردیف لجر بدون سند مبدأ (orphan) وجود دارد.",
                $"Sale={orphanSales}, Expense={orphanExpenses}, Loading={orphanLoadings}");
        }

        // هیچ FK بین LedgerEntry و سند مبدأ وجود ندارد؛ رابطه فقط (SourceType, SourceId) است.
        var hasForeignKey = await db.Database
            .SqlQuery<int>($@"
                SELECT COUNT(*)::int AS ""Value""
                FROM information_schema.table_constraints tc
                JOIN information_schema.constraint_column_usage ccu
                  ON tc.constraint_name = ccu.constraint_name
                WHERE tc.table_name = 'LedgerEntries'
                  AND tc.constraint_type = 'FOREIGN KEY'
                  AND ccu.column_name = 'SourceId'")
            .FirstAsync();

        // فاز ۸ — FK همچنان ممکن نیست (رابطه polymorphic است)، ولی جای خالیِ آن با
        // CONSTRAINT TRIGGERِ به‌تعویق‌افتاده پر شده: حذفِ خامِ سندِ پست‌شده رد می‌شود.
        // پس سنجه دیگر «FK هست یا نه» نیست، بلکه «کدام جدولِ مبدأ بی‌محافظ مانده».
        var guardedTables = await db.Database
            .SqlQuery<string>($@"
                SELECT c.relname AS ""Value""
                FROM pg_trigger t
                JOIN pg_class c ON c.oid = t.tgrelid
                WHERE t.tgname LIKE 'ptg_guard_%_ledger_delete'")
            .ToListAsync();

        var expectedGuards = new[]
        {
            "SalesTransactions",
            "ExpenseTransactions",
            "PaymentTransactions",
            "SupplierBalanceTransfers",
            "ContractBalanceTransfers"
        };

        var missingGuards = expectedGuards.Except(guardedTables, StringComparer.Ordinal).ToList();

        if (hasForeignKey == 0 && guardedTables.Count == 0)
        {
            log.Add(
                "SIM-LED-02",
                FindingSeverity.P1,
                "Ledger",
                "رابطهٔ لجر با سند مبدأ فقط (SourceType, SourceId) است و هیچ FK یا محافظی ندارد؛ حذف مستقیم سند، لجر را یتیم می‌کند.",
                "information_schema: no FOREIGN KEY and no ptg_guard_* trigger on LedgerEntries.SourceId");
        }
        else if (missingGuards.Count > 0)
        {
            log.Add(
                "SIM-LED-02",
                FindingSeverity.P1,
                "Ledger",
                "بعضی جدول‌های سندِ مالی هنوز محافظِ حذف ندارند و حذف مستقیمشان لجر را یتیم می‌کند.",
                $"unguarded: {string.Join(", ", missingGuards)}");
        }
        else
        {
            // محدودیتِ باقی‌مانده و آگاهانه: LoadingRegisters عمداً بی‌محافظ است، چون
            // BulkDelete از قصد لجرِ اصلی را نگه می‌دارد و فقط بارگیریِ اشتباه را پاک می‌کند.
            log.Add(
                "SIM-LED-02",
                FindingSeverity.P3,
                "Ledger",
                "حذف مستقیمِ سندِ مالیِ پست‌شده در سطح دیتابیس رد می‌شود. استثنای مستند: بارگیری (لجر عمداً به‌عنوان تاریخچه می‌ماند).",
                $"guarded: {string.Join(", ", expectedGuards)}; documented exception: LoadingRegisters");
        }
    }

    private static async Task CheckLedgerCoverageAsync(ApplicationDbContext db, SimulationFindingLog log)
    {
        var salesWithoutLedger = await db.SalesTransactions
            .AsNoTracking()
            .Where(s => !s.IsCancelled && !db.LedgerEntries.Any(l => l.SourceType == "Sale" && l.SourceId == s.Id))
            .CountAsync();

        var expensesWithoutLedger = await db.ExpenseTransactions
            .AsNoTracking()
            .Where(e => !e.IsCancelled && !db.LedgerEntries.Any(l => l.SourceType == "Expense" && l.SourceId == e.Id))
            .CountAsync();

        var paymentsWithoutLedger = await db.PaymentTransactions
            .AsNoTracking()
            .Where(p => p.LedgerEntryId == null)
            .CountAsync();

        var duplicateSaleLedgers = await db.LedgerEntries
            .AsNoTracking()
            .Where(l => l.SourceType == "Sale")
            .GroupBy(l => l.SourceId)
            .Where(g => g.Count() > 1)
            .CountAsync();

        log.Fact($"Ledger coverage gaps — sales:{salesWithoutLedger} expenses:{expensesWithoutLedger} " +
                 $"payments:{paymentsWithoutLedger} duplicateSaleLedgers:{duplicateSaleLedgers}");

        if (salesWithoutLedger + expensesWithoutLedger + paymentsWithoutLedger > 0)
        {
            log.Add(
                "SIM-LED-03",
                FindingSeverity.P0,
                "Ledger",
                "سندی وجود دارد که ردیف لجر متناظرش ساخته نشده.",
                $"sales={salesWithoutLedger}, expenses={expensesWithoutLedger}, payments={paymentsWithoutLedger}");
        }

        if (duplicateSaleLedgers > 0)
        {
            log.Add(
                "SIM-LED-04",
                FindingSeverity.P0,
                "Ledger",
                "برای یک فروش بیش از یک ردیف لجر ثبت شده.",
                $"duplicate groups={duplicateSaleLedgers}");
        }
    }

    private static async Task CheckMonthlyTotalsAsync(ApplicationDbContext db, SimulationFindingLog log)
    {
        var problems = new List<string>();

        for (var monthOffset = 0; monthOffset < SimulationWorld.Months; monthOffset++)
        {
            var from = SimulationWorld.StartDate.AddMonths(monthOffset);
            var to = from.AddMonths(1);

            var salesTotal = await db.SalesTransactions
                .AsNoTracking()
                .Where(s => !s.IsCancelled && s.SaleDate >= from && s.SaleDate < to)
                .SumAsync(s => (decimal?)s.TotalUsd) ?? 0m;

            var saleLedgerTotal = await db.LedgerEntries
                .AsNoTracking()
                .Where(l => l.SourceType == "Sale" && l.EntryDate >= from && l.EntryDate < to)
                .SumAsync(l => (decimal?)l.AmountUsd) ?? 0m;

            var expenseTotal = await db.ExpenseTransactions
                .AsNoTracking()
                .Where(e => !e.IsCancelled && e.ExpenseDate >= from && e.ExpenseDate < to)
                .SumAsync(e => (decimal?)e.AmountUsd) ?? 0m;

            var expenseLedgerTotal = await db.LedgerEntries
                .AsNoTracking()
                .Where(l => l.SourceType == "Expense" && l.EntryDate >= from && l.EntryDate < to)
                .SumAsync(l => (decimal?)l.AmountUsd) ?? 0m;

            if (Math.Abs(salesTotal - saleLedgerTotal) > 0.01m)
                problems.Add($"{from:yyyy-MM}: sales={salesTotal:N2} ledger={saleLedgerTotal:N2}");

            if (Math.Abs(expenseTotal - expenseLedgerTotal) > 0.01m)
                problems.Add($"{from:yyyy-MM}: expenses={expenseTotal:N2} ledger={expenseLedgerTotal:N2}");
        }

        if (problems.Count > 0)
        {
            log.Add(
                "SIM-LED-05",
                FindingSeverity.P0,
                "Ledger",
                "جمع ماهانهٔ اسناد با جمع ماهانهٔ لجر نمی‌خواند.",
                string.Join("\n", problems));
        }
        else
        {
            log.Fact("Monthly sale/expense totals reconcile with the ledger for all 12 months.");
        }
    }

    // ------------------------------------------------------------- partnership

    private static async Task CheckPartnershipConsistencyAsync(ApplicationDbContext db, SimulationFindingLog log)
    {
        var service = new PartnershipStatementService(db);
        var contractIds = await db.Contracts
            .AsNoTracking()
            .Where(c => c.OwnershipType == ContractOwnershipType.Partnership)
            .Select(c => c.Id)
            .ToListAsync();

        // PTG-P0-03/P0-04 — سهم تاریخ‌دار است: هر بازه باید خودش دقیقاً ۱۰۰٪ باشد.
        var shareSumProblems = await db.ContractPartners
            .AsNoTracking()
            .GroupBy(cp => new { cp.ContractId, cp.EffectiveFrom })
            .Select(g => new { g.Key.ContractId, Total = g.Sum(x => x.SharePercent) })
            .Where(x => x.Total != 100m)
            .ToListAsync();

        if (shareSumProblems.Count > 0)
        {
            log.Add(
                "SIM-PRT-01",
                FindingSeverity.P1,
                "Partnership",
                "جمع درصد سهم شرکا در سطح دیتابیس ۱۰۰ نیست (فقط اعتبارسنجی سمت Controller وجود دارد).",
                string.Join("\n", shareSumProblems.Take(10).Select(x => $"contract={x.ContractId} total={x.Total}")));
        }

        var pairs = await db.ContractPartners
            .AsNoTracking()
            .Where(cp => contractIds.Contains(cp.ContractId))
            .GroupBy(cp => cp.ContractId)
            .Select(g => g.Select(x => x.PartnerId).ToList())
            .ToListAsync();

        var distinctPairs = pairs
            .Where(p => p.Count == 2)
            .Select(p => (A: Math.Min(p[0], p[1]), B: Math.Max(p[0], p[1])))
            .Distinct()
            .Take(4)
            .ToList();

        var residuals = new List<string>();
        foreach (var (a, b) in distinctPairs)
        {
            var statement = await service.BuildAsync(a, b);
            foreach (var contract in statement.Contracts)
            {
                var profitShareSum = contract.Partners.Sum(p => p.ProfitShareUsd);
                if (Math.Abs(profitShareSum - contract.BookProfitUsd) > 0.02m)
                {
                    residuals.Add(
                        $"contract={contract.ContractNumber} bookProfit={contract.BookProfitUsd:N2} " +
                        $"sumOfShares={profitShareSum:N2}");
                }
            }
        }

        if (residuals.Count > 0)
        {
            log.Add(
                "SIM-PRT-02",
                FindingSeverity.P1,
                "Partnership",
                "جمع سهم مفادِ شرکا با مفادِ دفترِ همان قرارداد برابر نیست (خطای گِردکردن انباشته).",
                string.Join("\n", residuals.Take(10)));
        }
        else
        {
            log.Fact($"Partnership profit shares reconcile for {distinctPairs.Count} partner pairs.");
        }
    }

    // ------------------------------------------------------------- party balance

    private static async Task CheckPartyBalanceConsistencyAsync(ApplicationDbContext db, SimulationFindingLog log)
    {
        var supplierIds = await db.Suppliers.AsNoTracking().Select(s => s.Id).ToListAsync();
        var problems = new List<string>();

        foreach (var supplierId in supplierIds)
        {
            var ledgerNet = await db.LedgerEntries
                .AsNoTracking()
                .Where(l => l.SupplierId == supplierId)
                .SumAsync(l => (decimal?)(l.Side == LedgerSide.Credit ? l.AmountUsd : -l.AmountUsd)) ?? 0m;

            var loadingTotal = await db.LedgerEntries
                .AsNoTracking()
                .Where(l => l.SupplierId == supplierId && l.SourceType == "Loading")
                .SumAsync(l => (decimal?)l.AmountUsd) ?? 0m;

            var paidTotal = await db.PaymentTransactions
                .AsNoTracking()
                .Where(p => p.SupplierId == supplierId && p.Direction == PaymentDirection.Out)
                .SumAsync(p => (decimal?)p.AmountUsd) ?? 0m;

            var expected = loadingTotal - paidTotal;
            if (Math.Abs(expected - ledgerNet) > 0.01m)
            {
                problems.Add($"supplier={supplierId} ledgerNet={ledgerNet:N2} loading-paid={expected:N2}");
            }
        }

        if (problems.Count > 0)
        {
            log.Add(
                "SIM-BAL-01",
                FindingSeverity.P1,
                "Balances",
                "ماندهٔ تأمین‌کننده از روی لجر با «بارگیری منهای پرداخت» نمی‌خواند.",
                string.Join("\n", problems.Take(10)));
        }
        else
        {
            log.Fact($"Supplier ledger balances reconcile for {supplierIds.Count} suppliers.");
        }
    }

    // -------------------------------------------------------------- indexes/perf

    private static async Task CheckIndexCoverageAsync(ApplicationDbContext db, SimulationFindingLog log)
    {
        var indexes = await db.Database
            .SqlQuery<string>($@"
                SELECT (tablename || ' :: ' || indexdef) AS ""Value""
                FROM pg_indexes
                WHERE schemaname = 'public'")
            .ToListAsync();

        var missing = new List<string>();

        void Require(string table, string column)
        {
            var found = indexes.Any(i =>
                i.StartsWith(table + " ::", StringComparison.Ordinal)
                && i.Contains($"\"{column}\"", StringComparison.Ordinal));
            if (!found)
                missing.Add($"{table}.{column}");
        }

        Require("LedgerEntries", "EntryDate");
        Require("LedgerEntries", "SourceType");
        Require("LedgerEntries", "SupplierId");
        Require("LedgerEntries", "CustomerId");
        Require("LedgerEntries", "ContractId");
        Require("InventoryMovements", "MovementDate");
        Require("InventoryMovements", "ProductId");
        Require("InventoryMovements", "StorageTankId");
        Require("SalesTransactions", "SaleDate");
        Require("ExpenseTransactions", "ExpenseDate");
        Require("PaymentTransactions", "PaymentDate");

        log.Fact($"Public indexes present: {indexes.Count}");

        if (missing.Count > 0)
        {
            log.Add(
                "SIM-PRF-01",
                FindingSeverity.P4,
                "Performance",
                "ستون‌های پرکاربردِ فیلتر/مرتب‌سازی بدون ایندکس هستند.",
                string.Join(", ", missing));
        }
    }

    private async Task MeasureHotPathPerformanceAsync(ApplicationDbContext db, SimulationFindingLog log)
    {
        var stock = new StockService(db);
        var pnl = new ProfitAndLossService(db);

        async Task<double> TimeAsync(string label, Func<Task> action)
        {
            var sw = Stopwatch.StartNew();
            await action();
            sw.Stop();
            log.Fact($"{label}: {sw.ElapsedMilliseconds} ms");
            return sw.Elapsed.TotalMilliseconds;
        }

        var slow = new List<string>();

        var stockMs = await TimeAsync("StockService.GetMovementSummaryAsync (full history)",
            async () => await stock.GetMovementSummaryAsync());
        if (stockMs > 2000) slow.Add($"stock summary {stockMs:N0}ms");

        var pnlMs = await TimeAsync("ProfitAndLossService.BuildCompanyAsync (full year)",
            async () => await pnl.BuildCompanyAsync(new ManagementReportFilterViewModel()));
        if (pnlMs > 2000) slow.Add($"company P&L {pnlMs:N0}ms");

        var negativeMs = await TimeAsync("NegativeStockAnalysisService.AnalyzeAsync (full history)",
            async () => await new NegativeStockAnalysisService(db)
                .AnalyzeAsync(new ManagementReportFilterViewModel()));
        if (negativeMs > 2000) slow.Add($"negative-stock analysis {negativeMs:N0}ms");

        var ledgerMs = await TimeAsync("Ledger page 1 (50 rows, ordered by date desc)",
            async () => await db.LedgerEntries
                .AsNoTracking()
                .OrderByDescending(l => l.EntryDate)
                .ThenByDescending(l => l.Id)
                .Take(50)
                .ToListAsync());
        if (ledgerMs > 500) slow.Add($"ledger page {ledgerMs:N0}ms");

        var partnershipMs = await TimeAsync("PartnershipStatementService.BuildAsync (one pair, all contracts)",
            async () =>
            {
                var pair = await db.ContractPartners
                    .AsNoTracking()
                    .GroupBy(cp => cp.ContractId)
                    .Select(g => g.Select(x => x.PartnerId).ToList())
                    .FirstAsync();
                if (pair.Count == 2)
                    await new PartnershipStatementService(db).BuildAsync(pair[0], pair[1]);
            });
        if (partnershipMs > 2000) slow.Add($"partnership statement {partnershipMs:N0}ms");

        if (slow.Count > 0)
        {
            log.Add(
                "SIM-PRF-02",
                FindingSeverity.P4,
                "Performance",
                "صفحات کلیدی روی دادهٔ یک‌ساله کند هستند.",
                string.Join("\n", slow));
        }
    }
}

/// <summary>خطای «PostgreSQL در دسترس نیست» نباید به شکست تست ترجمه شود.</summary>
internal static class Skip
{
    public static void IfNotAvailable(SimulationPostgresFixture fixture)
    {
        if (!fixture.Available)
        {
            throw new SkipTestException(
                $"PostgreSQL is not reachable for the simulation: {fixture.UnavailableReason}");
        }
    }
}

internal sealed class SkipTestException : Exception
{
    public SkipTestException(string message) : base(message)
    {
    }
}
