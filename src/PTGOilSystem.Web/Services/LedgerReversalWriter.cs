using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services.Ledger;

namespace PTGOilSystem.Web.Services;

/// <summary>
/// برگشت audit-preserving برای Ledger قدیمی: ردیف اصلی دست‌نخورده می‌ماند و یک ردیف
/// جبرانیِ هم‌مبلغ با جهت معکوس کنار آن ثبت می‌شود.
///
/// PTG-P1-03 — منطقِ ساختِ ردیف دیگر اینجا تکرار نمی‌شود؛ این کلاس فقط همان امضای
/// آشنای static را نگه می‌دارد و کار را به <see cref="ILedgerPostingService.ReverseAsync"/>
/// می‌سپارد. رفتار، پیام‌ها و محافظِ «دو بار برگشت» دقیقاً همان‌اند.
/// </summary>
public static class LedgerReversalWriter
{
    // یک تعریف برای نشانهٔ برگشت: نویسندهٔ سطر و خوانندهٔ صورت‌حساب باید دقیقاً یک رشته
    // را بشناسند، وگرنه سطر برگشت مثل سند اصلی خوانده می‌شود و اثر را دو برابر می‌کند.
    public const string CancelReferenceSuffix = CompanyFlow.CompanyFlowSourceTypes.ReversalReferenceSuffix;

    public static Task<LedgerEntry?> ReverseAsync(
        ApplicationDbContext db,
        LedgerEntry original,
        DateTime reversalDate,
        string description,
        string fallbackReference,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(original);

        return new LedgerPostingService(db)
            .ReverseAsync(original, reversalDate, description, fallbackReference, ct);
    }
}
