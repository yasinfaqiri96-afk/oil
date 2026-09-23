using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
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
/// کارت‌های آماری فهرست رسیدها حالا از یک query ترکیبی می‌آیند. این تست همان چهار عدد را
/// روی دادهٔ ترکیبی (لغوشده، بدون مخزن، بدون مرجع، مرجعِ خودکار BULK-RCPT-) قفل می‌کند تا
/// تجمیع، قاعدهٔ «لغوشده در هیچ جمعی شمرده نمی‌شود» را نشکند.
/// </summary>
public sealed class LoadingReceiptsIndexStatsTests
{
    private static DbContextOptions<ApplicationDbContext> NewDbOptions()
        => new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

    private static void SeedReferenceData(ApplicationDbContext db)
    {
        db.Companies.Add(new Company { Id = 1, Code = "PTG", Name = "PTG" });
        db.Products.Add(new Product { Id = 1, Code = "GO", Name = "Gas Oil" });
        db.Suppliers.Add(new Supplier { Id = 1, Name = "Supplier A" });
        db.Terminals.Add(new Terminal { Id = 1, Code = "ILK", Name = "Ilinka" });
        db.StorageTanks.Add(new StorageTank { Id = 1, TerminalId = 1, TankCode = "TK-1", ProductId = 1, CapacityMt = 1000m });
        db.Contracts.Add(new Contract
        {
            Id = 1,
            ContractNumber = "PUR-001",
            ContractName = "Purchase 1",
            ContractType = ContractType.Purchase,
            Status = ContractStatus.Active,
            CompanyId = 1,
            ProductId = 1,
            SupplierId = 1,
            ContractDate = new DateTime(2026, 4, 1),
            QuantityMt = 1000m
        });
        db.LoadingRegisters.Add(new LoadingRegister
        {
            Id = 1,
            ContractId = 1,
            ProductId = 1,
            LoadingDate = new DateTime(2026, 5, 1),
            LoadedQuantityMt = 500m,
            BillOfLadingNumber = "BL-001"
        });
    }

    private static LoadingReceipt Receipt(
        int id,
        decimal quantity,
        bool cancelled = false,
        int? storageTankId = 1,
        string? reference = "RCV-001")
        => new()
        {
            Id = id,
            LoadingRegisterId = 1,
            TerminalId = 1,
            StorageTankId = storageTankId,
            ReceiptDate = new DateTime(2026, 5, 2),
            ReceivedQuantityMt = quantity,
            ReferenceDocument = reference,
            IsCancelled = cancelled,
            CancellationReason = cancelled ? "test" : null
        };

    [Fact]
    public async Task Index_Stat_Cards_Exclude_Cancelled_And_Auto_Generated_References()
    {
        var options = NewDbOptions();

        await using (var seed = new ApplicationDbContext(options))
        {
            SeedReferenceData(seed);
            seed.LoadingReceipts.AddRange(
                // شمرده می‌شود: مخزن دارد، مرجع دستی دارد
                Receipt(1, 100m),
                // مخزن ندارد: در WithTankCount نمی‌آید ولی در جمع مقدار هست
                Receipt(2, 50m, storageTankId: null),
                // مرجع خودکار انبوه: در WithReferenceCount نمی‌آید
                Receipt(3, 25m, reference: "BULK-RCPT-0007"),
                // مرجع خالی: در WithReferenceCount نمی‌آید
                Receipt(4, 10m, reference: ""),
                // لغوشده: در هیچ جمعی نمی‌آید ولی در TotalCount هست
                Receipt(5, 999m, cancelled: true));
            await seed.SaveChangesAsync();
        }

        await using var db = new ApplicationDbContext(options);
        var controller = new LoadingReceiptsController(
            db,
            new AuditService(db),
            NullLogger<LoadingReceiptsController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        var result = await controller.Index();

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<LoadingReceiptIndexViewModel>(view.Model);

        // فهرست، رسید لغوشده را هم نشان می‌دهد.
        Assert.Equal(5, model.TotalCount);

        // جمع‌ها فقط روی رسیدهای فعال: 100 + 50 + 25 + 10
        Assert.Equal(185m, (decimal)view.ViewData["SumQuantity"]!);
        // مخزن‌دار و فعال: رسیدهای ۱، ۳، ۴
        Assert.Equal(3, (int)view.ViewData["WithTankCount"]!);
        // مرجع دستیِ فعال: رسیدهای ۱ و ۲ (۳ مرجع خودکار دارد و ۴ مرجع خالی)
        Assert.Equal(2, (int)view.ViewData["WithReferenceCount"]!);
    }

    [Fact]
    public async Task Index_Stat_Cards_Are_Zero_When_No_Receipt_Exists()
    {
        var options = NewDbOptions();

        await using (var seed = new ApplicationDbContext(options))
        {
            SeedReferenceData(seed);
            await seed.SaveChangesAsync();
        }

        await using var db = new ApplicationDbContext(options);
        var controller = new LoadingReceiptsController(
            db,
            new AuditService(db),
            NullLogger<LoadingReceiptsController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        var result = await controller.Index();

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<LoadingReceiptIndexViewModel>(view.Model);

        Assert.Equal(0, model.TotalCount);
        Assert.Equal(0m, (decimal)view.ViewData["SumQuantity"]!);
        Assert.Equal(0, (int)view.ViewData["WithTankCount"]!);
        Assert.Equal(0, (int)view.ViewData["WithReferenceCount"]!);
    }
}
