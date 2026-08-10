using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PTGOilSystem.Web.Controllers;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Dispatch;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services;
using Xunit;

namespace PTGOilSystem.Web.Tests;

// لغو و ویرایش دیسپچ نباید موجودی مصنوعی بسازد.
// سناریوی مرجع: مخزن A خروج ۱۰۰ → موتر → مخزن B ورود ۱۰۰. اگر لغو فقط خروج مبدأ را برگرداند،
// ۱۰۰ تن در مخزن B بی‌صاحب باقی می‌ماند و کل موجودی سیستم ۱۰۰ تن بیشتر می‌شود.
public class DispatchCancelEditGuardTests
{
    [Fact]
    public async Task Cancel_Is_Blocked_When_The_Load_Was_Already_Unloaded_Into_A_Tank()
    {
        await using var db = BuildDb();
        await SeedDispatchWithStockOutAsync(db);

        // تخلیه در مخزن مقصد: ورودی + رسید تحویل.
        db.InventoryMovements.Add(new InventoryMovement
        {
            ProductId = 1,
            ContractId = 1,
            TerminalId = 2,
            StorageTankId = 2,
            Direction = MovementDirection.In,
            MovementDate = new DateTime(2026, 4, 26),
            QuantityMt = 100m,
            ReferenceDocument = "TRUCK-UNLOAD:1"
        });
        db.DeliveryReceipts.Add(new DeliveryReceipt
        {
            TruckDispatchId = 1,
            ReceiptDate = new DateTime(2026, 4, 26),
            ReceivedQuantityMt = 100m
        });
        await db.SaveChangesAsync();

        var controller = BuildController(db);
        var result = await controller.Cancel(1);

        Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(DispatchStatus.Loaded, (await db.TruckDispatches.SingleAsync()).Status);
        Assert.Contains("عملیات بعدی", Assert.IsType<string>(controller.TempData["err"]));

        // هیچ سند برگشتی ساخته نشده. بدون این نگهبان مخزن A هم ۱۰۰ تن پس می‌گرفت در حالی که
        // ۱۰۰ تن در مخزن B هست، و کل موجودی از یک خرید ۱۰۰ تنی به ۲۰۰ تن می‌رسید.
        Assert.Equal(0, await db.InventoryMovements.CountAsync(m => m.ReferenceDocument!.EndsWith("-CANCEL")));
        Assert.Equal(0m, await NetQuantityAsync(db, storageTankId: 1));
        Assert.Equal(100m, await NetQuantityAsync(db, storageTankId: 2));
        Assert.Equal(100m, await NetQuantityAsync(db));
    }

    [Fact]
    public async Task Cancel_Reverses_The_Stock_Out_When_Nothing_Downstream_Exists()
    {
        await using var db = BuildDb();
        await SeedDispatchWithStockOutAsync(db);

        var controller = BuildController(db);
        Assert.IsType<RedirectToActionResult>(await controller.Cancel(1));

        Assert.Equal(DispatchStatus.Cancelled, (await db.TruckDispatches.SingleAsync()).Status);
        Assert.Equal(1, await db.InventoryMovements.CountAsync(m => m.ReferenceDocument == "TRUCK-DISPATCH:1-CANCEL"));
        // بار به مخزن مبدأ برمی‌گردد و کل موجودی همان خرید اولیه می‌ماند.
        Assert.Equal(100m, await NetQuantityAsync(db, storageTankId: 1));
        Assert.Equal(100m, await NetQuantityAsync(db));
    }

    // ارسال دوباره فرم لغو (دابل‌کلیک/refresh) نباید سند برگشتی دوم بسازد.
    [Fact]
    public async Task Cancelling_Twice_Does_Not_Create_A_Second_Reversal()
    {
        await using var db = BuildDb();
        await SeedDispatchWithStockOutAsync(db);

        var controller = BuildController(db);
        await controller.Cancel(1);
        await controller.Cancel(1);

        Assert.Equal(1, await db.InventoryMovements.CountAsync(m => m.ReferenceDocument == "TRUCK-DISPATCH:1-CANCEL"));
        // لغو دوم سند دوم نمی‌سازد؛ در غیر این صورت مخزن A به ۲۰۰ تن می‌رسید.
        Assert.Equal(100m, await NetQuantityAsync(db, storageTankId: 1));
    }

    [Fact]
    public async Task Edit_Rejects_Quantity_Contract_And_Product_Changes_After_The_Stock_Out_Was_Posted()
    {
        await using var db = BuildDb();
        await SeedDispatchWithStockOutAsync(db);

        var controller = BuildController(db);
        var result = await controller.Edit(1, new DispatchCreateViewModel
        {
            ContractId = 2,
            ProductId = 2,
            TruckId = 1,
            SourceTerminalId = 1,
            SourceStorageTankId = 1,
            DispatchDate = new DateTime(2026, 4, 25),
            LoadedQuantityMt = 40m
        });

        Assert.IsType<ViewResult>(result);
        Assert.False(controller.ModelState.IsValid);
        Assert.True(controller.ModelState.ContainsKey(nameof(DispatchCreateViewModel.ContractId)));
        Assert.True(controller.ModelState.ContainsKey(nameof(DispatchCreateViewModel.ProductId)));
        Assert.True(controller.ModelState.ContainsKey(nameof(DispatchCreateViewModel.LoadedQuantityMt)));

        var dispatch = await db.TruckDispatches.SingleAsync();
        Assert.Equal(1, dispatch.ContractId);
        Assert.Equal(1, dispatch.ProductId);
        Assert.Equal(100m, dispatch.LoadedQuantityMt);
    }

