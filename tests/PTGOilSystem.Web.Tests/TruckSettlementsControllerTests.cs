using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using PTGOilSystem.Web.Controllers;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.Reports;
using PTGOilSystem.Web.Models.TruckSettlements;
using PTGOilSystem.Web.Services;
using Xunit;

namespace PTGOilSystem.Web.Tests;

// «تسویهٔ کرایه موترها»: فقط تسویهٔ کرایه است — تخلیهٔ موجودی/فروش ندارد.
// این تست‌ها تضمین می‌کنند: (۱) leg با SettlementOnly بدون InventoryMovement تسویه می‌شود؛
// (۲) dispatch بدون DeliveryReceipt/حرکت موجودی/Delivered تسویه می‌شود؛ (۳) ردیف تسویه‌شده از لیست خارج می‌شود.
public class TruckSettlementsControllerTests
{
    [Fact]
    public async Task Settle_Leg_Books_Freight_Without_InventoryMovement_And_Marks_Settled()
    {
        await using var db = CreateDb();
        await SeedReferenceDataAsync(db);
        var leg = await SeedLoadedTruckLegAsync(db, quantityMt: 30m);
        var controller = BuildController(db);

        var result = await controller.Settle(new TruckSettlementIndexViewModel
        {
            Inputs =
            [
                new TruckSettlementRowInputViewModel
                {
                    Selected = true,
                    Kind = TruckSettlementSourceKind.Leg,
                    SourceId = leg.Id,
                    OperationDate = new DateTime(2026, 5, 5),
                    QuantityMt = 28m,               // وزن تخلیه ⇒ کسری = 30 − 28 = 2
                    FreightRateUsdPerMt = 5m,       // کرایه کلی = 5 × 30 = 150
                    ShortageRateUsd = 10m,          // خسارت کسری = 2 × 10 = 20 ⇒ کرایه نهایی = 130
                    FreightParty = "driver:1"
                }
            ]
        });

        Assert.IsType<RedirectToActionResult>(result);
        Assert.True(controller.ModelState.IsValid);

        var reloaded = await db.InventoryTransportLegs.SingleAsync(l => l.Id == leg.Id);
        Assert.True(reloaded.IsFreightSettled);
        Assert.Equal(new DateTime(2026, 5, 5), reloaded.FreightSettledDate);
        // حمل هنوز تخلیه نشده — بار برای مرحلهٔ بعدی می‌ماند (کسری از باقیمانده کم شده).
        Assert.Equal(InventoryTransportLegStatus.Loaded, reloaded.Status);

        var receipt = await db.InventoryTransportReceipts.SingleAsync();
        Assert.Equal(0m, receipt.ReceivedQuantityMt);
        Assert.Equal(2m, receipt.ShortageQuantityMt);
        Assert.Null(receipt.InventoryMovementId);
        Assert.DoesNotContain(
            await db.InventoryMovements.ToListAsync(),
            m => m.ReferenceDocument != null && m.ReferenceDocument.StartsWith("TRANSPORT-RECEIPT:"));

        var expense = await db.ExpenseTransactions.Include(e => e.ExpenseType).SingleAsync();
        Assert.Equal("TRANSPORT-RECEIPT-FREIGHT", expense.ExpenseType?.Code);
        Assert.Equal(1, expense.DriverId);
        Assert.Equal(130m, expense.AmountUsd);

        var ledger = await db.LedgerEntries.SingleAsync(l => l.SourceType == "Expense");
        Assert.Equal(LedgerSide.Credit, ledger.Side);
        Assert.Equal(1, ledger.DriverId);
        Assert.Equal(130m, ledger.AmountUsd);
    }

    [Fact]
    public async Task Settle_Dispatch_Books_Freight_Without_Discharge_And_Marks_Settled()
    {
        await using var db = CreateDb();
        await SeedReferenceDataAsync(db);
        var dispatch = await SeedLoadedDispatchAsync(db, loadedMt: 30m);
        var controller = BuildController(db);

        var result = await controller.Settle(new TruckSettlementIndexViewModel
        {
            Inputs =
            [
                new TruckSettlementRowInputViewModel
                {
                    Selected = true,
                    Kind = TruckSettlementSourceKind.Dispatch,
                    SourceId = dispatch.Id,
                    OperationDate = new DateTime(2026, 5, 6),
                    QuantityMt = 28m,
                    FreightRateUsdPerMt = 5m,
                    ShortageRateUsd = 10m,
                    FreightParty = "driver:1"
                }
            ]
        });

        Assert.IsType<RedirectToActionResult>(result);
        Assert.True(controller.ModelState.IsValid);

        var reloaded = await db.TruckDispatches.SingleAsync(d => d.Id == dispatch.Id);
        Assert.True(reloaded.IsFreightSettled);
        Assert.Equal(new DateTime(2026, 5, 6), reloaded.FreightSettledDate);
        Assert.Equal(DispatchStatus.Loaded, reloaded.Status);       // تخلیه نشده — Status دست‌نخورده
        Assert.Equal(28m, reloaded.DischargedQuantityMt);           // وزن مؤثر = وزن تخلیه، نه بارگیری
        Assert.Equal(2m, reloaded.ShortageMt);
        Assert.Equal(130m, reloaded.FreightPayableUsd);

        Assert.Empty(await db.DeliveryReceipts.ToListAsync());
        Assert.DoesNotContain(
            await db.InventoryMovements.ToListAsync(),
            m => m.ReferenceDocument != null && m.ReferenceDocument.StartsWith("TRUCK-UNLOAD:"));

        var expense = await db.ExpenseTransactions.Include(e => e.ExpenseType).SingleAsync();
        Assert.Equal("TRUCK-DISPATCH-FREIGHT", expense.ExpenseType?.Code);
        Assert.Equal(1, expense.DriverId);
        Assert.Equal(130m, expense.AmountUsd);

        // کسری یک رکورد قابل ردیابی می‌سازد تا در «راپور کسری و اضافه‌بار حمل» دیده شود.
        var loss = Assert.Single(await db.LossEvents.ToListAsync());
        Assert.Equal(LossEventStage.DispatchShortage, loss.Stage);
        Assert.Equal(30m, loss.ExpectedQuantityMt);
        Assert.Equal(28m, loss.ActualQuantityMt);
        Assert.Equal(2m, loss.DifferenceQuantityMt);
        Assert.Equal(2m, loss.ChargeableLossMt);
        Assert.False(loss.IsCancelled);
    }

