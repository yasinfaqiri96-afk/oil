namespace PTGOilSystem.Web.Models.Reports;

/// <summary>
/// نمایش سود محقق‌شدهٔ فروش. مقادیر آن فقط از <c>IProfitAndLossService</c> پر می‌شود
/// و هیچ View، ViewModel یا Export اجازهٔ بازنویسی فرمول درآمد/COGS را ندارد.
/// </summary>
public sealed class RealisedPnlViewModel
{
    public static readonly RealisedPnlViewModel Empty = new();

    public decimal RevenueUsd { get; init; }
    public decimal CostOfGoodsSoldUsd { get; init; }
    public decimal GrossProfitUsd { get; init; }
    public int SaleCount { get; init; }
    public int CostedSaleCount { get; init; }
    public int UncostedSaleCount { get; init; }

    /// <summary>Verified | Estimated | Legacy | NeedsReview</summary>
    public string Confidence { get; init; } = "Verified";

    public bool HasUncostedSales => UncostedSaleCount > 0;

    public string ConfidenceLabelFa => Confidence switch
    {
        "Verified" => "قطعی",
        "Estimated" => "تخمینی",
        "Legacy" => "داده قدیمی",
        _ => "نیازمند بازبینی"
    };

    public string ConfidenceTone => Confidence switch
    {
        "Verified" => "success",
        "Estimated" => "warning",
        "Legacy" => "muted",
        _ => "danger"
    };
}
