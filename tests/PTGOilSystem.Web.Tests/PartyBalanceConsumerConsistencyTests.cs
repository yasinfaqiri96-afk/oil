using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Controllers;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.PartyStatements;
using PTGOilSystem.Web.Models.Payments;
using PTGOilSystem.Web.Models.ServiceProviders;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.CompanyFlow;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// ماندهٔ طرف‌حساب یک مرجع دارد (موتورِ رسمیِ صورت‌حساب). فهرست و جزئیات یک طرف‌حساب باید
/// همان عدد و همان علامت را نشان دهند: مثبت = طلب شرکت، منفی = بدهی شرکت.
/// </summary>
public sealed class PartyBalanceConsumerConsistencyTests
{
    [Fact]
    public async Task Service_Provider_List_And_Details_Show_The_Same_Official_Balance_And_Sign()
    {
        await using var db = NewDb();
        db.ServiceProviders.Add(new ServiceProvider { Id = 1, Name = "Alpha Freight" });
        // خدمت دریافت شد (تعهد 1000) و 400 پرداخت شد ⇒ شرکت 600 بدهکار است ⇒ مانده −600.
        db.LedgerEntries.AddRange(
            Ledger(1, CompanyFlowSourceTypes.Expense, LedgerSide.Credit, 1000m, serviceProviderId: 1),
            Ledger(2, nameof(PaymentKind.ServiceProviderPayment), LedgerSide.Debit, 400m, serviceProviderId: 1));
        await db.SaveChangesAsync();

        var controller = new ServiceProvidersController(db) { ControllerContext = NewContext() };
        var index = Assert.IsType<ServiceProviderIndexViewModel>(Assert.IsType<ViewResult>(await controller.Index()).Model);
        var details = Assert.IsType<ViewResult>(await controller.Details(1));
        var summary = Assert.IsType<PartyStatementSummary>(details.ViewData["PartyStatementSummary"]);

        Assert.Equal(-600m, summary.ClosingBalance);
        Assert.Equal(summary.ClosingBalance, Assert.Single(index.Items).LedgerBalanceUsd);
    }

    [Fact]
    public async Task Service_Provider_Balance_Card_Covers_All_Results_Not_Only_The_Current_Page()
    {
        await using var db = NewDb();
        db.ServiceProviders.AddRange(
            new ServiceProvider { Id = 1, Name = "A Provider" },
            new ServiceProvider { Id = 2, Name = "B Provider" });
        db.LedgerEntries.AddRange(
            Ledger(1, CompanyFlowSourceTypes.Expense, LedgerSide.Credit, 1000m, serviceProviderId: 1),
            Ledger(2, CompanyFlowSourceTypes.Expense, LedgerSide.Credit, 250m, serviceProviderId: 2));
        await db.SaveChangesAsync();

        var controller = new ServiceProvidersController(db) { ControllerContext = NewContext() };
        var firstPage = Assert.IsType<ServiceProviderIndexViewModel>(
            Assert.IsType<ViewResult>(await controller.Index(page: 1, perPage: 1)).Model);

        Assert.Single(firstPage.Items);
        Assert.Equal(-1250m, firstPage.FilteredBalanceUsd);
    }

    [Fact]
    public async Task Driver_Freight_Payment_Is_Not_Reported_As_Shortage_Damage()
    {
        await using var db = NewDb();
        db.Drivers.Add(new Driver { Id = 1, FullName = "Driver One" });
        db.LedgerEntries.AddRange(
            Ledger(1, CompanyFlowSourceTypes.Expense, LedgerSide.Credit, 500m, driverId: 1),
            Ledger(2, CompanyFlowSourceTypes.ShortageCharge, LedgerSide.Debit, 80m, driverId: 1),
            Ledger(3, nameof(PaymentKind.TruckPayment), LedgerSide.Debit, 300m, driverId: 1));
        await db.SaveChangesAsync();

        var controller = new DriversController(db, new AuditService(db))
        {
            ControllerContext = NewContext(),
            TempData = new TempDataDictionary(new DefaultHttpContext(), new EmptyTempDataProvider())
        };
        var view = Assert.IsType<ViewResult>(await controller.Details(1));
        var summary = Assert.IsType<PartyStatementSummary>(view.ViewData["PartyStatementSummary"]);

        Assert.Equal(500m, (decimal)view.ViewData["DriverFreightCreditUsd"]!);
        Assert.Equal(80m, (decimal)view.ViewData["DriverShortageDebitUsd"]!);
        Assert.Equal(300m, (decimal)view.ViewData["DriverPaymentsDebitUsd"]!);
        // کرایه 500 − خسارت 80 − پرداخت 300 ⇒ شرکت هنوز 120 به راننده بدهکار است.
        Assert.Equal(-120m, summary.ClosingBalance);
    }

