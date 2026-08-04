using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using System.IO;
using PTGOilSystem.Web.Controllers;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.AccountStatements;
using PTGOilSystem.Web.Models.ContractBalanceTransfers;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Audit;
using PTGOilSystem.Web.Services.Exceptions;
using Xunit;

namespace PTGOilSystem.Web.Tests;

public class ContractBalanceTransfersControllerTests
{
    [Fact]
    public async Task Create_Post_Creates_One_Transfer_And_Two_LedgerEntries()
    {
        var options = NewOptions();
        await using var db = new ApplicationDbContext(options);
        var (fromContract, toContract) = await SeedContractsAsync(db);
        await SeedContractCreditAsync(db, fromContract.Id, 50m);
        // ثبت جدید از کنترلر غیرفعال شده است؛ رفتار مالی سرویس دست‌نخورده مانده و
        // مستقیم آزمایش می‌شود تا سوابق قبلی همچنان محافظت شوند.
        var service = new ContractBalanceTransferService(db);

        await service.CreateAsync(new ContractBalanceTransferCreateRequest(
            new DateTime(2026, 3, 5),
            fromContract.Id,
            toContract.Id,
            500m,
            "RUB",
            0.0125m,
            new DateTime(2026, 3, 5),
            "Manual test",
            null,
            null,
            "TR-M15-M16",
            null));

        var transfer = await db.ContractBalanceTransfers.SingleAsync();
        var entries = await db.LedgerEntries
            .Where(l => l.SourceType == ContractBalanceTransferService.LedgerSourceType && l.SourceId == transfer.Id)
            .OrderBy(l => l.Side)
            .ToListAsync();

        Assert.Equal(500m, transfer.AmountOriginal);
        Assert.Equal("RUB", transfer.CurrencyCode);
        Assert.Equal(6.25m, transfer.AmountUsd);
        Assert.Equal(2, entries.Count);
    }

    [Fact]
    public async Task Create_Post_Debits_FromContract_And_Credits_ToContract()
    {
        var options = NewOptions();
        await using var db = new ApplicationDbContext(options);
        var (fromContract, toContract) = await SeedContractsAsync(db);
        await SeedContractCreditAsync(db, fromContract.Id, 50m);
        var service = new ContractBalanceTransferService(db);

        await service.CreateAsync(new ContractBalanceTransferCreateRequest(
            new DateTime(2026, 3, 5),
            fromContract.Id,
            toContract.Id,
            500m,
            "RUB",
            0.0125m,
            new DateTime(2026, 3, 5),
            "Manual test",
            null,
            null,
            "TR-M15-M16",
            null));

        var transferDebitBalance = await db.LedgerEntries
            .Where(l => l.ContractId == fromContract.Id && l.SourceType == ContractBalanceTransferService.LedgerSourceType)
            .SumAsync(l => l.Side == LedgerSide.Credit ? l.AmountUsd : -l.AmountUsd);
        var toBalance = await db.LedgerEntries
            .Where(l => l.ContractId == toContract.Id)
            .SumAsync(l => l.Side == LedgerSide.Credit ? l.AmountUsd : -l.AmountUsd);

        Assert.Equal(-6.25m, transferDebitBalance);
        Assert.Equal(6.25m, toBalance);
        Assert.Equal(LedgerSide.Debit, await db.LedgerEntries
            .Where(l => l.ContractId == fromContract.Id && l.SourceType == ContractBalanceTransferService.LedgerSourceType)
            .Select(l => l.Side).SingleAsync());
        Assert.Equal(LedgerSide.Credit, await db.LedgerEntries.Where(l => l.ContractId == toContract.Id).Select(l => l.Side).SingleAsync());
    }

