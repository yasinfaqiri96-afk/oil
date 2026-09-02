using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services.Reconciliation;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// PTG-P1-02 و ۱۲-F — تشخیصِ فقط‌خواندنیِ سلامتِ دفتر کل.
///
/// یک FK واقعی روی <c>(SourceType, SourceId)</c> ممکن نیست (رابطه چندریختی است)، پس
/// قرار شد «یتیم بی‌صدا نماند». این تست‌ها ثابت می‌کنند هر هشت اسکنر واقعاً همان چیزی را
/// می‌بیند که ادعا می‌کند — و روی دادهٔ سالم چیزی گزارش نمی‌کند.
/// </summary>
public sealed class LedgerIntegrityReconciliationTests
{
    private static ApplicationDbContext NewDb()
        => new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static LedgerEntry Ledger(string sourceType, int sourceId, decimal amountUsd = 100m) => new()
    {
        EntryDate = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
        SourceType = sourceType,
        SourceId = sourceId,
        AmountUsd = amountUsd,
        Side = LedgerSide.Debit,
        Description = sourceType
    };

    private static LedgerIntegrityFinding Find(LedgerIntegrityReport report, string code)
        => Assert.Single(report.Findings, finding => finding.Code == code);

    // ------------------------------------------------------------------
    // دادهٔ سالم: هیچ یافته‌ای
    // ------------------------------------------------------------------

    [Fact]
    public async Task HealthyData_ProducesNoFindings()
    {
        await using var db = NewDb();
        db.SalesTransactions.Add(new SalesTransaction { Id = 1, SaleDate = new DateTime(2026, 5, 1), TotalUsd = 100m });
        db.ExpenseTransactions.Add(new ExpenseTransaction { Id = 2, ExpenseDate = new DateTime(2026, 5, 1), AmountUsd = 50m, Description = "x" });
        db.LedgerEntries.AddRange(Ledger("Sale", 1), Ledger("Expense", 2, 50m));
        await db.SaveChangesAsync();

        var report = await new LedgerIntegrityReconciliationService(db).RunAsync();

        Assert.True(report.IsClean, string.Join(" | ", report.Findings.Where(f => !f.IsClean).Select(f => $"{f.Code}={f.Count}")));
        Assert.Equal(0, report.TotalIssues);
    }

    // ------------------------------------------------------------------
    // ۱ — لجر یتیم (سناریوی Probe06: DELETE خام روی سند)
    // ------------------------------------------------------------------

    [Fact]
    public async Task ALedgerRowWhoseDocumentWasDeleted_IsReportedAsOrphan()
    {
        await using var db = NewDb();
        var expense = new ExpenseTransaction { Id = 2, ExpenseDate = new DateTime(2026, 5, 1), AmountUsd = 50m, Description = "x" };
        db.ExpenseTransactions.Add(expense);
        db.LedgerEntries.Add(Ledger("Expense", 2, 50m));
        await db.SaveChangesAsync();

        db.ExpenseTransactions.Remove(expense);
        await db.SaveChangesAsync();

        var report = await new LedgerIntegrityReconciliationService(db).RunAsync();
        var finding = Find(report, "LEDGER-ORPHAN");

        Assert.Equal(1, finding.Count);
        Assert.Contains(finding.Samples, sample => sample.Contains("Expense#2"));
    }

    [Fact]
    public async Task AnOrphanPaymentLedgerRow_IsAlsoFound()
    {
        await using var db = NewDb();
        db.LedgerEntries.Add(Ledger(nameof(PaymentKind.SupplierPayment), 404));
        await db.SaveChangesAsync();

        var finding = Find(await new LedgerIntegrityReconciliationService(db).RunAsync(), "LEDGER-ORPHAN");

        Assert.Equal(1, finding.Count);
    }

    // ------------------------------------------------------------------
    // ۲ — سند بدون لجر
    // ------------------------------------------------------------------

    [Fact]
    public async Task ASaleWithoutALedgerRow_IsReported()
    {
        await using var db = NewDb();
        db.SalesTransactions.Add(new SalesTransaction { Id = 1, SaleDate = new DateTime(2026, 5, 1), TotalUsd = 900m });
        await db.SaveChangesAsync();

        var finding = Find(await new LedgerIntegrityReconciliationService(db).RunAsync(), "LEDGER-MISSING");

        Assert.Equal(1, finding.Count);
        Assert.Contains(finding.Samples, sample => sample.Contains("Sale#1"));
    }

