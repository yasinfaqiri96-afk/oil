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
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.Reconciliation;
using PTGOilSystem.Web.Models.Sales;
using PTGOilSystem.Web.Services;
using Xunit;

namespace PTGOilSystem.Web.Tests;

public class SalesControllerTests
{
    [Fact]
    public async Task Create_Get_Preselects_Contract_SourcePurchaseContract_And_ReturnUrl()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        SeedSaleContract(db, 1);
        SeedPurchaseContract(db, 2);
        await db.SaveChangesAsync();

        var controller = BuildController(db);

        var result = await controller.Create(
            contractId: 1,
            sourcePurchaseContractId: 2,
            returnUrl: "/Contracts/Details/1?tab=sales");

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<SalesCreateViewModel>(view.Model);
        Assert.Equal(1, model.ContractId);
        Assert.Equal(2, model.SourcePurchaseContractId);
        Assert.Equal("/Contracts/Details/1?tab=sales", model.ReturnUrl);
    }

    [Fact]
    public async Task Create_Post_Redirects_To_ReturnUrl_When_Provided()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        SeedSaleContract(db, 1);
        await db.SaveChangesAsync();

        var controller = BuildController(db);
        controller.Url = BuildUrlHelper();

        var result = await controller.Create(new SalesCreateViewModel
        {
            SaleStage = SaleStage.PreSale,
            ContractId = 1,
            CompanyId = 1,
            CustomerId = 1,
            ProductId = 1,
            DestinationLocationId = 1,
            SaleDate = new DateTime(2026, 4, 23),
            QuantityMt = 15m,
            UnitPriceUsd = 480m,
            InvoiceNumber = "INV-RETURN-001",
            ReturnUrl = "/Contracts/Details/1?tab=sales"
        });

        var redirect = Assert.IsType<RedirectResult>(result);
        Assert.Equal("/Contracts/Details/1?tab=sales", redirect.Url);
    }

    [Fact]
    public async Task Create_Post_Ignores_External_ReturnUrl()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        SeedSaleContract(db, 1);
        await db.SaveChangesAsync();

        var controller = BuildController(db);
        controller.Url = BuildUrlHelper();

        var result = await controller.Create(new SalesCreateViewModel
        {
            SaleStage = SaleStage.PreSale,
            ContractId = 1,
            CompanyId = 1,
            CustomerId = 1,
            ProductId = 1,
            DestinationLocationId = 1,
            SaleDate = new DateTime(2026, 4, 23),
            QuantityMt = 15m,
            UnitPriceUsd = 480m,
            InvoiceNumber = "INV-RETURN-EXTERNAL-001",
            ReturnUrl = "https://evil.com"
        });

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Details", redirect.ActionName);
    }

    [Fact]
    public async Task Create_Post_Blocks_When_Free_Stock_Is_Insufficient()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        SeedSaleContract(db, 1);
        SeedPurchaseContract(db, 2);
        await db.SaveChangesAsync();

        var controller = BuildController(db);

        var result = await controller.Create(new SalesCreateViewModel
        {
            SaleStage = SaleStage.TerminalStock,
            ContractId = 1,
            CompanyId = 1,
            CustomerId = 1,
            ProductId = 1,
            DestinationLocationId = 1,
            SourceTerminalId = 1,
            SourceStorageTankId = 1,
            SourcePurchaseContractId = 2,
            SaleDate = new DateTime(2026, 4, 23),
            QuantityMt = 20m,
            UnitPriceUsd = 450m,
            InvoiceNumber = "INV-001"
        });

        var view = Assert.IsType<ViewResult>(result);
        Assert.IsType<SalesCreateViewModel>(view.Model);
        Assert.False(controller.ModelState.IsValid);
        Assert.NotEmpty(controller.ModelState[string.Empty]!.Errors);
    }

    [Fact]
    public async Task Create_Post_Blocks_When_Contract_Does_Not_Match_Selected_Data()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        db.Products.Add(new Product { Id = 2, Code = "JET", Name = "Jet Fuel" });
        SeedSaleContract(db, 1);
        await db.SaveChangesAsync();

        var controller = BuildController(db);

        var result = await controller.Create(new SalesCreateViewModel
        {
            SaleStage = SaleStage.PreSale,
            ContractId = 1,
            CompanyId = 1,
            CustomerId = 1,
            ProductId = 2,
            DestinationLocationId = 1,
            SaleDate = new DateTime(2026, 4, 23),
            QuantityMt = 10m,
            UnitPriceUsd = 500m,
            InvoiceNumber = "INV-002"
        });

        var view = Assert.IsType<ViewResult>(result);
        Assert.IsType<SalesCreateViewModel>(view.Model);
        Assert.False(controller.ModelState.IsValid);
        Assert.NotEmpty(controller.ModelState[nameof(SalesCreateViewModel.ContractId)]!.Errors);
    }

    [Fact]
    public async Task Create_Post_Allows_Shipment_Sale_Without_SaleContract()
    {
        // فروش مبتنی بر Shipment نباید قرارداد فروش را اجباری کند؛ فروش مستقیم از محموله بدون قرارداد مجاز است.
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        SeedPurchaseContract(db, 2);
        SeedSaleContract(db, 3);
        db.Shipments.Add(new Shipment
        {
            Id = 1,
            ShipmentCode = "SHIP-NOCONTRACT-1",
            ContractId = 3,
            QuantityMt = 100m
        });
        db.InventoryMovements.Add(new InventoryMovement
        {
            ProductId = 1,
            ContractId = 2,
            TerminalId = 1,
            StorageTankId = 1,
            Direction = MovementDirection.In,
            MovementDate = new DateTime(2026, 4, 20),
            QuantityMt = 100m,
            ReferenceDocument = "GRN-NC-1"
        });
        await db.SaveChangesAsync();

        var controller = BuildController(db);

        var result = await controller.Create(new SalesCreateViewModel
        {
            SaleStage = SaleStage.TerminalStock,
            ContractId = null,
            CompanyId = 1,
            CustomerId = 1,
            ProductId = 1,
            DestinationLocationId = 1,
            ShipmentId = 1,
            SourceTerminalId = 1,
            SourceStorageTankId = 1,
            SourcePurchaseContractId = 2,
            SaleDate = new DateTime(2026, 4, 23),
            QuantityMt = 30m,
            UnitPriceUsd = 500m,
            InvoiceNumber = "INV-NOCONTRACT"
        });

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Details", redirect.ActionName);

        var sale = await db.SalesTransactions.SingleAsync();
        Assert.Null(sale.ContractId);
        Assert.Equal(1, sale.ShipmentId);
        Assert.Equal("INV-NOCONTRACT", sale.InvoiceNumber);
    }

    [Fact]
    public async Task Create_Post_MultiProduct_Shipment_Requires_Source_Contract()
    {
        // محموله چندمحصولی: بدون انتخاب قرارداد خرید منبع، فروش مبتنی بر Shipment باید رد شود.
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        db.Products.Add(new Product { Id = 2, Code = "JET", Name = "Jet Fuel" });
        SeedPurchaseContract(db, 2, productId: 1);
        SeedPurchaseContract(db, 3, productId: 2);
        db.Shipments.Add(new Shipment { Id = 1, ShipmentCode = "SHIP-MP", QuantityMt = 1000m, DestinationLocationId = 1 });
        db.ShipmentContracts.AddRange(
            new ShipmentContract { ShipmentId = 1, ContractId = 2, QuantityMt = 600m },
            new ShipmentContract { ShipmentId = 1, ContractId = 3, QuantityMt = 400m });
        await db.SaveChangesAsync();

        var controller = BuildController(db);

        var result = await controller.Create(new SalesCreateViewModel
        {
            SaleStage = SaleStage.InTransit,
            CompanyId = 1,
            CustomerId = 1,
            ProductId = 1,
            DestinationLocationId = 1,
            ShipmentId = 1,
            SaleDate = new DateTime(2026, 6, 25),
            QuantityMt = 10m,
            UnitPriceUsd = 500m,
            InvoiceNumber = "INV-MP-1"
        });

        Assert.IsType<ViewResult>(result);
        Assert.False(controller.ModelState.IsValid);
        Assert.Contains(
            controller.ModelState[nameof(SalesCreateViewModel.SourcePurchaseContractId)]!.Errors,
            e => e.ErrorMessage.Contains("چند محصول"));
        Assert.Empty(await db.SalesTransactions.ToListAsync());
    }

    [Fact]
    public async Task Create_Post_Shipment_Rejects_Product_Not_Matching_Source_Contract()
    {
        // محصول نامرتبط با قرارداد خرید منبعِ محموله باید در بک‌اند رد شود.
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        db.Products.Add(new Product { Id = 2, Code = "JET", Name = "Jet Fuel" });
        SeedPurchaseContract(db, 2, productId: 1);
        db.Shipments.Add(new Shipment { Id = 1, ShipmentCode = "SHIP-1P", QuantityMt = 600m, DestinationLocationId = 1 });
        db.ShipmentContracts.Add(new ShipmentContract { ShipmentId = 1, ContractId = 2, QuantityMt = 600m });
        await db.SaveChangesAsync();

        var controller = BuildController(db);

        var result = await controller.Create(new SalesCreateViewModel
        {
            SaleStage = SaleStage.InTransit,
            CompanyId = 1,
            CustomerId = 1,
            ProductId = 2, // نامرتبط با محصول قرارداد منبع (۱)
            DestinationLocationId = 1,
            ShipmentId = 1,
            SourcePurchaseContractId = 2,
            SaleDate = new DateTime(2026, 6, 25),
            QuantityMt = 10m,
            UnitPriceUsd = 500m,
            InvoiceNumber = "INV-1P-1"
        });

        Assert.IsType<ViewResult>(result);
        Assert.False(controller.ModelState.IsValid);
        Assert.Contains(
            controller.ModelState[nameof(SalesCreateViewModel.ProductId)]!.Errors,
            e => e.ErrorMessage.Contains("قرارداد خرید منبع"));
        Assert.Empty(await db.SalesTransactions.ToListAsync());
    }

    [Fact]
    public async Task Create_Post_Persists_TerminalStock_Sale_From_PurchaseBacked_Tank_Stock()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        SeedPurchaseContract(db, 2);
        SeedSaleContract(db, 3);
        db.Shipments.Add(new Shipment
        {
            Id = 1,
            ShipmentCode = "SHIP-SALE-1",
            ContractId = 3,
            QuantityMt = 100m
        });
        db.InventoryMovements.Add(new InventoryMovement
        {
            ProductId = 1,
            ContractId = 2,
            TerminalId = 1,
            StorageTankId = 1,
            Direction = MovementDirection.In,
            MovementDate = new DateTime(2026, 4, 20),
            QuantityMt = 100m,
            ReferenceDocument = "GRN-1"
        });
        await db.SaveChangesAsync();

        var controller = BuildController(db);

        var result = await controller.Create(new SalesCreateViewModel
        {
            SaleStage = SaleStage.TerminalStock,
            ContractId = 3,
            CompanyId = 1,
            CustomerId = 1,
            ProductId = 1,
            DestinationLocationId = 1,
            ShipmentId = 1,
            SourceTerminalId = 1,
            SourceStorageTankId = 1,
            SourcePurchaseContractId = 2,
            SaleDate = new DateTime(2026, 4, 23),
            QuantityMt = 30m,
            UnitPriceUsd = 500m,
            InvoiceNumber = "INV-003",
            Notes = "Cash customer"
        });

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Details", redirect.ActionName);

        var sale = await db.SalesTransactions.SingleAsync();
        Assert.Equal(SaleStage.TerminalStock, sale.SaleStage);
        Assert.Equal(15000m, sale.TotalUsd);
        Assert.Equal("INV-003", sale.InvoiceNumber);
        Assert.Equal(3, sale.ContractId);
        Assert.Equal(1, sale.ShipmentId);

        var ledger = await db.LedgerEntries.SingleAsync();
        Assert.Equal("Sale", ledger.SourceType);
        Assert.Equal(sale.Id, ledger.SourceId);
        Assert.Equal("INV-003", ledger.Reference);
        Assert.Equal(LedgerSide.Credit, ledger.Side);
        Assert.Equal(3, ledger.ContractId);
        Assert.Equal(1, ledger.ShipmentId);

        var stockOut = await db.InventoryMovements
            .Where(m => m.Direction == MovementDirection.Out)
            .SingleAsync();
        Assert.Equal(2, stockOut.ContractId);
        Assert.Equal(sale.Id, stockOut.SalesTransactionId);
        Assert.Equal(1, stockOut.TerminalId);
        Assert.Equal(1, stockOut.StorageTankId);
        Assert.Equal(30m, stockOut.QuantityMt);
        Assert.Equal("INV-003", stockOut.ReferenceDocument);

        var stock = new StockService(db);
        var purchaseContractFreeQuantity = await stock.GetFreeQuantityMtAsync(1, terminalId: 1, contractId: 2, storageTankId: 1);
        var saleContractFreeQuantity = await stock.GetFreeQuantityMtAsync(1, terminalId: 1, contractId: 3, storageTankId: 1);
        var totalFreeQuantity = await stock.GetFreeQuantityMtAsync(1, terminalId: 1, storageTankId: 1);
        Assert.Equal(70m, purchaseContractFreeQuantity);
        Assert.Equal(0m, saleContractFreeQuantity);
        Assert.Equal(70m, totalFreeQuantity);

        var audits = await db.AuditLogs.OrderBy(a => a.Id).ToListAsync();
        Assert.Equal(2, audits.Count);
        Assert.Contains(audits, audit => audit.EntityName == nameof(SalesTransaction) && (audit.Diff?.Contains("SourcePurchaseContractId=2") ?? false));
        Assert.Contains(audits, audit => audit.EntityName == nameof(InventoryMovement) && (audit.Diff?.Contains($"SaleId={sale.Id}") ?? false));

        var reconciliation = new ReconciliationController(db);
        var reconciliationResult = await reconciliation.OpenShipments();
        var reconciliationView = Assert.IsType<ViewResult>(reconciliationResult);
        var reconciliationModel = Assert.IsType<OpenShipmentsViewModel>(reconciliationView.Model);
        Assert.DoesNotContain(reconciliationModel.ShipmentsWithoutSales, shipment => shipment.ShipmentCode == "SHIP-SALE-1");
    }

    [Fact]
    public async Task Create_Post_Allows_TerminalStock_Sale_Without_Sales_Contract_And_Traces_Ledger_To_SourcePurchaseContract()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        SeedPurchaseContract(db, 2);
        db.InventoryMovements.Add(new InventoryMovement
        {
            ProductId = 1,
            ContractId = 2,
            TerminalId = 1,
            StorageTankId = 1,
            Direction = MovementDirection.In,
            MovementDate = new DateTime(2026, 4, 20),
            QuantityMt = 100m,
            ReferenceDocument = "GRN-SPOT-1"
        });
        await db.SaveChangesAsync();

        var controller = BuildController(db);

        var result = await controller.Create(new SalesCreateViewModel
        {
            SaleStage = SaleStage.TerminalStock,
            CompanyId = 1,
            CustomerId = 1,
            ProductId = 1,
            SourceTerminalId = 1,
            SourceStorageTankId = 1,
            SourcePurchaseContractId = 2,
            SaleDate = new DateTime(2026, 4, 23),
            QuantityMt = 20m,
            UnitPriceUsd = 500m,
            InvoiceNumber = "INV-SPOT-001",
            Notes = "Invoice sale"
        });

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Details", redirect.ActionName);

        var sale = await db.SalesTransactions.SingleAsync();
        Assert.Equal(SaleStage.TerminalStock, sale.SaleStage);
        Assert.Null(sale.ContractId);
        Assert.Equal(10000m, sale.TotalUsd);

        var ledger = await db.LedgerEntries.SingleAsync();
        Assert.Equal("Sale", ledger.SourceType);
        Assert.Equal(sale.Id, ledger.SourceId);
        Assert.Equal("INV-SPOT-001", ledger.Reference);
        Assert.Equal(LedgerSide.Credit, ledger.Side);
        Assert.Equal(2, ledger.ContractId);
        Assert.Equal(1, ledger.CustomerId);

        var stockOut = await db.InventoryMovements
            .Where(m => m.Direction == MovementDirection.Out)
            .SingleAsync();
        Assert.Equal(2, stockOut.ContractId);
        Assert.Equal(sale.Id, stockOut.SalesTransactionId);
        Assert.Equal(20m, stockOut.QuantityMt);

        var stock = new StockService(db);
        var purchaseContractFreeQuantity = await stock.GetFreeQuantityMtAsync(1, terminalId: 1, contractId: 2, storageTankId: 1);
        Assert.Equal(80m, purchaseContractFreeQuantity);
    }

    [Fact]
    public async Task Create_Post_TerminalStock_Without_SalesContract_Uses_SourcePurchaseContract_Company_When_FormCompanyIsStale()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        db.Companies.Add(new Company { Id = 2, Code = "PTG2", Name = "PTG 2" });
        SeedPurchaseContract(db, 2, companyId: 2);
        db.InventoryMovements.Add(new InventoryMovement
        {
            ProductId = 1,
            ContractId = 2,
            TerminalId = 1,
            StorageTankId = 1,
            Direction = MovementDirection.In,
            MovementDate = new DateTime(2026, 4, 20),
            QuantityMt = 100m,
            ReferenceDocument = "GRN-STOCK-COMPANY-2"
        });
        await db.SaveChangesAsync();

        var controller = BuildController(db);

        var result = await controller.Create(new SalesCreateViewModel
        {
            SaleStage = SaleStage.TerminalStock,
            CompanyId = 1,
            CustomerId = 1,
            ProductId = 1,
            SourceTerminalId = 1,
            SourceStorageTankId = 1,
            SourcePurchaseContractId = 2,
            SaleDate = new DateTime(2026, 4, 23),
            QuantityMt = 20m,
            UnitPriceUsd = 500m,
            InvoiceNumber = "INV-SOURCE-COMPANY-SYNC"
        });

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Details", redirect.ActionName);

        var sale = await db.SalesTransactions.SingleAsync();
        Assert.Null(sale.ContractId);
        Assert.Equal(2, sale.CompanyId);

        var stockOut = await db.InventoryMovements
            .Where(m => m.Direction == MovementDirection.Out)
            .SingleAsync();
        Assert.Equal(2, stockOut.ContractId);
        Assert.Equal(sale.Id, stockOut.SalesTransactionId);
    }

    [Fact]
    public async Task Create_Post_TerminalStock_Splits_StockOut_When_SelectedTankHasMixedPurchaseStock()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        SeedPurchaseContract(db, 2);
        SeedPurchaseContract(db, 3);
        db.InventoryMovements.AddRange(
            new InventoryMovement
            {
                ProductId = 1,
                ContractId = 2,
                TerminalId = 1,
                StorageTankId = 1,
                Direction = MovementDirection.In,
                MovementDate = new DateTime(2026, 4, 20),
                QuantityMt = 1000m,
                ReferenceDocument = "GRN-MIX-1"
            },
            new InventoryMovement
            {
                ProductId = 1,
                ContractId = 3,
                TerminalId = 1,
                StorageTankId = 1,
                Direction = MovementDirection.In,
                MovementDate = new DateTime(2026, 4, 21),
                QuantityMt = 2000m,
                ReferenceDocument = "GRN-MIX-2"
            });
        await db.SaveChangesAsync();

        var controller = BuildController(db);

        var result = await controller.Create(new SalesCreateViewModel
        {
            SaleStage = SaleStage.TerminalStock,
            CompanyId = 1,
            CustomerId = 1,
            ProductId = 1,
            SourceTerminalId = 1,
            SourceStorageTankId = 1,
            SourcePurchaseContractId = 2,
            SaleDate = new DateTime(2026, 4, 23),
            QuantityMt = 2470m,
            UnitPriceUsd = 500m,
            InvoiceNumber = "INV-MIXED-STOCK"
        });

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Details", redirect.ActionName);

        var sale = await db.SalesTransactions.SingleAsync();
        var stockOuts = await db.InventoryMovements
            .Where(m => m.Direction == MovementDirection.Out)
            .OrderBy(m => m.ContractId)
            .ToListAsync();

        Assert.Equal(2, stockOuts.Count);
        Assert.All(stockOuts, movement => Assert.Equal(sale.Id, movement.SalesTransactionId));
        Assert.Equal(1000m, stockOuts.Single(m => m.ContractId == 2).QuantityMt);
        Assert.Equal(1470m, stockOuts.Single(m => m.ContractId == 3).QuantityMt);

        var stock = new StockService(db);
        Assert.Equal(0m, await stock.GetFreeQuantityMtAsync(1, terminalId: 1, contractId: 2, storageTankId: 1));
        Assert.Equal(530m, await stock.GetFreeQuantityMtAsync(1, terminalId: 1, contractId: 3, storageTankId: 1));
        Assert.Equal(530m, await stock.GetFreeQuantityMtAsync(1, terminalId: 1, storageTankId: 1));

        var detailsResult = await controller.Details(sale.Id);
        var detailsView = Assert.IsType<ViewResult>(detailsResult);
        var detailsModel = Assert.IsType<SalesDetailsViewModel>(detailsView.Model);
        Assert.Equal(2, detailsModel.InventoryMovementCount);
        Assert.Contains("PUR-002", detailsModel.SourcePurchaseContractNumber);
        Assert.Contains("PUR-003", detailsModel.SourcePurchaseContractNumber);
    }

    [Fact]
    public async Task Create_Post_Allows_PreSale_Without_Stock_Decrement_But_Creates_Ledger()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        SeedSaleContract(db, 1);
        await db.SaveChangesAsync();

        var controller = BuildController(db);

        var result = await controller.Create(new SalesCreateViewModel
        {
            SaleStage = SaleStage.PreSale,
            ContractId = 1,
            CompanyId = 1,
            CustomerId = 1,
            ProductId = 1,
            DestinationLocationId = 1,
            SaleDate = new DateTime(2026, 4, 23),
            QuantityMt = 15m,
            UnitPriceUsd = 480m,
            InvoiceNumber = "INV-PS-001"
        });

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Details", redirect.ActionName);

        var sale = await db.SalesTransactions.SingleAsync();
        Assert.Equal(SaleStage.PreSale, sale.SaleStage);
        Assert.Equal(7200m, sale.TotalUsd);

        var ledger = await db.LedgerEntries.SingleAsync();
        Assert.Equal("Sale", ledger.SourceType);
        Assert.Equal(sale.Id, ledger.SourceId);

        Assert.Empty(await db.InventoryMovements.Where(m => m.Direction == MovementDirection.Out).ToListAsync());
    }

    [Fact]
    public async Task Create_Post_Allows_InTransit_With_Shipment_And_Does_Not_Decrement_Stock()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        SeedSaleContract(db, 1);
        db.Shipments.Add(new Shipment
        {
            Id = 1,
            ShipmentCode = "SHIP-TRANSIT-1",
            ContractId = 1,
            QuantityMt = 25m
        });
        await db.SaveChangesAsync();

        var controller = BuildController(db);

        var result = await controller.Create(new SalesCreateViewModel
        {
            SaleStage = SaleStage.InTransit,
            ContractId = 1,
            CompanyId = 1,
            CustomerId = 1,
            ProductId = 1,
            DestinationLocationId = 1,
            ShipmentId = 1,
            SaleDate = new DateTime(2026, 4, 23),
            QuantityMt = 20m,
            UnitPriceUsd = 500m,
            InvoiceNumber = "INV-TR-001"
        });

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Details", redirect.ActionName);

        var sale = await db.SalesTransactions.SingleAsync();
        Assert.Equal(SaleStage.InTransit, sale.SaleStage);
        Assert.Equal(1, sale.ShipmentId);

        var ledger = await db.LedgerEntries.SingleAsync();
        Assert.Equal(1, ledger.ShipmentId);
        Assert.Empty(await db.InventoryMovements.Where(m => m.Direction == MovementDirection.Out).ToListAsync());
    }

    [Fact]
    public async Task Create_Post_Blocks_TerminalStock_When_Tank_Is_Missing()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        SeedSaleContract(db, 1);
        SeedPurchaseContract(db, 2);
        await db.SaveChangesAsync();

        var controller = BuildController(db);

        var result = await controller.Create(new SalesCreateViewModel
        {
            SaleStage = SaleStage.TerminalStock,
            ContractId = 1,
            CompanyId = 1,
            CustomerId = 1,
            ProductId = 1,
            DestinationLocationId = 1,
            SourceTerminalId = 1,
            SourcePurchaseContractId = 2,
            SaleDate = new DateTime(2026, 4, 23),
            QuantityMt = 5m,
            UnitPriceUsd = 500m,
            InvoiceNumber = "INV-NO-TANK"
        });

        var view = Assert.IsType<ViewResult>(result);
        Assert.IsType<SalesCreateViewModel>(view.Model);
        Assert.False(controller.ModelState.IsValid);
        Assert.NotEmpty(controller.ModelState[nameof(SalesCreateViewModel.SourceStorageTankId)]!.Errors);
    }

    [Fact]
    public async Task Details_Returns_Ledger_Trace_For_Sale()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        SeedSaleContract(db, 1);
        SeedPurchaseContract(db, 2);
        db.Shipments.Add(new Shipment
        {
            Id = 1,
            ShipmentCode = "SHIP-DETAIL-1",
            ContractId = 1,
            QuantityMt = 20m
        });
        db.SalesTransactions.Add(new SalesTransaction
        {
            Id = 1,
            ContractId = 1,
            CompanyId = 1,
            CustomerId = 1,
            ProductId = 1,
            DestinationLocationId = 1,
            ShipmentId = 1,
            SaleStage = SaleStage.Border,
            InvoiceNumber = "INV-004",
            SaleDate = new DateTime(2026, 4, 23),
            QuantityMt = 10m,
            UnitPriceUsd = 400m,
            TotalUsd = 4000m
        });
        db.StorageTanks.Add(new StorageTank
        {
            Id = 2,
            TerminalId = 1,
            TankCode = "TK-02",
            ProductId = 1,
            CapacityMt = 1000m
        });
        db.InventoryMovements.AddRange(
            new InventoryMovement
            {
                Id = 10,
                ProductId = 1,
                ContractId = 2,
                TerminalId = 1,
                StorageTankId = 1,
                SalesTransactionId = 1,
                Direction = MovementDirection.Out,
                MovementDate = new DateTime(2026, 4, 23),
                QuantityMt = 10m,
                ReferenceDocument = "INV-004",
                Notes = "linked"
            },
            new InventoryMovement
            {
                Id = 20,
                ProductId = 1,
                ContractId = 2,
                TerminalId = 1,
                StorageTankId = 2,
                Direction = MovementDirection.Out,
                MovementDate = new DateTime(2026, 4, 23),
                QuantityMt = 10m,
                ReferenceDocument = "INV-004",
                Notes = "unlinked"
            });
        db.LedgerEntries.Add(new LedgerEntry
        {
            Id = 1,
            EntryDate = new DateTime(2026, 4, 23),
            Side = LedgerSide.Credit,
            AmountUsd = 4000m,
            Currency = "USD",
            Description = "Sale entry INV-004",
            SourceType = "Sale",
            SourceId = 1,
            Reference = "INV-004",
            ContractId = 1,
            CustomerId = 1,
            ShipmentId = 1
        });
        await db.SaveChangesAsync();

        var controller = BuildController(db);

        var result = await controller.Details(1);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<SalesDetailsViewModel>(view.Model);
        Assert.Equal(SaleStage.Border, model.SaleStage);
        Assert.Equal(1, model.LedgerEntryId);
        Assert.Equal("INV-004", model.LedgerReference);
        Assert.Equal("SHIP-DETAIL-1", model.ShipmentCode);
        Assert.Equal(10, model.InventoryMovementId);
        Assert.Equal("SAL-001", model.ContractNumber);
        Assert.Equal("PUR-002", model.SourcePurchaseContractNumber);
        Assert.Equal("TK-01", model.SourceStorageTankCode);
        Assert.False(string.IsNullOrWhiteSpace(model.LedgerSideName));
    }

    [Fact]
    public async Task Details_Shows_InvoiceSale_Label_When_Sales_Contract_Is_Missing()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        SeedPurchaseContract(db, 2);
        db.SalesTransactions.Add(new SalesTransaction
        {
            Id = 1,
            CompanyId = 1,
            CustomerId = 1,
            ProductId = 1,
            SaleStage = SaleStage.TerminalStock,
            InvoiceNumber = "INV-SPOT-DETAIL",
            SaleDate = new DateTime(2026, 4, 23),
            QuantityMt = 10m,
            UnitPriceUsd = 400m,
            TotalUsd = 4000m
        });
        db.InventoryMovements.Add(new InventoryMovement
        {
            Id = 10,
            ProductId = 1,
            ContractId = 2,
            TerminalId = 1,
            StorageTankId = 1,
            SalesTransactionId = 1,
            Direction = MovementDirection.Out,
            MovementDate = new DateTime(2026, 4, 23),
            QuantityMt = 10m,
            ReferenceDocument = "INV-SPOT-DETAIL"
        });
        db.LedgerEntries.Add(new LedgerEntry
        {
            Id = 1,
            EntryDate = new DateTime(2026, 4, 23),
            Side = LedgerSide.Credit,
            AmountUsd = 4000m,
            Currency = "USD",
            Description = "Sale entry INV-SPOT-DETAIL",
            SourceType = "Sale",
            SourceId = 1,
            Reference = "INV-SPOT-DETAIL",
            ContractId = 2,
            CustomerId = 1
        });
        await db.SaveChangesAsync();

        var controller = BuildController(db);

        var result = await controller.Details(1);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<SalesDetailsViewModel>(view.Model);
        Assert.Equal(SalesContractText.WithoutSalesContract, model.ContractNumber);
        Assert.Equal("PUR-002", model.SourcePurchaseContractNumber);
    }

    [Fact]
    public async Task Details_Projects_Multiple_Linked_Receipts_Into_ReadOnly_Receivable_Summary()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        db.SalesTransactions.Add(new SalesTransaction
        {
            Id = 1,
            CompanyId = 1,
            CustomerId = 1,
            ProductId = 1,
            SaleStage = SaleStage.TerminalStock,
            InvoiceNumber = "INV-MULTI-PAY",
            SaleDate = new DateTime(2026, 7, 24),
            QuantityMt = 2m,
            Currency = "USD",
            UnitPriceInCurrency = 500m,
            UnitPriceUsd = 500m,
            TotalInCurrency = 1000m,
            TotalUsd = 1000m
        });
        db.PaymentTransactions.AddRange(
            new PaymentTransaction
            {
                Id = 10,
                PaymentDate = new DateTime(2026, 7, 24),
                Direction = PaymentDirection.In,
                PaymentKind = PaymentKind.CustomerReceipt,
                CompanyId = 1,
                CashAccountId = 1,
                CustomerId = 1,
                SalesTransactionId = 1,
                Amount = 300m,
                Currency = "USD",
                AmountUsd = 300m,
                Reference = "RCPT-10"
            },
            new PaymentTransaction
            {
                Id = 11,
                PaymentDate = new DateTime(2026, 7, 25),
                Direction = PaymentDirection.In,
                PaymentKind = PaymentKind.CustomerReceipt,
                CompanyId = 1,
                CashAccountId = 1,
                CustomerId = 1,
                SalesTransactionId = 1,
                Amount = 200m,
                Currency = "USD",
                AmountUsd = 200m,
                Reference = "RCPT-11"
            });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var result = await BuildController(db).Details(1);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<SalesDetailsViewModel>(view.Model);
        Assert.Equal(2, model.Payments.Count);
        Assert.Equal(500m, model.ReceivedUsd);
        Assert.Equal(500m, model.ReceivableBalanceUsd);
        Assert.Equal(0m, model.OverpaymentUsd);

        Assert.Equal(1, await db.SalesTransactions.AsNoTracking().CountAsync());
        Assert.Equal(2, await db.PaymentTransactions.AsNoTracking().CountAsync());
        Assert.DoesNotContain(db.ChangeTracker.Entries(), entry =>
            entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted);
    }

    [Fact]
    public async Task Details_Shows_LoadingReceiptAllocation_Source_For_DirectSale_Without_InventoryMovement()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        SeedPurchaseContract(db, 2);
        db.LoadingRegisters.Add(new LoadingRegister
        {
            Id = 1,
            ContractId = 2,
            ProductId = 1,
            LoadingDate = new DateTime(2026, 4, 21),
            LoadedQuantityMt = 10m
        });
        db.LoadingReceipts.Add(new LoadingReceipt
        {
            Id = 1,
            LoadingRegisterId = 1,
            TerminalId = 1,
            ReceiptDate = new DateTime(2026, 4, 22),
            ReceivedQuantityMt = 10m
        });
        db.SalesTransactions.Add(new SalesTransaction
        {
            Id = 1,
            CompanyId = 1,
            CustomerId = 1,
            ProductId = 1,
            SaleStage = SaleStage.InTransit,
            InvoiceNumber = "DS-DETAIL",
            SaleDate = new DateTime(2026, 4, 23),
            QuantityMt = 10m,
            UnitPriceUsd = 400m,
            TotalUsd = 4000m
        });
        db.LoadingReceiptAllocations.Add(new LoadingReceiptAllocation
        {
            Id = 1,
            LoadingReceiptId = 1,
            Destination = LoadingReceiptAllocationDestination.DirectSale,
            Status = LoadingReceiptAllocationStatus.Completed,
            QuantityMt = 10m,
            SourcePurchaseContractId = 2,
            TerminalId = 1,
            StorageTankId = 1,
            SalesTransactionId = 1
        });
        db.LedgerEntries.Add(new LedgerEntry
        {
            Id = 1,
            EntryDate = new DateTime(2026, 4, 23),
            Side = LedgerSide.Credit,
            AmountUsd = 4000m,
            Currency = "USD",
            Description = "Direct sale entry DS-DETAIL",
            SourceType = "Sale",
            SourceId = 1,
            Reference = "DS-DETAIL",
            ContractId = 2,
            CustomerId = 1
        });
        await db.SaveChangesAsync();

        var controller = BuildController(db);

        var result = await controller.Details(1);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<SalesDetailsViewModel>(view.Model);
        Assert.Equal(1, model.LoadingReceiptAllocationId);
        Assert.Equal(1, model.LoadingReceiptId);
        Assert.Equal("PUR-002", model.SourcePurchaseContractNumber);
        Assert.Equal("Terminal 1", model.SourceTerminalName);
        Assert.Equal("TK-01", model.SourceStorageTankCode);
        Assert.Null(model.InventoryMovementId);
    }

    [Fact]
    public async Task Create_Post_Rolls_Back_When_Stock_Recheck_Fails_Inside_Transaction()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        SeedSaleContract(db, 1);
        SeedPurchaseContract(db, 2);
        await db.SaveChangesAsync();

        var stock = new SequencedStockService(8m, 0m);
        var controller = BuildController(db, stock);

        var result = await controller.Create(new SalesCreateViewModel
        {
            SaleStage = SaleStage.TerminalStock,
            ContractId = 1,
            CompanyId = 1,
            CustomerId = 1,
            ProductId = 1,
            DestinationLocationId = 1,
            SourceTerminalId = 1,
            SourceStorageTankId = 1,
            SourcePurchaseContractId = 2,
            SaleDate = new DateTime(2026, 4, 23),
            QuantityMt = 5m,
            UnitPriceUsd = 450m,
            InvoiceNumber = "INV-RECHECK-001"
        });

        var view = Assert.IsType<ViewResult>(result);
        Assert.IsType<SalesCreateViewModel>(view.Model);
        Assert.False(controller.ModelState.IsValid);
        Assert.Equal(2, stock.GetFreeQuantityCallCount);
        Assert.Equal(new int?[] { 2, 2 }, stock.ContractIds);
        Assert.Empty(await db.SalesTransactions.ToListAsync());
        Assert.Empty(await db.InventoryMovements.Where(m => m.Direction == MovementDirection.Out).ToListAsync());
        Assert.Empty(await db.LedgerEntries.ToListAsync());
    }

    [Fact]
    public async Task Create_Post_Blocks_TerminalStock_When_SourcePurchaseContract_Is_Missing()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        SeedSaleContract(db, 1);
        await db.SaveChangesAsync();

        var controller = BuildController(db);

        var result = await controller.Create(new SalesCreateViewModel
        {
            SaleStage = SaleStage.TerminalStock,
            ContractId = 1,
            CompanyId = 1,
            CustomerId = 1,
            ProductId = 1,
            DestinationLocationId = 1,
            SourceTerminalId = 1,
            SourceStorageTankId = 1,
            SaleDate = new DateTime(2026, 4, 23),
            QuantityMt = 5m,
            UnitPriceUsd = 450m,
            InvoiceNumber = "INV-NO-SOURCE-CONTRACT"
        });

        var view = Assert.IsType<ViewResult>(result);
        Assert.IsType<SalesCreateViewModel>(view.Model);
        Assert.False(controller.ModelState.IsValid);
        Assert.Single(controller.ModelState[nameof(SalesCreateViewModel.SourcePurchaseContractId)]!.Errors);
    }

    [Fact]
    public async Task Create_Post_Blocks_TerminalStock_When_SourcePurchaseContract_Is_Not_Purchase()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        SeedSaleContract(db, 1);
        await db.SaveChangesAsync();

        var controller = BuildController(db);

        var result = await controller.Create(new SalesCreateViewModel
        {
            SaleStage = SaleStage.TerminalStock,
            ContractId = 1,
            CompanyId = 1,
            CustomerId = 1,
            ProductId = 1,
            DestinationLocationId = 1,
            SourceTerminalId = 1,
            SourceStorageTankId = 1,
            SourcePurchaseContractId = 1,
            SaleDate = new DateTime(2026, 4, 23),
            QuantityMt = 5m,
            UnitPriceUsd = 450m,
            InvoiceNumber = "INV-BAD-SOURCE-CONTRACT"
        });

        var view = Assert.IsType<ViewResult>(result);
        Assert.IsType<SalesCreateViewModel>(view.Model);
        Assert.False(controller.ModelState.IsValid);
        Assert.Single(controller.ModelState[nameof(SalesCreateViewModel.SourcePurchaseContractId)]!.Errors);
    }

    [Fact]
    public async Task Create_Post_Blocks_TerminalStock_When_SourcePurchaseContract_Product_Does_Not_Match()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        SeedSaleContract(db, 1);
        db.Products.Add(new Product { Id = 2, Code = "JET", Name = "Jet Fuel" });
        SeedPurchaseContract(db, 2, productId: 2);
        await db.SaveChangesAsync();

        var controller = BuildController(db);

        var result = await controller.Create(new SalesCreateViewModel
        {
            SaleStage = SaleStage.TerminalStock,
            ContractId = 1,
            CompanyId = 1,
            CustomerId = 1,
            ProductId = 1,
            DestinationLocationId = 1,
            SourceTerminalId = 1,
            SourceStorageTankId = 1,
            SourcePurchaseContractId = 2,
            SaleDate = new DateTime(2026, 4, 23),
            QuantityMt = 5m,
            UnitPriceUsd = 450m,
            InvoiceNumber = "INV-SOURCE-PRODUCT-MISMATCH"
        });

        var view = Assert.IsType<ViewResult>(result);
        Assert.IsType<SalesCreateViewModel>(view.Model);
        Assert.False(controller.ModelState.IsValid);
        Assert.Single(controller.ModelState[nameof(SalesCreateViewModel.SourcePurchaseContractId)]!.Errors);
    }

    [Fact]
    public async Task Invoice_Renders_For_Existing_SalesTransaction()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        SeedInvoiceSale(db);
        await db.SaveChangesAsync();

        var controller = BuildController(db);

        var result = await controller.Invoice(1, "faisal");

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<SalesInvoicePrintViewModel>(view.Model);
        Assert.Equal(InvoiceTemplateKey.Faisal, model.TemplateKey);
        Assert.Equal(1, model.SalesTransactionId);
        Assert.Equal("INV-PRINT-001", model.InvoiceNumber);
    }

    [Fact]
    public async Task Invoice_Rejects_Invalid_Template()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        var controller = BuildController(db);

        var result = await controller.Invoice(1, "unknown-template");

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Invoice_Maps_Print_ViewModel_Amounts_And_InvoiceNumber()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        SeedInvoiceSale(db);
        await db.SaveChangesAsync();

        var controller = BuildController(db);

        var result = await controller.Invoice(1, "fawad");

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<SalesInvoicePrintViewModel>(view.Model);
        Assert.Equal("INV-PRINT-001", model.InvoiceNumber);
        Assert.Equal(12.5m, model.QuantityMt);
        Assert.Equal(500m, model.UnitPriceUsd);
        Assert.Equal(6250m, model.TotalPriceUsd);
        Assert.Equal("Hairatan", model.BorderOrLocation);
        Assert.Equal("Diesel", model.ProductType);
        Assert.Equal("Buyer Company", model.BuyerCompanyName);
        Assert.Equal("Buyer Rep", model.BuyerRepresentativeName);
    }

    [Fact]
    public async Task Invoice_Does_Not_Create_Inventory_Ledger_Payment_Or_Call_StockService()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        SeedInvoiceSale(db);
        await db.SaveChangesAsync();

        var beforeMovements = await db.InventoryMovements.CountAsync();
        var beforeLedger = await db.LedgerEntries.CountAsync();
        var beforePayments = await db.PaymentTransactions.CountAsync();
        var stock = new SequencedStockService();
        var controller = BuildController(db, stock);

        var result = await controller.Invoice(1, "faisal");

        Assert.IsType<ViewResult>(result);
        Assert.Equal(beforeMovements, await db.InventoryMovements.CountAsync());
        Assert.Equal(beforeLedger, await db.LedgerEntries.CountAsync());
        Assert.Equal(beforePayments, await db.PaymentTransactions.CountAsync());
        Assert.Equal(0, stock.GetFreeQuantityCallCount);
    }

    [Fact]
    public async Task CreateFromShipment_Uses_Loaded_Minus_Shortage_And_Previous_Sales_Without_Tank_Movement()
    {
        var options = NewDbOptions();
        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        SeedPurchaseContract(db, 2);
        db.Shipments.Add(new Shipment
        {
            Id = 1,
            ShipmentCode = "SHIP-4144",
            QuantityMt = 4144m,
            DestinationLocationId = 1
        });
        db.ShipmentContracts.Add(new ShipmentContract { ShipmentId = 1, ContractId = 2, QuantityMt = 4144m });
        db.InventoryTransportLegs.AddRange(
            new InventoryTransportLeg
            {
                Id = 1,
                ShipmentId = 1,
                SourcePurchaseContractId = 2,
                ProductId = 1,
                SourceTerminalId = 1,
                TransportType = LoadingTransportType.Vessel,
                LoadedDate = new DateTime(2026, 6, 20),
                QuantityMt = 4144m,
                PurchaseUnitCostUsd = 80m,
                Status = InventoryTransportLegStatus.Loaded
            },
            new InventoryTransportLeg
            {
                Id = 2,
                ShipmentId = 1,
                SourcePurchaseContractId = 2,
                ProductId = 1,
                SourceTerminalId = 1,
                TransportType = LoadingTransportType.Truck,
                LoadedDate = new DateTime(2026, 6, 21),
                QuantityMt = 2_000m,
                PurchaseUnitCostUsd = 80m,
                Status = InventoryTransportLegStatus.Loaded
            });
        db.LossEvents.Add(new LossEvent
        {
            Id = 1,
            Stage = LossEventStage.TransitLoss,
            ProductId = 1,
            ShipmentId = 1,
            EventDate = new DateTime(2026, 6, 21),
            ExpectedQuantityMt = 4144m,
            ActualQuantityMt = 4110m,
            DifferenceQuantityMt = 34m,
            ChargeableLossMt = 34m,
            AffectsInventory = false,
            Reference = "SHIPMENT-SHORTAGE:1"
        });
        await db.SaveChangesAsync();
        var controller = BuildController(db);

        var getResult = await controller.CreateFromShipment(1);
        var getView = Assert.IsType<ViewResult>(getResult);
        var getModel = Assert.IsType<ShipmentFlowSaleCreateViewModel>(getView.Model);
        Assert.Equal(4144m, getModel.LoadedQuantityMt);
        Assert.Equal(34m, getModel.RegisteredShortageQuantityMt);
        Assert.Equal(4110m, getModel.AvailableQuantityMt);

        var movementCountBefore = await db.InventoryMovements.CountAsync();
        var result = await controller.CreateFromShipment(new ShipmentFlowSaleCreateViewModel
        {
            ShipmentId = 1,
            CustomerId = 1,
            QuantityMt = 4110m,
            UnitPriceInCurrency = 120m,
            Currency = "USD",
            SaleDate = new DateTime(2026, 6, 22),
            InvoiceNumber = "SHIP-SALE-4110",
            DestinationLocationId = 1
        });

        Assert.IsType<RedirectToActionResult>(result);
        var sale = await db.SalesTransactions.SingleAsync();
        Assert.Equal(SaleStage.InTransit, sale.SaleStage);
        Assert.Equal(1, sale.ShipmentId);
        Assert.Equal(4110m, sale.QuantityMt);
        Assert.Equal(movementCountBefore, await db.InventoryMovements.CountAsync());
        Assert.Empty(await db.InventoryTransportReceipts.ToListAsync());
        Assert.Empty(await db.PaymentTransactions.ToListAsync());

        var ledger = await db.LedgerEntries.SingleAsync();
        Assert.Equal("Sale", ledger.SourceType);
        Assert.Equal(1, ledger.CustomerId);
        Assert.Equal(1, ledger.ShipmentId);
        Assert.Equal(LedgerSide.Credit, ledger.Side);

        var afterResult = await controller.CreateFromShipment(1);
        var afterView = Assert.IsType<ViewResult>(afterResult);
        var afterModel = Assert.IsType<ShipmentFlowSaleCreateViewModel>(afterView.Model);
        Assert.Equal(0m, afterModel.AvailableQuantityMt);

        var overResult = await controller.CreateFromShipment(new ShipmentFlowSaleCreateViewModel
        {
            ShipmentId = 1,
            CustomerId = 1,
            QuantityMt = 1m,
            UnitPriceInCurrency = 120m,
            Currency = "USD",
            SaleDate = new DateTime(2026, 6, 22),
            InvoiceNumber = "SHIP-SALE-OVER"
        });
        Assert.IsType<ViewResult>(overResult);
        Assert.Single(await db.SalesTransactions.ToListAsync());
    }

    [Fact]
    public async Task CreateFromShipment_Allows_Contracts_With_Different_Companies_And_Keeps_Contract_Lineage()
    {
        var options = NewDbOptions();
        await using var db = new ApplicationDbContext(options);
        SeedMixedCompanyShipment(db);
        await db.SaveChangesAsync();
        var controller = BuildController(db);

        // فروش از قرارداد احمد (جواز ۲) — فروش تک‌قراردادی، ردیابی جواز از قرارداد خودش.
        var ahmadSale = await controller.CreateFromShipment(new ShipmentFlowSaleCreateViewModel
        {
            ShipmentId = 1,
            CustomerId = 1,
            SourcePurchaseContractId = 2,
            QuantityMt = 600m,
            UnitPriceInCurrency = 120m,
            Currency = "USD",
            SaleDate = new DateTime(2026, 6, 22),
            InvoiceNumber = "MIX-AHMAD-600"
        });
        Assert.IsType<RedirectToActionResult>(ahmadSale);

        // فروش از قرارداد محمود (جواز ۳) در همان محموله.
        var mahmoudSale = await controller.CreateFromShipment(new ShipmentFlowSaleCreateViewModel
        {
            ShipmentId = 1,
            CustomerId = 1,
            SourcePurchaseContractId = 3,
            QuantityMt = 400m,
            UnitPriceInCurrency = 130m,
            Currency = "USD",
            SaleDate = new DateTime(2026, 6, 23),
            InvoiceNumber = "MIX-MAHMOUD-400"
        });
        Assert.IsType<RedirectToActionResult>(mahmoudSale);

        var sales = await db.SalesTransactions.OrderBy(s => s.Id).ToListAsync();
        Assert.Equal(2, sales.Count);
        Assert.Equal(2, sales[0].SourcePurchaseContractId);
        Assert.Equal(2, sales[0].CompanyId);
        Assert.Equal(3, sales[1].SourcePurchaseContractId);
        Assert.Equal(3, sales[1].CompanyId);
    }

    [Fact]
    public async Task CreateFromShipment_Allows_Blanket_Sale_When_Licenses_Differ_And_Splits_By_Weight()
    {
        var options = NewDbOptions();
        await using var db = new ApplicationDbContext(options);
        SeedMixedCompanyShipment(db);
        await db.SaveChangesAsync();
        var controller = BuildController(db);

        // فروش کلی بدون قرارداد منبع: اختلاف جواز مانع نیست؛ مقدار با همان تقسیم وزنی سرشکن می‌شود.
        var blanketSale = await controller.CreateFromShipment(new ShipmentFlowSaleCreateViewModel
        {
            ShipmentId = 1,
            CustomerId = 1,
            QuantityMt = 100m,
            UnitPriceInCurrency = 120m,
            Currency = "USD",
            SaleDate = new DateTime(2026, 6, 22),
            InvoiceNumber = "MIX-BLANKET-100"
        });
        Assert.IsType<RedirectToActionResult>(blanketSale);

        // یک فروش، یک فاکتور، یک ردیف لجر؛ بدون جواز ساختگی و بدون قرارداد منبع.
        var sale = await db.SalesTransactions.SingleAsync();
        Assert.Null(sale.CompanyId);
        Assert.Null(sale.SourcePurchaseContractId);
        Assert.Equal(100m, sale.QuantityMt);
        Assert.Equal("MIX-BLANKET-100", sale.InvoiceNumber);
        var ledger = await db.LedgerEntries.Where(l => l.SourceType == "Sale").ToListAsync();
        Assert.Single(ledger);
        Assert.Equal(1, ledger[0].CustomerId);

        // سهم هر قرارداد از همان فرمول وزنی: ۶۰٪ قرارداد احمد و ۴۰٪ قرارداد محمود.
        var pnl = await new InventoryTransportPnlService(db).BuildForLegsAsync([1, 2]);
        Assert.Equal(60m, pnl[1].SoldQuantityMt);
        Assert.Equal(40m, pnl[2].SoldQuantityMt);

        // جواز هر سهم از قرارداد خودش خوانده می‌شود، نه از سرِ فروش.
        var licenseByContract = await db.Contracts
            .Where(c => c.Id == 2 || c.Id == 3)
            .ToDictionaryAsync(c => c.Id, c => c.CompanyId);
        Assert.Equal(2, licenseByContract[2]);
        Assert.Equal(3, licenseByContract[3]);

        // مقدار قابل فروش باقی‌مانده هر قرارداد بعد از فروش کلی.
        var after = Assert.IsType<ShipmentFlowSaleCreateViewModel>(
            Assert.IsType<ViewResult>(await controller.CreateFromShipment(1)).Model);
        Assert.Equal(540m, after.Contracts.Single(r => r.ContractId == 2).AvailableQuantityMt);
        Assert.Equal(360m, after.Contracts.Single(r => r.ContractId == 3).AvailableQuantityMt);
    }

    [Fact]
    public async Task CreateFromShipment_Cancelled_Blanket_Sale_Restores_Contract_Availability()
    {
        var options = NewDbOptions();
        await using var db = new ApplicationDbContext(options);
        SeedMixedCompanyShipment(db);
        await db.SaveChangesAsync();
        var controller = BuildController(db);

        await controller.CreateFromShipment(new ShipmentFlowSaleCreateViewModel
        {
            ShipmentId = 1,
            CustomerId = 1,
            QuantityMt = 100m,
            UnitPriceInCurrency = 120m,
            Currency = "USD",
            SaleDate = new DateTime(2026, 6, 22),
            InvoiceNumber = "MIX-CANCEL-100"
        });

        var sale = await db.SalesTransactions.SingleAsync();
        sale.IsCancelled = true;
        await db.SaveChangesAsync();

        var after = Assert.IsType<ShipmentFlowSaleCreateViewModel>(
            Assert.IsType<ViewResult>(await controller.CreateFromShipment(1)).Model);
        Assert.Equal(1000m, after.AvailableQuantityMt);
        Assert.Equal(600m, after.Contracts.Single(r => r.ContractId == 2).AvailableQuantityMt);
        Assert.Equal(400m, after.Contracts.Single(r => r.ContractId == 3).AvailableQuantityMt);
    }

    [Fact]
    public async Task CreateFromShipment_Rejects_Blanket_Sale_When_Products_Differ()
    {
        var options = NewDbOptions();
        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        db.Products.Add(new Product { Id = 2, Code = "MG", Name = "Mogas" });
        SeedPurchaseContract(db, 2, productId: 1);
        SeedPurchaseContract(db, 3, productId: 2);
        db.Shipments.Add(new Shipment { Id = 1, ShipmentCode = "SHIP-MULTIPRODUCT", QuantityMt = 1000m });
        db.ShipmentContracts.AddRange(
            new ShipmentContract { ShipmentId = 1, ContractId = 2, QuantityMt = 600m },
            new ShipmentContract { ShipmentId = 1, ContractId = 3, QuantityMt = 400m });
        db.InventoryTransportLegs.AddRange(
            new InventoryTransportLeg
            {
                Id = 1, ShipmentId = 1, SourcePurchaseContractId = 2, ProductId = 1,
                SourceTerminalId = 1, TransportType = LoadingTransportType.Vessel,
                LoadedDate = new DateTime(2026, 6, 20), QuantityMt = 600m,
                PurchaseUnitCostUsd = 80m, Status = InventoryTransportLegStatus.Loaded
            },
            new InventoryTransportLeg
            {
                Id = 2, ShipmentId = 1, SourcePurchaseContractId = 3, ProductId = 2,
                SourceTerminalId = 1, TransportType = LoadingTransportType.Vessel,
                LoadedDate = new DateTime(2026, 6, 20), QuantityMt = 400m,
                PurchaseUnitCostUsd = 90m, Status = InventoryTransportLegStatus.Loaded
            });
        await db.SaveChangesAsync();
        var controller = BuildController(db);

        var result = await controller.CreateFromShipment(new ShipmentFlowSaleCreateViewModel
        {
            ShipmentId = 1,
            CustomerId = 1,
            QuantityMt = 100m,
            UnitPriceInCurrency = 120m,
            Currency = "USD",
            SaleDate = new DateTime(2026, 6, 22),
            InvoiceNumber = "MULTIPRODUCT-BLANKET"
        });

        Assert.IsType<ViewResult>(result);
        Assert.True(controller.ModelState.ContainsKey(nameof(ShipmentFlowSaleCreateViewModel.SourcePurchaseContractId)));
        Assert.Empty(await db.SalesTransactions.ToListAsync());
    }

    [Fact]
    public async Task CreateFromShipment_Rejects_Quantity_Above_Selected_Contract_Remaining()
    {
        var options = NewDbOptions();
        await using var db = new ApplicationDbContext(options);
        SeedMixedCompanyShipment(db);
        await db.SaveChangesAsync();
        var controller = BuildController(db);

        // قرارداد ۳ فقط ۴۰۰ تن در این محموله دارد، هرچند کل محموله ۱۰۰۰ تن است.
        var result = await controller.CreateFromShipment(new ShipmentFlowSaleCreateViewModel
        {
            ShipmentId = 1,
            CustomerId = 1,
            SourcePurchaseContractId = 3,
            QuantityMt = 700m,
            UnitPriceInCurrency = 120m,
            Currency = "USD",
            SaleDate = new DateTime(2026, 6, 22),
            InvoiceNumber = "MIX-OVER"
        });

        Assert.IsType<ViewResult>(result);
        Assert.True(controller.ModelState.ContainsKey(nameof(ShipmentFlowSaleCreateViewModel.QuantityMt)));
        Assert.Empty(await db.SalesTransactions.ToListAsync());
    }

    [Fact]
    public async Task CreateFromShipment_Rejects_Source_Contract_Not_Loaded_On_Shipment()
    {
        var options = NewDbOptions();
        await using var db = new ApplicationDbContext(options);
        SeedMixedCompanyShipment(db);
        SeedPurchaseContract(db, 4, companyId: 1);
        await db.SaveChangesAsync();
        var controller = BuildController(db);

        var result = await controller.CreateFromShipment(new ShipmentFlowSaleCreateViewModel
        {
            ShipmentId = 1,
            CustomerId = 1,
            SourcePurchaseContractId = 4,
            QuantityMt = 10m,
            UnitPriceInCurrency = 120m,
            Currency = "USD",
            SaleDate = new DateTime(2026, 6, 22),
            InvoiceNumber = "MIX-FOREIGN-CONTRACT"
        });

        Assert.IsType<ViewResult>(result);
        Assert.True(controller.ModelState.ContainsKey(nameof(ShipmentFlowSaleCreateViewModel.SourcePurchaseContractId)));
        Assert.Empty(await db.SalesTransactions.ToListAsync());
    }

    [Fact]
    public async Task CreateFromShipment_Keeps_Proportional_Sale_When_Contracts_Share_Company_And_Product()
    {
        var options = NewDbOptions();
        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        SeedPurchaseContract(db, 2, companyId: 1);
        SeedPurchaseContract(db, 3, companyId: 1);
        SeedTwoContractShipment(db);
        await db.SaveChangesAsync();
        var controller = BuildController(db);

        var getModel = Assert.IsType<ShipmentFlowSaleCreateViewModel>(
            Assert.IsType<ViewResult>(await controller.CreateFromShipment(1)).Model);
        Assert.False(getModel.SourceContractRequired);

        var result = await controller.CreateFromShipment(new ShipmentFlowSaleCreateViewModel
        {
            ShipmentId = 1,
            CustomerId = 1,
            QuantityMt = 1000m,
            UnitPriceInCurrency = 120m,
            Currency = "USD",
            SaleDate = new DateTime(2026, 6, 22),
            InvoiceNumber = "UNIFORM-1000"
        });

        Assert.IsType<RedirectToActionResult>(result);
        var sale = await db.SalesTransactions.SingleAsync();
        Assert.Null(sale.SourcePurchaseContractId);
        Assert.Equal(1, sale.CompanyId);
    }

    // محمولهٔ دو-قراردادی با دو جواز متفاوت: قرارداد ۲ (جواز احمد) ۶۰۰ تن، قرارداد ۳ (جواز محمود) ۴۰۰ تن.
    private static void SeedMixedCompanyShipment(ApplicationDbContext db)
    {
        SeedReferenceData(db);
        db.Companies.AddRange(
            new Company { Id = 2, Code = "AHMAD", Name = "جواز احمد" },
            new Company { Id = 3, Code = "MAHMOUD", Name = "جواز محمود" });
        SeedPurchaseContract(db, 2, companyId: 2);
        SeedPurchaseContract(db, 3, companyId: 3);
        SeedTwoContractShipment(db);
    }

    private static void SeedTwoContractShipment(ApplicationDbContext db)
    {
        db.Shipments.Add(new Shipment
        {
            Id = 1,
            ShipmentCode = "SHIP-MIX",
            QuantityMt = 1000m,
            DestinationLocationId = 1
        });
        db.ShipmentContracts.AddRange(
            new ShipmentContract { ShipmentId = 1, ContractId = 2, QuantityMt = 600m },
            new ShipmentContract { ShipmentId = 1, ContractId = 3, QuantityMt = 400m });
        db.InventoryTransportLegs.AddRange(
            new InventoryTransportLeg
            {
                Id = 1,
                ShipmentId = 1,
                SourcePurchaseContractId = 2,
                ProductId = 1,
                SourceTerminalId = 1,
                TransportType = LoadingTransportType.Vessel,
                LoadedDate = new DateTime(2026, 6, 20),
                QuantityMt = 600m,
                PurchaseUnitCostUsd = 80m,
                Status = InventoryTransportLegStatus.Loaded
            },
            new InventoryTransportLeg
            {
                Id = 2,
                ShipmentId = 1,
                SourcePurchaseContractId = 3,
                ProductId = 1,
                SourceTerminalId = 1,
                TransportType = LoadingTransportType.Vessel,
                LoadedDate = new DateTime(2026, 6, 20),
                QuantityMt = 400m,
                PurchaseUnitCostUsd = 90m,
                Status = InventoryTransportLegStatus.Loaded
            });
    }

    [Fact]
    public async Task CreateFromShipment_Uses_StockBacked_Unspecified_Root_Leg_Without_Counting_Later_Transport()
    {
        var options = NewDbOptions();
        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        SeedPurchaseContract(db, 2);
        db.Shipments.Add(new Shipment
        {
            Id = 1,
            ShipmentCode = "SHIP-TANK-ROOT",
            ContractId = 2,
            QuantityMt = 1_000m,
            DestinationLocationId = 1
        });
        db.ShipmentContracts.Add(new ShipmentContract
        {
            ShipmentId = 1,
            ContractId = 2,
            QuantityMt = 1_000m
        });
        db.InventoryMovements.AddRange(
            new InventoryMovement
            {
                Id = 10,
                ContractId = 2,
                ProductId = 1,
                TerminalId = 1,
                StorageTankId = 1,
                Direction = MovementDirection.Out,
                MovementDate = new DateTime(2026, 6, 20),
                QuantityMt = 1_000m,
                ReferenceDocument = "TRANSPORT-LEG:1"
            },
            new InventoryMovement
            {
                Id = 11,
                ContractId = 2,
                ProductId = 1,
                TerminalId = 1,
                StorageTankId = 1,
                Direction = MovementDirection.Out,
                MovementDate = new DateTime(2026, 6, 21),
                QuantityMt = 500m,
                ReferenceDocument = "TRANSPORT-LEG:4"
            });
        db.InventoryTransportLegs.AddRange(
            new InventoryTransportLeg
            {
                Id = 1,
                ShipmentId = 1,
                SourcePurchaseContractId = 2,
                ProductId = 1,
                SourceTerminalId = 1,
                SourceStorageTankId = 1,
                TransportType = LoadingTransportType.Unspecified,
                LoadedDate = new DateTime(2026, 6, 20),
                QuantityMt = 1_000m,
                PurchaseUnitCostUsd = 80m,
                Status = InventoryTransportLegStatus.Loaded,
                OutboundInventoryMovementId = 10
            },
            new InventoryTransportLeg
            {
                Id = 2,
                ShipmentId = 1,
                SourcePurchaseContractId = 2,
                ProductId = 1,
                SourceTerminalId = 1,
                SourceStorageTankId = 1,
                TransportType = LoadingTransportType.Truck,
                LoadedDate = new DateTime(2026, 6, 21),
                QuantityMt = 400m,
                Status = InventoryTransportLegStatus.Loaded
            },
            new InventoryTransportLeg
            {
                Id = 3,
                ShipmentId = 1,
                SourcePurchaseContractId = 2,
                ProductId = 1,
                SourceTerminalId = 1,
                SourceStorageTankId = 1,
                TransportType = LoadingTransportType.Unspecified,
                LoadedDate = new DateTime(2026, 6, 21),
                QuantityMt = 250m,
                Status = InventoryTransportLegStatus.Loaded
            },
            new InventoryTransportLeg
            {
                Id = 4,
                ShipmentId = 1,
                SourcePurchaseContractId = 2,
                ProductId = 1,
                SourceTerminalId = 1,
                SourceStorageTankId = 1,
                TransportType = LoadingTransportType.Unspecified,
                LoadedDate = new DateTime(2026, 6, 21),
                QuantityMt = 500m,
                Status = InventoryTransportLegStatus.Loaded,
                OutboundInventoryMovementId = 11
            });
        db.InventoryTransportReceipts.Add(new InventoryTransportReceipt
        {
            Id = 20,
            InventoryTransportLegId = 1,
            ReceiptDate = new DateTime(2026, 6, 21),
            ReceivedQuantityMt = 980m,
            ShortageQuantityMt = 20m
        });
        await db.SaveChangesAsync();
        var controller = BuildController(db);

        var getResult = await controller.CreateFromShipment(1);
        var getModel = Assert.IsType<ShipmentFlowSaleCreateViewModel>(Assert.IsType<ViewResult>(getResult).Model);
        Assert.Equal(1_000m, getModel.LoadedQuantityMt);
        Assert.Equal(20m, getModel.RegisteredShortageQuantityMt);
        Assert.Equal(980m, getModel.AvailableQuantityMt);
        Assert.Equal(980m, Assert.Single(getModel.Contracts).AvailableQuantityMt);

        var movementCountBefore = await db.InventoryMovements.CountAsync();
        var postResult = await controller.CreateFromShipment(new ShipmentFlowSaleCreateViewModel
        {
            ShipmentId = 1,
            CustomerId = 1,
            QuantityMt = 980m,
            UnitPriceInCurrency = 120m,
            Currency = "USD",
            SaleDate = new DateTime(2026, 6, 22),
            InvoiceNumber = "SHIP-TANK-SALE"
        });

        Assert.IsType<RedirectToActionResult>(postResult);
        var sale = await db.SalesTransactions.SingleAsync();
        Assert.Equal(SaleStage.InTransit, sale.SaleStage);
        Assert.Equal(980m, sale.QuantityMt);
        Assert.Equal(movementCountBefore, await db.InventoryMovements.CountAsync());

        var afterResult = await controller.CreateFromShipment(1);
        var afterModel = Assert.IsType<ShipmentFlowSaleCreateViewModel>(Assert.IsType<ViewResult>(afterResult).Model);
        Assert.Equal(0m, afterModel.AvailableQuantityMt);
    }

    [Fact]
    public async Task CreateFromShipment_Splits_Shortage_Sale_And_Estimated_Cost_Proportionally()
    {
        var options = NewDbOptions();
        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        SeedPurchaseContract(db, 2);
        SeedPurchaseContract(db, 3);
        db.Shipments.Add(new Shipment { Id = 1, ShipmentCode = "SHIP-SPLIT", QuantityMt = 100m });
        db.ShipmentContracts.AddRange(
            new ShipmentContract { ShipmentId = 1, ContractId = 2, QuantityMt = 60m },
            new ShipmentContract { ShipmentId = 1, ContractId = 3, QuantityMt = 40m });
        db.InventoryTransportLegs.AddRange(
            new InventoryTransportLeg
            {
                Id = 1, ShipmentId = 1, SourcePurchaseContractId = 2, ProductId = 1,
                SourceTerminalId = 1, TransportType = LoadingTransportType.Vessel,
                LoadedDate = new DateTime(2026, 6, 20), QuantityMt = 60m,
                PurchaseUnitCostUsd = 100m, Status = InventoryTransportLegStatus.Loaded
            },
            new InventoryTransportLeg
            {
                Id = 2, ShipmentId = 1, SourcePurchaseContractId = 3, ProductId = 1,
                SourceTerminalId = 1, TransportType = LoadingTransportType.Vessel,
                LoadedDate = new DateTime(2026, 6, 20), QuantityMt = 40m,
                PurchaseUnitCostUsd = 200m, Status = InventoryTransportLegStatus.Loaded
            });
        db.LossEvents.Add(new LossEvent
        {
            Stage = LossEventStage.TransitLoss,
            ProductId = 1,
            ShipmentId = 1,
            EventDate = new DateTime(2026, 6, 21),
            ExpectedQuantityMt = 100m,
            ActualQuantityMt = 90m,
            DifferenceQuantityMt = 10m,
            ChargeableLossMt = 10m,
            AffectsInventory = false,
            Reference = "SHIPMENT-SHORTAGE:SPLIT"
        });
        await db.SaveChangesAsync();
        var controller = BuildController(db);

        var getResult = await controller.CreateFromShipment(1);
        var getModel = Assert.IsType<ShipmentFlowSaleCreateViewModel>(Assert.IsType<ViewResult>(getResult).Model);
        Assert.Equal(90m, getModel.AvailableQuantityMt);
        var first = getModel.Contracts.Single(row => row.ContractId == 2);
        var second = getModel.Contracts.Single(row => row.ContractId == 3);
        Assert.Equal(6m, first.ShortageQuantityMt);
        Assert.Equal(4m, second.ShortageQuantityMt);
        Assert.Equal(54m, first.AvailableQuantityMt);
        Assert.Equal(36m, second.AvailableQuantityMt);
        Assert.Equal(6000m, first.TotalCostUsd);
        Assert.Equal(8000m, second.TotalCostUsd);
        Assert.Equal(14000m, first.TotalCostUsd + second.TotalCostUsd);

        var postResult = await controller.CreateFromShipment(new ShipmentFlowSaleCreateViewModel
        {
            ShipmentId = 1,
            CustomerId = 1,
            QuantityMt = 90m,
            UnitPriceInCurrency = 250m,
            Currency = "USD",
            SaleDate = new DateTime(2026, 6, 22),
            InvoiceNumber = "SHIP-SALE-SPLIT"
        });
        Assert.IsType<RedirectToActionResult>(postResult);

        var snapshots = await new InventoryTransportPnlService(db).BuildForLegsAsync([1, 2]);
        Assert.Equal(54m, snapshots[1].SoldQuantityMt);
        Assert.Equal(36m, snapshots[2].SoldQuantityMt);
        Assert.Equal(22500m, snapshots.Values.Sum(row => row.SalesUsd));
    }

    [Fact]
    public async Task CreateGroup_Sources_Show_DischargedWeight_For_FreightSettled_Dispatch()
    {
        await using var db = new ApplicationDbContext(NewDbOptions());
        SeedReferenceData(db);
        SeedPurchaseContract(db, 1);
        db.Trucks.Add(new Truck { Id = 1, PlateNumber = "TRK-1" });
        db.TruckDispatches.Add(new TruckDispatch
        {
            Id = 1,
            ContractId = 1,
            ProductId = 1,
            TruckId = 1,
            DispatchDate = new DateTime(2026, 5, 2),
            Status = DispatchStatus.Loaded,
            LoadedQuantityMt = 30m,
            DischargedQuantityMt = 28m,   // وزن تخلیه‌شده هنگام تسویهٔ کرایه
            ShortageMt = 2m,
            IsFreightSettled = true
        });
        await db.SaveChangesAsync();

        var view = Assert.IsType<ViewResult>(await BuildController(db).CreateGroup((string?)null));
        var sources = Assert.IsAssignableFrom<List<GroupSaleSourceItem>>(view.ViewData["Sources"]!);
        var item = sources.Single(s => s.Kind == GroupSaleSourceKind.TruckDispatch && s.Id == 1);
        Assert.Equal(28m, item.AvailableMt);          // وزن تخلیه، نه ۳۰ بارگیری
        Assert.Equal("کرایه تسویه‌شده", item.StatusLabel);
    }

    [Fact]
    public async Task CreateGroup_Sources_Include_SettlementOnly_Leg_With_DischargedWeight()
    {
        await using var db = new ApplicationDbContext(NewDbOptions());
        SeedReferenceData(db);
        SeedPurchaseContract(db, 1);
        db.InventoryTransportLegs.Add(new InventoryTransportLeg
        {
            Id = 1,
            SourcePurchaseContractId = 1,
            ProductId = 1,
            SourceTerminalId = 1,
            TransportType = LoadingTransportType.Wagon,
            WagonNumber = "WGN-1",
            LoadedDate = new DateTime(2026, 5, 2),
            QuantityMt = 30m,
            Status = InventoryTransportLegStatus.Loaded,
            IsFreightSettled = true
        });
        // رسیدِ «فقط تسویهٔ کرایه»: دریافت صفر، فقط کسری ۲ تن ⇒ باقیمانده (وزن تخلیه) = ۲۸.
        db.InventoryTransportReceipts.Add(new InventoryTransportReceipt
        {
            Id = 1,
            InventoryTransportLegId = 1,
            ReceiptDate = new DateTime(2026, 5, 5),
            ReceivedQuantityMt = 0m,
            ShortageQuantityMt = 2m,
            ReceiptDestination = InventoryTransportReceiptDestination.ToInventory
        });
        await db.SaveChangesAsync();

        var view = Assert.IsType<ViewResult>(await BuildController(db).CreateGroup((string?)null));
        var sources = Assert.IsAssignableFrom<List<GroupSaleSourceItem>>(view.ViewData["Sources"]!);
        var item = sources.Single(s => s.Kind == GroupSaleSourceKind.WagonLeg && s.Id == 1);
        Assert.Equal(28m, item.AvailableMt);          // وزن تخلیه، نه ۳۰ بارگیری
        Assert.Equal("کرایه تسویه‌شده", item.StatusLabel);
    }

    private static DbContextOptions<ApplicationDbContext> NewDbOptions()
        => new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

    private static SalesController BuildController(ApplicationDbContext db, IStockService? stockService = null)
        => new(
            db,
            stockService ?? new StockService(db),
            new AuditService(db),
            NullLogger<SalesController>.Instance)
        {
            TempData = BuildTempData()
        };

    private static void SeedReferenceData(ApplicationDbContext db)
    {
        db.Currencies.Add(new Currency { Id = 1, Code = "USD", Name = "US Dollar", Symbol = "$", IsActive = true });
        db.Companies.Add(new Company { Id = 1, Code = "PTG", Name = "PTG" });
        db.Customers.Add(new Customer { Id = 1, Name = "Herat Market" });
        db.Products.Add(new Product { Id = 1, Code = "GO", Name = "Gas Oil" });
        db.Locations.Add(new Location { Id = 1, Name = "Herat" });
        db.Terminals.Add(new Terminal { Id = 1, Code = "T1", Name = "Terminal 1" });
        db.StorageTanks.Add(new StorageTank { Id = 1, TerminalId = 1, TankCode = "TK-01", ProductId = 1, CapacityMt = 1000m });
    }

    private static void SeedInvoiceSale(ApplicationDbContext db)
    {
        db.Customers.Local.Single(c => c.Id == 1).Name = "Buyer Company";
        db.Customers.Local.Single(c => c.Id == 1).ContactPerson = "Buyer Rep";
        db.Customers.Local.Single(c => c.Id == 1).Phone = "+93 700 000 000";
        db.Customers.Local.Single(c => c.Id == 1).Address = "Herat";
        db.Products.Local.Single(p => p.Id == 1).Name = "Diesel";
        db.Locations.Local.Single(l => l.Id == 1).Name = "Hairatan";
        db.SalesTransactions.Add(new SalesTransaction
        {
            Id = 1,
            CompanyId = 1,
            CustomerId = 1,
            ProductId = 1,
            DestinationLocationId = 1,
            SaleStage = SaleStage.PreSale,
            InvoiceNumber = "INV-PRINT-001",
            SaleDate = new DateTime(2026, 5, 13),
            QuantityMt = 12.5m,
            Currency = "USD",
            UnitPriceInCurrency = 500m,
            UnitPriceUsd = 500m,
            TotalInCurrency = 6250m,
            TotalUsd = 6250m
        });
    }

    private static void SeedSaleContract(ApplicationDbContext db, int contractId)
    {
        db.Contracts.Add(new Contract
        {
            Id = contractId,
            ContractNumber = "SAL-001",
            ContractType = ContractType.Sale,
            CompanyId = 1,
            CustomerId = 1,
            ProductId = 1,
            DestinationLocationId = 1,
            ContractDate = new DateTime(2026, 4, 23),
            QuantityMt = 500m,
            PricingMethod = PricingMethod.Fixed,
            UnitPriceUsd = 450m
        });
    }

    private static void SeedPurchaseContract(ApplicationDbContext db, int contractId, int productId = 1, int companyId = 1)
    {
        db.Suppliers.Add(new Supplier { Id = contractId, Name = $"Supplier {contractId}" });
        db.Contracts.Add(new Contract
        {
            Id = contractId,
            ContractNumber = $"PUR-{contractId:000}",
            ContractType = ContractType.Purchase,
            CompanyId = companyId,
            SupplierId = contractId,
            ProductId = productId,
            ContractDate = new DateTime(2026, 4, 20),
            QuantityMt = 1000m,
            PricingMethod = PricingMethod.Fixed,
            UnitPriceUsd = 430m
        });
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

    private sealed class SequencedStockService : IStockService
    {
        private readonly Queue<decimal> _freeQuantities;

        public SequencedStockService(params decimal[] freeQuantities)
            => _freeQuantities = new Queue<decimal>(freeQuantities);

        public int GetFreeQuantityCallCount { get; private set; }
        public List<int?> ContractIds { get; } = [];

        public Task<decimal> GetFreeQuantityMtAsync(
            int productId,
            int? terminalId = null,
            int? contractId = null,
            int? inventoryBatchId = null,
            int? storageTankId = null,
            DateTime? asOfUtc = null,
            CancellationToken ct = default)
        {
            GetFreeQuantityCallCount++;
            ContractIds.Add(contractId);
            return Task.FromResult(_freeQuantities.Count == 0 ? 0m : _freeQuantities.Dequeue());
        }

        public Task<decimal> GetTotalFreeQuantityMtAsync(
            int? terminalId = null,
            DateTime? asOfUtc = null,
            CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<TankStockItem>> GetTankAvailabilityAsync(
            int productId,
            int contractId,
            DateTime? asOfUtc = null,
            CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<TankStockItem>>([]);

        public Task<IReadOnlyList<StockSummaryItem>> GetStockSummaryAsync(
            int? productId = null,
            int? contractId = null,
            int? terminalId = null,
            DateTime? asOfUtc = null,
            CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<StockCardItem>> GetStockCardAsync(
            int? productId = null,
            int? contractId = null,
            int? terminalId = null,
            int? storageTankId = null,
            DateTime? fromUtc = null,
            DateTime? toUtc = null,
            CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task AcquireStockMutationLockAsync(
            InventoryMovement movement,
            CancellationToken ct = default)
            => Task.CompletedTask;

        public Task EnsureSufficientStockForMovementAsync(
            InventoryMovement movement,
            CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task EnsureMovementDoesNotCauseFutureNegativeStockAsync(
            InventoryMovement movement,
            CancellationToken ct = default)
            => Task.CompletedTask;

        public Task EnsureSufficientStockForSaleAsync(
            SalesTransaction sale,
            CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task EnsureSufficientStockForSaleAsync(
            SalesTransaction sale,
            int? sourcePurchaseContractId,
            CancellationToken ct = default)
            => throw new NotSupportedException();
    }
}
