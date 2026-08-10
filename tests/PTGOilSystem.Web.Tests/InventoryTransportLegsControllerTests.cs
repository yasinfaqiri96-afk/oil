using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Controllers;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Helpers;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.InventoryTransport;
using PTGOilSystem.Web.Models.Reconciliation;
using PTGOilSystem.Web.Models.Reports;
using PTGOilSystem.Web.Services;
using Xunit;

namespace PTGOilSystem.Web.Tests;

public class InventoryTransportLegsControllerTests
{
    [Fact]
    public async Task Index_Uses_Real_Vessel_Name_For_Shipment_Backed_Unspecified_Transport()
    {
        await using var db = CreateDb();
        await SeedReferenceDataAsync(db);
        db.Vessels.Add(new Vessel { Id = 1, Code = "VS-1", Name = "Volga Star", IsActive = true });
        db.Shipments.Add(new Shipment
        {
            Id = 1,
            ShipmentCode = "SHIP-VOLGA-01",
            VesselId = 1,
            QuantityMt = 75m
        });
        db.InventoryTransportLegs.Add(new InventoryTransportLeg
        {
            Id = 1,
            ShipmentId = 1,
            SourcePurchaseContractId = 1,
            ProductId = 1,
            SourceTerminalId = 1,
            TransportType = LoadingTransportType.Unspecified,
            LoadedDate = new DateTime(2026, 5, 3),
            QuantityMt = 75m,
            Status = InventoryTransportLegStatus.InTransit
        });
        await db.SaveChangesAsync();

        var controller = BuildController(db);
        var result = await controller.Index(new InventoryTransportLegIndexFilterViewModel());

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<InventoryTransportLegIndexViewModel>(view.Model);
        var item = Assert.Single(model.Items);
        Assert.Equal(1, item.ShipmentId);
        Assert.Equal("SHIP-VOLGA-01", item.ShipmentCode);
        Assert.Equal("Volga Star", item.VesselName);
        Assert.Equal(LoadingTransportType.Unspecified, item.TransportType);
    }

    [Fact]
    public async Task Active_Groups_InProgress_Legs_By_Shared_Transport_Reference_And_Contract()
    {
        await using var db = CreateDb();
        await SeedReferenceDataAsync(db);
        db.Contracts.Add(new Contract
        {
            Id = 3,
            ContractNumber = "PUR-2",
            ContractType = ContractType.Purchase,
            CompanyId = 1,
            SupplierId = 1,
            ProductId = 1,
            ContractDate = new DateTime(2026, 5, 2),
            QuantityMt = 200m,
            PricingMethod = PricingMethod.Fixed,
            UnitPriceUsd = 110m
        });
        db.InventoryTransportLegs.AddRange(
            new InventoryTransportLeg
            {
                Id = 100,
                SourcePurchaseContractId = 1,
                ProductId = 1,
                SourceTerminalId = 1,
                SourceStorageTankId = 1,
                DestinationTerminalId = 2,
                TransportType = LoadingTransportType.Vessel,
                WagonNumber = "Vessel Alpha",
                RwbNo = "VOY-01",
                BillOfLadingNumber = "VES-100",
                LoadedDate = new DateTime(2026, 5, 4),
                ExpectedArrivalDate = new DateTime(2026, 5, 9),
                QuantityMt = 40m,
                Status = InventoryTransportLegStatus.Loaded,
                OutboundInventoryMovementId = 10
            },
            new InventoryTransportLeg
            {
                Id = 101,
                SourcePurchaseContractId = 3,
                ProductId = 1,
                SourceTerminalId = 1,
                SourceStorageTankId = 1,
                DestinationTerminalId = 2,
                TransportType = LoadingTransportType.Vessel,
                WagonNumber = "Vessel Alpha",
                RwbNo = "VOY-01",
                BillOfLadingNumber = "VES-100",
                LoadedDate = new DateTime(2026, 5, 4),
                ExpectedArrivalDate = new DateTime(2026, 5, 9),
                QuantityMt = 60m,
                Status = InventoryTransportLegStatus.Draft
            },
            new InventoryTransportLeg
            {
                Id = 102,
                SourcePurchaseContractId = 1,
                ProductId = 1,
                SourceTerminalId = 1,
                TransportType = LoadingTransportType.Vessel,
                BillOfLadingNumber = "VES-RECEIVED",
                LoadedDate = new DateTime(2026, 5, 1),
                QuantityMt = 10m,
                Status = InventoryTransportLegStatus.Received
            });
        db.InventoryMovements.Add(new InventoryMovement
        {
            Id = 10,
            ProductId = 1,
            ContractId = 1,
            TerminalId = 1,
            StorageTankId = 1,
            Direction = MovementDirection.Out,
            MovementDate = new DateTime(2026, 5, 4),
            QuantityMt = 40m,
            ReferenceDocument = "TRANSPORT-LEG:100"
        });
        await db.SaveChangesAsync();
        var controller = BuildController(db);

        var result = await controller.Active();

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<InventoryTransportFlowDashboardViewModel>(view.Model);
        Assert.Equal(1, model.ActiveTransportCount);
        Assert.Equal(100m, model.TotalAllocatedQuantityMt);
        Assert.Equal(40m, model.TotalLoadedQuantityMt);
        Assert.Equal(2, model.TotalContractCount);

        var transport = Assert.Single(model.Transports);
        Assert.Equal("VEH:1:VESSEL ALPHA", transport.GroupKey);
        Assert.Equal("Vessel Alpha", transport.PrimaryReference);
        Assert.Equal(100m, transport.TotalAllocatedQuantityMt);
        Assert.Equal(40m, transport.LoadedQuantityMt);
        Assert.Equal(60m, transport.PendingQuantityMt);
        Assert.Equal(2, transport.ContractCount);

        Assert.Collection(
            transport.ContractAllocations.OrderBy(a => a.ContractNumber),
            first =>
            {
                Assert.Equal("PUR-1", first.ContractNumber);
                Assert.Equal(40m, first.AllocatedQuantityMt);
                Assert.Equal(40m, first.LoadedQuantityMt);
            },
            second =>
            {
                Assert.Equal("PUR-2", second.ContractNumber);
                Assert.Equal(60m, second.AllocatedQuantityMt);
                Assert.Equal(0m, second.LoadedQuantityMt);
            });
    }

    [Fact]
    public async Task ActiveDetails_Shows_Allocation_Breakdown_For_Selected_Transport_Group()
    {
        await using var db = CreateDb();
        await SeedReferenceDataAsync(db);
        db.Contracts.Add(new Contract
        {
            Id = 3,
            ContractNumber = "PUR-2",
            ContractType = ContractType.Purchase,
            CompanyId = 1,
            SupplierId = 1,
            ProductId = 1,
            ContractDate = new DateTime(2026, 5, 2),
            QuantityMt = 200m,
            PricingMethod = PricingMethod.Fixed,
            UnitPriceUsd = 110m
        });
        db.InventoryTransportLegs.AddRange(
            new InventoryTransportLeg
            {
                Id = 200,
                SourcePurchaseContractId = 1,
                ProductId = 1,
                SourceTerminalId = 1,
                TransportType = LoadingTransportType.Truck,
                WagonNumber = "TRK-10",
                RwbNo = "CMR-77",
                LoadedDate = new DateTime(2026, 6, 1),
                QuantityMt = 25m,
                Status = InventoryTransportLegStatus.Loaded,
                OutboundInventoryMovementId = 20
            },
            new InventoryTransportLeg
            {
                Id = 201,
                SourcePurchaseContractId = 3,
                ProductId = 1,
                SourceTerminalId = 1,
                TransportType = LoadingTransportType.Truck,
                WagonNumber = "TRK-10",
                RwbNo = "CMR-77",
                LoadedDate = new DateTime(2026, 6, 1),
                QuantityMt = 35m,
                Status = InventoryTransportLegStatus.InTransit,
                OutboundInventoryMovementId = 21
            });
        db.InventoryMovements.AddRange(
            new InventoryMovement
            {
                Id = 20,
                ProductId = 1,
                ContractId = 1,
                TerminalId = 1,
                Direction = MovementDirection.Out,
                MovementDate = new DateTime(2026, 6, 1),
                QuantityMt = 25m,
                ReferenceDocument = "TRANSPORT-LEG:200"
            },
            new InventoryMovement
            {
                Id = 21,
                ProductId = 1,
                ContractId = 3,
                TerminalId = 1,
                Direction = MovementDirection.Out,
                MovementDate = new DateTime(2026, 6, 1),
                QuantityMt = 35m,
                ReferenceDocument = "TRANSPORT-LEG:201"
            });
        await db.SaveChangesAsync();
        var controller = BuildController(db);

        var view = Assert.IsType<ViewResult>(await controller.Active());
        var model = Assert.Single(
            Assert.IsType<InventoryTransportFlowDashboardViewModel>(view.Model).Transports,
            t => t.GroupKey == "VEH:3:TRK-10");
        Assert.Equal("VEH:3:TRK-10", model.GroupKey);
        Assert.Equal(60m, model.TotalAllocatedQuantityMt);
        Assert.Equal(60m, model.LoadedQuantityMt);
        Assert.Equal(2, model.ContractAllocations.Count);
        Assert.All(model.Legs, leg => Assert.Equal("CMR-77", leg.RwbNo));
    }

