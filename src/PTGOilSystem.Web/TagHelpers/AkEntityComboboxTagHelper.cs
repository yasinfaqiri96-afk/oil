using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;
using PTGOilSystem.Web.Services.QuickCreate;

namespace PTGOilSystem.Web.TagHelpers;

/// <summary>
/// Marks entity-backed selects for the single shared searchable combobox.
/// The real select remains the binding and event source; the client-side
/// component is progressive enhancement only.
/// </summary>
[HtmlTargetElement("select", Attributes = "asp-for")]
[HtmlTargetElement("select", Attributes = "data-ak-entity-combobox")]
public sealed class AkEntityComboboxTagHelper : TagHelper
{
    [HtmlAttributeName("asp-for")]
    public ModelExpression? For { get; set; }

    public override int Order => 1000;

    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        var explicitMode = context.AllAttributes["data-ak-entity-combobox"]?.Value?.ToString();
        if (string.Equals(explicitMode, "false", StringComparison.OrdinalIgnoreCase)
            || string.Equals(explicitMode, "off", StringComparison.OrdinalIgnoreCase))
        {
            output.Attributes.RemoveAll("data-ak-entity-combobox");
            return;
        }

        var fieldName = For?.Name
            ?? output.Attributes["name"]?.Value?.ToString()
            ?? context.AllAttributes["name"]?.Value?.ToString();

        if (string.IsNullOrWhiteSpace(fieldName))
        {
            return;
        }

        var normalized = Normalize(fieldName);
        var entity = QuickCreateEntityRegistry.Entities
            .FirstOrDefault(candidate => IsEntityField(normalized, candidate.Key));
        if (entity is null)
        {
            return;
        }

        output.Attributes.SetAttribute("data-ak-entity-combobox", "true");
        SetIfMissing(output, "data-ak-placeholder", "برای جستجو بنویسید...");
        SetIfMissing(output, "data-ak-empty", "موردی پیدا نشد");
        SetIfMissing(output, "data-ak-quick-create-label", entity.CreateLabel);
        SetIfMissing(output, "data-ak-quick-create-url", $"/{entity.Controller}/Create");
        if (string.Equals(entity.EntityTypeName, "Currency", StringComparison.Ordinal)
            && !normalized.EndsWith("Id", StringComparison.OrdinalIgnoreCase))
        {
            SetIfMissing(output, "data-ak-quick-create-value-field", "code");
        }
    }

    private static string Normalize(string fieldName)
    {
        var segment = fieldName.Split('.').Last();
        var bracket = segment.LastIndexOf(']');
        return bracket >= 0 && bracket + 1 < segment.Length
            ? segment[(bracket + 1)..]
            : segment;
    }

    private static bool IsEntityField(string fieldName, string key)
    {
        if (fieldName.Equals(key, StringComparison.OrdinalIgnoreCase)
            || fieldName.Equals($"{key}Id", StringComparison.OrdinalIgnoreCase)
            || fieldName.EndsWith($"{key}Id", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return key == "Currency"
            && (fieldName.EndsWith("Currency", StringComparison.OrdinalIgnoreCase)
                || fieldName.EndsWith("CurrencyCode", StringComparison.OrdinalIgnoreCase));
    }

    private static void SetIfMissing(TagHelperOutput output, string name, string value)
    {
        if (!output.Attributes.ContainsName(name))
        {
            output.Attributes.SetAttribute(name, value);
        }
    }
}