    [Fact]
    public async Task Edit_Still_Allows_Benign_Fields_While_The_Stock_Out_Stays_Untouched()
    {
        await using var db = BuildDb();
        await SeedDispatchWithStockOutAsync(db);

        var controller = BuildController(db);
        var result = await controller.Edit(1, new DispatchCreateViewModel
        {
            ContractId = 1,
            ProductId = 1,
            TruckId = 1,
            SourceTerminalId = 1,
            SourceStorageTankId = 1,
            DispatchDate = new DateTime(2026, 4, 25),
            LoadedQuantityMt = 100m,
            TicketSerialNumber = "TCK-9",
            Notes = "یادداشت تازه"
        });

        Assert.IsType<RedirectToActionResult>(result);
        var dispatch = await db.TruckDispatches.SingleAsync();
        Assert.Equal("TCK-9", dispatch.TicketSerialNumber);
        Assert.Equal("یادداشت تازه", dispatch.Notes);
        Assert.Equal(100m, dispatch.LoadedQuantityMt);
    }

    private static ApplicationDbContext BuildDb()
        => new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static DispatchController BuildController(ApplicationDbContext db)
        => new(db, new StockService(db), new AuditService(db), NullLogger<DispatchController>.Instance)
        {
            TempData = new TempDataDictionary(new DefaultHttpContext(), new NullTempDataProvider())
        };

    private sealed class NullTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object?> LoadTempData(HttpContext context) => new Dictionary<string, object?>();

        public void SaveTempData(HttpContext context, IDictionary<string, object?> values) { }
    }

    private static Task<decimal> NetQuantityAsync(ApplicationDbContext db, int? storageTankId = null)
        => db.InventoryMovements
            .Where(m => storageTankId == null || m.StorageTankId == storageTankId)
            .SumAsync(m =>
                m.Direction == MovementDirection.In || m.Direction == MovementDirection.Adjustment
                    ? m.QuantityMt
                    : -m.QuantityMt);

    // مخزن A: ورود ۱۰۰ (رسید خرید) و خروج ۱۰۰ برای دیسپچ #۱ — خالص صفر.
    private static async Task SeedDispatchWithStockOutAsync(ApplicationDbContext db)
    {
        db.Products.AddRange(
            new Product { Id = 1, Code = "GO", Name = "Gas Oil", IsActive = true },
            new Product { Id = 2, Code = "MG", Name = "Mogas", IsActive = true });
        db.Terminals.AddRange(
            new Terminal { Id = 1, Code = "T1", Name = "Terminal 1", IsActive = true },
            new Terminal { Id = 2, Code = "T2", Name = "Terminal 2", IsActive = true });
        db.StorageTanks.AddRange(
            new StorageTank { Id = 1, TankCode = "A", TerminalId = 1, ProductId = 1, IsActive = true },
            new StorageTank { Id = 2, TankCode = "B", TerminalId = 2, ProductId = 1, IsActive = true });
        db.Trucks.Add(new Truck { Id = 1, PlateNumber = "TRK-01", IsActive = true });
        db.Contracts.AddRange(
            new Contract { Id = 1, ContractNumber = "CTR-1", ContractType = ContractType.Purchase, ProductId = 1, ContractDate = new DateTime(2026, 4, 1), QuantityMt = 500m },
            new Contract { Id = 2, ContractNumber = "CTR-2", ContractType = ContractType.Purchase, ProductId = 2, ContractDate = new DateTime(2026, 4, 1), QuantityMt = 500m });
        db.TruckDispatches.Add(new TruckDispatch
        {
            Id = 1,
            DispatchMode = TruckDispatchMode.FromInventory,
            ContractId = 1,
            ProductId = 1,
            TruckId = 1,
            DispatchDate = new DateTime(2026, 4, 25),
            LoadedQuantityMt = 100m,
            Status = DispatchStatus.Loaded
        });
        db.InventoryMovements.AddRange(
            new InventoryMovement
            {
                ProductId = 1,
                ContractId = 1,
                TerminalId = 1,
                StorageTankId = 1,
                Direction = MovementDirection.In,
                MovementDate = new DateTime(2026, 4, 20),
                QuantityMt = 100m,
                ReferenceDocument = "SEED-IN"
            },
            new InventoryMovement
            {
                ProductId = 1,
                ContractId = 1,
                TerminalId = 1,
                StorageTankId = 1,
                Direction = MovementDirection.Out,
                MovementDate = new DateTime(2026, 4, 25),
                QuantityMt = 100m,
                ReferenceDocument = "TRUCK-DISPATCH:1"
            });
        await db.SaveChangesAsync();
    }
}
