using PTGOilSystem.Web.Models.ShipmentPnl;
using PTGOilSystem.Web.Services.Exports;

namespace PTGOilSystem.Web.Controllers;

public partial class ShipmentPnlController
{
    internal static TabularExportDocument BuildDetailsTabExportDocument(
        ShipmentPnlDetailsViewModel model,
        string tab,
        bool isEnglish)
    {
        // سربرگ خروجی فقط نام محموله را نشان می‌دهد؛ خط فیلترها (تاریخ تولید، قرارداد،
        // جنس، بخش) حذف شده تا سربرگ شلوغ نباشد. نام تب در نام فایل باقی می‌ماند.
        var shipmentName = string.IsNullOrWhiteSpace(model.ShipmentCode)
            ? (Fa: "محموله", En: "Shipment")
            : (Fa: $"محموله {model.ShipmentCode.Trim()}", En: $"Shipment {model.ShipmentCode.Trim()}");
        IReadOnlyList<TabularExportFilter> filters = [];
        var fileStem = $"PTG_Shipment_{tab}_{model.ShipmentCode}";

        return tab switch
        {
            "flow" => BuildReceiptExport(model, fileStem, shipmentName, filters),
            "compliance" => BuildExpenseExport(model, fileStem, shipmentName, filters, isEnglish),
            "balance" => BuildShortageExport(model, fileStem, shipmentName, filters),
            "sales" => BuildSalesExport(model, fileStem, shipmentName, filters, isEnglish),
            _ => BuildSummaryExport(model, fileStem, shipmentName, filters, isEnglish)
        };
    }

    internal static string NormalizeDetailsExportTab(string? tab)
        => tab?.Trim().ToLowerInvariant() switch
        {
            "flow" or "compliance" or "balance" or "sales" or "finance" => tab.Trim().ToLowerInvariant(),
            _ => "summary"
        };

    private static TabularExportDocument BuildSummaryExport(
        ShipmentPnlDetailsViewModel model,
        string fileStem,
        (string Fa, string En) title,
        IReadOnlyList<TabularExportFilter> filters,
        bool isEnglish)
    {
        var rows = model.ContractLines
            .Where(line => line.AllocatedQuantityMt > 0m)
            .OrderByDescending(line => line.AllocatedQuantityMt)
            .Select(line => new TabularExportRow(
            [
                TabularExportCell.Text((isEnglish ? "Cargo source: " : "منبع بار: ") + line.ContractNumber),
                TabularExportCell.Number(line.AllocatedQuantityMt),
                TabularExportCell.Number(line.TotalValueUsd),
                TabularExportCell.Text(line.SupplierName)
            ]))
            .ToList();
        rows.AddRange(
        [
            SummaryRow(isEnglish ? "Original cargo" : "کل بار", model.OriginalShipmentQuantityMt, null, model.VesselName),
            SummaryRow(isEnglish ? "Discharged" : "تخلیه‌شده", model.VesselUnloadedQuantityMt, null, null),
            SummaryRow(isEnglish ? "Recorded losses" : "ضایعات ثبت‌شده", model.RecordedLossQuantityMt, null, null),
            SummaryRow(isEnglish ? "Purchase cost" : "هزینه خرید", null, model.TotalPurchaseCostUsd, null),
            SummaryRow(isEnglish ? "Operational expenses" : "مصارف عملیاتی", null, model.TotalOperationalExpensesUsd, null),
            SummaryRow(isEnglish ? "Sales revenue" : "درآمد فروش", model.SoldQuantityMt, model.TotalSalesUsd, null),
            SummaryRow(isEnglish ? "Shipment net result" : "نتیجه خالص محموله", null, model.ShipmentNetResultUsd, null)
        ]);

        return Document(fileStem, title, filters,
        [
            new("شرح", "Line", Width: 24),
            new("مقدار MT", "Quantity MT", TabularExportValueType.Number, 15),
            new("مبلغ USD", "Amount USD", TabularExportValueType.Number, 16),
            new("جزئیات", "Details", Width: 26, Wrap: true)
        ], rows);

        static TabularExportRow SummaryRow(string label, decimal? quantity, decimal? amount, string? detail)
            => new(
            [
                TabularExportCell.Text(label),
                TabularExportCell.Number(quantity),
                TabularExportCell.Number(amount),
                TabularExportCell.Text(detail)
            ]);
    }

