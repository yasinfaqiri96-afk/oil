using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PTGOilSystem.Web.Controllers;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.Sales;
using PTGOilSystem.Web.Services;
using Xunit;

namespace PTGOilSystem.Web.Tests;

// پیش‌فروش = تعهد. این تست‌ها قرارداد رفتاری را قفل می‌کنند:
// ثبت تعهد هیچ اثر موجودی/مالی ندارد و فقط تحویل واقعی موجودی، درآمد و لجر می‌سازد.
public class PreSaleOrderTests
{
    [Fact]
    public async Task PreSale_Index_Separates_Active_Commitment_From_Closed_And_Cancelled()
    {
        await using var db = NewDb();
        SeedReferenceData(db);
        db.PreSaleOrders.AddRange(
            new PreSaleOrder
            {
                Id = 1, OrderNumber = "ACTIVE", CustomerId = 1, ProductId = 1,
                OrderDate = new DateTime(2026, 5, 1), QuantityMt = 100m,
                Currency = "USD", Status = PreSaleOrderStatus.Confirmed
            },
            new PreSaleOrder
            {
                Id = 2, OrderNumber = "CLOSED", CustomerId = 1, ProductId = 1,
                OrderDate = new DateTime(2026, 5, 1), QuantityMt = 50m,
                Currency = "USD", Status = PreSaleOrderStatus.Closed
            },
            new PreSaleOrder
            {
                Id = 3, OrderNumber = "CANCELLED", CustomerId = 1, ProductId = 1,
                OrderDate = new DateTime(2026, 5, 1), QuantityMt = 30m,
                Currency = "USD", Status = PreSaleOrderStatus.Cancelled
            });
        db.SalesTransactions.Add(new SalesTransaction
        {
            Id = 10, PreSaleOrderId = 1, CustomerId = 1, ProductId = 1,
            InvoiceNumber = "DEL-1", SaleDate = new DateTime(2026, 5, 10),
            QuantityMt = 20m, Currency = "USD"
        });
        await db.SaveChangesAsync();

        var result = Assert.IsType<ViewResult>(await BuildController(db).PreSales());
        var model = Assert.IsType<PreSaleIndexViewModel>(result.Model);

        Assert.Equal(3, model.TotalCount);
        Assert.Equal(1, model.ActiveOrderCount);
        Assert.Equal(1, model.ClosedOrderCount);
        Assert.Equal(1, model.CancelledOrderCount);
        Assert.Equal(100m, model.SumQuantityMt);
        Assert.Equal(80m, model.SumRemainingMt);
        Assert.Equal(0m, model.Items.Single(x => x.Id == 2).RemainingMt);
        Assert.Equal(0m, model.Items.Single(x => x.Id == 3).RemainingMt);
    }

    [Fact]
    public async Task PreSaleCreate_Does_Not_Touch_Inventory_Or_Ledger()
    {
        await using var db = NewDb();
        SeedReferenceData(db);
        await db.SaveChangesAsync();

        var controller = BuildController(db);

        var result = await controller.PreSaleCreate(NewOrderModel(quantityMt: 100m));

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(SalesController.PreSaleDetails), redirect.ActionName);

        var order = await db.PreSaleOrders.SingleAsync();
        Assert.Equal(PreSaleOrderStatus.Confirmed, order.Status);
        Assert.Equal(50_000m, order.TotalInCurrency);
        Assert.Equal("PRE-" + order.Id, order.OrderNumber);

