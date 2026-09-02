using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.PartyStatements;
using PTGOilSystem.Web.Models.Reports;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.CompanyFlow;
using PTGOilSystem.Web.Services.PartyStatements;
using PTGOilSystem.Web.Services.Reporting;
using Xunit;
using Xunit.Abstractions;

namespace PTGOilSystem.Web.Tests.Simulation;

[CollectionDefinition(ScalePostgresCollection.CollectionName, DisableParallelization = true)]
public sealed class ScalePostgresCollection : ICollectionFixture<SimulationPostgresFixture>
{
    public const string CollectionName = "PTG Scale And Performance";
}

/// <summary>
/// «سال دوم و سوم»: حجمی که یک شرکت واقعی بعد از چند سال به آن می‌رسد.
/// دادهٔ حجیم با SQL خام ساخته می‌شود (سریع و قطعی) و بعد همان کوئری‌هایی که
/// صفحات اصلی می‌زنند زمان‌گیری می‌شوند.
/// </summary>
[Collection(ScalePostgresCollection.CollectionName)]
public sealed class ScaleAndPerformanceTests
{
    private const int LedgerRows = 300_000;
    private const int MovementRows = 150_000;
    private const int SaleRows = 60_000;
    private const int ExpenseRows = 60_000;
    private const int PaymentRows = 60_000;

    private readonly SimulationPostgresFixture _fixture;
    private readonly ITestOutputHelper _output;

