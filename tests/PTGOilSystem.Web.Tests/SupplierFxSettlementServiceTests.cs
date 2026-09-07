using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// تفکیک «تفاوت نرخ» از «بدهی واقعی» روی حساب ارزیِ تأمین‌کننده.
///
/// سناریوی پذیرش، همان دادهٔ واقعیِ سیستم است: قرارداد خرید ۱۰۰۰ تن با تسویهٔ روبلی، دو
/// بارگیری ۵۰۰ تنی — یکی ۱۰۰۰ دالر با نرخ ۷۰ (۳۵ میلیون روبل) و یکی ۹۰۰ دالر با نرخ ۸۰
/// (۳۶ میلیون روبل) — یعنی ۷۱ میلیون روبل بدهی. سپس ۱ میلیون دالر از طریق صراف با نرخ ۷۰
/// پرداخت می‌شود که ۷۰ میلیون روبل به تأمین‌کننده می‌رسد.
///
/// انتظار: به روبل ۱ میلیون بدهکاریم؛ جمعِ دالریِ تاریخی ۵۰٬۰۰۰ طلب نشان می‌دهد، ولی
/// ۶۲٬۵۰۰ از آن ضررِ تفاوت نرخ است و بدهی واقعی ۱۲٬۵۰۰ دالر (۱ میلیون روبل با نرخ ۸۰).
/// </summary>
public class SupplierFxSettlementServiceTests
{
    private const int SupplierId = 1;

    [Fact]
    public async Task Rub_Debt_Settled_At_Different_Rate_Splits_Fx_From_Real_Debt()
    {
        await using var db = NewDb();
        SeedRealScenario(db);
        await db.SaveChangesAsync();

        var result = await new SupplierFxSettlementService(db).GetAsync(SupplierId);

        var rub = Assert.Single(result.Currencies);
        Assert.Equal("RUB", rub.CurrencyCode);

        // به روبل هنوز ۱ میلیون بدهکاریم.
        Assert.Equal(1_000_000m, rub.OutstandingSourceAmount);

        // جمعِ دالریِ فعلیِ سیستم: ۹۵۰٬۰۰۰ بدهی منهای ۱٬۰۰۰٬۰۰۰ پرداخت = ۵۰٬۰۰۰ طلب.
        Assert.Equal(-50_000m, rub.HistoricalNetUsd);

        // ۷۰ میلیون روبل تطبیق شد: ۳۵ با نرخ ۷۰ (بدون تفاوت) و ۳۵ با نرخ ۸۰.
        Assert.Equal(70_000_000m, rub.MatchedSourceAmount);
        Assert.Equal(62_500m, rub.RealizedFxDifferenceUsd);
        Assert.Equal(62_500m, rub.FxLossUsd);
        Assert.Equal(0m, rub.FxGainUsd);

        // بدهی واقعیِ باقی‌مانده: ۱ میلیون روبل با نرخ دفتریِ همان بارگیری (۸۰).
        Assert.Equal(12_500m, rub.OutstandingBookUsd);

        // اتحاد کنترلی: جمع تاریخی + تفاوت نرخ = بدهی واقعی.
        Assert.Equal(rub.OutstandingBookUsd, rub.HistoricalNetUsd + rub.RealizedFxDifferenceUsd);
        Assert.Equal(12_500m, result.AdjustedBalanceUsd);
    }

    [Fact]
    public async Task Internal_Transfer_And_Its_Reversal_Do_Not_Shift_The_Fifo_Queue()
    {
        await using var db = NewDb();
        SeedRealScenario(db);

        // انتقال مانده به قرارداد و برگشتش — جابه‌جایی داخلی، نه بدهی و نه پرداخت.
        db.LedgerEntries.Add(RubEntry(7, "2026-09-04", LedgerSide.Credit, 50_000m, 3_500_000m, "SupplierBalanceTransfer", 1));
        db.LedgerEntries.Add(RubEntry(9, "2026-09-04", LedgerSide.Debit, 50_000m, 3_500_000m, "SupplierBalanceTransferReversal", 1));
        await db.SaveChangesAsync();

        var rub = Assert.Single((await new SupplierFxSettlementService(db).GetAsync(SupplierId)).Currencies);

        // دقیقاً همان اعداد سناریوی پایه: انتقال داخلی چیزی را تکان نمی‌دهد.
        Assert.Equal(1_000_000m, rub.OutstandingSourceAmount);
        Assert.Equal(62_500m, rub.RealizedFxDifferenceUsd);
        Assert.Equal(12_500m, rub.OutstandingBookUsd);
    }

    [Fact]
    public async Task Same_Rate_On_Both_Sides_Recognizes_No_Fx_Difference()
    {
        await using var db = NewDb();
        db.LedgerEntries.Add(RubEntry(1, "2026-09-03", LedgerSide.Credit, 500_000m, 35_000_000m, "Loading", 1));
        db.LedgerEntries.Add(RubEntry(2, "2026-09-04", LedgerSide.Debit, 500_000m, 35_000_000m, "SupplierViaSarrafPayment", 1));
        await db.SaveChangesAsync();

        var rub = Assert.Single((await new SupplierFxSettlementService(db).GetAsync(SupplierId)).Currencies);

        Assert.Equal(0m, rub.OutstandingSourceAmount);
        Assert.Equal(0m, rub.RealizedFxDifferenceUsd);
        Assert.False(rub.HasFxDifference);
    }

