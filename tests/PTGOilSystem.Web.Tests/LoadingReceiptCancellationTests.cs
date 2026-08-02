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
using PTGOilSystem.Web.Models.Loading;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.LoadingReceipts;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// لغو امن رسید بارگیری: برگشت موجودی، کسری، فروش، دیسپچ و اسناد مالی؛ جلوگیری از لغو
/// رسیدِ مصرف‌شده؛ لغو گروهی اتمیک؛ اصلاح مقدار با لغو و ثبت دوباره.
/// </summary>
public class LoadingReceiptCancellationTests
{
    [Fact]
    public async Task Cancel_Simple_Receipt_Marks_Cancelled_And_Reverses_Inventory()
    {
        await using var db = NewDb();
        SeedLoadingContext(db);
        SeedInventoryReceipt(db, receiptId: 1, quantityMt: 40m, storageTankId: null);
        await db.SaveChangesAsync();

        var result = await NewService(db).CancelAsync([1], "مقدار اشتباه ثبت شده بود", actorUserId: 7);

        Assert.True(result.Succeeded);
        var receipt = await db.LoadingReceipts.SingleAsync(r => r.Id == 1);
        Assert.True(receipt.IsCancelled);
        Assert.Equal("مقدار اشتباه ثبت شده بود", receipt.CancellationReason);
        Assert.Equal(7, receipt.CancelledByUserId);
        Assert.NotNull(receipt.CancelledAtUtc);

        // رکورد اصلی حذف نمی‌شود؛ حرکت معکوس خروجی ثبت می‌شود.
        Assert.Equal(2, await db.InventoryMovements.CountAsync());
        var reversal = await db.InventoryMovements.SingleAsync(m => m.Direction == MovementDirection.Out);
        Assert.Equal(40m, reversal.QuantityMt);
        Assert.Contains("LoadingReceiptId=1", reversal.Notes);

        var allocation = await db.LoadingReceiptAllocations.SingleAsync();
        Assert.Equal(LoadingReceiptAllocationStatus.Cancelled, allocation.Status);
    }

    [Fact]
    public async Task Cancel_Tank_Receipt_Returns_Tank_Stock_To_Zero()
    {
        await using var db = NewDb();
        SeedLoadingContext(db);
        SeedInventoryReceipt(db, receiptId: 1, quantityMt: 60m, storageTankId: 1);
        await db.SaveChangesAsync();

        var stock = new StockService(db);
        Assert.Equal(60m, await stock.GetFreeQuantityMtAsync(productId: 1, terminalId: 1, storageTankId: 1));

        var result = await NewService(db).CancelAsync([1], "اشتباه مخزن", actorUserId: null);

        Assert.True(result.Succeeded);
        Assert.Equal(0m, await stock.GetFreeQuantityMtAsync(productId: 1, terminalId: 1, storageTankId: 1));
        var reversal = await db.InventoryMovements.SingleAsync(m => m.Direction == MovementDirection.Out);
        Assert.Equal(1, reversal.StorageTankId);
    }

    [Fact]
    public async Task Cancel_DirectDispatch_Receipt_Cancels_Dispatch()
    {
        await using var db = NewDb();
        SeedLoadingContext(db);
        SeedDirectDispatchReceipt(db, receiptId: 1, quantityMt: 25m, dispatchId: 11);
        await db.SaveChangesAsync();

        var result = await NewService(db).CancelAsync([1], "موتر اشتباه", actorUserId: null);

        Assert.True(result.Succeeded);
        Assert.Equal(DispatchStatus.Cancelled, (await db.TruckDispatches.SingleAsync(d => d.Id == 11)).Status);
        Assert.True((await db.LoadingReceipts.SingleAsync(r => r.Id == 1)).IsCancelled);
        // رسید تخلیه مستقیم حرکت موجودی نمی‌سازد، پس حرکت معکوسی هم ساخته نمی‌شود.
        Assert.Equal(0, await db.InventoryMovements.CountAsync());
    }

