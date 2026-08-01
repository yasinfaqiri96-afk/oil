using System.ComponentModel.DataAnnotations;
using PTGOilSystem.Web.Services.Time;

namespace PTGOilSystem.Web.Models.Expenses;

public static class ExpenseRuleCalculationKinds
{
    public const string Flat = "Flat";
    public const string PerMt = "PerMt";
    public const string Percent = "Percent";

    public static readonly string[] All = [Flat, PerMt, Percent];
}

public sealed class ExpenseRuleEditViewModel
{
    public int Id { get; set; }

    [Display(Name = "نام Rule")]
    [Required(ErrorMessage = "نام Rule الزامی است.")]
    [StringLength(100)]
    public string Name { get; set; } = string.Empty;

    [Display(Name = "نوع مصرف")]
    [Range(1, int.MaxValue, ErrorMessage = "انتخاب نوع مصرف الزامی است.")]
    public int ExpenseTypeId { get; set; }

    [Display(Name = "نوع محاسبه")]
    [Required(ErrorMessage = "نوع محاسبه الزامی است.")]
    [StringLength(50)]
    public string CalculationKind { get; set; } = ExpenseRuleCalculationKinds.PerMt;

    [Display(Name = "مقدار Rule")]
    [Range(typeof(decimal), "0.0001", "79228162514264337593543950335", ErrorMessage = "مقدار Rule باید بزرگ‌تر از صفر باشد.")]
    public decimal Amount { get; set; }

    [Display(Name = "ارز")]
    [Required(ErrorMessage = "ارز الزامی است.")]
    [StringLength(10)]
    public string Currency { get; set; } = "USD";

    [Display(Name = "فعال")]
    public bool IsActive { get; set; } = true;
}

public sealed class ExpenseRuleListItemViewModel
{
    public int Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string ExpenseTypeName { get; init; } = string.Empty;
    public string CalculationKind { get; init; } = string.Empty;
    public decimal Amount { get; init; }
    public string Currency { get; init; } = string.Empty;
    public bool IsActive { get; init; }
    public int UsageCount { get; init; }
}

public sealed class ExpenseRuleIndexViewModel
{
    public IReadOnlyList<ExpenseRuleListItemViewModel> Items { get; init; } = [];
}

public sealed class ExpenseRuleGeneratedExpenseItemViewModel
{
    public int Id { get; init; }
    public DateTime ExpenseDate { get; init; }
    public decimal Amount { get; init; }
    public string Currency { get; init; } = "USD";
    public decimal? AppliedFxRateToUsd { get; init; }
    public decimal AmountUsd { get; init; }
    public string? ContractNumber { get; init; }
    public string? ShipmentCode { get; init; }
    public string? TruckDispatchLabel { get; init; }
    public string? Description { get; init; }
}

public sealed class ExpenseRuleGenerateExpenseViewModel
{
    [Display(Name = "تاریخ مصرف")]
    [DataType(DataType.Date)]
    public DateTime ExpenseDate { get; set; } = AfghanistanBusinessClock.SystemToday;

    [Display(Name = "قرارداد")]
    public int? ContractId { get; set; }

    [Display(Name = "Shipment")]
    public int? ShipmentId { get; set; }

    [Display(Name = "دیسپچ موتر باربری")]
    public int? TruckDispatchId { get; set; }

    [Display(Name = "Quantity (MT)")]
    [Range(typeof(decimal), "0", "79228162514264337593543950335", ErrorMessage = "Quantity نمی‌تواند منفی باشد.")]
    public decimal? QuantityMt { get; set; }

    [Display(Name = "Base Amount (USD)")]
    [Range(typeof(decimal), "0", "79228162514264337593543950335", ErrorMessage = "Base Amount نمی‌تواند منفی باشد.")]
    public decimal? BaseAmountUsd { get; set; }

    [Display(Name = "نرخ تبدیل به USD")]
    [Range(typeof(decimal), "0.000001", "79228162514264337593543950335", ErrorMessage = "نرخ تبدیل باید بزرگ‌تر از صفر باشد.")]
    public decimal? AppliedFxRateToUsd { get; set; }

    [Display(Name = "یادداشت / مرجع")]
    [StringLength(500)]
    public string? Description { get; set; }
}

public sealed class ExpenseRuleDetailsViewModel
{
    public int Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string ExpenseTypeName { get; init; } = string.Empty;
    public string CalculationKind { get; init; } = string.Empty;
    public decimal Amount { get; init; }
    public string Currency { get; init; } = string.Empty;
    public bool IsActive { get; init; }
    public bool CanGenerateExpense { get; init; }
    public ExpenseRuleGenerateExpenseViewModel Generation { get; init; } = new();
    public IReadOnlyList<ExpenseRuleGeneratedExpenseItemViewModel> GeneratedExpenses { get; init; } = [];
}
