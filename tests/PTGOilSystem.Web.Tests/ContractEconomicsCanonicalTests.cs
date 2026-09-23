using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Controllers;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.Reports;
using PTGOilSystem.Web.Services.Reporting;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// سودِ قرارداد فقط از <see cref="ProfitAndLossService.BuildContractEconomicsAsync"/> می‌آید:
/// «سودِ محقق» فقط بخشِ فروخته‌شده را می‌شناسد و «سودِ چرخهٔ کامل» معیارِ جدایی است.
/// </summary>
public class ContractEconomicsCanonicalTests
{
    private const int PurchaseId = 5;
    private const int OtherPurchaseId = 6;
    private const int SaleContractId = 9;

    [Fact]
    public async Task Partial_Sale_Recognizes_Only_Sold_Share_And_Keeps_Lifecycle_Separate()
    {
        await using var db = NewDb();
        Seed(db);
        db.LoadingRegisters.Add(Loading(1, PurchaseId, 100m, 500m));
        db.ExpenseTransactions.Add(Expense(1, 1_000m, contractId: PurchaseId));
        db.SalesTransactions.Add(Sale(100, 40m, 24_000m));
        db.InventoryMovements.Add(StockOut(1, PurchaseId, saleId: 100, quantityMt: 40m));
        await db.SaveChangesAsync();

        var e = await EconomicsAsync(db, PurchaseId);

        Assert.Equal(40m, e.SoldQuantityMt);
        Assert.Equal(24_000m, e.RevenueUsd);
        Assert.Equal(0.4m, e.SoldShareRatio);
        Assert.Equal(20_000m, e.RealizedCostOfGoodsSoldUsd);
        Assert.Equal(400m, e.RealizedOperationalCostUsd);
        Assert.Equal(3_600m, e.RealizedNetProfitUsd);

        // چرخهٔ کامل: کلِ خرید و کلِ هزینه، چه فروخته شده باشد چه نه.
        Assert.Equal(51_000m, e.LifecycleTotalCostUsd);
        Assert.Equal(-27_000m, e.LifecycleMarginUsd);
    }

    [Fact]
    public async Task Unsold_Stock_Has_Zero_Realized_Profit_Not_A_Loss()
    {
        await using var db = NewDb();
        Seed(db);
        db.LoadingRegisters.Add(Loading(1, PurchaseId, 100m, 500m));
        db.ExpenseTransactions.Add(Expense(1, 1_000m, contractId: PurchaseId));
        await db.SaveChangesAsync();

        var e = await EconomicsAsync(db, PurchaseId);

        Assert.Equal(0m, e.RealizedCostOfGoodsSoldUsd);
        Assert.Equal(0m, e.RealizedOperationalCostUsd);
        Assert.Equal(0m, e.RealizedNetProfitUsd);
        Assert.Equal(-51_000m, e.LifecycleMarginUsd);
    }

    [Fact]
    public async Task Expense_And_Loss_Linked_Only_Through_Loading_Belong_To_That_Purchase_Contract()
    {
        await using var db = NewDb();
        Seed(db);
        db.LoadingRegisters.Add(Loading(1, PurchaseId, 100m, 500m));
        // بدون ContractId؛ فقط بارگیری. مصرفِ رسمیِ بارگیری فیلدهای درون‌خطی را کنار می‌زند، پس
        // اگر اینجا شمرده نشود هیچ‌جا شمرده نمی‌شود.
        db.ExpenseTransactions.Add(Expense(1, 700m, loadingRegisterId: 1));
        db.LossEvents.Add(Loss(1, chargeableMt: 2m, loadingRegisterId: 1));
        await db.SaveChangesAsync();

        var e = await EconomicsAsync(db, PurchaseId);

        Assert.Equal(700m, e.GeneralExpenseCostUsd);
        Assert.Equal(1_000m, e.LossCostUsd);
        Assert.Equal(0, e.UnvaluedLossCount);
    }

    [Theory]
    [InlineData(CostResponsibility.Seller, 0, 1_000)]   // فروشندهٔ قرارداد خرید = طرفِ بیرونی
    [InlineData(CostResponsibility.Buyer, 1_000, 0)]    // خریدارِ قرارداد خرید = خودِ شرکت
    [InlineData(CostResponsibility.Shared, 1_000, 0)]   // سهم ثبت نشده → بدونِ تغییر (شرکت)
    [InlineData(CostResponsibility.Unspecified, 1_000, 0)]
    public async Task Cost_Responsibility_On_Purchase_Contract(CostResponsibility responsibility, int companyBorne, int externalBorne)
    {
        await using var db = NewDb();
        Seed(db);
        db.LoadingRegisters.Add(Loading(1, PurchaseId, 100m, 500m));
        var expense = Expense(1, 1_000m, contractId: PurchaseId);
        expense.CostResponsibility = responsibility;
        db.ExpenseTransactions.Add(expense);
        await db.SaveChangesAsync();

        var e = await EconomicsAsync(db, PurchaseId);

        Assert.Equal(companyBorne, e.GeneralExpenseCostUsd);
        Assert.Equal(externalBorne, e.ExternalPartyBorneExpenseUsd);
    }

