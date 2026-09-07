using PTGOilSystem.Web.Models.Entities;

namespace PTGOilSystem.Web.Services.Customs;

/// <summary>
/// دو جنسِ متفاوتی که یک اظهارنامهٔ گمرکی در خود دارد.
///
/// این تفکیک سلیقه‌ای نیست؛ از واقعیتِ پرداخت می‌آید: مال تا حقوقِ دولتی پرداخت نشود از
/// گمرک حرکت نمی‌کند، پس آن بخش تقریباً همیشه همان‌جا نقد می‌شود و با دولت حساب جاری
/// نمی‌ماند. در مقابل، کمیشنکار و خدماتِ مشابه اشخاصِ واقعی‌اند که پول را از جیب خود
/// می‌دهند و بعد با شرکت تصفیه می‌کنند — یعنی بدهیِ واقعی به یک طرف‌حسابِ واقعی.
/// </summary>
public enum CustomsComponentGroup
{
    /// <summary>حقوق و محصولاتِ دولتی. طرف‌حسابِ ماندگار ندارد.</summary>
    GovernmentDuty = 1,

    /// <summary>کمیشن و خدماتِ اشخاص ثالث — کمیشنکار، بانک، ترازو، بارچلانی…</summary>
    ThirdPartyService = 2
}

/// <summary>
/// تنها مالکِ «هر جزءِ اظهارنامه از کدام جنس است».
///
/// هم‌خانوادهٔ <see cref="Parties.PartyTypeMap"/>: برای مقدارِ ناشناخته استثنا پرتاب
/// می‌کند و چیزی را خاموش در یک گروهِ پیش‌فرض نمی‌اندازد. جزءِ تازه‌ای که به
/// <see cref="CustomsComponentType"/> اضافه شود، اینجا هم باید تصمیمش گرفته شود — وگرنه
/// همان لحظه سر و صدا می‌کند، نه اینکه ماه‌ها در یک گروهِ غلط پول جابه‌جا کند.
/// </summary>
public static class CustomsComponentGroupMap
{
    public static CustomsComponentGroup Resolve(CustomsComponentType componentType) => componentType switch
    {
        // ——— حقوقِ دولتی: در گمرک و به‌نامِ دولت پرداخت می‌شود.
        CustomsComponentType.Mahsooli => CustomsComponentGroup.GovernmentDuty,
        CustomsComponentType.MahsooliDolari => CustomsComponentGroup.GovernmentDuty,
        CustomsComponentType.FawaidAama => CustomsComponentGroup.GovernmentDuty,
        CustomsComponentType.KomisionTarifa => CustomsComponentGroup.GovernmentDuty,
        CustomsComponentType.NormStandard => CustomsComponentGroup.GovernmentDuty,
        CustomsComponentType.GomrokSarhadi => CustomsComponentGroup.GovernmentDuty,
        CustomsComponentType.ElmKhabar => CustomsComponentGroup.GovernmentDuty,
        CustomsComponentType.KhatAhan => CustomsComponentGroup.GovernmentDuty,
        CustomsComponentType.GasMasbut => CustomsComponentGroup.GovernmentDuty,
        CustomsComponentType.Yozbulagh => CustomsComponentGroup.GovernmentDuty,
        CustomsComponentType.Masraf20Pul => CustomsComponentGroup.GovernmentDuty,

        // ——— خدماتِ اشخاص ثالث: صورت‌حسابِ یک شخص/شرکتِ مشخص است.
        CustomsComponentType.Komisionkar => CustomsComponentGroup.ThirdPartyService,
        CustomsComponentType.KomisionBarchalani => CustomsComponentGroup.ThirdPartyService,
        CustomsComponentType.KomisionBank => CustomsComponentGroup.ThirdPartyService,
        CustomsComponentType.HaqKhidma => CustomsComponentGroup.ThirdPartyService,
        CustomsComponentType.BarnamaWagon => CustomsComponentGroup.ThirdPartyService,
        CustomsComponentType.TarazuMotor => CustomsComponentGroup.ThirdPartyService,

        // «متفرقه» و «سایر» عمداً دولتی شمرده می‌شوند: پیش‌فرضِ محافظه‌کارانه این است که
        // بدهیِ کسی ساخته نشود. اگر ردیفی واقعاً صورت‌حسابِ یک طرف‌حساب است، کاربر همان
        // را در فرم به گروهِ خدمات می‌برد.
        CustomsComponentType.Mutafarraka => CustomsComponentGroup.GovernmentDuty,
        CustomsComponentType.Other => CustomsComponentGroup.GovernmentDuty,

        _ => throw new ArgumentOutOfRangeException(
            nameof(componentType),
            componentType,
            "Unknown customs component type; classify it in CustomsComponentGroupMap instead of letting it fall into a default group.")
    };

    public static bool IsGovernmentDuty(CustomsComponentType componentType)
        => Resolve(componentType) == CustomsComponentGroup.GovernmentDuty;

    public static bool IsThirdPartyService(CustomsComponentType componentType)
        => Resolve(componentType) == CustomsComponentGroup.ThirdPartyService;

    public static string Label(CustomsComponentGroup group) => group switch
    {
        CustomsComponentGroup.GovernmentDuty => "حقوق دولتی",
        CustomsComponentGroup.ThirdPartyService => "کمیشن و خدمات",
        _ => throw new ArgumentOutOfRangeException(nameof(group), group, null)
    };
}