    [Fact]
    public async Task Payment_Form_Party_Balance_Uses_The_Official_Statement()
    {
        await using var db = NewDb();
        db.ServiceProviders.Add(new ServiceProvider { Id = 1, Name = "Alpha Freight" });
        db.LedgerEntries.AddRange(
            Ledger(1, CompanyFlowSourceTypes.Expense, LedgerSide.Credit, 1000m, serviceProviderId: 1),
            Ledger(2, nameof(PaymentKind.ServiceProviderPayment), LedgerSide.Debit, 400m, serviceProviderId: 1));
        await db.SaveChangesAsync();

        var payments = new PaymentsController(db, new PricingService(db), new AuditService(db),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<PaymentsController>.Instance)
        {
            ControllerContext = NewContext()
        };
        var json = Assert.IsType<JsonResult>(await payments.PartyBalance((int)PaymentCounterpartyType.ServiceProvider, 1));
        var payload = System.Text.Json.JsonSerializer.Serialize(json.Value);

        Assert.Contains("\"available\":true", payload);
        Assert.Contains("600.00", payload);
        Assert.Contains("\"tone\":\"negative\"", payload);
    }

    [Fact]
    public async Task Reconciliation_Non_Zero_Balance_Equals_The_Official_Statement()
    {
        await using var db = NewDb();
        db.Customers.Add(new Customer { Id = 1, Name = "Customer A" });
        db.SalesTransactions.Add(new SalesTransaction { Id = 1, CustomerId = 1, SaleDate = new DateTime(2026, 6, 1), TotalUsd = 1000m });
        // سطرِ قدیمیِ فروش مشتری را ندارد؛ موتورِ رسمی آن را از خودِ فروش به مشتری نسبت می‌دهد.
        var sale = Ledger(1, CompanyFlowSourceTypes.Sale, LedgerSide.Credit, 1000m);
        var receipt = Ledger(2, nameof(PaymentKind.CustomerReceipt), LedgerSide.Debit, 400m);
        receipt.CustomerId = 1;
        db.LedgerEntries.AddRange(sale, receipt);
        await db.SaveChangesAsync();

        var statement = await PTGOilSystem.Web.Services.PartyStatements.PartyStatementReadService.CreateDefault(db)
            .GetStatementAsync(new PartyRef(PartyStatementPartyType.Customer, 1), new PartyStatementFilter());
        var reconciliation = await new PTGOilSystem.Web.Services.Reconciliation.ReconciliationService(db).BuildNonZeroBalancesAsync();

        Assert.Equal(600m, statement.Summary.ClosingBalance);
        Assert.Equal(statement.Summary.ClosingBalance, Assert.Single(reconciliation.CustomerBalances).BalanceUsd);
    }

    [Fact]
    public async Task Sarraf_List_And_Details_Show_The_Same_Official_Balance()
    {
        await using var db = NewDb();
        db.Sarrafs.Add(new Sarraf { Id = 1, Name = "Sarraf A", IsActive = true });
        // صراف 1000 به‌جای شرکت پرداخت کرد و شرکت 400 به او داد ⇒ شرکت 600 بدهکار است.
        db.SarrafSettlements.Add(new SarrafSettlement
        {
            Id = 1,
            SarrafId = 1,
            SettlementDate = new DateTime(2026, 6, 1),
            Status = SarrafSettlementStatus.Posted,
            Direction = SarrafSettlementDirection.Out,
            SarrafChargedAmountUsd = 1000m
        });
        db.PaymentTransactions.Add(new PaymentTransaction
        {
            Id = 1,
            PaymentDate = new DateTime(2026, 6, 2),
            Direction = PaymentDirection.Out,
            PaymentKind = PaymentKind.SarrafSettlement,
            SarrafId = 1,
            Amount = 400m,
            Currency = "USD",
            AmountUsd = 400m
        });
        await db.SaveChangesAsync();

        var controller = new SarrafsController(db) { ControllerContext = NewContext() };
        var index = Assert.IsType<PTGOilSystem.Web.Models.Sarrafs.SarrafIndexViewModel>(
            Assert.IsType<ViewResult>(await controller.Index()).Model);
        var details = Assert.IsType<ViewResult>(await controller.Details(1));
        var summary = Assert.IsType<PartyStatementSummary>(details.ViewData["PartyStatementSummary"]);

        Assert.Equal(-600m, summary.ClosingBalance);
        Assert.Equal(summary.ClosingBalance, Assert.Single(index.Items).OfficialBalanceUsd);
        Assert.Equal(summary.ClosingBalance, index.TotalOfficialBalanceUsd);
    }

    private static LedgerEntry Ledger(
        int id,
        string sourceType,
        LedgerSide side,
        decimal amountUsd,
        int? serviceProviderId = null,
        int? driverId = null)
        => new()
        {
            Id = id,
            EntryDate = new DateTime(2026, 6, id),
            SourceType = sourceType,
            SourceId = id,
            Side = side,
            AmountUsd = amountUsd,
            ServiceProviderId = serviceProviderId,
            DriverId = driverId,
            Description = sourceType
        };

    private static ApplicationDbContext NewDb()
        => new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static ControllerContext NewContext() => new() { HttpContext = new DefaultHttpContext() };

    private sealed class EmptyTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();

        public void SaveTempData(HttpContext context, IDictionary<string, object> values)
        {
        }
    }
}
