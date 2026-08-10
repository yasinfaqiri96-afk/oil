using PTGOilSystem.Web.Helpers;
using PTGOilSystem.Web.Models.Entities;

namespace PTGOilSystem.Web.Services;

/// <summary>
/// منبع واحد تعیین «نرخ خرید» در پروندهٔ محموله (Shipment).
///
/// ترتیب قطعی نرخ برای هر منبع (قرارداد خرید) محموله:
///   1) میانگین وزنیِ بارگیری‌های واقعی همان قرارداد از
///      <see cref="PurchaseAggregationSnapshot.WeightedAveragePurchasePriceUsd"/> —
///      یعنی LoadingRegister.LoadingPriceUsd هر بار (نرخ قطعیِ ثبت‌شده هنگام بارگیری؛
///      Platts بعدی یا نرخ روز اثری ندارد).
///   2) نرخ نهایی/توافقی هدر قرارداد (<see cref="ContractPricingAdapter.GetCanonicalFinalPrice"/>) —
///      فقط fallback برای قراردادهای بدون بارگیریِ قیمت‌دار (دادهٔ قدیمی).
///
/// برای لِج حمل، PurchaseUnitCostUsd خود لِج (override صریح workflow حمل) مقدم است.
/// هیچ Controller/View نباید فرمول جداگانهٔ purchase cost داشته باشد؛ همه از همین‌جا می‌خوانند.
/// </summary>
public static class ShipmentPurchaseCostResolver
{
    public const string SourceLegActualCost = "Transport leg actual cost";
    public const string SourceContractWeightedAverage = "Contract weighted average";
    public const string SourceContractFinalPrice = "Contract final price";
    public const string SourceMissing = "Missing purchase cost";

    /// <summary>نرخ مؤثر یک قرارداد: میانگین وزنی بارگیری‌ها، سپس نرخ نهایی قرارداد.</summary>
    public static (decimal? UnitCostUsd, string Source) ResolveContractUnitCost(
        PurchaseAggregationSnapshot? snapshot,
        decimal? contractFinalPriceUsd)
    {
        if (snapshot?.WeightedAveragePurchasePriceUsd is > 0m)
        {
            return (snapshot.WeightedAveragePurchasePriceUsd.Value, SourceContractWeightedAverage);
        }

        return contractFinalPriceUsd is > 0m
            ? (contractFinalPriceUsd.Value, SourceContractFinalPrice)
            : (null, SourceMissing);
    }

    /// <summary>نرخ مؤثر یک لِج حمل: override خود لِج، سپس همان زنجیرهٔ قرارداد.</summary>
    public static (decimal? UnitCostUsd, string Source) ResolveLegUnitCost(
        InventoryTransportLeg leg,
        IReadOnlyDictionary<int, PurchaseAggregationSnapshot> purchaseSnapshots)
    {
        if (leg.PurchaseUnitCostUsd is > 0m)
        {
            return (leg.PurchaseUnitCostUsd.Value, SourceLegActualCost);
        }

        purchaseSnapshots.TryGetValue(leg.SourcePurchaseContractId, out var snapshot);
        var contractFinalPrice = leg.SourcePurchaseContract is null
            ? null
            : ContractPricingAdapter.GetCanonicalFinalPrice(leg.SourcePurchaseContract);

        return ResolveContractUnitCost(snapshot, contractFinalPrice);
    }
}
