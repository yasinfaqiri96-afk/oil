using System.ComponentModel.DataAnnotations;
using PTGOilSystem.Web.Services.Time;

namespace PTGOilSystem.Web.Models.PlattsRates;

public static class PlattsRatesTabs
{
    public const string Daily = "daily";
    public const string Monthly = "monthly";
    public const string Manual = "manual";
}

public sealed class PlattsRatesPageViewModel
{
    public string ActiveTab { get; set; } = PlattsRatesTabs.Daily;
    public DailyPlattsRatesFilterViewModel DailyFilter { get; set; } = new();
    public MonthlyPlattsSummaryFilterViewModel MonthlyFilter { get; set; } = new();
    public MonthlyManualRatesFilterViewModel ManualFilter { get; set; } = new();
    public DailyPlattsRateFormViewModel DailyForm { get; set; } = new();
    public MonthlyManualRateFormViewModel MonthlyManualForm { get; set; } = new();
    public IReadOnlyList<DailyPlattsRateRowViewModel> DailyRates { get; set; } = [];
    public IReadOnlyList<MonthlyPlattsSummaryItemViewModel> MonthlySummaries { get; set; } = [];
    public IReadOnlyList<MonthlyManualRateRowViewModel> MonthlyManualRates { get; set; } = [];
}

public sealed class DailyPlattsRatesFilterViewModel
{
    [Display(Name = "جنس")]
    public int? ProductId { get; set; }

    [Display(Name = "Benchmark")]
    public string? BenchmarkCode { get; set; }

    [Display(Name = "از تاریخ")]
    public DateTime? From { get; set; }

    [Display(Name = "تا تاریخ")]
    public DateTime? To { get; set; }
}

public sealed class MonthlyPlattsSummaryFilterViewModel
{
    [Display(Name = "جنس")]
    public int? ProductId { get; set; }

    [Display(Name = "Benchmark")]
    public string? BenchmarkCode { get; set; }

    [Display(Name = "سال")]
    public int? Year { get; set; }
}

public sealed class MonthlyManualRatesFilterViewModel
{
    [Display(Name = "جنس")]
    public int? ProductId { get; set; }

    [Display(Name = "Benchmark")]
    public string? BenchmarkCode { get; set; }
}

public sealed class DailyPlattsRateFormViewModel
{
    public int? Id { get; set; }

    [Display(Name = "جنس")]
    [Range(1, int.MaxValue, ErrorMessage = "انتخاب جنس الزامی است.")]
    public int ProductId { get; set; }

    [Required, MaxLength(100)]
    [Display(Name = "Benchmark")]
    public string BenchmarkCode { get; set; } = string.Empty;

    [Display(Name = "تاریخ قیمت")]
    public DateTime PriceDate { get; set; } = AfghanistanBusinessClock.SystemToday;

    [Display(Name = "قیمت USD/MT")]
    [Range(typeof(decimal), "0.0001", "79228162514264337593543950335", ErrorMessage = "قیمت باید بزرگ‌تر از صفر باشد.")]
    public decimal PriceUsdPerMt { get; set; }

    [MaxLength(500)]
    [Display(Name = "منبع")]
    public string? Source { get; set; }
}

public sealed class MonthlyManualRateFormViewModel
{
    public int? Id { get; set; }

    [Display(Name = "جنس")]
    [Range(1, int.MaxValue, ErrorMessage = "انتخاب جنس الزامی است.")]
    public int ProductId { get; set; }

    [Required, MaxLength(100)]
    [Display(Name = "Benchmark")]
    public string BenchmarkCode { get; set; } = string.Empty;

    [Display(Name = "ماه")]
    public DateTime Month { get; set; } = new(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1);

    [Display(Name = "قیمت USD/MT")]
    [Range(typeof(decimal), "0.0001", "79228162514264337593543950335", ErrorMessage = "قیمت باید بزرگ‌تر از صفر باشد.")]
    public decimal PriceUsdPerMt { get; set; }

    [MaxLength(1000)]
    [Display(Name = "یادداشت")]
    public string? Notes { get; set; }
}

public sealed class DailyPlattsRateRowViewModel
{
    public int Id { get; init; }
    public int ProductId { get; init; }
    public string ProductName { get; init; } = string.Empty;
    public string BenchmarkCode { get; init; } = string.Empty;
    public DateTime PriceDate { get; init; }
    public decimal PriceUsdPerMt { get; init; }
    public string? Source { get; init; }
}

public sealed class MonthlyPlattsSummaryItemViewModel
{
    public int ProductId { get; init; }
    public string ProductName { get; init; } = string.Empty;
    public string BenchmarkCode { get; init; } = string.Empty;
    public DateTime Month { get; init; }
    public decimal AveragePriceUsdPerMt { get; init; }
    public int DayCount { get; init; }
    public decimal MinPriceUsdPerMt { get; init; }
    public decimal MaxPriceUsdPerMt { get; init; }
}

public sealed class MonthlyManualRateRowViewModel
{
    public int Id { get; init; }
    public int ProductId { get; init; }
    public string ProductName { get; init; } = string.Empty;
    public string BenchmarkCode { get; init; } = string.Empty;
    public DateTime Month { get; init; }
    public decimal PriceUsdPerMt { get; init; }
    public string? Notes { get; init; }
}
