using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Controllers;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.ShipmentPnl;
using PTGOilSystem.Web.Models.Shipments;
using PTGOilSystem.Web.Services;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// سهم دقیق بارگیری در محموله (ShipmentLoadingAllocation).
///
/// قانون قفل‌شده: بهای خرید محموله = Σ (مقدار تخصیص‌یافته از هر بارگیری × نرخ قطعی همان بارگیری).
/// میانگین وزنی فقط عدد نمایشیِ مشتق است، نه ورودی محاسبه. محموله‌های قدیمیِ بدون سهم بارگیری
/// دقیقاً همان fallback قبلی (میانگین قرارداد → نرخ نهایی هدر) را نگه می‌دارند.
/// </summary>
public class ShipmentLoadingAllocationTests
{
    private const int PlattsContract = 1;
    private const int SecondContract = 2;

    // ===== زیرساخت =====

    private static ApplicationDbContext CreateDb()
    {
        var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
        db.Companies.Add(new Company { Id = 1, Code = "PTG", Name = "PTG", IsActive = true });
        db.Products.Add(new Product { Id = 1, Code = "PMS", Name = "Petrol", IsActive = true });
        db.SaveChanges();
        return db;
    }

    private static ShipmentsController BuildController(ApplicationDbContext db)
        => new(db)
        {
            TempData = new TempDataDictionary(new DefaultHttpContext(), new InMemoryTempDataProvider())
        };

    private sealed class InMemoryTempDataProvider : ITempDataProvider
    {
        private IDictionary<string, object?> _data = new Dictionary<string, object?>();

        public IDictionary<string, object?> LoadTempData(HttpContext context) => _data;

        public void SaveTempData(HttpContext context, IDictionary<string, object?> values) => _data = values;
    }

