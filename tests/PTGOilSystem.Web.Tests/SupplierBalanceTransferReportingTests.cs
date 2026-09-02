using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PTGOilSystem.Web.Controllers;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.PartyStatements;
using PTGOilSystem.Web.Models.Suppliers;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Audit;
using PTGOilSystem.Web.Services.CompanyFlow;
using PTGOilSystem.Web.Services.DeleteSafety;
using PTGOilSystem.Web.Services.PartyStatements;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// گزارش‌گیریِ «انتقال پیش‌پرداخت آزاد به قرارداد» — با ارقام واقعیِ P-009.
///
/// سناریوی مرجع (همان داده‌ای که در محیط واقعی اشتباه گزارش می‌شد):
///   • پرداخت قبلی روی قرارداد: 500,000,000 RUB = 6,377,542.8858 USD
///   • پیش‌پرداخت آزاد (بدون قرارداد): 200,000,000 RUB = 2,551,017.1543 USD
///   • انتقال به قرارداد: 101,857,663.2 RUB — ارزش تاریخی 1,299,203.2306 USD،
///     ارزش روز 1,299,203.2307 USD، تفاوت نرخ 0.0001 USD
///
/// انتظار:
///   • مجموع پرداخت قرارداد: 601,857,663.2 RUB
///   • مانده قرارداد: +13,440,435.8 RUB (بیشتر از ارزش بارگیری پرداخت شده)
///   • باقی‌ماندهٔ پیش‌پرداخت آزاد: 98,142,336.8 RUB = 1,251,813.9237 USD
///   • مجموع پرداخت تأمین‌کننده دست‌نخورده: 700,000,000 RUB (انتقال داخلی است)
/// </summary>
public sealed class SupplierBalanceTransferReportingTests
{
    private const int SupplierId = 1;
    private const int ContractId = 1;
    private const int SarrafId = 7;

    private const decimal ContractLoadedRub = 588_417_227.40m;
    private const decimal ContractLoadedUsd = 7_505_310.58m;

    private const decimal ContractPaymentRub = 500_000_000m;
    private const decimal ContractPaymentUsd = 6_377_542.8858m;

    private const decimal FreePrepaymentRub = 200_000_000m;
    private const decimal FreePrepaymentUsd = 2_551_017.1543m;

    private const decimal TransferRub = 101_857_663.2m;
    private const decimal TransferHistoricalUsd = 1_299_203.2306m;
    private const decimal TransferValueUsd = 1_299_203.2307m;
    private const decimal TransferFxToUsd = 0.012755085771m;

    private const decimal ExpectedContractPaidRub = 601_857_663.2m;      // 500,000,000 + 101,857,663.2
    private const decimal ExpectedContractBalanceRub = 13_440_435.8m;    // پرداخت − ارزش بارگیری
    private const decimal ExpectedFreeRemainingRub = 98_142_336.8m;      // 200,000,000 − 101,857,663.2
    private const decimal ExpectedFreeRemainingUsd = 1_251_813.9237m;    // 2,551,017.1543 − 1,299,203.2306
    private const decimal ExpectedSupplierPaidRub = 700_000_000m;        // 500,000,000 + 200,000,000

    // خلاصهٔ قراردادها: انتقال باید به‌عنوان تخصیص روی قرارداد مقصد دیده شود و مانده
    // قرارداد را از −88,417,227.4 به +13,440,435.8 برساند.
    [Fact]
    public async Task ContractSummary_CountsTransferAsPaymentOnDestinationContract()
    {
        await using var db = NewDb();
        Seed(db);
        await db.SaveChangesAsync();

        var model = await LoadProfileAsync(db);
        var contract = Assert.Single(model.Contracts);

        Assert.Equal(ExpectedContractPaidRub, contract.PaidRub);
        Assert.Equal(ContractPaymentUsd + TransferValueUsd, contract.PaidUsd);
        Assert.Equal(ContractLoadedRub, contract.LoadedValueRub);
        Assert.Equal(ExpectedContractBalanceRub, -contract.LoadedValueBalanceRub!.Value);
    }