    // اضافه‌وزن ترازوی مقصد: 10 تن بارگیری، 10.1 تن تخلیه. تسویه باید بپذیرد،
    // تفاوت منفی (اضافه‌بار) ثبت شود، وزن مؤثر (مبنای تخلیه/فروش) 10.1 و کرایه روی وزن بارگیری (10 تن) بماند.
    [Fact]
    public async Task Settle_Dispatch_Accepts_Discharge_Above_Loaded_And_Keeps_Freight_On_Loaded_Weight()
    {
        await using var db = CreateDb();
        await SeedReferenceDataAsync(db);
        var dispatch = await SeedLoadedDispatchAsync(db, loadedMt: 10m);
        var controller = BuildController(db);

        var result = await controller.Settle(new TruckSettlementIndexViewModel
        {
            Inputs =
            [
                new TruckSettlementRowInputViewModel
                {
                    Selected = true,
                    Kind = TruckSettlementSourceKind.Dispatch,
                    SourceId = dispatch.Id,
                    OperationDate = new DateTime(2026, 5, 8),
                    QuantityMt = 10.1m,
                    FreightRateUsdPerMt = 5m,       // کرایه = 5 × 10 = 50 (وزن بارگیری، نه 10.1)
                    ShortageRateUsd = 10m,
                    FreightParty = "driver:1"
                }
            ]
        });

        Assert.IsType<RedirectToActionResult>(result);
        Assert.True(controller.ModelState.IsValid);

        var reloaded = await db.TruckDispatches.SingleAsync(d => d.Id == dispatch.Id);
        Assert.True(reloaded.IsFreightSettled);
        Assert.Equal(10.1m, reloaded.DischargedQuantityMt);
        Assert.Equal(-0.1m, reloaded.ShortageMt);        // اضافه‌بار = تفاوت منفی
        Assert.Equal(0m, reloaded.ChargeableShortageMt);
        Assert.Null(reloaded.PayableUsd);                // اضافه‌وزن جریمهٔ کسری نمی‌سازد
        Assert.Equal(50m, reloaded.FreightCostUsd);
        Assert.Equal(50m, reloaded.FreightPayableUsd);

        // اضافه‌بار هم رکورد قابل ردیابی می‌سازد — بدون هیچ مقدار قابل جریمه.
        var loss = Assert.Single(await db.LossEvents.ToListAsync());
        Assert.Equal(LossEventStage.DispatchShortage, loss.Stage);
        Assert.Equal(10m, loss.ExpectedQuantityMt);
        Assert.Equal(10.1m, loss.ActualQuantityMt);
        Assert.Equal(-0.1m, loss.DifferenceQuantityMt);
        Assert.Equal(0m, loss.ChargeableLossMt);
        Assert.Equal(0m, loss.AllowableLossMt);
        Assert.False(loss.IsCancelled);

        var expense = await db.ExpenseTransactions.SingleAsync();
        Assert.Equal(50m, expense.AmountUsd);
    }

