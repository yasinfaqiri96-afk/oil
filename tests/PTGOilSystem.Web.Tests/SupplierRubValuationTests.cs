using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Controllers;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.ContractJourney;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.Suppliers;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Audit;
using PTGOilSystem.Web.Services.DeleteSafety;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// ارزش روبلی در پروفایل تأمین‌کننده باید از ارقام واقعی بارگیری بیاید، نه از اجبارِ
/// ContractRubPerUsdRate. سناریوی مرجع: قرارداد بدون نرخ ثابت روبل، بارگیری با
/// SettlementValueRub، و SettlementCurrencyCode که هنوز USD مانده است.
/// </summary>
public class SupplierRubValuationTests
{
    // بارگیری رقم روبلی دارد ولی ارز تسویه‌اش USD ثبت شده و قرارداد نرخ ثابت روبل ندارد.
    // پیش‌تر همین ترکیب باعث می‌شد هیچ رقمی روبلی در صفحهٔ تأمین‌کننده دیده نشود.
    [Fact]
    public async Task Details_LoadingWithFileRub_AndNoContractRubRate_ValuesPurchaseInRub()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedContractWithFileRubLoading(db);
        await db.SaveChangesAsync();

        var controller = BuildSuppliersController(db);

        var result = await controller.Details(1);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<SupplierProfileViewModel>(view.Model);

        Assert.Equal(4_000_000m, model.LoadedPurchaseValueRub);
        Assert.False(model.LoadedPurchaseValueRubIsEstimated);

