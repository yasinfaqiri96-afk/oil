using Microsoft.AspNetCore.Mvc;
using PTGOilSystem.Web.Helpers;
using PTGOilSystem.Web.Models.PartyStatements;
using PTGOilSystem.Web.Services.Exports;
using PTGOilSystem.Web.Services.PartyStatements;

namespace PTGOilSystem.Web.Controllers;

// خروجی رسمی صورت‌حساب تأمین‌کننده را از همان داده/فیلترِ صفحه می‌سازد.
// Excel: دو شیت (Contract Summary + Contract Details). PDF از این مسیر نمی‌آید — کنترلر آن را
// به سند رسمی صورت‌حساب (PartyStatementPdfDocument) می‌سپارد تا دیزاین با بقیهٔ تب‌ها یکی بماند.
// اعداد از grouping/rowsِ موجود می‌آیند؛ هیچ محاسبهٔ مالی جدیدی اینجا انجام نمی‌شود.
internal static class SupplierStatementExport
{
    private static string FlowTitle(string fa, string en, string currency, bool isEnglish)
        => $"{(isEnglish ? en : fa)} ({currency})";

    public static IActionResult Build(
        Controller controller,
        string? format,
        PartyStatementResult statement,
        SupplierContractStatementViewModel grouping,
        bool includeDetails = true)
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

        // فیلتر بی‌مقدار در سربرگ خروجی نوشته نمی‌شود.
        filters = filters.Where(f => !string.IsNullOrWhiteSpace(f.Value)).ToList();

        // زبان خروجی همان زبان صفحه است؛ فقط عنوان‌ها ترجمه می‌شوند، نه اعداد و جهت‌ها.
        var isEnglish = UiText.IsEn(controller.HttpContext);
        var summary = BuildSummaryDocument(statement, grouping, stem, currencyLabel, filters, isEnglish);

        // PDF فقط خلاصه (هر قرارداد یک سطر).
        if (TabularExportSupport.ParseFormat(format) == TabularExportFormat.Pdf)
        {
            return TabularExportSupport.File(controller, format, summary);
        }

        if (!includeDetails)
        {
            return TabularExportSupport.File(controller, format, summary);
        }

