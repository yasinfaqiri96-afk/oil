using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.Reports;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Reporting;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// سود و زیانِ ارزیِ یک نوع رویداد باید هر دو در سود بیایند. پیش از این زیانِ تفاوت نرخِ
/// تأمین‌کننده (سندِ مصرف) در سود بود و سودش (سطرِ دفتر) نه؛ تفاوتِ نرخِ انتقالِ مانده و تخصیصِ
/// پرداخت هم اصلاً در سود نبود.
/// </summary>
public sealed class RealizedFxCanonicalTests
{
    [Fact]
    public async Task Supplier_Fx_Recognition_Gain_And_Loss_Both_Reach_Company_Pnl()
    {
        await using var db = NewDb();
        db.ExpenseTypes.AddRange(
            new ExpenseType { Id = 1, Code = "OPS", Name = "Operations" },
            new ExpenseType { Id = 2, Code = SupplierFxRecognitionService.FxDifferenceExpenseCode, Name = "FX difference", Category = "FxDifference" });
        db.ExpenseTransactions.AddRange(
            Expense(1, typeId: 1, 100m),
            // زیانِ شناسایی‌شدهٔ تفاوت نرخ (سندِ مصرف).
            Expense(2, typeId: 2, 20m));
        // سودِ شناسایی‌شدهٔ تفاوت نرخ (فقط سطرِ دفتر).
        db.LedgerEntries.Add(Ledger(1, SupplierFxRecognitionService.GainLedgerSourceType, LedgerSide.Credit, 30m));
        await db.SaveChangesAsync();

        var pnl = await new ProfitAndLossService(db).BuildCompanyAsync(new ManagementReportFilterViewModel());

        Assert.Equal(100m, pnl.OperatingExpenseUsd);
        Assert.Equal(30m, pnl.ExchangeGainUsd);
        Assert.Equal(20m, pnl.ExchangeLossUsd);
        Assert.Equal(-90m, pnl.NetProfitUsd);
    }

    [Fact]
    public async Task Balance_Transfer_And_Payment_Allocation_Fx_Reach_Company_And_Contract_Pnl_With_Reversals()
    {
        await using var db = NewDb();
        db.LedgerEntries.AddRange(
            // زیانِ تفاوت نرخِ انتقالِ مانده (بدهکار).
            Ledger(1, SupplierBalanceTransferService.ExchangeDifferenceLedgerSourceType, LedgerSide.Debit, 15m, contractId: 7),
            // سودِ تفاوت نرخِ تخصیصِ پرداخت (بستانکار) و برگشتِ کاملِ همان سود.
            Ledger(2, SupplierPaymentAllocationService.ExchangeDifferenceLedgerSourceType, LedgerSide.Credit, 25m, contractId: 7),
            Ledger(3, SupplierPaymentAllocationService.ExchangeDifferenceReversalLedgerSourceType, LedgerSide.Debit, 25m, contractId: 7),
            // سودِ تفاوت نرخِ تخصیصِ دیگری که برگشت نخورده.
            Ledger(4, SupplierPaymentAllocationService.ExchangeDifferenceLedgerSourceType, LedgerSide.Credit, 40m, contractId: 7));
        await db.SaveChangesAsync();

        var service = new ProfitAndLossService(db);
        var company = await service.BuildCompanyAsync(new ManagementReportFilterViewModel());
        var contract = (await service.BuildRealizedFxByContractAsync([7]))[7];

        Assert.Equal(40m, company.ExchangeGainUsd);
        Assert.Equal(15m, company.ExchangeLossUsd);
        Assert.Equal(40m, contract.GainUsd);
        Assert.Equal(15m, contract.LossUsd);
    }

    [Fact]
    public async Task Sarraf_Gain_Is_Counted_Once_From_The_Settlement_Not_Again_From_Its_Ledger_Row()
    {
        await using var db = NewDb();
        db.SarrafSettlements.Add(new SarrafSettlement
        {
            Id = 1,
            SarrafId = 1,
            ContractId = 7,
            SettlementDate = new DateTime(2026, 6, 1),
            Status = SarrafSettlementStatus.Posted,
            DifferenceType = SarrafSettlementDifferenceType.Gain,
            DifferenceAmountUsd = 12m
        });
        db.LedgerEntries.Add(Ledger(1, PaymentsControllerFxGainSourceType, LedgerSide.Credit, 12m, contractId: 7));
        await db.SaveChangesAsync();

        var service = new ProfitAndLossService(db);
        var company = await service.BuildCompanyAsync(new ManagementReportFilterViewModel());
        var contract = (await service.BuildRealizedFxByContractAsync([7]))[7];

        Assert.Equal(12m, company.ExchangeGainUsd);
        Assert.Equal(12m, contract.GainUsd);
    }

    private const string PaymentsControllerFxGainSourceType = "SarrafFxGain";

    private static ExpenseTransaction Expense(int id, int typeId, decimal amountUsd) => new()
    {
        Id = id,
        ExpenseTypeId = typeId,
        ExpenseDate = new DateTime(2026, 6, 1),
        Amount = amountUsd,
        AmountUsd = amountUsd,
        SettlementMode = ExpenseSettlementMode.NonCash
    };

    private static LedgerEntry Ledger(int id, string sourceType, LedgerSide side, decimal amountUsd, int? contractId = null) => new()
    {
        Id = id,
        EntryDate = new DateTime(2026, 6, id),
        SourceType = sourceType,
        SourceId = id,
        Side = side,
        AmountUsd = amountUsd,
        ContractId = contractId,
        SupplierId = 1,
        Description = sourceType
    };

    private static ApplicationDbContext NewDb()
        => new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
}
