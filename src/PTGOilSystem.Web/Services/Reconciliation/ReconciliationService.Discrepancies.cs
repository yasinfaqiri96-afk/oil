using Microsoft.EntityFrameworkCore;
// ثابت‌های SourceType و SourceEntityType همان‌جایی می‌مانند که نوشته می‌شوند؛ اینجا فقط
// خوانده می‌شوند تا نگاشت دفتر عملیاتی ↔ دفتر حسابداری تعریف موازی نداشته باشد.
using PTGOilSystem.Web.Controllers;
using PTGOilSystem.Web.Helpers;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.Reconciliation;
using PTGOilSystem.Web.Services.Accounting;

namespace PTGOilSystem.Web.Services.Reconciliation;

/// <summary>
/// دسته‌های مغایرت تفصیلی. هر دسته یک <see cref="IQueryable{T}"/> است، بنابراین
/// Where، Count، Order و Skip/Take همگی در SQL اجرا می‌شوند و هیچ‌جا قبل از فیلتر و
/// Paging کل جدول با ToListAsync خوانده نمی‌شود. همهٔ queryها read-only و AsNoTracking‌اند.
/// </summary>
public partial class ReconciliationService
{
    private const int MaxPageSize = 200;

    /// <summary>
    /// فقط COUNT هر دسته را می‌گیرد (بدون واکشی ردیف‌ها) تا کارت‌های خلاصه سبک بمانند.
    /// </summary>
    public async Task<IReadOnlyList<ReconciliationDiscrepancyCount>> BuildDiscrepancyCountsAsync(
        DateTime? fromDate = null,
        DateTime? toDate = null,
        CancellationToken ct = default)
    {
        var results = new List<ReconciliationDiscrepancyCount>(ReconciliationDiscrepancyText.All.Count);
        foreach (var category in ReconciliationDiscrepancyText.All)
        {
            var count = await BuildDiscrepancyCountAsync(category, fromDate, toDate, ct);
            results.Add(new ReconciliationDiscrepancyCount { Category = category, Count = count });
        }

        return results;
    }

    /// <summary>
    /// شمارش یک دسته. UI، Export و تست همگی از همین متد می‌خوانند تا عدد سه جا یکی باشد.
    /// فقط COUNT در SQL اجرا می‌شود.
    /// </summary>
    public Task<int> BuildDiscrepancyCountAsync(
        ReconciliationDiscrepancyCategory category,
        DateTime? fromDate = null,
        DateTime? toDate = null,
        CancellationToken ct = default)
        => BuildDiscrepancyQuery(category, fromDate, toDate).CountAsync(ct);

    /// <summary>
    /// همان query صفحه، بدون Paging و بدون ToList. مصرف‌کننده (Export) هر ردیف را
    /// همان‌طور که از reader می‌رسد می‌نویسد، پس حافظه با اندازهٔ نتیجه رشد نمی‌کند.
    /// </summary>
    public IAsyncEnumerable<ReconciliationDiscrepancyRow> StreamDiscrepancyRowsAsync(
        ReconciliationDiscrepancyCategory category,
        DateTime? fromDate = null,
        DateTime? toDate = null,
        CancellationToken ct = default)
        => BuildDiscrepancyQuery(category, fromDate, toDate).AsAsyncEnumerable();

    /// <summary>یک صفحه از یک دسته. Paging هر دسته کاملاً مستقل است.</summary>
    public async Task<ReconciliationDiscrepancyPage> BuildDiscrepancyPageAsync(
        ReconciliationDiscrepancyCategory category,
        int page = 1,
        int pageSize = 50,
        DateTime? fromDate = null,
        DateTime? toDate = null,
        CancellationToken ct = default)
    {
        var safePageSize = Math.Clamp(pageSize, 1, MaxPageSize);
        var safePage = Math.Max(1, page);

        var query = BuildDiscrepancyQuery(category, fromDate, toDate);
        var total = await query.CountAsync(ct);

        // اگر کاربر صفحه‌ای فراتر از آخرین صفحه بخواهد، به آخرین صفحهٔ موجود برمی‌گردیم.
        var pageCount = Math.Max(1, (int)Math.Ceiling(total / (double)safePageSize));
        if (safePage > pageCount) safePage = pageCount;

        var rows = await query
            .Skip((safePage - 1) * safePageSize)
            .Take(safePageSize)
            .ToListAsync(ct);

        return new ReconciliationDiscrepancyPage
        {
            Category = category,
            TotalCount = total,
            Page = safePage,
            PageSize = safePageSize,
            Rows = rows
        };
    }

