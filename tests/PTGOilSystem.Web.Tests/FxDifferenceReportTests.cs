using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Controllers;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.Reports;
using PTGOilSystem.Web.Services.Exports;
using PTGOilSystem.Web.Services.PartyStatements;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// گزارش «سود و ضرر تفاوت نرخ ارز» — فقط‌خواندنی. تست‌ها اعداد پذیرشِ رسیدهای ۶۶۵ و ۶۸۱،
/// جداییِ کمیشن از تفاوت نرخ، و نبودِ دوباره‌شماری را می‌سنجند.
/// </summary>
public class FxDifferenceReportTests
{
    private const int FxExpenseTypeId = 90;
    private const int CommissionExpenseTypeId = 91;

    // ===================== اعداد پذیرش =====================

    [Fact]
    public async Task Receipt_665_Shows_Exact_Loss()
    {
        await using var db = NewDb();
        Seed(db);
        db.SarrafSettlements.Add(Receipt665());
        await db.SaveChangesAsync();

        var row = Assert.Single((await BuildAsync(db)).Rows);

        Assert.Equal("665", row.ReferenceNumber);
        Assert.Equal(200_000_000m, row.Amount);
        Assert.Equal(76m, row.CompanyRate);
        Assert.Equal(79.3146m, row.SupplierRate);
        Assert.Equal(2_631_578.9474m, row.CompanyCostUsd);
        Assert.Equal(2_521_603.8409m, row.SupplierAcceptedUsd);
        Assert.Equal(-109_975.1065m, row.FxResultUsd);
        Assert.Equal(FxDifferenceResult.Loss, row.Result);
        Assert.Equal(109_975.1065m, row.LossUsd);
        Assert.Equal(0m, row.GainUsd);
    }

    [Fact]
    public async Task Receipt_681_Shows_Exact_Loss()
    {
        await using var db = NewDb();
        Seed(db);
        db.SarrafSettlements.Add(Receipt681());
        await db.SaveChangesAsync();

        var row = Assert.Single((await BuildAsync(db)).Rows);

        Assert.Equal(3_947_368.4211m, row.CompanyCostUsd);
        Assert.Equal(3_861_386.5209m, row.SupplierAcceptedUsd);
        Assert.Equal(FxDifferenceResult.Loss, row.Result);
        Assert.Equal(85_981.9002m, row.LossUsd);
        Assert.Equal(0m, row.GainUsd);
    }

    [Fact]
    public async Task Gain_Lands_In_Gain_Column_Only()
    {
        await using var db = NewDb();
        Seed(db);
        db.SarrafSettlements.Add(GainSettlement());
        await db.SaveChangesAsync();

        var model = await BuildAsync(db);
        var row = Assert.Single(model.Rows);

        Assert.Equal(FxDifferenceResult.Gain, row.Result);
        Assert.Equal(82_579.4507m, row.GainUsd);
        Assert.Equal(0m, row.LossUsd);
        Assert.Equal(82_579.4507m, model.Totals.TotalGainUsd);
        Assert.Equal(0m, model.Totals.TotalLossUsd);
        Assert.Equal(82_579.4507m, model.Totals.NetFxResultUsd);
    }

    [Fact]
    public async Task Equal_Rates_Are_Reported_As_No_Difference()
    {
        await using var db = NewDb();
        Seed(db);
        db.SarrafSettlements.Add(Settlement(
            id: 10, reference: "700", companyRate: 76m, supplierRate: 76m,
            amount: 100_000_000m, companyCostUsd: 1_315_789.4737m, supplierAcceptedUsd: 1_315_789.4737m,
            storedDifferenceUsd: 0m, differenceType: SarrafSettlementDifferenceType.None));
        await db.SaveChangesAsync();

        var model = await BuildAsync(db);
        var row = Assert.Single(model.Rows);

        Assert.Equal(FxDifferenceResult.None, row.Result);
        Assert.Equal(0m, row.GainUsd);
        Assert.Equal(0m, row.LossUsd);
        Assert.Equal(1, model.Totals.NoDifferenceCount);
    }

