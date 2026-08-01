namespace PTGOilSystem.Web.Models.Reconciliation;

/// <summary>
/// دسته‌های مغایرت که در صفحهٔ «مغایرت‌های تفصیلی» گزارش می‌شوند.
/// هر دسته یک query مستقل با Paging مستقل دارد؛ هیچ دسته‌ای کل جدول را در حافظه نمی‌آورد.
/// </summary>
public enum ReconciliationDiscrepancyCategory
{
    LedgerWithoutJournal = 1,
    JournalWithoutOperationalSource = 2,
    UnbalancedJournal = 3,
    SaleWithoutCogs = 4,
    SaleWithoutContractOrInventorySource = 5,
    PossiblyDoubleCountedExpense = 6,
    PossiblyDoubleCountedCustoms = 7,
    NegativeStock = 8,
    OverDelivery = 9,
    DeliveryWithoutPreSale = 10,
    ReservationExceedsStock = 11,
    OverduePreSaleCommitment = 12,
    UnallocatedPayment = 13,
    UnconfirmedSarrafSettlement = 14,
    OperationalDocumentWithoutLedger = 15,
    IncompleteContractOrShipmentLineage = 16,
    IncompleteCustomsDocument = 17,
    QualityInspectionPending = 18,
    QualityInspectionRejected = 19,
    QualityInspectionWithoutResultDocument = 20
}

/// <summary>یک ردیف مغایرت. عمداً تخت است تا مستقیماً در SQL projection ساخته شود.</summary>
public sealed class ReconciliationDiscrepancyRow
{
    public string Reference { get; set; } = "";
    public DateTime? Date { get; set; }
    public decimal? AmountUsd { get; set; }
    public decimal? QuantityMt { get; set; }
    public string Detail { get; set; } = "";

    /// <summary>Drill-down مستقیم به سند اصلی؛ اگر Controller خالی باشد لینک نمایش داده نمی‌شود.</summary>
    public string DrillDownController { get; set; } = "";
    public string DrillDownAction { get; set; } = "";
    public int? DrillDownId { get; set; }

    public bool HasDrillDown => !string.IsNullOrEmpty(DrillDownController) && DrillDownId.HasValue;
}

/// <summary>یک صفحه از یک دستهٔ مغایرت. <see cref="TotalCount"/> با COUNT در SQL محاسبه می‌شود.</summary>
public sealed class ReconciliationDiscrepancyPage
{
    public ReconciliationDiscrepancyCategory Category { get; init; }
    public int TotalCount { get; init; }
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 50;
    public IReadOnlyList<ReconciliationDiscrepancyRow> Rows { get; init; } = [];

    public int PageCount => PageSize <= 0 ? 1 : Math.Max(1, (int)Math.Ceiling(TotalCount / (double)PageSize));
    public bool HasPrevious => Page > 1;
    public bool HasNext => Page < PageCount;
}

/// <summary>شمارش یک دسته برای کارت خلاصه. فقط COUNT اجرا می‌شود، نه واکشی ردیف‌ها.</summary>
public sealed class ReconciliationDiscrepancyCount
{
    public ReconciliationDiscrepancyCategory Category { get; init; }
    public int Count { get; init; }
    public string TitleFa => ReconciliationDiscrepancyText.TitleFa(Category);
    public string TitleEn => ReconciliationDiscrepancyText.TitleEn(Category);
    public string Severity => ReconciliationDiscrepancyText.Severity(Category);
}

public sealed class ReconciliationDiscrepanciesViewModel
{
    public IReadOnlyList<ReconciliationDiscrepancyCount> Counts { get; init; } = [];
    public ReconciliationDiscrepancyPage? Selected { get; init; }
    public ReconciliationDiscrepancyCategory? SelectedCategory { get; init; }
    public DateTime? FromDate { get; init; }
    public DateTime? ToDate { get; init; }
    public DateTime GeneratedOnBusinessDate { get; init; }

    public int TotalCount => Counts.Sum(c => c.Count);
}

/// <summary>عنوان و شدت هر دسته در یک جا؛ View، Export و Summary همگی از همین می‌خوانند.</summary>
public static class ReconciliationDiscrepancyText
{
    public static IReadOnlyList<ReconciliationDiscrepancyCategory> All { get; } =
        Enum.GetValues<ReconciliationDiscrepancyCategory>();

