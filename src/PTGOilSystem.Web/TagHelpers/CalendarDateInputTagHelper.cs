using System.Globalization;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Razor.TagHelpers;
using PTGOilSystem.Web.Helpers;
using PTGOilSystem.Web.Services.Calendars;

namespace PTGOilSystem.Web.TagHelpers;

/// <summary>
/// لایهٔ سرورِ ورودیِ تاریخ برای تقویم هجری شمسی افغانستان.
///
/// input تاریخِ بومی میلادی است؛ مقدارش (ISO کانونیک) دست‌نخورده می‌ماند تا binding، اعتبارسنجی و
/// اسکریپت‌های موجود مثل قبل کار کنند. در حالت شمسی این TagHelper فقط معادلِ شمسیِ همان مقدار را
/// (محاسبه‌شده با <see cref="AfghanSolarCalendar"/>) در <c>data-solar-value</c> می‌گذارد؛
/// <c>ak-datepicker.js</c> فیلدِ قابل تایپِ شمسی و تقویمِ شمسی را از روی آن می‌سازد. در حالت میلادی
/// هیچ تغییری در خروجی نمی‌دهد.
/// </summary>
[HtmlTargetElement("input", Attributes = "asp-for")]
[HtmlTargetElement("input", Attributes = "type=date")]
public sealed class CalendarDateInputTagHelper : TagHelper
{
    /// <summary>بعد از InputTagHelper اجرا شود تا type و value تولیدشده در دسترس باشند.</summary>
    public override int Order => 1000;

    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(output);

        if (!DateDisplay.IsSolarHijri
            || !string.Equals(Read(output.Attributes["type"]?.Value), "date", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        output.Attributes.SetAttribute("data-calendar-input", "solar");

        var value = Read(output.Attributes["value"]?.Value);
        if (DateTime.TryParseExact(value, DateDisplay.HtmlDateInputPattern, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var date))
        {
            output.Attributes.SetAttribute("data-solar-value", AfghanSolarCalendar.FromGregorian(date).ToString());
        }
    }

    private static string? Read(object? value)
        => value switch
        {
            null => null,
            string text => text.Trim(),
            HtmlString html => html.Value?.Trim(),
            IHtmlContent content => Render(content),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim()
        };

    private static string Render(IHtmlContent content)
    {
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        content.WriteTo(writer, HtmlEncoder.Default);
        return writer.ToString().Trim();
    }
}
