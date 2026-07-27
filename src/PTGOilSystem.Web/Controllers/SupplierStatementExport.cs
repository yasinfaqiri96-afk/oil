using Microsoft.AspNetCore.Mvc;
using PTGOilSystem.Web.Models.PartyStatements;
using PTGOilSystem.Web.Services.Exports;

namespace PTGOilSystem.Web.Controllers;

// خروجی رسمی صورت‌حساب تأمین‌کننده را از همان داده/فیلترِ صفحه می‌سازد.
// Excel: دو شیت (Contract Summary + Contract Details). PDF از این مسیر نمی‌آید — کنترلر آن را
// به سند رسمی صورت‌حساب (PartyStatementPdfDocument) می‌سپارد تا دیزاین با بقیهٔ تب‌ها یکی بماند.
// اعداد از grouping/rowsِ موجود می‌آیند؛ هیچ محاسبهٔ مالی جدیدی اینجا انجام نمی‌شود.
internal static class SupplierStatementExport
{
    public static IActionResult Build(
        Controller controller,
        string? format,
        PartyStatementResult statement,
        SupplierContractStatementViewModel grouping)
    {
        var request = controller.Request;
        var currencyLabel = statement.Summary.BaseCurrencyCode;
        var stem = $"Statement_{statement.PartyInfo.Name}";

        var filters = new List<TabularExportFilter>
        {
            new("طرف‌حساب", "Party", statement.PartyInfo.Name),
            new("از تاریخ", "From date", request.Query["FromDate"].ToString()),
            new("تا تاریخ", "To date", request.Query["ToDate"].ToString()),
            new("ارز", "Currency", currencyLabel)
        };

        var summary = BuildSummaryDocument(statement, grouping, stem, currencyLabel, filters);

        // PDF فقط خلاصه (هر قرارداد یک سطر).
        if (TabularExportSupport.ParseFormat(format) == TabularExportFormat.Pdf)
        {
            return TabularExportSupport.File(controller, format, summary);
        }

        var details = BuildDetailsDocument(statement, stem, currencyLabel, filters);
        return TabularExportSupport.File(controller, format, new[] { summary, details });
    }

    private static TabularExportDocument BuildSummaryDocument(
        PartyStatementResult statement,
        SupplierContractStatementViewModel grouping,
        string stem,
        string currency,
        IReadOnlyList<TabularExportFilter> filters)
    {
        var isRub = grouping.IsRub;
        decimal? Money(decimal usd, decimal? rub) => isRub ? rub : usd;

        return new TabularExportDocument
        {
            FileNameStem = stem,
            TitleFa = $"خلاصهٔ قراردادها — {statement.PartyInfo.Name}",
            TitleEn = $"Contract Summary — {statement.PartyInfo.Name}",
            KnownRowCount = grouping.Rows.Count,
            ForceLandscape = true,
            Filters = filters,
            Columns =
            [
                new("شماره", "No", TabularExportValueType.Integer, 8),
                new("قرارداد", "Contract", Width: 22, Wrap: true),
                new("محصول", "Product", Width: 16),
                new("مقدار قرارداد", "Contract MT", TabularExportValueType.Number, 14),
                new("بارگیری‌شده", "Loaded MT", TabularExportValueType.Number, 14),
                new("ارزش قرارداد", "Contract value", TabularExportValueType.Number, 16),
                new($"بدهکار ({currency})", "Debit", TabularExportValueType.Number, 15),
                new($"بستانکار ({currency})", "Credit", TabularExportValueType.Number, 15),
                new($"مانده ({currency})", "Balance", TabularExportValueType.Number, 15)
            ],
            Rows = grouping.Rows.Select(row => new TabularExportRow(
            [
                TabularExportCell.Integer(row.Sequence),
                TabularExportCell.Text(row.ContractNumber ?? row.Title),
                TabularExportCell.Text(row.ProductName),
                TabularExportCell.Number(row.ContractQuantityMt),
                TabularExportCell.Number(row.LoadedQuantityMt),
                TabularExportCell.Number(row.ContractValueUsd),
                TabularExportCell.Number(Money(row.Debit, row.DebitRub)),
                TabularExportCell.Number(Money(row.Credit, row.CreditRub)),
                TabularExportCell.Number(Money(row.Balance, row.BalanceRub))
            ])).ToList(),
            Totals = new TabularExportRow(
            [
                TabularExportCell.Text(null),
                TabularExportCell.Text("مجموع دوره"),
                TabularExportCell.Text(null),
                TabularExportCell.Text(null),
                TabularExportCell.Text(null),
                TabularExportCell.Text(null),
                TabularExportCell.Number(Money(grouping.TotalDebit, grouping.TotalDebitRub)),
                TabularExportCell.Number(Money(grouping.TotalCredit, grouping.TotalCreditRub)),
                TabularExportCell.Number(Money(grouping.ClosingBalance, grouping.ClosingBalanceRub))
            ])
        };
    }

    private static TabularExportDocument BuildDetailsDocument(
        PartyStatementResult statement,
        string stem,
        string currency,
        IReadOnlyList<TabularExportFilter> filters)
    {
        var isRub = statement.Summary.IsRubPresentation;
        decimal? Money(decimal? usd, decimal? rub) => isRub ? rub : usd;
        var rows = statement.Rows.Where(r => !r.IsOpeningBalance).ToList();

        return new TabularExportDocument
        {
            FileNameStem = stem,
            TitleFa = "جزئیات قراردادها",
            TitleEn = "Contract Details",
            KnownRowCount = rows.Count,
            ForceLandscape = true,
            Filters = filters,
            Columns =
            [
                new("قرارداد", "Contract", Width: 14),
                new("تاریخ", "Date", TabularExportValueType.Date, 13),
                new("شرح", "Description", Width: 28, Wrap: true),
                new("مقدار", "Quantity", TabularExportValueType.Number, 12),
                new("نرخ واحد", "Unit price", TabularExportValueType.Number, 12),
                new($"بدهکار ({currency})", "Debit", TabularExportValueType.Number, 15),
                new($"بستانکار ({currency})", "Credit", TabularExportValueType.Number, 15),
                new($"مانده ({currency})", "Balance", TabularExportValueType.Number, 15)
            ],
            Rows = rows.Select(row => new TabularExportRow(
            [
                TabularExportCell.Text(row.ContractNumber),
                TabularExportCell.Date(row.Date),
                TabularExportCell.Text(row.Description),
                TabularExportCell.Number(row.Quantity),
                TabularExportCell.Number(row.UnitPrice),
                TabularExportCell.Number(Money(row.DebitBase, row.DebitRub)),
                TabularExportCell.Number(Money(row.CreditBase, row.CreditRub)),
                TabularExportCell.Number(Money(row.RunningBalance, row.RunningBalanceRub))
            ])).ToList()
        };
    }
}
