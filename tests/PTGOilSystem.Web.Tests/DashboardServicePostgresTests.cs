using System.Data.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services;
using Xunit;
using Xunit.Abstractions;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// اثبات هم‌ارزی رفتاری و اندازه‌گیری تعداد رفت‌وبرگشت دیتابیس برای داشبورد روی PostgreSQL واقعی.
/// مقادیر مورد انتظار با همان LINQ مرجع (معناشناسی فعلی) به‌صورت مستقل محاسبه می‌شوند تا
/// هر بهینه‌سازی SQL دقیقاً همان خروجی را برگرداند.
/// </summary>
[Collection(AccountingPostgreSqlCollection.CollectionName)]
[Trait("Category", "PostgreSql")]
[Trait("Category", "Integration")]
public sealed class DashboardServicePostgresTests(AccountingPostgreSqlFixture fixture, ITestOutputHelper output)
{
    private const decimal LowStockThresholdMt = 10m;

    private sealed class QueryCountingInterceptor : DbCommandInterceptor
    {
        public int Count;

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Count++;
            return base.ReaderExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Count++;
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override InterceptionResult<object> ScalarExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
        {
            Count++;
            return base.ScalarExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<object> result,
            CancellationToken cancellationToken = default)
        {
            Count++;
            return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private ApplicationDbContext CreateContext(QueryCountingInterceptor? counter = null)
    {
        var builder = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(fixture.ConnectionString);
        if (counter is not null)
        {
            builder.AddInterceptors(counter);
        }

        return new ApplicationDbContext(builder.Options);
    }

    private static async Task<int> SeedOperationalDataAsync(ApplicationDbContext db)
    {
        // «امروز» باید همان روز کاری کابلی باشد که DashboardService استفاده می‌کند؛
        // با DateTime.UtcNow.Date این تست بین 19:30 تا 23:59 UTC (بعد از نیمه‌شب کابل) می‌شکست.
        var todayUtc = BusinessToday;
        var marker = Guid.NewGuid().ToString("N")[..12];

        var product = new Product { Code = $"P-{marker}", Name = $"Product {marker}" };
        var company = new Company { Code = $"C-{marker}", Name = $"Company {marker}" };
        var customer = new Customer { Name = $"Customer {marker}" };
        var supplier = new Supplier { Name = $"Supplier {marker}" };
        var terminal = new Terminal { Code = $"T-{marker}", Name = $"Terminal {marker}" };
        var expenseType = new ExpenseType { Code = $"ET-{marker}", Name = $"Expense {marker}" };
        var cashAccount = new CashAccount { Code = $"CA-{marker}", Name = $"Cash {marker}" };
        var sarraf = new Sarraf { Name = $"Sarraf {marker}", IsActive = true };
        var truck = new Truck { PlateNumber = $"TRK-{marker}" };
        db.AddRange(product, company, customer, supplier, terminal, expenseType, cashAccount, sarraf, truck);
        await db.SaveChangesAsync();

        // قرارداد فعال بدون قیمت نهایی → ContractsWithoutFinalPriceCount
        var contract = new Contract
        {
            ContractNumber = $"CON-{marker}",
            ContractType = ContractType.Purchase,
            Status = ContractStatus.Active,
            CompanyId = company.Id,
            ProductId = product.Id,
            SupplierId = supplier.Id,
            ContractDate = todayUtc.AddDays(-5),
            QuantityMt = 100m,
            PricingMethod = PricingMethod.Fixed,
            UnitPriceUsd = null,
            ManualFinalPriceUsd = null,
            PlattsManualPriceUsd = null
        };
        db.Add(contract);
        await db.SaveChangesAsync();

        // بارگیری امروز بدون رسید و بدون گمرک → TodayLoadingCount / LoadingsWithoutReceiptCount / LoadingsWithoutCustomsCount
        var loadingNoReceipt = new LoadingRegister
        {
            ContractId = contract.Id,
            ProductId = product.Id,
            LoadingDate = todayUtc,
            LoadedQuantityMt = 20m
        };
        // بارگیری دوم با رسید بدون تخصیص → ReceiptsWithoutAllocationCount
        var loadingWithReceipt = new LoadingRegister
        {
            ContractId = contract.Id,
            ProductId = product.Id,
            LoadingDate = todayUtc,
            LoadedQuantityMt = 15m
        };
        db.AddRange(loadingNoReceipt, loadingWithReceipt);
        await db.SaveChangesAsync();

        db.Add(new LoadingReceipt
        {
            LoadingRegisterId = loadingWithReceipt.Id,
            TerminalId = terminal.Id,
            ReceiptDate = todayUtc,
            ReceivedQuantityMt = 15m
        });

        // فروش امروز بدون پرداخت → TodaySales / SalesWithoutPaymentCount
        db.Add(new SalesTransaction
        {
            CompanyId = company.Id,
            ContractId = contract.Id,
            CustomerId = customer.Id,
            ProductId = product.Id,
            InvoiceNumber = $"INV-{marker}",
            SaleDate = todayUtc,
            QuantityMt = 2m,
            UnitPriceUsd = 500m,
            TotalUsd = 1000m
        });

        // دریافت/پرداخت امروز → TodayReceiptsUsd / TodayPaymentsUsd
        db.AddRange(
            new PaymentTransaction
            {
                PaymentDate = todayUtc,
                Direction = PaymentDirection.In,
                CashAccountId = cashAccount.Id,
                CustomerId = customer.Id,
                Amount = 200m,
                AmountUsd = 200m
            },
            new PaymentTransaction
            {
                PaymentDate = todayUtc,
                Direction = PaymentDirection.Out,
                CashAccountId = cashAccount.Id,
                SupplierId = supplier.Id,
                Amount = 50m,
                AmountUsd = 50m
            });

        // هزینه امروز → TodayExpensesUsd / MonthExpensesUsd
        db.Add(new ExpenseTransaction
        {
            ExpenseTypeId = expenseType.Id,
            ContractId = contract.Id,
            ExpenseDate = todayUtc,
            Amount = 250m,
            AmountUsd = 250m
        });

        // ارسال امروز → TodayDispatchCount
        db.Add(new TruckDispatch
        {
            ContractId = contract.Id,
            ProductId = product.Id,
            TruckId = truck.Id,
            DispatchDate = todayUtc,
            LoadedQuantityMt = 3m,
            Status = DispatchStatus.Loaded
        });

        // ضایعه با کسری قابل مطالبه → ShortageCount / ExcessShortageCount
        db.Add(new LossEvent
        {
            ProductId = product.Id,
            ContractId = contract.Id,
            EventDate = todayUtc,
            ExpectedQuantityMt = 10m,
            ActualQuantityMt = 8m,
            DifferenceQuantityMt = 2m,
            ChargeableLossMt = 2m,
            Reference = $"LOSS-{marker}"
        });

        // تسویه صراف با تفاوت نرخ → SarrafRateDiffCount
        db.Add(new SarrafSettlement
        {
            SettlementDate = todayUtc,
            SarrafId = sarraf.Id,
            SupplierId = supplier.Id,
            RequestedAmount = 100m,
            RequestedAmountUsd = 100m,
            SarrafRate = 70m,
            SarrafChargedAmount = 7000m,
            SarrafFxRateToUsd = 70m,
            SarrafChargedAmountUsd = 100m,
            SupplierAcceptedAmount = 95m,
            SupplierAcceptedAmountUsd = 95m,
            DifferenceAmountUsd = 5m,
            DifferenceType = SarrafSettlementDifferenceType.Gain
        });

        // موجودی ترمینال → LowStockAlerts / TerminalStockMt
        db.Add(new InventoryMovement
        {
            ProductId = product.Id,
            ContractId = contract.Id,
            TerminalId = terminal.Id,
            Direction = MovementDirection.In,
            MovementDate = todayUtc,
            QuantityMt = 8m
        });

        // لجر برای خلاصه مانده‌ها
        db.AddRange(
            new LedgerEntry
            {
                EntryDate = todayUtc,
                Side = LedgerSide.Credit,
                AmountUsd = 1000m,
                SourceType = $"Sale-{marker}",
                SourceId = 1,
                ContractId = contract.Id,
                CustomerId = customer.Id
            },
            new LedgerEntry
            {
                EntryDate = todayUtc,
                Side = LedgerSide.Debit,
                AmountUsd = 250m,
                SourceType = $"Expense-{marker}",
                SourceId = 1,
                ContractId = contract.Id,
                SupplierId = supplier.Id
            });

        await db.SaveChangesAsync();
        return contract.Id;
    }

    // تنها مرجع «امروز» در این تست، همان ساعت تجاری کابل است که سرویس هم از آن استفاده می‌کند.
    private static DateTime BusinessToday
        => new PTGOilSystem.Web.Services.Time.AfghanistanBusinessClock(TimeProvider.System).Today;

    private sealed record BalanceReference(int ItemCount, decimal DebitTotalUsd, decimal CreditTotalUsd);

    private static async Task<BalanceReference> ComputeBalanceReferenceAsync(IQueryable<LedgerEntry> query)
    {
        var rows = await query
            .Select(l => new { l.Side, l.AmountUsd })
            .ToListAsync();
        return new BalanceReference(
            rows.Count,
            rows.Where(l => l.Side == LedgerSide.Debit).Sum(l => l.AmountUsd),
            rows.Where(l => l.Side == LedgerSide.Credit).Sum(l => l.AmountUsd));
    }

    [Fact]
    public async Task Dashboard_Operational_Stats_And_Balances_Match_Linq_Reference_On_PostgreSql()
    {
        await using var seedDb = CreateContext();
        await SeedOperationalDataAsync(seedDb);

        var todayUtc = BusinessToday;
        var tomorrowUtc = todayUtc.AddDays(1);
        var monthStartUtc = new DateTime(todayUtc.Year, todayUtc.Month, 1, 0, 0, 0, DateTimeKind.Utc);

        // مقادیر مرجع: همان معناشناسی LINQ فعلی، مستقل از پیاده‌سازی سرویس.
        await using var refDb = CreateContext();
        var expectedShipmentsInTransit = await refDb.InventoryTransportLegs
            .CountAsync(l => l.Status == InventoryTransportLegStatus.InTransit);
        var expectedTodaySalesUsd = await refDb.SalesTransactions
            .Where(s => !s.IsCancelled && s.SaleDate >= todayUtc && s.SaleDate < tomorrowUtc)
            .SumAsync(s => (decimal?)s.TotalUsd) ?? 0m;
        var expectedTodaySalesCount = await refDb.SalesTransactions
            .CountAsync(s => !s.IsCancelled && s.SaleDate >= todayUtc && s.SaleDate < tomorrowUtc);
        var expectedTodayReceiptsUsd = await refDb.PaymentTransactions
            .Where(p => p.Direction == PaymentDirection.In && p.PaymentDate >= todayUtc && p.PaymentDate < tomorrowUtc)
            .SumAsync(p => (decimal?)p.AmountUsd) ?? 0m;
        var expectedTodayPaymentsUsd = await refDb.PaymentTransactions
            .Where(p => p.Direction == PaymentDirection.Out && p.PaymentDate >= todayUtc && p.PaymentDate < tomorrowUtc)
            .SumAsync(p => (decimal?)p.AmountUsd) ?? 0m;
        var expectedTodayExpensesUsd = await refDb.ExpenseTransactions
            .Where(e => !e.IsCancelled && e.ExpenseDate >= todayUtc && e.ExpenseDate < tomorrowUtc)
            .SumAsync(e => (decimal?)e.AmountUsd) ?? 0m;
        var expectedMonthExpensesUsd = await refDb.ExpenseTransactions
            .Where(e => !e.IsCancelled && e.ExpenseDate >= monthStartUtc && e.ExpenseDate < tomorrowUtc)
            .SumAsync(e => (decimal?)e.AmountUsd) ?? 0m;
        var expectedActiveSarrafCount = await refDb.Sarrafs.CountAsync(s => s.IsActive);
        var expectedTodayLoadingCount = await refDb.LoadingRegisters
            .CountAsync(l => l.LoadingDate >= todayUtc && l.LoadingDate < tomorrowUtc);
        var expectedTodayDispatchCount = await refDb.TruckDispatches
            .CountAsync(d => d.Status != DispatchStatus.Cancelled && d.DispatchDate >= todayUtc && d.DispatchDate < tomorrowUtc);
        var expectedLoadingsWithoutReceipt = await refDb.LoadingRegisters
            .CountAsync(l => !refDb.LoadingReceipts.Any(r => r.LoadingRegisterId == l.Id));
        var expectedReceiptsWithoutAllocation = await refDb.LoadingReceipts
            .CountAsync(r => !refDb.LoadingReceiptAllocations.Any(a => a.LoadingReceiptId == r.Id));
        var expectedLoadingsWithoutCustoms = await refDb.LoadingRegisters
            .CountAsync(l => !refDb.CustomsDeclarations.Any(c => c.LoadingRegisterId == l.Id));
        var expectedSalesWithoutPayment = await refDb.SalesTransactions
            .CountAsync(s => !s.IsCancelled && !refDb.PaymentTransactions.Any(p => p.SalesTransactionId == s.Id));
        var expectedContractsWithoutFinalPrice = await refDb.Contracts
            .CountAsync(c => c.Status == ContractStatus.Active
                && c.UnitPriceUsd == null
                && c.ManualFinalPriceUsd == null
                && c.PlattsManualPriceUsd == null);
        var expectedShortageCount = await refDb.LossEvents.CountAsync(e => !e.IsCancelled);
        var expectedExcessShortageCount = await refDb.LossEvents
            .CountAsync(e => !e.IsCancelled && e.ChargeableLossMt > 0m);
        var expectedSarrafRateDiffCount = await refDb.SarrafSettlements
            .CountAsync(x => x.DifferenceType != SarrafSettlementDifferenceType.None);
        var expectedTankStocks = await refDb.InventoryMovements
            .Where(m => m.StorageTankId != null)
            .GroupBy(m => m.StorageTankId)
            .Select(g => g.Sum(m =>
                m.Direction == MovementDirection.In || m.Direction == MovementDirection.Adjustment
                    ? m.QuantityMt
                    : m.Direction == MovementDirection.Out || m.Direction == MovementDirection.Transfer
                        ? -m.QuantityMt
                        : 0m))
            .ToListAsync();
        var expectedLowStockTankCount = expectedTankStocks.Count(s => s <= LowStockThresholdMt);

        var expectedContractBalance = await ComputeBalanceReferenceAsync(
            refDb.LedgerEntries.AsNoTracking().Where(l => l.ContractId.HasValue));
        var expectedCustomerBalance = await ComputeBalanceReferenceAsync(
            refDb.LedgerEntries.AsNoTracking().Where(l => l.CustomerId.HasValue));
        var expectedSupplierBalance = await ComputeBalanceReferenceAsync(
            refDb.LedgerEntries.AsNoTracking().Where(l => l.SupplierId.HasValue));

        await using var db = CreateContext();
        var service = new DashboardService(db, new HttpContextAccessor());
        var vm = await service.BuildDashboardAsync();

        Assert.Equal(expectedShipmentsInTransit, vm.ShipmentsInTransitCount);
        Assert.Equal(expectedTodaySalesUsd, vm.TodaySalesUsd);
        Assert.Equal(expectedTodaySalesCount, vm.TodaySalesCount);
        Assert.Equal(expectedTodayReceiptsUsd, vm.TodayReceiptsUsd);
        Assert.Equal(expectedTodayPaymentsUsd, vm.TodayPaymentsUsd);
        Assert.Equal(expectedTodayExpensesUsd, vm.TodayExpensesUsd);
        Assert.Equal(expectedMonthExpensesUsd, vm.MonthExpensesUsd);
        Assert.Equal(expectedActiveSarrafCount, vm.ActiveSarrafCount);
        Assert.Equal(expectedTodayLoadingCount, vm.TodayLoadingCount);
        Assert.Equal(expectedTodayDispatchCount, vm.TodayDispatchCount);
        Assert.Equal(expectedLoadingsWithoutReceipt, vm.LoadingsWithoutReceiptCount);
        Assert.Equal(expectedReceiptsWithoutAllocation, vm.ReceiptsWithoutAllocationCount);
        Assert.Equal(expectedLoadingsWithoutCustoms, vm.LoadingsWithoutCustomsCount);
        Assert.Equal(expectedSalesWithoutPayment, vm.SalesWithoutPaymentCount);
        Assert.Equal(expectedContractsWithoutFinalPrice, vm.ContractsWithoutFinalPriceCount);
        Assert.Equal(expectedShortageCount, vm.ShortageCount);
        Assert.Equal(expectedExcessShortageCount, vm.ExcessShortageCount);
        Assert.Equal(expectedSarrafRateDiffCount, vm.SarrafRateDiffCount);
        Assert.Equal(expectedLowStockTankCount, vm.LowStockTankCount);

        Assert.Equal(expectedContractBalance.ItemCount, vm.ContractBalanceSummary.ItemCount);
        Assert.Equal(expectedContractBalance.DebitTotalUsd, vm.ContractBalanceSummary.DebitTotalUsd);
        Assert.Equal(expectedContractBalance.CreditTotalUsd, vm.ContractBalanceSummary.CreditTotalUsd);
        Assert.Equal(expectedCustomerBalance.ItemCount, vm.CustomerBalanceSummary.ItemCount);
        Assert.Equal(expectedCustomerBalance.DebitTotalUsd, vm.CustomerBalanceSummary.DebitTotalUsd);
        Assert.Equal(expectedCustomerBalance.CreditTotalUsd, vm.CustomerBalanceSummary.CreditTotalUsd);
        Assert.Equal(expectedSupplierBalance.ItemCount, vm.SupplierBalanceSummary.ItemCount);
        Assert.Equal(expectedSupplierBalance.DebitTotalUsd, vm.SupplierBalanceSummary.DebitTotalUsd);
        Assert.Equal(expectedSupplierBalance.CreditTotalUsd, vm.SupplierBalanceSummary.CreditTotalUsd);
    }

    [Fact]
    public async Task Dashboard_Db_Round_Trips_Are_Bounded_On_PostgreSql()
    {
        await using var seedDb = CreateContext();
        await SeedOperationalDataAsync(seedDb);

        var counter = new QueryCountingInterceptor();
        await using var db = CreateContext(counter);
        var service = new DashboardService(db, new HttpContextAccessor());

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await service.BuildDashboardAsync();
        stopwatch.Stop();

        output.WriteLine($"Dashboard DB commands: {counter.Count}, elapsed: {stopwatch.ElapsedMilliseconds} ms");

        // پیش از بهینه‌سازی ۴۰ فرمان بود؛ بعد از تجمیع OperationalStats و BalanceSummaries به ۲۱ رسید.
        // کارت‌های بینش (قراردادهای در جریان + ظرفیت مخازن) دو فرمان اضافه کردند: ۲۳.
        // این سقف regression تعداد رفت‌وبرگشت داشبورد را قفل می‌کند.
        Assert.InRange(counter.Count, 1, 25);
    }
}