    [Fact]
    public async Task Ui_And_Export_Report_The_Same_Numbers_For_665_And_681()
    {
        await using var db = NewDb();
        Seed(db);
        db.SarrafSettlements.AddRange(Receipt665(), Receipt681());
        await db.SaveChangesAsync();

        var ui = await BuildAsync(db);
        var export = await BuildAsync(db, paginate: false);

        Assert.Equal(2, ui.Rows.Count);
        Assert.Equal(2, export.Rows.Count);
        Assert.Equal(195_957.0067m, ui.Totals.TotalLossUsd);
        Assert.Equal(ui.Totals.TotalLossUsd, export.Totals.TotalLossUsd);
        Assert.Equal(ui.Totals.TotalGainUsd, export.Totals.TotalGainUsd);
        Assert.Equal(ui.Totals.NetFxResultUsd, export.Totals.NetFxResultUsd);
        Assert.Equal(ui.Totals.TotalCompanyCostUsd, export.Totals.TotalCompanyCostUsd);
        Assert.Equal(ui.Totals.TotalSupplierAcceptedUsd, export.Totals.TotalSupplierAcceptedUsd);

        foreach (var reference in new[] { "665", "681" })
        {
            var uiRow = ui.Rows.Single(r => r.ReferenceNumber == reference);
            var exportRow = export.Rows.Single(r => r.ReferenceNumber == reference);
            Assert.Equal(uiRow.FxResultUsd, exportRow.FxResultUsd);
            Assert.Equal(uiRow.GainUsd, exportRow.GainUsd);
            Assert.Equal(uiRow.LossUsd, exportRow.LossUsd);
        }
    }

    // ===================== منبع داده و دوباره‌شماری =====================

    [Fact]
    public async Task Settlement_With_Other_Difference_Reason_Is_Excluded()
    {
        await using var db = NewDb();
        Seed(db);
        var commissionReason = Receipt665();
        commissionReason.Id = 20;
        commissionReason.ReferenceNumber = "800";
        commissionReason.DifferenceReason = DifferenceReason.Commission;
        db.SarrafSettlements.AddRange(Receipt665(), commissionReason);
        await db.SaveChangesAsync();

        var model = await BuildAsync(db);

        Assert.Equal("665", Assert.Single(model.Rows).ReferenceNumber);
    }

    [Fact]
    public async Task Legacy_Single_Rate_Settlement_Is_Excluded()
    {
        await using var db = NewDb();
        Seed(db);
        var legacy = Receipt665();
        legacy.Id = 21;
        legacy.ReferenceNumber = "900";
        legacy.SupplierRate = null;
        db.SarrafSettlements.AddRange(Receipt665(), legacy);
        await db.SaveChangesAsync();

        var model = await BuildAsync(db);

        Assert.Equal("665", Assert.Single(model.Rows).ReferenceNumber);
    }

    [Fact]
    public async Task Fx_Loss_Is_Not_Counted_Twice_From_Expense_Transaction()
    {
        await using var db = NewDb();
        Seed(db);
        db.SarrafSettlements.Add(Receipt665());
        // همان ضرر، این بار به‌عنوان مصرف ثبت‌شده. گزارش نباید آن را دوباره جمع کند.
        db.ExpenseTransactions.Add(new ExpenseTransaction
        {
            Id = 500,
            ExpenseTypeId = FxExpenseTypeId,
            ExpenseDate = new DateTime(2026, 5, 1),
            Amount = 109_975.1065m,
            Currency = "USD",
            AmountUsd = 109_975.1065m,
            Description = "ضرر تفاوت نرخ ارز حواله صراف [SS-1] — نرخ شرکت 76 در برابر نرخ طرف مقابل 79.3146"
        });
        db.LedgerEntries.Add(new LedgerEntry
        {
            Id = 600,
            EntryDate = new DateTime(2026, 5, 1),
            Side = LedgerSide.Debit,
            AmountUsd = 109_975.1065m,
            SourceType = "Expense",
            SourceId = 500,
            Description = "ضرر تفاوت نرخ ارز حواله صراف [SS-1]"
        });
        await db.SaveChangesAsync();

        var model = await BuildAsync(db);
        var row = Assert.Single(model.Rows);

        Assert.Equal(109_975.1065m, model.Totals.TotalLossUsd);
        Assert.Equal(109_975.1065m, row.LossUsd);
        Assert.Equal(500, row.Profit.ExpenseTransactionId);
        Assert.Equal("Expense", row.Profit.LedgerSourceType);
    }

