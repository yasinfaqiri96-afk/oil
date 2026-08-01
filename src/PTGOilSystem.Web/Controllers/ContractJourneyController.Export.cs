using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using PTGOilSystem.Web.Helpers;
using PTGOilSystem.Web.Infrastructure.RateLimiting;
using PTGOilSystem.Web.Models.ContractJourney;
using PTGOilSystem.Web.Services.Exports;
using PTGOilSystem.Web.Services.Time;

namespace PTGOilSystem.Web.Controllers;

public partial class ContractJourneyController
{
    [EnableRateLimiting(RateLimitPolicies.CsvExport)]
    public async Task<IActionResult> DetailsExport(
        int contractId,
        string? tab,
        bool lockContract,
        string? format)
    {
        var activeTab = ContractJourneyTabs.Details.Normalize(tab);
        if (await Details(contractId, activeTab, lockContract) is not ViewResult view
            || view.Model is not ContractJourneyDetailsViewModel model)
        {
            return NotFound();
        }

        // تب «خلاصه» در PDF سند رسمی گشت قرارداد است، نه جدول ساده؛ Excel و بقیهٔ تب‌ها
        // همان مسیر جدولی قبلی را دارند.
        var exportService = HttpContext.RequestServices.GetService<ITabularExportService>();
        if (activeTab == ContractJourneyTabs.Details.Summary
            && TabularExportSupport.ParseFormat(format) == TabularExportFormat.Pdf
            && exportService is not null)
        {
            var isEnglish = UiText.IsEn(HttpContext);
            var summary = BuildSummaryPdfModel(model, isEnglish, DateTime.UtcNow);
            using var output = new MemoryStream();
            await exportService.WriteContractJourneySummaryPdfAsync(
                summary, isEnglish, output, HttpContext.RequestAborted);
            return File(
                output.ToArray(),
                "application/pdf",
                $"{summary.FileNameStem}_{AfghanistanBusinessClock.SystemToday:yyyy-MM-dd}.pdf");
        }

        return TabularExportSupport.File(
            this,
            format,
            BuildDetailsExportDocuments(model, activeTab));
    }

    internal static IReadOnlyList<TabularExportDocument> BuildDetailsExportDocuments(
        ContractJourneyDetailsViewModel model,
        string? tab)
    {
        var activeTab = ContractJourneyTabs.Details.Normalize(tab);
        var filters = TabularExportSupport.FilterSummary(
            ("تاریخ تولید (کابل) / Generated (Kabul)", AfghanistanBusinessClock.SystemToday.ToString("yyyy-MM-dd")),
            ("قرارداد / Contract", model.ContractNumber),
            ("نوع قرارداد / Contract type", model.ContractTypeName),
            ("شرکت / Company", model.CompanyName),
            ("جنس / Product", model.ProductName),
            ("تب / Tab", activeTab));
        var stem = $"PTG_Contract_{model.ContractNumber}_{activeTab}";

        return activeTab switch
        {
            ContractJourneyTabs.Details.Loadings => [BuildLoadings(model, stem, filters)],
            ContractJourneyTabs.Details.Receipts => [BuildReceipts(model, stem, filters)],
            ContractJourneyTabs.Details.Inventory => [BuildInventory(model, stem, filters)],
            ContractJourneyTabs.Details.InventoryTransport => [BuildInventoryTransport(model, stem, filters)],
            ContractJourneyTabs.Details.Dispatch => [BuildDispatch(model, stem, filters)],
            ContractJourneyTabs.Details.Sales =>
            [
                BuildSales(model, stem, filters),
                BuildPreSales(model, stem + "_PreSales", filters)
            ],
            ContractJourneyTabs.Details.Costs =>
            [
                BuildExpenses(model, stem, filters),
                BuildLosses(model, stem + "_Losses", filters)
            ],
            ContractJourneyTabs.Details.Finance =>
            [
                BuildPayments(model, stem, filters),
                BuildSarrafSettlements(model, stem + "_Sarraf", filters)
            ],
            _ => [BuildSummary(model, stem, filters)]
        };
    }