    // انتقال داخلی است: مجموع پرداختِ خودِ تأمین‌کننده نباید یک ریال جابه‌جا شود.
    [Fact]
    public async Task SupplierTotals_AreNotChangedByTheInternalTransfer()
    {
        await using var db = NewDb();
        Seed(db);
        await db.SaveChangesAsync();

        var withTransfer = await LoadProfileAsync(db);

        await using var dbWithout = NewDb();
        Seed(dbWithout, includeTransfer: false);
        await dbWithout.SaveChangesAsync();
        var withoutTransfer = await LoadProfileAsync(dbWithout);

        Assert.Equal(ExpectedSupplierPaidRub, withTransfer.TotalPaidRub);
        Assert.Equal(withoutTransfer.TotalPaidRub, withTransfer.TotalPaidRub);
        Assert.Equal(withoutTransfer.TotalPaidUsd, withTransfer.TotalPaidUsd);
    }

    // انتقال در تب پرداخت‌ها دیده می‌شود ولی به‌عنوان «انتقال مانده»، نه پرداخت جدید.
    [Fact]
    public async Task PaymentLines_ShowTheTransferWithItsOriginalRubAmount()
    {
        await using var db = NewDb();
        Seed(db);
        await db.SaveChangesAsync();

        var model = await LoadProfileAsync(db);
        var line = Assert.Single(model.PaymentLines.Where(l => l.IsBalanceTransfer));

        Assert.Equal(TransferRub, line.Amount);
        Assert.Equal("RUB", line.Currency);
        Assert.Equal(TransferValueUsd, line.AmountUsd);
        Assert.Equal("P-009", line.ContractNumber);
    }

    // پیش‌پرداخت آزاد دقیقاً به اندازهٔ انتقال کم می‌شود — در هر دو ارز.
    [Fact]
    public async Task FreePrepayment_IsReducedByExactlyTheTransferredAmount()
    {
        await using var db = NewDb();
        Seed(db);
        await db.SaveChangesAsync();

        var balance = await new SupplierTransferableBalanceService(db).GetAsync(SupplierId);
        var pool = Assert.Single(balance.AllPools.Where(p => p.HasTransferable));
        var bucket = Assert.Single(pool.Buckets.Where(b => b.CurrencyCode == "RUB"));

        Assert.Equal(ExpectedFreeRemainingRub, bucket.RemainingOriginalAmount);
        Assert.Equal(ExpectedFreeRemainingUsd, bucket.RemainingBookAmountUsd);
    }

    // جزئیات USD: انتقال با ارزش دالریِ خودش دیده می‌شود و ارزش تاریخی از پیش‌پرداخت آزاد
    // خارج می‌شود. مبلغ اصلی سطر دالری دست‌نخورده می‌ماند.
    [Fact]
    public async Task UsdStatement_ShowsTheTransferAtItsUsdValue()
    {
        await using var db = NewDb();
        Seed(db);
        await db.SaveChangesAsync();

        var statement = await BuildStatementAsync(db, "USD");
        var transferRows = statement.Rows
            .Where(r => r.SourceType == SupplierBalanceTransferService.LedgerSourceType)
            .ToList();

        // نمای دالری فقط اسناد دالری را می‌آورد: پای مقصد (ارز قرارداد USD) می‌ماند و
        // پای منبع که در ارز روبل ثبت شده وارد این نما نمی‌شود.
        var destination = Assert.Single(transferRows);
        Assert.Equal(ContractId, destination.ContractId);
        Assert.Equal(TransferValueUsd, destination.OutflowBase);
        // اصلاح روبلی نباید به نمای دالری نشت کند: مبلغ و ارز اصلی همان دالر می‌ماند.
        Assert.Equal("USD", destination.OriginalCurrency);
        Assert.Equal(TransferValueUsd, destination.OriginalAmount);

        // ارزش تاریخیِ خارج‌شده از مانده آزاد، از خودِ سند انتقال.
        var transfer = await db.SupplierBalanceTransfers.AsNoTracking()
            .Include(t => t.Sources)
            .SingleAsync();
        Assert.Equal(TransferHistoricalUsd, transfer.HistoricalAmountUsd);
        Assert.Equal(TransferHistoricalUsd, transfer.Sources.Sum(s => s.ConsumedBookAmountUsd));
    }

