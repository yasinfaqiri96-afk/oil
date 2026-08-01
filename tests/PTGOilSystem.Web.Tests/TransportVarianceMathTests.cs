using PTGOilSystem.Web.Services;
using Xunit;

namespace PTGOilSystem.Web.Tests;

// قرارداد علامتِ «کسری و اضافه‌بار حمل» را قفل می‌کند:
// تفاوت = بارگیری − تخلیه ⇒ مثبت = کسری، منفی = اضافه‌بار، و اضافه‌بار هرگز قابل جریمه نیست.
public class TransportVarianceMathTests
{
    [Theory]
    [InlineData(10, 9.8, 0.2)]      // کسری
    [InlineData(10, 10.2, -0.2)]    // اضافه‌بار
    [InlineData(10, 10, 0)]         // بدون تفاوت
    public void Difference_Keeps_The_Real_Sign(decimal loaded, decimal discharged, decimal expected)
    {
        Assert.Equal(expected, TransportVarianceMath.Difference(loaded, discharged));
    }

    [Fact]
    public void Difference_Rounds_To_Four_Decimals_AwayFromZero()
    {
        Assert.Equal(0.2346m, TransportVarianceMath.Difference(10.23455m, 10m));
        Assert.Equal(-0.2346m, TransportVarianceMath.Difference(10m, 10.23455m));
    }

    [Theory]
    [InlineData(0.2, 0.2, 0)]
    [InlineData(-0.2, 0, 0.2)]
    [InlineData(0, 0, 0)]
    public void Shortage_And_Surplus_Never_Go_Negative(decimal difference, decimal expectedShortage, decimal expectedSurplus)
    {
        Assert.Equal(expectedShortage, TransportVarianceMath.Shortage(difference));
        Assert.Equal(expectedSurplus, TransportVarianceMath.Surplus(difference));
    }

    [Theory]
    [InlineData(1.5, 0.5, 1.0)]     // کسری بیشتر از تلورانس ⇒ قابل جریمه
    [InlineData(0.3, 0.5, 0)]       // کسری داخل تلورانس ⇒ صفر
    [InlineData(-0.2, 0, 0)]        // اضافه‌بار ⇒ هرگز قابل جریمه نیست
    [InlineData(-2, 0.5, 0)]        // اضافه‌بار بزرگ ⇒ باز هم صفر
    public void ChargeableShortage_Is_Built_Only_From_The_Shortage_Side(decimal difference, decimal allowance, decimal expected)
    {
        Assert.Equal(expected, TransportVarianceMath.ChargeableShortage(difference, allowance));
    }

    [Theory]
    [InlineData(1.5, 0.5, 0.5)]     // تلورانس کامل مصرف می‌شود
    [InlineData(0.3, 0.5, 0.3)]     // مجاز هرگز از خودِ کسری بیشتر نمی‌شود
    [InlineData(-0.2, 0.5, 0)]      // اضافه‌بار مجازِ کسری ندارد
    public void AllowableShortage_Never_Exceeds_The_Shortage(decimal difference, decimal allowance, decimal expected)
    {
        Assert.Equal(expected, TransportVarianceMath.AllowableShortage(difference, allowance));
    }

    [Theory]
    [InlineData(0.2, true, false)]
    [InlineData(-0.2, false, true)]
    [InlineData(0, false, false)]
    [InlineData(0.00001, false, false)]  // زیر آستانهٔ دقتِ وزن ⇒ «بدون تفاوت»
    public void Variance_Classification_Uses_The_Storage_Epsilon(decimal difference, bool isShortage, bool isSurplus)
    {
        Assert.Equal(isShortage, TransportVarianceMath.IsShortage(difference));
        Assert.Equal(isSurplus, TransportVarianceMath.IsSurplus(difference));
        Assert.Equal(isShortage || isSurplus, TransportVarianceMath.HasVariance(difference));
    }

    [Theory]
    [InlineData(0.2, 10, 2)]
    [InlineData(-0.2, 10, -2)]
    [InlineData(0.2, 0, 0)]         // بدون بارگیری ⇒ فیصدی صفر (بدون تقسیم بر صفر)
    public void DifferencePercent_Is_Signed_And_Safe(decimal difference, decimal loaded, decimal expected)
    {
        Assert.Equal(expected, TransportVarianceMath.DifferencePercent(difference, loaded));
    }
}