    /// <summary>
    /// ستون‌های تاریخ در PostgreSQL از نوع <c>timestamp with time zone</c> هستند و
    /// <see cref="ApplicationDbContext"/> هنگام ذخیره، تاریخ تجاریِ بدون Kind را با
    /// <c>SpecifyKind(Utc)</c> نگه می‌دارد. فیلتر هم باید دقیقاً همان قرارداد را داشته
    /// باشد، وگرنه Npgsql پارامتر Unspecified را رد می‌کند. کران بالا شاملِ کل روز است.
    /// </summary>
    private static (DateTime? FromUtc, DateTime? ToUtcExclusive) NormalizeRange(DateTime? from, DateTime? to)
        => (from.HasValue ? DateTime.SpecifyKind(from.Value.Date, DateTimeKind.Utc) : null,
            to.HasValue ? DateTime.SpecifyKind(to.Value.Date.AddDays(1), DateTimeKind.Utc) : null);

    private IQueryable<ReconciliationDiscrepancyRow> BuildDiscrepancyQuery(
        ReconciliationDiscrepancyCategory category,
        DateTime? rawFrom,
        DateTime? rawTo)
    {
        var (fromDate, toDate) = NormalizeRange(rawFrom, rawTo);
        return category switch
        {
            ReconciliationDiscrepancyCategory.LedgerWithoutJournal => LedgerWithoutJournalQuery(fromDate, toDate),
            ReconciliationDiscrepancyCategory.JournalWithoutOperationalSource => JournalWithoutSourceQuery(fromDate, toDate),
            ReconciliationDiscrepancyCategory.UnbalancedJournal => UnbalancedJournalQuery(fromDate, toDate),
            ReconciliationDiscrepancyCategory.SaleWithoutCogs => SaleWithoutCogsQuery(fromDate, toDate),
            ReconciliationDiscrepancyCategory.SaleWithoutContractOrInventorySource => SaleWithoutSourceQuery(fromDate, toDate),
            ReconciliationDiscrepancyCategory.PossiblyDoubleCountedExpense => DoubleCountedExpenseQuery(fromDate, toDate),
            ReconciliationDiscrepancyCategory.PossiblyDoubleCountedCustoms => DoubleCountedCustomsQuery(fromDate, toDate),
            ReconciliationDiscrepancyCategory.NegativeStock => NegativeStockQuery(),
            ReconciliationDiscrepancyCategory.OverDelivery => OverDeliveryQuery(fromDate, toDate),
            ReconciliationDiscrepancyCategory.DeliveryWithoutPreSale => DeliveryWithoutPreSaleQuery(fromDate, toDate),
            ReconciliationDiscrepancyCategory.ReservationExceedsStock => ReservationExceedsStockQuery(fromDate, toDate),
            ReconciliationDiscrepancyCategory.OverduePreSaleCommitment => OverduePreSaleQuery(fromDate, toDate),
            ReconciliationDiscrepancyCategory.UnallocatedPayment => UnallocatedPaymentQuery(fromDate, toDate),
            ReconciliationDiscrepancyCategory.UnconfirmedSarrafSettlement => UnconfirmedSarrafQuery(fromDate, toDate),
            ReconciliationDiscrepancyCategory.OperationalDocumentWithoutLedger => OperationalDocWithoutLedgerQuery(fromDate, toDate),
            ReconciliationDiscrepancyCategory.IncompleteContractOrShipmentLineage => IncompleteLineageQuery(fromDate, toDate),
            ReconciliationDiscrepancyCategory.IncompleteCustomsDocument => IncompleteCustomsQuery(fromDate, toDate),
            ReconciliationDiscrepancyCategory.QualityInspectionPending => QualityByStatusQuery(QualityInspectionStatus.Pending, fromDate, toDate),
            ReconciliationDiscrepancyCategory.QualityInspectionRejected => QualityByStatusQuery(QualityInspectionStatus.Rejected, fromDate, toDate),
            ReconciliationDiscrepancyCategory.QualityInspectionWithoutResultDocument => QualityWithoutDocumentQuery(fromDate, toDate),
            // عمداً پرتاب می‌شود: اگر دسته‌ای به enum اضافه شود ولی query نگیرد، نباید
            // بی‌صدا صفر شمرده شود و UI/Export/تست را با هم ناسازگار کند.
            _ => throw new ArgumentOutOfRangeException(
                nameof(category), category, "این دستهٔ مغایرت query ندارد.")
        };
    }

    // ---------------------------------------------------------------- حسابداری

    // دفتر عملیاتی نام کوتاه می‌نویسد ("Expense"، "Sale"، "Loading") ولی دفتر حسابداری
    // نام موجودیت را می‌نویسد (nameof(ExpenseTransaction)، ...). مقایسهٔ مستقیم این دو
    // همیشه false می‌شد و عملاً همهٔ سطرهای دفتر کل «بدون سند» گزارش می‌شدند.
    // نگاشت زیر همان ثابت‌های آداپترهای حسابداری را می‌خواند تا تعریف موازی ساخته نشود.
    private const string LedgerSourceExpense = "Expense";
    private const string LedgerSourceSale = "Sale";
    private const string LedgerSourceLoading = SupplierLoadingLedger.SourceType;                  // "Loading"
    private const string LedgerSourceShortageCharge = "ShortageCharge";
    private const string LedgerSourceSarrafSettlement = SarrafSettlementService.SupplierLedgerSourceType;
    private const string LedgerSourceThreeWaySettlement = ThreeWaySettlementController.LedgerSourceType;
    private const string LedgerSourceContractBalanceTransfer = ContractBalanceTransferService.LedgerSourceType;
    private const string LedgerSourceSupplierPaymentAllocation = SupplierPaymentAllocationService.LedgerSourceType;
    private const string LedgerSourceViaSarrafPayment = PaymentsController.ViaSarrafSupplierLedgerSourceType;
    private const string LedgerSourceViaSarrafPayable = PaymentsController.ViaSarrafPayableLedgerSourceType;