    private static Contract PurchaseContract(
        int id,
        PricingMethod pricingMethod = PricingMethod.FormulaPlatts,
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

    private static LoadingRegister Loading(int id, int contractId, decimal quantityMt, decimal? lockedPriceUsd)
        => new()
        {
            Id = id,
            ContractId = contractId,
            ProductId = 1,
            LoadingDate = new DateTime(2026, 2, id),
            LoadedQuantityMt = quantityMt,
            LoadingPriceUsd = lockedPriceUsd,
            BillOfLadingNumber = $"BL-{id}"
        };

    private static void AddShipment(
        ApplicationDbContext db,
        int shipmentId,
        int contractId,
        decimal allocatedMt,
        params (int LoadingRegisterId, decimal QuantityMt)[] loadingPicks)
    {
        if (!db.Shipments.Local.Any(s => s.Id == shipmentId) && !db.Shipments.Any(s => s.Id == shipmentId))
        {
            db.Shipments.Add(new Shipment
            {
                Id = shipmentId,
                ShipmentCode = $"S{shipmentId}",
                QuantityMt = allocatedMt,
                ContractId = contractId
            });
        }

        db.ShipmentContracts.Add(new ShipmentContract
        {
            ShipmentId = shipmentId,
            ContractId = contractId,
            QuantityMt = allocatedMt
        });

        foreach (var (loadingRegisterId, quantityMt) in loadingPicks)
        {
            db.ShipmentLoadingAllocations.Add(new ShipmentLoadingAllocation
            {
                ShipmentId = shipmentId,
                ContractId = contractId,
                LoadingRegisterId = loadingRegisterId,
                QuantityMt = quantityMt
            });
        }
    }

    private static async Task<ShipmentPnlDetailsViewModel> LoadDetailsAsync(ApplicationDbContext db, int shipmentId)
    {
        var result = Assert.IsType<ViewResult>(await new ShipmentPnlController(db).Details(shipmentId));
        return Assert.IsType<ShipmentPnlDetailsViewModel>(result.Model);
    }

    // ===== ۱. یک بارگیری، یک محموله =====

    [Fact]
    public async Task Single_Loading_Allocation_Uses_That_Loading_Price()
    {
        await using var db = CreateDb();
        db.Contracts.Add(PurchaseContract(PlattsContract));
        db.LoadingRegisters.Add(Loading(1, PlattsContract, 100m, 500m));
        AddShipment(db, 10, PlattsContract, 100m, (1, 100m));
        await db.SaveChangesAsync();

        var snapshot = await new ShipmentPurchaseCostService(db).BuildAsync(10);

        Assert.Equal(50000m, snapshot.TotalPurchaseCostUsd);
        Assert.Equal(100m, snapshot.TotalQuantityMt);
        Assert.True(snapshot.HasLoadingExactSources);
        var line = Assert.Single(snapshot.Lines);
        Assert.Equal(1, line.LoadingRegisterId);
        Assert.Equal(500m, line.UnitCostUsd);
        Assert.Equal(ShipmentPurchaseCostService.SourceLoadingExact, line.CostSource);
    }

    // ===== ۲. تخصیص جزئی از یک بارگیری =====

    [Fact]
    public async Task Partial_Loading_Allocation_Costs_Only_The_Allocated_Quantity()
    {
        await using var db = CreateDb();
        db.Contracts.Add(PurchaseContract(PlattsContract));
        // همان مثال گزارش: قرارداد دو بارگیری با نرخ متفاوت دارد (میانگین قرارداد ۵۲۰ می‌شود)،
        // ولی محموله فقط ۵۰ تن از بارگیری ۵۰۰ می‌گیرد.
        db.LoadingRegisters.AddRange(
            Loading(1, PlattsContract, 100m, 500m),
            Loading(2, PlattsContract, 200m, 530m));
        AddShipment(db, 11, PlattsContract, 50m, (1, 50m));
        await db.SaveChangesAsync();

        var snapshot = await new ShipmentPurchaseCostService(db).BuildAsync(11);

        // 50 × 500 = 25,000 — نه 50 × 520 (میانگین کل قرارداد).
        Assert.Equal(25000m, snapshot.TotalPurchaseCostUsd);
        Assert.Equal(500m, snapshot.WeightedAverageUnitCostUsd);
    }

    // ===== ۳. دو محموله از یک بارگیری =====

    [Fact]
    public async Task Two_Shipments_Can_Share_One_Loading_And_Each_Keeps_Its_Own_Cost()
    {
        await using var db = CreateDb();
        db.Contracts.Add(PurchaseContract(PlattsContract));
        db.LoadingRegisters.Add(Loading(1, PlattsContract, 100m, 500m));
        AddShipment(db, 12, PlattsContract, 30m, (1, 30m));
        AddShipment(db, 13, PlattsContract, 40m, (1, 40m));
        await db.SaveChangesAsync();

        var service = new ShipmentPurchaseCostService(db);
        Assert.Equal(15000m, (await service.BuildAsync(12)).TotalPurchaseCostUsd);
        Assert.Equal(20000m, (await service.BuildAsync(13)).TotalPurchaseCostUsd);

        // باقی‌ماندهٔ قابل تخصیص همان بارگیری برای یک محمولهٔ سوم فقط ۳۰ تن است.
        var capacity = await new ShipmentLoadingAllocationService(db)
            .GetCapacityForContractAsync(PlattsContract, currentShipmentId: null);
        Assert.Equal(30m, Assert.Single(capacity).RemainingForShipmentMt);
    }

    // ===== ۴. رد کردن تخصیص بیش از ظرفیت =====

    [Fact]
    public async Task Over_Allocating_A_Loading_Is_Rejected()
    {
        await using var db = CreateDb();
        db.Contracts.Add(PurchaseContract(PlattsContract));
        db.LoadingRegisters.Add(Loading(1, PlattsContract, 100m, 500m));
        AddShipment(db, 14, PlattsContract, 70m, (1, 70m));
        await db.SaveChangesAsync();

        var controller = BuildController(db);
        var result = await controller.Create(new ShipmentCreateViewModel
        {
            ShipmentCode = "OVER",
            ContractAllocations =
            [
                new ShipmentContractAllocationInput
                {
                    ContractId = PlattsContract,
                    QuantityMt = 40m,
                    LoadingAllocations = [new ShipmentLoadingAllocationInput { LoadingRegisterId = 1, QuantityMt = 40m }]
                }
            ]
        });

        Assert.IsType<ViewResult>(result);
        Assert.False(controller.ModelState.IsValid);
        Assert.Equal(1, await db.Shipments.CountAsync());
    }

    [Fact]
    public async Task Loading_Allocation_Must_Equal_Its_Contract_Allocation()
    {
        await using var db = CreateDb();
        db.Contracts.Add(PurchaseContract(PlattsContract));
        db.LoadingRegisters.Add(Loading(1, PlattsContract, 100m, 500m));
        await db.SaveChangesAsync();

        var controller = BuildController(db);
        var result = await controller.Create(new ShipmentCreateViewModel
        {
            ShipmentCode = "MISMATCH",
            ContractAllocations =
            [
                new ShipmentContractAllocationInput
                {
                    ContractId = PlattsContract,
                    QuantityMt = 60m,
                    // جمع سهم بارگیری‌ها ۴۰ است ولی تخصیص قرارداد ۶۰ — باید رد شود.
                    LoadingAllocations = [new ShipmentLoadingAllocationInput { LoadingRegisterId = 1, QuantityMt = 40m }]
                }
            ]
        });

        Assert.IsType<ViewResult>(result);
        Assert.False(controller.ModelState.IsValid);
        Assert.Empty(await db.ShipmentLoadingAllocations.ToListAsync());
    }

    // ===== ۵. چند بارگیری از یک قرارداد با نرخ متفاوت =====

    [Fact]
    public async Task Multiple_Loadings_Same_Contract_Use_Each_Loadings_Own_Price()
    {
        await using var db = CreateDb();
        db.Contracts.Add(PurchaseContract(PlattsContract));
        db.LoadingRegisters.AddRange(
            Loading(1, PlattsContract, 100m, 500m),
            Loading(2, PlattsContract, 200m, 530m));
        AddShipment(db, 15, PlattsContract, 150m, (1, 50m), (2, 100m));
        await db.SaveChangesAsync();

        var snapshot = await new ShipmentPurchaseCostService(db).BuildAsync(15);

        // 50×500 + 100×530 = 78,000
        Assert.Equal(78000m, snapshot.TotalPurchaseCostUsd);
        Assert.Equal(150m, snapshot.TotalQuantityMt);
        // میانگین وزنی نمایشی = 78,000 / 150 = 520 — این بار درست است چون از خودِ تخصیص آمده.
        Assert.Equal(520m, snapshot.WeightedAverageUnitCostUsd);
        Assert.Equal(2, snapshot.Lines.Count);
    }

    // ===== ۶. چند قرارداد + چند بارگیری =====

    [Fact]
    public async Task Multi_Contract_Multi_Loading_Costs_Every_Source_Separately()
    {
        await using var db = CreateDb();
        db.Contracts.AddRange(PurchaseContract(PlattsContract), PurchaseContract(SecondContract));
        db.LoadingRegisters.AddRange(
            Loading(1, PlattsContract, 50m, 490m),
            Loading(2, PlattsContract, 100m, 510m),
            Loading(3, SecondContract, 200m, 550m));
        AddShipment(db, 16, PlattsContract, 100m, (1, 25m), (2, 75m));
        AddShipment(db, 16, SecondContract, 100m, (3, 100m));
        await db.SaveChangesAsync();

        var snapshot = await new ShipmentPurchaseCostService(db).BuildAsync(16);

        // 25×490 + 75×510 + 100×550 = 12,250 + 38,250 + 55,000 = 105,500
        Assert.Equal(105500m, snapshot.TotalPurchaseCostUsd);
        Assert.Equal(200m, snapshot.TotalQuantityMt);
        Assert.Equal(3, snapshot.Lines.Count);

        var byContract = snapshot.CostByContract;
        Assert.Equal(50500m, byContract[PlattsContract]);
        Assert.Equal(55000m, byContract[SecondContract]);
    }

    // ===== ۷ و ۸. Platts قفل‌شده و تغییر Platts پس از بارگیری =====

    [Fact]
    public async Task Platts_Price_Locked_At_Loading_Survives_Later_Contract_Repricing()
    {
        await using var db = CreateDb();
        db.Contracts.Add(PurchaseContract(PlattsContract));
        db.LoadingRegisters.Add(Loading(1, PlattsContract, 100m, 500m));
        AddShipment(db, 17, PlattsContract, 100m, (1, 100m));
        await db.SaveChangesAsync();

        Assert.Equal(50000m, (await new ShipmentPurchaseCostService(db).BuildAsync(17)).TotalPurchaseCostUsd);

        // Platts بالا می‌رود و قرارداد بعداً نرخ نهایی جدید می‌گیرد؛ بارگیری دست‌نخورده می‌ماند.
        var contract = await db.Contracts.SingleAsync(c => c.Id == PlattsContract);
        contract.ManualFinalPriceUsd = 900m;
        await db.SaveChangesAsync();

        var afterRepricing = await new ShipmentPurchaseCostService(db).BuildAsync(17);
        Assert.Equal(50000m, afterRepricing.TotalPurchaseCostUsd);
        Assert.Equal(500m, Assert.Single(afterRepricing.Lines).UnitCostUsd);
    }

    // ===== ۱۰. نرخ دستیِ هدر نباید جای نرخ بارگیریِ تخصیص‌یافته را بگیرد =====

    [Fact]
    public async Task Manual_Header_Price_Never_Overrides_Allocated_Loading_Price()
    {
        await using var db = CreateDb();
        db.Contracts.Add(PurchaseContract(PlattsContract, PricingMethod.ManualFinalPrice, manualFinalPriceUsd: 999m));
        db.LoadingRegisters.Add(Loading(1, PlattsContract, 100m, 500m));
        AddShipment(db, 18, PlattsContract, 100m, (1, 100m));
        await db.SaveChangesAsync();

        Assert.Equal(50000m, (await new ShipmentPurchaseCostService(db).BuildAsync(18)).TotalPurchaseCostUsd);
    }

    [Fact]
    public async Task Allocated_Loading_Without_Price_Falls_Back_To_Contract_Header()
    {
        await using var db = CreateDb();
        db.Contracts.Add(PurchaseContract(PlattsContract, PricingMethod.ManualFinalPrice, manualFinalPriceUsd: 610m));
        db.LoadingRegisters.Add(Loading(1, PlattsContract, 100m, lockedPriceUsd: null));
        AddShipment(db, 19, PlattsContract, 100m, (1, 100m));
        await db.SaveChangesAsync();

        var snapshot = await new ShipmentPurchaseCostService(db).BuildAsync(19);

        Assert.Equal(61000m, snapshot.TotalPurchaseCostUsd);
        Assert.Equal(
            ShipmentPurchaseCostService.SourceLoadingWithoutPrice,
            Assert.Single(snapshot.Lines).CostSource);
    }

    // ===== ۱۱. سازگاری با دادهٔ قدیمی =====

    [Fact]
    public async Task Legacy_Shipment_Without_Loading_Allocations_Keeps_Contract_Average()
    {
        await using var db = CreateDb();
        db.Contracts.Add(PurchaseContract(PlattsContract));
        db.LoadingRegisters.AddRange(
            Loading(1, PlattsContract, 100m, 500m),
            Loading(2, PlattsContract, 200m, 530m));
        AddShipment(db, 20, PlattsContract, 150m);
        await db.SaveChangesAsync();

        var snapshot = await new ShipmentPurchaseCostService(db).BuildAsync(20);

        // بدون سهم دقیق: همان میانگین وزنی تاریخی قرارداد (520) × 150 = 78,000.
        Assert.False(snapshot.HasLoadingExactSources);
        Assert.Equal(78000m, snapshot.TotalPurchaseCostUsd);
        Assert.Equal(
            ShipmentPurchaseCostResolver.SourceContractWeightedAverage,
            Assert.Single(snapshot.Lines).CostSource);
    }

    [Fact]
    public async Task Exact_And_Legacy_Contracts_Can_Coexist_In_One_Shipment()
    {
        await using var db = CreateDb();
        db.Contracts.AddRange(PurchaseContract(PlattsContract), PurchaseContract(SecondContract));
        db.LoadingRegisters.AddRange(
            Loading(1, PlattsContract, 100m, 500m),
            Loading(2, SecondContract, 200m, 550m));
        AddShipment(db, 21, PlattsContract, 100m, (1, 100m));
        AddShipment(db, 21, SecondContract, 50m);
        await db.SaveChangesAsync();

        var snapshot = await new ShipmentPurchaseCostService(db).BuildAsync(21);

        // 100×500 (دقیق) + 50×550 (میانگین قرارداد دوم) = 77,500
        Assert.Equal(77500m, snapshot.TotalPurchaseCostUsd);
        Assert.True(snapshot.HasLoadingExactSources);
    }

    // ===== ۱۳ + ۱۶. P&L و ریز منابع در پروندهٔ محموله =====

    [Fact]
    public async Task Shipment_Details_Reports_Exact_Cost_And_Source_Breakdown()
    {
        await using var db = CreateDb();
        db.Contracts.AddRange(PurchaseContract(PlattsContract), PurchaseContract(SecondContract));
        db.LoadingRegisters.AddRange(
            Loading(1, PlattsContract, 100m, 500m),
            Loading(2, PlattsContract, 200m, 530m),
            Loading(3, SecondContract, 200m, 550m));
        AddShipment(db, 22, PlattsContract, 150m, (1, 50m), (2, 100m));
        AddShipment(db, 22, SecondContract, 100m, (3, 100m));
        await db.SaveChangesAsync();

        var model = await LoadDetailsAsync(db, 22);

        // 50×500 + 100×530 + 100×550 = 133,000
        Assert.Equal(133000m, model.TotalPurchaseCostUsd);
        Assert.Equal(3, model.PurchaseSourceLines.Count);
        Assert.True(model.HasLoadingExactPurchaseSources);
        Assert.Equal(250m, model.PurchaseSourceQuantityMt);
        Assert.Equal(532m, model.PurchaseSourceWeightedAverageUsd);
        // سود/زیان از همان بهای خرید دقیق ساخته می‌شود.
        Assert.Equal(-133000m, model.ShipmentNetResultUsd);
        // ردیف تب قراردادها نباید نرخی متفاوت از جمع کل نشان بدهد.
        Assert.Equal(520m, model.ContractLines.Single(l => l.ContractId == PlattsContract).UnitPriceUsd);
        Assert.Equal(78000m, model.ContractLines.Single(l => l.ContractId == PlattsContract).TotalValueUsd);
    }

    // ===== ۱۴ + ۱۵. گارد ویرایش و رفتار پیش‌نویس =====

    [Fact]
    public async Task Edit_Rebuilds_Loading_Allocations_When_No_Downstream_Activity()
    {
        await using var db = CreateDb();
        db.Contracts.Add(PurchaseContract(PlattsContract));
        db.LoadingRegisters.AddRange(
            Loading(1, PlattsContract, 100m, 500m),
            Loading(2, PlattsContract, 200m, 530m));
        AddShipment(db, 23, PlattsContract, 100m, (1, 100m));
        await db.SaveChangesAsync();

        var controller = BuildController(db);
        var result = await controller.Edit(new ShipmentCreateViewModel
        {
            Id = 23,
            ShipmentCode = "S23",
            ContractAllocations =
            [
                new ShipmentContractAllocationInput
                {
                    ContractId = PlattsContract,
                    QuantityMt = 100m,
                    LoadingAllocations = [new ShipmentLoadingAllocationInput { LoadingRegisterId = 2, QuantityMt = 100m }]
                }
            ]
        });

        Assert.IsType<RedirectToActionResult>(result);
        var allocation = await db.ShipmentLoadingAllocations.SingleAsync();
        Assert.Equal(2, allocation.LoadingRegisterId);
        Assert.Equal(53000m, (await new ShipmentPurchaseCostService(db).BuildAsync(23)).TotalPurchaseCostUsd);
    }

    [Fact]
    public async Task Edit_Keeps_Loading_Allocations_When_Downstream_Sale_Exists()
    {
        await using var db = CreateDb();
        db.Contracts.Add(PurchaseContract(PlattsContract));
        db.LoadingRegisters.AddRange(
            Loading(1, PlattsContract, 100m, 500m),
            Loading(2, PlattsContract, 200m, 530m));
        AddShipment(db, 24, PlattsContract, 100m, (1, 100m));
        db.SalesTransactions.Add(new SalesTransaction
        {
            Id = 1,
            ShipmentId = 24,
            ProductId = 1,
            SaleDate = new DateTime(2026, 3, 1),
            QuantityMt = 10m,
            UnitPriceUsd = 700m,
            TotalUsd = 7000m
        });
        await db.SaveChangesAsync();

        var controller = BuildController(db);
        var result = await controller.Edit(new ShipmentCreateViewModel
        {
            Id = 24,
            ShipmentCode = "S24-RENAMED",
            ContractAllocations =
            [
                new ShipmentContractAllocationInput
                {
                    ContractId = PlattsContract,
                    QuantityMt = 100m,
                    LoadingAllocations = [new ShipmentLoadingAllocationInput { LoadingRegisterId = 2, QuantityMt = 100m }]
                }
            ]
        });

        Assert.IsType<RedirectToActionResult>(result);
        // گارد: فروش ثبت‌شده یعنی تخصیص قفل است — سهم بارگیری اصلی دست‌نخورده می‌ماند.
        var allocation = await db.ShipmentLoadingAllocations.SingleAsync();
        Assert.Equal(1, allocation.LoadingRegisterId);
        Assert.Equal(50000m, (await new ShipmentPurchaseCostService(db).BuildAsync(24)).TotalPurchaseCostUsd);
        // ولی هدر ویرایش شده است.
        Assert.Equal("S24-RENAMED", (await db.Shipments.SingleAsync(s => s.Id == 24)).ShipmentCode);
    }

    // ===== ۱۷. Idempotency =====

    [Fact]
    public async Task Rebuilding_The_Same_Allocation_Twice_Does_Not_Duplicate_Rows_Or_Cost()
    {
        await using var db = CreateDb();
        db.Contracts.Add(PurchaseContract(PlattsContract));
        db.LoadingRegisters.Add(Loading(1, PlattsContract, 100m, 500m));
        AddShipment(db, 25, PlattsContract, 100m, (1, 100m));
        await db.SaveChangesAsync();

        var model = new ShipmentCreateViewModel
        {
            Id = 25,
            ShipmentCode = "S25",
            ContractAllocations =
            [
                new ShipmentContractAllocationInput
                {
                    ContractId = PlattsContract,
                    QuantityMt = 100m,
                    LoadingAllocations = [new ShipmentLoadingAllocationInput { LoadingRegisterId = 1, QuantityMt = 100m }]
                }
            ]
        };

        Assert.IsType<RedirectToActionResult>(await BuildController(db).Edit(model));
        Assert.IsType<RedirectToActionResult>(await BuildController(db).Edit(model));

        Assert.Single(await db.ShipmentLoadingAllocations.ToListAsync());
        Assert.Equal(50000m, (await new ShipmentPurchaseCostService(db).BuildAsync(25)).TotalPurchaseCostUsd);
    }

    // ===== Create از مسیر کنترلر با سهم بارگیری =====

    [Fact]
    public async Task Create_Persists_Loading_Allocations_And_Prices_From_Them()
    {
        await using var db = CreateDb();
        db.Contracts.Add(PurchaseContract(PlattsContract));
        db.LoadingRegisters.AddRange(
            Loading(1, PlattsContract, 100m, 500m),
            Loading(2, PlattsContract, 200m, 530m));
        await db.SaveChangesAsync();

        var controller = BuildController(db);
        var result = await controller.Create(new ShipmentCreateViewModel
        {
            ShipmentCode = "EXACT",
            ContractAllocations =
            [
                new ShipmentContractAllocationInput
                {
                    ContractId = PlattsContract,
                    QuantityMt = 150m,
                    LoadingAllocations =
                    [
                        new ShipmentLoadingAllocationInput { LoadingRegisterId = 1, QuantityMt = 50m },
                        new ShipmentLoadingAllocationInput { LoadingRegisterId = 2, QuantityMt = 100m }
                    ]
                }
            ]
        });

        Assert.IsType<RedirectToActionResult>(result);
        var shipment = await db.Shipments.SingleAsync();
        Assert.Equal(2, await db.ShipmentLoadingAllocations.CountAsync(a => a.ShipmentId == shipment.Id));
        Assert.Equal(78000m, (await new ShipmentPurchaseCostService(db).BuildAsync(shipment.Id)).TotalPurchaseCostUsd);
    }

    [Fact]
    public async Task Create_Without_Loading_Allocations_Still_Works_As_Before()
    {
        await using var db = CreateDb();
        db.Contracts.Add(PurchaseContract(PlattsContract, PricingMethod.ManualFinalPrice, manualFinalPriceUsd: 600m));
        await db.SaveChangesAsync();

        var controller = BuildController(db);
        var result = await controller.Create(new ShipmentCreateViewModel
        {
            ShipmentCode = "LEGACY",
            ContractAllocations =
            [
                new ShipmentContractAllocationInput { ContractId = PlattsContract, QuantityMt = 50m }
            ]
        });

        Assert.IsType<RedirectToActionResult>(result);
        Assert.Empty(await db.ShipmentLoadingAllocations.ToListAsync());
        var shipment = await db.Shipments.SingleAsync();
        Assert.Equal(30000m, (await new ShipmentPurchaseCostService(db).BuildAsync(shipment.Id)).TotalPurchaseCostUsd);
    }

    // ===== endpoint ظرفیت بارگیری =====

    [Fact]
    public async Task LoadingAvailability_Reports_Remaining_And_Locked_Price()
    {
        await using var db = CreateDb();
        db.Contracts.Add(PurchaseContract(PlattsContract));
        db.LoadingRegisters.Add(Loading(1, PlattsContract, 100m, 500m));
        AddShipment(db, 26, PlattsContract, 30m, (1, 30m));
        await db.SaveChangesAsync();

        var json = Assert.IsType<JsonResult>(await BuildController(db).LoadingAvailability(PlattsContract));
        var model = Assert.IsType<ShipmentLoadingAvailabilityViewModel>(json.Value);

        var row = Assert.Single(model.Loadings);
        Assert.Equal(100m, row.LoadedQuantityMt);
        Assert.Equal(30m, row.AllocatedQuantityMt);
        Assert.Equal(70m, row.RemainingQuantityMt);
        Assert.Equal(500m, row.LoadingPriceUsd);
    }

    // ===== ۱۶. ساختار UI: کاربر فقط مقدار می‌دهد، نرخ خوانده می‌شود =====

    [Fact]
    public void Allocation_Forms_Collect_Quantity_Only_And_Never_Ask_For_A_Purchase_Price()
    {
        var partial = ReadRepoFile("src/PTGOilSystem.Web/Views/Shipments/_ShipmentLoadingAllocationRow.cshtml");
        var script = ReadRepoFile("src/PTGOilSystem.Web/wwwroot/js/shipment-loading-allocations.js");

        // ستون‌های خواستهٔ عملیات: بارگیری، تاریخ، مقدار، تخصیص‌یافته، باقی‌مانده، نرخ، سهم.
        Assert.Contains("سهم بارگیری‌ها", partial);
        Assert.Contains("باقی‌مانده", partial);
        Assert.Contains("نرخ قطعی خرید", partial);
        Assert.Contains("data-loading-body", partial);
        Assert.Contains("data-loading-total", partial);

        // نرخ فقط نمایش داده می‌شود؛ هیچ ورودیِ قیمت خرید در فرم محموله وجود ندارد.
        Assert.DoesNotContain("PurchaseUnitCostUsd", partial);
        Assert.DoesNotContain("LoadingPriceUsd\"", partial);
        Assert.Contains("data-loading-qty", script);
        Assert.Contains("LoadingAllocations[", script);

        foreach (var view in new[] { "Create", "Edit" })
        {
            var markup = ReadRepoFile($"src/PTGOilSystem.Web/Views/Shipments/{view}.cshtml");
            Assert.Contains("_ShipmentLoadingAllocationRow", markup);
            Assert.Contains("loading-allocation-row-template", markup);
            Assert.Contains("shipment-loading-allocations.js", markup);
            Assert.Contains("data-loading-allocations", markup);
            Assert.DoesNotContain("PurchaseUnitCostUsd", markup);
        }
    }

    private static string ReadRepoFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ptg-oil-system.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine(directory!.FullName, relativePath));
    }

    [Fact]
    public async Task LoadingAvailability_Frees_The_Current_Shipments_Own_Share()
    {
        await using var db = CreateDb();
        db.Contracts.Add(PurchaseContract(PlattsContract));
        db.LoadingRegisters.Add(Loading(1, PlattsContract, 100m, 500m));
        AddShipment(db, 27, PlattsContract, 30m, (1, 30m));
        await db.SaveChangesAsync();

        var json = Assert.IsType<JsonResult>(await BuildController(db).LoadingAvailability(PlattsContract, shipmentId: 27));
        var model = Assert.IsType<ShipmentLoadingAvailabilityViewModel>(json.Value);

        var row = Assert.Single(model.Loadings);
        // سهم خود همین محموله دوباره قابل تخصیص است تا ویرایش بدون خطای کاذب انجام شود.
        Assert.Equal(100m, row.RemainingQuantityMt);
        Assert.Equal(30m, row.CurrentShipmentQuantityMt);
    }
}
