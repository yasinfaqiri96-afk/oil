using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.InventoryTransport;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Accounting;
using PTGOilSystem.Web.Services.Exceptions;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// یک حملِ فیزیکی واحد که بارِ دو شرکت داخلی را با هم می‌برد. حمل تقسیم نمی‌شود و انتخابِ
/// منبع از چند شرکت ممنوع نیست؛ فقط مالکیتِ اقتصادیِ هر سهم باید در تمام مراحلِ بعدی —
/// رسید، کسری، زنجیرهٔ وسیله‌به‌وسیله و سود و زیان — سرِ جای خودش بماند.
///
/// قرارداد ۱ و ۳ مال شرکت A، قرارداد ۲ مال شرکت B (بر پایهٔ صحنهٔ مشترک زنجیرهٔ حمل).
/// </summary>
public class MultiCompanyTransportOwnershipTests
{
    private const int CompanyA = 1;
    private const int CompanyB = 2;

    // ---- زنجیرهٔ وسیله → وسیله ----

    // Split: فرزند فقط بخشی از بار والد را می‌برد، ولی همان نسبتِ مالکیت را با خود می‌برد.
    [Fact]
    public async Task Split_Carries_Company_Ownership_Into_The_Child_Leg()
    {
        await using var db = await BuildChainDbAsync();
        await TransportChainScenario.SeedLegAsync(db, legId: 1, LoadingTransportType.Wagon, quantityMt: 30m, contractId: 1);
        await TransportChainScenario.AddSourceAllocationAsync(db, legId: 1, contractId: 1, quantityMt: 10m);
        await TransportChainScenario.AddSourceAllocationAsync(db, legId: 1, contractId: 2, quantityMt: 20m);

        // ۱۲ تن از ۳۰ تن به موتر بعدی می‌رود: ۴ تن مال A و ۸ تن مال B.
        var result = await TransportChainScenario.Continue(db, sourceLegId: 1, LoadingTransportType.Truck, quantityMt: 12m);

        var slices = await ResolveAsync(db, result.ChildLeg);
        Assert.Equal(2, slices.Count);
        Assert.Equal(4m, slices.Single(x => x.CompanyId == CompanyA).QuantityMt);
        Assert.Equal(8m, slices.Single(x => x.CompanyId == CompanyB).QuantityMt);
        Assert.Equal(result.ChildLeg.QuantityMt, slices.Sum(x => x.QuantityMt));

        // باقیماندهٔ والد هم هنوز همان نسبت را دارد: ۶ تن A و ۱۲ تن B.
        var parent = await db.InventoryTransportLegs.SingleAsync(l => l.Id == 1);
        var parentSlices = await ResolveAsync(db, parent);
        Assert.Equal(10m, parentSlices.Single(x => x.CompanyId == CompanyA).QuantityMt);
        Assert.Equal(20m, parentSlices.Single(x => x.CompanyId == CompanyB).QuantityMt);
    }

    // Merge: دو والدِ متعلق به دو شرکت در یک موتر خالی می‌شوند. فرزند نباید شرکتِ قراردادِ
    // سرصفحهٔ والد اول را برای کل بار بگیرد.
    [Fact]
    public async Task Merge_Keeps_Both_Parent_Companies_In_The_Child_Leg()
    {
        await using var db = await BuildChainDbAsync();
        await TransportChainScenario.SeedLegAsync(db, legId: 1, LoadingTransportType.Wagon, quantityMt: 10m, contractId: 1);
        await TransportChainScenario.SeedLegAsync(db, legId: 2, LoadingTransportType.Wagon, quantityMt: 20m, contractId: 2);

        var merged = await TransportChainScenario.ContinueFrom(
            db,
            [new ContinueToVehicleSource(1, 10m), new ContinueToVehicleSource(2, 20m)],
            LoadingTransportType.Truck);

        Assert.Equal(30m, merged.ChildLeg.QuantityMt);

        var slices = await ResolveAsync(db, merged.ChildLeg);
        Assert.Equal(2, slices.Count);
        Assert.Equal(10m, slices.Single(x => x.CompanyId == CompanyA).QuantityMt);
        Assert.Equal(20m, slices.Single(x => x.CompanyId == CompanyB).QuantityMt);
        Assert.Equal(merged.ChildLeg.QuantityMt, slices.Sum(x => x.QuantityMt));
    }

    // ---- رسید و کسری ----

