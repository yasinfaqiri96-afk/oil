using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Controllers;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.ContractJourney;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services;
using Xunit;

namespace PTGOilSystem.Web.Tests;

public class ContractJourneyControllerTests
{
    [Theory]
    [InlineData("summary", 1)]
    [InlineData("loadings", 1)]
    [InlineData("receipts", 1)]
    [InlineData("inventory", 1)]
    [InlineData("inventorytransport", 1)]
    [InlineData("dispatch", 1)]
    [InlineData("sales", 2)]
    [InlineData("costs", 2)]
    [InlineData("finance", 2)]
    public void Details_Export_Builds_Documents_For_Every_Visible_Tab(
        string tab,
        int expectedDocumentCount)
    {
        var model = new ContractJourneyDetailsViewModel
        {
            ContractId = 17,
            ContractNumber = "PUR-17",
            ContractTypeName = "Purchase",
            CompanyName = "PTG",
            ProductName = "Diesel",
            ContractQuantityMt = 100m,
            IsPurchaseContract = true,
            LoadingItems = [new ContractJourneyLoadingItemViewModel { Id = 1, LoadingDate = new DateTime(2026, 8, 1), LoadedQuantityMt = 10m }],
            ReceiptItems = [new ContractJourneyReceiptItemViewModel { Id = 1, LoadingRegisterId = 1, ReceiptDate = new DateTime(2026, 8, 2), ReceivedQuantityMt = 9m }],
            InventoryMovementItems = [new ContractJourneyInventoryMovementItemViewModel { Id = 1, MovementDate = new DateTime(2026, 8, 2), SignedQuantityMt = 9m, RunningBalanceMt = 9m }],
            InventoryTransportLegItems = [new ContractJourneyTransportLegItemViewModel { Id = 1, LoadedDate = new DateTime(2026, 8, 3), QuantityMt = 9m, ReceivedQuantityMt = 8m, ShortageQuantityMt = 1m }],
            DispatchItems = [new ContractJourneyDispatchItemViewModel { Id = 1, DispatchDate = new DateTime(2026, 8, 3), LoadedQuantityMt = 8m }],
            SalesItems = [new ContractJourneySaleItemViewModel { SalesTransactionId = 1, SaleDate = new DateTime(2026, 8, 4), QuantityMt = 5m, AmountUsd = 2_500m }],
            PreSaleItems = [new ContractJourneyPreSaleItemViewModel { SalesTransactionId = 2, SaleDate = new DateTime(2026, 8, 4), QuantityMt = 2m }],
            ExpenseItems = [new ContractJourneyExpenseItemViewModel { ExpenseTransactionId = 1, ExpenseDate = new DateTime(2026, 8, 4), AmountUsd = 100m }],
            LossItems = [new ContractJourneyLossItemViewModel { LossEventId = 1, EventDate = new DateTime(2026, 8, 4), DifferenceQuantityMt = 1m }],
            PaymentItems = [new ContractJourneyPaymentItemViewModel { PaymentTransactionId = 1, PaymentDate = new DateTime(2026, 8, 5), AmountUsd = 500m }],
            SarrafSettlementItems = [new ContractJourneySarrafSettlementItemViewModel { SarrafSettlementId = 1, SettlementDate = new DateTime(2026, 8, 5), RequestedAmountUsd = 500m }]
        };

        var documents = ContractJourneyController.BuildDetailsExportDocuments(model, tab);

        Assert.Equal(expectedDocumentCount, documents.Count);
        Assert.All(documents, document =>
        {
            Assert.NotEmpty(document.Columns);
            Assert.Contains(tab, document.FileNameStem, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(document.Filters, filter => filter.Value == tab);
            Assert.All(document.Rows, row => Assert.Equal(document.Columns.Count, row.Cells.Count));
        });
    }

    [Fact]
    public async Task Index_Returns_Contracts_For_Selection()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        db.Contracts.AddRange(
            BuildPurchaseContract(1, "PUR-001", 1000m),
            BuildSaleContract(2, "SAL-001", 300m));
        await db.SaveChangesAsync();

        var controller = new ContractJourneyController(db, new StockService(db));

        var result = await controller.Index();

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<ContractJourneyIndexViewModel>(view.Model);
        Assert.Equal(2, model.Items.Count);
        Assert.Contains(model.Items, item => item.ContractNumber == "PUR-001" && item.ContractTypeName == "خرید");
        Assert.Contains(model.Items, item => item.ContractNumber == "SAL-001" && item.ContractTypeName == "فروش");
    }

    [Fact]
    public async Task Index_Applies_Selected_Tab()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        db.Contracts.Add(BuildPurchaseContract(1, "PUR-001", 1000m));
        await db.SaveChangesAsync();

        var controller = new ContractJourneyController(db, new StockService(db));