    [Fact]
    public async Task Needs_Review_Row_Is_Excluded_From_Gain_And_Loss_Totals()
    {
        await using var db = NewDb();
        Seed(db);
        var contradictory = Receipt665();
        // علامت ذخیره‌شده «سود» است در حالی که اختلاف دو نرخ ضرر می‌دهد.
        contradictory.DifferenceType = SarrafSettlementDifferenceType.Gain;
        db.SarrafSettlements.Add(contradictory);
        await db.SaveChangesAsync();

        var model = await BuildAsync(db);
        var row = Assert.Single(model.Rows);

        Assert.Equal(FxDifferenceResult.NeedsReview, row.Result);
        Assert.NotEmpty(row.ReviewNotes);
        Assert.Equal(0m, row.GainUsd);
        Assert.Equal(0m, row.LossUsd);
        Assert.Equal(0m, model.Totals.TotalGainUsd);
        Assert.Equal(0m, model.Totals.TotalLossUsd);
        Assert.Equal(1, model.Totals.NeedsReviewCount);
    }

    [Fact]
    public async Task Fx_Difference_Has_No_Separate_Cash_Effect_And_Stays_Out_Of_Supplier_Account()
    {
        await using var db = NewDb();
        Seed(db);
        db.SarrafSettlements.Add(Receipt665());
        db.ExpenseTransactions.Add(new ExpenseTransaction
        {
            Id = 501,
            ExpenseTypeId = FxExpenseTypeId,
            ExpenseDate = new DateTime(2026, 5, 1),
            Amount = 109_975.1065m,
            Currency = "USD",
            AmountUsd = 109_975.1065m,
            Description = "ضرر تفاوت نرخ ارز حواله صراف [SS-1]"
        });
        db.LedgerEntries.Add(new LedgerEntry
        {
            Id = 601,
            EntryDate = new DateTime(2026, 5, 1),
            Side = LedgerSide.Debit,
            AmountUsd = 109_975.1065m,
            SourceType = "Expense",
            SourceId = 501,
            SupplierId = null,
            ContractId = null,
            Description = "ضرر تفاوت نرخ ارز حواله صراف [SS-1]"
        });
        await db.SaveChangesAsync();

        var row = Assert.Single((await BuildAsync(db)).Rows);

        Assert.True(row.Profit.NoCashEffect);
        Assert.True(row.Supplier.FxIsolatedFromSupplier);
        Assert.Equal(FxDifferenceResult.Loss, row.Result);
    }

    [Fact]
    public async Task Supplier_Statement_Amount_Is_Supplier_Accepted_And_Sarraf_Side_Is_Company_Cost()
    {
        await using var db = NewDb();
        Seed(db);
        var settlement = Receipt665();
        settlement.LedgerEntryId = 700;
        db.LedgerEntries.Add(new LedgerEntry
        {
            Id = 700,
            EntryDate = new DateTime(2026, 5, 1),
            Side = LedgerSide.Debit,
            AmountUsd = 2_521_603.8409m,
            SourceType = "SarrafSettlement",
            SourceId = 1,
            SupplierId = 1,
            ContractId = 1,
            Description = "تسویه صراف"
        });
        db.SarrafSettlements.Add(settlement);
        await db.SaveChangesAsync();

        var row = Assert.Single((await BuildAsync(db)).Rows);

        // صورت‌حساب تأمین‌کننده فقط مبلغ قبول‌شده را دارد، نه هزینه واقعی شرکت.
        Assert.Equal(2_521_603.8409m, row.Supplier.StatementAmountUsd);
        Assert.Equal(2_521_603.8409m, row.Supplier.ContractPaidUsd);
        // سمت صراف فقط هزینه واقعی شرکت است؛ کمیشن جدا نگه داشته می‌شود.
        Assert.Equal(2_631_578.9474m, row.Sarraf.CompanyCostUsd);
        Assert.Null(row.Sarraf.CommissionUsd);
    }

    // ===================== کمیشن =====================