    private const string JournalExpense = ExpenseAccountingAdapter.SourceEntityType;              // ExpenseTransaction
    private const string JournalSale = SalesAccountingAdapter.SourceEntityType;                   // SalesTransaction
    private const string JournalPurchase = PurchaseAccountingAdapter.PurchaseSourceEntityType;    // LoadingRegister
    private const string JournalShortageCharge = ShortageChargeAccountingAdapter.SourceEntityType;// InventoryTransportReceipt
    private const string JournalSarrafSettlement = SarrafSettlementAccountingAdapter.SourceEntityType;
    private const string JournalThreeWaySettlement = ThreeWaySettlementAccountingAdapter.SourceEntityType;
    private const string JournalContractBalanceTransfer = ContractBalanceTransferAccountingAdapter.SourceEntityType;
    private const string JournalSupplierPaymentAllocation = SupplierPaymentAllocationAccountingAdapter.SourceEntityType;
    private const string JournalViaSarraf = ViaSarrafAccountingAdapter.SourceEntityType;          // LedgerEntry

    /// <summary>
    /// همان نگاشت، به‌صورت جفتِ خوانا برای تست. هر جفت یعنی «سطر دفتر کل با این
    /// SourceType باید سند حسابداری با این SourceEntityType داشته باشد».
    /// </summary>
    public static IReadOnlyList<(string LedgerSourceType, string JournalSourceEntityType)> LedgerToJournalSourceMap { get; } =
    [
        (LedgerSourceExpense, JournalExpense),
        (LedgerSourceSale, JournalSale),
        (LedgerSourceLoading, JournalPurchase),
        (LedgerSourceShortageCharge, JournalShortageCharge),
        (LedgerSourceSarrafSettlement, JournalSarrafSettlement),
        (LedgerSourceThreeWaySettlement, JournalThreeWaySettlement),
        (LedgerSourceContractBalanceTransfer, JournalContractBalanceTransfer),
        (LedgerSourceSupplierPaymentAllocation, JournalSupplierPaymentAllocation),
        (LedgerSourceViaSarrafPayment, JournalViaSarraf),
        (LedgerSourceViaSarrafPayable, JournalViaSarraf)
    ];

    private IQueryable<ReconciliationDiscrepancyRow> LedgerWithoutJournalQuery(DateTime? from, DateTime? to)
    {
        var query = _db.LedgerEntries.AsNoTracking()
            // فقط سطرهایی که واقعاً باید سند حسابداری مستقل داشته باشند. سطرهای مشتق
            // (برگشت، تفاوت نرخ، لغو، مانده افتتاحیه، سود تبادله) سند جدا ندارند و
            // شمردن‌شان false positive بود.
            .Where(l => l.SourceType == LedgerSourceExpense
                || l.SourceType == LedgerSourceSale
                || l.SourceType == LedgerSourceLoading
                || l.SourceType == LedgerSourceShortageCharge
                || l.SourceType == LedgerSourceSarrafSettlement
                || l.SourceType == LedgerSourceThreeWaySettlement
                || l.SourceType == LedgerSourceContractBalanceTransfer
                || l.SourceType == LedgerSourceSupplierPaymentAllocation
                || l.SourceType == LedgerSourceViaSarrafPayment
                || l.SourceType == LedgerSourceViaSarrafPayable)
            .Where(l => !_db.JournalEntries.Any(j =>
                j.SourceEntityType == (
                    l.SourceType == LedgerSourceExpense ? JournalExpense
                    : l.SourceType == LedgerSourceSale ? JournalSale
                    : l.SourceType == LedgerSourceLoading ? JournalPurchase
                    : l.SourceType == LedgerSourceShortageCharge ? JournalShortageCharge
                    : l.SourceType == LedgerSourceSarrafSettlement ? JournalSarrafSettlement
                    : l.SourceType == LedgerSourceThreeWaySettlement ? JournalThreeWaySettlement
                    : l.SourceType == LedgerSourceContractBalanceTransfer ? JournalContractBalanceTransfer
                    : l.SourceType == LedgerSourceSupplierPaymentAllocation ? JournalSupplierPaymentAllocation
                    : JournalViaSarraf)
                // سند «از طریق صراف» به خودِ سطر دفتر کل وصل می‌شود، نه به سند منبع آن.
                && j.SourceEntityId == (
                    l.SourceType == LedgerSourceViaSarrafPayment || l.SourceType == LedgerSourceViaSarrafPayable
                        ? l.Id
                        : l.SourceId)));

        if (from.HasValue) query = query.Where(l => l.EntryDate >= from.Value);
        if (to.HasValue) query = query.Where(l => l.EntryDate < to.Value);

        return query
            .OrderByDescending(l => l.EntryDate)
            .ThenByDescending(l => l.Id)
            .Select(l => new ReconciliationDiscrepancyRow
            {
                Reference = l.Reference ?? (l.SourceType + " #" + l.SourceId.ToString()),
                Date = l.EntryDate,
                AmountUsd = l.AmountUsd,
                Detail = "ثبت دفتر کل بدون سند حسابداری متناظر",
                DrillDownController = "",
                DrillDownAction = "",
                DrillDownId = l.Id
            });
    }

