using Microsoft.EntityFrameworkCore;
// ثابت‌های SourceType دفتر کلِ تسویهٔ سه‌جانبه همان‌جایی می‌مانند که نوشته می‌شوند؛
// اینجا فقط خوانده می‌شوند تا دو تعریف موازی ساخته نشود.
using PTGOilSystem.Web.Controllers;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.Payments;
using PTGOilSystem.Web.Models.Reconciliation;
using PTGOilSystem.Web.Models.ThreeWaySettlement;

namespace PTGOilSystem.Web.Services.Reconciliation;

// D2 — گزارش «پول‌های معلق / Suspense Money».
// کاملاً read-only: فقط queryهای AsNoTracking روی داده‌ٔ موجود اجرا می‌شود.
// هیچ Payment یا LedgerEntry ساخته یا تغییر داده نمی‌شود و هیچ رکوردی اصلاح نمی‌شود.
public partial class ReconciliationService
{
    /// <summary>
    /// فیلتر شدت و بازهٔ تاریخ روی همان مجموعهٔ ساخته‌شده اعمال می‌شود؛ کنترلر فقط
    /// پارامترها را می‌دهد و منطق تشخیص «پول معلق» اینجا می‌ماند.
    /// </summary>
    public async Task<SuspenseMoneyViewModel> BuildSuspenseMoneyAsync(
        string? severity,
        DateTime? fromDate,
        DateTime? toDate)
    {
        var all = await BuildSuspenseMoneyAsync();
        IEnumerable<SuspenseMoneyItemViewModel> filtered = all.Items;

        if (string.Equals(severity, "critical", StringComparison.OrdinalIgnoreCase))
        {
            filtered = filtered.Where(i => i.Severity == SuspenseSeverity.Critical);
        }
        else if (string.Equals(severity, "warning", StringComparison.OrdinalIgnoreCase))
        {
            filtered = filtered.Where(i => i.Severity == SuspenseSeverity.Warning);
        }

        if (fromDate.HasValue)
        {
            filtered = filtered.Where(i => i.Date >= fromDate.Value.Date);
        }

        if (toDate.HasValue)
        {
            filtered = filtered.Where(i => i.Date <= toDate.Value.Date);
        }

        return new SuspenseMoneyViewModel
        {
            Items = filtered.ToList(),
            SelectedSeverity = severity,
            FromDate = fromDate,
            ToDate = toDate
        };
    }

