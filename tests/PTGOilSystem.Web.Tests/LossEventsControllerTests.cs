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
using PTGOilSystem.Web.Models.LossEvents;
using PTGOilSystem.Web.Services;
using Xunit;

namespace PTGOilSystem.Web.Tests;

public class LossEventsControllerTests
{
    [Fact]
    public async Task Create_Get_Preselects_Contract_And_ReturnUrl()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        await db.SaveChangesAsync();

        var controller = BuildController(db);

        var result = await controller.Create(contractId: 1, returnUrl: "/Contracts/Details/1?tab=losses");

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<LossEventCreateViewModel>(view.Model);
        Assert.Equal(1, model.ContractId);
        Assert.Equal("/Contracts/Details/1?tab=losses", model.ReturnUrl);
    }

    [Fact]
    public async Task Create_Post_Redirects_To_ReturnUrl_When_Provided()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        SeedPurchaseFlow(db);
        await db.SaveChangesAsync();

        var controller = BuildController(db);
        controller.Url = BuildUrlHelper();

        var result = await controller.Create(new LossEventCreateViewModel
        {
            Stage = LossEventStage.ReceiptShortage,
            ProductId = 1,
            ContractId = 1,
            LoadingRegisterId = 10,
            LoadingReceiptId = 20,
            EventDate = new DateTime(2026, 4, 26),
            ExpectedQuantityMt = 100m,
            ActualQuantityMt = 96.5m,
            ToleranceQuantityMt = 1m,
            Reference = "LOSS-RETURN-1",
            ReturnUrl = "/Contracts/Details/1?tab=losses"
        });

        var redirect = Assert.IsType<RedirectResult>(result);
        Assert.Equal("/Contracts/Details/1?tab=losses", redirect.Url);
    }

    [Fact]
    public async Task Edit_Get_Loads_Existing_LossEvent_And_ReturnUrl()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        SeedPurchaseFlow(db);
        db.LossEvents.Add(new LossEvent
        {
            Id = 70,
            Stage = LossEventStage.ReceiptShortage,
            ProductId = 1,
            ContractId = 1,
            LoadingRegisterId = 10,
            LoadingReceiptId = 20,
            EventDate = new DateTime(2026, 4, 26),
            ExpectedQuantityMt = 100m,
            ActualQuantityMt = 96.5m,
            DifferenceQuantityMt = 3.5m,
            ToleranceQuantityMt = 1m,
            AllowableLossMt = 1m,
            ChargeableLossMt = 2.5m,
            Reference = "LOSS-EDIT-GET"
        });
        await db.SaveChangesAsync();

        var controller = BuildController(db);

        var result = await controller.Edit(70, returnUrl: "/Loading/Details/10");

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<LossEventCreateViewModel>(view.Model);
        Assert.Equal(1, model.ProductId);
        Assert.Equal(10, model.LoadingRegisterId);
        Assert.Equal("/Loading/Details/10", model.ReturnUrl);
    }

    [Fact]
    public async Task Edit_Post_Updates_ComputedMetrics_And_Redirects_To_ReturnUrl()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        SeedPurchaseFlow(db);
        db.LossEvents.Add(new LossEvent
        {
            Id = 71,
            Stage = LossEventStage.ReceiptShortage,
            ProductId = 1,
            ContractId = 1,
            LoadingRegisterId = 10,
            LoadingReceiptId = 20,
            EventDate = new DateTime(2026, 4, 26),
            ExpectedQuantityMt = 100m,
            ActualQuantityMt = 96.5m,
            DifferenceQuantityMt = 3.5m,
            ToleranceQuantityMt = 1m,
            AllowableLossMt = 1m,
            ChargeableLossMt = 2.5m,
            Reference = "LOSS-EDIT-OLD"
        });
        await db.SaveChangesAsync();

        var controller = BuildController(db);
        controller.Url = BuildUrlHelper();

        var result = await controller.Edit(71, new LossEventCreateViewModel
        {
            Stage = LossEventStage.ReceiptShortage,
            ProductId = 1,
            ContractId = 1,
            LoadingRegisterId = 10,
            LoadingReceiptId = 20,
            EventDate = new DateTime(2026, 4, 27),
            ExpectedQuantityMt = 100m,
            ActualQuantityMt = 97m,
            ToleranceQuantityMt = 0.75m,
            ResponsiblePartyName = "Driver A",
            Reference = "LOSS-EDIT-NEW",
            ReturnUrl = "/Loading/Details/10"
        });

        var redirect = Assert.IsType<RedirectResult>(result);
        Assert.Equal("/Loading/Details/10", redirect.Url);

        var updated = await db.LossEvents.SingleAsync(e => e.Id == 71);
        Assert.Equal(3m, updated.DifferenceQuantityMt);
        Assert.Equal(0.75m, updated.AllowableLossMt);
        Assert.Equal(2.25m, updated.ChargeableLossMt);
        Assert.Equal("Driver A", updated.ResponsiblePartyName);
        Assert.Equal("LOSS-EDIT-NEW", updated.Reference);
    }

    [Fact]
    public async Task Create_Post_Persists_ReceiptShortage_With_ComputedMetrics_And_No_InventoryMovement()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        SeedPurchaseFlow(db);
        await db.SaveChangesAsync();

        var controller = BuildController(db);

        var result = await controller.Create(new LossEventCreateViewModel
        {
            Stage = LossEventStage.ReceiptShortage,
            ProductId = 1,
            ContractId = 1,
            LoadingRegisterId = 10,
            LoadingReceiptId = 20,
            EventDate = new DateTime(2026, 4, 26),
            ExpectedQuantityMt = 100m,
            ActualQuantityMt = 96.5m,
            ToleranceQuantityMt = 1m,
            Reference = "LOSS-RCPT-1"
        });

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Details", redirect.ActionName);

        var lossEvent = await db.LossEvents.SingleAsync();
        Assert.Equal(3.5m, lossEvent.DifferenceQuantityMt);
        Assert.Equal(1m, lossEvent.AllowableLossMt);
        Assert.Equal(2.5m, lossEvent.ChargeableLossMt);
        Assert.False(lossEvent.AffectsInventory);
        Assert.Null(lossEvent.InventoryMovementId);

        Assert.Equal(1, await db.InventoryMovements.CountAsync());
        var audit = await db.AuditLogs.SingleAsync();
        Assert.Equal(nameof(LossEvent), audit.EntityName);
    }

    [Fact]
    public async Task Create_Post_Persists_DispatchShortage_Without_Double_Decrement()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        SeedDispatchFlow(db);
        await db.SaveChangesAsync();

        var stockService = new StockService(db);
        var beforeQuantity = await stockService.GetFreeQuantityMtAsync(1, terminalId: 1, contractId: 2);

        var controller = BuildController(db);

        var result = await controller.Create(new LossEventCreateViewModel
        {
            Stage = LossEventStage.DispatchShortage,
            ProductId = 1,
            ContractId = 2,
            TruckDispatchId = 40,
            EventDate = new DateTime(2026, 4, 27),
            ExpectedQuantityMt = 30m,
            ActualQuantityMt = 28.75m,
            ToleranceQuantityMt = 0.5m,
            Reference = "LOSS-DSP-1"
        });

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Details", redirect.ActionName);

        var afterQuantity = await stockService.GetFreeQuantityMtAsync(1, terminalId: 1, contractId: 2);
        Assert.Equal(beforeQuantity, afterQuantity);
        Assert.Equal(2, await db.InventoryMovements.CountAsync());
        Assert.Null((await db.LossEvents.SingleAsync()).InventoryMovementId);
    }

    [Fact]
    public async Task Create_Post_TankNaturalLoss_With_AffectsInventory_Creates_OutMovement_And_Audit()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        SeedTerminalStock(db, quantityMt: 80m, contractId: 2);
        await db.SaveChangesAsync();

        var controller = BuildController(db);

        var result = await controller.Create(new LossEventCreateViewModel
        {
            Stage = LossEventStage.TankNaturalLoss,
            ProductId = 1,
            ContractId = 2,
            TerminalId = 1,
            StorageTankId = 1,
            EventDate = new DateTime(2026, 4, 28),
            ExpectedQuantityMt = 80m,
            ActualQuantityMt = 78m,
            ToleranceQuantityMt = 0.5m,
            AffectsInventory = true,
            Reference = "LOSS-TANK-1"
        });

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Details", redirect.ActionName);

        var lossEvent = await db.LossEvents.SingleAsync();
        Assert.True(lossEvent.AffectsInventory);
        Assert.True(lossEvent.InventoryMovementId.HasValue);
        Assert.Equal(1.5m, lossEvent.ChargeableLossMt);

        var stockOut = await db.InventoryMovements.Where(m => m.Direction == MovementDirection.Out).SingleAsync();
        Assert.Equal(2m, stockOut.QuantityMt);

        var freeQuantity = await new StockService(db).GetFreeQuantityMtAsync(1, terminalId: 1, contractId: 2);
        Assert.Equal(78m, freeQuantity);

        var logs = await db.AuditLogs.OrderBy(a => a.Id).ToListAsync();
        Assert.Equal(2, logs.Count);
        Assert.Contains(logs, l => l.EntityName == nameof(LossEvent));
        Assert.Contains(logs, l => l.EntityName == nameof(InventoryMovement));
    }

    [Fact]
    public async Task Create_Post_ManualAdjustment_With_AffectsInventory_Creates_OutMovement()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        SeedTerminalStock(db, quantityMt: 50m, contractId: 2);
        await db.SaveChangesAsync();

        var controller = BuildController(db);

        var result = await controller.Create(new LossEventCreateViewModel
        {
            Stage = LossEventStage.ManualAdjustment,
            ProductId = 1,
            ContractId = 2,
            TerminalId = 1,
            StorageTankId = 1,
            EventDate = new DateTime(2026, 4, 29),
            ExpectedQuantityMt = 50m,
            ActualQuantityMt = 49.25m,
            ToleranceQuantityMt = 0.1m,
            AffectsInventory = true,
            Reference = "LOSS-MANUAL-1"
        });

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Details", redirect.ActionName);

        var stockOut = await db.InventoryMovements.Where(m => m.Direction == MovementDirection.Out).SingleAsync();
        Assert.Equal(0.75m, stockOut.QuantityMt);

        var freeQuantity = await new StockService(db).GetFreeQuantityMtAsync(1, terminalId: 1, contractId: 2);
        Assert.Equal(49.25m, freeQuantity);
    }

    [Fact]
    public async Task Create_Post_ReceiptShortage_Ignores_Tampered_AffectsInventory_Flag()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        SeedTerminalStock(db, quantityMt: 100m, contractId: 1);
        await db.SaveChangesAsync();

        var controller = BuildController(db);

        // درخواست دستکاری‌شده: کاربر تیک اثر موجودی را با ابزار بیرونی true فرستاده است.
        var result = await controller.Create(new LossEventCreateViewModel
        {
            Stage = LossEventStage.ReceiptShortage,
            ProductId = 1,
            ContractId = 1,
            TerminalId = 1,
            StorageTankId = 1,
            EventDate = new DateTime(2026, 4, 27),
            ExpectedQuantityMt = 100m,
            ActualQuantityMt = 92m,
            AffectsInventory = true
        });

        Assert.IsType<RedirectToActionResult>(result);

        var lossEvent = await db.LossEvents.SingleAsync();
        Assert.False(lossEvent.AffectsInventory);
        Assert.Null(lossEvent.InventoryMovementId);
        Assert.Equal(8m, lossEvent.DifferenceQuantityMt);

        // فقط همان ورودی اولیه باقی می‌ماند؛ هیچ خروجی دوباره‌ای ساخته نشده است.
        Assert.False(await db.InventoryMovements.AnyAsync(m => m.Direction == MovementDirection.Out));
        var freeQuantity = await new StockService(db).GetFreeQuantityMtAsync(1, terminalId: 1, contractId: 1);
        Assert.Equal(100m, freeQuantity);
    }

    [Fact]
    public async Task Create_Post_TankNaturalLoss_Derives_Terminal_From_Selected_Tank()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        SeedTerminalStock(db, quantityMt: 40m, contractId: 2);
        await db.SaveChangesAsync();

        var controller = BuildController(db);

        // کاربر فقط مخزن را انتخاب کرده؛ ترمینال در فرم وجود ندارد.
        var result = await controller.Create(new LossEventCreateViewModel
        {
            Stage = LossEventStage.TankNaturalLoss,
            ProductId = 1,
            ContractId = 2,
            StorageTankId = 1,
            EventDate = new DateTime(2026, 4, 30),
            ExpectedQuantityMt = 40m,
            ActualQuantityMt = 38.5m
        });

        Assert.IsType<RedirectToActionResult>(result);

        var lossEvent = await db.LossEvents.SingleAsync();
        Assert.True(lossEvent.AffectsInventory);
        Assert.Equal(1, lossEvent.TerminalId);
        Assert.True(lossEvent.InventoryMovementId.HasValue);

        var stockOut = await db.InventoryMovements.Where(m => m.Direction == MovementDirection.Out).SingleAsync();
        Assert.Equal(1.5m, stockOut.QuantityMt);
        Assert.Equal(1, stockOut.TerminalId);
        Assert.Equal(1, stockOut.StorageTankId);
    }

    [Fact]
    public async Task Create_Post_TankNaturalLoss_Without_Tank_Is_Rejected()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        SeedTerminalStock(db, quantityMt: 40m, contractId: 2);
        await db.SaveChangesAsync();

        var controller = BuildController(db);

        var result = await controller.Create(new LossEventCreateViewModel
        {
            Stage = LossEventStage.TankNaturalLoss,
            ProductId = 1,
            ContractId = 2,
            EventDate = new DateTime(2026, 4, 30),
            ExpectedQuantityMt = 40m,
            ActualQuantityMt = 38.5m
        });

        Assert.IsType<ViewResult>(result);
        Assert.True(controller.ModelState.ContainsKey(nameof(LossEventCreateViewModel.StorageTankId)));
        Assert.Empty(await db.LossEvents.ToListAsync());
        Assert.False(await db.InventoryMovements.AnyAsync(m => m.Direction == MovementDirection.Out));
    }

    [Fact]
    public async Task Create_Post_TankFinalSettlement_Is_Rejected_From_Manual_Form()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        SeedTerminalStock(db, quantityMt: 40m, contractId: 2);
        await db.SaveChangesAsync();

        var controller = BuildController(db);

        // تسویهٔ نهایی مخزن فقط از صفحهٔ «تسویه مخزن» ساخته می‌شود.
        var result = await controller.Create(new LossEventCreateViewModel
        {
            Stage = LossEventStage.TankFinalSettlement,
            ProductId = 1,
            ContractId = 2,
            StorageTankId = 1,
            EventDate = new DateTime(2026, 4, 30),
            ExpectedQuantityMt = 40m,
            ActualQuantityMt = 0m
        });

        Assert.IsType<ViewResult>(result);
        Assert.True(controller.ModelState.ContainsKey(nameof(LossEventCreateViewModel.Stage)));
        Assert.Empty(await db.LossEvents.ToListAsync());
    }

    [Fact]
    public async Task Create_Get_From_Tank_Prefills_Tank_Terminal_And_Stage()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        await db.SaveChangesAsync();

        var controller = BuildController(db);

        var result = await controller.Create(storageTankId: 1, returnUrl: "/StorageTanks/Details/1");

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<LossEventCreateViewModel>(view.Model);
        Assert.Equal(1, model.StorageTankId);
        Assert.Equal(1, model.TerminalId);
        Assert.Equal(1, model.ProductId);
        Assert.Equal(LossEventStage.TankNaturalLoss, model.Stage);
    }

    private static DbContextOptions<ApplicationDbContext> NewDbOptions()
        => new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

    private static LossEventsController BuildController(ApplicationDbContext db)
        => new(
            db,
            new StockService(db),
            new AuditService(db),
            NullLogger<LossEventsController>.Instance)
        {
            TempData = BuildTempData()
        };

    private static void SeedReferenceData(ApplicationDbContext db)
    {
        db.Products.Add(new Product { Id = 1, Code = "GO", Name = "Gas Oil" });
        db.Companies.Add(new Company { Id = 1, Code = "PTG", Name = "PTG" });
        db.Suppliers.Add(new Supplier { Id = 1, Name = "Supplier A" });
        db.Customers.Add(new Customer { Id = 1, Name = "Customer A" });
        db.Terminals.Add(new Terminal { Id = 1, Code = "TERM-1", Name = "Terminal 1" });
        db.StorageTanks.Add(new StorageTank { Id = 1, TerminalId = 1, TankCode = "TK-01", ProductId = 1, CapacityMt = 1000m });
        db.Trucks.Add(new Truck { Id = 1, PlateNumber = "TRK-1" });
        db.Contracts.Add(new Contract
        {
            Id = 1,
            ContractNumber = "PUR-001",
            ContractType = ContractType.Purchase,
            CompanyId = 1,
            ProductId = 1,
            SupplierId = 1,
            ContractDate = new DateTime(2026, 4, 23),
            QuantityMt = 100m,
            PricingMethod = PricingMethod.Fixed,
            UnitPriceUsd = 450m
        });
        db.Contracts.Add(new Contract
        {
            Id = 2,
            ContractNumber = "SAL-001",
            ContractType = ContractType.Sale,
            CompanyId = 1,
            ProductId = 1,
            CustomerId = 1,
            ContractDate = new DateTime(2026, 4, 23),
            QuantityMt = 100m,
            PricingMethod = PricingMethod.Fixed,
            UnitPriceUsd = 500m
        });
    }

    private static void SeedPurchaseFlow(ApplicationDbContext db)
    {
        db.LoadingRegisters.Add(new LoadingRegister
        {
            Id = 10,
            ContractId = 1,
            ProductId = 1,
            LoadingDate = new DateTime(2026, 4, 24),
            LoadedQuantityMt = 100m
        });
        db.LoadingReceipts.Add(new LoadingReceipt
        {
            Id = 20,
            LoadingRegisterId = 10,
            TerminalId = 1,
            StorageTankId = 1,
            ReceiptDate = new DateTime(2026, 4, 25),
            ReceivedQuantityMt = 96.5m
        });
        db.InventoryMovements.Add(new InventoryMovement
        {
            Id = 30,
            LoadingReceiptId = 20,
            ProductId = 1,
            ContractId = 1,
            TerminalId = 1,
            StorageTankId = 1,
            Direction = MovementDirection.In,
            MovementDate = new DateTime(2026, 4, 25),
            QuantityMt = 96.5m,
            ReferenceDocument = "RCPT-20"
        });
    }

    private static void SeedDispatchFlow(ApplicationDbContext db)
    {
        db.TruckDispatches.Add(new TruckDispatch
        {
            Id = 40,
            ContractId = 2,
            ProductId = 1,
            TruckId = 1,
            DispatchDate = new DateTime(2026, 4, 26),
            LoadedQuantityMt = 30m
        });
        db.InventoryMovements.Add(new InventoryMovement
        {
            Id = 41,
            ProductId = 1,
            ContractId = 2,
            TerminalId = 1,
            StorageTankId = 1,
            Direction = MovementDirection.In,
            MovementDate = new DateTime(2026, 4, 25),
            QuantityMt = 80m,
            ReferenceDocument = "GRN-40"
        });
        db.InventoryMovements.Add(new InventoryMovement
        {
            Id = 42,
            ProductId = 1,
            ContractId = 2,
            TerminalId = 1,
            StorageTankId = 1,
            Direction = MovementDirection.Out,
            MovementDate = new DateTime(2026, 4, 26),
            QuantityMt = 30m,
            ReferenceDocument = "DSP-40"
        });
    }

    private static void SeedTerminalStock(ApplicationDbContext db, decimal quantityMt, int contractId)
    {
        db.InventoryMovements.Add(new InventoryMovement
        {
            Id = 50,
            ProductId = 1,
            ContractId = contractId,
            TerminalId = 1,
            StorageTankId = 1,
            Direction = MovementDirection.In,
            MovementDate = new DateTime(2026, 4, 24),
            QuantityMt = quantityMt,
            ReferenceDocument = "GRN-STOCK"
        });
    }

    private static TempDataDictionary BuildTempData()
        => new(new DefaultHttpContext(), new InMemoryTempDataProvider());

    private static IUrlHelper BuildUrlHelper()
        => new UrlHelper(new ActionContext(new DefaultHttpContext(), new RouteData(), new ActionDescriptor()));

    private sealed class InMemoryTempDataProvider : ITempDataProvider
    {
        private IDictionary<string, object> _data = new Dictionary<string, object>();

        public IDictionary<string, object> LoadTempData(HttpContext context) => _data;

        public void SaveTempData(HttpContext context, IDictionary<string, object> values)
            => _data = new Dictionary<string, object>(values);
    }
}