    private static TabularExportDocument BuildReceiptExport(
        ShipmentPnlDetailsViewModel model,
        string fileStem,
        (string Fa, string En) title,
        IReadOnlyList<TabularExportFilter> filters)
    {
        var rows = model.RegisteredVesselReceipts.Select(receipt => new TabularExportRow(
        [
            TabularExportCell.Date(receipt.ReceiptDate),
            TabularExportCell.Integer(receipt.Id),
            TabularExportCell.Text(receipt.ContractNumber),
            TabularExportCell.Text(receipt.DestinationTerminalName),
            TabularExportCell.Text(receipt.DestinationTankName),
            TabularExportCell.Number(receipt.ReceivedQuantityMt)
        ])).ToList();

        return Document(fileStem, title, filters,
        [
            new("تاریخ", "Date", TabularExportValueType.Date, 14),
            new("شماره تخلیه", "Discharge no.", TabularExportValueType.Integer, 12),
            new("قرارداد", "Contract", Width: 16),
            new("مقصد", "Destination", Width: 20),
            new("مخزن تخلیه", "Unload tank", Width: 18),
            new("مقدار تخلیه MT", "Discharged MT", TabularExportValueType.Number, 16)
        ], rows, new TabularExportRow(
        [
            TabularExportCell.Text("جمع / Total"),
            TabularExportCell.Integer(null),
            TabularExportCell.Text(null),
            TabularExportCell.Text(null),
            TabularExportCell.Text(null),
            TabularExportCell.Number(model.RegisteredVesselReceiptQuantityMt)
        ]));
    }

    private static TabularExportDocument BuildExpenseExport(
        ShipmentPnlDetailsViewModel model,
        string fileStem,
        (string Fa, string En) title,
        IReadOnlyList<TabularExportFilter> filters,
        bool isEnglish)
    {
        var rows = model.ExpenseDisplayRows.Select(expense => new TabularExportRow(
        [
            TabularExportCell.Text(ExpenseCategoryName(expense.Category, isEnglish)),
            TabularExportCell.Date(expense.ExpenseDate),
            TabularExportCell.Text(expense.ExpenseTypeName),
            TabularExportCell.Text(expense.VehicleNumber),
            TabularExportCell.Text(expense.Description),
            TabularExportCell.Number(expense.AmountUsd)
        ])).ToList();

        return Document(fileStem, title, filters,
        [
            new("دسته", "Category", Width: 16),
            new("تاریخ", "Date", TabularExportValueType.Date, 14),
            new("نوع", "Type", Width: 18),
            new("نمبر وسیله", "Vehicle no.", Width: 16),
            new("شرح", "Description", Width: 30, Wrap: true),
            new("مبلغ USD", "Amount USD", TabularExportValueType.Number, 16)
        ], rows, new TabularExportRow(
        [
            TabularExportCell.Text("جمع / Total"),
            TabularExportCell.Date(null),
            TabularExportCell.Text(null),
            TabularExportCell.Text(null),
            TabularExportCell.Text(null),
            TabularExportCell.Number(model.ExpenseDisplayRows.Sum(row => row.AmountUsd))
        ]));
    }

    private static TabularExportDocument BuildShortageExport(
        ShipmentPnlDetailsViewModel model,
        string fileStem,
        (string Fa, string En) title,
        IReadOnlyList<TabularExportFilter> filters)
    {
        var rows = model.LossDisplayRows.Select(loss => new TabularExportRow(
        [
            TabularExportCell.Date(loss.EventDate),
            TabularExportCell.Text(loss.VehicleNumber),
            TabularExportCell.Number(loss.QuantityMt),
            TabularExportCell.Number(loss.EstimatedValueUsd),
            TabularExportCell.Text(loss.ResponsibilityTypeName),
            TabularExportCell.Text(loss.Description)
        ])).ToList();

        return Document(fileStem, title, filters,
        [
            new("تاریخ", "Date", TabularExportValueType.Date, 14),
            new("نمبر وسیله", "Vehicle no.", Width: 16),
            new("مقدار کسری MT", "Shortage MT", TabularExportValueType.Number, 17),
            new("ارزش تخمینی USD", "Estimated value USD", TabularExportValueType.Number, 18),
            new("نوع مسئولیت", "Responsibility", Width: 22),
            new("شرح", "Description", Width: 28, Wrap: true)
        ], rows, new TabularExportRow(
        [
            TabularExportCell.Text("جمع / Total"),
            TabularExportCell.Text(null),
            TabularExportCell.Number(model.LossDisplayRows.Sum(row => row.QuantityMt)),
            TabularExportCell.Number(model.LossDisplayRows.Sum(row => row.EstimatedValueUsd)),
            TabularExportCell.Text(null),
            TabularExportCell.Text(null)
        ]));
    }

