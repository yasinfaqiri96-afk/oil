namespace PTGOilSystem.Web.Services;

// منطق مشترک «کسری و اضافه‌بار حمل» برای هر دو مسیر تخلیه (دیسپچ موتر و رسید انتقال داخلی).
// قرارداد علامت در کل سیستم یکسان است:
//   تفاوت = مقدار بارگیری − مقدار تخلیه
//   تفاوت مثبت  ⇒ کسری   (۱۰ بارگیری، ۹٫۸ تخلیه ⇒ ‎+0.2‎)
//   تفاوت منفی  ⇒ اضافه‌بار (۱۰ بارگیری، ۱۰٫۲ تخلیه ⇒ ‎−0.2‎)
// اضافه‌بار هرگز قابل جریمه نیست؛ فقط کسریِ بیشتر از تلورانس جریمه می‌گیرد.
// این کلاس pure است: چیزی نمی‌خواند و چیزی ذخیره نمی‌کند.
public static class TransportVarianceMath
{
    // آستانهٔ صفرِ عملی. دقت ذخیره‌سازی وزن numeric(18,4) است، پس اختلافِ کوچک‌تر از نصفِ
    // آخرین رقم «بدون تفاوت» شمرده می‌شود و رکورد ردیابی نمی‌سازد.
    public const decimal Epsilon = 0.00005m;

    // تفاوت واقعی و علامت‌دار، گردشده به ۴ رقم (AwayFromZero) مثل بقیهٔ محاسبات وزنی.
    public static decimal Difference(decimal loadedQuantityMt, decimal dischargedQuantityMt)
        => decimal.Round(loadedQuantityMt - dischargedQuantityMt, 4, MidpointRounding.AwayFromZero);

    // بخش کسریِ تفاوت (هرگز منفی نمی‌شود).
    public static decimal Shortage(decimal differenceMt)
        => Math.Max(differenceMt, 0m);

    // بخش اضافه‌بارِ تفاوت به‌صورت عدد مثبت (هرگز منفی نمی‌شود).
    public static decimal Surplus(decimal differenceMt)
        => Math.Max(-differenceMt, 0m);

    public static bool IsSurplus(decimal differenceMt) => differenceMt <= -Epsilon;

    public static bool IsShortage(decimal differenceMt) => differenceMt >= Epsilon;

    // هر تفاوت غیر صفر — چه کسری چه اضافه‌بار — باید رکورد قابل ردیابی بسازد.
    public static bool HasVariance(decimal differenceMt) => Math.Abs(differenceMt) >= Epsilon;

    // کسری قابل مجرا: فقط از بخش کسری ساخته می‌شود، پس اضافه‌بار همیشه صفر می‌دهد.
    public static decimal ChargeableShortage(decimal differenceMt, decimal allowanceMt)
        => FreightShortageMath.ChargeableShortage(Shortage(differenceMt), Math.Max(allowanceMt, 0m));

    // کسری مجاز (داخل تلورانس): هیچ‌وقت از خودِ کسری بیشتر نمی‌شود و برای اضافه‌بار صفر است.
    public static decimal AllowableShortage(decimal differenceMt, decimal allowanceMt)
        => Math.Min(Shortage(differenceMt), Math.Max(allowanceMt, 0m));

    // فیصدی تفاوت نسبت به مقدار بارگیری (علامت‌دار، مثل خود تفاوت).
    public static decimal DifferencePercent(decimal differenceMt, decimal loadedQuantityMt)
        => loadedQuantityMt <= 0m
            ? 0m
            : decimal.Round(differenceMt / loadedQuantityMt * 100m, 2, MidpointRounding.AwayFromZero);
}
