using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Exceptions;
using Xunit;

namespace PTGOilSystem.Web.Tests;

public class DateTimeNormalizationTests
{
    [Fact]
    public void StockService_NormalizeUtc_Converts_Query_Date_Parameters_To_Utc_Safe_Values()
    {
        var helper = typeof(StockService).GetMethod("NormalizeUtc", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static, [typeof(DateTime?)]);

        Assert.NotNull(helper);

        var utcValue = new DateTime(2026, 4, 25, 12, 0, 0, DateTimeKind.Utc);
        var localValue = new DateTime(2026, 4, 25, 12, 0, 0, DateTimeKind.Local);
        var unspecifiedValue = new DateTime(2026, 4, 25, 12, 0, 0, DateTimeKind.Unspecified);

        Assert.Null((DateTime?)helper!.Invoke(null, [null]));
        Assert.Equal(utcValue, (DateTime?)helper.Invoke(null, [utcValue]));

        var normalizedLocal = Assert.IsType<DateTime>(helper.Invoke(null, [localValue]));
        Assert.Equal(DateTimeKind.Utc, normalizedLocal.Kind);
        Assert.Equal(localValue.ToUniversalTime(), normalizedLocal);

        var normalizedUnspecified = Assert.IsType<DateTime>(helper.Invoke(null, [unspecifiedValue]));
        Assert.Equal(DateTimeKind.Utc, normalizedUnspecified.Kind);
        Assert.Equal(unspecifiedValue.Ticks, normalizedUnspecified.Ticks);
    }

