namespace PTGOilSystem.Web.Services;

/// <summary>
/// سود و زیانِ «اضافه‌بار حمل» — ریاضیِ خالص، بدون دسترسی به دیتابیس.
///
/// اضافه‌بار هنگام تخلیه فقط «افزایش واقعی مقدار جنس» است، نه سود. شرکت برای آن تن‌ها
/// هیچ پولی نداده، پس هیچ قیمت خریدی نباید برایش ساخته شود و قیمت تمام‌شدهٔ آن صفر است.
/// سود فقط وقتی قطعی می‌شود که همان تن‌ها به مشتری فروخته شوند؛ تا آن وقت فقط در موجودی می‌مانند.
///
/// مبنای «چقدر از اضافه‌بار فروخته شد» مقدار بارگیری است: هر تنی که بیش از وزن بارگیری به
/// مشتری فروخته شده، از دلِ اضافه‌بار آمده است. همین قاعده به‌طور خودکار فروش قسمتی، ویرایش
/// مقدار فروش و لغو فروش را هم پوشش می‌دهد، چون همیشه از مقدارِ فعلیِ فروشِ فعال محاسبه می‌شود
/// و هیچ عددی ذخیره نمی‌گردد ⇒ دوباره‌شماری ممکن نیست.
/// </summary>
public static class SurplusPnlMath
{
    /// <summary>
    /// مقدارِ اضافه‌بارِ فروخته‌شده = آن بخش از فروش که از وزن بارگیری فراتر رفته،
    /// حداکثر تا خودِ اضافه‌بارِ ثبت‌شده. بدون فروشِ فعال، صفر است.
    /// </summary>
    public static decimal SoldSurplus(decimal surplusRecordedMt, decimal loadedQuantityMt, decimal saleQuantityMt)
    {
        if (surplusRecordedMt <= 0m || saleQuantityMt <= 0m)
        {
            return 0m;
        }

        var aboveLoaded = saleQuantityMt - loadedQuantityMt;
        return aboveLoaded <= 0m ? 0m : Round(Math.Min(aboveLoaded, surplusRecordedMt));
    }

    /// <summary>اضافه‌بارِ فروخته‌نشده — فقط در موجودی می‌ماند و در سود حساب نمی‌شود.</summary>
    public static decimal UnsoldSurplus(decimal surplusRecordedMt, decimal soldSurplusMt)
        => Round(Math.Max(surplusRecordedMt - soldSurplusMt, 0m));

    /// <summary>عاید حاصل از اضافه‌بار = مقدار فروخته‌شده × قیمت واحدِ همان فاکتور.</summary>
    public static decimal Revenue(decimal soldSurplusMt, decimal saleUnitPriceUsd)
        => soldSurplusMt <= 0m || saleUnitPriceUsd <= 0m ? 0m : Round(soldSurplusMt * saleUnitPriceUsd);

    /// <summary>
    /// سهمِ اضافه‌بار از مصارف واقعیِ همان وسیله (کرایه، کمیسیون و سایر مصارف ثبت‌شده)،
    /// به نسبت تنِ فروخته‌شده به کلِ تنِ حمل‌شده. هیچ مصرف تازه‌ای ساخته نمی‌شود؛ فقط
    /// مصارفِ موجود سهم‌بندی می‌شود، پس مجموع مصارف هرگز از مصارف واقعی بیشتر نمی‌شود.
    /// </summary>
    public static decimal AllocatableExpense(decimal soldSurplusMt, decimal carriedQuantityMt, decimal allocatableExpenseUsd)
    {
        if (soldSurplusMt <= 0m || carriedQuantityMt <= 0m || allocatableExpenseUsd <= 0m)
        {
            return 0m;
        }

        var share = Math.Min(soldSurplusMt / carriedQuantityMt, 1m);
        return Round(allocatableExpenseUsd * share);
    }

    /// <summary>منفعت خالص اضافه‌بار = عاید منهای مصارف قابل تخصیص (قیمت خرید ندارد).</summary>
    public static decimal NetProfit(decimal revenueUsd, decimal allocatableExpenseUsd)
        => Round(revenueUsd - allocatableExpenseUsd);

    private static decimal Round(decimal value) => decimal.Round(value, 4, MidpointRounding.AwayFromZero);
}