    // همان اضافه‌وزن برای «حمل از موجودی»: کسری منفی ثبت می‌شود تا باقیماندهٔ قابل
    // تخلیه/فروش حمل 10.1 شود، و کرایه روی 10 تن بارگیری بماند.
    [Fact]
    public async Task Settle_Leg_Accepts_Discharge_Above_Loaded_And_Raises_Remaining_Quantity()
    {
        await using var db = CreateDb();
        await SeedReferenceDataAsync(db);
        var leg = await SeedLoadedTruckLegAsync(db, quantityMt: 10m);
        var controller = BuildController(db);

        var result = await controller.Settle(new TruckSettlementIndexViewModel
        {
            Inputs =
            [
                new TruckSettlementRowInputViewModel
                {
                    Selected = true,
                    Kind = TruckSettlementSourceKind.Leg,
                    SourceId = leg.Id,
                    OperationDate = new DateTime(2026, 5, 8),
                    QuantityMt = 10.1m,
                    FreightRateUsdPerMt = 5m,       // کرایه = 5 × 10 = 50
                    ShortageRateUsd = 10m,
                    FreightParty = "driver:1"
                }
            ]
        });

        Assert.IsType<RedirectToActionResult>(result);
        Assert.True(controller.ModelState.IsValid);

        var receipt = await db.InventoryTransportReceipts.SingleAsync();
        Assert.Equal(0m, receipt.ReceivedQuantityMt);
        Assert.Equal(-0.1m, receipt.ShortageQuantityMt);     // اضافه‌وزن = کسری منفی
        Assert.Equal(0m, receipt.ChargeableShortageMt);
        Assert.Equal(50m, receipt.FreightCostUsd);
        Assert.Equal(50m, receipt.FreightPayableUsd);
        Assert.Empty(await db.LossEvents.ToListAsync());

        var reloaded = await db.InventoryTransportLegs.SingleAsync(l => l.Id == leg.Id);
        Assert.True(reloaded.IsFreightSettled);
        Assert.Equal(InventoryTransportLegStatus.Loaded, reloaded.Status);
        // باقیمانده = مقدار حمل − (دریافت + کسری) = 10 − (0 + (−0.1)) = 10.1
        var consumedMt = await db.InventoryTransportReceipts
            .Where(r => r.InventoryTransportLegId == leg.Id && !r.IsCancelled)
            .SumAsync(r => r.ReceivedQuantityMt + r.ShortageQuantityMt);
        Assert.Equal(10.1m, reloaded.QuantityMt - consumedMt);
    }

    [Fact]
    public async Task Index_Excludes_FreightSettled_Rows()
    {
        await using var db = CreateDb();
        await SeedReferenceDataAsync(db);
        var leg = await SeedLoadedTruckLegAsync(db, quantityMt: 30m);

        var before = Assert.IsType<TruckSettlementIndexViewModel>(
            Assert.IsType<ViewResult>(await BuildController(db).Index(null, null)).Model);
        Assert.Contains(before.Rows, r => r.Kind == TruckSettlementSourceKind.Leg && r.SourceId == leg.Id);

        leg.IsFreightSettled = true;
        leg.FreightSettledDate = new DateTime(2026, 5, 5);
        await db.SaveChangesAsync();

        var after = Assert.IsType<TruckSettlementIndexViewModel>(
            Assert.IsType<ViewResult>(await BuildController(db).Index(null, null)).Model);
        Assert.DoesNotContain(after.Rows, r => r.Kind == TruckSettlementSourceKind.Leg && r.SourceId == leg.Id);
    }

    [Fact]
    public async Task Index_DeepLink_Filters_And_Preselects_The_Requested_TransportLeg()
    {
        await using var db = CreateDb();
        await SeedReferenceDataAsync(db);
        var first = await SeedLoadedTruckLegAsync(db, quantityMt: 30m);
        db.Trucks.Add(new Truck { Id = 2, PlateNumber = "TRK-2" });
        var focused = new InventoryTransportLeg
        {
            Id = 2,
            SourcePurchaseContractId = 1,
            ProductId = 1,
            SourceTerminalId = 1,
            DestinationTerminalId = 2,
            TransportType = LoadingTransportType.Truck,
            TruckId = 2,
            DriverId = 1,
            LoadedDate = new DateTime(2026, 5, 3),
            QuantityMt = 24m,
            Status = InventoryTransportLegStatus.Loaded
        };
        db.InventoryTransportLegs.Add(focused);
        await db.SaveChangesAsync();

        var result = Assert.IsType<ViewResult>(
            await BuildController(db).Index(null, TruckSettlementSourceKind.Leg, focused.Id));
        var model = Assert.IsType<TruckSettlementIndexViewModel>(result.Model);

        var row = Assert.Single(model.Rows);
        var input = Assert.Single(model.Inputs);
        Assert.Equal(focused.Id, model.FocusedSourceId);
        Assert.Equal(focused.Id, row.SourceId);
        Assert.Equal("TRK-2", row.VehicleNumber);
        Assert.Equal(24m, row.RemainingQuantityMt);
        Assert.True(input.Selected);
        Assert.DoesNotContain(model.Rows, item => item.SourceId == first.Id);
    }

    [Fact]
    public async Task ImportExcel_Matches_And_Prefills_Without_Posting_Anything()
    {
        await using var db = CreateDb();
        await SeedReferenceDataAsync(db);
        var leg = await SeedLoadedTruckLegAsync(db, quantityMt: 30m);
        await using var stream = new MemoryStream(BuildSettlementWorkbook());
        var file = new FormFile(stream, 0, stream.Length, "file", "truck-settlements.xlsx")
        {
            Headers = new HeaderDictionary(),
            ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"
        };

        var result = Assert.IsType<ViewResult>(await BuildController(db).ImportExcel(file));
        var model = Assert.IsType<TruckSettlementIndexViewModel>(result.Model);

        Assert.Equal("Index", result.ViewName);
        var row = Assert.Single(model.Inputs);
        Assert.True(row.Selected);
        Assert.Equal(leg.Id, row.SourceId);
        Assert.Equal(28m, row.QuantityMt);
        Assert.Equal(1m, row.AllowanceMt);
        Assert.Empty(await db.InventoryTransportReceipts.ToListAsync());
        Assert.Empty(await db.InventoryMovements.ToListAsync());
        Assert.Empty(await db.ExpenseTransactions.ToListAsync());
        Assert.Empty(await db.LedgerEntries.ToListAsync());
        Assert.False((await db.InventoryTransportLegs.FindAsync(leg.Id))!.IsFreightSettled);
    }

