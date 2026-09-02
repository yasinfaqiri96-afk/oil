using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PTGOilSystem.Web.Controllers;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Dispatch;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.InventoryTransport;
using PTGOilSystem.Web.Models.OperationalAssets;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Audit;
using PTGOilSystem.Web.Services.Time;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// P0 پروندهٔ دارایی: تفکیک «کارکرد / مصارف / عواید»، بازهٔ پیش‌فرض دوازده‌ماهه، لینک سند منبع،
/// و کنترل سازگاری موتر با دارایی عملیاتی در مسیرهای تکیِ دیسپچ و حمل.
/// </summary>
public class OperationalAssetProfileP0Tests
{
    [Fact]
    public async Task Profile_Splits_Internal_Use_External_Rent_And_Costs_Into_Separate_Lists()
    {
        await using var db = CreateDb();
        SeedAsset(db);
        db.ExpenseTypes.Add(new ExpenseType
        {
            Id = 2,
            Code = "TRANSPORT-FREIGHT",
            Name = "Transport Freight",
            NamePersian = "کرایه حمل",
            Category = "Transport",
            IsActive = true
        });
        db.AssetRentTransactions.AddRange(
            new AssetRentTransaction
            {
                Id = 1,
                OperationalAssetId = 1,
                LoadingRegisterId = 42,
                RentDate = new DateTime(2026, 5, 5),
                UsageType = AssetRentUsageType.InternalCompanyUse,
                ChargedToType = AssetRentChargedToType.PurchaseContract,
                ChargedToContractId = 1,
                QuantityMt = 30m,
                Rate = 4m,
                Currency = "USD",
                FxRateToUsd = 1m,
                AmountOriginal = 120m,
                AmountUsd = 120m
            },
            new AssetRentTransaction
            {
                Id = 2,
                OperationalAssetId = 1,
                RentDate = new DateTime(2026, 5, 6),
                UsageType = AssetRentUsageType.ExternalCustomerRental,
                ChargedToType = AssetRentChargedToType.Customer,
                ChargedToCustomerId = 1,
                Rate = 70m,
                Currency = "USD",
                FxRateToUsd = 1m,
                AmountOriginal = 70m,
                AmountUsd = 70m
            });
        db.ExpenseTransactions.AddRange(
            new ExpenseTransaction
            {
                Id = 1,
                ExpenseTypeId = 2,
                OperationalAssetId = 1,
                TruckDispatchId = 7,
                ContractId = 1,
                ExpenseDate = new DateTime(2026, 5, 7),
                Amount = 500m,
                Currency = "USD",
                AmountUsd = 500m,
                Description = "Freight income for operational asset"
            },
            new ExpenseTransaction
            {
                Id = 2,
                ExpenseTypeId = 1,
                OperationalAssetId = 1,
                ExpenseDate = new DateTime(2026, 5, 8),
                Amount = 25m,
                Currency = "USD",
                AmountUsd = 25m,
                Description = "Fuel"
            });
        await db.SaveChangesAsync();
        var controller = BuildController(db);

        var result = await controller.Details(1, new DateTime(2026, 5, 1), new DateTime(2026, 5, 30));

        var model = Assert.IsType<OperationalAssetProfileViewModel>(Assert.IsType<ViewResult>(result).Model);

        // مصارف فقط هزینه است: ردیف عایداتی کرایهٔ حمل داخل این لیست نمی‌آید.
        var cost = Assert.Single(model.CostRows);
        Assert.Equal(2, cost.Id);
        Assert.False(cost.IsFreightIncome);

        // عواید داخلی = کرایهٔ خودکار بارگیری + کرایهٔ حملی که با وسیلهٔ خود شرکت انجام شده.
        Assert.Equal(2, model.InternalIncomeRows.Count);
        Assert.Equal(620m, model.InternalIncomeRows.Sum(row => row.AmountUsd));
        Assert.All(model.InternalIncomeRows, row => Assert.False(row.NeedsAttention));

        // عواید بیرونی فقط کرایهٔ ثبت‌شده برای مشتری بیرونی است و طرف حساب دارد.
        var external = Assert.Single(model.ExternalIncomeRows);
        Assert.Equal(70m, external.AmountUsd);
        Assert.Equal("Customer A", external.CounterpartyName);
        Assert.True(external.CanCancel);

        // کارکرد هر سه عملیات را نشان می‌دهد و هیچ مبلغی در آن نیست.
        Assert.Equal(3, model.WorkRows.Count);
        Assert.Contains(model.WorkRows, row => row.IsInternalUse && row.QuantityMt == 30m);
        Assert.Contains(model.WorkRows, row => !row.IsInternalUse);
    }