    // ===================== اضافه‌پرداخت: پول نزد تأمین‌کننده می‌ماند =====================
    // منطق شرکت: مازادِ پرداخت، پولی است که «نزد تأمین‌کننده» می‌ماند و بعداً با نرخ روزِ
    // ثبتِ قرارداد جدید به آن قرارداد منتقل می‌شود (SupplierBalanceTransferService). پس
    // روی مازاد هیچ تفاوت نرخی محقق نمی‌شود — با نرخ تاریخی خودش منتظر می‌ماند.

    [Fact]
    public async Task Surplus_Stays_At_Its_Own_Rate_And_Recognizes_No_Fx()
    {
        await using var db = NewDb();
        // بدهی ۵۰۰ هزار روبل با نرخ ۵۰، پرداخت ۱ میلیون روبل با همان نرخ ۵۰.
        db.LedgerEntries.Add(RubEntry(1, "2026-09-03", LedgerSide.Credit, 10_000m, 500_000m, "Loading", 1));
        db.LedgerEntries.Add(RubEntry(2, "2026-09-04", LedgerSide.Debit, 20_000m, 1_000_000m, "SupplierViaSarrafPayment", 1));
        await db.SaveChangesAsync();

        var rub = Assert.Single((await new SupplierFxSettlementService(db).GetAsync(SupplierId)).Currencies);

        // منفی یعنی مازاد: ۵۰۰ هزار روبل نزد تأمین‌کننده مانده است.
        Assert.Equal(-500_000m, rub.OutstandingSourceAmount);
        Assert.Equal(0m, rub.RealizedFxDifferenceUsd);
        Assert.False(rub.HasFxDifference);
    }

    [Fact]
    public async Task Only_The_Settled_Part_Realizes_Fx_The_Surplus_Waits()
    {
        await using var db = NewDb();
        // بدهی ۵۰۰ هزار روبل با نرخ ۸۰، پرداخت ۱ میلیون روبل با نرخ ۷۰.
        db.LedgerEntries.Add(RubEntry(1, "2026-09-03", LedgerSide.Credit, 6_250m, 500_000m, "Loading", 1));
        db.LedgerEntries.Add(RubEntry(2, "2026-09-04", LedgerSide.Debit, 14_285.7143m, 1_000_000m, "SupplierViaSarrafPayment", 1));
        await db.SaveChangesAsync();

        var rub = Assert.Single((await new SupplierFxSettlementService(db).GetAsync(SupplierId)).Currencies);

        // فقط ۵۰۰ هزار روبلِ بسته‌شده تفاوت نرخ می‌سازد: 500,000 × (1/70 − 1/80).
        Assert.Equal(500_000m, rub.MatchedSourceAmount);
        Assert.Equal(892.8572m, rub.RealizedFxDifferenceUsd);

        // مازاد با نرخ خودِ پرداخت (۷۰) منتظر انتقال به قرارداد بعدی می‌ماند.
        Assert.Equal(-500_000m, rub.OutstandingSourceAmount);
        Assert.Equal(-7_142.8572m, rub.OutstandingBookUsd);
    }

    [Fact]
    public async Task Usd_Only_Supplier_Has_No_Foreign_Currency_Debt()
    {
        await using var db = NewDb();
        db.LedgerEntries.Add(new LedgerEntry
        {
            Id = 1,
            EntryDate = DateTime.Parse("2026-09-03"),
            Side = LedgerSide.Credit,
            AmountUsd = 100_000m,
            Currency = "USD",
            SourceAmount = 100_000m,
            SourceCurrencyCode = "USD",
            SourceType = "Loading",
            SourceId = 1,
            SupplierId = SupplierId,
            Description = "بدهی دالری"
        });
        await db.SaveChangesAsync();

        var result = await new SupplierFxSettlementService(db).GetAsync(SupplierId);

        Assert.False(result.HasForeignCurrencyDebt);
        Assert.Equal(0m, result.RealizedFxDifferenceUsd);
    }

    // ===================== داده =====================

    // بارگیری #1 با نرخ ۷۰ و بارگیری #2 با نرخ ۸۰، سپس پرداخت صرافیِ ۷۰ میلیون روبل با نرخ ۷۰.
    // ترتیب Idها عمداً وارونهٔ ترتیب بارگیری است — دقیقاً مثل دیتابیس واقعی — تا ثابت شود
    // FIFO از تاریخ/منبع می‌آید نه از ترتیب درج سطر دفتر.
    private static void SeedRealScenario(ApplicationDbContext db)
    {
        db.LedgerEntries.Add(RubEntry(1, "2026-09-03", LedgerSide.Credit, 450_000m, 36_000_000m, "Loading", 2));
        db.LedgerEntries.Add(RubEntry(4, "2026-09-03", LedgerSide.Credit, 500_000m, 35_000_000m, "Loading", 1));
        db.LedgerEntries.Add(RubEntry(5, "2026-09-04", LedgerSide.Debit, 1_000_000m, 70_000_000m, "SupplierViaSarrafPayment", 1));
    }

    private static LedgerEntry RubEntry(
        int id,
        string date,
        LedgerSide side,
        decimal amountUsd,
        decimal sourceAmount,
        string sourceType,
        int sourceId)
        => new()
        {
            Id = id,
            EntryDate = DateTime.Parse(date),
            Side = side,
            AmountUsd = amountUsd,
            Currency = "USD",
            SourceAmount = sourceAmount,
            SourceCurrencyCode = "RUB",
            AppliedFxRateToUsd = decimal.Round(amountUsd / sourceAmount, 12, MidpointRounding.AwayFromZero),
            SourceType = sourceType,
            SourceId = sourceId,
            SupplierId = SupplierId,
            Description = $"{sourceType} #{sourceId}"
        };

    private static ApplicationDbContext NewDb()
        => new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
}