    private IQueryable<ReconciliationDiscrepancyRow> JournalWithoutSourceQuery(DateTime? from, DateTime? to)
    {
        var query = _db.JournalEntries.AsNoTracking()
            .Where(j => !j.IsOpening && !j.IsClosing && !j.IsAdjustment && !j.IsReversal)
            .Where(j => j.SourceEntityId == null || j.SourceEntityType == null || j.SourceEntityType == "");

        if (from.HasValue) query = query.Where(j => j.AccountingDate >= from.Value);
        if (to.HasValue) query = query.Where(j => j.AccountingDate < to.Value);

        return query
            .OrderByDescending(j => j.AccountingDate)
            .ThenByDescending(j => j.Id)
            .Select(j => new ReconciliationDiscrepancyRow
            {
                Reference = j.JournalNumber,
                Date = j.AccountingDate,
                AmountUsd = j.Lines.Sum(x => x.Debit),
                Detail = "سند حسابداری بدون ارجاع به سند عملیاتی",
                DrillDownController = "JournalEntries",
                DrillDownAction = "Details",
                DrillDownId = j.Id
            });
    }

    private IQueryable<ReconciliationDiscrepancyRow> UnbalancedJournalQuery(DateTime? from, DateTime? to)
    {
        var query = _db.JournalEntries.AsNoTracking()
            .Where(j => j.Lines.Sum(x => x.Debit) != j.Lines.Sum(x => x.Credit));

        if (from.HasValue) query = query.Where(j => j.AccountingDate >= from.Value);
        if (to.HasValue) query = query.Where(j => j.AccountingDate < to.Value);

        return query
            .OrderByDescending(j => j.AccountingDate)
            .ThenByDescending(j => j.Id)
            .Select(j => new ReconciliationDiscrepancyRow
            {
                Reference = j.JournalNumber,
                Date = j.AccountingDate,
                AmountUsd = j.Lines.Sum(x => x.Debit) - j.Lines.Sum(x => x.Credit),
                Detail = "جمع بدهکار و بستانکار سند برابر نیست",
                DrillDownController = "JournalEntries",
                DrillDownAction = "Details",
                DrillDownId = j.Id
            });
    }

    // ---------------------------------------------------------------- فروش

    private IQueryable<ReconciliationDiscrepancyRow> SaleWithoutCogsQuery(DateTime? from, DateTime? to)
    {
        // منبع یکتای بهای تمام‌شده: SalesCostConsumption فعال. اگر نبود، سود این فروش
        // ساختگی است و باید در گزارش دیده شود.
        var query = _db.SalesTransactions.AsNoTracking()
            .Where(s => !s.IsCancelled)
            .Where(s => !_db.SalesCostConsumptions.Any(c =>
                c.SalesTransactionId == s.Id && c.Status == SalesCostConsumptionStatus.Active));

        if (from.HasValue) query = query.Where(s => s.SaleDate >= from.Value);
        if (to.HasValue) query = query.Where(s => s.SaleDate < to.Value);

        return query
            .OrderByDescending(s => s.SaleDate)
            .ThenByDescending(s => s.Id)
            .Select(s => new ReconciliationDiscrepancyRow
            {
                Reference = s.InvoiceNumber ?? ("SAL-" + s.Id.ToString()),
                Date = s.SaleDate,
                AmountUsd = s.TotalUsd,
                QuantityMt = s.QuantityMt,
                Detail = "فروش بدون مصرف بهای تمام‌شدهٔ فعال",
                DrillDownController = "Sales",
                DrillDownAction = "Details",
                DrillDownId = s.Id
            });
    }

    private IQueryable<ReconciliationDiscrepancyRow> SaleWithoutSourceQuery(DateTime? from, DateTime? to)
    {
        var query = _db.SalesTransactions.AsNoTracking()
            .Where(s => !s.IsCancelled)
            .Where(s => s.ContractId == null
                && s.SourcePurchaseContractId == null
                && !_db.InventoryMovements.Any(m => m.SalesTransactionId == s.Id));

        if (from.HasValue) query = query.Where(s => s.SaleDate >= from.Value);
        if (to.HasValue) query = query.Where(s => s.SaleDate < to.Value);

        return query
            .OrderByDescending(s => s.SaleDate)
            .ThenByDescending(s => s.Id)
            .Select(s => new ReconciliationDiscrepancyRow
            {
                Reference = s.InvoiceNumber ?? ("SAL-" + s.Id.ToString()),
                Date = s.SaleDate,
                AmountUsd = s.TotalUsd,
                QuantityMt = s.QuantityMt,
                Detail = "فروش بدون قرارداد و بدون حرکت موجودی",
                DrillDownController = "Sales",
                DrillDownAction = "Details",
                DrillDownId = s.Id
            });
    }