    // ۳۰ تن رفت (۱۰ از A و ۲۰ از B) و ۲۹٫۴ تن رسید. کسریِ ۰٫۶ تن باید به همان نسبت
    // بین دو شرکت تقسیم شود، و ورودیِ مقصد هم ۹٫۸ و ۱۹٫۶ باشد.
    [Fact]
    public async Task Receipt_Splits_Inbound_Stock_And_Shortage_By_Company_Share()
    {
        await using var db = await BuildChainDbAsync();
        await TransportChainScenario.SeedLegAsync(db, legId: 1, LoadingTransportType.Truck, quantityMt: 30m, contractId: 1);
        await TransportChainScenario.AddSourceAllocationAsync(db, legId: 1, contractId: 1, quantityMt: 10m);
        await TransportChainScenario.AddSourceAllocationAsync(db, legId: 1, contractId: 2, quantityMt: 20m);
        var movementsBefore = await db.InventoryMovements.CountAsync();

        var leg = await db.InventoryTransportLegs
            .Include(l => l.SourcePurchaseContract)
            .SingleAsync(l => l.Id == 1);

        await new InventoryTransportReceiptService(db, new CurrencyConversionService(new PricingService(db)))
            .ApplyAsync(
                new InventoryTransportReceiptCreateViewModel
                {
                    InventoryTransportLegId = leg.Id,
                    ReceiptDate = new DateTime(2026, 5, 10),
                    ReceivedQuantityMt = 29.4m,
                    ShortageQuantityMt = 0.6m,
                    ReceiptDestination = InventoryTransportReceiptDestination.ToInventory,
                    DestinationTerminalId = 2,
                    DestinationStorageTankId = 2
                },
                leg,
                saleConversion: null);

        // ورودیِ مقصد به تفکیک قرارداد — و پس به تفکیک شرکت.
        var inbound = await db.InventoryMovements
            .Where(m => m.Id > movementsBefore && m.Direction == MovementDirection.In && m.StorageTankId == 2)
            .GroupBy(m => m.ContractId)
            .Select(g => new { ContractId = g.Key, QuantityMt = g.Sum(m => m.QuantityMt) })
            .ToListAsync();
        Assert.Equal(9.8m, inbound.Single(x => x.ContractId == 1).QuantityMt);
        Assert.Equal(19.6m, inbound.Single(x => x.ContractId == 2).QuantityMt);

        // کسری هم به همان نسبت: ۰٫۲ برای A و ۰٫۴ برای B.
        var shortage = await db.LossEventSourceAllocations.AsNoTracking().ToListAsync();
        Assert.Equal(0.2m, shortage.Single(x => x.SourcePurchaseContractId == 1).QuantityMt);
        Assert.Equal(0.4m, shortage.Single(x => x.SourcePurchaseContractId == 2).QuantityMt);
        Assert.Equal(0.6m, shortage.Sum(x => x.QuantityMt));
    }

    // ---- سود و زیان ----

    // بهای خرید یک حملِ چندشرکتی باید میانگینِ وزنیِ قراردادهای واقعیِ منبع باشد، نه نرخِ
    // قراردادِ سرصفحه برای کل بار.
    [Fact]
    public async Task Pnl_Prices_A_Multi_Company_Leg_From_Its_Own_Source_Contracts()
    {
        await using var db = await BuildChainDbAsync();
        await SetContractFinalPriceAsync(db, contractId: 1, priceUsd: 600m);
        await SetContractFinalPriceAsync(db, contractId: 2, priceUsd: 300m);
        await TransportChainScenario.SeedLegAsync(db, legId: 1, LoadingTransportType.Truck, quantityMt: 30m, contractId: 1);
        await TransportChainScenario.AddSourceAllocationAsync(db, legId: 1, contractId: 1, quantityMt: 10m);
        await TransportChainScenario.AddSourceAllocationAsync(db, legId: 1, contractId: 2, quantityMt: 20m);

        var pnl = await new InventoryTransportPnlService(db).BuildForLegsAsync([1]);

        // (10 x 600 + 20 x 300) / 30 = 400 — نه ۶۰۰ که نرخِ قراردادِ سرصفحه است.
        Assert.Equal(400m, pnl[1].PurchaseUnitCostUsd);
        Assert.Equal("Source allocation weighted average", pnl[1].PurchaseCostSource);
    }

    // ---- مطالبهٔ کسری (بدهیِ جدا) ----

