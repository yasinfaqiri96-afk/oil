using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Controllers;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.Suppliers;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.DeleteSafety;
using Xunit;

namespace PTGOilSystem.Web.Tests;

public class SuppliersControllerTests
{
    [Fact]
    public async Task Details_Builds_Profile_Statement_From_Direct_And_Contract_Ledger_Without_Duplicates()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedSupplierProfileData(db);
        await db.SaveChangesAsync();

        var controller = BuildController(db);

        var result = await controller.Details(1);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<SupplierProfileViewModel>(view.Model);
        Assert.Equal(2, model.PurchaseContractsCount);
        Assert.Equal(1, model.ActivePurchaseContractsCount);
        Assert.Equal(150m, model.TotalPurchaseQuantityMt);
        Assert.Equal(2000m, model.EstimatedContractValueUsd);
        Assert.Equal(250m, model.LedgerDebitUsd);
        Assert.Equal(1000m, model.LedgerCreditUsd);
        // مانده نمایشی = Σ(داده − گرفته) = 250 − 1000؛ منفی یعنی بدهی به تأمین‌کننده.
        Assert.Equal(-750m, model.LedgerBalanceUsd);
        Assert.Equal(100m, model.TotalPaidUsd);
        Assert.Equal(new DateTime(2026, 1, 4), model.LastPaymentDate);

