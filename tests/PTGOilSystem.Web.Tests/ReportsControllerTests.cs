using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Controllers;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.Reports;
using Xunit;

namespace PTGOilSystem.Web.Tests;

public class ReportsControllerTests
{
    [Fact]
    public async Task ContractPnl_Uses_Priced_Loading_Snapshots_And_Separates_Pending_Loadings()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        db.Contracts.Add(new Contract
        {
            Id = 3,
            ContractNumber = "PUR-PLATTS",
            ContractType = ContractType.Purchase,
            CompanyId = 1,
            ProductId = 1,
            SupplierId = 1,
            ContractDate = new DateTime(2026, 4, 1),
            QuantityMt = 150m,
            PricingMethod = PricingMethod.FormulaPlatts
        });
        db.LoadingRegisters.AddRange(
            new LoadingRegister
            {
                Id = 1,
                ContractId = 3,
                ProductId = 1,
                LoadingDate = new DateTime(2026, 4, 1),
                LoadedQuantityMt = 50m,
                LoadingPriceUsd = 200m
            },
            new LoadingRegister
            {
                Id = 2,
                ContractId = 3,
                ProductId = 1,
                LoadingDate = new DateTime(2026, 4, 2),
                LoadedQuantityMt = 50m,
                LoadingPriceUsd = 300m
            },
            new LoadingRegister
            {
                Id = 3,
                ContractId = 3,
                ProductId = 1,
                LoadingDate = new DateTime(2026, 4, 3),
                LoadedQuantityMt = 10m,
                LoadingPriceUsd = null
            });
        await db.SaveChangesAsync();

        var controller = new ReportsController(db);