    [Theory]
    [InlineData(CostResponsibility.Buyer, 0, 1_000)]    // خریدارِ قرارداد فروش = مشتری
    [InlineData(CostResponsibility.Seller, 1_000, 0)]   // فروشندهٔ قرارداد فروش = خودِ شرکت
    [InlineData(CostResponsibility.Shared, 1_000, 0)]
    public async Task Cost_Responsibility_On_Sale_Contract_And_Company_Pnl_Agree(
        CostResponsibility responsibility,
        int companyBorne,
        int externalBorne)
    {
        await using var db = NewDb();
        Seed(db);
        var expense = Expense(1, 1_000m, contractId: SaleContractId);
        expense.CostResponsibility = responsibility;
        db.ExpenseTransactions.Add(expense);
        await db.SaveChangesAsync();

        var service = new ProfitAndLossService(db);
        var e = (await service.BuildContractEconomicsAsync([SaleContractId]))[SaleContractId];
        var company = await service.BuildCompanyAsync(new ManagementReportFilterViewModel
        {
            FromDate = new DateTime(2026, 1, 1),
            ToDate = new DateTime(2026, 12, 31)
        });

        Assert.Equal(companyBorne, e.GeneralExpenseCostUsd);
        Assert.Equal(externalBorne, e.ExternalPartyBorneExpenseUsd);
        Assert.Equal(companyBorne, company.OperatingExpenseUsd);
    }