    public ScaleAndPerformanceTests(SimulationPostgresFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [Fact]
    public async Task Hot_Pages_Stay_Responsive_On_Multi_Year_Volume()
    {
        Skip.IfNotAvailable(_fixture);

        var log = new SimulationFindingLog();
        var scope = await SeedSkeletonAsync();

        var sw = Stopwatch.StartNew();
        await BulkLoadAsync(scope);
        sw.Stop();
        log.Fact($"Bulk load ({LedgerRows:N0} ledger / {MovementRows:N0} movements / {SaleRows:N0} sales / " +
                 $"{ExpenseRows:N0} expenses / {PaymentRows:N0} payments) took {sw.Elapsed.TotalSeconds:N1}s.");

        await using var db = _fixture.CreateDbContext();
        db.Database.SetCommandTimeout(900);

        var slow = new List<string>();

        async Task MeasureAsync(string label, int budgetMs, Func<Task> action)
        {
            var timer = Stopwatch.StartNew();
            await action();
            timer.Stop();
            log.Fact($"{label}: {timer.ElapsedMilliseconds:N0} ms (budget {budgetMs:N0} ms)");
            if (timer.ElapsedMilliseconds > budgetMs)
                slow.Add($"{label} = {timer.ElapsedMilliseconds:N0} ms");
        }

        await MeasureAsync("Ledger page 1 (50 rows)", 1000, async () =>
            await db.LedgerEntries.AsNoTracking()
                .OrderByDescending(l => l.EntryDate).ThenByDescending(l => l.Id)
                .Skip(0).Take(50)
                .Select(l => new { l.Id, l.EntryDate, l.AmountUsd, l.Description })
                .ToListAsync());

        await MeasureAsync("Ledger deep page (offset 250,000)", 3000, async () =>
            await db.LedgerEntries.AsNoTracking()
                .OrderByDescending(l => l.EntryDate).ThenByDescending(l => l.Id)
                .Skip(250_000).Take(50)
                .Select(l => new { l.Id, l.EntryDate, l.AmountUsd, l.Description })
                .ToListAsync());

        await MeasureAsync("Ledger total count (paging header)", 2000, async () =>
            await db.LedgerEntries.AsNoTracking().CountAsync());

        await MeasureAsync("Customer statement (no date filter, full history)", 4000, async () =>
            await BuildStatementAsync(db, PartyStatementPartyType.Customer, scope.CustomerId));

        await MeasureAsync("Supplier statement (no date filter, full history)", 4000, async () =>
            await BuildStatementAsync(db, PartyStatementPartyType.Supplier, scope.SupplierId));

        await MeasureAsync("StockService free quantity (single tank)", 1500, async () =>
            await new StockService(db).GetFreeQuantityMtAsync(
                scope.ProductId, terminalId: scope.TerminalId, storageTankId: scope.TankId));

        await MeasureAsync("StockService movement summary (whole history)", 5000, async () =>
            await new StockService(db).GetMovementSummaryAsync());

        await MeasureAsync("NegativeStockAnalysisService (whole history)", 8000, async () =>
            await new NegativeStockAnalysisService(db).AnalyzeAsync(new ManagementReportFilterViewModel()));

        await MeasureAsync("Company P&L (whole history)", 8000, async () =>
            await new ProfitAndLossService(db).BuildCompanyAsync(new ManagementReportFilterViewModel()));

        await MeasureAsync("Sales list page 1 (20 rows with joins)", 1500, async () =>
            await db.SalesTransactions.AsNoTracking()
                .OrderByDescending(s => s.SaleDate).ThenByDescending(s => s.Id)
                .Take(20)
                .Select(s => new
                {
                    s.Id,
                    s.InvoiceNumber,
                    s.SaleDate,
                    s.TotalUsd,
                    Customer = s.Customer!.Name,
                    Product = s.Product!.Name
                })
                .ToListAsync());

        if (slow.Count > 0)
        {
            log.Add(
                "SCALE-PRF-01",
                FindingSeverity.P4,
                "Performance",
                "روی حجم چندساله، این مسیرها از بودجهٔ زمانی عبور می‌کنند.",
                string.Join("\n", slow));
        }

        var path = log.WriteToDisk("simulation-scale-findings.md", "PTG Scale & Performance — measured");
        _output.WriteLine(log.Render("PTG Scale & Performance"));
        _output.WriteLine($"Findings written to: {path}");

        Assert.True(true);
    }

    // ------------------------------------------------------------------ helpers

    private sealed record Skeleton(
        int CompanyId,
        int ProductId,
        int TerminalId,
        int TankId,
        int CustomerId,
        int SupplierId,
        int ContractId,
        int ExpenseTypeId);

    private async Task<Skeleton> SeedSkeletonAsync()
    {
        await using var db = _fixture.CreateDbContext();

        if (!await db.Currencies.AnyAsync(c => c.Code == "USD"))
        {
            db.Currencies.Add(new Currency { Code = "USD", Name = "US Dollar", Symbol = "$" });
            await db.SaveChangesAsync();
        }

        var company = new Company { Code = "SCALE", Name = "Scale Co", Country = "AF" };
        var product = new Product { Code = "SCALE-GO", Name = "Gas Oil" };
        var terminal = new Terminal { Code = "SCALE-T", Name = "Scale Terminal" };
        var customer = new Customer { Code = "SCALE-CU", Name = "Scale Customer" };
        var supplier = new Supplier { Code = "SCALE-SU", Name = "Scale Supplier" };
        var expenseType = new ExpenseType { Code = "SCALE-ET", Name = "Scale Expense", Category = "Operational" };
        db.AddRange(company, product, terminal, customer, supplier, expenseType);
        await db.SaveChangesAsync();

        var tank = new StorageTank
        {
            TerminalId = terminal.Id,
            TankCode = "SCALE-TK",
            ProductId = product.Id,
            CapacityMt = 1_000_000m
        };
        db.StorageTanks.Add(tank);

        var contract = new Contract
        {
            ContractNumber = "SCALE-PUR-001",
            ContractName = "Scale Purchase",
            ContractType = ContractType.Purchase,
            Status = ContractStatus.Active,
            OwnershipType = ContractOwnershipType.Personal,
            CompanyId = company.Id,
            ProductId = product.Id,
            SupplierId = supplier.Id,
            ContractDate = new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            PricingMethod = PricingMethod.Fixed,
            QuantityMt = 5_000_000m,
            UnitPriceUsd = 500m,
            Currency = "USD"
        };
        db.Contracts.Add(contract);
        await db.SaveChangesAsync();

        return new Skeleton(
            company.Id, product.Id, terminal.Id, tank.Id,
            customer.Id, supplier.Id, contract.Id, expenseType.Id);
    }

    private async Task BulkLoadAsync(Skeleton s)
    {
        await using var db = _fixture.CreateDbContext();
        db.Database.SetCommandTimeout(900);

        // ورودی موجودی: یک حرکت بزرگ تا موجودی هرگز منفی نشود و تحلیل‌ها معنادار بمانند.
        await db.Database.ExecuteSqlRawAsync($"""
            INSERT INTO "InventoryMovements"
                ("TerminalId","StorageTankId","ProductId","ContractId","Direction","MovementDate",
                 "QuantityMt","ReferenceDocument","CreatedAtUtc")
            VALUES ({s.TerminalId},{s.TankId},{s.ProductId},{s.ContractId},1,
                    TIMESTAMPTZ '2023-01-01 00:00:00+00', 50000000, 'SCALE-OPENING', NOW());
            """);

        await db.Database.ExecuteSqlRawAsync($"""
            INSERT INTO "InventoryMovements"
                ("TerminalId","StorageTankId","ProductId","ContractId","Direction","MovementDate",
                 "QuantityMt","ReferenceDocument","CreatedAtUtc")
            SELECT {s.TerminalId},{s.TankId},{s.ProductId},{s.ContractId},2,
                   TIMESTAMPTZ '2023-01-01 00:00:00+00' + (g % 1095) * INTERVAL '1 day',
                   10 + (g % 37),
                   'SCALE-OUT-' || g,
                   NOW()
            FROM generate_series(1, {MovementRows}) AS g;
            """);

        await db.Database.ExecuteSqlRawAsync($"""
            INSERT INTO "SalesTransactions"
                ("CompanyId","CustomerId","ProductId","SourcePurchaseContractId","SaleStage",
                 "InvoiceNumber","SaleDate","QuantityMt","Currency","UnitPriceInCurrency",
                 "AppliedFxRateToUsd","UnitPriceUsd","TotalInCurrency","TotalUsd","IsCancelled","CreatedAtUtc")
            SELECT {s.CompanyId},{s.CustomerId},{s.ProductId},{s.ContractId},0,
                   'SCALE-INV-' || g,
                   TIMESTAMPTZ '2023-01-01 00:00:00+00' + (g % 1095) * INTERVAL '1 day',
                   10 + (g % 37), 'USD', 600, 1, 600,
                   (10 + (g % 37)) * 600, (10 + (g % 37)) * 600, FALSE, NOW()
            FROM generate_series(1, {SaleRows}) AS g;
            """);

        await db.Database.ExecuteSqlRawAsync($"""
            INSERT INTO "ExpenseTransactions"
                ("ExpenseTypeId","ContractId","ExpenseDate","Amount","Currency",
                 "AppliedFxRateToUsd","AmountUsd","Description","IsCancelled","CreatedAtUtc")
            SELECT {s.ExpenseTypeId},{s.ContractId},
                   TIMESTAMPTZ '2023-01-01 00:00:00+00' + (g % 1095) * INTERVAL '1 day',
                   100 + (g % 900), 'USD', 1, 100 + (g % 900),
                   'SCALE expense ' || g, FALSE, NOW()
            FROM generate_series(1, {ExpenseRows}) AS g;
            """);

        await db.Database.ExecuteSqlRawAsync($"""
            INSERT INTO "PaymentTransactions"
                ("PaymentDate","Direction","PaymentKind","CompanyId","ContractId","SupplierId",
                 "FundingSource","Amount","Currency","AppliedFxRateToUsd","AmountUsd",
                 "Reference","Description","CreatedAtUtc")
            SELECT TIMESTAMPTZ '2023-01-01 00:00:00+00' + (g % 1095) * INTERVAL '1 day',
                   2, 2, {s.CompanyId}, {s.ContractId}, {s.SupplierId},
                   1, 1000 + (g % 5000), 'USD', 1, 1000 + (g % 5000),
                   'SCALE-PAY-' || g, 'SCALE payment ' || g, NOW()
            FROM generate_series(1, {PaymentRows}) AS g;
            """);

        // دفتر کل: نیمی روی مشتری، نیمی روی تأمین‌کننده — همان شکلی که صورت‌حساب می‌خواند.
        await db.Database.ExecuteSqlRawAsync($"""
            INSERT INTO "LedgerEntries"
                ("EntryDate","Side","AmountUsd","Currency","SourceAmount","SourceCurrencyCode",
                 "AppliedFxRateToUsd","Description","SourceType","SourceId","Reference",
                 "ContractId","CustomerId","SupplierId","CreatedAtUtc")
            SELECT TIMESTAMPTZ '2023-01-01 00:00:00+00' + (g % 1095) * INTERVAL '1 day',
                   CASE WHEN g % 2 = 0 THEN 1 ELSE 2 END,
                   100 + (g % 9000), 'USD', 100 + (g % 9000), 'USD', 1,
                   'SCALE ledger ' || g,
                   CASE WHEN g % 2 = 0 THEN 'Sale' ELSE 'SupplierPayment' END,
                   g, 'SCALE-REF-' || g,
                   {s.ContractId},
                   CASE WHEN g % 2 = 0 THEN {s.CustomerId} ELSE NULL END,
                   CASE WHEN g % 2 = 1 THEN {s.SupplierId} ELSE NULL END,
                   NOW()
            FROM generate_series(1, {LedgerRows}) AS g;
            """);

        await db.Database.ExecuteSqlRawAsync("ANALYZE;");
    }

    private static Task<PartyStatementResult> BuildStatementAsync(
        ApplicationDbContext db,
        PartyStatementPartyType partyType,
        int partyId)
        => new PartyStatementReadService(
                db,
                new PartyStatementPolicyResolver(),
                new CompanyFlowDirectionResolver(),
                new CompanyFlowBalanceService(),
                Options.Create(new PartyStatementOptions()))
            .GetStatementAsync(
                new PartyRef(partyType, partyId),
                new PartyStatementFilter { IncludeOperationalColumns = false });
}
