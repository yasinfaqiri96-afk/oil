using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Exceptions;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// نرخ ارز «مانده قابل انتقال» تأمین‌کننده.
///
/// اشکالی که این تست‌ها قفل می‌کنند: نرخ ذخیره نمی‌شد و از روی مبالغ بازسازی می‌شد
/// (‎1 / (bookUsd / original)‎). چون 1/70 اعشار متناهی ندارد، نرخ ۷۰ همیشه ۶۹.۹۹۸۶ برمی‌گشت
/// و همان عدد منحرف به‌عنوان نرخ پیش‌فرض فورم انتقال ثبت می‌شد.
///
/// قرارداد جدید:
///   • نرخ واردشدهٔ کاربر مستقیم در AppliedCurrencyPerUsdRate ذخیره می‌شود.
///   • FxRateToUsd فقط ضریبِ محاسباتی است و با دقت numeric(24,12) نگه داشته می‌شود.
///   • نمایش و snapshot از نرخ ذخیره‌شده می‌آید، نه از بازسازی.
///   • دادهٔ Legacy بدون نرخ مستقیم «تخمینی» علامت می‌خورد و به‌عنوان نرخ قطعی ذخیره نمی‌شود.
///   • نرخ روزِ انتقال از نرخ تاریخی جداست و هرگز از آن پُر نمی‌شود.
/// </summary>
public class SupplierBalanceTransferRateTests
{
    private const int SupplierId = 1;
    private const int CompanyId = 1;
    private const int UsdContractId = 4;
    private const int UsdContract2Id = 5;

    // ---------- ۱) ۷۰۰۰ روبل با نرخ دقیق ۷۰ ----------

    [Fact]
    public async Task Exact_Rate_70_Is_Stored_And_Read_Back_As_Exactly_70()
    {
        await using var db = await NewDbAsync();
        AddClaimWithStoredRate(db, "RUB", originalAmount: 7000m, perUsdRate: 70m);
        await db.SaveChangesAsync();

        var balance = await new SupplierTransferableBalanceService(db).GetAsync(SupplierId);
        var bucket = Assert.Single(Company(balance).Buckets);

        // این همان جایی است که قبلاً ۶۹.۹۹۸۶ می‌داد.
        Assert.Equal(70m, bucket.WeightedHistoricalPerUsdRate);
        Assert.False(bucket.RateIsEstimated);
        Assert.Equal(70m, bucket.StoredPerUsdRate);
        Assert.Equal(7000m, bucket.RemainingOriginalAmount);
    }

    [Fact]
    public async Task Exact_Rate_70_Survives_Into_The_Saved_Transfer()
    {
        await using var db = await NewDbAsync();
        AddClaimWithStoredRate(db, "RUB", originalAmount: 7000m, perUsdRate: 70m);
        await db.SaveChangesAsync();

        var created = await NewTransferService(db).CreateAsync(
            Request("RUB", transferPerUsd: 70m, (UsdContractId, 7000m, 1m)));

        var transfer = Assert.Single(created);
        Assert.Equal(70m, transfer.HistoricalCurrencyPerUsdRate);
        Assert.False(transfer.HistoricalRateIsEstimated);
        Assert.Equal(70m, transfer.TransferPerUsdRate);

        // snapshot خودِ منبع هم همان ۷۰ است، نه عدد بازسازی‌شده.
        Assert.Equal(70m, Assert.Single(transfer.Sources).HistoricalCurrencyPerUsdRate);
    }

    // ---------- ۲) نرخ ۷۷.۳۵ بدون انحراف در رفت‌وبرگشت ----------

    [Theory]
    [InlineData(77.35)]
    [InlineData(70)]
    [InlineData(0.9137)]
    [InlineData(12345.678901)]
    public async Task Round_Trip_Keeps_The_Entered_Rate_Bit_For_Bit(decimal perUsdRate)
    {
        await using var db = await NewDbAsync();
        AddClaimWithStoredRate(db, "RUB", originalAmount: 10_000m, perUsdRate: perUsdRate);
        await db.SaveChangesAsync();

        var balance = await new SupplierTransferableBalanceService(db).GetAsync(SupplierId);
        var bucket = Assert.Single(Company(balance).Buckets);

        Assert.Equal(perUsdRate, bucket.WeightedHistoricalPerUsdRate);
        Assert.False(bucket.RateIsEstimated);
    }

