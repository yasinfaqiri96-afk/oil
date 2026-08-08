using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PTGOilSystem.Web.Controllers;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.Expenses;
using PTGOilSystem.Web.Models.OperationalAssets;
using PTGOilSystem.Web.Models.Reconciliation;
using PTGOilSystem.Web.Models.Reports;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Audit;
using Xunit;

namespace PTGOilSystem.Web.Tests;

public class OperationalAssetsControllerTests
{
    [Fact]
    public async Task Create_Post_Persists_OperationalAsset_With_Truck_Link()
    {
        await using var db = CreateDb();
        SeedReferenceData(db);
        await db.SaveChangesAsync();
        var controller = BuildOperationalAssetsController(db);

        var result = await controller.Create(new OperationalAssetFormViewModel
        {
            AssetCode = "TRK-OWN-1",
            Name = "Owned Truck 1",
            AssetType = OperationalAssetType.Truck,
            LinkedTruckId = 1,
            OwnershipMode = OperationalAssetOwnershipMode.SharedOwnership,
            MonthlyDepreciationUsd = 300m,
            DefaultInternalRateUsd = 25m,
            IsActive = true
        });

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Details", redirect.ActionName);
        var asset = await db.OperationalAssets.SingleAsync();
        Assert.Equal("TRK-OWN-1", asset.AssetCode);
        Assert.Equal(1, asset.LinkedTruckId);
        Assert.Equal(300m, asset.MonthlyDepreciationUsd);
    }

    [Fact]
    public async Task CreateRent_Post_Creates_Internal_Rent_Share_Snapshots_Without_Ledger_Or_InventoryMovement()
    {
        await using var db = CreateDb();
        SeedReferenceData(db);
        SeedAssetWithOwnership(db, companyShare: 60m, partnerShare: 40m);
        await db.SaveChangesAsync();
        var controller = BuildOperationalAssetsController(db);

        var result = await controller.CreateRent(new AssetRentCreateViewModel
        {
            OperationalAssetId = 1,
            RentDate = new DateTime(2026, 5, 10),
            UsageType = AssetRentUsageType.InternalCompanyUse,
            ChargedToType = AssetRentChargedToType.PurchaseContract,
            ChargedToContractId = 1,
            DistanceKm = 10m,
            Rate = 12m,
            Currency = "USD",
            FxRateToUsd = 1m,
            ReferenceDocument = "INT-RENT-1"
        });

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Details", redirect.ActionName);

        var rent = await db.AssetRentTransactions.SingleAsync();
        Assert.Equal(DateTimeKind.Utc, rent.RentDate.Kind);
        Assert.Equal(120m, rent.AmountUsd);
        Assert.False(rent.IsPostedToLedger);
        Assert.Null(rent.LedgerEntryId);
        Assert.Empty(await db.LedgerEntries.ToListAsync());
        Assert.Empty(await db.InventoryMovements.ToListAsync());

        var shares = await db.AssetRentShares.OrderBy(s => s.SharePercent).ToListAsync();
        Assert.Equal(2, shares.Count);
        Assert.Equal(40m, shares[0].SharePercent);
        Assert.Equal(48m, shares[0].ShareAmountUsd);
        Assert.Equal(60m, shares[1].SharePercent);
        Assert.Equal(72m, shares[1].ShareAmountUsd);
    }

    [Fact]
    public async Task CreateRent_Post_Creates_New_External_Customer_When_Name_Is_Entered()
    {
        await using var db = CreateDb();
        SeedReferenceData(db);
        SeedAssetWithOwnership(db, companyShare: 100m, partnerShare: 0m);
        await db.SaveChangesAsync();
        var controller = BuildOperationalAssetsController(db);

        var result = await controller.CreateRent(new AssetRentCreateViewModel
        {
            OperationalAssetId = 1,
            RentDate = new DateTime(2026, 5, 10),
            UsageType = AssetRentUsageType.ExternalCustomerRental,
            ChargedToType = AssetRentChargedToType.Customer,
            NewCustomerName = "External Rent Customer",
            Days = 1m,
            Rate = 250m,
            Currency = "USD",
            FxRateToUsd = 1m,
            ReferenceDocument = "EXT-RENT-NEW"
        });

        Assert.IsType<RedirectToActionResult>(result);

        var customer = await db.Customers.SingleAsync(c => c.Name == "External Rent Customer");
        var rent = await db.AssetRentTransactions.SingleAsync(r => r.ReferenceDocument == "EXT-RENT-NEW");
        Assert.Equal(customer.Id, rent.ChargedToCustomerId);
        Assert.Equal(250m, rent.AmountUsd);

        // کرایهٔ دستیِ خارجی از فاز یک به بعد حساب مشتری را بدهکار می‌کند: یک ردیف Credit روی همان
        // مشتریِ تازه‌ساخته‌شده، با ردیابی دوطرفه به خودِ تراکنش. جزئیات این رفتار در
        // AssetRentPostingTests پوشش داده شده و اینجا فقط اتصالِ مشتریِ جدید بررسی می‌شود.
        var ledger = Assert.Single(await db.LedgerEntries.ToListAsync());
        Assert.Equal(LedgerSide.Credit, ledger.Side);
        Assert.Equal(250m, ledger.AmountUsd);
        Assert.Equal(customer.Id, ledger.CustomerId);
        Assert.Equal(rent.Id, ledger.SourceId);
        Assert.True(rent.IsPostedToLedger);
        Assert.Equal(ledger.Id, rent.LedgerEntryId);

        Assert.Empty(await db.InventoryMovements.ToListAsync());
    }

