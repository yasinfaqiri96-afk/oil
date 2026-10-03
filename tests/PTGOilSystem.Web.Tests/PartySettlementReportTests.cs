using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Configuration;
using PTGOilSystem.Web.Controllers;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.Finance;
using PTGOilSystem.Web.Security;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Time;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// گزارش «تسویه بین طرف‌حساب‌ها» فقط رکوردهای PartySettlement را می‌خواند: فیلتر طرف‌حساب و جهت،
/// تاریخ، ارز و وضعیت؛ جمع‌ها فقط از تسویه‌های فعال و همیشه به تفکیک ارز.
/// </summary>
public class PartySettlementReportTests
{
    private const int A = 1;       // مشتری احمد
    private const int B = 2;       // تأمین‌کننده محمود
    private const int Other = 3;   // مشتری کریم

    private static string KeyA => PartySettlementParties.Key(AccountingPartyType.Customer, A);
    private static string KeyB => PartySettlementParties.Key(AccountingPartyType.Supplier, B);

    [Fact]
    public async Task AToB_Shows_Only_Active_Transfers_From_A_To_B()
    {
        await using var db = await SeedAsync();

        var model = await RunAsync(db, new() { PartyA = KeyA, PartyB = KeyB, Direction = PartySettlementReportDirection.AToB });

        Assert.Equal([3, 1], model.Rows.Select(r => r.Id));
        Assert.All(model.Rows, r => Assert.Equal("احمد", r.FromName));
        Assert.Equal("محمود", model.Rows[0].ToName);
        Assert.Equal(2, model.TotalCount);
    }

    [Fact]
    public async Task BToA_Shows_Only_Transfers_From_B_To_A()
    {
        await using var db = await SeedAsync();

        var model = await RunAsync(db, new() { PartyA = KeyA, PartyB = KeyB, Direction = PartySettlementReportDirection.BToA });

        var row = Assert.Single(model.Rows);
        Assert.Equal(2, row.Id);
        Assert.Equal("محمود", row.FromName);
        Assert.Equal("احمد", row.ToName);
    }

    [Fact]
    public async Task Both_Directions_Is_Default_And_Totals_Are_Split_Per_Direction_And_Currency()
    {
        await using var db = await SeedAsync();

        var model = await RunAsync(db, new() { PartyA = KeyA, PartyB = KeyB });

        Assert.Equal(PartySettlementReportDirection.Both, model.Filter.Direction);
        Assert.Equal([3, 2, 1], model.Rows.Select(r => r.Id));
        Assert.Equal([("AFN", 20000m), ("USD", 1000m)], model.AToBTotals.Select(t => (t.Currency, t.Amount)));
        Assert.Equal([("USD", 300m)], model.BToATotals.Select(t => (t.Currency, t.Amount)));
        Assert.Equal([("AFN", 20000m, 1), ("USD", 1300m, 2)], model.Totals.Select(t => (t.Currency, t.Amount, t.Count)));
    }

    [Fact]
    public async Task Different_Currencies_Are_Never_Added_Together()
    {
        await using var db = await SeedAsync();

        var model = await RunAsync(db, new() { PartyA = KeyA });

        Assert.Equal(2, model.Totals.Count);
        Assert.DoesNotContain(model.Totals, t => t.Amount == 21300m || t.Amount == 21000m);
        Assert.Equal(20000m, model.Totals.Single(t => t.Currency == "AFN").Amount);
        Assert.Equal(1300m, model.Totals.Single(t => t.Currency == "USD").Amount);
    }

    [Fact]
    public async Task Date_Filter_Is_Inclusive_On_Both_Ends()
    {
        await using var db = await SeedAsync();

        var model = await RunAsync(db, new()
        {
            PartyA = KeyA, PartyB = KeyB,
            FromDate = new DateTime(2026, 9, 15), ToDate = new DateTime(2026, 9, 20)
        });

        Assert.Equal([3, 2], model.Rows.Select(r => r.Id));
    }