    [Fact]
    public async Task Settle_Leg_Applies_Allowance_OtherDeductions_And_ShortagePenalty_Once()
    {
        await using var db = CreateDb();
        await SeedReferenceDataAsync(db);
        var leg = await SeedLoadedTruckLegAsync(db, quantityMt: 30m);
        var input = new TruckSettlementRowInputViewModel
        {
            Selected = true,
            Kind = TruckSettlementSourceKind.Leg,
            SourceId = leg.Id,
            OperationDate = new DateTime(2026, 5, 5),
            QuantityMt = 27m,
            AllowanceMt = 1m,
            FreightRateUsdPerMt = 5m,
            OtherDeductionsUsd = 10m,
            ShortageRateUsd = 10m,
            FreightParty = "driver:1"
        };

        var result = await BuildController(db).Settle(new TruckSettlementIndexViewModel { Inputs = [input] });

        Assert.IsType<RedirectToActionResult>(result);
        var receipt = await db.InventoryTransportReceipts.SingleAsync();
        Assert.Equal(3m, receipt.ShortageQuantityMt);
        Assert.Equal(1m, receipt.AllowanceMt);
        Assert.Equal(2m, receipt.ChargeableShortageMt);
        Assert.Equal(140m, receipt.FreightCostUsd);       // 5 * 30 - 10
        Assert.Equal(120m, receipt.FreightPayableUsd);    // 140 - (2 * 10)
        Assert.Equal(120m, (await db.ExpenseTransactions.SingleAsync()).AmountUsd);
        Assert.Equal(120m, (await db.LedgerEntries.SingleAsync()).AmountUsd);
        Assert.Empty(await db.InventoryMovements.ToListAsync());

        var retryController = BuildController(db);
        var retry = await retryController.Settle(new TruckSettlementIndexViewModel { Inputs = [input] });
        Assert.IsType<ViewResult>(retry);
        Assert.False(retryController.ModelState.IsValid);
        Assert.Equal(1, await db.InventoryTransportReceipts.CountAsync());
        Assert.Equal(1, await db.ExpenseTransactions.CountAsync());
        Assert.Equal(1, await db.LedgerEntries.CountAsync());
    }

    [Fact]
    public async Task Settle_Leg_ServiceProvider_Credits_Provider_Not_Driver()
    {
        await using var db = CreateDb();
        await SeedReferenceDataAsync(db);
        db.ServiceProviders.Add(new ServiceProvider { Id = 1, Name = "Carrier A", IsActive = true });
        await db.SaveChangesAsync();
        var leg = await SeedLoadedTruckLegAsync(db, quantityMt: 20m);

        var result = await BuildController(db).Settle(new TruckSettlementIndexViewModel
        {
            Inputs = [BuildSettlementInput(leg.Id, "sp:1")]
        });

        Assert.IsType<RedirectToActionResult>(result);
        var expense = await db.ExpenseTransactions.SingleAsync();
        var ledger = await db.LedgerEntries.SingleAsync();
        Assert.Equal(1, expense.ServiceProviderId);
        Assert.Null(expense.DriverId);
        Assert.Equal(1, ledger.ServiceProviderId);
        Assert.Null(ledger.DriverId);
        Assert.Equal(LedgerSide.Credit, ledger.Side);
    }

    [Fact]
    public async Task Settle_Leg_CompanyAsset_Has_Income_Record_But_No_External_Payable()
    {
        await using var db = CreateDb();
        await SeedReferenceDataAsync(db);
        db.OperationalAssets.Add(new OperationalAsset
        {
            Id = 1,
            AssetCode = "TRUCK-ASSET-1",
            Name = "Company Truck",
            AssetType = OperationalAssetType.Truck,
            OwnershipMode = OperationalAssetOwnershipMode.FullyCompanyOwned,
            IsActive = true
        });
        await db.SaveChangesAsync();
        var leg = await SeedLoadedTruckLegAsync(db, quantityMt: 20m);

        var result = await BuildController(db).Settle(new TruckSettlementIndexViewModel
        {
            Inputs = [BuildSettlementInput(leg.Id, "asset:1")]
        });

        Assert.IsType<RedirectToActionResult>(result);
        var income = await db.ExpenseTransactions.SingleAsync();
        Assert.Equal(1, income.OperationalAssetId);
        Assert.Null(income.DriverId);
        Assert.Null(income.ServiceProviderId);
        Assert.Empty(await db.LedgerEntries.ToListAsync());
        Assert.Empty(await db.InventoryMovements.ToListAsync());
    }