    [Fact]
    public async Task Cancel_DirectSale_Receipt_Cancels_Sale_And_Writes_Reversal_Ledger()
    {
        await using var db = NewDb();
        SeedLoadingContext(db);
        SeedDirectSaleReceipt(db, receiptId: 1, quantityMt: 30m, saleId: 21);
        await db.SaveChangesAsync();

        var result = await NewService(db).CancelAsync([1], "فروش اشتباه ثبت شد", actorUserId: null);

        Assert.True(result.Succeeded);
        Assert.True((await db.SalesTransactions.SingleAsync(s => s.Id == 21)).IsCancelled);

        var ledgers = await db.LedgerEntries.Where(l => l.SourceType == "Sale" && l.SourceId == 21).ToListAsync();
        Assert.Equal(2, ledgers.Count);
        // سند قبلی حذف نمی‌شود؛ سند معکوس اضافه می‌شود.
        Assert.Contains(ledgers, l => l.Reference != null && l.Reference.EndsWith("-CANCEL", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Cancel_Receipt_Cancels_Receipt_Loss_And_Returns_Loss_Quantity()
    {
        await using var db = NewDb();
        SeedLoadingContext(db);
        SeedInventoryReceipt(db, receiptId: 1, quantityMt: 40m, storageTankId: 1);
        db.InventoryMovements.Add(new InventoryMovement
        {
            Id = 900,
            ProductId = 1,
            ContractId = 1,
            TerminalId = 1,
            StorageTankId = 1,
            Direction = MovementDirection.Out,
            MovementDate = new DateTime(2026, 4, 25),
            QuantityMt = 2m
        });
        db.LossEvents.Add(new LossEvent
        {
            Id = 50,
            Stage = LossEventStage.ReceiptShortage,
            ProductId = 1,
            ContractId = 1,
            LoadingRegisterId = 1,
            LoadingReceiptId = 1,
            TerminalId = 1,
            StorageTankId = 1,
            EventDate = new DateTime(2026, 4, 25),
            ExpectedQuantityMt = 42m,
            ActualQuantityMt = 40m,
            DifferenceQuantityMt = 2m,
            ChargeableLossMt = 2m,
            InventoryMovementId = 900
        });
        await db.SaveChangesAsync();

        var result = await NewService(db).CancelAsync([1], "کسری اشتباه", actorUserId: null);

        Assert.True(result.Succeeded);
        Assert.True((await db.LossEvents.SingleAsync(e => e.Id == 50)).IsCancelled);
        // برگشت کسری: حرکت ورودیِ جبرانی ساخته می‌شود.
        Assert.Contains(
            await db.InventoryMovements.ToListAsync(),
            m => m.Direction == MovementDirection.In && m.Notes != null && m.Notes.Contains("LossEventId=50"));
    }

    [Fact]
    public async Task Cancel_Is_Blocked_When_Receipt_Stock_Already_Consumed()
    {
        await using var db = NewDb();
        SeedLoadingContext(db);
        SeedInventoryReceipt(db, receiptId: 1, quantityMt: 40m, storageTankId: 1);
        db.InventoryMovements.Add(new InventoryMovement
        {
            Id = 901,
            ProductId = 1,
            ContractId = 1,
            TerminalId = 1,
            StorageTankId = 1,
            Direction = MovementDirection.Out,
            MovementDate = new DateTime(2026, 4, 26),
            QuantityMt = 40m,
            Notes = "فروش از مخزن"
        });
        await db.SaveChangesAsync();

        var result = await NewService(db).CancelAsync([1], "تلاش برای لغو", actorUserId: null);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Blockers, b => b.Reason.Contains("مصرف شده"));
        Assert.False((await db.LoadingReceipts.SingleAsync(r => r.Id == 1)).IsCancelled);
        Assert.Equal(LoadingReceiptAllocationStatus.Completed, (await db.LoadingReceiptAllocations.SingleAsync()).Status);
    }

    [Fact]
    public async Task Cancel_Is_Blocked_When_Dispatch_Has_Linked_Sale()
    {
        await using var db = NewDb();
        SeedLoadingContext(db);
        SeedDirectDispatchReceipt(db, receiptId: 1, quantityMt: 25m, dispatchId: 11);
        db.TruckDispatches.Local.Single(d => d.Id == 11).SalesTransactionId = 77;
        await db.SaveChangesAsync();

        var result = await NewService(db).CancelAsync([1], "تلاش برای لغو", actorUserId: null);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Blockers, b => b.Reason.Contains("فروش"));
        Assert.NotEqual(DispatchStatus.Cancelled, (await db.TruckDispatches.SingleAsync(d => d.Id == 11)).Status);
    }

    [Fact]
    public async Task BulkCancel_Cancels_All_Selected_Receipts()
    {
        await using var db = NewDb();
        SeedLoadingContext(db);
        SeedInventoryReceipt(db, receiptId: 1, quantityMt: 20m, storageTankId: 1);
        SeedInventoryReceipt(db, receiptId: 2, quantityMt: 30m, storageTankId: 1);
        await db.SaveChangesAsync();

        var result = await NewService(db).CancelAsync([1, 2], "اصلاح گروهی", actorUserId: 3);

        Assert.True(result.Succeeded);
        Assert.Equal(2, result.CancelledReceiptIds.Count);
        Assert.All(await db.LoadingReceipts.ToListAsync(), r => Assert.True(r.IsCancelled));
        Assert.Equal(0m, await new StockService(db).GetFreeQuantityMtAsync(productId: 1, terminalId: 1, storageTankId: 1));
    }

    [Fact]
    public async Task BulkCancel_Rolls_Back_Completely_When_One_Receipt_Is_Blocked()
    {
        await using var db = NewDb();
        SeedLoadingContext(db);
        SeedInventoryReceipt(db, receiptId: 1, quantityMt: 20m, storageTankId: 1);
        SeedDirectDispatchReceipt(db, receiptId: 2, quantityMt: 25m, dispatchId: 11);
        db.TruckDispatches.Local.Single(d => d.Id == 11).SalesTransactionId = 77;
        await db.SaveChangesAsync();

        var result = await NewService(db).CancelAsync([1, 2], "اصلاح گروهی", actorUserId: null);

        Assert.False(result.Succeeded);
        Assert.Empty(result.CancelledReceiptIds);
        Assert.All(await db.LoadingReceipts.ToListAsync(), r => Assert.False(r.IsCancelled));
        Assert.DoesNotContain(await db.InventoryMovements.ToListAsync(), m => m.Direction == MovementDirection.Out);
    }

    [Fact]
    public async Task Cancelled_Receipt_Is_Excluded_From_Received_And_Remaining_Quantities()
    {
        await using var db = NewDb();
        SeedLoadingContext(db);
        SeedInventoryReceipt(db, receiptId: 1, quantityMt: 40m, storageTankId: 1);
        await db.SaveChangesAsync();

        await NewService(db).CancelAsync([1], "اشتباه", actorUserId: null);

        var controller = NewController(db);
        var view = Assert.IsType<ViewResult>(await controller.Create(loadingId: 1, returnUrl: null));
        var model = Assert.IsType<LoadingReceiptCreateViewModel>(view.Model);

        Assert.Equal(0m, model.AlreadyReceivedQuantityMt);
        Assert.Equal(100m, model.RemainingToReceiveMt);
    }

    [Fact]
    public async Task Cancelled_Receipt_Does_Not_Block_Loading_BulkDelete()
    {
        await using var db = NewDb();
        SeedLoadingContext(db);
        SeedInventoryReceipt(db, receiptId: 1, quantityMt: 40m, storageTankId: 1);
        await db.SaveChangesAsync();

        Assert.True(await db.LoadingReceipts.AnyAsync(r => r.LoadingRegisterId == 1 && !r.IsCancelled));

        await NewService(db).CancelAsync([1], "اشتباه", actorUserId: null);

        // بلاکر «رسید ثبت شده» فقط رسیدهای فعال را می‌شمارد.
        Assert.False(await db.LoadingReceipts.AnyAsync(r => r.LoadingRegisterId == 1 && !r.IsCancelled));
    }

    [Fact]
    public async Task Edit_Updates_Simple_Fields_Only_And_Keeps_Effective_Quantity()
    {
        await using var db = NewDb();
        SeedLoadingContext(db);
        SeedInventoryReceipt(db, receiptId: 1, quantityMt: 40m, storageTankId: 1);
        db.LoadingReceipts.Local.Single().ActualArrivedQuantityMt = 39m;
        await db.SaveChangesAsync();

        var controller = NewController(db);
        var result = await controller.Edit(1, new LoadingReceiptEditViewModel
        {
            Id = 1,
            LoadingRegisterId = 1,
            ArrivalDate = new DateTime(2026, 4, 28),
            LeakDate = new DateTime(2026, 4, 29),
            ActualArrivedQuantityMt = 10m,
            ReferenceDocument = "DOC-9",
            Notes = "یادداشت اصلاح‌شده"
        });

        Assert.IsType<RedirectToActionResult>(result);
        var receipt = await db.LoadingReceipts.SingleAsync();
        Assert.Equal(new DateTime(2026, 4, 28), receipt.ArrivalDate);
        Assert.Equal(new DateTime(2026, 4, 29), receipt.LeakDate);
        Assert.Equal("DOC-9", receipt.ReferenceDocument);
        Assert.Equal("یادداشت اصلاح‌شده", receipt.Notes);
        // مقدار اثرگذار با Update ساده تغییر نمی‌کند.
        Assert.Equal(39m, receipt.ActualArrivedQuantityMt);
        Assert.Equal(40m, receipt.ReceivedQuantityMt);
    }

    [Fact]
    public async Task Edit_Is_Rejected_For_Cancelled_Receipt()
    {
        await using var db = NewDb();
        SeedLoadingContext(db);
        SeedInventoryReceipt(db, receiptId: 1, quantityMt: 40m, storageTankId: 1);
        await db.SaveChangesAsync();
        await NewService(db).CancelAsync([1], "اشتباه", actorUserId: null);

        var controller = NewController(db);
        var result = await controller.Edit(1, returnUrl: null);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(LoadingReceiptsController.Details), redirect.ActionName);
    }

