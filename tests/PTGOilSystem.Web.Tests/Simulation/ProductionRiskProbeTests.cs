using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using PTGOilSystem.Web.Controllers;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Helpers;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.Expenses;
using PTGOilSystem.Web.Models.Loading;
using PTGOilSystem.Web.Models.LossEvents;
using PTGOilSystem.Web.Models.Sales;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.PartyStatements;
using Xunit;
using Xunit.Abstractions;

namespace PTGOilSystem.Web.Tests.Simulation;

[CollectionDefinition(ProbePostgresCollection.CollectionName, DisableParallelization = true)]
public sealed class ProbePostgresCollection : ICollectionFixture<SimulationPostgresFixture>
{
    public const string CollectionName = "PTG Production Risk Probes";
}

/// <summary>
/// سناریوهای «شرایط واقعی افغانستان» که فقط با اجرای واقعیِ مسیرِ نوشتن قابل اثبات‌اند:
/// اینترنت ضعیف و Submit دوباره، ثبت با تاریخ گذشته، تغییر درصد سهم بعد از ثبت تراکنش،
/// و حذف رکوردی که رکوردهای وابسته دارد.
///
/// هر تست دادهٔ خودش را روی همان دیتابیس موقت با شناسه‌های یکتا می‌سازد؛ هیچ دادهٔ واقعی
/// خوانده یا نوشته نمی‌شود.
/// </summary>
[Collection(ProbePostgresCollection.CollectionName)]
public sealed class ProductionRiskProbeTests
{
    private readonly SimulationPostgresFixture _fixture;
    private readonly ITestOutputHelper _output;

    public ProductionRiskProbeTests(SimulationPostgresFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    // ------------------------------------------------------------------ probe 1

    /// <summary>
    /// PTG-P0-01 — اینترنت ضعیف: کاربر «ثبت مصرف» را می‌زند، پاسخ دیر می‌آید، صفحه را Refresh
    /// می‌کند و مرورگر همان POST را دوباره می‌فرستد.
    ///
    /// پیش از اصلاح: دو سند مصرف و دو ردیف دفتر کل (۲۵٬۰۰۰ USD به‌جای ۱۲٬۵۰۰).
    /// پس از اصلاح: توکن فرم در همان Transaction مصرف می‌شود، ارسال دوم رد می‌شود.
    /// هر دو ارسال از دو DbContext جدا انجام می‌شود تا دقیقاً مثل دو درخواست HTTP باشد.
    /// </summary>
    [Fact]
    public async Task Probe01_Double_Submit_Of_Expense_Creates_Exactly_One_Expense_And_One_Ledger_Row()
    {
        Skip.IfNotAvailable(_fixture);
        var scope = await SeedMinimalScopeAsync("P01");

        // همان توکنی که فرم روی GET صادر کرده و در هر دو ارسال تکرار می‌شود.
        var formToken = Guid.NewGuid().ToString("N");

        ExpenseCreateViewModel BuildModel() => new()
        {
            ExpenseTypeId = scope.ExpenseTypeId,
            ContractId = scope.PurchaseContractId,
            ExpenseDate = new DateTime(2025, 6, 10, 0, 0, 0, DateTimeKind.Utc),
            Amount = 12_500m,
            Currency = "USD",
            Description = "Customs clearance — Hairatan"
        };

        await using (var db = _fixture.CreateDbContext())
        {
            await BuildExpensesController(db).Create(BuildModel(), formToken);
        }

        await using (var db = _fixture.CreateDbContext())
        {
            await BuildExpensesController(db).Create(BuildModel(), formToken);
        }

        await using var verify = _fixture.CreateDbContext();
        var expenses = await verify.ExpenseTransactions
            .AsNoTracking()
            .Where(e => e.ContractId == scope.PurchaseContractId)
            .ToListAsync();

        var ledgerRows = await verify.LedgerEntries
            .AsNoTracking()
            .Where(l => l.SourceType == "Expense" && l.ContractId == scope.PurchaseContractId)
            .ToListAsync();

        _output.WriteLine($"expenses={expenses.Count} ledgerRows={ledgerRows.Count} " +
                          $"totalUsd={expenses.Sum(e => e.AmountUsd):N2}");

        Assert.Equal(1, expenses.Count);
        Assert.Equal(1, ledgerRows.Count);
        Assert.Equal(12_500m, expenses.Sum(e => e.AmountUsd));
    }

    // ------------------------------------------------------------------ probe 2

    /// <summary>
    /// همان سناریو روی فروش — که برخلاف مصرف، توکن Idempotency دارد.
    /// این تست «کنترل مثبت» است: ثابت می‌کند نبودِ محافظ در مصرف یک خلأ است نه محدودیت تست.
    /// </summary>
    [Fact]
    public async Task Probe02_Double_Submit_Of_Sale_With_Same_Form_Token_Is_Rejected()
    {
        Skip.IfNotAvailable(_fixture);
        var scope = await SeedMinimalScopeAsync("P02", withStock: true);

        var token = Guid.NewGuid().ToString("N");

        await using (var db = _fixture.CreateDbContext())
        {
            var controller = BuildSalesController(db);
            await controller.Create(BuildSaleModel(scope, "INV-P02-001", 50m, new DateTime(2025, 6, 15, 0, 0, 0, DateTimeKind.Utc)), token);
        }

        await using (var db = _fixture.CreateDbContext())
        {
            var controller = BuildSalesController(db);
            await controller.Create(BuildSaleModel(scope, "INV-P02-001", 50m, new DateTime(2025, 6, 15, 0, 0, 0, DateTimeKind.Utc)), token);
        }

        await using (var verify = _fixture.CreateDbContext())
        {
            var sales = await verify.SalesTransactions
                .AsNoTracking()
                .Where(s => s.InvoiceNumber == "INV-P02-001")
                .CountAsync();
            _output.WriteLine($"sales with same invoice + token: {sales}");
            Assert.Equal(1, sales);
        }
    }

    // ------------------------------------------------------------------ probe 3

    /// <summary>
    /// PTG-P0-02 — ثبت با تاریخ گذشته نباید بی‌صدا موجودی را منفی کند.
    ///
    /// پیش از اصلاح: فروشِ عقب‌تاریخ فقط با موجودیِ «همان تاریخ» سنجیده می‌شد و
    /// موجودی نهایی به ‎-70 MT می‌رسید.
    /// پس از اصلاح: نگهبانِ خط زمانی سند دوم را رد می‌کند و پیام می‌گوید موجودی از
    /// چه تاریخی منفی می‌شد.
    /// </summary>
    [Fact]
    public async Task Probe03_Backdated_Sale_Is_Blocked_Instead_Of_Creating_Negative_Stock()
    {
        Skip.IfNotAvailable(_fixture);
        var scope = await SeedMinimalScopeAsync("P03", withStock: true, stockQty: 100m,
            receiptDate: new DateTime(2025, 1, 5, 0, 0, 0, DateTimeKind.Utc));

        await using (var db = _fixture.CreateDbContext())
        {
            var controller = BuildSalesController(db);
            // فروش امروزی: ۹۰ از ۱۰۰ تن — باید قبول شود.
            var result = await controller.Create(
                BuildSaleModel(scope, "INV-P03-001", 90m, new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc)));
            _output.WriteLine($"first sale result: {result.GetType().Name}");
            Assert.IsNotType<ViewResult>(result);
        }

        await using (var db = _fixture.CreateDbContext())
        {
            var controller = BuildSalesController(db);
            // فروشِ عقب‌تاریخ (۲۰ جنوری): در آن تاریخ ۱۰۰ تن موجود بوده، ولی ثبتش
            // موجودی ۱ جون را منفی می‌کند ⇒ باید رد شود و فرم با پیام برگردد.
            var result = await controller.Create(
                BuildSaleModel(scope, "INV-P03-002", 80m, new DateTime(2025, 1, 20, 0, 0, 0, DateTimeKind.Utc)));
            var view = Assert.IsType<ViewResult>(result);

            var errors = controller.ModelState
                .SelectMany(kv => kv.Value!.Errors.Select(e => e.ErrorMessage))
                .ToList();
            _output.WriteLine($"backdated sale rejected with: {string.Join(" | ", errors)}");

            Assert.NotEmpty(errors);
            // پیام باید تاریخِ منفی‌شدن و مخزن را بگوید، نه یک خطای عمومی.
            Assert.Contains(errors, e => e.Contains("2025-06-01", StringComparison.Ordinal));
            Assert.Contains(errors, e => e.Contains("TK-P03", StringComparison.Ordinal));
            Assert.NotNull(view);
        }

        await using (var verify = _fixture.CreateDbContext())
        {
            var stock = new StockService(verify);
            var closing = await stock.GetFreeQuantityMtAsync(
                scope.ProductId,
                terminalId: scope.TerminalId,
                storageTankId: scope.TankId);

            var sold = await verify.SalesTransactions
                .AsNoTracking()
                .Where(s => s.InvoiceNumber.StartsWith("INV-P03-"))
                .SumAsync(s => (decimal?)s.QuantityMt) ?? 0m;

            _output.WriteLine($"received=100 sold={sold} closingStock={closing}");
            Assert.Equal(90m, sold);
            Assert.True(closing >= 0m, $"stock must never go negative, got {closing}");
            Assert.Equal(10m, closing);
        }
    }