    [Fact]
    public async Task GroupUnload_Leg_Uses_Settled_Weight_And_Does_Not_Rebook_Freight()
    {
        await using var db = CreateDb();
        await SeedReferenceDataAsync(db);
        var leg = await SeedLoadedTruckLegAsync(db, quantityMt: 30m);
        var controller = BuildController(db);
        await controller.Settle(new TruckSettlementIndexViewModel
        {
            Inputs =
            [
                new TruckSettlementRowInputViewModel
                {
                    Selected = true,
                    Kind = TruckSettlementSourceKind.Leg,
                    SourceId = leg.Id,
                    OperationDate = new DateTime(2026, 5, 5),
                    QuantityMt = 28m,
                    FreightRateUsdPerMt = 5m,
                    ShortageRateUsd = 10m,
                    FreightParty = "driver:1"
                }
            ]
        });
        var expenseCount = await db.ExpenseTransactions.CountAsync();
        var ledgerCount = await db.LedgerEntries.CountAsync();
        controller = BuildController(db);

        var result = await controller.GroupUnload(new GroupUnloadCreateViewModel
        {
            SourceKind = TruckSettlementSourceKind.Leg,
            ReceiptDate = new DateTime(2026, 5, 7),
            DestinationStorageTankId = 2,
            DocumentReference = "GR-001",
            Items =
            [
                new GroupUnloadSelectedInput
                {
                    Selected = true,
                    Kind = TruckSettlementSourceKind.Leg,
                    SourceId = leg.Id
                }
            ]
        });

        Assert.IsType<RedirectToActionResult>(result);
        Assert.True(controller.ModelState.IsValid);
        Assert.Equal(InventoryTransportLegStatus.Received, (await db.InventoryTransportLegs.FindAsync(leg.Id))!.Status);
        var receipt = Assert.Single(
            await db.InventoryTransportReceipts.Where(item => item.ReceivedQuantityMt > 0m).ToListAsync());
        Assert.Equal(28m, receipt.ReceivedQuantityMt);
        Assert.Equal(2, receipt.DestinationStorageTankId);
        var movement = Assert.Single(
            await db.InventoryMovements.Where(item => item.ReferenceDocument == $"TRANSPORT-RECEIPT:{receipt.Id}").ToListAsync());
        Assert.Equal(28m, movement.QuantityMt);
        Assert.Equal(2, movement.StorageTankId);
        Assert.Equal(expenseCount, await db.ExpenseTransactions.CountAsync());
        Assert.Equal(ledgerCount, await db.LedgerEntries.CountAsync());
    }

    [Fact]
    public async Task GroupUnload_Dispatch_Creates_Delivery_And_Inbound_Movement_Without_Changing_Settlement()
    {
        await using var db = CreateDb();
        await SeedReferenceDataAsync(db);
        var dispatch = await SeedLoadedDispatchAsync(db, loadedMt: 30m);
        var controller = BuildController(db);
        await controller.Settle(new TruckSettlementIndexViewModel
        {
            Inputs =
            [
                new TruckSettlementRowInputViewModel
                {
                    Selected = true,
                    Kind = TruckSettlementSourceKind.Dispatch,
                    SourceId = dispatch.Id,
                    OperationDate = new DateTime(2026, 5, 6),
                    QuantityMt = 28m,
                    FreightRateUsdPerMt = 5m,
                    ShortageRateUsd = 10m,
                    FreightParty = "driver:1"
                }
            ]
        });
        var expenseCount = await db.ExpenseTransactions.CountAsync();
        controller = BuildController(db);

        var result = await controller.GroupUnload(new GroupUnloadCreateViewModel
        {
            SourceKind = TruckSettlementSourceKind.Dispatch,
            ReceiptDate = new DateTime(2026, 5, 7),
            DestinationStorageTankId = 2,
            DocumentReference = "GR-002",
            Items =
            [
                new GroupUnloadSelectedInput
                {
                    Selected = true,
                    Kind = TruckSettlementSourceKind.Dispatch,
                    SourceId = dispatch.Id
                }
            ]
        });

        Assert.IsType<RedirectToActionResult>(result);
        Assert.True(controller.ModelState.IsValid);
        var reloaded = await db.TruckDispatches.SingleAsync(item => item.Id == dispatch.Id);
        Assert.Equal(DispatchStatus.Delivered, reloaded.Status);
        Assert.Equal(28m, reloaded.DischargedQuantityMt);
        Assert.Equal(2m, reloaded.ShortageMt);
        Assert.Equal(130m, reloaded.FreightPayableUsd);
        var delivery = Assert.Single(await db.DeliveryReceipts.ToListAsync());
        Assert.Equal(28m, delivery.ReceivedQuantityMt);
        Assert.Equal("GR-002", delivery.DocumentReference);
        var movement = Assert.Single(
            await db.InventoryMovements.Where(item => item.ReferenceDocument == $"TRUCK-UNLOAD:{dispatch.Id}").ToListAsync());
        Assert.Equal(28m, movement.QuantityMt);
        Assert.Equal(2, movement.StorageTankId);
        Assert.Equal(expenseCount, await db.ExpenseTransactions.CountAsync());
        // تسویه رکورد کسری را ساخته و تخلیهٔ گروهی همان را به‌روز می‌کند — نه رکورد تکراری.
        var loss = Assert.Single(await db.LossEvents.Where(item => item.TruckDispatchId == dispatch.Id).ToListAsync());
        Assert.Equal(2, loss.StorageTankId);
        Assert.Equal(2m, loss.DifferenceQuantityMt);
        Assert.Equal(2m, loss.ChargeableLossMt);
        Assert.False(loss.IsCancelled);
    }

