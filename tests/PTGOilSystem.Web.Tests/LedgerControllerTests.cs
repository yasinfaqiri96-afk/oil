using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PTGOilSystem.Web.Controllers;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.Ledger;
using Xunit;

namespace PTGOilSystem.Web.Tests;

public class LedgerControllerTests
{
    [Fact]
    public async Task Index_Applies_Base_Filters_And_Returns_Readable_Columns()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        db.LedgerEntries.AddRange(
            new LedgerEntry
            {
                Id = 1,
                EntryDate = new DateTime(2026, 4, 20),
                Side = LedgerSide.Credit,
                AmountUsd = 5000m,
                Currency = "USD",
                Description = "ثبت فروش فاکتور INV-1",
                SourceType = "Sale",
                SourceId = 1,
                Reference = "INV-1",
                ContractId = 1,
                CustomerId = 1,
                ShipmentId = 1
            },
            new LedgerEntry
            {
                Id = 2,
                EntryDate = new DateTime(2026, 4, 21),
                Side = LedgerSide.Debit,
                AmountUsd = 700m,
                Currency = "USD",
                Description = "هزینه بندری",
                SourceType = "Expense",
                SourceId = 2,
                Reference = "PORT-2",
                ContractId = 1,
                SupplierId = 1,
                ShipmentId = 1
            });
        await db.SaveChangesAsync();

        var controller = BuildController(db);

