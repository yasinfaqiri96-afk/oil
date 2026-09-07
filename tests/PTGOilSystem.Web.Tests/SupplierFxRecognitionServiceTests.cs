using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.PartyStatements;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// ثبت تفاوت نرخِ حساب ارزی در دفتر — همان سناریوی واقعی: ۷۱ میلیون روبل بدهی (۳۵ با نرخ ۷۰
/// و ۳۶ با نرخ ۸۰) و ۷۰ میلیون روبل پرداخت با نرخ ۷۰.
///
/// انتظار: ۶۲٬۵۰۰ دالر ضرر ثبت شود و مانده حساب از «۵۰٬۰۰۰ طلب» به «۱۲٬۵۰۰ بدهی» برسد،
/// بدون آنکه اجرای دوباره چیزی را دوبرابر کند.
/// </summary>
public class SupplierFxRecognitionServiceTests
{
    private const int SupplierId = 1;

    [Fact]
    public async Task Recognition_Moves_The_Balance_From_False_Claim_To_Real_Debt()
    {
        await using var db = NewDb();
        SeedRealScenario(db);
        await db.SaveChangesAsync();

        Assert.Equal(-50_000m, await NetBalanceUsdAsync(db));

        var result = await new SupplierFxRecognitionService(db).RecognizeAsync(SupplierId);

        Assert.Equal(62_500m, result.RecognizedUsd);
        Assert.True(result.PostedAnything);
        Assert.Equal(12_500m, await NetBalanceUsdAsync(db));
    }

    [Fact]
    public async Task Loss_Books_An_Fx_Expense_And_A_Balanced_Ledger_Pair()
    {
        await using var db = NewDb();
        SeedRealScenario(db);
        await db.SaveChangesAsync();

        var result = await new SupplierFxRecognitionService(db).RecognizeAsync(SupplierId);

        // سمت سود و زیان: یک مصرفِ «تفاوت نرخ ارز»، بدون قرارداد و بدون پول نقد.
        var expense = Assert.Single(db.ExpenseTransactions.ToList());
        Assert.Equal(result.ExpenseTransactionId, expense.Id);
        Assert.Equal(62_500m, expense.AmountUsd);
        Assert.Null(expense.ContractId);
        Assert.Equal(ExpenseSettlementMode.NonCash, expense.SettlementMode);
        Assert.Equal("FxDifference", db.ExpenseTypes.Single().Category);

        var posted = db.LedgerEntries
            .Where(l => l.SourceType == "Expense"
                || l.SourceType == SupplierFxRecognitionService.PartyLedgerSourceType)
            .ToList();
        Assert.Equal(2, posted.Count);

        // فقط سطرِ طرف‌حساب SupplierId دارد؛ سطرِ مصرف نباید مانده او را دوباره تکان دهد.
        var party = Assert.Single(posted.Where(l => l.SourceType == SupplierFxRecognitionService.PartyLedgerSourceType));
        Assert.Equal(SupplierId, party.SupplierId);
        Assert.Equal(LedgerSide.Credit, party.Side);
        Assert.Equal(62_500m, party.AmountUsd);

        var cost = Assert.Single(posted.Where(l => l.SourceType == "Expense"));
        Assert.Null(cost.SupplierId);
        Assert.Equal(LedgerSide.Debit, cost.Side);

        // دو سطر متوازن: اثر خالص روی کل دفتر صفر است.
        Assert.Equal(0m, posted.Sum(l => l.Side == LedgerSide.Credit ? l.AmountUsd : -l.AmountUsd));

        // تاریخ شناسایی = تاریخ آخرین سندِ تطبیق‌شده (پرداخت).
        Assert.Equal(DateTime.Parse("2026-09-04").Date, party.EntryDate);
    }

    [Fact]
    public async Task Running_Twice_Does_Not_Double_Count()
    {
        await using var db = NewDb();
        SeedRealScenario(db);
        await db.SaveChangesAsync();

        var service = new SupplierFxRecognitionService(db);
        await service.RecognizeAsync(SupplierId);
        var second = await service.RecognizeAsync(SupplierId);

        // اجرای دوم همان عدد را می‌دهد چون ثبتِ قبلی اول برداشته می‌شود.
        Assert.Equal(62_500m, second.RecognizedUsd);
        Assert.True(second.RemovedEntries > 0);
        Assert.Equal(12_500m, await NetBalanceUsdAsync(db));
        Assert.Single(db.ExpenseTransactions.ToList());
    }