    private static TabularExportDocument BuildSalesExport(
        ShipmentPnlDetailsViewModel model,
        string fileStem,
        (string Fa, string En) title,
        IReadOnlyList<TabularExportFilter> filters,
        bool isEnglish)
    {
        var rows = model.SaleDisplayRows.Select(sale => new TabularExportRow(
        [
            TabularExportCell.Date(sale.SaleDate),
            TabularExportCell.Text(sale.InvoiceNumber),
            TabularExportCell.Text(sale.VehicleNumber),
            TabularExportCell.Text(sale.CustomerName),
            TabularExportCell.Text(sale.IsDirectShipmentSale
                ? (isEnglish ? "Direct shipment sale" : "فروش مستقیم از محموله")
                : (isEnglish ? "Sale after storage" : "فروش پس از ورود به مخزن")),
            TabularExportCell.Number(sale.QuantityMt),
            TabularExportCell.Number(sale.UnitPriceUsd),
            TabularExportCell.Number(sale.TotalUsd)
        ])).ToList();

        return Document(fileStem, title, filters,
        [
            new("تاریخ", "Date", TabularExportValueType.Date, 14),
            new("شماره فاکتور", "Invoice no.", Width: 16),
            new("نمبر وسیله", "Vehicle no.", Width: 16),
            new("مشتری", "Customer", Width: 20),
            new("منبع فروش", "Sale source", Width: 20),
            new("مقدار MT", "Quantity MT", TabularExportValueType.Number, 15),
            new("قیمت واحد USD", "Unit price USD", TabularExportValueType.Number, 16),
            new("مبلغ USD", "Amount USD", TabularExportValueType.Number, 16)
        ], rows, new TabularExportRow(
        [
            TabularExportCell.Text("جمع / Total"),
            TabularExportCell.Text(null),
            TabularExportCell.Text(null),
            TabularExportCell.Text(null),
            TabularExportCell.Text(null),
            TabularExportCell.Number(model.SoldQuantityMt),
            TabularExportCell.Number(null),
            TabularExportCell.Number(model.TotalSalesUsd)
        ]), forceLandscape: true);
    }

    private static TabularExportDocument Document(
        string fileStem,
        (string Fa, string En) title,
        IReadOnlyList<TabularExportFilter> filters,
        IReadOnlyList<TabularExportColumn> columns,
        IReadOnlyList<TabularExportRow> rows,
        TabularExportRow? totals = null,
        bool forceLandscape = false)
        => new()
        {
            FileNameStem = fileStem,
            // عنوان دقیقاً همان نام محموله است؛ سربرگ چیز دیگری نمی‌نویسد.
            TitleFa = title.Fa,
            TitleEn = title.En,
            Filters = filters,
            Columns = columns,
            Rows = rows,
            Totals = totals,
            KnownRowCount = rows.Count,
            ForceLandscape = forceLandscape
        };

    private static string ExpenseCategoryName(ShipmentExpenseCategory category, bool isEnglish)
        => category switch
        {
            ShipmentExpenseCategory.Freight => isEnglish ? "Freight" : "کرایه و حمل",
            ShipmentExpenseCategory.Customs => isEnglish ? "Customs" : "گمرک",
            ShipmentExpenseCategory.Terminal => isEnglish ? "Terminal" : "ترمینال و گدام",
            ShipmentExpenseCategory.Documents => isEnglish ? "Documents" : "اسناد و مجوزها",
            _ => isEnglish ? "Other" : "سایر مصارف"
        };
}
