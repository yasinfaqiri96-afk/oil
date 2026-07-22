using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PTGOilSystem.Web.Controllers;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services;
using Xunit;

namespace PTGOilSystem.Web.Tests;

public class HomeControllerTests
{
    [Fact]
    public async Task Index_Uses_Stock_Service_Compatible_Logic_For_Terminal_Stock()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        db.Products.Add(new Product { Id = 1, Code = "GO", Name = "Gas Oil" });
        db.InventoryMovements.AddRange(
            new InventoryMovement
            {
                ProductId = 1,
                TerminalId = 1,
                Direction = MovementDirection.In,
                MovementDate = new DateTime(2026, 4, 20),
                QuantityMt = 100m
            },
            new InventoryMovement
            {
                ProductId = 1,
                TerminalId = 1,
                Direction = MovementDirection.Adjustment,
                MovementDate = new DateTime(2026, 4, 21),
                QuantityMt = 5m
            },
            new InventoryMovement
            {
                ProductId = 1,
                TerminalId = 1,
                Direction = MovementDirection.Out,
                MovementDate = new DateTime(2026, 4, 22),
                QuantityMt = 20m
            },
            new InventoryMovement
            {
                ProductId = 1,
                TerminalId = 1,
                Direction = MovementDirection.Transfer,
                MovementDate = new DateTime(2026, 4, 23),
                QuantityMt = 10m
            });
        await db.SaveChangesAsync();

        var dashboardService = new DashboardService(db, new HttpContextAccessor());
        var controller = new HomeController(dashboardService, NullLogger<HomeController>.Instance);

