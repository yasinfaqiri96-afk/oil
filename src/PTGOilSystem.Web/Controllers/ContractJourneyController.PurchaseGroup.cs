using Microsoft.AspNetCore.Mvc;
using PTGOilSystem.Web.Models.ContractJourney;

namespace PTGOilSystem.Web.Controllers;

public partial class ContractJourneyController
{
    /// <summary>
    /// «چرخه قرارداد» ردیف‌های قرارداد خرید چندتأمین‌کننده: برای هر ردیف همان خلاصهٔ رسمی صفحهٔ
    /// جزئیات قرارداد عادی ساخته می‌شود (همان موتور مفاد، مصارف، ضایعات و پرداخت) تا قرارداد مادر
    /// جمع دقیق همین اعداد را نشان دهد، بدون مسیر محاسبهٔ موازی.
    /// </summary>
    [NonAction]
    public async Task<IReadOnlyList<PurchaseGroupLineLifecycle>> BuildPurchaseGroupLifecycleAsync(
        IReadOnlyList<int> lineContractIds)
    {
        var result = new List<PurchaseGroupLineLifecycle>(lineContractIds.Count);
        foreach (var contractId in lineContractIds)
        {
            if (await Details(contractId, ContractJourneyTabs.Details.Summary, lockContract: true)
                is ViewResult { Model: ContractJourneyDetailsViewModel model })
            {
                result.Add(new PurchaseGroupLineLifecycle(contractId, ContractLifecycleFigures.From(model)));
            }
        }

        return result;
    }
}
