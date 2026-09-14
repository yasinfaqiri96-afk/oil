using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Controllers;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Contracts;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Security;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.ContractClosure;
using PTGOilSystem.Web.Services.OperationalPeriod;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// بستن قرارداد: کنترل موارد باز پیش از بستن، قفل کامل ثبت/ویرایش/حذف پس از بستن
/// (پشتوانهٔ SaveChanges)، بازگشایی فقط با دلیل و ثبت Audit، و بی‌اثر بودن روی قراردادهای دیگر.
/// </summary>
public sealed class ContractClosureTests
{
    private const int ContractId = 1;
    private const int OtherContractId = 2;

    private static ApplicationDbContext NewDb()
        => new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static async Task<ApplicationDbContext> SeededDbAsync()
    {
        var db = NewDb();
        // فرم ویرایش Company/Product را Include می‌کند (رابطهٔ اجباری = INNER JOIN)، پس باید وجود داشته باشند.
        db.Units.Add(new Unit { Id = 1, Code = "MT", Name = "Metric Ton", Symbol = "MT", IsActive = true });
        db.Companies.Add(new Company { Id = 1, Code = "PTG", Name = "PTG", IsActive = true });
        db.Products.Add(new Product { Id = 1, Code = "GO", Name = "Gas Oil", UnitId = 1, UnitOfMeasure = "MT", IsActive = true });
        db.Suppliers.Add(new Supplier { Id = 1, Name = "Supplier A", IsActive = true });
        db.Drivers.Add(new Driver { Id = 1, FullName = "Driver A" });
        db.Contracts.AddRange(NewContract(ContractId, "PUR-001"), NewContract(OtherContractId, "PUR-002"));
        await db.SaveChangesAsync();
        return db;
    }

    private static Contract NewContract(int id, string number) => new()
    {
        Id = id,
        ContractName = $"قرارداد {number}",
        ContractNumber = number,
        ContractType = ContractType.Purchase,
        Status = ContractStatus.Active,
        CompanyId = 1,
        ProductId = 1,
        SupplierId = 1,
        ContractDate = new DateTime(2026, 1, 1),
        QuantityMt = 100m,
        PricingMethod = PricingMethod.Fixed,
        UnitPriceUsd = 500m
    };

    private static ContractClosureService Service(ApplicationDbContext db)
        => new(db, new AuditService(db), new StockService(db));

    private static ExpenseTransaction Expense(int contractId) => new()
    {
        ExpenseTypeId = 1,
        ContractId = contractId,
        ExpenseDate = new DateTime(2026, 2, 1),
        Amount = 100m,
        AmountUsd = 100m,
        Description = "کرایه"
    };

    private static LedgerEntry Ledger(LedgerSide side, decimal amountUsd, int? supplierId = null, int? driverId = null) => new()
    {
        EntryDate = new DateTime(2026, 2, 1),
        Side = side,
        AmountUsd = amountUsd,
        SourceType = "Test",
        SourceId = 1,
        ContractId = ContractId,
        SupplierId = supplierId,
        DriverId = driverId
    };

    private static async Task CloseCleanContractAsync(ApplicationDbContext db)
    {
        var result = await Service(db).CloseAsync(ContractId, "تسویه کامل", actorUserId: 7);
        Assert.True(result.Succeeded, result.Error);
    }

    private static ContractsController Controller(ApplicationDbContext db) => new(db, new AuditService(db))
    {
        ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        TempData = new TempDataDictionary(new DefaultHttpContext(), new NullTempDataProvider())
    };

    // ------------------------------------------------------------------
    // ۱ — کنترل‌های پیش از بستن
    // ------------------------------------------------------------------

    [Fact]
    public async Task Evaluate_ContractWithNothingOpen_CanClose()
    {
        await using var db = await SeededDbAsync();

        var check = await Service(db).EvaluateAsync(ContractId);

        Assert.NotNull(check);
        Assert.True(check!.CanClose);
        Assert.Empty(check.Blockers);
    }

    [Fact]
    public async Task Evaluate_UnknownContract_ReturnsNull()
    {
        await using var db = await SeededDbAsync();

        Assert.Null(await Service(db).EvaluateAsync(999));
    }

    [Fact]
    public async Task Evaluate_ListsOpenTransportLegAndUndeliveredDispatch()
    {
        await using var db = await SeededDbAsync();
        db.InventoryTransportLegs.AddRange(
            new InventoryTransportLeg { SourcePurchaseContractId = ContractId, QuantityMt = 30m, Status = InventoryTransportLegStatus.InTransit, LoadedDate = new DateTime(2026, 2, 1) },
            new InventoryTransportLeg { SourcePurchaseContractId = ContractId, QuantityMt = 10m, Status = InventoryTransportLegStatus.Received, LoadedDate = new DateTime(2026, 2, 1) });
        db.TruckDispatches.AddRange(
            new TruckDispatch { ContractId = ContractId, LoadedQuantityMt = 25m, Status = DispatchStatus.Loaded, DispatchDate = new DateTime(2026, 2, 2) },
            new TruckDispatch { ContractId = ContractId, LoadedQuantityMt = 25m, Status = DispatchStatus.Delivered, DispatchDate = new DateTime(2026, 2, 2) });
        await db.SaveChangesAsync();

        var check = await Service(db).EvaluateAsync(ContractId);

        Assert.False(check!.CanClose);
        var transport = check.Blockers.Where(b => b.Kind == ContractClosureBlockerKind.Transport).ToList();
        Assert.Equal(2, transport.Count);
        Assert.Contains(transport, b => b.Message.Contains("1 حمل موجودی") && b.Message.Contains("30"));
        Assert.Contains(transport, b => b.Message.Contains("1 ارسال موتر") && b.Message.Contains("25"));
    }

