using System.ComponentModel.DataAnnotations;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.Payments;

namespace PTGOilSystem.Web.Models.Ledger;

public sealed class LedgerIndexFilterViewModel
{
    [Display(Name = "از تاریخ")]
    [DataType(DataType.Date)]
    public DateTime? FromDate { get; set; }

    [Display(Name = "تا تاریخ")]
    [DataType(DataType.Date)]
    public DateTime? ToDate { get; set; }

    [Display(Name = "نوع منبع")]
    [StringLength(50)]
    public string? SourceType { get; set; }

    [Display(Name = "قرارداد")]
    public int? ContractId { get; set; }

    [Display(Name = "مشتری")]
    public int? CustomerId { get; set; }

    [Display(Name = "تأمین‌کننده")]
    public int? SupplierId { get; set; }

    [Display(Name = "مرجع")]
    [StringLength(200)]
    public string? Reference { get; set; }

    [Display(Name = "سمت")]
    public LedgerSide? Side { get; set; }
}

public sealed class LedgerListItemViewModel
{
    public int Id { get; init; }
    public DateTime EntryDate { get; init; }
    public LedgerSide Side { get; init; }
    public string SideName { get; init; } = string.Empty;
    public decimal AmountUsd { get; init; }
    public string Currency { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string SourceType { get; init; } = string.Empty;
    public string SourceTypeLabel { get; init; } = string.Empty;
    public int SourceId { get; init; }
    public string? Reference { get; init; }
    public string? SourceDetailsController { get; init; }
    public string? SourceDetailsAction { get; init; }
    public int? SourceDetailsRouteId { get; init; }
    public string? ContractNumber { get; init; }
    public string? CustomerName { get; init; }
    public string? SupplierName { get; init; }
    public string? EmployeeName { get; init; }
    public string? ShipmentCode { get; init; }
}

public sealed class LedgerIndexViewModel
{
    public LedgerIndexFilterViewModel Filter { get; init; } = new();
    public IReadOnlyList<LedgerListItemViewModel> Items { get; init; } = [];
    public int CurrentPage { get; init; } = 1;
    public int PageCount { get; init; } = 1;
    public int TotalCount { get; init; }
    public FinanceMetricCardsViewModel FinanceMetrics { get; init; } = new();
}

public sealed class LedgerRelationViewModel
{
    public int Id { get; init; }
    public string Label { get; init; } = string.Empty;
}

public sealed class LedgerTraceFieldViewModel
{
    public string Label { get; init; } = string.Empty;
    public string Value { get; init; } = string.Empty;
}

public sealed class LedgerSourceTraceViewModel
{
    public string SourceType { get; init; } = string.Empty;
    public int SourceId { get; init; }
    public string Title { get; init; } = string.Empty;
    public string? ControllerName { get; init; }
    public IReadOnlyList<LedgerTraceFieldViewModel> Fields { get; init; } = [];
}

public sealed class LedgerDetailsViewModel
{
    public int Id { get; init; }
    public DateTime EntryDate { get; init; }
    public LedgerSide Side { get; init; }
    public string SideName { get; init; } = string.Empty;
    public decimal AmountUsd { get; init; }
    public string Currency { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string SourceType { get; init; } = string.Empty;
    public string SourceTypeLabel { get; init; } = string.Empty;
    public int SourceId { get; init; }
    public string? Reference { get; init; }
    public LedgerRelationViewModel? Contract { get; init; }
    public LedgerRelationViewModel? Customer { get; init; }
    public LedgerRelationViewModel? Supplier { get; init; }
    public LedgerRelationViewModel? Employee { get; init; }
    public LedgerRelationViewModel? Shipment { get; init; }
    public LedgerSourceTraceViewModel? SourceTrace { get; init; }

    // «پرداخت از طریق صراف» فقط دو LedgerEntry دارد و PaymentTransaction ندارد؛ اتصال قرارداد
    // روی همین صفحه انجام می‌شود. این پرچم‌ها فقط برای ردیفِ سمتِ پرداخت تأمین‌کننده روشن می‌شوند.
    public bool IsViaSarrafSupplierPayment { get; init; }
    public bool HasViaSarrafGroup { get; init; }
}