    [Fact]
    public async Task Create_Post_Uses_Current_Fx_Multiplication_Convention()
    {
        var options = NewOptions();
        await using var db = new ApplicationDbContext(options);
        var (fromContract, toContract) = await SeedContractsAsync(db);
        await SeedContractCreditAsync(db, fromContract.Id, 10m);
        var service = new ContractBalanceTransferService(db);

        var transfer = await service.CreateAsync(new ContractBalanceTransferCreateRequest(
            new DateTime(2026, 3, 5),
            fromContract.Id,
            toContract.Id,
            123.4567m,
            "RUB",
            0.012345m,
            null,
            null,
            null,
            null,
            null,
            null));

        Assert.Equal(1.5241m, transfer.AmountUsd);
        var transferEntries = await db.LedgerEntries
            .Where(l => l.SourceType == ContractBalanceTransferService.LedgerSourceType)
            .ToListAsync();
        Assert.All(transferEntries, entry => Assert.Equal(1.5241m, entry.AmountUsd));
    }

    [Fact]
    public async Task Create_Post_Converts_Document_RubPerUsd_Rate_To_UsdPerRub()
    {
        var options = NewOptions();
        await using var db = new ApplicationDbContext(options);
        var (fromContract, toContract) = await SeedContractsAsync(db);
        await SeedContractCreditAsync(db, fromContract.Id, 50m);
        var service = new ContractBalanceTransferService(db);

        // نرخ خوانا «۱ دالر = ۷۸.۴۰۰۱ روبل» به کنوانسیون داخلی (۱/perUsd) تبدیل می‌شود.
        var fxRateToUsd = decimal.Round(1m / 78.4001m, 6, MidpointRounding.AwayFromZero);
        Assert.Equal(0.012755m, fxRateToUsd);

        await service.CreateAsync(new ContractBalanceTransferCreateRequest(
            new DateTime(2026, 3, 5),
            fromContract.Id,
            toContract.Id,
            500m,
            "RUB",
            fxRateToUsd,
            null,
            null,
            null,
            null,
            "BNK-RUB-USD",
            null));

        var transfer = await db.ContractBalanceTransfers.SingleAsync();
        var entries = await db.LedgerEntries.ToListAsync();

        Assert.Equal(0.012755m, transfer.FxRateToUsd);
        Assert.Equal(6.3775m, transfer.AmountUsd);
        var transferEntries = entries.Where(e => e.SourceType == ContractBalanceTransferService.LedgerSourceType).ToList();
        Assert.All(transferEntries, entry =>
        {
            Assert.Equal(0.012755m, entry.AppliedFxRateToUsd);
            Assert.NotEqual(78.4001m, entry.AppliedFxRateToUsd);
            Assert.Equal(6.3775m, entry.AmountUsd);
        });
    }

    [Fact]
    public async Task Create_Post_For_Usd_Forces_Rate_One_And_Amount_Equals_Original()
    {
        var options = NewOptions();
        await using var db = new ApplicationDbContext(options);
        var (fromContract, toContract) = await SeedContractsAsync(db);
        await SeedContractCreditAsync(db, fromContract.Id, 600m);
        var service = new ContractBalanceTransferService(db);

        // ارز دالری: سرویس نرخ را به‌زور ۱ می‌کند حتی اگر نرخ اشتباه فرستاده شود.
        await service.CreateAsync(new ContractBalanceTransferCreateRequest(
            new DateTime(2026, 3, 5),
            fromContract.Id,
            toContract.Id,
            500m,
            "USD",
            78.4001m,
            null,
            null,
            null,
            null,
            "USD-TRANSFER",
            null));

        var transfer = await db.ContractBalanceTransfers.SingleAsync();

        Assert.Equal(1m, transfer.FxRateToUsd);
        Assert.Equal(500m, transfer.AmountUsd);
        var transferEntries2 = await db.LedgerEntries
            .Where(e => e.SourceType == ContractBalanceTransferService.LedgerSourceType)
            .ToListAsync();
        Assert.All(transferEntries2, entry =>
        {
            Assert.Equal(1m, entry.AppliedFxRateToUsd);
            Assert.Equal(500m, entry.AmountUsd);
        });
    }