    [Fact]
    public async Task Exact_Commission_Match_Shows_Amount_And_Enters_Total()
    {
        await using var db = NewDb();
        Seed(db);
        db.SarrafSettlements.Add(Receipt665());
        AddCommission(db, ledgerId: 800, expenseId: 801, amountUsd: 5_000m, reference: "665");
        await db.SaveChangesAsync();

        var model = await BuildAsync(db);
        var row = Assert.Single(model.Rows);

        Assert.Equal(FxCommissionMatchStatus.Exact, row.CommissionStatus);
        Assert.Equal(5_000m, row.CommissionUsd);
        Assert.Equal(801, row.Sarraf.CommissionExpenseTransactionId);
        Assert.Equal(5_000m, model.Totals.TotalCommissionUsd);
        // کمیشن هرگز با ضرر تفاوت نرخ جمع نمی‌شود.
        Assert.Equal(109_975.1065m, model.Totals.TotalLossUsd);
    }

    [Fact]
    public async Task Ambiguous_Commission_Is_Not_Summed_And_Stays_Out_Of_Total()
    {
        await using var db = NewDb();
        Seed(db);
        db.SarrafSettlements.Add(Receipt665());
        AddCommission(db, ledgerId: 810, expenseId: 811, amountUsd: 5_000m, reference: "665");
        AddCommission(db, ledgerId: 812, expenseId: 813, amountUsd: 3_000m, reference: "665");
        await db.SaveChangesAsync();

        var model = await BuildAsync(db);
        var row = Assert.Single(model.Rows);

        Assert.Equal(FxCommissionMatchStatus.NeedsReview, row.CommissionStatus);
        Assert.Null(row.CommissionUsd);
        Assert.Equal(2, row.Sarraf.CommissionMatchCount);
        Assert.Equal(0m, model.Totals.TotalCommissionUsd);
        Assert.Equal(1, model.Totals.CommissionNeedsReviewCount);
        // تفاوت نرخ از ابهام کمیشن اثر نمی‌گیرد.
        Assert.Equal(FxDifferenceResult.Loss, row.Result);
    }

    [Fact]
    public async Task Commission_Filter_Splits_Rows_With_And_Without_Commission()
    {
        await using var db = NewDb();
        Seed(db);
        db.SarrafSettlements.AddRange(Receipt665(), Receipt681());
        AddCommission(db, ledgerId: 820, expenseId: 821, amountUsd: 5_000m, reference: "665");
        await db.SaveChangesAsync();

        var withCommission = await BuildAsync(db, filter: new FxDifferenceReportFilterViewModel
        {
            Commission = FxCommissionFilter.WithCommission
        });
        var withoutCommission = await BuildAsync(db, filter: new FxDifferenceReportFilterViewModel
        {
            Commission = FxCommissionFilter.WithoutCommission
        });

        Assert.Equal("665", Assert.Single(withCommission.Rows).ReferenceNumber);
        Assert.Equal("681", Assert.Single(withoutCommission.Rows).ReferenceNumber);
        Assert.Equal(5_000m, withCommission.Totals.TotalCommissionUsd);
        Assert.Equal(0m, withoutCommission.Totals.TotalCommissionUsd);
    }

    // ===================== شرکت و فیلترها =====================

    [Fact]
    public async Task Row_Without_Contract_Has_No_Company()
    {
        await using var db = NewDb();
        Seed(db);
        var noContract = Receipt665();
        noContract.ContractId = null;
        db.SarrafSettlements.Add(noContract);
        await db.SaveChangesAsync();

        var row = Assert.Single((await BuildAsync(db)).Rows);

        Assert.Null(row.CompanyName);
        Assert.Null(row.ContractNumber);
    }

    [Fact]
    public async Task Company_Filter_Uses_Contract_Company()
    {
        await using var db = NewDb();
        Seed(db);
        var noContract = Receipt681();
        noContract.ContractId = null;
        db.SarrafSettlements.AddRange(Receipt665(), noContract);
        await db.SaveChangesAsync();

        var filtered = await BuildAsync(db, filter: new FxDifferenceReportFilterViewModel { CompanyId = 1 });

        var row = Assert.Single(filtered.Rows);
        Assert.Equal("665", row.ReferenceNumber);
        Assert.Equal("PTG", row.CompanyName);
    }

