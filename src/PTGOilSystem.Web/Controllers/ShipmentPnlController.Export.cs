using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using PTGOilSystem.Web.Infrastructure.RateLimiting;
using PTGOilSystem.Web.Helpers;
using PTGOilSystem.Web.Models.ShipmentPnl;
using PTGOilSystem.Web.Services.Exports;
using PTGOilSystem.Web.Services.Time;

namespace PTGOilSystem.Web.Controllers;

public partial class ShipmentPnlController
{
    [EnableRateLimiting(RateLimitPolicies.CsvExport)]
    public async Task<IActionResult> Export(string? format)
    {
        var items = await BuildAllIndexItemsAsync();
        var document = new TabularExportDocument
        {
            FileNameStem = "PTG_Shipment_Profit",
            TitleFa = "سود محموله‌ها",
            TitleEn = "Shipment Profitability",
            KnownRowCount = items.Count,
            ForceLandscape = true,
            Columns =
            [
                new("محموله", "Shipment", Width: 16), new("قرارداد", "Contract", Width: 16), new("واحد", "Unit", Width: 12),
                new("جنس", "Product", Width: 15), new("مشتری", "Customer", Width: 18), new("تأمین‌کننده", "Supplier", Width: 18),
                new("مبدا", "Origin", Width: 14), new("مقصد", "Destination", Width: 14),
                new("مقدار MT", "Quantity MT", TabularExportValueType.Number, 14), new("فروش USD", "Sales USD", TabularExportValueType.Number, 15),
                new("خرید USD", "Purchase cost USD", TabularExportValueType.Number, 16),
                new("مصارف عملیاتی USD", "Operational expenses USD", TabularExportValueType.Number, 18),
                new("هزینه کل USD", "Total cost USD", TabularExportValueType.Number, 16),
                new("سود ناخالص USD", "Gross margin USD", TabularExportValueType.Number, 17),
                new("حمل‌ها", "Transport legs", TabularExportValueType.Integer, 11), new("فروش‌ها", "Sales", TabularExportValueType.Integer, 10),
                new("مصارف", "Expenses", TabularExportValueType.Integer, 10), new("اسناد دفتر", "Ledger entries", TabularExportValueType.Integer, 12),
                // سود محقق‌شده دقیقاً همان چیزی است که صفحه نشان می‌دهد و از ProfitAndLossService می‌آید.
                new("درآمد محقق‌شده USD", "Realised revenue USD", TabularExportValueType.Number, 18),
                new("بهای تمام‌شده USD", "Realised COGS USD", TabularExportValueType.Number, 18),
                new("سود محقق‌شده USD", "Realised gross profit USD", TabularExportValueType.Number, 19),
                new("اطمینان", "Confidence", Width: 14),
                new("فروش بدون بهای تمام‌شده", "Sales missing COGS", TabularExportValueType.Integer, 14)
            ],
            Rows = items.Select(item => new TabularExportRow(
            [
                TabularExportCell.Text(item.ShipmentCode), TabularExportCell.Text(item.ContractNumber), TabularExportCell.Text(item.ContractUnitText),
                TabularExportCell.Text(item.ProductName), TabularExportCell.Text(item.CustomerName), TabularExportCell.Text(item.SupplierName),
                TabularExportCell.Text(item.OriginName), TabularExportCell.Text(item.DestinationName), TabularExportCell.Number(item.QuantityMt),
                TabularExportCell.Number(item.TotalSalesUsd), TabularExportCell.Number(item.TotalPurchaseCostUsd),
                TabularExportCell.Number(item.TotalOperationalExpensesUsd), TabularExportCell.Number(item.TotalExpensesUsd),
                TabularExportCell.Number(item.GrossMarginUsd), TabularExportCell.Integer(item.RelatedTransportLegCount),
                TabularExportCell.Integer(item.RelatedSalesCount), TabularExportCell.Integer(item.RelatedExpensesCount),
                TabularExportCell.Integer(item.RelatedLedgerCount),
                TabularExportCell.Number(item.RealisedPnl.RevenueUsd),
                TabularExportCell.Number(item.RealisedPnl.CostOfGoodsSoldUsd),
                TabularExportCell.Number(item.RealisedPnl.GrossProfitUsd),
                TabularExportCell.Text(item.RealisedPnl.Confidence),
                TabularExportCell.Integer(item.RealisedPnl.UncostedSaleCount)
            ]))
        };
        return TabularExportSupport.File(this, format, document);
    }