    [Fact]
    public async Task Evaluate_LoadingWithoutActiveReceipt_IsAnOpenDelivery()
    {
        await using var db = await SeededDbAsync();
        var received = new LoadingRegister { ContractId = ContractId, ProductId = 1, LoadedQuantityMt = 40m, LoadingDate = new DateTime(2026, 2, 1) };
        var cancelledReceiptOnly = new LoadingRegister { ContractId = ContractId, ProductId = 1, LoadedQuantityMt = 60m, LoadingDate = new DateTime(2026, 2, 1) };
        db.LoadingRegisters.AddRange(received, cancelledReceiptOnly);
        await db.SaveChangesAsync();
        db.LoadingReceipts.AddRange(
            new LoadingReceipt { LoadingRegisterId = received.Id, TerminalId = 1, ReceivedQuantityMt = 40m, ReceiptDate = new DateTime(2026, 2, 3) },
            new LoadingReceipt { LoadingRegisterId = cancelledReceiptOnly.Id, TerminalId = 1, ReceivedQuantityMt = 60m, ReceiptDate = new DateTime(2026, 2, 3), IsCancelled = true });
        await db.SaveChangesAsync();

        var check = await Service(db).EvaluateAsync(ContractId);

        var delivery = Assert.Single(check!.Blockers, b => b.Kind == ContractClosureBlockerKind.Delivery);
        Assert.Contains("1 بارگیری", delivery.Message);
        Assert.Contains("60", delivery.Message);
    }

    [Fact]
    public async Task Evaluate_RemainingStock_IsAnOpenItem()
    {
        await using var db = await SeededDbAsync();
        db.InventoryMovements.AddRange(
            new InventoryMovement { ContractId = ContractId, ProductId = 1, TerminalId = 1, Direction = MovementDirection.In, QuantityMt = 50m, MovementDate = new DateTime(2026, 2, 1) },
            new InventoryMovement { ContractId = ContractId, ProductId = 1, TerminalId = 1, Direction = MovementDirection.Out, QuantityMt = 20m, MovementDate = new DateTime(2026, 2, 2) });
        await db.SaveChangesAsync();

        var check = await Service(db).EvaluateAsync(ContractId);

        var stock = Assert.Single(check!.Blockers, b => b.Kind == ContractClosureBlockerKind.Stock);
        Assert.Contains("30", stock.Message);
    }

    [Fact]
    public async Task Evaluate_DraftSarrafSettlement_IsAnOpenPayment()
    {
        await using var db = await SeededDbAsync();
        db.SarrafSettlements.AddRange(
            new SarrafSettlement { SarrafId = 1, ContractId = ContractId, SettlementDate = new DateTime(2026, 2, 1), Status = SarrafSettlementStatus.Draft },
            new SarrafSettlement { SarrafId = 1, ContractId = ContractId, SettlementDate = new DateTime(2026, 2, 1), Status = SarrafSettlementStatus.Posted });
        await db.SaveChangesAsync();

        var check = await Service(db).EvaluateAsync(ContractId);

        var payment = Assert.Single(check!.Blockers, b => b.Kind == ContractClosureBlockerKind.Payment);
        Assert.Contains("1 تسویهٔ صرافی", payment.Message);
    }

    [Fact]
    public async Task Evaluate_OpenLedgerBalance_IsReportedPerCounterparty()
    {
        await using var db = await SeededDbAsync();
        // تأمین‌کننده: ۱۰۰۰ بستانکار − ۴۰۰ پرداخت = ۶۰۰ بدهیِ ما.
        // راننده: ۶۰۰ بدهکار — جمع کل صفر است ولی هیچ‌کدام تسویه نشده‌اند و نباید یکدیگر را خنثی کنند.
        db.LedgerEntries.AddRange(
            Ledger(LedgerSide.Credit, 1000m, supplierId: 1),
            Ledger(LedgerSide.Debit, 400m, supplierId: 1),
            Ledger(LedgerSide.Debit, 600m, driverId: 1));
        await db.SaveChangesAsync();

        var check = await Service(db).EvaluateAsync(ContractId);

        var balance = Assert.Single(check!.Blockers, b => b.Kind == ContractClosureBlockerKind.Balance);
        Assert.Contains("Supplier A", balance.Message);
        Assert.Contains("600.00 USD", balance.Message);
        Assert.Contains("قابل پرداخت", balance.Message);
        var expense = Assert.Single(check.Blockers, b => b.Kind == ContractClosureBlockerKind.Expense);
        Assert.Contains("Driver A", expense.Message);
        Assert.Contains("قابل دریافت", expense.Message);
    }

    [Fact]
    public async Task Evaluate_SettledLedgerBalance_DoesNotBlock()
    {
        await using var db = await SeededDbAsync();
        db.LedgerEntries.AddRange(
            Ledger(LedgerSide.Credit, 1000m, supplierId: 1),
            Ledger(LedgerSide.Debit, 1000m, supplierId: 1));
        await db.SaveChangesAsync();

        var check = await Service(db).EvaluateAsync(ContractId);

        Assert.True(check!.CanClose);
    }

    // ------------------------------------------------------------------
    // ۱-ب — منشأمحور: حمل مستقیم از بارگیری، جزئی، لغو، فروش مستقیم، کسری، موتر، فروش مرحله‌ای
    // ------------------------------------------------------------------

    private static async Task<LoadingRegister> AddLoadingAsync(ApplicationDbContext db, decimal loadedMt)
    {
        var loading = new LoadingRegister { ContractId = ContractId, ProductId = 1, LoadedQuantityMt = loadedMt, LoadingDate = new DateTime(2026, 2, 1) };
        db.LoadingRegisters.Add(loading);
        await db.SaveChangesAsync();
        return loading;
    }

    private static async Task<InventoryTransportLeg> AddDirectLegAsync(
        ApplicationDbContext db,
        int loadingId,
        decimal allocatedMt,
        InventoryTransportLegStatus status)
    {
        var leg = new InventoryTransportLeg
        {
            SourcePurchaseContractId = ContractId,
            ProductId = 1,
            QuantityMt = allocatedMt,
            Status = status,
            LoadedDate = new DateTime(2026, 2, 2)
        };
        leg.Allocations.Add(new InventoryTransportLegAllocation
        {
            SourcePurchaseContractId = ContractId,
            SourceLoadingRegisterId = loadingId,
            QuantityMt = allocatedMt
        });
        db.InventoryTransportLegs.Add(leg);
        await db.SaveChangesAsync();
        return leg;
    }

