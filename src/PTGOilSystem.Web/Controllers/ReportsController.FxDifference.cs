using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Helpers;
using PTGOilSystem.Web.Infrastructure.RateLimiting;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.Reports;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Exports;
using PTGOilSystem.Web.Services.PartyStatements;

namespace PTGOilSystem.Web.Controllers;

/// <summary>
/// گزارش «سود و ضرر تفاوت نرخ ارز» — فقط‌خواندنی.
///
/// تنها منبعِ مبلغِ سود/زیان، خودِ <see cref="SarrafSettlement"/> است:
/// <c>FxResultUsd = SupplierAcceptedAmountUsd − SarrafChargedAmountUsd</c>.
/// <see cref="ExpenseTransaction"/> و <see cref="LedgerEntry"/> فقط برای لینک، تطبیقِ صحت و
/// وضعیت خوانده می‌شوند و هیچ‌گاه دوباره جمع نمی‌شوند (جلوگیری از دوباره‌شماری).
///
/// توجه به علامت: در دیتابیس <c>DifferenceAmountUsd = CompanyCost − SupplierAccepted</c> است،
/// یعنی دقیقاً قرینهٔ <c>FxResultUsd</c>. این دو با هم Reconcile می‌شوند و در صورت تناقض ردیف
/// «نیازمند بررسی» می‌شود، نه اینکه یکی حدس زده شود.
/// </summary>
public partial class ReportsController
{
    private const int FxDifferencePageSize = 50;

    /// <summary>تلورانس مقایسهٔ مبالغ در Reconcile. محاسبات با دقت کامل decimal انجام می‌شود.</summary>
    internal const decimal FxDifferenceTolerance = 0.0001m;

    // نوعِ مصرفِ کمیسیون — همان مقادیری که PaymentsController.EnsureCommissionExpenseTypeAsync می‌سازد.
    private const string CommissionExpenseCode = "COMMISSION";
    private const string CommissionExpenseCategory = "Commission";
    private const string FxDifferenceExpenseCategory = "FxDifference";

    private static readonly Regex SettlementMarkerPattern = new(@"\[SS-(\d+)\]", RegexOptions.Compiled);

    [EnableRateLimiting(RateLimitPolicies.HeavyReport)]
    public async Task<IActionResult> FxDifference(
        [FromQuery] FxDifferenceReportFilterViewModel? filter = null,
        int page = 1,
        [FromQuery(Name = "pageSize")] int? perPage = null,
        CancellationToken cancellationToken = default)
    {
        filter ??= new FxDifferenceReportFilterViewModel();
        await PopulateFxDifferenceLookupsAsync(filter, cancellationToken);

        var pageSize = ListPageSize.Resolve(perPage, FxDifferencePageSize);
        ViewData["PageSize"] = pageSize;
        ViewData["DefaultPageSize"] = FxDifferencePageSize;

        return View(await BuildFxDifferenceReportAsync(filter, page, paginate: true, cancellationToken, pageSize));
    }

