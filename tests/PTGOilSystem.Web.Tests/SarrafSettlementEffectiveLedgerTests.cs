using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PTGOilSystem.Web.Controllers;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.PartyStatements;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.PartyStatements;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// وقتی یک «تسویهٔ صراف» ویرایش می‌شود، سرویس ثبتِ قبلی را برگشت (reverse) و نسخهٔ تازه را
/// repost می‌کند؛ در نتیجه سه سطرِ دفترکل با یک SourceId می‌مانند (اصلی/برگشت/repost). این
/// تست‌ها تضمین می‌کنند صورت‌حساب رسمی و جمع‌ها فقط اثرِ جاری (سطری که تسویهٔ Posted به آن
/// اشاره می‌کند) را می‌بینند، در حالی که هر سه سطر برای تاریخچه/Audit در دفترکل می‌مانند.
///
/// کلید قطعیِ زنجیره: SarrafSettlement.LedgerEntryId / ExchangeDifferenceLedgerEntryId.
/// هیچ‌جا با مبلغ/تاریخ/توضیح حدس زده نمی‌شود.
/// </summary>
public sealed class SarrafSettlementEffectiveLedgerTests
{
    private const string Family = SarrafSettlementService.SupplierLedgerSourceType;      // "SarrafSettlement"
    private const string Reversal = SarrafSettlementService.EditReversalSourceType;      // "SarrafSettlementEditReversal"
    private const string Cancel = SarrafSettlementService.CancelSourceType;              // "SarrafSettlementCancel"

    // ۱) تسویهٔ ویرایش‌نشده: یک سطر مؤثر.
    [Fact]
    public async Task UneditedSettlement_ShowsSingleRow()
    {
        await using var db = CreateDb();
        var (supplier, contract) = await SeedSupplierContractAsync(db);
        await AddSettlementAsync(db, supplier.Id, contract.Id, 200_000_000m,
            effective: Leg(Family, LedgerSide.Debit, 200_000_000m, supplier.Id, contract.Id));

        var statement = await SupplierStatementRubAsync(db, supplier.Id);

        var row = Assert.Single(statement.Rows.Where(r => !r.IsOpeningBalance));
        Assert.Equal(200_000_000m, row.OriginalAmount);
        Assert.Equal(200_000_000m, statement.Summary.TotalCreditRub!.Value);
    }

    // ۲و۳و۴و۶) اصلی+برگشت+repost: فقط یک سطر مؤثر، با مبلغ و قراردادِ نسخهٔ جدید؛ اصلی/برگشت
    // در جمع نمی‌آیند.
    [Fact]
    public async Task OriginalReversalRepost_ShowsOnlyEffectiveWithNewAmountAndContract()
    {
        await using var db = CreateDb();
        var (supplier, contract) = await SeedSupplierContractAsync(db);

        // ثبتِ اصلی بدون قرارداد (منسوخ) + برگشت + repost با قرارداد (مؤثر).
        await AddSettlementAsync(db, supplier.Id, contract.Id, 300_000_000m,
            effective: Leg(Family, LedgerSide.Debit, 300_000_000m, supplier.Id, contract.Id),
            superseded: new[]
            {
                Leg(Family, LedgerSide.Debit, 300_000_000m, supplier.Id, contractId: null),
                Leg(Reversal, LedgerSide.Credit, 300_000_000m, supplier.Id, contractId: null)
            });

        var statement = await SupplierStatementRubAsync(db, supplier.Id);

        var row = Assert.Single(statement.Rows.Where(r => !r.IsOpeningBalance));
        Assert.Equal(Family, row.SourceType);
        Assert.Equal(300_000_000m, row.OriginalAmount);            // مبلغ = repost
        Assert.Equal(contract.ContractNumber, row.ContractNumber);  // قرارداد = نسخهٔ جدید
        Assert.Equal(300_000_000m, statement.Summary.TotalCreditRub!.Value); // نه ۶۰۰ میلیون
    }