    private static InventoryTransportReceipt LegReceipt(
        int legId,
        decimal receivedMt,
        InventoryTransportReceiptDestination destination,
        decimal shortageMt = 0m,
        bool cancelled = false) => new()
        {
            InventoryTransportLegId = legId,
            ReceiptDate = new DateTime(2026, 2, 5),
            ReceivedQuantityMt = receivedMt,
            ShortageQuantityMt = shortageMt,
            ReceiptDestination = destination,
            IsCancelled = cancelled
        };

    private static string Describe(ContractClosureCheck check)
        => string.Join(" | ", check.Blockers.Select(b => $"{b.Kind}: {b.Message}"));

    [Fact]
    public async Task DirectFromLoading_FullyAllocatedToOpenTransport_IsCountedOnceUnderTransport()
    {
        await using var db = await SeededDbAsync();
        var loading = await AddLoadingAsync(db, 100m);
        await AddDirectLegAsync(db, loading.Id, 100m, InventoryTransportLegStatus.InTransit);

        var check = await Service(db).EvaluateAsync(ContractId);

        // بارگیری رسید ندارد، ولی کل مقدارش به حمل رفته؛ پس فقط حمل باز است، نه هر دو.
        Assert.DoesNotContain(check!.Blockers, b => b.Kind == ContractClosureBlockerKind.Delivery);
        var transport = Assert.Single(check.Blockers, b => b.Kind == ContractClosureBlockerKind.Transport);
        Assert.Contains("(100 تن)", transport.Message);
    }

    [Theory]
    [InlineData(InventoryTransportReceiptDestination.ToInventory)]
    [InlineData(InventoryTransportReceiptDestination.DirectSale)]
    public async Task DirectFromLoading_CompletedTransportOutcome_WithShortage_IsResolved(InventoryTransportReceiptDestination destination)
    {
        await using var db = await SeededDbAsync();
        var loading = await AddLoadingAsync(db, 100m);
        var leg = await AddDirectLegAsync(db, loading.Id, 100m, InventoryTransportLegStatus.Received);
        db.InventoryTransportReceipts.Add(LegReceipt(leg.Id, 98m, destination, shortageMt: 2m));
        // کسریِ رسید حمل (با سرصفحهٔ بارگیری) نباید دوباره از باقیماندهٔ بارگیری کم شود.
        db.LossEvents.Add(new LossEvent
        {
            Stage = LossEventStage.ReceiptShortage,
            ProductId = 1,
            ContractId = ContractId,
            TransportLegId = leg.Id,
            LoadingRegisterId = loading.Id,
            EventDate = new DateTime(2026, 2, 5),
            DifferenceQuantityMt = 2m
        });
        await db.SaveChangesAsync();

        var check = await Service(db).EvaluateAsync(ContractId);

        Assert.True(check!.CanClose, Describe(check));
    }

    [Fact]
    public async Task DirectFromLoading_PartialAllocationAndPartialReceipt_OnlyRemaindersBlock()
    {
        await using var db = await SeededDbAsync();
        var loading = await AddLoadingAsync(db, 100m);
        var leg = await AddDirectLegAsync(db, loading.Id, 60m, InventoryTransportLegStatus.InTransit);
        db.InventoryTransportReceipts.Add(LegReceipt(leg.Id, 40m, InventoryTransportReceiptDestination.ToInventory));
        await db.SaveChangesAsync();

        var check = await Service(db).EvaluateAsync(ContractId);

        // ۱۰۰ = ۴۰ (مانده روی بارگیری) + ۴۰ (رسیده) + ۲۰ (هنوز روی وسیله). هیچ مقداری دو بار نیست.
        Assert.Contains("(40 تن)", Assert.Single(check!.Blockers, b => b.Kind == ContractClosureBlockerKind.Delivery).Message);
        Assert.Contains("(20 تن)", Assert.Single(check.Blockers, b => b.Kind == ContractClosureBlockerKind.Transport).Message);
    }

    [Fact]
    public async Task DirectFromLoading_CancelledTransport_ReturnsQuantityToLoadingExactlyOnce()
    {
        await using var db = await SeededDbAsync();
        var loading = await AddLoadingAsync(db, 100m);
        await AddDirectLegAsync(db, loading.Id, 100m, InventoryTransportLegStatus.Cancelled);

        var check = await Service(db).EvaluateAsync(ContractId);

        Assert.DoesNotContain(check!.Blockers, b => b.Kind == ContractClosureBlockerKind.Transport);
        Assert.Contains("(100 تن)", Assert.Single(check.Blockers, b => b.Kind == ContractClosureBlockerKind.Delivery).Message);
    }

    [Fact]
    public async Task DirectFromLoading_CancelledTransportReceipt_ReopensOnlyTheTransport()
    {
        await using var db = await SeededDbAsync();
        var loading = await AddLoadingAsync(db, 100m);
        var leg = await AddDirectLegAsync(db, loading.Id, 100m, InventoryTransportLegStatus.InTransit);
        db.InventoryTransportReceipts.Add(LegReceipt(leg.Id, 100m, InventoryTransportReceiptDestination.DirectSale, cancelled: true));
        await db.SaveChangesAsync();

        var check = await Service(db).EvaluateAsync(ContractId);

        Assert.DoesNotContain(check!.Blockers, b => b.Kind == ContractClosureBlockerKind.Delivery);
        Assert.Contains("(100 تن)", Assert.Single(check.Blockers, b => b.Kind == ContractClosureBlockerKind.Transport).Message);
    }

