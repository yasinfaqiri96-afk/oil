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
using PTGOilSystem.Web.Models.AccountStatements;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.Expenses;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Audit;
using Xunit;

namespace PTGOilSystem.Web.Tests;

public class ExpensesControllerTests
{
    [Fact]
    public async Task Create_Get_Preselects_Contract_And_ReturnUrl()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
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
            UnitPriceUsd = 400m
        });
        await db.SaveChangesAsync();

        var controller = BuildController(db);

        var result = await controller.Create(contractId: 1, returnUrl: "/Contracts/Details/1?tab=expenses");

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<ExpenseCreateViewModel>(view.Model);
        Assert.Equal(1, model.ContractId);
        Assert.Equal("/Contracts/Details/1?tab=expenses", model.ReturnUrl);
    }

    [Fact]
    public async Task Create_Get_Preselects_TransportLeg_And_SourcePurchaseContract()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
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
            UnitPriceUsd = 400m
        });
        db.Terminals.Add(new Terminal { Id = 1, Code = "T1", Name = "Terminal 1" });
        db.Shipments.Add(new Shipment { Id = 1, ShipmentCode = "KALUGA", QuantityMt = 20m });
        db.InventoryTransportLegs.Add(new InventoryTransportLeg
        {
            Id = 10,
            ShipmentId = 1,
            SourcePurchaseContractId = 1,
            ProductId = 1,
            SourceTerminalId = 1,
            TransportType = LoadingTransportType.Wagon,
            LoadedDate = new DateTime(2026, 4, 24),
            QuantityMt = 20m,
            Status = InventoryTransportLegStatus.Loaded
        });
        await db.SaveChangesAsync();

        var controller = BuildController(db);

        var result = await controller.Create(transportLegId: 10, returnUrl: "/InventoryTransportLegs/Details/10");

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<ExpenseCreateViewModel>(view.Model);
        Assert.Equal(10, model.TransportLegId);
        Assert.Equal(1, model.ContractId);
        Assert.Equal("/InventoryTransportLegs/Details/10", model.ReturnUrl);
    }

    [Fact]
    public async Task Create_Post_With_TransportLeg_Sets_SourcePurchaseContract_When_Contract_Is_Empty()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
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
            UnitPriceUsd = 400m
        });
        db.Terminals.Add(new Terminal { Id = 1, Code = "T1", Name = "Terminal 1" });
        db.Shipments.Add(new Shipment { Id = 1, ShipmentCode = "KALUGA", QuantityMt = 20m });
        db.InventoryTransportLegs.Add(new InventoryTransportLeg
        {
            Id = 10,
            ShipmentId = 1,
            SourcePurchaseContractId = 1,
            ProductId = 1,
            SourceTerminalId = 1,
            TransportType = LoadingTransportType.Wagon,
            LoadedDate = new DateTime(2026, 4, 24),
            QuantityMt = 20m,
            Status = InventoryTransportLegStatus.Loaded
        });
        await db.SaveChangesAsync();

        var controller = BuildController(db);

        var result = await controller.Create(new ExpenseCreateViewModel
        {
            ExpenseTypeId = 1,
            TransportLegId = 10,
            ExpenseDate = new DateTime(2026, 4, 25),
            AmountUsd = 1500m,
            Description = "Wagon expense"
        });

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Details", redirect.ActionName);

        var expense = await db.ExpenseTransactions.SingleAsync();
        Assert.Equal(10, expense.TransportLegId);
        Assert.Equal(1, expense.ContractId);
        Assert.Equal(1, expense.ShipmentId);

        var ledger = await db.LedgerEntries.SingleAsync();
        Assert.Equal(1, ledger.ContractId);
        Assert.Equal(1, ledger.ShipmentId);
    }

    [Fact]
    public async Task Create_Post_With_Manual_Expense_Type_Creates_Type_And_Expense()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
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
            UnitPriceUsd = 400m
        });
        await db.SaveChangesAsync();

        var controller = BuildController(db);
        var model = new ExpenseCreateViewModel
        {
            ContractId = 1,
            ExpenseDate = new DateTime(2026, 4, 25),
            AmountUsd = 275m,
            Description = "Manual expense type",
            ManualExpenseTypeName = "هزینه تخلیه واگن"
        };

        var result = await controller.Create(model);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Details", redirect.ActionName);

        var manualType = await db.ExpenseTypes.SingleAsync(e => e.NamePersian == "هزینه تخلیه واگن");
        Assert.Equal("هزینه تخلیه واگن", manualType.Name);
        Assert.True(manualType.IsActive);

        var expense = await db.ExpenseTransactions.SingleAsync();
        Assert.Equal(manualType.Id, expense.ExpenseTypeId);
        Assert.Equal(275m, expense.AmountUsd);

        var ledger = await db.LedgerEntries.SingleAsync();
        Assert.Contains("هزینه تخلیه واگن", ledger.Description ?? string.Empty);
    }

    [Fact]
    public void Create_View_Allows_Typing_Manual_Expense_Type()
    {
        var view = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "src",
            "PTGOilSystem.Web",
            "Views",
            "Expenses",
            "Create.cshtml"));

        Assert.Contains("asp-for=\"ManualExpenseTypeName\"", view);
        Assert.Contains("list=\"expenseTypeSuggestions\"", view);
    }

    [Fact]
    public async Task Create_Post_Redirects_To_ReturnUrl_When_Provided()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
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
            UnitPriceUsd = 400m
        });
        await db.SaveChangesAsync();

        var controller = BuildController(db);
        controller.Url = BuildUrlHelper();

        var result = await controller.Create(new ExpenseCreateViewModel
        {
            ExpenseTypeId = 1,
            ContractId = 1,
            ExpenseDate = new DateTime(2026, 4, 25),
            AmountUsd = 1500m,
            Description = "Port expense",
            ReturnUrl = "/Contracts/Details/1?tab=expenses"
        });

        var redirect = Assert.IsType<RedirectResult>(result);
        Assert.Equal("/Contracts/Details/1?tab=expenses", redirect.Url);
    }

    [Fact]
    public async Task Create_Post_Ignores_External_ReturnUrl()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
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
            UnitPriceUsd = 400m
        });
        await db.SaveChangesAsync();

        var controller = BuildController(db);
        controller.Url = BuildUrlHelper();

        var result = await controller.Create(new ExpenseCreateViewModel
        {
            ExpenseTypeId = 1,
            ContractId = 1,
            ExpenseDate = new DateTime(2026, 4, 25),
            AmountUsd = 1500m,
            Description = "Port expense",
            ReturnUrl = "https://evil.com"
        });

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Details", redirect.ActionName);
    }

    [Fact]
    public async Task Create_Post_Blocks_When_TruckDispatch_Does_Not_Match_Selected_Contract()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        db.Contracts.AddRange(
            new Contract
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
                UnitPriceUsd = 400m
            },
            new Contract
            {
                Id = 2,
                ContractNumber = "PUR-002",
                ContractType = ContractType.Purchase,
                CompanyId = 1,
                ProductId = 1,
                SupplierId = 1,
                ContractDate = new DateTime(2026, 4, 24),
                QuantityMt = 100m,
                PricingMethod = PricingMethod.Fixed,
                UnitPriceUsd = 410m
            });
        db.TruckDispatches.Add(new TruckDispatch
        {
            Id = 1,
            ContractId = 1,
            ProductId = 1,
            TruckId = 1,
            DispatchDate = new DateTime(2026, 4, 25),
            LoadedQuantityMt = 20m
        });
        await db.SaveChangesAsync();

        var controller = BuildController(db);

        var result = await controller.Create(new ExpenseCreateViewModel
        {
            ExpenseTypeId = 1,
            ContractId = 2,
            TruckDispatchId = 1,
            ExpenseDate = new DateTime(2026, 4, 25),
            AmountUsd = 1250m,
            Description = "کرایه مسیر هرات"
        });

        var view = Assert.IsType<ViewResult>(result);
        Assert.IsType<ExpenseCreateViewModel>(view.Model);
        Assert.False(controller.ModelState.IsValid);
        Assert.Contains(controller.ModelState[nameof(ExpenseCreateViewModel.TruckDispatchId)]!.Errors, e => e.ErrorMessage.Contains("هم‌خوان نیست"));
    }

    [Fact]
    public async Task Create_Post_Blocks_When_Shipment_And_Dispatch_Are_Inconsistent()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        db.Contracts.AddRange(
            new Contract
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
                UnitPriceUsd = 400m
            },
            new Contract
            {
                Id = 2,
                ContractNumber = "PUR-002",
                ContractType = ContractType.Purchase,
                CompanyId = 1,
                ProductId = 1,
                SupplierId = 1,
                ContractDate = new DateTime(2026, 4, 24),
                QuantityMt = 100m,
                PricingMethod = PricingMethod.Fixed,
                UnitPriceUsd = 410m
            });
        db.Shipments.Add(new Shipment
        {
            Id = 1,
            ShipmentCode = "SHP-001",
            ContractId = 1,
            QuantityMt = 50m
        });
        db.TruckDispatches.Add(new TruckDispatch
        {
            Id = 1,
            ContractId = 2,
            ProductId = 1,
            TruckId = 1,
            DispatchDate = new DateTime(2026, 4, 25),
            LoadedQuantityMt = 20m
        });
        await db.SaveChangesAsync();

        var controller = BuildController(db);

        var result = await controller.Create(new ExpenseCreateViewModel
        {
            ExpenseTypeId = 1,
            ShipmentId = 1,
            TruckDispatchId = 1,
            ExpenseDate = new DateTime(2026, 4, 25),
            AmountUsd = 900m,
            Description = "هزینه تخلیه و حمل"
        });

        var view = Assert.IsType<ViewResult>(result);
        Assert.IsType<ExpenseCreateViewModel>(view.Model);
        Assert.False(controller.ModelState.IsValid);
        Assert.Contains(controller.ModelState[string.Empty]!.Errors, e => e.ErrorMessage.Contains("قرارداد واحد"));
    }

    [Fact]
    public async Task Create_Post_Persists_Expense_Ledger_And_Audit()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
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
            UnitPriceUsd = 400m
        });
        db.Shipments.Add(new Shipment
        {
            Id = 1,
            ShipmentCode = "SHP-002",
            ContractId = 1,
            QuantityMt = 50m
        });
        await db.SaveChangesAsync();

        var controller = BuildController(db);

        var result = await controller.Create(new ExpenseCreateViewModel
        {
            ExpenseTypeId = 1,
            ContractId = 1,
            ShipmentId = 1,
            ExpenseDate = new DateTime(2026, 4, 25),
            AmountUsd = 1500m,
            Description = "هزینه بندری فاکتور PORT-22"
        });

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Details", redirect.ActionName);

        var expense = await db.ExpenseTransactions.SingleAsync();
        Assert.Equal(1500m, expense.AmountUsd);
        Assert.Equal("هزینه بندری فاکتور PORT-22", expense.Description);

        var ledger = await db.LedgerEntries.SingleAsync();
        Assert.Equal("Expense", ledger.SourceType);
        Assert.Equal(expense.Id, ledger.SourceId);
        Assert.Equal(LedgerSide.Debit, ledger.Side);
        Assert.Contains("PORT-22", ledger.Reference ?? string.Empty);

        var audit = await db.AuditLogs.SingleAsync();
        Assert.Equal(nameof(ExpenseTransaction), audit.EntityName);
        Assert.Equal("Insert", audit.Action);
        Assert.Contains("AmountUsd=1,500.0000", audit.Diff);
    }

    [Fact]
    public async Task Create_Post_ServiceProviderExpense_Creates_Credit_Ledger_With_ServiceProvider_Link()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        db.ServiceProviders.Add(new PTGOilSystem.Web.Models.Entities.ServiceProvider
        {
            Id = 1,
            Name = "Wagon Rent Provider",
            ProviderType = ServiceProviderType.WagonRent,
            IsActive = true
        });
        await db.SaveChangesAsync();

        var controller = BuildController(db);

        var result = await controller.Create(new ExpenseCreateViewModel
        {
            ExpenseTypeId = 1,
            ServiceProviderId = 1,
            ExpenseDate = new DateTime(2026, 4, 25),
            AmountUsd = 320m,
            Description = "Provider wagon rent"
        });

        Assert.IsType<RedirectToActionResult>(result);
        var expense = await db.ExpenseTransactions.SingleAsync();
        var ledger = await db.LedgerEntries.SingleAsync();
        Assert.Equal(1, expense.ServiceProviderId);
        Assert.Equal(1, ledger.ServiceProviderId);
        Assert.Equal("Expense", ledger.SourceType);
        Assert.Equal(expense.Id, ledger.SourceId);
        Assert.Equal(LedgerSide.Credit, ledger.Side);
        Assert.Equal(320m, ledger.AmountUsd);
    }

    [Fact]
    public async Task Details_Returns_Ledger_Trace_For_Expense()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
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
            UnitPriceUsd = 400m
        });
        db.ExpenseTransactions.Add(new ExpenseTransaction
        {
            Id = 1,
            ExpenseTypeId = 1,
            ContractId = 1,
            ExpenseDate = new DateTime(2026, 4, 25),
            AmountUsd = 2000m,
            Description = "هزینه تخلیه"
        });
        db.LedgerEntries.Add(new LedgerEntry
        {
            Id = 1,
            EntryDate = new DateTime(2026, 4, 25),
            Side = LedgerSide.Debit,
            AmountUsd = 2000m,
            Currency = "USD",
            Description = "ثبت هزینه بندری",
            SourceType = "Expense",
            SourceId = 1,
            Reference = "PORT-EXP-1",
            ContractId = 1
        });
        await db.SaveChangesAsync();

        var controller = BuildController(db);

        var result = await controller.Details(1);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<ExpenseDetailsViewModel>(view.Model);
        Assert.Equal(1, model.LedgerEntryId);
        Assert.Equal("PORT-EXP-1", model.LedgerReference);
        Assert.Equal("بدهکار", model.LedgerSideName);
    }

    [Fact]
    public async Task Edit_Get_Loads_Existing_Expense_And_ReturnUrl()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
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
            UnitPriceUsd = 400m
        });
        db.ExpenseTransactions.Add(new ExpenseTransaction
        {
            Id = 50,
            ExpenseTypeId = 1,
            ContractId = 1,
            ExpenseDate = new DateTime(2026, 4, 25),
            Amount = 1500m,
            Currency = "USD",
            AppliedFxRateToUsd = 1m,
            AmountUsd = 1500m,
            Description = "Before edit"
        });
        await db.SaveChangesAsync();

        var controller = BuildController(db);

        var result = await controller.Edit(50, returnUrl: "/ContractJourney/Details?contractId=1&tab=costs");

        var view = Assert.IsType<ViewResult>(result);
        Assert.Equal("Create", view.ViewName);
        var model = Assert.IsType<ExpenseCreateViewModel>(view.Model);
        Assert.Equal(50, model.Id);
        Assert.Equal(1, model.ExpenseTypeId);
        Assert.Equal(1, model.ContractId);
        Assert.Equal(1500m, model.Amount);
        Assert.Equal("Before edit", model.Description);
        Assert.Equal("/ContractJourney/Details?contractId=1&tab=costs", model.ReturnUrl);
        Assert.Equal("Edit", view.ViewData["ExpenseFormMode"]);
    }

    [Fact]
    public async Task Edit_Post_Updates_Expense_And_Linked_Ledger_Then_Returns_To_Local_ReturnUrl()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
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
            UnitPriceUsd = 400m
        });
        db.Shipments.Add(new Shipment
        {
            Id = 1,
            ShipmentCode = "KALUGA",
            ContractId = 1,
            QuantityMt = 50m
        });
        db.ExpenseTransactions.Add(new ExpenseTransaction
        {
            Id = 51,
            ExpenseTypeId = 1,
            ContractId = 1,
            ExpenseDate = new DateTime(2026, 4, 25),
            Amount = 1500m,
            Currency = "USD",
            AppliedFxRateToUsd = 1m,
            AmountUsd = 1500m,
            Description = "Port expense before"
        });
        db.LedgerEntries.Add(new LedgerEntry
        {
            Id = 151,
            EntryDate = new DateTime(2026, 4, 25),
            Side = LedgerSide.Debit,
            AmountUsd = 1500m,
            Currency = "USD",
            SourceAmount = 1500m,
            SourceCurrencyCode = "USD",
            AppliedFxRateToUsd = 1m,
            AppliedFxRateDate = new DateTime(2026, 4, 25),
            Description = "Old expense",
            SourceType = "Expense",
            SourceId = 51,
            Reference = "PORT-51 | before",
            ContractId = 1
        });
        await db.SaveChangesAsync();

        var controller = BuildController(db);
        controller.Url = BuildUrlHelper();

        var result = await controller.Edit(51, new ExpenseCreateViewModel
        {
            Id = 51,
            ExpenseTypeId = 1,
            ContractId = 1,
            ShipmentId = 1,
            ExpenseDate = new DateTime(2026, 4, 26),
            Amount = 1750m,
            Currency = "USD",
            Description = "Port expense after",
            ReturnUrl = "/ContractJourney/Details?contractId=1&tab=costs"
        });

        var redirect = Assert.IsType<RedirectResult>(result);
        Assert.Equal("/ContractJourney/Details?contractId=1&tab=costs", redirect.Url);

        var expense = await db.ExpenseTransactions.SingleAsync(e => e.Id == 51);
        var ledger = await db.LedgerEntries.SingleAsync(l => l.Id == 151);
        Assert.Equal(new DateTime(2026, 4, 26), expense.ExpenseDate);
        Assert.Equal(1750m, expense.Amount);
        Assert.Equal(1750m, expense.AmountUsd);
        Assert.Equal(1, expense.ShipmentId);
        Assert.Equal("Port expense after", expense.Description);
        Assert.Equal(1750m, ledger.AmountUsd);
        Assert.Equal(1750m, ledger.SourceAmount);
        Assert.Equal(1, ledger.ShipmentId);
        Assert.Equal("Expense", ledger.SourceType);
        Assert.Equal(51, ledger.SourceId);
        Assert.Contains("Port expense after", ledger.Reference ?? string.Empty);

        Assert.Contains(await db.AuditLogs.ToListAsync(),
            log => log.EntityName == nameof(ExpenseTransaction) && log.Action == "Update");
        Assert.Contains(await db.AuditLogs.ToListAsync(),
            log => log.EntityName == nameof(LedgerEntry) && log.Action == "Update");
    }

    [Fact]
    public async Task CreateWagonRent_Post_Creates_Expense_And_Expense_Ledger_Only()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        db.Currencies.Add(new Currency { Id = 3, Code = "RUB", Name = "Russian Ruble", Symbol = "RUB", IsActive = true });
        db.ExpenseTypes.Add(new ExpenseType { Id = 2, Code = "WAGON_RENT", Name = "Wagon Rent", NamePersian = "کرایه واگون", Category = "Transport" });
        db.Contracts.Add(new Contract
        {
            Id = 1,
            ContractNumber = "M-15",
            ContractType = ContractType.Purchase,
            CompanyId = 1,
            ProductId = 1,
            SupplierId = 1,
            ContractDate = new DateTime(2026, 4, 23),
            QuantityMt = 100m,
            PricingMethod = PricingMethod.Fixed,
            UnitPriceUsd = 400m
        });
        await db.SaveChangesAsync();

        var controller = BuildController(db);

        var result = await controller.CreateWagonRent(new WagonRentCreateViewModel
        {
            ExpenseTypeId = 2,
            ContractId = 1,
            ExpenseDate = new DateTime(2026, 4, 25),
            QuantityMt = 9522.050m,
            UnitPriceOriginal = 7752.050m,
            Currency = "RUB",
            DocumentCurrencyPerUsdRate = 92.0600m,
            Reference = "BNK-WAGON-RENT"
        });

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Details", redirect.ActionName);

        var expense = await db.ExpenseTransactions.SingleAsync();
        Assert.Equal(1, expense.ContractId);
        Assert.Equal(2, expense.ExpenseTypeId);
        Assert.Equal(73815407.7025m, expense.Amount);
        Assert.Equal("RUB", expense.Currency);
        Assert.Equal(0.010862m, expense.AppliedFxRateToUsd);
        Assert.Equal(801782.9585m, expense.AmountUsd);
        Assert.Contains("M-Tone: 9,522.0500", expense.Description);
        Assert.Contains("Unit Price: 7,752.0500 RUB/MT", expense.Description);

        var ledger = await db.LedgerEntries.SingleAsync();
        Assert.Equal("Expense", ledger.SourceType);
        Assert.Equal(expense.Id, ledger.SourceId);
        Assert.Equal(LedgerSide.Debit, ledger.Side);
        Assert.Equal(73815407.7025m, ledger.SourceAmount);
        Assert.Equal("RUB", ledger.SourceCurrencyCode);
        Assert.Equal(0.010862m, ledger.AppliedFxRateToUsd);
        Assert.NotEqual(92.0600m, ledger.AppliedFxRateToUsd);
        Assert.Equal(801782.9585m, ledger.AmountUsd);

        Assert.Empty(db.PaymentTransactions);
        Assert.Empty(db.InventoryMovements);
    }

    [Fact]
    public async Task CreateWagonRent_Post_For_Usd_Forces_Rate_One_And_Amount_Equals_Original()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        db.ExpenseTypes.Add(new ExpenseType { Id = 2, Code = "WAGON_RENT", Name = "Wagon Rent", NamePersian = "کرایه واگون", Category = "Transport" });
        db.Contracts.Add(new Contract
        {
            Id = 1,
            ContractNumber = "M-15",
            ContractType = ContractType.Purchase,
            CompanyId = 1,
            ProductId = 1,
            SupplierId = 1,
            ContractDate = new DateTime(2026, 4, 23),
            QuantityMt = 100m,
            PricingMethod = PricingMethod.Fixed,
            UnitPriceUsd = 400m
        });
        await db.SaveChangesAsync();

        var controller = BuildController(db);

        var result = await controller.CreateWagonRent(new WagonRentCreateViewModel
        {
            ExpenseTypeId = 2,
            ContractId = 1,
            ExpenseDate = new DateTime(2026, 4, 25),
            QuantityMt = 10m,
            UnitPriceOriginal = 25m,
            Currency = "USD",
            DocumentCurrencyPerUsdRate = 92.0600m,
            AppliedFxRateToUsd = 92.0600m
        });

        Assert.IsType<RedirectToActionResult>(result);
        var expense = await db.ExpenseTransactions.SingleAsync();
        var ledger = await db.LedgerEntries.SingleAsync();
        Assert.Equal(250m, expense.Amount);
        Assert.Equal(1m, expense.AppliedFxRateToUsd);
        Assert.Equal(250m, expense.AmountUsd);
        Assert.Equal(1m, ledger.AppliedFxRateToUsd);
        Assert.Equal(250m, ledger.AmountUsd);
    }

    [Fact]
    public async Task Contract_Account_Statement_Shows_Wagon_Rent_From_Ledger_Without_Duplicate()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        db.ExpenseTypes.Add(new ExpenseType { Id = 2, Code = "WAGON_RENT", Name = "Wagon Rent", NamePersian = "کرایه واگون", Category = "Transport" });
        db.Contracts.Add(new Contract
        {
            Id = 1,
            ContractNumber = "M-15",
            ContractType = ContractType.Purchase,
            CompanyId = 1,
            ProductId = 1,
            SupplierId = 1,
            ContractDate = new DateTime(2026, 4, 23),
            QuantityMt = 100m,
            PricingMethod = PricingMethod.Fixed,
            UnitPriceUsd = 400m
        });
        await db.SaveChangesAsync();

        var expenses = BuildController(db);
        await expenses.CreateWagonRent(new WagonRentCreateViewModel
        {
            ExpenseTypeId = 2,
            ContractId = 1,
            ExpenseDate = new DateTime(2026, 4, 25),
            QuantityMt = 10m,
            UnitPriceOriginal = 25m,
            Currency = "USD",
            Reference = "WAGON-STMT"
        });

        var statement = new AccountStatementsController(db, new PricingService(db), new AuditService(db))
        {
            TempData = BuildTempData()
        };
        var result = await statement.Contract(1);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<ContractAccountStatementViewModel>(view.Model);
        var row = Assert.Single(model.Rows);
        Assert.Equal("Expense", row.SourceType);
        Assert.Equal(250m, row.ReceiptUsd);
        Assert.Null(row.OutflowUsd);
        Assert.Contains("Wagon Rent", row.Description);
        Assert.Equal(-250m, model.Totals.BalanceUsd);
    }

    [Fact]
    public void ContractJourney_Details_Contains_Wagon_Rent_Link()
    {
        var view = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "src",
            "PTGOilSystem.Web",
            "Views",
            "ContractJourney",
            "Details.cshtml"));

        Assert.Contains("asp-controller=\"Expenses\"", view);
        Assert.Contains("asp-action=\"CreateWagonRent\"", view);
        Assert.Contains("ثبت کرایه واگون", view);
    }

    [Fact]
    public void ContractJourney_Details_Expense_List_Provides_Edit_Action()
    {
        var view = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "src",
            "PTGOilSystem.Web",
            "Views",
            "ContractJourney",
            "Details.cshtml"));

        Assert.Contains("asp-controller=\"Expenses\" asp-action=\"Edit\"", view);
        Assert.Contains("asp-route-id=\"@item.ExpenseTransactionId\"", view);
        Assert.Contains("asp-route-returnUrl=\"@ReturnUrl(ContractJourneyTabs.Details.Costs)\"", view);
    }

    private static ExpensesController BuildController(ApplicationDbContext db)
        => new(
            db,
            new AuditService(db),
            NullLogger<ExpensesController>.Instance)
        {
            TempData = BuildTempData()
        };

    private static void SeedReferenceData(ApplicationDbContext db)
    {
        db.Currencies.AddRange(
            new Currency { Id = 1, Code = "USD", Name = "US Dollar", Symbol = "$", IsActive = true },
            new Currency { Id = 2, Code = "EUR", Name = "Euro", Symbol = "EUR", IsActive = true });
        db.ExpenseTypes.Add(new ExpenseType { Id = 1, Code = "PORT", Name = "Port Charges", NamePersian = "هزینه بندری" });
        db.Companies.Add(new Company { Id = 1, Code = "PTG", Name = "PTG" });
        db.Suppliers.Add(new Supplier { Id = 1, Name = "Harbor Services" });
        db.Products.Add(new Product { Id = 1, Code = "GO", Name = "Gas Oil" });
        db.Trucks.Add(new Truck { Id = 1, PlateNumber = "AFG-101" });
    }

    [Fact]
    public async Task ImportPreview_Parses_And_Validates_Rows()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
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
            UnitPriceUsd = 400m
        });
        await db.SaveChangesAsync();

        var controller = BuildController(db);
        var model = new ExpenseImportViewModel
        {
            ImportFile = BuildImportFile(BuildExpenseWorkbookBytes())
        };

        var result = await controller.ImportPreview(model);

        var view = Assert.IsType<ViewResult>(result);
        Assert.Equal("ImportPreview", view.ViewName);
        var resultModel = Assert.IsType<ExpenseImportViewModel>(view.Model);
        Assert.Equal(2, resultModel.Rows.Count);
        Assert.Equal(1, resultModel.ValidCount);
        Assert.Equal(1, resultModel.ErrorCount);

        var validRow = resultModel.Rows[0];
        Assert.True(validRow.IsValid);
        Assert.Equal(1, validRow.ResolvedContractId);
        Assert.Equal(1200m, validRow.AmountUsd);

        var badRow = resultModel.Rows[1];
        Assert.False(badRow.IsValid);
        Assert.Contains(badRow.Errors, e => e.Contains("نوع مصرف"));
    }

    [Fact]
    public async Task ImportConfirm_Saves_Valid_Rows_As_Expense_And_Ledger()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        await db.SaveChangesAsync();

        var controller = BuildController(db);
        var model = new ExpenseImportViewModel
        {
            Rows = new List<ExpenseImportRowViewModel>
            {
                new()
                {
                    ExcelRowNumber = 2,
                    ExpenseDateText = "2026-04-25",
                    ExpenseTypeName = "هزینه بندری",
                    AmountText = "1200",
                    Currency = "USD",
                    Description = "تخلیه بندر"
                }
            }
        };

        var result = await controller.ImportConfirm(model);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(ExpensesController.Index), redirect.ActionName);

        var expense = Assert.Single(db.ExpenseTransactions);
        Assert.Equal(1, expense.ExpenseTypeId);
        Assert.Equal(1200m, expense.AmountUsd);
        Assert.Equal("USD", expense.Currency);

        var ledger = Assert.Single(db.LedgerEntries.Where(l => l.SourceType == "Expense"));
        Assert.Equal(expense.Id, ledger.SourceId);
        Assert.Equal(1200m, ledger.AmountUsd);
        Assert.Equal(LedgerSide.Debit, ledger.Side);
    }

    [Fact]
    public async Task ImportConfirm_With_Error_Rows_Does_Not_Save()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        await db.SaveChangesAsync();

        var controller = BuildController(db);
        var model = new ExpenseImportViewModel
        {
            Rows = new List<ExpenseImportRowViewModel>
            {
                new()
                {
                    ExcelRowNumber = 2,
                    ExpenseDateText = "2026-04-25",
                    ExpenseTypeName = "یک نوع ناموجود",
                    AmountText = "500",
                    Currency = "USD"
                }
            }
        };

        var result = await controller.ImportConfirm(model);

        var view = Assert.IsType<ViewResult>(result);
        Assert.Equal("ImportPreview", view.ViewName);
        Assert.Empty(db.ExpenseTransactions);
        Assert.Empty(db.LedgerEntries.Where(l => l.SourceType == "Expense"));
    }

    private static byte[] BuildExpenseWorkbookBytes()
    {
        using var stream = new MemoryStream();
        using (var document = DocumentFormat.OpenXml.Packaging.SpreadsheetDocument.Create(
            stream, DocumentFormat.OpenXml.SpreadsheetDocumentType.Workbook, true))
        {
            var workbookPart = document.AddWorkbookPart();
            workbookPart.Workbook = new DocumentFormat.OpenXml.Spreadsheet.Workbook();

            var worksheetPart = workbookPart.AddNewPart<DocumentFormat.OpenXml.Packaging.WorksheetPart>();
            worksheetPart.Worksheet = new DocumentFormat.OpenXml.Spreadsheet.Worksheet(
                new DocumentFormat.OpenXml.Spreadsheet.SheetData(
                    BuildImportRow(1, ("A", "تاریخ"), ("B", "نوع مصرف"), ("C", "مبلغ"), ("D", "ارز"), ("E", "قرارداد"), ("F", "شرح")),
                    BuildImportRow(2, ("A", "2026-04-25"), ("B", "هزینه بندری"), ("C", "1200"), ("D", "USD"), ("E", "PUR-001"), ("F", "تخلیه")),
                    BuildImportRow(3, ("A", "2026-04-25"), ("B", "یک نوع ناموجود"), ("C", "500"), ("D", "USD"))));

            var sheets = workbookPart.Workbook.AppendChild(new DocumentFormat.OpenXml.Spreadsheet.Sheets());
            sheets.Append(new DocumentFormat.OpenXml.Spreadsheet.Sheet
            {
                Id = workbookPart.GetIdOfPart(worksheetPart),
                SheetId = 1,
                Name = "expenses"
            });

            workbookPart.Workbook.Save();
        }

        return stream.ToArray();
    }

    private static DocumentFormat.OpenXml.Spreadsheet.Row BuildImportRow(uint rowIndex, params (string Col, string Val)[] cells)
    {
        var row = new DocumentFormat.OpenXml.Spreadsheet.Row { RowIndex = rowIndex };
        foreach (var (col, val) in cells)
        {
            row.Append(new DocumentFormat.OpenXml.Spreadsheet.Cell
            {
                CellReference = col + rowIndex,
                DataType = DocumentFormat.OpenXml.Spreadsheet.CellValues.String,
                CellValue = new DocumentFormat.OpenXml.Spreadsheet.CellValue(val)
            });
        }

        return row;
    }

    private static IFormFile BuildImportFile(byte[] bytes)
    {
        var stream = new MemoryStream(bytes);
        return new FormFile(stream, 0, bytes.Length, "ImportFile", "expenses.xlsx")
        {
            Headers = new HeaderDictionary(),
            ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"
        };
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