    // تخلیهٔ گروهی با اضافه‌بار: 10 تن بارگیری، 10.1 تن تخلیه.
    // یک رکورد اضافه‌بار (بدون جریمه) باقی می‌ماند، موجودی مقصد به اندازهٔ وزن واقعی تخلیه‌شده
    // ثبت می‌شود و تسویهٔ کرایهٔ موتر دست‌نخورده می‌ماند.
    [Fact]
    public async Task GroupUnload_Dispatch_Surplus_Keeps_Single_Surplus_LossEvent_And_Books_Full_Discharge()
    {
        await using var db = CreateDb();
        await SeedReferenceDataAsync(db);
        var dispatch = await SeedLoadedDispatchAsync(db, loadedMt: 10m);
        await SettleDispatchAsync(db, dispatch.Id, quantityMt: 10.1m);
        var expenseCount = await db.ExpenseTransactions.CountAsync();
        var freightPayableUsd = (await db.TruckDispatches.SingleAsync(item => item.Id == dispatch.Id)).FreightPayableUsd;
        var controller = BuildController(db);

        var result = await controller.GroupUnload(BuildGroupUnloadModel(dispatch.Id, "GR-003"));

        Assert.IsType<RedirectToActionResult>(result);
        Assert.True(controller.ModelState.IsValid);

        var reloaded = await db.TruckDispatches.SingleAsync(item => item.Id == dispatch.Id);
        Assert.Equal(DispatchStatus.Delivered, reloaded.Status);
        Assert.Equal(10.1m, reloaded.DischargedQuantityMt);
        Assert.Equal(-0.1m, reloaded.ShortageMt);
        Assert.Equal(0m, reloaded.ChargeableShortageMt);
        Assert.Null(reloaded.PayableUsd);
        Assert.Equal(freightPayableUsd, reloaded.FreightPayableUsd);      // تسویهٔ کرایه دست‌نخورده
        Assert.Equal(expenseCount, await db.ExpenseTransactions.CountAsync());

        // موجودی مقصد = وزن واقعی تخلیه‌شده، نه وزن بارگیری.
        var movement = Assert.Single(
            await db.InventoryMovements.Where(item => item.ReferenceDocument == $"TRUCK-UNLOAD:{dispatch.Id}").ToListAsync());
        Assert.Equal(10.1m, movement.QuantityMt);
        Assert.Equal(2, movement.StorageTankId);
        Assert.Equal(MovementDirection.In, movement.Direction);

        var loss = Assert.Single(await db.LossEvents.Where(item => item.TruckDispatchId == dispatch.Id).ToListAsync());
        Assert.Equal(-0.1m, loss.DifferenceQuantityMt);
        Assert.Equal(0m, loss.ChargeableLossMt);
        Assert.Equal(2, loss.StorageTankId);
        Assert.False(loss.IsCancelled);
    }

    // رکورد اضافه‌بارِ ساخته‌شده باید در «راپور کسری و اضافه‌بار حمل» به‌عنوان اضافه‌بار دیده شود.
    [Fact]
    public async Task TransportVariance_Report_Shows_Dispatch_Surplus_After_GroupUnload()
    {
        await using var db = CreateDb();
        await SeedReferenceDataAsync(db);
        var dispatch = await SeedLoadedDispatchAsync(db, loadedMt: 10m);
        await SettleDispatchAsync(db, dispatch.Id, quantityMt: 10.1m);
        await BuildController(db).GroupUnload(BuildGroupUnloadModel(dispatch.Id, "GR-004"));

        var view = Assert.IsType<ViewResult>(
            await new ReportsController(db).TransportVariance());
        var model = Assert.IsType<TransportVarianceReportViewModel>(view.Model);

        var row = Assert.Single(model.Rows);
        Assert.Equal(TransportVarianceKind.Surplus, row.Kind);
        Assert.Equal(TransportVarianceSource.TruckDispatch, row.Source);
        Assert.Equal(10m, row.LoadedQuantityMt);
        Assert.Equal(10.1m, row.UnloadedQuantityMt);
        Assert.Equal(-0.1m, row.DifferenceQuantityMt);
        Assert.Equal(0m, row.ChargeableShortageMt);
        Assert.Equal(0.1m, model.Totals.TotalSurplusMt);
        Assert.Equal(0m, model.Totals.TotalShortageMt);
        Assert.Equal(1, model.Totals.SurplusCount);
    }