    [Fact]
    public async Task LoadingReceiptShortageLoss_ResolvesTheLoadingRemainder()
    {
        await using var db = await SeededDbAsync();
        var loading = await AddLoadingAsync(db, 100m);
        var receipt = new LoadingReceipt { LoadingRegisterId = loading.Id, TerminalId = 1, ReceivedQuantityMt = 97m, ReceiptDate = new DateTime(2026, 2, 3) };
        db.LoadingReceipts.Add(receipt);
        await db.SaveChangesAsync();
        db.LossEvents.Add(new LossEvent
        {
            Stage = LossEventStage.ReceiptShortage,
            ProductId = 1,
            ContractId = ContractId,
            LoadingReceiptId = receipt.Id,
            EventDate = new DateTime(2026, 2, 3),
            DifferenceQuantityMt = 3m
        });
        await db.SaveChangesAsync();

        var check = await Service(db).EvaluateAsync(ContractId);

        Assert.True(check!.CanClose, Describe(check));
    }

    [Fact]
    public async Task MultiContractTransport_CountsOnlyThisContractsShare()
    {
        await using var db = await SeededDbAsync();
        var leg = new InventoryTransportLeg { SourcePurchaseContractId = OtherContractId, ProductId = 1, QuantityMt = 100m, Status = InventoryTransportLegStatus.InTransit, LoadedDate = new DateTime(2026, 2, 2) };
        leg.Allocations.Add(new InventoryTransportLegAllocation { SourcePurchaseContractId = ContractId, QuantityMt = 30m });
        leg.Allocations.Add(new InventoryTransportLegAllocation { SourcePurchaseContractId = OtherContractId, QuantityMt = 70m });
        db.InventoryTransportLegs.Add(leg);
        await db.SaveChangesAsync();

        var check = await Service(db).EvaluateAsync(ContractId);

        Assert.Contains("(30 تن)", Assert.Single(check!.Blockers, b => b.Kind == ContractClosureBlockerKind.Transport).Message);
    }

    [Fact]
    public async Task TruckDispatch_SoldIsResolved_CancelledSaleKeepsItOpen()
    {
        await using var db = await SeededDbAsync();
        var sold = new TruckDispatch { ContractId = ContractId, LoadedQuantityMt = 25m, Status = DispatchStatus.Loaded, DispatchDate = new DateTime(2026, 2, 2) };
        var saleCancelled = new TruckDispatch { ContractId = ContractId, LoadedQuantityMt = 25m, Status = DispatchStatus.Loaded, DispatchDate = new DateTime(2026, 2, 2) };
        db.TruckDispatches.AddRange(sold, saleCancelled);
        await db.SaveChangesAsync();
        db.SalesTransactions.AddRange(
            new SalesTransaction { ContractId = ContractId, TruckDispatchId = sold.Id, CustomerId = 1, ProductId = 1, InvoiceNumber = "INV-D1", SaleDate = new DateTime(2026, 2, 3), QuantityMt = 25m },
            new SalesTransaction { ContractId = ContractId, TruckDispatchId = saleCancelled.Id, CustomerId = 1, ProductId = 1, InvoiceNumber = "INV-D2", SaleDate = new DateTime(2026, 2, 3), QuantityMt = 25m, IsCancelled = true });
        await db.SaveChangesAsync();

        var check = await Service(db).EvaluateAsync(ContractId);

        var transport = Assert.Single(check!.Blockers, b => b.Kind == ContractClosureBlockerKind.Transport);
        Assert.Contains("1 ارسال موتر", transport.Message);
        Assert.Contains("(25 تن)", transport.Message);
    }

    [Fact]
    public async Task CompatibilityDispatchOfVehicleTransfer_IsNotCountedTwice()
    {
        await using var db = await SeededDbAsync();
        const int parentReceiptId = 77;
        db.TruckDispatches.Add(new TruckDispatch { ContractId = ContractId, InventoryTransportReceiptId = parentReceiptId, LoadedQuantityMt = 40m, Status = DispatchStatus.Loaded, DispatchDate = new DateTime(2026, 2, 2) });
        var child = new InventoryTransportLeg { SourcePurchaseContractId = ContractId, ProductId = 1, QuantityMt = 40m, Status = InventoryTransportLegStatus.InTransit, LoadedDate = new DateTime(2026, 2, 2) };
        child.Allocations.Add(new InventoryTransportLegAllocation { SourcePurchaseContractId = ContractId, SourceTransportReceiptId = parentReceiptId, QuantityMt = 40m });
        db.InventoryTransportLegs.Add(child);
        await db.SaveChangesAsync();

        var check = await Service(db).EvaluateAsync(ContractId);

        var transport = Assert.Single(check!.Blockers, b => b.Kind == ContractClosureBlockerKind.Transport);
        Assert.Contains("حمل موجودی", transport.Message);
        Assert.DoesNotContain(check.Blockers, b => b.Message.Contains("ارسال موتر"));
    }

    [Fact]
    public async Task ReceiptAllocations_AreSourceAware()
    {
        await using var db = await SeededDbAsync();
        var loading = await AddLoadingAsync(db, 100m);
        var receipt = new LoadingReceipt { LoadingRegisterId = loading.Id, TerminalId = 1, ReceivedQuantityMt = 100m, ReceiptDate = new DateTime(2026, 2, 3) };
        db.LoadingReceipts.Add(receipt);
        await db.SaveChangesAsync();
        var directDispatch = new LoadingReceiptAllocation { LoadingReceiptId = receipt.Id, TerminalId = 1, SourcePurchaseContractId = ContractId, Destination = LoadingReceiptAllocationDestination.DirectDispatchToTruck, Status = LoadingReceiptAllocationStatus.InTransit, QuantityMt = 50m };
        db.LoadingReceiptAllocations.AddRange(
            directDispatch,
            new LoadingReceiptAllocation { LoadingReceiptId = receipt.Id, TerminalId = 1, SourcePurchaseContractId = ContractId, Destination = LoadingReceiptAllocationDestination.TransferToOtherTerminal, Status = LoadingReceiptAllocationStatus.InTransit, QuantityMt = 10m },
            new LoadingReceiptAllocation { LoadingReceiptId = receipt.Id, TerminalId = 1, SourcePurchaseContractId = ContractId, Destination = LoadingReceiptAllocationDestination.DirectSale, Status = LoadingReceiptAllocationStatus.Completed, QuantityMt = 40m });
        await db.SaveChangesAsync();
        // ۳۰ تن از ۵۰ با موتر ارسال و تحویل شده؛ فقط ۲۰ تن ارسال‌نشده مانده است.
        db.TruckDispatches.Add(new TruckDispatch { ContractId = ContractId, LoadingReceiptAllocationId = directDispatch.Id, LoadedQuantityMt = 30m, Status = DispatchStatus.Delivered, DispatchDate = new DateTime(2026, 2, 4) });
        await db.SaveChangesAsync();

        var check = await Service(db).EvaluateAsync(ContractId);

        Assert.Contains("(20 تن)", Assert.Single(check!.Blockers, b => b.Kind == ContractClosureBlockerKind.Delivery).Message);
        Assert.Contains("(10 تن)", Assert.Single(check.Blockers, b => b.Kind == ContractClosureBlockerKind.Transport).Message);
    }