    [Fact]
    public async Task CreateGroupExpense_Splits_Expense_By_Contract_Transport_Share()
    {
        await using var db = CreateDb();
        await SeedReferenceDataAsync(db);
        db.Currencies.Add(new Currency { Id = 1, Code = "USD", Name = "US Dollar", Symbol = "$", IsActive = true });
        db.ExpenseTypes.Add(new ExpenseType { Id = 1, Code = "PORT", Name = "Port charges", Category = "Transport" });
        db.Contracts.Add(new Contract
        {
            Id = 3,
            ContractNumber = "PUR-2",
            ContractType = ContractType.Purchase,
            CompanyId = 1,
            SupplierId = 1,
            ProductId = 1,
            ContractDate = new DateTime(2026, 5, 2),
            QuantityMt = 200m,
            PricingMethod = PricingMethod.Fixed,
            UnitPriceUsd = 110m
        });
        db.Shipments.Add(new Shipment
        {
            Id = 1,
            ShipmentCode = "KALUGA",
            DepartureDate = new DateTime(2026, 6, 1),
            QuantityMt = 100m
        });
        db.InventoryTransportLegs.AddRange(
            new InventoryTransportLeg
            {
                Id = 300,
                ShipmentId = 1,
                SourcePurchaseContractId = 1,
                ProductId = 1,
                SourceTerminalId = 1,
                TransportType = LoadingTransportType.Vessel,
                WagonNumber = "KALUGA",
                LoadedDate = new DateTime(2026, 6, 1),
                QuantityMt = 40m,
                Status = InventoryTransportLegStatus.Loaded,
                OutboundInventoryMovementId = 30
            },
            new InventoryTransportLeg
            {
                Id = 301,
                ShipmentId = 1,
                SourcePurchaseContractId = 3,
                ProductId = 1,
                SourceTerminalId = 1,
                TransportType = LoadingTransportType.Vessel,
                WagonNumber = "KALUGA",
                LoadedDate = new DateTime(2026, 6, 1),
                QuantityMt = 60m,
                Status = InventoryTransportLegStatus.InTransit,
                OutboundInventoryMovementId = 31
            });
        await db.SaveChangesAsync();
        var controller = BuildController(db);

        var result = await controller.CreateGroupExpense(new InventoryTransportGroupExpenseCreateViewModel
        {
            GroupKey = "SHIP:1",
            ExpenseTypeId = 1,
            ExpenseDate = new DateTime(2026, 6, 2),
            Amount = 1000m,
            Currency = "USD",
            AppliedFxRateToUsd = 1m,
            Description = "Port invoice"
        });

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Journey", redirect.ActionName);
        Assert.Equal("SHIP:1", redirect.RouteValues!["groupKey"]);

        var expenses = await db.ExpenseTransactions
            .OrderBy(e => e.ContractId)
            .ToListAsync();
        Assert.Collection(
            expenses,
            first =>
            {
                Assert.Equal(1, first.ContractId);
                Assert.Equal(1, first.ShipmentId);
                Assert.Equal(300, first.TransportLegId);
                Assert.Equal(400m, first.Amount);
                Assert.Equal(400m, first.AmountUsd);
            },
            second =>
            {
                Assert.Equal(3, second.ContractId);
                Assert.Equal(1, second.ShipmentId);
                Assert.Equal(301, second.TransportLegId);
                Assert.Equal(600m, second.Amount);
                Assert.Equal(600m, second.AmountUsd);
            });

        var ledgerEntries = await db.LedgerEntries
            .OrderBy(e => e.ContractId)
            .ToListAsync();
        Assert.Collection(
            ledgerEntries,
            first =>
            {
                Assert.Equal(1, first.ContractId);
                Assert.Equal(1, first.ShipmentId);
                Assert.Equal(400m, first.AmountUsd);
                Assert.Equal(400m, first.SourceAmount);
                Assert.Equal("Expense", first.SourceType);
            },
            second =>
            {
                Assert.Equal(3, second.ContractId);
                Assert.Equal(1, second.ShipmentId);
                Assert.Equal(600m, second.AmountUsd);
                Assert.Equal(600m, second.SourceAmount);
                Assert.Equal("Expense", second.SourceType);
            });

        var details = Assert.IsType<ViewResult>(await controller.Active());
        var detailsModel = Assert.Single(
            Assert.IsType<InventoryTransportFlowDashboardViewModel>(details.Model).Transports,
            t => t.GroupKey == "SHIP:1");
        Assert.Equal(2, detailsModel.ExpenseCount);
        Assert.Equal(1000m, detailsModel.TotalExpenseUsd);
        Assert.Equal(new[] { 400m, 600m }, detailsModel.Expenses.OrderBy(e => e.ContractId).Select(e => e.AmountUsd).ToArray());
        Assert.Collection(
            detailsModel.ContractAllocations.OrderBy(a => a.ContractId),
            first =>
            {
                Assert.Equal(1, first.ContractId);
                Assert.Equal(400m, first.ExpenseAmountUsd);
                Assert.Equal(1, first.ExpenseCount);
            },
            second =>
            {
                Assert.Equal(3, second.ContractId);
                Assert.Equal(600m, second.ExpenseAmountUsd);
                Assert.Equal(1, second.ExpenseCount);
            });
        Assert.Equal(1, detailsModel.ExpenseCurrentPage);
        Assert.Equal(1, detailsModel.ExpensePageCount);
    }

    [Fact]
    public async Task CreateGroupExpense_With_ServiceProvider_Links_Expenses_And_Credit_Ledgers()
    {
        await using var db = CreateDb();
        await SeedReferenceDataAsync(db);
        db.Currencies.Add(new Currency { Id = 1, Code = "USD", Name = "US Dollar", Symbol = "$", IsActive = true });
        db.ExpenseTypes.Add(new ExpenseType { Id = 1, Code = "WGN", Name = "Wagon rent", Category = "Transport" });
        db.ServiceProviders.Add(new ServiceProvider
        {
            Id = 1,
            Code = "SP-RW",
            Name = "Railway Services",
            ProviderType = ServiceProviderType.RailwayService,
            IsActive = true
        });
        db.Contracts.Add(new Contract
        {
            Id = 3,
            ContractNumber = "PUR-2",
            ContractType = ContractType.Purchase,
            CompanyId = 1,
            SupplierId = 1,
            ProductId = 1,
            ContractDate = new DateTime(2026, 5, 2),
            QuantityMt = 200m,
            PricingMethod = PricingMethod.Fixed,
            UnitPriceUsd = 110m
        });
        db.Shipments.Add(new Shipment
        {
            Id = 1,
            ShipmentCode = "KALUGA",
            DepartureDate = new DateTime(2026, 6, 1),
            QuantityMt = 100m
        });
        db.InventoryTransportLegs.AddRange(
            new InventoryTransportLeg
            {
                Id = 300,
                ShipmentId = 1,
                SourcePurchaseContractId = 1,
                ProductId = 1,
                SourceTerminalId = 1,
                TransportType = LoadingTransportType.Vessel,
                WagonNumber = "KALUGA",
                LoadedDate = new DateTime(2026, 6, 1),
                QuantityMt = 40m,
                Status = InventoryTransportLegStatus.Loaded,
                OutboundInventoryMovementId = 30
            },
            new InventoryTransportLeg
            {
                Id = 301,
                ShipmentId = 1,
                SourcePurchaseContractId = 3,
                ProductId = 1,
                SourceTerminalId = 1,
                TransportType = LoadingTransportType.Vessel,
                WagonNumber = "KALUGA",
                LoadedDate = new DateTime(2026, 6, 1),
                QuantityMt = 60m,
                Status = InventoryTransportLegStatus.InTransit,
                OutboundInventoryMovementId = 31
            });
        await db.SaveChangesAsync();
        var controller = BuildController(db);

        var result = await controller.CreateGroupExpense(new InventoryTransportGroupExpenseCreateViewModel
        {
            GroupKey = "SHIP:1",
            ExpenseTypeId = 1,
            ServiceProviderId = 1,
            ExpenseDate = new DateTime(2026, 6, 2),
            Amount = 1000m,
            Currency = "USD",
            AppliedFxRateToUsd = 1m,
            Description = "Wagon rent invoice"
        });

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Journey", redirect.ActionName);

        var expenses = await db.ExpenseTransactions
            .OrderBy(e => e.ContractId)
            .ToListAsync();
        Assert.All(expenses, expense => Assert.Equal(1, expense.ServiceProviderId));
        Assert.Equal(new[] { 400m, 600m }, expenses.Select(e => e.AmountUsd).ToArray());

        var ledgerEntries = await db.LedgerEntries
            .OrderBy(e => e.ContractId)
            .ToListAsync();
        Assert.All(ledgerEntries, ledger =>
        {
            Assert.Equal(1, ledger.ServiceProviderId);
            Assert.Equal(LedgerSide.Credit, ledger.Side);
            Assert.Equal("Expense", ledger.SourceType);
        });

        var details = Assert.IsType<ViewResult>(await controller.Active());
        var detailsModel = Assert.Single(
            Assert.IsType<InventoryTransportFlowDashboardViewModel>(details.Model).Transports,
            t => t.GroupKey == "SHIP:1");
        Assert.All(detailsModel.Expenses, expense => Assert.Equal("Railway Services", expense.ServiceProviderName));
    }

    [Fact]
    public async Task SaveGroupExpenses_Accepts_Selected_ExpenseType_From_List()
    {
        await using var db = CreateDb();
        await SeedReferenceDataAsync(db);
        db.ExpenseTypes.Add(new ExpenseType { Id = 1, Code = "PORT", Name = "Port charges", Category = "Transport", IsActive = true });
        await SeedShipmentTransportGroupAsync(db);
        var controller = BuildController(db);

        var result = await controller.SaveGroupExpenses(new InventoryTransportGroupExpenseModalViewModel
        {
            GroupKey = "SHIP:1",
            Lines =
            [
                new InventoryTransportGroupExpenseModalRow
                {
                    ExpenseTypeId = 1,
                    AmountUsd = 1000m,
                    PartyType = LoadingExpensePartyType.None
                }
            ]
        });

        var redirect = Assert.IsType<RedirectResult>(result);
        Assert.NotNull(redirect.Url);
        Assert.Contains("/InventoryTransportLegs/Journey", redirect.Url);
        Assert.Contains("groupKey=SHIP", redirect.Url);

        var expenses = await db.ExpenseTransactions.OrderBy(e => e.ContractId).ToListAsync();
        Assert.Equal(new[] { 400m, 600m }, expenses.Select(e => e.AmountUsd).ToArray());
        Assert.All(expenses, expense => Assert.Equal(1, expense.ExpenseTypeId));
    }

    [Fact]
    public async Task SaveGroupExpenses_Creates_New_ExpenseType_From_Manual_Name_And_Shows_It_When_Reopened()
    {
        await using var db = CreateDb();
        await SeedReferenceDataAsync(db);
        await SeedShipmentTransportGroupAsync(db);
        var controller = BuildController(db);

        var saveResult = await controller.SaveGroupExpenses(new InventoryTransportGroupExpenseModalViewModel
        {
            GroupKey = "SHIP:1",
            Lines =
            [
                new InventoryTransportGroupExpenseModalRow
                {
                    ManualExpenseTypeName = "هزینه تخلیه بندر",
                    AmountUsd = 1000m,
                    PartyType = LoadingExpensePartyType.None
                }
            ]
        });

        Assert.IsType<RedirectResult>(saveResult);

        var manualType = await db.ExpenseTypes.SingleAsync(e => e.Name == "هزینه تخلیه بندر");
        var expenses = await db.ExpenseTransactions
            .OrderBy(e => e.ContractId)
            .ToListAsync();
        Assert.Equal(new[] { 400m, 600m }, expenses.Select(e => e.AmountUsd).ToArray());
        Assert.All(expenses, expense => Assert.Equal(manualType.Id, expense.ExpenseTypeId));

        var reopenResult = await controller.CreateGroupExpenseModal("SHIP:1");
        var partial = Assert.IsType<PartialViewResult>(reopenResult);
        var model = Assert.IsType<InventoryTransportGroupExpenseModalViewModel>(partial.Model);
        var existingExpenses = model.ExistingExpenses
            .OrderBy(expense => expense.ContractId)
            .ToList();
        Assert.Equal(2, existingExpenses.Count);
        Assert.Equal(new[] { 400m, 600m }, existingExpenses.Select(expense => expense.AmountUsd).ToArray());
        Assert.Equal(new int?[] { 1, 3 }, existingExpenses.Select(expense => expense.ContractId).ToArray());
        Assert.All(existingExpenses, expense => Assert.Equal("هزینه تخلیه بندر", expense.ExpenseTypeName));
    }