    [HttpGet]
    [EnableRateLimiting(RateLimitPolicies.CsvExport)]
    public async Task<IActionResult> FxDifferenceExport(
        string? format,
        [FromQuery] FxDifferenceReportFilterViewModel? filter = null,
        CancellationToken cancellationToken = default)
    {
        filter ??= new FxDifferenceReportFilterViewModel();
        // paginate: false — خروجی تمام نتایج فیلترشده است، نه فقط صفحهٔ جاری.
        var model = await BuildFxDifferenceReportAsync(filter, page: 1, paginate: false, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        return TabularExportSupport.File(this, format, new TabularExportDocument
        {
            FileNameStem = "PTG_FX_Difference",
            TitleFa = "گزارش سود و ضرر تفاوت نرخ ارز",
            TitleEn = "FX Gain / Loss Report",
            KnownRowCount = model.Rows.Count,
            ForceLandscape = true,
            Filters = BuildFxDifferenceExportFilters(model.Filter),
            Columns =
            [
                new("تاریخ", "Date", TabularExportValueType.Date, 13),
                new("شماره رسید", "Reference", Width: 14),
                new("شرکت قرارداد", "Contract company", Width: 18),
                new("تأمین‌کننده", "Supplier", Width: 18),
                new("صراف", "Sarraf", Width: 16),
                new("قرارداد", "Contract", Width: 16),
                new("ارز", "Currency", Width: 9),
                new("مبلغ اصلی", "Original amount", TabularExportValueType.Number, 18),
                new("نرخ شرکت", "Company rate", TabularExportValueType.Number, 13),
                new("نرخ تأمین‌کننده", "Supplier rate", TabularExportValueType.Number, 15),
                new("هزینه واقعی شرکت USD", "Company cost USD", TabularExportValueType.Number, 19),
                new("قبول تأمین‌کننده USD", "Supplier accepted USD", TabularExportValueType.Number, 19),
                new("خالص تفاوت USD", "Net FX result USD", TabularExportValueType.Number, 17),
                new("وضعیت تطبیق FX", "FX reconcile status", Width: 16),
                new("سود تفاوت نرخ USD", "FX gain USD", TabularExportValueType.Number, 17),
                new("ضرر تفاوت نرخ USD", "FX loss USD", TabularExportValueType.Number, 17),
                new("کمیشن صراف USD", "Sarraf commission USD", TabularExportValueType.Number, 17),
                new("وضعیت تطبیق کمیشن", "Commission match status", Width: 18),
                new("وضعیت تسویه", "Settlement status", Width: 13),
                new("توضیحات", "Description", Width: 30, Wrap: true),
                new("کاربر ثبت‌کننده", "Created by", Width: 16),
                new("تاریخ ثبت", "Created at", TabularExportValueType.DateTime, 16)
            ],
            Rows = model.Rows.Select(r => new TabularExportRow(
            [
                TabularExportCell.Date(r.SettlementDate),
                TabularExportCell.Text(r.ReferenceNumber),
                TabularExportCell.Text(r.CompanyName ?? "—"),
                TabularExportCell.Text(r.SupplierName ?? "—"),
                TabularExportCell.Text(r.SarrafName),
                TabularExportCell.Text(r.ContractNumber ?? "—"),
                TabularExportCell.Text(r.Currency),
                TabularExportCell.Number(r.Amount),
                TabularExportCell.Number(r.CompanyRate),
                TabularExportCell.Number(r.SupplierRate),
                TabularExportCell.Number(r.CompanyCostUsd),
                TabularExportCell.Number(r.SupplierAcceptedUsd),
                TabularExportCell.Number(r.FxResultUsd),
                TabularExportCell.Text(FxResultLabel(r.Result)),
                TabularExportCell.Number(r.GainUsd),
                TabularExportCell.Number(r.LossUsd),
                TabularExportCell.Number(r.CommissionUsd),
                TabularExportCell.Text(FxCommissionLabel(r.CommissionStatus)),
                TabularExportCell.Text(SarrafSettlementStatusLabel(r.Status)),
                TabularExportCell.Text(r.Description),
                TabularExportCell.Text(r.CreatedByUserName ?? "—"),
                TabularExportCell.DateTime(r.CreatedAtUtc)
            ])),
            // ردیف جمع کل — همان اعدادی که کارت‌های بالای گزارش نشان می‌دهند.
            Totals = new TabularExportRow(
            [
                TabularExportCell.Text("جمع / Total"),
                TabularExportCell.Text(null), TabularExportCell.Text(null), TabularExportCell.Text(null),
                TabularExportCell.Text(null), TabularExportCell.Text(null), TabularExportCell.Text(null),
                TabularExportCell.Text(null), TabularExportCell.Text(null), TabularExportCell.Text(null),
                TabularExportCell.Number(model.Totals.TotalCompanyCostUsd),
                TabularExportCell.Number(model.Totals.TotalSupplierAcceptedUsd),
                TabularExportCell.Number(model.Totals.NetFxResultUsd),
                TabularExportCell.Text(null),
                TabularExportCell.Number(model.Totals.TotalGainUsd),
                TabularExportCell.Number(model.Totals.TotalLossUsd),
                TabularExportCell.Number(model.Totals.TotalCommissionUsd),
                TabularExportCell.Text(null), TabularExportCell.Text(null), TabularExportCell.Text(null),
                TabularExportCell.Text(null), TabularExportCell.DateTime(null)
            ])
        });
    }

    private static IReadOnlyList<TabularExportFilter> BuildFxDifferenceExportFilters(FxDifferenceReportFilterViewModel filter)
        => TabularExportSupport.FilterSummary(
            ("از تاریخ / From", filter.FromDate?.ToString("yyyy-MM-dd")),
            ("تا تاریخ / To", filter.ToDate?.ToString("yyyy-MM-dd")),
            ("شرکت قرارداد / Contract company", filter.CompanyId),
            ("تأمین‌کننده / Supplier", filter.SupplierId),
            ("صراف / Sarraf", filter.SarrafId),
            ("قرارداد / Contract", filter.ContractId),
            ("ارز / Currency", filter.Currency),
            ("رسید / Reference", filter.Reference),
            ("نتیجه / Result", filter.Result == FxDifferenceResultFilter.All ? null : FxResultFilterLabel(filter.Result)),
            ("وضعیت / Status", filter.Status.HasValue ? SarrafSettlementStatusLabel(filter.Status.Value) : null),
            ("کمیشن / Commission", filter.Commission == FxCommissionFilter.All ? null : FxCommissionFilterLabel(filter.Commission)));

    // ===================== ساخت گزارش =====================

    /// <summary>مقادیر خامِ لازم برای یک ردیف. هیچ محاسبهٔ مالی در SQL انجام نمی‌شود.</summary>
    private sealed record FxSettlementProjection(
        int Id,
        DateTime SettlementDate,
        string? ReferenceNumber,
        string? Description,
        SarrafSettlementStatus Status,
        SarrafSettlementDirection Direction,
        int SarrafId,
        string SarrafName,
        int? SupplierId,
        string? SupplierName,
        int? ContractId,
        string? ContractNumber,
        string? CompanyName,
        string Currency,
        decimal Amount,
        decimal CompanyRate,
        decimal? SupplierRate,
        decimal CompanyCostUsd,
        decimal SupplierAcceptedUsd,
        decimal StoredDifferenceAmountUsd,
        SarrafSettlementDifferenceType StoredDifferenceType,
        int? LedgerEntryId,
        int? ExchangeDifferenceLedgerEntryId,
        int? PaymentTransactionId,
        int? CashAccountId,
        int? CreatedByUserId,
        DateTime CreatedAtUtc);

    private IQueryable<SarrafSettlement> BuildFxDifferenceQuery(FxDifferenceReportFilterViewModel filter)
    {
        // فقط تسویه‌هایی که دلیل تفاوتشان «تفاوت نرخ ارز» است و هر دو نرخ را دارند.
        // وضعیت عمداً hard-code نمی‌شود؛ انتخاب آن با کاربر است.
        var query = _db.SarrafSettlements
            .AsNoTracking()
            .Where(s => s.DifferenceReason == DifferenceReason.FxDifference
                && s.SupplierRate != null
                && s.SarrafRate > 0m);

        if (filter.FromDate.HasValue) query = query.Where(s => s.SettlementDate >= filter.FromDate.Value.Date);
        if (filter.ToDate.HasValue) query = query.Where(s => s.SettlementDate <= filter.ToDate.Value.Date);
        if (filter.Status.HasValue) query = query.Where(s => s.Status == filter.Status.Value);
        if (filter.SarrafId.HasValue) query = query.Where(s => s.SarrafId == filter.SarrafId.Value);
        if (filter.SupplierId.HasValue) query = query.Where(s => s.SupplierId == filter.SupplierId.Value);
        if (filter.ContractId.HasValue) query = query.Where(s => s.ContractId == filter.ContractId.Value);

        // شرکت فقط از مسیر قرارداد در دسترس است؛ تسویه خودش CompanyId ندارد.
        if (filter.CompanyId.HasValue)
        {
            query = query.Where(s => s.Contract != null && s.Contract.CompanyId == filter.CompanyId.Value);
        }

        if (!string.IsNullOrWhiteSpace(filter.Currency))
        {
            var currency = SystemCurrency.Normalize(filter.Currency);
            query = query.Where(s => s.SarrafCurrency == currency);
        }

        if (!string.IsNullOrWhiteSpace(filter.Reference))
        {
            var reference = filter.Reference.Trim();
            query = query.Where(s => s.ReferenceNumber != null && s.ReferenceNumber.Contains(reference));
        }

        return query;
    }

    /// <summary>internal است تا تست بتواند همان اعدادِ گزارش و خروجی را مستقیم بسنجد.</summary>
    internal async Task<FxDifferenceReportViewModel> BuildFxDifferenceReportAsync(
        FxDifferenceReportFilterViewModel filter,
        int page,
        bool paginate,
        CancellationToken cancellationToken,
        int pageSize = FxDifferencePageSize)
    {
        var settlements = await BuildFxDifferenceQuery(filter)
            .OrderByDescending(s => s.SettlementDate)
            .ThenByDescending(s => s.Id)
            .Select(s => new FxSettlementProjection(
                s.Id,
                s.SettlementDate,
                s.ReferenceNumber,
                s.Description,
                s.Status,
                s.Direction,
                s.SarrafId,
                s.Sarraf != null ? s.Sarraf.Name : "",
                s.SupplierId,
                s.Supplier != null ? s.Supplier.Name : null,
                s.ContractId,
                s.Contract != null ? s.Contract.ContractNumber : null,
                s.Contract != null && s.Contract.Company != null ? s.Contract.Company.Name : null,
                s.SarrafCurrency,
                s.SarrafChargedAmount,
                s.SarrafRate,
                s.SupplierRate,
                s.SarrafChargedAmountUsd,
                s.SupplierAcceptedAmountUsd,
                s.DifferenceAmountUsd,
                s.DifferenceType,
                s.LedgerEntryId,
                s.ExchangeDifferenceLedgerEntryId,
                s.PaymentTransactionId,
                s.CashAccountId,
                s.CreatedByUserId,
                s.CreatedAtUtc))
            .ToListAsync(cancellationToken);

        var links = await LoadFxDifferenceLinksAsync(settlements, cancellationToken);

        var allRows = settlements.Select(s => BuildFxDifferenceRow(s, links)).ToList();

        // فیلترهای محاسباتی (نتیجه و کمیشن) بعد از ساخت ردیف اعمال می‌شوند تا با همان
        // منطقی بخورند که در ستون‌ها و کارت‌ها دیده می‌شود.
        var rows = allRows
            .Where(r => MatchesResultFilter(r, filter.Result))
            .Where(r => MatchesCommissionFilter(r, filter.Commission))
            .ToList();

        // کارت‌ها از کل نتایج فیلترشده ساخته می‌شوند — قبل از صفحه‌بندی.
        var totals = BuildFxDifferenceTotals(rows);

        var pageCount = paginate
            ? Math.Max(1, (int)Math.Ceiling(rows.Count / (double)pageSize))
            : 1;
        var currentPage = paginate ? Math.Clamp(page, 1, pageCount) : 1;
        var pageRows = paginate
            ? rows.Skip((currentPage - 1) * pageSize).Take(pageSize).ToList()
            : rows;

        return new FxDifferenceReportViewModel
        {
            Filter = filter,
            Rows = pageRows,
            Totals = totals,
            CurrentPage = currentPage,
            PageCount = pageCount,
            TotalRowCount = rows.Count
        };
    }

    /// <summary>
    /// لینک‌های تطبیقی: مصرفِ ضرر، سطرِ سودِ دفتر، و کمیشن. هیچ مبلغی از این‌ها وارد
    /// جمع سود/زیان نمی‌شود؛ فقط برای وضعیت، لینک جزئیات و کشفِ تناقض.
    /// </summary>
    private sealed record FxDifferenceLinks(
        IReadOnlyDictionary<int, List<int>> LossExpenseIdsBySettlement,
        IReadOnlyDictionary<int, List<int>> GainLedgerIdsBySettlement,
        IReadOnlySet<int> FxLedgersTouchingSupplier,
        IReadOnlyDictionary<int, FxCommissionMatch> CommissionBySettlement,
        IReadOnlyDictionary<int, string> UserNames);

    private sealed record FxCommissionMatch(int Count, decimal? AmountUsd, int? ExpenseTransactionId);

    private async Task<FxDifferenceLinks> LoadFxDifferenceLinksAsync(
        IReadOnlyList<FxSettlementProjection> settlements,
        CancellationToken cancellationToken)
    {
        var settlementIds = settlements.Select(s => s.Id).ToHashSet();
        if (settlementIds.Count == 0)
        {
            return new FxDifferenceLinks(
                new Dictionary<int, List<int>>(), new Dictionary<int, List<int>>(),
                new HashSet<int>(), new Dictionary<int, FxCommissionMatch>(), new Dictionary<int, string>());
        }

        // ---- سمت ضرر: ExpenseTransaction با نوع تفاوت نرخ، شناسهٔ تسویه در Description ([SS-{id}]).
        var fxExpenses = await _db.ExpenseTransactions
            .AsNoTracking()
            .Where(e => e.ExpenseType != null
                && (e.ExpenseType.Code == PaymentsController.SarrafFxDifferenceExpenseCode
                    || e.ExpenseType.Category == FxDifferenceExpenseCategory))
            .Select(e => new { e.Id, e.Description })
            .ToListAsync(cancellationToken);

        var lossExpenseIds = new Dictionary<int, List<int>>();
        foreach (var expense in fxExpenses)
        {
            var settlementId = ExtractSettlementId(expense.Description);
            if (settlementId is null || !settlementIds.Contains(settlementId.Value))
            {
                continue;
            }

            if (!lossExpenseIds.TryGetValue(settlementId.Value, out var list))
            {
                list = [];
                lossExpenseIds[settlementId.Value] = list;
            }

            list.Add(expense.Id);
        }

        // ---- سمت سود: سطر بستانکارِ دفتر با SourceType = SarrafFxGain و SourceId = شناسهٔ تسویه.
        var gainLedgers = await _db.LedgerEntries
            .AsNoTracking()
            .Where(l => l.SourceType == PaymentsController.SarrafFxGainLedgerSourceType)
            .Select(l => new { l.Id, l.SourceId, l.SupplierId, l.ContractId })
            .ToListAsync(cancellationToken);

        var gainLedgerIds = gainLedgers
            .Where(l => settlementIds.Contains(l.SourceId))
            .GroupBy(l => l.SourceId)
            .ToDictionary(g => g.Key, g => g.Select(l => l.Id).ToList());

        // ---- کنترل جداسازی: سطرهای تفاوت نرخ نباید SupplierId یا ContractId داشته باشند.
        var allLossExpenseIds = lossExpenseIds.SelectMany(kv => kv.Value).ToHashSet();
        var lossLedgers = allLossExpenseIds.Count == 0
            ? []
            : await _db.LedgerEntries
                .AsNoTracking()
                .Where(l => l.SourceType == "Expense" && allLossExpenseIds.Contains(l.SourceId))
                .Select(l => new { l.SourceId, l.SupplierId, l.ContractId })
                .ToListAsync(cancellationToken);

        var touchingSupplier = new HashSet<int>();
        foreach (var (settlementId, expenseIds) in lossExpenseIds)
        {
            if (lossLedgers.Any(l => expenseIds.Contains(l.SourceId) && (l.SupplierId != null || l.ContractId != null)))
            {
                touchingSupplier.Add(settlementId);
            }
        }

        foreach (var ledger in gainLedgers.Where(l => settlementIds.Contains(l.SourceId)))
        {
            if (ledger.SupplierId != null || ledger.ContractId != null)
            {
                touchingSupplier.Add(ledger.SourceId);
            }
        }

        var commission = await LoadFxCommissionMatchesAsync(settlements, cancellationToken);

        var userIds = settlements
            .Where(s => s.CreatedByUserId.HasValue)
            .Select(s => s.CreatedByUserId!.Value)
            .Distinct()
            .ToList();
        var userNames = userIds.Count == 0
            ? new Dictionary<int, string>()
            : await _db.Users
                .AsNoTracking()
                .Where(u => userIds.Contains(u.Id))
                .ToDictionaryAsync(u => u.Id, u => u.FullName, cancellationToken);

        return new FxDifferenceLinks(lossExpenseIds, gainLedgerIds, touchingSupplier, commission, userNames);
    }

    /// <summary>
    /// کمیشن واقعی صراف هیچ FK یا marker قطعی به تسویه ندارد (PostViaSarrafCommissionAsync)،
    /// پس تطبیق ترکیبی است: سطرِ بدهیِ صراف با همان صراف، همان تاریخ و همان رسید.
    /// نوعِ مصرف باید «کمیسیون» باشد، نه FX_DIFFERENCE.
    /// چند تطبیق ⇒ جمع نمی‌شود و ردیف «نیازمند بررسی» علامت می‌خورد.
    /// </summary>
    private async Task<IReadOnlyDictionary<int, FxCommissionMatch>> LoadFxCommissionMatchesAsync(
        IReadOnlyList<FxSettlementProjection> settlements,
        CancellationToken cancellationToken)
    {
        var sarrafIds = settlements.Select(s => s.SarrafId).Distinct().ToList();
        var payableLedgers = await _db.LedgerEntries
            .AsNoTracking()
            .Where(l => l.SourceType == LedgerEntryOwnership.ViaSarrafPayableSourceType
                && sarrafIds.Contains(l.SourceId))
            .Select(l => new { l.SourceId, l.EntryDate, l.Reference, l.AmountUsd })
            .ToListAsync(cancellationToken);

        var result = new Dictionary<int, FxCommissionMatch>();
        if (payableLedgers.Count == 0)
        {
            return result;
        }

        var commissionExpenses = await _db.ExpenseTransactions
            .AsNoTracking()
            .Where(e => e.ExpenseType != null
                && (e.ExpenseType.Code == CommissionExpenseCode
                    || e.ExpenseType.Category == CommissionExpenseCategory))
            .Select(e => new { e.Id, e.ExpenseDate, e.AmountUsd })
            .ToListAsync(cancellationToken);

        var ledgersByKey = payableLedgers
            .GroupBy(l => (l.SourceId, l.EntryDate.Date, NormalizeReference(l.Reference)))
            .ToDictionary(g => g.Key, g => g.ToList());

        // اگر چند تسویه همان کلید را داشته باشند، هیچ‌کدام تطبیق قطعی نیستند.
        var settlementsPerKey = settlements
            .GroupBy(s => (s.SarrafId, s.SettlementDate.Date, NormalizeReference(s.ReferenceNumber)))
            .ToDictionary(g => g.Key, g => g.Count());

        foreach (var settlement in settlements)
        {
            var key = (settlement.SarrafId, settlement.SettlementDate.Date, NormalizeReference(settlement.ReferenceNumber));
            if (!ledgersByKey.TryGetValue(key, out var candidates) || candidates.Count == 0)
            {
                continue;
            }

            var sharing = settlementsPerKey.TryGetValue(key, out var count) ? count : 1;
            if (candidates.Count > 1 || sharing > 1)
            {
                // مبهم: مبلغ نمایش داده نمی‌شود و در هیچ جمعی وارد نمی‌گردد.
                result[settlement.Id] = new FxCommissionMatch(candidates.Count, null, null);
                continue;
            }

            var ledger = candidates[0];
            var expenseId = commissionExpenses
                .Where(e => e.ExpenseDate.Date == ledger.EntryDate.Date && e.AmountUsd == ledger.AmountUsd)
                .Select(e => (int?)e.Id)
                .FirstOrDefault();

            result[settlement.Id] = new FxCommissionMatch(1, ledger.AmountUsd, expenseId);
        }

        return result;
    }

    private static FxDifferenceRowViewModel BuildFxDifferenceRow(FxSettlementProjection s, FxDifferenceLinks links)
    {
        // تنها فرمول سود/زیان گزارش. دقت کامل decimal حفظ می‌شود.
        var fxResultUsd = s.SupplierAcceptedUsd - s.CompanyCostUsd;

        var expected = fxResultUsd > 0m
            ? FxDifferenceResult.Gain
            : fxResultUsd < 0m
                ? FxDifferenceResult.Loss
                : FxDifferenceResult.None;

        // خانوادهٔ ذخیره‌شده: SupplierShortfall هم یک صورتِ ضرر است (IsSarrafFxLoss).
        var stored = s.StoredDifferenceType switch
        {
            SarrafSettlementDifferenceType.Gain => FxDifferenceResult.Gain,
            SarrafSettlementDifferenceType.Loss or SarrafSettlementDifferenceType.SupplierShortfall => FxDifferenceResult.Loss,
            _ => FxDifferenceResult.None
        };

        var lossExpenseIds = links.LossExpenseIdsBySettlement.TryGetValue(s.Id, out var lossIds) ? lossIds : [];
        var gainLedgerIds = links.GainLedgerIdsBySettlement.TryGetValue(s.Id, out var gainIds) ? gainIds : [];

        var notes = new List<string>();
        if (expected != stored)
        {
            notes.Add($"علامت محاسبه‌شده ({FxResultLabel(expected)}) با نوع تفاوت ثبت‌شده ({SarrafDifferenceTypeLabel(s.StoredDifferenceType)}) نمی‌خواند.");
        }

        if (Math.Abs(Math.Abs(s.StoredDifferenceAmountUsd) - Math.Abs(fxResultUsd)) > FxDifferenceTolerance)
        {
            notes.Add("مبلغ تفاوت ثبت‌شده با اختلاف دو نرخ برابر نیست.");
        }

        if (lossExpenseIds.Count > 1)
        {
            notes.Add($"{lossExpenseIds.Count} مصرف تفاوت نرخ برای این تسویه ثبت شده است.");
        }

        if (gainLedgerIds.Count > 1)
        {
            notes.Add($"{gainLedgerIds.Count} سطر سود تفاوت نرخ برای این تسویه ثبت شده است.");
        }

        if (lossExpenseIds.Count > 0 && gainLedgerIds.Count > 0)
        {
            notes.Add("هم‌زمان مصرف ضرر و سطر سود برای این تسویه وجود دارد.");
        }

        if (links.FxLedgersTouchingSupplier.Contains(s.Id))
        {
            notes.Add("سطر تفاوت نرخ به تأمین‌کننده یا قرارداد بسته شده است.");
        }

        var result = notes.Count > 0 ? FxDifferenceResult.NeedsReview : expected;

        // ردیف نیازمند بررسی در هیچ ستون سود/ضرر و هیچ جمعی وارد نمی‌شود.
        var gainUsd = result == FxDifferenceResult.Gain ? fxResultUsd : 0m;
        var lossUsd = result == FxDifferenceResult.Loss ? -fxResultUsd : 0m;

        var commission = links.CommissionBySettlement.TryGetValue(s.Id, out var match) ? match : null;
        var commissionStatus = commission is null
            ? FxCommissionMatchStatus.None
            : commission.Count == 1 && commission.AmountUsd.HasValue
                ? FxCommissionMatchStatus.Exact
                : FxCommissionMatchStatus.NeedsReview;

        var sourceType = gainLedgerIds.Count > 0
            ? PaymentsController.SarrafFxGainLedgerSourceType
            : lossExpenseIds.Count > 0
                ? "Expense"
                : null;

        return new FxDifferenceRowViewModel
        {
            SettlementId = s.Id,
            SettlementDate = s.SettlementDate,
            ReferenceNumber = s.ReferenceNumber,
            CompanyName = s.CompanyName,
            SupplierName = s.SupplierName,
            SarrafName = s.SarrafName,
            ContractNumber = s.ContractNumber,
            Currency = s.Currency,
            Amount = s.Amount,
            CompanyRate = s.CompanyRate,
            SupplierRate = s.SupplierRate,
            CompanyCostUsd = s.CompanyCostUsd,
            SupplierAcceptedUsd = s.SupplierAcceptedUsd,
            FxResultUsd = fxResultUsd,
            Result = result,
            GainUsd = gainUsd,
            LossUsd = lossUsd,
            CommissionUsd = commissionStatus == FxCommissionMatchStatus.Exact ? commission!.AmountUsd : null,
            CommissionStatus = commissionStatus,
            Status = s.Status,
            Description = s.Description,
            CreatedByUserName = s.CreatedByUserId.HasValue && links.UserNames.TryGetValue(s.CreatedByUserId.Value, out var name)
                ? name
                : null,
            CreatedAtUtc = s.CreatedAtUtc,
            ReviewNotes = notes,
            Payment = new FxDifferencePaymentDetail
            {
                Currency = s.Currency,
                Amount = s.Amount,
                CompanyRate = s.CompanyRate,
                SupplierRate = s.SupplierRate,
                CompanyCostUsd = s.CompanyCostUsd,
                SupplierAcceptedUsd = s.SupplierAcceptedUsd
            },
            Supplier = new FxDifferenceSupplierDetail
            {
                SupplierId = s.SupplierId,
                SupplierName = s.SupplierName,
                StatementAmountUsd = s.LedgerEntryId.HasValue ? s.SupplierAcceptedUsd : null,
                StatementLedgerEntryId = s.LedgerEntryId,
                ContractId = s.ContractId,
                ContractNumber = s.ContractNumber,
                ContractPaidUsd = s.ContractId.HasValue && s.LedgerEntryId.HasValue ? s.SupplierAcceptedUsd : null,
                FxIsolatedFromSupplier = !links.FxLedgersTouchingSupplier.Contains(s.Id)
            },
            Sarraf = new FxDifferenceSarrafDetail
            {
                SarrafId = s.SarrafId,
                SarrafName = s.SarrafName,
                CompanyCostUsd = s.CompanyCostUsd,
                Direction = s.Direction,
                CommissionUsd = commissionStatus == FxCommissionMatchStatus.Exact ? commission!.AmountUsd : null,
                CommissionStatus = commissionStatus,
                CommissionMatchCount = commission?.Count ?? 0,
                CommissionExpenseTransactionId = commission?.ExpenseTransactionId
            },
            Profit = new FxDifferenceProfitDetail
            {
                LedgerSourceType = sourceType,
                ExpenseTransactionId = lossExpenseIds.Count == 1 ? lossExpenseIds[0] : null,
                LedgerEntryId = gainLedgerIds.Count == 1 ? gainLedgerIds[0] : null,
                ExchangeDifferenceLedgerEntryId = s.ExchangeDifferenceLedgerEntryId,
                NoCashEffect = s.PaymentTransactionId is null && s.CashAccountId is null
            }
        };
    }

    private static FxDifferenceTotalsViewModel BuildFxDifferenceTotals(IReadOnlyList<FxDifferenceRowViewModel> rows)
        => new()
        {
            SettlementCount = rows.Count,
            OriginalAmounts = rows
                .GroupBy(r => r.Currency)
                .OrderBy(g => g.Key)
                .Select(g => new FxDifferenceCurrencyTotalViewModel { Currency = g.Key, Amount = g.Sum(r => r.Amount) })
                .ToList(),
            TotalCompanyCostUsd = rows.Sum(r => r.CompanyCostUsd),
            TotalSupplierAcceptedUsd = rows.Sum(r => r.SupplierAcceptedUsd),
            TotalGainUsd = rows.Sum(r => r.GainUsd),
            TotalLossUsd = rows.Sum(r => r.LossUsd),
            // فقط تطبیق قطعی جمع می‌شود؛ کمیشن مبهم صفر حساب می‌شود.
            TotalCommissionUsd = rows
                .Where(r => r.CommissionStatus == FxCommissionMatchStatus.Exact)
                .Sum(r => r.CommissionUsd ?? 0m),
            GainCount = rows.Count(r => r.Result == FxDifferenceResult.Gain),
            LossCount = rows.Count(r => r.Result == FxDifferenceResult.Loss),
            NoDifferenceCount = rows.Count(r => r.Result == FxDifferenceResult.None),
            NeedsReviewCount = rows.Count(r => r.Result == FxDifferenceResult.NeedsReview),
            CommissionNeedsReviewCount = rows.Count(r => r.CommissionStatus == FxCommissionMatchStatus.NeedsReview)
        };

    private static bool MatchesResultFilter(FxDifferenceRowViewModel row, FxDifferenceResultFilter filter)
        => filter switch
        {
            FxDifferenceResultFilter.GainOnly => row.Result == FxDifferenceResult.Gain,
            FxDifferenceResultFilter.LossOnly => row.Result == FxDifferenceResult.Loss,
            FxDifferenceResultFilter.NoDifference => row.Result == FxDifferenceResult.None,
            FxDifferenceResultFilter.NeedsReview => row.Result == FxDifferenceResult.NeedsReview,
            _ => true
        };

    private static bool MatchesCommissionFilter(FxDifferenceRowViewModel row, FxCommissionFilter filter)
        => filter switch
        {
            FxCommissionFilter.WithCommission => row.CommissionStatus != FxCommissionMatchStatus.None,
            FxCommissionFilter.WithoutCommission => row.CommissionStatus == FxCommissionMatchStatus.None,
            _ => true
        };

    private static int? ExtractSettlementId(string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return null;
        }

        var match = SettlementMarkerPattern.Match(description);
        return match.Success && int.TryParse(match.Groups[1].Value, out var id) ? id : null;
    }

