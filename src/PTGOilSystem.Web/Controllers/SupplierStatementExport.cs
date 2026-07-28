using Microsoft.AspNetCore.Mvc;
using PTGOilSystem.Web.Helpers;
using PTGOilSystem.Web.Models.PartyStatements;
using PTGOilSystem.Web.Services.CompanyFlow;
using PTGOilSystem.Web.Services.Exports;

namespace PTGOilSystem.Web.Controllers;

// خروجی رسمی صورت‌حساب تأمین‌کننده را از همان داده/فیلترِ صفحه می‌سازد.
// Excel: دو شیت (Contract Summary + Contract Details). PDF از این مسیر نمی‌آید — کنترلر آن را
// به سند رسمی صورت‌حساب (PartyStatementPdfDocument) می‌سپارد تا دیزاین با بقیهٔ تب‌ها یکی بماند.
// اعداد از grouping/rowsِ موجود می‌آیند؛ هیچ محاسبهٔ مالی جدیدی اینجا انجام نمی‌شود.
internal static class SupplierStatementExport
{
    // عنوان ستون‌های «رسید/برد/بیلانس» فقط از منبع مرکزی می‌آید؛ TabularExport خودش
    // بر اساس زبانِ درخواست بین عنوان فارسی و انگلیسی انتخاب می‌کند.
    private static string FlowTitle(CompanyFlowTextKey key, string currency, bool isEnglish)
        => CompanyFlowText.WithCurrencyParenthesized(key, currency, isEnglish);

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

        // زبان خروجی همان زبان صفحه است؛ فقط عنوان‌ها ترجمه می‌شوند، نه اعداد و جهت‌ها.
        var isEnglish = UiText.IsEn(controller.HttpContext);
        var summary = BuildSummaryDocument(statement, grouping, stem, currencyLabel, filters, isEnglish);

        // PDF فقط خلاصه (هر قرارداد یک سطر).
        if (TabularExportSupport.ParseFormat(format) == TabularExportFormat.Pdf)
        {
            return TabularExportSupport.File(controller, format, summary);
        }

        var details = BuildDetailsDocument(statement, stem, currencyLabel, filters, isEnglish);
        return TabularExportSupport.File(controller, format, new[] { summary, details });
    }

    internal static TabularExportDocument BuildSummaryDocument(
        PartyStatementResult statement,
        SupplierContractStatementViewModel grouping,
        string stem,
        string currency,
        IReadOnlyList<TabularExportFilter> filters,
        bool isEnglish)
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
                new(FlowTitle(CompanyFlowTextKey.Receipt, currency, false), FlowTitle(CompanyFlowTextKey.Receipt, currency, true), TabularExportValueType.Number, 15),
                new(FlowTitle(CompanyFlowTextKey.Outflow, currency, false), FlowTitle(CompanyFlowTextKey.Outflow, currency, true), TabularExportValueType.Number, 15),
                new(FlowTitle(CompanyFlowTextKey.Balance, currency, false), FlowTitle(CompanyFlowTextKey.Balance, currency, true), TabularExportValueType.Number, 15)
            ],
            Rows = grouping.Rows.Select(row => new TabularExportRow(
            [
                TabularExportCell.Integer(row.Sequence),
                TabularExportCell.Text(row.ContractNumber ?? row.Title),
                TabularExportCell.Text(row.ProductName),
                TabularExportCell.Number(row.ContractQuantityMt),
                TabularExportCell.Number(row.LoadedQuantityMt),
                TabularExportCell.Number(row.ContractValueUsd),
                TabularExportCell.Number(Money(row.Receipt, row.ReceiptRub)),
                TabularExportCell.Number(Money(row.Outflow, row.OutflowRub)),
                TabularExportCell.Number(Money(row.Balance, row.BalanceRub))
            ])).ToList(),
            Totals = new TabularExportRow(
            [
                TabularExportCell.Text(null),
                TabularExportCell.Text(CompanyFlowText.Get(CompanyFlowTextKey.PeriodTotal, isEnglish)),
                TabularExportCell.Text(null),
                TabularExportCell.Text(null),
                TabularExportCell.Text(null),
                TabularExportCell.Text(null),
                TabularExportCell.Number(Money(grouping.TotalReceipt, grouping.TotalReceiptRub)),
                TabularExportCell.Number(Money(grouping.TotalOutflow, grouping.TotalOutflowRub)),
                TabularExportCell.Number(Money(grouping.ClosingBalance, grouping.ClosingBalanceRub))
            ])
        };
    }

    internal static TabularExportDocument BuildDetailsDocument(
        PartyStatementResult statement,
        string stem,
        string currency,
        IReadOnlyList<TabularExportFilter> filters,
        bool isEnglish)
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
                new(FlowTitle(CompanyFlowTextKey.Receipt, currency, false), FlowTitle(CompanyFlowTextKey.Receipt, currency, true), TabularExportValueType.Number, 15),
                new(FlowTitle(CompanyFlowTextKey.Outflow, currency, false), FlowTitle(CompanyFlowTextKey.Outflow, currency, true), TabularExportValueType.Number, 15),
                new(FlowTitle(CompanyFlowTextKey.Balance, currency, false), FlowTitle(CompanyFlowTextKey.Balance, currency, true), TabularExportValueType.Number, 15)
            ],
            Rows = rows.Select(row => new TabularExportRow(
            [
                TabularExportCell.Text(row.ContractNumber),
                TabularExportCell.Date(row.Date),
                TabularExportCell.Text(row.DescriptionFor(isEnglish)),
                TabularExportCell.Number(row.Quantity),
                TabularExportCell.Number(row.UnitPrice),
                TabularExportCell.Number(Money(row.ReceiptBase, row.ReceiptRub)),
                TabularExportCell.Number(Money(row.OutflowBase, row.OutflowRub)),
                TabularExportCell.Number(Money(row.RunningBalance, row.RunningBalanceRub))
            ])).ToList()
        };
    }
}
