using Microsoft.AspNetCore.Razor.TagHelpers;

namespace PTGOilSystem.Web.TagHelpers;

/// <summary>
/// Development-only source markers for the browser element picker.
/// Wraps every <c>&lt;partial /&gt;</c> render in HTML comments naming the
/// partial, so a picked element in the browser can be traced back to the exact
/// partial view that produced it.
///
/// Emits HTML comments only — no attributes, no markup changes — and is
/// completely inert unless the environment is Development AND
/// <c>PTG_UI_PICK=1</c>.
/// </summary>
[HtmlTargetElement("partial", Attributes = "name")]
public sealed class UiPickPartialMarkerTagHelper : TagHelper
{
    private static bool? _enabled;

    private readonly IWebHostEnvironment _environment;
    private readonly IConfiguration _configuration;

    public UiPickPartialMarkerTagHelper(IWebHostEnvironment environment, IConfiguration configuration)
    {
        _environment = environment;
        _configuration = configuration;
    }

    /// <summary>Runs after <c>PartialTagHelper</c> so the rendered body is already set.</summary>
    public override int Order => 5000;

    [HtmlAttributeName("name")]
    public string? Name { get; set; }

    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        if (!IsEnabled() || string.IsNullOrWhiteSpace(Name))
        {
            return;
        }

        var safeName = Name.Replace("-->", string.Empty, StringComparison.Ordinal);
        output.PreElement.AppendHtml($"<!--ptg-partial-begin:{safeName}-->");
        output.PostElement.AppendHtml($"<!--ptg-partial-end:{safeName}-->");
    }

    private bool IsEnabled()
    {
        // The flag cannot change while the process runs, so resolve it once.
        _enabled ??= _environment.IsDevelopment()
            && string.Equals(_configuration["PTG_UI_PICK"], "1", StringComparison.Ordinal);

        return _enabled.Value;
    }
}