    [Fact]
    public async Task Date_Sarraf_Supplier_And_Contract_Filters_Work()
    {
        await using var db = NewDb();
        Seed(db);
        db.Sarrafs.Add(new Sarraf { Id = 2, Name = "Sarraf B" });
        db.Suppliers.Add(new Supplier { Id = 2, Name = "Supplier B" });
        var other = Receipt681();
        other.SettlementDate = new DateTime(2026, 6, 15);
        other.SarrafId = 2;
        other.SupplierId = 2;
        other.ContractId = null;
        db.SarrafSettlements.AddRange(Receipt665(), other);
        await db.SaveChangesAsync();

        Assert.Equal("681", Assert.Single((await BuildAsync(db, filter: new FxDifferenceReportFilterViewModel
        {
            FromDate = new DateTime(2026, 6, 1)
        })).Rows).ReferenceNumber);

        Assert.Equal("681", Assert.Single((await BuildAsync(db, filter: new FxDifferenceReportFilterViewModel
        {
            SarrafId = 2
        })).Rows).ReferenceNumber);

        Assert.Equal("681", Assert.Single((await BuildAsync(db, filter: new FxDifferenceReportFilterViewModel
        {
            SupplierId = 2
        })).Rows).ReferenceNumber);

        Assert.Equal("665", Assert.Single((await BuildAsync(db, filter: new FxDifferenceReportFilterViewModel
        {
            ContractId = 1
        })).Rows).ReferenceNumber);
    }

    [Fact]
    public async Task Result_Filter_Keeps_Only_The_Requested_Outcome()
    {
        await using var db = NewDb();
        Seed(db);
        db.SarrafSettlements.AddRange(Receipt665(), GainSettlement());
        await db.SaveChangesAsync();

        var gainOnly = await BuildAsync(db, filter: new FxDifferenceReportFilterViewModel
        {
            Result = FxDifferenceResultFilter.GainOnly
        });
        var lossOnly = await BuildAsync(db, filter: new FxDifferenceReportFilterViewModel
        {
            Result = FxDifferenceResultFilter.LossOnly
        });

        Assert.Equal(FxDifferenceResult.Gain, Assert.Single(gainOnly.Rows).Result);
        Assert.Equal(0m, gainOnly.Totals.TotalLossUsd);
        Assert.Equal(FxDifferenceResult.Loss, Assert.Single(lossOnly.Rows).Result);
        Assert.Equal(0m, lossOnly.Totals.TotalGainUsd);
    }

    [Fact]
    public async Task Status_Filter_Is_Not_Hard_Coded()
    {
        await using var db = NewDb();
        Seed(db);
        var cancelled = Receipt681();
        cancelled.Status = SarrafSettlementStatus.Cancelled;
        db.SarrafSettlements.AddRange(Receipt665(), cancelled);
        await db.SaveChangesAsync();

        Assert.Equal(2, (await BuildAsync(db)).Rows.Count);
        Assert.Equal("681", Assert.Single((await BuildAsync(db, filter: new FxDifferenceReportFilterViewModel
        {
            Status = SarrafSettlementStatus.Cancelled
        })).Rows).ReferenceNumber);
    }

    // ===================== صفحه‌بندی و خروجی =====================

    [Fact]
    public async Task Pagination_Does_Not_Change_Totals_And_Export_Returns_Every_Filtered_Row()
    {
        await using var db = NewDb();
        Seed(db);
        for (var index = 0; index < 60; index++)
        {
            var settlement = Receipt665();
            settlement.Id = 1000 + index;
            settlement.ReferenceNumber = $"R{index:000}";
            db.SarrafSettlements.Add(settlement);
        }

        await db.SaveChangesAsync();

        var firstPage = await BuildAsync(db);
        var exportModel = await BuildAsync(db, paginate: false);
        var expectedLoss = 60 * 109_975.1065m;

        Assert.Equal(50, firstPage.Rows.Count);
        Assert.Equal(60, firstPage.TotalRowCount);
        Assert.Equal(2, firstPage.PageCount);
        Assert.Equal(expectedLoss, firstPage.Totals.TotalLossUsd);
        Assert.Equal(60, firstPage.Totals.SettlementCount);

        // خروجی همه نتایج فیلترشده را دارد، نه فقط ۵۰ ردیف صفحه جاری.
        Assert.Equal(60, exportModel.Rows.Count);
        Assert.Equal(expectedLoss, exportModel.Totals.TotalLossUsd);
        Assert.Equal(firstPage.Totals.TotalLossUsd, exportModel.Totals.TotalLossUsd);
    }