    // جزئیات RUB: پای مقصد با ارز قرارداد (USD) ثبت شده، پس مبلغ روبلی باید مستقیماً از
    // خودِ سند SupplierBalanceTransfer خوانده شود، نه از SourceAmount سطر دفتر.
    [Fact]
    public async Task RubStatement_ReadsTheTransferAmountFromTheTransferDocument()
    {
        await using var db = NewDb();
        Seed(db);
        await db.SaveChangesAsync();

        var statement = await BuildStatementAsync(db, "RUB");
        var transferRows = statement.Rows
            .Where(r => r.SourceType == SupplierBalanceTransferService.LedgerSourceType)
            .ToList();
        Assert.Equal(2, transferRows.Count);

        var destination = Assert.Single(transferRows.Where(r => r.ContractId == ContractId));
        var source = Assert.Single(transferRows.Where(r => r.ContractId is null));

        // هر دو پا همان یک مبلغ روبلی را نشان می‌دهند: از پیش‌پرداخت آزاد بیرون، روی قرارداد داخل.
        Assert.Equal("RUB", destination.OriginalCurrency);
        Assert.Equal(TransferRub, destination.OriginalAmount);
        Assert.Equal(TransferRub, destination.OutflowRub);
        Assert.Equal(TransferRub, source.OriginalAmount);
        Assert.Equal(TransferRub, source.ReceiptRub);
    }

    // نمای «قراردادها»ی صورت‌حساب روبلی: انتقال یک بار و با جهت درست شمرده می‌شود.
    [Fact]
    public async Task RubContractGrouping_CountsTheTransferOnceAndWithTheRightSign()
    {
        await using var db = NewDb();
        Seed(db);
        await db.SaveChangesAsync();

        var statement = await BuildStatementAsync(db, "RUB");
        var grouping = SupplierContractStatementBuilder.Build(
            statement,
            new Dictionary<int, SupplierContractStatementBuilder.ContractFacts>
            {
                [ContractId] = new(
                    ProductName: "gasoil",
                    ContractQuantityMt: 14_170m,
                    UnitPriceUsd: 530m,
                    ContractValueUsd: 14_170m * 530m,
                    LoadedQuantityMt: 14_145.75m)
            });

        var contractRow = Assert.Single(grouping.Rows.Where(r => r.ContractId == ContractId));
        var freeRow = Assert.Single(grouping.Rows.Where(r => r.ContractId is null));

        Assert.Equal(ExpectedContractPaidRub, contractRow.SettlementTotalRub);
        Assert.Equal(ContractLoadedRub, contractRow.ConfirmedValueRub);
        Assert.Equal(ExpectedContractBalanceRub, contractRow.BalanceRub);

        // پیش‌پرداخت آزاد: 200,000,000 خارج‌شده منهای 101,857,663.2 که به قرارداد رفت.
        Assert.Equal(ExpectedFreeRemainingRub, freeRow.SettlementTotalRub);
        Assert.Equal(ExpectedFreeRemainingRub, freeRow.BalanceRub);

        // انتقال داخلی مجموع پرداخت روبلی تأمین‌کننده را تغییر نمی‌دهد.
        Assert.Equal(ExpectedSupplierPaidRub, grouping.TotalSettlementRub);
    }

