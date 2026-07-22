using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Controllers;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Balance;
using PTGOilSystem.Web.Models.Entities;
using Xunit;

namespace PTGOilSystem.Web.Tests;

public class BalanceControllerTests
{
    [Fact]
    public async Task Contracts_Returns_Base_Balance_From_Direct_Contract_Relations()
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
                ContractNumber = "CON-001",
                ContractType = ContractType.Sale,
                Status = ContractStatus.Active,
                CompanyId = 1,
                ProductId = 1,
                CustomerId = 1,
                SupplierId = 1,
                ContractDate = new DateTime(2026, 4, 20),
                QuantityMt = 100m,
                PricingMethod = PricingMethod.Fixed,
                UnitPriceUsd = 500m
            },
            new Contract
            {
                Id = 2,
                ContractNumber = "CON-002",
                ContractType = ContractType.Sale,
                Status = ContractStatus.Active,
                CompanyId = 1,
                ProductId = 1,
                CustomerId = 2,
                SupplierId = 2,
                ContractDate = new DateTime(2026, 4, 20),
                QuantityMt = 50m,
                PricingMethod = PricingMethod.Fixed,
                UnitPriceUsd = 500m
            });
        db.Shipments.Add(new Shipment
        {
            Id = 1,
            ShipmentCode = "SHIP-01",
            ContractId = 1,
            QuantityMt = 50m
        });
        db.SalesTransactions.AddRange(
            new SalesTransaction
            {
                Id = 1,
                CompanyId = 1,
                ContractId = 1,
                CustomerId = 1,
                ProductId = 1,
                InvoiceNumber = "INV-1",
                SaleDate = new DateTime(2026, 4, 21),
                QuantityMt = 10m,
                UnitPriceUsd = 500m,
                TotalUsd = 5000m
            },
            new SalesTransaction
            {
                Id = 2,
                CompanyId = 1,
                ContractId = 2,
                CustomerId = 2,
                ProductId = 1,
                InvoiceNumber = "INV-2",
                SaleDate = new DateTime(2026, 4, 22),
                QuantityMt = 2m,
                UnitPriceUsd = 500m,
                TotalUsd = 1000m
            });
        db.ExpenseTypes.Add(new ExpenseType { Id = 1, Code = "PORT", Name = "Port", NamePersian = "هزینه بندری" });
        db.ExpenseTransactions.Add(new ExpenseTransaction
        {
            Id = 1,
            ExpenseTypeId = 1,
            ContractId = 1,
            ExpenseDate = new DateTime(2026, 4, 22),
            AmountUsd = 700m,
            Description = "PORT-1"
        });
        db.LedgerEntries.AddRange(
            new LedgerEntry
            {
                Id = 1,
                EntryDate = new DateTime(2026, 4, 21),
                Side = LedgerSide.Credit,
                AmountUsd = 5000m,
                Currency = "USD",
                Description = "Sale ledger",
                SourceType = "Sale",
                SourceId = 1,
                ContractId = 1,
                CustomerId = 1
            },
            new LedgerEntry
            {
                Id = 2,
                EntryDate = new DateTime(2026, 4, 22),
                Side = LedgerSide.Debit,
                AmountUsd = 700m,
                Currency = "USD",
                Description = "Expense ledger",
                SourceType = "Expense",
                SourceId = 1,
                ContractId = 1
            });
        await db.SaveChangesAsync();

        var controller = new BalanceController(db);

        var result = await controller.Contracts();

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<ContractsBalanceViewModel>(view.Model);
        var item = Assert.Single(model.Items.Where(i => i.Id == 1));
        Assert.Equal(5000m, item.TotalSalesUsd);
        Assert.Equal(700m, item.TotalExpensesUsd);
        Assert.Equal(2, item.RelatedLedgerCount);
        // مانده نمایشی = Σ(داده − گرفته)؛ منفی یعنی روی این قرارداد بدهکاریم.
        Assert.Equal(-4300m, item.BaseBalanceUsd);
        Assert.Equal(1, item.ShipmentCount);
    }

    [Fact]
    public async Task CustomerDetails_Redirects_To_Customer_Profile()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        db.Contracts.Add(new Contract
        {
            Id = 1,
            ContractNumber = "CON-001",
            ContractType = ContractType.Sale,
            Status = ContractStatus.Active,
            CompanyId = 1,
            ProductId = 1,
            CustomerId = 1,
            SupplierId = 1,
            ContractDate = new DateTime(2026, 4, 20),
            QuantityMt = 100m,
            PricingMethod = PricingMethod.Fixed,
            UnitPriceUsd = 500m
        });
        db.Contracts.Add(new Contract
        {
            Id = 2,
            ContractNumber = "CON-002",
            ContractType = ContractType.Sale,
            Status = ContractStatus.Closed,
            CompanyId = 1,
            ProductId = 1,
            CustomerId = 2,
            SupplierId = 2,
            ContractDate = new DateTime(2026, 4, 20),
            QuantityMt = 100m,
            PricingMethod = PricingMethod.Fixed,
            UnitPriceUsd = 500m
        });
        db.Shipments.Add(new Shipment { Id = 1, ShipmentCode = "SHIP-01", ContractId = 1, QuantityMt = 50m });
        db.SalesTransactions.Add(new SalesTransaction
        {
            Id = 1,
            CompanyId = 1,
            ContractId = 1,
            CustomerId = 1,
            ProductId = 1,
            InvoiceNumber = "INV-1",
            SaleDate = new DateTime(2026, 4, 21),
            QuantityMt = 10m,
            UnitPriceUsd = 500m,
            TotalUsd = 5000m
        });
        db.ExpenseTypes.Add(new ExpenseType { Id = 1, Code = "PORT", Name = "Port", NamePersian = "هزینه بندری" });
        db.ExpenseTransactions.AddRange(
            new ExpenseTransaction
            {
                Id = 1,
                ExpenseTypeId = 1,
                ContractId = 1,
                ExpenseDate = new DateTime(2026, 4, 22),
                AmountUsd = 700m
            },
            new ExpenseTransaction
            {
                Id = 2,
                ExpenseTypeId = 1,
                ContractId = 2,
                ExpenseDate = new DateTime(2026, 4, 22),
                AmountUsd = 200m
            });
        db.LedgerEntries.AddRange(
            new LedgerEntry
            {
                Id = 1,
                EntryDate = new DateTime(2026, 4, 21),
                Side = LedgerSide.Credit,
                AmountUsd = 5000m,
                Currency = "USD",
                Description = "Sale ledger",
                SourceType = "Sale",
                SourceId = 1,
                ContractId = 1,
                CustomerId = 1
            },
            new LedgerEntry
            {
                Id = 2,
                EntryDate = new DateTime(2026, 4, 22),
                Side = LedgerSide.Debit,
                AmountUsd = 700m,
                Currency = "USD",
                Description = "Expense ledger",
                SourceType = "Expense",
                SourceId = 1,
                ContractId = 1
            },
            new LedgerEntry
            {
                Id = 3,
                EntryDate = new DateTime(2026, 4, 22),
                Side = LedgerSide.Debit,
                AmountUsd = 200m,
                Currency = "USD",
                Description = "Other expense ledger",
                SourceType = "Expense",
                SourceId = 2,
                ContractId = 2
            });
        await db.SaveChangesAsync();

        var controller = new BalanceController(db);

        var result = await controller.CustomerDetails(1);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Details", redirect.ActionName);
        Assert.Equal("Customers", redirect.ControllerName);
        Assert.Equal(1, redirect.RouteValues?["id"]);
    }

    [Fact]
    public async Task SupplierDetails_Redirects_To_Supplier_Profile()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        db.Contracts.Add(new Contract
        {
            Id = 1,
            ContractNumber = "CON-001",
            ContractType = ContractType.Purchase,
            Status = ContractStatus.Active,
            CompanyId = 1,
            ProductId = 1,
            CustomerId = 1,
            SupplierId = 1,
            ContractDate = new DateTime(2026, 4, 20),
            QuantityMt = 100m,
            PricingMethod = PricingMethod.Fixed,
            UnitPriceUsd = 500m
        });
        db.SalesTransactions.Add(new SalesTransaction
        {
            Id = 1,
            CompanyId = 1,
            ContractId = 1,
            CustomerId = 1,
            ProductId = 1,
            InvoiceNumber = "INV-1",
            SaleDate = new DateTime(2026, 4, 21),
            QuantityMt = 10m,
            UnitPriceUsd = 500m,
            TotalUsd = 5000m
        });
        db.ExpenseTypes.Add(new ExpenseType { Id = 1, Code = "PORT", Name = "Port", NamePersian = "هزینه بندری" });
        db.ExpenseTransactions.Add(new ExpenseTransaction
        {
            Id = 1,
            ExpenseTypeId = 1,
            ContractId = 1,
            ExpenseDate = new DateTime(2026, 4, 22),
            AmountUsd = 700m
        });
        db.LedgerEntries.AddRange(
            new LedgerEntry
            {
                Id = 1,
                EntryDate = new DateTime(2026, 4, 21),
                Side = LedgerSide.Credit,
                AmountUsd = 5000m,
                Currency = "USD",
                Description = "Sale ledger",
                SourceType = "Sale",
                SourceId = 1,
                ContractId = 1
            },
            new LedgerEntry
            {
                Id = 2,
                EntryDate = new DateTime(2026, 4, 22),
                Side = LedgerSide.Debit,
                AmountUsd = 700m,
                Currency = "USD",
                Description = "Expense ledger",
                SourceType = "Expense",
                SourceId = 1,
                ContractId = 1
            });
        await db.SaveChangesAsync();

        var controller = new BalanceController(db);

        var result = await controller.SupplierDetails(1);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Details", redirect.ActionName);
        Assert.Equal("Suppliers", redirect.ControllerName);
        Assert.Equal(1, redirect.RouteValues?["id"]);
    }

    private static void SeedReferenceData(ApplicationDbContext db)
    {
        db.Companies.Add(new Company { Id = 1, Code = "PTG", Name = "PTG" });
        db.Products.Add(new Product { Id = 1, Code = "GO", Name = "Gas Oil" });
        db.Customers.AddRange(
            new Customer { Id = 1, Name = "Customer A" },
            new Customer { Id = 2, Name = "Customer B" });
        db.Suppliers.AddRange(
            new Supplier { Id = 1, Name = "Supplier A" },
            new Supplier { Id = 2, Name = "Supplier B" });
    }
}