    private static TabularExportDocument BuildSummary(
        ContractJourneyDetailsViewModel model,
        string stem,
        IReadOnlyList<TabularExportFilter> filters)
    {
        var partnerName = model.IsPurchaseContract ? model.SupplierName : model.CustomerName;
        var rows = new List<TabularExportRow>
        {
            Metric("شماره قرارداد / Contract no.", null, null, model.ContractNumber),
            Metric("طرف قرارداد / Counterparty", null, null, partnerName),
            Metric("مقدار قرارداد / Contract quantity", model.ContractQuantityMt, null, model.ContractUnitText),
            Metric("قیمت نهایی واحد / Final unit price", null, model.PricingFinalUnitPriceUsd, model.PriceDisplay),
            Metric("ارزش بارگیری‌ها / Loading value", null, model.LoadingsValueUsd, null),
            Metric("مقدار بارگیری / Loaded quantity", model.Kpis.LoadedQuantityMt, null, null),
            Metric("مقدار رسیده / Received quantity", model.Kpis.ReceivedQuantityMt, null, null),
            Metric("مقدار فروخته‌شده / Sold quantity", model.Kpis.SoldQuantityMt, null, null),
            Metric("وضعیت / Status", null, null, model.StatusName)
        };

        return Document(stem, "خلاصه قرارداد", "Contract summary", filters,
        [
            new("شرح", "Line", Width: 28),
            new("مقدار MT", "Quantity MT", TabularExportValueType.Number, 16),
            new("مبلغ USD", "Amount USD", TabularExportValueType.Number, 16),
            new("جزئیات", "Details", Width: 30, Wrap: true)
        ], rows);

        static TabularExportRow Metric(string label, decimal? quantity, decimal? amount, string? detail)
            => new(
            [
                TabularExportCell.Text(label),
                TabularExportCell.Number(quantity),
                TabularExportCell.Number(amount),
                TabularExportCell.Text(detail)
            ]);
    }

    private static TabularExportDocument BuildLoadings(
        ContractJourneyDetailsViewModel model,
        string stem,
        IReadOnlyList<TabularExportFilter> filters)
    {
        var rows = model.LoadingItems.Select(item => new TabularExportRow(
        [
            TabularExportCell.Date(item.LoadingDate),
            TabularExportCell.Integer(item.Id),
            TabularExportCell.Text(item.BillOfLadingNumber ?? item.RwbNo),
            TabularExportCell.Text(item.TransportTypeLabel),
            TabularExportCell.Text(item.VehicleNumber),
            TabularExportCell.Text(item.RouteDescription),
            TabularExportCell.Number(item.LoadedQuantityMt),
            TabularExportCell.Number(item.LoadingPriceUsd),
            TabularExportCell.Number(item.LoadingValueUsd),
            TabularExportCell.Number(item.AmountRubLive)
        ])).ToList();

        return Document(stem, "بارگیری‌های قرارداد", "Contract loadings", filters,
        [
            new("تاریخ", "Date", TabularExportValueType.Date, 14),
            new("شناسه", "ID", TabularExportValueType.Integer, 10),
            new("سند", "Document", Width: 17),
            new("نوع حمل", "Transport", Width: 16),
            new("نمبر وسیله", "Vehicle no.", Width: 16),
            new("مسیر", "Route", Width: 22),
            new("مقدار MT", "Quantity MT", TabularExportValueType.Number, 15),
            new("قیمت واحد USD", "Unit price USD", TabularExportValueType.Number, 16),
            new("ارزش USD", "Value USD", TabularExportValueType.Number, 16),
            new("ارزش RUB", "Value RUB", TabularExportValueType.Number, 16)
        ], rows, forceLandscape: true);
    }