    // اگر وزن تخلیه بعداً برابر وزن بارگیری شود، رکورد قبلی لغو می‌شود (نه رکورد دوم).
    [Fact]
    public async Task GroupUnload_Cancels_LossEvent_When_Difference_Becomes_Zero()
    {
        await using var db = CreateDb();
        await SeedReferenceDataAsync(db);
        var dispatch = await SeedLoadedDispatchAsync(db, loadedMt: 30m);
        await SettleDispatchAsync(db, dispatch.Id, quantityMt: 28m);
        Assert.Single(await db.LossEvents.Where(item => item.TruckDispatchId == dispatch.Id).ToListAsync());

        // اصلاح وزن تخلیه به وزن بارگیری پیش از تخلیهٔ گروهی.
        var tracked = await db.TruckDispatches.SingleAsync(item => item.Id == dispatch.Id);
        tracked.DischargedQuantityMt = 30m;
        await db.SaveChangesAsync();

        var controller = BuildController(db);
        var result = await controller.GroupUnload(BuildGroupUnloadModel(dispatch.Id, "GR-005"));

        Assert.IsType<RedirectToActionResult>(result);
        Assert.True(controller.ModelState.IsValid);

        var loss = Assert.Single(await db.LossEvents.Where(item => item.TruckDispatchId == dispatch.Id).ToListAsync());
        Assert.True(loss.IsCancelled);

        // رکورد لغوشده در راپور دیده نمی‌شود.
        var view = Assert.IsType<ViewResult>(await new ReportsController(db).TransportVariance());
        Assert.Empty(Assert.IsType<TransportVarianceReportViewModel>(view.Model).Rows);
    }

    [Fact]
    public async Task GroupUnload_Rejects_Mixed_Source_Kinds_Without_Inventory_Effect()
    {
        await using var db = CreateDb();
        await SeedReferenceDataAsync(db);
        var leg = await SeedLoadedTruckLegAsync(db, quantityMt: 30m);
        var dispatch = await SeedLoadedDispatchAsync(db, loadedMt: 30m);
        leg.IsFreightSettled = true;
        dispatch.IsFreightSettled = true;
        dispatch.DischargedQuantityMt = 30m;
        await db.SaveChangesAsync();
        var controller = BuildController(db);

        var result = await controller.GroupUnload(new GroupUnloadCreateViewModel
        {
            SourceKind = TruckSettlementSourceKind.Leg,
            ReceiptDate = new DateTime(2026, 5, 7),
            DestinationStorageTankId = 2,
            Items =
            [
                new GroupUnloadSelectedInput
                {
                    Selected = true,
                    Kind = TruckSettlementSourceKind.Leg,
                    SourceId = leg.Id
                },
                new GroupUnloadSelectedInput
                {
                    Selected = true,
                    Kind = TruckSettlementSourceKind.Dispatch,
                    SourceId = dispatch.Id
                }
            ]
        });

        Assert.IsType<ViewResult>(result);
        Assert.False(controller.ModelState.IsValid);
        Assert.Empty(await db.DeliveryReceipts.ToListAsync());
        Assert.Empty(await db.InventoryMovements.ToListAsync());
        Assert.Equal(InventoryTransportLegStatus.Loaded, (await db.InventoryTransportLegs.FindAsync(leg.Id))!.Status);
        Assert.Equal(DispatchStatus.Loaded, (await db.TruckDispatches.FindAsync(dispatch.Id))!.Status);
    }

