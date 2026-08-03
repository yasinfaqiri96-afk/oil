using System.Globalization;
using PTGOilSystem.Web.Models.ShipmentPnl;
using PTGOilSystem.Web.Services.Exports;

namespace PTGOilSystem.Web.Controllers;

// این مدل فقط داده‌های آمادهٔ صفحهٔ جزئیات را برای نمایش PDF قالب‌بندی می‌کند.
// هیچ query، ثبت مالی، فرمول موجودی یا محاسبهٔ تجاری تازه‌ای در این مسیر وجود ندارد.
public partial class ShipmentPnlController
{
    /// <summary>
    /// سطرهای PDF از همان سندی خوانده می‌شوند که خروجی اکسلِ تب خلاصه را می‌سازد،
    /// بنابراین هر دو خروجی همیشه یک آمار و یک ترتیب دارند و نمی‌توانند از هم جدا شوند.
    /// </summary>
    internal static ShipmentSummaryPdfModel BuildShipmentSummaryPdfModel(
        ShipmentPnlDetailsViewModel model,
        bool isEnglish,
        DateTime generatedAt)
    {
        string T(string fa, string en) => isEnglish ? en : fa;
        string TextOrDash(string? value) => string.IsNullOrWhiteSpace(value) ? "-" : value.Trim();
        string DateOrDash(DateTime? value) => value.HasValue
            ? value.Value.ToString("yyyy/MM/dd", CultureInfo.InvariantCulture)
            : "-";

        // منبع واحد آمار: همان سند خروجی اکسل تب خلاصه.
        var summaryExport = BuildDetailsTabExportDocument(model, "summary", isEnglish);
        var exportRows = summaryExport.Rows.ToList();

        var rows = new List<ShipmentSummaryPdfRow>(exportRows.Count);
        for (var index = 0; index < exportRows.Count; index++)
        {
            var cells = exportRows[index].Cells;
            var isTotal = index == exportRows.Count - 1;
            var amount = CellDecimal(cells, 2);
            var tone = isTotal
                ? (amount < 0m ? ShipmentSummaryPdfTone.Negative : ShipmentSummaryPdfTone.Positive)
                : ShipmentSummaryPdfTone.Neutral;

            rows.Add(new ShipmentSummaryPdfRow(
                TextOrDash(CellText(cells, 0)),
                FormatOrDash(CellDecimal(cells, 1), isEnglish, 3),
                FormatOrDash(amount, isEnglish, 2),
                TextOrDash(CellText(cells, 3)),
                tone,
                isTotal));
        }

        return new ShipmentSummaryPdfModel
        {
            FileNameStem = "PTG_Shipment_" + SafeFilePart(model.ShipmentCode, model.Id),
            VesselName = string.IsNullOrWhiteSpace(model.VesselName)
                ? T("نام کشتی ثبت نشده", "Vessel not recorded")
                : model.VesselName.Trim(),
            ShipmentCode = TextOrDash(model.ShipmentCode),
            ProductName = TextOrDash(model.ProductName),
            ContractNumber = TextOrDash(model.ContractNumber),
            CompanyName = string.IsNullOrWhiteSpace(model.CompanyName) ? string.Empty : model.CompanyName.Trim(),
            GeneratedAt = generatedAt,
            Origin = TextOrDash(model.OriginName),
            Destination = TextOrDash(model.DestinationName),
            DepartureDateText = DateOrDash(model.DepartureDate),
            ArrivalDateText = DateOrDash(model.ArrivalDate),
            Rows = rows
        };
    }

    private static string? CellText(IReadOnlyList<TabularExportCell> cells, int index)
        => index < cells.Count ? cells[index].Value as string : null;

    private static decimal? CellDecimal(IReadOnlyList<TabularExportCell> cells, int index)
        => index < cells.Count ? cells[index].Value as decimal? : null;

    private static string FormatOrDash(decimal? value, bool isEnglish, int decimals)
        => value.HasValue
            ? PdfDesignSystem.FormatPdfNumber(value.Value, isEnglish, decimals)
            : "-";

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
