using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Controllers;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.ShipmentPnl;
using PTGOilSystem.Web.Services;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// قیمت خرید محموله باید Source-based باشد:
///   مقدار تخصیص هر قرارداد × نرخ مؤثر همان قرارداد
/// که نرخ مؤثر = میانگین وزنی نرخ‌های قطعیِ ثبت‌شده در بارگیری‌ها (LoadingRegister.LoadingPriceUsd)
/// و فقط در نبود آن، نرخ نهایی هدر قرارداد. این تست‌ها سناریوهای Platts قفل‌شده،
/// چند بارگیری با نرخ متفاوت، چند قرارداد و fallback داده‌های قدیمی را قفل می‌کنند.
/// </summary>
public class ShipmentPurchaseCostTests
{
    private static ApplicationDbContext NewDb()
    {
        var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
        // Navigationهای الزامی قرارداد؛ بدون آن‌ها Include(Contract).ThenInclude(Product/Company)
        // در InMemory مثل INNER JOIN ردیف را حذف می‌کند.
        db.Companies.Add(new Company { Id = 1, Code = "CO", Name = "Co" });
        db.Products.Add(new Product { Id = 1, Code = "P", Name = "Diesel" });
        db.SaveChanges();
        return db;
    }

    private static Contract PurchaseContract(
        int id,
        PricingMethod pricingMethod,
        decimal? manualFinalPriceUsd = null,
        decimal? unitPriceUsd = null)
        => new()
        {
            Id = id,
            ContractName = $"CN-{id}",
            ContractNumber = $"C-{id}",
            ContractType = ContractType.Purchase,
            CompanyId = 1,
            ProductId = 1,
            PricingMethod = pricingMethod,
            ManualFinalPriceUsd = manualFinalPriceUsd,
            UnitPriceUsd = unitPriceUsd,
            ContractDate = new DateTime(2026, 1, 1)
        };

    private static LoadingRegister Loading(int contractId, decimal quantityMt, decimal? lockedPriceUsd)
        => new()
        {
            ContractId = contractId,
            ProductId = 1,
            LoadingDate = new DateTime(2026, 2, 1),
            LoadedQuantityMt = quantityMt,
            LoadingPriceUsd = lockedPriceUsd
        };

    private static async Task<ShipmentPnlDetailsViewModel> LoadDetailsAsync(ApplicationDbContext db, int shipmentId)
    {
        var controller = new ShipmentPnlController(db);
        var result = Assert.IsType<ViewResult>(await controller.Details(shipmentId));
        return Assert.IsType<ShipmentPnlDetailsViewModel>(result.Model);
    }

    // ===== واحد: زنجیرهٔ رسمی نرخ =====

    [Fact]
    public void Resolver_Prefers_Loading_Weighted_Average_Over_Contract_Header()
    {
        var snapshot = new PurchaseAggregationSnapshot(
            ContractId: 1,
            TotalLoadedQuantityMt: 300m,
            PricedPurchaseQuantityMt: 300m,
            PendingPurchaseQuantityMt: 0m,
            PendingLoadingCount: 0,
            TraceablePurchaseCostUsd: 156000m,
            WeightedAveragePurchasePriceUsd: 520m,
            LoadingTransportExpenseUsd: 0m,
            LoadingWarehouseExpenseUsd: 0m,
            LoadingOtherExpenseUsd: 0m,
            LoadingRailwayExpenseUsd: 0m,
            LoadingRailwayExpenseUsdFromLines: 0m);

        var (unitCost, source) = ShipmentPurchaseCostResolver.ResolveContractUnitCost(snapshot, contractFinalPriceUsd: 999m);

        Assert.Equal(520m, unitCost);
        Assert.Equal(ShipmentPurchaseCostResolver.SourceContractWeightedAverage, source);
    }

