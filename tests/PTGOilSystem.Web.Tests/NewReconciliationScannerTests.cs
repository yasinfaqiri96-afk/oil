using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services.Reconciliation;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// اسکنرهای تازهٔ این فاز. هر سه فقط‌خواندنی‌اند: هیچ‌کدام چیزی را «درست نمی‌کنند»، فقط
/// چیزی را که وگرنه بی‌صدا می‌ماند قابل دیدن می‌کنند.
/// </summary>
public sealed class NewReconciliationScannerTests
{
    private static ApplicationDbContext NewDb()
        => new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase($"new-scanners-{Guid.NewGuid():N}")
            .Options);

    private static LedgerIntegrityFinding Find(LedgerIntegrityReport report, string code)
        => report.Findings.Single(f => f.Code == code);

    private static SalesTransaction Sale(int id) => new()
    {
        Id = id,
        CustomerId = 1,
        ProductId = 1,
        InvoiceNumber = $"INV-{id}",
        SaleDate = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc),
        QuantityMt = 1m,
        Currency = "USD",
        UnitPriceInCurrency = 1m,
        UnitPriceUsd = 1m,
        TotalInCurrency = 1m,
        TotalUsd = 1m,
    };

    [Fact]
    public async Task Healthy_Data_Produces_No_Findings_From_The_New_Scanners()
    {
        await using var db = NewDb();

        var original = Sale(1);
        original.IsCancelled = true;
        original.ReplacementSaleId = 2;
        var replacement = Sale(2);
        replacement.CorrectedFromSaleId = 1;
        db.SalesTransactions.AddRange(original, replacement);
        await db.SaveChangesAsync();

        var report = await new LedgerIntegrityReconciliationService(db).RunAsync();

        Assert.Equal(0, Find(report, "SALE-CORRECTION-CHAIN").Count);
        Assert.Equal(0, Find(report, "CONCURRENCY-VERSION-INVALID").Count);
        Assert.Equal(0, Find(report, "PARTNER-PERIOD-COST-BASIS").Count);
    }

    [Fact]
    public async Task A_Replacement_Link_On_A_Live_Sale_Is_Reported()
    {
        await using var db = NewDb();

        var original = Sale(1);       // جایگزین دارد ولی ابطال نشده
        original.ReplacementSaleId = 2;
        var replacement = Sale(2);
        replacement.CorrectedFromSaleId = 1;
        db.SalesTransactions.AddRange(original, replacement);
        await db.SaveChangesAsync();

        var finding = Find(await new LedgerIntegrityReconciliationService(db).RunAsync(), "SALE-CORRECTION-CHAIN");

        Assert.Equal(1, finding.Count);
        Assert.Contains(finding.Samples, s => s.Contains("ابطال نشده"));
    }

    [Fact]
    public async Task A_One_Sided_Correction_Link_Is_Reported()
    {
        await using var db = NewDb();

        var original = Sale(1);
        original.IsCancelled = true;
        original.ReplacementSaleId = 2;
        db.SalesTransactions.AddRange(original, Sale(2)); // سرِ دیگر برنمی‌گردد
        await db.SaveChangesAsync();

        var finding = Find(await new LedgerIntegrityReconciliationService(db).RunAsync(), "SALE-CORRECTION-CHAIN");

        Assert.Equal(1, finding.Count);
        Assert.Contains(finding.Samples, s => s.Contains("یک‌طرفه"));
    }

    /// <summary>
    /// نسخهٔ نامعتبر از مسیرِ خودِ برنامه اصلاً ساخته نمی‌شود: هر ذخیره آن را از مقدارِ
    /// اصلیِ سطر بازمی‌سازد. این تست همان قاعده را pin می‌کند — پس اسکنر فقط برای
    /// نوشتنِ خامِ بیرون از برنامه معنی دارد، و روی دادهٔ سالم صفر می‌ماند.
    /// </summary>
    [Fact]
    public async Task The_Application_Cannot_Write_An_Invalid_Version()
    {
        await using var db = NewDb();

        var sale = Sale(1);
        sale.Version = 0;
        db.SalesTransactions.Add(sale);
        await db.SaveChangesAsync();
        Assert.Equal(1L, sale.Version);

        // حتی تلاشِ صریح برای برگرداندنِ نسخه به صفر هم اثر ندارد.
        sale.Version = 0;
        db.Entry(sale).Property(s => s.Version).IsModified = true;
        sale.Notes = "لمسِ سطر";
        await db.SaveChangesAsync();
        Assert.Equal(2L, sale.Version);

        Assert.Equal(
            0,
            Find(await new LedgerIntegrityReconciliationService(db).RunAsync(), "CONCURRENCY-VERSION-INVALID").Count);
    }

    /// <summary>
    /// PTG ۱۲-D — تنها حالتی که مدلِ «وزنِ عایدِ فروش» می‌تواند نتیجهٔ متفاوتی بدهد:
    /// قراردادی که هم چند بازهٔ سهم دارد و هم بهای واحدِ ناهمگون.
    /// </summary>
    [Fact]
    public async Task A_Multi_Period_Contract_With_Mixed_Unit_Costs_Is_Reported()
    {
        await using var db = NewDb();

        db.Companies.Add(new Company { Id = 1, Code = "PTG", Name = "PTG" });
        db.Products.Add(new Product { Id = 1, Code = "GO", Name = "Gas Oil" });
        db.Contracts.Add(new Contract
        {
            Id = 1,
            ContractName = "چندبازه‌ای",
            ContractNumber = "PUR-1",
            ContractType = ContractType.Purchase,
            CompanyId = 1,
            ProductId = 1,
            QuantityMt = 100m,
            ContractDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            OwnershipType = ContractOwnershipType.Partnership,
        });

        db.ContractPartners.AddRange(
            new ContractPartner { ContractId = 1, PartnerId = 1, SharePercent = 50m, EffectiveFrom = new DateTime(2026, 1, 1) },
            new ContractPartner { ContractId = 1, PartnerId = 2, SharePercent = 50m, EffectiveFrom = new DateTime(2026, 1, 1) },
            new ContractPartner { ContractId = 1, PartnerId = 1, SharePercent = 80m, EffectiveFrom = new DateTime(2026, 7, 1) },
            new ContractPartner { ContractId = 1, PartnerId = 2, SharePercent = 20m, EffectiveFrom = new DateTime(2026, 7, 1) });

        db.LoadingRegisters.AddRange(
            new LoadingRegister { ContractId = 1, ProductId = 1, LoadingDate = new DateTime(2026, 2, 1), LoadedQuantityMt = 10m, LoadingPriceUsd = 500m },
            new LoadingRegister { ContractId = 1, ProductId = 1, LoadingDate = new DateTime(2026, 8, 1), LoadedQuantityMt = 10m, LoadingPriceUsd = 620m });

        await db.SaveChangesAsync();

        var finding = Find(await new LedgerIntegrityReconciliationService(db).RunAsync(), "PARTNER-PERIOD-COST-BASIS");

        Assert.Equal(1, finding.Count);
        Assert.Contains(finding.Samples, s => s.Contains("قرارداد 1"));
    }

    [Fact]
    public async Task A_Multi_Period_Contract_With_One_Unit_Cost_Is_Not_Reported()
    {
        await using var db = NewDb();

        db.Companies.Add(new Company { Id = 1, Code = "PTG", Name = "PTG" });
        db.Products.Add(new Product { Id = 1, Code = "GO", Name = "Gas Oil" });
        db.Contracts.Add(new Contract
        {
            Id = 1,
            ContractName = "چندبازه‌ای",
            ContractNumber = "PUR-1",
            ContractType = ContractType.Purchase,
            CompanyId = 1,
            ProductId = 1,
            QuantityMt = 100m,
            ContractDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            OwnershipType = ContractOwnershipType.Partnership,
        });

        db.ContractPartners.AddRange(
            new ContractPartner { ContractId = 1, PartnerId = 1, SharePercent = 50m, EffectiveFrom = new DateTime(2026, 1, 1) },
            new ContractPartner { ContractId = 1, PartnerId = 1, SharePercent = 80m, EffectiveFrom = new DateTime(2026, 7, 1) });

        db.LoadingRegisters.AddRange(
            new LoadingRegister { ContractId = 1, ProductId = 1, LoadingDate = new DateTime(2026, 2, 1), LoadedQuantityMt = 10m, LoadingPriceUsd = 500m },
            new LoadingRegister { ContractId = 1, ProductId = 1, LoadingDate = new DateTime(2026, 8, 1), LoadedQuantityMt = 10m, LoadingPriceUsd = 500m });

        await db.SaveChangesAsync();

        // بهای واحد یکسان ⇒ تقسیمِ بر پایهٔ عاید دقیقاً درست است ⇒ چیزی گزارش نمی‌شود.
        Assert.Equal(
            0,
            Find(await new LedgerIntegrityReconciliationService(db).RunAsync(), "PARTNER-PERIOD-COST-BASIS").Count);
    }
}
