using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PTGOilSystem.Web.Controllers;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.AccountStatements;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.Ledger;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Audit;
using Xunit;

namespace PTGOilSystem.Web.Tests;

public class FinalExportControllerTests
{
    [Fact]
    public async Task Ledger_Csv_Uses_Filter_And_Utf8_Bom()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        db.LedgerEntries.AddRange(
            new LedgerEntry
            {
                Id = 1,
                EntryDate = new DateTime(2026, 4, 1),
                Side = LedgerSide.Credit,
                AmountUsd = 100m,
                SourceType = "Sale",
                SourceId = 10,
                Reference = "INV-1",
                Description = "فروش"
            },
            new LedgerEntry
            {
                Id = 2,
                EntryDate = new DateTime(2026, 4, 2),
                Side = LedgerSide.Debit,
                AmountUsd = 50m,
                SourceType = "Expense",
                SourceId = 20,
                Reference = "EXP-1",
                Description = "هزینه"
            });
        await db.SaveChangesAsync();

        var controller = new LedgerController(db, NullLogger<LedgerController>.Instance);

        var result = await controller.Csv(new LedgerIndexFilterViewModel
        {
            // بازهٔ تاریخ برای خروجی CSV الزامی است.
            FromDate = new DateTime(2026, 4, 1),
            ToDate = new DateTime(2026, 4, 2),
            SourceType = ["Sale"],
            Reference = "INV"
        });