    [Fact]
    public void Reconstructing_A_Rate_Is_Exactly_What_Used_To_Drift()
    {
        // مستندسازی خودِ اشکال: مسیر قدیمی با دقت ۶ رقم نرخ ۷۰ را ۶۹.۹۹۸۶ می‌کرد.
        var legacyToUsd = decimal.Round(1m / 70m, 6, MidpointRounding.AwayFromZero);
        var legacyBack = decimal.Round(1m / legacyToUsd, 6, MidpointRounding.AwayFromZero);
        Assert.NotEqual(70m, legacyBack);

        // با دقت ۱۲ رقم انحراف بسیار کوچک‌تر می‌شود ولی هنوز صفر نیست —
        // به همین دلیل صرفِ بالابردن دقت کافی نبود و نرخ مستقیم ذخیره می‌شود.
        var wideToUsd = FxRateMath.ToUsdFromPerUsd(70m);
        Assert.NotEqual(70m, FxRateMath.PerUsdFromToUsd(wideToUsd));
    }

    // ---------- ۳) چند Source با نرخ‌های متفاوت ----------

    [Fact]
    public async Task Multiple_Sources_With_The_Same_Rate_Keep_That_Exact_Rate()
    {
        await using var db = await NewDbAsync();
        AddClaimWithStoredRate(db, "RUB", 7000m, 70m, day: 1);
        AddClaimWithStoredRate(db, "RUB", 3500m, 70m, day: 2);
        await db.SaveChangesAsync();

        var bucket = Assert.Single(Company(await new SupplierTransferableBalanceService(db).GetAsync(SupplierId)).Buckets);

        Assert.Equal(70m, bucket.WeightedHistoricalPerUsdRate);
        Assert.False(bucket.RateIsEstimated);
    }

    [Fact]
    public async Task Multiple_Sources_With_Different_Rates_Blend_From_Real_Amounts()
    {
        await using var db = await NewDbAsync();
        // ۷۰۰۰ @ ۷۰ = ۱۰۰ دالر  ·  ۸۰۰۰ @ ۸۰ = ۱۰۰ دالر  →  ۱۵۰۰۰ برای ۲۰۰ دالر = ۷۵
        AddClaimWithStoredRate(db, "RUB", 7000m, 70m, day: 1);
        AddClaimWithStoredRate(db, "RUB", 8000m, 80m, day: 2);
        await db.SaveChangesAsync();

        var bucket = Assert.Single(Company(await new SupplierTransferableBalanceService(db).GetAsync(SupplierId)).Buckets);

        Assert.Equal(15_000m, bucket.RemainingOriginalAmount);
        Assert.Equal(200m, bucket.RemainingBookAmountUsd);
        // TotalOriginalAmount / TotalHistoricalAmountUsd — نه معکوسِ نرخِ وزنی.
        Assert.Equal(75m, bucket.WeightedHistoricalPerUsdRate);
        Assert.False(bucket.RateIsEstimated);
    }

    [Fact]
    public async Task Each_Source_Keeps_Its_Own_Rate_Snapshot_On_Transfer()
    {
        await using var db = await NewDbAsync();
        AddClaimWithStoredRate(db, "RUB", 7000m, 70m, day: 1);
        AddClaimWithStoredRate(db, "RUB", 8000m, 80m, day: 2);
        await db.SaveChangesAsync();

        var created = await NewTransferService(db).CreateAsync(
            Request("RUB", transferPerUsd: 75m, (UsdContractId, 15_000m, 1m)));

        var transfer = Assert.Single(created);
        var rates = transfer.Sources
            .OrderBy(s => s.SourceDate)
            .Select(s => s.HistoricalCurrencyPerUsdRate)
            .ToList();

        // هر منبع نرخ خودش را نگه می‌دارد؛ خلاصهٔ ۷۵ روی آنها نمی‌نشیند.
        Assert.Equal(new decimal?[] { 70m, 80m }, rates);
        // ارزش تاریخی از جمعِ همان snapshotها می‌آید.
        Assert.Equal(200m, transfer.HistoricalAmountUsd);
        Assert.Equal(75m, transfer.HistoricalCurrencyPerUsdRate);
    }