    // ------------------------------------------------------------------
    // ۳ — لجر تکراری برای یک سند
    // ------------------------------------------------------------------

    [Fact]
    public async Task TwoLedgerRowsForOneSale_AreReportedAsDuplicate()
    {
        await using var db = NewDb();
        db.SalesTransactions.Add(new SalesTransaction { Id = 1, SaleDate = new DateTime(2026, 5, 1), TotalUsd = 100m });
        db.LedgerEntries.AddRange(Ledger("Sale", 1), Ledger("Sale", 1));
        await db.SaveChangesAsync();

        var finding = Find(await new LedgerIntegrityReconciliationService(db).RunAsync(), "LEDGER-DUPLICATE");

        Assert.Equal(1, finding.Count);
        Assert.Contains(finding.Samples, sample => sample.Contains("Sale#1"));
    }

    // ------------------------------------------------------------------
    // ۴ — موجودی پایانی منفی
    // ------------------------------------------------------------------

    [Fact]
    public async Task AScopeThatClosesNegative_IsReported()
    {
        await using var db = NewDb();
        db.InventoryMovements.AddRange(
            new InventoryMovement { ProductId = 1, TerminalId = 1, Direction = MovementDirection.In, QuantityMt = 100m, MovementDate = new DateTime(2026, 1, 5) },
            new InventoryMovement { ProductId = 1, TerminalId = 1, Direction = MovementDirection.Out, QuantityMt = 170m, MovementDate = new DateTime(2026, 6, 1) });
        await db.SaveChangesAsync();

        var finding = Find(await new LedgerIntegrityReconciliationService(db).RunAsync(), "INVENTORY-NEGATIVE");

        Assert.Equal(1, finding.Count);
        Assert.Contains(finding.Samples, sample => sample.Contains("-70"));
    }

    [Fact]
    public async Task ATransientDipThatHealsBeforeTheEnd_IsNotReported()
    {
        await using var db = NewDb();
        db.InventoryMovements.AddRange(
            new InventoryMovement { ProductId = 1, TerminalId = 1, Direction = MovementDirection.Out, QuantityMt = 40m, MovementDate = new DateTime(2026, 1, 5) },
            new InventoryMovement { ProductId = 1, TerminalId = 1, Direction = MovementDirection.In, QuantityMt = 100m, MovementDate = new DateTime(2026, 2, 1) });
        await db.SaveChangesAsync();

        Assert.Equal(0, Find(await new LedgerIntegrityReconciliationService(db).RunAsync(), "INVENTORY-NEGATIVE").Count);
    }

    // ------------------------------------------------------------------
    // ۵ و ۶ و ۷ — سهم شرکا
    // ------------------------------------------------------------------

    [Fact]
    public async Task ASharePeriodThatDoesNotSumTo100_IsReported()
    {
        await using var db = NewDb();
        db.Contracts.Add(new Contract { Id = 1, ContractNumber = "PUR-01", OwnershipType = ContractOwnershipType.Partnership });
        db.ContractPartners.AddRange(
            new ContractPartner { ContractId = 1, PartnerId = 1, SharePercent = 80m, EffectiveFrom = new DateTime(2026, 1, 1) },
            new ContractPartner { ContractId = 1, PartnerId = 2, SharePercent = 80m, EffectiveFrom = new DateTime(2026, 1, 1) });
        await db.SaveChangesAsync();

        var finding = Find(await new LedgerIntegrityReconciliationService(db).RunAsync(), "PARTNER-SHARE-SUM");

        Assert.Equal(1, finding.Count);
        Assert.Contains(finding.Samples, sample => sample.Contains("160"));
    }