    // ۵) اصلی و برگشت در دفترکل (Audit) باقی می‌مانند — هیچ سطری حذف نمی‌شود.
    [Fact]
    public async Task SupersededAndReversalRows_RemainInLedgerForAudit()
    {
        await using var db = CreateDb();
        var (supplier, contract) = await SeedSupplierContractAsync(db);
        await AddSettlementAsync(db, supplier.Id, contract.Id, 300_000_000m,
            effective: Leg(Family, LedgerSide.Debit, 300_000_000m, supplier.Id, contract.Id),
            superseded: new[]
            {
                Leg(Family, LedgerSide.Debit, 300_000_000m, supplier.Id, contractId: null),
                Leg(Reversal, LedgerSide.Credit, 300_000_000m, supplier.Id, contractId: null)
            });

        // سه سطرِ دفترکل هنوز موجودند (اصلی + برگشت + repost).
        Assert.Equal(3, await db.LedgerEntries.CountAsync());
    }

    // ۷) تسویهٔ لغوشدهٔ بدون repost: اثر مالیِ صفر در صورت‌حساب رسمی.
    [Fact]
    public async Task CancelledSettlementWithoutRepost_HasZeroEffect()
    {
        await using var db = CreateDb();
        var (supplier, contract) = await SeedSupplierContractAsync(db);

        var original = ToLedger(Leg(Family, LedgerSide.Debit, 300_000_000m, supplier.Id, contract.Id));
        var cancel = ToLedger(Leg(Cancel, LedgerSide.Credit, 300_000_000m, supplier.Id, contract.Id));
        db.LedgerEntries.AddRange(original, cancel);
        await db.SaveChangesAsync();
        db.SarrafSettlements.Add(NewSettlement(supplier.Id, contract.Id, 300_000_000m,
            SarrafSettlementStatus.Cancelled, original.Id));
        await db.SaveChangesAsync();

        var statement = await SupplierStatementRubAsync(db, supplier.Id);

        Assert.DoesNotContain(statement.Rows, r => !r.IsOpeningBalance);
        Assert.Equal(0m, statement.Summary.ClosingBalance);
    }

    // ۸) دو بار ویرایش: فقط آخرین revision نمایش داده شود.
    [Fact]
    public async Task TwoEdits_ShowOnlyLatestRevision()
    {
        await using var db = CreateDb();
        var (supplier, contract) = await SeedSupplierContractAsync(db);
        await AddSettlementAsync(db, supplier.Id, contract.Id, 300_000_000m,
            effective: Leg(Family, LedgerSide.Debit, 330_000_000m, supplier.Id, contract.Id), // repost نهایی
            superseded: new[]
            {
                Leg(Family, LedgerSide.Debit, 300_000_000m, supplier.Id, contractId: null),   // اصلی
                Leg(Reversal, LedgerSide.Credit, 300_000_000m, supplier.Id, contractId: null), // برگشت اصلی
                Leg(Family, LedgerSide.Debit, 310_000_000m, supplier.Id, contract.Id),         // repost ۱ (منسوخ)
                Leg(Reversal, LedgerSide.Credit, 310_000_000m, supplier.Id, contract.Id)        // برگشت repost ۱
            });

        var statement = await SupplierStatementRubAsync(db, supplier.Id);

        var row = Assert.Single(statement.Rows.Where(r => !r.IsOpeningBalance));
        Assert.Equal(330_000_000m, row.OriginalAmount);
        Assert.Equal(330_000_000m, statement.Summary.TotalCreditRub!.Value);
    }

