using System;
using System.Collections.Generic;
using System.Linq;

namespace PTGOilSystem.Web.Models;

/// <summary>
/// View model for the shared Akaunting-style search/filter component
/// (<c>_AkSearchFilter.cshtml</c> + <c>ak-search-filter.js</c>).
///
/// Presentation-only: it describes how to render the search box and the
/// structured filter chips/popover. Each filter maps 1:1 to the existing
/// server query-string parameters — the component swaps the chrome only,
/// the produced GET request is identical to the legacy filter bar.
/// </summary>
public sealed record AkSearchFilterModel(
    string SearchName,
    string? SearchValue,
    string Placeholder,
    IReadOnlyList<AkFilterDefinition>? Filters = null,
    IReadOnlyDictionary<string, string>? Hidden = null,
    // مقصد submit. پیش‌فرض null یعنی همان نشانی فعلی (رفتار قبلی همهٔ صفحه‌ها).
    // فقط جایی لازم است که نوار فیلتر داخل صفحهٔ دیگری embed شده باشد.
    string? FormAction = null)
{
    /// <summary>Search-only convenience (group A / drop-in for the old bar).</summary>
    public static AkSearchFilterModel SearchOnly(string name, string? value, string placeholder)
        => new(name, value, placeholder);
}

/// <summary>A single structured filter, bound to one (or, for a range, two) query params.</summary>
public sealed record AkFilterDefinition(
    string Key,
    string Label,
    string Type,                                   // "select" | "bool" | "text" | "date" | "daterange"
    IReadOnlyList<AkFilterOption>? Options = null, // for select/bool
    string? Value = null,                          // current applied value (select/bool/date/daterange-from)
    string? SecondKey = null,                      // daterange: the "to" param name
    string? SecondValue = null,                    // daterange: current "to" value
    // چندانتخابی: همان Key چند بار در query تکرار می‌شود (key=1&key=2)؛ منطق
    // بین مقادیرِ یک فیلتر OR است و بین فیلترهای مختلف همچنان AND.
    bool Multiple = false,
    IReadOnlyList<string>? Values = null)          // multiple: all applied values
{
    /// <summary>All applied values, whether the filter is single- or multi-valued.</summary>
    public IReadOnlyList<string> AppliedValues
        => Multiple
            ? (Values ?? Array.Empty<string>()).Where(v => !string.IsNullOrEmpty(v)).ToList()
            : string.IsNullOrEmpty(Value) ? Array.Empty<string>() : new[] { Value };

    /// <summary>
    /// Multi-select filter bound to <paramref name="key"/>, repeated once per applied value.
    /// <paramref name="selected"/> takes ints/enums/strings straight from the controller model.
    /// </summary>
    public static AkFilterDefinition MultiSelect(
        string key,
        string label,
        IReadOnlyList<AkFilterOption> options,
        System.Collections.IEnumerable? selected = null,
        string type = "select")
        => new(key, label, type, options, Value: null, SecondKey: null, SecondValue: null,
               Multiple: true, Values: Normalize(selected));

    internal static IReadOnlyList<string> Normalize(System.Collections.IEnumerable? selected)
    {
        if (selected is null) return Array.Empty<string>();
        var list = new List<string>();
        foreach (var item in selected)
        {
            if (item is null) continue;
            var text = item switch
            {
                bool b => b ? "true" : "false",
                IFormattable f => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
                _ => item.ToString()
            };
            if (!string.IsNullOrEmpty(text) && !list.Contains(text)) list.Add(text!);
        }
        return list;
    }
}

/// <summary>An option for a select/bool filter (value submitted, label shown).</summary>
public sealed record AkFilterOption(string Value, string Label);
