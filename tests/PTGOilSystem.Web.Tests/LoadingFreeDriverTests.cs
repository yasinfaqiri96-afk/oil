using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PTGOilSystem.Web.Controllers;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.Loading;
using PTGOilSystem.Web.Services;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// «راننده آزاد» در فرم ثبت بارگیری: انتخاب راننده موجود، ساخت پروفایل راننده جدید،
/// جلوگیری از پروفایل تکراری و دست‌نخوردن دو حالت قبلی ترانسپورت.
/// </summary>
public class LoadingFreeDriverTests
{
    [Fact]
    public async Task Create_FreeDriver_With_Existing_Driver_Stores_DriverId_Name_And_Phone()
    {
        await using var db = NewDb();
        Seed(db);
        db.Drivers.Add(new Driver { Id = 7, FullName = "Karim Khan", Phone = "+93 700 111 222", IsActive = true });
        await db.SaveChangesAsync();

        var result = await NewController(db).Create(Model(new LoadingCreateRowViewModel
        {
            LoadingDate = new DateTime(2026, 4, 23),
            TruckId = 1,
            BillOfLadingNumber = "CMR-1",
            LoadedQuantityMt = 30m,
            LogisticsMode = "driver",
            DriverId = 7,
            TransitNumber = "TR-55"
        }));

        Assert.IsType<RedirectToActionResult>(result);
        var loading = await db.LoadingRegisters.SingleAsync();
        Assert.Equal(7, loading.DriverId);
        Assert.Equal("Karim Khan", loading.DriverName);
        Assert.Equal("+93 700 111 222", loading.DriverPhone);
        Assert.Equal("TR-55", loading.TransitNumber);
        Assert.Null(loading.LogisticsServiceProviderId);
        Assert.Equal(1, await db.Drivers.CountAsync());
    }

    [Fact]
    public async Task Create_FreeDriver_With_New_Name_Creates_Driver_Profile_Without_TransitNumber()
    {
        await using var db = NewDb();
        Seed(db);
        await db.SaveChangesAsync();

        var result = await NewController(db).Create(Model(new LoadingCreateRowViewModel
        {
            LoadingDate = new DateTime(2026, 4, 23),
            TruckId = 1,
            BillOfLadingNumber = "CMR-2",
            LoadedQuantityMt = 30m,
            LogisticsMode = "driver",
            DriverName = "New Driver",
            DriverPhone = "0700 333 444",
            TransitNumber = "TR-77"
        }));

        Assert.IsType<RedirectToActionResult>(result);
        var driver = await db.Drivers.SingleAsync();
        Assert.Equal("New Driver", driver.FullName);
        Assert.Equal("0700 333 444", driver.Phone);
        Assert.True(driver.IsActive);
        Assert.DoesNotContain("TR-77", driver.Notes ?? "");

        var loading = await db.LoadingRegisters.SingleAsync();
        Assert.Equal(driver.Id, loading.DriverId);
        Assert.Equal("TR-77", loading.TransitNumber);
    }

    [Fact]
    public async Task Create_FreeDriver_Same_Phone_Reuses_Existing_Driver()
    {
        await using var db = NewDb();
        Seed(db);
        db.Drivers.Add(new Driver { Id = 3, FullName = "Ahmad", Phone = "0700-555-666", IsActive = true });
        await db.SaveChangesAsync();

        var result = await NewController(db).Create(Model(
            new LoadingCreateRowViewModel
            {
                LoadingDate = new DateTime(2026, 4, 23),
                TruckId = 1,
                BillOfLadingNumber = "CMR-3",
                LoadedQuantityMt = 20m,
                LogisticsMode = "driver",
                DriverName = "Ahmad Jan",
                DriverPhone = "0700 555 666"
            },
            new LoadingCreateRowViewModel
            {
                LoadingDate = new DateTime(2026, 4, 23),
                TruckId = 1,
                BillOfLadingNumber = "CMR-4",
                LoadedQuantityMt = 20m,
                LogisticsMode = "driver",
                DriverName = "Ahmad",
                DriverPhone = "0700555666"
            }));

        Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(1, await db.Drivers.CountAsync());
        Assert.All(await db.LoadingRegisters.ToListAsync(), l => Assert.Equal(3, l.DriverId));
    }

