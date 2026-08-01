using System.Globalization;
using PTGOilSystem.Web.Models.ShipmentPnl;
using PTGOilSystem.Web.Services.Exports;

namespace PTGOilSystem.Web.Controllers;

// این مدل فقط داده‌های آمادهٔ صفحهٔ جزئیات را برای نمایش PDF قالب‌بندی می‌کند.
// هیچ query، ثبت مالی، فرمول موجودی یا محاسبهٔ تجاری تازه‌ای در این مسیر وجود ندارد.
public partial class ShipmentPnlController
{
    internal static ShipmentSummaryPdfModel BuildShipmentSummaryPdfModel(
        ShipmentPnlDetailsViewModel model,
        bool isEnglish,
        DateTime generatedAt)
    {
        string T(string fa, string en) => isEnglish ? en : fa;
        string Qty(decimal value) => PdfDesignSystem.FormatPdfNumber(value, isEnglish, 3);
        string Money(decimal value) => PdfDesignSystem.FormatPdfNumber(value, isEnglish, 2);
        string TextOrDash(string? value) => string.IsNullOrWhiteSpace(value) ? "-" : value.Trim();
        string DateOrDash(DateTime? value) => value.HasValue
            ? value.Value.ToString("yyyy/MM/dd", CultureInfo.InvariantCulture)
            : "-";

        const string UnitUsd = "USD";
        var unitMt = T("تن", "MT");
        var origin = TextOrDash(model.OriginName);
        var destination = TextOrDash(model.DestinationName);
        var originLabel = origin == "-" ? T("مبدأ ثبت‌شده", "recorded origin") : origin;
        var destinationLabel = destination == "-" ? T("مقصد ثبت‌شده", "recorded destination") : destination;

        string StageStatus(ShipmentSummaryPdfTone tone) => tone switch
        {
            ShipmentSummaryPdfTone.Positive => T("تکمیل", "Done"),
            ShipmentSummaryPdfTone.Warning => T("در جریان", "In progress"),
            ShipmentSummaryPdfTone.Negative => T("نیاز به توجه", "Needs attention"),
            _ => T("در انتظار", "Pending")
        };

        var voyageTone = model.ArrivalDate.HasValue || model.VesselUnloadedQuantityMt > 0m
            ? ShipmentSummaryPdfTone.Positive
            : model.DepartureDate.HasValue || model.OriginalShipmentQuantityMt > 0m
                ? ShipmentSummaryPdfTone.Warning
                : ShipmentSummaryPdfTone.Neutral;
        var unloadingTone = model.VesselUnloadingShortageQuantityMt > 0m
            ? ShipmentSummaryPdfTone.Negative
            : model.VesselUnloadedQuantityMt > 0m
                ? ShipmentSummaryPdfTone.Positive
                : ShipmentSummaryPdfTone.Neutral;
        var transportTone = model.InventoryTransportShortageQuantityMt > 0m
            ? ShipmentSummaryPdfTone.Negative
            : model.InTransitQuantityMt > 0m
                ? ShipmentSummaryPdfTone.Warning
                : model.DeliveredAtDestinationQuantityMt > 0m
                    ? ShipmentSummaryPdfTone.Positive
                    : ShipmentSummaryPdfTone.Neutral;
        var salesTone = model.SoldQuantityMt <= 0m
            ? ShipmentSummaryPdfTone.Neutral
            : model.RemainingUnsoldQuantityMt > 0m
                ? ShipmentSummaryPdfTone.Warning
                : ShipmentSummaryPdfTone.Positive;
        var collectionTone = model.TotalSalesUsd <= 0m
            ? ShipmentSummaryPdfTone.Neutral
            : model.OutstandingReceivableUsd > 0.01m
                ? ShipmentSummaryPdfTone.Warning
                : ShipmentSummaryPdfTone.Positive;
        var resultTone = model.PurchasePricingIncomplete
            ? ShipmentSummaryPdfTone.Warning
            : model.ShipmentNetResultUsd < 0m
                ? ShipmentSummaryPdfTone.Negative
                : ShipmentSummaryPdfTone.Positive;
        var overallTone = model.OriginalShipmentQuantityMt <= 0m
            ? ShipmentSummaryPdfTone.Neutral
            : model.PurchasePricingIncomplete
                || model.InTransitQuantityMt > 0m
                || model.RemainingUnsoldQuantityMt > 0m
                || model.OutstandingReceivableUsd > 0.01m
                    ? ShipmentSummaryPdfTone.Warning
                    : ShipmentSummaryPdfTone.Positive;

        var stages = new List<ShipmentSummaryPdfStage>
        {
            new(1, T("سفر کشتی", "Vessel voyage"), StageStatus(voyageTone), voyageTone,
            [
                new(T("بار کشتی", "Vessel cargo"), Qty(model.OriginalShipmentQuantityMt), unitMt),
                new(T("تاریخ حرکت", "Departure"), DateOrDash(model.DepartureDate)),
                new(T("تاریخ رسیدن", "Arrival"), DateOrDash(model.ArrivalDate))
            ]),
            new(2, T("تخلیه کشتی", "Vessel unloading"), StageStatus(unloadingTone), unloadingTone,
            [
                new(T($"تخلیه‌شده در {originLabel}", $"Unloaded at {originLabel}"), Qty(model.VesselUnloadedQuantityMt), unitMt),
                new(T("فروش مستقیم از داخل کشتی", "Direct sale from vessel"), Qty(model.DirectSaleQuantityMt), unitMt),
                new(T("کسری هنگام تخلیه", "Shortage during unloading"), Qty(model.VesselUnloadingShortageQuantityMt), unitMt,
                    model.VesselUnloadingShortageQuantityMt > 0m ? ShipmentSummaryPdfTone.Negative : ShipmentSummaryPdfTone.Neutral)
            ]),
            new(3, T($"انتقال {originLabel} به {destinationLabel}", $"Transfer from {originLabel} to {destinationLabel}"), StageStatus(transportTone), transportTone,
            [
                new(T($"ارسال‌شده از {originLabel}", $"Dispatched from {originLabel}"), Qty(model.InventoryTransportedOutQuantityMt), unitMt),
                new(T($"در راه به {destinationLabel}", $"In transit to {destinationLabel}"), Qty(model.InTransitQuantityMt), unitMt,
                    model.InTransitQuantityMt > 0m ? ShipmentSummaryPdfTone.Warning : ShipmentSummaryPdfTone.Neutral),
                new(T($"تحویل‌شده در {destinationLabel}", $"Delivered at {destinationLabel}"), Qty(model.DeliveredAtDestinationQuantityMt), unitMt)
            ]),
            new(4, T("فروش محموله", "Shipment sales"),
                model.RemainingUnsoldQuantityMt > 0m
                    ? T($"{Qty(model.RemainingUnsoldQuantityMt)} تن فروش‌نشده", $"{Qty(model.RemainingUnsoldQuantityMt)} MT unsold")
                    : T("فروش تکمیل", "Sales complete"),
                salesTone,
            [
                new(T("فروش مستقیم از داخل کشتی", "Direct sale from vessel"), Qty(model.DirectSaleQuantityMt), unitMt),
                new(T($"فروش پس از تخلیه در {originLabel}", $"Sale after unloading at {originLabel}"), Qty(model.StorageSaleQuantityMt), unitMt),
                new(T("کل مقدار فروخته‌شده", "Total quantity sold"), Qty(model.SoldQuantityMt), unitMt)
            ]),
            new(5, T("فروش و وصول", "Sales & collection"), StageStatus(collectionTone), collectionTone,
            [
                new(T("مبلغ فروش", "Sales value"), Money(model.TotalSalesUsd), UnitUsd),
                new(T("وصول‌شده", "Collected"), Money(model.CustomerReceiptsUsd), UnitUsd),
                new(T("طلب باقی‌مانده", "Receivable"), Money(model.OutstandingReceivableUsd), UnitUsd,
                    model.OutstandingReceivableUsd > 0.01m ? ShipmentSummaryPdfTone.Warning : ShipmentSummaryPdfTone.Positive)
            ]),
            new(6, T("نتیجه مالی", "Financial result"),
                model.PurchasePricingIncomplete
                    ? T("نرخ خرید ناقص", "Pricing incomplete")
                    : model.ShipmentNetResultUsd < 0m ? T("زیان", "Loss") : T("سود", "Profit"),
                resultTone,
            [
                new(T("قیمت خرید", "Purchase cost"), Money(model.TotalPurchaseCostUsd), UnitUsd),
                new(T("مصارف", "Expenses"), Money(model.TotalOperationalExpensesUsd), UnitUsd),
                new(T("نتیجه خالص", "Net result"), Money(model.ShipmentNetResultUsd), UnitUsd, resultTone)
            ])
        };

        return new ShipmentSummaryPdfModel
        {
            FileNameStem = "PTG_Shipment_" + SafeFilePart(model.ShipmentCode, model.Id),
            VesselName = string.IsNullOrWhiteSpace(model.VesselName)
                ? T("نام کشتی ثبت نشده", "Vessel not recorded")
                : model.VesselName.Trim(),
            ShipmentCode = TextOrDash(model.ShipmentCode),
            ProductName = TextOrDash(model.ProductName),
            ContractNumber = TextOrDash(model.ContractNumber),
            StatusText = StageStatus(overallTone),
            StatusTone = overallTone,
            CompanyName = string.IsNullOrWhiteSpace(model.CompanyName) ? string.Empty : model.CompanyName.Trim(),
            GeneratedAt = generatedAt,
            Origin = origin,
            Destination = destination,
            DepartureDateText = DateOrDash(model.DepartureDate),
            ArrivalDateText = DateOrDash(model.ArrivalDate),
            Stages = stages
        };
    }

    private static string SafeFilePart(string? value, int fallbackId)
    {
        var candidate = string.IsNullOrWhiteSpace(value)
            ? fallbackId.ToString(CultureInfo.InvariantCulture)
            : value.Trim();
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            candidate = candidate.Replace(invalid, '-');
        }
        return candidate;
    }
}