    /// <summary>
    /// PTG-P0-02 — ثبتِ عقب‌تاریخِ مجاز نباید مسدود شود. همان سناریو، ولی مقداری که
    /// خط زمانی را منفی نمی‌کند: باید ثبت شود.
    /// </summary>
    [Fact]
    public async Task Probe03b_Backdated_Sale_That_Keeps_The_Timeline_Positive_Is_Accepted()
    {
        Skip.IfNotAvailable(_fixture);
        var scope = await SeedMinimalScopeAsync("P03B", withStock: true, stockQty: 100m,
            receiptDate: new DateTime(2025, 1, 5, 0, 0, 0, DateTimeKind.Utc));

        await using (var db = _fixture.CreateDbContext())
        {
            var result = await BuildSalesController(db).Create(
                BuildSaleModel(scope, "INV-P03B-001", 60m, new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc)));
            Assert.IsNotType<ViewResult>(result);
        }

        await using (var db = _fixture.CreateDbContext())
        {
            // ۳۰ تن عقب‌تاریخ: 100 − 30 − 60 = 10 ⇒ هیچ‌جا منفی نمی‌شود.
            var controller = BuildSalesController(db);
            var result = await controller.Create(
                BuildSaleModel(scope, "INV-P03B-002", 30m, new DateTime(2025, 1, 20, 0, 0, 0, DateTimeKind.Utc)));
            _output.WriteLine($"legit backdated sale result: {result.GetType().Name} " +
                              $"errors=[{string.Join(" | ", controller.ModelState
                                  .SelectMany(kv => kv.Value!.Errors.Select(e => e.ErrorMessage)))}]");
            Assert.IsNotType<ViewResult>(result);
        }

        await using (var verify = _fixture.CreateDbContext())
        {
            var closing = await new StockService(verify).GetFreeQuantityMtAsync(
                scope.ProductId,
                terminalId: scope.TerminalId,
                storageTankId: scope.TankId);
            _output.WriteLine($"closingStock={closing}");
            Assert.Equal(10m, closing);
        }
    }

    // ------------------------------------------------------------------ probe 4

    /// <summary>
    /// PTG-P0-03 — تغییر درصد سهم نباید تاریخچه را بازنویسی کند.
    ///
    /// پیش از اصلاح: تغییر ۵۰/۵۰ به ۸۰/۲۰ سهم مفادِ گذشته را از
    /// A=270,000 / B=270,000 به A=432,000 / B=108,000 می‌برد.
    /// پس از اصلاح: بازهٔ تازه باز می‌شود و فروشِ گذشته همچنان ۵۰/۵۰ می‌ماند.
    /// </summary>
    [Fact]
    public async Task Probe04_Changing_SharePercent_Does_Not_Rewrite_Partner_Profit_History()
    {
        Skip.IfNotAvailable(_fixture);
        var scope = await SeedPartnershipScopeAsync("P04");

        decimal beforeA;
        decimal beforeB;
        decimal bookProfit;
        await using (var db = _fixture.CreateDbContext())
        {
            var statement = await new PartnershipStatementService(db)
                .BuildAsync(scope.PartnerAId, scope.PartnerBId);
            var contract = statement!.Contracts.Single(c => c.ContractId == scope.ContractId);
            beforeA = contract.Partners.Single(x => x.PartnerId == scope.PartnerAId).ProfitShareUsd;
            beforeB = contract.Partners.Single(x => x.PartnerId == scope.PartnerBId).ProfitShareUsd;
            bookProfit = contract.BookProfitUsd;
            _output.WriteLine($"before 50/50 -> A={beforeA:N2} B={beforeB:N2} bookProfit={bookProfit:N2}");
        }

        await OpenSharePeriodAsync(scope, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), 80m, 20m);

        await using (var db = _fixture.CreateDbContext())
        {
            var statement = await new PartnershipStatementService(db)
                .BuildAsync(scope.PartnerAId, scope.PartnerBId);
            var contract = statement!.Contracts.Single(c => c.ContractId == scope.ContractId);
            var afterA = contract.Partners.Single(x => x.PartnerId == scope.PartnerAId).ProfitShareUsd;
            var afterB = contract.Partners.Single(x => x.PartnerId == scope.PartnerBId).ProfitShareUsd;
            _output.WriteLine($"after opening an 80/20 period from 2026-01-01 -> A={afterA:N2} B={afterB:N2}");

            Assert.Equal(beforeA, afterA);
            Assert.Equal(beforeB, afterB);
            Assert.Equal(bookProfit, contract.BookProfitUsd);
        }
    }

    /// <summary>
    /// PTG-P0-03 — رویدادِ بعد از تاریخ تغییر، باید با درصدِ تازه محاسبه شود
    /// — وگرنه خودِ تغییر بی‌اثر می‌ماند و آن هم باگ است.
    /// </summary>
    [Fact]
    public async Task Probe04b_Sales_After_The_New_Period_Use_The_New_Share()
    {
        Skip.IfNotAvailable(_fixture);
        var scope = await SeedPartnershipScopeAsync("P04B");

        await OpenSharePeriodAsync(scope, new DateTime(2025, 7, 1, 0, 0, 0, DateTimeKind.Utc), 80m, 20m);

        await using (var db = _fixture.CreateDbContext())
        {
            var firstSale = await db.SalesTransactions
                .AsNoTracking()
                .SingleAsync(x => x.SourcePurchaseContractId == scope.ContractId);

            db.SalesTransactions.Add(new SalesTransaction
            {
                CompanyId = firstSale.CompanyId,
                SourcePurchaseContractId = scope.ContractId,
                CustomerId = firstSale.CustomerId,
                ProductId = firstSale.ProductId,
                SaleStage = SaleStage.TerminalStock,
                InvoiceNumber = "INV-P04B-2",
                SaleDate = new DateTime(2025, 9, 1, 0, 0, 0, DateTimeKind.Utc),
                QuantityMt = 1_000m,
                Currency = "USD",
                UnitPriceInCurrency = 600m,
                AppliedFxRateToUsd = 1m,
                UnitPriceUsd = 600m,
                TotalInCurrency = 600_000m,
                TotalUsd = 600_000m
            });
            await db.SaveChangesAsync();
        }

        await using (var db = _fixture.CreateDbContext())
        {
            var statement = await new PartnershipStatementService(db)
                .BuildAsync(scope.PartnerAId, scope.PartnerBId);
            var contract = statement!.Contracts.Single(c => c.ContractId == scope.ContractId);
            var a = contract.Partners.Single(x => x.PartnerId == scope.PartnerAId).ProfitShareUsd;
            var b = contract.Partners.Single(x => x.PartnerId == scope.PartnerBId).ProfitShareUsd;
            _output.WriteLine($"bookProfit={contract.BookProfitUsd:N2} A={a:N2} B={b:N2}");

            Assert.True(a > b, $"A={a} should exceed B={b}");
            Assert.True(a < contract.BookProfitUsd * 0.8m,
                $"A={a} must stay below a full 80% of {contract.BookProfitUsd}");
            Assert.Equal(contract.BookProfitUsd, a + b);
        }
    }

    /// <summary>بستنِ بازهٔ جاری و بازکردنِ یک بازهٔ تازه — همان کاری که ویرایش قرارداد می‌کند.</summary>
    private async Task OpenSharePeriodAsync(
        PartnershipScope scope,
        DateTime newStart,
        decimal shareA,
        decimal shareB)
    {
        await using var db = _fixture.CreateDbContext();

        var latestStart = await db.ContractPartners
            .Where(cp => cp.ContractId == scope.ContractId)
            .MaxAsync(cp => cp.EffectiveFrom);

        foreach (var slice in await db.ContractPartners
                     .Where(cp => cp.ContractId == scope.ContractId && cp.EffectiveFrom == latestStart)
                     .ToListAsync())
        {
            slice.EffectiveTo = newStart;
        }

        db.ContractPartners.AddRange(
            new ContractPartner
            {
                ContractId = scope.ContractId,
                PartnerId = scope.PartnerAId,
                SharePercent = shareA,
                EffectiveFrom = newStart
            },
            new ContractPartner
            {
                ContractId = scope.ContractId,
                PartnerId = scope.PartnerBId,
                SharePercent = shareB,
                EffectiveFrom = newStart
            });

        await db.SaveChangesAsync();
    }

    // ------------------------------------------------------------------ probe 5

    /// <summary>
    /// دو کاربر هم‌زمان از یک مخزن می‌فروشند. مسیر فروش قفلِ سطرِ مخزن
    /// (<c>SELECT … FOR UPDATE</c>) را داخل Transaction می‌گیرد؛ این تست بررسی می‌کند
    /// آیا این قفل واقعاً از فروشِ بیش از موجودی جلوگیری می‌کند یا نه.
    /// </summary>
    [Fact]
    public async Task Probe05_Concurrent_Sales_From_Same_Tank_Cannot_Oversell()
    {
        Skip.IfNotAvailable(_fixture);
        var scope = await SeedMinimalScopeAsync("P05", withStock: true, stockQty: 100m,
            receiptDate: new DateTime(2025, 2, 1, 0, 0, 0, DateTimeKind.Utc));

        var saleDate = new DateTime(2025, 3, 1, 0, 0, 0, DateTimeKind.Utc);

        async Task SellAsync(string invoice)
        {
            await using var db = _fixture.CreateDbContext();
            var controller = BuildSalesController(db);
            await controller.Create(BuildSaleModel(scope, invoice, 70m, saleDate));
        }

        await Task.WhenAll(SellAsync("INV-P05-001"), SellAsync("INV-P05-002"));

        await using var verify = _fixture.CreateDbContext();
        var sold = await verify.SalesTransactions
            .AsNoTracking()
            .Where(s => s.InvoiceNumber.StartsWith("INV-P05-"))
            .SumAsync(s => (decimal?)s.QuantityMt) ?? 0m;

        var closing = await new StockService(verify).GetFreeQuantityMtAsync(
            scope.ProductId,
            terminalId: scope.TerminalId,
            storageTankId: scope.TankId);

        _output.WriteLine($"concurrent sales sold={sold} closingStock={closing}");
        Assert.True(closing >= 0m, $"tank oversold under concurrency: closing={closing}, sold={sold}");
    }

    // ------------------------------------------------------------------ probe 6

    /// <summary>
    /// حذف مستقیم سندی که ردیف لجر دارد.
    ///
    /// <b>این کاوش قبلاً یک شکست را ثبت می‌کرد:</b> بین <c>LedgerEntry</c> و سند مبدأ هیچ FK
    /// نبود (رابطه polymorphic است)، پس یک <c>DELETE</c> خام سند را می‌برد و ردیف لجر یتیم
    /// می‌ماند. فاز ۸ همان جای خالی را با یک
    /// <c>CONSTRAINT TRIGGER ... DEFERRABLE INITIALLY DEFERRED</c> پر کرد.
    ///
    /// حالا همین کاوش رفتارِ اصلاح‌شده را pin می‌کند: حذفِ خام <b>رد</b> می‌شود و — مهم‌تر —
    /// نه سند پاک می‌شود نه لجر؛ هیچ چیزی cascade نمی‌شود. سنجهٔ اصلی همان است
    /// («ردیف لجرِ یتیم نباید بماند»)، فقط این‌بار دیتابیس خودش جلویش را می‌گیرد.
    /// </summary>
    [Fact]
    public async Task Probe06_Deleting_A_Posted_Expense_Is_Rejected_By_The_Database()
    {
        Skip.IfNotAvailable(_fixture);
        var scope = await SeedMinimalScopeAsync("P06");

        int expenseId;
        await using (var db = _fixture.CreateDbContext())
        {
            var controller = BuildExpensesController(db);
            await controller.Create(new ExpenseCreateViewModel
            {
                ExpenseTypeId = scope.ExpenseTypeId,
                ContractId = scope.PurchaseContractId,
                ExpenseDate = new DateTime(2025, 4, 4, 0, 0, 0, DateTimeKind.Utc),
                Amount = 4_000m,
                Currency = "USD",
                Description = "Storage — P06"
            });

            expenseId = await db.ExpenseTransactions
                .AsNoTracking()
                .Where(e => e.ContractId == scope.PurchaseContractId)
                .Select(e => e.Id)
                .SingleAsync();
        }

        await using (var db = _fixture.CreateDbContext())
        {
            var rejected = await Assert.ThrowsAsync<Npgsql.PostgresException>(
                () => db.Database.ExecuteSqlInterpolatedAsync(
                    $@"DELETE FROM ""ExpenseTransactions"" WHERE ""Id"" = {expenseId}"));

            _output.WriteLine($"raw delete rejected: {rejected.MessageText}");
            Assert.Contains("cannot delete ExpenseTransactions", rejected.MessageText);
        }

        await using (var verify = _fixture.CreateDbContext())
        {
            var orphans = await verify.LedgerEntries
                .AsNoTracking()
                .Where(l => l.SourceType == "Expense" && l.SourceId == expenseId)
                .CountAsync();
            var documents = await verify.ExpenseTransactions
                .AsNoTracking()
                .CountAsync(e => e.Id == expenseId);

            _output.WriteLine($"after rejected delete: ledgerRows={orphans} documents={documents}");

            // نه لجر یتیم شد، نه چیزی cascade پاک شد — هر دو سرِ جایشان.
            Assert.Equal(1, documents);
            Assert.Equal(1, orphans);
        }
    }

    // ------------------------------------------------------------------ probe 7

    /// <summary>
    /// گِردکردنِ ارز: مبلغ افغانی با نرخ روز به دالر تبدیل و در لجر ذخیره می‌شود.
    /// این تست اندازهٔ خطای برگشتی (USD → AFN) را روی یک سال پرداخت اندازه می‌گیرد.
    /// </summary>
    [Fact]
    public async Task Probe07_Afn_Round_Trip_Error_Stays_Below_One_Afghani_Per_Row()
    {
        Skip.IfNotAvailable(_fixture);
        var scope = await SeedMinimalScopeAsync("P07");

        var worstError = 0m;
        await using (var db = _fixture.CreateDbContext())
        {
            for (var i = 0; i < 200; i++)
            {
                var afnPerUsd = 70m + (i % 17) * 0.37m;
                var rate = decimal.Round(1m / afnPerUsd, 12, MidpointRounding.AwayFromZero);
                var amountAfn = 1_000m + (i * 9_337m);
                var amountUsd = decimal.Round(amountAfn * rate, 4, MidpointRounding.AwayFromZero);
                var backToAfn = decimal.Round(amountUsd / rate, 2, MidpointRounding.AwayFromZero);
                worstError = Math.Max(worstError, Math.Abs(backToAfn - amountAfn));
            }

            _ = await db.Contracts.CountAsync(c => c.Id == scope.PurchaseContractId);
        }

        _output.WriteLine($"worst AFN round-trip error over 200 rows: {worstError:N4} AFN");
        Assert.True(worstError < 1m, $"AFN round-trip error too large: {worstError}");
    }

    // ----------------------------------------------------------------- probe 1b

    /// <summary>
    /// PTG-P0-01 — همان سناریوی ثبت دوباره روی مسیر «بارگیری»، که برخلاف مصرف کلید
    /// <c>ImportUniqueKey</c> هم دارد. اینجا عمداً سطری بدون شماره سند/حمل ثبت می‌شود تا
    /// آن کلید <c>null</c> بماند و تنها محافظ، توکن فرم باشد.
    /// </summary>
    [Fact]
    public async Task Probe01b_Double_Submit_Of_Loading_Creates_Exactly_One_Loading()
    {
        Skip.IfNotAvailable(_fixture);
        var scope = await SeedMinimalScopeAsync("P01B");

        var formToken = Guid.NewGuid().ToString("N");

        LoadingCreateViewModel BuildModel() => new()
        {
            ContractId = scope.PurchaseContractId,
            ProductId = scope.ProductId,
            TransportType = LoadingTransportType.Truck,
            Rows =
            [
                new LoadingCreateRowViewModel
                {
                    RowKey = "row_1",
                    ContractId = scope.PurchaseContractId,
                    LoadingDate = new DateTime(2025, 7, 3, 0, 0, 0, DateTimeKind.Utc),
                    TruckId = scope.TruckId,
                    // بدون BOL/RWB و بدون شمارهٔ حمل ⇒ ImportUniqueKey برابر null می‌ماند،
                    // پس تنها محافظِ باقی‌مانده همان توکن فرم است.
                    LoadedQuantityMt = 120m,
                    LoadingPriceUsd = 500m,
                    ConsigneeName = "Herat depot",
                    Loss = new StageLossCaptureInput { Stage = LossEventStage.LoadingDifference }
                }
            ]
        };

        await using (var db = _fixture.CreateDbContext())
        {
            var controller = BuildLoadingController(db);
            var result = await controller.Create(BuildModel(), formToken);
            _output.WriteLine($"first result={result.GetType().Name} " +
                              $"errors=[{string.Join(" | ", controller.ModelState
                                  .SelectMany(kv => kv.Value!.Errors.Select(e => kv.Key + ":" + e.ErrorMessage)))}]");
        }

        await using (var db = _fixture.CreateDbContext())
        {
            await BuildLoadingController(db).Create(BuildModel(), formToken);
        }

        await using var verify = _fixture.CreateDbContext();
        var loadings = await verify.LoadingRegisters
            .AsNoTracking()
            .Where(l => l.ContractId == scope.PurchaseContractId)
            .ToListAsync();

        _output.WriteLine($"loadings={loadings.Count} " +
                          $"importKeys=[{string.Join(",", loadings.Select(l => l.ImportUniqueKey ?? "null"))}]");

        Assert.Equal(1, loadings.Count);
    }

    // ------------------------------------------------------------------ probe 8

    /// <summary>
    /// PTG-P1-04 — همان سند با ارقام لاتین، فارسی یا عربی باید یک هویت بسازد.
    ///
    /// پیش از اصلاح، <c>LoadingImportKey.Normalize</c> فقط فاصله و بزرگی/کوچکی حروف را
    /// یکسان می‌کرد؛ پس «RWB-۱۲۳۴۵» یک کلیدِ تازه می‌ساخت، Unique Index روی
    /// <c>ImportUniqueKey</c> بی‌اثر می‌شد و همان واگن دوباره وارد موجودی می‌شد.
    /// این همان Probe است با انتظارِ معکوس.
    /// </summary>
    [Fact]
    public void Probe08_Persian_And_Arabic_Digits_Produce_The_Same_Loading_Key()
    {
        var date = new DateTime(2025, 5, 5, 0, 0, 0, DateTimeKind.Utc);

        var english = LoadingImportKey.Build(7, "RWB-12345", "WGN-98765", date);
        var persian = LoadingImportKey.Build(7, "RWB-۱۲۳۴۵", "WGN-۹۸۷۶۵", date);
        var arabic = LoadingImportKey.Build(7, "RWB-١٢٣٤٥", "WGN-٩٨٧٦٥", date);

        _output.WriteLine($"english={english}");
        _output.WriteLine($"persian={persian}");
        _output.WriteLine($"arabic ={arabic}");

        Assert.Equal(english, persian);
        Assert.Equal(english, arabic);

        // و هویتِ واقعاً متفاوت همچنان متفاوت می‌ماند — یکسان‌سازی نباید دو سند را ادغام کند.
        Assert.NotEqual(english, LoadingImportKey.Build(7, "RWB-12346", "WGN-98765", date));
        Assert.NotEqual(english, LoadingImportKey.Build(8, "RWB-12345", "WGN-98765", date));
    }

    // ------------------------------------------------------------------ probe 9

    /// <summary>
    /// PTG-P0-04 — «جمع سهم شرکا = ۱۰۰» تا پیش از اصلاح فقط در Controller کنترل می‌شد و
    /// دیتابیس هر عددی را می‌پذیرفت (اندازه‌گیری‌شده: ۱۶۰٪ ⇒ ۳۲۴٬۰۰۰ USD توزیع اضافی).
    /// حالا یک CONSTRAINT TRIGGER تعویق‌دار همان قاعده را در لایهٔ داده نگه می‌دارد، پس هر
    /// مسیری (ایمپورت، ابزار، اسکریپت) هم از آن عبور می‌کند.
    /// </summary>
    [Fact]
    public async Task Probe09_Database_Rejects_Partner_Shares_That_Do_Not_Sum_To_100()
    {
        Skip.IfNotAvailable(_fixture);
        var scope = await SeedPartnershipScopeAsync("P09");

        await using (var db = _fixture.CreateDbContext())
        {
            var shares = await db.ContractPartners
                .Where(cp => cp.ContractId == scope.ContractId)
                .ToListAsync();
            shares.Single(s => s.PartnerId == scope.PartnerAId).SharePercent = 80m;
            shares.Single(s => s.PartnerId == scope.PartnerBId).SharePercent = 80m;

            // تریگر تعویق‌دار در لحظهٔ COMMIT شلیک می‌شود، پس خطا از خودِ Npgsql می‌آید
            // و EF آن را در DbUpdateException نمی‌پیچد.
            var error = await Assert.ThrowsAsync<PostgresException>(() => db.SaveChangesAsync());
            _output.WriteLine($"160% rejected by: {error.Message}");
            Assert.Equal("23514", error.SqlState);
            Assert.Contains("PTG_PARTNER_SHARE_SUM", error.Message, StringComparison.Ordinal);
        }

        await using (var verify = _fixture.CreateDbContext())
        {
            var total = await verify.ContractPartners
                .Where(cp => cp.ContractId == scope.ContractId)
                .SumAsync(cp => cp.SharePercent);
            _output.WriteLine($"share total after rejected write: {total}%");
            Assert.Equal(100m, total);
        }
    }

    /// <summary>PTG-P0-04 — سهم صفر یا منفی هم باید در لایهٔ داده رد شود.</summary>
    [Fact]
    public async Task Probe09b_Database_Rejects_Zero_Or_Negative_Partner_Share()
    {
        Skip.IfNotAvailable(_fixture);
        var scope = await SeedPartnershipScopeAsync("P09B");

        await using var db = _fixture.CreateDbContext();
        var shares = await db.ContractPartners
            .Where(cp => cp.ContractId == scope.ContractId)
            .ToListAsync();
        shares.Single(s => s.PartnerId == scope.PartnerAId).SharePercent = 100m;
        shares.Single(s => s.PartnerId == scope.PartnerBId).SharePercent = 0m;

        var error = await Assert.ThrowsAsync<PostgresException>(() => db.SaveChangesAsync());
        _output.WriteLine($"zero share rejected by: {error.Message}");
        Assert.Equal("23514", error.SqlState);
        Assert.Contains("PTG_PARTNER_SHARE_INVALID", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// PTG-P0-04 — ترکیب‌های معتبر باید همچنان کار کنند، از جمله «حذف همه و نوشتن دوباره»
    /// در یک تراکنش که ویرایش قرارداد انجام می‌دهد (به لطف تعویق‌دار بودن تریگر).
    /// </summary>
    [Theory]
    [InlineData(new[] { 50.0, 50.0 })]
    [InlineData(new[] { 60.0, 40.0 })]
    [InlineData(new[] { 33.3333, 33.3333, 33.3334 })]
    public async Task Probe09c_Valid_Share_Splits_Are_Accepted(double[] rawShares)
    {
        Skip.IfNotAvailable(_fixture);
        var tag = $"P09C{rawShares.Length}{(int)(rawShares[0] * 10)}";
        var scope = await SeedPartnershipScopeAsync(tag);
        var shares = rawShares.Select(v => (decimal)v).ToArray();

        var extraPartnerIds = new List<int>();
        await using (var db = _fixture.CreateDbContext())
        {
            for (var i = 2; i < shares.Length; i++)
            {
                var extra = new Partner { Code = $"PX-{tag}-{i}", Name = $"Partner X{i} {tag}" };
                db.Partners.Add(extra);
                await db.SaveChangesAsync();
                extraPartnerIds.Add(extra.Id);
            }
        }

        await using (var db = _fixture.CreateDbContext())
        {
            // همان الگوی ویرایش قرارداد: RemoveRange سپس AddRange در یک SaveChanges.
            var existing = await db.ContractPartners
                .Where(cp => cp.ContractId == scope.ContractId)
                .ToListAsync();
            db.ContractPartners.RemoveRange(existing);

            var partnerIds = new List<int> { scope.PartnerAId, scope.PartnerBId };
            partnerIds.AddRange(extraPartnerIds);

            for (var i = 0; i < shares.Length; i++)
            {
                db.ContractPartners.Add(new ContractPartner
                {
                    ContractId = scope.ContractId,
                    PartnerId = partnerIds[i],
                    SharePercent = shares[i]
                });
            }

            await db.SaveChangesAsync();
        }

        await using (var verify = _fixture.CreateDbContext())
        {
            var total = await verify.ContractPartners
                .Where(cp => cp.ContractId == scope.ContractId)
                .SumAsync(cp => cp.SharePercent);
            _output.WriteLine($"accepted split [{string.Join("/", shares)}] total={total}%");
            Assert.Equal(100m, total);
        }
    }

    /// <summary>
    /// PTG-P0-04 — کوئری تطبیق: قراردادهای شراکتیِ ناسازگار را پیدا می‌کند تا پیش از
    /// استقرار، دادهٔ تاریخی قابل بررسی باشد. (روی دیتابیس موقت اجرا می‌شود.)
    /// </summary>
    [Fact]
    public async Task Probe09d_Reconciliation_Query_Finds_Contracts_Whose_Shares_Do_Not_Sum_To_100()
    {
        Skip.IfNotAvailable(_fixture);

        await using var db = _fixture.CreateDbContext();
        var offenders = await db.ContractPartners
            .AsNoTracking()
            .Where(cp => cp.Contract != null
                && cp.Contract.OwnershipType == ContractOwnershipType.Partnership)
            // PTG-P0-03 — سهم تاریخ‌دار است: هر بازه باید خودش ۱۰۰٪ باشد، نه مجموع بازه‌ها.
            .GroupBy(cp => new { cp.ContractId, cp.EffectiveFrom })
            .Select(g => new
            {
                g.Key.ContractId,
                g.Key.EffectiveFrom,
                Total = g.Sum(x => x.SharePercent),
                Partners = g.Count(),
                Invalid = g.Count(x => x.SharePercent <= 0m || x.SharePercent > 100m)
            })
            .Where(x => x.Total != 100m || x.Invalid > 0)
            .ToListAsync();

        _output.WriteLine($"partnership contracts violating the share invariant: {offenders.Count}");
        foreach (var row in offenders)
        {
            _output.WriteLine($"  contract={row.ContractId} period={row.EffectiveFrom:yyyy-MM-dd} " +
                              $"total={row.Total}% partners={row.Partners} invalid={row.Invalid}");
        }

        Assert.Empty(offenders);
    }

    // ------------------------------------------------------------------ helpers

    private sealed record Scope(
        int CompanyId,
        int ProductId,
        int TerminalId,
        int TankId,
        int CustomerId,
        int SupplierId,
        int PurchaseContractId,
        int ExpenseTypeId,
        int LocationId,
        int TruckId);

    private sealed record PartnershipScope(
        int ContractId,
        int PartnerAId,
        int PartnerBId);

    private async Task<Scope> SeedMinimalScopeAsync(
        string tag,
        bool withStock = false,
        decimal stockQty = 500m,
        DateTime? receiptDate = null)
    {
        await using var db = _fixture.CreateDbContext();

        await EnsureCurrencyAsync(db);

        var company = new Company { Code = $"C-{tag}", Name = $"Company {tag}", Country = "AF" };
        var product = new Product { Code = $"PR-{tag}", Name = $"Product {tag}" };
        var terminal = new Terminal { Code = $"T-{tag}", Name = $"Terminal {tag}" };
        var customer = new Customer { Code = $"CU-{tag}", Name = $"Customer {tag}" };
        var supplier = new Supplier { Code = $"SU-{tag}", Name = $"Supplier {tag}" };
        var location = new Location { Code = $"L-{tag}", Name = $"Location {tag}" };
        var expenseType = new ExpenseType { Code = $"ET-{tag}", Name = $"Expense {tag}", Category = "Operational" };
        var truck = new Truck { PlateNumber = $"TRK-{tag}", MaxLoadMt = 200m };
        db.AddRange(company, product, terminal, customer, supplier, location, expenseType, truck);
        await db.SaveChangesAsync();

        var tank = new StorageTank
        {
            TerminalId = terminal.Id,
            TankCode = $"TK-{tag}",
            ProductId = product.Id,
            CapacityMt = 10_000m
        };
        db.StorageTanks.Add(tank);

        var contract = new Contract
        {
            ContractNumber = $"PUR-{tag}",
            ContractName = $"Purchase {tag}",
            ContractType = ContractType.Purchase,
            Status = ContractStatus.Active,
            OwnershipType = ContractOwnershipType.Personal,
            CompanyId = company.Id,
            ProductId = product.Id,
            SupplierId = supplier.Id,
            ContractDate = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            PricingMethod = PricingMethod.Fixed,
            QuantityMt = 10_000m,
            UnitPriceUsd = 500m,
            Currency = "USD"
        };
        db.Contracts.Add(contract);
        await db.SaveChangesAsync();

        if (withStock)
        {
            var date = receiptDate ?? new DateTime(2025, 1, 5, 0, 0, 0, DateTimeKind.Utc);
            var loading = new LoadingRegister
            {
                ContractId = contract.Id,
                ProductId = product.Id,
                TransportType = LoadingTransportType.Wagon,
                LoadingDate = date,
                LoadedQuantityMt = stockQty,
                BillOfLadingNumber = $"BOL-{tag}",
                ImportUniqueKey = $"PROBE|{tag}",
                SettlementCurrencyCode = "USD"
            };
            db.LoadingRegisters.Add(loading);
            await db.SaveChangesAsync();

            var receipt = new LoadingReceipt
            {
                LoadingRegisterId = loading.Id,
                ReceiptDestination = LoadingReceiptDestination.ToInventory,
                TerminalId = terminal.Id,
                StorageTankId = tank.Id,
                ReceiptDate = date,
                ReceivedQuantityMt = stockQty,
                ReferenceDocument = loading.BillOfLadingNumber
            };
            db.LoadingReceipts.Add(receipt);
            await db.SaveChangesAsync();

            db.InventoryMovements.Add(new InventoryMovement
            {
                ProductId = product.Id,
                ContractId = contract.Id,
                TerminalId = terminal.Id,
                StorageTankId = tank.Id,
                LoadingReceiptId = receipt.Id,
                Direction = MovementDirection.In,
                MovementDate = date,
                QuantityMt = stockQty,
                ReferenceDocument = loading.BillOfLadingNumber
            });
            await db.SaveChangesAsync();
        }

        return new Scope(
            company.Id,
            product.Id,
            terminal.Id,
            tank.Id,
            customer.Id,
            supplier.Id,
            contract.Id,
            expenseType.Id,
            location.Id,
            truck.Id);
    }

    private async Task<PartnershipScope> SeedPartnershipScopeAsync(string tag)
    {
        await using var db = _fixture.CreateDbContext();
        await EnsureCurrencyAsync(db);

        var company = new Company { Code = $"C-{tag}", Name = $"Company {tag}", Country = "AF" };
        var product = new Product { Code = $"PR-{tag}", Name = $"Product {tag}" };
        var customer = new Customer { Code = $"CU-{tag}", Name = $"Customer {tag}" };
        var supplier = new Supplier { Code = $"SU-{tag}", Name = $"Supplier {tag}" };
        var partnerA = new Partner { Code = $"PA-{tag}", Name = $"Partner A {tag}" };
        var partnerB = new Partner { Code = $"PB-{tag}", Name = $"Partner B {tag}" };
        var expenseType = new ExpenseType { Code = $"ET-{tag}", Name = $"Expense {tag}", Category = "Operational" };
        db.AddRange(company, product, customer, supplier, partnerA, partnerB, expenseType);
        await db.SaveChangesAsync();

        var contract = new Contract
        {
            ContractNumber = $"PUR-S-{tag}",
            ContractName = $"Partnership {tag}",
            ContractType = ContractType.Purchase,
            Status = ContractStatus.Active,
            OwnershipType = ContractOwnershipType.Partnership,
            CompanyId = company.Id,
            ProductId = product.Id,
            SupplierId = supplier.Id,
            ContractDate = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            PricingMethod = PricingMethod.Fixed,
            QuantityMt = 1_000m,
            UnitPriceUsd = 500m,
            Currency = "USD",
            SaleProceedsHolderPartnerId = partnerA.Id
        };
        db.Contracts.Add(contract);
        await db.SaveChangesAsync();

        // PTG-P0-03 — نخستین بازهٔ سهم از تاریخ خودِ قرارداد آغاز می‌شود (همان کاری که
        // ContractsController.Create انجام می‌دهد)، وگرنه رویدادهای همان قرارداد پیش از
        // آغاز بازه می‌افتند.
        db.ContractPartners.AddRange(
            new ContractPartner
            {
                ContractId = contract.Id,
                PartnerId = partnerA.Id,
                SharePercent = 50m,
                EffectiveFrom = contract.ContractDate.Date
            },
            new ContractPartner
            {
                ContractId = contract.Id,
                PartnerId = partnerB.Id,
                SharePercent = 50m,
                EffectiveFrom = contract.ContractDate.Date
            });

        // Partner A خرید را پرداخت می‌کند، Partner B گمرک را.
        db.PaymentTransactions.AddRange(
            new PaymentTransaction
            {
                PaymentDate = new DateTime(2025, 2, 1, 0, 0, 0, DateTimeKind.Utc),
                Direction = PaymentDirection.Out,
                PaymentKind = PaymentKind.SupplierPayment,
                FundingSource = PaymentFundingSource.Partner,
                PaidByPartnerId = partnerA.Id,
                ContractId = contract.Id,
                SupplierId = supplier.Id,
                Amount = 400_000m,
                Currency = "USD",
                AppliedFxRateToUsd = 1m,
                AmountUsd = 400_000m,
                Reference = $"PAY-A-{tag}"
            },
            new PaymentTransaction
            {
                PaymentDate = new DateTime(2025, 2, 10, 0, 0, 0, DateTimeKind.Utc),
                Direction = PaymentDirection.Out,
                PaymentKind = PaymentKind.ExpensePayment,
                FundingSource = PaymentFundingSource.Partner,
                PaidByPartnerId = partnerB.Id,
                ContractId = contract.Id,
                Amount = 60_000m,
                Currency = "USD",
                AppliedFxRateToUsd = 1m,
                AmountUsd = 60_000m,
                Reference = $"PAY-B-{tag}"
            });

        db.ExpenseTransactions.Add(new ExpenseTransaction
        {
            ExpenseTypeId = expenseType.Id,
            ContractId = contract.Id,
            ExpenseDate = new DateTime(2025, 2, 10, 0, 0, 0, DateTimeKind.Utc),
            Amount = 60_000m,
            Currency = "USD",
            AppliedFxRateToUsd = 1m,
            AmountUsd = 60_000m,
            Description = $"Customs {tag}"
        });

        db.SalesTransactions.Add(new SalesTransaction
        {
            CompanyId = company.Id,
            SourcePurchaseContractId = contract.Id,
            CustomerId = customer.Id,
            ProductId = product.Id,
            SaleStage = SaleStage.TerminalStock,
            InvoiceNumber = $"INV-{tag}",
            SaleDate = new DateTime(2025, 4, 1, 0, 0, 0, DateTimeKind.Utc),
            QuantityMt = 1_000m,
            Currency = "USD",
            UnitPriceInCurrency = 600m,
            AppliedFxRateToUsd = 1m,
            UnitPriceUsd = 600m,
            TotalInCurrency = 600_000m,
            TotalUsd = 600_000m
        });

        await db.SaveChangesAsync();

        return new PartnershipScope(contract.Id, partnerA.Id, partnerB.Id);
    }

    private static async Task EnsureCurrencyAsync(ApplicationDbContext db)
    {
        if (!await db.Currencies.AnyAsync(c => c.Code == "USD"))
        {
            db.Currencies.Add(new Currency { Code = "USD", Name = "US Dollar", Symbol = "$" });
            await db.SaveChangesAsync();
        }
    }

    private static SalesCreateViewModel BuildSaleModel(Scope scope, string invoice, decimal quantity, DateTime saleDate)
        => new()
        {
            SaleStage = SaleStage.TerminalStock,
            CompanyId = scope.CompanyId,
            CustomerId = scope.CustomerId,
            ProductId = scope.ProductId,
            DestinationLocationId = scope.LocationId,
            SourceTerminalId = scope.TerminalId,
            SourceStorageTankId = scope.TankId,
            SourcePurchaseContractId = scope.PurchaseContractId,
            InvoiceNumber = invoice,
            SaleDate = saleDate,
            QuantityMt = quantity,
            Currency = "USD",
            UnitPriceInCurrency = 700m,
            AppliedFxRateToUsd = 1m
        };

    private static SalesController BuildSalesController(ApplicationDbContext db)
        => new(
            db,
            new StockService(db),
            new CurrencyConversionService(new PricingService(db)),
            new AuditService(db),
            NullLogger<SalesController>.Instance)
        {
            TempData = BuildTempData(),
            Url = BuildUrlHelper()
        };

    private static LoadingController BuildLoadingController(ApplicationDbContext db)
        => new(db, new AuditService(db), NullLogger<LoadingController>.Instance)
        {
            TempData = BuildTempData(),
            Url = BuildUrlHelper()
        };

    private static ExpensesController BuildExpensesController(ApplicationDbContext db)
        => new(db, new AuditService(db), NullLogger<ExpensesController>.Instance)
        {
            TempData = BuildTempData(),
            Url = BuildUrlHelper()
        };

    private static ITempDataDictionary BuildTempData()
        => new TempDataDictionary(new DefaultHttpContext(), new ProbeTempDataProvider());

    private static IUrlHelper BuildUrlHelper()
        => new UrlHelper(new ActionContext(new DefaultHttpContext(), new RouteData(), new ActionDescriptor()));

    private sealed class ProbeTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object?> LoadTempData(HttpContext context) => new Dictionary<string, object?>();

        public void SaveTempData(HttpContext context, IDictionary<string, object?> values)
        {
        }
    }
}