        var contract = Assert.Single(model.Contracts);
        Assert.Equal(4_000_000m, contract.LoadedValueRub);
        Assert.False(contract.LoadedValueRubIsEstimated);
        // منطق دالری دست‌نخورده: ۱۰ تن × ۵۰۰ دالر.
        Assert.Equal(5_000m, model.LoadedPurchaseValueUsd);
    }

    // هیچ پرداختی ثبت نشده: پرداخت روبلی واقعاً صفر است، پس مانده روبلی باید کل خرید
    // روبلی باشد و کارت «مانده حساب» در حالت RUB نباید «ثبت نشده» نشان دهد.
    [Fact]
    public async Task Details_NoPaymentsAtAll_RemainingClaimRub_EqualsLoadedPurchaseRub()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedContractWithFileRubLoading(db);
        await db.SaveChangesAsync();

        var controller = BuildSuppliersController(db);

        var result = await controller.Details(1);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<SupplierProfileViewModel>(view.Model);

        Assert.Equal(0m, model.TotalPaidRub);
        Assert.Equal(4_000_000m, model.SupplierRemainingClaimRub);
        Assert.Equal(0m, model.TotalPaidUsd);
    }

    // پرداخت روبلی واقعی → پرداخت‌شده و مانده طلب هر دو روبلی محاسبه می‌شوند.
    [Fact]
    public async Task Details_RubPayment_ProducesPaidAndRemainingClaimInRub()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedContractWithFileRubLoading(db);
        db.PaymentTransactions.Add(new PaymentTransaction
        {
            Id = 1,
            PaymentDate = new DateTime(2026, 1, 3),
            Direction = PaymentDirection.Out,
            PaymentKind = PaymentKind.SupplierPayment,
            CashAccountId = 1,
            SupplierId = 1,
            ContractId = 1,
            Amount = 1_000_000m,
            Currency = "RUB",
            AppliedFxRateToUsd = 0.0125m,
            AmountUsd = 12_500m,
            Reference = "RUB-PAY"
        });
        await db.SaveChangesAsync();

        var controller = BuildSuppliersController(db);

        var result = await controller.Details(1);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<SupplierProfileViewModel>(view.Model);

        Assert.Equal(1_000_000m, model.TotalPaidRub);
        Assert.Equal(3_000_000m, model.SupplierRemainingClaimRub);

        var contract = Assert.Single(model.Contracts);
        Assert.Equal(1_000_000m, contract.PaidRub);
        Assert.Equal(3_000_000m, contract.LoadedValueBalanceRub);
    }

    // پرداخت دالری با تخصیصِ قفل‌شده به ارز روبل: فقط همان مبلغ قفل‌شدهٔ تخصیص ملاک است،
    // نه نرخ عمومی و نه نرخ امروز.
    [Fact]
    public async Task Details_UsdPayment_WithRubLockedAllocation_UsesAllocationLockedAmount()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedContractWithFileRubLoading(db);
        db.PaymentTransactions.Add(new PaymentTransaction
        {
            Id = 1,
            PaymentDate = new DateTime(2026, 1, 3),
            Direction = PaymentDirection.Out,
            PaymentKind = PaymentKind.SupplierPayment,
            CashAccountId = 1,
            SupplierId = 1,
            ContractId = 1,
            Amount = 5_000m,
            Currency = "USD",
            AppliedFxRateToUsd = 1m,
            AmountUsd = 5_000m,
            IsAdvancePayment = true,
            Reference = "USD-PAY"
        });
        db.SupplierPaymentAllocations.Add(new SupplierPaymentAllocation
        {
            Id = 1,
            PaymentTransactionId = 1,
            ContractId = 1,
            AllocationDate = new DateTime(2026, 1, 4),
            AllocatedPaymentAmount = 5_000m,
            PaymentCurrencyCode = "USD",
            AllocatedBookAmountUsd = 5_000m,
            ContractCurrencyCode = "RUB",
            ContractCurrencyPerUsdRate = 90m,
            AllocatedContractCurrencyAmount = 450_000m,
            Status = SupplierPaymentAllocationStatus.Active
        });
        await db.SaveChangesAsync();

        var controller = BuildSuppliersController(db);

        var result = await controller.Details(1);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<SupplierProfileViewModel>(view.Model);

        Assert.Equal(450_000m, model.TotalPaidRub);
        Assert.Equal(3_550_000m, model.SupplierRemainingClaimRub);
        Assert.Equal(5_000m, model.TotalPaidUsd);
    }

    // هیچ منبع روبلی نداریم: نه رقم فایل، نه قفل نرخ، نه نرخ قرارداد. هیچ عدد تخمینی
    // ساخته نمی‌شود و مقادیر null می‌مانند («ثبت نشده»، نه صفر).
    [Fact]
    public async Task Details_NoRubSourceAtAll_ProducesNoRubNumbers()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedContractWithFileRubLoading(db);
        await db.SaveChangesAsync();

        var loading = await db.LoadingRegisters.FirstAsync(l => l.Id == 1);
        loading.SettlementValueRub = null;
        await db.SaveChangesAsync();

        var controller = BuildSuppliersController(db);

        var result = await controller.Details(1);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<SupplierProfileViewModel>(view.Model);

        Assert.Null(model.LoadedPurchaseValueRub);
        Assert.False(model.LoadedPurchaseValueRubIsEstimated);
        Assert.Null(model.SupplierRemainingClaimRub);
        Assert.Null(Assert.Single(model.Contracts).LoadedValueRub);
    }

    // نبودِ رقم واقعی + وجود ContractRubPerUsdRate: تبدیل انجام می‌شود ولی «تخمینی» علامت
    // می‌خورد تا بدون علامت با رقم واقعی اشتباه نشود.
    [Fact]
    public async Task Details_ContractRateFallback_IsMarkedEstimated()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedContractWithFileRubLoading(db);
        await db.SaveChangesAsync();

        var loading = await db.LoadingRegisters.FirstAsync(l => l.Id == 1);
        loading.SettlementValueRub = null;
        var contract = await db.Contracts.FirstAsync(c => c.Id == 1);
        contract.ContractRubPerUsdRate = 90m;
        await db.SaveChangesAsync();

        var controller = BuildSuppliersController(db);

        var result = await controller.Details(1);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<SupplierProfileViewModel>(view.Model);

        Assert.Equal(450_000m, model.LoadedPurchaseValueRub);
        Assert.True(model.LoadedPurchaseValueRubIsEstimated);
        Assert.True(Assert.Single(model.Contracts).LoadedValueRubIsEstimated);
    }

    // ارزش روبلی صفحهٔ تأمین‌کننده و صفحهٔ جریان قرارداد باید یک عدد باشد (منبع مشترک).
    [Fact]
    public async Task SupplierProfile_And_ContractJourney_Report_SameLoadingRubTotal()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedContractWithFileRubLoading(db);
        await db.SaveChangesAsync();

        var supplierResult = await BuildSuppliersController(db).Details(1);
        var supplierModel = Assert.IsType<SupplierProfileViewModel>(
            Assert.IsType<ViewResult>(supplierResult).Model);

        var journeyResult = await new ContractJourneyController(db, new StockService(db)).Details(1);
        var journeyModel = Assert.IsType<ContractJourneyDetailsViewModel>(
            Assert.IsType<ViewResult>(journeyResult).Model);

        Assert.Equal(4_000_000m, journeyModel.RubSettlementSummary.LoadingValueRub);
        Assert.Equal(supplierModel.LoadedPurchaseValueRub, journeyModel.RubSettlementSummary.LoadingValueRub);
        Assert.Equal(
            supplierModel.LoadedPurchaseValueRubIsEstimated,
            journeyModel.RubSettlementSummary.LoadingValueRubIsEstimated);
    }

    // انتخابگر ارز فقط نمای صفحه را عوض می‌کند و مقادیر دالری را دست نمی‌زند.
    [Fact]
    public async Task Details_CurrencyRub_SwitchesDisplayCurrency_WithoutChangingUsdTotals()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedContractWithFileRubLoading(db);
        await db.SaveChangesAsync();

        var controller = BuildSuppliersController(db);

        var usdModel = Assert.IsType<SupplierProfileViewModel>(
            Assert.IsType<ViewResult>(await controller.Details(1)).Model);
        var rubModel = Assert.IsType<SupplierProfileViewModel>(
            Assert.IsType<ViewResult>(await controller.Details(1, currency: "rub")).Model);

        Assert.False(usdModel.IsRubDisplay);
        Assert.Equal("USD", usdModel.DisplayCurrency);
        Assert.True(rubModel.IsRubDisplay);
        Assert.Equal("RUB", rubModel.DisplayCurrency);
        Assert.Equal(usdModel.LoadedPurchaseValueUsd, rubModel.LoadedPurchaseValueUsd);
        Assert.Equal(usdModel.SupplierRemainingClaimUsd, rubModel.SupplierRemainingClaimUsd);
    }

    // قرارداد دالری بدون ContractRubPerUsdRate؛ بارگیری ۱۰ تن × ۵۰۰ دالر با رقم روبلی فایل
    // و ارز تسویهٔ USD (یعنی SettlementCurrencyCode نباید شرط ورود باشد).
    private static void SeedContractWithFileRubLoading(ApplicationDbContext db)
    {
        db.Companies.Add(new Company { Id = 1, Code = "PTG", Name = "PTG" });
        db.Products.Add(new Product { Id = 1, Code = "GO", Name = "Gas Oil" });
        db.Suppliers.Add(new Supplier { Id = 1, Code = "SU001", Name = "Petrogaz" });
        db.CashAccounts.Add(new CashAccount
        {
            Id = 1,
            Code = "BANK-USD",
            Name = "Main USD Bank",
            AccountType = CashAccountType.Bank,
            Currency = "USD",
            IsActive = true
        });
        db.Contracts.Add(new Contract
        {
            Id = 1,
            ContractNumber = "PUR-FILE-RUB",
            ContractType = ContractType.Purchase,
            Status = ContractStatus.Active,
            CompanyId = 1,
            SupplierId = 1,
            ProductId = 1,
            ContractDate = new DateTime(2026, 1, 1),
            QuantityMt = 20m,
            PricingMethod = PricingMethod.Fixed,
            UnitPriceInCurrency = 500m,
            UnitPriceUsd = 500m,
            Currency = "USD",
            SettlementCurrencyCode = "USD",
            RubRatePolicy = RubSettlementRatePolicy.NotApplicable,
            ContractRubPerUsdRate = null,
            AppliedFxRateToUsd = 1m
        });
        db.LoadingRegisters.Add(new LoadingRegister
        {
            Id = 1,
            ContractId = 1,
            ProductId = 1,
            LoadingDate = new DateTime(2026, 1, 2),
            LoadedQuantityMt = 10m,
            LoadingPriceUsd = 500m,
            SettlementCurrencyCode = "USD",
            RubRateStatus = RubSettlementRateStatus.NotRequired,
            SettlementValueRub = 4_000_000m
        });
    }

    private static SuppliersController BuildSuppliersController(ApplicationDbContext db)
        => new(db, new AuditService(db), new MasterDataDeleteSafetyService(db));

    private static DbContextOptions<ApplicationDbContext> NewDbOptions()
        => new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
}