    // ۹) چند تسویه با مبلغ یکسان با هم اشتباه نشوند — تفکیک با FK نه مبلغ.
    [Fact]
    public async Task SettlementsWithIdenticalAmounts_AreNotConfused()
    {
        await using var db = CreateDb();
        var (supplier, contract) = await SeedSupplierContractAsync(db);
        // دو تسویهٔ ۳۰۰ میلیونی، هر کدام یک بار ویرایش‌شده.
        await AddSettlementAsync(db, supplier.Id, contract.Id, 300_000_000m,
            effective: Leg(Family, LedgerSide.Debit, 300_000_000m, supplier.Id, contract.Id),
            superseded: new[]
            {
                Leg(Family, LedgerSide.Debit, 300_000_000m, supplier.Id, contractId: null),
                Leg(Reversal, LedgerSide.Credit, 300_000_000m, supplier.Id, contractId: null)
            });
        await AddSettlementAsync(db, supplier.Id, contract.Id, 300_000_000m,
            effective: Leg(Family, LedgerSide.Debit, 300_000_000m, supplier.Id, contract.Id),
            superseded: new[]
            {
                Leg(Family, LedgerSide.Debit, 300_000_000m, supplier.Id, contractId: null),
                Leg(Reversal, LedgerSide.Credit, 300_000_000m, supplier.Id, contractId: null)
            });

        var statement = await SupplierStatementRubAsync(db, supplier.Id);

        // دو سطرِ مؤثر (یکی برای هر تسویه) — نه یک، نه چهار.
        Assert.Equal(2, statement.Rows.Count(r => !r.IsOpeningBalance));
        Assert.Equal(600_000_000m, statement.Summary.TotalCreditRub!.Value);
    }

    // ۱۰و۱۱) پرداخت‌های ۲۰۰/۳۰۰/۵۰۰ میلیون هرکدام یک‌بار؛ مجموع دقیقاً ۱ میلیارد روبل.
    // بازسازیِ دقیقِ شکلِ دادهٔ واقعی: ۳۰۰ میلیون ویرایش‌شده (اصلی+برگشت+repost)،
    // ۲۰۰ میلیون تسویهٔ ساده، ۵۰۰ میلیون پرداختِ via-sarraf (خارج از خانوادهٔ تسویه).
    [Fact]
    public async Task RealShape_TwoHundredThreeHundredFiveHundred_EachCountedOnce_TotalOneBillion()
    {
        await using var db = CreateDb();
        var (supplier, contract) = await SeedSupplierContractAsync(db);

        await AddSettlementAsync(db, supplier.Id, contract.Id, 200_000_000m,
            effective: Leg(Family, LedgerSide.Debit, 200_000_000m, supplier.Id, contract.Id));

        await AddSettlementAsync(db, supplier.Id, contract.Id, 300_000_000m,
            effective: Leg(Family, LedgerSide.Debit, 300_000_000m, supplier.Id, contract.Id),
            superseded: new[]
            {
                Leg(Family, LedgerSide.Debit, 300_000_000m, supplier.Id, contractId: null),
                Leg(Reversal, LedgerSide.Credit, 300_000_000m, supplier.Id, contractId: null)
            });

        // پرداخت via-sarraf (۵۰۰ میلیون) — نوعِ SourceType متفاوت، فیلترِ تسویه لمسش نمی‌کند.
        db.LedgerEntries.Add(new LedgerEntry
        {
            EntryDate = new DateTime(2026, 7, 23),
            Side = LedgerSide.Debit,
            AmountUsd = 6_363_662.9753m,
            Currency = "RUB",
            SourceAmount = 500_000_000m,
            SourceCurrencyCode = "RUB",
            AppliedFxRateToUsd = 0.0127m,
            SupplierId = supplier.Id,
            ContractId = contract.Id,
            SourceType = PaymentsController.ViaSarrafSupplierLedgerSourceType,
            SourceId = 999,
            Description = "پرداخت از طریق صراف"
        });
        await db.SaveChangesAsync();

        var statement = await SupplierStatementRubAsync(db, supplier.Id);

        var amounts = statement.Rows
            .Where(r => !r.IsOpeningBalance)
            .Select(r => r.OriginalAmount)
            .OrderBy(a => a)
            .ToList();
        Assert.Equal(new decimal?[] { 200_000_000m, 300_000_000m, 500_000_000m }, amounts);
        Assert.Equal(1_000_000_000m, statement.Summary.TotalCreditRub!.Value);
    }

