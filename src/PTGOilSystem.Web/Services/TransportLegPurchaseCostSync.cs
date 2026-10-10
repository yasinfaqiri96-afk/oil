using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;

namespace PTGOilSystem.Web.Services;

/// <summary>
/// لِجِ حملی که از یک بارگیری ساخته می‌شود، قیمت همان بارگیری را در <c>PurchaseUnitCostUsd</c> نگه می‌دارد
/// (TransportWorkflowService) و این بها در P&amp;L حمل/محموله مقدم بر قرارداد است. پس اصلاح قیمت بارگیری
/// باید به همان لِج‌ها هم برسد، وگرنه سود حمل با قیمت کهنه حساب می‌شود.
/// فقط لِجی عوض می‌شود که همهٔ تخصیص‌هایش از همین بارگیری است و بهایش هنوز همان قیمت قبلی بارگیری است؛
/// لِجِ چندبارگیری یا لِجی که بهایش دستی عوض شده دست نمی‌خورد. SaveChanges با فراخواننده است.
/// </summary>
public static class TransportLegPurchaseCostSync
{
    public static async Task<int> SyncFromLoadingAsync(
        ApplicationDbContext db,
        int loadingRegisterId,
        decimal? previousPriceUsd,
        decimal? newPriceUsd,
        CancellationToken ct = default)
    {
        if (previousPriceUsd == newPriceUsd)
        {
            return 0;
        }

        var legs = await db.InventoryTransportLegs
            .Where(l => l.PurchaseUnitCostUsd == previousPriceUsd
                && l.Allocations.Any(a => a.SourceLoadingRegisterId == loadingRegisterId)
                && l.Allocations.All(a => a.SourceLoadingRegisterId == loadingRegisterId))
            .ToListAsync(ct);

        foreach (var leg in legs)
        {
            leg.PurchaseUnitCostUsd = newPriceUsd;
        }

        return legs.Count;
    }
}
