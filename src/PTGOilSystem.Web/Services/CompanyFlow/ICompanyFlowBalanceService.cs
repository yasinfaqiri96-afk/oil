namespace PTGOilSystem.Web.Services.CompanyFlow;

/// <summary>
/// محاسبهٔ مرکزی بیلانس. تفاوت «صورت‌حساب طرف‌حساب» و «حساب نقدی» فقط اینجا مدیریت می‌شود.
/// </summary>
public interface ICompanyFlowBalanceService
{
    /// <summary>اثر یک ردیف روی بیلانس همان نوع حساب (با علامت).</summary>
    decimal SignedEffect(CompanyFlowDirection direction, decimal amount, CompanyFlowAccountKind accountKind);

    /// <summary>بیلانس پایان دوره از روی مانده اول دوره و مجموع رسید/برد.</summary>
    decimal Close(decimal openingBalance, decimal totalReceipt, decimal totalOutflow, CompanyFlowAccountKind accountKind);

    /// <summary>جمع‌بندی کامل یک مجموعه ردیف.</summary>
    CompanyFlowBalance Summarize(
        decimal openingBalance,
        IEnumerable<CompanyFlowAmount> amounts,
        CompanyFlowAccountKind accountKind);
}
