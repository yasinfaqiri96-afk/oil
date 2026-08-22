using PTGOilSystem.Web.Models.Entities;

namespace PTGOilSystem.Web.Models.LossEvents;

/// <summary>
/// تنها مرجع تصمیمِ «کدام مرحلهٔ کسری موجودی را کم می‌کند».
///
/// قاعده یکی است و از قبل در سیستم برقرار بوده: موجودی فقط وقتی دوباره کم می‌شود که مال هنوز
/// در دفتر داخل مخزن باشد. کسری بارگیری، مسیر، رسید، دیسپچ، گمرک و فروش را سند اصلیِ همان
/// مرحله از قبل درست جابه‌جا کرده (ورودی رسید فقط به اندازهٔ تحویل‌گرفته‌شده ثبت می‌شود و
/// خروجی دیسپچ کل بار را می‌برد)، پس رویداد کسری برای آن‌ها فقط سند ردیابی و مسئولیت است.
/// </summary>
public static class LossStagePolicy
{
    /// <summary>آیا این مرحله باید یک حرکت خروجی موجودی بسازد.</summary>
    public static bool AffectsInventory(LossEventStage stage)
        => stage is LossEventStage.TankNaturalLoss
            or LossEventStage.ManualAdjustment
            or LossEventStage.TankFinalSettlement;

    /// <summary>آیا برای این مرحله انتخاب مخزن الزامی است (ترمینال از خود مخزن مشتق می‌شود).</summary>
    public static bool RequiresTankScope(LossEventStage stage)
        => AffectsInventory(stage);

    /// <summary>
    /// تسویهٔ نهایی مخزن فقط از مسیر «تسویه مخزن» ساخته می‌شود، نه از فرم دستی ضایعات؛
    /// همان محدودیتی که پیش‌تر در فهرست مرحله‌های فرم اعمال می‌شد.
    /// </summary>
    public static bool AllowedInManualForm(LossEventStage stage)
        => stage != LossEventStage.TankFinalSettlement;
}