    [Fact]
    public async Task TwoValidPeriodsOfTheSameContract_AreNotReported()
    {
        await using var db = NewDb();
        db.Contracts.Add(new Contract { Id = 1, ContractNumber = "PUR-01", OwnershipType = ContractOwnershipType.Partnership });
        db.ContractPartners.AddRange(
            new ContractPartner { ContractId = 1, PartnerId = 1, SharePercent = 50m, EffectiveFrom = new DateTime(2026, 1, 1), EffectiveTo = new DateTime(2026, 5, 31) },
            new ContractPartner { ContractId = 1, PartnerId = 2, SharePercent = 50m, EffectiveFrom = new DateTime(2026, 1, 1), EffectiveTo = new DateTime(2026, 5, 31) },
            new ContractPartner { ContractId = 1, PartnerId = 1, SharePercent = 80m, EffectiveFrom = new DateTime(2026, 6, 1) },
            new ContractPartner { ContractId = 1, PartnerId = 2, SharePercent = 20m, EffectiveFrom = new DateTime(2026, 6, 1) });
        await db.SaveChangesAsync();

        var report = await new LedgerIntegrityReconciliationService(db).RunAsync();

        Assert.Equal(0, Find(report, "PARTNER-SHARE-SUM").Count);
        Assert.Equal(0, Find(report, "PARTNER-PERIOD-OVERLAP").Count);
    }

    [Fact]
    public async Task OverlappingSharePeriodsForOnePartner_AreReported()
    {
        await using var db = NewDb();
        db.Contracts.Add(new Contract { Id = 1, ContractNumber = "PUR-01", OwnershipType = ContractOwnershipType.Partnership });
        db.ContractPartners.AddRange(
            new ContractPartner { ContractId = 1, PartnerId = 1, SharePercent = 100m, EffectiveFrom = new DateTime(2026, 1, 1), EffectiveTo = new DateTime(2026, 7, 31) },
            new ContractPartner { ContractId = 1, PartnerId = 1, SharePercent = 100m, EffectiveFrom = new DateTime(2026, 6, 1) });
        await db.SaveChangesAsync();

        var finding = Find(await new LedgerIntegrityReconciliationService(db).RunAsync(), "PARTNER-PERIOD-OVERLAP");

        Assert.Equal(1, finding.Count);
    }

    [Fact]
    public async Task APartnershipContractWithNoShareRow_IsReported()
    {
        await using var db = NewDb();
        db.Contracts.Add(new Contract { Id = 1, ContractNumber = "PUR-01", OwnershipType = ContractOwnershipType.Partnership });
        await db.SaveChangesAsync();

        var finding = Find(await new LedgerIntegrityReconciliationService(db).RunAsync(), "PARTNERSHIP-WITHOUT-SHARES");

        Assert.Equal(1, finding.Count);
        Assert.Contains(finding.Samples, sample => sample.Contains("PUR-01"));
    }

    // ------------------------------------------------------------------
    // ۸ — کلید ایمپورتِ غیر canonical و برخوردها (PTG-P1-04)
    // ------------------------------------------------------------------

    [Fact]
    public async Task ImportKeysWrittenWithPersianDigits_AreReportedWithTheirCollision()
    {
        await using var db = NewDb();
        db.LoadingRegisters.AddRange(
            new LoadingRegister { Id = 1, LoadingDate = new DateTime(2026, 1, 1), ImportUniqueKey = "7|RWB-12345|WGN-98765" },
            new LoadingRegister { Id = 2, LoadingDate = new DateTime(2026, 1, 1), ImportUniqueKey = "7|RWB-۱۲۳۴۵|WGN-۹۸۷۶۵" });
        await db.SaveChangesAsync();

        var finding = Find(await new LedgerIntegrityReconciliationService(db).RunAsync(), "IMPORT-KEY-NON-CANONICAL");

        // یک سطرِ غیرcanonical + یک برخورد.
        Assert.Equal(2, finding.Count);
        Assert.Contains(finding.Samples, sample => sample.Contains("برخورد"));
    }

    [Fact]
    public async Task AlreadyCanonicalImportKeys_AreNotReported()
    {
        await using var db = NewDb();
        db.LoadingRegisters.AddRange(
            new LoadingRegister { Id = 1, LoadingDate = new DateTime(2026, 1, 1), ImportUniqueKey = "7|RWB-12345|WGN-98765" },
            new LoadingRegister { Id = 2, LoadingDate = new DateTime(2026, 1, 1), ImportUniqueKey = "7|RWB-12346|WGN-98766" },
            new LoadingRegister { Id = 3, LoadingDate = new DateTime(2026, 1, 1), ImportUniqueKey = null });
        await db.SaveChangesAsync();

        Assert.Equal(0, Find(await new LedgerIntegrityReconciliationService(db).RunAsync(), "IMPORT-KEY-NON-CANONICAL").Count);
    }
}
