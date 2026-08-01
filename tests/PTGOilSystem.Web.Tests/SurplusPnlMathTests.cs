using PTGOilSystem.Web.Services;
using Xunit;

namespace PTGOilSystem.Web.Tests;

// قرارداد سود و زیانِ «اضافه‌بار حمل» را قفل می‌کند:
// اضافه‌بار تا فروخته‌نشدن سود نیست، قیمت خرید ندارد، و از مصارف واقعی فقط سهم می‌گیرد.
public class SurplusPnlMathTests
{
    [Theory]
    // بارگیری ۱۰، اضافه‌بار ۰.۲، فروش ۱۰.۲ ⇒ تمام اضافه‌بار فروخته شده.
    [InlineData(0.2, 10, 10.2, 0.2)]
    // اضافه‌بار ثبت‌شده ولی فروخته‌نشده (بدون فروش) ⇒ صفر.
    [InlineData(0.2, 10, 0, 0)]
    // فروش دقیقاً به‌اندازهٔ بارگیری ⇒ هیچ بخشی از اضافه‌بار فروخته نشده.
    [InlineData(0.2, 10, 10, 0)]
    // فروش قسمتی از اضافه‌بار.
    [InlineData(0.2, 10, 10.05, 0.05)]
    // فروش کمتر از بارگیری ⇒ اضافه‌بار دست‌نخورده در موجودی می‌ماند.
    [InlineData(0.2, 10, 9, 0)]
    // فروشِ بیشتر از تخلیه هرگز اضافه‌بارِ بیشتر از ثبت‌شده نمی‌سازد.
    [InlineData(0.2, 10, 12, 0.2)]
    public void SoldSurplus_Is_Only_The_Part_Sold_Above_The_Loaded_Weight(
        decimal recordedMt, decimal loadedMt, decimal saleMt, decimal expected)
    {
        Assert.Equal(expected, SurplusPnlMath.SoldSurplus(recordedMt, loadedMt, saleMt));
    }

    [Fact]
    public void Cancelling_The_Sale_Leaves_No_Sold_Surplus()
    {
        // فروش لغوشده ⇒ caller مقدار فروش صفر می‌دهد ⇒ اضافه‌بار دوباره فقط موجودی است.
        Assert.Equal(0.2m, SurplusPnlMath.SoldSurplus(0.2m, 10m, 10.2m));
        Assert.Equal(0m, SurplusPnlMath.SoldSurplus(0.2m, 10m, 0m));
    }

    [Theory]
    [InlineData(0.2, 0, 0.2)]
    [InlineData(0.2, 0.2, 0)]
    [InlineData(0.2, 0.5, 0)] // فروخته‌نشده هرگز منفی نمی‌شود
    public void UnsoldSurplus_Never_Goes_Negative(decimal recordedMt, decimal soldMt, decimal expected)
    {
        Assert.Equal(expected, SurplusPnlMath.UnsoldSurplus(recordedMt, soldMt));
    }

    [Theory]
    [InlineData(0.2, 500, 100)]
    [InlineData(0, 500, 0)]      // فروخته‌نشده ⇒ هیچ عایدی
    [InlineData(0.2, 0, 0)]      // بدون قیمت ⇒ هیچ عایدی
    public void Revenue_Comes_Only_From_The_Sold_Surplus(decimal soldMt, decimal unitPriceUsd, decimal expected)
    {
        Assert.Equal(expected, SurplusPnlMath.Revenue(soldMt, unitPriceUsd));
    }

