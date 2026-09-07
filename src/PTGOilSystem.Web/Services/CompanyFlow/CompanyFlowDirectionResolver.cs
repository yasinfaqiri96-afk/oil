using PTGOilSystem.Web.Models.Entities;

namespace PTGOilSystem.Web.Services.CompanyFlow;

/// <summary>
/// پیاده‌سازی مرکزی «رسید یا برد» از دید شرکت.
///
/// ترتیب تصمیم — از قطعی‌ترین به عمومی‌ترین:
///  ۱) SourceTypeهایی که معنی تجاری‌شان قطعی است (بارگیری، فروش، مصرف) — سمت حسابداری نادیده گرفته می‌شود.
///  ۲) جهت واقعی حرکت پول برای اسناد نقدی (PaymentDirection).
///  ۳) نقش طرف‌حساب + سمت حسابداری.
///
/// وارونه‌سازیِ سطرِ برگشت فقط برای مرحلهٔ (۱) انجام می‌شود؛ دلیلش در <c>Resolve</c> آمده است.
///
/// چرا مرحلهٔ (۱) لازم است: یک <see cref="LedgerSide"/> در دو نوع فعالیت معنی تجاری متفاوت دارد.
/// نمونهٔ اثبات‌شده: «مصرف» با <c>LedgerSide.Debit</c> ثبت می‌شود، دقیقاً همان سمتی که «پرداخت
/// همان مصرف» دارد؛ اگر جهت را از سمت بخوانیم هر دو یک‌جهت می‌شوند و بیلانس شرکت خدماتی
/// دو برابر می‌شود. مصرف در واقع «دریافت خدمت» است، پس رسید است و پرداختش برد.
///
/// قاعدهٔ مرحلهٔ (۳): کنوانسیون خودِ سیستم این است که Credit مانده طبیعی طرف‌حساب را زیاد و
/// Debit آن را کم می‌کند. برای حساب‌های پرداختنی (تأمین‌کننده، شرکت خدماتی، راننده، کارمند،
/// صراف، شریک) مانده طبیعی «بدهی ما» است، پس Credit یعنی ارزشی گرفته‌ایم (رسید) و Debit یعنی
/// پولی داده‌ایم (برد). برای حساب دریافتنی (مشتری) دقیقاً برعکس است.
/// </summary>
public sealed class CompanyFlowDirectionResolver : ICompanyFlowDirectionResolver
{
    public CompanyFlowDirection Resolve(in CompanyFlowEvent flowEvent)
    {
        // سطرِ برگشت همیشه با سمتِ معکوسِ سند اصلی نوشته می‌شود (LedgerPostingService.ReverseAsync،
        // AssetRentLedgerFactory، SupplierPaymentAllocationService، ThreeWaySettlementController، …).
        // پس فقط جایی باید جهت را وارونه کنیم که جهت از روی سمت خوانده *نشده* باشد:
        //
        //   • مرحلهٔ ۱ (SourceType قطعی): سمت اصلاً دیده نمی‌شود، پس بارگیری و برگشتِ بارگیری هر دو
        //     «رسید» خوانده می‌شوند. اینجا وارونه‌کردنِ صریح لازم است وگرنه اثر دو برابر می‌شود.
        //   • مرحلهٔ ۲ و ۳ (PaymentDirection یا سمت حسابداری): خودِ سطرِ برگشت وارونه است، پس جهتِ
        //     محاسبه‌شده از قبل وارونه است. وارونه‌کردنِ دوباره یعنی برگشت به جهتِ سند اصلی و
        //     دو برابر شدنِ مانده — همان چیزی که در «لغو کرایه دارایی»، «برگشت تخصیص پیش‌پرداخت»
        //     و «لغو تسویه سه‌طرفه» رخ می‌داد.
        if (TryResolveByBusinessMeaning(flowEvent.SourceType, out var deterministic))
        {
            return flowEvent.Lifecycle == CompanyFlowLifecycle.Reversal
                ? Invert(deterministic)
                : deterministic;
        }

        return ResolveByMovement(flowEvent);
    }

    public static CompanyFlowDirection Invert(CompanyFlowDirection direction)
        => direction == CompanyFlowDirection.Receipt
            ? CompanyFlowDirection.Outflow
            : CompanyFlowDirection.Receipt;