    private static TabularExportDocument BuildReceipts(
        ContractJourneyDetailsViewModel model,
        string stem,
        IReadOnlyList<TabularExportFilter> filters)
    {
        // نمبر وسیله از همان بارگیریِ این رسید می‌آید تا معلوم باشد بار با کدام وسیله رسیده.
        var vehicleByLoadingId = model.LoadingItems
            .GroupBy(loading => loading.Id)
            .ToDictionary(group => group.Key, group => group.First().VehicleNumber);

        var rows = model.ReceiptItems.Select(item => new TabularExportRow(
        [
            TabularExportCell.Date(item.ReceiptDate),
            TabularExportCell.Integer(item.Id),
            TabularExportCell.Integer(item.LoadingRegisterId),
            TabularExportCell.Text(vehicleByLoadingId.GetValueOrDefault(item.LoadingRegisterId)),
            TabularExportCell.Text(item.TerminalName),
            TabularExportCell.Text(item.StorageTankCode),
            TabularExportCell.Number(item.ReceivedQuantityMt),
            TabularExportCell.Number(item.DifferenceQuantityMt),
            TabularExportCell.Text(item.ReferenceDocument)
        ])).ToList();

        return Document(stem, "رسیدهای قرارداد", "Contract receipts", filters,
        [
            new("تاریخ", "Date", TabularExportValueType.Date, 14),
            new("شناسه رسید", "Receipt ID", TabularExportValueType.Integer, 12),
            new("شناسه بارگیری", "Loading ID", TabularExportValueType.Integer, 12),
            new("نمبر وسیله", "Vehicle no.", Width: 16),
            new("ترمینال", "Terminal", Width: 18),
            new("مخزن", "Tank", Width: 14),
            new("مقدار رسیده MT", "Received MT", TabularExportValueType.Number, 16),
            new("تفاوت MT", "Difference MT", TabularExportValueType.Number, 15),
            new("مرجع", "Reference", Width: 18)
        ], rows, forceLandscape: true);
    }

    private static TabularExportDocument BuildInventory(
        ContractJourneyDetailsViewModel model,
        string stem,
        IReadOnlyList<TabularExportFilter> filters)
    {
        var rows = model.InventoryMovementItems.Select(item => new TabularExportRow(
        [
            TabularExportCell.Date(item.MovementDate),
            TabularExportCell.Integer(item.Id),
            TabularExportCell.Text(item.DirectionName),
            TabularExportCell.Text(item.TerminalName),
            TabularExportCell.Text(item.StorageTankCode),
            TabularExportCell.Number(item.SignedQuantityMt),
            TabularExportCell.Number(item.RunningBalanceMt),
            TabularExportCell.Text(item.ReferenceDocument ?? item.SourceLabel)
        ])).ToList();

        return Document(stem, "حرکات موجودی قرارداد", "Contract inventory movements", filters,
        [
            new("تاریخ", "Date", TabularExportValueType.Date, 14),
            new("شناسه", "ID", TabularExportValueType.Integer, 10),
            new("جهت", "Direction", Width: 14),
            new("ترمینال", "Terminal", Width: 18),
            new("مخزن", "Tank", Width: 14),
            new("مقدار MT", "Quantity MT", TabularExportValueType.Number, 15),
            new("مانده MT", "Balance MT", TabularExportValueType.Number, 15),
            new("مرجع", "Reference", Width: 20)
        ], rows, forceLandscape: true);
    }

    private static TabularExportDocument BuildInventoryTransport(
        ContractJourneyDetailsViewModel model,
        string stem,
        IReadOnlyList<TabularExportFilter> filters)
    {
        var rows = model.InventoryTransportLegItems.Select(item => new TabularExportRow(
        [
            TabularExportCell.Date(item.LoadedDate),
            TabularExportCell.Integer(item.Id),
            TabularExportCell.Text(item.VehicleNumber ?? item.WagonNumber),
            TabularExportCell.Text(item.RwbNo ?? item.TransportTypeName),
            TabularExportCell.Text(item.SourceTerminalName),
            TabularExportCell.Text(item.DestinationReceipt?.DestinationTerminalName ?? item.DestinationTerminalName),
            TabularExportCell.Number(item.QuantityMt),
            TabularExportCell.Number(TransportInTransitQuantity(item)),
            TabularExportCell.Number(item.ReceivedQuantityMt),
            TabularExportCell.Number(item.ShortageQuantityMt),
            TabularExportCell.Text(item.StatusName)
        ])).ToList();

        return Document(stem, "جریان حمل قرارداد", "Contract cargo flow", filters,
        [
            new("تاریخ", "Date", TabularExportValueType.Date, 14),
            new("شناسه", "ID", TabularExportValueType.Integer, 10),
            new("نمبر وسیله", "Vehicle no.", Width: 16),
            new("سند", "Document", Width: 16),
            new("مبدأ", "Source", Width: 18),
            new("مقصد", "Destination", Width: 18),
            new("حمل MT", "Transported MT", TabularExportValueType.Number, 15),
            new("در مسیر MT", "In transit MT", TabularExportValueType.Number, 15),
            new("رسیده MT", "Received MT", TabularExportValueType.Number, 15),
            new("کسری MT", "Shortage MT", TabularExportValueType.Number, 15),
            new("وضعیت", "Status", Width: 14)
        ], rows, forceLandscape: true);
    }