    public async Task<SuspenseMoneyViewModel> BuildSuspenseMoneyAsync()
    {
        var items = new List<SuspenseMoneyItemViewModel>();

        // ۱) رزنامچه/پرداخت‌هایی که حساب نقد را تغییر داده‌اند اما لینک تجاری‌شان ناقص است.
        var payments = await _db.PaymentTransactions
            .AsNoTracking()
            .Include(p => p.Customer)
            .Include(p => p.Supplier)
            .Include(p => p.ServiceProvider)
            .Include(p => p.Sarraf)
            .Include(p => p.Employee)
            .Include(p => p.Driver)
            .Include(p => p.Contract)
            .OrderByDescending(p => p.PaymentDate)
            .ThenByDescending(p => p.Id)
            .ToListAsync();

        foreach (var payment in payments)
        {
            var hasCounterparty = payment.CustomerId.HasValue
                || payment.SupplierId.HasValue
                || payment.ServiceProviderId.HasValue
                || payment.SarrafId.HasValue
                || payment.EmployeeId.HasValue
                || payment.DriverId.HasValue;
            var hasOperationalLink = payment.ContractId.HasValue
                || payment.ShipmentId.HasValue
                || payment.SalesTransactionId.HasValue
                || payment.ExpenseTransactionId.HasValue
                || payment.TruckDispatchId.HasValue;

            string? issue = null;
            string? explanation = null;
            var severity = SuspenseSeverity.Warning;

            if (!hasCounterparty && !hasOperationalLink)
            {
                issue = "طرف حساب و سند نامشخص";
                explanation = "این پول وارد/خارج شده اما نه طرف حساب دارد و نه به سندی وصل است.";
                severity = SuspenseSeverity.Critical;
            }
            else if (!hasCounterparty)
            {
                issue = "طرف حساب نامشخص";
                explanation = "این پول جابه‌جا شده اما مشخص نیست از کی گرفته یا به کی داده شده است.";
                severity = SuspenseSeverity.Critical;
            }
            else if ((payment.PaymentKind == PaymentKind.SupplierPayment
                    || payment.PaymentKind == PaymentKind.SupplierReceipt)
                && payment.SupplierId is null)
            {
                issue = "تأمین‌کننده مشخص نیست";
                explanation = "این سند پرداخت/دریافت تأمین‌کننده است اما تأمین‌کننده انتخاب نشده است.";
                severity = SuspenseSeverity.Critical;
            }
            else if ((payment.PaymentKind == PaymentKind.CustomerReceipt
                    || payment.PaymentKind == PaymentKind.CustomerPayment)
                && payment.CustomerId is null)
            {
                issue = "مشتری مشخص نیست";
                explanation = "این سند دریافت/پرداخت مشتری است اما مشتری انتخاب نشده است.";
                severity = SuspenseSeverity.Critical;
            }
            else if ((payment.PaymentKind == PaymentKind.SupplierPayment
                    || payment.PaymentKind == PaymentKind.SupplierReceipt)
                && payment.ContractId is null)
            {
                issue = "قرارداد مشخص نیست";
                explanation = "این پول تأمین‌کننده هنوز به هیچ قراردادی وصل نشده است.";
                severity = SuspenseSeverity.Warning;
            }

            if (issue is null)
            {
                continue;
            }

            items.Add(new SuspenseMoneyItemViewModel
            {
                Date = payment.PaymentDate,
                DocumentType = $"رزنامچه — {PaymentKindLabels.ToPersian(payment.PaymentKind)}",
                Amount = payment.Amount,
                Currency = payment.Currency,
                AmountUsd = payment.AmountUsd,
                CounterpartyName = ResolvePaymentCounterpartyName(payment),
                ContractNumber = payment.Contract?.ContractNumber,
                IssueSource = issue,
                PlainExplanation = explanation ?? "",
                Severity = severity,
                DetailsController = "Payments",
                DetailsAction = "Details",
                DetailsRouteId = payment.Id,
                // «وصل کن» فقط کاربر را به فرم ویرایش موجود می‌برد؛ هیچ اصلاح خودکار نیست.
                ConnectController = "Payments",
                ConnectAction = "Edit",
                ConnectRouteId = payment.Id
            });
        }

        // ۲) ثبت‌های دفتر کل که ردیابی منبع‌شان ناقص است (SourceType/SourceId).
        var ledgerIssues = await _db.LedgerEntries
            .AsNoTracking()
            .Include(l => l.Contract)
            .Include(l => l.Supplier)
            .Include(l => l.Customer)
            .Where(l => l.SourceType == "" || l.SourceId <= 0)
            .OrderByDescending(l => l.EntryDate)
            .ThenByDescending(l => l.Id)
            .ToListAsync();

        foreach (var ledger in ledgerIssues)
        {
            items.Add(new SuspenseMoneyItemViewModel
            {
                Date = ledger.EntryDate,
                DocumentType = "ثبت دفتر کل",
                Amount = ledger.SourceAmount ?? ledger.AmountUsd,
                Currency = ledger.SourceCurrencyCode ?? ledger.Currency,
                AmountUsd = ledger.AmountUsd,
                CounterpartyName = ledger.Supplier?.Name ?? ledger.Customer?.Name,
                ContractNumber = ledger.Contract?.ContractNumber,
                IssueSource = "سند منبع نامشخص",
                PlainExplanation = "این ثبت مالی به سند اصلی (نوع و شماره) وصل نیست و نیاز به بررسی دارد.",
                Severity = SuspenseSeverity.Critical,
                DetailsController = "Ledger",
                DetailsAction = "Details",
                DetailsRouteId = ledger.Id
                // دفتر کل مستقیم ویرایش نمی‌شود؛ لینک «وصل کن» نمی‌گذاریم.
            });
        }

        // ۳) تسویه سه‌طرفه / حواله: فقط trace سند و Ledger بررسی می‌شود.
        await AddThreeWaySettlementTraceIssuesAsync(items);

        // ۴) تسویه‌های صراف که تفاوت دارند اما دلیل تفاوت نوشته نشده است.
        var sarrafSettlements = await _db.SarrafSettlements
            .AsNoTracking()
            .Include(s => s.Sarraf)
            .Include(s => s.Supplier)
            .Include(s => s.Contract)
            .Where(s => s.Status == SarrafSettlementStatus.Posted
                && s.DifferenceType != SarrafSettlementDifferenceType.None
                && s.DifferenceReason == null
                && (s.Description == null || s.Description == ""))
            .OrderByDescending(s => s.SettlementDate)
            .ThenByDescending(s => s.Id)
            .ToListAsync();

        foreach (var settlement in sarrafSettlements)
        {
            items.Add(new SuspenseMoneyItemViewModel
            {
                Date = settlement.SettlementDate,
                DocumentType = "تسویه صراف",
                Amount = Math.Abs(settlement.DifferenceAmountUsd),
                Currency = "USD",
                AmountUsd = Math.Abs(settlement.DifferenceAmountUsd),
                CounterpartyName = settlement.Sarraf?.Name
                    ?? settlement.Supplier?.Name
                    ?? settlement.Contract?.Supplier?.Name,
                ContractNumber = settlement.Contract?.ContractNumber,
                IssueSource = "دلیل تفاوت نوشته نشده",
                PlainExplanation = "این تسویه تفاوت نرخ/مبلغ دارد اما دلیل آن نوشته نشده است.",
                Severity = SuspenseSeverity.Warning,
                DetailsController = "SarrafSettlements",
                DetailsAction = "Details",
                DetailsRouteId = settlement.Id
            });
        }

        // ۵) احتمال ثبت تکراری حواله: همان تأمین‌کننده هم تسویه سه‌طرفه و هم تسویه صراف دارد (خطر دوباره‌کم‌شدن بدهی).
        await AddThreeWayVsSarrafDuplicateRiskAsync(items);

        // ۶) سیاست تفاوت مبلغ تسویه سه‌طرفه (فاز C1): فقط هشدار read-only، نه Critical.
        await AddThreeWaySettlementDifferencePolicyIssuesAsync(items);

        var ordered = items
            .OrderByDescending(i => i.Severity)
            .ThenByDescending(i => i.Date)
            .ThenByDescending(i => i.AmountUsd)
            .ToList();

        return new SuspenseMoneyViewModel { Items = ordered };
    }