    [Fact]
    public async Task Create_Post_Rejects_Same_Contract()
    {
        var options = NewOptions();
        await using var db = new ApplicationDbContext(options);
        var (fromContract, _) = await SeedContractsAsync(db);
        var service = new ContractBalanceTransferService(db);

        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => service.CreateAsync(new ContractBalanceTransferCreateRequest(
            new DateTime(2026, 3, 5),
            fromContract.Id,
            fromContract.Id,
            500m,
            "RUB",
            0.0125m,
            null,
            null,
            null,
            null,
            null,
            null)));

        Assert.Equal("CONTRACT_BALANCE_TRANSFER_SAME_CONTRACT", ex.Code);
        Assert.Empty(db.ContractBalanceTransfers);
        Assert.Empty(db.LedgerEntries);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-10)]
    public async Task Create_Post_Rejects_Zero_Or_Negative_Amount(decimal amount)
    {
        var options = NewOptions();
        await using var db = new ApplicationDbContext(options);
        var (fromContract, toContract) = await SeedContractsAsync(db);
        var service = new ContractBalanceTransferService(db);

        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => service.CreateAsync(new ContractBalanceTransferCreateRequest(
            new DateTime(2026, 3, 5),
            fromContract.Id,
            toContract.Id,
            amount,
            "RUB",
            0.0125m,
            null,
            null,
            null,
            null,
            null,
            null)));

        Assert.Equal("CONTRACT_BALANCE_TRANSFER_AMOUNT_INVALID", ex.Code);
    }

    [Theory]
    [InlineData("")]
    [InlineData("RUB")]
    public async Task Create_Post_Rejects_Missing_Currency_Or_Invalid_NonUsd_Fx(string currencyCode)
    {
        var options = NewOptions();
        await using var db = new ApplicationDbContext(options);
        var (fromContract, toContract) = await SeedContractsAsync(db);
        var service = new ContractBalanceTransferService(db);

        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => service.CreateAsync(new ContractBalanceTransferCreateRequest(
            new DateTime(2026, 3, 5),
            fromContract.Id,
            toContract.Id,
            500m,
            currencyCode,
            0m,
            null,
            null,
            null,
            null,
            null,
            null)));

        Assert.Contains("CONTRACT_BALANCE_TRANSFER_", ex.Code);
        Assert.Empty(db.ContractBalanceTransfers);
        Assert.Empty(db.LedgerEntries);
    }

    // ثبت جدید از این مسیر غیرفعال شده است: GET دیگر فورم نمی‌دهد و هیچ رکوردی نمی‌سازد.
    [Fact]
    public async Task Create_Get_Is_Disabled_And_Creates_No_Records()
    {
        var options = NewOptions();
        await using var db = new ApplicationDbContext(options);
        var (fromContract, _) = await SeedContractsAsync(db);
        var controller = BuildController(db);

        var result = controller.Create(fromContractId: fromContract.Id, returnUrl: null);

        Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(ContractBalanceTransfersController.Index), ((RedirectToActionResult)result).ActionName);
        Assert.Empty(db.ContractBalanceTransfers);
        Assert.Empty(db.LedgerEntries);
        Assert.Empty(db.PaymentTransactions);
        Assert.Empty(db.ExpenseTransactions);
        Assert.Empty(db.InventoryMovements);
        await Task.CompletedTask;
    }

    // POST هم هیچ سندی نمی‌سازد؛ مسیر انتقال اکنون «مانده قابل انتقال» تأمین‌کننده است.
    [Fact]
    public async Task Create_Post_Is_Disabled_And_Writes_Nothing()
    {
        var options = NewOptions();
        await using var db = new ApplicationDbContext(options);
        var (fromContract, toContract) = await SeedContractsAsync(db);
        await SeedContractCreditAsync(db, fromContract.Id, 50m);
        var controller = BuildController(db);

        var result = controller.Create(new ContractBalanceTransferCreateViewModel
        {
            TransferDate = new DateTime(2026, 3, 5),
            FromContractId = fromContract.Id,
            ToContractId = toContract.Id,
            AmountOriginal = 500m,
            CurrencyCode = "RUB",
            FxRateToUsd = 0.0125m
        });

        Assert.IsType<RedirectToActionResult>(result);
        Assert.Empty(db.ContractBalanceTransfers);
        Assert.False(await db.LedgerEntries.AnyAsync(
            l => l.SourceType == ContractBalanceTransferService.LedgerSourceType));
    }

    // مسیر قدیمی فقط‌خواندنی است: لینک «ثبت انتقال» برداشته شده ولی سوابق قابل دیدن مانده‌اند.
    [Fact]
    public void ContractJourney_Details_Keeps_Transfer_History_Link_Without_Create()
    {
        var financePartial = ReadRepoFile("src/PTGOilSystem.Web/Views/ContractJourney/_ContractJourneyFinanceTab.cshtml");
        var contents = ReadRepoFile("src/PTGOilSystem.Web/Views/ContractJourney/Details.cshtml") + financePartial;

        Assert.Contains("asp-controller=\"ContractBalanceTransfers\"", financePartial);
        Assert.Contains("انتقالات مانده این قرارداد", contents);
        Assert.DoesNotContain("asp-route-fromContractId=\"@Model.ContractId\"", financePartial);
    }

    // کارت «مانده قابل انتقال» و مسیر جدید در جزئیات تأمین‌کننده حاضر است.
    [Fact]
    public void Supplier_Details_Exposes_Transferable_Balance_Card()
    {
        var details = ReadRepoFile("src/PTGOilSystem.Web/Views/Suppliers/Details.cshtml");
        var card = ReadRepoFile("src/PTGOilSystem.Web/Views/Suppliers/_SupplierTransferableBalanceCard.cshtml");

        Assert.Contains("_SupplierTransferableBalanceCard.cshtml", details);
        Assert.Contains("پیش‌پرداخت آزاد تأمین‌کننده", card);
        Assert.Contains("انتقال به قرارداد", card);
        Assert.Contains("سوابق انتقال", card);
        Assert.Contains("asp-controller=\"SupplierBalanceTransfers\"", card);
    }

    [Fact]
    public async Task Contract_Account_Statement_Shows_Transfer_From_Ledger_Without_Duplicate()
    {
        var options = NewOptions();
        await using var db = new ApplicationDbContext(options);
        var (fromContract, toContract) = await SeedContractsAsync(db);
        await SeedContractCreditAsync(db, fromContract.Id, 50m);
        var service = new ContractBalanceTransferService(db);
        await service.CreateAsync(new ContractBalanceTransferCreateRequest(
            new DateTime(2026, 3, 5),
            fromContract.Id,
            toContract.Id,
            500m,
            "RUB",
            0.0125m,
            null,
            null,
            null,
            null,
            "TR-M15-M16",
            "Carry forward balance"));
        var controller = BuildStatementController(db);

        var result = await controller.Contract(fromContract.Id);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<ContractAccountStatementViewModel>(view.Model);
        var transferRow = model.Rows.Single(r => r.SourceType == ContractBalanceTransferService.LedgerSourceType);
        Assert.Equal("TR-M15-M16", transferRow.Reference);
        Assert.Contains("Transfer to contract M-16", transferRow.Description);
        Assert.Equal("M-16", transferRow.RelatedContractNumber);
        Assert.Equal(6.25m, transferRow.OutflowUsd);
        Assert.Null(transferRow.ReceiptUsd);
        Assert.Equal(-(50m - 6.25m), model.Totals.BalanceUsd);
    }

    [Fact]
    public void ContractBalanceTransfer_Does_Not_Depend_On_StockService()
    {
        var service = ReadRepoFile("src/PTGOilSystem.Web/Services/ContractBalanceTransferService.cs");
        var controller = ReadRepoFile("src/PTGOilSystem.Web/Controllers/ContractBalanceTransfersController.cs");

        Assert.DoesNotContain("IStockService", service);
        Assert.DoesNotContain("StockService", service);
        Assert.DoesNotContain("IStockService", controller);
        Assert.DoesNotContain("StockService", controller);
    }

    [Fact]
    public void AddContractBalanceTransfers_Migration_Is_Additive_Only()
    {
        var migration = ReadRepoFile("src/PTGOilSystem.Web/Migrations/20260513154138_AddContractBalanceTransfers.cs");
        var upStart = migration.IndexOf("protected override void Up", StringComparison.Ordinal);
        var downStart = migration.IndexOf("protected override void Down", StringComparison.Ordinal);
        var up = migration[upStart..downStart];

        Assert.Contains("CreateTable(", up);
        Assert.Contains("name: \"ContractBalanceTransfers\"", up);
        Assert.Contains("CreateIndex(", up);
        Assert.DoesNotContain("DropTable", up);
        Assert.DoesNotContain("DropColumn", up);
        Assert.DoesNotContain("AlterColumn", up);
        Assert.DoesNotContain("Rename", up);
        Assert.DoesNotContain("AddColumn", up);
    }

    private static ContractBalanceTransfersController BuildController(ApplicationDbContext db)
        => new(db, new ContractBalanceTransferService(db))
        {
            TempData = new TempDataDictionary(new DefaultHttpContext(), new TestTempDataProvider())
        };

    private static AccountStatementsController BuildStatementController(ApplicationDbContext db)
        => new(db, new PricingService(db), new AuditService(db))
        {
            TempData = new TempDataDictionary(new DefaultHttpContext(), new TestTempDataProvider())
        };

    private static DbContextOptions<ApplicationDbContext> NewOptions()
        => new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

    [Fact]
    public async Task CreateAsync_Throws_When_FromContract_Has_Insufficient_Balance()
    {
        var options = NewOptions();
        await using var db = new ApplicationDbContext(options);
        var (fromContract, toContract) = await SeedContractsAsync(db);
        await SeedContractCreditAsync(db, fromContract.Id, 5m);
        var service = new ContractBalanceTransferService(db);

        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => service.CreateAsync(
            new ContractBalanceTransferCreateRequest(
                new DateTime(2026, 3, 5),
                fromContract.Id,
                toContract.Id,
                1000m,
                "USD",
                1m,
                null,
                null,
                null,
                null,
                "OVER-TRANSFER",
                null)));

        Assert.Equal("CONTRACT_BALANCE_TRANSFER_INSUFFICIENT_BALANCE", ex.Code);
        Assert.Empty(db.ContractBalanceTransfers);
    }

    [Fact]
    public async Task CreateAsync_Throws_When_FromContract_Has_Zero_Balance()
    {
        var options = NewOptions();
        await using var db = new ApplicationDbContext(options);
        var (fromContract, toContract) = await SeedContractsAsync(db);
        var service = new ContractBalanceTransferService(db);

        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => service.CreateAsync(
            new ContractBalanceTransferCreateRequest(
                new DateTime(2026, 3, 5),
                fromContract.Id,
                toContract.Id,
                10m,
                "USD",
                1m,
                null,
                null,
                null,
                null,
                "ZERO-BALANCE",
                null)));

        Assert.Equal("CONTRACT_BALANCE_TRANSFER_INSUFFICIENT_BALANCE", ex.Code);
    }

    [Fact]
    public async Task CreateAsync_Succeeds_When_AmountUsd_Equals_Exact_Balance()
    {
        var options = NewOptions();
        await using var db = new ApplicationDbContext(options);
        var (fromContract, toContract) = await SeedContractsAsync(db);
        await SeedContractCreditAsync(db, fromContract.Id, 100m);
        var service = new ContractBalanceTransferService(db);

        var transfer = await service.CreateAsync(new ContractBalanceTransferCreateRequest(
            new DateTime(2026, 3, 5),
            fromContract.Id,
            toContract.Id,
            100m,
            "USD",
            1m,
            null,
            null,
            null,
            null,
            "EXACT-BALANCE",
            null));

        Assert.Equal(100m, transfer.AmountUsd);
        var netBalance = await service.GetContractNetBalanceUsdAsync(fromContract.Id);
        Assert.Equal(0m, netBalance);
    }

    [Fact]
    public async Task GetContractNetBalanceUsdAsync_Returns_Net_Credit_Minus_Debit()
    {
        var options = NewOptions();
        await using var db = new ApplicationDbContext(options);
        var (fromContract, _) = await SeedContractsAsync(db);
        await SeedContractCreditAsync(db, fromContract.Id, 200m);
        var service = new ContractBalanceTransferService(db);

        var balance = await service.GetContractNetBalanceUsdAsync(fromContract.Id);

        Assert.Equal(200m, balance);
    }

    [Fact]
    public void Payment_DocumentCurrencyPerUsdRate_RoundTrips_To_PerUsdDisplay()
    {
        decimal documentRate = 91.3398m;
        decimal stored = 1m / documentRate;
        string display = (1m / stored).ToString("N4");
        Assert.Equal("91.3398", display);
    }

    [Fact]
    public async Task FullScenario_BnkHimor_Transfer_Rejected_When_Exceeds_Net_Balance()
    {
        var options = NewOptions();
        await using var db = new ApplicationDbContext(options);
        var (fromContract, toContract) = await SeedContractsAsync(db);
        await SeedBnkHimorLedgerAsync(db, fromContract.Id);
        var service = new ContractBalanceTransferService(db);

        var netBalance = await service.GetContractNetBalanceUsdAsync(fromContract.Id);
        Assert.Equal(1100m, netBalance);

        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => service.CreateAsync(
            new ContractBalanceTransferCreateRequest(
                new DateTime(2026, 5, 10),
                fromContract.Id,
                toContract.Id,
                1200m,
                "USD",
                1m,
                null, null, null, null,
                "OVER-BALANCE",
                null)));

        Assert.Equal("CONTRACT_BALANCE_TRANSFER_INSUFFICIENT_BALANCE", ex.Code);
        var balanceAfter = await service.GetContractNetBalanceUsdAsync(fromContract.Id);
        Assert.Equal(1100m, balanceAfter);
    }

    [Fact]
    public async Task FullScenario_BnkHimor_Transfer_Exact_Balance_Succeeds_And_Zeros_Account()
    {
        var options = NewOptions();
        await using var db = new ApplicationDbContext(options);
        var (fromContract, toContract) = await SeedContractsAsync(db);
        await SeedBnkHimorLedgerAsync(db, fromContract.Id);
        var service = new ContractBalanceTransferService(db);

        var transfer = await service.CreateAsync(
            new ContractBalanceTransferCreateRequest(
                new DateTime(2026, 5, 10),
                fromContract.Id,
                toContract.Id,
                1100m,
                "USD",
                1m,
                null, null, null, null,
                "FULL-BALANCE",
                null));

        Assert.Equal(1100m, transfer.AmountUsd);

        var balanceFrom = await service.GetContractNetBalanceUsdAsync(fromContract.Id);
        Assert.Equal(0m, balanceFrom);

        var balanceTo = await service.GetContractNetBalanceUsdAsync(toContract.Id);
        Assert.Equal(1100m, balanceTo);
    }

    private static async Task SeedBnkHimorLedgerAsync(ApplicationDbContext db, int contractId)
    {
        var entries = new[]
        {
            new LedgerEntry
            {
                EntryDate = new DateTime(2026, 3, 1),
                Side = LedgerSide.Credit,
                AmountUsd = 10900m,
                Currency = "USD",
                SourceAmount = 1000000m,
                SourceCurrencyCode = "RUB",
                AppliedFxRateToUsd = 1m / 91.7431m,
                AppliedFxRateDate = new DateTime(2026, 3, 1),
                Description = "پرداخت به HIMOR — قرارداد M-15",
                SourceType = nameof(PaymentKind.SupplierPayment),
                SourceId = 1001,
                ContractId = contractId
            },
            new LedgerEntry
            {
                EntryDate = new DateTime(2026, 3, 15),
                Side = LedgerSide.Debit,
                AmountUsd = 9000m,
                Currency = "USD",
                SourceAmount = 9000m,
                SourceCurrencyCode = "USD",
                AppliedFxRateToUsd = 1m,
                AppliedFxRateDate = new DateTime(2026, 3, 15),
                Description = "مصرف بار — قرارداد M-15",
                SourceType = "ContractCost",
                SourceId = 2001,
                ContractId = contractId
            },
            new LedgerEntry
            {
                EntryDate = new DateTime(2026, 3, 20),
                Side = LedgerSide.Debit,
                AmountUsd = 500m,
                Currency = "USD",
                SourceAmount = 500m,
                SourceCurrencyCode = "USD",
                AppliedFxRateToUsd = 1m,
                AppliedFxRateDate = new DateTime(2026, 3, 20),
                Description = "جریمه تأخیر — Demurrage M-15",
                SourceType = "Demurrage",
                SourceId = 3001,
                ContractId = contractId
            },
            new LedgerEntry
            {
                EntryDate = new DateTime(2026, 4, 1),
                Side = LedgerSide.Debit,
                AmountUsd = 300m,
                Currency = "USD",
                SourceAmount = 300m,
                SourceCurrencyCode = "USD",
                AppliedFxRateToUsd = 1m,
                AppliedFxRateDate = new DateTime(2026, 4, 1),
                Description = "Offset — اصلاح حساب M-15",
                SourceType = "Adjustment",
                SourceId = 4001,
                ContractId = contractId
            }
        };

        db.LedgerEntries.AddRange(entries);
        await db.SaveChangesAsync();
    }

    private static async Task SeedContractCreditAsync(ApplicationDbContext db, int contractId, decimal amountUsd)
    {
        db.LedgerEntries.Add(new LedgerEntry
        {
            EntryDate = new DateTime(2026, 1, 1),
            Side = LedgerSide.Credit,
            AmountUsd = amountUsd,
            Currency = "USD",
            SourceAmount = amountUsd,
            SourceCurrencyCode = "USD",
            AppliedFxRateToUsd = 1m,
            AppliedFxRateDate = new DateTime(2026, 1, 1),
            Description = "Test seed credit",
            SourceType = nameof(PaymentKind.SupplierPayment),
            SourceId = 9999,
            ContractId = contractId
        });
        await db.SaveChangesAsync();
    }

    private static async Task<(Contract FromContract, Contract ToContract)> SeedContractsAsync(ApplicationDbContext db)
    {
        db.Products.Add(new Product { Id = 1, Code = "G92", Name = "Gasoline 92", UnitOfMeasure = "MT", IsActive = true });
        db.Companies.Add(new Company { Id = 1, Code = "PTG", Name = "PTG", Country = "AF", IsActive = true });
        db.Suppliers.Add(new Supplier { Id = 1, Code = "HIMOR", Name = "HIMOR", IsActive = true });

        var fromContract = new Contract
        {
            Id = 1,
            ContractNumber = "M-15",
            ContractType = ContractType.Purchase,
            Status = ContractStatus.Active,
            CompanyId = 1,
            ProductId = 1,
            SupplierId = 1,
            ContractDate = new DateTime(2026, 1, 1),
            PricingMethod = PricingMethod.ManualFinalPrice,
            QuantityMt = 1000m,
            Currency = "RUB"
        };
        var toContract = new Contract
        {
            Id = 2,
            ContractNumber = "M-16",
            ContractType = ContractType.Purchase,
            Status = ContractStatus.Active,
            CompanyId = 1,
            ProductId = 1,
            SupplierId = 1,
            ContractDate = new DateTime(2026, 1, 2),
            PricingMethod = PricingMethod.ManualFinalPrice,
            QuantityMt = 800m,
            Currency = "RUB"
        };

        db.Contracts.AddRange(fromContract, toContract);
        await db.SaveChangesAsync();
        return (fromContract, toContract);
    }

    private static string ReadRepoFile(string relativePath)
    {
        var path = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            relativePath.Replace('/', Path.DirectorySeparatorChar)));

        return File.ReadAllText(path);
    }

    private sealed class TestTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();

        public void SaveTempData(HttpContext context, IDictionary<string, object> values)
        {
        }
    }
}