    [Fact]
    public async Task Correction_Cancels_Old_Receipt_And_Registers_New_Quantity()
    {
        await using var db = NewDb();
        SeedLoadingContext(db);
        SeedInventoryReceipt(db, receiptId: 1, quantityMt: 40m, storageTankId: 1);
        await db.SaveChangesAsync();

        var controller = NewController(db);
        var result = await controller.Create(new LoadingReceiptCreateViewModel
        {
            LoadingRegisterId = 1,
            CorrectionOfReceiptId = 1,
            CorrectionReason = "وزن درست ۳۵ تن است",
            ReceiptDestination = LoadingReceiptDestination.ToInventory,
            AllocationDestination = LoadingReceiptAllocationDestination.ToInventory,
            ReceiptDate = new DateTime(2026, 4, 24),
            TerminalId = 1,
            StorageTankId = 1,
            ReceivedQuantityMt = 35m,
            ReferenceDocument = "RWB-001-FIX"
        });

        Assert.IsType<RedirectToActionResult>(result);

        var oldReceipt = await db.LoadingReceipts.SingleAsync(r => r.Id == 1);
        Assert.True(oldReceipt.IsCancelled);
        Assert.Equal("وزن درست ۳۵ تن است", oldReceipt.CancellationReason);

        var newReceipt = await db.LoadingReceipts.SingleAsync(r => r.Id != 1);
        Assert.False(newReceipt.IsCancelled);
        Assert.Equal(35m, newReceipt.ReceivedQuantityMt);

        // موجودی خالص = فقط رسید اصلاح‌شده.
        Assert.Equal(35m, await new StockService(db).GetFreeQuantityMtAsync(productId: 1, terminalId: 1, storageTankId: 1));
    }