        Assert.Equal(3, model.StatementRows.Count);
        Assert.Collection(
            model.StatementRows,
            row => Assert.Equal(1000m, row.RunningBalanceUsd),
            row => Assert.Equal(800m, row.RunningBalanceUsd),
            row => Assert.Equal(750m, row.RunningBalanceUsd));
        Assert.Equal(1, model.StatementRows.Count(r => r.LedgerEntryId == 1));
    }

    [Fact]
    public async Task Details_ContractFilter_Returns_Only_Selected_Contract_Statement()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedSupplierProfileData(db);
        await db.SaveChangesAsync();

        var controller = BuildController(db);

        var result = await controller.Details(1, contractId: 2);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<SupplierProfileViewModel>(view.Model);
        Assert.Equal("statement", model.ActiveTab);
        Assert.Equal(2, model.SelectedContractId);
        Assert.NotNull(model.SelectedContract);
        var row = Assert.Single(model.StatementRows);
        Assert.Equal(2, row.ContractId);
        Assert.Equal(-200m, row.RunningBalanceUsd);
    }

    [Fact]
    public async Task Index_Returns_Supplier_Metrics_Without_Row_By_Row_Loading()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedSupplierProfileData(db);
        await db.SaveChangesAsync();

        var controller = BuildController(db);

        var result = await controller.Index(null);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<SupplierIndexViewModel>(view.Model);
        var supplier = Assert.Single(model.Items.Where(i => i.SupplierId == 1));
        Assert.Equal(2, supplier.PurchaseContractsCount);
        Assert.Equal(1, supplier.ActivePurchaseContractsCount);
        Assert.Equal(150m, supplier.TotalPurchaseQuantityMt);
        Assert.Equal(-750m, supplier.LedgerBalanceUsd);
        Assert.Equal(100m, supplier.TotalPaidUsd);
        Assert.Equal(new DateTime(2026, 1, 4), supplier.LastPaymentDate);
    }

    [Fact]
    public async Task Details_Statement_Shows_FxRate_Per_Row_And_Recognized_Sarraf_Fx_Difference()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        db.Suppliers.Add(new Supplier { Id = 1, Code = "SUP-A", Name = "Supplier A" });
        db.Sarrafs.Add(new Sarraf { Id = 1, Name = "Sarraf A" });
        db.LedgerEntries.Add(new LedgerEntry
        {
            Id = 1,
            EntryDate = new DateTime(2026, 1, 2),
            Side = LedgerSide.Debit,
            AmountUsd = 100m,
            Currency = "USD",
            SourceAmount = 8000m,
            SourceCurrencyCode = "AFN",
            AppliedFxRateToUsd = 0.0125m,
            Description = "Supplier payment",
            SourceType = "SupplierPayment",
            SourceId = 1,
            SupplierId = 1
        });
        db.SarrafSettlements.Add(new SarrafSettlement
        {
            Id = 1,
            SettlementDate = new DateTime(2026, 1, 3),
            SarrafId = 1,
            SupplierId = 1,
            Status = SarrafSettlementStatus.Posted,
            DifferenceTreatment = SarrafSettlementDifferenceTreatment.RecognizeExchangeGainLoss,
            DifferenceType = SarrafSettlementDifferenceType.Loss,
            DifferenceAmountUsd = 50m
        });
        await db.SaveChangesAsync();

        var controller = BuildController(db);

        var result = await controller.Details(1);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<SupplierProfileViewModel>(view.Model);

        var row = Assert.Single(model.StatementRows);
        Assert.Equal(0.0125m, row.FxRateUsed);
        Assert.Equal("AFN", row.Currency);

        Assert.True(model.HasRecognizedFx);
        Assert.Equal(50m, model.RecognizedFxLossUsd);
        Assert.Equal(0m, model.RecognizedFxGainUsd);
        Assert.Equal(50m, model.RecognizedFxNetUsd);
    }

    [Fact]
    public async Task Details_Statement_Without_Recognized_Sarraf_Fx_Reports_No_Fx_Data()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        db.Suppliers.Add(new Supplier { Id = 1, Code = "SUP-A", Name = "Supplier A" });
        db.LedgerEntries.Add(new LedgerEntry
        {
            Id = 1,
            EntryDate = new DateTime(2026, 1, 2),
            Side = LedgerSide.Credit,
            AmountUsd = 100m,
            Currency = "USD",
            Description = "Supplier purchase",
            SourceType = "SupplierPayment",
            SourceId = 1,
            SupplierId = 1
        });
        await db.SaveChangesAsync();

        var controller = BuildController(db);

        var result = await controller.Details(1);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<SupplierProfileViewModel>(view.Model);

        Assert.False(model.HasRecognizedFx);
        Assert.Equal(0m, model.RecognizedFxNetUsd);
        // نرخ ارز این سند ذخیره نشده؛ ستون باید null باشد (در View «—» نمایش داده می‌شود).
        var row = Assert.Single(model.StatementRows);
        Assert.Null(row.FxRateUsed);
    }

    [Fact]
    public async Task Details_Statement_Shows_ThreeWaySettlement_Labels_And_Source_Details_Links()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        db.Suppliers.Add(new Supplier { Id = 1, Code = "SUP-A", Name = "Supplier A" });
        db.LedgerEntries.AddRange(
            new LedgerEntry
            {
                Id = 1,
                EntryDate = new DateTime(2026, 6, 6),
                Side = LedgerSide.Debit,
                AmountUsd = 950m,
                Currency = "USD",
                SourceType = ThreeWaySettlementController.LedgerSourceType,
                SourceId = 10,
                Reference = "HW-10",
                Description = "Three-way settlement",
                SupplierId = 1
            },
            new LedgerEntry
            {
                Id = 2,
                EntryDate = new DateTime(2026, 6, 7),
                Side = LedgerSide.Credit,
                AmountUsd = 950m,
                Currency = "USD",
                SourceType = ThreeWaySettlementController.CancellationLedgerSourceType,
                SourceId = 10,
                Reference = "HW-10",
                Description = "Three-way settlement cancellation",
                SupplierId = 1
            });
        await db.SaveChangesAsync();

        var controller = BuildController(db);

        var result = await controller.Details(1);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<SupplierProfileViewModel>(view.Model);

        Assert.Collection(
            model.StatementRows,
            row =>
            {
                Assert.Equal("تسویه سه‌طرفه / حواله", row.Type);
                Assert.Equal("ThreeWaySettlement", row.SourceDetailsController);
                Assert.Equal("Details", row.SourceDetailsAction);
                Assert.Equal(10, row.SourceDetailsRouteId);
            },
            row =>
            {
                Assert.Equal("برگشت تسویه سه‌طرفه", row.Type);
                Assert.Equal("ThreeWaySettlement", row.SourceDetailsController);
                Assert.Equal("Details", row.SourceDetailsAction);
                Assert.Equal(10, row.SourceDetailsRouteId);
            });
    }

    [Fact]
    public async Task Details_Statement_RubSarrafReduction_ShowsExactRub_NotReconvertedAtContractRate()
    {
        // پرداختِ صراف 100,000 روبل: نرخ تأمین‌کننده 77 (USD داخلی = 1298.7013)،
        // اما نرخ قراردادِ ثبت‌شده 80 است. صورت‌حساب باید دقیقاً 100,000 روبل کاهش نشان دهد،
        // نه 1298.7013 × 80 = 103,896 (که نتیجهٔ تبدیلِ نادرستِ RUB→USD→RUB بود).
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        db.Companies.Add(new Company { Id = 1, Code = "PTG", Name = "PTG" });
        db.Products.Add(new Product { Id = 1, Code = "GO", Name = "Gas Oil" });
        db.Suppliers.Add(new Supplier { Id = 1, Code = "SUP-A", Name = "Supplier A" });
        db.Sarrafs.Add(new Sarraf { Id = 1, Name = "Sarraf A" });
        db.Contracts.Add(new Contract
        {
            Id = 1,
            ContractNumber = "PUR-RUB-1",
            ContractType = ContractType.Purchase,
            Status = ContractStatus.Active,
            CompanyId = 1,
            SupplierId = 1,
            ProductId = 1,
            ContractDate = new DateTime(2026, 1, 1),
            QuantityMt = 100m,
            PricingMethod = PricingMethod.Fixed,
            Currency = "RUB",
            ContractRubPerUsdRate = 80m
        });
        db.SarrafSettlements.Add(new SarrafSettlement
        {
            Id = 1,
            SettlementDate = new DateTime(2026, 1, 5),
            SarrafId = 1,
            SupplierId = 1,
            ContractId = 1,
            RequestedAmount = 100_000m,
            RequestedCurrency = "RUB",
            RequestedFxRateToUsd = 0.012987013m,
            RequestedAmountUsd = 1_298.7013m,
            SarrafChargedAmount = 100_000m,
            SarrafCurrency = "RUB",
            SarrafFxRateToUsd = 0.012987013m,
            SarrafChargedAmountUsd = 1_298.7013m,
            SupplierAcceptedAmount = 95_000m,
            SupplierAcceptedCurrency = "RUB",
            SupplierAcceptedFxRateToUsd = 0.012987013m,
            SupplierAcceptedAmountUsd = 1_233.7662m,
            DifferenceAmountUsd = 64.9351m,
            DifferenceType = SarrafSettlementDifferenceType.Loss,
            DifferenceTreatment = SarrafSettlementDifferenceTreatment.RecognizeExchangeGainLoss,
            Status = SarrafSettlementStatus.Posted,
            LedgerEntryId = 1
        });
        db.LedgerEntries.Add(new LedgerEntry
        {
            Id = 1,
            EntryDate = new DateTime(2026, 1, 5),
            Side = LedgerSide.Debit,
            AmountUsd = 1_298.7013m,
            Currency = "USD",
            SourceAmount = 100_000m,
            SourceCurrencyCode = "RUB",
            AppliedFxRateToUsd = 0.012987m,
            SourceType = SarrafSettlementService.SupplierLedgerSourceType,
            SourceId = 1,
            Description = "Sarraf settlement supplier reduction",
            SupplierId = 1,
            ContractId = 1
        });
        await db.SaveChangesAsync();

        var controller = BuildController(db);

        var result = await controller.Details(1);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<SupplierProfileViewModel>(view.Model);

        var row = Assert.Single(model.StatementRows);
        Assert.Equal("RUB", row.Currency);
        Assert.Equal(100_000m, row.Debit);
        Assert.Equal(100_000m, row.DebitRubEquivalent);
        Assert.Equal(-100_000m, row.RunningBalanceRubEquivalent);
        Assert.Equal(100_000m, model.TotalPaidRub);
        Assert.Equal(100_000m, Assert.Single(model.Contracts).PaidRub);
        var sarrafRow = Assert.Single(model.SarrafSettlements);
        Assert.Equal(1_298.7013m, sarrafRow.SupplierReductionAmountUsd);
        Assert.Equal(100_000m, sarrafRow.SupplierReductionAmountRub);
        Assert.Equal(95_000m, sarrafRow.SupplierAcceptedAmountRub);
        var paymentLine = Assert.Single(model.PaymentLines);
        Assert.True(paymentLine.IsSarraf);
        Assert.Equal(100_000m, paymentLine.Amount);
        Assert.Equal("RUB", paymentLine.Currency);
        Assert.Equal(1_298.7013m, paymentLine.AmountUsd);
        // USD داخلی دست‌نخورده می‌ماند.
        Assert.Equal(1_298.7013m, row.DebitUsd);
    }

    [Fact]
    public async Task SupplierStatement_ViaSarrafPayment_Decreases_RubSettlementRunningBalance()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        db.Suppliers.Add(new Supplier { Id = 1, Code = "SUP-A", Name = "Supplier A" });

        // قرارداد خرید با ارز تسویه RUB.
        db.Contracts.Add(new Contract
        {
            Id = 1,
            ContractNumber = "PUR-RUB",
            ContractType = ContractType.Purchase,
            Status = ContractStatus.Active,
            SupplierId = 1,
            ContractDate = new DateTime(2026, 1, 1),
            QuantityMt = 1m,
            PricingMethod = PricingMethod.Fixed,
            UnitPriceUsd = 77m,
            Currency = "USD",
            SettlementCurrencyCode = "RUB"
        });

        // ردیف بدهکار شدن بابت بار (Credit): +77 USD / +7700 RUB.
        db.LedgerEntries.Add(new LedgerEntry
        {
            Id = 1,
            EntryDate = new DateTime(2026, 1, 2),
            Side = LedgerSide.Credit,
            AmountUsd = 77m,
            Currency = "USD",
            SourceAmount = 7_700m,
            SourceCurrencyCode = "RUB",
            AppliedFxRateToUsd = 0.01m,
            Description = "Loading payable",
            SourceType = "Loading",
            SourceId = 1,
            SupplierId = 1,
            ContractId = 1
        });

        // پرداخت از طریق صراف (Debit): -30 USD / -3000 RUB.
        db.LedgerEntries.Add(new LedgerEntry
        {
            Id = 2,
            EntryDate = new DateTime(2026, 1, 3),
            Side = LedgerSide.Debit,
            AmountUsd = 30m,
            Currency = "USD",
            SourceAmount = 3_000m,
            SourceCurrencyCode = "RUB",
            AppliedFxRateToUsd = 0.01m,
            Description = "Sarraf settlement supplier reduction",
            SourceType = SarrafSettlementService.SupplierLedgerSourceType,
            SourceId = 1,
            SupplierId = 1,
            ContractId = 1
        });
        await db.SaveChangesAsync();

        var controller = BuildController(db);

        var result = await controller.Details(1);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<SupplierProfileViewModel>(view.Model);

        Assert.Equal(2, model.StatementRows.Count);

        var loadingRow = model.StatementRows[0];
        Assert.Equal("RUB", loadingRow.Currency);
        Assert.Equal(7_700m, loadingRow.Credit);
        Assert.Equal(7_700m, loadingRow.CreditRubEquivalent);
        Assert.Equal(77m, loadingRow.RunningBalanceUsd);
        Assert.Equal(7_700m, loadingRow.RunningBalanceRubEquivalent);

        var sarrafRow = model.StatementRows[1];
        Assert.Equal("RUB", sarrafRow.Currency);
        Assert.Equal(3_000m, sarrafRow.Debit);
        Assert.Equal(3_000m, sarrafRow.DebitRubEquivalent);

        // مانده نهایی: USD = 47، RUB = 4700.
        Assert.Equal(47m, sarrafRow.RunningBalanceUsd);
        Assert.Equal(4_700m, sarrafRow.RunningBalanceRubEquivalent);
    }

    [Fact]
    public async Task Details_UsdSupplierPayment_DoesNotFabricateRub_FromLoadedRubTotals()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
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
            ContractNumber = "PUR-PETROGAZ",
            ContractType = ContractType.Purchase,
            Status = ContractStatus.Active,
            CompanyId = 1,
            SupplierId = 1,
            ProductId = 1,
            ContractDate = new DateTime(2026, 1, 1),
            QuantityMt = 1m,
            PricingMethod = PricingMethod.Fixed,
            UnitPriceUsd = 3_080_371m,
            Currency = "USD",
            SettlementCurrencyCode = "RUB"
        });
        db.LoadingRegisters.Add(new LoadingRegister
        {
            Id = 1,
            ContractId = 1,
            ProductId = 1,
            LoadingDate = new DateTime(2026, 1, 2),
            LoadedQuantityMt = 1m,
            LoadingPriceUsd = 3_080_371m,
            SettlementCurrencyCode = "RUB",
            SettlementValueRub = 1_750_000m
        });
        db.PaymentTransactions.Add(new PaymentTransaction
        {
            Id = 1,
            PaymentDate = new DateTime(2026, 1, 3),
            Direction = PaymentDirection.Out,
            PaymentKind = PaymentKind.SupplierPayment,
            CashAccountId = 1,
            SupplierId = 1,
            ContractId = 1,
            Amount = 25_000m,
            Currency = "USD",
            AppliedFxRateToUsd = 1m,
            AmountUsd = 25_000m,
            Reference = "USD-PAY"
        });
        await db.SaveChangesAsync();

        var controller = BuildController(db);

        var result = await controller.Details(1);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<SupplierProfileViewModel>(view.Model);
        Assert.Equal(3_080_371m, model.LoadedPurchaseValueUsd);
        Assert.Equal(1_750_000m, model.LoadedPurchaseValueRub);
        Assert.Equal(25_000m, model.TotalPaidUsd);
        Assert.Null(model.TotalPaidRub);
        Assert.Equal(1_750_000m, model.SupplierRemainingClaimRub);
        Assert.Null(Assert.Single(model.Contracts).PaidRub);
    }

    [Fact]
    public async Task Details_SarrafSettlement_FallsBack_To_LoadingRubRate_When_Ledger_Has_No_Exact_Rub()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        db.Companies.Add(new Company { Id = 1, Code = "PTG", Name = "PTG" });
        db.Products.Add(new Product { Id = 1, Code = "GO", Name = "Gas Oil" });
        db.Suppliers.Add(new Supplier { Id = 1, Code = "SU001", Name = "Petrogaz" });
        db.Sarrafs.Add(new Sarraf { Id = 3, Name = "Noorzad", IsActive = true });
        db.Contracts.Add(new Contract
        {
            Id = 1,
            ContractNumber = "PUR-RUB-LEGACY",
            ContractType = ContractType.Purchase,
            Status = ContractStatus.Active,
            CompanyId = 1,
            SupplierId = 1,
            ProductId = 1,
            ContractDate = new DateTime(2026, 1, 1),
            QuantityMt = 1m,
            PricingMethod = PricingMethod.Fixed,
            UnitPriceUsd = 500m,
            Currency = "USD",
            SettlementCurrencyCode = "RUB"
        });
        db.LoadingRegisters.Add(new LoadingRegister
        {
            Id = 1,
            ContractId = 1,
            ProductId = 1,
            LoadingDate = new DateTime(2026, 1, 2),
            LoadedQuantityMt = 1m,
            LoadingPriceUsd = 500m,
            SettlementCurrencyCode = "RUB",
            SettlementValueRub = 40_000m
        });
        db.SarrafSettlements.Add(new SarrafSettlement
        {
            Id = 1,
            SarrafId = 3,
            SupplierId = 1,
            ContractId = 1,
            SettlementDate = new DateTime(2026, 1, 3),
            RequestedAmount = 30m,
            RequestedCurrency = "USD",
            RequestedFxRateToUsd = 1m,
            RequestedAmountUsd = 30m,
            SarrafChargedAmount = 2_400m,
            SarrafCurrency = "RUB",
            SarrafFxRateToUsd = 0.0125m,
            SarrafChargedAmountUsd = 30m,
            SupplierAcceptedAmount = 30m,
            SupplierAcceptedCurrency = "USD",
            SupplierAcceptedFxRateToUsd = 1m,
            SupplierAcceptedAmountUsd = 30m,
            DifferenceAmountUsd = 0m,
            DifferenceType = SarrafSettlementDifferenceType.None,
            DifferenceTreatment = SarrafSettlementDifferenceTreatment.AcceptedAmountOnly,
            Status = SarrafSettlementStatus.Posted,
            LedgerEntryId = 1
        });
        db.LedgerEntries.Add(new LedgerEntry
        {
            Id = 1,
            EntryDate = new DateTime(2026, 1, 3),
            Side = LedgerSide.Debit,
            AmountUsd = 30m,
            Currency = "USD",
            SourceAmount = 30m,
            SourceCurrencyCode = "USD",
            AppliedFxRateToUsd = 1m,
            Description = "Sarraf settlement supplier reduction",
            SourceType = SarrafSettlementService.SupplierLedgerSourceType,
            SourceId = 1,
            SupplierId = 1,
            ContractId = 1
        });
        await db.SaveChangesAsync();

        var controller = BuildController(db);

        var result = await controller.Details(1);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<SupplierProfileViewModel>(view.Model);

        Assert.Equal(40_000m, model.LoadedPurchaseValueRub);
        Assert.Equal(2_400m, model.TotalPaidRub);
        Assert.Equal(37_600m, model.SupplierRemainingClaimRub);

        var contract = Assert.Single(model.Contracts);
        Assert.Equal(2_400m, contract.PaidRub);

        var sarraf = Assert.Single(model.SarrafSettlements);
        Assert.Equal(2_400m, sarraf.SupplierReductionAmountRub);

        var paymentLine = Assert.Single(model.PaymentLines);
        Assert.True(paymentLine.IsSarraf);
        Assert.Equal("RUB", paymentLine.Currency);
        Assert.Equal(2_400m, paymentLine.Amount);

        var statementRow = Assert.Single(model.StatementRows);
        Assert.Equal(2_400m, statementRow.DebitRubEquivalent);
        Assert.Equal(-2_400m, statementRow.RunningBalanceRubEquivalent);
    }

    [Fact]
    public async Task Details_Separates_Actual_Rub_Paid_From_Rub_Applied_To_Supplier_Claim()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        db.Companies.Add(new Company { Id = 1, Code = "PTG", Name = "PTG" });
        db.Products.Add(new Product { Id = 1, Code = "GO", Name = "Gas Oil" });
        db.Suppliers.Add(new Supplier { Id = 1, Code = "SU001", Name = "Petrogaz" });
        db.Sarrafs.Add(new Sarraf { Id = 3, Name = "Noorzad", IsActive = true });
        db.Contracts.Add(new Contract
        {
            Id = 1,
            ContractNumber = "PUR-RUB-ACTUAL",
            ContractType = ContractType.Purchase,
            Status = ContractStatus.Active,
            CompanyId = 1,
            SupplierId = 1,
            ProductId = 1,
            ContractDate = new DateTime(2026, 1, 1),
            QuantityMt = 1m,
            PricingMethod = PricingMethod.Fixed,
            UnitPriceUsd = 500m,
            Currency = "USD",
            SettlementCurrencyCode = "RUB"
        });
        db.LoadingRegisters.Add(new LoadingRegister
        {
            Id = 1,
            ContractId = 1,
            ProductId = 1,
            LoadingDate = new DateTime(2026, 1, 2),
            LoadedQuantityMt = 1m,
            LoadingPriceUsd = 500m,
            SettlementCurrencyCode = "RUB",
            SettlementValueRub = 40_000m
        });
        db.SarrafSettlements.Add(new SarrafSettlement
        {
            Id = 1,
            SarrafId = 3,
            SupplierId = 1,
            ContractId = 1,
            SettlementDate = new DateTime(2026, 1, 3),
            RequestedAmount = 30m,
            RequestedCurrency = "USD",
            RequestedFxRateToUsd = 1m,
            RequestedAmountUsd = 30m,
            SarrafChargedAmount = 2_400m,
            SarrafCurrency = "RUB",
            SarrafFxRateToUsd = 0.0125m,
            SarrafChargedAmountUsd = 30m,
            SupplierAcceptedAmount = 28m,
            SupplierAcceptedCurrency = "USD",
            SupplierAcceptedFxRateToUsd = 1m,
            SupplierAcceptedAmountUsd = 28m,
            DifferenceAmountUsd = -2m,
            DifferenceType = SarrafSettlementDifferenceType.SupplierShortfall,
            DifferenceReason = DifferenceReason.FxDifference,
            DifferenceTreatment = SarrafSettlementDifferenceTreatment.AcceptedAmountOnly,
            Status = SarrafSettlementStatus.Posted,
            LedgerEntryId = 1
        });
        db.LedgerEntries.Add(new LedgerEntry
        {
            Id = 1,
            EntryDate = new DateTime(2026, 1, 3),
            Side = LedgerSide.Debit,
            AmountUsd = 28m,
            Currency = "USD",
            SourceAmount = 28m,
            SourceCurrencyCode = "USD",
            AppliedFxRateToUsd = 1m,
            Description = "Sarraf settlement supplier reduction",
            SourceType = SarrafSettlementService.SupplierLedgerSourceType,
            SourceId = 1,
            SupplierId = 1,
            ContractId = 1
        });
        await db.SaveChangesAsync();

        var controller = BuildController(db);

        var result = await controller.Details(1);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<SupplierProfileViewModel>(view.Model);

        Assert.Equal(30m, model.TotalPaidActualUsd);
        Assert.Equal(2_400m, model.TotalPaidActualRub);
        Assert.Equal(28m, model.TotalPaidUsd);
        Assert.Equal(2_240m, model.TotalPaidRub);
        Assert.Equal(37_760m, model.SupplierRemainingClaimRub);
    }

    private static SuppliersController BuildController(ApplicationDbContext db)
        => new(db, new AuditService(db), new MasterDataDeleteSafetyService(db));

    private static DbContextOptions<ApplicationDbContext> NewDbOptions()
        => new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

    private static void SeedSupplierProfileData(ApplicationDbContext db)
    {
        db.Companies.Add(new Company { Id = 1, Code = "PTG", Name = "PTG" });
        db.Products.Add(new Product { Id = 1, Code = "GO", Name = "Gas Oil" });
        db.Suppliers.AddRange(
            new Supplier { Id = 1, Code = "SUP-A", Name = "Supplier A" },
            new Supplier { Id = 2, Code = "SUP-B", Name = "Supplier B" });
        db.CashAccounts.Add(new CashAccount
        {
            Id = 1,
            Code = "BANK-USD",
            Name = "Main USD Bank",
            AccountType = CashAccountType.Bank,
            Currency = "USD",
            IsActive = true
        });
        db.Contracts.AddRange(
            new Contract
            {
                Id = 1,
                ContractNumber = "PUR-001",
                ContractType = ContractType.Purchase,
                Status = ContractStatus.Active,
                CompanyId = 1,
                SupplierId = 1,
                ProductId = 1,
                ContractDate = new DateTime(2026, 1, 1),
                QuantityMt = 100m,
                PricingMethod = PricingMethod.Fixed,
                UnitPriceInCurrency = 10m,
                UnitPriceUsd = 10m,
                Currency = "USD"
            },
            new Contract
            {
                Id = 2,
                ContractNumber = "PUR-002",
                ContractType = ContractType.Purchase,
                Status = ContractStatus.Closed,
                CompanyId = 1,
                SupplierId = 1,
                ProductId = 1,
                ContractDate = new DateTime(2026, 1, 2),
                QuantityMt = 50m,
                PricingMethod = PricingMethod.Fixed,
                UnitPriceInCurrency = 20m,
                UnitPriceUsd = 20m,
                Currency = "USD"
            },
            new Contract
            {
                Id = 3,
                ContractNumber = "PUR-OTHER",
                ContractType = ContractType.Purchase,
                Status = ContractStatus.Active,
                CompanyId = 1,
                SupplierId = 2,
                ProductId = 1,
                ContractDate = new DateTime(2026, 1, 3),
                QuantityMt = 10m,
                PricingMethod = PricingMethod.Fixed,
                UnitPriceUsd = 1m,
                Currency = "USD"
            });
        db.LedgerEntries.AddRange(
            new LedgerEntry
            {
                Id = 1,
                EntryDate = new DateTime(2026, 1, 1),
                Side = LedgerSide.Credit,
                AmountUsd = 1000m,
                Currency = "USD",
                SourceAmount = 1000m,
                SourceCurrencyCode = "USD",
                AppliedFxRateToUsd = 1m,
                SourceType = "Adjustment",
                SourceId = 1,
                Description = "Supplier payable",
                ContractId = 1,
                SupplierId = 1
            },
            new LedgerEntry
            {
                Id = 2,
                EntryDate = new DateTime(2026, 1, 2),
                Side = LedgerSide.Debit,
                AmountUsd = 200m,
                Currency = "USD",
                SourceAmount = 200m,
                SourceCurrencyCode = "USD",
                AppliedFxRateToUsd = 1m,
                SourceType = "SupplierPayment",
                SourceId = 2,
                Description = "Contract path only",
                ContractId = 2
            },
            new LedgerEntry
            {
                Id = 3,
                EntryDate = new DateTime(2026, 1, 3),
                Side = LedgerSide.Debit,
                AmountUsd = 50m,
                Currency = "USD",
                SourceAmount = 50m,
                SourceCurrencyCode = "USD",
                AppliedFxRateToUsd = 1m,
                SourceType = "SupplierPayment",
                SourceId = 3,
                Description = "Direct supplier only",
                SupplierId = 1
            },
            new LedgerEntry
            {
                Id = 4,
                EntryDate = new DateTime(2026, 1, 4),
                Side = LedgerSide.Credit,
                AmountUsd = 999m,
                Currency = "USD",
                SourceAmount = 999m,
                SourceCurrencyCode = "USD",
                AppliedFxRateToUsd = 1m,
                SourceType = "Adjustment",
                SourceId = 4,
                Description = "Other supplier",
                SupplierId = 2
            });
        db.PaymentTransactions.Add(new PaymentTransaction
        {
            Id = 1,
            PaymentDate = new DateTime(2026, 1, 4),
            Direction = PaymentDirection.Out,
            PaymentKind = PaymentKind.SupplierPayment,
            CashAccountId = 1,
            SupplierId = 1,
            ContractId = 1,
            Amount = 100m,
            Currency = "USD",
            AppliedFxRateToUsd = 1m,
            AmountUsd = 100m,
            Reference = "SUP-PAY"
        });
    }
}