    [Fact]
    public async Task Export_Action_Returns_Tabular_Result()
    {
        await using var db = NewDb();
        Seed(db);
        db.SarrafSettlements.Add(Receipt665());
        await db.SaveChangesAsync();

        var controller = new ReportsController(db);
        var result = await controller.FxDifferenceExport("excel");

        Assert.IsType<TabularExportResult>(result);
    }

    [Fact]
    public async Task Report_Action_Returns_View_Without_Writing_Anything()
    {
        await using var db = NewDb();
        Seed(db);
        db.SarrafSettlements.Add(Receipt665());
        await db.SaveChangesAsync();

        var expensesBefore = await db.ExpenseTransactions.CountAsync();
        var ledgersBefore = await db.LedgerEntries.CountAsync();

        var controller = new ReportsController(db);
        var view = Assert.IsType<ViewResult>(await controller.FxDifference());
        var model = Assert.IsType<FxDifferenceReportViewModel>(view.Model);

        Assert.Single(model.Rows);
        Assert.Equal(expensesBefore, await db.ExpenseTransactions.CountAsync());
        Assert.Equal(ledgersBefore, await db.LedgerEntries.CountAsync());
    }

    // ===================== کمک‌کننده‌ها =====================

    private static ApplicationDbContext NewDb()
        => new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static Task<FxDifferenceReportViewModel> BuildAsync(
        ApplicationDbContext db,
        FxDifferenceReportFilterViewModel? filter = null,
        bool paginate = true)
        => new ReportsController(db).BuildFxDifferenceReportAsync(
            filter ?? new FxDifferenceReportFilterViewModel(),
            page: 1,
            paginate,
            CancellationToken.None);

    private static void Seed(ApplicationDbContext db)
    {
        db.Companies.Add(new Company { Id = 1, Code = "PTG", Name = "PTG" });
        db.Products.Add(new Product { Id = 1, Code = "GAS", Name = "Gasoline" });
        db.Suppliers.Add(new Supplier { Id = 1, Name = "Supplier A" });
        db.Sarrafs.Add(new Sarraf { Id = 1, Name = "Sarraf A" });
        db.Users.Add(new User { Id = 5, Username = "ali", FullName = "Ali", PasswordHash = "x" });
        db.Contracts.Add(new Contract
        {
            Id = 1,
            ContractNumber = "PUR-1",
            ContractType = ContractType.Purchase,
            CompanyId = 1,
            ProductId = 1,
            SupplierId = 1,
            ContractDate = new DateTime(2026, 1, 1),
            QuantityMt = 100m,
            PricingMethod = PricingMethod.Fixed,
            UnitPriceUsd = 500m
        });
        db.ExpenseTypes.AddRange(
            new ExpenseType
            {
                Id = FxExpenseTypeId,
                Code = "FX_DIFFERENCE",
                Name = "FX Difference",
                NamePersian = "تفاوت نرخ ارز",
                Category = "FxDifference"
            },
            new ExpenseType
            {
                Id = CommissionExpenseTypeId,
                Code = "COMMISSION",
                Name = "Commission",
                NamePersian = "کمیسیون",
                Category = "Commission"
            });
        db.SaveChanges();
    }

    /// <summary>رسید ۶۶۵ — ۲۰۰ میلیون روبل، نرخ شرکت ۷۶، نرخ تأمین‌کننده ۷۹٫۳۱۴۶.</summary>
    private static SarrafSettlement Receipt665()
        => Settlement(
            id: 1, reference: "665", companyRate: 76m, supplierRate: 79.3146m,
            amount: 200_000_000m, companyCostUsd: 2_631_578.9474m, supplierAcceptedUsd: 2_521_603.8409m,
            storedDifferenceUsd: 109_975.1065m, differenceType: SarrafSettlementDifferenceType.SupplierShortfall);