    private IQueryable<ReconciliationDiscrepancyRow> DoubleCountedExpenseQuery(DateTime? from, DateTime? to)
    {
        // «احتمال» ثبت دوباره: همان نوع مصرف، همان تاریخ، همان مبلغ و همان قرارداد.
        // این فقط نشانه است؛ هیچ رکوردی حذف یا اصلاح نمی‌شود.
        var query = _db.ExpenseTransactions.AsNoTracking()
            .Where(e => !e.IsCancelled)
            .Where(e => _db.ExpenseTransactions.Any(o =>
                o.Id != e.Id
                && !o.IsCancelled
                && o.ExpenseTypeId == e.ExpenseTypeId
                && o.ExpenseDate == e.ExpenseDate
                && o.AmountUsd == e.AmountUsd
                && o.ContractId == e.ContractId));

        if (from.HasValue) query = query.Where(e => e.ExpenseDate >= from.Value);
        if (to.HasValue) query = query.Where(e => e.ExpenseDate < to.Value);

        return query
            .OrderByDescending(e => e.ExpenseDate)
            .ThenByDescending(e => e.Id)
            .Select(e => new ReconciliationDiscrepancyRow
            {
                Reference = "EXP-" + e.Id.ToString(),
                Date = e.ExpenseDate,
                AmountUsd = e.AmountUsd,
                Detail = "مصرف با نوع، تاریخ، مبلغ و قرارداد یکسان تکرار شده است",
                DrillDownController = "Expenses",
                DrillDownAction = "Details",
                DrillDownId = e.Id
            });
    }

    private IQueryable<ReconciliationDiscrepancyRow> DoubleCountedCustomsQuery(DateTime? from, DateTime? to)
    {
        var query = _db.CustomsDeclarations.AsNoTracking()
            .Where(c => c.DeclarationReference != null && c.DeclarationReference != "")
            .Where(c => _db.CustomsDeclarations.Any(o =>
                o.Id != c.Id && o.DeclarationReference == c.DeclarationReference));

        if (from.HasValue) query = query.Where(c => c.DeclarationDate >= from.Value);
        if (to.HasValue) query = query.Where(c => c.DeclarationDate < to.Value);

        return query
            .OrderByDescending(c => c.DeclarationDate)
            .ThenByDescending(c => c.Id)
            .Select(c => new ReconciliationDiscrepancyRow
            {
                Reference = c.DeclarationReference ?? "",
                Date = c.DeclarationDate,
                AmountUsd = c.TotalUsd,
                QuantityMt = c.ConsignmentWeightMt,
                Detail = "شمارهٔ اظهارنامهٔ گمرکی تکراری است",
                DrillDownController = "CustomsDeclarations",
                DrillDownAction = "Details",
                DrillDownId = c.Id
            });
    }

    // ---------------------------------------------------------------- موجودی

    private IQueryable<ReconciliationDiscrepancyRow> NegativeStockQuery()
    {
        // دقیقاً همان قرارداد علامت که StockService استفاده می‌کند: In/Adjustment مثبت،
        // Out/Transfer منفی. هیچ فرمول موازی ساخته نشده است.
        return _db.InventoryMovements.AsNoTracking()
            .GroupBy(m => new { m.TerminalId, m.StorageTankId, m.ProductId })
            .Select(g => new
            {
                g.Key.TerminalId,
                g.Key.StorageTankId,
                g.Key.ProductId,
                QuantityMt = g.Sum(m =>
                    m.Direction == MovementDirection.In || m.Direction == MovementDirection.Adjustment
                        ? m.QuantityMt
                        : m.Direction == MovementDirection.Out || m.Direction == MovementDirection.Transfer
                            ? -m.QuantityMt
                            : 0m)
            })
            .Where(x => x.QuantityMt < 0m)
            .OrderBy(x => x.QuantityMt)
            .Select(x => new ReconciliationDiscrepancyRow
            {
                Reference = (_db.Products.Where(p => p.Id == x.ProductId).Select(p => p.Name).FirstOrDefault() ?? "")
                    + " @ "
                    + (_db.Terminals.Where(t => t.Id == x.TerminalId).Select(t => t.Name).FirstOrDefault() ?? "-"),
                QuantityMt = x.QuantityMt,
                Detail = "مانده موجودی این ترکیب ترمینال/مخزن/جنس منفی است",
                DrillDownController = "Stock",
                DrillDownAction = "Index",
                DrillDownId = x.ProductId
            });
    }

    // ---------------------------------------------------------------- پیش‌فروش

