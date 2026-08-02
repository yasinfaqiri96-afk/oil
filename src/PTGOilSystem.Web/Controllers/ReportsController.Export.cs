using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using PTGOilSystem.Web.Helpers;
using PTGOilSystem.Web.Infrastructure.RateLimiting;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.Reports;
using PTGOilSystem.Web.Services.Exports;

namespace PTGOilSystem.Web.Controllers;

public partial class ReportsController
{
    [HttpGet]
    [EnableRateLimiting(RateLimitPolicies.CsvExport)]
    public async Task<IActionResult> ReceivablesPayablesExport(
        string? format,
        [FromQuery] ManagementReportFilterViewModel? filter = null,
        CancellationToken cancellationToken = default)
    {
        filter ??= new ManagementReportFilterViewModel();
        var model = await BuildReceivablesPayablesReportAsync(filter);
        cancellationToken.ThrowIfCancellationRequested();
        var rows = model.Rows.ToList();

        return TabularExportSupport.File(this, format, new TabularExportDocument
        {
            FileNameStem = "PTG_Receivables_Payables", TitleFa = "دریافتنی‌ها و پرداختنی‌ها", TitleEn = "Receivables & Payables",
            KnownRowCount = rows.Count,
            Filters = BuildReportExportFilters(filter),
            Columns =
            [
                new("طرف حساب", "Party", Width: 24), new("نوع", "Type", Width: 16), new("شرح مانده", "Balance kind", Width: 18),
                new("اول دوره USD", "Opening USD", TabularExportValueType.Number, 16),
                new("رسید USD", "Received USD", TabularExportValueType.Number, 16),
                new("برد USD", "Given USD", TabularExportValueType.Number, 16),
                new("گردش دوره USD", "Period movement USD", TabularExportValueType.Number, 17),
                new("مانده USD", "Balance USD", TabularExportValueType.Number, 16),
                new("آخرین تاریخ", "Last date", TabularExportValueType.Date, 14)
            ],
            Rows = rows.Select(r => new TabularExportRow(
            [
                TabularExportCell.Text(r.PartyName), TabularExportCell.Text(r.PartyType), TabularExportCell.Text(r.BalanceKind),
                TabularExportCell.Number(r.OpeningBalanceUsd), TabularExportCell.Number(r.DebitUsd),
                TabularExportCell.Number(r.CreditUsd), TabularExportCell.Number(r.PeriodMovementUsd),
                TabularExportCell.Number(r.BalanceUsd),
                TabularExportCell.Date(r.LastEntryDate)
            ])),
            Totals = new TabularExportRow(
            [
                TabularExportCell.Text("جمع / Total"), TabularExportCell.Text(null), TabularExportCell.Text(null),
                TabularExportCell.Number(rows.Sum(r => r.OpeningBalanceUsd)),
                TabularExportCell.Number(rows.Sum(r => r.DebitUsd)), TabularExportCell.Number(rows.Sum(r => r.CreditUsd)),
                TabularExportCell.Number(rows.Sum(r => r.PeriodMovementUsd)),
                TabularExportCell.Number(rows.Sum(r => r.BalanceUsd)), TabularExportCell.Date(null)
            ])
        });
    }

