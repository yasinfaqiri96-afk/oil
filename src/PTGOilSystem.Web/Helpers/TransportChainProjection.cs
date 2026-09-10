using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;

namespace PTGOilSystem.Web.Helpers;

/// <summary>
/// «ادامهٔ حمل به وسیلهٔ دیگر» یک مرحلهٔ فرزند می‌سازد و — فقط برای مقصد موتر — یک
/// <see cref="TruckDispatch"/> به‌عنوان رکورد سازگاری روی رسیدِ حملِ والد. مرجعِ آن عملیات
/// همان مرحلهٔ فرزند است؛ این کمکی، همان یک قاعده را به تمام لیست‌ها، گزارش‌ها و فرم‌ها
/// می‌دهد تا یک بارِ واحد دو بار (هم به‌نام حمل، هم به‌نام ارسال موتر) دیده یا ثبت نشود.
/// </summary>
public static class TransportChainProjection
{
    /// <summary>رسیدهایی که «ادامهٔ حمل» از آن‌ها یک مرحلهٔ فرزند ساخته است.</summary>
    public static IQueryable<int> ContinuedTransferReceiptIds(ApplicationDbContext db)
        => db.InventoryTransportLegAllocations
            .Where(a => a.SourceTransportReceiptId != null)
            .Select(a => a.SourceTransportReceiptId!.Value);

    /// <summary>آیا این دیسپچ فقط رکورد سازگاریِ یک ادامهٔ حمل است؟</summary>
    public static Task<bool> IsContinuationProjectionAsync(
        ApplicationDbContext db,
        TruckDispatch dispatch,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(dispatch);

        if (!dispatch.InventoryTransportReceiptId.HasValue)
        {
            return Task.FromResult(false);
        }

        return db.InventoryTransportLegAllocations
            .AsNoTracking()
            .AnyAsync(a => a.SourceTransportReceiptId == dispatch.InventoryTransportReceiptId.Value, ct);
    }
}