    [Fact]
    public void Resolver_Falls_Back_To_Contract_Final_Price_Then_Missing()
    {
        var (fallbackCost, fallbackSource) = ShipmentPurchaseCostResolver.ResolveContractUnitCost(null, 600m);
        Assert.Equal(600m, fallbackCost);
        Assert.Equal(ShipmentPurchaseCostResolver.SourceContractFinalPrice, fallbackSource);

        var (missingCost, missingSource) = ShipmentPurchaseCostResolver.ResolveContractUnitCost(null, null);
        Assert.Null(missingCost);
        Assert.Equal(ShipmentPurchaseCostResolver.SourceMissing, missingSource);
    }

    // ===== سناریو: Platts قفل‌شده هنگام بارگیری =====

    [Fact]
    public async Task Platts_Contract_Uses_Locked_Loading_Price_Not_Contract_Header()
    {
        using var db = NewDb();
        // قرارداد Platts بدون نرخ نهایی هدر — نرخ فقط هنگام بارگیری قفل شده است.
        db.Contracts.Add(PurchaseContract(1, PricingMethod.FormulaPlatts));
        db.LoadingRegisters.Add(Loading(contractId: 1, quantityMt: 100m, lockedPriceUsd: 500m));
        db.Shipments.Add(new Shipment { Id = 10, ShipmentCode = "S1", QuantityMt = 100m, ContractId = 1 });
        db.ShipmentContracts.Add(new ShipmentContract { ShipmentId = 10, ContractId = 1, QuantityMt = 100m });
        await db.SaveChangesAsync();

        var model = await LoadDetailsAsync(db, 10);

        // 100 × 500 (نرخ قفل‌شدهٔ بارگیری)، نه صفر و نه نرخ Platts جدید.
        Assert.Equal(50000m, model.TotalPurchaseCostUsd);
        // با وجود نداشتن نرخ هدر، قیمت از بارگیری‌ها قطعی است → اخطار «نرخ ناقص» نمایش داده نمی‌شود.
        Assert.False(model.PurchasePricingIncomplete);
        var line = Assert.Single(model.ContractLines);
        Assert.Equal(500m, line.UnitPriceUsd);
        Assert.Equal(ShipmentPurchaseCostResolver.SourceContractWeightedAverage, line.UnitPriceSource);
    }

    // ===== سناریو: چند بارگیری با نرخ متفاوت در یک قرارداد =====

    [Fact]
    public async Task Multiple_Loadings_With_Different_Prices_Produce_Weighted_Cost()
    {
        using var db = NewDb();
        db.Contracts.Add(PurchaseContract(1, PricingMethod.FormulaPlatts));
        db.LoadingRegisters.Add(Loading(1, 100m, 500m));
        db.LoadingRegisters.Add(Loading(1, 200m, 530m));
        db.Shipments.Add(new Shipment { Id = 11, ShipmentCode = "S2", QuantityMt = 150m, ContractId = 1 });
        db.ShipmentContracts.Add(new ShipmentContract { ShipmentId = 11, ContractId = 1, QuantityMt = 150m });
        await db.SaveChangesAsync();

        var model = await LoadDetailsAsync(db, 11);

        // میانگین وزنی = (100×500 + 200×530) / 300 = 520 → 150 × 520 = 78000.
        Assert.Equal(78000m, model.TotalPurchaseCostUsd);
        Assert.Equal(520m, Assert.Single(model.ContractLines).UnitPriceUsd);
    }

    // ===== سناریو: چند قرارداد در یک محموله =====