        var result = await controller.ContractPnl(new ManagementReportFilterViewModel
        {
            ContractId = 3
        });

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<ContractPnlReportViewModel>(view.Model);
        var row = Assert.Single(model.PurchaseRows);
        Assert.Equal(110m, row.TotalLoadedMt);
        Assert.Equal(100m, row.PricedLoadedMt);
        Assert.Equal(10m, row.PendingLoadedMt);
        Assert.Equal(1, row.PendingLoadingCount);
        Assert.Equal(25_000m, row.PurchaseValueUsd);
        Assert.Equal(250m, row.AveragePurchasePriceUsd);
        Assert.True(row.NeedsReview);
        Assert.True(model.HasPendingPurchasePricing);
    }

    [Fact]
    public async Task ContractPnl_Uses_Contract_Final_Price_For_Loadings_With_Missing_Snapshot_Price()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        db.Contracts.Add(new Contract
        {
            Id = 3,
            ContractNumber = "PUR-PRICED",
            ContractType = ContractType.Purchase,
            CompanyId = 1,
            ProductId = 1,
            SupplierId = 1,
            ContractDate = new DateTime(2026, 4, 1),
            QuantityMt = 150m,
            PricingMethod = PricingMethod.ManualFinalPrice,
            ManualFinalPriceUsd = 585m
        });
        db.LoadingRegisters.Add(new LoadingRegister
        {
            Id = 1,
            ContractId = 3,
            ProductId = 1,
            LoadingDate = new DateTime(2026, 4, 1),
            LoadedQuantityMt = 40m,
            LoadingPriceUsd = null
        });
        await db.SaveChangesAsync();

        var controller = new ReportsController(db);

        var result = await controller.ContractPnl(new ManagementReportFilterViewModel
        {
            ContractId = 3
        });

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<ContractPnlReportViewModel>(view.Model);
        var row = Assert.Single(model.PurchaseRows);
        Assert.Equal(40m, row.PricedLoadedMt);
        Assert.Equal(0m, row.PendingLoadedMt);
        Assert.Equal(0, row.PendingLoadingCount);
        Assert.Equal(23_400m, row.PurchaseValueUsd);
        Assert.Equal(585m, row.AveragePurchasePriceUsd);
        Assert.False(row.NeedsReview);
        Assert.False(model.HasPendingPurchasePricing);
    }

    [Fact]
    public async Task ContractPnl_PurchaseValue_Remains_The_Same_After_Aggregation_Service()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        db.Contracts.Add(new Contract
        {
            Id = 13,
            ContractNumber = "PUR-AGG",
            ContractType = ContractType.Purchase,
            CompanyId = 1,
            ProductId = 1,
            SupplierId = 1,
            ContractDate = new DateTime(2026, 4, 1),
            QuantityMt = 50m,
            PricingMethod = PricingMethod.Fixed,
            UnitPriceUsd = null
        });
        db.ExpenseTypes.Add(new ExpenseType { Id = 30, Code = "GEN", Name = "General" });
        db.LoadingRegisters.AddRange(
            new LoadingRegister
            {
                Id = 130,
                ContractId = 13,
                ProductId = 1,
                LoadingDate = new DateTime(2026, 4, 1),
                LoadedQuantityMt = 10m,
                LoadingPriceUsd = 100m,
                TransportExpenseUsd = 50m,
                WarehouseExpenseUsd = 25m,
                OtherExpenseUsd = 5m,
                RailwayExpenseUsd = 10m
            },
            new LoadingRegister
            {
                Id = 131,
                ContractId = 13,
                ProductId = 1,
                LoadingDate = new DateTime(2026, 4, 2),
                LoadedQuantityMt = 5m,
                LoadingPriceUsd = 200m
            },
            new LoadingRegister
            {
                Id = 132,
                ContractId = 13,
                ProductId = 1,
                LoadingDate = new DateTime(2026, 4, 3),
                LoadedQuantityMt = 3m,
                LoadingPriceUsd = null
            });
        db.ExpenseTransactions.Add(new ExpenseTransaction
        {
            Id = 1300,
            ContractId = 13,
            ExpenseTypeId = 30,
            ExpenseDate = new DateTime(2026, 4, 4),
            AmountUsd = 30m
        });
        await db.SaveChangesAsync();

        var controller = new ReportsController(db);

        var result = await controller.ContractPnl(new ManagementReportFilterViewModel { ContractId = 13 });

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<ContractPnlReportViewModel>(view.Model);
        var row = Assert.Single(model.PurchaseRows);
        Assert.Equal(18m, row.TotalLoadedMt);
        Assert.Equal(15m, row.PricedLoadedMt);
        Assert.Equal(3m, row.PendingLoadedMt);
        Assert.Equal(1, row.PendingLoadingCount);
        Assert.Equal(2_000m, row.PurchaseValueUsd);
        Assert.Equal(50m, row.TransportCostUsd);
        Assert.Equal(25m, row.WarehouseCostUsd);
        Assert.Equal(5m, row.OtherCostUsd);
        Assert.Equal(10m, row.RailwayCostUsd);
        Assert.Equal(30m, row.GeneralExpenseCostUsd);
        Assert.Equal(2_120m, row.TotalCostUsd);
    }

    [Fact]
    public async Task ContractPnl_PurchaseValue_And_LoadedQuantity_Ignore_InventoryTransportLeg()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        db.Contracts.Add(new Contract
        {
            Id = 14,
            ContractNumber = "PUR-LEG",
            ContractType = ContractType.Purchase,
            CompanyId = 1,
            ProductId = 1,
            SupplierId = 1,
            ContractDate = new DateTime(2026, 4, 1),
            QuantityMt = 100m,
            PricingMethod = PricingMethod.Fixed,
            UnitPriceUsd = null
        });
        db.LoadingRegisters.Add(new LoadingRegister
        {
            Id = 140,
            ContractId = 14,
            ProductId = 1,
            LoadingDate = new DateTime(2026, 4, 1),
            LoadedQuantityMt = 10m,
            LoadingPriceUsd = 100m
        });
        db.InventoryTransportLegs.Add(new InventoryTransportLeg
        {
            Id = 1400,
            SourcePurchaseContractId = 14,
            ProductId = 1,
            SourceTerminalId = 1,
            TransportType = LoadingTransportType.Wagon,
            WagonNumber = "WGN-1400",
            RwbNo = "RWB-1400",
            LoadedDate = new DateTime(2026, 4, 2),
            QuantityMt = 25m,
            Status = InventoryTransportLegStatus.Loaded
        });
        await db.SaveChangesAsync();

        var controller = new ReportsController(db);

        var result = await controller.ContractPnl(new ManagementReportFilterViewModel { ContractId = 14 });

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<ContractPnlReportViewModel>(view.Model);
        var row = Assert.Single(model.PurchaseRows);
        Assert.Equal(10m, row.TotalLoadedMt);
        Assert.Equal(10m, row.PricedLoadedMt);
        Assert.Equal(1_000m, row.PurchaseValueUsd);
        Assert.Equal(1_000m, row.TotalCostUsd);
    }

    [Fact]
    public async Task ContractPnl_Includes_TransportLeg_Expense_As_GeneralExpense_Without_Changing_Purchase()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        db.Contracts.Add(new Contract
        {
            Id = 15,
            ContractNumber = "PUR-LEG-EXP",
            ContractType = ContractType.Purchase,
            CompanyId = 1,
            ProductId = 1,
            SupplierId = 1,
            ContractDate = new DateTime(2026, 4, 1),
            QuantityMt = 100m,
            PricingMethod = PricingMethod.Fixed
        });
        db.Terminals.Add(new Terminal { Id = 15, Code = "T15", Name = "Terminal 15" });
        db.LoadingRegisters.Add(new LoadingRegister
        {
            Id = 150,
            ContractId = 15,
            ProductId = 1,
            LoadingDate = new DateTime(2026, 4, 1),
            LoadedQuantityMt = 10m,
            LoadingPriceUsd = 100m
        });
        db.InventoryTransportLegs.Add(new InventoryTransportLeg
        {
            Id = 1500,
            SourcePurchaseContractId = 15,
            ProductId = 1,
            SourceTerminalId = 15,
            TransportType = LoadingTransportType.Wagon,
            LoadedDate = new DateTime(2026, 4, 2),
            QuantityMt = 25m,
            Status = InventoryTransportLegStatus.Loaded
        });
        db.ExpenseTypes.Add(new ExpenseType { Id = 150, Code = "WGN", Name = "Wagon expense" });
        db.ExpenseTransactions.Add(new ExpenseTransaction
        {
            Id = 1500,
            ExpenseTypeId = 150,
            ContractId = 15,
            TransportLegId = 1500,
            ExpenseDate = new DateTime(2026, 4, 3),
            Amount = 275m,
            Currency = "USD",
            AmountUsd = 275m,
            Description = "Wagon border expense"
        });
        await db.SaveChangesAsync();

        var controller = new ReportsController(db);

        var result = await controller.ContractPnl(new ManagementReportFilterViewModel { ContractId = 15 });

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<ContractPnlReportViewModel>(view.Model);
        var row = Assert.Single(model.PurchaseRows);
        Assert.Equal(10m, row.TotalLoadedMt);
        Assert.Equal(1_000m, row.PurchaseValueUsd);
        Assert.Equal(275m, row.GeneralExpenseCostUsd);
        Assert.Equal(1_275m, row.TotalCostUsd);
    }

    [Fact]
    public async Task ContractPnl_Excludes_Cancelled_TransportLeg_Expense()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        db.Contracts.Add(new Contract
        {
            Id = 16,
            ContractNumber = "PUR-LEG-CANCEL",
            ContractType = ContractType.Purchase,
            CompanyId = 1,
            ProductId = 1,
            SupplierId = 1,
            ContractDate = new DateTime(2026, 4, 1),
            QuantityMt = 100m,
            PricingMethod = PricingMethod.Fixed
        });
        db.Terminals.Add(new Terminal { Id = 16, Code = "T16", Name = "Terminal 16" });
        db.LoadingRegisters.Add(new LoadingRegister
        {
            Id = 160,
            ContractId = 16,
            ProductId = 1,
            LoadingDate = new DateTime(2026, 4, 1),
            LoadedQuantityMt = 10m,
            LoadingPriceUsd = 100m
        });
        db.InventoryTransportLegs.Add(new InventoryTransportLeg
        {
            Id = 1600,
            SourcePurchaseContractId = 16,
            ProductId = 1,
            SourceTerminalId = 16,
            TransportType = LoadingTransportType.Wagon,
            LoadedDate = new DateTime(2026, 4, 2),
            QuantityMt = 25m,
            Status = InventoryTransportLegStatus.Loaded
        });
        db.ExpenseTypes.Add(new ExpenseType { Id = 160, Code = "WGN", Name = "Wagon expense" });
        db.ExpenseTransactions.Add(new ExpenseTransaction
        {
            Id = 1600,
            ExpenseTypeId = 160,
            ContractId = 16,
            TransportLegId = 1600,
            ExpenseDate = new DateTime(2026, 4, 3),
            Amount = 275m,
            Currency = "USD",
            AmountUsd = 275m,
            IsCancelled = true
        });
        await db.SaveChangesAsync();

        var controller = new ReportsController(db);

        var result = await controller.ContractPnl(new ManagementReportFilterViewModel { ContractId = 16 });

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<ContractPnlReportViewModel>(view.Model);
        var row = Assert.Single(model.PurchaseRows);
        Assert.Equal(0m, row.GeneralExpenseCostUsd);
        Assert.Equal(1_000m, row.TotalCostUsd);
    }

    [Fact]
    public async Task ContractPnl_Includes_TransportLeg_Customs_As_CustomsCost_Without_Changing_Purchase()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        db.Contracts.Add(new Contract
        {
            Id = 17,
            ContractNumber = "PUR-TLCUSTOMS",
            ContractType = ContractType.Purchase,
            CompanyId = 1,
            ProductId = 1,
            SupplierId = 1,
            ContractDate = new DateTime(2026, 4, 1),
            QuantityMt = 100m,
            PricingMethod = PricingMethod.Fixed,
            UnitPriceUsd = 500m
        });
        db.InventoryTransportLegs.Add(new InventoryTransportLeg
        {
            Id = 1700,
            SourcePurchaseContractId = 17,
            ProductId = 1,
            SourceTerminalId = 1,
            TransportType = LoadingTransportType.Wagon,
            LoadedDate = new DateTime(2026, 4, 2),
            QuantityMt = 30m,
            Status = InventoryTransportLegStatus.Loaded
        });
        db.LoadingRegisters.Add(new LoadingRegister
        {
            Id = 170,
            ContractId = 17,
            ProductId = 1,
            LoadingDate = new DateTime(2026, 4, 1),
            LoadedQuantityMt = 10m,
            LoadingPriceUsd = 100m
        });
        db.CustomsDeclarations.Add(new CustomsDeclaration
        {
            Id = 1700,
            LoadingRegisterId = null,
            TransportLegId = 1700,
            DeclarationDate = new DateTime(2026, 4, 3),
            TotalAfn = 70_000m,
            TotalUsd = 1_400m
        });
        await db.SaveChangesAsync();

        var controller = new ReportsController(db);
        var result = await controller.ContractPnl(new ManagementReportFilterViewModel { ContractId = 17 });

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<ContractPnlReportViewModel>(view.Model);
        var row = Assert.Single(model.PurchaseRows);
        Assert.Equal(10m, row.TotalLoadedMt);
        Assert.Equal(1_000m, row.PurchaseValueUsd);
        Assert.Equal(1_400m, row.CustomsCostUsd);
        Assert.Equal(2_400m, row.TotalCostUsd);
    }

    [Fact]
    public async Task ContractPnl_TransportLeg_Customs_Does_Not_Change_PurchaseValue()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        db.Contracts.Add(new Contract
        {
            Id = 18,
            ContractNumber = "PUR-TLCUSTOMS2",
            ContractType = ContractType.Purchase,
            CompanyId = 1,
            ProductId = 1,
            SupplierId = 1,
            ContractDate = new DateTime(2026, 4, 1),
            QuantityMt = 100m,
            PricingMethod = PricingMethod.Fixed,
            UnitPriceUsd = 500m
        });
        db.LoadingRegisters.Add(new LoadingRegister
        {
            Id = 180,
            ContractId = 18,
            ProductId = 1,
            LoadingDate = new DateTime(2026, 4, 1),
            LoadedQuantityMt = 12m,
            LoadingPriceUsd = 125m
        });
        db.InventoryTransportLegs.Add(new InventoryTransportLeg
        {
            Id = 1800,
            SourcePurchaseContractId = 18,
            ProductId = 1,
            SourceTerminalId = 1,
            TransportType = LoadingTransportType.Wagon,
            LoadedDate = new DateTime(2026, 4, 2),
            QuantityMt = 40m,
            Status = InventoryTransportLegStatus.Loaded
        });
        db.CustomsDeclarations.Add(new CustomsDeclaration
        {
            Id = 1800,
            TransportLegId = 1800,
            DeclarationDate = new DateTime(2026, 4, 3),
            TotalUsd = 800m
        });
        await db.SaveChangesAsync();

        var controller = new ReportsController(db);
        var result = await controller.ContractPnl(new ManagementReportFilterViewModel { ContractId = 18 });

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<ContractPnlReportViewModel>(view.Model);
        var row = Assert.Single(model.PurchaseRows);
        Assert.Equal(12m, row.TotalLoadedMt);
        Assert.Equal(1_500m, row.PurchaseValueUsd);
        Assert.Equal(800m, row.CustomsCostUsd);
    }

    [Fact]
    public async Task ContractPnl_Includes_DirectSale_Revenue_From_LoadingReceiptAllocation_For_PurchaseContract()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        db.Contracts.Add(new Contract
        {
            Id = 3,
            ContractNumber = "PUR-DS",
            ContractType = ContractType.Purchase,
            CompanyId = 1,
            ProductId = 1,
            SupplierId = 1,
            ContractDate = new DateTime(2026, 4, 1),
            QuantityMt = 100m,
            PricingMethod = PricingMethod.Fixed,
            UnitPriceUsd = 300m
        });
        db.LoadingRegisters.Add(new LoadingRegister
        {
            Id = 1,
            ContractId = 3,
            ProductId = 1,
            LoadingDate = new DateTime(2026, 4, 1),
            LoadedQuantityMt = 25m,
            LoadingPriceUsd = 300m
        });
        db.LoadingReceipts.Add(new LoadingReceipt
        {
            Id = 1,
            LoadingRegisterId = 1,
            TerminalId = 1,
            ReceiptDate = new DateTime(2026, 4, 2),
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
            SaleDate = new DateTime(2026, 4, 3),
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
            SourcePurchaseContractId = 3,
            TerminalId = 1,
            SalesTransactionId = 1
        });
        await db.SaveChangesAsync();

        var controller = new ReportsController(db);

        var result = await controller.ContractPnl(new ManagementReportFilterViewModel
        {
            ContractId = 3
        });

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<ContractPnlReportViewModel>(view.Model);
        var row = Assert.Single(model.PurchaseRows);
        Assert.Equal(25m, row.TotalSoldMt);
        Assert.Equal(12500m, row.TotalRevenueUsd);
        Assert.Equal(7500m, row.PurchaseValueUsd);
        Assert.Equal(5000m, row.GrossMarginUsd);
        Assert.Equal(12500m, model.TotalDirectSaleRevenueUsd);
        Assert.Equal(0m, model.TotalSalesRevenueUsd);
        Assert.Equal(0m, model.TotalGrossMarginUsd);
        Assert.Empty(model.SaleRows);
    }

    [Fact]
    public async Task ContractPnl_Does_Not_Create_Revenue_For_DirectFromReceipt_Dispatch_Without_Sale()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        db.Contracts.Add(new Contract
        {
            Id = 3,
            ContractNumber = "PUR-DD",
            ContractType = ContractType.Purchase,
            CompanyId = 1,
            ProductId = 1,
            SupplierId = 1,
            ContractDate = new DateTime(2026, 4, 1),
            QuantityMt = 100m,
            PricingMethod = PricingMethod.Fixed,
            UnitPriceUsd = 300m
        });
        db.Trucks.Add(new Truck { Id = 1, PlateNumber = "TRK-1" });
        db.LoadingRegisters.Add(new LoadingRegister
        {
            Id = 1,
            ContractId = 3,
            ProductId = 1,
            LoadingDate = new DateTime(2026, 4, 1),
            LoadedQuantityMt = 25m,
            LoadingPriceUsd = 300m
        });
        db.LoadingReceipts.Add(new LoadingReceipt
        {
            Id = 1,
            LoadingRegisterId = 1,
            TerminalId = 1,
            ReceiptDate = new DateTime(2026, 4, 2),
            ReceivedQuantityMt = 25m,
            ReceiptDestination = LoadingReceiptDestination.Mixed
        });
        db.LoadingReceiptAllocations.Add(new LoadingReceiptAllocation
        {
            Id = 1,
            LoadingReceiptId = 1,
            Destination = LoadingReceiptAllocationDestination.DirectDispatchToTruck,
            Status = LoadingReceiptAllocationStatus.Completed,
            QuantityMt = 25m,
            SourcePurchaseContractId = 3,
            TerminalId = 1
        });
        db.TruckDispatches.Add(new TruckDispatch
        {
            Id = 1,
            DispatchMode = TruckDispatchMode.DirectFromReceipt,
            LoadingReceiptAllocationId = 1,
            ContractId = 3,
            ProductId = 1,
            TruckId = 1,
            DispatchDate = new DateTime(2026, 4, 3),
            Status = DispatchStatus.Loaded,
            LoadedQuantityMt = 25m
        });
        await db.SaveChangesAsync();

        var controller = new ReportsController(db);

        var result = await controller.ContractPnl(new ManagementReportFilterViewModel
        {
            ContractId = 3
        });

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<ContractPnlReportViewModel>(view.Model);
        var row = Assert.Single(model.PurchaseRows);
        Assert.Equal(0m, row.TotalSoldMt);
        Assert.Equal(0m, row.TotalRevenueUsd);
        Assert.Equal(0m, model.TotalSalesRevenueUsd);
        Assert.Empty(model.SaleRows);
        Assert.Empty(await db.InventoryMovements.ToListAsync());
        Assert.Empty(await db.LedgerEntries.ToListAsync());
    }

    [Fact]
    public async Task ContractPnl_Includes_ExpenseTransaction_Linked_To_Purchase_Contract()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        db.Contracts.Add(new Contract
        {
            Id = 5,
            ContractNumber = "PUR-EXP",
            ContractType = ContractType.Purchase,
            CompanyId = 1,
            ProductId = 1,
            SupplierId = 1,
            ContractDate = new DateTime(2026, 4, 1),
            QuantityMt = 100m,
            PricingMethod = PricingMethod.Fixed,
            UnitPriceUsd = null
        });
        db.ExpenseTypes.Add(new ExpenseType { Id = 10, Code = "COMM", Name = "Commission" });
        db.ExpenseTransactions.Add(new ExpenseTransaction
        {
            Id = 1,
            ExpenseTypeId = 10,
            ContractId = 5,
            ExpenseDate = new DateTime(2026, 4, 5),
            Amount = 100m,
            Currency = "USD",
            AmountUsd = 100m,
            Description = "Broker fee"
        });
        await db.SaveChangesAsync();

        var controller = new ReportsController(db);
        var result = await controller.ContractPnl(new ManagementReportFilterViewModel { ContractId = 5 });

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<ContractPnlReportViewModel>(view.Model);
        var row = Assert.Single(model.PurchaseRows);
        Assert.Equal(100m, row.GeneralExpenseCostUsd);
        Assert.Equal(100m, row.TotalCostUsd);
    }

    [Fact]
    public async Task ContractPnl_ServiceProvider_Linked_Expense_Is_Counted_Once_As_GeneralExpense()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        db.Contracts.Add(new Contract
        {
            Id = 50,
            ContractNumber = "PUR-SP-EXP",
            ContractType = ContractType.Purchase,
            CompanyId = 1,
            ProductId = 1,
            SupplierId = 1,
            ContractDate = new DateTime(2026, 4, 1),
            QuantityMt = 100m,
            PricingMethod = PricingMethod.Fixed,
            UnitPriceUsd = 100m
        });
        db.ServiceProviders.Add(new PTGOilSystem.Web.Models.Entities.ServiceProvider
        {
            Id = 1,
            Name = "Wagon Rent Provider",
            ProviderType = ServiceProviderType.WagonRent,
            IsActive = true
        });
        db.ExpenseTypes.Add(new ExpenseType { Id = 50, Code = "COMM", Name = "Commission" });
        db.ExpenseTransactions.Add(new ExpenseTransaction
        {
            Id = 50,
            ExpenseTypeId = 50,
            ContractId = 50,
            ServiceProviderId = 1,
            ExpenseDate = new DateTime(2026, 4, 5),
            Amount = 120m,
            Currency = "USD",
            AmountUsd = 120m,
            Description = "Provider commission"
        });
        await db.SaveChangesAsync();

        var controller = new ReportsController(db);
        var result = await controller.ContractPnl(new ManagementReportFilterViewModel { ContractId = 50 });

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<ContractPnlReportViewModel>(view.Model);
        var row = Assert.Single(model.PurchaseRows);
        Assert.Equal(120m, row.GeneralExpenseCostUsd);
        Assert.Equal(0m, row.RailwayCostUsd);
        Assert.Equal(120m, row.TotalCostUsd);
    }

    [Fact]
    public async Task ContractPnl_Excludes_ExpenseTransaction_Linked_To_Sales_Contract()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        db.Contracts.Add(new Contract
        {
            Id = 6,
            ContractNumber = "PUR-CLEAN",
            ContractType = ContractType.Purchase,
            CompanyId = 1,
            ProductId = 1,
            SupplierId = 1,
            ContractDate = new DateTime(2026, 4, 1),
            QuantityMt = 100m,
            PricingMethod = PricingMethod.Fixed,
            UnitPriceUsd = 500m
        });
        db.ExpenseTypes.Add(new ExpenseType { Id = 10, Code = "COMM", Name = "Commission" });
        // Expense bound to a Sales contract (Id=1, seeded as ContractType.Sale) — must not leak into Purchase P&L.
        db.ExpenseTransactions.Add(new ExpenseTransaction
        {
            Id = 2,
            ExpenseTypeId = 10,
            ContractId = 1,
            ExpenseDate = new DateTime(2026, 4, 6),
            Amount = 50m,
            Currency = "USD",
            AmountUsd = 50m
        });
        await db.SaveChangesAsync();

        var controller = new ReportsController(db);
        var result = await controller.ContractPnl(new ManagementReportFilterViewModel { ContractId = 6 });

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<ContractPnlReportViewModel>(view.Model);
        var row = Assert.Single(model.PurchaseRows);
        Assert.Equal(0m, row.GeneralExpenseCostUsd);
    }

    [Fact]
    public async Task ContractPnl_Excludes_ExpenseTransaction_Without_ContractId_And_Cancelled()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        db.Contracts.Add(new Contract
        {
            Id = 7,
            ContractNumber = "PUR-MIXED",
            ContractType = ContractType.Purchase,
            CompanyId = 1,
            ProductId = 1,
            SupplierId = 1,
            ContractDate = new DateTime(2026, 4, 1),
            QuantityMt = 100m,
            PricingMethod = PricingMethod.Fixed,
            UnitPriceUsd = 500m
        });
        db.ExpenseTypes.Add(new ExpenseType { Id = 10, Code = "COMM", Name = "Commission" });
        db.ExpenseTransactions.AddRange(
            new ExpenseTransaction
            {
                Id = 3,
                ExpenseTypeId = 10,
                ContractId = 7,
                ExpenseDate = new DateTime(2026, 4, 7),
                Amount = 200m,
                Currency = "USD",
                AmountUsd = 200m
            }, // counted
            new ExpenseTransaction
            {
                Id = 4,
                ExpenseTypeId = 10,
                ContractId = null, // unallocated — not counted
                ExpenseDate = new DateTime(2026, 4, 8),
                Amount = 300m,
                Currency = "USD",
                AmountUsd = 300m
            },
            new ExpenseTransaction
            {
                Id = 5,
                ExpenseTypeId = 10,
                ContractId = 7,
                ExpenseDate = new DateTime(2026, 4, 9),
                Amount = 400m,
                Currency = "USD",
                AmountUsd = 400m,
                IsCancelled = true // cancelled — not counted
            });
        await db.SaveChangesAsync();

        var controller = new ReportsController(db);
        var result = await controller.ContractPnl(new ManagementReportFilterViewModel { ContractId = 7 });

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<ContractPnlReportViewModel>(view.Model);
        var row = Assert.Single(model.PurchaseRows);
        Assert.Equal(200m, row.GeneralExpenseCostUsd);
    }

    [Fact]
    public async Task ContractPnl_Values_Loss_From_LoadingPriceUsd_For_Purchase_Contract()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        db.Contracts.Add(new Contract
        {
            Id = 8,
            ContractNumber = "PUR-LOSS",
            ContractType = ContractType.Purchase,
            CompanyId = 1,
            ProductId = 1,
            SupplierId = 1,
            ContractDate = new DateTime(2026, 4, 1),
            QuantityMt = 100m,
            PricingMethod = PricingMethod.Fixed,
            UnitPriceUsd = 500m
        });
        db.LoadingRegisters.Add(new LoadingRegister
        {
            Id = 100,
            ContractId = 8,
            ProductId = 1,
            LoadingDate = new DateTime(2026, 4, 1),
            LoadedQuantityMt = 50m,
            LoadingPriceUsd = 600m
        });
        db.LossEvents.Add(new LossEvent
        {
            Id = 1,
            ProductId = 1,
            ContractId = 8,
            LoadingRegisterId = 100,
            EventDate = new DateTime(2026, 4, 5),
            ChargeableLossMt = 2m
        });
        await db.SaveChangesAsync();

        var controller = new ReportsController(db);
        var result = await controller.ContractPnl(new ManagementReportFilterViewModel { ContractId = 8 });

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<ContractPnlReportViewModel>(view.Model);
        var row = Assert.Single(model.PurchaseRows);
        Assert.Equal(1200m, row.LossCostUsd); // 2 MT × 600 USD/MT
        Assert.Equal(0, row.UnvaluedLossCount);
    }

    [Fact]
    public async Task ContractPnl_Excludes_Loss_Without_LoadingPrice_And_Reports_It_As_Unvalued()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        db.Contracts.Add(new Contract
        {
            Id = 9,
            ContractNumber = "PUR-LOSS-NP",
            ContractType = ContractType.Purchase,
            CompanyId = 1,
            ProductId = 1,
            SupplierId = 1,
            ContractDate = new DateTime(2026, 4, 1),
            QuantityMt = 100m,
            PricingMethod = PricingMethod.Fixed,
            UnitPriceUsd = null
        });
        db.LoadingRegisters.Add(new LoadingRegister
        {
            Id = 110,
            ContractId = 9,
            ProductId = 1,
            LoadingDate = new DateTime(2026, 4, 1),
            LoadedQuantityMt = 50m,
            LoadingPriceUsd = null // not yet priced
        });
        db.LossEvents.Add(new LossEvent
        {
            Id = 2,
            ProductId = 1,
            ContractId = 9,
            LoadingRegisterId = 110,
            EventDate = new DateTime(2026, 4, 5),
            ChargeableLossMt = 3m
        });
        await db.SaveChangesAsync();

        var controller = new ReportsController(db);
        var result = await controller.ContractPnl(new ManagementReportFilterViewModel { ContractId = 9 });

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<ContractPnlReportViewModel>(view.Model);
        var row = Assert.Single(model.PurchaseRows);
        Assert.Equal(0m, row.LossCostUsd);
        Assert.Equal(1, row.UnvaluedLossCount);
    }

    [Fact]
    public async Task ContractPnl_Excludes_Cancelled_Loss_And_Loss_On_Sales_Contract()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        db.Contracts.Add(new Contract
        {
            Id = 11,
            ContractNumber = "PUR-LOSS-CLEAN",
            ContractType = ContractType.Purchase,
            CompanyId = 1,
            ProductId = 1,
            SupplierId = 1,
            ContractDate = new DateTime(2026, 4, 1),
            QuantityMt = 100m,
            PricingMethod = PricingMethod.Fixed,
            UnitPriceUsd = 500m
        });
        db.LoadingRegisters.AddRange(
            new LoadingRegister
            {
                Id = 120,
                ContractId = 11,
                ProductId = 1,
                LoadingDate = new DateTime(2026, 4, 1),
                LoadedQuantityMt = 50m,
                LoadingPriceUsd = 700m
            },
            new LoadingRegister
            {
                Id = 121,
                ContractId = 1, // Sales contract from seed
                ProductId = 1,
                LoadingDate = new DateTime(2026, 4, 2),
                LoadedQuantityMt = 50m,
                LoadingPriceUsd = 800m
            });
        db.LossEvents.AddRange(
            new LossEvent
            {
                Id = 3,
                ProductId = 1,
                ContractId = 11,
                LoadingRegisterId = 120,
                EventDate = new DateTime(2026, 4, 5),
                ChargeableLossMt = 1m,
                IsCancelled = true // cancelled — must not be counted
            },
            new LossEvent
            {
                Id = 4,
                ProductId = 1,
                ContractId = 1, // Sales contract — must not feed Purchase P&L
                LoadingRegisterId = 121,
                EventDate = new DateTime(2026, 4, 6),
                ChargeableLossMt = 2m
            });
        await db.SaveChangesAsync();

        var controller = new ReportsController(db);
        var result = await controller.ContractPnl(new ManagementReportFilterViewModel { ContractId = 11 });

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<ContractPnlReportViewModel>(view.Model);
        var row = Assert.Single(model.PurchaseRows);
        Assert.Equal(0m, row.LossCostUsd);
        Assert.Equal(0, row.UnvaluedLossCount);
    }

    [Fact]
    public async Task ContractPnl_Values_TransportLeg_Loss_From_SourcePurchaseWeightedAverage()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        db.Contracts.Add(new Contract
        {
            Id = 19,
            ContractNumber = "PUR-TL-LOSS",
            ContractType = ContractType.Purchase,
            CompanyId = 1,
            ProductId = 1,
            SupplierId = 1,
            ContractDate = new DateTime(2026, 4, 1),
            QuantityMt = 100m,
            PricingMethod = PricingMethod.Fixed,
            UnitPriceUsd = null
        });
        db.LoadingRegisters.AddRange(
            new LoadingRegister
            {
                Id = 190,
                ContractId = 19,
                ProductId = 1,
                LoadingDate = new DateTime(2026, 4, 1),
                LoadedQuantityMt = 10m,
                LoadingPriceUsd = 100m
            },
            new LoadingRegister
            {
                Id = 191,
                ContractId = 19,
                ProductId = 1,
                LoadingDate = new DateTime(2026, 4, 2),
                LoadedQuantityMt = 30m,
                LoadingPriceUsd = 200m
            });
        db.InventoryTransportLegs.Add(new InventoryTransportLeg
        {
            Id = 1900,
            SourcePurchaseContractId = 19,
            ProductId = 1,
            SourceTerminalId = 1,
            TransportType = LoadingTransportType.Wagon,
            LoadedDate = new DateTime(2026, 4, 3),
            QuantityMt = 20m,
            Status = InventoryTransportLegStatus.Received
        });
        db.LossEvents.Add(new LossEvent
        {
            Id = 1900,
            ProductId = 1,
            ContractId = 19,
            TransportLegId = 1900,
            Stage = LossEventStage.ReceiptShortage,
            EventDate = new DateTime(2026, 4, 4),
            ChargeableLossMt = 2m
        });
        await db.SaveChangesAsync();

        var controller = new ReportsController(db);
        var result = await controller.ContractPnl(new ManagementReportFilterViewModel { ContractId = 19 });

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<ContractPnlReportViewModel>(view.Model);
        var row = Assert.Single(model.PurchaseRows);
        Assert.Equal(40m, row.TotalLoadedMt);
        Assert.Equal(7_000m, row.PurchaseValueUsd);
        Assert.Equal(350m, row.LossCostUsd); // 2 MT × weighted average 175 USD/MT
        Assert.Equal(0, row.UnvaluedLossCount);
    }

    [Fact]
    public async Task ContractPnl_Reports_TransportLeg_Loss_Without_PurchasePrice_As_Unvalued()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        db.Contracts.Add(new Contract
        {
            Id = 20,
            ContractNumber = "PUR-TL-UNVALUED",
            ContractType = ContractType.Purchase,
            CompanyId = 1,
            ProductId = 1,
            SupplierId = 1,
            ContractDate = new DateTime(2026, 4, 1),
            QuantityMt = 100m,
            PricingMethod = PricingMethod.Fixed,
            UnitPriceUsd = null
        });
        db.LoadingRegisters.Add(new LoadingRegister
        {
            Id = 200,
            ContractId = 20,
            ProductId = 1,
            LoadingDate = new DateTime(2026, 4, 1),
            LoadedQuantityMt = 10m,
            LoadingPriceUsd = null
        });
        db.InventoryTransportLegs.Add(new InventoryTransportLeg
        {
            Id = 2000,
            SourcePurchaseContractId = 20,
            ProductId = 1,
            SourceTerminalId = 1,
            TransportType = LoadingTransportType.Wagon,
            LoadedDate = new DateTime(2026, 4, 3),
            QuantityMt = 20m,
            Status = InventoryTransportLegStatus.Received
        });
        db.LossEvents.Add(new LossEvent
        {
            Id = 2000,
            ProductId = 1,
            ContractId = 20,
            TransportLegId = 2000,
            Stage = LossEventStage.ReceiptShortage,
            EventDate = new DateTime(2026, 4, 4),
            ChargeableLossMt = 2m
        });
        await db.SaveChangesAsync();

        var controller = new ReportsController(db);
        var result = await controller.ContractPnl(new ManagementReportFilterViewModel { ContractId = 20 });

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<ContractPnlReportViewModel>(view.Model);
        var row = Assert.Single(model.PurchaseRows);
        Assert.Equal(0m, row.LossCostUsd);
        Assert.Equal(1, row.UnvaluedLossCount);
        Assert.Equal(0m, row.PurchaseValueUsd);
    }

    [Fact]
    public async Task ContractPnl_TotalCost_Sums_All_Cost_Sources_Including_Loss_And_GeneralExpense()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        db.Contracts.Add(new Contract
        {
            Id = 12,
            ContractNumber = "PUR-FULLCOST",
            ContractType = ContractType.Purchase,
            CompanyId = 1,
            ProductId = 1,
            SupplierId = 1,
            ContractDate = new DateTime(2026, 4, 1),
            QuantityMt = 100m,
            PricingMethod = PricingMethod.Fixed,
            UnitPriceUsd = 500m
        });
        db.LoadingRegisters.Add(new LoadingRegister
        {
            Id = 130,
            ContractId = 12,
            ProductId = 1,
            LoadingDate = new DateTime(2026, 4, 1),
            LoadedQuantityMt = 10m,
            LoadingPriceUsd = 100m,
            TransportExpenseUsd = 50m,
            WarehouseExpenseUsd = 25m,
            OtherExpenseUsd = 5m,
            RailwayExpenseUsd = 10m
        });
        db.ExpenseTypes.Add(new ExpenseType { Id = 100, Code = "COMM", Name = "Commission" });
        db.ExpenseTransactions.Add(new ExpenseTransaction
        {
            Id = 50,
            ExpenseTypeId = 100,
            ContractId = 12,
            ExpenseDate = new DateTime(2026, 4, 5),
            Amount = 30m,
            Currency = "USD",
            AmountUsd = 30m
        });
        db.LossEvents.Add(new LossEvent
        {
            Id = 5,
            ProductId = 1,
            ContractId = 12,
            LoadingRegisterId = 130,
            EventDate = new DateTime(2026, 4, 6),
            ChargeableLossMt = 0.5m
        });
        await db.SaveChangesAsync();

        var controller = new ReportsController(db);
        var result = await controller.ContractPnl(new ManagementReportFilterViewModel { ContractId = 12 });

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<ContractPnlReportViewModel>(view.Model);
        var row = Assert.Single(model.PurchaseRows);
        Assert.Equal(1000m, row.PurchaseValueUsd);  // 10 × 100
        Assert.Equal(50m, row.TransportCostUsd);
        Assert.Equal(25m, row.WarehouseCostUsd);
        Assert.Equal(5m, row.OtherCostUsd);
        Assert.Equal(10m, row.RailwayCostUsd);
        Assert.Equal(30m, row.GeneralExpenseCostUsd);
        Assert.Equal(50m, row.LossCostUsd); // 0.5 × 100
        Assert.Equal(1170m, row.TotalCostUsd); // 1000 + 50 + 25 + 5 + 10 + 0 (customs) + 30 + 50
    }

    [Fact]
    public async Task ContractPnl_Uses_Official_WagonRent_Expense_Instead_Of_Inline_Railway_To_Avoid_Double_Count()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        db.Contracts.Add(new Contract
        {
            Id = 21,
            ContractNumber = "PUR-WAGON-RENT",
            ContractType = ContractType.Purchase,
            CompanyId = 1,
            ProductId = 1,
            SupplierId = 1,
            ContractDate = new DateTime(2026, 4, 1),
            QuantityMt = 100m,
            PricingMethod = PricingMethod.Fixed,
            UnitPriceUsd = 100m
        });
        db.LoadingRegisters.Add(new LoadingRegister
        {
            Id = 210,
            ContractId = 21,
            ProductId = 1,
            LoadingDate = new DateTime(2026, 4, 2),
            LoadedQuantityMt = 10m,
            LoadingPriceUsd = 100m,
            RailwayExpenseUsd = 100m
        });
        db.ExpenseTypes.Add(new ExpenseType
        {
            Id = 21,
            Code = "WAGON_RENT",
            Name = "Wagon Rent",
            NamePersian = "کرایه واگون",
            Category = "Transport",
            IsActive = true
        });
        db.ExpenseTransactions.Add(new ExpenseTransaction
        {
            Id = 21,
            ExpenseTypeId = 21,
            ContractId = 21,
            ExpenseDate = new DateTime(2026, 4, 3),
            Amount = 25m,
            Currency = "USD",
            AppliedFxRateToUsd = 1m,
            AmountUsd = 25m,
            Description = "Wagon Rent | M-Tone: 10 | Unit Price: 2.5 USD/MT"
        });
        await db.SaveChangesAsync();

        var controller = new ReportsController(db);
        var result = await controller.ContractPnl(new ManagementReportFilterViewModel { ContractId = 21 });

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<ContractPnlReportViewModel>(view.Model);
        var row = Assert.Single(model.PurchaseRows);
        Assert.Equal(1000m, row.PurchaseValueUsd);
        Assert.Equal(0m, row.RailwayCostUsd);
        Assert.Equal(25m, row.GeneralExpenseCostUsd);
        Assert.Equal(1025m, row.TotalCostUsd);
    }

    [Fact]
    public async Task Management_Report_Actions_Return_Core_ReadOnly_ViewModels()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        db.ServiceProviders.Add(new ServiceProvider { Id = 1, Name = "Service Provider A" });
        db.Sarrafs.Add(new Sarraf { Id = 1, Name = "Sarraf A" });
        db.CashAccounts.Add(new CashAccount { Id = 1, Code = "CASH", Name = "Main Cash", Currency = "USD" });
        db.StorageTanks.Add(new StorageTank { Id = 1, TerminalId = 1, TankCode = "TANK-A" });
        db.ExpenseTypes.Add(new ExpenseType { Id = 50, Code = "OPS", Name = "Operations" });
        db.SalesTransactions.Add(new SalesTransaction
        {
            Id = 50,
            CompanyId = 1,
            ContractId = 1,
            CustomerId = 1,
            ProductId = 1,
            SaleStage = SaleStage.PreSale,
            InvoiceNumber = "MGT-INV",
            SaleDate = new DateTime(2026, 4, 4),
            QuantityMt = 10m,
            UnitPriceUsd = 500m,
            TotalUsd = 5000m
        });
        db.ExpenseTransactions.Add(new ExpenseTransaction
        {
            Id = 50,
            ExpenseTypeId = 50,
            ContractId = 1,
            ExpenseDate = new DateTime(2026, 4, 5),
            Amount = 700m,
            Currency = "USD",
            AppliedFxRateToUsd = 1m,
            AmountUsd = 700m
        });
        db.PaymentTransactions.AddRange(
            new PaymentTransaction
            {
                Id = 50,
                PaymentDate = new DateTime(2026, 4, 6),
                Direction = PaymentDirection.In,
                PaymentKind = PaymentKind.CustomerReceipt,
                CashAccountId = 1,
                CustomerId = 1,
                ContractId = 1,
                Amount = 1200m,
                Currency = "USD",
                AmountUsd = 1200m
            },
            new PaymentTransaction
            {
                Id = 51,
                PaymentDate = new DateTime(2026, 4, 7),
                Direction = PaymentDirection.Out,
                PaymentKind = PaymentKind.SupplierPayment,
                CashAccountId = 1,
                SupplierId = 1,
                ContractId = 1,
                Amount = 400m,
                Currency = "USD",
                AmountUsd = 400m
            },
            new PaymentTransaction
            {
                Id = 52,
                PaymentDate = new DateTime(2026, 4, 8),
                Direction = PaymentDirection.Out,
                PaymentKind = PaymentKind.SarrafSettlement,
                CashAccountId = 1,
                SarrafId = 1,
                ContractId = 1,
                Amount = 300m,
                Currency = "USD",
                AmountUsd = 300m
            });
        db.LedgerEntries.AddRange(
            new LedgerEntry { Id = 50, EntryDate = new DateTime(2026, 4, 4), Side = LedgerSide.Credit, AmountUsd = 1000m, CustomerId = 1, ContractId = 1, SourceType = "Opening", SourceId = 50 },
            new LedgerEntry { Id = 51, EntryDate = new DateTime(2026, 4, 5), Side = LedgerSide.Debit, AmountUsd = 200m, CustomerId = 1, ContractId = 1, SourceType = "Adjustment", SourceId = 51 },
            new LedgerEntry { Id = 52, EntryDate = new DateTime(2026, 4, 6), Side = LedgerSide.Credit, AmountUsd = 600m, SupplierId = 1, ContractId = 1, SourceType = "Opening", SourceId = 52 },
            new LedgerEntry { Id = 53, EntryDate = new DateTime(2026, 4, 7), Side = LedgerSide.Credit, AmountUsd = 250m, ServiceProviderId = 1, ContractId = 1, SourceType = "Opening", SourceId = 53 });
        db.InventoryMovements.Add(new InventoryMovement
        {
            Id = 50,
            ProductId = 1,
            ContractId = 1,
            TerminalId = 1,
            StorageTankId = 1,
            Direction = MovementDirection.In,
            MovementDate = new DateTime(2026, 4, 9),
            QuantityMt = 100m
        });
        db.LoadingRegisters.Add(new LoadingRegister
        {
            Id = 50,
            ContractId = 1,
            ProductId = 1,
            LoadingDate = new DateTime(2026, 4, 10),
            LoadedQuantityMt = 25m,
            LoadingPriceUsd = 100m
        });
        await db.SaveChangesAsync();

        var controller = new ReportsController(db);
        var filter = new ManagementReportFilterViewModel
        {
            FromDate = new DateTime(2026, 4, 1),
            ToDate = new DateTime(2026, 4, 30),
            ContractId = 1
        };

        var company = Assert.IsType<CompanyFinancialOverviewViewModel>(
            Assert.IsType<ViewResult>(await controller.CompanyOverview(filter)).Model);
        Assert.Equal(5000m, company.RevenueUsd);
        Assert.Equal(700m, company.ExpenseUsd);
        Assert.Equal(500m, company.NetCashMovementUsd);
        Assert.True(company.WarningCount > 0);

        var cashFlow = Assert.IsType<CashFlowReportViewModel>(
            Assert.IsType<ViewResult>(await controller.CashFlow(filter)).Model);
        Assert.Equal(1200m, cashFlow.TotalInflowUsd);
        Assert.Equal(700m, cashFlow.TotalOutflowUsd);
        Assert.Equal(500m, cashFlow.NetCashFlowUsd);
        Assert.Contains(cashFlow.Rows, r => r.GroupName.Contains("مشتری"));

        var balances = Assert.IsType<ReceivablesPayablesReportViewModel>(
            Assert.IsType<ViewResult>(await controller.ReceivablesPayables(filter)).Model);
        Assert.Equal(800m, balances.CustomerReceivableUsd);
        Assert.Equal(600m, balances.SupplierPayableUsd);
        Assert.Equal(250m, balances.ServiceProviderPayableUsd);
        Assert.Equal(300m, balances.SarrafBalanceUsd);

        var inventory = Assert.IsType<InventoryOperationsReportViewModel>(
            Assert.IsType<ViewResult>(await controller.InventoryOperations(filter)).Model);
        var productRow = Assert.Single(inventory.ProductRows);
        Assert.Equal(100m, productRow.QuantityMt);
        Assert.NotEmpty(inventory.Warnings);

        var warnings = Assert.IsType<ReportsWarningsViewModel>(
            Assert.IsType<ViewResult>(await controller.Warnings()).Model);
        Assert.True(warnings.TotalIssueCount > 0);
    }

    // فروش مستقیم از موتر نه InventoryMovement دارد و نه تخصیصِ DirectSale؛ قبلاً کل عایدش —
    // شامل عایدِ اضافه‌بار — از سود و زیان قرارداد خرید بیرون می‌ماند. حالا از Lineage خودِ موتر
    // به همان قرارداد می‌رسد و هیچ قیمت خریدی هم برای اضافه‌بار ساخته نمی‌شود.
    [Fact]
    public async Task ContractPnl_Counts_Direct_Truck_Sale_Revenue_Including_Surplus()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        db.Contracts.Add(new Contract
        {
            Id = 40,
            ContractNumber = "PUR-TRUCK-SALE",
            ContractType = ContractType.Purchase,
            CompanyId = 1,
            ProductId = 1,
            SupplierId = 1,
            ContractDate = new DateTime(2026, 4, 1),
            QuantityMt = 100m,
            PricingMethod = PricingMethod.Fixed,
            UnitPriceUsd = 400m
        });
        db.LoadingRegisters.Add(new LoadingRegister
        {
            Id = 40,
            ContractId = 40,
            ProductId = 1,
            LoadingDate = new DateTime(2026, 4, 2),
            LoadedQuantityMt = 10m,
            LoadingPriceUsd = 400m
        });
        db.Trucks.Add(new Truck { Id = 40, PlateNumber = "TRK-40", IsActive = true });
        db.TruckDispatches.Add(new TruckDispatch
        {
            Id = 40,
            DispatchMode = TruckDispatchMode.DirectFromReceipt,
            ContractId = 40,
            ProductId = 1,
            TruckId = 40,
            DispatchDate = new DateTime(2026, 4, 3),
            LoadedQuantityMt = 10m,
            DischargedQuantityMt = 10.2m,
            SalesTransactionId = 40
        });
        db.SalesTransactions.Add(new SalesTransaction
        {
            Id = 40,
            CompanyId = 1,
            CustomerId = 1,
            ProductId = 1,
            SourcePurchaseContractId = 40,
            TruckDispatchId = 40,
            SaleStage = SaleStage.InTransit,
            InvoiceNumber = "TRK-SALE-40",
            SaleDate = new DateTime(2026, 4, 4),
            QuantityMt = 10.2m,
            Currency = "USD",
            UnitPriceInCurrency = 500m,
            UnitPriceUsd = 500m,
            TotalInCurrency = 5100m,
            TotalUsd = 5100m
        });
        await db.SaveChangesAsync();

        var controller = new ReportsController(db);
        var result = await controller.ContractPnl(new ManagementReportFilterViewModel { ContractId = 40 });

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<ContractPnlReportViewModel>(view.Model);
        var row = Assert.Single(model.PurchaseRows);

        // عاید کاملِ ۱۰.۲ تن — دقیقاً یک بار، با وجود اینکه هر دو مسیرِ لینک روی همین فروش ست است.
        Assert.Equal(10.2m, row.TotalSoldMt);
        Assert.Equal(5100m, row.TotalRevenueUsd);

        // قیمت خرید فقط از ۱۰ تنِ خریداری‌شده؛ برای ۰.۲ تن اضافه‌بار قیمت ساختگی ساخته نمی‌شود.
        Assert.Equal(10m, row.TotalLoadedMt);
        Assert.Equal(4000m, row.PurchaseValueUsd);
    }

    private static void SeedReferenceData(ApplicationDbContext db)
    {
        db.Products.AddRange(
            new Product { Id = 1, Code = "GAS", Name = "Gasoline" },
            new Product { Id = 2, Code = "DSL", Name = "Diesel" });
        db.Companies.Add(new Company { Id = 1, Code = "PTG", Name = "PTG" });
        db.Customers.AddRange(
            new Customer { Id = 1, Name = "Customer A" },
            new Customer { Id = 2, Name = "Customer B" });
        db.Suppliers.Add(new Supplier { Id = 1, Name = "Supplier A" });
        db.Terminals.Add(new Terminal { Id = 1, Code = "ILK", Name = "Ilinka" });
        db.Contracts.AddRange(
            new Contract
            {
                Id = 1,
                ContractNumber = "CON-1",
                ContractType = ContractType.Sale,
                CompanyId = 1,
                ProductId = 1,
                CustomerId = 1,
                SupplierId = 1,
                ContractDate = new DateTime(2026, 1, 1),
                QuantityMt = 100m,
                PricingMethod = PricingMethod.Fixed,
                UnitPriceUsd = 500m
            },
            new Contract
            {
                Id = 2,
                ContractNumber = "CON-2",
                ContractType = ContractType.Sale,
                CompanyId = 1,
                ProductId = 2,
                CustomerId = 2,
                ContractDate = new DateTime(2026, 1, 2),
                QuantityMt = 100m,
                PricingMethod = PricingMethod.Fixed,
                UnitPriceUsd = 600m
            });
    }
}