    private IQueryable<ReconciliationDiscrepancyRow> OverDeliveryQuery(DateTime? from, DateTime? to)
    {
        var query = _db.PreSaleOrders.AsNoTracking()
            .Where(o => o.Status != PreSaleOrderStatus.Cancelled)
            .Where(o => _db.SalesTransactions
                .Where(s => s.PreSaleOrderId == o.Id && !s.IsCancelled)
                .Sum(s => s.QuantityMt) > o.QuantityMt);

        if (from.HasValue) query = query.Where(o => o.OrderDate >= from.Value);
        if (to.HasValue) query = query.Where(o => o.OrderDate < to.Value);

        return query
            .OrderByDescending(o => o.OrderDate)
            .ThenByDescending(o => o.Id)
            .Select(o => new ReconciliationDiscrepancyRow
            {
                Reference = o.OrderNumber,
                Date = o.OrderDate,
                AmountUsd = o.TotalUsd,
                QuantityMt = _db.SalesTransactions
                    .Where(s => s.PreSaleOrderId == o.Id && !s.IsCancelled)
                    .Sum(s => s.QuantityMt) - o.QuantityMt,
                Detail = "مقدار تحویل‌شده از تعهد پیش‌فروش بیشتر است",
                DrillDownController = "Sales",
                DrillDownAction = "PreSaleDetails",
                DrillDownId = o.Id
            });
    }

    private IQueryable<ReconciliationDiscrepancyRow> DeliveryWithoutPreSaleQuery(DateTime? from, DateTime? to)
    {
        var query = _db.SalesTransactions.AsNoTracking()
            .Where(s => !s.IsCancelled && s.SaleStage == SaleStage.PreSale && s.PreSaleOrderId == null);

        if (from.HasValue) query = query.Where(s => s.SaleDate >= from.Value);
        if (to.HasValue) query = query.Where(s => s.SaleDate < to.Value);

        return query
            .OrderByDescending(s => s.SaleDate)
            .ThenByDescending(s => s.Id)
            .Select(s => new ReconciliationDiscrepancyRow
            {
                Reference = s.InvoiceNumber ?? ("SAL-" + s.Id.ToString()),
                Date = s.SaleDate,
                AmountUsd = s.TotalUsd,
                QuantityMt = s.QuantityMt,
                Detail = "فروش در مرحلهٔ پیش‌فروش ثبت شده اما به هیچ سفارش پیش‌فروشی وصل نیست",
                DrillDownController = "Sales",
                DrillDownAction = "Details",
                DrillDownId = s.Id
            });
    }

    private IQueryable<ReconciliationDiscrepancyRow> ReservationExceedsStockQuery(DateTime? from, DateTime? to)
    {
        var query = _db.PreSaleOrders.AsNoTracking()
            .Where(o => o.Status == PreSaleOrderStatus.Confirmed
                || o.Status == PreSaleOrderStatus.PartiallyDelivered)
            .Where(o => o.QuantityMt - _db.SalesTransactions
                    .Where(s => s.PreSaleOrderId == o.Id && !s.IsCancelled)
                    .Sum(s => s.QuantityMt)
                > _db.InventoryMovements
                    .Where(m => m.ProductId == o.ProductId)
                    .Sum(m =>
                        m.Direction == MovementDirection.In || m.Direction == MovementDirection.Adjustment
                            ? m.QuantityMt
                            : m.Direction == MovementDirection.Out || m.Direction == MovementDirection.Transfer
                                ? -m.QuantityMt
                                : 0m));

        if (from.HasValue) query = query.Where(o => o.OrderDate >= from.Value);
        if (to.HasValue) query = query.Where(o => o.OrderDate < to.Value);

        return query
            .OrderByDescending(o => o.OrderDate)
            .ThenByDescending(o => o.Id)
            .Select(o => new ReconciliationDiscrepancyRow
            {
                Reference = o.OrderNumber,
                Date = o.OrderDate,
                AmountUsd = o.TotalUsd,
                QuantityMt = o.QuantityMt - _db.SalesTransactions
                    .Where(s => s.PreSaleOrderId == o.Id && !s.IsCancelled)
                    .Sum(s => s.QuantityMt),
                Detail = "باقیماندهٔ تعهد این سفارش از کل موجودی همان جنس بیشتر است",
                DrillDownController = "Sales",
                DrillDownAction = "PreSaleDetails",
                DrillDownId = o.Id
            });
    }

