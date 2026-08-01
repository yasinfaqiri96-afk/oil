using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using PTGOilSystem.Web.Infrastructure.RateLimiting;
using PTGOilSystem.Web.Helpers;
using PTGOilSystem.Web.Models.Reconciliation;
using PTGOilSystem.Web.Services.Exports;

namespace PTGOilSystem.Web.Controllers;

public partial class ReconciliationController
{
    /// <summary>
    /// خروجی مغایرت‌های تفصیلی. دقیقاً همان سرویس، همان دسته و همان فیلتر تاریخِ صفحه را
    /// می‌خواند؛ هیچ query یا فرمول جداگانه‌ای برای Export وجود ندارد. اگر دسته انتخاب نشده
    /// باشد، جدول خلاصهٔ شمارش دسته‌ها خروجی می‌شود.
    /// </summary>
    [EnableRateLimiting(RateLimitPolicies.CsvExport)]
    public async Task<IActionResult> DiscrepanciesExport(
        string? format,
        ReconciliationDiscrepancyCategory? category = null,
        DateTime? fromDate = null,
        DateTime? toDate = null,
        CancellationToken ct = default)
    {
        var isEn = UiText.IsEn(HttpContext);
        var businessDate = _businessClock.Today.ToString("yyyy-MM-dd");

        if (category is null)
        {
            var counts = await _reconciliation.BuildDiscrepancyCountsAsync(fromDate, toDate, ct);
            return TabularExportSupport.File(this, format, new TabularExportDocument
            {
                FileNameStem = "PTG_Reconciliation_Discrepancies",
                TitleFa = "مغایرت‌های تفصیلی — خلاصه",
                TitleEn = "Reconciliation discrepancies — summary",
                Filters = TabularExportSupport.FilterSummary(
                    ("تاریخ تولید (کابل) / Generated (Kabul)", businessDate),
                    ("از تاریخ / From", fromDate?.ToString("yyyy-MM-dd")),
                    ("تا تاریخ / To", toDate?.ToString("yyyy-MM-dd"))),
                KnownRowCount = counts.Count,
                Columns =
                [
                    new("دسته", "Category", Width: 34),
                    new("تعداد", "Count", TabularExportValueType.Integer, 12),
                    new("شدت", "Severity", Width: 14)
                ],
                Rows = counts.Select(c => new TabularExportRow(
                [
                    TabularExportCell.Text(isEn ? c.TitleEn : c.TitleFa),
                    TabularExportCell.Integer(c.Count),
                    TabularExportCell.Text(c.Severity == "critical"
                        ? (isEn ? "Critical" : "بحرانی")
                        : (isEn ? "Warning" : "هشدار"))
                ]))
            });
        }

        // خروجی یک دسته: کل ردیف‌های همان دسته با همان فیلتر صفحه (بدون Paging صفحه).
        // ردیف‌ها جریانی خوانده می‌شوند؛ هیچ List کاملی ساخته نمی‌شود، پس حجم زیاد
        // حافظهٔ سرور را پر نمی‌کند. تعداد کل فقط با یک COUNT گرفته می‌شود تا سقف
        // خروجی پیش از شروع نوشتن بررسی شود.
        var totalCount = await _reconciliation.BuildDiscrepancyCountAsync(category.Value, fromDate, toDate, ct);

        var titleFa = ReconciliationDiscrepancyText.TitleFa(category.Value);
        var titleEn = ReconciliationDiscrepancyText.TitleEn(category.Value);

        return TabularExportSupport.File(this, format, new TabularExportDocument
        {
            FileNameStem = "PTG_Reconciliation_" + category.Value,
            TitleFa = titleFa,
            TitleEn = titleEn,
            Filters = TabularExportSupport.FilterSummary(
                ("تاریخ تولید (کابل) / Generated (Kabul)", businessDate),
                ("دسته / Category", isEn ? titleEn : titleFa),
                ("از تاریخ / From", fromDate?.ToString("yyyy-MM-dd")),
                ("تا تاریخ / To", toDate?.ToString("yyyy-MM-dd"))),
            KnownRowCount = totalCount,
            ForceLandscape = true,
            Columns =
            [
                new("مرجع", "Reference", Width: 22),
                new("تاریخ", "Date", TabularExportValueType.Date, 13),
                new("مبلغ USD", "Amount USD", TabularExportValueType.Number, 16),
                new("مقدار MT", "Quantity MT", TabularExportValueType.Number, 15),
                new("توضیح", "Detail", Width: 34, Wrap: true)
            ],
            Rows = [],
            RowsAsync = StreamDiscrepancyExportRowsAsync(category.Value, fromDate, toDate, ct)
        });
    }