        var details = BuildDetailsDocument(statement, stem, currencyLabel, filters, isEnglish);
        return TabularExportSupport.File(controller, format, new[] { summary, details });
    }

    public static IActionResult BuildDetailsOnly(
        Controller controller,
        string? format,
        PartyStatementResult statement)
    {
        var currency = statement.Summary.BaseCurrencyCode;
        var filters = new List<TabularExportFilter>
        {
            new("طرف‌حساب", "Party", statement.PartyInfo.Name),
            new("از تاریخ", "From date", controller.Request.Query["FromDate"].ToString()),
            new("تا تاریخ", "To date", controller.Request.Query["ToDate"].ToString()),
            new("ارز", "Currency", currency),
            new("نوع سند", "Document type", controller.Request.Query["SourceType"].ToString()),
            new("جستجو", "Search", controller.Request.Query["Search"].ToString())
        };
        filters = filters.Where(f => !string.IsNullOrWhiteSpace(f.Value)).ToList();
        var document = BuildDetailsDocument(
            statement,
            $"Statement_{statement.PartyInfo.Name}",
            currency,
            filters,
            UiText.IsEn(controller.HttpContext));
        return TabularExportSupport.File(controller, format, document);
    }

    // خروجی پنل «جزئیات قرارداد» — همان فکت‌ها و همان جدول «گردش قرارداد» که روی صفحه
    // باز می‌شود، نه کل صورت‌حساب. سطرها از همان statementِ فیلترشده به قرارداد می‌آیند.
    public static IActionResult BuildContractDetails(
        Controller controller,
        string? format,
        PartyStatementResult statement,
        SupplierContractStatementBuilder.ContractFacts? facts,
        int contractId)
    {
        var currency = statement.Summary.BaseCurrencyCode;
        var isRub = statement.Summary.IsRubPresentation;
        var isEnglish = UiText.IsEn(controller.HttpContext);
        decimal? Money(decimal? usd, decimal? rub) => isRub ? rub : usd;
        string? Qty(decimal? value) => value?.ToString("N3");

        var contractNumber = statement.Rows
            .Select(row => row.ContractNumber)
            .FirstOrDefault(number => !string.IsNullOrWhiteSpace(number))
            ?? contractId.ToString();
        var remaining = facts?.ContractQuantityMt.HasValue == true && facts.LoadedQuantityMt.HasValue
            ? facts.ContractQuantityMt.Value - facts.LoadedQuantityMt.Value
            : (decimal?)null;

        var filters = new List<TabularExportFilter>
        {
            new("طرف‌حساب", "Party", statement.PartyInfo.Name),
            new("قرارداد", "Contract", $"قرارداد #{contractNumber}"),
            new("محصول", "Product", facts?.ProductName),
            new("نرخ هر تن (USD)", "Unit price (USD)", facts?.UnitPriceUsd?.ToString("N2")),
            new("ارزش کل قرارداد (USD)", "Contract value (USD)", facts?.ContractValueUsd?.ToString("N2")),
            new("مقدار قرارداد (MT)", "Contract quantity (MT)", Qty(facts?.ContractQuantityMt)),
            new("بارگیری‌شده (MT)", "Loaded (MT)", Qty(facts?.LoadedQuantityMt)),
            new("تعهد باقی‌مانده (MT)", "Remaining (MT)", Qty(remaining)),
            new("از تاریخ", "From date", controller.Request.Query["FromDate"].ToString()),
            new("تا تاریخ", "To date", controller.Request.Query["ToDate"].ToString()),
            new("ارز", "Currency", currency)
        };

        var rows = statement.Rows.Where(row => !row.IsOpeningBalance).ToList();
        var document = new TabularExportDocument
        {
            FileNameStem = $"Statement_{statement.PartyInfo.Name}_Contract_{contractNumber}",
            TitleFa = $"گردش قرارداد #{contractNumber} — {statement.PartyInfo.Name}",
            TitleEn = $"Contract #{contractNumber} activity — {statement.PartyInfo.Name}",
            KnownRowCount = rows.Count,
            ForceLandscape = true,
            Filters = filters.Where(f => !string.IsNullOrWhiteSpace(f.Value)).ToList(),
            Columns =
            [
                new("تاریخ", "Date", TabularExportValueType.Date, 12),
                new("شرح", "Description", Width: 38, Wrap: true),
                new("مرجع", "Reference", Width: 20, Wrap: true),
                new(FlowTitle("رسیدگی", "Debit", currency, false), FlowTitle("رسیدگی", "Debit", currency, true), TabularExportValueType.Number, 15),
                new(FlowTitle("بردگی", "Credit", currency, false), FlowTitle("بردگی", "Credit", currency, true), TabularExportValueType.Number, 15),
                new(FlowTitle("بیلانس", "Balance", currency, false), FlowTitle("بیلانس", "Balance", currency, true), TabularExportValueType.Number, 15)
            ],
            Rows = rows.Select(row => new TabularExportRow(
            [
                TabularExportCell.Date(row.Date),
                TabularExportCell.Text(PartyStatementFormatting.ShortDescription(
                    row.DescriptionFor(isEnglish),
                    PartyStatementFormatting.ExportDescriptionMaxLength)),
                TabularExportCell.Text(PartyStatementFormatting.ShortReference(row.Reference)),
                TabularExportCell.Number(Money(row.ReceiptBase, row.ReceiptRub)),
                TabularExportCell.Number(Money(row.OutflowBase, row.OutflowRub)),
                TabularExportCell.Number(Money(row.RunningBalance, row.RunningBalanceRub))
            ])).ToList()
        };
        return TabularExportSupport.File(controller, format, document);
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
                new("قرارداد", "Contract", Width: 26, Wrap: true),
                new("مبلغ کل قرارداد (USD)", "Contract total (USD)", TabularExportValueType.Number, 18),
                new("ارزش مقدار بارگیری‌شده", "Loaded value", TabularExportValueType.Number, 18),
                new("پرداخت / دریافت", "Payment / receipt", TabularExportValueType.Number, 16),
                new("بیلانس قرارداد", "Contract balance", TabularExportValueType.Number, 16)
            ],
            Rows = grouping.Rows.Select(row => new TabularExportRow(
            [
                TabularExportCell.Integer(row.Sequence),
                TabularExportCell.Text(ContractTitle(row)),
                TabularExportCell.Number(row.ContractValueUsd),
                TabularExportCell.Number(Money(row.ConfirmedValue, row.ConfirmedValueRub)),
                TabularExportCell.Number(Money(row.SettlementTotal, row.SettlementTotalRub)),
                TabularExportCell.Number(isRub ? row.BalanceRub : row.Balance)
            ])).ToList(),
            Totals = new TabularExportRow(
            [
                TabularExportCell.Text(null),
                TabularExportCell.Text(isEnglish ? "Period total" : "جمع دوره"),
                TabularExportCell.Text(null),
                TabularExportCell.Number(Money(grouping.TotalConfirmedValue, grouping.TotalConfirmedValueRub)),
                TabularExportCell.Number(Money(grouping.TotalSettlement, grouping.TotalSettlementRub)),
                TabularExportCell.Number(isRub
                    ? statement.Summary.ClosingBalanceRub
                    : statement.Summary.ClosingBalance)
            ])
        };
    }

    // عنوان قرارداد دقیقاً همان چیزی است که جدول صفحه نشان می‌دهد: «قرارداد #…» و
    // در صورت وجود، سهم شریک. هیچ عددی اینجا ساخته نمی‌شود.
    private static string ContractTitle(SupplierContractStatementRow row)
    {
        var title = row.ContractId.HasValue && !string.IsNullOrWhiteSpace(row.ContractNumber)
            ? $"قرارداد #{row.ContractNumber}"
            : row.Title;
        return row.SharePercent.HasValue
            ? $"{title} — سهم شریک {row.SharePercent.Value:N2}٪"
            : title;
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
        var rows = statement.Rows.ToList();

        // ستون‌هایی که برای این طرف‌حساب هیچ داده‌ای ندارند (صراف: قرارداد، مقدار، نرخ واحد)
        // اصلاً نوشته نمی‌شوند تا شیت شلوغ و پر از خانهٔ خالی نشود.
        var showContract = rows.Any(row => !string.IsNullOrWhiteSpace(row.ContractNumber));
        var showQuantity = rows.Any(row => row.Quantity.HasValue);
        var showUnitPrice = rows.Any(row => row.UnitPrice.HasValue);

        // سند روبلی: مبلغ اصلی همان سند به روبل. وقتی نمایش خودش روبلی است، ستون‌های
        // رسیدگی/بردگی از قبل روبل‌اند و این ستون تکراری می‌شود. مبلغ ثبت‌شدهٔ سند است،
        // نه تبدیل جدید.
        static bool IsRubRow(PartyStatementRow row)
            => row.OriginalAmount.HasValue
                && string.Equals(row.OriginalCurrency, "RUB", StringComparison.OrdinalIgnoreCase);
        var showRubAmount = !isRub && rows.Any(IsRubRow);

        var columns = new List<TabularExportColumn>();
        if (showContract) columns.Add(new("قرارداد", "Contract", Width: 14));
        columns.Add(new("تاریخ", "Date", TabularExportValueType.Date, 12));
        columns.Add(new("مرجع", "Reference", Width: 20, Wrap: true));
        columns.Add(new("شرح", "Description", Width: 38, Wrap: true));
        if (showRubAmount) columns.Add(new("مبلغ روبل", "Amount (RUB)", TabularExportValueType.Number, 16));
        if (showQuantity) columns.Add(new("مقدار", "Quantity", TabularExportValueType.Number, 12));
        if (showUnitPrice) columns.Add(new("نرخ واحد", "Unit price", TabularExportValueType.Number, 12));
        columns.Add(new(FlowTitle("رسیدگی", "Debit", currency, false), FlowTitle("رسیدگی", "Debit", currency, true), TabularExportValueType.Number, 15));
        columns.Add(new(FlowTitle("بردگی", "Credit", currency, false), FlowTitle("بردگی", "Credit", currency, true), TabularExportValueType.Number, 15));
        columns.Add(new(FlowTitle("بیلانس", "Balance", currency, false), FlowTitle("بیلانس", "Balance", currency, true), TabularExportValueType.Number, 15));

        TabularExportRow BuildRow(PartyStatementRow row)
        {
            var cells = new List<TabularExportCell>(columns.Count);
            if (showContract) cells.Add(TabularExportCell.Text(row.ContractNumber));
            cells.Add(TabularExportCell.Date(row.Date));
            cells.Add(TabularExportCell.Text(PartyStatementFormatting.ShortReference(row.Reference)));
            cells.Add(TabularExportCell.Text(PartyStatementFormatting.ShortDescription(
                row.DescriptionFor(isEnglish),
                PartyStatementFormatting.ExportDescriptionMaxLength)));
            if (showRubAmount) cells.Add(TabularExportCell.Number(IsRubRow(row) ? row.OriginalAmount : null));
            if (showQuantity) cells.Add(TabularExportCell.Number(row.Quantity));
            if (showUnitPrice) cells.Add(TabularExportCell.Number(row.UnitPrice));
            cells.Add(TabularExportCell.Number(Money(row.ReceiptBase, row.ReceiptRub)));
            cells.Add(TabularExportCell.Number(Money(row.OutflowBase, row.OutflowRub)));
            cells.Add(TabularExportCell.Number(Money(row.RunningBalance, row.RunningBalanceRub)));
            return new TabularExportRow(cells);
        }

        return new TabularExportDocument
        {
            FileNameStem = stem,
            TitleFa = "گردش حساب رسمی",
            TitleEn = "Official Statement",
            KnownRowCount = rows.Count,
            ForceLandscape = true,
            Filters = filters,
            Columns = columns,
            Rows = rows.Select(BuildRow).ToList()
        };
    }
}