    private static TabularExportDocument BuildDispatch(
        ContractJourneyDetailsViewModel model,
        string stem,
        IReadOnlyList<TabularExportFilter> filters)
    {
        var rows = model.DispatchItems.Select(item => new TabularExportRow(
        [
            TabularExportCell.Date(item.DispatchDate),
            TabularExportCell.Integer(item.Id),
            TabularExportCell.Text(item.TruckPlateNumber),
            TabularExportCell.Text(item.DriverName),
            TabularExportCell.Text(item.SourceTerminalName),
            TabularExportCell.Text(item.DestinationName),
            TabularExportCell.Number(item.LoadedQuantityMt),
            TabularExportCell.Number(item.DischargedQuantityMt),
            TabularExportCell.Number(item.FreightCostUsd),
            TabularExportCell.Text(item.StatusName)
        ])).ToList();

        return Document(stem, "نقل و انتقالات قرارداد", "Contract transfers", filters,
        [
            new("تاریخ", "Date", TabularExportValueType.Date, 14),
            new("شناسه", "ID", TabularExportValueType.Integer, 10),
            new("نمبر پلیت", "Plate no.", Width: 14),
            new("راننده", "Driver", Width: 16),
            new("مبدأ", "Source", Width: 17),
            new("مقصد", "Destination", Width: 17),
            new("بارگیری MT", "Loaded MT", TabularExportValueType.Number, 15),
            new("تخلیه MT", "Discharged MT", TabularExportValueType.Number, 15),
            new("کرایه USD", "Freight USD", TabularExportValueType.Number, 15),
            new("وضعیت", "Status", Width: 14)
        ], rows, forceLandscape: true);
    }

    private static TabularExportDocument BuildSales(
        ContractJourneyDetailsViewModel model,
        string stem,
        IReadOnlyList<TabularExportFilter> filters)
    {
        var rows = model.SalesItems.Select(item => new TabularExportRow(
        [
            TabularExportCell.Date(item.SaleDate),
            TabularExportCell.Text(item.InvoiceNumber),
            TabularExportCell.Text(SaleVehicleNumber(model, item)),
            TabularExportCell.Text(item.CustomerName),
            TabularExportCell.Number(item.QuantityMt),
            TabularExportCell.Number(item.UnitPriceUsd),
            TabularExportCell.Number(item.AmountUsd),
            TabularExportCell.Number(item.TotalCostUsd),
            TabularExportCell.Number(item.GrossProfitUsd),
            TabularExportCell.Text(item.TraceKind)
        ])).ToList();

        return Document(stem, "فروشات قرارداد", "Contract sales", filters,
        [
            new("تاریخ", "Date", TabularExportValueType.Date, 14),
            new("فاکتور", "Invoice", Width: 16),
            new("نمبر وسیله", "Vehicle no.", Width: 16),
            new("مشتری", "Customer", Width: 18),
            new("مقدار MT", "Quantity MT", TabularExportValueType.Number, 15),
            new("قیمت واحد USD", "Unit price USD", TabularExportValueType.Number, 16),
            new("مبلغ USD", "Amount USD", TabularExportValueType.Number, 16),
            new("هزینه USD", "Cost USD", TabularExportValueType.Number, 16),
            new("سود USD", "Profit USD", TabularExportValueType.Number, 16),
            new("ردیابی", "Trace", Width: 16)
        ], rows, forceLandscape: true);
    }