    [Theory]
    // ۰.۲ از ۱۰.۲ تنِ حمل‌شده ⇒ همان نسبت از مصارف واقعی.
    [InlineData(0.2, 10.2, 510, 10)]
    [InlineData(0, 10.2, 510, 0)]     // فروخته‌نشده ⇒ هیچ مصرفی تخصیص نمی‌یابد
    [InlineData(0.2, 0, 510, 0)]      // بدون تنِ حمل‌شده ⇒ بدون تقسیم بر صفر
    [InlineData(0.2, 10.2, 0, 0)]     // بدون مصرف واقعی ⇒ صفر
    [InlineData(12, 10, 500, 500)]    // سهم هرگز از کلِ مصرف واقعی بیشتر نمی‌شود
    public void AllocatableExpense_Is_A_Share_Of_Real_Expenses_Only(
        decimal soldMt, decimal carriedMt, decimal expenseUsd, decimal expected)
    {
        Assert.Equal(expected, SurplusPnlMath.AllocatableExpense(soldMt, carriedMt, expenseUsd));
    }

    [Fact]
    public void Full_Scenario_Loaded_10_Unloaded_And_Sold_10_Point_2()
    {
        const decimal loadedMt = 10m;
        const decimal unloadedMt = 10.2m;
        const decimal saleMt = 10.2m;
        const decimal unitPriceUsd = 500m;
        const decimal realExpenseUsd = 510m; // کرایه واقعی کل موتر

        var recordedMt = TransportVarianceMath.Surplus(TransportVarianceMath.Difference(loadedMt, unloadedMt));
        Assert.Equal(0.2m, recordedMt);

        var soldMt = SurplusPnlMath.SoldSurplus(recordedMt, loadedMt, saleMt);
        var revenueUsd = SurplusPnlMath.Revenue(soldMt, unitPriceUsd);
        var expenseUsd = SurplusPnlMath.AllocatableExpense(soldMt, unloadedMt, realExpenseUsd);
        var netProfitUsd = SurplusPnlMath.NetProfit(revenueUsd, expenseUsd);

        Assert.Equal(0.2m, soldMt);
        Assert.Equal(100m, revenueUsd);
        Assert.Equal(10m, expenseUsd);
        Assert.Equal(90m, netProfitUsd);

        // مجموع عاید فروش = قیمت × مقدار واقعی فروخته‌شده، و سهمِ اضافه‌بار دقیقاً بخشی از همان است.
        var totalRevenueUsd = saleMt * unitPriceUsd;
        Assert.Equal(5100m, totalRevenueUsd);
        Assert.Equal(totalRevenueUsd - loadedMt * unitPriceUsd, revenueUsd);

        // برای اضافه‌بار هیچ قیمت خریدی ساخته نمی‌شود؛ قیمت تمام‌شده همان خریدِ ۱۰ تن می‌ماند.
        const decimal purchaseUnitCostUsd = 400m;
        var costOfGoodsUsd = loadedMt * purchaseUnitCostUsd;
        Assert.Equal(4000m, costOfGoodsUsd);
        Assert.Equal(costOfGoodsUsd, decimal.Round(loadedMt * purchaseUnitCostUsd, 4, MidpointRounding.AwayFromZero));
    }

    [Fact]
    public void Recomputing_The_Same_Row_Twice_Gives_The_Same_Numbers()
    {
        // اعداد مشتق‌شده‌اند و هیچ‌جا ذخیره نمی‌شوند، پس محاسبهٔ دوباره سود را دوبرابر نمی‌کند.
        var first = SurplusPnlMath.NetProfit(
            SurplusPnlMath.Revenue(SurplusPnlMath.SoldSurplus(0.2m, 10m, 10.2m), 500m),
            SurplusPnlMath.AllocatableExpense(SurplusPnlMath.SoldSurplus(0.2m, 10m, 10.2m), 10.2m, 510m));
        var second = SurplusPnlMath.NetProfit(
            SurplusPnlMath.Revenue(SurplusPnlMath.SoldSurplus(0.2m, 10m, 10.2m), 500m),
            SurplusPnlMath.AllocatableExpense(SurplusPnlMath.SoldSurplus(0.2m, 10m, 10.2m), 10.2m, 510m));

        Assert.Equal(first, second);
        Assert.Equal(90m, first);
    }
}