    [HttpGet]
    [EnableRateLimiting(RateLimitPolicies.CsvExport)]
    public async Task<IActionResult> ContractPnlExport(
        string? format,
        [FromQuery] ManagementReportFilterViewModel? filter = null,
        CancellationToken cancellationToken = default)
    {
        filter ??= new ManagementReportFilterViewModel();
        var model = await BuildContractPnlAsync(filter);
        cancellationToken.ThrowIfCancellationRequested();
        var rows = model.PurchaseRows.Concat(model.SaleRows).ToList();

        return TabularExportSupport.File(this, format, new TabularExportDocument
        {
            FileNameStem = "PTG_Contract_PnL", TitleFa = "سود و زیان قراردادها", TitleEn = "Contract P&L",
            KnownRowCount = rows.Count, ForceLandscape = true, Filters = BuildReportExportFilters(filter),
            Columns =
            [
                new("نام قرارداد", "Contract name", Width: 20),
                new("قرارداد", "Contract", Width: 17), new("نوع", "Type", Width: 11), new("جنس", "Product", Width: 17),
                new("طرف قرارداد", "Counterparty", Width: 20), new("وضعیت", "Status", Width: 12),
                new("مقدار قرارداد MT", "Contract qty MT", TabularExportValueType.Number, 16),
                new("بارگیری/فروش MT", "Loaded/sold MT", TabularExportValueType.Number, 16),
                new("ارزش خرید USD", "Purchase value USD", TabularExportValueType.Number, 17),
                new("مصارف USD", "Expenses USD", TabularExportValueType.Number, 16),
                new("درآمد USD", "Revenue USD", TabularExportValueType.Number, 16),
                new("سود/زیان USD", "Profit/loss USD", TabularExportValueType.Number, 17),
                new("حاشیه", "Margin", TabularExportValueType.Percentage, 12),
                new("اطمینان", "Confidence", Width: 14),
                new("فروش بدون COGS", "Sales missing COGS", TabularExportValueType.Number, 14)
            ],
            Rows = rows.Select(r => new TabularExportRow(
            [
                TabularExportCell.Text(r.ContractName), TabularExportCell.Text(r.ContractNumber), TabularExportCell.Text(r.ContractType.ToString()), TabularExportCell.Text(r.ProductName),
                TabularExportCell.Text(r.CounterpartyName), TabularExportCell.Text(r.Status.ToString()), TabularExportCell.Number(r.ContractQuantityMt),
                TabularExportCell.Number(r.ContractType == ContractType.Purchase ? r.TotalLoadedMt : r.TotalSoldMt),
                TabularExportCell.Number(r.PurchaseValueUsd), TabularExportCell.Number(r.TotalCostUsd - r.PurchaseValueUsd),
                TabularExportCell.Number(r.TotalRevenueUsd), TabularExportCell.Number(r.GrossMarginUsd),
                TabularExportCell.Percentage(r.MarginPercent.HasValue ? r.MarginPercent.Value / 100m : null),
                TabularExportCell.Text(r.PnlConfidence.ToString()), TabularExportCell.Number(r.UncostedSaleCount)
            ]))
        });
    }

    /// <summary>
    /// سود و زیان شرکت. دقیقاً همان <c>BuildCompanyFinancialOverviewAsync</c> صفحه را با همان
    /// فیلتر صدا می‌زند؛ هیچ عدد مالی اینجا دوباره محاسبه نمی‌شود. بخش دوم خروجی، همان
    /// قراردادهای برتر صفحه است تا جمع شرکت با جمع قراردادها قابل تطبیق باشد.
    /// </summary>
    [HttpGet]
    [EnableRateLimiting(RateLimitPolicies.CsvExport)]
    public async Task<IActionResult> CompanyOverviewExport(
        string? format,
        [FromQuery] ManagementReportFilterViewModel? filter = null,
        CancellationToken cancellationToken = default)
    {
        filter ??= new ManagementReportFilterViewModel();
        var model = await BuildCompanyFinancialOverviewAsync(filter);
        cancellationToken.ThrowIfCancellationRequested();

        var lines = new (string Fa, string En, decimal Value)[]
        {
            ("درآمد فروش محقق‌شده", "Realised sales revenue", model.RevenueUsd),
            ("بهای تمام‌شدهٔ فروش", "Cost of goods sold", -model.PurchaseCostUsd),
            ("سود ناخالص", "Gross profit", model.GrossProfitUsd),
            ("مصارف", "Expenses", -model.ExpenseUsd),
            ("ضایعات و کسری", "Losses and shortages", -model.LossCostUsd),
            ("سود تسعیر ارز", "Exchange gain", model.ExchangeGainUsd),
            ("زیان تسعیر ارز", "Exchange loss", -model.ExchangeLossUsd),
            ("سود خالص", "Net profit", model.NetProfitUsd),
            ("گردش خالص نقدی", "Net cash movement", model.NetCashMovementUsd),
            ("دریافتنی از مشتریان", "Customer receivable", model.CustomerReceivableUsd),
            ("پرداختنی به تأمین‌کنندگان", "Supplier payable", model.SupplierPayableUsd),
            ("خالص صراف", "Sarraf net", model.SarrafNetUsd)
        };

        var isEn = UiText.IsEn(HttpContext);
        var filters = BuildReportExportFilters(filter).ToList();
        filters.AddRange(TabularExportSupport.FilterSummary(
            ("تاریخ تولید (کابل) / Generated (Kabul)", _businessClock.Today.ToString("yyyy-MM-dd")),
            ("اطمینان / Confidence", model.PnlConfidence.ToString()),
            ("فروش بدون بهای تمام‌شده / Sales missing COGS", model.UncostedSaleCount.ToString())));

        return TabularExportSupport.File(this, format, new TabularExportDocument
        {
            FileNameStem = "PTG_Company_PnL",
            TitleFa = "سود و زیان شرکت",
            TitleEn = "Company P&L",
            KnownRowCount = lines.Length + model.TopContracts.Count,
            Filters = filters,
            Columns =
            [
                new("شرح", "Line", Width: 30),
                new("مبلغ USD", "Amount USD", TabularExportValueType.Number, 18),
                new("منبع", "Source", Width: 26)
            ],
            Rows = lines
                .Select(line => new TabularExportRow(
                [
                    TabularExportCell.Text(isEn ? line.En : line.Fa),
                    TabularExportCell.Number(line.Value),
                    TabularExportCell.Text(isEn ? "Company total" : "جمع شرکت")
                ]))
                .Concat(model.TopContracts.Select(contract => new TabularExportRow(
                [
                    TabularExportCell.Text(contract.ContractNumber),
                    TabularExportCell.Number(contract.GrossMarginUsd),
                    TabularExportCell.Text((isEn ? "Contract — " : "قرارداد — ") + contract.PnlConfidence)
                ]))),
            Totals = new TabularExportRow(
            [
                TabularExportCell.Text(isEn ? "Net profit / Total" : "سود خالص / جمع"),
                TabularExportCell.Number(model.NetProfitUsd),
                TabularExportCell.Text(null)
            ])
        });
    }

