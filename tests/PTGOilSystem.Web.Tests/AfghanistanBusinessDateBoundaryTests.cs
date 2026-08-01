using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PTGOilSystem.Web.Controllers;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Customs;
using PTGOilSystem.Web.Models.Expenses;
using PTGOilSystem.Web.Models.InventoryTransport;
using PTGOilSystem.Web.Models.Payments;
using PTGOilSystem.Web.Models.Sales;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Time;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// مرز نیمه‌شب کابل. کابل UTC+04:30 و بدون ساعت تابستانی است، پس ۰۰:۰۰ کابل برابر
/// ۱۹:۳۰ UTC روز قبل است. هر تاریخ تجاریِ پیش‌فرض باید دقیقاً روی همین مرز بچرخد،
/// نه روی نیمه‌شب UTC. timestamp‌های فنی (CreatedAtUtc و …) عمداً UTC می‌مانند.
/// </summary>
public class AfghanistanBusinessDateBoundaryTests
{
    private sealed class FixedClock(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    /// <summary>۱۹:۲۹:۵۹ UTC = ۲۳:۵۹ کابلِ ۳۰ جولای.</summary>
    private static readonly DateTimeOffset JustBeforeKabulMidnight =
        new(2026, 7, 30, 19, 29, 59, TimeSpan.Zero);

    /// <summary>۱۹:۳۰:۰۰ UTC = ۰۰:۰۰ کابلِ ۳۱ جولای.</summary>
    private static readonly DateTimeOffset AtKabulMidnight =
        new(2026, 7, 30, 19, 30, 0, TimeSpan.Zero);

    private static readonly DateTime July30 = new(2026, 7, 30, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime July31 = new(2026, 7, 31, 0, 0, 0, DateTimeKind.Utc);

    private static IAfghanistanBusinessClock ClockAt(DateTimeOffset utcNow)
        => new AfghanistanBusinessClock(new FixedClock(utcNow));

    private static ApplicationDbContext NewDb()
        => new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    [Fact]
    public void Kabul_Midnight_Rolls_The_Business_Day_At_1930_Utc_Not_At_0000_Utc()
    {
        Assert.Equal(July30, ClockAt(JustBeforeKabulMidnight).Today);
        Assert.Equal(July31, ClockAt(AtKabulMidnight).Today);

        // ۲۳:۵۹ کابل هنوز همان روز کاری است.
        var lastMinute = ClockAt(new DateTimeOffset(2026, 7, 31, 19, 29, 0, TimeSpan.Zero));
        Assert.Equal(July31, lastMinute.Today);

        // در ۲۰:۰۰ UTC، تاریخ UTC هنوز ۳۰ است ولی روز کاری کابل ۳۱ است.
        var utcStillJuly30 = new DateTimeOffset(2026, 7, 30, 20, 0, 0, TimeSpan.Zero);
        Assert.Equal(30, utcStillJuly30.UtcDateTime.Day);
        Assert.Equal(July31, ClockAt(utcStillJuly30).Today);
    }

    [Fact]
    public void Business_Date_Is_Marked_Utc_Kind_So_Npgsql_Can_Bind_It()
    {
        // ستون‌های تاریخ تجاری از نوع timestamptz هستند؛ Kind=Unspecified رد می‌شود.
        Assert.Equal(DateTimeKind.Utc, ClockAt(AtKabulMidnight).Today.Kind);
        Assert.Equal(DateTimeKind.Utc, AfghanistanBusinessClock.SystemToday.Kind);
    }

    [Fact]
    public void Poco_Default_Dates_Follow_Kabul_Not_Utc()
    {
        // ViewModel و Entity نمی‌توانند DI بگیرند، پس پیش‌فرضشان از همان تقویم کابل می‌آید.
        var kabulToday = AfghanistanBusinessClock.SystemToday;

        Assert.Equal(kabulToday, new SalesCreateViewModel().SaleDate);
        Assert.Equal(kabulToday, new ExpenseCreateViewModel().ExpenseDate);
        Assert.Equal(kabulToday, new PaymentCreateViewModel().PaymentDate);
        Assert.Equal(kabulToday, new PTGOilSystem.Web.Models.Entities.Employee().HireDate);
    }

    [Fact]
    public async Task Sale_Create_Form_Defaults_To_The_Kabul_Business_Day()
    {
        await using var db = NewDb();

        var before = NewSalesController(db, JustBeforeKabulMidnight);
        var after = NewSalesController(db, AtKabulMidnight);

        Assert.Equal(July30, ModelOf<SalesCreateViewModel>(await before.Create()).SaleDate);
        Assert.Equal(July31, ModelOf<SalesCreateViewModel>(await after.Create()).SaleDate);
    }

    [Fact]
    public async Task Payment_Create_Form_Defaults_To_The_Kabul_Business_Day()
    {
        await using var db = NewDb();

        var before = NewPaymentsController(db, JustBeforeKabulMidnight);
        var after = NewPaymentsController(db, AtKabulMidnight);

        Assert.Equal(July30, ModelOf<PaymentCreateViewModel>(await before.Create()).PaymentDate);
        Assert.Equal(July31, ModelOf<PaymentCreateViewModel>(await after.Create()).PaymentDate);
    }

    [Fact]
    public async Task Expense_Create_Form_Defaults_To_The_Kabul_Business_Day()
    {
        await using var db = NewDb();

        var before = NewExpensesController(db, JustBeforeKabulMidnight);
        var after = NewExpensesController(db, AtKabulMidnight);

        Assert.Equal(July30, ModelOf<ExpenseCreateViewModel>(await before.Create()).ExpenseDate);
        Assert.Equal(July31, ModelOf<ExpenseCreateViewModel>(await after.Create()).ExpenseDate);
    }

    [Fact]
    public async Task Customs_Declaration_Create_Form_Defaults_To_The_Kabul_Business_Day()
    {
        await using var db = NewDb();
        SeedLoadingRegister(db);
        await db.SaveChangesAsync();

        var before = NewCustomsController(db, JustBeforeKabulMidnight);
        var after = NewCustomsController(db, AtKabulMidnight);

        Assert.Equal(July30, ModelOf<CustomsDeclarationCreateViewModel>(await before.Create(1, null)).DeclarationDate);
        Assert.Equal(July31, ModelOf<CustomsDeclarationCreateViewModel>(await after.Create(1, null)).DeclarationDate);
    }

    [Fact]
    public async Task Transport_Unload_Receipt_Form_Defaults_To_The_Kabul_Business_Day()
    {
        await using var db = NewDb();
        SeedTransportLeg(db);
        await db.SaveChangesAsync();
        Assert.NotNull(await db.InventoryTransportLegs.FirstOrDefaultAsync(l => l.Id == 1));

        var before = NewReceiptsController(db, JustBeforeKabulMidnight);
        var after = NewReceiptsController(db, AtKabulMidnight);

        var beforeModel = ModelOf<InventoryTransportReceiptCreateViewModel>(await before.Create(1));
        var afterModel = ModelOf<InventoryTransportReceiptCreateViewModel>(await after.Create(1));

        Assert.Equal(July30, beforeModel.ReceiptDate);
        Assert.Equal(July31, afterModel.ReceiptDate);
        // تاریخ فروش مستقیم و ارسال مستقیم هم از همان مرجع می‌آیند.
        Assert.Equal(July31, afterModel.SaleDate);
        Assert.Equal(July31, afterModel.DirectDispatchDate);
    }

    // ---------------------------------------------------------------- helpers

    private static T ModelOf<T>(IActionResult result) where T : class
    {
        var view = Assert.IsType<ViewResult>(result);
        return Assert.IsType<T>(view.Model);
    }

    private static SalesController NewSalesController(ApplicationDbContext db, DateTimeOffset utcNow)
        => new(
            db,
            new StockService(db),
            new CurrencyConversionService(new PricingService(db)),
            new AuditService(db),
            NullLogger<SalesController>.Instance,
            businessClock: ClockAt(utcNow));

    private static PaymentsController NewPaymentsController(ApplicationDbContext db, DateTimeOffset utcNow)
        => new(
            db,
            new CurrencyConversionService(new PricingService(db)),
            new SupplierPaymentAllocationService(db),
            new SarrafSettlementService(db),
            new AuditService(db),
            NullLogger<PaymentsController>.Instance,
            businessClock: ClockAt(utcNow));

    private static ExpensesController NewExpensesController(ApplicationDbContext db, DateTimeOffset utcNow)
        => new(
            db,
            new CurrencyConversionService(new PricingService(db)),
            new AuditService(db),
            NullLogger<ExpensesController>.Instance,
            businessClock: ClockAt(utcNow));

    private static CustomsDeclarationsController NewCustomsController(ApplicationDbContext db, DateTimeOffset utcNow)
        => new(
            db,
            NullLogger<CustomsDeclarationsController>.Instance,
            new TestWebHostEnvironment(),
            businessClock: ClockAt(utcNow));

    private static InventoryTransportReceiptsController NewReceiptsController(ApplicationDbContext db, DateTimeOffset utcNow)
        => new(
            db,
            new CurrencyConversionService(new PricingService(db)),
            NullLogger<InventoryTransportReceiptsController>.Instance,
            businessClock: ClockAt(utcNow));

    private static void SeedLoadingRegister(ApplicationDbContext db)
    {
        db.Products.Add(new PTGOilSystem.Web.Models.Entities.Product { Id = 1, Code = "GAS", Name = "Gasoline" });
        db.Companies.Add(new PTGOilSystem.Web.Models.Entities.Company { Id = 1, Code = "PTG", Name = "PTG" });
        db.Suppliers.Add(new PTGOilSystem.Web.Models.Entities.Supplier { Id = 1, Name = "Supplier A" });
        db.Terminals.Add(new PTGOilSystem.Web.Models.Entities.Terminal { Id = 1, Code = "T1", Name = "Terminal 1" });
        db.Contracts.Add(new PTGOilSystem.Web.Models.Entities.Contract
        {
            Id = 1,
            ContractNumber = "PUR-1",
            ContractType = PTGOilSystem.Web.Models.Entities.ContractType.Purchase,
            CompanyId = 1,
            ProductId = 1,
            SupplierId = 1,
            ContractDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            QuantityMt = 1_000m,
            PricingMethod = PTGOilSystem.Web.Models.Entities.PricingMethod.Fixed,
            UnitPriceUsd = 500m
        });
        db.LoadingRegisters.Add(new PTGOilSystem.Web.Models.Entities.LoadingRegister
        {
            Id = 1,
            ContractId = 1,
            ProductId = 1,
            TransportType = PTGOilSystem.Web.Models.Entities.LoadingTransportType.Wagon,
            WagonNumber = "W-1",
            LoadingDate = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc),
            LoadedQuantityMt = 60m
        });
    }

    private static void SeedTransportLeg(ApplicationDbContext db)
    {
        SeedLoadingRegister(db);
        db.InventoryTransportLegs.Add(new PTGOilSystem.Web.Models.Entities.InventoryTransportLeg
        {
            Id = 1,
            SourcePurchaseContractId = 1,
            ProductId = 1,
            SourceTerminalId = 1,
            DestinationTerminalId = 1,
            TransportType = PTGOilSystem.Web.Models.Entities.LoadingTransportType.Wagon,
            WagonNumber = "W-1",
            LoadedDate = new DateTime(2026, 7, 10, 0, 0, 0, DateTimeKind.Utc),
            QuantityMt = 50m,
            Status = PTGOilSystem.Web.Models.Entities.InventoryTransportLegStatus.InTransit
        });
    }

    private sealed class TestWebHostEnvironment : Microsoft.AspNetCore.Hosting.IWebHostEnvironment
    {
        public string WebRootPath { get; set; } = Path.GetTempPath();
        public Microsoft.Extensions.FileProviders.IFileProvider WebRootFileProvider { get; set; }
            = new Microsoft.Extensions.FileProviders.NullFileProvider();
        public string ApplicationName { get; set; } = "PTGOilSystem.Web.Tests";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; }
            = new Microsoft.Extensions.FileProviders.NullFileProvider();
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public string EnvironmentName { get; set; } = "Development";
    }
}