    [Fact]
    public async Task CreateRent_Post_Rejects_When_Active_Ownership_Is_Not_100_Percent()
    {
        await using var db = CreateDb();
        SeedReferenceData(db);
        SeedAssetWithOwnership(db, companyShare: 60m, partnerShare: 0m);
        await db.SaveChangesAsync();
        var controller = BuildOperationalAssetsController(db);

        var result = await controller.CreateRent(new AssetRentCreateViewModel
        {
            OperationalAssetId = 1,
            RentDate = new DateTime(2026, 5, 10),
            UsageType = AssetRentUsageType.InternalCompanyUse,
            ChargedToType = AssetRentChargedToType.PurchaseContract,
            ChargedToContractId = 1,
            Rate = 100m,
            Currency = "USD",
            FxRateToUsd = 1m
        });

        Assert.IsType<ViewResult>(result);
        Assert.False(controller.ModelState.IsValid);
        Assert.Empty(await db.AssetRentTransactions.ToListAsync());
    }

    [Fact]
    public async Task Details_Treats_Transport_Freight_Expense_On_Company_Asset_As_Revenue()
    {
        await using var db = CreateDb();
        SeedReferenceData(db);
        SeedAssetWithOwnership(db, companyShare: 100m, partnerShare: 0m);
        // نوع مصرف کرایهٔ حمل (دستهٔ Transport) — همان نوعی که در Loading/InventoryTransport برای کرایه استفاده می‌شود.
        db.ExpenseTypes.Add(new ExpenseType
        {
            Id = 2,
            Code = "TRANSPORT-FREIGHT",
            Name = "Transport Freight",
            NamePersian = "کرایه حمل",
            Category = "Transport",
            IsActive = true
        });
        db.ExpenseTransactions.AddRange(
            // کرایهٔ حمل با موتر شرکت: برای قرارداد هزینه است، اما برای دارایی باید عواید باشد.
            new ExpenseTransaction
            {
                Id = 1,
                ExpenseTypeId = 2,
                OperationalAssetId = 1,
                ContractId = 1,
                ExpenseDate = new DateTime(2026, 5, 7),
                Amount = 500m,
                Currency = "USD",
                AmountUsd = 500m,
                Description = "کرایه حمل با موتر شرکت"
            },
            // مصرف واقعی دارایی (تیل/ترمیم) باید هزینه بماند.
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
        var controller = BuildOperationalAssetsController(db);

        var result = await controller.Details(1, new DateTime(2026, 5, 1), new DateTime(2026, 5, 30));

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<OperationalAssetProfileViewModel>(view.Model);
        // کرایه = درآمد دارایی؛ تیل = هزینه.
        Assert.Equal(500m, model.FreightIncomeUsd);
        Assert.Equal(25m, model.DirectExpensesUsd);
        // P&L دارایی مثبت: 500 درآمد − 25 هزینه − 300 استهلاک = 175.
        Assert.Equal(175m, model.NetResultUsd);
        Assert.True(model.NetResultUsd > 0);
        // ردیف کرایه به‌عنوان درآمد علامت‌گذاری شده باشد.
        var freightRow = Assert.Single(model.Expenses, e => e.Id == 1);
        Assert.True(freightRow.IsFreightIncome);
        // رکورد مصرف دست‌نخورده می‌ماند؛ پس P&L قرارداد همچنان آن را هزینه می‌بیند.
        var rawExpense = await db.ExpenseTransactions.SingleAsync(e => e.Id == 1);
        Assert.Equal(500m, rawExpense.AmountUsd);
        Assert.Equal(1, rawExpense.ContractId);
    }

    [Fact]
    public async Task Details_Calculates_Rent_Expenses_Depreciation_And_Excludes_Cancelled_Expenses()
    {
        await using var db = CreateDb();
        SeedReferenceData(db);
        SeedAssetWithOwnership(db, companyShare: 100m, partnerShare: 0m);
        db.AssetRentTransactions.AddRange(
            new AssetRentTransaction
            {
                Id = 1,
                OperationalAssetId = 1,
                RentDate = new DateTime(2026, 5, 5),
                UsageType = AssetRentUsageType.InternalCompanyUse,
                ChargedToType = AssetRentChargedToType.PurchaseContract,
                ChargedToContractId = 1,
                Rate = 100m,
                Currency = "USD",
                FxRateToUsd = 1m,
                AmountOriginal = 100m,
                AmountUsd = 100m
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
                ExpenseTypeId = 1,
                OperationalAssetId = 1,
                ExpenseDate = new DateTime(2026, 5, 7),
                Amount = 25m,
                Currency = "USD",
                AmountUsd = 25m,
                Description = "Fuel"
            },
            new ExpenseTransaction
            {
                Id = 2,
                ExpenseTypeId = 1,
                OperationalAssetId = 1,
                ExpenseDate = new DateTime(2026, 5, 8),
                Amount = 99m,
                Currency = "USD",
                AmountUsd = 99m,
                Description = "Cancelled",
                IsCancelled = true
            });
        await db.SaveChangesAsync();
        var controller = BuildOperationalAssetsController(db);

        var result = await controller.Details(1, new DateTime(2026, 5, 1), new DateTime(2026, 5, 30));

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<OperationalAssetProfileViewModel>(view.Model);
        Assert.Equal(100m, model.InternalRentUsd);
        Assert.Equal(70m, model.ExternalRentUsd);
        Assert.Equal(25m, model.DirectExpensesUsd);
        Assert.Equal(300m, model.DepreciationUsd);
        Assert.Equal(-155m, model.NetResultUsd);
        Assert.Single(model.Expenses);
    }

    [Fact]
    public async Task Index_Builds_Operational_Asset_Metric_Totals_For_Filtered_List()
    {
        await using var db = CreateDb();
        SeedReferenceData(db);
        SeedAssetWithOwnership(db, companyShare: 100m, partnerShare: 0m);
        db.AssetRentTransactions.AddRange(
            new AssetRentTransaction
            {
                OperationalAssetId = 1,
                RentDate = new DateTime(2026, 5, 5),
                UsageType = AssetRentUsageType.InternalCompanyUse,
                ChargedToType = AssetRentChargedToType.PurchaseContract,
                ChargedToContractId = 1,
                Rate = 100m,
                Currency = "USD",
                FxRateToUsd = 1m,
                AmountOriginal = 100m,
                AmountUsd = 100m
            },
            new AssetRentTransaction
            {
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
                ExpenseTypeId = 1,
                OperationalAssetId = 1,
                ExpenseDate = new DateTime(2026, 5, 7),
                Amount = 25m,
                Currency = "USD",
                AmountUsd = 25m,
                Description = "Fuel"
            },
            new ExpenseTransaction
            {
                ExpenseTypeId = 1,
                OperationalAssetId = 1,
                ExpenseDate = new DateTime(2026, 5, 8),
                Amount = 99m,
                Currency = "USD",
                AmountUsd = 99m,
                Description = "Cancelled",
                IsCancelled = true
            });
        await db.SaveChangesAsync();
        var controller = BuildOperationalAssetsController(db);

        var result = await controller.Index(new OperationalAssetIndexFilterViewModel());

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<OperationalAssetIndexViewModel>(view.Model);
        Assert.Equal(100m, model.TotalInternalRentUsd);
        Assert.Equal(70m, model.TotalExternalRentUsd);
        Assert.Equal(25m, model.TotalDirectExpensesUsd);
        Assert.Equal(300m, model.TotalMonthlyDepreciationUsd);
        Assert.Equal(-155m, model.TotalNetResultUsd);
        Assert.Single(model.Items);
    }

    [Fact]
    public async Task Expense_Create_WithOperationalAsset_Links_Expense_And_Keeps_Standard_Debit_Ledger()
    {
        await using var db = CreateDb();
        SeedReferenceData(db);
        SeedAssetWithOwnership(db, companyShare: 100m, partnerShare: 0m);
        await db.SaveChangesAsync();
        var controller = BuildExpensesController(db);

        var result = await controller.Create(new ExpenseCreateViewModel
        {
            ExpenseTypeId = 1,
            OperationalAssetId = 1,
            ExpenseDate = new DateTime(2026, 5, 9),
            Amount = 35m,
            Currency = "USD",
            AppliedFxRateToUsd = 1m,
            Description = "Asset maintenance"
        });

        Assert.IsType<RedirectToActionResult>(result);
        var expense = await db.ExpenseTransactions.SingleAsync();
        Assert.Equal(1, expense.OperationalAssetId);
        var ledger = await db.LedgerEntries.SingleAsync();
        Assert.Equal("Expense", ledger.SourceType);
        Assert.Equal(LedgerSide.Debit, ledger.Side);
        Assert.Null(ledger.ServiceProviderId);
    }

    [Fact]
    public async Task ContractPnl_Does_Not_Count_Internal_Asset_Rent_As_Official_Total()
    {
        await using var db = CreateDb();
        SeedReferenceData(db);
        SeedAssetWithOwnership(db, companyShare: 100m, partnerShare: 0m);
        db.AssetRentTransactions.Add(new AssetRentTransaction
        {
            OperationalAssetId = 1,
            RentDate = new DateTime(2026, 5, 10),
            UsageType = AssetRentUsageType.InternalCompanyUse,
            ChargedToType = AssetRentChargedToType.PurchaseContract,
            ChargedToContractId = 1,
            Rate = 100m,
            Currency = "USD",
            FxRateToUsd = 1m,
            AmountOriginal = 100m,
            AmountUsd = 100m
        });
        db.ExpenseTransactions.Add(new ExpenseTransaction
        {
            ExpenseTypeId = 1,
            OperationalAssetId = 1,
            ContractId = 1,
            ExpenseDate = new DateTime(2026, 5, 11),
            Amount = 25m,
            Currency = "USD",
            AmountUsd = 25m,
            Description = "Asset fuel"
        });
        await db.SaveChangesAsync();
        var controller = new ReportsController(db);

        var result = await controller.ContractPnl(new ManagementReportFilterViewModel { ContractId = 1 });

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<ContractPnlReportViewModel>(view.Model);
        var row = Assert.Single(model.PurchaseRows);
        Assert.Equal(25m, row.GeneralExpenseCostUsd);
        Assert.Equal(25m, row.TotalCostUsd);
    }

    [Fact]
    public async Task Reconciliation_Flags_OperationalAsset_Rent_And_Link_Issues()
    {
        await using var db = CreateDb();
        SeedReferenceData(db);
        await db.SaveChangesAsync();
        db.Trucks.Single().IsActive = false;
        db.OperationalAssets.Add(new OperationalAsset
        {
            Id = 1,
            AssetCode = "TRK-OWN-1",
            Name = "Owned Truck 1",
            AssetType = OperationalAssetType.Truck,
            LinkedTruckId = 1,
            MonthlyDepreciationUsd = 300m
        });
        db.AssetRentTransactions.Add(new AssetRentTransaction
        {
            Id = 1,
            OperationalAssetId = 1,
            RentDate = new DateTime(2026, 5, 10),
            UsageType = AssetRentUsageType.InternalCompanyUse,
            ChargedToType = AssetRentChargedToType.PurchaseContract,
            Rate = 100m,
            Currency = "USD",
            FxRateToUsd = 1m,
            AmountOriginal = 100m,
            AmountUsd = 100m,
            IsPostedToLedger = true
        });
        await db.SaveChangesAsync();
        var controller = new ReconciliationController(db);

        var result = await controller.MissingLedger();

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<MissingLedgerViewModel>(view.Model);
        Assert.Contains(model.AssetRentTransactionsWithoutShares, row => row.SourceId == 1);
        Assert.Contains(model.AssetRentOwnershipCoverageIssues, row => row.SourceId == 1);
        Assert.Contains(model.AssetRentPostedWithoutLedger, row => row.SourceId == 1);
        Assert.Contains(model.AssetRentContractRequirementIssues, row => row.SourceId == 1);
        Assert.Contains(model.OperationalAssetLinkIssues, row => row.OperationalAssetId == 1);
    }

    [Fact]
    public async Task AddOwnershipShare_Post_Normalizes_Bound_Date_To_Utc_For_Postgres()
    {
        await using var db = CreateDb();
        SeedReferenceData(db);
        db.OperationalAssets.Add(new OperationalAsset
        {
            Id = 1,
            AssetCode = "TRK-OWN-1",
            Name = "Owned Truck 1",
            AssetType = OperationalAssetType.Truck,
            MonthlyDepreciationUsd = 300m,
            IsActive = true
        });
        await db.SaveChangesAsync();
        var controller = BuildOperationalAssetsController(db);

        var result = await controller.AddOwnershipShare(new AssetOwnershipShareCreateViewModel
        {
            OperationalAssetId = 1,
            OwnerType = AssetOwnerType.Company,
            CompanyId = 1,
            SharePercent = 100m,
            EffectiveFrom = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Unspecified)
        });

        Assert.IsType<RedirectToActionResult>(result);
        var share = await db.AssetOwnershipShares.SingleAsync();
        Assert.Equal(DateTimeKind.Utc, share.EffectiveFrom.Kind);
    }