    [Fact]
    public async Task Create_FreeDriver_New_Driver_On_Several_Rows_Creates_One_Profile()
    {
        await using var db = NewDb();
        Seed(db);
        await db.SaveChangesAsync();

        var result = await NewController(db).Create(Model(
            new LoadingCreateRowViewModel
            {
                LoadingDate = new DateTime(2026, 4, 23),
                TruckId = 1,
                BillOfLadingNumber = "CMR-5",
                LoadedQuantityMt = 20m,
                LogisticsMode = "driver",
                DriverName = "Rahim",
                DriverPhone = "0799 000 111"
            },
            new LoadingCreateRowViewModel
            {
                LoadingDate = new DateTime(2026, 4, 23),
                TruckId = 1,
                BillOfLadingNumber = "CMR-6",
                LoadedQuantityMt = 20m,
                LogisticsMode = "driver",
                DriverName = "Rahim",
                DriverPhone = "0799000111"
            }));

        Assert.IsType<RedirectToActionResult>(result);
        var driver = await db.Drivers.SingleAsync();
        Assert.All(await db.LoadingRegisters.ToListAsync(), l => Assert.Equal(driver.Id, l.DriverId));
    }

    [Fact]
    public async Task Create_FreeDriver_Same_Name_Different_Phone_Is_A_Different_Driver()
    {
        await using var db = NewDb();
        Seed(db);
        db.Drivers.Add(new Driver { Id = 4, FullName = "Nabi", Phone = "0700 000 001", IsActive = true });
        await db.SaveChangesAsync();

        var result = await NewController(db).Create(Model(new LoadingCreateRowViewModel
        {
            LoadingDate = new DateTime(2026, 4, 23),
            TruckId = 1,
            BillOfLadingNumber = "CMR-7",
            LoadedQuantityMt = 20m,
            LogisticsMode = "driver",
            DriverName = "Nabi",
            DriverPhone = "0700 000 002"
        }));

        Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(2, await db.Drivers.CountAsync());
        Assert.NotEqual(4, (await db.LoadingRegisters.SingleAsync()).DriverId);
    }

    [Fact]
    public async Task Create_FreeDriver_Without_Driver_Name_Is_Rejected()
    {
        await using var db = NewDb();
        Seed(db);
        await db.SaveChangesAsync();

        var controller = NewController(db);
        var result = await controller.Create(Model(new LoadingCreateRowViewModel
        {
            LoadingDate = new DateTime(2026, 4, 23),
            TruckId = 1,
            BillOfLadingNumber = "CMR-8",
            LoadedQuantityMt = 20m,
            LogisticsMode = "driver"
        }));

        Assert.IsType<ViewResult>(result);
        Assert.False(controller.ModelState.IsValid);
        Assert.Empty(await db.LoadingRegisters.ToListAsync());
        Assert.Empty(await db.Drivers.ToListAsync());
    }

    [Fact]
    public async Task Create_FreeTransport_Ignores_Posted_DriverId_And_Creates_No_Driver()
    {
        await using var db = NewDb();
        Seed(db, withServiceProvider: true);
        db.Drivers.Add(new Driver { Id = 9, FullName = "Someone", IsActive = true });
        await db.SaveChangesAsync();

        var result = await NewController(db).Create(Model(new LoadingCreateRowViewModel
        {
            LoadingDate = new DateTime(2026, 4, 23),
            TruckId = 1,
            BillOfLadingNumber = "CMR-9",
            LoadedQuantityMt = 20m,
            LogisticsMode = "free",
            LogisticsServiceProviderId = 1,
            DriverId = 9,
            DriverName = "Typed Driver",
            DriverPhone = "0700 999 999"
        }));

        Assert.IsType<RedirectToActionResult>(result);
        var loading = await db.LoadingRegisters.SingleAsync();
        Assert.Null(loading.DriverId);
        Assert.Equal(1, loading.LogisticsServiceProviderId);
        Assert.Equal("Typed Driver", loading.DriverName);
        Assert.Equal(1, await db.Drivers.CountAsync());
    }

