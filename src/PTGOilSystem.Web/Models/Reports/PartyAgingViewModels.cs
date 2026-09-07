namespace PTGOilSystem.Web.Models.Reports;

/// <summary>
/// دستهٔ سنِ حساب. مبنا «آخرین حرکت حساب» است، نه تاریخ سررسید — سیستم برای
/// طرف‌حساب‌ها سررسید جداگانه ثبت نمی‌کند، پس این گزارش هم چیزی از خود نمی‌سازد.
/// </summary>
public enum PartyAgingBucket
{
    UpTo30 = 0,
    From31To60 = 1,
    From61To90 = 2,
    Over90 = 3
}

/// <summary>یک طرف‌حساب با مانده و سنِ سکوت همان حساب. فقط‌خواندنی.</summary>
public sealed class PartyAgingRowViewModel
{
    public string PartyType { get; init; } = "";
    public string PartyTypeLabel { get; init; } = "";
    public int? PartyId { get; init; }
    public string PartyName { get; init; } = "";

    /// <summary>مانده مثبت — شرکت از این طرف طلبکار است.</summary>
    public decimal ReceivableUsd { get; init; }

    /// <summary>مانده منفی به‌صورت مثبت — شرکت به این طرف بدهکار است.</summary>
    public decimal PayableUsd { get; init; }

    public DateTime? LastEntryDate { get; init; }

    /// <summary>چند روز است که این حساب هیچ حرکتی ندارد.</summary>
    public int DaysIdle { get; init; }

    public PartyAgingBucket Bucket { get; init; }
    public string? DetailsController { get; init; }
}

/// <summary>
/// «سررسید طلبات و بدهی‌ها» — همان مانده‌های گزارش طلبات و بدهی‌ها، فقط بر اساس
/// روزهای بی‌حرکتی دسته‌بندی شده. هیچ مانده‌ای اینجا دوباره محاسبه نمی‌شود.
/// </summary>
public sealed class PartyAgingReportViewModel
{
    public ManagementReportFilterViewModel Filter { get; init; } = new();
    public DateTime AsOfDate { get; init; } = DateTime.Today;
    public IReadOnlyList<PartyAgingRowViewModel> Rows { get; init; } = [];
    public IReadOnlyList<ReportMetricViewModel> Metrics { get; init; } = [];

    public decimal ReceivableIn(PartyAgingBucket bucket)
        => Rows.Where(r => r.Bucket == bucket).Sum(r => r.ReceivableUsd);

    public decimal PayableIn(PartyAgingBucket bucket)
        => Rows.Where(r => r.Bucket == bucket).Sum(r => r.PayableUsd);

    public decimal TotalReceivableUsd => Rows.Sum(r => r.ReceivableUsd);
    public decimal TotalPayableUsd => Rows.Sum(r => r.PayableUsd);
    public int PartyCount => Rows.Count;
}
