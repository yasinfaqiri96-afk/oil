using System.Collections.Generic;

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
    string Type,                                   // "select" | "bool" | "date" | "daterange"
    IReadOnlyList<AkFilterOption>? Options = null, // for select/bool
    string? Value = null,                          // current applied value (select/bool/date/daterange-from)
    string? SecondKey = null,                      // daterange: the "to" param name
    string? SecondValue = null);                   // daterange: current "to" value

/// <summary>An option for a select/bool filter (value submitted, label shown).</summary>
public sealed record AkFilterOption(string Value, string Label);