    // ۱۳) سایر Queryها (مسیر مبلغِ روبلیِ قرارداد) ثبتِ منسوخ را دوباره جمع نزنند.
    [Fact]
    public async Task EffectiveFilterQuery_KeepsEffectiveDropsSupersededAndReversal_KeepsNonFamily()
    {
        await using var db = CreateDb();
        var (supplier, contract) = await SeedSupplierContractAsync(db);
        await AddSettlementAsync(db, supplier.Id, contract.Id, 300_000_000m,
            effective: Leg(Family, LedgerSide.Debit, 300_000_000m, supplier.Id, contract.Id),
            superseded: new[]
            {
                Leg(Family, LedgerSide.Debit, 300_000_000m, supplier.Id, contractId: null),
                Leg(Reversal, LedgerSide.Credit, 300_000_000m, supplier.Id, contractId: null)
            });
        // سطرِ غیرخانواده (بارگیری) باید بماند.
        db.LedgerEntries.Add(new LedgerEntry
        {
            EntryDate = new DateTime(2026, 1, 1), Side = LedgerSide.Credit, AmountUsd = 10m,
            Currency = "USD", SupplierId = supplier.Id, ContractId = contract.Id,
            SourceType = "Loading", SourceId = 1, Description = "Loading"
        });
        await db.SaveChangesAsync();

        var kept = await db.LedgerEntries
            .WhereEffectiveSarrafSettlementLegs(db)
            .ToListAsync();

        Assert.Equal(2, kept.Count); // مؤثرِ تسویه + بارگیری
        Assert.Contains(kept, l => l.SourceType == "Loading");
        Assert.Single(kept, l => l.SourceType == Family);
        Assert.DoesNotContain(kept, l => l.SourceType == Reversal);
    }

    // ۱۴) زنجیرهٔ سالم هرگز خاموش حذف نمی‌شود: سطری که تسویهٔ Posted به آن اشاره می‌کند همیشه
    // مؤثر است؛ فقط سطرهای بی‌ارجاع کنار می‌روند. (نقضِ داده مثل تسویهٔ Posted بدونِ سطر، توسط
    // ReconciliationController به‌عنوان «نیازمند بازبینی» گزارش می‌شود، نه اینجا خاموش.)
    [Fact]
    public async Task EffectiveLeg_IsNeverDropped_OnlyUnreferencedRowsAreExcluded()
    {
        await using var db = CreateDb();
        var (supplier, contract) = await SeedSupplierContractAsync(db);
        var effective = ToLedger(Leg(Family, LedgerSide.Debit, 300_000_000m, supplier.Id, contract.Id));
        var orphan = ToLedger(Leg(Family, LedgerSide.Debit, 999m, supplier.Id, contract.Id)); // بی‌ارجاع
        db.LedgerEntries.AddRange(effective, orphan);
        await db.SaveChangesAsync();
        db.SarrafSettlements.Add(NewSettlement(supplier.Id, contract.Id, 300_000_000m,
            SarrafSettlementStatus.Posted, effective.Id));
        await db.SaveChangesAsync();

        var effectiveIds = await SarrafSettlementLedgerEffectiveness.LoadEffectiveLedgerEntryIdsAsync(db);

        Assert.Contains(effective.Id, effectiveIds);
        Assert.DoesNotContain(orphan.Id, effectiveIds);
        Assert.False(SarrafSettlementLedgerEffectiveness.IsSuperseded(effective.SourceType, effective.Id, effectiveIds));
        Assert.True(SarrafSettlementLedgerEffectiveness.IsSuperseded(orphan.SourceType, orphan.Id, effectiveIds));
    }

    // ---- helpers ----

    private sealed record LegSpec(string SourceType, LedgerSide Side, decimal Rub, int SupplierId, int? ContractId);