    // مانده قرارداد در همهٔ صفحات یک عنوان و یک مقدار دارد — بدون علامت مثبت/منفی.
    // P-009 اضافه‌پرداخت دارد: 13,440,435.80 RUB.
    [Fact]
    public async Task ContractBalance_IsShownWithTheSameTitleAndValue_Everywhere()
    {
        await using var db = NewDb();
        Seed(db);
        await db.SaveChangesAsync();

        const string overpaidTitle = "اضافه‌پرداخت قرارداد";

        // ۱) خلاصهٔ قراردادها (صفحهٔ تأمین‌کننده)
        var summaryRow = Assert.Single((await LoadProfileAsync(db)).Contracts);
        Assert.Equal(overpaidTitle, summaryRow.BalanceTitleFor(isRub: true));
        Assert.Equal(ExpectedContractBalanceRub, summaryRow.BalanceAbsoluteFor(isRub: true));

        // ۲) صورت‌حساب (نمای قراردادها) — همین اعداد به PDF و Excel هم می‌روند.
        var grouping = BuildGrouping(await BuildStatementAsync(db, "RUB"));
        var statementRow = Assert.Single(grouping.Rows.Where(r => r.ContractId == ContractId));
        Assert.Equal(overpaidTitle, statementRow.BalanceTitleFor(grouping.IsRub));
        Assert.Equal(ExpectedContractBalanceRub, statementRow.BalanceAbsoluteFor(grouping.IsRub));

        // ۳) Excel — بیلانس signed همان قرارداد.
        var excel = SupplierStatementExport.BuildSummaryDocument(
            await BuildStatementAsync(db, "RUB"), grouping, "Statement", "RUB", [], isEnglish: false);
        var balanceIndex = excel.Columns.ToList().FindIndex(c => c.TitleFa == "بیلانس قرارداد");
        // ستون «قرارداد» همان عنوانی است که جدول صفحه نشان می‌دهد: «قرارداد #P-009».
        var excelRow = Assert.Single(excel.Rows.Where(r =>
            ((string?)r.Cells[1].Value)?.Contains("P-009", StringComparison.Ordinal) == true));
        Assert.Equal(ExpectedContractBalanceRub, excelRow.Cells[balanceIndex].Value);

        // هر سه منبع دقیقاً یک عدد و یک عنوان می‌دهند.
        Assert.Equal(summaryRow.BalanceAbsoluteFor(true), statementRow.BalanceAbsoluteFor(grouping.IsRub));
        Assert.Equal(summaryRow.BalanceTitleFor(true), statementRow.BalanceTitleFor(grouping.IsRub));
    }

    // قرارداد بدهکار عنوان مقابل را می‌گیرد و باز هم عدد بدون علامت است.
    [Fact]
    public async Task ContractBalance_WithoutTheTransfer_ReadsAsPayableToSupplier()
    {
        await using var db = NewDb();
        Seed(db, includeTransfer: false);
        await db.SaveChangesAsync();

        var summaryRow = Assert.Single((await LoadProfileAsync(db)).Contracts);
        Assert.Equal("قابل پرداخت به تأمین‌کننده", summaryRow.BalanceTitleFor(isRub: true));
        Assert.Equal(88_417_227.4m, summaryRow.BalanceAbsoluteFor(isRub: true));

        var grouping = BuildGrouping(await BuildStatementAsync(db, "RUB"));
        var statementRow = Assert.Single(grouping.Rows.Where(r => r.ContractId == ContractId));
        Assert.Equal("قابل پرداخت به تأمین‌کننده", statementRow.BalanceTitleFor(grouping.IsRub));
        Assert.Equal(88_417_227.4m, statementRow.BalanceAbsoluteFor(grouping.IsRub));
    }

    private static SupplierContractStatementViewModel BuildGrouping(PartyStatementResult statement)
        => SupplierContractStatementBuilder.Build(
            statement,
            new Dictionary<int, SupplierContractStatementBuilder.ContractFacts>
            {
                [ContractId] = new(
                    ProductName: "gasoil",
                    ContractQuantityMt: 14_170m,
                    UnitPriceUsd: 530m,
                    ContractValueUsd: 14_170m * 530m,
                    LoadedQuantityMt: 14_145.75m)
            });