    [Fact]
    public async Task StagedDelivery_PartiallyDeliveredOrder_BlocksUntilFullyDelivered()
    {
        await using var db = await SeededDbAsync();
        var order = new PreSaleOrder { OrderNumber = "PS-1", CustomerId = 1, ProductId = 1, OrderDate = new DateTime(2026, 2, 1), QuantityMt = 100m, Status = PreSaleOrderStatus.PartiallyDelivered };
        db.PreSaleOrders.Add(order);
        await db.SaveChangesAsync();
        db.SalesTransactions.Add(new SalesTransaction { ContractId = ContractId, PreSaleOrderId = order.Id, CustomerId = 1, ProductId = 1, InvoiceNumber = "PS-1-1", SaleDate = new DateTime(2026, 2, 2), QuantityMt = 40m });
        await db.SaveChangesAsync();

        var open = await Service(db).EvaluateAsync(ContractId);
        Assert.Contains("60 تن", Assert.Single(open!.Blockers, b => b.Kind == ContractClosureBlockerKind.StagedDelivery).Message);

        db.SalesTransactions.Add(new SalesTransaction { ContractId = ContractId, PreSaleOrderId = order.Id, CustomerId = 1, ProductId = 1, InvoiceNumber = "PS-1-2", SaleDate = new DateTime(2026, 2, 3), QuantityMt = 60m });
        order.Status = PreSaleOrderStatus.FullyDelivered;
        await db.SaveChangesAsync();

        var resolved = await Service(db).EvaluateAsync(ContractId);
        Assert.DoesNotContain(resolved!.Blockers, b => b.Kind == ContractClosureBlockerKind.StagedDelivery);
    }

    // ------------------------------------------------------------------
    // ۱-ج — فروش مستقیم از محموله (کشتی) بدون رسید
    // ------------------------------------------------------------------

    private const int ShipmentId = 50;

    private static async Task<InventoryTransportLeg> AddVesselLegAsync(ApplicationDbContext db, decimal quantityMt, int contractId = ContractId)
    {
        var leg = new InventoryTransportLeg
        {
            ShipmentId = ShipmentId,
            SourcePurchaseContractId = contractId,
            ProductId = 1,
            QuantityMt = quantityMt,
            TransportType = LoadingTransportType.Vessel,
            Status = InventoryTransportLegStatus.InTransit,
            LoadedDate = new DateTime(2026, 2, 2)
        };
        db.InventoryTransportLegs.Add(leg);
        await db.SaveChangesAsync();
        return leg;
    }

    private static SalesTransaction ShipmentSale(
        string invoice,
        decimal quantityMt,
        int? sourceContractId = ContractId,
        SaleStage stage = SaleStage.InTransit,
        bool cancelled = false) => new()
        {
            ShipmentId = ShipmentId,
            SourcePurchaseContractId = sourceContractId,
            SaleStage = stage,
            CustomerId = 1,
            ProductId = 1,
            InvoiceNumber = invoice,
            SaleDate = new DateTime(2026, 2, 4),
            QuantityMt = quantityMt,
            IsCancelled = cancelled
        };

    [Fact]
    public async Task VesselSale_WithExplicitSource_ResolvesTheOpenVesselLeg()
    {
        await using var db = await SeededDbAsync();
        await AddVesselLegAsync(db, 100m);
        db.SalesTransactions.AddRange(ShipmentSale("V-1", 60m), ShipmentSale("V-2", 40m));
        await db.SaveChangesAsync();

        var check = await Service(db).EvaluateAsync(ContractId);

        Assert.True(check!.CanClose, Describe(check));
    }

    [Fact]
    public async Task VesselSale_Partial_LeavesOnlyTheUnsoldRemainder()
    {
        await using var db = await SeededDbAsync();
        await AddVesselLegAsync(db, 100m);
        db.SalesTransactions.Add(ShipmentSale("V-1", 30m));
        await db.SaveChangesAsync();

        var check = await Service(db).EvaluateAsync(ContractId);

        Assert.Contains("(70 تن)", Assert.Single(check!.Blockers, b => b.Kind == ContractClosureBlockerKind.Transport).Message);
    }

    [Fact]
    public async Task VesselSale_OverSold_NeverResolvesMoreThanTheLegHolds()
    {
        await using var db = await SeededDbAsync();
        await AddVesselLegAsync(db, 50m);
        await AddVesselLegAsync(db, 50m);
        db.SalesTransactions.Add(ShipmentSale("V-1", 70m));
        await db.SaveChangesAsync();

        var check = await Service(db).EvaluateAsync(ContractId);

        var transport = Assert.Single(check!.Blockers, b => b.Kind == ContractClosureBlockerKind.Transport);
        Assert.Contains("1 حمل موجودی", transport.Message);
        Assert.Contains("(30 تن)", transport.Message);
    }