    [Fact]
    public async Task Recognized_Row_Is_Excluded_From_The_Next_Calculation()
    {
        await using var db = NewDb();
        SeedRealScenario(db);
        await db.SaveChangesAsync();

        await new SupplierFxRecognitionService(db).RecognizeAsync(SupplierId);

        // سطرِ شناسایی دالری است، پس موتور محاسبه دوباره واردش نمی‌کند و عدد ثابت می‌ماند.
        var recomputed = await new SupplierFxSettlementService(db).GetAsync(SupplierId);
        Assert.Equal(62_500m, recomputed.RealizedFxDifferenceUsd);
        Assert.Equal(12_500m, recomputed.AdjustedBalanceUsd);
    }

    [Fact]
    public async Task Remove_Puts_The_Balance_Back()
    {
        await using var db = NewDb();
        SeedRealScenario(db);
        await db.SaveChangesAsync();

        var service = new SupplierFxRecognitionService(db);
        await service.RecognizeAsync(SupplierId);

        await service.RemoveAsync(SupplierId);
        await db.SaveChangesAsync();

        Assert.Equal(-50_000m, await NetBalanceUsdAsync(db));
        Assert.Empty(db.ExpenseTransactions.ToList());
    }

    [Fact]
    public async Task Gain_Posts_A_Debit_On_The_Party_And_No_Expense()
    {
        await using var db = NewDb();
        // بدهی با نرخ ۷۰ ایجاد و با نرخ ۸۰ تسویه شده: همان روبل، دالرِ کمتر → سود.
        db.LedgerEntries.Add(RubEntry(1, "2026-09-03", LedgerSide.Credit, 500_000m, 35_000_000m, "Loading", 1));
        db.LedgerEntries.Add(RubEntry(2, "2026-09-04", LedgerSide.Debit, 437_500m, 35_000_000m, "SupplierViaSarrafPayment", 1));
        await db.SaveChangesAsync();

        var result = await new SupplierFxRecognitionService(db).RecognizeAsync(SupplierId);

        Assert.Equal(-62_500m, result.RecognizedUsd);
        Assert.Null(result.ExpenseTransactionId);
        Assert.Empty(db.ExpenseTransactions.ToList());

        var party = Assert.Single(db.LedgerEntries
            .Where(l => l.SourceType == SupplierFxRecognitionService.PartyLedgerSourceType)
            .ToList());
        Assert.Equal(LedgerSide.Debit, party.Side);
        Assert.Equal(62_500m, party.AmountUsd);

        var gain = Assert.Single(db.LedgerEntries
            .Where(l => l.SourceType == SupplierFxRecognitionService.GainLedgerSourceType)
            .ToList());
        Assert.Equal(LedgerSide.Credit, gain.Side);
        Assert.Null(gain.SupplierId);

        // بدهی صفر شده بود؛ بعد از شناسایی هم صفر می‌ماند.
        Assert.Equal(0m, await NetBalanceUsdAsync(db));
    }

    [Fact]
    public async Task Nothing_To_Recognize_Posts_Nothing()
    {
        await using var db = NewDb();
        db.LedgerEntries.Add(RubEntry(1, "2026-09-03", LedgerSide.Credit, 500_000m, 35_000_000m, "Loading", 1));
        db.LedgerEntries.Add(RubEntry(2, "2026-09-04", LedgerSide.Debit, 500_000m, 35_000_000m, "SupplierViaSarrafPayment", 1));
        await db.SaveChangesAsync();

        var result = await new SupplierFxRecognitionService(db).RecognizeAsync(SupplierId);

        Assert.False(result.PostedAnything);
        Assert.Equal(0m, result.RecognizedUsd);
        Assert.Equal(2, db.LedgerEntries.Count());
    }

    // ===================== کمکی =====================

    // مانده حساب تأمین‌کننده به کنوانسیون سیستم: بستانکار (بدهی ما) منهای بدهکار (پرداخت ما).
    // مثبت = به تأمین‌کننده بدهکاریم.
    private static async Task<decimal> NetBalanceUsdAsync(ApplicationDbContext db)
        => await db.LedgerEntries
            .Where(LedgerEntryOwnership.SupplierOwned(SupplierId))
            .SumAsync(l => l.Side == LedgerSide.Credit ? l.AmountUsd : -l.AmountUsd);

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