    /// <summary>
    /// خروجی جریان نقدی. همان <c>BuildCashFlowReportAsync</c> صفحه را با همان فیلتر صدا می‌زند؛
    /// هیچ عدد نقدی اینجا دوباره محاسبه نمی‌شود. بخش دوم خروجی، همان تفکیک حساب‌های صفحه است.
    /// </summary>
    [HttpGet]
    [EnableRateLimiting(RateLimitPolicies.CsvExport)]
    public async Task<IActionResult> CashFlowExport(
        string? format,
        [FromQuery] ManagementReportFilterViewModel? filter = null,
        CancellationToken cancellationToken = default)
    {
        filter ??= new ManagementReportFilterViewModel();
        var model = await BuildCashFlowReportAsync(filter);
        cancellationToken.ThrowIfCancellationRequested();

        var isEn = UiText.IsEn(HttpContext);
        var groupLabel = isEn ? "Group" : "گروه";
        var accountLabel = isEn ? "Cash account" : "حساب نقد / بانک";

        var rows = model.Rows
            .Select(r => new TabularExportRow(
            [
                TabularExportCell.Text(groupLabel), TabularExportCell.Text(r.GroupName), TabularExportCell.Text(null),
                TabularExportCell.Number(r.InflowUsd), TabularExportCell.Number(r.OutflowUsd),
                TabularExportCell.Number(r.NetUsd), TabularExportCell.Integer(r.Count)
            ]))
            .Concat(model.AccountRows.Select(r => new TabularExportRow(
            [
                TabularExportCell.Text(accountLabel), TabularExportCell.Text(r.CashAccountName), TabularExportCell.Text(r.Currency),
                TabularExportCell.Number(r.InflowUsd), TabularExportCell.Number(r.OutflowUsd),
                TabularExportCell.Number(r.NetUsd), TabularExportCell.Integer(null)
            ])))
            .ToList();

        return TabularExportSupport.File(this, format, new TabularExportDocument
        {
            FileNameStem = "PTG_Cash_Flow", TitleFa = "جریان نقدی", TitleEn = "Cash Flow",
            KnownRowCount = rows.Count, Filters = BuildReportExportFilters(filter),
            Columns =
            [
                new("بخش", "Section", Width: 18), new("عنوان", "Name", Width: 26), new("ارز", "Currency", Width: 10),
                new("ورودی USD", "Inflow USD", TabularExportValueType.Number, 16),
                new("خروجی USD", "Outflow USD", TabularExportValueType.Number, 16),
                new("خالص USD", "Net USD", TabularExportValueType.Number, 16),
                new("تعداد", "Count", TabularExportValueType.Integer, 11)
            ],
            Rows = rows,
            Totals = new TabularExportRow(
            [
                TabularExportCell.Text(isEn ? "Total" : "جمع"), TabularExportCell.Text(null), TabularExportCell.Text(null),
                TabularExportCell.Number(model.TotalInflowUsd), TabularExportCell.Number(model.TotalOutflowUsd),
                TabularExportCell.Number(model.NetCashFlowUsd), TabularExportCell.Integer(null)
            ])
        });
    }

