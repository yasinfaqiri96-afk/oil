using System.ComponentModel.DataAnnotations;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services.Time;

namespace PTGOilSystem.Web.Models.Expenses;

// یک عملیات در جریان (ارسال موتر یا حمل از موجودی) برای انتخاب در ثبت مصرف گروهی.
public sealed class GroupExpenseOperationItem
{
    public string Kind { get; init; } = "";          // "Dispatch" | "Leg"
    public int Id { get; init; }
    public string OperationLabel { get; init; } = ""; // حمل / ارسال موتر
    public string VehicleKind { get; init; } = "";    // واگن / موتر
    public string Number { get; init; } = "";
    // نمبر جایگزین (سیمیر/CMR) فقط برای تطبیق در امپورت اکسل؛ در جدول نمایش داده نمی‌شود.
    public string? AltNumber { get; init; }
    public string Route { get; init; } = "";
    public decimal QuantityMt { get; init; }
    public string StatusLabel { get; init; } = "";
    public DateTime MoveDate { get; init; }
}

public sealed class GroupExpenseSelectedInput
{
    public string Kind { get; set; } = "";
    public int Id { get; set; }
    // فقط برای روش «دستی» استفاده می‌شود.
    public decimal? ManualAmount { get; set; }
}

// یک «مصرف» در فرم ثبت مصرف گروهی. چند خط می‌توانند در یک ثبت با هم بیایند؛ هر خط
// یک ExpenseBatch جداگانه با همان عملیات‌های انتخاب‌شده می‌سازد. تاریخ، ارز، نرخ ارز و
// «مصرف بدوش کیست» بین همهٔ خط‌ها مشترک است.
public sealed class GroupExpenseLineInput
{
    public int? ExpenseTypeId { get; set; }

    [StringLength(200)]
    public string? ManualExpenseTypeName { get; set; }

    public int? ServiceProviderId { get; set; }

    public ExpenseAllocationMethod AllocationMethod { get; set; } = ExpenseAllocationMethod.EqualSplit;

    public decimal? AmountPerOperation { get; set; }

    public decimal? TotalAmount { get; set; }

    public decimal? RatePerTon { get; set; }

    [StringLength(1000)]
    public string? Description { get; set; }

    // فقط روش دستی: هم‌ترتیب با Items همین فرم.
    public List<decimal?> ManualAmounts { get; set; } = [];

    // فقط روش «بر اساس مقدار»: نرخ فی تنِ جداگانه برای هر عملیات، هم‌ترتیب با Items.
    // خالی بودن یک خانه یعنی همان RatePerTon مشترک استفاده شود.
    public List<decimal?> RatePerTonOverrides { get; set; } = [];
}

public sealed class GroupExpenseCreateViewModel
{
    [Display(Name = "نوع مصرف")]
    public int? ExpenseTypeId { get; set; }

    // امکان تایپ آزاد نوع مصرف؛ اگر پر باشد و از لیست انتخاب نشده باشد، نوع مصرف پیدا/ساخته می‌شود.
    [Display(Name = "نوع مصرف")]
    [StringLength(200)]
    public string? ManualExpenseTypeName { get; set; }

    [Display(Name = "پرداخت شونده (شرکت خدماتی)")]
    public int? ServiceProviderId { get; set; }

    [Display(Name = "تاریخ مصرف")]
    [DataType(DataType.Date)]
    public DateTime ExpenseDate { get; set; } = AfghanistanBusinessClock.SystemToday;

    [Display(Name = "ارز")]
    [Required(ErrorMessage = "ارز الزامی است.")]
    [StringLength(10)]
    public string Currency { get; set; } = "USD";

    [Display(Name = "نرخ تبدیل به USD")]
    [Range(typeof(decimal), "0.000001", "79228162514264337593543950335", ErrorMessage = "نرخ تبدیل باید بزرگ‌تر از صفر باشد.")]
    public decimal? AppliedFxRateToUsd { get; set; }

    [Display(Name = "روش تقسیم")]
    public ExpenseAllocationMethod AllocationMethod { get; set; } = ExpenseAllocationMethod.EqualSplit;

    // FixedPerOperation → مبلغ ثابت هر عملیات؛ EqualSplit → مبلغ کل؛ ByQuantity → نرخ فی تن.
    [Display(Name = "مبلغ برای هر عملیات")]
    public decimal? AmountPerOperation { get; set; }

    [Display(Name = "مبلغ کل")]
    public decimal? TotalAmount { get; set; }

    // بر اساس مقدار: سهم هر عملیات = نرخ فی تن × مقدار (تن) همان حمل.
    [Display(Name = "نرخ فی تن")]
    public decimal? RatePerTon { get; set; }

    [Display(Name = "توضیحات")]
    [StringLength(1000)]
    public string? Description { get; set; }

    [Display(Name = "مصرف بدوش کیست")]
    public CostResponsibility? CostResponsibility { get; set; }

    public List<GroupExpenseSelectedInput> Items { get; set; } = [];

    // چند مصرف در یک ثبت. اگر خالی باشد، فیلدهای بالا یک خط می‌سازند (سازگاری با فرم قبلی).
    public List<GroupExpenseLineInput> Lines { get; set; } = [];

    [StringLength(1000)]
    public string? ReturnUrl { get; set; }
}

// خلاصهٔ یک مصرف گروهی ثبت‌شده برای نمایش در فرم ثبت مصرف گروهی (با امکان لغو).
public sealed class GroupExpenseBatchListItem
{
    public int Id { get; init; }
    public string BatchNumber { get; init; } = "";
    public string ExpenseTypeName { get; init; } = "";
    public string? ServiceProviderName { get; init; }
    public DateTime ExpenseDate { get; init; }
    public string Currency { get; init; } = "USD";
    public decimal TotalAmount { get; init; }
    public decimal TotalAmountUsd { get; init; }
    public int OperationCount { get; init; }
}

public sealed class GroupExpenseShareViewModel
{
    public int ExpenseId { get; init; }
    public string OperationLabel { get; init; } = "";
    public string VehicleKind { get; init; } = "";
    public string Number { get; init; } = "";
    public string Route { get; init; } = "";
    public decimal QuantityMt { get; init; }
    public decimal Amount { get; init; }
    public decimal AmountUsd { get; init; }
    public bool IsCancelled { get; init; }
    public int? TruckDispatchId { get; init; }
    public int? TransportLegId { get; init; }
}

public sealed class GroupExpenseDetailsViewModel
{
    public int Id { get; init; }
    public string BatchNumber { get; init; } = "";
    public string ExpenseTypeName { get; init; } = "";
    public string? ServiceProviderName { get; init; }
    public DateTime ExpenseDate { get; init; }
    public string AllocationMethodName { get; init; } = "";
    public string Currency { get; init; } = "USD";
    public decimal? AppliedFxRateToUsd { get; init; }
    public decimal TotalAmount { get; init; }
    public decimal TotalAmountUsd { get; init; }
    public string? Description { get; init; }
    public bool IsCancelled { get; init; }
    public IReadOnlyList<GroupExpenseShareViewModel> Shares { get; init; } = [];
}