        var result = await controller.Index(tab: ContractJourneyTabs.Index.Recent);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<ContractJourneyIndexViewModel>(view.Model);
        Assert.Equal(ContractJourneyTabs.Index.Recent, model.ActiveTab);
    }

    [Fact]
    public async Task Details_For_Purchase_Contract_Builds_Journey_From_Direct_Relations()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        db.Contracts.AddRange(
            BuildPurchaseContract(1, "PUR-001", 1000m),
            BuildSaleContract(2, "SAL-001", 200m));
        db.LoadingRegisters.Add(new LoadingRegister
        {
            Id = 1,
            ContractId = 1,
            ProductId = 1,
            LoadingDate = new DateTime(2026, 5, 1),
            LoadedQuantityMt = 100m,
            BillOfLadingNumber = "BL-001"
        });
        db.LoadingReceipts.Add(new LoadingReceipt
        {
            Id = 1,
            LoadingRegisterId = 1,
            TerminalId = 1,
            StorageTankId = 1,
            ReceiptDate = new DateTime(2026, 5, 2),
            ReceivedQuantityMt = 80m,
            ReferenceDocument = "RCV-001"
        });
        db.InventoryMovements.AddRange(
            new InventoryMovement
            {
                Id = 1,
                ProductId = 1,
                ContractId = 1,
                TerminalId = 1,
                StorageTankId = 1,
                LoadingReceiptId = 1,
                Direction = MovementDirection.In,
                MovementDate = new DateTime(2026, 5, 2),
                QuantityMt = 80m,
                ReferenceDocument = "RCV-001"
            },
            new InventoryMovement
            {
                Id = 2,
                ProductId = 1,
                ContractId = 1,
                TerminalId = 1,
                StorageTankId = 1,
                SalesTransactionId = 1,
                Direction = MovementDirection.Out,
                MovementDate = new DateTime(2026, 5, 4),
                QuantityMt = 20m,
                ReferenceDocument = "INV-001"
            });
        db.TruckDispatches.Add(new TruckDispatch
        {
            Id = 1,
            ContractId = 1,
            ProductId = 1,
            TruckId = 1,
            DriverId = 1,
            DispatchDate = new DateTime(2026, 5, 3),
            LoadedQuantityMt = 15m,
            FreightCostUsd = 4m
        });
        db.SalesTransactions.Add(new SalesTransaction
        {
            Id = 1,
            CompanyId = 1,
            ContractId = null,
            CustomerId = 1,
            ProductId = 1,
            SaleStage = SaleStage.TerminalStock,
            InvoiceNumber = "INV-001",
            SaleDate = new DateTime(2026, 5, 4),
            QuantityMt = 20m,
            UnitPriceUsd = 5m,
            TotalUsd = 100m
        });
        db.ExpenseTypes.Add(new ExpenseType { Id = 1, Code = "PORT", Name = "Port", NamePersian = "بندر" });
        db.ExpenseTransactions.Add(new ExpenseTransaction
        {
            Id = 1,
            ExpenseTypeId = 1,
            ContractId = 1,
            ExpenseDate = new DateTime(2026, 5, 5),
            AmountUsd = 10m,
            Description = "EXP-001"
        });
        db.LossEvents.Add(new LossEvent
        {
            Id = 1,
            Stage = LossEventStage.ReceiptShortage,
            ProductId = 1,
            ContractId = 1,
            LoadingReceiptId = 1,
            EventDate = new DateTime(2026, 5, 6),
            ExpectedQuantityMt = 80m,
            ActualQuantityMt = 79m,
            DifferenceQuantityMt = 1m,
            ToleranceQuantityMt = 0m,
            AllowableLossMt = 0m,
            ChargeableLossMt = 1m,
            AffectsInventory = false
        });
        db.LedgerEntries.AddRange(
            new LedgerEntry
            {
                Id = 1,
                EntryDate = new DateTime(2026, 5, 4),
                Side = LedgerSide.Credit,
                AmountUsd = 100m,
                Currency = "USD",
                Description = "Sale ledger",
                SourceType = "Sale",
                SourceId = 1,
                ContractId = 1,
                Reference = "INV-001"
            },
            new LedgerEntry
            {
                Id = 2,
                EntryDate = new DateTime(2026, 5, 5),
                Side = LedgerSide.Debit,
                AmountUsd = 10m,
                Currency = "USD",
                Description = "Expense ledger",
                SourceType = "Expense",
                SourceId = 1,
                ContractId = 1,
                Reference = "EXP-001"
            },
            new LedgerEntry
            {
                Id = 3,
                EntryDate = new DateTime(2026, 5, 7),
                Side = LedgerSide.Credit,
                AmountUsd = 50m,
                Currency = "USD",
                Description = "Manual receipt",
                SourceType = "ManualReceipt",
                SourceId = 1,
                ContractId = 1,
                Reference = "PAY-001"
            });
        db.PaymentTransactions.Add(new PaymentTransaction
        {
            Id = 1,
            PaymentDate = new DateTime(2026, 5, 7),
            Direction = PaymentDirection.In,
            PaymentKind = PaymentKind.ManualReceipt,
            CashAccountId = 1,
            ContractId = 1,
            Amount = 50m,
            Currency = "USD",
            AppliedFxRateToUsd = 1m,
            AmountUsd = 50m,
            Reference = "PAY-001",
            LedgerEntryId = 3
        });
        await db.SaveChangesAsync();

        var controller = new ContractJourneyController(db, new StockService(db));

        var result = await controller.Details(1);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<ContractJourneyDetailsViewModel>(view.Model);
        Assert.True(model.IsPurchaseContract);
        Assert.Equal(1000m, model.Kpis.ContractQuantityMt);
        Assert.Equal(100m, model.Kpis.LoadedQuantityMt);
        Assert.Equal(80m, model.Kpis.ReceivedQuantityMt);
        Assert.Equal(20m, model.Kpis.SoldQuantityMt);
        Assert.Equal(60m, model.Kpis.CurrentStockQuantityMt);
        Assert.Equal(10m, model.Kpis.TotalExpensesUsd);
        Assert.Equal(50m, model.Kpis.TotalPaymentsUsd);
        Assert.Equal(140m, model.Kpis.RelatedBalanceUsd);
        Assert.Single(model.DispatchItems);
        Assert.Single(model.SalesItems);
        Assert.Equal("فروش فاکتوری / بدون قرارداد فروش", model.SalesItems[0].SalesContractDisplay);
        Assert.True(model.PreSaleSectionState.IsNeedsReview);
        Assert.Contains(model.NotesForReview, note => note.Contains("پیش‌فروش"));
    }

    [Fact]
    public async Task Details_For_Sale_Contract_Returns_Guidance_Message()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        db.Contracts.Add(BuildSaleContract(2, "SAL-001", 300m));
        await db.SaveChangesAsync();

        var controller = new ContractJourneyController(db, new StockService(db));

        var result = await controller.Details(2);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<ContractJourneyDetailsViewModel>(view.Model);
        Assert.False(model.IsPurchaseContract);
        Assert.NotNull(model.UnsupportedMessage);
        Assert.Contains("قرارداد خرید", model.UnsupportedMessage!);
    }

    [Fact]
    public async Task Details_For_Empty_Purchase_Contract_Returns_Zeroed_Summary_And_Review_State()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        db.Contracts.Add(BuildPurchaseContract(1, "PUR-001", 500m));
        await db.SaveChangesAsync();

        var controller = new ContractJourneyController(db, new StockService(db));

        var result = await controller.Details(1);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<ContractJourneyDetailsViewModel>(view.Model);
        Assert.True(model.IsPurchaseContract);
        Assert.Equal(500m, model.Kpis.ContractQuantityMt);
        Assert.Equal(0m, model.Kpis.LoadedQuantityMt);
        Assert.Equal(0m, model.Kpis.ReceivedQuantityMt);
        Assert.Equal(0m, model.Kpis.SoldQuantityMt);
        Assert.Equal(0m, model.Kpis.CurrentStockQuantityMt);
        Assert.Empty(model.LoadingItems);
        Assert.Empty(model.ReceiptItems);
        Assert.Empty(model.SalesItems);
        Assert.True(model.PreSaleSectionState.IsNeedsReview);
        Assert.True(model.DispatchSectionState.IsNeedsReview);
    }

    [Fact]
    public async Task Details_Summary_Http_Uses_Light_Initial_Payload_Without_Tab_Collections()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        db.Contracts.Add(BuildPurchaseContract(1, "PUR-001", 500m));
        db.LoadingRegisters.Add(new LoadingRegister
        {
            Id = 1,
            ContractId = 1,
            ProductId = 1,
            LoadingDate = new DateTime(2026, 5, 1),
            LoadedQuantityMt = 100m,
            LoadingPriceUsd = 250m,
            BillOfLadingNumber = "BL-001"
        });
        db.LoadingReceipts.Add(new LoadingReceipt
        {
            Id = 1,
            LoadingRegisterId = 1,
            TerminalId = 1,
            StorageTankId = 1,
            ReceiptDate = new DateTime(2026, 5, 2),
            ReceivedQuantityMt = 80m
        });
        db.InventoryMovements.Add(new InventoryMovement
        {
            Id = 1,
            ContractId = 1,
            ProductId = 1,
            TerminalId = 1,
            StorageTankId = 1,
            Direction = MovementDirection.Out,
            MovementDate = new DateTime(2026, 5, 4),
            QuantityMt = 20m,
            SalesTransactionId = 1
        });
        db.SalesTransactions.Add(new SalesTransaction
        {
            Id = 1,
            CompanyId = 1,
            CustomerId = 1,
            ProductId = 1,
            SaleStage = SaleStage.TerminalStock,
            InvoiceNumber = "INV-001",
            SaleDate = new DateTime(2026, 5, 4),
            QuantityMt = 20m,
            UnitPriceUsd = 500m,
            TotalUsd = 10_000m
        });
        db.ExpenseTypes.Add(new ExpenseType { Id = 1, Code = "PORT", Name = "Port" });
        db.ExpenseTransactions.Add(new ExpenseTransaction
        {
            Id = 1,
            ExpenseTypeId = 1,
            ContractId = 1,
            ExpenseDate = new DateTime(2026, 5, 5),
            AmountUsd = 100m
        });
        await db.SaveChangesAsync();

        var controller = new ContractJourneyController(db, new StockService(db))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };

        var result = await controller.Details(1, tab: ContractJourneyTabs.Details.Summary);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<ContractJourneyDetailsViewModel>(view.Model);
        Assert.True(model.IsInitialSummaryPayload);
        Assert.True(model.SummaryMetrics.HasValues);
        Assert.Equal(100m, model.Kpis.LoadedQuantityMt);
        Assert.Equal(80m, model.Kpis.ReceivedQuantityMt);
        Assert.Equal(20m, model.Kpis.SoldQuantityMt);
        Assert.Equal(1, model.SummaryMetrics.LoadingCount);
        Assert.Equal(1, model.SummaryMetrics.ReceiptCount);
        Assert.Equal(1, model.SummaryMetrics.SaleCount);
        Assert.Empty(model.LoadingItems);
        Assert.Empty(model.ReceiptItems);
        Assert.Empty(model.InventoryMovementItems);
        Assert.Empty(model.InventoryTransportLegItems);
        Assert.Empty(model.DispatchItems);
        Assert.Empty(model.SalesItems);
        Assert.Empty(model.ExpenseItems);
        Assert.Empty(model.PaymentItems);
        Assert.Empty(model.LedgerItems);
    }

    [Fact]
    public async Task Details_Purchase_Without_Loading_Recommends_Loading_And_Does_Not_Write_Operational_Records()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        db.Contracts.Add(BuildPurchaseContract(1, "PUR-001", 500m));
        await db.SaveChangesAsync();

        var controller = new ContractJourneyController(db, new StockService(db));

        var result = await controller.Details(1);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<ContractJourneyDetailsViewModel>(view.Model);
        Assert.Equal("ثبت بارگیری", model.NextRecommendedActionTitle);
        Assert.Contains("/Loading/Create", model.NextRecommendedActionUrl);
        Assert.Contains("contractId=1", model.NextRecommendedActionUrl);
        Assert.Empty(await db.InventoryMovements.ToListAsync());
        Assert.Empty(await db.LedgerEntries.ToListAsync());
    }

    [Fact]
    public async Task Details_Purchase_With_Loading_And_No_Receipt_Recommends_Receipt()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        db.Contracts.Add(BuildPurchaseContract(1, "PUR-001", 500m));
        db.LoadingRegisters.Add(new LoadingRegister
        {
            Id = 1,
            ContractId = 1,
            ProductId = 1,
            LoadingDate = new DateTime(2026, 5, 1),
            TransportType = LoadingTransportType.Truck,
            TruckId = 1,
            LoadedQuantityMt = 100m,
            LoadingPriceUsd = 250m
        });
        await db.SaveChangesAsync();

        var controller = new ContractJourneyController(db, new StockService(db));

        var result = await controller.Details(1);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<ContractJourneyDetailsViewModel>(view.Model);
        Assert.Equal("ثبت رسید بارگیری", model.NextRecommendedActionTitle);
        Assert.Contains("/LoadingReceipts/Create", model.NextRecommendedActionUrl);
        Assert.Contains("loadingId=1", model.NextRecommendedActionUrl);
        var bulkCandidate = Assert.Single(model.BulkReceiptCandidates);
        Assert.Equal(1, bulkCandidate.LoadingRegisterId);
        Assert.Equal("TRK-01", bulkCandidate.VehicleNumber);
        Assert.Equal("Gas Oil", bulkCandidate.ProductName);
        Assert.Equal("موتر", bulkCandidate.TransportTypeLabel);
        Assert.Equal(100m, bulkCandidate.LoadedQuantityMt);
        Assert.Equal(250m, bulkCandidate.LoadingPriceUsd);
        Assert.Equal(25_000m, bulkCandidate.LoadingValueUsd);
        Assert.Equal(100m, bulkCandidate.RemainingQuantityMt);
    }

    [Fact]
    public async Task Details_Purchase_With_Receipt_But_No_Inventory_Or_Exit_Recommends_Inventory_Review()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        db.Contracts.Add(BuildPurchaseContract(1, "PUR-001", 500m));
        db.LoadingRegisters.Add(new LoadingRegister
        {
            Id = 1,
            ContractId = 1,
            ProductId = 1,
            LoadingDate = new DateTime(2026, 5, 1),
            LoadedQuantityMt = 100m
        });
        db.LoadingReceipts.Add(new LoadingReceipt
        {
            Id = 1,
            LoadingRegisterId = 1,
            TerminalId = 1,
            ReceiptDate = new DateTime(2026, 5, 2),
            ReceivedQuantityMt = 100m
        });
        await db.SaveChangesAsync();

        var controller = new ContractJourneyController(db, new StockService(db));

        var result = await controller.Details(1);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<ContractJourneyDetailsViewModel>(view.Model);
        Assert.Equal("بررسی موجودی یا ثبت Dispatch / فروش", model.NextRecommendedActionTitle);
        Assert.Contains("tab=inventory", model.NextRecommendedActionUrl);
    }

    [Fact]
    public async Task Details_Purchase_Builds_Phase2_ReadOnly_Summaries_And_Warnings()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        var contract = BuildPurchaseContract(1, "PUR-001", 500m);
        contract.UnitPriceUsd = null;
        db.Contracts.Add(contract);
        db.LoadingRegisters.AddRange(
            new LoadingRegister
            {
                Id = 1,
                ContractId = 1,
                ProductId = 1,
                LoadingDate = new DateTime(2026, 5, 1),
                LoadedQuantityMt = 100m,
                BillOfLadingNumber = "BL-001",
                RwbNo = "RWB-001",
                WagonNumber = "WGN-001",
                LoadingPriceUsd = 410m,
                TransportExpenseUsd = 12m,
                WarehouseExpenseUsd = 6m,
                RailwayExpenseUsd = 8m
            },
            new LoadingRegister
            {
                Id = 2,
                ContractId = 1,
                ProductId = 1,
                LoadingDate = new DateTime(2026, 5, 3),
                LoadedQuantityMt = 50m,
                RwbNo = "RWB-002",
                WagonNumber = "WGN-002"
            });
        db.LoadingReceipts.Add(new LoadingReceipt
        {
            Id = 1,
            LoadingRegisterId = 1,
            TerminalId = 1,
            StorageTankId = 1,
            ReceiptDate = new DateTime(2026, 5, 2),
            ReceivedQuantityMt = 95m,
            ActualArrivedQuantityMt = 94m,
            ReferenceDocument = "RCV-001"
        });
        db.InventoryMovements.Add(new InventoryMovement
        {
            Id = 1,
            ProductId = 1,
            ContractId = 1,
            TerminalId = 1,
            StorageTankId = 1,
            LoadingReceiptId = 1,
            Direction = MovementDirection.In,
            MovementDate = new DateTime(2026, 5, 2),
            QuantityMt = 95m,
            ReferenceDocument = "RCV-001"
        });
        db.CustomsDeclarations.Add(new CustomsDeclaration
        {
            Id = 1,
            LoadingRegisterId = 1,
            DeclarationDate = new DateTime(2026, 5, 4),
            DeclarationReference = "CUS-001",
            TotalUsd = 30m
        });
        db.ExpenseTypes.Add(new ExpenseType { Id = 1, Code = "CUSTOMS", Name = "Customs", NamePersian = "گمرک" });
        db.ExpenseTransactions.Add(new ExpenseTransaction
        {
            Id = 1,
            ExpenseTypeId = 1,
            ContractId = 1,
            ExpenseDate = new DateTime(2026, 5, 4),
            AmountUsd = 20m,
            Description = "customs"
        });
        db.ExpenseTypes.AddRange(
            new ExpenseType { Id = 2, Code = "STORAGE", Name = "Okarem storage rent" },
            new ExpenseType { Id = 3, Code = "TRUCK-FREIGHT", Name = "Truck freight Okarem to Herat" });
        db.ExpenseTransactions.AddRange(
            new ExpenseTransaction
            {
                Id = 2,
                ExpenseTypeId = 2,
                ContractId = 1,
                ExpenseDate = new DateTime(2026, 5, 4),
                AmountUsd = 10m,
                Description = "storage rent from balance sheet"
            },
            new ExpenseTransaction
            {
                Id = 3,
                ExpenseTypeId = 3,
                ContractId = 1,
                ExpenseDate = new DateTime(2026, 5, 4),
                AmountUsd = 40m,
                Description = "truck freight from balance sheet"
            });
        db.LossEvents.AddRange(
            new LossEvent
            {
                Id = 1,
                Stage = LossEventStage.LoadingDifference,
                ProductId = 1,
                ContractId = 1,
                LoadingRegisterId = 1,
                EventDate = new DateTime(2026, 5, 1),
                ExpectedQuantityMt = 100m,
                ActualQuantityMt = 99.25m,
                DifferenceQuantityMt = 0.75m,
                ChargeableLossMt = 0.75m
            },
            new LossEvent
            {
                Id = 2,
                Stage = LossEventStage.ReceiptShortage,
                ProductId = 1,
                ContractId = 1,
                LoadingReceiptId = 1,
                EventDate = new DateTime(2026, 5, 2),
                ExpectedQuantityMt = 95m,
                ActualQuantityMt = 94.4m,
                DifferenceQuantityMt = 0.6m,
                ChargeableLossMt = 0.5m
            },
            new LossEvent
            {
                Id = 4,
                Stage = LossEventStage.ReceiptShortage,
                ProductId = 1,
                ContractId = 1,
                LoadingReceiptId = 1,
                EventDate = new DateTime(2026, 5, 2),
                ExpectedQuantityMt = 95m,
                ActualQuantityMt = 94.75m,
                DifferenceQuantityMt = 0.25m,
                ChargeableLossMt = 0m
            },
            new LossEvent
            {
                Id = 3,
                Stage = LossEventStage.ReceiptShortage,
                ProductId = 1,
                ContractId = 1,
                LoadingReceiptId = 1,
                EventDate = new DateTime(2026, 5, 3),
                ExpectedQuantityMt = 95m,
                ActualQuantityMt = 90m,
                DifferenceQuantityMt = 5m,
                ChargeableLossMt = 5m,
                IsCancelled = true
            });
        await db.SaveChangesAsync();

        var beforeMovements = await db.InventoryMovements.CountAsync();
        var beforeLedgers = await db.LedgerEntries.CountAsync();
        var controller = new ContractJourneyController(db, new StockService(db));

        var result = await controller.Details(1);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<ContractJourneyDetailsViewModel>(view.Model);
        Assert.Equal(1, model.UnreceiptedLoadingCount);
        Assert.Contains("RWB-001", model.LoadingDocumentReferences);
        Assert.Equal(-1m, model.ReceiptDifferenceQuantityMt);
        Assert.Equal(95m, model.InventoryInQuantityMt);
        Assert.Equal(0m, model.InventoryOutQuantityMt);
        Assert.Equal(95m, model.Kpis.CurrentStockQuantityMt);
        Assert.Equal(26m, model.LoadingOperationalExpenseUsd);
        Assert.Equal(8m, model.LoadingRailwayExpenseUsd);
        Assert.Equal(6m, model.LoadingWarehouseExpenseUsd);
        Assert.Equal(52m, model.ContractTransportExpenseUsd);
        Assert.Equal(16m, model.ContractStorageRentExpenseUsd);
        Assert.Equal(0.75m, model.LoadingDifferenceLossMt);
        Assert.Equal(0.85m, model.ReceiptShortageLossMt);
        Assert.Equal(0m, model.DispatchShortageLossMt);
        Assert.Equal(0m, model.InventoryTransportLossMt);
        Assert.Equal(0m, model.TankLossMt);
        Assert.Equal(0m, model.SalesLossMt);
        Assert.Equal(1.6m, model.Kpis.LossQuantityMt);
        Assert.Equal(3, model.LossItems.Count);
        Assert.Equal(41000m, model.MiniPnl.TraceablePurchaseCostUsd);
        Assert.Equal(1, model.CustomsDeclarationCount);
        Assert.Equal(30m, model.CustomsDeclarationTotalUsd);
        Assert.Equal(3, model.ExpenseBreakdowns.Count);
        Assert.Contains(model.Warnings, warning => warning.Contains("رسید") && warning.Contains("فروش"));
        Assert.Equal(beforeMovements, await db.InventoryMovements.CountAsync());
        Assert.Equal(beforeLedgers, await db.LedgerEntries.CountAsync());
    }

    [Fact]
    public async Task Details_Purchase_Loading_Pnl_Uses_Priced_Lots_And_Tracks_Pending_Loading_Prices()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        var contract = BuildPurchaseContract(1, "PUR-001", 150m);
        contract.UnitPriceUsd = null;
        db.Contracts.Add(contract);
        db.LoadingRegisters.AddRange(
            new LoadingRegister
            {
                Id = 1,
                ContractId = 1,
                ProductId = 1,
                LoadingDate = new DateTime(2026, 5, 1),
                LoadedQuantityMt = 50m,
                WagonNumber = "WGN-001",
                LoadingPriceUsd = 200m
            },
            new LoadingRegister
            {
                Id = 2,
                ContractId = 1,
                ProductId = 1,
                LoadingDate = new DateTime(2026, 5, 2),
                LoadedQuantityMt = 50m,
                WagonNumber = "WGN-002",
                LoadingPriceUsd = 300m
            },
            new LoadingRegister
            {
                Id = 3,
                ContractId = 1,
                ProductId = 1,
                LoadingDate = new DateTime(2026, 5, 3),
                LoadedQuantityMt = 10m,
                WagonNumber = "WGN-003",
                LoadingPriceUsd = null
            });
        await db.SaveChangesAsync();

        var controller = new ContractJourneyController(db, new StockService(db));

        var result = await controller.Details(1);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<ContractJourneyDetailsViewModel>(view.Model);
        Assert.Equal(25_000m, model.MiniPnl.TraceablePurchaseCostUsd);
        Assert.Equal(100m, model.MiniPnl.PricedPurchaseQuantityMt);
        Assert.Equal(10m, model.MiniPnl.PendingPurchaseQuantityMt);
        Assert.Equal(250m, model.MiniPnl.WeightedAveragePurchasePriceUsd);
        Assert.True(model.MiniPnl.NeedsReview);
        Assert.Equal(1, model.PendingLoadingPriceCount);
        Assert.Equal(10m, model.PendingLoadingPriceQuantityMt);
        Assert.Contains(model.Warnings, warning => warning.Contains("P&L"));
        Assert.Contains(model.LoadingItems, item => item.Id == 3 && item.IsPricePending);
    }

    [Fact]
    public async Task ContractJourney_PurchaseAggregation_Matches_Previous_Inline_Numbers()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        var contract = BuildPurchaseContract(1, "PUR-001", 150m);
        contract.UnitPriceUsd = null;
        db.Contracts.Add(contract);
        db.LoadingRegisters.AddRange(
            new LoadingRegister
            {
                Id = 1,
                ContractId = 1,
                ProductId = 1,
                LoadingDate = new DateTime(2026, 5, 1),
                LoadedQuantityMt = 50m,
                LoadingPriceUsd = 200m,
                TransportExpenseUsd = 1m,
                WarehouseExpenseUsd = 2m,
                OtherExpenseUsd = 3m,
                RailwayExpenseUsd = 4m
            },
            new LoadingRegister
            {
                Id = 2,
                ContractId = 1,
                ProductId = 1,
                LoadingDate = new DateTime(2026, 5, 2),
                LoadedQuantityMt = 50m,
                LoadingPriceUsd = 300m,
                TransportExpenseUsd = 4m,
                WarehouseExpenseUsd = 5m,
                OtherExpenseUsd = 6m,
                RailwayExpenseUsd = 7m
            },
            new LoadingRegister
            {
                Id = 3,
                ContractId = 1,
                ProductId = 1,
                LoadingDate = new DateTime(2026, 5, 3),
                LoadedQuantityMt = 10m,
                LoadingPriceUsd = null,
                TransportExpenseUsd = 8m,
                WarehouseExpenseUsd = 9m,
                OtherExpenseUsd = 10m,
                RailwayExpenseUsd = 11m
            });
        await db.SaveChangesAsync();

        var controller = new ContractJourneyController(db, new StockService(db));

        var result = await controller.Details(1);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<ContractJourneyDetailsViewModel>(view.Model);
        Assert.Equal(110m, model.Kpis.LoadedQuantityMt);
        Assert.Equal(25_000m, model.MiniPnl.TraceablePurchaseCostUsd);
        Assert.Equal(100m, model.MiniPnl.PricedPurchaseQuantityMt);
        Assert.Equal(10m, model.MiniPnl.PendingPurchaseQuantityMt);
        Assert.Equal(250m, model.MiniPnl.WeightedAveragePurchasePriceUsd);
        Assert.Equal(13m, model.ContractTransportExpenseUsd);
        Assert.Equal(16m, model.LoadingWarehouseExpenseUsd);
        Assert.Equal(70m, model.LoadingOperationalExpenseUsd);
        Assert.Equal(1, model.PendingLoadingPriceCount);
        var firstLoading = Assert.Single(model.LoadingItems.Where(item => item.Id == 1));
        Assert.Equal(1m, firstLoading.TransportExpenseUsd);
        Assert.Equal(2m, firstLoading.WarehouseExpenseUsd);
        Assert.Equal(3m, firstLoading.OtherExpenseUsd);
        Assert.Equal(4m, firstLoading.RailwayExpenseUsd);
        Assert.Equal(10m, firstLoading.LoadingExpenseTotalUsd);
    }

    [Fact]
    public async Task Details_Purchase_Uses_Contract_Final_Price_For_Loadings_With_Missing_Snapshot_Price()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        var contract = BuildPurchaseContract(1, "PUR-001", 100m);
        contract.PricingMethod = PricingMethod.ManualFinalPrice;
        contract.UnitPriceUsd = null;
        contract.ManualFinalPriceUsd = 585m;
        db.Contracts.Add(contract);
        db.LoadingRegisters.Add(new LoadingRegister
        {
            Id = 1,
            ContractId = 1,
            ProductId = 1,
            LoadingDate = new DateTime(2026, 5, 1),
            LoadedQuantityMt = 40m,
            WagonNumber = "WGN-001",
            LoadingPriceUsd = null
        });
        await db.SaveChangesAsync();

        var controller = new ContractJourneyController(db, new StockService(db));

        var result = await controller.Details(1);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<ContractJourneyDetailsViewModel>(view.Model);
        var loading = Assert.Single(model.LoadingItems);
        Assert.Equal(585m, loading.LoadingPriceUsd);
        Assert.Equal(23_400m, loading.LoadingValueUsd);
        Assert.Equal(23_400m, model.MiniPnl.TraceablePurchaseCostUsd);
        Assert.Equal(40m, model.MiniPnl.PricedPurchaseQuantityMt);
        Assert.Equal(0m, model.MiniPnl.PendingPurchaseQuantityMt);
        Assert.False(model.MiniPnl.NeedsReview);
        Assert.Equal(0, model.PendingLoadingPriceCount);
    }

    [Fact]
    public async Task Details_Purchase_Includes_DirectSale_From_LoadingReceiptAllocation_Without_InventoryMovement()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        db.Contracts.Add(BuildPurchaseContract(1, "PUR-001", 100m));
        db.LoadingRegisters.Add(new LoadingRegister
        {
            Id = 1,
            ContractId = 1,
            ProductId = 1,
            LoadingDate = new DateTime(2026, 5, 1),
            LoadedQuantityMt = 40m,
            LoadingPriceUsd = 300m
        });
        db.LoadingReceipts.Add(new LoadingReceipt
        {
            Id = 1,
            LoadingRegisterId = 1,
            TerminalId = 1,
            ReceiptDate = new DateTime(2026, 5, 2),
            ReceivedQuantityMt = 25m,
            ReferenceDocument = "RCV-DS-001"
        });
        db.SalesTransactions.Add(new SalesTransaction
        {
            Id = 1,
            CompanyId = 1,
            ContractId = null,
            CustomerId = 1,
            ProductId = 1,
            SaleStage = SaleStage.InTransit,
            InvoiceNumber = "DS-001",
            SaleDate = new DateTime(2026, 5, 3),
            QuantityMt = 25m,
            UnitPriceUsd = 500m,
            TotalUsd = 12500m
        });
        db.LoadingReceiptAllocations.Add(new LoadingReceiptAllocation
        {
            Id = 1,
            LoadingReceiptId = 1,
            Destination = LoadingReceiptAllocationDestination.DirectSale,
            Status = LoadingReceiptAllocationStatus.Completed,
            QuantityMt = 25m,
            SourcePurchaseContractId = 1,
            TerminalId = 1,
            SalesTransactionId = 1,
            ReferenceDocument = "DS-001"
        });
        db.LedgerEntries.Add(new LedgerEntry
        {
            Id = 1,
            EntryDate = new DateTime(2026, 5, 3),
            Side = LedgerSide.Credit,
            AmountUsd = 12500m,
            Currency = "USD",
            Description = "Direct sale ledger",
            SourceType = "Sale",
            SourceId = 1,
            ContractId = 1,
            CustomerId = 1,
            Reference = "DS-001"
        });
        await db.SaveChangesAsync();

        var controller = new ContractJourneyController(db, new StockService(db));

        var result = await controller.Details(1, tab: ContractJourneyTabs.Details.Sales);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<ContractJourneyDetailsViewModel>(view.Model);
        var sale = Assert.Single(model.SalesItems);
        Assert.Equal("DS-001", sale.InvoiceNumber);
        Assert.Equal(25m, sale.QuantityMt);
        Assert.Equal(12500m, sale.AmountUsd);
        Assert.Equal("Customer A", sale.CustomerName);
        Assert.False(sale.HasInventoryMovementTrace);
        Assert.Equal("Direct Sale from Receipt Allocation", sale.TraceKind);
        Assert.Equal(1, sale.LoadingReceiptAllocationId);
        Assert.Equal(25m, sale.AllocationQuantityMt);
        Assert.False(sale.HasQuantityMismatch);
        Assert.Equal("PUR-001", sale.SourcePurchaseContractNumber);
        Assert.Equal(25m, model.Kpis.SoldQuantityMt);
        Assert.Equal(12500m, model.MiniPnl.TraceableSalesRevenueUsd);
        Assert.Equal(0, model.InventoryOutQuantityMt);
        Assert.Empty(model.InventoryMovementItems);
        Assert.Single(model.LedgerItems);
    }

    [Fact]
    public async Task Details_Purchase_MiniPnl_Subtracts_Traceable_Purchase_And_Operational_Costs()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        db.Contracts.Add(BuildPurchaseContract(1, "PUR-001", 100m));
        db.LoadingRegisters.Add(new LoadingRegister
        {
            Id = 1,
            ContractId = 1,
            ProductId = 1,
            LoadingDate = new DateTime(2026, 5, 1),
            LoadedQuantityMt = 25m,
            LoadingPriceUsd = 300m,
            TransportExpenseUsd = 20m,
            WarehouseExpenseUsd = 30m,
            OtherExpenseUsd = 40m,
            RailwayExpenseUsd = 10m
        });
        db.LoadingReceipts.Add(new LoadingReceipt
        {
            Id = 1,
            LoadingRegisterId = 1,
            TerminalId = 1,
            ReceiptDate = new DateTime(2026, 5, 2),
            ReceivedQuantityMt = 25m
        });
        db.SalesTransactions.Add(new SalesTransaction
        {
            Id = 1,
            CompanyId = 1,
            CustomerId = 1,
            ProductId = 1,
            SaleStage = SaleStage.InTransit,
            InvoiceNumber = "DS-PNL",
            SaleDate = new DateTime(2026, 5, 3),
            QuantityMt = 25m,
            UnitPriceUsd = 500m,
            TotalUsd = 12500m
        });
        db.LoadingReceiptAllocations.Add(new LoadingReceiptAllocation
        {
            Id = 1,
            LoadingReceiptId = 1,
            Destination = LoadingReceiptAllocationDestination.DirectSale,
            Status = LoadingReceiptAllocationStatus.Completed,
            QuantityMt = 25m,
            SourcePurchaseContractId = 1,
            TerminalId = 1,
            SalesTransactionId = 1
        });
        db.CustomsDeclarations.Add(new CustomsDeclaration
        {
            Id = 1,
            LoadingRegisterId = 1,
            DeclarationDate = new DateTime(2026, 5, 3),
            TotalUsd = 50m
        });
        db.ExpenseTypes.Add(new ExpenseType { Id = 1, Code = "PORT", Name = "Port" });
        db.ExpenseTransactions.Add(new ExpenseTransaction
        {
            Id = 1,
            ExpenseTypeId = 1,
            ContractId = 1,
            ExpenseDate = new DateTime(2026, 5, 4),
            AmountUsd = 75m
        });
        db.LossEvents.Add(new LossEvent
        {
            Id = 1,
            ProductId = 1,
            ContractId = 1,
            LoadingRegisterId = 1,
            EventDate = new DateTime(2026, 5, 5),
            ChargeableLossMt = 1m
        });
        await db.SaveChangesAsync();

        var controller = new ContractJourneyController(db, new StockService(db));

        var result = await controller.Details(1);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<ContractJourneyDetailsViewModel>(view.Model);
        Assert.Equal(12_500m, model.MiniPnl.TraceableSalesRevenueUsd);
        Assert.Equal(7_500m, model.MiniPnl.TraceablePurchaseCostUsd);
        Assert.Equal(525m, model.MiniPnl.TraceableExpensesUsd);
        Assert.Equal(4_475m, model.MiniPnl.GrossMarginUsd);
    }

    [Fact]
    public async Task Details_Purchase_Warns_When_DirectSale_Allocation_Quantity_Differs_From_Sale()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        db.Contracts.Add(BuildPurchaseContract(1, "PUR-001", 100m));
        db.LoadingRegisters.Add(new LoadingRegister
        {
            Id = 1,
            ContractId = 1,
            ProductId = 1,
            LoadingDate = new DateTime(2026, 5, 1),
            LoadedQuantityMt = 40m
        });
        db.LoadingReceipts.Add(new LoadingReceipt
        {
            Id = 1,
            LoadingRegisterId = 1,
            TerminalId = 1,
            ReceiptDate = new DateTime(2026, 5, 2),
            ReceivedQuantityMt = 25m
        });
        db.SalesTransactions.Add(new SalesTransaction
        {
            Id = 1,
            CompanyId = 1,
            CustomerId = 1,
            ProductId = 1,
            SaleStage = SaleStage.InTransit,
            InvoiceNumber = "DS-MISMATCH",
            SaleDate = new DateTime(2026, 5, 3),
            QuantityMt = 20m,
            UnitPriceUsd = 500m,
            TotalUsd = 10000m
        });
        db.LoadingReceiptAllocations.Add(new LoadingReceiptAllocation
        {
            Id = 1,
            LoadingReceiptId = 1,
            Destination = LoadingReceiptAllocationDestination.DirectSale,
            Status = LoadingReceiptAllocationStatus.Completed,
            QuantityMt = 25m,
            SourcePurchaseContractId = 1,
            TerminalId = 1,
            SalesTransactionId = 1
        });
        await db.SaveChangesAsync();

        var controller = new ContractJourneyController(db, new StockService(db));

        var result = await controller.Details(1);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<ContractJourneyDetailsViewModel>(view.Model);
        var sale = Assert.Single(model.SalesItems);
        Assert.True(sale.HasQuantityMismatch);
        Assert.Equal(25m, sale.AllocationQuantityMt);
        Assert.Equal(20m, sale.QuantityMt);
        Assert.Contains(model.Warnings, warning => warning.Contains("DirectSale") && warning.Contains("quantity mismatch"));
    }

    [Fact]
    public async Task Details_Purchase_Includes_DirectFromReceipt_Dispatch_Trace_Without_Revenue_Or_InventoryWarning()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        db.Contracts.Add(BuildPurchaseContract(1, "PUR-001", 100m));
        db.Locations.Add(new Location { Id = 1, Name = "Kabul Depot" });
        db.LoadingRegisters.Add(new LoadingRegister
        {
            Id = 1,
            ContractId = 1,
            ProductId = 1,
            LoadingDate = new DateTime(2026, 5, 1),
            LoadedQuantityMt = 100m
        });
        db.LoadingReceipts.Add(new LoadingReceipt
        {
            Id = 1,
            LoadingRegisterId = 1,
            TerminalId = 1,
            ReceiptDate = new DateTime(2026, 5, 2),
            ReceivedQuantityMt = 100m,
            ReceiptDestination = LoadingReceiptDestination.Mixed
        });
        db.LoadingReceiptAllocations.Add(new LoadingReceiptAllocation
        {
            Id = 1,
            LoadingReceiptId = 1,
            Destination = LoadingReceiptAllocationDestination.DirectDispatchToTruck,
            Status = LoadingReceiptAllocationStatus.InTransit,
            QuantityMt = 100m,
            SourcePurchaseContractId = 1,
            TerminalId = 1,
            DestinationLocationId = 1,
            DestinationName = "Kabul Depot"
        });
        db.TruckDispatches.Add(new TruckDispatch
        {
            Id = 1,
            DispatchMode = TruckDispatchMode.DirectFromReceipt,
            LoadingReceiptAllocationId = 1,
            ContractId = 1,
            ProductId = 1,
            TruckId = 1,
            DriverId = 1,
            DestinationLocationId = 1,
            DispatchDate = new DateTime(2026, 5, 3),
            Status = DispatchStatus.InTransit,
            LoadedQuantityMt = 60m
        });
        await db.SaveChangesAsync();

        var controller = new ContractJourneyController(db, new StockService(db));

        var result = await controller.Details(1, tab: ContractJourneyTabs.Details.Inventory);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<ContractJourneyDetailsViewModel>(view.Model);
        var dispatch = Assert.Single(model.DispatchItems);
        Assert.Equal(TruckDispatchMode.DirectFromReceipt, dispatch.DispatchMode);
        Assert.Equal(1, dispatch.LoadingReceiptAllocationId);
        Assert.Equal(1, dispatch.LoadingReceiptId);
        Assert.Equal("Direct Truck Dispatch from Receipt Allocation", dispatch.TraceKind);
        Assert.Equal("TRK-01", dispatch.TruckPlateNumber);
        Assert.Equal("Driver A", dispatch.DriverName);
        Assert.Equal("Kabul Depot", dispatch.DestinationName);
        Assert.Equal("InTransit", dispatch.StatusName);
        Assert.Equal(100m, dispatch.AllocationQuantityMt);
        Assert.Equal(60m, dispatch.AllocationTotalDirectDispatchedQuantityMt);
        Assert.Equal(40m, dispatch.AllocationRemainingQuantityMt);
        Assert.Equal(0m, model.MiniPnl.TraceableSalesRevenueUsd);
        Assert.Equal(0, model.DispatchWithoutInventoryTraceCount);
        Assert.Empty(await db.InventoryMovements.ToListAsync());
        Assert.Empty(await db.LedgerEntries.ToListAsync());
    }

    [Fact]
    public async Task Details_Purchase_Derives_Dispatch_Shortage_From_Delivered_TruckDispatch_Without_Creating_LossEvent()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        db.Contracts.Add(BuildPurchaseContract(1, "VOLGA-2026", 100m));
        db.Locations.Add(new Location { Id = 1, Name = "Kabul Depot" });
        db.LoadingRegisters.Add(new LoadingRegister
        {
            Id = 1,
            ContractId = 1,
            ProductId = 1,
            LoadingDate = new DateTime(2026, 5, 1),
            LoadedQuantityMt = 31m,
            LoadingPriceUsd = 590m
        });
        db.LoadingReceipts.Add(new LoadingReceipt
        {
            Id = 1,
            LoadingRegisterId = 1,
            TerminalId = 1,
            ReceiptDate = new DateTime(2026, 5, 2),
            ReceivedQuantityMt = 31m,
            ReceiptDestination = LoadingReceiptDestination.Mixed
        });
        db.LoadingReceiptAllocations.Add(new LoadingReceiptAllocation
        {
            Id = 1,
            LoadingReceiptId = 1,
            Destination = LoadingReceiptAllocationDestination.DirectDispatchToTruck,
            Status = LoadingReceiptAllocationStatus.Completed,
            QuantityMt = 31m,
            SourcePurchaseContractId = 1,
            TerminalId = 1,
            DestinationLocationId = 1
        });
        db.TruckDispatches.Add(new TruckDispatch
        {
            Id = 1,
            DispatchMode = TruckDispatchMode.DirectFromReceipt,
            LoadingReceiptAllocationId = 1,
            ContractId = 1,
            ProductId = 1,
            TruckId = 1,
            DriverId = 1,
            DestinationLocationId = 1,
            DispatchDate = new DateTime(2026, 5, 3),
            Status = DispatchStatus.Delivered,
            LoadedQuantityMt = 31m,
            DischargedQuantityMt = 30.75m,
            ShortageMt = 0.25m,
            AllowanceMt = 0.1m,
            ChargeableShortageMt = 0.15m,
            FreightCostUsd = 1705m,
            FreightPayableUsd = 1565.5m,
            PayableUsd = 1565.5m
        });
        await db.SaveChangesAsync();

        var controller = new ContractJourneyController(db, new StockService(db));

        var result = await controller.Details(1, tab: ContractJourneyTabs.Details.Costs);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<ContractJourneyDetailsViewModel>(view.Model);
        var loss = Assert.Single(model.LossItems);
        Assert.Equal(0m, loss.LossEventId);
        Assert.Equal(0.25m, loss.DifferenceQuantityMt);
        Assert.Equal(0.15m, loss.ChargeableLossMt);
        Assert.Equal("TruckDispatch #1", loss.TraceKind);
        Assert.Equal(0.25m, model.DispatchShortageLossMt);
        Assert.Equal(0.25m, model.Kpis.LossQuantityMt);
        Assert.Equal(0m, model.MiniPnl.TraceableExpensesUsd);
        Assert.Empty(await db.LossEvents.ToListAsync());
        Assert.Empty(await db.LedgerEntries.ToListAsync());
    }

    [Fact]
    public async Task Details_Purchase_Includes_DirectFromReceipt_Linked_Sale_Revenue_Only_When_Sale_Exists()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        db.Contracts.Add(BuildPurchaseContract(1, "PUR-001", 100m));
        db.Locations.Add(new Location { Id = 1, Name = "Kabul Depot" });
        db.LoadingRegisters.Add(new LoadingRegister
        {
            Id = 1,
            ContractId = 1,
            ProductId = 1,
            LoadingDate = new DateTime(2026, 5, 1),
            LoadedQuantityMt = 100m
        });
        db.LoadingReceipts.Add(new LoadingReceipt
        {
            Id = 1,
            LoadingRegisterId = 1,
            TerminalId = 1,
            ReceiptDate = new DateTime(2026, 5, 2),
            ReceivedQuantityMt = 100m,
            ReceiptDestination = LoadingReceiptDestination.Mixed
        });
        db.LoadingReceiptAllocations.Add(new LoadingReceiptAllocation
        {
            Id = 1,
            LoadingReceiptId = 1,
            Destination = LoadingReceiptAllocationDestination.DirectDispatchToTruck,
            Status = LoadingReceiptAllocationStatus.Completed,
            QuantityMt = 60m,
            SourcePurchaseContractId = 1,
            TerminalId = 1,
            DestinationLocationId = 1
        });
        db.SalesTransactions.Add(new SalesTransaction
        {
            Id = 1,
            CompanyId = 1,
            CustomerId = 1,
            ProductId = 1,
            DestinationLocationId = 1,
            SaleStage = SaleStage.InTransit,
            InvoiceNumber = "DDS-001",
            SaleDate = new DateTime(2026, 5, 3),
            QuantityMt = 60m,
            UnitPriceInCurrency = 500m,
            UnitPriceUsd = 500m,
            TotalInCurrency = 30000m,
            TotalUsd = 30000m
        });
        db.TruckDispatches.Add(new TruckDispatch
        {
            Id = 1,
            DispatchMode = TruckDispatchMode.DirectFromReceipt,
            LoadingReceiptAllocationId = 1,
            SalesTransactionId = 1,
            ContractId = 1,
            ProductId = 1,
            TruckId = 1,
            DriverId = 1,
            DestinationLocationId = 1,
            DispatchDate = new DateTime(2026, 5, 3),
            Status = DispatchStatus.Loaded,
            LoadedQuantityMt = 60m
        });
        await db.SaveChangesAsync();

        var controller = new ContractJourneyController(db, new StockService(db));

        var result = await controller.Details(1, tab: ContractJourneyTabs.Details.Sales);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<ContractJourneyDetailsViewModel>(view.Model);
        var sale = Assert.Single(model.SalesItems);
        Assert.Equal(1, sale.SalesTransactionId);
        Assert.Equal(1, sale.TruckDispatchId);
        Assert.Equal("Sale from Direct Truck Dispatch", sale.TraceKind);
        Assert.False(sale.HasInventoryMovementTrace);
        Assert.Equal(60m, sale.QuantityMt);
        Assert.Equal(30000m, sale.AmountUsd);
        Assert.Equal(30000m, model.MiniPnl.TraceableSalesRevenueUsd);
        Assert.Equal(60m, model.Kpis.SoldQuantityMt);
        Assert.Empty(model.InventoryMovementItems);
        Assert.Empty(await db.InventoryMovements.ToListAsync());
    }

    [Fact]
    public async Task Details_Purchase_Surfaces_InventoryTransportLegs_And_TransportLosses()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        db.Contracts.Add(BuildPurchaseContract(1, "PUR-001", 500m));
        db.Terminals.Add(new Terminal { Id = 2, Code = "DST", Name = "Destination Terminal" });
        db.StorageTanks.Add(new StorageTank { Id = 2, TerminalId = 2, TankCode = "TK-DST", ProductId = 1, CapacityMt = 1000m });
        db.LoadingRegisters.Add(new LoadingRegister
        {
            Id = 1,
            ContractId = 1,
            ProductId = 1,
            LoadingDate = new DateTime(2026, 5, 1),
            LoadedQuantityMt = 100m,
            LoadingPriceUsd = 400m
        });
        db.InventoryMovements.Add(new InventoryMovement
        {
            Id = 50,
            ProductId = 1,
            ContractId = 1,
            TerminalId = 1,
            StorageTankId = 1,
            Direction = MovementDirection.Out,
            MovementDate = new DateTime(2026, 5, 2),
            QuantityMt = 20m,
            ReferenceDocument = "TRANSPORT-LEG:50"
        });
        db.InventoryTransportLegs.Add(new InventoryTransportLeg
        {
            Id = 50,
            SourcePurchaseContractId = 1,
            ProductId = 1,
            SourceTerminalId = 1,
            SourceStorageTankId = 1,
            DestinationTerminalId = 2,
            DestinationStorageTankId = 2,
            TransportType = LoadingTransportType.Wagon,
            WagonNumber = "WGN-TL",
            RwbNo = "RWB-TL",
            LoadedDate = new DateTime(2026, 5, 2),
            QuantityMt = 20m,
            Status = InventoryTransportLegStatus.Received,
            OutboundInventoryMovementId = 50
        });
        db.InventoryTransportReceipts.Add(new InventoryTransportReceipt
        {
            Id = 51,
            InventoryTransportLegId = 50,
            ReceiptDate = new DateTime(2026, 5, 4),
            ReceiptDestination = InventoryTransportReceiptDestination.ToInventory,
            ReceivedQuantityMt = 18m,
            ShortageQuantityMt = 2m,
            DestinationTerminalId = 2,
            DestinationStorageTankId = 2
        });
        db.LossEvents.Add(new LossEvent
        {
            Id = 52,
            ProductId = 1,
            ContractId = 1,
            TransportLegId = 50,
            Stage = LossEventStage.ReceiptShortage,
            EventDate = new DateTime(2026, 5, 4),
            ExpectedQuantityMt = 20m,
            ActualQuantityMt = 18m,
            DifferenceQuantityMt = 2m,
            ChargeableLossMt = 2m
        });
        await db.SaveChangesAsync();

        var controller = new ContractJourneyController(db, new StockService(db));

        var result = await controller.Details(1, tab: ContractJourneyTabs.Details.Dispatch);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<ContractJourneyDetailsViewModel>(view.Model);
        var leg = Assert.Single(model.InventoryTransportLegItems);
        Assert.Equal(50, leg.Id);
        Assert.Equal("WGN-TL", leg.WagonNumber);
        Assert.Equal(20m, leg.QuantityMt);
        Assert.Equal(18m, leg.ReceivedQuantityMt);
        Assert.Equal(2m, leg.ShortageQuantityMt);
        Assert.Equal(2m, model.ReceiptShortageLossMt);
        Assert.Equal(2m, model.InventoryTransportLossMt);
        Assert.Equal(800m, model.MiniPnl.TraceableExpensesUsd);
        Assert.Equal(100m, model.Kpis.LoadedQuantityMt);
        Assert.DoesNotContain(model.LoadingItems, l => l.Id == 50);
    }

    [Fact]
    public async Task Details_Purchase_Splits_InventoryTransportLeg_Expenses_By_Leg_Weight()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        db.Contracts.Add(BuildPurchaseContract(1, "PUR-001", 500m));
        db.Terminals.Add(new Terminal { Id = 2, Code = "DST", Name = "Destination Terminal" });
        db.StorageTanks.Add(new StorageTank { Id = 2, TerminalId = 2, TankCode = "TK-DST", ProductId = 1, CapacityMt = 1000m });
        db.LoadingRegisters.Add(new LoadingRegister
        {
            Id = 1,
            ContractId = 1,
            ProductId = 1,
            LoadingDate = new DateTime(2026, 5, 1),
            LoadedQuantityMt = 100m,
            LoadingPriceUsd = 400m
        });
        db.InventoryTransportLegs.AddRange(
            new InventoryTransportLeg
            {
                Id = 60,
                SourcePurchaseContractId = 1,
                ProductId = 1,
                SourceTerminalId = 1,
                SourceStorageTankId = 1,
                DestinationTerminalId = 2,
                DestinationStorageTankId = 2,
                TransportType = LoadingTransportType.Wagon,
                WagonNumber = "WGN-60",
                RwbNo = "RWB-60",
                LoadedDate = new DateTime(2026, 5, 2),
                QuantityMt = 60m,
                Status = InventoryTransportLegStatus.Loaded
            },
            new InventoryTransportLeg
            {
                Id = 61,
                SourcePurchaseContractId = 1,
                ProductId = 1,
                SourceTerminalId = 1,
                SourceStorageTankId = 1,
                DestinationTerminalId = 2,
                DestinationStorageTankId = 2,
                TransportType = LoadingTransportType.Vessel,
                BillOfLadingNumber = "BL-61",
                LoadedDate = new DateTime(2026, 5, 3),
                QuantityMt = 50m,
                Status = InventoryTransportLegStatus.Loaded
            });
        db.ExpenseTypes.Add(new ExpenseType { Id = 1, Code = "LEG", Name = "Leg expense", NamePersian = "هزینه حمل" });
        db.ExpenseTransactions.AddRange(
            new ExpenseTransaction
            {
                Id = 100,
                ExpenseTypeId = 1,
                ContractId = 1,
                ExpenseDate = new DateTime(2026, 5, 2),
                Amount = 30m,
                AmountUsd = 30m,
                Description = "General contract expense"
            },
            new ExpenseTransaction
            {
                Id = 101,
                ExpenseTypeId = 1,
                TransportLegId = 60,
                ExpenseDate = new DateTime(2026, 5, 2),
                Amount = 100m,
                AmountUsd = 100m,
                Description = "Leg 60 expense without direct ContractId"
            },
            new ExpenseTransaction
            {
                Id = 102,
                ExpenseTypeId = 1,
                ContractId = 1,
                TransportLegId = 60,
                ExpenseDate = new DateTime(2026, 5, 3),
                Amount = 40m,
                AmountUsd = 40m,
                Description = "Leg 60 expense with ContractId"
            },
            new ExpenseTransaction
            {
                Id = 103,
                ExpenseTypeId = 1,
                TransportLegId = 61,
                ExpenseDate = new DateTime(2026, 5, 4),
                Amount = 250m,
                AmountUsd = 250m,
                Description = "Leg 61 expense without direct ContractId"
            });
        await db.SaveChangesAsync();

        var controller = new ContractJourneyController(db, new StockService(db));

        var result = await controller.Details(1, tab: ContractJourneyTabs.Details.Costs);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<ContractJourneyDetailsViewModel>(view.Model);
        Assert.Equal(4, model.ExpenseItems.Count);
        Assert.Equal(420m, model.Kpis.TotalExpensesUsd);

        var wagonAllocation = Assert.Single(
            model.InventoryTransportExpenseAllocations,
            a => a.TransportLegId == 60);
        Assert.Equal(60m, wagonAllocation.QuantityMt);
        Assert.Equal(140m, wagonAllocation.ExpenseTotalUsd);
        Assert.Equal(2, wagonAllocation.ExpenseCount);
        Assert.Equal(140m / 60m, wagonAllocation.ExpensePerMtUsd);

        var vesselAllocation = Assert.Single(
            model.InventoryTransportExpenseAllocations,
            a => a.TransportLegId == 61);
        Assert.Equal(50m, vesselAllocation.QuantityMt);
        Assert.Equal(250m, vesselAllocation.ExpenseTotalUsd);
        Assert.Equal(1, vesselAllocation.ExpenseCount);
        Assert.Equal(5m, vesselAllocation.ExpensePerMtUsd);

        var generalExpense = Assert.Single(model.ExpenseItems, e => e.ExpenseTransactionId == 100);
        Assert.Null(generalExpense.TransportLegId);
        Assert.Null(generalExpense.TransportLegQuantityMt);
        Assert.Null(generalExpense.TransportLegExpensePerMtUsd);

        var legExpense = Assert.Single(model.ExpenseItems, e => e.ExpenseTransactionId == 101);
        Assert.Equal(60, legExpense.TransportLegId);
        Assert.Equal("WGN-60", legExpense.TransportLegLabel);
        Assert.Equal(60m, legExpense.TransportLegQuantityMt);
        Assert.Equal(100m / 60m, legExpense.TransportLegExpensePerMtUsd);
    }

    [Fact]
    public async Task Details_Purchase_Includes_DirectSale_From_InventoryTransportReceipt_With_Item_Pnl()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        db.Contracts.Add(BuildPurchaseContract(1, "PUR-001", 500m));
        db.Terminals.Add(new Terminal { Id = 2, Code = "DST", Name = "Destination Terminal" });
        db.StorageTanks.Add(new StorageTank { Id = 2, TerminalId = 2, TankCode = "TK-DST", ProductId = 1, CapacityMt = 1000m });
        db.LoadingRegisters.Add(new LoadingRegister
        {
            Id = 1,
            ContractId = 1,
            ProductId = 1,
            LoadingDate = new DateTime(2026, 5, 1),
            LoadedQuantityMt = 100m,
            LoadingPriceUsd = 400m
        });
        db.InventoryTransportLegs.Add(new InventoryTransportLeg
        {
            Id = 60,
            SourcePurchaseContractId = 1,
            ProductId = 1,
            SourceTerminalId = 1,
            SourceStorageTankId = 1,
            DestinationTerminalId = 2,
            DestinationStorageTankId = 2,
            TransportType = LoadingTransportType.Vessel,
            BillOfLadingNumber = "BL-60",
            LoadedDate = new DateTime(2026, 5, 2),
            QuantityMt = 50m,
            Status = InventoryTransportLegStatus.Received
        });
        db.SalesTransactions.Add(new SalesTransaction
        {
            Id = 80,
            CompanyId = 1,
            ContractId = null,
            CustomerId = 1,
            ProductId = 1,
            SaleStage = SaleStage.InTransit,
            InvoiceNumber = "INV-ITR-001",
            SaleDate = new DateTime(2026, 5, 4),
            QuantityMt = 40m,
            Currency = "USD",
            UnitPriceInCurrency = 600m,
            UnitPriceUsd = 600m,
            TotalInCurrency = 24_000m,
            TotalUsd = 24_000m
        });
        db.InventoryTransportReceipts.Add(new InventoryTransportReceipt
        {
            Id = 70,
            InventoryTransportLegId = 60,
            ReceiptDate = new DateTime(2026, 5, 4),
            ReceiptDestination = InventoryTransportReceiptDestination.DirectSale,
            ReceivedQuantityMt = 40m,
            ShortageQuantityMt = 10m,
            SalesTransactionId = 80
        });
        db.LedgerEntries.Add(new LedgerEntry
        {
            Id = 90,
            EntryDate = new DateTime(2026, 5, 4),
            Side = LedgerSide.Credit,
            AmountUsd = 24_000m,
            Currency = "USD",
            SourceType = "Sale",
            SourceId = 80,
            Reference = "INV-ITR-001",
            ContractId = 1,
            CustomerId = 1,
            Description = "Direct sale from inventory transport receipt"
        });
        db.ExpenseTypes.Add(new ExpenseType { Id = 1, Code = "LEG", Name = "Leg expense" });
        db.ExpenseTransactions.Add(new ExpenseTransaction
        {
            Id = 100,
            ExpenseTypeId = 1,
            TransportLegId = 60,
            ExpenseDate = new DateTime(2026, 5, 3),
            Amount = 100m,
            AmountUsd = 100m,
            Description = "Transport leg expense"
        });
        db.CustomsDeclarations.Add(new CustomsDeclaration
        {
            Id = 110,
            TransportLegId = 60,
            DeclarationDate = new DateTime(2026, 5, 3),
            TotalUsd = 50m
        });
        await db.SaveChangesAsync();

        var controller = new ContractJourneyController(db, new StockService(db));

        var result = await controller.Details(1, tab: ContractJourneyTabs.Details.Sales);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<ContractJourneyDetailsViewModel>(view.Model);
        var sale = Assert.Single(model.SalesItems);
        Assert.Equal(80, sale.SalesTransactionId);
        Assert.Equal(60, sale.InventoryTransportLegId);
        Assert.Equal(70, sale.InventoryTransportReceiptId);
        Assert.Equal("BL-60", sale.InventoryTransportReference);
        Assert.Equal("Direct Sale from Inventory Transport Receipt", sale.TraceKind);
        Assert.Equal(40m, sale.QuantityMt);
        Assert.Equal(24_000m, sale.AmountUsd);
        Assert.Equal(400m, sale.PurchaseUnitCostUsd);
        Assert.Equal(20_000m, sale.PurchaseCostUsd);
        Assert.Equal(100m, sale.TransportLegExpenseCostUsd);
        Assert.Equal(50m, sale.TransportLegCustomsCostUsd);
        Assert.Equal(150m, sale.TransportCostUsd);
        Assert.Equal(3_850m, sale.GrossProfitUsd);
        Assert.Equal(40m, model.Kpis.SoldQuantityMt);
        Assert.Equal(24_000m, model.MiniPnl.TraceableSalesRevenueUsd);
        Assert.Contains(model.LedgerItems, l => l.SourceType == "Sale" && l.SourceId == 80);
    }

    [Fact]
    public async Task Details_Purchase_Allocates_Shipment_Landed_Cost_To_Inventory_Sales()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        var contract = BuildPurchaseContract(1, "PUR-001", 100m);
        contract.UnitPriceUsd = 100m;
        db.Contracts.Add(contract);
        db.Shipments.Add(new Shipment
        {
            Id = 10,
            ShipmentCode = "VOLGA-TEST",
            QuantityMt = 100m,
            DepartureDate = new DateTime(2026, 5, 2)
        });
        db.ShipmentContracts.Add(new ShipmentContract
        {
            Id = 20,
            ShipmentId = 10,
            ContractId = 1,
            QuantityMt = 100m
        });
        db.InventoryTransportLegs.AddRange(
            new InventoryTransportLeg
            {
                Id = 30,
                ShipmentId = 10,
                SourcePurchaseContractId = 1,
                ProductId = 1,
                SourceTerminalId = 1,
                TransportType = LoadingTransportType.Vessel,
                BillOfLadingNumber = "BL-VOLGA",
                LoadedDate = new DateTime(2026, 5, 2),
                QuantityMt = 100m,
                Status = InventoryTransportLegStatus.Received
            },
            new InventoryTransportLeg
            {
                Id = 31,
                ShipmentId = 10,
                SourcePurchaseContractId = 1,
                ProductId = 1,
                SourceTerminalId = 1,
                TransportType = LoadingTransportType.Truck,
                RwbNo = "TRUCK-LEG",
                LoadedDate = new DateTime(2026, 5, 3),
                QuantityMt = 80m,
                Status = InventoryTransportLegStatus.Received
            });
        db.SalesTransactions.Add(new SalesTransaction
        {
            Id = 40,
            CompanyId = 1,
            ContractId = null,
            CustomerId = 1,
            ProductId = 1,
            ShipmentId = 10,
            SaleStage = SaleStage.TerminalStock,
            InvoiceNumber = "INV-VOLGA-001",
            SaleDate = new DateTime(2026, 5, 5),
            QuantityMt = 80m,
            Currency = "USD",
            UnitPriceInCurrency = 200m,
            UnitPriceUsd = 200m,
            TotalInCurrency = 16_000m,
            TotalUsd = 16_000m
        });
        db.InventoryMovements.Add(new InventoryMovement
        {
            Id = 50,
            ContractId = 1,
            ProductId = 1,
            TerminalId = 1,
            StorageTankId = 1,
            MovementDate = new DateTime(2026, 5, 5),
            Direction = MovementDirection.Out,
            QuantityMt = 80m,
            SalesTransactionId = 40,
            ReferenceDocument = "INV-VOLGA-001"
        });
        db.ExpenseTypes.Add(new ExpenseType { Id = 1, Code = "OPS", Name = "Operations" });
        db.ExpenseTransactions.AddRange(
            new ExpenseTransaction
            {
                Id = 60,
                ExpenseTypeId = 1,
                TransportLegId = 30,
                ExpenseDate = new DateTime(2026, 5, 3),
                Amount = 1_000m,
                AmountUsd = 1_000m,
                Description = "Vessel expense"
            },
            new ExpenseTransaction
            {
                Id = 61,
                ExpenseTypeId = 1,
                TransportLegId = 31,
                ExpenseDate = new DateTime(2026, 5, 4),
                Amount = 600m,
                AmountUsd = 600m,
                Description = "Truck expense"
            },
            new ExpenseTransaction
            {
                Id = 62,
                ExpenseTypeId = 1,
                ShipmentId = 10,
                ExpenseDate = new DateTime(2026, 5, 4),
                Amount = 400m,
                AmountUsd = 400m,
                Description = "Shared shipment expense"
            });
        db.CustomsDeclarations.Add(new CustomsDeclaration
        {
            Id = 70,
            TransportLegId = 31,
            DeclarationDate = new DateTime(2026, 5, 4),
            TotalUsd = 800m
        });
        await db.SaveChangesAsync();

        var controller = new ContractJourneyController(db, new StockService(db));

        var result = await controller.Details(1, tab: ContractJourneyTabs.Details.Sales);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<ContractJourneyDetailsViewModel>(view.Model);
        var sale = Assert.Single(model.SalesItems);
        Assert.Equal(10, sale.ShipmentId);
        Assert.Equal(40, sale.SalesTransactionId);
        Assert.Equal(80m, sale.QuantityMt);
        Assert.Equal(16_000m, sale.AmountUsd);
        Assert.Equal(125m, sale.PurchaseUnitCostUsd);
        Assert.Equal(10_000m, sale.PurchaseCostUsd);
        Assert.Equal(2_000m, sale.TransportLegExpenseCostUsd);
        Assert.Equal(800m, sale.TransportLegCustomsCostUsd);
        Assert.Equal(2_800m, sale.TransportCostUsd);
        Assert.Equal(3_200m, sale.GrossProfitUsd);
        Assert.Equal("Shipment landed cost allocation", sale.CostAllocationNote);
    }

    [Fact]
    public async Task Details_Purchase_Summary_And_Costs_Only_Count_Expenses_Of_The_Current_Contract_Within_Shared_Shipment()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        db.Contracts.AddRange(
            BuildPurchaseContract(1, "PUR-001", 60m),
            BuildPurchaseContract(2, "PUR-002", 40m));
        db.Shipments.Add(new Shipment
        {
            Id = 10,
            ShipmentCode = "SHIP-MIX-01",
            QuantityMt = 100m,
            DepartureDate = new DateTime(2026, 5, 2)
        });
        db.ShipmentContracts.AddRange(
            new ShipmentContract
            {
                Id = 20,
                ShipmentId = 10,
                ContractId = 1,
                QuantityMt = 60m
            },
            new ShipmentContract
            {
                Id = 21,
                ShipmentId = 10,
                ContractId = 2,
                QuantityMt = 40m
            });
        db.InventoryTransportLegs.AddRange(
            new InventoryTransportLeg
            {
                Id = 30,
                ShipmentId = 10,
                SourcePurchaseContractId = 1,
                ProductId = 1,
                SourceTerminalId = 1,
                TransportType = LoadingTransportType.Wagon,
                RwbNo = "WGN-30",
                LoadedDate = new DateTime(2026, 5, 2),
                QuantityMt = 60m,
                Status = InventoryTransportLegStatus.Received
            },
            new InventoryTransportLeg
            {
                Id = 31,
                ShipmentId = 10,
                SourcePurchaseContractId = 2,
                ProductId = 1,
                SourceTerminalId = 1,
                TransportType = LoadingTransportType.Wagon,
                RwbNo = "WGN-31",
                LoadedDate = new DateTime(2026, 5, 2),
                QuantityMt = 40m,
                Status = InventoryTransportLegStatus.Received
            });
        db.ExpenseTypes.Add(new ExpenseType { Id = 1, Code = "OPS", Name = "Operations" });
        db.ExpenseTransactions.AddRange(
            new ExpenseTransaction
            {
                Id = 60,
                ExpenseTypeId = 1,
                ContractId = 1,
                ShipmentId = 10,
                TransportLegId = 30,
                ExpenseDate = new DateTime(2026, 5, 3),
                Amount = 120m,
                AmountUsd = 120m,
                Description = "Allocated expense for contract 1"
            },
            new ExpenseTransaction
            {
                Id = 61,
                ExpenseTypeId = 1,
                ContractId = 2,
                ShipmentId = 10,
                TransportLegId = 31,
                ExpenseDate = new DateTime(2026, 5, 3),
                Amount = 80m,
                AmountUsd = 80m,
                Description = "Allocated expense for contract 2"
            });
        await db.SaveChangesAsync();

        var summaryController = new ContractJourneyController(db, new StockService(db))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };

        var summaryResult = await summaryController.Details(1, tab: ContractJourneyTabs.Details.Summary);

        var summaryView = Assert.IsType<ViewResult>(summaryResult);
        var summaryModel = Assert.IsType<ContractJourneyDetailsViewModel>(summaryView.Model);
        Assert.True(summaryModel.IsInitialSummaryPayload);
        Assert.Equal(120m, summaryModel.Kpis.TotalExpensesUsd);
        Assert.Equal(120m, summaryModel.SummaryMetrics.ExpenseTotalUsd);
        Assert.Equal(120m, summaryModel.SummaryMetrics.InventoryTransportExpenseTotalUsd);

        var costsController = new ContractJourneyController(db, new StockService(db));

        var costsResult = await costsController.Details(1, tab: ContractJourneyTabs.Details.Costs);

        var costsView = Assert.IsType<ViewResult>(costsResult);
        var costsModel = Assert.IsType<ContractJourneyDetailsViewModel>(costsView.Model);
        Assert.Equal(120m, costsModel.Kpis.TotalExpensesUsd);
        Assert.Single(costsModel.ExpenseItems);
        Assert.Equal(60, costsModel.ExpenseItems[0].ExpenseTransactionId);
        var allocation = Assert.Single(costsModel.InventoryTransportExpenseAllocations);
        Assert.Equal(30, allocation.TransportLegId);
        Assert.Equal(120m, allocation.ExpenseTotalUsd);
    }

    [Fact]
    public async Task Details_Purchase_Summary_And_Costs_Only_Count_Losses_Of_The_Current_Contract_Within_Shared_Shipment()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        db.Contracts.AddRange(
            BuildPurchaseContract(1, "PUR-001", 60m),
            BuildPurchaseContract(2, "PUR-002", 40m));
        db.Shipments.Add(new Shipment
        {
            Id = 10,
            ShipmentCode = "SHIP-LOSS-01",
            QuantityMt = 100m,
            DepartureDate = new DateTime(2026, 5, 2)
        });
        db.ShipmentContracts.AddRange(
            new ShipmentContract
            {
                Id = 20,
                ShipmentId = 10,
                ContractId = 1,
                QuantityMt = 60m
            },
            new ShipmentContract
            {
                Id = 21,
                ShipmentId = 10,
                ContractId = 2,
                QuantityMt = 40m
            });
        db.InventoryTransportLegs.AddRange(
            new InventoryTransportLeg
            {
                Id = 30,
                ShipmentId = 10,
                SourcePurchaseContractId = 1,
                ProductId = 1,
                SourceTerminalId = 1,
                TransportType = LoadingTransportType.Wagon,
                RwbNo = "WGN-30",
                LoadedDate = new DateTime(2026, 5, 2),
                QuantityMt = 60m,
                Status = InventoryTransportLegStatus.Received
            },
            new InventoryTransportLeg
            {
                Id = 31,
                ShipmentId = 10,
                SourcePurchaseContractId = 2,
                ProductId = 1,
                SourceTerminalId = 1,
                TransportType = LoadingTransportType.Wagon,
                RwbNo = "WGN-31",
                LoadedDate = new DateTime(2026, 5, 2),
                QuantityMt = 40m,
                Status = InventoryTransportLegStatus.Received
            });
        db.LossEvents.AddRange(
            new LossEvent
            {
                Id = 70,
                ContractId = 1,
                ShipmentId = 10,
                TransportLegId = 30,
                Stage = LossEventStage.TransitLoss,
                EventDate = new DateTime(2026, 5, 3),
                DifferenceQuantityMt = 3m,
                ChargeableLossMt = 2m
            },
            new LossEvent
            {
                Id = 71,
                ContractId = 2,
                ShipmentId = 10,
                TransportLegId = 31,
                Stage = LossEventStage.TransitLoss,
                EventDate = new DateTime(2026, 5, 3),
                DifferenceQuantityMt = 5m,
                ChargeableLossMt = 4m
            });
        await db.SaveChangesAsync();

        var summaryController = new ContractJourneyController(db, new StockService(db))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };

        var summaryResult = await summaryController.Details(1, tab: ContractJourneyTabs.Details.Summary);

        var summaryView = Assert.IsType<ViewResult>(summaryResult);
        var summaryModel = Assert.IsType<ContractJourneyDetailsViewModel>(summaryView.Model);
        Assert.True(summaryModel.IsInitialSummaryPayload);
        Assert.Equal(3m, summaryModel.Kpis.LossQuantityMt);
        Assert.Equal(3m, summaryModel.SummaryMetrics.LossQuantityMt);

        var costsController = new ContractJourneyController(db, new StockService(db));

        var costsResult = await costsController.Details(1, tab: ContractJourneyTabs.Details.Costs);

        var costsView = Assert.IsType<ViewResult>(costsResult);
        var costsModel = Assert.IsType<ContractJourneyDetailsViewModel>(costsView.Model);
        var loss = Assert.Single(costsModel.LossItems);
        Assert.Equal(3m, loss.DifferenceQuantityMt);
        Assert.Equal(2m, loss.ChargeableLossMt);
        Assert.Equal(3m, costsModel.Kpis.LossQuantityMt);
    }

    [Fact]
    public async Task Details_Sale_Without_Sale_Recommends_Sale()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        db.Contracts.Add(BuildSaleContract(2, "SAL-001", 300m));
        await db.SaveChangesAsync();

        var controller = new ContractJourneyController(db, new StockService(db));

        var result = await controller.Details(2);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<ContractJourneyDetailsViewModel>(view.Model);
        Assert.False(model.IsPurchaseContract);
        Assert.Equal("ثبت فروش", model.NextRecommendedActionTitle);
        Assert.Contains("/Sales/Create", model.NextRecommendedActionUrl);
        Assert.Contains("contractId=2", model.NextRecommendedActionUrl);
    }

    [Fact]
    public async Task Details_Sale_With_Sale_Without_InventoryMovement_Flags_SourceTrace_Warning()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        db.Contracts.Add(BuildSaleContract(2, "SAL-001", 300m));
        db.SalesTransactions.Add(new SalesTransaction
        {
            Id = 1,
            CompanyId = 1,
            ContractId = 2,
            CustomerId = 1,
            ProductId = 1,
            InvoiceNumber = "SAL-INV-001",
            SaleDate = new DateTime(2026, 5, 5),
            QuantityMt = 25m,
            UnitPriceUsd = 500m,
            TotalUsd = 12500m,
            SaleStage = SaleStage.TerminalStock
        });
        await db.SaveChangesAsync();

        var controller = new ContractJourneyController(db, new StockService(db));

        var result = await controller.Details(2);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<ContractJourneyDetailsViewModel>(view.Model);
        Assert.Equal(1, model.SalesWithoutTraceCount);
        Assert.Contains(model.Warnings, warning => warning.Contains("trace") || warning.Contains("ردیابی"));
    }

    [Fact]
    public async Task Details_Invalid_Tab_Falls_Back_To_Summary()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        db.Contracts.Add(BuildPurchaseContract(1, "PUR-001", 500m));
        await db.SaveChangesAsync();

        var controller = new ContractJourneyController(db, new StockService(db));

        var result = await controller.Details(1, tab: "unknown-tab");

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<ContractJourneyDetailsViewModel>(view.Model);
        Assert.Equal(ContractJourneyTabs.Details.Summary, model.ActiveTab);
    }

    [Fact]
    public async Task Details_Dashboard_Tab_Is_Normalized_To_Summary()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        db.Contracts.Add(BuildPurchaseContract(1, "PUR-001", 500m));
        await db.SaveChangesAsync();

        var controller = new ContractJourneyController(db, new StockService(db));

        var result = await controller.Details(1, tab: ContractJourneyTabs.Details.Dashboard);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<ContractJourneyDetailsViewModel>(view.Model);
        Assert.Equal(ContractJourneyTabs.Details.Summary, model.ActiveTab);
    }

    [Fact]
    public async Task Details_ManualFinalPrice_Contract_Shows_CorrectPricingMethodName_And_FormulaNote()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        db.Contracts.Add(new Contract
        {
            Id = 1,
            ContractNumber = "PUR-001",
            ContractType = ContractType.Purchase,
            Status = ContractStatus.Active,
            CompanyId = 1,
            ProductId = 1,
            SupplierId = 1,
            ContractDate = new DateTime(2026, 5, 1),
            QuantityMt = 300m,
            Currency = "USD",
            PricingMethod = PricingMethod.ManualFinalPrice,
            ManualFinalPriceUsd = 585m,
            PricingFormulaNote = "توافق شخصی"
        });
        await db.SaveChangesAsync();

        var controller = new ContractJourneyController(db, new StockService(db));

        var result = await controller.Details(1);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<ContractJourneyDetailsViewModel>(view.Model);
        Assert.Equal("نرخ قطعی / توافقی", model.PricingMethodName);
        Assert.Equal("توافق شخصی", model.PricingFormulaNote);
    }

    [Fact]
    public async Task Details_Purchase_Cancelled_WagonRent_Does_Not_Suppress_Inline_RailwayExpense()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        db.Contracts.Add(BuildPurchaseContract(1, "PUR-WAGON-CANCELLED", 100m));
        db.LoadingRegisters.Add(new LoadingRegister
        {
            Id = 1,
            ContractId = 1,
            ProductId = 1,
            LoadingDate = new DateTime(2026, 5, 2),
            LoadedQuantityMt = 10m,
            LoadingPriceUsd = 100m,
            RailwayExpenseUsd = 125m
        });
        db.ExpenseTypes.Add(new ExpenseType
        {
            Id = 1,
            Code = "WAGON_RENT",
            Name = "Wagon Rent",
            NamePersian = "کرایه واگون",
            Category = "Transport",
            IsActive = true
        });
        db.ExpenseTransactions.Add(new ExpenseTransaction
        {
            Id = 1,
            ExpenseTypeId = 1,
            ContractId = 1,
            ExpenseDate = new DateTime(2026, 5, 3),
            AmountUsd = 25m,
            Description = "Wagon Rent cancelled",
            IsCancelled = true
        });
        await db.SaveChangesAsync();

        var controller = new ContractJourneyController(db, new StockService(db));

        var result = await controller.Details(1, tab: ContractJourneyTabs.Details.Costs);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<ContractJourneyDetailsViewModel>(view.Model);
        Assert.Equal(125m, model.LoadingRailwayExpenseUsd);
        Assert.Equal(150m, model.MiniPnl.TraceableExpensesUsd);
    }

    [Fact]
    public async Task Details_Purchase_Active_WagonRent_Suppresses_Inline_RailwayExpense()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        db.Contracts.Add(BuildPurchaseContract(1, "PUR-WAGON-ACTIVE", 100m));
        db.LoadingRegisters.Add(new LoadingRegister
        {
            Id = 1,
            ContractId = 1,
            ProductId = 1,
            LoadingDate = new DateTime(2026, 5, 2),
            LoadedQuantityMt = 10m,
            LoadingPriceUsd = 100m,
            RailwayExpenseUsd = 125m
        });
        db.ExpenseTypes.Add(new ExpenseType
        {
            Id = 1,
            Code = "WAGON_RENT",
            Name = "Wagon Rent",
            NamePersian = "کرایه واگون",
            Category = "Transport",
            IsActive = true
        });
        db.ExpenseTransactions.Add(new ExpenseTransaction
        {
            Id = 1,
            ExpenseTypeId = 1,
            ContractId = 1,
            ExpenseDate = new DateTime(2026, 5, 3),
            AmountUsd = 25m,
            Description = "Wagon Rent active",
            IsCancelled = false
        });
        await db.SaveChangesAsync();

        var controller = new ContractJourneyController(db, new StockService(db));

        var result = await controller.Details(1, tab: ContractJourneyTabs.Details.Costs);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<ContractJourneyDetailsViewModel>(view.Model);
        Assert.Equal(0m, model.LoadingRailwayExpenseUsd);
        Assert.Equal(25m, model.MiniPnl.TraceableExpensesUsd);
    }

    [Fact]
    public async Task Details_RubSettlementSummary_Uses_Only_Locked_Loadings_For_Weighted_Rate()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        db.Contracts.Add(new Contract
        {
            Id = 1,
            ContractNumber = "PUR-RUB-001",
            ContractType = ContractType.Purchase,
            Status = ContractStatus.Active,
            CompanyId = 1,
            ProductId = 1,
            SupplierId = 1,
            ContractDate = new DateTime(2026, 5, 1),
            QuantityMt = 100m,
            Currency = "USD",
            PricingMethod = PricingMethod.Fixed,
            UnitPriceUsd = 100m,
            SettlementCurrencyCode = "RUB",
            RubRatePolicy = RubSettlementRatePolicy.PerLoadingRate
        });
        db.LoadingRegisters.AddRange(
            new LoadingRegister
            {
                Id = 1,
                ContractId = 1,
                ProductId = 1,
                LoadingDate = new DateTime(2026, 5, 2),
                LoadedQuantityMt = 10m,
                LoadingPriceUsd = 100m,
                SettlementCurrencyCode = "RUB",
                RubRateStatus = RubSettlementRateStatus.Locked,
                RubPerUsdRate = 80m,
                AmountUsdAtRubLock = 1000m,
                AmountRubAtRubLock = 80000m
            },
            new LoadingRegister
            {
                Id = 2,
                ContractId = 1,
                ProductId = 1,
                LoadingDate = new DateTime(2026, 5, 3),
                LoadedQuantityMt = 10m,
                LoadingPriceUsd = 100m,
                SettlementCurrencyCode = "RUB",
                RubRateStatus = RubSettlementRateStatus.Locked,
                RubPerUsdRate = 82.5m,
                AmountUsdAtRubLock = 1000m,
                AmountRubAtRubLock = 82500m
            },
            new LoadingRegister
            {
                Id = 3,
                ContractId = 1,
                ProductId = 1,
                LoadingDate = new DateTime(2026, 5, 4),
                LoadedQuantityMt = 5m,
                LoadingPriceUsd = 100m,
                SettlementCurrencyCode = "RUB",
                RubRateStatus = RubSettlementRateStatus.Pending
            });
        await db.SaveChangesAsync();

        var controller = new ContractJourneyController(db, new StockService(db));

        var result = await controller.Details(1);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<ContractJourneyDetailsViewModel>(view.Model);
        Assert.True(model.RubSettlementSummary.IsRubSettlement);
        Assert.Equal(2000m, model.RubSettlementSummary.LockedAmountUsd);
        Assert.Equal(162500m, model.RubSettlementSummary.LockedAmountRub);
        Assert.Equal(81.25m, model.RubSettlementSummary.WeightedRubPerUsdRate);
        Assert.Equal(500m, model.RubSettlementSummary.PendingAmountUsd);
        Assert.Equal(5m, model.RubSettlementSummary.PendingQuantityMt);
        Assert.Equal(2, model.RubSettlementSummary.LockedLoadingCount);
        Assert.Equal(1, model.RubSettlementSummary.PendingRateLoadingCount);
    }

    [Fact]
    public async Task Details_LoadingItems_Keep_Ruble_Amounts_Imported_From_Excel()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        db.Contracts.Add(new Contract
        {
            Id = 1,
            ContractNumber = "PUR-RUB-FILE-001",
            ContractType = ContractType.Purchase,
            Status = ContractStatus.Active,
            CompanyId = 1,
            ProductId = 1,
            SupplierId = 1,
            ContractDate = new DateTime(2026, 5, 1),
            QuantityMt = 100m,
            Currency = "USD",
            PricingMethod = PricingMethod.Fixed,
            UnitPriceUsd = 100m,
            SettlementCurrencyCode = "RUB",
            RubRatePolicy = RubSettlementRatePolicy.PerLoadingRate
        });
        db.LoadingRegisters.Add(new LoadingRegister
        {
            Id = 1,
            ContractId = 1,
            ProductId = 1,
            LoadingDate = new DateTime(2026, 5, 2),
            LoadedQuantityMt = 10m,
            LoadingPriceUsd = 100m,
            SettlementCurrencyCode = "RUB",
            RubRateStatus = RubSettlementRateStatus.Pending,
            SettlementUnitPriceRub = 8100m,
            SettlementValueRub = 81000m
        });
        await db.SaveChangesAsync();

        var controller = new ContractJourneyController(db, new StockService(db));

        var result = await controller.Details(1);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<ContractJourneyDetailsViewModel>(view.Model);
        var loading = Assert.Single(model.LoadingItems);
        Assert.True(loading.HasFileRub);
        Assert.Equal(8100m, loading.SettlementUnitPriceRub);
        Assert.Equal(81000m, loading.SettlementValueRub);
        Assert.Equal(81000m, loading.FileRubAmount);
        Assert.Null(loading.RubPerUsdRate);
    }

    [Fact]
    public async Task Details_SarrafSettlements_Show_Exact_Rub_Reduction_From_Ledger()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        db.Sarrafs.Add(new Sarraf { Id = 1, Name = "Sarraf A" });
        db.Contracts.Add(BuildPurchaseContract(1, "PUR-RUB-1", 100m));
        db.SarrafSettlements.Add(new SarrafSettlement
        {
            Id = 1,
            SettlementDate = new DateTime(2026, 1, 5),
            SarrafId = 1,
            SupplierId = 1,
            ContractId = 1,
            RequestedAmount = 140_000m,
            RequestedCurrency = "RUB",
            RequestedFxRateToUsd = 0.0125m,
            RequestedAmountUsd = 1_750m,
            SarrafChargedAmount = 140_000m,
            SarrafCurrency = "RUB",
            SarrafFxRateToUsd = 0.0125m,
            SarrafChargedAmountUsd = 1_750m,
            SupplierAcceptedAmount = 137_500m,
            SupplierAcceptedCurrency = "RUB",
            SupplierAcceptedFxRateToUsd = 0.0125m,
            SupplierAcceptedAmountUsd = 1_718.75m,
            DifferenceAmountUsd = 31.25m,
            DifferenceType = SarrafSettlementDifferenceType.Loss,
            DifferenceTreatment = SarrafSettlementDifferenceTreatment.RecognizeExchangeGainLoss,
            Status = SarrafSettlementStatus.Posted,
            LedgerEntryId = 1
        });
        db.LedgerEntries.Add(new LedgerEntry
        {
            Id = 1,
            EntryDate = new DateTime(2026, 1, 5),
            Side = LedgerSide.Debit,
            AmountUsd = 1_750m,
            Currency = "USD",
            SourceAmount = 140_000m,
            SourceCurrencyCode = "RUB",
            AppliedFxRateToUsd = 0.0125m,
            SourceType = SarrafSettlementService.SupplierLedgerSourceType,
            SourceId = 1,
            Description = "Sarraf settlement supplier reduction",
            SupplierId = 1,
            ContractId = 1
        });
        await db.SaveChangesAsync();

        var controller = new ContractJourneyController(db, new StockService(db));

        var result = await controller.Details(1);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<ContractJourneyDetailsViewModel>(view.Model);
        var settlement = Assert.Single(model.SarrafSettlementItems);
        Assert.Equal(1_750m, settlement.SupplierReductionAmountUsd);
        Assert.Equal(140_000m, settlement.SupplierReductionAmountRub);
    }

    private static DbContextOptions<ApplicationDbContext> NewDbOptions()
        => new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

    private static void SeedReferenceData(ApplicationDbContext db)
    {
        db.Companies.Add(new Company { Id = 1, Code = "PTG", Name = "PTG" });
        db.Products.Add(new Product { Id = 1, Code = "GO", Name = "Gas Oil" });
        db.Customers.Add(new Customer { Id = 1, Name = "Customer A" });
        db.Suppliers.Add(new Supplier { Id = 1, Name = "Supplier A" });
        db.Terminals.Add(new Terminal { Id = 1, Code = "ILK", Name = "Ilinka" });
        db.StorageTanks.Add(new StorageTank { Id = 1, TerminalId = 1, TankCode = "TK-1", ProductId = 1, CapacityMt = 1000m });
        db.Trucks.Add(new Truck { Id = 1, PlateNumber = "TRK-01" });
        db.Drivers.Add(new Driver { Id = 1, FullName = "Driver A" });
        db.CashAccounts.Add(new CashAccount
        {
            Id = 1,
            Code = "BANK-USD",
            Name = "Main USD Bank",
            AccountType = CashAccountType.Bank,
            Currency = "USD",
            IsActive = true
        });
    }

    private static Contract BuildPurchaseContract(int id, string contractNumber, decimal quantityMt)
        => new()
        {
            Id = id,
            ContractNumber = contractNumber,
            ContractType = ContractType.Purchase,
            Status = ContractStatus.Active,
            CompanyId = 1,
            ProductId = 1,
            SupplierId = 1,
            ContractDate = new DateTime(2026, 5, 1),
            QuantityMt = quantityMt,
            Currency = "USD",
            PricingMethod = PricingMethod.Fixed,
            UnitPriceUsd = 400m
        };

    private static Contract BuildSaleContract(int id, string contractNumber, decimal quantityMt)
        => new()
        {
            Id = id,
            ContractNumber = contractNumber,
            ContractType = ContractType.Sale,
            Status = ContractStatus.Active,
            CompanyId = 1,
            ProductId = 1,
            CustomerId = 1,
            ContractDate = new DateTime(2026, 5, 1),
            QuantityMt = quantityMt,
            Currency = "USD",
            PricingMethod = PricingMethod.Fixed,
            UnitPriceUsd = 500m
        };
}
