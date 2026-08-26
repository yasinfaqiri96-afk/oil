using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Controllers;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.Reports;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Audit;
using PTGOilSystem.Web.Services.Reporting;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// انتساب «فروش → قرارداد خرید» فقط از <see cref="SalesTransactionSourceAllocation"/>
/// خوانده می‌شود. یک فروش چند-قراردادی نباید کل <c>TotalUsd</c> خود را به هر قرارداد
/// بدهد و جمع سهم‌ها باید دقیقاً برابر همان <c>TotalUsd</c> بماند.
/// </summary>
public class SaleContractAttributionTests
{
    private const decimal SaleTotalUsd = 29_994.80m;
    private const decimal ShareContract6Usd = 16_596.25m;
    private const decimal ShareContract5Usd = 13_398.55m;

    [Fact]
    public async Task Single_Contract_Sale_Keeps_Full_Revenue_On_Its_Purchase_Contract()
    {
        await using var db = NewDb();
        SeedPurchaseContracts(db);
        db.SalesTransactions.Add(Sale(108, quantityMt: 32.08m, totalUsd: SaleTotalUsd));
        db.InventoryMovements.Add(StockOut(1, contractId: 5, saleId: 108, quantityMt: 32.08m));
        db.SalesTransactionSourceAllocations.Add(Allocation(108, contractId: 5, 32.08m, SaleTotalUsd));
        await db.SaveChangesAsync();

        var rows = await PurchaseRowsAsync(db);

        Assert.Equal(SaleTotalUsd, Row(rows, 5).TotalRevenueUsd);
        Assert.Equal(0m, Row(rows, 6).TotalRevenueUsd);
    }

    [Fact]
    public async Task Multi_Contract_Sale_Splits_Revenue_By_Allocation_And_Never_Doubles_It()
    {
        await using var db = NewDb();
        SeedPurchaseContracts(db);
        db.SalesTransactions.Add(Sale(108, quantityMt: 32.08m, totalUsd: SaleTotalUsd));
        // Stock-out از هر دو قرارداد — همان چیزی که پیش‌تر باعث می‌شد کل مبلغ دو بار شمرده شود.
        db.InventoryMovements.AddRange(
            StockOut(1, contractId: 6, saleId: 108, quantityMt: 17.75m),
            StockOut(2, contractId: 5, saleId: 108, quantityMt: 14.33m));
        db.SalesTransactionSourceAllocations.AddRange(
            Allocation(108, contractId: 6, 17.75m, ShareContract6Usd),
            Allocation(108, contractId: 5, 14.33m, ShareContract5Usd));
        await db.SaveChangesAsync();

        var rows = await PurchaseRowsAsync(db);

        Assert.Equal(ShareContract6Usd, Row(rows, 6).TotalRevenueUsd);
        Assert.Equal(ShareContract5Usd, Row(rows, 5).TotalRevenueUsd);
        Assert.Equal(17.75m, Row(rows, 6).TotalSoldMt);
        Assert.Equal(14.33m, Row(rows, 5).TotalSoldMt);

        // Invariant: جمع سهم قراردادها دقیقاً برابر مبلغ فروش است — نه بیشتر، نه کمتر.
        Assert.Equal(SaleTotalUsd, rows.Sum(r => r.TotalRevenueUsd));
        Assert.All(rows, r => Assert.NotEqual(SaleTotalUsd, r.TotalRevenueUsd));
    }

    [Fact]
    public async Task Sale_Without_Proven_Allocation_Keeps_Legacy_Single_Contract_Behaviour()
    {
        await using var db = NewDb();
        SeedPurchaseContracts(db);
        db.SalesTransactions.Add(Sale(200, quantityMt: 10m, totalUsd: 5_000m));
        db.InventoryMovements.Add(StockOut(1, contractId: 5, saleId: 200, quantityMt: 10m));
        await db.SaveChangesAsync();

        var map = await new SaleContractAttributionReader(db).LoadForSalesAsync([200]);
        Assert.False(map.HasProvenAllocation(200));
        Assert.Null(map.ShareFor(200, 5));

        var rows = await PurchaseRowsAsync(db);
        Assert.Equal(5_000m, Row(rows, 5).TotalRevenueUsd);
        Assert.Equal(0m, Row(rows, 6).TotalRevenueUsd);
    }