    [Fact]
    public async Task Policy_Resolves_Buyer_And_Seller_Relative_To_Contract_Role()
    {
        Assert.Equal(CostBearer.ExternalParty, CostResponsibilityPolicy.Resolve(CostResponsibility.Buyer, ContractType.Sale));
        Assert.Equal(CostBearer.Company, CostResponsibilityPolicy.Resolve(CostResponsibility.Buyer, ContractType.Purchase));
        Assert.Equal(CostBearer.ExternalParty, CostResponsibilityPolicy.Resolve(CostResponsibility.Seller, ContractType.Purchase));
        Assert.Equal(CostBearer.Company, CostResponsibilityPolicy.Resolve(CostResponsibility.Seller, ContractType.Sale));
        Assert.Equal(CostBearer.SharedUndetermined, CostResponsibilityPolicy.Resolve(CostResponsibility.Shared, ContractType.Sale));
        Assert.Equal(CostBearer.Company, CostResponsibilityPolicy.Resolve(null, null));
        Assert.Equal(CostBearer.Company, CostResponsibilityPolicy.Resolve(CostResponsibility.Buyer, null));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Unproven_Sale_Owner_Does_Not_Depend_On_Which_Contracts_Are_Requested()
    {
        await using var db = NewDb();
        Seed(db);
        db.LoadingRegisters.AddRange(Loading(1, PurchaseId, 100m, 500m), Loading(2, OtherPurchaseId, 100m, 500m));
        var sale = Sale(100, 10m, 6_000m);
        sale.SourcePurchaseContractId = OtherPurchaseId; // مسیرِ آخر
        db.SalesTransactions.Add(sale);
        db.InventoryMovements.Add(StockOut(1, PurchaseId, saleId: 100, quantityMt: 10m)); // مسیرِ مقدم
        await db.SaveChangesAsync();

        var service = new ProfitAndLossService(db);
        var alone = (await service.BuildContractEconomicsAsync([OtherPurchaseId]))[OtherPurchaseId];
        var both = await service.BuildContractEconomicsAsync([PurchaseId, OtherPurchaseId]);

        Assert.Empty(alone.Sales);
        Assert.Empty(both[OtherPurchaseId].Sales);
        Assert.Equal(6_000m, both[PurchaseId].RevenueUsd);
        Assert.Equal(6_000m, both.Values.Sum(x => x.RevenueUsd));
    }

    [Fact]
    public async Task Untagged_Shipment_Expense_Is_Shared_By_Recorded_Contract_Quantity()
    {
        await using var db = NewDb();
        Seed(db);
        db.LoadingRegisters.AddRange(Loading(1, PurchaseId, 60m, 500m), Loading(2, OtherPurchaseId, 40m, 500m));
        db.Shipments.Add(new Shipment { Id = 1, ShipmentCode = "SH-1", QuantityMt = 100m });
        db.ShipmentContracts.AddRange(
            new ShipmentContract { Id = 1, ShipmentId = 1, ContractId = PurchaseId, QuantityMt = 60m },
            new ShipmentContract { Id = 2, ShipmentId = 1, ContractId = OtherPurchaseId, QuantityMt = 40m });
        var shared = Expense(1, 1_000m);
        shared.ShipmentId = 1;
        db.ExpenseTransactions.Add(shared);
        await db.SaveChangesAsync();

        var economics = await new ProfitAndLossService(db).BuildContractEconomicsAsync([PurchaseId, OtherPurchaseId]);

        Assert.Equal(600m, economics[PurchaseId].SharedShipmentExpenseUsd);
        Assert.Equal(400m, economics[OtherPurchaseId].SharedShipmentExpenseUsd);
        Assert.Equal(600m, economics[PurchaseId].OperationalCostBaseUsd);
    }

    [Fact]
    public async Task Transport_Shortage_Without_Recorded_Loss_Is_Valued_At_Weighted_Average()
    {
        await using var db = NewDb();
        Seed(db);
        db.LoadingRegisters.Add(Loading(1, PurchaseId, 100m, 500m));
        db.InventoryTransportLegs.Add(new InventoryTransportLeg
        {
            Id = 1,
            SourcePurchaseContractId = PurchaseId,
            ProductId = 1,
            LoadedDate = new DateTime(2026, 4, 5),
            QuantityMt = 30m,
            Status = InventoryTransportLegStatus.Received
        });
        db.InventoryTransportReceipts.Add(new InventoryTransportReceipt
        {
            Id = 1,
            InventoryTransportLegId = 1,
            ReceiptDate = new DateTime(2026, 4, 8),
            ReceivedQuantityMt = 29m,
            ShortageQuantityMt = 1m
        });
        await db.SaveChangesAsync();

        var e = await EconomicsAsync(db, PurchaseId);

        Assert.Equal(500m, e.ShipmentLossCostUsd);
    }

    [Fact]
    public async Task Contract_Pnl_Report_Shows_The_Canonical_Realized_And_Lifecycle_Values()
    {
        await using var db = NewDb();
        Seed(db);
        db.LoadingRegisters.Add(Loading(1, PurchaseId, 100m, 500m));
        db.ExpenseTransactions.Add(Expense(1, 1_000m, contractId: PurchaseId));
        db.SalesTransactions.Add(Sale(100, 40m, 24_000m));
        db.InventoryMovements.Add(StockOut(1, PurchaseId, saleId: 100, quantityMt: 40m));
        await db.SaveChangesAsync();

        var e = await EconomicsAsync(db, PurchaseId);
        var view = Assert.IsType<ViewResult>(
            await new ReportsController(db).ContractPnl(new ManagementReportFilterViewModel { ContractId = PurchaseId }));
        var row = Assert.Single(Assert.IsType<ContractPnlReportViewModel>(view.Model).PurchaseRows);

        Assert.Equal(e.RealizedNetProfitUsd, row.RealizedNetProfitUsd);
        Assert.Equal(e.LifecycleMarginUsd, row.GrossMarginUsd);
        Assert.Equal(e.LifecycleTotalCostUsd, row.TotalCostUsd);
        Assert.NotEqual(row.RealizedNetProfitUsd, row.GrossMarginUsd);
    }

    [Fact]
    public async Task Partnership_Profit_Is_The_Canonical_Realized_Profit_And_Unsold_Cost_Is_Shared_Separately()
    {
        await using var db = NewDb();
        Seed(db);
        db.Partners.AddRange(
            new Partner { Id = 1, Code = "PA-1", Name = "Partner A", IsActive = true },
            new Partner { Id = 2, Code = "PA-2", Name = "Partner B", IsActive = true });
        var contract = await db.Contracts.FindAsync(PurchaseId);
        contract!.OwnershipType = ContractOwnershipType.Partnership;
        contract.SaleProceedsHolderPartnerId = 2;
        db.ContractPartners.AddRange(
            new ContractPartner { ContractId = PurchaseId, PartnerId = 1, SharePercent = 50m },
            new ContractPartner { ContractId = PurchaseId, PartnerId = 2, SharePercent = 50m });
        db.LoadingRegisters.Add(Loading(1, PurchaseId, 100m, 500m));
        db.ExpenseTransactions.Add(Expense(1, 1_000m, contractId: PurchaseId));
        db.SalesTransactions.Add(Sale(100, 40m, 24_000m));
        db.InventoryMovements.Add(StockOut(1, PurchaseId, saleId: 100, quantityMt: 40m));
        await db.SaveChangesAsync();

        var e = await EconomicsAsync(db, PurchaseId);
        var statement = await new PTGOilSystem.Web.Services.PartyStatements.PartnershipStatementService(db)
            .BuildForContractAsync(PurchaseId);

        Assert.NotNull(statement);
        Assert.Equal(e.RevenueUsd, statement!.SalesUsd);
        Assert.Equal(e.RealizedNetProfitUsd, statement.BookProfitUsd);
        Assert.Equal(3_600m, statement.BookProfitUsd);
        Assert.Equal(e.RealizedNetProfitUsd, statement.Partners.Sum(p => p.ProfitShareUsd));

        // کلِ هزینه ۵۱٬۰۰۰، فروخته‌شده ۲۰٬۴۰۰ → ۳۰٬۶۰۰ هزینهٔ فروخته‌نشده، نصف‌نصف.
        Assert.Equal(30_600m, statement.UnrealizedCostCarriedUsd);
        Assert.All(statement.Partners, p => Assert.Equal(15_300m, p.UnsoldCostShareUsd));
        // برابرسازیِ هزینه بین شرکا همان مفادِ کامل است: ۱۸۰۰ − ۱۵۳۰۰ = −۱۳۵۰۰ هر شریک.
        Assert.All(statement.Partners, p => Assert.Equal(
            decimal.Round(e.LifecycleMarginUsd / 2m, 2),
            p.ProfitShareUsd - p.UnsoldCostShareUsd));
    }

    // ---- helpers ----

    private static async Task<ContractEconomicsSnapshot> EconomicsAsync(ApplicationDbContext db, int contractId)
        => (await new ProfitAndLossService(db).BuildContractEconomicsAsync([contractId]))[contractId];

    private static ApplicationDbContext NewDb()
        => new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static void Seed(ApplicationDbContext db)
    {
        db.Companies.Add(new Company { Id = 1, Code = "PTG", Name = "PTG" });
        db.Products.Add(new Product { Id = 1, Code = "GAS", Name = "Gasoline" });
        db.Suppliers.Add(new Supplier { Id = 1, Name = "Supplier A" });
        db.Customers.Add(new Customer { Id = 1, Name = "Customer A" });
        db.Terminals.Add(new Terminal { Id = 1, Code = "ILK", Name = "Ilinka" });
        db.StorageTanks.Add(new StorageTank { Id = 1, TerminalId = 1, TankCode = "TANK-A" });
        db.ExpenseTypes.Add(new ExpenseType { Id = 1, Code = "GEN", Name = "General" });
        db.Contracts.AddRange(
            Contract(PurchaseId, "P-005", ContractType.Purchase),
            Contract(OtherPurchaseId, "P-006", ContractType.Purchase),
            Contract(SaleContractId, "S-009", ContractType.Sale));
    }

    private static Contract Contract(int id, string number, ContractType type)
        => new()
        {
            Id = id,
            ContractNumber = number,
            ContractType = type,
            CompanyId = 1,
            ProductId = 1,
            SupplierId = type == ContractType.Purchase ? 1 : null,
            CustomerId = type == ContractType.Sale ? 1 : null,
            ContractDate = new DateTime(2026, 4, 1),
            QuantityMt = 100m,
            PricingMethod = PricingMethod.Fixed,
            UnitPriceUsd = 500m
        };

    private static LoadingRegister Loading(int id, int contractId, decimal quantityMt, decimal priceUsd)
        => new()
        {
            Id = id,
            ContractId = contractId,
            ProductId = 1,
            LoadingDate = new DateTime(2026, 4, 2),
            LoadedQuantityMt = quantityMt,
            LoadingPriceUsd = priceUsd
        };

    private static ExpenseTransaction Expense(int id, decimal amountUsd, int? contractId = null, int? loadingRegisterId = null)
        => new()
        {
            Id = id,
            ExpenseTypeId = 1,
            ExpenseDate = new DateTime(2026, 4, 10),
            Amount = amountUsd,
            AmountUsd = amountUsd,
            Currency = "USD",
            ContractId = contractId,
            LoadingRegisterId = loadingRegisterId
        };

    private static LossEvent Loss(int id, decimal chargeableMt, int? loadingRegisterId = null)
        => new()
        {
            Id = id,
            Stage = LossEventStage.LoadingDifference,
            ProductId = 1,
            EventDate = new DateTime(2026, 4, 3),
            DifferenceQuantityMt = chargeableMt,
            ChargeableLossMt = chargeableMt,
            LoadingRegisterId = loadingRegisterId
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
            UnitPriceUsd = totalUsd / quantityMt,
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
}
