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
        var direction = ResolveOriginalDirection(flowEvent);

        // سطر معکوس‌کننده دقیقاً وارونهٔ سند اصلی است تا گردش دوبرابر نشود و بیلانس برنگردد.
        return flowEvent.Lifecycle == CompanyFlowLifecycle.Reversal
            ? Invert(direction)
            : direction;
    }

    public static CompanyFlowDirection Invert(CompanyFlowDirection direction)
        => direction == CompanyFlowDirection.Receipt
            ? CompanyFlowDirection.Outflow
            : CompanyFlowDirection.Receipt;

    private static CompanyFlowDirection ResolveOriginalDirection(in CompanyFlowEvent flowEvent)
    {
        // ۱) معنی تجاری قطعی — مستقل از سمت حسابداری و نقش طرف‌حساب.
        switch (flowEvent.SourceType)
        {
            // شرکت کالا/بار از تأمین‌کننده گرفته است.
            case CompanyFlowSourceTypes.Loading:
            // شرکت خدمت/کالا گرفته و تعهد ایجاد شده است. پرداخت همین مصرف «برد» جداگانه است.
            case CompanyFlowSourceTypes.Expense:
            // معاش/بونس تعهدشده = کار و خدمتی که شرکت از کارمند گرفته است.
            case CompanyFlowSourceTypes.SalaryAccrual:
            case CompanyFlowSourceTypes.Bonus:
                return CompanyFlowDirection.Receipt;

            // شرکت کالا را به مشتری تحویل داده است.
            case CompanyFlowSourceTypes.Sale:
            // پول از شرکت به کارمند رفته است (معاش، مساعده) یا تعهد بدون پرداخت کم شده است.
            case CompanyFlowSourceTypes.SalaryPayment:
            case CompanyFlowSourceTypes.SalaryAdvance:
            case CompanyFlowSourceTypes.SalaryDeduction:
                return CompanyFlowDirection.Outflow;
        }

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
