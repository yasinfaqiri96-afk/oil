using Microsoft.AspNetCore.Html;
using System.Text.Encodings.Web;

namespace PTGOilSystem.Web.Helpers;

/// <summary>
/// نمایش «عدد + واحد» به‌صورت کپسول رنگیِ کامپوننت مشترک (فقط نمایش HTML).
/// خودِ عدد از <see cref="NumberDisplay"/> می‌آید (هیچ محاسبه‌ای اینجا نیست)؛
/// فقط واحدِ پول (USD/RUB…) یا واحدِ محصول (MT) داخل یک کپسول رنگی می‌نشیند
/// تا در سطرهای پرعدد، واحدها از خودِ رقم جدا و خوانا شوند.
/// در جاهایی که HTML رندر نمی‌شود (اکسپورت/چاپ متنی) همچنان از NumberDisplay استفاده کنید.
/// </summary>
public static class AkUnit
{
    public static IHtmlContent Money(decimal value, string unit)
        => Wrap(NumberDisplay.Money(value), unit);

    public static IHtmlContent Money(decimal? value, string unit, string empty = NumberDisplay.EmptyValue)
        => value.HasValue ? Money(value.Value, unit) : Plain(empty);

    public static IHtmlContent Quantity(decimal value, string unit)
        => Wrap(NumberDisplay.Quantity(value), unit);

    public static IHtmlContent Quantity(decimal? value, string unit, string empty = NumberDisplay.EmptyValue)
        => value.HasValue ? Quantity(value.Value, unit) : Plain(empty);

    public static IHtmlContent UnitPrice(decimal value, string unit)
        => Wrap(NumberDisplay.UnitPrice(value), unit);

    public static IHtmlContent UnitPrice(decimal? value, string unit, string empty = NumberDisplay.EmptyValue)
        => value.HasValue ? UnitPrice(value.Value, unit) : Plain(empty);

    public static IHtmlContent FxRate(decimal value, string unit)
        => Wrap(NumberDisplay.FxRate(value), unit);

    public static IHtmlContent FxRate(decimal? value, string unit, string empty = NumberDisplay.EmptyValue)
        => value.HasValue ? FxRate(value.Value, unit) : Plain(empty);

    private static IHtmlContent Plain(string text)
        => new HtmlString(HtmlEncoder.Default.Encode(text));

    private static IHtmlContent Wrap(string numberText, string? unit)
    {
        var num = HtmlEncoder.Default.Encode(numberText);
        if (string.IsNullOrWhiteSpace(unit))
        {
            return new HtmlString(num);
        }

        var u = unit.Trim();
        var enc = HtmlEncoder.Default.Encode(u);
        return new HtmlString(
            $"<span class=\"ak-amount\">{num}<span class=\"ak-unit ak-unit--{Modifier(u)}\">{enc}</span></span>");
    }

    // تعیین رنگ کپسول از روی واحد — پول‌ها و واحد محصول رنگ متمایز می‌گیرند، بقیه خنثی.
    private static string Modifier(string unit) => unit.ToUpperInvariant() switch
    {
        "USD" => "usd",
        "RUB" => "rub",
        "MT" => "mt",
        _ => "muted",
    };
}