        var result = await controller.Index(new LedgerIndexFilterViewModel
        {
            FromDate = new DateTime(2026, 4, 19),
            ToDate = new DateTime(2026, 4, 20),
            SourceType = ["Sale"],
            ContractId = [1],
            CustomerId = [1],
            Reference = "INV",
            Side = [LedgerSide.Credit]
        });

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<LedgerIndexViewModel>(view.Model);
        var item = Assert.Single(model.Items);
        Assert.Equal(1, item.Id);
        Assert.Equal("بستانکار", item.SideName);
        Assert.Equal("CUST A", item.CustomerName);
        Assert.Equal("CON-001", item.ContractNumber);
        Assert.Equal("SHIP-01", item.ShipmentCode);
    }

    [Fact]
    public async Task Details_Returns_Sale_Trace_With_Real_Relations()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        db.SalesTransactions.Add(new SalesTransaction
        {
            Id = 1,
            CompanyId = 1,
            ContractId = 1,
            CustomerId = 1,
            ProductId = 1,
            ShipmentId = 1,
            InvoiceNumber = "INV-100",
            SaleDate = new DateTime(2026, 4, 20),
            QuantityMt = 10m,
            UnitPriceUsd = 500m,
            TotalUsd = 5000m,
            Notes = "ارسال اول"
        });
        db.LedgerEntries.Add(new LedgerEntry
        {
            Id = 10,
            EntryDate = new DateTime(2026, 4, 20),
            Side = LedgerSide.Credit,
            AmountUsd = 5000m,
            Currency = "USD",
            Description = "ثبت فروش",
            SourceType = "Sale",
            SourceId = 1,
            Reference = "INV-100",
            ContractId = 1,
            CustomerId = 1,
            ShipmentId = 1
        });
        await db.SaveChangesAsync();

        var controller = BuildController(db);

        var result = await controller.Details(10);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<LedgerDetailsViewModel>(view.Model);
        Assert.Equal("بستانکار", model.SideName);
        Assert.Equal("CON-001", model.Contract?.Label);
        Assert.Equal("CUST A", model.Customer?.Label);
        Assert.Equal("SHIP-01", model.Shipment?.Label);
        Assert.NotNull(model.SourceTrace);
        Assert.Equal("Sales", model.SourceTrace!.ControllerName);
        Assert.Contains(model.SourceTrace.Fields, f => f.Label == "فاکتور / مرجع" && f.Value == "INV-100");
        Assert.Contains(model.SourceTrace.Fields, f => f.Label == "مشتری" && f.Value == "CUST A");
    }

    [Fact]
    public async Task Details_Returns_Expense_Trace_With_Supplier_And_Shipment()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        db.ExpenseTypes.Add(new ExpenseType { Id = 1, Code = "PORT", Name = "Port", NamePersian = "هزینه بندری" });
        db.ExpenseTransactions.Add(new ExpenseTransaction
        {
            Id = 2,
            ExpenseTypeId = 1,
            ContractId = 1,
            ShipmentId = 1,
            ExpenseDate = new DateTime(2026, 4, 21),
            AmountUsd = 700m,
            Description = "فاکتور PORT-2"
        });
        db.LedgerEntries.Add(new LedgerEntry
        {
            Id = 11,
            EntryDate = new DateTime(2026, 4, 21),
            Side = LedgerSide.Debit,
            AmountUsd = 700m,
            Currency = "USD",
            Description = "ثبت هزینه بندری",
            SourceType = "Expense",
            SourceId = 2,
            Reference = "PORT-2",
            ContractId = 1,
            SupplierId = 1,
            ShipmentId = 1
        });
        await db.SaveChangesAsync();

        var controller = BuildController(db);

        var result = await controller.Details(11);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<LedgerDetailsViewModel>(view.Model);
        Assert.Equal("بدهکار", model.SideName);
        Assert.Equal("SUP A", model.Supplier?.Label);
        Assert.Equal("SHIP-01", model.Shipment?.Label);
        Assert.NotNull(model.SourceTrace);
        Assert.Equal("Expenses", model.SourceTrace!.ControllerName);
        Assert.Contains(model.SourceTrace.Fields, f => f.Label == "نوع مصرف" && f.Value == "هزینه بندری");
        Assert.Contains(model.SourceTrace.Fields, f => f.Label == "Shipment" && f.Value == "SHIP-01");
    }

    [Fact]
    public async Task Details_Returns_ThreeWaySettlement_Trace_With_Source_Link_Label()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        db.ThreeWaySettlements.Add(new ThreeWaySettlement
        {
            Id = 50,
            SettlementDate = new DateTime(2026, 6, 6),
            Status = ThreeWaySettlementStatus.Cancelled,
            PayeeType = ThreeWayPayeeType.Supplier,
            CustomerId = 1,
            SupplierId = 1,
            CustomerPaidAmount = 1000m,
            SupplierAcceptedAmount = 950m,
            Currency = "USD",
            FxRateToUsd = 1m,
            CustomerPaidUsd = 1000m,
            SupplierAcceptedUsd = 950m,
            DifferenceUsd = 50m,
            DifferenceReason = DifferenceReason.Commission,
            CustomerSaleContractId = 1,
            SupplierPurchaseContractId = 1,
            HawalaReference = "HW-50",
            CancellationReason = "Wrong hawala",
            CancelledAtUtc = new DateTime(2026, 6, 7)
        });
        db.LedgerEntries.Add(new LedgerEntry
        {
            Id = 50,
            EntryDate = new DateTime(2026, 6, 7),
            Side = LedgerSide.Credit,
            AmountUsd = 1000m,
            Currency = "USD",
            SourceType = ThreeWaySettlementController.CancellationLedgerSourceType,
            SourceId = 50,
            Reference = "HW-50",
            Description = "Three-way settlement cancellation",
            CustomerId = 1
        });
        await db.SaveChangesAsync();

        var controller = BuildController(db);

        var result = await controller.Details(50);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<LedgerDetailsViewModel>(view.Model);

        Assert.Equal("برگشت تسویه سه‌طرفه", model.SourceTypeLabel);
        Assert.NotNull(model.SourceTrace);
        Assert.Equal("ThreeWaySettlement", model.SourceTrace!.ControllerName);
        Assert.Equal("برگشت تسویه سه‌طرفه", model.SourceTrace.Title);
        Assert.Contains(model.SourceTrace.Fields, f => f.Label == "شماره حواله / مرجع" && f.Value == "HW-50");
        Assert.Contains(model.SourceTrace.Fields, f => f.Label == "دلیل لغو" && f.Value == "Wrong hawala");
    }

    private static LedgerController BuildController(ApplicationDbContext db)
        => new(db, NullLogger<LedgerController>.Instance);

    private static void SeedReferenceData(ApplicationDbContext db)
    {
        db.Companies.Add(new Company { Id = 1, Code = "PTG", Name = "PTG" });
        db.Products.Add(new Product { Id = 1, Code = "GO", Name = "Gas Oil" });
        db.Contracts.Add(new Contract
        {
            Id = 1,
            ContractNumber = "CON-001",
            ContractType = ContractType.Sale,
            CompanyId = 1,
            ProductId = 1,
            CustomerId = 1,
            ContractDate = new DateTime(2026, 4, 18),
            QuantityMt = 100m,
            PricingMethod = PricingMethod.Fixed,
            UnitPriceUsd = 500m
        });
        db.Customers.Add(new Customer { Id = 1, Name = "CUST A" });
        db.Suppliers.Add(new Supplier { Id = 1, Name = "SUP A" });
        db.Shipments.Add(new Shipment
        {
            Id = 1,
            ShipmentCode = "SHIP-01",
            ContractId = 1,
            QuantityMt = 50m
        });
    }
}
