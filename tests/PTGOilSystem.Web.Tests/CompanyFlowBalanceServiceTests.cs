using PTGOilSystem.Web.Services.CompanyFlow;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// فرمول بیلانس مطابق فایل کاری شرکت:
///   صورت‌حساب طرف‌حساب / دفتر قرارداد → بیلانس = اول دوره + Σبرد − Σرسید
///   حساب نقدی                          → بیلانس = اول دوره + Σرسید − Σبرد
/// </summary>
public sealed class CompanyFlowBalanceServiceTests
{
    private static readonly CompanyFlowBalanceService Service = new();

    private static CompanyFlowBalance Party(decimal opening, params CompanyFlowAmount[] amounts)
        => Service.Summarize(opening, amounts, CompanyFlowAccountKind.PartyAccount);

    private static CompanyFlowAmount Receipt(decimal amount) => new(CompanyFlowDirection.Receipt, amount);
    private static CompanyFlowAmount Outflow(decimal amount) => new(CompanyFlowDirection.Outflow, amount);

    [Fact]
    public void ReceiptOnly_MakesTheBalanceNegative_MeaningTheCompanyOwes()
    {
        var balance = Party(0m, Receipt(1_000m));

        Assert.Equal(1_000m, balance.TotalReceipt);
        Assert.Equal(0m, balance.TotalOutflow);
        Assert.Equal(-1_000m, balance.ClosingBalance);
        Assert.Contains("بدهکار", CompanyFlowText.BalanceMeaning(balance.ClosingBalance, CompanyFlowAccountKind.PartyAccount));
    }

    [Fact]
    public void OutflowOnly_MakesTheBalancePositive_MeaningTheCompanyIsOwed()
    {
        var balance = Party(0m, Outflow(1_000m));

        Assert.Equal(1_000m, balance.ClosingBalance);
        Assert.Contains("طلب", CompanyFlowText.BalanceMeaning(balance.ClosingBalance, CompanyFlowAccountKind.PartyAccount));
    }

    [Fact]
    public void EqualReceiptAndOutflow_SettleTheAccount()
    {
        var balance = Party(0m, Receipt(750m), Outflow(750m));

        Assert.Equal(0m, balance.ClosingBalance);
        Assert.Contains("تسویه", CompanyFlowText.BalanceMeaning(balance.ClosingBalance, CompanyFlowAccountKind.PartyAccount));
    }

    [Fact]
    public void Prepayment_ThenGoods_MovesFromPositiveToSettled()
    {
        // پیش‌پرداخت ۱۰۰۰ به تأمین‌کننده، سپس دریافت ۱۰۰۰ بار.
        Assert.Equal(1_000m, Party(0m, Outflow(1_000m)).ClosingBalance);
        Assert.Equal(0m, Party(0m, Outflow(1_000m), Receipt(1_000m)).ClosingBalance);
    }

    [Fact]
    public void PartialPayment_LeavesTheRemainingDebt()
    {
        // ۱۰۰۰ بار گرفته‌ایم و فقط ۴۰۰ پرداخت کرده‌ایم ⇒ ۶۰۰ بدهکاریم.
        var balance = Party(0m, Receipt(1_000m), Outflow(400m));
        Assert.Equal(-600m, balance.ClosingBalance);
    }

    [Fact]
    public void OpeningBalance_CarriesIntoTheClosingBalance()
        => Assert.Equal(250m, Party(-250m, Outflow(500m)).ClosingBalance);

    [Fact]
    public void CashAccount_UsesTheOppositeFormula()
    {
        var cash = Service.Summarize(
            100m,
            [Receipt(500m), Outflow(200m)],
            CompanyFlowAccountKind.CashAccount);

        // موجودی نقدی = اول دوره + رسید − برد
        Assert.Equal(400m, cash.ClosingBalance);
        Assert.Equal(300m, cash.NetMovement);
    }

    [Fact]
    public void SignedEffect_IsConsistentWithSummarize()
    {
        var rows = new[] { Receipt(120m), Outflow(45m), Outflow(5m) };

        var manual = rows.Sum(r => Service.SignedEffect(r.Direction, r.Amount, CompanyFlowAccountKind.PartyAccount));
        var summarized = Service.Summarize(0m, rows, CompanyFlowAccountKind.PartyAccount).ClosingBalance;

        Assert.Equal(summarized, manual);
    }

    [Fact]
    public void EmptyPeriod_KeepsTheOpeningBalance()
    {
        var balance = Party(-1_234.56m);

        Assert.Equal(-1_234.56m, balance.ClosingBalance);
        Assert.Equal(0m, balance.NetMovement);
    }
}
