using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;

namespace PTGOilSystem.Web.Services.PartyStatements;

/// <summary>
/// تعریفِ مرکزیِ «کدام سطرِ دفترکلِ خانوادهٔ تسویهٔ صراف، اثرِ مالیِ جاری است».
///
/// یک «تسویهٔ صراف» هنگام ویرایش، اثرِ قبلی را با یک سطرِ برگشت (EditReversal) خنثی و
/// نسخهٔ تازه را repost می‌کند؛ هنگام لغو هم سطرِ برگشت (Cancel) می‌سازد. پس برای یک
/// تسویه ممکن است چند سطر با همان SourceId در دفترکل باشد:
///   (۱) ثبتِ اصلیِ منسوخ، (۲) سطرِ برگشتِ آن، (۳) ثبتِ فعالِ جدید (repost).
/// اثرِ اقتصادیِ جاری فقط ثبتِ فعالِ جدید است؛ اما هر سه سطر برای تاریخچه/Audit در دفترکل
/// باقی می‌مانند.
///
/// کلیدِ قطعیِ «اثر جاری» حدس‌زدنی نیست (نه با مبلغ، نه تاریخ، نه توضیح): خودِ رکوردِ
/// <see cref="SarrafSettlement"/> با <see cref="SarrafSettlement.LedgerEntryId"/> و
/// <see cref="SarrafSettlement.ExchangeDifferenceLedgerEntryId"/> به سطرِ فعالِ فعلی اشاره
/// می‌کند و سرویس در هر Create/Edit آن را بازنویسی می‌کند
/// (رجوع: SarrafSettlementService.EditPostedAsync). بنابراین:
///
///   یک سطرِ خانوادهٔ تسویهٔ صراف «مؤثر» است ⇔ یک تسویهٔ Posted از طریق یکی از این دو FK به
///   آن اشاره کند. ثبت‌های منسوخ، سطرهای برگشت، و سطرهای تسویهٔ لغوشده (غیر Posted) به هیچ
///   FKِ فعالی اشاره‌شده نیستند و «مؤثر» شمرده نمی‌شوند.
///
/// این فقط لایهٔ خواندن/جمع/نمایش را اصلاح می‌کند؛ هیچ LedgerEntry حذف یا ویرایش نمی‌شود.
/// نقض‌های داده (تسویهٔ Posted بدون سطرِ دفترکلِ معتبر) توسط ReconciliationController
/// به‌عنوان «نیازمند بازبینی» گزارش می‌شوند و اینجا خاموش نادیده گرفته نمی‌شوند.
/// </summary>
public static class SarrafSettlementLedgerEffectiveness
{
    /// <summary>چهار SourceTypeِ دفترکل که به خانوادهٔ تسویهٔ صراف تعلق دارند.</summary>
    public static readonly IReadOnlyList<string> FamilySourceTypes = new[]
    {
        SarrafSettlementService.SupplierLedgerSourceType,     // "SarrafSettlement"
        SarrafSettlementService.ExchangeDifferenceSourceType, // "SarrafSettlementExchangeDifference"
        SarrafSettlementService.EditReversalSourceType,       // "SarrafSettlementEditReversal"
        SarrafSettlementService.CancelSourceType              // "SarrafSettlementCancel"
    };

    /// <summary>
    /// سطرهای منسوخ/برگشتیِ خانوادهٔ تسویهٔ صراف را از یک Queryِ دفترکل حذف می‌کند و فقط اثرِ
    /// جاری (سطری که یک تسویهٔ Posted به آن اشاره می‌کند) را نگه می‌دارد. سطرهای خارج از این
    /// خانواده دست‌نخورده می‌مانند. قابل ترجمه به SQL (EXISTS همبسته روی SarrafSettlements).
    /// </summary>
    public static IQueryable<LedgerEntry> WhereEffectiveSarrafSettlementLegs(
        this IQueryable<LedgerEntry> query,
        ApplicationDbContext db)
        => query.Where(l =>
            (l.SourceType != SarrafSettlementService.SupplierLedgerSourceType
             && l.SourceType != SarrafSettlementService.ExchangeDifferenceSourceType
             && l.SourceType != SarrafSettlementService.EditReversalSourceType
             && l.SourceType != SarrafSettlementService.CancelSourceType)
            || db.SarrafSettlements.Any(s =>
                s.Status == SarrafSettlementStatus.Posted
                && (s.LedgerEntryId == l.Id || s.ExchangeDifferenceLedgerEntryId == l.Id)));

    /// <summary>
    /// مجموعهٔ شناسه‌های دفترکلِ «اثرِ جاری»: سطرهایی که یک تسویهٔ Posted از طریق
    /// LedgerEntryId یا ExchangeDifferenceLedgerEntryId به آن‌ها اشاره می‌کند. برای فیلترِ
    /// درون‌حافظه‌ای وقتی سطرها قبلاً materialize شده‌اند.
    /// </summary>
    public static async Task<IReadOnlySet<int>> LoadEffectiveLedgerEntryIdsAsync(
        ApplicationDbContext db,
        CancellationToken ct = default)
    {
        var refs = await db.SarrafSettlements
            .AsNoTracking()
            .Where(s => s.Status == SarrafSettlementStatus.Posted
                && (s.LedgerEntryId != null || s.ExchangeDifferenceLedgerEntryId != null))
            .Select(s => new { s.LedgerEntryId, s.ExchangeDifferenceLedgerEntryId })
            .ToListAsync(ct);

        var ids = new HashSet<int>();
        foreach (var r in refs)
        {
            if (r.LedgerEntryId.HasValue)
            {
                ids.Add(r.LedgerEntryId.Value);
            }

            if (r.ExchangeDifferenceLedgerEntryId.HasValue)
            {
                ids.Add(r.ExchangeDifferenceLedgerEntryId.Value);
            }
        }

        return ids;
    }

    /// <summary>آیا این SourceType به خانوادهٔ تسویهٔ صراف تعلق دارد؟</summary>
    public static bool IsFamilySourceType(string? sourceType)
        => sourceType == SarrafSettlementService.SupplierLedgerSourceType
            || sourceType == SarrafSettlementService.ExchangeDifferenceSourceType
            || sourceType == SarrafSettlementService.EditReversalSourceType
            || sourceType == SarrafSettlementService.CancelSourceType;

    /// <summary>
    /// آیا این سطر یک ثبتِ منسوخ/برگشتیِ تسویهٔ صراف است (باید از جمع و نمایشِ اثرِ جاری کنار
    /// برود)؟ سطرِ خانواده وقتی منسوخ است که هیچ تسویهٔ Posted از طریق FK به آن اشاره نکند.
    /// </summary>
    public static bool IsSuperseded(string? sourceType, int ledgerEntryId, IReadOnlySet<int> effectiveLedgerEntryIds)
        => IsFamilySourceType(sourceType) && !effectiveLedgerEntryIds.Contains(ledgerEntryId);
}
