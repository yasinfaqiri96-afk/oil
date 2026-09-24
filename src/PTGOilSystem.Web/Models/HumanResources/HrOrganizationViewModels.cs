using System.ComponentModel.DataAnnotations;

namespace PTGOilSystem.Web.Models.HumanResources;

public sealed class DepartmentFormViewModel
{
    public int Id { get; set; }

    [Display(Name = "نام بخش")]
    [Required(ErrorMessage = "نام بخش الزامی است.")]
    [StringLength(150)]
    public string Name { get; set; } = "";

    [Display(Name = "کد")]
    [StringLength(30)]
    public string? Code { get; set; }

    [Display(Name = "توضیح")]
    [StringLength(500)]
    public string? Description { get; set; }

    [Display(Name = "فعال")]
    public bool IsActive { get; set; } = true;
}

public sealed class PositionFormViewModel
{
    public int Id { get; set; }

    [Display(Name = "نام بست")]
    [Required(ErrorMessage = "نام بست الزامی است.")]
    [StringLength(150)]
    public string Name { get; set; } = "";

    [Display(Name = "بخش")]
    public int? DepartmentId { get; set; }

    [Display(Name = "توضیح")]
    [StringLength(500)]
    public string? Description { get; set; }

    [Display(Name = "فعال")]
    public bool IsActive { get; set; } = true;
}

public sealed class DepartmentListItemViewModel
{
    public int Id { get; init; }
    public string Name { get; init; } = "";
    public string? Code { get; init; }
    public string? Description { get; init; }
    public bool IsActive { get; init; }
    public int PositionCount { get; init; }
    public int ActiveEmployeeCount { get; init; }
}

public sealed class PositionListItemViewModel
{
    public int Id { get; init; }
    public string Name { get; init; } = "";
    public string? DepartmentName { get; init; }
    public string? Description { get; init; }
    public bool IsActive { get; init; }
    public int ActiveEmployeeCount { get; init; }
}

public sealed class HrOrganizationIndexViewModel
{
    public string Tab { get; init; } = "departments";
    public string? Query { get; init; }
    public IReadOnlyList<DepartmentListItemViewModel> Departments { get; init; } = [];
    public IReadOnlyList<PositionListItemViewModel> Positions { get; init; } = [];

    /// <summary>کارمندانی که متنِ بخش/بستِ قدیمی دارند ولی به بخش/بستِ تعریف‌شده وصل نیستند.</summary>
    public int UnlinkedEmployeeCount { get; init; }
}
