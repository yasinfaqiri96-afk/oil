using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PTGOilSystem.Web.Controllers;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.Sales;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Ledger;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// PTG-P2-03 — جریانِ اصلاحِ فروش.
///
/// قاعدهٔ اصلی که این تست‌ها نگه می‌دارند: <b>سندِ ثبت‌شده هرگز بازنویسی نمی‌شود.</b>
/// اصلاح یعنی ابطالِ دلیل‌دار + سندِ تازه‌ای که به سند اصلی پیوند دارد. هر چیزی که این
/// زنجیره را بشکند — ابطالِ بی‌دلیل، دو بار ابطال، دو جایگزین برای یک سند — باید رد شود.
/// </summary>
public sealed class SaleCorrectionWorkflowTests
{
    private const int SaleId = 501;

    private static DbContextOptions<ApplicationDbContext> NewDbOptions()
        => new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase($"sale-correction-{Guid.NewGuid():N}")
            .Options;

    private static SalesController BuildController(ApplicationDbContext db)
        => new(db, new StockService(db), new AuditService(db), NullLogger<SalesController>.Instance)
        {
            TempData = new TempDataDictionary(new DefaultHttpContext(), new NullTempDataProvider()),
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
            Url = new UrlHelper(new ActionContext(
                new DefaultHttpContext(),
                new RouteData(),
                new ActionDescriptor())),
        };

    private static async Task<ApplicationDbContext> SeedPostedSaleAsync(DbContextOptions<ApplicationDbContext> options)
    {
        var db = new ApplicationDbContext(options);

        db.Currencies.Add(new Currency { Id = 1, Code = "USD", Name = "US Dollar", Symbol = "$", IsActive = true });
        db.Companies.Add(new Company { Id = 1, Code = "PTG", Name = "PTG" });
        db.Customers.Add(new Customer { Id = 1, Name = "Herat Market" });
        db.Products.Add(new Product { Id = 1, Code = "GO", Name = "Gas Oil" });

        var sale = new SalesTransaction
        {
            Id = SaleId,
            CompanyId = 1,
            CustomerId = 1,
            ProductId = 1,
            SaleStage = SaleStage.InTransit,
            InvoiceNumber = "INV-501",
            SaleDate = new DateTime(2026, 4, 10, 0, 0, 0, DateTimeKind.Utc),
            QuantityMt = 20m,
            Currency = "USD",
            UnitPriceInCurrency = 700m,
            AppliedFxRateToUsd = 1m,
            UnitPriceUsd = 700m,
            TotalInCurrency = 14_000m,
            TotalUsd = 14_000m,
        };
        db.SalesTransactions.Add(sale);
        await db.SaveChangesAsync();

        // سند دفتر کلِ همان فروش — بدون آن، ابطال عمداً انجام نمی‌شود.
        new LedgerPostingService(db).Post(new LedgerPostingRequest
        {
            SourceType = "Sale",
            SourceId = sale.Id,
            EntryDate = sale.SaleDate,
            Side = LedgerSide.Credit,
            AmountUsd = sale.TotalUsd,
            Currency = "USD",
            SourceAmount = sale.TotalInCurrency,
            SourceCurrencyCode = sale.Currency,
            AppliedFxRateToUsd = 1m,
            Description = "ثبت فروش",
            Reference = sale.InvoiceNumber,
            CustomerId = sale.CustomerId,
        });
        await db.SaveChangesAsync();

        return db;
    }

    [Fact]
    public async Task Correct_Page_Shows_The_Original_And_Refuses_An_Already_Cancelled_Sale()
    {
        var options = NewDbOptions();

        await using (var db = await SeedPostedSaleAsync(options))
        {
            var controller = BuildController(db);

            var view = Assert.IsType<ViewResult>(await controller.Correct(SaleId));
            var model = Assert.IsType<SaleCorrectionViewModel>(view.Model);

            Assert.Equal(SaleId, model.SaleId);
            Assert.Equal("INV-501", model.InvoiceNumber);
            Assert.Equal(14_000m, model.TotalUsd);
            Assert.True(model.Version > 0);
        }

        await using (var db = new ApplicationDbContext(options))
        {
            var sale = await db.SalesTransactions.SingleAsync(s => s.Id == SaleId);
            sale.IsCancelled = true;
            await db.SaveChangesAsync();

            var controller = BuildController(db);
            var result = await controller.Correct(SaleId);

            var redirect = Assert.IsType<RedirectToActionResult>(result);
            Assert.Equal(nameof(SalesController.Details), redirect.ActionName);
        }
    }

    [Fact]
    public async Task Cancel_Without_A_Reason_Changes_Nothing()
    {
        var options = NewDbOptions();
        await using var db = await SeedPostedSaleAsync(options);
        var controller = BuildController(db);

        var result = await controller.Cancel(SaleId, cancelReason: "   ");

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(SalesController.Correct), redirect.ActionName);

        var sale = await db.SalesTransactions.AsNoTracking().SingleAsync(s => s.Id == SaleId);
        Assert.False(sale.IsCancelled);
        Assert.Null(sale.CancelReason);