    [Fact]
    public async Task Edit_Resaving_Free_Driver_Loading_Does_Not_Create_Duplicate_Driver()
    {
        await using var db = NewDb();
        Seed(db);
        await db.SaveChangesAsync();
        await NewController(db).Create(Model(new LoadingCreateRowViewModel
        {
            LoadingDate = new DateTime(2026, 4, 23),
            TruckId = 1,
            BillOfLadingNumber = "CMR-10",
            LoadedQuantityMt = 20m,
            LogisticsMode = "driver",
            DriverName = "Wali",
            DriverPhone = "0700 123 456",
            TransitNumber = "TR-1"
        }));
        var loading = await db.LoadingRegisters.AsNoTracking().SingleAsync();
        var driverId = loading.DriverId;
        Assert.NotNull(driverId);

        var editController = NewController(db);
        var result = await editController.Edit(loading.Id, new LoadingEditViewModel
        {
            Id = loading.Id,
            Version = loading.Version,
            LoadingDate = loading.LoadingDate,
            LoadedQuantityMt = loading.LoadedQuantityMt,
            BillOfLadingNumber = loading.BillOfLadingNumber,
            DriverName = "Wali",
            DriverPhone = "0700 123 456",
            TransitNumber = "TR-2"
        });

        Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(1, await db.Drivers.CountAsync());
        var edited = await db.LoadingRegisters.AsNoTracking().SingleAsync();
        Assert.Equal(driverId, edited.DriverId);
        Assert.Equal("TR-2", edited.TransitNumber);
    }

    private static ApplicationDbContext NewDb()
        => new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static LoadingController NewController(ApplicationDbContext db)
        => new(db, new AuditService(db), NullLogger<LoadingController>.Instance)
        {
            TempData = new TempDataDictionary(new DefaultHttpContext(), new InMemoryTempDataProvider())
        };

    private static LoadingCreateViewModel Model(params LoadingCreateRowViewModel[] rows)
        => new()
        {
            ContractId = 1,
            ProductId = 1,
            TransportType = LoadingTransportType.Truck,
            RecordFreight = true,
            // ترانسپورت بیرونی + ثبت کرایه ⇒ مسئول کرایه اجباری است.
            FreightCostResponsibility = CostResponsibility.Buyer,
            Rows = rows.ToList()
        };

    private static void Seed(ApplicationDbContext db, bool withServiceProvider = false)
    {
        db.Products.Add(new Product { Id = 1, Code = "GO", Name = "Gas Oil", IsActive = true });
        db.Companies.Add(new Company { Id = 1, Code = "PTG", Name = "Petro Trade Group", IsActive = true });
        if (withServiceProvider)
        {
            db.ServiceProviders.Add(new ServiceProvider
            {
                Id = 1,
                Code = "LOG-1",
                Name = "Axis Logistics",
                ProviderType = ServiceProviderType.TransportCompany,
                IsActive = true
            });
        }

        db.Contracts.Add(new Contract
        {
            Id = 1,
            ContractNumber = "PUR-1",
            ContractType = ContractType.Purchase,
            ProductId = 1,
            CompanyId = 1,
            ContractDate = new DateTime(2026, 4, 23),
            QuantityMt = 500m
        });
        db.Trucks.Add(new Truck { Id = 1, PlateNumber = "TRK-01", IsActive = true });
    }

    private sealed class InMemoryTempDataProvider : ITempDataProvider
    {
        private IDictionary<string, object> _data = new Dictionary<string, object>();

        public IDictionary<string, object> LoadTempData(HttpContext context) => _data;

        public void SaveTempData(HttpContext context, IDictionary<string, object> values)
            => _data = new Dictionary<string, object>(values);
    }
}