    [Fact]
    public async Task SaveGroupExpenses_Rejects_Blank_Manual_Name_When_No_Type_Is_Selected()
    {
        await using var db = CreateDb();
        await SeedReferenceDataAsync(db);
        await SeedShipmentTransportGroupAsync(db);
        var controller = BuildController(db);

        var result = await controller.SaveGroupExpenses(new InventoryTransportGroupExpenseModalViewModel
        {
            GroupKey = "SHIP:1",
            Lines =
            [
                new InventoryTransportGroupExpenseModalRow
                {
                    ManualExpenseTypeName = "   ",
                    AmountUsd = 55m,
                    PartyType = LoadingExpensePartyType.None
                }
            ]
        });

        Assert.IsType<PartialViewResult>(result);
        Assert.False(controller.ModelState.IsValid);
        Assert.Contains(controller.ModelState, item => item.Key == "Lines[0].ExpenseTypeId"
            && item.Value?.Errors.Any(error => error.ErrorMessage.Contains("نوع مصرف را انتخاب یا وارد کنید.")) == true);
        Assert.Empty(await db.ExpenseTransactions.ToListAsync());
    }

    [Fact]
    public async Task SaveGroupExpenses_Rejects_Manual_Name_Longer_Than_Database_Limit()
    {
        await using var db = CreateDb();
        await SeedReferenceDataAsync(db);
        await SeedShipmentTransportGroupAsync(db);
        var controller = BuildController(db);

        var result = await controller.SaveGroupExpenses(new InventoryTransportGroupExpenseModalViewModel
        {
            GroupKey = "SHIP:1",
            Lines =
            [
                new InventoryTransportGroupExpenseModalRow
                {
                    ManualExpenseTypeName = new string('ا', 201),
                    AmountUsd = 55m,
                    PartyType = LoadingExpensePartyType.None
                }
            ]
        });

        Assert.IsType<PartialViewResult>(result);
        Assert.False(controller.ModelState.IsValid);
        Assert.Contains(controller.ModelState, item => item.Key == "Lines[0].ManualExpenseTypeName"
            && item.Value?.Errors.Any(error => error.ErrorMessage.Contains("200")) == true);
        Assert.Empty(await db.ExpenseTransactions.ToListAsync());
    }

    [Fact]
    public async Task ActiveDetails_Paginates_Expenses_And_Keeps_Total_Summary()
    {
        await using var db = CreateDb();
        await SeedReferenceDataAsync(db);
        db.Currencies.Add(new Currency { Id = 1, Code = "USD", Name = "US Dollar", Symbol = "$", IsActive = true });
        db.ExpenseTypes.Add(new ExpenseType { Id = 1, Code = "PORT", Name = "Port charges", Category = "Transport" });
        db.Shipments.Add(new Shipment
        {
            Id = 1,
            ShipmentCode = "KALUGA",
            DepartureDate = new DateTime(2026, 6, 1),
            QuantityMt = 100m
        });
        db.InventoryTransportLegs.Add(new InventoryTransportLeg
        {
            Id = 350,
            ShipmentId = 1,
            SourcePurchaseContractId = 1,
            ProductId = 1,
            SourceTerminalId = 1,
            TransportType = LoadingTransportType.Vessel,
            WagonNumber = "KALUGA",
            LoadedDate = new DateTime(2026, 6, 1),
            QuantityMt = 100m,
            Status = InventoryTransportLegStatus.Loaded,
            OutboundInventoryMovementId = 35
        });

        for (var i = 1; i <= 12; i++)
        {
            db.ExpenseTransactions.Add(new ExpenseTransaction
            {
                Id = i,
                ExpenseTypeId = 1,
                ContractId = 1,
                ShipmentId = 1,
                TransportLegId = 350,
                ExpenseDate = new DateTime(2026, 6, 1).AddDays(i),
                Amount = 10m,
                Currency = "USD",
                AmountUsd = 10m,
                Description = $"Expense {i}"
            });
        }

        await db.SaveChangesAsync();
        var controller = BuildController(db);

        var view = Assert.IsType<ViewResult>(await controller.Active());
        var model = Assert.Single(
            Assert.IsType<InventoryTransportFlowDashboardViewModel>(view.Model).Transports,
            t => t.GroupKey == "SHIP:1");
        Assert.Equal(12, model.ExpenseCount);
        Assert.Equal(120m, model.TotalExpenseUsd);
        Assert.Equal(12, model.Expenses.Count);
        Assert.Equal(1, model.ExpenseCurrentPage);
        Assert.Equal(1, model.ExpensePageCount);
        var allocation = Assert.Single(model.ContractAllocations);
        Assert.Equal(12, allocation.ExpenseCount);
        Assert.Equal(120m, allocation.ExpenseAmountUsd);
    }

    [Fact]
    public async Task CreateGroupReceipt_Unloads_MultiContract_Shipment_And_Splits_Shortage()
    {
        await using var db = CreateDb();
        await SeedReferenceDataAsync(db);
        db.Contracts.Add(new Contract
        {
            Id = 3,
            ContractNumber = "PUR-2",
            ContractType = ContractType.Purchase,
            CompanyId = 1,
            SupplierId = 1,
            ProductId = 1,
            ContractDate = new DateTime(2026, 5, 2),
            QuantityMt = 200m,
            PricingMethod = PricingMethod.Fixed,
            UnitPriceUsd = 110m
        });
        db.Shipments.Add(new Shipment
        {
            Id = 1,
            ShipmentCode = "KALUGA",
            DepartureDate = new DateTime(2026, 6, 1),
            QuantityMt = 100m
        });
        db.InventoryTransportLegs.AddRange(
            new InventoryTransportLeg
            {
                Id = 360,
                ShipmentId = 1,
                SourcePurchaseContractId = 1,
                ProductId = 1,
                SourceTerminalId = 1,
                DestinationTerminalId = 2,
                DestinationStorageTankId = 2,
                TransportType = LoadingTransportType.Vessel,
                WagonNumber = "KALUGA",
                LoadedDate = new DateTime(2026, 6, 1),
                QuantityMt = 40m,
                Status = InventoryTransportLegStatus.Loaded,
                OutboundInventoryMovementId = 36
            },
            new InventoryTransportLeg
            {
                Id = 361,
                ShipmentId = 1,
                SourcePurchaseContractId = 3,
                ProductId = 1,
                SourceTerminalId = 1,
                DestinationTerminalId = 2,
                DestinationStorageTankId = 2,
                TransportType = LoadingTransportType.Vessel,
                WagonNumber = "KALUGA",
                LoadedDate = new DateTime(2026, 6, 1),
                QuantityMt = 60m,
                Status = InventoryTransportLegStatus.InTransit,
                OutboundInventoryMovementId = 37
            });
        await db.SaveChangesAsync();
        var controller = BuildController(db);

        var result = await controller.CreateGroupReceipt(new InventoryTransportGroupReceiptCreateViewModel
        {
            GroupKey = "SHIP:1",
            ReceiptDate = new DateTime(2026, 6, 5),
            TotalReceivedQuantityMt = 97m,
            TotalShortageQuantityMt = 3m,
            AllowanceMt = 1m,
            DestinationTerminalId = 2,
            DestinationStorageTankId = 2,
            Notes = "Vessel discharge"
        });

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Journey", redirect.ActionName);

        var receipts = await db.InventoryTransportReceipts
            .OrderBy(r => r.InventoryTransportLegId)
            .ToListAsync();
        Assert.Collection(
            receipts,
            first =>
            {
                Assert.Equal(360, first.InventoryTransportLegId);
                Assert.Equal(38.8m, first.ReceivedQuantityMt);
                Assert.Equal(1.2m, first.ShortageQuantityMt);
                Assert.Equal(0.4m, first.AllowanceMt);
                Assert.Equal(0.8m, first.ChargeableShortageMt);
                Assert.NotNull(first.InventoryMovementId);
            },
            second =>
            {
                Assert.Equal(361, second.InventoryTransportLegId);
                Assert.Equal(58.2m, second.ReceivedQuantityMt);
                Assert.Equal(1.8m, second.ShortageQuantityMt);
                Assert.Equal(0.6m, second.AllowanceMt);
                Assert.Equal(1.2m, second.ChargeableShortageMt);
                Assert.NotNull(second.InventoryMovementId);
            });

        var movements = await db.InventoryMovements
            .Where(m => m.ReferenceDocument != null && m.ReferenceDocument.StartsWith("TRANSPORT-RECEIPT:"))
            .OrderBy(m => m.ContractId)
            .ToListAsync();
        Assert.Collection(
            movements,
            first =>
            {
                Assert.Equal(1, first.ContractId);
                Assert.Equal(38.8m, first.QuantityMt);
                Assert.Equal(MovementDirection.In, first.Direction);
                Assert.Equal(2, first.TerminalId);
                Assert.Equal(2, first.StorageTankId);
            },
            second =>
            {
                Assert.Equal(3, second.ContractId);
                Assert.Equal(58.2m, second.QuantityMt);
                Assert.Equal(MovementDirection.In, second.Direction);
                Assert.Equal(2, second.TerminalId);
                Assert.Equal(2, second.StorageTankId);
            });

        var losses = await db.LossEvents
            .OrderBy(l => l.ContractId)
            .ToListAsync();
        Assert.Collection(
            losses,
            first =>
            {
                Assert.Equal(1, first.ContractId);
                Assert.Equal(360, first.TransportLegId);
                Assert.Equal(1.2m, first.DifferenceQuantityMt);
                Assert.Equal(0.4m, first.AllowableLossMt);
                Assert.Equal(0.8m, first.ChargeableLossMt);
                Assert.Equal(LossEventStage.ReceiptShortage, first.Stage);
            },
            second =>
            {
                Assert.Equal(3, second.ContractId);
                Assert.Equal(361, second.TransportLegId);
                Assert.Equal(1.8m, second.DifferenceQuantityMt);
                Assert.Equal(0.6m, second.AllowableLossMt);
                Assert.Equal(1.2m, second.ChargeableLossMt);
                Assert.Equal(LossEventStage.ReceiptShortage, second.Stage);
            });

        Assert.All(await db.InventoryTransportLegs.ToListAsync(), leg => Assert.Equal(InventoryTransportLegStatus.Received, leg.Status));
    }