    // ---------- ۴) داده قدیمی بدون نرخ مستقیم ----------

    [Fact]
    public async Task Legacy_Source_Without_Stored_Rate_Is_Flagged_Estimated()
    {
        await using var db = await NewDbAsync();
        AddLegacyClaim(db, "RUB", originalAmount: 7000m, perUsdRate: 70m);
        await db.SaveChangesAsync();

        var bucket = Assert.Single(Company(await new SupplierTransferableBalanceService(db).GetAsync(SupplierId)).Buckets);

        Assert.True(bucket.RateIsEstimated);
        // نرخِ بازسازی‌شده نمایش داده می‌شود ولی به‌عنوان نرخ قطعی عرضه نمی‌شود.
        Assert.Null(bucket.StoredPerUsdRate);
        Assert.NotEqual(0m, bucket.WeightedHistoricalPerUsdRate);
    }

    [Fact]
    public async Task Legacy_Source_Never_Writes_A_Guessed_Rate_Into_The_Transfer()
    {
        await using var db = await NewDbAsync();
        AddLegacyClaim(db, "RUB", originalAmount: 7000m, perUsdRate: 70m);
        await db.SaveChangesAsync();

        var created = await NewTransferService(db).CreateAsync(
            Request("RUB", transferPerUsd: 70m, (UsdContractId, 7000m, 1m)));

        var transfer = Assert.Single(created);
        Assert.Null(transfer.HistoricalCurrencyPerUsdRate);
        Assert.True(transfer.HistoricalRateIsEstimated);
        // ارزش دفتری تاریخی همچنان از خودِ سند می‌آید و دست‌نخورده است.
        Assert.Equal(100m, transfer.HistoricalAmountUsd);
    }

    [Fact]
    public async Task Mixed_Legacy_And_Stored_Sources_Are_Flagged_Estimated()
    {
        await using var db = await NewDbAsync();
        AddClaimWithStoredRate(db, "RUB", 7000m, 70m, day: 1);
        AddLegacyClaim(db, "RUB", 8000m, 80m, day: 2);
        await db.SaveChangesAsync();

        var bucket = Assert.Single(Company(await new SupplierTransferableBalanceService(db).GetAsync(SupplierId)).Buckets);

        Assert.True(bucket.RateIsEstimated);
        Assert.Null(bucket.StoredPerUsdRate);
    }

    // ---------- ۵) نرخ تاریخی جدا از نرخ روز انتقال ----------

    [Fact]
    public async Task Historical_And_Transfer_Rates_Are_Stored_Separately()
    {
        await using var db = await NewDbAsync();
        AddClaimWithStoredRate(db, "RUB", originalAmount: 7000m, perUsdRate: 70m);
        await db.SaveChangesAsync();

        var transfer = Assert.Single(await NewTransferService(db).CreateAsync(
            Request("RUB", transferPerUsd: 80m, (UsdContractId, 7000m, 1m))));

        // چهار نرخ، چهار ستون؛ هیچ‌کدام از دیگری بازسازی نمی‌شود.
        Assert.Equal(70m, transfer.HistoricalCurrencyPerUsdRate);
        Assert.Equal(FxRateMath.ToUsdFromPerUsd(70m), transfer.HistoricalFxRateToUsd);
        Assert.Equal(80m, transfer.TransferPerUsdRate);
        Assert.Equal(FxRateMath.ToUsdFromPerUsd(80m), transfer.TransferFxRateToUsd);
    }

    // ---------- سازگاری نرخ مستقیم و معکوس ----------

