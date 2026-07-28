namespace PTGOilSystem.Web.Services.CompanyFlow;

/// <summary>
/// فرمول بیلانس — مطابق فایل‌های کاری شرکت (BNK - Solvex و Petrogas).
///
/// صورت‌حساب طرف‌حساب و دفتر قرارداد (شیت «بیلانس»: بیلانس = مصارف − عواید + قبلی):
///     بیلانس = مانده اول دوره + Σبرد − Σرسید
///     مثبت = شرکت بیشتر داده و طلب/پیش‌پرداخت دارد
///     صفر  = تسویه
///     منفی = شرکت بیشتر گرفته و بدهکار است
///
/// حساب نقدی (صندوق/بانک) برعکس است، چون آنجا خودِ پول موجود می‌شمارد:
///     بیلانس نقدی = موجودی اول دوره + Σرسید − Σبرد
/// </summary>
public sealed class CompanyFlowBalanceService : ICompanyFlowBalanceService
{
    public decimal SignedEffect(CompanyFlowDirection direction, decimal amount, CompanyFlowAccountKind accountKind)
    {
        var outflowIncreasesBalance = accountKind == CompanyFlowAccountKind.PartyAccount;
        var positive = direction == CompanyFlowDirection.Outflow ? outflowIncreasesBalance : !outflowIncreasesBalance;
        return positive ? amount : -amount;
    }

    public decimal Close(decimal openingBalance, decimal totalReceipt, decimal totalOutflow, CompanyFlowAccountKind accountKind)
        => accountKind == CompanyFlowAccountKind.PartyAccount
            ? openingBalance + totalOutflow - totalReceipt
            : openingBalance + totalReceipt - totalOutflow;

    public CompanyFlowBalance Summarize(
        decimal openingBalance,
        IEnumerable<CompanyFlowAmount> amounts,
        CompanyFlowAccountKind accountKind)
    {
        ArgumentNullException.ThrowIfNull(amounts);

        var totalReceipt = 0m;
        var totalOutflow = 0m;
        foreach (var amount in amounts)
        {
            totalReceipt += amount.Receipt;
            totalOutflow += amount.Outflow;
        }

        return new CompanyFlowBalance
        {
            AccountKind = accountKind,
            OpeningBalance = openingBalance,
            TotalReceipt = totalReceipt,
            TotalOutflow = totalOutflow,
            ClosingBalance = Close(openingBalance, totalReceipt, totalOutflow, accountKind)
        };
    }
}
