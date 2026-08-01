using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Controllers;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.Reconciliation;
using PTGOilSystem.Web.Services.Accounting;
using PTGOilSystem.Web.Services.Exports;
using PTGOilSystem.Web.Services.Reconciliation;
using PTGOilSystem.Web.Services.Time;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// عدد هر دسته باید در صفحه، تست و خروجی یکی باشد، و نگاشت دفتر عملیاتی به دفتر
/// حسابداری باید واقعی باشد نه مقایسهٔ نامِ متفاوت.
/// </summary>
public class ReconciliationDiscrepancyConsistencyTests
{
    private static readonly DateTime FixedUtc = new(2026, 7, 30, 12, 0, 0, DateTimeKind.Utc);

    private static IAfghanistanBusinessClock Clock
        => new AfghanistanBusinessClock(new FixedUtcTimeProvider(FixedUtc));

    private sealed class FixedUtcTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
    }

    private static ApplicationDbContext NewDb()
        => new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase("recon-consistency-" + Guid.NewGuid())
            .Options);

    private static ReconciliationController WithHttpContext(ReconciliationController controller)
    {
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        return controller;
    }

    // ------------------------------------------------------------ یکسانی شمارش

    [Fact]
    public void Every_Enum_Category_Has_Text_Severity_And_Is_Listed_Once()
    {
        var all = ReconciliationDiscrepancyText.All;
        var enumValues = Enum.GetValues<ReconciliationDiscrepancyCategory>();

        Assert.Equal(enumValues.Length, all.Count);
        Assert.Equal(20, all.Count);
        Assert.Equal(all.Count, all.Distinct().Count());

        foreach (var category in all)
        {
            // عنوان نباید fallback به نامِ enum باشد.
            Assert.NotEqual(category.ToString(), ReconciliationDiscrepancyText.TitleFa(category));
            Assert.NotEqual(category.ToString(), ReconciliationDiscrepancyText.TitleEn(category));
            Assert.Contains(ReconciliationDiscrepancyText.Severity(category), new[] { "critical", "warning" });
        }
    }

    [Fact]
    public async Task Every_Category_Has_A_Real_Query_And_Never_Silently_Returns_Zero()
    {
        await using var db = NewDb();
        var service = new ReconciliationService(db, null, Clock);

        var counts = await service.BuildDiscrepancyCountsAsync();

        // اگر دسته‌ای query نداشته باشد، BuildDiscrepancyQuery پرتاب می‌کند و این تست می‌شکند.
        Assert.Equal(ReconciliationDiscrepancyText.All.Count, counts.Count);
        Assert.Equal(
            ReconciliationDiscrepancyText.All.ToArray(),
            counts.Select(c => c.Category).ToArray());
    }

    [Fact]
    public async Task Page_Count_Service_Count_And_Export_Count_Agree_For_Every_Category()
    {
        await using var db = NewDb();
        SeedSalesWithoutCogs(db, 3);
        await db.SaveChangesAsync();

        var service = new ReconciliationService(db, null, Clock);
        var controller = WithHttpContext(new ReconciliationController(db, null, Clock));

        var summaryCounts = (await service.BuildDiscrepancyCountsAsync())
            .ToDictionary(c => c.Category, c => c.Count);

        var view = Assert.IsType<ViewResult>(await controller.Discrepancies());
        var pageModel = Assert.IsType<ReconciliationDiscrepanciesViewModel>(view.Model);
        var pageCounts = pageModel.Counts.ToDictionary(c => c.Category, c => c.Count);

        var exportResult = Assert.IsType<TabularExportResult>(await controller.DiscrepanciesExport("excel"));
        var exportRows = exportResult.Document.Rows.ToList();

        Assert.Equal(ReconciliationDiscrepancyText.All.Count, exportRows.Count);

        foreach (var category in ReconciliationDiscrepancyText.All)
        {
            var single = await service.BuildDiscrepancyCountAsync(category);
            var paged = await service.BuildDiscrepancyPageAsync(category, 1, 200);

            Assert.Equal(single, summaryCounts[category]);
            Assert.Equal(single, pageCounts[category]);
            Assert.Equal(single, paged.TotalCount);
        }

        // ستون «تعداد» خروجی خلاصه همان اعداد صفحه است.
        var exportedCounts = exportRows
            .Select(r => Convert.ToInt32(r.Cells[1].Value))
            .ToArray();
        Assert.Equal(pageModel.Counts.Select(c => c.Count).ToArray(), exportedCounts);
    }

    [Fact]
    public async Task Streamed_Export_Rows_Match_The_Paged_Rows()
    {
        await using var db = NewDb();
        SeedSalesWithoutCogs(db, 7);
        await db.SaveChangesAsync();

        var service = new ReconciliationService(db, null, Clock);

        var paged = await service.BuildDiscrepancyPageAsync(
            ReconciliationDiscrepancyCategory.SaleWithoutCogs, 1, 200);

        var streamed = new List<ReconciliationDiscrepancyRow>();
        await foreach (var row in service.StreamDiscrepancyRowsAsync(
            ReconciliationDiscrepancyCategory.SaleWithoutCogs))
        {
            streamed.Add(row);
        }

        Assert.Equal(7, paged.TotalCount);
        Assert.Equal(
            paged.Rows.Select(r => r.Reference).ToArray(),
            streamed.Select(r => r.Reference).ToArray());
    }

    // --------------------------------------------- نگاشت دفتر کل ↔ سند حسابداری

    [Fact]
    public void Ledger_To_Journal_Map_Uses_The_Real_Adapter_Constants()
    {
        var map = ReconciliationService.LedgerToJournalSourceMap.ToDictionary(x => x.LedgerSourceType, x => x.JournalSourceEntityType);

        Assert.Equal(nameof(ExpenseTransaction), map["Expense"]);
        Assert.Equal(nameof(SalesTransaction), map["Sale"]);
        Assert.Equal(nameof(LoadingRegister), map["Loading"]);
        Assert.Equal(nameof(InventoryTransportReceipt), map["ShortageCharge"]);
        Assert.Equal(nameof(SarrafSettlement), map["SarrafSettlement"]);
        Assert.Equal(nameof(ThreeWaySettlement), map["ThreeWaySettlement"]);
        Assert.Equal("ContractBalanceTransfer", map["ContractBalanceTransfer"]);
        Assert.Equal("SupplierPaymentAllocation", map["SupplierPaymentAllocation"]);
        Assert.Equal(nameof(LedgerEntry), map[PaymentsController.ViaSarrafSupplierLedgerSourceType]);
        Assert.Equal(nameof(LedgerEntry), map[PaymentsController.ViaSarrafPayableLedgerSourceType]);

        // هر نگاشت باید دقیقاً همان ثابتی باشد که آداپتر می‌نویسد، نه رشتهٔ دستی.
        Assert.Equal(ExpenseAccountingAdapter.SourceEntityType, map["Expense"]);
        Assert.Equal(SalesAccountingAdapter.SourceEntityType, map["Sale"]);
        Assert.Equal(PurchaseAccountingAdapter.PurchaseSourceEntityType, map["Loading"]);
        Assert.Equal(ShortageChargeAccountingAdapter.SourceEntityType, map["ShortageCharge"]);
    }

    [Fact]
    public async Task Ledger_With_A_Matching_Journal_Is_Not_Reported_Even_Though_The_Names_Differ()
    {
        await using var db = NewDb();

        // نام‌ها عمداً متفاوت‌اند: دفتر عملیاتی "Expense"، دفتر حسابداری "ExpenseTransaction".
        db.LedgerEntries.Add(NewLedger(1, "Expense", sourceId: 55));
        db.JournalEntries.Add(NewJournal(1, nameof(ExpenseTransaction), 55));
        await db.SaveChangesAsync();

        var service = new ReconciliationService(db, null, Clock);
        var count = await service.BuildDiscrepancyCountAsync(
            ReconciliationDiscrepancyCategory.LedgerWithoutJournal);

        Assert.Equal(0, count);
    }

    [Fact]
    public async Task Ledger_Without_A_Journal_Is_Still_Reported()
    {
        await using var db = NewDb();
        db.LedgerEntries.Add(NewLedger(1, "Sale", sourceId: 9));
        await db.SaveChangesAsync();

        var service = new ReconciliationService(db, null, Clock);
        var page = await service.BuildDiscrepancyPageAsync(
            ReconciliationDiscrepancyCategory.LedgerWithoutJournal);

        Assert.Equal(1, page.TotalCount);
    }

    [Fact]
    public async Task Journal_Pointing_At_A_Different_Source_Id_Is_Not_A_Match()
    {
        await using var db = NewDb();
        db.LedgerEntries.Add(NewLedger(1, "Sale", sourceId: 9));
        db.JournalEntries.Add(NewJournal(1, nameof(SalesTransaction), 10));
        await db.SaveChangesAsync();

        var service = new ReconciliationService(db, null, Clock);
        Assert.Equal(1, await new ReconciliationService(db, null, Clock)
            .BuildDiscrepancyCountAsync(ReconciliationDiscrepancyCategory.LedgerWithoutJournal));
        Assert.NotNull(service);
    }

    [Fact]
    public async Task Derived_Ledger_Rows_Are_Never_Reported_As_Missing_A_Journal()
    {
        await using var db = NewDb();

        // این سطرها سند حسابداری مستقل ندارند؛ شمردن‌شان false positive بود.
        db.LedgerEntries.AddRange(
            NewLedger(1, "OpeningBalance", 1),
            NewLedger(2, "SarrafFxGain", 2),
            NewLedger(3, "SarrafSettlementCancel", 3),
            NewLedger(4, "SarrafSettlementEditReversal", 4),
            NewLedger(5, "SarrafSettlementExchangeDifference", 5),
            NewLedger(6, "SupplierPaymentAllocationReversal", 6),
            NewLedger(7, "SupplierPaymentAllocationExchangeDifference", 7),
            NewLedger(8, "SupplierPaymentAllocationExchangeDifferenceReversal", 8),
            NewLedger(9, "ThreeWaySettlementCancellation", 9));
        await db.SaveChangesAsync();

        var count = await new ReconciliationService(db, null, Clock)
            .BuildDiscrepancyCountAsync(ReconciliationDiscrepancyCategory.LedgerWithoutJournal);

        Assert.Equal(0, count);
    }

    [Fact]
    public async Task Via_Sarraf_Journal_Is_Matched_Against_The_Ledger_Row_Itself()
    {
        await using var db = NewDb();

        // آداپتر «از طریق صراف» سند را به شناسهٔ خودِ سطر دفتر کل وصل می‌کند، نه به SourceId.
        db.LedgerEntries.Add(NewLedger(
            42, PaymentsController.ViaSarrafSupplierLedgerSourceType, sourceId: 777));
        db.JournalEntries.Add(NewJournal(1, nameof(LedgerEntry), 42));
        await db.SaveChangesAsync();

        var count = await new ReconciliationService(db, null, Clock)
            .BuildDiscrepancyCountAsync(ReconciliationDiscrepancyCategory.LedgerWithoutJournal);

        Assert.Equal(0, count);
    }

    // ------------------------------------------------------- زنجیرهٔ ناقص حمل

    [Fact]
    public async Task Transport_Leg_Without_A_Source_Purchase_Contract_Is_Reported()
    {
        await using var db = NewDb();
        db.InventoryTransportLegs.AddRange(
            NewLeg(1, sourceContractId: 0, shipmentId: 5),   // بدون قرارداد خرید
            NewLeg(2, sourceContractId: 3, shipmentId: null), // بدون محموله
            NewLeg(3, sourceContractId: 3, shipmentId: 5));   // سالم
        await db.SaveChangesAsync();

        var page = await new ReconciliationService(db, null, Clock)
            .BuildDiscrepancyPageAsync(
                ReconciliationDiscrepancyCategory.IncompleteContractOrShipmentLineage);

        Assert.Equal(2, page.TotalCount);
        Assert.Contains(page.Rows, r => r.Detail.Contains("قرارداد خرید"));
        Assert.Contains(page.Rows, r => r.Detail.Contains("محموله"));
    }

    // ------------------------------------------------------------------ داده

    private static void SeedSalesWithoutCogs(ApplicationDbContext db, int count)
    {
        db.Customers.Add(new Customer { Id = 1, Name = "مشتری" });
        db.Products.Add(new Product { Id = 1, Name = "دیزل" });
        for (var index = 1; index <= count; index++)
        {
            db.SalesTransactions.Add(new SalesTransaction
            {
                Id = index,
                CustomerId = 1,
                ProductId = 1,
                InvoiceNumber = "INV-" + index,
                SaleDate = new DateTime(2026, 5, index, 0, 0, 0, DateTimeKind.Utc),
                QuantityMt = 10m,
                UnitPriceUsd = 100m,
                TotalUsd = 1_000m
            });
        }
    }

    private static LedgerEntry NewLedger(int id, string sourceType, int sourceId) => new()
    {
        Id = id,
        EntryDate = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
        Side = LedgerSide.Debit,
        AmountUsd = 100m,
        Currency = "USD",
        SourceType = sourceType,
        SourceId = sourceId,
        Reference = sourceType + "-" + sourceId
    };

    private static JournalEntry NewJournal(int id, string sourceEntityType, int sourceEntityId) => new()
    {
        Id = id,
        CompanyId = 1,
        JournalNumber = "J-" + id,
        AccountingDate = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
        DocumentDate = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
        SourceModule = "Test",
        SourceEntityType = sourceEntityType,
        SourceEntityId = sourceEntityId
    };

    private static InventoryTransportLeg NewLeg(int id, int sourceContractId, int? shipmentId) => new()
    {
        Id = id,
        SourcePurchaseContractId = sourceContractId,
        ShipmentId = shipmentId,
        ProductId = 1,
        SourceTerminalId = 1,
        LoadedDate = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
        QuantityMt = 10m,
        WagonNumber = "W-" + id
    };
}
