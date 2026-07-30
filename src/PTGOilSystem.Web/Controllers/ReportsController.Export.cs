using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
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
                TabularExportCell.Text(r.ContractNumber), TabularExportCell.Text(r.ContractType.ToString()), TabularExportCell.Text(r.ProductName),
                TabularExportCell.Text(r.CounterpartyName), TabularExportCell.Text(r.Status.ToString()), TabularExportCell.Number(r.ContractQuantityMt),
                TabularExportCell.Number(r.ContractType == ContractType.Purchase ? r.TotalLoadedMt : r.TotalSoldMt),
                TabularExportCell.Number(r.PurchaseValueUsd), TabularExportCell.Number(r.TotalCostUsd - r.PurchaseValueUsd),
                TabularExportCell.Number(r.TotalRevenueUsd), TabularExportCell.Number(r.GrossMarginUsd),
                TabularExportCell.Percentage(r.MarginPercent.HasValue ? r.MarginPercent.Value / 100m : null),
                TabularExportCell.Text(r.PnlConfidence.ToString()), TabularExportCell.Number(r.UncostedSaleCount)
            ]))
        });
    }

    private static IReadOnlyList<TabularExportFilter> BuildReportExportFilters(ManagementReportFilterViewModel filter)
        => TabularExportSupport.FilterSummary(
            ("از تاریخ / From", filter.FromDate?.ToString("yyyy-MM-dd")), ("تا تاریخ / To", filter.ToDate?.ToString("yyyy-MM-dd")),
            ("جنس / Product", filter.ProductId), ("قرارداد / Contract", filter.ContractId),
            ("مشتری / Customer", filter.CustomerId), ("تأمین‌کننده / Supplier", filter.SupplierId),
            ("ترمینال / Terminal", filter.TerminalId), ("مخزن / Tank", filter.StorageTankId));
}