    [Fact]
    public async Task VesselSale_ReceiptLinkedDirectSale_IsNotCountedTwice()
    {
        await using var db = await SeededDbAsync();
        var leg = await AddVesselLegAsync(db, 100m);
        var receiptSale = ShipmentSale("V-R", 40m);
        db.SalesTransactions.Add(receiptSale);
        await db.SaveChangesAsync();
        var receipt = LegReceipt(leg.Id, 40m, InventoryTransportReceiptDestination.DirectSale);
        receipt.SalesTransactionId = receiptSale.Id;
        db.InventoryTransportReceipts.Add(receipt);
        await db.SaveChangesAsync();

        var check = await Service(db).EvaluateAsync(ContractId);

        // رسید ۴۰ تن را مصرف کرده؛ فروشِ همان رسید نباید ۴۰ تنِ دیگر را هم حل کند.
        Assert.Contains("(60 تن)", Assert.Single(check!.Blockers, b => b.Kind == ContractClosureBlockerKind.Transport).Message);
    }

    [Theory]
    [InlineData(null, SaleStage.InTransit, false)]
    [InlineData(OtherContractId, SaleStage.InTransit, false)]
    [InlineData(ContractId, SaleStage.PreSale, false)]
    [InlineData(ContractId, SaleStage.TerminalStock, false)]
    [InlineData(ContractId, SaleStage.InTransit, true)]
    public async Task VesselSale_WithoutExplicitLiveLink_DoesNotResolve(int? sourceContractId, SaleStage stage, bool cancelled)
    {
        await using var db = await SeededDbAsync();
        await AddVesselLegAsync(db, 100m);
        db.SalesTransactions.Add(ShipmentSale("V-X", 100m, sourceContractId, stage, cancelled));
        await db.SaveChangesAsync();

        var check = await Service(db).EvaluateAsync(ContractId);

        Assert.Contains("(100 تن)", Assert.Single(check!.Blockers, b => b.Kind == ContractClosureBlockerKind.Transport).Message);
    }

    // ------------------------------------------------------------------
    // ۲ — بستن
    // ------------------------------------------------------------------

    [Fact]
    public async Task Close_WhenNothingOpen_ClosesAndWritesAudit()
    {
        await using var db = await SeededDbAsync();

        await CloseCleanContractAsync(db);

        var contract = await db.Contracts.AsNoTracking().SingleAsync(c => c.Id == ContractId);
        Assert.Equal(ContractStatus.Closed, contract.Status);
        var log = await db.AuditLogs.AsNoTracking().SingleAsync(a => a.EntityName == nameof(Contract) && a.EntityId == ContractId);
        Assert.Equal(ContractClosureService.CloseAuditAction, log.Action);
        Assert.Equal(7, log.ActorUserId);
        Assert.Contains("تسویه کامل", log.Description);
        Assert.Contains("Status", log.Diff);
    }

    [Fact]
    public async Task Close_WithOpenItems_IsRefused_AndNothingChanges()
    {
        await using var db = await SeededDbAsync();
        db.TruckDispatches.Add(new TruckDispatch { ContractId = ContractId, LoadedQuantityMt = 25m, Status = DispatchStatus.InTransit, DispatchDate = new DateTime(2026, 2, 2) });
        await db.SaveChangesAsync();

        var result = await Service(db).CloseAsync(ContractId, null, null);

        Assert.False(result.Succeeded);
        Assert.NotNull(result.Check);
        Assert.NotEmpty(result.Check!.Blockers);
        Assert.Equal(ContractStatus.Active, (await db.Contracts.AsNoTracking().SingleAsync(c => c.Id == ContractId)).Status);
        Assert.False(await db.AuditLogs.AnyAsync());
    }