    private static TabularExportDocument BuildPreSales(
        ContractJourneyDetailsViewModel model,
        string stem,
        IReadOnlyList<TabularExportFilter> filters)
        => Document(stem, "پیش‌فروش‌های قرارداد", "Contract pre-sales", filters,
        [
            new("تاریخ", "Date", TabularExportValueType.Date, 14),
            new("فاکتور", "Invoice", Width: 16),
            new("مشتری", "Customer", Width: 20),
            new("مقدار MT", "Quantity MT", TabularExportValueType.Number, 15),
            new("قرارداد فروش", "Sales contract", Width: 18),
            new("ردیابی", "Trace", Width: 16)
        ], model.PreSaleItems.Select(item => new TabularExportRow(
        [
            TabularExportCell.Date(item.SaleDate),
            TabularExportCell.Text(item.InvoiceNumber),
            TabularExportCell.Text(item.CustomerName),
            TabularExportCell.Number(item.QuantityMt),
            TabularExportCell.Text(item.SalesContractDisplay),
            TabularExportCell.Text(item.TraceKind)
        ])).ToList());

    private static TabularExportDocument BuildExpenses(
        ContractJourneyDetailsViewModel model,
        string stem,
        IReadOnlyList<TabularExportFilter> filters)
        => Document(stem, "مصارف قرارداد", "Contract expenses", filters,
        [
            new("تاریخ", "Date", TabularExportValueType.Date, 14),
            new("نوع", "Type", Width: 20),
            new("نمبر وسیله", "Vehicle no.", Width: 16),
            new("مبلغ USD", "Amount USD", TabularExportValueType.Number, 16),
            new("مرجع", "Reference", Width: 20),
            new("شرح", "Description", Width: 30, Wrap: true)
        ], model.ExpenseItems.Select(item => new TabularExportRow(
        [
            TabularExportCell.Date(item.ExpenseDate),
            TabularExportCell.Text(item.ExpenseTypeName),
            TabularExportCell.Text(item.VehicleNumber),
            TabularExportCell.Number(item.AmountUsd),
            TabularExportCell.Text(item.ShipmentCode ?? item.DispatchLabel ?? item.TransportLegLabel),
            TabularExportCell.Text(item.Description)
        ])).ToList());

    private static TabularExportDocument BuildLosses(
        ContractJourneyDetailsViewModel model,
        string stem,
        IReadOnlyList<TabularExportFilter> filters)
        => Document(stem, "کسری‌های قرارداد", "Contract losses", filters,
        [
            new("تاریخ", "Date", TabularExportValueType.Date, 14),
            new("مرحله", "Stage", Width: 18),
            new("نمبر وسیله", "Vehicle no.", Width: 16),
            new("مورد انتظار MT", "Expected MT", TabularExportValueType.Number, 16),
            new("واقعی MT", "Actual MT", TabularExportValueType.Number, 16),
            new("تفاوت MT", "Difference MT", TabularExportValueType.Number, 16),
            new("کسری قابل مطالبه MT", "Chargeable MT", TabularExportValueType.Number, 18),
            new("ردیابی", "Trace", Width: 16)
        ], model.LossItems.Select(item => new TabularExportRow(
        [
            TabularExportCell.Date(item.EventDate),
            TabularExportCell.Text(item.StageName),
            TabularExportCell.Text(item.VehicleNumber),
            TabularExportCell.Number(item.ExpectedQuantityMt),
            TabularExportCell.Number(item.ActualQuantityMt),
            TabularExportCell.Number(item.DifferenceQuantityMt),
            TabularExportCell.Number(item.ChargeableLossMt),
            TabularExportCell.Text(item.TraceKind)
        ])).ToList(), forceLandscape: true);

    private static TabularExportDocument BuildPayments(
        ContractJourneyDetailsViewModel model,
        string stem,
        IReadOnlyList<TabularExportFilter> filters)
        => Document(stem, "پرداخت‌های قرارداد", "Contract payments", filters,
        [
            new("تاریخ", "Date", TabularExportValueType.Date, 14),
            new("شناسه", "ID", TabularExportValueType.Integer, 10),
            new("جهت", "Direction", Width: 14),
            new("نوع", "Kind", Width: 18),
            new("حساب نقدی", "Cash account", Width: 18),
            new("مبلغ", "Amount", TabularExportValueType.Number, 16),
            new("ارز", "Currency", Width: 10),
            new("مبلغ USD", "Amount USD", TabularExportValueType.Number, 16),
            new("مرجع", "Reference", Width: 18)
        ], model.PaymentItems.Select(item => new TabularExportRow(
        [
            TabularExportCell.Date(item.PaymentDate),
            TabularExportCell.Integer(item.PaymentTransactionId),
            TabularExportCell.Text(item.DirectionName),
            TabularExportCell.Text(item.PaymentKindName),
            TabularExportCell.Text(item.CashAccountName),
            TabularExportCell.Number(item.Amount),
            TabularExportCell.Text(item.Currency),
            TabularExportCell.Number(item.AmountUsd),
            TabularExportCell.Text(item.Reference)
        ])).ToList(), forceLandscape: true);

