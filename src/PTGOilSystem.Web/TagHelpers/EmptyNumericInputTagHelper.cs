using System.Globalization;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace PTGOilSystem.Web.TagHelpers;

/// <summary>
/// در فرم‌های «ثبت جدید»، فیلد عددی نباید صفرِ پیش‌فرض را نشان بدهد؛ کاربر باید مستقیم عدد
/// خودش را تایپ کند، نه اینکه اول صفر را پاک کند.
///
/// این TagHelper بعد از <c>InputTagHelper</c> اجرا می‌شود و فقط attribute مقدار را حذف می‌کند
/// (input واقعاً خالی می‌شود؛ مخفی‌سازی با CSS نیست). هیچ چیز دیگری تغییر نمی‌کند:
/// min/max/step/required/asp-for/validation و format دست‌نخورده می‌مانند.
///
/// عمداً دست نمی‌زند به:
/// <list type="bullet">
/// <item>صفحات Edit/Details — صفرِ واقعی رکورد باید دیده شود.</item>
/// <item>input های hidden — حامل داده و منطق کاری‌اند.</item>
/// <item>input های readonly/disabled — خروجی محاسبه‌اند، نه ورودی کاربر.</item>
/// <item>حالتی که فرم با خطای validation برگشته و کاربر خودش صفر را وارد کرده است.</item>
/// <item>هر input با <c>data-keep-zero="true"</c> — فرار اضطراری برای فیلدی که عمداً صفر می‌خواهد.</item>
/// </list>
/// </summary>
[HtmlTargetElement("input", Attributes = "asp-for")]
[HtmlTargetElement("input", Attributes = "type=number")]
public sealed class EmptyNumericInputTagHelper : TagHelper
{
    private static readonly HashSet<Type> NumericTypes =
    [
        typeof(decimal), typeof(double), typeof(float),
        typeof(int), typeof(long), typeof(short),
        typeof(byte), typeof(sbyte),
        typeof(uint), typeof(ulong), typeof(ushort)
    ];

    [HtmlAttributeName("asp-for")]
    public ModelExpression? For { get; set; }

    [HtmlAttributeNotBound]
    [ViewContext]
    public ViewContext ViewContext { get; set; } = default!;

    /// <summary>بعد از InputTagHelper (Order صفر) اجرا شود تا مقدار تولیدشده در دسترس باشد.</summary>
    public override int Order => 1000;

    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(output);

        if (!IsCreateForm())
        {
            return;
        }

        if (IsTrue(Read(context.AllAttributes["data-keep-zero"]?.Value)))
        {
            return;
        }

        var inputType = Read(output.Attributes["type"]?.Value)
            ?? Read(context.AllAttributes["type"]?.Value);

        if (string.Equals(inputType, "hidden", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (output.Attributes.ContainsName("readonly") || output.Attributes.ContainsName("disabled"))
        {
            return;
        }

        var isNumberInput = string.Equals(inputType, "number", StringComparison.OrdinalIgnoreCase);
        var isNumericModel = For is not null && NumericTypes.Contains(For.Metadata.UnderlyingOrModelType);
        if (!isNumberInput && !isNumericModel)
        {
            return;
        }

        var rendered = Read(output.Attributes["value"]?.Value);
        if (string.IsNullOrWhiteSpace(rendered) || !IsZero(rendered))
        {
            return;
        }

        // فرم پس از خطای validation برگشته و کاربر خودش صفر را نوشته: مقدارش حفظ شود.
        var fieldName = For?.Name
            ?? Read(output.Attributes["name"]?.Value)
            ?? Read(context.AllAttributes["name"]?.Value);
        if (!string.IsNullOrEmpty(fieldName)
            && ViewContext.ViewData.ModelState.TryGetValue(fieldName, out var entry)
            && entry.RawValue is not null)
        {
            return;
        }

        output.Attributes.RemoveAll("value");
    }

    private bool IsCreateForm()
    {
        var action = ViewContext?.RouteData.Values["action"]?.ToString();
        return action is not null && action.Contains("Create", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsZero(string text)
        => (decimal.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var value)
            || decimal.TryParse(text, NumberStyles.Any, CultureInfo.CurrentCulture, out value))
           && value == 0m;

    private static bool IsTrue(string? text)
        => string.Equals(text, "true", StringComparison.OrdinalIgnoreCase);

    private static string? Read(object? value)
    {
        switch (value)
        {
            case null:
                return null;
            case string text:
                return text;
            case IHtmlContent html:
            {
                using var writer = new StringWriter();
                html.WriteTo(writer, HtmlEncoder.Default);
                return writer.ToString();
            }
            default:
                return value.ToString();
        }
    }
}