    [Theory]
    [InlineData(70, 80)]
    [InlineData(77.35, 90.125)]
    [InlineData(90, 90)]
    [InlineData(0.9137, 1.2255)]
    public async Task Direct_And_Inverse_Rates_Can_Never_Disagree(decimal historicalPerUsd, decimal transferPerUsd)
    {
        await using var db = await NewDbAsync();
        AddClaimWithStoredRate(db, "RUB", originalAmount: 10_000m, perUsdRate: historicalPerUsd);
        await db.SaveChangesAsync();

        var transfer = Assert.Single(await NewTransferService(db).CreateAsync(
            Request("RUB", transferPerUsd, (UsdContractId, 10_000m, 1m))));

        // هر ستون معکوس باید دقیقاً از ستون مستقیمِ کنارش ساخته شده باشد.
        Assert.Equal(
            FxRateMath.ToUsdFromPerUsd(transfer.HistoricalCurrencyPerUsdRate!.Value),
            transfer.HistoricalFxRateToUsd);
        Assert.Equal(
            FxRateMath.ToUsdFromPerUsd(transfer.TransferPerUsdRate),
            transfer.TransferFxRateToUsd);
        Assert.Equal(
            FxRateMath.ToUsdFromPerUsd(transfer.ContractCurrencyPerUsdRate),
            transfer.ContractCurrencyFxRateToUsd);
    }

    [Fact]
    public async Task Blended_Historical_Rate_Still_Drives_Its_Own_Inverse()
    {
        await using var db = await NewDbAsync();
        AddClaimWithStoredRate(db, "RUB", 7000m, 70m, day: 1);
        AddClaimWithStoredRate(db, "RUB", 8000m, 80m, day: 2);
        await db.SaveChangesAsync();

        var transfer = Assert.Single(await NewTransferService(db).CreateAsync(
            Request("RUB", transferPerUsd: 75m, (UsdContractId, 15_000m, 1m))));

        Assert.Equal(75m, transfer.HistoricalCurrencyPerUsdRate);
        Assert.Equal(FxRateMath.ToUsdFromPerUsd(75m), transfer.HistoricalFxRateToUsd);
    }

    [Fact]
    public void Create_Request_Exposes_No_Inverse_Rate_To_Callers()
    {
        // قرارداد API: کنترلر و کاربر فقط نرخ مستقیم می‌دهند. اگر روزی ستون معکوسی به
        // درخواست اضافه شود، این تست می‌شکند و باید عمداً بازبینی شود.
        var requestProps = typeof(SupplierBalanceTransferCreateRequest)
            .GetProperties().Select(p => p.Name).ToList();
        var lineProps = typeof(SupplierBalanceTransferLineRequest)
            .GetProperties().Select(p => p.Name).ToList();

        Assert.DoesNotContain(requestProps, n => n.Contains("FxRateToUsd", StringComparison.Ordinal));
        Assert.DoesNotContain(lineProps, n => n.Contains("FxRateToUsd", StringComparison.Ordinal));
        Assert.Contains("TransferPerUsdRate", requestProps);
        Assert.Contains("ContractCurrencyPerUsdRate", lineProps);
    }