    /// <summary>
    /// خروجی تب فعال یک محموله. همان <see cref="Details"/> را می‌سازد و همان داده‌ها و اعداد
    /// نمایش‌داده‌شده در صفحه را بدون query یا محاسبهٔ تجاری موازی صادر می‌کند.
    /// </summary>
    [EnableRateLimiting(RateLimitPolicies.CsvExport)]
    public async Task<IActionResult> DetailsExport(int id, string? format, string? tab)
    {
        // عمداً همان اکشن صفحه صدا زده می‌شود تا هیچ query یا محاسبه‌ای تکرار نشود؛
        // بنابراین صفحه و خروجی حتماً یک عدد را نشان می‌دهند.
        if (await Details(id) is not ViewResult view || view.Model is not ShipmentPnlDetailsViewModel model)
        {
            return NotFound();
        }

        var normalizedTab = NormalizeDetailsExportTab(tab);
        var isEn = UiText.IsEn(HttpContext);
        var exportService = HttpContext.RequestServices.GetService<ITabularExportService>();
        if (string.Equals(normalizedTab, "summary", StringComparison.Ordinal)
            && TabularExportSupport.ParseFormat(format) == TabularExportFormat.Pdf
            && exportService is not null)
        {
            var summary = BuildShipmentSummaryPdfModel(model, isEn, AfghanistanBusinessClock.SystemToday);
            using var output = new MemoryStream();
            await exportService.WriteShipmentSummaryPdfAsync(
                summary, isEn, output, HttpContext.RequestAborted);
            return File(
                output.ToArray(),
                "application/pdf",
                $"{summary.FileNameStem}_{AfghanistanBusinessClock.SystemToday:yyyy-MM-dd}.pdf");
        }

        if (!string.Equals(normalizedTab, "finance", StringComparison.Ordinal))
        {
            return TabularExportSupport.File(
                this,
                format,
                BuildDetailsTabExportDocument(model, normalizedTab, isEn));
        }

        var lines = new (string Fa, string En, decimal? Amount)[]
        {
            ("درآمد فروش محقق‌شده", "Realised sales revenue", model.RealisedPnl.RevenueUsd),
            ("بهای تمام‌شدهٔ فروش", "Cost of goods sold", -model.RealisedPnl.CostOfGoodsSoldUsd),
            ("سود ناخالص محقق‌شده", "Realised gross profit", model.RealisedPnl.GrossProfitUsd),
            ("برآورد عملیاتی سود ناخالص", "Operational gross margin estimate", model.RealizedGrossMarginUsd),
            ("اختلاف محقق‌شده و عملیاتی", "Realised vs operational variance", model.OperationalVsRealisedVarianceUsd),
            ("هزینهٔ خرید عملیاتی", "Operational purchase cost", -model.RealizedPurchaseCostUsd),
            ("مصارف عملیاتی", "Operational expenses", -model.RealizedOperationalExpensesUsd),
            ("نتیجهٔ خالص محموله", "Shipment net result", model.ShipmentNetResultUsd)
        };

        return TabularExportSupport.File(this, format, new TabularExportDocument
        {
            FileNameStem = "PTG_Shipment_Profit_" + (model.ShipmentCode ?? id.ToString()),
            TitleFa = "سود محموله " + model.ShipmentCode,
            TitleEn = "Shipment P&L " + model.ShipmentCode,
            KnownRowCount = lines.Length,
            Filters = TabularExportSupport.FilterSummary(
                ("تاریخ تولید (کابل) / Generated (Kabul)", AfghanistanBusinessClock.SystemToday.ToString("yyyy-MM-dd")),
                ("محموله / Shipment", model.ShipmentCode),
                ("قرارداد / Contract", model.ContractNumber),
                ("جنس / Product", model.ProductName),
                ("اطمینان / Confidence", model.RealisedPnl.Confidence),
                ("فروش بدون بهای تمام‌شده / Sales missing COGS", model.RealisedPnl.UncostedSaleCount.ToString())),
            Columns =
            [
                new("شرح", "Line", Width: 32),
                new("مبلغ USD", "Amount USD", TabularExportValueType.Number, 18),
                new("توضیح", "Note", Width: 30, Wrap: true)
            ],
            Rows = lines.Select(line => new TabularExportRow(
            [
                TabularExportCell.Text(isEn ? line.En : line.Fa),
                TabularExportCell.Number(line.Amount),
                TabularExportCell.Text(
                    line.Fa == "اختلاف محقق‌شده و عملیاتی" && model.HasOperationalVsRealisedVariance
                        ? model.OperationalVsRealisedVarianceReasonFa
                        : null)
            ]))
        });
    }

    [EnableRateLimiting(RateLimitPolicies.CsvExport)]
    public async Task<IActionResult> Csv()
    {
        var items = await BuildAllIndexItemsAsync();
        return CsvExportSupport.File(this, "shipment-pnl.csv",
            ["Shipment", "Contract", "ContractUnit", "Product", "Customer", "Supplier", "Origin", "Destination", "QuantityMt", "SalesUsd", "PurchaseCostUsd", "OperationalExpensesUsd", "TotalCostUsd", "GrossMarginUsd", "TransportLegCount", "SalesCount", "ExpensesCount", "LedgerCount"],
            items.Select(i => new[]
            {
                i.ShipmentCode, i.ContractNumber, i.ContractUnitText, i.ProductName, i.CustomerName, i.SupplierName, i.OriginName, i.DestinationName,
                CsvExportSupport.Decimal(i.QuantityMt), CsvExportSupport.Decimal(i.TotalSalesUsd), CsvExportSupport.Decimal(i.TotalPurchaseCostUsd),
                CsvExportSupport.Decimal(i.TotalOperationalExpensesUsd), CsvExportSupport.Decimal(i.TotalExpensesUsd),
                CsvExportSupport.Decimal(i.GrossMarginUsd), i.RelatedTransportLegCount.ToString(), i.RelatedSalesCount.ToString(), i.RelatedExpensesCount.ToString(), i.RelatedLedgerCount.ToString()
            }));
    }
}
