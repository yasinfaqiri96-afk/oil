using PTGOilSystem.Web.Models.Reports;

namespace PTGOilSystem.Web.Services.Reporting;

/// <summary>
/// تنها مسیر تبدیل خروجی <see cref="IProfitAndLossService"/> به مدل نمایش.
/// هیچ محاسبهٔ مالی اینجا انجام نمی‌شود؛ فقط نگاشت میدان‌ها.
/// </summary>
public static class RealisedPnlViewModelMapper
{
    public static RealisedPnlViewModel ToViewModel(this SalesPnlSnapshot snapshot)
        => new()
        {
            RevenueUsd = snapshot.RevenueUsd,
            CostOfGoodsSoldUsd = snapshot.CostOfGoodsSoldUsd,
            GrossProfitUsd = snapshot.GrossProfitUsd,
            SaleCount = snapshot.SaleCount,
            CostedSaleCount = snapshot.CostedSaleCount,
            UncostedSaleCount = snapshot.UncostedSaleCount,
            Confidence = snapshot.Confidence.ToString()
        };
}