    [Fact]
    public async Task Multi_Contract_Shipment_Costs_Each_Allocation_From_Its_Own_Source()
    {
        using var db = NewDb();
        db.Contracts.Add(PurchaseContract(1, PricingMethod.FormulaPlatts));
        db.Contracts.Add(PurchaseContract(2, PricingMethod.FormulaPlatts));
        db.LoadingRegisters.Add(Loading(1, 100m, 500m));
        db.LoadingRegisters.Add(Loading(2, 200m, 550m));
        db.Shipments.Add(new Shipment { Id = 12, ShipmentCode = "S3", QuantityMt = 300m, ContractId = 1 });
        db.ShipmentContracts.Add(new ShipmentContract { ShipmentId = 12, ContractId = 1, QuantityMt = 100m });
        db.ShipmentContracts.Add(new ShipmentContract { ShipmentId = 12, ContractId = 2, QuantityMt = 200m });
        await db.SaveChangesAsync();

        var model = await LoadDetailsAsync(db, 12);

        // 100×500 + 200×550 = 160000 — نه کل محموله با نرخ قرارداد اول.
        Assert.Equal(160000m, model.TotalPurchaseCostUsd);
        Assert.Equal(2, model.ContractLines.Count);
        Assert.Equal(500m, model.ContractLines.Single(l => l.ContractId == 1).UnitPriceUsd);
        Assert.Equal(550m, model.ContractLines.Single(l => l.ContractId == 2).UnitPriceUsd);
        // میانگین نمایشی: 160000 / 300.
        Assert.Equal(decimal.Round(160000m / 300m, 4, MidpointRounding.AwayFromZero),
            decimal.Round(model.AverageCostPerMtUsd, 4, MidpointRounding.AwayFromZero));
    }

    // ===== سناریو: هدر دستی نباید نرخ واقعی بارگیری را بپوشاند =====

    [Fact]
    public async Task Manual_Contract_Header_Price_Does_Not_Override_Locked_Loading_Prices()
    {
        using var db = NewDb();
        db.Contracts.Add(PurchaseContract(1, PricingMethod.ManualFinalPrice, manualFinalPriceUsd: 999m));
        db.LoadingRegisters.Add(Loading(1, 100m, 500m));
        db.Shipments.Add(new Shipment { Id = 13, ShipmentCode = "S4", QuantityMt = 100m, ContractId = 1 });
        db.ShipmentContracts.Add(new ShipmentContract { ShipmentId = 13, ContractId = 1, QuantityMt = 100m });
        await db.SaveChangesAsync();

        var model = await LoadDetailsAsync(db, 13);

        Assert.Equal(50000m, model.TotalPurchaseCostUsd);
    }

    // ===== سناریو: fallback داده‌های قدیمی (بدون بارگیری قیمت‌دار) =====

    [Fact]
    public async Task Contract_Without_Priced_Loadings_Falls_Back_To_Contract_Final_Price()
    {
        using var db = NewDb();
        db.Contracts.Add(PurchaseContract(1, PricingMethod.ManualFinalPrice, manualFinalPriceUsd: 600m));
        db.Shipments.Add(new Shipment { Id = 14, ShipmentCode = "S5", QuantityMt = 50m, ContractId = 1 });
        db.ShipmentContracts.Add(new ShipmentContract { ShipmentId = 14, ContractId = 1, QuantityMt = 50m });
        await db.SaveChangesAsync();

        var model = await LoadDetailsAsync(db, 14);

        Assert.Equal(30000m, model.TotalPurchaseCostUsd);
        var line = Assert.Single(model.ContractLines);
        Assert.Equal(600m, line.UnitPriceUsd);
        Assert.Equal(ShipmentPurchaseCostResolver.SourceContractFinalPrice, line.UnitPriceSource);
    }

    // ===== سناریو: بدون هیچ نرخی → بها صفر و اخطار «نرخ ناقص» =====

    [Fact]
    public async Task Contract_With_No_Price_Anywhere_Reports_Incomplete_Pricing()
    {
        using var db = NewDb();
        db.Contracts.Add(PurchaseContract(1, PricingMethod.FormulaPlatts));
        db.Shipments.Add(new Shipment { Id = 15, ShipmentCode = "S6", QuantityMt = 40m, ContractId = 1 });
        db.ShipmentContracts.Add(new ShipmentContract { ShipmentId = 15, ContractId = 1, QuantityMt = 40m });
        await db.SaveChangesAsync();

        var model = await LoadDetailsAsync(db, 15);

        Assert.Equal(0m, model.TotalPurchaseCostUsd);
        Assert.True(model.PurchasePricingIncomplete);
        Assert.False(Assert.Single(model.ContractLines).HasFinalPrice);
    }
}