    [Fact]
    public async Task Company_Revenue_Is_Unchanged_By_Contract_Level_Attribution()
    {
        await using var db = NewDb();
        SeedPurchaseContracts(db);
        db.SalesTransactions.Add(Sale(108, quantityMt: 32.08m, totalUsd: SaleTotalUsd));
        db.InventoryMovements.AddRange(
            StockOut(1, contractId: 6, saleId: 108, quantityMt: 17.75m),
            StockOut(2, contractId: 5, saleId: 108, quantityMt: 14.33m));
        db.SalesTransactionSourceAllocations.AddRange(
            Allocation(108, contractId: 6, 17.75m, ShareContract6Usd),
            Allocation(108, contractId: 5, 14.33m, ShareContract5Usd));
        await db.SaveChangesAsync();

        var company = await new ProfitAndLossService(db).BuildCompanyAsync(new ManagementReportFilterViewModel());
        var rows = await PurchaseRowsAsync(db);

        Assert.Equal(SaleTotalUsd, company.Sales.RevenueUsd);
        Assert.Equal(company.Sales.RevenueUsd, rows.Sum(r => r.TotalRevenueUsd));
    }

    [Fact]
    public async Task Contract_Account_Statement_Shows_Only_This_Contracts_Allocated_Share()
    {
        await using var db = NewDb();
        SeedPurchaseContracts(db);
        db.SalesTransactions.Add(Sale(108, quantityMt: 32.08m, totalUsd: SaleTotalUsd));
        // فروش چند-قراردادی: AUD-06 عمداً ContractId دفترکل را خالی می‌گذارد.
        db.LedgerEntries.Add(SaleLedger(108, contractId: null, SaleTotalUsd));
        db.SalesTransactionSourceAllocations.AddRange(
            Allocation(108, contractId: 6, 17.75m, ShareContract6Usd),
            Allocation(108, contractId: 5, 14.33m, ShareContract5Usd));
        await db.SaveChangesAsync();

        var contract6 = await ContractStatementAsync(db, 6);
        var contract5 = await ContractStatementAsync(db, 5);

        var row6 = Assert.Single(contract6.Rows.Where(r => r.SourceType == "Sale"));
        var row5 = Assert.Single(contract5.Rows.Where(r => r.SourceType == "Sale"));
        Assert.Equal(ShareContract6Usd, (row6.ReceiptUsd ?? 0m) + (row6.OutflowUsd ?? 0m));
        Assert.Equal(ShareContract5Usd, (row5.ReceiptUsd ?? 0m) + (row5.OutflowUsd ?? 0m));

        // همان قاعدهٔ ContractPnl: مجموع سهم دو صورت‌حساب برابر مبلغ فروش است.
        Assert.Equal(
            SaleTotalUsd,
            (row6.ReceiptUsd ?? 0m) + (row6.OutflowUsd ?? 0m) + (row5.ReceiptUsd ?? 0m) + (row5.OutflowUsd ?? 0m));
    }

    [Fact]
    public async Task Contract_Account_Statement_Does_Not_Duplicate_A_Sale_Already_Posted_On_That_Contract()
    {
        await using var db = NewDb();
        SeedPurchaseContracts(db);
        db.SalesTransactions.Add(Sale(109, quantityMt: 10m, totalUsd: 5_000m));
        // فروش تک‌قراردادی: دفترکل خودش روی قرارداد ۵ نشسته است.
        db.LedgerEntries.Add(SaleLedger(109, contractId: 5, 5_000m));
        db.SalesTransactionSourceAllocations.Add(Allocation(109, contractId: 5, 10m, 5_000m));
        await db.SaveChangesAsync();

        var contract5 = await ContractStatementAsync(db, 5);

        var saleRows = contract5.Rows.Where(r => r.SourceType == "Sale").ToList();
        Assert.Single(saleRows);
        Assert.Equal(5_000m, (saleRows[0].ReceiptUsd ?? 0m) + (saleRows[0].OutflowUsd ?? 0m));
    }

    [Fact]
    public async Task Reader_Reports_A_Zero_Share_For_A_Contract_Proven_To_Have_None()
    {
        await using var db = NewDb();
        SeedPurchaseContracts(db);
        db.SalesTransactions.Add(Sale(108, quantityMt: 32.08m, totalUsd: SaleTotalUsd));
        db.SalesTransactionSourceAllocations.Add(Allocation(108, contractId: 6, 32.08m, SaleTotalUsd));
        await db.SaveChangesAsync();

        var map = await new SaleContractAttributionReader(db).LoadForPurchaseContractAsync(6);

        Assert.True(map.HasProvenAllocation(108));
        Assert.Equal((32.08m, SaleTotalUsd), map.ShareFor(108, 6));
        // اثبات‌شده که قرارداد ۵ سهمی ندارد — این با «اثبات‌نشده» (null) یکی نیست.
        Assert.Equal((0m, 0m), map.ShareFor(108, 5));
    }