    // LedgerEntry ستون شرکت ندارد؛ انتساب شرکتیِ یک سطر فقط از ContractId خوانده می‌شود.
    // حملِ تک‌قراردادی باید دقیقاً همان یک سطرِ قبلی را با همان کلید یکتا بسازد.
    [Fact]
    public async Task Shortage_Debt_Of_A_Single_Company_Leg_Stays_One_Row_On_The_Header_Contract()
    {
        await using var db = await BuildChainDbAsync();
        await SeedCarrierAsync(db);
        await TransportChainScenario.SeedLegAsync(db, legId: 1, LoadingTransportType.Truck, quantityMt: 30m, contractId: 1);
        await TransportChainScenario.AddSourceAllocationAsync(db, legId: 1, contractId: 1, quantityMt: 30m);

        await ApplyShortageReceiptAsync(db, legId: 1, receivedMt: 29.4m, shortageMt: 0.6m, chargeUsd: 900m);

        var debt = Assert.Single(await ShortageDebtRowsAsync(db));
        Assert.Equal(900m, debt.AmountUsd);
        Assert.Equal(LedgerSide.Debit, debt.Side);
        Assert.Equal(1, debt.ContractId);
        Assert.Equal(1, debt.ServiceProviderId);
        Assert.Equal("TRANSPORT-SHORTAGE:1", debt.Reference);
    }

    // ۳۰ تن (۱۰ از A و ۲۰ از B) و کسری ۰٫۶ تن به ارزش ۹۰۰ دالر: A باید ۳۰۰ و B باید ۶۰۰
    // مطالبه بگیرد — نه اینکه کل ۹۰۰ روی قراردادِ سرصفحه (شرکت A) بنشیند.
    [Fact]
    public async Task Shortage_Debt_Of_A_Multi_Company_Leg_Splits_By_Company_Share()
    {
        await using var db = await BuildChainDbAsync();
        await SeedCarrierAsync(db);
        await TransportChainScenario.SeedLegAsync(db, legId: 1, LoadingTransportType.Truck, quantityMt: 30m, contractId: 1);
        await TransportChainScenario.AddSourceAllocationAsync(db, legId: 1, contractId: 1, quantityMt: 10m);
        await TransportChainScenario.AddSourceAllocationAsync(db, legId: 1, contractId: 2, quantityMt: 20m);

        await ApplyShortageReceiptAsync(db, legId: 1, receivedMt: 29.4m, shortageMt: 0.6m, chargeUsd: 900m);

        var rows = await ShortageDebtRowsAsync(db);
        Assert.Equal(2, rows.Count);

        // قرارداد ۱ مال شرکت A و قرارداد ۲ مال شرکت B.
        Assert.Equal(300m, rows.Single(l => l.ContractId == 1).AmountUsd);
        Assert.Equal(600m, rows.Single(l => l.ContractId == 2).AmountUsd);
        Assert.All(rows, row => Assert.Equal(LedgerSide.Debit, row.Side));
        Assert.All(rows, row => Assert.Equal(1, row.ServiceProviderId));

        // جمع سهم‌ها دقیقاً برابر مطالبهٔ کل — هیچ سنتی نه گم می‌شود نه دوبار مطالبه می‌شود.
        Assert.Equal(900m, rows.Sum(l => l.AmountUsd));

        // هر سطر کلید یکتای خودش را دارد.
        Assert.Equal(rows.Count, rows.Select(l => l.Reference).Distinct().Count());
    }

    // مقدارِ گِردنشدنی: ۱۰۰ دالر بین ۱۰ و ۲۰ تن. جمع سطرها باید هنوز دقیقاً ۱۰۰ بماند.
    [Fact]
    public async Task Shortage_Debt_Shares_Always_Add_Back_Up_To_The_Whole_Charge()
    {
        await using var db = await BuildChainDbAsync();
        await SeedCarrierAsync(db);
        await TransportChainScenario.SeedLegAsync(db, legId: 1, LoadingTransportType.Truck, quantityMt: 30m, contractId: 1);
        await TransportChainScenario.AddSourceAllocationAsync(db, legId: 1, contractId: 1, quantityMt: 10m);
        await TransportChainScenario.AddSourceAllocationAsync(db, legId: 1, contractId: 2, quantityMt: 20m);

        await ApplyShortageReceiptAsync(db, legId: 1, receivedMt: 29.4m, shortageMt: 0.6m, chargeUsd: 100m);

        var rows = await ShortageDebtRowsAsync(db);
        Assert.Equal(100m, rows.Sum(l => l.AmountUsd));
        Assert.Equal(2, rows.Count);
    }