    [Fact]
    public void Create_ViewModel_Exposes_No_Inverse_Rate_To_The_Form()
    {
        var vmProps = typeof(PTGOilSystem.Web.Models.Suppliers.SupplierBalanceTransferCreateViewModel)
            .GetProperties().Select(p => p.Name).ToList();
        var lineProps = typeof(PTGOilSystem.Web.Models.Suppliers.SupplierBalanceTransferLineViewModel)
            .GetProperties().Select(p => p.Name).ToList();

        Assert.DoesNotContain(vmProps, n => n.Contains("FxRateToUsd", StringComparison.Ordinal));
        Assert.DoesNotContain(lineProps, n => n.Contains("FxRateToUsd", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Legacy_Source_Is_The_Only_Case_Where_Inverse_Is_Not_Derived()
    {
        await using var db = await NewDbAsync();
        AddLegacyClaim(db, "RUB", originalAmount: 7000m, perUsdRate: 70m);
        await db.SaveChangesAsync();

        var transfer = Assert.Single(await NewTransferService(db).CreateAsync(
            Request("RUB", transferPerUsd: 70m, (UsdContractId, 7000m, 1m))));

        // نرخ مستقیم وجود ندارد، پس معکوس از مبالغ می‌آید و رکورد «تخمینی» است.
        Assert.Null(transfer.HistoricalCurrencyPerUsdRate);
        Assert.True(transfer.HistoricalRateIsEstimated);
        Assert.True(transfer.HistoricalFxRateToUsd > 0m);

        // ولی نرخ روزِ انتقال همچنان از نرخ مستقیم ساخته می‌شود.
        Assert.Equal(FxRateMath.ToUsdFromPerUsd(70m), transfer.TransferFxRateToUsd);
    }

    // ---------- ۶) سود و زیان تسعیر دقیق ----------

    [Fact]
    public async Task Exchange_Loss_Is_Exact_When_Todays_Rate_Is_Weaker()
    {
        await using var db = await NewDbAsync();
        AddClaimWithStoredRate(db, "RUB", originalAmount: 7000m, perUsdRate: 70m);
        await db.SaveChangesAsync();

        // تاریخی: ۷۰۰۰ @ ۷۰ = ۱۰۰ دالر · امروز: ۷۰۰۰ @ ۸۰ = ۸۷.۵ دالر → زیان ۱۲.۵
        var transfer = Assert.Single(await NewTransferService(db).CreateAsync(
            Request("RUB", transferPerUsd: 80m, (UsdContractId, 7000m, 1m))));

        Assert.Equal(100m, transfer.HistoricalAmountUsd);
        Assert.Equal(87.5m, transfer.TransferValueUsd);
        Assert.Equal(-12.5m, transfer.ExchangeDifferenceUsd);
        Assert.Equal(SarrafSettlementDifferenceType.Loss, transfer.ExchangeDifferenceType);
    }

    [Fact]
    public async Task Exchange_Gain_Is_Exact_When_Todays_Rate_Is_Stronger()
    {
        await using var db = await NewDbAsync();
        AddClaimWithStoredRate(db, "RUB", originalAmount: 7000m, perUsdRate: 70m);
        await db.SaveChangesAsync();

        // امروز: ۷۰۰۰ @ ۵۶ = ۱۲۵ دالر → سود ۲۵
        var transfer = Assert.Single(await NewTransferService(db).CreateAsync(
            Request("RUB", transferPerUsd: 56m, (UsdContractId, 7000m, 1m))));

        Assert.Equal(125m, transfer.TransferValueUsd);
        Assert.Equal(25m, transfer.ExchangeDifferenceUsd);
        Assert.Equal(SarrafSettlementDifferenceType.Gain, transfer.ExchangeDifferenceType);
    }

    [Fact]
    public async Task Same_Rate_Produces_No_Exchange_Difference_At_All()
    {
        await using var db = await NewDbAsync();
        AddClaimWithStoredRate(db, "RUB", originalAmount: 7000m, perUsdRate: 70m);
        await db.SaveChangesAsync();

        var transfer = Assert.Single(await NewTransferService(db).CreateAsync(
            Request("RUB", transferPerUsd: 70m, (UsdContractId, 7000m, 1m))));

        // با نرخ‌های بازسازی‌شده اینجا یک سنتِ ساختگی سود/زیان می‌ساخت.
        Assert.Equal(0m, transfer.ExchangeDifferenceUsd);
        Assert.Equal(SarrafSettlementDifferenceType.None, transfer.ExchangeDifferenceType);
        Assert.Equal(transfer.HistoricalAmountUsd, transfer.TransferValueUsd);
    }

    // ---------- ۷) نرخ پیش‌فرض فورم از نرخ روز، نه نرخ تاریخی ----------

    [Fact]
    public async Task Form_Default_Rate_Comes_From_The_Day_Rate_Not_The_Historical_Rate()
    {
        await using var db = await NewDbAsync();
        AddClaimWithStoredRate(db, "RUB", originalAmount: 7000m, perUsdRate: 70m);
        // نرخ روزِ ثبت‌شده عمداً با نرخ تاریخی فرق دارد: ۱ روبل = 1/85 دالر.
        db.DailyFxRates.Add(new DailyFxRate
        {
            Id = 1,
            BaseCurrency = "RUB",
            QuoteCurrency = "USD",
            RateDate = new DateTime(2026, 4, 1),
            Rate = FxRateMath.ToUsdFromPerUsd(85m)
        });
        await db.SaveChangesAsync();

        var model = await GetCreateModelAsync(db, new DateTime(2026, 4, 1));

        Assert.Equal(85m, model.TransferPerUsdRate);
        Assert.NotEqual(70m, model.TransferPerUsdRate);
        Assert.False(model.DayRateMissing);
        Assert.NotNull(model.DayRateSource);
    }

    [Fact]
    public async Task Form_Leaves_Rate_Empty_When_No_Day_Rate_Exists()
    {
        await using var db = await NewDbAsync();
        AddClaimWithStoredRate(db, "RUB", originalAmount: 7000m, perUsdRate: 70m);
        await db.SaveChangesAsync();

        var model = await GetCreateModelAsync(db, new DateTime(2026, 4, 1));

        // مهم‌ترین بند: نبودِ نرخ روز نباید با نرخ تاریخی پر شود.
        Assert.True(model.DayRateMissing);
        Assert.Equal(0m, model.TransferPerUsdRate);
        Assert.NotEqual(70m, model.TransferPerUsdRate);
    }

    // ---------- ۸) برگشت انتقال با همان نرخ‌های قفل‌شده ----------

    [Fact]
    public async Task Reversal_Uses_The_Locked_Rates_Not_A_Fresh_Reconstruction()
    {
        await using var db = await NewDbAsync();
        AddClaimWithStoredRate(db, "RUB", originalAmount: 7000m, perUsdRate: 70m);
        await db.SaveChangesAsync();

        var service = NewTransferService(db);
        var transfer = Assert.Single(await service.CreateAsync(
            Request("RUB", transferPerUsd: 80m, (UsdContractId, 7000m, 1m))));

        var historicalPerUsd = transfer.HistoricalCurrencyPerUsdRate;
        var historicalUsd = transfer.HistoricalAmountUsd;
        var transferUsd = transfer.TransferValueUsd;

        var reversed = await service.ReverseAsync(
            new SupplierBalanceTransferReverseRequest(transfer.Id, "اشتباه ثبت شد", "tester"));

        Assert.Equal(SupplierBalanceTransferStatus.Reversed, reversed.Status);
        // نرخ‌ها و مبالغ دست‌نخورده می‌مانند.
        Assert.Equal(historicalPerUsd, reversed.HistoricalCurrencyPerUsdRate);
        Assert.Equal(70m, reversed.HistoricalCurrencyPerUsdRate);
        Assert.Equal(80m, reversed.TransferPerUsdRate);

        // سطرهای برگشت دقیقاً همان مبالغ قفل‌شده را برمی‌گردانند.
        var reversalRows = db.LedgerEntries
            .Where(l => l.SourceType == SupplierBalanceTransferService.ReversalLedgerSourceType)
            .ToList();
        Assert.Equal(2, reversalRows.Count);
        Assert.Contains(reversalRows, r => r.Side == LedgerSide.Debit && r.AmountUsd == historicalUsd);
        Assert.Contains(reversalRows, r => r.Side == LedgerSide.Credit && r.AmountUsd == transferUsd);
        // نرخ مستقیم روی سطر برگشت هم ثبت می‌شود تا خواندنِ بعدی بازسازی نکند.
        Assert.Contains(reversalRows, r => r.AppliedCurrencyPerUsdRate == 70m);
    }

    [Fact]
    public async Task Reversal_Restores_The_Balance_At_The_Original_Rate()
    {
        await using var db = await NewDbAsync();
        AddClaimWithStoredRate(db, "RUB", originalAmount: 7000m, perUsdRate: 70m);
        await db.SaveChangesAsync();

        var service = NewTransferService(db);
        var transfer = Assert.Single(await service.CreateAsync(
            Request("RUB", transferPerUsd: 80m, (UsdContractId, 7000m, 1m))));
        await service.ReverseAsync(
            new SupplierBalanceTransferReverseRequest(transfer.Id, "اشتباه ثبت شد", "tester"));

        var bucket = Assert.Single(Company(await new SupplierTransferableBalanceService(db).GetAsync(SupplierId)).Buckets);

        Assert.Equal(7000m, bucket.RemainingOriginalAmount);
        Assert.Equal(70m, bucket.WeightedHistoricalPerUsdRate);
        Assert.False(bucket.RateIsEstimated);
    }

    // ---------- ledger stamping ----------

    [Fact]
    public async Task New_Transfer_Ledger_Rows_Carry_The_Direct_Rate()
    {
        await using var db = await NewDbAsync();
        AddClaimWithStoredRate(db, "RUB", originalAmount: 7000m, perUsdRate: 70m);
        await db.SaveChangesAsync();

        await NewTransferService(db).CreateAsync(
            Request("RUB", transferPerUsd: 80m, (UsdContractId, 7000m, 1m)));

        var credit = db.LedgerEntries.Single(l =>
            l.SourceType == SupplierBalanceTransferService.LedgerSourceType
            && l.Side == LedgerSide.Credit);

        // بدون این، خواندنِ بعدیِ همین سطر دوباره نرخ را بازسازی می‌کرد و انحراف برمی‌گشت.
        Assert.Equal(70m, credit.AppliedCurrencyPerUsdRate);
    }

    // ================= helpers =================

    private static SupplierCompanyTransferableBalance Company(SupplierTransferableBalance balance)
        => balance.Company(CompanyId)!;

    private static SupplierBalanceTransferService NewTransferService(ApplicationDbContext db)
        => new(db, new SupplierTransferableBalanceService(db));

    private static SupplierBalanceTransferCreateRequest Request(
        string currency,
        decimal transferPerUsd,
        params (int ContractId, decimal Amount, decimal ContractPerUsd)[] lines)
        => new(
            SupplierId,
            CompanyId,
            new DateTime(2026, 4, 1),
            currency,
            transferPerUsd,
            lines.Select(l => new SupplierBalanceTransferLineRequest(l.ContractId, l.Amount, l.ContractPerUsd)).ToList(),
            "REF-RATE",
            null,
            "tester");

    /// <summary>سطر جدید: نرخ مستقیم ثبت شده است.</summary>
    private static void AddClaimWithStoredRate(
        ApplicationDbContext db,
        string currency,
        decimal originalAmount,
        decimal perUsdRate,
        int day = 1)
        => AddLedgerRow(db, currency, originalAmount, perUsdRate, day, storeDirectRate: true);

    /// <summary>سطر Legacy: فقط FxRateToUsd با دقت ۶ رقم دارد، نرخ مستقیم ندارد.</summary>
    private static void AddLegacyClaim(
        ApplicationDbContext db,
        string currency,
        decimal originalAmount,
        decimal perUsdRate,
        int day = 1)
        => AddLedgerRow(db, currency, originalAmount, perUsdRate, day, storeDirectRate: false);

    private static void AddLedgerRow(
        ApplicationDbContext db,
        string currency,
        decimal originalAmount,
        decimal perUsdRate,
        int day,
        bool storeDirectRate)
    {
        // ارزش دفتری از «مقدار ÷ نرخ» می‌آید تا مثل داده واقعی دقیقاً ۱۰۰ دالر باشد.
        var amountUsd = decimal.Round(originalAmount / perUsdRate, 4, MidpointRounding.AwayFromZero);
        var fxRateToUsd = storeDirectRate
            ? FxRateMath.ToUsdFromPerUsd(perUsdRate)
            : decimal.Round(1m / perUsdRate, 6, MidpointRounding.AwayFromZero);

        db.LedgerEntries.Add(new LedgerEntry
        {
            Id = _nextLedgerId++,
            EntryDate = new DateTime(2026, 2, day),
            Side = LedgerSide.Debit,
            AmountUsd = amountUsd,
            Currency = "USD",
            SourceAmount = originalAmount,
            SourceCurrencyCode = currency,
            AppliedFxRateToUsd = fxRateToUsd,
            AppliedCurrencyPerUsdRate = storeDirectRate ? perUsdRate : null,
            AppliedFxRateDate = new DateTime(2026, 2, day),
            Description = "SupplierPayment",
            SourceType = "SupplierPayment",
            SourceId = _nextSourceId++,
            SupplierId = SupplierId,
            ContractId = UsdContractId
        });
    }

    private static async Task<PTGOilSystem.Web.Models.Suppliers.SupplierBalanceTransferCreateViewModel>
        GetCreateModelAsync(ApplicationDbContext db, DateTime today)
    {
        var controller = new PTGOilSystem.Web.Controllers.SupplierBalanceTransfersController(
            db,
            new SupplierTransferableBalanceService(db),
            NewTransferService(db),
            new PTGOilSystem.Web.Services.AuditService(db),
            new PricingService(db),
            new FixedClock(today))
        {
            TempData = new Microsoft.AspNetCore.Mvc.ViewFeatures.TempDataDictionary(
                new Microsoft.AspNetCore.Http.DefaultHttpContext(), new RateTestTempDataProvider())
        };

        var result = await controller.Create(SupplierId, CompanyId, "RUB", null);
        var view = Assert.IsType<Microsoft.AspNetCore.Mvc.ViewResult>(result);
        return Assert.IsType<PTGOilSystem.Web.Models.Suppliers.SupplierBalanceTransferCreateViewModel>(view.Model);
    }

    private static int _nextLedgerId = 9000;
    private static int _nextSourceId = 9000;

    private static async Task<ApplicationDbContext> NewDbAsync()
    {
        var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

        db.Products.Add(new Product { Id = 1, Code = "G92", Name = "Gasoline 92", UnitOfMeasure = "MT", IsActive = true });
        db.Companies.Add(new Company { Id = CompanyId, Code = "PTG", Name = "PTG", Country = "AF", IsActive = true });
        db.Suppliers.Add(new Supplier { Id = SupplierId, Code = "SUP1", Name = "Supplier One", IsActive = true });
        db.Contracts.AddRange(
            NewContract(UsdContractId, "P-USD-1"),
            NewContract(UsdContract2Id, "P-USD-2"));

        await db.SaveChangesAsync();
        return db;
    }

    private static Contract NewContract(int id, string number)
        => new()
        {
            Id = id,
            ContractNumber = number,
            ContractName = number,
            ContractType = ContractType.Purchase,
            Status = ContractStatus.Active,
            CompanyId = CompanyId,
            ProductId = 1,
            SupplierId = SupplierId,
            ContractDate = new DateTime(2026, 1, 1),
            PricingMethod = PricingMethod.ManualFinalPrice,
            QuantityMt = 1000m,
            Currency = "USD"
        };

    private sealed class FixedClock(DateTime today) : PTGOilSystem.Web.Services.Time.IAfghanistanBusinessClock
    {
        public DateTime Today => today.Date;
        public DateTimeOffset Now => new(today, TimeSpan.FromHours(4.5));
        public (DateTime StartUtc, DateTime EndUtcExclusive) UtcRange(DateTime localDate)
            => (localDate.Date, localDate.Date.AddDays(1));
    }

    private sealed class RateTestTempDataProvider : Microsoft.AspNetCore.Mvc.ViewFeatures.ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(Microsoft.AspNetCore.Http.HttpContext context)
            => new Dictionary<string, object>();

        public void SaveTempData(
            Microsoft.AspNetCore.Http.HttpContext context,
            IDictionary<string, object> values)
        {
        }
    }
}