    // ---- helpers ----

    private static ApplicationDbContext NewDb()
        => new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static async Task<IReadOnlyList<ContractPnlRowViewModel>> PurchaseRowsAsync(ApplicationDbContext db)
    {
        var view = Assert.IsType<ViewResult>(
            await new ReportsController(db).ContractPnl(new ManagementReportFilterViewModel()));
        return Assert.IsType<ContractPnlReportViewModel>(view.Model).PurchaseRows;
    }

    private static ContractPnlRowViewModel Row(IReadOnlyList<ContractPnlRowViewModel> rows, int contractId)
        => rows.Single(r => r.ContractId == contractId);

    private static async Task<PTGOilSystem.Web.Models.AccountStatements.ContractAccountStatementViewModel>
        ContractStatementAsync(ApplicationDbContext db, int contractId)
    {
        var controller = new AccountStatementsController(db, new PricingService(db), new AuditService(db));
        var view = Assert.IsType<ViewResult>(await controller.Contract(contractId));
        return Assert.IsType<PTGOilSystem.Web.Models.AccountStatements.ContractAccountStatementViewModel>(view.Model);
    }

    private static void SeedPurchaseContracts(ApplicationDbContext db)
    {
        db.Companies.Add(new Company { Id = 1, Code = "PTG", Name = "PTG" });
        db.Products.Add(new Product { Id = 1, Code = "GAS", Name = "Gasoline" });
        db.Suppliers.Add(new Supplier { Id = 1, Name = "Supplier A" });
        db.Customers.Add(new Customer { Id = 1, Name = "Customer A" });
        db.Terminals.Add(new Terminal { Id = 1, Code = "ILK", Name = "Ilinka" });
        db.StorageTanks.Add(new StorageTank { Id = 1, TerminalId = 1, TankCode = "TANK-A" });
        db.Contracts.AddRange(
            PurchaseContract(5, "P-005"),
            PurchaseContract(6, "P-006"));
    }

    private static Contract PurchaseContract(int id, string number)
        => new()
        {
            Id = id,
            ContractNumber = number,
            ContractType = ContractType.Purchase,
            CompanyId = 1,
            ProductId = 1,
            SupplierId = 1,
            ContractDate = new DateTime(2026, 4, 1),
            QuantityMt = 100m,
            PricingMethod = PricingMethod.Fixed,
            UnitPriceUsd = 500m
        };

    private static SalesTransaction Sale(int id, decimal quantityMt, decimal totalUsd)
        => new()
        {
            Id = id,
            CompanyId = 1,
            CustomerId = 1,
            ProductId = 1,
            InvoiceNumber = $"INV-{id}",
            SaleDate = new DateTime(2026, 4, 22),
            QuantityMt = quantityMt,
            UnitPriceUsd = quantityMt > 0m ? totalUsd / quantityMt : 0m,
            TotalUsd = totalUsd,
            TotalInCurrency = totalUsd,
            Currency = "USD"
        };

    private static InventoryMovement StockOut(int id, int contractId, int saleId, decimal quantityMt)
        => new()
        {
            Id = id,
            ProductId = 1,
            ContractId = contractId,
            TerminalId = 1,
            StorageTankId = 1,
            SalesTransactionId = saleId,
            Direction = MovementDirection.Out,
            MovementDate = new DateTime(2026, 4, 22),
            QuantityMt = quantityMt
        };

    private static SalesTransactionSourceAllocation Allocation(
        int saleId,
        int contractId,
        decimal quantityMt,
        decimal amountUsd)
        => new()
        {
            SalesTransactionId = saleId,
            SourcePurchaseContractId = contractId,
            QuantityMt = quantityMt,
            AmountUsd = amountUsd
        };

    private static LedgerEntry SaleLedger(int saleId, int? contractId, decimal amountUsd)
        => new()
        {
            EntryDate = new DateTime(2026, 4, 22),
            Side = LedgerSide.Credit,
            AmountUsd = amountUsd,
            Currency = "USD",
            SourceAmount = amountUsd,
            SourceCurrencyCode = "USD",
            Description = "Sale",
            SourceType = "Sale",
            SourceId = saleId,
            Reference = $"INV-{saleId}",
            ContractId = contractId,
            CustomerId = 1
        };
}
