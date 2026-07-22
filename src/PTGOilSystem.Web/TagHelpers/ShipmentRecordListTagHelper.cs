using Microsoft.AspNetCore.Razor.TagHelpers;

namespace PTGOilSystem.Web.TagHelpers;

/// <summary>
/// Shell state shared between <c>&lt;shipment-record-list&gt;</c> and its slots.
/// The parent creates it before reading child content; each slot writes its own
/// rendered fragment into it and suppresses its own output.
/// </summary>
public sealed class ShipmentRecordListSlots
{
    public string? Action { get; set; }
    public string? Meta { get; set; }
    public string? Filter { get; set; }
}

/// <summary>
/// The single card/toolbar/scroll shell for every record table inside the
/// shipment file page (پرونده محموله). Tabs supply only their own table body;
/// geometry, toolbar and scroll behaviour live here so no tab can drift.
/// Rendered as a plain section, or as a native <c>&lt;details&gt;</c> accordion
/// when <c>collapsible</c> is set (expense categories keep that behaviour).
/// </summary>
[HtmlTargetElement("shipment-record-list", Attributes = "title")]
public sealed class ShipmentRecordListTagHelper : TagHelper
{
    /// <summary>Visible list title (already localised by the caller).</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Record count shown in the dark badge next to the title.</summary>
    public int? Count { get; set; }

    /// <summary>Render as an accordion instead of a static card.</summary>
    public bool Collapsible { get; set; }

    /// <summary>Initial accordion state; ignored when not collapsible.</summary>
    public bool Open { get; set; }

    /// <summary>Optional icon (bootstrap-icons class) shown before the title.</summary>
    public string? Icon { get; set; }

    public override async Task ProcessAsync(TagHelperContext context, TagHelperOutput output)
    {
        var slots = new ShipmentRecordListSlots();
        context.Items[typeof(ShipmentRecordListSlots)] = slots;

        var body = (await output.GetChildContentAsync()).GetContent();

        output.TagName = Collapsible ? "details" : "section";
        output.TagMode = TagMode.StartTagAndEndTag;
        output.Attributes.SetAttribute("class", "shipment-record-list");
        if (Collapsible && Open)
        {
            output.Attributes.SetAttribute("open", "open");
        }

        var toolbarTag = Collapsible ? "summary" : "div";
        output.Content.SetHtmlContent(
            $"""
             <{toolbarTag} class="shipment-record-list__toolbar">
               <span class="shipment-record-list__titlegroup">
                 {(string.IsNullOrWhiteSpace(Icon) ? string.Empty : $"""<i class="bi {Icon} shipment-record-list__icon" aria-hidden="true"></i>""")}
                 <span class="shipment-record-list__title">{System.Net.WebUtility.HtmlEncode(Title)}</span>
                 {(Count.HasValue ? $"""<span class="shipment-record-list__count">{Count.Value.ToString("N0")}</span>""" : string.Empty)}
               </span>
               <span class="shipment-record-list__tools">
                 {slots.Meta}
                 {slots.Action}
                 {(Collapsible ? """<i class="bi bi-chevron-down shipment-record-list__chevron" aria-hidden="true"></i>""" : string.Empty)}
               </span>
             </{toolbarTag}>
             {(string.IsNullOrWhiteSpace(slots.Filter) ? string.Empty : $"""<div class="shipment-record-list__filter">{slots.Filter}</div>""")}
             <div class="shipment-record-list__scroll ak-table-wrap">{body}</div>
             """);
    }
}

/// <summary>Primary action slot — moved into the list toolbar (start side).</summary>
[HtmlTargetElement("shipment-record-action", ParentTag = "shipment-record-list")]
public sealed class ShipmentRecordActionTagHelper : TagHelper
{
    public override async Task ProcessAsync(TagHelperContext context, TagHelperOutput output)
    {
        if (context.Items.TryGetValue(typeof(ShipmentRecordListSlots), out var raw) && raw is ShipmentRecordListSlots slots)
        {
            slots.Action = (await output.GetChildContentAsync()).GetContent();
        }

        output.SuppressOutput();
    }
}

/// <summary>Quiet meta slot (totals, record counts) shown before the action.</summary>
[HtmlTargetElement("shipment-record-meta", ParentTag = "shipment-record-list")]
public sealed class ShipmentRecordMetaTagHelper : TagHelper
{
    public override async Task ProcessAsync(TagHelperContext context, TagHelperOutput output)
    {
        if (context.Items.TryGetValue(typeof(ShipmentRecordListSlots), out var raw) && raw is ShipmentRecordListSlots slots)
        {
            slots.Meta = (await output.GetChildContentAsync()).GetContent();
        }

        output.SuppressOutput();
    }
}

/// <summary>Filter row rendered between the toolbar and the table.</summary>
[HtmlTargetElement("shipment-record-filter", ParentTag = "shipment-record-list")]
public sealed class ShipmentRecordFilterTagHelper : TagHelper
{
    public override async Task ProcessAsync(TagHelperContext context, TagHelperOutput output)
    {
        if (context.Items.TryGetValue(typeof(ShipmentRecordListSlots), out var raw) && raw is ShipmentRecordListSlots slots)
        {
            slots.Filter = (await output.GetChildContentAsync()).GetContent();
        }

        output.SuppressOutput();
    }
}
