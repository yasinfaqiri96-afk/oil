using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services.CompanyFlow;
using PTGOilSystem.Web.Services.Ledger;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// PTG-P1-03 — قراردادِ تنها نویسندهٔ دفتر کل.
///
/// دو چیز اینجا pin می‌شود:
///   ۱. سرویس هر فیلدِ درخواست را عیناً روی سطر می‌نشاند (هیچ فیلدی جا نمی‌افتد و هیچ
///      مقداری گِرد یا نرمال نمی‌شود) — همین است که «خروجی مثل قبل» را اثبات می‌کند؛
///   ۲. هیچ مسیر تازه‌ای دوباره مستقیم <c>new LedgerEntry</c> نمی‌سازد.
/// </summary>
public sealed class LedgerPostingServiceTests
{
    private static ApplicationDbContext NewDb()
        => new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase($"ledger-posting-{Guid.NewGuid():N}")
            .Options);

    private static LedgerPostingRequest FullyPopulatedRequest() => new()
    {
        SourceType = "Expense",
        SourceId = 42,
        EntryDate = new DateTime(2026, 4, 5, 0, 0, 0, DateTimeKind.Utc),
        Side = LedgerSide.Debit,
        AmountUsd = 1_234.5678m,
        Currency = "USD",
        SourceAmount = 98_765.4321m,
        SourceCurrencyCode = "RUB",
        AppliedFxRateToUsd = 0.0125m,
        AppliedCurrencyPerUsdRate = 80m,
        AppliedFxRateDate = new DateTime(2026, 4, 4, 0, 0, 0, DateTimeKind.Utc),
        AppliedFxRateSource = "Daily FX",
        Description = "شرح آزمون",
        Reference = "REF-1",
        ViaSarrafGroupId = Guid.Parse("11111111-2222-3333-4444-555555555555"),
        ContractId = 1,
        CustomerId = 2,
        SupplierId = 3,
        ServiceProviderId = 4,
        DriverId = 5,
        EmployeeId = 6,
        ShipmentId = 7,
    };

    private static void AssertMatches(LedgerPostingRequest request, LedgerEntry entry)
    {
        Assert.Equal(request.EntryDate, entry.EntryDate);
        Assert.Equal(request.Side, entry.Side);
        Assert.Equal(request.AmountUsd, entry.AmountUsd);
        Assert.Equal(request.Currency, entry.Currency);
        Assert.Equal(request.SourceAmount, entry.SourceAmount);
        Assert.Equal(request.SourceCurrencyCode, entry.SourceCurrencyCode);
        Assert.Equal(request.AppliedFxRateToUsd, entry.AppliedFxRateToUsd);
        Assert.Equal(request.AppliedCurrencyPerUsdRate, entry.AppliedCurrencyPerUsdRate);
        Assert.Equal(request.AppliedFxRateDate, entry.AppliedFxRateDate);
        Assert.Equal(request.AppliedFxRateSource, entry.AppliedFxRateSource);
        Assert.Equal(request.Description, entry.Description);
        Assert.Equal(request.SourceType, entry.SourceType);
        Assert.Equal(request.SourceId, entry.SourceId);
        Assert.Equal(request.Reference, entry.Reference);
        Assert.Equal(request.ViaSarrafGroupId, entry.ViaSarrafGroupId);
        Assert.Equal(request.ContractId, entry.ContractId);
        Assert.Equal(request.CustomerId, entry.CustomerId);
        Assert.Equal(request.SupplierId, entry.SupplierId);
        Assert.Equal(request.ServiceProviderId, entry.ServiceProviderId);
        Assert.Equal(request.DriverId, entry.DriverId);
        Assert.Equal(request.EmployeeId, entry.EmployeeId);
        Assert.Equal(request.ShipmentId, entry.ShipmentId);
    }

    [Fact]
    public void Post_Copies_Every_Field_Verbatim_And_Tracks_The_Row()
    {
        using var db = NewDb();
        var request = FullyPopulatedRequest();

        var entry = new LedgerPostingService(db).Post(request);

        AssertMatches(request, entry);
        Assert.Equal(EntityState.Added, db.Entry(entry).State);
    }

    /// <summary>
    /// هیچ گِردکردنی نباید اضافه شده باشد: مبلغ و نرخ دقیقاً همان چیزی می‌مانند که
    /// مسیرهای کسب‌وکار محاسبه کرده‌اند. این همان شرطِ «عدد عوض نمی‌شود» است.
    /// </summary>
    [Theory]
    [InlineData("0.00005")]
    [InlineData("1234567.891234")]
    [InlineData("-0.0001")]
    public void Post_Never_Rounds_Or_Normalises_Amounts(string raw)
    {
        using var db = NewDb();
        var amount = decimal.Parse(raw, System.Globalization.CultureInfo.InvariantCulture);

        var entry = new LedgerPostingService(db)
            .Post(FullyPopulatedRequest() with { AmountUsd = amount, SourceAmount = amount });

        Assert.Equal(amount, entry.AmountUsd);
        Assert.Equal(amount, entry.SourceAmount);
    }

    [Fact]
    public void Apply_Overwrites_Every_Field_On_An_Existing_Row()
    {
        using var db = NewDb();
        var existing = new LedgerEntry
        {
            SourceType = "Sale",
            SourceId = 1,
            Description = "قدیمی",
            AmountUsd = 1m,
            CustomerId = 99,
            SupplierId = 99,
        };

        var request = FullyPopulatedRequest();
        var applied = new LedgerPostingService(db).Apply(existing, request);

        Assert.Same(existing, applied);
        AssertMatches(request, applied);
    }

    [Fact]
    public void PostRange_Keeps_The_Given_Order()
    {
        using var db = NewDb();
        var first = FullyPopulatedRequest() with { Reference = "A" };
        var second = FullyPopulatedRequest() with { Reference = "B", Side = LedgerSide.Credit };

        var posted = new LedgerPostingService(db).PostRange(first, second);

        Assert.Equal(2, posted.Count);
        Assert.Equal("A", posted[0].Reference);
        Assert.Equal("B", posted[1].Reference);
        Assert.Equal(LedgerSide.Credit, posted[1].Side);
    }

    [Fact]
    public void Rejects_A_Row_That_Could_Never_Be_Traced_Back()
    {
        using var db = NewDb();
        var service = new LedgerPostingService(db);

        Assert.Throws<LedgerPostingValidationException>(
            () => service.Post(FullyPopulatedRequest() with { SourceType = "   " }));
        Assert.Throws<LedgerPostingValidationException>(
            () => service.Post(FullyPopulatedRequest() with { SourceId = 0 }));
        Assert.Throws<LedgerPostingValidationException>(
            () => service.Post(FullyPopulatedRequest() with { Currency = "" }));
        Assert.Throws<LedgerPostingValidationException>(
            () => service.Post(FullyPopulatedRequest() with { Side = (LedgerSide)99 }));
    }

    /// <summary>
    /// چند مسیرِ موجود عمداً سطر دفتر را پیش از داشتنِ شناسهٔ سند می‌سازند. آن حالت باید
    /// صریح باشد، نه استثنای خاموش.
    /// </summary>
    [Fact]
    public void Deferred_SourceId_Is_Allowed_Only_When_Asked_For()
    {
        using var db = NewDb();
        var service = new LedgerPostingService(db);

        var entry = service.Post(FullyPopulatedRequest() with { SourceId = 0, AllowDeferredSourceId = true });

        Assert.Equal(0, entry.SourceId);
    }

    [Fact]
    public async Task ReverseAsync_Flips_The_Side_Marks_The_Reference_And_Refuses_A_Second_Reversal()
    {
        using var db = NewDb();
        var service = new LedgerPostingService(db);

        var original = service.Post(FullyPopulatedRequest());
        await db.SaveChangesAsync();

        var reversal = await service.ReverseAsync(
            original,
            new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
            "لغو آزمون",
            "FALLBACK");

        Assert.NotNull(reversal);
        Assert.Equal(LedgerSide.Credit, reversal!.Side);
        Assert.Equal(original.AmountUsd, reversal.AmountUsd);
        Assert.Equal(original.SourceType, reversal.SourceType);
        Assert.Equal(original.SourceId, reversal.SourceId);
        Assert.EndsWith(CompanyFlowSourceTypes.ReversalReferenceSuffix, reversal.Reference);
        Assert.True(CompanyFlowSourceTypes.IsReversal(reversal.SourceType, reversal.Reference));

        // برگشتِ دوم چیزی نمی‌سازد — همان محافظی که پیش از تمرکز هم وجود داشت.
        var second = await service.ReverseAsync(
            original,
            new DateTime(2026, 5, 2, 0, 0, 0, DateTimeKind.Utc),
            "لغو دوم",
            "FALLBACK");

        Assert.Null(second);
        Assert.Equal(2, await db.LedgerEntries.CountAsync());
    }

    /// <summary>
    /// نگهبانِ P1-03: هیچ فایلی بیرون از <c>Services/Ledger/</c> اجازه ندارد دوباره
    /// مستقیماً یک سطر دفتر کل بسازد یا اضافه کند. بدون این تست، فاز بعد آرام‌آرام به
    /// همان ۱۸ نقطهٔ پراکنده برمی‌گشت و هیچ تستِ رفتاری‌ای هم قرمز نمی‌شد.
    /// </summary>
    [Fact]
    public void No_Code_Outside_The_Posting_Service_Writes_A_Ledger_Row_Directly()
    {
        var web = Path.Combine(FindRepositoryRoot(), "src", "PTGOilSystem.Web");
        var offenders = new List<string>();

        var writer = new Regex(
            @"new\s+LedgerEntry\s*[\{\(]|LedgerEntries\s*\.\s*Add(Range)?\s*\(",
            RegexOptions.Compiled);

        foreach (var file in Directory.EnumerateFiles(web, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(web, file).Replace('\\', '/');

            // خودِ سرویس، و Migrationها که schema می‌سازند نه سند.
            if (relative.StartsWith("Services/Ledger/", StringComparison.Ordinal)
                || relative.StartsWith("Migrations/", StringComparison.Ordinal)
                || relative.StartsWith("obj/", StringComparison.Ordinal)
                || relative.StartsWith("bin/", StringComparison.Ordinal))
            {
                continue;
            }

            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                // فهرستِ نمایشیِ صفحهٔ سود و زیان محموله هم‌نام است ولی ViewModel است، نه دفتر.
                if (lines[i].Contains("ShipmentPnlLedgerItemViewModel", StringComparison.Ordinal))
                {
                    continue;
                }

                if (writer.IsMatch(lines[i]))
                {
                    offenders.Add($"{relative}:{i + 1}: {lines[i].Trim()}");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "سطر دفتر کل فقط از ILedgerPostingService ساخته می‌شود. موارد زیر مستقیم می‌نویسند:\n"
            + string.Join("\n", offenders));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ptg-oil-system.sln")))
        {
            directory = directory.Parent;
        }

        Assert.True(directory is not null, "ریشهٔ مخزن پیدا نشد.");
        return directory!.FullName;
    }
}
