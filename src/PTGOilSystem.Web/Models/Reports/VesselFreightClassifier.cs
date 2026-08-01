namespace PTGOilSystem.Web.Models.Reports;

/// <summary>
/// تنها محلِ تشخیصِ «کرایهٔ کشتی» برای گزارش گشتی‌ها (سفرهای کشتی).
///
/// چرا جدا از <see cref="ShipmentPnl.ShipmentExpenseCategorizer"/>: آن دسته‌بندی «کرایه» را
/// به معنی کلِّ حمل می‌گیرد — خط‌آهن، کرایهٔ مخزن، موتر و کشتی همه <c>Category=Transport</c>
/// هستند و همگی در یک دسته می‌افتند. گزارش گشتی‌ها فقط کرایهٔ خودِ کشتی را می‌خواهد، پس
/// اینجا قاعدهٔ باریک‌تری دارد. دسته‌بندیِ پروندهٔ محموله و سود و زیان دست‌نخورده می‌ماند.
///
/// قاعده:
///  • فارسی: متن باید هم «کرایه» داشته باشد هم «کشتی».
///  • انگلیسی: یکی از عبارت‌های vessel/sea/ocean/marine freight.
///  • «دیمرج» جریمهٔ معطلیِ کشتی است، نه کرایه — همیشه کنار گذاشته می‌شود.
///
/// نامِ نوع مصرف و شرحِ سند هر دو بررسی می‌شوند تا نوع‌های دستیِ آینده هم بدون تغییر کد
/// شناخته شوند. هیچ مبلغی اینجا محاسبه نمی‌شود؛ فقط «هست یا نیست».
/// </summary>
public static class VesselFreightClassifier
{
    // معطلی کشتی — نه کرایه. هر دو املای رایج فارسی و معادل انگلیسی.
    private static readonly string[] DemurrageTerms = ["دیمرج", "دیمیرج", "demurrage"];

    private static readonly string[] VesselTerms = ["کشتی"];
    private static readonly string[] FreightTerms = ["کرایه"];

    // در انگلیسی خودِ عبارت کامل بررسی می‌شود؛ «ship» به‌تنهایی چون داخل «shipment»
    // هم هست به‌عمد کنار گذاشته شده تا مصارف عمومیِ محموله اشتباهی وارد نشوند.
    private static readonly string[] EnglishVesselFreightPhrases =
        ["vessel freight", "sea freight", "ocean freight", "marine freight"];

    public static bool IsVesselFreight(string? expenseTypeName, string? description)
    {
        var text = Normalize($"{expenseTypeName} {description}");
        if (text.Length == 0)
        {
            return false;
        }

        if (ContainsAny(text, DemurrageTerms))
        {
            return false;
        }

        if (ContainsAny(text, EnglishVesselFreightPhrases))
        {
            return true;
        }

        return ContainsAny(text, VesselTerms) && ContainsAny(text, FreightTerms);
    }

    /// <summary>
    /// حروف عربیِ «ي» و «ك» در دادهٔ واقعی با معادل فارسیِ «ی» و «ک» مخلوط‌اند؛ بدون
    /// یکسان‌سازی، «کشتي» با «کشتی» برابر شمرده نمی‌شود.
    /// </summary>
    private static string Normalize(string value)
        => value
            .Replace('ي', 'ی')
            .Replace('ك', 'ک')
            .ToLowerInvariant()
            .Trim();

    private static bool ContainsAny(string text, string[] terms)
        => terms.Any(term => text.Contains(term, StringComparison.Ordinal));
}