    [Fact]
    public async Task SaveChangesAsync_Normalizes_Unspecified_Contract_Dates_ToUtc_Without_Shifting_Clock_Time()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);

        var contractDate = new DateTime(2026, 4, 25);
        var startDate = new DateTime(2026, 4, 26);
        var endDate = new DateTime(2026, 4, 27);
        var savingChangesObserved = false;
        var contractDateBeforeSave = default(DateTime);
        DateTime? startDateBeforeSave = null;
        DateTime? endDateBeforeSave = null;

        var contract = new Contract
        {
            ContractNumber = "CTR-DATE-001",
            ContractType = ContractType.Purchase,
            Status = ContractStatus.Draft,
            CompanyId = 1,
            ProductId = 1,
            SupplierId = 1,
            ContractDate = contractDate,
            StartDate = startDate,
            EndDate = endDate,
            PricingMethod = PricingMethod.Fixed,
            QuantityMt = 100m,
            UnitPriceUsd = 500m
        };

        db.SavingChanges += (_, _) =>
        {
            savingChangesObserved = true;
            contractDateBeforeSave = contract.ContractDate;
            startDateBeforeSave = contract.StartDate;
            endDateBeforeSave = contract.EndDate;
        };

        db.Contracts.Add(contract);
        await db.SaveChangesAsync();

        Assert.True(savingChangesObserved);
        Assert.Equal(DateTimeKind.Utc, contractDateBeforeSave.Kind);
        Assert.Equal(contractDate.Ticks, contractDateBeforeSave.Ticks);
        Assert.Equal(DateTimeKind.Utc, startDateBeforeSave!.Value.Kind);
        Assert.Equal(startDate.Ticks, startDateBeforeSave.Value.Ticks);
        Assert.Equal(DateTimeKind.Utc, endDateBeforeSave!.Value.Kind);
        Assert.Equal(endDate.Ticks, endDateBeforeSave.Value.Ticks);
    }

    [Fact]
    public async Task SaveChangesAsync_Normalizes_Modified_Local_And_Nullable_DateTimes_ToUtc()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);

        var shipment = new Shipment
        {
            ShipmentCode = "SHP-DATE-001",
            QuantityMt = 50m
        };
        var savingChangesObserved = false;
        DateTime? departureDateBeforeSave = null;
        DateTime? arrivalDateBeforeSave = null;

        db.Shipments.Add(shipment);
        await db.SaveChangesAsync();

        var departureDate = new DateTime(2026, 4, 25, 14, 30, 0, DateTimeKind.Local);
        var arrivalDate = new DateTime(2026, 4, 27, 9, 15, 0, DateTimeKind.Unspecified);

        shipment.DepartureDate = departureDate;
        shipment.ArrivalDate = arrivalDate;

        db.SavingChanges += (_, _) =>
        {
            savingChangesObserved = true;
            departureDateBeforeSave = shipment.DepartureDate;
            arrivalDateBeforeSave = shipment.ArrivalDate;
        };

        await db.SaveChangesAsync();

        Assert.True(savingChangesObserved);
        Assert.Equal(DateTimeKind.Utc, departureDateBeforeSave!.Value.Kind);
        Assert.Equal(departureDate.ToUniversalTime(), departureDateBeforeSave.Value);
        Assert.Equal(DateTimeKind.Utc, arrivalDateBeforeSave!.Value.Kind);
        Assert.Equal(arrivalDate.Ticks, arrivalDateBeforeSave.Value.Ticks);
    }

    [Fact]
    public async Task EnsureSufficientStockForMovement_Allows_Unspecified_MovementDate_When_Stock_Is_Available()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        db.InventoryMovements.Add(new InventoryMovement
        {
            ProductId = 1,
            TerminalId = 1,
            Direction = MovementDirection.In,
            MovementDate = new DateTime(2026, 4, 20),
            QuantityMt = 100m
        });
        await db.SaveChangesAsync();

        var service = new StockService(db);

        var exception = await Record.ExceptionAsync(() => service.EnsureSufficientStockForMovementAsync(new InventoryMovement
        {
            ProductId = 1,
            TerminalId = 1,
            Direction = MovementDirection.Out,
            MovementDate = new DateTime(2026, 4, 25),
            QuantityMt = 25m
        }));

        Assert.Null(exception);
    }

    // PTG-P0-02 — این تست قبلاً «مجاز بودنِ موقتِ» ثبت عقب‌تاریخی را pin می‌کرد که مانده را
    // منفی می‌گذاشت. آن رفتار خودِ باگ بود؛ حالا همان سناریو باید مسدود شود.
    [Fact]
    public async Task EnsureMovementDoesNotCauseFutureNegativeStock_BlocksBackdatedOutThatBreaksLaterBalance()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        db.Products.Add(new Product { Id = 1, Code = "GO", Name = "Gas Oil" });
        db.Terminals.Add(new Terminal { Id = 1, Code = "T1", Name = "Terminal 1" });
        db.InventoryMovements.AddRange(
            new InventoryMovement
            {
                ProductId = 1,
                TerminalId = 1,
                Direction = MovementDirection.In,
                MovementDate = new DateTime(2026, 4, 1),
                QuantityMt = 100m
            },
            new InventoryMovement
            {
                ProductId = 1,
                TerminalId = 1,
                Direction = MovementDirection.Out,
                MovementDate = new DateTime(2026, 4, 20),
                QuantityMt = 90m
            });
        await db.SaveChangesAsync();

        var service = new StockService(db);

        // 100 − 90 − 50 = −40 ⇒ مانده پایانی منفی می‌شود، پس باید رد شود.
        var exception = await Assert.ThrowsAsync<BusinessRuleException>(() =>
            service.EnsureMovementDoesNotCauseFutureNegativeStockAsync(new InventoryMovement
            {
                ProductId = 1,
                TerminalId = 1,
                Direction = MovementDirection.Out,
                MovementDate = new DateTime(2026, 4, 10),
                QuantityMt = 50m
            }));

        Assert.Equal("STOCK_FUTURE_NEGATIVE", exception.Code);
        Assert.Contains("2026-04-20", exception.Message, StringComparison.Ordinal);
    }

    // PTG-P0-02 — گودالِ گذرا مجاز است: سندِ رسید اغلب بعد از بارگیریِ موتر/واگن به دفتر
    // می‌رسد، پس خط زمانی می‌تواند موقتاً منفی شود و خودش ترمیم گردد. معیار «ماندهٔ پایانی» است.
    [Fact]
    public async Task EnsureMovementDoesNotCauseFutureNegativeStock_AllowsTransientDipThatHealsLater()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        db.InventoryMovements.Add(new InventoryMovement
        {
            ProductId = 1,
            TerminalId = 1,
            Direction = MovementDirection.In,
            MovementDate = new DateTime(2026, 5, 5),
            QuantityMt = 100m
        });
        await db.SaveChangesAsync();

        var service = new StockService(db);

        // خروج ۳۰ تن در ۱ می: در آن لحظه ‎-30 است ولی رسیدِ ۵ می آن را ترمیم می‌کند ⇒ مانده پایانی ۷۰.
        var exception = await Record.ExceptionAsync(() =>
            service.EnsureMovementDoesNotCauseFutureNegativeStockAsync(new InventoryMovement
            {
                ProductId = 1,
                TerminalId = 1,
                Direction = MovementDirection.Out,
                MovementDate = new DateTime(2026, 5, 1),
                QuantityMt = 30m
            }));

        Assert.Null(exception);
    }

    [Fact]
    public async Task EnsureMovementDoesNotCauseFutureNegativeStock_AllowsBackdatedOutThatStillFits()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        db.InventoryMovements.AddRange(
            new InventoryMovement
            {
                ProductId = 1,
                TerminalId = 1,
                Direction = MovementDirection.In,
                MovementDate = new DateTime(2026, 4, 1),
                QuantityMt = 100m
            },
            new InventoryMovement
            {
                ProductId = 1,
                TerminalId = 1,
                Direction = MovementDirection.Out,
                MovementDate = new DateTime(2026, 4, 20),
                QuantityMt = 30m
            });
        await db.SaveChangesAsync();

        var service = new StockService(db);

        var exception = await Record.ExceptionAsync(() =>
            service.EnsureMovementDoesNotCauseFutureNegativeStockAsync(new InventoryMovement
            {
                ProductId = 1,
                TerminalId = 1,
                Direction = MovementDirection.Out,
                MovementDate = new DateTime(2026, 4, 10),
                QuantityMt = 50m
            }));

        Assert.Null(exception);
    }
}