    [Fact]
    public async Task Cancel_Post_Rejects_Stale_RowVersion()
    {
        await using var db = NewDb();
        SeedLoadingContext(db);
        SeedInventoryReceipt(db, receiptId: 1, quantityMt: 40m, storageTankId: 1);
        db.LoadingReceipts.Local.Single().RowVersion = 5u;
        await db.SaveChangesAsync();

        var controller = NewController(db);
        var result = await controller.Cancel(1, "دلیل", rowVersion: 4u, returnUrl: null);

        Assert.IsType<RedirectToActionResult>(result);
        Assert.False((await db.LoadingReceipts.SingleAsync()).IsCancelled);
        Assert.True(controller.TempData.ContainsKey("err"));
    }

    [Fact]
    public async Task Cancel_Post_Requires_Reason()
    {
        await using var db = NewDb();
        SeedLoadingContext(db);
        SeedInventoryReceipt(db, receiptId: 1, quantityMt: 40m, storageTankId: 1);
        await db.SaveChangesAsync();

        var controller = NewController(db);
        var result = await controller.Cancel(1, reason: "   ", rowVersion: null, returnUrl: null);

        Assert.IsType<RedirectToActionResult>(result);
        Assert.False((await db.LoadingReceipts.SingleAsync()).IsCancelled);
        Assert.True(controller.TempData.ContainsKey("err"));
    }

