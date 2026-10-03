using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Controllers;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.Finance;
using PTGOilSystem.Web.Models.PartyStatements;
using PTGOilSystem.Web.Models.Reports;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.PartyStatements;
using PTGOilSystem.Web.Services.Time;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// سناریوی «تسویه بین طرف‌حساب‌ها»: احمد (مشتری) ۱۰٬۰۰۰ دالر به شرکت بدهکار است، محمود
/// (تأمین‌کننده) ۱۰٬۰۰۰ دالر از شرکت طلب دارد؛ احمد مستقیماً به محمود می‌پردازد.
/// مانده‌ها از همان موتور رسمی (<see cref="PartyBalanceReadService"/>) خوانده می‌شوند.
/// </summary>
public class PartySettlementsControllerTests
{
    private const int Ahmad = 1;
    private const int Mahmoud = 2;

    [Fact]
    public async Task Settlement_ZeroesBothBalances_AndTouchesNoCashOrBank()
    {
        await using var db = await SeedAsync();
        Assert.Equal(10000m, await BalanceAsync(db, PartyStatementPartyType.Customer, Ahmad));
        Assert.Equal(-10000m, await BalanceAsync(db, PartyStatementPartyType.Supplier, Mahmoud));
        var paymentsBefore = await db.PaymentTransactions.CountAsync();

        var result = await NewController(db).Create(new PartySettlementFormModel
        {
            SettlementDate = new DateTime(2026, 10, 3),
            FromParty = PartySettlementParties.Key(AccountingPartyType.Customer, Ahmad),
            ToParty = PartySettlementParties.Key(AccountingPartyType.Supplier, Mahmoud),
            Amount = 10000m,
            Currency = "USD"
        });

        Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(0m, await BalanceAsync(db, PartyStatementPartyType.Customer, Ahmad));
        Assert.Equal(0m, await BalanceAsync(db, PartyStatementPartyType.Supplier, Mahmoud));

        // صندوق/بانک: هیچ PaymentTransaction (تنها منبع گردش صندوق/بانک) ساخته نشده.
        Assert.Equal(paymentsBefore, await db.PaymentTransactions.CountAsync());

        var settlement = await db.PartySettlements.SingleAsync();
        var lines = await db.LedgerEntries.Where(l => l.SourceType == PartySettlementsController.LedgerSourceType).ToListAsync();
        Assert.Equal(2, lines.Count);
        Assert.Contains(lines, l => l.CustomerId == Ahmad && l.Side == LedgerSide.Debit && l.AmountUsd == 10000m);
        Assert.Contains(lines, l => l.SupplierId == Mahmoud && l.Side == LedgerSide.Debit && l.AmountUsd == 10000m);
        Assert.All(lines, l => Assert.Equal(settlement.Id, l.SourceId));
        Assert.True(await db.AuditLogs.AnyAsync(a => a.EntityName == nameof(PartySettlement) && a.EntityId == settlement.Id));
    }

    [Fact]
    public async Task Cancel_RestoresBothBalancesExactly()
    {
        await using var db = await SeedAsync();
        var controller = NewController(db);
        await controller.Create(new PartySettlementFormModel
        {
            SettlementDate = new DateTime(2026, 10, 3),
            FromParty = PartySettlementParties.Key(AccountingPartyType.Customer, Ahmad),
            ToParty = PartySettlementParties.Key(AccountingPartyType.Supplier, Mahmoud),
            Amount = 10000m,
            Currency = "USD"
        });
        var id = (await db.PartySettlements.SingleAsync()).Id;

        var cancelController = NewController(db);
        await cancelController.Cancel(id, "اشتباه ثبت شد");
        Assert.Null(cancelController.TempData["err"]);

        var settlement = await db.PartySettlements.SingleAsync();
        Assert.Equal(PartySettlementStatus.Cancelled, settlement.Status);
        Assert.Equal("اشتباه ثبت شد", settlement.CancellationReason);
        Assert.Equal(10000m, await BalanceAsync(db, PartyStatementPartyType.Customer, Ahmad));
        Assert.Equal(-10000m, await BalanceAsync(db, PartyStatementPartyType.Supplier, Mahmoud));
        Assert.Equal(4, await db.LedgerEntries.CountAsync(l => l.SourceType == PartySettlementsController.LedgerSourceType));

        // لغو دوباره هیچ سطر تازه‌ای نمی‌سازد.
        await NewController(db).Cancel(id, "دوباره");
        Assert.Equal(4, await db.LedgerEntries.CountAsync(l => l.SourceType == PartySettlementsController.LedgerSourceType));
    }

