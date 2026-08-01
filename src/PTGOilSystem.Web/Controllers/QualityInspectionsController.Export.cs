using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Helpers;
using PTGOilSystem.Web.Infrastructure.RateLimiting;
using PTGOilSystem.Web.Models.Quality;
using PTGOilSystem.Web.Services.Exports;

namespace PTGOilSystem.Web.Controllers;

public partial class QualityInspectionsController
{
    /// <summary>
    /// خروجی فهرست آزمایش‌های کیفیت. دقیقاً همان <c>BuildFilteredQuery</c> صفحه را با همان
    /// فیلتر اجرا می‌کند، پس صفحه و خروجی همیشه یک مجموعه‌اند. صفحه‌بندی صفحه اعمال نمی‌شود
    /// چون خروجی کل نتیجهٔ همان فیلتر است؛ فیلترها در سربرگ خروجی چاپ می‌شوند.
    /// </summary>
    [HttpGet]
    [EnableRateLimiting(RateLimitPolicies.CsvExport)]
    public async Task<IActionResult> Export(
        string? format,
        [FromQuery] QualityInspectionFilterViewModel? filter = null,
        CancellationToken ct = default)
    {
        filter ??= new QualityInspectionFilterViewModel();
        var isEn = UiText.IsEn(HttpContext);

        var rows = await BuildFilteredQuery(filter)
            .OrderByDescending(q => q.SampleDate)
            .ThenByDescending(q => q.Id)
            .Select(q => new
            {
                q.Id,
                ProductName = q.Product != null ? q.Product.Name : "",
                ContractNumber = q.Contract != null ? q.Contract.ContractNumber : null,
                ShipmentReference = q.Shipment != null ? q.Shipment.ShipmentCode : null,
                CustomsReference = q.CustomsDeclaration != null ? q.CustomsDeclaration.DeclarationReference : null,
                q.LaboratoryName,
                q.ResultNumber,
                q.SampleDate,
                q.ResultDate,
                q.Status,
                q.DensityKgM3,
                q.SulphurPercent,
                q.RejectionReason,
                DocumentCount = q.Documents.Count
            })
            .ToListAsync(ct);

        return TabularExportSupport.File(this, format, new TabularExportDocument
        {
            FileNameStem = "PTG_Quality_Inspections",
            TitleFa = "آزمایش‌های کیفیت",
            TitleEn = "Quality Inspections",
            KnownRowCount = rows.Count,
            ForceLandscape = true,
            Filters = TabularExportSupport.FilterSummary(
                ("تاریخ تولید (کابل) / Generated (Kabul)", _clock.Today.ToString("yyyy-MM-dd")),
                ("از تاریخ / From", filter.FromDate?.ToString("yyyy-MM-dd")),
                ("تا تاریخ / To", filter.ToDate?.ToString("yyyy-MM-dd")),
                ("جنس / Product", filter.ProductId),
                ("قرارداد / Contract", filter.ContractId),
                ("محموله / Shipment", filter.ShipmentId),
                ("وضعیت / Status", filter.Status?.ToString())),
            Columns =
            [
                new("شمارهٔ نتیجه", "Result no.", Width: 16),
                new("آزمایشگاه", "Laboratory", Width: 20),
                new("جنس", "Product", Width: 16),
                new("قرارداد", "Contract", Width: 16),
                new("محموله", "Shipment", Width: 14),
                new("اظهارنامه", "Customs", Width: 16),
                new("تاریخ نمونه", "Sample date", TabularExportValueType.Date, 13),
                new("تاریخ نتیجه", "Result date", TabularExportValueType.Date, 13),
                new("وضعیت", "Status", Width: 14),
                new("چگالی kg/m³", "Density kg/m³", TabularExportValueType.Number, 14),
                new("گوگرد %", "Sulphur %", TabularExportValueType.Number, 12),
                new("اسناد", "Documents", TabularExportValueType.Integer, 10),
                new("دلیل رد", "Rejection reason", Width: 26, Wrap: true)
            ],
            Rows = rows.Select(r => new TabularExportRow(
            [
                TabularExportCell.Text(r.ResultNumber ?? ("QI-" + r.Id)),
                TabularExportCell.Text(r.LaboratoryName),
                TabularExportCell.Text(r.ProductName),
                TabularExportCell.Text(r.ContractNumber),
                TabularExportCell.Text(r.ShipmentReference),
                TabularExportCell.Text(r.CustomsReference),
                TabularExportCell.Date(r.SampleDate),
                TabularExportCell.Date(r.ResultDate),
                TabularExportCell.Text(isEn ? r.Status.ToString() : QualityInspectionStatusFa(r.Status)),
                TabularExportCell.Number(r.DensityKgM3),
                TabularExportCell.Number(r.SulphurPercent),
                TabularExportCell.Integer(r.DocumentCount),
                TabularExportCell.Text(r.RejectionReason)
            ]))
        });
    }

    private static string QualityInspectionStatusFa(Models.Entities.QualityInspectionStatus status) => status switch
    {
        Models.Entities.QualityInspectionStatus.Pending => "در انتظار نتیجه",
        Models.Entities.QualityInspectionStatus.Accepted => "قبول",
        Models.Entities.QualityInspectionStatus.Rejected => "رد",
        _ => status.ToString()
    };
}