    private static ApplicationDbContext NewDb()
        => new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static LoadingReceiptCancellationService NewService(ApplicationDbContext db)
        => new(db, new AuditService(db), NullLogger<LoadingReceiptCancellationService>.Instance);

    private static LoadingReceiptsController NewController(ApplicationDbContext db)
        => new(
            db,
            new AuditService(db),
            NullLogger<LoadingReceiptsController>.Instance,
            cancellation: NewService(db))
        {
            TempData = new TempDataDictionary(new DefaultHttpContext(), new InMemoryTempDataProvider()),
            Url = new UrlHelper(new ActionContext(new DefaultHttpContext(), new RouteData(), new ActionDescriptor()))
        };

    private static void SeedLoadingContext(ApplicationDbContext db)
    {
        db.Products.Add(new Product { Id = 1, Code = "GO", Name = "Gas Oil" });
        db.Companies.Add(new Company { Id = 1, Code = "PTG", Name = "Petro Trade Group" });
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
        db.Terminals.Add(new Terminal { Id = 1, Code = "TERM-1", Name = "Ilinka Terminal" });
        db.StorageTanks.Add(new StorageTank { Id = 1, TerminalId = 1, TankCode = "TK-01", ProductId = 1, CapacityMt = 8000m });
        db.LoadingRegisters.Add(new LoadingRegister
        {
            Id = 1,
            ContractId = 1,
            ProductId = 1,
            LoadingDate = new DateTime(2026, 4, 23),
            LoadedQuantityMt = 100m,
            BillOfLadingNumber = "RWB-001"
        });
    }

    private static void SeedInventoryReceipt(ApplicationDbContext db, int receiptId, decimal quantityMt, int? storageTankId)
    {
        var movementId = 1000 + receiptId;
        db.LoadingReceipts.Add(new LoadingReceipt
        {
            Id = receiptId,
            LoadingRegisterId = 1,
            ReceiptDestination = LoadingReceiptDestination.ToInventory,
            TerminalId = 1,
            StorageTankId = storageTankId,
            ReceiptDate = new DateTime(2026, 4, 24),
            ReceivedQuantityMt = quantityMt,
            ReferenceDocument = $"RCPT-{receiptId}"
        });
        db.InventoryMovements.Add(new InventoryMovement
        {
            Id = movementId,
            ProductId = 1,
            ContractId = 1,
            TerminalId = 1,
            StorageTankId = storageTankId,
            LoadingReceiptId = receiptId,
            Direction = MovementDirection.In,
            MovementDate = new DateTime(2026, 4, 24),
            QuantityMt = quantityMt,
            ReferenceDocument = $"RCPT-{receiptId}"
        });
        db.LoadingReceiptAllocations.Add(new LoadingReceiptAllocation
        {
            Id = receiptId,
            LoadingReceiptId = receiptId,
            Destination = LoadingReceiptAllocationDestination.ToInventory,
            Status = LoadingReceiptAllocationStatus.Completed,
            QuantityMt = quantityMt,
            SourcePurchaseContractId = 1,
            TerminalId = 1,
            StorageTankId = storageTankId,
            InventoryMovementId = movementId
        });
    }