    [Fact]
    public async Task Single_Party_Filter_Matches_Either_Side_Or_The_Chosen_Direction()
    {
        await using var db = await SeedAsync();

        var involvingA = await RunAsync(db, new() { PartyA = KeyA });
        var receivedByB = await RunAsync(db, new() { PartyB = KeyB, Direction = PartySettlementReportDirection.AToB });
        var paidByA = await RunAsync(db, new() { PartyA = KeyA, Direction = PartySettlementReportDirection.AToB });

        Assert.Equal([3, 2, 1], involvingA.Rows.Select(r => r.Id));
        Assert.Equal([3, 5, 1], receivedByB.Rows.Select(r => r.Id));
        Assert.Equal([3, 1], paidByA.Rows.Select(r => r.Id));
    }

    [Fact]
    public async Task Currency_Filter_Limits_Rows_And_Totals()
    {
        await using var db = await SeedAsync();

        var model = await RunAsync(db, new() { Currency = "afn" });

        var row = Assert.Single(model.Rows);
        Assert.Equal(3, row.Id);
        Assert.Equal(70m, row.CurrencyPerUsdRate);
        Assert.Equal([("AFN", 20000m)], model.Totals.Select(t => (t.Currency, t.Amount)));
    }

    [Fact]
    public async Task Cancelled_Settlements_Are_Listed_On_Request_But_Never_Totalled()
    {
        await using var db = await SeedAsync();

        var cancelledOnly = await RunAsync(db, new() { PartyA = KeyA, PartyB = KeyB, Status = PartySettlementReportStatus.Cancelled });
        var all = await RunAsync(db, new() { PartyA = KeyA, PartyB = KeyB, Status = PartySettlementReportStatus.All });
        var active = await RunAsync(db, new() { PartyA = KeyA, PartyB = KeyB });

        var cancelled = Assert.Single(cancelledOnly.Rows);
        Assert.Equal(4, cancelled.Id);
        Assert.Equal(PartySettlementStatus.Cancelled, cancelled.Status);
        Assert.Equal("ثبت اشتباه", cancelled.CancellationReason);
        Assert.Empty(cancelledOnly.Totals);

        Assert.Equal(4, all.TotalCount);
        Assert.Equal(1, all.CancelledCount);
        Assert.Equal(1300m, all.Totals.Single(t => t.Currency == "USD").Amount);
        Assert.DoesNotContain(active.Rows, r => r.Id == 4);
    }

    [Fact]
    public async Task Same_Party_On_Both_Sides_Is_Rejected_Without_Failing()
    {
        await using var db = await SeedAsync();
        var controller = NewController(db);

        var result = Assert.IsType<ViewResult>(await controller.Report(new() { PartyA = KeyA, PartyB = KeyA }));

        Assert.False(controller.ModelState.IsValid);
        var model = Assert.IsType<PartySettlementReportViewModel>(result.Model);
        Assert.Null(model.Filter.PartyB);
        Assert.Equal([3, 2, 1], model.Rows.Select(r => r.Id));
    }

    [Fact]
    public async Task Report_Is_Read_Only()
    {
        await using var db = await SeedAsync();
        var settlements = await db.PartySettlements.AsNoTracking().ToListAsync();
        var ledger = await db.LedgerEntries.CountAsync();
        var payments = await db.PaymentTransactions.CountAsync();
        var cashAccounts = await db.CashAccounts.CountAsync();

        await RunAsync(db, new() { Status = PartySettlementReportStatus.All });

        Assert.False(db.ChangeTracker.HasChanges());
        Assert.Equal(ledger, await db.LedgerEntries.CountAsync());
        Assert.Equal(payments, await db.PaymentTransactions.CountAsync());
        Assert.Equal(cashAccounts, await db.CashAccounts.CountAsync());
        var after = await db.PartySettlements.AsNoTracking().ToListAsync();
        Assert.Equal(
            settlements.Select(s => (s.Id, s.Status, s.Amount, s.AmountUsd)),
            after.Select(s => (s.Id, s.Status, s.Amount, s.AmountUsd)));
    }