    [Fact]
    public async Task Create_RejectsSamePartyZeroAmountAndMissingRate()
    {
        await using var db = await SeedAsync();
        var controller = NewController(db);

        var result = await controller.Create(new PartySettlementFormModel
        {
            SettlementDate = new DateTime(2026, 10, 3),
            FromParty = PartySettlementParties.Key(AccountingPartyType.Customer, Ahmad),
            ToParty = PartySettlementParties.Key(AccountingPartyType.Customer, Ahmad),
            Amount = 0m,
            Currency = "AFN"
        });

        Assert.IsType<ViewResult>(result);
        Assert.True(controller.ModelState.ContainsKey(nameof(PartySettlementFormModel.ToParty)));
        Assert.True(controller.ModelState.ContainsKey(nameof(PartySettlementFormModel.Amount)));
        Assert.True(controller.ModelState.ContainsKey(nameof(PartySettlementFormModel.CurrencyPerUsdRate)));
        Assert.Empty(db.PartySettlements);
        Assert.Equal(2, await db.LedgerEntries.CountAsync());
    }

    [Fact]
    public async Task Create_NonUsd_PostsUsdEquivalentWithRate()
    {
        await using var db = await SeedAsync();

        await NewController(db).Create(new PartySettlementFormModel
        {
            SettlementDate = new DateTime(2026, 10, 3),
            FromParty = PartySettlementParties.Key(AccountingPartyType.Customer, Ahmad),
            ToParty = PartySettlementParties.Key(AccountingPartyType.Supplier, Mahmoud),
            Amount = 700000m,
            Currency = "AFN",
            CurrencyPerUsdRate = 70m
        });

        var settlement = await db.PartySettlements.SingleAsync();
        Assert.Equal(10000m, settlement.AmountUsd);
        Assert.Equal(0m, await BalanceAsync(db, PartyStatementPartyType.Customer, Ahmad));
        Assert.Equal(0m, await BalanceAsync(db, PartyStatementPartyType.Supplier, Mahmoud));
    }

    private static async Task<ApplicationDbContext> SeedAsync()
    {
        var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
        db.Currencies.Add(new Currency { Id = 1, Code = "USD", Name = "US Dollar" });
        db.Currencies.Add(new Currency { Id = 2, Code = "AFN", Name = "Afghani" });
        db.Customers.Add(new Customer { Id = Ahmad, Name = "احمد" });
        db.Suppliers.Add(new Supplier { Id = Mahmoud, Name = "محمود" });
        // احمد ۱۰٬۰۰۰ بدهکار (برد به مشتری = Credit)، محمود ۱۰٬۰۰۰ طلبکار (رسید از تأمین‌کننده = Credit).
        db.LedgerEntries.Add(new LedgerEntry
        {
            EntryDate = new DateTime(2026, 9, 1), Side = LedgerSide.Credit, AmountUsd = 10000m,
            SourceType = "Adjustment", SourceId = 1, Description = "opening", CustomerId = Ahmad
        });
        db.LedgerEntries.Add(new LedgerEntry
        {
            EntryDate = new DateTime(2026, 9, 1), Side = LedgerSide.Credit, AmountUsd = 10000m,
            SourceType = "Adjustment", SourceId = 2, Description = "opening", SupplierId = Mahmoud
        });
        await db.SaveChangesAsync();
        return db;
    }

    private static PartySettlementsController NewController(ApplicationDbContext db)
        => new(db, new AuditService(db), new AfghanistanBusinessClock(TimeProvider.System))
        {
            TempData = new TempDataDictionary(new DefaultHttpContext(), new NullTempDataProvider()),
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

    private static async Task<decimal> BalanceAsync(ApplicationDbContext db, PartyStatementPartyType type, int id)
    {
        var rows = await PartyBalanceReadService.CreateDefault(db)
            .GetBalancesAsync(new ManagementReportFilterViewModel(), default, [type]);
        return rows.FirstOrDefault(r => r.PartyId == id)?.ClosingBalanceUsd ?? 0m;
    }

    private sealed class NullTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object?> LoadTempData(HttpContext context) => new Dictionary<string, object?>();

        public void SaveTempData(HttpContext context, IDictionary<string, object?> values)
        {
        }
    }
}