    // فقط read-only: تسویه سه‌طرفه‌ای که با یک تسویه صراف ثبت‌شده هم‌پوشانی قوی دارد را به‌عنوان هشدار نشان می‌دهد.
    // هیچ رکوردی ساخته، تغییر یا اصلاح نمی‌شود.
    private async Task AddThreeWayVsSarrafDuplicateRiskAsync(List<SuspenseMoneyItemViewModel> items)
    {
        var threeWays = await _db.ThreeWaySettlements
            .AsNoTracking()
            .Include(s => s.Customer)
            .Include(s => s.Supplier)
            .Include(s => s.Sarraf)
            .Where(s => s.Status == ThreeWaySettlementStatus.Posted && s.SupplierId != null)
            .OrderByDescending(s => s.SettlementDate)
            .ThenByDescending(s => s.Id)
            .ToListAsync();

        if (threeWays.Count == 0)
        {
            return;
        }

        var supplierIds = threeWays.Select(s => s.SupplierId!.Value).Distinct().ToList();
        var sarrafSettlements = await _db.SarrafSettlements
            .AsNoTracking()
            .Where(s => s.Status == SarrafSettlementStatus.Posted
                && s.SupplierId != null
                && supplierIds.Contains(s.SupplierId.Value))
            .Select(s => new { s.SupplierId, s.ReferenceNumber, s.SettlementDate, s.SupplierAcceptedAmountUsd, s.SarrafId })
            .ToListAsync();

        if (sarrafSettlements.Count == 0)
        {
            return;
        }

        foreach (var tw in threeWays)
        {
            var reference = string.IsNullOrWhiteSpace(tw.HawalaReference) ? null : tw.HawalaReference.Trim();
            var matches = sarrafSettlements.Any(c => c.SupplierId == tw.SupplierId
                && ((!string.IsNullOrWhiteSpace(reference)
                        && string.Equals(c.ReferenceNumber != null ? c.ReferenceNumber.Trim() : null, reference, StringComparison.OrdinalIgnoreCase))
                    || (c.SettlementDate == tw.SettlementDate
                        && Math.Abs(c.SupplierAcceptedAmountUsd - tw.SupplierAcceptedUsd) <= 0.01m
                        && (tw.SarrafId == null || c.SarrafId == tw.SarrafId))));

            if (!matches)
            {
                continue;
            }

            items.Add(new SuspenseMoneyItemViewModel
            {
                Date = tw.SettlementDate,
                DocumentType = "تسویه سه‌طرفه / حواله",
                Amount = tw.SupplierAcceptedAmount,
                Currency = tw.Currency,
                AmountUsd = tw.SupplierAcceptedUsd,
                CounterpartyName = $"{tw.Customer?.Name ?? "مشتری"} / {tw.Supplier?.Name ?? "تأمین‌کننده"}"
                    + (tw.Sarraf != null ? $" (صراف: {tw.Sarraf.Name})" : ""),
                IssueSource = "احتمال ثبت تکراری حواله",
                PlainExplanation = "برای همین تأمین‌کننده هم تسویه سه‌طرفه و هم تسویه صراف ثبت شده است؛ ممکن است بدهی تأمین‌کننده دوبار کم شده باشد. این گزارش فقط هشدار است و چیزی را اصلاح نمی‌کند.",
                Severity = SuspenseSeverity.Warning,
                DetailsController = "ThreeWaySettlement",
                DetailsAction = "Details",
                DetailsRouteId = tw.Id
            });
        }
    }