    private static string NormalizeReference(string? reference)
        => string.IsNullOrWhiteSpace(reference) ? "" : reference.Trim();

    // ===================== برچسب‌ها =====================

    internal static string FxResultLabel(FxDifferenceResult result)
        => result switch
        {
            FxDifferenceResult.Gain => "سود تفاوت نرخ",
            FxDifferenceResult.Loss => "ضرر تفاوت نرخ",
            FxDifferenceResult.NeedsReview => "نیازمند بررسی",
            _ => "بدون تفاوت"
        };

    internal static string FxCommissionLabel(FxCommissionMatchStatus status)
        => status switch
        {
            FxCommissionMatchStatus.Exact => "تطبیق قطعی",
            FxCommissionMatchStatus.NeedsReview => "نیازمند بررسی",
            _ => "بدون کمیشن"
        };

    private static string FxResultFilterLabel(FxDifferenceResultFilter filter)
        => filter switch
        {
            FxDifferenceResultFilter.GainOnly => "فقط سود",
            FxDifferenceResultFilter.LossOnly => "فقط ضرر",
            FxDifferenceResultFilter.NoDifference => "بدون تفاوت",
            FxDifferenceResultFilter.NeedsReview => "نیازمند بررسی",
            _ => "همه"
        };

    private static string FxCommissionFilterLabel(FxCommissionFilter filter)
        => filter switch
        {
            FxCommissionFilter.WithCommission => "دارای کمیشن",
            FxCommissionFilter.WithoutCommission => "بدون کمیشن",
            _ => "همه"
        };

