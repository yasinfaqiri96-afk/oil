namespace PTGOilSystem.Web.Models.Payments;

public sealed class FinanceMetricCardsViewModel
{
    public string AriaLabel { get; init; } = "آمار روزنامچه دریافت و پرداخت";
    public decimal TodayReceiptUsd { get; init; }
    public decimal TodayPaymentUsd { get; init; }
    public int TodayReceiptMissingUsdEquivalentCount { get; init; }
    public int TodayPaymentMissingUsdEquivalentCount { get; init; }
    public decimal CashAccountsBalanceUsd { get; init; }
    /// <summary>اسنادِ ارزیِ بی‌معادلِ دالری که در <see cref="CashAccountsBalanceUsd"/> صفر حساب شده‌اند.</summary>
    public int CashBalanceMissingUsdEquivalentCount { get; init; }
    public int TransactionCount { get; init; }

    public FinanceMetricCardsViewModel WithAriaLabel(string ariaLabel) => new()
    {
        AriaLabel = ariaLabel,
        TodayReceiptUsd = TodayReceiptUsd,
        TodayPaymentUsd = TodayPaymentUsd,
        TodayReceiptMissingUsdEquivalentCount = TodayReceiptMissingUsdEquivalentCount,
        TodayPaymentMissingUsdEquivalentCount = TodayPaymentMissingUsdEquivalentCount,
        CashAccountsBalanceUsd = CashAccountsBalanceUsd,
        CashBalanceMissingUsdEquivalentCount = CashBalanceMissingUsdEquivalentCount,
        TransactionCount = TransactionCount
    };
}
