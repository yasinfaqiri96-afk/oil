using System.Linq.Expressions;
using PTGOilSystem.Web.Models.Entities;

namespace PTGOilSystem.Web.Services.Reporting;

public enum CostBearer
{
    /// <summary>شرکت هزینه را می‌پردازد؛ از سودِ شرکت کم می‌شود.</summary>
    Company = 1,

    /// <summary>طرفِ بیرونیِ قرارداد (خریدارِ قرارداد فروش یا فروشندهٔ قرارداد خرید) هزینه را بر عهده دارد.</summary>
    ExternalParty = 2,

    /// <summary>«مشترک» بدون هیچ سهمِ ثبت‌شده؛ تا وقتی قاعدهٔ سهم وجود ندارد مثل قبل به حسابِ شرکت می‌ماند.</summary>
    SharedUndetermined = 3
}

/// <summary>
/// تنها سیاستِ «این مصرف از سودِ شرکت کم می‌شود یا نه».
/// <para>
/// «خریدار» و «فروشنده» نسبت به طرفِ قرارداد معنا می‌گیرند، نه خودِ کلمه: در قرارداد فروش شرکت
/// فروشنده است، در قرارداد خرید خریدار. پس فقط این دو حالت قطعاً بیرونی‌اند: «بدوش خریدار» روی قرارداد
/// فروش و «بدوش فروشنده» روی قرارداد خرید. هر حالتِ دیگر (بی‌قرارداد، نامشخص، خالی) مثل قبل به حسابِ
/// شرکت است.
/// </para>
/// <para>
/// «مشترک» فیلدِ سهم ندارد؛ هیچ درصدی حدس زده نمی‌شود و رفتارش تغییر نکرده است (به حساب شرکت).
/// </para>
/// </summary>
public static class CostResponsibilityPolicy
{
    public static CostBearer Resolve(CostResponsibility? responsibility, ContractType? contractType)
        => responsibility switch
        {
            CostResponsibility.Buyer when contractType == ContractType.Sale => CostBearer.ExternalParty,
            CostResponsibility.Seller when contractType == ContractType.Purchase => CostBearer.ExternalParty,
            CostResponsibility.Shared => CostBearer.SharedUndetermined,
            _ => CostBearer.Company
        };

    /// <summary>همان قاعدهٔ <see cref="Resolve"/> به شکلِ قابلِ ترجمه به SQL: مصرفی که از سودِ شرکت کم می‌شود.</summary>
    public static readonly Expression<Func<ExpenseTransaction, bool>> IsCompanyBorne = e =>
        !(e.CostResponsibility == CostResponsibility.Buyer
            && e.Contract != null
            && e.Contract.ContractType == ContractType.Sale)
        && !(e.CostResponsibility == CostResponsibility.Seller
            && e.Contract != null
            && e.Contract.ContractType == ContractType.Purchase);

    /// <summary>مکملِ <see cref="IsCompanyBorne"/>: مصرفی که طرفِ بیرونی بر عهده دارد.</summary>
    public static readonly Expression<Func<ExpenseTransaction, bool>> IsExternalPartyBorne = e =>
        (e.CostResponsibility == CostResponsibility.Buyer
            && e.Contract != null
            && e.Contract.ContractType == ContractType.Sale)
        || (e.CostResponsibility == CostResponsibility.Seller
            && e.Contract != null
            && e.Contract.ContractType == ContractType.Purchase);
}
