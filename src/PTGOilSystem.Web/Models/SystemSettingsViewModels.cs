using System.ComponentModel.DataAnnotations;
using PTGOilSystem.Web.Models.Entities;

namespace PTGOilSystem.Web.Models;

public sealed class SystemSettingsFormViewModel
{
    [Display(Name = "نوع تقویم")]
    public CalendarType CalendarType { get; set; } = CalendarType.Gregorian;
}

public sealed class SystemSettingsPageViewModel
{
    public SystemSettingsFormViewModel Settings { get; set; } = new();

    /// <summary>نمونهٔ نمایشِ امروز در هر دو تقویم تا اثرِ انتخاب پیش از ذخیره روشن باشد.</summary>
    public string TodayGregorian { get; set; } = string.Empty;
    public string TodaySolar { get; set; } = string.Empty;

    public static IReadOnlyList<(CalendarType Value, string Label)> CalendarOptions { get; } =
    [
        (CalendarType.Gregorian, "میلادی"),
        (CalendarType.AfghanSolarHijri, "هجری شمسی افغانستان")
    ];
}