    /// <summary>
    /// مرحلهٔ ۱ — معنی تجاری قطعی. سمت حسابداری و نقش طرف‌حساب اصلاً خوانده نمی‌شوند، پس سطرِ
    /// برگشتِ همین اسناد هم همان جهت را می‌دهد و باید صریح وارونه شود.
    /// </summary>
    private static bool TryResolveByBusinessMeaning(string? sourceType, out CompanyFlowDirection direction)
    {
        switch (sourceType)
        {
            // شرکت کالا/بار از تأمین‌کننده گرفته است.
            case CompanyFlowSourceTypes.Loading:
            // شرکت خدمت/کالا گرفته و تعهد ایجاد شده است. پرداخت همین مصرف «برد» جداگانه است.
            case CompanyFlowSourceTypes.Expense:
            // معاش/بونس تعهدشده = کار و خدمتی که شرکت از کارمند گرفته است.
            case CompanyFlowSourceTypes.SalaryAccrual:
            case CompanyFlowSourceTypes.Bonus:
                direction = CompanyFlowDirection.Receipt;
                return true;

            // شرکت کالا را به مشتری تحویل داده است.
            case CompanyFlowSourceTypes.Sale:
            // پول از شرکت به کارمند رفته است (معاش، مساعده) یا تعهد بدون پرداخت کم شده است.
            case CompanyFlowSourceTypes.SalaryPayment:
            case CompanyFlowSourceTypes.SalaryAdvance:
            case CompanyFlowSourceTypes.SalaryDeduction:
                direction = CompanyFlowDirection.Outflow;
                return true;

            default:
                direction = CompanyFlowDirection.Outflow;
                return false;
        }
    }

    /// <summary>
    /// مرحلهٔ ۲ و ۳ — جهت از خودِ حرکت خوانده می‌شود (جهت نقدی، یا سمت حسابداری + نقش طرف‌حساب).
    /// سطرِ برگشت چون سمتِ معکوس دارد، همین‌جا خودبه‌خود جهتِ معکوس می‌گیرد.
    /// </summary>
    private static CompanyFlowDirection ResolveByMovement(in CompanyFlowEvent flowEvent)
    {
        // ۲) اسناد نقدی: جهت واقعی حرکت پول نسبت به شرکت.
        if (flowEvent.PaymentDirection.HasValue)
        {
            return flowEvent.PaymentDirection.Value == Models.Entities.PaymentDirection.In
                ? CompanyFlowDirection.Receipt
                : CompanyFlowDirection.Outflow;
        }

        // ۳) نقش طرف‌حساب + سمت حسابداری.
        if (!flowEvent.LedgerSide.HasValue)
        {
            // بدون هیچ نشانه‌ای، «برد» امن‌ترین پیش‌فرض نیست؛ سند بی‌سمت اصلاً گردش ندارد.
            // چنین سطری فقط اطلاعاتی است و صفر می‌ماند، اما جهت باید مقدار معتبر برگرداند.
            return CompanyFlowDirection.Outflow;
        }

        var increasesPartyNaturalBalance = flowEvent.LedgerSide.Value == LedgerSide.Credit;
        return IsReceivableAccount(flowEvent.PartyRole)
            ? (increasesPartyNaturalBalance ? CompanyFlowDirection.Outflow : CompanyFlowDirection.Receipt)
            : (increasesPartyNaturalBalance ? CompanyFlowDirection.Receipt : CompanyFlowDirection.Outflow);
    }

    /// <summary>
    /// مشتری تنها طرف‌حسابِ ذاتاً «دریافتنی» است؛ بقیه پرداختنی‌اند. حساب شرکت (نمای داخلی)
    /// هم پرداختنی در نظر گرفته می‌شود، ولی رویدادهای اصلی‌اش (فروش/بارگیری) در مرحلهٔ ۱
    /// تعیین تکلیف شده‌اند و به این قاعده نمی‌رسند.
    /// </summary>
    private static bool IsReceivableAccount(CompanyFlowPartyRole role)
        => role == CompanyFlowPartyRole.Customer;
}