    private static LegSpec Leg(string sourceType, LedgerSide side, decimal rub, int supplierId, int? contractId)
        => new(sourceType, side, rub, supplierId, contractId);

    private static async Task AddSettlementAsync(
        ApplicationDbContext db,
        int supplierId,
        int contractId,
        decimal chargedRub,
        LegSpec effective,
        IEnumerable<LegSpec>? superseded = null)
    {
        // سطرهای منسوخ/برگشت اول (تا ترتیب تاریخی مثل واقعیت باشد)، سپس سطرِ مؤثر.
        foreach (var s in superseded ?? Enumerable.Empty<LegSpec>())
        {
            db.LedgerEntries.Add(ToLedger(s));
        }

        var effectiveLeg = ToLedger(effective);
        db.LedgerEntries.Add(effectiveLeg);
        await db.SaveChangesAsync();

        db.SarrafSettlements.Add(NewSettlement(supplierId, contractId, chargedRub,
            SarrafSettlementStatus.Posted, effectiveLeg.Id));
        await db.SaveChangesAsync();
    }

    private static LedgerEntry ToLedger(LegSpec s)
        => new()
        {
            EntryDate = new DateTime(2026, 7, 22),
            Side = s.Side,
            AmountUsd = s.Rub / 78m,
            Currency = "RUB",
            SourceAmount = s.Rub,
            SourceCurrencyCode = "RUB",
            AppliedFxRateToUsd = 1m / 78m,
            SupplierId = s.SupplierId,
            ContractId = s.ContractId,
            SourceType = s.SourceType,
            SourceId = 0,
            Description = "Sarraf settlement supplier reduction"
        };

    private static SarrafSettlement NewSettlement(
        int supplierId,
        int contractId,
        decimal chargedRub,
        SarrafSettlementStatus status,
        int ledgerEntryId)
        => new()
        {
            SarrafId = 1,
            SettlementDate = new DateTime(2026, 7, 22),
            Direction = SarrafSettlementDirection.Out,
            CounterpartyType = SarrafSettlementCounterpartyType.Supplier,
            SupplierId = supplierId,
            ContractId = contractId,
            Status = status,
            SarrafCurrency = "RUB",
            SarrafChargedAmount = chargedRub,
            SarrafChargedAmountUsd = chargedRub / 78m,
            SupplierAcceptedAmount = chargedRub,
            SupplierAcceptedCurrency = "RUB",
            SupplierAcceptedAmountUsd = chargedRub / 78m,
            LedgerEntryId = ledgerEntryId
        };

    private static async Task<(Supplier supplier, Contract contract)> SeedSupplierContractAsync(ApplicationDbContext db)
    {
        var supplier = new Supplier { Name = "SOLVEX FZE" };
        var company = new Company { Code = "C1", Name = "Noorzad" };
        db.AddRange(supplier, company);
        await db.SaveChangesAsync();
        var sarraf = new Sarraf { Id = 1, Name = "Noorzad Dubai", IsActive = true };
        db.Sarrafs.Add(sarraf);
        var contract = new Contract
        {
            ContractNumber = "Diesel 610",
            ContractType = ContractType.Purchase,
            CompanyId = company.Id,
            SupplierId = supplier.Id
        };
        db.Contracts.Add(contract);
        await db.SaveChangesAsync();
        return (supplier, contract);
    }

    private static Task<PartyStatementResult> SupplierStatementRubAsync(ApplicationDbContext db, int supplierId)
        => BuildService(db).GetStatementAsync(
            new PartyRef(PartyStatementPartyType.Supplier, supplierId),
            new PartyStatementFilter { CurrencyCode = "RUB", IncludeOperationalColumns = false });

    private static PartyStatementReadService BuildService(ApplicationDbContext db)
        => new(db, new PartyStatementPolicyResolver(), Options.Create(new PartyStatementOptions()));

    private static ApplicationDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ApplicationDbContext(options);
    }
}