    private IQueryable<ReconciliationDiscrepancyRow> OverduePreSaleQuery(DateTime? from, DateTime? to)
    {
        // مرجع «امروز» ساعت کاری کابل است، نه تاریخ UTC سرور.
        var businessToday = _businessClock.Today;

        var query = _db.PreSaleOrders.AsNoTracking()
            .Where(o => o.Status == PreSaleOrderStatus.Confirmed
                || o.Status == PreSaleOrderStatus.PartiallyDelivered)
            .Where(o => o.ExpectedDeliveryTo != null && o.ExpectedDeliveryTo < businessToday)
            .Where(o => _db.SalesTransactions
                .Where(s => s.PreSaleOrderId == o.Id && !s.IsCancelled)
                .Sum(s => s.QuantityMt) < o.QuantityMt);

        if (from.HasValue) query = query.Where(o => o.OrderDate >= from.Value);
        if (to.HasValue) query = query.Where(o => o.OrderDate < to.Value);

        return query
            .OrderBy(o => o.ExpectedDeliveryTo)
            .ThenBy(o => o.Id)
            .Select(o => new ReconciliationDiscrepancyRow
            {
                Reference = o.OrderNumber,
                Date = o.ExpectedDeliveryTo,
                AmountUsd = o.TotalUsd,
                QuantityMt = o.QuantityMt - _db.SalesTransactions
                    .Where(s => s.PreSaleOrderId == o.Id && !s.IsCancelled)
                    .Sum(s => s.QuantityMt),
                Detail = "مهلت تحویل گذشته و تعهد هنوز کامل تحویل نشده است",
                DrillDownController = "Sales",
                DrillDownAction = "PreSaleDetails",
                DrillDownId = o.Id
            });
    }

    // ---------------------------------------------------------------- پول

    private IQueryable<ReconciliationDiscrepancyRow> UnallocatedPaymentQuery(DateTime? from, DateTime? to)
    {
        var query = _db.PaymentTransactions.AsNoTracking()
            .Where(p => p.SalesTransactionId == null
                && p.ExpenseTransactionId == null
                && p.ContractId == null
                && !_db.CustomerPaymentAllocations.Any(a =>
                    a.PaymentTransactionId == p.Id && a.ReversedAtUtc == null));

        if (from.HasValue) query = query.Where(p => p.PaymentDate >= from.Value);
        if (to.HasValue) query = query.Where(p => p.PaymentDate < to.Value);

        return query
            .OrderByDescending(p => p.PaymentDate)
            .ThenByDescending(p => p.Id)
            .Select(p => new ReconciliationDiscrepancyRow
            {
                Reference = p.Reference ?? ("PAY-" + p.Id.ToString()),
                Date = p.PaymentDate,
                AmountUsd = p.AmountUsd,
                Detail = "پرداخت به هیچ فروش، مصرف، قرارداد یا تخصیص فعالی وصل نیست",
                DrillDownController = "Payments",
                DrillDownAction = "Details",
                DrillDownId = p.Id
            });
    }

    private IQueryable<ReconciliationDiscrepancyRow> UnconfirmedSarrafQuery(DateTime? from, DateTime? to)
    {
        var query = _db.SarrafSettlements.AsNoTracking()
            .Where(s => s.Status == SarrafSettlementStatus.Draft
                || (s.Status == SarrafSettlementStatus.Posted && s.LedgerEntryId == null));

        if (from.HasValue) query = query.Where(s => s.SettlementDate >= from.Value);
        if (to.HasValue) query = query.Where(s => s.SettlementDate < to.Value);

        return query
            .OrderByDescending(s => s.SettlementDate)
            .ThenByDescending(s => s.Id)
            .Select(s => new ReconciliationDiscrepancyRow
            {
                Reference = s.ReferenceNumber ?? ("SRF-" + s.Id.ToString()),
                Date = s.SettlementDate,
                AmountUsd = s.RequestedAmountUsd,
                Detail = s.Status == SarrafSettlementStatus.Draft
                    ? "حوالهٔ صراف هنوز تأیید و ثبت نهایی نشده است"
                    : "حواله ثبت شده اما به دفتر کل منتقل نشده است",
                DrillDownController = "SarrafSettlements",
                DrillDownAction = "Details",
                DrillDownId = s.Id
            });
    }

    // ---------------------------------------------------------------- عملیات

    private IQueryable<ReconciliationDiscrepancyRow> OperationalDocWithoutLedgerQuery(DateTime? from, DateTime? to)
    {
        // پای حمل با کرایهٔ ثبت‌شده که هیچ سند مصرف مالی متناظری ندارد.
        var query = _db.InventoryTransportLegs.AsNoTracking()
            .Where(l => l.FreightAmount > 0m)
            .Where(l => !_db.ExpenseTransactions.Any(e => e.TransportLegId == l.Id && !e.IsCancelled));

        if (from.HasValue) query = query.Where(l => l.LoadedDate >= from.Value);
        if (to.HasValue) query = query.Where(l => l.LoadedDate < to.Value);

        return query
            .OrderByDescending(l => l.LoadedDate)
            .ThenByDescending(l => l.Id)
            .Select(l => new ReconciliationDiscrepancyRow
            {
                Reference = l.WagonNumber ?? l.RwbNo ?? ("LEG-" + l.Id.ToString()),
                Date = l.LoadedDate,
                QuantityMt = l.QuantityMt,
                Detail = "پای حمل کرایه دارد اما سند مصرف مالی برای آن ثبت نشده است",
                DrillDownController = "InventoryTransportLegs",
                DrillDownAction = "Details",
                DrillDownId = l.Id
            });
    }