    private static TabularExportDocument BuildSarrafSettlements(
        ContractJourneyDetailsViewModel model,
        string stem,
        IReadOnlyList<TabularExportFilter> filters)
        => Document(stem, "تسویه‌های صراف قرارداد", "Contract sarraf settlements", filters,
        [
            new("تاریخ", "Date", TabularExportValueType.Date, 14),
            new("صراف", "Sarraf", Width: 18),
            new("تأمین‌کننده", "Supplier", Width: 18),
            new("مرجع", "Reference", Width: 16),
            new("درخواست USD", "Requested USD", TabularExportValueType.Number, 16),
            new("قبول تأمین‌کننده USD", "Supplier accepted USD", TabularExportValueType.Number, 19),
            new("کسر تأمین‌کننده USD", "Supplier reduction USD", TabularExportValueType.Number, 19),
            new("تفاوت USD", "Difference USD", TabularExportValueType.Number, 16),
            new("وضعیت", "Status", Width: 14)
        ], model.SarrafSettlementItems.Select(item => new TabularExportRow(
        [
            TabularExportCell.Date(item.SettlementDate),
            TabularExportCell.Text(item.SarrafName),
            TabularExportCell.Text(item.SupplierName),
            TabularExportCell.Text(item.ReferenceNumber),
            TabularExportCell.Number(item.RequestedAmountUsd),
            TabularExportCell.Number(item.SupplierAcceptedAmountUsd),
            TabularExportCell.Number(item.SupplierReductionAmountUsd),
            TabularExportCell.Number(item.DifferenceAmountUsd),
            TabularExportCell.Text(item.Status.ToString())
        ])).ToList(), forceLandscape: true);

    private static TabularExportDocument Document(
        string stem,
        string titleFa,
        string titleEn,
        IReadOnlyList<TabularExportFilter> filters,
        IReadOnlyList<TabularExportColumn> columns,
        IReadOnlyList<TabularExportRow> rows,
        bool forceLandscape = false)
        => new()
        {
            FileNameStem = stem,
            TitleFa = titleFa,
            TitleEn = titleEn,
            Filters = filters,
            Columns = columns,
            Rows = rows,
            KnownRowCount = rows.Count,
            ForceLandscape = forceLandscape
        };

    // نمبر وسیلهٔ فروش از همان زنجیرهٔ ردیابی می‌آید: موتر ارسال، سپس سفر حمل داخلی.
    private static string? SaleVehicleNumber(
        ContractJourneyDetailsViewModel model,
        ContractJourneySaleItemViewModel item)
    {
        if (item.TruckDispatchId is int dispatchId)
        {
            var dispatch = model.DispatchItems.FirstOrDefault(d => d.Id == dispatchId);
            if (!string.IsNullOrWhiteSpace(dispatch?.TruckPlateNumber))
            {
                return dispatch!.TruckPlateNumber;
            }
        }

        if (item.InventoryTransportLegId is int legId)
        {
            var leg = model.InventoryTransportLegItems.FirstOrDefault(l => l.Id == legId);
            var legVehicle = leg?.WagonNumber ?? leg?.RwbNo;
            if (!string.IsNullOrWhiteSpace(legVehicle))
            {
                return legVehicle;
            }
        }

        return item.InventoryTransportReference;
    }

    private static decimal TransportInTransitQuantity(ContractJourneyTransportLegItemViewModel item)
        => string.Equals(item.StatusName, "Received", StringComparison.OrdinalIgnoreCase)
            || string.Equals(item.StatusName, "Cancelled", StringComparison.OrdinalIgnoreCase)
                ? 0m
                : Math.Max(item.QuantityMt - item.ReceivedQuantityMt - item.ShortageQuantityMt, 0m);
}