    /// <summary>
    /// خروجی موجودی و عملیات. همان <c>BuildInventoryOperationsReportAsync</c> صفحه را با همان
    /// فیلتر صدا می‌زند؛ سه بخش صفحه (جنس، ترمینال، اخطارها) در یک جدول با ستون «بخش» می‌آید.
    /// </summary>
    [HttpGet]
    [EnableRateLimiting(RateLimitPolicies.CsvExport)]
    public async Task<IActionResult> InventoryOperationsExport(
        string? format,
        [FromQuery] ManagementReportFilterViewModel? filter = null,
        CancellationToken cancellationToken = default)
    {
        filter ??= new ManagementReportFilterViewModel();
        var model = await BuildInventoryOperationsReportAsync(filter);
        cancellationToken.ThrowIfCancellationRequested();

        var isEn = UiText.IsEn(HttpContext);
        var productLabel = isEn ? "Product" : "جنس";
        var terminalLabel = isEn ? "Terminal" : "ترمینال";
        var warningLabel = isEn ? "Warning" : "اخطار";

        var rows = model.ProductRows
            .Select(r => new TabularExportRow(
            [
                TabularExportCell.Text(productLabel), TabularExportCell.Text(r.GroupName), TabularExportCell.Text(r.SecondaryName),
                TabularExportCell.Number(r.QuantityMt), TabularExportCell.Integer(r.MovementCount), TabularExportCell.Date(r.LastMovementDate)
            ]))
            .Concat(model.TerminalRows.Select(r => new TabularExportRow(
            [
                TabularExportCell.Text(terminalLabel), TabularExportCell.Text(r.GroupName), TabularExportCell.Text(r.SecondaryName),
                TabularExportCell.Number(r.QuantityMt), TabularExportCell.Integer(r.MovementCount), TabularExportCell.Date(r.LastMovementDate)
            ])))
            .Concat(model.Warnings.Select(w => new TabularExportRow(
            [
                TabularExportCell.Text(warningLabel), TabularExportCell.Text(w.Title), TabularExportCell.Text(w.Description),
                TabularExportCell.Number(null), TabularExportCell.Integer(w.Count), TabularExportCell.Date(null)
            ])))
            .ToList();

        return TabularExportSupport.File(this, format, new TabularExportDocument
        {
            FileNameStem = "PTG_Inventory_Operations", TitleFa = "موجودی و عملیات", TitleEn = "Inventory & Operations",
            KnownRowCount = rows.Count, ForceLandscape = true, Filters = BuildReportExportFilters(filter),
            Columns =
            [
                new("بخش", "Section", Width: 16), new("عنوان", "Name", Width: 26),
                new("شرح", "Detail", Width: 30, Wrap: true),
                new("مقدار MT", "Quantity MT", TabularExportValueType.Number, 16),
                new("تعداد", "Count", TabularExportValueType.Integer, 12),
                new("آخرین حرکت", "Last movement", TabularExportValueType.Date, 14)
            ],
            Rows = rows
        });
    }

    private static IReadOnlyList<TabularExportFilter> BuildReportExportFilters(ManagementReportFilterViewModel filter)
        => TabularExportSupport.FilterSummary(
            ("از تاریخ / From", filter.FromDate?.ToString("yyyy-MM-dd")), ("تا تاریخ / To", filter.ToDate?.ToString("yyyy-MM-dd")),
            ("جنس / Product", filter.ProductId), ("قرارداد / Contract", filter.ContractId),
            ("مشتری / Customer", filter.CustomerId), ("تأمین‌کننده / Supplier", filter.SupplierId),
            ("ترمینال / Terminal", filter.TerminalId), ("مخزن / Tank", filter.StorageTankId));
}