    private async Task AddThreeWaySettlementTraceIssuesAsync(List<SuspenseMoneyItemViewModel> items)
    {
        var settlements = await _db.ThreeWaySettlements
            .AsNoTracking()
            .Include(s => s.Customer)
            .Include(s => s.Supplier)
            .Include(s => s.CustomerSaleContract)
            .Include(s => s.SupplierPurchaseContract)
            .Include(s => s.CustomerLedgerEntry)
            .Include(s => s.SupplierLedgerEntry)
            // فاز A: حالت صراف (صراف فقط واسطه) هم مثل تأمین‌کننده trace می‌شود؛ «حساب دیگر» چون unposted/پیش‌نمایش است وارد نمی‌شود.
            .Where(s => (s.PayeeType == ThreeWayPayeeType.Supplier || s.PayeeType == ThreeWayPayeeType.Sarraf)
                && (s.Status == ThreeWaySettlementStatus.Posted || s.Status == ThreeWaySettlementStatus.Cancelled))
            .OrderByDescending(s => s.SettlementDate)
            .ThenByDescending(s => s.Id)
            .ToListAsync();

        if (settlements.Count == 0)
        {
            return;
        }

        var settlementIds = settlements.Select(s => s.Id).ToList();
        var cancellationLedgers = await _db.LedgerEntries
            .AsNoTracking()
            .Where(l => l.SourceType == ThreeWaySettlementController.CancellationLedgerSourceType
                && settlementIds.Contains(l.SourceId))
            .ToListAsync();
        var cancellationBySettlement = cancellationLedgers
            .GroupBy(l => l.SourceId)
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var settlement in settlements)
        {
            cancellationBySettlement.TryGetValue(settlement.Id, out var reversals);
            reversals ??= [];

            var issue = ResolveThreeWaySettlementTraceIssue(settlement, reversals);
            if (issue is null)
            {
                continue;
            }

            items.Add(new SuspenseMoneyItemViewModel
            {
                Date = settlement.Status == ThreeWaySettlementStatus.Cancelled
                    ? (settlement.CancelledAtUtc ?? settlement.SettlementDate).Date
                    : settlement.SettlementDate,
                DocumentType = settlement.Status == ThreeWaySettlementStatus.Cancelled
                    ? "برگشت تسویه سه‌طرفه"
                    : "تسویه سه‌طرفه / حواله",
                Amount = settlement.CustomerPaidAmount,
                Currency = settlement.Currency,
                AmountUsd = settlement.CustomerPaidUsd,
                CounterpartyName = $"{settlement.Customer?.Name ?? "مشتری نامشخص"} / {settlement.Supplier?.Name ?? "تأمین‌کننده نامشخص"}",
                ContractNumber = settlement.CustomerSaleContract?.ContractNumber
                    ?? settlement.SupplierPurchaseContract?.ContractNumber,
                IssueSource = issue,
                PlainExplanation = "ردیابی Ledger این تسویه سه‌طرفه کامل نیست. سند را از صفحه جزئیات بررسی کنید؛ این گزارش هیچ posting جدید نمی‌سازد.",
                Severity = SuspenseSeverity.Critical,
                DetailsController = "ThreeWaySettlement",
                DetailsAction = "Details",
                DetailsRouteId = settlement.Id
            });
        }
    }

    private static string? ResolveThreeWaySettlementTraceIssue(
        ThreeWaySettlement settlement,
        IReadOnlyCollection<LedgerEntry> cancellationLedgers)
    {
        if (settlement.SupplierId is null)
        {
            return "تأمین‌کننده تسویه سه‌طرفه مشخص نیست";
        }

        var customerIssue = ValidateThreeWayLedger(
            settlement.CustomerLedgerEntry,
            settlement.Id,
            LedgerSide.Debit,
            settlement.CustomerPaidUsd,
            settlement.CustomerId,
            supplierId: null,
            settlement.CustomerSaleContractId,
            ThreeWaySettlementController.LedgerSourceType,
            "Ledger اصلی مشتری");
        if (customerIssue is not null)
        {
            return customerIssue;
        }

        var supplierIssue = ValidateThreeWayLedger(
            settlement.SupplierLedgerEntry,
            settlement.Id,
            LedgerSide.Debit,
            settlement.SupplierAcceptedUsd,
            customerId: null,
            settlement.SupplierId,
            settlement.SupplierPurchaseContractId,
            ThreeWaySettlementController.LedgerSourceType,
            "Ledger اصلی تأمین‌کننده");
        if (supplierIssue is not null)
        {
            return supplierIssue;
        }

        if (settlement.Status == ThreeWaySettlementStatus.Posted)
        {
            return cancellationLedgers.Count == 0
                ? null
                : "سند فعال است اما Ledger برگشت دارد";
        }

        if (string.IsNullOrWhiteSpace(settlement.CancellationReason))
        {
            return "دلیل لغو تسویه سه‌طرفه ثبت نشده";
        }

        if (!settlement.CancelledAtUtc.HasValue)
        {
            return "زمان لغو تسویه سه‌طرفه ثبت نشده";
        }

        if (cancellationLedgers.Count != 2)
        {
            return "Ledger برگشت تسویه سه‌طرفه کامل نیست";
        }

        var customerReversal = cancellationLedgers.FirstOrDefault(l => l.CustomerId == settlement.CustomerId);
        var customerReversalIssue = ValidateThreeWayLedger(
            customerReversal,
            settlement.Id,
            LedgerSide.Credit,
            settlement.CustomerPaidUsd,
            settlement.CustomerId,
            supplierId: null,
            settlement.CustomerSaleContractId,
            ThreeWaySettlementController.CancellationLedgerSourceType,
            "Ledger برگشت مشتری");
        if (customerReversalIssue is not null)
        {
            return customerReversalIssue;
        }

        var supplierReversal = cancellationLedgers.FirstOrDefault(l => l.SupplierId == settlement.SupplierId);
        return ValidateThreeWayLedger(
            supplierReversal,
            settlement.Id,
            LedgerSide.Credit,
            settlement.SupplierAcceptedUsd,
            customerId: null,
            settlement.SupplierId,
            settlement.SupplierPurchaseContractId,
            ThreeWaySettlementController.CancellationLedgerSourceType,
            "Ledger برگشت تأمین‌کننده");
    }

    private static string? ValidateThreeWayLedger(
        LedgerEntry? ledger,
        int sourceId,
        LedgerSide expectedSide,
        decimal expectedAmountUsd,
        int? customerId,
        int? supplierId,
        int? contractId,
        string sourceType,
        string label)
    {
        if (ledger is null)
        {
            return $"{label} پیدا نشد";
        }

        if (!string.Equals(ledger.SourceType, sourceType, StringComparison.Ordinal) || ledger.SourceId != sourceId)
        {
            return $"{label} SourceType/SourceId درست ندارد";
        }

        if (ledger.Side != expectedSide)
        {
            return $"{label} سمت درست ندارد";
        }

        if (ledger.AmountUsd != expectedAmountUsd)
        {
            return $"{label} مبلغ درست ندارد";
        }

        if (customerId.HasValue && ledger.CustomerId != customerId)
        {
            return $"{label} مشتری درست ندارد";
        }

        if (supplierId.HasValue && ledger.SupplierId != supplierId)
        {
            return $"{label} تأمین‌کننده درست ندارد";
        }

        if (contractId.HasValue && ledger.ContractId != contractId)
        {
            return $"{label} قرارداد درست ندارد";
        }

        return null;
    }

    // فاز C1 — فقط read-only و فقط Warning (هرگز Critical): سیاست trace-only تفاوت مبلغ.
    // هیچ رکوردی ساخته/اصلاح نمی‌شود؛ فقط رکوردهای Posted با تفاوت غیرصفر بررسی می‌شوند.
    private async Task AddThreeWaySettlementDifferencePolicyIssuesAsync(List<SuspenseMoneyItemViewModel> items)
    {
        var settlements = await _db.ThreeWaySettlements
            .AsNoTracking()
            .Include(s => s.Customer)
            .Include(s => s.Supplier)
            .Where(s => s.Status == ThreeWaySettlementStatus.Posted && s.DifferenceUsd != 0m)
            .OrderByDescending(s => s.SettlementDate)
            .ThenByDescending(s => s.Id)
            .ToListAsync();

        foreach (var settlement in settlements)
        {
            string? issue = null;

            if (settlement.DifferenceReason is null)
            {
                issue = "تفاوت مبلغ بدون دلیل ثبت شده";
            }
            else if (settlement.DifferenceReason == DifferenceReason.Other && string.IsNullOrWhiteSpace(settlement.Notes))
            {
                issue = "دلیل تفاوت «سایر» است اما توضیح ندارد";
            }
            else if (settlement.DifferenceReason == DifferenceReason.Commission
                || settlement.DifferenceReason == DifferenceReason.TransferFee)
            {
                issue = "تفاوت کمیشن/کرایه حواله هنوز به هزینه تبدیل نشده (فعلاً فقط ردیابی)";
            }

            if (issue is null)
            {
                continue;
            }

            items.Add(new SuspenseMoneyItemViewModel
            {
                Date = settlement.SettlementDate,
                DocumentType = "تسویه سه‌طرفه / حواله",
                Amount = Math.Abs(settlement.DifferenceUsd),
                Currency = "USD",
                AmountUsd = Math.Abs(settlement.DifferenceUsd),
                CounterpartyName = $"{settlement.Customer?.Name ?? "مشتری"} / {settlement.Supplier?.Name ?? "تأمین‌کننده"}",
                IssueSource = issue,
                PlainExplanation = "این تفاوت فعلاً فقط ثبت/ردیابی شده و در هزینه یا P&L وارد نشده است. این گزارش فقط هشدار است و چیزی را اصلاح نمی‌کند.",
                Severity = SuspenseSeverity.Warning,
                DetailsController = "ThreeWaySettlement",
                DetailsAction = "Details",
                DetailsRouteId = settlement.Id
            });
        }
    }

    private static string? ResolvePaymentCounterpartyName(PaymentTransaction payment)
        => payment.Customer?.Name
            ?? payment.Supplier?.Name
            ?? payment.ServiceProvider?.Name
            ?? payment.Sarraf?.Name
            ?? payment.Employee?.FullName
            ?? payment.Driver?.FullName;
}