    private static async Task<PartyStatementResult> BuildStatementAsync(
        ApplicationDbContext db,
        string currency)
        => await new PartyStatementReadService(
                db,
                new PartyStatementPolicyResolver(),
                new CompanyFlowDirectionResolver(),
                new CompanyFlowBalanceService(),
                Options.Create(new PartyStatementOptions()))
            .GetStatementAsync(
                new PartyRef(PartyStatementPartyType.Supplier, SupplierId),
                new PartyStatementFilter { CurrencyCode = currency, IncludeOperationalColumns = false });

    private static async Task<SupplierProfileViewModel> LoadProfileAsync(ApplicationDbContext db)
    {
        var controller = new SuppliersController(db, new AuditService(db), new MasterDataDeleteSafetyService(db));
        var result = await controller.Details(SupplierId);
        return Assert.IsType<SupplierProfileViewModel>(Assert.IsType<ViewResult>(result).Model);
    }

    private static ApplicationDbContext NewDb()
        => new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static void Seed(ApplicationDbContext db, bool includeTransfer = true)
    {
        db.Companies.Add(new Company { Id = 1, Code = "BNK", Name = "BNK Khumor LLC" });
        db.Products.Add(new Product { Id = 1, Code = "GO", Name = "gasoil" });
        db.Suppliers.Add(new Supplier { Id = SupplierId, Code = "SU004", Name = "BNK-FZCO" });
        db.Sarrafs.Add(new Sarraf { Id = SarrafId, Name = "Sarraf" });
        db.Contracts.Add(new Contract
        {
            Id = ContractId,
            ContractNumber = "P-009",
            ContractName = "M-32",
            ContractType = ContractType.Purchase,
            Status = ContractStatus.Active,
            CompanyId = 1,
            SupplierId = SupplierId,
            ProductId = 1,
            ContractDate = new DateTime(2025, 12, 1),
            QuantityMt = 14_170m,
            PricingMethod = PricingMethod.Fixed,
            UnitPriceInCurrency = 530m,
            UnitPriceUsd = 530m,
            Currency = "USD",
            SettlementCurrencyCode = "USD",
            RubRatePolicy = RubSettlementRatePolicy.NotApplicable,
            AppliedFxRateToUsd = 1m
        });

        // یک بارگیری با رقم روبلی واقعی — جمع ارزش خرید قرارداد.
        db.LoadingRegisters.Add(new LoadingRegister
        {
            Id = 1,
            ContractId = ContractId,
            ProductId = 1,
            LoadingDate = new DateTime(2025, 12, 28),
            LoadedQuantityMt = 14_145.75m,
            LoadingPriceUsd = 530.56m,
            SettlementCurrencyCode = "USD",
            RubRateStatus = RubSettlementRateStatus.NotRequired,
            SettlementValueRub = ContractLoadedRub
        });
        db.LedgerEntries.Add(new LedgerEntry
        {
            Id = 100,
            EntryDate = new DateTime(2025, 12, 28),
            Side = LedgerSide.Credit,
            SourceType = "Loading",
            SourceId = 1,
            SupplierId = SupplierId,
            ContractId = ContractId,
            Currency = "RUB",
            SourceCurrencyCode = "RUB",
            SourceAmount = ContractLoadedRub,
            AmountUsd = ContractLoadedUsd,
            Description = "بارگیری"
        });

        // پرداخت قبلیِ همین قرارداد از طریق صراف: 500,000,000 RUB.
        AddViaSarrafPayment(db, id: 200, contractId: ContractId, rub: ContractPaymentRub, usd: ContractPaymentUsd);

        // پیش‌پرداخت آزاد بدون قرارداد: 200,000,000 RUB.
        AddViaSarrafPayment(db, id: 202, contractId: null, rub: FreePrepaymentRub, usd: FreePrepaymentUsd);

        if (!includeTransfer)
        {
            return;
        }

        db.SupplierBalanceTransfers.Add(new SupplierBalanceTransfer
        {
            Id = 1,
            BatchId = Guid.NewGuid(),
            SupplierId = SupplierId,
            CompanyId = 1,
            ContractId = ContractId,
            TransferDate = new DateTime(2026, 8, 4),
            TransferOriginalAmount = TransferRub,
            OriginalCurrencyCode = "RUB",
            HistoricalFxRateToUsd = TransferFxToUsd,
            HistoricalAmountUsd = TransferHistoricalUsd,
            TransferPerUsdRate = 78.4001m,
            TransferFxRateToUsd = TransferFxToUsd,
            TransferValueUsd = TransferValueUsd,
            ExchangeDifferenceUsd = 0.0001m,
            ExchangeDifferenceType = SarrafSettlementDifferenceType.Gain,
            ContractCurrencyCode = "USD",
            ContractCurrencyPerUsdRate = 1m,
            ContractCurrencyFxRateToUsd = 1m,
            TransferContractCurrencyAmount = TransferValueUsd,
            Status = SupplierBalanceTransferStatus.Active,
            Sources =
            [
                new SupplierBalanceTransferSource
                {
                    SourceType = PaymentsController.ViaSarrafSupplierLedgerSourceType,
                    SourceId = SarrafId,
                    LedgerEntryId = 202,
                    SourceDate = new DateTime(2026, 8, 4),
                    ConsumedOriginalAmount = TransferRub,
                    OriginalCurrencyCode = "RUB",
                    HistoricalFxRateToUsd = TransferFxToUsd,
                    ConsumedBookAmountUsd = TransferHistoricalUsd
                }
            ]
        });

        // پای منبع: کاهش مانده قابل انتقال به ارزش تاریخی، در ارز خودِ مانده.
        db.LedgerEntries.Add(new LedgerEntry
        {
            Id = 300,
            EntryDate = new DateTime(2026, 8, 4),
            Side = LedgerSide.Credit,
            SourceType = SupplierBalanceTransferService.LedgerSourceType,
            SourceId = 1,
            SupplierId = SupplierId,
            Currency = "RUB",
            SourceCurrencyCode = "RUB",
            SourceAmount = TransferRub,
            AmountUsd = TransferHistoricalUsd,
            AppliedFxRateToUsd = TransferFxToUsd,
            Description = "کاهش مانده قابل انتقال"
        });

        // پای مقصد: با ارز قرارداد (USD) ثبت می‌شود — منشأ خالی‌بودن ستون روبل در گزارش.
        db.LedgerEntries.Add(new LedgerEntry
        {
            Id = 301,
            EntryDate = new DateTime(2026, 8, 4),
            Side = LedgerSide.Debit,
            SourceType = SupplierBalanceTransferService.LedgerSourceType,
            SourceId = 1,
            SupplierId = SupplierId,
            ContractId = ContractId,
            Currency = "USD",
            SourceCurrencyCode = "USD",
            SourceAmount = TransferValueUsd,
            AmountUsd = TransferValueUsd,
            AppliedCurrencyPerUsdRate = 1m,
            AppliedFxRateToUsd = 1m,
            Description = "انتقال مانده به قرارداد"
        });
    }

    private static void AddViaSarrafPayment(
        ApplicationDbContext db,
        int id,
        int? contractId,
        decimal rub,
        decimal usd)
    {
        db.LedgerEntries.Add(new LedgerEntry
        {
            Id = id,
            EntryDate = new DateTime(2026, 8, 4),
            Side = LedgerSide.Debit,
            SourceType = PaymentsController.ViaSarrafSupplierLedgerSourceType,
            SourceId = SarrafId,
            SupplierId = SupplierId,
            ContractId = contractId,
            Currency = "RUB",
            SourceCurrencyCode = "RUB",
            SourceAmount = rub,
            AmountUsd = usd,
            ViaSarrafGroupId = Guid.NewGuid(),
            Description = "پرداخت تأمین‌کننده از طریق صراف"
        });
    }
}