    public static string TitleFa(ReconciliationDiscrepancyCategory category) => category switch
    {
        ReconciliationDiscrepancyCategory.LedgerWithoutJournal => "دفتر کل بدون سند حسابداری",
        ReconciliationDiscrepancyCategory.JournalWithoutOperationalSource => "سند حسابداری بدون منبع عملیاتی",
        ReconciliationDiscrepancyCategory.UnbalancedJournal => "سند حسابداری نامتوازن",
        ReconciliationDiscrepancyCategory.SaleWithoutCogs => "فروش بدون بهای تمام‌شده",
        ReconciliationDiscrepancyCategory.SaleWithoutContractOrInventorySource => "فروش بدون قرارداد یا منبع موجودی",
        ReconciliationDiscrepancyCategory.PossiblyDoubleCountedExpense => "مصرف با احتمال ثبت دوباره",
        ReconciliationDiscrepancyCategory.PossiblyDoubleCountedCustoms => "گمرک با احتمال ثبت دوباره",
        ReconciliationDiscrepancyCategory.NegativeStock => "موجودی منفی",
        ReconciliationDiscrepancyCategory.OverDelivery => "تحویل بیش از تعهد",
        ReconciliationDiscrepancyCategory.DeliveryWithoutPreSale => "تحویل بدون پیش‌فروش",
        ReconciliationDiscrepancyCategory.ReservationExceedsStock => "رزرو بیشتر از موجودی",
        ReconciliationDiscrepancyCategory.OverduePreSaleCommitment => "تعهد پیش‌فروش سررسیدشده",
        ReconciliationDiscrepancyCategory.UnallocatedPayment => "پرداخت تخصیص‌نیافته",
        ReconciliationDiscrepancyCategory.UnconfirmedSarrafSettlement => "حوالهٔ صراف تأییدنشده",
        ReconciliationDiscrepancyCategory.OperationalDocumentWithoutLedger => "سند عملیاتی بدون دفتر کل",
        ReconciliationDiscrepancyCategory.IncompleteContractOrShipmentLineage => "قرارداد یا محموله با زنجیرهٔ ناقص",
        ReconciliationDiscrepancyCategory.IncompleteCustomsDocument => "سند گمرکی ناقص",
        ReconciliationDiscrepancyCategory.QualityInspectionPending => "آزمایش کیفیت در انتظار نتیجه",
        ReconciliationDiscrepancyCategory.QualityInspectionRejected => "آزمایش کیفیت رد شده",
        ReconciliationDiscrepancyCategory.QualityInspectionWithoutResultDocument => "آزمایش بدون سند نتیجه",
        _ => category.ToString()
    };

    public static string TitleEn(ReconciliationDiscrepancyCategory category) => category switch
    {
        ReconciliationDiscrepancyCategory.LedgerWithoutJournal => "Ledger without journal entry",
        ReconciliationDiscrepancyCategory.JournalWithoutOperationalSource => "Journal without operational source",
        ReconciliationDiscrepancyCategory.UnbalancedJournal => "Unbalanced journal entry",
        ReconciliationDiscrepancyCategory.SaleWithoutCogs => "Sale without COGS",
        ReconciliationDiscrepancyCategory.SaleWithoutContractOrInventorySource => "Sale without contract or inventory source",
        ReconciliationDiscrepancyCategory.PossiblyDoubleCountedExpense => "Possibly double-counted expense",
        ReconciliationDiscrepancyCategory.PossiblyDoubleCountedCustoms => "Possibly double-counted customs",
        ReconciliationDiscrepancyCategory.NegativeStock => "Negative stock",
        ReconciliationDiscrepancyCategory.OverDelivery => "Over-delivery",
        ReconciliationDiscrepancyCategory.DeliveryWithoutPreSale => "Delivery without pre-sale",
        ReconciliationDiscrepancyCategory.ReservationExceedsStock => "Reservation exceeds stock",
        ReconciliationDiscrepancyCategory.OverduePreSaleCommitment => "Overdue pre-sale commitment",
        ReconciliationDiscrepancyCategory.UnallocatedPayment => "Unallocated payment",
        ReconciliationDiscrepancyCategory.UnconfirmedSarrafSettlement => "Unconfirmed sarraf settlement",
        ReconciliationDiscrepancyCategory.OperationalDocumentWithoutLedger => "Operational document without ledger",
        ReconciliationDiscrepancyCategory.IncompleteContractOrShipmentLineage => "Incomplete contract or shipment lineage",
        ReconciliationDiscrepancyCategory.IncompleteCustomsDocument => "Incomplete customs document",
        ReconciliationDiscrepancyCategory.QualityInspectionPending => "Quality inspection pending",
        ReconciliationDiscrepancyCategory.QualityInspectionRejected => "Quality inspection rejected",
        ReconciliationDiscrepancyCategory.QualityInspectionWithoutResultDocument => "Inspection without result document",
        _ => category.ToString()
    };

    /// <summary>critical = عدد مالی/موجودی غلط می‌شود، warning = سند ناقص ولی عدد سالم است.</summary>
    public static string Severity(ReconciliationDiscrepancyCategory category) => category switch
    {
        ReconciliationDiscrepancyCategory.UnbalancedJournal => "critical",
        ReconciliationDiscrepancyCategory.SaleWithoutCogs => "critical",
        ReconciliationDiscrepancyCategory.NegativeStock => "critical",
        ReconciliationDiscrepancyCategory.OverDelivery => "critical",
        ReconciliationDiscrepancyCategory.ReservationExceedsStock => "critical",
        ReconciliationDiscrepancyCategory.PossiblyDoubleCountedExpense => "critical",
        ReconciliationDiscrepancyCategory.PossiblyDoubleCountedCustoms => "critical",
        ReconciliationDiscrepancyCategory.QualityInspectionRejected => "critical",
        _ => "warning"
    };
}