    // تبدیل جریانیِ ردیف مغایرت به ردیف خروجی؛ هر ردیف بلافاصله پس از خواندن نوشته می‌شود.
    private async IAsyncEnumerable<TabularExportRow> StreamDiscrepancyExportRowsAsync(
        ReconciliationDiscrepancyCategory category,
        DateTime? fromDate,
        DateTime? toDate,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var row in _reconciliation
            .StreamDiscrepancyRowsAsync(category, fromDate, toDate, ct)
            .WithCancellation(ct))
        {
            yield return new TabularExportRow(
            [
                TabularExportCell.Text(row.Reference),
                TabularExportCell.Date(row.Date),
                TabularExportCell.Number(row.AmountUsd),
                TabularExportCell.Number(row.QuantityMt),
                TabularExportCell.Text(row.Detail)
            ]);
        }
    }

    [EnableRateLimiting(RateLimitPolicies.CsvExport)]
    public async Task<IActionResult> OpenContractsExport(string? format)
    {
        var model = await _reconciliation.BuildOpenContractsAsync();
        var rows = model.Rows.ToList();
        return TabularExportSupport.File(this, format, new TabularExportDocument
        {
            FileNameStem = "PTG_Open_Contracts",
            TitleFa = "قراردادهای باز",
            TitleEn = "Open Contracts",
            KnownRowCount = rows.Count,
            ForceLandscape = true,
            Columns =
            [
                new("قرارداد", "Contract", Width: 17), new("جنس", "Product", Width: 15), new("واحد", "Unit", Width: 12),
                new("مقدار قرارداد MT", "Contract quantity MT", TabularExportValueType.Number, 16),
                new("بارگیری MT", "Loaded MT", TabularExportValueType.Number, 14), new("رسید MT", "Received MT", TabularExportValueType.Number, 14),
                new("فروش MT", "Sold MT", TabularExportValueType.Number, 14), new("باقیمانده MT", "Remaining MT", TabularExportValueType.Number, 15),
                new("وضعیت", "Status", Width: 14)
            ],
            Rows = rows.Select(row => new TabularExportRow(
            [
                TabularExportCell.Text(row.ContractNumber), TabularExportCell.Text(row.ProductName), TabularExportCell.Text(row.ContractUnitText),
                TabularExportCell.Number(row.ContractQuantityMt), TabularExportCell.Number(row.LoadedQuantityMt),
                TabularExportCell.Number(row.ReceivedQuantityMt), TabularExportCell.Number(row.SoldQuantityMt),
                TabularExportCell.Number(row.RemainingQuantityMt), TabularExportCell.Text(row.Status)
            ]))
        });
    }

    [EnableRateLimiting(RateLimitPolicies.CsvExport)]
    public async Task<IActionResult> OpenShipmentsExport(string? format)
    {
        var model = await _reconciliation.BuildOpenShipmentsAsync();
        var rows = model.ShipmentsWithoutSales.Select(row =>
                (KindFa: "محموله بدون فروش", KindEn: "Shipment without sales", Reference: (string?)row.ShipmentCode,
                    ContractNumber: (string?)row.ContractNumber, ContractUnitText: row.ContractUnitText,
                    Quantity: row.QuantityMt, Status: row.Status, Reason: (string?)null))
            .Concat(model.ShipmentsWithoutExpenses.Select(row =>
                (KindFa: "محموله بدون مصرف", KindEn: "Shipment without expenses", Reference: (string?)row.ShipmentCode,
                    ContractNumber: (string?)row.ContractNumber, ContractUnitText: row.ContractUnitText,
                    Quantity: row.QuantityMt, Status: row.Status, Reason: (string?)null)))
            .Concat(model.DispatchesWithoutReceipt.Select(row =>
                (KindFa: "ارسال بدون رسید", KindEn: "Dispatch without receipt", Reference: (string?)$"#{row.DispatchId}",
                    ContractNumber: (string?)row.ContractNumber, ContractUnitText: row.ContractUnitText,
                    Quantity: row.LoadedQuantityMt, Status: row.Status, Reason: (string?)row.Reason)))
            .ToList();

        return TabularExportSupport.File(this, format, new TabularExportDocument
        {
            FileNameStem = "PTG_Open_Shipments",
            TitleFa = "محموله‌های باز",
            TitleEn = "Open Shipments",
            KnownRowCount = rows.Count,
            Columns =
            [
                new("نوع", "Kind", Width: 22), new("مرجع", "Reference", Width: 16), new("قرارداد", "Contract", Width: 17),
                new("واحد", "Unit", Width: 12), new("مقدار MT", "Quantity MT", TabularExportValueType.Number, 15),
                new("وضعیت", "Status", Width: 14), new("دلیل", "Reason", Width: 28, Wrap: true)
            ],
            Rows = rows.Select(row => new TabularExportRow(
            [
                TabularExportCell.Text(UiText.IsEn(HttpContext) ? row.KindEn : row.KindFa), TabularExportCell.Text(row.Reference),
                TabularExportCell.Text(row.ContractNumber), TabularExportCell.Text(row.ContractUnitText), TabularExportCell.Number(row.Quantity),
                TabularExportCell.Text(row.Status), TabularExportCell.Text(row.Reason)
            ]))
        });
    }

    [EnableRateLimiting(RateLimitPolicies.CsvExport)]
    public async Task<IActionResult> MissingLedgerExport(string? format)
    {
        var model = await _reconciliation.BuildMissingLedgerAsync();
        var rows = model.SalesWithoutLedger.Concat(model.ExpensesWithoutLedger).Concat(model.PaymentsWithoutLedger).ToList();
        return TabularExportSupport.File(this, format, new TabularExportDocument
        {
            FileNameStem = "PTG_Missing_Ledger",
            TitleFa = "اسناد فاقد ثبت دفتر کل",
            TitleEn = "Missing Ledger Entries",
            KnownRowCount = rows.Count,
            Columns =
            [
                new("نوع منبع", "Source type", Width: 18), new("شناسه منبع", "Source ID", TabularExportValueType.Integer, 12),
                new("تاریخ", "Date", TabularExportValueType.Date, 13), new("مرجع", "Reference", Width: 18),
                new("مبلغ USD", "Amount USD", TabularExportValueType.Number, 16), new("وضعیت", "Status", Width: 15)
            ],
            Rows = rows.Select(row => new TabularExportRow(
            [
                TabularExportCell.Text(row.SourceType), TabularExportCell.Integer(row.SourceId), TabularExportCell.Date(row.Date),
                TabularExportCell.Text(row.Reference), TabularExportCell.Number(row.AmountUsd), TabularExportCell.Text(row.Status)
            ]))
        });
    }

    [EnableRateLimiting(RateLimitPolicies.CsvExport)]
    public async Task<IActionResult> NonZeroBalancesExport(string? format)
    {
        var model = await _reconciliation.BuildNonZeroBalancesAsync();
        var rows = model.ContractBalances.Concat(model.CustomerBalances).Concat(model.SupplierBalances).ToList();
        return TabularExportSupport.File(this, format, new TabularExportDocument
        {
            FileNameStem = "PTG_Non_Zero_Balances",
            TitleFa = "مانده‌های غیرصفر",
            TitleEn = "Non-zero Balances",
            KnownRowCount = rows.Count,
            Columns =
            [
                new("نوع", "Entity type", Width: 15), new("شناسه", "Entity ID", TabularExportValueType.Integer, 11),
                new("نام", "Name", Width: 22), new("بدهکار USD", "Debit USD", TabularExportValueType.Number, 16),
                new("بستانکار USD", "Credit USD", TabularExportValueType.Number, 16),
                new("مانده USD", "Balance USD", TabularExportValueType.Number, 16), new("وضعیت", "Status", Width: 14)
            ],
            Rows = rows.Select(row => new TabularExportRow(
            [
                TabularExportCell.Text(row.EntityType), TabularExportCell.Integer(row.EntityId), TabularExportCell.Text(row.Name),
                TabularExportCell.Number(row.DebitUsd), TabularExportCell.Number(row.CreditUsd),
                TabularExportCell.Number(row.BalanceUsd), TabularExportCell.Text(row.Status)
            ]))
        });
    }

    [EnableRateLimiting(RateLimitPolicies.CsvExport)]
    public async Task<IActionResult> IncompleteAfterReceiptExport(string? format)
    {
        var model = await _reconciliation.BuildIncompleteAfterReceiptAsync();
        var rows = model.DirectDispatchesWithoutFinish
            .Concat(model.TransfersInTransit)
            .Concat(model.DispatchSaleAndDeliveryConflicts)
            .Concat(model.DirectDispatchPartialRemaining)
            .ToList();

        return TabularExportSupport.File(this, format, new TabularExportDocument
        {
            FileNameStem = "PTG_Incomplete_After_Receipt",
            TitleFa = "ناتمام‌های بعد از رسید",
            TitleEn = "Incomplete After Receipt",
            KnownRowCount = rows.Count,
            ForceLandscape = true,
            Columns =
            [
                new("نوع مسیر", "Path type", Width: 20), new("قرارداد", "Contract", Width: 17),
                new("تاریخ", "Date", TabularExportValueType.Date, 13),
                new("مقدار MT", "Quantity MT", TabularExportValueType.Number, 15),
                new("ارسال‌شده MT", "Dispatched MT", TabularExportValueType.Number, 15),
                new("باقیمانده MT", "Remaining MT", TabularExportValueType.Number, 15),
                new("موتر", "Truck", Width: 14), new("راننده", "Driver", Width: 18),
                new("مقصد", "Destination", Width: 18), new("وضعیت", "Status", Width: 16),
                new("مشکل", "Issue", Width: 30, Wrap: true)
            ],
            Rows = rows.Select(row => new TabularExportRow(
            [
                TabularExportCell.Text(row.PathTypeText), TabularExportCell.Text(row.ContractNumber),
                TabularExportCell.Date(row.Date), TabularExportCell.Number(row.QuantityMt),
                TabularExportCell.Number(row.DispatchedQuantityMt), TabularExportCell.Number(row.RemainingQuantityMt),
                TabularExportCell.Text(row.TruckPlateNumber), TabularExportCell.Text(row.DriverName),
                TabularExportCell.Text(row.DestinationName), TabularExportCell.Text(row.CurrentStatus),
                TabularExportCell.Text(row.Issue)
            ]))
        });
    }

    [EnableRateLimiting(RateLimitPolicies.CsvExport)]
    public async Task<IActionResult> EmployeeTransactionsExport(string? format)
    {
        var model = await _reconciliation.BuildEmployeeReconciliationAsync();
        var transactionRows = model.TransactionsWithoutLedger
            .Concat(model.TransactionsWithoutCashAccount)
            .Concat(model.LedgerAmountMismatches)
            .Concat(model.CancelledTransactionsWithActiveLedger)
            .ToList();

        var rows = transactionRows
            .Select(row => new TabularExportRow(
            [
                TabularExportCell.Text(row.EmployeeCode), TabularExportCell.Text(row.EmployeeName),
                TabularExportCell.Date(row.TransactionDate), TabularExportCell.Text(row.TransactionTypeName),
                TabularExportCell.Number(row.AmountUsd), TabularExportCell.Number(row.LedgerAmountUsd),
                TabularExportCell.Text(row.CashAccountName), TabularExportCell.Text(row.Reference),
                TabularExportCell.Text(row.Issue), TabularExportCell.Text(row.Status)
            ]))
            .Concat(model.NegativeOrUnexpectedBalances.Select(row => new TabularExportRow(
            [
                TabularExportCell.Text(row.EmployeeCode), TabularExportCell.Text(row.EmployeeName),
                TabularExportCell.Date(null), TabularExportCell.Text(UiText.IsEn(HttpContext) ? "Balance" : "مانده"),
                TabularExportCell.Number(row.BalanceUsd), TabularExportCell.Number(null),
                TabularExportCell.Text(null), TabularExportCell.Text(null),
                TabularExportCell.Text(row.Issue), TabularExportCell.Text(row.Status)
            ])))
            .ToList();

        return TabularExportSupport.File(this, format, new TabularExportDocument
        {
            FileNameStem = "PTG_Employee_Reconciliation",
            TitleFa = "مغایرت‌های معاملات کارمندان",
            TitleEn = "Employee Transaction Reconciliation",
            KnownRowCount = rows.Count,
            ForceLandscape = true,
            Columns =
            [
                new("کد کارمند", "Employee code", Width: 14), new("کارمند", "Employee", Width: 22),
                new("تاریخ", "Date", TabularExportValueType.Date, 13), new("نوع", "Type", Width: 18),
                new("مبلغ USD", "Amount USD", TabularExportValueType.Number, 16),
                new("مبلغ دفتر کل USD", "Ledger USD", TabularExportValueType.Number, 17),
                new("حساب نقد", "Cash account", Width: 20), new("مرجع", "Reference", Width: 18),
                new("مشکل", "Issue", Width: 30, Wrap: true), new("وضعیت", "Status", Width: 15)
            ],
            Rows = rows
        });
    }

    [EnableRateLimiting(RateLimitPolicies.CsvExport)]
    public async Task<IActionResult> RoznamchaExport(string? format)
    {
        var model = await _reconciliation.BuildRoznamchaReconciliationAsync();
        var rows = model.PaymentsWithoutCounterparty
            .Concat(model.PaymentsWithoutCashAccount)
            .Concat(model.LedgerAmountMismatches)
            .Concat(model.ExpenseDoubleCountingRisks)
            .Concat(model.EmployeePaymentsWithoutEmployeeLink)
            .Concat(model.SupplierCustomerPaymentsWithoutProfileLink)
            .ToList();

        return TabularExportSupport.File(this, format, new TabularExportDocument
        {
            FileNameStem = "PTG_Roznamcha_Reconciliation",
            TitleFa = "مغایرت‌های روزنامچه",
            TitleEn = "Roznamcha Reconciliation",
            KnownRowCount = rows.Count,
            ForceLandscape = true,
            Columns =
            [
                new("شناسه سند", "Payment ID", TabularExportValueType.Integer, 12),
                new("تاریخ", "Date", TabularExportValueType.Date, 13),
                new("جهت", "Direction", Width: 14), new("نوع", "Kind", Width: 18),
                new("حساب نقد", "Cash account", Width: 20), new("طرف حساب", "Counterparty", Width: 22),
                new("مبلغ USD", "Amount USD", TabularExportValueType.Number, 16),
                new("مبلغ دفتر کل USD", "Ledger USD", TabularExportValueType.Number, 17),
                new("مرجع", "Reference", Width: 18), new("مشکل", "Issue", Width: 30, Wrap: true),
                new("وضعیت", "Status", Width: 15)
            ],
            Rows = rows.Select(row => new TabularExportRow(
            [
                TabularExportCell.Integer(row.PaymentTransactionId), TabularExportCell.Date(row.PaymentDate),
                TabularExportCell.Text(row.DirectionName), TabularExportCell.Text(row.PaymentKindName),
                TabularExportCell.Text(row.CashAccountName), TabularExportCell.Text(row.CounterpartyName),
                TabularExportCell.Number(row.AmountUsd), TabularExportCell.Number(row.LedgerAmountUsd),
                TabularExportCell.Text(row.Reference), TabularExportCell.Text(row.Issue),
                TabularExportCell.Text(row.Status)
            ]))
        });
    }

    [EnableRateLimiting(RateLimitPolicies.CsvExport)]
    public async Task<IActionResult> SuspenseMoneyExport(
        string? format,
        string? severity = null,
        DateTime? fromDate = null,
        DateTime? toDate = null)
    {
        var isEn = UiText.IsEn(HttpContext);
        var model = await _reconciliation.BuildSuspenseMoneyAsync(severity, fromDate, toDate);
        var rows = model.Items.ToList();

        return TabularExportSupport.File(this, format, new TabularExportDocument
        {
            FileNameStem = "PTG_Suspense_Money",
            TitleFa = "پول‌های معلق",
            TitleEn = "Suspense Money",
            KnownRowCount = rows.Count,
            ForceLandscape = true,
            Filters = TabularExportSupport.FilterSummary(
                ("تاریخ تولید (کابل) / Generated (Kabul)", _businessClock.Today.ToString("yyyy-MM-dd")),
                ("شدت / Severity", severity),
                ("از تاریخ / From", fromDate?.ToString("yyyy-MM-dd")),
                ("تا تاریخ / To", toDate?.ToString("yyyy-MM-dd"))),
            Columns =
            [
                new("تاریخ", "Date", TabularExportValueType.Date, 13), new("نوع سند", "Document type", Width: 26),
                new("مبلغ", "Amount", TabularExportValueType.Number, 16), new("ارز", "Currency", Width: 10),
                new("مبلغ USD", "Amount USD", TabularExportValueType.Number, 16),
                new("طرف حساب", "Counterparty", Width: 22), new("قرارداد", "Contract", Width: 17),
                new("منبع مشکل", "Issue source", Width: 22), new("توضیح", "Explanation", Width: 32, Wrap: true),
                new("شدت", "Severity", Width: 12)
            ],
            Rows = rows.Select(row => new TabularExportRow(
            [
                TabularExportCell.Date(row.Date), TabularExportCell.Text(row.DocumentType),
                TabularExportCell.Number(row.Amount), TabularExportCell.Text(row.Currency),
                TabularExportCell.Number(row.AmountUsd), TabularExportCell.Text(row.CounterpartyName),
                TabularExportCell.Text(row.ContractNumber), TabularExportCell.Text(row.IssueSource),
                TabularExportCell.Text(row.PlainExplanation),
                TabularExportCell.Text(row.Severity == SuspenseSeverity.Critical
                    ? (isEn ? "Critical" : "بحرانی")
                    : (isEn ? "Warning" : "هشدار"))
            ])),
            Totals = new TabularExportRow(
            [
                TabularExportCell.Date(null), TabularExportCell.Text(isEn ? "Total" : "جمع"),
                TabularExportCell.Number(null), TabularExportCell.Text(null),
                TabularExportCell.Number(model.TotalAmountUsd), TabularExportCell.Text(null),
                TabularExportCell.Text(null), TabularExportCell.Text(null),
                TabularExportCell.Text(null), TabularExportCell.Text(null)
            ])
        });
    }

    [EnableRateLimiting(RateLimitPolicies.CsvExport)]
    public async Task<IActionResult> OpenContractsCsv()
    {
        var model = await _reconciliation.BuildOpenContractsAsync();
        return CsvExportSupport.File(this, "open-contracts.csv",
            ["Contract", "Product", "ContractUnit", "ContractQuantityMt", "LoadedQuantityMt", "ReceivedQuantityMt", "SoldQuantityMt", "RemainingQuantityMt", "Status"],
            model.Rows.Select(r => new[]
            {
                r.ContractNumber, r.ProductName, r.ContractUnitText, CsvExportSupport.Decimal(r.ContractQuantityMt),
                CsvExportSupport.Decimal(r.LoadedQuantityMt), CsvExportSupport.Decimal(r.ReceivedQuantityMt),
                CsvExportSupport.Decimal(r.SoldQuantityMt), CsvExportSupport.Decimal(r.RemainingQuantityMt), r.Status
            }));
    }

    [EnableRateLimiting(RateLimitPolicies.CsvExport)]
    public async Task<IActionResult> OpenShipmentsCsv()
    {
        var model = await _reconciliation.BuildOpenShipmentsAsync();
        var rows = model.ShipmentsWithoutSales.Select(r => new[]
            {
                "ShipmentWithoutSales", r.ShipmentCode, r.ContractNumber, r.ContractUnitText, CsvExportSupport.Decimal(r.QuantityMt), r.Status, ""
            })
            .Concat(model.ShipmentsWithoutExpenses.Select(r => new[]
            {
                "ShipmentWithoutExpenses", r.ShipmentCode, r.ContractNumber, r.ContractUnitText, CsvExportSupport.Decimal(r.QuantityMt), r.Status, ""
            }))
            .Concat(model.DispatchesWithoutReceipt.Select(r => new[]
            {
                "DispatchWithoutReceipt", "#" + r.DispatchId, r.ContractNumber, r.ContractUnitText, CsvExportSupport.Decimal(r.LoadedQuantityMt), r.Status, r.Reason
            }));

        return CsvExportSupport.File(this, "open-shipments.csv",
            ["Kind", "Reference", "Contract", "ContractUnit", "QuantityMt", "Status", "Reason"],
            rows);
    }

    [EnableRateLimiting(RateLimitPolicies.CsvExport)]
    public async Task<IActionResult> MissingLedgerCsv()
    {
        var model = await _reconciliation.BuildMissingLedgerAsync();
        var rows = model.SalesWithoutLedger
            .Concat(model.ExpensesWithoutLedger)
            .Concat(model.PaymentsWithoutLedger)
            .Select(r => new[]
            {
                r.SourceType, r.SourceId.ToString(), CsvExportSupport.Date(r.Date), r.Reference,
                CsvExportSupport.Decimal(r.AmountUsd), r.Status
            });

        return CsvExportSupport.File(this, "missing-ledger.csv",
            ["SourceType", "SourceId", "Date", "Reference", "AmountUsd", "Status"],
            rows);
    }

    [EnableRateLimiting(RateLimitPolicies.CsvExport)]
    public async Task<IActionResult> NonZeroBalancesCsv()
    {
        var model = await _reconciliation.BuildNonZeroBalancesAsync();
        var rows = model.ContractBalances.Concat(model.CustomerBalances).Concat(model.SupplierBalances)
            .Select(r => new[]
            {
                r.EntityType, r.EntityId.ToString(), r.Name, CsvExportSupport.Decimal(r.DebitUsd),
                CsvExportSupport.Decimal(r.CreditUsd), CsvExportSupport.Decimal(r.BalanceUsd), r.Status
            });

        return CsvExportSupport.File(this, "non-zero-balances.csv",
            ["EntityType", "EntityId", "Name", "DebitUsd", "CreditUsd", "BalanceUsd", "Status"],
            rows);
    }
}