    [Fact]
    public async Task System_Generated_Rows_Carry_A_Live_Link_To_Their_Source_Document()
    {
        await using var db = CreateDb();
        SeedAsset(db);
        db.AssetRentTransactions.Add(new AssetRentTransaction
        {
            Id = 1,
            OperationalAssetId = 1,
            LoadingRegisterId = 42,
            RentDate = new DateTime(2026, 5, 5),
            UsageType = AssetRentUsageType.InternalCompanyUse,
            ChargedToType = AssetRentChargedToType.PurchaseContract,
            ChargedToContractId = 1,
            Rate = 120m,
            Currency = "USD",
            FxRateToUsd = 1m,
            AmountOriginal = 120m,
            AmountUsd = 120m
        });
        await db.SaveChangesAsync();
        var controller = BuildController(db);

        var result = await controller.Details(1, new DateTime(2026, 5, 1), new DateTime(2026, 5, 30));

        var model = Assert.IsType<OperationalAssetProfileViewModel>(Assert.IsType<ViewResult>(result).Model);
        var income = Assert.Single(model.InternalIncomeRows);
        Assert.NotNull(income.Source);
        Assert.Equal(42, income.Source!.DocumentId);
        Assert.Contains("#42", income.Source.Label);
        Assert.False(string.IsNullOrWhiteSpace(income.Source.DocumentTypeName));
        Assert.Equal($"/Loading/Details/42", income.Source.Url);
        // ردیف خودکار از سند خودش لغو می‌شود، نه از پروندهٔ دارایی.
        Assert.False(income.CanCancel);
        // وضعیت به زبان کاربر است، نه کد سیاست ثبت.
        Assert.DoesNotContain("SYSTEM_GENERATED", income.StateText);
        Assert.Contains("no outside payment", income.StateText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Profile_Without_Explicit_Dates_Shows_The_Last_Twelve_Months()
    {
        await using var db = CreateDb();
        SeedAsset(db);
        await db.SaveChangesAsync();
        var controller = BuildController(db);

        var result = await controller.Details(1);

        var model = Assert.IsType<OperationalAssetProfileViewModel>(Assert.IsType<ViewResult>(result).Model);
        var expectedFrom = AfghanistanBusinessClock.SystemToday.AddMonths(-12).Date;
        Assert.Equal(expectedFrom, model.FromDate.Date);
        Assert.Equal(AfghanistanBusinessClock.SystemToday.Date, model.ToDate.Date);
    }

    [Fact]
    public async Task Single_Dispatch_Blocks_An_Asset_That_Belongs_To_Another_Truck()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        db.Products.Add(new Product { Id = 1, Code = "GO", Name = "Gas Oil" });
        db.Terminals.Add(new Terminal { Id = 1, Code = "T1", Name = "Terminal 1" });
        db.StorageTanks.Add(new StorageTank { Id = 1, TerminalId = 1, TankCode = "TK-1", ProductId = 1, CapacityMt = 500m });
        db.Trucks.AddRange(
            new Truck { Id = 1, PlateNumber = "AFG-101", IsActive = true },
            new Truck { Id = 2, PlateNumber = "AFG-202", IsActive = true });
        db.Locations.Add(new Location { Id = 1, Name = "Kabul Depot" });
        db.Contracts.Add(new Contract
        {
            Id = 1,
            ContractNumber = "CTR-1",
            ContractType = ContractType.Purchase,
            ProductId = 1,
            ContractDate = new DateTime(2026, 4, 23),
            QuantityMt = 500m
        });
        db.OperationalAssets.Add(new OperationalAsset
        {
            Id = 1,
            AssetCode = "TRK-OWN-2",
            Name = "Owned Truck 2",
            AssetType = OperationalAssetType.Truck,
            LinkedTruckId = 2,
            IsActive = true
        });
        db.InventoryMovements.Add(new InventoryMovement
        {
            ProductId = 1,
            ContractId = 1,
            TerminalId = 1,
            StorageTankId = 1,
            Direction = MovementDirection.In,
            MovementDate = new DateTime(2026, 4, 20),
            QuantityMt = 100m,
            ReferenceDocument = "GRN-1"
        });
        await db.SaveChangesAsync();

        var controller = new DispatchController(
            db,
            new StockService(db),
            new AuditService(db),
            NullLogger<DispatchController>.Instance)
        {
            TempData = BuildTempData()
        };

        var result = await controller.Create(new DispatchCreateViewModel
        {
            ContractId = 1,
            ProductId = 1,
            TruckId = 1,
            SourceTerminalId = 1,
            SourceStorageTankId = 1,
            DestinationLocationId = 1,
            OperationalAssetId = 1,
            DispatchDate = new DateTime(2026, 4, 23),
            LoadedQuantityMt = 25m
        });

        Assert.IsType<ViewResult>(result);
        Assert.False(controller.ModelState.IsValid);
        var error = Assert.Single(controller.ModelState[nameof(DispatchCreateViewModel.OperationalAssetId)]!.Errors);
        Assert.Contains("AFG-202", error.ErrorMessage);
        Assert.Contains("AFG-101", error.ErrorMessage);
        // ذخیره نشده و مقدار خودکار اصلاح نشده است.
        Assert.Empty(await db.TruckDispatches.ToListAsync());
    }

    [Fact]
    public async Task Single_Transport_Leg_Blocks_An_Asset_That_Belongs_To_Another_Truck()
    {
        await using var db = CreateDb();
        db.Products.Add(new Product { Id = 1, Code = "GO", Name = "Gas Oil", IsActive = true });
        db.Terminals.Add(new Terminal { Id = 1, Code = "T1", Name = "Terminal 1", IsActive = true });
        db.Trucks.AddRange(
            new Truck { Id = 1, PlateNumber = "AFG-101", IsActive = true },
            new Truck { Id = 2, PlateNumber = "AFG-202", IsActive = true });
        db.Contracts.Add(new Contract
        {
            Id = 1,
            ContractNumber = "PUR-001",
            ContractType = ContractType.Purchase,
            ProductId = 1,
            ContractDate = new DateTime(2026, 5, 1),
            QuantityMt = 100m
        });
        db.OperationalAssets.Add(new OperationalAsset
        {
            Id = 1,
            AssetCode = "TRK-OWN-2",
            Name = "Owned Truck 2",
            AssetType = OperationalAssetType.Truck,
            LinkedTruckId = 2,
            IsActive = true
        });
        db.InventoryTransportLegs.Add(new InventoryTransportLeg
        {
            Id = 1,
            SourcePurchaseContractId = 1,
            ProductId = 1,
            SourceTerminalId = 1,
            TransportType = LoadingTransportType.Truck,
            TruckId = 1,
            LoadedDate = new DateTime(2026, 5, 2),
            QuantityMt = 20m,
            Status = InventoryTransportLegStatus.Draft
        });
        await db.SaveChangesAsync();

        var controller = new InventoryTransportLegsController(db, new StockService(db))
        {
            TempData = BuildTempData()
        };

        var result = await controller.Edit(1, new InventoryTransportLegCreateViewModel
        {
            Id = 1,
            SourcePurchaseContractId = 1,
            ProductId = 1,
            SourceTerminalId = 1,
            TransportType = LoadingTransportType.Truck,
            OperationalAssetId = 1,
            LoadedDate = new DateTime(2026, 5, 2),
            QuantityMt = 20m
        });

        Assert.IsType<ViewResult>(result);
        Assert.False(controller.ModelState.IsValid);
        var error = Assert.Single(controller.ModelState[nameof(InventoryTransportLegCreateViewModel.OperationalAssetId)]!.Errors);
        Assert.Contains("AFG-202", error.ErrorMessage);
        Assert.Contains("AFG-101", error.ErrorMessage);
        var leg = await db.InventoryTransportLegs.AsNoTracking().SingleAsync();
        Assert.Null(leg.OperationalAssetId);
    }

    private static OperationalAssetsController BuildController(ApplicationDbContext db)
        => new(db)
        {
            TempData = BuildTempData(),
            Url = new StubUrlHelper()
        };

    /// <summary>آدرس‌ساز سادهٔ تست: همان شکلی که مسیرهای پیش‌فرض MVC می‌سازند.</summary>
    private sealed class StubUrlHelper : IUrlHelper
    {
        public ActionContext ActionContext { get; } = new();

        public string? Action(UrlActionContext actionContext)
        {
            var id = actionContext.Values?.GetType().GetProperty("id")?.GetValue(actionContext.Values);
            return $"/{actionContext.Controller}/{actionContext.Action}/{id}";
        }

        public string? Content(string? contentPath) => contentPath;

        public bool IsLocalUrl(string? url) => !string.IsNullOrEmpty(url) && url.StartsWith('/');

        public string? Link(string? routeName, object? values) => "/";

        public string? RouteUrl(UrlRouteContext routeContext) => "/";
    }

    private static ApplicationDbContext CreateDb()
        => new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static void SeedAsset(ApplicationDbContext db)
    {
        db.Currencies.Add(new Currency { Id = 1, Code = "USD", Name = "US Dollar", Symbol = "$", IsActive = true });
        db.Companies.Add(new Company { Id = 1, Code = "PTG", Name = "PTG", IsActive = true });
        db.Customers.Add(new Customer { Id = 1, Name = "Customer A", IsActive = true });
        db.Products.Add(new Product { Id = 1, Code = "GO", Name = "Gas Oil", IsActive = true });
        db.ExpenseTypes.Add(new ExpenseType { Id = 1, Code = "FUEL", Name = "Fuel", IsActive = true });
        db.Trucks.Add(new Truck { Id = 1, PlateNumber = "AFG-101", IsActive = true });
        db.Contracts.Add(new Contract
        {
            Id = 1,
            ContractNumber = "PUR-001",
            ContractType = ContractType.Purchase,
            CompanyId = 1,
            ProductId = 1,
            ContractDate = new DateTime(2026, 5, 1),
            QuantityMt = 100m,
            PricingMethod = PricingMethod.Fixed,
            UnitPriceUsd = 100m
        });
        db.OperationalAssets.Add(new OperationalAsset
        {
            Id = 1,
            AssetCode = "TRK-OWN-1",
            Name = "Owned Truck 1",
            AssetType = OperationalAssetType.Truck,
            LinkedTruckId = 1,
            OwnershipMode = OperationalAssetOwnershipMode.FullyCompanyOwned,
            MonthlyDepreciationUsd = 300m,
            IsActive = true
        });
        db.AssetOwnershipShares.Add(new AssetOwnershipShare
        {
            OperationalAssetId = 1,
            OwnerType = AssetOwnerType.Company,
            CompanyId = 1,
            SharePercent = 100m,
            EffectiveFrom = new DateTime(2026, 1, 1)
        });
    }

    private static TempDataDictionary BuildTempData()
        => new(new DefaultHttpContext(), new NullTempDataProvider());

    private sealed class NullTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();

        public void SaveTempData(HttpContext context, IDictionary<string, object> values)
        {
        }
    }
}