    /// <summary>رسید ۶۸۱ — ۳۰۰ میلیون روبل، نرخ شرکت ۷۶، نرخ تأمین‌کننده ۷۷٫۶۹۲۳.</summary>
    private static SarrafSettlement Receipt681()
        => Settlement(
            id: 2, reference: "681", companyRate: 76m, supplierRate: 77.6923m,
            amount: 300_000_000m, companyCostUsd: 3_947_368.4211m, supplierAcceptedUsd: 3_861_386.5209m,
            storedDifferenceUsd: 85_981.9002m, differenceType: SarrafSettlementDifferenceType.SupplierShortfall);

    /// <summary>نمونه سود — نرخ شرکت ۸۲ بهتر از نرخ قبول تأمین‌کننده ۷۹٫۳۱۴۶.</summary>
    private static SarrafSettlement GainSettlement()
        => Settlement(
            id: 3, reference: "690", companyRate: 82m, supplierRate: 79.3146m,
            amount: 200_000_000m, companyCostUsd: 2_439_024.3902m, supplierAcceptedUsd: 2_521_603.8409m,
            storedDifferenceUsd: -82_579.4507m, differenceType: SarrafSettlementDifferenceType.Gain);

    private static SarrafSettlement Settlement(
        int id,
        string reference,
        decimal companyRate,
        decimal? supplierRate,
        decimal amount,
        decimal companyCostUsd,
        decimal supplierAcceptedUsd,
        decimal storedDifferenceUsd,
        SarrafSettlementDifferenceType differenceType)
        => new()
        {
            Id = id,
            SettlementDate = new DateTime(2026, 5, 1),
            Direction = SarrafSettlementDirection.Out,
            CounterpartyType = SarrafSettlementCounterpartyType.Supplier,
            SarrafId = 1,
            SupplierId = 1,
            ContractId = 1,
            ReferenceNumber = reference,
            Description = "پرداخت از طریق صراف",
            RequestedAmount = amount,
            RequestedCurrency = "RUB",
            RequestedFxRateToUsd = 1m / companyRate,
            RequestedAmountUsd = companyCostUsd,
            SarrafCurrency = "RUB",
            SarrafRate = companyRate,
            SarrafChargedAmount = amount,
            SarrafFxRateToUsd = 1m / companyRate,
            SarrafChargedAmountUsd = companyCostUsd,
            SupplierAcceptedAmount = amount,
            SupplierAcceptedCurrency = "RUB",
            SupplierAcceptedFxRateToUsd = supplierRate.HasValue ? 1m / supplierRate.Value : 1m,
            SupplierAcceptedAmountUsd = supplierAcceptedUsd,
            SupplierRate = supplierRate,
            DifferenceAmountUsd = storedDifferenceUsd,
            DifferenceType = differenceType,
            DifferenceReason = DifferenceReason.FxDifference,
            DifferenceTreatment = SarrafSettlementDifferenceTreatment.AcceptedAmountOnly,
            Status = SarrafSettlementStatus.Posted,
            PaymentTransactionId = null,
            CashAccountId = null,
            CreatedByUserId = 5,
            CreatedAtUtc = new DateTime(2026, 5, 1, 9, 30, 0, DateTimeKind.Utc)
        };

    /// <summary>کمیشن واقعی صراف — همان شکلی که PostViaSarrafCommissionAsync می‌سازد.</summary>
    private static void AddCommission(
        ApplicationDbContext db,
        int ledgerId,
        int expenseId,
        decimal amountUsd,
        string reference)
    {
        db.ExpenseTransactions.Add(new ExpenseTransaction
        {
            Id = expenseId,
            ExpenseTypeId = CommissionExpenseTypeId,
            ExpenseDate = new DateTime(2026, 5, 1),
            Amount = amountUsd,
            Currency = "USD",
            AmountUsd = amountUsd,
            Description = "کمیسیون صراف — حواله"
        });
        db.LedgerEntries.Add(new LedgerEntry
        {
            Id = ledgerId,
            EntryDate = new DateTime(2026, 5, 1),
            Side = LedgerSide.Credit,
            AmountUsd = amountUsd,
            SourceType = LedgerEntryOwnership.ViaSarrafPayableSourceType,
            SourceId = 1,
            Reference = reference,
            Description = "بدهی شرکت به صراف بابت کمیسیون"
        });
    }
}