    [Fact]
    public async Task CreateGroupReceipt_Get_Uses_Shipment_Available_Quantity_And_Rejects_OverReceipt()
    {
        await using var db = CreateDb();
        await SeedReferenceDataAsync(db);
        db.Shipments.Add(new Shipment
        {
            Id = 1,
            ShipmentCode = "BNK-SOLVEX-JAN",
            DepartureDate = new DateTime(2026, 6, 1),
            QuantityMt = 4_144m
        });
        db.InventoryTransportLegs.Add(new InventoryTransportLeg
        {
            Id = 362,
            ShipmentId = 1,
            SourcePurchaseContractId = 1,
            ProductId = 1,
            SourceTerminalId = 1,
            DestinationTerminalId = 2,
            DestinationStorageTankId = 2,
            TransportType = LoadingTransportType.Vessel,
            LoadedDate = new DateTime(2026, 6, 1),
            QuantityMt = 4_144m,
            Status = InventoryTransportLegStatus.InTransit,
            OutboundInventoryMovementId = 38
        });
        db.LossEvents.Add(new LossEvent
        {
            ShipmentId = 1,
            ProductId = 1,
            Stage = LossEventStage.TransitLoss,
            EventDate = new DateTime(2026, 6, 3),
            ExpectedQuantityMt = 4_144m,
            ActualQuantityMt = 4_110m,
            DifferenceQuantityMt = 34m,
            ChargeableLossMt = 34m
        });
        await db.SaveChangesAsync();
        var controller = BuildController(db);

        var getResult = await controller.CreateGroupReceipt("SHIP:1");

        var getView = Assert.IsType<ViewResult>(getResult);
        var getModel = Assert.IsType<InventoryTransportGroupReceiptCreateViewModel>(getView.Model);
        Assert.Equal(4_144m, getModel.TotalLoadedQuantityMt);
        Assert.Equal(34m, getModel.RegisteredShortageQuantityMt);
        Assert.Equal(4_110m, getModel.AvailableQuantityMt);
        Assert.Equal(4_110m, getModel.TotalReceivedQuantityMt);

        var receiptCountBefore = await db.InventoryTransportReceipts.CountAsync();
        var movementCountBefore = await db.InventoryMovements.CountAsync();
        var postResult = await controller.CreateGroupReceipt(new InventoryTransportGroupReceiptCreateViewModel
        {
            GroupKey = "SHIP:1",
            ReceiptDate = new DateTime(2026, 6, 5),
            TotalReceivedQuantityMt = 4_111m,
            DestinationTerminalId = 2,
            DestinationStorageTankId = 2
        });

        Assert.IsType<ViewResult>(postResult);
        Assert.False(controller.ModelState.IsValid);
        Assert.Contains(
            controller.ModelState.Values.SelectMany(value => value.Errors),
            error => error.ErrorMessage.Contains("4,110", StringComparison.OrdinalIgnoreCase)
                || error.ErrorMessage.Contains("4110", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(receiptCountBefore, await db.InventoryTransportReceipts.CountAsync());
        Assert.Equal(movementCountBefore, await db.InventoryMovements.CountAsync());

        var saveController = BuildController(db);
        var saveResult = await saveController.CreateGroupReceipt(new InventoryTransportGroupReceiptCreateViewModel
        {
            GroupKey = "SHIP:1",
            ReceiptDate = new DateTime(2026, 6, 5),
            TotalReceivedQuantityMt = 4_110m,
            DestinationTerminalId = 2,
            DestinationStorageTankId = 2
        });

        Assert.IsType<RedirectToActionResult>(saveResult);
        var savedReceipt = Assert.Single(await db.InventoryTransportReceipts.ToListAsync());
        Assert.Equal(4_110m, savedReceipt.ReceivedQuantityMt);
        Assert.Equal(0m, savedReceipt.ShortageQuantityMt);
        var savedMovement = Assert.Single(await db.InventoryMovements
            .Where(m => m.ReferenceDocument != null && m.ReferenceDocument.StartsWith("TRANSPORT-RECEIPT:"))
            .ToListAsync());
        Assert.Equal(4_110m, savedMovement.QuantityMt);
        Assert.Single(await db.LossEvents.ToListAsync());

        var afterController = BuildController(db);
        var afterResult = await afterController.CreateGroupReceipt("SHIP:1");
        var afterView = Assert.IsType<ViewResult>(afterResult);
        var afterModel = Assert.IsType<InventoryTransportGroupReceiptCreateViewModel>(afterView.Model);
        Assert.Equal(0m, afterModel.AvailableQuantityMt);
        Assert.Equal(4_110m, afterModel.PreviousReceiptQuantityMt);
    }

    [Fact]
    public async Task MarkLoaded_Creates_InventoryMovement_Out()
    {
        await using var db = CreateDb();
        await SeedReferenceDataAsync(db);
        await SeedSourceStockAsync(db, quantityMt: 100m);
        var leg = await SeedDraftLegAsync(db, quantityMt: 30m);
        var controller = BuildController(db);

        var result = await controller.MarkLoaded(leg.Id);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Details", redirect.ActionName);

        var reloaded = await db.InventoryTransportLegs.SingleAsync(l => l.Id == leg.Id);
        Assert.Equal(InventoryTransportLegStatus.Loaded, reloaded.Status);
        Assert.NotNull(reloaded.OutboundInventoryMovementId);

        var movement = await db.InventoryMovements.SingleAsync(m => m.ReferenceDocument == $"TRANSPORT-LEG:{leg.Id}");
        Assert.Equal(MovementDirection.Out, movement.Direction);
        Assert.Equal(30m, movement.QuantityMt);
        Assert.Equal(1, movement.ProductId);
        Assert.Equal(1, movement.ContractId);
        Assert.Equal(1, movement.TerminalId);
        Assert.Equal(1, movement.StorageTankId);
    }

    [Fact]
    public async Task Details_Shows_ReadOnly_Expenses_Linked_To_TransportLeg()
    {
        await using var db = CreateDb();
        await SeedReferenceDataAsync(db);
        var leg = await SeedDraftLegAsync(db, quantityMt: 30m);
        db.ExpenseTypes.Add(new ExpenseType { Id = 1, Code = "WGN", Name = "Wagon expense" });
        db.ExpenseTransactions.Add(new ExpenseTransaction
        {
            Id = 1,
            ExpenseTypeId = 1,
            ContractId = 1,
            TransportLegId = leg.Id,
            ExpenseDate = new DateTime(2026, 5, 3),
            Amount = 225m,
            Currency = "USD",
            AmountUsd = 225m,
            Description = "Wagon handling"
        });
        await db.SaveChangesAsync();
        var controller = BuildController(db);

        var result = await controller.Details(leg.Id);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<InventoryTransportLegDetailsViewModel>(view.Model);
        var expense = Assert.Single(model.Expenses);
        Assert.Equal(1, expense.Id);
        Assert.Equal("Wagon expense", expense.ExpenseTypeName);
        Assert.Equal(225m, expense.AmountUsd);
    }

    [Fact]
    public async Task Details_Computes_Pnl_For_Transported_Quantity()
    {
        await using var db = CreateDb();
        await SeedReferenceDataAsync(db);
        var leg = await SeedDraftLegAsync(db, quantityMt: 10m);
        leg.PurchaseUnitCostUsd = 100m;
        leg.Status = InventoryTransportLegStatus.Received;
        db.SalesTransactions.Add(new SalesTransaction
        {
            Id = 50,
            CompanyId = 1,
            CustomerId = 1,
            ProductId = 1,
            SaleStage = SaleStage.InTransit,
            InvoiceNumber = "INV-TR-1",
            SaleDate = new DateTime(2026, 5, 5),
            QuantityMt = 9m,
            UnitPriceUsd = 200m,
            TotalUsd = 1_800m
        });
        db.InventoryTransportReceipts.Add(new InventoryTransportReceipt
        {
            Id = 60,
            InventoryTransportLegId = leg.Id,
            ReceiptDate = new DateTime(2026, 5, 5),
            ReceiptDestination = InventoryTransportReceiptDestination.DirectSale,
            ReceivedQuantityMt = 9m,
            ShortageQuantityMt = 1m,
            ChargeableShortageMt = 1m,
            FreightCostUsd = 50m,
            ShortageChargeUsd = 10m,
            FreightPayableUsd = 40m,
            SalesTransactionId = 50
        });
        db.ExpenseTypes.Add(new ExpenseType { Id = 1, Code = "OPS", Name = "Operations" });
        db.ExpenseTransactions.Add(new ExpenseTransaction
        {
            Id = 70,
            ExpenseTypeId = 1,
            ContractId = 1,
            TransportLegId = leg.Id,
            ExpenseDate = new DateTime(2026, 5, 5),
            Amount = 100m,
            Currency = "USD",
            AmountUsd = 100m
        });
        db.CustomsDeclarations.Add(new CustomsDeclaration
        {
            Id = 80,
            TransportLegId = leg.Id,
            DeclarationDate = new DateTime(2026, 5, 5),
            TotalUsd = 60m
        });
        await db.SaveChangesAsync();
        var controller = BuildController(db);

        var result = await controller.Details(leg.Id);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<InventoryTransportLegDetailsViewModel>(view.Model);
        Assert.Equal(100m, model.Pnl.PurchaseUnitCostUsd);
        Assert.Equal(1_000m, model.Pnl.PurchaseCostUsd);
        Assert.Equal(9m, model.Pnl.SoldQuantityMt);
        Assert.Equal(1_800m, model.Pnl.SalesUsd);
        Assert.Equal(200m, model.Pnl.OperationalExpensesUsd);
        Assert.Equal(1_200m, model.Pnl.TotalCostUsd);
        Assert.Equal(600m, model.Pnl.GrossMarginUsd);
        Assert.Equal(1m, model.Pnl.LossQuantityMt);
        Assert.Equal(100m, model.Pnl.LossCostUsd);
        Assert.Equal(0m, model.Pnl.UnsoldQuantityMt);
        Assert.Equal(0m, model.RemainingQuantityMt);
        Assert.Single(model.Pnl.Sales);
    }

    [Fact]
    public async Task Edit_Draft_Updates_TransportLeg_Without_Creating_InventoryMovement()
    {
        await using var db = CreateDb();
        await SeedReferenceDataAsync(db);
        var leg = await SeedDraftLegAsync(db, quantityMt: 30m);
        var controller = BuildController(db);

        var result = await controller.Edit(leg.Id, new InventoryTransportLegCreateViewModel
        {
            Id = leg.Id,
            SourcePurchaseContractId = 1,
            ProductId = 1,
            SourceTerminalId = 1,
            SourceStorageTankId = 1,
            DestinationTerminalId = 2,
            DestinationStorageTankId = 2,
            DestinationLocationId = 1,
            TransportType = LoadingTransportType.Wagon,
            WagonNumber = "WGN-EDIT",
            RwbNo = "RWB-EDIT",
            BillOfLadingNumber = "BL-EDIT",
            RouteDescription = "Edited route",
            LoadedDate = new DateTime(2026, 5, 6),
            ExpectedArrivalDate = new DateTime(2026, 5, 8),
            QuantityMt = 25m,
            ChargeableQuantityMt = 24.5m,
            Notes = "Edited draft"
        });

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Details", redirect.ActionName);

        var updated = await db.InventoryTransportLegs.SingleAsync(l => l.Id == leg.Id);
        Assert.Equal(InventoryTransportLegStatus.Draft, updated.Status);
        Assert.Null(updated.OutboundInventoryMovementId);
        Assert.Equal("WGN-EDIT", updated.WagonNumber);
        Assert.Equal("RWB-EDIT", updated.RwbNo);
        Assert.Equal("BL-EDIT", updated.BillOfLadingNumber);
        Assert.Equal("Edited route", updated.RouteDescription);
        Assert.Equal(new DateTime(2026, 5, 6), updated.LoadedDate);
        Assert.Equal(25m, updated.QuantityMt);
        Assert.Equal(24.5m, updated.ChargeableQuantityMt);
        Assert.Equal("Edited draft", updated.Notes);
        Assert.Empty(await db.InventoryMovements.ToListAsync());
    }

    [Fact]
    public async Task Edit_Rejects_Loaded_TransportLeg()
    {
        await using var db = CreateDb();
        await SeedReferenceDataAsync(db);
        var leg = await SeedDraftLegAsync(db, quantityMt: 30m);
        db.InventoryMovements.Add(new InventoryMovement
        {
            Id = 10,
            ProductId = 1,
            ContractId = 1,
            TerminalId = 1,
            StorageTankId = 1,
            Direction = MovementDirection.Out,
            MovementDate = leg.LoadedDate,
            QuantityMt = leg.QuantityMt,
            ReferenceDocument = $"TRANSPORT-LEG:{leg.Id}"
        });
        leg.Status = InventoryTransportLegStatus.Loaded;
        leg.OutboundInventoryMovementId = 10;
        await db.SaveChangesAsync();
        var controller = BuildController(db);

        var result = await controller.Edit(leg.Id, new InventoryTransportLegCreateViewModel
        {
            Id = leg.Id,
            SourcePurchaseContractId = 1,
            ProductId = 1,
            SourceTerminalId = 1,
            SourceStorageTankId = 1,
            TransportType = LoadingTransportType.Wagon,
            WagonNumber = "SHOULD-NOT-SAVE",
            LoadedDate = new DateTime(2026, 5, 9),
            QuantityMt = 10m
        });

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Details", redirect.ActionName);
        var unchanged = await db.InventoryTransportLegs.SingleAsync(l => l.Id == leg.Id);
        Assert.Equal("WGN-001", unchanged.WagonNumber);
        Assert.Equal(30m, unchanged.QuantityMt);
        Assert.Equal(InventoryTransportLegStatus.Loaded, unchanged.Status);
    }

    [Fact]
    public async Task Details_Shows_ReadOnly_CustomsDeclarations_Linked_To_TransportLeg()
    {
        await using var db = CreateDb();
        await SeedReferenceDataAsync(db);
        var leg = await SeedDraftLegAsync(db, quantityMt: 30m);
        db.CustomsDeclarations.Add(new CustomsDeclaration
        {
            Id = 1,
            TransportLegId = leg.Id,
            DeclarationDate = new DateTime(2026, 5, 4),
            WagonOrTruckNumber = "WGN-C",
            TotalAfn = 70_000m,
            TotalUsd = 1_000m
        });
        await db.SaveChangesAsync();
        var controller = BuildController(db);

        var result = await controller.Details(leg.Id);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<InventoryTransportLegDetailsViewModel>(view.Model);
        var customs = Assert.Single(model.CustomsDeclarations);
        Assert.Equal(1, customs.Id);
        Assert.Equal("WGN-C", customs.WagonOrTruckNumber);
        Assert.Equal(1_000m, customs.TotalUsd);
    }

    [Fact]
    public async Task Details_Shows_ReadOnly_Losses_Linked_To_TransportLeg()
    {
        await using var db = CreateDb();
        await SeedReferenceDataAsync(db);
        var leg = await SeedDraftLegAsync(db, quantityMt: 30m);
        db.LossEvents.Add(new LossEvent
        {
            Id = 1,
            TransportLegId = leg.Id,
            ContractId = 1,
            ProductId = 1,
            Stage = LossEventStage.ReceiptShortage,
            EventDate = new DateTime(2026, 5, 5),
            ExpectedQuantityMt = 30m,
            ActualQuantityMt = 28m,
            DifferenceQuantityMt = 2m,
            ChargeableLossMt = 2m,
            Reference = "TRANSPORT-RECEIPT:1"
        });
        await db.SaveChangesAsync();
        var controller = BuildController(db);

        var result = await controller.Details(leg.Id);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<InventoryTransportLegDetailsViewModel>(view.Model);
        var loss = Assert.Single(model.Losses);
        Assert.Equal(1, loss.Id);
        Assert.Equal(LossEventStage.ReceiptShortage.ToString(), loss.StageName);
        Assert.Equal(2m, loss.ChargeableLossMt);
        Assert.Equal("TRANSPORT-RECEIPT:1", loss.Reference);
        Assert.Equal(28m, model.RemainingQuantityMt);
    }

    [Fact]
    public async Task MarkLoaded_Decreases_Source_Stock()
    {
        await using var db = CreateDb();
        await SeedReferenceDataAsync(db);
        await SeedSourceStockAsync(db, quantityMt: 100m);
        var leg = await SeedDraftLegAsync(db, quantityMt: 30m);
        var stock = new StockService(db);
        var before = await stock.GetFreeQuantityMtAsync(1, terminalId: 1, contractId: 1, storageTankId: 1);
        var controller = BuildController(db);

        await controller.MarkLoaded(leg.Id);

        var after = await stock.GetFreeQuantityMtAsync(1, terminalId: 1, contractId: 1, storageTankId: 1);
        Assert.Equal(100m, before);
        Assert.Equal(70m, after);
    }

    [Fact]
    public async Task MarkLoaded_Uses_Current_Source_Stock_For_Backdated_Transport()
    {
        await using var db = CreateDb();
        await SeedReferenceDataAsync(db);
        db.InventoryMovements.Add(new InventoryMovement
        {
            ProductId = 1,
            ContractId = 1,
            TerminalId = 1,
            StorageTankId = 1,
            Direction = MovementDirection.In,
            MovementDate = new DateTime(2026, 5, 5),
            QuantityMt = 100m,
            ReferenceDocument = "LATE-RECEIPT-1"
        });
        var leg = await SeedDraftLegAsync(db, quantityMt: 30m);
        var controller = BuildController(db);

        await controller.MarkLoaded(leg.Id);

        var updatedLeg = await db.InventoryTransportLegs.SingleAsync(l => l.Id == leg.Id);
        Assert.Equal(InventoryTransportLegStatus.Loaded, updatedLeg.Status);
        Assert.NotNull(updatedLeg.OutboundInventoryMovementId);

        var stock = new StockService(db);
        Assert.Equal(70m, await stock.GetFreeQuantityMtAsync(1, terminalId: 1, contractId: 1, storageTankId: 1));
    }

    [Fact]
    public async Task MarkGroupLoaded_Loads_MultiContract_Vessel_When_SourceStockUsesReceiptContractFallback()
    {
        await using var db = CreateDb();
        await SeedReferenceDataAsync(db);
        db.Contracts.Add(new Contract
        {
            Id = 3,
            ContractNumber = "PUR-2",
            ContractType = ContractType.Purchase,
            CompanyId = 1,
            SupplierId = 1,
            ProductId = 1,
            ContractDate = new DateTime(2026, 5, 2),
            QuantityMt = 200m,
            PricingMethod = PricingMethod.Fixed,
            UnitPriceUsd = 110m
        });
        await db.SaveChangesAsync();
        await SeedLegacyReceiptBackedStockAsync(db, contractId: 1, loadingId: 101, receiptId: 201, movementId: 301, quantityMt: 100m);
        await SeedLegacyReceiptBackedStockAsync(db, contractId: 3, loadingId: 103, receiptId: 203, movementId: 303, quantityMt: 100m);
        db.InventoryTransportLegs.AddRange(
            new InventoryTransportLeg
            {
                Id = 401,
                SourcePurchaseContractId = 1,
                ProductId = 1,
                SourceTerminalId = 1,
                SourceStorageTankId = 1,
                DestinationTerminalId = 2,
                TransportType = LoadingTransportType.Vessel,
                WagonNumber = "Vessel Alpha",
                BillOfLadingNumber = "VES-100",
                LoadedDate = new DateTime(2026, 5, 10),
                QuantityMt = 40m,
                Status = InventoryTransportLegStatus.Draft
            },
            new InventoryTransportLeg
            {
                Id = 402,
                SourcePurchaseContractId = 3,
                ProductId = 1,
                SourceTerminalId = 1,
                SourceStorageTankId = 1,
                DestinationTerminalId = 2,
                TransportType = LoadingTransportType.Vessel,
                WagonNumber = "Vessel Alpha",
                BillOfLadingNumber = "VES-100",
                LoadedDate = new DateTime(2026, 5, 10),
                QuantityMt = 60m,
                Status = InventoryTransportLegStatus.Draft
            });
        await db.SaveChangesAsync();
        var controller = BuildController(db);

        var result = await controller.MarkGroupLoaded("VEH:1:VESSEL ALPHA");

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Journey", redirect.ActionName);

        var legs = await db.InventoryTransportLegs
            .Where(l => l.Id == 401 || l.Id == 402)
            .OrderBy(l => l.Id)
            .ToListAsync();
        Assert.All(legs, leg => Assert.Equal(InventoryTransportLegStatus.Loaded, leg.Status));
        Assert.All(legs, leg => Assert.NotNull(leg.OutboundInventoryMovementId));

        var outboundMovements = await db.InventoryMovements
            .Where(m => m.ReferenceDocument == "TRANSPORT-LEG:401" || m.ReferenceDocument == "TRANSPORT-LEG:402")
            .OrderBy(m => m.ReferenceDocument)
            .ToListAsync();
        Assert.Collection(
            outboundMovements,
            first =>
            {
                Assert.Equal(1, first.ContractId);
                Assert.Equal(40m, first.QuantityMt);
                Assert.Equal(1, first.StorageTankId);
            },
            second =>
            {
                Assert.Equal(3, second.ContractId);
                Assert.Equal(60m, second.QuantityMt);
                Assert.Equal(1, second.StorageTankId);
            });

        var stock = new StockService(db);
        Assert.Equal(60m, await stock.GetFreeQuantityMtAsync(1, terminalId: 1, contractId: 1, storageTankId: 1));
        Assert.Equal(40m, await stock.GetFreeQuantityMtAsync(1, terminalId: 1, contractId: 3, storageTankId: 1));
    }

    [Fact]
    public async Task MarkLoaded_Rejects_Insufficient_Stock()
    {
        await using var db = CreateDb();
        await SeedReferenceDataAsync(db);
        await SeedSourceStockAsync(db, quantityMt: 20m);
        var leg = await SeedDraftLegAsync(db, quantityMt: 30m);
        var controller = BuildController(db);

        var result = await controller.MarkLoaded(leg.Id);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Details", redirect.ActionName);
        Assert.Equal(InventoryTransportLegStatus.Draft, (await db.InventoryTransportLegs.SingleAsync(l => l.Id == leg.Id)).Status);
        Assert.DoesNotContain(await db.InventoryMovements.ToListAsync(), m => m.ReferenceDocument == $"TRANSPORT-LEG:{leg.Id}");
    }

    [Fact]
    public async Task MarkLoaded_Does_Not_Change_PurchaseAggregation()
    {
        await using var db = CreateDb();
        await SeedReferenceDataAsync(db);
        await SeedSourceStockAsync(db, quantityMt: 100m);
        await SeedPurchaseLoadingAsync(db);
        var leg = await SeedDraftLegAsync(db, quantityMt: 30m);
        var aggregation = new PurchaseAggregationService(db);
        var before = await aggregation.AggregateForContractAsync(1, contractFinalPriceUsd: null);
        var controller = BuildController(db);

        await controller.MarkLoaded(leg.Id);

        var after = await aggregation.AggregateForContractAsync(1, contractFinalPriceUsd: null);
        Assert.Equal(before.TotalLoadedQuantityMt, after.TotalLoadedQuantityMt);
        Assert.Equal(before.TraceablePurchaseCostUsd, after.TraceablePurchaseCostUsd);
        Assert.Equal(10m, after.TotalLoadedQuantityMt);
        Assert.Equal(1_000m, after.TraceablePurchaseCostUsd);
    }

    [Fact]
    public async Task ContractPnl_Does_Not_Count_TransportLeg_As_Purchase_After_MarkLoaded()
    {
        await using var db = CreateDb();
        await SeedReferenceDataAsync(db);
        await SeedSourceStockAsync(db, quantityMt: 100m);
        await SeedPurchaseLoadingAsync(db);
        var leg = await SeedDraftLegAsync(db, quantityMt: 30m);
        await BuildController(db).MarkLoaded(leg.Id);

        var controller = new ReportsController(db);
        var result = await controller.ContractPnl(new ManagementReportFilterViewModel { ContractId = 1 });

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<ContractPnlReportViewModel>(view.Model);
        var row = Assert.Single(model.PurchaseRows);
        Assert.Equal(10m, row.TotalLoadedMt);
        Assert.Equal(1_000m, row.PurchaseValueUsd);
    }

    [Fact]
    public async Task Reconciliation_LoadedByContract_Does_Not_Count_TransportLeg_After_MarkLoaded()
    {
        await using var db = CreateDb();
        await SeedReferenceDataAsync(db);
        await SeedSourceStockAsync(db, quantityMt: 100m);
        await SeedPurchaseLoadingAsync(db);
        var leg = await SeedDraftLegAsync(db, quantityMt: 30m);
        await BuildController(db).MarkLoaded(leg.Id);

        var controller = new ReconciliationController(db);
        var result = await controller.OpenContracts();

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<OpenContractsViewModel>(view.Model);
        var row = Assert.Single(model.Rows, r => r.ContractId == 1);
        Assert.Equal(10m, row.LoadedQuantityMt);
    }

    [Fact]
    public async Task MarkLoaded_Cannot_Run_Twice()
    {
        await using var db = CreateDb();
        await SeedReferenceDataAsync(db);
        await SeedSourceStockAsync(db, quantityMt: 100m);
        var leg = await SeedDraftLegAsync(db, quantityMt: 30m);
        var controller = BuildController(db);

        await controller.MarkLoaded(leg.Id);
        await controller.MarkLoaded(leg.Id);

        Assert.Single(await db.InventoryMovements.Where(m => m.ReferenceDocument == $"TRANSPORT-LEG:{leg.Id}").ToListAsync());
    }

    [Fact]
    public async Task Edit_Draft_Can_Clear_Destination_Fields()
    {
        await using var db = CreateDb();
        await SeedReferenceDataAsync(db);
        var leg = await SeedDraftLegAsync(db, quantityMt: 30m);
        var controller = BuildController(db);

        var result = await controller.Edit(leg.Id, new InventoryTransportLegCreateViewModel
        {
            Id = leg.Id,
            SourcePurchaseContractId = 1,
            ProductId = 1,
            SourceTerminalId = 1,
            SourceStorageTankId = 1,
            DestinationTerminalId = null,
            DestinationStorageTankId = null,
            DestinationLocationId = null,
            TransportType = LoadingTransportType.Vessel,
            WagonNumber = "VESSEL-A",
            RwbNo = "VOY-100",
            BillOfLadingNumber = "BL-100",
            RouteDescription = "Sea route",
            LoadedDate = new DateTime(2026, 5, 6),
            QuantityMt = 25m
        });

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Details", redirect.ActionName);

        var updated = await db.InventoryTransportLegs.SingleAsync(l => l.Id == leg.Id);
        Assert.Null(updated.DestinationTerminalId);
        Assert.Null(updated.DestinationStorageTankId);
        Assert.Null(updated.DestinationLocationId);
        Assert.Equal(LoadingTransportType.Vessel, updated.TransportType);
        Assert.Equal("VESSEL-A", updated.WagonNumber);
    }

    [Fact]
    public async Task Legacy_Create_Routes_Redirect_To_Unified_Transport_Entry()
    {
        await using var db = CreateDb();
        var controller = BuildController(db);

        var create = Assert.IsType<RedirectToActionResult>(controller.Create(shipmentId: 7));
        var shipment = Assert.IsType<RedirectToActionResult>(controller.CreateForShipment(shipmentId: 7));
        var batch = Assert.IsType<RedirectToActionResult>(controller.CreateBatch(shipmentId: 7));

        Assert.Equal("Create", create.ActionName);
        Assert.Equal("Transports", create.ControllerName);
        Assert.Equal("CreateFromInventory", shipment.ActionName);
        Assert.Equal("CreateFromInventory", batch.ActionName);
        Assert.Equal((int)TransportStartSourceKind.Inventory, create.RouteValues!["sourceKind"]);
        Assert.Equal(7, shipment.RouteValues!["shipmentId"]);
        Assert.Equal(7, batch.RouteValues!["shipmentId"]);
    }

    [Fact]
    public async Task CreateFromInventory_Post_Creates_Standalone_Operational_Asset_Transport()
    {
        await using var db = CreateDb();
        await SeedReferenceDataAsync(db);
        db.Drivers.Add(new Driver { Id = 1, FullName = "Ahmad", IsActive = true });
        db.OperationalAssets.Add(new OperationalAsset
        {
            Id = 1,
            AssetCode = "AS001",
            Name = "Company Truck",
            AssetType = OperationalAssetType.Truck,
            IsActive = true
        });
        var source = new InventoryMovement
        {
            Id = 100,
            ProductId = 1,
            ContractId = 1,
            TerminalId = 1,
            StorageTankId = 1,
            Direction = MovementDirection.In,
            MovementDate = new DateTime(2026, 7, 1),
            QuantityMt = 50m,
            ReferenceDocument = "TEST-RECEIPT"
        };
        db.InventoryMovements.Add(source);
        await db.SaveChangesAsync();
        var controller = BuildController(db);
        var model = new InventoryTransportFromInventoryViewModel
        {
            SourceTerminalId = 1,
            SourceStorageTankId = 1,
            ProductId = 1,
            TransportDate = new DateTime(2026, 7, 3),
            SubmissionMode = InventoryTransportSubmissionMode.Draft,
            Sources =
            [
                new() { SourceInventoryMovementId = source.Id, QuantityMt = 10m }
            ],
            Vehicles =
            [
                new()
                {
                    TransportType = LoadingTransportType.Truck,
                    DriverId = 1,
                    QuantityMt = 10m,
                    CapacityMt = 20m,
                    CarrierType = CarrierType.OperationalAsset,
                    OperationalAssetId = 1,
                    Allocations =
                    [
                        new() { SourceInventoryMovementId = source.Id, QuantityMt = 10m }
                    ]
                }
            ]
        };

        var result = await controller.CreateFromInventory(model, formToken: "controller-create-token");

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Details", redirect.ActionName);
        var batch = await db.InventoryTransportBatches.Include(b => b.Legs).SingleAsync();
        var leg = Assert.Single(batch.Legs);
        Assert.Equal(InventoryTransportBatchStatus.Draft, batch.Status);
        Assert.Null(leg.TruckId);
        Assert.Equal(1, leg.OperationalAssetId);
        Assert.Equal(20m, leg.CapacityMt);
        Assert.Equal("AS001", leg.WagonNumber);
    }

    [Fact]
    public async Task CreateFromInventory_Post_ModelState_Vehicle_Error_Reopens_Step_Three()
    {
        await using var db = CreateDb();
        var controller = BuildController(db);
        controller.ModelState.AddModelError("Vehicles[0].TruckId", "موتر معتبر نیست.");
        var model = new InventoryTransportFromInventoryViewModel
        {
            ActiveStep = 4,
            Vehicles = [new InventoryTransportVehicleInput()]
        };

        var result = await controller.CreateFromInventory(model, formToken: "validation-token");

        var view = Assert.IsType<ViewResult>(result);
        var returnedModel = Assert.IsType<InventoryTransportFromInventoryViewModel>(view.Model);
        Assert.Equal(3, returnedModel.ActiveStep);
        Assert.True(controller.ModelState.ContainsKey("Vehicles[0].TruckId"));
        Assert.Empty(await db.InventoryTransportBatches.ToListAsync());
    }

    [Fact]
    public async Task CreateFromInventory_Post_Missing_Form_Token_Reopens_Step_Four_Without_Saving()
    {
        await using var db = CreateDb();
        var controller = BuildController(db);
        var model = new InventoryTransportFromInventoryViewModel
        {
            ActiveStep = 4,
            Vehicles = [new InventoryTransportVehicleInput()]
        };

        var result = await controller.CreateFromInventory(model, formToken: null);

        var view = Assert.IsType<ViewResult>(result);
        var returnedModel = Assert.IsType<InventoryTransportFromInventoryViewModel>(view.Model);
        Assert.Equal(4, returnedModel.ActiveStep);
        Assert.Contains(
            controller.ModelState[FormTokenHtmlHelper.FieldName]!.Errors,
            error => error.ErrorMessage.Contains("توکن فورم", StringComparison.Ordinal));
        Assert.Empty(await db.InventoryTransportBatches.ToListAsync());
    }

    [Fact]
    public async Task Edit_Batch_Leg_Redirects_To_Journey()
    {
        await using var db = CreateDb();
        db.InventoryTransportBatches.Add(new InventoryTransportBatch
        {
            Id = 10,
            BatchNumber = "ITB-10",
            TransportGroupKey = "ITG:10",
            ProductId = 1,
            SourceTerminalId = 1,
            SourceStorageTankId = 1,
            TransportDate = new DateTime(2026, 7, 3),
            Status = InventoryTransportBatchStatus.Draft
        });
        db.InventoryTransportLegs.Add(new InventoryTransportLeg
        {
            Id = 11,
            InventoryTransportBatchId = 10,
            TransportGroupKey = "ITG:10",
            ProductId = 1,
            SourceTerminalId = 1,
            SourceStorageTankId = 1,
            TransportType = LoadingTransportType.Truck,
            LoadedDate = new DateTime(2026, 7, 3),
            QuantityMt = 10m,
            Status = InventoryTransportLegStatus.Draft
        });
        await db.SaveChangesAsync();

        var result = await BuildController(db).Edit(11);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Journey", redirect.ActionName);
        Assert.Equal("ITG:10", redirect.RouteValues!["groupKey"]);
    }

    // ───────── فروش گروهی مستقیم + تسویه: کسر از کرایه / بدهی جداگانه ─────────

    [Fact]
    public async Task GroupOperation_DirectSale_DeductFromFreight_Reduces_Freight_And_Posts_No_Separate_Debt()
    {
        await using var db = CreateDb();
        await SeedReferenceDataAsync(db);
        db.ServiceProviders.Add(new ServiceProvider { Id = 1, Code = "SP1", Name = "Carrier A", IsActive = true });
        var leg = await SeedGroupTruckLegAsync(db, id: 50, qty: 30m);
        var controller = BuildController(db);

        var model = BuildSaleGroupModel(leg.Id, received: 28m, shortage: 2m, allowance: 0m,
            deduct: true, debt: false, invoice: "G-DED-1",
            freightRate: 10m, shortageRate: 50m, serviceProviderId: 1, operationalAssetId: null);

        var result = await controller.CreateGroupOperation(model);

        Assert.IsNotType<ViewResult>(result); // redirect on success
        var receipt = await db.InventoryTransportReceipts.SingleAsync();
        Assert.Equal(300m, receipt.FreightCostUsd);       // gross = 30 × 10
        Assert.Equal(100m, receipt.ShortageChargeUsd);    // chargeable 2 × 50
        Assert.Equal(200m, receipt.FreightPayableUsd);    // gross − shortage
        var expense = await db.ExpenseTransactions.SingleAsync();
        Assert.Equal(200m, expense.AmountUsd);
        Assert.DoesNotContain(await db.LedgerEntries.ToListAsync(), l => l.SourceType == "ShortageCharge");
        Assert.DoesNotContain(await db.InventoryMovements.ToListAsync(),
            m => m.Direction == MovementDirection.In && m.ReferenceDocument != null && m.ReferenceDocument.StartsWith("TRANSPORT-RECEIPT:"));
    }

    [Fact]
    public async Task GroupOperation_DirectSale_SeparateDebt_Keeps_Freight_Gross_And_Posts_One_Debt()
    {
        await using var db = CreateDb();
        await SeedReferenceDataAsync(db);
        db.ServiceProviders.Add(new ServiceProvider { Id = 1, Code = "SP1", Name = "Carrier A", IsActive = true });
        var leg = await SeedGroupTruckLegAsync(db, id: 50, qty: 30m);
        var controller = BuildController(db);

        var model = BuildSaleGroupModel(leg.Id, received: 28m, shortage: 2m, allowance: 0m,
            deduct: false, debt: true, invoice: "G-DEBT-1",
            freightRate: 10m, shortageRate: 50m, serviceProviderId: 1, operationalAssetId: null);

        await controller.CreateGroupOperation(model);

        var receipt = await db.InventoryTransportReceipts.SingleAsync();
        Assert.Equal(300m, receipt.FreightCostUsd);
        Assert.Equal(100m, receipt.ShortageChargeUsd);
        Assert.Equal(300m, receipt.FreightPayableUsd);    // gross unchanged
        var expense = await db.ExpenseTransactions.SingleAsync();
        Assert.Equal(300m, expense.AmountUsd);
        var debt = Assert.Single(await db.LedgerEntries.Where(l => l.SourceType == "ShortageCharge").ToListAsync());
        Assert.Equal(LedgerSide.Debit, debt.Side);
        Assert.Equal(100m, debt.AmountUsd);
        Assert.Equal(1, debt.ServiceProviderId);
        Assert.Null(debt.DriverId);
    }

    [Fact]
    public async Task GroupOperation_DirectSale_Preview_Formula_Matches_Stored_Values_For_Both_Modes()
    {
        // همان فرمول JS: gross = (received+shortage)×fr ، charge = chargeable×sr
        // کسر ⇒ payable = gross − charge ؛ بدهی ⇒ payable = gross.
        await using var db = CreateDb();
        await SeedReferenceDataAsync(db);
        db.ServiceProviders.Add(new ServiceProvider { Id = 1, Code = "SP1", Name = "Carrier A", IsActive = true });
        var legDeduct = await SeedGroupTruckLegAsync(db, id: 60, qty: 25m);
        var controllerA = BuildController(db);
        await controllerA.CreateGroupOperation(BuildSaleGroupModel(legDeduct.Id, received: 24m, shortage: 1m, allowance: 0m,
            deduct: true, debt: false, invoice: "G-CMP-DED", freightRate: 12m, shortageRate: 40m, serviceProviderId: 1, operationalAssetId: null));

        var deductReceipt = await db.InventoryTransportReceipts.SingleAsync();
        var gross = 25m * 12m;          // 300
        var charge = 1m * 40m;          // 40
        Assert.Equal(gross, deductReceipt.FreightCostUsd);
        Assert.Equal(gross - charge, deductReceipt.FreightPayableUsd); // 260
    }

    [Fact]
    public async Task GroupOperation_DirectSale_ZeroShortage_Posts_No_ShortageCharge()
    {
        await using var db = CreateDb();
        await SeedReferenceDataAsync(db);
        db.ServiceProviders.Add(new ServiceProvider { Id = 1, Code = "SP1", Name = "Carrier A", IsActive = true });
        var leg = await SeedGroupTruckLegAsync(db, id: 50, qty: 30m);
        var controller = BuildController(db);

        var model = BuildSaleGroupModel(leg.Id, received: 30m, shortage: 0m, allowance: 0m,
            deduct: true, debt: false, invoice: "G-ZERO-1",
            freightRate: 10m, shortageRate: 50m, serviceProviderId: 1, operationalAssetId: null);

        await controller.CreateGroupOperation(model);

        var receipt = await db.InventoryTransportReceipts.SingleAsync();
        Assert.Equal(300m, receipt.FreightPayableUsd);   // no deduction
        Assert.DoesNotContain(await db.LedgerEntries.ToListAsync(), l => l.SourceType == "ShortageCharge");
    }

    [Fact]
    public async Task GroupOperation_DirectSale_SeparateDebt_On_IndependentDriver_Posts_Debt_To_Driver()
    {
        await using var db = CreateDb();
        await SeedReferenceDataAsync(db);
        db.Drivers.Add(new Driver { Id = 1, FullName = "Driver A", IsActive = true });
        var leg = await SeedGroupTruckLegAsync(db, id: 50, qty: 30m, driverId: 1);
        var controller = BuildController(db);

        var model = BuildSaleGroupModel(leg.Id, received: 28m, shortage: 2m, allowance: 0m,
            deduct: false, debt: true, invoice: "G-DRV-1",
            freightRate: 10m, shortageRate: 50m, serviceProviderId: null, operationalAssetId: null);

        await controller.CreateGroupOperation(model);

        var freight = Assert.Single(await db.LedgerEntries.Where(l => l.SourceType == "Expense").ToListAsync());
        Assert.Equal(LedgerSide.Credit, freight.Side);
        Assert.Equal(1, freight.DriverId);
        var debt = Assert.Single(await db.LedgerEntries.Where(l => l.SourceType == "ShortageCharge").ToListAsync());
        Assert.Equal(1, debt.DriverId);
        Assert.Null(debt.ServiceProviderId);
        Assert.Equal(100m, debt.AmountUsd);
    }

    [Fact]
    public async Task GroupOperation_DirectSale_OwnOperationalAsset_Posts_No_Freight_And_No_Debt()
    {
        await using var db = CreateDb();
        await SeedReferenceDataAsync(db);
        db.OperationalAssets.Add(new OperationalAsset { Id = 1, AssetCode = "ASSET-1", Name = "Company Truck", IsActive = true });
        var leg = await SeedGroupTruckLegAsync(db, id: 50, qty: 30m);
        var controller = BuildController(db);

        var model = BuildSaleGroupModel(leg.Id, received: 28m, shortage: 2m, allowance: 0m,
            deduct: false, debt: true, invoice: "G-ASSET-1",
            freightRate: 10m, shortageRate: 50m, serviceProviderId: null, operationalAssetId: 1);

        await controller.CreateGroupOperation(model);

        Assert.Empty(await db.ExpenseTransactions.ToListAsync());
        Assert.DoesNotContain(await db.LedgerEntries.ToListAsync(), l => l.SourceType == "Expense" || l.SourceType == "ShortageCharge");
        Assert.Single(await db.SalesTransactions.ToListAsync()); // فروش هنوز ثبت می‌شود
    }

    [Fact]
    public async Task GroupOperation_DirectSale_Does_Not_Create_Inbound_InventoryMovement()
    {
        await using var db = CreateDb();
        await SeedReferenceDataAsync(db);
        db.ServiceProviders.Add(new ServiceProvider { Id = 1, Code = "SP1", Name = "Carrier A", IsActive = true });
        var leg = await SeedGroupTruckLegAsync(db, id: 50, qty: 30m);
        var beforeMovements = await db.InventoryMovements.CountAsync();
        var controller = BuildController(db);

        await controller.CreateGroupOperation(BuildSaleGroupModel(leg.Id, received: 28m, shortage: 2m, allowance: 0m,
            deduct: true, debt: false, invoice: "G-MOV-1", freightRate: 10m, shortageRate: 50m, serviceProviderId: 1, operationalAssetId: null));

        Assert.Equal(beforeMovements, await db.InventoryMovements.CountAsync());
        var sale = await db.SalesTransactions.SingleAsync();
        Assert.Null(sale.ContractId);
        Assert.Equal(1, sale.ProductId);
    }

    [Fact]
    public async Task GroupOperation_DirectSale_Invalid_Row_Saves_Nothing()
    {
        await using var db = CreateDb();
        await SeedReferenceDataAsync(db);
        db.ServiceProviders.Add(new ServiceProvider { Id = 1, Code = "SP1", Name = "Carrier A", IsActive = true });
        var leg1 = await SeedGroupTruckLegAsync(db, id: 50, qty: 30m);
        var leg2 = await SeedGroupTruckLegAsync(db, id: 51, qty: 20m);
        var controller = BuildController(db);

        var model = BuildSaleGroupModel(leg1.Id, received: 28m, shortage: 2m, allowance: 0m,
            deduct: true, debt: false, invoice: "G-OK-1", freightRate: 10m, shortageRate: 50m, serviceProviderId: 1, operationalAssetId: null);
        model.Legs.Add(new InventoryTransportGroupOperationLegRow
        {
            LegId = leg2.Id,
            ReceivedQuantityMt = 20m,
            ShortageQuantityMt = 0m,
            AllowanceMt = 0m,
            DeductShortageFromFreight = true,
            SaleInvoiceNumber = null // فاکتور غایب ⇒ ردیف نامعتبر
        });

        var result = await controller.CreateGroupOperation(model);

        Assert.IsType<ViewResult>(result);
        Assert.False(controller.ModelState.IsValid);
        Assert.Empty(await db.InventoryTransportReceipts.ToListAsync());
        Assert.Empty(await db.SalesTransactions.ToListAsync());
        Assert.Empty(await db.LedgerEntries.ToListAsync());
        Assert.Empty(await db.ExpenseTransactions.ToListAsync());
    }

    [Fact]
    public async Task GroupOperation_DirectSale_Resubmit_Does_Not_Duplicate()
    {
        await using var db = CreateDb();
        await SeedReferenceDataAsync(db);
        db.ServiceProviders.Add(new ServiceProvider { Id = 1, Code = "SP1", Name = "Carrier A", IsActive = true });
        var leg = await SeedGroupTruckLegAsync(db, id: 50, qty: 30m);

        await BuildController(db).CreateGroupOperation(BuildSaleGroupModel(leg.Id, received: 28m, shortage: 2m, allowance: 0m,
            deduct: true, debt: false, invoice: "G-DUP-1", freightRate: 10m, shortageRate: 50m, serviceProviderId: 1, operationalAssetId: null));

        var salesAfterFirst = await db.SalesTransactions.CountAsync();
        var receiptsAfterFirst = await db.InventoryTransportReceipts.CountAsync();

        // ثبت دوباره با فاکتور جدید: leg دیگر رسیدپذیر نیست ⇒ چیزی ثبت نمی‌شود.
        var second = await BuildController(db).CreateGroupOperation(BuildSaleGroupModel(leg.Id, received: 28m, shortage: 2m, allowance: 0m,
            deduct: true, debt: false, invoice: "G-DUP-2", freightRate: 10m, shortageRate: 50m, serviceProviderId: 1, operationalAssetId: null));

        Assert.Equal(salesAfterFirst, await db.SalesTransactions.CountAsync());
        Assert.Equal(receiptsAfterFirst, await db.InventoryTransportReceipts.CountAsync());
    }

    private static async Task<InventoryTransportLeg> SeedGroupTruckLegAsync(
        ApplicationDbContext db, int id, decimal qty, int? driverId = null)
    {
        var leg = new InventoryTransportLeg
        {
            Id = id,
            TransportGroupKey = "ITG:1",
            SourcePurchaseContractId = 1,
            ProductId = 1,
            SourceTerminalId = 1,
            SourceStorageTankId = 1,
            DestinationTerminalId = 2,
            DestinationLocationId = 1,
            TransportType = LoadingTransportType.Truck,
            WagonNumber = "TRK-" + id,
            LoadedDate = new DateTime(2026, 5, 4),
            QuantityMt = qty,
            DriverId = driverId,
            Status = InventoryTransportLegStatus.Loaded
        };
        db.InventoryTransportLegs.Add(leg);
        await db.SaveChangesAsync();
        return leg;
    }

    private static InventoryTransportGroupOperationViewModel BuildSaleGroupModel(
        int legId, decimal received, decimal shortage, decimal allowance,
        bool deduct, bool debt, string invoice,
        decimal freightRate, decimal shortageRate,
        int? serviceProviderId, int? operationalAssetId)
        => new()
        {
            GroupKey = "ITG:1",
            Mode = InventoryTransportReceiptDestination.DirectSale,
            OperationDate = new DateTime(2026, 5, 6),
            SaleCustomerId = 1,
            SaleCurrency = "USD",
            SaleUnitPriceInCurrency = 750m,
            FreightRateUsdPerMt = freightRate,
            ShortageRateUsd = shortageRate,
            ServiceProviderId = serviceProviderId,
            OperationalAssetId = operationalAssetId,
            Legs =
            [
                new InventoryTransportGroupOperationLegRow
                {
                    LegId = legId,
                    ReceivedQuantityMt = received,
                    ShortageQuantityMt = shortage,
                    AllowanceMt = allowance,
                    DeductShortageFromFreight = deduct,
                    ShortageAsSeparateDebt = debt,
                    SaleInvoiceNumber = invoice
                }
            ]
        };

    private static ApplicationDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ApplicationDbContext(options);
    }

    private static InventoryTransportLegsController BuildController(ApplicationDbContext db)
        => new(db, new StockService(db))
        {
            TempData = new TempDataDictionary(new DefaultHttpContext(), new InMemoryTempDataProvider())
        };

    private static void AssertTankDisplayName(
        InventoryTransportLegsController controller,
        string displayName,
        string technicalCode)
    {
        var tanks = Assert.IsType<SelectList>(controller.ViewData["SourceStorageTanks"]);
        var option = Assert.Single(tanks.Where(item => item.Value == "1"));
        Assert.Equal(displayName, option.Text);
        Assert.DoesNotContain(technicalCode, option.Text, StringComparison.Ordinal);
    }

    private static async Task SeedReferenceDataAsync(ApplicationDbContext db)
    {
        db.Companies.Add(new Company { Id = 1, Code = "PTG", Name = "PTG", IsActive = true });
        db.Suppliers.Add(new Supplier { Id = 1, Name = "Supplier A", IsActive = true });
        db.Customers.Add(new Customer { Id = 1, Name = "Customer A", IsActive = true });
        db.Products.AddRange(
            new Product { Id = 1, Code = "GO", Name = "Gas Oil", IsActive = true },
            new Product { Id = 2, Code = "PMS", Name = "Petrol", IsActive = true });
        db.Terminals.AddRange(
            new Terminal { Id = 1, Code = "T1", Name = "Third Country Tank", IsActive = true },
            new Terminal { Id = 2, Code = "T2", Name = "Destination Terminal", IsActive = true });
        db.StorageTanks.AddRange(
            new StorageTank { Id = 1, TerminalId = 1, TankCode = "TK-1", ProductId = 1, CapacityMt = 500m, IsActive = true },
            new StorageTank { Id = 2, TerminalId = 2, TankCode = "TK-2", ProductId = 1, CapacityMt = 500m, IsActive = true });
        db.Locations.Add(new Location { Id = 1, Name = "Kabul Depot", IsActive = true });
        db.Contracts.AddRange(
            new Contract
            {
                Id = 1,
                ContractNumber = "PUR-1",
                ContractType = ContractType.Purchase,
                CompanyId = 1,
                SupplierId = 1,
                ProductId = 1,
                ContractDate = new DateTime(2026, 5, 1),
                QuantityMt = 100m,
                PricingMethod = PricingMethod.Fixed,
                UnitPriceUsd = 100m
            },
            new Contract
            {
                Id = 2,
                ContractNumber = "SALE-1",
                ContractType = ContractType.Sale,
                CompanyId = 1,
                CustomerId = 1,
                ProductId = 1,
                ContractDate = new DateTime(2026, 5, 1),
                QuantityMt = 100m,
                PricingMethod = PricingMethod.Fixed,
                UnitPriceUsd = 120m
            });
        await db.SaveChangesAsync();
    }

    private static async Task SeedSourceStockAsync(ApplicationDbContext db, decimal quantityMt)
    {
        db.InventoryMovements.Add(new InventoryMovement
        {
            ProductId = 1,
            ContractId = 1,
            TerminalId = 1,
            StorageTankId = 1,
            Direction = MovementDirection.In,
            MovementDate = new DateTime(2026, 5, 1),
            QuantityMt = quantityMt,
            ReferenceDocument = "RECEIPT-1"
        });
        await db.SaveChangesAsync();
    }

    private static async Task SeedShipmentTransportGroupAsync(ApplicationDbContext db)
    {
        db.Contracts.Add(new Contract
        {
            Id = 3,
            ContractNumber = "PUR-2",
            ContractType = ContractType.Purchase,
            CompanyId = 1,
            SupplierId = 1,
            ProductId = 1,
            ContractDate = new DateTime(2026, 5, 2),
            QuantityMt = 200m,
            PricingMethod = PricingMethod.Fixed,
            UnitPriceUsd = 110m
        });
        db.Shipments.Add(new Shipment
        {
            Id = 1,
            ShipmentCode = "KALUGA",
            DepartureDate = new DateTime(2026, 6, 1),
            QuantityMt = 100m
        });
        db.InventoryTransportLegs.AddRange(
            new InventoryTransportLeg
            {
                Id = 300,
                ShipmentId = 1,
                SourcePurchaseContractId = 1,
                ProductId = 1,
                SourceTerminalId = 1,
                TransportType = LoadingTransportType.Vessel,
                WagonNumber = "KALUGA",
                LoadedDate = new DateTime(2026, 6, 1),
                QuantityMt = 40m,
                Status = InventoryTransportLegStatus.Loaded,
                OutboundInventoryMovementId = 30
            },
            new InventoryTransportLeg
            {
                Id = 301,
                ShipmentId = 1,
                SourcePurchaseContractId = 3,
                ProductId = 1,
                SourceTerminalId = 1,
                TransportType = LoadingTransportType.Vessel,
                WagonNumber = "KALUGA",
                LoadedDate = new DateTime(2026, 6, 1),
                QuantityMt = 60m,
                Status = InventoryTransportLegStatus.InTransit,
                OutboundInventoryMovementId = 31
            });
        await db.SaveChangesAsync();
    }

    private static async Task SeedLegacyReceiptBackedStockAsync(
        ApplicationDbContext db,
        int contractId,
        int loadingId,
        int receiptId,
        int movementId,
        decimal quantityMt)
    {
        var loading = new LoadingRegister
        {
            Id = loadingId,
            ContractId = contractId,
            ProductId = 1,
            TransportType = LoadingTransportType.Wagon,
            LoadingDate = new DateTime(2026, 5, 1),
            LoadedQuantityMt = quantityMt,
            LoadingPriceUsd = 100m
        };
        var receipt = new LoadingReceipt
        {
            Id = receiptId,
            LoadingRegister = loading,
            LoadingRegisterId = loadingId,
            TerminalId = 1,
            StorageTankId = 1,
            ReceiptDestination = LoadingReceiptDestination.ToInventory,
            ReceiptDate = new DateTime(2026, 5, 2),
            ReceivedQuantityMt = quantityMt
        };

        db.LoadingRegisters.Add(loading);
        db.LoadingReceipts.Add(receipt);
        db.InventoryMovements.Add(new InventoryMovement
        {
            Id = movementId,
            ProductId = 1,
            ContractId = null,
            TerminalId = 1,
            StorageTankId = 1,
            LoadingReceipt = receipt,
            LoadingReceiptId = receiptId,
            Direction = MovementDirection.In,
            MovementDate = new DateTime(2026, 5, 2),
            QuantityMt = quantityMt,
            ReferenceDocument = $"LEGACY-RECEIPT:{receiptId}"
        });
        await db.SaveChangesAsync();
    }

    private static async Task SeedPurchaseLoadingAsync(ApplicationDbContext db)
    {
        db.LoadingRegisters.Add(new LoadingRegister
        {
            ContractId = 1,
            ProductId = 1,
            TransportType = LoadingTransportType.Vessel,
            LoadingDate = new DateTime(2026, 5, 1),
            LoadedQuantityMt = 10m,
            LoadingPriceUsd = 100m
        });
        await db.SaveChangesAsync();
    }

    private static async Task<InventoryTransportLeg> SeedDraftLegAsync(ApplicationDbContext db, decimal quantityMt)
    {
        var leg = new InventoryTransportLeg
        {
            SourcePurchaseContractId = 1,
            ProductId = 1,
            SourceTerminalId = 1,
            SourceStorageTankId = 1,
            DestinationTerminalId = 2,
            DestinationLocationId = 1,
            TransportType = LoadingTransportType.Wagon,
            WagonNumber = "WGN-001",
            RwbNo = "RWB-001",
            LoadedDate = new DateTime(2026, 5, 2),
            QuantityMt = quantityMt,
            Status = InventoryTransportLegStatus.Draft
        };
        db.InventoryTransportLegs.Add(leg);
        await db.SaveChangesAsync();
        return leg;
    }

    private sealed class InMemoryTempDataProvider : ITempDataProvider
    {
        private IDictionary<string, object> _data = new Dictionary<string, object>();

        public IDictionary<string, object> LoadTempData(HttpContext context) => _data;

        public void SaveTempData(HttpContext context, IDictionary<string, object> values)
            => _data = new Dictionary<string, object>(values);
    }
}