    [Fact]
    public async Task Close_AlreadyClosed_IsRefused()
    {
        await using var db = await SeededDbAsync();
        await CloseCleanContractAsync(db);

        var result = await Service(db).CloseAsync(ContractId, null, null);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Check!.Blockers, b => b.Kind == ContractClosureBlockerKind.Status);
    }

    // ------------------------------------------------------------------
    // ۳ — قفل پس از بستن (پشتوانهٔ SaveChanges)
    // ------------------------------------------------------------------

    [Fact]
    public async Task ClosedContract_BlocksNewOperations()
    {
        await using var db = await SeededDbAsync();
        await CloseCleanContractAsync(db);

        db.ExpenseTransactions.Add(Expense(ContractId));
        var error = await Assert.ThrowsAsync<ContractClosedException>(() => db.SaveChangesAsync());
        Assert.Equal(ContractId, error.ContractId);
        Assert.Contains("بسته شده است", error.Message);
        db.ChangeTracker.Clear();

        db.PaymentTransactions.Add(new PaymentTransaction { ContractId = ContractId, PaymentDate = new DateTime(2026, 2, 1), Amount = 10m, AmountUsd = 10m });
        await Assert.ThrowsAsync<ContractClosedException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();

        db.LoadingRegisters.Add(new LoadingRegister { ContractId = ContractId, ProductId = 1, LoadedQuantityMt = 5m, LoadingDate = new DateTime(2026, 2, 1) });
        await Assert.ThrowsAsync<ContractClosedException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();

        // فروش روی قرارداد فعالِ دیگر اما با منبعِ خرید از قرارداد بسته هم قفل است.
        db.SalesTransactions.Add(new SalesTransaction { ContractId = OtherContractId, SourcePurchaseContractId = ContractId, CustomerId = 1, ProductId = 1, InvoiceNumber = "INV-X", SaleDate = new DateTime(2026, 2, 1), QuantityMt = 1m });
        await Assert.ThrowsAsync<ContractClosedException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task ClosedContract_BlocksEditDeleteAndMovingExistingDocuments()
    {
        await using var db = await SeededDbAsync();
        var expense = Expense(ContractId);
        db.ExpenseTransactions.Add(expense);
        await db.SaveChangesAsync();
        // هزینه بدون Ledger است، پس کنترل مانده مانع بستن نمی‌شود.
        await CloseCleanContractAsync(db);

        expense.AmountUsd = 999m;
        await Assert.ThrowsAsync<ContractClosedException>(() => db.SaveChangesAsync());
        db.Entry(expense).Reload();

        db.ExpenseTransactions.Remove(expense);
        await Assert.ThrowsAsync<ContractClosedException>(() => db.SaveChangesAsync());
        db.Entry(expense).State = EntityState.Unchanged;

        expense.ContractId = OtherContractId;
        await Assert.ThrowsAsync<ContractClosedException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task ClosedContract_ItselfCannotBeEditedOrDeleted()
    {
        await using var db = await SeededDbAsync();
        await CloseCleanContractAsync(db);
        var contract = await db.Contracts.SingleAsync(c => c.Id == ContractId);

        contract.Notes = "ویرایش پس از بستن";
        await Assert.ThrowsAsync<ContractClosedException>(() => db.SaveChangesAsync());
        db.Entry(contract).Reload();

        contract.Status = ContractStatus.Active;
        await Assert.ThrowsAsync<ContractClosedException>(() => db.SaveChangesAsync());
        db.Entry(contract).Reload();

        db.Contracts.Remove(contract);
        await Assert.ThrowsAsync<ContractClosedException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task SettingClosedDirectly_WithoutTheCloseService_IsBlocked()
    {
        await using var db = await SeededDbAsync();
        var contract = await db.Contracts.SingleAsync(c => c.Id == ContractId);

        contract.Status = ContractStatus.Closed;

        var error = await Assert.ThrowsAsync<ContractClosedException>(() => db.SaveChangesAsync());
        Assert.Equal(ContractClosureScope.DirectStatusChangeMessage, error.Message);
    }

    [Fact]
    public async Task ClosedContract_TechnicalOnlyChange_IsAllowed()
    {
        await using var db = await SeededDbAsync();
        await CloseCleanContractAsync(db);
        var contract = await db.Contracts.SingleAsync(c => c.Id == ContractId);

        // شبیه backfill کلید جستجو: فقط SearchKey تغییر می‌کند.
        db.Entry(contract).Property(c => c.SearchKey).IsModified = true;
        await db.SaveChangesAsync();

        Assert.Equal(ContractStatus.Closed, (await db.Contracts.AsNoTracking().SingleAsync(c => c.Id == ContractId)).Status);
    }

    [Fact]
    public async Task ClosingOneContract_DoesNotAffectOtherContracts()
    {
        await using var db = await SeededDbAsync();
        await CloseCleanContractAsync(db);

        db.ExpenseTransactions.Add(Expense(OtherContractId));
        db.ExpenseTransactions.Add(new ExpenseTransaction { ExpenseTypeId = 1, ExpenseDate = new DateTime(2026, 2, 1), AmountUsd = 5m });
        await db.SaveChangesAsync();

        Assert.Equal(2, await db.ExpenseTransactions.CountAsync());
    }

    // ------------------------------------------------------------------
    // ۴ — بازگشایی
    // ------------------------------------------------------------------

    [Fact]
    public async Task Reopen_RequiresReason()
    {
        await using var db = await SeededDbAsync();
        await CloseCleanContractAsync(db);

        var result = await Service(db).ReopenAsync(ContractId, "   ", 1);

        Assert.False(result.Succeeded);
        Assert.Contains("دلیل", result.Error);
        Assert.Equal(ContractStatus.Closed, (await db.Contracts.AsNoTracking().SingleAsync(c => c.Id == ContractId)).Status);
    }

    [Fact]
    public async Task Reopen_OnlyForClosedContract()
    {
        await using var db = await SeededDbAsync();

        var result = await Service(db).ReopenAsync(ContractId, "دلیل", 1);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Reopen_ActivatesContract_LogsAudit_AndUnlocksOperations()
    {
        await using var db = await SeededDbAsync();
        await CloseCleanContractAsync(db);

        var result = await Service(db).ReopenAsync(ContractId, "اصلاح سند جاافتاده", 1);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(ContractStatus.Active, (await db.Contracts.AsNoTracking().SingleAsync(c => c.Id == ContractId)).Status);
        var reopenLog = await db.AuditLogs.AsNoTracking().SingleAsync(a => a.Action == ContractClosureService.ReopenAuditAction);
        Assert.Equal(1, reopenLog.ActorUserId);
        Assert.Contains("اصلاح سند جاافتاده", reopenLog.Description);

        db.ExpenseTransactions.Add(Expense(ContractId));
        await db.SaveChangesAsync();
        Assert.Equal(1, await db.ExpenseTransactions.CountAsync());
    }

    // ------------------------------------------------------------------
    // ۵ — کنترلر، دسترسی و ترجمهٔ خطا
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(nameof(ContractsController.Close), AuthPolicies.ManageData)]
    [InlineData(nameof(ContractsController.Reopen), AuthPolicies.AdminOnly)]
    public void ClosureActions_RequireTheirPolicy(string actionName, string policy)
    {
        var actions = typeof(ContractsController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.Name == actionName)
            .ToList();

        Assert.Equal(2, actions.Count);
        Assert.All(actions, action => Assert.Contains(
            action.GetCustomAttributes<AuthorizeAttribute>(),
            attribute => attribute.Policy == policy));
    }

    [Fact]
    public async Task CloseGet_ShowsOpenItems()
    {
        await using var db = await SeededDbAsync();
        db.TruckDispatches.Add(new TruckDispatch { ContractId = ContractId, LoadedQuantityMt = 25m, Status = DispatchStatus.Loaded, DispatchDate = new DateTime(2026, 2, 2) });
        await db.SaveChangesAsync();

        var result = await Controller(db).Close(ContractId);

        var view = Assert.IsType<ViewResult>(result);
        Assert.Equal("Closure", view.ViewName);
        var model = Assert.IsType<ContractClosureViewModel>(view.Model);
        Assert.False(model.CanSubmit);
        Assert.Contains(model.Blockers, b => b.Kind == ContractClosureBlockerKind.Transport);
    }

    [Fact]
    public async Task ClosePost_WithOpenItems_RedirectsBackWithError()
    {
        await using var db = await SeededDbAsync();
        db.TruckDispatches.Add(new TruckDispatch { ContractId = ContractId, LoadedQuantityMt = 25m, Status = DispatchStatus.Loaded, DispatchDate = new DateTime(2026, 2, 2) });
        await db.SaveChangesAsync();
        var controller = Controller(db);

        var result = await controller.Close(ContractId, reason: null);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(ContractsController.Close), redirect.ActionName);
        Assert.NotNull(controller.TempData["err"]);
    }

    [Fact]
    public async Task ClosePost_ThenReopenPost_RoundTripsWithHistory()
    {
        await using var db = await SeededDbAsync();
        var controller = Controller(db);

        var closed = await controller.Close(ContractId, reason: "پایان قرارداد");
        Assert.Equal(nameof(ContractsController.Details), Assert.IsType<RedirectToActionResult>(closed).ActionName);

        var reopenPage = Assert.IsType<ViewResult>(await controller.Reopen(ContractId));
        var reopenModel = Assert.IsType<ContractClosureViewModel>(reopenPage.Model);
        Assert.True(reopenModel.IsReopen);
        Assert.True(reopenModel.CanSubmit);
        Assert.Single(reopenModel.History);

        var reopened = await controller.Reopen(ContractId, reason: "ثبت هزینه جاافتاده");
        Assert.Equal(nameof(ContractsController.Details), Assert.IsType<RedirectToActionResult>(reopened).ActionName);
        Assert.Equal(ContractStatus.Active, (await db.Contracts.AsNoTracking().SingleAsync(c => c.Id == ContractId)).Status);
        Assert.Equal(2, await db.AuditLogs.CountAsync());
    }

    [Fact]
    public async Task EditAndPricing_OnClosedContract_RedirectToDetails()
    {
        await using var db = await SeededDbAsync();
        await CloseCleanContractAsync(db);
        var controller = Controller(db);

        var edit = Assert.IsType<RedirectToActionResult>(await controller.Edit(ContractId));
        Assert.Equal(nameof(ContractsController.Details), edit.ActionName);
        var pricing = Assert.IsType<RedirectToActionResult>(await controller.EditPricing(ContractId));
        Assert.Equal(nameof(ContractsController.Details), pricing.ActionName);
        Assert.NotNull(controller.TempData["err"]);
    }

    [Fact]
    public void ExceptionFilter_TranslatesClosedContractToUserMessage()
    {
        var exception = new ContractClosedException(ContractClosureScope.BuildLockedMessage("PUR-001"), ContractId);

        Assert.Equal(exception.Message, BusinessRuleExceptionFilter.Translate(exception));
    }

    // ------------------------------------------------------------------
    // ۶ — نمای «فقط مشاهده» و برچسب وضعیت
    // ------------------------------------------------------------------

    private static string ReadWebFile(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "src", "PTGOilSystem.Web", "PTGOilSystem.Web.csproj")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        var text = File.ReadAllText(Path.Combine(dir!.FullName, "src", "PTGOilSystem.Web", relativePath));
        // بعضی Viewها متن فارسی را به‌صورت \uXXXX در رشتهٔ C# نگه می‌دارند؛ خروجیِ نمایشی همان متن فارسی است.
        return System.Text.RegularExpressions.Regex.Replace(
            text,
            @"\\u([0-9a-fA-F]{4})",
            m => ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString());
    }

    [Theory]
    [InlineData("Views/ContractJourney/_ContractJourneyLoadingsTab.cshtml", "asp-controller=\"Loading\" asp-action=\"Create\"")]
    [InlineData("Views/ContractJourney/_ContractJourneyReceiptsTab.cshtml", "asp-controller=\"LoadingReceipts\" asp-action=\"Edit\"")]
    [InlineData("Views/ContractJourney/_ContractJourneySalesTab.cshtml", "asp-controller=\"Sales\" asp-action=\"Create\"")]
    [InlineData("Views/ContractJourney/_ContractJourneyFinanceTab.cshtml", "asp-controller=\"Payments\" asp-action=\"Create\"")]
    [InlineData("Views/ContractJourney/_ContractJourneyDispatchTab.cshtml", "asp-controller=\"Dispatch\" asp-action=\"Create\"")]
    [InlineData("Views/ContractJourney/Details.cshtml", "asp-controller=\"Expenses\" asp-action=\"Create\"")]
    [InlineData("Views/ContractJourney/Details.cshtml", "asp-controller=\"InventoryTransportLegs\" asp-action=\"Create\"")]
    public void JourneyTabs_GateOperationButtons_OnClosedContract(string relativePath, string operationMarkup)
    {
        var text = ReadWebFile(relativePath);

        Assert.Contains("ViewData[\"ContractIsClosed\"] is true", text);
        // هر دکمهٔ عملیات بعد از یک شرط !contractIsClosed می‌آید.
        var operationIndex = text.IndexOf(operationMarkup, StringComparison.Ordinal);
        Assert.True(operationIndex > 0, $"'{operationMarkup}' not found in {relativePath}");
        var guardIndex = text.LastIndexOf("!contractIsClosed", operationIndex, StringComparison.Ordinal);
        Assert.True(guardIndex > 0 && operationIndex - guardIndex < 600, $"'{operationMarkup}' in {relativePath} is not gated by !contractIsClosed");
    }

    [Theory]
    [InlineData("Views/Contracts/Index.cshtml")]
    [InlineData("Views/Reports/ContractPnl.cshtml")]
    public void ClosedStatus_IsLabelledClosed_Everywhere(string relativePath)
    {
        var text = ReadWebFile(relativePath);

        Assert.Contains("ContractStatus.Closed => T(\"بسته‌شده\", \"Closed\")", text);
        Assert.DoesNotContain("In progress", text);
    }

    private sealed class NullTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();

        public void SaveTempData(HttpContext context, IDictionary<string, object> values)
        {
        }
    }
}