    [Theory]
    [InlineData(AuthRoles.Admin, null, true)]
    [InlineData("Clerk", RoleNavigationKeys.Payments, true)]
    [InlineData("Clerk", RoleNavigationKeys.Sales, false)]
    public async Task Direct_Url_Follows_Profile_And_Role_Navigation(string role, string? navigation, bool allowed)
    {
        var farjad = new ClientModuleProfile(new ClientProfileOptions
        {
            Name = "Farjad",
            SimplePurchaseEnabled = true,
            HiddenModules = ["HumanResources"],
            HiddenControllers = ["Loading", "SarrafSettlements"]
        });
        var claims = new List<Claim> { new(ClaimTypes.Name, "tester"), new(ClaimTypes.Role, role) };
        if (navigation is not null) claims.Add(new Claim(AppClaimTypes.AllowedNavigation, navigation));
        var user = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));

        var routeData = new RouteData();
        routeData.Values["controller"] = "PartySettlements";
        routeData.Values["action"] = "Report";
        var context = new ActionExecutingContext(
            new ActionContext(new DefaultHttpContext { User = user }, routeData, new ActionDescriptor()),
            new List<IFilterMetadata>(), new Dictionary<string, object?>(), controller: new object());

        await new RoleNavigationAuthorizationFilter(farjad).OnActionExecutionAsync(context, () =>
            Task.FromResult(new ActionExecutedContext(context, new List<IFilterMetadata>(), new object())));

        Assert.Equal(allowed, context.Result is null);
    }

    private static async Task<PartySettlementReportViewModel> RunAsync(ApplicationDbContext db, PartySettlementReportFilter filter)
    {
        var result = Assert.IsType<ViewResult>(await NewController(db).Report(filter));
        return Assert.IsType<PartySettlementReportViewModel>(result.Model);
    }

    private static async Task<ApplicationDbContext> SeedAsync()
    {
        var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
        db.Currencies.Add(new Currency { Id = 1, Code = "USD", Name = "US Dollar" });
        db.Currencies.Add(new Currency { Id = 2, Code = "AFN", Name = "Afghani" });
        db.Customers.Add(new Customer { Id = A, Name = "احمد" });
        db.Customers.Add(new Customer { Id = Other, Name = "کریم" });
        db.Suppliers.Add(new Supplier { Id = B, Name = "محمود" });

        PartySettlement S(int id, DateTime date, AccountingPartyType fromType, int from, AccountingPartyType toType, int to,
            decimal amount, string currency, decimal? rate = null, PartySettlementStatus status = PartySettlementStatus.Posted)
            => new()
            {
                Id = id, SettlementDate = date, FromPartyType = fromType, FromPartyId = from, ToPartyType = toType, ToPartyId = to,
                Amount = amount, Currency = currency, CurrencyPerUsdRate = rate,
                FxRateToUsd = rate.HasValue ? 1m / rate.Value : 1m, AmountUsd = rate.HasValue ? amount / rate.Value : amount,
                Status = status, CreatedByUserName = "admin",
                CancellationReason = status == PartySettlementStatus.Cancelled ? "ثبت اشتباه" : null
            };

        const AccountingPartyType C = AccountingPartyType.Customer;
        const AccountingPartyType Sp = AccountingPartyType.Supplier;
        db.PartySettlements.AddRange(
            S(1, new DateTime(2026, 9, 10), C, A, Sp, B, 1000m, "USD"),
            S(2, new DateTime(2026, 9, 15), Sp, B, C, A, 300m, "USD"),
            S(3, new DateTime(2026, 9, 20), C, A, Sp, B, 20000m, "AFN", 70m),
            S(4, new DateTime(2026, 9, 25), C, A, Sp, B, 700m, "USD", status: PartySettlementStatus.Cancelled),
            S(5, new DateTime(2026, 9, 12), C, Other, Sp, B, 400m, "USD"));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return db;
    }

    private static PartySettlementsController NewController(ApplicationDbContext db)
        => new(db, new AuditService(db), new AfghanistanBusinessClock(TimeProvider.System))
        {
            TempData = new TempDataDictionary(new DefaultHttpContext(), new NullTempDataProvider()),
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

    private sealed class NullTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object?> LoadTempData(HttpContext context) => new Dictionary<string, object?>();

        public void SaveTempData(HttpContext context, IDictionary<string, object?> values)
        {
        }
    }
}
