namespace PTGOilSystem.Web.Models.Accounting;

/// <summary>
/// یک ردیفِ خلاصهٔ فعالیت در صفحهٔ «مشاهده فعالیت‌ها». فقط نمایشی است؛ هیچ محاسبهٔ مالی‌ای اینجا
/// یا در View انجام نمی‌شود — همه در <see cref="Services.PeriodActivityService"/>.
/// </summary>
public sealed record PeriodActivityRow(
    DateTime Date,
    string Title,
    string? Subtitle,
    decimal? QuantityMt,
    decimal? AmountUsd,
    string? Status);

/// <summary>
/// شاخص‌های خلاصهٔ بالای صفحه برای بازهٔ یک دورهٔ مالی.
/// </summary>
public sealed record PeriodActivityKpis(
    int PurchaseCount,
    decimal PurchaseQuantityMt,
    int LoadingCount,
    decimal LoadingQuantityMt,
    int SalesCount,
    decimal SalesQuantityMt,
    decimal SalesUsd,
    decimal ReceiptsUsd,
    decimal PaymentsUsd,
    decimal ExpensesUsd,
    decimal InventoryInMt,
    decimal InventoryOutMt,
    int JournalCount,
    decimal JournalDebitUsd)
{
    public decimal InventoryNetMt => InventoryInMt - InventoryOutMt;
}

/// <summary>
/// مدلِ صفحهٔ فقط‌خواندنیِ «فعالیت‌های دوره». همهٔ لیست‌ها به بازهٔ [<see cref="StartDate"/>,
/// <see cref="EndDate"/>] و شرکتِ همان دوره محدودند. بخشِ اسناد حسابداری فقط وقتی
/// <see cref="AccountingEnabled"/> باشد پر می‌شود.
/// </summary>
public sealed record PeriodActivityViewModel(
    int FiscalPeriodId,
    int FiscalYearId,
    string FiscalYearName,
    string PeriodName,
    DateTime StartDate,
    DateTime EndDate,
    string CompanyName,
    bool AccountingEnabled,
    PeriodActivityKpis Kpis,
    IReadOnlyList<PeriodActivityRow> Purchases,
    IReadOnlyList<PeriodActivityRow> Loadings,
    IReadOnlyList<PeriodActivityRow> Sales,
    IReadOnlyList<PeriodActivityRow> Receipts,
    IReadOnlyList<PeriodActivityRow> Payments,
    IReadOnlyList<PeriodActivityRow> Expenses,
    IReadOnlyList<PeriodActivityRow> InventoryMovements,
    IReadOnlyList<PeriodActivityRow> Journals);