    [Fact]
    public async Task Profitability_Normalizes_Bound_Date_Filters_To_Utc_For_Postgres()
    {
        await using var db = CreateDb();
        SeedReferenceData(db);
        SeedAssetWithOwnership(db, companyShare: 100m, partnerShare: 0m);
        await db.SaveChangesAsync();
        var controller = BuildOperationalAssetsController(db);

        var result = await controller.Profitability(new OperationalAssetProfitabilityFilterViewModel
        {
            FromDate = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Unspecified),
            ToDate = new DateTime(2026, 5, 31, 0, 0, 0, DateTimeKind.Unspecified)
        });

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<OperationalAssetProfitabilityViewModel>(view.Model);
        Assert.Equal(DateTimeKind.Utc, model.Filter.FromDate!.Value.Kind);
        Assert.Equal(DateTimeKind.Utc, model.Filter.ToDate!.Value.Kind);
    }

    private static OperationalAssetsController BuildOperationalAssetsController(ApplicationDbContext db)
        => new(db)
        {
            TempData = BuildTempData()
        };

    private static ExpensesController BuildExpensesController(ApplicationDbContext db)
        => new(
            db,
            new AuditService(db),
            NullLogger<ExpensesController>.Instance)
        {
            TempData = BuildTempData()
        };

    private static ApplicationDbContext CreateDb()
        => new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static void SeedReferenceData(ApplicationDbContext db)
    {
        db.Currencies.Add(new Currency { Id = 1, Code = "USD", Name = "US Dollar", Symbol = "$", IsActive = true });
        db.Companies.Add(new Company { Id = 1, Code = "PTG", Name = "PTG", IsActive = true });
        db.Partners.Add(new Partner { Id = 1, Code = "P-1", Name = "Partner A", IsActive = true });
        db.Suppliers.Add(new Supplier { Id = 1, Name = "Supplier A", IsActive = true });
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
            SupplierId = 1,
            ProductId = 1,
            ContractDate = new DateTime(2026, 5, 1),
            QuantityMt = 100m,
            PricingMethod = PricingMethod.Fixed,
            UnitPriceUsd = 100m
        });
    }

    private static void SeedAssetWithOwnership(ApplicationDbContext db, decimal companyShare, decimal partnerShare)
    {
        db.OperationalAssets.Add(new OperationalAsset
        {
            Id = 1,
            AssetCode = "TRK-OWN-1",
            Name = "Owned Truck 1",
            AssetType = OperationalAssetType.Truck,
            LinkedTruckId = 1,
            OwnershipMode = OperationalAssetOwnershipMode.SharedOwnership,
            MonthlyDepreciationUsd = 300m,
            DefaultInternalRateUsd = 12m,
            IsActive = true
        });
        if (companyShare > 0m)
        {
            db.AssetOwnershipShares.Add(new AssetOwnershipShare
            {
                OperationalAssetId = 1,
                OwnerType = AssetOwnerType.Company,
                CompanyId = 1,
                SharePercent = companyShare,
                EffectiveFrom = new DateTime(2026, 1, 1)
            });
        }

        if (partnerShare > 0m)
        {
            db.AssetOwnershipShares.Add(new AssetOwnershipShare
            {
                OperationalAssetId = 1,
                OwnerType = AssetOwnerType.Partner,
                PartnerId = 1,
                SharePercent = partnerShare,
                EffectiveFrom = new DateTime(2026, 1, 1)
            });
        }
    }

    private static TempDataDictionary BuildTempData()
        => new(new DefaultHttpContext(), new TestTempDataProvider());

    private sealed class TestTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values)
        {
        }
    }
}