    private IQueryable<ReconciliationDiscrepancyRow> IncompleteLineageQuery(DateTime? from, DateTime? to)
    {
        // SourcePurchaseContractId از نوع int است (nullable نیست)؛ حالتِ «تنظیم‌نشده»
        // یعنی صفر، نه null. مقایسه با null همیشه false بود و این نیمه از دسته را خاموش می‌کرد.
        var query = _db.InventoryTransportLegs.AsNoTracking()
            .Where(l => l.SourcePurchaseContractId <= 0 || l.ShipmentId == null);

        if (from.HasValue) query = query.Where(l => l.LoadedDate >= from.Value);
        if (to.HasValue) query = query.Where(l => l.LoadedDate < to.Value);

        return query
            .OrderByDescending(l => l.LoadedDate)
            .ThenByDescending(l => l.Id)
            .Select(l => new ReconciliationDiscrepancyRow
            {
                Reference = l.WagonNumber ?? l.RwbNo ?? ("LEG-" + l.Id.ToString()),
                Date = l.LoadedDate,
                QuantityMt = l.QuantityMt,
                Detail = l.SourcePurchaseContractId <= 0
                    ? "پای حمل بدون قرارداد خرید مبدأ"
                    : "پای حمل بدون محمولهٔ متصل",
                DrillDownController = "InventoryTransportLegs",
                DrillDownAction = "Details",
                DrillDownId = l.Id
            });
    }

    private IQueryable<ReconciliationDiscrepancyRow> IncompleteCustomsQuery(DateTime? from, DateTime? to)
    {
        var query = _db.CustomsDeclarations.AsNoTracking()
            .Where(c => c.DeclarationReference == null
                || c.DeclarationReference == ""
                || c.TotalUsd <= 0m
                || !c.Items.Any()
                || !c.Documents.Any());

        if (from.HasValue) query = query.Where(c => c.DeclarationDate >= from.Value);
        if (to.HasValue) query = query.Where(c => c.DeclarationDate < to.Value);

        return query
            .OrderByDescending(c => c.DeclarationDate)
            .ThenByDescending(c => c.Id)
            .Select(c => new ReconciliationDiscrepancyRow
            {
                Reference = c.DeclarationReference ?? ("CUS-" + c.Id.ToString()),
                Date = c.DeclarationDate,
                AmountUsd = c.TotalUsd,
                QuantityMt = c.ConsignmentWeightMt,
                Detail = !c.Documents.Any()
                    ? "اظهارنامهٔ گمرکی بدون فایل سند"
                    : !c.Items.Any()
                        ? "اظهارنامهٔ گمرکی بدون ردیف کالا"
                        : "اظهارنامهٔ گمرکی ناقص است",
                DrillDownController = "CustomsDeclarations",
                DrillDownAction = "Details",
                DrillDownId = c.Id
            });
    }

    // ---------------------------------------------------------------- کیفیت

    private IQueryable<ReconciliationDiscrepancyRow> QualityByStatusQuery(
        QualityInspectionStatus status,
        DateTime? from,
        DateTime? to)
    {
        var query = _db.QualityInspections.AsNoTracking().Where(q => q.Status == status);

        if (from.HasValue) query = query.Where(q => q.SampleDate >= from.Value);
        if (to.HasValue) query = query.Where(q => q.SampleDate < to.Value);

        return query
            .OrderByDescending(q => q.SampleDate)
            .ThenByDescending(q => q.Id)
            .Select(q => new ReconciliationDiscrepancyRow
            {
                Reference = q.ResultNumber ?? ("QI-" + q.Id.ToString()),
                Date = q.SampleDate,
                Detail = status == QualityInspectionStatus.Pending
                    ? "آزمایش کیفیت هنوز نتیجه ندارد"
                    : (q.RejectionReason ?? "آزمایش کیفیت رد شده است"),
                DrillDownController = "QualityInspections",
                DrillDownAction = "Details",
                DrillDownId = q.Id
            });
    }

    private IQueryable<ReconciliationDiscrepancyRow> QualityWithoutDocumentQuery(DateTime? from, DateTime? to)
    {
        var query = _db.QualityInspections.AsNoTracking()
            .Where(q => q.Status != QualityInspectionStatus.Pending && !q.Documents.Any());

        if (from.HasValue) query = query.Where(q => q.SampleDate >= from.Value);
        if (to.HasValue) query = query.Where(q => q.SampleDate < to.Value);

        return query
            .OrderByDescending(q => q.SampleDate)
            .ThenByDescending(q => q.Id)
            .Select(q => new ReconciliationDiscrepancyRow
            {
                Reference = q.ResultNumber ?? ("QI-" + q.Id.ToString()),
                Date = q.SampleDate,
                Detail = "آزمایش نتیجه دارد اما فایل سند نتیجه بارگذاری نشده است",
                DrillDownController = "QualityInspections",
                DrillDownAction = "Details",
                DrillDownId = q.Id
            });
    }
}