    // retry روی همان رسید نباید مطالبه را دوباره بسازد.
    [Fact]
    public async Task Shortage_Debt_Is_Not_Written_Twice_For_The_Same_Receipt()
    {
        await using var db = await BuildChainDbAsync();
        await SeedCarrierAsync(db);
        await TransportChainScenario.SeedLegAsync(db, legId: 1, LoadingTransportType.Truck, quantityMt: 30m, contractId: 1);
        await TransportChainScenario.AddSourceAllocationAsync(db, legId: 1, contractId: 1, quantityMt: 10m);
        await TransportChainScenario.AddSourceAllocationAsync(db, legId: 1, contractId: 2, quantityMt: 20m);

        var receipt = await ApplyShortageReceiptAsync(db, legId: 1, receivedMt: 29.4m, shortageMt: 0.6m, chargeUsd: 900m);
        Assert.Equal(2, (await ShortageDebtRowsAsync(db)).Count);

        // همان رسید دوباره sync می‌شود (مسیر retry): هیچ سطر تازه‌ای نباید اضافه شود.
        var leg = await db.InventoryTransportLegs.SingleAsync(l => l.Id == 1);
        await ReSyncShortageDebtAsync(db, receipt, leg);

        var rows = await ShortageDebtRowsAsync(db);
        Assert.Equal(2, rows.Count);
        Assert.Equal(900m, rows.Sum(l => l.AmountUsd));
    }

    // ---- گاردِ مسیر بارگیریِ تک‌قراردادی ----

    // مسیر standalone یک حرکت خروجی برای کل مقدار با قراردادِ سرصفحه می‌سازد؛ حملِ تک‌قراردادی
    // (با یا بدون سهمِ منبعِ هم‌قرارداد) باید مثل قبل بارگیری شود.
    [Fact]
    public async Task Standalone_Single_Contract_Load_Still_Posts_Its_Outbound_Movement()
    {
        await using var db = await BuildChainDbAsync();
        await SeedDraftLegAsync(db, legId: 1, contractId: 1, quantityMt: 10m);
        db.InventoryMovements.Add(new InventoryMovement
        {
            ProductId = 1,
            ContractId = 1,
            TerminalId = 1,
            StorageTankId = 1,
            Direction = MovementDirection.In,
            MovementDate = new DateTime(2026, 4, 20),
            QuantityMt = 10m,
            ReferenceDocument = "SEED-IN:1"
        });
        await db.SaveChangesAsync();

        var leg = await LoadDraftLegAsync(db, legId: 1);
        await new InventoryTransportLegLoadService(db, new StockService(db)).LoadAsync(leg);

        Assert.Equal(InventoryTransportLegStatus.Loaded, leg.Status);
        Assert.NotNull(leg.OutboundInventoryMovementId);
        var movement = await db.InventoryMovements.SingleAsync(m => m.Id == leg.OutboundInventoryMovementId);
        Assert.Equal(MovementDirection.Out, movement.Direction);
        Assert.Equal(1, movement.ContractId);
        Assert.Equal(10m, movement.QuantityMt);
    }

    // حملی که سهم‌های چندقراردادی دارد نباید بی‌صدا یک حرکت خروجیِ واحد با قراردادِ سرصفحه
    // بسازد؛ مسیر allocation-aware خودش را دارد.
    [Fact]
    public async Task Standalone_Load_Path_Rejects_A_Multi_Allocation_Leg()
    {
        await using var db = await BuildChainDbAsync();
        await SeedDraftLegAsync(db, legId: 1, contractId: 1, quantityMt: 30m);
        db.InventoryTransportLegAllocations.AddRange(
            new InventoryTransportLegAllocation { InventoryTransportLegId = 1, SourcePurchaseContractId = 1, QuantityMt = 10m },
            new InventoryTransportLegAllocation { InventoryTransportLegId = 1, SourcePurchaseContractId = 2, QuantityMt = 20m });
        await db.SaveChangesAsync();

        var leg = await LoadDraftLegAsync(db, legId: 1);
        var service = new InventoryTransportLegLoadService(db, new StockService(db));

        var error = await Assert.ThrowsAsync<BusinessRuleException>(() => service.LoadAsync(leg));
        Assert.Equal("TRANSPORT_LEG_MULTI_ALLOCATION_LOAD_BLOCKED", error.Code);

        // هیچ سند/وضعیتی نوشته نشده است.
        Assert.Equal(InventoryTransportLegStatus.Draft, leg.Status);
        Assert.Null(leg.OutboundInventoryMovementId);
        Assert.Empty(await db.InventoryMovements.Where(m => m.Direction == MovementDirection.Out).ToListAsync());
    }

    // ---- کمک‌کننده‌ها ----