    private static ApplicationDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ApplicationDbContext(options);
    }

    private static TruckSettlementsController BuildController(ApplicationDbContext db)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers.Cookie = "ptg-ui-lang=fa";

        return new(
            db,
            new CurrencyConversionService(new PricingService(db)),
            InventoryLineageWriterFactory.Disabled(db),
            new LossEventWorkflowService(db, new StockService(db), new AuditService(db)))
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
            TempData = new TempDataDictionary(httpContext, new InMemoryTempDataProvider())
        };
    }

    private static async Task SeedReferenceDataAsync(ApplicationDbContext db)
    {
        db.Companies.Add(new Company { Id = 1, Code = "PTG", Name = "PTG" });
        db.Suppliers.Add(new Supplier { Id = 1, Name = "Supplier A" });
        db.Products.Add(new Product { Id = 1, Code = "GO", Name = "Gas Oil" });
        db.Trucks.Add(new Truck { Id = 1, PlateNumber = "TRK-1" });
        db.Drivers.Add(new Driver { Id = 1, FullName = "Driver A", IsActive = true });
        db.Terminals.AddRange(
            new Terminal { Id = 1, Code = "SRC", Name = "Source Terminal" },
            new Terminal { Id = 2, Code = "DST", Name = "Destination Terminal" });
        db.StorageTanks.Add(new StorageTank
        {
            Id = 2,
            TerminalId = 2,
            TankCode = "DST-01",
            ProductId = 1,
            CapacityMt = 1000m,
            IsActive = true
        });
        db.Contracts.Add(new Contract
        {
            Id = 1,
            ContractNumber = "PUR-001",
            ContractType = ContractType.Purchase,
            CompanyId = 1,
            ProductId = 1,
            SupplierId = 1,
            ContractDate = new DateTime(2026, 5, 1),
            QuantityMt = 100m,
            PricingMethod = PricingMethod.Fixed,
            UnitPriceUsd = 500m
        });
        await db.SaveChangesAsync();
    }

    private static async Task<InventoryTransportLeg> SeedLoadedTruckLegAsync(ApplicationDbContext db, decimal quantityMt)
    {
        var leg = new InventoryTransportLeg
        {
            Id = 1,
            SourcePurchaseContractId = 1,
            ProductId = 1,
            SourceTerminalId = 1,
            DestinationTerminalId = 2,
            TransportType = LoadingTransportType.Truck,
            TruckId = 1,
            DriverId = 1,
            LoadedDate = new DateTime(2026, 5, 2),
            QuantityMt = quantityMt,
            Status = InventoryTransportLegStatus.Loaded
        };
        db.InventoryTransportLegs.Add(leg);
        await db.SaveChangesAsync();
        return leg;
    }

    private static TruckSettlementRowInputViewModel BuildSettlementInput(int sourceId, string freightParty)
        => new()
        {
            Selected = true,
            Kind = TruckSettlementSourceKind.Leg,
            SourceId = sourceId,
            OperationDate = new DateTime(2026, 5, 5),
            QuantityMt = 20m,
            FreightRateUsdPerMt = 5m,
            FreightParty = freightParty
        };

    private static byte[] BuildSettlementWorkbook()
    {
        using var stream = new MemoryStream();
        using (var document = SpreadsheetDocument.Create(stream, SpreadsheetDocumentType.Workbook, true))
        {
            var workbookPart = document.AddWorkbookPart();
            workbookPart.Workbook = new Workbook();
            var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
            worksheetPart.Worksheet = new Worksheet(new SheetData(
                BuildWorkbookRow(1, ("A", "نمبر موتر"), ("B", "وزن تخلیه"), ("C", "حواکت")),
                BuildWorkbookRow(2, ("A", "TRK-1"), ("B", "28"), ("C", "1"))));

            var sheets = workbookPart.Workbook.AppendChild(new Sheets());
            sheets.Append(new Sheet
            {
                Id = workbookPart.GetIdOfPart(worksheetPart),
                SheetId = 1,
                Name = "settlements"
            });
            workbookPart.Workbook.Save();
        }

        return stream.ToArray();
    }

    private static Row BuildWorkbookRow(uint rowIndex, params (string Column, string Value)[] values)
    {
        var row = new Row { RowIndex = rowIndex };
        foreach (var (column, value) in values)
        {
            row.Append(new Cell
            {
                CellReference = column + rowIndex,
                DataType = CellValues.String,
                CellValue = new CellValue(value)
            });
        }

        return row;
    }

    // تسویهٔ کرایهٔ یک دیسپچ با وزن تخلیهٔ داده‌شده (مبنای مشترک تست‌های تخلیهٔ گروهی).
    private static async Task SettleDispatchAsync(ApplicationDbContext db, int dispatchId, decimal quantityMt)
    {
        var controller = BuildController(db);
        var result = await controller.Settle(new TruckSettlementIndexViewModel
        {
            Inputs =
            [
                new TruckSettlementRowInputViewModel
                {
                    Selected = true,
                    Kind = TruckSettlementSourceKind.Dispatch,
                    SourceId = dispatchId,
                    OperationDate = new DateTime(2026, 5, 6),
                    QuantityMt = quantityMt,
                    FreightRateUsdPerMt = 5m,
                    ShortageRateUsd = 10m,
                    FreightParty = "driver:1"
                }
            ]
        });

        Assert.IsType<RedirectToActionResult>(result);
        Assert.True(controller.ModelState.IsValid);
    }

    private static GroupUnloadCreateViewModel BuildGroupUnloadModel(int dispatchId, string documentReference)
        => new()
        {
            SourceKind = TruckSettlementSourceKind.Dispatch,
            ReceiptDate = new DateTime(2026, 5, 7),
            DestinationStorageTankId = 2,
            DocumentReference = documentReference,
            Items =
            [
                new GroupUnloadSelectedInput
                {
                    Selected = true,
                    Kind = TruckSettlementSourceKind.Dispatch,
                    SourceId = dispatchId
                }
            ]
        };

    private static async Task<TruckDispatch> SeedLoadedDispatchAsync(ApplicationDbContext db, decimal loadedMt)
    {
        var dispatch = new TruckDispatch
        {
            Id = 1,
            ContractId = 1,
            ProductId = 1,
            TruckId = 1,
            DriverId = 1,
            DispatchDate = new DateTime(2026, 5, 2),
            Status = DispatchStatus.Loaded,
            LoadedQuantityMt = loadedMt
        };
        db.TruckDispatches.Add(dispatch);
        await db.SaveChangesAsync();
        return dispatch;
    }

    private sealed class InMemoryTempDataProvider : ITempDataProvider
    {
        private IDictionary<string, object> _data = new Dictionary<string, object>();

        public IDictionary<string, object> LoadTempData(HttpContext context) => _data;

        public void SaveTempData(HttpContext context, IDictionary<string, object> values)
            => _data = new Dictionary<string, object>(values);
    }
}