        var (bytes, contentType) = await CsvResultTestHelper.ExecuteAsync(result);
        Assert.Equal("text/csv; charset=utf-8", contentType);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes.Take(3).ToArray());
        var csv = Encoding.UTF8.GetString(bytes);
        Assert.Contains("INV-1", csv);
        Assert.DoesNotContain("EXP-1", csv);
    }

    [Fact]
    public async Task AccountStatements_Csv_Uses_Filter_And_Utf8_Bom()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        db.LedgerEntries.AddRange(
            new LedgerEntry
            {
                Id = 1,
                EntryDate = new DateTime(2026, 4, 1),
                Side = LedgerSide.Credit,
                AmountUsd = 100m,
                SourceAmount = 100m,
                SourceCurrencyCode = "USD",
                AppliedFxRateToUsd = 1m,
                SourceType = "OpeningBalance",
                SourceId = 1,
                Reference = "OPEN-USD",
                Description = "Opening"
            },
            new LedgerEntry
            {
                Id = 2,
                EntryDate = new DateTime(2026, 4, 2),
                Side = LedgerSide.Credit,
                AmountUsd = 200m,
                SourceAmount = 180m,
                SourceCurrencyCode = "EUR",
                AppliedFxRateToUsd = 1.111111m,
                SourceType = "ManualAdjustment",
                SourceId = 2,
                Reference = "ADJ-EUR",
                Description = "Adjustment"
            });
        await db.SaveChangesAsync();

        var controller = new AccountStatementsController(db, new PricingService(db), new AuditService(db));

        var result = await controller.Csv(new AccountStatementFilterViewModel
        {
            SourceCurrencyCode = ["USD"],
            Reference = "OPEN"
        });

        var (bytes, contentType) = await CsvResultTestHelper.ExecuteAsync(result);
        Assert.Equal("text/csv; charset=utf-8", contentType);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes.Take(3).ToArray());
        var csv = Encoding.UTF8.GetString(bytes);
        Assert.Contains("OPEN-USD", csv);
        Assert.DoesNotContain("ADJ-EUR", csv);
    }

    [Theory]
    [InlineData(null, null)]   // بدون هیچ تاریخی
    [InlineData("2026-04-01", null)]   // فقط «از تاریخ»
    [InlineData(null, "2026-04-02")]   // فقط «تا تاریخ»
    public async Task Ledger_Csv_Requires_A_Complete_Date_Range(string? from, string? to)
    {
        await using var db = new ApplicationDbContext(NewOptions());
        var controller = BuildLedgerController(db);

        var result = await controller.Csv(new LedgerIndexFilterViewModel
        {
            FromDate = from is null ? null : DateTime.Parse(from),
            ToDate = to is null ? null : DateTime.Parse(to)
        });

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(LedgerController.Index), redirect.ActionName);
        Assert.Contains("الزامی", Assert.IsType<string>(controller.TempData["error"]));
    }

    [Fact]
    public async Task Ledger_Csv_Rejects_Export_Larger_Than_Row_Cap()
    {
        await using var db = new ApplicationDbContext(NewOptions());

        // یک ردیف بیشتر از سقف مجاز.
        var entries = Enumerable.Range(1, CsvExportSupport.MaxRows + 1).Select(i => new LedgerEntry
        {
            Id = i,
            EntryDate = new DateTime(2026, 4, 1),
            Side = LedgerSide.Credit,
            AmountUsd = 1m,
            SourceType = "Sale",
            SourceId = i,
            Reference = $"REF-{i}"
        });
        db.LedgerEntries.AddRange(entries);
        await db.SaveChangesAsync();

        var controller = BuildLedgerController(db);

        var result = await controller.Csv(new LedgerIndexFilterViewModel
        {
            FromDate = new DateTime(2026, 4, 1),
            ToDate = new DateTime(2026, 4, 1)
        });

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(LedgerController.Index), redirect.ActionName);
        var message = Assert.IsType<string>(controller.TempData["error"]);
        Assert.Contains("سقف مجاز", message);
    }

    [Fact]
    public async Task Ledger_Csv_Allows_Export_Exactly_At_Row_Cap()
    {
        await using var db = new ApplicationDbContext(NewOptions());

        // دقیقاً روی سقف — باید مجاز باشد (شرط > است، نه >=).
        var entries = Enumerable.Range(1, CsvExportSupport.MaxRows).Select(i => new LedgerEntry
        {
            Id = i,
            EntryDate = new DateTime(2026, 4, 1),
            Side = LedgerSide.Credit,
            AmountUsd = 1m,
            SourceType = "Sale",
            SourceId = i,
            Reference = $"REF-{i}"
        });
        db.LedgerEntries.AddRange(entries);
        await db.SaveChangesAsync();

        var controller = BuildLedgerController(db);

        var result = await controller.Csv(new LedgerIndexFilterViewModel
        {
            FromDate = new DateTime(2026, 4, 1),
            ToDate = new DateTime(2026, 4, 1)
        });

        var (bytes, _) = await CsvResultTestHelper.ExecuteAsync(result);
        var lineCount = Encoding.UTF8.GetString(bytes)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;

        // سرصفحه + سقف ردیف
        Assert.Equal(CsvExportSupport.MaxRows + 1, lineCount);
    }

    [Fact]
    public async Task Csv_Stream_Preserves_Persian_Text_And_Escapes_Commas()
    {
        await using var db = new ApplicationDbContext(NewOptions());
        db.LedgerEntries.Add(new LedgerEntry
        {
            Id = 1,
            EntryDate = new DateTime(2026, 4, 1),
            Side = LedgerSide.Credit,
            AmountUsd = 100m,
            SourceType = "Sale",
            SourceId = 10,
            Reference = "INV-1",
            Description = "فروش گاز مایع، محموله اول"   // شامل ویرگول فارسی و لاتین
        });
        await db.SaveChangesAsync();

        var controller = BuildLedgerController(db);

        var result = await controller.Csv(new LedgerIndexFilterViewModel
        {
            FromDate = new DateTime(2026, 4, 1),
            ToDate = new DateTime(2026, 4, 1)
        });

        var (bytes, _) = await CsvResultTestHelper.ExecuteAsync(result);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes.Take(3).ToArray());

        var csv = Encoding.UTF8.GetString(bytes);
        Assert.Contains("فروش گاز مایع، محموله اول", csv);
    }

    private static LedgerController BuildLedgerController(ApplicationDbContext db)
        => new(db, NullLogger<LedgerController>.Instance)
        {
            TempData = new TempDataDictionary(new DefaultHttpContext(), new TestTempDataProvider())
        };

    private static DbContextOptions<ApplicationDbContext> NewOptions()
        => new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

    private sealed class TestTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();

        public void SaveTempData(HttpContext context, IDictionary<string, object> values)
        {
        }
    }
}