    private static async Task<InventoryTransportReceipt> ApplyShortageReceiptAsync(
        ApplicationDbContext db,
        int legId,
        decimal receivedMt,
        decimal shortageMt,
        decimal chargeUsd)
    {
        var leg = await db.InventoryTransportLegs
            .Include(l => l.SourcePurchaseContract)
            .SingleAsync(l => l.Id == legId);

        return await new InventoryTransportReceiptService(db, new CurrencyConversionService(new PricingService(db)))
            .ApplyAsync(
                new InventoryTransportReceiptCreateViewModel
                {
                    InventoryTransportLegId = leg.Id,
                    ReceiptDate = new DateTime(2026, 5, 10),
                    ReceivedQuantityMt = receivedMt,
                    ShortageQuantityMt = shortageMt,
                    ChargeableShortageMt = shortageMt,
                    ShortageChargeUsd = chargeUsd,
                    ShortageAsSeparateDebt = true,
                    ServiceProviderId = 1,
                    ReceiptDestination = InventoryTransportReceiptDestination.ToInventory,
                    DestinationTerminalId = 2,
                    DestinationStorageTankId = 2
                },
                leg,
                saleConversion: null);
    }

    // مسیر retry: همان رسیدِ ذخیره‌شده دوباره از راه ApplyAsync نمی‌آید، ولی هر فراخوانِ
    // دوبارهٔ همگام‌سازی نباید سطر تازه بسازد.
    private static async Task ReSyncShortageDebtAsync(
        ApplicationDbContext db,
        InventoryTransportReceipt receipt,
        InventoryTransportLeg leg)
    {
        var method = typeof(InventoryTransportReceiptService).GetMethod(
            "SyncShortageDebtAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var service = new InventoryTransportReceiptService(db, new CurrencyConversionService(new PricingService(db)));
        await (Task)method.Invoke(service, [receipt, leg, true])!;
    }

    private static async Task<IReadOnlyList<LedgerEntry>> ShortageDebtRowsAsync(ApplicationDbContext db)
        => await db.LedgerEntries
            .AsNoTracking()
            .Where(l => l.SourceType == "ShortageCharge")
            .OrderBy(l => l.Id)
            .ToListAsync();

    private static async Task SeedCarrierAsync(ApplicationDbContext db)
    {
        db.ServiceProviders.Add(new ServiceProvider { Id = 1, Name = "Carrier", IsActive = true });
        await db.SaveChangesAsync();
    }

    private static async Task SeedDraftLegAsync(
        ApplicationDbContext db,
        int legId,
        int contractId,
        decimal quantityMt)
    {
        db.InventoryTransportLegs.Add(new InventoryTransportLeg
        {
            Id = legId,
            SourcePurchaseContractId = contractId,
            ProductId = 1,
            SourceTerminalId = 1,
            SourceStorageTankId = 1,
            TransportType = LoadingTransportType.Truck,
            TruckId = 1,
            LoadedDate = new DateTime(2026, 5, 1),
            QuantityMt = quantityMt,
            Status = InventoryTransportLegStatus.Draft
        });
        await db.SaveChangesAsync();
    }

    private static Task<InventoryTransportLeg> LoadDraftLegAsync(ApplicationDbContext db, int legId)
        => db.InventoryTransportLegs
            .Include(l => l.SourcePurchaseContract)
            .Include(l => l.SourceStorageTank)
            .SingleAsync(l => l.Id == legId);

    private static Task<IReadOnlyList<LegCompanyOwnershipSlice>> ResolveAsync(
        ApplicationDbContext db,
        InventoryTransportLeg leg)
        => new InventoryTransportLegOwnershipResolver(db).ResolveCompanyOwnershipSlicesAsync(leg);

    // صحنهٔ مشترک زنجیرهٔ حمل، به‌اضافهٔ شرکت دوم و نسبت‌دادن قراردادها به شرکت‌ها.
    private static async Task<ApplicationDbContext> BuildChainDbAsync()
    {
        var db = TransportChainScenario.BuildDb();
        await TransportChainScenario.SeedAsync(db);

        db.Companies.Add(new Company { Id = CompanyB, Code = "B", Name = "Company B", IsActive = true });
        foreach (var contract in await db.Contracts.ToListAsync())
            contract.CompanyId = contract.Id == 2 ? CompanyB : CompanyA;
        await db.SaveChangesAsync();
        return db;
    }

    private static async Task SetContractFinalPriceAsync(ApplicationDbContext db, int contractId, decimal priceUsd)
    {
        var contract = await db.Contracts.SingleAsync(c => c.Id == contractId);
        contract.PricingMethod = PricingMethod.ManualFinalPrice;
        contract.ManualFinalPriceUsd = priceUsd;
        await db.SaveChangesAsync();
    }
}