        Assert.Empty(db.InventoryMovements);
        Assert.Empty(db.LedgerEntries);
        Assert.Empty(db.SalesTransactions);
        Assert.Empty(db.JournalEntries);
    }

    [Fact]
    public async Task PreSaleCreate_Succeeds_When_No_Stock_Exists_Yet()
    {
        await using var db = NewDb();
        SeedReferenceData(db);
        await db.SaveChangesAsync();

        var controller = BuildController(db);

        Assert.IsType<RedirectToActionResult>(await controller.PreSaleCreate(NewOrderModel(quantityMt: 100m)));
        Assert.Equal(1, await db.PreSaleOrders.CountAsync());
        Assert.Empty(db.InventoryMovements);
    }

    [Fact]
    public async Task Delivery_From_Tank_Posts_Only_Delivered_Portion_And_Sets_PartiallyDelivered()
    {
        await using var db = NewDb();
        SeedReferenceData(db);
        SeedPurchaseContract(db, 2);
        SeedTankStock(db, contractId: 2, quantityMt: 100m);
        await db.SaveChangesAsync();

        var controller = BuildController(db);
        await controller.PreSaleCreate(NewOrderModel(quantityMt: 100m));
        var order = await db.PreSaleOrders.SingleAsync();

        await controller.PreSaleDeliver(NewDeliveryModel(order.Id, quantityMt: 20m));

        var sale = await db.SalesTransactions.SingleAsync();
        Assert.Equal(order.Id, sale.PreSaleOrderId);
        Assert.Equal(20m, sale.QuantityMt);
        Assert.Equal(10_000m, sale.TotalInCurrency);   // ۲۰ تن × ۵۰۰ = فقط سهم همین تحویل
        Assert.Equal(SaleStage.TerminalStock, sale.SaleStage);

        var movement = await db.InventoryMovements.SingleAsync(m => m.Direction == MovementDirection.Out);
        Assert.Equal(20m, movement.QuantityMt);

        var ledger = await db.LedgerEntries.SingleAsync();
        Assert.Equal("Sale", ledger.SourceType);
        Assert.Equal(10_000m, ledger.AmountUsd);

        await db.Entry(order).ReloadAsync();
        Assert.Equal(PreSaleOrderStatus.PartiallyDelivered, order.Status);
    }

    [Fact]
    public async Task Multiple_Deliveries_Complete_The_Order()
    {
        await using var db = NewDb();
        SeedReferenceData(db);
        SeedPurchaseContract(db, 2);
        SeedTankStock(db, contractId: 2, quantityMt: 100m);
        await db.SaveChangesAsync();

        var controller = BuildController(db);
        await controller.PreSaleCreate(NewOrderModel(quantityMt: 50m));
        var order = await db.PreSaleOrders.SingleAsync();

        await controller.PreSaleDeliver(NewDeliveryModel(order.Id, quantityMt: 20m));
        await controller.PreSaleDeliver(NewDeliveryModel(order.Id, quantityMt: 30m));

        Assert.Equal(2, await db.SalesTransactions.CountAsync(s => s.PreSaleOrderId == order.Id));
        await db.Entry(order).ReloadAsync();
        Assert.Equal(PreSaleOrderStatus.FullyDelivered, order.Status);
    }

    [Fact]
    public async Task Delivery_Beyond_Remaining_Is_Rejected()
    {
        await using var db = NewDb();
        SeedReferenceData(db);
        SeedPurchaseContract(db, 2);
        SeedTankStock(db, contractId: 2, quantityMt: 100m);
        await db.SaveChangesAsync();

        var controller = BuildController(db);
        await controller.PreSaleCreate(NewOrderModel(quantityMt: 30m));
        var order = await db.PreSaleOrders.SingleAsync();

        await controller.PreSaleDeliver(NewDeliveryModel(order.Id, quantityMt: 40m));

        Assert.Empty(db.SalesTransactions);
        Assert.Empty(db.InventoryMovements.Where(m => m.Direction == MovementDirection.Out));
        Assert.Contains("مانده", controller.TempData["err"]?.ToString() ?? "");
    }

    [Fact]
    public async Task Delivery_Beyond_Available_Stock_Is_Rejected()
    {
        await using var db = NewDb();
        SeedReferenceData(db);
        SeedPurchaseContract(db, 2);
        SeedTankStock(db, contractId: 2, quantityMt: 5m);
        await db.SaveChangesAsync();

        var controller = BuildController(db);
        await controller.PreSaleCreate(NewOrderModel(quantityMt: 100m));
        var order = await db.PreSaleOrders.SingleAsync();

        await controller.PreSaleDeliver(NewDeliveryModel(order.Id, quantityMt: 20m));

        Assert.Empty(db.SalesTransactions);
        Assert.NotNull(controller.TempData["err"]);
    }

    [Fact]
    public async Task Delivery_From_Source_With_Other_Product_Is_Rejected()
    {
        await using var db = NewDb();
        SeedReferenceData(db);
        db.Products.Add(new Product { Id = 2, Code = "MG", Name = "Mogas" });
        SeedPurchaseContract(db, 2, productId: 2);
        SeedTankStock(db, contractId: 2, quantityMt: 100m, productId: 2);
        await db.SaveChangesAsync();

        var controller = BuildController(db);
        await controller.PreSaleCreate(NewOrderModel(quantityMt: 100m));   // کالای ۱
        var order = await db.PreSaleOrders.SingleAsync();

        var delivery = NewDeliveryModel(order.Id, quantityMt: 10m);
        delivery.Kind = GroupSaleSourceKind.TruckDispatch;
        delivery.Id = 999;
        await controller.PreSaleDeliver(delivery);

        Assert.Empty(db.SalesTransactions);
        Assert.NotNull(controller.TempData["err"]);
    }

    [Fact]
    public async Task Cancelled_Delivery_Is_Excluded_And_Reverses_Stock_And_Ledger()
    {
        await using var db = NewDb();
        SeedReferenceData(db);
        SeedPurchaseContract(db, 2);
        SeedTankStock(db, contractId: 2, quantityMt: 100m);
        await db.SaveChangesAsync();

        var controller = BuildController(db);
        await controller.PreSaleCreate(NewOrderModel(quantityMt: 100m));
        var order = await db.PreSaleOrders.SingleAsync();
        await controller.PreSaleDeliver(NewDeliveryModel(order.Id, quantityMt: 20m));
        var sale = await db.SalesTransactions.SingleAsync();

        await controller.PreSaleDeliveryCancel(order.Id, sale.Id);

        await db.Entry(sale).ReloadAsync();
        Assert.True(sale.IsCancelled);
        Assert.Equal(2, await db.LedgerEntries.CountAsync());                       // فروش + برگشت
        Assert.Equal(1, await db.InventoryMovements.CountAsync(m => m.Direction == MovementDirection.In
            && m.SalesTransactionId == sale.Id));                                   // برگشت موجودی

        await db.Entry(order).ReloadAsync();
        Assert.Equal(PreSaleOrderStatus.Confirmed, order.Status);                   // تحویل لغوشده شمرده نمی‌شود
    }

    [Fact]
    public async Task Cancelled_PreSale_Rejects_New_Delivery()
    {
        await using var db = NewDb();
        SeedReferenceData(db);
        SeedPurchaseContract(db, 2);
        SeedTankStock(db, contractId: 2, quantityMt: 100m);
        await db.SaveChangesAsync();

        var controller = BuildController(db);
        await controller.PreSaleCreate(NewOrderModel(quantityMt: 100m));
        var order = await db.PreSaleOrders.SingleAsync();

        await controller.PreSaleCancel(order.Id, "تست");
        await controller.PreSaleDeliver(NewDeliveryModel(order.Id, quantityMt: 10m));

        Assert.Empty(db.SalesTransactions);
        await db.Entry(order).ReloadAsync();
        Assert.Equal(PreSaleOrderStatus.Cancelled, order.Status);
    }

    [Fact]
    public async Task PreSale_With_Active_Delivery_Cannot_Be_Cancelled()
    {
        await using var db = NewDb();
        SeedReferenceData(db);
        SeedPurchaseContract(db, 2);
        SeedTankStock(db, contractId: 2, quantityMt: 100m);
        await db.SaveChangesAsync();

        var controller = BuildController(db);
        await controller.PreSaleCreate(NewOrderModel(quantityMt: 100m));
        var order = await db.PreSaleOrders.SingleAsync();
        await controller.PreSaleDeliver(NewDeliveryModel(order.Id, quantityMt: 10m));

        await controller.PreSaleCancel(order.Id, "تست");

        await db.Entry(order).ReloadAsync();
        Assert.NotEqual(PreSaleOrderStatus.Cancelled, order.Status);
    }

    [Fact]
    public async Task Customer_Advance_Can_Be_Allocated_And_Deallocated_Without_Touching_The_Payment()
    {
        await using var db = NewDb();
        SeedReferenceData(db);
        await db.SaveChangesAsync();

        var controller = BuildController(db);
        await controller.PreSaleCreate(NewOrderModel(quantityMt: 100m));
        var order = await db.PreSaleOrders.SingleAsync();

        db.CashAccounts.Add(new CashAccount { Id = 1, Name = "Main", Currency = "USD" });
        db.PaymentTransactions.Add(new PaymentTransaction
        {
            Id = 1,
            PaymentDate = new DateTime(2026, 5, 1),
            Direction = PaymentDirection.In,
            PaymentKind = PaymentKind.CustomerReceipt,
            CashAccountId = 1,
            CustomerId = 1,
            Amount = 5_000m,
            Currency = "USD",
            AppliedFxRateToUsd = 1m,
            AmountUsd = 5_000m,
            IsCustomerAdvance = true
        });
        await db.SaveChangesAsync();

        await controller.PreSaleAllocatePayment(order.Id, paymentTransactionId: 1, amount: 3_000m);

        var allocation = await db.CustomerPaymentAllocations.SingleAsync();
        Assert.Equal(order.Id, allocation.PreSaleOrderId);
        Assert.Equal(3_000m, allocation.AllocatedPaymentAmount);   // تخصیص جزئی

        var payment = await db.PaymentTransactions.SingleAsync();
        Assert.Equal(5_000m, payment.Amount);
        Assert.Equal(1m, payment.AppliedFxRateToUsd);   // نرخ روز دریافت دست‌نخورده می‌ماند
        Assert.Empty(db.LedgerEntries);                 // تخصیص هیچ سند مالی جدیدی نمی‌سازد

        await controller.PreSaleReverseAllocation(order.Id, allocation.Id, "تست");
        await db.Entry(allocation).ReloadAsync();
        Assert.Equal(CustomerPaymentAllocationStatus.Reversed, allocation.Status);
    }

    [Fact]
    public async Task Payment_Of_Another_Customer_Cannot_Be_Allocated()
    {
        await using var db = NewDb();
        SeedReferenceData(db);
        db.Customers.Add(new Customer { Id = 2, Name = "Other" });
        await db.SaveChangesAsync();

        var controller = BuildController(db);
        await controller.PreSaleCreate(NewOrderModel(quantityMt: 100m));
        var order = await db.PreSaleOrders.SingleAsync();

        db.CashAccounts.Add(new CashAccount { Id = 1, Name = "Main", Currency = "USD" });
        db.PaymentTransactions.Add(new PaymentTransaction
        {
            Id = 1,
            PaymentDate = new DateTime(2026, 5, 1),
            Direction = PaymentDirection.In,
            PaymentKind = PaymentKind.CustomerReceipt,
            CashAccountId = 1,
            CustomerId = 2,
            Amount = 5_000m,
            Currency = "USD",
            AppliedFxRateToUsd = 1m,
            AmountUsd = 5_000m
        });
        await db.SaveChangesAsync();

        await controller.PreSaleAllocatePayment(order.Id, paymentTransactionId: 1, amount: 1_000m);

        Assert.Empty(db.CustomerPaymentAllocations);
        Assert.NotNull(controller.TempData["err"]);
    }

    [Fact]
    public async Task Details_Summary_Counts_Only_Active_Deliveries_And_Allocated_Payments()
    {
        await using var db = NewDb();
        SeedReferenceData(db);
        SeedPurchaseContract(db, 2);
        SeedTankStock(db, contractId: 2, quantityMt: 100m);
        await db.SaveChangesAsync();

        var controller = BuildController(db);
        await controller.PreSaleCreate(NewOrderModel(quantityMt: 100m));
        var order = await db.PreSaleOrders.SingleAsync();
        await controller.PreSaleDeliver(NewDeliveryModel(order.Id, quantityMt: 20m));
        await controller.PreSaleDeliver(NewDeliveryModel(order.Id, quantityMt: 10m));
        var cancelled = await db.SalesTransactions.OrderBy(s => s.Id).LastAsync();
        await controller.PreSaleDeliveryCancel(order.Id, cancelled.Id);

        var view = Assert.IsType<ViewResult>(await controller.PreSaleDetails(order.Id));
        var model = Assert.IsType<PreSaleDetailsViewModel>(view.Model);

        Assert.Equal(20m, model.DeliveredMt);
        Assert.Equal(80m, model.RemainingMt);
        Assert.Equal(10_000m, model.DeliveredValueUsd);
        Assert.Equal(0m, model.ReceivedUsd);
        Assert.Equal(10_000m, model.OutstandingUsd);
    }

    // ---------- تحویل جزئی از حملِ کشتی (TransportLeg) ----------

    [Fact]
    public async Task Vessel_Delivery_Uses_Manual_Quantity_And_Keeps_Both_Remainders()
    {
        await using var db = NewDb();
        SeedReferenceData(db);
        SeedPurchaseContract(db, 2);
        SeedVesselLeg(db, legId: 10, contractId: 2, quantityMt: 500m);
        await db.SaveChangesAsync();

        var controller = BuildController(db);
        await controller.PreSaleCreate(NewOrderModel(quantityMt: 100m));
        var order = await db.PreSaleOrders.SingleAsync();

        await controller.PreSaleDeliver(NewLegDeliveryModel(order.Id, quantityMt: 20m, legId: 10));

        var sale = await db.SalesTransactions.SingleAsync();
        Assert.Equal(20m, sale.QuantityMt);                 // فقط مقدار واردشدهٔ کاربر، نه کل حمل
        Assert.Equal(10_000m, sale.TotalInCurrency);        // ۲۰ × ۵۰۰
        Assert.Equal(SaleStage.InTransit, sale.SaleStage);

        var view = Assert.IsType<ViewResult>(await controller.PreSaleDetails(order.Id));
        var model = Assert.IsType<PreSaleDetailsViewModel>(view.Model);
        Assert.Equal(80m, model.RemainingMt);               // مانده پیش‌فروش
        var leg = model.Sources.Single(s => s.Kind == GroupSaleSourceKind.TransportLeg && s.Id == 10);
        Assert.Equal(480m, leg.AvailableMt);                // مانده واقعی حمل
    }

    [Fact]
    public async Task Two_Consecutive_Vessel_Partial_Deliveries_Succeed()
    {
        await using var db = NewDb();
        SeedReferenceData(db);
        SeedPurchaseContract(db, 2);
        SeedVesselLeg(db, legId: 10, contractId: 2, quantityMt: 500m);
        await db.SaveChangesAsync();

        var controller = BuildController(db);
        await controller.PreSaleCreate(NewOrderModel(quantityMt: 100m));
        var order = await db.PreSaleOrders.SingleAsync();

        await controller.PreSaleDeliver(NewLegDeliveryModel(order.Id, quantityMt: 20m, legId: 10));
        await controller.PreSaleDeliver(NewLegDeliveryModel(order.Id, quantityMt: 30m, legId: 10));

        Assert.Equal(2, await db.SalesTransactions.CountAsync(s => s.PreSaleOrderId == order.Id));
        Assert.Equal(50m, await db.InventoryTransportReceipts.SumAsync(r => r.ReceivedQuantityMt));
        await db.Entry(order).ReloadAsync();
        Assert.Equal(PreSaleOrderStatus.PartiallyDelivered, order.Status);
    }

    [Fact]
    public async Task Vessel_Delivery_Beyond_PreSale_Remaining_Is_Rejected()
    {
        await using var db = NewDb();
        SeedReferenceData(db);
        SeedPurchaseContract(db, 2);
        SeedVesselLeg(db, legId: 10, contractId: 2, quantityMt: 500m);
        await db.SaveChangesAsync();

        var controller = BuildController(db);
        await controller.PreSaleCreate(NewOrderModel(quantityMt: 30m));
        var order = await db.PreSaleOrders.SingleAsync();

        await controller.PreSaleDeliver(NewLegDeliveryModel(order.Id, quantityMt: 40m, legId: 10));

        Assert.Empty(db.SalesTransactions);
        Assert.Contains("مانده", controller.TempData["err"]?.ToString() ?? "");
    }

    [Fact]
    public async Task Vessel_Delivery_Beyond_Leg_Available_Is_Rejected()
    {
        await using var db = NewDb();
        SeedReferenceData(db);
        SeedPurchaseContract(db, 2);
        SeedVesselLeg(db, legId: 10, contractId: 2, quantityMt: 15m);
        await db.SaveChangesAsync();

        var controller = BuildController(db);
        await controller.PreSaleCreate(NewOrderModel(quantityMt: 100m));
        var order = await db.PreSaleOrders.SingleAsync();

        await controller.PreSaleDeliver(NewLegDeliveryModel(order.Id, quantityMt: 20m, legId: 10));

        Assert.Empty(db.SalesTransactions);
        Assert.NotNull(controller.TempData["err"]);
    }

    [Fact]
    public async Task Vessel_Leg_Disappears_From_Sources_When_Fully_Consumed()
    {
        await using var db = NewDb();
        SeedReferenceData(db);
        SeedPurchaseContract(db, 2);
        SeedVesselLeg(db, legId: 10, contractId: 2, quantityMt: 50m);
        await db.SaveChangesAsync();

        var controller = BuildController(db);
        await controller.PreSaleCreate(NewOrderModel(quantityMt: 100m));
        var order = await db.PreSaleOrders.SingleAsync();

        await controller.PreSaleDeliver(NewLegDeliveryModel(order.Id, quantityMt: 30m, legId: 10));

        var midView = Assert.IsType<ViewResult>(await controller.PreSaleDetails(order.Id));
        var midModel = Assert.IsType<PreSaleDetailsViewModel>(midView.Model);
        Assert.Contains(midModel.Sources, s => s.Kind == GroupSaleSourceKind.TransportLeg && s.Id == 10);   // مانده ۲۰ → هنوز در فهرست

        await controller.PreSaleDeliver(NewLegDeliveryModel(order.Id, quantityMt: 20m, legId: 10));

        var finalView = Assert.IsType<ViewResult>(await controller.PreSaleDetails(order.Id));
        var finalModel = Assert.IsType<PreSaleDetailsViewModel>(finalView.Model);
        Assert.DoesNotContain(finalModel.Sources, s => s.Kind == GroupSaleSourceKind.TransportLeg && s.Id == 10);
        Assert.Equal(InventoryTransportLegStatus.Received, (await db.InventoryTransportLegs.SingleAsync()).Status);
    }

    [Fact]
    public async Task Vessel_Partial_Delivery_Posts_Only_Delivered_Portion_To_Receipt_Sale_And_Ledger()
    {
        // مبنای COGS و Lineage همان ReceivedQuantityMt/QuantityMt سند فروش است؛ اینجا قفل می‌شود که
        // فقط مقدار جزئی ثبت می‌شود، نه کل حمل. (اثبات ژورنالِ سطحِ COGS در سوئیت حسابداری PostgreSQL است.)
        await using var db = NewDb();
        SeedReferenceData(db);
        SeedPurchaseContract(db, 2);
        SeedVesselLeg(db, legId: 10, contractId: 2, quantityMt: 500m);
        await db.SaveChangesAsync();

        var controller = BuildController(db);
        await controller.PreSaleCreate(NewOrderModel(quantityMt: 100m));
        var order = await db.PreSaleOrders.SingleAsync();

        await controller.PreSaleDeliver(NewLegDeliveryModel(order.Id, quantityMt: 20m, legId: 10));

        var receipt = await db.InventoryTransportReceipts.SingleAsync();
        Assert.Equal(20m, receipt.ReceivedQuantityMt);
        Assert.Equal(20m, (await db.SalesTransactions.SingleAsync()).QuantityMt);

        var ledger = await db.LedgerEntries.SingleAsync();
        Assert.Equal("Sale", ledger.SourceType);
        Assert.Equal(10_000m, ledger.AmountUsd);            // فقط سهم مقدار جزئی
    }

    [Fact]
    public async Task Wagon_Leg_Presale_Delivery_Stays_Whole_And_Single_Sale()
    {
        // واگن نباید جزئی شود: مقدارِ ارسالی نادیده گرفته می‌شود، کل حمل یک‌بار فروخته می‌شود،
        // و تحویل دوم رد می‌شود — یعنی رفتار تاریخیِ کل‌وسیله دست‌نخورده مانده است.
        await using var db = NewDb();
        SeedReferenceData(db);
        SeedPurchaseContract(db, 2);
        SeedVesselLeg(db, legId: 20, contractId: 2, quantityMt: 40m,
            transportType: LoadingTransportType.Wagon);
        await db.SaveChangesAsync();

        var controller = BuildController(db);
        await controller.PreSaleCreate(NewOrderModel(quantityMt: 100m));
        var order = await db.PreSaleOrders.SingleAsync();

        await controller.PreSaleDeliver(
            NewLegDeliveryModel(order.Id, quantityMt: 10m, legId: 20, kind: GroupSaleSourceKind.WagonLeg));

        var sale = await db.SalesTransactions.SingleAsync();
        Assert.Equal(40m, sale.QuantityMt);                 // کل واگن، نه مقدار ۱۰ ارسالی

        await controller.PreSaleDeliver(
            NewLegDeliveryModel(order.Id, quantityMt: 10m, legId: 20, kind: GroupSaleSourceKind.WagonLeg));

        Assert.Equal(1, await db.SalesTransactions.CountAsync());   // تحویل دوم رد شد (واگن قبلاً فروخته)
        Assert.NotNull(controller.TempData["err"]);
    }

    // ---------- helpers ----------

    private static ApplicationDbContext NewDb()
        => new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static SalesController BuildController(ApplicationDbContext db)
        => new(db, new StockService(db), new AuditService(db), NullLogger<SalesController>.Instance)
        {
            TempData = new TempDataDictionary(new DefaultHttpContext(), new NullTempDataProvider())
        };

    private static PreSaleCreateViewModel NewOrderModel(decimal quantityMt) => new()
    {
        CustomerId = 1,
        ProductId = 1,
        QuantityMt = quantityMt,
        Currency = "USD",
        UnitPriceInCurrency = 500m,
        OrderDate = new DateTime(2026, 5, 1)
    };

    private static PreSaleDeliveryCreateViewModel NewDeliveryModel(int orderId, decimal quantityMt) => new()
    {
        PreSaleOrderId = orderId,
        DeliveryDate = new DateTime(2026, 5, 10),
        QuantityMt = quantityMt,
        Kind = GroupSaleSourceKind.TerminalStock,
        TerminalId = 1,
        StorageTankId = 1,
        SourcePurchaseContractId = 2
    };

    private static PreSaleDeliveryCreateViewModel NewLegDeliveryModel(
        int orderId, decimal quantityMt, int legId,
        GroupSaleSourceKind kind = GroupSaleSourceKind.TransportLeg) => new()
    {
        PreSaleOrderId = orderId,
        DeliveryDate = new DateTime(2026, 5, 10),
        QuantityMt = quantityMt,
        Kind = kind,
        Id = legId
    };

    private static void SeedVesselLeg(
        ApplicationDbContext db, int legId, int contractId, decimal quantityMt,
        int productId = 1, LoadingTransportType transportType = LoadingTransportType.Vessel)
        => db.InventoryTransportLegs.Add(new InventoryTransportLeg
        {
            Id = legId,
            SourcePurchaseContractId = contractId,
            ProductId = productId,
            SourceTerminalId = 1,
            TransportType = transportType,
            LoadedDate = new DateTime(2026, 4, 25),
            QuantityMt = quantityMt,
            Status = InventoryTransportLegStatus.Loaded,
            WagonNumber = $"VSL-{legId}"
        });

    private static void SeedReferenceData(ApplicationDbContext db)
    {
        db.Currencies.Add(new Currency { Id = 1, Code = "USD", Name = "US Dollar", Symbol = "$", IsActive = true });
        db.Companies.Add(new Company { Id = 1, Code = "PTG", Name = "PTG" });
        db.Customers.Add(new Customer { Id = 1, Name = "Herat Market" });
        db.Products.Add(new Product { Id = 1, Code = "GO", Name = "Gas Oil" });
        db.Terminals.Add(new Terminal { Id = 1, Code = "T1", Name = "Terminal 1" });
        db.StorageTanks.Add(new StorageTank { Id = 1, TerminalId = 1, TankCode = "TK-01", ProductId = 1, CapacityMt = 1000m });
    }

    private static void SeedPurchaseContract(ApplicationDbContext db, int contractId, int productId = 1)
    {
        db.Suppliers.Add(new Supplier { Id = contractId, Name = $"Supplier {contractId}" });
        db.Contracts.Add(new Contract
        {
            Id = contractId,
            ContractNumber = $"PUR-{contractId:000}",
            ContractType = ContractType.Purchase,
            CompanyId = 1,
            SupplierId = contractId,
            ProductId = productId,
            ContractDate = new DateTime(2026, 4, 20),
            QuantityMt = 1000m,
            PricingMethod = PricingMethod.Fixed,
            UnitPriceUsd = 430m
        });
    }

    private static void SeedTankStock(ApplicationDbContext db, int contractId, decimal quantityMt, int productId = 1)
        => db.InventoryMovements.Add(new InventoryMovement
        {
            ProductId = productId,
            ContractId = contractId,
            TerminalId = 1,
            StorageTankId = 1,
            Direction = MovementDirection.In,
            MovementDate = new DateTime(2026, 4, 25),
            QuantityMt = quantityMt,
            ReferenceDocument = "GRN-PRE-1"
        });

    private sealed class NullTempDataProvider : ITempDataProvider
    {
        private IDictionary<string, object> _data = new Dictionary<string, object>();

        public IDictionary<string, object> LoadTempData(HttpContext context) => _data;

        public void SaveTempData(HttpContext context, IDictionary<string, object> values)
            => _data = new Dictionary<string, object>(values);
    }
}