    internal static string SarrafSettlementStatusLabel(SarrafSettlementStatus status)
        => status switch
        {
            SarrafSettlementStatus.Posted => "ثبت‌شده",
            SarrafSettlementStatus.Cancelled => "لغوشده",
            _ => "پیش‌نویس"
        };

    private static string SarrafDifferenceTypeLabel(SarrafSettlementDifferenceType type)
        => type switch
        {
            SarrafSettlementDifferenceType.Gain => "سود",
            SarrafSettlementDifferenceType.Loss => "ضرر",
            SarrafSettlementDifferenceType.SupplierShortfall => "کسری تأمین‌کننده",
            _ => "بدون تفاوت"
        };

    // ===================== Lookups =====================

    private async Task PopulateFxDifferenceLookupsAsync(
        FxDifferenceReportFilterViewModel filter,
        CancellationToken cancellationToken)
    {
        ViewBag.Companies = new SelectList(
            await _db.Companies.AsNoTracking().OrderBy(c => c.Name)
                .Select(c => new LookupOption(c.Id, c.Name)).ToListAsync(cancellationToken),
            nameof(LookupOption.Id), nameof(LookupOption.Name), filter.CompanyId);

        ViewBag.Suppliers = new SelectList(
            await _db.Suppliers.AsNoTracking().OrderBy(s => s.Name)
                .Select(s => new LookupOption(s.Id, s.Name)).ToListAsync(cancellationToken),
            nameof(LookupOption.Id), nameof(LookupOption.Name), filter.SupplierId);

        ViewBag.Sarrafs = new SelectList(
            await _db.Sarrafs.AsNoTracking().OrderBy(s => s.Name)
                .Select(s => new LookupOption(s.Id, s.Name)).ToListAsync(cancellationToken),
            nameof(LookupOption.Id), nameof(LookupOption.Name), filter.SarrafId);

        ViewBag.Contracts = new SelectList(
            await _db.Contracts.AsNoTracking().OrderByDescending(c => c.ContractDate)
                .Select(c => new LookupOption(c.Id, c.ContractNumber)).ToListAsync(cancellationToken),
            nameof(LookupOption.Id), nameof(LookupOption.Name), filter.ContractId);

        var currencies = await _db.SarrafSettlements.AsNoTracking()
            .Where(s => s.DifferenceReason == DifferenceReason.FxDifference)
            .Select(s => s.SarrafCurrency)
            .Distinct()
            .OrderBy(c => c)
            .ToListAsync(cancellationToken);
        ViewBag.Currencies = new SelectList(currencies, filter.Currency);
    }
}