        var result = await controller.Index();

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<DashboardViewModel>(view.Model);
        Assert.Equal(75m, model.TerminalStockMt);
    }

    [Fact]
    public async Task Index_Returns_Managerial_Kpis_And_Operational_Warnings_From_Real_Relations()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        db.Products.Add(new Product { Id = 1, Code = "GO", Name = "Gas Oil" });
        db.Companies.Add(new Company { Id = 1, Code = "PTG", Name = "PTG" });
        db.Customers.Add(new Customer { Id = 1, Name = "Customer A" });
        db.Suppliers.Add(new Supplier { Id = 1, Name = "Supplier A" });
        db.Terminals.Add(new Terminal { Id = 1, Code = "TERM", Name = "Terminal" });
        db.Contracts.AddRange(
            new Contract
            {
                Id = 1,
                ContractNumber = "CON-ACTIVE",
                ContractType = ContractType.Sale,
                Status = ContractStatus.Active,
                CompanyId = 1,
                ProductId = 1,
                CustomerId = 1,
                SupplierId = 1,
                ContractDate = DateTime.UtcNow.Date.AddDays(-10),
                EndDate = DateTime.UtcNow.Date.AddDays(10),
                QuantityMt = 100m,
                PricingMethod = PricingMethod.Fixed,
                UnitPriceUsd = 500m
            },
            new Contract
            {
                Id = 2,
                ContractNumber = "CON-DRAFT",
                ContractType = ContractType.Purchase,
                Status = ContractStatus.Draft,
                CompanyId = 1,
                ProductId = 1,
                ContractDate = DateTime.UtcNow.Date,
                QuantityMt = 50m,
                PricingMethod = PricingMethod.Fixed,
                UnitPriceUsd = 450m
            });
        db.LoadingRegisters.Add(new LoadingRegister
        {
            Id = 1,
            ContractId = 1,
            ProductId = 1,
            LoadingDate = DateTime.UtcNow.Date,
            LoadedQuantityMt = 20m
        });
        db.LoadingReceipts.Add(new LoadingReceipt
        {
            Id = 1,
            LoadingRegisterId = 1,
            TerminalId = 1,
            ReceiptDate = DateTime.UtcNow.Date,
            ReceivedQuantityMt = 8m
        });
        db.InventoryMovements.Add(new InventoryMovement
        {
            Id = 1,
            ProductId = 1,
            ContractId = 1,
            TerminalId = 1,
            Direction = MovementDirection.In,
            MovementDate = DateTime.UtcNow.Date,
            QuantityMt = 8m
        });
        db.Trucks.Add(new Truck { Id = 1, PlateNumber = "TRK-1" });
        db.TruckDispatches.Add(new TruckDispatch
        {
            Id = 1,
            ContractId = 1,
            ProductId = 1,
            TruckId = 1,
            DispatchDate = DateTime.UtcNow.Date.AddDays(-1),
            LoadedQuantityMt = 3m
        });
        db.Shipments.AddRange(
            new Shipment { Id = 1, ShipmentCode = "SHIP-NO-SALE-EXP", ContractId = 1, QuantityMt = 10m },
            new Shipment { Id = 2, ShipmentCode = "SHIP-WITH-SALE", ContractId = 1, QuantityMt = 5m });
        db.ExpenseTypes.Add(new ExpenseType { Id = 1, Code = "PORT", Name = "Port" });
        db.SalesTransactions.Add(new SalesTransaction
        {
            Id = 1,
            CompanyId = 1,
            ContractId = 1,
            CustomerId = 1,
            ProductId = 1,
            ShipmentId = 2,
            InvoiceNumber = "INV-1",
            SaleDate = DateTime.UtcNow.Date,
            QuantityMt = 2m,
            UnitPriceUsd = 500m,
            TotalUsd = 1000m
        });
        db.ExpenseTransactions.Add(new ExpenseTransaction
        {
            Id = 1,
            ExpenseTypeId = 1,
            ContractId = 1,
            ExpenseDate = DateTime.UtcNow.Date,
            AmountUsd = 250m
        });
        db.LedgerEntries.AddRange(
            new LedgerEntry
            {
                Id = 1,
                EntryDate = DateTime.UtcNow.Date,
                Side = LedgerSide.Credit,
                AmountUsd = 1000m,
                SourceType = "Sale",
                SourceId = 1,
                ContractId = 1,
                CustomerId = 1
            },
            new LedgerEntry
            {
                Id = 2,
                EntryDate = DateTime.UtcNow.Date,
                Side = LedgerSide.Debit,
                AmountUsd = 250m,
                SourceType = "Expense",
                SourceId = 1,
                ContractId = 1,
                SupplierId = 1
            });
        await db.SaveChangesAsync();

        var dashboardService = new DashboardService(db, new HttpContextAccessor());
        var controller = new HomeController(dashboardService, NullLogger<HomeController>.Instance);

        var result = await controller.Index();

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<DashboardViewModel>(view.Model);
        Assert.Equal(1, model.ActiveContractCount);
        Assert.Equal(1, model.LoadingCount);
        Assert.Equal(1, model.LoadingReceiptCount);
        Assert.Equal(8m, model.TerminalStockMt);
        Assert.Equal(1, model.RecentDispatchCount);
        Assert.Equal(1000m, model.TotalSalesUsd);
        Assert.Equal(250m, model.TotalExpensesUsd);
        Assert.Equal(750m, model.GrossMarginUsd);
        Assert.Equal(2, model.ShipmentCount);
        Assert.Equal(2, model.ContractBalanceSummary.ItemCount);
        // مانده نمایشی = Σ(داده − گرفته)؛ منفی یعنی بدهکاریم.
        Assert.Equal(-750m, model.ContractBalanceSummary.BaseBalanceUsd);
        Assert.Equal(1, model.CustomerBalanceSummary.ItemCount);
        Assert.Equal(-1000m, model.CustomerBalanceSummary.BaseBalanceUsd);
        Assert.Equal(1, model.SupplierBalanceSummary.ItemCount);
        // مثبت یعنی نزد این تأمین‌کننده پیش‌پرداخت داریم.
        Assert.Equal(250m, model.SupplierBalanceSummary.BaseBalanceUsd);
        Assert.NotEmpty(model.LowStockAlerts);
        Assert.NotEmpty(model.ContractsEndingSoonAlerts);
        Assert.NotEmpty(model.ShipmentsWithoutSalesAlerts);
        Assert.NotEmpty(model.ShipmentsWithoutExpensesAlerts);
    }

    [Fact]
    public async Task Index_Excludes_Cancelled_Sales_And_Expenses_From_Dashboard_Totals()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        db.Products.Add(new Product { Id = 1, Code = "GO", Name = "Gas Oil" });
        db.Companies.Add(new Company { Id = 1, Code = "PTG", Name = "PTG" });
        db.Customers.Add(new Customer { Id = 1, Name = "Customer A" });
        db.ExpenseTypes.Add(new ExpenseType { Id = 1, Code = "PORT", Name = "Port" });

        db.SalesTransactions.AddRange(
            new SalesTransaction
            {
                Id = 1,
                CompanyId = 1,
                CustomerId = 1,
                ProductId = 1,
                InvoiceNumber = "INV-ACTIVE",
                SaleDate = DateTime.UtcNow.Date,
                QuantityMt = 10m,
                UnitPriceInCurrency = 100m,
                UnitPriceUsd = 100m,
                TotalInCurrency = 1000m,
                TotalUsd = 1000m,
                IsCancelled = false
            },
            new SalesTransaction
            {
                Id = 2,
                CompanyId = 1,
                CustomerId = 1,
                ProductId = 1,
                InvoiceNumber = "INV-CANCELLED",
                SaleDate = DateTime.UtcNow.Date,
                QuantityMt = 10m,
                UnitPriceInCurrency = 999m,
                UnitPriceUsd = 999m,
                TotalInCurrency = 9990m,
                TotalUsd = 9990m,
                IsCancelled = true
            });

        db.ExpenseTransactions.AddRange(
            new ExpenseTransaction
            {
                Id = 1,
                ExpenseTypeId = 1,
                ExpenseDate = DateTime.UtcNow.Date,
                Amount = 250m,
                AmountUsd = 250m,
                IsCancelled = false
            },
            new ExpenseTransaction
            {
                Id = 2,
                ExpenseTypeId = 1,
                ExpenseDate = DateTime.UtcNow.Date,
                Amount = 900m,
                AmountUsd = 900m,
                IsCancelled = true
            });

        await db.SaveChangesAsync();

        var dashboardService = new DashboardService(db, new HttpContextAccessor());
        var controller = new HomeController(dashboardService, NullLogger<HomeController>.Instance);

        var result = await controller.Index();

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<DashboardViewModel>(view.Model);
        Assert.Equal(1000m, model.TotalSalesUsd);
        Assert.Equal(250m, model.TotalExpensesUsd);
        Assert.Equal(750m, model.NetUsd);
    }

    [Fact]
    public async Task Index_Builds_Recent_Activities_And_Order_Rows_From_Real_Operational_Data()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        db.Products.Add(new Product { Id = 1, Code = "LPG", Name = "LPG" });
        db.Companies.Add(new Company { Id = 1, Code = "PTG", Name = "PTG" });
        db.Customers.Add(new Customer { Id = 1, Name = "Customer A" });
        db.Contracts.Add(new Contract
        {
            Id = 1,
            ContractNumber = "PUR-LPG-1",
            ContractType = ContractType.Purchase,
            Status = ContractStatus.Active,
            CompanyId = 1,
            ProductId = 1,
            ContractDate = DateTime.UtcNow.Date,
            QuantityMt = 100m,
            PricingMethod = PricingMethod.Fixed,
            UnitPriceUsd = 468.06m
        });
        db.LoadingRegisters.Add(new LoadingRegister
        {
            Id = 1,
            ContractId = 1,
            ProductId = 1,
            LoadingDate = DateTime.UtcNow.Date.AddDays(-1),
            LoadedQuantityMt = 35.88m,
            LoadingPriceUsd = 468.06m,
            BillOfLadingNumber = "RWB-100"
        });
        db.SalesTransactions.Add(new SalesTransaction
        {
            Id = 1,
            CompanyId = 1,
            ContractId = 1,
            CustomerId = 1,
            ProductId = 1,
            InvoiceNumber = "INV-100",
            SaleDate = DateTime.UtcNow.Date,
            QuantityMt = 12.5m,
            UnitPriceInCurrency = 610m,
            UnitPriceUsd = 610m,
            TotalInCurrency = 7625m,
            TotalUsd = 7625m
        });

        await db.SaveChangesAsync();

        var dashboardService = new DashboardService(db, new HttpContextAccessor());
        var controller = new HomeController(dashboardService, NullLogger<HomeController>.Instance);

        var result = await controller.Index();

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<DashboardViewModel>(view.Model);

        Assert.Contains(model.RecentActivities, row => row.Name.Contains("INV-100", StringComparison.Ordinal));
        Assert.Contains(model.RecentActivities, row => row.Name.Contains("RWB-100", StringComparison.Ordinal));
        Assert.Equal("Recent Sales", model.OutboundOrderPanel.Title);
        Assert.Equal("Recent Loading", model.InboundOrderPanel.Title);
        Assert.Contains(model.OutboundOrderPanel.Rows, row => row.Reference == "INV-100");
        Assert.Contains(model.InboundOrderPanel.Rows, row => row.Reference == "RWB-100");
    }
}