        // و هیچ سطر برگشتی ساخته نشده است.
        Assert.Equal(1, await db.LedgerEntries.CountAsync(l => l.SourceType == "Sale"));
    }

    [Fact]
    public async Task Cancel_With_A_Reason_Records_Who_When_And_Why_And_Reverses_The_Ledger()
    {
        var options = NewDbOptions();
        await using var db = await SeedPostedSaleAsync(options);
        var controller = BuildController(db);

        await controller.Cancel(SaleId, cancelReason: "مقدار اشتباه ثبت شده بود");

        var sale = await db.SalesTransactions.AsNoTracking().SingleAsync(s => s.Id == SaleId);
        Assert.True(sale.IsCancelled);
        Assert.Equal("مقدار اشتباه ثبت شده بود", sale.CancelReason);
        Assert.NotNull(sale.CancelledAtUtc);
        Assert.Null(sale.ReplacementSaleId);

        var ledgers = await db.LedgerEntries.AsNoTracking()
            .Where(l => l.SourceType == "Sale" && l.SourceId == SaleId)
            .ToListAsync();

        // سند اصلی دست‌نخورده مانده و یک سطر معکوس کنارش نشسته است.
        Assert.Equal(2, ledgers.Count);
        Assert.Single(ledgers, l => l.Side == LedgerSide.Credit);
        Assert.Single(ledgers, l => l.Side == LedgerSide.Debit);
        Assert.Equal(0m, ledgers.Sum(l => l.Side == LedgerSide.Credit ? l.AmountUsd : -l.AmountUsd));
    }

    [Fact]
    public async Task Cancelling_Twice_Does_Not_Reverse_Twice()
    {
        var options = NewDbOptions();
        await using var db = await SeedPostedSaleAsync(options);
        var controller = BuildController(db);

        await controller.Cancel(SaleId, cancelReason: "اول");
        await controller.Cancel(SaleId, cancelReason: "دوم");

        var ledgers = await db.LedgerEntries.AsNoTracking()
            .Where(l => l.SourceType == "Sale" && l.SourceId == SaleId)
            .ToListAsync();

        Assert.Equal(2, ledgers.Count);

        var sale = await db.SalesTransactions.AsNoTracking().SingleAsync(s => s.Id == SaleId);
        Assert.Equal("اول", sale.CancelReason);
    }

    [Fact]
    public async Task Cancel_With_Replacement_Sends_The_User_To_A_Prefilled_Linked_Form()
    {
        var options = NewDbOptions();
        await using var db = await SeedPostedSaleAsync(options);
        var controller = BuildController(db);

        var result = await controller.Cancel(SaleId, cancelReason: "قیمت اشتباه", createReplacement: true);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(SalesController.Create), redirect.ActionName);
        Assert.Equal(SaleId, redirect.RouteValues!["correctedFromSaleId"]);

        var view = Assert.IsType<ViewResult>(await controller.Create(correctedFromSaleId: SaleId));
        var model = Assert.IsType<SalesCreateViewModel>(view.Model);

        Assert.Equal(SaleId, model.CorrectedFromSaleId);
        Assert.Equal(20m, model.QuantityMt);
        Assert.Equal(700m, model.UnitPriceInCurrency);
        Assert.Equal(1, model.CustomerId);
    }

    [Fact]
    public async Task A_Replacement_Form_Is_Refused_For_A_Sale_That_Is_Still_Live()
    {
        var options = NewDbOptions();
        await using var db = await SeedPostedSaleAsync(options);
        var controller = BuildController(db);

        var result = await controller.Create(correctedFromSaleId: SaleId);

        Assert.IsType<RedirectToActionResult>(result);
    }

    [Fact]
    public async Task A_Sale_That_Already_Has_A_Replacement_Cannot_Get_A_Second_One()
    {
        var options = NewDbOptions();
        await using var db = await SeedPostedSaleAsync(options);

        var original = await db.SalesTransactions.SingleAsync(s => s.Id == SaleId);
        original.IsCancelled = true;
        original.CancelReason = "اصلاح";
        original.ReplacementSaleId = 999;
        await db.SaveChangesAsync();

        var controller = BuildController(db);
        var result = await controller.Create(correctedFromSaleId: SaleId);

        Assert.IsType<RedirectToActionResult>(result);
    }

    /// <summary>
    /// نگهبانِ ساختاری: مسیر ویرایشِ فروش هرگز نباید فیلدهای مالی را بپذیرد. اگر روزی
    /// کسی این را باز کند، اصلاحِ حسابرسی‌پذیر بی‌معنا می‌شود چون سند بی‌صدا عوض می‌شود.
    /// </summary>
    [Fact]
    public async Task Edit_Still_Refuses_To_Touch_Financial_Fields()
    {
        var options = NewDbOptions();
        await using var db = await SeedPostedSaleAsync(options);
        var controller = BuildController(db);

        await controller.Edit(SaleId, new SalesCreateViewModel
        {
            QuantityMt = 999m,
            UnitPriceInCurrency = 1m,
            Currency = "USD",
            CustomerId = 1,
            ProductId = 1,
            InvoiceNumber = "HACK",
            SaleDate = new DateTime(2026, 4, 10, 0, 0, 0, DateTimeKind.Utc),
            Notes = "یادداشت تازه",
        });

        var sale = await db.SalesTransactions.AsNoTracking().SingleAsync(s => s.Id == SaleId);
        Assert.Equal(20m, sale.QuantityMt);
        Assert.Equal(700m, sale.UnitPriceInCurrency);
        Assert.Equal(14_000m, sale.TotalUsd);
        Assert.Equal("INV-501", sale.InvoiceNumber);
    }

    private sealed class NullTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object?> LoadTempData(HttpContext context) => new Dictionary<string, object?>();

        public void SaveTempData(HttpContext context, IDictionary<string, object?> values)
        {
        }
    }
}
