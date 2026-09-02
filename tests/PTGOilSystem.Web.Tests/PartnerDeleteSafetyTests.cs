using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.DeleteSafety;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// PTG-P2-01 — حذف شریک باید هر ارجاعِ واقعی را ببیند.
///
/// شکستِ واقعی: <c>EvaluatePartnerAsync</c> فقط <c>ContractPartners</c> را می‌دید، پس
/// شریکی که تنها «تأمین مالی» کرده بود <c>CanDelete=true</c> می‌گرفت؛ کلید خارجیِ
/// <c>Restrict</c> جلوی خرابیِ داده را می‌گرفت ولی کاربر خطای ۵۰۰ می‌دید.
/// </summary>
public sealed class PartnerDeleteSafetyTests
{
    private const int PartnerId = 77;

    private static ApplicationDbContext NewDb()
        => new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static async Task<ApplicationDbContext> WithPartnerAsync()
    {
        var db = NewDb();
        db.Partners.Add(new Partner { Id = PartnerId, Code = "P-77", Name = "شریک آزمایشی" });
        await db.SaveChangesAsync();
        return db;
    }

    [Fact]
    public async Task APartnerWithNoReference_CanStillBeDeleted()
    {
        await using var db = await WithPartnerAsync();

        var result = await new MasterDataDeleteSafetyService(db).EvaluatePartnerAsync(PartnerId);

        Assert.True(result.CanDelete);
    }

    // ------------------------------------------------------------------
    // هر ارجاعی که پیش‌تر دیده نمی‌شد
    // ------------------------------------------------------------------

    [Fact]
    public async Task FundingAPayment_BlocksTheDelete()
    {
        await using var db = await WithPartnerAsync();
        db.PaymentTransactions.Add(new PaymentTransaction
        {
            PaymentDate = new DateTime(2026, 5, 1),
            PaidByPartnerId = PartnerId
        });
        await db.SaveChangesAsync();

        var result = await new MasterDataDeleteSafetyService(db).EvaluatePartnerAsync(PartnerId);

        Assert.False(result.CanDelete);
        Assert.Contains("تأمین‌مالی", result.DependencySummary);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BothSidesOfAPartnerSettlement_BlockTheDelete(bool payingSide)
    {
        await using var db = await WithPartnerAsync();
        db.PartnerSettlements.Add(new PartnerSettlement
        {
            SettlementDate = new DateTime(2026, 5, 2),
            FromPartnerId = payingSide ? PartnerId : 5,
            ToPartnerId = payingSide ? 5 : PartnerId,
            Amount = 1_000m,
            Currency = "USD",
            AppliedFxRateToUsd = 1m,
            AmountUsd = 1_000m
        });
        await db.SaveChangesAsync();

        var result = await new MasterDataDeleteSafetyService(db).EvaluatePartnerAsync(PartnerId);

        Assert.False(result.CanDelete);
        Assert.Contains("تسویه", result.DependencySummary);
    }

    [Fact]
    public async Task HoldingTheSaleProceedsOfAContract_BlocksTheDelete()
    {
        await using var db = await WithPartnerAsync();
        db.Contracts.Add(new Contract
        {
            ContractNumber = "PUR-01",
            SaleProceedsHolderPartnerId = PartnerId
        });
        await db.SaveChangesAsync();

        var result = await new MasterDataDeleteSafetyService(db).EvaluatePartnerAsync(PartnerId);

        Assert.False(result.CanDelete);
        Assert.Contains("عاید فروش", result.DependencySummary);
    }

    [Fact]
    public async Task OwningAShareOfAnOperationalAsset_BlocksTheDelete()
    {
        await using var db = await WithPartnerAsync();
        db.AssetOwnershipShares.Add(new AssetOwnershipShare
        {
            OperationalAssetId = 3,
            OwnerType = AssetOwnerType.Partner,
            PartnerId = PartnerId,
            SharePercent = 50m
        });
        await db.SaveChangesAsync();

        var result = await new MasterDataDeleteSafetyService(db).EvaluatePartnerAsync(PartnerId);

        Assert.False(result.CanDelete);
        Assert.Contains("مالکیت", result.DependencySummary);
    }

    [Fact]
    public async Task AShareOfAssetRent_BlocksTheDelete()
    {
        await using var db = await WithPartnerAsync();
        db.AssetRentShares.Add(new AssetRentShare
        {
            AssetRentTransactionId = 9,
            OwnerType = AssetOwnerType.Partner,
            PartnerId = PartnerId,
            SharePercent = 25m,
            ShareAmountUsd = 100m
        });
        await db.SaveChangesAsync();

        var result = await new MasterDataDeleteSafetyService(db).EvaluatePartnerAsync(PartnerId);

        Assert.False(result.CanDelete);
        Assert.Contains("کرایه", result.DependencySummary);
    }

    [Fact]
    public async Task PartnerLedgerEntry_BlocksTheDelete()
    {
        await using var db = await WithPartnerAsync();
        db.LedgerEntries.Add(new LedgerEntry
        {
            EntryDate = new DateTime(2026, 5, 3),
            Side = LedgerSide.Credit,
            AmountUsd = 100m,
            Currency = "USD",
            Description = "Partner asset rent",
            SourceType = AssetRentLedgerFactory.LedgerSourceType,
            SourceId = 9,
            PartnerId = PartnerId
        });
        await db.SaveChangesAsync();

        var result = await new MasterDataDeleteSafetyService(db).EvaluatePartnerAsync(PartnerId);

        Assert.False(result.CanDelete);
        Assert.Contains("مالی شریک", result.DependencySummary);
    }

    [Fact]
    public async Task ContractPartnership_StillBlocksTheDelete()
    {
        await using var db = await WithPartnerAsync();
        db.ContractPartners.Add(new ContractPartner
        {
            ContractId = 4,
            PartnerId = PartnerId,
            SharePercent = 100m,
            EffectiveFrom = new DateTime(2026, 1, 1)
        });
        await db.SaveChangesAsync();

        var result = await new MasterDataDeleteSafetyService(db).EvaluatePartnerAsync(PartnerId);

        Assert.False(result.CanDelete);
        Assert.Contains("مشارکتی", result.DependencySummary);
    }

    /// <summary>ارجاعِ شریکِ دیگر نباید این شریک را قفل کند.</summary>
    [Fact]
    public async Task AReferenceToAnotherPartner_DoesNotBlockThisOne()
    {
        await using var db = await WithPartnerAsync();
        db.PaymentTransactions.Add(new PaymentTransaction
        {
            PaymentDate = new DateTime(2026, 5, 1),
            PaidByPartnerId = 999
        });
        await db.SaveChangesAsync();

        var result = await new MasterDataDeleteSafetyService(db).EvaluatePartnerAsync(PartnerId);

        Assert.True(result.CanDelete);
    }
}