    private static void SeedDirectDispatchReceipt(ApplicationDbContext db, int receiptId, decimal quantityMt, int dispatchId)
    {
        db.LoadingReceipts.Add(new LoadingReceipt
        {
            Id = receiptId,
            LoadingRegisterId = 1,
            ReceiptDestination = LoadingReceiptDestination.DirectDispatch,
            TerminalId = 1,
            ReceiptDate = new DateTime(2026, 4, 24),
            ReceivedQuantityMt = quantityMt
        });
        db.LoadingReceiptAllocations.Add(new LoadingReceiptAllocation
        {
            Id = receiptId,
            LoadingReceiptId = receiptId,
            Destination = LoadingReceiptAllocationDestination.DirectDispatchToTruck,
            Status = LoadingReceiptAllocationStatus.Completed,
            QuantityMt = quantityMt,
            SourcePurchaseContractId = 1,
            TerminalId = 1
        });
        db.Trucks.Add(new Truck { Id = dispatchId, PlateNumber = $"TRK-{dispatchId}" });
        db.TruckDispatches.Add(new TruckDispatch
        {
            Id = dispatchId,
            DispatchMode = TruckDispatchMode.DirectFromReceipt,
            LoadingReceiptAllocationId = receiptId,
            ContractId = 1,
            ProductId = 1,
            TruckId = dispatchId,
            DispatchDate = new DateTime(2026, 4, 24),
            Status = DispatchStatus.Loaded,
            LoadedQuantityMt = quantityMt
        });
    }

    private static void SeedDirectSaleReceipt(ApplicationDbContext db, int receiptId, decimal quantityMt, int saleId)
    {
        db.Customers.Add(new Customer { Id = 1, Code = "CUS-1", Name = "Customer 1" });
        db.LoadingReceipts.Add(new LoadingReceipt
        {
            Id = receiptId,
            LoadingRegisterId = 1,
            ReceiptDestination = LoadingReceiptDestination.DirectDispatch,
            TerminalId = 1,
            ReceiptDate = new DateTime(2026, 4, 24),
            ReceivedQuantityMt = quantityMt
        });
        db.SalesTransactions.Add(new SalesTransaction
        {
            Id = saleId,
            CompanyId = 1,
            CustomerId = 1,
            ProductId = 1,
            SaleStage = SaleStage.InTransit,
            InvoiceNumber = $"INV-{saleId}",
            SaleDate = new DateTime(2026, 4, 24),
            QuantityMt = quantityMt,
            Currency = "USD",
            UnitPriceInCurrency = 500m,
            UnitPriceUsd = 500m,
            TotalInCurrency = quantityMt * 500m,
            TotalUsd = quantityMt * 500m
        });
        db.LedgerEntries.Add(new LedgerEntry
        {
            Id = 300 + saleId,
            EntryDate = new DateTime(2026, 4, 24),
            Side = LedgerSide.Credit,
            AmountUsd = quantityMt * 500m,
            Currency = "USD",
            Description = $"فروش #{saleId}",
            SourceType = "Sale",
            SourceId = saleId,
            Reference = $"INV-{saleId}",
            ContractId = 1,
            CustomerId = 1
        });
        db.LoadingReceiptAllocations.Add(new LoadingReceiptAllocation
        {
            Id = receiptId,
            LoadingReceiptId = receiptId,
            Destination = LoadingReceiptAllocationDestination.DirectSale,
            Status = LoadingReceiptAllocationStatus.Completed,
            QuantityMt = quantityMt,
            SourcePurchaseContractId = 1,
            TerminalId = 1,
            SalesTransactionId = saleId
        });
    }

    private sealed class InMemoryTempDataProvider : ITempDataProvider
    {
        private IDictionary<string, object> _data = new Dictionary<string, object>();

        public IDictionary<string, object> LoadTempData(HttpContext context) => _data;

        public void SaveTempData(HttpContext context, IDictionary<string, object> values)
            => _data = new Dictionary<string, object>(values);
    }
}
